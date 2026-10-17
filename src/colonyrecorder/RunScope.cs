using System;
using System.Globalization;
using System.IO;
using System.Text;
using Verse;

namespace ColonySnapshots
{
    /// <summary>
    /// ★★ **进程级 run 作用域** —— "这次 RimWorld 进程启动"的唯一身份与唯一落点。
    ///
    /// ## 它解决什么问题（用户 2026-10-04 要求）
    ///
    /// 老实现把三流（快照 / 操作 / 事件）都写进 `foodcontrol-snapshots/` 根目录，
    /// 文件名只有 `<前缀>-<殖民地名>-<mapId>.jsonl` ⇒ **两次启动的同名殖民地会追加进同一个文件**，
    /// 靠什么区分都不干净（`mapId` 新殖民地恒为 0；重启后 tick 从 1 重来，
    /// 进程内状态全丢，采集端**不认为换局**）。真数据里已经实测到：同一个 `actions-` 文件里
    /// **3 条 `session` 全是 `tick=1`**（三次开局混在一起）。
    ///
    /// ⇒ 现在按**每次进程启动**生成一个 `runId`，三流写进
    /// `foodcontrol-snapshots/runs/run-<runId>/`（**同一个 run 目录**），
    /// 于是"哪一局"这件事从**分析端的纪律**（开局前清目录 + 用 session 切段）
    /// 变成**目录结构上的事实**。
    ///
    /// ## 三层身份（别混）
    ///
    /// | 层 | 是什么 | 落在哪 | 什么时候变 |
    /// | --- | --- | --- | --- |
    /// | **run** | 一次**进程启动** | `runs/run-<runId>/` 目录名 | 进程重启 |
    /// | **session** | 进程内**换局 / 读档**（tick 倒退） | 行字段 `sessionId` / `sessionSeq` | tick 倒退 |
    /// | **map** | 地图 | 文件名里的 `<mapId>` | 换地图 |
    ///
    /// ⚠ **同一条流的文件在一个 run 内是追加的**（用户要"文件名简洁"）：
    ///   一个 `actions-<名>-<mapId>.jsonl` 里可以有多个 session，
    ///   分析端按 `sessionId` / `sessionSeq` **切段**，**不要**按文件名切。
    ///
    /// ## 初始化纪律（用户 2026-10-04 明确要求的那三条）
    ///
    /// 1. **只初始化一次、且可重入**：`EnsureReady()` 幂等；反复调不会换 runId。
    ///    （runId 一旦定下来就**进程内恒定** —— 中途换目录会把一次启动的数据劈成两半。）
    /// 2. **不能因为"存档目录还没就绪"就永久失败**：取不到 `/ 建不出` 目录时**不缓存失败**，
    ///    下次调用**再试**，只是退避节流（`RetryDelayMs`，最多 `MaxAttempts` 次）；
    ///    `Log.Warning` 明确写出**降级行为**（"写盘已停，还在重试"）。
    /// 3. **绝不抛异常**：所有入口整段 try/catch —— 录制器绝不能让游戏崩。
    ///
    /// ## 降级行为（失败时到底怎么样 —— 必须说清，用户要求）
    ///
    /// | 情况 | 行为 |
    /// | --- | --- |
    /// | 存档目录取不到 / 建不出（前 10 次） | `PathFor` 返回 `null` ⇒ 三条流**这一轮不写**（内存里的差分/序号照常推进），`Log.Warning` 一次 + 按计划重试（第 1 次立即试，之后每次失败隔约 10 秒） |
    /// | 连续 10 次之后仍失败 | 仍是"这一轮不写"，但**本进程内不再重试、也不碰文件系统**（`EnsureReady` 开头直接返回 `false`，只把最后原因与"已停止重试"各记一次日志）—— 避免每 tick / 每 30 tick 刷爆 `Player.log`；**重启游戏（新进程）会重新从第 1 次尝试开始** |
    /// | 单个文件写失败（磁盘满 / 被占） | 那一条流的 `_writeFailed` 置位 ⇒ **本会话内不再重试写盘**，只记一次警告（换局 / 读档开新会话时清掉 ⇒ 新文件重新给机会） |
    /// | 一切正常 | 三流写进**同一个** `runs/run-<runId>/` 目录 |
    ///
    /// ⚠ 老的 `foodcontrol-snapshots/` **根目录里的老文件一个都不动**（那是用户的数据），
    ///   新数据也不往根目录写（防止又把两局混起来）。
    /// </summary>
    public static class RunScope
    {
        // ══════════════════════════════════════════════════════════════
        // 常量
        // ══════════════════════════════════════════════════════════════

