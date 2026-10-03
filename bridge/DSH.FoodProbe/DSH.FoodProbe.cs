using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using RimBridgeServer.Sdk;
using Verse;
using RimWorld;

namespace DSH.FoodProbe
{
    /// <summary>
    /// FoodControl 的地基验证探针。
    ///
    /// 为什么需要它：`ZoneMaker.MakeZoneWithCells` 的**签名**已经确证，
    /// 但**内部行为**（是否自动 RegisterZone / 是否逐格 AddCell / 有没有校验）
    /// **无法从元数据判断** —— 只能真的在游戏里跑一遍。
    /// 这是整个自动种田的地基，风险最高，所以先验。
    ///
    /// 设计：用**反射**调 mod 的类型，不硬引用 mod 程序集。
    /// 好处：mod 没加载时能给出清楚的诊断，而不是整个 DLL 加载失败。
    /// </summary>
    public sealed class FoodProbeTools
    {
        private const string MOD_ASM = "FoodControl";
        private const BindingFlags ANY_STATIC =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        private const BindingFlags ANY_ALL =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        // ── 反射小工具 ───────────────────────────────────────────────

        private static Assembly FindMod()
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { if (a.GetName().Name == MOD_ASM) return a; }
                catch { }
            }
            return null;
        }

        private static Type FindType(Assembly asm, string name)
        {
            if (asm == null) return null;
            try
            {
                var t = asm.GetType("FoodControl.Game." + name, false);
                if (t != null) return t;
                t = asm.GetType(name, false);
                if (t != null) return t;
                foreach (var x in asm.GetTypes())
                    if (x.Name == name || x.FullName == name) return x;
            }
            catch { }
            return null;
        }

        private static object CallStatic(Type t, string method, params object[] args)
        {
            if (t == null) return null;
            foreach (var m in t.GetMethods(ANY_STATIC))
            {
                if (m.Name != method) continue;
                var ps = m.GetParameters();
                if (ps.Length != args.Length) continue;
                try { return m.Invoke(null, args); }
                catch (TargetInvocationException tie)
                {
                    throw tie.InnerException ?? tie;
                }
            }
            return null;
        }

        private static object GetStaticField(Type t, string field)
        {
            if (t == null) return null;
            var f = t.GetField(field, ANY_STATIC);
            return f == null ? null : f.GetValue(null);
        }

        /// <summary>把任意对象粗略转成人类可读结构（用于诊断输出）</summary>
        private static object Describe(object o, int depth)
        {
            if (o == null) return null;
            if (depth <= 0) return o.ToString();

            var t = o.GetType();
            if (t.IsPrimitive || t == typeof(string) || t.IsEnum) return o;
            if (o is System.Collections.IEnumerable en && !(o is string))
            {
                var list = new List<object>();
                int n = 0;
                foreach (var item in en)
                {
                    if (n++ >= 20) { list.Add("..."); break; }
                    list.Add(Describe(item, depth - 1));
                }
                return list;
            }

            var d = new Dictionary<string, object>();
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                try { d[p.Name] = Describe(p.GetValue(o, null), depth - 1); }
                catch { }
            }
            return d;
        }

        // ── 工具 ─────────────────────────────────────────────────────

        [Tool("foodprobe/health", Description = "自检：FoodControl mod 加载了吗、内部类型和静态入口在不在。只读。")]
        public object Health()
        {
            var asm = FindMod();
            if (asm == null)
            {
                var loaded = string.Join(", ",
                    AppDomain.CurrentDomain.GetAssemblies()
                        .Select(a => { try { return a.GetName().Name; } catch { return "?"; } })
                        .Where(n => n.ToLowerInvariant().Contains("food"))
                        .ToArray());

                return new
                {
                    success = false,
                    modLoaded = false,
                    message = "程序集 FoodControl 未加载。mod 是否启用？DSH 是否重启过游戏？",
                    foodLikeAssemblies = loaded,
                };
            }

            var boot = FindType(asm, "ModBoot");
            var comp = FindType(asm, "FoodControlGameComponent");
            var reader = FindType(asm, "StateReader");
            var planner = FindType(asm, "PlotPlanner");
            var executor = FindType(asm, "Executor");
            var telemetry = FindType(asm, "Telemetry");

            return new
            {
                success = true,
                modLoaded = true,
                assembly = asm.FullName,
                types = new
                {
                    ModBoot = boot != null,
                    FoodControlGameComponent = comp != null,
                    StateReader = reader != null,
                    PlotPlanner = planner != null,
                    Executor = executor != null,
                    Telemetry = telemetry != null,
                },
                staticEntryPoints = boot == null ? new string[0] :
                    boot.GetMethods(ANY_STATIC).Select(m => m.Name).Distinct().ToArray(),
                hasCurrentGame = Current.Game != null,
            };
        }

        [Tool("foodprobe/run_cycle", Description = "让 mod 立刻跑一轮闭环（不等游戏日），用于测试。")]
        public object RunCycle()
        {
            var asm = FindMod();
            if (asm == null) return new { success = false, message = "FoodControl 未加载" };

            var boot = FindType(asm, "ModBoot");
            if (boot == null) return new { success = false, message = "找不到 FoodControl.Game.ModBoot" };

            try
            {
                var r = CallStatic(boot, "RunCycleNow");
                if (r == null)
                {
                    return new
                    {
                        success = false,
                        message = "ModBoot 上没有 RunCycleNow() 静态方法。现有静态方法："
                            + string.Join(", ", boot.GetMethods(ANY_STATIC).Select(m => m.Name).Distinct().ToArray()),
                    };
                }
                return new { success = true, result = Describe(r, 3) };
            }
            catch (Exception e)
            {
                return new
                {
                    success = false,
                    message = "跑一轮时抛异常",
                    exception = e.GetType().FullName,
                    detail = e.Message,
                    stack = e.StackTrace,
                };
            }
        }

        [Tool("foodprobe/read_state", Description = "读 mod 感知层算出来的 FoodState（食物总量/消耗/在种作物/可用空地）。只读。")]
        public object ReadState()
        {
            var asm = FindMod();
            if (asm == null) return new { success = false, message = "FoodControl 未加载" };

            var reader = FindType(asm, "StateReader");
            if (reader == null) return new { success = false, message = "找不到 StateReader" };

            try
            {
                var r = CallStatic(reader, "Read");
                if (r == null) r = CallStatic(reader, "ReadCurrentMap");
                if (r == null)
                {
                    return new
                    {
                        success = false,
                        message = "StateReader 上没有 Read()/ReadCurrentMap() 静态方法。现有方法："
                            + string.Join(", ", reader.GetMethods(ANY_STATIC).Select(m => m.Name).Distinct().ToArray()),
                    };
                }
                return new { success = true, state = Describe(r, 3) };
            }
            catch (Exception e)
            {
                return new
                {
                    success = false,
                    message = "读状态时抛异常",
                    exception = e.GetType().FullName,
                    detail = e.Message,
                    stack = e.StackTrace,
                };
            }
        }

        /// <summary>
        /// ★ 地基验证的核心：直接测「建种植区 + 设作物」这条路径在真实游戏里行不行。
        ///
        /// 为什么不通过 mod：这一步要验的是**游戏 API 本身**（MakeZoneWithCells / SetPlantDefToGrow），
        /// 如果 mod 层封装掩盖了失败，验证就没意义。
        /// 所以这里**直接调游戏 API**，然后**回读**确认，不假定成功。
        /// </summary>
        [Tool("foodprobe/zone_test",
            Description = "地基验证：在指定格建 Zone_Growing、设作物、回读确认。可 dryRun 只做可行性检查。")]
        public object ZoneTest(
            [ToolParameter(Description = "起点 x（左下角）")] int x,
            [ToolParameter(Description = "起点 z")] int z,
            [ToolParameter(Description = "边长（正方形），默认 6")] int side = 6,
            [ToolParameter(Description = "作物 defName，默认 Plant_Rice")] string plantDefName = "Plant_Rice",
            [ToolParameter(Description = "true = 只检查不真的建")] bool dryRun = false)
        {
            return ZoneTestImpl(x, z, side, plantDefName, dryRun);
        }

        /// <summary>
        /// `zone_test` 的实现。
        ///
        /// ⚠ 做成 **public static** 不是为了给 SDK 用，而是为了能被
        /// `bridge/DSH.FoodProbe/CompileProbe.cs` **在部署前编译验证** ——
        /// 部署要重启游戏（约 12 秒 + 重开殖民地），编译不过就浪费一整轮。
        /// </summary>
        public static object ZoneTestImpl(int x, int z, int side, string plantDefName, bool dryRun)
        {
            var map = Find.CurrentMap;
            if (map == null) return new { success = false, message = "没有当前地图" };
            if (side < 1) side = 6;
            if (x < 0 || z < 0 || x + side > map.Size.x || z + side > map.Size.z)
                return new { success = false, message = "矩形越界", mapSize = new { x = map.Size.x, z = map.Size.z } };

            var plantDef = DefDatabase<ThingDef>.GetNamedSilentFail(plantDefName);
            if (plantDef == null) return new { success = false, message = "找不到作物 Def: " + plantDefName };

            // 逐格诊断：哪些格能种、为什么不能
            var cells = new List<IntVec3>();
            var cellDiag = new List<object>();
            int arable = 0;

            for (int dx = 0; dx < side; dx++)
            {
                for (int dz = 0; dz < side; dz++)
                {
                    var c = new IntVec3(x + dx, 0, z + dz);
                    cells.Add(c);

                    string terrain = null;
                    float fert = 0f;
                    bool canEver = false;
                    string report = null;
                    try
                    {
                        terrain = c.GetTerrain(map)?.defName;
                        fert = map.fertilityGrid.FertilityAt(c);
                        Thing blocking;
                        canEver = plantDef.CanEverPlantAt(c, map, out blocking, false);
                        if (!canEver) report = blocking != null ? ("blocked by " + blocking.def?.defName) : "cannot plant";
                    }
                    catch (Exception e) { report = "probe error: " + e.Message; }

                    if (canEver) arable++;
                    if (cellDiag.Count < 12)
                        cellDiag.Add(new { x = c.x, z = c.z, terrain, fert, canEver, report });
                }
            }

            var before = map.zoneManager.AllZones.OfType<Zone_Growing>().Count();

            if (dryRun)
            {
                return new
                {
                    success = true, dryRun = true,
                    cellsRequested = cells.Count, cellsArable = arable,
                    existingGrowingZones = before,
                    sample = cellDiag,
                };
            }

            object createResult = null;
            Exception createEx = null;
            object registerResult = null;
            Zone_Growing newZone = null;

            try
            {
                // ★ 第 1 步：建区对象 + 逐格 AddCell
                //   IL 实测：MakeZoneWithCells 全文就是一个 `foreach (c in cells) z.AddCell(c)`，
                //   **完全不碰 zoneManager** —— 所以光调它是不够的。
                newZone = new Zone_Growing(map.zoneManager);
                ZoneMaker.MakeZoneWithCells(newZone, cells);

                var midZones = map.zoneManager.AllZones.OfType<Zone_Growing>().Count();
                createResult = new { zonesAfterMakeOnly = midZones };

                // ★ 第 2 步：注册进 zoneManager（实测必需，否则 AllZones 里找不到、小人也不会去种）
                map.zoneManager.RegisterZone(newZone);
                registerResult = new
                {
                    ok = true,
                    zonesAfterRegister = map.zoneManager.AllZones.OfType<Zone_Growing>().Count(),
                    registered = map.zoneManager.AllZones.Contains(newZone),
                };
            }
            catch (Exception e)
            {
                createEx = e;
            }

            // ★ 回读：不假定 MakeZoneWithCells 成功了
            var after = map.zoneManager.AllZones.OfType<Zone_Growing>().ToList();
            var found = after.FirstOrDefault(zn => zn == newZone)
                     ?? after.FirstOrDefault(zn => zn.cells != null && zn.cells.Contains(cells[0]));

            object setCropResult = null;
            string cropLabel = null;
            if (found != null)
            {
                try
                {
                    found.SetPlantDefToGrow(plantDef);
                    cropLabel = found.PlantDefToGrow?.defName;
                }
                catch (Exception e)
                {
                    setCropResult = new { ok = false, error = e.GetType().Name + ": " + e.Message };
                }
            }

            var zoneInfo = found == null ? null : (object)new
            {
                zoneId = found.ID,
                cellCount = found.cells == null ? 0 : found.cells.Count,
                plantDefToGrow = found.PlantDefToGrow?.defName,
                label = found.label,
                registeredInZoneManager = map.zoneManager.AllZones.Contains(found),
                firstCellIsInZone = found.cells != null && found.cells.Contains(cells[0]),
            };

            return new
            {
                // 判定：建出来了 且 注册成功 且 格数对 且 作物设上了
                success = createEx == null
                          && found != null
                          && found.cells != null
                          && found.cells.Count > 0
                          && map.zoneManager.AllZones.Contains(found)
                          && cropLabel == plantDef.defName,
                verdict = createEx != null ? "建区/注册 抛异常"
                        : found == null ? "RegisterZone 之后 zoneManager 里仍然找不到"
                        : (found.cells == null || found.cells.Count == 0) ? "建出来了但没有格（未 AddCell）"
                        : cropLabel != plantDef.defName ? "区建好了但作物没设上"
                        : "★ 地基可用：AddCell + RegisterZone + SetPlantDefToGrow 都成功",
                growingZoneCountBefore = before,
                growingZoneCountAfter = after.Count,
                createException = createEx == null ? null : new
                {
                    type = createEx.GetType().FullName,
                    message = createEx.Message,
                    stack = createEx.StackTrace,
                },
                setCropResult,
                zone = zoneInfo,
                cellsRequested = cells.Count,
                cellsArable = arable,
                sample = cellDiag,
                createResult = createResult,
                registerResult = registerResult,
            };
        }

        [Tool("foodprobe/read_zone", Description = "回读指定种植区的状态：格数、作物、植物生长进度。只读。")]
        public object ReadZone(
            [ToolParameter(Description = "zone ID；省略则列出所有种植区")] int zoneId = -1)
        {
            var map = Find.CurrentMap;
            if (map == null) return new { success = false, message = "没有当前地图" };

            var zones = map.zoneManager.AllZones.OfType<Zone_Growing>().ToList();

            if (zoneId < 0)
            {
                return new
                {
                    success = true,
                    count = zones.Count,
                    zones = zones.Select(zn => (object)new
                    {
                        zoneId = zn.ID,
                        cells = zn.cells == null ? 0 : zn.cells.Count,
                        plantDefToGrow = zn.PlantDefToGrow?.defName,
                    }).ToArray(),
                };
            }

            var z = zones.FirstOrDefault(zn => zn.ID == zoneId);
            if (z == null) return new { success = false, message = "找不到种植区 " + zoneId };

            // 读该区里的植物：生长进度、当前生长速率、growDays、平均肥力
            var plants = new List<object>();
            float fertSum = 0f; int fertN = 0;
            if (z.cells != null)
            {
                foreach (var c in z.cells)
                {
                    try { fertSum += map.fertilityGrid.FertilityAt(c); fertN++; } catch { }
                    var things = c.GetThingList(map);
                    foreach (var th in things)
                    {
                        var p = th as Plant;
                        if (p == null) continue;
                        if (plants.Count < 40)
                        {
                            plants.Add(new
                            {
                                def = p.def.defName,
                                growth = p.Growth,
                                growthRate = p.GrowthRate,
                                growDays = p.def.plant?.growDays,
                                harvestMinGrowth = p.def.plant?.harvestMinGrowth,
                                harvestableNow = p.HarvestableNow,
                                lifeStage = p.LifeStage.ToString(),
                            });
                        }
                    }
                }
            }

            return new
            {
                success = true,
                zoneId = z.ID,
                cells = z.cells == null ? 0 : z.cells.Count,
                plantDefToGrow = z.PlantDefToGrow?.defName,
                avgFertility = fertN > 0 ? fertSum / fertN : 0f,
                plantCount = plants.Count,
                samples = plants,
            };
        }

        [Tool("foodprobe/food_summary", Description = "按游戏 API 直接汇总地图上的食物（验证感知层可行性）。只读。")]
        public object FoodSummary()
        {
            var map = Find.CurrentMap;
            if (map == null) return new { success = false, message = "没有当前地图" };

            double totalNutrition = 0;
            int foodStacks = 0;
            var byDef = new Dictionary<string, double>();
            var errors = new List<string>();

            foreach (var th in map.listerThings.AllThings)
            {
                try
                {
                    if (th == null || th.def == null) continue;
                    if (!th.def.IsNutritionGivingIngestible) continue;

                    float nut = th.GetStatValue(StatDefOf.Nutrition, true);
                    if (nut <= 0f) continue;

                    double n = nut * th.stackCount;
                    totalNutrition += n;
                    foodStacks++;
                    if (!byDef.ContainsKey(th.def.defName)) byDef[th.def.defName] = 0;
                    byDef[th.def.defName] += n;
                }
                catch (Exception e)
                {
                    if (errors.Count < 5) errors.Add(th?.def?.defName + ": " + e.Message);
                }
            }

            var colonists = map.mapPawns.FreeColonistsSpawned;
            int pop = 0;
            try { pop = colonists.Count(); } catch { }

            return new
            {
                success = true,
                totalNutrition = Math.Round(totalNutrition, 1),
                foodStacks,
                colonistCount = pop,
                reserveDaysAt1_6PerDay = pop > 0 ? Math.Round(totalNutrition / (pop * 1.6), 2) : (double?)null,
                topFoods = byDef.OrderByDescending(kv => kv.Value).Take(10)
                    .Select(kv => new { def = kv.Key, nutrition = Math.Round(kv.Value, 1) }).ToArray(),
                errors,
            };
        }

        [Tool("foodprobe/telemetry_tail", Description = "读 mod 写的 telemetry.jsonl 末尾若干行。只读。")]
        public object TelemetryTail(
            [ToolParameter(Description = "读多少行，默认 5")] int lines = 5)
        {
            try
            {
                string dir = GenFilePaths.SaveDataFolderPath;
                string path = System.IO.Path.Combine(dir, "foodcontrol-telemetry.jsonl");
                if (!System.IO.File.Exists(path))
                    return new { success = false, message = "还没有遥测文件", path, dir };

                var all = System.IO.File.ReadAllLines(path);
                int take = Math.Min(Math.Max(lines, 1), all.Length);
                var tail = all.Skip(all.Length - take).ToArray();
                return new { success = true, path, totalLines = all.Length, tail };
            }
            catch (Exception e)
            {
                return new { success = false, message = e.Message };
            }
        }

        /// <summary>
        /// **标定：每格成熟作物值多少 nutrition。**
        ///
        /// 为什么必须实测而不是估：这个数直接决定「缺口 → 要种几格」。
        /// 估错就是**系统性**地种太多或太少（比如真值 6 而填了 18 → 种 3 倍的地）。
        ///
        /// 算法：以游戏自己的数据为准 ——
        ///   每格产量  = `plantDef.plant.harvestYield`（每株基础产量，受收获系数修正）
        ///   每株营养  = `harvestedThingDef.GetStatValue(StatDefOf.Nutrition)`
        ///   ⇒ 每格 nutrition = 产量 × 每株营养
        /// 不做任何硬编码假设，全部从 Def 与 Stat 读。
        /// </summary>
        public static object YieldPerCellImpl(string plantDefName, int sampleCells)
        {
            var map = Find.CurrentMap;
            if (map == null) return new { success = false, message = "没有当前地图" };

            var plantDef = DefDatabase<ThingDef>.GetNamedSilentFail(plantDefName);
            if (plantDef?.plant == null)
                return new { success = false, message = "找不到植物 Def 或它不是植物: " + plantDefName };

            var pp = plantDef.plant;

            // 收获产物的营养值
            //
            // ⚠ `GetStatValue` 是 `RimWorld.StatExtension` 的扩展方法，**接收者必须是 `Thing` 实例**，
            //   不能拿 `ThingDef` 调（实测报 CS1929）。所以这里用 `ThingMaker.MakeThing` 造一个
            //   **未入图**的实例来读 —— 不 spawn，不碰地图。
            double nutPerItem = 0;
            string harvestedDef = null;
            try
            {
                var hd = pp.harvestedThingDef;
                if (hd != null)
                {
                    harvestedDef = hd.defName;
                    var probeThing = ThingMaker.MakeThing(hd, null);
                    if (probeThing != null)
                    {
                        nutPerItem = probeThing.GetStatValue(StatDefOf.Nutrition, true);
                    }
                }
            }
            catch (Exception e)
            {
                return new { success = false, message = "读 harvestedThingDef 营养值失败: " + e.Message };
            }

            double yieldBase = pp.harvestYield;
            double perCell = yieldBase * nutPerItem;

            // 顺便读一下游戏里**真实**的植物：生长进度、以及成熟后可收多少
            var samples = new List<object>();
            double liveYieldSum = 0; int liveYieldN = 0;
            double liveGrowthSum = 0; int liveGrowthN = 0;

            if (map.listerThings != null)
            {
                foreach (var th in map.listerThings.AllThings)
                {
                    var p = th as Plant;
                    if (p == null) continue;
                    if (p.def != plantDef) continue;

                    liveGrowthSum += p.Growth; liveGrowthN++;

                    // 只有成熟到可收获时 YieldNow 才有意义
                    double yn = 0;
                    try { if (p.HarvestableNow) yn = p.YieldNow(); } catch { }
                    if (yn > 0) { liveYieldSum += yn; liveYieldN++; }

                    if (samples.Count < Math.Max(sampleCells, 6))
                    {
                        samples.Add(new
                        {
                            growth = Math.Round(p.Growth, 4),
                            growthRate = Math.Round(p.GrowthRate, 4),
                            harvestableNow = p.HarvestableNow,
                            yieldNow = Math.Round(yn, 2),
                        });
                    }
                }
            }

            return new
            {
                success = true,
                plantDef = plantDef.defName,
                growDays = pp.growDays,
                harvestMinGrowth = pp.harvestMinGrowth,
                harvestYieldBase = yieldBase,
                harvestedThingDef = harvestedDef,
                nutritionPerHarvestedItem = Math.Round(nutPerItem, 5),

                // ★ 这就是要回填到 Calibration.NutritionPerCellAtMaturity 的数
                nutritionPerCellAtMaturity = Math.Round(perCell, 4),
                calculation = string.Format("{0} (harvestYield) × {1} (nutrition/item) = {2}",
                    yieldBase, nutPerItem, Math.Round(perCell, 4)),

                livePlantsFound = liveGrowthN,
                liveAvgGrowth = liveGrowthN > 0 ? Math.Round(liveGrowthSum / liveGrowthN, 4) : 0,
                liveHarvestableCount = liveYieldN,
                liveAvgYieldNow = liveYieldN > 0 ? Math.Round(liveYieldSum / liveYieldN, 2) : 0,

                sample = samples,
                note = "perCell 用的是 harvestYield（每株基础产量）。实际还会被种植者技能/"
                     + "PlantHarvestYield 等 stat 修正；liveAvgYieldNow 是游戏真实 YieldNow 的均值，"
                     + "两者对比可以看出修正幅度。",
            };
        }

        [Tool("foodprobe/yield_per_cell",
            Description = "标定：某作物每格成熟后值多少 nutrition（读游戏 Def + Stat，不硬编码）。只读。")]
        public object YieldPerCell(
            [ToolParameter(Description = "作物 defName，默认 Plant_Rice")] string plantDefName = "Plant_Rice",
            [ToolParameter(Description = "采样多少个植物，默认 6")] int sampleCells = 6)
        {
            return YieldPerCellImpl(plantDefName, sampleCells);
        }

        // ── 禁取（Forbidden）统计与一键允许 ────────────────────────────
        //
        // 背景：**debug 开局刷出来的物资默认是 forbidden（禁止）的，小人不碰。**
        // 如果测试场景没做"允许全部"，小人就只能干看着一堆资源饿死 ——
        // 那是**测试场景的缺陷，不是 mod 的 bug**，必须先区分清楚。
        // 而"开局允许所有物资"本身也是玩家习惯上的一次性操作，值得做成工具。

        /// <summary>统计地图上被禁取的东西（分可搬运/建筑/植物三类）。只读。</summary>
        public static object ForbiddenStatsImpl(int maxSamples)
        {
            var map = Find.CurrentMap;
            if (map == null) return new { success = false, message = "没有当前地图" };

            int total = 0, haulable = 0, buildings = 0, plants = 0, other = 0;
            double forbiddenNutrition = 0;
            var samples = new List<object>();
            var byDef = new Dictionary<string, int>();

            foreach (var th in map.listerThings.AllThings)
            {
                try
                {
                    if (th == null) continue;
                    // 用 CompForbiddable（不是 IsForbidden 字段，那是旧版本写法）
                    var comp = th.TryGetComp<CompForbiddable>();
                    if (comp == null || !comp.Forbidden) continue;

                    total++;
                    if (th is Plant) plants++;
                    else if (th.def != null && th.def.category == ThingCategory.Building) buildings++;
                    else if (th.def != null && th.def.EverHaulable) haulable++;
                    else other++;

                    // 顺手算一下"被禁取的食物有多少 nutrition"——这直接关系到会不会饿死
                    try
                    {
                        if (th.def != null && th.def.category == ThingCategory.Item
                            && th.def.IsNutritionGivingIngestible)
                        {
                            forbiddenNutrition += th.GetStatValue(StatDefOf.Nutrition, true) * th.stackCount;
                        }
                    }
                    catch { }

                    string dn = th.def != null ? th.def.defName : "?";
                    if (!byDef.ContainsKey(dn)) byDef[dn] = 0;
                    byDef[dn] += th.stackCount;

                    if (samples.Count < Math.Max(maxSamples, 8))
                    {
                        samples.Add(new
                        {
                            def = dn,
                            stackCount = th.stackCount,
                            isPlant = th is Plant,
                            everHaulable = th.def != null && th.def.EverHaulable,
                            category = th.def != null ? th.def.category.ToString() : null,
                        });
                    }
                }
                catch { }
            }

            var colonistInfo = new List<object>();
            try
            {
                foreach (var p in map.mapPawns.FreeColonistsSpawned)
                {
                    colonistInfo.Add(new
                    {
                        name = p.LabelShort,
                        mood = Math.Round(p.needs?.mood?.CurLevel ?? 0f, 3),
                        food = Math.Round(p.needs?.food?.CurLevel ?? 0f, 3),
                        // ⚠ 不要写 p.IsForbiddenByAnyone() / playerSettings.forbidItems —— 实测两者都不存在
                        downed = p.Downed,
                        dead = p.Dead,
                    });
                }
            }
            catch { }

            return new
            {
                success = true,
                totalForbidden = total,
                haulable,
                buildings,
                plants,
                other,
                forbiddenFoodNutrition = Math.Round(forbiddenNutrition, 1),
                topDefs = byDef.OrderByDescending(kv => kv.Value).Take(12)
                    .Select(kv => new { def = kv.Key, stackCount = kv.Value }).ToArray(),
                sample = samples,
                colonists = colonistInfo,
                note = "forbiddenFoodNutrition > 0 表示**有食物但小人拿不到** —— "
                     + "这种情况下饿死是测试场景问题，不是 mod 问题。",
            };
        }

        [Tool("foodprobe/forbidden_stats",
            Description = "统计地图上被禁取（forbidden）的东西，含被禁取的食物营养总量。只读。")]
        public object ForbiddenStats(
            [ToolParameter(Description = "采样条数，默认 8")] int maxSamples = 8)
        {
            return ForbiddenStatsImpl(maxSamples);
        }

        /// <summary>
        /// **一键允许全部**：把地图上所有 forbidden 的东西改成允许。
        ///
        /// 这就是玩家开局习惯做的那次性操作（全选 → 允许）。
        /// 做成工具后测试场景可以一条调用搞定，不用手动点。
        /// </summary>
        public static object UnforbidAllImpl(bool onlyItems)
        {
            var map = Find.CurrentMap;
            if (map == null) return new { success = false, message = "没有当前地图" };

            int checkedCount = 0, changed = 0, foodUnforbid = 0;
            double nutritionUnlocked = 0;
            var errors = new List<string>();

            foreach (var th in map.listerThings.AllThings.ToList())
            {
                try
                {
                    if (th == null) continue;
                    var comp = th.TryGetComp<CompForbiddable>();
                    if (comp == null || !comp.Forbidden) continue;

                    checkedCount++;

                    if (onlyItems)
                    {
                        // 只处理"能被搬运的物品"（Item 类），跳过建筑/植物/蓝图等
                        if (th.def == null || th.def.category != ThingCategory.Item) continue;
                    }

                    comp.Forbidden = false;
                    changed++;

                    try
                    {
                        if (th.def != null && th.def.IsNutritionGivingIngestible)
                        {
                            foodUnforbid++;
                            nutritionUnlocked += th.GetStatValue(StatDefOf.Nutrition, true) * th.stackCount;
                        }
                    }
                    catch { }
                }
                catch (Exception e)
                {
                    if (errors.Count < 5) errors.Add(e.GetType().Name + ": " + e.Message);
                }
            }

            // 回读确认：再统计一次还剩多少被禁取
            int remaining = 0;
            foreach (var th in map.listerThings.AllThings)
            {
                try
                {
                    var c = th.TryGetComp<CompForbiddable>();
                    if (c != null && c.Forbidden)
                    {
                        if (!onlyItems || (th.def != null && th.def.category == ThingCategory.Item)) remaining++;
                    }
                }
                catch { }
            }

            return new
            {
                success = true,
                onlyItems,
                scanned = checkedCount,
                unforbidden = changed,
                remainingForbidden = remaining,
                foodStacksUnlocked = foodUnforbid,
                nutritionUnlocked = Math.Round(nutritionUnlocked, 1),
                errors,
                verdict = remaining == 0 ? "全部允许（回读确认无剩余）"
                        : changed == 0 ? "没有需要改的"
                        : string.Format("改了 {0} 个，回读仍剩 {1} 个", changed, remaining),
            };
        }

        [Tool("foodprobe/unforbid_all",
            Description = "一键允许地图上所有被禁取的东西（玩家开局习惯操作）。onlyItems=true 只处理可搬运物品。")]
        public object UnforbidAll(
            [ToolParameter(Description = "true = 只允许可搬运的物品，跳过建筑/植物，默认 true")] bool onlyItems = true)
        {
            return UnforbidAllImpl(onlyItems);
        }
    }
}
