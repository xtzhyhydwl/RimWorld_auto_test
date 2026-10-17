using UnityEngine;
using Verse;

namespace VoiceDeputy.UI
{
    internal static class ToggleRows
    {
        public const float RowHeight = 46f;
        public const float GroupHeaderHeight = 28f;
        private const float LabelHeight = 26f;
        private const float DescriptionIndent = 30f;

        public static float GroupedHeight(CategoryEntry[] entries)
        {
            float height = 0f;
            string group = null;
            foreach (CategoryEntry entry in entries)
            {
                if (entry.Group != group)
                {
                    group = entry.Group;
                    height += GroupHeaderHeight;
                }

                height += RowHeight;
            }

            return height;
        }

        public static void DrawGroupHeader(Rect row, string title)
        {
            GameFont font = Text.Font;
            Color color = GUI.color;
            Text.Font = GameFont.Medium;
            GUI.color = new Color(0.85f, 0.78f, 0.6f);
            Widgets.Label(row, title);
            GUI.color = color;
            Text.Font = font;
        }

        // 22 条播报类型和调试页那几条都走这里，开关行的长相只有一处定义。
        public static bool DrawRow(Rect row, string label, string description, bool value)
        {
            bool checkOn = value;
            Widgets.CheckboxLabeled(new Rect(row.x, row.y, row.width, LabelHeight), label, ref checkOn, false, null, null, placeCheckboxNearText: true);

            HintText.Draw(new Rect(row.x + DescriptionIndent, row.y + LabelHeight - 4f, row.width - DescriptionIndent, row.height - LabelHeight), description);
            return checkOn;
        }
    }

    internal static class HintText
    {
        public static void Draw(Rect rect, string text)
        {
            GameFont font = Text.Font;
            Color color = GUI.color;
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.72f, 0.72f, 0.72f);
            Widgets.Label(rect, text);
            GUI.color = color;
            Text.Font = font;
        }

        public static void Line(Listing_Standard listing, string text)
        {
            Draw(listing.GetRect(Text.SmallFontHeight), text);
        }
    }
}
