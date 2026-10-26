# 交接：ColonyPilot（通用殖民地自动化）

> 写于 **2026-10-04**。这是给**新会话**的接力文件。
> 上一个会话（2026-10-03 晚 → 10-04 凌晨）做完了「样板语义层 / 分段材料 / 代码规范」+
> 「AI 托管开关 + 意图窗口 + 进程间通信框架」，并在末尾**只出设计、不动代码**地
> 完成了「ColonyPilot 通用化与改名」的方案。
>
> **本文件是唯一入口**。读完它 → 读它点名的那几份文档 → 就能接手。

---

## 〇、一句话目标

把现在这个 **FoodControl mod 改造成 `ColonyPilot`** —— 一个**几乎覆盖殖民地所有方面**的
自动化 mod（不只是 food），并且**框架要能持续接入更多操作**（加东西 = 加接口，不改流程）。
先离线完善，简单测试之后，用户可能换别的模型来接手继续做。

用户原话（2026-10-04）：

> 「我本来的打算就是要做一个**几乎能够覆盖所有方面**、至少我们能覆盖的都给它覆盖到的
>   **那种自动化操作的 mod**，而不只是和 food 相关的。那么在考虑的时候就要考虑
>   **之后会接入更多的操作**。」

> 「把它叫做 **copilot**，应该也没什么问题。就像写代码的时候也有 copilot。
>   这个实际上它是一个**辅助的**性质。」

---

## 一、★ 开工前必读的三条铁律（用户 2026-10-04 明确要求）

### ① 不要重置用户的设置，也不要重置 mod 的设置

> 「你要注意测试的话，**不要去重置我的各种设置，还有 mod 的设置**。」

**实测事实（我核过代码，不是猜的）**：`node src/cli.js start` 走 `src/prefs.js` 的
`ensurePrefs()`，它只强制 `FORCE_ALWAYS_PREFS`（窗口/安全那几个键），
**其余键只在 `Config/Prefs.xml` 里还没有该键时「播种」一次**，
之后**以游戏里存下来的为准**。

→ 所以：**你在游戏里改的语言 / dev mode / UI 缩放 / 音量 / 读档暂停，不会被我重启实例重置。**
`profiles/vanilla/prefs.json` 里有一条中文备注写着这件事（PowerShell 读会乱码，用 `read` 工具看）。

**具体禁止**：
- 不要删 / 重建 `profiles/vanilla/Config/Prefs.xml`
- 不要跑会写 `Prefs.xml` 的脚本去"恢复默认"
- 不要动 `profiles/vanilla/Config/KeyPrefs.xml`、`Knowledge.xml`
- mod 自己的开关（`TakeoverController.Mode`、鼠标格显示）存在**存档**里，也别去清

### ② mod 列表只许**加**，不许删

> 「你可以往 mod 的 list 里边**加入**这个 copilot，但是**不要去尝试移除别的无关的 mod**，
>   除非这个 mod **真的影响到了你测试**，那么你就**告诉我**，我来评估怎么处理合适。」

**当前 `profiles/vanilla/Config/ModsConfig.xml` 的 activeMods（实测）**：

```xml
brrainz.harmony / ludeon.rimworld / .royalty / .ideology / .biotech / .anomaly / .odyssey
imranfish.xmlextensions / erdelf.HumanoidAlienRaces / Solaris.RatkinRaceMod
brrainz.rimbridgeserver / dsh.autotest / void.charactereditor / dsh.colonyrecorder
```

★ **注意一个大坑**：`dsh.autotest` 与 `dsh.colonyrecorder` 是**两个不同的东西**，
走的是两个不同的 junction。**`mods/FoodControl` 这个 junction 虽然存在，但它的 packageId
`dsh.foodcontrol` 不在 activeMods 里** ——
**也就是说这个 mod 目前没被游戏加载过**。

→ 要测它，只能**往 activeMods 里加 `<li>dsh.foodcontrol</li>`（改名后是 `dsh.colonypilot`）**。
ModsConfig 是**启动时读一次**，所以加完必须重启游戏（`stop` → 改 → `start`），**运行中改无效**。
加之前**先备份** `ModsConfig.xml`。

### ③ 游戏可以开、可以测（用户已同意）

> 「你可以开游戏去测试，去稍微测试一下，没问题的。」

⚠ 但**先跟快照线（`src/colonyrecorder/`，`dsh.colonyrecorder`）协调**：它也在用这个实例。

