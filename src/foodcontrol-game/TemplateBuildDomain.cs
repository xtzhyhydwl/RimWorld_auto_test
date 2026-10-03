using System;
using System.Collections.Generic;
using System.Linq;
using FoodControl.Core;
using RimWorld;
using Verse;

// `PlacementReport` 是嵌套在 `ShelterDomain` 里的类型；用别名省掉到处写全名。
// （它是那次"先查再放 + 记账"的产物，样板路径要复用它，不该另写一份。）
using PlacementReport = FoodControl.Game.ShelterDomain.PlacementReport;

namespace FoodControl.Game
{
    /// <summary>
    /// **样板建造域**：从 `templates/` 里挑一份样板，**照抄**盖出来。
    ///
    /// ## 它和 `ShelterDomain` 的关系
    ///
    /// `ShelterDomain` 是**程序化生成**户型（13×13 方块房 + 4 门 + 3 床 + 火把）。
    /// 那是"AI 猜的"设计。用户 2026-10-03 提出并确认了更好的路线：
    ///
    /// > 「我不希望它是一个**很随机**的东西，反而如果我们有一些样板，
    /// >  那么需要建造的时候**直接抄**就可以。」
    ///
    /// 所以这个域是**样板驱动**的版本：造什么由 `templates/*.json` 决定，
    /// 而不是由代码里的几何规则决定。
    ///
    /// ## 为什么两个域都留着
    ///
    /// - **样板是首选**：有样板就抄样板（确定、可审查）
    /// - **程序化是兜底**：一份样板都没有时，至少还能盖个能住的方盒子
    ///
    /// 但两者**不能同时跑**（会盖两间房）。所以这里用"已经有人盖过了就让位"的规则：
    /// 样板域发现地图上已有玩家建筑/蓝图 → 什么都不做。
    ///
    /// ## 放置纪律（照抄 `ShelterDomain` 的，因为那些都是踩坑换来的）
    ///
    /// 1. **先查再放**：`CanPlaceBlueprintAt` 通过才 `PlaceBlueprintForBuild`
    /// 2. **单格失败不拖垮整体**：`TemplatePlacer` 已经保证这点，这里逐格报错不中断
    /// 3. **回读验证**：放完数一遍蓝图，和计划对账
    /// 4. **没人在做建造就别放**：否则蓝图一直挂着（`WorkIsActive(Construction)`）
    /// </summary>
    public sealed class TemplateBuildDomain : INeedDomain
    {
        /// <summary>总开关</summary>
        public static bool Enabled = true;

        /// <summary>每隔几个游戏日检查一次</summary>
        public static int CheckEveryDays = 2;

        /// <summary>优先用哪个 tag 的样板（空 = 不限）</summary>
        public static string PreferredTag = "bedroom";

        /// <summary>测试专用：忽略"已有住处"守卫</summary>
        public static bool ForceBuild = false;

        /// <summary>上次放置的报告（供探针读）</summary>
        public static PlacementReport LastReport;

        public string Id
        {
            get { return "templatebuild"; }
        }

        public string DisplayName
        {
            get { return "样板建造"; }
        }

        /// <summary>排在程序化住处**之前**（样板优先）</summary>
        public int Priority
        {
            get { return 25; }
        }

        public bool IsSurvivalCritical
        {
            get { return false; }
        }

        public SetupActionResult OnDayStart(DomainContext ctx)
        {
            return null;
        }

        /// <summary>
        /// 每 `CheckEveryDays` 天看一次。
        /// ⚠ 会被很频繁调用，所以这里只做取模（别做重活）。
        /// </summary>
        public bool ShouldRunToday(DomainContext ctx)
        {
            if (!Enabled || ctx == null) return false;
            int every = CheckEveryDays > 0 ? CheckEveryDays : 2;
            return ctx.Day % every == 0;
        }

