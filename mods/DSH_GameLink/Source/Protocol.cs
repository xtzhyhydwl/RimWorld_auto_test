// DSH_GameLink · 命令协议常量（唯一真源）。
//
// 版本、命令名、错误码、探针种类、事件种类都只在这里出现一次；
// 上位机（src/gamelink.js）与文档（docs/GameLink-协议-v1.md）都抄这里，不许各写一份。

namespace DSH_GameLink
{
    internal static class Protocol
    {
        /// <summary>协议版本。上位机只认这个数字；不认就报错，不做半吊子兼容。</summary>
        internal const int Version = 1;

        internal static class Commands
        {
            /// <summary>推时间：{toTick?, speed?, stopWhen?, watch?, forceSpeed?, maxWallClockMs?, maxTicks?}。</summary>
            internal const string Advance = "time.advance";

            /// <summary>只读：当前 tick / 档位 / 暂停 / 正在跑的那一程 / 上一次结果。</summary>
            internal const string Status = "time.status";

            /// <summary>停掉当前这一程并暂停游戏。</summary>
            internal const string Cancel = "time.cancel";

            /// <summary>自检：Harmony 挂上了吗、能不能读到信件/消息、当前世界状态。</summary>
            internal const string Probe = "time.probe";
        }

        internal static class Errors
        {
            internal const string UnknownCommand = "E_UNKNOWN_CMD";
            internal const string BadArgs = "E_BAD_ARGS";
            internal const string Busy = "E_BUSY";
            internal const string NoGame = "E_NO_GAME";
            internal const string Harmony = "E_HARMONY";
            internal const string ExternalPause = "E_EXTERNAL_PAUSE";
            internal const string Timeout = "E_TIMEOUT";
            internal const string Internal = "E_INTERNAL";
        }

        /// <summary>停下来的原因（`state.last.stopReason`）。</summary>
        internal static class StopReasons
        {
            internal const string Target = "target";
            internal const string Probe = "probe";
            internal const string Timeout = "timeout";
            internal const string MaxTicks = "maxTicks";
            internal const string Cancelled = "cancelled";
            internal const string Stalled = "stalled";
        }

        /// <summary>探针种类。加一种 = 在 Probes.cs 里加一个分支，并把名字加到这张表。</summary>
        internal static readonly string[] ProbeKinds = { "letter", "message" };

        /// <summary>本 mod 会往事件通道（NDJSON）写的 kind。</summary>
        internal static readonly string[] EventKinds =
        {
            "game_loaded", "colony_started", "window", "heartbeat", "log", "error", "probe_hit"
        };

        /// <summary>命令是否改游戏状态 —— 在 hello.json 里如实标出来（开源用户一眼能看出谁动游戏）。</summary>
        internal static bool IsMutating(string command)
        {
            return command == Commands.Advance || command == Commands.Cancel;
        }

        internal static readonly string[] AllCommands =
        {
            Commands.Advance, Commands.Status, Commands.Cancel, Commands.Probe
        };
    }
}
