using System;
using System.Collections.Generic;
using FoodControl.Core;
using Verse;

namespace FoodControl.Game
{
    /// <summary>
    /// **一个"需求域"（need domain）** —— 殖民地的一类生产目标。
    ///
    /// 目前只有一个实现（食物），但框架先立起来，因为：
    ///   - 用户的目标是「保证小人有吃、有穿、有住处、心情不崩」，
    ///     这天然是**多个域**而不是一个
    ///   - 复用的部分是**骨架**（调度、异常隔离、遥测、测试钩子），
    ///     而不是具体策略。现在把食物搬进这个骨架，第二个域就只是"再写一个实现"
    ///
    /// ## 设计要点（都是踩过的坑换来的）
    ///
    /// 1. **每个域自带"启动动作"（`OnDayStart`）**
    ///    因为"开局允许所有物资"那次踩过一个坑：把初始化当成一次性的，
    ///    结果它在地图还没就绪时就跑了、扫了个空、然后永远不再跑。
    ///    → 所以域**每个游戏日**都会被问一次"你要不要先做点准备"，由域自己决定做什么。
    ///
    /// 2. **域之间的异常必须互相隔离**
    ///    食物域炸了不能连累住处域。协调器逐个 `try/catch`。
    ///
    /// 3. **域自己决定"今天要不要动手"**
    ///    不同域的节奏不同：食物要天天看，住处可能盖完就几个月不用管。
    ///    所以 `ShouldRunToday` 交给域自己实现，而不是框架统一规定。
    ///
    /// 4. **共享上下文而不是各自乱读游戏**
    ///    `DomainContext` 里带上当天算好的公共量（地图、日期、作物 Def、选址参数……），
    ///    避免每个域都去重复读一遍游戏状态。
    /// </summary>
    public interface INeedDomain
    {
        /// <summary>稳定标识，用于遥测打标与按天去重（如 "food" / "shelter"）。**不许改**。</summary>
        string Id { get; }

        /// <summary>人能读的名字（日志/遥测用）。</summary>
        string DisplayName { get; }

        /// <summary>
        /// 优先级：数字**小的先跑**。
        /// 将来域之间抢资源（同一批小人工时）时，这个顺序就是仲裁的依据。
        /// </summary>
        int Priority { get; }

        /// <summary>
        /// 是否是**生存必需**的域（食物、住处）。用于将来的"保命线"：
        /// 资源不够时优先保证这些，其余可以让路。
        /// </summary>
        bool IsSurvivalCritical { get; }

        /// <summary>
        /// **每天一次的准备工作**（在决策之前）。可以返回 null 表示"无事可做"。
        ///
        /// 这一层的存在是因为"允许物资"这类家务活：
        /// 它不属于"决策"，但必须**每天**检查（游戏里随时可能出现新的禁取物）。
        /// </summary>
        SetupActionResult OnDayStart(DomainContext ctx);

        /// <summary>
        /// **今天要不要跑决策**。由域自己决定节奏（食物天天看，住处可能很久才看一次）。
        /// </summary>
        bool ShouldRunToday(DomainContext ctx);

        /// <summary>跑一轮这个域的闭环。**不许抛异常**（自己吞掉并写进结果）。</summary>
        DomainCycleResult RunCycle(DomainContext ctx);
    }

    /// <summary>协调器传给每个域的共享上下文（当天算好一次，避免各域重复读游戏）。</summary>
    public sealed class DomainContext
    {
        public Map Map;

        /// <summary>绝对游戏日（年 × 60 + 年内天）</summary>
        public int Day;

        /// <summary>一天中的小时（0–23）</summary>
        public int Hour;

        public string Season = "";

        /// <summary>策略参数（将来 LLM 慢环改的就是这里，见设计文档 §三）</summary>
        public PolicyConfig Policy;

        /// <summary>标定系数</summary>
        public Calibration Calibration;

        /// <summary>选址参数</summary>
        public SiteSelectionOptions SiteOptions;

        /// <summary>解析好的目标作物 Def（可能为 null —— 配置里写错名字时）</summary>
        public ThingDef CropDef;
    }

    /// <summary>一个域跑完一轮的结果。保留原有的诊断字段（遥测要用）。</summary>
    public sealed class DomainCycleResult
    {
        public string DomainId = "";
        public bool Ok;

        /// <summary>是否本次真的做了事（false = 看了但不需要动手）</summary>
        public bool DidWork;

        public string Summary = "";
        public string Error = "";

