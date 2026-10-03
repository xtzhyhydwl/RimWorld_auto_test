using System;
using System.Collections.Generic;

namespace FoodControl.Core
{
    /// <summary>
    /// **一个槽位在语义上属于哪一类**。
    ///
    /// ## 为什么需要它（用户 2026-10-03 的原话）
    ///
    /// > 「在这个 13×13 有内容的房间里，在我心目中也是**有建造顺序**的……
    /// >   先把墙都造好……然后把床造了，然后把桌子造了，然后把光源造了，
    /// >   最后才是造娱乐设施。」
    ///
    /// 也就是说，样板**不只是形状**，它还有"这里是什么用途"这一层。
    ///
    /// ## 为什么是枚举而不是继续加 bool
    ///
    /// 提出这个之前，`TemplateRole` 上已经有三个互斥的布尔开关：
    /// `IsWallLike` / `IsDoorLike` / `IsFurniture`。问题在于：
    ///   - 它们**表达不出**"娱乐"（原版 `ChessTable` 国际象棋桌就是这么被漏掉的：
    ///     扫描器只能靠 Def 名字猜，把 `ChessTable` 归成了"餐桌"）
    ///   - 每加一种用途就要加一个 bool，而且**组合出来的一些状态是无意义的**
    ///     （`IsWallLike && IsFurniture`？）→ 这是"加功能要改数据结构"的典型症状
    ///
    /// 枚举 + 一个注册表就不一样了：**加一类用途 = 加一个枚举值 + 往注册表加一条**，
    /// 不动任何流程代码（这正是用户要的"加接口就行"）。
    ///
    /// ⚠ 旧的三个 bool **没有删**：它们仍是"封闭性/复用判定"的输入（见
    /// `TemplateRole.CanReuse`），而且旧样板 JSON 里就有。语义层建在它们**之上**，
    /// 属于"新增一层"，不是"推翻重来"。
    /// </summary>
    public enum SlotKind
    {
        /// <summary>说不清是什么（**要显式说出来，不要瞎猜**）</summary>
        Unknown = 0,

        /// <summary>结构：墙、柱子 —— 决定房子成不成立</summary>
        Structure = 1,

        /// <summary>门（用户把它的优先级**降到结构之后**："造不造其实无所谓"）</summary>
        Door = 2,

        /// <summary>睡眠：床</summary>
        Sleeping = 3,

        /// <summary>进食：餐桌、餐椅</summary>
        Eating = 4,

        /// <summary>娱乐：棋桌、马蹄铁、台球桌……（★ 曾经被漏掉的一类）</summary>
        Entertainment = 5,

        /// <summary>照明：火把、灯</summary>
        Lighting = 6,

        /// <summary>储物：架子、货架</summary>
        Storage = 7,

        /// <summary>生产：工作台、灶台</summary>
        Production = 8,

        /// <summary>医疗：床（医疗）、药柜</summary>
        Medical = 9,
    }

    /// <summary>
    /// **这个槽位放几个** —— 数量策略。
    ///
    /// 用户：
    /// > 「一般来说，**有几个人造几个床**就可以了。」
    ///
    /// 所以样板里的 8 张床是"**最多 8 个位置**"，不是"必须 8 张"：
    /// 3 个殖民者只放 3 张，**其余槽位留空**。
    ///
    /// 这一层和"位置"是正交的：样板给的是**上限与位置**，策略决定**实放几个**。
    /// </summary>
    public enum SlotCountPolicy
    {
        /// <summary>照样板放满（墙、门）</summary>
        Fixed = 0,

        /// <summary>**按殖民者数**放（床）</summary>
        PerColonist = 1,

        /// <summary>至少 1 个（光源、娱乐设施 —— 没有也无所谓，有 1 个就够）</summary>
        AtLeastOne = 2,

        /// <summary>按殖民者数 × 比例（如"2 人 1 桌"用 0.5）</summary>
        PerColonistRatio = 3,

        /// <summary>不主动放，只在该槽位被别的东西需要时才放（保留）</summary>
        Optional = 4,
    }

