using System;
using System.Collections.Generic;

namespace FoodControl.Core
{
    /// <summary>一次待放的蓝图：某个格子上放某个 Def（可能与样板里的"角色"不同 Def）。</summary>
    public sealed class PlannedBlueprint
    {
        /// <summary>地图上的格索引</summary>
        public int CellIndex;
        public int X;
        public int Z;

        /// <summary>样板里的角色代号</summary>
        public char Symbol;

        /// <summary>角色名（wall/door/bed/light…）</summary>
        public string RoleName = "";

        /// <summary>最终选中的**建筑 Def** 名（盖什么：Wall / Door / Bed…）</summary>
        public string DefName = "";

        /// <summary>
        /// 这个建筑**要不要用建造材料（stuff）**。
        ///
        /// ⚠ 这是踩过坑才加的一个区分：**样板里的 `accepts` 是"建筑 Def"，不是"材料"**。
        ///   `accepts: ["Wall"]` 的意思是"这里盖一面墙"，而墙**由木头/钢/石头建成** ——
        ///   材料不在样板里，由游戏层按库存挑。
        ///
        ///   我最初把两者混为一谈，拿 `"Wall"` 去查库存 → 永远查到 0
        ///   → 所有墙槽都判"没货" → 48 个槽位全放不下。
        /// </summary>
        public bool NeedsStuff;

        /// <summary>
        /// 如果**不需要 stuff**（床/灯这类本身就是成品），那就必须库存里
        /// 有**这个 Def 的物品**才能盖（比如 1 个 `Bed` 物品 → 1 张床蓝图）。
        /// </summary>
        public bool NeedsDefAsItem;

        /// <summary>
        /// 该格上**已有的东西是什么类别**（供"共享墙复用"判断用）。
        /// 放在 `PlannedBlueprint` 里是为了让游戏层填一次、Core 反复判。
        /// </summary>
        public TemplateRole.ExistingKind Existing = TemplateRole.ExistingKind.Empty;

        /// <summary>旋转（0..3），只对需要朝向的东西有意义</summary>
        public int Rotation;

        public override string ToString()
        {
            return string.Format("{0}({1},{2}) {3}->{4}{5}", RoleName, X, Z, Symbol, DefName,
                NeedsStuff ? " +stuff" : (NeedsDefAsItem ? " (as item)" : ""));
        }
    }

    /// <summary>
    /// **共享复用的格子** —— 那一格已经有同类的墙/门了，所以**不重复放蓝图**。
    ///
    /// 用户 2026-10-03：「两个房子连在一起，中间的墙是可以复用的……
    /// 能复用的话少一面墙，而且更像一个整体。」
    /// </summary>
    public sealed class ReusedSlot
    {
        public int X;
        public int Z;
        public char Symbol;
        public string RoleName = "";
        /// <summary>复用的是已有的什么东西（`ExistingKind` 的名字）</summary>
        public string Existing = "";

        public override string ToString()
        {
            return string.Format("({0},{1}) {2} 复用了已有的 {3}", X, Z, RoleName, Existing);
        }
    }

    /// <summary>放样板时的"没找到可用 Def"记录 —— 不整个放弃，而是留空并说明。</summary>
    public sealed class UnresolvedSlot
    {
        public int X;
        public int Z;
        public char Symbol;
        public string RoleName = "";
        /// <summary>为什么没放（比如"候选都没库存" / "格子上有东西挡住"）</summary>
        public string Reason = "";

        public override string ToString()
        {
            return string.Format("({0},{1}) {2}：{3}", X, Z, RoleName, Reason);
        }
    }

    /// <summary>把样板解析成"每格放什么"的结果。</summary>
    public sealed class TemplatePlacement
    {
        public string TemplateId = "";
        /// <summary>放到地图上时，样板左上角对应的原点是这个</summary>
        public int OriginX;
        public int OriginZ;
        public int Rotation;
        public bool Mirrored;

        public List<PlannedBlueprint> Blueprints = new List<PlannedBlueprint>();
        public List<UnresolvedSlot> Unresolved = new List<UnresolvedSlot>();

        /// <summary>
        /// **共享复用的格子**（已有同类的墙/门，所以没重复放蓝图）。
        /// 用户 2026-10-03 提的"两个房子连在一起，中间的墙可以复用"。
        /// </summary>
        public List<ReusedSlot> Reused = new List<ReusedSlot>();

        /// <summary>各角色的计划格数（校验用）</summary>
        public Dictionary<string, int> PlannedByRole = new Dictionary<string, int>();

