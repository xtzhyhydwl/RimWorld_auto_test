using System;
using System.Collections.Generic;
using System.Text;

namespace FoodControl.Core
{
    /// <summary>
    /// **殖民地状态快照**（纯数据，可序列化）。
    ///
    /// ## 设计立场（用户 2026-10-03 明确要求）
    ///
    /// 这是**独立于自动化**的一条线 —— 只读、只记，**不做任何决策**。
    /// 用户原话：
    /// > 「这个和我们现在的自动化**没有任何关系**，它是我的另外一个想法。
    /// >  它和自动化……能够帮助开发自动化。但我不希望在这个过程中
    /// >  **再把自动化的东西放进去**，你就**纯净地去读**、读快照就可以了。
    /// >  我就是玩这个游戏，然后你就在后台检测到存档开起来之后就**静默地读**。」
    ///
    /// ## 面向对象（用户 2026-10-03 上一轮的要求）
    ///
    /// 记的是**游戏已经存好的对象**，不是我推断出来的"用户操作事件"：
    /// - 区域（种植区/储藏区）：格子、作物、过滤器
    /// - **存储组**：组名、成员（存储单位及位置）、组的存储设置
    /// - 建筑/物品：按 Def 分组 + 位置
    /// - 小人：心情、期望、**负面想法**（这是"为什么做某事"的线索）
    /// - 生产队列 / 研究 / 财富
    ///
    /// **差分留给分析端做** —— 这里只负责"某一刻世界长什么样"。
    /// </summary>
    public sealed class ColonySnapshot
    {
        /// <summary>游戏 tick（采样基准，**不用真实时间** —— 用户明确要求）</summary>
        public int Tick;

        /// <summary>第几个游戏日（tick / 60000）</summary>
        public int Day;

        /// <summary>地图 id</summary>
        public int MapId;

        /// <summary>真实时间（仅用于排查，不用于采样）</summary>
        public string WallClock = "";

        /// <summary>快照序号（同一会话内递增）</summary>
        public int Seq;

        /// <summary>区域（种植区 / 储藏区 / 其他）</summary>
        public List<SnapZone> Zones = new List<SnapZone>();

        /// <summary>★ 存储组（游戏自带的对象结构）</summary>
        public List<SnapStorageGroup> StorageGroups = new List<SnapStorageGroup>();

        /// <summary>建筑/物品/蓝图/框架：Def 名 → 位置列表</summary>
        public Dictionary<string, List<int[]>> Things = new Dictionary<string, List<int[]>>();

        /// <summary>小人状态</summary>
        public List<SnapPawn> Pawns = new List<SnapPawn>();

        /// <summary>生产队列</summary>
        public List<SnapBill> Bills = new List<SnapBill>();

        /// <summary>研究</summary>
        public string ResearchCurrent = "";
        public int ResearchFinishedCount;
        public List<string> ResearchFinished = new List<string>();

        /// <summary>财富（期望的驱动量）</summary>
        public double WealthTotal;
        public double WealthItems;
        public double WealthBuildings;
        public double WealthPawns;

        /// <summary>地图标记（采矿/拆除等）</summary>
        public Dictionary<string, List<int[]>> Designations = new Dictionary<string, List<int[]>>();

        /// <summary>
        /// ★ **每个读取块的健康记录**（可度量的基础）。
        ///
        /// 用户 2026-10-03：
        /// > 「现在我**无法知道我们现在是否读取了足量的快照**。」
        ///
        /// → 每个 Reader 跑完留一条记录，分析端就能区分
        ///   "这一局真的没有这个对象" 和 "我的读法失效了"。
        /// </summary>
        public List<ReaderHealth> Health = new List<ReaderHealth>();

        /// <summary>★ 采样元信息（回答"快照够不够密"）</summary>
        public SamplingInfo Sampling = new SamplingInfo();

        /// <summary>
        /// ★ **扩展逃生口**：某个 Reader 要写非结构化内容时用它，
        /// 不用改 `ColonySnapshot` 的既有字段。这保证"加新读取块不用动老代码"。
        /// </summary>
        public Dictionary<string, string> Extra = new Dictionary<string, string>();

