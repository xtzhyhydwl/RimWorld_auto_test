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

        [Tool("foodprobe/list_stockpiles",
            Description = "列出地图上所有储藏区（Zone_Stockpile）：ID/格数/filter 是否允许物品。只读。")]
        public object ListStockpiles()
        {
            var map = Find.CurrentMap;
            if (map == null) return new { success = false, message = "没有当前地图" };

            var zones = new List<object>();
            int totalCells = 0;
            try
            {
                foreach (var z in map.zoneManager.AllZones)
                {
                    var sp = z as Zone_Stockpile;
                    if (sp == null) continue;

                    int cells = 0;
                    bool acceptsAnything = false;
                    int sampleAllowed = 0;
                    try
                    {
                        cells = sp.cells != null ? sp.cells.Count : 0;
                        totalCells += cells;

                        // filter 是否"允许所有"：拿几个常见 def 试
                        if (sp.settings != null && sp.settings.filter != null)
                        {
                            var f = sp.settings.filter;
                            var probes = new[] { "WoodLog", "Steel", "Silver", "RawRice", "MealSimple" };
                            foreach (var dn in probes)
                            {
                                var d = DefDatabase<ThingDef>.GetNamedSilentFail(dn);
                                if (d == null) continue;
                                try { if (f.Allows(d)) sampleAllowed++; } catch { }
                            }
                            acceptsAnything = sampleAllowed > 0;
                        }
                    }
                    catch { }

                    zones.Add(new
                    {
                        zoneId = sp.ID,
                        cells,
                        registered = map.zoneManager.AllZones.Contains(sp),
                        acceptsCommonItems = acceptsAnything,
                        allowedProbeCount = sampleAllowed,
                        label = sp.label,
                        firstCell = cells > 0 ? (object)new { x = sp.cells[0].x, z = sp.cells[0].z } : null,
                    });
                }
            }
            catch (Exception e)
            {
                return new { success = false, message = e.Message };
            }

            return new
            {
                success = true,
                count = zones.Count,
                totalCells,
                zones,
                note = "count>0 且 totalCells>0 才说明真的建出了储藏区（只看调用成功不算）。",
            };
        }

        [Tool("foodprobe/run_stockpile",
            Description = "让 mod 立刻跑一次储藏区域（不等调度），返回一行结果。")]
        public object RunStockpile()
        {
            var asm = FindMod();
            if (asm == null) return new { success = false, message = "FoodControl 未加载" };
            var comp = FindType(asm, "FoodControlGameComponent");
            if (comp == null) return new { success = false, message = "找不到 FoodControlGameComponent" };

            try
            {
                var r = CallStatic(comp, "RunStockpileNow");
                if (r == null)
                {
                    return new
                    {
                        success = false,
                        message = "没有 RunStockpileNow() 静态方法。现有静态方法："
                            + string.Join(", ", comp.GetMethods(ANY_STATIC).Select(m => m.Name).Distinct().ToArray()),
                    };
                }
                return new { success = true, result = r.ToString() };
            }
            catch (Exception e)
            {
                return new { success = false, message = e.Message, exception = e.GetType().FullName };
            }
        }

        [Tool("foodprobe/domains",
            Description = "读域协调器状态：注册了哪些域、每个域上次跑过哪天、当前游戏日/时段。只读。")]
        public object Domains()
        {
            var map = Find.CurrentMap;
            if (map == null) return new { success = false, message = "没有当前地图" };

            int day = 0, hour = 0;
            try
            {
                day = GenLocalDate.Year(map) * 60 + GenLocalDate.DayOfYear(map);
                hour = GenLocalDate.HourOfDay(map);
            }
            catch { }

            var asm = FindMod();
            if (asm == null) return new { success = false, message = "FoodControl 未加载", day, hour };

            var coord = FindType(asm, "NeedCoordinator");
            if (coord == null) return new { success = false, message = "找不到 NeedCoordinator", day, hour };

            var domainInfos = new List<object>();
            try
            {
                // 读静态属性 Domains（IList<INeedDomain>）
                var prop = coord.GetProperty("Domains", ANY_STATIC);
                var list = prop != null ? prop.GetValue(null, null) : null;
                if (list is System.Collections.IEnumerable en)
                {
                    foreach (var d in en)
                    {
                        if (d == null) continue;
                        var t = d.GetType();
                        string id = null;
                        try
                        {
                            var p = t.GetProperty("Id");
                            if (p != null) id = (string)p.GetValue(d, null);
                        }
                        catch { }

                        int lastDay = int.MinValue;
                        if (id != null)
                        {
                            var m = coord.GetMethod("LastRunDayOf", ANY_STATIC);
                            if (m != null)
                            {
                                try
                                {
                                    var v = m.Invoke(null, new object[] { id });
                                    if (v is int) lastDay = (int)v;
                                }
                                catch { }
                            }
                        }

                        domainInfos.Add(new
                        {
                            id,
                            type = t.Name,
                            lastRunDay = lastDay,
                            ranToday = lastDay == day,
                        });
                    }
                }
            }
            catch (Exception e)
            {
                return new { success = false, message = "读域列表失败：" + e.Message, day, hour };
            }

            int totalCycles = 0;
            try
            {
                var f = coord.GetField("TotalCycles", ANY_STATIC);
                if (f != null) totalCycles = (int)f.GetValue(null);
            }
            catch { }

            return new
            {
                success = true,
                day,
                hour,
                totalCycles,
                domainCount = domainInfos.Count,
                domains = domainInfos,
                note = "ranToday=true 表示这个域今天已经跑过。"
                     + "如果同一天里 totalCycles 持续增长，说明按天去重坏了 —— "
                     + "曾经因此一天跑 900 次决策。",
            };
        }

        [Tool("foodprobe/reset_domain_days",
            Description = "清掉域协调器的'按天去重'记录，让所有域今天可以再跑一次（测试用）。")]
        public object ResetDomainDays()
        {
            var asm = FindMod();
            if (asm == null) return new { success = false, message = "FoodControl 未加载" };
            var coord = FindType(asm, "NeedCoordinator");
            if (coord == null) return new { success = false, message = "找不到 NeedCoordinator" };

            try
            {
                var m = coord.GetMethod("ResetDayTracking", ANY_STATIC);
                if (m == null) return new { success = false, message = "没有 ResetDayTracking()" };
                m.Invoke(null, null);
                return new { success = true, result = "已清空按天去重记录" };
            }
            catch (Exception e)
            {
                return new { success = false, message = e.Message };
            }
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

        // ── S4 住处域：建筑/蓝图的独立回读 ───────────────────────────
        //
        // ★ 这是 S4 验证的关键一环：**只信桥接自己读到的地图状态**，
        //   绝不信 mod 自述的 "success"。判定原则见 skill 的"第一原则"第 2 条。

        /// <summary>
        /// 数地图上的建造物：蓝图（含门蓝图）/ 框架 / 已建成的墙门。
        ///
        /// ⚠ 元数据确证：`RimWorld.Blueprint_Door : RimWorld.Blueprint_Build` ——
        ///   **子类**。所以 `t is Blueprint_Build` 一次就把墙的和门的都算上。
        ///   但分开数才能验证"墙 23 + 门 1"这个具体账。
        /// </summary>
        public static object ListBuildingsImpl(int maxSamples)
        {
            var map = Find.CurrentMap;
            if (map == null) return new { success = false, message = "没有当前地图" };

            int bpBuild = 0;          // 所有 Blueprint_Build（含 Blueprint_Door）
            int bpDoor = 0;           // 其中是 Blueprint_Door 的
            int frames = 0;           // 建造框架
            int wallsBuilt = 0;       // 已建成的墙（ThingDefOf.Wall）
            int doorsBuilt = 0;       // 已建成的门
            int playerBuildings = 0;

            var bpByBuildDef = new Dictionary<string, int>();
            var bpByStuff = new Dictionary<string, int>();
            var bpSamples = new List<object>();
            var frameSamples = new List<object>();

            var wallDef = DefDatabase<ThingDef>.GetNamedSilentFail("Wall");
            var doorDef = DefDatabase<ThingDef>.GetNamedSilentFail("Door");

            try
            {
                foreach (var t in map.listerThings.AllThings)
                {
                    try
                    {
                        if (t?.def == null) continue;

                        var bp = t as Blueprint_Build;
                        if (bp != null)
                        {
                            bpBuild++;
                            if (bp is Blueprint_Door) bpDoor++;

                            var buildDef = bp.def.entityDefToBuild != null
                                ? bp.def.entityDefToBuild.defName : "(null)";
                            if (!bpByBuildDef.ContainsKey(buildDef)) bpByBuildDef[buildDef] = 0;
                            bpByBuildDef[buildDef]++;

                            // ★ 材质要读 `Blueprint_Build.stuffToUse`（公开方法 `EntityToBuildStuff()`），
                            //   **不是** `Thing.Stuff`！
                            //   实测踩过：读 `bp.Stuff` 全是 null → byStuff 显示 "(无)"，
                            //   于是"蓝图到底用什么料"完全看不出来（元数据：Blueprint_Build 有
                            //   `public ThingDef stuffToUse` 字段 + `public ThingDef EntityToBuildStuff()`）。
                            string stuffName = "(无)";
                            try
                            {
                                var sp = bp.EntityToBuildStuff();
                                if (sp != null) stuffName = sp.defName;
                            }
                            catch { }
                            if (!bpByStuff.ContainsKey(stuffName)) bpByStuff[stuffName] = 0;
                            bpByStuff[stuffName]++;

                            // 顺便把"这个蓝图总共要哪些料"读出来（TotalMaterialCost）
                            var costSample = new List<object>();
                            try
                            {
                                var cost = bp.TotalMaterialCost();
                                if (cost != null)
                                {
                                    foreach (var c in cost)
                                    {
                                        if (c == null || c.thingDef == null) continue;
                                        costSample.Add(new { def = c.thingDef.defName, count = c.count });
                                    }
                                }
                            }
                            catch { }

                            if (bpSamples.Count < Math.Max(maxSamples, 8))
                            {
                                bpSamples.Add(new
                                {
                                    x = bp.Position.x, z = bp.Position.z,
                                    blueprintDef = bp.def.defName,
                                    buildsDef = buildDef,
                                    stuff = stuffName,
                                    isDoorBlueprint = bp is Blueprint_Door,
                                    materialCost = costSample,
                                    faction = bp.Faction != null ? bp.Faction.Name : null,
                                });
                            }
                            continue;
                        }

                        if (t is Frame)
                        {
                            frames++;
                            string frameStuff = "(无)";
                            try
                            {
                                var fs = t.Stuff;
                                if (fs != null) frameStuff = fs.defName;
                            }
                            catch { }
                            if (frameSamples.Count < 6)
                            {
                                frameSamples.Add(new
                                {
                                    x = t.Position.x, z = t.Position.z,
                                    def = t.def.defName,
                                    stuff = frameStuff,
                                });
                            }
                            continue;
                        }

                        if (wallDef != null && t.def == wallDef) wallsBuilt++;
                        if (doorDef != null && t.def == doorDef) doorsBuilt++;
                        if (t.def.category == ThingCategory.Building
                            && t.Faction == Faction.OfPlayer) playerBuildings++;
                    }
                    catch { }
                }
            }
            catch (Exception e)
            {
                return new { success = false, message = e.Message, bpBuild, frames, wallsBuilt, doorsBuilt };
            }

            return new
            {
                success = true,
                // ★ 核心数字
                blueprintBuild_total = bpBuild,          // 墙 + 门 的蓝图（门是子类）
                blueprintDoor = bpDoor,                  // 其中门蓝图
                blueprintWall = bpBuild - bpDoor,        // 墙蓝图 = 总数 - 门
                frames,
                wallsBuilt,
                doorsBuilt,
                playerBuildings,
                byBuildDef = bpByBuildDef,
                byStuff = bpByStuff,
                blueprintSamples = bpSamples,
                frameSamples,
                note = "★ 判定：blueprintBuild_total>0 才说明**地图上真的出现了蓝图**。"
                     + "只看 mod 返回的 success 不算数。"
                     + "Blueprint_Door 是 Blueprint_Build 的子类，所以 blueprintBuild_total 已含门，"
                     + "blueprintWall = total - blueprintDoor。",
            };
        }

        [Tool("foodprobe/list_buildings",
            Description = "★S4：数地图上的建造物（蓝图/门蓝图/框架/已建成墙门）+ 材质与位置。只读。")]
        public object ListBuildings(
            [ToolParameter(Description = "采样条数，默认 8")] int maxSamples = 8)
        {
            return ListBuildingsImpl(maxSamples);
        }

        /// <summary>
        /// **地板那条路的干净实验**（一次只动**一格**，前后各记一次账）。
        ///
        /// 背景：我先前**读 IL** 得出"`Designator_Build(TerrainDef)` 走 `TerrainGrid::SetTerrain`，
        /// 是瞬间免费改地形"——实测把它推翻了。根因是 `tools/asmdump/IlDump.cs`
        /// 的 `ShortInlineBrTarget` 有 **off-by-one**，把所有条件跳转的目标都读成"操作数中间"，
        /// 于是我把 `IL_008E: brtrue.s IL_00AF`（**其实是跳向 IL_00B0 的地形分支**）读反了。
        ///
        /// 这个工具就是把结论钉死用的：**一格、一次、前后记账**。
        ///
        /// 判据（用户给的）：
        ///   - 地形没变 + 蓝图 +1（buildsDef == 目标 TerrainDef） ⇒ **地板就是蓝图**
        ///   - 地形变了 + 蓝图 +0                                ⇒ 才是"直接改地形"
        /// </summary>
        public static object FloorPathImpl(string terrainDefName, int count, string mode, int index)
        {
            var map = Find.CurrentMap;
            if (map == null) return new { success = false, message = "没有当前地图" };
            if (count < 1) count = 1;
            if (count > 16) count = 16;
            if (index < 0) index = 0;
            if (string.IsNullOrEmpty(mode)) mode = "designator";

            var terrain = DefDatabase<TerrainDef>.GetNamedSilentFail(terrainDefName);
            if (terrain == null)
                return new { success = false, message = "找不到 TerrainDef: " + terrainDefName };

            // ── 找一个**干净格**：无任何 Thing、无标定、不属于任何区 ──
            var clean = new List<IntVec3>();
            try
            {
                foreach (var c in GenRadial.RadialCellsAround(map.Center, 45, true))
                {
                    if (!c.InBounds(map)) continue;
                    if (!c.Walkable(map)) continue;
                    if (c.GetTerrain(map) == terrain) continue;
                    if (c.GetThingList(map).Count > 0) continue;
                    if (map.zoneManager.ZoneAt(c) != null) continue;
                    if (map.designationManager.HasMapDesignationAt(c)) continue;
                    // 只要"真正干净"的：连相邻格都没有建造物（避免门的交互格之类干扰）
                    clean.Add(c);
                }
            }
            catch (Exception e)
            {
                return new { success = false, message = "找干净格失败：" + e.Message };
            }

            if (clean.Count == 0)
                return new { success = false, message = "地图中心附近找不到干净格" };
            if (index >= clean.Count) index = 0;

            var targets = new List<IntVec3>();
            for (int k = 0; k < count && index + k < clean.Count; k++) targets.Add(clean[index + k]);

            // ── 记账：before ──
            Func<Dictionary<string, int>> countBlueprints = delegate
            {
                var d = new Dictionary<string, int>();
                foreach (var t in map.listerThings.AllThings)
                {
                    var bp = t as Blueprint_Build;
                    if (bp == null) continue;
                    string k = bp.def.entityDefToBuild != null ? bp.def.entityDefToBuild.defName : "(null)";
                    if (!d.ContainsKey(k)) d[k] = 0;
                    d[k]++;
                }
                return d;
            };

            var bpBefore = countBlueprints();
            var terrainBefore = new List<string>();
            foreach (var c in targets) terrainBefore.Add(c.GetTerrain(map).defName);

            int settledBefore = 0;
            foreach (var c in CellRect.WholeMap(map).Cells)
            {
                try { if (c.GetTerrain(map) == terrain) settledBefore++; } catch { }
            }

            // ── 动手 ──
            int accepted = 0, rejected = 0;
            var rejectedReasons = new List<string>();
            var errors = new List<string>();
            bool dryRun = mode == "designator_dry";

            try
            {
                if (mode == "genconstruct")
                {
                    // 另一条路：直接把 TerrainDef 当 BuildableDef 交给 GenConstruct
                    foreach (var c in targets)
                    {
                        try
                        {
                            var rep = GenConstruct.CanPlaceBlueprintAt(
                                terrain, c, Rot4.North, map, true, null, null, null,
                                false, false, false);
                            if (!rep.Accepted)
                            {
                                rejected++;
                                if (rejectedReasons.Count < 5) rejectedReasons.Add($"({c.x},{c.z}) {rep.Reason}");
                                continue;
                            }
                            var bp = GenConstruct.PlaceBlueprintForBuild(
                                terrain, c, map, Rot4.North, Faction.OfPlayer, null, null, null, true);
                            if (bp == null) { rejected++; continue; }
                            accepted++;
                        }
                        catch (Exception ex)
                        {
                            rejected++;
                            if (errors.Count < 5)
                                errors.Add($"({c.x},{c.z}) {ex.GetType().Name}: {ex.Message}");
                        }
                    }
                }
                else
                {
                    var des = new Designator_Build(terrain);
                    foreach (var c in targets)
                    {
                        try
                        {
                            var can = des.CanDesignateCell(c);
                            if (!can.Accepted)
                            {
                                rejected++;
                                if (rejectedReasons.Count < 5) rejectedReasons.Add($"({c.x},{c.z}) {can.Reason}");
                                continue;
                            }
                            accepted++;
                            if (dryRun) continue;
                            des.DesignateSingleCell(c);
                        }
                        catch (Exception ex)
                        {
                            rejected++;
                            if (errors.Count < 5)
                                errors.Add($"({c.x},{c.z}) {ex.GetType().Name}: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception e)
            {
                return new
                {
                    success = false,
                    message = "动手时抛异常：" + e.GetType().Name + " " + e.Message,
                    mode, targets = targets.Select(c => $"{c.x},{c.z}").ToArray(),
                };
            }

            // ── 记账：after ──
            var bpAfter = countBlueprints();
            var terrainAfter = new List<string>();
            foreach (var c in targets) terrainAfter.Add(c.GetTerrain(map).defName);

            int settledAfter = 0;
            foreach (var c in CellRect.WholeMap(map).Cells)
            {
                try { if (c.GetTerrain(map) == terrain) settledAfter++; } catch { }
            }

            var delta = new Dictionary<string, int>();
            foreach (var kv in bpAfter)
            {
                int was = bpBefore.ContainsKey(kv.Key) ? bpBefore[kv.Key] : 0;
                if (kv.Value != was) delta[kv.Key] = kv.Value - was;
            }
            foreach (var kv in bpBefore)
            {
                if (!bpAfter.ContainsKey(kv.Key)) delta[kv.Key] = -kv.Value;
            }
            int bpDeltaTotal = 0;
            foreach (var kv in delta) bpDeltaTotal += kv.Value;

            int newBlueprintsForTargetTerrain =
                (bpAfter.ContainsKey(terrain.defName) ? bpAfter[terrain.defName] : 0)
                - (bpBefore.ContainsKey(terrain.defName) ? bpBefore[terrain.defName] : 0);

            bool terrainChanged = settledAfter != settledBefore;
            bool madeBlueprint = newBlueprintsForTargetTerrain > 0;

            string verdict;
            if (dryRun) verdict = "dry-run：只查了 CanDesignateCell，没动手";
            else if (terrainChanged && !madeBlueprint)
                verdict = "★ 地形**直接变了**且**没有**产生蓝图 ⇒ 才是'直接改地形'";
            else if (!terrainChanged && madeBlueprint)
                verdict = "★ 地形**没变**、蓝图 **+1**（且 buildsDef == " + terrain.defName + "）"
                        + " ⇒ **地板就是蓝图**（我先前读 IL 得出的'瞬间改地形'是错的）";
            else if (terrainChanged && madeBlueprint)
                verdict = "两者都变了（地形 + 蓝图）—— 请把这份数据记进文档";
            else
                verdict = "地形没变、蓝图也没变 ⇒ designator 没生效或全被拒";

            return new
            {
                success = true,
                mode,
                dryRun,
                terrainDef = terrain.defName,
                // 元数据层：TerrainDef **有** blueprintDef（这点实测已确证）
                terrain_blueprintDef = terrain.blueprintDef != null ? terrain.blueprintDef.defName : null,
                terrain_frameDef = terrain.frameDef != null ? terrain.frameDef.defName : null,
                terrain_hasBlueprintForm = terrain.blueprintDef != null,
                terrain_costStuffCount = terrain.costStuffCount,

                // ★ 干净实验：一格一次、前后记账
                targetCells = targets.Select(c => $"{c.x},{c.z}").ToArray(),
                terrainBefore,
                terrainAfter,
                terrainChanged,
                settledCountBefore = settledBefore,
                settledCountAfter = settledAfter,

                blueprintCountBefore = bpBefore,
                blueprintCountAfter = bpAfter,
                blueprintDeltaByDef = delta,
                blueprintDeltaTotal = bpDeltaTotal,
                newBlueprintsForTargetTerrain,

                accepted, rejected, rejectedReasons, errors,
                verdict,
            };
        }

        [Tool("foodprobe/floor_path",
            Description = "★地板路径干净实验：在**一格**干净格上动手，前后各记一次地形与蓝图账。"
                        + "mode=designator|genconstruct|designator_dry。会真的改地图（除 dry）。")]
        public object FloorPath(
            [ToolParameter(Description = "地形 defName，默认 WoodPlankFloor")] string terrainDefName = "WoodPlankFloor",
            [ToolParameter(Description = "动几格，默认 1（干净实验建议 1）")] int count = 1,
            [ToolParameter(Description = "designator / genconstruct / designator_dry，默认 designator")] string mode = "designator",
            [ToolParameter(Description = "从第几个干净格开始，默认 0")] int index = 0)
        {
            return FloorPathImpl(terrainDefName, count, mode, index);
        }

        /// <summary>
        /// 查 `TerrainGrid.SetTerrain` / `Designator_Build.DesignateSingleCell` 等有没有被
        /// **别的 mod Harmony patch** 过。
        ///
        /// 为什么要查这个：IL 说"地形直接改"，实测说"产生了蓝图"。
        /// 除了"我自己 IL 读反"之外，第二个可能是 **Harmony patch 改写了行为**。
        /// 这个工具把 patch 列表打出来，一眼就能排除/坐实。
        ///
        /// ⚠ 用**反射**而不是硬引用 `HarmonyLib`：Harmony 的那个 `0Harmony.dll`
        ///   不在游戏的 `Managed\` 里（由 brrainz.harmony 提供，版本还随工坊更新变），
        ///   硬引用会给探针编译引入一个"版本对不上就编不过"的隐形依赖。
        ///   反射只多几十行，但**部署前不需要找到那个 DLL**。
        /// </summary>
        public static object PatchesImpl()
        {
            var rows = new List<object>();
            var errors = new List<string>();

            // ① 找到 HarmonyLib.Harmony 类型
            Type harmonyType = null;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (a.GetName().Name != "0Harmony") continue;
                    harmonyType = a.GetType("HarmonyLib.Harmony", false);
                    if (harmonyType != null) break;
                }
                catch { }
            }
            if (harmonyType == null)
            {
                return new
                {
                    success = false,
                    message = "程序集 0Harmony 未加载（没有别的 mod 用 Harmony）—— "
                            + "那就更说明'IL 说改地形、实测出蓝图'只可能是**我自己 IL 读反了**。",
                };
            }

            MethodInfo getAll = harmonyType.GetMethod(
                "GetAllPatchedMethods", BindingFlags.Public | BindingFlags.Static);
            MethodInfo getInfo = harmonyType.GetMethod(
                "GetPatchInfo", BindingFlags.Public | BindingFlags.Static);
            if (getAll == null || getInfo == null)
                return new { success = false, message = "Harmony 版本不兼容：找不到 GetAllPatchedMethods/GetPatchInfo" };

            IEnumerable patched = null;
            try { patched = getAll.Invoke(null, null) as IEnumerable; }
            catch (Exception e) { return new { success = false, message = "GetAllPatchedMethods 失败：" + e.Message }; }
            if (patched == null) return new { success = false, message = "GetAllPatchedMethods 返回 null" };

            var list = new List<MethodBase>();
            foreach (var o in patched) { var mb = o as MethodBase; if (mb != null) list.Add(mb); }

            var targets = new List<object[]>
            {
                new object[] { typeof(TerrainGrid), "SetTerrain" },
                new object[] { typeof(TerrainGrid), "SetFoundation" },
                new object[] { typeof(Designator_Build), "DesignateSingleCell" },
                new object[] { typeof(Designator_Build), "CanDesignateCell" },
                new object[] { typeof(GenConstruct), "PlaceBlueprintForBuild" },
                new object[] { typeof(GenConstruct), "CanPlaceBlueprintAt_NewTemp" },
            };

            foreach (var tg in targets)
            {
                var ty = (Type)tg[0];
                var mn = (string)tg[1];
                string label = ty.Name + "." + mn;
                try
                {
                    MethodBase hit = null;
                    for (int i = 0; i < list.Count; i++)
                    {
                        MethodBase mb = list[i];
                        if (mb.DeclaringType == ty && mb.Name == mn) { hit = mb; break; }
                    }
                    if (hit == null)
                    {
                        rows.Add(new { target = label, patched = false, owners = new string[0] });
                        continue;
                    }

                    object info = getInfo.Invoke(null, new object[] { hit });
                    var owners = new List<string>();
                    if (info != null)
                    {
                        Type it = info.GetType();
                        string[] kinds = new string[] { "Prefixes", "Postfixes", "Transpilers", "Finalizers" };
                        for (int k = 0; k < kinds.Length; k++)
                        {
                            var p = it.GetProperty(kinds[k]);
                            if (p == null) continue;
                            var coll = p.GetValue(info, null) as IEnumerable;
                            if (coll == null) continue;
                            foreach (var pt in coll)
                            {
                                if (pt == null) continue;
                                var op = pt.GetType().GetField("owner");
                                string owner = op != null ? (op.GetValue(pt) as string) : "?";
                                owners.Add(kinds[k].Substring(0, kinds[k].Length - 2).ToLowerInvariant() + ":" + owner);
                            }
                        }
                    }
                    rows.Add(new { target = label, patched = owners.Count > 0, owners = owners.ToArray() });
                }
                catch (Exception e)
                {
                    errors.Add(label + ": " + e.Message);
                }
            }

            int patchedCount = 0;
            foreach (var r in rows)
            {
                var p = r.GetType().GetProperty("patched");
                if (p != null && (bool)p.GetValue(r, null)) patchedCount++;
            }

            return new
            {
                success = true,
                harmonyLoaded = true,
                totalPatchedMethodsInGame = list.Count,
                ourTargetsPatched = patchedCount,
                patchedMethods = rows,
                errors,
                note = "我们的六个目标方法里 patchedCount 若为 0，"
                     + "那'IL 说改地形、实测出蓝图'就只能是**我自己 IL 读反了**"
                     + "（已定位：IlDump 的 ShortInlineBrTarget off-by-one，已修）。",
            };
        }

        [Tool("foodprobe/patches",
            Description = "查 TerrainGrid.SetTerrain / Designator_Build.DesignateSingleCell 等有没有被 Harmony patch。只读。")]
        public object Patches()
        {
            return PatchesImpl();
        }

        /// <summary>
        /// 排查用的读操作开关：给"数地图上的东西"这类**必须前后一致**的测量用。
        ///
        /// 为什么需要它（本轮实测抓到的一个测量陷阱）：
        ///   我在"放蓝图之前"记一次数量、"之后"再记一次，然后算增量。
        ///   但小人**在这两次之间一直在建造** —— 蓝图会被变成 `Frame` 再从地图上消失，
        ///   于是 `before` 和 `after` 根本不是同一个世界状态，增量算出来是 +0 甚至负数。
        ///   ⇒ 测量期间必须把游戏**暂停**（`TickManager.Paused = true`），
        ///     让两次读数落在同一个静止状态上。
        /// </summary>
        public static object SetPausedImpl(bool paused)
        {
            try
            {
                var tm = Find.TickManager;
                if (tm == null) return new { success = false, message = "没有 TickManager" };

                // ⚠ `TickManager.Paused` 是 **get-only**（写它 CS0200）。
                //   `get_Paused` 的 IL：`curTimeSpeed == Paused(0) || ForcePaused`。
                //
                // ⚠⚠ 更坑的是：`set_CurTimeSpeed` 会**先过 `PlayerCanControl` 这一关**，
                //   不通过就**静默丢弃**（IL：`brtrue.s IL_003C` → 否则走 Messages.Message 然后 ret）。
                //   实测踩过：调试殖民地刚开出来时 `Game.PlayerHasControl` 还是 false，
                //   于是 `CurTimeSpeed = Paused` 被无声拒绝，`set_paused` 返回 `paused=false`，
                //   后面所有"前后两次读数"的增量全变成 +0。
                //   → 测试夹具必须能**绕过 UI 状态**冻结世界，所以这里直接写**私有字段**
                //     `TickManager.curTimeSpeed`（反射），改不动才退回属性。
                var fi = typeof(TickManager).GetField(
                    "curTimeSpeed",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (fi != null)
                {
                    fi.SetValue(tm, paused ? TimeSpeed.Paused : TimeSpeed.Normal);
                }
                else
                {
                    if (paused) tm.CurTimeSpeed = TimeSpeed.Paused;
                    else if (tm.CurTimeSpeed == TimeSpeed.Paused) tm.CurTimeSpeed = TimeSpeed.Normal;
                }

                return new
                {
                    success = true,
                    paused = tm.Paused,
                    curTimeSpeed = tm.CurTimeSpeed.ToString(),
                    forcedFieldWrite = fi != null,
                    playerHasControl = Current.Game != null ? Current.Game.PlayerHasControl : false,
                };
            }
            catch (Exception e)
            {
                return new { success = false, message = e.Message };
            }
        }

        [Tool("foodprobe/set_paused",
            Description = "暂停/恢复游戏，给'先后必须一致'的测量用（小人建造会让蓝图数在两次读之间变化）。")]
        public object SetPaused(
            [ToolParameter(Description = "true = 暂停，false = 恢复")] bool paused = true)
        {
            return SetPausedImpl(paused);
        }

        /// <summary>
        /// **屋顶实测**（用户凭经验说「墙体和门组成封闭空间以后，自动铺屋顶」——请实测确认）。
        ///
        /// 做法：列出地图上所有 `Room`，报告
        ///   ① 这个房间是不是封闭的（`Rooms` / `District` 的封闭判定）
        ///   ② 它有多少格、其中多少格有屋顶、`RoofDef` 是什么
        ///   ③ 有没有 `WorkGiver_BuildRoof` 对应的活儿（`RoofGrid` 里待铺的格）
        ///
        /// ⚠ 屋顶**不是 Thing**（没有 `Blueprint_Build`），它是 `RoofGrid` 上的
        ///   `RoofDef` + `RoofGrid` 的"待建"标记。所以"数蓝图"是数不到屋顶的，
        ///   必须读 `map.roofGrid.RoofAt(c)`。
        /// </summary>
        public static object RoofCheckImpl(int maxRooms)
        {
            var map = Find.CurrentMap;
            if (map == null) return new { success = false, message = "没有当前地图" };
            if (maxRooms <= 0) maxRooms = 8;

            var rooms = new List<object>();
            int totalRoofedCells = 0, totalUnroofedCells = 0;
            var roofsByDef = new Dictionary<string, int>();

            try
            {
                // 全图屋顶统计（"有屋顶的格"和"没屋顶的格"）
                foreach (var c in CellRect.WholeMap(map).Cells)
                {
                    try
                    {
                        var rd = map.roofGrid.RoofAt(c);
                        if (rd != null)
                        {
                            totalRoofedCells++;
                            string roofName = rd.defName;
                            if (!roofsByDef.ContainsKey(roofName)) roofsByDef[roofName] = 0;
                            roofsByDef[roofName]++;
                        }
                        else totalUnroofedCells++;
                    }
                    catch { }
                }

                int n = 0;
                foreach (var room in map.regionGrid.AllRooms)
                {
                    if (room == null) continue;
                    if (n++ >= maxRooms) break;

                    bool anyRoof = false, allRoof = true, noneRoof = true;
                    int roofed = 0, cells = 0;
                    var roomRoofDefs = new Dictionary<string, int>();
                    var roomCells = new List<IntVec3>();

                    try
                    {
                        foreach (var c in room.Cells)
                        {
                            cells++;
                            roomCells.Add(c);
                            var rd = map.roofGrid.RoofAt(c);
                            if (rd != null)
                            {
                                roofed++;
                                anyRoof = true;
                                noneRoof = false;
                                string dn = rd.defName;
                                if (!roomRoofDefs.ContainsKey(dn)) roomRoofDefs[dn] = 0;
                                roomRoofDefs[dn]++;
                            }
                            else allRoof = false;
                        }
                    }
                    catch { }

                    int doorCount = 0, wallCount = 0;
                    try
                    {
                        var doorDef = DefDatabase<ThingDef>.GetNamedSilentFail("Door");
                        var wallDef = DefDatabase<ThingDef>.GetNamedSilentFail("Wall");
                        for (int ci = 0; ci < roomCells.Count; ci++)
                        {
                            var things = roomCells[ci].GetThingList(map);
                            for (int i = 0; i < things.Count; i++)
                            {
                                var t = things[i];
                                if (t?.def == null) continue;
                                if (doorDef != null && t.def == doorDef) doorCount++;
                                if (wallDef != null && t.def == wallDef) wallCount++;
                            }
                        }
                    }
                    catch { }

                    rooms.Add(new
                    {
                        id = room.ID,
                        cells,
                        roofedCells = roofed,
                        anyRoof,
                        allRoof,
                        noneRoof,
                        roofDefs = roomRoofDefs,
                        // 房间是否被判定为"封闭"（有屋顶的系统通常也这么判）
                        isPrisonCell = room.IsPrisonCell,
                        touchesMapEdge = room.TouchesMapEdge,
                        // 房间的边界建筑统计（墙/门）
                        wallCount,
                        doorCount,
                        firstCell = roomCells.Count > 0
                            ? (object)new { x = roomCells[0].x, z = roomCells[0].z } : null,
                    });
                }
            }
            catch (Exception e)
            {
                return new { success = false, message = e.Message, rooms };
            }

            // 待铺屋顶的格子（RoofGrid 的 "unroofed but designated" 之类）
            int roofBuildDesignations = 0;
            try
            {
                var d = DefDatabase<DesignationDef>.GetNamedSilentFail("BuildRoof");
                if (d != null && map.designationManager != null)
                {
                    foreach (var des in map.designationManager.SpawnedDesignationsOfDef(d)) roofBuildDesignations++;
                }
            }
            catch { }

            return new
            {
                success = true,
                mapRoofedCells = totalRoofedCells,
                mapUnroofedCells = totalUnroofedCells,
                roofsByDef = roofsByDef,
                roomCount = map.regionGrid.AllRooms != null ? new List<object>(map.regionGrid.AllRooms).Count : 0,
                roomsShown = rooms.Count,
                rooms,
                roofBuildDesignationCount = roofBuildDesignations,
                note = "★ 结论看 rooms[].allRoof/noneRoof：如果**封闭的墙+门房间**里 allRoof=true，"
                     + "就证实了用户说的'封闭空间自动铺屋顶'。"
                     + "屋顶不是 Thing（数蓝图数不到），它是 map.roofGrid 上的 RoofDef。",
            };
        }

        [Tool("foodprobe/roof_check",
            Description = "★S4：实测屋顶 —— 列房间、每间多少格有屋顶/RoofDef 是什么。只读。")]
        public object RoofCheck(
            [ToolParameter(Description = "最多列几个房间，默认 8")] int maxRooms = 8)
        {
            return RoofCheckImpl(maxRooms);
        }

        /// <summary>
        /// **测试夹具**：直接在指定位置**生成一个封闭的墙+门方盒**（不是蓝图，是已建成的建筑）。
        ///
        /// 为什么需要它：要回答用户那个问题 ——「13×13 的情况下，墙体和门组成封闭空间以后，
        /// **自动铺屋顶**」—— 就必须先有一个**真的建成**的封闭房间。
        /// 而 48 面墙 + 4 扇门由小人真建，要跑好几天游戏日，验证脚本等不起。
        ///
        /// ⚠ 这是**夹具，不是 mod 行为**：它不是 mod 放的，只用来给"屋顶会不会自己长出来"
        ///   这个物理问题造一个确定的初始条件。报告里必须写清楚这一点。
        ///
        /// ⚠ 屋顶不是 Thing（数蓝图数不到），要读 `map.roofGrid.RoofAt(c)`；
        ///   而且屋顶生成要走 tick，所以本工具跑完必须**解除暂停并推进几 tick**再看。
        /// </summary>
        public static object BuildBoxInstantImpl(int x, int z, int size, bool doors, string stuffDefName)
        {
            var map = Find.CurrentMap;
            if (map == null) return new { success = false, message = "没有当前地图" };
            if (size < 3) size = 3;
            if (x < 0 || z < 0 || x + size > map.Size.x || z + size > map.Size.z)
                return new { success = false, message = "越界", mapSize = new { x = map.Size.x, z = map.Size.z } };

            var wallDef = DefDatabase<ThingDef>.GetNamedSilentFail("Wall");
            var doorDef = DefDatabase<ThingDef>.GetNamedSilentFail("Door");
            if (wallDef == null || doorDef == null)
                return new { success = false, message = "找不到 Wall/Door Def" };

            ThingDef stuff = DefDatabase<ThingDef>.GetNamedSilentFail(stuffDefName);
            if (stuff == null) stuff = DefDatabase<ThingDef>.GetNamedSilentFail("WoodLog");

            int maxX = x + size - 1, maxZ = z + size - 1;
            int mid = size / 2;

            // 四边中心的门格（和 mod 的户型一致）
            var doorCells = new HashSet<IntVec3>();
            if (doors)
            {
                doorCells.Add(new IntVec3(x + mid, 0, maxZ));
                doorCells.Add(new IntVec3(maxX, 0, z + mid));
                doorCells.Add(new IntVec3(x + mid, 0, z));
                doorCells.Add(new IntVec3(x, 0, z + mid));
            }

            int walls = 0, drs = 0;
            var errors = new List<string>();

            for (int cx = x; cx <= maxX; cx++)
            {
                for (int cz = z; cz <= maxZ; cz++)
                {
                    bool onEdge = (cx == x || cx == maxX || cz == z || cz == maxZ);
                    if (!onEdge) continue;

                    var c = new IntVec3(cx, 0, cz);
                    bool isDoor = doorCells.Contains(c);
                    ThingDef def = isDoor ? doorDef : wallDef;

                    try
                    {
                        // 先清掉这一格上的东西（夹具要的是确定状态）
                        var existing = c.GetThingList(map);
                        for (int i = existing.Count - 1; i >= 0; i--)
                        {
                            var t = existing[i];
                            if (t != null && t.def != null && !(t is Pawn)) t.Destroy(DestroyMode.Vanish);
                        }

                        Thing b = ThingMaker.MakeThing(def, isDoor ? null : stuff);
                        if (b == null) { errors.Add($"({cx},{cz}) MakeThing null"); continue; }
                        b.SetFactionDirect(Faction.OfPlayer);
                        Rot4 rot = isDoor
                            ? ((cz == z || cz == maxZ) ? Rot4.South : Rot4.East)
                            : Rot4.North;
                        GenSpawn.Spawn(b, c, map, rot);
                        if (isDoor) drs++; else walls++;
                    }
                    catch (Exception e)
                    {
                        if (errors.Count < 6) errors.Add($"({cx},{cz}) {e.GetType().Name}: {e.Message}");
                    }
                }
            }

            return new
            {
                success = errors.Count == 0,
                x, z, size, stuff = stuff.defName,
                wallsSpawned = walls,
                doorsSpawned = drs,
                expectedWalls = (size * 4 - 4) - (doors ? 4 : 0),
                errors,
                note = "★ 这是**测试夹具**（直接 spawn 已建成的建筑），不是 mod 行为。"
                     + "目的是给'封闭空间会不会自动长屋顶'造一个确定条件。",
            };
        }

        [Tool("foodprobe/build_box_instant",
            Description = "★S4 夹具：直接生成一个封闭墙+门方盒（已建成，非蓝图），用来实测屋顶。会改地图。")]
        public object BuildBoxInstant(
            [ToolParameter(Description = "左下角 x")] int x,
            [ToolParameter(Description = "左下角 z")] int z,
            [ToolParameter(Description = "边长，默认 13")] int size = 13,
            [ToolParameter(Description = "四边中心各开一扇门，默认 true")] bool doors = true,
            [ToolParameter(Description = "材质 defName，默认 WoodLog")] string stuffDefName = "WoodLog")
        {
            return BuildBoxInstantImpl(x, z, size, doors, stuffDefName);
        }

        // ── mod 内部的一次性初始化钩子 ─────────────────────────────────
        //
        // 探针自己也能 `unforbid_all`，但**测试要走 mod 自己的代码路径** ——
        // 否则测的是探针，不是 mod。

        [Tool("foodprobe/run_setup",
            Description = "调 mod 自己的一次性初始化（允许所有物资），返回一行结果。")]
        public object RunSetup()
        {
            var asm = FindMod();
            if (asm == null) return new { success = false, message = "FoodControl 未加载" };

            var comp = FindType(asm, "FoodControlGameComponent");
            if (comp == null) return new { success = false, message = "找不到 FoodControlGameComponent" };

            try
            {
                var r = CallStatic(comp, "RunUnforbidNow");
                if (r == null)
                {
                    return new
                    {
                        success = false,
                        message = "FoodControlGameComponent 上没有 RunUnforbidNow()。现有静态方法："
                            + string.Join(", ", comp.GetMethods(ANY_STATIC).Select(m => m.Name).Distinct().ToArray()),
                    };
                }
                return new { success = true, result = r.ToString() };
            }
            catch (Exception e)
            {
                return new
                {
                    success = false,
                    message = "调用 RunUnforbidNow 抛异常",
                    exception = e.GetType().FullName,
                    detail = e.Message,
                };
            }
        }

        [Tool("foodprobe/forbid_some",
            Description = "测试夹具：把若干可搬运物品设成禁取（用于验证 mod 的解锁逻辑）。count 默认 8。")]
        public object ForbidSome(
            [ToolParameter(Description = "禁取多少个堆叠，默认 8")] int count = 8,
            [ToolParameter(Description = "true = 只挑建材（木/石/钢）；false = 任意物品")] bool preferStuff = true)
        {
            return ForbidSomeImpl(count, preferStuff);
        }

        /// <summary>
        /// 造夹具：把若干物品设成 forbidden。
        ///
        /// **为什么需要它**：新开的 debug 殖民地**时有时无**禁取物（实测遇到过 0 个）。
        /// 没有夹具的话，S1 的验证会变成"验证了一个空场景" —— 看起来通过，其实什么都没测。
        /// 有了它就能**确定性地**复现"有禁取物"的前置条件。
        /// </summary>
        public static object ForbidSomeImpl(int count, bool preferStuff)
        {
            var map = Find.CurrentMap;
            if (map == null) return new { success = false, message = "没有当前地图" };

            var picked = new List<object>();
            int changed = 0;

            // 两遍：先按偏好挑，不够再放宽
            for (int pass = 0; pass < 2 && changed < count; pass++)
            {
                foreach (var th in map.listerThings.AllThings.ToList())
                {
                    if (changed >= count) break;
                    try
                    {
                        if (th?.def == null) continue;
                        if (th.def.category != ThingCategory.Item) continue;

                        var comp = th.TryGetComp<CompForbiddable>();
                        if (comp == null || comp.Forbidden) continue;

                        if (pass == 0 && preferStuff && !th.def.IsStuff) continue;
                        // pass 1：放宽到任意物品

                        comp.Forbidden = true;
                        changed++;
                        if (picked.Count < 12)
                        {
                            picked.Add(new
                            {
                                def = th.def.defName,
                                stackCount = th.stackCount,
                                isStuff = th.def.IsStuff,
                            });
                        }
                    }
                    catch { }
                }
            }

            // 回读确认
            int nowForbidden = 0;
            foreach (var th in map.listerThings.AllThings)
            {
                try
                {
                    var c = th.TryGetComp<CompForbiddable>();
                    if (c != null && c.Forbidden && th.def != null && th.def.category == ThingCategory.Item) nowForbidden++;
                }
                catch { }
            }

            return new
            {
                success = true,
                requested = count,
                changed,
                totalForbiddenItemsNow = nowForbidden,
                picked,
                verdict = changed == count ? "夹具就绪" : string.Format("只造出 {0}/{1} 个（可搬运物品不够）", changed, count),
            };
        }

        [Tool("foodprobe/reset_setup",
            Description = "重置 mod 的\"一次性初始化已做过\"标记，让下次 tick 重跑。")]
        public object ResetSetup()
        {
            var asm = FindMod();
            if (asm == null) return new { success = false, message = "FoodControl 未加载" };

            var comp = FindType(asm, "FoodControlGameComponent");
            if (comp == null) return new { success = false, message = "找不到 FoodControlGameComponent" };

            try
            {
                var r = CallStatic(comp, "ResetSetupFlags");
                return new { success = r != null, result = r == null ? "没有 ResetSetupFlags()" : r.ToString() };
            }
            catch (Exception e)
            {
                return new { success = false, message = e.Message };
            }
        }

        // ── S4 住处域：跑 mod 自己的代码路径 ──────────────────────────
        //
        // 为什么必须走 mod 的静态钩子而不是探针自己放蓝图：
        //   否则测的是**探针**，不是 mod。S1/S3 都是这个规矩。

        [Tool("foodprobe/run_shelter",
            Description = "★S4：让 mod 立刻跑一次住处域（选址→选材→放蓝图→回读），返回一行结果。")]
        public object RunShelter()
        {
            return CallModStatic("RunShelterNow", "run_shelter");
        }

        [Tool("foodprobe/run_shelter_force",
            Description = "★S4 **测试专用**：忽略'已有住处/已有蓝图'的守卫，强制盖一间。"
                        + "用于 debug 开局自带几百面墙、正常路径永远拒绝动手的场景。")]
        public object RunShelterForce()
        {
            return CallModStatic("RunShelterNowForce", "run_shelter_force");
        }

        [Tool("foodprobe/set_shelter_force",
            Description = "★S4 **测试专用**：开关'强制盖房'标志位。on 之后生产路径也会无视守卫，"
                        + "所以只是为了让测试能临时打开，**不是**生产配置。")]
        public object SetShelterForce(
            [ToolParameter(Description = "on 或 off，默认 off")] string mode = "off")
        {
            return CallModStatic("SetShelterForce", "set_shelter_force", mode);
        }

        [Tool("foodprobe/shelter_report",
            Description = "★S4：读 mod 最近一次住处域的详细记账 JSON（我们放了几个蓝图、材质、户型、被拒原因）。只读。")]
        public object ShelterReport()
        {
            return CallModStatic("ShelterReportJson", "shelter_report");
        }

        [Tool("foodprobe/inspect_shelter",
            Description = "★S4：只读诊断 —— 住处域这一轮**会**怎么盖（户型/材质/需要多少料），绝不动地图。")]
        public object InspectShelter()
        {
            return CallModStatic("InspectShelterNow", "inspect_shelter");
        }

        [Tool("foodprobe/set_shelter_floor_path",
            Description = "★S4 取证：打开/关闭 mod 里'用 designator 铺地板'这条实验路径。mode=on/off。")]
        public object SetShelterFloorPath(
            [ToolParameter(Description = "on 或 off，默认 off")] string mode = "off")
        {
            return CallModStatic("SetShelterFloorPath", "set_shelter_floor_path", mode);
        }

        [Tool("foodprobe/reset_shelter_days",
            Description = "★S4：清掉域协调器的按天去重，让住处域今天可以再跑一次（测试用）。")]
        public object ResetShelterDays()
        {
            return CallModStatic("ResetShelterFlags", "reset_shelter_days");
        }

        /// <summary>
        /// 调 mod 里 `FoodControlGameComponent` 上的一个 **public static** 方法（0 或 1 个字符串参数）。
        ///
        /// 抽出来是因为 S4 一口气多了四个钩子，每个都抄一遍 FindMod/FindType/CallStatic 太啰嗦。
        /// ⚠ 失败时必须把"现有静态方法列表"回出来 —— 否则排查只能靠猜（这是 S1 留下的一条经验）。
        /// </summary>
        private object CallModStatic(string method, string toolName, params object[] args)
        {
            var asm = FindMod();
            if (asm == null) return new { success = false, message = "FoodControl 未加载" };

            var comp = FindType(asm, "FoodControlGameComponent");
            if (comp == null) return new { success = false, message = "找不到 FoodControlGameComponent" };

            try
            {
                var r = CallStatic(comp, method, args);
                if (r == null)
                {
                    return new
                    {
                        success = false,
                        message = toolName + "：没有 " + method + "() 或返回 null。现有静态方法："
                            + string.Join(", ", comp.GetMethods(ANY_STATIC).Select(m => m.Name).Distinct().ToArray()),
                    };
                }
                return new { success = true, result = r.ToString() };
            }
            catch (Exception e)
            {
                return new
                {
                    success = false,
                    message = "调用 " + method + " 抛异常",
                    exception = e.GetType().FullName,
                    detail = e.Message,
                    stack = e.StackTrace,
                };
            }
        }

        [Tool("foodprobe/materials",
            Description = "数一下手上能用于建造的建材（按 def 分堆）—— S4「按现有资源选材质」的输入。只读。")]
        public object Materials()
        {
            var asm = FindMod();
            if (asm == null) return new { success = false, message = "FoodControl 未加载" };

            var comp = FindType(asm, "FoodControlGameComponent");
            var map = Find.CurrentMap;
            if (map == null) return new { success = false, message = "没有当前地图" };

            // 优先走 mod 自己的代码（测的是 mod 的判据，不是探针的）
            if (comp != null)
            {
                try
                {
                    var r = CallStatic(comp, "CountMaterialsJson");
                    if (r != null) return new { success = true, source = "mod", raw = r.ToString() };
                }
                catch (Exception) { }
            }

            // 退路：探针自己数（mod 没加载时仍能给出参考）
            var counts = new Dictionary<string, int>();
            foreach (var th in map.listerThings.AllThings)
            {
                try
                {
                    if (th?.def == null) continue;
                    if (th.def.category != ThingCategory.Item) continue;
                    if (!th.def.IsStuff) continue;
                    if (!counts.ContainsKey(th.def.defName)) counts[th.def.defName] = 0;
                    counts[th.def.defName] += th.stackCount;
                }
                catch { }
            }
            return new
            {
                success = true,
                source = "probe",
                count = counts.Count,
                materials = counts.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value),
            };
        }
    }
}
