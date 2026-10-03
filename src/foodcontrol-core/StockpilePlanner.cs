using System;
using System.Collections.Generic;

namespace FoodControl.Core
{
    /// <summary>一个待建储藏区的候选格（由游戏层填好"那里有什么资源"）。</summary>
    public sealed class StockpileCandidate
    {
        /// <summary>格索引（z * width + x）</summary>
        public int CellIndex;

        /// <summary>那一格/附近有多少可搬运资源（用于加权，0 = 不重要）</summary>
        public double ResourceWeight = 1.0;
    }

    /// <summary>聚类出来的一个储藏区。</summary>
    public sealed class StockpileCluster
    {
        /// <summary>成员格索引</summary>
        public List<int> Cells = new List<int>();

        /// <summary>到基地中心的平均通行代价距离</summary>
        public double AvgDistanceToBase;

        /// <summary>簇内格之间的紧密度（越小越紧凑，便于算矩形）</summary>
        public double Compactness;

        /// <summary>覆盖的资源权重总量</summary>
        public double CoveredResource;

        /// <summary>总面积</summary>
        public int Area
        {
            get { return Cells.Count; }
        }

        /// <summary>轴对齐包围盒（放区的时候要用）</summary>
        public int MinX, MinZ, MaxX, MaxZ;

        public int BBoxWidth
        {
            get { return MaxX - MinX + 1; }
        }

        public int BBoxHeight
        {
            get { return MaxZ - MinZ + 1; }
        }

        public int BBoxArea
        {
            get { return BBoxWidth * BBoxHeight; }
        }

        /// <summary>
        /// 包围盒里**真的属于本簇**的格占比。
        /// 越高说明这个簇越接近一个规整的矩形（放储藏区越省地方）。
        /// </summary>
        public double FillRatio
        {
            get
            {
                int b = BBoxArea;
                return b > 0 ? (double)Cells.Count / b : 0;
            }
        }

        public double Score;

        public override string ToString()
        {
            return string.Format("簇[格={0} 包围盒={1}x{2} 填充率={3:F2} 基地距={4:F1} 资源={5:F0} 分={6:F1}]",
                Area, BBoxWidth, BBoxHeight, FillRatio, AvgDistanceToBase, CoveredResource, Score);
        }
    }

    public sealed class StockpilePlannerOptions
    {
        /// <summary>要划几个储藏区</summary>
        public int ClusterCount = 2;

        /// <summary>单个储藏区的最大格数（RimWorld 里储藏区太大反而不方便）</summary>
        public int MaxCellsPerCluster = 400;

        /// <summary>单个储藏区的最小格数（太小不值得单独开一个）</summary>
        public int MinCellsPerCluster = 9;

        /// <summary>从基地中心出发，多远以内的格子才考虑（通行代价距离）</summary>
        public double MaxDistanceFromBase = 120.0;

        /// <summary>评分里"离基地近"的权重</summary>
        public double DistanceWeight = 1.0;

        /// <summary>评分里"紧凑"的权重</summary>
        public double CompactnessWeight = 1.0;

        /// <summary>评分里"覆盖资源"的权重</summary>
        public double ResourceWeight = 1.0;

        /// <summary>K-means 最大迭代次数</summary>
        public int MaxIterations = 30;
    }

    /// <summary>
    /// **储藏区选址**。纯算法，不依赖游戏，可离线测试。
    ///
    /// ## 为什么不能用"找一块矩形"那套（食物域的做法）
    ///
    /// 种田要的是**连片、肥沃、离基地近**。储藏区不一样：它的价值在于
    /// **靠近资源**（散落在地图上的木头、钢铁、石料），
    /// 而且**可以有好几个**分散在不同矿区。
    ///
    /// ## 算法（以及一个写错过的地方）
    ///
    /// ```
    /// ① 从基地中心做带地形代价的 BFS → 得到"基地距离场"
    /// ② 收集 MaxDistanceFromBase 以内的候选格（由游戏层决定哪些格"可放储藏区"）
    /// ③ 对候选格的**地理坐标**做 K-means，分成 K 簇
    /// ④ 对每个簇用**距离场**评分（离基地近、紧凑、覆盖资源多）
    /// ```
    ///
    /// ⚠ **第 ③ 步最初写错了**：我用"到基地的通行距离场值"当聚类特征，
    /// 那实际是在**按距离分环**（一圈一圈），而不是**按地理聚拢** ——
    /// 资源堆在一起就该聚成一簇，跟它离基地多远无关。
    /// 所以聚类用**真实坐标**，而"通行代价"只用在**评分**（第 ④ 步）。
    ///
    /// ## 为什么用固定种子的自实现伪随机
    ///
    /// K-means 是局部最优算法，初始中心不同结果差很多，所以做多次随机初始化、
    /// 取总代价最小的一次。但**结果必须可复现**，否则测试没法写断言。
    /// 所以用自实现的 LCG（固定种子），而不是 `System.Random`
    /// （它的实现细节不保证跨运行稳定）。
    /// </summary>
    public static class StockpilePlanner
    {
        /// <summary>随机初始化次数（取总代价最小的一次）</summary>
        public const int Restarts = 8;

