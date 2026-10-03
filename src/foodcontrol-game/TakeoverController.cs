using System;
using System.Collections.Generic;
using Verse;

namespace FoodControl.Game
{
    /// <summary>
    /// **AI 托管的总开关**（用户 2026-10-03 要求）。
    ///
    /// > 「局内可以做一个**是否让 AI 进行托管的一个小按钮**，
    /// >   这样我可以**灵活地开关**这个功能。」
    ///
    /// ## 为什么要有"模式"而不是一个 bool
    ///
    /// 只要一个 bool 就分不清"**AI 关掉了**"和"**AI 开着但现在不该动手**" ——
    /// 这两件事在窗口里必须看得出区别，否则玩家会以为 mod 坏了。
    /// 所以是三态：
    ///
    /// | 模式 | 自动闭环 | 手动测试钩子 | 谁会用 |
    /// | --- | --- | --- | --- |
    /// | `Off` | ❌ | ❌ | 玩家想自己玩 |
    /// | `Auto` | ✅ | ❌ | 正常托管（**默认**） |
    /// | `Manual` | ❌ | ✅ | 我（测试）显式戳某一步 |
    ///
    /// ## 为什么要"否决意图"
    ///
    /// 关掉托管之后，如果什么都不记，窗口上就只剩"没有待办" ——
    /// 玩家无法判断是"AI 觉得没事做"还是"我把 AI 关了"。
    /// 所以每次被开关拦下都**记一条 `Vetoed` 意图，并写清是哪一关拦的**
    /// （项目铁律：任何"没做成"的分支都要能说出为什么）。
    ///
    /// ⚠ 本类**不引用任何具体域**，也不引用 UI。所以：
    ///   - 加一个域 → 不用改这里
    ///   - 换一个 UI/按钮 → 不用改这里
    ///   - 将来把"托管"升级成"分域托管"（食物托管、建造不托管）→ **在这里加一个集合**，
    ///     仍然是"加接口"，不用改调用方（调用方问的永远是 `ShouldAct(domainId, source)`）
    /// </summary>
    public static class TakeoverController
    {
        /// <summary>托管模式</summary>
        public static TakeoverMode Mode = TakeoverMode.Auto;

        /// <summary>上次开关的 tick（窗口显示"什么时候被谁关的"）</summary>
        public static int LastChangedTick;
        public static string LastChangedBy = "默认";

        /// <summary>被开关拦下来的**总次数**（窗口显示，避免"看起来没动静"被误解成坏了）</summary>
        public static int VetoedCount;

        /// <summary>最后一次否决的原因（窗口显示一行）</summary>
        public static string LastVetoReason = "";

        /// <summary>存档键（改了名字会导致老存档读不回来，别乱改）</summary>
        public const string ScribeKey = "foodControlTakeoverMode";

        public static bool IsOn
        {
            get { return Mode == TakeoverMode.Auto; }
        }

        public static string ModeName
        {
            get
            {
                switch (Mode)
                {
                    case TakeoverMode.Off: return "关（AI 不托管）";
                    case TakeoverMode.Auto: return "开（AI 托管中）";
                    case TakeoverMode.Manual: return "手动（仅测试钩子可用）";
                    default: return Mode.ToString();
                }
            }
        }

        /// <summary>窗口顶栏那一行（纯函数，可测）</summary>
        public static string StatusLine()
        {
            string line = "AI 托管：" + ModeName;
            line += "　（上次切换：tick " + LastChangedTick + "，由 " + LastChangedBy + "）";
            if (VetoedCount > 0) line += "　被拦 " + VetoedCount + " 次";
            return line;
        }

        /// <summary>
        /// **唯一的判定入口**。任何要"动手"的地方都问它，不要各自看 `Mode`。
        ///
        /// </summary>
        /// <param name="domainId">哪个域在问（用于否决记录）</param>
        /// <param name="source">
        /// "auto" = 日常调度（受托管开关管）；
        /// "manual" = 测试钩子（只在 `Manual` 或 `Auto` 下放行）；
        /// "player" = 玩家在本机点的（永远放行 —— 玩家自己动手不该被 AI 开关挡住）
        /// </param>
        public static bool ShouldAct(string domainId, string source)
        {
            if (string.Equals(source, "player", StringComparison.OrdinalIgnoreCase)) return true;

            if (Mode == TakeoverMode.Off)
            {
                LastVetoReason = "AI 托管已关闭（" + domainId + " 的 " + source + " 动作被拦）";
                return false;
            }
            if (string.Equals(source, "manual", StringComparison.OrdinalIgnoreCase)
                && Mode != TakeoverMode.Manual && Mode != TakeoverMode.Auto)
            {
                LastVetoReason = "当前模式不允许手动动作（" + domainId + "）";
                return false;
            }
            return true;
        }

