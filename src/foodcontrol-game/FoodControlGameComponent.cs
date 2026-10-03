using System;
using System.Collections.Generic;
using FoodControl.Core;
using RimWorld;
using Verse;

namespace FoodControl.Game
{
    /// <summary>一次闭环跑完之后的全部中间产物（遥测与桥接钩子都用它）。</summary>
    public sealed class CycleResult
    {
        public bool Ok;
        public string Source = "";

        public int Tick;
        public int Day;
        public string Season = "";

        /// <summary>一行摘要（写进 Player.log，也直接返回给桥接钩子）</summary>
        public string Summary = "";
        /// <summary>失败时的异常全文（成功时为空）</summary>
        public string Error = "";

        public GameReadings Readings;
        public FoodState State;
        public PlotPlan Plan;
        public FoodDecision Decision;
        public ExecutionResult Execution;
    }

    /// <summary>
    /// 闭环驱动：**每游戏日一次**（不是每 tick）。
    ///
    /// 为什么每天一次：设计文档 §「决策周期」明确「游戏内每天一次 + 事件触发，避免频繁改动导致振荡」。
    /// 验收标准 #5（不振荡）就是冲着这条来的 —— 控制类 mod 最常见的死法就是「种了铲、铲了种」，
    /// 而它连续跑一跑是看不出来的。
    ///
    /// **实例是怎么被游戏造出来的（IL 确证，不是推断）**：
    ///   `Game.FillComponents()` 的 IL 是
    ///       components.RemoveAll(...);
    ///       foreach (Type t in typeof(GameComponent).AllSubclassesNonAbstract())
    ///           if (GetComponent(t) == null)
    ///               try { components.Add((GameComponent)Activator.CreateInstance(t, this)); } catch { Log.Error(...) }
    ///   而 `GenTypes.AllActiveAssemblies` 的 IL 是
    ///       yield Assembly.GetExecutingAssembly();                       // Assembly-CSharp
    ///       foreach (mod in LoadedModManager.RunningMods)
    ///           foreach (asm in mod.assemblies.loadedAssemblies) yield asm;   // ★ 包含本 mod 的程序集
    ///   ⇒ **只要本类有个 public 的 (Game) 构造函数，游戏就会自动把它 new 出来并挂上**，
    ///     不需要在 Mod 子类里手动注册。
    ///   ⇒ 构造函数**必须恰好一个 `Game` 参数**（`Activator.CreateInstance(type, this)`），
    ///     参数少了/多了都会进 catch 分支 → `Log.Error("Could not instantiate a GameComponent of type ...")`。
    /// </summary>
    public sealed class FoodControlGameComponent : GameComponent
    {
        /// <summary>静态钩子：供 RimBridgeServer 伴生 DLL / 测试直接拿实例，不用等真实游戏日过去。</summary>
        public static FoodControlGameComponent Instance;

        // ---- 调度参数（都是 public static，方便测试临时改）----

        /// <summary>总开关</summary>
        public static bool Enabled = true;

        /// <summary>决策窗口起点（游戏内小时）。默认 8。
        /// 为什么要窗口：`Plant.GrowthRate` 在**夜里是 0**（机制是 `Plant.Resting`，确证文档 §B4 的 Resting IL：
        /// 与 `GenLocalDate.DayPercent` 的 0.25/0.8 比较，即约 6:00–19:12 之外为 resting）。
        /// 如果在夜里决策，预测器会认为「所有在种的作物永远不会成熟」→ 缺口被高估 → 反复扩大种植。</summary>
        public static int DecisionHourFrom = 8;
        /// <summary>决策窗口终点（游戏内小时）。默认 19（≈日落）。</summary>
        public static int DecisionHourTo = 19;

        /// <summary>策略参数（将来的 LLM 慢环改的就是这一层，见设计文档 §三）</summary>
        public PolicyConfig Policy = new PolicyConfig();

        /// <summary>标定系数（MVP 用默认值；`NutritionPerCellAtMaturity` 等应由实测回填）</summary>
        public Calibration Calibration = new Calibration();

