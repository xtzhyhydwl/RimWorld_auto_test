// BikeProbe · Harmony 入口与解绑前缀（探针 A17 / A1a / A1b / A1c）
//
// ★ A17 的问题原文：“Harmony 注入入口能否挂上，以及『补丁是否挂上』的自检是否有效。”
//   入口形状原本**未确证**。IL 从工坊 Harmony（`0Harmony.dll` 2.4.3.0）读到：
//       ·  `HarmonyLib.Harmony` 的 `.ctor(string)`（public）
//       ·  `void PatchAll()` / `void PatchAll(System.Reflection.Assembly)`（public）
//       ·  `MethodInfo Patch(MethodBase, HarmonyMethod prefix, HarmonyMethod postfix, ...)`（public，**实例方法**）
//       ·  `static HarmonyLib.Patches GetPatchInfo(System.Reflection.MethodBase)`（public + static）
//       ·  `IEnumerable<MethodBase> GetPatchedMethods()`（public，**实例方法**）+ `static GetAllPatchedMethods()`
//       ·  `HarmonyLib.Patches` 的 public 字段：`Prefixes` / `Postfixes` / `Transpilers` / `Finalizers`
//          以及 public 属性 `Owners`（补丁 owner 的 Harmony id 列表）
//       ·  `HarmonyLib.AccessTools.Method(Type, string, Type[], Type[])`
//       ·  `HarmonyLib.HarmonyMethod` 的 `.ctor(MethodInfo)` / `.ctor(MethodInfo,int,string[],string[],Nullable<bool>)`
//
// ★★★ 2026-10-13（run-2）修复的**静默缺陷**（run-1 的根因，已确证）★★★
//   run-1 的入口是 `Instance.PatchAll(Assembly.GetExecutingAssembly())`，而 `BikeProbe.dll` 里
//   **一个 `[HarmonyPatch]` 注解都没有**（run-1 用 TypeRef 表 + 原始字节双重核对过：
//   `HarmonyPatch = 0` 次出现）。`PatchAll` 在**扫不到任何被注解的类时不会抛异常、不打 Warning、
//   正常返回** ⇒ `ExitMap` / `Kill` 两个前缀**从未挂上** ⇒ A1a / A1b / A1c 一行没打。
//   ⇒ 修法（两条一起上，各自独立）：
//     ① **显式 `harmony.Patch(target, prefix, postfix)`**：目标用 `AccessTools.Method` 逐个解析，
//        **每个目标单独 try/catch 并打一行结果**，任何一个挂不上都会响亮地报在日志里。
//     ② 同时给前缀方法补 `[HarmonyPatch(typeof(Pawn), nameof(Pawn.ExitMap), new[]{typeof(bool), typeof(Rot4)})]`
//        注解作为兜底（这样将来谁只想用 `PatchAll` 也不会再掉进同一个坑），但**本轮不调 PatchAll**：
//        注解写错时 `PatchAll` 又会静默吞掉，显式路径才能给出证据。
//   ⇒ 独立验证（不信自检，也不信 `Patch()` 不抛异常）：`Harmony.GetPatchedMethods()`
//      是 Harmony 自己的全局注册表，A17 的三项自检**同时**比对这个集合与 `GetPatchInfo`。
//
// ★ A1a / A1b / A1c：两条解绑防线都用 **Prefix**。
//   · ExitMap：真值表 §2.8 / §A.2-13 的 IL 逐条复核结论 —— `if (carryTracker?.CarriedThing != null)` 块里
//     `child == null`（抱的不是 Verse.Pawn）时会走 `carryTracker.CarriedThing.Destroy(DestroyMode.Vanish)`
//     （IL_0101，**无条件销毁**）⇒ 不提前摘车，车必被销毁。这就是必须打前缀的根据。
//     而 `RimWorld.Planet.CaravanExitMapUtility::{ExitMapAndJoinOrCreateCaravan, ExitMapAndCreateCaravan}`
//     内部都会 `callvirt Pawn::ExitMap(false, dir)` **重入一次** ⇒ 前缀必须幂等（A1b）。
//   · Kill：真值表 §2.8 已确证 `Verse.Pawn::Kill` 本身对随身容器**零解绑**；
//     且 `carryTracker.ExposeData` 走 `LookMode.Deep` ⇒ 不摘车，车会跟着尸体被深存。
//     Prefix 里先抓引用 → 幂等摘车落地 → `return true` 放行原版。
//   ⚠ **幂等不靠标志位**：第二次进入时 `CarriedThing` 已经不是我们的车 ⇒ 自然 no-op（方案 §② A1b 的硬要求）。

