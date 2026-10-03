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
        /// 距离「可收获」还有多少天。
        ///
        /// 公式直接对应游戏（见 `docs/游戏API符号确证.md`，均为实测确证）：
        ///   游戏内部：`GrowthPerTick = GrowthRate / (60000 × def.plant.growDays)`
        ///   → 每天增长   `GrowthRate / growDays`
        ///   → 距离可收获 `(harvestMinGrowth − Growth) / (GrowthRate / growDays)`
        ///
        /// ⚠ 两个曾经写错、且**会系统性高估成熟时间**的地方：
        ///   1. 终点是 `harvestMinGrowth`（默认 **0.65**），**不是 1.0**
        ///   2. 夜里 `GrowthRate` 是 0（机制是 `Plant.Resting`，**不是**光照因子），
        ///      所以"夜里不长"由 GrowthRate 自然反映，Core 不需要自己造光周期模型
        /// </summary>
        public static double DaysUntilHarvest(PlantGroup g, Calibration cal)
        {
            if (g == null) return double.MaxValue;

            double remaining = g.RemainingToHarvest(cal.HarvestMinGrowth);
            if (remaining <= 0) return 0;                    // 现在就能收

            if (g.GrowDays <= 0) return double.MaxValue;
            if (g.GrowthRate <= 0) return double.MaxValue;   // 当前完全不生长（夜里 / 太冷）

            double growthPerDay = g.GrowthRate / g.GrowDays;
            if (growthPerDay <= 0) return double.MaxValue;

            return remaining / growthPerDay;
        }

        /// <summary>旧名字，语义同 <see cref="DaysUntilHarvest"/>。</summary>
        public static double DaysUntilMature(PlantGroup g, Calibration cal)
        {
            return DaysUntilHarvest(g, cal);
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
    }
}
