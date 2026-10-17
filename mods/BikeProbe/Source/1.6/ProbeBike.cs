// BikeProbe · 物品形态与建筑形态（探针 C1 / A2 的载体 + 手动驱动面）
//
// ★ C1（最反常识的一条，设计 v2 §2.3）：
//   原版 XML 里**没有任何 RecipeDef 产出 MinifiedThing**，所以“在锻造台造出物品形态的车”
//   必须自己造。做法 = 一个 `ParentName="MinifiedThing"` 的 def + 一个 `RimWorld.MinifiedThing`
//   子类，在 **`Verse.Thing::PostMake`**（`public virtual`）里装内层。
//   ⚠ 真值表 §8 / 设计 v2 §2.3 已明确：`Verse.ThingComp::PostMake` **不存在**
//     （ThingComp 上的兄弟是 `PostPostMake`）。我们的 MinifiedBike 继承 `RimWorld.MinifiedThing
//     : Verse.ThingWithComps`，走的是继承链上的 `Thing.PostMake`。
//   ⚠ 若装内层这条路不成立，后果是**造出来就自毁 + 刷红字**：
//     `RimWorld.MinifiedThing::Tick` 里 `if (InnerThing == null) Log.Error("MinifiedThing with null InnerThing. Destroying.")`。
//     这是预期的“响亮失败”。
//
// ★ 驱动面（探针在游戏里跑时用）：ProbeBike 建筑上挂一组 Gizmo，避免“只能靠 AI 自己触发”的
//   不确定性 —— 也避免“摆不出场景就答不了题”：
//     · 整理这辆           → 给最近的殖民者派 ProbeTidyBike Job（A2 / A4 / C3）
//     · 骑这辆             → 给最近的殖民者派 ProbeRideBike Job（C2 / D / A1 前置状态）
//     · 报告计数           → 打一行 [BikeProbe] A2 计数（含 ThingRequestGroup.MinifiedThing 枚举结果）
//   ★★★ 2026-10-13（run-2）新增的四个 A1 专用动作（`ProbeActions`）★★★
//     动机（run-1 实测）：A1a/A1b/A1c 一行没打的**根因**是补丁没挂（已修），
//     但**另一个独立障碍**是"摆不出场景"——调试树里没有任何单步 debug action 会走
//     `Pawn::ExitMap`（`Actions\T: Pass To World` 走的是 `Pawn::PassToWorld`，不是 ExitMap；
//     `Spawn Random Caravan` 是商队**进图**）。所以本轮把"触发真实 ExitMap / Kill"这件事
//     **做进探针自己的 Gizmo**里：在游戏内直接调用 `Pawn.ExitMap(...)` / `Pawn.Kill(...)`，
//     以及真值表 §A.2-10 点名的两条商队重入路径
//     `CaravanExitMapUtility::{ExitMapAndJoinOrCreateCaravan, ExitMapAndCreateCaravan}`。
//     这些都是**探针动作，不是产品功能**（方案 §⑦：不实现产品功能）。
//
// ⚠ 本轮 IL 实测的符号纠错（避免“看起来像的名字”）：`Verse.Thing` **没有** `Mass` 成员
//   （全类型成员表里 0 命中）⇒ 读质量必须走 `RimWorld.StatExtension::GetStatValue(thing, StatDefOf.Mass)`。

using System;
using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;
namespace BikeProbe
{
    /// <summary>C1：物品形态。PostMake 里装内层 ProbeBike。</summary>
    public class MinifiedBike : MinifiedThing
    {
        public override void PostMake()
        {
            base.PostMake();

            ThingDef innerDef = ProbeDefs.Bike;

            // ★★★ run-4 修正（2026-10-14）：这里原来是 `ThingDef stuffDef = ProbeDefs.Steel;`
            //   它照抄了设计 v2 §2.3 的示例代码，而 `ProbeBike` 的 def **没有 `stuffCategories`**
            //   ⇒ `BuildableDef.MadeFromStuff == false`。原版 `ThingMaker.MakeThing(def, stuff)`
            //   的第 3 条守卫于是走：
            //     `Log.Error("MakeThing error: " + def + " is not madeFromStuff but stuff=" + stuff + ". Setting to null.")`
            //   并把 stuff 静默置 null —— 这就是 run-1/2/3 反复看到的那条红字（run-3 已用 IL 钉死）。
            //   修正方式 = 与 def 的真实声明一致：不传 stuff。
            //   ⚠ 只修这一处，不改任何产品语义（内层仍是 `ProbeBike`，Mass 仍由 statBases=7 决定）。
            ThingDef stuffDef = null;

            if (innerDef == null)
            {
                ProbeLog.Fail("C1", "ProbeBike ThingDef 找不到 → 无法装内层（这本该在 Def 加载期就红字）");
                return;
            }

            Thing inner = ThingMaker.MakeThing(innerDef, stuffDef);
            InnerThing = inner;

            float mass = this.GetStatValue(StatDefOf.Mass, true, -1);
            ProbeLog.Line("C1", "PostMake inner=" + (inner == null ? "null" : inner.def.defName)
                          + " InnerThing=" + (InnerThing == null ? "null" : InnerThing.def.defName)
                          + " stuff=" + (stuffDef == null ? "null" : stuffDef.defName)
                          + " innerMadeFromStuff=" + innerDef.MadeFromStuff   // ★ run-4 新增：让"为什么不许传 stuff"在游戏内可回读
                          + " massStat=" + mass.ToString("0.##")
                          + " spawned=" + Spawned);
        }
    }

