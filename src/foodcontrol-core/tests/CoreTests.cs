using System;
using System.Collections.Generic;
using FoodControl.Core;

/// <summary>
/// 零依赖的离线测试跑器。**故意不用 NUnit/xUnit** —— 本机没有 .NET SDK，
/// 用游戏自带的 csc 编成一个 exe，拿游戏自带的 mono 跑，比引入测试框架省事得多。
///
/// 退出码：全过 = 0，有失败 = 1（便于 CI / 脚本调用）。
/// </summary>
public static class CoreTests
{
    private static int _pass;
    private static int _fail;
    private static readonly List<string> _failures = new List<string>();

    public static int Main(string[] args)
    {
        Console.WriteLine("=== FoodControl.Core 离线测试 ===");

        RunProjectionTests();
        RunPolicyTests();
        RunSiteSelectionTests();
        RunStockpileTests();

        Console.WriteLine();
        Console.WriteLine(string.Format("=== 通过 {0} / 失败 {1} ===", _pass, _fail));
        if (_fail > 0)
        {
            Console.WriteLine("失败项：");
            for (int i = 0; i < _failures.Count; i++) Console.WriteLine("  - " + _failures[i]);
        }
        return _fail == 0 ? 0 : 1;
    }

    // ---------------- 断言助手 ----------------

    private static void Ok(string name)
    {
        _pass++;
        Console.WriteLine("  [PASS] " + name);
    }

    private static void Bad(string name, string detail)
    {
        _fail++;
        _failures.Add(name + " —— " + detail);
        Console.WriteLine("  [FAIL] " + name + " —— " + detail);
    }

    private static void Near(string name, double actual, double expected, double tol)
    {
        if (Math.Abs(actual - expected) <= tol) Ok(name);
        else Bad(name, string.Format("期望 {0}±{1}，实际 {2}", expected, tol, actual));
    }

    private static void True(string name, bool cond, string detail = "")
    {
        if (cond) Ok(name);
        else Bad(name, detail == "" ? "条件为 false" : detail);
    }

    private static void Eq(string name, object actual, object expected)
    {
        bool same = (actual == null && expected == null) || (actual != null && actual.Equals(expected));
        if (same) Ok(name);
        else Bad(name, string.Format("期望 {0}，实际 {1}", expected, actual));
    }

    // ---------------- 预测器 ----------------

