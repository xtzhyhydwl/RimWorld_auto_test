# 状态

> 这是给下一个会话看的接力文件。每完成一个阶段就更新它。
> 规则见 [docs/协作规范.md](docs/协作规范.md)。

## 当前在做什么

**把 FoodControl 从「只自动种食物」扩展到「能运营前期殖民地」。**

进度：**S1 完成**（开局允许所有被禁取物资，14/14 实测通过）→ 下一步 S2 抽 `INeedDomain` 框架。

| 阶段 | 内容 | 状态 |
| --- | --- | --- |
| S1 | 开局允许所有被禁取物资 | ✅ **完成** 14/14（`runs/s1-unforbid/report.md`） |
| S2 | 抽 `INeedDomain` 框架（只搬食物，零行为变化） | ✅ **完成**（+ 修掉自己引入的去重 bug，8/8：`runs/s2b-dedup/report.md`） |
| S3 | 储藏区 domain（复用 `SiteSelection`） | ✅ **完成** 13/13（`runs/s3-stockpile/report.md`） |
| S4 | 住处 domain（方块房：选址 → 选建材 → 放蓝图） | ⬜ 下一步 |
| S5 | 5 天生存验收（3 人全有饭吃 + 心情不崩） | ⬜ |

### ★ S2 期间自己引入又自己抓到的一个 bug（留档）

重构 `NeedCoordinator` 时我**删掉了"按天去重"的检查**，只留"执行后记账"，
结果**一天跑了约 900 次决策**（日志实证：tick 间隔 64、内容完全相同）。

极具欺骗性：**日志里"有决策"看起来是对的** —— 我最初就是看到日志有正常输出才以为没事。
教训：**验证不能只看"有没有发生"，要看"发生了几次"**。
回禁用 `scripts/verify-s2b-dedup.mjs` 兜住（断言数量而非有无）。
详情见 [docs/foodcontrol-架构与流程.md](docs/foodcontrol-架构与流程.md) §四。

设计文档：[docs/生产自动化-设计.md](docs/生产自动化-设计.md)
扩展需求：[docs/生产自动化-扩展需求.md](docs/生产自动化-扩展需求.md)
架构与流程：[docs/foodcontrol-架构与流程.md](docs/foodcontrol-架构与流程.md)

### ★ S1 踩到的一个重要坑（值得记住）

最初把"允许所有物资"实现成**一次性初始化**（一个 `_done` 标记）。
实测发现它**在地图刚生成、debug 开局的物资还没刷出来时就跑了** —— 扫到 0 个禁取物，
然后标记"已完成"，**此后 49 个被禁取物品永远没人解锁**。日志证据：

```
[FoodControl] 一次性初始化：扫描 0 / 解锁 0 / 剩余 0 | 没有需要解锁的东西
```

**教训：「只做一次」这个前提本身就是错的。** 游戏里随时可能出现新的禁取物
（空投、商队、新生成的东西）。改成**跟着日闭环每天检查一次**（幂等，开销可忽略）。
修复后实测：12 个禁取物 → 推一天 → 1 个 → 再推一天 → **0**（持续收敛）。

### 用户已定的参数

| 参数 | 值 |
| --- | --- |
| 目标带 | 常备 **10** 天，低于 **7** 补，高于 **20** 停 |
| 狩猎 | **不进 MVP** |
| 作物 | **固定水稻**（实测 `growDays = 3`，`harvestYield=6`，`RawRice` 营养 0.05） |
| 基地中心 | Home 质心 → 最近的贮藏区 → 地图中心（兜底链） |
| 距离惩罚 | 可接受 **40** 格，超出每格罚 **1** 分 |
| 已有种植区 | **优先复用空地**，不新开 |

---

## 已完成

### S1 · 开局允许所有被禁取物资（2026-10-03）
- [x] `src/foodcontrol-game/ColonySetup.cs`：`unforbid_all` 的 mod 内实现
      - 用 `CompForbiddable`（不是废弃的 `IsForbidden` 字段）
      - **回读校验**：改完再数一遍，把"还剩几个"记进结果（不假定自己成功了）
      - 幂等 + 按游戏日去重
- [x] 挂到 `GameComponentTick` 的日循环（不在决策时段内，因为家务活没理由等到 8 点）
- [x] `CountBuildingMaterials`：用游戏的 `IsStuff` 机制识别建材，**不硬编码 defName**
      → 这是 S4「按现有资源选材质」的输入。实测：`WoodLog 300 / Steel 1170 / ComponentIndustrial 30`