        /// <summary>选址参数（Core 的默认值就是用户拍板的 40/1.0/120）</summary>
        public SiteSelectionOptions SiteOptions = new SiteSelectionOptions();

        /// <summary>保留 game 引用（1.6 的 `Verse.GameComponent` 自己**没有** game 字段，所以自己存一份）</summary>
        private readonly Game _game;

        /// <summary>上次跑过的"日"（绝对日序号 = 年 × 60 + 年内天）</summary>
        private int _lastRunDay = int.MinValue;

        private int _cycleCount;
        private bool _loggedPath;

        /// <summary>
        /// ⚠ 参数**必须是单个 `Game`** —— 游戏用 `Activator.CreateInstance(type, this)` 造这个类（见类注释）。
        /// </summary>
        public FoodControlGameComponent(Game game)
            : base()
        {
            _game = game;
            Instance = this;
        }

        /// <summary>存档/读档。把"上次跑过的日"存下来，避免读档瞬间重复决策一次。</summary>
        public override void ExposeData()
        {
            try
            {
                Scribe_Values.Look(ref _lastRunDay, "foodControlLastRunDay", int.MinValue);
            }
            catch (Exception e)
            {
                try { Log.Warning("[FoodControl] ExposeData 失败（忽略）：" + e.Message); } catch (Exception) { }
            }
        }

        /// <summary>
        /// 每个游戏 tick 都会被 `GameComponentUtility.GameComponentTick()` 调（确证：IL 里是 `callvirt`，
        /// 所以 `override` 才生效）。
        ///
        /// ⚠ **整个方法体必须在 try/catch 里**：这是设计要求（mod 异常绝不能把游戏搞崩）。
        ///   游戏自己虽然也包了一层 try/catch（只是 `Log.Error(exception.ToString())`），
        ///   但那样一次决策失败会变成一堆红字，我们宁可自己吞掉并把原因写进遥测。
        /// </summary>
        public override void GameComponentTick()
        {
            if (!Enabled) return;
            if (Instance == null) Instance = this;

            // 日期查询没必要每 tick 做：64 tick 粒度足够（一天 60000 tick，窗口内至少有几百次机会）
            if ((GenTicks.TicksGame & 0x3F) != 0) return;

            try
            {
                if (Current.ProgramState != ProgramState.Playing) return;
                if (Current.Game == null) return;

                Map map = Find.CurrentMap;
                if (map == null || !map.IsPlayerHome) return;

                int hour = GenLocalDate.HourOfDay(map);
                if (hour < DecisionHourFrom || hour > DecisionHourTo) return;

                int day = CalendarDay(map);
                if (day == _lastRunDay) return;         // 今天已经跑过了
                _lastRunDay = day;

                CycleResult cr = RunCycle(map, "auto");
                try { Log.Message("[FoodControl] " + cr.Summary); } catch (Exception) { }
            }
            catch (Exception e)
            {
                // 到这里为止任何异常都只记日志，绝不外抛
                try { Log.Error("[FoodControl] GameComponentTick 异常（已吞掉，不影响游戏）：" + e); } catch (Exception) { }
            }
        }

        // ------------------------------------------------------------------
        // 测试钩子（桥接 / 手动跑一轮）
        // ------------------------------------------------------------------

        /// <summary>
        /// **手动跑一轮闭环**：忽略「每天一次」和「决策时段」两条调度规则，立即执行完整闭环。
        /// 给 RimBridgeServer 伴生 DLL 用 —— 测试不用等真实游戏日过去。
        /// </summary>
        /// <returns>一行摘要（成功或失败都会给出）</returns>
        public string RunCycleNow()
        {
            try
            {
                if (Current.ProgramState != ProgramState.Playing)
                    return "未在游戏中（ProgramState != Playing）";
                Map map = Find.CurrentMap;
                if (map == null) return "没有当前地图";
                CycleResult cr = RunCycle(map, "manual");
                return cr.Summary;
            }
            catch (Exception e)
            {
                try { Log.Error("[FoodControl] RunCycleNow 异常：" + e); } catch (Exception) { }
                return "RunCycleNow 异常：" + e.Message;
            }
        }