        /// <summary>
        /// 判定 + **记一条否决意图**（这样窗口上看得见"我把它关了，所以它没动"）。
        /// 返回能不能动手。
        /// </summary>
        public static bool ShouldActAndRecord(string domainId, string source, IntentKind kind, string what)
        {
            if (ShouldAct(domainId, source)) return true;

            VetoedCount++;
            AiIntent it = IntentBoard.Report(
                string.IsNullOrEmpty(domainId) ? "framework" : domainId,
                IntentKind.Veto,
                "被开关拦下：" + (string.IsNullOrEmpty(what) ? kind.ToString() : what),
                LastVetoReason + " —— 想让 AI 动手请点顶部的「AI 托管」按钮",
                "", IntentState.Vetoed,
                SafeTick(), SafeDay());
            it.Result = LastVetoReason;
            return false;
        }

        // ------------------------------------------------------------------
        // 开关操作（按钮/探针都走这里 —— 只有这一份实现）
        // ------------------------------------------------------------------

        public static string SetMode(TakeoverMode mode, string by)
        {
            Mode = mode;
            LastChangedTick = SafeTick();
            LastChangedBy = string.IsNullOrEmpty(by) ? "?" : by;

            string line = "AI 托管 → " + ModeName;
            IntentBoard.Report("framework", IntentKind.Notify,
                line, "由 " + LastChangedBy + " 切换", "", IntentState.Done, SafeTick(), SafeDay());
            try { Log.Message("[FoodControl] " + line); } catch (Exception) { }
            return line;
        }

        /// <summary>按钮用：在 开/关 之间切（手动模式不参与，免得玩家切不回来）</summary>
        public static string Toggle(string by)
        {
            return SetMode(IsOn ? TakeoverMode.Off : TakeoverMode.Auto, by);
        }

        public static string CycleMode(string by)
        {
            TakeoverMode next;
            switch (Mode)
            {
                case TakeoverMode.Auto: next = TakeoverMode.Off; break;
                case TakeoverMode.Off: next = TakeoverMode.Manual; break;
                default: next = TakeoverMode.Auto; break;
            }
            return SetMode(next, by);
        }

        /// <summary>存档/读档（由主组件的 ExposeData 调；这里只做值的读写，不认识游戏类型）</summary>
        public static void ExposeMode()
        {
            try
            {
                int m = (int)Mode;
                Scribe_Values.Look(ref m, ScribeKey, (int)TakeoverMode.Auto);
                if (!Enum.IsDefined(typeof(TakeoverMode), m)) m = (int)TakeoverMode.Auto;   // 读到脏值就回默认
                Mode = (TakeoverMode)m;

                Scribe_Values.Look(ref VetoedCount, ScribeKey + "Vetoed", 0);
            }
            catch (Exception)
            {
                // 存档字段出问题不能拦着读档
            }
        }

        private static int SafeTick()
        {
            try { return Find.TickManager != null ? Find.TickManager.TicksGame : 0; }
            catch (Exception) { return 0; }
        }

        private static int SafeDay()
        {
            try
            {
                Map m = Find.CurrentMap;
                if (m != null) return FoodControlGameComponent.CalendarDay(m);
            }
            catch (Exception) { }
            return 0;
        }
    }

    /// <summary>托管模式（详见 `TakeoverController`）</summary>
    public enum TakeoverMode
    {
        /// <summary>关：AI 什么都不做（玩家想自己玩）</summary>
        Off = 0,

        /// <summary>开：自动闭环照常跑（**默认**）</summary>
        Auto = 1,

        /// <summary>手动：停掉自动闭环，但允许显式测试钩子</summary>
        Manual = 2,
    }
}
