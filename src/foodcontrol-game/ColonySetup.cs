using System;
using System.Collections.Generic;
using System.Linq;
using Verse;
using RimWorld;

namespace FoodControl.Game
{
    /// <summary>一次性初始化操作的结果（进遥测，也给桥接看）。</summary>
    public sealed class SetupActionResult
    {
        /// <summary>是否本次真的做了事（false = 之前已经做过 / 没有要改的）</summary>
        public bool DidWork;

        /// <summary>扫描到多少个被禁取的东西</summary>
        public int Scanned;

        /// <summary>改了多少个</summary>
        public int Changed;

        /// <summary>回读后还剩多少个（**校验用**：必须为 0 才算干净）</summary>
        public int Remaining;

        /// <summary>顺带解锁的"可搬运物品"的堆数</summary>
        public int ItemStacks;

        /// <summary>顺带解锁的建材（木/石/钢等）堆数 —— 盖房要用</summary>
        public int BuildingMaterialStacks;

        public string Detail = "";

        public override string ToString()
        {
            return string.Format("扫描 {0} / 解锁 {1} / 剩余 {2}（物品 {3}，建材 {4}）{5}",
                Scanned, Changed, Remaining, ItemStacks, BuildingMaterialStacks,
                string.IsNullOrEmpty(Detail) ? "" : " | " + Detail);
        }
    }

    /// <summary>
    /// 殖民地的**初始化操作**。
    ///
    /// 这里放的是玩家开局习惯做的"家务活"。第一条是**允许所有被禁取的物资**：
    ///
    /// ## 为什么这条重要
    ///
    /// RimWorld 里新刷出来的东西默认可能是 **forbidden（禁取）** 的，小人不会去搬。
    /// 玩家开局的习惯动作是「全选 → 允许」，否则小人会**干看着一堆资源挨饿/没法盖房**。
    ///
    /// 实测（`foodprobe/forbidden_stats`）：新开的 debug 殖民地有 **50 个**被禁取的东西
    /// （含木头/钢铁等建材）。不处理的话，后续"盖房子"会因为拿不到建材而卡住。
    ///
    /// ## ⚠ 为什么不是"只做一次"（这是踩过的坑）
    ///
    /// 最初设计成**只做一次**（一个 `_done` 标记）。结果实测发现：
    /// **它在地图刚生成、debug 开局的物资还没刷出来的时候就跑了** ——
    /// 那一刻扫到 0 个禁取物，于是把自己标成"已完成"，**此后永远不再检查**。
    /// 日志证据：
    /// ```
    /// [FoodControl] 一次性初始化：扫描 0 / 解锁 0 / 剩余 0 | 没有需要解锁的东西
    /// ```
    /// 而随后地图上实际有 49 个被禁取的物品。
    ///
    /// 教训：「只做一次」这个前提本身就是错的。游戏里完全可能出现
    /// **开局之后才出现的禁取物**（空投舱、商队、新生成的建筑/物品），
    /// 只检查一次就永远漏掉。
    ///
    /// ## 现在的策略：**按游戏日重复检查**（幂等）
    ///
    /// - 每天（跟着日闭环）检查一次；没有需要解锁的就立刻返回，**开销可以忽略**
    ///   （一次 `listerThings` 遍历，全图也就几万项，且一天只做一次）
    /// - 幂等：已经全允许时什么都不做，不会反复刷日志
    /// - 也可以在任意时刻手动触发（测试钩子）
    /// </summary>
    public static class ColonySetup
    {
        /// <summary>总开关（测试可以临时关掉，避免它干扰别的场景）</summary>
        public static bool UnforbidEnabled = true;

        /// <summary>上次做过的游戏日（-1 = 还没做过）。按天去重，避免每 tick 扫全图。</summary>
        private static int _lastSetupDay = int.MinValue;

        /// <summary>历史上累计解锁过多少个（诊断用）</summary>
        private static int _totalUnforbidden;

        /// <summary>测试/调试用：清掉"今天已经做过"的标记，让下次调用重跑一遍。</summary>
        public static void ResetForTesting()
        {
            _lastSetupDay = int.MinValue;
        }

        /// <summary>上次做过的游戏日（存档用）</summary>
        public static int LastSetupDay
        {
            get { return _lastSetupDay; }
            set { _lastSetupDay = value; }
        }

        /// <summary>历史上累计解锁过多少个（存档用 + 诊断）</summary>
        public static int TotalUnforbidden
        {
            get { return _totalUnforbidden; }
            set { _totalUnforbidden = value; }
        }

        /// <summary>
        /// 每个决策周期的入口（由 `FoodControlGameComponent` 调）。
        /// 同一天内重复调用只做一次；换天会再做一次（因为可能又出现了新的禁取物）。
        /// </summary>
        /// <param name="day">当前游戏日（由调用方算好，避免这里重复算）</param>
        public static SetupActionResult Tick(Map map, int day)
        {
            if (!UnforbidEnabled) return null;
            if (map == null) return null;
            if (_lastSetupDay == day) return null;      // 今天已经检查过了

            _lastSetupDay = day;

            SetupActionResult r = UnforbidAllImpl(map, true);
            if (r != null && r.Changed > 0)
            {
                _totalUnforbidden += r.Changed;
            }
            return r;
        }