        /// <summary>
        /// **只读感知**：跑感知 + 选址，返回一行 JSON，**不决策、不执行、不写遥测**。
        /// 给测试断言"当前读到什么"用（免得观测动作本身改变了世界）。
        /// </summary>
        public string ReadStateJson()
        {
            try
            {
                Map map = Find.CurrentMap;
                if (map == null) return "{\"error\":\"没有当前地图\"}";

                ThingDef crop = ResolveCropDef(Policy.PreferredPlantDefName);
                PlotPlan plan = PlotPlanner.BuildPlan(map, crop, SiteOptions, Policy.MaxCellsPerDecision);
                GameReadings readings = StateReader.Read(map, plan);
                FoodState state = StateReader.ToFoodState(readings, plan, Calibration);
                return Telemetry.BuildJson(readings, state, plan, null, null, "read-only", "");
            }
            catch (Exception e)
            {
                return "{\"error\":" + Escape(e.Message) + "}";
            }
        }

        /// <summary>最近一次决策的遥测行（不碰文件）。</summary>
        public string LastTelemetryLine
        {
            get { return Telemetry.LastLine; }
        }

        public int CycleCount
        {
            get { return _cycleCount; }
        }

        /// <summary>遥测文件绝对路径 + 已写入行数（测试脚本需要知道去哪儿读）。</summary>
        public string TelemetryLocation
        {
            get
            {
                return Telemetry.TelemetryPath + "  (lines=" + Telemetry.LinesWritten
                    + ", lastError=" + (string.IsNullOrEmpty(Telemetry.LastError) ? "none" : Telemetry.LastError) + ")";
            }
        }

        // ------------------------------------------------------------------
        // 闭环本体
        // ------------------------------------------------------------------

        /// <summary>
        /// 一轮闭环：感知 → 预测 → 决策 → 执行 → 遥测。
        /// **任何异常都被吞进 <see cref="CycleResult.Error"/>**，返回值永远非 null。
        /// </summary>
        public CycleResult RunCycle(Map map, string source)
        {
            var result = new CycleResult();
            result.Source = source != null ? source : "";

            try
            {
                // ① 作物 Def：策略里写的是 defName，这里解析成 ThingDef。
                //    用 GetNamed(name, false)（确证文档 §C10：DefDatabase<T>.GetNamed(string, bool errorOnFail)）
                //    —— 不要 errorOnFail=true，否则玩家改配置文件写错一个名字就刷红字。
                ThingDef crop = ResolveCropDef(Policy.PreferredPlantDefName);

                // ② 选址计划（游戏地图 → Core 的栅格/距离场/候选）
                PlotPlan plan = PlotPlanner.BuildPlan(map, crop, SiteOptions, Policy.MaxCellsPerDecision);

                // ③ 感知（游戏 API → 原始读数 → Core 的 FoodState）
                GameReadings readings = StateReader.Read(map, plan);
                FoodState state = StateReader.ToFoodState(readings, plan, Calibration);

                // ④ 决策（Core，纯函数，可离线复现）
                FoodDecision decision = Policy.Decide(state, Policy);

                // ⑤ 执行（Core 的动作 → 真的建区/设作物）
                ExecutionResult exec = Executor.Execute(decision.Action, plan, map);

                result.Readings = readings;
                result.State = state;
                result.Plan = plan;
                result.Decision = decision;
                result.Execution = exec;
                result.Tick = readings.Tick;
                result.Day = readings.Year * 60 + readings.DayOfYear;
                result.Season = readings.Season;
                result.Ok = true;
                result.Summary = Summarize(readings, state, decision, exec, plan);

                Telemetry.Write(readings, state, plan, decision, exec, source, "");
            }
            catch (Exception e)
            {
                result.Ok = false;
                result.Error = e.ToString();
                result.Summary = "决策轮失败（已吞掉，不影响游戏）：" + e.Message;
                try { Log.Error("[FoodControl] 决策轮异常：" + e); } catch (Exception) { }

                // 失败也要留痕：遥测里应该能看到"这一轮炸了"
                try { Telemetry.Write(null, null, null, null, null, source, e.ToString()); } catch (Exception) { }
            }

            _cycleCount++;

            if (!_loggedPath)
            {
                _loggedPath = true;
                try { Log.Message("[FoodControl] 遥测文件：" + Telemetry.TelemetryPath); } catch (Exception) { }
            }

            return result;
        }

