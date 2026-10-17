using Verse;

namespace VoiceDeputy
{
    // 钩子交给成句层的唯一载荷（01 §6.1）：出钩子之后全链只有 enum/string/int，没有 Verse 对象（I1）。
    // 数组构造时拷一份：钩子交的是现场临时拼的数组，不能在别处被改。
    public sealed class Notice
    {
        public readonly SpokenCategory Category;
        public readonly SpeechPhrase Phrase;
        public readonly string[] Args;
        public readonly string[] Names;
        public readonly int Count;
        public readonly string BatchKey;
        public readonly int Tick;

        public Notice(SpokenCategory category, SpeechPhrase phrase, string[] args, string[] names, int count, string batchKey)
        {
            Category = category;
            Phrase = phrase;
            Args = Copy(args);
            Names = Copy(names);
            Count = count;
            BatchKey = batchKey;
            Tick = GenTicks.TicksGame;
        }

        private static string[] Copy(string[] values)
        {
            return values == null || values.Length == 0 ? EmptyTexts : (string[])values.Clone();
        }

        private static readonly string[] EmptyTexts = new string[0];
    }
}