    /// <summary>A2 的“建筑形态”载体；顺便提供探针的手动驱动 Gizmo。</summary>
    public class ProbeBike : Building
    {
        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo g in base.GetGizmos())
            {
                yield return g;
            }

            Command_Action tidy = new Command_Action();
            tidy.defaultLabel = "Probe: 整理这辆";
            tidy.defaultDesc = "给最近的殖民者派 ProbeTidyBike（A2/A4/C3）。";
            tidy.action = delegate
            {
                Pawn p = ProbeDriving.PickColonist();
                if (p == null)
                {
                    ProbeLog.Fail("A2", "没有可用殖民者");
                    return;
                }
                ProbeDriving.StartTidy(p, this);
            };
            yield return tidy;

            Command_Action ride = new Command_Action();
            ride.defaultLabel = "Probe: 骑这辆";
            ride.defaultDesc = "给最近的殖民者派 ProbeRideBike（C2/D）。";
            ride.action = delegate
            {
                Pawn p = ProbeDriving.PickColonist();
                if (p == null)
                {
                    ProbeLog.Fail("C2", "没有可用殖民者");
                    return;
                }
                ProbeDriving.StartRide(p, this);
            };
            yield return ride;

            Command_Action report = new Command_Action();
            report.defaultLabel = "Probe: 报告计数";
            report.defaultDesc = "打一行 [BikeProbe] A2 计数（含 ThingRequestGroup.MinifiedThing 枚举）。";
            report.action = delegate
            {
                if (Map != null)
                {
                    ProbeCounts.LogAll("A2", Map, "manual-report");
                }
            };
            yield return report;

            // ── run-2 新增：A1 专用（触发真实的 ExitMap / Kill / 商队重入路径）──

            Command_Action mount = new Command_Action();
            mount.defaultLabel = "Probe: A1 摆场景(殖民者抱起这辆)";
            mount.defaultDesc = "把最近的殖民者记成骑手，并让 TA 把这辆车抱进 carryTracker"
                                + "（不派 Job、不挂补丁也能抱 ⇒ A1a 的阴性对照用得上）。";
            mount.action = delegate { ProbeActions.MountNearest(this); };
            yield return mount;

            // ★ run-2 追加：**遥控式**摆场景。存在的理由（实测）：
            //   被抱起来的车会 DeSpawn ⇒ 它的 gizmo 列表立刻变空，无法再用它执行第二个动作
            //   （实测：execute_gizmo 报 "No objects are currently selected."）。
            //   所以用一辆不参与动作的"哨兵车"保持选中，由它遥控骑手。
            Command_Action mountThisRider = new Command_Action();
            mountThisRider.defaultLabel = "Probe: A1 遥控(骑手抱起这辆车)";
            mountThisRider.defaultDesc = "把最近的殖民者记成骑手，并让他抱起**这辆车**（不选中骑手、不动当前选中）。";
            mountThisRider.action = delegate { ProbeActions.MountNearest(this); };
            yield return mountThisRider;

            Command_Action mountLast = new Command_Action();
            mountLast.defaultLabel = "Probe: A1 遥控(骑手抱起最近新出的车)";
            mountLast.defaultDesc = "把最近的殖民者记成骑手，并让他抱起**最近通过调试动作/桥接新生成的那辆 ProbeBike**"
                                    + "（用 spawn_thing 出新车的场景：哨兵车负责发令，新车负责被抱）。";
            mountLast.action = delegate { ProbeActions.MountLastSpawned(); };
            yield return mountLast;

            Command_Action exitMap = new Command_Action();
            exitMap.defaultLabel = "Probe: A1a 骑手离图 ExitMap";
            exitMap.defaultDesc = "对已抱车的骑手直接调 Pawn.ExitMap(true, North)（走真值表 §A.2-13 那条会"
                                  + "Destroy 非 Pawn 携带物的路径）。骑手/车由 '摆场景' 决定。";
            exitMap.action = delegate { ProbeActions.ExitMapRider(); };
            yield return exitMap;

            Command_Action kill = new Command_Action();
            kill.defaultLabel = "Probe: A1c 杀死骑手 Kill";
            kill.defaultDesc = "对已抱车的骑手直接调 Pawn.Kill(null, null)（死亡总线）。";
            kill.action = delegate { ProbeActions.KillRider(); };
            yield return kill;

            Command_Action caravan = new Command_Action();
            caravan.defaultLabel = "Probe: A1a-bis 商队带走骑手";
            caravan.defaultDesc = "调 CaravanExitMapUtility.ExitMapAndCreateCaravan（真值表 §A.2-10 的商队路径之一，"
                                  + "内部会 callvirt Pawn::ExitMap 重入）。";
            caravan.action = delegate { ProbeActions.CaravanTakeRider(false); };
            yield return caravan;

