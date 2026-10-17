// DSH_GameLink · 事件通道的唯一写入口。
//
// 形状：NDJSON —— 一行一条、UTF-8（无 BOM）、追加写、每条 Flush。
//   {"ts":1760000000.123,"seq":41,"kind":"heartbeat","tick":1200,...}
//
// 为什么每行都 Flush：DSH 侧是「跟读文件增量」的（src/live.js），不 Flush 就看不到最新一条。
// 为什么全部吞异常：这是旁路。写盘失败只 Log.Error 一次然后永久停写；
//   绝不能把异常冒泡到 Unity 的 Update/Tick 里（那会把整局游戏搞崩）。
// 为什么超过 8 MB 轮转：跟读端只需要「最近的事件」，8 MB 足够；轮转只做一代（<file>.1），简单。
// 帧率：这里的每条路径都是常数级工作（没有目录遍历、没有全图扫描）。

using System;
using System.Globalization;
using System.IO;
using System.Text;
using Verse;

namespace DSH_GameLink
{
    internal static class LiveChannel
    {
        /// <summary>本 mod 自己写进 Player.log 的行前缀。LogTap 会跳过它，避免「转发自己的报错」自激。</summary>
        internal const string OwnPrefix = "[GameLink]";

        private const long RotateBytes = 8L * 1024 * 1024;
        private const string DirEnv = "DSH_LIVE_DIR";
        private const string GameIdEnv = "GABS_GAME_ID";
        private const string GameIdPrefix = "dsh-rimworld-";
        private const string FallbackProfile = "game";
        private const string FallbackSubDir = "dsh-live";
        private const string FallbackFileName = "game.ndjson";

        private static readonly object Gate = new object();
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        private static string filePath;
        private static bool pathErrorLogged;
        private static FileStream stream;
        private static StreamWriter writer;
        private static long bytesWritten;
        private static bool broken;
        private static int seq;

        /// <summary>
        /// 写一条事件。`fields` 是已拼好的 `,"k":v,"k2":v2`（见 EventLine）；
        /// ts 与 seq 必须在这里（同一把锁里）分配，否则并发写会乱序。
        /// </summary>
        internal static void Write(string kind, StringBuilder fields)
        {
            if (broken)
            {
                return;
            }

            lock (Gate)
            {
                if (broken)
                {
                    return;
                }

                try
                {
                    if (!EnsureOpen())
                    {
                        return; // 路径/目录还没就绪（例如游戏刚起来）：本条丢弃，下次再试
                    }

                    string line = BuildLine(kind, fields);
                    writer.Write(line);
                    writer.Write('\n');
                    writer.Flush();
                    bytesWritten += Utf8NoBom.GetByteCount(line) + 1;
                    if (bytesWritten >= RotateBytes)
                    {
                        Rotate();
                    }
                }
                catch (Exception ex)
                {
                    // 只报一次，之后永久停写：反复失败的日志洪水比丢事件更糟。
                    broken = true;
                    CloseQuietly();
                    Err("写事件失败，已停止写事件（游戏不受影响）：" + DescribePath() + " → " + Describe(ex));
                }
            }
        }

        /// <summary>自身捕获到的异常 → 一条 error 事件。它自己绝不抛。</summary>
        internal static void ReportError(string where, Exception ex)
        {
            try
            {
                new EventLine("error").Text("where", where).Text("message", Describe(ex)).Emit();
            }
            catch (Exception)
            {
                // 连 error 事件都写不出去时，没有更低一层可以报了。
            }
        }

        /// <summary>本 mod 自己的红字：统一前缀，LogTap 会跳过它。</summary>
        internal static void Err(string message)
        {
            try
            {
                Log.Error(OwnPrefix + " " + message);
            }
            catch (Exception)
            {
                // 日志系统自己坏了也不关这条通道的事。
            }
        }

        internal static string Describe(Exception ex)
        {
            if (ex == null)
            {
                return "null";
            }

            return ex.GetType().Name + ": " + ex.Message;
        }

        internal static string DescribePath()
        {
            return string.IsNullOrEmpty(filePath) ? "(未解析)" : filePath;
        }

