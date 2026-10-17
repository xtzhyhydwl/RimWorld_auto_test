using UnityEngine;

namespace VoiceDeputy
{
    // 可播放句（01 §6.4）：ClipAssembler 产出，SpeechQueue 排队、VoicePlayer 出声。
    // 停顿与音量不在这里（VoicePlayer 现读 Settings），优先级也不在这里（SpeechQueue 按 Category 查）。
    public sealed class PlayableUtterance
    {
        public readonly Utterance Source;
        public readonly AudioClip[] Clips;
        public readonly bool FromSentenceCache;

        public PlayableUtterance(Utterance source, AudioClip[] clips, bool fromSentenceCache)
        {
            Source = source;
            Clips = clips == null || clips.Length == 0 ? EmptyClips : (AudioClip[])clips.Clone();
            FromSentenceCache = fromSentenceCache;
        }

        private static readonly AudioClip[] EmptyClips = new AudioClip[0];
    }

    // 三态互斥（01 §6.15）：Ready 必带 Playable、Missing 必带片段、Nothing 两者皆空。
    public enum AssemblyOutcome
    {
        Ready,
        Missing,
        Nothing
    }

    // 作业种类（01 §6.16）：缺片段合成还是预热。
    public enum JobKind
    {
        Missing,
        Warmup
    }

    public sealed class AssemblyResult
    {
        public readonly AssemblyOutcome Outcome;
        public readonly PlayableUtterance Playable;

        // 文档里这一栏叫 Missing，与工厂方法 Missing(Segment[]) 同名会编译不过（CS0102），
        // 所以字段名带上它是什么：这是"还缺的片段"。
        public readonly Segment[] MissingSegments;

        private AssemblyResult(AssemblyOutcome outcome, PlayableUtterance playable, Segment[] missingSegments)
        {
            Outcome = outcome;
            Playable = playable;
            MissingSegments = missingSegments == null || missingSegments.Length == 0 ? EmptySegments : (Segment[])missingSegments.Clone();
        }

        public static AssemblyResult Ready(PlayableUtterance playable)
        {
            return new AssemblyResult(AssemblyOutcome.Ready, playable, null);
        }

        public static AssemblyResult Missing(Segment[] missingSegments)
        {
            return new AssemblyResult(AssemblyOutcome.Missing, null, missingSegments);
        }

        public static AssemblyResult Nothing()
        {
            return new AssemblyResult(AssemblyOutcome.Nothing, null, null);
        }

        private static readonly Segment[] EmptySegments = new Segment[0];
    }
}
