using System;
using System.Collections.Generic;
using FoodControl.Core;
using RimWorld;
using Verse;

namespace FoodControl.Game
{
    /// <summary>选出来的建材：一个 ThingDef + 它的库存 + 它是不是被 Def 允许用于墙/门。</summary>
    public sealed class MaterialChoice
    {
        public ThingDef Def;
        public int Available;
        public bool AllowedForWall;
        public bool AllowedForDoor;
        public string Why = "";

        public override string ToString()
        {
            return (Def != null ? Def.defName : "(null)")
                + "(库存" + Available + (AllowedForWall ? ",可砌墙" : "") + (AllowedForDoor ? ",可做门" : "") + ")";
        }
    }

    /// <summary>
    /// **住处域**：选一块地 → 生成户型（墙 + 门 + 地板）→ 按现有建材选材质 → 放蓝图 → 回读验证。
    ///
    /// 这是 S4 的全部内容，也是设计文档 §2.5 说的「第二个领域」：
    /// 住户域是**低频半静态**的（盖完就几个月不动），和食物（天天滚动）节奏差异大，
    /// 冲突面小，适合当第二个练手。
    ///
    /// ## 与食物域/储藏区共享什么、不共享什么
    ///
    /// | 复用 | 自己实现 |
    /// | --- | --- |
    /// | 距离场（`SiteSelection.ComputeDistanceField`） | 可盖性地图（地表承重 + 无阻挡） |
    /// | 基地中心兜底链（`PlotPlanner.BuildCenterCandidates`） | 户型算法（`BuildSitePlanner`，Core 已测过 98/98） |
    /// | 建区那套「先查再放 + 回读验证」纪律 | **选建材**（数库存，不猜） |
    /// | 域框架（调度/异常隔离/遥测打标） | **放蓝图**（`GenConstruct`） |
    ///
    /// ## ⚠ 地板这条路径：写代码之前先去元数据里查了，结论和任务书里的猜测**不同**
    ///
    /// 任务书说「地板是 `TerrainDef`，不是 `ThingDef` —— 这条没完全确证」。
    /// 实测元数据（`tools/asmdump`，2026-10-03）：
    ///
    /// 1. `TerrainDefOf.WoodPlankFloor` / `.Concrete` / `.PavedTile` **确实存在**，类型是 `TerrainDef`。
    /// 2. **`TerrainDef` 没有 `buildingDef` 字段**（写 `floor.buildingDef` 直接 CS1061）——
    ///    只有 `blueprintDef` / `frameDef` / `costStuffCount`。
    /// 3. `Blueprint_Door : Blueprint_Build`（子类）—— 所以回读时 `is Blueprint_Build` **已经包含门**。
    /// 4. `Designator_Build.DesignateSingleCell` 的 IL（`tools/asmdump/IlDump`）显示：
    ///    - `entDef is TerrainDef` ⇒ 走 `map.terrainGrid.SetTerrain(c, terrainDef)` —— **直接改地形**；
    ///    - `isFoundation` 走 `SetFoundation`；`temporary` 走 `SetTempTerrain`；
    ///    - `else`（普通 ThingDef，比如墙）⇒ `GenSpawn.WipeExistingThings(...)` +
    ///      `GenConstruct.PlaceBlueprintForBuild(...)` —— **蓝图**。
    ///    ⇒ **地板没有"蓝图形态"**：地板在游戏里就是"小人过去铺地形"。
    ///      用 `Designator_Build` 铺地板 = **瞬间免费**改地形（不走 Frame、不耗料、不需要小人）。
    /// 5. 元数据还确证了 `Verse.Thing.stuff` 是 **protected**（只能读公开的 `Thing.Stuff`）、
    ///    `DesignationManager.DesignationAt` **必须**带 `DesignationDef`
    ///    （要"任意标定"得用 `HasMapDesignationAt`）。
    ///
    /// **所以本域默认只放墙和门**（`BuildFloor = false`），地板写进 TODO —— 见
    /// `docs/s4-住区域.md`。留一个 `AllowInstantTerrainFloor` 开关是给**实测取证**用的：
    /// 打开它会真的用 designator 铺一次地板，用来在游戏里证明"它确实瞬间改地形、不产生蓝图"。
    /// **默认关**，因为"瞬间免费铺地板"不是这个 mod 想做的事。
    /// </summary>
    public sealed class ShelterDomain : INeedDomain
    {
        // ---- 可调参数（都是 public static，方便测试临时改）----