    /// <summary>
    /// **建造顺序** —— 用户直接给的公式，照抄，不自己发明：
    /// ```
    /// 1. 墙（先！）
    /// 2. 门 —— 用户说"造不造其实无所谓"（★ 门被降级了）
    /// 3. 床
    /// 4. 桌子
    /// 5. 光源
    /// 6. 娱乐设施（最后）
    /// ```
    ///
    /// ⚠ 这是**规矩**，不是"算法算出来的最优"—— 所以它就该是一张表。
    /// 顺序变了就改这张表，不需要动任何流程代码。
    /// </summary>
    public static class BuildOrder
    {
        public const int Structure = 0;
        public const int Door = 1;
        public const int Sleeping = 2;
        public const int Eating = 3;
        public const int Lighting = 4;
        public const int Entertainment = 5;

        /// <summary>没被规定过的类别排在**最后**（宁可晚做，也不要插队打乱墙/门）</summary>
        public const int Unspecified = 900;

        /// <summary>类别 → 建造顺序。**唯一的映射处**。</summary>
        public static int For(SlotKind kind)
        {
            switch (kind)
            {
                case SlotKind.Structure: return Structure;
                case SlotKind.Door: return Door;
                case SlotKind.Sleeping: return Sleeping;
                case SlotKind.Eating: return Eating;
                case SlotKind.Lighting: return Lighting;
                case SlotKind.Entertainment: return Entertainment;
                default: return Unspecified;
            }
        }

        /// <summary>人能读的顺序名（日志/遥测用）</summary>
        public static string NameOf(int order)
        {
            switch (order)
            {
                case Structure: return "墙/结构";
                case Door: return "门";
                case Sleeping: return "床";
                case Eating: return "桌子";
                case Lighting: return "光源";
                case Entertainment: return "娱乐";
                default: return "未规定";
            }
        }
    }

    /// <summary>
    /// **一个槽位的完整语义**：它是什么（`Kind`）、放几个（`Count`）、什么时候放（`Order`）。
    ///
    /// 三个维度刻意分开，因为它们**互相正交**：
    ///   - 墙：`Structure` / `Fixed` / 0
    ///   - 床：`Sleeping` / `PerColonist` / 2
    ///   - 灯：`Lighting` / `AtLeastOne` / 4
    /// 如果把它们揉成一个"角色类型"，就会出现"床-但只有一个"这种要新增枚举值的烂摊子。
    /// </summary>
    public sealed class SlotSemantics
    {
        public SlotKind Kind = SlotKind.Unknown;
        public SlotCountPolicy Count = SlotCountPolicy.Fixed;
        public int Order = BuildOrder.Unspecified;

        /// <summary>比例（仅 `PerColonistRatio` 用）</summary>
        public double Ratio = 1.0;

        /// <summary>本条语义是**怎么定出来的** —— 任何判定都必须能说出原因（项目铁律）</summary>
        public string Why = "";

        /// <summary>是不是"显式声明过的"（`Unknown` 且没声明过 = 需要人去补注册表）</summary>
        public bool IsDeclared;

        public SlotSemantics() { }

        public SlotSemantics(SlotKind kind, SlotCountPolicy count, string why)
        {
            Kind = kind;
            Count = count;
            Order = BuildOrder.For(kind);
            Why = why;
            IsDeclared = true;
        }

        public SlotSemantics WithRatio(double ratio)
        {
            Ratio = ratio;
            return this;
        }

        public override string ToString()
        {
            return string.Format("{0}/{1}/顺序{2}", Kind, Count, Order);
        }
    }

    /// <summary>
    /// **"哪个 Def 属于哪一类"的注册表** —— 语义层的**唯一扩展点**。
    ///
    /// ## 为什么要注册表而不是 `if (defName == "ChessTable")`
    ///
    /// 因为**按名字猜一定会错**。本项目的实例：扫描器把 `ChessTable`（国际象棋桌）
    /// 按字面名字归成了 `table`（餐桌），而它其实是**娱乐设施** ——
    /// 与它配对的 `Stool` + `HorseshoesPin` 合起来是一个娱乐区。
    ///
    /// 真相在 **Def 自己的分类**里（`thingCategories` / `joyKind`），不在名字里。
    /// 所以：**判定数据由游戏层从 Def 读出来填进来**（Game 层能做，Core 不能 ——
    /// Core 不许引用游戏程序集），Core 只负责"拿这份数据做判定"。
    ///
    /// ## 加一类用途要动几个地方（这就是"易更改"的度量）
    ///
    ///   1. `SlotKind` 加一个枚举值
    ///   2. `BuildOrder.For` 给它一个顺序（不想规定就落到 `Unspecified`）
    ///   3. 往注册表加若干 Def
    /// → **不动**任何流程代码、任何 if/else 分支。
    /// </summary>
    public sealed class SlotSemanticRegistry
    {
        private readonly Dictionary<string, SlotKind> _byDef =
            new Dictionary<string, SlotKind>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, SlotCountPolicy> _countByDef =
            new Dictionary<string, SlotCountPolicy>(StringComparer.OrdinalIgnoreCase);