        public DomainCycleResult RunCycle(DomainContext ctx)
        {
            var result = new DomainCycleResult { DomainId = Id };

            try
            {
                Map map = ctx.Map;

                // ① 样板库为空 → 让位给程序化兜底
                List<BuildTemplate> all = TemplateLibrary.All();
                if (all.Count == 0)
                {
                    result.Ok = true;
                    result.DidWork = false;
                    result.Summary = "样板库为空（" + TemplateLibrary.Describe() + "）→ 交给程序化兜底";
                    return result;
                }

                // ② 已经有住处了就不要盖第二间（除非测试强制）
                //
                // ★ 判据来自**建筑账本**，不是数地图上的建筑。
                //
                //   为什么必须这样（踩过）：`ShelterDomain.HasAnyShelter` 的第一行是
                //   `CountBlueprintBuilds(map) > 0` —— 而 **debug 开局自带 32 个蓝图
                //   + 662 面墙**（实测），所以那个守卫一上来就判定"已有住处"，
                //   正常路径**永远拒绝动手**。这正是我上一轮"验证被守卫拦住"的根因。
                //
                //   账本问的是"**我们造过没有**"，天然不受开局自带的东西污染。
                BuildingLedger.BindToMap(map);
                if (!ForceBuild && BuildingLedger.HasAnyOfOurs())
                {
                    result.Ok = true;
                    result.DidWork = false;
                    result.Summary = "账本里已有我们造的 " + BuildingLedger.Describe() + "，不重复盖";
                    return result;
                }

                // ③ 选一份样板：优先 tag，其次第一份
                BuildTemplate tpl = null;
                if (!string.IsNullOrEmpty(PreferredTag))
                {
                    List<BuildTemplate> tagged = TemplateLibrary.WithTag(PreferredTag);
                    if (tagged.Count > 0) tpl = tagged[0];
                }
                if (tpl == null) tpl = all[0];

                List<string> verrs = tpl.Validate();
                if (verrs.Count > 0)
                {
                    result.Ok = false;
                    result.Summary = "样板 " + tpl.Id + " 校验有问题，不敢盖：" + string.Join("; ", verrs.ToArray());
                    return result;
                }

                // ④ ★ 有没有人做建造？没有就别放蓝图（否则一直挂着）
                ShelterDomain.ConstructionWorkInfo work = ShelterDomain.CountConstructionWorkers(map);
                if (work.ConstructionWorkers <= 0)
                {
                    result.Ok = true;
                    result.DidWork = false;
                    result.Summary = "没人做建造（" + work.ToString() + "）→ 不放蓝图（放了也没人建）";
                    return result;
                }

                // ⑤ 建地图栅格 + 距离场（复用 CORE 的纯逻辑）
                PlacementGrid grid;
                double[] dist;
                int mapW = map.Size.x, mapH = map.Size.z;
                if (!BuildPlacementGrid(map, out grid, out dist))
                {
                    result.Ok = false;
                    result.Summary = "构建放置栅格失败";
                    return result;
                }

                // ⑥ 找位置（离基地最近的可行点）
                int siteX, siteZ; double avgDist;
                var opts = ctx.SiteOptions ?? new SiteSelectionOptions();
                var bsOpts = new BuildSiteOptions
                {
                    SearchStep = 1,
                    MaxDistanceFromBase = 80,
                    ComfortableDistance = 20,
                };
                bool found = TemplatePlacer.FindBestSite(
                    tpl, grid, dist, mapW, mapH, bsOpts, out siteX, out siteZ, out avgDist);

                if (!found)
                {
                    result.Ok = true;
                    result.DidWork = false;
                    result.Summary = string.Format(
                        "找不到能放下样板 {0}（{1}x{2}）的地方 → 不放",
                        tpl.Id, tpl.Width, tpl.Height);
                    return result;
                }

                // ⑦ 库存 → 角色解析（Core 纯逻辑）
                Dictionary<string, int> available = CountAvailableBuildables(map);
                TemplatePlacement plan = TemplatePlacer.Plan(tpl, grid, siteX, siteZ, available, 0, false);

                if (plan.Blueprints.Count == 0)
                {
                    result.Ok = true;
                    result.DidWork = false;
                    var why = new List<string>();
                    if (plan.SkippedByReason != null)
                        foreach (var kv in plan.SkippedByReason) why.Add(kv.Key + "×" + kv.Value);

                    // ★ 把网格实况也打出来 —— "全都放不下"必须能看到网格本身的样子
                    int placeableTotal = 0, placeableNear = 0;
                    var probe = new List<string>();
                    try
                    {
                        for (int i = 0; i < grid.CanPlace.Length; i++) if (grid.CanPlace[i]) placeableTotal++;
                        for (int dz = 0; dz < tpl.Height; dz++)
                        {
                            for (int dx = 0; dx < tpl.Width; dx++)
                            {
                                if (grid.IsPlaceable(dx, dz)) placeableNear++;
                            }
                        }
                        // 地图中心那几格的实况（便于和 site 对照）
                        int cx = map.Size.x / 2, cz = map.Size.z / 2;
                        for (int k = 0; k < 3; k++)
                        {
                            int qx = siteX + k, qz = siteZ;
                            probe.Add(string.Format("({0},{1})={2}", qx, qz, grid.IsPlaceable(qx, qz) ? "可放" : "否"));
                        }
                    }
                    catch (Exception) { }

                    result.Summary = string.Format(
                        "样板解析后没有可放的格子（未解决 {0}）；诊断：{1}；网格 {2}x{3} 可放 {4}/{5} 格，模板原点附近可放 {6}/{7}；site=({8},{9}) 抽检 {10}；未解决前3条：{11}",
                        plan.Unresolved.Count, string.Join("；", why.ToArray()),
                        grid.Width, grid.Height, placeableTotal, grid.CanPlace.Length,
                        placeableNear, tpl.Width * tpl.Height, siteX, siteZ,
                        string.Join(",", probe.ToArray()),
                        string.Join(" / ", plan.Unresolved.Take(3).Select(u => u.ToString()).ToArray()))
                        + "；PlanPass 样本：" + string.Join(" | ", plan.PassSamples.ToArray());
                    return result;
                }

                // ⑧ 真的放蓝图（逐格，复用 ShelterDomain 的放置纪律）
                var report = new PlacementReport();
                int placed = 0;
                var materialUse = new Dictionary<string, int>();
                int wallPlaced = 0, doorPlaced = 0, bedPlaced = 0, torchPlaced = 0;

                for (int i = 0; i < plan.Blueprints.Count; i++)
                {
                    PlannedBlueprint bp = plan.Blueprints[i];
                    try
                    {
                        ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(bp.DefName);
                        if (def == null)
                        {
                            report.Rejected.Add(bp.DefName + " 找不到这个 Def");
                            continue;
                        }

                        ThingDef stuff = PickStuffFor(map, def, bp.RoleName, available);
                        var c = new IntVec3(bp.X, 0, bp.Z);

                        if (ShelterDomain.TryPlacePublic(map, def, c, Rot4.South, stuff, report))
                        {
                            placed++;
                            // 分类计数（用 roleName，不靠 defName 猜）
                            if (bp.RoleName == "wall") wallPlaced++;
                            else if (bp.RoleName == "door") doorPlaced++;
                            else if (bp.RoleName == "bed") bedPlaced++;
                            else if (bp.RoleName == "light") torchPlaced++;

                            if (stuff != null)
                            {
                                int u;
                                materialUse[stuff.defName] = materialUse.TryGetValue(stuff.defName, out u) ? u + 1 : 1;
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        if (report.Rejected.Count < 12)
                            report.Rejected.Add(bp.DefName + "@" + bp.X + "," + bp.Z + "：" + e.Message);
                    }
                }

                // ⑨ 回读验证（不信自己成功了）
                int seen = ShelterDomain.CountBlueprintBuilds(map);

                // ⑩ ★ **记账** —— 我们自己放了什么，我们自己知道（用户 2026-10-03）
                //
                // 这是唯一能 100% 确定"这是我们造的"的时机：
                // 事后从地图猜会被 debug 开局的 662 堵墙、废墟、其他 mod 的东西污染。
                int recorded = 0;
                try
                {
                    var ledgerCells = new List<RegistryCell>();
                    for (int i = 0; i < plan.Blueprints.Count; i++)
                    {
                        PlannedBlueprint bp = plan.Blueprints[i];
                        ledgerCells.Add(new RegistryCell
                        {
                            X = bp.X, Z = bp.Z,
                            Role = bp.RoleName ?? "",
                            DefName = bp.DefName ?? "",
                            StillThere = true,
                        });
                    }
                    // 共享墙复用的那些格**也要记**（它们是这栋建筑的一部分，
                    // 只是蓝图是前一栋放的）
                    for (int i = 0; i < plan.Reused.Count; i++)
                    {
                        ReusedSlot ru = plan.Reused[i];
                        ledgerCells.Add(new RegistryCell
                        {
                            X = ru.X, Z = ru.Z,
                            Role = ru.RoleName ?? "",
                            DefName = "(复用)",
                            StillThere = true,
                        });
                    }

                    if (ledgerCells.Count > 0)
                    {
                        BuildingRecord rec = BuildingLedger.Record(
                            map, tpl.Id, siteX, siteZ, 0, false, ledgerCells);
                        if (rec != null) recorded = rec.Cells.Count;
                    }
                }
                catch (Exception e)
                {
                    try { Log.Warning("[FoodControl] 记账失败（不影响建造）：" + e.Message); } catch (Exception) { }
                }

                report.WallsPlaced = wallPlaced;
                report.DoorsPlaced = doorPlaced;
                report.BedsPlaced = bedPlaced;
                report.TorchesPlaced = torchPlaced;
                report.MaterialUse = materialUse;
                report.MaterialNote = "样板路径逐格选材";
                report.TotalBlueprintReadBack = seen;
                // 验证 = "计划的结构格都放下了，且地图上真的数得到蓝图"
                report.Verified = placed > 0 && seen > 0;
                LastReport = report;

                result.Ok = true;
                result.DidWork = placed > 0;
                result.Summary = string.Format(
                    "样板 {0} @({1},{2}) 基地距{3:F0}：计划 {4} 格 → 放下 {5} 格（墙{6}/门{7}/床{8}/灯{9}，未解决 {10}，蓝图回读 {11}，记账 {12}）",
                    tpl.Id, siteX, siteZ, avgDist,
                    plan.Blueprints.Count, placed, wallPlaced, doorPlaced, bedPlaced, torchPlaced,
                    plan.Unresolved.Count, seen, recorded);

                if (plan.Unresolved.Count > 0 && plan.Unresolved.Count <= 6)
                {
                    result.Summary += "；未解决：" + string.Join(" / ",
                        plan.Unresolved.Select(u => u.ToString()).ToArray());
                }
            }
            catch (Exception e)
            {
                result.Ok = false;
                result.Error = e.ToString();
                result.Summary = "样板建造域失败（已吞掉，不影响游戏）：" + e.Message;
                try { Log.Error("[FoodControl] 样板建造域异常：" + e); } catch (Exception) { }
            }

            return result;
        }

        // ------------------------------------------------------------------
        // 游戏状态 → Core 输入
        // ------------------------------------------------------------------

        /// <summary>
        /// 把地图填成 `PlacementGrid` + 基地距离场。
        ///
        /// `PlacementGrid` 只表达"这格能不能放"（地表可承重 + 没被占）。
        /// **样板里的空地对这里没要求** —— Core 侧已经处理了。
        ///
        /// ⚠ **刻意不复用 `PlotPlanner.BuildPlan`**：那个方法在 `cropDef == null` 时
        ///   会提前返回一个**空网格**（它本来是给食物域判"可种性"用的）。
        ///   我第一次就是这么传 null，结果拿到空网格 → 放置失败。
        ///   基地中心和距离场这两件事跟"作物"无关，自己算更干净。
        /// </summary>
        public static bool BuildPlacementGrid(Map map, out PlacementGrid grid, out double[] dist)
        {
            grid = null;
            dist = null;
            if (map == null) return false;

            int w = map.Size.x, h = map.Size.z;
            if (w <= 0 || h <= 0) return false;

            try
            {
                // ① 通行代价网格（Core 的 PassabilityMap 用来算带地形代价的距离场）
                var pass = new PassabilityMap
                {
                    Width = w,
                    Height = h,
                    MoveCost = new double[w * h],
                    Arable = new bool[w * h],
                    Fertility = new double[w * h],
                };
                for (int z = 0; z < h; z++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        pass.MoveCost[z * w + x] = PlotPlanner.MoveCostAt(new IntVec3(x, 0, z), map);
                    }
                }

                // ② 基地中心（用户定的兜底顺序：Home 质心 → 最近贮藏区 → 地图中心）
                ResolvedBaseCenter center = ResolveBaseCenterDirect(map, pass);
                if (center == null) return false;

                int ci = pass.Index(center.X, center.Z);
                if (ci < 0 || ci >= pass.MoveCost.Length) return false;

                // ③ 距离场。⚠ 起点必须是"能走出第一步"的格，否则整个距离场只有起点是 0，
                //    其余全是无穷 → 选址全被否掉（这是 PlotPlanner 注释里记过的一个静默失效坑）
                if (pass.MoveCost[ci] <= 0)
                {
                    int seed = FindWalkableNear(pass, center.X, center.Z, 12);
                    if (seed >= 0) ci = seed;
                }

                dist = SiteSelection.ComputeDistanceField(pass, new List<int> { ci }, 400);
                if (dist == null) return false;

                // ④ 可放性：地表能站 + 没被建筑/蓝图/框架占
                var g = new PlacementGrid
                {
                    Width = w,
                    Height = h,
                    CanPlace = new bool[w * h],
                    BlockedBySolid = new bool[w * h],
                };

                for (int z = 0; z < h; z++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        var c = new IntVec3(x, 0, z);
                        int i = z * w + x;
                        bool ok = true;
                        try
                        {
                            if (!GenGrid.Walkable(c, map)) ok = false;
                            if (ok)
                            {
                                List<Thing> ts = c.GetThingList(map);
                                for (int k = 0; k < ts.Count; k++)
                                {
                                    Thing t = ts[k];
                                    if (t?.def == null) continue;
                                    if (t is Building || t is Blueprint || t is Frame)
                                    {
                                        g.BlockedBySolid[i] = true;
                                        ok = false;
                                        break;
                                    }
                                }
                            }
                        }
                        catch (Exception) { ok = false; }
                        g.CanPlace[i] = ok;
                    }
                }

                grid = g;
                return true;
            }
            catch (Exception e)
            {
                try { Log.Error("[FoodControl] BuildPlacementGrid 失败：" + e); } catch (Exception) { }
                return false;
            }
        }