    private static void RunProjectionTests()
    {
        Console.WriteLine();
        Console.WriteLine("-- Projection（预测器） --");

        // 水稻实测：growDays = 3、harvestMinGrowth = 0.65、
        //           harvestYield = 6（每株）× RawRice 营养 0.05 = 每格每周期 0.3 nutrition
        // 公式对应游戏：GrowthPerTick = GrowthRate / (60000 × growDays)
        var cal = new Calibration
        {
            HarvestYieldPerCell = 6.0,
            NutritionPerHarvestedItem = 0.05,
            HarvestMinGrowth = 0.65,
            HorizonDays = 15,
        };

        // ★ 钉住"每格产出是总量不是速率"这件事 —— 这里差 60 倍过一次
        Near("每格每周期 = 6 × 0.05 = 0.3 nutrition（不是 18！）",
            cal.NutritionPerCellPerCycle, 0.3, 1e-9);

        // GrowthRate = 1、growDays = 2、从 0 到 0.65 → 0.65 × 2 = 1.3 天
        var g = new PlantGroup("Plant_Rice", 100, 0.0, 1.0, 2.0);
        double days = Projection.DaysUntilHarvest(g, cal);
        Near("水稻 0%、GrowthRate=1、growDays=2 → 1.3 天到可收获", days, 1.3, 0.001);

        // ★ 这一条钉住最容易错的地方：终点是 0.65 而不是 1.0
        // 如果哪天有人把 HarvestMinGrowth 改回 1.0，这里会变成 2.0 天
        True("收获终点是 harvestMinGrowth(0.65) 而非 1.0（否则会算成 2.0 天）", days < 1.5,
            "实际 " + days + " 天");

        // GrowthRate 减半（比如肥力/光照差）→ 时间加倍
        var gSlow = new PlantGroup("Plant_Rice", 100, 0.0, 0.5, 2.0);
        Near("GrowthRate=0.5 → 2.6 天", Projection.DaysUntilHarvest(gSlow, cal), 2.6, 0.001);

        // growDays 加倍 → 时间加倍
        var gLong = new PlantGroup("Plant_Rice", 100, 0.0, 1.0, 4.0);
        Near("growDays=4 → 2.6 天", Projection.DaysUntilHarvest(gLong, cal), 2.6, 0.001);

        // 已经长过阈值 → 现在就能收
        var gReady = new PlantGroup("Plant_Rice", 100, 0.8, 1.0, 2.0);
        Near("growth 0.8 > 0.65 → 0 天（立刻可收）", Projection.DaysUntilHarvest(gReady, cal), 0.0, 1e-9);

        // ★ 夜里 GrowthRate = 0（机制是 Plant.Resting，不是光照因子）→ 推不出成熟时间
        var gNight = new PlantGroup("Plant_Rice", 100, 0.0, 0.0, 2.0);
        double dDark = Projection.DaysUntilHarvest(gNight, cal);
        True("GrowthRate=0（夜里）→ 不给出有限天数", double.IsPositiveInfinity(dDark) || dDark > 1e6,
            "实际返回 " + dDark);

        // ── 存量曲线 ──
        // GrowthRate=1、growDays=3 → 1.95 天到可收获 → 第 2 天首收
        // 100 格 × 0.3 = 每次 30 nutrition；复种周期 1.95 天
        //   → 第 2/3/5/7/9/11/13/15 天各有一次（ceiling 后落点）
        var st = new FoodState
        {
            Stock = 100,
            BurnRatePerDay = 2.0,
            Calibration = cal,
            Planted = new List<PlantGroup> { new PlantGroup("Plant_Rice", 100, 0.0, 1.0, 3.0) },
        };
        ForecastResult p = Projection.Forecast(st);
        Eq("曲线长度 = 窗口+1", p.ProjectedReserve.Length, 16);
        Near("第 0 天 = 当前存量", p.ProjectedReserve[0], 100, 1e-9);
        Near("第 1 天 = 100-2", p.ProjectedReserve[1], 98, 1e-9);
        Near("第 2 天首收 = 100-4+30", p.ProjectedReserve[2], 126, 1e-6);
        Near("第 2 天新增产出 = 100格×0.3", p.DailySupply[2], 30, 1e-9);

        // ★ 复种：窗口内应该收获多次，而不是只收一次
        int harvestDays = 0;
        for (int d = 1; d <= 15; d++) if (p.DailySupply[d] > 0) harvestDays++;
        True("★ 窗口内复种多次产出（不是只收一次）", harvestDays >= 5,
            "有产出的天数 = " + harvestDays + "（0.65×3 天成熟 → 15 天约 7~8 次）");

        // 总产出应该远超"只收一次"
        double totalSupply = 0;
        for (int d = 1; d <= 15; d++) totalSupply += p.DailySupply[d];
        True("★ 总产出 > 单次产出（复种累积）", totalSupply > 30 * 5,
            "总产出 " + totalSupply + " vs 单次 30");

        // 窗口外的作物不计入
        // growDays=30 且 GrowthRate=0.5 → 0.65×30/0.5 = 39 天，远超 15 天窗口
        var gFar = new PlantGroup("Plant_Corn", 100, 0.0, 0.5, 30.0);
        var stFar = new FoodState { Stock = 100, BurnRatePerDay = 2.0, Calibration = cal,
            Planted = new List<PlantGroup> { gFar } };
        ForecastResult pFar = Projection.Forecast(stFar);
        Near("窗口外成熟 → 不计产出", pFar.DailySupply[6], 0, 1e-9);
        Near("窗口外成熟 → 存量线性下降", pFar.ProjectedReserve[15], 70, 1e-9);

        // 空状态不炸
        var pEmpty = Projection.Forecast(new FoodState { Stock = 0, BurnRatePerDay = 1, Calibration = cal });
        Eq("空状态也不炸", pEmpty.ProjectedReserve.Length, 16);
    }

    // ---------------- 决策 ----------------

