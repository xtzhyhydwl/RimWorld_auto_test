// DSH S4ApiProbe —— 住处域（ShelterDomain）用到的每一个游戏 API 的**编译验证**。
//
// 为什么单独一份：S4 的路子里最不确定的是「地板 = TerrainDef」那条
// （`TerrainDefOf.WoodPlankFloor` 是 TerrainDef 不是 ThingDef，
//  而 `Designator_Build.DesignateSingleCell` 对 TerrainDef 走的是
//  `TerrainGrid::SetTerrain` —— 那是**直接改地形**，不是放蓝图）。
// 所以这里把两条路都写成真代码编一遍：编得过 = 符号名/签名（含 out/ref/默认值/可见性）都对得上。
//
// 它**不是** mod，只要求能编译；产物写在工作区，不碰游戏目录。
// 用法见 tools/asmdump/README.md 的 ApiProbe 一节。
using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace DSH.S4ApiProbe
{
    public static class ShelterProbe
    {
        /// <summary>不执行，只引用。每个 API 都真调一次（编译器会检查签名）。</summary>
        public static void TouchEveryApi(Map map, Pawn pawn, Thing thing)
        {
            // ============ ① 放蓝图（S4 的核心路径）============
            ThingDef stuff = DefDatabase<ThingDef>.GetNamedSilentFail("WoodLog");
            Rot4 rot = Rot4.North;

            // 先查合不合法（本项目纪律：先查再放）
            AcceptanceReport ok = GenConstruct.CanPlaceBlueprintAt(
                ThingDefOf.Wall, new IntVec3(10, 0, 10), rot, map,
                true, null, null, stuff, false, false, false);
            if (ok.Accepted && ok.Reason != null) { }

            // 地表承重
            bool onTerrain = GenConstruct.CanBuildOnTerrain(
                ThingDefOf.Wall, new IntVec3(10, 0, 10), map, rot, null, stuff);
            if (onTerrain) { }

            // 真的放（返回 Blueprint_Build）
            Blueprint_Build bp = GenConstruct.PlaceBlueprintForBuild(
                ThingDefOf.Wall, new IntVec3(10, 0, 10), map, rot,
                Faction.OfPlayer, stuff, null, null, true);
            if (bp != null)
            {
                ThingDef bpDef = bp.def;                 // Blueprint_Build / Blueprint_Door
                ThingDef builtDef = bp.def.entityDefToBuild as ThingDef;
                // ⚠ `Verse.Thing.stuff` 是 **protected** —— 只能用公开的 `Stuff` 属性。
                //   （实测：写 `bp.stuff` 直接 CS1061）
                ThingDef bpStuff = bp.Stuff;
                if (bpDef != null && builtDef != null && bpStuff != null) { }
            }

            // ============ ② 门（回读要连 Blueprint_Door 一起算）============
            Blueprint_Build doorBp = GenConstruct.PlaceBlueprintForBuild(
                ThingDefOf.Door, new IntVec3(10, 0, 11), map, Rot4.East,
                Faction.OfPlayer, stuff, null, null, true);
            bool isDoorBlueprint = doorBp is Blueprint_Door;   // Blueprint_Door : Blueprint_Build
            if (isDoorBlueprint) { }

            // 两个 Def 都必须在
            ThingDef wallDef = ThingDefOf.Wall;
            ThingDef doorDef = ThingDefOf.Door;
            ThingDef stoolDef = ThingDefOf.Stool;
            if (wallDef != null && doorDef != null && stoolDef != null) { }

            // 建造要多少料（选材质/够不够料的输入）
            int wallCost = ThingDefOf.Wall.costStuffCount;
            int doorCost = ThingDefOf.Door.costStuffCount;
            List<StuffCategoryDef> cats = ThingDefOf.Wall.stuffCategories;
            if (wallCost >= 0 && doorCost >= 0 && cats != null)
            {
                bool catContainsWood = cats.Contains(StuffCategoryDefOf.Woody);
                if (catContainsWood) { }
            }

            // ============ ③ 地板 = TerrainDef（★ 不确定的那条）============
            //
            // ⚠ 这里刻意**两件事都写**，因为要分清它们是不是同一件事：
            //   (a) TerrainDef 有没有 blueprintDef / buildingDef？
            //       有 ⇒ 放蓝图那条路对地板也成立
            //       没有 ⇒ TerrainDef 在游戏里就是"直接改地形"，没有蓝图形态
            //   (b) Designator_Build(TerrainDef) 这条路能不能调？
            TerrainDef floor = TerrainDefOf.WoodPlankFloor;
            TerrainDef concrete = TerrainDefOf.Concrete;
            TerrainDef tile = TerrainDefOf.PavedTile;
            if (floor == null || concrete == null || tile == null) return;

            ThingDef floorBlueprint = floor.blueprintDef;     // 推断：可能为 null
            // ⚠ `TerrainDef` **没有** `buildingDef` 字段（实测 CS1061）—— 只有 blueprintDef / frameDef
            ThingDef floorFrame = floor.frameDef;
            int floorCostStuff = floor.costStuffCount;
            List<StuffCategoryDef> floorCats = floor.stuffCategories;
            bool floorIsTerrainDef = floor is TerrainDef;
            if (floorBlueprint != null || floorFrame != null) { }
            if (floorCostStuff >= 0 && floorCats != null && floorIsTerrainDef) { }

            // (b) 通用 designator 入口能不能 new / 能不能直接调
            var des = new Designator_Build(floor);
            des.SetStuffDef(stuff);
            AcceptanceReport canDes = des.CanDesignateCell(new IntVec3(10, 0, 12));
            if (canDes.Accepted) { }
            if (canDes.Reason != null) { }
            des.ResetStuffToDefault();
            // ⚠ 不在这里调 DesignateSingleCell —— 它真的会改地图（探针文件不许有副作用）。
            //   实机验证由 scripts/verify-s4-shelter.mjs 的 foodprobe/floor_path 工具做。
            ThingDef readBackStuff = des.StuffDef;          // Designator_Place.StuffDef（public get）
            if (readBackStuff != null) { }

            // ============ ④ 地图可盖性（喂 Core 的 BuildabilityMap）============
            IntVec3 c = new IntVec3(10, 0, 10);
            bool walkable = c.Walkable(map);                       // Verse.GenGrid 扩展
            int terrainPathCost = c.GetTerrain(map).pathCost;      // BuildableDef.pathCost
            Building edifice = c.GetEdifice(map);
            TerrainAffordanceDef need = ThingUtility.GetTerrainAffordanceNeed(ThingDefOf.Wall, stuff);
            if (walkable && terrainPathCost >= 0 && need != null)
            {
                if (edifice != null && edifice.def != null) { }
            }
            // 挡路的东西（树/石头/已有建筑）
            List<Thing> cellThings = c.GetThingList(map);
            for (int i = 0; i < cellThings.Count; i++)
            {
                Thing t = cellThings[i];
                if (t == null || t.def == null) continue;
                bool isBuilding = t.def.category == ThingCategory.Building;
                bool isBlueprint = t is Blueprint;
                bool isFrame = t is Frame;
                bool isPlant = t is Plant;
                if (isBuilding || isBlueprint || isFrame || isPlant) { }
            }
            // ⚠ `DesignationManager.DesignationAt` **必须**带 DesignationDef（实测 CS7036）；
            //   要"任意标定"得用 `HasMapDesignationAt(IntVec3)`
            bool hasDesignation = map.designationManager.HasMapDesignationAt(c);
            if (hasDesignation) { }

            // ============ ⑤ 回读（数蓝图/建筑）============
            int blueprintBuildCount = 0;
            int wallCount = 0, doorCount = 0, floorBuiltCount = 0;
            foreach (Thing t in map.listerThings.AllThings)
            {
                if (t is Blueprint_Build) blueprintBuildCount++;        // 含 Blueprint_Door（子类）
                if (t.def == ThingDefOf.Wall) wallCount++;
                if (t.def == ThingDefOf.Door) doorCount++;
            }
            // 地板没有 Thing —— 只能读地形
            foreach (IntVec3 cell in CellRect.WholeMap(map).Cells)
            {
                if (cell.GetTerrain(map) == TerrainDefOf.WoodPlankFloor) floorBuiltCount++;
            }
            if (blueprintBuildCount < 0 || wallCount < 0 || doorCount < 0 || floorBuiltCount < 0) { }

            // ============ ⑥ 已有玩家建筑检测（别重复盖）============
            int playerWalls = 0;
            foreach (Thing t in map.listerThings.ThingsOfDef(ThingDefOf.Wall))
            {
                if (t.Faction == Faction.OfPlayer) playerWalls++;
            }
            var spawnedBlueprints = map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint);
            var spawnedFrames = map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame);
            if (playerWalls < 0 || spawnedBlueprints == null || spawnedFrames == null) { }

            // ============ ⑦ 殖民者（够不够人盖楼，报告用）============
            int free = map.mapPawns.FreeColonistsSpawnedCount;
            if (free < 0 || pawn == null || thing == null) { }
        }

        /// <summary>只有编译意义，不会被调用。</summary>
        public static int Noop()
        {
            return ThingDefOf.Wall.costStuffCount + ThingDefOf.Door.costStuffCount;
        }
    }
}