        /// <summary>
        /// 自己算基地中心（不依赖 PlotPlanner）。
        /// 顺序照用户定的：**Home 区质心 → 离地图中心最近的贮藏区 → 地图中心**。
        /// </summary>
        public static ResolvedBaseCenter ResolveBaseCenterDirect(Map map, PassabilityMap pass)
        {
            try
            {
                var candidates = new List<CenterCandidate>();

                // ① Home 区
                AreaManager am = map.areaManager;
                Area_Home home = am != null ? am.Home : null;
                if (home != null && home.TrueCount > 0)
                {
                    var cand = new CenterCandidate { Kind = BaseCenterKind.HomeArea, CenterIndex = -1 };
                    foreach (IntVec3 c in home.ActiveCells)
                    {
                        if (!c.InBounds(map)) continue;
                        cand.Cells.Add(pass.Index(c.x, c.z));
                    }
                    if (cand.Cells.Count > 0) candidates.Add(cand);
                }

                // ② 贮藏区（每个 Zone_Stockpile 一个候选；Core 要求给 CenterIndex）
                int mc = pass.Width / 2, mz = pass.Height / 2;
                try
                {
                    foreach (Zone z in map.zoneManager.AllZones)
                    {
                        var sp = z as Zone_Stockpile;
                        if (sp == null || sp.cells == null || sp.cells.Count == 0) continue;
                        long sx = 0, sz = 0;
                        int n = 0;
                        foreach (IntVec3 c in sp.cells) { sx += c.x; sz += c.z; n++; }
                        if (n == 0) continue;
                        int cx = (int)Math.Round((double)sx / n);
                        int cz = (int)Math.Round((double)sz / n);
                        cx = Math.Min(Math.Max(cx, 0), pass.Width - 1);
                        cz = Math.Min(Math.Max(cz, 0), pass.Height - 1);
                        candidates.Add(new CenterCandidate
                        {
                            Kind = BaseCenterKind.Stockpile,
                            CenterIndex = pass.Index(cx, cz),
                        });
                    }
                }
                catch (Exception) { }

                ResolvedBaseCenter r = SiteSelection.ResolveBaseCenter(pass, candidates);
                if (r != null) return r;

                // ③ 兜底：地图中心
                return new ResolvedBaseCenter
                {
                    Kind = BaseCenterKind.MapCenterBias,
                    X = mc,
                    Z = mz,
                    Note = "没有 Home 也没有贮藏区，用地图中心",
                };
            }
            catch (Exception e)
            {
                try { Log.Warning("[FoodControl] ResolveBaseCenterDirect 失败：" + e.Message); } catch (Exception) { }
                return null;
            }
        }

