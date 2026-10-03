using System;
using System.Collections.Generic;
using System.Text;

namespace FoodControl.Core
{
    /// <summary>
    /// **样板（template）的一个槽位角色** —— "这里该放什么"。
    ///
    /// 关键设计（来自用户要求）：
    /// **不必百分百还原，只要"物块对得上"**。
    /// 所以角色是**一类东西**而不是具体的 Def：
    /// 床槽可以放任何床、光源槽可以放任何光源、墙槽可以放任何墙。
    /// </summary>
    public sealed class TemplateRole
    {
        /// <summary>在 rows 里用的单字符代号，如 'W' / 'D' / 'B' / 'L'</summary>
        public char Symbol;

        /// <summary>角色名（人可读），如 "wall" / "door" / "bed" / "light"</summary>
        public string Name = "";

        /// <summary>
        /// **可接受的 Def 列表，按偏好排序**。
        /// 放置时依次尝试：有库存吗 → 能放吗 → 用第一个通过的。
        /// 全都不行 → 这个槽位留空并记录（**不整个放弃**）。
        /// </summary>
        public List<string> Accepts = new List<string>();

        /// <summary>是不是"墙"类（用于判断封闭性）</summary>
        public bool IsWallLike;

        /// <summary>是不是"门"类</summary>
        public bool IsDoorLike;

        /// <summary>是不是家具（放室内，不参与封闭性）</summary>
        public bool IsFurniture;

        // ------------------------------------------------------------------
        // 语义层（2026-10-03 新增）
        //
        // 上面那三个 bool 只够表达"这格能不能复用"，表达不了
        // **"这是什么用途、该放几个、第几个放"**。现在补上这三个维度，
        // 而且是**可选字段**：老样板 JSON 没有它们 → 默认 Unknown
        // → 由 `SlotSemanticsResolver` 去注册表/旧字段兜底。
        // 详见 `TemplateSemantics.cs` 与 `docs/样板语义层-设计.md`。
        // ------------------------------------------------------------------

        /// <summary>
        /// 这个槽位的用途类别。`Unknown` = **没声明**（交给解析器判，不要在这里猜）。
        /// </summary>
        public SlotKind Kind = SlotKind.Unknown;

        /// <summary>放几个。默认 `Fixed` = 照样板放满</summary>
        public SlotCountPolicy CountPolicy = SlotCountPolicy.Fixed;

        /// <summary>比例（仅 `PerColonistRatio` 用）</summary>
        public double CountRatio = 1.0;

        /// <summary>
        /// 这个建筑**要不要建造材料（stuff）**。
        ///
        /// ⚠ 关键区分（踩过坑）：**`Accepts` 是"建筑 Def"，不是"材料"**。
        ///   - 墙/门：**由材料建成** → `NeedsStuff = true`。
        ///     "库存里有没有 Wall"是**无意义的问题**（Wall 不是物品，是由木头/钢建的）。
        ///   - 床/灯：多数是**成品** → `NeedsStuff = false`。
        ///     "库存里有没有 Bed 这个物品"**才有意义**（1 个 Bed 物品 → 1 张床蓝图）。
        ///
        ///   我最初把两者混为一谈，拿 `"Wall"` 去查库存 → 永远 0
        ///   → 所有墙槽判"没货" → 48 个槽位全放不下。
        /// </summary>
        public bool NeedsStuff;

        /// <summary>
        /// 该格上**已有的东西属于哪一类** —— 这是"两个房子连在一起时中间的墙可以复用"
        /// （用户 2026-10-03）所需的输入。
        ///
        /// 匹配规则必须**同类才算能复用**：
        ///   `wall ↔ wall` 可以，`door ↔ door` 可以，**`wall ↔ door` 绝对不行**。
        ///
        /// 为什么：把墙当门跳过 → **墙上多了个洞，房子不封闭，屋顶不会长**（危房）。
        /// </summary>
        public enum ExistingKind
        {
            /// <summary>那里什么都没有</summary>
            Empty = 0,
            /// <summary>已有墙类建筑/蓝图</summary>
            Wall = 1,
            /// <summary>已有门类建筑/蓝图</summary>
            Door = 2,
            /// <summary>已有家具（不能当结构复用）</summary>
            Furniture = 3,
            /// <summary>已有**框架**（未建成的墙）—— ★ 不能当可复用</summary>
            Frame = 4,
            /// <summary>已有别的东西（挡路）</summary>
            Other = 5,
        }