            Command_Action caravanJoin = new Command_Action();
            caravanJoin.defaultLabel = "Probe: A1a-bis2 骑手加入现成商队";
            caravanJoin.defaultDesc = "调 CaravanExitMapUtility.ExitMapAndJoinOrCreateCaravan（另一条商队路径）。";
            caravanJoin.action = delegate { ProbeActions.CaravanTakeRider(true); };
            yield return caravanJoin;

            Command_Action bypass = new Command_Action();
            bypass.defaultLabel = "Probe: A1-neg 切前缀旁通(阴性对照)";
            bypass.defaultDesc = "切换 ProbeUnbind.BypassPrefix：开=前缀不摘车（验证『不打补丁车会被销毁』），"
                                 + "关=正常摘车。当前值会打进日志。";
            bypass.action = delegate { ProbeActions.ToggleBypass(); };
            yield return bypass;

            Command_Action summary = new Command_Action();
            summary.defaultLabel = "Probe: A1 报告骑手状态";
            summary.defaultDesc = "打一行当前骑手/车/计数（配合上面几个动作做前后对照）。";
            summary.action = delegate { ProbeActions.ReportRider("manual-report"); };
            yield return summary;

            Command_Action mapScan = new Command_Action();
            mapScan.defaultLabel = "Probe: 全图数车(含被AI收走的)";
            mapScan.defaultDesc = "扫全图，把 ProbeBike（建筑形态）与 ProbeBikeMinified（物品形态）的位置与数量"
                                  + "全部打进日志。用来判『车丢了吗』——run-2 实测殖民者的 WorkGiver 会自动"
                                  + "把我的探针车收成物品形态搬走，只看某几个格子会得出错误结论。";
            mapScan.action = delegate { ProbeActions.ScanWholeMap(); };
            yield return mapScan;

            Command_Action carryMass = new Command_Action();
            carryMass.defaultLabel = "Probe: C3 依次背 N 辆(用 mass=0 的探针车)";
            carryMass.defaultDesc = "用法：先在附近摆几辆 ProbeBikeMassless（调试动作 Spawn thing...），"
                                    + "选中本车点这个 → 打一行殖民者的 BodySize/Capacity，"
                                    + "并给 TA 派一个『把半径 " + ProbeActions.CarryRadius
                                    + " 格内所有探针车依次收进背包』的探针 Job。"
                                    + "用于把 C3 从『结构上无法累积』变成可实测的累积计数。";
            carryMass.action = delegate { ProbeActions.StartCarryMassProbe(this); };
            yield return carryMass;

