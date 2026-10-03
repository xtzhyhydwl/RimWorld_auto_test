# FoodControl 架构与流程

> 回答两个问题：**① 这个 mod 的执行流程长什么样？② 现在的结构能不能低成本地扩展到「小人心情/吃穿/住处」？**
> 依据：`src/foodcontrol-game/*.cs` 的**实际代码**（不是设计意图），2026-10-03。

---

## 一、现在的真实流程

### 1.1 从游戏到闭环：谁在什么时候叫它

```
                    RimWorld 游戏进程
                           │
      ① Verse.LoadedModManager.CreateModClasses()
         └─ 扫每个已加载程序集里 Verse.Mod 的子类
            └─ Activator.CreateInstance(ModBoot, modContentPack)
                                  │
                                  ▼
      ② Game.FillComponents()    ← ★ 自动发现，不用手动注册
         foreach t in typeof(GameComponent).AllSubclassesNonAbstract()
             components.Add(Activator.CreateInstance(t, this))
         （GenTypes.AllActiveAssemblies 会 yield 每个 mod 的 loadedAssemblies
           ⇒ 所以本 mod 的 GameComponent 会被自动 new 出来挂上）
                                  │
                                  ▼
      ③ GameComponentTick()  ← 每个游戏 tick 都被调
```

**为什么 `GameComponentTick` 里那三层过滤是必须的**（少一层就会出问题）：

```
GameComponentTick()
│
├─ ➊ if (!Enabled) return                      总开关
├─ ➋ if (GenTicks.TicksGame & 0x3F) != 0 return  ← 64 tick 粒度节流
│      为什么：一天 60000 tick，日期检查没必要每 tick 做；
│      窗口内还有几百次机会，不影响精度，但省掉 ~98% 的调用。
│
└─ try {
     ├─ ➌ ProgramState != Playing      → return   不在游戏里
     ├─ ➍ Current.Game == null          → return   没有游戏
     ├─ ➎ CurrentMap == null || !IsPlayerHome → return   没地图/不是玩家基地
     ├─ ➏ hour < 8 || hour > 19         → return   ★ 决策时段窗口
     ├─ ➐ day == _lastRunDay            → return   ★ 每天只跑一次
     │
     └─ RunCycle(map, "auto")
   } catch { Log.Error(...) }   ← 整段包在 try/catch，mod 异常绝不外抛
```

**➏ 决策时段窗口（8:00–19:00）不是随手加的** —— 它是为了防一个具体的坑：

> 实测 `Plant.GrowthRate` 在**夜里是 0**（机制是 `Plant.Resting`，一天中约 0.25–0.8 的时段之外）。
> 如果在夜里决策，预测器会认为「所有在种的作物**永远不会成熟**」
> → 缺口被**系统性高估** → mod 会反复扩大种植。
> **限制在白天决策，这个坑就不会发生。**

**➐ `_lastRunDay` 会存档**（`ExposeData` 里 `Scribe_Values.Look`），
所以读档不会重复决策一次。

---

### 1.2 一轮闭环（`RunCycle`）的七步