        /// <summary>
        /// 本角色**能不能复用**那一格上已有的东西（即"跳过不放蓝图"）。
        ///
        /// ⚠ **框架（`Frame`）不算可复用**：框架是"还没建成的墙"。
        ///   如果因为那里有框架就跳过，一旦那个框架被取消，这面墙就**永远没建成**
        ///   → 房子缺一面墙。宁可重复放（多花几格料，安全），也不要冒这个风险。
        /// </summary>
        public static bool CanReuse(TemplateRole role, ExistingKind existing)
        {
            if (role == null) return false;
            if (existing == ExistingKind.Empty) return false;
            if (existing == ExistingKind.Frame) return false;    // ★ 框架不算
            if (existing == ExistingKind.Other) return false;

            if (role.IsWallLike) return existing == ExistingKind.Wall;
            if (role.IsDoorLike) return existing == ExistingKind.Door;
            // 家具/工作台这类**不允许**复用：别人的家具不是你的家具
            return false;
        }

        public TemplateRole Clone()
        {
            return new TemplateRole
            {
                Symbol = Symbol,
                Name = Name,
                Accepts = new List<string>(Accepts),
                IsWallLike = IsWallLike,
                IsDoorLike = IsDoorLike,
                IsFurniture = IsFurniture,
                NeedsStuff = NeedsStuff,
                Kind = Kind,
                CountPolicy = CountPolicy,
                CountRatio = CountRatio,
            };
        }
    }

    /// <summary>样板里的一个格子。</summary>
    public sealed class TemplateCell
    {
        public int X;
        public int Z;
        /// <summary>角色代号；'\0' 或空格表示"空地"</summary>
        public char Symbol;
    }

    /// <summary>
    /// **建筑样板**：一份"照抄就能盖"的确定设计。
    ///
    /// ## 为什么需要它（用户 2026-10-03 提出的方向）
    ///
    /// 原本 `BuildSitePlanner` 是**程序化生成**户型 —— 但那是**AI 猜的**。
    /// 用户手上有久经考验的现实设计，所以正确做法是：
    /// **把用户的样板编码下来，需要时直接抄。**
    ///
    /// 好处：不随机、可审查（样板就是一份可读文件）、可复现（逐格确定），
    /// 而且**为"逐步扩张的成熟阵地"留了路** —— 那本质上是有序的样板序列 + 前置条件。
    ///
    /// ## 编码设计
    ///
    /// - **相对网格 + 角色槽位**（不是绝对坐标）→ 能放到地图任何位置
    /// - **按角色匹配，不按具体 Def 匹配** → "床可以是别的床"
    /// - **行字符串**表示形状 → 人能一眼看出、能手画、diff 能看懂
    /// </summary>
    public sealed class BuildTemplate
    {
        public string Id = "";
        public string Name = "";
        /// <summary>user = 用户在游戏里手造并导出；builtin = 仓库自带</summary>
        public string Origin = "user";
        public int Width;
        public int Height;
        public List<string> Tags = new List<string>();

        /// <summary>
        /// **行长不一致**的标记。
        ///
        /// 为什么需要它：`NormalizeSize()` 会按**最长的行**定宽度，
        /// 所以"某行少写了一个字符"这种手写错误**不会**自然暴露 ——
        /// 短的行的右端会被当成空地，于是样板会缺一块墙。
        /// （我写测试时就踩了这个：以为会报错，结果静默通过了。）
        /// </summary>
        public bool RowLengthsDiffer;

        /// <summary>允许 mod 按地形旋转后再放</summary>
        public bool Rotatable = true;
        /// <summary>允许镜像</summary>
        public bool Mirrorable = true;

        /// <summary>
        /// 逐格的角色图。用**行字符串**而不是二维数组：
        /// 13×13 的行字符串在编辑器里直接就能看出形状，可以手画，
        /// 而 `[[0,1,1,0...]]` 不行。这对"人可以审查样板"很重要。
        /// </summary>
        public List<string> Rows = new List<string>();

        /// <summary>角色表：代号 → 角色定义</summary>
        public Dictionary<char, TemplateRole> Roles = new Dictionary<char, TemplateRole>();

        /// <summary>空格/'.' 用来表示空地（两种都接受，'.' 更醒目）</summary>
        public const char EmptyDot = '.';
        public const char EmptySpace = ' ';

