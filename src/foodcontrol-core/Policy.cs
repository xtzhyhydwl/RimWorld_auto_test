using System;

namespace FoodControl.Core
{
    /// <summary>可调策略参数。将来 LLM 慢环要改的就是这一层（见设计文档 §三）。</summary>
    public sealed class PolicyConfig
    {
        /// <summary>希望常备多少天口粮</summary>
        public double ReserveDaysTarget = 10.0;
        /// <summary>低于这个天数开始补</summary>
        public double ReserveDaysLow = 7.0;
        /// <summary>高于这个天数停止扩张</summary>
        public double ReserveDaysHigh = 20.0;

        /// <summary>默认种什么</summary>
        public string PreferredPlantDefName = "Plant_Rice";

        /// <summary>单次决策最多新开多少格（防止一次冲动铺满整张图）</summary>
        public int MaxCellsPerDecision = 120;

        /// <summary>单次决策最少种多少格（太小的地块不值得单独开一块）</summary>
        public int MinCellsPerDecision = 4;
    }

    /// <summary>
    /// 决策层：目标带规则。**唯一的可替换层**（其余四层是确定性的）。
    ///
    /// 规则（刻意保持简单、可离线穷举测试）：
    ///   1. 预测未来 HorizonDays 天的存量曲线
    ///   2. 若整条曲线都不低于「低水位」→ 什么都不做
    ///   3. 否则算缺口，换算成需要多少格
    ///   4. 没有空地就报告 NeedsMoreLand（让上层决定，而不是硬种）
    /// </summary>
    public static class Policy
    {
        public static FoodDecision Decide(FoodState state, PolicyConfig cfg)
        {
            return Decide(state, cfg, null);
        }

        public static FoodDecision Decide(FoodState state, PolicyConfig cfg, double[] extraSupplyPerDay)
        {
            if (state == null) throw new ArgumentNullException("state");
            if (cfg == null) cfg = new PolicyConfig();

            Calibration cal = state.Calibration ?? new Calibration();
            double burn = state.BurnRatePerDay;

            ForecastResult proj = Projection.Forecast(state, extraSupplyPerDay);

            double lowThreshold = burn * cfg.ReserveDaysLow;
            double highThreshold = burn * cfg.ReserveDaysHigh;

            var decision = new FoodDecision();
            decision.Stock = state.Stock;
            decision.BurnRatePerDay = burn;
            decision.ReserveDays = state.ReserveDays;
            decision.ProjectedReserve = proj.ProjectedReserve;
            decision.MinProjectedReserve = proj.MinReserve;
            decision.LowThreshold = lowThreshold;
            decision.HighThreshold = highThreshold;

            // 从第几天开始不够
            int firstShort = Projection.FirstDayBelow(proj.ProjectedReserve, lowThreshold);
            decision.FirstShortfallDay = firstShort;

            // --- 分支 1：够吃 ---
            if (firstShort < 0)
            {
                decision.Action = FoodAction.Nothing(string.Format(
                    "预测 {0} 天内最低仍有 {1:F1} nutrition（阈值 {2:F1}），不需要动手",
                    cal.HorizonDays, proj.MinReserve, lowThreshold));
                return decision;
            }

            // --- 分支 2：缺口 ---
            double gap = lowThreshold - proj.MinReserve;
            if (gap <= 0)
            {
                decision.Action = FoodAction.Nothing("缺口未超过阈值，不动手");
                return decision;
            }

            double nutritionPerCell = cal.NutritionPerCellAtMaturity;
            int needed = nutritionPerCell > 0
                ? (int)Math.Ceiling(gap / nutritionPerCell)
                : 0;

            decision.NeededCells = needed;

            // 上限：别一次种太多
            if (needed > cfg.MaxCellsPerDecision) needed = cfg.MaxCellsPerDecision;
            // 下限：太小的地块不值得
            if (needed < cfg.MinCellsPerDecision) needed = cfg.MinCellsPerDecision;

            // --- 分支 3：没有空地 ---
            if (state.ArableCellsAvailable <= 0 || needed > state.ArableCellsAvailable)
            {
                decision.NeedsMoreLand = true;
                decision.Action = FoodAction.Nothing(string.Format(
                    "第 {0} 天会跌破阈值（预计最低 {1:F1}，阈值 {2:F1}），缺 {3:F1} nutrition" +
                    " ≈ {4} 格，但可用空地只有 {5} 格 → 需要更多土地/其它食物来源",
                    firstShort, proj.MinReserve, lowThreshold, gap, needed, state.ArableCellsAvailable));
                return decision;
            }

            // --- 分支 4：种 ---
            decision.Action = FoodAction.Plant(
                cfg.PreferredPlantDefName,
                needed,
                string.Format(
                    "第 {0} 天将跌破阈值（预计最低 {1:F1} < {2:F1}），缺 {3:F1} nutrition → 新开 {4} 格 {5}",
                    firstShort, proj.MinReserve, lowThreshold, gap, needed, cfg.PreferredPlantDefName));

            return decision;
        }
    }

    /// <summary>决策结果（含诊断信息，全部进遥测）。</summary>
    public sealed class FoodDecision
    {
        public FoodAction Action = FoodAction.Nothing("未决策");

        public double Stock;
        public double BurnRatePerDay;
        public double ReserveDays;
        public double[] ProjectedReserve = new double[0];
        public double MinProjectedReserve;
        public int FirstShortfallDay = -1;
        public double LowThreshold;
        public double HighThreshold;

        /// <summary>按缺口算出来需要多少格（未截断前）</summary>
        public int NeededCells;

        /// <summary>需要更多土地（缺口超过可用空地）</summary>
        public bool NeedsMoreLand;
    }
}
