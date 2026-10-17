# 自动化测试 · 状态

> 本文从仓库根 `STATUS.md` 拆出（2026-10-17）。**内容原样搬运，未做取舍。**
> 归属：顶层线 —— 用户用自然语言描述一个测试流程 → 翻译成游戏里需要的操作；测试用的机器（启动器、桥接、调试殖民地、UI 驱动、断言）都属于它。
> 上级：（无，顶层） ｜ 交接文件：[../docs/交接-ColonyPilot.md](../docs/交接-ColonyPilot.md)（原文记为该线最新交接文件）

---

## ⚠ 本项目已分成**两条独立的线**（2026-10-03）

用户明确要求分开，**不要混在一起做**：

| 线 | 做什么 | 交接文件 |
| --- | --- | --- |
| **② 自动化（→ ColonyPilot）** | 让 mod 自主运营殖民地；**通用自动化**（不只是食物）；框架可持续接入更多操作 | **★ [../docs/交接-ColonyPilot.md](../docs/交接-ColonyPilot.md)**（**最新，唯一入口**） |

（上面那两行的内容一字未动。）

**三条线的耦合点**：① 只读、② 改地图、③ 只做自己的 mod 与贴图（**与 ① / ② 无代码耦合**；
共用的是测试基础设施，见 [../docs/自动化测试-变化驱动设计.md](../docs/自动化测试-变化驱动设计.md)）。

### 已知问题 / 坑

- **`.dsh/skills/rimworld-auto-test/SKILL.md` 与桥接 schema 有矛盾**：`close_main_tab` 的参数名（`SKILL.md` 仍写 `tabId`，
      在线 schema 是 `mainTabId`；传 `tabId` 会走"无条件关闭任意 tab"的漂移路径）、以及 `pause_game` 能否解除暂停 ——
      **两处都仍未修**（`SKILL.md` 本轮未动）；完整登记见 [../docs/自动化测试-变化驱动设计.md](../docs/自动化测试-变化驱动设计.md) §0.3 与 §10（#15 / #17）。
- **本工程的写入通道曾静默改写竖线字符与行尾**（已登记的失效类型 **S8b** / 规程 **R9**）
      → **改文档后必须跑结构自检**：逐字符遍历 `[char]124` 数表格列（**不要用 `split('|')`**）、核对行数、确认行尾仍是 **LF-only**。

★ **自动化线的最新交接文件是 [../docs/交接-ColonyPilot.md](../docs/交接-ColonyPilot.md)** ——
它取代了旧的 `docs/交接-自动化测试.md`（旧文件保留作为历史，内容已被新文件覆盖并扩展）。
新会话**只需要读新文件**。

**两条线的耦合点只有一个**：快照那条线**只读**，自动化那条线**会改地图**。
所以：

- 做自动化时**不要**碰 `src/colonyrecorder/` / `mods/ColonyRecorder/`
  （**录制器已彻底搬出，无需再删 —— 2026-10-04 核实：自动化源码里零引用**）

★ **两条线都需要先读**：`docs/协作规范.md` + 各自的交接文件。
★ 本文件（STATUS.md）**偏自动化线**；快照线的完整说明在它自己的交接文件里。

⚠ **给自动化线的两条跨线提醒**：
1. `src/prefs.js` 已改（用户 2026-10-04 要求："游戏设置每次启动都被重置"）：
   **只有『窗口/安全』6 个键每次强制**，其余（`langFolderName`/`devMode`/`uiScale`/`volumeMusic`/`pauseOnLoad`…）
   **只在缺失时播种一次**，之后以游戏里存下来的为准。
   ⇒ **若自动化测试依赖英文界面，必须在 `profiles/<x>/Config/Prefs.xml` 里显式把 `langFolderName` 设为 `English`**
   （或删掉该键让 prefs.json 重新播种），否则会继承用户设的中文。
2. `ModsConfig.xml` 我们**从不写**（`src/` 里没有任何写它的代码）—— 用户加的 mod 不会被我们的启动流程重置。

## ★ 一条贯穿全项目的教训：**摘要必须带原因**

`MaxCellsPerCluster` 那个 bug 之所以难查，是因为摘要只说"没有达标的" ——
而 `Score` 在 Area **过大**和**过小**时都返回 `-Infinity`，两种原因长得一模一样。
同理，探针反射读集合拿到 null 时 `success` **还是 true**（看起来成功了其实什么都没读到）。

**同类问题在本项目已出现四次**，所以它是**纪律**不是技巧：
> 任何"没做成"或"读不到"的分支，都必须能说出**为什么**。

对应的做法：
- 摘要里并列**每个簇的实况**（格数/基地距/填充率/分数）+ 上限下限
- 探针**不反射读复杂集合**，让 Core 直接产出 JSON（少一层就能少一类错）

## ★ 代码规范（用户 2026-10-03 要求，写成可执行的规约）

用户原话：「框架能够**不断拓展**，而不是只完成一些小事……**接口要明确**，
考虑裁撤一个功能或添加一个功能，**只需要加接口就行，不需要大改整个代码流程**……
命名也要规范，结构上……**不要犯重复**（DRY）。」

所以下面这些不是"风格偏好"，是**判定标准**：

