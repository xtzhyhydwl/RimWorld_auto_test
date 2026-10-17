using System.Globalization;
using System.Text;

namespace VoiceDeputy
{
    // 一句话 = 有序片段列表 + 类别 + 日志用的合并键（01 §6.3）。
    public sealed class Utterance
    {
        public readonly SpokenCategory Category;
        public readonly Segment[] Segments;
        public readonly string BatchKey;

        public Utterance(SpokenCategory category, Segment[] segments, string batchKey)
        {
            Category = category;
            Segments = segments == null || segments.Length == 0 ? EmptySegments : (Segment[])segments.Clone();
            BatchKey = batchKey;
        }

        // 唯一真源：不另存拼接结果（01 §6.3）。整句缓存键与"最近播报"都读这里。
        public string FullText
        {
            get
            {
                StringBuilder builder = new StringBuilder();
                for (int i = 0; i < Segments.Length; i++)
                {
                    builder.Append(Segments[i].Text);
                }

                return builder.ToString();
            }
        }

        private static readonly Segment[] EmptySegments = new Segment[0];
    }

    // 批出句的载荷（01 §6.5）。Single：批内合计只有 1 条时指向原 Notice，
    // 由 SegmentPlanner 据此走该类别的单事件模板（03 §5.2），否则走聚合模板。
    public sealed class BatchSummary
    {
        public readonly SpokenCategory Category;
        public readonly string BatchKey;
        public readonly int Count;
        public readonly string[] Names;
        public readonly Notice Single;

        public BatchSummary(SpokenCategory category, string batchKey, int count, string[] names, Notice single = null)
        {
            Category = category;
            BatchKey = batchKey;
            Count = count;
            Names = names == null || names.Length == 0 ? new string[0] : (string[])names.Clone();
            Single = single;
        }
    }

    // 三条入口在内部都归约成它（01 §6.17），再由 SegmentPlanner 的唯一一处切分函数展开。
    public sealed class UtteranceSource
    {
        public readonly SpokenCategory Category;
        public readonly SpeechPhrase Phrase;
        public readonly string[] Args;
        public readonly string[] Names;
        public readonly int Count;
        public readonly string BatchKey;

        private UtteranceSource(SpokenCategory category, SpeechPhrase phrase, string[] args, string[] names, int count, string batchKey)
        {
            Category = category;
            Phrase = phrase;
            Args = Copy(args);
            Names = Copy(names);
            Count = count;
            BatchKey = batchKey;
        }

        public static UtteranceSource From(Notice notice)
        {
            if (notice == null)
            {
                return null;
            }

            return new UtteranceSource(notice.Category, notice.Phrase, notice.Args, notice.Names, notice.Count, notice.BatchKey);
        }

        // 合计只有 1 条 ⇒ 走原通知的单事件模板（点名 + 原因齐全）；否则聚合模板由
        // CategoryEntry.BatchPhrase 给；该类别没有聚合模板 ⇒ 交不出句子（01 §6.17 / 03 §5.2）。
        public static UtteranceSource From(BatchSummary batch)
        {
            if (batch == null)
            {
                return null;
            }

            if (batch.Single != null)
            {
                return From(batch.Single);
            }

            CategoryEntry entry = SpeechCatalog.EntryOf(batch.Category);
            if (entry == null || !entry.BatchPhrase.HasValue)
            {
                return null;
            }

            return new UtteranceSource(
                batch.Category,
                entry.BatchPhrase.Value,
                new[] { batch.Count.ToString(CultureInfo.InvariantCulture) },
                batch.Names,
                batch.Count,
                batch.BatchKey);
        }

        // 试听 / 手动朗读：没有类别来源，取兜底的 OtherMessage（01 §6.17 未给 FromText 的 Category）。
        public static UtteranceSource FromText(string text)
        {
            return new UtteranceSource(SpokenCategory.OtherMessage, SpeechPhrase.Phrase_RawText, new[] { text }, null, 1, null);
        }

        private static string[] Copy(string[] values)
        {
            return values == null || values.Length == 0 ? EmptyTexts : (string[])values.Clone();
        }

        private static readonly string[] EmptyTexts = new string[0];
    }
}
