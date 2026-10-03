using System;
using System.Collections.Generic;
using FoodControl.Core;
using RimWorld;
using Verse;

namespace FoodControl.Game
{
    /// <summary>
    /// 感知层：**游戏 API → FoodControl.Core.FoodState**。
    ///
    /// 这一层是「确定性代码」的一部分（设计文档 §二：①感知和④执行必须是确定性代码，
    /// 只有③决策可替换）。所以这里**不做任何策略判断**，只做读数与翻译。
    ///
    /// 依据标注规则：凡标「确证文档 §X」的都来自 `docs/游戏API符号确证.md` 的实测结论（含 IL 反汇编
    /// 与 Roslyn 编译探针）；标「推断」的是我自己的判断，未在游戏里跑过。
    /// </summary>
    public static class StateReader
    {
        /// <summary>
        /// 成年人类殖民者每天消耗的 nutrition。
        ///
        /// **来源与不确定性（必须交代清楚）**：
        ///   确证文档 §A3 从 IL 推出来 `BaseHungerRate = lifeStage.hungerRateFactor(1.0)
        ///   × pawnDef.race.baseHungerRate(1.0) × 2.6666667E-05`，乘 60000 tick/天 = **1.6 /天**。
        ///   这条推导链是 IL 级的，但**「1.6」这个数值本身从没在运行的游戏里实测过**
        ///   （确证文档 §「建议在游戏里实测的」第 5 条明确列为待实测）。
        ///
        /// ⚠ 已知会偏离 1.6 的情形（全部**未处理**，MVP 先接受）：
        ///   - 各种特性 / 基因（Biotech 代谢率）/ 卧床 / 低温休眠舱：会让实际消耗低于或高于 1.6
        ///   - 饥饿档位：`HungerMultiplier` 在 Hungry/UrgentlyHungry/Starving 时是 0.5/0.25/0（确证文档 §A3）
        ///   - **动物、囚犯、访客、商队**也在吃，这里**完全没算**
        ///
        /// TODO(实测替换)：跑一次「清点存量 → 推进 1 天 → 再清点」，用实测值替换这个常量，
        /// 或改成对每个殖民者调 `Need_Food.FoodFallPerTickAssumingCategory(...)` 求和（确证文档 §A3 有签名）。
        /// </summary>
        public const double NutritionPerColonistPerDay = 1.6;

        /// <summary>growth 分桶宽度（1/20 = 5%）。同桶的作物被当成一组。</summary>
        private const float GrowthBucketWidth = 1f / 20f;

        // ------------------------------------------------------------------
        // 主入口
        // ------------------------------------------------------------------

