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
                //
                // ⚠ `ClusterCount` 要比"想建的区数"**多 1**：
                //   我们要建两片（生活区取**最近**、丢弃区取**最远**），所以至少要有 2 个簇可选。
                //   若只聚 1 簇，就永远只有一簇，"最远的那簇"不存在 → 丢弃区划不出来。
                var opts = new StockpilePlannerOptions
                {
                    ClusterCount = Math.Max(2, MaxZones - existing + 1),
                    MaxCellsPerCluster = 400,
                    MinCellsPerCluster = 9,
                    MaxDistanceFromBase = 120,
                };
                List<StockpileCluster> clusters = StockpilePlanner.Plan(
                    dist, candidates, plan.Grid.Width, opts);

                // ⑥ ★ 建**两片用途明确的**储藏区（用户 2026-10-03 的需求）
                //
                //   ① 生活区储藏区：**离基地最近**的那一簇 → 放正常物资，**禁止**尸体/垃圾
                //   ② 丢弃区：**离基地最远**的那一簇 → 只放尸体/垃圾
                //
                //   ⚠ 为什么不按"距离阈值"推断类型（我第一版就是这么干的，错了）：
                //     实测只划出了一片基地距 89 的区，被整体判成丢弃区 →
                //     **一个生活区都没有**，正常物资无处可放。
                //     类型是**用途**，应该由调用方决定，不该从位置猜。
                var usable = new List<StockpileCluster>();
                for (int i = 0; i < clusters.Count; i++)
                {
                    StockpileCluster c = clusters[i];
                    if (c == null) continue;
                    if (double.IsNegativeInfinity(c.Score) || double.IsInfinity(c.Score)) continue;
                    if (c.Area < opts.MinCellsPerCluster) continue;
                    usable.Add(c);
                }
                usable.Sort((a, b) => a.AvgDistanceToBase.CompareTo(b.AvgDistanceToBase));

                var built = new List<string>();

                // ① 生活区：取**离基地最近**的那一簇（正常物资要离生活区近）
                if (usable.Count > 0)
                {
                    string living = CreateStockpile(map, usable[0], StockpileKind.Living);
                    if (living != null) built.Add("生活区:" + living);
                }

                // ② 丢弃区：**不走聚类**，直接在地图边缘画一个方框
                //
                //    用户原话：「放垃圾的地方就是在地图，你可以直接找一个，
                //    比如说最左边、最右边、最上边、最下边或者说四个角。
                //    直接靠着最边边画一个比较大的方框啊，就可能你画一个 10 乘 10，
                //    直接结束了，就**不用管了**。」
                //
                //    ⚠ 丢弃区**不该**参与 K-means 聚类：那套算法是为"贴着资源的生活区"
                //      设计的（按地理聚拢 + 按离基地距离评分），而丢弃区的选址标准
                //      几乎相反 —— **越靠边越好、离资源越远越好**。
                //      把它们塞进同一个算法会互相干扰（我第一版就这么干，结果只划出一片远离
                //      基地的区，被整体判成丢弃区，**一个生活区都没有**）。
                // 边缘丢弃区用 `PlacementGrid`（"这格能不能放储藏区"），
                // 而这里手里是 `PassabilityMap`（通行/肥力，给食物选址用的）。
                // 两者关心的事不同，所以现场按 `CanPlaceStockpileAt` 造一个 —— 复用同一个判据，
                // 避免"两处判据不一致"这种难查的问题。
                var edgeGrid = new PlacementGrid
                {
                    Width = map.Size.x,
                    Height = map.Size.z,
                    CanPlace = new bool[map.Size.x * map.Size.z],
                };
                for (int zz = 0; zz < map.Size.z; zz++)
                {
                    for (int xx = 0; xx < map.Size.x; xx++)
                    {
                        edgeGrid.CanPlace[zz * map.Size.x + xx] = CanPlaceStockpileAt(map, xx, zz);
                    }
                }

                var edgeSite = EdgeDumpPlanner.Find(
                    edgeGrid, map.Size.x, map.Size.z,
                    EdgeDumpSize, EdgeDumpInset, EdgeDumpMinPlaceableRatio);

                if (edgeSite != null)
                {
                    var dumpCells = new List<IntVec3>();
                    for (int dz = 0; dz < edgeSite.Size; dz++)
                    {
                        for (int dx = 0; dx < edgeSite.Size; dx++)
                        {
                            int x = edgeSite.X + dx, z = edgeSite.Z + dz;
                            if (!CanPlaceStockpileAt(map, x, z)) continue;
                            dumpCells.Add(new IntVec3(x, 0, z));
                        }
                    }
                    string dump = CreateStockpileFromCells(map, dumpCells, StockpileKind.Dump,
                        string.Format("{0}边{1}x{1}", edgeSite.Edge, edgeSite.Size));
                    if (dump != null) built.Add("丢弃区:" + dump);
                    else built.Add("（边缘方框找到了但没建成）");
                }
                else
                {
                    built.Add("（地图边缘没找到够大的空地方框，未划丢弃区）");
                }

                result.Ok = true;
                result.DidWork = built.Count > 0 && !(built.Count == 1 && built[0].StartsWith("（"));

                // ★ 摘要必须带**每个簇的实况**。
                //   我吃过教训：只报"没有达标的"会让人完全不知道该调什么 ——
                //   而 Score 在 Area 过大/过小时会返回 -Infinity，两种原因看起来一模一样。
                var clusterInfo = new List<string>();
                for (int i = 0; i < clusters.Count && i < 6; i++)
                {
                    StockpileCluster c = clusters[i];
                    if (c == null) { clusterInfo.Add("#" + i + "=null"); continue; }
                    clusterInfo.Add(string.Format("#{0}[格{1} 基地距{2:F0} 填充{3:F2} 分{4:F0}]",
                        i, c.Area, c.AvgDistanceToBase, c.FillRatio,
                        double.IsNegativeInfinity(c.Score) ? -99999 : c.Score));
                }

                result.Summary = built.Count > 0
                    ? string.Format("储藏区：{0}（候选 {1} 格 / {2} 个簇，可用 {3} 个；簇实况 {4}）",
                        string.Join(" / ", built.ToArray()),
                        candidates.Count, clusters.Count, usable.Count,
                        string.Join(" ", clusterInfo.ToArray()))
                    : string.Format("看了 {0} 个候选格 / {1} 个簇，但没有达标的（上限 {2} 格，下限 {3} 格）；簇实况：{4}",
                        candidates.Count, clusters.Count,
                        opts.MaxCellsPerCluster, opts.MinCellsPerCluster,
                        string.Join(" ", clusterInfo.ToArray()));
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
        // 储藏区 filter：丢弃区 vs 生活区
        // ------------------------------------------------------------------
        //
        // ## 为什么需要两片、而且 filter 要互为补集（用户 2026-10-03 指出的真缺陷）
        //
        // 用户原话：
        //   「新放下来的存储器它会有一个缺点，就是**他什么都允许放置**，
        //    结果就导致能够从事搬运的小人，他就会去把那种**尸体**给搬运到存储区，
        //    也就是搬运到**生活区附近**，这就不太好了。」
        //
        // 确实如此：DLC 开局地图上通常刷两具尸体，而"允许所有"的储藏区会被小人
        // 用来把尸体搬回生活区旁边 —— 既不美观也有心情影响。
        //
        // 正确做法是**两片互为补集**：
        //   ① 边缘"丢弃区"：默认禁止全部，只允许**尸体/垃圾**这类脏东西
        //   ② 生活区储藏区：默认允许全部，但**显式禁止**尸体/垃圾
        //
        // 为什么用"禁止"而不是"只允许白名单"：
        //   白名单会漏掉 DLC 新增的物资（玩家会发现"某些东西放不进去"）。
        //   而"禁止脏东西"是**黑名单**，黑名单短且稳定，漏进来的风险小得多。

        /// <summary>
        /// 储藏区的**用途**（决定 filter）。用户 2026-10-03 的需求：
        /// 两片区，一片放尸体/垃圾（在边缘），一片放正常物资（在生活区附近）。
        /// </summary>
        public enum StockpileKind
        {
            /// <summary>生活区：放正常物资，**禁止**尸体/垃圾</summary>
            Living = 0,
            /// <summary>丢弃区：**只放**尸体/垃圾（通常在地图边缘）</summary>
            Dump = 1,
        }

        /// <summary>离基地超过这个通行距离的储藏区，视为"边缘丢弃区"</summary>
        public static double DumpZoneMinDistance = 60.0;

        // ---- 边缘丢弃区的参数（用户："画一个 10 乘 10，直接结束了"）----

        /// <summary>丢弃区方框边长</summary>
        public static int EdgeDumpSize = 10;
        /// <summary>离地图边缘留几格（0 = 紧贴边缘）</summary>
        public static int EdgeDumpInset = 1;
        /// <summary>方框内可放格的最低比例</summary>
        public static double EdgeDumpMinPlaceableRatio = 0.6;

        /// <summary>把一块储藏区设成"丢弃区"或"生活区"</summary>
        public static void ApplyFilter(Zone_Stockpile zone, bool isEdgeDump)
        {
            if (zone == null) return;
            try
            {
                if (zone.settings == null || zone.settings.filter == null) return;
                ThingFilter f = zone.settings.filter;

                if (isEdgeDump)
                {
                    // 丢弃区：**只允许脏东西**
                    f.SetDisallowAll(null, null);
                    AllowDirtyThings(f, true);
                }
                else
                {
                    // 生活区：允许一切，**但禁止脏东西**
                    f.SetAllowAll(null, true);
                    AllowDirtyThings(f, false);
                }
            }
            catch (Exception e)
            {
                try { Log.Warning("[FoodControl] 设储藏区 filter 失败（忽略）：" + e.Message); } catch (Exception) { }
            }
        }

        /// <summary>
        /// 允许/禁止"脏东西"（尸体、有毒垃圾）。
        ///
        /// ## 为什么不硬引用常量字段（踩过的坑）
        ///
        /// 我最初写了 `ThingCategoryDefOf.Corpses` 和 `SpecialThingFilterDefOf.AllowCorpses` ——
        /// **两者都不存在**（编译器直接报 CS0117；`SpecialThingFilterDefOf` 只有
        /// `AllowCorpsesUnnatural` / `AllowLargeCorpses`，`ThingCategoryDefOf` 则一个字段都没有）。
        ///
        /// 所以改成**按 defName 查**：
        ///   - 不存在也不会编译报错（用 `GetNamedSilentFail`）
        ///   - 对不同 mod 组合更鲁棒（某个 def 没有就跳过那一条）
        ///
        /// ⚠ 按**类别**（category）操作是首选：DLC 加了新的尸体种类也会被覆盖到，
        ///   而逐个硬编码 Def 名一定会漏。
        /// </summary>
        public static void AllowDirtyThings(ThingFilter f, bool allow)
        {
            if (f == null) return;

            // ① 按**类别**：尸体（人类/动物都在这个 category 下）
            try
            {
                ThingCategoryDef corpses = DefDatabase<ThingCategoryDef>.GetNamedSilentFail("Corpses");
                if (corpses != null) f.SetAllow(corpses, allow, null, null);
            }
            catch (Exception) { }

            // ② 其他可能的"垃圾类"类别名（不同版本/DLC 可能不同，存在才用）
            string[] dirtyCats = new string[]
            {
                "Corpses", "Corpse", "ToxicWaste", "Waste", "Chunks",
            };
            for (int i = 0; i < dirtyCats.Length; i++)
            {
                try
                {
                    ThingCategoryDef cat = DefDatabase<ThingCategoryDef>.GetNamedSilentFail(dirtyCats[i]);
                    if (cat != null) f.SetAllow(cat, allow, null, null);
                }
                catch (Exception) { }
            }

            // ③ 特殊过滤器（有些东西不走 category 而走 special filter）
            //    ⚠ 只有 AllowCorpsesUnnatural / AllowLargeCorpses 这些真实存在的名字，
            //      而且用 defName 查更稳。
            string[] dirtyFilters = new string[]
            {
                "AllowCorpsesUnnatural", "AllowLargeCorpses",
            };
            for (int i = 0; i < dirtyFilters.Length; i++)
            {
                try
                {
                    SpecialThingFilterDef sf = DefDatabase<SpecialThingFilterDef>.GetNamedSilentFail(dirtyFilters[i]);
                    if (sf != null) f.SetAllow(sf, allow);
                }
                catch (Exception) { }
            }

            // ④ 具体的脏东西 Def（存在才设）
            string[] dirtyDefs = new string[]
            {
                "ToxicWastepack", "ToxicWaste", "ToxicWastepacks",
            };
            for (int i = 0; i < dirtyDefs.Length; i++)
            {
                try
                {
                    ThingDef d = DefDatabase<ThingDef>.GetNamedSilentFail(dirtyDefs[i]);
                    if (d != null) f.SetAllow(d, allow);
                }
                catch (Exception) { }
            }
        }

        /// <summary>回读：这块储藏区**会不会收下**某个东西（给验证用）</summary>
        public static bool ZoneAccepts(Zone_Stockpile zone, ThingDef def)
        {
            if (zone == null || def == null) return false;
            try
            {
                if (zone.settings == null || zone.settings.filter == null) return false;
                return zone.settings.filter.Allows(def);
            }
            catch (Exception) { return false; }
        }

        /// <summary>回读：这块储藏区**是不是被设成了丢弃区**（用来断言 filter 真的生效了）</summary>
        public static bool LooksLikeDumpZone(Zone_Stockpile zone)
        {
            if (zone == null) return false;
            try
            {
                // 判据：**不收正常物资**（用木料当代表）⇒ 是丢弃区
                ThingDef wood = DefDatabase<ThingDef>.GetNamedSilentFail("WoodLog");
                bool acceptsNormal = wood != null && ZoneAccepts(zone, wood);
                return !acceptsNormal;
            }
            catch (Exception) { return false; }
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
        /// <summary>
        /// **从一组格子直接建储藏区**（供"边缘丢弃区"用）。
        ///
        /// 与 `CreateStockpile`（吃聚类结果）分开，因为两者的**格子来源完全不同**：
        /// 生活区来自 K-means 聚类，丢弃区来自"边缘方框"。
        /// 复用同一段建区+回读逻辑（那里的姿势是踩坑换来的）。
        /// </summary>
        public static string CreateStockpileFromCells(
            Map map, List<IntVec3> cells, StockpileKind kind, string label)
        {
            if (map == null || cells == null || cells.Count < 4) return null;
            try
            {
                var zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
                map.zoneManager.RegisterZone(zone);
                ZoneMaker.MakeZoneWithCells(zone, cells);
                ApplyFilter(zone, kind == StockpileKind.Dump);

                bool registered = map.zoneManager.AllZones.Contains(zone);
                int cellCount = zone.cells != null ? zone.cells.Count : 0;
                bool cellBack = false;
                try { cellBack = map.zoneManager.ZoneAt(cells[0]) == zone; } catch (Exception) { }

                if (!registered || cellCount == 0 || !cellBack)
                {
                    try { map.zoneManager.DeregisterZone(zone); } catch (Exception) { }
                    try { zone.Delete(false); } catch (Exception) { }
                    return null;
                }

                return string.Format("区#{0}({1}格 {2})", zone.ID, cellCount, label ?? "");
            }
            catch (Exception e)
            {
                try { Log.Error("[FoodControl] CreateStockpileFromCells 失败：" + e); } catch (Exception) { }
                return null;
            }
        }

        public static string CreateStockpile(Map map, StockpileCluster cluster, StockpileKind kind)
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

                // ★ filter：这块区是"丢弃区"还是"生活区"？
                //
                // ⚠ 一开始我用"离基地远不远"来**推断**类型（>60 算丢弃区）——
                //   实测发现这不对：只划分出一片基地距 89 的区，它被整体判成丢弃区
                //   → **一个生活区都没有**，正常物资无处可放。
                //
                //   用户要的是**两片区，各自明确用途**，所以类型应该由**调用方指定**，
                //   而不是从位置猜。
                ApplyFilter(zone, kind == StockpileKind.Dump);

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
