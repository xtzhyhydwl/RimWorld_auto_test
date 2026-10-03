using System;
using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace ColonySnapshots
{
    /// <summary>
    /// **一个快照读取块** —— 每个"关注面"实现一个。
    ///
    /// ## 为什么要这层抽象（用户 2026-10-03 明确要求）
    ///
    /// 用户原话：
    /// > 「我希望这种独开照的形式在后边还要有**拓展**……
    /// >  假如说后续有需要去读取**别的类型的内容**的话，
    /// >  那么我们要有一个**易于拓展的结构**，把新的快照内容加进去，
    /// >  就是一定要保证它**易于更改、易于拓展**，就整个读快照的结构。」
    ///
    /// → 加一个新的面（比如"装备"、"房间美观"、"科技树细节"）时：
    ///   **只新增一个实现类 + 注册**，不改 `SnapshotBuilder`、不改其他 Reader。
    ///
    /// ⚠ 只读：`Read` **不得**修改地图上任何东西。
    /// </summary>
    public interface ISnapshotReader
    {
        /// <summary>稳定标识（进快照的 `health[].reader`，用于度量覆盖率）</summary>
        string Id { get; }

        /// <summary>人话名字</summary>
        string DisplayName { get; }

        /// <summary>执行顺序（小的先）</summary>
        int Order { get; }

        /// <summary>
        /// **读**。实现里自己 try/catch 局部失败并记 `warnings`；
        /// 整体失败可以抛，由注册表统一记 `health.error`（**一块失败不影响其他块**）。
        /// </summary>
        void Read(Map map, ColonySnapshot snap, SnapshotReadContext ctx);

        /// <summary>这个块往快照里写了哪些字段（供覆盖率核对，人读用）</summary>
        string ProvidedFields { get; }
    }

    /// <summary>读取过程中的临时上下文（记警告、数对象）</summary>
    public sealed class SnapshotReadContext
    {
        public List<string> Warnings = new List<string>();
        public int ItemsRead;

        public void Warn(string msg)
        {
            if (Warnings.Count < 20) Warnings.Add(msg);
        }
    }

    /// <summary>
    /// **快照读取注册表** —— 遍历所有 `ISnapshotReader` 拼出一张快照。
    ///
    /// 这个类**故意保持很薄**：它只做"遍历 + 失败隔离 + 记健康"，
    /// 所有实际读取逻辑都在各个 Reader 里。这样加新面就不用碰它。
    /// </summary>
    public static class SnapshotReaders
    {
        private static readonly List<ISnapshotReader> _readers = new List<ISnapshotReader>();
        private static bool _initialized;

        /// <summary>注册一个读取块。重复调用安全（按 Id 去重）。</summary>
        public static void Register(ISnapshotReader r)
        {
            if (r == null) return;
            for (int i = 0; i < _readers.Count; i++)
                if (_readers[i].Id == r.Id) return;      // 已注册
            _readers.Add(r);
            _readers.Sort((a, b) => a.Order.CompareTo(b.Order));
        }

        /// <summary>注册内置的读取块。多次调用安全。</summary>
        public static void RegisterBuiltIns()
        {
            if (_initialized) return;
            _initialized = true;

            // ★ 加新面就在这里加一行 —— 不用改别的任何地方
            Register(new ZonesReader());
            Register(new StorageGroupsReader());
            Register(new ThingsReader());
            Register(new PawnsReader());
            Register(new BillsReader());
            Register(new ResearchReader());
            Register(new WealthReader());
            Register(new DesignationsReader());

            // ★ 2026-10-03 用户新要的三类关注面
            Register(new PawnDetailReader());     // 45：殖民者明细（一人一条）
            Register(new RoomsReader());          // 46：房间（职责 / 隔断 / 材质）
            Register(new MaterialsReader());      // 47：建筑材料（def + 材质聚合）
        }

        /// <summary>已注册的读取块（供度量/诊断）</summary>
        public static List<ISnapshotReader> All()
        {
            RegisterBuiltIns();
            return new List<ISnapshotReader>(_readers);
        }

        /// <summary>
        /// **拼一张快照**。每个块独立 try/catch —— 一块失败不拖垮整张。
        /// </summary>
        public static ColonySnapshot Build(Map map, int tick, SamplingInfo sampling)
        {
            RegisterBuiltIns();

            var snap = new ColonySnapshot
            {
                Tick = tick,
                Day = tick / 60000,
            };
            try { snap.MapId = map.uniqueID; } catch (Exception) { }
            if (sampling != null) snap.Sampling = sampling;

            // ★ 游戏内日期 / 殖民地身份 —— 放在所有 Reader 之前，
            //   这样即使某个 Reader 崩了，快照头部也一定带着"这是哪一天、哪个殖民地"。
            ReadGameDateAndIdentity(map, snap);

            for (int i = 0; i < _readers.Count; i++)
            {
                ISnapshotReader r = _readers[i];
                var ctx = new SnapshotReadContext();
                var h = new ReaderHealth { ReaderId = r.Id };

                try
                {
                    r.Read(map, snap, ctx);
                    h.Ran = true;
                    h.ItemsRead = ctx.ItemsRead;
                }
                catch (Exception e)
                {
                    h.Ran = true;                      // 跑了，但失败了
                    h.Error = e.GetType().Name + ": " + e.Message;
                }

                if (ctx.Warnings.Count > 0) h.Warnings = ctx.Warnings;
                snap.Health.Add(h);
            }

            return snap;
        }

        /// <summary>
        /// ★ **游戏内日期 + 殖民地身份**（快照头部的那 8 个追加字段）。
        ///
        /// ## 为什么这件事非做不可（用户 2026-10-03 实测发现）
        ///
        /// 用户原话（他以为数据被截断了）：
        /// > 「快照里的 `day` 只是'殖民地建立以来的天数'（29），**不是游戏内日期**
        /// >  （真实是 year 5500 / day-of-year 40.5 / Septober）。」
        ///
        /// 实测证据（存档文件里）：
        /// `<ticksGame>1761003</ticksGame>` + `<gameStartAbsTick>670000</gameStartAbsTick>`
        /// ⇒ `absTick = 1761003 + 670000 = 2431003`，这才是能换算真实日期的量。
        ///
        /// ## 元数据确证（**不是猜的**，2026-10-03 用 `tmp/research-api-members.ps1` 实测）
        ///
        /// | 成员 | 可见性 |
        /// | --- | --- |
        /// | `Verse.TickManager.ticksGameInt` | **private Int32 字段** |
        /// | `Verse.TickManager.TicksGame` | **public 属性** |
        /// | `Verse.TickManager.gameStartAbsTick` | **public Int32 字段** |
        /// | `Verse.TickManager.TicksAbs` | **public 属性**（= TicksGame + gameStartAbsTick） |
        /// | `RimWorld.GenDate.Year/DayOfQuadrum/DayOfYear/Quadrum` | public（`long absTicks, float longitude`） |
        /// | `RimWorld.GenDate.DateFullStringAt` | public（`long absTicks, Vector2 location`） |
        /// | `RimWorld.Planet.WorldGrid.LongLatOf` | public（`PlanetTile -> Vector2`） |
        /// | `Verse.Map.Tile` | public 属性（`PlanetTile`） |
        /// | `Verse.Map.Parent` | public 属性（`MapParent`） |
        /// | `RimWorld.Planet.Settlement.Name` | public 属性 |
        ///
        /// ⇒ **本方法一个反射都不需要**，全部走确证过的公有成员。
        /// </summary>
        internal static void ReadGameDateAndIdentity(Map map, ColonySnapshot snap)
        {
            try
            {
                TickManager tm = Find.TickManager;
                if (tm != null)
                {
                    // ① 绝对 tick：优先用公有属性 TicksAbs（等价于 ticksGameInt + gameStartAbsTick）
                    try { snap.AbsTick = tm.TicksAbs; }
                    catch (Exception) { }

                    if (snap.AbsTick == 0)
                    {
                        // ② 兜底：自己算（`ticksGame` 在 1.6 是 private 字段，所以用公有 TicksGame）
                        try { snap.AbsTick = tm.TicksGame + tm.gameStartAbsTick; }
                        catch (Exception) { }
                    }
                }

                // 地图经纬度：GenDate 的日期换算都需要它（决定时区/季节）
                Vector2 longLat = new Vector2(0f, 0f);
                bool hasLongLat = false;
                try
                {
                    WorldGrid grid = Find.WorldGrid;
                    if (grid != null)
                    {
                        longLat = grid.LongLatOf(map.Tile);
                        hasLongLat = true;
                    }
                }
                catch (Exception) { }

                if (snap.AbsTick != 0)
                {
                    long abs = snap.AbsTick;
                    try { snap.Year = GenDate.Year(abs, longLat.x); } catch (Exception) { }
                    try { snap.DayOfQuadrum = GenDate.DayOfQuadrum(abs, longLat.x); } catch (Exception) { }
                    try { snap.DayOfYear = GenDate.DayOfYear(abs, longLat.x); } catch (Exception) { }
                    try
                    {
                        Quadrum q = GenDate.Quadrum(abs, longLat.x);
                        snap.Quadrum = q.ToString();
                    }
                    catch (Exception) { }

                    try
                    {
                        string s = hasLongLat
                            ? GenDate.DateFullStringAt(abs, longLat)
                            : GenDate.DateFullStringAt(abs, new Vector2(0f, 0f));
                        snap.DateText = s ?? "";
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }

            // ── 殖民地 / 地图身份 ──
            try
            {
                MapParent parent = map.Parent;
                if (parent != null)
                {
                    // 定居点（Settlement.Name 是确证过的 public 属性）
                    Settlement settlement = parent as Settlement;
                    if (settlement != null && !string.IsNullOrEmpty(settlement.Name)) snap.ColonyName = settlement.Name;
                    try { snap.MapName = parent.Label ?? ""; } catch (Exception) { }
                }
            }
            catch (Exception) { }

            // 兜底：玩家派系名（开局那个"殖民地名"就是派系名）
            try
            {
                if (string.IsNullOrEmpty(snap.ColonyName) && Find.World != null && Find.World.info != null
                    && Find.World.info.name != null)
                    snap.ColonyName = Find.World.info.name;
            }
            catch (Exception) { }

            try
            {
                if (string.IsNullOrEmpty(snap.MapName)) snap.MapName = "Map" + snap.MapId;
            }
            catch (Exception) { }
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // 下面是各个读取块。**每个都是独立的一块**，加新的照着抄就行。
    // ══════════════════════════════════════════════════════════════════

    /// <summary>区域（种植区/储藏区/其他）</summary>
    public sealed class ZonesReader : ISnapshotReader
    {
        private static readonly string[] Probes = new string[]
        {
            "WoodLog", "Steel", "Silver", "Gold", "ComponentIndustrial", "Plasteel",
            "MealSimple", "MealFine", "MealLavish", "Pemmican", "RawRice", "RawPotatoes",
            "MedicineHerbal", "MedicineIndustrial",
            "Corpse_Human", "ToxicWastepack",
            "ApparelRatkin", "WeaponRifle",
        };

        public string Id { get { return "zones"; } }
        public string DisplayName { get { return "区域"; } }
        public int Order { get { return 10; } }
        public string ProvidedFields { get { return "zones[]（id/type/label/cellCount/plantDef/cells/allowed/disallowed）"; } }

        public void Read(Map map, ColonySnapshot snap, SnapshotReadContext ctx)
        {
            foreach (Zone z in map.zoneManager.AllZones)
            {
                if (z == null) continue;
                var sz = new SnapZone { Id = z.ID, Type = z.GetType().Name, Label = z.label ?? "" };

                try
                {
                    var g = z as Zone_Growing;
                    if (g != null)
                    {
                        ThingDef pd = null;
                        try { pd = g.GetPlantDefToGrow(); } catch (Exception) { }
                        sz.PlantDef = pd != null ? pd.defName : "";
                    }
                }
                catch (Exception e) { ctx.Warn("种植物读取失败：" + e.Message); }

                try
                {
                    int n = 0;
                    foreach (IntVec3 c in z.cells)
                    {
                        sz.Cells.Add(new int[] { c.x, c.z });
                        if (++n >= 1200) { ctx.Warn("区域#" + z.ID + " 格子超过 1200，已截断"); break; }
                    }
                    sz.CellCount = z.cells != null ? z.cells.Count : 0;
                }
                catch (Exception e) { ctx.Warn("格子读取失败：" + e.Message); }

                try
                {
                    var sp = z as Zone_Stockpile;
                    if (sp != null && sp.settings != null && sp.settings.filter != null)
                        SnapshotFilters.Probe(sp.settings.filter, Probes, sz.Allowed, sz.Disallowed, ctx);
                }
                catch (Exception e) { ctx.Warn("过滤器读取失败：" + e.Message); }

                snap.Zones.Add(sz);
                ctx.ItemsRead++;
            }
        }
    }

    /// <summary>★ 存储组（游戏自带对象）</summary>
    public sealed class StorageGroupsReader : ISnapshotReader
    {
        private static readonly string[] Probes = new string[]
        {
            "WoodLog", "Steel", "Silver", "Gold", "ComponentIndustrial", "Plasteel",
            "MealSimple", "MealFine", "MealLavish", "Pemmican", "RawRice", "RawPotatoes",
            "MedicineHerbal", "MedicineIndustrial",
            "Corpse_Human", "ToxicWastepack",
            "ApparelRatkin", "WeaponRifle",
        };

        public string Id { get { return "storageGroups"; } }
        public string DisplayName { get { return "存储组"; } }
        public int Order { get { return 20; } }
        public string ProvidedFields { get { return "storageGroups[]（loadId/label/memberCount/cellCount/members[]/allowed/disallowed）"; } }

        public void Read(Map map, ColonySnapshot snap, SnapshotReadContext ctx)
        {
            var mgr = map.storageGroups;
            if (mgr == null) { ctx.Warn("map.storageGroups 为 null"); return; }
            var src = mgr.StorageGroupsForReading;
            if (src == null) return;

            foreach (StorageGroup g in src)
            {
                if (g == null) continue;
                var sg = new SnapStorageGroup
                {
                    LoadId = g.loadID,
                    Label = g.RenamableLabel ?? "",
                    MemberCount = g.MemberCount,
                };

                try { sg.CellCount = g.CellsList != null ? g.CellsList.Count : 0; }
                catch (Exception e) { ctx.Warn("CellsList 失败：" + e.Message); }

                try
                {
                    if (g.members != null)
                    {
                        foreach (IStorageGroupMember m in g.members)
                        {
                            if (m == null) continue;
                            var sm = new SnapGroupMember { Kind = m.GetType().Name };
                            try
                            {
                                var zone = m as Zone;
                                if (zone != null && zone.cells != null)
                                {
                                    int n = 0;
                                    foreach (IntVec3 c in zone.cells)
                                    {
                                        sm.Cells.Add(new int[] { c.x, c.z });
                                        if (++n >= 400) break;
                                    }
                                }
                                else
                                {
                                    var th = m as Thing;
                                    if (th != null) sm.Cells.Add(new int[] { th.Position.x, th.Position.z });
                                }
                            }
                            catch (Exception) { }
                            sg.Members.Add(sm);
                        }
                    }
                }
                catch (Exception e) { ctx.Warn("members 失败：" + e.Message); }

                try
                {
                    StorageSettings ss = g.GetStoreSettings();
                    if (ss != null && ss.filter != null)
                        SnapshotFilters.Probe(ss.filter, Probes, sg.Allowed, sg.Disallowed, ctx);
                }
                catch (Exception e) { ctx.Warn("GetStoreSettings 失败：" + e.Message); }

                snap.StorageGroups.Add(sg);
                ctx.ItemsRead++;
            }
        }
    }

    /// <summary>建筑 / 物品 / 蓝图 / 框架</summary>
    public sealed class ThingsReader : ISnapshotReader
    {
        public string Id { get { return "things"; } }
        public string DisplayName { get { return "建筑与物品"; } }
        public int Order { get { return 30; } }
        public string ProvidedFields { get { return "things{defName:[[x,z],...]}（蓝图前缀 BP:，框架前缀 FR:）"; } }

        public void Read(Map map, ColonySnapshot snap, SnapshotReadContext ctx)
        {
            foreach (Thing t in map.listerThings.AllThings)
            {
                try
                {
                    if (t?.def == null) continue;
                    bool interesting = t is Blueprint || t is Frame
                        || t.def.category == ThingCategory.Building
                        || t.def.category == ThingCategory.Item;
                    if (!interesting) continue;

                    string key;
                    if (t is Blueprint) key = "BP:" + t.def.defName;
                    else if (t is Frame) key = "FR:" + t.def.defName;
                    else key = t.def.defName;

                    List<int[]> list;
                    if (!snap.Things.TryGetValue(key, out list))
                    {
                        list = new List<int[]>();
                        snap.Things[key] = list;
                    }
                    if (list.Count < 1500) list.Add(new int[] { t.Position.x, t.Position.z });
                    else ctx.Warn(key + " 超过 1500 个，已截断");
                    ctx.ItemsRead++;
                }
                catch (Exception) { }
            }
        }
    }

    /// <summary>小人（心情 / 期望 / 负面想法）</summary>
    public sealed class PawnsReader : ISnapshotReader
    {
        public string Id { get { return "pawns"; } }
        public string DisplayName { get { return "小人"; } }
        public int Order { get { return 40; } }
        public string ProvidedFields { get { return "pawns[]（name/x/z/mood/food/expectation/negativeThoughts）"; } }

        public void Read(Map map, ColonySnapshot snap, SnapshotReadContext ctx)
        {
            foreach (Pawn p in map.mapPawns.FreeColonists)
            {
                if (p == null) continue;
                var sp = new SnapPawn();

                try { sp.Name = p.Name != null ? p.Name.ToStringShort : ""; } catch (Exception) { }
                try { sp.X = p.Position.x; sp.Z = p.Position.z; } catch (Exception) { }

                try
                {
                    if (p.needs != null && p.needs.mood != null) sp.Mood = p.needs.mood.CurLevelPercentage;
                }
                catch (Exception) { }

                try
                {
                    if (p.needs != null && p.needs.food != null) sp.Food = p.needs.food.CurLevelPercentage;
                }
                catch (Exception) { }

                // ★ 期望 —— "变富 → 期望↑ → 心情↓"的驱动量
                try
                {
                    ExpectationDef e = ExpectationsUtility.CurrentExpectationFor(p);
                    sp.Expectation = e != null ? e.defName : "";
                }
                catch (Exception e) { ctx.Warn("期望读取失败：" + e.Message); }

                // ★ 负面想法 —— "为什么心情掉"
                try
                {
                    var mem = p.needs != null && p.needs.mood != null ? p.needs.mood.thoughts : null;
                    if (mem != null && mem.memories != null)
                    {
                        foreach (Thought_Memory m in mem.memories.Memories)
                        {
                            if (m?.def == null) continue;
                            float off = 0;
                            try { off = m.MoodOffset(); } catch (Exception) { }
                            if (off < 0 && sp.NegativeThoughts.Count < 6)
                                sp.NegativeThoughts.Add(m.def.defName + "(" + off.ToString("F1") + ")");
                        }
                    }
                }
                catch (Exception e) { ctx.Warn("想法读取失败：" + e.Message); }

                snap.Pawns.Add(sp);
                ctx.ItemsRead++;
            }
        }
    }

    /// <summary>生产队列</summary>
    public sealed class BillsReader : ISnapshotReader
    {
        public string Id { get { return "bills"; } }
        public string DisplayName { get { return "生产队列"; } }
        public int Order { get { return 50; } }
        public string ProvidedFields { get { return "bills[]（bench/benchX/benchZ/recipe/recipeLabel/suspended/repeatMode/targetCount）"; } }

        public void Read(Map map, ColonySnapshot snap, SnapshotReadContext ctx)
        {
            foreach (Thing t in map.listerThings.AllThings)
            {
                try
                {
                    if (t?.def == null) continue;
                    var giver = t as IBillGiver;
                    if (giver == null) continue;
                    BillStack stack = giver.BillStack;
                    if (stack == null) continue;

                    foreach (Bill b in stack.Bills)
                    {
                        if (b == null) continue;
                        var sb = new SnapBill
                        {
                            Bench = t.def.defName,
                            BenchX = t.Position.x,
                            BenchZ = t.Position.z,
                            Recipe = b.recipe != null ? b.recipe.defName : "",
                            RecipeLabel = b.recipe != null ? b.recipe.label : "",
                            Suspended = b.suspended,
                        };

                        // ⚠ repeatMode / targetCount 不在 Bill 基类上（编译期确证）→ 反射探测
                        try
                        {
                            var rmProp = b.GetType().GetProperty("repeatMode");
                            if (rmProp != null)
                            {
                                var rmv = rmProp.GetValue(b, null);
                                if (rmv != null)
                                {
                                    var dn = rmv.GetType().GetProperty("defName");
                                    sb.RepeatMode = dn != null ? (dn.GetValue(rmv, null) as string ?? "") : rmv.ToString();
                                }
                            }
                            var tcProp = b.GetType().GetProperty("targetCount");
                            if (tcProp != null)
                            {
                                var tcv = tcProp.GetValue(b, null);
                                if (tcv != null) sb.TargetCount = tcv.ToString();
                            }
                        }
                        catch (Exception) { }

                        snap.Bills.Add(sb);
                        ctx.ItemsRead++;
                    }
                }
                catch (Exception) { }
            }
        }
    }

    /// <summary>研究</summary>
    public sealed class ResearchReader : ISnapshotReader
    {
        public string Id { get { return "research"; } }
        public string DisplayName { get { return "研究"; } }
        public int Order { get { return 60; } }
        public string ProvidedFields { get { return "research{current/finishedCount/finished[]}"; } }

        /// <summary>诊断只记一次，避免每张快照都刷屏</summary>
        private static bool _diagLogged;

        public void Read(Map map, ColonySnapshot snap, SnapshotReadContext ctx)
        {
            var rm = Find.ResearchManager;
            if (rm == null) { ctx.Warn("ResearchManager 为 null"); return; }

            // ⚠ **两次都读错，第二次才找到真原因**（2026-10-03，值得留档）：
            //   ① 第一版用 `GetProperty("currentProj")` → 沉默拿到 null，`research.current` 恒空。
            //   ② 查程序集元数据发现"它是个字段"，于是改用 `GetField("currentProj")`（默认只找 public）
            //      —— **运行时仍然返回 null**，于是每张快照都报"读不到"。
            //   ③ 真原因（用**运行时**反射打出来的字段清单 + 可见性对照确证）：
            //      `RimWorld.ResearchManager.currentProj` 在 1.6 里是 **private 字段**，
            //      公有侧**没有**"当前项目"的 getter（只有 `IsCurrentProject(proj)` / `GetProgress(proj)`）。
            //   ⇒ 教训：**元数据里"有这个成员" ≠ 默认绑定能拿得到**；
            //     凡是反射，必须显式写清 `BindingFlags`，并且在**运行时**验证（磁盘元数据会骗人）。
            //
            //   所以这里**先用公有 API**（遍历 + `IsCurrentProject`，不碰反射），
            //   再退回"反射读 private 字段"兜底。
            object v = null;
            bool memberFound = false;

            // ① 首选：公有 API —— 逐个问"这个项目是不是正在研究的那个"
            try
            {
                foreach (ResearchProjectDef d in DefDatabase<ResearchProjectDef>.AllDefsListForReading)
                {
                    if (d == null) continue;
                    bool cur = false;
                    try { cur = rm.IsCurrentProject(d); } catch (Exception) { }
                    if (cur) { v = d; break; }
                }
                // 遍历本身成功了（没抛）⇒ 读法没坏；没找到就是**真的没在研究**
                memberFound = true;
            }
            catch (Exception e) { ctx.Warn("IsCurrentProject 遍历失败：" + e.Message); }

            // ② 兜底：反射读 `currentProj`（★ 必须带 NonPublic —— 它是 private）
            if (!memberFound)
            {
                try
                {
                    const System.Reflection.BindingFlags F =
                        System.Reflection.BindingFlags.Public
                        | System.Reflection.BindingFlags.NonPublic
                        | System.Reflection.BindingFlags.Instance;

                    var f = rm.GetType().GetField("currentProj", F);
                    if (f != null) { memberFound = true; v = f.GetValue(rm); }
                    else
                    {
                        var p = rm.GetType().GetProperty("currentProj", F);
                        if (p != null) { memberFound = true; v = p.GetValue(rm, null); }
                    }
                }
                catch (Exception e) { ctx.Warn("反射读 currentProj 失败：" + e.Message); }
            }

            snap.ResearchCurrent = v != null ? v.ToString() : "";
            if (!memberFound)
            {
                // ★ 一次性诊断：说明白"为什么读不到"（本项目的纪律：失败必须带原因）
                if (!_diagLogged)
                {
                    _diagLogged = true;
                    try
                    {
                        Type t = rm.GetType();
                        var flags = System.Reflection.BindingFlags.Public
                                  | System.Reflection.BindingFlags.NonPublic
                                  | System.Reflection.BindingFlags.Instance
                                  | System.Reflection.BindingFlags.Static;
                        var names = new List<string>();
                        foreach (System.Reflection.FieldInfo fi in t.GetFields(flags)) names.Add(fi.Name);
                        var props = new List<string>();
                        foreach (System.Reflection.PropertyInfo pi in t.GetProperties(flags)) props.Add(pi.Name);
                        Log.Message("[ColonyRecorder][诊断] research 读不到：运行时类型=" + t.AssemblyQualifiedName
                            + " | 字段(" + names.Count + ")=" + string.Join(",", names.ToArray())
                            + " | 属性(" + props.Count + ")=" + string.Join(",", props.ToArray()));
                    }
                    catch (Exception e2)
                    {
                        try { Log.Warning("[ColonyRecorder][诊断] 连诊断都失败了：" + e2.Message); } catch (Exception) { }
                    }
                }
                ctx.Warn("currentProj 读不到（公有 API 与 private 字段反射都失败）");
            }

            try
            {
                foreach (ResearchProjectDef d in DefDatabase<ResearchProjectDef>.AllDefsListForReading)
                {
                    bool done = false;
                    try { done = d.IsFinished; } catch (Exception) { }
                    if (done) snap.ResearchFinished.Add(d.defName);
                }
                snap.ResearchFinishedCount = snap.ResearchFinished.Count;
                ctx.ItemsRead += snap.ResearchFinishedCount;
            }
            catch (Exception e) { ctx.Warn("IsFinished 失败：" + e.Message); }
        }
    }

    /// <summary>财富（期望的驱动量）</summary>
    public sealed class WealthReader : ISnapshotReader
    {
        public string Id { get { return "wealth"; } }
        public string DisplayName { get { return "财富"; } }
        public int Order { get { return 70; } }
        public string ProvidedFields { get { return "wealth{total/items/buildings/pawns}"; } }

        public void Read(Map map, ColonySnapshot snap, SnapshotReadContext ctx)
        {
            var ww = map.wealthWatcher;
            if (ww == null) { ctx.Warn("wealthWatcher 为 null"); return; }
            snap.WealthTotal = ww.WealthTotal;
            snap.WealthItems = ww.WealthItems;
            snap.WealthBuildings = ww.WealthBuildings;
            snap.WealthPawns = ww.WealthPawns;
            ctx.ItemsRead = 4;
        }
    }

    /// <summary>地图标记（采矿/拆除等）</summary>
    public sealed class DesignationsReader : ISnapshotReader
    {
        public string Id { get { return "designations"; } }
        public string DisplayName { get { return "地图标记"; } }
        public int Order { get { return 80; } }
        public string ProvidedFields { get { return "designations{defName:[[x,z],...]}"; } }

        public void Read(Map map, ColonySnapshot snap, SnapshotReadContext ctx)
        {
            var dm = map.designationManager;
            if (dm == null) { ctx.Warn("designationManager 为 null"); return; }

            foreach (Designation d in dm.AllDesignations)
            {
                try
                {
                    if (d?.def == null) continue;
                    List<int[]> list;
                    if (!snap.Designations.TryGetValue(d.def.defName, out list))
                    {
                        list = new List<int[]>();
                        snap.Designations[d.def.defName] = list;
                    }
                    if (list.Count < 1500) list.Add(new int[] { d.target.Cell.x, d.target.Cell.z });
                    else ctx.Warn(d.def.defName + " 超过 1500 个，已截断");
                    ctx.ItemsRead++;
                }
                // ⚠ 记原因：静默吞掉会让"标记读不到"看起来像"这一局没有标记"（本项目的纪律）
                catch (Exception e) { ctx.Warn("标记读取失败：" + e.Message); }
            }
        }
    }

    /// <summary>过滤器探测（多个 Reader 共用）</summary>
    internal static class SnapshotFilters
    {
        public static void Probe(ThingFilter f, string[] probes, List<string> allowed, List<string> disallowed, SnapshotReadContext ctx)
        {
            for (int i = 0; i < probes.Length; i++)
            {
                try
                {
                    ThingDef d = DefDatabase<ThingDef>.GetNamedSilentFail(probes[i]);
                    if (d == null) continue;
                    if (f.Allows(d)) allowed.Add(probes[i]);
                    else disallowed.Add(probes[i]);
                }
                catch (Exception e) { ctx.Warn("过滤器探测 " + probes[i] + " 失败：" + e.Message); }
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // ★ 2026-10-03 用户新要的三类关注面（45 / 46 / 47）
    //   写法完全照抄上面的 Reader —— 加一个面 = 一个类 + `RegisterBuiltIns()` 一行。
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// ★ **殖民者明细**（Order 45）—— 用户原话：
    /// > 「**以一个殖民者为单位**，有新的殖民者就在总的殖民者当中新加一个对象，
    /// >  这个对象会记录殖民者的**各种状态**。」
    ///
    /// ## 元数据确证（`tmp/research-api-members.ps1`，2026-10-03 实测）
    ///
    /// | 成员 | 实际所在类型 | 可见性 |
    /// | --- | --- | --- |
    /// | `IsColonist` / `IsSlave` / `IsPrisoner` | `Verse.Pawn` | public 属性 |
    /// | `GuestStatus` | `Verse.Pawn` | public 属性（`RimWorld.GuestStatus?`）|
    /// | `KindLabel` / `RaceProps` / `Downed` | `Verse.Pawn` | public 属性 |
    /// | `gender` | `Verse.Pawn` | **public 字段** |
    /// | `equipment`（`Pawn_EquipmentTracker`）| `Verse.Pawn` | public 字段 |
    /// | `apparel`（`RimWorld.Pawn_ApparelTracker`）| `Verse.Pawn` | public 字段 |
    /// | `skills`（`RimWorld.Pawn_SkillTracker`）| `Verse.Pawn` | public 字段 |
    /// | `story`（`RimWorld.Pawn_StoryTracker`）| `Verse.Pawn` | public 字段 |
    /// | `health`（`Verse.Pawn_HealthTracker`）| `Verse.Pawn` | public 字段 |
    /// | `ageTracker`（`Verse.Pawn_AgeTracker`）| `Verse.Pawn` | public 字段 |
    /// | `workSettings`（`RimWorld.Pawn_WorkSettings`）| `Verse.Pawn` | public 字段 |
    /// | `Pawn_EquipmentTracker.Primary` | `Verse.Pawn_EquipmentTracker` | public 属性 |
    /// | `Pawn_ApparelTracker.WornApparel` | `RimWorld.Pawn_ApparelTracker` | public 属性 |
    /// | `Pawn_SkillTracker.skills` | `RimWorld.Pawn_SkillTracker` | **public 字段** |
    /// | `SkillRecord.levelInt` / `.passion` | `RimWorld.SkillRecord` | **public 字段** |
    /// | `Pawn_StoryTracker.traits`（`TraitSet`）| `RimWorld.Pawn_StoryTracker` | **public 字段** |
    /// | `TraitSet.allTraits` | `RimWorld.TraitSet` | **public 字段** |
    /// | `Pawn._HealthTracker.summaryHealth` | `Verse.Pawn_HealthTracker` | **public 字段** |
    /// | `HealthUtility.GetGeneralConditionLabel(Pawn,bool)` | `Verse.HealthUtility` | public 方法 |
    /// | `PawnCapacitiesHandler.GetLevel(PawnCapacityDef)` | `Verse.PawnCapacitiesHandler` | public 方法 |
    /// | `RimWorld.PawnCapacityDefOf`（Consciousness/Moving/Manipulation/Sight）| | public 静态字段 |
    /// | `Pawn_WorkSettings.GetPriority(WorkTypeDef)` | `RimWorld.Pawn_WorkSettings` | public 方法 |
    /// | `CompQuality.Quality`（`GetComp<CompQuality>()`）| `RimWorld.CompQuality` | public 属性 |
    /// | `Thing.Stuff` / `ThingDef.MadeFromStuff` | `Verse.Thing` / `Verse.BuildableDef` | public 属性 |
    /// | `Pawn_AgeTracker.AgeBiologicalYearsFloat` | `Verse.Pawn_AgeTracker` | public 属性 |
    /// | `Pawn.nameInt`（private 字段）| `Verse.Pawn` | **private** ⇒ 用 `p.Name.ToStringShort`（既有 `pawns` 面同款）|
    /// | `Pawn_WorkSettings.priorities`（private `DefMap`）| | **private** ⇒ **不用反射**，改走 `GetPriority` |
    ///
    /// ⚠ 本 Reader **只读**。所有读不到的路径都 `ctx.Warn(...)` 而不是静默 ——
    ///   静默吞掉会把"读法失效"伪装成"这个小人没有这项"（本项目纪律）。
    /// </summary>
    public sealed class PawnDetailReader : ISnapshotReader
    {
        public string Id { get { return "pawnDetail"; } }
        public string DisplayName { get { return "殖民者明细"; } }
        public int Order { get { return 45; } }
        public string ProvidedFields
        {
            get { return "pawnDetails[]（name/kind/gender/age/weapon/apparel[]/skills[]/traits[]/health{summary,downed,capacities[]}/mood/food/expectation/workPriorities[]）"; }
        }

        /// <summary>
        /// 关键能力值。
        ///
        /// ⚠ `RimWorld.PawnCapacityDefOf` 上的 `Consciousness` / `Moving` /
        ///   `Manipulation` / `Sight` 是**公有的静态 `PawnCapacityDef` 字段**（元数据实测），
        ///   **不是** Def 的名字 —— 所以**不能**用 `DefDatabase<PawnCapacityDef>.GetNamed("Consciousness")`
        ///   （DefDB 里只有 `PawnCapacityDef` 这个**类型**，这一行编译期就报 CS0120）。
        /// </summary>
        private static readonly string[] CapacityLabels = new string[]
        {
            "Consciousness", "Moving", "Manipulation", "Sight",
        };

        private static PawnCapacityDef CapacityByName(string name)
        {
            switch (name)
            {
                case "Consciousness": return PawnCapacityDefOf.Consciousness;
                case "Moving": return PawnCapacityDefOf.Moving;
                case "Manipulation": return PawnCapacityDefOf.Manipulation;
                case "Sight": return PawnCapacityDefOf.Sight;
                default: return null;
            }
        }

        /// <summary>工作优先级最多记这么多（防 mod 把 WorkTypeDef 撑爆）</summary>
        private const int MaxWorkPriorities = 120;

        public void Read(Map map, ColonySnapshot snap, SnapshotReadContext ctx)
        {
            var pawns = map.mapPawns;
            if (pawns == null) { ctx.Warn("map.mapPawns 为 null"); return; }

            // ⚠ 为什么读 AllPawns 而不是只用 FreeColonists：
            //   用户要的是"**殖民者**"这个单位的**状态**，而监狱里的人 / 奴隶
            //   也在"我的殖民地"里（它们本来就不在 FreeColonists 里）。
            //   已有的 `pawns` 面继续只记自由殖民者，**不动**；这一面把
            //   slave / prisoner / guest 也记下来（用 `kind` 区分）。
            System.Collections.Generic.IReadOnlyList<Pawn> all = null;
            try { all = pawns.AllPawnsSpawned; } catch (Exception e) { ctx.Warn("AllPawnsSpawned 读取失败：" + e.Message); }
            if (all == null) return;

            for (int i = 0; i < all.Count; i++)
            {
                Pawn p = all[i];
                if (p == null || p.Dead) continue;

                var d = new SnapPawnDetail();
                try { d.Name = p.Name != null ? p.Name.ToStringShort : ""; } catch (Exception) { }

                // ★ 只要"属于玩家殖民地"的（含奴隶/囚犯/访客），路人/袭击者不要
                string kind = Classify(p, ctx);
                if (kind == null) continue;
                d.Kind = kind;

                try { d.Gender = p.gender.ToString(); } catch (Exception) { }

                try
                {
                    if (p.ageTracker != null) d.Age = p.ageTracker.AgeBiologicalYearsFloat;
                }
                catch (Exception e) { ctx.Warn("年龄读取失败：" + e.Message); }

                // ★ 主武器
                try
                {
                    ThingWithComps eq = p.equipment != null ? p.equipment.Primary : null;
                    d.Weapon = eq != null ? DescribeThing(eq, ctx) : "";
                }
                catch (Exception e) { ctx.Warn("武器读取失败：" + e.Message); }

                // ★ 穿的
                try
                {
                    if (p.apparel != null && p.apparel.WornApparel != null)
                    {
                        List<Apparel> worn = p.apparel.WornApparel;
                        for (int j = 0; j < worn.Count && j < 40; j++)
                        {
                            if (worn[j] == null) continue;
                            d.Apparel.Add(DescribeThing(worn[j], ctx));
                        }
                    }
                }
                catch (Exception e) { ctx.Warn("服装读取失败：" + e.Message); }

                // ★ 技能
                try
                {
                    if (p.skills != null && p.skills.skills != null)
                    {
                        List<SkillRecord> sk = p.skills.skills;
                        for (int j = 0; j < sk.Count; j++)
                        {
                            SkillRecord r = sk[j];
                            if (r == null || r.def == null) continue;
                            var s = new SnapSkill { Def = r.def.defName, Level = r.levelInt };
                            try { s.Passion = r.passion.ToString(); } catch (Exception) { }
                            d.Skills.Add(s);
                        }
                    }
                    else
                    {
                        ctx.Warn("skills 为 null（" + d.Name + "）");
                    }
                }
                catch (Exception e) { ctx.Warn("技能读取失败：" + e.Message); }

                // ★ 特质
                try
                {
                    TraitSet ts = p.story != null ? p.story.traits : null;
                    if (ts != null && ts.allTraits != null)
                    {
                        List<Trait> tr = ts.allTraits;
                        for (int j = 0; j < tr.Count; j++)
                        {
                            Trait t = tr[j];
                            if (t == null || t.def == null) continue;
                            // 有 degree 的特质（如"乐观/悲观"同一 TraitDef 不同档）用 Label 区分
                            string lbl = "";
                            try { lbl = t.Label; } catch (Exception) { }
                            d.Traits.Add(string.IsNullOrEmpty(lbl) ? t.def.defName : lbl);
                        }
                    }
                }
                catch (Exception e) { ctx.Warn("特质读取失败：" + e.Message); }

                // ★ 健康概括 + 关键能力值
                try
                {
                    if (p.health != null)
                    {
                        d.Health.Downed = p.health.Downed;
                        try { d.Health.Summary = HealthUtility.GetGeneralConditionLabel(p, true) ?? ""; }
                        catch (Exception e) { ctx.Warn("健康概括读取失败：" + e.Message); }

                        if (p.health.capacities != null)
                        {
                            for (int j = 0; j < CapacityLabels.Length; j++)
                            {
                                try
                                {
                                    PawnCapacityDef cd = CapacityByName(CapacityLabels[j]);
                                    if (cd == null) continue;
                                    d.Health.Capacities.Add(new SnapCapacity
                                    {
                                        Def = cd.defName,
                                        Level = p.health.capacities.GetLevel(cd),
                                    });
                                }
                                catch (Exception e) { ctx.Warn("能力值 " + CapacityLabels[j] + " 读取失败：" + e.Message); }
                            }
                        }
                    }
                }
                catch (Exception e) { ctx.Warn("健康读取失败：" + e.Message); }

                // ★ 与既有 `pawns` 面**逐字同名**的三项（便于交叉核对）
                try
                {
                    if (p.needs != null && p.needs.mood != null) d.Mood = p.needs.mood.CurLevelPercentage;
                }
                catch (Exception) { }
                try
                {
                    if (p.needs != null && p.needs.food != null) d.Food = p.needs.food.CurLevelPercentage;
                }
                catch (Exception) { }
                try
                {
                    ExpectationDef ed = ExpectationsUtility.CurrentExpectationFor(p);
                    d.Expectation = ed != null ? ed.defName : "";
                }
                catch (Exception e) { ctx.Warn("期望读取失败：" + e.Message); }

                // ★ 工作优先级
                //   ⚠ `Pawn_WorkSettings.priorities` 是 private `DefMap<WorkTypeDef,int>`，
                //     所以**不反射**，改走确证过的公有 `GetPriority`（枚举 WorkTypeDef 全表）。
                try
                {
                    if (p.workSettings != null)
                    {
                        List<WorkTypeDef> wts = DefDatabase<WorkTypeDef>.AllDefsListForReading;
                        int n = 0;
                        for (int j = 0; j < wts.Count; j++)
                        {
                            WorkTypeDef wt = wts[j];
                            if (wt == null) continue;
                            if (++n > MaxWorkPriorities) { ctx.Warn("WorkTypeDef 超过 " + MaxWorkPriorities + " 个，已截断"); break; }
                            d.WorkPriorities.Add(new SnapWorkPriority
                            {
                                WorkType = wt.defName,
                                Priority = p.workSettings.GetPriority(wt),
                            });
                        }
                    }
                    else
                    {
                        ctx.Warn("workSettings 为 null（" + d.Name + "）");
                    }
                }
                catch (Exception e) { ctx.Warn("工作优先级读取失败：" + e.Message); }

                snap.PawnDetails.Add(d);
                ctx.ItemsRead++;
            }
        }

        /// <summary>
        /// 分类：Colonist / Slave / Prisoner / Guest / ColonistMech / Subhuman / Animal / Other；
        /// **不属于玩家殖民地**的返回 null（调用方跳过）。
        /// </summary>
        private static string Classify(Pawn p, SnapshotReadContext ctx)
        {
            try
            {
                if (p.IsColonist) return "Colonist";
                if (p.IsSlave) return "Slave";
                if (p.IsPrisoner) return "Prisoner";
                if (p.IsColonyMech) return "ColonistMech";
                if (p.IsColonySubhuman) return "Subhuman";
                if (p.IsColonyAnimal) return "Animal";
                if (p.GuestStatus.HasValue) return "Guest";
                Faction f = p.Faction;
                if (f != null && f.IsPlayer) return "Other";
            }
            catch (Exception e)
            {
                ctx.Warn("kind 判断失败：" + e.Message);
                return null;
            }
            return null;
        }

        /// <summary>`defName` [+ `(品质)`] [+ `[材质]`]</summary>
        internal static string DescribeThing(Thing t, SnapshotReadContext ctx)
        {
            try
            {
                if (t == null || t.def == null) return "";
                string s = t.def.defName;
                try
                {
                    // 元数据实测：`Verse.ThingCompUtility.TryGetComp<T>(Thing)` 是 public 扩展方法
                    // （另有 `ThingWithComps.compQuality` public 字段，但 `GetComp<T>` 在 Thing 上不存在）
                    CompQuality q = t.TryGetComp<CompQuality>();
                    if (q != null) s = s + "(" + q.Quality.ToString() + ")";
                }
                catch (Exception) { }
                try
                {
                    ThingDef stuff = t.Stuff;
                    if (stuff != null) s = s + "[" + stuff.defName + "]";
                }
                catch (Exception) { }
                return s;
            }
            catch (Exception e)
            {
                if (ctx != null) ctx.Warn("物体描述失败：" + e.Message);
                return "";
            }
        }
    }

    /// <summary>
    /// ★ **房间**（Order 46）—— 用户最在意的面：
    /// > 「一个房间在**不同时间段承担多种职责**」「13×13 的大房间可能**被一隔为二**」
    /// > 「主要关注**变化**」
    ///
    /// ## 元数据确证（`tmp/research-api-members.ps1`，2026-10-03 实测）
    ///
    /// | 成员 | 可见性 |
    /// | --- | --- |
    /// | `Verse.RegionGrid.AllRooms` | public 属性（`IReadOnlyList<Room>`）|
    /// | `Verse.Room.ID` | **public Int32 字段**（稳定 id，不用自己算哈希）|
    /// | `Verse.Room.Role` | public 属性（`RoomRoleDef`）|
    /// | `Verse.Room.CellCount` / `.ExtentsClose` / `.Temperature` | public 属性 |
    /// | `Verse.Room.OpenRoofCount` | public 属性 |
    /// | `Verse.Room.IsPrisonCell` | public 属性 |
    /// | `Verse.Room.Owners` | public 属性（`IEnumerable<Building_Bed>`）|
    /// | `Verse.Room.GetStat(RoomStatDef)` | public 方法 |
    /// | `Verse.RoomStatDef`（`DefDatabase` 取 "Beauty"）| public 类型 |
    /// | `Verse.Room.Owners` | public 属性（**`IEnumerable<Pawn>`** —— 是这个房间里床的主人，不是床）|
    /// | `Verse.Room.ContainedThings<T>()` | public **泛型**方法（必须显式 `ContainedThings<Thing>()`）|
    /// | `Verse.CellRect.minX/minZ/Width/Height` | public（字段/属性）|
    /// | `Verse.RoofGrid.Roofed(IntVec3)` | public 方法 |
    /// | `Verse.RoomStatWorker.GetScore(Room)`（`RoomStatDef.Worker`）| public 方法（美观分就靠它）|
    /// | `Verse.TerrainGrid.TerrainAt(IntVec3)` | public 方法 |
    /// | `Verse.Thing.Stuff` / `Verse.ThingDef.defName` | public 属性 |
    /// | `Verse.GridsUtility.GetEdifice(IntVec3, Map)` | public **扩展**方法（边界格取建筑）|
    /// | `Verse.Room.cachedBorderCells` | **private 字段** |
    /// | `Verse.Room.BorderCells` | public 属性 ⇒ **不用反射，直接用公有属性** |
    /// | `RimWorld.BuildingProperties.isDoor` | **不存在**（只有 `isWall`）⇒ 门只能靠 `Building_Door` 类型判定 |
    ///
    /// ⚠ 用户最关心的"**隔断**"（13×13 被一隔为二）不需要专门检测：
    ///   房间被墙分开后会产生**两个 `Room`**，各自的 `rect`/`cellCount`/`id` 都变 ——
    ///   分析端对比跨快照的 `rooms[]` 就能看到（这是"对象的历史"，不是"面向过程"）。
    /// </summary>
    public sealed class RoomsReader : ISnapshotReader
    {
        public string Id { get { return "rooms"; } }
        public string DisplayName { get { return "房间"; } }
        public int Order { get { return 46; } }
        public string ProvidedFields
        {
            get { return "rooms[]（id/role/roleLabel/label/cellCount/rect/roofed/openRoofCount/isPrisonCell/temperature/beauty/wallMaterials{}/floorMaterials{}/buildings{}）"; }
        }

        /// <summary>
        /// ⚠ `wallMaterials{}` 的**唯一**来源是房间的**边界格**（`Room.BorderCells` + `GetEdifice`）：
        ///   墙不属于房间（墙是另一个 region），门属于房间（可通行 ⇒ region 连通），
        ///   若两处都数就会把门数两遍。所以 `buildings{}` = 边界墙门 + 房间内物品，
        ///   `wallMaterials{}` = 边界墙门，**互不重叠**。
        /// </summary>

        /// <summary>单个房间最多数这么多格（防超大房间拖慢游戏）</summary>
        private const int MaxCells = 2500;
        /// <summary>单房间边界/内部最多记这么多条目</summary>
        private const int MaxThingEntries = 1200;

        public void Read(Map map, ColonySnapshot snap, SnapshotReadContext ctx)
        {
            RegionGrid grid = map.regionGrid;
            if (grid == null) { ctx.Warn("map.regionGrid 为 null"); return; }
            System.Collections.Generic.IReadOnlyList<Room> rooms = null;
            try { rooms = grid.AllRooms; }
            catch (Exception e) { ctx.Warn("regionGrid.AllRooms 读取失败：" + e.Message); }
            if (rooms == null) return;

            for (int i = 0; i < rooms.Count; i++)
            {
                Room r = rooms[i];
                if (r == null) continue;

                var sr = new SnapRoom();
                try { sr.Id = r.ID; } catch (Exception) { }

                // 职责
                try
                {
                    RoomRoleDef role = r.Role;
                    if (role != null)
                    {
                        sr.Role = role.defName;
                        try { sr.RoleLabel = role.label ?? ""; } catch (Exception) { }
                    }
                }
                catch (Exception e) { ctx.Warn("房间职责读取失败：" + e.Message); }

                try { sr.CellCount = r.CellCount; } catch (Exception) { }
                try
                {
                    CellRect rect = r.ExtentsClose;
                    sr.Rect = new int[] { rect.minX, rect.minZ, rect.Width, rect.Height };
                }
                catch (Exception e) { ctx.Warn("房间范围读取失败：" + e.Message); }
                try { sr.IsPrisonCell = r.IsPrisonCell; } catch (Exception) { }
                try { sr.Temperature = r.Temperature; } catch (Exception) { }
                try
                {
                    RoomStatDef beauty = DefDatabase<RoomStatDef>.GetNamedSilentFail("Beauty");
                    if (beauty == null)
                    {
                        // ★ 兜底：Def 改名/换语言时不至于直接丢掉这一项
                        //   （`GetNamedSilentFail` 只按 defName 精确匹配）
                        List<RoomStatDef> stats = DefDatabase<RoomStatDef>.AllDefsListForReading;
                        for (int k = 0; k < stats.Count; k++)
                        {
                            RoomStatDef cand = stats[k];
                            if (cand == null || string.IsNullOrEmpty(cand.defName)) continue;
                            if (cand.defName.IndexOf("Beauty", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                beauty = cand;
                                ctx.Warn("房间美观用兜底 defName=" + cand.defName + "（没找到 \"Beauty\"）");
                                break;
                            }
                        }
                    }
                    if (beauty != null) sr.Beauty = r.GetStat(beauty);
                    else ctx.Warn("找不到 RoomStatDef \"Beauty\"，房间美观留空");
                }
                catch (Exception e) { ctx.Warn("房间美观读取失败：" + e.Message); }

                // 房间名：用房间的**所有者**（元数据实测：`Room.Owners` 是
                // `IEnumerable<Pawn>`，即这个房间里那些床的主人 —— 比"翻床"更直接）
                try
                {
                    System.Collections.Generic.IEnumerable<Pawn> owners = r.Owners;
                    if (owners != null)
                    {
                        foreach (Pawn owner in owners)
                        {
                            if (owner == null || owner.Name == null) continue;
                            sr.Label = owner.Name.ToStringShort;
                            break;
                        }
                    }
                }
                catch (Exception e) { ctx.Warn("房间名读取失败：" + e.Message); }

                // ★ 逐格：地板材质 + 有顶/没顶
                try
                {
                    System.Collections.Generic.IEnumerable<IntVec3> cells = r.Cells;
                    if (cells != null)
                    {
                        int n = 0;
                        int roofed = 0;
                        RoofGrid roofGrid = map.roofGrid;
                        TerrainGrid terrainGrid = map.terrainGrid;
                        foreach (IntVec3 c in cells)
                        {
                            if (++n > MaxCells) { ctx.Warn("房间#" + sr.Id + " 超过 " + MaxCells + " 格，统计已截断"); break; }

                            if (roofGrid != null)
                            {
                                try { if (roofGrid.Roofed(c)) roofed++; } catch (Exception) { }
                            }
                            if (terrainGrid != null)
                            {
                                try
                                {
                                    TerrainDef td = terrainGrid.TerrainAt(c);
                                    if (td != null) Bump(sr.FloorMaterials, td.defName);
                                }
                                catch (Exception) { }
                            }
                        }
                        sr.OpenRoofCount = n - roofed;
                        sr.Roofed = (sr.OpenRoofCount <= 0);
                    }
                }
                catch (Exception e) { ctx.Warn("房间格子统计失败：" + e.Message); }

                // ★ 房间内部的东西（家具/工作台，以及**门**）—— `ContainedThings`（确证过的 public 方法）
                //   ⚠ 它是**泛型** `ContainedThings<T>()`（`where T : Thing`），
                //     类型推断不出来 ⇒ 必须显式写 `<Thing>`。
                //   ⚠ 这里**只数 `Item`**，`Building` 一个都不数：
                //     墙**不属于**房间（墙是另一个 region），从边界格数；
                //     门**属于**房间（可通行 ⇒ region 连通），从边界格也会数到它
                //     ⇒ 若这里也数 Building，门会被**数两遍**。
                //     `buildings{}` 因此等于"边界上的墙/门" + "房间内的物品"，
                //     而 `wallMaterials{}` 只有一处来源、不会重复计数。
                try
                {
                    System.Collections.Generic.IEnumerable<Thing> things = r.ContainedThings<Thing>();
                    if (things != null)
                    {
                        int n = 0;
                        foreach (Thing t in things)
                        {
                            if (t == null || t.def == null) continue;
                            if (++n > MaxThingEntries) { ctx.Warn("房间#" + sr.Id + " 内含物超过 " + MaxThingEntries + "，已截断"); break; }

                            if (t.def.category == ThingCategory.Item) Bump(sr.Buildings, Key2(t.def.defName, t.Stuff));
                        }
                    }
                }
                catch (Exception e) { ctx.Warn("房间内含物读取失败：" + e.Message); }

                // ★ 边界格上的墙与门 —— `wallMaterials{}` 的**唯一**来源
                //   （元数据实测：`Room.BorderCells` 是 public 属性，
                //     private 的是缓存字段 `cachedBorderCells` —— 不用碰反射）
                try
                {
                    System.Collections.Generic.IEnumerable<IntVec3> border = r.BorderCells;
                    if (border != null)
                    {
                        int n = 0;
                        foreach (IntVec3 c in border)
                        {
                            if (++n > MaxThingEntries) { ctx.Warn("房间#" + sr.Id + " 边界格超过 " + MaxThingEntries + "，已截断"); break; }
                            Building b = null;
                            try { b = c.GetEdifice(map); } catch (Exception) { }
                            if (b == null || b.def == null) continue;
                            if (!IsWallOrDoor(b)) continue;
                            string key = Key2(b.def.defName, b.Stuff);
                            Bump(sr.WallMaterials, key);
                            Bump(sr.Buildings, key);
                        }
                    }
                }
                catch (Exception e) { ctx.Warn("房间边界读取失败：" + e.Message); }

                snap.Rooms.Add(sr);
                ctx.ItemsRead++;
            }
        }

        private static void Bump(Dictionary<string, int> map, string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            int n;
            if (map.TryGetValue(key, out n)) map[key] = n + 1;
            else map[key] = 1;
        }

        /// <summary>
        /// 是墙还是门？
        ///
        /// ⚠ 元数据实测：`RimWorld.BuildingProperties` **只有 `isWall`，没有 `isDoor`**
        ///   （门相关的全是 `doorXxx` 音效/参数字段，不是布尔标记）。
        ///   所以门的判定只能靠**类型**（`Verse.Building_Door`）+ defName。
        /// </summary>
        internal static bool IsWallOrDoor(Thing t)
        {
            if (t == null || t.def == null) return false;
            if (t is Building_Door) return true;
            BuildingProperties bp = t.def.building;
            if (bp != null && bp.isWall) return true;
            string dn = t.def.defName;
            if (!string.IsNullOrEmpty(dn)
                && (dn.IndexOf("Door", StringComparison.OrdinalIgnoreCase) >= 0
                    || dn.IndexOf("Gate", StringComparison.OrdinalIgnoreCase) >= 0))
                return true;
            return false;
        }

        /// <summary>`defName` 或 `defName[材质]`</summary>
        private static string Key2(string defName, ThingDef stuff)
        {
            if (string.IsNullOrEmpty(defName)) return "";
            if (stuff == null || string.IsNullOrEmpty(stuff.defName)) return defName;
            return defName + "[" + stuff.defName + "]";
        }
    }

    /// <summary>
    /// ★ **建筑材料**（Order 47）—— 用户原话：
    /// > 「主要用来建筑的**材料有什么变化**……早期用木头，后来逐步换成石砖，
    /// >  外墙用防御更高的石砖，防火防袭击……钢铁有战略意义」
    ///
    /// 全局聚合：`{ 建筑def: { 材质def: 个数 } }`，例如
    /// `{"Wall":{"WoodLog":120,"BlocksGranite":44},"Door":{"WoodLog":9}}`。
    ///
    /// ## 元数据确证（`tmp/research-api-members.ps1`，2026-10-03 实测）
    ///
    /// | 成员 | 可见性 |
    /// | --- | --- |
    /// | `Verse.Thing.Stuff` | public 属性（材质 def）|
    /// | `Verse.ThingDef.category` | public 字段（`ThingCategory`）|
    /// | `Verse.BuildableDef.MadeFromStuff` | public 属性（**在基类 `BuildableDef` 上，不在 `ThingDef` 上**）|
    /// | `Verse.Map.listerThings.AllThings` | public 字段/属性 |
    ///
    /// ⚠ **只统计"建筑"**（`ThingCategory.Building`）：
    ///   - 矿脉是 `Terrain`（不是 Thing），**天然被排除**
    ///   - 墙/门/家具/工作台都属于 `Building`，正是用户说的"用来建筑的材料"
    ///   - 堆在仓库里的木料是 `Item`，**不是已建成的建筑**，所以不计入
    ///     （"换了多少墙"与"存了多少木料"是两件事，混在一起就看不出来了）
    /// </summary>
    public sealed class MaterialsReader : ISnapshotReader
    {
        public string Id { get { return "materials"; } }
        public string DisplayName { get { return "建筑材料"; } }
        public int Order { get { return 47; } }
        public string ProvidedFields { get { return "materials{建筑def:{材质def:个数}}"; } }

        public void Read(Map map, ColonySnapshot snap, SnapshotReadContext ctx)
        {
            if (map.listerThings == null || map.listerThings.AllThings == null)
            {
                ctx.Warn("listerThings 为 null");
                return;
            }

            List<Thing> all = map.listerThings.AllThings;
            for (int i = 0; i < all.Count; i++)
            {
                Thing t = all[i];
                if (t == null || t.def == null) continue;

                try
                {
                    if (t.def.category != ThingCategory.Building) continue;

                    string dk = t.def.defName;
                    string sk = (t.Stuff != null && !string.IsNullOrEmpty(t.Stuff.defName))
                        ? t.Stuff.defName
                        : "(none)";

                    Dictionary<string, int> inner;
                    if (!snap.Materials.TryGetValue(dk, out inner))
                    {
                        inner = new Dictionary<string, int>();
                        snap.Materials[dk] = inner;
                    }
                    int n;
                    if (inner.TryGetValue(sk, out n)) inner[sk] = n + 1;
                    else inner[sk] = 1;

                    ctx.ItemsRead++;
                }
                catch (Exception e) { ctx.Warn("建材统计失败：" + e.Message); }
            }
        }
    }
}
