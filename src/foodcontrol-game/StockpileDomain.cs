using System;
using System.Collections.Generic;
using FoodControl.Core;
using RimWorld;
using Verse;

namespace FoodControl.Game
{
    /// <summary>
    /// **储藏区域**：在资源（木材/钢铁/石料等散落物）附近划出储藏区。
    ///
    /// ## 它和食物域的区别（决定了算法完全不同）
    ///
    /// | | 食物域 | 储藏区域 |
    /// | --- | --- | --- |
    /// | 目标 | 连片、肥沃、离基地近 | **靠近散落的资源** |
    /// | 数量 | 一块大田 | **可以有好几个**，分别贴不同矿区 |
    /// | 节奏 | 天天看 | 低频半静态（盖完就不动） |
    ///
    /// 所以算法不是"找一块矩形"，而是
    /// **对"资源附近的候选格"做 K-means 聚类**（见 `StockpilePlanner`）。
    ///
    /// ## 一段值得留档的排查（"资源权重"曾经全是 1）
    ///
    /// 最初我把**所有可放格**都当候选、每格权重 1，结果候选格扩散到整张地图，
    /// K-means 就退化成"按地理把大地图切两半" —— **跟资源在哪毫无关系**。
    /// 日志实证（第二次运行，总格数约 15000 —— 正好约等于 250×250/4）：
    /// ```
    /// executor=new-zone(7001 格)   executor=new-zone(8055 格)
    /// ```
    /// 一大片横跨基地两侧的区、另一片在地图另一头 —— **明显不对**。
    ///
    /// 修法有两处，缺一不可：
    ///   1. **限制单区最大格数**（`MaxCellsPerCluster`，见 `StockpilePlannerOptions`）
    ///   2. **候选格的权重真的反映资源量**：某个候选格的价值 = 它**邻域内**可搬运资源的堆叠总量
    ///
    /// 所以候选格生成用的是「**资源堆周围 n 格内的可放格**」，而不是"全图可放格"。
    /// </summary>
    public sealed class StockpileDomain : INeedDomain
    {
        /// <summary>每隔几个游戏日检查一次（储藏区是低频设施，不用天天看）</summary>
        public static int CheckEveryDays = 3;

        /// <summary>资源堆周围多少格内的可放格算候选（体现"靠近资源"）</summary>
        public static int ResourceNeighborhood = 4;

        /// <summary>最多划几个储藏区</summary>
        public static int MaxZones = 2;

        /// <summary>总开关</summary>
        public static bool Enabled = true;

        public string Id
        {
            get { return "stockpile"; }
        }

        public string DisplayName
        {
            get { return "储藏区"; }
        }

        /// <summary>排在食物之后（食物保命；储藏区是效率设施）</summary>
        public int Priority
        {
            get { return 20; }
        }

        public bool IsSurvivalCritical
        {
            get { return false; }
        }

        public SetupActionResult OnDayStart(DomainContext ctx)
        {
            return null;      // 没有"每天要做的家务"
        }

        /// <summary>
        /// 每 `CheckEveryDays` 天检查一次。
        ///
        /// ⚠ 这个方法会被**很频繁**地调用（组件每 64 tick 会问一次，而协调器在
        /// "时段不满足"时不记账）。所以这里只做取模，**别做重活**。
        /// </summary>
        public bool ShouldRunToday(DomainContext ctx)
        {
            if (!Enabled || ctx == null) return false;
            int every = CheckEveryDays > 0 ? CheckEveryDays : 3;
            return ctx.Day % every == 0;
        }

