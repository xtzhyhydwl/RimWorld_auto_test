using System.Collections.Generic;
using RimWorld;
using Verse;

namespace FoodControl.Game
{
    /// <summary>
    /// 一轮决策用到的「原始读数」——**纯数据，不做任何计算**。
    ///
    /// 为什么单独一个文件、且只放数据：
    ///   Core（`FoodControl.Core`）是纯逻辑层，**不许引用 Verse/RimWorld**（有静态检查拦着），
    ///   所以「游戏里读到的东西」必须先在 Game 层落脚，再由 <see cref="StateReader"/> 翻译成
    ///   Core 的 <see cref="FoodControl.Core.FoodState"/>。计算全部属于 Core —— 那才是能离线
    ///   毫秒级验证的部分。
    ///
    /// ⚠ 本文件属于 Game 层，所以**允许**出现 Verse/RimWorld 类型；Core 里出现这些会被
    ///   `scripts/build-core.mjs` 的静态检查挡下来。
    ///
    /// 依据：`docs/游戏API符号确证.md`（下称「确证文档」）—— 凡涉及符号的地方都在成员注释里标了节号。
    /// </summary>
    public sealed class GameReadings
    {
        /// <summary>当前地图。允许为 null（表示这一轮没有可读的地图）。</summary>
        public Map Map;

        // ---- 时间（确证文档未涉及，均为元数据确证，见 docs/foodcontrol-game层.md）----
        /// <summary>`Verse.GenTicks.TicksGame`（确证文档 §E 编译探针用过）</summary>
        public int Tick;
        /// <summary>`RimWorld.GenLocalDate.DayOfYear(map)`</summary>
        public int DayOfYear;
        public int Year;
        public int HourOfDay;
        /// <summary>`GenLocalDate.Season(map).ToString()`。用 ToString 而不是 `Label()` 扩展方法，少一个依赖。</summary>
        public string Season = "";

        // ---- 食物（确证文档 §A1 / §A2）----
        /// <summary>全地图「食物物品」的总 nutrition。⚠ 只算 Item，不算长在地里的植物（理由见 StateReader.IsFoodThing）</summary>
        public double StockNutrition;
        /// <summary>参与统计的食物堆数（诊断用）</summary>
        public int FoodStackCount;
        /// <summary>参与统计的食物种类数（诊断用）</summary>
        public int FoodDefCount;

        // ---- 消耗（确证文档 §A3）----
        public int ColonistCount;
        public double BurnRatePerDay;

        // ---- 在种作物（确证文档 §B4 / B5 / B7）----
        /// <summary>「同作物 + 同 growth 桶」聚合后的在种作物</summary>
        public List<PlantBucket> CultivatedPlants = new List<PlantBucket>();
        public int CultivatedCellCount;
        /// <summary>地图上**没被人种**的植物株数（野生浆果丛/树）。MVP 不计入预测，但要写进遥测。</summary>
        public int WildPlantCount;
        /// <summary>GrowthRate == 0 的在种株数（夜里 / 非生长季 / 太冷 → 预测会退化，遥测要看得到）</summary>
        public int ZeroGrowthRateCells;

        // ---- 现有种植区（确证文档 §C9）----
        public int GrowingZoneCount;
        public int GrowingZoneCellCount;

        /// <summary>非空 = 感知阶段降级（例如没有地图 / 拿不到作物 Def）。**遥测必须如实写出来**。</summary>
        public string Error = "";
    }

    /// <summary>
    /// 「同 ThingDef + 同 growth 桶」聚合成的一组在种作物。
    ///
    /// 为什么要分桶而不是逐格：一次决策里几千株作物逐格进 Core 会让遥测和预测都爆掉；
    /// 而 Core 的预测本来只需要（株数, growth, growthRate, growDays）四个量。
    /// 桶宽 5%（见 <see cref="StateReader"/>），同桶内 growth 取平均。
    /// </summary>
    public sealed class PlantBucket
    {
        public ThingDef Def;
        public string DefName = "";
        public int CellCount;

        public double GrowthSum;
        public double GrowthRateSum;

        /// <summary>该作物从 0 长到 1 需要的游戏天数（`ThingDef.plant.growDays`，确证文档 §B4）</summary>
        public double GrowDays = 1.0;

        public double AvgGrowth
        {
            get { return CellCount > 0 ? GrowthSum / CellCount : 0.0; }
        }

        public double AvgGrowthRate
        {
            get { return CellCount > 0 ? GrowthRateSum / CellCount : 0.0; }
        }
    }
}
