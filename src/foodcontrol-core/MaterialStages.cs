using System;
using System.Collections.Generic;

namespace FoodControl.Core
{
    /// <summary>
    /// **一个建造段需要多少材料** —— 分段材料决策的输入。
    ///
    /// 需求按**角色**分开算（墙和床的 `costStuffCount` 不一定一样），
    /// 而且只有 `NeedsStuff` 的角色才吃材料（床/灯这类成品是"做一个拿一个"）。
    /// </summary>
    public sealed class StageNeed
    {
        public int Order = BuildOrder.Unspecified;
        public string Label = "";

        /// <summary>这一段总共要吃多少材料</summary>
        public int StuffNeeded;

        /// <summary>其中需要材料的格子数（诊断用）</summary>
        public int StuffCells;

        /// <summary>这一段里**不需要材料**的格子数（成品家具，诊断用）</summary>
        public int StuffFreeCells;

        /// <summary>按类型分的格子数（让摘要能说出"这一段有多少格是墙"）</summary>
        public Dictionary<char, int> CellsBySymbol = new Dictionary<char, int>();

        /// <summary>哪些角色**吃材料**（`NeedsStuff=true`）—— 分段决策只关心这些</summary>
        public List<char> StuffSymbols = new List<char>();

        /// <summary>
        /// 是否**需要材料**。
        /// ⚠ 与"有没有东西要放"是两件事：全是成品家具的段是"要放但不用料"，
        ///   不能因为 `StuffNeeded == 0` 就把它判成"这段不用管"。
        /// </summary>
        public bool NeedsMaterial
        {
            get { return StuffNeeded > 0; }
        }

        public override string ToString()
        {
            return string.Format("{0}({1})：料 {2} / 吃料格 {3} / 免料格 {4}",
                Label, Order, StuffNeeded, StuffCells, StuffFreeCells);
        }
    }

    /// <summary>某一段**最终用了什么料、用掉多少**。</summary>
    public sealed class StageAllocation
    {
        public int Order;

        /// <summary>这一段**计划**要多少材料（不管库存够不够）</summary>
        public int Needed;

        /// <summary>
        /// 选定的材料。三种语义，**与旧 `MaterialPlan` 保持一致**（这是踩过坑的约定）：
        ///   - `null` → 都不够，**这段不造**
        ///   - `""`   → 无需材料（全是成品家具），**可以造**
        ///   - `"Steel"` 等 → 用这种料
        /// </summary>
        public string ChosenStuff;

        /// <summary>从库存里实际划走多少（可能小于 `Needed` —— 剩下的部分就是缺口）</summary>
        public int Allocated;

        /// <summary>每个候选材料的评估（**含"为什么没选它"**）</summary>
        public List<MaterialOption> Options = new List<MaterialOption>();

        /// <summary>缺口（`Needed - Allocated`，够时为 0）</summary>
        public int Short
        {
            get { return Needed > Allocated ? Needed - Allocated : 0; }
        }

        public bool CanBuild
        {
            get { return ChosenStuff != null; }     // "" 也算能造
        }

        public bool NeedsMaterial
        {
            get { return Needed > 0; }
        }

        /// <summary>一句话理由（必须能说清"为什么是这个料 / 为什么这段不造"）</summary>
        public string Reason = "";

        public override string ToString()
        {
            if (ChosenStuff == null) return "第" + Order + "段：不造（" + Reason + "）";
            if (ChosenStuff.Length == 0) return "第" + Order + "段：无需材料";
            return string.Format("第{0}段：{1} 划走 {2}/{3}{4}",
                Order, ChosenStuff, Allocated, Needed, Short > 0 ? "（缺 " + Short + "）" : "");
        }
    }

    /// <summary>分段材料决策的结果。</summary>
    public sealed class StagedMaterialPlan
    {
        public List<StageAllocation> Stages = new List<StageAllocation>();

        /// <summary>每种材料最终用掉多少</summary>
        public Dictionary<string, int> Used = new Dictionary<string, int>();

