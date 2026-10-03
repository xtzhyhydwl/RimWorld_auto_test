using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Verse;

namespace FoodControl.Game
{
    /// <summary>
    /// **意图的进程间出口** —— 把"AI 打算做什么"送出游戏进程。
    ///
    /// ## 用户原话（2026-10-03）
    ///
    /// > 「虽然现在是你操作……但也可以做一个**进程间通信**嘛，把内容发过去。
    /// >   具体这个 AI 的实力最后落在哪，是落在游戏内，还是落在 DSH harness 这边，
    /// >   这个**之后再看**。但是现在我希望有这样一个功能。」
    ///
    /// 所以这里定的**不是落点，是接口**：
    ///
    /// | 出口 | 类 | 状态 |
    /// | --- | --- | --- |
    /// | 游戏日志 | `LogIntentSink` | ✅ 已实现 |
    /// | **JSONL 文件** | `JsonlIntentSink` | ✅ 已实现（**外部任何进程读文件就能拿**） |
    /// | HTTP 推送 | `HttpIntentSink` | ⬜ 预留（一行 `IntentBoard.AddSink` 就能上） |
    /// | 其他 | 实现 `IIntentSink` | ⬜ 扩展点 |
    ///
    /// ## 为什么先做"文件"而不是"网络"
    ///
    /// 本项目已经有一条**验证过的**外部观测通道：mod 写文件、测试脚本读文件
    /// （隔离实例的 `-savedatafolder` 指向 `profiles/vanilla`，我们**有完全的文件访问权**）。
    /// 它不需要任何网络、不需要 DLL、不依赖桥接，而且**天然可回放**（文件留在磁盘上）。
    /// 网络推送以后要加也很便宜 —— 但那是个"要不要"的决定，不是"能不能"的问题。
    ///
    /// ## 格式：一行一个 JSON 对象（jsonl）
    ///
    /// 选 jsonl 而不是一个 JSON 数组，理由有三条：
    ///   ① **追加写**，不用重写整个文件（O(1) 而不是 O(n)），也不怕写到一半崩
    ///   ② 外部可以 `tail` 实时跟（`Get-Content -Wait` / `tail -f`）
    ///   ③ 一行坏了不影响别的行（可恢复）
    /// ⚠ 与遥测文件一样，写盘必须用 UTF-8 **无 BOM**（BOM 会让第一行解析失败）。
    /// </summary>
    public static class IntentIpc
    {
        /// <summary>意图流文件名（放在隔离存档目录里，与 `foodcontrol-telemetry.jsonl` 同级）</summary>
        public const string FileName = "foodcontrol-intents.jsonl";

        /// <summary>单个文件的上限；超了就**截断重开**（跑 15 天不会无限涨）</summary>
        private const long MaxBytes = 4L * 1024 * 1024;

        private static JsonlIntentSink _fileSink;
        private static LogIntentSink _logSink;

        /// <summary>意图流的绝对路径（没有文件时也给出"本该在哪"，便于排查）</summary>
        public static string IntentPath
        {
            get
            {
                string root = null;
                try { root = GenFilePaths.SaveDataFolderPath; }
                catch (Exception) { }
                if (string.IsNullOrEmpty(root)) root = Path.GetTempPath();
                return Path.Combine(root, FileName);
            }
        }

        /// <summary>
        /// **装上默认出口**（幂等）。由 `ModBoot` 在启动时调一次。
        ///
        /// 日志出口默认**关着**（`Enabled=false`）：一次决策可能产生好几条意图，
        /// 全打到 Player.log 会很难读。需要时在窗口里一键打开（开关就是 sink 的 `Enabled`）。
        /// </summary>
        public static void InstallDefaults()
        {
            if (_fileSink == null)
            {
                _fileSink = new JsonlIntentSink();
                IntentBoard.AddSink(_fileSink);
            }
            if (_logSink == null)
            {
                _logSink = new LogIntentSink();
                IntentBoard.AddSink(_logSink);
            }
            WriteHeaderOnce();
        }

