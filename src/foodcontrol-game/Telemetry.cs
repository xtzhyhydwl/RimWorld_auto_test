using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using FoodControl.Core;
using Verse;

namespace FoodControl.Game
{
    /// <summary>
    /// 遥测：每轮决策往 `telemetry.jsonl` 追加一行 JSON。
    ///
    /// **为什么这是测试的观测通道（设计文档 §七bis）**：
    ///   测试脚本不需要通过桥接读游戏内存 —— mod 自己把结构化状态写进文件，
    ///   隔离实例的 `-savedatafolder` 指向 `profiles/vanilla`，我们有完整文件访问权。
    ///   这条通道比桥接稳得多，也不依赖任何编译。
    ///
    /// ⚠ 两条硬性要求：
    ///   1. **写文件失败绝不能让 mod 崩**（玩家存档目录可能只读/满了）→ 全部 try/catch，降级成 <see cref="LastError"/>
    ///   2. **不引入新依赖** → 手写极简 JSON（RimWorld mod 里引 Newtonsoft 会带来版本冲突风险）
    /// </summary>
    public static class Telemetry
    {
        /// <summary>落盘目录名（在 `GenFilePaths.SaveDataFolderPath` 之下）</summary>
        public const string FolderName = "FoodControl";
        public const string FileName = "telemetry.jsonl";

        /// <summary>超过这个大小就轮转成 telemetry.1.jsonl，避免长跑把盘写满</summary>
        private const long MaxBytes = 4L * 1024 * 1024;

        private const int RecentCap = 200;
        private static readonly List<string> _recent = new List<string>();

        /// <summary>最近一次写出的 JSON 原文（桥接/测试钩子直接读这个，不用去碰文件）</summary>
        public static string LastLine = "";
        /// <summary>最近一次失败原因（空 = 没失败过）</summary>
        public static string LastError = "";
        public static int LinesWritten;

        /// <summary>
        /// 遥测文件的绝对路径。
        /// ⚠ 这是**推断**的落点：设计文档只说「`GenFilePaths.SaveDataFolderPath` 下」，
        ///   我选了子目录 `FoodControl/telemetry.jsonl`（比散在存档根目录干净）。
        ///   真实路径同时会写进 Player.log（第一条日志会打印一次），免得对不上账。
        /// </summary>
        public static string TelemetryPath
        {
            get
            {
                string root = null;
                try
                {
                    root = GenFilePaths.SaveDataFolderPath;   // 确证：public static get（元数据确证）
                }
                catch (Exception e)
                {
                    LastError = "拿不到 SaveDataFolderPath: " + e.Message;
                }
                if (string.IsNullOrEmpty(root)) root = Path.GetTempPath();
                return Path.Combine(Path.Combine(root, FolderName), FileName);
            }
        }

        /// <summary>最近 n 行（内存环形缓冲，桥接钩子用）</summary>
        public static List<string> Recent(int n)
        {
            lock (_recent)
            {
                int take = n > 0 && n < _recent.Count ? n : _recent.Count;
                var outList = new List<string>(take);
                for (int i = _recent.Count - take; i < _recent.Count; i++) outList.Add(_recent[i]);
                return outList;
            }
        }

        /// <summary>写一行。**任何参数都可以为 null**（感知失败时也要留痕：留下的是"这一轮失败了"这个事实）。</summary>
        public static void Write(
            GameReadings readings, FoodState state, PlotPlan plan,
            FoodDecision decision, ExecutionResult exec, string source, string error)
        {
            string line;
            try
            {
                line = BuildJson(readings, state, plan, decision, exec, source, error);
            }
            catch (Exception e)
            {
                LastError = "构造 JSON 失败: " + e.Message;
                return;
            }

            LastLine = line;
            lock (_recent)
            {
                _recent.Add(line);
                if (_recent.Count > RecentCap) _recent.RemoveAt(0);
            }

            try
            {
                string path = TelemetryPath;
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                if (File.Exists(path))
                {
                    var fi = new FileInfo(path);
                    if (fi.Length > MaxBytes)
                    {
                        string rotated = Path.Combine(dir, "telemetry.1.jsonl");
                        try
                        {
                            if (File.Exists(rotated)) File.Delete(rotated);
                            File.Move(path, rotated);
                        }
                        catch (Exception) { /* 轮转失败不影响继续追加 */ }
                    }
                }

                File.AppendAllText(path, line + "\n");   // \n 而不是 Environment.NewLine：jsonl 用 LF 更好处理
                LinesWritten++;
                LastError = "";
            }
            catch (Exception e)
            {
                // ★ 写文件失败**绝不**向上抛：遥测只是观测通道，不能因为它把闭环弄停
                LastError = "写遥测失败: " + e.Message;
                try { Log.Warning("[FoodControl] 写遥测失败（已降级，不影响游戏）：" + e.Message); } catch (Exception) { }
            }
        }

