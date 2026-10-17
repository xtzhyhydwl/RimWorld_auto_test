using System.Collections.Generic;
using Verse;

namespace VoiceDeputy
{
    // 聚合节点（01 §5.3）：同类别 + 同 BatchKey 的多条合成一句，tick 一变就出句。
    // 只做同 tick 合并；名单归钩子给、句子归 SegmentPlanner 拼、开关归 Switchboard。
    public static class BatchAggregator
    {
        private sealed class Batch
        {
            public readonly SpokenCategory Category;
            public readonly string BatchKey;
            public readonly List<Notice> Members = new List<Notice>();
            public int TotalCount;

            public Batch(SpokenCategory category, string batchKey)
            {
                Category = category;
                BatchKey = batchKey;
            }
        }

        // 同 tick 里可能并行存在多个 (Category, BatchKey) 批（例如同一帧里死了人又崩溃了），
        // 所以暂存的是"成员"，Flush 时再按 (Category, BatchKey) 分组——批的边界只有 tick 一条规则。
        private static readonly List<Notice> staging = new List<Notice>();
        private static readonly BatchSummary[] EmptySummaries = new BatchSummary[0];

        public static int Count
        {
            get { return staging.Count; }
        }

        public static void Add(Notice notice)
        {
            if (notice == null || notice.BatchKey == null)
            {
                return;
            }

            staging.Add(notice);
        }

        // 每帧被 Announcer.Tick 调：只出"已经不是本 tick"的成员；本 tick 的留到下一 tick。
        public static BatchSummary[] Flush()
        {
            if (staging.Count == 0)
            {
                return EmptySummaries;
            }

            int now = GenTicks.TicksGame;
            List<Batch> batches = null;
            for (int i = 0; i < staging.Count; i++)
            {
                Notice notice = staging[i];
                if (notice.Tick == now)
                {
                    continue;
                }

                if (batches == null)
                {
                    batches = new List<Batch>();
                }

                Batch batch = FindBatch(batches, notice);
                batch.Members.Add(notice);
                batch.TotalCount += notice.Count < 1 ? 1 : notice.Count;
            }

            if (batches == null)
            {
                return EmptySummaries;
            }

            // 本 tick 的成员留在暂存里，下一轮再出句。
            int keep = 0;
            for (int i = 0; i < staging.Count; i++)
            {
                if (staging[i].Tick == now)
                {
                    staging[keep++] = staging[i];
                }
            }

            staging.RemoveRange(keep, staging.Count - keep);

            BatchSummary[] summaries = new BatchSummary[batches.Count];
            for (int i = 0; i < batches.Count; i++)
            {
                summaries[i] = Summarize(batches[i]);
            }

            return summaries;
        }

        private static Batch FindBatch(List<Batch> batches, Notice notice)
        {
            for (int i = 0; i < batches.Count; i++)
            {
                if (batches[i].Category == notice.Category && string.Equals(batches[i].BatchKey, notice.BatchKey, System.StringComparison.Ordinal))
                {
                    return batches[i];
                }
            }

            Batch created = new Batch(notice.Category, notice.BatchKey);
            batches.Add(created);
            return created;
        }

        // 合计 Count > 1 ⇒ 聚合形态；合计 == 1 ⇒ Single 指向原 Notice，走该类的单事件模板（03 §5.2）。
        private static BatchSummary Summarize(Batch batch)
        {
            Notice single = batch.TotalCount == 1 && batch.Members.Count == 1 ? batch.Members[0] : null;
            return new BatchSummary(batch.Category, batch.BatchKey, batch.TotalCount, UnionNames(batch.Members), single);
        }

        private static string[] UnionNames(List<Notice> members)
        {
            List<string> names = null;
            for (int i = 0; i < members.Count; i++)
            {
                string[] memberNames = members[i].Names;
                for (int j = 0; j < memberNames.Length; j++)
                {
                    string name = memberNames[j];
                    if (string.IsNullOrEmpty(name))
                    {
                        continue;
                    }

                    if (names == null)
                    {
                        names = new List<string>();
                    }

                    if (!names.Contains(name))
                    {
                        names.Add(name);
                    }
                }
            }

            return names == null ? EmptyNames : names.ToArray();
        }

        private static readonly string[] EmptyNames = new string[0];
    }
}