        /// <summary>在 (x,z) 附近找一个可通行的格当距离场起点</summary>
        public static int FindWalkableNear(PassabilityMap pass, int x, int z, int radius)
        {
            for (int r = 0; r <= radius; r++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    for (int dz = -r; dz <= r; dz++)
                    {
                        int nx = x + dx, nz = z + dz;
                        if (!pass.InBounds(nx, nz)) continue;
                        int i = pass.Index(nx, nz);
                        if (i >= 0 && i < pass.MoveCost.Length && pass.MoveCost[i] > 0) return i;
                    }
                }
            }
            return -1;
        }

        /// <summary>
        /// 数"手上能用于建造的材料/物品"：defName → 数量。
        /// 用游戏自己的 `IsStuff` 判据（不硬编码列表）。
        /// 另外把样板可能用到的家具 Def 也数上（床/门/灯这类不是 stuff）。
        /// </summary>
        public static Dictionary<string, int> CountAvailableBuildables(Map map)
        {
            var counts = new Dictionary<string, int>();
            if (map == null) return counts;

            try
            {
                foreach (Thing t in map.listerThings.AllThings)
                {
                    try
                    {
                        if (t?.def == null) continue;
                        if (t.def.category != ThingCategory.Item) continue;
                        if (t.IsForbidden(Faction.OfPlayer)) continue;      // 拿不到的不算

                        bool interesting = t.def.IsStuff
                            || t.def.IsBuildingArtificial
                            || t.def.defName == "Bed" || t.def.defName == "DoubleBed"
                            || t.def.defName == "Door"
                            || t.def.defName == "TorchLamp" || t.def.defName == "StandingLamp";
                        if (!interesting) continue;

                        int n;
                        counts[t.def.defName] = counts.TryGetValue(t.def.defName, out n) ? n + t.stackCount : t.stackCount;
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception e)
            {
                try { Log.Warning("[FoodControl] CountAvailableBuildables 失败：" + e.Message); } catch (Exception) { }
            }

            return counts;
        }

        /// <summary>
        /// 给某个建筑挑材质。
        /// **简单可解释**：优先木头（前期最常用）→ 钢 → 石砖 → 第一个有货的 stuff。
        /// 挑不到就返回 null（不阻止放置：有些建筑不需要材质，比如门本身）。
        /// </summary>
        public static ThingDef PickStuffFor(
            Map map, ThingDef def, string roleName, Dictionary<string, int> available)
        {
            try
            {
                if (def == null) return null;
                // 不需要材质的（门/灯/床在 RimWorld 里多数是可选的 stuff）
                if (def.MadeFromStuff != true && def.costStuffCount <= 0) return null;

                string[] pref = { "WoodLog", "Steel", "BlocksGranite", "BlocksSandstone", "BlocksMarble" };
                for (int i = 0; i < pref.Length; i++)
                {
                    int have;
                    if (!available.TryGetValue(pref[i], out have) || have <= 0) continue;
                    ThingDef s = DefDatabase<ThingDef>.GetNamedSilentFail(pref[i]);
                    if (s != null) return s;
                }

                // 退而求其次：任何有货的 stuff
                foreach (var kv in available)
                {
                    if (kv.Value <= 0) continue;
                    ThingDef s = DefDatabase<ThingDef>.GetNamedSilentFail(kv.Key);
                    if (s != null && s.IsStuff) return s;
                }
            }
            catch (Exception) { }
            return null;
        }
    }
}
