using System;
using System.Collections.Generic;
using UnityEngine;
using RimWorld;
using Verse;

namespace FoodControl.Game
{
    /// <summary>
    /// **「AI 在做什么」窗口**（用户 2026-10-03 要求）。
    ///
    /// > 「局内再放一个窗口，可以把 **AI 打算做什么**事情给打印出来，
    /// >   也能让玩家知道 AI 要做什么或者**即将要做什么**。」
    ///
    /// ## 分工（踩过的坑：别把逻辑写进 OnGUI）
    ///
    /// | 谁 | 负责 |
    /// | --- | --- |
    /// | `IntentBoard.LinesForUi()` | **显示什么**（纯函数，可离线测试） |
    /// | 本类 `DoWindowContents` | **画出来** + 接按钮 |
    ///
    /// 之前 `MouseCellOverlay` 的教训是把可测的逻辑和绘制混在一起；
    /// 这里刻意分开：文本由看板产出，UI 只负责布局。
    ///
    /// ## 窗口里有什么
    ///
    /// 1. 托管状态一行（模式 / 上次谁切的 / 被拦了几次）
    /// 2. **即将要做**（`Planned` / `Running`）—— 用户要的"即将"
    /// 3. 最近发生（含失败与**被否决**，以及原因）
    /// 4. 出口状态（写到哪个文件了、写了几条、有没有出错）
    /// 5. 三个按钮：托管开关 / 意图流文件开关 / 清空记录
    /// </summary>
    public class IntentWindow : Window
    {
        public static IntentWindow Instance;

        /// <summary>最多显示多少条"即将做"和"最近发生"（避免窗口爆掉）</summary>
        public static int MaxPendingShown = 10;
        public static int MaxRecentShown = 14;

        private Vector2 _scroll = Vector2.zero;
        private List<string> _lines = new List<string>();
        private int _linesTick = -999;
        private int _linesModeHash;

        public IntentWindow()
        {
            // ⚠ 这些是 `Window` 的**字段**，不是属性 —— 赋值必须在构造里
            this.doWindowBackground = true;
            this.draggable = true;
            this.resizeable = true;
            this.preventCameraMotion = false;
            this.onlyOneOfTypeAllowed = true;
            this.absorbInputAroundWindow = false;
            this.forcePause = false;         // ★ 绝不能强制暂停（否则 play_for 会静默跑 0 tick）
            this.closeOnAccept = false;
            this.closeOnCancel = false;
            this.closeOnClickedOutside = false;
            this.soundAppear = null;
            this.soundClose = null;
            this.layer = WindowLayer.GameUI;
        }

        public override Vector2 InitialSize
        {
            get { return new Vector2(660f, 520f); }
        }

        /// <summary>
        /// **加上/去掉窗口**（"+XX" 与 "▣ 按钮" 都走这里 —— 只有这一份实现）。
        ///
        /// ⚠ 一定要判空再 new：`WindowStack` 里同类型只允许一个，
        ///   重复 new 会变成"点了没反应"（旧的那个还在栈上，新的被拒）。
        /// </summary>
        public static void SetVisible(bool visible)
        {
            try
            {
                if (visible)
                {
                    if (Instance == null)
                    {
                        Instance = new IntentWindow();
                        Find.WindowStack.Add(Instance);
                    }
                    else if (!Find.WindowStack.IsOpen(typeof(IntentWindow)))
                    {
                        Find.WindowStack.Add(Instance);
                    }
                }
                else
                {
                    if (Instance != null) Find.WindowStack.TryRemove(Instance, false);
                }
            }
            catch (Exception e)
            {
                try { Log.Warning("[FoodControl] 意图窗口开关失败：" + e.Message); } catch (Exception) { }
            }
        }