```
RunCycle(map, source)
│
├─ ① ResolveCropDef(PolicySettings.PreferredPlantDefName)
│     defName → ThingDef
│     ⚠ 用 GetNamed(name, errorOnFail: false)：玩家配置写错一个名字不该刷红字
│                                        │
├─ ② PlotPlanner.BuildPlan(map, crop, SiteOptions, MaxCellsPerDecision)
│     ┌────────────────────────────────────────────────────────┐
│     │ 地图 → PassabilityMap（可种性 / 肥力 / 通行代价）        │
│     │ 基地中心（兜底链）：Home 质心 → 最近贮藏区 → 地图中心     │
│     │ 带地形代价的 BFS 距离场（不是欧氏距离）                  │
│     │ 复用已有种植区空地？→ 否则 SiteSelection.FindBestPlot    │
│     └────────────────────────────────────────────────────────┘
│                                        │
├─ ③ StateReader.Read(map, plan) → GameReadings（游戏原始读数）
│    食物总量 / 殖民者数 / 在种作物 / 可用空地 / 季节
│     ⚠ 食物判据用 ThingCategory == Item 做硬闸
│       （否则地里长着的野草和树会被算成食物 —— 实测踩过，算出 2903 天口粮）
│                                        │
├─ ④ StateReader.ToFoodState(readings, plan, CalibrationSettings) → FoodState
│    把游戏读数翻译成 **Core 能懂的数据结构**（Core 不许出现游戏类型）
│                                        │
├─ ⑤ FoodControl.Core.Policy.Decide(state, PolicySettings) → FoodDecision
│    ✅ 纯函数，零游戏依赖，可离线穷举测试
│    产出：动作（DoNothing / PlantCrop） + 预测曲线 + 首次跌破天数 + 人话理由
│                                        │
├─ ⑥ Executor.Execute(decision.Action, plan, map) → ExecutionResult
│    ★ 三段式，缺一不可：
│       new Zone_Growing(map.zoneManager)
│       map.zoneManager.RegisterZone(z)        ← ★ 缺这步 = 僵尸区，小人永不去种
│       ZoneMaker.MakeZoneWithCells(z, cells)  ← 只负责逐格 AddCell
│       z.SetPlantDefToGrow(crop)
│    ★ 回读验证三项全过才算成功：
│       AllZones 里 ReferenceEquals 找到它
│       且 CellCount > 0
│       且格级 ZoneAt(c) 指回它
│    空区回滚：注册后若 0 格 → Delete(false)
│      （否则 WorkGiver_Grower 会 Log.ErrorOnce "Grow zone has 0 cells"）
│                                        │
└─ ⑦ Telemetry.Write(...)  → foodcontrol-telemetry.jsonl
      一行 JSON：tick/day/season/stock/burnRate/reserveDays/
                 projectedReserve[]/action{kind,cells,reason}/执行细节
      ⚠ 写文件失败**绝不让 mod 崩**
      ⚠ 失败也要留痕（catch 里再写一条带 error 的）
```

**整段包在 try/catch 里，任何异常都吞进 `CycleResult.Error` 并记遥测 ——
mod 异常绝不把游戏搞崩。**

---

### 1.3 分层与依赖方向（关键：依赖是单向的）

```
        ┌──────────────────────────────────────────────┐
        │  src/foodcontrol-core/   （FoodControl.Core） │
        │  ★ 不许出现 Verse./RimWorld./UnityEngine.     │
        │                                              │
        │  Models        FoodState / PlantGroup /       │
        │                Calibration / FoodAction       │
        │  Projection    15 天存量曲线（纯函数）          │
        │  Policy        目标带规则 → 动作                │
        │  SiteSelection 距离场 / 评分 / 基地中心         │
        └───────────────────▲──────────────────────────┘
                            │ 只依赖这一个方向
        ┌───────────────────┴──────────────────────────┐
        │  src/foodcontrol-game/   （FoodControl.dll）  │
        │  ModBoot                静态测试钩子           │
        │  FoodControlGameComponent  调度 + 闭环编排     │
        │  StateReader            游戏 → Core           │
        │  PlotPlanner            地图 → Core           │
        │  Executor               Core 动作 → 游戏       │
        │  Telemetry              落盘                  │
        └───────────────────▲──────────────────────────┘
                            │ 反射调用（不硬引用）
        ┌───────────────────┴──────────────────────────┐
        │  bridge/DSH.FoodProbe/   （伴生 DLL，测试用）  │
        │  7 个 foodprobe/* 桥接工具                     │
        └──────────────────────────────────────────────┘
```

**为什么 Core 不许碰游戏类型**：`scripts/build-core.mjs` 里有**静态检查**，
一旦 Core 里出现 `Verse.` / `RimWorld.` 就**直接编译失败**。
这不是洁癖 —— 它是「逻辑可以毫秒级离线验证」的**机制保证**：
现在 59 个 Core 测试跑完不到 1 秒，而每次开游戏测试要 12 秒启动 + 建殖民地。

