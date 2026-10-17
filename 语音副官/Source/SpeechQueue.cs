using System.Collections.Generic;
using Verse;

namespace VoiceDeputy
{
    // 只放"还没开始播"的句子（01 §5.4）：上限、优先级插队、丢最旧最低。
    // 当前句与 clip 游标归 VoicePlayer；本类只报优先级给插队判据用。
    public static class SpeechQueue
    {
        private sealed class Pending
        {
            public readonly PlayableUtterance Playable;
            public readonly int Priority;
            public readonly int QueuedTick;

            public Pending(PlayableUtterance playable, int priority, int queuedTick)
            {
                Playable = playable;
                Priority = priority;
                QueuedTick = queuedTick;
            }
        }

        private static readonly List<Pending> pending = new List<Pending>();

        public static int Count
        {
            get { return pending.Count; }
        }

        public static void Enqueue(PlayableUtterance playable)
        {
            if (playable == null || playable.Source == null)
            {
                return;
            }

            SpokenCategory category = playable.Source.Category;
            int priority = SpeechCatalog.PriorityOf(category);
            int limit = QueueLimit();

            if (limit <= 0)
            {
                Trace.Step("3b", "drop-new " + category);
                return;
            }

            // 超限：丢"优先级最低、同级最旧"；若最低的都比新句高，则丢新句。
            while (pending.Count >= limit)
            {
                int victim = LowestIndex();
                if (victim < 0 || pending[victim].Priority >= priority)
                {
                    Trace.Step("3b", "drop-new " + category);
                    return;
                }

                SpokenCategory dropped = pending[victim].Playable.Source.Category;
                pending.RemoveAt(victim);
                Trace.Step("3b", "drop-old " + dropped);
            }

            Pending entry = new Pending(playable, priority, GenTicks.TicksGame);

            // 威胁可打断：更高优先级的新句插到队首（当前句播完就轮到它）。
            if (ThreatInterrupts() && pending.Count > 0 && priority > VoicePlayer.CurrentPriority)
            {
                pending.Insert(0, entry);
            }
            else
            {
                pending.Add(entry);
            }

            Trace.Step("3b", pending.Count + " " + category);
        }

        // 取出即转移所有权（01 §6.4）。
        public static bool TryTake(out PlayableUtterance playable)
        {
            if (pending.Count == 0)
            {
                playable = null;
                return false;
            }

            playable = pending[0].Playable;
            pending.RemoveAt(0);
            return true;
        }

        private static int LowestIndex()
        {
            int lowest = -1;
            for (int i = 0; i < pending.Count; i++)
            {
                if (lowest < 0)
                {
                    lowest = i;
                    continue;
                }

                int candidate = pending[i].Priority;
                int current = pending[lowest].Priority;
                if (candidate < current)
                {
                    lowest = i;
                }
                else if (candidate == current && pending[i].QueuedTick < pending[lowest].QueuedTick)
                {
                    lowest = i;
                }
            }

            return lowest;
        }

        private static int QueueLimit()
        {
            VoiceDeputySettings settings = VoiceDeputyMod.Settings;
            return settings == null ? 6 : settings.QueueLimit;
        }

        private static bool ThreatInterrupts()
        {
            VoiceDeputySettings settings = VoiceDeputyMod.Settings;
            return settings != null && settings.ThreatInterrupts;
        }
    }
}
