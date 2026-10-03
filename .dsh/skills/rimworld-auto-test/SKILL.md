---
name: rimworld-auto-test
description: 在真实的 RimWorld 1.6 里自动化测试 mod —— 启动隔离游戏实例、开调试殖民地、用原版 debug action 摆出任意测试场景、驱动真实 UI、截图取证、跑断言出报告。当用户要求「测一下这个 mod」「跑一遍游戏测试」「在环世界里验证某个东西」「改完 Def 后验证」，或提到 RimBridgeServer / 调试殖民地 / RimWorld 测试时使用。
---

# RimWorld 自动化测试

工具链已经在 `RimWorld_auto_test` 仓库里搭好并实机验证过。**不要重新发明**，直接用现成的脚本和库。

## 仓库位置（绝对路径）

```
E:\2026.7以后的新工程\DSH\RimWorld_auto_test
```

**所有命令都在这个目录下执行**（下称 `$WS`）。新会话如果工作目录不是它，先 `cd` 过去。

先做一次自检，确认环境还在：

```powershell
cd 'E:\2026.7以后的新工程\DSH\RimWorld_auto_test'
node src/cli.js status          # 游戏在不在
node src/cli.js bridge          # 桥接信息（端口/令牌）
node src/cli.js tools | Select-String '工具总数'
```

游戏没在跑就 `node src/cli.js start` 重新拉起（**用长驻后台任务托管**，见下）。
环境细节、全部踩坑记录、当前 mod 组合见 `README.md` 和 `docs/`。

---

## 第一原则（违反会浪费大量时间）

1. **一切调用都要有限超时。** 桥接把调用排在游戏主线程上——某个调用挂住，**后续包括只读的调用全部一起挂死**，只能重启游戏。
   本仓库默认 60 秒。收到 `GABP_TIMEOUT` 就当「会话已废」，**重启游戏而不是重试**。
2. **永远回读状态确认，不要相信 `success`。**
   `success: true` 只表示调用返回了，不保证游戏真的变了。
3. **别和用户抢窗口。** 桥接的点击是真注入到游戏 UI 的；人工同时操作会把游戏搞卡死。
   驱动期间不要动那个窗口。
4. **卡住了先跑诊断**：`node scripts/diag.mjs`（每步 15 秒超时），看桥接是否响应、最后完成到哪一步。

---

## 已知地雷（实测确认，别踩）

| 工具 | 症状 | 对策 |
| --- | --- | --- |
| `rimworld/create_allowed_area` | **无限挂起，并把整个桥接一起拖死** | **不要调用** |
| `rimworld/spawn_thing` 传 pawn | 崩（`Verse.Pawn.SpawnSetup` NRE） | 用 `execute_debug_action('Actions\Spawn Pawn...\<kind>')` |
| `rimworld/search_debug_actions` 传短语 | 会挂死 | 只用**单个关键词**，如 `spawn` 而不是 `spawn pawn` |
| `rimworld/get_camera_state` 无地图时 | NRE | 先确认 `hasCurrentGame=true` |
| Doorstop 与 RimBridgeServer | 桥接完全起不来 | 启动时加 `--doorstop-enabled false`（仓库已默认） |

---

## 标准流程

### 1. 起会话

```bash
node src/cli.js start          # 启动隔离实例并等桥接就绪（约 6 秒）
node src/cli.js status         # 看进程/桥接状态
node src/cli.js stop           # 关闭
```

隔离机制：`-savedatafolder=profiles/<name>` + 写 `Config/Prefs.xml`。
**绝不碰用户的主力存档。** 窗口/画面设置在 `profiles/<name>/prefs.json`
（`runInBackground=true`、静音、英文界面）。

**多显示器**：`prefs.json` 的 `monitor` 决定启动到哪个屏（1=主屏，2=副屏），
会转成 Unity 的 `-monitor N`（**1-based**）。用户在主屏玩别的游戏时用 `"monitor": 2` 把它赶到副屏。

⚠ **分辨率必须按目标屏设**。实测主屏 4K、副屏 1080p：若 `screenWidth/Height` 写 2560x1440
而目标是 1080p 副屏，窗口会**溢出到主屏**。先量目标屏尺寸再定，副屏 1080p 用 1600x900 刚好。

查窗口落在哪个屏（WinForms 一句话，不用写 Win32 枚举）：

```powershell
Add-Type -AssemblyName System.Windows.Forms
[System.Windows.Forms.Screen]::FromHandle((Get-Process RimWorldWin64).MainWindowHandle).DeviceName
# → \\.\DISPLAY2
```

⚠ **判断"窗口是否在屏内"要给 ~10px 容差**：Windows 10 窗口有一圈隐形拖拽边框（DWM），
外框比可见区域大 7px 左右，直接拿 `GetWindowRect` 比会误判成溢出。