- [x] 探针新增：`forbid_some`（造夹具）、`run_setup`、`reset_setup`、`materials`
- [x] 验证脚本 `scripts/verify-s1-unforbid.mjs`：**14/14 通过**
      - 关键：用 `forbid_some` **造夹具**，否则会"验证一个空场景"（debug 开局时有时无禁取物）
      - 关键：**桥接独立回读**（8 → 0），不信 mod 自述

### 前序（已完成）

**Core 逻辑层**：`src/foodcontrol-core/`，**65/65 离线测试**（`node scripts/build-core.mjs`，毫秒级）
**地基验证**：`runs/foundation/report.md`，**14/14**（建区+注册+设作物+小人真去种）
**Game 层**：`src/foodcontrol-game/`，编译通过

### 生产自动化 · 设计与侦察（2026-10-03）
- [x] 需求讨论 + 设计文档：[docs/生产自动化-设计.md](docs/生产自动化-设计.md)
- [x] 技术侦察（20 项实测）：[docs/食物自动化-技术侦察.md](docs/食物自动化-技术侦察.md)
      + `runs/food-recon/summary.json`
- [x] **关键结论**：桥接**读不到** nutrition / stackCount / ThingCategory，
      **工作优先级也读不到** → 感知层与 P2 都必须写伴生 DLL
- [x] 执行层好消息：`apply_architect_designator` 能用代码建种植区（只收 Gravel/Soil）；
      狩猎可完全驱动；但**改作物只能走 UI**
- [x] 时间实测 + 一个通用陷阱：[docs/时间推进实测.md](docs/时间推进实测.md)
      - `play_for(Ultrafast)` ≈ **7110 tick/s** vs `step_game_ticks` ≈ **592**（快 12 倍）
      - ⚠ **forcePause 窗口会让 `play_for` 静默跑 0 tick**（实测 `MainTabWindow_Menu`）
- [x] 上述两条通用经验已回填 skill `rimworld-auto-test`

### 桥接与测试基础设施
- [x] GABP v1 客户端（零依赖，`src/gabp.js`）
- [x] 隔离启动器：`-savedatafolder` + 独立 `Config/Prefs.xml`，绝不碰主力存档
- [x] 无管道子进程执行（沙箱下必须，`src/proc.js`）
- [x] 冒烟测试闭环 + 报告（`node src/cli.js smoke`，实测 9/9 通过）
- [x] 环境变量传端口/token（`GABP_SERVER_PORT` / `GABP_TOKEN`），桥接认这两项
- [x] 多显示器：`prefs.json` 的 `monitor`（1=主屏，2=副屏）
- [x] 窗口化 1600x900 + `runInBackground` + 静音 + 英文界面

### DSH 集成
- [x] `dsh-rimworld` Cordis 插件（7 个工具）—— **装好了但需要重启 DSH 才生效**
- [x] 两个 skill：`rimworld-auto-test`、`rimworld-mod-compat`（**已装全局**）
- [x] `docs/` 四篇文档 + 本文件 + 协作规范

### 原版调试体系研究
- [x] 完整爬取 debug action 树：341 个顶层动作，约 4 万节点
      缓存于 `runs/debug-tree-latest.json`（28.8 MB）
- [x] 结构规律：带 `...` 的动作按 Def 展开；`requiredTargetKind` 决定传什么；
      `T:` 前缀是工具型；标签 `...` 结尾的执行后可能弹多级对话框
- [x] 181 个 Settings 开关分类（确定性/加速/可视化/日志/作弊）
- [x] 247 个 Outputs 诊断读取（执行后会弹 `Window_DebugTable`，用 `get_ui_layout` 抠文字）

### Character Editor（角色编辑器）
- [x] CE 无官方 API，自己做了**两条驱动路径**：
      1. **UI 驱动**（`src/ce.js`）：开面板、选人、切标签页、点技能 −/+
      2. **反射直调**（`bridge/DSH.CEBridge/`，自写伴生 DLL）：绕过 UI 读写数据
- [x] 工具集：`ce/probe` `ce/colonists` `ce/body` `ce/amputate` `ce/restore`
      `ce/get_skills` `ce/set_skills` `ce/get_traits` `ce/ce_api`
- [x] 断肢可精确控制：截 `left leg` = 9 个部位缺失；截 `left foot` = 6 个

### Hot reload
- [x] `Actions\Hot reload Defs` 实测**亚秒生效**：改 XML → 重载 → 立即验证，不用重启

### 兼容性
- [x] 装好鼠族（NewRatkinPlus + HAR + XML Extensions），验证功能正常
- [x] 识别出「依赖红字是上游 packageId 大小写笔误」这一类纸老虎
- [x] 预设：`node scripts/modsconfig.mjs preset <baseline|ratkin>`

