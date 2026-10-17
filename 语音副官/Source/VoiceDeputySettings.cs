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
            TraceEnabled = false;
            SpeechStyle = VoiceDeputy.SpeechStyle.Deputy;
        }

        // 存盘时把「当前值」当缺省值交给 Scribe：老配置文件里没有的键会保持代码里的默认值，
        // 而不是被重置成 0 / false。构造函数已经设过默认值，所以调用时当前值就是默认值。
        public override void ExposeData()
        {
            foreach (CategoryEntry entry in SpeechCatalog.Entries)
            {
                bool enabled = IsCategoryEnabled(entry.Category);
                Scribe_Values.Look(ref enabled, "enabled_" + entry.Category, enabled);
                CategorySlot(entry.Category) = enabled;
            }

            Scribe_Values.Look(ref BaseUrl, "baseUrl", BaseUrl);
            Scribe_Values.Look(ref ApiKey, "apiKey", ApiKey);
            Scribe_Values.Look(ref Model, "model", Model);
            Scribe_Values.Look(ref Voice, "voice", Voice);
            Scribe_Values.Look(ref ResponseFormat, "responseFormat", ResponseFormat);

            Scribe_Values.Look(ref MasterVolume, "masterVolume", MasterVolume);
            Scribe_Values.Look(ref TtsVolume, "ttsVolume", TtsVolume);
            Scribe_Values.Look(ref Speed, "speed", Speed);
            Scribe_Values.Look(ref GapMs, "gapMs", GapMs);
            Scribe_Values.Look(ref BatchWindowMs, "batchWindowMs", BatchWindowMs);
            Scribe_Values.Look(ref MaxNamedInBatch, "maxNamedInBatch", MaxNamedInBatch);
            Scribe_Values.Look(ref QueueLimit, "queueLimit", QueueLimit);
            Scribe_Values.Look(ref ThreatInterrupts, "threatInterrupts", ThreatInterrupts);

            Scribe_Values.Look(ref LibraryLimit, "libraryLimit", LibraryLimit);
            Scribe_Values.Look(ref TraceEnabled, "traceEnabled", TraceEnabled);
            Scribe_Values.Look(ref SpeechStyle, "speechStyle", VoiceDeputy.SpeechStyle.Deputy);
        }
    }
}
