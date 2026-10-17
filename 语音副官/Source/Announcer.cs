using System;
using System.Collections.Generic;
using Verse;

namespace VoiceDeputy
{
    // 全 mod 唯一的开关检查点 + 去重（01 §5.3）。
    // 第 4 步只走到"去重 + 记日志"为止，句子直接丢掉（不组装）；成句在第 5 步接 SegmentPlanner。
    public static class Announcer
    {
        // 01 §5.3：键 = Category + Phrase + Args + Count，窗口 2000 ms。
        public const int DedupeWindowMs = 2000;

        // 过期项每 5 分钟清一次，去重表不常驻增长。
        private const int DedupeSweepMs = 300000;

        private static readonly Dictionary<string, int> LastAcceptedMs = new Dictionary<string, int>(StringComparer.Ordinal);
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

                // 第 5 步在这里改成 SegmentPlanner.Plan + Trace("2", FullText)；本步到此为止。
                Trace.Step("2", "accept " + notice.Category + " " + notice.Phrase);
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
