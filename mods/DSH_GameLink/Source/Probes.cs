// DSH_GameLink · 探针：把「某件事发生了」变成可比较的判据。
//
// 用途有两份，**共用同一份实现**（这是"各取所需"的关键）：
//   · stopWhen —— 命中就把这一程停掉；
//   · watch    —— 不拦时间，只往事件通道写一条 probe_hit，让上位机知道发生过。
//
// 文法（写在命令文件的 `stopWhen=` / `watch=` 里，`;` 分隔多条）：
//   letter                     任何新信件
//   letter:<defName>           指定信件（例如 ThreatBig / RaidEnemy）
//   letter:<defName>:<包含词>   指定信件且标题里含某个词
//   message                    任何新的右上角消息
//   message:<包含词>           消息文本里含某个词（例如「离开了」）
//
// 为什么都是"轮询计数 + 只看新增的那几条"：信件/消息列表都很短（个位数），
//   每 tick 只读一次 Count，变化了才扫新增项 —— 11k tick/s 下这点开销可以忽略；
//   而 Harmony 钩子最多让我们早 1 tick，却要 patch 别人的方法（冲突 + 版本脆弱），不值得。

using System;
using System.Collections.Generic;
using System.Reflection;
using RimWorld;
using Verse;

namespace DSH_GameLink
{
    internal sealed class ProbeSpec
    {
        internal string Raw;
        internal string Kind;
        internal string FilterDef;
        internal string FilterText;

        /// <summary>解析一条探针；失败给出给人看的原因（不要抛）。</summary>
        internal static bool TryParse(string raw, out ProbeSpec spec, out string error)
        {
            spec = null;
            error = null;
            if (string.IsNullOrEmpty(raw))
            {
                error = "空探针";
                return false;
            }

            string[] parts = raw.Split(':');
            string kind = parts[0].Trim().ToLowerInvariant();
            if (kind != "letter" && kind != "message")
            {
                error = "不认识的探针种类「" + parts[0] + "」（目前支持 letter / message）";
                return false;
            }

            spec = new ProbeSpec { Raw = raw.Trim(), Kind = kind };
            // 两种 kind 的第二段含义不同（2026-10-25 实测踩过：`message:崩溃风险` 被当成 defName，永不命中）：
            //   letter:<defName>[:<含词>]                        —— 信件先按 defName 过滤（一封袭击信的身份就是它）
            //   message:<含词>  |  message:def=<defName>[:<含词>] —— 消息先按文本过滤：
            //     消息的 defName 只分 ThreatBig / NeutralEvent 这种大类，按它筛几乎没意义，真正有用的是那句话。
            if (kind == "letter")
            {
                if (parts.Length > 1) spec.FilterDef = CleanDefName(parts[1]);
                if (parts.Length > 2) spec.FilterText = parts[2];
            }
            else if (parts.Length == 2)
            {
                spec.FilterText = parts[1];
            }
            else if (parts.Length >= 3)
            {
                spec.FilterDef = CleanDefName(parts[1]);
                spec.FilterText = parts[2];
            }

            if (string.IsNullOrEmpty(spec.FilterDef)) spec.FilterDef = null;
            if (string.IsNullOrEmpty(spec.FilterText)) spec.FilterText = null;
            return true;
        }

        /// <summary>`def=ThreatBig` 与裸 `ThreatBig` 两种写法都认。</summary>
        private static string CleanDefName(string raw)
        {
            if (raw == null) return null;
            string v = raw.Trim();
            if (v.StartsWith("def=", StringComparison.OrdinalIgnoreCase)) v = v.Substring(4).Trim();
            return v.Length == 0 ? null : v;
        }

        internal static List<ProbeSpec> ParseList(string rawList, out string error)
        {
            error = null;
            List<ProbeSpec> list = new List<ProbeSpec>();
            if (string.IsNullOrEmpty(rawList)) return list;
            string[] raws = rawList.Split(';');
            for (int i = 0; i < raws.Length; i++)
            {
                string raw = raws[i].Trim();
                if (raw.Length == 0) continue;
                ProbeSpec spec;
                string err;
                if (!TryParse(raw, out spec, out err))
                {
                    error = err;
                    return null;
                }

                list.Add(spec);
            }

            return list;
        }

        internal bool MatchesLetter(Letter letter)
        {
            if (letter == null) return false;
            string defName = null;
            try { defName = letter.def != null ? letter.def.defName : null; } catch { }
            if (FilterDef != null && !Eq(defName, FilterDef)) return false;
            if (FilterText != null && !Contains(LetterLabel(letter), FilterText)) return false;
            return true;
        }

        internal bool MatchesMessage(Message message)
        {
            if (message == null) return false;
            if (FilterDef != null && !Eq(MessageDefName(message), FilterDef)) return false;
            if (FilterText != null && !Contains(MessageText(message), FilterText)) return false;
            return true;
        }

