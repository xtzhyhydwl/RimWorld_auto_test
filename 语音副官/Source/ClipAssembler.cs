using System.Collections.Generic;
using UnityEngine;

namespace VoiceDeputy
{
    // 片段序列 → 可播放（01 §5.4）。无状态：读库、解码、建 clip 全在这一处，AudioClip 只在这里建（主线程）。
    // 缺片段 ⇒ Missing（不播半句）；一个 clip 都建不出 ⇒ Nothing。
    public static class ClipAssembler
    {
        private const string ClipName = "VD";

        public static AssemblyResult Plan(Utterance utterance, IReadOnlyCollection<string> deadTexts)
        {
            if (utterance == null || utterance.Segments.Length == 0)
            {
                return AssemblyResult.Nothing();
            }

            // 第 1 步：整句键（03 §6）。命中就不再看片段。
            AudioClip sentence = ClipOf(SpeechLibrary.Key(utterance.FullText, SegmentKind.Sentence));
            if (sentence != null)
            {
                Trace.Step("3a", "sentence-hit " + utterance.FullText);
                return AssemblyResult.Ready(new PlayableUtterance(utterance, new[] { sentence }, true));
            }

            List<AudioClip> clips = new List<AudioClip>(utterance.Segments.Length);
            List<Segment> missing = null;
            foreach (Segment segment in utterance.Segments)
            {
                if (IsDead(deadTexts, segment.Text))
                {
                    continue;
                }

                // WAV 解不开时 ClipOf 也回 null：该片段进 Missing（坏文件的登记留给第 8 步的 ReportBroken）。
                AudioClip clip = ClipOf(SpeechLibrary.Key(segment.Text, segment.Kind));
                if (clip == null)
                {
                    if (missing == null)
                    {
                        missing = new List<Segment>();
                    }

                    missing.Add(segment);
                    continue;
                }

                clips.Add(clip);
            }

            if (missing != null)
            {
                Trace.Step("3a", "Missing " + missing.Count + " " + utterance.FullText);
                return AssemblyResult.Missing(missing.ToArray());
            }

            if (clips.Count == 0)
            {
                Trace.Step("3a", "nothing " + utterance.FullText);
                return AssemblyResult.Nothing();
            }

            Trace.Step("3a", "Ready " + clips.Count + " " + utterance.FullText);
            return AssemblyResult.Ready(new PlayableUtterance(utterance, clips.ToArray(), false));
        }

        private static AudioClip ClipOf(LibraryKey key)
        {
            byte[] bytes;
            if (!SpeechLibrary.TryRead(key, out bytes))
            {
                return null;
            }

            PcmData pcm = WavReader.Read(bytes);
            if (pcm == null || pcm.Frames <= 0)
            {
                // 文件在、WAV 解不开（头坏/截断）⇒ 坏文件：只登记，删除归清理器（04 §4.6）。
                LibraryJanitor.ReportBroken(key);
                return null;
            }

            LoudnessNormalizer.Normalize(pcm.Samples);
            AudioClip clip = AudioClip.Create(ClipName, pcm.Frames, pcm.Channels, pcm.SampleRate, false);
            if (clip == null)
            {
                return null;
            }

            clip.SetData(pcm.Samples, 0);
            return clip;
        }

        private static bool IsDead(IReadOnlyCollection<string> deadTexts, string text)
        {
            if (deadTexts == null || deadTexts.Count == 0 || text == null)
            {
                return false;
            }

            foreach (string dead in deadTexts)
            {
                if (string.Equals(dead, text, System.StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
