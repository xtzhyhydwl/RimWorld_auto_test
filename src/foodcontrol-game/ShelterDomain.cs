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
    /// ## ★★ 地板这条路径：我读 IL 得出的结论是**错的**，实测推翻了它（值得留档）
    ///
    /// **第一阶段（错）**：我拿 `tools/asmdump/IlDump.exe` 反汇编
    /// `Designator_Build.DesignateSingleCell`，看到的跳转是
    /// ```
    /// IL_008E: brtrue.s IL_00AF      ← 我以为"godMode 成立就跳到地形分支"
    /// IL_00AB: bne.un   IL_0202
    /// IL_00B0: ... isinst TerrainDef ... TerrainGrid::SetTerrain
    /// ```
    /// 于是我在类注释里写下结论：**「地板是 `TerrainDef`，走 `terrainGrid.SetTerrain`，
    /// 是瞬间免费改地形，没有蓝图形态」**，并把 `BuildFloor` 默认关掉。
    ///
    /// **第二阶段（实测打脸）**：`foodprobe/floor_path` 真的跑了一次，返回
    /// ```
    /// "terrain_blueprintDef": "Blueprint_WoodPlankFloor",   ← 不是 null！
    /// "hasBlueprintForm": true,
    /// "blueprintDelta": 3,  "terrainAreaBefore/After": 0 → 0
    /// ```
    /// 也就是说：**地形没变、蓝图 +3**。地板**就是蓝图**，和墙门走的是同一条路。
    ///
    /// **根因（自己工具的 off-by-one）**：`IlDump.cs` 的 `ShortInlineBrTarget` 分支
    /// 把跳转目标算成了 `操作数起点 + d`，而 CLI 的定义是 `操作数起点 + 1 + d`。
    /// 于是**每一个条件跳转的目标都指向"操作数中间"**，`brtrue.s IL_00B0` 被打印成
    /// `brtrue.s IL_00AF` —— **条件成立时走哪条路被读反了**。
    /// 修好工具后重看，真实结构是：
    /// ```
    /// IL_0089: ldsfld godMode
    /// IL_008E: brtrue.s IL_00B0      ← godMode = true 才走地形分支
    /// IL_0090: ... GetStatValueAbstract(WorkToBuild)   ← 正常模式会走到这里
    /// IL_00AB: bne.un   IL_0202      ← WorkToBuild != 0 → ★ 走蓝图分支
    /// IL_00B0: ... isinst TerrainDef ... TerrainGrid::SetTerrain   ← 只有 WorkToBuild == 0 的无本建造才走
    /// ```
    /// **结论**：`WorkToBuild != 0` 的地板（木地板/混凝土地砖都是）走
    /// **`GenSpawn.WipeExistingThings` + `GenConstruct.PlaceBlueprintForBuild`** —— 和墙一模一样。
    ///
    /// **三条教训**（写进 `docs/s4-住区域.md`）：
    ///   1. **元数据/IL 只是旁证，行为必须实测。** 我拿 IL 当"确证"用了，代价是编出一套错结论。
    ///   2. **工具本身也会错，而且错了会让"确证"变成"反向确证"。** 发现"IL 说不通但实测有结果"时，
    ///      第一反应应该是**怀疑工具**，而不是怀疑游戏。
    ///   3. **断言要对着数据写。** 我那条 `terrain.blueprintDef == null` 的断言，
    ///      在回读已经打印出 `Blueprint_WoodPlankFloor` 的情况下还判"失败"，
    ///      等于把"预期错、实际对"记成了 bug —— 报告和数据自相矛盾早该被发现。
    ///
    /// **最终做法**：`BuildFloor` **默认 true**，地板走 `GenConstruct.PlaceBlueprintForBuild`
    /// （和墙门同一条路：先查再放、耗料、要小人来铺）。
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
        /// **地板开关**。默认 **true**：木质/混凝土地板都有 `blueprintDef`
        /// （实测 `TerrainDefOf.WoodPlankFloor.blueprintDef == Blueprint_WoodPlankFloor`），
        /// 走的是和墙门完全一样的 `GenConstruct.PlaceBlueprintForBuild` 路径。
        /// </summary>
        public static bool BuildFloor = true;

        /// <summary>
        /// 用哪个地形当地板。默认木地板（最便宜、前期就有）。
        /// </summary>
        public static string FloorTerrainDefName = "WoodPlankFloor";

        /// <summary>
        /// **测试专用**：强制盖房，忽略"已有住处/已有蓝图"的守卫。
        ///
        /// 为什么需要：debug 开局自带 662 面墙 + 28 扇门，正常守卫会正确地拒绝盖房，
        /// 于是**验收永远看不到 23+1 个蓝图**。这个开关只给测试用。
        ///
        /// ⚠ 生产路径必须保持 false（默认就是 false）。
        /// </summary>
        public static bool ForceOverride = false;

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

            // ⚠ `ForceOverride`（测试专用）也要在这里生效：否则"已经盖过"之后，
            //   连"今天要不要跑"这道门都过不去，force 钩子就白设了。
            if (ForceOverride) return true;

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
                if (CountBlueprintBuilds(map) > 0) return true;
                if (CountFrames(map) > 0) return true;
                // ⚠ 只在"我们自己盖过"的前提下才拿"有墙有门"当住处证据。
                //   原因（实测）：**debug 开局自带 662 面墙 + 28 扇门**，
                //   无条件把"有墙有门"当住处 ⇒ 正常路径永远拒绝动手，验收也就永远看不到蓝图。
                //   野外遗迹同样会误伤，所以这里必须限定"我们盖过"。
                if (_placedOnce)
                {
                    if (CountOfDef(map, ThingDefOf.Wall) > 0) return true;
                    if (CountOfDef(map, ThingDefOf.Door) > 0) return true;
                }
            }
            catch (Exception) { }
            return false;
        }

        /// <summary>本会话里**我们自己**放过至少一次住处蓝图。
        ///
        /// 用途只有两个：① 让 `ShouldRunToday` 的"已有住处"守卫只在**真的盖过**之后生效
        /// （否则会被 debug 开局自带的几百面墙误伤）；
        /// ② 防止"蓝图被取消后无限重盖"。
        ///
        /// ⚠ 不存档：读档后这个标志会归零，于是会先看一眼现有蓝图再决定 —— 行为仍然正确。
        /// </summary>
        private static bool _placedOnce;

        /// <summary>测试用：清掉"我们盖过"的标记（配合 `ForceOverride`）。</summary>
        public static void ResetPlacedFlag()
        {
            _placedOnce = false;
        }

        // ------------------------------------------------------------------
        // 闭环
        // ------------------------------------------------------------------

        /// <summary>
        /// 最近一次放置的**详细记账**（JSON）。给测试**分清"我们放了几个"和"地图上有几个"**。
        ///
        /// 为什么需要它：我先前只看"地图上的总数"，看到 48 面墙蓝图对不上 23 面墙的户型，
        /// 花了很多时间怀疑循环写重了。**把"我们放了多少"单独记一笔**，
        /// 一眼就能看出差额是"别的东西也在放"还是"我们写重了"。
        /// </summary>
        public static string LastReportJson = "{\"note\":\"还没有跑过\"}";

        private static string BuildReportJson(PlacementReport pr, BuildingPlan plan, MaterialChoice mat)
        {
            if (pr == null) return "{\"note\":\"没有记账\"}";
            var sb = new System.Text.StringBuilder();
            sb.Append("{");
            sb.Append("\"wallsPlaced\":").Append(pr.WallsPlaced).Append(",");
            sb.Append("\"doorsPlaced\":").Append(pr.DoorsPlaced).Append(",");
            sb.Append("\"floorsPlaced\":").Append(pr.FloorsPlaced).Append(",");
            sb.Append("\"wallBlueprintReadBack\":").Append(pr.WallBlueprintReadBack).Append(",");
            sb.Append("\"doorBlueprintReadBack\":").Append(pr.DoorBlueprintReadBack).Append(",");
            sb.Append("\"floorBlueprintReadBack\":").Append(pr.FloorBlueprintReadBack).Append(",");
            sb.Append("\"totalBlueprintReadBack\":").Append(pr.TotalBlueprintReadBack).Append(",");
            sb.Append("\"verified\":").Append(pr.Verified ? "true" : "false").Append(",");
            sb.Append("\"floorPathTried\":").Append(pr.FloorPathTried ? "true" : "false").Append(",");
            sb.Append("\"floorPathNote\":\"").Append(Esc(pr.FloorPathNote)).Append("\",");
            sb.Append("\"rejectedCount\":").Append(pr.Rejected.Count).Append(",");
            sb.Append("\"rejected\":[");
            for (int i = 0; i < pr.Rejected.Count && i < 8; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append("\"").Append(Esc(pr.Rejected[i])).Append("\"");
            }
            sb.Append("],");
            if (plan != null)
            {
                sb.Append("\"plan\":{\"x\":").Append(plan.X).Append(",\"z\":").Append(plan.Z)
                  .Append(",\"size\":").Append(plan.Size)
                  .Append(",\"wallCells\":").Append(plan.WallCells.Count)
                  .Append(",\"doorCells\":").Append(plan.DoorCells.Count)
                  .Append(",\"floorCells\":").Append(plan.FloorCells.Count)
                  .Append("},");
            }
            if (mat != null)
            {
                sb.Append("\"material\":{\"def\":\"").Append(Esc(mat.Def != null ? mat.Def.defName : ""))
                  .Append("\",\"available\":").Append(mat.Available)
                  .Append(",\"allowedForWall\":").Append(mat.AllowedForWall ? "true" : "false")
                  .Append(",\"allowedForDoor\":").Append(mat.AllowedForDoor ? "true" : "false")
                  .Append(",\"why\":\"").Append(Esc(mat.Why)).Append("\"},");
            }
            sb.Append("\"floorTerrainDef\":\"").Append(Esc(FloorTerrainDefName)).Append("\",");
            sb.Append("\"buildFloor\":").Append(BuildFloor ? "true" : "false");
            sb.Append("}");
            return sb.ToString();
        }

        private static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "'").Replace("\n", " ").Replace("\r", "");
        }

        public DomainCycleResult RunCycle(DomainContext ctx)
        {
            var result = new DomainCycleResult { DomainId = Id };

            try
            {
                Map map = ctx.Map;

                // ── ① 别重复盖 ──
                //
                // ⚠ `ForceOverride` 是**测试专用**（默认 false）：debug 开局自带几百面墙，
                //   正常守卫会正确地拒绝动手，验收就永远看不到 23+1 个蓝图。
                //   ⚠⚠ 它必须把**两条**守卫都绕过 —— 我第一版只绕了"已有住处"那条，
                //   漏了"已有蓝图"那条，于是 force 路径在"上一轮留下的蓝图还在"时
                //   照样被拒（实测第二次跑 delta=+0，白跑一轮验证）。
                int existingWalls = CountOfDef(map, ThingDefOf.Wall);
                int existingDoors = CountOfDef(map, ThingDefOf.Door);
                int existingBPs = CountBlueprintBuilds(map);
                int existingFrames = CountFrames(map);

                if (!ForceOverride)
                {
                    if (existingBPs > 0 || existingFrames > 0)
                    {
                        result.Ok = true;
                        result.DidWork = false;
                        result.Summary = string.Format(
                            "已经有一处在盖（蓝图 {0} / 框架 {1}），不重复动手",
                            existingBPs, existingFrames);
                        return result;
                    }
                    // ★ 只有**我们自己盖过**之后，才把"地图上有墙有门"当成住处证据。
                    //   debug 开局自带 662 面墙 + 28 扇门，不加这个限定就会永远拒绝干活。
                    if (_placedOnce && existingWalls > 0 && existingDoors > 0 && MaxHouses <= 1)
                    {
                        result.Ok = true;
                        result.DidWork = false;
                        result.Summary = string.Format(
                            "已有住处（墙 {0} / 门 {1}），不再盖第二间",
                            existingWalls, existingDoors);
                        return result;
                    }
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

                // 料够不够：墙 + 门 各要 costStuffCount 个 stuff；地板按 TerrainDef 的 costStuffCount
                //
                // ⚠ 实测：地板蓝图**不带 stuff**（byStuff 显示 "(无)"），
                //   它的用料走 `TerrainDef.costStuffCount`（木地板是 3）。
                //   所以这里要**分开算**，不能把地板也按"墙的 stuff"算。
                int needWall = plan.WallCells.Count * ThingDefOf.Wall.costStuffCount;
                int needDoor = plan.DoorCells.Count * ThingDefOf.Door.costStuffCount;
                int floorCostPerCell = FloorCostStuffOf();
                int needFloor = plan.FloorCells.Count * floorCostPerCell;
                int need = needWall + needDoor + (BuildFloor ? needFloor : 0);

                if (material.Available < needWall + needDoor)
                {
                    result.Ok = true;
                    result.DidWork = false;
                    result.Summary = string.Format(
                        "材料不足：墙门要 {0} 个 {1}（墙{2}+门{3}），手上只有 {4}"
                        + "（地板另需 {5} 个，未计）",
                        needWall + needDoor, material.Def.defName, needWall, needDoor,
                        material.Available, needFloor);
                    return result;
                }
                if (BuildFloor && material.Available < need)
                {
                    // 墙门够、地板不够 → **降级**：把墙门盖起来，地板先不放
                    // （总比什么都不盖好；理由写进摘要，别静默）
                    try { BuildFloor = false; } catch (Exception) { }
                }

                // ── ⑤ 放蓝图：先查再放，逐格回读 ──
                PlacementReport pr = PlaceBlueprints(map, plan, material);

                // ── ⑥ ★ 回读验证：地图上真的有对应数量的蓝图 ──
                int afterBPs = CountBlueprintBuilds(map);
                int afterWallsBP = CountBlueprintOfBuildDef(map, ThingDefOf.Wall);
                int afterDoorsBP = CountBlueprintOfBuildDef(map, ThingDefOf.Door);
                TerrainDef floorDef = ResolveFloorTerrainDef();
                int afterFloorsBP = floorDef != null ? CountBlueprintOfBuildDef(map, floorDef) : 0;

                pr.WallBlueprintReadBack = afterWallsBP;
                pr.DoorBlueprintReadBack = afterDoorsBP;
                pr.FloorBlueprintReadBack = afterFloorsBP;
                pr.TotalBlueprintReadBack = afterBPs;

                bool okWalls = afterWallsBP >= pr.WallsPlaced;
                bool okDoors = afterDoorsBP >= pr.DoorsPlaced;
                bool okFloors = !pr.FloorPathTried || BuildFloor == false || afterFloorsBP >= pr.FloorsPlaced;
                pr.Verified = okWalls && okDoors && okFloors && pr.WallsPlaced > 0 && pr.DoorsPlaced > 0;

                if (pr.Verified) _placedOnce = true;

                result.Ok = true;
                result.DidWork = pr.WallsPlaced > 0 || pr.DoorsPlaced > 0 || pr.FloorsPlaced > 0;
                result.Summary = Summarize(pr, plan, material, center);

                // ★ 详细记账落一份 JSON（给测试分清"我们放了几个" vs "地图上有几个"）
                LastReportJson = BuildReportJson(pr, plan, material);

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

            /// <summary>放出去的地板蓝图格数</summary>
            public int FloorsPlaced;

            /// <summary>被 `CanPlaceBlueprintAt` 拒掉的位置（含原因），最多记 8 条</summary>
            public List<string> Rejected = new List<string>();

            /// <summary>回读到的蓝图数（★ 桥接之外的第二道独立确认）</summary>
            public int WallBlueprintReadBack;
            public int DoorBlueprintReadBack;
            public int FloorBlueprintReadBack;
            public int TotalBlueprintReadBack;

            /// <summary>回读通过吗（**只有 true 才算真的落地了**）</summary>
            public bool Verified;

            public bool FloorPathTried;
            public string FloorPathNote = "";

            public override string ToString()
            {
                return string.Format("墙蓝图{0} 门蓝图{1} 地板蓝图{2} 回读(墙{3}/门{4}/地板{5}/总{6}) 验证={7}{8}",
                    WallsPlaced, DoorsPlaced, FloorsPlaced,
                    WallBlueprintReadBack, DoorBlueprintReadBack, FloorBlueprintReadBack,
                    TotalBlueprintReadBack, Verified,
                    Rejected.Count > 0 ? " 被拒" + Rejected.Count : "");
            }
        }

        /// <summary>
        /// **真的放蓝图**。顺序照本项目纪律：**每一格先 `CanPlaceBlueprintAt` 查，再放**。
        ///
        /// 墙 / 门 / 地板**三条都走 `GenConstruct.PlaceBlueprintForBuild`** ——
        /// 这是实测确认的（见类注释 §「地板这条路径」）：地板虽然 `def` 是 `TerrainDef`，
        /// 但它有 `blueprintDef == Blueprint_WoodPlankFloor`，放出来就是**蓝图**，
        /// 要耗料、要小人来铺，和墙完全一致。
        ///
        /// ⚠ 门必须有正确朝向：门的 `Size` 是 1×1，但它嵌在墙上，
        ///   转向错了会挡住门洞两侧。南北向的墙 ⇒ `Rot4.South`；东西向的墙 ⇒ `Rot4.East`。
        /// </summary>
        public static PlacementReport PlaceBlueprints(Map map, BuildingPlan plan, MaterialChoice material)
        {
            var pr = new PlacementReport();
            if (map == null || plan == null || material == null || material.Def == null) return pr;

            int w = map.Size.x;

            // ── ① 墙（北向：墙是 1×1，朝向无影响，但保持和 Designator_Build 一致）──
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

            // ── ③ 地板 ★（BuildFloor 默认 true；实测地板有蓝图形态）──
            if (BuildFloor)
            {
                pr.FloorPathTried = true;
                TerrainDef floor = ResolveFloorTerrainDef();
                if (floor == null)
                {
                    pr.FloorPathNote = "找不到地板 TerrainDef（" + FloorTerrainDefName + "），地板跳过";
                }
                else
                {
                    // ⚠ 地板**不带 stuff**：TerrainDef 的构造材料由它自己的 costStuffCount 决定，
                    //   传 stuff 反而会让 CanPlaceBlueprintAt / 蓝图语义对不上。
                    //   （实测 byStuff 里地板蓝图显示 "(无)"。）
                    for (int i = 0; i < plan.FloorCells.Count; i++)
                    {
                        IntVec3 c = IndexToCell(plan.FloorCells[i], w, map);
                        if (!TryPlace(map, floor, c, Rot4.North, null, pr)) continue;
                        pr.FloorsPlaced++;
                    }
                    pr.FloorPathNote = string.Format(
                        "用 GenConstruct.PlaceBlueprintForBuild({0}) 放了 {1}/{2} 格地板蓝图",
                        floor.defName, pr.FloorsPlaced, plan.FloorCells.Count);
                }
            }
            else
            {
                pr.FloorPathNote = "BuildFloor=false：只算了户型（" + plan.FloorCells.Count + " 格），没放地板蓝图";
            }

            return pr;
        }

        /// <summary>解析地板地形 Def（配置写错名字不该刷红字，找不到就返回 null 并降级）。</summary>
        public static TerrainDef ResolveFloorTerrainDef()
        {
            try
            {
                if (!string.IsNullOrEmpty(FloorTerrainDefName))
                {
                    TerrainDef d = DefDatabase<TerrainDef>.GetNamedSilentFail(FloorTerrainDefName);
                    if (d != null) return d;
                }
                return TerrainDefOf.WoodPlankFloor;
            }
            catch (Exception)
            {
                return null;
            }
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
        /// **地板那条路**：保留成"取证/兼容"入口。
        ///
        /// ⚠ 实测结论是**地板走 `GenConstruct.PlaceBlueprintForBuild` 也能出蓝图**
        ///   （见类注释），所以生产路径**不再用** `Designator_Build`。
        ///   这个函数留着是因为它是一条**合法的备用路**（`CanDesignateCell` 内部
        ///   就是 `GenConstruct.CanPlaceBlueprintAt`，也遵守"先查再放"），
        ///   将来若需要"覆盖已有地形/地基"这类语义可以走它。
        /// </summary>
        public static int PlaceFloorViaDesignator(Map map, BuildingPlan plan, MaterialChoice material)
        {
            int placed = 0;
            if (map == null || plan == null || plan.FloorCells.Count == 0) return 0;

            TerrainDef floor = ResolveFloorTerrainDef();
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
                // 备用路，失败就算了（生产不用它）
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

        /// <summary>
        /// 数"某个建造目标 def 的蓝图"（墙 / 门 / 地板分开数）。
        ///
        /// ⚠ 参数类型必须是 **`BuildableDef`**，不能是 `ThingDef`：
        ///   `Blueprint_Build.entityDefToBuild` 就是 `BuildableDef`，
        ///   而**地板的目标是 `TerrainDef`**（不是 ThingDef）。
        ///   实测踩过：写 `ThingDef` 会在传 `TerrainDef` 时 CS1503。
        /// </summary>
        public static int CountBlueprintOfBuildDef(Map map, BuildableDef buildDef)
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
                "盖 {0}×{0} 于 ({1},{2})，材质 {3}，墙 {4}/{5} 门 {6}/{7} 地板 {8}/{9}，"
                + "★回读 墙蓝图{10} 门蓝图{11} 地板蓝图{12} 总蓝图{13}，验证={14}；"
                + "基地中心={15}@{16},{17}{18}",
                plan.Size, plan.X, plan.Z,
                material.Def != null ? material.Def.defName : "?",
                pr.WallsPlaced, plan.WallCells.Count,
                pr.DoorsPlaced, plan.DoorCells.Count,
                pr.FloorsPlaced, plan.FloorCells.Count,
                pr.WallBlueprintReadBack, pr.DoorBlueprintReadBack, pr.FloorBlueprintReadBack,
                pr.TotalBlueprintReadBack,
                pr.Verified,
                center != null ? center.Kind.ToString() : "?",
                plan.X + plan.Size / 2, plan.Z + plan.Size / 2,
                string.IsNullOrEmpty(pr.FloorPathNote) ? "" : " | " + pr.FloorPathNote);
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

        /// <summary>
        /// 一格地板要几个 stuff。**读 `TerrainDef.costStuffCount`**，不硬编码。
        ///
        /// ⚠ 地板蓝图本身**不带 stuff**（实测 byStuff 显示 "(无)"）——
        ///   它的用料是"地形自己按 costStuffCount 算"，所以取料判断要单独走这里。
        /// </summary>
        private static int FloorCostStuffOf()
        {
            try
            {
                TerrainDef f = ResolveFloorTerrainDef();
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

                TerrainDef floor = ResolveFloorTerrainDef();
                int floorCost = FloorCostStuffOf();

                return string.Format(
                    "户型({0},{1}) {2}×{2} 墙{3} 门{4} 地板{5}({6}) 基地距{7:F0} 分{8:F1} | "
                    + "可盖格{9}/{10} | 材质 {11} | 需要 stuff {12}(墙门){13}(地板{14}格×{15})",
                    plan.X, plan.Z, plan.Size, plan.WallCells.Count, plan.DoorCells.Count,
                    plan.FloorCells.Count, floor != null ? floor.defName : "无地板",
                    plan.AvgDistanceToBase, plan.Score,
                    buildable, grid.Buildable.Length, mc != null ? mc.ToString() : "(无)",
                    plan.WallCells.Count * ThingDefOf.Wall.costStuffCount
                        + plan.DoorCells.Count * ThingDefOf.Door.costStuffCount,
                    BuildFloor ? plan.FloorCells.Count * floorCost : 0,
                    plan.FloorCells.Count, floorCost);
            }
            catch (Exception e)
            {
                return "InspectNow 异常：" + e.Message;
            }
        }
    }
}
