using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using RimWorld;
using Verse;

namespace ColonySnapshots
{
    /// <summary>
    /// ★ **游戏事件时间线**（`kind:"letter"` / `op:"arrive"`）—— 本 mod 的**第三套系统**。
    ///
    /// ## 与另外两套的关系（三条并列的线，互不影响）
    ///
    /// | 系统 | 驱动方式 | 产物 |
    /// | --- | --- | --- |
    /// | `ColonyRecorder`（`GameComponent`） | 时间驱动，每 6000 tick | `colony-<名>-<mapId>.jsonl` |
    /// | `PlayerActionObserver`（`MapComponent`） | 事件驱动，每 30 tick 状态差分 | `actions-<名>-<mapId>.jsonl` |
    /// | **本类**（由 `PlayerActionObserver` 每轮末尾驱动） | 事件驱动，**轮询时还未见过的信件**（含新到达的，也含重启后仍挂在列表里的旧信） | `events-<名>-<mapId>.jsonl` |
    ///
    /// ## 为什么读 `Find.LetterStack` 而不是 hook
    ///
    /// 用户 2026-10-04 要求：**最小游戏事件时间线 MVP** —— 先把"游戏自己产生的、有确切
    /// 游戏内时刻的事件"记下来（信件是其中最典型、最稳定的一种），
    /// 不去 hook 任何 UI、不引入 Harmony、不依赖 mod 加载顺序。
    ///
    /// `Verse.LetterStack.LettersListForReading` 是**公开只读**的当前信件列表
    /// （元数据实测：`P List`1 LettersListForReading {get:public set:none}`），
    /// 事件源是**游戏自己的** `Letter.arrivalTick`（不是我们轮询到的时刻）——
    /// 所以本文件里 `arrivalTick` 是**游戏自带时刻**，字段 `tickSource:"game"` 就是把这件事
    /// 明确写进数据里，避免分析端把它当成"我们的采样时刻"。
    ///
    /// ## 只读与静默（本项目硬约束）
    ///
    /// - **只读**：只读 `LettersListForReading` / 信件的字段，**绝不**调用
    ///   `ReceiveLetter` / `RemoveLetter`（那会改游戏状态）。
    /// - **不抛异常**：`ObserveLettersFrom` 整段包 try/catch，任何异常只 `Log.Warning`。
    /// - **失败带原因**：写盘失败、读取失败都留痕（本项目纪律），但**同一会话只报一次**，
    ///   绝不每 30 tick 刷爆 `Player.log`。
    /// - **C# 7.3**：不用 switch 表达式 / `??=` / 范围索引（游戏运行时是旧 Mono）。
    ///
    /// ## 元数据确证（`tmp/research-letter-members.ps1`，2026-10-04 本机实测）
    ///
    /// | 成员 | 可见性 |
    /// | --- | --- |
    /// | `Verse.Find.LetterStack` | public 属性 |
    /// | `Verse.LetterStack.LettersListForReading` | public 属性（`List<Letter>`；私有字段是 `letters`）|
    /// | `Verse.Letter.ID` / `.def` / `.lookTargets` / `.relatedFaction` / `.arrivalTick` / `.arrivalTime` / `.debugInfo` | **public 字段** |
    /// | `Verse.Letter.Label` | public 属性（`TaggedString`）|
    /// | `Verse.LetterDef.letterClass` | **public 字段**（`System.Type`）|
    /// | `RimWorld.ChoiceLetter_RansomDemand.faction` | **public 字段**（`relatedFaction` 的兜底来源）|
    ///
    /// ## ★ 跨地图去重：为什么状态是**静态**的
    ///
    /// `Find.LetterStack` 是**游戏级**的（不是地图级），而本观察者是**每张地图一个实例**
    /// （`MapComponent`）。若把"已见过的 `Letter.ID`"放在实例字段里：
    /// 一封信到达时，**每张已加载地图**都会各自判成"新信件" ⇒ A 图的 `events-A.jsonl`
    /// 与 B 图的 `events-B.jsonl` **各写一遍同一封信** ⇒ 跨文件重复。
    ///
    /// ⇒ 去重状态一律放**静态**容器（按"殖民地名+mapId"做 key，也就是**按输出文件**），
    ///   于是"一封信 = 一行"，且这一行只出现在**它被检测到时玩家所在的那张地图**的文件里
    ///   （`Verse.Find.LetterStack` 里只有一封实在的信，先轮询到的地图实例写它，
    ///   其余地图实例因为静态 `Seen` 命中而跳过 —— 不会重复、也不会丢）。
    ///
    /// ⚠ 去重集合**不持久化**（跟 `PlayerActionObserver._buildabilityWritten` 同一个取舍）：
    ///   重启游戏 / 读档后第一轮，`Seen` 是空的 ⇒ **当前还挂在 `LetterStack` 里的每一封信
    ///   都会被写成 `op:"arrive"`** —— 其中包含"上一局就在那儿"的旧信。
    ///   **不要**声称"基线里的信件不会产生 `arrive`"：基线行只是把那一刻的信件条数记下来
    ///   （`letters` / `seen`），它**不过滤**任何一行事件（见 `WriteBaselineLine`）。
    ///   这样**不会丢**（宁可重复一次），分析端按 `(会话, letterId)` 去重即可 ——
    ///   跨会话重复与"同一会话内真到达"必须分开：真到达的 `lagTicks ≤ 轮询间隔` 且
    ///   `arrivalAbsTick ≥` 本会话的 `absTick`，首轮重放的旧信两条都不满足。
    /// </summary>
    public static class EventTimelineObserver
    {
        // ══════════════════════════════════════════════════════════════
        // 开关
        // ══════════════════════════════════════════════════════════════

