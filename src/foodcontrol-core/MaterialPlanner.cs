using System;
using System.Collections.Generic;

namespace FoodControl.Core
{
    /// <summary>一种候选材料的评估结果。</summary>
    public sealed class MaterialOption
    {
        public string StuffDefName = "";
        /// <summary>用这种料建完整栋需要多少</summary>
        public int Needed;
        /// <summary>地图上可搬运的有多少</summary>
        public int Available;
        /// <summary>够不够（`Available >= Needed`）</summary>
        public bool Enough
        {
            get { return Available >= Needed; }
        }
        /// <summary>差多少（够时为 0）</summary>
        public int Short
        {
            get { return Enough ? 0 : Needed - Available; }
        }

        public override string ToString()
        {
            return string.Format("{0}: 需{1} 有{2} {3}", StuffDefName, Needed, Available,
                Enough ? "✓" : "缺" + Short);
        }
    }

    /// <summary>
    /// **建造前的材料评估结果**。
    ///
    /// 用户 2026-10-03：
    /// 「所有人就卡在那了……其实钢铁这个资源是够用的。当时如果说就是在建造之前
    ///  **事先评估过这个材料够不够用**，那么可能就会**从木头转向钢铁**。」
    /// </summary>
    public sealed class MaterialPlan
    {
        /// <summary>最终选定的材料（**null = 都不够，不要造**）</summary>
        public string ChosenStuff;

        /// <summary>
        /// 这份样板**根本不需要材料**（全是成品家具，比如只有床和灯）。
        ///
        /// ⚠ 为什么要单独一个标志：`ChosenStuff` 有三种语义，光看字符串分不清：
        ///   - `null`      → 都不够，**不造**
        ///   - `""` + 本标志为 true → 无需材料，**可以造**
        ///   - `"WoodLog"` → 用木头造
        ///   我第一版用 `IsNullOrEmpty(ChosenStuff)` 判 `CanBuild`，
        ///   于是"无需材料"被误判成"不能造"（测试抓到了）。
        /// </summary>
        public bool NoStuffNeeded;

        /// <summary>选定材料需要多少 / 有多少</summary>
        public int NeededTotal;
        public int AvailableChosen;

        /// <summary>每种候选的评估（按"够不够"和"首选顺序"排）</summary>
        public List<MaterialOption> Options = new List<MaterialOption>();

        /// <summary>能不能造</summary>
        public bool CanBuild
        {
            get { return NoStuffNeeded || !string.IsNullOrEmpty(ChosenStuff); }
        }

        /// <summary>一句话说明（必须能说清"为什么不能造、缺多少"）</summary>
        public string Reason = "";

        /// <summary>需要材料的总格数（诊断用）</summary>
        public int StructuralCells;

        public override string ToString()
        {
            if (CanBuild)
                return string.Format("材料：用 {0}（需 {1}，有 {2}）", ChosenStuff, NeededTotal, AvailableChosen);
            return "材料不足，不造：" + Reason;
        }
    }