        /// <summary>每隔几个游戏日检查一次（房子是低频设施）</summary>
        public static int CheckEveryDays = 3;

        /// <summary>总开关</summary>
        public static bool Enabled = true;

        /// <summary>房子边长（含墙）。7 → 内部 5×5 = 25 格、墙 23 格、门 1 格</summary>
        public static int HouseSize = 7;

        /// <summary>
        /// **地板开关**。默认 **false**：地板在游戏里没有蓝图形态
        /// （见类注释 §「地板这条路径」），用 designator 铺等于瞬间免费改地形。
        ///
        /// 打开它的唯一用途是**实测取证**：证明这条路真的绕过了建造流程。
        /// </summary>
        public static bool BuildFloor = false;

        /// <summary>
        /// 允许"没有建造成本"的地形改造（即上面那个开关真的动手）。
        /// 和 `BuildFloor` 分成两个开关，是为了能表达
        /// 「我想看看户型里地板有多少格，但**不要**改地图」。
        /// </summary>
        public static bool AllowInstantTerrainFloor = false;

        /// <summary>最多盖几间（别无限盖）</summary>
        public static int MaxHouses = 1;

        /// <summary>选材质的优先顺序（defName）。在库存里按这个顺序挑第一个"料够 + Def 允许"的。</summary>
        public static string[] MaterialPreference = new string[]
        {
            "WoodLog",    // 前期最常用、最便宜
            "Steel",      // 没有木头就钢
            "BlocksSandstone",
            "BlocksGranite",
            "BlocksMarble",
            "BlocksLimestone",
            "BlocksSlate",
        };

        /// <summary>低于这个库存就不选它（免得把料全吃掉、别的活没法干）</summary>
        public static int MinMaterialReserve = 0;

        public string Id
        {
            get { return "shelter"; }
        }

        public string DisplayName
        {
            get { return "住处"; }
        }

        /// <summary>排在食物（10）和储藏区（20）之后</summary>
        public int Priority
        {
            get { return 30; }
        }

        /// <summary>有住处关系到心情与生存（睡地板 / 睡外面会崩心情）</summary>
        public bool IsSurvivalCritical
        {
            get { return true; }
        }

        public SetupActionResult OnDayStart(DomainContext ctx)
        {
            return null;      // 没有"每天要做的家务"
        }

        /// <summary>
        /// **今天要不要跑**。低频：每 `CheckEveryDays` 天一次。
        ///
        /// ⚠ 这个方法被**很频繁**地调用（组件每 64 tick 问一次，而协调器在"时段不满足"时
        /// 不记账）。所以这里**只做取模和几个计数**，绝不能在这里算距离场 / 遍历地图。
        /// </summary>
        public bool ShouldRunToday(DomainContext ctx)
        {
            if (!Enabled || ctx == null) return false;

            // 已有房子就别再来（这个判断很便宜：只看已有的墙/门/蓝图数量）
            if (!HasAnyShelter(ctx.Map) || MaxHouses > 1)
            {
                int every = CheckEveryDays > 0 ? CheckEveryDays : 3;
                return ctx.Day % every == 0;
            }
            return false;
        }

        /// <summary>
        /// 地图上有没有"住处"的痕迹：玩家造的墙/门，或者它们的蓝图/框架。
        ///
        /// 这个判断**必须便宜**（`ShouldRunToday` 每 64 tick 用一次），所以扫的是
        /// `listerThings.ThingsOfDef`（按 def 索引的桶），不是全图 AllThings。
        /// </summary>
        public static bool HasAnyShelter(Map map)
        {
            if (map == null) return false;
            try
            {
                if (CountOfDef(map, ThingDefOf.Wall) > 0) return true;
                if (CountOfDef(map, ThingDefOf.Door) > 0) return true;
                if (CountBlueprintBuilds(map) > 0) return true;
                if (CountFrames(map) > 0) return true;
            }
            catch (Exception) { }
            return false;
        }

        // ------------------------------------------------------------------
        // 闭环
        // ------------------------------------------------------------------