        /// <summary>事件时间线总开关（默认**开**）</summary>
        public static bool Enabled = true;

        // ══════════════════════════════════════════════════════════════
        // 输出文件（与另外两条流**同一个 run 目录**；路径规则收口在 `RunScope.PathFor`）
        // ══════════════════════════════════════════════════════════════

        /// <summary>run 目录（三流共用；诊断/日志用。⚠ 老字段 `OutDir` 已随 run 目录层作废）</summary>
        public static string RunDir
        {
            get { return RunScope.RunDir; }
        }

        /// <summary>
        /// 输出根目录（`…/foodcontrol-snapshots`）—— **只用于日志**。
        /// 新数据写在它的子目录 `runs/run-<runId>/` 里（见 `RunScope`）。
        /// </summary>
        public static string OutDirRoot
        {
            get { return RunScope.OutDirRoot; }
        }

        // ══════════════════════════════════════════════════════════════
        // 静态状态：按"输出文件"（= 殖民地名 + mapId）分组
        //
        // ⚠ 为什么要跟文件名而不是跟地图实例 —— 见类注释"跨地图去重"那一节。
        // ══════════════════════════════════════════════════════════════

        /// <summary>每个输出文件的状态（key = `殖民地名safe#mapId`）</summary>
        private static readonly Dictionary<string, FileState> _states = new Dictionary<string, FileState>();

        /// <summary>已报过一次的警告（去重，防每轮刷日志）</summary>
        private static readonly HashSet<string> _warnedOnce = new HashSet<string>();

        /// <summary>本会话已写的行数（诊断用）</summary>
        public static int WrittenLines;

        /// <summary>最近一轮检测到的**新信件**条数（诊断用）</summary>
        public static int LastArrived;

        /// <summary>已加载过的事件观察**会话**总数（诊断用）</summary>
        public static int SessionCount;

        private sealed class FileState
        {
            /// <summary>输出文件全路径</summary>
            public string File;
            /// <summary>地图 id（写进语义里，方便诊断）</summary>
            public int MapId;
            /// <summary>殖民地名（原始，未安全化）</summary>
            public string ColonyName = "";
            /// <summary>本文件的轮询序号（**本观察者自己的**，从 1 开始）—— 跨文件对齐用</summary>
            public int PollSeq;
            /// <summary>上一轮的游戏 tick（判 tick 倒退 = 新会话）</summary>
            public int LastTick = int.MinValue;
            /// <summary>已写过会话头没有</summary>
            public bool SessionStarted;
            /// <summary>
            /// 本文件的会话是否**已经在 `RunScope` 里开过号**（= 已调过 `EnsureSessionForPoll`）。
            /// ★ 每个会话只开一次号：换局/读档（tick 倒退）时置回 false，下一轮重开。
            /// </summary>
            public bool SessionOpened;
            /// <summary>已写过基线没有</summary>
            public bool BaselineWritten;
            /// <summary>已见过的信件 ID（`HashSet` 而不是 `Dictionary` —— 只判存在，不存内容）</summary>
            public readonly HashSet<int> Seen = new HashSet<int>();
            /// <summary>写盘失败只警告一次</summary>
            public bool WriteWarned;
            /// <summary>读信失败只警告一次</summary>
            public bool ReadWarned;
        }