        /// <summary>
        /// **测试钩子专用**：无参数地读「当前地图」的状态，返回一个**全是属性**的快照对象。
        ///
        /// 为什么单独加这一个（`bridge/DSH.FoodProbe` 的 `foodprobe/read_state` 会反射调它）：
        ///   1. 那个探针是 `CallStatic(reader, "Read")` → `"ReadCurrentMap"`，**按 0 个参数找方法**，
        ///      而 <see cref="Read"/> 需要 (Map, PlotPlan)，所以必须有一个 0 参入口。
        ///   2. 探针的 `Describe()` **只遍历 public 属性、不遍历字段**，
        ///      而 `FoodState` / `GameReadings` 的成员都是 public 字段 —— 直接返回它们只会得到空字典。
        ///      所以这里用一个以属性为主的 DTO。
        /// 只读：不决策、不执行、不写遥测。
        /// </summary>
        public static FoodStateSnapshot ReadCurrentMap()
        {
            var snap = new FoodStateSnapshot();
            try
            {
                Map map = Verse.Find.CurrentMap;
                if (map == null)
                {
                    snap.Error = "没有当前地图";
                    return snap;
                }

                // 选址计划也要造一份 —— 「可用空地」是 PlotPlanner 才能算的量
                ThingDef crop = null;
                try { crop = DefDatabase<ThingDef>.GetNamed("Plant_Rice", false); } catch (Exception) { }
                PlotPlan plan = PlotPlanner.BuildPlan(map, crop, new SiteSelectionOptions(), 0);

                GameReadings readings = Read(map, plan);
                FoodState state = ToFoodState(readings, plan, new Calibration());

                snap.Tick = readings.Tick;
                snap.DayOfYear = readings.DayOfYear;
                snap.Season = readings.Season;
                snap.Stock = state.Stock;
                snap.ColonistCount = readings.ColonistCount;
                snap.BurnRatePerDay = state.BurnRatePerDay;
                snap.ReserveDays = state.ReserveDays;
                snap.ArableCellsAvailable = state.ArableCellsAvailable;
                snap.ArableCellsInExistingZones = plan.ArableCellsInExistingZones;
                snap.GrowingZoneCount = readings.GrowingZoneCount;
                snap.CultivatedCells = readings.CultivatedCellCount;
                snap.WildPlantsIgnored = readings.WildPlantCount;
                snap.FoodStacks = readings.FoodStackCount;
                snap.FoodDefs = readings.FoodDefCount;
                snap.Error = readings.Error;
                snap.PlanNote = plan.Note;

                snap.Planted = new List<string>();
                if (readings.CultivatedPlants != null)
                {
                    for (int i = 0; i < readings.CultivatedPlants.Count; i++)
                    {
                        PlantBucket b = readings.CultivatedPlants[i];
                        if (b == null) continue;
                        snap.Planted.Add(string.Format(
                            "{0} cells={1} growth={2:F3} growthRate={3:F3} growDays={4:F1}",
                            b.DefName, b.CellCount, b.AvgGrowth, b.AvgGrowthRate, b.GrowDays));
                    }
                }
            }
            catch (Exception e)
            {
                snap.Error = "ReadCurrentMap 异常: " + e.Message;
            }
            return snap;
        }

