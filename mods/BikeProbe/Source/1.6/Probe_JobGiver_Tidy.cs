// BikeProbe · 思考树插入探针（A9）
//
// ★ A9 的问题原文：“ThinkTreeDef 的 XML 结构（insertTag / insertPriority 的取值与根节点写法）
//   正确 ⇒ 自发骑行决策真的被触发。”原本整条链都**未确证**。
//
// ★ 本轮 IL 已把机制钉死（真值表 §2 未收录，见 tmp\bike-probe\evidence\05-...）：
//   `Verse.AI.ThinkNode_SubtreesByTag::TryIssueJobPackage`：
//       matchedTrees = DefDatabase<ThinkTreeDef>.AllDefs.Where(d => d.insertTag == this.insertTag)
//                      .OrderByDescending(d => d.insertPriority).ToList();
//       foreach (t in matchedTrees) { r = t.thinkRoot.TryIssueJobPackage(pawn, params); if (r.IsValid) return r; }
//       return ThinkResult.NoJob;
//   原版挂点：Data\Core\Defs\ThinkTreeDefs\Humanlike.xml:383 的
//       <li Class="ThinkNode_SubtreesByTag"><insertTag>Humanlike_PostMain</insertTag></li>
//   字段真值（AsmDump `=Verse.ThinkTreeDef`）：`FIELD public float insertPriority`、
//       `FIELD public string insertTag`、`FIELD public Verse.AI.ThinkNode thinkRoot`。
//   ⇒ XML 里 `<thinkRoot Class="全名" />` 就是对的写法。
//
// ★ 本探针只做“被调到没有”：TryGiveJob **恒返回 null**，不真的派骑行 Job
//   （方案 A9：“只打日志 + 恒返回 null，先只验被调到”）。
//   每次命中都把**当前** insertTag / insertPriority 打出来 —— 这样 X1 阶段
//   把 <insertPriority> 改成 0 / 100 / 500 各热重载一次，日志能直接对上。

using Verse;
using Verse.AI;

namespace BikeProbe
{
    public class Probe_JobGiver_Tidy : ThinkNode_JobGiver
    {
        private static int hits;
        private static int lastLogTick = -1000;

        internal static int Hits
        {
            get { return hits; }
        }

        protected override Job TryGiveJob(Pawn pawn)
        {
            hits++;
            int tick = GenTicks.TicksGame;

            // 头 5 次 + 之后每 250 tick 一次，免得把 Player.log 淹掉
            if (hits <= 5 || tick - lastLogTick >= 250)
            {
                lastLogTick = tick;

                ThinkTreeDef def = DefDatabase<ThinkTreeDef>.GetNamed(ProbeDefs.ThinkTreeDefName, false);
                string tag = def == null ? "<def-not-found>" : def.insertTag;
                string prio = def == null ? "<def-not-found>" : def.insertPriority.ToString();

                ProbeLog.Line("A9", "TryGiveJob hits=" + hits
                              + " pawn=" + (pawn == null ? "null" : pawn.ThingID)
                              + " tick=" + tick
                              + " insertTag=" + tag
                              + " insertPriority=" + prio
                              + " -> return null(只验被调到)");
            }

            return null;
        }
    }
}