        /// <summary>注册一个 Def 的类别（可覆盖）</summary>
        public void SetKind(string defName, SlotKind kind)
        {
            if (string.IsNullOrEmpty(defName)) return;
            _byDef[defName] = kind;
        }

        /// <summary>注册一类 Def 的类别（批量）</summary>
        public void SetKind(IEnumerable<string> defNames, SlotKind kind)
        {
            if (defNames == null) return;
            foreach (string d in defNames) SetKind(d, kind);
        }

        /// <summary>单独规定某个 Def 的数量策略（不给就用类别默认）</summary>
        public void SetCountPolicy(string defName, SlotCountPolicy policy)
        {
            if (string.IsNullOrEmpty(defName)) return;
            _countByDef[defName] = policy;
        }

        public bool TryGetKind(string defName, out SlotKind kind)
        {
            kind = SlotKind.Unknown;
            if (string.IsNullOrEmpty(defName)) return false;
            return _byDef.TryGetValue(defName, out kind);
        }

        public bool TryGetCountPolicy(string defName, out SlotCountPolicy policy)
        {
            policy = SlotCountPolicy.Fixed;
            if (string.IsNullOrEmpty(defName)) return false;
            return _countByDef.TryGetValue(defName, out policy);
        }

        public int Count
        {
            get { return _byDef.Count; }
        }

        /// <summary>注册表里全部条目（诊断/遥测用）</summary>
        public List<KeyValuePair<string, SlotKind>> Entries()
        {
            var list = new List<KeyValuePair<string, SlotKind>>(_byDef);
            list.Sort(delegate (KeyValuePair<string, SlotKind> a, KeyValuePair<string, SlotKind> b)
            {
                return string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase);
            });
            return list;
        }