        public DomainCycleResult RunCycle(DomainContext ctx)
        {
            var result = new DomainCycleResult { DomainId = Id };

            try
            {
                Map map = ctx.Map;

                // ① 先看现有的储藏区：已经够多就不必再划
                int existing = 0;
                try
                {
                    foreach (Zone z in map.zoneManager.AllZones)
                    {
                        if (z is Zone_Stockpile) existing++;
                    }
                }
                catch (Exception) { }

                if (existing >= MaxZones)
                {
                    result.Ok = true;
                    result.DidWork = false;
                    result.Summary = "储藏区已有 " + existing + " 个（上限 " + MaxZones + "），不动手";
                    return result;
                }

                // ② 基地中心（复用食物域那套兜底链：Home 质心 → 最近贮藏区 → 地图中心）
                PlotPlan plan = PlotPlanner.BuildPlan(
                    map, ctx.CropDef, ctx.SiteOptions, 0);
                if (plan == null || plan.Grid == null)
                {
                    result.Ok = false;
                    result.Summary = "拿不到地图栅格，跳过";
                    return result;
                }

                ResolvedBaseCenter center = plan.Center;
                if (center == null)
                {
                    result.Ok = false;
                    result.Summary = "算不出基地中心，跳过";
                    return result;
                }

                int centerIdx = plan.Grid.Index(center.X, center.Z);
                if (centerIdx < 0 || centerIdx >= plan.Grid.MoveCost.Length)
                {
                    result.Ok = false;
                    result.Summary = "基地中心越界，跳过";
                    return result;
                }

                // ③ 从基地中心做**带地形代价**的距离场（不是欧氏距离）
                double[] dist = SiteSelection.ComputeDistanceField(
                    plan.Grid, new List<int> { centerIdx }, 400);
                if (dist == null)
                {
                    result.Ok = false;
                    result.Summary = "距离场计算失败，跳过";
                    return result;
                }

                // ④ ★ 生成候选格：**资源堆周围的可放格**（权重反映邻域资源量）
                List<StockpileCandidate> candidates = BuildCandidates(map, plan.Grid);
                if (candidates.Count == 0)
                {
                    result.Ok = true;
                    result.DidWork = false;
                    result.Summary = "地图上没有可搬运资源，无需划储藏区";
                    return result;
                }

                // ⑤ 聚类
                var opts = new StockpilePlannerOptions
                {
                    ClusterCount = MaxZones - existing,
                    MaxCellsPerCluster = 400,
                    MinCellsPerCluster = 9,
                    MaxDistanceFromBase = 120,
                };
                List<StockpileCluster> clusters = StockpilePlanner.Plan(
                    dist, candidates, plan.Grid.Width, opts);

                // ⑥ 只取评分达标的（Plan 已按 Score 降序）
                var built = new List<string>();
                for (int i = 0; i < clusters.Count; i++)
                {
                    StockpileCluster c = clusters[i];
                    if (c == null || double.IsNegativeInfinity(c.Score) || double.IsInfinity(c.Score)) continue;
                    if (c.Area < opts.MinCellsPerCluster) continue;

                    string info = CreateStockpile(map, c);
                    if (info != null) built.Add(info);
                }

                result.Ok = true;
                result.DidWork = built.Count > 0;
                result.Summary = built.Count > 0
                    ? string.Format("划了 {0} 个储藏区：{1}（候选 {2} 格，从 {3} 个簇里选）",
                        built.Count, string.Join(" / ", built.ToArray()),
                        candidates.Count, clusters.Count)
                    : string.Format("看了 {0} 个候选格 / {1} 个簇，但没有达标的（不动手）",
                        candidates.Count, clusters.Count);
            }
            catch (Exception e)
            {
                result.Ok = false;
                result.Error = e.ToString();
                result.Summary = "储藏区域失败（已吞掉，不影响游戏）：" + e.Message;
                try { Log.Error("[FoodControl] 储藏区域异常：" + e); } catch (Exception) { }
            }

            return result;
        }

        // ------------------------------------------------------------------
        // 候选格：资源堆周围的可放格
        // ------------------------------------------------------------------