        // ══════════════════════════════════════════════════════════════
        // 驱动入口（由 `PlayerActionObserver` 每轮末尾调用）
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// ★ **一整轮**：读当前信件列表 → 与 `Seen` 差出"新到达" → 追加成 `letter` 事件行。
        ///
        /// 本方法是本类**唯一**的对外入口；**整段吞异常**（绝不影响游戏）。
        ///
        /// 调用时机：`PlayerActionObserver.MapComponentTick` 里**每次轮询都调**（含
        /// `firstOfSession` 那一轮、也含 `Step` 因为建文件失败而提前返回的那些轮）——
        /// 因为事件线**不能因为操作线出问题就断掉**。
        /// </summary>
        public static void ObserveLettersFrom(Map map, int pollTick)
        {
            try
            {
                if (!Enabled) return;
                if (map == null) return;

                int mapId;
                try { mapId = map.uniqueID; } catch (Exception e) { WarnOnce("mapId", "取 map.uniqueID 失败：" + e.Message); return; }

                // ⚠ 状态 key 也带 `mapId` 即可：**同一个进程内**殖民地名+mapId 已经能唯一确定一个输出文件；
                //   runId 的作用在**文件名/目录**层（不同 run = 不同的 RunScope._runDir ⇒ 不同文件路径）。
                string colonyRaw = SafeColonyName(map);
                string colonySafe = RunNaming.SafeSegment(colonyRaw);
                string stateKey = colonySafe + "#" + mapId.ToString(CultureInfo.InvariantCulture);

                int absTick;
                try { absTick = Find.TickManager != null ? Find.TickManager.TicksAbs : 0; } catch (Exception) { absTick = 0; }
                string wall;
                try { wall = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture); } catch (Exception) { wall = ""; }

                FileState st;
                if (!_states.TryGetValue(stateKey, out st))
                {
                    st = new FileState();
                    st.MapId = mapId;
                    st.ColonyName = colonyRaw;
                    _states[stateKey] = st;
                }

                // ── 文件路径：`<run 目录>/events-<殖民地名>-<mapId>.jsonl`（惰性准备、跨轮复用）──
                //
                //   ⚠ 路径一旦定下来就**不再重算**：它已经绑定了"那个 run 目录"，
                //     重算会在 `RunScope` 尚未就绪时把 `st.File` 冲成 null（白写一轮）。
                if (string.IsNullOrEmpty(st.File) && !EnsureFile(st, colonyRaw))
                    return;                                   // 建不出文件就这一轮不做（警告已在 EnsureFile 里报过）

                // ── ① tick 倒退 / 新会话：基线、去重集合、轮询号全部重开 ──
                //
                //   ⚠ 与 `PlayerActionObserver` 是**同一条**纪律：读档/换地图会让 tick 倒退，
                //     不重开去重集合就会把"上一局的信件列表"当成"这一局刚到的信"刷一大堆假事件。
                //     （⚠ 注意：**重启游戏**走不到这里 —— 那时进程内状态全没了，
                //       `Seen` 本来就是空的，效果与"重开基线"相同：首轮仍会把当前信件全部写一遍 arrive。）
                if (st.LastTick != int.MinValue && pollTick < st.LastTick)
                {
                    WarnOnce("tick-back-" + stateKey,
                        "tick 倒退（" + st.LastTick + " → " + pollTick + "），事件线按新会话重开基线（文件：" + st.File + "）");
                    st.SessionStarted = false;
                    st.SessionOpened = false;                 // ★ 下一个会话要重新开号（与另两条流共用一本账）
                    st.BaselineWritten = false;
                    st.Seen.Clear();
                    st.PollSeq = 0;
                    st.LastTick = int.MinValue;
                }

                // ── ①-b ★ 会话开号（**与操作线/快照线共用同一本账**）──
                //
                //   用户 2026-10-04 要求："actions/events 的会话必须共享 RunScope 的 sessionId"。
                //   `RunScope.EnsureSessionForPoll` 内部按 tick 节流 ⇒ 同一次换局
                //   （操作流与事件流都会检测到 tick 倒退）**只吃掉一个会话号**。
                //   ⚠ 位置在落盘之前：会话号是内存里的事，不能因为"文件还没建出来"而漏开。
                if (!st.SessionOpened)
                {
                    RunScope.EnsureSessionForPoll(pollTick);
                    st.SessionOpened = true;
                }
                SessionMeta meta = SessionMeta.Capture();

                st.LastTick = pollTick;
                st.PollSeq++;

                // ── ② 读当前信件列表（**只读**；这里一个游戏对象都不改）──
                List<Letter> letters = null;
                try
                {
                    LetterStack stack = Find.LetterStack;
                    if (stack != null) letters = stack.LettersListForReading;
                }
                catch (Exception e)
                {
                    if (!st.ReadWarned)
                    {
                        st.ReadWarned = true;
                        WarnOnce("read-" + stateKey, "读 Find.LetterStack.LettersListForReading 失败（已吞）：" + e.Message);
                    }
                }
                // 读不到就当"这一轮没有信" —— 不写事件，也不动基线之外的状态
                int count = (letters != null) ? letters.Count : 0;

                var sb = new StringBuilder(4096);

                // ── ③ 会话头 + 基线（会话的第一轮各写一条）──
                if (!st.SessionStarted)
                {
                    WriteSessionLine(sb, st, colonySafe, pollTick, absTick, wall, meta);
                    st.SessionStarted = true;
                }
                if (!st.BaselineWritten)
                {
                    WriteBaselineLine(sb, st, count, pollTick, absTick, wall, meta);
                    st.BaselineWritten = true;
                }

                // ── ④ 逐封判"新到达"──
                int arrived = 0;
                if (letters != null)
                {
                    for (int i = 0; i < letters.Count; i++)
                    {
                        Letter let = letters[i];
                        if (let == null) continue;

                        int id;
                        try { id = let.ID; } catch (Exception) { continue; }

                        // ★ 去重的**唯一依据**：`Letter.ID`（元数据实测：public 字段，
                        //   游戏内部按它索引信件，同一局内唯一且稳定）
                        if (st.Seen.Contains(id)) continue;

                        // ⚠ `Seen` 的更新放在**行拼完之后**：拼行途中抛异常 ⇒ 这一封这一轮不算
                        //   "已见"，下一轮还会重试（宁可晚一行，也不要静默丢一封）。
                        EmitLetterLine(sb, st, let, id, pollTick, absTick, wall, meta);
                        st.Seen.Add(id);
                        arrived++;
                    }
                }
                LastArrived = arrived;

                // ── ⑤ 落盘（一行一个事件；这一轮可能 0 条）──
                if (sb.Length > 0) Append(st, sb.ToString());
            }
            catch (Exception e)
            {
                try { Log.Warning("[ColonyRecorder][事件] 信件轮询异常（已吞）：" + e.Message); } catch (Exception) { }
            }
        }