**为什么探针用反射而不是硬引用 mod**：mod 没加载时能给出清楚诊断
（"程序集 FoodControl 未加载"），而不是整个 DLL 加载失败。

---

## 二、能不能低成本扩展到「心情 / 吃穿 / 住处」？

### 2.1 先说结论

**部分能，但需要一次重构 —— 而且现在是最好的时机。**

现在**没有任何其他 mod 依赖它、也没人存档**，重构成本最低。
等以后有了存档和玩家再改结构，代价会大得多。

### 2.2 好消息：可复用的部分比想象的多

| 已有资产 | 能不能直接复用 | 说明 |
| --- | --- | --- |
| **Core 的纯逻辑 + 离线测试机制** | ✅ 完全复用 | 任何新领域的策略/预测都能这样测 |
| **`SiteSelection`（距离场/基地中心/评分）** | ✅ 直接复用 | 住处选址、储物区选址都要"离基地多近" |
| **观测通道（telemetry jsonl）** | ✅ 完全复用 | 换个 schema 而已 |
| **测试钩子（静态入口 + 伴生 DLL）** | ✅ 完全复用 | `RunCycleNow()` 这套照搬 |
| **探针的编译验证机制** | ✅ 完全复用 | 新领域照样先编一遍再部署 |
| **`Executor` 的回读验证纪律** | ✅ 概念复用 | "断言状态而非断言调用成功" |
| **`GameComponentTick` 的三层过滤** | ✅ 结构复用 | 只是过滤条件要按领域改 |
| **`Policy.Decide` 这条规则** | ❌ **不可复用** | 它是食物专用的（目标带 + nutrition 缺口） |

### 2.3 坏消息：闭环现在是**硬编码七步**

`RunCycle` 里这七步是**写死的**：

```
ResolveCropDef → BuildPlan → Read → ToFoodState → Policy.Decide → Execute → Telemetry
```

想加「心情」，只能再写一个方法 `RunMoodCycle()`，把七步抄一遍。
**加三个领域就是抄三遍** —— 而且调度逻辑（每天一次/时段窗口/异常吞掉）也会重复。

**这就是需要重构的地方。**

### 2.4 建议的重构（最小代价）

核心思路：**把"食物"从"框架"里摘出来，变成一个可插拔的领域模块。**

```
现在（硬编码）                        改后（可插拔）
─────────────────────────────       ──────────────────────────────────────
                                     interface INeedDomain          ← 新
GameComponentTick()                    string Id                    // "food"/"mood"/"shelter"
  └─ RunCycle()                        int    Priority              // 冲突时谁先
       ├─ ResolveCropDef()             bool   ShouldRun(map, now)   // 自己的运行条件
       ├─ PlotPlanner.BuildPlan()      SenseResult Sense(map)       // ① 感知
       ├─ StateReader.Read()           object      Predict(sensed)   // ② 预测（可选）
       ├─ ToFoodState()                Action      Decide(sensed,p)  // ③ 决策
       ├─ Policy.Decide()              ExecResult  Execute(action)  // ④ 执行
       ├─ Executor.Execute()           string      Describe(sensed)  // ⑤ 遥测行
       └─ Telemetry.Write()
                                     NeedCoordinator               ← 新：统一编排
                                       ├─ 统一调度（节流/时段/每天一次/异常吞掉）
                                       ├─ 按 Priority 依次跑各 domain
                                       └─ 统一写遥测（带 domain 标签）
                                              ▲
                           ┌──────────────────┴──────────────────┐
                    FoodDomain（现在的逻辑搬进来）         MoodDomain（新）
```

**收益**：
- 加一个领域 = **只写一个 `INeedDomain` 实现**，调度/遥测/测试钩子都不用碰
- 现有的 `StateReader` / `PlotPlanner` / `Executor` 都变成 `FoodDomain` 的内部实现，
  **代码基本不用改，只是搬个位置**
- 遥测天然带 domain 标签，测试脚本可以按领域断言

**成本估计**：约一天的重构（抽接口 + 搬食物逻辑 + 改调度），
Core 和测试**几乎不用动**（这是前面分层做对了的红利）。

