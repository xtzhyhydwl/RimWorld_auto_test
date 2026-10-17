using UnityEngine;
using Verse;

namespace VoiceDeputy.UI
{
    internal sealed class Tab_Debug : IVoiceSettingsTab
    {
        private string manualText = string.Empty;

        public void Draw(Rect inRect)
        {
            VoiceDeputySettings settings = VoiceDeputyMod.Settings;

            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);

            settings.SetIfChanged(ref settings.TraceEnabled, ToggleRows.DrawRow(listing.GetRect(ToggleRows.RowHeight), "路径日志", "每个环节打 [VD] 1…6 到 Player.log，供自动化测试读", settings.TraceEnabled));

            listing.Gap(8f);
            LibraryUsage usage = SpeechLibrary.Usage;
            listing.Label("缓存占用：" + usage.Describe());
            HintText.Line(listing, "缓存目录 " + SpeechLibrary.FolderName + "（全存档共用同一个目录）");
            settings.SetIfChanged(ref settings.LibraryLimit, Mathf.RoundToInt(listing.SliderLabeled("库上限 " + settings.LibraryLimit + " 条", settings.LibraryLimit, 0f, 20000f)));

            listing.Gap(10f);
            listing.Label("最近播报（只读）");
            Rect recentRect = listing.GetRect(140f);
            Widgets.DrawMenuSection(recentRect);
            Widgets.Label(recentRect.ContractedBy(8f), "（还没有播报记录）");

            listing.Gap(10f);
            listing.Label("手动朗读");
            Rect manualRow = listing.GetRect(28f);
            const float speakWidth = 100f;
            manualText = Widgets.TextField(new Rect(manualRow.x, manualRow.y, manualRow.width - speakWidth - 8f, manualRow.height), manualText);
            Widgets.ButtonText(new Rect(manualRow.xMax - speakWidth, manualRow.y, speakWidth, manualRow.height), "朗读", active: false);
            HintText.Line(listing, "TTS 还没接（施工图第 5 步），按钮先灰着；文本框先留着定接口。");

            listing.End();
        }
    }
}
