using System;
using System.Collections.Generic;
using System.Text;
using System.Globalization;
using System.IO;
using RimWorld;
using Verse;

namespace ColonySnapshots
{
    /// <summary>
    /// **殖民地状态录制器** —— 静默地按**游戏 tick** 间隔把殖民地状态写成快照。
    ///
    /// ## 立场：和自动化**完全隔离**（用户 2026-10-03 明确要求）
    ///
    /// 用户原话：
    /// > 「这个和我们现在的自动化**没有任何关系**，它是我的另外一个想法。
    /// >  它和自动化……能够帮助开发自动化。但我不希望在这个过程中
    /// >  **再把自动化的东西放进去**，你就**纯净地去读**、读快照就可以了。
    /// >  我就是玩这个游戏，然后你就在后台**检测到存档开起来之后就静默地读**就可以了。」
    ///
    /// ## 为什么按**游戏 tick** 而不是真实时间（用户明确要求）
    ///
    /// 用户：
    /// > 「读快照的时间间隔最好是**按照游戏 tick 来算**，不要按照真实的时间来算。」
    ///
    /// 技术上这也是对的：
    /// - 按真实时间 → 采样点**随帧率漂移**（游戏快了慢了两张快照对应的游戏时长不同）
    /// - 按真实时间 → **游戏暂停时还会一直拍**，拍出一堆完全相同的快照
    /// - 按 tick → 每张快照**固定间隔 6000 tick（0.1 游戏天）**，时序分析才对得齐
    ///
    /// ## 隔离保证
    ///
    /// 本组件**只读**，且：
    /// - 不调用任何 domain / coordinator
    /// - 不受任何自动化开关影响（本 mod 里根本没有自动化的东西）
    /// - 自己独立的开关 `ColonyRecorder.Enabled`
    /// - 写盘失败只记警告，**绝不抛给游戏**
    /// </summary>
    public sealed class ColonyRecorder : GameComponent
    {
        /// <summary>录制总开关（默认**开** —— 用户要"检测到存档就静默地读"）</summary>
        public static bool Enabled = true;

        /// <summary>采样间隔（游戏 tick）。6000 tick = 0.1 游戏天 = 6 游戏小时</summary>
        public static int IntervalTicks = 6000;

        /// <summary>上次采样的 tick</summary>
        private int _lastTick = int.MinValue;

        // ⚠ 这里**没有**自己的序号 —— 序号由 `SnapshotBuilder` 统一维护（`snap.Seq`）。
        //   第一版留了一个 `_seq` 字段但没同步，于是 `snap.Seq` 恒为 0，
        //   而 `snap.Seq % 10 == 0` 对 0 永远成立 → "每 10 张报一次"变成"每张都报"。
        //   教训：**默认值刚好让条件恒真**的 bug 最难看出来。

        /// <summary>本会话的输出文件</summary>
        private string _file;

        /// <summary>
        /// 本进程内写盘是否**失败过**（磁盘满 / 文件被占）。
        ///
        /// ★ 失败一次就不再往同一个文件里试了（只记一次警告）—— 本项目纪律：
        ///   失败必须**带原因**地留痕，但绝不每 6000 tick 刷爆 `Player.log`。
        /// </summary>
        private bool _writeFailed;

        /// <summary>已写入的行数（诊断用）</summary>
        public static int WrittenLines;

        public ColonyRecorder(Verse.Game game)
        {
        }

        /// <summary>
        /// **老根目录**（存档目录下的 `foodcontrol-snapshots/`）——
        /// ⚠ 现在**只用于日志/诊断**：新数据写在它的子目录 `runs/run-<runId>/` 里
        ///   （见 <see cref="RunScope"/>）。老文件留在根目录，**一个都不动、也不续写**。
        ///
        /// ⚠ 根目录名**故意保持不变**（不改成 colony-snapshots）：分析端脚本
        ///   `scripts/analyze-snapshots.mjs` 的 `SNAPSHOT_DIR` 与既有的
        ///   `profiles/vanilla/foodcontrol-snapshots/` 都依赖这个名字，
        ///   改名会让已有数据与工具链对不上。它只是个目录名，不影响本 mod 的独立性。
        /// </summary>
        public static string OutDir
        {
            get { return RunScope.OutDirRoot; }
        }

        /// <summary>本次启动的 run 目录（三流共用；拿不到就 null）</summary>
        public static string RunDir
        {
            get { return RunScope.RunDir; }
        }

