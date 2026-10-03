using System;
using System.Collections.Generic;
using FoodControl.Core;
using RimWorld;
using Verse;

namespace FoodControl.Game
{
    /// <summary>
    /// 一轮选址的全部中间产物。**存下来是为了遥测与调试**（设计文档 §七quater 要求把
    /// 「用的是哪个基地中心」写进遥测，免得以后对不上账）。
    /// </summary>
    public sealed class PlotPlan
    {
        public Map Map;
        public ThingDef CropDef;
        public SiteSelectionOptions Options = new SiteSelectionOptions();

        /// <summary>喂给 Core 的格子栅格</summary>
        public PassabilityMap Grid;
        /// <summary>到基地的带地形代价距离场（Core 算的）</summary>
        public double[] DistanceField;

        public ResolvedBaseCenter Center;

        /// <summary>已有种植区的「空地」候选（喂 Core 的 FindBestExistingZone）</summary>
        public List<ZonePatch> ZonePatches = new List<ZonePatch>();
        /// <summary>ZoneId → 真 zone 对象（Core 不认游戏类型，所以映射留在 Game 层）</summary>
        public Dictionary<string, Zone_Growing> ZoneById = new Dictionary<string, Zone_Growing>();
        /// <summary>ZoneId → 可扩格（紧邻该区、空闲、可种、且不属于任何区）</summary>
        public Dictionary<string, List<IntVec3>> ExtendCellsById = new Dictionary<string, List<IntVec3>>();

        /// <summary>Core 选出来的最佳新地块（没有可用的就是 null）</summary>
        public FarmPlot BestPlot;

        /// <summary>距离基地可接受范围内、当前未被占用的可种格数 —— 决策的硬约束</summary>
        public int ArableCellsAvailable;
        /// <summary>上面那个数里，有多少是「已有种植区内部的空地」（可复用，不用新开地）</summary>
        public int ArableCellsInExistingZones;

        /// <summary>实际做过可种性判定的格数（性能诊断用）</summary>
        public int ArableScanCells;

        /// <summary>人类可读的说明（进遥测）</summary>
        public string Note = "";
    }

    /// <summary>
    /// 选址：**游戏地图 → Core 的 PassabilityMap + 各种候选**。
    ///
    /// 为什么要这一层：Core 的 `SiteSelection` 是纯算法（可离线毫秒级测），它不认 IntVec3 / Map，
    /// 只认「一维数组 + 距离场」。所以「哪些格能种、走过去多贵、基地在哪」必须在这里翻译进去。
    ///
    /// 设计文档 §七quater：选址不能只看"能不能种"，还要看**离基地多远**——离得远小人每天跑路
    /// 就把工时吃光，而且边缘往往没有电力/仓储覆盖。距离用带地形代价的 BFS 距离场（Core 里），
    /// 不是欧氏距离（河对岸直线 10 格、实际绕桥 200 格）。
    /// </summary>
    public static class PlotPlanner
    {
        /// <summary>单次决策最多为「扩格」预备多少候选格（纯粹是防止病态地图把 CPU 吃光）</summary>
        private const int MaxExtendCandidatesPerZone = 512;

        /// <summary>基地中心不可通行时，往四周找可行走起点的最大半径</summary>
        private const int SeedSearchRadius = 20;

