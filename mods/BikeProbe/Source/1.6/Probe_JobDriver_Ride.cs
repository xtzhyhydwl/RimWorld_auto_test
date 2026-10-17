// BikeProbe · 骑行 Job（探针 C2 / D / A1 的前置状态）
//
// ★ C2 的问题：“骑行 Job 的上下车开销 O”。判据 = 用游戏 tick 打点，从“进入上车 toil”
//   到“下车 toil 结束”的 tick 差 ÷ 60 得秒，**给出一个数**。
//   ⚠ 本探针把打点拆细了，因为方案里的 O 定义会把“路上时间”也算进去：
//       mountToilTicks   = 上车 toil 自身花掉的 tick（Instant toil 应为 0~1）
//       dismountToilTicks= 下车 toil 自身花掉的 tick
//       O = (mountToilTicks + dismountToilTicks) / 60   ← **这才是 v2 §7.2 里那个 O**
//       travelTicks      = 中间路上的 tick（不属于 O，单独打印以便核对）
//       holdTicks        = 故意加的停留窗口（见下），也必须从 O 里排除
// ★ D 的问题：“骑行时车的贴图跟着小人连续移动、层级正确；下车后车出现在合法格”。
//   技术路线 = 车进 `carryTracker`（原版渲染链每帧画它），姿态偏移的候选挂点是
//   `Verse.AI.JobDriver::ModifyCarriedThingDrawPos(ref Vector3, ref bool)`。
//   ⚠ 这个符号**真值表里没有**（方案 D 行标“未确证”）。
//     本轮 IL 实测：`Verse.AI.JobDriver` 的 declared-only 成员表里**确实有**
//     `public bool ModifyCarriedThingDrawPos(ref UnityEngine.Vector3 drawPos, ref bool flip)`。
//   ⇒ 这里是探针唯一一处“覆写未确证符号”，它若能过编译就等于拿到第一层确证；
//     运行时是否被调用、返回值被怎么用，由 ProbeDrawStats 的计数日志回答。
//   ⚠ 默认**不改**绘制（OverrideDrawPos=false），返回 base 的结果；只在被显式打开时才 return true。
//     这样万一 drawPos 语义猜错也不会把车画飞。
// ★ A1a/A1b/A1c 的前置状态：本 Job 上车后**故意停留 1200 tick（20 秒）**，
//   给下一阶段一个稳定窗口去触发“远行队带走”（ExitMap）与“debug 杀死骑手”（Kill）。

using System.Collections.Generic;
using UnityEngine;
using Verse;
using Verse.AI;

namespace BikeProbe
{
    /// <summary>D 的可观测面：ModifyCarriedThingDrawPos 有没有被调到、被调了多少次。</summary>
    public static class ProbeDrawStats
    {
        public static bool OverrideDrawPos;
        private static int calls;
        private static int lastLogTick = -1000;

        internal static int Calls
        {
            get { return calls; }
        }

        internal static void NotifyCarriedDraw()
        {
            calls++;
            int tick = GenTicks.TicksGame;
            if (calls <= 3 || tick - lastLogTick >= 120)
            {
                lastLogTick = tick;
                ProbeLog.Line("D", "ModifyCarriedThingDrawPos 被调到 calls=" + calls
                              + " tick=" + tick + " overrideDraw=" + OverrideDrawPos);
            }
        }
    }

    public class Probe_JobDriver_Ride : JobDriver
    {
        private int tWalkStart = -1;
        private int tMountStart = -1;
        private int tRideStart = -1;
        private int tHoldStart = -1;
        private int tDismountStart = -1;
        private int tEnd = -1;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            ProbeLog.Line("C2", "TryMakePreToilReservations errorOnFailed=" + errorOnFailed + " -> true(探针不预约)");
            return true;
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            ProbeLog.Line("C2", "toil plan = [0]GotoThing(A,Touch) [1]Mount(Instant,carryTracker) "
                          + "[2]GotoCell(B,OnCell) [3]Hold(1200 tick) [4]Dismount(Instant,TryDropCarriedThing)"
                          + " job=" + (job == null ? "null" : job.def.defName));

            Toil walk = Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);
            walk.AddPreInitAction(delegate
            {
                tWalkStart = GenTicks.TicksGame;
                ProbeLog.Line("C2", "T0 走向车 start tick=" + tWalkStart
                              + " target=" + job.targetA
                              + " bikeSpawned=" + (job.targetA.Thing != null && job.targetA.Thing.Spawned));
            });
            yield return walk;

            yield return MakeMountToil();

            Toil ride = Toils_Goto.GotoCell(TargetIndex.B, PathEndMode.OnCell);
            ride.AddPreInitAction(delegate
            {
                tRideStart = GenTicks.TicksGame;
                ProbeLog.Line("C2", "T2 开始骑（已上车）tick=" + tRideStart
                              + " mountToilTicks=" + (tRideStart - tMountStart)
                              + " 携带=" + DescribeCarried(pawn));
            });
            yield return ride;