        /// <summary>初始化最多试几次（每次失败后隔 `RetryDelayMs` 再试）</summary>
        private const int MaxAttempts = 10;

        /// <summary>两次初始化尝试之间的最小间隔（真实毫秒）—— 防止每 30 tick 都去摸一次文件系统</summary>
        private const int RetryDelayMs = 10000;

        // ══════════════════════════════════════════════════════════════
        // 进程级状态（一次启动 == 一个 run）
        // ══════════════════════════════════════════════════════════════

        private static readonly object _lock = new object();
        private static readonly Random _rand = new Random(unchecked(Environment.TickCount * 31 + AppDomain.CurrentDomain.Id));

        /// <summary>已经成功初始化了吗（= runId 已定，**永不再变**）</summary>
        private static bool _ready;

        /// <summary>初始化尝试次数（失败节流用；成功后不再看它）</summary>
        private static int _attempts;

        /// <summary>下一次允许尝试的真实时刻（`Environment.TickCount`，毫秒）</summary>
        private static int _nextAttemptAt;

        /// <summary>本次启动的 runId（`_ready` 之前为 null）</summary>
        private static string _runId;

        /// <summary>本次启动的 run 目录全路径（`_ready` 之前为 null）</summary>
        private static string _runDir;

        /// <summary>存档根目录（`GenFilePaths.SaveDataFolderPath`；诊断/日志用）</summary>
        private static string _savesRoot;

        /// <summary>"初始化失败"只报一次（用户在 `Player.log` 里能一眼看到降级了）</summary>
        private static bool _failureLogged;

        /// <summary>"已到上限、停止重试"只报一次（第 `MaxAttempts` 次失败那一刻）</summary>
        private static bool _gaveUpLogged;

        /// <summary>降级警告里带的最后一条原因</summary>
        private static string _lastError = "";

        // ── session 账本（进程内递增，**三条流共用一本**）──
        private static int _sessionSeq;
        private static int _beginCalledTick = int.MinValue;

        // ══════════════════════════════════════════════════════════════
        // 只读视图
        // ══════════════════════════════════════════════════════════════

        /// <summary>本次启动的 runId（未初始化时为 `null`）—— **进程内恒定**</summary>
        public static string RunId
        {
            get { lock (_lock) { return _runId; } }
        }

        /// <summary>本次启动的 run 目录全路径（未初始化时为 `null`）</summary>
        public static string RunDir
        {
            get { lock (_lock) { return _runDir; } }
        }

        /// <summary>已就绪（= runId 与目录都定下来了）</summary>
        public static bool Ready
        {
            get { lock (_lock) { return _ready; } }
        }

        /// <summary>
        /// 老根目录（`…/foodcontrol-snapshots`）—— **只用于日志/诊断**，
        /// 新数据**不写**这里（老文件在这里躺着，不动）。
        /// </summary>
        public static string OutDirRoot
        {
            get
            {
                try
                {
                    string saves = GenFilePaths.SaveDataFolderPath;
                    if (string.IsNullOrEmpty(saves)) return null;
                    return Path.Combine(saves, RunNaming.RootDirName);
                }
                catch (Exception) { return null; }
            }
        }

        /// <summary>当前会话号（0 = 还没开过会话；第一次 `BeginSession` 之后 ≥ 1）</summary>
        public static int SessionSeq
        {
            get { lock (_lock) { return _sessionSeq; } }
        }

        /// <summary>当前会话 id = `<runId>-s<sessionSeq，4 位>`；没开过会话时返回空串</summary>
        public static string SessionId
        {
            get { lock (_lock) { return BuildSessionIdLocked(_runId, _sessionSeq); } }
        }