        /// <summary>
        /// 窗口是不是开着。
        ///
        /// ⚠ 名字**不能**叫 `IsOpen` —— `Verse.Window` 上已经有一个同名成员，
        ///   隐藏它会得到 CS0108 警告，而且容易在别处调错实现。所以叫 `IsShown`。
        /// </summary>
        public static bool IsShown
        {
            get
            {
                try { return Find.WindowStack != null && Find.WindowStack.IsOpen(typeof(IntentWindow)); }
                catch (Exception) { return false; }
            }
        }

        public static void ToggleVisible()
        {
            SetVisible(!IsShown);
        }

        public override void DoWindowContents(Rect inRect)
        {
            // ── 缓存：不必每帧重算文本（内容只在意图变化/开关变化时变） ──
            int tick = SafeTick();
            int modeHash = (int)TakeoverController.Mode * 31
                + (TakeoverController.VetoedCount & 0xFFFF) * 7
                + IntentBoard.All().Count * 131
                + (IntentIpc.FileSink != null && IntentIpc.FileSink.Enabled ? 1 : 0);
            if (tick - _linesTick > 30 || modeHash != _linesModeHash)
            {
                _lines = IntentBoard.LinesForUi(TakeoverController.StatusLine(),
                    MaxPendingShown, MaxRecentShown);
                _linesTick = tick;
                _linesModeHash = modeHash;
            }

            var listing = new RectOffset(0, 0, 0, 0);
            var inner = new Rect(inRect.x, inRect.y, inRect.width, inRect.height);
            Listing_Standard list = new Listing_Standard();
            list.Begin(inner);

            // ── 顶部：标题 + 托管开关 ──
            Text.Font = GameFont.Medium;
            list.Label("AI 在做什么");
            Text.Font = GameFont.Small;

            list.Label(TakeoverController.StatusLine());
            list.GapLine();

            // ── 按钮行 ──
            var btnRow = list.GetRect(30f);
            float bw = 150f;
            if (Widgets.ButtonText(new Rect(btnRow.x, btnRow.y, bw, 28f),
                    TakeoverController.IsOn ? "AI 托管：开 → 点我关" : "AI 托管：关 → 点我开"))
            {
                TakeoverController.Toggle("窗口按钮");
            }
            if (Widgets.ButtonText(new Rect(btnRow.x + bw + 6f, btnRow.y, bw, 28f), "意图记录：清空"))
            {
                IntentBoard.Reset();
                TakeoverController.VetoedCount = 0;
            }
            bool logSinkOn = IntentIpc.LogSink != null && IntentIpc.LogSink.Enabled;
            if (Widgets.ButtonText(new Rect(btnRow.x + (bw + 6f) * 2f, btnRow.y, bw, 28f),
                    logSinkOn ? "写游戏日志：开" : "写游戏日志：关"))
            {
                if (IntentIpc.LogSink != null) IntentIpc.LogSink.Enabled = !logSinkOn;
            }
            bool fileSinkOn = IntentIpc.FileSink == null || IntentIpc.FileSink.Enabled;
            if (Widgets.ButtonText(new Rect(btnRow.x + (bw + 6f) * 3f, btnRow.y, bw - 10f, 28f),
                    fileSinkOn ? "写文件：开" : "写文件：关"))
            {
                if (IntentIpc.FileSink != null) IntentIpc.FileSink.Enabled = !fileSinkOn;
            }
            list.Gap(6f);

            // ── 正文（可滚动） ──
            float footer = 52f;
            var scrollRect = new Rect(inRect.x, list.CurHeight, inRect.width, inRect.height - list.CurHeight - footer);
            float lineH = Text.LineHeight;
            float contentH = Mathf.Max(scrollRect.height, _lines.Count * lineH + 8f);
            var viewRect = new Rect(0f, 0f, scrollRect.width - 20f, contentH);

            Widgets.BeginScrollView(scrollRect, ref _scroll, viewRect);
            float y = 0f;
            for (int i = 0; i < _lines.Count; i++)
            {
                string line = _lines[i] ?? "";
                // 状态前缀上色（★ 让"被否决/失败"一眼看得出来）
                Color old = GUI.color;
                if (line.IndexOf("被否决") >= 0 || line.IndexOf("失败") >= 0) GUI.color = new Color(1f, 0.6f, 0.5f);
                else if (line.IndexOf("完成") >= 0) GUI.color = new Color(0.7f, 1f, 0.7f);
                else if (line.IndexOf("──") >= 0) GUI.color = new Color(0.75f, 0.85f, 1f);
                Widgets.Label(new Rect(4f, y, viewRect.width - 8f, lineH), line);
                GUI.color = old;
                y += lineH;
            }
            Widgets.EndScrollView();

            // ── 底部：出口状态（"发到哪儿了、有没有出错"） ──
            var foot = new Rect(inRect.x, inRect.yMax - footer, inRect.width, footer);
            GUI.color = new Color(0.8f, 0.8f, 0.8f);
            Widgets.Label(new Rect(foot.x, foot.y, foot.width, 22f), IntentIpc.DescribeOutlets());
            GUI.color = Color.white;
            try
            {
                Widgets.Label(new Rect(foot.x, foot.y + 22f, foot.width, 22f),
                    "文件：" + IntentIpc.IntentPath);
            }
            catch (Exception) { }

            list.End();
        }