        private static string BuildLine(string kind, StringBuilder fields)
        {
            seq++;
            StringBuilder sb = new StringBuilder(192);
            sb.Append("{\"ts\":").Append(NowUnix().ToString("0.000", CultureInfo.InvariantCulture));
            sb.Append(",\"seq\":").Append(seq.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"kind\":\"").Append(kind).Append('"');
            if (fields != null && fields.Length > 0)
            {
                // EventLine 的每个字段自带前导逗号（见 EventLine.Num/Text/Flag），这里不能再补一个。
                sb.Append(fields);
            }

            sb.Append('}');
            return sb.ToString();
        }

        private static double NowUnix()
        {
            return (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        /// <summary>打开（必要时建目录）。返回 false = 这一轮还拿不到路径，调用方丢弃本条。</summary>
        private static bool EnsureOpen()
        {
            if (writer != null)
            {
                return true;
            }

            if (filePath == null)
            {
                filePath = ComputePath();
                if (filePath == null)
                {
                    return false;
                }
            }

            string dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            stream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            writer = new StreamWriter(stream, Utf8NoBom);
            writer.AutoFlush = false; // 每条由 Write() 显式 Flush
            bytesWritten = stream.Length;
            return true;
        }

        private static string ComputePath()
        {
            try
            {
                string dir = Environment.GetEnvironmentVariable(DirEnv);
                string fileName = ProfileName() + ".ndjson";
                if (!string.IsNullOrEmpty(dir))
                {
                    return Path.Combine(dir, fileName);
                }

                return Path.Combine(GenFilePaths.SaveDataFolderPath, FallbackSubDir, FallbackFileName);
            }
            catch (Exception ex)
            {
                // 例如 GenFilePaths 还没初始化。只报一次，退到下一个事件再试（不置 broken）。
                if (!pathErrorLogged)
                {
                    pathErrorLogged = true;
                    Err("解析事件文件路径失败：" + Describe(ex));
                }

                return null;
            }
        }

        /// <summary>
        /// profile 名：GABS_GAME_ID（launcher 里是 `dsh-rimworld-&lt;profile&gt;`）去掉前缀。
        /// 拿不到就用 `game` —— 这时文件名固定，跟读端也知道该看哪个。
        /// </summary>
        private static string ProfileName()
        {
            string id = Environment.GetEnvironmentVariable(GameIdEnv);
            if (!string.IsNullOrEmpty(id) && id.StartsWith(GameIdPrefix, StringComparison.OrdinalIgnoreCase))
            {
                id = id.Substring(GameIdPrefix.Length);
            }

            if (string.IsNullOrEmpty(id))
            {
                return FallbackProfile;
            }

            char[] invalid = Path.GetInvalidFileNameChars();
            StringBuilder sb = new StringBuilder(id.Length);
            for (int i = 0; i < id.Length; i++)
            {
                char c = id[i];
                bool bad = false;
                for (int j = 0; j < invalid.Length; j++)
                {
                    if (invalid[j] == c)
                    {
                        bad = true;
                        break;
                    }
                }

                sb.Append(bad ? '_' : c);
            }

            return sb.ToString();
        }

        /// <summary>轮转一代：当前文件改名成 &lt;file&gt;.1（覆盖旧的那一代），然后继续往新文件追加。</summary>
        private static void Rotate()
        {
            CloseQuietly();
            string rotated = filePath + ".1";
            try
            {
                if (File.Exists(rotated))
                {
                    File.Delete(rotated);
                }

                File.Move(filePath, rotated);
                EnsureOpen(); // 新文件，bytesWritten 归零
            }
            catch (Exception ex)
            {
                Err("轮转事件文件失败（继续追加原文件）：" + DescribePath() + " → " + Describe(ex));
                if (EnsureOpen())
                {
                    // 计数清零：轮转失败不该变成「每次写都试一遍轮转」。
                    bytesWritten = 0;
                }
            }
        }

        private static void CloseQuietly()
        {
            try
            {
                if (writer != null)
                {
                    writer.Flush();
                }
            }
            catch (Exception)
            {
                // 关的时候再抛就没意义了。
            }

            try
            {
                if (writer != null)
                {
                    writer.Dispose();
                }
            }
            catch (Exception)
            {
            }

            writer = null;
            stream = null;
        }
    }
}