        // ══════════════════════════════════════════════════════════════
        // 初始化
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// **幂等**初始化：定下 runId（只生成一次）并建出 run 目录。
        ///
        /// 返回 `true` = 可以用 `PathFor` 了。返回 `false` = 这次还不行（原因已 `Log.Warning`）。
        ///
        /// ## 次数与退避（**"最多 10 次"就是 10 次**）
        ///
        /// - **第 1 次尝试永远放行**：正常启动必须在第一刻就能成功 —— 不拿 `_nextAttemptAt`
        ///   的初值去卡它（`Environment.TickCount` 在开机初期可以很小、甚至回绕成负数）。
        /// - 之后**每次失败**都把下一次推到 `RetryDelayMs` 之后（`FailLocked` 里设 `_nextAttemptAt`）
        ///   ⇒ **最多 `MaxAttempts` 次**，且两次之间按计划退避，不是每 tick 来一次。
        /// - **第 `MaxAttempts` 次失败之后**：后续调用在**函数开头直接返回 `false`** ——
        ///   一次都不再触碰文件系统（`GenFilePaths` / `Directory.CreateDirectory` 全都不碰），
        ///   也不再刷日志。
        ///   ⚠ 老实现把边界写反了：`_attempts` 到顶后 `retryable` 变 `false`，
        ///   反而**跳过**了退避判断并继续 `_attempts++` ⇒ 上限形同虚设，
        ///   之后**每个 tick** 都来摸一次文件系统（正是这条承诺的反面）。
        /// - 上限是**进程级**的：重新启动游戏（新进程）时静态状态归零 ⇒ 从第 1 次尝试重新开始，
        ///   "新进程出现后允许再试"就落在这条上（不需要任何人调额外 API）。
        ///   整个降级过程**绝不抛异常**，警告**每进程至多两次**（首次降级一次、到上限停止重试一次），
        ///   绝不按 tick 刷屏。
        ///
        /// ⚠ 调用点（三处，都是"首次安全时机"）：
        ///   ① `RecorderMod` 构造函数（尽早让日志里出现 runId / run 目录）；
        ///   ② `ColonyRecorder` 第一次 `Capture` 前；
        ///   ③ 操作流 / 事件流第一次 `EnsureFile` 前（它们**惰性**，因为构造时可能还没有存档目录）。
        ///   三处调的是同一个幂等方法，先到的那个定 runId，后到的直接复用。
        /// </summary>
        public static bool EnsureReady()
        {
            try
            {
                lock (_lock)
                {
                    if (_ready) return true;

                    // ── **上限**：失败满 `MaxAttempts` 次 ⇒ **本进程内一次都不再触碰文件系统** ──
                    //   （连退避判断都不做 —— 这里是"超过上限之后不要每 tick 摸文件系统"的**唯一**保证点。）
                    if (_attempts >= MaxAttempts) return false;

                    int now = Environment.TickCount;

                    // ── **退避**：第 1 次尝试永远放行；之后按 `_nextAttemptAt` 节流 ──
                    //   ⚠ `_attempts > 0` 这个前置条件不能省：`_nextAttemptAt` 初值是 0，
                    //     若拿它直接比，开机初期（`TickCount` 很小）或回绕成负数时
                    //     **正常启动的第一次就会被打回** —— 正常路径一个 tick 都不该被拖。
                    if (_attempts > 0 && now < _nextAttemptAt) return false;

                    _attempts++;
                    // ★ 用掉这一次之后**还有没有下一次** —— 必须在 `++` **之后**算：
                    //   老实现先算后 ++，于是第 10 次（最后一次）失败时日志还在说"还会重试"。
                    bool retryable = _attempts < MaxAttempts;

                    // ── ① 存档根目录：拿不到就**这次不算失败**，下次再试 ──
                    string saves = null;
                    try { saves = GenFilePaths.SaveDataFolderPath; } catch (Exception) { }
                    if (string.IsNullOrEmpty(saves))
                    {
                        FailLocked("取不到 GenFilePaths.SaveDataFolderPath（存档目录还没就绪）", retryable);
                        return false;
                    }

                    // ── ② runId：**只生成一次**，之后进程内恒定 ──
                    if (string.IsNullOrEmpty(_runId))
                        _runId = RunNaming.RunIdFrom(DateTime.Now, AppDomain.CurrentDomain.Id, RandomHex4Locked());

                    string dir = RunNaming.RunDir(saves, _runId);
                    if (string.IsNullOrEmpty(dir))
                    {
                        // 理论上到不了（saves 与 runId 都非空）—— 留一条明确的失败原因，不静默
                        FailLocked("RunNaming.RunDir 返回空（savesRoot 或 runId 为空）", retryable);
                        return false;
                    }

                    // ── ③ 建目录（`CreateDirectory` 对已存在的目录是幂等且"只建不删"）──
                    try { Directory.CreateDirectory(dir); }
                    catch (Exception e)
                    {
                        FailLocked("建 run 目录失败：" + e.Message + "（" + dir + "）", retryable);
                        return false;
                    }

                    _savesRoot = saves;
                    _runDir = dir;
                    _ready = true;
                    _lastError = "";
                    try
                    {
                        Log.Message("[ColonyRecorder][run] 本次启动 runId=" + _runId
                            + "；三条流（colony/actions/events）都写到 " + _runDir
                            + "（第 " + _attempts + " 次尝试成功）");
                    }
                    catch (Exception) { }
                    return true;
                }
            }
            catch (Exception e)
            {
                // ★ 绝不抛给游戏：这里连"记失败"都再包一层
                try { Log.Warning("[ColonyRecorder][run] EnsureReady 异常（已吞）：" + e.Message); } catch (Exception) { }
                return false;
            }
        }

