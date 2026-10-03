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

        /// <summary>已写入的行数（诊断用）</summary>
        public static int WrittenLines;

        public ColonyRecorder(Verse.Game game)
        {
        }

        /// <summary>
        /// 输出目录：存档目录下的 `foodcontrol-snapshots/`。
        ///
        /// ⚠ 目录名**故意保持不变**（不改成 colony-snapshots）：分析端脚本
        ///   `scripts/analyze-snapshots.mjs` 的 `SNAPSHOT_DIR` 与既有的
        ///   `profiles/vanilla/foodcontrol-snapshots/` 都依赖这个名字，
        ///   改名会让已有数据与工具链对不上。它只是个目录名，不影响本 mod 的独立性。
        /// </summary>
        public static string OutDir
        {
            get
            {
                try { return Path.Combine(GenFilePaths.SaveDataFolderPath, "foodcontrol-snapshots"); }
                catch (Exception) { return null; }
            }
        }

        public override void GameComponentTick()
        {
            if (!Enabled) return;
            if (IntervalTicks < 600) IntervalTicks = 600;      // 下限，防手滑设成 1

            try
            {
                int tick = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
                if (tick <= 0) return;

                // 首次：立刻拍一张（这样"存档刚开"就有基线）
                if (_lastTick == int.MinValue)
                {
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

                EnsureFile();
                if (string.IsNullOrEmpty(_file)) return;

                // JSONL：一行一张快照，追加写
                File.AppendAllText(_file, snap.ToJsonLine() + "\n");
                WrittenLines++;

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

        private void EnsureFile()
        {
            if (!string.IsNullOrEmpty(_file)) return;
            string dir = OutDir;
            if (string.IsNullOrEmpty(dir)) return;

            Directory.CreateDirectory(dir);
            // 文件名带启动时间，一次游戏会话一个文件（重新开档不会混在一起）
            string name = "colony-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".jsonl";
            _file = Path.Combine(dir, name);
            try { Log.Message("[ColonyRecorder][录制] 本次会话写入 " + _file); } catch (Exception) { }
        }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref Enabled, "colonyRecorderEnabled", true);
            Scribe_Values.Look(ref IntervalTicks, "colonyRecorderInterval", 6000);
        }
    }
}