    private static void RunPolicyTests()
    {
        Console.WriteLine();
        Console.WriteLine("-- Policy（决策，目标带 10/7/20） --");

        var cfg = new PolicyConfig
        {
            ReserveDaysTarget = 10, ReserveDaysLow = 7, ReserveDaysHigh = 20,
            PreferredPlantDefName = "Plant_Rice",
        };
        var cal = new Calibration
        {
            HarvestYieldPerCell = 6.0, NutritionPerHarvestedItem = 0.05,
            HarvestMinGrowth = 0.65, HorizonDays = 15,
        };
        var baseState = new FoodState { BurnRatePerDay = 2.0, Calibration = cal, ArableCellsAvailable = 1000 };

        // ★ 拐点 1：食物充足 → 什么都不做
        var rich = Clone(baseState);
        rich.Stock = 1000;   // 500 天
        var d1 = Policy.Decide(rich, cfg);
        Eq("充足（500天）→ DoNothing", d1.Action.Kind, FoodActionKind.DoNothing);
        True("充足时不报 NeedsMoreLand", !d1.NeedsMoreLand);

        // ★ 拐点 2：刚好在低水位之上（7 天 = 14 nutrition）
        var okState = Clone(baseState);
        okState.Stock = 15;   // 7.5 天，15 天窗口内线性降到 0 会跌破 → 应触发
        var d2 = Policy.Decide(okState, cfg);
        Eq("7.5 天存量 + 无产出 → 触发种植（因为窗口内会跌破）", d2.Action.Kind, FoodActionKind.PlantCrop);

        // ★ 拐点 2b：存量 + 已有产出，只要全程不跌破阈值就不该动手
        //
        // 注意这里有个**实测发现**：存粮只够 7.5 天、但要等 6 天才收获时，
        // 期间存量会跌到 5（低于阈值 14）→ 触发种植是**正确**的，不是 bug。
        // 因为「依赖一次还没到手的收成」本身就是风险，提前补种才是稳健行为。
        // 所以下面用成对的用例把这个边界钉住。
        var midStock = Clone(baseState);
        midStock.Stock = 50;   // 25 天
        var dMid = Policy.Decide(midStock, cfg);
        Eq("25 天存量 + 无产出 → DoNothing", dMid.Action.Kind, FoodActionKind.DoNothing);

        var lowStock = Clone(baseState);
        lowStock.Stock = 40;   // 20 天；15 天窗口内会降到 10，低于阈值 14 → 应触发
        var dLow = Policy.Decide(lowStock, cfg);
        Eq("20 天存量 + 无产出 → 窗口内跌破 7 天 → PlantCrop", dLow.Action.Kind, FoodActionKind.PlantCrop);

        // 同样 20 天存量，但有一块即将成熟的地补上 → 不该动手
        var lowWithCrop = Clone(baseState);
        lowWithCrop.Stock = 40;
        lowWithCrop.Planted = new List<PlantGroup> { new PlantGroup("Plant_Rice", 100, 0.0, 1.0, 3.0) };
        var dLowWithCrop = Policy.Decide(lowWithCrop, cfg);
        Eq("20 天存量 + 即将收获 → DoNothing（产出补上了缺口）",
            dLowWithCrop.Action.Kind, FoodActionKind.DoNothing);

        // ★ 拐点 3：饥饿 → 种，且格数合理
        var starving = Clone(baseState);
        starving.Stock = 0;
        var d4 = Policy.Decide(starving, cfg);
        Eq("零存量 → PlantCrop", d4.Action.Kind, FoodActionKind.PlantCrop);

        // ★★ 钉住"总产出口径"这个语义 —— 这里曾经犯过一个真错误：
        //    要求"预测窗口内**每一天**存量都 ≥ 低阈值"。但第 1 天还没有任何收获，
        //    存量只减不增，这个约束**永远无法满足** → 二分搜索永远找不到解
        //    → 返回巨大值 → 被 MaxCellsPerDecision 截断成 0
        //    → 表现为"零存量却决定 DoNothing"这种荒谬结果。
        {
            ForecastResult pf = Projection.Forecast(starving);
            int h = cal.HorizonDays;
            double windowSupply = pf.ProjectedReserve[h] - starving.Stock + starving.BurnRatePerDay * h;
            double need = starving.BurnRatePerDay * h + starving.BurnRatePerDay * cfg.ReserveDaysTarget - starving.Stock;
            True("★ 零存量时确实给出了正的格数（不是被截断成 0）", d4.Action.Cells > 0,
                "cells=" + d4.Action.Cells);
            True("★ 种下的格数×每格产出 ≥ 需要补的总量",
                d4.Action.Cells * cal.NutritionPerCellPerCycle * 7 >= need - windowSupply - 4,
                "cells=" + d4.Action.Cells + " need=" + need.ToString("F1")
                + " 已在种产出=" + windowSupply.ToString("F1"));

            // ★★ 真正该验的：**把新种下的地算进去之后，预测曲线应该变健康**
            //    （上一条断言曾经写错——传的是没算新地的 forecast，那当然还是完蛋的曲线）
            var extra = new double[h + 1];
            int cycle = (int)Math.Ceiling(cal.HarvestMinGrowth * 3.0);   // 1.95 → 2
            for (int d = cycle; d <= h; d += cycle)
            {
                extra[d] += d4.Action.Cells * cal.NutritionPerCellPerCycle;
            }
            ForecastResult after = Projection.Forecast(starving, extra);
            True("★★ 把新种的算进去后，期末存量转正",
                after.ProjectedReserve[h] > 0,
                "期末=" + after.ProjectedReserve[h].ToString("F1"));
            // 注意：仍然不要求"每一天都在水位线上" —— 第 1 天没有收获，那做不到。
            // 只要求窗口内总产出把期末抬到正数以上，且曲线整体在改善。
            True("★★ 种地后最低点明显抬高（曲线整体改善）",
                after.MinReserve > pf.MinReserve,
                "最低点 " + pf.MinReserve.ToString("F1") + " → " + after.MinReserve.ToString("F1"));
        }
        Eq("  种的作物 = 配置里的水稻", d4.Action.PlantDefName, "Plant_Rice");
        True("  格数为正", d4.Action.Cells > 0, "实际 " + d4.Action.Cells);
        True("  格数不超过单次上限", d4.Action.Cells <= cfg.MaxCellsPerDecision);
        True("  理由非空（要进遥测）", !string.IsNullOrEmpty(d4.Action.Reason));

        // 缺口越大，种得越多（单调性）
        var s1 = Clone(baseState); s1.Stock = 0;
        var s2 = Clone(baseState); s2.Stock = 40;
        int cellsHungry = Policy.Decide(s1, cfg).Action.Cells;
        int cellsMild = Policy.Decide(s2, cfg).Action.Cells;
        True("缺口越大种得越多（单调）", cellsHungry >= cellsMild,
            string.Format("饿 {0} 格 vs 微缺 {1} 格", cellsHungry, cellsMild));

        // 没地 → 报 NeedsMoreLand，而不是硬种
        var noLand = Clone(baseState);
        noLand.Stock = 0;
        noLand.ArableCellsAvailable = 0;
        var d5 = Policy.Decide(noLand, cfg);
        True("没地时标记 NeedsMoreLand", d5.NeedsMoreLand);
        Eq("没地时不硬种", d5.Action.Kind, FoodActionKind.DoNothing);

        // 地不够（少于缺口）也要标记
        var fewLand = Clone(baseState);
        fewLand.Stock = 0;
        fewLand.ArableCellsAvailable = 3;   // 低于 MinCellsPerDecision 附近的极小值
        var d6 = Policy.Decide(fewLand, cfg);
        True("地不足时标记 NeedsMoreLand 或缩小规模", d6.NeedsMoreLand || d6.Action.Cells <= 3,
            "NeedsMoreLand=" + d6.NeedsMoreLand + " cells=" + d6.Action.Cells);

        // 预测曲线要带出来（遥测要写）
        True("决策结果带出预测曲线", d4.ProjectedReserve != null && d4.ProjectedReserve.Length == 16);
        True("决策结果记录首次跌破天数", d4.FirstShortfallDay > 0, "实际 " + d4.FirstShortfallDay);
    }