        /// <summary>
        /// **诊断**：每一格"被怎么处理了"的分类计数。
        /// 排查"为什么全解决不了"时，这个字典是第一现场。
        /// </summary>
        public Dictionary<string, int> SkippedByReason = new Dictionary<string, int>();

        /// <summary>
        /// **诊断**：`PlanPass` 实际看到的前几条槽位数据（局部坐标 → 绝对坐标 → 可放结论）。
        /// 排查"全都放不下"时，这个列表能直接证明它到底查了哪些格。
        /// </summary>
        public List<string> PassSamples = new List<string>();

        public int WallCount
        {
            get { return CountRole("wall"); }
        }

        public int DoorCount
        {
            get { return CountRole("door"); }
        }

        public int CountRole(string roleName)
        {
            int n;
            return PlannedByRole.TryGetValue(roleName, out n) ? n : 0;
        }

        public int TotalSlots
        {
            get { return Blueprints.Count + Unresolved.Count; }
        }

        public override string ToString()
        {
            return string.Format("样板 {0} @({1},{2}) rot={3} mir={4}：已定 {5} 格，未解决 {6} 格",
                TemplateId, OriginX, OriginZ, Rotation, Mirrored, Blueprints.Count, Unresolved.Count);
        }
    }

    /// <summary>某格能不能放（由游戏层判定后填进来）。tiles 按模板尺寸给出。</summary>
    public sealed class PlacementGrid
    {
        public int Width;
        public int Height;

        /// <summary>该格能不能放（地表可承重 + 没被占）</summary>
        public bool[] CanPlace = new bool[0];

        /// <summary>该格是墙类/实体（放门/墙时这里被占没关系，因为要拆掉重建）</summary>
        public bool[] BlockedBySolid = new bool[0];

        /// <summary>
        /// 索引/边界逻辑**只有一份实现**（`GridIndex`）。
        /// 这里用属性转发而不是每次 new 一个：`GridIndex` 是 readonly struct，开销可忽略。
        /// </summary>
        private GridIndex Bounds
        {
            get { return new GridIndex(Width, Height); }
        }

        public bool InBounds(int x, int z)
        {
            return Bounds.InBounds(x, z);
        }

        /// <summary>越界返回 -1（与 `GridIndex.Index` 一致）</summary>
        public int Index(int x, int z)
        {
            return Bounds.Index(x, z);
        }

        public bool IsPlaceable(int x, int z)
        {
            return Bounds.At(CanPlace, x, z, false);
        }
    }