        // ══════════════════════════════════════════════════════════════
        // 输出文件
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 惰性建立输出文件路径：**`<本进程 run 目录>/events-<殖民地名>-<mapId>.jsonl`**（**追加**写）。
        ///
        /// ★ 文件名风格与另外两条流**逐条一致**（同一个 run 目录、`<前缀>-<殖民地名>-<mapId>.jsonl`、
        ///   目录内跨会话追加）—— 三份产物永远在同一个 `runs/run-<runId>/` 里，能一起拷走。
        ///   ⚠ 路径拼接**只有一处**（`RunScope.PathFor` → `RunNaming.PathFor`），
        ///   老实现这里自己 `Path.Combine`，三套各一份，加目录层必然分裂。
        ///
        /// ⚠ 返回 `false` 表示"这一轮先不写"（run 目录还没就绪；降级说明与原因已由 `RunScope` 报过一次）。
        ///   本方法**不缓存失败**：`st.File` 保持空 ⇒ 下一轮自动再试（用户明确要求）。
        /// </summary>
        private static bool EnsureFile(FileState st, string colonyNameRaw)
        {
            try
            {
                // ★ 唯一出口：run 目录 + 文件名（内部做文件系统安全化）
                string file = RunScope.PathFor(RunNaming.EventsPrefix, colonyNameRaw, st.MapId);
                if (string.IsNullOrEmpty(file)) return false;      // 降级：这一轮不写

                st.File = file;

                bool existed = false;
                try { existed = File.Exists(file); } catch (Exception) { }

                try
                {
                    Log.Message("[ColonyRecorder][事件] 本次会话**追加**到 " + file
                        + (existed ? "（文件已存在，接着往里写）" : "（新文件）")
                        + "；runId=" + (RunScope.RunId ?? "(未就绪)")
                        + " sessionSeq=" + RunScope.SessionSeq
                        + "；只读 Find.LetterStack，按 Letter.ID 去重");
                }
                catch (Exception) { }
                return true;
            }
            catch (Exception e)
            {
                WarnOnce("ensure-" + st.MapId, "建立事件输出文件失败（已吞）：" + e.Message);
                return false;
            }
        }

