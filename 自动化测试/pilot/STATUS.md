# pilot（ColonyPilot）· 状态

> ⚠️ **本文件里提到的 `scripts\*.mjs` 命令已在 2026-10-17 被用户全部删除。**
> 那些命令是**历史记录**，现在跑不了；需要时按标准重新写。**原文未改。**
> 本文从仓库根 `STATUS.md` 拆出（2026-10-17）。**内容原样搬运，未做取舍。**
> 归属：自动化测试的**特化版** —— 加一个上层让它**自己决策**，目标是**把游戏玩好**。
> 上级：[../STATUS.md](../STATUS.md)（自动化测试线） ｜ 交接文件：[../../docs/交接-ColonyPilot.md](../../docs/交接-ColonyPilot.md)

---

## 当前在做什么

### ★★ 定位澄清（2026-10-04 用户）：这是一个**通用殖民地自动化** mod

> 「你不要被现在这个 mod 的名字**误导**了。我本来的打算就是要做一个**几乎能够覆盖所有方面**、
>   至少我们能覆盖的都给它覆盖到的**那种自动化操作的 mod**，而不只是和 food 相关的。
>   那么在考虑的时候就要考虑**之后会接入更多的操作**。」

- **目标名：`ColonyPilot`**（用户 2026-10-04 选定）。食物只是它现在会开的**第一条线**。
- 设计（**不动代码**）：[../../docs/ColonyPilot-通用化与改名-设计.md](../../docs/ColonyPilot-通用化与改名-设计.md)
  —— 含命名映射表（67 文件 / 443 处）、**四条泛化轴**（声明式节奏 / 事件驱动 / 分域托管 / 操作清单）、
  「什么会破、什么不会破」、三阶段执行计划、6 条验收问题。
- ★ 关键结论：**存档键与类名/程序集名无关** → 改名**不破存档**；
  真正会破的只有「老存档的 mod 列表条目」+「游戏目录里那个 junction」+「已部署的 DLL 文件名」。
- ⚠ 执行**分三阶段**，现在一步都不做：另一个进程（快照线）正在改 `src/` 全域 → **会撞车**。

### ✅ 录制器已彻底搬出（2026-10-04 核实，无需再删）

用户要求「和录制器/抓快照相关的都删掉」。核实结果：**已经是干净的**（快照线在 21:5x 搬完了，
并已提交 `dc4173d`）：`src/foodcontrol-game/` 与 `src/foodcontrol-core/` 里**零处**引用
`Snapshot*`/`Recorder*`（只剩 `FoodStateSnapshot` —— 食物域自己的状态模型，与殖民地快照无关）；
已编译的 FoodControl.dll 里没有快照类型；快照代码现在在独立位置
`src/colonyrecorder/` + `mods/ColonyRecorder/`（自己的程序集 `dsh.colonyrecorder`、不依赖 FoodControl.Core）。
→ **没有残留可删**，再删就会删到另一个进程正在做的活。

### ★ 本轮（2026-10-03 晚）：代码规范 / 框架可扩展性 + 建造顺序确证

用户要求：「我希望这个框架能够**不断拓展**……**接口要明确**，考虑裁撤或添加一个功能，
**只需要加接口就行，不需要大改整个代码流程**……**不要犯重复**（DRY）。」
同时明确：**这一轮不要部署**（另一个进程在用游戏做快照捕捉）。

| 产出 | 文件 | 状态 |
| --- | --- | --- |
| **★ 建造顺序 ≠ 放置顺序**（IL 确证，**承重假设被否定**） | [../../docs/建造任务选取顺序-IL确证.md](../../docs/建造任务选取顺序-IL确证.md) | ✅ 443 行 + 60 份原始证据 |
| **样板语义层**（用途/数量/顺序三层，注册表可扩展） | [../../src/foodcontrol-core/TemplateSemantics.cs](../../src/foodcontrol-core/TemplateSemantics.cs) | ✅ 落地 + 测试 |
| **建造纲要**（把"墙→门→床→桌→光→娱乐"变成数据） | [../../src/foodcontrol-core/BuildOutline.cs](../../src/foodcontrol-core/BuildOutline.cs) | ✅ 落地 + 测试 |
| **分段材料决策**（推翻"整栋一次评估"） | [../../src/foodcontrol-core/MaterialStages.cs](../../src/foodcontrol-core/MaterialStages.cs) | ✅ 落地 + 测试（旧路径原样保留） |
| **栅格索引去重**（三份抄写 → 一份 `GridIndex`） | [../../src/foodcontrol-core/GridIndex.cs](../../src/foodcontrol-core/GridIndex.cs) | ✅ 271 条测试全绿 |
| **游戏层架构审计**（20 组重复 / 10 个死成员 / 裁剪实验 / 7 步改造） | [../../docs/审计-游戏层框架与重复.md](../../docs/审计-游戏层框架与重复.md) | ✅ 只读审计 |
| **探针真 bug 修复 + 离线回归测试** | [../../scripts/test-probe-parser.mjs](../../scripts/test-probe-parser.mjs) | ✅ 13/13，且验证过"抓得住 bug" |
| 设计与踩坑记录 | [../../docs/样板语义层-设计与落地.md](../../docs/样板语义层-设计与落地.md) | ✅ |