        /// <summary>决策后还剩多少（= 输入库存 − 用掉）</summary>
        public Dictionary<string, int> Remaining = new Dictionary<string, int>();

        /// <summary>每种材料**本来**有多少（诊断用，免得看不出"是不是根本没这个料"）</summary>
        public Dictionary<string, int> Available = new Dictionary<string, int>();

        /// <summary>整个计划一共要吃多少材料</summary>
        public int TotalNeeded;
        public int TotalAllocated;

        /// <summary>有没有任何一段能造（全不能造时整栋就别动）</summary>
        public bool AnyStageBuildable;

        /// <summary>是不是**每一段**都能造（整栋可完整建造）</summary>
        public bool FullyCovered;

        /// <summary>一句话摘要（带原因）</summary>
        public string Summary = "";

        public StageAllocation Find(int order)
        {
            for (int i = 0; i < Stages.Count; i++) if (Stages[i].Order == order) return Stages[i];
            return null;
        }

        public int TotalShort
        {
            get { return TotalNeeded > TotalAllocated ? TotalNeeded - TotalAllocated : 0; }
        }

        public override string ToString()
        {
            return Summary;
        }
    }

    /// <summary>
    /// **分段材料决策** —— 逐段决定用什么料，用完一段再决定下一段。
    ///
    /// ## 为什么推翻"整栋一次评估"（用户 2026-10-03 的原话）
    ///
    /// > 「**不是**在造整个样板之前就决定，而是它可能有一个**分段的决策**。
    /// >   比如说你外墙，先把外墙的命令下下去，也是用木头，然后发现木头不够了，
    /// >   然后再把那个床的命令下下去，用的是铁。」
    ///
    /// 原来的 `MaterialPlanner.Plan` 是**整栋一次性评估**：选一种料把整栋建完。
    /// 那在"整栋同料"这个子问题里是最优的，但它会**浪费已经够用的材料**：
    ///
    /// ```
    /// 整栋需 320，木头 300、钢 900
    ///   整栋一次评估 → 木头不够整栋 → 选钢 → **木头 300 一点没用上**
    ///   分段         → 外墙用木头（用它 300 里能用的部分，剩 40）→ 床用钢
    /// ```
    ///
    /// ## 与"建造顺序"的关系
    ///
    /// 这两件事**是同一个机制的两面**：既然建造是有顺序的（`BuildOutline`），
    /// 那材料也应该**跟着顺序分段决定**。所以本类刻意接受一个**有序的**
    /// `List<StageNeed>`，就按给定顺序依次决策。
    ///
    /// ## 不改动旧路径
    ///
    /// `MaterialPlanner.Plan`（整栋评估）**原样保留**，还有测试覆盖它。
    /// 本类是**新增的接口**：要不要换过来是个显式决定，而不是"重构顺手改了行为"。
    /// 这正是用户要的"加接口就行，不需要大改整个流程"。
    ///
    /// 纯逻辑，不依赖游戏，可离线测试。
    /// </summary>
    public static class MaterialStagedPlanner
    {
        /// <summary>
        /// 按**给定的段顺序**逐段决定材料。
        /// </summary>
        /// <param name="stages">有序的段需求。顺序 = 建造顺序（墙 → 门 → 床 → …）</param>
        /// <param name="available">可搬运的材料数量：stuff defName → 数量。**本方法按值拷贝，不改调用方的表**</param>
        /// <param name="preference">首选顺序（木头 → 钢 → 石砖…）。为空时用 `available` 的键顺序（不稳定，建议显式给）</param>
        public static StagedMaterialPlan Plan(
            IList<StageNeed> stages,
            Dictionary<string, int> available,
            IList<string> preference)
        {
            var plan = new StagedMaterialPlan();
            if (available != null)
            {
                foreach (KeyValuePair<string, int> kv in available)
                {
                    plan.Available[kv.Key] = kv.Value;
                    plan.Remaining[kv.Key] = kv.Value;      // 工作副本，不动入参
                }
            }

            var candidates = BuildCandidates(available, preference);

            if (stages == null || stages.Count == 0)
            {
                plan.Summary = "没有建造段（样板是空的？）";
                return plan;
            }
            if (candidates.Count == 0)
            {
                plan.Summary = "地图上没有任何可用材料 —— 整栋都造不了";
                for (int i = 0; i < stages.Count; i++)
                {
                    plan.Stages.Add(new StageAllocation
                    {
                        Order = stages[i].Order,
                        Needed = stages[i].StuffNeeded,
                        ChosenStuff = null,
                        Reason = "地图上没有任何可用材料",
                    });
                }
                return plan;
            }

            for (int i = 0; i < stages.Count; i++)
            {
                StageNeed need = stages[i];
                var alloc = new StageAllocation
                {
                    Order = need.Order,
                    Needed = need.StuffNeeded,
                };

                // ① 这段不需要材料（全是成品家具）→ 可以造，不用划料
                if (need.StuffNeeded <= 0)
                {
                    alloc.ChosenStuff = "";
                    alloc.Allocated = 0;
                    alloc.Reason = "这一段全是成品家具，不需要建造材料";
                    plan.Stages.Add(alloc);
                    continue;
                }
                plan.TotalNeeded += need.StuffNeeded;

                // ② 按首选顺序，找第一个**能凑够这一段**的材料
                string chosen = null;
                string why = "";
                for (int c = 0; c < candidates.Count; c++)
                {
                    string stuff = candidates[c];
                    int have;
                    if (!plan.Remaining.TryGetValue(stuff, out have)) have = 0;

                    alloc.Options.Add(new MaterialOption
                    {
                        StuffDefName = stuff,
                        Needed = need.StuffNeeded,
                        Available = have,
                    });

                    if (chosen == null && have >= need.StuffNeeded)
                    {
                        chosen = stuff;
                        why = string.Format("首选顺序里第一个够的（需 {0}，有 {1}）", need.StuffNeeded, have);
                    }
                }

                if (chosen != null)
                {
                    alloc.ChosenStuff = chosen;
                    alloc.Allocated = need.StuffNeeded;
                    alloc.Reason = why;
                }
                else
                {
                    // ③ 都凑不够这一段 → 退而求其次：用**手头最多**的那种（0 就用首选顺序第一个）
                    //
                    //    为什么不是"这段不造"：把料留着也不会自己变成房子，
                    //    而"用现有最多的料把这面墙砌起来"才是往前走。
                    //    代价（缺口）**明写进 Short 与 Reason**，绝不静默。
                    string best = null;
                    int bestHave = -1;
                    for (int c = 0; c < candidates.Count; c++)
                    {
                        int have;
                        if (!plan.Remaining.TryGetValue(candidates[c], out have)) have = 0;
                        if (have > bestHave) { bestHave = have; best = candidates[c]; }
                    }
                    if (bestHave <= 0)
                    {
                        alloc.ChosenStuff = null;
                        alloc.Reason = string.Format("这一段需要 {0} 材料，但库存已经全空 —— 不造这一段",
                            need.StuffNeeded);
                    }
                    else
                    {
                        alloc.ChosenStuff = best;
                        alloc.Allocated = bestHave;
                        alloc.Reason = string.Format(
                            "没有任何一种料够这一段（需 {0}）→ 用现有的 {1} 先做（缺 {2}，"
                            + "硬凑会把料占住让小人卡在建造上）",
                            need.StuffNeeded, best, need.StuffNeeded - bestHave);
                    }
                }

                // ④ 从库存里划走
                if (alloc.ChosenStuff != null && alloc.ChosenStuff.Length > 0 && alloc.Allocated > 0)
                {
                    int have;
                    plan.Remaining.TryGetValue(alloc.ChosenStuff, out have);
                    plan.Remaining[alloc.ChosenStuff] = have - alloc.Allocated;
                    plan.TotalAllocated += alloc.Allocated;

                    int usedBefore;
                    plan.Used.TryGetValue(alloc.ChosenStuff, out usedBefore);
                    plan.Used[alloc.ChosenStuff] = usedBefore + alloc.Allocated;
                }

                plan.Stages.Add(alloc);
            }

            // ⑤ 汇总判定
            plan.AnyStageBuildable = false;
            plan.FullyCovered = true;
            int buildableStages = 0;
            for (int i = 0; i < plan.Stages.Count; i++)
            {
                if (plan.Stages[i].CanBuild) { plan.AnyStageBuildable = true; buildableStages++; }
                else plan.FullyCovered = false;
                if (plan.Stages[i].Short > 0) plan.FullyCovered = false;
            }

            var parts = new List<string>();
            for (int i = 0; i < plan.Stages.Count; i++) parts.Add(plan.Stages[i].ToString());
            plan.Summary = string.Format("分段材料（{0}/{1} 段可造，共需 {2}，划走 {3}{4}）：{5}",
                buildableStages, plan.Stages.Count, plan.TotalNeeded, plan.TotalAllocated,
                plan.TotalShort > 0 ? "，缺 " + plan.TotalShort : "",
                string.Join("；", parts.ToArray()));

            return plan;
        }