using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace BikeProbe
{
    /// <summary>解绑动作的唯一写入口（A1a/A1b/A1c 共用）。</summary>
    public static class ProbeUnbind
    {
        public static int ExitMapPrefixHits;
        public static int KillPrefixHits;
        public static int TrueHits;

        /// <summary>
        /// ★ run-2 新增的**阴性对照开关**（探针调试动作会用，见 ProbeBike.cs 的 gizmo）：
        /// 置 true 时前缀**照样打日志、照样计数，但不摘车**，只 `return true` 放行原版。
        /// 用途 = 在真实游戏里验证"不挂补丁会发生什么"（真值表 §A.2-13 的 IL 推论：
        /// 非 Pawn 的携带物会被 `Destroy(Vanish)`）——这是"必须打这个前缀"的独立证据，
        /// 不能只靠"patch 挂上了"来自证。
        /// </summary>
        public static bool BypassPrefix;

        public static void Unbind(Pawn pawn, string probe, string tag)
        {
            if (pawn == null)
            {
                ProbeLog.Line(probe, tag + " pawn=null");
                return;
            }

            Pawn_CarryTracker ct = pawn.carryTracker;
            Thing carried = ct != null ? ct.CarriedThing : null;
            // ⚠ 只认**建筑形态**的 ProbeBike 为本项目的车：它是"非 Pawn 的携带物"，
            //   正是 ExitMap 里会被无条件 Destroy 的那一类（A1a 的实验对象）。
            //   物品形态 MinifiedBike 会走另一路（gizmo 里 mount-only 的阴性对照）。
            bool ours = carried is ProbeBike;

            ProbeLog.Line(probe, tag + " 前缀进入 pawn=" + pawn.ThingID
                          + " carriedThing=" + (carried == null ? "null" : carried.def.defName)
                          + " isOurBikeBuilding=" + ours
                          + " bypass=" + BypassPrefix
                          + " teleporting=" + pawn.teleporting
                          + " spawned=" + pawn.Spawned
                          + " mapHeld=" + (pawn.MapHeld == null ? "null" : "ok")
                          + " tick=" + GenTicks.TicksGame);

            if (BypassPrefix)
            {
                ProbeLog.Line(probe, tag + " 阴性对照：BypassPrefix=true ⇒ 本前缀**故意不摘车**，"
                              + "直接放行原版（用来验证『不打补丁车会怎样』）");
                return;
            }

            if (!ours)
            {
                ProbeLog.Line(probe, tag + " no-op（手里不是我们的车 ⇒ 幂等就靠这一点，不用标志位）"
                              + " trueHits=" + TrueHits);
                return;
            }

            TrueHits++;

            IntVec3 cell = pawn.PositionHeld;
            Map map = pawn.MapHeld;
            Thing dropped = null;
            bool ok = false;
            try
            {
                ok = ct.TryDropCarriedThing(cell, ThingPlaceMode.Near, out dropped, null);
            }
            catch (Exception ex)
            {
                ProbeLog.Warn(probe, tag + " TryDropCarriedThing 抛异常：" + ex.GetType().Name + " " + ex.Message);
            }

            if (dropped == null)
            {
                // ★ 方案 §3.5-③ 的硬要求：摘车动作必须自带去向决策，禁止“只是不管”。
                //   兜底路径 = 从容器移除 + 显式落地。
                //
                // ★★★ run-2 实测修的一个真 bug（商队路径）★★★
                //   走 `CaravanExitMapUtility::ExitMapAndCreateCaravan` 时，小人**已经被商队
                //   DeSpawn 掉**，于是：
                //     · `pawn.PositionHeld` = `IntVec3.Invalid`（= (0,0,0)）
                //     · `pawn.MapHeld` = null
                //   第一版兜底直接拿这两样去 `GenSpawn.Spawn(carried, cell, map)` ⇒
                //   根本没落地（日志：`兜底结果 removed=True dropped=NULL droppedSpawned=False`），
                //   车于是变成**不在任何格子上、也不在任何容器里的无主对象**
                //   —— 正是这条兜底本来要消灭的那种状态（A1a 判据的"车丢了"）。
                //   ⇒ 现在先算一个**合法落点**：优先原格 → 地图上的最近合法格 → 原版
                //     `DropPodUtility.DropThingsNear` 的同族做法（clone + GenSpawn）。
                ProbeLog.Warn(probe, tag + " TryDropCarriedThing 没交出东西 ⇒ 走兜底：定位合法落点 + Remove + GenSpawn.Spawn");
                bool removed = false;
                IntVec3 fallback = ResolveFallbackCell(pawn, map, cell, out map);
                try
                {
                    if (carried.holdingOwner != null)
                    {
                        removed = carried.holdingOwner.Remove(carried);
                    }
                    if (map != null && fallback.IsValid)
                    {
                        dropped = GenSpawn.Spawn(carried, fallback, map, WipeMode.Vanish);
                    }
                }
                catch (Exception ex)
                {
                    ProbeLog.Fail(probe, tag + " 兜底落地失败：" + ex.GetType().Name + " " + ex.Message);
                }
                ProbeLog.Line(probe, tag + " 兜底结果 removed=" + removed
                              + " fallbackCell=" + fallback
                              + " map=" + (map == null ? "null" : map.uniqueID.ToString())
                              + " dropped=" + (dropped == null ? "NULL" : dropped.def.defName)
                              + " droppedSpawned=" + (dropped != null && dropped.Spawned));
                if (dropped == null)
                {
                    ProbeLog.Fail(probe, tag + " 兜底也失败 ⇒ 车变成无主对象（Spawned=false 且不在任何容器里）");
                }
            }

            ProbeLog.Line(probe, tag + " 摘车完成 ok=" + ok
                          + " dropped=" + (dropped == null ? "NULL" : dropped.def.defName + "/" + dropped.ThingID)
                          + " droppedSpawned=" + (dropped != null && dropped.Spawned)
                          + " droppedDestroyed=" + (dropped != null && dropped.Destroyed)
                          + " droppedPos=" + (dropped == null ? default(IntVec3) : dropped.Position)
                          + " trueHits=" + TrueHits
                          + " tick=" + GenTicks.TicksGame);

            if (map != null)
            {
                ProbeCounts.LogAll(probe, map, tag + "-after-unbind");
            }
        }

        /// <summary>
        /// 兜底落点：优先用"原格"，原格非法（`IntVec3.Invalid`，商队路径就是这样）或不可站，
        /// 就在**任一张地图**上找离它最近的合法格。实在找不到就返回 Invalid（调用方会响亮地报失败）。
        ///
        /// 同时把 `map` 修正成"真的有这张地图"（商队路径下 `pawn.MapHeld` 是 null，
        /// 要靠 `Find.AnyPlayerHomeMap` / `Find.CurrentMap` 兜）。
        /// </summary>
        private static IntVec3 ResolveFallbackCell(Pawn pawn, Map mapIn, IntVec3 cellIn, out Map mapOut)
        {
            mapOut = mapIn;
            if (mapOut == null && pawn != null)
            {
                mapOut = pawn.Map;
            }
            if (mapOut == null && pawn != null)
            {
                mapOut = pawn.MapHeld;
            }
            if (mapOut == null)
            {
                mapOut = Find.CurrentMap;
            }
            if (mapOut == null)
            {
                mapOut = Find.AnyPlayerHomeMap;
            }

            if (mapOut == null)
            {
                return IntVec3.Invalid;
            }

            if (cellIn.IsValid && cellIn.InBounds(mapOut) && cellIn.Standable(mapOut))
            {
                return cellIn;
            }

            // 商队路径下 pawn 已经不在图上 ⇒ 没有"小人站位"可用，退回地图中心附近找一格。
            IntVec3 anchor = cellIn.IsValid && cellIn.InBounds(mapOut)
                ? cellIn
                : mapOut.Center;
            IntVec3 found = CellFinder.StandableCellNear(anchor, mapOut, 12f, null);
            if (found.IsValid)
            {
                return found;
            }

            return CellFinder.RandomCell(mapOut);
        }
    }

    [StaticConstructorOnStartup]
    public static class Probe_Harmony
    {
        public const string HarmonyId = "dsh.bikeprobe";
        public static Harmony Instance;

        static Probe_Harmony()
        {
            ProbeLog.Line("A17", "Harmony 入口静态构造器开始 id=" + HarmonyId
                          + " jsonl=" + ProbeLog.JsonlPath);

            Instance = new Harmony(HarmonyId);
            MethodInfo exitMap = AccessTools.Method(
                typeof(Pawn), "ExitMap", new[] { typeof(bool), typeof(Rot4) });
            MethodInfo kill = AccessTools.Method(
                typeof(Pawn), "Kill", new[] { typeof(Nullable<DamageInfo>), typeof(Hediff) });

            ProbeLog.Line("A17", "本程序集里带 [HarmonyPatch] 注解的类数=" + CountAnnotatedPatchClasses()
                          + "（run-1 实测这个数是 0 —— 那就是当时 PatchAll 静默失效的根因；"
                          + "本轮改走下面的显式 Patch，注解只做兜底）");

            // ① ExitMap：前缀（摘车）+ 后缀（观测离图后的状态，A1a 的收口证据）
            PatchOne(exitMap, "ExitMap",
                new HarmonyMethod(typeof(Probe_Harmony).GetMethod(
                    "Prefix_ExitMap", BindingFlags.Static | BindingFlags.NonPublic)),
                new HarmonyMethod(typeof(Probe_Harmony).GetMethod(
                    "Postfix_ExitMap", BindingFlags.Static | BindingFlags.NonPublic)));

            // ② Kill：只前缀（先抓引用 → 幂等摘车 → 放行原版）
            PatchOne(kill, "Kill",
                new HarmonyMethod(typeof(Probe_Harmony).GetMethod(
                    "Prefix_Kill", BindingFlags.Static | BindingFlags.NonPublic)),
                null);

            VerifyPatched(exitMap, kill);

            SelfCheck();

            Probe_MapComponent.SelfCheck(Find.CurrentMap, "startup");
            ProbeCounts.LogAll("A3", Find.CurrentMap, "startup");
        }

        // ── 显式挂补丁的机器（不再依赖 PatchAll 的注解扫描）────────────

        private static void PatchOne(MethodBase target, string label,
            HarmonyMethod prefix, HarmonyMethod postfix)
        {
            if (target == null)
            {
                ProbeLog.Fail("A17", "显式挂 " + label + " 失败：目标方法解析为 null（MethodBase=null）");
                return;
            }

            try
            {
                Instance.Patch(target, prefix, postfix, null, null);
                ProbeLog.Line("A17", "显式挂 " + label + " 成功：目标=" + Describe(target)
                              + " prefix=" + Describe(prefix == null ? null : prefix.method)
                              + " postfix=" + Describe(postfix == null ? null : postfix.method)
                              + "（Patch() 正常返回）");
            }
            catch (Exception ex)
            {
                // ★ 这里的响亮失败正是 run-1 缺的那一环：挂不上必须能看见。
                ProbeLog.Fail("A17", "显式挂 " + label + " 抛异常：" + ex.GetType().Name + " " + ex.Message);
            }
        }

        /// <summary>
        /// ★ 独立于自检（`GetPatchInfo`）的第二重证据：`Harmony.GetPatchedMethods()` 是
        /// Harmony 自己的全局注册表。两个来源一致才叫真挂上。
        /// </summary>
        private static void VerifyPatched(params MethodInfo[] targets)
        {
            MethodBase[] actuallyPatched;
            try
            {
                // ⚠ 实测（run-2 编译期）：`GetPatchedMethods()` 是**实例方法**，
                //   不能写成 `Harmony.GetPatchedMethods()`（CS0120）。用我们的实例调。
                actuallyPatched = Enumerable.ToArray(Instance.GetPatchedMethods());
            }
            catch (Exception ex)
            {
                ProbeLog.Fail("A17", "Harmony.GetPatchedMethods() 抛异常：" + ex.GetType().Name + " " + ex.Message);
                return;
            }

            ProbeLog.Line("A17", "独立验证：Harmony.GetPatchedMethods() 返回 " + actuallyPatched.Length
                          + " 个方法：" + string.Join(" ; ", Enumerable.Select(actuallyPatched, Describe).ToArray()));

            for (int i = 0; i < targets.Length; i++)
            {
                MethodInfo t = targets[i];
                if (t == null)
                {
                    continue;
                }

                bool seen = Enumerable.Contains(actuallyPatched, (MethodBase)t);
                ProbeLog.Line("A17", "独立验证 " + Describe(t) + " 在 GetPatchedMethods() 里=" + seen);
            }
        }

        private static int CountAnnotatedPatchClasses()
        {
            try
            {
                Type[] types = Assembly.GetExecutingAssembly().GetTypes();
                int n = 0;
                for (int i = 0; i < types.Length; i++)
                {
                    object[] attrs = types[i].GetCustomAttributes(typeof(HarmonyPatch), false);
                    if (attrs != null && attrs.Length > 0)
                    {
                        n++;
                    }
                }
                return n;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        // ── 实际补丁 ────────────────────────────────────────────────
        //
        // ★ 参数绑定：`__instance` 绑定实例（两个方法都是实例方法），
        //   ExitMap 的前缀还显式接收 `bool allowedToJoinOrCreateCaravan` 与 `Rot4 exitDir`
        //   （按位置用 `__0` / `__1`，这样不受参数名改名影响）。
        [HarmonyPatch(typeof(Pawn), nameof(Pawn.ExitMap), new[] { typeof(bool), typeof(Rot4) })]
        private static bool Prefix_ExitMap(Pawn __instance, bool __0, Rot4 __1)
        {
            ProbeUnbind.ExitMapPrefixHits++;
            try
            {
                ProbeLog.Line("A1a", "ExitMap#P" + ProbeUnbind.ExitMapPrefixHits
                              + " 参数 allowedToJoinOrCreateCaravan=" + __0
                              + " exitDir=" + __1 + " tick=" + GenTicks.TicksGame);
                ProbeUnbind.Unbind(__instance, "A1a", "ExitMap#P" + ProbeUnbind.ExitMapPrefixHits);
            }
            catch (Exception ex)
            {
                ProbeLog.Fail("A1a", "前缀自身抛异常：" + ex);
            }
            return true; // ★ 永远放行原版，不吞 ExitMap 的其余行为
        }

        /// <summary>
        /// 观测"原版 ExitMap 跑完之后"的状态：小人还在图里吗？车还在手里吗？
        /// A1a 判据里"前缀真拦住了销毁路径"的收口证据。
        /// </summary>
        private static void Postfix_ExitMap(Pawn __instance)
        {
            try
            {
                if (__instance == null)
                {
                    return;
                }
                Thing carried = __instance.carryTracker != null ? __instance.carryTracker.CarriedThing : null;
                Map map = __instance.MapHeld;
                ProbeLog.Line("A1a", "ExitMap 后缀：pawn=" + __instance.ThingID
                              + " spawned=" + __instance.Spawned
                              + " mapHeld=" + (map == null ? "null" : "ok")
                              + " carriedThing=" + (carried == null
                                    ? "null" : carried.def.defName + "/" + carried.ThingID)
                              + " trueHits=" + ProbeUnbind.TrueHits
                              + " tick=" + GenTicks.TicksGame);
                if (map != null)
                {
                    ProbeCounts.LogAll("A1a", map, "exitmap-postfix");
                }
            }
            catch (Exception ex)
            {
                ProbeLog.Fail("A1a", "后缀自身抛异常：" + ex);
            }
        }

        [HarmonyPatch(typeof(Pawn), nameof(Pawn.Kill), new[] { typeof(Nullable<DamageInfo>), typeof(Hediff) })]
        private static bool Prefix_Kill(Pawn __instance, Nullable<DamageInfo> __0, Hediff __1)
        {
            ProbeUnbind.KillPrefixHits++;
            try
            {
                ProbeLog.Line("A1c", "Kill#P" + ProbeUnbind.KillPrefixHits
                              + " 参数 dinfo=" + (__0.HasValue ? "hasValue" : "null")
                              + " exactCulprit=" + (__1 == null ? "null" : __1.def.defName)
                              + " tick=" + GenTicks.TicksGame);
                ProbeUnbind.Unbind(__instance, "A1c", "Kill#P" + ProbeUnbind.KillPrefixHits);
            }
            catch (Exception ex)
            {
                ProbeLog.Fail("A1c", "前缀自身抛异常：" + ex);
            }
            return true; // ★ 先摘车，再放行原版 Kill
        }

        // ── 自检（A17）────────────────────────────────────────────
        private static void SelfCheck()
        {
            MethodInfo exitMap = AccessTools.Method(
                typeof(Pawn), "ExitMap", new[] { typeof(bool), typeof(Rot4) });
            MethodInfo kill = AccessTools.Method(
                typeof(Pawn), "Kill", new[] { typeof(Nullable<DamageInfo>), typeof(Hediff) });
            MethodInfo ticksPerMove = AccessTools.Method(
                typeof(Pawn), "TicksPerMove", new[] { typeof(bool) });

            ProbeLog.Line("A17", "目标解析： ExitMap=" + Describe(exitMap)
                          + " | Kill=" + Describe(kill)
                          + " | TicksPerMove=" + Describe(ticksPerMove));

            Report("A17-1 ExitMap(前缀+后缀)", exitMap, 1, 1);
            Report("A17-2 Kill(前缀)", kill, 1, 0);
            // TicksPerMove 是**产品侧**的第 3 个补丁（加速），探针**故意不挂**：
            // 它在这里的作用是“一个已知应当 0 命中的目标”，作为第二重阴性对照。
            // ⚠ 它的期望本来就是 0 命中 ⇒ 判词必须是「符合期望」，不能念成「已挂上」（run-1 的措辞 bug）。
            Report("A17-3 TicksPerMove(产品侧补丁·探针故意不挂·期望 0 命中)", ticksPerMove, 0, 0);

            // ★ 阴性对照（方案 §⑤ 第 4 条）：故意写错的目标必须被自检报成“未挂上”。
            Type bogus = AccessTools.TypeByName("BikeProbe.ThisTypeDoesNotExist_NegativeControl");
            if (bogus == null)
            {
                ProbeLog.Line("A17-neg", "阴性对照成立：故意写错的目标类型 'BikeProbe.ThisTypeDoesNotExist_NegativeControl' "
                              + "解析为 null ⇒ 自检对它只会报『未挂上』。自检不是恒真的。");
            }
            else
            {
                ProbeLog.Fail("A17-neg", "阴性对照失败：不存在的类型竟然解析出来了 → 自检不可信");
            }
        }

        private static void Report(string label, MethodInfo target, int expectPrefixes, int expectPostfixes)
        {
            if (target == null)
            {
                ProbeLog.Fail("A17", label + " 目标方法解析失败（MethodBase=null）⇒ 视为【未挂上】");
                return;
            }

            Patches patches = Harmony.GetPatchInfo(target);
            // `GetPatchInfo` 对“完全没有补丁的方法”返回 null，这**不是**错误：
            // 语义上等于 Prefixes=Postfixes=Transpilers=0（A17-3 的 TicksPerMove 就靠这条）。
            int prefixes = patches == null || patches.Prefixes == null
                ? 0 : Enumerable.Count(patches.Prefixes);
            int postfixes = patches == null || patches.Postfixes == null
                ? 0 : Enumerable.Count(patches.Postfixes);
            int transpilers = patches == null || patches.Transpilers == null
                ? 0 : Enumerable.Count(patches.Transpilers);
            string owners = patches == null || patches.Owners == null
                ? "-" : string.Join(",", Enumerable.ToArray(patches.Owners));

            // ★ 2026-10-13（run-2）措辞修复：run-1 这里把布尔量 = 「实测命中数符合期望」
            //   直接念成「已挂上 / 未挂上」，于是 A17-3（**期望本来就是 0 命中**的阴性对照）
            //   被念成「已挂上」，与它自己的标签「探针故意不挂」自相矛盾。
            //   现在拆成三个互不混淆的字段：期望 N / 实测 M / 一致=bool。
            bool consistent = prefixes == expectPrefixes && postfixes == expectPostfixes;

            string line = label + " 目标=" + target.DeclaringType.FullName + "::" + target.Name
                          + " Patches" + (patches == null ? "=null" : "!=null")
                          + " Prefixes=" + prefixes + " Postfixes=" + postfixes
                          + " Transpilers=" + transpilers
                          + " owners=" + owners
                          + " | 期望 prefix=" + expectPrefixes + " postfix=" + expectPostfixes
                          + " / 实测 prefix=" + prefixes + " postfix=" + postfixes
                          + " / 一致=" + consistent
                          + " ⇒ 判定=" + (expectPrefixes >= 1
                                ? (consistent ? "【真挂上】" : "【未挂上】")
                                : (consistent ? "【符合期望·本项期望 0 命中】" : "【不符期望·多了补丁】"));
            if (consistent)
            {
                ProbeLog.Line("A17", line);
            }
            else
            {
                ProbeLog.Fail("A17", line);
            }
        }

        private static string Describe(MethodBase m)
        {
            return m == null ? "null" : m.DeclaringType.FullName + "::" + m.Name;
        }
    }
}
