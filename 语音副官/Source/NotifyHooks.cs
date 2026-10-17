using HarmonyLib;
using Verse;

namespace VoiceDeputy
{
    // ① 角落小字通道（01 §4）：4 个 Message 重载全部汇流到 Message(Message, bool)，
    // 参数类型必须写全，否则按名 patch 会抛 AmbiguousMatchException。
    [HarmonyPatch(typeof(Messages), nameof(Messages.Message), new[] { typeof(Message), typeof(bool) })]
    internal static class NotifyHooks
    {
        [HarmonyPostfix]
        private static void Postfix(Message msg)
        {
            try
            {
                NotifyMessage(msg);
            }
            catch
            {
                Trace.Step("1", SpeechCatalog.ChannelName(SpeechChannel.Message));
            }
        }

        private static void NotifyMessage(Message msg)
        {
            if (msg == null)
            {
                return;
            }

            string defName = msg.def == null ? null : msg.def.defName;
            SpokenCategory? category = SpeechCatalog.ForMessage(defName, msg.text);

            // 02 §4.5：只有落点失败与兜底要原句，腐烂模板不带参数且按 tick 聚合。
            bool rotten = category == SpokenCategory.Rotten;
            Announcer.Receive(
                SpeechChannel.Message,
                category,
                defName,
                rotten ? null : new[] { msg.text },
                null,
                1,
                rotten ? "rotten" : null);
        }
    }
}
