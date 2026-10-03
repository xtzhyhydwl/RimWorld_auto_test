using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace FoodControl.Core
{
    /// <summary>账本里的一格：地图坐标 + 我们放的什么。</summary>
    public sealed class RegistryCell
    {
        public int X;
        public int Z;

        /// <summary>样板里的角色名（wall/door/bed…）</summary>
        public string Role = "";

        /// <summary>建筑 Def 名</summary>
        public string DefName = "";

        /// <summary>
        /// 这一格**在地图上还在吗**。
        ///
        /// ⚠ 这是"意图历史"与"当前状态"的分界点：
        ///   被烧/被拆之后 `StillThere = false`，但**这条记录不删** ——
        ///   因为"我们造过"是历史事实（用户明确要的就是这个）。
        /// </summary>
        public bool StillThere = true;

        public override string ToString()
        {
            return string.Format("({0},{1}){2}{3}", X, Z, Role, StillThere ? "" : "[没了]");
        }
    }

    /// <summary>账本里的一栋建筑（一次放置的完整记录）。</summary>
    public sealed class BuildingRecord
    {
        public string Id = "";
        /// <summary>用哪份样板盖的（空 = 程序化几何）</summary>
        public string TemplateId = "";
        public int OriginX;
        public int OriginZ;
        /// <summary>0..3</summary>
        public int Rotation;
        public bool Mirrored;

        /// <summary>第几个游戏日盖的</summary>
        public int Day;
        public int Tick;

        public List<RegistryCell> Cells = new List<RegistryCell>();

        /// <summary>这栋建筑的范围（含边界）</summary>
        public int MinX = int.MaxValue, MaxX = int.MinValue, MinZ = int.MaxValue, MaxZ = int.MinValue;

        public void RecomputeBounds()
        {
            MinX = int.MaxValue; MaxX = int.MinValue;
            MinZ = int.MaxValue; MaxZ = int.MinValue;
            for (int i = 0; i < Cells.Count; i++)
            {
                RegistryCell c = Cells[i];
                if (c == null) continue;
                if (c.X < MinX) MinX = c.X;
                if (c.X > MaxX) MaxX = c.X;
                if (c.Z < MinZ) MinZ = c.Z;
                if (c.Z > MaxZ) MaxZ = c.Z;
            }
        }

        public int Width
        {
            get { return MaxX >= MinX ? MaxX - MinX + 1 : 0; }
        }

        public int Height
        {
            get { return MaxZ >= MinZ ? MaxZ - MinZ + 1 : 0; }
        }

        /// <summary>现在地图上还剩几格</summary>
        public int AliveCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < Cells.Count; i++)
                    if (Cells[i] != null && Cells[i].StillThere) n++;
                return n;
            }
        }

        public override string ToString()
        {
            return string.Format("{0}{1} @({2},{3}) {4}×{5} {6}格（现存{7}）",
                string.IsNullOrEmpty(TemplateId) ? "建筑" : TemplateId,
                Rotation != 0 ? " rot" + Rotation : "",
                OriginX, OriginZ, Width, Height, Cells.Count, AliveCount);
        }
    }

    /// <summary>
    /// **建筑账本** —— 记录"我们自己造过什么、在哪里"。
    ///
    /// ## 为什么需要它（用户 2026-10-03 提出）
    ///
    /// > 「我们要维护一个我们目前为止建造过的**所有建筑的大致的样貌**，
    /// >  就是你要有它的**全局数据**，就是**剥离开地图原有的元素**。
    /// >  我们要能够在任何时候想知道的时候都能知道**我们到底造了些啥**。」
    ///
    /// 三个理由：
    ///
    /// 1. **比"从地图推理"可靠** —— 我们自己放蓝图的那一刻，是唯一能 100% 确定
    ///    "这是我们造的"的时机。事后从地图猜会被 debug 开局自带的 662 堵墙、
    ///    地图原有废墟、其他 mod/玩家造的东西污染。
    /// 2. **它是"贴靠选址"的前置** —— 要"贴着已有建筑盖"，必须先知道已有建筑在哪。
    /// 3. **让"我们造了啥"变成可回答的问题** —— 不用数地图上的墙。
    ///
    /// ## 语义（关键取舍）
    ///
    /// 账本记录的是「**我们曾经下了什么蓝图**」，是 **append-only 的意图历史**，
    /// **不是**"现在地图上还剩什么"。
    ///
    /// 所以被烧/被拆的格子**不删除**，只标 `StillThere = false`。
    /// 理由（用户原话）：
    /// > 「假如说是因为不可抗力……墙体的那些建筑我们没法跟踪。
    /// >  但是至少我们可以知道我们造了一些什么。」
    ///
    /// 这也是为什么本类**只有一个"标记没了"的入口**，没有"删除"入口。
    /// </summary>
    public sealed class BuildingRegistry
    {
        private readonly List<BuildingRecord> _records = new List<BuildingRecord>();

        /// <summary>格子 → 它属于哪几栋（共享墙会让同一格属于两栋）</summary>
        private readonly Dictionary<long, List<BuildingRecord>> _byCell
            = new Dictionary<long, List<BuildingRecord>>();

        public int Count
        {
            get { return _records.Count; }
        }

        public IList<BuildingRecord> Records
        {
            get { return _records.AsReadOnly(); }
        }

        private static long Key(int x, int z)
        {
            // 地图坐标是非负的；用它拼一个稳定 key
            return ((long)x << 32) | (uint)z;
        }

        /// <summary>记一笔：我们放了一栋建筑</summary>
        public BuildingRecord Add(BuildingRecord rec)
        {
            if (rec == null) return null;
            rec.RecomputeBounds();
            _records.Add(rec);

            for (int i = 0; i < rec.Cells.Count; i++)
            {
                RegistryCell c = rec.Cells[i];
                if (c == null) continue;

                long k = Key(c.X, c.Z);
                List<BuildingRecord> list;
                if (!_byCell.TryGetValue(k, out list))
                {
                    list = new List<BuildingRecord>(1);
                    _byCell[k] = list;
                }
                // ★ 同一格可以属于多栋（共享墙）→ 不覆盖，追加
                if (!list.Contains(rec)) list.Add(rec);
            }

            return rec;
        }

        /// <summary>
        /// **这一格是我们造的吗**（账本最有用的一个方法）。
        ///
        /// 用途：
        /// - 贴靠选址：找"边缘贴着我们、内部为空"的位置
        /// - 共享墙复用：那一格如果是**我们造的墙** → 可以复用
        ///
        /// ⚠ 不能放宽成"那一格有墙就复用" —— 地图原有的墙（废墟、其他 mod）
        ///   **不该**被当成我们的墙。否则拆自己建筑时会连带拆掉别人的东西。
        /// </summary>
        public bool IsOurs(int x, int z)
        {
            List<BuildingRecord> list;
            return _byCell.TryGetValue(Key(x, z), out list) && list.Count > 0;
        }

        /// <summary>这一格上**我们造过什么**（可能多栋：共享墙）</summary>
        public List<BuildingRecord> At(int x, int z)
        {
            List<BuildingRecord> list;
            if (_byCell.TryGetValue(Key(x, z), out list)) return new List<BuildingRecord>(list);
            return new List<BuildingRecord>();
        }

        /// <summary>
        /// 找出**离 (x,z) 最近**的那些建筑（贴靠选址用）。
        ///
        /// 距离用**切比雪夫距离**（Chebyshev）—— 因为"挨着"在方格地图上是
        /// "横向或纵向或斜向差 1 格"，也就是 max(|dx|,|dz|) &lt;= 1。
        /// 用曼哈顿距离会把斜对角判成"距离 2"，而实际上它们**角对角**接触，
        /// 已经算挨着了。
        /// </summary>
        public List<BuildingRecord> Near(int x, int z, int radius)
        {
            var result = new List<BuildingRecord>();
            if (radius < 0) radius = 0;

            for (int i = 0; i < _records.Count; i++)
            {
                BuildingRecord r = _records[i];
                if (r == null) continue;

                // 点到矩形的切比雪夫距离
                int dx = 0, dz = 0;
                if (x < r.MinX) dx = r.MinX - x;
                else if (x > r.MaxX) dx = x - r.MaxX;
                if (z < r.MinZ) dz = r.MinZ - z;
                else if (z > r.MaxZ) dz = z - r.MaxZ;

                int d = Math.Max(dx, dz);
                if (d <= radius) result.Add(r);
            }
            return result;
        }

        /// <summary>所有我们造过的格（含那些已经没了的 —— 调用方自己看 `StillThere`）</summary>
        public List<RegistryCell> AllCells()
        {
            var all = new List<RegistryCell>();
            for (int i = 0; i < _records.Count; i++)
            {
                BuildingRecord r = _records[i];
                if (r == null) continue;
                for (int j = 0; j < r.Cells.Count; j++)
                    if (r.Cells[j] != null) all.Add(r.Cells[j]);
            }
            return all;
        }

        /// <summary>
        /// **标记某一格没了**（被烧/被拆/被砸）。
        ///
        /// ⚠ 这是唯一的"状态变更"入口，而且它**不删记录** ——
        ///   见类注释里的语义说明（"我们造过"是历史事实）。
        /// </summary>
        public bool MarkMissing(int x, int z)
        {
            List<BuildingRecord> list;
            if (!_byCell.TryGetValue(Key(x, z), out list)) return false;

            bool any = false;
            for (int i = 0; i < list.Count; i++)
            {
                BuildingRecord r = list[i];
                if (r == null) continue;
                for (int j = 0; j < r.Cells.Count; j++)
                {
                    RegistryCell c = r.Cells[j];
                    if (c != null && c.X == x && c.Z == z && c.StillThere)
                    {
                        c.StillThere = false;
                        any = true;
                    }
                }
            }
            return any;
        }

        /// <summary>
        /// 用地图现状**校验**账本：格子还在吗？
        ///
        /// `exists` 由游戏层提供（那一格现在有没有对应类型的东西）。
        /// 返回"变了多少格"，并就地更新 `StillThere`。
        ///
        /// ⚠ **只标记，不删除**。校验的作用是让我们知道"账本与现实的差异"，
        ///   而不是让账本变成地图的副本（那就失去意义了）。
        /// </summary>
        public int Reconcile(Func<int, int, string, bool> exists)
        {
            if (exists == null) return 0;
            int changed = 0;

            for (int i = 0; i < _records.Count; i++)
            {
                BuildingRecord r = _records[i];
                if (r == null) continue;

                for (int j = 0; j < r.Cells.Count; j++)
                {
                    RegistryCell c = r.Cells[j];
                    if (c == null) continue;

                    bool now;
                    try { now = exists(c.X, c.Z, c.Role); }
                    catch (Exception) { continue; }

                    if (now != c.StillThere)
                    {
                        c.StillThere = now;
                        changed++;
                    }
                }
            }
            return changed;
        }

        /// <summary>清空（测试/换图用）</summary>
        public void Clear()
        {
            _records.Clear();
            _byCell.Clear();
        }

        /// <summary>一行摘要</summary>
        public string Summary()
        {
            int cells = 0, alive = 0;
            for (int i = 0; i < _records.Count; i++)
            {
                BuildingRecord r = _records[i];
                if (r == null) continue;
                cells += r.Cells.Count;
                alive += r.AliveCount;
            }
            return string.Format("账本：{0} 栋建筑，共 {1} 格（现存 {2}）", _records.Count, cells, alive);
        }

        // ------------------------------------------------------------------
        // 序列化（和样板一样，Core 自写 JSON，零依赖）
        // ------------------------------------------------------------------

        public string ToJson()
        {
            var sb = new StringBuilder();
            sb.Append("{\n  \"version\": 1,\n  \"records\": [\n");

            for (int i = 0; i < _records.Count; i++)
            {
                BuildingRecord r = _records[i];
                if (r == null) continue;
                if (i > 0) sb.Append(",\n");
                sb.Append("    { ");
                sb.Append("\"id\": ").Append(Q(r.Id)).Append(", ");
                sb.Append("\"templateId\": ").Append(Q(r.TemplateId)).Append(", ");
                sb.Append("\"originX\": ").Append(r.OriginX).Append(", ");
                sb.Append("\"originZ\": ").Append(r.OriginZ).Append(", ");
                sb.Append("\"rotation\": ").Append(r.Rotation).Append(", ");
                sb.Append("\"mirrored\": ").Append(r.Mirrored ? "true" : "false").Append(", ");
                sb.Append("\"day\": ").Append(r.Day).Append(", ");
                sb.Append("\"tick\": ").Append(r.Tick).Append(", ");
                sb.Append("\"cells\": [");

                for (int j = 0; j < r.Cells.Count; j++)
                {
                    RegistryCell c = r.Cells[j];
                    if (c == null) continue;
                    if (j > 0) sb.Append(", ");
                    sb.Append("[").Append(c.X).Append(",").Append(c.Z).Append(",")
                      .Append(Q(c.Role)).Append(",").Append(Q(c.DefName)).Append(",")
                      .Append(c.StillThere ? "1" : "0").Append("]");
                }
                sb.Append("] }");
            }

            sb.Append("\n  ]\n}\n");
            return sb.ToString();
        }

        private static string Q(string s)
        {
            if (s == null) return "\"\"";
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>从 JSON 读回（用样板那套极简解析器，见 `TemplateJson`）</summary>
        public static BuildingRegistry FromJson(string json)
        {
            var reg = new BuildingRegistry();
            if (string.IsNullOrEmpty(json)) return reg;

            var parsed = SimpleJson.Parse(json) as Dictionary<string, object>;
            if (parsed == null) return reg;

            object recsObj;
            if (!parsed.TryGetValue("records", out recsObj)) return reg;
            var recs = recsObj as List<object>;
            if (recs == null) return reg;

            for (int i = 0; i < recs.Count; i++)
            {
                var m = recs[i] as Dictionary<string, object>;
                if (m == null) continue;

                var r = new BuildingRecord();
                r.Id = GetS(m, "id");
                r.TemplateId = GetS(m, "templateId");
                r.OriginX = (int)GetN(m, "originX");
                r.OriginZ = (int)GetN(m, "originZ");
                r.Rotation = (int)GetN(m, "rotation");
                r.Mirrored = GetB(m, "mirrored");
                r.Day = (int)GetN(m, "day");
                r.Tick = (int)GetN(m, "tick");

                object cellsObj;
                if (m.TryGetValue("cells", out cellsObj))
                {
                    var cells = cellsObj as List<object>;
                    if (cells != null)
                    {
                        for (int j = 0; j < cells.Count; j++)
                        {
                            var arr = cells[j] as List<object>;
                            if (arr == null || arr.Count < 5) continue;
                            var c = new RegistryCell
                            {
                                X = (int)ToD(arr[0]),
                                Z = (int)ToD(arr[1]),
                                Role = arr[2] as string ?? "",
                                DefName = arr[3] as string ?? "",
                                StillThere = ToD(arr[4]) != 0,
                            };
                            r.Cells.Add(c);
                        }
                    }
                }

                reg.Add(r);
            }

            return reg;
        }

        private static string GetS(Dictionary<string, object> m, string k)
        {
            object v;
            return m.TryGetValue(k, out v) ? (v as string ?? "") : "";
        }

        private static double GetN(Dictionary<string, object> m, string k)
        {
            object v;
            return m.TryGetValue(k, out v) ? ToD(v) : 0;
        }

        private static bool GetB(Dictionary<string, object> m, string k)
        {
            object v;
            if (m.TryGetValue(k, out v))
            {
                if (v is bool) return (bool)v;
                return ToD(v) != 0;
            }
            return false;
        }

        private static double ToD(object o)
        {
            if (o == null) return 0;
            if (o is double) return (double)o;
            if (o is bool) return ((bool)o) ? 1 : 0;
            double d;
            if (double.TryParse(o.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            return 0;
        }
    }
}