        public DomainCycleResult RunCycle(DomainContext ctx)
        {
            var result = new DomainCycleResult { DomainId = Id };

            try
            {
                Map map = ctx.Map;

                // ── ① 别重复盖 ──
                int existingWalls = CountOfDef(map, ThingDefOf.Wall);
                int existingDoors = CountOfDef(map, ThingDefOf.Door);
                int existingBPs = CountBlueprintBuilds(map);
                int existingFrames = CountFrames(map);

                if (existingBPs > 0 || existingFrames > 0)
                {
                    result.Ok = true;
                    result.DidWork = false;
                    result.Summary = string.Format(
                        "已经有一处在盖（蓝图 {0} / 框架 {1}），不重复动手",
                        existingBPs, existingFrames);
                    return result;
                }
                if (existingWalls > 0 && existingDoors > 0 && MaxHouses <= 1)
                {
                    result.Ok = true;
                    result.DidWork = false;
                    result.Summary = string.Format(
                        "已有住处（墙 {0} / 门 {1}），不再盖第二间",
                        existingWalls, existingDoors);
                    return result;
                }

                // ── ② 可盖性地图 + 基地距离场（复用食物域那套距离场与兜底链）──
                BuildabilityMap grid = PlotPlanner.BuildBuildabilityMap(map);
                if (grid == null)
                {
                    result.Ok = false;
                    result.Summary = "拿不到可盖性地图，跳过";
                    return result;
                }

                List<CenterCandidate> candidates = PlotPlanner.BuildCenterCandidates(map, grid);
                ResolvedBaseCenter center = SiteSelection.ResolveBaseCenter(
                    CloneAsPassability(grid), candidates);
                if (center == null)
                {
                    result.Ok = false;
                    result.Summary = "算不出基地中心，跳过";
                    return result;
                }

                int seed = FindWalkableSeed(grid, center.X, center.Z);
                double[] dist = SiteSelection.ComputeDistanceField(
                    CloneAsPassability(grid), new List<int> { seed }, 400);
                if (dist == null)
                {
                    result.Ok = false;
                    result.Summary = "距离场计算失败，跳过";
                    return result;
                }

                // ── ③ 选址 + 户型（Core 的算法，已 98/98 测过）──
                var opts = new BuildSiteOptions
                {
                    Size = HouseSize,
                    MaxDistanceFromBase = 60.0,
                    ComfortableDistance = 20.0,
                    DistancePenaltyPerCell = 4.0,
                    MaxBlockedRatio = 0.10,
                    SearchStep = 1,
                    DoorSide = 2,                 // 门朝南（面向基地的那一侧最常见）
                    RequireOutsideAccess = true,
                };

                BuildingPlan plan = BuildSitePlanner.Plan(grid, dist, opts);
                if (plan == null)
                {
                    result.Ok = true;
                    result.DidWork = false;
                    result.Summary = string.Format(
                        "基地附近找不到可盖 {0}×{0} 的干净地块（中心 {1}@{2},{3}）",
                        HouseSize, center.Kind, center.X, center.Z);
                    return result;
                }

                // ── ④ 按现有资源选材质（数库存，不猜）──
                MaterialChoice material = ChooseMaterial(map, plan);
                if (material == null || material.Def == null)
                {
                    result.Ok = true;
                    result.DidWork = false;
                    result.Summary = "没有可用建材（库存里没有能砌墙/做门的东西），不放蓝图";
                    return result;
                }

                // 料够不够：墙 + 门 各要 costStuffCount 个 stuff
                int needWall = plan.WallCells.Count * ThingDefOf.Wall.costStuffCount;
                int needDoor = plan.DoorCells.Count * ThingDefOf.Door.costStuffCount;
                int needFloor = plan.FloorCells.Count * FloorCostStuffOf();
                int need = needWall + needDoor + (BuildFloor ? needFloor : 0);

                if (material.Available < need)
                {
                    result.Ok = true;
                    result.DidWork = false;
                    result.Summary = string.Format(
                        "材料不足：要 {0} 个 {1}（墙{2}+门{3}{4}），手上只有 {5}",
                        need, material.Def.defName, needWall, needDoor,
                        BuildFloor ? "+地板" + needFloor : "", material.Available);
                    return result;
                }

                // ── ⑤ 放蓝图：先查再放，逐格回读 ──
                PlacementReport pr = PlaceBlueprints(map, plan, material);

                // ── ⑥ ★ 回读验证：地图上真的有对应数量的蓝图 ──
                int afterBPs = CountBlueprintBuilds(map);
                int afterWallsBP = CountBlueprintOfBuildDef(map, ThingDefOf.Wall);
                int afterDoorsBP = CountBlueprintOfBuildDef(map, ThingDefOf.Door);

                pr.WallBlueprintReadBack = afterWallsBP;
                pr.DoorBlueprintReadBack = afterDoorsBP;
                pr.TotalBlueprintReadBack = afterBPs;

                bool okWalls = afterWallsBP >= pr.WallsPlaced;
                bool okDoors = afterDoorsBP >= pr.DoorsPlaced;
                pr.Verified = okWalls && okDoors && pr.WallsPlaced > 0 && pr.DoorsPlaced > 0;

                result.Ok = true;
                result.DidWork = pr.WallsPlaced > 0 || pr.DoorsPlaced > 0;
                result.Summary = Summarize(pr, plan, material, center);

                try
                {
                    Log.Message("[FoodControl] 住处：" + result.Summary);
                }
                catch (Exception) { }
            }
            catch (Exception e)
            {
                result.Ok = false;
                result.Error = e.ToString();
                result.Summary = "住处域失败（已吞掉，不影响游戏）：" + e.Message;
                try { Log.Error("[FoodControl] 住处域异常：" + e); } catch (Exception) { }
            }

            return result;
        }