        /// <summary>取某格的角色代号；越界或空地返回 '\0'</summary>
        public char SymbolAt(int x, int z)
        {
            if (z < 0 || z >= Rows.Count) return '\0';
            string row = Rows[z];
            if (x < 0 || x >= row.Length) return '\0';
            char c = row[x];
            if (c == EmptyDot || c == EmptySpace) return '\0';
            return c;
        }

        public bool IsEmptyAt(int x, int z)
        {
            return SymbolAt(x, z) == '\0';
        }

        /// <summary>这块样板的实际网格尺寸（从 Rows 推，比声明的 Width/Height 可信）</summary>
        public void NormalizeSize()
        {
            Height = Rows.Count;
            int w = 0;
            bool differ = false;
            for (int i = 0; i < Rows.Count; i++)
            {
                int len = Rows[i] == null ? 0 : Rows[i].Length;
                if (len > w) w = len;
            }
            // 再走一遍判断是否齐长（要在 max 定下来之后）
            for (int i = 0; i < Rows.Count; i++)
            {
                int len = Rows[i] == null ? 0 : Rows[i].Length;
                if (len != w) { differ = true; break; }
            }
            Width = w;
            RowLengthsDiffer = differ;
        }

        /// <summary>
        /// **校验**：形状自洽（尺寸对得上、用到的代号都在 Roles 里、有墙有门）。
        ///
        /// 返回空列表 = 通过；否则是错误说明。
        /// 这一步很重要：样板是**手写/导出的**，可能写错，
        /// 而"抄一个坏样板"会盖出莫名其妙的房子。
        ///
        /// ⚠ 这个重载**只返回错误**（老调用方在用）。要同时拿到警告用 `ValidateDetailed()`。
        /// </summary>
        public List<string> Validate()
        {
            return ValidateDetailed(null).Errors;
        }

        /// <summary>
        /// **带严重级别**的校验：错误（错误到不能抄）与警告（能抄，但值得看一眼）分开。
        ///
        /// ## 为什么必须分级（用户纠正过一次）
        ///
        /// 原来"没有任何门"被当成**错误**。但用户 2026-10-03 明确说
        /// 「**门造不造其实无所谓**」—— 决定"房子成不成立"的是**墙**，门只是通路。
        /// 把可选的东西判成致命错误，会让一个**完全能用的样板**被拒之门外，
        /// 而且错误信息读起来像是"这样板是坏的"，与实际不符。
        ///
        /// 判据因此定死为：
        ///   - **错**：抄了会出问题（尺寸空、代号没定义、`Accepts` 为空、没有墙）
        ///   - **警**：抄了能用，但值得知道（没有门、某些角色说不清用途、定义了没用到）
        /// </summary>
        public TemplateValidation ValidateDetailed(SlotSemanticRegistry registry)
        {
            var errors = new List<string>();
            var warnings = new List<string>();
            NormalizeSize();

            if (Width <= 0 || Height <= 0)
            {
                errors.Add("尺寸为空（Rows 没有内容）");
                return new TemplateValidation(errors, warnings, this);
            }
            if (Width > 120 || Height > 120)
            {
                errors.Add("尺寸过大（" + Width + "x" + Height + "），样板不该超过 120");
            }

            // 行长是否一致
            // ⚠ 必须看 RowLengthsDiffer 这个标记，不能逐行跟 Width 比 ——
            //   Width 是"最长行"的长度，所以短的行会被漏掉（我踩过）。
            if (RowLengthsDiffer)
            {
                for (int z = 0; z < Rows.Count; z++)
                {
                    int len = Rows[z] == null ? 0 : Rows[z].Length;
                    if (len != Width)
                        errors.Add("第 " + z + " 行长度 " + len + " != 宽度 " + Width);
                }
            }
            else
            {
                for (int z = 0; z < Rows.Count; z++)
                {
                    if (Rows[z] == null) errors.Add("第 " + z + " 行是 null");
                }
            }

            // 用到的代号都得有定义
            var used = new HashSet<char>();
            int wallLike = 0, doorLike = 0, furniture = 0;
            for (int z = 0; z < Rows.Count; z++)
            {
                string row = Rows[z];
                if (row == null) continue;
                for (int x = 0; x < row.Length; x++)
                {
                    char c = row[x];
                    if (c == EmptyDot || c == EmptySpace) continue;
                    used.Add(c);

                    TemplateRole r;
                    if (!Roles.TryGetValue(c, out r))
                    {
                        errors.Add("用了代号 '" + c + "' 但 Roles 里没有定义");
                        continue;
                    }
                    if (r.Accepts == null || r.Accepts.Count == 0)
                        errors.Add("角色 '" + c + "' 没有可接受的 Def（Accepts 为空）");
                    if (r.IsWallLike) wallLike++;
                    if (r.IsDoorLike) doorLike++;
                    if (r.IsFurniture) furniture++;
                }
            }

            // 定义了但没用到的代号（只是警告，不算错）
            foreach (KeyValuePair<char, TemplateRole> kv in Roles)
            {
                if (!used.Contains(kv.Key))
                    warnings.Add("角色 '" + kv.Key + "' 定义了但没用到（可清理）");
            }

            // 墙还是必须的：没有墙就没有"房子"，这是结构性的
            if (wallLike == 0) errors.Add("没有任何墙类格 —— 这不像一间房");

            // ⚠ 门是**警告**不是错误（用户原话：「门造不造其实无所谓」）
            if (doorLike == 0)
                warnings.Add("没有任何门 —— 房子会完全封闭，小人进不去（用户认为门可以先不造，"
                    + "所以只记警告；但真盖出来之前记得补门）");

            // 语义说不清的角色 → 警告（不许猜，但要显式提出来）
            if (registry != null)
            {
                List<string> unresolved = UnresolvedSemanticRoles(registry);
                if (unresolved.Count > 0)
                {
                    warnings.Add("有 " + unresolved.Count + " 个角色的**用途说不清**（建造顺序会排到最后）："
                        + string.Join("；", unresolved.ToArray()));
                }
            }

            return new TemplateValidation(errors, warnings, this);
        }

