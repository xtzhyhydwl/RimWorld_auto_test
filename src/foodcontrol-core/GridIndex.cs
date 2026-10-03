using System;

namespace FoodControl.Core
{
    /// <summary>
    /// **二维网格的索引/边界** —— 一个值类型，而不是每个地图类各写一遍。
    ///
    /// ## 为什么单独提出来（DRY）
    ///
    /// 在提出来之前，`InBounds` + `Index` 这对方法在 Core 里被**抄了三份**：
    ///   - `PassabilityMap`（SiteSelection.cs）
    ///   - `BuildabilityMap`（BuildSitePlanner.cs）
    ///   - `PlacementGrid`（TemplatePlacer.cs）
    /// 三份实现**逐字相同**（`x >= 0 && z >= 0 && x < Width && z < Height`
    /// 和 `z * Width + x`）。
    ///
    /// 这类重复的危险不在于"多打了字"，而在于**它们会悄悄分叉**：
    /// 一旦有人在一个类里把索引改成列优先（`x * Height + z`）去查一个行优先的数组，
    /// 得到的是**完全错乱但不会崩**的结果 —— 这正是本项目踩过好几次的
    /// "静默错位"型 bug（坐标系混用那次就是这么来的）。
    /// 提成一个类型之后，坐标系**只有一处定义**。
    ///
    /// ## 为什么用 `struct`
    ///
    /// 它会被放进每帧/每次规划都要用的地图类里。`readonly struct` 不会引入堆分配，
    /// 也天然不可变（地图尺寸定了就不该再改）。C# 7.3 支持 `readonly struct`，
    /// 而 Core 的语言级别就钉在 7.3（见 `scripts/build-core.mjs`）。
    /// </summary>
    public readonly struct GridIndex
    {
        public readonly int Width;
        public readonly int Height;

        public GridIndex(int width, int height)
        {
            Width = width < 0 ? 0 : width;
            Height = height < 0 ? 0 : height;
        }

        public int CellCount
        {
            get { return Width * Height; }
        }

        public bool IsEmpty
        {
            get { return Width <= 0 || Height <= 0; }
        }

        public bool InBounds(int x, int z)
        {
            return x >= 0 && z >= 0 && x < Width && z < Height;
        }

        /// <summary>行优先索引（`z * Width + x`）。**越界时返回 -1**（不是乱算一个数）。</summary>
        public int Index(int x, int z)
        {
            if (!InBounds(x, z)) return -1;
            return z * Width + x;
        }

        /// <summary>索引 → 坐标。越界返回 false（调用方不必自己反算）。</summary>
        public bool CoordAt(int index, out int x, out int z)
        {
            if (index < 0 || index >= CellCount)
            {
                x = -1;
                z = -1;
                return false;
            }
            x = index % Width;
            z = index / Width;
            return true;
        }

        /// <summary>
        /// 带长度校验的取值：`index` 越界或数组太短都返回 `fallback`。
        ///
        /// 这是"**读不到就说读不到**"的落地写法：老代码里常见的
        /// `i >= 0 &amp;&amp; i &lt; arr.Length &amp;&amp; arr[i]` 抄多了总会漏掉一段。
        /// </summary>
        public T At<T>(T[] values, int x, int z, T fallback)
        {
            int i = Index(x, z);
            if (values == null || i < 0 || i >= values.Length) return fallback;
            return values[i];
        }

        public override string ToString()
        {
            return Width + "x" + Height;
        }
    }
}
