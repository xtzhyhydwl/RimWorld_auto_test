using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using RimWorld;
using Verse;

namespace VoiceDeputy
{
    // ⑧ 蓝图缺料（01 §4）：一对补丁算一个点——玩家手动放置标记 + 蓝图落地后算缺口。
    internal static class BlueprintHooks
    {
        // Prefix +1 / Postfix −1：只有玩家在设计器里手动放置的那一次才算，自动重建 / 样板 / 攻城蓝图不算。
        private static int manualPlacementDepth;

        public static void EnterManualPlacement()
        {
            try
            {
                manualPlacementDepth++;
            }
            catch
            {
                // 标记失衡会把"自动重建"误判成手动放置，所以宁可复位（01 §5.3 失败模式）。
                manualPlacementDepth = 0;
            }
        }

        public static void ExitManualPlacement()
        {
            try
            {
                if (manualPlacementDepth > 0)
                {
                    manualPlacementDepth--;
                }
            }
            catch
            {
                manualPlacementDepth = 0;
            }
        }

        // 复用原版同款算法（02 §2.1）：CostListAdjusted 逐项比 ResourceCounter，只报第一个缺口项。
        public static void NotifyMissingMaterials(BuildableDef sourceDef, Map map, ThingDef stuff)
        {
            if (manualPlacementDepth <= 0 || sourceDef == null || map == null || map.resourceCounter == null)
            {
                return;
            }

            List<ThingDefCountClass> costs = sourceDef.CostListAdjusted(stuff, false);
            if (costs == null)
            {
                return;
            }

            foreach (ThingDefCountClass cost in costs)
            {
                if (cost == null || cost.thingDef == null || cost.count <= 0)
                {
                    continue;
                }

                int missing = cost.count - map.resourceCounter.GetCount(cost.thingDef);
                if (missing <= 0)
                {
                    continue;
                }

                Announcer.Receive(
                    SpeechChannel.SelfComputed,
                    SpokenCategory.BuildMissingMaterials,
                    sourceDef.defName,
                    new[] { missing.ToString(CultureInfo.InvariantCulture), cost.thingDef.label },
                    null,
                    1,
                    null);
                return;
            }
        }
    }

    [HarmonyPatch(typeof(Designator_Build), nameof(Designator_Build.DesignateSingleCell), new[] { typeof(IntVec3) })]
    internal static class ManualPlacementMarker
    {
        [HarmonyPrefix]
        private static void Prefix()
        {
            BlueprintHooks.EnterManualPlacement();
        }

        [HarmonyPostfix]
        private static void Postfix()
        {
            BlueprintHooks.ExitManualPlacement();
        }
    }

    [HarmonyPatch(typeof(GenConstruct), nameof(GenConstruct.PlaceBlueprintForBuild), new[]
    {
        typeof(BuildableDef), typeof(IntVec3), typeof(Map), typeof(Rot4), typeof(Faction), typeof(ThingDef),
        typeof(Precept_ThingStyle), typeof(ThingStyleDef), typeof(bool),
    })]
    internal static class MissingMaterialsHook
    {
        [HarmonyPostfix]
        private static void Postfix(BuildableDef sourceDef, Map map, ThingDef stuff)
        {
            try
            {
                BlueprintHooks.NotifyMissingMaterials(sourceDef, map, stuff);
            }
            catch
            {
                Trace.Step("1", SpeechCatalog.ChannelName(SpeechChannel.SelfComputed));
            }
        }
    }
}
