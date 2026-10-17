using UnityEngine;
using Verse;

namespace VoiceDeputy.UI
{
    internal interface IVoiceSettingsTab
    {
        void Draw(Rect inRect);
    }

    internal sealed class SettingsTab
    {
        public readonly string Title;
        public readonly IVoiceSettingsTab Body;

        public SettingsTab(string title, IVoiceSettingsTab body)
        {
            Title = title;
            Body = body;
        }
    }

    public class Dialog_VoiceSettings : Window
    {
        private const float TabWidth = 150f;
        private const float TabHeight = 38f;
        private const float HeaderHeight = 42f;
        private const float FooterHeight = 46f;

        private static readonly SettingsTab[] Tabs =
        {
            new SettingsTab("播报类型", new Tab_SpokenCategories()),
            new SettingsTab("语音", new Tab_Voice()),
            new SettingsTab("音量与节奏", new Tab_Volume()),
            new SettingsTab("调试", new Tab_Debug()),
        };

        private int selectedTab;

        public Dialog_VoiceSettings()
        {
            doCloseX = true;
            doCloseButton = false;
            closeOnAccept = false;
            closeOnCancel = false;
            forcePause = true;
            absorbInputAroundWindow = true;
        }

        public override Vector2 InitialSize
        {
            get { return new Vector2(920f, 780f); }
        }

        public override void DoWindowContents(Rect inRect)
        {
            VoiceDeputySettings settings = VoiceDeputyMod.Settings;

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, HeaderHeight), "副官语音设置");
            Text.Font = GameFont.Small;

            float bodyHeight = inRect.height - HeaderHeight - FooterHeight;
            for (int i = 0; i < Tabs.Length; i++)
            {
                Rect tabRect = new Rect(0f, HeaderHeight + i * (TabHeight + 6f), TabWidth, TabHeight);
                if (Widgets.ButtonText(tabRect, Tabs[i].Title))
                {
                    selectedTab = i;
                }

                if (i == selectedTab)
                {
                    Widgets.DrawHighlightSelected(tabRect);
                }
            }

            Rect contentRect = new Rect(TabWidth + 8f, HeaderHeight, inRect.width - TabWidth - 8f, bodyHeight);
            Widgets.DrawMenuSection(contentRect);
            Tabs[selectedTab].Body.Draw(contentRect.ContractedBy(14f));

            float footerY = inRect.height - FooterHeight + 4f;
            if (Widgets.ButtonText(new Rect(0f, footerY, 140f, 34f), "恢复默认"))
            {
                settings.ResetToDefaults();
            }

            if (Widgets.ButtonText(new Rect(inRect.width - 140f, footerY, 140f, 34f), "关闭"))
            {
                Close();
            }

            // 没有「应用」按钮：控件改动在改的那一刻就置脏，当帧写盘。
            settings.FlushIfDirty();
        }

        public override void PostClose()
        {
            base.PostClose();
            VoiceDeputyMod.Settings.FlushIfDirty();
        }
    }
}