        private static int SafeTick()
        {
            try { return Find.TickManager != null ? Find.TickManager.TicksGame : 0; }
            catch (Exception) { return 0; }
        }
    }

    /// <summary>
    /// **主按钮栏上的「AI 托管」按钮**（`MainButtonDef` 的 worker）。
    ///
    /// ## ★ 为什么逻辑全写在 `Activate()` 里（IL 确证过，不是猜的）
    ///
    /// `AsmDump` + `IlDump` 实测原版 1.6：
    /// ```
    /// MainButtonWorker.get_Visible()      → 非 virtual，只读 def.buttonVisible
    /// MainButtonWorker.DoButton(Rect)     → 非 virtual（主按钮栏直接用基类引用调它）
    /// MainButtonWorker.Activate()         → **abstract**（所以子类覆写它一定被调到）
    /// InterfaceTryActivate()              → 末尾 `callvirt MainButtonWorker::Activate`
    /// ```
    /// → 结论：**只有 `override Activate()` 是保证会被调到的入口**。
    ///   所以"开/关"和"开窗口"都挂在这里，而不去改 `DoButton`（改了也不会被调）。
    ///
    /// ## 交互（一个物理按钮，两个动作）
    ///
    /// | 操作 | 结果 |
    /// | --- | --- |
    /// | **左键** | 切换 AI 托管（开 ↔ 关）—— 最常用的动作 |
    /// | **右键** | 打开/关闭「AI 在做什么」窗口 |
    /// | **中键** | 三态循环（自动 → 关 → 手动 → 自动），给测试用 |
    ///
    /// 按钮文字来自 `MainButtonDefs.xml` 的 `label`（"AI托管"）。
    /// ⚠ 想让它显示"开/关"两个字，就必须覆写 `DoButton` —— 而那需要原版字段是 virtual
    ///   （现在不是）。**替代方案已实现**：每次切换都会往意图流里写一条 `Notify`，
    ///   窗口与 Player.log 里都能看到"AI 托管 → 关（…）"，所以状态不会看不见。
    /// </summary>
    public class MainButtonWorker_Takeover : MainButtonWorker
    {
        public override void Activate()
        {
            int button = 0;
            try { if (Event.current != null) button = Event.current.button; }
            catch (Exception) { }

            if (button == 1)
            {
                // 右键：开/关那个窗口（用户要的"把 AI 打算做什么打印出来"）
                IntentWindow.ToggleVisible();
                return;
            }
            if (button == 2)
            {
                TakeoverController.CycleMode("主按钮(中键)");
                return;
            }
            TakeoverController.Toggle("主按钮");
        }
    }
}
