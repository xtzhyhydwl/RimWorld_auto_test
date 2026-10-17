using UnityEngine;
using Verse;

namespace VoiceDeputy
{
    // 出声（01 §5.4）：独占"当前句 + clip 游标"，dspTime 按序连播，音量与停顿每帧现读 Settings。
    // 第 6 步是最小版：单句、按序播、无队列（队列在第 7 步的 SpeechQueue）。
    public static class VoicePlayer
    {
        private const string HostName = "VoiceDeputy";

        private static GameObject host;
        private static AudioSource source;
        private static PlayableUtterance current;
        private static int clipIndex;
        private static double nextDspTime;

        public static void Play(PlayableUtterance playable)
        {
            if (playable == null || playable.Clips.Length == 0)
            {
                return;
            }

            // 最小版没有队列：新句直接顶掉旧句（第 7 步起由 SpeechQueue 排队）。
            current = playable;
            clipIndex = 0;
            nextDspTime = 0.0;
            Trace.Step("4", Text(playable));
        }

        public static void Tick()
        {
            if (current == null)
            {
                return;
            }

            AudioSource audio = Source();
            if (audio == null)
            {
                current = null;
                return;
            }

            if (AudioSettings.dspTime < nextDspTime)
            {
                return;
            }

            AudioClip clip = NextClip();
            if (clip == null)
            {
                Finish();
                return;
            }

            audio.clip = clip;
            audio.volume = Volume();
            double start = AudioSettings.dspTime + GapSeconds();
            audio.PlayScheduled(start);
            nextDspTime = start + clip.length + GapSeconds();
        }

        private static AudioClip NextClip()
        {
            while (clipIndex < current.Clips.Length)
            {
                AudioClip clip = current.Clips[clipIndex];
                clipIndex++;
                if (clip != null && clip.length > 0f)
                {
                    return clip;
                }
            }

            return null;
        }

        private static void Finish()
        {
            string text = Text(current);
            current = null;
            clipIndex = 0;
            nextDspTime = 0.0;
            Trace.Step("6", text);
        }

        private static string Text(PlayableUtterance playable)
        {
            return playable == null || playable.Source == null ? string.Empty : playable.Source.FullText;
        }

        private static AudioSource Source()
        {
            if (source != null)
            {
                return source;
            }

            if (host == null)
            {
                host = new GameObject(HostName);
                UnityEngine.Object.DontDestroyOnLoad(host);
            }

            source = host.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.spatialBlend = 0f;
            source.loop = false;
            return source;
        }

        private static float Volume()
        {
            VoiceDeputySettings settings = VoiceDeputyMod.Settings;
            if (settings == null)
            {
                return 1f;
            }

            // 两个音量分开是因为 TTS 音频通常比其它音源响；AudioSource.volume 上限是 1。
            return Mathf.Clamp01(settings.MasterVolume * settings.TtsVolume);
        }

        private static double GapSeconds()
        {
            VoiceDeputySettings settings = VoiceDeputyMod.Settings;
            return settings == null ? 0.0 : settings.GapMs / 1000.0;
        }
    }
}
