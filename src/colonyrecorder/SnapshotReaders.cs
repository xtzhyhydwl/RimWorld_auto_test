using System;
using System.Collections.Generic;
using RimWorld;
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
}
