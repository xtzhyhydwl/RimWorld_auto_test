using System;
using System.Collections.Generic;

namespace FoodControl.Core
{
    /// <summary>
    /// **建造纲要（build outline）**：把一份样板变成"**按什么顺序、分几段、每段放几个**"。
    ///
    /// ## 它填的是哪个坑
    ///
    /// 用户 2026-10-03 的两句原话定了这个类的存在：
    ///
    /// > 「在这个 13×13 有内容的房间里，在我心目中也是**有建造顺序**的……
    /// >   先把墙都造好，然后把床造了，然后把桌子造了，然后把光源造了，
    /// >   最后才是造娱乐设施。」
    ///
    /// > 「一般**有几个人造几个床**就可以了。」
    ///
    /// 在这之前，"顺序"只存在于**注释和口头约定**里（墙门优先那几条），
    /// 代码里没有任何地方**表达**它 —— 所以"按顺序建造"没法被测试、也没法被复用。
    /// 现在它是一份**数据**：`Stages` 是排好序的段，每段里有它要放的格子。
    ///
    /// ## 三个概念的层次（不要混）
    ///
    /// ```
    /// 模板几何   BuildTemplate      哪些格放什么符号（形状）
    ///      ↓
    /// 语义       SlotSemantics      这个符号是"什么用途/放几个"（TemplateSemantics.cs）
    ///      ↓
    /// 纲要       BuildOutline       按用途归并成有序的建造段，算出实放数量（本文件）
    ///      ↓
    /// 材料       MaterialStagedPlanner  逐段决定用什么料（MaterialStages.cs）
    /// ```
    ///
    /// 纯逻辑，不依赖游戏，可离线测试。
    /// </summary>
    public sealed class BuildOutline
    {
        /// <summary>按 `Order` 排好序的建造段</summary>
        public List<Stage> Stages = new List<Stage>();

        public int TotalCells;
        public int TotalToPlace;

        /// <summary>出现的次序（诊断用）</summary>
        public string OrderSummary = "";

        public Stage Find(int order)
        {
            for (int i = 0; i < Stages.Count; i++)
                if (Stages[i].Order == order) return Stages[i];
            return null;
        }

        public override string ToString()
        {
            return string.Format("{0} 段 / {1} 格（实放 {2}）：{3}",
                Stages.Count, TotalCells, TotalToPlace, OrderSummary);
        }

        /// <summary>一个建造段（= 一种用途的所有格子）</summary>
        public sealed class Stage
        {
            /// <summary>建造顺序（越小越先）—— 见 `BuildOrder`</summary>
            public int Order = BuildOrder.Unspecified;

            public SlotKind Kind = SlotKind.Unknown;

            /// <summary>人能读的段名（"墙/结构" / "床" …）</summary>
            public string Label = "";

            /// <summary>这一段里的条目（按角色分）</summary>
            public List<Item> Items = new List<Item>();

            public int CellCount;
            public int ToPlace;

            public override string ToString()
            {
                return string.Format("{0}({1}) {2}格/放{3}", Label, Order, CellCount, ToPlace);
            }
        }

        /// <summary>段里的一个条目（= 样板里的一个角色）</summary>
        public sealed class Item
        {
            public char Symbol;
            public string RoleName = "";
            public SlotKind Kind = SlotKind.Unknown;
            public SlotCountPolicy CountPolicy = SlotCountPolicy.Fixed;

            /// <summary>样板里这个角色有多少格</summary>
            public int Cells;

            /// <summary>实际要放几个（数量策略算出来的）</summary>
            public int ToPlace;

            public override string ToString()
            {
                return string.Format("{0}×{1}(格{2})", RoleName, ToPlace, Cells);
            }
        }

