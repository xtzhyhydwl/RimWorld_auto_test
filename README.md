# RimWorld 自动测试桥

给 AI agent（DSH）用的 RimWorld 1.6 **闭环自动化测试基础设施**。

一句话：**让 agent 自己把游戏开起来、开一局殖民地、推进时间、收日志、截图、跑断言、出报告**——不需要人点鼠标。

---

## 现在能做什么

已经实机验证跑通的完整闭环：

```
启动隔离实例(6.1s) → GABP 握手 → 启动调试殖民地(4.0s) → 读殖民者(3人)
  → 推进 300 tick → 收集新增日志(0错0警) → 快照告警 → 截图 → 断言 → 出报告
```

```powershell
node src/cli.js smoke --attach --ticks 300
```

产出的报告见 [`runs/20261003-011053_smoke/report.md`](runs/20261003-011053_smoke/report.md)。

游戏内**可调用工具共 125 个**，覆盖：
读游戏/地图/相机/选择/通知/UI 状态、执行 debug action、跑 architect 设计器、
改 mod 设置与加载顺序、截图（含按 UI 元素裁剪）、存档读档、出生物品、
点/拖/右键地图格、开关主面板与 inspect 面板、Dubs 性能分析器采样……
完整清单见 `runs/tools.json`（`node src/cli.js dump-tools` 生成）。

---

## 架构

```
┌──────────────────────────────────────────────┐
│ DSH agent                                    │
│   └── dsh-rimworld 插件（待做，见"下一步"）    │
└───────────────────┬──────────────────────────┘
                    │ 本仓库的 Node 库
┌───────────────────▼──────────────────────────┐
│ 本仓库 (RimWorld_auto_test)                   │
│   src/gabp.js      GABP v1 客户端（零依赖）    │
│   src/launcher.js  隔离启动/停止/等桥接        │
│   src/proc.js      无管道子进程执行            │
│   src/smoke.js     冒烟测试闭环 + 报告          │
│   src/cli.js       命令行入口                  │
└───────────────────┬──────────────────────────┘
                    │ TCP 127.0.0.1:5174, LSP 分帧, JSON-RPC
┌───────────────────▼──────────────────────────┐
│ RimWorld 1.6（隔离实例）                       │
│   RimBridgeServer 2.1.1 → 125 个工具           │
└──────────────────────────────────────────────┘
```

**为什么选 GABP 直连而不是 MCP**：RimBridgeServer 的 direct 模式自己监听 TCP 并打印端口/token，
协议就是 LSP 分帧的 JSON-RPC，Node 里 200 行就能实现，不用额外装 GABS 或任何依赖。

---

## 文档与技能

| 文件 | 内容 |
| --- | --- |
| [docs/RIMBRIDGE-技巧手册.md](docs/RIMBRIDGE-技巧手册.md) | 操作手册：会话纪律、地雷清单、各类操作的正确姿势 |
| [docs/原版调试体系研究.md](docs/原版调试体系研究.md) | 原版调试体系：3 个根、5 条结构规律、完整分类 |
| [docs/验证结果总报告.md](docs/验证结果总报告.md) | 5 个阶段的端到端验证结果（含未验证项） |
| `runs/debug-actions-top.md` | 341 个顶层调试动作清单 |
| `runs/debug-settings-outputs.md` | 181 个开关 + 247 个诊断读取 |
| `.dsh/skills/rimworld-auto-test-closed-loop/SKILL.md` | **DSH 技能**：一提到 RimWorld 测试就自动带上拆解 + 硬规则 + 指针 |

---

## 快速开始

