using System;
using System.Collections.Generic;

namespace FoodControl.Game
{
    /// <summary>
    /// **一条"AI 打算做什么"的意图** —— 这是"让玩家/AI 知道 mod 要干什么"的**唯一数据形状**。
    ///
    /// ## 为什么需要它（用户 2026-10-03 原话）
    ///
    /// > 「局内再放一个窗口，可以把**AI 打算做什么事情**给打印出来，
    /// >   也能让玩家知道 AI 要做什么或者**即将要做什么**。」
    ///
    /// > 「虽然现在是你操作……但也可以做一个**进程间通信**，把内容发过去。」
    ///
    /// ## 设计立场：意图是**数据**，出口是**可插拔的**
    ///
    /// 这条记录**不知道**自己会被显示在哪里 —— 游戏窗口、日志、文件、HTTP、DSH 那边，
    /// 都只是它的**消费者**（见 `IIntentSink`）。所以"最后落在哪"是以后可以改的决定，
    /// 不需要改任何产生意图的代码。
    ///
    /// ## 生命周期（有状态，所以能被"追进度"）
    ///
    /// ```
    /// Planned ──→ Running ──→ Done
    ///    │            │
    ///    │            └──→ Failed
    ///    └──→ Vetoed（被玩家/框架否决 —— ★ 必须能说出为什么）
    /// ```
    ///
    /// 为什么要状态而不是"只记一条日志"：用户要的是知道"**即将**要做什么"，
    /// 那就必须有"还没做但打算做"的一栏 —— 纯日志只能事后知道。
    /// </summary>
    public sealed class AiIntent
    {
        /// <summary>全局自增序号（稳定、可跨进程引用同一条意图）</summary>
        public int Seq;

        /// <summary>产生时的游戏 tick</summary>
        public int Tick;

        /// <summary>游戏内第几天</summary>
        public int Day;

        /// <summary>谁打算做（域 id，如 "food" / "templatebuild"）</summary>
        public string DomainId = "";

        /// <summary>动作大类</summary>
        public IntentKind Kind = IntentKind.Other;

        /// <summary>人话摘要（一句话，给玩家看的那句）</summary>
        public string Summary = "";

        /// <summary>为什么（★ 项目铁律：任何判定都要能说出原因）</summary>
        public string Reason = "";

        /// <summary>作用目标（人话，如 "(119,119) 13×13 样板 selftest-box13"）</summary>
        public string Target = "";

        /// <summary>由谁触发：auto（日常调度）/ manual（测试钩子）/ player（按钮）</summary>
        public string Source = "auto";

        public IntentState State = IntentState.Planned;

        /// <summary>结果/失败原因（Done/Failed/Vetoed 时填）</summary>
        public string Result = "";

        /// <summary>状态最后变化的 tick（用于算"卡了多久"）</summary>
        public int UpdatedTick;

        public AiIntent Clone()
        {
            return new AiIntent
            {
                Seq = Seq,
                Tick = Tick,
                Day = Day,
                DomainId = DomainId,
                Kind = Kind,
                Summary = Summary,
                Reason = Reason,
                Target = Target,
                Source = Source,
                State = State,
                Result = Result,
                UpdatedTick = UpdatedTick,
            };
        }

        /// <summary>一行文本（日志/窗口共用，避免两处各写一套格式）</summary>
        public string ToLine()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("#").Append(Seq).Append(" [").Append(DomainId).Append("] ");
            sb.Append(StateText()).Append(' ');
            sb.Append(string.IsNullOrEmpty(Summary) ? Kind.ToString() : Summary);
            if (!string.IsNullOrEmpty(Target)) sb.Append(" @ ").Append(Target);
            if (!string.IsNullOrEmpty(Reason)) sb.Append(" —— ").Append(Reason);
            if (!string.IsNullOrEmpty(Result)) sb.Append("（").Append(Result).Append("）");
            return sb.ToString();
        }

        /// <summary>状态的**唯一**文字映射（别再在 UI 里写第二套）</summary>
        public string StateText()
        {
            switch (State)
            {
                case IntentState.Planned: return "打算";
                case IntentState.Running: return "正在";
                case IntentState.Done: return "完成";
                case IntentState.Failed: return "失败";
                case IntentState.Vetoed: return "被否决";
                default: return State.ToString();
            }
        }

        public override string ToString()
        {
            return ToLine();
        }
    }

    /// <summary>意图的生命周期状态。</summary>
    public enum IntentState
    {
        /// <summary>打算做（**"即将要做"就是这一栏**）</summary>
        Planned = 0,

        /// <summary>正在做</summary>
        Running = 1,

        /// <summary>做完了</summary>
        Done = 2,

        /// <summary>失败了（`Result` 里写原因）</summary>
        Failed = 3,

        /// <summary>被否决（AI 托管关着、或框架判定不该做 —— `Result` 里写为什么）</summary>
        Vetoed = 4,
    }

    /// <summary>
    /// **意图的动作大类**。
    ///
    /// 加一类动作 = 加一个枚举值（**不改任何流程**）：
    /// UI 会按 `ToString()` 显示，IPC 会按名字序列化，所以新值自动就能用。
    /// </summary>
    public enum IntentKind
    {
        Other = 0,

        // ---- 食物域 ----
        PlantCrop = 10,
        HarvestCrop = 11,
        Hunt = 12,

        // ---- 建造/住处域 ----
        BuildTemplate = 20,
        BuildStructure = 21,
        PlaceFurniture = 22,
        LayFloor = 23,

        // ---- 储藏域 ----
        CreateStockpile = 30,

        // ---- 开局清单 ----
        AllowItems = 40,
        BootstrapStep = 41,

        // ---- 威胁响应（将来）----
        Retreat = 50,
        Defend = 51,

        // ---- 框架自身 ----
        Veto = 90,
        Notify = 91,
    }
}