> 用长驻任务托管游戏：从一次性 shell 命令里启动的游戏，会在该命令结束时被进程树清理掉。

### 2. 开调试殖民地

```js
await call('rimworld/start_debug_game_ready',
  { readiness: 'mapData', pauseIfNeeded: true, timeoutMs: 300000 },
  { timeoutMs: 330000 });      // 请求超时必须大于工具自身 timeoutMs
await call('rimbridge/wait_for_long_event_idle', { timeoutMs: 120000 });
```

`readiness`：`gameData` < `mapData` < `currentMap` < `playable` < `visual`。
只要数据用 `mapData`（快得多），要截图/UI 才用 `visual`。
**殖民者名字每次随机，不要硬编码**，一律 `list_colonists` 现取。

### 3. 把环境设成确定性的（做回归测试必做）

```js
import { applyTestProfile } from '../src/testprofile.js';
// 必须：默认有一堆随机源是【开着】的，不关掉测试就不可复现
await applyTestProfile(client, ['deterministic']);   // 关 Storyteller / 随机精神状态 / 随机疾病 / 伤害
await applyTestProfile(client, ['fast']);            // 加速研究/学习/制作/商队
await applyTestProfile(client, ['sandbox']);         // 上帝模式等
```

⚠ **关掉 Storyteller 不等于完全确定**：Anomaly DLC 的方尖碑（monolith）事件是独立系统，
Settings 里没有开关能关。要彻底确定，就在 `ModsConfig` 里去掉 `ludeon.rimworld.anomaly`。

### 4. 摆测试场景 —— 用 debug action，不要手搓

原版调试树有 **341 个顶层动作**，索引在 `runs/debug-tree-index.json`，清单在 `runs/debug-actions-top.md`。

```js
// 生成指定 pawn（替代会崩的 spawn_thing）—— 叶子 requiredTargetKind = map
await call('rimworld/execute_debug_action',
  { path: 'Actions\\Spawn Pawn...\\Thrumbo', x, z });

// 改人物属性 —— requiredTargetKind = pawn，传 pawnId
await call('rimworld/execute_debug_action',
  { path: 'Actions\\Max Skill...\\Shooting', pawnId });        // 12 个技能
await call('rimworld/execute_debug_action',
  { path: 'Actions\\Give Trait...\\nudist (0)', pawnId });     // 73 个特质；标签带等级后缀
await call('rimworld/execute_debug_action',
  { path: 'Actions\\Add Hediff...\\Acid burn', pawnId });      // 324 个
// 三级结构：先拿第二层，再列它的子项，再执行
//   Actions\Set Backstory...\Childhood  -> 383 个童年背景
//   Actions\Add Gene...\Xenogene        -> 232 个基因

// 事件与袭击
await call('rimworld/execute_debug_action', { path: 'Actions\\Do incident\\AmbrosiaSprout' });
await call('rimworld/execute_debug_action', { path: 'Actions\\Execute raid with points...\\400 points', x, z });
```

摆场景前先查结构，别猜：

```bash
node src/cli.js call rimworld/get_debug_action --args-file args.json   # {"path":"Actions\\Spawn Pawn...","includeChildren":true}
```

规律：带 `...` 的是**参数化动作，按 Def 展开**；父节点是 `BrowseOnly`（菜单容器），
**能执行的是叶子**；`T:` 前缀是工具型（要地图坐标）。

### 5. 改 Def 后热重载 —— 不用重启游戏（实测有效）

```bash
node scripts/verify-4-hotreload.mjs      # 完整验证脚本，可当参考实现
```

流程：改 `profiles/<name>/Mods/<TestMod>/Defs/*.xml` → 执行 `Actions\Hot reload Defs` → 重新生成/读取验证。
实测 **亚秒级生效**（label v1 → v2）。

> 游戏只从**游戏安装目录的 `Mods/`** 和工坊目录加载 mod，不扫描隔离存档目录。
> 仓库用**目录联接（junction）**把 `游戏目录/Mods/DSH_AutoTest` 指向工作区里的那份，
> 这样改工作区就等于改游戏看到的文件，不需要工作区外写权限。

ModsConfig 管理：`node scripts/modsconfig.mjs {list|add|remove|set|baseline|copy-main}`

### 6. 驱动真实 UI

```js
const snap = await call('rimworld/get_ui_layout');
// snap.surfaces[] -> elements[] { targetId, kind, label, actionable, screenRect, scroll }
await call('rimworld/click_ui_target', { targetId, timeoutMs: 5000 });
```

- **列表行 = 2 个 label + 1 个 button，且 button 自己的 `label` 是 `null`** ——
  必须靠 **`screenRect` 完全一致**配对，不能"找到文字就点"。用 `src/ui.js` 的 `clickRowByLabel()`。
