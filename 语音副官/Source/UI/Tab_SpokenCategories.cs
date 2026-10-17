using UnityEngine;
using Verse;

namespace VoiceDeputy.UI
{
    internal sealed class Tab_SpokenCategories : IVoiceSettingsTab
    {
        private Vector2 scroll;

        public void Draw(Rect inRect)
        {
            VoiceDeputySettings settings = VoiceDeputyMod.Settings;
            Rect content = new Rect(0f, 0f, inRect.width - 20f, ToggleRows.GroupedHeight(SpeechCatalog.Entries));

            Widgets.BeginScrollView(inRect, ref scroll, content);
            float y = 0f;
            string group = null;
            foreach (CategoryEntry entry in SpeechCatalog.Entries)
            {
                if (entry.Group != group)
                {
                    group = entry.Group;
                    ToggleRows.DrawGroupHeader(new Rect(0f, y, content.width, ToggleRows.GroupHeaderHeight), group);
                    y += ToggleRows.GroupHeaderHeight;
                }

                bool current = settings.IsCategoryEnabled(entry.Category);
                bool next = ToggleRows.DrawRow(new Rect(0f, y, content.width, ToggleRows.RowHeight), entry.Label, entry.Description, current);
                if (next != current)
                {
                    settings.SetCategoryEnabled(entry.Category, next);
                }

                y += ToggleRows.RowHeight;
            }

            Widgets.EndScrollView();
        }
    }
}
