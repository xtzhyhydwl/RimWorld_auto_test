using HarmonyLib;
using Verse;

namespace VoiceDeputy
{
    // ② 信件通道（01 §4）：全部信件都汇流到 ReceiveLetter(Letter, string, int, bool)，
    // 3 个重载，参数类型必须写全。
    [HarmonyPatch(typeof(LetterStack), nameof(LetterStack.ReceiveLetter), new[] { typeof(Letter), typeof(string), typeof(int), typeof(bool) })]
    internal static class LetterHooks
    {
        [HarmonyPostfix]
        private static void Postfix(Letter let)
        {
            try
            {
                NotifyLetter(let);
            }
            catch
            {
                Trace.Step("1", SpeechCatalog.ChannelName(SpeechChannel.Letter));
            }
        }

        private static void NotifyLetter(Letter let)
        {
            if (let == null)
            {
                return;
            }

            string defName = let.def == null ? null : let.def.defName;
            SpokenCategory? category = SpeechCatalog.ForLetter(defName);
            if (!category.HasValue)
            {
                Announcer.Receive(SpeechChannel.Letter, null, defName);
                return;
            }

            // 02 §4.5：Quests 与 OtherLetter 都只念信件的标题。
            string label = let.Label.RawText;
            Announcer.Receive(SpeechChannel.Letter, category, defName, new[] { label });
        }
    }
}
