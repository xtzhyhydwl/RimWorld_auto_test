// DSH_GameLink · 日志转发（Harmony 后置补丁）+ mod 加载埋点。
//
// 三条：
//   · Log.Message —— 只转发文本里含 `[VD]`（产品 mod 的路径日志）或含 `[DSH-TEST]`（测试埋点，
//     约定见 .dsh\skills\rimworld-auto-test-closed-loop\docs\测试SOP\埋点协议.md）的行；其余 Message 不进事件文件，否则会被刷屏；
//   · Log.Warning —— 同一条过滤（其它 mod 的警告每秒刷屏，会把事件文件变成垃圾）；
//   · Log.Error —— 全转发，用来抓红字（这是「为什么这局没跑通」的主要证据）；
//   · 自己打的 `[GameLink] ...` 一律跳过，否则「写盘失败 → Log.Error → 转发 → 再写盘失败」会自激。
//
// 另外本类的静态构造就是 mod 的初始化入口（[StaticConstructorOnStartup]），末尾打一条
//   `[DSH-TEST] DSH_GameLink loaded version=<About.xml 的 modVersion>`
// 这是「mod 起来了」的唯一发射点 —— 没有它，`live --until 'kind=log text~[DSH-TEST]'` 永远等不到东西，
// 而且它同时证明「补丁挂上了」（这条 Message 自己会被本类的后缀转进 ndjson）。
//
// 为什么显式 Patch 而不是 PatchAll：本程序集里没有任何 [HarmonyPatch] 注解时 PatchAll 会静默什么都不做
// （BikeProbe run-1 踩过，见 mods/BikeProbe/Source/1.6/Probe_Harmony.cs 顶部）。显式挂 + 每个目标单独 try/catch，
// 挂不上会响亮地打进 Player.log。
//
// 补丁本身的异常绝不外抛：日志已经打出去了，转发失败只是少一条事件。

using System;
using System.IO;
using System.Reflection;
using System.Xml.Linq;
using HarmonyLib;
using Verse;

namespace DSH_GameLink
{
    [StaticConstructorOnStartup]
    public static class LogTap
    {
        internal const string HarmonyId = "dsh.gamelink";
        private const string VoicePrefix = "[VD]";
        private const string TestPrefix = "[DSH-TEST]";
        private const string AboutFileName = "About.xml";
        private const string ModVersionElement = "modVersion";

        static LogTap()
        {
            try
            {
                Harmony harmony = new Harmony(HarmonyId);
                PatchOne(harmony, nameof(Log.Message), nameof(PostfixMessage));
                PatchOne(harmony, nameof(Log.Warning), nameof(PostfixWarning));
                PatchOne(harmony, nameof(Log.Error), nameof(PostfixError));
            }
            catch (Exception ex)
            {
                LiveChannel.Err("日志转发初始化失败（日志事件全部不可用）：" + LiveChannel.Describe(ex));
            }

            EmitLoadedBanner();
        }

        public static void PostfixMessage(string __0)
        {
            Emit("Message", __0, true);
        }

        public static void PostfixWarning(string __0)
        {
            Emit("Warning", __0, true);
        }

        public static void PostfixError(string __0)
        {
            Emit("Error", __0, false);
        }

        // ── mod 加载埋点（正面埋点，见 .dsh\skills\rimworld-auto-test-closed-loop\docs\测试SOP\埋点协议.md）─────────────

        // 埋点放在静态构造里而不是新组件里：这是 mod 最早的、必定执行的入口，
        // 而 GameComponent 要等有了 Game 才被创建，主菜单阶段根本没有。
        private static void EmitLoadedBanner()
        {
            try
            {
                Log.Message(TestPrefix + " DSH_GameLink loaded version=" + AboutModVersion());
            }
            catch (Exception ex)
            {
                LiveChannel.Err("打加载埋点失败（测试无法断言 mod 是否起来）：" + LiveChannel.Describe(ex));
            }
        }

        // About.xml 的 modVersion 是这条埋点的唯一真源，所以直接读文件：不走 ModMetaData，
        // 1.7（Waylith）下那个 API 是否还提供 modVersion 没有证据。
        private static string AboutModVersion()
        {
            const string Unknown = "unknown";
            const string Absent = "(About.xml 无 modVersion)";   // 本 mod 现在就是这种：About.xml 里没有这个元素
            try
            {
                string assemblyPath = typeof(LogTap).Assembly.Location;
                if (string.IsNullOrEmpty(assemblyPath))
                {
                    return Unknown;
                }

                DirectoryInfo assembliesDir = Directory.GetParent(assemblyPath);        // …\<mod>\Assemblies\DSH_GameLink.dll
                if (assembliesDir == null || assembliesDir.Parent == null)
                {
                    return Unknown;
                }

                string aboutPath = Path.Combine(assembliesDir.Parent.FullName, "About", AboutFileName);
                if (!File.Exists(aboutPath))
                {
                    return Unknown;
                }

                XElement element = XDocument.Load(aboutPath).Root.Element(ModVersionElement);
                if (element == null || string.IsNullOrWhiteSpace(element.Value))
                {
                    return Absent;
                }

                return element.Value.Trim();
            }
            catch (Exception)
            {
                // 读版本失败不能让埋点消失：打 unknown 比什么都不打强。
                return Unknown;
            }
        }

        // ── 转发 ─────────────────────────────────────────────────────

        private static void Emit(string level, string text, bool onlyMarked)
        {
            try
            {
                if (string.IsNullOrEmpty(text))
                {
                    return;
                }

                if (text.StartsWith(LiveChannel.OwnPrefix, StringComparison.Ordinal))
                {
                    return;
                }

                if (onlyMarked && !IsMarked(text))
                {
                    return;
                }

                new EventLine("log").Text("level", level).Text("text", text).Emit();
            }
            catch (Exception)
            {
                // 转发是旁路：失败不记录（记录也会走同一条可能已经坏掉的通道）。
            }
        }

        // 两种标记：产品 mod 的路径日志 `[VD]`，测试埋点 `[DSH-TEST]`。
        private static bool IsMarked(string text)
        {
            return text.IndexOf(VoicePrefix, StringComparison.Ordinal) >= 0
                || text.IndexOf(TestPrefix, StringComparison.Ordinal) >= 0;
        }

        private static void PatchOne(Harmony harmony, string targetName, string postfixName)
        {
            MethodInfo target = AccessTools.Method(typeof(Log), targetName, new[] { typeof(string) });
            if (target == null)
            {
                LiveChannel.Err("未找到 Verse.Log." + targetName + "(string)，这一类日志不转发");
                return;
            }

            MethodInfo postfix = typeof(LogTap).GetMethod(postfixName, BindingFlags.Static | BindingFlags.Public);
            if (postfix == null)
            {
                LiveChannel.Err("找不到自己的后缀方法 " + postfixName + "，这一类日志不转发");
                return;
            }

            try
            {
                harmony.Patch(target, postfix: new HarmonyMethod(postfix));
            }
            catch (Exception ex)
            {
                LiveChannel.Err("挂 Verse.Log." + targetName + " 后缀失败：" + LiveChannel.Describe(ex));
            }
        }
    }
}