        public string Summary()
        {
            int failed = 0;
            for (int i = 0; i < Health.Count; i++) if (Health[i] != null && !string.IsNullOrEmpty(Health[i].Error)) failed++;
            return string.Format("tick={0} day={1} 区域{2} 存储组{3} 建筑组{4} 小人{5} 队列{6} 研究[{7}] 财富{8:F0} 读取块{9}{10}",
                Tick, Day, Zones.Count, StorageGroups.Count, Things.Count, Pawns.Count,
                Bills.Count, string.IsNullOrEmpty(ResearchCurrent) ? "无" : ResearchCurrent, WealthTotal,
                Health.Count, failed > 0 ? "（" + failed + " 块失败）" : "");
        }

        /// <summary>序列化成一行 JSON（JSONL 用）</summary>
        public string ToJsonLine()
        {
            var sb = new StringBuilder(4096);
            sb.Append('{');
            sb.Append("\"tick\":").Append(Tick);
            sb.Append(",\"day\":").Append(Day);
            sb.Append(",\"mapId\":").Append(MapId);
            sb.Append(",\"seq\":").Append(Seq);
            sb.Append(",\"wallClock\":").Append(Q(WallClock));

            // zones
            sb.Append(",\"zones\":[");
            for (int i = 0; i < Zones.Count; i++)
            {
                if (i > 0) sb.Append(',');
                Zones[i].Write(sb);
            }
            sb.Append(']');

            // storage groups
            sb.Append(",\"storageGroups\":[");
            for (int i = 0; i < StorageGroups.Count; i++)
            {
                if (i > 0) sb.Append(',');
                StorageGroups[i].Write(sb);
            }
            sb.Append(']');

            // things
            sb.Append(",\"things\":{");
            bool first = true;
            foreach (var kv in Things)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(Q(kv.Key)).Append(":[");
                WriteCells(sb, kv.Value);
                sb.Append(']');
            }
            sb.Append('}');

            // pawns
            sb.Append(",\"pawns\":[");
            for (int i = 0; i < Pawns.Count; i++)
            {
                if (i > 0) sb.Append(',');
                Pawns[i].Write(sb);
            }
            sb.Append(']');

            // bills
            sb.Append(",\"bills\":[");
            for (int i = 0; i < Bills.Count; i++)
            {
                if (i > 0) sb.Append(',');
                Bills[i].Write(sb);
            }
            sb.Append(']');

            // research
            sb.Append(",\"research\":{\"current\":").Append(Q(ResearchCurrent));
            sb.Append(",\"finishedCount\":").Append(ResearchFinishedCount);
            sb.Append(",\"finished\":[");
            for (int i = 0; i < ResearchFinished.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(Q(ResearchFinished[i]));
            }
            sb.Append("]}");

            // wealth
            sb.Append(",\"wealth\":{\"total\":").Append(N(WealthTotal));
            sb.Append(",\"items\":").Append(N(WealthItems));
            sb.Append(",\"buildings\":").Append(N(WealthBuildings));
            sb.Append(",\"pawns\":").Append(N(WealthPawns));
            sb.Append('}');

            // designations
            sb.Append(",\"designations\":{");
            first = true;
            foreach (var kv in Designations)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(Q(kv.Key)).Append(":[");
                WriteCells(sb, kv.Value);
                sb.Append(']');
            }
            sb.Append('}');

            // ── ★ 可度量：读取块健康 ──
            sb.Append(",\"health\":[");
            for (int i = 0; i < Health.Count; i++)
            {
                if (i > 0) sb.Append(',');
                Health[i].Write(sb);
            }
            sb.Append(']');

            // ── ★ 可度量：采样信息 ──
            sb.Append(",\"sampling\":");
            Sampling.Write(sb);

