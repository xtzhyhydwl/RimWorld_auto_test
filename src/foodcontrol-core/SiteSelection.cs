using System;
using System.Collections.Generic;

namespace FoodControl.Core
{
    /// <summary>地图格子的通行代价（由游戏内适配层填充）。</summary>
    public sealed class PassabilityMap
    {
        public int Width;
        public int Height;

        /// <summary>每格的通行代价；&lt;= 0 表示不可通行（山、深水）。</summary>
        public double[] MoveCost = new double[0];

        /// <summary>每格能否种（地表 affordance 由游戏内判定，Core 不猜）。</summary>
        public bool[] Arable = new bool[0];

        /// <summary>每格的肥力系数（1.0 = 普通土壤），仅用于评分。</summary>
        public double[] Fertility = new double[0];

        /// <summary>索引/边界逻辑**只有一份实现**（`GridIndex`）。</summary>
        private GridIndex Bounds
        {
            get { return new GridIndex(Width, Height); }
        }

        public bool InBounds(int x, int z)
        {
            return Bounds.InBounds(x, z);
        }

        /// <summary>越界返回 -1（旧版会返回一个乱算出来的下标 —— 静默错位的来源）</summary>
        public int Index(int x, int z)
        {
            return Bounds.Index(x, z);
        }

        public bool IsArable(int x, int z)
        {
            return Bounds.At(Arable, x, z, false);
        }
    }

    /// <summary>选出来的一块待建种植区。</summary>
    public sealed class FarmPlot
    {
        public int X;
        public int Z;
        public int Width;
        public int Height;
        public double AvgDistance;
        public double AvgFertility;
        public double Score;

        public int Area
        {
            get { return Width * Height; }
        }

        public override string ToString()
        {
            return string.Format(
                "({0},{1}) {2}x{3} area={4} dist={5:F1} fert={6:F2} score={7:F1}",
                X, Z, Width, Height, Area, AvgDistance, AvgFertility, Score);
        }
    }

    public sealed class SiteSelectionOptions
    {
        /// <summary>种植区的最小边长</summary>
        public int MinSide = 5;
        /// <summary>种植区的最大边长</summary>
        public int MaxSide = 14;

        /// <summary>这个距离以内认为"通勤可接受"，不惩罚（格）。用户定：40。</summary>
        public double ComfortableDistance = 40.0;

        /// <summary>超过可接受距离后，每格的惩罚强度。用户定：1（宁要大块肥地）。</summary>
        public double DistancePenaltyPerCell = 1.0;

        /// <summary>选中的区内允许的非可种格比例上限（避免拉进一堆石头）</summary>
        public double MaxNonArableRatio = 0.25;

        /// <summary>离基地超过这个距离直接不考虑</summary>
        public double HardMaxDistance = 120.0;
    }

    /// <summary>基地中心是怎么定出来的（写进遥测，免得以后对不上账）。</summary>
    public enum BaseCenterKind
    {
        HomeArea = 0,
        Stockpile = 1,
        MapCenterBias = 2,
        None = 3,
    }

    /// <summary>基地中心的候选区域。</summary>
    public sealed class CenterCandidate
    {
        public BaseCenterKind Kind = BaseCenterKind.None;
        /// <summary>该区域包含的格子（已过滤成"玩家自己的"）</summary>
        public List<int> Cells = new List<int>();
        /// <summary>该区域的中心（可选，便于游戏层直接给）</summary>
        public int CenterIndex = -1;
    }

    public sealed class ResolvedBaseCenter
    {
        public BaseCenterKind Kind = BaseCenterKind.None;
        public int X;
        public int Z;
        public string Note = "";
    }

    /// <summary>复用已有种植区的候选。</summary>
    public sealed class ZonePatch
    {
        public string ZoneId = "";
        public List<int> FreeCells = new List<int>();
        public double AvgDistance;
        public double AvgFertility;
        public double Score;

        public int FreeCount
        {
            get { return FreeCells.Count; }
        }
    }

