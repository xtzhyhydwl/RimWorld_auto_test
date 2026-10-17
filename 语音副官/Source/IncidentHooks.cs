using System;
using HarmonyLib;
using RimWorld;
using Verse;

namespace VoiceDeputy
{
    // ③ incident 通道（01 §4）：所有 incident 都必经 TryExecute，只有 __result == true 才算真发生。
    [HarmonyPatch(typeof(IncidentWorker), nameof(IncidentWorker.TryExecute), new[] { typeof(IncidentParms) })]
    internal static class IncidentHooks
    {
        [HarmonyPostfix]
        private static void Postfix(IncidentWorker __instance, IncidentParms parms, bool __result)
        {
            try
            {
                NotifyIncident(__instance, parms, __result);
            }
            catch
            {
                Trace.Step("1", SpeechCatalog.ChannelName(SpeechChannel.Incident));
            }
        }

        private static void NotifyIncident(IncidentWorker worker, IncidentParms parms, bool executed)
        {
            if (!executed || parms == null)
            {
                return;
            }

            // 1.6 的 IncidentParms 没有 def 字段（实测），事件本身在 worker.def 上。
            IncidentDef def = worker == null ? null : worker.def;
            string defName = def == null ? null : def.defName;
            SpokenCategory? category = SpeechCatalog.ForIncident(
                defName,
                def == null || def.category == null ? null : def.category.defName,
                parms.raidStrategy == null ? null : parms.raidStrategy.defName,
                parms.raidArrivalMode == null ? null : parms.raidArrivalMode.defName,
                def == null ? null : WorkerClassNames(def.workerClass));

            string label = def == null ? null : def.label;
            string factionName = parms.faction == null ? null : parms.faction.Name;
            int pawns = Math.Max(1, parms.pawnCount);

            string[] args;
            int count = 1;
            string batchKey = null;
            switch (category)
            {
                case SpokenCategory.ThreatRaid:
                case SpokenCategory.ThreatSiege:
                    args = new[] { factionName, label };
                    count = pawns;
                    break;
                case SpokenCategory.ThreatAnimalInsanity:
                    args = new[] { label };
                    count = pawns;
                    break;
                case SpokenCategory.Visitors:
                    args = new[] { factionName };
                    count = pawns;
                    break;
                case SpokenCategory.TraderArrival:
                    args = new[] { factionName, label };
                    break;
                case SpokenCategory.DiseaseOutbreak:
                    args = new[] { label };
                    count = pawns;
                    batchKey = defName;
                    break;
                default:
                    // WeatherExtreme / EnvHazard / OtherIncident：只念 def 的 label。
                    args = new[] { label };
                    break;
            }

            Announcer.Receive(SpeechChannel.Incident, category, defName, args, null, count, batchKey);
        }

        // 02 §3.1 第 3 步要「workerClass 是 IncidentWorker_Disease 或其子类」，而绑定表是按子串比的，
        // 所以这里交整条继承链的名字（含子类名，也含被继承的 IncidentWorker_Disease）。
        private static string WorkerClassNames(Type workerClass)
        {
            if (workerClass == null)
            {
                return null;
            }

            string names = workerClass.Name;
            Type baseType = workerClass.BaseType;
            while (baseType != null)
            {
                names = names + "|" + baseType.Name;
                baseType = baseType.BaseType;
            }

            return names;
        }
    }
}