---

## 二、当前环境（哪些是真跑得起来的）

| 项 | 值 |
| --- | --- |
| 工作目录 | `E:\2026.7以后的新工程\DSH\RimWorld_auto_test` |
| 游戏 | RimWorld **1.6.9676**（`Assembly-CSharp` 版本号，实测） |
| 隔离实例 | `profiles/vanilla`（`-savedatafolder`），副屏（`monitor: 2`），1600×900 窗口 |
| 启动 / 停止 | `node src/cli.js start` / `stop`（**必须后台任务托管**；`start` 显示 completed 是正常的） |
| 桥接 | `127.0.0.1:5174`，token 在 `profiles/vanilla/Player.log` 里（`node src/cli.js bridge` 也能拿） |
| 探针 | `bridge/DSH.FoodProbe/` → 部署到 `<游戏目录>/BridgeTools/DSH.FoodProbe/`。**当前部署的是旧版**（新加的 `takeover` / `intents` 两个工具**没部署**，因为游戏在跑时 DLL 被锁） |
| Core 离线测试 | `node scripts/build-core.mjs` → **357/357**（毫秒级，**不需要游戏**） |
| 探针解析器测试 | `node scripts/test-probe-parser.mjs` → **13/13**（离线） |
| 编译 mod | `node scripts/build-mod.mjs`（只编译到 `tmp/`）；`--deploy` 拷进 `mods/FoodControl/1.6/Assemblies` |
| 部署到游戏 | 见 §六 |

**游戏现在没在跑**（`gameRunning: false`，实测）。

> ⚠ 2026-10-13 更正：上面这条结论**不可信**。当时 `node src/cli.js status` 的 `gameRunning`
> 是一个**恒为 false 的假阴性**——它把 `listGameProcesses()` 返回的 `{known,processes,reason}` 对象
> 当数组用（`obj.length > 0` 恒假），沙箱下 tasklist 又恰好被拒，于是"查不到"被写成了确定的 `false`。
> 已修：现在 `gameRunning` 是三态 `true|false|null`（null=判据不足），并给出 `runningState` /
> `runningEvidence` / 桥接握手证据 / `Get-Process` 兜底枚举。当天实测同一时刻游戏**一直在跑**
> （PID 29908 存活 + 桥接握手成功）。凡引用过 `gameRunning:false` 的结论都要按新输出重判。

---

## 三、已经做完并**验证过**的（直接信，不用重做）

### 3.1 上一轮（自动化/运营线）

| 阶段 | 内容 | 证据 |
| --- | --- | --- |
| S1 | 开局允许所有被禁取物资 | `runs/s1-unforbid/report.md` 14/14 |
| S2 | `INeedDomain` 域框架 + 修"一天跑 900 次决策"去重 bug | `runs/s2b-dedup/report.md` 8/8 |
| S3 | 储藏区两片区（filter 互为补集） | `foodprobe/stockpile_filters` |
| S4a/b/c | 样板→蓝图 / 建筑账本 / 建造前材料评估 | `runs/template-build/report.md` 12/12 |
| S5-a | **5 天生存（和平模式）10/11** | `runs/s5-peaceful/report.md` |
| S5-b | 5 天生存（带袭击）9/14 —— **人死于战斗**，不是饿死 | `docs/s5-第一次真实验收结果.md` |

★ **S5 结论决定了优先级**：运营（食物）这一环**成立**（3 人全活、人人有饭、mood 最低 0.406），
**死因 100% 来自战斗** → 威胁响应是唯一的致命缺口。

### 3.2 最后一个会话做完的（**本轮**）

