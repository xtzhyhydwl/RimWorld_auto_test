using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace VoiceDeputy
{
    // 最近播报的一行（05 §5）：时间 / 类别 / 原文 / 是否整句命中 / 用时 ms。
    public sealed class RecentPlay
    {
        public readonly int Tick;
        public readonly SpokenCategory Category;
        public readonly string Text;
        public readonly bool FromSentenceCache;
        public readonly int Millis;

        public RecentPlay(int tick, SpokenCategory category, string text, bool fromSentenceCache, int millis)
        {
            Tick = tick;
            Category = category;
            Text = text ?? string.Empty;
            FromSentenceCache = fromSentenceCache;
            Millis = millis;
        }
    }

    // 出声（01 §5.4）：独占"当前句 + clip 游标"，从 SpeechQueue 取下一句，dspTime 按序连播；
    // 音量与停顿每帧现读 Settings，优先级只在开场时从 Category 查一次。
    // [StaticConstructorOnStartup]：本类有 UnityEngine.Object 类型的静态字段（host / source），
    // 让静态构造在主线程、资源加载之前跑完，否则原版会报 "probably needs a StaticConstructorOnStartup"。
    [StaticConstructorOnStartup]
    public static class VoicePlayer
    {
        public const int RecentCapacity = 20;

        private const string HostName = "VoiceDeputy";

        private static readonly List<RecentPlay> recent = new List<RecentPlay>(RecentCapacity);

        private static GameObject host;
        private static AudioSource source;
        private static PlayableUtterance current;
        private static int clipIndex;
        private static double nextDspTime;
        private static int startedMs;

        public static int CurrentPriority { get; private set; }

        public static RecentPlay[] RecentSnapshot()
        {
            return recent.ToArray();
        }

        public static void Tick()
        {
            if (current == null)
            {
                PlayableUtterance next;
                if (!SpeechQueue.TryTake(out next))
                {
                    return;
                }

                Start(next);
            }

            AudioSource audio = Source();
            if (audio == null)
            {
                current = null;
                CurrentPriority = 0;
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

        private static void Start(PlayableUtterance playable)
        {
            current = playable;
            clipIndex = 0;
            nextDspTime = 0.0;
            startedMs = Environment.TickCount;
            CurrentPriority = playable.Source == null ? 0 : SpeechCatalog.PriorityOf(playable.Source.Category);
            Trace.Step("4", Text(playable));
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

        // 播完置空 current（clip 交给 GC，I6），并把这一条记进"最近播报"。
        private static void Finish()
        {
            PlayableUtterance finished = current;
            current = null;
            clipIndex = 0;
            nextDspTime = 0.0;
            CurrentPriority = 0;

            string text = Text(finished);
            int millis = Environment.TickCount - startedMs;

            if (recent.Count >= RecentCapacity)
            {
                recent.RemoveAt(recent.Count - 1);
            }

            recent.Insert(0, new RecentPlay(
                GenTicks.TicksGame,
                finished == null || finished.Source == null ? SpokenCategory.OtherMessage : finished.Source.Category,
                text,
                finished != null && finished.FromSentenceCache,
                millis));

            Trace.Step("6", text);

            // 03 §6：整句播完才交给预热排整句缓存（播之前排等于给没听过的句子花钱）。
            if (finished != null)
            {
                WarmupScheduler.EnqueueSentence(finished.Source);
            }
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
