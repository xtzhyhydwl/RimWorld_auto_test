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
    /// ★★ **操作事件观察者** —— 本 mod 的**第二套系统**（与快照并列，互不影响）。
    ///
    /// ## 为什么需要它（用户 2026-10-03 原话）
    ///
    /// 用户：
    /// > 「画一幅那种**总体的地图**，记录的是**我们修改的部分**……每次都**确实要记录操作**。
    /// >  这个可能不是快照能解决的，他是要**记录操作**，就记录我**每一次进行建筑的操作**。
    /// >  然后在一张空白的地图上进行（画）……这个可能和那种定期截取快照是**另一套机制**，
    /// >  不过我希望放到**同一个 mod** 里……就像是那种**定期打印的数据**和**实时记录的操作**，
    /// >  一种是**事件驱动**，一种是以**时间来记录**的。」
    ///
    /// ⇒ 所以本文件与 `ColonyRecorder.cs` 是**两条并列的线**：
    /// - `ColonyRecorder`（时间驱动）：每 6000 tick 一行**全量快照** → `colony-<名>-<mapId>.jsonl`
    /// - `PlayerActionObserver`（事件驱动）：每 30 tick 做一次**状态差分**，只把**变的地方**写成事件
    ///   → `actions-<名>-<mapId>.jsonl`
    ///
    /// ⚠ 老快照格式**一个字段都没动**（分析端 `scripts/analyze-snapshots.mjs` 依赖它）。
    ///
    /// ## 机制：高频状态观察者（用户已选定；零 Harmony、零加载顺序依赖）
    ///
    /// 不去 hook 任何"玩家按下了什么"，而是**高频地读一遍世界状态**，
    /// 与上一次做差分 ⇒ 差出来的就是"玩家刚做的操作"。
    /// 优点：不需要 Harmony patch、不受 mod 加载顺序影响、任何来源的改动都能看到。
    /// 代价：两次轮询之间的"建了又立刻拆"看不见（30 tick 内极少见；这也是用户选定的取舍）。
    ///
    /// ## 怎么被挂到地图上（**不手动注册**）
    ///
    /// `Verse.Map.FillComponents()` 的 IL 是
    /// `foreach (t in typeof(MapComponent).AllSubclassesNonAbstract()) Activator.CreateInstance(t, this)`，
    /// 所以只要有一个 **单个 `Map` 参数的公有构造函数**，游戏就会自动 new 出来挂到 `map.components`。
    /// 本类**没有**任何静态注册代码 —— 与 `ColonyRecorder`（`GameComponent`）同理。
    ///
    /// ## 硬约束（本项目纪律）
    ///
    /// - **只读**：只读游戏对象，绝不改地图、绝不改任何游戏对象（不调任何 `Add*`/`Remove*`/`Set*`）。
    /// - **静默**：`MapComponentTick` 整个方法体包 try/catch，任何异常只 `Log.Warning` 吞掉。
    ///   录制器**绝不能影响游戏** —— 用户的存档体验比数据重要。
    /// - **C# 7.3**：不能用 switch 表达式 / `??=` / 范围索引（游戏运行时是旧 Mono）。
    ///
    /// ## 元数据确证（`tmp/research-action-members.ps1`，2026-10-03 本机实测）
    ///
    /// | 成员 | 可见性 |
    /// | --- | --- |
    /// | `Verse.DesignationManager.AllDesignations` | public 属性（`List<Designation>`）|
    /// | `Verse.Designation.def` / `.target` | **public 字段** |
    /// | `Verse.LocalTargetInfo.Cell` / `.HasThing` / `.Thing` | public 属性 |
    /// | `Verse.ZoneManager.AllZones` | public 属性（private 字段是 `allZones`）|
    /// | `Verse.Zone.ID` / `.label` / `.cells` | **public 字段**（另有 public 属性 `Cells` / `CellCount`）|
    /// | `Verse.StorageGroupManager.StorageGroupsForReading` | public 属性（私有字段 `groups` 读不到）|
    /// | `RimWorld.StorageGroup.loadID` / `.RenamableLabel` / `.MemberCount` / `.members` | public（前三个已在既有 Reader 里跑过）|
    /// | `RimWorld.BillStack.Bills` | public 属性（私有字段 `bills`）|
    /// | `RimWorld.Bill.recipe` / `.suspended` | **public 字段** |
    /// | `RimWorld.Bill_Production.targetCount` / `.paused` / `.repeatMode` / `.repeatCount` | **public 字段**（**不在 `Bill` 基类上**）|
    /// | `Verse.Thing.Position` / `.Rotation` / `.def` / `.Stuff` | public 属性 |
    /// | `Verse.ThingDef.entityDefToBuild` | **public 字段**（`BuildableDef`）|
    /// | `Verse.ThingDef.IsBlueprint` / `.IsFrame` | public 属性 |
    /// | `Verse.Map.terrainGrid` / `.Size` / `.uniqueID` / `.listerThings` | public 字段 |
    /// | `Verse.MapComponent.MapComponentTick` | public override 方法 |
    /// </summary>
    public sealed partial class PlayerActionObserver : MapComponent
    {
        // ══════════════════════════════════════════════════════════════
        // 开关与参数（静态，便于调试时改；与 ColonyRecorder 的风格一致）
        // ══════════════════════════════════════════════════════════════

        /// <summary>操作观察总开关（默认**开**）</summary>
        public static bool Enabled = true;

        /// <summary>
        /// 轮询间隔（游戏 tick）。默认 **30 tick = 0.5 游戏秒**（用户要"实时记录操作"）。
        ///
        /// ⚠ 采样间隔越大 → 越可能漏掉"建了又立刻拆"（两次轮询之间的净变化看不见）。
        ///   **不设下限**（用户明确说 30 可调）：只把 0 / 负数兜成 30，防手滑写出死循环式轮询。
        /// </summary>
        public static int IntervalTicks = 30;

        /// <summary>单条事件里 `cells` 最多记几个（超了就只给 `count` + `truncated:true`）</summary>
        private const int MaxCellsPerEvent = 400;

        /// <summary>一轮差分最多冒多少条事件（防一次超大划线把文件写爆）</summary>
        private const int MaxEventsPerTick = 60;

        /// <summary>一次差分里最多处理多少个**区域**的格子（超出的区域整块跳过并记警告）</summary>
        private const int MaxZonesPerTick = 400;

        // ══════════════════════════════════════════════════════════════
        // 持久状态（随 MapComponent 实例存在；一张地图一个实例）
        // ══════════════════════════════════════════════════════════════

        private string _file;
        private int _lastTick = int.MinValue;

        /// <summary>
        /// 本会话写盘是否**失败过**（磁盘满 / 文件被占）。
        ///
        /// ★ 失败一次就不再往同一个文件里试了（只记一次警告）—— 否则每 30 tick
        ///   都会抛一次异常 + 记一条日志，把 `Player.log` 刷爆（本项目纪律：
        ///   失败要留痕，但**一次就够**）。
        ///
        /// ★ 它是**本会话**的标记，不是"本进程"的：tick 倒退（换局 / 读档）会把它清掉
        ///   （新会话 = 新文件，失败原因很可能已经消失）。⚠ 老实现漏了这一步 ⇒
        ///   一次写失败（比如那一刻文件被占）会让**这一整局之后的每一局都不再写**。
        /// </summary>
        private bool _writeFailed;

        /// <summary>上一次的"意图状态"：key = `kind:identity`，value = 内容签名</summary>
        private Dictionary<string, string> _prev;

        /// <summary>上一次的格子占用视图（`x,z` → 种类）—— 用来发现"同一次操作的前后"</summary>
        private Dictionary<string, string> _prevCells;

        /// <summary>同一次操作的**提示信息**（定义名 / 材质 / 目标量…），只在变化时输出</summary>
        private Dictionary<string, string> _prevInfo;

        /// <summary>
        /// 已写过第 0 层"可建造性"的（run × 地图）。
        ///
        /// ⚠ key 用 `RunScope.KeyForMap(mapId)`（**带 runId**）而不是裸 `mapId`：
        ///   进程内 tick 倒退（读档/换图）时会清这个集合；而**同一个进程内**万一没清到，
        ///   带 runId 的 key 也保证"另一个 run 的文件不会被误判成已写过"。
        /// </summary>
        private static readonly HashSet<string> _buildabilityWritten = new HashSet<string>();

        /// <summary>本会话已写完的行数（诊断用）</summary>
        public static int WrittenLines;

        /// <summary>最近一次差分统计（诊断用）：本轮算出的事件条数</summary>
        public static int LastTickEvents;

        /// <summary>
        /// ★ **轮询序号** —— 每跑一轮 `Step` 自增（1, 2, 3 …）。
        ///
        /// 存在的理由是**跨文件对齐**：事件时间线（`events-*.jsonl`）由本类每轮末尾驱动
        /// （见 `MapComponentTick` 里对 `EventTimelineObserver.ObserveLettersFrom` 的调用），
        /// 两条线**共用同一个 `IntervalTicks` 节拍**，于是
        /// 「操作文件的 `pollSeq`」与「事件文件的 `pollSeq`」指的是**同一次轮询** ——
        /// 分析端不用去猜"这封信是在哪两次操作采样之间到的"。
        ///
        /// ⚠ 会话开始时从 0 起（tick 倒退 / 新的一局会重置），它**不是**全局唯一序号。
        /// </summary>
        public static int PollSeq;

        public PlayerActionObserver(Map map)
            : base(map)
        {
            // ⚠ 构造函数里**不做任何 IO**（游戏可能在没有存档目录的时机 new 它）：
            //   文件与目录都在第一次 tick 时惰性准备（见 EnsureFile）。
            //   注意：**不**在这里建目录 —— 目录由 `RunScope` 在首次写盘前惰性建
            //   （用户要求："不能因保存目录尚未就绪而永久失败"）。
            // ⚠ `: base(map)` 是**必须**的（`MapComponent` 没有无参构造函数，编译期就会报 CS7036）。
        }

        // ══════════════════════════════════════════════════════════════
        // 输出文件（与另外两条流**同一个 run 目录**；路径规则收口在 `RunScope.PathFor`）
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 殖民地名（**原始、未安全化**）—— 取法与快照流**同一段**逻辑
        /// （`SnapshotReaders.ReadGameDateAndIdentity`：`Settlement.Name → World.info.name`）。
        ///
        /// ⚠ 文件系统安全化**不在这里做**：统一由 `RunNaming.SafeSegment` 在
        ///   `RunScope.PathFor` 内部完成（"一件事只有一处实现"）。
        ///   老实现这里返回已安全化的名字，于是每个调用点都得记住"别再安全化一次" ——
        ///   这种"约定靠记性"的接口必被踩。
        /// </summary>
        private static string SafeColonyName(Map map)
        {
            string name = null;
            try
            {
                // 复用快照那边**同一段**取名字的逻辑（Settlement.Name → World.info.name）
                var snap = new ColonySnapshot();
                SnapshotReaders.ReadGameDateAndIdentity(map, snap);
                name = snap.ColonyName;
            }
            catch (Exception) { }

            // 兜底：World.info.name（其实上面已经覆盖，这里只防"快照那边改了实现"）
            try
            {
                if (string.IsNullOrEmpty(name) && Find.World != null && Find.World.info != null)
                    name = Find.World.info.name;
            }
            catch (Exception) { }

            return name ?? "";
        }

        /// <summary>
        /// **文件名** = `actions-<殖民地名>-<mapId>.jsonl`，落在**本进程的 run 目录**里
        /// （`foodcontrol-snapshots/runs/run-<runId>/`）。
        ///
        /// ⚠ 路径拼接**只有一处**（`RunScope.PathFor` → `RunNaming.PathFor`）：
        ///   老实现这里自己 `Path.Combine(dir, "actions-" + …)`，与快照流各写一份，
        ///   这一轮加了 run 目录层，再各写一份就一定会分裂
        ///   （快照进新目录、操作还在老目录）。
        /// </summary>
        private bool EnsureFile(Map map)
        {
            if (!string.IsNullOrEmpty(_file)) return true;
            if (_writeFailed) return false;                 // 本会话写盘已失败 → 不再试

            try
            {
                int mapId = RunScope.GetMapId(map);
                string safe = SafeColonyName(map);

                // ★ 唯一出口（run 目录 + 文件名；内部已做文件系统安全化）
                string file = RunScope.PathFor(RunNaming.ActionsPrefix, safe, mapId);
                if (string.IsNullOrEmpty(file))
                {
                    // 降级：这一轮不写（`RunScope` 已把原因与降级行为记过一次警告）
                    return false;
                }
                _file = file;

                bool existed = false;
                try { existed = File.Exists(_file); } catch (Exception) { }

                try
                {
                    Log.Message("[ColonyRecorder][操作] 本次会话**追加**到 " + _file
                        + (existed ? "（文件已存在，接着往里写）" : "（新文件）")
                        + "；runId=" + (RunScope.RunId ?? "(未就绪)")
                        + " sessionSeq=" + RunScope.SessionSeq
                        + "；轮询间隔 " + EffectiveInterval() + " tick"
                        + "（= " + (EffectiveInterval() / 60f).ToString("F2", CultureInfo.InvariantCulture) + " 游戏秒）");
                }
                catch (Exception) { }
                return true;
            }
            catch (Exception e)
            {
                try { Log.Warning("[ColonyRecorder][操作] 建立输出文件失败（已吞）：" + e.Message); } catch (Exception) { }
                return false;
            }
        }

        /// <summary>
        /// 实际使用的轮询间隔（0 / 负数 → 退回默认 30）。
        ///
        /// ⚠ **public**（原来 private）：事件时间线（`EventTimelineObserver`）的会话头要写
        ///   `intervalTicks` —— 它必须与"操作线"写的那个值**逐字相同**，
        ///   否则分析端会以为两条线的节拍不一样（同一个常量，只有一处定义）。
        /// </summary>
        public static int EffectiveInterval()
        {
            return IntervalTicks > 0 ? IntervalTicks : 30;
        }

        // ══════════════════════════════════════════════════════════════
        // 每 tick 的入口（整段吞异常）
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 高频轮询：每 `IntervalTicks` tick 跑一次差分。
        ///
        /// ★ **整个方法体包 try/catch，任何异常只 `Log.Warning` 吞掉** ——
        ///   本组件绝不能因为游戏数据异常而影响游戏。
        /// </summary>
        public override void MapComponentTick()
        {
            try
            {
                if (!Enabled) return;

                int tick = 0;
                try { tick = Find.TickManager != null ? Find.TickManager.TicksGame : 0; } catch (Exception) { }
                if (tick <= 0) return;

                int interval = EffectiveInterval();

                // ★ `first` 必须在"清基线"**之前**算出来。
                //   ⚠ 我第一版把这一行写在清基线**之后**，于是 `_lastTick` 已被重置成
                //     `int.MinValue` ⇒ `first` 恒为 true ⇒ **换局后永远走 baseline 分支**，
                //     基线再也建不起来（等于换局后整套差分静默失效）。
                //     这是"默认值刚好让条件恒真"的**第 N 次**同类 bug（见 ColonyRecorder.cs 里的注记）。
                bool first = (_lastTick == int.MinValue);

                // ★ 换局 / 读档会让 tick **倒退**（新地图从 ~0 开始）。
                //   不处理的话 `_lastTick` 停在上局，差分基线是"上一张地图的状态" ——
                //   第一次差分会把整张新地图当成"玩家刚建的东西"，刷出成千上万条假事件。
                //   所以：tick 倒退 ⇒ 当作新的一局，基线全部清掉、重新写第 0 层。
                if (_lastTick != int.MinValue && tick < _lastTick)
                {
                    _prev = null;
                    _prevInfo = null;
                    _prevCells = null;
                    _file = null;
                    _lastTick = int.MinValue;          // ★ 必须重置（见上面那段注记）
                    // ★ P2：写盘失败标记也要清 —— 它现在是**本会话**的标记（换局/读档 = 新会话、新文件），
                    //   与快照流 `ColonyRecorder` 的 tick 倒退分支一致。不清的话：本进程里只要有一条流
                    //   写失败过一次（磁盘满 / 文件被占），之后**每一局都再也不写** —— 连新会话的新文件也不写。
                    _writeFailed = false;
                    PollSeq = 0;                       // ★ 新会话 → 轮询序号重开（与事件线各自重开的规则一致）
                    // ★ 本 tick 就算"新会话的第一轮"（`first` 的**唯一**一处改写）：下面闸门处在
                    //   这一轮不早退，于是这次换局的开号**留在同一个 tick** 上 —— 才可能与快照线 /
                    //   事件线共用**同一个**会话号（`RunScope` 按 tick 节流，见闸门后面那一段注记）。
                    first = true;
                    try { _buildabilityWritten.Clear(); } catch (Exception) { }
                    try
                    {
                        Log.Message("[ColonyRecorder][操作] tick 倒退（→ " + tick + "），按新的一局重开基线"
                            + "（新会话文件：" + (RunScope.RunDir ?? "(run 目录未就绪)") + "）");
                    }
                    catch (Exception) { }
                }

                // ── ★ interval 闸门：没到间隔就这一轮什么都不做（**会话号也不再动**）──
                //
                //   ⚠ `tick - _lastTick` 的溢出陷阱与本段无关了：唯一会留下 `int.MinValue` 的
                //     tick 倒退分支现在把 `first` 置真 ⇒ 这一轮必然过闸门，不会去算那个会溢出的减法。
                if (!first && tick - _lastTick < interval) return;
                _lastTick = tick;

                // ★ **会话号从这里开**（三条流共用一本账，见 `RunScope.EnsureSessionForPoll`）：
                //   ① **位置：闸门之后**。老实现把这一行写在闸门**之前**，于是**每个 tick 都开一个新号**
                //      （`RunScope` 里的 `_beginCalledTick` 只挡得住"同一个 tick 的第二次调用"，
                //       挡不住"下一个 tick"）⇒ 一个会话内 `sessionSeq` 一路涨到几万，
                //      `sessionSeq` / `sessionId` 就失去了"换局边界"的意义（分析端按它切段会切碎）。
                //      现在只有**真的轮询了这一轮、且这一轮是本会话第一轮**（`first`）才开号
                //      ⇒ 与另外两条流的调用点语义逐字一致（快照线：首次采样 + tick 倒退各一次；
                //        事件线：`if (!st.SessionOpened)` 每会话一次）。
                //   ② **多地图同一轮只共享一个会话号**：每张地图一个 `MapComponent` 实例，各自有一份
                //      `_lastTick` ⇒ 它们的"第一轮"（进程首轮 / 同一次 tick 倒退）落在**同一个 tick** 上，
                //      而 `RunScope.EnsureSessionForPoll` 内部按 tick 节流 ⇒ 先到的开号、后到的直接复用，
                //      多张地图 + 快照线 + 事件线一共只消耗一个号。
                //   ③ 先过闸门再开号**不会漏号**：号只在"新会话第一轮"消耗，而新会话第一轮必然过闸门
                //      （`first` 为真时闸门不拦）；事件线/快照线照样各开各的（它们在同 tick 上只是复用）。
                if (first) RunScope.EnsureSessionForPoll(tick);

                // ★ 轮询序号（两条线共用的节拍 —— 见 `PollSeq` 的字段注释）
                PollSeq++;

                Step(tick, first);

                // ── ★ **第三套系统：游戏事件时间线**（信件到达）──
                //
                //   位置刻意放在这里（而不是 `Step` 里面）：
                //   ① 每轮**无论 `Step` 怎么写**都会跑 —— 事件线不能因为操作线建文件失败
                //      （`Step` 开头那一批 early return）就整段静默；两条线是**并列**的，不是串联。
                //   ② `PollSeq` 已在上面自增过 —— 两条线的轮询序号因此**指向同一次轮询**。
                //   ③ 整段自己吞异常（`ObserveLettersFrom` 内部 try/catch），
                //      即使它炸了也不会把操作线的这一轮带崩（外面还有一层 catch）。
                EventTimelineObserver.ObserveLettersFrom(map, tick);
            }
            catch (Exception e)
            {
                try { Log.Warning("[ColonyRecorder][操作] 轮询异常（已吞）：" + e.Message); } catch (Exception) { }
            }
        }

        /// <summary>一次完整的"读 → 差分 → 写"</summary>
        private void Step(int tick, bool firstOfSession)
        {
            try
            {
                Map map = this.map;
                if (map == null) return;
                if (!EnsureFile(map)) return;

                // ★ run/session 元数据：本轮的**每一行**都带（三个平级字段，追加在行尾）。
                //   成组取一次（`SessionMeta.Capture` 内部一次锁）—— 分三次读有可能拼出
                //   "sessionSeq=2 配 sessionId=<s0001>"这种自相矛盾的行。
                SessionMeta meta = SessionMeta.Capture();

                var sb = new StringBuilder(8192);
                int absTick = 0;
                try { absTick = Find.TickManager != null ? Find.TickManager.TicksAbs : 0; } catch (Exception) { }
                string wall = "";
                try { wall = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture); } catch (Exception) { }

                // ── ① 第 0 层"可建造性"：**首次见到这张地图时**写一次（静态、只算一次）──
                if (WriteBuildabilityOnce(map, sb, meta)) { }

                // ── ①-b **会话头**：每次"新的一局"（含读档）写一条 ──
                //   它说明"这个事件流是谁、哪张地图、轮询多密"，也是**事件流的起点标记**；
                //   分析端按它切割不同会话（一个文件里会跨很多次会话）。
                if (firstOfSession) WriteSessionLine(map, sb, tick, absTick, wall, meta);

                // ── ② 读当前"意图状态" ──
                var cur = new Dictionary<string, string>();
                var curInfo = new Dictionary<string, string>();
                var curCells = new Dictionary<string, string>();
                var warn = new List<string>();
                ReadDesignations(map, cur, curInfo, curCells, warn);
                ReadBuildThings(map, cur, curInfo, curCells, warn);
                ReadZones(map, cur, curInfo, curCells, warn);
                ReadBills(map, cur, curInfo, warn);
                ReadStorageGroups(map, cur, curInfo, warn);
                ReportWarnings(warn);

                if (firstOfSession || _prev == null)
                {
                    // ★ **首次不写"新增"**：否则会把这局已存在的成百上千个对象
                    //   全部当成"玩家刚建的"。只写一条 baseline 当**分界线**。
                    sb.Append("{\"kind\":\"baseline\",\"op\":\"init\"");
                    AppendHeader(sb, tick, absTick, wall);
                    sb.Append(",\"objects\":").Append(cur.Count);
                    sb.Append(",\"cells\":").Append(curCells.Count);
                    meta.AppendTo(sb);                        // ★ 行尾 run/session 元数据
                    sb.Append("}\n");
                }
                else
                {
                    Diff(sb, tick, absTick, wall, cur, curInfo, curCells, meta);
                }
                // ── ③ 落盘（一行一个事件；这一轮可能 0 条）──
                if (sb.Length > 0)
                {
                    try
                    {
                        File.AppendAllText(_file, sb.ToString());
                        WrittenLines++;
                    }
                    catch (Exception e)
                    {
                        // 写失败只警告一次（磁盘满 / 文件被占），绝不影响游戏
                        _writeFailed = true;
                        try
                        {
                            Log.Warning("[ColonyRecorder][操作] 写文件失败（已吞，本会话不再重试）：" + e.Message
                                + "；文件=" + _file);
                        }
                        catch (Exception) { }
                    }
                }

                _prev = cur;
                _prevInfo = curInfo;
                _prevCells = curCells;
            }
            catch (Exception e)
            {
                try { Log.Warning("[ColonyRecorder][操作] 差分失败（已吞）：" + e.Message); } catch (Exception) { }
            }
        }

        /// <summary>
        /// **会话头**（`kind:"session"`）—— 每次新的一局（含读档）写一条。
        ///
        /// ```json
        /// {"kind":"session","tick":…,"absTick":…,"colonyName":"…","mapId":0,"intervalTicks":30,"wallClock":"…",
        ///  "pollSeq":1,"runId":"20261004-113045-123-4f2a","sessionId":"…-s0001","sessionSeq":1}
        /// ```
        /// 有了它，分析端不用猜"这个文件是从哪一局、哪张地图、多密的间隔开始记的"。
        ///
        /// ★ `runId` / `sessionId` / `sessionSeq` **追加在行尾**（与另外两条流逐字同形）——
        ///   "一个 run 目录里的文件可以跨多个 session"这件事**只能从这里看出来**，
        ///   因为用户要求文件名保持简洁（不带 sessionSeq）。
        /// </summary>
        private void WriteSessionLine(Map map, StringBuilder sb, int tick, int absTick, string wall,
            SessionMeta meta)
        {
            int mapId = RunScope.GetMapId(map);

            string colony = "";
            try
            {
                // 只用来取殖民地名 —— **复用**快照那边同一套取法（Settlement.Name → World.info.name）
                var snap = new ColonySnapshot();
                SnapshotReaders.ReadGameDateAndIdentity(map, snap);
                colony = snap.ColonyName ?? "";
            }
            catch (Exception) { }

            // 身份读不到也照样写（colonyName 空）—— 会话头是"事件流从哪开始"的标记，不能丢
            try
            {
                sb.Append("{\"kind\":\"session\"");
                sb.Append(",\"tick\":").Append(tick);
                sb.Append(",\"absTick\":").Append(absTick);
                sb.Append(",\"colonyName\":").Append(JsonString(colony));
                sb.Append(",\"mapId\":").Append(mapId);
                sb.Append(",\"intervalTicks\":").Append(EffectiveInterval());
                sb.Append(",\"wallClock\":").Append(JsonString(wall));
                sb.Append(",\"pollSeq\":").Append(PollSeq);
                if (meta != null) meta.AppendTo(sb);       // ★ 行尾 run/session 元数据
                sb.Append("}\n");
            }
            catch (Exception) { }
        }

        // ══════════════════════════════════════════════════════════════
        // ① 第 0 层：可建造性（静态，只算一次）
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 写**第 0 层"可建造性"** —— 用户要的"先建一张没有多余信息的图"：
        /// > 「这个虚拟的地图**忽略一切环境因素**，或者说它**只保留关键的环境因素**
        /// >  ——比如水或者沼泽地，就是"能在上面建造建筑和不能建造"的那种地块。」
        ///
        /// ## 格式（**按行 RLE**，绝不逐格一行）
        ///
        /// ```json
        /// {"kind":"buildability","mapId":0,"w":250,"h":250,
        ///  "terrainLegend":["Slate","WaterDeep",...],
        ///  "rows":[[x,z,len,legendIndex],...]}
        /// ```
        /// - `terrainLegend[legendIndex]` = **terrain 的 defName 原样**（可建造/不可建造的分辨信息
        ///   至少在 legend 里体现：给分析端自己判水/沼泽/岩石，不在这里替它下结论）
        /// - 每个 `rows` 元素 = 从 `(x,z)` 开始、连续 `len` 格都是 `legendIndex` 这一种地形
        /// - 地图尺寸同时写 `w`/`h`（分析端不用去数 rows）
        ///
        /// ⚠ 只读 `map.terrainGrid.TerrainAt`（public 方法，既有 Reader 已在用）。
        ///
        /// ★ 去重 key = `RunScope.KeyForMap(mapId)`（**带 runId**，见那边的注记）——
        ///   老实现用裸 `mapId`，跨 run 会误判"已经写过"。
        /// </summary>
        private bool WriteBuildabilityOnce(Map map, StringBuilder sb, SessionMeta meta)
        {
            int mapId = RunScope.GetMapId(map);
            string dedupeKey = RunScope.KeyForMap(mapId);
            if (_buildabilityWritten.Contains(dedupeKey)) return false;

            // 标记"已写过"要放在成功之后 —— 不然一次异常会让这张图永远没有第 0 层
            try
            {
                TerrainGrid grid = map.terrainGrid;
                if (grid == null) return false;

                int w = map.Size.x;
                int h = map.Size.z;
                if (w <= 0 || h <= 0) return false;

                var legend = new List<string>();
                var legendIndex = new Dictionary<string, int>();
                var runs = new List<int[]>();               // [x, z, len, legendIndex]

                for (int z = 0; z < h; z++)
                {
                    int x = 0;
                    while (x < w)
                    {
                        TerrainDef td = null;
                        try { td = grid.TerrainAt(new IntVec3(x, 0, z)); } catch (Exception) { }
                        string key = (td != null && !string.IsNullOrEmpty(td.defName)) ? td.defName : "(unknown)";

                        int idx;
                        if (!legendIndex.TryGetValue(key, out idx))
                        {
                            idx = legend.Count;
                            legend.Add(key);
                            legendIndex[key] = idx;
                        }

                        int len = 1;
                        while (x + len < w)
                        {
                            TerrainDef nx = null;
                            try { nx = grid.TerrainAt(new IntVec3(x + len, 0, z)); } catch (Exception) { }
                            string nk = (nx != null && !string.IsNullOrEmpty(nx.defName)) ? nx.defName : "(unknown)";
                            if (nk != key) break;
                            len++;
                        }

                        runs.Add(new int[] { x, z, len, idx });
                        x += len;
                    }
                }

                sb.Append("{\"kind\":\"buildability\"");
                sb.Append(",\"mapId\":").Append(mapId);
                sb.Append(",\"w\":").Append(w);
                sb.Append(",\"h\":").Append(h);
                sb.Append(",\"terrainLegend\":[");
                for (int i = 0; i < legend.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(JsonString(legend[i]));
                }
                sb.Append("],\"rows\":[");
                for (int i = 0; i < runs.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    int[] r = runs[i];
                    sb.Append('[').Append(r[0]).Append(',').Append(r[1]).Append(',')
                      .Append(r[2]).Append(',').Append(r[3]).Append(']');
                }
                sb.Append("]");
                if (meta != null) meta.AppendTo(sb);         // ★ 行尾 run/session 元数据
                sb.Append("}\n");

                _buildabilityWritten.Add(dedupeKey);

                try
                {
                    Log.Message("[ColonyRecorder][操作] 已写第 0 层可建造性：mapId=" + mapId
                        + " " + w + "x" + h + "，地形种类 " + legend.Count
                        + "，RLE 行段 " + runs.Count + " 条");
                }
                catch (Exception) { }
                return true;
            }
            catch (Exception e)
            {
                try { Log.Warning("[ColonyRecorder][操作] 可建造性层失败（已吞，下次再试）：" + e.Message); } catch (Exception) { }
                return false;
            }
        }

        // ══════════════════════════════════════════════════════════════
        // ② 五个观察面：都只**读**，把结果写进 cur / curInfo / curCells
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// **地图标记**（`designation`）—— 采矿 / 拆除 / 收割 / 搬运标记…
        ///
        /// 身份 = `def.defName + "@" + 目标格`（或 `"@thing:" + thingID`）；
        /// 内容 = `0/1`（无内容可变的标记，存在即状态）。
        /// 差分 → `add` / `remove`（用户按了"采矿"和取消采矿就会各出一条）。
        ///
        /// ## 事件行（与 blueprint/frame 同一套字段形状）
        ///
        /// ```json
        /// {"kind":"designation","op":"add","tick":123,"absTick":670123,"day":0,
        ///  "wallClock":"22:10:03","def":"Haul","cells":[[121,124]],"count":1}
        /// ```
        /// - `def` **一定是 JSON 字符串**（转义过）；物体型标记另给 `thingId`，
        ///   这时 `cells:[]` / `count:0`
        ///
        /// ⚠⚠ **这里曾经写出非法 JSON（本文件最重要的一个坑）**：
        ///   原先只写了一行 `info[key] = defName;`（**裸字符串** `Haul`），
        ///   而 `WriteEntityEvent` 把 `info` 当成**已经拼好的 JSON 片段**直接拼接
        ///   ⇒ designation 行变成 `{…,"wallClock":"22:10:03",Haul}`：
        ///   `JSON.parse` 直接抛（"Unexpected token 'H'"），**整行/整个文件都废**。
        ///   ⇒ **不变量：`info[...]` 里放的东西必须是合法 JSON 片段**（一律走 `Encode*`），
        ///   不变量本身由 `AppendInfoFragment` 兜底（兜底时会一次性警告点名，不静默）。
        /// </summary>
        private static void ReadDesignations(Map map, Dictionary<string, string> cur,
            Dictionary<string, string> info, Dictionary<string, string> cells, List<string> warn)
        {
            DesignationManager dm = null;
            try { dm = map.designationManager; } catch (Exception) { }
            if (dm == null) { warn.Add("designationManager 为 null"); return; }

            List<Designation> all = null;
            try { all = dm.AllDesignations; } catch (Exception e) { warn.Add("AllDesignations 失败：" + e.Message); }
            if (all == null) return;

            for (int i = 0; i < all.Count; i++)
            {
                Designation d = all[i];
                try
                {
                    if (d == null || d.def == null) continue;
                    string defName = d.def.defName;
                    string loc;
                    string cellKey = null;

                    // ★ 事件行要用的东西在这里一起取出来（**不再是只存一个 defName**）
                    bool hasCell = false;
                    int cx = 0, cz = 0;
                    int thingId = -1;

                    try
                    {
                        if (d.target.HasThing)
                        {
                            Thing t = d.target.Thing;
                            // ⚠ 物体型标记（很少见）：用 thingIDNumber 当身份 ——
                            //   它能稳定区分"同一个目标"与"另一个目标"。
                            if (t != null)
                            {
                                thingId = t.thingIDNumber;
                                loc = "@thing:" + thingId.ToString(CultureInfo.InvariantCulture);
                            }
                            else
                            {
                                loc = "@thing:?";
                            }
                        }
                        else
                        {
                            IntVec3 c = d.target.Cell;
                            loc = "@" + c.x + "," + c.z;
                            cellKey = CellKey(c.x, c.z);
                            hasCell = true;
                            cx = c.x;
                            cz = c.z;
                        }
                    }
                    catch (Exception) { continue; }

                    string key = "designation:" + defName + loc;
                    cur[key] = "1";
                    // ⚠⚠ **曾经是 `info[key] = defName;`（裸字符串）→ 整行非法 JSON。**
                    //   现在一律交给编码器：info 必须是**已拼好、已转义**的 JSON 片段。
                    info[key] = EncodeDesignationInfo(defName, hasCell, cx, cz, thingId);
                    if (cellKey != null) cells[cellKey] = "designation:" + defName;
                }
                catch (Exception) { }
            }
        }

        /// <summary>
        /// **蓝图 + 框架**（`blueprint` / `frame`）—— 也就是"**玩家下的建造指令**"：
        ///
        /// - 蓝图出现 = 你刚下了建造指令（`def` = 要建的东西，`stuff` = 材质，`cells` = 位置）
        /// - 蓝图 → 框架 = **开工**（材料已送到）
        /// - 框架 → 成品建筑 = **建成**
        /// - 蓝图/框架消失且原地没有成品 = **取消**（或拆掉）
        ///
        /// ⚠ 这里**只收 `Blueprint` / `Frame`**（用户明确说的两个面）；
        ///   成品建筑由"快照那套"负责（`things` 面 + `materials` 面），本观察者不重复记，
        ///   但**会**记进 `_prevCells` 用来判"建成了"这个 transition。
        ///
        /// ★ "别让分析端分不清'取消了'和'建成了'" —— 靠**两样东西**一起保证：
        ///   ① 蓝图/框架的 `add`/`remove` 事件（谁没了）
        ///   ② 单独一条 `transition` 事件（那个格子上**前后分别是什么**）
        /// </summary>
        private static void ReadBuildThings(Map map, Dictionary<string, string> cur,
            Dictionary<string, string> info, Dictionary<string, string> cells, List<string> warn)
        {
            List<Thing> all = null;
            try { all = map.listerThings.AllThings; } catch (Exception e) { warn.Add("AllThings 失败：" + e.Message); }
            if (all == null) return;

            for (int i = 0; i < all.Count; i++)
            {
                Thing t = all[i];
                try
                {
                    if (t == null || t.def == null) continue;

                    // ★ 是不是蓝图 / 框架 —— 用 **`ThingDef` 上的两个 public 属性**判定
                    //   （`ThingDef.IsBlueprint` / `.IsFrame`，元数据实测）；
                    //   ⚠ **不是** `t.IsBlueprint`（`Thing` 上没有这个成员，CS1061 实测）。
                    bool isBp = false;
                    bool isFr = false;
                    try { isBp = t.def.IsBlueprint; } catch (Exception) { }
                    try { isFr = t.def.IsFrame; } catch (Exception) { }

                    // 先记"这一格现在是什么"（含成品建筑）—— transition 要靠它
                    IntVec3 pos = t.Position;
                    string ck = CellKey(pos.x, pos.z);
                    string kindStr = null;
                    if (isBp) kindStr = "blueprint:" + t.def.defName;
                    else if (isFr) kindStr = "frame:" + t.def.defName;
                    else if (t.def.category == ThingCategory.Building) kindStr = "building:" + t.def.defName;
                    if (kindStr != null)
                    {
                        // ⚠ 一格上可能同时有**多栋**建筑（墙 + 挂在上面的东西 / 门 + …），
                        //   所以这里**追加**而不是覆盖 —— 覆盖会让 transition 认错"前后"。
                        AppendCellKind(cells, ck, kindStr);
                    }

                    if (!isBp && !isFr) continue;

                    // ★ 要建的东西 + 材质：
                    //   - `t.def.entityDefToBuild` 是"**要建的东西**"（`Blueprint_Wall` → `Wall`），
                    //     **public 字段**（`BuildableDef`，元数据实测）
                    //   - `t.Stuff` 就是玩家选的材料（`Thing.Stuff`，public 属性）
                    //
                    // ⚠⚠ **实测踩到的坑（第三次同类，必须留档）**：
                    //   凭直觉写 `t is Blueprint` / `t.IsBlueprint` —— **两个都编译不过**：
                    //   ① `Blueprint` / `Frame` / `Blueprint_Build` 在 **global 命名空间**，
                    //      **不在 `Verse` 里**（写 `Verse.Blueprint` 直接 CS0234）；
                    //   ② `Thing` 上**根本没有** `IsBlueprint` / `IsFrame`（也不是扩展方法，CS1061）。
                    //   真身是 **`ThingDef.IsBlueprint` / `ThingDef.IsFrame`**（public 属性）。
                    //   既有 `ThingsReader` 里那句 `t is Blueprint` 能编过，是因为它写的
                    //   **不带命名空间前缀**，正好落到 global 命名空间那个类型上。
                    //   ⇒ 教训再次成立：**API 名字/位置必须先用元数据或编译确证，别凭直觉。**
                    ThingDef targetDef = t.def;
                    try
                    {
                        BuildableDef bd = t.def.entityDefToBuild;
                        ThingDef asThing = bd as ThingDef;
                        if (asThing != null) targetDef = asThing;
                    }
                    catch (Exception) { }

                    ThingDef stuffDef = null;
                    try { stuffDef = t.Stuff; } catch (Exception) { }
                    string stuffName = (stuffDef != null && !string.IsNullOrEmpty(stuffDef.defName)) ? stuffDef.defName : "";

                    int rot = 0;
                    try { rot = t.Rotation.AsInt; } catch (Exception) { }

                    string kind = isBp ? "blueprint" : "frame";
                    string key = kind + ":" + targetDef.defName + "|" + stuffName + "|" + ck;
                    cur[key] = rot.ToString(CultureInfo.InvariantCulture);
                    info[key] = EncodeInfo(targetDef.defName, stuffName, pos.x, pos.z, rot, t.def.defName);
                }
                catch (Exception) { }
            }
        }

        /// <summary>
        /// **区域**（`zone`）—— 划区域 / 改形状 / 删区域。
        ///
        /// 身份 = `区域类型 + "#" + ID`（`Zone.ID` 是稳定 id）；
        /// 内容 = `label + "|" + 格子集合的哈希式签名`；
        /// 另外把每个格子记进 `cells`（`zone:类型#ID`）—— 改形状时用来算**格子增减**。
        ///
        /// ⚠ 只读 `z.cells`；**绝不**调 `AddCell`/`RemoveCell`（那会改地图）。
        /// </summary>
        private static void ReadZones(Map map, Dictionary<string, string> cur,
            Dictionary<string, string> info, Dictionary<string, string> cells, List<string> warn)
        {
            ZoneManager zm = null;
            try { zm = map.zoneManager; } catch (Exception) { }
            if (zm == null) { warn.Add("zoneManager 为 null"); return; }

            List<Zone> all = null;
            try { all = zm.AllZones; } catch (Exception e) { warn.Add("AllZones 失败：" + e.Message); }
            if (all == null) return;

            int handled = 0;
            for (int i = 0; i < all.Count; i++)
            {
                Zone z = all[i];
                try
                {
                    if (z == null) continue;
                    if (++handled > MaxZonesPerTick) { warn.Add("区域超过 " + MaxZonesPerTick + " 个，本轮剩余区域跳过"); break; }

                    string type = z.GetType().Name;
                    int id = z.ID;
                    string label = "";
                    try { label = z.label ?? ""; } catch (Exception) { }

                    string zk = "zone:" + type + "#" + id;
                    var sig = new StringBuilder(64);
                    sig.Append(label).Append('|');

                    List<IntVec3> zcells = null;
                    try { zcells = z.cells; } catch (Exception) { }
                    int n = 0;
                    if (zcells != null)
                    {
                        for (int j = 0; j < zcells.Count; j++)
                        {
                            IntVec3 c = zcells[j];
                            AppendCellKind(cells, CellKey(c.x, c.z), zk);
                            // 签名只需要"格子集合变了没有" —— 用坐标和当指纹，避免拼出巨大字符串
                            sig.Append(c.x).Append('.').Append(c.z).Append(';');
                            n++;
                        }
                    }

                    cur[zk] = sig.ToString();
                    info[zk] = EncodeZoneInfo(type, id, label, n);
                }
                catch (Exception) { }
            }
        }

        /// <summary>
        /// **生产队列**（`bill`）—— 新增 / 删除 / 改目标量 / 暂停。
        ///
        /// 身份 = `工作台def@x,z + "#" + 这条 bill 的 loadID`；
        /// 内容 = `recipe|targetCount|repeatMode|repeatCount|suspended|paused`。
        ///
        /// ⚠ 元数据实测：`targetCount` / `paused` / `repeatMode` / `repeatCount`
        ///   **不在 `Bill` 基类上**，都在 `RimWorld.Bill_Production` 上（public 字段）。
        ///   既有 `BillsReader` 当年是被迫用反射去读它们的 —— 这里**用类型转换**，
        ///   因为转换是编译期检查的（比反射硬），读不到就退化。
        /// </summary>
        private static void ReadBills(Map map, Dictionary<string, string> cur,
            Dictionary<string, string> info, List<string> warn)
        {
            List<Thing> all = null;
            try { all = map.listerThings.AllThings; } catch (Exception e) { warn.Add("AllThings(bill) 失败：" + e.Message); }
            if (all == null) return;

            int diagLogged = 0;
            for (int i = 0; i < all.Count; i++)
            {
                Thing t = all[i];
                try
                {
                    if (t == null || t.def == null) continue;
                    IBillGiver giver = t as IBillGiver;
                    if (giver == null) continue;
                    BillStack stack = null;
                    try { stack = giver.BillStack; } catch (Exception) { }
                    if (stack == null) continue;
                    List<Bill> bills = null;
                    try { bills = stack.Bills; } catch (Exception) { }
                    if (bills == null) continue;

                    for (int j = 0; j < bills.Count; j++)
                    {
                        Bill b = bills[j];
                        if (b == null) continue;

                        int benchX = 0, benchZ = 0;
                        try { benchX = t.Position.x; benchZ = t.Position.z; } catch (Exception) { }

                        // bill 的稳定 id：`Bill.loadID` 是 **private 字段**（元数据实测），
                        // 但公有 `GetUniqueLoadID()` 就是 "loadID" 的字符串形式 —— 优先用它。
                        string billId = null;
                        try { billId = b.GetUniqueLoadID(); } catch (Exception) { }
                        if (string.IsNullOrEmpty(billId))
                        {
                            billId = "idx" + j;
                            if (diagLogged < 3)
                            {
                                diagLogged++;
                                warn.Add("bill 拿不到 GetUniqueLoadID，退化用栈内序号（换局/重排后可能对不上）");
                            }
                        }

                        string recipe = "";
                        try { recipe = b.recipe != null ? b.recipe.defName : ""; } catch (Exception) { }
                        string suspended = "0";
                        try { suspended = b.suspended ? "1" : "0"; } catch (Exception) { }

                        // ★ 只有 `Bill_Production` 才有目标量 / 暂停位 / 重复模式
                        string target = "";
                        string paused = "";
                        string repeatMode = "";
                        string repeatCount = "";
                        Bill_Production bp = b as Bill_Production;
                        if (bp != null)
                        {
                            try { target = bp.targetCount.ToString(CultureInfo.InvariantCulture); } catch (Exception) { }
                            try { paused = bp.paused ? "1" : "0"; } catch (Exception) { }
                            try { repeatMode = bp.repeatMode != null ? bp.repeatMode.defName : ""; } catch (Exception) { }
                            try { repeatCount = bp.repeatCount.ToString(CultureInfo.InvariantCulture); } catch (Exception) { }
                        }
                        string bench = t.def.defName;
                        string key = "bill:" + bench + "@" + benchX + "," + benchZ + "#" + billId;

                        cur[key] = recipe + "|" + target + "|" + repeatMode + "|" + repeatCount
                                 + "|" + suspended + "|" + paused;
                        info[key] = EncodeBillInfo(bench, benchX, benchZ, recipe, target, repeatMode, repeatCount, suspended, paused);
                    }
                }
                catch (Exception) { }
            }
        }

        /// <summary>
        /// **存储组**（`storageGroup`）—— 链接存储单位 / 改组名 / 成员增减。
        ///
        /// 身份 = `loadID`；内容 = `label|成员数|成员类型清单`。
        ///
        /// ⚠ `StorageGroupManager.StorageGroupsForReading`（public 属性）是**唯一**入口
        ///   （私有字段 `groups` 读不到，也没必要反射）。
        /// </summary>
        private static void ReadStorageGroups(Map map, Dictionary<string, string> cur,
            Dictionary<string, string> info, List<string> warn)
        {
            StorageGroupManager mgr = null;
            try { mgr = map.storageGroups; } catch (Exception) { }
            if (mgr == null) { warn.Add("map.storageGroups 为 null"); return; }

            List<StorageGroup> all = null;
            try { all = mgr.StorageGroupsForReading; } catch (Exception e) { warn.Add("StorageGroupsForReading 失败：" + e.Message); }
            if (all == null) return;

            for (int i = 0; i < all.Count; i++)
            {
                StorageGroup g = all[i];
                try
                {
                    if (g == null) continue;
                    int loadId = g.loadID;
                    string label = "";
                    try { label = g.RenamableLabel ?? ""; } catch (Exception) { }
                    int members = 0;
                    try { members = g.MemberCount; } catch (Exception) { }

                    var kinds = new StringBuilder(32);
                    try
                    {
                        List<IStorageGroupMember> ms = g.members;
                        if (ms != null)
                        {
                            for (int j = 0; j < ms.Count && j < 40; j++)
                            {
                                if (ms[j] == null) continue;
                                if (kinds.Length > 0) kinds.Append(',');
                                kinds.Append(ms[j].GetType().Name);
                            }
                        }
                    }
                    catch (Exception) { }

                    string key = "storageGroup:" + loadId;
                    cur[key] = label + "|" + members + "|" + kinds.ToString();
                    info[key] = EncodeStorageInfo(loadId, label, members, kinds.ToString());
                }
                catch (Exception) { }
            }
        }

        // ══════════════════════════════════════════════════════════════
        // ③ 差分 → 事件行
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 把"上一次"和"这一次"的意图状态做差，把**差**写成事件行。
        ///
        /// 有序三块：
        /// ① 蓝图/框架/区域/标记/存储组/生产队列的 `add` / `remove` / `change`
        /// ② 区域格子的 `cells+` / `cells-`（划区域、改形状）
        /// ③ `transition`（同一格上"蓝图 → 框架 → 成品"的前后）
        /// </summary>
        private void Diff(StringBuilder sb, int tick, int absTick, string wall,
            Dictionary<string, string> cur, Dictionary<string, string> curInfo,
            Dictionary<string, string> curCells, SessionMeta meta)
        {
            int emitted = 0;

            // ── ① 对象级差分 ──
            foreach (KeyValuePair<string, string> kv in cur)
            {
                if (emitted >= MaxEventsPerTick) break;
                string key = kv.Key;
                string now = kv.Value;

                string before;
                if (_prev.TryGetValue(key, out before))
                {
                    if (before == now) continue;                 // 没变 → 不发事件（这是"事件流"的关键）
                    string inf;
                    if (!curInfo.TryGetValue(key, out inf)) continue;
                    WriteEntityEvent(sb, tick, absTick, wall, key, "change", inf, now, meta);
                    emitted++;
                }
                else
                {
                    string inf;
                    if (!curInfo.TryGetValue(key, out inf)) continue;
                    WriteEntityEvent(sb, tick, absTick, wall, key, "add", inf, now, meta);
                    emitted++;
                }
            }

            foreach (KeyValuePair<string, string> kv in _prev)
            {
                if (emitted >= MaxEventsPerTick) break;
                if (cur.ContainsKey(kv.Key)) continue;
                string inf;
                if (!_prevInfo.TryGetValue(kv.Key, out inf))
                {
                    if (!curInfo.TryGetValue(kv.Key, out inf)) inf = "";
                }
                WriteEntityEvent(sb, tick, absTick, wall, kv.Key, "remove", inf, kv.Value, meta);
                emitted++;
            }

            // ── ② 区域格子增减（划区域 / 改形状：一条事件只带变化的那些格）──
            if (_prevCells != null && emitted < MaxEventsPerTick)
            {
                var zoneAdd = new Dictionary<string, List<int[]>>();
                var zoneDel = new Dictionary<string, List<int[]>>();

                foreach (KeyValuePair<string, string> kv in curCells)
                {
                    string cellKey = kv.Key;
                    string nowKind = kv.Value;
                    if (!nowKind.StartsWith("zone:", StringComparison.Ordinal)) continue;

                    string prevKind;
                    if (_prevCells.TryGetValue(cellKey, out prevKind) && prevKind == nowKind) continue;

                    int[] xz = DecodeCellKey(cellKey);
                    if (xz == null) continue;
                    AddCell(zoneAdd, nowKind, xz);
                }

                foreach (KeyValuePair<string, string> kv in _prevCells)
                {
                    string cellKey = kv.Key;
                    string prevKind = kv.Value;
                    if (!prevKind.StartsWith("zone:", StringComparison.Ordinal)) continue;

                    string nowKind;
                    if (curCells.TryGetValue(cellKey, out nowKind) && nowKind == prevKind) continue;

                    int[] xz = DecodeCellKey(cellKey);
                    if (xz == null) continue;
                    AddCell(zoneDel, prevKind, xz);
                }

                emitted += WriteZoneCellEvents(sb, tick, absTick, wall, "cells+", zoneAdd, curInfo, emitted, meta);
                emitted += WriteZoneCellEvents(sb, tick, absTick, wall, "cells-", zoneDel, curInfo, emitted, meta);
            }

            // ── ③ transition：同一格"蓝图 → 框架 → 成品"的前后 ──
            //
            // ⚠ 必须**两个方向**都扫（`curCells` 与 `_prevCells`）：
            //   只扫 `curCells` 的话，"**取消**"这一类（格子上的东西整个消失）
            //   永远不会被看见 —— 而那恰恰是"取消了建造指令"的唯一证据。
            //   （这个漏判是被离线逻辑自测抓出来的，见 `tmp/action-observer-logic-test.mjs` 的 B2。）
            if (_prevCells != null && emitted < MaxEventsPerTick)
            {
                // ③-a 现在还存在的格子：前后不同就报
                foreach (KeyValuePair<string, string> kv in curCells)
                {
                    if (emitted >= MaxEventsPerTick) break;
                    string cellKey = kv.Key;
                    string nextKind = kv.Value;

                    string prevKind;
                    if (!_prevCells.TryGetValue(cellKey, out prevKind)) continue;
                    if (prevKind == nextKind) continue;
                    if (!IsBuildChain(prevKind) && !IsBuildChain(nextKind)) continue;

                    int[] xz = DecodeCellKey(cellKey);
                    if (xz == null) continue;

                    EmitTransition(sb, tick, absTick, wall, prevKind, nextKind, xz, meta);
                    emitted++;
                }

                // ③-b 上一轮有、这一轮**整个没了**的格子：nextKind 留空（= 取消/被拆）
                foreach (KeyValuePair<string, string> kv in _prevCells)
                {
                    if (emitted >= MaxEventsPerTick) break;
                    string cellKey = kv.Key;
                    string prevKind = kv.Value;
                    if (curCells.ContainsKey(cellKey)) continue;
                    if (!IsBuildChain(prevKind)) continue;

                    int[] xz = DecodeCellKey(cellKey);
                    if (xz == null) continue;

                    EmitTransition(sb, tick, absTick, wall, prevKind, null, xz, meta);
                    emitted++;
                }
            }

            LastTickEvents = emitted;
        }

        /// <summary>
        /// 写一条 `transition` 事件。
        ///
        /// `nextKind` 为 null 表示**这一格现在什么都没有** —— 事件里就**不写** `nextKind` 字段
        /// （分析端看到"只有 prevKind 的 transition" = **取消了 / 被拆了**；
        ///  看到"prevKind=blueprint / nextKind=building" = **建成了**）。
        /// </summary>
        private void EmitTransition(StringBuilder sb, int tick, int absTick, string wall,
            string prevKind, string nextKind, int[] xz, SessionMeta meta)
        {
            sb.Append("{\"kind\":\"transition\",\"op\":\"change\"");
            AppendHeader(sb, tick, absTick, wall);
            sb.Append(",\"prevKind\":").Append(JsonString(prevKind));
            if (!string.IsNullOrEmpty(nextKind)) sb.Append(",\"nextKind\":").Append(JsonString(nextKind));
            sb.Append(",\"cells\":[[").Append(xz[0]).Append(',').Append(xz[1]).Append("]]");
            sb.Append(",\"count\":1");
            if (meta != null) meta.AppendTo(sb);            // ★ 行尾 run/session 元数据
            sb.Append("}\n");
        }

        private static bool IsBuildChain(string kind)
        {
            return kind != null
                && (kind.StartsWith("blueprint:", StringComparison.Ordinal)
                 || kind.StartsWith("frame:", StringComparison.Ordinal)
                 || kind.StartsWith("building:", StringComparison.Ordinal));
        }

        /// <summary>把一条对象级事件（add / remove / change）写进 sb</summary>
        private void WriteEntityEvent(StringBuilder sb, int tick, int absTick, string wall,
            string key, string op, string info, string sig, SessionMeta meta)
        {
            if (string.IsNullOrEmpty(info)) return;

            string kind;
            try
            {
                int p = key.IndexOf(':');
                kind = p > 0 ? key.Substring(0, p) : key;
            }
            catch (Exception) { kind = "unknown"; }

            if (kind == "designation")
            {
                sb.Append("{\"kind\":\"designation\",\"op\":").Append(JsonString(op));
                AppendHeader(sb, tick, absTick, wall);
                sb.Append(',');
                AppendInfoFragment(sb, info);                    // def/cells/count（thingId）由 info 自带
                if (meta != null) meta.AppendTo(sb);             // ★ 行尾 run/session 元数据
                sb.Append("}\n");
                return;
            }

            if (kind == "blueprint" || kind == "frame")
            {
                sb.Append("{\"kind\":").Append(JsonString(kind)).Append(",\"op\":").Append(JsonString(op));
                AppendHeader(sb, tick, absTick, wall);
                sb.Append(',');
                AppendInfoFragment(sb, info);
                if (meta != null) meta.AppendTo(sb);             // ★ 行尾 run/session 元数据
                sb.Append("}\n");
                return;
            }

            if (kind == "zone")
            {
                // 区域本体（新建 / 删除 / 改名）—— 格子增减由 `cells+`/`cells-` 负责
                sb.Append("{\"kind\":\"zone\",\"op\":").Append(JsonString(op));
                AppendHeader(sb, tick, absTick, wall);
                sb.Append(',');
                AppendInfoFragment(sb, info);
                if (meta != null) meta.AppendTo(sb);             // ★ 行尾 run/session 元数据
                sb.Append("}\n");
                return;
            }

            if (kind == "bill")
            {
                sb.Append("{\"kind\":\"bill\",\"op\":").Append(JsonString(op));
                AppendHeader(sb, tick, absTick, wall);
                sb.Append(',');
                AppendInfoFragment(sb, info);
                sb.Append(",\"sig\":").Append(JsonString(sig));
                if (meta != null) meta.AppendTo(sb);             // ★ 行尾 run/session 元数据
                sb.Append("}\n");
                return;
            }

            if (kind == "storageGroup")
            {
                sb.Append("{\"kind\":\"storageGroup\",\"op\":").Append(JsonString(op));
                AppendHeader(sb, tick, absTick, wall);
                sb.Append(',');
                AppendInfoFragment(sb, info);
                if (meta != null) meta.AppendTo(sb);             // ★ 行尾 run/session 元数据
                sb.Append("}\n");
                return;
            }

            // 兜底：未知 kind 也写出来（宁可多，不可丢 —— 但带上 key 便于排查）
            sb.Append("{\"kind\":\"other\",\"op\":").Append(JsonString(op));
            AppendHeader(sb, tick, absTick, wall);
            sb.Append(",\"key\":").Append(JsonString(key));
            sb.Append(",\"sig\":").Append(JsonString(sig));
            if (meta != null) meta.AppendTo(sb);                 // ★ 行尾 run/session 元数据
            sb.Append("}\n");
        }

        /// <summary>
        /// 把 `info`（各 `Encode*` 拼好的那段**JSON 字段片段**）接进事件行。
        ///
        /// ★ **不变量**：`info` 必须以 `"` 开头（= 一个合法的 `"字段":值,…` 片段）。
        ///
        /// ⚠ 为什么要有这个兜底：**designation 曾经就是在这里写出非法 JSON 的** ——
        ///   那时它塞进来的是**裸 defName**（`Haul`），拼出来是
        ///   `{"kind":"designation",…,"wallClock":"22:10:03",Haul}` ⇒ `JSON.parse` 抛异常，
        ///   **一行坏 = 分析端读到那一行就断**（用户 2026-10-04 报的正是这个）。
        ///   现在即使将来又有人手滑塞裸字符串：**整行仍然是合法 JSON**（降级成 `"def":"…"`），
        ///   并且**一次性警告点名**（不静默 —— 本项目的纪律：失败必须带原因）。
        /// </summary>
        private static void AppendInfoFragment(StringBuilder sb, string info)
        {
            if (!string.IsNullOrEmpty(info) && info[0] == '"')
            {
                sb.Append(info);
                return;
            }

            WarnNotJsonFragmentOnce(info);
            sb.Append("\"def\":").Append(JsonString(info));
        }

        /// <summary>`info` 不是 JSON 片段时**只警告一次**（避免每 30 tick 刷爆 Player.log）</summary>
        private static void WarnNotJsonFragmentOnce(string info)
        {
            const string key = "info-not-a-json-fragment";
            if (_warnedOnce.Contains(key)) return;
            _warnedOnce.Add(key);
            try
            {
                Log.Warning("[ColonyRecorder][操作] info 不是合法 JSON 片段（已降级为 \"def\":…，"
                    + "请修产生它的 Encode*）：" + info);
            }
            catch (Exception) { }
        }

        /// <summary>区域格子事件（`cells+` / `cells-`）：一条事件装同一个区域的**变化的格子**</summary>
        private int WriteZoneCellEvents(StringBuilder sb, int tick, int absTick, string wall,
            string op, Dictionary<string, List<int[]>> byZone, Dictionary<string, string> curInfo, int alreadyEmitted, SessionMeta meta)
        {
            int n = 0;
            foreach (KeyValuePair<string, List<int[]>> kv in byZone)
            {
                if (alreadyEmitted + n >= MaxEventsPerTick) break;

                string zk = kv.Key;                              // `zone:类型#ID`
                List<int[]> list = kv.Value;

                int id = 0;
                string type = zk;
                try
                {
                    string t = zk.Substring("zone:".Length);
                    int h = t.IndexOf('#');
                    if (h > 0)
                    {
                        type = t.Substring(0, h);
                        int.TryParse(t.Substring(h + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
                    }
                }
                catch (Exception) { }

                sb.Append("{\"kind\":\"zone\",\"op\":").Append(JsonString(op));
                AppendHeader(sb, tick, absTick, wall);
                sb.Append(",\"zoneType\":").Append(JsonString(type));
                sb.Append(",\"id\":").Append(id);

                // 顺手带上区域名（这些格子变化事件也要能看懂"是哪个区在变"）
                string inf;
                if (curInfo != null && curInfo.TryGetValue(zk, out inf))
                {
                    string label = ExtractJsonField(inf, "label");
                    if (!string.IsNullOrEmpty(label)) sb.Append(",\"label\":").Append(JsonString(label));
                }

                sb.Append(",\"cells\":[");
                // ⚠ 写出去的最多 **400** 个（`MaxCellsPerEvent`）——
                //   `AddCell` 会多留 1 个"哨兵"，所以这里**必须**再夹一次，
                //   否则会写出 401 个（这个 off-by-one 也是离线自测抓出来的）。
                int cap = list.Count < MaxCellsPerEvent ? list.Count : MaxCellsPerEvent;
                for (int i = 0; i < cap; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('[').Append(list[i][0]).Append(',').Append(list[i][1]).Append(']');
                }
                sb.Append("],\"count\":").Append(list.Count);
                // 单条事件最多 400 个格子：超了只给 count + truncated（否则一次大面积划线写爆文件）
                if (list.Count > MaxCellsPerEvent) sb.Append(",\"truncated\":true");
                if (meta != null) meta.AppendTo(sb);        // ★ 行尾 run/session 元数据
                sb.Append("}\n");
                n++;
            }
            return n;
        }

        /// <summary>从我们自己拼的 JSON 片段里取一个字符串字段（只用于内部补 label，不解析外部数据）</summary>
        private static string ExtractJsonField(string jsonFragment, string field)
        {
            try
            {
                string pat = "\"" + field + "\":\"";
                int i = jsonFragment.IndexOf(pat, StringComparison.Ordinal);
                if (i < 0) return null;
                i += pat.Length;
                int j = i;
                while (j < jsonFragment.Length && jsonFragment[j] != '"')
                {
                    if (jsonFragment[j] == '\\') j++;
                    j++;
                }
                return jsonFragment.Substring(i, j - i);
            }
            catch (Exception) { return null; }
        }

        // ══════════════════════════════════════════════════════════════
        // 小工具
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 每行事件都要带的公共字段（**字段名逐字固定**，分析端依赖）。
        ///
        /// ★ `pollSeq` 是**新增的最后一个字段**（2026-10-04）：它与
        ///   `events-*.jsonl`（`EventTimelineObserver`）里的同名字段指的是**同一次轮询**
        ///   —— 两条线共用 `IntervalTicks` 节拍，且事件线由本类每轮末尾驱动。
        ///   分析端因此能把"某个操作"与"某封信到达"对齐到同一拍，
        ///   不用去猜"这封信落在哪两次操作采样之间"。
        ///
        /// ⚠ 追加在**末尾**是刻意的：老字段的**名字、类型、相对顺序**一个都没动
        ///   （本项目硬约束：既有数据格式只能追加，不能改序）。
        ///
        /// ★ `tickSource` / `detectedTick` 与事件线（`events-*.jsonl`）**同名字、同语义**
        ///   —— 两条线都读游戏时钟，这两个字段让分析端能直接交叉核对
        ///   （同一个 `pollSeq` 上两边的 `detectedTick` 不一致 = 有一边读错了时钟）。
        ///
        /// ⚠⚠ **run/session 元数据不在这里**（2026-10-04 起）：`AppendHeader` 是**静态**的、
        ///   只负责"公共头"，而元数据由各行在其**行尾**（收尾 `}` 之前）用自己的
        ///   `SessionMeta.AppendTo(sb)` 追加 —— 因为不同 kind 的行在头之后**还有别的字段**，
        ///   在头里插元数据会把它挤到行中间（老解析器虽然按名字取值不会错，
        ///   但"元数据一律在行尾"这条规则被打破后，肉眼/`tail` 核对就不可靠了）。
        /// </summary>
        private static void AppendHeader(StringBuilder sb, int tick, int absTick, string wall)
        {
            sb.Append(",\"tick\":").Append(tick);
            sb.Append(",\"absTick\":").Append(absTick);
            sb.Append(",\"day\":").Append(tick / 60000);
            sb.Append(",\"wallClock\":").Append(JsonString(wall));
            sb.Append(",\"pollSeq\":").Append(PollSeq);
            sb.Append(",\"tickSource\":").Append(JsonString("game"));
            sb.Append(",\"detectedTick\":").Append(tick);
        }

        private static string CellKey(int x, int z)
        {
            return x.ToString(CultureInfo.InvariantCulture) + "," + z.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 往格子的"这一格有什么"里**追加**一种种类。
        ///
        /// 分隔符用 `|`：它既不可能是 `blueprint:` / `frame:` / `building:` / `zone:` 前缀的一部分，
        /// 也让"一格多物"的情况（墙 + 挂件 / 区域叠在地板上）能被分析端拆开看。
        ///
        /// ⚠ 去重必须看**每一段**，不能只比整串 —— 否则"同一格上第二栋同 def 建筑"
        ///   会写成 `building:Wall|building:Wall`：**签名无意义地变了**，
        ///   于是每轮都刷一条假 `transition`（这个 bug 是被离线逻辑自测抓出来的，
        ///   见 `tmp/action-observer-logic-test.mjs` 的 B1）。
        /// </summary>
        private static void AppendCellKind(Dictionary<string, string> cells, string cellKey, string kind)
        {
            if (cells == null || string.IsNullOrEmpty(cellKey) || string.IsNullOrEmpty(kind)) return;

            string existing;
            if (!cells.TryGetValue(cellKey, out existing) || string.IsNullOrEmpty(existing))
            {
                cells[cellKey] = kind;
                return;
            }

            // 逐段比（`a|b|c`）—— 段数极少（一格上不会有几十样东西）
            int from = 0;
            while (from <= existing.Length)
            {
                int bar = existing.IndexOf('|', from);
                string seg = (bar < 0) ? existing.Substring(from) : existing.Substring(from, bar - from);
                if (seg == kind) return;                       // 已经有了 → 幂等
                if (bar < 0) break;
                from = bar + 1;
            }

            cells[cellKey] = existing + "|" + kind;
        }

        /// <summary>
        /// 读不到某一块时**必须留痕**（本项目纪律：失败要带原因）。
        ///
        /// ⚠ 但**不能每 30 tick 刷一次日志** —— 那会把 Player.log 冲爆。
        ///   所以：同一条警告在**一个会话里只报一次**（用静态集合去重）。
        /// </summary>
        private static readonly HashSet<string> _warnedOnce = new HashSet<string>();

        private static void ReportWarnings(List<string> warn)
        {
            if (warn == null || warn.Count == 0) return;
            for (int i = 0; i < warn.Count; i++)
            {
                string w = warn[i];
                if (string.IsNullOrEmpty(w)) continue;
                if (_warnedOnce.Contains(w)) continue;
                _warnedOnce.Add(w);
                try { Log.Warning("[ColonyRecorder][操作] 读取警告（只报一次）：" + w); } catch (Exception) { }
            }
        }

        private static int[] DecodeCellKey(string key)
        {
            try
            {
                int p = key.IndexOf(',');
                if (p <= 0) return null;
                int x = int.Parse(key.Substring(0, p), CultureInfo.InvariantCulture);
                int z = int.Parse(key.Substring(p + 1), CultureInfo.InvariantCulture);
                return new int[] { x, z };
            }
            catch (Exception) { return null; }
        }

        private static void AddCell(Dictionary<string, List<int[]>> map, string key, int[] xz)
        {
            List<int[]> list;
            if (!map.TryGetValue(key, out list))
            {
                list = new List<int[]>();
                map[key] = list;
            }
            // ⚠ 只保留 `MaxCellsPerEvent + 1` 个：多出来的那一个用来让 `count` 如实
            //   反映"超过 400 了"（同时给 `truncated:true`），内存也不会因为一次全图划线爆掉。
            if (list.Count <= MaxCellsPerEvent) list.Add(xz);
        }

        /// <summary>`info` 编码：直接把该事件的**其余 JSON 字段**拼好存起来，输出时零解析。</summary>
        private static string EncodeInfo(string defName, string stuffName, int x, int z, int rot, string blueprintDef)
        {
            var sb = new StringBuilder(96);
            sb.Append("\"def\":").Append(JsonString(defName));
            // `stuff` **永远是字符串**（没材质就是空串）—— 字段恒定存在，分析端不用判空类型
            sb.Append(",\"stuff\":").Append(JsonString(stuffName));
            sb.Append(",\"cells\":[[").Append(x).Append(',').Append(z).Append("]]");
            sb.Append(",\"count\":1");
            sb.Append(",\"rot\":").Append(rot);
            // `bpDef` = 蓝图/框架自己的 def（`Blueprint_Wall` / `Frame_Wall`）——
            // 用来交叉核对 `def`（要建的东西）；两者不一致说明读法有问题
            sb.Append(",\"bpDef\":").Append(JsonString(blueprintDef));
            return sb.ToString();
        }

        /// <summary>
        /// `designation` 事件的 info 片段 —— **合法 JSON，`def` 是转义过的字符串**。
        ///
        /// ```json
        /// {"kind":"designation","op":"add","tick":1,"absTick":2,"day":0,
        ///  "wallClock":"22:10:03","def":"Haul","cells":[[121,124]],"count":1}
        /// ```
        /// - **格子型标记**（绝大多数）：`cells:[[x,z]]` + `count:1`
        /// - **物体型标记**（`target.HasThing`）：`cells:[]` + `count:0` + `thingId:N`
        ///   （`thingId` 读不到时省略该字段，其余字段形状不变）
        ///
        /// ★ 字段名与 blueprint/frame 事件**同名同形**（`def` / `cells` / `count`）——
        ///   分析端一套代码读所有事件；`def` 恒为字符串，不用担心"有时是字符串有时是裸字面量"。
        ///
        /// ⚠ 历史 bug 见 <see cref="ReadDesignations"/> 的 XML 注释：
        ///   这里以前不存在，info 里放的是裸 `defName` ⇒ designation 行**非法 JSON**。
        /// </summary>
        private static string EncodeDesignationInfo(string defName, bool hasCell, int x, int z, int thingId)
        {
            var sb = new StringBuilder(80);
            sb.Append("\"def\":").Append(JsonString(defName));
            if (hasCell)
            {
                // 格子型：与 blueprint/frame 完全同形
                sb.Append(",\"cells\":[[").Append(x).Append(',').Append(z).Append("]],\"count\":1");
            }
            else
            {
                // 物体型：没有格子（`cells` 仍是数组 —— 字段恒定存在，分析端不用判类型）
                sb.Append(",\"cells\":[],\"count\":0");
                if (thingId >= 0) sb.Append(",\"thingId\":").Append(thingId);
            }
            return sb.ToString();
        }

        private static string EncodeZoneInfo(string type, int id, string label, int cellCount)
        {
            var sb = new StringBuilder(96);
            sb.Append("\"zoneType\":").Append(JsonString(type));
            sb.Append(",\"id\":").Append(id);
            sb.Append(",\"label\":").Append(JsonString(label));
            sb.Append(",\"cellCount\":").Append(cellCount);
            return sb.ToString();
        }

        private static string EncodeBillInfo(string bench, int x, int z, string recipe,
            string target, string repeatMode, string repeatCount, string suspended, string paused)
        {
            var sb = new StringBuilder(160);
            sb.Append("\"bench\":").Append(JsonString(bench));
            sb.Append(",\"benchX\":").Append(x);
            sb.Append(",\"benchZ\":").Append(z);
            sb.Append(",\"recipe\":").Append(JsonString(recipe));
            sb.Append(",\"targetCount\":").Append(JsonString(target));
            sb.Append(",\"repeatMode\":").Append(JsonString(repeatMode));
            sb.Append(",\"repeatCount\":").Append(JsonString(repeatCount));
            sb.Append(",\"suspended\":").Append(suspended == "1" ? "true" : "false");
            sb.Append(",\"paused\":").Append(paused == "1" ? "true" : (paused == "0" ? "false" : "null"));
            return sb.ToString();
        }

        private static string EncodeStorageInfo(int loadId, string label, int members, string kinds)
        {
            var sb = new StringBuilder(96);
            sb.Append("\"loadId\":").Append(loadId);
            sb.Append(",\"label\":").Append(JsonString(label));
            sb.Append(",\"memberCount\":").Append(members);
            sb.Append(",\"memberKinds\":").Append(JsonString(kinds));
            return sb.ToString();
        }

        // ── 首轮要把 curInfo 交给差分阶段（用来补 zone label），用字段传一下 ──

        /// <summary>
        /// JSON 字符串转义。
        ///
        /// ⚠ 规则与老快照的 `ColonySnapshot.Quote` **逐条一致**（同为 JSONL，分析端同一套解析）。
        ///   这里**故意复制**一份（而不是把 `Quote` 改成 public）：
        ///   老快照那边一个字节都不动 —— 这是硬约束。
        /// </summary>
        internal static string JsonString(string s)
        {
            if (s == null) return "\"\"";
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