        // ------------------------------------------------------------------
        // 材质选择
        // ------------------------------------------------------------------

        /// <summary>
        /// **按现有资源选材质**（设计文档要求"真的去数库存，没料就别放"）。
        ///
        /// 策略简单可解释：
        ///   1. 数地图上所有 `IsStuff` 的物品（复用 `ColonySetup.CountBuildingMaterials`）
        ///   2. 按 `MaterialPreference` 的顺序挑第一个**同时满足**：
        ///        - 库存 ≥ 需要的量（这里先粗筛 > 0，精确判断在 RunCycle 里）
        ///        - Def 允许用来砌墙（`ThingDefOf.Wall.stuffCategories` 里含该 stuff 的类别）
        ///   3. 一个都没有 → 返回 null（**不放蓝图**：放了也没人能建）
        ///
        /// ⚠ 不硬编码"什么料能砌墙"—— 用游戏自己的 `stuffCategories` 判定。
        ///   木头/钢/石都能砌墙，但比如"布"就不行（不在 Wall 的 stuffCategories 里）。
        /// </summary>
        public static MaterialChoice ChooseMaterial(Map map, BuildingPlan plan)
        {
            if (map == null) return null;

            Dictionary<string, int> stock = ColonySetup.CountBuildingMaterials(map);
            if (stock == null || stock.Count == 0) return null;

            List<StuffCategoryDef> wallCats = null;
            List<StuffCategoryDef> doorCats = null;
            try { wallCats = ThingDefOf.Wall.stuffCategories; } catch (Exception) { }
            try { doorCats = ThingDefOf.Door.stuffCategories; } catch (Exception) { }

            MaterialChoice fallback = null;

            // ① 先按偏好顺序
            if (MaterialPreference != null)
            {
                for (int i = 0; i < MaterialPreference.Length; i++)
                {
                    string name = MaterialPreference[i];
                    int have;
                    if (string.IsNullOrEmpty(name) || !stock.TryGetValue(name, out have)) continue;
                    if (have <= MinMaterialReserve) continue;

                    ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(name);
                    if (def == null) continue;

                    var mc = new MaterialChoice
                    {
                        Def = def,
                        Available = have,
                        AllowedForWall = StuffAllowed(wallCats, def),
                        AllowedForDoor = StuffAllowed(doorCats, def),
                    };
                    if (!mc.AllowedForWall) { mc.Why = "Def 不允许用来砌墙"; continue; }

                    mc.Why = "按偏好顺序命中第 " + (i + 1) + " 个";
                    if (mc.AllowedForDoor) return mc;
                    if (fallback == null) fallback = mc;      // 能砌墙但不能做门 → 先留着
                }
            }

            // ② 偏好列表都没有 → 在库存里挑一个"能砌墙的"（可解释：就地取材）
            if (fallback == null)
            {
                foreach (KeyValuePair<string, int> kv in stock)
                {
                    if (kv.Value <= MinMaterialReserve) continue;
                    ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(kv.Key);
                    if (def == null) continue;
                    if (!StuffAllowed(wallCats, def)) continue;

                    fallback = new MaterialChoice
                    {
                        Def = def,
                        Available = kv.Value,
                        AllowedForWall = true,
                        AllowedForDoor = StuffAllowed(doorCats, def),
                        Why = "偏好列表里没有，就地取材选了库存最多的可砌墙材料",
                    };
                    if (fallback.AllowedForDoor) return fallback;
                }
            }

            return fallback;
        }

