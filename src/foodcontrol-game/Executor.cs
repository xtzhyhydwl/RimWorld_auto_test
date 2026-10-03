using System;
using System.Collections.Generic;
using FoodControl.Core;
using RimWorld;
using Verse;

namespace FoodControl.Game
{
    /// <summary>执行结果。**必须诚实**：没验证通过就不算成功（这是设计文档 §「不要假定成功」的要求）。</summary>
    public sealed class ExecutionResult
    {
        /// <summary>真的落地了吗（回读验证通过）</summary>
        public bool Success;

        /// <summary>走了哪条路：none / reuse-zone / extend-zone / new-zone</summary>
        public string Method = "none";

        public string ZoneId = "";
        public int ZoneCellCountAfter = -1;

        public int CellsRequested;
        public int CellsAdded;
        public int CellsAlreadyFree;

        /// <summary>是否改写了区的作物设定</summary>
        public bool PlantDefSet;

        /// <summary>建区之后**重新读** `map.zoneManager.AllZones` 的验证结论</summary>
        public bool Verified;

        public string Detail = "";
    }

    /// <summary>
    /// 执行层：`FoodControl.Core.FoodAction` → 真的建种植区 / 设作物。
    ///
    /// 设计文档 §「执行」把执行排成 A/B/C 三层（A = 游戏内直接调 API 最稳），本类就是 A 层。
    ///
    /// ⚠ 本类里最值钱的一条结论（**用 IL 反汇编查出来的，不是猜的**）：
    ///   `ZoneMaker.MakeZoneWithCells(z, cells)` 的实现**只有** `foreach (c in cells) z.AddCell(c)`，
    ///   **它不注册 zone**。而 `Zone.AddCell` 只做「cells.Add + zoneManager.AddZoneGridCell + 网格脏标记」，
    ///   也不注册。`ZoneManager.RegisterZone` 才负责 `allZones.Add + PostRegister`。
    ///   如果不注册：`map.zoneManager.AllZones` 里根本没有这个区，而**播种作业恰恰是扫 AllZones 的**
    ///   （`WorkGiver_Grower.PotentialWorkCellsGlobal` 的 IL：`ZoneManager::get_AllZones` → `isinst Zone_Growing`），
    ///   ⇒ 建出来的区会是个**没人去种的幽灵区**。所以本类的顺序是游戏自己的顺序
    ///   （`Designator_ZoneAdd.DesignateMultiCell` 的 IL：先 `MakeNewZone` → `RegisterZone` → 再 `AddCell`）：
    ///       new Zone_Growing → RegisterZone → ZoneMaker.MakeZoneWithCells → SetPlantDefToGrow
    ///   光靠"编译通过"是发现不了这一条的 —— 签名全对，行为错。
    /// </summary>
    public static class Executor
    {
        /// <summary>
        /// 执行一个动作。**永不向调用方抛异常**（mod 异常不能把游戏搞崩），失败信息放在返回值里。
        /// </summary>
        public static ExecutionResult Execute(FoodAction action, PlotPlan plan, Map map)
        {
            var res = new ExecutionResult();
            try
            {
                return ExecuteInner(action, plan, map, res);
            }
            catch (Exception e)
            {
                res.Success = false;
                res.Detail = Append(res.Detail, "执行时异常：" + e);
                try { Log.Error("[FoodControl] 执行异常（已吞掉，不影响游戏）：" + e); } catch (Exception) { }
                return res;
            }
        }