        internal string DescribeLetter(Letter letter)
        {
            string defName = null;
            try { defName = letter.def != null ? letter.def.defName : null; } catch { }
            return new JsonBuf()
                .Str("probe", Raw)
                .Str("kind", "letter")
                .Str("defName", defName)
                .Str("label", LetterLabel(letter))
                .ToString();
        }

        internal string DescribeMessage(Message message)
        {
            return new JsonBuf()
                .Str("probe", Raw)
                .Str("kind", "message")
                .Str("defName", MessageDefName(message))
                .Str("text", MessageText(message))
                .ToString();
        }

        private static string LetterLabel(Letter letter)
        {
            // `Letter.Label` 是 TaggedString（隐式转 string），不能直接 ?? string
            try { string s = letter.Label; return s ?? string.Empty; } catch { return string.Empty; }
        }

        private static string MessageText(Message message)
        {
            try { return message.text ?? string.Empty; } catch { return string.Empty; }
        }

        private static string MessageDefName(Message message)
        {
            try { return message.def != null ? message.def.defName : null; } catch { return null; }
        }

        private static bool Eq(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static bool Contains(string haystack, string needle)
        {
            return haystack != null && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    /// <summary>探针的唯一取数口：这里读游戏，别处不许再读（一个概念一个名字）。</summary>
    internal static class ProbeSource
    {
        internal static int LetterCount()
        {
            try
            {
                LetterStack stack = Find.LetterStack;
                if (stack == null) return 0;
                List<Letter> letters = stack.LettersListForReading;
                return letters == null ? 0 : letters.Count;
            }
            catch
            {
                return 0;
            }
        }

        internal static Letter LetterAt(int index)
        {
            try
            {
                LetterStack stack = Find.LetterStack;
                if (stack == null) return null;
                List<Letter> letters = stack.LettersListForReading;
                if (letters == null || index < 0 || index >= letters.Count) return null;
                return letters[index];
            }
            catch
            {
                return null;
            }
        }

        // 消息列表的取数口用**反射解析一次**，而不是硬写 `Messages.messages`：
        // 1.7 里这个成员不叫 `messages`（实测编译不过 CS0117），而游戏改小版本也可能改名。
        // 做法：扫 `Verse.Messages` 上所有静态成员，找类型是 `List<Message>` 的那个；
        // 找到就把名字报给上位机（`time.probe.messageSource`），找不到就是 null（消息探针静默不触发）。
        private static bool messageSourceResolved;
        private static FieldInfo messageField;
        private static PropertyInfo messageProperty;

        /// <summary>解析到的成员名（null = 没找到，消息探针不可用）。给 `time.probe` 如实报。</summary>
        internal static string MessageSourceName { get; private set; }

        private static void ResolveMessageSource()
        {
            if (messageSourceResolved) return;
            messageSourceResolved = true;
            try
            {
                const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                FieldInfo[] fields = typeof(Messages).GetFields(flags);
                for (int i = 0; i < fields.Length; i++)
                {
                    if (fields[i].FieldType != typeof(List<Message>)) continue;
                    if (messageField == null || fields[i].Name == "messages") messageField = fields[i];
                    if (fields[i].Name == "messages") break;
                }

                if (messageField != null)
                {
                    MessageSourceName = messageField.Name;
                    return;
                }

                PropertyInfo[] props = typeof(Messages).GetProperties(flags);
                for (int i = 0; i < props.Length; i++)
                {
                    if (props[i].PropertyType != typeof(List<Message>)) continue;
                    messageProperty = props[i];
                    MessageSourceName = props[i].Name;
                    break;
                }
            }
            catch (Exception ex)
            {
                LiveChannel.Err("解析消息列表失败（message 探针不可用）：" + LiveChannel.Describe(ex));
            }
        }

        private static List<Message> LiveMessages()
        {
            ResolveMessageSource();
            try
            {
                if (messageField != null) return messageField.GetValue(null) as List<Message>;
                if (messageProperty != null) return messageProperty.GetValue(null, null) as List<Message>;
                return null;
            }
            catch
            {
                return null;
            }
        }

        internal static int MessageCount()
        {
            try
            {
                List<Message> messages = LiveMessages();
                return messages == null ? 0 : messages.Count;
            }
            catch
            {
                return 0;
            }
        }

        internal static Message MessageAt(int index)
        {
            try
            {
                List<Message> messages = LiveMessages();
                if (messages == null || index < 0 || index >= messages.Count) return null;
                return messages[index];
            }
            catch
            {
                return null;
            }
        }

        /// <summary>在 [from, to) 这个新增区间里找第一条命中的；没有就 null。</summary>
        internal static Letter FindNewLetter(ProbeSpec spec, int from, int to)
        {
            for (int i = Math.Max(0, from); i < to; i++)
            {
                Letter letter = LetterAt(i);
                if (spec.MatchesLetter(letter)) return letter;
            }

            return null;
        }

        internal static Message FindNewMessage(ProbeSpec spec, int from, int to)
        {
            for (int i = Math.Max(0, from); i < to; i++)
            {
                Message message = MessageAt(i);
                if (spec.MatchesMessage(message)) return message;
            }

            return null;
        }
    }
}