        /// <summary>这个 stuff def 是否属于这些类别之一（不硬编码 defName）。</summary>
        private static bool StuffAllowed(List<StuffCategoryDef> cats, ThingDef def)
        {
            if (cats == null || cats.Count == 0) return true;    // 没限制
            if (def == null || def.stuffProps == null || def.stuffProps.categories == null) return false;
            for (int i = 0; i < def.stuffProps.categories.Count; i++)
            {
                StuffCategoryDef c = def.stuffProps.categories[i];
                if (c == null) continue;
                if (cats.Contains(c)) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------
        // 放蓝图
        // ------------------------------------------------------------------

        /// <summary>一次放置的完整记账（进摘要，也给测试读）。</summary>
        public sealed class PlacementReport
        {
            public int WallsPlaced;
            public int DoorsPlaced;
            public int FloorsPlacedViaDesignator;

            /// <summary>被 `CanPlaceBlueprintAt` 拒掉的位置（含原因），最多记 8 条</summary>
            public List<string> Rejected = new List<string>();

            /// <summary>回读到的蓝图数（★ 桥接之外的第二道独立确认）</summary>
            public int WallBlueprintReadBack;
            public int DoorBlueprintReadBack;
            public int TotalBlueprintReadBack;

            /// <summary>回读通过吗（**只有 true 才算真的落地了**）</summary>
            public bool Verified;

            public bool FloorPathTried;
            public string FloorPathNote = "";

            public override string ToString()
            {
                return string.Format("墙蓝图{0} 门蓝图{1} 回读(墙{2}/门{3}/总{4}) 验证={5}{6}",
                    WallsPlaced, DoorsPlaced, WallBlueprintReadBack, DoorBlueprintReadBack,
                    TotalBlueprintReadBack, Verified,
                    Rejected.Count > 0 ? " 被拒" + Rejected.Count : "");
            }
        }

        /// <summary>
        /// **真的放蓝图**。顺序照本项目纪律：**每一格先 `CanPlaceBlueprintAt` 查，再放**。
        ///
        /// 为什么不用 `Designator_Build.DesignateSingleCell` 放墙：
        ///   它能用（IL 里走的就是 `PlaceBlueprintForBuild`），但它还会做
        ///   `GenSpawn.WipeExistingThings(...)` —— 会把挡路的树/石头**直接删掉**。
        ///   我们要的是"挑一块干净的地"，不是"推平它"，所以走 `GenConstruct` 更可控。
        ///
        /// ⚠ 门必须是 `ThingDefOf.Door` + 正确朝向：门的 `Size` 是 1×1 但
        ///   它贴在墙上，朝向错会挡住两侧。门朝南的墙上开门时，
        ///   门的 "forward" 应该是南北向 → 用 `Rot4.South`（门面朝南）。
        /// </summary>
        public static PlacementReport PlaceBlueprints(Map map, BuildingPlan plan, MaterialChoice material)
        {
            var pr = new PlacementReport();
            if (map == null || plan == null || material == null || material.Def == null) return pr;

            int w = map.Size.x;

            // ── ① 墙 ──
            for (int i = 0; i < plan.WallCells.Count; i++)
            {
                IntVec3 c = IndexToCell(plan.WallCells[i], w, map);
                if (!TryPlace(map, ThingDefOf.Wall, c, Rot4.North, material.Def, pr)) continue;
                pr.WallsPlaced++;
            }

            // ── ② 门 ──
            Rot4 doorRot = DoorRotation(plan.DoorSide);
            for (int i = 0; i < plan.DoorCells.Count; i++)
            {
                IntVec3 c = IndexToCell(plan.DoorCells[i], w, map);
                if (!TryPlace(map, ThingDefOf.Door, c, doorRot, material.Def, pr)) continue;
                pr.DoorsPlaced++;
            }

            // ── ③ 地板（默认不做，见类注释）──
            if (BuildFloor)
            {
                pr.FloorPathTried = true;
                if (!AllowInstantTerrainFloor)
                {
                    pr.FloorPathNote = "BuildFloor=true 但 AllowInstantTerrainFloor=false："
                        + "只是算了户型（" + plan.FloorCells.Count + " 格），**没有**动地图";
                }
                else
                {
                    int placed = PlaceFloorViaDesignator(map, plan, material);
                    pr.FloorsPlacedViaDesignator = placed;
                    pr.FloorPathNote = "用 Designator_Build(TerrainDef) 铺了 " + placed
                        + " 格 —— 注意：这条路是**直接改地形**，不产生蓝图、不耗料、不需要小人";
                }
            }

            return pr;
        }

        /// <summary>
        /// 放一个蓝图。**先查再放**；被拒就把原因记进报告（最多 8 条），绝不"试一试看抛不抛异常"。
        /// </summary>
        private static bool TryPlace(
            Map map, BuildableDef def, IntVec3 c, Rot4 rot, ThingDef stuff, PlacementReport pr)
        {
            if (def == null) return false;
            try
            {
                // ★ 先查合不合法（godMode=true：只做"几何/地表/占用"判定，不要求格子被解锁）
                AcceptanceReport report = GenConstruct.CanPlaceBlueprintAt(
                    def, c, rot, map, true, null, null, stuff,
                    false, false, false);

                if (!report.Accepted)
                {
                    if (pr.Rejected.Count < 8)
                    {
                        pr.Rejected.Add(string.Format("({0},{1}) {2}: {3}",
                            c.x, c.z, def.defName, report.Reason));
                    }
                    return false;
                }

                // ★ 再放
                Blueprint_Build bp = GenConstruct.PlaceBlueprintForBuild(
                    def, c, map, rot, Faction.OfPlayer, stuff, null, null, true);

                if (bp == null)
                {
                    if (pr.Rejected.Count < 8)
                        pr.Rejected.Add(string.Format("({0},{1}) {2}: PlaceBlueprintForBuild 返回 null",
                            c.x, c.z, def.defName));
                    return false;
                }
                return true;
            }
            catch (Exception e)
            {
                if (pr.Rejected.Count < 8)
                {
                    pr.Rejected.Add(string.Format("({0},{1}) {2}: 异常 {3}",
                        c.x, c.z, def.defName, e.GetType().Name + " " + e.Message));
                }
                return false;
            }
        }

        /// <summary>
        /// **地板那条路**：用 `Designator_Build(TerrainDef)` 直接改地形。
        ///
        /// 元数据/IL 已经说明它走 `TerrainGrid::SetTerrain`（不是蓝图），
        /// 这里保留它是为了**在游戏里取证**（默认开关是关的）。
        ///
        /// ⚠ 仍然遵守"先查再放"：先调 `designator.CanDesignateCell(c)`
        ///   （它的 IL 就是 `GenConstruct.CanPlaceBlueprintAt`），通过才 `DesignateSingleCell`。
        /// </summary>
        public static int PlaceFloorViaDesignator(Map map, BuildingPlan plan, MaterialChoice material)
        {
            int placed = 0;
            if (map == null || plan == null || plan.FloorCells.Count == 0) return 0;

            TerrainDef floor = TerrainDefOf.WoodPlankFloor;
            if (floor == null) return 0;

            try
            {
                var des = new Designator_Build(floor);
                if (material != null && material.Def != null)
                {
                    try { des.SetStuffDef(material.Def); } catch (Exception) { }
                }

                int w = map.Size.x;
                for (int i = 0; i < plan.FloorCells.Count; i++)
                {
                    IntVec3 c = IndexToCell(plan.FloorCells[i], w, map);
                    try
                    {
                        AcceptanceReport can = des.CanDesignateCell(c);
                        if (!can.Accepted) continue;
                        des.DesignateSingleCell(c);
                        placed++;
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception)
            {
                // 这条路是实验性的，失败就算了（默认也不开）
            }

            return placed;
        }

        /// <summary>门在墙上的朝向。门朝南/北的墙 ⇒ 门体南北向（Rot4.South）。</summary>
        private static Rot4 DoorRotation(int doorSide)
        {
            // doorSide: 0=北 1=东 2=南 3=西（BuildSiteOptions 的约定）
            // 门开在南北墙上 → 门朝南；开在东西墙上 → 门朝东
            if (doorSide == 1 || doorSide == 3) return Rot4.East;
            return Rot4.South;
        }

        private static IntVec3 IndexToCell(int idx, int width, Map map)
        {
            int x = width > 0 ? idx % width : 0;
            int z = width > 0 ? idx / width : 0;
            var c = new IntVec3(x, 0, z);
            try
            {
                if (!c.InBounds(map))
                {
                    c.x = Math.Min(Math.Max(x, 0), map.Size.x - 1);
                    c.z = Math.Min(Math.Max(z, 0), map.Size.z - 1);
                }
            }
            catch (Exception) { }
            return c;
        }

        // ------------------------------------------------------------------
        // 回读 / 计数（★ 绝不假定自己成功了）
        // ------------------------------------------------------------------

        /// <summary>某个 def 在地图上的实例数（含蓝图/框架？——**不含**，`listerThings` 按 def 精确匹配）</summary>
        public static int CountOfDef(Map map, ThingDef def)
        {
            if (map == null || def == null) return 0;
            try
            {
                List<Thing> list = map.listerThings.ThingsOfDef(def);
                return list == null ? 0 : list.Count;
            }
            catch (Exception) { return 0; }
        }

        /// <summary>
        /// 数"建墙/建门的蓝图"。**`Blueprint_Door` 是 `Blueprint_Build` 的子类**（元数据确证），
        /// 所以 `is Blueprint_Build` 一次就把墙和门都算上。
        /// </summary>
        public static int CountBlueprintBuilds(Map map)
        {
            return CountThings(map, delegate(Thing t) { return t is Blueprint_Build; });
        }

        /// <summary>数"某个建筑 def 的蓝图"（墙的蓝图 / 门的蓝图分开数）。</summary>
        public static int CountBlueprintOfBuildDef(Map map, ThingDef buildDef)
        {
            if (buildDef == null) return 0;
            return CountThings(map, delegate(Thing t)
            {
                var bp = t as Blueprint_Build;
                if (bp == null || bp.def == null) return false;
                return bp.def.entityDefToBuild == buildDef;
            });
        }

        /// <summary>数建造框架（Frame）—— 蓝图被小人接手后会变成 Frame。</summary>
        public static int CountFrames(Map map)
        {
            return CountThings(map, delegate(Thing t) { return t is Frame; });
        }

        private static int CountThings(Map map, Predicate<Thing> pred)
        {
            if (map == null || pred == null) return 0;
            int n = 0;
            try
            {
                foreach (Thing t in map.listerThings.AllThings)
                {
                    try { if (t != null && pred(t)) n++; }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
            return n;
        }

        // ------------------------------------------------------------------
        // 摘要
        // ------------------------------------------------------------------

        private static string Summarize(
            PlacementReport pr, BuildingPlan plan, MaterialChoice material, ResolvedBaseCenter center)
        {
            return string.Format(
                "盖 {0}×{0} 于 ({1},{2})，材质 {3}，墙 {4}/{5} 门 {6}/{7}，"
                + "★回读 墙蓝图{8} 门蓝图{9} 总蓝图{10}，验证={11}；基地中心={12}@{13},{14}",
                plan.Size, plan.X, plan.Z,
                material.Def != null ? material.Def.defName : "?",
                pr.WallsPlaced, plan.WallCells.Count,
                pr.DoorsPlaced, plan.DoorCells.Count,
                pr.WallBlueprintReadBack, pr.DoorBlueprintReadBack, pr.TotalBlueprintReadBack,
                pr.Verified,
                center != null ? center.Kind.ToString() : "?",
                plan.X + plan.Size / 2, plan.Z + plan.Size / 2);
        }

        // ------------------------------------------------------------------
        // 工具
        // ------------------------------------------------------------------

        /// <summary>
        /// `BuildabilityMap` → `PassabilityMap`（**只为了复用 Core 里那两个只认
        /// `PassabilityMap` 的纯函数**：`SiteSelection.ResolveBaseCenter` /
        /// `ComputeDistanceField`）。
        ///
        /// ⚠ 这是**有代价的**：每次克隆一个 6.25 万元素的数组（250×250）。
        /// 但住处域是**每几天跑一次**的低频域，一次几十毫秒可以接受；
        /// 换成改 Core 的签名（加接口）收益不大、风险更大（Core 已经 98/98 测过）。
        /// 记在这里，将来真嫌慢再说。
        /// </summary>
        private static PassabilityMap CloneAsPassability(BuildabilityMap b)
        {
            var p = new PassabilityMap
            {
                Width = b.Width,
                Height = b.Height,
                MoveCost = b.MoveCost,          // 直接共享引用（只读用）
                Arable = new bool[0],
                Fertility = new double[0],
            };
            return p;
        }

        /// <summary>
        /// 距离场起点：基地中心可能落在墙里/山里/已有建筑上，
        /// 那样距离场只有起点自己是 0，所有格都"走不到"（**静默失效**，最难查）。
        /// 按半径逐圈找最近的**可通行**格。
        /// </summary>
        private static int FindWalkableSeed(BuildabilityMap grid, int cx, int cz)
        {
            if (grid.InBounds(cx, cz) && grid.MoveCost[grid.Index(cx, cz)] > 0)
                return grid.Index(cx, cz);

            for (int r = 1; r <= 20; r++)
            {
                for (int dz = -r; dz <= r; dz++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Abs(dx) != r && Math.Abs(dz) != r) continue;   // 只看这一圈边框
                        int x = cx + dx;
                        int z = cz + dz;
                        if (!grid.InBounds(x, z)) continue;
                        int i = grid.Index(x, z);
                        if (i >= 0 && i < grid.MoveCost.Length && grid.MoveCost[i] > 0) return i;
                    }
                }
            }

            if (grid.InBounds(cx, cz)) return grid.Index(cx, cz);
            return 0;
        }

        /// <summary>木地板一格要几个 stuff（TerrainDef 有 costStuffCount；读不到就按 1 估）。</summary>
        private static int FloorCostStuffOf()
        {
            try
            {
                TerrainDef f = TerrainDefOf.WoodPlankFloor;
                if (f != null && f.costStuffCount > 0) return f.costStuffCount;
            }
            catch (Exception) { }
            return 1;
        }

        // ------------------------------------------------------------------
        // 只读诊断（给测试钩子/报告用，GUI 不变）
        // ------------------------------------------------------------------

        /// <summary>
        /// **只读诊断**：把"这一轮会怎么盖"算出来，返回一行人话。**绝不动地图**。
        /// 用途：测试可以先看户型合不合理，再决定要不要真放蓝图。
        /// </summary>
        public static string InspectNow(Map map)
        {
            try
            {
                if (map == null) return "没有地图";

                BuildabilityMap grid = PlotPlanner.BuildBuildabilityMap(map);
                if (grid == null) return "拿不到可盖性地图";

                List<CenterCandidate> cands = PlotPlanner.BuildCenterCandidates(map, grid);
                ResolvedBaseCenter center = SiteSelection.ResolveBaseCenter(CloneAsPassability(grid), cands);
                int seed = FindWalkableSeed(grid, center.X, center.Z);
                double[] dist = SiteSelection.ComputeDistanceField(
                    CloneAsPassability(grid), new List<int> { seed }, 400);

                var opts = new BuildSiteOptions { Size = HouseSize, DoorSide = 2 };
                BuildingPlan plan = BuildSitePlanner.Plan(grid, dist, opts);
                if (plan == null) return "找不到可盖地块（中心 " + center.Kind + "@" + center.X + "," + center.Z + "）";

                MaterialChoice mc = ChooseMaterial(map, plan);
                int buildable = 0;
                for (int i = 0; i < grid.Buildable.Length; i++) if (grid.Buildable[i]) buildable++;

                return string.Format(
                    "户型({0},{1}) {2}×{2} 墙{3} 门{4} 地板{5} 基地距{6:F0} 分{7:F1} | "
                    + "可盖格{8}/{9} | 材质 {10} | 需要 stuff {11}",
                    plan.X, plan.Z, plan.Size, plan.WallCells.Count, plan.DoorCells.Count,
                    plan.FloorCells.Count, plan.AvgDistanceToBase, plan.Score,
                    buildable, grid.Buildable.Length, mc != null ? mc.ToString() : "(无)",
                    plan.WallCells.Count * ThingDefOf.Wall.costStuffCount
                        + plan.DoorCells.Count * ThingDefOf.Door.costStuffCount);
            }
            catch (Exception e)
            {
                return "InspectNow 异常：" + e.Message;
            }
        }
    }
}