    // ---------------- 选址 ----------------

    private static void RunSiteSelectionTests()
    {
        Console.WriteLine();
        Console.WriteLine("-- SiteSelection（选址） --");

        // 造一张 120x120 的图：全是沃土，除了右下角一片沙漠
        const int W = 120, H = 120;
        PassabilityMap map = MakeMap(W, H);
        for (int z = 0; z < H; z++)
            for (int x = 0; x < W; x++)
            {
                int i = map.Index(x, z);
                bool desert = x > 90 && z > 90;
                map.Arable[i] = !desert;
                map.Fertility[i] = desert ? 0.0 : 1.0;
                map.MoveCost[i] = 1.0;
            }

        // 基地在 (20,20)
        int centerIdx = map.Index(20, 20);
        double[] dist = SiteSelection.ComputeDistanceField(map, new List<int> { centerIdx }, 500);
        True("距离场算出来了", dist != null && dist[centerIdx] == 0);
        Near("基地自身距离 0", dist[centerIdx], 0, 1e-9);
        Near("(21,20) 距离 1", dist[map.Index(21, 20)], 1, 1e-9);
        True("远处距离更大", dist[map.Index(60, 60)] > dist[map.Index(30, 30)]);

        var opts = new SiteSelectionOptions
        {
            MinSide = 8, MaxSide = 12, ComfortableDistance = 40,
            DistancePenaltyPerCell = 1.0, HardMaxDistance = 120,
        };

        // ★ 核心断言：必须选基地附近，而不是地图边角
        FarmPlot best = SiteSelection.FindBestPlot(map, dist, 100, opts);
        True("选出了地", best != null);
        if (best != null)
        {
            double distToBase = Math.Sqrt(Math.Pow(best.X - 20, 2) + Math.Pow(best.Z - 20, 2));
            True(string.Format("选的地靠近基地（中心距 {0:F0} 格，应 < 40）", distToBase), distToBase < 40);
            True("选的地不在沙漠角落", !(best.X > 90 && best.Z > 90));
            Console.WriteLine("       选中：" + best);
        }

        // 全不可种 → 返回 null，而不是硬选一块
        PassabilityMap barren = MakeMap(40, 40);
        double[] distB = SiteSelection.ComputeDistanceField(barren, new List<int> { barren.Index(20, 20) }, 200);
        FarmPlot none = SiteSelection.FindBestPlot(barren, distB, 100, opts);
        Eq("全不可种 → 返回 null", none, null);

        // ★ 距离惩罚真的起作用：把惩罚调到极端，应该更靠近基地
        var greedy = new SiteSelectionOptions
        {
            MinSide = 8, MaxSide = 12, ComfortableDistance = 5, DistancePenaltyPerCell = 50.0,
            HardMaxDistance = 120,
        };
        FarmPlot near = SiteSelection.FindBestPlot(map, dist, 100, greedy);
        True("重惩罚下仍能选出地", near != null);
        if (near != null && best != null)
        {
            double dNear = Math.Sqrt(Math.Pow(near.X - 20, 2) + Math.Pow(near.Z - 20, 2));
            double dBest = Math.Sqrt(Math.Pow(best.X - 20, 2) + Math.Pow(best.Z - 20, 2));
            True(string.Format("重惩罚选址不比轻惩罚更远（{0:F0} vs {1:F0}）", dNear, dBest), dNear <= dBest + 1e-6);
        }

        // ★ 肥力：同样的距离偏好下，肥力高的赢
        PassabilityMap fertMap = MakeMap(60, 60);
        for (int z = 0; z < 60; z++)
            for (int x = 0; x < 60; x++)
            {
                int i = fertMap.Index(x, z);
                fertMap.Arable[i] = true;
                fertMap.MoveCost[i] = 1.0;
                // 左半 0.5 肥力，右半 1.5 肥力
                fertMap.Fertility[i] = x < 30 ? 0.5 : 1.5;
            }
        double[] distF = SiteSelection.ComputeDistanceField(fertMap, new List<int> { fertMap.Index(30, 30) }, 200);
        FarmPlot fBest = SiteSelection.FindBestPlot(fertMap, distF, 64,
            new SiteSelectionOptions { MinSide = 6, MaxSide = 10, ComfortableDistance = 40, DistancePenaltyPerCell = 0.1 });
        True("肥力图选出地", fBest != null);
        if (fBest != null)
        {
            True("选了肥力更高的一侧（平均肥力 > 1.0）",
                fBest.AvgFertility > 1.0, "实际平均肥力 " + fBest.AvgFertility.ToString("F2"));
        }

        // ★ 河流障碍：BFS 距离必须反映"要绕路"，欧氏距离会骗人
        PassabilityMap riverMap = MakeMap(60, 60);
        for (int z = 0; z < 60; z++)
            for (int x = 0; x < 60; x++)
            {
                int i = riverMap.Index(x, z);
                riverMap.Arable[i] = true;
                riverMap.Fertility[i] = 1.0;
                // x=30 一整条河（不可通行），只在 z=59 留一个渡口
                bool river = (x == 30) && (z != 59);
                riverMap.MoveCost[i] = river ? 0.0 : 1.0;
            }
        double[] distR = SiteSelection.ComputeDistanceField(riverMap, new List<int> { riverMap.Index(25, 5) }, 500);
        double acrossStraight = distR[riverMap.Index(35, 5)];   // 直线 10 格，但要绕到 z=59
        double sameSide = distR[riverMap.Index(20, 5)];
        True(string.Format("河对岸距离 ≫ 直线距离（对岸 {0:F0} vs 同侧 {1:F0}）", acrossStraight, sameSide),
            acrossStraight > 90);
        True("同侧才是近的", sameSide < 20);

        // 基地中心兜底链
        var cands = new List<CenterCandidate>
        {
            new CenterCandidate { Kind = BaseCenterKind.HomeArea, CenterIndex = map.Index(20, 20) },
        };
        ResolvedBaseCenter rc1 = SiteSelection.ResolveBaseCenter(map, cands);
        Eq("① 有居住区 → 用居住区", rc1.Kind, BaseCenterKind.HomeArea);
        Eq("  坐标正确", rc1.X, 20);

        var candsStock = new List<CenterCandidate>
        {
            new CenterCandidate { Kind = BaseCenterKind.Stockpile, CenterIndex = map.Index(50, 50) },
        };
        ResolvedBaseCenter rc2 = SiteSelection.ResolveBaseCenter(map, candsStock);
        Eq("② 无居住区 → 退到贮藏区", rc2.Kind, BaseCenterKind.Stockpile);
        Eq("  坐标正确", rc2.X, 50);

        ResolvedBaseCenter rc3 = SiteSelection.ResolveBaseCenter(map, new List<CenterCandidate>());
        Eq("③ 都没有 → 地图中心", rc3.Kind, BaseCenterKind.MapCenterBias);
        Eq("  地图中心 x", rc3.X, W / 2);

        // 多个贮藏区 → 取离地图中心最近的
        var twoStocks = new List<CenterCandidate>
        {
            new CenterCandidate { Kind = BaseCenterKind.Stockpile, CenterIndex = map.Index(2, 2) },
            new CenterCandidate { Kind = BaseCenterKind.Stockpile, CenterIndex = map.Index(58, 58) },
        };
        ResolvedBaseCenter rc4 = SiteSelection.ResolveBaseCenter(map, twoStocks);
        Eq("多贮藏区 → 取离地图中心最近的", rc4.X, 58);

        // 复用已有种植区
        var patch = new ZonePatch
        {
            ZoneId = "zone:1",
            FreeCells = new List<int> { map.Index(21, 21), map.Index(22, 21), map.Index(23, 21) },
        };
        var emptyPatch = new ZonePatch { ZoneId = "zone:2", FreeCells = new List<int>() };
        ZonePatch zp = SiteSelection.FindBestExistingZone(map, dist, new List<ZonePatch> { emptyPatch, patch }, opts);
        True("复用有空的种植区", zp != null && zp.ZoneId == "zone:1");
        Eq("  空种植区被跳过（不选 zone:2）", zp == null ? "?" : zp.ZoneId, "zone:1");

        ZonePatch zpNone = SiteSelection.FindBestExistingZone(map, dist,
            new List<ZonePatch> { emptyPatch }, opts);
        Eq("全是空的种植区 → 返回 null（转去新建）", zpNone, null);

        // 质心
        int cx, cz;
        bool okc = SiteSelection.CentroidOf(W, new List<int> { map.Index(10, 10), map.Index(20, 20) }, out cx, out cz);
        True("质心算得出", okc);
        Eq("  质心 x", cx, 15);
        Eq("  质心 z", cz, 15);
    }

