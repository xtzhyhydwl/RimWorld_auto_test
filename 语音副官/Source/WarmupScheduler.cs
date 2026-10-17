using System;
using System.Collections.Generic;
using Verse;

namespace VoiceDeputy
{
    // 预热（03 §7 / 01 §5.4）：三个触发源都汇到两个 FIFO，每 2 s 出队送 SegmentSynthesizer.RequestWarmup。
    // 不组装、不入队播放、不出声；只落库。能不能进库由键决定（本类不看等级）。
    public static class WarmupScheduler
    {
        public const int WarmupQueueLimit = 500;

        private const int MinuteMs = 60000;

        private static readonly Queue<WarmupJob> segmentQueue = new Queue<WarmupJob>();
        private static readonly Queue<WarmupJob> sentenceQueue = new Queue<WarmupJob>();

        private static bool firstBatchQueued;
        private static int windowStartMs;
        private static int jobsThisWindow;

        // 触发源 2（03 §7.1）：名单变化的新名字。整批进片段队列，逐个查库。
        public static void EnqueueRoster(RosterDelta delta)
        {
            try
            {
                if (delta == null || delta.NewNames.Length == 0)
                {
                    return;
                }

                foreach (string name in delta.NewNames)
                {
                    Enqueue(name, SegmentKind.PersonName);
                }
            }
            catch (Exception exception)
            {
                NoteError("roster", exception);
            }
        }

        // 触发源 3（03 §6 / §7.1）：整句播完才排（播之前排等于给没听过的句子花钱）。
        public static void EnqueueSentence(Utterance utterance)
        {
            try
            {
                if (utterance == null)
                {
                    return;
                }

                Enqueue(utterance.FullText, SegmentKind.Sentence);
            }
            catch (Exception exception)
            {
                NoteError("sentence", exception);
            }
        }

        // 每 2 s 一次（DeputyGameComponent）：先片段队列、后整句队列，按 WarmupJobsPerMinute 限流。
        public static void Tick()
        {
            try
            {
                if (!firstBatchQueued)
                {
                    firstBatchQueued = true;
                    EnqueueConstants();
                }

                Pump();
            }
            catch (Exception exception)
            {
                NoteError("tick", exception);
            }
        }

        public static int PendingCount
        {
            get { return segmentQueue.Count + sentenceQueue.Count; }
        }

        // 触发源 1（03 §7.1）：模板切出来的全部字面量；派生，不另维护词表。
        private static void EnqueueConstants()
        {
            string[] texts = SpeechCatalog.ConstantTexts();
            int queued = 0;
            foreach (string text in texts)
            {
                if (Enqueue(text, SegmentKind.Const))
                {
                    queued++;
                }
            }

            Trace.Step("warmup", "first-batch " + queued + "/" + texts.Length);
        }

        // 入队前先查库（03 §6 / §7.2）：命中就不排。返回是否真的进了队列。
        private static bool Enqueue(string text, SegmentKind segmentKind)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            if (SpeechLibrary.Contains(SpeechLibrary.Key(text, segmentKind)))
            {
                return false;
            }

            Queue<WarmupJob> queue = segmentKind == SegmentKind.Sentence ? sentenceQueue : segmentQueue;
            if (ContainsText(queue, text))
            {
                return false;
            }

            while (PendingCount >= WarmupQueueLimit)
            {
                // 超上限丢最旧：片段队列装着启动批，天然更旧，先丢它。
                WarmupJob dropped = segmentQueue.Count > 0 ? segmentQueue.Dequeue() : sentenceQueue.Dequeue();
                Trace.Step("warmup", "queue-drop " + dropped.Text);
            }

            queue.Enqueue(new WarmupJob(text, segmentKind));
            return true;
        }

        private static int Pump()
        {
            int budget = Budget();
            int sent = PumpQueue(segmentQueue, budget);
            sent += PumpQueue(sentenceQueue, budget - sent);

            if (sent > 0)
            {
                jobsThisWindow += sent;
                Trace.Step("warmup", "sent " + sent + " left=" + PendingCount);
            }

            return sent;
        }

        private static int PumpQueue(Queue<WarmupJob> queue, int allowance)
        {
            int sent = 0;
            while (sent < allowance && queue.Count > 0)
            {
                WarmupJob job = queue.Dequeue();

                // 出队前再查一次库（04 §2.7 / 03 §7.2）：排队期间别处可能已经把它合成好了。
                if (SpeechLibrary.Contains(SpeechLibrary.Key(job.Text, job.SegmentKind)))
                {
                    continue;
                }

                if (SegmentSynthesizer.RequestWarmup(job) == 0)
                {
                    continue;
                }

                sent++;
            }

            return sent;
        }

        // Settings.WarmupJobsPerMinute：默认 100000 ⇒ 不节流；按自然分钟滑窗复位。
        private static int Budget()
        {
            int now = Environment.TickCount;
            if (windowStartMs == 0 || now < windowStartMs || now - windowStartMs >= MinuteMs)
            {
                windowStartMs = now;
                jobsThisWindow = 0;
            }

            int remaining = LimitPerMinute() - jobsThisWindow;
            return remaining <= 0 ? 0 : remaining;
        }

        private static int LimitPerMinute()
        {
            VoiceDeputySettings settings = VoiceDeputyMod.Settings;
            return settings == null ? 100000 : settings.WarmupJobsPerMinute;
        }

        private static bool ContainsText(Queue<WarmupJob> queue, string text)
        {
            foreach (WarmupJob job in queue)
            {
                if (string.Equals(job.Text, text, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static void NoteError(string stage, Exception exception)
        {
            Trace.Step("warmup", stage + "-err " + exception.GetType().Name);
        }
    }
}