        /// <summary>
        /// 把游戏状态读成 <see cref="GameReadings"/>。
        ///
        /// 设计上**永不抛异常**给调用方：任何单点失败都降级成 <see cref="GameReadings.Error"/> 文本，
        /// 让闭环继续（宁可这一轮不动作，也不能把游戏搞崩）。
        /// </summary>
        public static GameReadings Read(Map map, PlotPlan plan)
        {
            var r = new GameReadings();
            r.Map = map;

            if (map == null)
            {
                r.Error = "没有当前地图（Current/Find.CurrentMap 为 null）";
                return r;
            }

            // ---- 时间 ----
            try
            {
                r.Tick = GenTicks.TicksGame;
                r.DayOfYear = GenLocalDate.DayOfYear(map);
                r.Year = GenLocalDate.Year(map);
                r.HourOfDay = GenLocalDate.HourOfDay(map);
                r.Season = GenLocalDate.Season(map).ToString();
            }
            catch (Exception e)
            {
                r.Error = Append(r.Error, "读日期失败: " + e.Message);
            }

            // ---- 食物总量 ----
            try
            {
                double stock = 0.0;
                int stacks = 0;
                var defs = new HashSet<ThingDef>();

                List<Thing> all = ReadAllThings(map);
                for (int i = 0; i < all.Count; i++)
                {
                    Thing t = all[i];
                    if (t == null || t.Destroyed || !t.Spawned) continue;
                    if (!IsFoodThing(t)) continue;

                    // 确证文档 §A1：GetStatValue 是 RimWorld.StatExtension 的**扩展方法**，不是 Thing 的实例方法
                    float nutrition = t.GetStatValue(StatDefOf.Nutrition);
                    if (nutrition <= 0f) continue;

                    int count = t.stackCount > 0 ? t.stackCount : 1;
                    stock += (double)nutrition * count;
                    stacks++;
                    defs.Add(t.def);
                }

                r.StockNutrition = stock;
                r.FoodStackCount = stacks;
                r.FoodDefCount = defs.Count;
            }
            catch (Exception e)
            {
                r.Error = Append(r.Error, "统计食物失败: " + e.Message);
            }

            // ---- 消耗 ----
            try
            {
                // ⚠ 用「本地图上已生成的自由殖民者」，不是 FreeColonists（那个含商队/其它地图的小人，
                //    他们并不吃这张地图的库存）。囚犯/访客/动物没算 —— 见常量的注释。
                List<Pawn> colonists = map.mapPawns != null ? map.mapPawns.FreeColonistsSpawned : null;
                r.ColonistCount = colonists != null ? colonists.Count : 0;
                r.BurnRatePerDay = r.ColonistCount * NutritionPerColonistPerDay;
            }
            catch (Exception e)
            {
                r.Error = Append(r.Error, "统计殖民者失败: " + e.Message);
            }

            // ---- 在种作物 ----
            try
            {
                int cultivated;
                int wild;
                int zeroRate;
                r.CultivatedPlants = ReadPlants(map, out cultivated, out wild, out zeroRate);
                r.CultivatedCellCount = cultivated;
                r.WildPlantCount = wild;
                r.ZeroGrowthRateCells = zeroRate;
            }
            catch (Exception e)
            {
                r.Error = Append(r.Error, "统计作物失败: " + e.Message);
            }

            // ---- 现有种植区 ----
            try
            {
                var zones = map.zoneManager != null ? map.zoneManager.AllZones : null;
                if (zones != null)
                {
                    for (int i = 0; i < zones.Count; i++)
                    {
                        Zone_Growing zg = zones[i] as Zone_Growing;   // 确证文档 §C9：Zone_Growing 在 RimWorld 命名空间
                        if (zg == null) continue;
                        r.GrowingZoneCount++;
                        r.GrowingZoneCellCount += zg.CellCount;
                    }
                }
            }
            catch (Exception e)
            {
                r.Error = Append(r.Error, "统计种植区失败: " + e.Message);
            }

            return r;
        }

        /// <summary>
        /// <see cref="GameReadings"/> → Core 的 <see cref="FoodState"/>。
        /// 这一步是纯翻译，没有游戏调用，所以也**不该**失败。
        /// </summary>
        public static FoodState ToFoodState(GameReadings r, PlotPlan plan, Calibration cal)
        {
            if (cal == null) cal = new Calibration();
            var st = new FoodState();
            if (r == null) return st;

            st.Stock = r.StockNutrition;
            st.BurnRatePerDay = r.BurnRatePerDay;   // 0 是合法值（没有殖民者）；Core 会因此 DoNothing
            st.Season = r.Season;
            st.Tick = r.Tick;
            st.Calibration = cal;

            st.Planted = new List<PlantGroup>();
            if (r.CultivatedPlants != null)
            {
                for (int i = 0; i < r.CultivatedPlants.Count; i++)
                {
                    PlantBucket b = r.CultivatedPlants[i];
                    if (b == null || b.CellCount <= 0) continue;
                    // 确证文档 §B4：GrowthPerTick = GrowthRate / (60000 × def.plant.growDays)
                    // → Core 需要的 GrowDays 就是 def.plant.growDays，GrowthRate 直接用公开的 Plant.GrowthRate
                    double growDays = b.GrowDays > 0 ? b.GrowDays : 1.0;
                    st.Planted.Add(new PlantGroup(b.DefName, b.CellCount, b.AvgGrowth, b.AvgGrowthRate, growDays));
                }
            }

            // 「可用空地」由 PlotPlanner 算（它才知道基地中心与距离场）
            st.ArableCellsAvailable = plan != null ? plan.ArableCellsAvailable : 0;

            return st;
        }

        // ------------------------------------------------------------------
        // 细节
        // ------------------------------------------------------------------

