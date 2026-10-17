using UnityEngine;
using Verse;

namespace VoiceDeputy.UI
{
    // 调试页（05 §5）：缓存占用 / 清缓存 / 库上限 / 清理周期 / 预热节流 / 最近播报 / 手动朗读。
    // 删除动作只有 LibraryJanitor.ClearAll 一个入口（04 §4.6），本页不碰文件、不碰索引。
    internal sealed class Tab_Debug : IVoiceSettingsTab
    {
        private const float ClearButtonWidth = 96f;
        private const float RecentBoxHeight = 160f;
        private const float RecentRowHeight = 20f;
        private const int RecentTextLimit = 40;

        private string manualText = string.Empty;
        private Vector2 recentScroll;

        public void Draw(Rect inRect)
        {
            VoiceDeputySettings settings = VoiceDeputyMod.Settings;

            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);

            settings.SetIfChanged(ref settings.TraceEnabled, ToggleRows.DrawRow(listing.GetRect(ToggleRows.RowHeight), "路径日志", "每个环节打 [VD] 1…6 到 Player.log，供自动化测试读", settings.TraceEnabled));

            listing.Gap(8f);
            DrawLibraryRow(listing);
            settings.SetIfChanged(ref settings.LibraryLimit, Mathf.RoundToInt(listing.SliderLabeled("库上限 " + settings.LibraryLimit + " 条", settings.LibraryLimit, 0f, 20000f)));
            HintText.Line(listing, "超限先淘汰最旧的整句 / 原句 / 不在名单里的名字；常量片段（L1）永不淘汰。");

            int interval = settings.JanitorIntervalSeconds;
            int chosenInterval = Mathf.RoundToInt(listing.SliderLabeled("清理周期 " + interval + " 秒", interval, 30f, 3600f));
            settings.SetIfChanged(ref settings.JanitorIntervalSeconds, chosenInterval);

            int perMinute = settings.WarmupJobsPerMinute;
            int chosenPerMinute = Mathf.RoundToInt(listing.SliderLabeled("预热节流 " + perMinute + " 条/分", perMinute, 1f, 1000000f));
            settings.SetIfChanged(ref settings.WarmupJobsPerMinute, chosenPerMinute);
            HintText.Line(listing, "默认 100000 ＝ 不节流；调小后每 2 秒那一批预热按此上限出队。");

            listing.Gap(10f);
            DrawRecent(listing);
            DrawManualSpeak(listing);

            listing.End();
        }

        private static void DrawLibraryRow(Listing_Standard listing)
        {
            Rect row = listing.GetRect(30f);
            Widgets.Label(new Rect(row.x, row.y, row.width - ClearButtonWidth - 8f, row.height), "缓存占用：" + SpeechLibrary.Usage.Describe() + "（预热待发 " + WarmupScheduler.PendingCount + "）");

            if (Widgets.ButtonText(new Rect(row.xMax - ClearButtonWidth, row.y, ClearButtonWidth, row.height), "清缓存"))
            {
                LibraryJanitor.ClearAll();
            }

            HintText.Line(listing, "缓存目录 " + SpeechLibrary.FolderName + "（全存档共用）；清缓存会删掉全部 .wav 并清空索引。");
        }

        // 最近播报（05 §5）：时间 / 类别 / 原文 / 是否整句命中 / 用时 ms。只读。
        private void DrawRecent(Listing_Standard listing)
        {
            RecentPlay[] recent = VoicePlayer.RecentSnapshot();
            listing.Label("最近播报（" + recent.Length + " / " + VoicePlayer.RecentCapacity + "）");

            Rect box = listing.GetRect(RecentBoxHeight);
            Widgets.DrawMenuSection(box);
            Rect inner = box.ContractedBy(6f);
            if (recent.Length == 0)
            {
                Widgets.Label(inner, "（还没有播报记录）");
                return;
            }

            Rect view = new Rect(0f, 0f, inner.width - 16f, recent.Length * RecentRowHeight);
            Widgets.BeginScrollView(inner, ref recentScroll, view);
            for (int i = 0; i < recent.Length; i++)
            {
                HintText.Draw(new Rect(0f, i * RecentRowHeight, view.width, RecentRowHeight), Describe(recent[i]));
            }

            Widgets.EndScrollView();
        }

        private void DrawManualSpeak(Listing_Standard listing)
        {
            listing.Gap(10f);
            listing.Label("手动朗读");

            Rect row = listing.GetRect(28f);
            const float speakWidth = 100f;
            manualText = Widgets.TextField(new Rect(row.x, row.y, row.width - speakWidth - 8f, row.height), manualText);
            if (Widgets.ButtonText(new Rect(row.xMax - speakWidth, row.y, speakWidth, row.height), "朗读"))
            {
                Announcer.SpeakDirect(manualText);
            }

            HintText.Line(listing, "绕过事件与开关，直接把文本切句、合成、播放；用于调音色。");
        }

        private static string Describe(RecentPlay play)
        {
            return Age(play.Tick) + " · " + play.Category + " · " + Shorten(play.Text) + " · " + (play.FromSentenceCache ? "整句" : "片段") + " · " + play.Millis + " ms";
        }

        private static string Age(int tick)
        {
            int seconds = (GenTicks.TicksGame - tick) / 60;
            if (seconds < 0)
            {
                seconds = 0;
            }

            return seconds < 60 ? seconds + " 秒前" : (seconds / 60) + " 分钟前";
        }

        private static string Shorten(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            return text.Length <= RecentTextLimit ? text : text.Substring(0, RecentTextLimit) + "…";
        }
    }
}
