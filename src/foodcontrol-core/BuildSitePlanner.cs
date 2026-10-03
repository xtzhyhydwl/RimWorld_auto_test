using System;
using System.Collections.Generic;

namespace FoodControl.Core
{
    /// <summary>
    /// 住处选址用的地图数据（由游戏层填充）。
    ///
    /// 和 `PassabilityMap` 的区别：那个关心"能不能走、多肥"，这个关心
    /// **"能不能盖房子"** —— 地表能不能承重、上面有没有挡路的东西。
    /// </summary>
    public sealed class BuildabilityMap
    {
        public int Width;
        public int Height;

        /// <summary>每格能不能盖（地表可承重 + 没有阻挡物）。由游戏层判定。</summary>
        public bool[] Buildable = new bool[0];

        /// <summary>每格的通行代价（&lt;=0 表示不可通行），用来算"离基地多远"</summary>
        public double[] MoveCost = new double[0];

        public bool InBounds(int x, int z)
        {
            return x >= 0 && z >= 0 && x < Width && z < Height;
        }

        public int Index(int x, int z)
        {
            return z * Width + x;
        }

        public bool IsBuildable(int x, int z)
        {
            if (!InBounds(x, z)) return false;
            int i = Index(x, z);
            return i >= 0 && i < Buildable.Length && Buildable[i];
        }
    }

    /// <summary>一间房的户型（纯几何，不含材质）。</summary>
    public sealed class BuildingPlan
    {
        /// <summary>外墙范围（含墙本身）</summary>
        public int X, Z, Size;

        /// <summary>墙的位置（矩形四边，含四角）</summary>
        public List<int> WallCells = new List<int>();

        /// <summary>门的位置（墙上的一格）</summary>
        public List<int> DoorCells = new List<int>();

        /// <summary>地板的位置（内部，不含墙和门）</summary>
        public List<int> FloorCells = new List<int>();

        /// <summary>内部空地（可以用来放床/桌子）</summary>
        public List<int> InteriorCells = new List<int>();

        /// <summary>人站在门外的那一格（门朝外方向的相邻格）</summary>
        public int DoorOutsideCell = -1;

        /// <summary>门朝向（0=北 +z, 1=东 +x, 2=南 -z, 3=西 -x）</summary>
        public int DoorSide = 2;

        /// <summary>到基地的平均通行距离（评分用，也进遥测）</summary>
        public double AvgDistanceToBase;

        public double Score;

        public int WallCount
        {
            get { return WallCells.Count; }
        }

        public int FloorCount
        {
            get { return FloorCells.Count; }
        }

        public override string ToString()
        {
            return string.Format("房({0},{1}) {2}x{2} 墙{3} 门{4} 地板{5} 基地距{6:F0} 分{7:F1}",
                X, Z, Size, WallCount, DoorCells.Count, FloorCount, AvgDistanceToBase, Score);
        }
    }

    public sealed class BuildSiteOptions
    {
        /// <summary>
        /// 房子边长（含墙）。**默认 13** —— 用户明确要求：
        /// 「最万能的建筑……13 乘 13，用墙去画一个 13 乘 13 的正方形」。
        /// 13×13 → 外墙 48 格、内部 11×11 = 121 格。
        /// </summary>
        public int Size = 13;

        /// <summary>到基地中心的最大通行距离</summary>
        public double MaxDistanceFromBase = 60.0;

        /// <summary>这个距离内不惩罚</summary>
        public double ComfortableDistance = 20.0;

        /// <summary>超出可接受距离后每格的惩罚（房子比田重要，所以罚得比田重一些）</summary>
        public double DistancePenaltyPerCell = 4.0;

        /// <summary>允许地基范围内最多有多少格不能盖（挡路的树/石头）</summary>
        public double MaxBlockedRatio = 0.10;

        /// <summary>搜索步长（越小越精细越慢；2 表示每 2 格试一个位置）</summary>
        public int SearchStep = 1;

        /// <summary>
        /// 门开在哪一侧：0=北(+z) 1=东(+x) 2=南(-z) 3=西(-x)。
        /// **只在 `DoorSides` 为空时用作单门兜底。**
        /// </summary>
        public int DoorSide = 2;

        /// <summary>
        /// **要开门的边**（0=北 1=东 2=南 3=西）。默认**四条边都开**。
        ///
        /// 为什么默认四边：
        /// 用户看过只有一扇门的实测后明确说
        /// 「**只有一个门，实际开门方向是封闭的，所以还是多留几道门好**」——
        /// 单门事实上把这栋房子变成了"只有一个方向能进"，
        /// 一旦那个方向被堵/被敌人占据，屋里的人就出不来。四边各一扇才对称、才"万能"。
        /// </summary>
        public int[] DoorSides = new int[] { 0, 1, 2, 3 };

