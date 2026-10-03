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
    internal static void TouchEveryApi(Map map, Zone_Growing zone, ThingDef plantDef, Thing thing, Plant plant, Pawn pawnForProbe)
    {
        // ---- 禁取（Forbidden）----
        // 用 CompForbiddable（不是 IsForbidden 字段，那是旧版本写法）。
        // ⚠ 实测不存在的写法，别再用：thing.IsForbiddenByAnyone()、pawn.playerSettings.forbidItems
        CompForbiddable forbiddable = thing.TryGetComp<CompForbiddable>();
        if (forbiddable != null)
        {
            bool isForbidden = forbiddable.Forbidden;
            forbiddable.Forbidden = false;              // 一键允许
            if (isForbidden) { }
        }

        // ---- 建材识别（S4「按现有资源选材质」的输入）----
        // 不硬编码 defName 列表，用游戏的 stuff 机制
        if (plantDef.IsStuff) { }
        if (plantDef.category == ThingCategory.Item) { }
        int matCount = thing.stackCount;
        bool preferStuff = plantDef.IsStuff;      // 造夹具时按"是不是建材"筛选

        // ---- 殖民者状态（用于"是不是饿死/崩溃"的诊断）----
        float colonistMood = pawnForProbe.needs?.mood?.CurLevel ?? 0f;
        float colonistFood = pawnForProbe.needs?.food?.CurLevel ?? 0f;
        bool colonistDowned = pawnForProbe.Downed;
        if (colonistMood < 0 || colonistFood < 0 || colonistDowned || matCount < 0) { }

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

    /// <summary>
    /// **S4（住处域）用到的每一个游戏 API**。
    ///
    /// 这一节是 S4 部署前最值钱的东西：部署要重启游戏（约 12 秒 + 重开殖民地），
    /// 编不过就白费一轮。而 S4 的路子里**最不确定的是地板那条**（`TerrainDef` 不是 `ThingDef`），
    /// 所以这里把两条路都写成真代码编一遍。
    ///
    /// ⚠ 这个方法是**上一轮实测抓出来的错误**的活档案，别再改回去：
    ///   - `Verse.Thing.stuff` 是 **protected** → 只能读公开的 `Thing.Stuff`（写 `bp.stuff` 是 CS1061）
    ///   - `TerrainDef` **没有** `buildingDef` 字段（写 `floor.buildingDef` 也是 CS1061）
    ///   - `DesignationManager.DesignationAt` **必须**带 `DesignationDef`（少参数是 CS7036）；
    ///     要"任意标定"得用 `HasMapDesignationAt(IntVec3)`
    /// </summary>
    internal static void TouchShelterApis(Map map)
    {
        ThingDef stuff = DefDatabase<ThingDef>.GetNamedSilentFail("WoodLog");

        // ---- 放蓝图（S4 的核心）：先查再放 ----
        AcceptanceReport report = GenConstruct.CanPlaceBlueprintAt(
            ThingDefOf.Wall, new IntVec3(10, 0, 10), Rot4.North, map,
            true, null, null, stuff, false, false, false);
        if (report.Accepted && report.Reason != null) { }

        bool canBuildOn = GenConstruct.CanBuildOnTerrain(
            ThingDefOf.Wall, new IntVec3(10, 0, 10), map, Rot4.North, null, stuff);
        if (canBuildOn) { }

        Blueprint_Build bp = GenConstruct.PlaceBlueprintForBuild(
            ThingDefOf.Wall, new IntVec3(10, 0, 10), map, Rot4.North,
            Faction.OfPlayer, stuff, null, null, true);
        if (bp != null)
        {
            ThingDef bpDef = bp.def;
            ThingDef builds = bp.def.entityDefToBuild as ThingDef;
            // ⚠ 材质要读 `EntityToBuildStuff()`（= `Blueprint_Build.stuffToUse`），
            //   读 `Thing.Stuff` 对蓝图永远是 null（实测踩过，会得出"蓝图没有材质"的错结论）
            ThingDef bpStuff = bp.EntityToBuildStuff();
            ThingDef namedProperty = bp.BuildDef;                  // == def.entityDefToBuild
            List<ThingDefCountClass> cost = bp.TotalMaterialCost();
            if (bpDef != null && builds != null && bpStuff != null && namedProperty != null && cost != null) { }
        }
        bool isDoorBp = bp is Blueprint_Door;            // Blueprint_Door : Blueprint_Build
        if (isDoorBp) { }

        // ---- 三个 Def 必须在 ----
        if (ThingDefOf.Wall != null && ThingDefOf.Door != null && ThingDefOf.Stool != null) { }

        // ---- 造建造价 / 允许的 stuff 类别 ----
        int wallCost = ThingDefOf.Wall.costStuffCount;
        int doorCost = ThingDefOf.Door.costStuffCount;
        List<StuffCategoryDef> wallCats = ThingDefOf.Wall.stuffCategories;
        if (wallCost >= 0 && doorCost >= 0 && wallCats != null)
        {
            bool hasWoody = wallCats.Contains(StuffCategoryDefOf.Woody);
            if (hasWoody) { }
        }

        // ---- ★ 地板 = TerrainDef（任务书里"没完全确证"的那条）----
        TerrainDef floor = TerrainDefOf.WoodPlankFloor;
        TerrainDef concrete = TerrainDefOf.Concrete;
        TerrainDef tile = TerrainDefOf.PavedTile;
        if (floor == null || concrete == null || tile == null) return;

        ThingDef floorBlueprint = floor.blueprintDef;    // ★ 实测：== Blueprint_WoodPlankFloor（**不是 null**）
        ThingDef floorFrame = floor.frameDef;
        int floorCost = floor.costStuffCount;
        if (floorBlueprint != null || floorFrame != null || floorCost >= 0) { }
        // ⚠ 注意：这里**没有** floor.buildingDef —— 那个成员不存在

        // ★★ 地板也能走 GenConstruct（实测确认：放出来就是 Blueprint_WoodPlankFloor）
        //    这一条是"地板到底能不能放蓝图"的**结论性**引用。
        AcceptanceReport floorCan = GenConstruct.CanPlaceBlueprintAt(
            floor, new IntVec3(10, 0, 13), Rot4.North, map,
            true, null, null, null, false, false, false);
        if (floorCan.Accepted && floorCan.Reason != null) { }

        Blueprint_Build floorBp = GenConstruct.PlaceBlueprintForBuild(
            floor, new IntVec3(10, 0, 13), map, Rot4.North,
            Faction.OfPlayer, null, null, null, true);
        if (floorBp != null)
        {
            // 地板蓝图的 entityDefToBuild 是 TerrainDef（不是 ThingDef！）
            BuildableDef buildsWhat = floorBp.def.entityDefToBuild;
            bool isTerrainTarget = buildsWhat is TerrainDef;
            if (isTerrainTarget) { }
        }

        // 查地形（地板没有 Thing，只能读地形）
        TerrainDef terrainNow = new IntVec3(10, 0, 13).GetTerrain(map);
        if (terrainNow == floor) { }

        // 通用 designator 入口（能吃 TerrainDef）—— 备用路，生产不用
        var des = new Designator_Build(floor);
        des.SetStuffDef(stuff);
        AcceptanceReport canDes = des.CanDesignateCell(new IntVec3(10, 0, 12));
        if (canDes.Accepted && canDes.Reason != null) { }
        des.ResetStuffToDefault();
        ThingDef desStuff = des.StuffDef;                // Designator_Place.StuffDef（public get）
        if (desStuff != null) { }

        // ---- 可盖性（喂 Core 的 BuildabilityMap）----
        IntVec3 c = new IntVec3(10, 0, 10);
        bool walkable = c.Walkable(map);
        int pathCost = c.GetTerrain(map).pathCost;
        Building edifice = c.GetEdifice(map);
        TerrainAffordanceDef afford = ThingUtility.GetTerrainAffordanceNeed(ThingDefOf.Wall, stuff);
        if (walkable && pathCost >= 0 && afford != null && edifice != null) { }

        List<Thing> cellThings = c.GetThingList(map);
        if (cellThings != null)
        {
            for (int i = 0; i < cellThings.Count; i++)
            {
                Thing t = cellThings[i];
                if (t == null || t.def == null) continue;
                bool b = t.def.category == ThingCategory.Building
                      || t is Blueprint || t is Frame || t is Plant;
                if (b) { }
            }
        }
        bool designated = map.designationManager.HasMapDesignationAt(c);
        if (designated) { }

        // ---- 回读：数蓝图 / 已建成的墙门（不信 mod 自述）----
        int bpCount = 0, doorBpCount = 0, frames = 0, walls = 0, doors = 0;
        foreach (Thing t in map.listerThings.AllThings)
        {
            Blueprint_Build b = t as Blueprint_Build;    // 含 Blueprint_Door（子类）
            if (b != null)
            {
                bpCount++;
                if (b is Blueprint_Door) doorBpCount++;
            }
            if (t is Frame) frames++;
            if (t.def == ThingDefOf.Wall) walls++;
            if (t.def == ThingDefOf.Door) doors++;
        }
        if (bpCount < 0 || doorBpCount < 0 || frames < 0 || walls < 0 || doors < 0) { }

        // 按 def 索引的桶（比遍历 AllThings 便宜）
        List<Thing> wallThings = map.listerThings.ThingsOfDef(ThingDefOf.Wall);
        List<Thing> bpGroup = map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint);
        List<Thing> frameGroup = map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame);
        if (wallThings != null && bpGroup != null && frameGroup != null) { }

        // 地形回读（地板没有 Thing —— 只能读地形）
        TerrainDef nowTerrain = new IntVec3(10, 0, 10).GetTerrain(map);
        if (nowTerrain == TerrainDefOf.WoodPlankFloor) { }

        // 径向扫描（地板取证工具用）
        foreach (IntVec3 rc in GenRadial.RadialCellsAround(map.Center, 5, true))
        {
            if (!rc.InBounds(map)) continue;
        }
        foreach (IntVec3 mc in CellRect.WholeMap(map).Cells)
        {
            if (mc.GetTerrain(map) == null) continue;
            break;
        }

        // ---- 测量一致性：暂停游戏（"前后两次读数必须在同一个静止状态"）----
        //
        // ⚠ `TickManager.Paused` 是 **get-only**（写它直接 CS0200，实测踩过）。
        //   `Paused` 的 IL 是：`curTimeSpeed == Paused(0) || ForcePaused`。
        //   所以要暂停/恢复必须改 **`CurTimeSpeed`**（public get+set），值是 `Verse.TimeSpeed`。
        //   （`TickManager.Pause()` 是 private 的，`TogglePaused()` 不保证方向，都不好用。）
        TickManager tm = Find.TickManager;
        if (tm != null)
        {
            tm.CurTimeSpeed = TimeSpeed.Paused;      // 暂停
            bool nowPaused = tm.Paused;
            tm.CurTimeSpeed = TimeSpeed.Normal;      // 恢复
            if (nowPaused) { }
        }

        // ---- ★ 建造人力检查（蓝图放下去没人建 = 白放）----
        //
        // 元数据确证：`Pawn.workSettings` 是 public 字段；`Pawn_WorkSettings` 的
        // `GetPriority` / `SetPriority` / `WorkIsActive` / `Initialized` /
        // `EnableAndInitializeIfNotAlreadyInitialized` 全是 public。
        // `WorkIsActive` 的 IL = `ConfirmInitializedDebug(); return GetPriority(w) > 0;`
        // ⇒ 它**假设已初始化**，新殖民者 priorities 为 null 时读到的 0 不可信（先查 Initialized）。
        WorkTypeDef construction = WorkTypeDefOf.Construction;
        if (construction != null)
        {
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
            {
                Pawn_WorkSettings ws = p.workSettings;
                if (ws == null) continue;
                bool inited = ws.Initialized;
                if (!inited) ws.EnableAndInitializeIfNotAlreadyInitialized();
                int prio = ws.GetPriority(construction);
                bool active = ws.WorkIsActive(construction);
                if (prio < 0 || active) { }
            }
        }

        // ---- ★ 家具 Def（用户要「3 床 + 1 火把」）----
        //
        // ⚠ 单人床 `Bed` 的 Size 是 **1×2**（不是 1×1）—— 这个尺寸必须读 Def，不能写死，
        //   否则 Core 的家具摆放会按错尺寸找位置。
        ThingDef bedDef = DefDatabase<ThingDef>.GetNamedSilentFail("Bed");
        ThingDef torchDef = DefDatabase<ThingDef>.GetNamedSilentFail("TorchLamp");
        ThingDef standingLampDef = DefDatabase<ThingDef>.GetNamedSilentFail("StandingLamp");
        ThingDef campfireDef = DefDatabase<ThingDef>.GetNamedSilentFail("Campfire");
        if (ThingDefOf.Bed != null && ThingDefOf.TorchLamp != null && ThingDefOf.StandingLamp != null)
        {
            if (bedDef != null && torchDef != null && standingLampDef != null && campfireDef != null)
            {
                IntVec2 bedSize = bedDef.Size;
                IntVec2 torchSize = torchDef.Size;
                int bedCost = bedDef.costStuffCount;
                int torchCost = torchDef.costStuffCount;
                // 火把/床也是 Building：同样用 GenConstruct 放蓝图
                AcceptanceReport bedCan = GenConstruct.CanPlaceBlueprintAt(
                    bedDef, new IntVec3(10, 0, 14), Rot4.North, map,
                    true, null, null, stuff, false, false, false);
                if (bedCan.Accepted && bedSize.x > 0 && torchSize.z > 0 && bedCost >= 0 && torchCost >= 0) { }
            }
        }

        // ---- ★ 屋顶：从 Room + RoofGrid 读（屋顶不是 Thing，数蓝图数不到）----
        foreach (Room room in map.regionGrid.AllRooms)
        {
            if (room == null) continue;
            bool isPrison = room.IsPrisonCell;
            bool edge = room.TouchesMapEdge;
            int n = 0;
            foreach (IntVec3 rc in room.Cells)
            {
                RoofDef rd = map.roofGrid.RoofAt(rc);
                if (rd != null) n++;
            }
            if (isPrison || edge || n < 0) { }
            break;
        }
        // 全图屋顶统计用的成员
        RoofDef anyRoof = map.roofGrid.RoofAt(new IntVec3(10, 0, 10));
        bool roofed = map.roofGrid.Roofed(new IntVec3(10, 0, 10));
        if (anyRoof != null && roofed) { }
        DesignationDef buildRoofDef = DefDatabase<DesignationDef>.GetNamedSilentFail("BuildRoof");
        if (buildRoofDef != null && map.designationManager != null)
        {
            foreach (Designation desg in map.designationManager.SpawnedDesignationsOfDef(buildRoofDef)) { break; }
        }

        // ---- 屋顶夹具：直接 spawn 已建成的墙/门（给"封闭空间会不会自动长屋顶"造条件）----
        {
            Thing wallThing = ThingMaker.MakeThing(ThingDefOf.Wall, stuff);
            if (wallThing != null)
            {
                wallThing.SetFactionDirect(Faction.OfPlayer);
                // 注意：这里只构造，不 spawn（探针文件不许有副作用）
                IntVec3 spawnCell = new IntVec3(15, 0, 15);
                if (!spawnCell.InBounds(map)) { }
            }
            Thing doorThing = ThingMaker.MakeThing(ThingDefOf.Door, null);
            if (doorThing != null) { }
            GenSpawn.Spawn(wallThing, new IntVec3(15, 0, 15), map, Rot4.North);
            GenSpawn.Spawn(doorThing, new IntVec3(15, 0, 16), map, Rot4.South);
            // 夹具要能清格
            foreach (Thing t in new IntVec3(15, 0, 15).GetThingList(map))
            {
                if (t != null && t.def != null && !(t is Pawn)) t.Destroy(DestroyMode.Vanish);
                break;
            }
            wallThing.SetFactionDirect(Faction.OfPlayer);
        }
    }

    private static bool argsNoop(params object[] xs) { return xs == null; }
}
