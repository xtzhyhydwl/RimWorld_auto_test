// BikeProbe · WorkGiver（探针 A2 / A4 的“活源”）
//
// ★ 真值表 §1.3：`WorkGiver_Scanner` 在 **RimWorld**（不是 Verse.AI）。
// ★ 真值表 §2.2：`WorkGiver_Scanner::PotentialWorkThingsGlobal(Pawn)` 基类默认是 `ldnull`
//   ⇒ 不覆写它，JobGiver_Work 就永远扫不到候选（静默失效）。
// ★ 本探针**不做预约**（`CanReserve` 那条留到产品侧），所以 HasJobOnThing 只判类型/禁用，
//   JobOnThing 直接复用 ProbeDriving 建 Job。这一条在 BUILD-REPORT 的“未做”里登记。

using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace BikeProbe
{
    public class Probe_WorkGiver_Tidy : WorkGiver_Scanner
    {
        public override PathEndMode PathEndMode
        {
            get { return PathEndMode.Touch; }
        }

        private static int scanLogged;
        private static int lastCount = -1;

        public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn)
        {
            if (pawn == null || pawn.Map == null)
            {
                yield break;
            }

            ThingDef def = ProbeDefs.Bike;
            if (def == null)
            {
                yield break;
            }

            List<Thing> found = pawn.Map.listerThings.ThingsOfDef(def);
            if (found == null)
            {
                yield break;
            }

            // ★ 2026-10-13（run-2）改动：run-1 的 `scanLogged < 5` 把 5 次机会全花在
            //   "车落地之前"（count 恒为 0）⇒ 之后 AI 自主派活那条链**没有任何日志**。
            //   现在改成"计数变化时不限次数地打 + 头 3 次不论如何都打"，总上限 12 行：
            //   既能证明扫描器在**有空车之后**还在跑，也不会淹掉 Player.log。
            bool changed = found.Count != lastCount;
            if ((changed || scanLogged < 3) && scanLogged < 12)
            {
                scanLogged++;
                lastCount = found.Count;
                ProbeLog.Line("A2", "WorkGiver 扫描到地上的车 count=" + found.Count
                              + " pawn=" + pawn.ThingID + " tick=" + GenTicks.TicksGame);
            }

            for (int i = 0; i < found.Count; i++)
            {
                yield return found[i];
            }
        }

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            return t is ProbeBike && !t.IsForbidden(pawn) && !pawn.Downed && !pawn.Dead;
        }

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            ThingDef bikeDef = ProbeDefs.Bike;
            JobDef jobDef = ProbeDefs.TidyJob;
            if (jobDef == null || bikeDef == null)
            {
                return null;
            }

            Job job = new Job(jobDef);
            job.targetA = new LocalTargetInfo(t);
            job.targetB = new LocalTargetInfo(ProbeCells.FindNear(t.Map, t.Position, 4));
            job.count = 1;
            job.checkEncumbrance = true;

            ProbeLog.Line("A2", "WorkGiver 派活 ProbeTidyBike pawn=" + pawn.ThingID
                          + " bike=" + t.ThingID + " dest=" + job.targetB.Cell);
            return job;
        }
    }
}