| 规约 | 怎么自检 | 反面例子（本项目真出现过） |
| --- | --- | --- |
| **加功能 = 加注册项，不是改流程** | 数一下"加这一类要动几个文件"。>2 个地方就是设计坏了 | 语义类别原来靠 `IsWallLike`/`IsDoorLike`/`IsFurniture` 三个 bool，加一类要改判定代码 |
| **撤功能 = 删一个文件，不是改别人** | 问"把这个模块删掉，谁会被拖累？" | `TemplateBuildDomain`（要保留）依赖 `ShelterDomain`（兜底域）里的 4 样基础设施 |
| **共享设施不属于某个具体功能** | 被 ≥2 个域用到的东西，必须在自己的文件里 | `TryPlacePublic` / `PlacementReport` 藏在住处域里 |
| **同一语义只有一份实现** | 全仓搜关键动词/判据；≥2 处就要合并 | 网格索引 `InBounds`+`Index` 抄了 3 份；食物闭环有 2 份 |
| **新接口优先于改老行为** | 老路径的行为**一行不动**，新路径与它并行 + 各自有测试 | `MaterialPlanner`（整栋评估）保留，`MaterialStagedPlanner` 是新增 |
| **判定必须能说出原因** | 任何"没做成/读不到"的分支都要带 `Reason`/`Why` | 探针解析失败时返回 `success:true`（第一对坐标被静默丢掉） |
| **可测的才留下** | 纯逻辑放 Core，离线可测；碰游戏的放 Game | 分段材料决策做成纯函数 → 毫秒级测 20 条边界 |
| **魔法值要有名字** | 出现第二次就提成常量/类型 | 空地符号 `'\0'`、材质偏好列表 3 份且顺序不一致 |
| **注释与代码不符 = bug** | 改行为时同步改注释；发现不一致就当 bug 报 | `BuildFloor` 代码是 `false`、三处注释说 `true` |

**纪律**：动手改代码前先读 [../docs/审计-游戏层框架与重复.md](../docs/审计-游戏层框架与重复.md)，
里面 20 组重复都带**全部出现位置 + 建议的单一归属**。

---

## 已知问题 / 坑（详见 docs/）

| 坑 | 说明 |
| --- | --- |
| **`create_allowed_area` 会挂死桥接** | 不要调用 |
| **`spawn_thing` 生成 pawn 会崩** | 用 `execute_debug_action` |
| **`play_for` 会静默跑 0 tick** | ★ **实测元凶**：`Dialog_NamePlayerFactionAndSettlement`（新殖民地起名框，forcePause=true）。用 `foodprobe/close_force_pause` 通用关闭 |
| **`step_game_ticks` 的 `timeoutMs` 默认 10000** | 必须显式传大值 |
| **`start` 后台任务显示 completed 是正常的** | launcher 等到桥接就绪就退出，游戏继续跑。**别据此以为游戏没起来** |
| **游戏可能"端口在听但 HTTP 超时"** | 主线程卡住。重启实例 |
| **`start_debug_game_ready` 几乎总会先超时** | 但殖民地会建成功 → 容忍超时 + 自己轮询 |
| **readiness 必须查 `programState=='Playing'` 且 `ticksGame>0`** | 只看 `mapCount>0` 不够 |
| **`forbid_some` 打不到食物上** | 实测改 8 个物品但 `forbiddenFoodNutrition` 仍是 0 → 用 `forbid_food`（只动 `category==Item && IsNutritionGivingIngestible`） |
| **减库存的测试手段必须先验证"真的减了"** | 否则验收变成"验证了一个不吃紧的场景"，看起来通过其实什么都没测 |
| **PowerShell `Get-Content` 读中文会乱码** | 文件本身是好的；用 `read` 工具 |
| **PowerShell 里 `git commit -m "多行中文"` 会被引号拆坏** | 写进 `tmp/commit-msg.txt` 用 `git commit -F` |
| **别用 JS 正则改 C# 文件** | 引号会打架，且容易替换到错位置。用 `edit` 工具 |
| **探针反射读复杂集合会拿到 null 而 `success` 仍为 true** | 让 Core 直接产出 JSON |
| **`map.uniqueID` 每个新殖民地都是 0；世界种子也会复用** | 换图检测不可靠 → 测试用 `foodprobe/ledger_reset` 显式清 |
| **测试断言不能只断言"有没有发生"** | 曾因此漏掉"一天跑 900 次决策"。要断言**数量/趋势** |
| **测"某功能"时被它自己的守卫拦住** | debug 殖民地自带墙/蓝图。测建造用 force，测守卫用非 force |

## 桥接与测试基础设施（前序会话成果）

- [x] GABP v1 客户端（零依赖 `src/gabp.js`）；隔离启动器（`-savedatafolder` + 独立 Prefs）
- [x] 无管道子进程执行（沙箱下必须，`src/proc.js`）
- [x] 冒烟测试闭环（`node src/cli.js smoke`，9/9）
- [x] 多显示器：`prefs.json` 的 `monitor`（1=主屏，2=副屏）
- [x] `dsh-rimworld` Cordis 插件（7 个工具，**需重启 DSH 生效**）
- [x] 两个 skill：`rimworld-auto-test`、`rimworld-mod-compat`（已装全局）
- [x] debug action 树完整爬取（341 顶层 / 约 4 万节点）
- [x] Character Editor 两条驱动路径（UI + 自写伴生 DLL 反射）
- [x] Hot reload Defs 实测亚秒生效
- [x] 鼠族（NewRatkinPlus + HAR）安装验证；`modsconfig.mjs preset`

## 最近一次验证

- 桥接工具数：**195+**
- 游戏实例：副屏运行中（`node src/cli.js start`；⚠ 后台任务显示 completed 是正常的）