    /// <summary>
    /// 种植区选址。**纯算法，不依赖游戏，可离线测试。**
    ///
    /// 需求来源：用户指出「不能只看能不能种，还得看离基地多远」——
    /// 离得远小人每天跑路就把工时吃光了，而且边缘地带往往没有电力/仓储覆盖。
    ///
    /// 所以评分 = 面积收益 - 距离惩罚 + 肥力加成，距离用**带地形代价的 BFS 距离场**算，
    /// 不是欧氏距离（河对岸直线 10 格、实际绕桥 200 格，欧氏距离会选错）。
    /// </summary>
    public static class SiteSelection
    {
        /// <summary>
        /// 从一组起点做带地形代价的 Dijkstra/BFS，得到每格的通行代价距离。
        /// 返回 null 表示起点全不可通行。
        /// </summary>
        public static double[] ComputeDistanceField(
            PassabilityMap map,
            IList<int> startIndices,
            double maxDistance)
        {
            if (map == null || map.Width <= 0 || map.Height <= 0) return null;
            int n = map.Width * map.Height;
            double[] dist = new double[n];
            for (int i = 0; i < n; i++) dist[i] = double.MaxValue;

            // 简单二叉堆
            var heap = new List<KeyValuePair<double, int>>();
            var cmp = Comparer<KeyValuePair<double, int>>.Create(
                (a, b) => a.Key.CompareTo(b.Key));

            for (int s = 0; s < startIndices.Count; s++)
            {
                int idx = startIndices[s];
                if (idx < 0 || idx >= n) continue;
                if (dist[idx] <= 0) continue;
                dist[idx] = 0;
                HeapPush(heap, cmp, new KeyValuePair<double, int>(0, idx));
            }

            int[] dx = { 1, -1, 0, 0 };
            int[] dz = { 0, 0, 1, -1 };

            while (heap.Count > 0)
            {
                KeyValuePair<double, int> top = HeapPop(heap, cmp);
                double d = top.Key;
                int cur = top.Value;
                if (d > dist[cur]) continue;              // 过期条目
                if (d > maxDistance) continue;

                int cx = cur % map.Width;
                int cz = cur / map.Width;

                for (int k = 0; k < 4; k++)
                {
                    int nx = cx + dx[k];
                    int nz = cz + dz[k];
                    if (nx < 0 || nz < 0 || nx >= map.Width || nz >= map.Height) continue;

                    int ni = nz * map.Width + nx;
                    double step = (ni < map.MoveCost.Length) ? map.MoveCost[ni] : 1.0;
                    if (step <= 0) continue;              // 不可通行

                    double nd = d + step;
                    if (nd > maxDistance) continue;
                    if (nd < dist[ni] - 1e-9)
                    {
                        dist[ni] = nd;
                        HeapPush(heap, cmp, new KeyValuePair<double, int>(nd, ni));
                    }
                }
            }

            return dist;
        }

        /// <summary>
        /// 找一块最合适的种植区。
        /// </summary>
        /// <param name="map">地图</param>
        /// <param name="distField">距离场（ComputeDistanceField 的结果）</param>
        /// <param name="desiredCells">想要多少格；0 = 只挑一块最好的</param>
        /// <param name="opts">可调参数</param>
        /// <returns></returns>
        public static FarmPlot FindBestPlot(
            PassabilityMap map,
            double[] distField,
            int desiredCells,
            SiteSelectionOptions opts)
        {
            if (opts == null) opts = new SiteSelectionOptions();
            if (map == null || distField == null) return null;

            int minSide = Math.Max(1, opts.MinSide);
            int maxSide = Math.Max(minSide, opts.MaxSide);

            // 集成图（integral image），快速算矩形内的可种格数量与肥力和
            int w = map.Width, h = map.Height;
            var integralArable = new int[(w + 1) * (h + 1)];
            var integralFert = new double[(w + 1) * (h + 1)];
            var integralDist = new double[(w + 1) * (h + 1)];

            for (int z = 0; z < h; z++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = map.Index(x, z);
                    int iA = map.IsArable(x, z) ? 1 : 0;
                    double f = (i < map.Fertility.Length) ? map.Fertility[i] : 0;
                    double d = distField[i];

                    int iw = (z + 1) * (w + 1) + (x + 1);
                    integralArable[iw] = iA
                        + integralArable[z * (w + 1) + (x + 1)]
                        + integralArable[(z + 1) * (w + 1) + x]
                        - integralArable[z * (w + 1) + x];
                    integralFert[iw] = f
                        + integralFert[z * (w + 1) + (x + 1)]
                        + integralFert[(z + 1) * (w + 1) + x]
                        - integralFert[z * (w + 1) + x];
                    integralDist[iw] = (double.IsInfinity(d) || d > opts.HardMaxDistance ? opts.HardMaxDistance : d)
                        + integralDist[z * (w + 1) + (x + 1)]
                        + integralDist[(z + 1) * (w + 1) + x]
                        - integralDist[z * (w + 1) + x];
                }
            }

