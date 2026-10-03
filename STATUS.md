# 状态

> 这是给下一个会话看的接力文件。每完成一个阶段就更新它。
> 规则见 [docs/协作规范.md](docs/协作规范.md)。

## 当前在做什么

**用户决定做「生产自动化」mod，第一条线路：食物闭环。**

设计已完成，等用户拍板几个参数后进入实现。设计文档：
[docs/生产自动化-设计.md](docs/生产自动化-设计.md)

一句话形态：**感知（现有多少食物）→ 预测（未来 15 天每天多少）→ 决策（要不要动手）
→ 执行（自动画种植区去种）→ 观测回流**。只有「决策」一层是可替换的，
所以「上层用脚本还是 LLM」是局部改动。

**下一步**：写 `DSH.FoodBridge` 伴生 DLL（感知必须靠它，桥接读不到 nutrition）。

---

## 已完成

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

- [ ] **写 `DSH.FoodBridge` 伴生 DLL**（P1 的 blocking 前置，照 `bridge/DSH.CEBridge/` 范式）
      - `food/summary`（食物总量 + 殖民者数 + 每日消耗）
      - `food/plants`（在种作物：Def/格数/`growth` float/土壤肥力）
      - `food/set_crop`（绕开「改作物只能走 UI」）
      - 编译前**必须先停游戏**；「写 DLL 能解决」目前仍是**推断**，未编译验证过
- [ ] 用户拍板：目标带数值 / 是否把狩猎放进 MVP / 作物是否固定水稻
- [ ] 补做 `play_for` 的**反向对照**（用 `open_main_tab {mainTabId}` 重开 Menu 标签页，预期再次 0 tick）
- [ ] 查清 Menu 标签页是谁打开的（`press_cancel` 已排除）
- [ ] 测精确的**有效生长小时数**（晚上 Growth rate = 0%，影响预测器精度）
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