    /// <summary>
    /// **建造前的材料评估**。纯逻辑，可离线测试。
    ///
    /// ## 为什么必须"先算整栋"而不是"逐格挑"
    ///
    /// 原来的做法是 `PickStuffFor` **逐格**选材料：放第一格时木头够就选木头，
    /// 放到第 30 格木头没了，那格的代码**还是会选木头**（它只看"还剩多少"，
    /// 不看"够不够整栋"）→ 结果是**半木半钢的壳子**，或者干脆建不完。
    ///
    /// 更糟的是：蓝图会**预留材料**，一堆建不完的蓝图会把料占住，
    /// 让小人**卡在建造上**，别的活都不干 —— 这是**最差的失败方式**。
    ///
    /// ## 判据
    ///
    /// ```
    /// 需要 = Σ(每种结构格的 costStuffCount)     ← 整栋
    /// 够   = 可用数量 >= 需要
    /// ```
    ///
    /// 首选顺序由 `preference` 给出（木头 → 钢 → 石砖…），
    /// **第一个够的就选它**；都不够就 `ChosenStuff = null`（**不造**）。
    /// </summary>
    public static class MaterialPlanner
    {
        /// <summary>
        /// 评估整栋建筑的材料。
        /// </summary>
        /// <param name="tpl">样板</param>
        /// <param name="costPerCell">
        /// 每个结构格要几个材料。由游戏层按 `ThingDef.costStuffCount` 提供。
        /// ⚠ 传 0 或负数会被当成 1（保守：宁可算多，也不要算少了导致"以为够"）。
        /// </param>
        /// <param name="available">
        /// 可搬运的材料数量：stuff defName → 数量。
        /// </param>
        /// <param name="preference">
        /// 首选顺序。为空时用 `Options` 里 `available` 的键顺序（不稳定，所以建议显式给）。
        /// </param>
        public static MaterialPlan Plan(
            BuildTemplate tpl,
            Func<string, int> costPerCell,
            Dictionary<string, int> available,
            IList<string> preference)
        {
            var plan = new MaterialPlan();
            if (tpl == null)
            {
                plan.Reason = "没有样板";
                return plan;
            }
            tpl.NormalizeSize();
            if (available == null) available = new Dictionary<string, int>();

            // ① 数出**需要材料的格子**有哪些角色
            //    （墙/门这类 needsStuff=true；床/灯是成品，不需要材料）
            var stuffRoles = new List<TemplateRole>();
            int structuralCells = 0;
            for (int z = 0; z < tpl.Height; z++)
            {
                for (int x = 0; x < tpl.Width; x++)
                {
                    char sym = tpl.SymbolAt(x, z);
                    if (sym == '\0') continue;
                    TemplateRole r;
                    if (!tpl.Roles.TryGetValue(sym, out r)) continue;
                    if (!r.NeedsStuff) continue;
                    structuralCells++;
                    if (!stuffRoles.Contains(r)) stuffRoles.Add(r);
                }
            }
            plan.StructuralCells = structuralCells;

            if (structuralCells == 0)
            {
                plan.Reason = "这份样板没有需要材料的格子（全是成品家具）";
                plan.NeededTotal = 0;
                plan.NoStuffNeeded = true;   // ★ 可以造（只是不用材料）
                plan.ChosenStuff = "";
                return plan;
            }

            // ② 候选材料：优先用 preference 给的顺序，再补上 available 里其余有货的
            var candidates = new List<string>();
            if (preference != null)
            {
                for (int i = 0; i < preference.Count; i++)
                {
                    string s = preference[i];
                    if (!string.IsNullOrEmpty(s) && !candidates.Contains(s)) candidates.Add(s);
                }
            }
            foreach (var kv in available)
            {
                if (kv.Value > 0 && !candidates.Contains(kv.Key)) candidates.Add(kv.Key);
            }

            if (candidates.Count == 0)
            {
                plan.Reason = "地图上没有任何可用材料";
                return plan;
            }

            // ③ 逐个候选算：需要多少、有多少、够不够
            //
            // 需要量按**角色**分别算（不同建筑的 costStuffCount 可能不同），
            // 由 costPerCell(角色名) 提供。
            for (int i = 0; i < candidates.Count; i++)
            {
                string stuff = candidates[i];

                int need = 0;
                for (int z = 0; z < tpl.Height; z++)
                {
                    for (int x = 0; x < tpl.Width; x++)
                    {
                        char sym = tpl.SymbolAt(x, z);
                        if (sym == '\0') continue;
                        TemplateRole r;
                        if (!tpl.Roles.TryGetValue(sym, out r)) continue;
                        if (!r.NeedsStuff) continue;

                        int c = 1;
                        try { c = costPerCell != null ? costPerCell(r.Name) : 1; }
                        catch (Exception) { c = 1; }
                        if (c <= 0) c = 1;      // ★ 保守：未知成本按 1 算，不要算成 0
                        need += c;
                    }
                }

                int have;
                if (!available.TryGetValue(stuff, out have)) have = 0;

                plan.Options.Add(new MaterialOption
                {
                    StuffDefName = stuff,
                    Needed = need,
                    Available = have,
                });
            }

            // ④ 选第一个够的（按 preference 顺序）
            for (int i = 0; i < plan.Options.Count; i++)
            {
                if (plan.Options[i].Enough)
                {
                    plan.ChosenStuff = plan.Options[i].StuffDefName;
                    plan.NeededTotal = plan.Options[i].Needed;
                    plan.AvailableChosen = plan.Options[i].Available;
                    plan.Reason = string.Format("用 {0}（需 {1}，有 {2}）",
                        plan.ChosenStuff, plan.NeededTotal, plan.AvailableChosen);
                    return plan;
                }
            }

            // ⑤ 都不够 → **不造**，并说清缺多少
            var parts = new List<string>();
            for (int i = 0; i < plan.Options.Count; i++)
                parts.Add(plan.Options[i].ToString());

            plan.ChosenStuff = null;
            plan.NeededTotal = plan.Options.Count > 0 ? plan.Options[0].Needed : 0;
            plan.AvailableChosen = plan.Options.Count > 0 ? plan.Options[0].Available : 0;
            plan.Reason = string.Format(
                "材料不足，整栋要 {0} 个：{1} —— **不造**（硬造会让小人卡在建造上，还不如不造）",
                plan.NeededTotal, string.Join("；", parts.ToArray()));
            return plan;
        }
    }
}
