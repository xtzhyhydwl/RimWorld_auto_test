using System;
using System.Collections.Generic;

namespace FoodControl.Core
{
    /// <summary>
    /// 预测器用到的标定系数。
    ///
    /// 设计要点：**这些数字不来自对 RimWorld 公式的硬编码，而是由游戏内适配层实测后填入。**
    /// 这样 Core 不依赖 Assembly-CSharp，可以离线（毫秒级）测试，
    /// 而且游戏公式变了也只是重新标定，不用改预测器。
    /// </summary>
    public sealed class Calibration
    {
        /// <summary>成熟作物每格能提供的 nutrition。由「每株产量 × 营养值」实测填入。</summary>
        public double NutritionPerCellAtMaturity = 18.0;

        /// <summary>
        /// 每天的有效生长小时数（受光周期限制）。实测晚上生长速率为 0。
        /// 用于把「满速成熟天数」折算成当前季节的实际成熟天数。
        /// </summary>
        public double EffectiveGrowthHoursPerDay = 12.0;

        /// <summary>
        /// 光照充足（24 小时有效生长）时的有效生长小时数，作为归一化基准。
        /// 默认 24 = 室内水培 / 无光周期限制的情形。
        /// </summary>
        public double FullSpeedGrowthHoursPerDay = 24.0;

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
        /// <summary>0~1</summary>
        public double Growth;
        /// <summary>土壤肥力系数，1.0 = 普通土壤</summary>
        public double Fertility = 1.0;
        /// <summary>该作物从 0 长到 1 需要的游戏天数（在肥力 1.0、光照充足条件下）</summary>
        public double DaysToMatureAtFullSpeed = 6.0;

        public PlantGroup() { }

        public PlantGroup(string defName, int cells, double growth, double fertility, double daysToMature)
        {
            PlantDefName = defName;
            CellCount = cells;
            Growth = growth;
            Fertility = fertility;
            DaysToMatureAtFullSpeed = daysToMature;
        }

        public double RemainingGrowth
        {
            get
            {
                double r = 1.0 - Growth;
                return r > 0 ? r : 0;
            }
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