        /// <summary>
        /// ★ **三条流唯一的路径出口** —— `run 目录 + <prefix>-<殖民地名safe>-<mapId>.jsonl`。
        ///
        /// 返回 `null` = 现在还不能写（原因已记过一次警告；调用方**直接跳过这一轮写盘**，
        /// 不要当成异常，也不要自己再拼路径）。
        ///
        /// ⚠ 本方法**不建目录**（建目录是 `EnsureReady` 的事）；它只保证"同一个 run 目录"。
        /// </summary>
        /// <param name="prefix">`colony` / `actions` / `events`（用 `RunNaming.*Prefix` 常量）</param>
        /// <param name="colonyNameRaw">**未安全化**的殖民地名（本方法内部走 `RunNaming.SafeSegment`）</param>
        /// <param name="mapId">地图 id（`map.uniqueID`）</param>
        public static string PathFor(string prefix, string colonyNameRaw, int mapId)
        {
            try
            {
                if (!EnsureReady()) return null;
                string dir;
                lock (_lock) { dir = _runDir; }
                return RunNaming.PathFor(dir, prefix, RunNaming.SafeSegment(colonyNameRaw), mapId);
            }
            catch (Exception e)
            {
                try { Log.Warning("[ColonyRecorder][run] 拼路径失败（已吞）：" + e.Message); } catch (Exception) { }
                return null;
            }
        }

        /// <summary>取 `map.uniqueID`（读不到给 0；不抛）</summary>
        public static int GetMapId(Map map)
        {
            try { return map != null ? map.uniqueID : 0; }
            catch (Exception) { return 0; }
        }

        /// <summary>
        /// "一次性动作"的去重 key（如"第 0 层可建造性一张图只写一次"）。
        ///
        /// ★ **必须带 runId**：不然读档/重启后（tick 倒退、但进程内静态集合还在同一个进程里）
        ///   会误以为"这张图已经写过第 0 层了" —— 而那一次写的是**另一个 run 的文件**。
        ///   带上 runId 之后，"每个 run × 每张图"各写一次，语义才自洽。
        /// </summary>
        public static string KeyForMap(int mapId)
        {
            string run;
            lock (_lock) { run = _runId; }
            return (run ?? "(no-run)") + "#" + mapId.ToString(CultureInfo.InvariantCulture);
        }

