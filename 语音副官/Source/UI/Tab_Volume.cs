using System.Globalization;
using UnityEngine;
using Verse;

namespace VoiceDeputy.UI
{
    internal sealed class Tab_Volume : IVoiceSettingsTab
    {
        public void Draw(Rect inRect)
        {
            VoiceDeputySettings settings = VoiceDeputyMod.Settings;

            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);

            settings.SetIfChanged(ref settings.MasterVolume, listing.SliderLabeled("总音量 " + Percent(settings.MasterVolume), settings.MasterVolume, 0f, 2f));
            settings.SetIfChanged(ref settings.TtsVolume, listing.SliderLabeled("TTS 音量 " + Percent(settings.TtsVolume), settings.TtsVolume, 0f, 2f));
            settings.SetIfChanged(ref settings.Speed, listing.SliderLabeled("语速 " + settings.Speed.ToString("0.00", CultureInfo.InvariantCulture) + "×", settings.Speed, 0.5f, 2f));

            listing.Gap(8f);
            settings.SetIfChanged(ref settings.GapMs, Mathf.RoundToInt(listing.SliderLabeled("句间停顿 " + settings.GapMs + " ms", settings.GapMs, 0f, 500f)));
            settings.SetIfChanged(ref settings.BatchWindowMs, Mathf.RoundToInt(listing.SliderLabeled("合并窗口 " + Seconds(settings.BatchWindowMs) + " s", settings.BatchWindowMs, 0f, 5000f)));
            settings.SetIfChanged(ref settings.MaxNamedInBatch, Mathf.RoundToInt(listing.SliderLabeled("同句最多点名 " + settings.MaxNamedInBatch + " 人", settings.MaxNamedInBatch, 0f, 8f)));
            settings.SetIfChanged(ref settings.QueueLimit, Mathf.RoundToInt(listing.SliderLabeled("队列上限 " + settings.QueueLimit + " 句", settings.QueueLimit, 1f, 20f)));

            listing.Gap(10f);
            settings.SetIfChanged(ref settings.ThreatInterrupts, ToggleRows.DrawRow(listing.GetRect(ToggleRows.RowHeight), "威胁可打断", "威胁类插队，当前这句播完立刻播", settings.ThreatInterrupts));

            listing.End();
        }

        private static string Percent(float value)
        {
            return Mathf.RoundToInt(value * 100f).ToString(CultureInfo.InvariantCulture) + "%";
        }

        private static string Seconds(int milliseconds)
        {
            return (milliseconds / 1000f).ToString("0.0", CultureInfo.InvariantCulture);
        }
    }
}