        /// <summary>
        /// 判「这个 Thing 算不算存粮」。
        ///
        /// ⚠ 这里有一个**很容易踩的坑**（本层独有，确证文档没直接写，是我从 IL 推出来的）：
        ///   `ThingDef.IsNutritionGivingIngestible`（确证文档 §A2）对**长在地里的植物也是 true**，
        ///   因为 `Plants_Bases.xml` 里 `PlantBase` 自己就带
        ///   `<ingestible><foodType>Plant</foodType><preferability>RawBad</preferability></ingestible>`，
        ///   而 `IsNutritionGivingIngestible` 的 IL 就是 `ingestible != null && ingestible.CachedNutrition > 0`。
        ///   ⇒ 只按这个属性筛，会把**水稻田和野生浆果丛都算进"现有存粮"**，而它们的产出已经被
        ///     预测器算过一遍了 → **重复计数 + 严重高估存量**（后果是永远不种地）。
        ///
        /// 所以判据是：**必须是 Item（已收获、可堆叠入库的东西）**，再谈营养。
        /// 尸体排除掉（`Corpse` 也是 Item，且带营养，但它是"要屠宰的尸体"不是"存粮"）。
        ///
        /// 副作用边界（推断，未实测）：只统计 `map.listerThings.AllThings` 里的东西，
        /// 所以**小人身上扛着的、商队里的、其它地图的、容器里的**食物都不算。
        /// 对「还能吃几天」这个指标来说这个偏差是可接受的（扛在手上的那点量级很小）。
        /// </summary>
        public static bool IsFoodThing(Thing t)
        {
            if (t == null) return false;
            ThingDef def = t.def;
            if (def == null) return false;

            if (def.category != ThingCategory.Item) return false;   // ★ 关键：挡住长在地里的植物
            if (t is Corpse) return false;                          // 尸体不算存粮

            if (def.IsNutritionGivingIngestible) return true;        // 确证文档 §A2（含编译探针）

            // 再补一条走分类树的判据（确证文档 §A2 两种稳妥写法之二）。
            // 为什么两条都要：`IsNutritionGivingIngestible` 依赖 def.ingestible，而部分食物
            // （例如某些 mod 加的）营养是走 stat 的；分类树能兜住"被定义成 Food 类"的东西。
            ThingCategoryDef foods = ThingCategoryDefOf.Foods;       // ⚠ 命名空间是 RimWorld，不是 Verse（确证文档 §A2）
            if (foods != null && def.thingCategories != null
                && foods.ContainedInThisOrDescendant(def)) return true;

            return false;
        }

        /// <summary>
        /// 把地图上所有植物按「同 ThingDef + 同 growth 桶」聚合成 <see cref="PlantBucket"/>。
        ///
        /// **只统计"人种的"**（在 `Zone_Growing` 里，或长在 `IPlantToGrowSettable` 的种植设施上）。
        /// 为什么不算野生植物：预测器算的是「照现状走下去会有多少收成」，野生浆果丛/树能不能收、
        /// 什么时候被动物吃掉都不确定；把它们算进供给会让决策层以为粮食充足而**不再种地**。
        /// 设计文档 §二也把「地图上可采集的野生食物」列为**独立修正项**（MVP 不做）。
        /// 野生株数仍然写进遥测（`wildPlantsIgnored`），免得以后对不上账。
        /// </summary>
        public static List<PlantBucket> ReadPlants(Map map, out int cultivatedCells, out int wildPlants, out int zeroRateCells)
        {
            cultivatedCells = 0;
            wildPlants = 0;
            zeroRateCells = 0;

            var byKey = new Dictionary<string, PlantBucket>();
            if (map == null) return new List<PlantBucket>();

            List<Thing> all = ReadAllThings(map);
            for (int i = 0; i < all.Count; i++)
            {
                Thing t = all[i];
                if (t == null || t.Destroyed || !t.Spawned) continue;

                Plant p = t as Plant;                 // 确证文档 §B4：RimWorld.Plant
                if (p == null) continue;
                if (p.def == null || p.def.plant == null) continue;

                if (!IsCultivated(p, map))
                {
                    wildPlants++;
                    continue;
                }

                float growth = p.Growth;              // 确证文档 §B4：Growth 可读可写
                float rate = p.GrowthRate;            // 确证文档 §B4：GrowthRate 是 public get（GrowthPerTick 是 protected，调不到）

                if (rate <= 0.0001f) zeroRateCells++;

                int bucket = (int)Math.Floor(growth / GrowthBucketWidth);
                if (bucket < 0) bucket = 0;
                if (bucket > 19) bucket = 19;
                string key = p.def.defName + "#" + bucket;

                PlantBucket b;
                if (!byKey.TryGetValue(key, out b))
                {
                    b = new PlantBucket();
                    b.Def = p.def;
                    b.DefName = p.def.defName;
                    float growDays = p.def.plant.growDays;          // 确证文档 §B4
                    b.GrowDays = growDays > 0f ? growDays : 1.0;
                    byKey[key] = b;
                }

                b.CellCount++;
                b.GrowthSum += growth;
                b.GrowthRateSum += rate;
                cultivatedCells++;
            }

            return new List<PlantBucket>(byKey.Values);
        }