        /// <summary>
        /// 主入口。
        /// </summary>
        /// <param name="distField">从基地中心算出的通行代价距离场（`SiteSelection.ComputeDistanceField` 的结果）</param>
        /// <param name="candidates">候选格 + 资源权重（由游戏层填）</param>
        /// <param name="mapWidth">地图宽（用于把格索引反解成坐标）</param>
        /// <param name="opts">参数</param>
        public static List<StockpileCluster> Plan(
            double[] distField,
            IList<StockpileCandidate> candidates,
            int mapWidth,
            StockpilePlannerOptions opts)
        {
            if (opts == null) opts = new StockpilePlannerOptions();
            var result = new List<StockpileCluster>();
            if (distField == null || candidates == null || candidates.Count == 0) return result;
            if (mapWidth <= 0) return result;

            // ① 过滤：必须在距离场覆盖范围内
            var usable = new List<StockpileCandidate>();
            for (int i = 0; i < candidates.Count; i++)
            {
                StockpileCandidate c = candidates[i];
                if (c == null) continue;
                if (c.CellIndex < 0 || c.CellIndex >= distField.Length) continue;
                double d = distField[c.CellIndex];
                if (double.IsInfinity(d) || d > opts.MaxDistanceFromBase) continue;
                usable.Add(c);
            }
            if (usable.Count == 0) return result;

            int k = opts.ClusterCount;
            if (k < 1) k = 1;
            if (k > usable.Count) k = usable.Count;

            // ② 多次随机初始化，取总代价最小的
            int[] bestAssign = null;
            int[] bestCenters = null;
            double bestCost = double.MaxValue;

            var rng = new Lcg(12345);      // 固定种子 ⇒ 结果可复现
            for (int restart = 0; restart < Restarts; restart++)
            {
                int[] centers = InitCenters(usable, k, restart, ref rng);
                int[] assign = new int[usable.Count];
                double cost = RunKMeans(usable, mapWidth, centers, assign, opts);

                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestAssign = assign;
                    bestCenters = centers;
                }
            }

            if (bestAssign == null) return result;

            // ③ 把分簇结果整理成 StockpileCluster
            for (int ci = 0; ci < k; ci++)
            {
                var cluster = new StockpileCluster();
                double distSum = 0;
                int minX = int.MaxValue, minZ = int.MaxValue, maxX = int.MinValue, maxZ = int.MinValue;

                for (int i = 0; i < usable.Count; i++)
                {
                    if (bestAssign[i] != ci) continue;
                    int idx = usable[i].CellIndex;
                    cluster.Cells.Add(idx);
                    cluster.CoveredResource += usable[i].ResourceWeight;
                    distSum += distField[idx];

                    int x = idx % mapWidth;
                    int z = idx / mapWidth;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (z < minZ) minZ = z;
                    if (z > maxZ) maxZ = z;
                }

                if (cluster.Cells.Count == 0) continue;

                cluster.MinX = minX; cluster.MaxX = maxX;
                cluster.MinZ = minZ; cluster.MaxZ = maxZ;
                cluster.AvgDistanceToBase = distSum / cluster.Cells.Count;
                // 松散度用"包围盒面积 − 实际格数"当代理指标：
                // 0 = 完美矩形，越大越散布。
                cluster.Compactness = cluster.BBoxArea - cluster.Cells.Count;
                cluster.Score = Score(cluster, opts);

                result.Add(cluster);
            }

            result.Sort((a, b) => b.Score.CompareTo(a.Score));
            return result;
        }

        /// <summary>
        /// 评分。**单独提出来**，因为它是唯一带主观判断的部分，要能单独调参与测试。
        ///
        /// ```
        /// score = 覆盖资源 × w1
        ///       − 平均基地**通行**距离 × w2
        ///       − 松散度 × w3
        /// ```
        /// 太大/太小的簇直接判负（不值得单独开一个储藏区）。
        /// </summary>
        public static double Score(StockpileCluster cluster, StockpilePlannerOptions opts)
        {
            if (cluster == null) return double.NegativeInfinity;
            if (opts == null) opts = new StockpilePlannerOptions();
            if (cluster.Area < opts.MinCellsPerCluster) return double.NegativeInfinity;
            if (cluster.Area > opts.MaxCellsPerCluster) return double.NegativeInfinity;

            double score = 0;
            score += cluster.CoveredResource * opts.ResourceWeight;
            score -= cluster.AvgDistanceToBase * opts.DistanceWeight;
            score -= (1.0 - cluster.FillRatio) * cluster.Area * opts.CompactnessWeight;
            return score;
        }