| 产出 | 文件 | 状态 |
| --- | --- | --- |
| 样板语义层（用途/数量/顺序三层，注册表可扩展） | `src/foodcontrol-core/TemplateSemantics.cs` | ✅ 落地 + 测试 |
| 建造纲要（把"墙→门→床→桌→光→娱乐"变成数据） | `src/foodcontrol-core/BuildOutline.cs` | ✅ |
| 分段材料决策（推翻"整栋一次评估"） | `src/foodcontrol-core/MaterialStages.cs` | ✅ 旧路径**原样保留** |
| 栅格索引去重（三份抄写 → 一份） | `src/foodcontrol-core/GridIndex.cs` | ✅ |
| **AI 托管开关**（三态 + 否决留痕 + 存档） | `src/foodcontrol-game/TakeoverController.cs` | ✅ 编译过，**未实机验** |
| **意图看板**（"即将要做什么"） | `src/foodcontrol-game/IntentBoard.cs` + `AiIntent.cs` | ✅ |
| **「AI 在做什么」窗口 + 主按钮栏按钮** | `src/foodcontrol-game/IntentWindow.cs` + `mods/FoodControl/Defs/MainButtonDefs.xml` | ✅ 编译过，**未实机验** |
| **进程间出口**（日志 + JSONL 文件 + HTTP 预留） | `src/foodcontrol-game/IntentIpc.cs` | ✅ |
| 域 → 框架的意图接口 | `DomainCycleResult.Intent`（`NeedDomain.cs`） | ✅ 框架就绪，**域还没接线** |
| 统一 JSON 原语（DRY） | `src/foodcontrol-game/Json.cs` | ✅ 行为逐字不变 |
| 探针真 bug 修复（解析 `[[x,z],...]` 丢第一对） | `bridge/DSH.FoodProbe/DSH.FoodProbe.cs` | ✅ 配了 13 条离线回归测试 |
| 探针新工具 `foodprobe/takeover` / `foodprobe/intents` | 同上 | ✅ 编译过，**未部署** |
| 游戏层架构审计（20 组重复 / 10 死成员 / 裁剪实验 / 7 步改造） | `docs/审计-游戏层框架与重复.md` | ✅ 只读审计 |
| **ColonyPilot 通用化与改名设计** | `docs/ColonyPilot-通用化与改名-设计.md` | ✅ **本文件的执行依据** |

### 3.3 ★ 两条 IL 确证结论（**别再猜，直接用**）

**① 建造顺序 ≠ 放置顺序**（承重假设被**否定**）：
`JobGiver_Work` →（建造类的 `PotentialWorkThingsGlobal` 基类返回 null、四个 Construct 类都没覆写）
→ `GenClosest.ClosestThingReachable` = **区域 BFS 由近及远 + 区域内欧氏距离最近**，
首个产出候选的区域一出现就立即终止搜索。全程**没有任何地方读放置顺序**。
→ 想控制顺序只能用 **分阶段放 + 等上一段完成**。详见 `docs/建造任务选取顺序-IL确证.md`（443 行 + 60 份证据）。

**② 主按钮栏的 UI 挂点**：
```
MainButtonWorker.get_Visible()  → 非 virtual（可见性只能用 XML 的 buttonVisible 控）
MainButtonWorker.DoButton(Rect) → 非 virtual（覆写它不会被调到！）
MainButtonWorker.Activate()     → abstract；InterfaceTryActivate 末尾 callvirt 它
```
→ **只有 `override Activate()` 是保证会被调到的入口**。

---

## 四、★ 要做的活：ColonyPilot 改名 + 四条泛化轴

**完整方案见 [docs/ColonyPilot-通用化与改名-设计.md](ColonyPilot-通用化与改名-设计.md)**
（含：命名映射表 67 文件 / 443 处、四条泛化轴的接口设计、什么会破什么不会破、
三阶段执行计划、6 条验收问题）。**这里只摘要点与命令。**

### 4.1 名字

用户接受 **ColonyPilot**，也说得通 **Copilot**（"辅助"性质）。
- 推荐 **`ColonyPilot`**：与快照线的 `ColonyRecorder` 同族（都是 Colony*），
  一眼看出是这个项目的自动化线；`Copilot` 在 mod 列表里辨识度太低。
- packageId 建议 `dsh.colonypilot`。

### 4.2 命名映射要点（**不破存档**）

| 现在 | 改成 |
| --- | --- |
| packageId `dsh.foodcontrol` | `dsh.colonypilot` |
| 目录 `src/foodcontrol-core/` `src/foodcontrol-game/` | `src/colonypilot-core/` `src/colonypilot-game/` |
| 目录 `mods/FoodControl/` + 游戏目录里的 **junction** | `mods/ColonyPilot/`（**junction 要重建**） |
| DLL `FoodControl.dll` / `FoodControl.Core.dll` | `ColonyPilot.dll` / `ColonyPilot.Core.dll` |
| 命名空间 `FoodControl.Core` / `FoodControl.Game` | `ColonyPilot.Core` / `ColonyPilot.Game` |
| 类 `FoodControlGameComponent` / `ModBoot` | `PilotGameComponent` / `PilotMod` |
| 探针 `DSH.FoodProbe` / `foodprobe/*`（**50 个工具**） | `DSH.ColonyProbe` / `colonyprobe/*` |
| ★ 探针常量 `MOD_ASM = "FoodControl"` | `"ColonyPilot"` ← **漏了这行 50 个工具全报"mod 未加载"** |
| 数据文件 `foodcontrol-*.jsonl` / `foodcontrol-buildings.json` | `pilot-*`（⚠ 脚本里有硬编码路径） |
| 日志前缀 `[FoodControl]` | `[ColonyPilot]` |