        public static JsonlIntentSink FileSink
        {
            get { return _fileSink; }
        }

        public static LogIntentSink LogSink
        {
            get { return _logSink; }
        }

        /// <summary>窗口里显示的"发到哪儿了"（含状态，便于确认"真的写了吗"）</summary>
        public static string DescribeOutlets()
        {
            var parts = new List<string>();
            if (_fileSink != null) parts.Add(_fileSink.Describe());
            if (_logSink != null) parts.Add(_logSink.Describe());
            if (parts.Count == 0) return "（没有装任何出口）";
            return string.Join(" ｜ ", parts.ToArray());
        }

        // ------------------------------------------------------------------
        // 探针/测试用的查询入口（游戏内钩子）
        // ------------------------------------------------------------------

        /// <summary>最近若干条意图的 JSON 数组（探针 `CallStatic` 用：必须是 public static + 参数简单）</summary>
        public static string RecentJson(int n)
        {
            var sb = new StringBuilder(2048);
            sb.Append('[');
            List<AiIntent> list = IntentBoard.Recent(n <= 0 ? 20 : n);
            for (int i = 0; i < list.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(IntentJson(list[i]));
            }
            sb.Append(']');
            return sb.ToString();
        }

        /// <summary>"即将要做"的 JSON 数组（**这是窗口和外部都最关心的那一栏**）</summary>
        public static string PendingJson()
        {
            var sb = new StringBuilder(1024);
            sb.Append('[');
            List<AiIntent> list = IntentBoard.Pending();
            for (int i = 0; i < list.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(IntentJson(list[i]));
            }
            sb.Append(']');
            return sb.ToString();
        }

        /// <summary>一条意图 → JSON 对象（**唯一**的序列化处：文件、探针、将来 HTTP 全用它）</summary>
        public static string IntentJson(AiIntent it)
        {
            if (it == null) return "null";
            var sb = new StringBuilder(256);
            sb.Append('{');
            bool first = true;
            Json.KVi(sb, ref first, "seq", it.Seq);
            Json.KVi(sb, ref first, "tick", it.Tick);
            Json.KVi(sb, ref first, "day", it.Day);
            Json.KVs(sb, ref first, "domain", it.DomainId);
            Json.KVs(sb, ref first, "kind", it.Kind.ToString());
            Json.KVs(sb, ref first, "state", it.State.ToString());
            Json.KVs(sb, ref first, "summary", it.Summary);
            Json.KVs(sb, ref first, "reason", it.Reason);
            Json.KVs(sb, ref first, "target", it.Target);
            Json.KVs(sb, ref first, "source", it.Source);
            Json.KVs(sb, ref first, "result", it.Result);
            Json.KVi(sb, ref first, "updatedTick", it.UpdatedTick);
            sb.Append('}');
            return sb.ToString();
        }

        private static bool _headerWritten;

        /// <summary>
        /// 第一次写之前记一行注释（`#` 开头）。
        ///
        /// ⚠ 这不是合法 JSONL —— 所以解析端**必须跳过 `#` 开头的行**。
        ///   值这个代价：排查时最想知道的是"这个文件是谁写的、写到哪"，而这句话
        ///   放在文件第一行最省事。（本项目踩过"路径对不上账"的坑，见 Telemetry 的同类注释。）
        /// </summary>
        private static void WriteHeaderOnce()
        {
            if (_headerWritten) return;
            _headerWritten = true;
            try
            {
                string path = IntentPath;
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                if (!File.Exists(path))
                {
                    string header = "# FoodControl 意图流（jsonl，一行一条；解析请跳过 # 开头的行）"
                        + " path=" + path + "\n";
                    File.WriteAllText(path, header, new UTF8Encoding(false));
                }
                try { Log.Message("[FoodControl] 意图流文件：" + path); } catch (Exception) { }
            }
            catch (Exception e)
            {
                if (_fileSink != null) _fileSink.LastError = "写头失败: " + e.Message;
            }
        }

