namespace VoiceDeputy
{
    // 四个通道名固定（01 §6.14 / N4）；日志拼写由 SpeechCatalog.ChannelName 给。
    public enum SpeechChannel
    {
        Incident,
        Letter,
        Message,
        SelfComputed
    }

    // 一条绑定怎么把 Value 和现场事实比（02 §2.2）。
    public enum BindingKey
    {
        DefName,
        Category,
        RaidStrategyPrefix,
        ArrivalModeContains,
        WorkerClass,
        DefNameContains
    }

    public sealed class SourceBinding
    {
        public readonly SpokenCategory Category;
        public readonly SpeechChannel Channel;
        public readonly BindingKey Kind;
        public readonly string Value;
        public readonly string TextSuffix;

        public SourceBinding(SpokenCategory category, SpeechChannel channel, BindingKey kind, string value, string textSuffix = null)
        {
            Category = category;
            Channel = channel;
            Kind = kind;
            Value = value;
            TextSuffix = textSuffix;
        }
    }

    // 28 个成员 = 22 单事件 + 5 聚合 + 1 裸文本（01 §6.18）。
    public enum SpeechPhrase
    {
        Phrase_ThreatRaid,
        Phrase_ThreatSiege,
        Phrase_ThreatAnimalInsanity,
        Phrase_PawnDowned,
        Phrase_PawnDied,
        Phrase_MentalBreak,
        Phrase_Birth,
        Phrase_DiseaseOutbreak,
        Phrase_DiseaseSpread,
        Phrase_Addiction,
        Phrase_TraderArrival,
        Phrase_Visitors,
        Phrase_Quests,
        Phrase_WeatherExtreme,
        Phrase_EnvHazard,
        Phrase_Rotten,
        Phrase_BuildMissingMaterials,
        Phrase_BuildPlaceFailed,
        Phrase_BillSuspended,
        Phrase_OtherIncident,
        Phrase_OtherLetter,
        Phrase_OtherMessage,
        Phrase_PawnDied_Batch,
        Phrase_MentalBreak_Batch,
        Phrase_DiseaseOutbreak_Batch,
        Phrase_DiseaseSpread_Batch,
        Phrase_Rotten_Batch,
        Phrase_RawText
    }

    public enum PhraseForm
    {
        SingleEvent,
        Batch,
        RawText
    }

    // Sentence 只作缓存键的取值，不出现在 Segment.Kind（03 §2）。
    public enum SegmentKind
    {
        Const,
        Enum,
        PersonName,
        Number,
        RawText,
        Sentence
    }

    // Label / Group / Description 的中文数据只写在 SpeechCatalog 里。
    public sealed class CategoryEntry
    {
        public readonly SpokenCategory Category;
        public readonly string Group;
        public readonly string Label;
        public readonly string Description;
        public readonly int Priority;
        public readonly SpeechPhrase? BatchPhrase;
        public readonly bool DefaultEnabled;

        public CategoryEntry(SpokenCategory category, string group, string label, string description, int priority, SpeechPhrase? batchPhrase, bool defaultEnabled)
        {
            Category = category;
            Group = group;
            Label = label;
            Description = description;
            Priority = priority;
            BatchPhrase = batchPhrase;
            DefaultEnabled = defaultEnabled;
        }
    }

    public sealed class PhraseEntry
    {
        public readonly SpeechPhrase Id;
        public readonly SpokenCategory? Category;
        public readonly PhraseForm Form;
        public readonly string Template;
        public readonly SegmentKind[] ArgKinds;

        public PhraseEntry(SpeechPhrase id, SpokenCategory? category, PhraseForm form, string template, SegmentKind[] argKinds)
        {
            Id = id;
            Category = category;
            Form = form;
            Template = template;
            ArgKinds = argKinds;
        }
    }
}