        public override void GameComponentTick()
        {
            if (!Enabled) return;
            if (IntervalTicks < 600) IntervalTicks = 600;      // 下限，防手滑设成 1

            try
            {
                int tick = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
                if (tick <= 0) return;

                // ★ **tick 倒退 = 换局 / 读档**：采样状态、序号、输出文件、会话号**全部重开**。
                //
                //   ⚠ 老实现**漏了这条**（操作流有、快照流没有）—— 后果是：
                //     读档后 `_file` 仍指向上一次会话那个文件路径，
                //   而那个路径现在是**同一个 run 目录**里的老文件 ⇒
                //   同一进程内"读档续写旧文件"（文件名只由 殖民地名+mapId 决定，正好还是同一个），
                //   于是 `seq` 从 1 重开、`sessionId` 却没变 —— 分析端按会话切段会切错。
                //   ⇒ 这里必须清 `_file`（下一张快照重新按新会话去拼路径）并开新会话号。
                if (_lastTick != int.MinValue && tick < _lastTick)
                {
                    try
                    {
                        Log.Message("[ColonyRecorder][录制] tick 倒退（" + _lastTick + " → " + tick
                            + "），按新的一局重开会话（文件：" + (_file ?? "(还没建)") + "）");
                    }
                    catch (Exception) { }
                    _file = null;
                    _lastTick = int.MinValue;
                    _writeFailed = false;
                    // ★ 会话号由三流**共用一本账**（见 RunScope.EnsureSessionForPoll）
                    RunScope.EnsureSessionForPoll(tick);
                }

                // 首次：立刻拍一张（这样"存档刚开"就有基线）
                if (_lastTick == int.MinValue)
                {
                    // 进程内第一次会话也要**占一个会话号**（守卫保证同一 tick 只开一次）
                    RunScope.EnsureSessionForPoll(tick);
                    _lastTick = tick;
                    Capture(tick);
                    return;
                }

                if (tick - _lastTick < IntervalTicks) return;
                _lastTick = tick;
                Capture(tick);
            }
            catch (Exception e)
            {
                // ★ 录制器**绝不能影响游戏** —— 吞掉一切
                try { Log.Warning("[ColonyRecorder][录制] tick 异常（已吞）：" + e.Message); } catch (Exception) { }
            }
        }

        private void Capture(int tick)
        {
            try
            {
                Map map = Verse.Find.CurrentMap;
                if (map == null) return;

                ColonySnapshot snap = SnapshotBuilder.Build(map, tick, IntervalTicks);
                snap.WallClock = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

                // ★ **三流行尾元数据一致**：快照行也要带 runId / sessionId / sessionSeq
                //   （由 `ColonySnapshot.ToJsonLine()` 追加在整行最后 —— 老字段顺序/类型一个不动）。
                //   ⚠ 成组取（内部一次锁）：防止取到"sessionSeq=2 配 sessionId=<s0001>"的自相矛盾组合。
                SessionMeta meta = SessionMeta.Capture();
                snap.RunId = meta.RunId;
                snap.SessionId = meta.SessionId;
                snap.SessionSeq = meta.SessionSeq;

                EnsureFile(map, snap);
                if (string.IsNullOrEmpty(_file)) return;      // 建不出文件（已警告）→ 这一张不写
                if (_writeFailed) return;                     // 本进程写盘已失败过 → 不再重试刷日志

                // JSONL：一行一张快照，追加写
                try
                {
                    File.AppendAllText(_file, snap.ToJsonLine() + "\n");
                    WrittenLines++;
                }
                catch (Exception e)
                {
                    // ★ 写失败只警告一次，绝不影响游戏（磁盘满 / 文件被占 / 权限）
                    _writeFailed = true;
                    try
                    {
                        Log.Warning("[ColonyRecorder][录制] 写文件失败（已吞，本进程后续不再重试）：" + e.Message
                            + "；文件=" + _file);
                    }
                    catch (Exception) { }
                    return;
                }

                // 每 10 张露一次脸 + **健康摘要**（回答用户说的"是否读取了足量"）
                if (snap.Seq == 1 || snap.Seq % 10 == 0)
                {
                    try { Log.Message("[ColonyRecorder][录制] " + HealthSummary(snap)); } catch (Exception) { }
                }
            }
            catch (Exception e)
            {
                try { Log.Warning("[ColonyRecorder][录制] 采集失败（已吞）：" + e.Message); } catch (Exception) { }
            }
        }

        /// <summary>
        /// **快照健康摘要** —— 回答用户 2026-10-03 说的
        /// 「我**无法知道我们现在是否读取了足量的快照**」。
        ///
        /// 三件事：
        /// ① 有几个读取块、有没有失败的
        /// ② 采样间隔对不对（有没有漏采）
        /// ③ ★ **可疑**：某个块全程 `items=0` —— 是"这一局真的没有"，还是"读法失效了"？
        ///    这是最难自己发现的情况（`error` 是空的，但什么也没读到）。
        /// </summary>
        public static string HealthSummary(ColonySnapshot snap)
        {
            int total = snap.Health.Count;
            int failed = 0;
            var suspects = new List<string>();

            for (int i = 0; i < snap.Health.Count; i++)
            {
                ReaderHealth h = snap.Health[i];
                if (h == null) continue;
                if (!string.IsNullOrEmpty(h.Error)) failed++;
                // ★ 全程 0 项且没报错 —— 可疑（可能是读法失效，也可能真的没有）
                if (string.IsNullOrEmpty(h.Error) && h.ItemsRead == 0)
                    suspects.Add(h.ReaderId);
            }

            var sb = new System.Text.StringBuilder();
            sb.Append("#").Append(snap.Seq)
              .Append(" tick=").Append(snap.Tick)
              .Append(" 读取块 ").Append(total - failed).Append("/").Append(total);

            if (failed > 0)
            {
                sb.Append(" **失败").Append(failed).Append("**：");
                for (int i = 0; i < snap.Health.Count; i++)
                {
                    ReaderHealth h = snap.Health[i];
                    if (h != null && !string.IsNullOrEmpty(h.Error))
                        sb.Append(h.ReaderId).Append("(").Append(h.Error).Append(") ");
                }
            }

            // 采样密度
            SamplingInfo s = snap.Sampling;
            sb.Append(" 间隔 ").Append(s.SincePrevTick).Append("/").Append(s.IntervalTicks).Append(" tick");
            if (s.LooksDropped) sb.Append(" **漏采**");

            if (suspects.Count > 0)
                sb.Append(" 可疑(0项)[").Append(string.Join(",", suspects.ToArray())).Append("]");

            return sb.ToString();
        }

