using System;
using System.Collections.Generic;
using FoodControl.Core;
using RimWorld;
using Verse;

namespace FoodControl.Game
{
    /// <summary>
    /// **食物域**：把原来的"种田闭环"搬进 <see cref="INeedDomain"/>。
    ///
    /// 这是 S2 重构的核心动作 —— **只是搬家，不改行为**。
    /// 原来这七步硬编码在 `FoodControlGameComponent.RunCycle` 里，
    /// 现在外壳换成域接口，内部逻辑一行没动。
    ///
    /// 搬家带来的好处（也是这次重构的全部目的）：
    ///   - 加"住处/储藏区"时不用再抄一遍七步，只写一个新的 `INeedDomain`
    ///   - 调度、异常隔离、按天去重、遥测打标都由 <see cref="NeedCoordinator"/> 统一负责
    ///
    /// ## 这个域的节奏
    ///
    /// - **每天**跑一次决策（`ShouldRunToday` 目前恒为 true，但判断放在域里，
    ///   将来要改成"每 3 天一次"或"只在缺粮时跑"都不用动框架）
    /// - 每天先做**准备工作**：把地图上被禁取的物资解锁（见 `ColonySetup` 的注释，
    ///   那里记着"只做一次"踩过的坑）
    /// </summary>
    public sealed class FoodDomain : INeedDomain
    {
        public string Id
        {
            get { return "food"; }
        }

        public string DisplayName
        {
            get { return "食物"; }
        }

        /// <summary>食物是保命的，优先级最高（数字最小）</summary>
        public int Priority
        {
            get { return 10; }
        }

        public bool IsSurvivalCritical
        {
            get { return true; }
        }

        /// <summary>
        /// 每天的准备：允许所有被禁取的物资。
        ///
        /// 为什么归到**食物域**而不是独立成一个域：它目前服务的主要目的就是
        /// "让小人有吃的可用"（食物、建材都可能被禁）。将来如果别的域也需要，
        /// 再把它提成独立的 `SetupDomain`，那时框架已经在了，改动很小。
        /// </summary>
        public SetupActionResult OnDayStart(DomainContext ctx)
        {
            if (ctx == null || ctx.Map == null) return null;
            return ColonySetup.Tick(ctx.Map, ctx.Day);
        }

        /// <summary>
        /// 只在**决策时段**内跑（默认 8:00–19:00）。
        ///
        /// ## 为什么必须有时段窗口（这不是随手加的）
        ///
        /// 实测：`Plant.GrowthRate` 在**夜里是 0**（机制是 `Plant.Resting`，
        /// 一天中约 0.25–0.8 的时段之外）。如果在夜里决策，
        /// 预测器会认为「所有在种的作物**永远不会成熟**」→ 缺口被**系统性高估**
        /// → mod 会反复扩大种植。**限制在白天决策，这个坑就不会发生。**
        ///
        /// ⚠ 这个判断必须在**域里**做，而且协调器**不能**因为"今天没跑"就标记已完成 ——
        ///   否则凌晨第一次 tick 会把当天标记掉，8 点之后就再也不跑了。
        ///   这个坑我在 `NeedCoordinator` 的注释里也记了一笔。
        /// </summary>
        public bool ShouldRunToday(DomainContext ctx)
        {
            if (ctx == null) return false;
            return ctx.Hour >= FoodControlGameComponent.DecisionHourFrom
                && ctx.Hour <= FoodControlGameComponent.DecisionHourTo;
        }

        public DomainCycleResult RunCycle(DomainContext ctx)
        {
            var result = new DomainCycleResult { DomainId = Id };

            try
            {
                Map map = ctx.Map;

                // ① 作物 Def：策略里写的是 defName，这里解析成 ThingDef
                //    用 GetNamed(name, false) —— 不要 errorOnFail=true，
                //    否则玩家配置写错一个名字就刷红字
                ThingDef crop = ctx.CropDef ?? FoodControlGameComponent.ResolveCropDef(ctx.Policy.PreferredPlantDefName);

                // ② 选址计划（游戏地图 → Core 的栅格/距离场/候选）
                PlotPlan plan = PlotPlanner.BuildPlan(
                    map, crop, ctx.SiteOptions, ctx.Policy.MaxCellsPerDecision);

                // ③ 感知（游戏 API → 原始读数）
                GameReadings readings = StateReader.Read(map, plan);

                // ④ 翻译成 Core 的数据结构（Core 不许出现游戏类型）
                FoodState state = StateReader.ToFoodState(readings, plan, ctx.Calibration);

                // ⑤ 决策（Core，纯函数，可离线复现）
                FoodDecision decision = FoodControl.Core.Policy.Decide(state, ctx.Policy);

                // ⑥ 执行（Core 的动作 → 真的建区/设作物）
                ExecutionResult exec = Executor.Execute(decision.Action, plan, map);

                result.Readings = readings;
                result.State = state;
                result.Plan = plan;
                result.Decision = decision;
                result.Execution = exec;
                result.Ok = true;
                result.DidWork = decision.Action != null
                    && decision.Action.Kind != FoodActionKind.DoNothing;
                result.Summary = FoodControlGameComponent.SummarizeCycle(
                    readings, state, decision, exec, plan);

                // ⑦ 遥测
                Telemetry.Write(readings, state, plan, decision, exec, "auto", "");
            }
            catch (Exception e)
            {
                result.Ok = false;
                result.Error = e.ToString();
                result.Summary = "食物域决策轮失败（已吞掉，不影响游戏）：" + e.Message;
                try { Log.Error("[FoodControl] 食物域决策轮异常：" + e); } catch (Exception) { }

                // 失败也要留痕：遥测里应该能看到"这一轮炸了"
                try { Telemetry.Write(null, null, null, null, null, "auto", e.ToString()); } catch (Exception) { }
            }

            return result;
        }
    }
}