### 2.5 但真正难的不是架构，是**领域之间会互相冲突**

这是必须提前想清楚的，架构能不能扩展是次要问题：

| 冲突 | 例子 | 需要什么机制 |
| --- | --- | --- |
| **资源竞争** | 食物要种地，住处要盖房 —— 同一批小人工时有限 | 全局优先级 / 仲裁器 |
| **人力竞争** | 食物说"都去种地"，心情说"都去娱乐" | 同上 |
| **空间竞争** | 选址算法各自找"离基地近的好地" | 共享放置决策 / 预留区 |
| **目标冲突** | 心情要精致食物，食物要最大热量产出 | 需要权重或优先级 |
| **级联失败** | 食物崩了 → 心情崩了 → 更种不出食物 | 需要"保命线"概念 |

**建议的演进顺序**（不要一次全上）：

```
P1  食物闭环（现在）         ← 单领域，把闭环跑通
P2  抽 INeedDomain 接口      ← 重构，零功能变化，只是让 P3/P4 便宜
P3  第二个领域：住处         ← 相对静态（盖房/铺床），与食物的冲突面小
P4  第三个领域：心情         ← 最动态，依赖吃穿住都稳了才有意义
P5  仲裁器（优先级/预算）     ← 等真的出现冲突再写，别提前设计
```

**P3 为什么选"住处"**：它是**低频、半静态**的（盖完就不动），
和 "食物"（持续滚动）的节奏差异大，冲突面小，适合当第二个练手。
**心情放最后**：它同时依赖食物、住处、衣着、娱乐，是**结果**而不是**原因** ——
前面几个稳了，心情自然就稳了。

### 2.6 一个反直觉但重要的判断

> **不要为了"以后能扩展"而过早做通用框架。**

现在这七步硬编码其实是**优点**：它让 P1 能被真正测通。
如果一上来就做 `INeedDomain`，会在**还不知道第二个领域长什么样**的情况下定接口，
大概率定错，然后被自己的抽象绑架。

**建议**：先让食物闭环**真的在游戏里跑起来并验收通过**，
再抽接口。那时候你会知道：哪些步骤是真的通用（遥测、异常吞掉、时段窗口），
哪些只是食物专有（选作物、nutrition 换算）。

**抽接口的正确时机是「第二个领域的具体需求已经清楚」，不是「隐约觉得以后会有」。**

---

## 三、给验收用的现状清单

> ⚠ 这一节会过时，以 `STATUS.md` 为准。

| 项 | 状态 |
| --- | --- |
| Core 逻辑层（预测/决策/选址/储藏区聚类） | ✅ **79/79** 离线测试通过 |
| 地基（建区+注册+设作物+小人真去种） | ✅ 14/14 实测通过 |
| S1 开局允许所有物资 | ✅ 14/14 实测通过 |
| S2 域框架（`INeedDomain` + `NeedCoordinator`） | ✅ 编译通过，验证中 |
| S3 储藏区 domain | 🔄 实现中 |
| S4 住处 domain | ⬜ |
| S5 5 天生存验收 | ⬜ |
| 标定实测（每格产量、每日消耗） | ✅ 已实测（`harvestYield=6`、`RawRice` 营养 `0.05`、`growDays=3`） |

---

## 四、⚠ 一个自己引入又自己抓到的 bug：按天去重被删掉（2026-10-03）

**这是 S2 重构时我犯的错，值得单独留档** —— 因为它的表现极具欺骗性。

### 症状

日志里 mod 的决策**每个游戏日重复约 900 次**（间隔 64 tick，内容完全一样）：

```
[FoodControl] tick=154240 day=3(Spring) 存量=34.2 消耗=4.80/天 … 动作=PlantCrop Plant_Rice ×41
[FoodControl] tick=154304 day=3(Spring) 存量=34.2 消耗=4.80/天 … 动作=PlantCrop Plant_Rice ×41
[FoodControl] tick=154368 day=3(Spring) …
```

### 成因

重构 `NeedCoordinator` 时，我把"按天去重"的**检查**删掉了，只保留了"执行后记账"：

