using System;
using Verse;

namespace VoiceDeputy
{
    public enum SpeechStyle
    {
        Deputy,
        Neutral,
        Terse
    }

    public class VoiceDeputySettings : ModSettings
    {
        public const string DefaultBaseUrl = "https://api.siliconflow.cn/v1/audio/speech";
        public const string DefaultModel = "FunAudioLLM/CosyVoice2-0.5B";
        public const string DefaultVoice = "FunAudioLLM/CosyVoice2-0.5B:alex";
        public const string DefaultResponseFormat = "wav";

        public bool ThreatRaid;
        public bool ThreatSiege;
        public bool ThreatAnimalInsanity;
        public bool PawnDowned;
        public bool PawnDied;
        public bool MentalBreak;
        public bool Birth;
        public bool DiseaseOutbreak;
        public bool DiseaseSpread;
        public bool Addiction;
        public bool TraderArrival;
        public bool Visitors;
        public bool Quests;
        public bool WeatherExtreme;
        public bool EnvHazard;
        public bool Rotten;
        public bool BuildMissingMaterials;
        public bool BuildPlaceFailed;
        public bool BillSuspended;
        public bool OtherIncident;
        public bool OtherLetter;
        public bool OtherMessage;

        public string BaseUrl;
        public string ApiKey;
        public string Model;
        public string Voice;
        public string ResponseFormat;

        public float MasterVolume;
        public float TtsVolume;
        public float Speed;
        public int GapMs;
        public int BatchWindowMs;
        public int MaxNamedInBatch;
        public int QueueLimit;
        public bool ThreatInterrupts;

        public int LibraryLimit;
        public int JanitorIntervalSeconds;
        public int WarmupJobsPerMinute;
        public bool TraceEnabled;
        public SpeechStyle SpeechStyle;

        private bool dirty;

        public VoiceDeputySettings()
        {
            SetDefaults();
        }

        public void ResetToDefaults()
        {
            SetDefaults();
            MarkDirty();
        }

        public bool IsCategoryEnabled(SpokenCategory category)
        {
            return CategorySlot(category);
        }

        public void SetCategoryEnabled(SpokenCategory category, bool value)
        {
            if (CategorySlot(category) == value)
            {
                return;
            }

            CategorySlot(category) = value;
            MarkDirty();
        }

        // 原版控件的开关/按钮走 GUI.Button，不会置 GUI.changed，所以脏标记只能靠比较新旧值。
        public void SetIfChanged(ref string field, string value)
        {
            if (field == value)
            {
                return;
            }

            field = value;
            MarkDirty();
        }

        public void SetIfChanged(ref float field, float value)
        {
            if (field == value)
            {
                return;
            }

            field = value;
            MarkDirty();
        }

        public void SetIfChanged(ref int field, int value)
        {
            if (field == value)
            {
                return;
            }

            field = value;
            MarkDirty();
        }

        public void SetIfChanged(ref bool field, bool value)
        {
            if (field == value)
            {
                return;
            }

            field = value;
            MarkDirty();
        }

        public void MarkDirty()
        {
            dirty = true;
        }

        public void FlushIfDirty()
        {
            if (!dirty)
            {
                return;
            }

            dirty = false;
            Write();
        }