    /// <summary>
    /// **从样板算出"每格放什么"**。纯逻辑，可离线测试。
    ///
    /// ## 它解决的核心问题
    ///
    /// 用户的要求是「**不必百分百还原，只要物块对得上**」：
    /// 样板里的床槽可以用任何床、光源槽可以用任何光源。
    /// 所以这一步要做的是**角色 → 具体 Def 的解析**，而且要考虑：
    ///   - 候选 Def 有没有**库存**（没料放了也建不成）
    ///   - 该格**能不能放**（地表/占用）
    ///
    /// ## 三条"不放弃"原则（都是用户场景决定的）
    ///
    /// 1. **单格失败不拖垮整体**：某个床槽找不到可用床 → 那个格子留空并记录，
    ///    其余照放。样板大（13×13）时"因为一个床没料就整间不盖"是荒唐的。
    /// 2. **墙/门优先**：它们是"房子成不成立"的关键。如果料到后面不够，
    ///    宁可少放家具，也要保证外墙和门放完（否则盖出来是个漏风的壳）。
    /// 3. **旋转/镜像要能表达**：同一份样板放在不同朝向的地形上。
    /// </summary>
    public static class TemplatePlacer
    {
        /// <summary>
        /// 解析一份样板，产出逐格计划。
        /// </summary>
        /// <param name="tpl">样板（已旋转/镜像处理过的最终形态）</param>
        /// <param name="grid">放置点的可放性（尺寸与 tpl 一致）</param>
        /// <param name="originX">样板左上角在地图上的 x</param>
        /// <param name="originZ">同上 z</param>
        /// <param name="available">当前可用的材料/物品数量：defName -> 数量</param>
        /// <param name="rotation">记录用途（实际旋转已在 tpl 里做好）</param>
        /// <param name="mirrored">记录用途</param>
        /// <param name="existingAt">
        /// **每一格上已有什么**（地图坐标 → 类别）。传 null 表示"全是空地"（不启用共享墙复用）。
        ///
        /// 这是"两个房子连在一起时中间的墙可以复用"（用户 2026-10-03）的入口：
        /// 重合且**同类**的格会被跳过（不重复放蓝图），从而少一面墙、且两间房更像个整体。
        /// ⚠ 框架（Frame）**不算可复用**，见 `TemplateRole.CanReuse`。
        /// </param>
        public static TemplatePlacement Plan(
            BuildTemplate tpl,
            PlacementGrid grid,
            int originX,
            int originZ,
            Dictionary<string, int> available,
            int rotation,
            bool mirrored,
            Func<int, int, TemplateRole.ExistingKind> existingAt = null)
        {
            var result = new TemplatePlacement
            {
                TemplateId = tpl != null ? tpl.Id : "",
                OriginX = originX,
                OriginZ = originZ,
                Rotation = rotation,
                Mirrored = mirrored,
            };
            if (tpl == null || grid == null) return result;

            tpl.NormalizeSize();

            // 已用掉的量（同一种 Def 在多个槽位上要分摊库存）
            var used = new Dictionary<string, int>();

            // ★ 诊断：统计"为什么某些格被跳过"。
            //   没有这个统计时，"48 个槽位全解决不了"会毫无线索 —— 我排查时就在这里卡住过。
            result.SkippedByReason = new Dictionary<string, int>();
            for (int z = 0; z < tpl.Height; z++)
            {
                for (int x = 0; x < tpl.Width; x++)
                {
                    char sym = tpl.SymbolAt(x, z);
                    if (sym == '\0') { Bump(result, "空地（样板里就是 '.'）"); continue; }
                    TemplateRole r;
                    if (!tpl.Roles.TryGetValue(sym, out r))
                    {
                        Bump(result, "代号 '" + sym + "' 不在 Roles 表里");
                        continue;
                    }
                    Bump(result, "会用角色 " + r.Name + "（'" + sym + "'）");
                }
            }

            // ★ 两遍：先墙/门（房子的骨架），再家具。
            //   理由见类注释第 2 条：料不够时宁可少家具，也要先把壳立起来。
            PlanPass(tpl, grid, originX, originZ, available, used, result, true, existingAt);
            PlanPass(tpl, grid, originX, originZ, available, used, result, false, existingAt);

            return result;
        }

        private static void Bump(TemplatePlacement r, string key)
        {
            if (r == null || key == null) return;
            if (r.SkippedByReason == null) r.SkippedByReason = new Dictionary<string, int>();
            int n;
            r.SkippedByReason[key] = r.SkippedByReason.TryGetValue(key, out n) ? n + 1 : 1;
        }