★★ **存档键刻意不改**：`foodControlLastRunDay` / `foodControlSetupDay` /
`foodControlTotalUnforbidden` / `foodControlTakeoverMode`。
`Scribe_Values.Look` 的键是**字符串**，与类名/命名空间/程序集名**无关**
→ **改名不破存档**（开关状态、账本、去重记忆都还在）。

**真的会破的只有三样**（要提前跟用户说）：
1. 老存档的 **mod 列表条目**（packageId 变了）
2. 游戏目录里的 **junction 要重建**（工作区外操作）
3. **已部署的 DLL 文件名**（游戏加载中锁着 → **改名必须在游戏停止时做**）

### 4.3 四条泛化轴（用户**全选**了）

| # | 要做的 | 关键设计决定 |
| --- | --- | --- |
| 1 | **声明式节奏** `TriggerPolicy` | `DayInterval / DayModulo / TimeWindow / EventDriven / Manual`。**`ShouldRunToday` 保留为可覆写、默认实现读 `Trigger`** → 现有四个域**一行不改也能编译**。协调器的"廉价早退"与"实跑"共用同一份判定 → 去重 bug 结构上不可能再出现 |
| 2 | **事件驱动** `DomainEvent` / `IDomainEventSource` | 威胁响应域需要"敌人一出现就动手"。刻意用**显式 `Poll`** 而不是 C# `event`（单线程主循环好诊断，且不怕读档/重载留悬空订阅）。调度：事件域**插队最前**，威胁类事件**否决**低优先级稳态域 |
| 3 | **分域托管** | `ShouldAct(domainId, source)` 的**签名一个字不改**（它早就带 `domainId`），内部换成"全局 + `PerDomain` 覆盖 + `SuspendedUntil`"。另加 `WorkClaim`（只**声明**抢哪类工时，**不做**全局最优分配 —— 那是 P2） |
| 4 | **操作清单一等公民** `IPilotOperation` / `OperationList` | 把 `BootstrapStep` 升上来（开局清单、中期扩张、手动指令**共用同一形状**）。可靠性排序写进代码：**操作清单 > 样板库 > 域**。顺手拆 `DomainContext` → `ColonyContext` + 域专属 payload |

### 4.4 执行顺序（**按撞车风险排**）

```
阶段 1  机械改名（不动任何逻辑、不动存档键；游戏必须停着做）
        验收：build-core 357/357 + build-mod + build-probe 全绿；重启后探针 health 认出 mod
   ↓
阶段 2  触发层 + 事件层（**纯新增**；给 INeedDomain.Trigger 默认实现 →
        现有四域一行不改也能编译；逐域把 ShouldRunToday 换成 Trigger，**一域一提交可单独回滚**）
        验收：三个按天取模的域行为**与改造前逐日完全相同**（这是回归关键）
   ↓
阶段 3  分域托管 + 操作清单 + ColonyContext 拆分 + **第一次做威胁响应域**
```

⚠ **先跟快照线（`src/colonyrecorder/`）协调**：改名会扫到 `src/` 全域，撞车就白做。

### 4.5 ★ 还没接线的那一半（**不依赖改名，可以先做**）

上一轮把「AI 托管开关 / 意图窗口 / 进程间通信」的**框架**做完了，但：

| 缺 | 说明 |
| --- | --- |
| **各域还没填 `Intent`** | 每个域在"我打算动手"时填一次 `DomainCycleResult.Intent`（一句人话 + 原因），框架会自动送进窗口与 JSONL。**加接口就行，不用改流程** |
| **四件事没实机验** | ① 主按钮点得动（左键切托管 / 右键开窗口 / 中键三态）② 窗口画得出来、按钮有效 ③ `foodprobe/intents selftest` 10 条断言全过 ④ **关掉托管后 AI 真的不动手**（最关键：闸门只在 `GameComponentTick` 一处） |
| **探针新工具没部署** | `foodprobe/takeover` / `foodprobe/intents` 编译过了，要 `stop` → `build-probe --deploy` → `start` |
| **Core 那套语义层没接进游戏层** | ①从 Def 真实分类（`thingCategories`/`joyKind`，**不是 Def 名字** —— `ChessTable` 那次就是名字骗人）填语义注册表 ②`BuildOutline` + `MaterialStagedPlanner` 接进 `TemplateBuildDomain` ③`scripts/template-capture.mjs` 用注册表判定角色 |