        // 用 ref 返回而不是 get/set 两个 switch：加一类播报只在这里加一行。
        private ref bool CategorySlot(SpokenCategory category)
        {
            switch (category)
            {
                case SpokenCategory.ThreatRaid: return ref ThreatRaid;
                case SpokenCategory.ThreatSiege: return ref ThreatSiege;
                case SpokenCategory.ThreatAnimalInsanity: return ref ThreatAnimalInsanity;
                case SpokenCategory.PawnDowned: return ref PawnDowned;
                case SpokenCategory.PawnDied: return ref PawnDied;
                case SpokenCategory.MentalBreak: return ref MentalBreak;
                case SpokenCategory.Birth: return ref Birth;
                case SpokenCategory.DiseaseOutbreak: return ref DiseaseOutbreak;
                case SpokenCategory.DiseaseSpread: return ref DiseaseSpread;
                case SpokenCategory.Addiction: return ref Addiction;
                case SpokenCategory.TraderArrival: return ref TraderArrival;
                case SpokenCategory.Visitors: return ref Visitors;
                case SpokenCategory.Quests: return ref Quests;
                case SpokenCategory.WeatherExtreme: return ref WeatherExtreme;
                case SpokenCategory.EnvHazard: return ref EnvHazard;
                case SpokenCategory.Rotten: return ref Rotten;
                case SpokenCategory.BuildMissingMaterials: return ref BuildMissingMaterials;
                case SpokenCategory.BuildPlaceFailed: return ref BuildPlaceFailed;
                case SpokenCategory.BillSuspended: return ref BillSuspended;
                case SpokenCategory.OtherIncident: return ref OtherIncident;
                case SpokenCategory.OtherLetter: return ref OtherLetter;
                case SpokenCategory.OtherMessage: return ref OtherMessage;
            }

            throw new ArgumentOutOfRangeException(nameof(category), category, null);
        }

        private void SetDefaults()
        {
            // 类别开关的默认值只有 CategoryEntry.DefaultEnabled 一处（02 §5），本方法是唯一读者。
            foreach (CategoryEntry entry in SpeechCatalog.Entries)
            {
                SetCategoryEnabled(entry.Category, entry.DefaultEnabled);
            }

            BaseUrl = DefaultBaseUrl;
            ApiKey = string.Empty;
            Model = DefaultModel;
            Voice = DefaultVoice;
            ResponseFormat = DefaultResponseFormat;

            MasterVolume = 1f;
            TtsVolume = 1f;
            Speed = 1f;
            GapMs = 120;
            BatchWindowMs = 1500;
            MaxNamedInBatch = 2;
            QueueLimit = 6;
            ThreatInterrupts = true;

            LibraryLimit = 3000;
            JanitorIntervalSeconds = 300;
            WarmupJobsPerMinute = 100000;
            TraceEnabled = false;
            SpeechStyle = VoiceDeputy.SpeechStyle.Deputy;
        }

        // 全字段走同一个 Look：缺键 ⇒ 保持当前值（构造函数已设过默认值），写盘 ⇒ 一律落盘。
        // 不能直接写 Scribe_Values.Look(ref X, "k", X)：保存模式下"等于缺省值"的项会被跳过，
        // 而这里第三参传的就是当前值 ⇒ 每一行都被判成"等于缺省"而永远写不出来（配置 XML 成空对象）。
        public override void ExposeData()
        {
            foreach (CategoryEntry entry in SpeechCatalog.Entries)
            {
                bool enabled = IsCategoryEnabled(entry.Category);
                Look(ref enabled, "enabled_" + entry.Category);
                CategorySlot(entry.Category) = enabled;
            }

            Look(ref BaseUrl, "baseUrl");
            Look(ref ApiKey, "apiKey");
            Look(ref Model, "model");
            Look(ref Voice, "voice");
            Look(ref ResponseFormat, "responseFormat");

            Look(ref MasterVolume, "masterVolume");
            Look(ref TtsVolume, "ttsVolume");
            Look(ref Speed, "speed");
            Look(ref GapMs, "gapMs");
            Look(ref BatchWindowMs, "batchWindowMs");
            Look(ref MaxNamedInBatch, "maxNamedInBatch");
            Look(ref QueueLimit, "queueLimit");
            Look(ref ThreatInterrupts, "threatInterrupts");

            Look(ref LibraryLimit, "libraryLimit");
            Look(ref JanitorIntervalSeconds, "janitorIntervalSeconds");
            Look(ref WarmupJobsPerMinute, "warmupJobsPerMinute");
            Look(ref TraceEnabled, "traceEnabled");
            Look(ref SpeechStyle, "speechStyle");
        }

        // 唯一的存取点：defaultValue 传当前值（读档缺键 ⇒ 原样保留），forceSave 让保存不跳过。
        private static void Look<T>(ref T field, string key)
        {
            Scribe_Values.Look(ref field, key, field, true);
        }
    }
}