        private static ExecutionResult ExecuteInner(FoodAction action, PlotPlan plan, Map map, ExecutionResult res)
        {
            if (action == null || action.Kind != FoodActionKind.PlantCrop)
            {
                res.Success = true;
                res.Method = "none";
                res.Detail = "动作不是 PlantCrop，无需执行";
                return res;
            }

            res.CellsRequested = action.Cells;

            if (map == null)
            {
                res.Detail = "没有地图";
                return res;
            }
            if (plan == null || plan.Grid == null)
            {
                res.Detail = "没有选址计划";
                return res;
            }
            if (plan.CropDef == null)
            {
                res.Detail = "没有作物 Def";
                return res;
            }
            if (action.Cells <= 0)
            {
                res.Success = true;
                res.Detail = "决策要求 0 格";
                return res;
            }

            int need = action.Cells;

            // ---- ① 优先复用已有种植区的空地（有就不新开地）----
            ZonePatch patch = SiteSelection.FindBestExistingZone(
                plan.Grid, plan.DistanceField, plan.ZonePatches, plan.Options);

            Zone_Growing zone = null;
            if (patch != null) plan.ZoneById.TryGetValue(patch.ZoneId, out zone);

            if (zone != null)
            {
                res.CellsAlreadyFree = patch.FreeCount;
                return ReuseZone(zone, patch, plan, map, need, res);
            }

            // ---- ② 没有空地，但某个已有区旁边还有可扩的格 → 扩它，不新开地 ----
            zone = PickZoneWithExtendCells(plan);
            if (zone != null)
            {
                return ExtendZone(zone, plan, map, need, res);
            }

            // ---- ③ 实在没有可复用的，才新建地块 ----
            return CreateNewZone(plan, map, need, res);
        }

        // ------------------------------------------------------------------
        // 复用 / 扩格
        // ------------------------------------------------------------------

        private static ExecutionResult ReuseZone(
            Zone_Growing zone, ZonePatch patch, PlotPlan plan, Map map, int need, ExecutionResult res)
        {
            res.Method = "reuse-zone";
            res.ZoneId = patch.ZoneId;

            // 作物设定：不一样才写，避免无意义地改动（SetPlantDefToGrow 内部是纯字段赋值，没有前置检查 —— 确证文档 §C9）
            if (zone.GetPlantDefToGrow() != plan.CropDef)
            {
                zone.SetPlantDefToGrow(plan.CropDef);
                res.PlantDefSet = true;
            }

            // 区里已有的空地如果还不够这一轮要种的量，就顺手把旁边可扩的格并进来
            int missing = need - patch.FreeCount;
            if (missing > 0)
            {
                List<IntVec3> extend;
                if (plan.ExtendCellsById.TryGetValue(patch.ZoneId, out extend) && extend != null && extend.Count > 0)
                {
                    res.CellsAdded = AddCells(zone, extend, missing);
                    if (res.CellsAdded > 0) res.Method = "reuse-zone+extend";
                }
                else
                {
                    res.Detail = Append(res.Detail,
                        string.Format("区里只有 {0} 格空地，比要种的 {1} 格少，且旁边没有可扩的格",
                            patch.FreeCount, need));
                }
            }

            Verify(map, zone, res);
            return res;
        }

        private static ExecutionResult ExtendZone(
            Zone_Growing zone, PlotPlan plan, Map map, int need, ExecutionResult res)
        {
            res.Method = "extend-zone";
            string id = PlotPlanner.ZoneIdOf(zone);
            res.ZoneId = id;

            if (zone.GetPlantDefToGrow() != plan.CropDef)
            {
                zone.SetPlantDefToGrow(plan.CropDef);
                res.PlantDefSet = true;
            }

            List<IntVec3> extend;
            if (plan.ExtendCellsById.TryGetValue(id, out extend) && extend != null && extend.Count > 0)
            {
                res.CellsAdded = AddCells(zone, extend, need);
            }
            else
            {
                res.Detail = Append(res.Detail, "该区旁边找不到可扩的格");
            }

            Verify(map, zone, res);
            return res;
        }

        private static int AddCells(Zone_Growing zone, List<IntVec3> candidates, int wanted)
        {
            int added = 0;
            for (int i = 0; i < candidates.Count && added < wanted; i++)
            {
                try
                {
                    zone.AddCell(candidates[i]);   // 确证文档 §C9：Zone.AddCell(IntVec3)
                    added++;
                }
                catch (Exception e)
                {
                    try { Log.Warning("[FoodControl] AddCell 失败 " + candidates[i] + " : " + e.Message); } catch (Exception) { }
                }
            }
            return added;
        }