- 文字匹配**默认要精确**：`"800 points"` 会命中 `"1800 points"`，用 `exact: true`。
- **对话框层数不可预测**（空投袭击实测 4 步，点了地图还会再弹）。
  写成**贪心循环**，参考 `scripts/raid-full.mjs`。
- `click_ui_target` 在下一个真实绘制帧激活，**不需要窗口焦点**。

### 7. 读状态

```js
const c = (await call('rimworld/get_cell_info', { x, z })).cell;
c.terrainDefName, c.walkable, c.things[], c.blueprintBuildDefs, c.designationCount

// 选中语义：inspect 文本在 selectedObjects[].inspectString（不是 inspectStrings）
const sem = await call('rimworld/get_selection_semantics');
sem.selectedObjects[0].inspectString     // 例："Female, age 18 (1943) of New Arrivals / Mental state: Berserk"

// 批量：get_cells_info 上限 1024 格

// Outputs：247 个诊断读取，执行后会弹出 Window_DebugTable，用 get_ui_layout 抠文字
await call('rimworld/execute_debug_action', { path: 'Outputs\\Current Threat Points' });
const lay = await call('rimworld/get_ui_layout');   // 只取 type 含 Window_DebugTable 的那个 surface
await call('rimworld/close_window', {});
```

### 8. 时间控制

| 方式 | 用途 |
| --- | --- |
| `step_game_ticks({ticks, pauseFirst:true})` | **精确推进，返回 `advancedTicks` 可直接当断言**（回归测试首选） |
| `set_time_speed('Ultrafast', ultraSpeedBoost:true)` + `play_for({durationMs})` | 真实速度跑，实测约 **4700 tick/秒**（不确定，只用于观察） |
| `play_until_letter` | 跑到出现新信件 |

⚠ **刚开完殖民地时 `step_game_ticks` 可能返回 `status=failed`、推进 0 tick** ——
`readiness=mapData` 只保证数据就绪，地图还在初始化。
所以：开完殖民地要等 `readiness=currentMap`，并且推进要**带重试**（失败就再等一次地图就绪）。
`src/smoke.js` 已实装这个模式。

### 9. 截图

- **截的是游戏渲染画面，不是桌面**（已用置顶洋红窗口验证）。
- 分辨率 = `Config/Prefs.xml` 的 `screenWidth/Height`（**不是** `-screen-width`）。
  换分辨率要用**显示器支持的模式**（4K 屏上用 2560x1440；实测 1600x900 会被忽略成 2539x1439）。
- `screenshot_cell_rect({x,z,width,height,paddingCells})` 会自动移镜头→按格裁剪→还原镜头。

### 10. 存档 / 读档

```js
await call('rimworld/save_game', { saveName });
await call('rimworld/load_game_ready',
  { saveName, readiness: 'mapData', pauseIfNeeded: true, ignoreModCompatibility: true },
  { timeoutMs: 200000 });
```

---

## 现成脚本

| 脚本 | 用途 |
| --- | --- |
| `src/cli.js` | 命令行入口：`start` / `status` / `tools` / `describe` / `call` / `logs` / `stop` / `smoke` |
| `src/gabp.js` | GABP 协议客户端（零依赖） |
| `src/launcher.js` | 隔离启动/停止、等桥接、读端口与 token |
| `src/prefs.js` | 画面/音频偏好合并（只强制关键项，保留用户设置） |
| `src/testprofile.js` | 确定性/加速/沙盒/压力四套配置档 |
| `src/ui.js` | `clickRowByLabel()` 等真实 UI 操作辅助 |
| `src/smoke.js` | 冒烟测试闭环 + 报告 |
| `scripts/diag.mjs` | **卡住时的第一诊断** |
| `scripts/debug-tree.mjs` | 爬取 debug action 树 → `runs/debug-tree-latest.json` |
| `scripts/modsconfig.mjs` | 切换测试用 mod 组合 |
| `scripts/raid-full.mjs` | 多步原生对话框驱动的参考实现 |

---

## 用 Character Editor 精确摆人物（两条路，都已验证）

CE 是纯 UI mod，**没有任何官方 API**。我们给它做了两条驱动路径，按需选：

### 路线 A：反射直接调 CE 内部数据 API（推荐，精确、批量、不依赖 UI）

`bridge/DSH.CEBridge/` 是我们自己写的 RimBridgeServer 伴生 DLL，已部署到游戏 `BridgeTools/`。

