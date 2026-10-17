// DSH_GameLink · 日志转发（Harmony 后置补丁）。
//
// 三条：
//   · Log.Message —— 只转发文本里含 `[VD]` 的行（产品 mod 的路径日志，前缀 [VD]）；
//   · Log.Warning / Log.Error —— 全转发，用来抓红字（这两条是「为什么这局没跑通」的主要证据）；
//   · 自己打的 `[GameLink] ...` 一律跳过，否则「写盘失败 → Log.Error → 转发 → 再写盘失败」会自激。
//
// 为什么显式 Patch 而不是 PatchAll：本程序集里没有任何 [HarmonyPatch] 注解时 PatchAll 会静默什么都不做
// （BikeProbe run-1 踩过，见 mods/BikeProbe/Source/1.6/Probe_Harmony.cs 顶部）。显式挂 + 每个目标单独 try/catch，
// 挂不上会响亮地打进 Player.log。
//
// 补丁本身的异常绝不外抛：日志已经打出去了，转发失败只是少一条事件。

using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace DSH_GameLink
{
    [StaticConstructorOnStartup]
    public static class LogTap
    {
        internal const string HarmonyId = "dsh.gamelink";
        private const string VoicePrefix = "[VD]";

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
        }

        public static void PostfixMessage(string __0)
        {
            Emit("Message", __0, true);
        }

        public static void PostfixWarning(string __0)
        {
            Emit("Warning", __0, false);
        }

        public static void PostfixError(string __0)
        {
            Emit("Error", __0, false);
        }

        private static void Emit(string level, string text, bool onlyVoice)
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

                if (onlyVoice && text.IndexOf(VoicePrefix, StringComparison.Ordinal) < 0)
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