        /// <summary>追加写（失败只警告一次，绝不影响游戏）</summary>
        private static void Append(FileState st, string text)
        {
            if (st == null || string.IsNullOrEmpty(text)) return;
            if (string.IsNullOrEmpty(st.File)) return;

            try
            {
                File.AppendAllText(st.File, text);
                WrittenLines++;
            }
            catch (Exception e)
            {
                if (!st.WriteWarned)
                {
                    st.WriteWarned = true;
                    WarnOnce("write-" + st.File, "写事件文件失败（已吞，后续不再重复报）：" + e.Message);
                }
            }
        }

        // ══════════════════════════════════════════════════════════════
        // 三种行
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// **会话头**（`kind:"session"`）—— 每次"新的一局 / 读档 / tick 倒退"写一条，
        /// 是**事件流的起点标记**（一个 `events-*.jsonl` 里会跨很多次会话）。
        ///
        /// ```json
        /// {"kind":"session","op":"start","pollSeq":1,"tick":123,"absTick":670123,"day":0,
        ///  "wallClock":"22:10:03","tickSource":"game","detectedTick":123,
        ///  "colonyName":"…","mapId":0,"intervalTicks":30}
        /// ```
        ///
        /// ★ 额外带一个 `actionPollSeq`（= 同一轮里 `PlayerActionObserver.PollSeq` 的值）：
        ///   本文件的 `pollSeq` 是**本文件自己的**（会话从 1 起，可能因为之前有别的会话
        ///   而与操作文件不同步），而 `actionPollSeq` 是**操作线那本账**上的同一个轮询号 ——
        ///   分析端拿它就能把两条线**精确对齐**到同一拍（对应 `actions-*.jsonl` 里
        ///   `pollSeq` 相同的那些行）。
        /// </summary>
        private static void WriteSessionLine(StringBuilder sb, FileState st, string colonySafe,
            int tick, int absTick, string wall, SessionMeta meta)
        {
            try
            {
                SessionCount++;
                int actionPollSeq = 0;
                try { actionPollSeq = PlayerActionObserver.PollSeq; } catch (Exception) { }
                sb.Append("{\"kind\":\"session\",\"op\":\"start\"");
                AppendCommon(sb, st.PollSeq, tick, absTick, wall);
                sb.Append(",\"colonyName\":").Append(JsonString(st.ColonyName ?? ""));
                sb.Append(",\"colonyFileSafe\":").Append(JsonString(colonySafe ?? ""));
                sb.Append(",\"mapId\":").Append(st.MapId);
                sb.Append(",\"intervalTicks\":").Append(PlayerActionObserver.EffectiveInterval());
                sb.Append(",\"actionPollSeq\":").Append(actionPollSeq);
                sb.Append(",\"letterSource\":").Append(JsonString("Find.LetterStack.LettersListForReading"));
                sb.Append(",\"dedupeKey\":").Append(JsonString("Letter.ID"));
                if (meta != null) meta.AppendTo(sb);          // ★ 行尾 run/session 元数据
                sb.Append("}\n");
            }
            catch (Exception) { }
        }

        /// <summary>
        /// **基线行**（`kind:"baseline"`）—— 会话第一轮写一条，记录"这一轮开始时 `LetterStack`
        /// 里有几封信"（`letters` = 当时读到的条数；`seen` = 去重集合里已有的条数）。
        ///
        /// ⚠ **它不过滤任何事件**：基线里的那些信**照样**会在同一轮被写成 `op:"arrive"`
        ///   （`Seen` 是空的，见第 ④ 步）。所以它**不是**"这些信不产生 arrive"的开关，
        ///   而是**"本会话开始时列表里的信有哪些"的现场记录**：
        ///   分析端拿 `baseline.letters` 就能判出"本轮这批 arrive 里哪些是重放的旧信"
        ///   （同一封 `letterId` 若在**上一个会话**已经出现过，就是重启/读档造成的重复）。
        ///   ⇒ 本行之前（更早的会话）的历史确实不在本文件里，这一点不变。
        ///
        /// ```json
        /// {"kind":"baseline","op":"init","pollSeq":1,"tick":123,…,"letters":3}
        /// ```
        /// </summary>
        private static void WriteBaselineLine(StringBuilder sb, FileState st, int lettersCount,
            int tick, int absTick, string wall, SessionMeta meta)
        {
            try
            {
                sb.Append("{\"kind\":\"baseline\",\"op\":\"init\"");
                AppendCommon(sb, st.PollSeq, tick, absTick, wall);
                sb.Append(",\"mapId\":").Append(st.MapId);
                sb.Append(",\"letters\":").Append(lettersCount);
                sb.Append(",\"seen\":").Append(st.Seen.Count);
                if (meta != null) meta.AppendTo(sb);          // ★ 行尾 run/session 元数据
                sb.Append("}\n");
            }
            catch (Exception) { }
        }