```bash
node scripts/build-cebridge.mjs    # 编译（改游戏状态前必须先停游戏，DLL 会被占用）
node src/cli.js call ce/probe       # 自检：CE 加载了吗、内部 API 还在吗
node src/cli.js call ce/colonists   # 列殖民者 + 稳定 pawnId
node src/cli.js call ce/get_skills  --args-file a.json   # {"pawnId":"Thing_115"}
node src/cli.js call ce/set_skills  --args-file b.json   # {"pawnId":"...","skills":"..."}
node src/cli.js call ce/get_traits  --args-file a.json
```

CE 的技能是**自描述文本格式**，一次读全部、一次写全部：

```
Shooting|5|1|0|0:Melee|6|2|0|0:Construction|4|1|0|0
        ↑等级 ↑激情 ↑经验
```

**编译器**：本机有 Roslyn（VS2022 BuildTools 里），不用装 .NET SDK。
沙箱里编译要用仓库的 `capture()`，不能直接 `execFileSync`（管道会 EPERM）。

> ⚠ 反射全靠字符串匹配 CE 内部类型名，**CE 升级后可能失效**。出问题先跑 `ce/probe`。

### 路线 B：驱动 CE 的真实 UI（可见、无需编译）

`src/ce.js`：

```js
import * as ce from '../src/ce.js';
await ce.ensureOpen(client);              // main-tab:HotkeyEditor
await ce.openTab(client, 'Bio');           // Bio/Inventory/Health/Needs/Social/Log/Info/Records
await ce.selectPawn(client, 'Alex');       // 左侧列表
await ce.bumpSkill(client, 'Shooting', +3);// 点该行右侧的 −/+ 按钮
await ce.getSkillLevel(client, 'Shooting');
```

**坑**：CE 把左栏（殖民者）、中栏（特质）、右栏（技能）排在同一批 y 上，
必须**同时按 x 限列 + 按 y 容差（±8px）取带**；技能 ± 是该行仅有的两个 `icon_button`，
靠左的减、靠右的加。刚打开 CE 立刻截图会拿到未绘制完的帧，等约 1 秒。

### 什么时候用哪条

| 需求 | 用哪条 |
| --- | --- |
| 精确批量设置技能/特质等文本化状态 | **反射**（`ce/set_skills`） |
| 改名字、改滑块类数值 | **反射**（UI 路线的硬伤：`HorizontalSlider` 未 patch、无文字输入工具） |
| 需要"看着界面操作"、验证 UI 本身 | **UI**（`src/ce.js`） |

---

### 身体部位 / 断肢（已实测验证）

```bash
node src/cli.js call ce/body     --args-file a.json   # {"pawnId":"Thing_709"}  读身体部位树 + 缺失状态
node src/cli.js call ce/amputate --args-file b.json   # {"part":"left leg","pawnId":"..."}
node src/cli.js call ce/restore  --args-file b.json   # {"part":"left leg","pawnId":"..."}
node src/cli.js call ce/ce_api   --args-file c.json   # {"tool":"HealthTool"}  dump CE 内部方法
```

**实测语义**（Human 身体共 64 个部位）：

| 操作 | 结果 |
| --- | --- |
| `amputate "left leg"` | **9 个部位变缺失**：Leg + Femur + Tibia + Foot + 5 个 Toe |
| `amputate "left foot"` | **6 个部位变缺失**：Foot + 5 个 Toe |
| `restore` | 清掉整棵子树，回到 0 缺失 |

`part` 是**标签子串**（大小写不敏感），如 `left leg` / `right arm` / `foot`；
多个匹配时用 `index` 选。父部位缺失时子部位通过 `PartIsMissing()` 也算缺失 ——
**读状态必须用这个 API，不能只看有没有 MissingPart 这个 hediff**。

⚠ **一个重要的实现事实**：CE 自己的 `HealthTool` 有 86 个方法，但**几乎全是判定谓词**
（`IsForLeg` / `IsForArm` / `GetBodyPart` …），**没有截肢/恢复的方法**。
真正的部位变更是走**游戏原版健康系统**（`HediffMaker.MakeHediff(HediffDefOf.MissingBodyPart, pawn, part)`
+ `pawn.health.AddHediff/RemoveHediff`），CE 的 UI 只是它的前端。
→ 所以要做「条件贴图/断肢」类 mod，**依赖原版 API 就够了，不需要依赖 CE**；
CE 的价值在于人工微调与可视化确认。

**一个坑**：`amputate` 父部位时子部位会各自带上 MissingBodyPart，
所以 `restore` 必须清**整棵子树**，只清目标本身不够（实测 18 → 17 而不是 18 → 9）。

---

## 交付测试时

跑 `node src/cli.js smoke`，报告落在 `runs/<runId>/`（`report.json` 机器可读 + `report.md` 人看 + 截图副本）。

**报告里要如实区分**：桥接通了 / 游戏真的变了 / 只有 `success` 没有回读证据。
不要把「调用成功」写成「功能正确」。