        /// <summary>挑一个「旁边有空闲可种格」的已有种植区。有多个时选离基地最近的（距离场平均）。</summary>
        private static Zone_Growing PickZoneWithExtendCells(PlotPlan plan)
        {
            Zone_Growing best = null;
            double bestScore = double.NegativeInfinity;

            foreach (KeyValuePair<string, Zone_Growing> kv in plan.ZoneById)
            {
                List<IntVec3> extend;
                if (!plan.ExtendCellsById.TryGetValue(kv.Key, out extend)) continue;
                if (extend == null || extend.Count == 0) continue;

                // 用「可扩格的平均距离」当分数，越近越好（缺距离场信息时给个中性值）
                double sum = 0;
                int counted = 0;
                for (int i = 0; i < extend.Count; i++)
                {
                    IntVec3 c = extend[i];
                    if (!plan.Grid.InBounds(c.x, c.z)) continue;
                    int idx = plan.Grid.Index(c.x, c.z);
                    double d = plan.DistanceField != null ? plan.DistanceField[idx] : plan.Options.ComfortableDistance;
                    if (double.IsInfinity(d)) continue;
                    sum += d;
                    counted++;
                }
                double avg = counted > 0 ? sum / counted : plan.Options.ComfortableDistance;
                double score = extend.Count - avg;      // 格多、离得近 → 分数高

                if (score > bestScore)
                {
                    bestScore = score;
                    best = kv.Value;
                }
            }

            return best;
        }

        // ------------------------------------------------------------------
        // 新建
        // ------------------------------------------------------------------

        private static ExecutionResult CreateNewZone(PlotPlan plan, Map map, int need, ExecutionResult res)
        {
            res.Method = "new-zone";

            FarmPlot plot = plan.BestPlot;
            if (plot == null)
            {
                res.Detail = Append(res.Detail, "没有可用的新地块（Core 的 FindBestPlot 返回 null）");
                return res;
            }

            List<IntVec3> cells = CollectPlotCells(plan, plot, need);
            if (cells.Count == 0)
            {
                res.Detail = Append(res.Detail, "选出的地块里没有合法可种的格");
                return res;
            }

            var zone = new Zone_Growing(map.zoneManager);   // 确证文档 §C9

            // ① 先注册（游戏自己的顺序；不注册 = 幽灵区，小人不会去种 —— 见类注释）
            map.zoneManager.RegisterZone(zone);

            // ② 再落格
            try
            {
                ZoneMaker.MakeZoneWithCells(zone, cells);   // 设计文档指定的调用（Verse.ZoneMaker 静态类）
            }
            catch (Exception e)
            {
                res.Detail = Append(res.Detail, "MakeZoneWithCells 抛异常，已回滚：" + e.Message);
                Rollback(map, zone);
                return res;
            }

            // ③ 空区必须撤销：注册过的空种植区会让游戏 `Log.ErrorOnce("Grow zone has 0 cells: ...")`
            //    （WorkGiver_Grower 的 IL 里就是这么写的），而且它永远不会被种。
            if (zone.CellCount <= 0 || zone.cells == null || zone.cells.Count == 0)
            {
                res.Detail = Append(res.Detail, "建区后格数为 0，已回滚（不留空区）");
                Rollback(map, zone);
                return res;
            }

            zone.SetPlantDefToGrow(plan.CropDef);           // ⚠ PlantDefToGrow 是 get-only，必须用这个方法（确证文档 §C9）
            res.PlantDefSet = true;
            res.CellsAdded = zone.cells.Count;

            Verify(map, zone, res);
            return res;
        }

        /// <summary>
        /// 从一个矩形地块里挑出真正要落进种植区的格：
        ///   - 必须是可种格（`plan.Grid.Arable`，即游戏自己判过能种）
        ///   - 还要过一遍 `Designator_ZoneAdd.IsZoneableCell`（游戏判"这格允许被画进某个区吗"的公开静态方法）
        ///   - 超过本轮要种的数量时，**优先留离基地近的**（通勤成本，设计文档 §七quater）
        /// </summary>
        private static List<IntVec3> CollectPlotCells(PlotPlan plan, FarmPlot plot, int need)
        {
            var picked = new List<IntVec3>();
            var keyed = new List<KeyValuePair<double, IntVec3>>();

            for (int z = plot.Z; z < plot.Z + plot.Height; z++)
            {
                for (int x = plot.X; x < plot.X + plot.Width; x++)
                {
                    if (!plan.Grid.InBounds(x, z)) continue;
                    int idx = plan.Grid.Index(x, z);
                    if (!plan.Grid.Arable[idx]) continue;

                    var c = new IntVec3(x, 0, z);
                    if (!IsZoneable(c, plan.Map)) continue;

                    double d = plan.DistanceField != null ? plan.DistanceField[idx] : 0.0;
                    if (double.IsInfinity(d)) d = plan.Options.HardMaxDistance;
                    keyed.Add(new KeyValuePair<double, IntVec3>(d, c));
                }
            }

            keyed.Sort(delegate (KeyValuePair<double, IntVec3> a, KeyValuePair<double, IntVec3> b)
            {
                return a.Key.CompareTo(b.Key);
            });

            int take = need > 0 && need < keyed.Count ? need : keyed.Count;
            for (int i = 0; i < take; i++) picked.Add(keyed[i].Value);
            return picked;
        }