前置：Node ≥ 20、RimWorld 1.6、`Mods/` 下有 [RimBridgeServer](https://github.com/pardeike/RimBridgeServer)、
Workshop 有 [Harmony](https://steamcommunity.com/sharedfiles/filedetails/?id=2009463077)。

```powershell
# 游戏不在默认路径时设置它
$env:RIMWORLD_ROOT = 'D:\SteamLibrary\steamapps\common\RimWorld'

node src/cli.js start              # 启动隔离实例并等桥接就绪
node src/cli.js tools              # 列出 125 个工具
node src/cli.js tools colonist     # 关键词过滤
node src/cli.js describe rimworld/step_game_ticks   # 看参数 schema
node src/cli.js call rimworld/list_colonists
node src/cli.js call rimworld/step_game_ticks --args-file runs\args.json
node src/cli.js logs 100           # Player.log 尾部
node src/cli.js stop               # 关闭实例
```

`smoke` 子命令：

```powershell
node src/cli.js smoke                    # 自己启动 → 开新殖民地 → 跑 → 关掉
node src/cli.js smoke --attach           # 用已在跑的实例（游戏里已有存档就跳过开新局）
node src/cli.js smoke --attach --ticks 2000
node src/cli.js smoke --keep-running     # 跑完不关游戏
node src/cli.js smoke --doorstop         # 保留 Doorstop（一般不要，见下）
```

> **参数里的 JSON 用 `--args-file` 传**，别直接写在命令行里：PowerShell 5.1 会吞掉双引号，
> 而 `Set-Content -Encoding UTF8` 又会加 BOM。CLI 已经会自动去掉 BOM。

---

## DSH 插件：让 agent 直接调用

`plugin/dsh-rimworld/` 是一个 Cordis 插件，把上面的能力注册成 DSH 原生工具：

| 工具 | 作用 |
| --- | --- |
| `rimworld_status` | 游戏是否在跑、桥接是否可达 |
| `rimworld_launch` | 启动隔离实例并等桥接就绪（默认关掉 Doorstop） |
| `rimworld_stop` | 关闭实例 |
| `rimworld_tools` | 列出游戏暴露的 125 个工具（可关键词过滤） |
| `rimworld_call` | 调用任意游戏内工具，参数传 JSON |
| `rimworld_smoke` | 跑完整冒烟测试并返回报告路径 |
| `rimworld_logs` | 看 Player.log 尾部，或读游戏内日志期刊 |

```powershell
node scripts\test-plugin.mjs --live      # 在 DSH 之外验证插件（不装也能测）
node scripts\install-plugin.mjs --dry-run # 看安装会改什么
node scripts\install-plugin.mjs           # 安装（自动备份配置）
node scripts\uninstall-plugin.mjs         # 完整卸载
```

安装会改 `~/.dsh/profiles/<profile>/` 下的三处，两个配置文件都先备份成 `.bak-<时间戳>`：
复制插件目录到 `node_modules/dsh-rimworld`、在 `package.json` 的 `dsh.profile.bundles` 里登记、
在 `cordis.patch.yml` 末尾追加带配置的行。**改完要重启 DSH 才生效。**

插件源码在装进 DSH 之前就已经用 `scripts/test-plugin.mjs` 验证过：工具注册、
展示层形状、以及对真实游戏的调用都跑通了。

---

## 隔离：绝不碰你的主力存档

启动时传 RimWorld 原生参数：

| 参数 | 作用 |
| --- | --- |
| `-savedatafolder=<dir>` | Config / Saves / Prefs 全部重定向到 `profiles/<name>/` |
| `-logFile <path>` | 日志写到隔离目录，桥接端口和 token 从这里读 |
| `-screen-fullscreen 0` | 窗口化 |
| `-screen-width/-height` | 固定窗口尺寸 |

实测确认（游戏自己打印的）：

```
Command line arguments: -savedatafolder=E:\...\profiles\vanilla -logFile ...
Save data folder overridden to E:\2026.7以后的新工程\DSH\RimWorld_auto_test\profiles\vanilla
```

`profiles/vanilla/Config/ModsConfig.xml` 里是**纯净基线**：原版 + 全 6 个 DLC + Harmony + RimBridgeServer，
共 8 项。实测启动 **6.1 秒**（你的主力配置是 385 个 mod）。

要测特定 mod 组合时，往这个 ModsConfig 里加条目即可，或者复制一份成 `profiles/<新名字>/`。

---

## 踩过的坑（都很关键，别重复踩）

### 1. Doorstop 和 RimBridgeServer 冲突 —— 本次最大的坑

你游戏根目录装了 [Rimworld-Doorstop](https://github.com/pardeike/Rimworld-Doorstop)
（`doorstop_config.ini` + `winhttp.dll` + `Doorstop.dll`，作者 Brrainz，当前 1.8.2.0 已是最新）。
它提供 **Mono 调试器（127.0.0.1:56000）+ IL 热重载**，会 patch `Verse.ModAssemblyHandler.ReloadAll`。

它的 `ILReloaderLib.AssemblyLoadingPatcher.RegisterTypes` 会**提前解析每个被加载程序集的自定义特性**。
而 RimWorld 按文件名顺序加载 `Assemblies/` 下的 DLL：

```
RimBridgeServer.Contracts.dll
RimBridgeServer.Core.dll                    ← 在这里就解析特性了
RimBridgeServer.Extensions.Abstractions.dll ← 但它的类型还没加载
RimBridgeServer.Sdk.dll
RimBridgeServer.dll
```

Core 引用了 Abstractions 里的 `IRimBridgeCapabilityProvider`，于是：

```
Exception loading RimBridgeServer.Core.dll: System.TypeLoadException:
Could not resolve type with token 0100001f from typeref
(expected class 'RimBridgeServer.Extensions.Abstractions.IRimBridgeCapabilityProvider' ...)
  at ILReloaderLib.Tools.IsReloadable ...
  at ILReloaderLib.AssemblyLoadingPatcher.RegisterTypes ...
```

整个桥接 mod 加载失败 → 没有 GABP 服务 → 什么都连不上。

**解法**（本仓库采用）：启动时加 `--doorstop-enabled false`（Unity Doorstop v4 的原生开关）。
不动你的 `doorstop_config.ini`，只在测试实例上生效。

**顺带一提**：`DOORSTOP_ENABLED=0` 环境变量在这个 fork 上无效，只有命令行开关管用。

**副作用提醒**：只要是「Doorstop 开着 + Harmony 已加载 + RimBridgeServer 启用」的组合，
你在正常玩的时候 RimBridgeServer 也起不来。想手动用桥接就临时用带 `--doorstop-enabled false` 的方式启动，
或者把 `RimBridgeServer.Extensions.Abstractions.dll` 改名成 `0RimBridgeServer.Extensions.Abstractions.dll`
让它排在 Core 前面（能同时保住热重载，但会改动 mod 文件）。

### 2. 受限沙箱下不能用管道子进程

在 DSH 沙箱里 `child_process` 默认的管道 stdio 会 `spawn EPERM`。
把 stdout/stderr **重定向到临时文件**则完全正常。`src/proc.js` 的 `capture()` 就是这么做的，
顺带还免掉了 shell 引号转义。

### 3. 沙箱下 `tasklist` 返回 Access denied

`tasklist` 被拒绝时返回的是「查不到」而不是「没有」，早期版本据此误判「游戏已退出」，
在启动 1 秒后就放弃了。现在一律以**自己记录的 PID + `process.kill(pid, 0)`** 为准，
不依赖任何外部进程枚举。

### 4. pwsh 任务结束会带走整个进程树

从 pwsh 后台任务里 `spawn` 出的游戏，会在该任务结束时被 DSH 一起清理掉。
开发时要用一个长驻任务把游戏「托管」起来；正式方案是由 DSH 插件在自己的进程里
用 `ctx.subprocess` 启动（不受文件沙箱限制）。

### 5. PowerShell 传 JSON 的两个坑

双引号被吞（`Expected property name`）→ 用 `--args-file`；`Set-Content -Encoding UTF8` 带 BOM
（`Unexpected token ''`）→ CLI 读文件时自动去 BOM。

### 6. 自动化驱动时不要同时人工操作游戏窗口

桥接的点击/按键是**注入到真实游戏 UI 的**。如果 agent 正在驱动（尤其是 `play_for` 高速推进、
或正在点对话框），人同时在那个窗口里点鼠标，两边会互相打断，实测能把游戏搞卡死。

**约定**：agent 驱动期间别碰那个窗口。要自己玩就用主力存档另开一个实例。
（后续可以考虑默认以最小化窗口启动，或者加一个"占用中"的互斥标记。）

### 7. 多步 debug action 的对话框层数是不确定的

例："Drop pods raid at location..." 实测要走 **势力 → 点数 → 点地图 → 半径** 四步，
而且点了地图之后还会再弹一次。**不要硬编码层数**，写成贪心循环：
有弹窗就按候选池挑一个能匹配的选项点掉，没弹窗就补一次地图点击，直到点击后不再弹。
参考 `scripts/raid-full.mjs`。

另一个细节：RimWorld 列表行在 UI 快照里是**多个元素**（2 个 label + 1 个 button，
button 自己的 label 是 null），所以不能靠 label 找可点元素，要靠**屏幕矩形相同**配对。
`src/ui.js` 的 `clickRowByLabel()` 已经处理好了。精确匹配要用 `exact: true`，
否则「800 points」会命中「1800 points」。

### 8. 桥接的调用排在游戏主线程上，一个挂住就全挂

某个调用一旦挂起，**后续所有调用（包括只读的）都会一起挂死**，只能重启游戏实例恢复。

所以：**所有调用都必须设有限超时**（本仓库默认 60 秒）；一旦收到 `GABP_TIMEOUT`，
当成「会话已废」，重启而不是重试。

实测到的地雷：

| 工具 | 症状 |
| --- | --- |
| `rimworld/create_allowed_area` | **无限挂起，并把整个桥接一起拖死** |
| `rimworld/spawn_thing` 传 pawn | 崩溃（`Verse.Pawn.SpawnSetup` NullReferenceException）；只能生成物品 |
| `rimworld/search_debug_actions` 传短语（如 `"spawn pawn"`） | 挂死。用单个关键词 |

完整清单和正确用法见 **[docs/RIMBRIDGE-技巧手册.md](docs/RIMBRIDGE-技巧手册.md)**。

### 9. 上帝模式下建筑是瞬间建成的

`apply_architect_designator` 配 `set_god_mode(true)` 时，建筑**立刻建成**并出现在
`cell.things` 里。如果你去查 `cell.blueprintBuildDefs` 会得到空数组，从而误判「没生效」。
（我为此浪费了一轮排查。）

---

## 目录结构

```
src/
  gabp.js        GABP v1 协议客户端：LSP 分帧、session/hello 握手、tools/list、tools/call、事件
  launcher.js    隔离启动 / 等待桥接 / 停止；读日志抓端口与 token
  proc.js        无管道子进程执行 + PID 存活检测
  ui.js          真实 UI 操作辅助：按文字找行并点它、等待条件、列出界面
  smoke.js       冒烟测试闭环与报告生成
  cli.js         命令行入口
plugin/dsh-rimworld/   DSH Cordis 插件（7 个工具）
scripts/
  install-plugin.mjs      安装插件到 DSH profile（带备份）
  uninstall-plugin.mjs    完整卸载
  test-plugin.mjs         在 DSH 之外验证插件（含真实游戏调用）
  demo.mjs                一串真实游戏操作演示
  raid-full.mjs           驱动多层原生对话框叫一场空投袭击
profiles/
  vanilla/       纯净隔离存档目录（Config/Saves/Screenshots/Player.log）
runs/
  <runId>/       report.json + report.md + 截图副本
  game-process.json  当前实例的 PID 与启动参数
  tools.json     全部 125 个工具的完整 schema
```

---

## 下一步

- [x] **dsh-rimworld 插件**：7 个工具已注册，并已在 DSH 之外验证通过（含真实游戏调用）
- [x] **原版调试体系研究**：341 个 Actions + 247 个 Outputs + 181 个 Settings，
      结构规律与用法见 [docs/原版调试体系研究.md](docs/原版调试体系研究.md)
- [x] **Hot reload Defs 端到端验证**：改 Def XML → 亚秒级重载 → 立即验证，不用重启游戏
- [x] **固化为 skill**：`.dsh/skills/rimworld-auto-test-closed-loop/SKILL.md`（项目级，DSH 已识别）
- [x] **验证总报告**：[docs/验证结果总报告.md](docs/验证结果总报告.md)（5 个阶段，逐项通过/失败）
- [ ] **回归测试用例**：面向具体 bug 的「读档 → 执行操作序列 → 断言」脚本化
- [ ] **视觉回归**：截图 diff（分辨率已固定在 2560x1440）
- [ ] **打通 `Loaded Files For Mod`**、解决 `Add Hediff` 的效果确认方式
- [ ] **把 Character Editor 等 mod 纳入**（自带 UI，走 UI 驱动路径）
- [ ] **RimWorldForge 互补**：它管静态侧（Def 索引、XML 校验、构建打包），
      本仓库管运行时侧（真的把游戏跑起来验证）

---

## 相关项目

| 项目 | 作用 |
| --- | --- |
| [RimBridgeServer](https://github.com/pardeike/RimBridgeServer) | 游戏内桥接（本仓库依赖，已装 2.1.1） |
| [GABP](https://github.com/pardeike/GABP) | 桥接协议规范 |
| [GABS](https://github.com/pardeike/GABS) | 官方 MCP 多路复用器（本仓库没走这条路） |
| [Rimworld-Doorstop](https://github.com/pardeike/Rimworld-Doorstop) | Mono 调试 + IL 热重载（冲突源，见坑 1） |
| [RimWorldForge](https://github.com/ShugokiFable/RimWorldForge) | 静态侧 mod 开发工作台 |
| [rimUIMCP](https://github.com/Hcrab/rimUIMCP) | 另一套中文生态的桥接（本仓库启动方式参考了它） |