        /// <summary>**JSONL 文件出口**：一条一行，追加写</summary>
        public sealed class JsonlIntentSink : IIntentSink
        {
            public bool Enabled = true;
            public int Written;
            public int Failed;
            public string LastError = "";

            public string Name
            {
                get { return "文件(jsonl)"; }
            }

            public string Location
            {
                get { return IntentPath; }
            }

            public string Describe()
            {
                string state = Enabled ? "开" : "关";
                return string.Format("{0}[{1}] 写 {2} 失败 {3}{4}",
                    Name, state, Written, Failed,
                    string.IsNullOrEmpty(LastError) ? "" : " 最后一错: " + LastError);
            }

            public void OnIntent(AiIntent intent, string change)
            {
                if (!Enabled || intent == null) return;
                try
                {
                    string path = IntentPath;
                    string dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                    // 超限就截断重开（附一行说明，免得看到"文件突然变小"以为出 bug）
                    if (File.Exists(path))
                    {
                        var fi = new FileInfo(path);
                        if (fi.Length > MaxBytes)
                        {
                            File.WriteAllText(path,
                                "# 超过 " + MaxBytes + " 字节，截断重开\n", new UTF8Encoding(false));
                        }
                    }

                    File.AppendAllText(path, IntentJson(intent) + "\n", new UTF8Encoding(false));
                    Written++;
                }
                catch (Exception e)
                {
                    // ⚠ 写盘失败**绝不能影响游戏**，但必须留痕（否则"没写进去"会被当成"没发生"）
                    Failed++;
                    LastError = e.Message;
                }
            }
        }

        /// <summary>**游戏日志出口**：默认关（决策多的时候会刷屏）</summary>
        public sealed class LogIntentSink : IIntentSink
        {
            public bool Enabled = false;
            public int Written;

            public string Name
            {
                get { return "游戏日志"; }
            }

            public string Describe()
            {
                return string.Format("{0}[{1}] 写 {2}", Name, Enabled ? "开" : "关", Written);
            }

            public void OnIntent(AiIntent intent, string change)
            {
                if (!Enabled || intent == null) return;
                try
                {
                    Log.Message("[FoodControl·意图] " + intent.ToLine());
                    Written++;
                }
                catch (Exception) { }
            }
        }

        /// <summary>
        /// **HTTP 推送出口（预留，未实现）**。
        ///
        /// 为什么现在只留壳：用户明确说落点「**之后再看**」。
        /// 但接口现在就定下来 —— 将来要接 DSH 那边，只需要
        /// ① 在这个类里把 `OnIntent` 写成真正的 HTTP POST（或用后台线程 + 队列，
        ///    ⚠ **绝不能在游戏主线程上做网络调用**，桥接调用排在主线程上会挂死整个游戏）
        /// ② `IntentIpc.InstallDefaults()` 里 `AddSink(new HttpIntentSink(...))`
        /// **不需要动任何产生意图的代码** —— 这就是"加接口就行"的意思。
        /// </summary>
        public sealed class HttpIntentSink : IIntentSink
        {
            public string Endpoint;
            public bool Enabled;

            public HttpIntentSink(string endpoint)
            {
                Endpoint = endpoint ?? "";
            }

            public string Name
            {
                get { return "HTTP推送"; }
            }

            public string Describe()
            {
                return string.Format("{0}[{1}] → {2}（未实现，预留扩展点）",
                    Name, Enabled ? "开" : "关", string.IsNullOrEmpty(Endpoint) ? "(未设地址)" : Endpoint);
            }

            public void OnIntent(AiIntent intent, string change)
            {
                // 有意不实现：见类注释。留空而不是抛异常，免得被 AddSink 之后炸掉别人的出口。
            }
        }
    }
}