### 工程化
- [x] git 仓库已初始化，65 个文件，`.gitignore` 排除了 profiles/runs/node_modules/DLL
- [x] 仓库位置：`E:\2026.7以后的新工程\DSH\RimWorld_auto_test`

---

## 待办

- [ ] **游戏内适配层 `Game/`**（P1 blocking）—— 等 `docs/游戏API符号确证.md` 回来后开工
      - `StateReader`：游戏 API → `FoodState`（食物总量、殖民者数、在种作物、可用空地）
      - `PlotPlanner`：游戏地图 → `PassabilityMap`（地表可种性 / 肥力 / 通行代价）
      - `Executor`：`FoodAction` → 建种植区 / 设作物 / 复用已有区
      - `FoodControlGameComponent`：每游戏日跑一轮
      - `Telemetry`：写 `telemetry.jsonl`（测试脚本读它，**不需要桥接**）
- [ ] mod 骨架 + `About.xml` + junction 部署 + ModsConfig 接入
- [ ] 标定实测：`NutritionPerCellAtMaturity`、`EffectiveGrowthHoursPerDay`、每日消耗 nutrition
- [ ] 集成测试（读遥测 + 摆场景 + 断言，含**不振荡**断言）
- [ ] 补做 `play_for` 的**反向对照**（用 `open_main_tab {mainTabId}` 重开 Menu 标签页，预期再次 0 tick）
- [ ] 查清 Menu 标签页是谁打开的（`press_cancel` 已排除）
- [ ] 补齐 CE 工具：`ce/set_traits`、`ce/set_age`、`ce/set_hediffs`、`ce/set_genes`
- [ ] 打通 `Actions\Loaded Files For Mod`（现在只弹主菜单文字，疑似需要先选 mod）
- [ ] 解决 `Add Hediff` 的效果确认方式（Health 面板有变化但文字匹配不命中）
- [ ] 重启 DSH 让 `dsh-rimworld` 插件的 7 个工具生效

---

## 已知问题 / 坑（详见 docs/）

| 坑 | 说明 |
| --- | --- |
| **`create_allowed_area` 会挂死桥接** | 无限挂起，之后连只读调用也挂，只能重启游戏。**不要调用** |
| **`spawn_thing` 生成 pawn 会崩** | 用 `execute_debug_action('Actions\\Spawn Pawn...\\<kind>')` |
| **桥接调用排在游戏主线程** | 一个挂住全挂。超时必须有限，收到 `GABP_TIMEOUT` 就重启游戏 |
| **Doorstop 与 RimBridgeServer 冲突** | 启动时加 `--doorstop-enabled false`（启动器已默认） |
| **Prefs.xml 在 `Config/` 下** | 放错位置游戏静默不读 |
| **RimWorld 不扫描存档目录的 `Mods/`** | 用目录联接指向工作区 |
| **pawnId 有两种格式** | `Thing_115` 和 `Thing_Ratkin46626`，按尾部数字匹配 |
| **`restore` 要清整棵子树** | 截父部位时子部位各自带 MissingBodyPart |
| **DWM 隐形边框约 7px** | 判断窗口是否在屏内要留容差 |
| **`play_for` 会静默跑 0 tick** | 有 `forcePause` 窗口开着时（实测 `MainTabWindow_Menu`）。长跑前先查 `get_ui_state.windowsForcePause`。`pause_game` **只暂停不解除** |
| **`step_game_ticks` 的 `timeoutMs` 默认 10000** | 不显式传就 `timedout`，只推进几千 tick |
| **`open_main_tab` / `close_main_tab` 参数名不同** | 前者 `mainTabId`，后者 `tabId` |
| **桥接读不到 nutrition / 工作优先级** | 返回结构里就没这些字段，别硬凑，写伴生 DLL |

---

## 最近一次验证

- **食物自动化技术侦察**：20 项实测通过，全程 0 次 `GABP_TIMEOUT`，桥接健康
  （详见 `docs/食物自动化-技术侦察.md`；最要紧的 9 项未验证已列在报告里）
- **时间推进速度**：`play_for` 7110 tick/s / `step_game_ticks` 592 tick/s（实测）
- **`play_for` 秒退根因**：确认为 `forcePause` 窗口，关掉后 4 次复现稳定恢复（反向对照未做）
- **冒烟测试**：9/9 通过（副屏 1600x900）
- **鼠族身体编辑**：8/8 通过（`bodyDef=RK_Body_Ratkin`，65 部位 vs 人类 64）
- 桥接工具数：**134**（125 原生 + 9 个自写 CE 工具）
- mod 组合：13 项（鼠族套 + CE + 桥接 + 测试 mod）
- 游戏实例：副屏运行中（PID 39332；`node src/cli.js start` 恢复）