        /// <summary>
        /// 门朝外那一格必须在**地基之外**，而且要是可通行的 ——
        /// 否则小人进不去（全封闭的盒子等于没门）。
        /// </summary>
        public bool RequireOutsideAccess = true;
    }

    /// <summary>
    /// **住处选址与户型**。纯算法，不依赖游戏，可离线测试。
    ///
    /// ## 为什么不复用食物域那套选址
    ///
    /// 种田在乎"连片、肥沃"；盖房在乎**"地基平整且没挡路的东西"** ——
    /// 树、大石头、深水都盖不了。所以这里用 `BuildabilityMap`（可盖性）
    /// 而不是 `PassabilityMap`（通行/肥力）。
    ///
    /// 复用的部分是**距离场**：`SiteSelection.ComputeDistanceField` 算出的
    /// "到基地的通行代价"，这里拿它当评分的一维。
    ///
    /// ## 门是必须的（不是可选项）
    ///
    /// 一个四边全封死的方块房，小人**进不去也出不来**。
    /// 所以户型生成时必须留一格门，而且**门外那一格要在房子外面**（薄墙的话
    /// 门就是墙上的缺口，门外的格子天然在墙外）。
    /// `RequireOutsideAccess` 打开时会检查门外那格可通行。
    /// </summary>
    public static class BuildSitePlanner
    {
        /// <summary>
        /// 找最好的建房位置，并生成户型。
        /// </summary>
        /// <param name="grid">可盖性地图</param>
        /// <param name="distField">到基地的通行距离场（`SiteSelection.ComputeDistanceField`）</param>
        /// <param name="opts">参数</param>
        public static BuildingPlan Plan(
            BuildabilityMap grid, double[] distField, BuildSiteOptions opts)
        {
            if (opts == null) opts = new BuildSiteOptions();
            if (grid == null || distField == null) return null;

            int size = opts.Size;
            if (size < 3) size = 3;
            if (size > grid.Width || size > grid.Height) return null;

            int step = opts.SearchStep > 0 ? opts.SearchStep : 1;
            int maxBlocked = (int)Math.Floor(size * size * opts.MaxBlockedRatio);

            BuildingPlan best = null;
            double bestScore = double.NegativeInfinity;

            for (int z = 0; z + size <= grid.Height; z += step)
            {
                for (int x = 0; x + size <= grid.Width; x += step)
                {
                    // ① 地基范围内统计：可盖格数、平均基地距离
                    int blocked = 0;
                    double distSum = 0;
                    bool anyUnreachable = false;

                    for (int dz = 0; dz < size; dz++)
                    {
                        for (int dx = 0; dx < size; dx++)
                        {
                            int cx = x + dx, cz = z + dz;
                            if (!grid.IsBuildable(cx, cz)) blocked++;

                            double d = distField[grid.Index(cx, cz)];
                            if (double.IsInfinity(d)) anyUnreachable = true;
                            distSum += d;
                        }
                    }

                    if (blocked > maxBlocked) continue;         // 挡路的太多
                    if (anyUnreachable) continue;               // 走不到

                    double avgDist = distSum / (size * size);
                    if (avgDist > opts.MaxDistanceFromBase) continue;

                    // ② 生成户型（含门），顺便检查门外可通行
                    BuildingPlan plan = BuildPlan(grid, distField, x, z, size, opts);
                    if (plan == null) continue;

                    // ③ 评分：离基地近 + 挡路的少
                    double score = -blocked * 10.0;
                    double excess = avgDist - opts.ComfortableDistance;
                    if (excess > 0) score -= excess * opts.DistancePenaltyPerCell;
                    score += size * size * 0.1;                 // 大一点略好（同尺寸下无差别）

                    plan.AvgDistanceToBase = avgDist;
                    plan.Score = score;

                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = plan;
                    }
                }
            }

            return best;
        }

