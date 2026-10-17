using System;
using System.Collections.Generic;
using Verse;

namespace VoiceDeputy
{
    // 全 mod 唯一的开关检查点 + 去重（01 §5.3），去重通过后交给 SegmentPlanner 成句，
    // 成好的句子攒到 Tick 交出去（DeputyGameComponent 是唯一的组装调用者）。
    public static class Announcer
    {
        // 01 §5.3：键 = Category + Phrase + Args + Count，窗口 2000 ms。
        public const int DedupeWindowMs = 2000;

        // 过期项每 5 分钟清一次，去重表不常驻增长。
        private const int DedupeSweepMs = 300000;

        private static readonly Dictionary<string, int> LastAcceptedMs = new Dictionary<string, int>(StringComparer.Ordinal);
        private static readonly Utterance[] EmptyUtterances = new Utterance[0];

        // 本帧待组装列表（DeputyGameComponent 每帧取走一次）。
        private static readonly List<Utterance> ready = new List<Utterance>();
        private static int lastSweepMs;

        // 钩子的唯一出口：只交事实（通道 / 类别 / def 名 / 参数），Notice 与 [VD] 1 都在这一处生成，
        // 免得 5 个钩子文件各抄一遍构造与日志格式。category 为空 = 命中丢弃表（02 §4.2）。
        public static void Receive(SpeechChannel channel, SpokenCategory? category, string defName, string[] args = null, string[] names = null, int count = 1, string batchKey = null)
        {
            try
            {
                if (!category.HasValue)
                {
                    Trace.Step("1", "drop " + defName);
                    return;
                }

                SpokenCategory hit = category.Value;
                Trace.Step("1", SpeechCatalog.ChannelName(channel) + " " + hit + " " + defName);
                Accept(new Notice(hit, SpeechCatalog.SinglePhraseOf(hit), args, names, count, batchKey));
            }
            catch
            {
                // 钩子里抛异常会污染原版调用栈（01 §4 铁律），这里一律吞掉并留一行日志。
                Trace.Step("1", SpeechCatalog.ChannelName(channel));
            }
        }

        // 试听 / 手动朗读（01 §5.3）：绕过事件与开关闸，直接把文本送 PlanRaw。
        public static void SpeakDirect(string text)
        {
            try
            {
                if (string.IsNullOrEmpty(text))
                {
                    return;
                }

                Utterance utterance = SegmentPlanner.PlanRaw(text);
                if (utterance == null)
                {
                    Trace.Step("2", "bad-phrase");
                    return;
                }

                Trace.Step("2", utterance.FullText);
                ready.Add(utterance);
            }
            catch
            {
                // 同 Receive：异常不出 Announcer。
            }
        }

        // 每帧一次：交出本帧待组装的句子（含已在 Receive 里成好的句）。
        public static Utterance[] Tick()
        {
            if (ready.Count == 0)
            {
                return EmptyUtterances;
            }

            Utterance[] batch = ready.ToArray();
            ready.Clear();
            return batch;
        }

        private static void Accept(Notice notice)
        {
            try
            {
                if (!VoiceDeputyMod.HooksInstalled)
                {
                    return;
                }

                if (!Switchboard.Enabled(notice.Category))
                {
                    Trace.Step("2", "off");
                    return;
                }

                int now = Environment.TickCount;
                Sweep(now);

                string key = DedupeKey(notice);
                int lastAccepted;
                if (LastAcceptedMs.TryGetValue(key, out lastAccepted) && now - lastAccepted < DedupeWindowMs)
                {
                    Trace.Step("2", "dup");
                    return;
                }

                LastAcceptedMs[key] = now;

                Utterance utterance = SegmentPlanner.Plan(notice);
                if (utterance == null)
                {
                    Trace.Step("2", "bad-phrase");
                    return;
                }

                Trace.Step("2", utterance.FullText);
                ready.Add(utterance);
            }
            catch
            {
                // 同 Receive：异常不出 Announcer（01 §5.3 失败模式）。
            }
        }

        private static string DedupeKey(Notice notice)
        {
            string args = notice.Args.Length == 0 ? string.Empty : string.Join("|", notice.Args);
            return (int)notice.Category + "/" + (int)notice.Phrase + "/" + notice.Count + "/" + args;
        }

        private static void Sweep(int now)
        {
            if (lastSweepMs != 0 && now - lastSweepMs < DedupeSweepMs)
            {
                return;
            }

            lastSweepMs = now;

            List<string> expired = null;
            foreach (KeyValuePair<string, int> pair in LastAcceptedMs)
            {
                if (now - pair.Value < DedupeWindowMs)
                {
                    continue;
                }

                if (expired == null)
                {
                    expired = new List<string>();
                }

                expired.Add(pair.Key);
            }

            if (expired == null)
            {
                return;
            }

            foreach (string key in expired)
            {
                LastAcceptedMs.Remove(key);
            }
        }
    }
}