        private static int[] InitCenters(IList<StockpileCandidate> usable, int k, int restart, ref Lcg rng)
        {
            var centers = new int[k];

            if (restart == 0)
            {
                // 第一次用确定性策略：按格索引等距取样（可复现）
                for (int i = 0; i < k; i++)
                {
                    int pos = (int)((long)i * usable.Count / k);
                    centers[i] = usable[Math.Min(pos, usable.Count - 1)].CellIndex;
                }
                return centers;
            }

            // 后续用随机（种子固定，所以整体仍可复现）
            var picked = new HashSet<int>();
            for (int i = 0; i < k; i++)
            {
                int tries = 0;
                int pos;
                do
                {
                    pos = rng.Next(usable.Count);
                    tries++;
                } while (picked.Contains(pos) && tries < 50);
                picked.Add(pos);
                centers[i] = usable[pos].CellIndex;
            }
            return centers;
        }

        /// <summary>
        /// 跑 K-means，返回总代价（各点到其簇心的实际地理距离平方和）。
        ///
        /// **聚类用真实坐标**（见类注释里"写错过的地方"）——
        /// 要的是"资源堆在一起就聚成一簇"，而不是按基地距离分环。
        /// 通行代价只在 `Score` 里体现。
        /// </summary>
        private static double RunKMeans(
            IList<StockpileCandidate> usable,
            int mapWidth,
            int[] centers,
            int[] assign,
            StockpilePlannerOptions opts)
        {
            int k = centers.Length;
            var centerX = new double[k];
            var centerZ = new double[k];
            for (int c = 0; c < k; c++)
            {
                centerX[c] = centers[c] % mapWidth;
                centerZ[c] = centers[c] / mapWidth;
            }

            for (int iter = 0; iter < opts.MaxIterations; iter++)
            {
                bool changed = false;

                // ── 分配：按地理距离平方（资源多的地方略微加分，让它更"吸引"）──
                for (int i = 0; i < usable.Count; i++)
                {
                    int idx = usable[i].CellIndex;
                    double x = idx % mapWidth;
                    double z = idx / mapWidth;

                    int best = 0;
                    double bestCost = double.MaxValue;
                    for (int c = 0; c < k; c++)
                    {
                        double dx = x - centerX[c];
                        double dz = z - centerZ[c];
                        double cost = dx * dx + dz * dz;
                        cost -= usable[i].ResourceWeight * 2.0;   // 资源点更"重"
                        if (cost < bestCost) { bestCost = cost; best = c; }
                    }

                    if (assign[i] != best) { assign[i] = best; changed = true; }
                }

                // ── 更新簇心：簇内地点的均值坐标 ──
                var sx = new double[k];
                var sz = new double[k];
                var counts = new int[k];
                for (int i = 0; i < usable.Count; i++)
                {
                    int idx = usable[i].CellIndex;
                    int c = assign[i];
                    sx[c] += idx % mapWidth;
                    sz[c] += idx / mapWidth;
                    counts[c]++;
                }

                for (int c = 0; c < k; c++)
                {
                    if (counts[c] == 0) continue;
                    double mx = sx[c] / counts[c];
                    double mz = sz[c] / counts[c];
                    // 找离均值最近的实际候选点当新簇心（保证簇心始终是个合法格）
                    int bestIdx = centers[c];
                    double bestD = double.MaxValue;
                    for (int i = 0; i < usable.Count; i++)
                    {
                        if (assign[i] != c) continue;
                        int idx = usable[i].CellIndex;
                        double dx = (idx % mapWidth) - mx;
                        double dz = (idx / mapWidth) - mz;
                        double dd = dx * dx + dz * dz;
                        if (dd < bestD) { bestD = dd; bestIdx = idx; }
                    }
                    if (bestIdx != centers[c])
                    {
                        centers[c] = bestIdx;
                        centerX[c] = bestIdx % mapWidth;
                        centerZ[c] = bestIdx / mapWidth;
                        changed = true;
                    }
                }

                if (!changed) break;
            }

            // 总代价（地理距离平方和）
            double total = 0;
            for (int i = 0; i < usable.Count; i++)
            {
                int idx = usable[i].CellIndex;
                int c = assign[i];
                double dx = (idx % mapWidth) - centerX[c];
                double dz = (idx / mapWidth) - centerZ[c];
                total += dx * dx + dz * dz;
            }
            return total;
        }

        /// <summary>
        /// 极简线性同余伪随机。**自己实现是为了保证跨运行可复现** ——
        /// `System.Random` 的实现细节不保证稳定，而我们需要"同样的输入必得同样的储藏区"，
        /// 否则测试没法写断言。
        /// </summary>
        private struct Lcg
        {
            private uint _state;

            public Lcg(int seed)
            {
                _state = (uint)(seed == 0 ? 1 : seed);
            }

            public int Next(int exclusiveMax)
            {
                if (exclusiveMax <= 0) return 0;
                // Numerical Recipes 的常数
                _state = unchecked(_state * 1664525u + 1013904223u);
                return (int)(_state % (uint)exclusiveMax);
            }
        }
    }
}
