using System;
using System.Collections.Generic;
using System.Linq;
using Verse;
using RimWorld;

/// <summary>
/// DSH.FoodProbe 的**编译探针**。
///
/// 为什么需要它：部署探针 DLL 要
///   ① 把 DLL 拷到游戏 `BridgeTools/`（可逆，便宜）
///   ② **重启游戏**（约 12 秒 + 重开殖民地，贵）
/// 如果探针 DLL 里有编译错误，第 ② 步就白花了。
/// 所以把这个文件跟 `DSH.FoodProbe.cs` **一起编**：
/// 编得过 = 探针里用到的每一个游戏 API 都真实存在且签名对得上。
///
/// 这一招是从 `tools/asmdump/README.md` 学来的 —— 那里证明了
/// 「把符号写成真代码编一遍，是唯一能证明签名正确的手段」。
/// 元数据反射看不出的东西（`protected` 访问器、`out` vs `ref`）编译器能抓出来。
///
/// 这个类**不会被调用**，只要求能编过。
/// </summary>
public static class CompileProbe
{
    public static void Main(string[] args)
    {
        Console.WriteLine("DSH.FoodProbe 编译探针通过 —— 所有游戏 API 符号有效。");
    }

    /// <summary>
    /// 引用探针里用到的每个 API。**不执行**，只要求编译通过。
    /// </summary>
    internal static void TouchEveryApi(Map map, Zone_Growing zone, ThingDef plantDef, Thing thing, Plant plant)
    {
        // ---- 建区（地基验证的核心三行）----
        var z = new Zone_Growing(map.zoneManager);
        var cells = new List<IntVec3> { new IntVec3(0, 0, 0) };
        ZoneMaker.MakeZoneWithCells(z, cells);
        // ⚠ IL 实测：MakeZoneWithCells 只做 `foreach (c) z.AddCell(c)`，
        //    **不注册**。必须自己调 RegisterZone，否则 AllZones 里没有它、小人也不会去种。
        map.zoneManager.RegisterZone(z);
        map.zoneManager.DeregisterZone(z);   // 反向操作也在，便于撤销/测试清理

        // ---- 设作物（PlantDefToGrow 是 get-only，只能走这个方法）----
        zone.SetPlantDefToGrow(plantDef);
        ThingDef readBack = zone.PlantDefToGrow;

        // ---- 区管理 ----
        var all = map.zoneManager.AllZones;
        int zoneId = zone.ID;
        var zoneCells = zone.cells;

        // ---- 可种性判定 ----
        Thing blocking;
        bool canEver = plantDef.CanEverPlantAt(new IntVec3(0, 0, 0), map, out blocking, false);

        // ---- 肥力 ----
        float fert = map.fertilityGrid.FertilityAt(new IntVec3(0, 0, 0));

        // ---- 光照（注意方法名是 GroundGlowAt）----
        float glow = map.glowGrid.GroundGlowAt(new IntVec3(0, 0, 0), false, false);

        // ---- 地表 ----
        TerrainDef terrain = new IntVec3(0, 0, 0).GetTerrain(map);

        // ---- 格子上的东西 ----
        List<Thing> things = new IntVec3(0, 0, 0).GetThingList(map);

        // ---- 产量标定（NutritionPerCellAtMaturity 必须实测，不能估）----
        float harvestYield = plantDef.plant.harvestYield;          // 每株基础产量
        float harvestMinGrowth2 = plantDef.plant.harvestMinGrowth; // 可收获阈值
        ThingDef harvested = plantDef.plant.harvestedThingDef;     // 收获产物
        if (harvested != null)
        {
            // ⚠ GetStatValue 是 StatExtension 的扩展方法，接收者必须是 Thing 实例，
            //   不能拿 ThingDef 调（实测 CS1929）。ThingMaker.MakeThing 造未入图实例来读。
            Thing probeThing = ThingMaker.MakeThing(harvested, null);
            if (probeThing != null)
            {
                float nutPerItem = probeThing.GetStatValue(StatDefOf.Nutrition, true);
                float perCell = harvestYield * nutPerItem;         // ★ 回填 Calibration 的数
                if (perCell < 0) { }
            }
        }
        float yieldNow = plant.YieldNow();                         // 成熟后实际可收量

        // ---- 生长（GrowthRate 是 public；GrowthPerTick/Resting 是 protected，不能碰）----
        float growth = plant.Growth;
        float growthRate = plant.GrowthRate;
        bool harvestable = plant.HarvestableNow;
        var stage = plant.LifeStage;
        float growDays = plantDef.plant.growDays;
        float harvestMinGrowth = plantDef.plant.harvestMinGrowth;

        // ---- 食物/营养（GetStatValue 是 RimWorld.StatExtension 的扩展方法）----
        float nutrition = thing.GetStatValue(StatDefOf.Nutrition, true);
        int stackCount = thing.stackCount;
        bool isFood = thing.def.IsNutritionGivingIngestible;

        // ---- 遍历所有东西 ----
        foreach (var th in map.listerThings.AllThings) { if (th == null) break; }

        // ---- 殖民者 ----
        var colonists = map.mapPawns.FreeColonistsSpawned;
        int pop = colonists.Count();

        // ---- 存档目录（遥测要写这里）----
        string saveDir = GenFilePaths.SaveDataFolderPath;

        // ---- 当前地图 ----
        var cur = Find.CurrentMap;
        var size = map.Size;

        // ---- Def 查询 ----
        var def = DefDatabase<ThingDef>.GetNamedSilentFail("Plant_Rice");

        // 防止"未使用变量"警告把上面的引用优化掉（其实不会，但显式说明意图）
        if (argsNoop(growth, growthRate, growDays, harvestMinGrowth, glow, fert, nutrition, stackCount,
                     isFood, canEver, harvestable, pop, zoneId, zoneCells, all, terrain, things, saveDir,
                     cur, size, def, stage, readBack, blocking, z)) { }
    }

    private static bool argsNoop(params object[] xs) { return xs == null; }
}