            Command_Action encumbrance = new Command_Action();
            encumbrance.defaultLabel = "Probe: C3 质量闸门口算";
            encumbrance.defaultDesc = "打一行殖民者的 BodySize / Capacity(=BodySize*35) / FreeSpace 与车的实际质量。";
            encumbrance.action = delegate { ProbeActions.ReportEncumbrance(this); };
            yield return encumbrance;
        }
    }

    public static class ProbeDriving
    {
        public static Pawn PickColonist()
        {
            Map map = Find.CurrentMap;
            if (map == null)
            {
                return null;
            }

            Pawn selected = Find.Selector != null ? Find.Selector.SingleSelectedThing as Pawn : null;
            if (selected != null && selected.IsColonistPlayerControlled && !selected.Dead)
            {
                return selected;
            }

            List<Pawn> pawns = map.mapPawns != null ? map.mapPawns.FreeColonistsSpawned : null;
            if (pawns == null || pawns.Count == 0)
            {
                return null;
            }

            for (int i = 0; i < pawns.Count; i++)
            {
                if (!pawns[i].Dead && !pawns[i].Downed)
                {
                    return pawns[i];
                }
            }
            return pawns[0];
        }

        public static void StartTidy(Pawn pawn, Thing bike)
        {
            JobDef def = ProbeDefs.TidyJob;
            if (def == null)
            {
                ProbeLog.Fail("A2", "ProbeTidyBike JobDef 找不到");
                return;
            }

            Job job = new Job(def);
            job.targetA = new LocalTargetInfo(bike);
            // ★ run-2 改动（D 的"移动中携带"取证）：run-1 的落点半径=4，实测 travelTicks 只有 0/49
            //   ⇒ "贴图跟着小人连续移动"这个视觉判据**根本没窗口**可拍。
            //   这里改成"从车的位置附近找一个距离 15~30 格的合法落点"，
            //   让"携带 + 移动"持续 1~3 秒，足够拍连续帧（不改任何产品语义）。
            IntVec3 dest = ProbeCells.FindFar(bike.Map, bike.Position, 15, 30);
            job.targetB = new LocalTargetInfo(dest);
            job.count = 1;
            // ⚠ 真值表 §5 第 14 项：Job.checkEncumbrance 的默认值原本未确证。
            //   本轮 IL 实测：Verse.AI.Job 的全部 7 个 ctor 里**没有任何** stfld checkEncumbrance
            //   ⇒ CLR 默认 false。下面同时打印“刚 new 出来时的值”与“我们设成 true 之后的值”，
            //   让下一阶段能在游戏里直接核对这个 IL 结论。
            bool freshDefault = new Job(def).checkEncumbrance;
            job.checkEncumbrance = true;

            ProbeLog.Line("A4", "StartTidy pawn=" + pawn.ThingID + " bike=" + bike.ThingID
                          + " checkEncumbrance_default=" + freshDefault
                          + " checkEncumbrance_set=" + job.checkEncumbrance);

            pawn.jobs.TryTakeOrderedJob(job, JobTag.MiscWork, false);
        }

        public static void StartRide(Pawn pawn, Thing bike)
        {
            JobDef def = ProbeDefs.RideJob;
            if (def == null)
            {
                ProbeLog.Fail("C2", "ProbeRideBike JobDef 找不到");
                return;
            }

            Job job = new Job(def);
            job.targetA = new LocalTargetInfo(bike);
            job.targetB = new LocalTargetInfo(ProbeCells.FindNear(bike.Map, bike.Position, 8));
            job.count = 1;

            ProbeLog.Line("C2", "StartRide pawn=" + pawn.ThingID + " bike=" + bike.ThingID
                          + " dest=" + job.targetB.Cell);
            pawn.jobs.TryTakeOrderedJob(job, JobTag.MiscWork, false);
        }
    }

    /// <summary>
    /// A1 家族的场景驱动器（run-2 新增）。
    ///
    /// 为什么放在 Gizmo 而不是调试动作：run-1 已实测本仓库的调试树里**没有**任何会走
    /// `Pawn::ExitMap` 的单步动作（`T: Pass To World` → `Pawn::PassToWorld`；`Spawn Random Caravan`
    /// → 商队进图）。要在真实游戏里触发 EppMap，最省的办法就是让探针自己成为驱动面。
    ///
    /// 注意：这里**不实现任何产品功能**（没有共享语义 / 停放区 / 预约），
    /// 只是把"调用原版收口方法"这一件事暴露给操作者。
    /// </summary>
    public static class ProbeActions
    {
        /// <summary>A1a 的挂车工具：把最近的殖民者记成骑手，并让 TA 抱起指定的一辆车。</summary>
        public static void MountNearest(Thing bike)
        {
            Map map = Find.CurrentMap;
            if (map == null || bike == null)
            {
                ProbeLog.Fail("A1", "MountNearest map/bike 为空");
                return;
            }

            Pawn p = ProbeDriving.PickColonist();
            if (p == null)
            {
                ProbeLog.Fail("A1", "MountNearest：没有可用殖民者");
                return;
            }

            bool isBuilding = bike is ProbeBike;
            bool isMinified = bike is MinifiedBike;

            // ★ 抱车**不靠补丁**：先 (DeSpawn 后) 用 carryTracker.TryStartCarry 直接抱起来。
            //   建筑形态的 ProbeBike 是"非 Pawn 的携带物" ⇒ 正是 ExitMap 里会被无条件
            //   Destroy(Vanish) 的那一类（真值表 §A.2-13）。
            if (bike.Spawned)
            {
                bike.DeSpawn(DestroyMode.Vanish);
            }

            // 把手里可能已经抱着的东西先放下，保证"这一次抱的就是目标车"。
            if (p.carryTracker != null && p.carryTracker.CarriedThing != null)
            {
                Thing old = null;
                p.carryTracker.TryDropCarriedThing(p.Position, ThingPlaceMode.Near, out old, null);
                ProbeLog.Line("A1", "MountNearest：先放下手里的 " + (old == null ? "NULL" : old.def.defName));
            }

            bool ok = p.carryTracker != null && p.carryTracker.TryStartCarry(bike);

            Rider = p;
            RiderBike = bike;

            ProbeLog.Line("A1", "MountNearest 骑手=" + p.ThingID
                          + " 车=" + bike.def.defName + "/" + bike.ThingID
                          + " isProbeBikeBuilding=" + isBuilding
                          + " isProbeBikeMinified=" + isMinified
                          + " TryStartCarry->" + ok
                          + " carried=" + Describe(p.carryTracker != null ? p.carryTracker.CarriedThing : null)
                          + " bikeSpawned=" + bike.Spawned
                          + " bikeDestroyed=" + bike.Destroyed
                          + " BypassPrefix=" + ProbeUnbind.BypassPrefix
                          + " tick=" + GenTicks.TicksGame);
            if (map != null)
            {
                ProbeCounts.LogAll("A1", map, "after-mount");
            }
        }

        /// <summary>A1a：对骑手直接调 `Pawn.ExitMap(true, Rot4.North)`。</summary>
        public static void ExitMapRider()
        {
            Pawn p = Rider;
            if (p == null || p.Destroyed)
            {
                ProbeLog.Fail("A1a", "ExitMapRider：还没有骑手（先点『A1 摆场景』）");
                return;
            }

            ProbeLog.Line("A1a", "ExitMapRider 调用前：骑手=" + p.ThingID
                          + " spawned=" + p.Spawned
                          + " carried=" + Describe(p.carryTracker != null ? p.carryTracker.CarriedThing : null)
                          + " BypassPrefix=" + ProbeUnbind.BypassPrefix
                          + " tick=" + GenTicks.TicksGame);
            try
            {
                // ⚠ 注意第 1 个参数 = true：ExitMap 内部会先试
                //   `CanExitMapAndJoinOrCreateCaravanNow` → `ExitMapAndJoinOrCreateCaravan` ⇒
                //   这正是真值表 §A.2-10 点名的**商队重入**路径（A1b 要验的那次重入）。
                p.ExitMap(true, Rot4.North);
                ProbeLog.Line("A1a", "ExitMapRider 调用返回（未抛异常）tick=" + GenTicks.TicksGame);
            }
            catch (Exception ex)
            {
                ProbeLog.Fail("A1a", "ExitMapRider 抛异常：" + ex.GetType().Name + " " + ex.Message);
            }

            ReportRider("exitmap-after-call");
        }

        /// <summary>A1c：对骑手直接调 `Pawn.Kill(null, null)`（死亡总线）。</summary>
        public static void KillRider()
        {
            Pawn p = Rider;
            if (p == null || p.Destroyed)
            {
                ProbeLog.Fail("A1c", "KillRider：还没有骑手（先点『A1 摆场景』）");
                return;
            }

            ProbeLog.Line("A1c", "KillRider 调用前：骑手=" + p.ThingID
                          + " carried=" + Describe(p.carryTracker != null ? p.carryTracker.CarriedThing : null)
                          + " tick=" + GenTicks.TicksGame);
            try
            {
                p.Kill(null, null);
                ProbeLog.Line("A1c", "KillRider 调用返回（未抛异常）dead=" + p.Dead
                              + " tick=" + GenTicks.TicksGame);
            }
            catch (Exception ex)
            {
                ProbeLog.Fail("A1c", "KillRider 抛异常：" + ex.GetType().Name + " " + ex.Message);
            }

            ReportRider("kill-after-call");
        }

        /// <summary>
        /// A1a-bis：真值表 §A.2-10 的两条商队路径。
        /// `join==false` → `ExitMapAndCreateCaravan(...)`（新建商队）
        /// `join==true`  → `ExitMapAndJoinOrCreateCaravan(pawn, Rot4.North)`
        /// 两条内部都会 `callvirt Pawn::ExitMap` ⇒ 前缀命中计数与幂等都在这条路上被验。
        /// </summary>
        public static void CaravanTakeRider(bool join)
        {
            Pawn p = Rider;
            if (p == null || p.Destroyed)
            {
                ProbeLog.Fail("A1a", "CaravanTakeRider：还没有骑手（先点『A1 摆场景』）");
                return;
            }

            string label = join ? "ExitMapAndJoinOrCreateCaravan" : "ExitMapAndCreateCaravan";
            ProbeLog.Line("A1a", "CaravanTakeRider 走 " + label
                          + " 骑手=" + p.ThingID
                          + " spawned=" + p.Spawned
                          + " carried=" + Describe(p.carryTracker != null ? p.carryTracker.CarriedThing : null)
                          + " BypassPrefix=" + ProbeUnbind.BypassPrefix
                          + " tick=" + GenTicks.TicksGame);

            try
            {
                if (join)
                {
                    RimWorld.Planet.CaravanExitMapUtility.ExitMapAndJoinOrCreateCaravan(p, Rot4.North);
                }
                else
                {
                    Map map = p.MapHeld;
                    if (map == null)
                    {
                        ProbeLog.Fail("A1a", "CaravanTakeRider：骑手不在任何地图上（MapHeld=null）");
                        return;
                    }
                    int before = Find.WorldObjects != null ? Find.WorldObjects.CaravansCount : -1;
                    // ⚠ 4 参重载的第 4 个参数是 `Direction8Way`（不是 Rot4）——
                    //   run-2 编译期实测：`无法从 Verse.Rot4 转换为 RimWorld.Direction8Way`。
                    RimWorld.Planet.Caravan c = RimWorld.Planet.CaravanExitMapUtility.ExitMapAndCreateCaravan(
                        new List<Pawn> { p }, Faction.OfPlayer, map.Tile, Direction8Way.North, -1, true);
                    int after = Find.WorldObjects != null ? Find.WorldObjects.CaravansCount : -1;
                    ProbeLog.Line("A1a", "CaravanTakeRider ExitMapAndCreateCaravan 返回 caravan="
                                  + (c == null ? "null" : c.ID.ToString())
                                  + " caravansCount=" + before + "->" + after
                                  + " tick=" + GenTicks.TicksGame);
                }
            }
            catch (Exception ex)
            {
                ProbeLog.Fail("A1a", "CaravanTakeRider(" + label + ") 抛异常：" + ex.GetType().Name + " " + ex.Message);
            }

            ReportRider("caravan-after-call");
        }

        /// <summary>
        /// 全图数车（含被殖民者 WorkGiver 收成物品形态搬走的那些）。
        ///
        /// 为什么必须全图扫（run-2 实测教训）：本探针 mod 注册的 `Probe_WorkGiver_Tidy`
        /// 会让殖民者**自主**把我的 `ProbeBike` 用 `MinifyUtility.Uninstall` 收起、
        /// 搬到一个随机近处格子放下。于是"某个格子上还有没有车"完全不能代表"车丢没丢"，
        /// 只看那一个格子会得出"车不见了"的错误结论。判据必须按**全图计数**来写。
        /// </summary>
        public static void ScanWholeMap()
        {
            Map map = Find.CurrentMap;
            if (map == null)
            {
                ProbeLog.Fail("A1", "ScanWholeMap：map=null");
                return;
            }

            ThingDef bikeDef = ProbeDefs.Bike;
            ThingDef masslessDef = ProbeDefs.BikeMassless;
            ThingDef miniDef = ProbeDefs.MinifiedBike;

            int bikeN = 0, masslessN = 0, miniN = 0, miniInnerNull = 0;
            List<string> rows = new List<string>();

            foreach (IntVec3 cell in CellRect.WholeMap(map).Cells)
            {
                List<Thing> things = cell.GetThingList(map);
                if (things == null)
                {
                    continue;
                }
                for (int i = 0; i < things.Count; i++)
                {
                    Thing t = things[i];
                    if (bikeDef != null && t.def == bikeDef)
                    {
                        bikeN++;
                        rows.Add("B@" + cell);
                    }
                    else if (masslessDef != null && t.def == masslessDef)
                    {
                        masslessN++;
                        rows.Add("L@" + cell);
                    }
                    else if (miniDef != null && t.def == miniDef)
                    {
                        miniN++;
                        MinifiedThing mt = t as MinifiedThing;
                        if (mt == null || mt.InnerThing == null)
                        {
                            miniInnerNull++;
                        }
                        rows.Add("M@" + cell);
                    }
                }
            }

            // 随身容器里的也要算：被抱起/被塞进背包的车**不在任何格子上**。
            // ⚠ `mapPawns.AllPawnsSpawned` 实测是 `IReadOnlyList<Pawn>`（编译期 CS0266），要用接口接。
            System.Collections.Generic.IReadOnlyList<Pawn> pawns =
                map.mapPawns != null ? map.mapPawns.AllPawnsSpawned : null;
            int carriedN = 0, invN = 0;
            if (pawns != null)
            {
                for (int i = 0; i < pawns.Count; i++)
                {
                    Pawn p = pawns[i];
                    if (p.carryTracker != null && p.carryTracker.CarriedThing != null)
                    {
                        Thing c = p.carryTracker.CarriedThing;
                        if ((bikeDef != null && c.def == bikeDef) || (miniDef != null && c.def == miniDef))
                        {
                            carriedN++;
                            rows.Add("C:" + p.ThingID + "=" + c.def.defName + "/" + c.ThingID);
                        }
                    }
                    if (p.inventory != null && p.inventory.innerContainer != null && miniDef != null)
                    {
                        ThingOwner<Thing> owner = p.inventory.innerContainer;
                        for (int j = 0; j < owner.Count; j++)
                        {
                            if (owner[j].def == miniDef)
                            {
                                invN++;
                                rows.Add("I:" + p.ThingID + "=" + owner[j].ThingID);
                            }
                        }
                    }
                }
            }

            ProbeLog.Line("A1", "ScanWholeMap 建筑形态ProbeBike=" + bikeN
                          + " 建筑形态ProbeBikeMassless=" + masslessN
                          + " 物品形态ProbeBikeMinified=" + miniN + "(其中InnerThing=null 的 " + miniInnerNull + ")"
                          + " 被carryTracker抱着=" + carriedN
                          + " 在某人背包里=" + invN
                          + " | 明细：" + (rows.Count == 0 ? "(无)" : string.Join(" ", rows.ToArray()))
                          + " tick=" + GenTicks.TicksGame);
        }

        public static void ToggleBypass()        {
            ProbeUnbind.BypassPrefix = !ProbeUnbind.BypassPrefix;
            ProbeLog.Line("A1a", "BypassPrefix 现在是 " + ProbeUnbind.BypassPrefix
                          + "（true = 前缀不摘车，只放行原版）tick=" + GenTicks.TicksGame);
        }

        /// <summary>
        /// 遥控版摆场景：抱起"最近新出现的那辆 ProbeBike"。
        ///
        /// 用法（run-2 跑 A1 的标准动作）：
        ///   ① 在远处摆一辆"哨兵车"（不参与动作，只当控制面板，保持选中）；
        ///   ② 用 `rimworld/spawn_thing` 当场生成一辆新车（它会成为"最近新出现的"）；
        ///   ③ 点哨兵车这个 gizmo → 最近的殖民者抱起那辆**新车**；
        ///   ④ 再用哨兵车上的 ExitMap / Kill / 商队 gizmo 遥控触发真实路径。
        ///
        /// 为什么要"最近新出现"而不是"当前选中"：Gizmo 的动作在点击时执行，那一刻的
        /// `Find.Selector` 指向的是**哨兵车**（我们靠它保持选中），不是要抱的那辆。
        /// 用"ThingID 最大"这条规则取"最新的那辆"最省事、也最不容易拿错。
        /// </summary>
        public static void MountLastSpawned()
        {
            Map map = Find.CurrentMap;
            if (map == null)
            {
                ProbeLog.Fail("A1", "MountLastSpawned：map=null");
                return;
            }

            ThingDef def = ProbeDefs.Bike;
            List<Thing> all = def != null ? map.listerThings.ThingsOfDef(def) : null;
            if (all == null || all.Count == 0)
            {
                ProbeLog.Fail("A1", "MountLastSpawned：地图上没有 ProbeBike —— 先 spawn 一辆");
                return;
            }

            // ★ 必须排除"发令者自己"：这个 gizmo 是**哨兵车**上的控制面板，
            //   而哨兵车通常就是地图上 ThingID 最大的那辆（它也是被 spawn 出来的）
            //   ⇒ 不排除的话会"哨兵把哨兵自己抱起来"，测试车一个都没动。
            Thing self = CurrentGizmoOwner();
            Thing newest = null;
            for (int i = 0; i < all.Count; i++)
            {
                Thing t = all[i];
                if (t.Destroyed || t == self)
                {
                    continue;
                }
                if (newest == null || t.thingIDNumber > newest.thingIDNumber)
                {
                    newest = t;
                }
            }

            if (newest == null)
            {
                ProbeLog.Fail("A1", "MountLastSpawned：除了哨兵车以外没有别的 ProbeBike（共 " + all.Count
                              + " 辆）—— 先用 spawn_thing 出一辆新车");
                return;
            }

            ProbeLog.Line("A1", "MountLastSpawned 选中的最新车=" + newest.def.defName + "/" + newest.ThingID
                          + " 哨兵车=" + (self == null ? "null" : self.ThingID)
                          + " 地图上 ProbeBike 总数=" + all.Count
                          + " tick=" + GenTicks.TicksGame);
            MountNearest(newest);
        }

        /// <summary>
        /// 当前正在执行 gizmo 的那辆车（哨兵车）。`Find.Selector` 指向的就是它。
        /// ⚠ 不能假设"选中的一定是车"：也可能选着小人，所以要 As 一下。
        /// </summary>
        private static Thing CurrentGizmoOwner()
        {
            try
            {
                return Find.Selector != null ? Find.Selector.SingleSelectedThing as Thing : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ── C3：让"一次背 N 辆"变成可实测的累积计数 ───────────────────

        /// <summary>收车半径（格）。</summary>
        public const int CarryRadius = 30;

        /// <summary>
        /// C3 的可测量构造：真车 `ProbeBike` 的 `Mass=7`，而殖民者 `Capacity = BodySize × 35`
        /// ⇒ 1.0 BodySize 的人最多 5 辆，第 6 辆必然越限（数学上说得通，但 run-1 的探针
        /// "一次只 TryAdd 一辆 + 链尾清空背包"，**结构上无法累积** ⇒ 判据答不了）。
        ///
        /// 这里给一个 `Mass=0` 的探针车 `ProbeBikeMassless`：它让"收 7 辆"在质量上永不越限
        /// ⇒ 只要计数能到 7，就证明**容器闸门与 Job 循环本身**没有别的隐性上限，
        /// "只到 1 辆"这类失败一定来自质量闸门（同一 Job 结构下两者可直接对比）。
        /// ⚠ 它是**探针工具 def**，不是产品 def，也不改 `ProbeBike` 的质量（不顺手改产品参数）。
        /// </summary>
        public static void StartCarryMassProbe(Thing anyBike)
        {
            Map map = Find.CurrentMap;
            if (map == null)
            {
                ProbeLog.Fail("C3", "StartCarryMassProbe：map=null");
                return;
            }

            Pawn p = ProbeDriving.PickColonist();
            if (p == null)
            {
                ProbeLog.Fail("C3", "StartCarryMassProbe：没有可用殖民者");
                return;
            }

            JobDef def = ProbeDefs.TidyJob;
            if (def == null)
            {
                ProbeLog.Fail("C3", "ProbeTidyBike JobDef 找不到");
                return;
            }

            IntVec3 from = anyBike != null ? anyBike.Position : p.Position;

            Job job = new Job(def);
            job.checkEncumbrance = true;
            job.count = 1;
            // ★ 实测（run-2）：`Job.targetQueueA` **ctor 不会初始化**，是 null
            //   ⇒ 用之前必须自己 new（第一版漏了这一步，点这个 gizmo 直接
            //   `System.NullReferenceException`，桥接原话：
            //   "Executing gizmo '...:18:...' failed: Object reference not set to an instance of an object"）。
            if (job.targetQueueA == null)
            {
                job.targetQueueA = new List<LocalTargetInfo>();
            }

            int n = 0;
            // 真车（Mass=7）与 massless 车（Mass=0）都收，日志里逐辆打出各自质量 ⇒ 两条曲线一次跑出来。
            for (int r = 1; r <= CarryRadius; r++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    for (int dz = -r; dz <= r; dz++)
                    {
                        if (dx != -r && dx != r && dz != -r && dz != r)
                        {
                            continue;
                        }
                        IntVec3 c = new IntVec3(from.x + dx, 0, from.z + dz);
                        if (!GenGrid.InBounds(c, map))
                        {
                            continue;
                        }
                        List<Thing> things = c.GetThingList(map);
                        if (things == null)
                        {
                            continue;
                        }
                        for (int i = 0; i < things.Count; i++)
                        {
                            Thing t = things[i];
                            if ((t is ProbeBike || IsMasslessBike(t)) && !t.Destroyed)
                            {
                                job.targetQueueA.Add(new LocalTargetInfo(t));
                                n++;
                            }
                        }
                    }
                }
                if (n > 0 && r >= 3)
                {
                    break; // 半径 3 内找到东西就不再往外扩，避免把整图的杂物都收进来
                }
            }

            ProbeLog.Line("C3", "StartCarryMassProbe pawn=" + p.ThingID
                          + " bodySize=" + p.BodySize.ToString("0.###")
                          + " capacity=BodySize*35=" + MassUtility.Capacity(p, null).ToString("0.##")
                          + " freeSpace=" + MassUtility.FreeSpace(p).ToString("0.##")
                          + " 候选车数=" + n
                          + " 半径=" + CarryRadius
                          + " checkEncumbrance=" + job.checkEncumbrance
                          + "（真车 ProbeBike 的 Mass=? 见每辆的日志；massless 探针车为 0）"
                          + " tick=" + GenTicks.TicksGame);

            if (n == 0)
            {
                ProbeLog.Fail("C3", "StartCarryMassProbe：半径 " + CarryRadius
                              + " 内没有找到任何 ProbeBike/ProbeBikeMassless —— 先摆车再点这个");
                return;
            }

            job.targetB = new LocalTargetInfo(from);
            p.jobs.TryTakeOrderedJob(job, JobTag.MiscWork, false);
            ProbeLog.Line("C3", "StartCarryMassProbe 已派活 目标队列长度=" + job.targetQueueA.Count
                          + " tick=" + GenTicks.TicksGame);
        }

        private static bool IsMasslessBike(Thing t)
        {
            ThingDef d = ProbeDefs.BikeMassless;
            return d != null && t.def == d;
        }

        public static void ReportEncumbrance(Thing bike)
        {
            Pawn p = ProbeDriving.PickColonist();
            if (p == null)
            {
                ProbeLog.Fail("C3", "ReportEncumbrance：没有可用殖民者");
                return;
            }

            float bikeMass = bike != null ? bike.GetStatValue(StatDefOf.Mass, true, -1) : -1f;
            ProbeLog.Line("C3", "质量口径 pawn=" + p.ThingID
                          + " bodySize=" + p.BodySize.ToString("0.###")
                          + " capacity=BodySize*35=" + MassUtility.Capacity(p, null).ToString("0.##")
                          + " freeSpace=" + MassUtility.FreeSpace(p).ToString("0.##")
                          + " 选中车的def=" + (bike == null ? "null" : bike.def.defName)
                          + " 该车massStat=" + bikeMass.ToString("0.##")
                          + " ⇒ 该容量可背几辆=" + (bikeMass > 0f
                                ? Math.Floor(MassUtility.Capacity(p, null) / bikeMass).ToString("0")
                                : "∞(mass=0)"));
        }

        public static void ReportRider(string tag)        {
            Pawn p = Rider;
            Thing bike = RiderBike;
            if (p == null)
            {
                ProbeLog.Line("A1", tag + " rider=(未设置) trueHits=" + ProbeUnbind.TrueHits
                              + " exitMapPrefixHits=" + ProbeUnbind.ExitMapPrefixHits
                              + " killPrefixHits=" + ProbeUnbind.KillPrefixHits);
                return;
            }

            Thing carried = p.carryTracker != null ? p.carryTracker.CarriedThing : null;
            ProbeLog.Line("A1", tag + " rider=" + p.ThingID
                          + " spawned=" + p.Spawned
                          + " dead=" + p.Dead
                          + " destroyed=" + p.Destroyed
                          + " mapHeld=" + (p.MapHeld == null ? "null" : p.MapHeld.uniqueID.ToString())
                          + " carried=" + Describe(carried)
                          + " | 目标车=" + (bike == null
                                ? "null" : bike.def.defName + "/" + bike.ThingID
                                  + " spawned=" + bike.Spawned + " destroyed=" + bike.Destroyed
                                  + " pos=" + bike.Position)
                          + " | trueHits=" + ProbeUnbind.TrueHits
                          + " exitMapPrefixHits=" + ProbeUnbind.ExitMapPrefixHits
                          + " killPrefixHits=" + ProbeUnbind.KillPrefixHits
                          + " bypass=" + ProbeUnbind.BypassPrefix
                          + " tick=" + GenTicks.TicksGame);

            Map map = Find.CurrentMap;
            if (map != null)
            {
                ProbeCounts.LogAll("A1", map, tag + "-counts");
            }
        }

        private static string Describe(Thing t)
        {
            if (t == null)
            {
                return "null";
            }
            return t.def.defName + "/" + t.ThingID
                   + "(isProbeBikeBuilding=" + (t is ProbeBike)
                   + ",isProbeBikeMinified=" + (t is MinifiedBike) + ")";
        }

        /// <summary>
        /// 当前骑手（= 被 `MountNearest` 摆好的那个）与 TA 抱着的那辆车。
        /// 刻意用静态字段：Gizmo 的动作是分开点的（摆场景 → 触发），中间隔着真实的游戏帧；
        /// 用 `Find.Selector` 之类的界面状态当传递媒介会在"点了车再点动作"时丢掉关联。
        /// </summary>
        private static Pawn Rider;
        private static Thing RiderBike;
    }
}