        /// <summary>
        /// **旋转 90°**（`rot` 次，顺时针）。放在不同朝向的地形上时用。
        ///
        /// 旋转后尺寸会互换（宽高对调）。
        /// </summary>
        public BuildTemplate Rotated(int rot)
        {
            rot = ((rot % 4) + 4) % 4;
            if (rot == 0) return this;

            BuildTemplate cur = this;
            for (int i = 0; i < rot; i++) cur = cur.Rotated90();
            return cur;
        }

        /// <summary>
        /// **造一个空壳副本**（Id/Name/标签/开关/角色表都拷过去，`Rows` 留空由调用方填）。
        ///
        /// 为什么单独提出来：`Rotated90()` 和 `Mirrored()` 原来各自抄了一遍
        /// 这段"拷贝元数据 + 克隆角色表"（约 15 行 × 2）。这类重复的危险是
        /// **加字段时只改一处** —— 比如上面新增的 `Kind`/`CountPolicy`，
        /// 漏掉的那个变换就会**把语义丢掉**，而且不报错。
        /// </summary>
        private BuildTemplate EmptyShell(int width, int height)
        {
            var outp = new BuildTemplate
            {
                Id = Id,
                Name = Name,
                Origin = Origin,
                Width = width,
                Height = height,
                Tags = new List<string>(Tags),
                Rotatable = Rotatable,
                Mirrorable = Mirrorable,
            };
            foreach (KeyValuePair<char, TemplateRole> kv in Roles)
                outp.Roles[kv.Key] = kv.Value.Clone();
            return outp;
        }

        private BuildTemplate Rotated90()
        {
            NormalizeSize();
            BuildTemplate outp = EmptyShell(Height, Width);   // 旋转后宽高对调

            // 顺时针 90°：(x, z) -> (H-1-z, x)
            var newRows = new char[Width][];        // 新的高 = 旧宽
            for (int i = 0; i < Width; i++) newRows[i] = new char[Height];

            for (int z = 0; z < Height; z++)
            {
                for (int x = 0; x < Width; x++)
                {
                    char c = SymbolAt(x, z);
                    char put = c == '\0' ? EmptyDot : c;
                    int nx = Height - 1 - z;
                    int nz = x;
                    newRows[nz][nx] = put;
                }
            }

            for (int i = 0; i < newRows.Length; i++) outp.Rows.Add(new string(newRows[i]));
            outp.NormalizeSize();
            return outp;
        }

        /// <summary>**水平镜像**（左右翻转）。</summary>
        public BuildTemplate Mirrored()
        {
            NormalizeSize();
            BuildTemplate outp = EmptyShell(Width, Height);

            for (int z = 0; z < Height; z++)
            {
                var sb = new StringBuilder(Width);
                for (int x = Width - 1; x >= 0; x--)
                {
                    char c = SymbolAt(x, z);
                    sb.Append(c == '\0' ? EmptyDot : c);
                }
                outp.Rows.Add(sb.ToString());
            }
            outp.NormalizeSize();
            return outp;
        }

