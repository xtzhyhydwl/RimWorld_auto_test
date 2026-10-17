namespace VoiceDeputy
{
    // 片段法的单位（01 §6.2）。SegmentKind 在 SpeechTypes.cs（第 3 步已定义），这里只建 Segment，不重复定义枚举。
    public readonly struct Segment
    {
        public readonly string Text;
        public readonly SegmentKind Kind;

        public Segment(string text, SegmentKind kind)
        {
            Text = text;
            Kind = kind;
        }
    }
}
