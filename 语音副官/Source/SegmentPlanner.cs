using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VoiceDeputy
{
    // 文案 → Segment 序列（01 §5.3 / 03 §4）：切分、名字清洗、数字、点名截断都只在这个文件里（I5）。
    public static class SegmentPlanner
    {
        private const string UnpronounceableNameChars = "\"'“”()[]{}<>#@*";

        public static Utterance Plan(Notice notice)
        {
            UtteranceSource source = UtteranceSource.From(notice);
            return source == null ? null : PlanCore(source, MaxNamedInBatch());
        }

        public static Utterance Plan(BatchSummary batch)
        {
            UtteranceSource source = UtteranceSource.From(batch);
            return source == null ? null : PlanCore(source, MaxNamedInBatch());
        }

        public static Utterance PlanRaw(string text)
        {
            return PlanCore(UtteranceSource.FromText(text), MaxNamedInBatch());
        }

        // 03 §4.1：设置项由入口每次读出后当参数传给切分函数，切分本身是纯函数。
        private static int MaxNamedInBatch()
        {
            VoiceDeputySettings settings = VoiceDeputyMod.Settings;
            return settings == null ? 0 : settings.MaxNamedInBatch;
        }

        // 切分全文唯一一处（03 §4.2）。返回 null = 不播半句（`Args` 个数不符或模板查不到）。
        private static Utterance PlanCore(UtteranceSource source, int maxNamedInBatch)
        {
            PhraseEntry entry = FindPhrase(source.Phrase);
            if (entry == null)
            {
                return null;
            }

            List<Segment> segments = new List<Segment>();
            if (entry.Form == PhraseForm.RawText)
            {
                // 03 §4.1：裸文本整段一个 RawText 片段，不清洗、不切分。
                if (source.Args.Length != 1 || string.IsNullOrEmpty(source.Args[0]))
                {
                    return null;
                }

                segments.Add(new Segment(source.Args[0], SegmentKind.RawText));
            }
            else
            {
                string[] args = SlotValues(entry, source, maxNamedInBatch);
                if (args == null || args.Length != entry.ArgKinds.Length)
                {
                    return null;
                }

                if (!SplitTemplate(entry.Template, args, entry.ArgKinds, segments))
                {
                    return null;
                }
            }

            return segments.Count == 0 ? null : new Utterance(source.Category, segments.ToArray(), source.BatchKey);
        }

        // 聚合形态固定两槽 {0}=Count、{1}=Names（03 §3.2）；单事件形态直接用钩子给的 Args。
        private static string[] SlotValues(PhraseEntry entry, UtteranceSource source, int maxNamedInBatch)
        {
            if (entry.Form != PhraseForm.Batch)
            {
                return source.Args;
            }

            if (entry.ArgKinds.Length != 2)
            {
                return null;
            }

            return new[] { source.Count.ToString(CultureInfo.InvariantCulture), JoinNames(source.Names, maxNamedInBatch) };
        }

        // 模板按 {n} 切成"字面量 / 占位符"交替的段序列（03 §4.2 步 1–3）。返回 false = 模板不合法。
        private static bool SplitTemplate(string template, string[] args, SegmentKind[] argKinds, List<Segment> segments)
        {
            if (template == null)
            {
                return false;
            }

            int start = 0;
            while (start < template.Length)
            {
                int open = template.IndexOf('{', start);
                if (open < 0)
                {
                    AddConst(segments, template.Substring(start));
                    return true;
                }

                int close = template.IndexOf('}', open);
                if (close < 0)
                {
                    return false;
                }

                AddConst(segments, template.Substring(start, open - start));

                int slot;
                if (!int.TryParse(template.Substring(open + 1, close - open - 1), NumberStyles.None, CultureInfo.InvariantCulture, out slot)
                    || slot < 0
                    || slot >= args.Length
                    || slot >= argKinds.Length)
                {
                    return false;
                }

                AddArg(segments, args[slot], argKinds[slot]);
                start = close + 1;
            }

            return true;
        }

        private static void AddConst(List<Segment> segments, string text)
        {
            // 字面量原样、不清洗（03 §4.2 步 2）。
            if (!string.IsNullOrEmpty(text))
            {
                segments.Add(new Segment(text, SegmentKind.Const));
            }
        }

        private static void AddArg(List<Segment> segments, string value, SegmentKind kind)
        {
            // 空段整段丢弃（03 §4.2 步 6）。
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            if (kind == SegmentKind.PersonName)
            {
                AddPersonName(segments, value);
                return;
            }

            // Number 原样送 TTS（03 §4.4）：中文转换开关 NumericText 还没有设置字段（05 §6 标"待加字段"）。
            segments.Add(new Segment(value, kind));
        }

        // 03 §4.3：一次清洗可能产出两个片段（名字 + 编号数字）。
        private static void AddPersonName(List<Segment> segments, string raw)
        {
            List<string> numbers;
            string name = SplitPersonName(raw, out numbers);

            if (name.Length > 0)
            {
                segments.Add(new Segment(name, SegmentKind.PersonName));
            }

            if (numbers == null)
            {
                return;
            }

            foreach (string number in numbers)
            {
                segments.Add(new Segment(number, SegmentKind.Number));
            }
        }

        // 04 §4.3：清理器的名单要按"能念出来的名字"匹配（条目里存的是清洗后的 PersonName 文本）。
        // 名字清洗只有这一处（03 §4.3），所以清理器从这里取结果，不自己抄一遍规则。
        public static string PersonNameText(string raw)
        {
            List<string> numbers;
            return string.IsNullOrEmpty(raw) ? string.Empty : SplitPersonName(raw, out numbers);
        }

        // 尾部编号后缀拆成 Number，剩下的部分做清洗（03 §4.3 步 1–4）。
        private static string SplitPersonName(string raw, out List<string> numbers)
        {
            numbers = null;
            string name = raw;
            while (true)
            {
                int separator = TrailingNumberSeparator(name);
                if (separator < 0)
                {
                    break;
                }

                if (numbers == null)
                {
                    numbers = new List<string>();
                }

                numbers.Insert(0, name.Substring(separator + 1));
                name = name.Substring(0, separator);
            }

            return CleanName(name);
        }

        // 尾部编号后缀 `.数字` / `-数字` / `_数字` 的分隔符下标；没有则 −1（03 §4.3 步 1）。
        private static int TrailingNumberSeparator(string text)
        {
            int i = text.Length - 1;
            while (i >= 0 && text[i] >= '0' && text[i] <= '9')
            {
                i--;
            }

            if (i < 0 || i == text.Length - 1)
            {
                return -1;
            }

            char separator = text[i];
            return separator == '.' || separator == '-' || separator == '_' ? i : -1;
        }

        // 03 §4.3 步 2–4：删不能念的符号、& → 和、/ \ | 与空白折成一个空格、首尾去空白。
        private static string CleanName(string name)
        {
            StringBuilder builder = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                if (c == '&')
                {
                    builder.Append('和');
                    continue;
                }

                if (c == '/' || c == '\\' || c == '|' || char.IsWhiteSpace(c))
                {
                    AppendSpaceOnce(builder);
                    continue;
                }

                if (UnpronounceableNameChars.IndexOf(c) >= 0)
                {
                    continue;
                }

                builder.Append(c);
            }

            int end = builder.Length;
            while (end > 0 && builder[end - 1] == ' ')
            {
                end--;
            }

            builder.Length = end;
            return builder.ToString();
        }

        private static void AppendSpaceOnce(StringBuilder builder)
        {
            if (builder.Length > 0 && builder[builder.Length - 1] != ' ')
            {
                builder.Append(' ');
            }
        }

        // 03 §4.4：超上限只取前 N 个（Count 不变）；上限 0 ⇒ 只报数；名字之间用「、」。
        // 名字逐个走同一处清洗，所以每个名字的编号后缀照样拆成"名字 + 数字"。
        private static string JoinNames(string[] names, int maxNamedInBatch)
        {
            if (names == null || names.Length == 0 || maxNamedInBatch <= 0)
            {
                return string.Empty;
            }

            int take = Math.Min(names.Length, maxNamedInBatch);
            StringBuilder builder = new StringBuilder();
            List<Segment> cleaned = new List<Segment>();
            for (int i = 0; i < take; i++)
            {
                cleaned.Clear();
                AddPersonName(cleaned, names[i]);
                if (cleaned.Count == 0)
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.Append('、');
                }

                AppendSegmentTexts(builder, cleaned);
            }

            return builder.ToString();
        }

        private static void AppendSegmentTexts(StringBuilder builder, List<Segment> segments)
        {
            foreach (Segment segment in segments)
            {
                if (segment.Kind == SegmentKind.Number && builder.Length > 0 && builder[builder.Length - 1] != ' ')
                {
                    builder.Append(' ');
                }

                builder.Append(segment.Text);
            }
        }

        private static PhraseEntry FindPhrase(SpeechPhrase id)
        {
            foreach (PhraseEntry phrase in SpeechCatalog.Phrases)
            {
                if (phrase.Id == id)
                {
                    return phrase;
                }
            }

            return null;
        }
    }
}