            FarmPlot best = null;
            double bestScore = double.NegativeInfinity;

            // 目标边长：优先最接近 desiredCells 的正方形
            int targetSide = minSide;
            if (desiredCells > 0)
            {
                targetSide = (int)Math.Ceiling(Math.Sqrt(desiredCells));
                if (targetSide < minSide) targetSide = minSide;
                if (targetSide > maxSide) targetSide = maxSide;
            }

            // 只在若干候选边长上搜索（全搜 O(W*H*side^2) 太慢，实际也不需要）
            var sides = new List<int>();
            for (int s = minSide; s <= maxSide; s++)
            {
                if (s == targetSide || s == minSide || s == maxSide) sides.Add(s);
            }
            if (!sides.Contains(targetSide)) sides.Add(targetSide);

            for (int si = 0; si < sides.Count; si++)
            {
                int side = sides[si];
                if (side > w || side > h) continue;

                for (int z = 0; z + side <= h; z++)
                {
                    for (int x = 0; x + side <= w; x++)
                    {
                        int arable = RectSum(integralArable, w, x, z, side, side);
                        int area = side * side;
                        if (arable <= 0) continue;

                        double nonArableRatio = 1.0 - ((double)arable / area);
                        if (nonArableRatio > opts.MaxNonArableRatio) continue;

                        double fertSum = RectSum(integralFert, w, x, z, side, side);
                        double distSum = RectSum(integralDist, w, x, z, side, side);

                        // 只按**可种格**平均距离，因为不可种格没人去
                        double avgDist = arable > 0 ? distSum / area : opts.HardMaxDistance;
                        double avgFert = arable > 0 ? fertSum / arable : 0;

                        if (avgDist > opts.HardMaxDistance) continue;

                        double score = Score(arable, avgDist, avgFert, opts);

                        if (score > bestScore)
                        {
                            bestScore = score;
                            best = new FarmPlot
                            {
                                X = x,
                                Z = z,
                                Width = side,
                                Height = side,
                                AvgDistance = avgDist,
                                AvgFertility = avgFert,
                                Score = score,
                            };
                        }
                    }
                }
            }

            return best;
        }

        /// <summary>
        /// 评分函数。**单独提出来**，因为它是整个选址逻辑里唯一"有主观判断"的部分，
        /// 应该能被单独测试和调参。
        /// </summary>
        public static double Score(int arableCells, double avgDistance, double avgFertility, SiteSelectionOptions opts)
        {
            if (opts == null) opts = new SiteSelectionOptions();

            // 面积收益：可种格数是主项
            double score = arableCells;

            // 肥力加成：肥力 1.0 给 +50%，0.5 给 +0%
            score += arableCells * (avgFertility - 0.5) * 1.0;

            // 距离惩罚：超出"通勤可接受"的部分线性罚
            double excess = avgDistance - opts.ComfortableDistance;
            if (excess > 0) score -= excess * opts.DistancePenaltyPerCell;

            return score;
        }