        private static void PlanPass(
            BuildTemplate tpl, PlacementGrid grid, int originX, int originZ,
            Dictionary<string, int> available, Dictionary<string, int> used,
            TemplatePlacement result, bool structuralPass,
            Func<int, int, TemplateRole.ExistingKind> existingAt)
        {
            for (int z = 0; z < tpl.Height; z++)
            {
                for (int x = 0; x < tpl.Width; x++)
                {
                    char sym = tpl.SymbolAt(x, z);
                    if (sym == '\0') continue;

                    TemplateRole role;
                    if (!tpl.Roles.TryGetValue(sym, out role)) continue;

                    bool isStructural = role.IsWallLike || role.IsDoorLike;
                    if (structuralPass != isStructural) continue;   // 这一遍不管它

                    int mx = originX + x;
                    int mz = originZ + z;

                    // ① 能不能放
                    //
                    // ⚠ **必须用地图绝对坐标（mx,mz），不能用模板局部坐标（x,z）**。
                    //   `PlacementGrid` 是**整张地图**尺寸的，`IsPlaceable` 的入参是地图坐标。
                    //   我最初传了局部坐标 → 它去查了**地图左上角**那一片能不能放，
                    //   而不是真实站点 → 48 个槽位全部误判为"放不下"
                    //   （而 `FindBestSite` 传的是 `tplOriginX + x`，所以它成功 —— 两边不一致才暴露出来）。
                    // ①b ★ **共享墙复用**：那一格已经有同类的墙/门了 → 跳过，不重复放
                    //
                    // 用户 2026-10-03：「如果你要把两个房子连在一起，
                    // 那么他们中间的墙是可以复用的……能复用的话少一面墙，
                    // 然后它其实也更像一个整体。」
                    //
                    // ⚠ 判断必须**同类才算可复用**（`TemplateRole.CanReuse`）：
                    //   `wall↔wall` 可以，`wall↔door` **绝对不行** ——
                    //   把墙当门跳过 → 墙上多了个洞，房子不封闭，屋顶不会长（危房）。
                    //   框架（Frame）也不算，理由见 `CanReuse` 的注释。
                    if (existingAt != null)
                    {
                        TemplateRole.ExistingKind existing = TemplateRole.ExistingKind.Empty;
                        try { existing = existingAt(mx, mz); }
                        catch (Exception) { existing = TemplateRole.ExistingKind.Empty; }

                        if (TemplateRole.CanReuse(role, existing))
                        {
                            result.Reused.Add(new ReusedSlot
                            {
                                X = mx, Z = mz, Symbol = sym, RoleName = role.Name,
                                Existing = existing.ToString(),
                            });
                            Bump(result, "共享复用（" + role.Name + " 复用了已有的 " + existing + "）");
                            continue;
                        }

                        // 已有东西但**不能**复用 → 一定要记下来（否则会静默盖出危房）
                        if (existing != TemplateRole.ExistingKind.Empty)
                        {
                            result.Unresolved.Add(new UnresolvedSlot
                            {
                                X = mx, Z = mz, Symbol = sym, RoleName = role.Name,
                                Reason = string.Format(
                                    "该格 ({0},{1}) 已有 {2}，但样板要放 {3} —— 不能复用（会造出危房）",
                                    mx, mz, existing, role.Name),
                            });
                            Bump(result, "已有 " + existing + " 与要放的 " + role.Name + " 冲突");
                            continue;
                        }
                    }

                    if (!grid.IsPlaceable(mx, mz))
                    {
                        if (result.PassSamples.Count < 12)
                        {
                            result.PassSamples.Add(string.Format(
                                "'{0}'({1},{2})→({3},{4}) 否 structuralPass={5} isStructural={6}",
                                sym, x, z, mx, mz, structuralPass, isStructural));
                        }
                        result.Unresolved.Add(new UnresolvedSlot
                        {
                            X = mx, Z = mz, Symbol = sym, RoleName = role.Name,
                            Reason = string.Format("该格 ({0},{1}) 放不下（地表不可承重 或 已被占用）", mx, mz),
                        });
                        continue;
                    }

                    // ② 挑建筑 Def —— **以及分清"它要不要材料"**
                    //
                    // ⚠ 这是踩过坑的地方（见 PlannedBlueprint.NeedsStuff 的注释）：
                    //   墙/门是**由材料建成**的（stuff），所以"库存里有没有 Wall"是无意义的问题；
                    //   而床/灯多数是**成品**，"库存里有没有 Bed"才有意义。
                    //   两种角色的"有没有货"判据完全不同，不能混。
                    string chosen = null;
                    string why = null;
                    for (int i = 0; i < role.Accepts.Count; i++)
                    {
                        string def = role.Accepts[i];
                        if (role.NeedsStuff)
                        {
                            // 需要材料：**不检查这个 Def 的库存**（它不是物品），
                            // 材料够不够由游戏层挑 stuff 时判断
                            chosen = def;
                            break;
                        }

                        // 成品：库存里要有这个 Def 的物品
                        int have;
                        if (!available.TryGetValue(def, out have)) have = 0;
                        int alreadyUsed;
                        if (!used.TryGetValue(def, out alreadyUsed)) alreadyUsed = 0;

                        if (have - alreadyUsed > 0)
                        {
                            chosen = def;
                            break;
                        }
                        if (why == null) why = "候选 " + def + " 库存 0";
                    }

                    if (chosen == null)
                    {
                        result.Unresolved.Add(new UnresolvedSlot
                        {
                            X = mx, Z = mz, Symbol = sym, RoleName = role.Name,
                            Reason = role.Accepts.Count == 0
                                ? "角色没有配置任何候选 Def"
                                : ("所有候选都没货（" + why + "）"),
                        });
                        continue;
                    }

                    // ③ 记账（只对"成品"角色扣库存；需材料的角色不扣）
                    if (!role.NeedsStuff)
                    {
                        int u;
                        used[chosen] = used.TryGetValue(chosen, out u) ? u + 1 : 1;
                    }

                    string rn;
                    result.Blueprints.Add(new PlannedBlueprint
                    {
                        CellIndex = grid.Index(mx, mz),
                        X = mx, Z = mz,
                        Symbol = sym,
                        RoleName = role.Name,
                        DefName = chosen,
                        NeedsStuff = role.NeedsStuff,
                        NeedsDefAsItem = !role.NeedsStuff,
                    });

                    rn = role.Name ?? "";
                    int c;
                    result.PlannedByRole[rn] = result.PlannedByRole.TryGetValue(rn, out c) ? c + 1 : 1;
                }
            }
        }

