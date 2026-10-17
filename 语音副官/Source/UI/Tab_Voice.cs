using UnityEngine;
using Verse;

namespace VoiceDeputy.UI
{
    internal sealed class Tab_Voice : IVoiceSettingsTab
    {
        private const float LabelColumnWidth = 130f;

        public void Draw(Rect inRect)
        {
            VoiceDeputySettings settings = VoiceDeputyMod.Settings;

            Rect statusRect = new Rect(inRect.x, inRect.y, inRect.width, 30f);
            Widgets.DrawMenuSection(statusRect);
            Widgets.Label(statusRect.ContractedBy(8f), StatusText());

            Listing_Standard listing = new Listing_Standard();
            listing.Begin(new Rect(inRect.x, inRect.y + 38f, inRect.width, inRect.height - 38f));

            settings.SetIfChanged(ref settings.BaseUrl, listing.TextEntryLabeled("服务地址", settings.BaseUrl));
            DrawApiKeyRow(listing, settings);
            listing.Gap(4f);
            settings.SetIfChanged(ref settings.Model, listing.TextEntryLabeled("模型", settings.Model));
            settings.SetIfChanged(ref settings.Voice, listing.TextEntryLabeled("音色", settings.Voice));
            HintText.Line(listing, "音色必须带模型名前缀，例：FunAudioLLM/CosyVoice2-0.5B:alex");

            listing.Gap(10f);
            Rect listenRow = listing.GetRect(32f);
            if (Widgets.ButtonText(new Rect(listenRow.x, listenRow.y, 120f, 32f), "试听"))
            {
                Announcer.SpeakDirect(SpeechCatalog.ListenText);
            }

            HintText.Line(listing, "点一次＝一次真实合成；失败时状态条显示 TtsClient 的状态码与响应体前 200 字。");

            listing.End();
        }

        // 可用性判定只归 TtsClient 的配置闸（I4）：这里只读它的失败快照。
        private static string StatusText()
        {
            LastError error = TtsClient.LastError;
            return error == null ? "已配置" : error.Describe();
        }

        private static void DrawApiKeyRow(Listing_Standard listing, VoiceDeputySettings settings)
        {
            const float clearWidth = 64f;
            Rect row = listing.GetRect(24f);
            Widgets.Label(new Rect(row.x, row.y, LabelColumnWidth, row.height), "API Key");

            Rect fieldRect = new Rect(row.x + LabelColumnWidth, row.y, row.width - LabelColumnWidth - clearWidth - 6f, row.height);
            string typed = GUI.PasswordField(fieldRect, settings.ApiKey ?? string.Empty, '•');
            settings.SetIfChanged(ref settings.ApiKey, typed);

            if (Widgets.ButtonText(new Rect(row.xMax - clearWidth, row.y, clearWidth, row.height), "清空"))
            {
                settings.SetIfChanged(ref settings.ApiKey, string.Empty);
            }

            HintText.Line(listing, "当前 " + MaskedKey(settings.ApiKey) + " · 明文存在本地 Config，别把 Config 发给别人");
        }

        private static string MaskedKey(string apiKey)
        {
            if (string.IsNullOrEmpty(apiKey))
            {
                return "未配置";
            }

            return apiKey.Length <= 4 ? "••••" : "••••" + apiKey.Substring(apiKey.Length - 4);
        }
    }
}
