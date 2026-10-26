// DSH_GameLink · 命令通道（上位机 → 游戏）。
//
// 这是 mod 的第二条通道：原来只有「事件出向」（LiveChannel），现在多了「命令入向」。
//
// 落点（与事件通道同一个根目录，按 profile 分开）：
//   <root>/<profile>.link/
//     hello.json      游戏写：协议版本、会话 id、命令表（含 mutating）、探针/事件种类
//     state.json      游戏写：每 100 ms 或状态变化时原子替换（tmp + move）
//     cmd/<seq>.req   上位机写：`key=value` 行（**入向用 kv 不用 JSON** —— 游戏侧不带 JSON 解析器）
//     ack/<seq>.ack   游戏写：JSON（成功/失败 + 结果）
//
// 为什么入向用 kv：这个 mod 刻意零第三方 dll；JSON 解析器手写不值得，
//   而 kv 行有个好处——人能直接看懂、手写、diff。出向用 JSON 是因为**上位机读**，那边解析 JSON 零成本。
//
// 时序纪律（沿用 LiveChannel 的旁路契约）：
//   · Pump 由 GameComponentUpdate 每帧调，内部 50 ms 限频；
//   · 任何异常只记一次 error + 停一轮，**绝不向 Unity Update 冒泡**；
//   · 命令文件读不到/半写 ⇒ 本轮跳过，下一轮再读（不删、不报错）。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Verse;

namespace DSH_GameLink
{
    /// <summary>命令文件的参数袋：`key=value` 行。值一律当字符串，取的时候显式转。</summary>
    internal sealed class KvArgs
    {
        private readonly Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        internal string Get(string key)
        {
            string v;
            return map.TryGetValue(key, out v) ? v : null;
        }

        internal bool TryInt(string key, out int value)
        {
            value = 0;
            string raw = Get(key);
            if (string.IsNullOrEmpty(raw)) return false;
            return int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        internal bool Bool(string key)
        {
            string raw = Get(key);
            if (string.IsNullOrEmpty(raw)) return false;
            raw = raw.Trim();
            return raw == "1" || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(raw, "yes", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool TryParse(string text, out KvArgs args, out string error)
        {
            args = new KvArgs();
            error = null;
            if (text == null)
            {
                error = "命令文件为空";
                return false;
            }

            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0)
                {
                    error = "第 " + (i + 1) + " 行不是 key=value：" + line;
                    return false;
                }

                args.map[line.Substring(0, eq).Trim()] = line.Substring(eq + 1);
            }

            return true;
        }
    }

    internal static class CommandChannel
    {
        /// <summary>命令轮询间隔：50 ms。命令是"一条腿一次"，这个延迟对几万 tick 的运行等于 0。</summary>
        private const float PollIntervalSeconds = 0.05f;

        /// <summary>state.json 的重写间隔：100 ms。</summary>
        private const float StateIntervalSeconds = 0.1f;
        private const float IdleStateIntervalSeconds = 1f;

        private const string CmdSuffix = ".req";
        private const string AckSuffix = ".ack";
        private const string HelloFile = "hello.json";
        private const string StateFile = "state.json";
        private const int AckKeep = 200;

        internal static readonly string Session = Guid.NewGuid().ToString("N");

        private static string linkDir;
        private static string cmdDir;
        private static string ackDir;
        private static long lastSeq;
        private static bool helloWritten;
        private static float nextPollAt;
        private static float nextStateAt;
        private static bool broken;
        private static int consecutiveFailures;
        private static bool stateDirty = true;

        /// <summary>由 GameLinkComponent.GameComponentUpdate 每帧调用；内部自己限频。</summary>
        internal static void Pump()
        {
            if (broken) return;
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now < nextPollAt) return;
            nextPollAt = now + PollIntervalSeconds;

            try
            {
                if (!EnsureReady()) return;
                consecutiveFailures = 0;
                WriteHelloOnce();
                ProcessCommands();
                WriteStateIfDue(now);
            }
            catch (Exception ex)
            {
                // 通道坏掉不能拖累游戏，但也不能一次抖动就永久停掉（主菜单/加载中也会泵）：
                // 第一次失败报一条，连续失败 50 次才停。
                consecutiveFailures++;
                if (consecutiveFailures == 1)
                {
                    LiveChannel.Err("命令通道本轮失败（会自动重试）：" + LiveChannel.Describe(ex));
                }
                else if (consecutiveFailures >= 50)
                {
                    broken = true;
                    LiveChannel.Err("命令通道已停止（连续失败 50 次，游戏不受影响）");
                }
            }
        }

        /// <summary>执行过命令后调用：下一次泵立刻重写 state.json，别等 100 ms。</summary>
        internal static void MarkDirty()
        {
            stateDirty = true;
        }

        // ── 目录 ─────────────────────────────────────────────────

        private static bool EnsureReady()
        {
            if (linkDir != null) return true;

            string root = LiveChannel.ResolveDir();
            string profile = LiveChannel.ResolveProfileName();
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(profile)) return false;

            linkDir = Path.Combine(root, profile + ".link");
            cmdDir = Path.Combine(linkDir, "cmd");
            ackDir = Path.Combine(linkDir, "ack");
            Directory.CreateDirectory(cmdDir);
            Directory.CreateDirectory(ackDir);
            return true;
        }

