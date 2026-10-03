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
        /// <summary>诊断开关（测试里打开，生产默认关闭）。</summary>
        public static bool DiagEnabled = false;

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

            // ★ 缺口 → 格数：必须用**跨时间累积**的产出换算
            //
            // 踩过的坑（量级错误，60 倍）：把每格产出当成"每格能提供的产能"，
            // 直接用 `ceil(gap / 每格)`。但那是**每格一个生长周期的总量**，
            // 而一格里在预测窗口内可以收获**多次**（水稻 3 天成熟 → 15 天可收 5 次）。
            // 更根本的是：缺口是**沿整条曲线累积**的量，
            // 所以要在时间轴上逐日累积产出、找最小格数把整条曲线抬到阈值以上。
            int needed = CellsForGap(state, cfg, cal, proj.ProjectedReserve);

            decision.NeededCells = needed;

            if (needed <= 0)
            {
                decision.Action = FoodAction.Nothing("缺口换算后不需要新增格数，不动手");
                return decision;
            }

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

        /// <summary>
        /// **缺口 → 需要多少格**：算出"要补上多少总产出"，再除以"一格在窗口内能产出多少"。
        ///
        /// ## 为什么不能要求"每一天都高于阈值"（这是踩过的一个真错误）
        ///
        /// 最初的写法是：找最小格数，使得**预测窗口内每一天**的存量都 ≥ 低阈值。
        /// 这在数学上**不可能满足** —— 因为**第 1 天还没有任何收获**，
        /// 存量只减不增。于是二分搜索永远找不到解，一直加倍到 guard 上限，
        /// 返回一个巨大的数，又被 `MaxCellsPerDecision` 截断成 0，
        /// 表现为"零存量却决定 DoNothing"这种荒谬结果。
        ///
        /// ## 正确的语义
        ///
        /// 不要去追求"每一天都在水位线上"（做不到），而是：
        ///   **让窗口内的总产出，补上窗口内总消耗 + 目标存量 与 现有存量的差**
        ///
        /// 即：把"目标带"看成**期末目标**，而不是**逐日约束**。
        /// 是否动手的触发条件仍是"会跌破低水位"（在 <see cref="Decide"/> 里判断）。
        ///
        /// 总产出口径下，所有会落在窗口内的收获都算数，包括**已经在种的作物**
        /// （它们也会贡献产出，不扣掉就会过种）。
        /// </summary>
        public static int CellsForGap(FoodState state, PolicyConfig cfg, Calibration cal, double[] baseReserve)
        {
            if (state == null || cfg == null || cal == null) return 0;
            if (!(cal.NutritionPerCellPerCycle > 0)) return 0;
            if (state.BurnRatePerDay <= 0) return 0;

            int horizon = cal.HorizonDays > 0 ? cal.HorizonDays : 15;
            if (baseReserve != null && baseReserve.Length >= 2 && baseReserve.Length - 1 < horizon)
                horizon = baseReserve.Length - 1;
            if (horizon < 1) return 0;

            double cycleDays = GuessCycleDays(cal, state);
            if (!(cycleDays > 0) || double.IsInfinity(cycleDays)) return 0;

            int firstHarvestDay = (int)Math.Ceiling(cycleDays);
            if (firstHarvestDay < 1) firstHarvestDay = 1;
            if (firstHarvestDay > horizon) return 0;      // 窗口内收不到，种了也白种

            // 一格在窗口内能收几次（复种累积）
            int harvestsPerCell = 0;
            for (double d = firstHarvestDay; d <= horizon; d += cycleDays) harvestsPerCell++;
            if (harvestsPerCell <= 0) return 0;

            double perCellTotal = cal.NutritionPerCellPerCycle * harvestsPerCell;
            if (!(perCellTotal > 0)) return 0;

            // 需求 = 窗口内总消耗 + 目标期末存量 − 当前存量 − 已在种的作物在窗口内的产出
            double consumption = state.BurnRatePerDay * horizon;
            double targetEnd = state.BurnRatePerDay * cfg.ReserveDaysTarget;
            double need = consumption + targetEnd - state.Stock;
            if (need <= 0) return 0;

            // 已在种的作物也算产出（不扣掉会过种）
            double alreadyComing = 0;
            if (baseReserve != null && baseReserve.Length > horizon)
            {
                // 期末存量 − 当前存量 + 总消耗 = 窗口内的总产出（由预测曲线反推，天然含复种）
                alreadyComing = baseReserve[horizon] - state.Stock + consumption;
            }
            if (alreadyComing < 0) alreadyComing = 0;

            double deficit = need - alreadyComing;
            if (deficit <= 0) return 0;

            int cells = (int)Math.Ceiling(deficit / perCellTotal);

            if (DiagEnabled)
            {
                Console.WriteLine("         [CellsForGap] cycleDays=" + cycleDays
                    + " harvestsPerCell=" + harvestsPerCell
                    + " perCellTotal=" + perCellTotal.ToString("F2")
                    + " consumption=" + consumption
                    + " targetEnd=" + targetEnd
                    + " need=" + need.ToString("F1")
                    + " alreadyComing=" + alreadyComing.ToString("F1")
                    + " deficit=" + deficit.ToString("F1")
                    + " cells=" + cells);
            }

            return cells;
        }

        /// <summary>
        /// 推测"新种下的作物"一个生长周期多少天。
        ///
        /// Core 不该硬编码具体作物的 growDays，所以优先用 state 里已有作物的**实测**周期；
        /// 一块地都没种时，退回一个保守默认（按水稻 growDays=3、GrowthRate=1）。
        /// </summary>
        private static double GuessCycleDays(Calibration cal, FoodState state)
        {
            if (state.Planted != null)
            {
                for (int i = 0; i < state.Planted.Count; i++)
                {
                    PlantGroup g = state.Planted[i];
                    if (g == null || g.GrowDays <= 0 || g.GrowthRate <= 0) continue;
                    return cal.HarvestMinGrowth * g.GrowDays / g.GrowthRate;
                }
            }
            return cal.HarvestMinGrowth * 3.0;   // 保守默认：水稻 growDays=3
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
