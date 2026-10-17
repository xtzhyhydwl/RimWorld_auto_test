// BikeProbe · 整理 Job（探针 A2 / A4 / C3）
//
// ★ A4 的问题：“整理 Job 的单辆 toil 形状（一次一辆即可）”。判据 = 把 toil 名与顺序打出来，
//   每段结束有断言，并记录 `Job.checkEncumbrance` 的实际取值（真值表 §5 第 14 项：默认值原本未确证）。
// ★ A2 的问题：“地面上的车是建筑形态时，整理工能不能『收起 → 进背包 → 放下』整条链走通”。
//   判据 = 逐段打点：Uninstall 返回值非 null → listerThings.ThingsInGroup(MinifiedThing) 计数 +1
//          → 背包 innerContainer 计数 +1 → 放下后背包计数 −1；全程无红字。
//   ⚠ `ThingRequestGroup.MinifiedThing` 能不能枚举到**自定义** MinifiedThing 子类原本**未确证**。
//     本轮 IL 实测（Verse.ThingListGroupHelper::Includes 的第 39 号 switch 分支）：
//         typeof(RimWorld.MinifiedThing).IsAssignableFrom(def.thingClass)
//     ⇒ 理论上“能”。本探针负责给出运行时数字。
// ★ C3 的问题：“WorkGiver 派活后整理工是否真能一次背 5 辆车”。
//   判据 = 每辆进包前后的 MassUtility.FreeSpace 被记录；车的 Mass = 7 ⇒ Capacity = BodySize × 35。
//
// ⚠ 本探针**故意不写**（方案 §⑦ 不做清单）：预约仲裁、共享语义、停放区、批量调度、
//   把物品形态还原成建筑形态（那是原版的 Blueprint_Install 链路，真值表 §2.3 已确证）。