        // ── hello / state ────────────────────────────────────────

        private static void WriteHelloOnce()
        {
            if (helloWritten) return;
            helloWritten = true;

            List<string> commands = new List<string>();
            for (int i = 0; i < Protocol.AllCommands.Length; i++)
            {
                commands.Add(new JsonBuf()
                    .Str("name", Protocol.AllCommands[i])
                    .Bool("mutating", Protocol.IsMutating(Protocol.AllCommands[i]))
                    .ToString());
            }

            string hello = new JsonBuf()
                .Num("v", Protocol.Version)
                .Str("session", Session)
                .Str("modVersion", LogTap.AboutModVersion())
                .Num("startedAt", NowUnix(), 3)
                .Raw("commands", "[" + string.Join(",", commands.ToArray()) + "]")
                .StrArray("probeKinds", Protocol.ProbeKinds)
                .StrArray("eventKinds", Protocol.EventKinds)
                .ToString();

            WriteAtomic(Path.Combine(linkDir, HelloFile), hello);
            LiveChannel.Note("命令通道已就绪：" + linkDir);
        }

        private static void WriteStateIfDue(float now)
        {
            if (!stateDirty && now < nextStateAt) return;
            // 空闲时 1 秒一次就够（上位机只在长命令期间才需要 100 ms 的粒度），
            // 不然会在盘上每秒重写 10 次同一个文件。
            nextStateAt = now + (TickDriver.IsActive ? StateIntervalSeconds : IdleStateIntervalSeconds);
            stateDirty = false;

            string state = new JsonBuf()
                .Num("v", Protocol.Version)
                .Str("session", Session)
                .Num("updatedAt", NowUnix(), 3)
                .Raw("game", TickDriver.StatusJson())
                .ToString();

            WriteAtomic(Path.Combine(linkDir, StateFile), state);
        }


        private static void WriteAtomic(string path, string content)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, content, Utf8NoBom);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        // ── 命令轮询 ─────────────────────────────────────────────

        private static void ProcessCommands()
        {
            string[] files = Directory.GetFiles(cmdDir, "*" + CmdSuffix);
            if (files.Length == 0) return;

            Array.Sort(files, CompareBySeq);

            for (int i = 0; i < files.Length; i++)
            {
                string file = files[i];
                long seq;
                if (!TrySeq(file, out seq))
                {
                    TryDelete(file);
                    continue;
                }

                if (seq <= lastSeq)
                {
                    TryDelete(file);
                    continue;
                }

                string text;
                try
                {
                    text = File.ReadAllText(file, Encoding.UTF8);
                }
                catch (IOException)
                {
                    continue; // 还被上位机占着：下一轮再读
                }

                KvArgs args;
                string parseError;
                if (!KvArgs.TryParse(text, out args, out parseError))
                {
                    WriteAck(seq, "?", false, Protocol.Errors.BadArgs, parseError, null);
                    TryDelete(file);
                    lastSeq = seq;
                    continue;
                }

                string command = args.Get("cmd");
                string payload;
                bool ok;
                string code;
                string message;
                CommandRouter.Dispatch(command, args, out payload, out ok, out code, out message);

                WriteAck(seq, command, ok, code, message, payload);
                TryDelete(file);
                lastSeq = seq;
                stateDirty = true;
            }

            PruneAcks();
        }

        private static int CompareBySeq(string a, string b)
        {
            long sa, sb;
            TrySeq(a, out sa);
            TrySeq(b, out sb);
            return sa.CompareTo(sb);
        }

        private static bool TrySeq(string path, out long seq)
        {
            seq = 0;
            string name = Path.GetFileNameWithoutExtension(path);
            return long.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out seq);
        }

        private static void WriteAck(long seq, string command, bool ok, string code, string message, string payload)
        {
            JsonBuf ack = new JsonBuf()
                .Num("v", Protocol.Version)
                .Num("seq", seq)
                .Str("cmd", command)
                .Bool("ok", ok)
                .Num("at", NowUnix(), 3)
                .Num("tick", TickDriver.SafeTicksGame());

            if (!ok)
            {
                ack.Raw("error", new JsonBuf().Str("code", code ?? Protocol.Errors.Internal).Str("message", message).ToString());
            }

            if (payload != null)
            {
                ack.Raw("result", payload);
            }

            WriteAtomic(Path.Combine(ackDir, seq.ToString(CultureInfo.InvariantCulture) + AckSuffix), ack.ToString());
        }

        private static void PruneAcks()
        {
            try
            {
                string[] files = Directory.GetFiles(ackDir, "*" + AckSuffix);
                if (files.Length <= AckKeep) return;
                Array.Sort(files, CompareBySeq);
                int remove = files.Length - AckKeep;
                for (int i = 0; i < remove; i++) TryDelete(files[i]);
            }
            catch (Exception)
            {
                // 清理失败不影响主流程
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception)
            {
                // 删不掉就留着，下一次轮询会因为 seq<=lastSeq 再删一次
            }
        }

        private static double NowUnix()
        {
            return (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }
    }
}
