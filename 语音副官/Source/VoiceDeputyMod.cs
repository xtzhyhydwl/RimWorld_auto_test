using UnityEngine;
using Verse;

namespace VoiceDeputy
{
    public class VoiceDeputyMod : Mod
    {
        public static VoiceDeputySettings Settings;

        // TEMP：第 4 步进游戏验收后删除（连同 SpeechCatalog.SelfCheck 整段）。
        // 注册放在构造里、Settings 赋值之后：静态构造早于实例构造，那时 Settings 还是 null，
        // 万一 ExecuteWhenFinished 立即执行就会静默跳过。自检要等语言包就绪（腐烂后缀来自 Translate）。
        public VoiceDeputyMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<VoiceDeputySettings>();
            LongEventHandler.ExecuteWhenFinished(RunSelfCheckWhenTracing);
        }

        // TEMP：第 4 步进游戏验收后删除。
        private static void RunSelfCheckWhenTracing()
        {
            if (Settings != null && Settings.TraceEnabled)
            {
                SpeechCatalog.SelfCheck();
            }
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