        // ------------------------------------------------------------------
        // 极简 JSON（手写，避免引入 Newtonsoft 依赖）
        // ------------------------------------------------------------------

        /// <summary>
        /// 字段名按任务约定：`tick / day / season / stock / burnRatePerDay / reserveDays /
        /// projectedReserve[] / minProjectedReserve / firstShortfallDay / action.* /
        /// arableCellsAvailable / baseCenter{} / plantedGroups[]`。
        /// 其余是这个实现额外加的诊断字段（全部以"能对账"为目的）。
        /// </summary>
        public static string BuildJson(
            GameReadings readings, FoodState state, PlotPlan plan,
            FoodDecision decision, ExecutionResult exec, string source, string error)
        {
            var sb = new StringBuilder(1024);
            sb.Append('{');
            bool first = true;

            KV(sb, ref first, "source", Q(source != null ? source : ""));
            KV(sb, ref first, "tick", readings != null ? readings.Tick.ToString(CultureInfo.InvariantCulture) : "null");
            KV(sb, ref first, "day", readings != null ? CalendarDay(readings).ToString(CultureInfo.InvariantCulture) : "null");
            KV(sb, ref first, "dayOfYear", readings != null ? readings.DayOfYear.ToString(CultureInfo.InvariantCulture) : "null");
            KV(sb, ref first, "year", readings != null ? readings.Year.ToString(CultureInfo.InvariantCulture) : "null");
            KV(sb, ref first, "hour", readings != null ? readings.HourOfDay.ToString(CultureInfo.InvariantCulture) : "null");
            KV(sb, ref first, "season", Q(readings != null ? readings.Season : ""));

            // ---- 存量 / 消耗 / 预测 ----
            KV(sb, ref first, "stock", N(state != null ? state.Stock : (readings != null ? readings.StockNutrition : 0.0)));
            KV(sb, ref first, "burnRatePerDay", N(state != null ? state.BurnRatePerDay : 0.0));
            KV(sb, ref first, "reserveDays", state != null ? N(state.ReserveDays) : "null");
            KV(sb, ref first, "colonistCount", readings != null ? readings.ColonistCount.ToString(CultureInfo.InvariantCulture) : "null");
            KV(sb, ref first, "nutritionPerColonistPerDay", N(StateReader.NutritionPerColonistPerDay));
            KV(sb, ref first, "foodStacks", readings != null ? readings.FoodStackCount.ToString(CultureInfo.InvariantCulture) : "null");
            KV(sb, ref first, "foodDefs", readings != null ? readings.FoodDefCount.ToString(CultureInfo.InvariantCulture) : "null");

            KV(sb, ref first, "projectedReserve", decision != null ? DoubleArray(decision.ProjectedReserve) : "[]");
            KV(sb, ref first, "minProjectedReserve", decision != null ? N(decision.MinProjectedReserve) : "null");
            KV(sb, ref first, "firstShortfallDay", decision != null ? decision.FirstShortfallDay.ToString(CultureInfo.InvariantCulture) : "null");
            KV(sb, ref first, "lowThreshold", decision != null ? N(decision.LowThreshold) : "null");
            KV(sb, ref first, "highThreshold", decision != null ? N(decision.HighThreshold) : "null");
            KV(sb, ref first, "neededCells", decision != null ? decision.NeededCells.ToString(CultureInfo.InvariantCulture) : "null");
            KV(sb, ref first, "needsMoreLand", decision != null ? (decision.NeedsMoreLand ? "true" : "false") : "null");

            // ---- 决策动作（任务指定的字段名 action.kind / action.plantDefName / action.cells / action.reason）----
            FoodAction act = decision != null ? decision.Action : null;
            KV(sb, ref first, "action.kind", Q(act != null ? act.Kind.ToString() : ""));
            KV(sb, ref first, "action.plantDefName", Q(act != null ? act.PlantDefName : ""));
            KV(sb, ref first, "action.cells", act != null ? act.Cells.ToString(CultureInfo.InvariantCulture) : "0");
            KV(sb, ref first, "action.reason", Q(act != null ? act.Reason : ""));

            // ---- 土地 ----
            KV(sb, ref first, "arableCellsAvailable", plan != null ? plan.ArableCellsAvailable.ToString(CultureInfo.InvariantCulture) : "null");
            KV(sb, ref first, "arableCellsInExistingZones", plan != null ? plan.ArableCellsInExistingZones.ToString(CultureInfo.InvariantCulture) : "null");
            KV(sb, ref first, "arableScanCells", plan != null ? plan.ArableScanCells.ToString(CultureInfo.InvariantCulture) : "null");
            KV(sb, ref first, "plannedCropDef", Q(plan != null && plan.CropDef != null ? plan.CropDef.defName : ""));
            KV(sb, ref first, "mapSize", plan != null && plan.Grid != null
                ? "[" + plan.Grid.Width.ToString(CultureInfo.InvariantCulture) + ","
                    + plan.Grid.Height.ToString(CultureInfo.InvariantCulture) + "]"
                : "null");

            // ---- 基地中心 ----
            if (plan != null && plan.Center != null)
            {
                KV(sb, ref first, "baseCenter", "{"
                    + "\"kind\":" + Q(plan.Center.Kind.ToString())
                    + ",\"x\":" + plan.Center.X.ToString(CultureInfo.InvariantCulture)
                    + ",\"z\":" + plan.Center.Z.ToString(CultureInfo.InvariantCulture)
                    + ",\"note\":" + Q(plan.Center.Note) + "}");
            }
            else
            {
                KV(sb, ref first, "baseCenter", "null");
            }

            // ---- 地块 / 现有区 ----
            if (plan != null && plan.BestPlot != null)
            {
                FarmPlot p = plan.BestPlot;
                KV(sb, ref first, "bestPlot", "{"
                    + "\"x\":" + p.X.ToString(CultureInfo.InvariantCulture)
                    + ",\"z\":" + p.Z.ToString(CultureInfo.InvariantCulture)
                    + ",\"w\":" + p.Width.ToString(CultureInfo.InvariantCulture)
                    + ",\"h\":" + p.Height.ToString(CultureInfo.InvariantCulture)
                    + ",\"area\":" + p.Area.ToString(CultureInfo.InvariantCulture)
                    + ",\"avgDistance\":" + N(p.AvgDistance)
                    + ",\"avgFertility\":" + N(p.AvgFertility)
                    + ",\"score\":" + N(p.Score) + "}");
            }
            else
            {
                KV(sb, ref first, "bestPlot", "null");
            }

            KV(sb, ref first, "growingZones", readings != null ? readings.GrowingZoneCount.ToString(CultureInfo.InvariantCulture) : "null");
            KV(sb, ref first, "growingZoneCells", readings != null ? readings.GrowingZoneCellCount.ToString(CultureInfo.InvariantCulture) : "null");
            KV(sb, ref first, "zones", ZonesJson(plan));

            // ---- 在种作物 ----
            KV(sb, ref first, "plantedGroups", PlantedGroupsJson(readings, state));
            KV(sb, ref first, "cultivatedCells", readings != null ? readings.CultivatedCellCount.ToString(CultureInfo.InvariantCulture) : "null");
            KV(sb, ref first, "wildPlantsIgnored", readings != null ? readings.WildPlantCount.ToString(CultureInfo.InvariantCulture) : "null");
            KV(sb, ref first, "zeroGrowthRateCells", readings != null ? readings.ZeroGrowthRateCells.ToString(CultureInfo.InvariantCulture) : "null");

            // ---- 执行结果 ----
            if (exec != null)
            {
                KV(sb, ref first, "execution", "{"
                    + "\"method\":" + Q(exec.Method)
                    + ",\"success\":" + (exec.Success ? "true" : "false")
                    + ",\"verified\":" + (exec.Verified ? "true" : "false")
                    + ",\"zoneId\":" + Q(exec.ZoneId)
                    + ",\"zoneCellsAfter\":" + exec.ZoneCellCountAfter.ToString(CultureInfo.InvariantCulture)
                    + ",\"cellsRequested\":" + exec.CellsRequested.ToString(CultureInfo.InvariantCulture)
                    + ",\"cellsAdded\":" + exec.CellsAdded.ToString(CultureInfo.InvariantCulture)
                    + ",\"cellsAlreadyFree\":" + exec.CellsAlreadyFree.ToString(CultureInfo.InvariantCulture)
                    + ",\"plantDefSet\":" + (exec.PlantDefSet ? "true" : "false")
                    + ",\"detail\":" + Q(exec.Detail) + "}");
            }
            else
            {
                KV(sb, ref first, "execution", "null");
            }

            // ---- 错误（感知/决策/执行任一环节的降级信息）----
            string allError = Join(readings != null ? readings.Error : null, error);
            KV(sb, ref first, "error", Q(allError));
            KV(sb, ref first, "planNote", Q(plan != null ? plan.Note : ""));

            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>绝对日序号（年 × 60 + 年内天）。用 <see cref="FoodState"/> 时也能对账。</summary>
        private static int CalendarDay(GameReadings r)
        {
            return r.Year * 60 + r.DayOfYear;   // GenDate.DaysPerYear == 60（确证：const int）
        }

        private static string ZonesJson(PlotPlan plan)
        {
            if (plan == null || plan.ZonePatches == null || plan.ZonePatches.Count == 0) return "[]";
            var sb = new StringBuilder();
            sb.Append('[');
            for (int i = 0; i < plan.ZonePatches.Count; i++)
            {
                ZonePatch p = plan.ZonePatches[i];
                if (i > 0) sb.Append(',');
                int extend = 0;
                List<IntVec3> ex;
                if (plan.ExtendCellsById != null && plan.ExtendCellsById.TryGetValue(p.ZoneId, out ex) && ex != null)
                {
                    extend = ex.Count;
                }
                Zone_Growing z;
                string plant = "";
                int cells = -1;
                if (plan.ZoneById != null && plan.ZoneById.TryGetValue(p.ZoneId, out z) && z != null)
                {
                    cells = z.CellCount;
                    ThingDef d = null;
                    try { d = z.GetPlantDefToGrow(); } catch (Exception) { }
                    plant = d != null ? d.defName : "";
                }
                sb.Append('{')
                  .Append("\"id\":").Append(Q(p.ZoneId))
                  .Append(",\"plant\":").Append(Q(plant))
                  .Append(",\"cells\":").Append(cells.ToString(CultureInfo.InvariantCulture))
                  .Append(",\"free\":").Append(p.FreeCount.ToString(CultureInfo.InvariantCulture))
                  .Append(",\"extendable\":").Append(extend.ToString(CultureInfo.InvariantCulture))
                  .Append(",\"score\":").Append(N(p.Score))
                  .Append('}');
            }
            sb.Append(']');
            return sb.ToString();
        }

        private static string PlantedGroupsJson(GameReadings readings, FoodState state)
        {
            var sb = new StringBuilder();
            sb.Append('[');
            bool first = true;

            if (readings != null && readings.CultivatedPlants != null)
            {
                // 原始读数（含 DefName、格数、平均 growth / growthRate、growDays）
                for (int i = 0; i < readings.CultivatedPlants.Count; i++)
                {
                    PlantBucket b = readings.CultivatedPlants[i];
                    if (b == null) continue;
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append('{')
                      .Append("\"def\":").Append(Q(b.DefName))
                      .Append(",\"cells\":").Append(b.CellCount.ToString(CultureInfo.InvariantCulture))
                      .Append(",\"growth\":").Append(N(b.AvgGrowth))
                      .Append(",\"growthRate\":").Append(N(b.AvgGrowthRate))
                      .Append(",\"growDays\":").Append(N(b.GrowDays))
                      .Append('}');
                }
            }
            else if (state != null && state.Planted != null)
            {
                // 降级：至少把 Core 看到的东西写出来
                for (int i = 0; i < state.Planted.Count; i++)
                {
                    PlantGroup g = state.Planted[i];
                    if (g == null) continue;
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append('{')
                      .Append("\"def\":").Append(Q(g.PlantDefName))
                      .Append(",\"cells\":").Append(g.CellCount.ToString(CultureInfo.InvariantCulture))
                      .Append(",\"growth\":").Append(N(g.Growth))
                      .Append(",\"growthRate\":").Append(N(g.GrowthRate))
                      .Append(",\"growDays\":").Append(N(g.GrowDays))
                      .Append('}');
                }
            }

            sb.Append(']');
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        // 最小 JSON 原语
        // ------------------------------------------------------------------

        private static void KV(StringBuilder sb, ref bool first, string key, string rawValue)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append('"').Append(Escape(key)).Append("\":").Append(rawValue);
        }

        private static string Q(string s)
        {
            if (s == null) return "\"\"";
            return "\"" + Escape(s) + "\"";
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 8);
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
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// 数字格式化。**必须用 InvariantCulture** —— 中文/欧洲区域设置会把小数点写成逗号，
        /// 那样 jsonl 就不是合法 JSON 了（测试脚本解析会炸）。
        /// </summary>
        private static string N(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "null";
            double r = Math.Round(v, 4);
            if (r == 0) r = 0;   // 消掉 -0
            return r.ToString("0.####", CultureInfo.InvariantCulture);
        }

        private static string DoubleArray(double[] arr)
        {
            if (arr == null || arr.Length == 0) return "[]";
            var sb = new StringBuilder(arr.Length * 8);
            sb.Append('[');
            for (int i = 0; i < arr.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(N(arr[i]));
            }
            sb.Append(']');
            return sb.ToString();
        }

        private static string Join(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return b != null ? b : "";
            if (string.IsNullOrEmpty(b)) return a;
            return a + " | " + b;
        }
    }
}