        /// <summary>解析策略里写的作物 defName。找不到就返回 null（调用方会降级，不会硬崩）。</summary>
        public static ThingDef ResolveCropDef(string defName)
        {
            if (string.IsNullOrEmpty(defName)) return null;
            try
            {
                ThingDef def = DefDatabase<ThingDef>.GetNamed(defName, false);
                if (def == null)
                {
                    try { Log.Warning("[FoodControl] 找不到作物 Def：" + defName + "（这一轮不动作）"); } catch (Exception) { }
                }
                return def;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 找一个活着的实例。
        /// 优先用静态钩子；钩子为空（例如 mod 被后加载）时去 `Current.Game.components` 里翻。
        /// </summary>
        public static FoodControlGameComponent Find()
        {
            if (Instance != null) return Instance;
            try
            {
                Game g = Current.Game;
                if (g != null && g.components != null)
                {
                    for (int i = 0; i < g.components.Count; i++)
                    {
                        var c = g.components[i] as FoodControlGameComponent;
                        if (c != null)
                        {
                            Instance = c;
                            return c;
                        }
                    }
                }
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>
        /// 保险绳：万一自动发现没生效（例如别的东西先缓存了 GenTypes），手动把组件挂上。
        /// 正常路径用不到（见类注释的 IL 确证），但多这一条成本极低。
        /// </summary>
        public static FoodControlGameComponent EnsureRegistered()
        {
            FoodControlGameComponent found = Find();
            if (found != null) return found;

            try
            {
                Game g = Current.Game;
                if (g == null || g.components == null) return null;
                var created = new FoodControlGameComponent(g);
                g.components.Add(created);
                try { Log.Message("[FoodControl] 自动发现失败，已手动注册 GameComponent（保险绳生效）"); } catch (Exception) { }
                return created;
            }
            catch (Exception e)
            {
                try { Log.Error("[FoodControl] 手动注册 GameComponent 失败：" + e); } catch (Exception) { }
                return null;
            }
        }

        // ------------------------------------------------------------------
        // 工具
        // ------------------------------------------------------------------

        /// <summary>绝对日序号（年 × 60 + 年内天）。`GenDate.DaysPerYear` 是 const 60。</summary>
        public static int CalendarDay(Map map)
        {
            return GenLocalDate.Year(map) * 60 + GenLocalDate.DayOfYear(map);
        }

        private static string Summarize(
            GameReadings readings, FoodState state, FoodDecision decision, ExecutionResult exec, PlotPlan plan)
        {
            if (readings == null || decision == null)
                return "决策轮没有产出（感知或决策为 null）";

            FoodAction act = decision.Action;
            string action = act == null ? "(null)" : act.Kind.ToString();
            if (act != null && act.Kind == FoodActionKind.PlantCrop)
                action += " " + act.PlantDefName + " ×" + act.Cells;

            return string.Format(
                "tick={0} day={1}({2}) 存量={3:F1} 消耗={4:F2}/天 可撑={5:F1}天 可用空地={6} 动作={7} 执行={8}({9})",
                readings.Tick, readings.DayOfYear, readings.Season,
                state.Stock, state.BurnRatePerDay, state.ReserveDays,
                plan != null ? plan.ArableCellsAvailable : 0,
                action,
                exec != null ? exec.Method : "none",
                exec != null ? exec.Detail : "");
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "\"\"";
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "") + "\"";
        }
    }
}