        /// <summary>
        /// 造出完整的选址计划。
        /// </summary>
        /// <param name="map">地图</param>
        /// <param name="cropDef">打算种的作物（可种性判定要用它 —— 游戏是"对某个作物"判能不能种）</param>
        /// <param name="opts">可调参数（Core 的 SiteSelectionOptions）</param>
        /// <param name="desiredCells">这一轮想种多少格（只用来挑"搜哪几种边长"，见 Core 注释）</param>
        public static PlotPlan BuildPlan(Map map, ThingDef cropDef, SiteSelectionOptions opts, int desiredCells)
        {
            var plan = new PlotPlan();
            plan.Map = map;
            plan.CropDef = cropDef;
            plan.Options = opts != null ? opts : new SiteSelectionOptions();

            if (map == null)
            {
                plan.Note = "没有地图";
                return plan;
            }
            if (cropDef == null)
            {
                plan.Note = "没有作物 Def（DefDatabase 里找不到），无法判可种性";
                plan.Grid = MakeEmptyGrid(map);
                return plan;
            }

            int w = map.Size.x;      // 确证：Verse.Map.Size 是 public get（IntVec3）
            int h = map.Size.z;
            if (w <= 0 || h <= 0)
            {
                plan.Note = "地图尺寸为 0";
                plan.Grid = MakeEmptyGrid(map);
                return plan;
            }

            var grid = new PassabilityMap
            {
                Width = w,
                Height = h,
                MoveCost = new double[w * h],
                Arable = new bool[w * h],
                Fertility = new double[w * h],
            };
            plan.Grid = grid;

            // ---------------------------------------------------------------
            // ① 通行代价：不可通行 = 0（Core 的约定）
            // ---------------------------------------------------------------
            for (int z = 0; z < h; z++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = z * w + x;
                    var c = new IntVec3(x, 0, z);
                    grid.MoveCost[i] = MoveCostAt(c, map);
                }
            }

            // ---------------------------------------------------------------
            // ② 基地中心（用户定的兜底顺序，Core 里实现）
            // ---------------------------------------------------------------
            List<CenterCandidate> candidates = BuildCenterCandidates(map, grid);
            plan.Center = SiteSelection.ResolveBaseCenter(grid, candidates);
            if (plan.Center == null)
            {
                plan.Center = new ResolvedBaseCenter
                {
                    Kind = BaseCenterKind.None,
                    X = w / 2,
                    Z = h / 2,
                    Note = "ResolveBaseCenter 返回 null，退回地图中心",
                };
            }

            // ---------------------------------------------------------------
            // ③ 距离场。起点必须是「能走出第一步」的格 —— 否则距离场只有起点自己是 0，
            //    所有可种格都会被判成"太远"，等于这一轮完全放弃（静默失效，最难查）。
            // ---------------------------------------------------------------
            int seed = FindWalkableSeed(grid, plan.Center.X, plan.Center.Z);
            plan.DistanceField = SiteSelection.ComputeDistanceField(
                grid, new List<int> { seed }, plan.Options.HardMaxDistance);

            if (seed != grid.Index(plan.Center.X, plan.Center.Z))
            {
                plan.Note = Append(plan.Note, "基地中心不可通行，起点挪到 (" +
                    (seed % w) + "," + (seed / w) + ")");
            }

            // ---------------------------------------------------------------
            // ④ 可种性 + 肥力：**只扫距离基地可接受范围内的格**
            //
            //    为什么要裁剪：游戏自己的判定 `CanEverPlantAt` 很重（要查地形 tag、肥力、
            //    污染、阻挡物、温度…），250×250 全图 6.25 万次会让一次决策卡住。而超出
            //    HardMaxDistance 的格本来就不会被选中（Core 里也会被丢掉）。
            // ---------------------------------------------------------------
            int scanRadius = (int)Math.Ceiling(plan.Options.HardMaxDistance);
            plan.ArableScanCells = 0;

            for (int z = 0; z < h; z++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = z * w + x;
                    if (grid.MoveCost[i] <= 0) continue;                       // 本来就不通

                    double d = plan.DistanceField != null ? plan.DistanceField[i] : double.MaxValue;
                    if (double.IsInfinity(d) || d > scanRadius) continue;      // 太远，不参与

                    var c = new IntVec3(x, 0, z);
                    plan.ArableScanCells++;

                    grid.Fertility[i] = FertilityAt(c, map);
                    grid.Arable[i] = IsArableFor(cropDef, c, map);
                }
            }

            // ---------------------------------------------------------------
            // ⑤ 现有种植区：优先复用它们的空地，而不是新开地
            // ---------------------------------------------------------------
            CollectGrowingZones(plan, map, grid);

