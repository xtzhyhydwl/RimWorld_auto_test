using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;

namespace VoiceDeputy
{
    // ④⑤⑥⑦ 人身通道（01 §4）：崩溃 / 分娩 / 倒地 / 死亡 → Notice。
    // 三个句柄类各自只做"取事实 + 交 Announcer"，中文与模板一概不在这里。
    internal static class PawnHooks
    {
        // ④ 与 ⑥⑦ 的 pawn 都是宿主类的私有字段（1.6 实测），钩子体内读一次就算完，不往 Notice 里带对象。
        private static readonly FieldInfo TrackerPawnField = typeof(Pawn_HealthTracker).GetField("pawn", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo HandlerPawnField = typeof(MentalStateHandler).GetField("pawn", BindingFlags.Instance | BindingFlags.NonPublic);

        public static Pawn TrackerPawn(Pawn_HealthTracker tracker)
        {
            return ReadPawn(TrackerPawnField, tracker);
        }

        public static Pawn HandlerPawn(MentalStateHandler handler)
        {
            return ReadPawn(HandlerPawnField, handler);
        }

        // ④ 02 §2.1：只认殖民者、且该精神状态配置了崩溃信（49 个状态里 21 个有信）。
        public static void NotifyMentalBreak(Pawn pawn, MentalStateDef stateDef, bool started)
        {
            if (!started || pawn == null || stateDef == null || !pawn.IsColonist || stateDef.beginLetterDef == null)
            {
                return;
            }

            string name = NameOf(pawn);
            Announcer.Receive(
                SpeechChannel.SelfComputed,
                SpokenCategory.MentalBreak,
                stateDef.defName,
                new[] { name, stateDef.label },
                new[] { name },
                1,
                stateDef.defName);
        }

        // ⑤ 02 §2.1：只认人形（动物生产走消息，不归本类）。
        public static void NotifyBirth(Hediff_Pregnant pregnancy)
        {
            Pawn pawn = pregnancy == null ? null : pregnancy.pawn;
            if (pawn == null || !pawn.RaceProps.Humanlike)
            {
                return;
            }

            string name = NameOf(pawn);
            Announcer.Receive(
                SpeechChannel.SelfComputed,
                SpokenCategory.Birth,
                pawn.def.defName,
                new[] { name },
                new[] { name },
                1,
                null);
        }

        // ⑥ 原版零文字（02 §2.1）。MakeDowned 对任何 pawn 都调（含袭击者），
        // 所以用原版同一把尺子筛"玩家该知道的人"：殖民者 / 宠物 / 囚犯 / 访客。
        public static void NotifyDowned(Pawn pawn)
        {
            if (pawn == null || !PawnUtility.ShouldSendNotificationAbout(pawn))
            {
                return;
            }

            string name = NameOf(pawn);
            Announcer.Receive(
                SpeechChannel.SelfComputed,
                SpokenCategory.PawnDowned,
                pawn.def.defName,
                new[] { name },
                new[] { name },
                1,
                null);
        }

        // ⑦ 死亡单点（01 §4）：玩家派系的信与囚犯/访客/远行队的消息都从这里出，两支互斥。
        public static void NotifyKilled(Pawn pawn, DamageInfo? dinfo, Hediff hediff)
        {
            if (pawn == null)
            {
                return;
            }

            string name = NameOf(pawn);
            string cause = HealthUtility.GetDiedLetterText(pawn, dinfo, hediff).RawText;
            string batchKey = pawn.Faction == null || pawn.Faction.def == null ? null : pawn.Faction.def.defName;

            Announcer.Receive(
                SpeechChannel.SelfComputed,
                SpokenCategory.PawnDied,
                pawn.def.defName,
                new[] { name, cause },
                new[] { name },
                1,
                batchKey);
        }

        // 1.6 没有 Pawn.LabelShortCap（实测：Thing/Pawn 都没这个成员），LabelShort + 首字母大写等价。
        private static string NameOf(Pawn pawn)
        {
            string label = pawn.LabelShort;
            return label == null ? string.Empty : label.CapitalizeFirst();
        }

        private static Pawn ReadPawn(FieldInfo field, object owner)
        {
            if (field == null || owner == null)
            {
                return null;
            }

            return field.GetValue(owner) as Pawn;
        }
    }

    [HarmonyPatch(typeof(MentalStateHandler), nameof(MentalStateHandler.TryStartMentalState), new[]
    {
        typeof(MentalStateDef), typeof(string), typeof(bool), typeof(bool), typeof(bool), typeof(Pawn), typeof(bool), typeof(bool), typeof(bool),
    })]
    internal static class MentalBreakPatch
    {
        [HarmonyPostfix]
        private static void Postfix(MentalStateHandler __instance, MentalStateDef stateDef, bool __result)
        {
            try
            {
                PawnHooks.NotifyMentalBreak(PawnHooks.HandlerPawn(__instance), stateDef, __result);
            }
            catch
            {
                Trace.Step("1", SpeechCatalog.ChannelName(SpeechChannel.SelfComputed));
            }
        }
    }

    [HarmonyPatch(typeof(Hediff_Pregnant), nameof(Hediff_Pregnant.StartLabor), new Type[0])]
    internal static class BirthPatch
    {
        [HarmonyPostfix]
        private static void Postfix(Hediff_Pregnant __instance)
        {
            try
            {
                PawnHooks.NotifyBirth(__instance);
            }
            catch
            {
                Trace.Step("1", SpeechCatalog.ChannelName(SpeechChannel.SelfComputed));
            }
        }
    }

    // private 方法：不能写 nameof，方法名按字符串写死，参数类型写全。
    [HarmonyPatch(typeof(Pawn_HealthTracker), "MakeDowned", new[] { typeof(DamageInfo?), typeof(Hediff) })]
    internal static class DownedPatch
    {
        [HarmonyPostfix]
        private static void Postfix(Pawn_HealthTracker __instance)
        {
            try
            {
                PawnHooks.NotifyDowned(PawnHooks.TrackerPawn(__instance));
            }
            catch
            {
                Trace.Step("1", SpeechCatalog.ChannelName(SpeechChannel.SelfComputed));
            }
        }
    }

    [HarmonyPatch(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.NotifyPlayerOfKilled), new[]
    {
        typeof(DamageInfo?), typeof(Hediff), typeof(Caravan),
    })]
    internal static class KilledPatch
    {
        [HarmonyPostfix]
        private static void Postfix(Pawn_HealthTracker __instance, DamageInfo? dinfo, Hediff hediff)
        {
            try
            {
                PawnHooks.NotifyKilled(PawnHooks.TrackerPawn(__instance), dinfo, hediff);
            }
            catch
            {
                Trace.Step("1", SpeechCatalog.ChannelName(SpeechChannel.SelfComputed));
            }
        }
    }
}