        /// <summary>这株植物是不是「人种的」（而不是野生的）。</summary>
        private static bool IsCultivated(Plant p, Map map)
        {
            IntVec3 c = p.Position;

            // ① 长在种植设施上（水培盆 Building_PlantGrower 等，确证文档 §C9 末尾）
            //    GridsUtility.GetPlantToGrowSettable 是 this IntVec3 扩展方法（确证文档 §C10）
            IPlantToGrowSettable settable = c.GetPlantToGrowSettable(map);
            if (settable != null) return true;

            // ② 在种植区里
            if (map.zoneManager != null)
            {
                Zone z = map.zoneManager.ZoneAt(c);
                if (z is Zone_Growing) return true;
            }

            return false;
        }

        /// <summary>
        /// 拿 `map.listerThings.AllThings`。
        /// 确证文档 §七.2：mod 自己在游戏内遍历 listerThings 是 O(n)，比桥接扫 62500 格快 2~3 个数量级。
        /// </summary>
        private static List<Thing> ReadAllThings(Map map)
        {
            if (map == null || map.listerThings == null) return new List<Thing>();
            List<Thing> all = map.listerThings.AllThings;    // 确证文档 §七.2（ListerThings.AllThings 是 public get）
            return all != null ? all : new List<Thing>();
        }

        private static string Append(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return b;
            return a + " | " + b;
        }
    }

    /// <summary>
    /// 感知结果的**只读快照**，全部用 public 属性。
    ///
    /// 为什么用属性而不是字段：`bridge/DSH.FoodProbe` 的 `Describe()` 只反射 public 属性，
    /// 用字段的话探针输出会是一堆空对象（踩过）。这个类只服务于「测试读一眼」这个场景，
    /// 闭环本体走的是 <see cref="GameReadings"/> + <see cref="FoodControl.Core.FoodState"/>。
    /// </summary>
    public sealed class FoodStateSnapshot
    {
        public int Tick { get; set; }
        public int DayOfYear { get; set; }
        public string Season { get; set; }

        public double Stock { get; set; }
        public int ColonistCount { get; set; }
        public double BurnRatePerDay { get; set; }
        public double ReserveDays { get; set; }

        public int ArableCellsAvailable { get; set; }
        public int ArableCellsInExistingZones { get; set; }
        public int GrowingZoneCount { get; set; }
        public int CultivatedCells { get; set; }
        public int WildPlantsIgnored { get; set; }
        public int FoodStacks { get; set; }
        public int FoodDefs { get; set; }

        public List<string> Planted { get; set; }
        public string PlanNote { get; set; }
        public string Error { get; set; }
    }
}
