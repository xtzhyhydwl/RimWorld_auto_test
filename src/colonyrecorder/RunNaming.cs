using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace ColonySnapshots
{
    /// <summary>
    /// ★★ **命名与路径规则**（纯逻辑、零游戏依赖、零 IO）—— 三条数据流共用的**唯一**一份。
    ///
    /// ## 为什么把这个单独拆出来（用户 2026-10-04 要求）
    ///
    /// 老实现里"输出目录 + 文件名"这件事散在**三处**（`ColonyRecorder` / `PlayerActionObserver` /
    /// `EventTimelineObserver`），每处各写一遍 `Path.Combine(dir, "前缀-" + safe + "-" + mapId + ".jsonl")` ——
    /// 于是"文件名规则"事实上有三份实现，加一层目录（这一轮加的 `runs/run-<runId>/`）就得改三处，
    /// 而且改漏一处就会出现"快照进新目录、事件还在老目录"的分裂。
    ///
    /// ⇒ 规则收口到本文件：**文件系统安全化**（`SafeSegment`，从 `ColonyRecorder.FileSafe` 搬来）、
    ///   **runId 生成**（`RunIdFrom`，参数化以便自测）、**文件名**（`FileName`）、
    ///   **run 目录名**（`RunDirectoryName`）、**完整路径**（`PathFor`）。
    ///
    /// ## 三层容器（用户 2026-10-04 定的口径）
    ///
    /// ```text
    /// foodcontrol-snapshots/                 ← 老根目录（老文件一个都不动，也不往里写新数据）
    ///   runs/                                ← 新的一层：run 的容器
    ///     run-20261004-113045-12345-4f2a/    ← ★ 一个**进程**（= 一次启动）= 一个 run 目录
    ///       colony-<殖民地名>-<mapId>.jsonl  ← 三条流写在**同一个 run 目录**里
    ///       actions-<殖民地名>-<mapId>.jsonl
    ///       events-<殖民地名>-<mapId>.jsonl
    /// ```
    ///
    /// ⇒ **三层**：`run`（进程）× `session`（进程内换局/读档，**不是目录**，是行字段）×
    ///            `map`（文件名里的 `<mapId>`）。
    ///
    /// ## runId 的规则（要同时满足三条）
    ///
    /// | 要求 | 怎么满足 |
    /// | --- | --- |
    /// | **进程内恒定** | 只在 `RunScope` 初始化时生成一次（见 `RunScope`） |
    /// | **可排序** | 前缀是 `yyyyMMdd-HHmmss`，字典序 = 时间序 |
    /// | **不撞** | 后面接 `AppDomain.Id`（区分同一秒启动的两个进程）+ 4 位十六进制随机 |
    ///
    /// 例：`20261004-113045-123-4f2a` ⇒ 目录 `runs/run-20261004-113045-123-4f2a/`。
    ///
    /// ⚠ 纯函数（无 IO、无静态状态），所以可以**单独编出来跑自测**：
    ///   `tmp/runscope-selftest/RunScopeSelfTest.cs`（用游戏自带的 Mono 跑，见 STATUS.md）。
    /// </summary>
    public static class RunNaming
    {
        // ══════════════════════════════════════════════════════════════
        // 常量（"一个常量只有一处定义" —— 三流共用）
        // ══════════════════════════════════════════════════════════════

        /// <summary>老根目录名（**故意不变**：分析端脚本与既有数据都用这个名字；老文件不迁不写）</summary>
        public const string RootDirName = "foodcontrol-snapshots";

        /// <summary>run 的容器目录名（`runs/`）</summary>
        public const string RunsDirName = "runs";

        /// <summary>每个 run 目录的前缀</summary>
        public const string RunDirPrefix = "run-";

        /// <summary>快照流文件名前缀</summary>
        public const string ColonyPrefix = "colony";

        /// <summary>操作流文件名前缀</summary>
        public const string ActionsPrefix = "actions";

        /// <summary>事件流文件名前缀</summary>
        public const string EventsPrefix = "events";

        /// <summary>三条流的文件后缀（JSON Lines）</summary>
        public const string FileExtension = ".jsonl";

        /// <summary>殖民地名取不到时的占位（老实现就是这个字面量，保持不变）</summary>
        public const string UnknownName = "unknown";

        // ══════════════════════════════════════════════════════════════
        // runId / run 目录
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// runId 的**可测试**形式：把"时间 + 进程区分 + 随机"拼起来。
        ///
        /// ⚠ 参数化（而不是内部直接调 `DateTime.Now`/`new Random()`）是刻意的：
        ///   纯函数才可以在离线自测里把边界（同一秒、同一 AppDomain）造出来验证"不撞"。
        /// </summary>
        /// <param name="when">进程启动时刻（本地时间）</param>
        /// <param name="processToken">进程区分标记（用 `AppDomain.CurrentDomain.Id`；见 `RunScope`）</param>
        /// <param name="randomHex4">4 位十六进制随机（小写；调用方保证格式）</param>
        public static string RunIdFrom(DateTime when, int processToken, string randomHex4)
        {
            // ⚠ 一律 `InvariantCulture`：用户机器是中文区域，默认区域会给出别的日期分隔符/数字形
            string stamp = when.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string rand = string.IsNullOrEmpty(randomHex4) ? "0000" : randomHex4;
            return stamp + "-" + processToken.ToString(CultureInfo.InvariantCulture) + "-" + rand;
        }

        /// <summary>run 目录名 = `run-<runId>`</summary>
        public static string RunDirectoryName(string runId)
        {
            return RunDirPrefix + (runId ?? "");
        }

        /// <summary>`foodcontrol-snapshots/runs`</summary>
        public static string RunsDir(string savesRoot)
        {
            if (string.IsNullOrEmpty(savesRoot)) return null;
            return Path.Combine(savesRoot, RootDirName, RunsDirName);
        }

        /// <summary>`foodcontrol-snapshots/runs/run-<runId>` —— **一次进程启动的落点**</summary>
        public static string RunDir(string savesRoot, string runId)
        {
            if (string.IsNullOrEmpty(savesRoot)) return null;
            if (string.IsNullOrEmpty(runId)) return null;
            return Path.Combine(savesRoot, RootDirName, RunsDirName, RunDirectoryName(runId));
        }

        // ══════════════════════════════════════════════════════════════
        // 文件名 / 完整路径
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 文件名 = `<prefix>-<殖民地名safe>-<mapId>.jsonl`。
        ///
        /// ⚠ **不含 runId / sessionSeq**（用户 2026-10-04 明确要"文件名保持简洁"）：
        ///   - `runId` 在**目录名**里（`runs/run-<runId>/`）；
        ///   - `sessionSeq` 在**行字段**里（`sessionId` / `sessionSeq`，见 `SessionMeta`）。
        ///
        /// ⇒ 一个文件里可以有很多 session（同一进程内读档 = tick 倒退 = 新 session），
        ///   分析端按 `sessionId` / `sessionSeq` **切段**，不要按文件名切。
        /// </summary>
        public static string FileName(string prefix, string colonyNameSafe, int mapId)
        {
            string safe = string.IsNullOrEmpty(colonyNameSafe) ? UnknownName : colonyNameSafe;
            return prefix + "-" + safe + "-" + mapId.ToString(CultureInfo.InvariantCulture) + FileExtension;
        }

        /// <summary>
        /// ★ **三条流唯一的路径出口**：`run 目录 + 文件名`。
        ///
        /// ⚠ 只拼字符串，**不建目录、不碰磁盘**（建目录是 `RunScope` 的事；
        ///   这样本文件能被单独编出来自测）。
        /// </summary>
        public static string PathFor(string runDir, string prefix, string colonyNameSafe, int mapId)
        {
            if (string.IsNullOrEmpty(runDir)) return null;
            if (string.IsNullOrEmpty(prefix)) return null;
            return Path.Combine(runDir, FileName(prefix, colonyNameSafe, mapId));
        }

        // ══════════════════════════════════════════════════════════════
        // 文件系统安全化（**从 `ColonyRecorder.FileSafe` 原样搬来**，逐条规则不变）
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 把一段文本（殖民地名）变成**文件系统安全**的片段。
        ///
        /// 规则（与老 `ColonyRecorder.FileSafe` **逐条一致**，不能改 —— 改了就与老数据/分析端对不上）：
        ///   非法字符（`Path.GetInvalidFileNameChars()`）→ `_`；
        ///   只留字母/数字/中文/`-`/`_`；连续 `_` 压成一个；
        ///   截断 40 字符；结果为空 → `unknown`。
        /// </summary>
        public static string SafeSegment(string name)
        {
            if (string.IsNullOrEmpty(name)) return UnknownName;

            var sb = new StringBuilder(name.Length);
            char[] bad = Path.GetInvalidFileNameChars();
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                bool ok = true;
                for (int j = 0; j < bad.Length; j++) if (bad[j] == c) { ok = false; break; }

                if (ok)
                {
                    // 只留"看得懂"的字符：ASCII 字母数字 + 中文 + - _
                    bool keep = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                             || (c >= '0' && c <= '9') || c == '-' || c == '_'
                             || c > 0x7F;                      // 中文等非 ASCII 一律保留（NTFS 支持）
                    if (!keep) ok = false;
                }

                if (ok)
                {
                    // 连续 `_` 压成一个（`_` 本身是合法字符，但重复没意义）
                    if (c == '_' && sb.Length > 0 && sb[sb.Length - 1] == '_') continue;
                    sb.Append(c);
                }
                else
                {
                    if (sb.Length > 0 && sb[sb.Length - 1] != '_') sb.Append('_');
                }
            }

            string s = sb.ToString().Trim('_', '.', ' ');
            if (s.Length > 40) s = s.Substring(0, 40).Trim('_', '.', ' ');
            return string.IsNullOrEmpty(s) ? UnknownName : s;
        }
    }
}
