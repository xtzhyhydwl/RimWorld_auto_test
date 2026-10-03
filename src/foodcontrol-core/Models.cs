using System;
using System.Collections.Generic;

namespace FoodControl.Core
{
    /// <summary>
    /// 预测器用到的标定系数。
    ///
    /// 设计要点：**不硬编码 RimWorld 的公式**，而是用游戏内适配层实测/调用公开 API 得到的值填入。
    /// 这样 Core 不依赖 Assembly-CSharp，可以离线（毫秒级）测试。
    ///
    /// ⚠ 本文件里的注释涉及游戏机制的地方，都来自 `docs/游戏API符号确证.md` 的**实测确证**，
    ///   不是凭记忆写的。曾经验证过：`PlantGrowthRateCalculator`、`ZoneDefOf.Growing`、
    ///   `TerrainAffordanceDefOf.GrowSoil` 这些"看起来很合理"的名字**在 1.6 根本不存在**。
    /// </summary>
    public sealed class Calibration
    {
        /// <summary>
        /// 成熟作物每格能提供的 nutrition。由「每株产量 × 营养值」实测填入。
        /// </summary>
        public double NutritionPerCellAtMaturity = 18.0;

        /// <summary>
        /// 可收获的 growth 阈值。
        ///
        /// ⚠ **这是 0.65，不是 1.0。** 游戏的 `Plant.HarvestableNow` 用的是
        /// `def.plant.harvestMinGrowth`（ctor 默认 0.65）。
        /// 按 1.0 算会**系统性高估成熟时间**，进而高估缺口、过度种植。
        /// </summary>
        public double HarvestMinGrowth = 0.65;

        /// <summary>产出摊开的预测天数窗口。</summary>
        public int HorizonDays = 15;

        public Calibration Clone()
        {
            return (Calibration)MemberwiseClone();
        }
    }

    /// <summary>一块在种的作物（按「同作物 + 同生长进度」聚合，不是逐格）。</summary>
    public sealed class PlantGroup
    {
        public string PlantDefName = "";
        public int CellCount;

        /// <summary>当前 growth（0~1）。注意"可收获"发生在 <see cref="Calibration.HarvestMinGrowth"/>，不是 1.0。</summary>
        public double Growth;

        /// <summary>
        /// 当前的生长速率。
        ///
        /// 对应游戏的 `Plant.GrowthRate`（**public，可读**），
        /// 由公开的 `PlantUtility.GrowthRateFactorFor_Light / _Temperature / _Fertility` 相乘得到。
        /// 游戏层把它算好填进来，Core 不去猜游戏公式。
        ///
        /// 注意：**夜里它是 0**，但原因不是光照因子，而是 `Plant.Resting`
        /// （一天中约 0.25–0.8 的时段之外）让 `GrowthPerTick` 直接返回 0。
        /// 所以"每晚不长"这件事通过 GrowthRate=0 自然反映进来，Core 不需要额外的光周期模型。
        /// </summary>
        public double GrowthRate = 1.0;

        /// <summary>
        /// 该作物从 0 长到 1 需要的游戏天数，对应 `ThingDef.plant.growDays`。
        ///
        /// 水稻实测 **growDays = 3**（游戏内实测，2026-10-03）。
        /// ⚠ 这里**不要硬编码具体作物的值** —— 由游戏层运行时从 `def.plant.growDays` 读取填入。
        /// 游戏里的公式是 `GrowthPerTick = GrowthRate / (60000 × growDays)`。
        /// </summary>
        public double GrowDays = 3.0;

        public PlantGroup() { }

        public PlantGroup(string defName, int cells, double growth, double growthRate, double growDays)
        {
            PlantDefName = defName;
            CellCount = cells;
            Growth = growth;
            GrowthRate = growthRate;
            GrowDays = growDays;
        }

        /// <summary>距离「可收获」还差多少 growth。</summary>
        public double RemainingToHarvest(double harvestMinGrowth)
        {
            double r = harvestMinGrowth - Growth;
            return r > 0 ? r : 0;
        }
    }

    /// <summary>殖民地在某一时刻的食物状态快照（由游戏内适配层读取后填入）。</summary>
    public sealed class FoodState
    {
        /// <summary>当前食物总 nutrition。</summary>
        public double Stock;

        /// <summary>当前殖民者每天消耗的 nutrition 总和。</summary>
        public double BurnRatePerDay = 1.0;

        public List<PlantGroup> Planted = new List<PlantGroup>();

        /// <summary>
        /// 距离基地可接受范围内、当前**未被占用**的可种植格数。
        /// 这是决策的硬约束：缺口再大，没地也种不了（那时应该报 NeedsMoreLand）。
        /// </summary>
        public int ArableCellsAvailable;

        /// <summary>当前季节 / 时间的诊断信息，只用于遥测。</summary>
        public string Season = "";
        public int Tick;

        public Calibration Calibration = new Calibration();

        public double ReserveDays
        {
            get { return BurnRatePerDay > 0 ? Stock / BurnRatePerDay : double.MaxValue; }
        }
    }

    public enum FoodActionKind
    {
        DoNothing = 0,
        PlantCrop = 1,
    }

    /// <summary>决策层输出的动作。</summary>
    public sealed class FoodAction
    {
        public FoodActionKind Kind = FoodActionKind.DoNothing;

        /// <summary>种什么（Kind == PlantCrop 时有效）</summary>
        public string PlantDefName = "";
        /// <summary>种多少格</summary>
        public int Cells;

        /// <summary>人能读的理由，进遥测与日志</summary>
        public string Reason = "";

        public static FoodAction Nothing(string reason)
        {
            return new FoodAction { Kind = FoodActionKind.DoNothing, Reason = reason };
        }

        public static FoodAction Plant(string defName, int cells, string reason)
        {
            return new FoodAction
            {
                Kind = FoodActionKind.PlantCrop,
                PlantDefName = defName,
                Cells = cells,
                Reason = reason,
            };
        }
    }

    /// <summary>预测结果。projectedReserve[d] = 从今天算起第 d 天结束时的预计存量（nutrition）。</summary>
    public sealed class ForecastResult
    {
        /// <summary>索引 0 恒等于今天的存量。长度 = HorizonDays + 1。</summary>
        public double[] ProjectedReserve = new double[0];

        /// <summary>每天新增的产出 nutrition（索引 0 无意义）。</summary>
        public double[] DailySupply = new double[0];

        public double MinReserve = double.MaxValue;
        /// <summary>第一次跌破低水位的天数；没跌破则为 -1。</summary>
        public int FirstShortfallDay = -1;
    }
}