using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace BikeProbe
{
    public class Probe_JobDriver_Tidy : JobDriver
    {
        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            // 探针不做预约（真值表 §2.3 的 ReservationUtility::CanReserve 是产品侧的事）。
            // 返回 true 让 toil 链能跑起来，这一条在 BUILD-REPORT 的“未做”里如实登记。
            ProbeLog.Line("A4", "TryMakePreToilReservations errorOnFailed=" + errorOnFailed + " -> true(探针不预约)");
            return true;
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            // ★ 2026-10-13（run-2）改动：C3 的判据是"背包 innerContainer 的**实际计数**"。
            //   run-1 的结构是"一次 Job 只 TryAdd 一辆，链尾 DropAllNearPawn 清空背包"
            //   ⇒ 背包计数永远 0/1，**结构上无法累积**，"一次背 5 辆"因此答不了。
            //   现在改成：把 `job.targetQueueA` 里的**每一辆**车都走一遍
            //   [走到车 → 收起+进包]，全部收完才走到 targetB 放下 ⇒ 计数能累积到 N。
            //   探针仍然**不实现产品功能**（没有批量调度、没有预约、没有停放区）。
            List<Thing> bikes = new List<Thing>();
            if (job != null && job.targetQueueA != null)
            {
                for (int i = 0; i < job.targetQueueA.Count; i++)
                {
                    Thing t = job.targetQueueA[i].Thing;
                    if (t != null)
                    {
                        bikes.Add(t);
                    }
                }
            }
            if (bikes.Count == 0 && job != null && job.targetA.Thing != null)
            {
                bikes.Add(job.targetA.Thing);
            }

            ProbeLog.Line("A4", "toil plan = 共 " + bikes.Count + " 辆："
                          + "每辆 [Goto(A_i,Touch) -> Take_Uninstall+Inventory(Instant)]，"
                          + "然后 [GotoCell(B,OnCell) -> Drop_Inventory(Instant)]"
                          + " job=" + (job == null ? "null" : job.def.defName)
                          + " targetA=" + job.targetA + " targetB=" + job.targetB
                          + " checkEncumbrance=" + job.checkEncumbrance);

            // ⚠ `Toils_Goto.GotoThing` 只收 `TargetIndex`（A/B/C），不能直接传 LocalTargetInfo
            //   （run-2 编译期实测：`无法从 Verse.LocalTargetInfo 转换为 Verse.AI.TargetIndex`）。
            //   ⇒ 每个 toil 在**自己的构造函数**里把 `job.targetA` 设成这一辆，然后再 yield。
            //   ⚠ 关键坑（run-2 实测）：**if 的循环体是惰性执行的**，所以"先按 i 设一次 targetA
            //   再 yield 一长串 toil"是错的 —— 真正执行时 i 已经跑到末尾。
            //   正确写法 = 每一轮只构造这一轮要用的东西，然后立刻 yield。
            for (int i = 0; i < bikes.Count; i++)
            {
                Thing target = bikes[i];
                yield return GotoAndTakeToil(target, i, bikes.Count);
            }

            yield return Toils_Goto.GotoCell(TargetIndex.B, PathEndMode.OnCell);
            yield return MakeDropToil();
        }

        /// <summary>
        /// 「走到这一辆车 → 收起 → 进包」的**自足** toil。
        ///
        /// 为什么不用 `Toils_Goto.GotoThing(TargetIndex.A)` + 独立的 Take toil：
        /// run-2 实测那条路会让**整个 Job 在第 1 辆之后就结束**（日志里只有 `第0/5辆`，
        /// 之后再没有任何 T1/T3 行，小人回去闲逛、背包里那辆还留在手上）。
        /// 现在的结构把"路径目标切换"关在**单个 toil 内部**：toil 自己的 `tickAction`
        /// 维护 `stage`，用 `toil.actor.pather.StartPath(cell, ...)` 逐辆改目的地，
        /// 只有全部收完才 `ReadyForNextToil()` ⇒ 不碰 `job.targetA`、不受 JobDriver
        /// 换 toil / 校验目标的时机影响。
        /// ⚠ 探针仍然**不做预约**（不实现产品功能），只做"能带几辆"的测量。
        /// </summary>
        private Toil GotoAndTakeToil(Thing target, int index, int total)
        {
            Thing bike = target;
            int stage = 0; // 0 = 正在走过去；1 = 已到位、已处理

            Toil toil = ToilMaker.MakeToil("ProbeBike.Tidy_GotoTake");
            toil.defaultCompleteMode = ToilCompleteMode.Never;
            toil.handlingFacing = true;

            toil.tickAction = delegate
            {
                Pawn p = toil.actor;
                if (p == null || bike == null || bike.Destroyed || !bike.Spawned)
                {
                    ProbeLog.Fail("A2", "第" + index + "辆在途中失效 bike=" + (bike == null ? "null"
                                  : bike.def.defName + " spawned=" + bike.Spawned + " destroyed=" + bike.Destroyed));
                    EndJobWith(JobCondition.Incompletable);
                    return;
                }

                if (stage == 0)
                {
                    IntVec3 cell = bike.Position;
                    if (p.Position.InHorDistOf(cell, 1.5f))
                    {
                        stage = 1;
                        TakeOne(p, bike, index, total);
                        toil.actor.pather.StopDead(); // 收完了：这一辆的 toil 到此结束
                        ReadyForNextToil();
                        return;
                    }

                    if (!p.pather.Moving)
                    {
                        PathEndMode pem = bike.def.passability == Traversability.Impassable
                            ? PathEndMode.Touch : PathEndMode.OnCell;
                        p.pather.StartPath(cell, pem);
                    }
                }
            };

            toil.AddFailCondition(delegate
            {
                return bike == null || bike.Destroyed || !bike.Spawned;
            });

            return toil;
        }

        /// <summary>
        /// 收起一辆车并塞进背包（原 `MakeTakeToil(...).initAction` 的实体，改成普通方法：
        /// 现在由 `GotoAndTakeToil` 的 tickAction 在"到位"那一刻直接调用）。
        /// </summary>
        private void TakeOne(Pawn p, Thing bike, int index, int total)
        {
            Map map = p.Map;
            if (bike == null || map == null)
            {
                ProbeLog.Fail("A2", "T1 到达后目标丢失 index=" + index + " bike=" + (bike == null) + " map=" + (map == null));
                EndJobWith(JobCondition.Incompletable);
                return;
            }

            ProbeLog.Line("A2", "T1 到位 第" + index + "/" + total + "辆 bike=" + bike.ThingID
                          + " def=" + bike.def.defName
                          + " spawned=" + bike.Spawned
                          + " pos=" + bike.Position
                          + " minifiable=" + bike.def.Minifiable);
            ProbeCounts.LogAll("A2", map, "before-uninstall");

            float freeBefore = MassUtility.FreeSpace(p);
            float capacity = MassUtility.Capacity(p, null);
            int invBefore = p.inventory.innerContainer.Count;
            int groupBefore = map.listerThings.ThingsInGroup(ThingRequestGroup.MinifiedThing).Count;

            MinifiedThing mini = MinifyUtility.Uninstall(bike);

            int groupAfter = map.listerThings.ThingsInGroup(ThingRequestGroup.MinifiedThing).Count;
            ProbeLog.Line("A2", "T2 Uninstall -> "
                          + (mini == null ? "NULL" : mini.def.defName + "/" + mini.ThingID)
                          + " InnerThing=" + (mini == null || mini.InnerThing == null
                                ? "null" : mini.InnerThing.def.defName)
                          + " | 组枚举 ThingsInGroup(MinifiedThing): before=" + groupBefore
                          + " after=" + groupAfter + " delta=" + (groupAfter - groupBefore)
                          + " (我们的 def=" + ProbeDefs.MinifiedDefName + ")");

            if (mini == null)
            {
                ProbeLog.Fail("A2", "Uninstall 返回 null ⇒ 整理链在第一步断掉");
                EndJobWith(JobCondition.Incompletable);
                return;
            }

            if (!mini.Spawned)
            {
                // Uninstall 已经把建筑换成物品形态，正常情况下它是 Spawned 的；
                // 只有异常分支才需要我们补一次 DeSpawn（真值表 §3.2：TryAdd 是抽象，不要依赖容器自己 despawn）。
                ProbeLog.Warn("A2", "T2 后 mini.Spawned=false（异常形态，补一次 DeSpawn）");
            }
            else
            {
                mini.DeSpawn(DestroyMode.Vanish);
            }

            bool added = p.inventory.innerContainer.TryAdd(mini, true);
            int invAfter = p.inventory.innerContainer.Count;
            float freeAfter = MassUtility.FreeSpace(p);

            ProbeLog.Line("C3", "T3 进背包 第" + index + "辆 TryAdd=" + added
                          + " inv=" + invBefore + "->" + invAfter
                          + " freeSpace=" + freeBefore.ToString("0.##") + "->" + freeAfter.ToString("0.##")
                          + " capacity(BodySize*35)=" + capacity.ToString("0.##")
                          + " bikeMass=" + mini.GetStatValue(StatDefOf.Mass, true, -1).ToString("0.##")
                          + " checkEncumbrance=" + job.checkEncumbrance);

            if (!added)
            {
                // ★ C3 的判据要求"若只到 1~2 辆，把卡住的判据打出来"⇒ 这里点名是哪一边先失败。
                ProbeLog.Fail("C3", "背包拒收第 " + index + " 辆 ⇒ 卡住的判据："
                              + "FreeSpace前后=" + freeBefore.ToString("0.##") + "/" + freeAfter.ToString("0.##")
                              + " capacity=" + capacity.ToString("0.##")
                              + " 本辆质量=" + mini.GetStatValue(StatDefOf.Mass, true, -1).ToString("0.##")
                              + "（若 FreeSpace >= 质量仍拒收，则失败原因不是质量闸门）");
                EndJobWith(JobCondition.Incompletable);
                return;
            }

            ProbeCounts.LogAll("C3", map, "after-take");
        }

        private Toil MakeDropToil()
        {
            Toil toil = ToilMaker.MakeToil("ProbeBike.Tidy_Drop");
            toil.defaultCompleteMode = ToilCompleteMode.Instant;
            toil.initAction = delegate
            {
                Pawn p = toil.actor;
                Map map = p.Map;
                IntVec3 cell = job.targetB.Cell;
                if (map == null)
                {
                    ProbeLog.Fail("A2", "T4 落地时 map=null");
                    return;
                }

                int invBefore = p.inventory.innerContainer.Count;
                // DropAllNearPawn(IntVec3, bool forbid = False, bool unforbid = False)
                // —— 真值表 §2.7 已确证的成员，避免用未验证的 TryDrop 重载。
                p.inventory.DropAllNearPawn(cell, false, false);
                int invAfter = p.inventory.innerContainer.Count;

                ProbeLog.Line("A2", "T4 放下 inv=" + invBefore + "->" + invAfter
                              + " cell=" + cell + " tick=" + GenTicks.TicksGame);
                ProbeCounts.LogAll("A2", map, "after-drop");
                Probe_MapComponent.SelfCheck(map, "after-drop");
            };
            toil.AddFinishAction(delegate
            {
                ProbeLog.Line("A4", "整理链结束 tick=" + GenTicks.TicksGame);
            });
            return toil;
        }
    }
}