        // ══════════════════════════════════════════════════════════════
        // session 账本（三条流共用）
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 开一个新会话（换局 / 读档 = tick 倒退时调），会话号 +1。
        ///
        /// ★ **节流**：同一 tick 只会真正开一次（`_beginCalledTick` 守卫）——
        ///   操作流与事件流都会检测 tick 倒退，若各开一次，同一次换局就吃掉两个会话号，
        ///   两边的 `sessionSeq` 会对不上。⇒ 会话号由**这个守卫**保证"一次换局 = 一个号"。
        ///
        /// ⚠ 第 1 次调用（进程内第一次会话）也走这里 ⇒ 会话号**从 1 开始**（不是 0）。
        /// </summary>
        public static void EnsureSessionForPoll(int pollTick)
        {
            try
            {
                lock (_lock)
                {
                    if (_beginCalledTick == pollTick) return;
                    _beginCalledTick = pollTick;
                    _sessionSeq++;
                }
            }
            catch (Exception) { }
        }

        /// <summary>只读地造一个会话 id（`<runId>-s0001`；runId 还没定就返回空串）</summary>
        public static string BuildSessionId(string runId, int sessionSeq)
        {
            return BuildSessionIdLocked(runId, sessionSeq);
        }

        // ══════════════════════════════════════════════════════════════
        // 内部
        // ══════════════════════════════════════════════════════════════

        private static string BuildSessionIdLocked(string runId, int seq)
        {
            if (string.IsNullOrEmpty(runId) || seq <= 0) return "";
            return runId + "-s" + seq.ToString("D4", CultureInfo.InvariantCulture);
        }

        /// <summary>4 位十六进制随机（调用方持锁）</summary>
        private static string RandomHex4Locked()
        {
            try
            {
                int v = _rand.Next(0, 0x10000);
                return v.ToString("x4", CultureInfo.InvariantCulture);
            }
            catch (Exception) { return "0000"; }
        }

        /// <summary>
        /// 记一次失败。⚠ **不缓存失败**（`_ready` 保持 false）—— 用户明确要求
        /// "不能因为保存目录尚未就绪而永久失败"。但警告**只打一次**，
        /// 且把**降级行为**写清楚（写盘停了、还在重试），不静默、也不刷屏。
        ///
        /// <paramref name="retryable"/> = 用掉这一次之后**还有没有下一次**（由 `EnsureReady` 在
        /// `_attempts++` **之后**算好传进来）：`false` 表示这一次就是第 `MaxAttempts` 次，
        /// ⇒ 额外再记**一条**"已到上限、本进程内停止重试"的日志（每个进程至多一条）。
        /// 之后 `EnsureReady` 开头直接返回 `false`，不会再来这里 —— 所以这不是每 tick 的日志。
        /// </summary>
        private static void FailLocked(string why, bool retryable)
        {
            _lastError = why;
            _nextAttemptAt = unchecked(Environment.TickCount + RetryDelayMs);

            if (!_failureLogged)
            {
                _failureLogged = true;
                try
                {
                    Log.Warning("[ColonyRecorder][run] **降级**：run 目录还没就绪 ⇒ **这一轮三条流都不写盘**"
                        + "（内存里的采样/差分照常推进）。原因：" + why
                        + "\n  将在最多 " + MaxAttempts + " 次尝试内每隔约 " + (RetryDelayMs / 1000) + " 秒重试一次"
                        + "（" + (retryable ? "还会重试" : "已达上限，不再重试") + "）；"
                        + "期间绝不影响游戏，也绝不抛异常。");
                }
                catch (Exception) { }
            }

            // ★ 到上限：**只再报一条**，把"最后原因"和"本进程内不再重试"钉在日志里。
            //   ⚠ 这条之后的每次 `EnsureReady` 都在开头返回 false（不碰文件系统、不刷日志）——
            //     所以整条降级路径的日志总量固定为：首次降级 1 条 + 到上限 1 条。
            //   ⚠ 上限是**进程级**的：重启游戏（新进程）静态状态归零，会从第 1 次尝试重新开始
            //     —— 这就是"新进程条件出现时允许重试"的落地方式（不需要谁调额外 API）。
            if (!retryable && !_gaveUpLogged)
            {
                _gaveUpLogged = true;
                try
                {
                    Log.Warning("[ColonyRecorder][run] 初始化已尝试 " + MaxAttempts + " 次仍未成功 ⇒ **本进程内停止重试**"
                        + "（不再触碰文件系统，也不会每 tick 刷日志）。最后原因：" + why
                        + "\n  三条流仍按降级行为走：这一轮不写盘（内存里的采样 / 差分 / 序号照常推进）；"
                        + "重新启动游戏（新进程）会从第 1 次尝试重新开始。绝不影响游戏，也绝不抛异常。");
                }
                catch (Exception) { }
            }
        }
    }