### ★ 本轮第二件：AI 托管开关 + 意图窗口 + 进程间通信（**框架，未部署**）

用户要求：「局内做一个**是否让 AI 托管的小按钮**」「局内放一个窗口，把 **AI 打算做什么**
打印出来，让人知道 AI 要做什么或**即将要做什么**」「也可以做**进程间通信**把内容发过去；
**落点之后再看**，但结构要先定清楚」。

| 产出 | 文件 | 说明 |
| --- | --- | --- |
| 意图模型（有生命周期：打算/正在/完成/失败/**被否决**） | [AiIntent.cs](../../src/foodcontrol-game/AiIntent.cs) | "即将要做"靠 `Planned` 这一栏 |
| 意图看板（单一事实来源） | [IntentBoard.cs](../../src/foodcontrol-game/IntentBoard.cs) | 窗口/文件/探针读的是**同一份** |
| 托管总开关（三态 + 否决留痕 + 存档） | [TakeoverController.cs](../../src/foodcontrol-game/TakeoverController.cs) | 闸门在 `GameComponentTick` **最前面** |
| 局内按钮 + 「AI 在做什么」窗口 | [IntentWindow.cs](../../src/foodcontrol-game/IntentWindow.cs) + [Defs/MainButtonDefs.xml](../../mods/FoodControl/Defs/MainButtonDefs.xml) | 左键切托管 / 右键开窗口 / 中键三态 |
| 进程间出口（日志 + **JSONL 文件** + HTTP 预留） | [IntentIpc.cs](../../src/foodcontrol-game/IntentIpc.cs) | `foodcontrol-intents.jsonl`，追加写、可 tail |
| 域 → 框架的意图接口 | `DomainCycleResult.Intent`（[NeedDomain.cs](../../src/foodcontrol-game/NeedDomain.cs)） | ★ 域只填一个字段，其余全在框架 |
| JSON 原语收口（DRY） | [Json.cs](../../src/foodcontrol-game/Json.cs) | Telemetry 的私有实现提成公开，**行为逐字不变** |
| 探针：外部可调可读的那一半 | `foodprobe/takeover` / `foodprobe/intents` | `intents selftest` 有 10 条断言 |
| 设计文档 | [../../docs/AI托管与意图窗口-设计.md](../../docs/AI托管与意图窗口-设计.md) | 含 IL 确证的 UI 挂点结论 |

**★ 本轮的第二条 IL 确证结论（写码前别猜）**：
```
MainButtonWorker.get_Visible()  → 非 virtual（可见性只能用 XML 的 buttonVisible 控）
MainButtonWorker.DoButton(Rect) → 非 virtual（覆写它不会被调到！）
MainButtonWorker.Activate()     → abstract；InterfaceTryActivate 末尾 callvirt 它
```
→ **只有 `override Activate()` 是保证会被调到的入口**。

#### 扩展点（"加东西"要动哪里）

| 想加 | 动几处 |
| --- | --- |
| 一条新的"AI 要做的事" | **1**（域里填 `Intent`） |
| 一类新动作大类 | **1**（`IntentKind` 加枚举值） |
| 一个新出口（HTTP/管道） | **1**（实现 `IIntentSink` + `AddSink`） |
| 分域托管 | **1**（`TakeoverController` 加集合，调用方签名不变） |
| 新的 UI 展示 | **0**（读 `IntentBoard.LinesForUi()`） |

**Core 测试：271 → 357（全绿）**。本轮**没有部署**，游戏层接线是下一步。

#### ★ 一句话结论：**不能靠"按期望顺序放蓝图"控制施工顺序**

IL 确证机制：`JobGiver_Work` →（建造类 `PotentialWorkThingsGlobal` 基类返回 null、
四个 Construct 类都没覆写）→ `GenClosest.ClosestThingReachable`
= **区域 BFS 由近及远 + 区域内欧氏距离最近**，首个产出候选的区域一出现就立即终止搜索。
全程没有任何地方读放置顺序。
→ 可行替代：**A 分阶段放 + 等上一段完成**（推荐，零侵入）／B 强制点单／C 自定义 WorkGiver。
（本设计的分段顺序与方案 A 天然契合。）

## 当前在做什么（路线）

**路线已调整为「样板驱动」**（用户 2026-10-03 决定）：不靠程序化生成户型，
而是**把用户手造的样板房编码下来，需要时直接抄**。

| 模块 | 状态 | 证据 |
| --- | --- | --- |
| S1 开局允许所有物资 | ✅ 14/14 | `runs/s1-unforbid/report.md` |
| S2 域框架 `INeedDomain` | ✅ 含去重 bug 修复 8/8 | `runs/s2b-dedup/report.md` |
| S3 储藏区（**两片区 + filter 互补集**） | ✅ **实测通过** | `foodprobe/stockpile_filters` |
| S4 住处：**样板 → 蓝图** | ✅ **端到端 12/12** | `runs/template-build/report.md` |
| S4 住处：程序化兜底 | ✅ 编译通过（样板优先，它兜底） | — |
| 样板捕捉工具 | ✅ 10/10 | `runs/template-capture-selftest/report.md` |
| **开局清单（三步全通）** | ✅ **14/14** | `runs/bootstrap/report.md` |
| **建筑账本（记我们造过什么）** | ✅ **游戏内实测** | `foodprobe/ledger` |
| **建造前材料评估** | ✅ **实测"木不够转钢"** | `docs/建造前材料评估-设计.md` |
| 共享墙复用 | ✅ Core 逻辑 + 23 测试 | `docs/样板拼接-共享墙设计.md` |
| 贴靠选址（缺口 A） | ⬜ **未做**（账本已就绪，是它的前置） | — |
| **S5 5 天生存（和平模式）** | ✅ **10/11 —— 运营成立** | `runs/s5-peaceful/report.md` |
| S5 5 天生存（带袭击） | ❌ 9/14 —— 人死于战斗 | `docs/s5-第一次真实验收结果.md` |
| Core 离线测试 | ✅ **357/357** | `node scripts/build-core.mjs` |

**四个域全部注册并跑通**：`food` / `stockpile` / `templatebuild`（样板优先）/ `shelter`（程序化兜底）。

---

## ★ S5 结论：**运营这一环成立，死因 100% 来自战斗**

用户要求：「创造一种环境**永远不触发袭击和动物发疯事件**，我想要看**纯粹的运营表现**。」

办法找到了：`foodprobe/peaceful` 把 **34 个威胁类 `IncidentDef` 的 `baseChance` 设 0**
（判定用「分类名 + defName 关键词」双条件，并排除友方/疾病）。
含元凶 `AnimalInsanitySingle`（就是"Chaz has been torn to death"那个）。

### 和平模式实测（3 人撑过 5 游戏天）

| 判据 | 结果 |
| --- | --- |
| ① 没人死 | ✅ 3 → 3（Hummell, Sophie, Ulana 全程存活） |
| ② 人人有饭吃 | ✅ 最低 food **0.118** |
| ③ 没人精神崩溃 | ✅ 从没出现 `MentalState` |
| ③b 心情没触底 | ✅ 最低 mood **0.406** |
| ④ 没断粮 | ✅ 最低存量 **5.4** |
| ⑤ 没人倒地 | ✅ |
| 推进 5 天 | ✅ **300415 tick** |

### ★ 最硬的经济学证据

```
粮食轨迹  51.3 → 47.7 → 44.1 → 37.8 → 34.2 → 27.9 → 19.8 → 9.9 → 5.4
5 天净消耗 45.9
3 人 × 4.8/天 × 5 天 = 应消耗 72
→ **mod 在这 5 天里自己生产了约 26 点营养（≈17 天口粮）**
```

结束时：可撑 **1.12 天** / `CultivatedCells=51` / `GrowingZoneCount=1`
→ **mod 把粮食稳定维持在"略高于饿死线"上，而一个人都没饿着。**

### 同一套 mod，带袭击 vs 不带袭击

| | 带袭击（第一次） | 和平模式 |
| --- | --- | --- |
| 存活 | 3 → **1** | 3 → **3** ✅ |
| 最低 mood | **0.012** | **0.406** |
| 死因 | 野兽撕碎 / 失血 | — |

→ **结论：运营（食物）这一环是成立的；死因 100% 来自战斗。**
  两者必须分开评价 —— 用户的判断是对的。

## ★ 建筑账本（本轮新增，用户提的基础设施）

用户原话：
> 「我们要维护一个我们目前为止建造过的**所有建筑的大致的样貌**，就是你要有它的
>  **全局数据**，就是**剥离开地图原有的元素**。我们要能够在任何时候想知道的时候
>  都能知道**我们到底造了些啥**。」

**实测证据**（`foodprobe/ledger`）：

```json
{"id":"b1-...","templateId":"selftest-box13","originX":86,"originZ":97,
 "width":13,"height":13,"cells":48,"alive":48}
```

★ 我核对了门的坐标 `[92,97] [86,103] [98,103] [92,109]` —— 原点 (86,97) + 尺寸 13×13
→ `92=86+6`、`103=97+6` 正好是**四条边的中点**，与样板 4 个门槽完全吻合。
**账本记的是真实几何。**

### 关键语义：**意图历史**，不是当前状态

账本记的是「我们**曾经下了什么蓝图**」，被烧/被拆的格子**只标 `StillThere=false`，不删除**。
理由（用户原话）：「因为不可抗力……我们没法跟踪。但至少我们可以知道我们造了一些什么。」
→ 所以本类**只有 `MarkMissing`，没有删除入口**。

### 顺手修掉一个一直在干扰测试的真 bug

`ShelterDomain.HasAnyShelter` 第一行是 `CountBlueprintBuilds(map) > 0` ——
而 **debug 开局自带 32 个蓝图 + 662 面墙**，所以守卫一上来就判定"已有住处"，
**正常路径永远拒绝动手**（这正是上一轮"验证被守卫拦住"的根因）。

改用账本后实测对比：

```
修前（非强制）  已有住处/蓝图，不重复盖
修后（非强制）  样板 selftest-box13 @(119,119)：计划 48 格 → 放下 48 格（记账 48）
```

## ★ 开局清单：三步全通（用户提的方向，已可执行）

用户原话：
> 「至少**开局一定要做的事情是可以完全被规定出来的**，
>  那只要做到这些事情，基本上**开局就稳了**。」

`node scripts/verify-bootstrap.mjs`（**14/14**）就是这份"规定"的可执行版本：

| 步骤 | 结果 | 实测证据 |
| --- | --- | --- |
| ① 允许所有物资被拾取 | ✅ | 禁取 0 → 0 |
| ② 划两片储藏区 | ✅ | 区#0 400格 生活区 `wood=true corpse=false`；区#1 100格 丢弃区 `wood=false corpse=true` |
| ③ 从样板盖出住处 | ✅ | 蓝图 38 → 86，**+48 = 44墙+4门**，门 +4 |

设计见 [../../docs/开局流程-设计.md](../../docs/开局流程-设计.md)。
三层可靠性：**开局清单 > 样板库 > 域**（能用左边解决的，就不要动右边）。

## ★ 样板系统（用户新增的核心方向）

```
用户手造样板房 → capture_grid 扫描 → 编码成可读 JSON → mod 直接抄
```

设计见 [../../docs/蓝图模板系统-设计.md](../../docs/蓝图模板系统-设计.md)。

- `templates/*.json`：**行字符串**表示形状（人能一眼看懂、能手画、diff 可读）
- `roles[sym].accepts`：「**不必百分百还原，只要物块对得上**」的落点
- **`accepts` 是建筑 Def，材质由库存另挑**
- 已有样板：`templates/selftest-box13.json`（13×13 四门）
- 端到端实测 **12/12**：计划 48 格 → 放下 48 格 → 桥接独立回读 **+48**（44墙+4门）

### 共享墙复用（已实现 Core 逻辑，23 条测试）

用户 2026-10-03：「两个房子连在一起，中间的墙是可以复用的……少一面墙，也更像一个整体。」

- `wall↔wall` ✅ / `door↔door` ✅ / **`wall↔door` ✗**（否则墙上多个洞，房子不封闭）
- **框架不算可复用**（框架可能被取消 → 那面墙永远建不成）
- ⚠ **缺口 A「贴靠选址」未做** —— 现在 `FindBestSite` 只找空地，
  所以两个房子根本不会挨着。**账本已就绪，是贴靠选址的前置。**
- 见 [../../docs/样板拼接-共享墙设计.md](../../docs/样板拼接-共享墙设计.md)

## ★ 修掉的根因累计（每个都配了**能抓住它的回归测试**）

1. **坐标系混用** —— `Plan` 把模板局部坐标当地图绝对坐标查网格 → 48 槽位全判"放不下"
2. **建筑 Def 与建造材料混淆** —— 拿 `"Wall"` 查库存（Wall 是木头/钢建的，不是物品）→ 全判"没货"
3. **`MaxCellsPerCluster` 只当否决条件** —— K-means 出 3 个 8400 格的簇全被否 → 一个区都建不出来
4. **守卫被 debug 开局骗到** —— `CountBlueprintBuilds` 数到了开局自带的蓝图

## 用户已澄清 / 已定的参数

### 澄清
1. **地板**：默认**完全不铺**。扫描器也不会扫它（地板是 `TerrainDef`）。
   将来按"资源富余 + 心情需要"在卧室铺 —— 那属于心情域。
2. **工具优先级**：先要一个**能用的**（方案 A，已有）；持久局内动作（方案 B）以后再说。
3. **建筑账本**：维护"我们造过什么"的全局数据，**剥离开地图原有元素**。

### 参数
| 参数 | 值 |
| --- | --- |
| 目标带（食物） | 常备 **10** 天，低于 **7** 补，高于 **20** 停 |
| 狩猎 | 不进 MVP |
| 作物 | 固定水稻（`growDays=3`、`harvestYield=6`、`RawRice` 营养 0.05） |
| 住房尺寸 | **13×13**，四边中心各一扇门（共 4） |
| 地板 | **默认完全不铺** |
| 材质 | **有什么用什么，可混用** |
| 储藏区 | **两片**：生活区（近基地，禁尸体垃圾）+ 边缘丢弃区（10×10 方框） |

## 待办（按优先级）

### A. 代码规范 / 框架（用户本轮要求，**不部署**）

1. **★ ColonyPilot 改名 + 通用化（三阶段，见
   [设计文档](../../docs/ColonyPilot-通用化与改名-设计.md)）** ——
   ⚠ **必须等快照线告一段落**（改名会扫到 `src/` 全域，现在做会撞车）：
   - **阶段 1**：机械改名（67 文件 / 443 处；**不动存档键**；**游戏必须停着做**，junction 要重建）
   - **阶段 2**：`TriggerPolicy` 声明式节奏 + `DomainEvent`/`IDomainEventSource` 事件驱动
     （**纯新增**，给 `INeedDomain.Trigger` 默认实现 → 现有四个域一行不改也能编译）
   - **阶段 3**：`TakeoverController` 分域托管 + `IPilotOperation` 操作清单一等公民 + `ColonyContext` 拆分
2. **接线：把各域填上 `Intent`**（开关/窗口/文件框架已就绪，缺这一步）。
   每个域在"我打算动手"时填一次 `DomainCycleResult.Intent`（一句人话 + 原因），
   框架会自动送进窗口与 JSONL。**加接口就行，不用改流程**。
3. **重启后实机验证这四件事**（已编译通过，但没跑过）：
   ① 主按钮「AI托管」点得动、左键切开关、右键开窗口、中键三态
   ② 窗口画得出来、能滚动、按钮有效
   ③ `foodprobe/intents selftest` 10 条断言全过
   ④ **关掉托管后 AI 真的不动手**（这条最关键：闸门只在 `GameComponentTick` 一处）
4. **游戏层接线（Core 已就绪）**：
   （a）**从 Def 真实分类填语义注册表**（`thingCategories` / `joyKind`，
   而不是 Def 名字 —— `ChessTable` 那次就是名字骗人）
   （b）把 `BuildOutline` + `MaterialStagedPlanner` 接进 `TemplateBuildDomain`
   （c）扫 `scripts/template-capture.mjs` 用注册表判定角色
5. **按审计报告改造游戏层**（7 步，按收益/风险排序）——
   [../../docs/审计-游戏层框架与重复.md](../../docs/审计-游戏层框架与重复.md)。
   最高价值的一条：**把基点从「主组件」搬到「协调器」**（现在加/撤一个域要改主组件 + 探针）。
   最低风险的一条：**删 10 个死成员**（含接口成员 `IsSurvivalCritical`，4 个域都实现了但零消费者）。
   已顺手完成其中一步（统一 JSON 出口，见 `Json.cs`）。
6. **两处需要用户确认**（审计发现，我不改）：
   - `ShelterDomain.BuildFloor` 代码是 `false`，但三处注释说 `true`/`on` —— 是漏改还是本意？
   - `NeedCoordinator._lastRunDay` **不存档**：读档后"今天跑过了"的记忆归零，是有意的吗？

### B. 运营 / 生存（原优先级）

4. **威胁响应域** —— S5 实测证明这是**唯一的致命缺口**。
   最低限度：敌意目标出现时**让小人躲进最近的封闭空间并关门**。
   技术基础已有：住处域建的房子 = 封闭空间，账本知道它在哪。
5. **心情干预** —— mood 掉到 0.012 时 mod 什么都不做。
   接用户提过的"地板与心情管理挂钩"（卧室铺地板提升美观）。
6. **重跑 S5 作回归** —— 这次要**同时**看"有饭吃"和"活着"。
7. **贴靠选址（缺口 A）** —— 账本已就绪，是它的前置。
8. **收用户的样板房**（卧室/仓库/厨房），存进 `templates/`。
9. **分阶段放 + 等上一段完成** —— 这是"控制建造顺序"的**唯一可靠手段**
   （放置顺序不起作用，已 IL 确证）。
10. 样板旋转/镜像接入放置路径；家具槽位实测。

## 已知问题 / 坑（详见 docs/）

| 坑 | 说明 |
| --- | --- |
| **`SpecialThingFilterDefOf.AllowCorpses` 不存在** | 只有 `AllowCorpsesUnnatural`/`AllowLargeCorpses`；`ThingCategoryDefOf` 没有字段 → 按 defName 查 |
| **`yield return` 不能在带 catch 的 try 里** | CS1626 → 先收集再返回 |
| **`ZoneMaker.MakeZoneWithCells` 不注册** | 必须补 `RegisterZone`，否则是"僵尸区" |
| **`GetStatValue` 是扩展方法，接收者必须是 `Thing` 实例** | 拿 `ThingDef` 调报 CS1929 |
| **材料成本未知时按 1 算，绝不按 0** | 按 0 → 需要 0 → 永远"够" → 掩盖缺口 → 又变成"不够也硬造" |

## 最近一次验证

| 验证 | 结果 |
| --- | --- |
| **S5 五天生存在（和平模式）** | ✅ **10/11 —— 3 人全活、有饭吃、心情稳（最低 mood 0.406）** |
| S5 五天生存在（带袭击） | ❌ 9/14 —— 人死于战斗（非饥饿） |
| **建筑账本** | ✅ 游戏内实测（48 格、门坐标吻合） |
| **守卫修复** | ✅ 非强制模式真的建造了（修前被拦） |
| **建造前材料评估** | ✅ 实测"木头不够转钢" |
| **开局清单端到端** | ✅ **14/14**（`node scripts/verify-bootstrap.mjs`） |
| **样板 → 蓝图端到端** | ✅ **12/12**（`node scripts/verify-template-build.mjs`） |
| **储藏区两片区 filter** | ✅ 生活区 `corpse=false` / 丢弃区 `corpse=true` |
| **样板捕捉链路** | ✅ 10/10 |
| **Core 离线测试** | ✅ **357/357**（毫秒级） |
| S1 / S2 去重 / 地基 | ✅ 14/14 / 8/8 / 14/14 |

- 代码规模：Core 5.5k 行 / Game 7.5k 行 / 探针 3.8k 行

- ★ **和平模式**：`foodprobe/peaceful`（关 34 个威胁事件，`enable=false` 可恢复）；
  查看器 `node scripts/show-peaceful.mjs`