---

## 五、代码地图

### Core（`src/foodcontrol-core/`，**禁止引用 Verse/RimWorld**，构建脚本会静态检查）

| 文件 | 作用 |
| --- | --- |
| `Models.cs` / `Policy.cs` / `Projection.cs` | 食物预测与决策 |
| `SiteSelection.cs` | 选址通用（距离场等） |
| `StockpilePlanner.cs` | 储藏区选址 + `EdgeDumpPlanner` |
| `BuildSitePlanner.cs` | 建造选址（程序化兜底户型） |
| `BuildTemplate.cs` | 样板数据模型（行字符串 + 角色 + 旋转/镜像 + **校验分级**） |
| `TemplateSemantics.cs` | ★ **语义层**（`SlotKind` / `SlotCountPolicy` / `BuildOrder` / 注册表 / 解析器） |
| `BuildOutline.cs` | ★ **建造纲要**（段排序 + 数量策略落地 `HowManyToPlace`） |
| `MaterialStages.cs` | ★ **分段材料决策**（`StageNeed` / `StagedMaterialPlan`） |
| `MaterialPlanner.cs` | 旧的**整栋一次评估**（**原样保留**，别删 —— 有测试覆盖，换不换是显式决定） |
| `TemplateJson.cs` / `TemplatePlacer.cs` | 样板读写 / 样板→逐格蓝图（含共享墙复用） |
| `GridIndex.cs` | ★ 网格索引的唯一实现（DRY） |
| `BuildingRegistry.cs` | 建筑账本（append-only 意图历史） |
| `tests/CoreTests.cs` | **357 条离线测试** |

### 游戏层（`src/foodcontrol-game/`）