```csharp
// ❌ 我当时的写法（错的）
bool shouldRun = domain.ShouldRunToday(ctx);
if (!shouldRun) { ...; continue; }      // 时段没到 → 不跑，也不记账
_lastRunDay[domain.Id] = ctx.Day;       // 执行后记账
RunCycle(...);
```

看起来没问题，实际漏了关键一环：**没有"今天跑过了就跳过"这个判断**。
于是白天的每一次协调器调用（每 64 tick）都会走到 `RunCycle`。

### 为什么极具欺骗性

**日志里"有决策"看起来是对的** —— 我最初就是看到日志有正常输出才以为没事。
但实际上那是**同一轮被重复执行了 900 次**。

> ⚠ **教训：验证不能只看"有没有发生"，要看"发生了几次"。**
> 这个 bug 让"调用成功"和"功能正确"的差别变得极其具体。

它还污染了别的观察：看起来像"结论一直没变"，实际是同一轮被反复跑。

### 修法：把两件事分开

「**今天跑过了吗**」和「**现在到时段了吗**」是**两个独立的判断**，不能合并：

```csharp
// ① 按天去重（必须在 ShouldRunToday 之前）
if (LastRunDayOf(domain.Id) == ctx.Day) { ...; continue; }

// ② 时段（域自己的节奏）
if (!domain.ShouldRunToday(ctx)) { ...; continue; }   // ⚠ 这里**不记账**
_lastRunDay[domain.Id] = ctx.Day;                     // 决定要跑了，先记账再跑
RunCycle(...);
```

第 ② 步**不能记账**：`ShouldRunToday` 是时段敏感的（食物域等 8:00–19:00），
凌晨第一次 tick 若记了账，当天 8 点之后就再也不会跑了。
而第 ① 步**必须**存在，否则就是那个 900 次 bug。**两个坑方向相反，必须同时避开。**

### 顺手加的廉价早退

组件每 64 tick 都会调协调器，而完整跑一遍要建上下文（含解析 Def）+ 遍历域。
加了 `NeedCoordinator.AnyDomainDue(day, hour)`：只做字典查找 + 几次时段比较，
**不碰游戏数据**，答案通常是"没事可做"。

### 回归测试

`scripts/verify-s2b-dedup.mjs`（**8/8 通过**）。关键设计：
**断言数量，不断言有无**。

```
决策日志共 2 行，不同指纹 2 组
同一指纹最多重复 1 次          ← 旧 bug 下会是几百
totalCycles 增长 2，上限 3      ← 跨 1 天 × 1 域
```

新增探针工具 `foodprobe/domains`（读注册了哪些域、各自上次跑过哪天、当前日/时段），
让测试能直接验证协调器的内部状态，而不用从日志反推。

---

## 五、本仓库的两个"看代码"陷阱（都踩过）
### 4.1 看中文源码要用 `read` 工具，不要用 PowerShell 的 `Get-Content`

```
PS> Get-Content src\foodcontrol-game\PlotPlanner.cs
    // Core 瑕佹眰璐棌鍖哄€欓€夊繀椤诲甫 CenterIndex    ← 乱码！
```

这是 **PowerShell 的默认解码不是 UTF-8** 导致的**显示问题**，**文件本身是好的**
（用 `read` 工具看同一行完全正常）。

⚠ 但它很危险：**看起来像文件被写坏了**，会让人误以为要修复编码而去做无用功
（甚至真的去"修"而把文件弄坏）。**先换工具确认，再决定要不要动文件。**

### 4.2 改文件不要用 PowerShell 的 `Set-Content` 批量替换

我曾用一个 PowerShell 脚本把多个 `.cs` 里的 `Projection` 批量改名成 `ForecastResult`。
这次没出事（`-Encoding UTF8` 生效了），但这是一次**没必要的风险**：
一旦编码处理不当，中文注释会静默损坏，而**编译照样通过**，你根本不会发现。

→ 改文件用 `edit` 工具（按 UTF-8 处理，且要求先 read）。
→ 真要批量替换，改完必须用 `read` 抽查中文注释是否完好。

---