        /// <summary>
        /// **默认注册表**：最小、真正中立的兜底。
        ///
        /// ⚠ 这里**刻意只放原版最稳定、最不会改名的那几个 Def**。
        ///   完整的数据应当由游戏层从 Def 真实分类（`thingCategories` / `joyKind`）
        ///   生成后 `SetKind` 进来 —— 那才是"查分类"而不是"猜名字"。
        ///   把一大堆 Def 硬编码在这里，就又把"名字匹配"请回来了。
        /// </summary>
        public static SlotSemanticRegistry CreateDefault()
        {
            var r = new SlotSemanticRegistry();

            // 结构类（由材料建的，`NeedsStuff`)
            r.SetKind(new string[] { "Wall", "Door", "Autodoor" }, SlotKind.Structure);
            // 门单独归 Door：用户把门降级到结构之后
            r.SetKind("Door", SlotKind.Door);
            r.SetKind("Autodoor", SlotKind.Door);

            // 睡眠
            r.SetKind(new string[] { "Bed", "DoubleBed", "RoyalBed", "Bedroll" }, SlotKind.Sleeping);

            // 进食
            r.SetKind(new string[] { "Table2x2c", "Table3x3c", "Table2x4c", "Table1x2c" }, SlotKind.Eating);

            // 娱乐 ★ 这一条就是 `ChessTable` 那次教训的固化
            r.SetKind(new string[] { "ChessTable", "HorseshoesPin", "BilliardsTable", "PokerTable" },
                SlotKind.Entertainment);

            // 照明
            r.SetKind(new string[] { "TorchLamp", "StandingLamp", "SunLamp" }, SlotKind.Lighting);

            // 数量：床按人头、灯和娱乐至少一个
            r.SetCountPolicy("Bed", SlotCountPolicy.PerColonist);
            r.SetCountPolicy("DoubleBed", SlotCountPolicy.PerColonist);
            r.SetCountPolicy("Bedroll", SlotCountPolicy.PerColonist);
            r.SetCountPolicy("TorchLamp", SlotCountPolicy.AtLeastOne);
            r.SetCountPolicy("StandingLamp", SlotCountPolicy.AtLeastOne);
            r.SetCountPolicy("ChessTable", SlotCountPolicy.AtLeastOne);
            r.SetCountPolicy("HorseshoesPin", SlotCountPolicy.AtLeastOne);

            return r;
        }
    }

    /// <summary>
    /// **语义解析器**：给一个 `TemplateRole`，说出"它是什么、放几个、第几个放"。
    ///
    /// 判定顺序（**每一步都记在 `Why` 里，绝不能只给结论**）：
    ///   1. 角色**自己声明过** → 用它的（用户在样板里显式写了，最高优先）
    ///   2. **注册表**里能查到它的某个 `Accepts` → 用注册表的（游戏层从 Def 真实分类填的）
    ///   3. 退回**旧布尔开关**（`IsWallLike`/`IsDoorLike`/`IsFurniture`）→ 兼容老样板
    ///   4. 都不行 → `Unknown`，并说明"没查到，需要补注册表"
    ///
    /// ★ 第 4 步**不许猜**。宁可说"我不知道这是什么"，也不要猜成餐桌 ——
    ///   猜错的后果是用户拿到一份**语义错误的样板**。
    /// </summary>
    public static class SlotSemanticsResolver
    {
        public static SlotSemantics Resolve(TemplateRole role, SlotSemanticRegistry registry)
        {
            if (role == null) return new SlotSemantics(SlotKind.Unknown, SlotCountPolicy.Fixed, "角色为 null");

            // ① 角色自己声明过
            if (role.Kind != SlotKind.Unknown)
            {
                var s = new SlotSemantics(role.Kind, role.CountPolicy,
                    string.Format("角色 '{0}' 自己声明了 kind={1}", role.Name, role.Kind));
                if (role.CountPolicy == SlotCountPolicy.PerColonistRatio) s.Ratio = role.CountRatio;
                return s;
            }

            // ② 注册表（按 Accepts 的顺序查，第一个命中的算）
            if (registry != null && role.Accepts != null)
            {
                for (int i = 0; i < role.Accepts.Count; i++)
                {
                    string def = role.Accepts[i];
                    SlotKind k;
                    if (!registry.TryGetKind(def, out k)) continue;

                    SlotCountPolicy policy;
                    if (!registry.TryGetCountPolicy(def, out policy))
                        policy = DefaultCountPolicy(k);

                    var s = new SlotSemantics(k, policy,
                        string.Format("注册表：Accepts[0..{0}] 命中 {1} → {2}", i, def, k));
                    return s;
                }
            }

            // ③ 旧布尔开关兜底（老样板没有 kind 字段）
            SlotKind legacy = SlotKind.Unknown;
            string why = "";
            if (role.IsWallLike) { legacy = SlotKind.Structure; why = "旧字段 IsWallLike"; }
            else if (role.IsDoorLike) { legacy = SlotKind.Door; why = "旧字段 IsDoorLike"; }
            else if (role.IsFurniture) { legacy = SlotKind.Unknown; why = "旧字段 IsFurniture（但说不出是哪一类家具）"; }

            if (legacy != SlotKind.Unknown)
                return new SlotSemantics(legacy, DefaultCountPolicy(legacy), "兜底：" + why);

            // ④ 说不出就不说
            return new SlotSemantics(SlotKind.Unknown, SlotCountPolicy.Fixed,
                string.Format("没查到角色 '{0}'（Accepts={1}）的语义 —— 需要补注册表",
                    role.Name, role.Accepts == null ? "空" : string.Join("/", role.Accepts.ToArray())));
        }

        /// <summary>类别的默认数量策略</summary>
        public static SlotCountPolicy DefaultCountPolicy(SlotKind kind)
        {
            switch (kind)
            {
                case SlotKind.Structure:
                case SlotKind.Door:
                    return SlotCountPolicy.Fixed;
                case SlotKind.Sleeping:
                    return SlotCountPolicy.PerColonist;
                case SlotKind.Lighting:
                case SlotKind.Entertainment:
                    return SlotCountPolicy.AtLeastOne;
                default:
                    return SlotCountPolicy.Fixed;
            }
        }
    }
}
