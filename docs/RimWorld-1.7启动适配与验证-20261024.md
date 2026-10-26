# RimWorld 1.7 启动适配与验证 —— 2026-10-24

**范围**：只改了 `src/launcher.js`（游戏入口/日志落点的探测），并用 `rhah` profile 在 1.7 unstable 上实跑验证到主菜单。
**没碰**：玩家存档（`%LocalLow%\…\Saves\` 一个文件都没打开/加载/保存）、Steam 分支、`profiles\voiced` / `vanilla` / `bike`、其它 profile 的 `ModsConfig`、`brrainz.rimbridgeserver` 与 `dsh.gamelink` 两个 mod 自身的文件。
**没提交** git（工作区里 `mods/DSH_GameLink/**` 的两处改动是**另一路**工作流在 17:53:35 写的，不是本轮）。

标注：**【实测】**= 本轮跑出来/读到的；**【推测】**= 未验证的推断，报告里不当作结论。

---

## 1. 结论（5 行）

1. **探测修好了**：exe 名从写死 `RimWorldWin64.exe` 改成「`RimWorld.exe` 优先 → `RimWorldWin64.exe` 回落 → 都不在则报错并列出找过的路径」，启动 / `status` / `stop` 的进程匹配**共用同一处探测结果**（`detectGameImage`）。【实测】
2. **1.7 能起，且已到主菜单**：`rhah` profile（14 项 mod，含 RimBridgeServer / GameLink / HAR / Ratkin / IrisMenus / Oxygendsl / RHAH）在 1.7.4985 rev1269 上启动、桥接 21.9s 就绪、184 个桥接工具可用、主菜单截图已取证。【实测】
3. **成功的 profile = `rhah`**（`voiced` / 最小 mod 的降级路径**没有启用**，因为 `rhah` 一次就过）。【实测】
4. **没有 mod 因为「不支持 1.7」而挡启动**：14 项全部 `loadedInSession: true`。只有 `brrainz.rimbridgeserver`（About 只声明 1.6）和 5 个官方 DLC（About 只声明 1.6）被游戏标了「版本不符」警告，但都正常加载并工作（桥接就是我这次用的）。真正读到的异常见 §6/§7。【实测】
5. **已停**：`node src/cli.js stop --profile rhah`（**不带** `--force`），`Get-CimInstance Win32_Process` 复查残留 RimWorld 进程 **0 个**。【实测】

---

## 2. 硬编码清单（按 exe 名 / 进程名判断的地方）

改前全部集中在 `src/launcher.js`（`src/cli.js`、`src/prefs.js`、`src/live.js`、`src/smoke.js`、`scripts\*`、`plugin\dsh-rimworld` 里**一处都没有** exe/进程名，grep `RimWorldWin64|RimWorld.exe|GAME_IMAGE` 实测确认）。

| 改前位置 | 用途 | 1.7 下的后果【实测】 |
|-|-|-|
| `src/launcher.js:33` `export const GAME_IMAGE = 'RimWorldWin64.exe'` | 唯一的 exe 名常量 | 名字已不存在（旧 exe 被 Steam 删除） |
| `src/launcher.js:329` `const exe = path.join(root, GAME_IMAGE)` + `:330-334` 存在性检查 | **启动**：拼可执行文件路径并在缺失时报错 | 启动直接 throw，在任何副作用之前 —— 这就是本轮故障点 |
| `src/launcher.js:120` `tryListProcessesByName(GAME_IMAGE)` | **`status` 的进程枚举**（tasklist） | 按 `RimWorldWin64.exe` 枚举 ⇒ 1.7 实例永远「查不到」 |
| `src/launcher.js:133` `listProcessesByPowerShell(GAME_IMAGE)` | **`status` 的进程枚举兜底**（Get-Process） | 同上，兜底也瞎 |
| `src/launcher.js:549-606` `stopGame()`：非 force 只杀记录里的 PID；`--force` 走 `listGameProcessesWithFallback()` | **`stop` 的进程匹配** | 非 force 路（我们用的）靠 PID 不受影响；`--force` 路继承 `:120/:133` 的失效 |
| `src/launcher.js:44-46` `logPathFor()` = `<profile>/Player.log` | 桥接发现 / `logs` / `live --source log` / `waitForBridge` 的日志落点 | **1.7 下这条也不对**：1.7 忽略 `-logFile`，见 §4 |

改后（同一处探测，供核对）：`GAME_IMAGE_CANDIDATES`:`41`、`detectGameImage()`:`56`、`detectGameLayout()`:`95`、`logPathFor()`:`113`、`listGameProcesses()`:`251`、`listGameProcessesWithFallback()`:`258-262`、`startGame` 的 exe 检查:`462-470`。

---

## 3. 改动 diff 摘要（只有 `src/launcher.js`）

`git --no-pager diff --stat src/launcher.js` → **185 行变动（+173 / −12）**；语义上是 5 件事，都围绕「一处探测，多处共用」：

| # | 位置（改后行号） | 原值 → 新值 | 为什么 |
|-|-|-|-|
| 1 | `:41` | `export const GAME_IMAGE = 'RimWorldWin64.exe'` → `export const GAME_IMAGE_CANDIDATES = ['RimWorld.exe', 'RimWorldWin64.exe']` + 新增 `detectGameImage(root)`（`:56`，返回 `{image, exe, found, searched}`） | 1.7 入口改名；`searched` 让报错能列全找过的路径 |
| 2 | `:251` / `:258-262` | `tryListProcessesByName(GAME_IMAGE)` → `tryListProcessesByName(detectGameImage().image)`；`listProcessesByPowerShell(GAME_IMAGE)` → `...(image)` | `status` 与 `stop --force` 的进程匹配跟着同一次探测走，不再各写一套 |
| 3 | `:462-470` | `const exe = path.join(root, GAME_IMAGE); if (!fs.existsSync(exe)) throw …'找不到 RimWorld 可执行文件：<exe>'` → `const layout = detectGameLayout(profile, root)`；`!found` 时抛「已按顺序查找：<两个完整路径>…」 | 启动只认探测结果；两个都不在时报错可诊断 |
| 4 | `:64-120`、`:185-247`、`:505-530`、`:600-612` | 新增 `readGameVersionText()`、`WAYLITH_LOG_DIR`、`detectGameLayout()`（一处决定 exe 名/日志落点/版本）、`logPathFor()` 改为走它；`readBridgeInfo(logFile, {sinceOffset})` 支持「只读本次启动新增的日志」+ `findRecordByLogFile()`；`startGame` 只归档 profile 内的日志、记录 `logStartOffset`/`logShared`/`gameVersion`/`waylith`/`modsConfigStamp` | 1.7 日志落到全机共用的默认存档目录（§4），不修这条链，`start` 永远等不到桥接 |
| 5 | `:145-160`、`:513` | 新增 `stampModsConfigVersion(profile, versionText)`：启动前把 profile 的 `ModsConfig.xml` **`<version>` 那一行**改成游戏自己的版本串（其余字节不动；版本一致时什么都不做） | 1.6→1.7 换版本时游戏会把**整个 mod 清单重置成只有 Core**（§7 日志原文），不追平版本串，人工排好的 14 项会被抹掉 |

**可回滚性**：改动只在一个文件、只新增函数和替换 5 处调用点；CLI 参数、CLI 行为语义、prefs 逻辑、多实例/端口/profile 隔离逻辑都没动。没有留下任何一次性脚本（`.ps1`/`.py` 一个都没落地；`runs/` 下只有证据文件和一份截图用的 `args-screenshot.json` 参数文件）。

---

## 4. 启动过程与等待方式

**命令**（profile 必须显式给，默认 `vanilla` 是 561 项）：

```powershell
node src/cli.js start --profile rhah --width 1600 --height 900 --monitor 2
```

**事件驱动的等待（没有一处 `Start-Sleep` 轮询状态）**：

| 等什么 | 用什么（推送） | 结果【实测】 |
|-|-|-|
| 版本行进日志 | 后台 job：`node src/cli.js live --profile rhah --source log --until 'text~RimBridge' --new-only --timeout 300000` | 5.6s 命中 `[RimBridge] Applied essential Harmony patches.`（Exit 0） |
| 桥接就绪 | `start` 自己的 `waitForBridge`（轮询日志 + TCP 握手，是既有的有界等待，不是新加的 sleep） | 「桥接就绪：127.0.0.1:5174（等待 21.9s，来源=env）」 |
| 到主菜单 | 桥接 `rimworld/get_ui_state` 回读：`programState="Entry"`、`inEntryScene=true`、`hasCurrentGame=false`、`windowCount=0` | 主菜单（未进任何游戏） |
| 兜底 | `start` 的 `waitForBridge` 有界超时（240s，代码既有）；第 1 次运行是先撞上「PID 已退出」这条更早的分支（见 §7），没走到超时那一步 | — |

窗口/焦点：`-monitor 2` 与 `-screen-width/height` 在 1.7 下**被忽略**（Waylith 宿主），窗口开在**主屏**、尺寸约 813×486；取证时我用 Win32 `MoveWindow` 把它挪到副屏再截桌面，桥接截图本身不受影响。

---

## 5. 日志版本行原文【实测】

1.7 的日志不在 `profiles\rhah\Player.log`（那个文件一个字节都没写），而在：

```
C:\Users\MikanseiP\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Logs\Player.log
```

日志头（第 1–8 行）自己给出了落点与命令行：

```
=== Waylith log ===
Started:   2026-10-24 17:55:50.587 +08:00
Version:   1.7.4985.1269
OS:        Microsoft Windows 10.0.26200 (X64)
Runtime:   .NET 10.0.13-waylithclr.dev
Process:   pid 32672
Log file:  C:\Users\MikanseiP\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Logs\Player.log
Command:   D:\SteamLibrary\steamapps\common\RimWorld\RimWorld.dll -savedatafolder=E:\…\profiles\rhah -logFile "…\Logs\Player.log" -screen-fullscreen 0 -screen-width 1600 -screen-height 900 -monitor 2 --doorstop-enabled false
```

**`RimWorld x.y.z revN` 那一行（原文，行 121）**：

```
2026-10-24 17:55:52.081  INFO [Verse] RimWorld 1.7.4985 rev1269
```

配套两条：

```
2026-10-24 17:55:52.083  INFO [Verse] Save data folder overridden to E:\2026.7以后的新工程\DSH\RimWorld_auto_test\profiles\rhah
2026-10-24 17:56:11.723  INFO [Verse] [RimBridge] GABP server connected to GABS on port 5174
```

主菜单截图左上角也印着（`main-menu-1.7-rhah-bridge.png`）：`Version 1.7.4985 (64-bit)` / `Compiled Oct 21 2026` / `Harmony v3.0.1.0`。

**另一个实测特性**：Waylith 日志**每次启动重写**（不是追加）—— 第二次运行的文件从 `=== Waylith log ===` 重新开始；所以 `readBridgeInfo` 的 `sinceOffset` 做了「偏移比文件还大 ⇒ 整份都算本次」的兜底。

---

## 6. mod 兼容性（逐个 packageId 的 `supportedVersions`，本轮亲自读 `About.xml`）

| # | packageId | 来源 | `supportedVersions` 原文 | 含 1.7？ | 1.7 实跑结果【实测】 |
|-|-|-|-|-|-|
| 1 | `brrainz.harmony` | 工坊 2009463077（3.0.1.0） | 1.2 / 1.3 / 1.4 / 1.5 / 1.6 / 1.7 | ✅ | 加载，主菜单显示 `Harmony v3.0.1.0` |
| 2 | `ludeon.rimworld` (Core) | 游戏 `Data\Core` | **该文件没有 `supportedVersions` 段** | —（随游戏） | 加载 |
| 3 | `ludeon.rimworld.royalty` | 游戏 `Data\Royalty` | 1.6 | ❌ | 加载；游戏标 `hasVersionWarning: true` |
| 4 | `ludeon.rimworld.ideology` | `Data\Ideology` | 1.6 | ❌ | 同上 |
| 5 | `ludeon.rimworld.biotech` | `Data\Biotech` | 1.6 | ❌ | 同上 |
| 6 | `ludeon.rimworld.anomaly` | `Data\Anomaly` | 1.6 | ❌ | 同上 |
| 7 | `ludeon.rimworld.odyssey` | `Data\Odyssey` | 1.6 | ❌ | 同上 |
| 8 | `erdelf.HumanoidAlienRaces` | 工坊 839005762 | 0.19–1.6 / 1.7 | ✅ | 加载；日志 `Alien race successfully completed 280 patches (50 pre, 82 post, 148 trans) with harmony.` |
| 9 | `Solaris.RatkinRaceMod` | 工坊 1578693166（NewRatkinPlus） | 1.5 / 1.6 / 1.7 | ✅ | 加载；`LoadFolders` 有 `<v1.7>` |
| 10 | `Nanaloveyuki.IrisMenus` | 工坊 3798502474（1.1.2） | 1.6 / 1.7（`LoadFolders` 有 `<v1.7>`→`1.7` + `Common`，`1.7\Assemblies\IrisMenus.dll` 实际存在） | ✅ | 加载（`loadedInSession: true`，loadOrder 9）——但 RHAH 认不到它，见下 |
| 11 | `nanaloveyuki.oxygendsl` | 工坊 3817603245（0.0.1） | 1.6 / 1.7 | ✅ | 加载；日志 `[Oxygendsl] RHAH behavior adapter registered.` |
| 12 | `brrainz.rimbridgeserver` | 游戏 `Mods\RimBridgeServer`（2.1.1） | **只有 1.6**（`LoadFolders` 只有 `<v1.6>`→`/`+`1.6`，程序集在 `1.6\Assemblies`） | ❌ | **仍然加载并工作**：`[RimBridge] Applied essential Harmony patches` / `GABP server connected to GABS on port 5174`，184 个工具可用 —— 实测行为等价于「1.7 仍采用了 `<v1.6>` 的文件夹声明」（推翻了此前文档里「按声明不会加载」的推测） |
| 13 | `nanaloveyuki.ratkin.hungerandhavoc`（RHAH，1.1.4） | 工坊 3807233439 | 1.6 / 1.7 | ✅ | 加载；`[RHAH] IrisMenus is not active. Menu pages were not registered.`（见下） |
| 14 | `dsh.gamelink` | 游戏 `Mods\DSH_GameLink`（junction→`mods\DSH_GameLink`） | **原本只有 1.6**；另一路工作流在 17:53:35（本次运行**之前**）给它加了 `<li>1.7</li>` 并在 `LoadFolders.xml` 加了 `<v1.7><li>/</li></v1.7>` | 视版本而定 | 加载（无 version 警告），但**从启动到 stop 全程（约 2 分钟主菜单）没写任何事件**（`runs\live\rhah.ndjson` mtime 停在 17:44:41）⇒ 事件通道本次不可用（原因未验证，DLL 是 2026-10-17 的 1.6 编译产物） |

**本轮真正读到的异常（不是「不支持 1.7」的挡路，但对后续测试有影响）**：

1. `[RHAH] IrisMenus is not active. Menu pages were not registered.`（17:55:54.387）—— IrisMenus 自身已加载，RHAH 的兼容检查仍判它「未激活」，RHAH 的 IrisMenus 菜单页在 1.7 下**没有注册**。原因（RHAH 用的判据）**未验证**。
2. `ERROR [Verse] Failed to find Verse.TerrainDef named AncientHeavyBridge. There are 383 defs of this type loaded.`（17:55:57.053，栈 `RimWorld.DefOfHelper.BindDefsFor` → `GenDefDatabase.GetDef`）—— 我扫了 91 个 DLL，这个 DefOf 字段只存在于**原版** `Assembly-CSharp.dll`，不在任何 mod 里 ⇒ **原版 1.7 侧的缺失 TerrainDef**，与本 profile 的 mod 无关。
3. `WARN [Verse] Key binding conflict: RK_OpenInfoCard and Misc9 are both bound to I.`（鼠族快捷键与 Misc9 撞车，纯提示）。
4. RimWorld 的 mod 配置层（`rimworld/get_mod_configuration_status`，已落盘 `mod-configuration-status.json`）报 `activeModCount: 14 / loadedSessionModCount: 14 / versionWarningCount: 6 / unsatisfiedDependencyCount: 2 / orderingIssueCount: 0`。两条 `unsatisfiedDependencies` 是**按显示名**匹配出的假警报：`Solaris.RatkinRaceMod → "Humanoid Alien Races 2.0"`、`RHAH → "NewRatkinPlus[1.6]" + "Iris Menus"`（实际对应 mod 已激活、包名不同）。**未验证**它们是否影响玩法。

---

## 7. 失败时的报错原文【实测】

**第 1 次运行（17:50:41，14 项清单被游戏重置前的状态）——`start` 失败，原文**：

```
[错误] 游戏进程 29504 已退出，但桥接始终没出现。日志尾部：
(日志为空)
```

根因不是崩溃，是**两条链同时断**（都有日志/文件证据）：

```
2026-10-24 17:50:46.078  INFO [Verse] Mods config data is from version 1.6.4871 rev591 while we are running 1.7.4985 rev1269. Resetting.
```

（游戏把 profile 的 `ModsConfig.xml` 重置成 `activeMods` 只剩 `ludeon.rimworld`，并把 `<version>` 改写成 `1.7.4985 rev1269`；原件与重置后文件都已落盘存档）
同时 1.7 忽略了 `-logFile`：`profiles\rhah\Player.log` **不存在**，`start` 的 `waitForBridge` 没有日志可读 ⇒ 一直等到我 `stop`（0 字节日志 ⇒ 报「日志为空」）。

**改前那处 exe 报错的原文（代码里，本轮未再复现运行）**：

```
找不到 RimWorld 可执行文件：D:\SteamLibrary\steamapps\common\RimWorld\RimWorldWin64.exe
请设置环境变量 RIMWORLD_ROOT 指向游戏安装目录。
```

**改后的同一个失败场景（用假 `RIMWORLD_ROOT` 实跑）**：

```
[错误] 找不到 RimWorld 可执行文件。已按顺序查找：
  - C:\__no_such_rimworld__\RimWorld.exe
  - C:\__no_such_rimworld__\RimWorldWin64.exe
请设置环境变量 RIMWORLD_ROOT 指向游戏安装目录（当前解析为 C:\__no_such_rimworld__）。
```

**第 2 次运行（17:55:50，14 项清单 + `<version>1.7.4985 rev1269</version>`）—— `start` 成功**，无失败报错。

---

## 8. 未验证 / 明确没做

- **没开始新游戏、没读任何存档**（按要求本轮只验证能起）；因此「1.7 下开新殖民地 / 读 1.6 存档升级」全部未验证。
- **`voiced` profile 与「最小 mod」降级路径没有启用**（`rhah` 一次就过）。
- **1.6 分支没有实机验证**（本机 1.6 已被 Steam 删掉）。只用临时目录做过逻辑验证（`Version.txt=1.6.4871 rev591` + 只有 `RimWorldWin64.exe`）：`image=RimWorldWin64.exe`、`waylith=false`、日志落回 `<profile>\Player.log`，夹具用完即删。
- **`dsh.gamelink` 在 1.7 下为什么不产事件未查**（只测到「14 项都 `loadedInSession`，但 `runs\live\rhah.ndjson` 在从启动到 stop 的约 2 分钟里没更新」）。⇒ 目前 `live` 只能用 `--source log`；事件驱动等「进图后」的状态在 1.7 下**还没有可用通道**。
- **`--force` 路未验证**（按要求不加 `--force`；非 force 路只杀记录 PID，实测干净）。
- **多实例并发**（两个 profile 同时跑）在 1.7 下未验证；特别是 1.7 日志是**全机共用**的一个文件，两个 1.7 实例会互相覆盖日志（`logStartOffset`/`findRecordByLogFile` 只是让步处理，没实测过并发）。
- **`-monitor 2` / `-screen-width/height` 在 1.7 是否还能生效**：实测窗口开在主屏、约 813×486 ⇒ 命令行参数被忽略；`Prefs.xml`（launcher 每次强制写 1600×900）**是否被 1.7 采用未验证**（截图是 1600×900，但那是桥接渲染尺寸，不能当作证据）。
- **1.7 unstable 的原版缺陷** `AncientHeavyBridge` 缺失 TerrainDef 是否影响开局未验证。
- 没有改 `brrainz.rimbridgeserver` / `dsh.gamelink` 的任何文件（人类未授权）。
- 没有更新 `STATUS.md`（工作区里另一路工作流同时在写，避免撞车）。

---

## 9. 证据文件（`runs\rhah-1.7-startup-20261024\`）

| 文件 | 是什么 |
|-|-|
| `main-menu-1.7-rhah-bridge.png` | **主菜单截图（桥接 `rimworld/take_screenshot`，1600×900）**，左上角印 `Version 1.7.4985` |
| `main-menu-1.7-rhah-display2.png` | 桌面截图（窗口挪到副屏）：主菜单 + Debug log 窗口（可读的 mod 加载消息） |
| `debug-log-window-zoom.png` | 上图的 Debug log 区放大：`RimWorld 1.7.4985 rev1269` / `Mods config data is from version 1.6.4871 rev591 … Resetting.` |
| `main-menu-1.7-rhah.png` / `-fg.png` / `-topmost.png` | 第 1 次运行（被浏览器压住）的桌面截图，保留作过程证据 |
| `waylith-player-log-run1-core-only.log` | 第 1 次运行完整日志（92,184 B，仅 Core） |
| `waylith-player-log-run2-14mods.log` | **第 2 次运行完整日志（14 项 mod）** |
| `ModsConfig-after-1.7-reset.xml` | 游戏自动重置后的 `ModsConfig`（只剩 Core），留档 |
| `ModsConfig-restored-14mods-1.7.xml` | 我恢复的 14 项 + 1.7 版本串（现役状态） |
| `status-while-running.json` | 运行中的 `node src/cli.js status --profile rhah` |
| `mod-configuration-status.json` | 桥接 `rimworld/get_mod_configuration_status` 全量输出（14 项逐条的 versionCompatible / 警告） |
| `start-console.txt` / `start-console-run2-14mods.txt` | 两次 `start` 的控制台输出（第 1 次失败原文 / 第 2 次成功） |
| `stop-console.txt` | `stop --profile rhah` 输出（`forced:false`） |
| `args-screenshot.json` | 截图工具的调用参数（PowerShell 会吃掉内联 JSON 的引号，所以走 `--args-file`） |