        // 食物域专用的诊断字段（其它域可以留空，不影响）
        public GameReadings Readings;
        public FoodState State;
        public PlotPlan Plan;
        public FoodDecision Decision;
        public ExecutionResult Execution;
    }

    /// <summary>
    /// **需求域协调器**：统一的调度、异常隔离、遥测编排。
    ///
    /// 它负责的事（各域都不用重复实现）：
    ///   - 按 `Priority` 依次跑各域
    ///   - 每个域独立 `try/catch`（一个域炸了不连累其它域）
    ///   - 每天每个域只跑一次（按 `域Id + 日` 去重）
    ///   - 跑完统一记日志
    ///
    /// ⚠ 它**不负责判断该不该跑某个域** —— 那是各域 `ShouldRunToday` 的事。
    /// </summary>
    public static class NeedCoordinator
    {
        private static readonly List<INeedDomain> _domains = new List<INeedDomain>();

        /// <summary>每个域上次跑过的日子（按 Id 索引），用于按天去重</summary>
        private static readonly Dictionary<string, int> _lastRunDay = new Dictionary<string, int>();

        /// <summary>本次会话里跑过的总轮数（诊断用）</summary>
        public static int TotalCycles;

        /// <summary>已经注册的域（只读，供测试/状态展示）</summary>
        public static IList<INeedDomain> Domains
        {
            get { return _domains.AsReadOnly(); }
        }

        /// <summary>
        /// 注册一个域。**幂等**：同一个 Id 重复注册会被忽略
        /// （游戏可能重新加载程序集，重复注册会跑两遍）。
        /// </summary>
        public static void Register(INeedDomain domain)
        {
            if (domain == null) return;
            if (string.IsNullOrEmpty(domain.Id)) return;

            for (int i = 0; i < _domains.Count; i++)
            {
                if (_domains[i].Id == domain.Id) return;      // 已经有了
            }

            _domains.Add(domain);
            // 按 Priority 排序（小的先跑）
            _domains.Sort((a, b) => a.Priority.CompareTo(b.Priority));
        }

        /// <summary>注册内置的域。多次调用安全。</summary>
        public static void RegisterBuiltIns()
        {
            Register(new FoodDomain());
        }

        /// <summary>测试用：把"按天去重"清掉，让下一轮重新跑。</summary>
        public static void ResetDayTracking()
        {
            _lastRunDay.Clear();
        }

        /// <summary>测试/调试用：看某个域上次跑过哪天</summary>
        public static int LastRunDayOf(string domainId)
        {
            int d;
            return _lastRunDay.TryGetValue(domainId, out d) ? d : int.MinValue;
        }

        /// <summary>
        /// **廉价的早退检查**：有没有任何域"今天该跑但还没跑"。
        ///
        /// 为什么要它：组件每 64 tick 就会调一次协调器，而完整跑一遍
        /// `RunAllDomains` 要建上下文（含解析 Def）、遍历所有域。
        /// 大部分时候答案都是"没事可做"，所以先用这个便宜的问题挡一下 ——
        /// 它只做字典查找 + 几次时段比较，不碰游戏数据。
        ///
        /// ⚠ 语义上必须与 `RunAllDomains` 的判定**一致**，否则会出现
        ///   "早退说没事、实际有事"（永远不跑）或反向（每 tick 都跑）。
        /// </summary>
        public static bool AnyDomainDue(int day, int hour)
        {
            for (int i = 0; i < _domains.Count; i++)
            {
                INeedDomain d = _domains[i];
                if (LastRunDayOf(d.Id) == day) continue;          // 今天跑过了

                // 时段敏感的域：这里要能安全调用（失败就当作"不跑"，不抛）
                try
                {
                    var probe = new DomainContext { Day = day, Hour = hour };
                    if (d.ShouldRunToday(probe)) return true;
                }
                catch (Exception)
                {
                    // 判断失败就当它今天不跑；真正的异常会在 RunAllDomains 里被记录
                }
            }
            return false;
        }