        /// <summary>游戏自己的「这格能不能被画进区」。公开静态：`Designator_ZoneAdd.IsZoneableCell(IntVec3, Map)`。</summary>
        private static bool IsZoneable(IntVec3 c, Map map)
        {
            try
            {
                AcceptanceReport report = Designator_ZoneAdd.IsZoneableCell(c, map);
                return report.Accepted;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>撤销一个刚建的区（失败路径专用）。</summary>
        private static void Rollback(Map map, Zone_Growing zone)
        {
            if (zone == null) return;
            try
            {
                zone.Delete(false);      // 游戏自己的清理：撤格 + 撤销
            }
            catch (Exception) { }
            try
            {
                if (map != null && map.zoneManager != null
                    && map.zoneManager.AllZones != null
                    && map.zoneManager.AllZones.Contains(zone))
                {
                    map.zoneManager.DeregisterZone(zone);
                }
            }
            catch (Exception) { }
        }

        // ------------------------------------------------------------------
        // 回读验证
        // ------------------------------------------------------------------

        /// <summary>
        /// **不要假定成功**：建/改完之后重新读一遍，确认区真的在 `map.zoneManager.AllZones` 里、
        /// 格数 > 0、并且格级回读（`ZoneAt`）指向同一个区。三个都过才算 `Verified`。
        /// </summary>
        private static void Verify(Map map, Zone_Growing zone, ExecutionResult res)
        {
            res.Verified = false;
            res.Success = false;
            if (map == null || zone == null || map.zoneManager == null)
            {
                res.Detail = Append(res.Detail, "验证失败：没有地图/区管理器");
                return;
            }

            try
            {
                // 回读 ①：AllZones 里有没有它
                bool found = false;
                int cellCount = -1;
                List<Zone> all = map.zoneManager.AllZones;
                if (all != null)
                {
                    for (int i = 0; i < all.Count; i++)
                    {
                        if (!ReferenceEquals(all[i], zone)) continue;
                        found = true;
                        cellCount = all[i].CellCount;
                        break;
                    }
                }

                // 回读 ②：格级 ZoneAt 是否指回这个区
                bool gridHit = false;
                if (zone.cells != null && zone.cells.Count > 0)
                {
                    for (int i = 0; i < zone.cells.Count; i++)
                    {
                        if (ReferenceEquals(map.zoneManager.ZoneAt(zone.cells[i]), zone))
                        {
                            gridHit = true;
                            break;
                        }
                    }
                }

                res.ZoneCellCountAfter = cellCount;
                res.ZoneId = PlotPlanner.ZoneIdOf(zone);
                res.Verified = found && cellCount > 0 && gridHit;
                res.Success = res.Verified;

                if (!found) res.Detail = Append(res.Detail, "⚠ 区不在 AllZones 里（注册失败）");
                if (found && cellCount <= 0) res.Detail = Append(res.Detail, "⚠ 区格数为 0");
                if (!gridHit) res.Detail = Append(res.Detail, "⚠ 格级 ZoneAt 回读不一致");
                if (res.Verified)
                {
                    res.Detail = Append(res.Detail, string.Format("{0} 格，AllZones 与 ZoneAt 双回读一致",
                        cellCount));
                }
            }
            catch (Exception e)
            {
                res.Detail = Append(res.Detail, "验证时异常：" + e.Message);
            }
        }

        private static string Append(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return b;
            return a + " | " + b;
        }
    }
}
