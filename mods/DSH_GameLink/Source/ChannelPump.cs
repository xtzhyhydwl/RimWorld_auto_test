// DSH_GameLink · 主菜单也要泵命令。
//
// 为什么单独一个类：`GameLinkComponent` 是 `GameComponent`，**只有局内才 Update**。
// 而"上位机推时间"最常犯的错就是在主菜单就下命令 —— 那时命令没人读，上位机只能等 15 s 拿 E_NO_ACK，
// 报出来的原因是"通道没在泵"，跟真实原因（还没进图）差得很远。
//
// 挂 `Verse.Root.Update`（每帧都跑，主菜单也跑）只做一件事：`CommandChannel.Pump()`。
// Pump 自己 50 ms 限频、自己吞异常，所以这里的开销就是一次时间戳比较。
// 挂不上不致命：通道退化成"只在局内可用"，日志里会说明。

using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace DSH_GameLink
{
    [StaticConstructorOnStartup]
    internal static class ChannelPump
    {
        private const string HarmonyId = "dsh.gamelink.channelpump";

        static ChannelPump()
        {
            Install();
        }

        internal static void Install()
        {
            try
            {
                MethodBase target = AccessTools.Method(typeof(Root), "Update");
                if (target == null)
                {
                    LiveChannel.Err("主菜单泵不可用（Root.Update 找不到）：命令只在局内处理");
                    return;
                }

                MethodInfo prefix = typeof(ChannelPump).GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic);
                if (prefix == null)
                {
                    LiveChannel.Err("主菜单泵不可用（ChannelPump.Prefix 找不到）");
                    return;
                }

                new Harmony(HarmonyId).Patch(target, new HarmonyMethod(prefix));
            }
            catch (Exception ex)
            {
                LiveChannel.Err("主菜单泵挂载失败（命令只在局内处理）：" + LiveChannel.Describe(ex));
            }
        }

        /// <summary>每帧；Pump 内部限频。</summary>
        private static void Prefix()
        {
            try
            {
                CommandChannel.Pump();
            }
            catch (Exception)
            {
                // Pump 已经自己兜住异常；这里再兜一层是因为**绝不能**把异常丢进 Unity 的 Update。
            }
        }
    }
}