        /// <summary>
        /// 在指定位置生成一间方形房的户型（含门与地板）。**纯几何**。
        ///
        /// 返回 null 表示这个位置不可行（比如门外那格走不通）。
        /// </summary>
        public static BuildingPlan BuildPlan(
            BuildabilityMap grid, double[] distField, int x, int z, int size, BuildSiteOptions opts)
        {
            if (opts == null) opts = new BuildSiteOptions();
            if (size < 3) return null;
            if (grid == null) return null;
            if (x < 0 || z < 0 || x + size > grid.Width || z + size > grid.Height) return null;

            var plan = new BuildingPlan { X = x, Z = z, Size = size, DoorSide = opts.DoorSide };

            int maxX = x + size - 1;
            int maxZ = z + size - 1;

            // ① 门：**每条要开的边的中点各一扇**
            //
            // ⚠ 13 是奇数，所以 size/2 = 6 正好是"从起点数第 7 格"= 正中那格。
            //   四条边的中点：北 (x+6, maxZ)、东 (maxX, z+6)、南 (x+6, z)、西 (x, z+6)。
            var doorIdx = new HashSet<int>();
            int[] sides = (opts.DoorSides != null && opts.DoorSides.Length > 0)
                ? opts.DoorSides
                : new int[] { opts.DoorSide };

            for (int i = 0; i < sides.Length; i++)
            {
                int dx, dz;
                DoorCellOnSide(x, z, maxX, maxZ, size, sides[i], out dx, out dz);
                if (dx < x || dx > maxX || dz < z || dz > maxZ) continue;   // 越界（尺寸太小）
                int idx = grid.Index(dx, dz);
                if (!doorIdx.Contains(idx))
                {
                    doorIdx.Add(idx);
                    plan.DoorCells.Add(idx);
                }
            }

            if (plan.DoorCells.Count == 0) return null;      // 一扇门都没有 = 进不去的盒子

            // ② 墙：矩形四边，去掉门
            for (int cx = x; cx <= maxX; cx++)
            {
                for (int cz = z; cz <= maxZ; cz++)
                {
                    bool onEdge = (cx == x || cx == maxX || cz == z || cz == maxZ);
                    if (!onEdge) continue;

                    int idx = grid.Index(cx, cz);
                    if (doorIdx.Contains(idx)) continue;         // 已经是门
                    plan.WallCells.Add(idx);
                }
            }

            // 墙不能是 0 个（尺寸太小时可能出现）
            if (plan.WallCells.Count == 0) return null;

            // ③ 内部：地板 + 空地（同一批格，地板铺完后这些格还能放家具）
            for (int cx = x + 1; cx < maxX; cx++)
            {
                for (int cz = z + 1; cz < maxZ; cz++)
                {
                    int idx = grid.Index(cx, cz);
                    plan.FloorCells.Add(idx);
                    plan.InteriorCells.Add(idx);
                }
            }

            // ④ 门外那一格：**每一扇门**都要检查"门外能走"。
            //
            //    单门时只查一个方向就够；四门时**四个方向都要查** ——
            //    否则可能出现"朝北那扇门外面是山"，那扇门就是死门。
            //    这里仍然只把**第一扇门**记进 `DoorOutsideCell`（给遥测/调试看），
            //    但**方向性检查对每扇门都做**；任一门走不通 → 这个位置不可行。
            for (int i = 0; i < plan.DoorCells.Count; i++)
            {
                int idx = plan.DoorCells[i];
                int dcx = idx % grid.Width;
                int dcz = idx / grid.Width;

                int side = SideOfDoor(dcx, dcz, x, z, maxX, maxZ);
                int outX = dcx, outZ = dcz;
                switch (side)
                {
                    case 0: outZ = maxZ + 1; break;
                    case 1: outX = maxX + 1; break;
                    case 3: outX = x - 1; break;
                    default: outZ = z - 1; break;
                }

                if (i == 0)
                {
                    plan.DoorOutsideCell = grid.InBounds(outX, outZ) ? grid.Index(outX, outZ) : -1;
                }

                if (opts.RequireOutsideAccess)
                {
                    if (!grid.InBounds(outX, outZ)) return null;         // 门外越界 → 不行
                    int oi = grid.Index(outX, outZ);
                    double cost = (oi >= 0 && oi < grid.MoveCost.Length) ? grid.MoveCost[oi] : 0;
                    if (cost <= 0) return null;                          // 门外走不通 → 不行
                }
            }

            return plan;
        }

        /// <summary>算某条边的中点格（0=北 1=东 2=南 3=西）。</summary>
        public static void DoorCellOnSide(
            int x, int z, int maxX, int maxZ, int size, int side, out int dx, out int dz)
        {
            int mid = size / 2;          // 13 → 6，正好是正中间那格（0-based）
            switch (side)
            {
                case 0: dx = x + mid; dz = maxZ; break;     // 北 (+z)
                case 1: dx = maxX; dz = z + mid; break;     // 东 (+x)
                case 3: dx = x; dz = z + mid; break;        // 西 (-x)
                default: dx = x + mid; dz = z; break;       // 南 (-z)
            }
        }

        /// <summary>这一格在矩形四边里属于哪条边（给"门外那一格"用）。</summary>
        private static int SideOfDoor(int cx, int cz, int x, int z, int maxX, int maxZ)
        {
            if (cz == maxZ) return 0;    // 北
            if (cx == maxX) return 1;    // 东
            if (cz == z) return 2;       // 南
            return 3;                    // 西
        }
    }
}