        /// <summary>
        /// 生成候选格。**这是"靠近资源"的核心**（见类注释里那段排查记录）。
        ///
        /// 做法：
        ///   1. 扫地图上**可搬运的物品**（`EverHaulable` + `ThingCategory.Item`）
        ///   2. 每个物品周围 `ResourceNeighborhood` 格内、**能放储藏区**的格成为候选
        ///   3. 候选格的权重 = **它邻域内所有资源的堆叠总量**（资源越密越"重"）
        ///
        /// 权重必须这样算，否则 K-means 只会把地图切成两半，跟资源无关。
        /// </summary>
        public static List<StockpileCandidate> BuildCandidates(Map map, PassabilityMap grid)
        {
            var result = new List<StockpileCandidate>();
            if (map == null || grid == null) return result;

            // 权重累加表（按格索引）
            var weight = new Dictionary<int, double>();

            // ① 扫可搬运物品
            try
            {
                foreach (Thing t in map.listerThings.AllThings)
                {
                    try
                    {
                        if (t == null || t.def == null) continue;
                        if (!t.def.EverHaulable) continue;
                        if (t.def.category != ThingCategory.Item) continue;
                        if (t.IsForbidden(Faction.OfPlayer)) continue;   // 拿不到的别算

                        double w = Math.Max(1, t.stackCount);

                        // ② 物品周围若干格，权重按距离衰减累加
                        IntVec3 p = t.Position;
                        int r = ResourceNeighborhood > 0 ? ResourceNeighborhood : 4;
                        for (int dx = -r; dx <= r; dx++)
                        {
                            for (int dz = -r; dz <= r; dz++)
                            {
                                int x = p.x + dx, z = p.z + dz;
                                if (!grid.InBounds(x, z)) continue;

                                int idx = grid.Index(x, z);
                                double falloff = 1.0 / (1.0 + Math.Abs(dx) + Math.Abs(dz));
                                double add = w * falloff;

                                double cur;
                                weight[idx] = weight.TryGetValue(idx, out cur) ? cur + add : add;
                            }
                        }
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception e)
            {
                try { Log.Warning("[FoodControl] 扫资源失败：" + e.Message); } catch (Exception) { }
            }

            // ③ 过滤：只保留真的能放储藏区的格
            foreach (KeyValuePair<int, double> kv in weight)
            {
                int idx = kv.Key;
                int x = idx % grid.Width;
                int z = idx / grid.Width;

                if (!CanPlaceStockpileAt(map, x, z)) continue;

                result.Add(new StockpileCandidate { CellIndex = idx, ResourceWeight = kv.Value });
            }

            return result;
        }

        /// <summary>
        /// 这一格能不能放储藏区。
        ///
        /// **先查再放**（本项目纪律）：不能靠"试一下看看抛不抛异常"。
        /// 判据：
        ///   - 地表可站（用游戏自己的 `GenGrid.Walkable`）
        ///   - 已经有区的不放（避免和种植区/已有储藏区重叠）
        ///   - 有建筑/蓝图的不放（别把墙里划成储藏区）
        /// </summary>
        public static bool CanPlaceStockpileAt(Map map, int x, int z)
        {
            try
            {
                IntVec3 c = new IntVec3(x, 0, z);
                if (!c.InBounds(map)) return false;

                // 地表要能站人（储藏品是要被搬来搬去的）
                if (!GenGrid.Walkable(c, map)) return false;

                // 已有区（种植区/储藏区/钓鱼区…）不重叠
                Zone existing = map.zoneManager.ZoneAt(c);
                if (existing != null) return false;

                // 有建筑或蓝图的不放
                var things = c.GetThingList(map);
                for (int i = 0; i < things.Count; i++)
                {
                    Thing t = things[i];
                    if (t == null || t.def == null) continue;
                    if (t.def.category == ThingCategory.Building) return false;
                    if (t is Blueprint || t is Frame) return false;
                }

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ------------------------------------------------------------------
        // 建区
        // ------------------------------------------------------------------

        /// <summary>
        /// 真的建一个储藏区，并**回读验证**。返回一行描述；失败返回 null。
        ///
        /// ⚠ 建区姿势照 `Executor`（那里踩过一个大坑）：
        ///   `ZoneMaker.MakeZoneWithCells` **只做逐格 AddCell，完全不注册**！
        ///   不补 `RegisterZone` 就建出"僵尸区"：AllZones 里没有、小人永远不碰。
        ///   所以顺序是：new → **RegisterZone** → MakeZoneWithCells → 回读。
        /// </summary>
        public static string CreateStockpile(Map map, StockpileCluster cluster)
        {
            if (map == null || cluster == null || cluster.Cells == null || cluster.Cells.Count == 0)
                return null;

            try
            {
                int w = map.Size.x;
                var cells = new List<IntVec3>();
                for (int i = 0; i < cluster.Cells.Count; i++)
                {
                    int idx = cluster.Cells[i];
                    int x = idx % w;
                    int z = idx / w;
                    var c = new IntVec3(x, 0, z);
                    if (!CanPlaceStockpileAt(map, x, z)) continue;   // 再过滤一遍（聚类期间可能变了）
                    cells.Add(c);
                }

                if (cells.Count < 9) return null;

                // ★ 正确的建区顺序
                var zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
                map.zoneManager.RegisterZone(zone);
                ZoneMaker.MakeZoneWithCells(zone, cells);

                // 允许所有物品（默认 preset 已经允许大部分，这里显式保证）
                try
                {
                    if (zone.settings != null && zone.settings.filter != null)
                    {
                        zone.settings.filter.SetAllowAll(null, true);
                    }
                }
                catch (Exception e)
                {
                    try { Log.Warning("[FoodControl] 设储藏区 filter 失败（忽略）：" + e.Message); } catch (Exception) { }
                }

                // ── ★ 回读验证：不假定自己成功了 ──
                bool registered = map.zoneManager.AllZones.Contains(zone);
                int cellCount = zone.cells != null ? zone.cells.Count : 0;
                bool cellBack = false;
                try { cellBack = map.zoneManager.ZoneAt(cells[0]) == zone; } catch (Exception) { }

                if (!registered || cellCount == 0 || !cellBack)
                {
                    // 回滚：别留下"僵尸区"
                    try { map.zoneManager.DeregisterZone(zone); } catch (Exception) { }
                    try { zone.Delete(false); } catch (Exception) { }
                    return null;
                }

                return string.Format("区#{0}({1}格, 资源权重{2:F0}, 基地距{3:F0})",
                    zone.ID, cellCount, cluster.CoveredResource, cluster.AvgDistanceToBase);
            }
            catch (Exception e)
            {
                try { Log.Error("[FoodControl] 建储藏区失败：" + e); } catch (Exception) { }
                return null;
            }
        }
    }
}