        private static int RectSum(int[] integral, int w, int x, int z, int rw, int rh)
        {
            int stride = w + 1;
            int x2 = x + rw, z2 = z + rh;
            return integral[z2 * stride + x2]
                 - integral[z * stride + x2]
                 - integral[z2 * stride + x]
                 + integral[z * stride + x];
        }

        private static double RectSum(double[] integral, int w, int x, int z, int rw, int rh)
        {
            int stride = w + 1;
            int x2 = x + rw, z2 = z + rh;
            return integral[z2 * stride + x2]
                 - integral[z * stride + x2]
                 - integral[z2 * stride + x]
                 + integral[z * stride + x];
        }

        // --- 基地中心与"复用已有种植区" ---

        /// <summary>
        /// 按用户定的**兜底顺序**决定基地中心：
        ///   ① 居住区（Home area）的质心
        ///   ② 没有居住区 → 离得最近的贮藏区（stockpile）
        ///   ③ 都没有 → 以靠近地图中心为准
        ///
        /// 用哪个会写进 <see cref="ResolvedBaseCenter.Kind"/>，进遥测。
        /// </summary>
        public static ResolvedBaseCenter ResolveBaseCenter(
            PassabilityMap map,
            IList<CenterCandidate> candidates)
        {
            var result = new ResolvedBaseCenter();

            if (candidates != null && map != null && map.Width > 0)
            {
                // ① 居住区
                ResolvedBaseCenter home = CentroidOfKind(map, candidates, BaseCenterKind.HomeArea);
                if (home != null)
                {
                    home.Note = "使用居住区质心";
                    return home;
                }

                // ② 贮藏区：取"离地图中心最近"的那个（用户原话："找离得最近的贮藏区"）
                ResolvedBaseCenter stock = PickNearestStockpile(map, candidates);
                if (stock != null)
                {
                    stock.Note = "无居住区，退到最近的贮藏区";
                    return stock;
                }
            }

            // ③ 退化到地图中心
            if (map != null && map.Width > 0 && map.Height > 0)
            {
                result.Kind = BaseCenterKind.MapCenterBias;
                result.X = map.Width / 2;
                result.Z = map.Height / 2;
                result.Note = "既无居住区也无贮藏区，退到地图中心";
                return result;
            }

            result.Kind = BaseCenterKind.None;
            result.Note = "无法确定基地中心";
            return result;
        }

        /// <summary>找"可用空地最多"的已有种植区。有它就优先复用，不去新开地。</summary>
        public static ZonePatch FindBestExistingZone(
            PassabilityMap map,
            double[] distField,
            IList<ZonePatch> zones,
            SiteSelectionOptions opts)
        {
            if (opts == null) opts = new SiteSelectionOptions();
            if (zones == null) return null;

            ZonePatch best = null;
            double bestScore = double.NegativeInfinity;

            for (int i = 0; i < zones.Count; i++)
            {
                ZonePatch z = zones[i];
                if (z == null || z.FreeCells == null || z.FreeCells.Count <= 0) continue;

                double fertSum = 0;
                double distSum = 0;
                int counted = 0;

                for (int c = 0; c < z.FreeCells.Count; c++)
                {
                    int idx = z.FreeCells[c];
                    if (map != null && idx >= 0 && idx < map.Fertility.Length) fertSum += map.Fertility[idx];
                    if (distField != null && idx >= 0 && idx < distField.Length)
                    {
                        double d = distField[idx];
                        distSum += (double.IsInfinity(d) || d > opts.HardMaxDistance) ? opts.HardMaxDistance : d;
                    }
                    counted++;
                }

                z.AvgFertility = counted > 0 ? fertSum / counted : 0;
                z.AvgDistance = counted > 0 ? distSum / counted : opts.HardMaxDistance;
                z.Score = Score(counted, z.AvgDistance, z.AvgFertility, opts);

                if (z.Score > bestScore)
                {
                    bestScore = z.Score;
                    best = z;
                }
            }

            return best;
        }

