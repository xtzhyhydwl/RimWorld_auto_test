using System;
using System.Collections.Generic;

namespace FoodControl.Core
{
    /// <summary>
    /// 未来 N 天的食物供给 / 存量预测。**纯函数，无副作用，可离线测试。**
    ///
    /// 关键设计：不硬编码 RimWorld 的生长公式，而是用 <see cref="Calibration"/> 里的
    /// 实测标定系数。理由见 Models.cs 的注释。
    /// </summary>
    public static class Projection
    {
        /// <summary>
        /// 按「有效生长小时」把某组作物的成熟时间算出来。
        /// 为什么不用墙上时间：实测晚上作物生长速率为 0，直接按 24 小时外推会严重高估产出。
        ///
        /// 约定（单一约定，避免两个系数打架）：
        ///   DaysToMatureAtFullSpeed = 该作物在「肥力 1.0 + 全天有效生长」下从 0 长到 1 的天数
        ///   实际每天生长的 growth 增量 = (1 / DaysToMature) × 肥力 × (有效小时 / 满速小时)
        /// </summary>
        public static double DaysUntilMature(PlantGroup g, Calibration cal)
        {
            if (g == null || g.RemainingGrowth <= 0) return 0;

            double baseDays = g.DaysToMatureAtFullSpeed;
            if (baseDays <= 0) return double.MaxValue;

            double fertility = SafeFertility(g.Fertility);
            double fullHours = cal.FullSpeedGrowthHoursPerDay > 0 ? cal.FullSpeedGrowthHoursPerDay : 24.0;
            double lightFactor = cal.EffectiveGrowthHoursPerDay / fullHours;

            // 每天能推进的 growth 量（0~1）
            double growthPerDay = (1.0 / baseDays) * fertility * lightFactor;
            if (growthPerDay <= 0) return double.MaxValue;   // 完全不长（冬天/无光）

            return g.RemainingGrowth / growthPerDay;
        }

        public static ForecastResult Forecast(FoodState state)
        {
            return Forecast(state, null);
        }

        /// <summary>
        /// 预测未来 HorizonDays 天的存量曲线。
        /// </summary>
        /// <param name="state">当前状态</param>
        /// <param name="extraSupplyPerDay">
        /// 可选的「假设再种下某些东西」的额外日产出（索引 d = 第 d 天新增 nutrition）。
        /// 决策层用它做 what-if 仿真。为 null 则只预测现状。
        /// </param>
        public static ForecastResult Forecast(FoodState state, double[] extraSupplyPerDay)
        {
            if (state == null) throw new ArgumentNullException("state");
            Calibration cal = state.Calibration ?? new Calibration();
            int horizon = cal.HorizonDays > 0 ? cal.HorizonDays : 15;

            double[] supply = new double[horizon + 1];   // 索引 0 恒为 0
            double[] reserve = new double[horizon + 1];

            // --- 已在种的作物：按成熟天数把产出摊到那一天 ---
            if (state.Planted != null)
            {
                for (int i = 0; i < state.Planted.Count; i++)
                {
                    PlantGroup g = state.Planted[i];
                    if (g == null || g.CellCount <= 0) continue;

                    double days = DaysUntilMature(g, cal);
                    if (double.IsInfinity(days) || days > horizon) continue;   // 窗口外，不计

                    int day = (int)Math.Ceiling(days);
                    if (day < 1) day = 1;
                    if (day > horizon) continue;

                    double nutrition = g.CellCount * cal.NutritionPerCellAtMaturity;
                    supply[day] += nutrition;
                }
            }

            // --- what-if 的额外产出 ---
            if (extraSupplyPerDay != null)
            {
                int n = Math.Min(extraSupplyPerDay.Length, supply.Length);
                for (int d = 1; d < n; d++) supply[d] += extraSupplyPerDay[d];
            }

            // --- 累积：存量 = 今天 - 消耗 + 产出 ---
            reserve[0] = state.Stock;
            double min = state.Stock;
            int firstShort = -1;

            for (int d = 1; d <= horizon; d++)
            {
                reserve[d] = reserve[d - 1] + supply[d] - state.BurnRatePerDay;
                if (reserve[d] < min) min = reserve[d];
            }

            return new ForecastResult {
                ProjectedReserve = reserve,
                DailySupply = supply,
                MinReserve = min,
                FirstShortfallDay = firstShort,
            };
        }

        /// <summary>
        /// 找第一次跌破阈值的天数。**单独一个方法**，因为决策层要对不同阈值反复查。
        /// </summary>
        public static int FirstDayBelow(double[] reserve, double threshold)
        {
            if (reserve == null) return -1;
            for (int d = 1; d < reserve.Length; d++)
            {
                if (reserve[d] < threshold) return d;
            }
            return -1;
        }

        private static double SafeFertility(double f)
        {
            if (f <= 0) return 0.01;
            return f;
        }
    }
}
