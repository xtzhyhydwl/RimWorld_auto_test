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
        /// <summary>
        /// 房子边长（含墙）。**13** —— 用户明确要求：
        /// 「最万能的建筑……13 乘 13，用墙去画一个 13 乘 13 的正方形」。
        /// 13×13 → 外墙 48 格（44 墙 + 4 门）、内部 11×11 = 121 格。
        /// </summary>
        public static int HouseSize = 13;

        /// <summary>
        /// **地板开关**。用户明确说「地板这个东西一般是**后边再考虑**……
        /// 先有墙壁，墙壁之后房顶小人自动去盖，盖好之后可能我们才考虑去建造地板」
        /// ⇒ **默认关**（但地板**确实有蓝图形态**，这是实测确证的，见类注释）。
        /// </summary>
        public static bool BuildFloor = false;

        /// <summary>
        /// 用哪个地形当地板（`BuildFloor` 打开时才用；默认木地板，最便宜、前期就有）。
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

        /// <summary>
        /// **选材质的偏好顺序**（defName）。
        ///
        /// ## ⚠ 用户明确要求「现阶段，有什么用什么，可以混用」
        ///
        /// 所以这个列表**不是**"只用第一个"的硬优先级，它的作用降级成两件事：
        ///   ① 当**库存量相同**时决定谁先被用（可解释的稳定顺序）；
        ///   ② 过滤掉"这个 Def 不允许砌墙/做门"的东西。
        ///
        /// 真正的规则看 `AssignMaterials`：**按库存从多到少依次取用，一种不够就用下一种**，
        /// 于是"木头 300 + 钢 1170"会变成"先用木头、木头用完接着用钢"，
        /// 一栋房子里**允许混用不同材质**，且**逐格记录用了什么**（便于遥测与验证）。
        /// </summary>
        public static string[] MaterialPreference = new string[]
        {
            "WoodLog",    // 前期最常用、最便宜
            "Steel",
            "BlocksSandstone",
            "BlocksGranite",
            "BlocksMarble",
            "BlocksLimestone",
            "BlocksSlate",
        };

        /// <summary>低于这个库存就不选它（免得把料全吃掉、别的活没法干）</summary>
        public static int MinMaterialReserve = 0;

        /// <summary>
        /// 是否允许一栋房**混用**多种材质（用户要求：现阶段有什么用什么）。
        /// 关掉它就退化成"只用库存最多的那一种"。
        /// </summary>
        public static bool AllowMixedMaterials = true;

        /// <summary>
        /// **家具开关**（用户要「直接往房间里面扔一个火把，加三个床」→ 最基本卧室）。
        /// </summary>
        public static bool BuildFurniture = true;

        /// <summary>火把的 defName（`ThingDefOf.TorchLamp` 是"火把灯"，不需要电）</summary>
        public static string TorchDefName = "TorchLamp";

        /// <summary>床的 defName</summary>
        public static string BedDefName = "Bed";

        /// <summary>
        /// 门开在哪几条边（0=北 1=东 2=南 3=西）。默认**四条边都开** ——
        /// 用户看过只有一扇门的实测后说「只有一个门，实际开门方向是封闭的，所以还是多留几道门好」。
        /// </summary>
        public static int[] DoorSides = new int[] { 0, 1, 2, 3 };

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

        private static string BuildReportJson(
            PlacementReport pr, BuildingPlan plan, MaterialChoice mat,
            ConstructionWorkInfo cw, Dictionary<string, int> usedCounts)
        {
            if (pr == null) return "{\"note\":\"没有记账\"}";
            var sb = new System.Text.StringBuilder();
            sb.Append("{");
            sb.Append("\"wallsPlaced\":").Append(pr.WallsPlaced).Append(",");
            sb.Append("\"doorsPlaced\":").Append(pr.DoorsPlaced).Append(",");
            sb.Append("\"bedsPlaced\":").Append(pr.BedsPlaced).Append(",");
            sb.Append("\"torchesPlaced\":").Append(pr.TorchesPlaced).Append(",");
            sb.Append("\"floorsPlaced\":").Append(pr.FloorsPlaced).Append(",");
            sb.Append("\"wallBlueprintReadBack\":").Append(pr.WallBlueprintReadBack).Append(",");
            sb.Append("\"doorBlueprintReadBack\":").Append(pr.DoorBlueprintReadBack).Append(",");
            sb.Append("\"bedBlueprintReadBack\":").Append(pr.BedBlueprintReadBack).Append(",");
            sb.Append("\"torchBlueprintReadBack\":").Append(pr.TorchBlueprintReadBack).Append(",");
            sb.Append("\"floorBlueprintReadBack\":").Append(pr.FloorBlueprintReadBack).Append(",");
            sb.Append("\"totalBlueprintReadBack\":").Append(pr.TotalBlueprintReadBack).Append(",");
            sb.Append("\"verified\":").Append(pr.Verified ? "true" : "false").Append(",");
            sb.Append("\"floorPathTried\":").Append(pr.FloorPathTried ? "true" : "false").Append(",");
            sb.Append("\"floorPathNote\":\"").Append(Esc(pr.FloorPathNote)).Append("\",");
            sb.Append("\"buildFloor\":").Append(BuildFloor ? "true" : "false").Append(",");
            sb.Append("\"buildFurniture\":").Append(BuildFurniture ? "true" : "false").Append(",");
            sb.Append("\"materialNote\":\"").Append(Esc(pr.MaterialNote)).Append("\",");
            // ★ 逐格材质用量（混用时会有多个键）
            sb.Append("\"materialUseByCell\":{");
            {
                bool first = true;
                if (pr.MaterialUse != null)
                {
                    foreach (KeyValuePair<string, int> kv in pr.MaterialUse)
                    {
                        if (!first) sb.Append(",");
                        first = false;
                        sb.Append("\"").Append(Esc(kv.Key)).Append("\":").Append(kv.Value);
                    }
                }
                sb.Append("},");
            }
            // 分配阶段算出来的（可能比实际放下的多，因为有些格被拒）
            sb.Append("\"materialAssigned\":{");
            {
                bool first = true;
                if (usedCounts != null)
                {
                    foreach (KeyValuePair<string, int> kv in usedCounts)
                    {
                        if (!first) sb.Append(",");
                        first = false;
                        sb.Append("\"").Append(Esc(kv.Key)).Append("\":").Append(kv.Value);
                    }
                }
                sb.Append("},");
            }
            // ★ 建造人力（放蓝图之前查的；回答"有没有人会去建"）
            if (cw != null)
            {
                sb.Append("\"constructionWork\":{\"colonists\":").Append(cw.Colonists)
                  .Append(",\"workers\":").Append(cw.ConstructionWorkers)
                  .Append(",\"notInitialized\":").Append(cw.NotInitialized)
                  .Append(",\"anyWorker\":").Append(cw.AnyWorker ? "true" : "false")
                  .Append(",\"detail\":[");
                for (int i = 0; i < cw.Detail.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    sb.Append("\"").Append(Esc(cw.Detail[i])).Append("\"");
                }
                sb.Append("]},");
            }
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
            sb.Append("\"floorTerrainDef\":\"").Append(Esc(FloorTerrainDefName)).Append("\"");
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

                // ── ③ 选址 + 户型（Core 的算法，已 123/123 测过）──
                //
                // 默认值全在 `BuildSiteOptions` 里：Size=13（用户拍板）、
                // DoorSides={0,1,2,3}（四边中心各一扇门 —— 用户明确要求"多留几道门"）。
                // 这里只覆盖"和本地图/本域策略相关"的几项。
                var opts = new BuildSiteOptions
                {
                    Size = HouseSize,
                    MaxDistanceFromBase = 60.0,
                    ComfortableDistance = 20.0,
                    DistancePenaltyPerCell = 4.0,
                    MaxBlockedRatio = 0.10,
                    SearchStep = 1,
                    RequireOutsideAccess = true,
                };
                // 门的边：默认四边；HouseSize 太小时四门会把墙吃掉，交给 Core 的越界检查兜。
                if (DoorSides != null && DoorSides.Length > 0) opts.DoorSides = DoorSides;

                // 家具尺寸**必须**从游戏 Def 读（Core 不认 ThingDef）：
                // 单人床 `Bed` 是 1×2、火把 `TorchLamp` 是 1×1。
                FillFurnitureSizes(opts);

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

                // ── ③b 家具摆放（Core 的纯几何：3 床 + 1 火把）──
                int colonists = 0;
                try { colonists = map.mapPawns.FreeColonistsSpawnedCount; } catch (Exception) { }
                List<FurniturePlacement> furniture = BuildFurniture
                    ? BuildSitePlanner.PlaceFurniture(plan, grid, colonists, opts)
                    : new List<FurniturePlacement>();

                // ── ④ ★ 前置检查：**有没有人做建造**（否则蓝图放下去也没人建 = 白放）──
                //
                // 依据（元数据确证）：建造不是一个 WorkGiver，而是
                //   `WorkGiver_ConstructDeliverResourcesToBlueprints`（送料到蓝图）
                //   `WorkGiver_ConstructDeliverResourcesToFrames`（送料到框架）
                //   `WorkGiver_ConstructFinishFrames`（施工完成）
                //   工作类型是 `WorkTypeDefOf.Construction`（送料还可能走 Hauling）。
                // 放蓝图后游戏会**自动生成**这些工作，我们不用自己派活 ——
                // 但前提是**至少有一个殖民者把 Construction 设为参与**。
                //
                // ⚠ 纪律：这里**只读不改**。偷偷改玩家的优先级属于「工作优先级自动化」（P2），
                //   不属于 S4。所以发现"没人建造"时只报告 + 给出建议，绝不动设置。
                ConstructionWorkInfo cw = CountConstructionWorkers(map);

                // ── ⑤ 按现有资源分配材质（支持混用，逐格记录）──
                //
                // 料够不够：墙 + 门 各要 costStuffCount 个 stuff。
                // ⚠ 实测：地板蓝图**不带 stuff**（byStuff 显示 "(无)"），
                //   它的用料走 `TerrainDef.costStuffCount`（木地板是 3），所以**分开算**。
                // ⚠ 家具（床/火把）也要 stuff：`ThingDef.costStuffCount`。床通常是 10~30 个。
                int needWall = plan.WallCells.Count * Math.Max(1, ThingDefOf.Wall.costStuffCount);
                int needDoor = plan.DoorCells.Count * Math.Max(1, ThingDefOf.Door.costStuffCount);
                int needFloor = BuildFloor ? plan.FloorCells.Count * FloorCostStuffOf() : 0;
                int needFurniture = NeedStuffForFurniture(furniture);
                int need = needWall + needDoor + needFloor + needFurniture;

                List<ThingDef> perCellMats;
                Dictionary<string, int> usedCounts;
                string matWhy;
                List<MaterialChoice> pool = AssignMaterials(
                    map, needWall + needDoor + needFurniture,
                    out perCellMats, out usedCounts, out matWhy);

                if (pool == null || pool.Count == 0)
                {
                    result.Ok = true;
                    result.DidWork = false;
                    result.Summary = "没有可用建材（库存里没有能砌墙的东西），不放蓝图：" + matWhy;
                    return result;
                }

                int availableTotal = 0;
                for (int i = 0; i < perCellMats.Count; i++) if (perCellMats[i] != null) availableTotal++;

                if (availableTotal < needWall + needDoor)
                {
                    result.Ok = true;
                    result.DidWork = false;
                    result.Summary = string.Format(
                        "材料不足：墙门要 {0} 个（墙{1}+门{2}），库存能凑 {3} 个"
                        + "（家具另需 {4}，地板另需 {5}，未计）｜{6}",
                        needWall + needDoor, needWall, needDoor, availableTotal,
                        needFurniture, needFloor, matWhy);
                    return result;
                }

                // 家具料不够 → 只放墙门，家具跳过（降级但报告，别静默）
                string furnitureNote = "";
                List<FurniturePlacement> toPlace = furniture;
                if (BuildFurniture && needFurniture > 0 && availableTotal < needWall + needDoor + needFurniture)
                {
                    toPlace = new List<FurniturePlacement>();
                    furnitureNote = string.Format(
                        "家具料不足（要 {0} 个，凑不出）→ 本轮只放墙门，家具跳过",
                        needFurniture);
                }

                // ── ⑥ 放蓝图：先查再放，逐格回读 ──
                MaterialChoice mainMat = pool[0];
                mainMat.Why = matWhy;
                PlacementReport pr = PlaceBlueprints(map, plan, mainMat, perCellMats, toPlace);
                pr.MaterialNote = matWhy + (string.IsNullOrEmpty(furnitureNote) ? "" : "｜" + furnitureNote);

                // ── ⑦ ★ 回读验证：地图上真的有对应数量的蓝图 ──
                int afterBPs = CountBlueprintBuilds(map);
                int afterWallsBP = CountBlueprintOfBuildDef(map, ThingDefOf.Wall);
                int afterDoorsBP = CountBlueprintOfBuildDef(map, ThingDefOf.Door);
                TerrainDef floorDef = ResolveFloorTerrainDef();
                int afterFloorsBP = (BuildFloor && floorDef != null)
                    ? CountBlueprintOfBuildDef(map, floorDef) : 0;
                ThingDef bedDef = DefDatabase<ThingDef>.GetNamedSilentFail(BedDefName);
                ThingDef torchDef = DefDatabase<ThingDef>.GetNamedSilentFail(TorchDefName);
                int afterBedsBP = bedDef != null ? CountBlueprintOfBuildDef(map, bedDef) : 0;
                int afterTorchesBP = torchDef != null ? CountBlueprintOfBuildDef(map, torchDef) : 0;

                pr.WallBlueprintReadBack = afterWallsBP;
                pr.DoorBlueprintReadBack = afterDoorsBP;
                pr.FloorBlueprintReadBack = afterFloorsBP;
                pr.BedBlueprintReadBack = afterBedsBP;
                pr.TorchBlueprintReadBack = afterTorchesBP;
                pr.TotalBlueprintReadBack = afterBPs;

                bool okWalls = afterWallsBP >= pr.WallsPlaced;
                bool okDoors = afterDoorsBP >= pr.DoorsPlaced;
                bool okFloors = !BuildFloor || afterFloorsBP >= pr.FloorsPlaced;
                bool okBeds = !BuildFurniture || afterBedsBP >= pr.BedsPlaced;
                bool okTorches = !BuildFurniture || afterTorchesBP >= pr.TorchesPlaced;
                pr.Verified = okWalls && okDoors && okFloors && okBeds && okTorches
                    && pr.WallsPlaced > 0 && pr.DoorsPlaced > 0;

                if (pr.Verified) _placedOnce = true;

                result.Ok = true;
                result.DidWork = pr.WallsPlaced > 0 || pr.DoorsPlaced > 0
                    || pr.FloorsPlaced > 0 || pr.BedsPlaced > 0 || pr.TorchesPlaced > 0;
                result.Summary = Summarize(pr, plan, mainMat, center)
                    + "｜" + cw.ToString();

                // ★ 详细记账落一份 JSON（给测试分清"我们放了几个" vs "地图上有几个"）
                LastReportJson = BuildReportJson(pr, plan, mainMat, cw, usedCounts);

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
        // 建造人力检查（S4：蓝图放下去没人建 = 白放）
        // ------------------------------------------------------------------

        /// <summary>
        /// 殖民地"有没有人做建造"的读数（**只读**，绝不动玩家的优先级设置）。
        /// </summary>
        public sealed class ConstructionWorkInfo
        {
            /// <summary>在场的自由殖民者数</summary>
            public int Colonists;

            /// <summary>其中 `WorkIsActive(Construction) == true` 的人数</summary>
            public int ConstructionWorkers;

            /// <summary>每个人的 (名字, 建造优先级) —— 进 JSON，便于测试/遥测核对</summary>
            public List<string> Detail = new List<string>();

            /// <summary>workSettings 还没初始化的殖民者数（读到的那种"0"不可信）</summary>
            public int NotInitialized;

            public bool AnyWorker
            {
                get { return ConstructionWorkers > 0; }
            }

            public override string ToString()
            {
                if (Colonists == 0) return "建造人力：地图上没有一个自由殖民者";
                return string.Format("建造人力：{0}/{1} 人参与建造{2}",
                    ConstructionWorkers, Colonists,
                    NotInitialized > 0 ? "（" + NotInitialized + " 人的工作设置尚未初始化，读到的 0 不可信）" : "");
            }
        }

        /// <summary>
        /// 数"有没有人做建造"。
        ///
        /// ## 为什么要它
        ///
        /// 蓝图放下去之后，游戏靠 `WorkGiver_ConstructDeliverResourcesToBlueprints` /
        /// `WorkGiver_ConstructFinishFrames` 自动生成工作 —— **我们不用派活**。
        /// 但前提是至少有一个殖民者的 `WorkTypeDefOf.Construction` 是参与的。
        /// 如果一个都没有，蓝图会**永久躺在那儿**（这正是"放了等于白放"）。
        ///
        /// ## 两个容易读错的地方（元数据确证后写的）
        ///
        /// 1. `Pawn_WorkSettings.WorkIsActive(w)` 的实现是
        ///    `ConfirmInitializedDebug(); return GetPriority(w) > 0;` ——
        ///    也就是说**它假设已经初始化**。新殖民者的 `priorities` 可能还是 null
        ///    （`get_Initialized` 就是 `priorities != null`），这时 `GetPriority` 返回的 0
        ///    会**误导**成"这人不建造"。
        ///    → 所以先读 `Initialized`；没初始化时**先 `EnableAndInitializeIfNotAlreadyInitialized()`**
        ///      （这只是把默认优先级填上，**不是**改玩家的设置 —— 新殖民者本来就该有默认值）。
        ///      ⚠ 但这一步有副作用，所以只对**没初始化**的人做，且记进 `NotInitialized`。
        /// 2. `GetPriority` 返回 0 = 不参与；1..4 = 优先级。
        /// </summary>
        public static ConstructionWorkInfo CountConstructionWorkers(Map map)
        {
            var info = new ConstructionWorkInfo();
            if (map == null) return info;

            WorkTypeDef construction = null;
            try { construction = WorkTypeDefOf.Construction; } catch (Exception) { }
            if (construction == null) return info;

            try
            {
                foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
                {
                    if (p == null || p.Dead) continue;
                    info.Colonists++;

                    try
                    {
                        Pawn_WorkSettings ws = p.workSettings;
                        if (ws == null)
                        {
                            info.Detail.Add(p.LabelShort + "=没有 workSettings（跳过）");
                            continue;
                        }

                        // ⚠ 没初始化时 GetPriority 的 0 不可信：先补默认值（不改玩家已做的设置）
                        if (!ws.Initialized)
                        {
                            info.NotInitialized++;
                            try { ws.EnableAndInitializeIfNotAlreadyInitialized(); }
                            catch (Exception) { }
                        }

                        int prio = ws.GetPriority(construction);
                        bool active = ws.WorkIsActive(construction);
                        if (active) info.ConstructionWorkers++;

                        info.Detail.Add(string.Format("{0}=prio{1}{2}",
                            p.LabelShort, prio, active ? "(参与)" : "(不参与)"));
                    }
                    catch (Exception e)
                    {
                        info.Detail.Add(p.LabelShort + "=读工作设置失败：" + e.GetType().Name);
                    }
                }
            }
            catch (Exception e)
            {
                info.Detail.Add("整体失败：" + e.Message);
            }

            return info;
        }

        /// <summary>
        /// 把家具的真实占地填进 Core 的选项（**Core 不认 ThingDef，必须这里填**）。
        ///
        /// 实测关键点：RimWorld 的单人床 `Bed` 的 `ThingDef.Size` 是 **1×2**，不是 1×1。
        /// 写死 1×1 会让床的占格算错、摆不进墙角（Core 的 `PlaceFurniture` 会按错尺寸找位置）。
        /// </summary>
        public static void FillFurnitureSizes(BuildSiteOptions opts)
        {
            if (opts == null) return;
            try
            {
                ThingDef bed = DefDatabase<ThingDef>.GetNamedSilentFail(BedDefName);
                if (bed != null)
                {
                    opts.BedSizeX = Math.Max(1, bed.Size.x);
                    opts.BedSizeZ = Math.Max(1, bed.Size.z);
                }
            }
            catch (Exception) { }

            try
            {
                ThingDef torch = DefDatabase<ThingDef>.GetNamedSilentFail(TorchDefName);
                if (torch != null)
                {
                    opts.TorchSizeX = Math.Max(1, torch.Size.x);
                    opts.TorchSizeZ = Math.Max(1, torch.Size.z);
                }
            }
            catch (Exception) { }
        }

        /// <summary>家具总共要几个 stuff（墙门之外的那部分）。</summary>
        private static int NeedStuffForFurniture(List<FurniturePlacement> furniture)
        {
            if (!BuildFurniture || furniture == null || furniture.Count == 0) return 0;

            int total = 0;
            ThingDef bed = null, torch = null;
            try { bed = DefDatabase<ThingDef>.GetNamedSilentFail(BedDefName); } catch (Exception) { }
            try { torch = DefDatabase<ThingDef>.GetNamedSilentFail(TorchDefName); } catch (Exception) { }

            for (int i = 0; i < furniture.Count; i++)
            {
                FurniturePlacement fp = furniture[i];
                if (fp == null) continue;
                ThingDef def = fp.Kind == "torch" ? torch : bed;
                if (def == null) continue;
                total += Math.Max(1, def.costStuffCount);
            }
            return total;
        }

        // ------------------------------------------------------------------
        // 材质选择
        // ------------------------------------------------------------------

        /// <summary>
        /// **按现有资源分配材质** —— 支持**混用**（用户：「现阶段，有什么用什么，可以混用」）。
        ///
        /// ## 规则（简单可解释）
        ///
        /// 1. 数库存（`ColonySetup.CountBuildingMaterials`，用游戏自己的 `IsStuff` 判定）
        /// 2. 只保留**Def 允许砌墙**的（用 `ThingDefOf.Wall.stuffCategories` 判定，不硬编码 defName）
        /// 3. 排序：**先按 `MaterialPreference` 里的位次**（位次相同或都不在列表里时按库存从多到少）
        ///    → 所以"木头 300 + 钢 1170"会**先用木头**（木头在偏好里第一位），
        ///      木头用完（300）再接着用钢（148）—— 这就是用户要的"混用"。
        /// 4. 依次取用把 `totalNeed` 填满；**一种不够就用下一种**。
        ///    `AllowMixedMaterials=false` 时退化成"只用第一种"（够就够，不够就不放）。
        ///
        /// 返回的是**逐格分配表**：`i` 号格用哪个 stuff。这样遥测/验证能回答
        /// "第 17 面墙用了什么料"，而不是只有一个笼统的"材质=WoodLog"。
        ///
        /// 返回 null 表示**一个可用的料都没有**（那就不该放蓝图：放了也没人能建）。
        /// </summary>
        public static List<MaterialChoice> AssignMaterials(
            Map map, int totalNeed, out List<ThingDef> perCell,
            out Dictionary<string, int> usedCounts, out string why)
        {
            perCell = new List<ThingDef>();
            usedCounts = new Dictionary<string, int>();
            why = "";

            if (map == null || totalNeed <= 0) { why = "不需要料或没有地图"; return null; }

            Dictionary<string, int> stock = ColonySetup.CountBuildingMaterials(map);
            if (stock == null || stock.Count == 0) { why = "库存里没有任何建材"; return null; }

            List<StuffCategoryDef> wallCats = null;
            try { wallCats = ThingDefOf.Wall.stuffCategories; } catch (Exception) { }

            // ── 候选池：能砌墙的 ──
            var pool = new List<MaterialChoice>();
            foreach (KeyValuePair<string, int> kv in stock)
            {
                if (kv.Value <= MinMaterialReserve) continue;
                ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(kv.Key);
                if (def == null) continue;
                bool okWall = StuffAllowed(wallCats, def);
                if (!okWall) continue;

                bool okDoor = false;
                try { okDoor = StuffAllowed(ThingDefOf.Door.stuffCategories, def); } catch (Exception) { }

                pool.Add(new MaterialChoice
                {
                    Def = def,
                    Available = kv.Value,
                    AllowedForWall = true,
                    AllowedForDoor = okDoor,
                });
            }

            if (pool.Count == 0) { why = "库存里没有能砌墙的建材（stuffCategories 不匹配）"; return null; }

            // ── 排序：偏好位次优先，其次库存多的先 ──
            pool.Sort(delegate(MaterialChoice a, MaterialChoice b)
            {
                int ra = PreferenceRank(a.Def != null ? a.Def.defName : null);
                int rb = PreferenceRank(b.Def != null ? b.Def.defName : null);
                if (ra != rb) return ra.CompareTo(rb);
                return b.Available.CompareTo(a.Available);
            });

            // ── 依次取用，填满 totalNeed ──
            int filled = 0;
            int kinds = 0;
            for (int i = 0; i < pool.Count && filled < totalNeed; i++)
            {
                MaterialChoice mc = pool[i];
                int take = mc.Available;
                if (!AllowMixedMaterials) take = totalNeed;      // 只用第一种
                if (take > totalNeed - filled) take = totalNeed - filled;
                if (take <= 0) continue;

                for (int k = 0; k < take; k++) perCell.Add(mc.Def);
                int prev;
                usedCounts[mc.Def.defName] = usedCounts.TryGetValue(mc.Def.defName, out prev) ? prev + take : take;
                filled += take;
                kinds++;
                if (!AllowMixedMaterials) break;
            }

            if (filled <= 0) { why = "可用建材总量为 0"; return null; }

            mc_why(pool, usedCounts, totalNeed, filled, kinds, ref why);

            return pool;
        }

        private static void mc_why(
            List<MaterialChoice> pool, Dictionary<string, int> usedCounts,
            int totalNeed, int filled, int kinds, ref string why)
        {
            var parts = new List<string>();
            foreach (KeyValuePair<string, int> kv in usedCounts)
                parts.Add(kv.Key + "×" + kv.Value);
            why = string.Format("按库存混用（偏好位次优先）：{0}；需要 {1} 个，能凑出 {2} 个，用了 {3} 种",
                string.Join("+", parts.ToArray()), totalNeed, filled, kinds);
        }

        /// <summary>在偏好列表里的位次（不在列表里给一个很大的数 → 排后面）。</summary>
        private static int PreferenceRank(string defName)
        {
            if (string.IsNullOrEmpty(defName) || MaterialPreference == null) return 1000;
            for (int i = 0; i < MaterialPreference.Length; i++)
                if (MaterialPreference[i] == defName) return i;
            return 1000;
        }

        /// <summary>
        /// 兼容用的"挑一个主材质"（遥测里显示"主要用了什么"）。
        /// 新逻辑请用 <see cref="AssignMaterials"/>（支持混用 + 逐格记录）。
        /// </summary>
        public static MaterialChoice ChooseMaterial(Map map, BuildingPlan plan)
        {
            if (map == null) return null;

            List<ThingDef> perCell;
            Dictionary<string, int> used;
            string why;
            int need = plan != null
                ? plan.WallCells.Count * Math.Max(1, ThingDefOf.Wall.costStuffCount)
                  + plan.DoorCells.Count * Math.Max(1, ThingDefOf.Door.costStuffCount)
                : 1;

            List<MaterialChoice> pool = AssignMaterials(map, need, out perCell, out used, out why);
            if (pool == null || pool.Count == 0) return null;

            // 用得最多的那种当"主材质"
            MaterialChoice best = null;
            int bestN = -1;
            for (int i = 0; i < pool.Count; i++)
            {
                int n;
                if (!used.TryGetValue(pool[i].Def.defName, out n)) continue;
                if (n > bestN) { bestN = n; best = pool[i]; }
            }
            if (best == null) best = pool[0];
            best.Why = why;
            return best;
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

            // ---- S4 第二版：家具 + 逐格材质 ----

            /// <summary>放出去的床蓝图数（目标 = 殖民者数）</summary>
            public int BedsPlaced;
            /// <summary>放出去的火把蓝图数（目标 1）</summary>
            public int TorchesPlaced;

            /// <summary>**逐格材质用量**（stuff defName → 用了几格）。混用时会有多个键。</summary>
            public Dictionary<string, int> MaterialUse = new Dictionary<string, int>();

            /// <summary>材质分配的一句解释（进摘要/遥测）</summary>
            public string MaterialNote = "";

            /// <summary>回读到的床/火把蓝图数</summary>
            public int BedBlueprintReadBack;
            public int TorchBlueprintReadBack;

            public override string ToString()
            {
                return string.Format(
                    "墙{0} 门{1} 床{2} 火把{3} 地板{4} 材质[{5}] 回读(墙{6}/门{7}/床{8}/火把{9}/总{10}) 验证={11}{12}",
                    WallsPlaced, DoorsPlaced, BedsPlaced, TorchesPlaced, FloorsPlaced,
                    JoinMaterialUse(),
                    WallBlueprintReadBack, DoorBlueprintReadBack,
                    BedBlueprintReadBack, TorchBlueprintReadBack,
                    TotalBlueprintReadBack, Verified,
                    Rejected.Count > 0 ? " 被拒" + Rejected.Count : "");
            }

            public string JoinMaterialUse()
            {
                if (MaterialUse == null || MaterialUse.Count == 0) return "(无)";
                var parts = new List<string>();
                foreach (KeyValuePair<string, int> kv in MaterialUse) parts.Add(kv.Key + "×" + kv.Value);
                return string.Join("+", parts.ToArray());
            }
        }

        /// <summary>
        /// **真的放蓝图**。顺序照本项目纪律：**每一格先 `CanPlaceBlueprintAt` 查，再放**。
        ///
        /// 墙 / 门 / 地板**三条都走 `GenConstruct.PlaceBlueprintForBuild`** ——
        /// 这是实测确认的（见类注释 §「地板这条路径」）：地板虽然 `def` 是 `TerrainDef`，
        /// 但它有 `blueprintDef == Blueprint_WoodPlankFloor`，放出来就是**蓝图**。
        ///
        /// ## 材质**逐格**指定（用户要求"有什么用什么，可以混用"）
        ///
        /// 同一次调用里**允许**墙用木头、门用钢。`materialsForCells[i]` 就是第 i 格
        /// （按调用顺序：先墙、再门、再地板）要用的 stuff；长度不够时退回 `fallbackMaterial`。
        /// 这样遥测能回答"第 17 面墙用了什么料"，验证也能核对材质分布。
        ///
        /// ⚠ 门朝向：13×13 有 4 扇门，**每扇都得按它所在的边给朝向** ——
        ///   南北墙上的门是南北向（`Rot4.South`），东西墙上的门是东西向（`Rot4.East`）。
        ///   全都用一个朝向会让门嵌不进墙里（门的贴图/占用方向不对）。
        /// </summary>
        public static PlacementReport PlaceBlueprints(
            Map map, BuildingPlan plan, MaterialChoice fallbackMaterial,
            List<ThingDef> materialsForCells, List<FurniturePlacement> furniture)
        {
            var pr = new PlacementReport();
            if (map == null || plan == null) return pr;

            int w = map.Size.x;
            int mi = 0;

            Func<ThingDef> nextStuff = delegate
            {
                if (materialsForCells != null && mi < materialsForCells.Count)
                    return materialsForCells[mi++];
                return fallbackMaterial != null ? fallbackMaterial.Def : null;
            };

            // ── ① 墙（墙是 1×1，朝向无影响）──
            for (int i = 0; i < plan.WallCells.Count; i++)
            {
                ThingDef stuff = nextStuff();
                IntVec3 c = IndexToCell(plan.WallCells[i], w, map);
                if (!TryPlace(map, ThingDefOf.Wall, c, Rot4.North, stuff, pr)) continue;
                pr.WallsPlaced++;
                pr.MaterialUse[stuff != null ? stuff.defName : "(无)"] =
                    (pr.MaterialUse.ContainsKey(stuff != null ? stuff.defName : "(无)")
                        ? pr.MaterialUse[stuff != null ? stuff.defName : "(无)"] : 0) + 1;
            }

            // ── ② 门：**每扇按自己所在的边给朝向** ──
            int maxX = plan.X + plan.Size - 1;
            int maxZ = plan.Z + plan.Size - 1;
            for (int i = 0; i < plan.DoorCells.Count; i++)
            {
                ThingDef stuff = nextStuff();
                int di = plan.DoorCells[i];
                int dx = di % w, dz = di / w;

                // 南北墙（z == z 或 maxZ）→ 门南北向；东西墙 → 门东西向
                Rot4 rot = (dz == plan.Z || dz == maxZ) ? Rot4.South : Rot4.East;

                IntVec3 c = IndexToCell(di, w, map);
                if (!TryPlace(map, ThingDefOf.Door, c, rot, stuff, pr)) continue;
                pr.DoorsPlaced++;
                string key = stuff != null ? stuff.defName : "(无)";
                pr.MaterialUse[key] = (pr.MaterialUse.ContainsKey(key) ? pr.MaterialUse[key] : 0) + 1;
            }

            // ── ③ 地板（默认关：用户说"地板后边再考虑"）──
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
                    // ⚠ 地板**不带 stuff**：TerrainDef 的构造材料由它自己的 costStuffCount 决定
                    //   （实测 byStuff 里地板蓝图显示 "(无)"）。
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
                pr.FloorPathNote = "BuildFloor=false（用户：地板后边再考虑）：户型里有 "
                    + plan.FloorCells.Count + " 格内部，没放地板蓝图";
            }

            // ── ④ ★ 家具：3 张床 + 1 个火把（用户要的"最基本卧室"）──
            if (BuildFurniture && furniture != null && furniture.Count > 0)
            {
                ThingDef bedDef = DefDatabase<ThingDef>.GetNamedSilentFail(BedDefName);
                ThingDef torchDef = DefDatabase<ThingDef>.GetNamedSilentFail(TorchDefName);

                for (int i = 0; i < furniture.Count; i++)
                {
                    FurniturePlacement fp = furniture[i];
                    if (fp == null) continue;

                    ThingDef def = fp.Kind == "torch" ? torchDef : bedDef;
                    if (def == null)
                    {
                        if (pr.Rejected.Count < 8)
                            pr.Rejected.Add(fp.Kind + ": 找不到 Def（" + (fp.Kind == "torch" ? TorchDefName : BedDefName) + "）");
                        continue;
                    }

                    Rot4 rot = Rot4FromInt(fp.Rotation);
                    IntVec3 c = IndexToCell(fp.CellIndex, w, map);

                    // 家具通常也要 stuff（木床/钢床），沿用同一个逐格分配
                    ThingDef stuff = nextStuff();
                    if (!TryPlace(map, def, c, rot, stuff, pr)) continue;

                    if (fp.Kind == "torch") pr.TorchesPlaced++;
                    else pr.BedsPlaced++;
                }
            }

            return pr;
        }

        private static Rot4 Rot4FromInt(int r)
        {
            switch (r)
            {
                case 0: return Rot4.North;
                case 1: return Rot4.East;
                case 3: return Rot4.West;
                default: return Rot4.South;
            }
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
        ///
        /// ## ★ 为什么要先 `WipeExistingThings`（这一条是实测逼出来的）
        ///
        /// 13×13 要 169 格的空地，而 debug/正常地图上**到处是树和灌木**。
        /// 第一版直接 `CanPlaceBlueprintAt` → 大量格被拒 → 44 面墙只放下十几面。
        ///
        /// 而**原版玩家点"建墙"压在灌木上时，游戏是允许的** —— 因为
        /// `Designator_Build.DesignateSingleCell` 的 IL 里在 `PlaceBlueprintForBuild` **之前**先调了
        /// `GenSpawn.WipeExistingThings(c, rot, entDef.blueprintDef, map, DestroyMode.Vanish)`：
        /// 它把"会被这栋建筑替换掉的东西"（植物、碎石堆…）清掉，然后再放蓝图。
        ///
        /// 我们照抄这个顺序，行为就和玩家手点一致 —— **不是**我们在乱删东西，
        /// 而是"在这里盖房子"本来就意味着"先清场"。
        /// </summary>
        private static bool TryPlace(
            Map map, BuildableDef def, IntVec3 c, Rot4 rot, ThingDef stuff, PlacementReport pr)
        {
            if (def == null) return false;
            try
            {
                // ★ 先按原版顺序清场（多格建筑要按占用矩形逐格清）
                try
                {
                    WipeFootprint(map, def, c, rot);
                }
                catch (Exception e)
                {
                    if (pr.Rejected.Count < 8)
                        pr.Rejected.Add(string.Format("({0},{1}) {2}: WipeExistingThings 异常 {3}",
                            c.x, c.z, def.defName, e.GetType().Name));
                }

                // ★ 再查合不合法（godMode=true：只做"几何/地表/占用"判定，不要求格子被解锁）
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
        /// 按建筑的实际占用矩形逐格清场（和原版 <c>Designator_Build</c> 调的是同一个 API）。
        ///
        /// ⚠ 必须按 `GenAdj.OccupiedRect(c, rot, def.Size)` 来，不能只清锚点那一格 ——
        ///   床是 1×2、门/墙是 1×1，只清锚点会漏掉另一半，结果是"清了一半还是放不下"。
        /// </summary>
        private static void WipeFootprint(Map map, BuildableDef def, IntVec3 c, Rot4 rot)
        {
            ThingDef blueprintDef = null;
            try { blueprintDef = def.blueprintDef; } catch (Exception) { }
            if (blueprintDef == null) return;      // 没有蓝图形态的东西不按这个顺序走

            CellRect rect;
            try { rect = GenAdj.OccupiedRect(c, rot, def.Size); }
            catch (Exception) { rect = CellRect.SingleCell(c); }

            foreach (IntVec3 cell in rect)
            {
                if (!cell.InBounds(map)) continue;
                try
                {
                    GenSpawn.WipeExistingThings(cell, rot, blueprintDef, map, DestroyMode.Vanish);
                }
                catch (Exception) { }
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
                "盖 {0}×{0} 于 ({1},{2})，墙 {3}/{4} 门 {5}/{6} 床 {7} 火把 {8} 地板 {9}/{10}，"
                + "★回读 墙{11} 门{12} 床{13} 火把{14} 总{15}，验证={16}，材质[{17}]；"
                + "基地中心={18}@{19},{20}{21}",
                plan.Size, plan.X, plan.Z,
                pr.WallsPlaced, plan.WallCells.Count,
                pr.DoorsPlaced, plan.DoorCells.Count,
                pr.BedsPlaced, pr.TorchesPlaced,
                pr.FloorsPlaced, plan.FloorCells.Count,
                pr.WallBlueprintReadBack, pr.DoorBlueprintReadBack,
                pr.BedBlueprintReadBack, pr.TorchBlueprintReadBack,
                pr.TotalBlueprintReadBack,
                pr.Verified,
                pr.JoinMaterialUse(),
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

                var opts = new BuildSiteOptions { Size = HouseSize };
                if (DoorSides != null && DoorSides.Length > 0) opts.DoorSides = DoorSides;
                FillFurnitureSizes(opts);

                BuildingPlan plan = BuildSitePlanner.Plan(grid, dist, opts);
                if (plan == null) return "找不到可盖地块（中心 " + center.Kind + "@" + center.X + "," + center.Z + "）";

                int colonists = 0;
                try { colonists = map.mapPawns.FreeColonistsSpawnedCount; } catch (Exception) { }

                List<FurniturePlacement> furniture = BuildFurniture
                    ? BuildSitePlanner.PlaceFurniture(plan, grid, colonists, opts)
                    : new List<FurniturePlacement>();

                ConstructionWorkInfo cw = CountConstructionWorkers(map);

                List<ThingDef> perCell;
                Dictionary<string, int> used;
                string matWhy;
                int needWall = plan.WallCells.Count * Math.Max(1, ThingDefOf.Wall.costStuffCount);
                int needDoor = plan.DoorCells.Count * Math.Max(1, ThingDefOf.Door.costStuffCount);
                int needFurn = NeedStuffForFurniture(furniture);
                AssignMaterials(map, needWall + needDoor + needFurn, out perCell, out used, out matWhy);

                int buildable = 0;
                for (int i = 0; i < grid.Buildable.Length; i++) if (grid.Buildable[i]) buildable++;

                TerrainDef floor = ResolveFloorTerrainDef();
                int floorCost = FloorCostStuffOf();

                var beds = furniture.FindAll(delegate(FurniturePlacement f) { return f.Kind == "bed"; });
                var torches = furniture.FindAll(delegate(FurniturePlacement f) { return f.Kind == "torch"; });

                return string.Format(
                    "户型({0},{1}) {2}×{2} 墙{3} 门{4} 内部{5} 基地距{6:F0} 分{7:F1} | "
                    + "可盖格{8}/{9} | 家具: 床{10} 火把{11}（床尺寸 {12}×{13}）| "
                    + "需要 stuff 墙门{14} 家具{15}{16} | 材质分配 {17} | floor={18} | {19}",
                    plan.X, plan.Z, plan.Size, plan.WallCells.Count, plan.DoorCells.Count,
                    plan.FloorCells.Count, plan.AvgDistanceToBase, plan.Score,
                    buildable, grid.Buildable.Length,
                    beds.Count, torches.Count, opts.BedSizeX, opts.BedSizeZ,
                    needWall + needDoor, needFurn,
                    BuildFloor ? string.Format(" 地板{0}({1}格×{2})", floor != null ? floor.defName : "?", plan.FloorCells.Count, floorCost) : " 地板关",
                    matWhy,
                    BuildFloor ? "开" : "关",
                    cw.ToString());
            }
            catch (Exception e)
            {
                return "InspectNow 异常：" + e.Message;
            }
        }
    }
}