        /// <summary>
        /// **从样板建纲要**。
        /// </summary>
        /// <param name="tpl">样板</param>
        /// <param name="registry">语义注册表（可为 null，那就只有角色自声明的语义可用）</param>
        /// <param name="colonists">殖民者数（`PerColonist` 类策略要用）</param>
        public static BuildOutline Build(BuildTemplate tpl, SlotSemanticRegistry registry, int colonists)
        {
            var outline = new BuildOutline();
            if (tpl == null) return outline;
            tpl.NormalizeSize();

            // ① 逐格统计：每个符号有几格
            var cellsBySymbol = new Dictionary<char, int>();
            for (int z = 0; z < tpl.Height; z++)
            {
                for (int x = 0; x < tpl.Width; x++)
                {
                    char c = tpl.SymbolAt(x, z);
                    if (c == '\0') continue;
                    int n;
                    cellsBySymbol[c] = cellsBySymbol.TryGetValue(c, out n) ? n + 1 : 1;
                    outline.TotalCells++;
                }
            }

            // ② 每个符号 → 语义 → 归入对应的段
            var byOrder = new Dictionary<int, Stage>();
            foreach (KeyValuePair<char, int> kv in cellsBySymbol)
            {
                TemplateRole role;
                if (!tpl.Roles.TryGetValue(kv.Key, out role)) continue;   // 未定义代号：校验阶段已报错

                SlotSemantics sem = SlotSemanticsResolver.Resolve(role, registry);
                int howMany = HowManyToPlace(sem, kv.Value, colonists);

                Stage stage;
                if (!byOrder.TryGetValue(sem.Order, out stage))
                {
                    stage = new Stage
                    {
                        Order = sem.Order,
                        Kind = sem.Kind,
                        Label = BuildOrder.NameOf(sem.Order),
                    };
                    byOrder[sem.Order] = stage;
                    outline.Stages.Add(stage);
                }

                stage.Items.Add(new Item
                {
                    Symbol = kv.Key,
                    RoleName = role.Name,
                    Kind = sem.Kind,
                    CountPolicy = sem.Count,
                    Cells = kv.Value,
                    ToPlace = howMany,
                });
                stage.CellCount += kv.Value;
                stage.ToPlace += howMany;
                outline.TotalToPlace += howMany;
            }

            // ③ 段内条目按符号排序（结果稳定、可 diff）
            for (int i = 0; i < outline.Stages.Count; i++)
            {
                outline.Stages[i].Items.Sort(delegate (Item a, Item b) { return a.Symbol.CompareTo(b.Symbol); });
            }

            // ④ 段按建造顺序排序（★ 这就是"先墙后床"的落地处）
            outline.Stages.Sort(delegate (Stage a, Stage b) { return a.Order.CompareTo(b.Order); });

            var names = new List<string>();
            for (int i = 0; i < outline.Stages.Count; i++) names.Add(outline.Stages[i].ToString());
            outline.OrderSummary = string.Join(" → ", names.ToArray());

            return outline;
        }

        /// <summary>
        /// **实放几个** —— 数量策略的落地处。
        ///
        /// | 策略 | 结果 |
        /// | --- | --- |
        /// | `Fixed` | 照样板放满（墙、门） |
        /// | `PerColonist` | 每个殖民者 1 个，**但不超过样板给的位置数** |
        /// | `AtLeastOne` | 1（样板给了位置的话） |
        /// | `PerColonistRatio` | `ceil(殖民者 × 比例)`，至少 1、不超过位置数 |
        /// | `Optional` | 0（不主动放） |
        ///
        /// ★ 上限一律是**样板里的格数**：样板给的是"最多几个位置"，
        ///   所以 3 个殖民者 + 8 个床槽 = 放 3 张床，**其余槽位留空**（用户原话）。
        /// </summary>
        public static int HowManyToPlace(SlotSemantics sem, int cellsInTemplate, int colonists)
        {
            if (sem == null) return cellsInTemplate;
            if (cellsInTemplate <= 0) return 0;
            if (colonists < 0) colonists = 0;

            int want;
            switch (sem.Count)
            {
                case SlotCountPolicy.Optional:
                    return 0;
                case SlotCountPolicy.PerColonist:
                    want = colonists;
                    break;
                case SlotCountPolicy.PerColonistRatio:
                    want = (int)Math.Ceiling(colonists * (sem.Ratio <= 0 ? 1.0 : sem.Ratio));
                    break;
                case SlotCountPolicy.AtLeastOne:
                    want = 1;
                    break;
                default:     // Fixed
                    want = cellsInTemplate;
                    break;
            }

            // 按人头类的策略至少 1 个（殖民地还没人时也不要算出 0 —— 那会让"要不要盖房"看起来没事可做）
            if (want < 1 && sem.Count != SlotCountPolicy.Optional) want = 1;

            return want > cellsInTemplate ? cellsInTemplate : want;
        }
    }
}
