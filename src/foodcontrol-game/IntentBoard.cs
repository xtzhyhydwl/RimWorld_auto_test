using System;
using System.Collections.Generic;
using System.Text;

namespace FoodControl.Game
{
    /// <summary>
    /// **意图看板** —— AI 打算做什么的**单一事实来源**（single source of truth）。
    ///
    /// 游戏窗口读它、IPC 发它、探针查它、遥测写它。**任何**关于"AI 要干什么"的问题
    /// 都从这里回答，不允许别处再拼一套（DRY）。
    ///
    /// ## 用户要的三件事分别落在哪
    ///
    /// | 用户要求 | 落在哪 |
    /// | --- | --- |
    /// | 「知道 AI 要做什么 / **即将**要做什么」 | `Planned` / `Running` 状态的意图（`Pending()`） |
    /// | 「局内窗口打印出来」 | `IntentWindow` 渲染 `LinesForUi()` |
    /// | 「进程间通信，把内容发过去」 | `Sinks`（`IIntentSink`，见 `IntentIpc.cs`） |
    ///
    /// ## 为什么要"看板"而不是"日志"
    ///
    /// 纯日志只能事后知道。用户要的是**"即将要做什么"**，那就必须有
    /// "还没做但打算做"这一栏 —— 这是一个**有状态**的东西，不是一个字符串流。
    ///
    /// ⚠ 本类**不引用游戏类型**：它只认 `AiIntent` 这些纯数据。
    ///   游戏类型（`Find.TickManager` 等）在 `IntentReporter` 里适配。
    ///   这样看板逻辑可以在没有游戏的情况下被测试/复用（也让将来搬去别处成为可能）。
    /// </summary>
    public static class IntentBoard
    {
        /// <summary>环形缓冲容量：够看"最近发生了什么"，也不会无限涨</summary>
        public static int Capacity = 200;

        private static readonly List<AiIntent> _all = new List<AiIntent>();
        private static readonly List<IIntentSink> _sinks = new List<IIntentSink>();
        private static int _nextSeq = 1;

        /// <summary>广播用：每次意图**新增**或**进入终态**时都会通知所有 sink</summary>
        public static void AddSink(IIntentSink sink)
        {
            if (sink == null) return;
            for (int i = 0; i < _sinks.Count; i++) if (_sinks[i] == sink) return;
            _sinks.Add(sink);
        }

        public static void RemoveSink(IIntentSink sink)
        {
            _sinks.Remove(sink);
        }

        public static IList<IIntentSink> Sinks
        {
            get { return _sinks.AsReadOnly(); }
        }

        /// <summary>全部记录（**只读快照**，按 seq 升序）</summary>
        public static List<AiIntent> All()
        {
            return new List<AiIntent>(_all);
        }

        /// <summary>
        /// **"即将要做"的那一栏** —— 还没到终态的意图（`Planned` + `Running`）。
        ///
        /// 窗口最上面显示的就是它：玩家一眼看到"AI 接下来要干什么"。
        /// </summary>
        public static List<AiIntent> Pending()
        {
            var outp = new List<AiIntent>();
            for (int i = 0; i < _all.Count; i++)
            {
                if (_all[i].State == IntentState.Planned || _all[i].State == IntentState.Running)
                    outp.Add(_all[i]);
            }
            return outp;
        }

        /// <summary>最近的记录（倒序，最新的在前）</summary>
        public static List<AiIntent> Recent(int n)
        {
            var outp = new List<AiIntent>();
            if (n <= 0) return outp;
            for (int i = _all.Count - 1; i >= 0 && outp.Count < n; i--) outp.Add(_all[i]);
            return outp;
        }

        /// <summary>按域分组统计（窗口顶部的"各域现状"）</summary>
        public static Dictionary<string, int> CountByDomain()
        {
            var map = new Dictionary<string, int>();
            for (int i = 0; i < _all.Count; i++)
            {
                string id = _all[i].DomainId ?? "";
                int n;
                map[id] = map.TryGetValue(id, out n) ? n + 1 : 1;
            }
            return map;
        }

        /// <summary>
        /// **登记一条意图**。返回带 seq 的那条（调用方之后拿它改状态）。
        ///
        /// 这是扩展点：将来任何新域/新功能要"告诉玩家我要干什么"，
        /// **只需要调这一个方法**，UI 与 IPC 自动就有了。
        /// </summary>
        public static AiIntent Add(AiIntent intent)
        {
            if (intent == null) return null;

            intent.Seq = _nextSeq++;
            if (intent.UpdatedTick == 0) intent.UpdatedTick = intent.Tick;
            if (string.IsNullOrEmpty(intent.DomainId)) intent.DomainId = "unknown";

            _all.Add(intent);
            Trim();
            Broadcast(intent, "add");
            return intent;
        }