    // ---------------- 储藏区选址 ----------------

    /// <summary>造一个"从基地中心出发的欧氏距离场"当测试输入（真实场景里是带地形代价的 BFS）。</summary>
    private static double[] MakeDistField(int w, int h, int cx, int cz)
    {
        var d = new double[w * h];
        for (int z = 0; z < h; z++)
            for (int x = 0; x < w; x++)
                d[z * w + x] = Math.Sqrt((double)(x - cx) * (x - cx) + (double)(z - cz) * (z - cz));
        return d;
    }

    private static void RunStockpileTests()
    {
        Console.WriteLine();
        Console.WriteLine("-- StockpilePlanner（储藏区选址） --");

        const int W = 120, H = 120;
        double[] dist = MakeDistField(W, H, 60, 60);
        var opts = new StockpilePlannerOptions
        {
            ClusterCount = 2,
            MaxDistanceFromBase = 120,
            MinCellsPerCluster = 9,
            MaxCellsPerCluster = 400,
        };

        // ── 场景：两堆资源，一堆在左上、一堆在右下，彼此很远 ──
        var cands = new List<StockpileCandidate>();
        // 左上资源堆（20,20）附近 5×5
        for (int x = 18; x < 23; x++)
            for (int z = 18; z < 23; z++)
                cands.Add(new StockpileCandidate { CellIndex = z * W + x, ResourceWeight = 10 });
        // 右下资源堆（100,100）附近 5×5
        for (int x = 98; x < 103; x++)
            for (int z = 98; z < 103; z++)
                cands.Add(new StockpileCandidate { CellIndex = z * W + x, ResourceWeight = 10 });

        List<StockpileCluster> clusters = StockpilePlanner.Plan(dist, cands, W, opts);
        Eq("分出 2 个簇", clusters.Count, 2);

        if (clusters.Count == 2)
        {
            // ★ 核心断言：聚类必须按**地理聚拢**，而不是按"到基地的距离"分环。
            //   如果按距离分环，两堆资源会被切成"近环"和"远环"，
            //   每簇内部会同时含有左上和右下的点 —— 那就不对了。
            //
            // ⚠ 注意不要假设簇的顺序：`Plan` 返回值是按 Score 排序的，
            //   而两堆资源关于基地对称（(20,20) 与 (100,100) 到 (60,60) 距离相同），
            //   所以谁在前是任意的。**断言"分处对角象限"而不是"哪个是哪个"**。
            StockpileCluster a = clusters[0], b = clusters[1];
            int ax = (a.MinX + a.MaxX) / 2, az = (a.MinZ + a.MaxZ) / 2;
            int bx = (b.MinX + b.MaxX) / 2, bz = (b.MinZ + b.MaxZ) / 2;
            bool diagonal =
                (ax < 60 && az < 60 && bx > 60 && bz > 60) ||
                (bx < 60 && bz < 60 && ax > 60 && az > 60);
            True("★ 两个簇分处对角象限（按地理聚拢，不是按基地距离分环）", diagonal,
                "簇A 中心=(" + ax + "," + az + ")  簇B 中心=(" + bx + "," + bz + ")");

            // 每个簇应该只含一堆资源（25 格）
            True("★ 每簇恰好覆盖一堆资源（25 格）",
                a.Area == 25 && b.Area == 25,
                "A=" + a.Area + " B=" + b.Area);

            // 紧凑：5×5 的方块，包围盒 5×5 = 25，填充率应为 1
            Near("方块填充率 = 1.0（规整矩形）", a.FillRatio, 1.0, 1e-9);
            True("包围盒是 5×5", a.BBoxWidth == 5 && a.BBoxHeight == 5,
                a.BBoxWidth + "x" + a.BBoxHeight);
        }

        // ── ★ 可复现性：同样的输入必须给出完全相同的结果 ──
        //    这是能写断言的前提。K-means 用了固定种子的自实现 LCG，
        //    如果哪天有人换成 System.Random，这条会挂。
        List<StockpileCluster> again = StockpilePlanner.Plan(dist, cands, W, opts);
        bool same = again.Count == clusters.Count;
        if (same)
        {
            for (int i = 0; i < clusters.Count; i++)
            {
                if (again[i].Cells.Count != clusters[i].Cells.Count
                    || again[i].MinX != clusters[i].MinX
                    || again[i].MinZ != clusters[i].MinZ)
                {
                    same = false;
                    break;
                }
            }
        }
        True("★ 可复现：同样输入两次调用结果完全一致", same);

        // ── 距离权重：两个簇规模相同时，离基地近的应该评分更高 ──
        //
        // ⚠ 这里必须**故意造出距离不同**的两个簇。
        //   最初我用了 (20,20) 与 (100,100) —— 它们关于基地 (60,60) **对称**，
        //   到基地距离完全相同，于是评分相同，断言必然失败（那是对的行为，测试写错了）。
        var asymCands = new List<StockpileCandidate>();
        for (int x = 55; x < 60; x++)          // 近：基地旁边
            for (int z = 55; z < 60; z++)
                asymCands.Add(new StockpileCandidate { CellIndex = z * W + x, ResourceWeight = 1 });
        for (int x = 110; x < 115; x++)        // 远：地图另一侧
            for (int z = 110; z < 115; z++)
                asymCands.Add(new StockpileCandidate { CellIndex = z * W + x, ResourceWeight = 1 });

        List<StockpileCluster> asym = StockpilePlanner.Plan(dist, asymCands, W, opts);
        Eq("不对称场景也分出 2 簇", asym.Count, 2);
        if (asym.Count == 2)
        {
            StockpileCluster c0 = asym[0], c1 = asym[1];
            True("★ 离基地近的簇平均距离确实更小",
                Math.Min(c0.AvgDistanceToBase, c1.AvgDistanceToBase) < 10,
                "距 " + c0.AvgDistanceToBase.ToString("F1") + " / " + c1.AvgDistanceToBase.ToString("F1"));
            True("★ 同规模下离基地近的簇评分更高",
                (c0.AvgDistanceToBase < c1.AvgDistanceToBase) == (c0.Score > c1.Score),
                "近者分 " + (c0.AvgDistanceToBase < c1.AvgDistanceToBase ? c0.Score : c1.Score).ToString("F1")
                + " vs 远者分 " + (c0.AvgDistanceToBase < c1.AvgDistanceToBase ? c1.Score : c0.Score).ToString("F1"));
        }

        // ── 边界：空输入 ──
        Eq("空候选 → 返回空列表",
            StockpilePlanner.Plan(dist, new List<StockpileCandidate>(), W, opts).Count, 0);
        Eq("候选为 null → 不炸",
            StockpilePlanner.Plan(dist, null, W, opts).Count, 0);
        Eq("距离场为 null → 不炸",
            StockpilePlanner.Plan(null, cands, W, opts).Count, 0);

        // ── 边界：全部超出最大距离 → 一个都不选 ──
        var farOpts = new StockpilePlannerOptions { ClusterCount = 2, MaxDistanceFromBase = 5 };
        Eq("全部超出最大距离 → 空结果",
            StockpilePlanner.Plan(dist, cands, W, farOpts).Count, 0);

        // ── 边界：太小的簇判负 ──
        var tiny = new List<StockpileCandidate>
        {
            new StockpileCandidate { CellIndex = 10 * W + 10, ResourceWeight = 1 },
            new StockpileCandidate { CellIndex = 10 * W + 11, ResourceWeight = 1 },
        };
        var bigMin = new StockpilePlannerOptions { ClusterCount = 1, MinCellsPerCluster = 9 };
        List<StockpileCluster> tinyRes = StockpilePlanner.Plan(dist, tiny, W, bigMin);
        True("只有 2 格时被判负（低于 MinCellsPerCluster=9）",
            tinyRes.Count == 0 || tinyRes[0].Score == double.NegativeInfinity,
            "簇数 " + tinyRes.Count + (tinyRes.Count > 0 ? " 分=" + tinyRes[0].Score : ""));
    }

    // ---------------- 工具 ----------------

    private static PassabilityMap MakeMap(int w, int h)
    {
        int n = w * h;
        var m = new PassabilityMap
        {
            Width = w, Height = h,
            MoveCost = new double[n],
            Arable = new bool[n],
            Fertility = new double[n],
        };
        for (int i = 0; i < n; i++) m.MoveCost[i] = 1.0;   // 默认全可走
        return m;
    }

    private static FoodState Clone(FoodState s)
    {
        return new FoodState
        {
            Stock = s.Stock,
            BurnRatePerDay = s.BurnRatePerDay,
            Planted = s.Planted == null ? new List<PlantGroup>() : new List<PlantGroup>(s.Planted),
            ArableCellsAvailable = s.ArableCellsAvailable,
            Season = s.Season,
            Tick = s.Tick,
            Calibration = s.Calibration,
        };
    }
}