            yield return MakeHoldToil();
            yield return MakeDismountToil();
        }

        private Toil MakeMountToil()
        {
            Toil toil = ToilMaker.MakeToil("ProbeBike.Ride_Mount");
            toil.defaultCompleteMode = ToilCompleteMode.Instant;   // 真值表 §2.8：Instant = 1
            toil.initAction = delegate
            {
                Pawn p = toil.actor;
                Thing bike = job.targetA.Thing;
                tMountStart = GenTicks.TicksGame;

                if (bike == null)
                {
                    ProbeLog.Fail("C2", "上车时 targetA 丢失");
                    EndJobWith(JobCondition.Incompletable);
                    return;
                }

                if (bike.Spawned)
                {
                    bike.DeSpawn(DestroyMode.Vanish);
                }

                // ⚠ 真值表 §2.3 写的是 `int TryStartCarry(Verse.Thing item)` —— 本轮编译实测是
                //   **`bool TryStartCarry(Verse.Thing item)`**（AsmDump `=Verse.Pawn_CarryTracker` 亦如此：
                //   `METHOD public bool TryStartCarry(Verse.Thing item)` / `METHOD public int TryStartCarry(Verse.Thing, int, bool)`）。
                //   真值表那一行需要更正（见 tmp\bike-probe\BUILD-REPORT.md §3）。
                bool started = p.carryTracker.TryStartCarry(bike);
                ProbeLog.Line("C2", "T1 上车 tick=" + tMountStart
                              + " TryStartCarry->" + started
                              + " carried=" + DescribeCarried(p)
                              + " bikeSpawned=" + bike.Spawned
                              + " bikeDestroyed=" + bike.Destroyed
                              + " walkTicks=" + (tMountStart - tWalkStart));

                ProbeCounts.LogAll("C2", p.Map, "after-mount");
                Probe_MapComponent.SelfCheck(p.Map, "after-mount");
            };
            return toil;
        }

        private Toil MakeHoldToil()
        {
            Toil toil = ToilMaker.MakeToil("ProbeBike.Ride_Hold");
            toil.defaultCompleteMode = ToilCompleteMode.Delay;
            toil.defaultDuration = 1200;
            toil.AddPreInitAction(delegate
            {
                tHoldStart = GenTicks.TicksGame;
                ProbeLog.Line("A1", "T3 停留窗口开始 tick=" + tHoldStart
                              + " 时长=1200 tick(20 秒) —— 这 20 秒内去触发 ExitMap / Kill 两条解绑路径"
                              + " 携带=" + DescribeCarried(pawn));
            });
            return toil;
        }

        private Toil MakeDismountToil()
        {
            Toil toil = ToilMaker.MakeToil("ProbeBike.Ride_Dismount");
            toil.defaultCompleteMode = ToilCompleteMode.Instant;
            toil.initAction = delegate
            {
                Pawn p = toil.actor;
                tDismountStart = GenTicks.TicksGame;

                Thing dropped = null;
                bool ok = p.carryTracker.TryDropCarriedThing(
                    p.Position, ThingPlaceMode.Near, out dropped, null);

                tEnd = GenTicks.TicksGame;
                int mountToilTicks = tRideStart - tMountStart;
                int holdTicks = tDismountStart - tHoldStart;
                int dismountToilTicks = tEnd - tDismountStart;
                int travelTicks = tHoldStart - tRideStart;
                int oTicks = mountToilTicks + dismountToilTicks;

                ProbeLog.Line("C2", "T4 下车 ok=" + ok
                              + " dropped=" + (dropped == null ? "NULL" : dropped.def.defName + "/" + dropped.ThingID)
                              + " droppedSpawned=" + (dropped != null && dropped.Spawned)
                              + " droppedDestroyed=" + (dropped != null && dropped.Destroyed)
                              + " droppedPos=" + (dropped == null ? default(IntVec3) : dropped.Position)
                              + " posStandable=" + (dropped != null && dropped.Spawned
                                    ? dropped.Position.Standable(dropped.Map) : false));

                ProbeLog.Line("C2", "O 打点汇总: mountToilTicks=" + mountToilTicks
                              + " dismountToilTicks=" + dismountToilTicks
                              + " O_ticks=" + oTicks
                              + " O_seconds=" + (oTicks / 60f).ToString("0.###")
                              + " | 不算进 O 的: walkTicks=" + (tMountStart - tWalkStart)
                              + " travelTicks=" + travelTicks
                              + " holdTicks=" + holdTicks
                              + " 全程=" + (tEnd - tWalkStart));

                if (!ok || dropped == null)
                {
                    ProbeLog.Fail("C2", "下车失败 ⇒ 车可能变成无主对象（Spawned=false 且不在容器里）");
                }

                ProbeCounts.LogAll("C2", p.Map, "after-dismount");
            };
            return toil;
        }

        /// <summary>
        /// ★ D 行唯一的未确证符号挂点。真值表没有这一行；本轮 IL 从
        /// `Verse.AI.JobDriver` 的 declared-only 成员表里读到它存在，签名如下：
        ///     public bool ModifyCarriedThingDrawPos(ref UnityEngine.Vector3 drawPos, ref bool flip)
        /// 默认（OverrideDrawPos=false）只记录调用次数并返回 base 的结果；
        /// 打开后 return true 并只改 x/z（y 不动，方案 D 的硬要求）。
        /// </summary>
        public override bool ModifyCarriedThingDrawPos(ref Vector3 drawPos, ref bool flip)
        {
            bool inner = base.ModifyCarriedThingDrawPos(ref drawPos, ref flip);
            ProbeDrawStats.NotifyCarriedDraw();

            if (!ProbeDrawStats.OverrideDrawPos)
            {
                return inner;
            }

            drawPos.x += 0f;
            drawPos.z += 0f;
            return true;
        }

        private static string DescribeCarried(Pawn p)
        {
            if (p == null || p.carryTracker == null || p.carryTracker.CarriedThing == null)
            {
                return "none";
            }
            Thing t = p.carryTracker.CarriedThing;
            return t.def.defName + "/" + t.ThingID
                   + "(isProbeBikeBuilding=" + (t is ProbeBike)
                   + ",isProbeBikeMinified=" + (t is MinifiedBike) + ")";
        }
    }
}