        /// <summary>
        /// **每个游戏日跑一次**：按优先级依次跑各域。
        /// 调用方（`FoodControlGameComponent`）负责判断"是不是新的一天"。
        /// </summary>
        public static List<DomainCycleResult> RunAllDomains(DomainContext ctx)
        {
            var results = new List<DomainCycleResult>();
            if (ctx == null || ctx.Map == null) return results;

            for (int i = 0; i < _domains.Count; i++)
            {
                INeedDomain domain = _domains[i];
                var r = new DomainCycleResult { DomainId = domain.Id };

                try
                {
                    // ── ① 每天一次的准备工作（各域自己决定做什么）──
                    try
                    {
                        SetupActionResult setup = domain.OnDayStart(ctx);
                        if (setup != null && setup.Changed > 0)
                        {
                            try
                            {
                                Log.Message(string.Format("[FoodControl] {0} 准备：{1}",
                                    domain.DisplayName, setup));
                            }
                            catch (Exception) { }
                        }
                    }
                    catch (Exception e)
                    {
                        // 准备工作失败**不阻止**该域继续跑决策 —— 能做的事还是要做
                        try
                        {
                            Log.Error(string.Format("[FoodControl] {0}.OnDayStart 异常（已吞掉，继续跑决策）：{1}",
                                domain.DisplayName, e));
                        }
                        catch (Exception) { }
                    }

                    // ── ② 按天去重：今天已经跑过就不再跑 ──
                    //
                    // ⚠ 这一步**必须**存在，而且必须在 ShouldRunToday 之前。
                    //   我重构时曾经把它删掉，只留了"执行后记账"，结果：
                    //   白天的每一次协调器调用（每 64 tick 一次）都判定 ShouldRunToday=true
                    //   → **一天跑了约 900 次决策**，日志被刷爆、CPU 白烧。
                    //   实测证据（tick 间隔 64、内容完全相同）：
                    //     [FoodControl] tick=154240 day=3(Spring) ... 动作=PlantCrop Plant_Rice ×41
                    //     [FoodControl] tick=154304 day=3(Spring) ... 动作=PlantCrop Plant_Rice ×41
                    //   → 去重与"时段"是**两件独立的事**，不能合并成一个判断。
                    int lastRunDay = LastRunDayOf(domain.Id);
                    if (lastRunDay == ctx.Day)
                    {
                        r.Ok = true;
                        r.Summary = "今天已经跑过";
                        results.Add(r);
                        continue;
                    }

                    // ── ③ 域自己的节奏（今天这个时刻要不要跑）──
                    bool shouldRun;
                    try { shouldRun = domain.ShouldRunToday(ctx); }
                    catch (Exception e)
                    {
                        r.Ok = false;
                        r.Error = "ShouldRunToday 异常：" + e.Message;
                        results.Add(r);
                        continue;
                    }

                    if (!shouldRun)
                    {
                        // ⚠ 这里**不要**记 _lastRunDay：`ShouldRunToday` 是**时段敏感**的
                        //   （食物域要等到 8:00–19:00）。如果在这里就记下"今天跑过了"，
                        //   凌晨第一次 tick 会把当天标记掉，当天 8 点之后就再也不会跑了。
                        //   → 只"暂不跑"，不记账；等时段满足时再去重那张表还是空的，于是会跑。
                        r.Ok = true;
                        r.Summary = "按自身节奏暂不跑（等条件满足）";
                        results.Add(r);
                        continue;
                    }

                    // 到这一步说明"决定要跑这一轮了"，先记账再跑：
                    // 万一下面抛异常，今天也不会疯狂重试（异常本来就会被吞掉）。
                    _lastRunDay[domain.Id] = ctx.Day;

                    // ── ③ 跑闭环 ──
                    DomainCycleResult inner = domain.RunCycle(ctx);
                    if (inner != null)
                    {
                        inner.DomainId = domain.Id;
                        r = inner;
                        if (r.DidWork) TotalCycles++;
                    }
                    else
                    {
                        r.Ok = false;
                        r.Error = "RunCycle 返回 null";
                    }
                }
                catch (Exception e)
                {
                    // ★ 域之间的异常隔离：某个域炸了，其它域照跑
                    r.Ok = false;
                    r.Error = e.ToString();
                    r.Summary = domain.DisplayName + " 异常（已吞掉，不影响其它域）：" + e.Message;
                    try { Log.Error("[FoodControl] " + domain.DisplayName + " 异常：" + e); } catch (Exception) { }
                }

                results.Add(r);
            }

            return results;
        }

        /// <summary>把一批结果拼成一行日志/摘要。</summary>
        public static string Summarize(List<DomainCycleResult> results)
        {
            if (results == null || results.Count == 0) return "（没有注册任何域）";
            var parts = new List<string>();
            for (int i = 0; i < results.Count; i++)
            {
                DomainCycleResult r = results[i];
                parts.Add(string.IsNullOrEmpty(r.Summary) ? (r.DomainId + ":（无摘要）") : r.Summary);
            }
            return string.Join(" || ", parts.ToArray());
        }
    }
}