            // ---------------------------------------------------------------
            // ⑥ 挑一块新地（Core 的算法：BFS 距离场 + 滑动窗口 + 评分）
            // ---------------------------------------------------------------
            plan.BestPlot = SiteSelection.FindBestPlot(grid, plan.DistanceField, desiredCells, plan.Options);

            // ---------------------------------------------------------------
            // ⑦ 可用空地总数（决策的硬约束）
            // ---------------------------------------------------------------
            int available = 0;
            int inZones = 0;
            var zoneCellIndex = BuildZoneCellIndex(plan, grid);
            for (int i = 0; i < grid.Arable.Length; i++)
            {
                if (!grid.Arable[i]) continue;
                if (grid.MoveCost[i] <= 0) continue;
                available++;
                if (zoneCellIndex.Contains(i)) inZones++;
            }
            plan.ArableCellsAvailable = available;
            plan.ArableCellsInExistingZones = inZones;

            if (plan.BestPlot == null && available > 0)
            {
                // 有可种格但 Core 挑不出矩形（可能都是零散格 / 都超了距离上限）
                plan.Note = Append(plan.Note, "有可种格但挑不出符合约束的矩形地块");
            }

            return plan;
        }

        // ------------------------------------------------------------------
        // 可盖性（S4 住处域用）
        // ------------------------------------------------------------------

        /// <summary>
        /// **地图 → Core 的 `BuildabilityMap`**（S4 住处域的输入）。
        ///
        /// 和 `PassabilityMap`（可种性/肥力）的区别：这里关心的是
        /// **"能不能盖房子"** —— 地表能不能承重、上面有没有挡路的东西。
        ///
        /// `MoveCost` 直接复用 <see cref="MoveCostAt"/>（和食物域同一套），
        /// 因为选址评分只关心"离基地多远"，两边用同一个代价函数才能比较。
        ///
        /// ⚠ `Buildable` 这里用的是**保守近似**（地表可站 + 格上无植物/建筑/蓝图/框架）。
        ///   真正的权威判据是游戏自己的 `GenConstruct.CanPlaceBlueprintAt`，
        ///   所以放蓝图之前**每一个格子都要再查一次**（见 `ShelterDomain`）。
        ///   这里只负责把"明显不可能的格"筛掉，省掉大量昂贵的游戏判定。
        /// </summary>
        public static BuildabilityMap BuildBuildabilityMap(Map map)
        {
            if (map == null) return null;

            int w = map.Size.x;
            int h = map.Size.z;
            if (w <= 0 || h <= 0) return null;

            var grid = new BuildabilityMap
            {
                Width = w,
                Height = h,
                Buildable = new bool[w * h],
                MoveCost = new double[w * h],
            };

            for (int z = 0; z < h; z++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = z * w + x;
                    var c = new IntVec3(x, 0, z);

                    grid.MoveCost[i] = MoveCostAt(c, map);      // 与食物域同一套代价
                    grid.Buildable[i] = IsBuildableAt(c, map);
                }
            }

            return grid;
        }

        /// <summary>
        /// 这一格"看起来能不能盖"（保守近似，**不是权威判据**）。
        ///
        /// 判据（按代价从低到高）：
        ///   ① 在界内、可行走（不可行走 = 山体/深水/已有建筑占着）
        ///   ② 格上没有植物 / 建筑 / 蓝图 / 框架
        ///
        /// ⚠ 刻意**不**在这里调 `CanPlaceBlueprintAt`：那是 250×250 = 6.25 万次重判定，
        ///   会把一次决策卡死。放蓝图时再逐格查。
        /// </summary>
        public static bool IsBuildableAt(IntVec3 c, Map map)
        {
            try
            {
                if (map == null || !c.InBounds(map)) return false;
                if (!c.Walkable(map)) return false;

                List<Thing> things = c.GetThingList(map);
                for (int i = 0; i < things.Count; i++)
                {
                    Thing t = things[i];
                    if (t == null || t.def == null) continue;
                    if (t.def.category == ThingCategory.Building) return false;
                    if (t is Blueprint) return false;
                    if (t is Frame) return false;
                    if (t is Plant) return false;
                }

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ------------------------------------------------------------------
        // 可种性 / 通行代价 / 肥力
        // ------------------------------------------------------------------

        /// <summary>
        /// 游戏自己的「这格能不能种」。**这是唯一权威判据**，不要自己复刻。
        ///
        /// 依据（确证文档 §C10）：
        ///   - 游戏判可种**根本不看 affordances**，看的是**肥力**：
        ///     `CanEverPlantAt` 的前几步是 inBounds → `map.fertilityGrid.FertilityAt(c) < plantDef.plant.fertilityMin`
        ///     （默认 0.9）就拒。这正好解释了实机里「种植区拒收 Sand、接受 Gravel/Soil」。
        ///   - `TerrainAffordanceDefOf.GrowSoil` / `.Gravel` **不存在**（`GrowSoil` 只是 XML 里的
        ///     一个 TerrainAffordanceDef 名字，没有 DefOf 字段）→ 不要走 affordance 那条路。
        ///   - `CanEverPlantAt(this ThingDef plantDef, IntVec3 c, Map map, out Thing blockingThing,
        ///     bool canWipePlantsExceptTree = false, bool checkMapTemperature = true, bool writeNoReason = false)`
        ///     第 4 个参数**确定是 `out`**（ref 会 CS1620）。
        /// </summary>
        public static bool IsArableFor(ThingDef cropDef, IntVec3 c, Map map)
        {
            if (cropDef == null || map == null) return false;
            try
            {
                if (!c.InBounds(map)) return false;

                // 便宜且安全的预筛（推断，但保守）：
                // 不可通行的格一定是被墙/岩石/深水之类的东西挡着，游戏自己的判定也必然拒绝。
                // 先用它把山体和水面剔掉，能省掉大量昂贵的 CanEverPlantAt 调用。
                // ⚠ 反过来不成立：可行走 ≠ 可种（水泥地可行走但没肥力），所以下面照样要正式判一次。
                if (!c.Walkable(map)) return false;

                Thing blocker;
                AcceptanceReport report = cropDef.CanEverPlantAt(c, map, out blocker, false, true);
                return report.Accepted;
            }
            catch (Exception)
            {
                // 单格判定失败不能影响整轮：保守当作"不能种"
                return false;
            }
        }

        /// <summary>
        /// 通行代价。
        ///
        /// ⚠ **这里是推断，不是确证**：游戏真正用的是内部 `PathGrid`，但 `Verse.AI.PathingContext`
        ///   的字段类型在只读工具宿主里 TypeLoadException（dump 不出来），`Walkable`/`PathCostAt`/
        ///   `CalculatedCostAt` 这些名字**编译探针全部失败**（见 docs/foodcontrol-game层.md）。
        ///   能确证的是 `Verse.GenGrid.Walkable(this IntVec3, Map)`（扩展方法，编译通过）
        ///   和 `Verse.BuildableDef.pathCost`（public int 字段，TerrainDef/BuildingDef 都继承它）。
        ///   所以我用：不可通行 = 0；否则 max(1, terrain.pathCost) + edifice.pathCost。
        /// 对选址来说代价只用于**相对比较**（谁更远），所以这个近似够用；
        /// TODO：等能确证 PathGrid 的公开读法后换成游戏原值。
        /// </summary>
        public static double MoveCostAt(IntVec3 c, Map map)
        {
            try
            {
                if (!c.InBounds(map)) return 0.0;
                if (!c.Walkable(map)) return 0.0;      // 确证：Verse.GenGrid.Walkable 扩展方法（编译探针通过）

                double cost = 1.0;
                TerrainDef terrain = c.GetTerrain(map);
                if (terrain != null && terrain.pathCost > 1) cost = terrain.pathCost;

                Building edifice = c.GetEdifice(map);
                if (edifice != null && edifice.def != null && edifice.def.pathCost > 0)
                {
                    cost += edifice.def.pathCost;      // 门/栅栏之类：地形之外再加价
                }

                return cost > 0 ? cost : 1.0;
            }
            catch (Exception)
            {
                return 0.0;
            }
        }

        /// <summary>肥力（`map.fertilityGrid.FertilityAt`，确证文档 §C10 公开方法）。只用于评分。</summary>
        public static double FertilityAt(IntVec3 c, Map map)
        {
            try
            {
                return map.fertilityGrid.FertilityAt(c);
            }
            catch (Exception)
            {
                return 0.0;
            }
        }

        // ------------------------------------------------------------------
        // 基地中心
        // ------------------------------------------------------------------

        /// <summary>
        /// 按用户拍板的兜底顺序给候选：
        ///   ① 玩家 Home 区域（`map.areaManager.Home`）的**质心** —— 小人实际生活的范围，语义最正确
        ///   ② 没有 Home → **离地图中心最近的贮藏区**（每个 `Zone_Stockpile` 一个候选，必须给 CenterIndex，
        ///      因为 Core 的 `PickNearestStockpile` 会跳过 CenterIndex &lt; 0 的候选）
        ///   ③ 都没有 → Core 自己退到地图中心
        ///
        /// ⚠ Home 区这一条用 `CenterIndex = -1` + 填 Cells：让 **Core** 自己求质心
        ///   （`SiteSelection.CentroidOf`），保证离线测试和线上跑的是同一份实现。
        ///
        /// **internal** 是给 S4 住处域复用（`BuildabilityMap` 同样有 `Index`/`InBounds`）。
        /// </summary>
        internal static List<CenterCandidate> BuildCenterCandidates(Map map, PassabilityMap grid)
        {
            // 只用到 InBounds/Index/Width/Height 四个成员，两种栅格的签名一致
            // → 用一个转换器复用同一份实现（不要抄两遍）
            return BuildCenterCandidatesCore(
                map, grid.Width, grid.Height,
                (x, z) => grid.InBounds(x, z),
                (x, z) => grid.Index(x, z));
        }

        /// <summary>住处域版本（栅格是 `BuildabilityMap`）。</summary>
        internal static List<CenterCandidate> BuildCenterCandidates(Map map, BuildabilityMap grid)
        {
            return BuildCenterCandidatesCore(
                map, grid.Width, grid.Height,
                (x, z) => grid.InBounds(x, z),
                (x, z) => grid.Index(x, z));
        }

        private static List<CenterCandidate> BuildCenterCandidatesCore(
            Map map, int gridW, int gridH,
            Func<int, int, bool> inBounds, Func<int, int, int> index)
        {
            var list = new List<CenterCandidate>();

            // ① Home 区
            try
            {
                AreaManager am = map.areaManager;
                Area_Home home = am != null ? am.Home : null;
                if (home != null && home.TrueCount > 0)
                {
                    var homeCand = new CenterCandidate
                    {
                        Kind = BaseCenterKind.HomeArea,
                        CenterIndex = -1,     // -1 = 让 Core 求质心
                    };
                    foreach (IntVec3 c in home.ActiveCells)
                    {
                        if (!c.InBounds(map)) continue;
                        homeCand.Cells.Add(index(c.x, c.z));
                    }
                    if (homeCand.Cells.Count > 0) list.Add(homeCand);
                }
            }
            catch (Exception)
            {
                // Home 区读不到就退到下一条兜底
            }

            // ② 贮藏区
            try
            {
                List<Zone> zones = map.zoneManager != null ? map.zoneManager.AllZones : null;
                if (zones != null)
                {
                    for (int i = 0; i < zones.Count; i++)
                    {
                        Zone_Stockpile sp = zones[i] as Zone_Stockpile;
                        if (sp == null || sp.cells == null || sp.cells.Count == 0) continue;

                        var cand = new CenterCandidate { Kind = BaseCenterKind.Stockpile };
                        long sx = 0;
                        long sz = 0;
                        for (int k = 0; k < sp.cells.Count; k++)
                        {
                            IntVec3 c = sp.cells[k];
                            if (!c.InBounds(map)) continue;
                            cand.Cells.Add(index(c.x, c.z));
                            sx += c.x;
                            sz += c.z;
                        }
                        if (cand.Cells.Count == 0) continue;

                        // Core 要求贮藏区候选必须带 CenterIndex（否则被跳过）→ 这里自己算质心
                        int cx = (int)Math.Round((double)sx / cand.Cells.Count);
                        int cz = (int)Math.Round((double)sz / cand.Cells.Count);
                        if (!inBounds(cx, cz))
                        {
                            cx = Math.Min(Math.Max(cx, 0), Math.Max(0, gridW - 1));
                            cz = Math.Min(Math.Max(cz, 0), Math.Max(0, gridH - 1));
                        }
                        cand.CenterIndex = index(cx, cz);
                        list.Add(cand);
                    }
                }
            }
            catch (Exception)
            {
                // 贮藏区读不到就退到地图中心（Core 的兜底 ③）
            }

            return list;
        }

        /// <summary>
        /// 从基地中心出发找一个**能走**的起点格。中心落在墙上/山里时（Home 质心完全可能落在建筑内部），
        /// 距离场会退化成一堆 double.MaxValue，导致这一轮静默地"什么都没地可种"。
        /// 所以按半径逐圈找最近的可行走格。
        /// </summary>
        private static int FindWalkableSeed(PassabilityMap grid, int cx, int cz)
        {
            if (grid.InBounds(cx, cz) && grid.MoveCost[grid.Index(cx, cz)] > 0)
            {
                return grid.Index(cx, cz);
            }

            for (int r = 1; r <= SeedSearchRadius; r++)
            {
                for (int dz = -r; dz <= r; dz++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Abs(dx) != r && Math.Abs(dz) != r) continue;   // 只看这一圈的边框
                        int x = cx + dx;
                        int z = cz + dz;
                        if (!grid.InBounds(x, z)) continue;
                        int i = grid.Index(x, z);
                        if (grid.MoveCost[i] > 0) return i;
                    }
                }
            }

            // 全图都不通（极罕见）：退回中心，让距离场自然退化，遥测会显示 arable=0
            if (grid.InBounds(cx, cz)) return grid.Index(cx, cz);
            return 0;
        }

        // ------------------------------------------------------------------
        // 已有种植区
        // ------------------------------------------------------------------

        /// <summary>
        /// 收集每个 `Zone_Growing` 的**空地**与**可扩格**。
        ///
        /// 语义（这里是我定的，写清楚免得误解）：
        ///   - **空地 FreeCells**：该区**已经在范围内**、且当前没有植物/阻挡、可以马上播种的格
        ///     → 不需要任何改动，只要把区的作物设定好，小人自然会去种。
        ///   - **可扩格 ExtendCells**：紧贴该区、空闲、可种、且**不属于任何区**的格
        ///     → 要 `zone.AddCell(...)` 把它们并进来。
        ///
        /// 为什么优先复用而不是新开地：设计文档 §七quater 的意图是别把地铺到天边；
        /// 而且新开区会触发 `AutoHomeAreaMaker.Notify_ZoneCellAdded`（`Zone.AddCell` 的 IL 里调的），
        /// 可能反过来扩大 Home 区、让下一轮的基地中心漂移 —— 少开区就少这种反馈。
        /// </summary>
        private static void CollectGrowingZones(PlotPlan plan, Map map, PassabilityMap grid)
        {
            if (map.zoneManager == null) return;

            // 先把"所有区占用的格"标出来，扩格时要用
            var occupiedByAnyZone = new HashSet<int>();
            List<Zone> allZones = map.zoneManager.AllZones;
            if (allZones != null)
            {
                for (int i = 0; i < allZones.Count; i++)
                {
                    Zone z = allZones[i];
                    if (z == null || z.cells == null) continue;
                    for (int k = 0; k < z.cells.Count; k++)
                    {
                        IntVec3 c = z.cells[k];
                        if (!c.InBounds(map)) continue;
                        occupiedByAnyZone.Add(grid.Index(c.x, c.z));
                    }
                }
            }

            if (allZones == null) return;

            for (int i = 0; i < allZones.Count; i++)
            {
                Zone_Growing zone = allZones[i] as Zone_Growing;
                if (zone == null || zone.cells == null || zone.cells.Count == 0) continue;

                string id = ZoneIdOf(zone);
                var patch = new ZonePatch { ZoneId = id };
                var freeCells = new List<IntVec3>();

                for (int k = 0; k < zone.cells.Count; k++)
                {
                    IntVec3 c = zone.cells[k];
                    if (!c.InBounds(map)) continue;
                    int idx = grid.Index(c.x, c.z);

                    // Arable 已经是"游戏说这里能种"（含：没有植物、没有阻挡物）
                    if (!grid.Arable[idx]) continue;

                    // ⚠ 还要排掉一种情况：格子属于种植区，但里面的植物被别的区/水培盆占着 ——
                    //    CanEverPlantAt 已经覆盖了植物阻挡，这里只要再确认一次没有 Plant 就行。
                    if (c.GetPlant(map) != null) continue;

                    freeCells.Add(c);
                    patch.FreeCells.Add(idx);
                }

                var extendCells = new List<IntVec3>();
                var seen = new HashSet<int>();
                for (int k = 0; k < zone.cells.Count && extendCells.Count < MaxExtendCandidatesPerZone; k++)
                {
                    IntVec3 baseCell = zone.cells[k];
                    for (int d = 0; d < 4; d++)
                    {
                        IntVec3 n = baseCell + GenAdj.CardinalDirections[d];
                        if (!n.InBounds(map)) continue;
                        int nidx = grid.Index(n.x, n.z);
                        if (occupiedByAnyZone.Contains(nidx)) continue;   // 已经在某个区里了
                        if (seen.Contains(nidx)) continue;
                        seen.Add(nidx);
                        if (!grid.Arable[nidx]) continue;
                        if (n.GetPlant(map) != null) continue;
                        extendCells.Add(n);
                        if (extendCells.Count >= MaxExtendCandidatesPerZone) break;
                    }
                }

                plan.ZonePatches.Add(patch);
                plan.ZoneById[id] = zone;
                plan.ExtendCellsById[id] = extendCells;
            }
        }

        /// <summary>把「已有种植区占用的格」做成索引集合（统计可用空地时要区分"已有的空地"）。</summary>
        private static HashSet<int> BuildZoneCellIndex(PlotPlan plan, PassabilityMap grid)
        {
            var set = new HashSet<int>();
            foreach (KeyValuePair<string, Zone_Growing> kv in plan.ZoneById)
            {
                Zone_Growing zone = kv.Value;
                if (zone == null || zone.cells == null) continue;
                for (int k = 0; k < zone.cells.Count; k++)
                {
                    IntVec3 c = zone.cells[k];
                    if (!grid.InBounds(c.x, c.z)) continue;
                    set.Add(grid.Index(c.x, c.z));
                }
            }
            return set;
        }

        /// <summary>
        /// zone 的稳定标识。用游戏自己的 `GetUniqueLoadID()`（`Zone_Growing` + ID），
        /// 比 label 稳（玩家可以改名，而且新区的 label 是自动生成的）。
        /// </summary>
        public static string ZoneIdOf(Zone zone)
        {
            if (zone == null) return "";
            try
            {
                string id = zone.GetUniqueLoadID();
                if (!string.IsNullOrEmpty(id)) return id;
            }
            catch (Exception) { }
            return "zone_" + zone.ID;
        }

        private static PassabilityMap MakeEmptyGrid(Map map)
        {
            int w = map != null ? map.Size.x : 0;
            int h = map != null ? map.Size.z : 0;
            if (w < 0) w = 0;
            if (h < 0) h = 0;
            return new PassabilityMap
            {
                Width = w,
                Height = h,
                MoveCost = new double[w * h],
                Arable = new bool[w * h],
                Fertility = new double[w * h],
            };
        }

        private static string Append(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return b;
            return a + " | " + b;
        }
    }
}