            // ── ★ 扩展逃生口 ──
            sb.Append(",\"extra\":{");
            first = true;
            foreach (var kv in Extra)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(Q(kv.Key)).Append(':').Append(Q(kv.Value));
            }
            sb.Append('}');

            sb.Append('}');
            return sb.ToString();
        }

        // ---- 内部工具 ----

        internal static void WriteCells(StringBuilder sb, List<int[]> cells)
        {
            if (cells == null) return;
            for (int i = 0; i < cells.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('[').Append(cells[i][0]).Append(',').Append(cells[i][1]).Append(']');
            }
        }

        internal static string Q(string s)
        {
            return SimpleJson.Quote(s);
        }

        internal static string N(double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) return "0";
            return d.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// **一个读取块的健康记录** —— "可度量"的基础。
    ///
    /// 有了它，分析端才能区分：
    ///   - "这一局真的没有存储组"（`Ran=true, ItemsRead=0, Error=null`）
    ///   - "我的读法坏了"（`Error != null` / `Ran=false`）
    ///
    /// 没有这个区分，用户说的"无法知道是否读取了足量"就永远无解。
    /// </summary>
    public sealed class ReaderHealth
    {
        /// <summary>读取块的稳定标识（如 `zones` / `storageGroups`）</summary>
        public string ReaderId = "";
        /// <summary>跑了吗</summary>
        public bool Ran;
        /// <summary>读了几个对象（0 可能是"真没有"，也可能是"读法失效" —— 配合 Error 判断）</summary>
        public int ItemsRead;
        /// <summary>失败原因（空 = 成功）</summary>
        public string Error = "";
        /// <summary>部分失败（比如某个 Def 找不到，但主体读到了）</summary>
        public List<string> Warnings = new List<string>();

        public void Write(StringBuilder sb)
        {
            sb.Append("{\"reader\":").Append(ColonySnapshot.Q(ReaderId));
            sb.Append(",\"ran\":").Append(Ran ? "true" : "false");
            sb.Append(",\"items\":").Append(ItemsRead);
            sb.Append(",\"error\":").Append(ColonySnapshot.Q(Error));
            sb.Append(",\"warnings\":[");
            for (int i = 0; i < Warnings.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(ColonySnapshot.Q(Warnings[i]));
            }
            sb.Append("]}");
        }
    }

    /// <summary>
    /// **采样元信息** —— 回答"快照够不够密"。
    ///
    /// 用户 2026-10-03：「我**无法知道我们现在是否读取了足量的快照**。」
    /// </summary>
    public sealed class SamplingInfo
    {
        /// <summary>设定的采样间隔（游戏 tick）</summary>
        public int IntervalTicks;
        /// <summary>距上一张快照多少 tick（★ 应该 ≈ IntervalTicks）</summary>
        public int SincePrevTick;
        /// <summary>这张是累计第几张</summary>
        public int TotalSnapshots;
        /// <summary>这一局一共玩了多少 tick（用于判断"相对于游戏时长，快照够不够"）</summary>
        public int PlayedTicks;

        /// <summary>这一张是否**漏采**了（间隔明显大于设定值）</summary>
        public bool LooksDropped
        {
            get
            {
                if (IntervalTicks <= 0 || TotalSnapshots <= 1) return false;
                return SincePrevTick > IntervalTicks * 3 / 2;      // 超过 1.5 倍就算漏
            }
        }

        public void Write(StringBuilder sb)
        {
            sb.Append("{\"intervalTicks\":").Append(IntervalTicks);
            sb.Append(",\"sincePrevTick\":").Append(SincePrevTick);
            sb.Append(",\"totalSnapshots\":").Append(TotalSnapshots);
            sb.Append(",\"playedTicks\":").Append(PlayedTicks);
            sb.Append(",\"looksDropped\":").Append(LooksDropped ? "true" : "false");
            sb.Append('}');
        }
    }

    /// <summary>快照里的一个区域</summary>
    public sealed class SnapZone
    {
        public int Id;
        public string Type = "";
        public string Label = "";
        public int CellCount;
        public string PlantDef = "";
        public List<int[]> Cells = new List<int[]>();
        /// <summary>储藏区：抽查若干 Def 判断收不收（"这个区承载什么内容"）</summary>
        public List<string> Allowed = new List<string>();
        public List<string> Disallowed = new List<string>();

        public void Write(StringBuilder sb)
        {
            sb.Append("{\"id\":").Append(Id);
            sb.Append(",\"type\":").Append(ColonySnapshot.Q(Type));
            sb.Append(",\"label\":").Append(ColonySnapshot.Q(Label));
            sb.Append(",\"cellCount\":").Append(CellCount);
            sb.Append(",\"plantDef\":").Append(ColonySnapshot.Q(PlantDef));
            sb.Append(",\"cells\":[");
            ColonySnapshot.WriteCells(sb, Cells);
            sb.Append("],\"allowed\":[");
            for (int i = 0; i < Allowed.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(ColonySnapshot.Q(Allowed[i]));
            }
            sb.Append("],\"disallowed\":[");
            for (int i = 0; i < Disallowed.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(ColonySnapshot.Q(Disallowed[i]));
            }
            sb.Append("]}");
        }
    }

    /// <summary>★ 快照里的一个存储组（游戏自带对象）</summary>
    public sealed class SnapStorageGroup
    {
        public int LoadId;
        public string Label = "";
        public int MemberCount;
        public int CellCount;
        /// <summary>成员：类型 + 位置（"应用了这个组的存储单位、在哪"）</summary>
        public List<SnapGroupMember> Members = new List<SnapGroupMember>();
        public List<string> Allowed = new List<string>();
        public List<string> Disallowed = new List<string>();

        public void Write(StringBuilder sb)
        {
            sb.Append("{\"loadId\":").Append(LoadId);
            sb.Append(",\"label\":").Append(ColonySnapshot.Q(Label));
            sb.Append(",\"memberCount\":").Append(MemberCount);
            sb.Append(",\"cellCount\":").Append(CellCount);
            sb.Append(",\"members\":[");
            for (int i = 0; i < Members.Count; i++)
            {
                if (i > 0) sb.Append(',');
                Members[i].Write(sb);
            }
            sb.Append("],\"allowed\":[");
            for (int i = 0; i < Allowed.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(ColonySnapshot.Q(Allowed[i]));
            }
            sb.Append("],\"disallowed\":[");
            for (int i = 0; i < Disallowed.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(ColonySnapshot.Q(Disallowed[i]));
            }
            sb.Append("]}");
        }
    }

    /// <summary>存储组的一个成员（存储单位）</summary>
    public sealed class SnapGroupMember
    {
        public string Kind = "";
        public List<int[]> Cells = new List<int[]>();

        public void Write(StringBuilder sb)
        {
            sb.Append("{\"kind\":").Append(ColonySnapshot.Q(Kind));
            sb.Append(",\"cells\":[");
            ColonySnapshot.WriteCells(sb, Cells);
            sb.Append("]}");
        }
    }

    /// <summary>快照里的一个小人</summary>
    public sealed class SnapPawn
    {
        public string Name = "";
        public int X, Z;
        public double Mood;
        /// <summary>★ 期望等级 —— "变富 → 期望↑ → 心情↓"那个环的驱动量</summary>
        public string Expectation = "";
        /// <summary>★ 负面想法（"为什么心情掉"的线索）</summary>
        public List<string> NegativeThoughts = new List<string>();
        public double Food;

        public void Write(StringBuilder sb)
        {
            sb.Append("{\"name\":").Append(ColonySnapshot.Q(Name));
            sb.Append(",\"x\":").Append(X).Append(",\"z\":").Append(Z);
            sb.Append(",\"mood\":").Append(ColonySnapshot.N(Mood));
            sb.Append(",\"food\":").Append(ColonySnapshot.N(Food));
            sb.Append(",\"expectation\":").Append(ColonySnapshot.Q(Expectation));
            sb.Append(",\"negativeThoughts\":[");
            for (int i = 0; i < NegativeThoughts.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(ColonySnapshot.Q(NegativeThoughts[i]));
            }
            sb.Append("]}");
        }
    }

    /// <summary>快照里的一条生产队列</summary>
    public sealed class SnapBill
    {
        public string Bench = "";
        public int BenchX, BenchZ;
        public string Recipe = "";
        public string RecipeLabel = "";
        public bool Suspended;
        public string RepeatMode = "";
        public string TargetCount = "";

        public void Write(StringBuilder sb)
        {
            sb.Append("{\"bench\":").Append(ColonySnapshot.Q(Bench));
            sb.Append(",\"benchX\":").Append(BenchX).Append(",\"benchZ\":").Append(BenchZ);
            sb.Append(",\"recipe\":").Append(ColonySnapshot.Q(Recipe));
            sb.Append(",\"recipeLabel\":").Append(ColonySnapshot.Q(RecipeLabel));
            sb.Append(",\"suspended\":").Append(Suspended ? "true" : "false");
            sb.Append(",\"repeatMode\":").Append(ColonySnapshot.Q(RepeatMode));
            sb.Append(",\"targetCount\":").Append(ColonySnapshot.Q(TargetCount));
            sb.Append('}');
        }
    }
}
