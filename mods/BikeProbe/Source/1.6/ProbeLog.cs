// BikeProbe · 探针的唯一日志出口（用完即弃，不属于产品代码）
//
// 目标（方案 §⑤ 取证纪律第 1 条）：每一行证据都要能落盘复核。
//   · Player.log —— 统一前缀 `[BikeProbe] <探针编号> ...`，可以直接 grep
//   · JSONL      —— `Application.persistentDataPath/BikeProbe/bikeprobe.jsonl`，一行一条记录
// 两者内容一致；JSONL 只是把同一句话结构化成 {tick, probe, msg}。

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using Verse;

namespace BikeProbe
{
    public static class ProbeLog
    {
        public const string Prefix = "[BikeProbe]";

        private static readonly object Gate = new object();
        private static readonly List<string> Buffer = new List<string>(256);
        private static string jsonlPath;
        private static string jsonlError;
        private static bool fileBroken;

        /// <summary>
        /// JSONL 的真实落点。
        ///
        /// ★★★ 2026-10-13（run-2）修复 ②：run-1 启动日志里是
        /// `jsonl=&lt;unavailable:UnauthorizedAccessException&gt;`。
        /// 原因（run-1 已实测）：这里原先把落点定在
        /// `Application.persistentDataPath\BikeProbe\` = `%USERPROFILE%\AppData\LocalLow\Ludeon Studios\...`，
        /// 而游戏进程由受限沙箱的 shell 启动、**继承了"工作区之外禁止写"的文件策略**
        /// ⇒ `Directory.CreateDirectory` 抛 `UnauthorizedAccessException` ⇒ 整个 JSONL 通道静默丢弃
        /// （`fileBroken=true`，`Record()` 直接 return），只剩 `Player.log` 一条通道。
        ///
        /// 改成**游戏进程自己确实能写的位置**，两个候选**按顺序真的试一遍**，成的那个才用：
        ///   ① `GenFilePaths.SaveDataFolderPath`（= `-savedatafolder` 指向的目录，本实例里
        ///      就是 `$WS\profiles\bike`；run-1 实测这里一切写入正常）下的 `BikeProbe\` 子目录
        ///      —— 证据跟着实例走，销毁探针时删 `profiles/bike/BikeProbe/` 即可。
        ///   ② 兜底：`Application.persistentDataPath`（原来的落点）。它在本机被拒，
        ///      但换一个不受沙箱约束的启动方式时它是对的，所以保留为第二候选。
        ///
        /// ⚠ 失败时必须把**路径**和**异常**一起打出来（run-1 只留了异常类型名，把路径信息丢了，
        ///   这正是"找不出它想写哪"的原因）。
        /// </summary>
        public static string JsonlPath
        {
            get
            {
                if (jsonlPath != null)
                {
                    return jsonlPath;
                }

                List<string> tried = new List<string>();
                string[] candidates = BuildCandidates();
                for (int i = 0; i < candidates.Length; i++)
                {
                    string dir = candidates[i];
                    if (string.IsNullOrEmpty(dir))
                    {
                        continue;
                    }
                    try
                    {
                        if (!Directory.Exists(dir))
                        {
                            Directory.CreateDirectory(dir);
                        }
                        // 真的写一次（探针头一行），证明"这个位置游戏进程确实能写"。
                        string probe = Path.Combine(dir, "bikeprobe.jsonl");
                        File.AppendAllText(probe, "", new UTF8Encoding(false));
                        jsonlPath = probe;
                        return jsonlPath;
                    }
                    catch (Exception ex)
                    {
                        tried.Add(dir + " → " + ex.GetType().Name + ": " + ex.Message);
                    }
                }

                jsonlError = string.Join(" ｜ ", tried.ToArray());
                jsonlPath = "<unavailable>";
                fileBroken = true;
                Log.Error(Prefix + " JSONL 落点全部不可写，已放弃结构化落盘（只走 Player.log）。"
                          + " 试过的位置：" + (jsonlError.Length == 0 ? "(一个候选都没构造出来)" : jsonlError));
                return jsonlPath;
            }
        }

        /// <summary>JSONL 不可写时，把"试过哪些位置、各自为什么失败"原样交出来。</summary>
        public static string JsonlError
        {
            get { return jsonlError; }
        }

        private static string[] BuildCandidates()
        {
            string saveData = null;
            try
            {
                saveData = GenFilePaths.SaveDataFolderPath;
            }
            catch (Exception)
            {
                saveData = null;
            }

            string persistent = null;
            try
            {
                persistent = Application.persistentDataPath;
            }
            catch (Exception)
            {
                persistent = null;
            }

            return new[]
            {
                string.IsNullOrEmpty(saveData) ? null : Path.Combine(saveData, "BikeProbe"),
                string.IsNullOrEmpty(persistent) ? null : Path.Combine(persistent, "BikeProbe"),
            };
        }

        public static void Line(string probe, string message)
        {
            string line = Prefix + " " + probe + " " + message;
            Log.Message(line);
            Record(probe, message);
        }

        public static void Warn(string probe, string message)
        {
            string line = Prefix + " " + probe + " WARN " + message;
            Log.Warning(line);
            Record(probe, "WARN " + message);
        }

        public static void Fail(string probe, string message)
        {
            string line = Prefix + " " + probe + " FAIL " + message;
            Log.Error(line);
            Record(probe, "FAIL " + message);
        }

        /// <summary>探针的原始数据（进程内快照；给“同一句话里塞太多数字”的场合用）。</summary>
        public static string[] Snapshot()
        {
            lock (Gate)
            {
                return Buffer.ToArray();
            }
        }

        private static void Record(string probe, string message)
        {
            lock (Gate)
            {
                if (Buffer.Count < 4000)
                {
                    Buffer.Add(probe + "\t" + message);
                }
            }

            string path = JsonlPath;
            if (fileBroken || path == null || path.StartsWith("<", StringComparison.Ordinal))
            {
                return;
            }

            int tick = -1;
            try
            {
                tick = Verse.GenTicks.TicksGame;
            }
            catch (Exception)
            {
                tick = -1;
            }

            string json = "{\"tick\":" + tick + ",\"probe\":" + Json(probe) + ",\"msg\":" + Json(message) + "}";
            try
            {
                File.AppendAllText(path, json + Environment.NewLine, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                if (!fileBroken)
                {
                    fileBroken = true;
                    Log.Warning(Prefix + " ProbeLog 无法写 JSONL（" + path + "）：" + ex.GetType().Name
                                + " —— 后续只走 Player.log");
                }
            }
        }

        private static string Json(string raw)
        {
            if (raw == null)
            {
                return "null";
            }
            StringBuilder sb = new StringBuilder(raw.Length + 8);
            sb.Append('"');
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
