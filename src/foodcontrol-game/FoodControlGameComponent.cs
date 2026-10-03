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

        /// <summary>
        /// 策略参数（将来的 LLM 慢环改的就是这一层，见设计文档 §三）。
        /// ⚠ 字段名**不能**叫 `Policy`：本文件 `using FoodControl.Core;` 引入了同名类型 `Policy`，
        ///   字段会遮蔽类型，`Policy.Decide(...)` 直接编译不过（CS1061）。踩过。
        /// </summary>
        public PolicyConfig PolicySettings = new PolicyConfig();

        /// <summary>标定系数（MVP 用默认值；`NutritionPerCellAtMaturity` 等应由实测回填）。
        /// 同理不叫 `Calibration`，避免遮蔽同名的 Core 类型。</summary>
        public Calibration CalibrationSettings = new Calibration();

        /// <summary>选址参数（Core 的默认值就是用户拍板的 40/1.0/120）</summary>
        public SiteSelectionOptions SiteOptions = new SiteSelectionOptions();

        /// <summary>
        /// 保留 game 引用（1.6 的 `Verse.GameComponent` 自己**没有** game 字段，所以自己存一份）。
        /// ⚠ 必须写全 `Verse.Game` —— 本文件的命名空间就是 `FoodControl.Game`，
        ///   裸写 `Game` 会被解析成命名空间（CS0118），这是踩过的编译错误。
        /// </summary>
        private readonly Verse.Game _game;

        /// <summary>上次跑过的"日"（绝对日序号 = 年 × 60 + 年内天）</summary>
        private int _lastRunDay = int.MinValue;

        private int _cycleCount;
        private bool _loggedPath;

        /// <summary>
        /// ⚠ 参数**必须是单个 `Game`** —— 游戏用 `Activator.CreateInstance(type, this)` 造这个类（见类注释）。
        /// </summary>
        public FoodControlGameComponent(Verse.Game game)
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

                // 初始化操作也按"天"去重，所以把"上次做过的日"一起存
                int setupDay = ColonySetup.LastSetupDay;
                Scribe_Values.Look(ref setupDay, "foodControlSetupDay", int.MinValue);
                ColonySetup.LastSetupDay = setupDay;

                int totalUnforbidden = ColonySetup.TotalUnforbidden;
                Scribe_Values.Look(ref totalUnforbidden, "foodControlTotalUnforbidden", 0);
                ColonySetup.TotalUnforbidden = totalUnforbidden;
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
        ///
        /// ## ⚠ 这里曾经犯过一个错（值得留档）
        ///
        /// 最初的写法是把"允许所有物资"当成**一次性初始化**，在进地图后**立刻**做一次、
        /// 之后永不重复。实测发现它**在地图刚生成、debug 开局的物资还没刷出来时就跑了** ——
        /// 扫到 0 个禁取物，然后标记"已完成"，**此后 49 个被禁取物品永远没人解锁**。
        ///
        /// 教训：「只做一次」这个前提本身就是错的。游戏里随时可能出现新的禁取物
        /// （空投、商队、新生成的东西）。**改成跟着日闭环每天检查一次**（幂等、开销可忽略）。
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

                Map map = Verse.Find.CurrentMap;
                if (map == null || !map.IsPlayerHome) return;

                int day = CalendarDay(map);

                // ── ① 每日一次：按优先级跑各"需求域" ──
                //
                // 放在时段判断**之前**，因为域自己会在 `OnDayStart` 里做家务
                // （比如"允许所有物资"），那类事没理由等到 8 点 ——
                // 而且新殖民地往往是半夜开局的。
                //
                // ⚠ 先用 `AnyDomainDue` 做**廉价早退**：这个方法每 64 tick 就会被调一次，
                //   而真正跑一遍要建上下文（解析 Def）+ 遍历域。大部分时候答案都是"没事可做"。
                //   （曾经因为漏了按天去重，导致一天跑 900 次决策 —— 见 `NeedCoordinator` 的注释）
                int hour = GenLocalDate.HourOfDay(map);
                try
                {
                    if (NeedCoordinator.AnyDomainDue(day, hour))
                    {
                        DomainContext ctx = BuildContext(map, day, hour);
                        List<DomainCycleResult> results = NeedCoordinator.RunAllDomains(ctx);
                        string summary = NeedCoordinator.Summarize(results);

                        // 只在真的有域动手了才打日志，避免刷屏
                        bool anyWork = false;
                        for (int i = 0; i < results.Count; i++) if (results[i].DidWork) anyWork = true;
                        if (anyWork)
                        {
                            try { Log.Message("[FoodControl] " + summary); } catch (Exception) { }
                        }
                    }
                }
                catch (Exception e)
                {
                    try { Log.Error("[FoodControl] 域协调器异常（已吞掉）：" + e); } catch (Exception) { }
                }
            }
            catch (Exception e)
            {
                // 到这里为止任何异常都只记日志，绝不外抛
                try { Log.Error("[FoodControl] GameComponentTick 异常（已吞掉，不影响游戏）：" + e); } catch (Exception) { }
            }
        }

        /// <summary>
        /// 组装一次域调用的共享上下文（公共量只算一次，避免各域重复读游戏）。
        /// </summary>
        public DomainContext BuildContext(Map map, int day, int hour)
        {
            var ctx = new DomainContext
            {
                Map = map,
                Day = day,
                Hour = hour,
                Policy = PolicySettings,
                Calibration = CalibrationSettings,
                SiteOptions = SiteOptions,
            };

            try { ctx.Season = GenLocalDate.Season(map).ToString(); } catch (Exception) { }
            try { ctx.CropDef = ResolveCropDef(PolicySettings.PreferredPlantDefName); } catch (Exception) { }

            return ctx;
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
                Map map = Verse.Find.CurrentMap;
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
                Map map = Verse.Find.CurrentMap;
                if (map == null) return "{\"error\":\"没有当前地图\"}";

                ThingDef crop = ResolveCropDef(PolicySettings.PreferredPlantDefName);
                PlotPlan plan = PlotPlanner.BuildPlan(map, crop, SiteOptions, PolicySettings.MaxCellsPerDecision);
                GameReadings readings = StateReader.Read(map, plan);
                FoodState state = StateReader.ToFoodState(readings, plan, CalibrationSettings);
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

        // ------------------------------------------------------------------
        // 测试钩子（桥接 / 手动跑一轮）
        // ------------------------------------------------------------------

        /// <summary>给测试钩子复用的单例（真实调度走协调器各自 new；这里只为手动触发）</summary>
        private static StockpileDomain _stockpileForManualRun;

        /// <summary>
        /// **静态测试钩子**：立刻跑一次储藏区域（**忽略"每 N 天"的调度**）。
        ///
        /// 为什么需要它：储藏区域的正常节奏是每 3 个游戏日一次，
        /// 测试如果只能等真实调度，就要推进 3 天，慢且不好定位。
        /// </summary>
        public static string RunStockpileNow()
        {
            try
            {
                Map map = Verse.Find.CurrentMap;
                if (map == null) return "没有当前地图";

                if (_stockpileForManualRun == null) _stockpileForManualRun = new StockpileDomain();

                DomainContext ctx = BuildManualContext(map);
                DomainCycleResult r = _stockpileForManualRun.RunCycle(ctx);
                return r == null ? "返回 null" : r.Summary;
            }
            catch (Exception e)
            {
                try { Log.Error("[FoodControl] RunStockpileNow 异常：" + e); } catch (Exception) { }
                return "RunStockpileNow 异常：" + e.Message;
            }
        }

        /// <summary>
        /// **静态测试钩子**：立刻跑一次"允许所有被禁取物资"，并返回一行结果。
        ///
        /// 为什么放在这个类上而不是 `ColonySetup` 上：
        ///   探针 `DSH.FoodProbe` 是按 `FoodControl.Game.<类型名>` 找类型的，
        ///   而它已经知道 `FoodControlGameComponent` 这个名字。把钩子放这里，
        ///   探针不用改 `FindType` 的搜索逻辑。
        ///
        /// ⚠ 必须是 **public static、0 个参数**（探针是 `CallStatic(t, "名字")` 调进来的）。
        /// </summary>
        public static string RunUnforbidNow()
        {
            try
            {
                Map map = Verse.Find.CurrentMap;
                if (map == null) return "没有当前地图";
                SetupActionResult r = ColonySetup.RunNow(map);
                if (r == null) return "ColonySetup.RunNow 返回 null";
                return r.ToString();
            }
            catch (Exception e)
            {
                try { Log.Error("[FoodControl] RunUnforbidNow 异常：" + e); } catch (Exception) { }
                return "RunUnforbidNow 异常：" + e.Message;
            }
        }

        /// <summary>**静态测试钩子**：数一下手上能用于建造的建材（S4 的输入）。</summary>
        public static string CountMaterialsJson()
        {
            try
            {
                Map map = Verse.Find.CurrentMap;
                if (map == null) return "{\"error\":\"没有当前地图\"}";
                var counts = ColonySetup.CountBuildingMaterials(map);
                var parts = new List<string>();
                foreach (var kv in counts)
                {
                    parts.Add("\"" + kv.Key.Replace("\"", "'") + "\":" + kv.Value);
                }
                return "{\"count\":" + counts.Count + ",\"materials\":{" + string.Join(",", parts.ToArray()) + "}}";
            }
            catch (Exception e)
            {
                return "{\"error\":\"" + e.Message.Replace("\"", "'") + "\"}";
            }
        }

        // ------------------------------------------------------------------
        // S4 住处域的测试钩子
        // ------------------------------------------------------------------

        /// <summary>给测试钩子复用的单例（真实调度走协调器各自 new）</summary>
        private static ShelterDomain _shelterForManualRun;

        /// <summary>
        /// **静态测试钩子**：立刻跑一次住处域（**忽略"每 N 天"的调度**）。
        ///
        /// 和 `RunStockpileNow` 同款：不这么写的话，测试要推进到
        /// "第 3 的倍数的游戏日"才能看到住处域动手，慢且不好定位。
        /// </summary>
        public static string RunShelterNow()
        {
            try
            {
                Map map = Verse.Find.CurrentMap;
                if (map == null) return "没有当前地图";

                if (_shelterForManualRun == null) _shelterForManualRun = new ShelterDomain();

                DomainContext ctx = BuildManualContext(map);
                DomainCycleResult r = _shelterForManualRun.RunCycle(ctx);
                return r == null ? "返回 null" : r.Summary;
            }
            catch (Exception e)
            {
                try { Log.Error("[FoodControl] RunShelterNow 异常：" + e); } catch (Exception) { }
                return "RunShelterNow 异常：" + e.Message;
            }
        }

        /// <summary>
        /// **静态测试钩子**：最近一次住处域的详细记账 JSON（0 参数，探针直接调）。
        ///
        /// 它回答"**我们自己放了几个**"；地图上"**实际有几个**"由桥接的
        /// `foodprobe/list_buildings` 独立回读。两个数一比就知道差额从哪来 ——
        /// 这正是我看到"48 面墙蓝图 vs 23 面墙户型"时缺的那条信息。
        /// </summary>
        public static string ShelterReportJson()
        {
            try { return ShelterDomain.LastReportJson; }
            catch (Exception e) { return "{\"error\":\"" + e.Message.Replace("\"", "'") + "\"}"; }
        }

        /// <summary>
        /// **静态测试钩子（只读）**：把住处域"这一轮会怎么盖"算出来，**绝不动地图**。
        ///
        /// 为什么需要它：真放蓝图是不可逆的（地图上会多几十个蓝图）。
        /// 测试可以先用它确认户型/材质合理，再决定要不要真放。
        /// </summary>
        public static string InspectShelterNow()
        {
            try
            {
                Map map = Verse.Find.CurrentMap;
                if (map == null) return "没有当前地图";
                return ShelterDomain.InspectNow(map);
            }
            catch (Exception e)
            {
                return "InspectShelterNow 异常：" + e.Message;
            }
        }

        /// <summary>
        /// **静态测试钩子（测试专用）**：忽略"已有住处/已有蓝图"的守卫，强制盖一间。
        ///
        /// 为什么必须有它：**debug 开局自带 662 面墙 + 28 扇门**，
        /// 正常守卫会正确地拒绝动手 —— 守卫没错，但验收就永远看不到那 23+1 个蓝图。
        /// 这个钩子把 `ForceOverride` 打开跑一轮，**跑完一定关回去**。
        /// </summary>
        public static string RunShelterNowForce()
        {
            bool old = ShelterDomain.ForceOverride;
            try
            {
                Map map = Verse.Find.CurrentMap;
                if (map == null) return "没有当前地图";

                if (_shelterForManualRun == null) _shelterForManualRun = new ShelterDomain();

                ShelterDomain.ForceOverride = true;
                ShelterDomain.ResetPlacedFlag();

                DomainContext ctx = BuildManualContext(map);
                DomainCycleResult r = _shelterForManualRun.RunCycle(ctx);

                return "[force] " + (r == null ? "返回 null" : r.Summary);
            }
            catch (Exception e)
            {
                try { Log.Error("[FoodControl] RunShelterNowForce 异常：" + e); } catch (Exception) { }
                return "RunShelterNowForce 异常：" + e.Message;
            }
            finally
            {
                ShelterDomain.ForceOverride = old;      // ★ 无论如何都关回去（生产路径不许留着）
            }
        }

        /// <summary>**静态测试钩子（测试专用）**：开关 `ShelterDomain.ForceOverride`（"on"/"off"）。</summary>
        public static string SetShelterForce(string mode)
        {
            try
            {
                bool on = !string.IsNullOrEmpty(mode)
                    && (mode == "on" || mode == "true" || mode == "1");
                ShelterDomain.ForceOverride = on;
                if (on) ShelterDomain.ResetPlacedFlag();
                return "ShelterDomain.ForceOverride=" + ShelterDomain.ForceOverride;
            }
            catch (Exception e)
            {
                return "SetShelterForce 异常：" + e.Message;
            }
        }

        /// <summary>
        /// **静态测试钩子**：开关地板蓝图（"on"/"off"）。
        ///
        /// ⚠ 实测确证地板**有**蓝图形态（`TerrainDefOf.WoodPlankFloor.blueprintDef`
        /// == `Blueprint_WoodPlankFloor`），所以生产默认是 **on**。
        /// 这个开关留给测试做"关掉地板"的对照。
        /// </summary>
        public static string SetShelterFloorPath(string mode)
        {
            try
            {
                bool on = !string.IsNullOrEmpty(mode)
                    && (mode == "on" || mode == "true" || mode == "1");
                ShelterDomain.BuildFloor = on;
                return "ShelterDomain.BuildFloor=" + ShelterDomain.BuildFloor
                    + "（地板走 GenConstruct.PlaceBlueprintForBuild）";
            }
            catch (Exception e)
            {
                return "SetShelterFloorPath 异常：" + e.Message;
            }
        }

        /// <summary>**静态测试钩子**：把住处域的"已经盖过"状态清掉（配合按天去重一起清）。</summary>
        public static string ResetShelterFlags()
        {
            try
            {
                NeedCoordinator.ResetDayTracking();
                ShelterDomain.ResetPlacedFlag();
                return "已清空按天去重记录 + 住处'盖过'标记（住处域今天可以再跑）";
            }
            catch (Exception e)
            {
                return "ResetShelterFlags 异常：" + e.Message;
            }
        }

        /// <summary>组装手动触发的上下文（`Instance` 没有时的兜底）。</summary>
        private static DomainContext BuildManualContext(Map map)
        {
            if (Instance != null)
            {
                return Instance.BuildContext(map, CalendarDay(map), GenLocalDate.HourOfDay(map));
            }
            return new DomainContext
            {
                Map = map,
                Day = CalendarDay(map),
                Hour = GenLocalDate.HourOfDay(map),
                Policy = new PolicyConfig(),
                Calibration = new Calibration(),
                SiteOptions = new SiteSelectionOptions(),
                CropDef = ResolveCropDef("Plant_Rice"),
            };
        }

        /// <summary>**静态测试钩子**：重置"今天已做过初始化"标记，让下次 tick 重跑。</summary>
        public static string ResetSetupFlags()
        {
            try
            {
                ColonySetup.ResetForTesting();
                return "已重置（下次调用会重跑初始化）";
            }
            catch (Exception e)
            {
                return "重置失败：" + e.Message;
            }
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
                ThingDef crop = ResolveCropDef(PolicySettings.PreferredPlantDefName);

                // ② 选址计划（游戏地图 → Core 的栅格/距离场/候选）
                PlotPlan plan = PlotPlanner.BuildPlan(map, crop, SiteOptions, PolicySettings.MaxCellsPerDecision);

                // ③ 感知（游戏 API → 原始读数 → Core 的 FoodState）
                GameReadings readings = StateReader.Read(map, plan);
                FoodState state = StateReader.ToFoodState(readings, plan, CalibrationSettings);

                // ④ 决策（Core，纯函数，可离线复现）
                FoodDecision decision = FoodControl.Core.Policy.Decide(state, PolicySettings);

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
                result.Summary = SummarizeCycle(readings, state, decision, exec, plan);

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
                Verse.Game g = Current.Game;
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
                Verse.Game g = Current.Game;
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

        /// <summary>
        /// 一行摘要。**public** 是因为 `FoodDomain` 要用它（S2 重构后闭环搬到了域里）。
        /// </summary>
        public static string SummarizeCycle(
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