        /// <summary>
        /// ★ **一个 run 目录一个文件，run 内跨会话追加**（用户 2026-10-04 口径变更）。
        ///
        /// ## 为什么又改了（用户实测遇到的问题）
        ///
        /// 上一版是"**一个殖民地一个文件、跨会话甚至跨启动追加**"：`colony-<名>-<mapId>.jsonl`。
        /// 用户实测结果（真数据）：同一个 `actions-` 文件里有 **3 条 `session` 全是 `tick=1`**
        /// （三次开局混进了一个文件）—— `mapId` 新殖民地恒为 0，重启后 tick 从 1 重来，
        /// 采集端**根本不认为换局**。⇒ "哪一局"不能靠分析端的纪律，得靠**目录结构**。
        ///
        /// ## 现在的规则（三层：run × session × map）
        ///
        /// ```text
        /// foodcontrol-snapshots/runs/run-<runId>/colony-<殖民地名>-<mapId>.jsonl
        ///                              └─ 一次进程启动一个目录（三流共享）
        /// ```
        ///
        /// - **runId**：进程内恒定（`RunScope`，目录名里）
        /// - **session**：进程内换局/读档（tick 倒退）⇒ 行字段 `sessionId` / `sessionSeq`（**不进文件名**，
        ///   用户要求"文件名保持简洁"），一个文件里可以有多个 session
        /// - **mapId**：区分同一 run 内的不同地图（文件名里）
        ///
        /// ⚠ 目录建不出来 / 存档目录还没就绪 ⇒ `RunScope.PathFor` 返回 `null` ⇒
        ///   **这一张不写**（内存里的采样序号照常推进），警告由 `RunScope` 统一报，
        ///   下次 tick 自动重试。**绝不抛异常。**
        ///
        /// ⚠ 老根目录 `foodcontrol-snapshots/*.jsonl` 的老文件**一个都不动**（那是用户的数据），
        ///   新数据也不再往根目录写。
        /// </summary>
        private void EnsureFile(Map map, ColonySnapshot snap)
        {
            if (!string.IsNullOrEmpty(_file)) return;

            int mapId = RunScope.GetMapId(map);
            string colony = snap != null ? snap.ColonyName : "";

            // ★ 唯一的路径出口（三流共用同一套规则；run 目录由 RunScope 保证是同一个）
            string file = RunScope.PathFor(RunNaming.ColonyPrefix, colony, mapId);
            if (string.IsNullOrEmpty(file)) return;       // 降级：这一张不写（原因已由 RunScope 记过警告）

            _file = file;

            bool existed = false;
            try { existed = File.Exists(_file); } catch (Exception) { }

            try
            {
                Log.Message("[ColonyRecorder][录制] 本次会话**追加**到 " + _file
                    + (existed ? "（文件已存在，接着往里写）" : "（新文件）")
                    + "；runId=" + (RunScope.RunId ?? "(未就绪)")
                    + " sessionSeq=" + RunScope.SessionSeq
                    + "；殖民地名=" + (string.IsNullOrEmpty(colony) ? "（拿不到，用 unknown）" : colony)
                    + " mapId=" + mapId);
            }
            catch (Exception) { }
        }

        /// <summary>
        /// 把殖民地名变成**文件系统安全**的名字。
        ///
        /// ⚠ 规则已**搬到** <see cref="RunNaming.SafeSegment"/>（三条流共用一份，见那边的注记：
        ///   "文件名规则三份实现"正是这一轮要消掉的重复）。这里**只保留转发**，
        ///   因为另外两条流（`PlayerActionObserver` / `EventTimelineObserver`）
        ///   与既有调用点都还在用这个名字 —— 行为逐字不变。
        /// </summary>
        internal static string FileSafe(string name)
        {
            return RunNaming.SafeSegment(name);
        }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref Enabled, "colonyRecorderEnabled", true);
            Scribe_Values.Look(ref IntervalTicks, "colonyRecorderInterval", 6000);
        }
    }
}