    /// <summary>
    /// ★ **一行 JSON 都要带的 run/session 元数据**（三条流形状**完全一致**）。
    ///
    /// ## 为什么是"简单独立字段"而不是嵌套对象（用户 2026-10-04 明确要求）
    ///
    /// 老解析器是**按字段名取值**的（`analyze-snapshots.mjs` 只 `obj.foo`）：
    /// 追加**平级**字段老解析器**无感**；而塞一个 `{"run":{"id":…,"session":…}}` 嵌套对象，
    /// 就会诱使新代码去读嵌套、老代码读平级 —— 同一份数据两套读法，必翻车。
    /// ⇒ 三个平级字段：`runId`（string）/ `sessionId`（string）/ `sessionSeq`（int），
    ///   **一律追加在行尾**（老字段的名字、类型、相对顺序一个不动）。
    ///
    /// ## 为什么 `runId` 与 `sessionId` 都写（冗余）
    ///
    /// `sessionId` 已经含 `runId` 前缀（`<runId>-s0001`），看着冗余。
    /// 但分析端"只想要 run 边界"时不必去解析 `sessionId` 的字符串；
    /// "只想要会话边界"时也不必回填 runId。**冗余是为了让两种切段都成为一次 string/tuple 比较**。
    ///
    /// ## 快照流的特例
    ///
    /// 快照（`colony-*.jsonl`）一行是一整张快照，由 `ColonySnapshot.ToJsonLine()` 拼 ——
    /// 它的三个字段同样**追加在整行最后**（`ColonySnapshot` 自己写，见那边的注记）。
    /// 本类只服务 `actions-*` / `events-*` 两条**事件行**。
    /// </summary>
    public sealed class SessionMeta
    {
        /// <summary>`RunScope.RunId`（进程级；拿不到就是空串）</summary>
        public string RunId = "";

        /// <summary>`<runId>-s<sessionSeq>`（会话级；没开过会话就是空串）</summary>
        public string SessionId = "";

        /// <summary>会话号（1, 2, 3 …；0 = 还没开过会话）</summary>
        public int SessionSeq;

        /// <summary>
        /// ★ **快照当前状态**（每次开新会话都要重新 Capture 一次）。
        ///
        /// ⚠ 必须**成组读**（三个字段在同一次锁里取）：若分三次读，
        ///   中间恰好发生"换局 → 新会话"，就会拼出 `sessionSeq=2` 配 `sessionId=<s0001>`
        ///   这种**自相矛盾**的一行 —— 分析端按 sessionId 切段时会切错。
        /// </summary>
        public static SessionMeta Capture()
        {
            var m = new SessionMeta();
            try
            {
                int seq = RunScope.SessionSeq;
                string runId = RunScope.RunId;
                m.RunId = runId ?? "";
                m.SessionSeq = seq;
                m.SessionId = RunScope.BuildSessionId(runId, seq);
            }
            catch (Exception) { }
            return m;
        }

        /// <summary>
        /// 把三个字段**追加到行尾**（`{"…"}` 已收尾时由调用方保证前面还有内容）。
        ///
        /// ⚠ 调用方必须在**行尾**（最后一个 `}` 之前或之后）调用它；
        ///   本方法只负责拼 `,"runId":…,"sessionId":…,"sessionSeq":…`。
        /// </summary>
        public void AppendTo(StringBuilder sb)
        {
            if (sb == null) return;
            sb.Append(",\"runId\":").Append(PlayerActionObserver.JsonString(RunId));
            sb.Append(",\"sessionId\":").Append(PlayerActionObserver.JsonString(SessionId));
            sb.Append(",\"sessionSeq\":").Append(SessionSeq.ToString(CultureInfo.InvariantCulture));
        }
    }
}
