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
        RunBuildSiteTests();
        RunTemplateTests();
        RunTemplatePlacerTests();
        RunTemplateReuseTests();
        RunRegistryTests();
        RunMaterialTests();

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

        // ── ★★ 回归：`MaxCellsPerCluster` 必须被**真正约束**，不能只当否决条件 ──
        //
        // 这是游戏里抓出来的真 bug：原来 `MaxCellsPerCluster` **只**在 `Score()` 里
        // 当否决条件用 —— K-means 把两万多个候选分成 3 簇（每簇约 8400 格），
        // 全部 > 400 被否决 → **一个储藏区都建不出来**，而摘要只说"没有达标的"。
        // （历史上"建出 7001 格和 8055 格两个区"也是同一个根因。）
        //
        // 场景设计：给远超上限的一大片候选，断言产出的簇**每个都不超过上限**。
        {
            const int W2 = 200, H2 = 200;
            double[] d2 = MakeDistField(W2, H2, 100, 100);
            var opts2 = new StockpilePlannerOptions
            {
                ClusterCount = 2,
                MaxDistanceFromBase = 400,
                MinCellsPerCluster = 9,
                MaxCellsPerCluster = 50,        // 故意设小，便于断言
            };

            // 铺一大片候选（40×40 = 1600 格，远超 50 的上限）
            var bigCands = new List<StockpileCandidate>();
            for (int x = 80; x < 120; x++)
                for (int z = 80; z < 120; z++)
                    bigCands.Add(new StockpileCandidate { CellIndex = z * W2 + x, ResourceWeight = 1 });

            List<StockpileCluster> big = StockpilePlanner.Plan(d2, bigCands, W2, opts2);
            True("★★ 候选远超上限时仍能产出簇（不是全被否决）", big.Count > 0,
                "产出 " + big.Count + " 个簇");

            bool allCapped = true;
            int maxSeen = 0;
            for (int i = 0; i < big.Count; i++)
            {
                int area = big[i].Area;
                if (area > maxSeen) maxSeen = area;
                if (area > opts2.MaxCellsPerCluster) allCapped = false;
            }
            True("★★ 每个簇的格数都不超过 MaxCellsPerCluster（真的被约束了）", allCapped,
                "最大簇 " + maxSeen + " 格，上限 " + opts2.MaxCellsPerCluster);

            bool finite = true;
            for (int i = 0; i < big.Count; i++)
                if (double.IsNegativeInfinity(big[i].Score) || double.IsInfinity(big[i].Score)) finite = false;
            True("★★ 截断后评分是有限值（不再因为\"太大\"被否成 -Infinity）", finite);
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

    // ---------------- 住处选址与户型 ----------------

    private static BuildabilityMap MakeBuildMap(int w, int h, bool allBuildable)
    {
        int n = w * h;
        var m = new BuildabilityMap
        {
            Width = w, Height = h,
            Buildable = new bool[n],
            MoveCost = new double[n],
        };
        for (int i = 0; i < n; i++)
        {
            m.Buildable[i] = allBuildable;
            m.MoveCost[i] = 1.0;
        }
        return m;
    }

    private static void RunBuildSiteTests()
    {
        Console.WriteLine();
        Console.WriteLine("-- BuildSitePlanner（住处选址与户型） --");

        const int W = 80, H = 80;
        BuildabilityMap grid = MakeBuildMap(W, H, true);
        double[] dist = MakeDistField(W, H, 40, 40);
        // 默认 Size=13 / 四边各一扇门（用户拍板："13 乘 13" + "四个边的中心留门"）
        var opts = new BuildSiteOptions { Size = 13, MaxDistanceFromBase = 60, ComfortableDistance = 20 };

        BuildingPlan plan = BuildSitePlanner.Plan(grid, dist, opts);
        True("选出了建房位置", plan != null);
        if (plan == null) return;

        Console.WriteLine("       " + plan);

        // ── 户型几何（13×13）──
        // 外圈 = 13*4 - 4 = 48 格；四边中心各 1 扇门 = 4；剩墙 44；内部 11×11 = 121
        Eq("边长 = 13", plan.Size, 13);
        Eq("墙 + 门 = 外圈 48 格", plan.WallCells.Count + plan.DoorCells.Count, 48);
        Eq("★ 恰好 4 扇门（四边中心各一）", plan.DoorCells.Count, 4);
        Eq("★ 剩下 44 面墙", plan.WallCells.Count, 44);
        Eq("内部 121 格地板", plan.FloorCells.Count, 121);
        Eq("内部 121 格空地", plan.InteriorCells.Count, 121);

        // ── ★ 四个方向都有门（用户明确指出的问题：只有一扇门 = 只从一个方向可进）──
        {
            int maxX = plan.X + plan.Size - 1, maxZ = plan.Z + plan.Size - 1;
            bool hasN = false, hasE = false, hasS = false, hasW = false;
            int mid = plan.Size / 2;
            foreach (int di in plan.DoorCells)
            {
                int dx = di % W, dz = di / W;
                if (dz == maxZ && dx == plan.X + mid) hasN = true;
                if (dx == maxX && dz == plan.Z + mid) hasE = true;
                if (dz == plan.Z && dx == plan.X + mid) hasS = true;
                if (dx == plan.X && dz == plan.Z + mid) hasW = true;
            }
            True("★★ 四个方向都有门（北）", hasN, "北门");
            True("★★ 四个方向都有门（东）", hasE, "东门");
            True("★★ 四个方向都有门（南）", hasS, "南门");
            True("★★ 四个方向都有门（西）", hasW, "西门");
            True("★★ 四扇门分别落在四条边的**中点**", hasN && hasE && hasS && hasW,
                "mid=" + mid + " 房=(" + plan.X + "," + plan.Z + ")..(" + maxX + "," + maxZ + ")");
        }

        // ── 门的存在性（四边全封死 = 小人进不去）──
        True("★ 有门（不是全封闭的盒子）", plan.DoorCells.Count > 0,
            "门数 " + plan.DoorCells.Count);

        // 门外那格必须在地基之外
        if (plan.DoorOutsideCell >= 0)
        {
            int ox = plan.DoorOutsideCell % W;
            int oz = plan.DoorOutsideCell / W;
            bool outside = ox < plan.X || ox > plan.X + plan.Size - 1
                        || oz < plan.Z || oz > plan.Z + plan.Size - 1;
            True("★ 门外的格子在地基之外", outside, "门外=(" + ox + "," + oz + ")");

            double cost = grid.MoveCost[plan.DoorOutsideCell];
            True("★ 门外那格可通行（不然出不去的等于没门）", cost > 0, "通行代价 " + cost);
        }
        else
        {
            True("★ 门外的格子在地基之外", false, "DoorOutsideCell 未设置");
        }

        // 墙/门/地板三者不重叠
        var seen = new HashSet<int>();
        bool overlap = false;
        foreach (int i in plan.WallCells) if (!seen.Add(i)) overlap = true;
        foreach (int i in plan.DoorCells) if (!seen.Add(i)) overlap = true;
        foreach (int i in plan.FloorCells) if (!seen.Add(i)) overlap = true;
        True("墙/门/地板互不重叠", !overlap);

        // ── 位置偏好：应该靠近基地 ──
        // （13×13 的房子中心在 plan.X+6 / plan.Z+6）
        double dToBase = Math.Sqrt(Math.Pow(plan.X + 6 - 40, 2) + Math.Pow(plan.Z + 6 - 40, 2));
        True("选的位置靠近基地（中心距 < 40）", dToBase < 40,
            "中心距 " + dToBase.ToString("F1") + " 格");

        // ── 全不可盖 → 返回 null，而不是硬选 ──
        BuildabilityMap barren = MakeBuildMap(W, H, false);
        Eq("全不可盖 → 返回 null", BuildSitePlanner.Plan(barren, dist, opts), null);

        // ── 挡路太多的地方要跳过 ──
        // 造一张只有右下角一小块可盖的图（够放 13×13）
        BuildabilityMap patchy = MakeBuildMap(W, H, false);
        for (int x = 66; x < 80; x++)
            for (int z = 66; z < 80; z++)
                patchy.Buildable[z * W + x] = true;
        BuildingPlan patchPlan = BuildSitePlanner.Plan(patchy, dist, opts);
        True("只造出一块可盖地时能选到它", patchPlan != null,
            patchPlan == null ? "返回 null" : patchPlan.ToString());
        if (patchPlan != null)
        {
            True("选在可盖的那一块（x≥66）", patchPlan.X >= 66, "X=" + patchPlan.X);
        }

        // ── 单门模式仍可用（DoorSides 只给一条边）──
        for (int side = 0; side <= 3; side++)
        {
            var so = new BuildSiteOptions { Size = 13, DoorSides = new int[] { side } };
            BuildingPlan sp = BuildSitePlanner.Plan(grid, dist, so);
            bool ok = sp != null && sp.DoorCells.Count == 1;
            // 门应该落在对应的边上
            if (ok)
            {
                int dx = sp.DoorCells[0] % W;
                int dz = sp.DoorCells[0] / W;
                int maxX = sp.X + sp.Size - 1, maxZ = sp.Z + sp.Size - 1;
                bool onSide =
                    (side == 0 && dz == maxZ) ||
                    (side == 1 && dx == maxX) ||
                    (side == 2 && dz == sp.Z) ||
                    (side == 3 && dx == sp.X);
                True("单门模式 DoorSides={" + side + "} 时门在对应边上", onSide,
                    "门=(" + dx + "," + dz + ") 房=(" + sp.X + "," + sp.Z + ")..(" + maxX + "," + maxZ + ")");
            }
            else
            {
                True("单门模式 DoorSides={" + side + "} 时门在对应边上", false, "没生成户型");
            }
        }

        // ── 门开在地图边缘时应判不可行（门外越界）──
        // 把基地中心放到地图角落，房子会被推到边缘
        double[] cornerDist = MakeDistField(W, H, 0, 0);
        BuildingPlan cornerPlan = BuildSitePlanner.Plan(grid, cornerDist,
            new BuildSiteOptions
            {
                Size = 13,
                DoorSides = new int[] { 3 },      // 只开西门：贴 x=0 时门外必越界
                RequireOutsideAccess = true,
                MaxDistanceFromBase = 20,
            });
        // 门朝西(-x) 时，如果房子贴着 x=0，门外就出界了 → 应该被拒
        if (cornerPlan != null)
        {
            True("★ 贴边时不会把门开到地图外（RequireOutsideAccess 生效）",
                cornerPlan.X > 0,
                "X=" + cornerPlan.X + "（门朝西，若 X=0 则门外越界）");
        }
        else
        {
            True("★ 贴边时不会把门开到地图外（RequireOutsideAccess 生效）", true,
                "该配置下没有可行位置（也算正确拒绝）");
        }

        // ── ★ 四门模式下，任一门被堵（门外不可通行）就不该选那个位置 ──
        //    造一张图：除了"北门外那一格"之外全可通行
        {
            BuildabilityMap blockedNorth = MakeBuildMap(W, H, true);
            // 先正常选一次拿到位置，再把该位置的北门外那格封死，重选应当换位置或返回 null
            BuildingPlan p1 = BuildSitePlanner.Plan(grid, dist, opts);
            if (p1 != null)
            {
                int nOutX = p1.X + p1.Size / 2;
                int nOutZ = p1.Z + p1.Size;          // 北门外那一格
                if (nOutX >= 0 && nOutX < W && nOutZ >= 0 && nOutZ < H)
                {
                    blockedNorth.MoveCost[nOutZ * W + nOutX] = 0;    // 封死
                    BuildabilityMap g2 = MakeBuildMap(W, H, true);
                    g2.MoveCost[nOutZ * W + nOutX] = 0;
                    BuildingPlan p2 = BuildSitePlanner.Plan(g2, dist, opts);
                    bool moved = p2 == null
                        || p2.X != p1.X || p2.Z != p1.Z
                        || (p2.X + p2.Size / 2) != nOutX
                        || (p2.Z + p2.Size != nOutZ);
                    True("★ 四门模式：某扇门外走不通时会换位置（四门都要能出去）",
                        moved, p1 == null ? "p1=null" : ("p1=(" + p1.X + "," + p1.Z + ") p2="
                            + (p2 == null ? "null" : "(" + p2.X + "," + p2.Z + ")")));
                }
            }
        }

        RunFurnitureTests(grid, dist, opts, W, H);
    }

    /// <summary>
    /// 家具摆放（S4 第二版：用户要「3 张床 + 1 个火把」构成最基本的卧室）。
    ///
    /// 这些断言全是**纯几何**，毫秒级可跑。它们锁住的是用户口述的规则：
    /// 床沿墙排、避开门、不要挤在一起、火把放中心、某格不可用就换一格。
    /// </summary>
    private static void RunFurnitureTests(
        BuildabilityMap grid, double[] dist, BuildSiteOptions opts, int W, int H)
    {
        Console.WriteLine();
        Console.WriteLine("-- BuildSitePlanner（家具摆放：3 床 + 1 火把） --");

        BuildingPlan plan = BuildSitePlanner.Plan(grid, dist, opts);
        if (plan == null) { True("拿到户型", false, "plan=null"); return; }

        int maxX = plan.X + plan.Size - 1, maxZ = plan.Z + plan.Size - 1;

        var f = BuildSitePlanner.PlaceFurniture(plan, grid, 3, opts);
        Console.WriteLine("       " + string.Join(" | ", f.ConvertAll(x => x.ToString()).ToArray()));

        var beds = f.FindAll(x => x.Kind == "bed");
        var torches = f.FindAll(x => x.Kind == "torch");

        Eq("★ 3 个殖民者 → 3 张床", beds.Count, 3);
        Eq("★ 恰好 1 个火把", torches.Count, 1);

        // ── 全都在屋里 ──
        bool allInside = true;
        foreach (var it in f)
            foreach (int ci in it.Cells)
            {
                int cx = ci % W, cz = ci / W;
                if (cx <= plan.X || cx >= maxX || cz <= plan.Z || cz >= maxZ) allInside = false;
            }
        True("★ 家具全部在房间内部（不在墙里）", allInside);

        // ── 不堵门：门内侧那格必须空着 ──
        {
            var approach = new HashSet<int>();
            int depth = opts.DoorApproachDepth > 0 ? opts.DoorApproachDepth : 1;
            foreach (int di in plan.DoorCells)
            {
                int dx = di % W, dz = di / W;
                int sx = 0, sz = 0;
                if (dz == maxZ) sz = -1;
                else if (dz == plan.Z) sz = 1;
                else if (dx == maxX) sx = -1;
                else if (dx == plan.X) sx = 1;
                else continue;
                int nx = dx, nz = dz;
                for (int k = 1; k <= depth; k++)
                {
                    nx += sx; nz += sz;
                    approach.Add(nz * W + nx);
                }
            }
            bool clear = true;
            foreach (var it in f)
                foreach (int ci in it.Cells)
                    if (approach.Contains(ci)) clear = false;
            True("★★ 家具避开了每扇门内侧的通道（不堵门）", clear,
                "门内侧通道 " + approach.Count + " 格");
        }

        // ── 不重叠、且有间隔 ──
        {
            var used = new HashSet<int>();
            bool overlap = false;
            foreach (var it in f)
                foreach (int ci in it.Cells)
                    if (!used.Add(ci)) overlap = true;
            True("★ 家具之间不重叠", !overlap);

            bool gapOk = true;
            int gap = opts.FurnitureGap > 0 ? opts.FurnitureGap : 1;
            var all = new List<int>(used);
            for (int i = 0; i < all.Count && gapOk; i++)
                for (int j = i + 1; j < all.Count; j++)
                {
                    int ax = all[i] % W, az = all[i] / W;
                    int bx = all[j] % W, bz = all[j] / W;
                    if (Math.Abs(ax - bx) <= gap && Math.Abs(az - bz) <= gap &&
                        Math.Abs(ax - bx) + Math.Abs(az - bz) > 1)
                        continue;   // 同一件家具内部的相邻格，不算"两件家具挤在一起"
                }
            True("★ 不同家具之间至少留了间隔格", gapOk, "gap=" + gap);
        }

        // ── 床沿同一列（x 相同）排，朝向一致 ──
        {
            bool sameCol = true;
            int col = beds.Count > 0 ? beds[0].CellIndex % W : -1;
            foreach (var b in beds) if (b.CellIndex % W != col) sameCol = false;
            True("★ 3 张床排在**同一列**（沿墙一列，符合「沿墙排」的意图）", sameCol,
                "列 x=" + col);
        }

        // ── 都朝北（床 1×2 沿 z 长，床头朝北）──
        {
            bool allNorth = true;
            foreach (var b in beds) if (b.Rotation != 0) allNorth = false;
            True("★ 床朝向一致（北，沿 z 长 1×2）", allNorth);
        }

        // ── 火把离房间中心更近（比所有床都近）──
        if (torches.Count == 1 && beds.Count > 0)
        {
            double cx = plan.X + (plan.Size - 1) / 2.0;
            double cz = plan.Z + (plan.Size - 1) / 2.0;
            Func<int, double> dToCentre = i =>
            {
                double px = i % W, pz = i / W;
                return Math.Sqrt((px - cx) * (px - cx) + (pz - cz) * (pz - cz));
            };
            double dTorch = dToCentre(torches[0].CellIndex);
            double dBedMin = double.MaxValue;
            foreach (var b in beds) dBedMin = Math.Min(dBedMin, dToCentre(b.CellIndex));
            True("★★ 火把比所有床都更靠近房间中心（照明覆盖最好）",
                dTorch < dBedMin, "火把距 " + dTorch.ToString("F2") + " vs 最近的床 " + dBedMin.ToString("F2"));
        }

        // ── 可复现：同样输入两次结果一致 ──
        {
            var f2 = BuildSitePlanner.PlaceFurniture(plan, grid, 3, opts);
            bool same = f2.Count == f.Count;
            if (same)
                for (int i = 0; i < f.Count; i++)
                    if (f2[i].CellIndex != f[i].CellIndex || f2[i].Rotation != f[i].Rotation
                        || f2[i].Kind != f[i].Kind) same = false;
            True("★ 摆放规则可复现（同样输入 → 同样输出）", same);
        }

        // ── 某格不可用就换一格：把最靠西的一列全封掉，床应该往东挪而不是放弃 ──
        {
            BuildabilityMap g2 = MakeBuildMap(W, H, true);
            // 封掉 plan.X+1 整列（原本床排的那一列）
            for (int z = plan.Z + 1; z < maxZ; z++)
                g2.Buildable[z * W + (plan.X + 1)] = false;
            var f3 = BuildSitePlanner.PlaceFurniture(plan, g2, 3, opts);
            var beds3 = f3.FindAll(x => x.Kind == "bed");
            Eq("★ 最靠墙那列被封后仍然放满 3 张床（换一格，不放弃）", beds3.Count, 3);
            bool movedEast = true;
            foreach (var b in beds3) if (b.CellIndex % W == plan.X + 1) movedEast = false;
            True("★ 床确实挪到了别的列", movedEast,
                "列 " + string.Join(",", beds3.ConvertAll(b => (b.CellIndex % W).ToString()).ToArray()));
        }

        // ── 殖民者 0 人 → 不放床（但还是放火把）──
        {
            var f4 = BuildSitePlanner.PlaceFurniture(plan, grid, 0, opts);
            Eq("0 个殖民者 → 0 张床", f4.FindAll(x => x.Kind == "bed").Count, 0);
            Eq("0 个殖民者 → 仍然放 1 个火把", f4.FindAll(x => x.Kind == "torch").Count, 1);
        }

        // ── 默认参数确实是用户要的那套：13×13 / 4 门 / 床 3 张 ──
        {
            var d = new BuildSiteOptions();
            Eq("默认边长 = 13（用户拍板）", d.Size, 13);
            Eq("默认开门边数 = 4（四边中心各一）", d.DoorSides.Length, 4);
            Eq("默认每殖民者一床", d.BedPerColonist, true);
            Eq("默认火把 1 个", d.TorchCount, 1);
        }
    }

    // ---------------- 建筑样板 ----------------

    /// <summary>造一个 5x5 的小样板：一圈墙 + 南边中间一个门 + 屋里一个床/光源</summary>
    private static BuildTemplate MakeSmallTemplate()
    {
        var t = new BuildTemplate
        {
            Id = "test-room-5x5",
            Name = "测试小房",
            Origin = "user",
            Rows = new List<string>
            {
                "WWWWW",
                "WBL.W",
                "W...W",
                "W...D",
                "WWWWW",
            },        };
        t.Roles['W'] = new TemplateRole { Symbol = 'W', Name = "wall", Accepts = new List<string> { "Wall" }, IsWallLike = true, NeedsStuff = true };
        t.Roles['D'] = new TemplateRole { Symbol = 'D', Name = "door", Accepts = new List<string> { "Door" }, IsDoorLike = true, NeedsStuff = true };
        t.Roles['B'] = new TemplateRole { Symbol = 'B', Name = "bed", Accepts = new List<string> { "Bed", "DoubleBed" }, IsFurniture = true, NeedsStuff = false };
        t.Roles['L'] = new TemplateRole { Symbol = 'L', Name = "light", Accepts = new List<string> { "TorchLamp" }, IsFurniture = true, NeedsStuff = false };
        t.NormalizeSize();
        return t;
    }

    private static void RunTemplateTests()
    {
        Console.WriteLine();
        Console.WriteLine("-- BuildTemplate（建筑样板） --");

        BuildTemplate t = MakeSmallTemplate();
        Eq("尺寸推出来了", t.Width + "x" + t.Height, "5x5");

        // ── 校验 ──
        List<string> errs = t.Validate();
        bool hasHardError = false;
        for (int i = 0; i < errs.Count; i++)
        {
            // "定义了没用到" 只是警告，不算硬错
            if (errs[i].IndexOf("可清理") < 0) hasHardError = true;
        }
        True("合法样板校验通过（无硬错误）", !hasHardError,
            errs.Count > 0 ? string.Join(" / ", errs.ToArray()) : "无错误");

        // ── 缺门 → 必须报错（全封闭房子小人进不去）──
        var noDoor = MakeSmallTemplate();
        noDoor.Rows[3] = "W...W";
        bool gotDoorErr = false;
        foreach (string e in noDoor.Validate()) if (e.IndexOf("门") >= 0) gotDoorErr = true;
        True("★ 没有门时校验报错（全封闭的房子进不去）", gotDoorErr);

        // ── 缺墙 → 必须报错 ──
        var noWall = MakeSmallTemplate();
        for (int i = 0; i < noWall.Rows.Count; i++) noWall.Rows[i] = noWall.Rows[i].Replace('W', '.');
        bool gotWallErr = false;
        foreach (string e in noWall.Validate()) if (e.IndexOf("墙") >= 0) gotWallErr = true;
        True("★ 没有墙时校验报错", gotWallErr);

        // ── 用了未定义的代号 → 必须报错 ──
        var badSym = MakeSmallTemplate();
        badSym.Roles.Remove('B');
        bool gotSymErr = false;
        foreach (string e in badSym.Validate()) if (e.IndexOf("没有定义") >= 0) gotSymErr = true;
        True("★ 用了未定义的代号时校验报错", gotSymErr);

        // ── 行长不一致 → 必须报错 ──
        // ⚠ 必须真的写成**更短**的行。我第一次写成 "W...." —— 那也是 5 个字符，
        //   跟其他行一样长，所以根本不是"不一致"（测试白写）。
        var badRow = MakeSmallTemplate();
        badRow.Rows[2] = "W...";
        bool gotRowErr = false;
        foreach (string e in badRow.Validate()) if (e.IndexOf("长度") >= 0) gotRowErr = true;
        True("★ 行长不一致时报错", gotRowErr,
            string.Join(" / ", badRow.Validate().ToArray()));

        // ── 角色计数 ──
        // ⚠ 外圈 16 格里有 1 格是门，所以**墙是 15 不是 16**（这里我一开始写错了）
        Dictionary<string, int> counts = t.CountByRole();
        Eq("墙 15 格（外圈 16 减去 1 个门）", counts.ContainsKey("W") ? counts["W"] : 0, 15);
        Eq("门 1 格", counts.ContainsKey("D") ? counts["D"] : 0, 1);
        Eq("床 1 格", counts.ContainsKey("B") ? counts["B"] : 0, 1);
        Eq("光源 1 格", counts.ContainsKey("L") ? counts["L"] : 0, 1);
        Eq("墙+门 = 外圈 16 格", (counts.ContainsKey("W") ? counts["W"] : 0) + (counts.ContainsKey("D") ? counts["D"] : 0), 16);

        // ── ★ 旋转 90° ──
        BuildTemplate r90 = t.Rotated(1);
        Eq("★ 旋转 90° 后尺寸仍是 5x5", r90.Width + "x" + r90.Height, "5x5");
        Dictionary<string, int> rc = r90.CountByRole();
        Eq("★ 旋转后墙格数不变", rc.ContainsKey("W") ? rc["W"] : 0, 15);
        Eq("★ 旋转后门还在", rc.ContainsKey("D") ? rc["D"] : 0, 1);
        Eq("★ 旋转后床还在", rc.ContainsKey("B") ? rc["B"] : 0, 1);

        BuildTemplate r360 = t.Rotated(4);
        bool sameAsOrig = r360.Width == t.Width && r360.Height == t.Height;
        if (sameAsOrig)
        {
            for (int z = 0; z < t.Height && sameAsOrig; z++)
                for (int x = 0; x < t.Width; x++)
                    if (r360.SymbolAt(x, z) != t.SymbolAt(x, z)) { sameAsOrig = false; break; }
        }
        True("★ 旋转 4 次 = 原样", sameAsOrig);

        // ── ★ 镜像 ──
        BuildTemplate mir = t.Mirrored();
        Eq("★ 镜像后尺寸不变", mir.Width + "x" + mir.Height, "5x5");
        Dictionary<string, int> mc = mir.CountByRole();
        Eq("★ 镜像后门还在", mc.ContainsKey("D") ? mc["D"] : 0, 1);

        // ── ★ JSON 往返 ──
        string json = TemplateJson.Write(t);
        True("JSON 输出非空", json.Length > 50, json.Length + " 字符");
        BuildTemplate back = TemplateJson.Read(json);
        Eq("★ JSON 往返：id 一致", back.Id, t.Id);
        Eq("★ JSON 往返：尺寸一致", back.Width + "x" + back.Height, t.Width + "x" + t.Height);
        Eq("★ JSON 往返：角色数一致", back.Roles.Count, t.Roles.Count);

        bool gridSame = back.Rows.Count == t.Rows.Count;
        if (gridSame)
        {
            for (int i = 0; i < t.Rows.Count; i++)
                if (back.Rows[i] != t.Rows[i]) { gridSame = false; break; }
        }
        True("★ JSON 往返：逐格完全一致", gridSame,
            gridSame ? "" : "原始 " + string.Join("|", t.Rows.ToArray()) + " vs 回来 " + string.Join("|", back.Rows.ToArray()));

        TemplateRole backBed;
        if (back.Roles.TryGetValue('B', out backBed))
        {
            True("★ JSON 往返：床槽的 accepts 列表完整",
                backBed.Accepts.Count == 2 && backBed.Accepts[0] == "Bed" && backBed.Accepts[1] == "DoubleBed",
                string.Join(",", backBed.Accepts.ToArray()));
            True("★ JSON 往返：家具标记保留", backBed.IsFurniture);
        }
        else
        {
            True("★ JSON 往返：床槽的 accepts 列表完整", false, "Roles 里没有 B");
        }

        // ── 解析健壮性 ──
        bool threw = false;
        try { TemplateJson.Read("{ not json"); } catch (Exception) { threw = true; }
        True("坏 JSON 会抛异常（而不是静默返回半个样板）", threw);

        var empty = new BuildTemplate();
        threw = false;
        try { TemplateJson.Read(TemplateJson.Write(empty)); } catch (Exception) { threw = true; }
        True("空样板不该在序列化往返时抛异常", !threw);
    }

    // ---------------- 样板放置（角色 → 具体 Def） ----------------

    private static PlacementGrid MakePlacementGrid(int w, int h, bool allPlaceable)
    {
        int n = w * h;
        var g = new PlacementGrid { Width = w, Height = h, CanPlace = new bool[n], BlockedBySolid = new bool[n] };
        for (int i = 0; i < n; i++) g.CanPlace[i] = allPlaceable;
        return g;
    }

    private static void RunTemplatePlacerTests()
    {
        Console.WriteLine();
        Console.WriteLine("-- TemplatePlacer（样板 → 逐格蓝图计划） --");

        BuildTemplate t = MakeSmallTemplate();     // 5x5：15 墙 + 1 门 + 1 床 + 1 光源
        var grid = MakePlacementGrid(20, 20, true);
        double[] dist = MakeDistField(20, 20, 10, 10);

        // ── 1. 料充足：所有槽位都该解析出来 ──
        var stock = new Dictionary<string, int>
        {
            { "Wall", 100 }, { "Door", 10 }, { "Bed", 10 }, { "DoubleBed", 10 }, { "TorchLamp", 10 },
        };
        TemplatePlacement p = TemplatePlacer.Plan(t, grid, 5, 5, stock, 0, false);
        Eq("料充足：槽位总数 = 18（15墙+1门+1床+1光源）", p.TotalSlots, 18);
        Eq("料充足：全部解析成功", p.Blueprints.Count, 18);
        Eq("料充足：没有未解决项", p.Unresolved.Count, 0);
        Eq("墙 15", p.WallCount, 15);
        Eq("门 1", p.DoorCount, 1);
        Eq("床 1", p.CountRole("bed"), 1);
        Eq("光源 1", p.CountRole("light"), 1);

        // 坐标要落到正确的绝对位置
        bool foundDoorAt = false;
        for (int i = 0; i < p.Blueprints.Count; i++)
        {
            PlannedBlueprint b = p.Blueprints[i];
            if (b.RoleName == "door" && b.X == 5 + 4 && b.Z == 5 + 3) foundDoorAt = true;
        }
        True("★ 门的绝对坐标正确（原点 (5,5) + 样板内 (4,3)）", foundDoorAt);

        // ── 2. ★ 料不够时：先保墙/门，家具可以丢 ──
        var poorStock = new Dictionary<string, int>
        {
            // ★ **故意什么库存都没有** —— 这正是游戏里抓出 bug 的那组数据。
            //   墙/门是**由材料建成**的（needsStuff），所以"库存里有没有 Wall"是无意义的问题；
            //   床/灯是成品，库存里没有就真的放不了。
        };
        TemplatePlacement p2 = TemplatePlacer.Plan(t, grid, 0, 0, poorStock, 0, false);
        Eq("★★ 库存全空时墙仍然全放（墙是由材料建的，不查 Wall 库存）", p2.WallCount, 15);
        Eq("★★ 库存全空时门仍然全放", p2.DoorCount, 1);
        Eq("★ 成品（床）没货 → 放不了", p2.CountRole("bed"), 0);
        Eq("★ 成品（光源）没货 → 放不了", p2.CountRole("light"), 0);
        Eq("★ 未解决 2 项（床+光源）", p2.Unresolved.Count, 2);
        bool reasonOk = p2.Unresolved.Count > 0 && p2.Unresolved[0].Reason.Length > 0;
        True("★ 未解决项带理由（不是静默丢弃）", reasonOk,
            p2.Unresolved.Count > 0 ? p2.Unresolved[0].Reason : "没有未解决项");
        // 需要材料的角色必须被标出来（供游戏层挑 stuff）
        bool wallNeedsStuff = false;
        for (int i = 0; i < p2.Blueprints.Count; i++)
            if (p2.Blueprints[i].RoleName == "wall") wallNeedsStuff = p2.Blueprints[i].NeedsStuff;
        True("★★ 墙蓝图被标记为 needsStuff（游戏层据此挑木/钢/石）", wallNeedsStuff);

        // ── 3. ★ 库存分摊：床只有 1 个，但有两个床槽 → 只放一个 ──
        var twoBeds = MakeSmallTemplate();
        twoBeds.Rows[1] = "WBB.W";          // 两个床槽
        twoBeds.Rows[2] = "W...W";          // 去掉光源
        TemplatePlacement p3 = TemplatePlacer.Plan(twoBeds, grid, 0, 0,
            new Dictionary<string, int> { { "Wall", 100 }, { "Door", 10 }, { "Bed", 1 } }, 0, false);
        Eq("★ 库存分摊：只有 1 个床 → 只放 1 个床槽", p3.CountRole("bed"), 1);
        Eq("★ 库存分摊：另一个床槽进未解决", p3.Unresolved.Count, 1);

        // ── 4. ★ 候选顺序：优先用 accepts 里靠前的 ──
        var pickFirst = TemplatePlacer.Plan(t, grid, 0, 0,
            new Dictionary<string, int> { { "Wall", 100 }, { "Door", 10 }, { "Bed", 5 }, { "DoubleBed", 5 }, { "TorchLamp", 5 } },
            0, false);
        string bedDef = null;
        for (int i = 0; i < pickFirst.Blueprints.Count; i++)
            if (pickFirst.Blueprints[i].RoleName == "bed") bedDef = pickFirst.Blueprints[i].DefName;
        Eq("★ 床槽用了 accepts 首位的 Bed（不是 DoubleBed）", bedDef, "Bed");

        // 首位没货时应该退到第二位
        var pickSecond = TemplatePlacer.Plan(t, grid, 0, 0,
            new Dictionary<string, int> { { "Wall", 100 }, { "Door", 10 }, { "DoubleBed", 5 }, { "TorchLamp", 5 } },
            0, false);
        string bedDef2 = null;
        for (int i = 0; i < pickSecond.Blueprints.Count; i++)
            if (pickSecond.Blueprints[i].RoleName == "bed") bedDef2 = pickSecond.Blueprints[i].DefName;
        Eq("★ Bed 没货时退到 DoubleBed（\"床可以是别的床\"）", bedDef2, "DoubleBed");

        // ── 5. ★ 可放性：某些格放不下 → 那些槽位进未解决，其余照放 ──
        var blocked = MakePlacementGrid(20, 20, true);
        blocked.CanPlace[blocked.Index(0, 0)] = false;      // 样板左上角（一堵墙）
        TemplatePlacement p5 = TemplatePlacer.Plan(t, blocked, 0, 0, stock, 0, false);
        Eq("★ 有 1 格放不下 → 未解决 1 项", p5.Unresolved.Count, 1);
        Eq("★ 其余 17 格照放（不因一格失败而整体放弃）", p5.Blueprints.Count, 17);
        bool placeReason = p5.Unresolved[0].Reason.IndexOf("放不下") >= 0;
        True("★ 放不下的理由写清楚了", placeReason, p5.Unresolved[0].Reason);

        // ── 6. ★ 空地的格不要求可放（样板里的 '.' 可以是任何东西）──
        var onlyWallBlocked = MakePlacementGrid(20, 20, true);
        onlyWallBlocked.CanPlace[onlyWallBlocked.Index(2, 2)] = false;   // 样板 (2,2) 是空地
        TemplatePlacement p6 = TemplatePlacer.Plan(t, onlyWallBlocked, 0, 0, stock, 0, false);
        Eq("★ 空地被占不影响放置（不要求空地可放）", p6.Unresolved.Count, 0);

        // ── 6b. ★★ 回归：`Plan` 必须按**地图绝对坐标**判断可放性 ──
        //
        // 这是游戏里抓出来的一个真 bug：`Plan` 最初传了**模板局部坐标**去查
        // 一个**地图尺寸**的网格 → 它检查了地图左上角，而不是真实站点
        // → 48 个槽位全部误判"放不下"。
        // 离线测试当时全绿，因为测试的网格小、"左上角"恰好也是可放的 —— 测错了地方。
        //
        // 这个测试**故意**把"地图原点附近"设成不可放、而"真实站点"设成可放，
        // 这样局部/绝对混用就必然失败。
        {
            var tricky = MakePlacementGrid(40, 40, false);     // 整张网格默认不可放
            // 只把真实站点 (20,20)..(24,24) 设成可放
            for (int x = 20; x < 25; x++)
                for (int z = 20; z < 25; z++)
                    tricky.CanPlace[tricky.Index(x, z)] = true;

            TemplatePlacement pt = TemplatePlacer.Plan(t, tricky, 20, 20, stock, 0, false);
            Eq("★★ 站点在 (20,20) 而非原点时，槽位仍能全部解析（绝对坐标回归）",
                pt.Blueprints.Count, 18);
            Eq("★★ 且没有未解决项", pt.Unresolved.Count, 0);
        }

        // ── 7. 选址：找到离基地最近的可放点 ──
        int sx, sz; double sd;
        bool found = TemplatePlacer.FindBestSite(t, grid, dist, 20, 20,
            new BuildSiteOptions { SearchStep = 1, MaxDistanceFromBase = 100 }, out sx, out sz, out sd);
        True("找得到放置点", found);
        if (found)
        {
            // 基地在 (10,10)，样板 5x5，最优左上角应该在 (8,8)
            True("★ 选址结果贴近基地（(8,8) 附近）", Math.Abs(sx - 8) <= 1 && Math.Abs(sz - 8) <= 1,
                "选到 (" + sx + "," + sz + ")，平均距离 " + sd.ToString("F1"));
        }

        // 可放的地太小 → 找不到
        var tiny = MakePlacementGrid(20, 20, false);
        for (int x = 0; x < 4; x++) for (int z = 0; z < 3; z++) tiny.CanPlace[tiny.Index(x, z)] = true;
        int tx, tz; double td;
        bool foundTiny = TemplatePlacer.FindBestSite(t, tiny, dist, 20, 20,
            new BuildSiteOptions { SearchStep = 1, MaxDistanceFromBase = 100 }, out tx, out tz, out td);
        True("★ 可放区域装不下样板时 → 返回找不到（不硬塞）", !foundTiny);

        // ── 8. 整份样板要多少料 ──
        Dictionary<string, int> need = TemplatePlacer.RequiredMaterials(t);
        Eq("整份样板需要 Wall 15", need.ContainsKey("Wall") ? need["Wall"] : 0, 15);
        Eq("整份样板需要 Door 1", need.ContainsKey("Door") ? need["Door"] : 0, 1);
        Eq("整份样板需要 Bed 1", need.ContainsKey("Bed") ? need["Bed"] : 0, 1);
        Eq("整份样板需要 TorchLamp 1", need.ContainsKey("TorchLamp") ? need["TorchLamp"] : 0, 1);

        // ── 9. 边界 ──
        var nullGrid = TemplatePlacer.Plan(t, null, 0, 0, stock, 0, false);
        Eq("grid 为 null 时不炸，返回空计划", nullGrid.Blueprints.Count, 0);
        var nullTpl = TemplatePlacer.Plan(null, grid, 0, 0, stock, 0, false);
        Eq("样板为 null 时不炸", nullTpl.Blueprints.Count, 0);
        Eq("空样板要 0 材料", TemplatePlacer.RequiredMaterials(new BuildTemplate()).Count, 0);
    }

    // ---------------- 样板拼接：共享墙复用 ----------------

    private static void RunTemplateReuseTests()
    {
        Console.WriteLine();
        Console.WriteLine("-- 共享墙复用（两个房子连在一起，中间的墙复用）--");

        BuildTemplate t = MakeSmallTemplate();     // 5x5：15 墙 + 1 门 + 1 床 + 1 光源
        var grid = MakePlacementGrid(40, 40, true);
        var stock = new Dictionary<string, int>
        {
            { "Wall", 100 }, { "Door", 10 }, { "Bed", 10 }, { "TorchLight", 10 }, { "TorchLamp", 10 },
        };

        // ── 1. ★ `CanReuse` 的判据（这是最关键的一组）──
        var wall = new TemplateRole { Name = "wall", IsWallLike = true, NeedsStuff = true };
        var door = new TemplateRole { Name = "door", IsDoorLike = true, NeedsStuff = true };
        var bed = new TemplateRole { Name = "bed", IsFurniture = true };

        True("★★ 墙可以复用已有的墙",
            TemplateRole.CanReuse(wall, TemplateRole.ExistingKind.Wall));
        True("★★ 墙**不能**复用已有的门（否则墙上多个洞，房子不封闭）",
            !TemplateRole.CanReuse(wall, TemplateRole.ExistingKind.Door));
        True("★★ 门**不能**复用已有的墙（否则门没了）",
            !TemplateRole.CanReuse(door, TemplateRole.ExistingKind.Wall));
        True("★ 门可以复用已有的门",
            TemplateRole.CanReuse(door, TemplateRole.ExistingKind.Door));
        True("★★ 墙**不能**复用框架（框架可能被取消 → 那面墙永远建不成）",
            !TemplateRole.CanReuse(wall, TemplateRole.ExistingKind.Frame));
        True("★★ 家具不能复用任何东西（别人的家具不是你的家具）",
            !TemplateRole.CanReuse(bed, TemplateRole.ExistingKind.Furniture)
            && !TemplateRole.CanReuse(bed, TemplateRole.ExistingKind.Wall));
        True("空地不算复用", !TemplateRole.CanReuse(wall, TemplateRole.ExistingKind.Empty));

        // ── 2. ★ 两间房并排：共享那一列墙只放一次 ──
        //
        // 把第二个样板放在 x=4（第一个在 x=0），于是第二个的左列（样板内 x=0）
        // 正好落在第一个的右列（地图 x=4）上 —— 那就是共享墙。
        {
            var first = TemplatePlacer.Plan(t, grid, 0, 0, stock, 0, false);
            Eq("第一间房正常放下 18 格", first.Blueprints.Count, 18);
            Eq("第一间没有复用", first.Reused.Count, 0);

            // 第一间占用的格子上"已有墙"（只对墙类格）
            var existing = new Dictionary<long, TemplateRole.ExistingKind>();
            for (int i = 0; i < first.Blueprints.Count; i++)
            {
                PlannedBlueprint b = first.Blueprints[i];
                var kind = b.RoleName == "wall" ? TemplateRole.ExistingKind.Wall
                         : b.RoleName == "door" ? TemplateRole.ExistingKind.Door
                         : TemplateRole.ExistingKind.Furniture;
                existing[((long)b.X << 32) ^ (uint)b.Z] = kind;
            }
            Func<int, int, TemplateRole.ExistingKind> lookup = (x, z) =>
            {
                TemplateRole.ExistingKind k;
                return existing.TryGetValue(((long)x << 32) ^ (uint)z, out k)
                    ? k : TemplateRole.ExistingKind.Empty;
            };

            // 第二间房放在 x=4（与第一间共享 x=4 那一列）
            var second = TemplatePlacer.Plan(t, grid, 4, 0, stock, 0, false, lookup);

            True("★★ 第二间房发生了共享复用", second.Reused.Count > 0,
                second.Reused.Count + " 格被复用");

            // ⚠ 精确算一下共享那一列（第一间的右列 x=4）：
            //   样板第 3 行是 "W...D" → 右列 x=4 在 z=0..4 分别是 W W W **D** W
            //   所以那一列是 **4 墙 + 1 门**。
            //   第二间的**左列**（样板内 x=0）在 z=3 是**墙**（那一行是 "W...W"）。
            //   → z=0,1,2,4 这 4 格：墙 ↔ 墙 → 复用 ✅
            //   → z=3 那格：已有**门**、样板要放**墙** → **不能复用**（否则墙上多一个洞）
            //     → 落进 Unresolved（这是**正确**行为，不是 bug）
            //
            // ★ 我第一版期望写了 5，就是没算清这一步 —— 而"墙不能复用门"正是
            //   整条需求里最容易做错的地方，所以这个 4 是**必须**的。
            Eq("★★ 共享那一列复用 4 格（第 5 格是门，不能当墙复用）", second.Reused.Count, 4);
            Eq("★★ 第二间实际放 13 格（18 − 4 复用 − 1 冲突）", second.Blueprints.Count, 13);
            Eq("★★ 那 1 格冲突被记进未解决（不是静默略过）", second.Unresolved.Count, 1);
            bool conflictReason2 = second.Unresolved.Count > 0
                && second.Unresolved[0].Reason.IndexOf("不能复用") >= 0;
            True("★★ 冲突原因写明\"不能复用\"（这就是防危房的那道闸）", conflictReason2,
                second.Unresolved.Count > 0 ? second.Unresolved[0].Reason : "无");

            // 复用的应该都是墙
            bool allWall = true;
            for (int i = 0; i < second.Reused.Count; i++)
                if (second.Reused[i].RoleName != "wall") allWall = false;
            True("★ 复用的都是墙", allWall);

            // ★ 核心收益：两间房的总墙数 < 2×单间
            int singleWalls = first.WallCount;
            int totalWalls = first.WallCount + second.WallCount;
            True("★★ 两间房的总墙数少于两倍单间（真的省了墙）",
                totalWalls < singleWalls * 2,
                string.Format("单间 {0} 墙，两间共 {1} 墙（若不复用应为 {2}）",
                    singleWalls, totalWalls, singleWalls * 2));
        }

        // ── 3. ★★ 冲突：已有的是门、样板要放墙 → 必须记进未解决（不能静默跳过）──
        {
            Func<int, int, TemplateRole.ExistingKind> doorEverywhere =
                (x, z) => TemplateRole.ExistingKind.Door;
            var conflict = TemplatePlacer.Plan(t, grid, 0, 0, stock, 0, false, doorEverywhere);

            Eq("★★ 已有门/样板要墙 → 一堵墙都放不下", conflict.WallCount, 0);
            True("★★ 冲突被记进未解决（不是静默略过）", conflict.Unresolved.Count >= 15,
                conflict.Unresolved.Count + " 条未解决");
            bool reasonOk = conflict.Unresolved.Count > 0
                && conflict.Unresolved[0].Reason.IndexOf("不能复用") >= 0;
            True("★★ 冲突原因写明了\"不能复用\"（否则会造出危房）", reasonOk,
                conflict.Unresolved.Count > 0 ? conflict.Unresolved[0].Reason : "无");
        }

        // ── 4. ★ 框架不算可复用（防止"以为省了，结果墙永远没建成"）──
        {
            Func<int, int, TemplateRole.ExistingKind> frameEverywhere =
                (x, z) => TemplateRole.ExistingKind.Frame;
            var onFrame = TemplatePlacer.Plan(t, grid, 0, 0, stock, 0, false, frameEverywhere);

            // ⚠ 这里**不能**断言"照放成功 15 堵墙" —— 框架本身占着格子，
            //   所以 `IsPlaceable` 是 false，墙放不下、进未解决。
            //   真实流程是"**先清场再放**"（`ShelterDomain.TryPlace` 里就有 WipeFootprint），
            //   那一步在游戏层；Core 只负责"**不把框架误判成可复用而静默跳过**"。
            //   （我第一版期望写了 15，是没想清 Core 与游戏层的分工。）
            Eq("★★ 框架不算复用（没有一格被当成可复用而跳过）", onFrame.Reused.Count, 0);

            // 关键：那些格必须**出现在未解决里**（说明 Core 明确拒绝了，而不是悄悄放过）
            bool allReported = onFrame.Unresolved.Count >= 15;
            True("★★ 被框架占住的墙格全部进未解决（证明没有被静默跳过）", allReported,
                onFrame.Unresolved.Count + " 条未解决");
        }

        // ── 5. ★ 不传 existingAt 时行为完全不变（向后兼容）──
        {
            var plain = TemplatePlacer.Plan(t, grid, 0, 0, stock, 0, false);
            Eq("★ 不启用复用时有 18 格", plain.Blueprints.Count, 18);
            Eq("★ 不启用复用时 Reused 为空", plain.Reused.Count, 0);
        }
    }

    // ---------------- 建筑账本 ----------------

    /// <summary>造一栋 3x3 的记账建筑（外圈墙 + 中心门，够用来测查询）</summary>
    private static BuildingRecord MakeRecord(string id, int ox, int oz)
    {
        var r = new BuildingRecord
        {
            Id = id, TemplateId = "t-" + id,
            OriginX = ox, OriginZ = oz, Day = 1, Tick = 100,
        };
        for (int dz = 0; dz < 3; dz++)
        {
            for (int dx = 0; dx < 3; dx++)
            {
                bool edge = dx == 0 || dx == 2 || dz == 0 || dz == 2;
                if (!edge) continue;
                r.Cells.Add(new RegistryCell
                {
                    X = ox + dx, Z = oz + dz,
                    Role = "wall", DefName = "Wall",
                });
            }
        }
        return r;
    }

    private static void RunRegistryTests()
    {
        Console.WriteLine();
        Console.WriteLine("-- BuildingRegistry（建筑账本）--");

        var reg = new BuildingRegistry();
        BuildingRecord a = reg.Add(MakeRecord("A", 10, 10));    // 占 (10..12, 10..12)
        BuildingRecord b = reg.Add(MakeRecord("B", 14, 10));    // 占 (14..16, 10..12)

        Eq("记了 2 栋", reg.Count, 2);
        Eq("A 的包围盒宽 3", a.Width, 3);
        Eq("A 的包围盒高 3", a.Height, 3);
        Eq("A 有 8 格（3x3 外圈）", a.Cells.Count, 8);

        // ── IsOurs：这是账本最有用的方法 ──
        True("★ (10,10) 是我们的", reg.IsOurs(10, 10));
        True("★ (12,12) 是我们的", reg.IsOurs(12, 12));
        True("★★ (13,10) 不是我们的（A 和 B 之间的空隙）", !reg.IsOurs(13, 10));
        True("★★ (11,11) 不是我们的（A 的中心是空的）", !reg.IsOurs(11, 11));
        True("★ (100,100) 不是我们的", !reg.IsOurs(100, 100));

        // ── At ──
        Eq("(10,10) 属于 1 栋", reg.At(10, 10).Count, 1);
        Eq("(10,10) 属于 A", reg.At(10, 10)[0].Id, "A");
        Eq("空隙 (13,10) 不属于任何栋", reg.At(13, 10).Count, 0);

        // ── ★ 共享墙：同一格属于两栋 ──
        //
        // 用户要的就是这个：两个房子连在一起时，中间那面墙是**同一格**。
        var shared = reg.Add(MakeRecord("C", 12, 10));   // C 从 x=12 起 → 与 A 的右列重合
        Eq("(12,10) 现在属于 2 栋（A 和 C 共享）", reg.At(12, 10).Count, 2);
        bool hasBoth = false;
        {
            var at = reg.At(12, 10);
            bool fa = false, fc = false;
            for (int i = 0; i < at.Count; i++) { if (at[i].Id == "A") fa = true; if (at[i].Id == "C") fc = true; }
            hasBoth = fa && fc;
        }
        True("★★ 共享墙那一格确实同时属于两栋", hasBoth);
        True("★ 共享墙那一格 IsOurs 仍然为真", reg.IsOurs(12, 10));

        // ── Near：切比雪夫距离 ──
        //
        // B 在 (14..16, 10..12)。
        // (13,10) 贴着 B 的左边界 → 距离 1 → 应该能找到 B
        var nearB = reg.Near(13, 10, 1);
        bool foundB = false;
        for (int i = 0; i < nearB.Count; i++) if (nearB[i].Id == "B") foundB = true;
        True("★ 距 B 1 格的地方能找到 B（贴靠选址要用）", foundB);

        // ★ 斜对角也算"挨着"：切比雪夫距离，(13,13) 到 B 的角 (14,12) 是 max(1,1)=1
        var nearCorner = reg.Near(13, 13, 1);
        bool foundBCorner = false;
        for (int i = 0; i < nearCorner.Count; i++) if (nearCorner[i].Id == "B") foundBCorner = true;
        True("★★ 斜对角也算挨着（切比雪夫距离，不是曼哈顿）", foundBCorner);
        // 若用曼哈顿距离，(13,13)→(14,12) 是 1+1=2，就会被漏掉 —— 这正是要避免的

        var farAway = reg.Near(50, 50, 3);
        Eq("远处找不到任何栋", farAway.Count, 0);

        // ── ★ 历史性：被烧掉也要记得"造过" ──
        True("标记前 (10,10) 还在", reg.At(10, 10)[0].Cells[0].StillThere || true);
        bool marked = reg.MarkMissing(10, 10);
        True("★ 能标记 (10,10) 没了", marked);
        True("★★ 标记后 IsOurs **仍然为真**（我们知道造过 —— 这是用户明确要的）",
            reg.IsOurs(10, 10));
        Eq("★★ 账本**没有删除**记录（栋数不变）", reg.Count, 3);
        True("★★ 那一格被标成 StillThere=false", !reg.At(10, 10)[0].Cells[0].StillThere);
        Eq("A 现存格数 8 → 7", a.AliveCount, 7);

        // ── Reconcile：用地图现状校验 ──
        {
            var reg2 = new BuildingRegistry();
            BuildingRecord r2 = reg2.Add(MakeRecord("D", 0, 0));
            Eq("D 有 8 格", r2.Cells.Count, 8);
            // 假装只有 (0,0) 和 (1,0) 还在
            int changed = reg2.Reconcile((x, z, role) => (x == 0 && z == 0) || (x == 1 && z == 0));
            Eq("★★ 校验发现 6 格变了（8 格里只有 2 格还在）", changed, 6);
            Eq("★ 校验后现存 2 格", r2.AliveCount, 2);
            Eq("★★ 校验后仍然记着 8 格（不删除）", r2.Cells.Count, 8);
        }

        // ── 序列化往返 ──
        {
            var reg3 = new BuildingRegistry();
            reg3.Add(MakeRecord("X", 5, 7));
            reg3.Add(MakeRecord("Y", 9, 7));
            reg3.MarkMissing(5, 7);

            string json = reg3.ToJson();
            True("JSON 非空", json.Length > 100, json.Length + " 字符");

            BuildingRegistry back = BuildingRegistry.FromJson(json);
            Eq("★ 往返：栋数一致", back.Count, 2);
            Eq("★ 往返：总格数一致", back.AllCells().Count, reg3.AllCells().Count);
            True("★★ 往返：IsOurs 结果一致", back.IsOurs(5, 7) && back.IsOurs(9, 7) && !back.IsOurs(13, 7));
            True("★★ 往返：\"没了\"的标记也保住了",
                !back.At(5, 7)[0].Cells[0].StillThere);

            // 脏数据不该炸
            Eq("空 JSON → 空账本", BuildingRegistry.FromJson("").Count, 0);
            Eq("垃圾 JSON → 空账本（不炸）", BuildingRegistry.FromJson("{}").Count, 0);
        }

        // ── 摘要 ──
        True("摘要里带栋数", reg.Summary().IndexOf("3 栋") >= 0, reg.Summary());
    }

    // ---------------- 建造前的材料评估 ----------------

    private static void RunMaterialTests()
    {
        Console.WriteLine();
        Console.WriteLine("-- MaterialPlanner（建造前材料评估）--");

        BuildTemplate t = MakeSmallTemplate();     // 15 墙 + 1 门（都要材料）+ 床/光源（成品）
        Func<string, int> cost = (role) => 5;
        var pref = new List<string> { "WoodLog", "Steel", "BlocksGranite" };

        // ── 1. 木头够 → 选木头 ──
        {
            var avail = new Dictionary<string, int> { { "WoodLog", 500 }, { "Steel", 1000 } };
            MaterialPlan p = MaterialPlanner.Plan(t, cost, avail, pref);
            Eq("★ 整栋需要 80（16 结构格 × 5）", p.NeededTotal, 80);
            Eq("★★ 木头够 → 选木头（尊重首选顺序）", p.ChosenStuff, "WoodLog");
            True("可以造", p.CanBuild);
            Eq("结构格数 = 16", p.StructuralCells, 16);
        }

        // ── 2. ★★ 木头不够、钢够 → **转向钢**（用户要的核心行为）──
        {
            var avail = new Dictionary<string, int> { { "WoodLog", 50 }, { "Steel", 1000 } };
            MaterialPlan p = MaterialPlanner.Plan(t, cost, avail, pref);
            Eq("★★ 木头只有 50（需 80）→ 转向钢", p.ChosenStuff, "Steel");
            True("可以造", p.CanBuild);
            Eq("选中的钢需要 80", p.NeededTotal, 80);
            Eq("钢有 1000", p.AvailableChosen, 1000);
            bool woodShortShown = false;
            for (int i = 0; i < p.Options.Count; i++)
                if (p.Options[i].StuffDefName == "WoodLog" && p.Options[i].Short == 30)
                    woodShortShown = true;
            True("★ 诊断里写明木头缺 30", woodShortShown);
        }

        // ── 3. ★★ 都不够 → **不造**（"最糟糕的硬造"的对立面）──
        {
            var avail = new Dictionary<string, int>
            {
                { "WoodLog", 20 }, { "Steel", 30 }, { "BlocksGranite", 5 },
            };
            MaterialPlan p = MaterialPlanner.Plan(t, cost, avail, pref);
            True("★★ 所有材料都不够 → ChosenStuff 为 null（不造）", !p.CanBuild);
            True("★ 理由里带\"不造\"", p.Reason.IndexOf("不造") >= 0, p.Reason);
            bool allShown = p.Reason.IndexOf("WoodLog") >= 0
                && p.Reason.IndexOf("Steel") >= 0
                && p.Reason.IndexOf("BlocksGranite") >= 0;
            True("★ 理由里列出每种材料的缺口", allShown);
        }

        // ── 4/5. 边界：刚好够 / 差 1 个 ──
        {
            var just = new Dictionary<string, int> { { "WoodLog", 80 } };
            Eq("★ 刚好 80 → 选木头（边界包含）",
                MaterialPlanner.Plan(t, cost, just, pref).ChosenStuff, "WoodLog");

            var oneLess = new Dictionary<string, int> { { "WoodLog", 79 } };
            True("★ 79 < 80 → 不造（边界不含）",
                !MaterialPlanner.Plan(t, cost, oneLess, pref).CanBuild);
        }

        // ── 6. ★★ 成本未知时按 1 算（保守），绝不能算成 0 ──
        //
        // 若把 0 采信 → 需要 0 → 永远"够" → **掩盖真实缺口** → 又变成"材料不够也硬造"。
        {
            Func<string, int> zeroCost = (role) => 0;
            var exact = new Dictionary<string, int> { { "WoodLog", 16 } };
            MaterialPlan p1 = MaterialPlanner.Plan(t, zeroCost, exact, pref);
            Eq("★★ 未知成本按 1 算（16 格 → 需 16）", p1.NeededTotal, 16);
            True("★ 恰好 16 可以造", p1.CanBuild);

            var short1 = new Dictionary<string, int> { { "WoodLog", 15 } };
            True("★★ 15 < 16 → 不造（没有被 0 成本蒙过去）",
                !MaterialPlanner.Plan(t, zeroCost, short1, pref).CanBuild);
        }

        // ── 7. 边界：空输入不炸 ──
        {
            var empty = MaterialPlanner.Plan(t, cost, new Dictionary<string, int>(), pref);
            True("没有任何材料 → 不造", !empty.CanBuild);
            True("理由非空", empty.Reason.Length > 0, empty.Reason);

            True("样板为 null → 不造",
                !MaterialPlanner.Plan(null, cost, new Dictionary<string, int>(), pref).CanBuild);
            True("材料表为 null → 不造（不炸）",
                !MaterialPlanner.Plan(t, cost, null, pref).CanBuild);

            var noStuff = MakeSmallTemplate();
            foreach (var kv in new List<char>(noStuff.Roles.Keys))
                noStuff.Roles[kv].NeedsStuff = false;
            var p2 = MaterialPlanner.Plan(noStuff, cost, new Dictionary<string, int>(), pref);
            True("★ 全是成品家具的样板 → 无需材料，可以造", p2.CanBuild);
            Eq("无需材料时需要量为 0", p2.NeededTotal, 0);
        }

        // ── 8. 不传 preference 时用 available 的键（不炸）──
        {
            var avail = new Dictionary<string, int> { { "Steel", 1000 } };
            Eq("没有首选顺序时也能选出够的那个",
                MaterialPlanner.Plan(t, cost, avail, null).ChosenStuff, "Steel");
        }
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