        /// <summary>强制跑一次（测试钩子 / 需要立刻处理的场合），**不受按天去重影响**。</summary>
        public static SetupActionResult RunNow(Map map)
        {
            return UnforbidAllImpl(map, true);
        }

        /// <summary>
        /// **一键允许**地图上所有被禁取的东西。
        /// </summary>
        /// <param name="onlyItems">
        /// true = 只处理"可搬运的物品"（Item 类）。默认 true：
        /// 建筑/植物/蓝图通常不需要、也不该被改。建材（木/石/钢）属于 Item，会被覆盖到。
        /// </param>
        public static SetupActionResult UnforbidAllImpl(Map map, bool onlyItems)
        {
            var result = new SetupActionResult();
            if (map == null)
            {
                result.Detail = "没有地图";
                return result;
            }

            try
            {
                // 先 ToList() 固化一份：改 comp 状态时不想让枚举器看到变化
                List<Thing> all = map.listerThings.AllThings.ToList();

                // ── 第一遍：解锁 ──
                foreach (Thing th in all)
                {
                    try
                    {
                        if (th == null) continue;

                        // ⚠ 用 CompForbiddable（不是已废弃的 IsForbidden 字段）
                        CompForbiddable comp = th.TryGetComp<CompForbiddable>();
                        if (comp == null || !comp.Forbidden) continue;

                        bool isItem = th.def != null && th.def.category == ThingCategory.Item;
                        if (onlyItems && !isItem) continue;

                        result.Scanned++;
                        comp.Forbidden = false;
                        result.Changed++;

                        if (isItem)
                        {
                            result.ItemStacks++;
                            if (IsBuildingMaterial(th.def)) result.BuildingMaterialStacks++;
                        }
                    }
                    catch (Exception e)
                    {
                        if (string.IsNullOrEmpty(result.Detail))
                            result.Detail = "个别项失败：" + e.GetType().Name + " " + e.Message;
                    }
                }

                result.DidWork = result.Changed > 0;

                // ── 第二遍：★ 回读校验，不假定自己成功了 ──
                foreach (Thing th in map.listerThings.AllThings)
                {
                    try
                    {
                        if (th == null) continue;
                        CompForbiddable comp = th.TryGetComp<CompForbiddable>();
                        if (comp == null || !comp.Forbidden) continue;

                        bool isItem = th.def != null && th.def.category == ThingCategory.Item;
                        if (onlyItems && !isItem) continue;
                        result.Remaining++;
                    }
                    catch (Exception)
                    {
                        // 回读失败不致命，但要在结果里体现出来（下面按 Remaining 判定）
                    }
                }

                if (!result.DidWork && result.Scanned == 0)
                    result.Detail = "没有需要解锁的东西";
                else if (result.Remaining > 0)
                    result.Detail = "⚠ 回读仍有剩余，可能有取不到的东西";
            }
            catch (Exception e)
            {
                result.Detail = "整体失败：" + e.Message;
                try { Log.Error("[FoodControl] UnforbidAll 失败：" + e); } catch (Exception) { }
            }

            return result;
        }

        /// <summary>
        /// 判断是不是"建材"（盖房子要用）。
        ///
        /// 不硬编码 defName 列表 —— 用游戏的 stuff 机制：能当 stuff 用的东西就是建材候选。
        /// （木/石/钢/布/玻璃等都通过 `ThingDef.IsStuff` 体现。）
        /// </summary>
        public static bool IsBuildingMaterial(ThingDef def)
        {
            if (def == null) return false;
            try
            {
                if (def.IsStuff) return true;
            }
            catch (Exception) { }
            return false;
        }

        /// <summary>
        /// 统计当前手上**能用于建造**的建材数量（按 def 分堆）。
        /// 这是 S4「按现有资源选材质」的输入 —— 不猜，直接数。
        /// </summary>
        public static Dictionary<string, int> CountBuildingMaterials(Map map)
        {
            var counts = new Dictionary<string, int>();
            if (map == null) return counts;

            try
            {
                foreach (Thing th in map.listerThings.AllThings)
                {
                    try
                    {
                        if (th == null || th.def == null) continue;
                        if (th.def.category != ThingCategory.Item) continue;
                        if (!IsBuildingMaterial(th.def)) continue;
                        if (!counts.ContainsKey(th.def.defName)) counts[th.def.defName] = 0;
                        counts[th.def.defName] += th.stackCount;
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception e)
            {
                try { Log.Warning("[FoodControl] CountBuildingMaterials 失败：" + e.Message); } catch (Exception) { }
            }

            return counts;
        }
    }
}