        /// <summary>便捷：直接给字段（99% 的调用点用这个）</summary>
        public static AiIntent Report(
            string domainId, IntentKind kind, string summary, string reason,
            string target = "", IntentState state = IntentState.Planned, int tick = 0, int day = 0)
        {
            return Add(new AiIntent
            {
                DomainId = domainId ?? "unknown",
                Kind = kind,
                Summary = summary ?? "",
                Reason = reason ?? "",
                Target = target ?? "",
                State = state,
                Tick = tick,
                Day = day,
                UpdatedTick = tick,
            });
        }

        /// <summary>
        /// **改状态**。进终态（Done/Failed/Vetoed）时会广播一次。
        ///
        /// ⚠ `Vetoed` 必须带 `result`（为什么被否决）—— 项目铁律：
        ///   任何"没做成"的分支都要能说出原因。
        /// </summary>
        public static void SetState(AiIntent intent, IntentState state, string result = null, int tick = 0)
        {
            if (intent == null) return;

            intent.State = state;
            if (result != null) intent.Result = result;
            if (tick != 0) intent.UpdatedTick = tick;

            if (state == IntentState.Done || state == IntentState.Failed || state == IntentState.Vetoed)
                Broadcast(intent, "state");
        }

        /// <summary>清空（测试用；"换图检测不可靠"那条坑的应对手段）</summary>
        public static void Reset()
        {
            _all.Clear();
            _nextSeq = 1;
        }

        private static void Trim()
        {
            int cap = Capacity < 16 ? 16 : Capacity;
            while (_all.Count > cap) _all.RemoveAt(0);
        }

        private static void Broadcast(AiIntent intent, string change)
        {
            for (int i = 0; i < _sinks.Count; i++)
            {
                try { _sinks[i].OnIntent(intent, change); }
                catch (Exception)
                {
                    // ★ 一个 sink 炸了不能影响别的 sink，更不能影响游戏
                }
            }
        }

        /// <summary>
        /// **窗口要显示的文本行**（纯函数，可离线测试）。
        ///
        /// ⚠ 放在这里而不是 `IntentWindow` 里：这样"显示什么"能被测试，
        ///   而 UI 只剩"画出来"这件事（踩过的坑：把逻辑写进 OnGUI 就没法测）。
        /// </summary>
        public static List<string> LinesForUi(string takeoverLine, int maxPending = 8, int maxRecent = 12)
        {
            var lines = new List<string>();

            lines.Add(takeoverLine ?? "(托管状态未知)");
            lines.Add("");

            List<AiIntent> pending = Pending();
            lines.Add("── 即将要做（" + pending.Count + "）──");
            if (pending.Count == 0)
            {
                lines.Add("  （没有待办 —— mod 认为当前不需要动手）");
            }
            else
            {
                for (int i = 0; i < pending.Count && i < maxPending; i++)
                    lines.Add("  " + pending[i].ToLine());
                if (pending.Count > maxPending)
                    lines.Add("  …还有 " + (pending.Count - maxPending) + " 条（按开关看全部）");
            }

            lines.Add("");
            List<AiIntent> recent = Recent(maxRecent);
            lines.Add("── 最近发生（" + recent.Count + "）──");
            if (recent.Count == 0)
            {
                lines.Add("  （还没有任何记录）");
            }
            else
            {
                for (int i = 0; i < recent.Count; i++) lines.Add("  " + recent[i].ToLine());
            }

            return lines;
        }
    }

    /// <summary>
    /// **意图的出口**（sink）—— 每加一个消费方就实现一个，不改任何产生意图的代码。
    ///
    /// 已实现：日志（`LogIntentSink`）、JSONL 文件（`JsonlIntentSink`）。
    /// 预留：HTTP 推送 / 命名管道 / DSH 那边的接收端 —— **"最后落在哪"以后再说**，
    /// 但接口现在就是定的。
    /// </summary>
    public interface IIntentSink
    {
        /// <summary>人能读的名字（窗口里显示"发到哪儿了"）</summary>
        string Name { get; }

        /// <summary>
        /// 有意图变化时被调用。
        /// </summary>
        /// <param name="intent">那条意图（**不要改它**；要留就 `Clone()`）</param>
        /// <param name="change">"add"（新增）/"state"（进入终态）</param>
        void OnIntent(AiIntent intent, string change);
    }
}