        /// <summary>统计各类角色的格数（遥测与验证用）。</summary>
        public Dictionary<string, int> CountByRole()
        {
            var counts = new Dictionary<string, int>();
            NormalizeSize();
            for (int z = 0; z < Height; z++)
            {
                for (int x = 0; x < Width; x++)
                {
                    char c = SymbolAt(x, z);
                    if (c == '\0') continue;
                    string key = c.ToString();
                    int n;
                    counts[key] = counts.TryGetValue(key, out n) ? n + 1 : 1;
                }
            }
            return counts;
        }

        /// <summary>一行摘要（日志/遥测用）。</summary>
        public override string ToString()
        {
            var counts = CountByRole();
            var parts = new List<string>();
            foreach (KeyValuePair<string, int> kv in counts)
            {
                TemplateRole r;
                string nm = Roles.TryGetValue(kv.Key[0], out r) ? r.Name : kv.Key;
                parts.Add(nm + "×" + kv.Value);
            }
            return string.Format("{0} [{1}] {2}x{3}：{4}",
                Id, Origin, Width, Height, string.Join(", ", parts.ToArray()));
        }

        /// <summary>
        /// **每个角色解析出来的语义**（代号 → 语义）—— 与几何无关的那一层。
        ///
        /// 纯查询，不改样板。要"改"语义请直接改 `Roles[sym].Kind` 等字段。
        /// </summary>
        public Dictionary<char, SlotSemantics> RoleSemantics(SlotSemanticRegistry registry)
        {
            var map = new Dictionary<char, SlotSemantics>();
            foreach (KeyValuePair<char, TemplateRole> kv in Roles)
                map[kv.Key] = SlotSemanticsResolver.Resolve(kv.Value, registry);
            return map;
        }

        /// <summary>
        /// **语义自检**：哪些角色的用途说不清？
        ///
        /// 返回空列表 = 每个角色都能说出"这是什么"。
        /// 这就是那条铁律的落地：**说不出来就要显式说出来**，
        /// 而不是默默把它当成餐桌（`ChessTable` 那次就是这么错的）。
        /// </summary>
        public List<string> UnresolvedSemanticRoles(SlotSemanticRegistry registry)
        {
            var outp = new List<string>();
            foreach (KeyValuePair<char, TemplateRole> kv in Roles)
            {
                SlotSemantics s = SlotSemanticsResolver.Resolve(kv.Value, registry);
                if (s.Kind == SlotKind.Unknown)
                    outp.Add(string.Format("{0}({1})：{2}", kv.Key, kv.Value.Name, s.Why));
            }
            return outp;
        }
    }

    /// <summary>
    /// **样板校验结果**：错误与警告**分开**（见 `BuildTemplate.ValidateDetailed`）。
    ///
    /// 分的理由不是"好看"，是**会拦住能用的样板**：
    /// 用户明确说「门造不造其实无所谓」，把没门判成致命错误，
    /// 会让一个完全能用的样板被拒，而且错误信息读起来像"这样板是坏的"。
    /// </summary>
    public sealed class TemplateValidation
    {
        public readonly List<string> Errors;
        public readonly List<string> Warnings;

        /// <summary>每种角色各有几格（结构/睡眠/…的规模，遥测与材料估算用）</summary>
        public readonly Dictionary<string, int> CellsByRole;

        public TemplateValidation(List<string> errors, List<string> warnings, BuildTemplate tpl)
        {
            Errors = errors ?? new List<string>();
            Warnings = warnings ?? new List<string>();
            CellsByRole = new Dictionary<string, int>();
            if (tpl != null)
            {
                Dictionary<string, int> raw = tpl.CountByRole();
                foreach (KeyValuePair<string, int> kv in raw)
                {
                    TemplateRole r;
                    string name = tpl.Roles.TryGetValue(kv.Key[0], out r) ? r.Name : kv.Key;
                    if (string.IsNullOrEmpty(name)) name = kv.Key;
                    int n;
                    CellsByRole[name] = CellsByRole.TryGetValue(name, out n) ? n + kv.Value : kv.Value;
                }
            }
        }

        public bool Ok
        {
            get { return Errors.Count == 0; }
        }

        public override string ToString()
        {
            return string.Format("校验：{0}（错误 {1} / 警告 {2}）",
                Ok ? "通过" : "不通过", Errors.Count, Warnings.Count);
        }
    }
}