        /// <summary>
        /// **一封信件到达**（`kind:"letter"` / `op:"arrive"`）—— 本 MVP 的**唯一事件类型**。
        ///
        /// ⚠ 两线对齐的**精确锚点**是会话头的 `actionPollSeq`（同一轮里操作线那本账上的号），
        ///   不是本字段：本字段是**按本文件（= 本殖民地名 + 本 mapId）**计数的，
        ///   而 `PlayerActionObserver.PollSeq` 是**全进程的静态计数**（多地图共用一本账）——
        ///   同一次会话里多个 `events-*` 文件并存时，两张账的 `pollSeq` 不保证逐值相等。
        ///
        /// ## ★ `op:"arrive"` 有两种来源（分析端必须分开，否则会把旧信当新事件）
        ///
        /// 1. **会话内真到达**：来信后第一次被轮询到 ⇒ `lagTicks ≤ 轮询间隔`
        ///    （默认为 30），且 `arrivalAbsTick ≥` 本会话的 `absTick`。
        /// 2. **首轮重放**（重启游戏 / 读档后第一轮，`Seen` 不持久化）：当时 `LetterStack` 里
        ///    挂着**但本会话还没见过**的信，全部各写一行 `arrive`。这些信可能已经挂了
        ///    几个游戏日 ⇒ **`lagTicks` 会远大于轮询间隔**（几十万 tick 量级），
        ///    并且推出来的 `arrivalAbsTick` **可以早于本会话的 `absTick`**（即落在本会话开始之前）。
        ///    这不是"到得比采样早"，是**重复记账**；按 `letterId` 与更早的会话比对即可识别。
        ///
        /// ⇒ 想只统计"真正的新信件"，就要**先用会话头切段**，再按上面两条过滤。
        ///
        /// 字段 schema（**字段名逐字固定**，分析端依赖；顺序恒定）：
        ///
        /// | 字段 | 类型 | 含义 | 来源 |
        /// | --- | --- | --- | --- |
        /// | `kind` | string | 恒为 `"letter"` | 本实现 |
        /// | `op` | string | 恒为 `"arrive"` | 本实现 |
        /// | `pollSeq` | int | 本文件（= 本殖民地名 + 本 mapId）的轮询序号（从 1 起）—— 跨文件对齐用 | `PlayerActionObserver` 的同一循环 |
        /// | `tick` | int | 检测时的游戏 tick（`TicksGame`） | 游戏 |
        /// | `absTick` | int | 检测时的绝对 tick（`TicksAbs`） | 游戏 |
        /// | `day` | int | `tick / 60000`（**殖民地建立以来的天数**，与另外两套一致） | 派生 |
        /// | `wallClock` | string | 真实时间 `HH:mm:ss`（仅排查用，不用于时序分析） | 本机 |
        /// | `tickSource` | string | 恒为 `"game"` —— 表示 `arrivalTick` 是**游戏自带时刻** | 本实现 |
        /// | `detectedTick` | int | 我们**检测到**它的 tick（= `tick`，显式重复一份避免歧义） | 游戏 |
        /// | `detectedAbsTick` | int | 我们检测到它的绝对 tick（= `absTick`） | 游戏 |
        /// | `detectedWallClock` | string | 我们检测到它的真实时刻（= `wallClock`） | 本机 |
        /// | `arrivalTick` | int | ★**游戏自带的到达时刻**（`Letter.arrivalTick`） | 游戏 |
        /// | `arrivalAbsTick` | int \| null | `absTick - (detectedTick - arrivalTick)`（由两个游戏时刻推出；**首轮重放的旧信可早于本会话 `absTick`**） | 派生 |
        /// | `lagTicks` | int | `detectedTick - arrivalTick`（> 0 说明那封信到得比我们早）。⚠ **只有会话内真到达才 ≤ 轮询间隔**；重启/读档首轮重放的旧信会远大于间隔 | 派生 |
        /// | `arrivalTime` | number | `Letter.arrivalTime`（真实时间秒，仅供排查） | 游戏 |
        /// | `letterId` | int | ★`Letter.ID`（**去重键**） | 游戏 |
        /// | `letterUid` | string | `Letter.GetUniqueLoadID()`（拿不到就空串） | 游戏 |
        /// | `letterDef` | string | `Letter.def.defName`（如 `ThreatBig` / `PositiveEvent`） | 游戏 |
        /// | `letterClass` | string \| null | `Letter.def.letterClass.Name`（def 没写就 `letter.GetType().FullName`） | 游戏 |
        /// | `labelRaw` | string | `Letter.Label` 的**原样**字符串（不翻译、不加工） | 游戏 |
        /// | `relatedFaction` | object \| null | `{def, name, loadId}`；没有关联派系就是 `null`（字段恒定存在） | 游戏 |
        /// | `mapId` | int | 地图 id | 游戏 |
        /// </summary>
        private static void EmitLetterLine(StringBuilder sb, FileState st, Letter let, int id,
            int tick, int absTick, string wall, SessionMeta meta)
        {
            // ── 先把所有字段读出来（每个字段单独 try/catch：某一项读不到不影响整行）──
            string defName = "";
            string letterClass = null;
            try
            {
                LetterDef d = let.def;
                if (d != null)
                {
                    defName = d.defName ?? "";
                    Type t = d.letterClass;                    // public 字段（元数据实测）
                    if (t != null) letterClass = t.Name;
                }
            }
            catch (Exception) { }

            // def 没写 letterClass 时退化为**实体自己的类型**（同样是"可可靠取得"的）
            if (string.IsNullOrEmpty(letterClass))
            {
                try { letterClass = let.GetType().Name; } catch (Exception) { }
            }

            string labelRaw = "";
            try { if (let.Label != null) labelRaw = let.Label.ToString(); } catch (Exception) { }

            // ⚠ `Letter.Label` 是 `TaggedString`（结构体）；`ToString()` 拿到的是**原样文本**，
            //   不做任何翻译/格式化 —— 分析端要"raw"就给它 raw。

            string uid = "";
            try { uid = let.GetUniqueLoadID(); } catch (Exception) { }
            if (uid == null) uid = "";

            int arrivalTick = tick;
            try { arrivalTick = let.arrivalTick; } catch (Exception) { }

            float arrivalTime = 0f;
            try { arrivalTime = let.arrivalTime; } catch (Exception) { }

            // relatedFaction 读取链（**只找"可靠"的来源**，找不到就给 null，不瞎猜）：
            //   ① `Letter.relatedFaction`（public 字段，游戏自己给的值）
            //   ② `ChoiceLetter_RansomDemand.faction`（元数据实测的 public 字段，绑票信那种）
            Faction faction = null;
            try { faction = let.relatedFaction; } catch (Exception) { }
            if (faction == null)
            {
                try
                {
                    ChoiceLetter_RansomDemand rd = let as ChoiceLetter_RansomDemand;
                    if (rd != null) faction = rd.faction;
                }
                catch (Exception) { }
            }

            int lag = tick - arrivalTick;
            int arrivalAbs;
            try { arrivalAbs = absTick - lag; } catch (Exception) { arrivalAbs = 0; }

            sb.Append("{\"kind\":\"letter\",\"op\":\"arrive\"");
            AppendCommon(sb, st.PollSeq, tick, absTick, wall);
            sb.Append(",\"arrivalTick\":").Append(arrivalTick);
            sb.Append(",\"arrivalAbsTick\":").Append(arrivalAbs);
            sb.Append(",\"lagTicks\":").Append(lag);
            sb.Append(",\"arrivalTime\":").Append(arrivalTime.ToString("F2", CultureInfo.InvariantCulture));
            sb.Append(",\"letterId\":").Append(id);
            sb.Append(",\"letterUid\":").Append(JsonString(uid));
            sb.Append(",\"letterDef\":").Append(JsonString(defName));
            if (letterClass == null) sb.Append(",\"letterClass\":null");
            else sb.Append(",\"letterClass\":").Append(JsonString(letterClass));
            sb.Append(",\"labelRaw\":").Append(JsonString(labelRaw));
            sb.Append(",\"relatedFaction\":");
            AppendFaction(sb, faction);
            sb.Append(",\"mapId\":").Append(st.MapId);
            if (meta != null) meta.AppendTo(sb);              // ★ 行尾 run/session 元数据
            sb.Append("}\n");
        }