        /// <summary>
        /// **找一处能放下这份样板的位置**（纯几何，不碰游戏）。
        ///
        /// 判据：样板的**每一个非空格**都必须在 `canPlace[x][z]` 为 true
        /// （即地表能承重、且没被别的东西占）。
        /// 空的格不要求 —— 因为样板里"空地"本来就允许是任何东西。
        ///
        /// 返回 null 表示没找到。**搜索顺序**：从距离场值最小（离基地最近）的地方开始，
        /// 所以传入的 distField 决定了"优先盖在哪"。
        /// </summary>
        public static bool FindSite(
            BuildTemplate tpl,
            PlacementGrid mapGrid,
            int tplOriginX, int tplOriginZ,
            double[] distField, int mapWidth,
            out double avgDistance)
        {
            avgDistance = double.MaxValue;
            if (tpl == null || mapGrid == null) return false;
            tpl.NormalizeSize();

            if (tplOriginX < 0 || tplOriginZ < 0) return false;
            if (tplOriginX + tpl.Width > mapGrid.Width) return false;
            if (tplOriginZ + tpl.Height > mapGrid.Height) return false;

            double sum = 0;
            int n = 0;

            for (int z = 0; z < tpl.Height; z++)
            {
                for (int x = 0; x < tpl.Width; x++)
                {
                    char sym = tpl.SymbolAt(x, z);
                    if (sym == '\0') continue;                 // 样板里是空地 —— 不要求

                    if (!mapGrid.IsPlaceable(tplOriginX + x, tplOriginZ + z)) return false;

                    if (distField != null)
                    {
                        int mi = (tplOriginZ + z) * mapWidth + (tplOriginX + x);
                        if (mi >= 0 && mi < distField.Length)
                        {
                            double d = distField[mi];
                            if (double.IsInfinity(d)) return false;   // 走不到
                            sum += d;
                            n++;
                        }
                    }
                }
            }

            avgDistance = n > 0 ? sum / n : 0;
            return true;
        }

        /// <summary>
        /// 在整张图上滑窗找**最好的**放置点（离基地最近的可行位置）。
        /// 纯几何，可离线测试。
        /// </summary>
        public static bool FindBestSite(
            BuildTemplate tpl,
            PlacementGrid mapGrid,
            double[] distField,
            int mapWidth,
            int mapHeight,
            BuildSiteOptions opts,
            out int bestX, out int bestZ, out double bestAvgDist)
        {
            bestX = bestZ = -1;
            bestAvgDist = double.MaxValue;
            if (tpl == null || mapGrid == null) return false;
            tpl.NormalizeSize();

            int step = (opts != null && opts.SearchStep > 0) ? opts.SearchStep : 1;
            double maxDist = opts != null ? opts.MaxDistanceFromBase : 120.0;
            bool found = false;

            for (int z = 0; z + tpl.Height <= mapHeight; z += step)
            {
                for (int x = 0; x + tpl.Width <= mapWidth; x += step)
                {
                    double avg;
                    if (!FindSite(tpl, mapGrid, x, z, distField, mapWidth, out avg)) continue;
                    if (avg > maxDist) continue;

                    if (avg < bestAvgDist)
                    {
                        bestAvgDist = avg;
                        bestX = x;
                        bestZ = z;
                        found = true;
                    }
                }
            }

            return found;
        }

        /// <summary>
        /// **整份样板一次放下去需要多少料**（按角色的首选 Def 算）。
        /// 可用来在放之前判断"料够不够一趟做完"。
        /// </summary>
        public static Dictionary<string, int> RequiredMaterials(BuildTemplate tpl)
        {
            var need = new Dictionary<string, int>();
            if (tpl == null) return need;
            tpl.NormalizeSize();

            for (int z = 0; z < tpl.Height; z++)
            {
                for (int x = 0; x < tpl.Width; x++)
                {
                    char sym = tpl.SymbolAt(x, z);
                    if (sym == '\0') continue;

                    TemplateRole role;
                    if (!tpl.Roles.TryGetValue(sym, out role)) continue;
                    if (role.Accepts.Count == 0) continue;

                    string first = role.Accepts[0];
                    int n;
                    need[first] = need.TryGetValue(first, out n) ? n + 1 : 1;
                }
            }
            return need;
        }
    }
}