        private static ResolvedBaseCenter CentroidOfKind(
            PassabilityMap map,
            IList<CenterCandidate> candidates,
            BaseCenterKind kind)
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                CenterCandidate c = candidates[i];
                if (c == null || c.Kind != kind) continue;

                // 游戏层若直接给了中心，优先用它
                if (c.CenterIndex >= 0)
                {
                    return new ResolvedBaseCenter
                    {
                        Kind = kind,
                        X = c.CenterIndex % map.Width,
                        Z = c.CenterIndex / map.Width,
                    };
                }

                int cx, cz;
                if (CentroidOf(map.Width, c.Cells, out cx, out cz))
                {
                    return new ResolvedBaseCenter { Kind = kind, X = cx, Z = cz };
                }
            }
            return null;
        }

        private static ResolvedBaseCenter PickNearestStockpile(
            PassabilityMap map,
            IList<CenterCandidate> candidates)
        {
            int mapCx = map != null ? map.Width / 2 : 0;
            int mapCz = map != null ? map.Height / 2 : 0;

            ResolvedBaseCenter best = null;
            double bestD = double.MaxValue;

            for (int i = 0; i < candidates.Count; i++)
            {
                CenterCandidate c = candidates[i];
                if (c == null || c.Kind != BaseCenterKind.Stockpile) continue;
                if (c.CenterIndex < 0 || map == null || map.Width <= 0) continue;

                int x = c.CenterIndex % map.Width;
                int z = c.CenterIndex / map.Width;
                double d = Math.Sqrt((double)(x - mapCx) * (x - mapCx) + (double)(z - mapCz) * (z - mapCz));

                if (d < bestD)
                {
                    bestD = d;
                    best = new ResolvedBaseCenter { Kind = BaseCenterKind.Stockpile, X = x, Z = z };
                }
            }
            return best;
        }

        /// <summary>
        /// 把一组格子（index）求质心。**需要地图宽度才能反解坐标**，所以单独暴露，
        /// 让游戏层和测试都能用同一份实现。
        /// </summary>
        public static bool CentroidOf(int mapWidth, IList<int> cells, out int cx, out int cz)
        {
            cx = 0;
            cz = 0;
            if (mapWidth <= 0 || cells == null || cells.Count == 0) return false;

            long sx = 0, sz = 0;
            for (int i = 0; i < cells.Count; i++)
            {
                sx += cells[i] % mapWidth;
                sz += cells[i] / mapWidth;
            }
            cx = (int)Math.Round((double)sx / cells.Count);
            cz = (int)Math.Round((double)sz / cells.Count);
            return true;
        }

        // --- 最小二叉堆（避免依赖 System.Collections.Generic.PriorityQueue，那是 .NET 6+） ---
        private static void HeapPush(
            List<KeyValuePair<double, int>> heap,
            Comparer<KeyValuePair<double, int>> cmp,
            KeyValuePair<double, int> item)
        {
            heap.Add(item);
            int i = heap.Count - 1;
            while (i > 0)
            {
                int parent = (i - 1) / 2;
                if (cmp.Compare(heap[parent], heap[i]) <= 0) break;
                var tmp = heap[parent];
                heap[parent] = heap[i];
                heap[i] = tmp;
                i = parent;
            }
        }

        private static KeyValuePair<double, int> HeapPop(
            List<KeyValuePair<double, int>> heap,
            Comparer<KeyValuePair<double, int>> cmp)
        {
            var root = heap[0];
            int last = heap.Count - 1;
            heap[0] = heap[last];
            heap.RemoveAt(last);

            int i = 0;
            while (true)
            {
                int l = 2 * i + 1, r = 2 * i + 2, smallest = i;
                if (l < heap.Count && cmp.Compare(heap[l], heap[smallest]) < 0) smallest = l;
                if (r < heap.Count && cmp.Compare(heap[r], heap[smallest]) < 0) smallest = r;
                if (smallest == i) break;
                var tmp = heap[smallest];
                heap[smallest] = heap[i];
                heap[i] = tmp;
                i = smallest;
            }
            return root;
        }
    }
}