        /// <summary>
        /// **每行都带的公共字段**（会话头 / 基线 / 信件事件三者的形状**逐字一致**，
        /// 让分析端一套代码读所有行，不用按 `kind` 分叉）。
        ///
        /// ★ `tickSource` / `detectedTick` / `detectedAbsTick` / `detectedWallClock` 是**显式**的：
        ///   本文件里有两个时刻（游戏自带 `arrivalTick` 与我们的采样时刻），
        ///   不写清楚分析端一定会把它们弄混（本项目已经因为"两种时刻混用"踩过坑）。
        /// </summary>
        private static void AppendCommon(StringBuilder sb, int pollSeq, int tick, int absTick, string wall)
        {
            sb.Append(",\"pollSeq\":").Append(pollSeq);
            sb.Append(",\"tick\":").Append(tick);
            sb.Append(",\"absTick\":").Append(absTick);
            sb.Append(",\"day\":").Append(tick / 60000);
            sb.Append(",\"wallClock\":").Append(JsonString(wall));
            sb.Append(",\"tickSource\":").Append(JsonString("game"));
            sb.Append(",\"detectedTick\":").Append(tick);
            sb.Append(",\"detectedAbsTick\":").Append(absTick);
            sb.Append(",\"detectedWallClock\":").Append(JsonString(wall));
        }