| 文件 | 作用 |
| --- | --- |
| `FoodControlGameComponent.cs` | 主组件 + **10 个域专属静态测试钩子**（审计说这是"加域要改主组件"的根因） |
| `NeedDomain.cs` | `INeedDomain` + `NeedCoordinator`（调度/异常隔离/按天去重/**意图发布**） |
| `AiIntent.cs` / `IntentBoard.cs` / `IntentIpc.cs` / `IntentWindow.cs` | ★ 本轮的意图流（模型/看板/出口/窗口） |
| `TakeoverController.cs` | ★ AI 托管总开关（三态，闸门在 `GameComponentTick` 最前面） |
| `Json.cs` | ★ 唯一的 JSON 输出原语（`Telemetry` 委托给它） |
| `FoodDomain.cs` / `StockpileDomain.cs` / `TemplateBuildDomain.cs` / `ShelterDomain.cs` | 四个域（`ShelterDomain.cs` 约 1623 行，含四域共用的基础设施 —— 审计说**归属放错**了） |
| `BuildingLedger.cs` / `ColonySetup.cs` / `StateReader.cs` / `PlotPlanner.cs` / `Executor.cs` / `Telemetry.cs` / `TemplateLibrary.cs` / `MouseCellOverlay.cs` | 基础设施 |
| `ModBoot.cs` | mod 入口（注册域 + 装意图出口） |

### 样板与资源

```
templates/selftest-box13.json        自测产物（13×13 四门）
templates/bedroom-13x13.json         ★ 用户造的第一份真样板（内含 ChessTable —— 娱乐，不是餐桌）
templates/README.md                  格式说明 + 工作流
mods/FoodControl/Defs/MainButtonDefs.xml   ★ 「AI托管」主按钮
```

### 脚本（常用的）

```bash
node scripts/build-core.mjs            # 编译 + 跑 357 条离线测试（不需要游戏）
node scripts/build-mod.mjs --deploy    # 编译 mod 并拷进 mods/FoodControl/1.6/Assemblies
node scripts/build-probe.mjs --deploy  # 编译探针并拷进游戏 BridgeTools（需要游戏已停）
node scripts/test-probe-parser.mjs     # 探针解析器离线回归测试（13 条）
node scripts/verify-bootstrap.mjs      # 开局清单端到端
node scripts/verify-template-build.mjs # 样板→蓝图端到端
node scripts/verify-s5-peaceful.mjs    # 5 天生存（和平模式）
node scripts/template-capture.mjs <id> --x .. --z .. --w .. --h ..   # 扫样板
node scripts/diag.mjs                  # 卡住时的第一诊断
```

---

## 六、★ 标准操作流程（含"别乱动设置"的落法）

### 6.1 只看代码、不改游戏（最安全，先做这个）

```bash
node scripts/build-core.mjs          # 357/357
node scripts/test-probe-parser.mjs   # 13/13
```

改名/重构**大部分工作可以只在这里验证**（Core 是纯逻辑，离线毫秒级）。

### 6.2 要实机测（用户已同意）

```bash
# ① 停游戏（改名/部署 DLL 必须停）
node src/cli.js stop

# ② 编译 + 部署
node scripts/build-mod.mjs --deploy
node scripts/build-probe.mjs --deploy     # 要测 takeover/intents 才需要

# ③ ★ 备份 ModsConfig 再加 mod（只加不删！）
copy profiles\vanilla\Config\ModsConfig.xml profiles\vanilla\Config\ModsConfig.xml.bak
#    然后在 <activeMods> 里加一行 <li>dsh.foodcontrol</li>（改名后是 dsh.colonypilot）
#    ⚠ 它现在**不在** activeMods 里 —— 不补这行，mod 根本不会被加载

# ④ 起游戏（后台任务托管）+ 开调试殖民地
node src/cli.js start
#    start_debug_game_ready 几乎总会先超时，但殖民地会建成功 → 容忍超时 + 自己轮询
#    readiness 必须查 programState=='Playing' 且 ticksGame>0（只看 mapCount>0 不够）
```

### 6.3 改名时的额外一步（**工作区外操作，先跟用户确认**）

```powershell
# 删掉旧 junction，指向新目录
Remove-Item 'D:\SteamLibrary\steamapps\common\RimWorld\Mods\FoodControl' -Force
New-Item -ItemType Junction -Path 'D:\SteamLibrary\steamapps\common\RimWorld\Mods\ColonyPilot' `
         -Target 'E:\2026.7以后的新工程\DSH\RimWorld_auto_test\mods\ColonyPilot'
```

---

## 七、★ 验证纪律（本项目**多次踩坑换来**，不是可选项）

1. **不信 `success`** —— 桥接返回 `success: true` 只表示调用返回了。
   （实例：`capture_grid` 报成功但把环境杂物当家具槽）
2. **不信 mod 自述** —— mod 说"放下 48 格"不算数，必须用 `foodprobe/list_buildings` **独立数一遍**，两边对账
3. **断言数量/趋势，不是"有没有发生"** —— 曾因此漏掉"一天跑 900 次决策"
4. **用已知答案验证工具** —— 造一个已知形状看扫出来对不对，而不是"跑一遍看有没有报错"
5. **回归测试必须验证"能抓住 bug"** —— 把修复还原，看测试**真的失败**
   （本项目已有 5 个这样的测试，每个都验证过）
6. **改行为前先确认"测试手段真的生效了"** —— 曾因 `forbid_some` 打不到食物，验收"没有证明力"
7. **任何"没做成/读不到"的分支都要能说出为什么**（这条已出现**四次**同类问题）
8. **验收失败不要调参凑数** —— 用户原话：「不要调参数强行通过，要诚实下结论并把它变成下一步需求」

---

## 八、已知坑（**都实测过**，别重踩）

### 8.1 桥接/游戏

| 坑 | 说明 |
| --- | --- |
| **`create_allowed_area` 会挂死桥接** | **不要调用** |
| **`spawn_thing` 生成 pawn 会崩** | 用 `execute_debug_action('Actions\Spawn Pawn...\<kind>')` |
| **`search_debug_actions` 传短语会挂死** | 只用**单个关键词** |
| **`play_for` 会静默跑 0 tick** | 有 `forcePause` 窗口时（实测 `MainTabWindow_Menu`、新殖民地起名框）→ 先 `get_ui_state.windowsForcePause`，必要时 `close_main_tab {tabId:"main-tab:Menu"}` 或 `foodprobe/close_force_pause` |
| **`step_game_ticks` 的 `timeoutMs` 默认 10000** | 必须显式传大值（如 `110000`） |
| **`start` 后台任务显示 completed 是正常的** | launcher 就绪后退出，游戏继续跑 |
| **`start_debug_game_ready` 几乎总会先超时** | 殖民地会建成功 → 容忍超时 + 自己轮询 |
| **readiness 必须查 `programState=='Playing'` 且 `ticksGame>0`** | 只看 `mapCount>0` 不够 |
| **`start_debug_game`（轻量版）只能在主菜单用** | 报 `only supported from the main menu entry scene` |
| **两种推进方式的速度** | `step_game_ticks` ≈ 592 tick/s（精确）；`set_time_speed(Ultrafast,boost)+play_for` ≈ 7100–7800 tick/s（长跑）。**都不能信 `success`**，按 `advancedTicks` 累计 |
| **游戏目录里的 DLL 被加载时锁着** | 部署/改名必须在 `stop` 之后 |
| **从一次性 shell 命令启动游戏会被孤儿化** | 必须后台任务托管 |

### 8.2 mod / 游戏 API

| 坑 | 说明 |
| --- | --- |
| **`ZoneMaker.MakeZoneWithCells` 不注册** | 必须补 `map.zoneManager.RegisterZone(z)`，否则是"僵尸区" |
| **工作优先级** | 桥接读不到（Work 格子 `isChecked` 全 null），但 **mod 自己可以直接读写 `pawn.workSettings`**（`GetPriority`/`SetPriority`/`WorkIsActive`） |
| **`PlaceBlueprintForBuild` 之后游戏自己生成建造工作** | 前提是有人 `WorkIsActive(Construction)` |
| **`SpecialThingFilterDefOf.AllowCorpses` 不存在** | 只有 `AllowCorpsesUnnatural` / `AllowLargeCorpses`；`ThingCategoryDefOf` 没字段 → 按 defName 查 |
| **`TerrainAffordanceDefOf.GrowSoil` 不存在** | 判"能不能种"用**肥力** `map.fertilityGrid.FertilityAt` |
| **`GetStatValue` 是扩展方法，接收者必须是 `Thing` 实例** | 拿 `ThingDef` 调会 CS1929 |
| **`yield return` 不能在带 catch 的 try 里** | CS1626 → 先收集再返回 |
| **`Window` 的 `doWindowBackground`/`draggable`/`forcePause` 是字段不是属性** | 必须在**构造**里赋值；`forcePause` 必须 `false` |
| **`Window` 上已有 `IsOpen`** | 自定义同名成员会 CS0108 且容易调错实现 |
| **`map.uniqueID` 每个新殖民地都是 0** | 换图检测不可靠 → 测试用 `foodprobe/ledger_reset` 显式清 |
| **`findstr` 搜 DLL 是子串匹配** | "命中但类型不存在"踩过多次 → 用 `tools/asmdump`（`AsmDump`/`IlDump`）+ 编译探针收口 |

### 8.3 工程

| 坑 | 说明 |
| --- | --- |
| **PowerShell 读中文会乱码** | 文件本身是好的；用 `read` 工具 |
| **PowerShell 里 `git commit -m "多行中文"` 会被引号拆坏** | 写 `tmp/commit-msg.txt` 再 `git commit -F` |
| **`git add <目录>` 会把另一个进程暂存的 rename 一起带走** | 提交前一定 `git diff --cached --stat` 核对；误加用 `git restore --staged` 撤 |
| **别用 JS 正则改 C# 文件** | 引号会打架且容易替换到错位置 → 用 `edit` 工具 |
| **在类中间插入新类型会破坏括号层次** | 我插 `TemplateValidation` 时多关了一层 → 后面所有成员被挤出类外（CS1519） |
| **中文里夹 ASCII 双引号会拆坏 C# 字符串** | `True("理由说清是"没有"")` 会把字符串切成三段 |
| **手推格数会错** | 我推"墙 18 格"，实际 23 —— **这种数交给脚本数** |
| **测试期望 > 实现时，先怀疑期望** | 先量再改，别改实现去凑期望 |
| **写文件后要回读整体结构** | 只看改的那段，括号/层级问题要到编译才暴露 |

---

## 九、代码规范（用户要求过的，**可执行的自检表**）

| 规约 | 怎么自检 | 反面例子（本项目真出现过） |
| --- | --- | --- |
| **加功能 = 加注册项，不是改流程** | 数"加这一类要动几个文件"。>2 就是设计坏了 | 语义类别原来靠三个 bool，加一类要改判定代码 |
| **撤功能 = 删一个文件，不是改别人** | 问"删掉这个模块，谁被拖累？" | `TemplateBuildDomain` 依赖 `ShelterDomain` 里的 4 样基础设施 |
| **共享设施不属于某个具体功能** | 被 ≥2 个域用到的必须在自己的文件里 | `TryPlacePublic` / `PlacementReport` 藏在住处域里 |
| **同一语义只有一份实现** | 全仓搜关键动词/判据 | 网格索引抄 3 份；食物闭环 2 份；JSON 转义 4 套 |
| **新接口优先于改老行为** | 老路径行为一行不动，新路径并行 + 各自有测试 | `MaterialPlanner` 保留，`MaterialStagedPlanner` 是新增 |
| **判定必须能说出原因** | 任何"没做成/读不到"都要带 `Reason`/`Why` | 探针解析失败还返回 `success:true` |
| **可测的才留下** | 纯逻辑放 Core（离线可测）；碰游戏的放 Game | 分段材料做成纯函数 → 毫秒级测边界 |
| **魔法值要有名字** | 出现第二次就提成常量/类型 | 空地符号 `'\0'`、材质偏好列表 3 份且顺序不一致 |
| **注释与代码不符 = bug** | 改行为时同步改注释 | `ShelterDomain.BuildFloor` 代码是 `false`、注释说 `true` |

---

## 十、待用户拍板的两条（**不要自己改**）

1. `ShelterDomain.BuildFloor` 代码是 `false`，但三处注释说 `true`/`on` —— **漏改还是本意？**
2. `NeedCoordinator._lastRunDay` **不存档**：读档后"今天跑过了"的记忆归零 —— **有意的吗？**

---

## 十一、最好先读的三份文档（按顺序）

1. **本文件**（唯一入口）
2. [docs/ColonyPilot-通用化与改名-设计.md](ColonyPilot-通用化与改名-设计.md) —— **本次要执行的方案**（命名映射 / 四条泛化轴 / 三阶段 / 6 条验收问题）
3. [docs/审计-游戏层框架与重复.md](审计-游戏层框架与重复.md) —— 20 组重复 + 10 死成员 + 裁剪实验 + 7 步改造顺序

补充（按需）：

- [docs/AI托管与意图窗口-设计.md](AI托管与意图窗口-设计.md) —— 上个会话做的框架（结构图 / 扩展点清单 / IL 确证结论）
- [docs/样板语义层-设计与落地.md](样板语义层-设计与落地.md) —— 语义层/分段材料的设计 + 踩坑
- [docs/建造任务选取顺序-IL确证.md](建造任务选取顺序-IL确证.md) —— "放置顺序不决定施工顺序"的完整证据
- [docs/生产自动化-设计.md](生产自动化-设计.md) —— 主设计（含大量 API 确证记录）
- [docs/开局流程-设计.md](开局流程-设计.md) —— `BootstrapStep` 的原始设计（第 4 条泛化轴的起点）
- [docs/游戏API符号确证.md](游戏API符号确证.md) —— **写码前先查**
- `STATUS.md` —— **≤25 行**关键事实与指针（**不记状态**）

★ 文档里有**大量用户原话**（引号里的）。**优先信原话，不要信转述** ——
本项目出现过几次"转述时丢了关键限定"。

---

## 十二、交接时的环境快照（2026-10-04 凌晨）

| 项 | 状态 |
| --- | --- |
| 游戏 | **没在跑** |
| `profiles/vanilla/Config/ModsConfig.xml` | 14 个 activeMods，**不含 `dsh.foodcontrol`**（所以 mod 还没被加载过） |
| 游戏目录 junction | `FoodControl` / `ColonyRecorder` / `DSH_AutoTest`（都指向工作区） |
| 已部署的探针 DLL | **旧版**（新加的 `takeover`/`intents` 没部署） |
| 已部署的 mod DLL | 上个会话编译的版本（含 AI 托管 + 意图框架，**未实机验**） |
| git | 上个会话提交了 6 个 commit（`cf9974a` / `67f3883` / `f9b065c` / `051eef3` + 快照线的 `dc4173d` / `fafc8af`） |
| 另一条线 | **快照线**（`src/colonyrecorder/`，独立 mod `dsh.colonyrecorder`，**只读、只写自己的 jsonl**）。两者的耦合点只有一个：**它只读地图，自动化线会改地图**。⚠ 它也在用同一个游戏实例 → **动手前先协调** |