        /// <summary>候选材料顺序：优先 `preference`，再补 `available` 里其余的（去重）</summary>
        private static List<string> BuildCandidates(Dictionary<string, int> available, IList<string> preference)
        {
            var candidates = new List<string>();
            if (preference != null)
            {
                for (int i = 0; i < preference.Count; i++)
                {
                    string s = preference[i];
                    if (!string.IsNullOrEmpty(s) && !candidates.Contains(s)) candidates.Add(s);
                }
            }
            if (available != null)
            {
                foreach (KeyValuePair<string, int> kv in available)
                {
                    if (kv.Value > 0 && !candidates.Contains(kv.Key)) candidates.Add(kv.Key);
                }
            }
            return candidates;
        }

        /// <summary>
        /// 从**建造纲要**算出每段的材料需求。
        ///
        /// `costPerRole` 由游戏层提供（读 `ThingDef.costStuffCount`，按角色名给；
        /// 读不到时应当返回 1 —— **绝不返回 0**，否则"需要 0"会掩盖缺口）。
        /// </summary>
        public static List<StageNeed> NeedsFrom(BuildOutline outline, BuildTemplate tpl, Func<string, int> costPerRole)
        {
            var list = new List<StageNeed>();
            if (outline == null || tpl == null) return list;

            for (int i = 0; i < outline.Stages.Count; i++)
            {
                BuildOutline.Stage stage = outline.Stages[i];
                var need = new StageNeed
                {
                    Order = stage.Order,
                    Label = stage.Label,
                };

                for (int k = 0; k < stage.Items.Count; k++)
                {
                    BuildOutline.Item item = stage.Items[k];
                    TemplateRole role;
                    if (!tpl.Roles.TryGetValue(item.Symbol, out role)) continue;

                    int cells;
                    need.CellsBySymbol.TryGetValue(item.Symbol, out cells);
                    need.CellsBySymbol[item.Symbol] = cells + item.Cells;

                    if (!role.NeedsStuff)
                    {
                        need.StuffFreeCells += item.Cells;
                        continue;
                    }

                    int per = 1;
                    try { per = costPerRole != null ? costPerRole(role.Name) : 1; }
                    catch (Exception) { per = 1; }
                    if (per <= 0) per = 1;      // ★ 保守：未知成本按 1，不当 0

                    need.StuffNeeded += per * item.Cells;
                    need.StuffCells += item.Cells;
                    if (!need.StuffSymbols.Contains(item.Symbol)) need.StuffSymbols.Add(item.Symbol);
                }

                list.Add(need);
            }
            return list;
        }
    }
}