        /// <summary>
        /// `relatedFaction` 字段 —— **恒存在**：
        /// 有关联派系 → `{"def":"Pirate","name":"…","loadId":123}`；没有 → `null`。
        ///
        /// ⚠ 恒存在（而不是"读不到就省略"）是**故意的**：分析端不该为了一个可能缺席的字段
        ///   写 `if (f in row)` 那种分支；`null` 是最明确的"这封信确实没有关联派系"。
        /// </summary>
        private static void AppendFaction(StringBuilder sb, Faction f)
        {
            if (f == null) { sb.Append("null"); return; }

            string def = "";
            try { if (f.def != null) def = f.def.defName ?? ""; } catch (Exception) { }

            string name = "";
            try { name = f.Name ?? ""; } catch (Exception) { }

            int loadId = -1;
            try { loadId = f.loadID; } catch (Exception) { }

            sb.Append("{\"def\":").Append(JsonString(def));
            sb.Append(",\"name\":").Append(JsonString(name));
            sb.Append(",\"loadId\":").Append(loadId);
            sb.Append('}');
        }

        // ══════════════════════════════════════════════════════════════
        // 小工具
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 殖民地名取法与文件系统安全化**直接复用**另外两套现成的：
        /// `SnapshotReaders.ReadGameDateAndIdentity` + `ColonyRecorder.FileSafe`
        /// （重写就会出现"快照叫一个名、事件文件叫另一个名"的分裂）。
        /// </summary>
        private static string SafeColonyName(Map map)
        {
            string name = null;
            try
            {
                var snap = new ColonySnapshot();
                SnapshotReaders.ReadGameDateAndIdentity(map, snap);
                name = snap.ColonyName;
            }
            catch (Exception) { }

            try
            {
                if (string.IsNullOrEmpty(name) && Find.World != null && Find.World.info != null)
                    name = Find.World.info.name;
            }
            catch (Exception) { }

            return name ?? "";
        }

        /// <summary>同一条警告**只报一次**（失败要留痕，但绝不刷爆 `Player.log`）</summary>
        private static void WarnOnce(string key, string msg)
        {
            try
            {
                if (_warnedOnce.Contains(key)) return;
                _warnedOnce.Add(key);
                Log.Warning("[ColonyRecorder][事件] " + msg);
            }
            catch (Exception) { }
        }

        /// <summary>
        /// JSON 字符串转义 —— **逐条复用** `PlayerActionObserver.JsonString`
        /// （它与老快照的 `ColonySnapshot.Quote` 规则一致；三份 JSONL 用同一套转义）。
        /// </summary>
        internal static string JsonString(string s)
        {
            return PlayerActionObserver.JsonString(s);
        }
    }
}
