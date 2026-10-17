using UnityEngine;
using Verse;

namespace VoiceDeputy
{
    public class VoiceDeputyMod : Mod
    {
        public static VoiceDeputySettings Settings;

        public VoiceDeputyMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<VoiceDeputySettings>();
        }

        public override string SettingsCategory()
        {
            return "副官语音";
        }

        public override void DoSettingsWindowContents(UnityEngine.Rect inRect)
        {
            if (Widgets.ButtonText(new Rect(inRect.x, inRect.y, 260f, 32f), "打开副官语音设置"))
            {
                Find.WindowStack.Add(new UI.Dialog_VoiceSettings());
            }
        }
    }
}
