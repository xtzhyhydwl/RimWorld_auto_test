using System;
using System.Collections.Generic;
using System.Text;

namespace ColonySnapshots
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

        /// <summary>
        /// ★ **游戏绝对 tick**（= 游戏内 tick + 起始绝对 tick）。
        ///
        /// ⚠ 为什么必须记它（2026-10-03 用户实测发现的坑）：
        ///   `Day = tick / 60000` 只是"**殖民地建立以来**的天数"（用户那局是 29），
        ///   **不是游戏内日期**（真实是 year 5500 / day-of-year 40.5 / Septober）。
        ///   用户因此以为"数据被截断了"。要算真实日期/季节，只有 `Tick` 不够 ——
        ///   必须 `absTick = ticksGame + gameStartAbsTick`（存档里实测：
        ///   `<ticksGame>1761003</ticksGame>` + `<gameStartAbsTick>670000</gameStartAbsTick>`）。
        /// </summary>
        public int AbsTick;

        /// <summary>游戏内年份（如 5500）</summary>
        public int Year;

        /// <summary>象限（Aprimay / Jugust / Septober / Decembary）</summary>
        public string Quadrum = "";

        /// <summary>本象限内第几天（1..15）</summary>
        public int DayOfQuadrum;

        /// <summary>绝对年内第几天（1..60）</summary>
        public int DayOfYear;

        /// <summary>人话日期（`GenDate.DateFullStringAt`，可能本地化）</summary>
        public string DateText = "";

        /// <summary>殖民地 / 定居点名（拿不到就空串）</summary>
        public string ColonyName = "";

        /// <summary>地图名（拿不到就空串）</summary>
        public string MapName = "";

        /// <summary>真实时间（仅用于排查，不用于采样）</summary>
        public string WallClock = "";

        /// <summary>快照序号（同一会话内递增）</summary>
        public int Seq;

        /// <summary>
        /// ★ **本次进程启动的 runId**（= `foodcontrol-snapshots/runs/run-<runId>/` 里那个 runId）。
        ///
        /// 由 `ColonyRecorder.Capture` 从 `SessionMeta` 灌进来；序列化时**追加在整行最后**
        /// （老字段的名字/类型/相对顺序一个不动 —— 本项目硬约束）。
        /// </summary>
        public string RunId = "";

        /// <summary>★ **会话 id**（`<runId>-s<sessionSeq>`；进程内换局/读档 = 新会话）</summary>
        public string SessionId = "";

        /// <summary>★ **会话号**（1, 2, 3 …；0 = 还没开过会话）</summary>
        public int SessionSeq;

        /// <summary>区域（种植区 / 储藏区 / 其他）</summary>
        public List<SnapZone> Zones = new List<SnapZone>();

        /// <summary>★ 存储组（游戏自带的对象结构）</summary>
        public List<SnapStorageGroup> StorageGroups = new List<SnapStorageGroup>();

        /// <summary>建筑/物品/蓝图/框架：Def 名 → 位置列表</summary>
        public Dictionary<string, List<int[]>> Things = new Dictionary<string, List<int[]>>();

        /// <summary>小人状态</summary>
        public List<SnapPawn> Pawns = new List<SnapPawn>();

        /// <summary>
        /// ★ **殖民者明细**（一人一条，追加面，不动 `pawns[]`）。
        ///
        /// 用户 2026-10-03：
        /// > 「**以一个殖民者为单位**，有新的殖民者就在总的殖民者当中新加一个对象，
        /// >  这个对象会记录殖民者的**各种状态**。」
        /// </summary>
        public List<SnapPawnDetail> PawnDetails = new List<SnapPawnDetail>();

        /// <summary>
        /// ★ **房间**（追加面）。
        ///
        /// 用户最在意的面：
        /// > 「一个房间在**不同时间段承担多种职责**」「13×13 的大房间可能**被一隔为二**」
        /// > 「主要关注**变化**」
        /// </summary>
        public List<SnapRoom> Rooms = new List<SnapRoom>();

        /// <summary>
        /// ★ **建筑材料**（追加面）：`{ 建筑def: { 材质def: 个数 } }`。
        ///
        /// 用户：
        /// > 「主要用来建筑的**材料有什么变化**……早期用木头，后来逐步换成石砖，
        /// >  外墙用防御更高的石砖，防火防袭击……钢铁有战略意义」
        /// </summary>
        public Dictionary<string, Dictionary<string, int>> Materials =
            new Dictionary<string, Dictionary<string, int>>();

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

            // ── ★ 游戏内日期 / 殖民地身份（**追加**字段，老字段一个不动）──
            //    注意：这些全部**必须放在 `wallClock` 之后、`zones` 之前**，
            //    以保证老字段的相对顺序也不变（分析端 `analyze-snapshots.mjs` 只按名字取）。
            sb.Append(",\"absTick\":").Append(AbsTick);
            sb.Append(",\"year\":").Append(Year);
            sb.Append(",\"quadrum\":").Append(Q(Quadrum));
            sb.Append(",\"dayOfQuadrum\":").Append(DayOfQuadrum);
            sb.Append(",\"dayOfYear\":").Append(DayOfYear);
            sb.Append(",\"dateText\":").Append(Q(DateText));
            sb.Append(",\"colonyName\":").Append(Q(ColonyName));
            sb.Append(",\"mapName\":").Append(Q(MapName));

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

            // ── ★ 殖民者明细（Order 45）──
            sb.Append(",\"pawnDetails\":[");
            for (int i = 0; i < PawnDetails.Count; i++)
            {
                if (i > 0) sb.Append(',');
                PawnDetails[i].Write(sb);
            }
            sb.Append(']');

            // ── ★ 房间（Order 46）──
            sb.Append(",\"rooms\":[");
            for (int i = 0; i < Rooms.Count; i++)
            {
                if (i > 0) sb.Append(',');
                Rooms[i].Write(sb);
            }
            sb.Append(']');

            // ── ★ 建筑材料（Order 47）：{建筑def:{材质def:个数}} ──
            sb.Append(",\"materials\":{");
            first = true;
            foreach (var kv in Materials)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(Q(kv.Key)).Append(":{");
                bool f2 = true;
                foreach (var skv in kv.Value)
                {
                    if (!f2) sb.Append(',');
                    f2 = false;
                    sb.Append(Q(skv.Key)).Append(':').Append(skv.Value);
                }
                sb.Append('}');
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

            // ── ★ run / session 元数据（**追加在整行最后**）──
            //
            //  用户 2026-10-04 要求"三流 JSON 行尾追加一致的 runId/sessionId/sessionSeq 元数据"。
            //  规则与 `SessionMeta.AppendTo` **逐字一致**：
            //   ⚠ 三个**平级**字段（不是嵌套对象）—— 老解析器按字段名取值，追加平级字段它无感；
            //     嵌套对象会诱使新老两套读法并存，必翻车。
            //   ⚠ 字段顺序也要与另外两条流一致：runId → sessionId → sessionSeq。
            //   ⚠ 老字段一个都没动：这是整行**最后**的三个字段。
            sb.Append(",\"runId\":").Append(Q(RunId));
            sb.Append(",\"sessionId\":").Append(Q(SessionId));
            sb.Append(",\"sessionSeq\":").Append(SessionSeq);

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
            return Quote(s);
        }

        /// <summary>
        /// 把字符串转成带引号的 JSON 字面量（转义规则与原 `SimpleJson.Quote` **逐条一致**）。
        ///
        /// ⚠ 这里**故意复制**一份实现，而不是引用 FoodControl 的 `SimpleJson`：
        ///   本 mod 是独立程序集，不能依赖 FoodControl.Core。
        ///   转义规则必须与原实现一致，否则 JSONL 的往返一致性会被打破
        ///   （`scripts/analyze-snapshots.mjs` 依赖现有字段格式）。
        /// </summary>
        private static string Quote(string s)
        {
            if (s == null) return "\"\"";
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        internal static string N(double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) return "0";
            return d.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>写一个字符串数组</summary>
        internal static void WriteStrings(StringBuilder sb, List<string> list)
        {
            if (list == null) return;
            for (int i = 0; i < list.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(Q(list[i]));
            }
        }

        /// <summary>写 `{"key":int}`（材质统计用）</summary>
        internal static void WriteCountMap(StringBuilder sb, Dictionary<string, int> map)
        {
            sb.Append('{');
            if (map != null)
            {
                bool first = true;
                foreach (var kv in map)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(Q(kv.Key)).Append(':').Append(kv.Value);
                }
            }
            sb.Append('}');
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

    /// <summary>
    /// ★ **一个殖民者的明细**（Order 45 的 `pawnDetail` 面）。
    ///
    /// 这是 `pawns[]` 的**追加面**，不是替换：`pawns[]` 里的
    /// `mood` / `food` / `expectation` 在这里**逐字同名重复一份**，
    /// 方便交叉核对（两份对不上 = 有一边读法坏了）。
    /// </summary>
    public sealed class SnapPawnDetail
    {
        public string Name = "";
        /// <summary>Colonist / Slave / Prisoner / Guest / ColonistMech / Subhuman / Animal / Other</summary>
        public string Kind = "";
        public string Gender = "";
        public double Age;
        /// <summary>主武器：`defName` 或 `defName(品质)`</summary>
        public string Weapon = "";
        /// <summary>穿的：`defName` 或 `defName(品质/材质)`</summary>
        public List<string> Apparel = new List<string>();
        public List<SnapSkill> Skills = new List<SnapSkill>();
        public List<string> Traits = new List<string>();
        public SnapHealth Health = new SnapHealth();
        public double Mood;
        public double Food;
        public string Expectation = "";
        public List<SnapWorkPriority> WorkPriorities = new List<SnapWorkPriority>();

        public void Write(StringBuilder sb)
        {
            sb.Append("{\"name\":").Append(ColonySnapshot.Q(Name));
            sb.Append(",\"kind\":").Append(ColonySnapshot.Q(Kind));
            sb.Append(",\"gender\":").Append(ColonySnapshot.Q(Gender));
            sb.Append(",\"age\":").Append(ColonySnapshot.N(Age));
            sb.Append(",\"weapon\":").Append(ColonySnapshot.Q(Weapon));
            sb.Append(",\"apparel\":[");
            ColonySnapshot.WriteStrings(sb, Apparel);
            sb.Append("],\"skills\":[");
            for (int i = 0; i < Skills.Count; i++)
            {
                if (i > 0) sb.Append(',');
                Skills[i].Write(sb);
            }
            sb.Append("],\"traits\":[");
            ColonySnapshot.WriteStrings(sb, Traits);
            sb.Append("],\"health\":");
            Health.Write(sb);
            sb.Append(",\"mood\":").Append(ColonySnapshot.N(Mood));
            sb.Append(",\"food\":").Append(ColonySnapshot.N(Food));
            sb.Append(",\"expectation\":").Append(ColonySnapshot.Q(Expectation));
            sb.Append(",\"workPriorities\":[");
            for (int i = 0; i < WorkPriorities.Count; i++)
            {
                if (i > 0) sb.Append(',');
                WorkPriorities[i].Write(sb);
            }
            sb.Append("]}");
        }
    }

    /// <summary>一个技能（`pawnDetail.skills[]`）</summary>
    public sealed class SnapSkill
    {
        public string Def = "";
        public int Level;
        /// <summary>激情（None / Minor / Major），读不到就空串</summary>
        public string Passion = "";

        public void Write(StringBuilder sb)
        {
            sb.Append("{\"def\":").Append(ColonySnapshot.Q(Def));
            sb.Append(",\"level\":").Append(Level);
            sb.Append(",\"passion\":").Append(ColonySnapshot.Q(Passion));
            sb.Append('}');
        }
    }

    /// <summary>健康概括（`pawnDetail.health`）</summary>
    public sealed class SnapHealth
    {
        /// <summary>`HealthUtility.GetGeneralConditionLabel` 的概括（拿不到就空串）</summary>
        public string Summary = "";
        public bool Downed;
        /// <summary>关键能力值：Consciousness / Moving / Manipulation / Sight</summary>
        public List<SnapCapacity> Capacities = new List<SnapCapacity>();

        public void Write(StringBuilder sb)
        {
            sb.Append("{\"summary\":").Append(ColonySnapshot.Q(Summary));
            sb.Append(",\"downed\":").Append(Downed ? "true" : "false");
            sb.Append(",\"capacities\":[");
            for (int i = 0; i < Capacities.Count; i++)
            {
                if (i > 0) sb.Append(',');
                Capacities[i].Write(sb);
            }
            sb.Append("]}");
        }
    }

    /// <summary>一个能力值（0..1）</summary>
    public sealed class SnapCapacity
    {
        public string Def = "";
        public double Level;

        public void Write(StringBuilder sb)
        {
            sb.Append("{\"def\":").Append(ColonySnapshot.Q(Def));
            sb.Append(",\"level\":").Append(ColonySnapshot.N(Level));
            sb.Append('}');
        }
    }

    /// <summary>一条工作优先级（`pawnDetail.workPriorities[]`）</summary>
    public sealed class SnapWorkPriority
    {
        public string WorkType = "";
        public int Priority;

        public void Write(StringBuilder sb)
        {
            sb.Append("{\"workType\":").Append(ColonySnapshot.Q(WorkType));
            sb.Append(",\"priority\":").Append(Priority);
            sb.Append('}');
        }
    }

    /// <summary>
    /// ★ **一个房间**（Order 46 的 `rooms` 面）。
    ///
    /// 用户最在意的是**变化**：一个房间昨天是卧室、今天是储藏区；
    /// 13×13 的大房间被一隔为二；外墙从木头换成石砖。
    /// 所以这里既记"职责"（`role`），也记**空间边界**（`rect`/`cellCount`）
    /// 和**材质构成**（`wallMaterials`/`floorMaterials`）——
    /// 三者合起来才能看出"隔断"和"换材"。
    /// </summary>
    public sealed class SnapRoom
    {
        /// <summary>稳定 id（`Room.ID`）</summary>
        public int Id;
        /// <summary>房间职责：`RoomRoleDef.defName`（Bedroom / Kitchen / Stockpile / Workshop / Barracks / None …）</summary>
        public string Role = "";
        /// <summary>职责的人话名（`RoomRoleDef.LabelCap`）</summary>
        public string RoleLabel = "";
        /// <summary>房间名（床的 `RoomOwnerName`，没有就空串）</summary>
        public string Label = "";
        public int CellCount;
        /// <summary>`[x, z, w, h]`（`Room.ExtentsClose`）</summary>
        public int[] Rect = new int[] { 0, 0, 0, 0 };
        /// <summary>有顶格数 == 格数（全有顶）</summary>
        public bool Roofed;
        /// <summary>没顶的格子数（>0 = 部分露天）</summary>
        public int OpenRoofCount;
        public bool IsPrisonCell;
        public double Temperature;
        /// <summary>美观（`RoomStatDef` "Beauty" 的房间分；读不到为 0）</summary>
        public double Beauty;
        /// <summary>边界上的墙/门：`{defName: 段数}`</summary>
        public Dictionary<string, int> WallMaterials = new Dictionary<string, int>();
        /// <summary>地板：`{terrainDefName: 格数}`</summary>
        public Dictionary<string, int> FloorMaterials = new Dictionary<string, int>();
        /// <summary>房间里的东西：`{defName[材质]: 个数}`（边界上的墙/门 + 房间内部的物品）</summary>
        public Dictionary<string, int> Buildings = new Dictionary<string, int>();

        public void Write(StringBuilder sb)
        {
            sb.Append("{\"id\":").Append(Id);
            sb.Append(",\"role\":").Append(ColonySnapshot.Q(Role));
            sb.Append(",\"roleLabel\":").Append(ColonySnapshot.Q(RoleLabel));
            sb.Append(",\"label\":").Append(ColonySnapshot.Q(Label));
            sb.Append(",\"cellCount\":").Append(CellCount);
            sb.Append(",\"rect\":[")
              .Append(Rect[0]).Append(',').Append(Rect[1]).Append(',')
              .Append(Rect[2]).Append(',').Append(Rect[3]).Append(']');
            sb.Append(",\"roofed\":").Append(Roofed ? "true" : "false");
            sb.Append(",\"openRoofCount\":").Append(OpenRoofCount);
            sb.Append(",\"isPrisonCell\":").Append(IsPrisonCell ? "true" : "false");
            sb.Append(",\"temperature\":").Append(ColonySnapshot.N(Temperature));
            sb.Append(",\"beauty\":").Append(ColonySnapshot.N(Beauty));
            sb.Append(",\"wallMaterials\":");
            ColonySnapshot.WriteCountMap(sb, WallMaterials);
            sb.Append(",\"floorMaterials\":");
            ColonySnapshot.WriteCountMap(sb, FloorMaterials);
            sb.Append(",\"buildings\":");
            ColonySnapshot.WriteCountMap(sb, Buildings);
            sb.Append('}');
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
