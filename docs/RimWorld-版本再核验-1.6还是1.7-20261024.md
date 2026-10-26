# RimWorld 版本再核验：1.6 还是 1.7（2026-10-24，只读取证）

> 取证窗口：2026-10-24 17:35–17:48（Asia/Shanghai）。
> **本文件有一条时间线，不是单一结论**：17:46 之前这台机器是 1.6，17:46 Steam 把安装换成了 1.7 unstable 分支。
> 标记约定：**【实测】**= 直接读到的文件/注册表/进程内容；**【推断】**= 由实测推出，未直接读到。

---

## 结论（5 行）

1. **【实测】此刻安装的是 1.7**：`D:\SteamLibrary\steamapps\common\RimWorld\Version.txt` = `1.7.4985 rev1269`，`Assembly-CSharp.dll` FileVersion = `1.7.9790.38077`，且本机 appmanifest 出现了 `"BetaKey" "unstable"`。
2. **【实测】17:46 之前它是 1.6**：我在 17:35–17:45 读到的同一份安装是 `1.6.4871 rev590`（Version.txt）/ 运行时打印 `1.6.4871 rev591` / `Assembly-CSharp 1.6.9676.17735` / 无 `BetaKey` / buildid 23969874 / 577,318,615 B。17:46:24–17:46:37 Steam 把它换成了 buildid 26115268、647,288,980 B 的 1.7。
3. **【实测】玩家目前为止真正玩过的全是 1.6.4871 rev591**：190/190 个存档是 1.6.x（最新一个 `个人主义协定.rws`，2026-10-24 16:10:56），`Player.log` 第 25 行 `RimWorld 1.6.4871 rev591`、最后写入 16:48:28；**磁盘上没有任何 1.7 存档、没有任何 1.7 日志**。
4. **【推断】玩家说「1.7 有重大优化，我体验到了」**，与 1.7 的事实相符（1.7 = 换自研 Waylith 引擎，官方宣传最高约 4 倍性能），但**他截至 16:48 的游玩记录里没有 1.7 的痕迹**；最可能是他刚把 Steam 切到 `unstable` 分支（17:46 这次切换），或把模组/公告里的 1.7 当成了自己已经在玩的版本。
5. **【实测+推断】1.7 现在可以用来构建 1.7 程序集**（游戏根目录同时有 `Assembly-CSharp.dll 1.7.9790.38077` + `UnityStubs.dll` + `Waylith.dll`，正好是仓库 `Directory.Build.props` 对 1.7 的前置要求），但默认 `RimWorldDir`（`E:/Apps/Steam/...`）在本机不存在，必须显式覆盖。

---

## 一、安装清单（全部 Steam 库 + 非 Steam 副本）

### 1.1 Steam 库的认定来源【实测】

| 来源 | 读到的原文 |
|-|-|
| `HKCU\Software\Valve\Steam` → `SteamPath` | `c:/program files (x86)/steam` |
| `HKLM\SOFTWARE\WOW6432Node\Valve\Steam` → `InstallPath` | `C:\Program Files (x86)\Steam` |
| `C:\Program Files (x86)\Steam\steamapps\libraryfolders.vdf` | 两个库：`"0"` = `C:\\Program Files (x86)\\Steam`（apps 只有 228980、250820），`"1"` = `D:\\SteamLibrary`（apps 列表含 `"294100" "577318615"`） |
| `D:\steamcmd\steamapps\libraryfolders.vdf` | 独立 steamcmd 根：`"path" "D:\\steamcmd"`，`apps { }` **为空**；该 `steamapps` 下只有 `workshop\` 和 `libraryfolders.vdf`，**没有 `common\`、没有 294100 清单** |
| `E:\SteamLibrary`、`K:\SteamLibrary` | 目录存在（各有 `libraryfolder.vdf` + `steam.dll`），但**未登记在上面任何 `libraryfolders.vdf`** → 残留/未挂载库 |

### 1.2 库与安装表【实测】

| # | 位置 | RimWorld 是否存在 | 证据 |
|-|-|-|-|
| A | `D:\SteamLibrary\steamapps\common\RimWorld` | **是（唯一一份）** | `appmanifest_294100.acf` 存在；`Version.txt` 现为 `1.7.4985 rev1269`；`RimWorld.exe` FileVersion `1.7.4985.1269` |
| B | `C:\Program Files (x86)\Steam\steamapps\common` | 否 | 该库 apps 只有 228980 / 250820；全盘 exe 扫描无命中 |
| C | `D:\steamcmd\steamapps` | 否 | 只有 `workshop\` + `libraryfolders.vdf`，无 `common\`，无 `appmanifest_294100.acf` |
| D | `E:\SteamLibrary\steamapps\common` | 否 | 只有 3DMark Demo、KingdomComeDeliverance2、Mount & Blade II Bannerlord、Phasmophobia、The Escapists 2、The Witcher 3、Titanfall2 |
| E | `K:\SteamLibrary\steamapps\common` | 否 | 15 个游戏（Borderlands 3、Cyberpunk 2077、Fallout 4、Unturned…），`steamapps` 里只有 `appmanifest_304930.acf` |
| F | 全盘 `RimWorldWin64.exe`（**不限深度**，C/D/E/K） | 仅 2 处命中 | ① `D:\SteamLibrary\steamapps\common\RimWorld\RimWorldWin64.exe`（666,624 B，2026-10-11 12:57:33）——**注意：17:46 后该文件已被删除**；② `C:\Users\MikanseiP\AppData\Local\AMD\CN\GameReport\RimWorldWin64.exe`，**0 字节**、无版本信息，是 AMD 显卡驱动的游戏识别残留 |
| G | 全盘 `Assembly-CSharp.dll` | RimWorld 的只有 D 那一份 | 其余命中全是别的游戏（Unturned、CleanFall…）或创意工坊 mod 的 `obj/` 目录 |
| H | 全盘 `UnityStubs.dll` | **17:46 前：一处都没有**；17:46 后：只有 `D:\...\RimWorld\UnityStubs.dll`（963,072 B） | 全深度扫描 C/D/E/K |
| I | 全盘 `Waylith.dll` | 17:46 前无；17:46 后 `D:\...\RimWorld\Waylith.dll`（1,562,624 B，mtime 17:46:24） | 同上 |
| J | 注册表卸载项 `DisplayName -match RimWorld` | 无 | HKLM WOW6432Node\...\Uninstall 与 HKCU\...\Uninstall 均无命中 → **没有非 Steam 安装版** |
| K | 第二套 Ludeon 数据目录 | 无 | `LocalLow` 下只有一个 `Ludeon Studios\RimWorld by Ludeon Studios`；`Roaming` / `Local` / `ProgramData` 均无 Ludeon 或 RimWorld 目录 |

### 1.3 appmanifest 前后对比（同一文件，`D:\SteamLibrary\steamapps\appmanifest_294100.acf`）【实测】

| 字段 | 17:35 读到（=`2` 小节记录） | 17:47 读到 |
|-|-|-|
| `StateFlags` | `4` | `4` |
| `buildid` | `23969874` | `26115268` |
| `LastUpdated` | `1791694672` → 2026-10-11 12:57:52 | `1792835196` → 2026-10-24 17:46:36 |
| `LastPlayed` | `1792832514` → 2026-10-24 17:01:54 | `1792835099` → 2026-10-24 17:44:59 |
| `SizeOnDisk` | `577318615` | `647288980` |
| `InstalledDepots.294104.manifest` | `3464668865934009251` | `2434112405223082685` |
| `betas` / `BetaKey` | **无任何 betas 段**，`UserConfig` 只有 `"language" "schinese"` | `"UserConfig" { "language" "schinese" "BetaKey" "unstable" }`；`"MountedConfig"` 同样有 `"BetaKey" "unstable"` |
| 文件 mtime | 2026-10-24 17:01:54 | 2026-10-24 17:46:37 |

**17:46 后的安装内容（实测原文/属性）**

- `Version.txt`（18 B，mtime 17:46:29）：`1.7.4985 rev1269`
- 游戏根 `Assembly-CSharp.dll`（15,632,896 B，mtime 17:46:24）：FileVersion `1.7.9790.38077`，`ProductVersion` `1.7.9790.38077+535691cc7c6279fe45ab70da06a06f89e46297f6`，`AssemblyName.Version` = `1.7.9790.38077`；其引用表含 `UnityStubs 1.0.0.0`、`Waylith 1.0.0.0`、`System.Runtime 10.0.0.0`
- 新入口 `RimWorld.exe`（198,656 B，mtime 17:46:28）：FileVersion `1.7.4985.1269`；`RimWorld.dll` 同为 `1.7.4985.1269`
- 新增：`UnityStubs.dll`、`Waylith.dll` + `Waylith.UnityCompat/AssetBake/VideoDecode.dll`、`Brutal.*` 系列、`coreclr.dll`/`hostfxr.dll`/`System.Private.CoreLib.dll`（.NET 10）、`Rendering\`、`vulkan\`、`Assets.zip`（358,892,126 B）
- 删除：`RimWorldWin64.exe`、`UnityPlayer.dll`、`UnityCrashHandler64.exe`；`RimWorldWin64_Data\` 只剩一个 `Plugins`（2026-04-13）
- 保留的旧注入件：`Doorstop.dll`（2026-08-01）、`winhttp.dll`（2026-08-01）、`doorstop_config.ini`——是否仍对 .NET 10 生效**未验证**

**17:46 前的安装内容（实测原文/属性）**

- `Version.txt`（mtime 2026-10-11 12:57:47）：`1.6.4871 rev590`
- `RimWorldWin64.exe`：FileVersion `2022.3.35.70150`，ProductVersion `2022.3.35f1 (011206c7a712)` → Unity 2022.3.35f1（1.6 是 Unity 版；1.7 才是 Waylith）
- `RimWorldWin64_Data\Managed\Assembly-CSharp.dll`（15,777,280 B）：FileVersion `1.6.9676.17735`，**全文扫不到任何 `1.7.x` 版本串**
- 游戏自报版本 → 见下节日志：`RimWorld 1.6.4871 rev591`。**【推断】** `Version.txt` 的 `rev590` 是过期文件（同目录 `Assembly-CSharp.dll` 与运行日志都是 591 线），`rev` 差异不影响「1.6 / 1.7」的判定。
- 配置侧（同一时间窗口）【实测】：`Config\LastPlayedVersion.txt` = `1.6.4871`；`Config\ModsConfig.xml` 第 3 行 `<version>1.6.4871 rev591</version>`；`Config\` 下另有 4 份历史备份（`backup_ModsConfig.xml`、`ModsConfig.备份-切收藏前-20261017.xml`、`ModsConfig.备份-清理前-20260921.xml`、`ModsConfig.备份-纯净档-20261002.xml`、`ModsConfig.备份-装桥前-20260925.xml`）

---

## 二、存档版本统计（`LocalLow\...\Saves\`，明文 XML 读 `<gameVersion>`）

扫描范围：`C:\Users\MikanseiP\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\` 下**递归**全部 `.rws` = **190 个**（`Saves\` 无子目录，全数据目录递归也只有这 190 个）。

| `<gameVersion>` | 存档数 | 占比 |
|-|-|-|
| `1.6.4633 rev1261` | 118 | 62.1% |
| `1.6.4871 rev591` | 63 | 33.2% |
| `1.6.4850 rev646` | 9 | 4.7% |
| **合计** | **190** | 100% |
| **含 `1.7` 的存档** | **0** | **0%** |

**最新 5 个存档**【实测】

| 修改时间 | `<gameVersion>` | 文件名 |
|-|-|-|
| 2026-10-24 16:10:56 | `1.6.4871 rev591` | `个人主义协定.rws` |
| 2026-10-24 16:10:51 | `1.6.4871 rev591` | `Autosave-1.rws` |
| 2026-10-24 16:10:21 | `1.6.4871 rev591` | `Autosave-4.rws` |
| 2026-10-24 16:09:11 | `1.6.4871 rev591` | `Autosave-5.rws` |
| 2026-10-24 16:08:25 | `1.6.4871 rev591` | `Autosave-3.rws` |

最老 3 个：`real first story.rws`（2026-04-14 03:23:53）等，均 `1.6.4633 rev1261`。
**结论**：存档序列是连续的一条 1.6 线（4633 → 4850 → 4871），**没有任何跨到 1.7 的痕迹**。

---

## 三、日志版本行清单

`LocalLow\...\RimWorld by Ludeon Studios\` 下 `.log` 只有 4 个，无归档日志目录：

| 文件 | 修改时间 | 版本行原文（行号） |
|-|-|-|
| `Player.log` | 2026-10-24 16:48:28 | 第 24 行 `Command line arguments: -disable-compute-shaders -popupwindow --doorstop-enabled false`；第 25 行 **`RimWorld 1.6.4871 rev591`** |
| `Player-prev.log` | 2026-10-24 09:54:40 | 第 24 行同上；第 25 行 **`RimWorld 1.6.4871 rev591`** |
| `HaulersDream-debug.log` | 2026-10-24 09:54:39 | 无版本行（模组日志，第 20–30 行是 `DBG ...` 搬运日志） |
| `HaulersDream-debug.prev.log` | 2026-10-23 22:45:20 | 无版本行（模组异常栈，第 24 行 `System.InvalidOperationException: Operations that change non-concurrent collections ...`） |

补充【实测】：
- `Player.log` 里 **`1.7.` 出现次数 = 0**；`RHAH` 相关行 43 行（如 `[RHAH] IrisMenus 1.1.2 pages registered.`）→ 模组在 1.6 下正常工作。
- 自动化测试 profile 的日志 `profiles\rhah\Player.log`（mtime 17:40:39）第 25 行同样 **`RimWorld 1.6.4871 rev591`**，命令行含 `-savedatafolder=...\profiles\rhah`。
- 17:47:55 复查：`Player.log` 仍是 16:48:28 那一份，**切换 1.7 后游戏尚未被启动过**。

---

## 四、模组侧证据（`workshop\content\294100\3807233439` + 仓库）

| 项 | 实测原文/值 |
|-|-|
| `About\About.xml` `supportedVersions` | `<li>1.6</li>`、`<li>1.7</li>` |
| `LoadFolders.xml` | 有 `<v1.6>` 与 `<v1.7>` 两个块；`<v1.7>` 首项是 `<li>1.7/Guard</li>` |
| `1.7\Assemblies\HungerAndHavoc.dll` | 868,352 B，mtime **2026-10-24 08:40:00**；引用 **`Assembly-CSharp 1.7.9790.38077`** |
| `1.6\Assemblies\HungerAndHavoc.dll` | 848,384 B，mtime **2026-10-16 22:15:02**；引用 **`Assembly-CSharp 1.6.9438.37837`** |
| 仓库 `RimworldMods\ratkin_hunger_and_havoc\1.7\Assemblies\HungerAndHavoc.dll` | 868,352 B；引用 **`Assembly-CSharp 1.7.9790.38077`** |
| 反射方法 | 各 DLL 在**独立进程**里 `[Reflection.Assembly]::ReflectionOnlyLoadFrom(...).GetReferencedAssemblies()`（同一进程内二次加载同标识程序集会报 "已从其他位置加载"，故逐个进程做） |

**关键点【实测】**：模组 1.7 DLL 引用的 `1.7.9790.38077`，与 17:46 之后游戏根目录的 `Assembly-CSharp.dll` 版本**逐位相同**。也就是说这份 1.7 DLL 就是针对现在这台机器上的这条 1.7 线编译的。

**仓库构建配置【实测】**

- `Directory.Build.props`：
  - `<RimWorldVersion Condition="'$(RimWorldVersion)' == ''">1.7</RimWorldVersion>`（**默认目标就是 1.7**）
  - 1.7 时 `<RimWorldManagedDir>$(RimWorldDir)</RimWorldManagedDir>`（**游戏根目录**，不是 `RimWorldWin64_Data\Managed`）
  - `<RimWorldDir ...>E:/Apps/Steam/steamapps/common/RimWorld</RimWorldDir>` ← **本机 `E:\Apps` 不存在**（`Test-Path E:\Apps` = `False`）
  - 前置检查原文：`RimWorld 1.7 requires UnityStubs.dll at the game root.`；`Missing game assembly: $(RimWorldManagedDir)/Assembly-CSharp.dll`
- `docs\agents\automation.md` 第 36 行：`1.7 游戏程序集来自游戏根目录，Harmony 与可选 IrisMenus 使用各自 1.7/Assemblies`；`RIMWORLD_VERSION`（默认 `1.7`）
- `README.md` 第 226 行：`默认构建和部署 RimWorld 1.7，使用游戏根目录的 Assembly-CSharp.dll、UnityStubs.dll 与对应 1.7 前置`；第 39 行：`此版本已部署至本机 1.7 Mods，保留 1.6 已编译内容。`
- 仓库里**没有**任何 `beta` / `unstable` 字样（对 `*.md/*.props/*.targets/*.sh/*.ps1/*.json/*.csproj` 全量 grep，只命中 `RimWorldDir` 之类），也没有把 1.7 说成「测试分支」的说明
- `~\.nuget\packages` 里**没有** `Krafs.Rimworld.Ref`（只有 lib.harmony、lib.harmony.ref、microsoft.netcore.platforms、netstandard.library、system.reflection.emit 5 个包）→ 1.7 引用不可能来自 NuGet 参考包

**外部旁证（网络，非本机文件）**：2026-10-12 官方公告 1.7 迁移到自研 Waylith 引擎，媒体标题称性能最高约 4 倍 —— 与玩家「1.7 有重大优化」的说法吻合（[Steam 公告](https://store.steampowered.com/news/app/294100/view/672882362547178633)、[Notebookcheck](https://www.notebookcheck.net/Popular-Steam-game-abandons-Unity-for-new-engine-promising-up-to-4x-better-performance.1422995.0.html)、[Game8](https://game8.co/articles/latest/rimworld-now-uses-its-faster-engine-four-times-more-efficient-than-unity)）。**1.7 是否只走 `unstable` 分支发布：未从一手页面核实**（SteamDB 拒绝抓取）。

---

## 五、进程证据

| 时间 | 实测 |
|-|-|
| 17:35:31 | `PID 24848`，`ExecutablePath = D:\SteamLibrary\steamapps\common\RimWorld\RimWorldWin64.exe`，`CreationDate = 2026/10/24 17:01:53`，`CommandLine = ... -savedatafolder=E:\2026.7以后的新工程\DSH\RimWorld_auto_test\profiles\rhah -logFile ...\profiles\rhah\Player.log -screen-fullscreen 0 -screen-width 1600 -screen-height 900 -monitor 2 --doorstop-enabled false` |
| 17:38:08 | 上一条进程消失，新起 `PID 20752`，同一 exe 路径、同一 `-savedatafolder=...\profiles\rhah` 命令行，`CreationDate 2026/10/24 17:38:08` |
| 17:47:55 | **没有任何** `RimWorldWin64.exe` / `RimWorld.exe` 进程（旧 exe 已被 Steam 删除，新 exe 未被启动） |

【实测】这两条进程都是**自动化测试实例**（`-savedatafolder=...\profiles\rhah`、`-monitor 2`），不是玩家自己那一局；玩家那一局在 16:48 已结束（`Player.log` 停在那里）。
【实测】`HKCU\...\Shell\MuiCache` 里 RimWorld 只登记过一个路径：`D:\SteamLibrary\steamapps\common\RimWorld\RimWorldWin64.exe.FriendlyAppName`。
【实测】`C:\Program Files (x86)\Steam\logs\content_log.txt`（3,124,040 B）里 `294100` 的最近记录：`[2026-10-24 17:38:08] AppID 294100 state changed : Fully Installed,App Running,` → `[17:46:37] AppID 294100 state changed : Fully Installed,Update Queued,` → `Fully Installed,`；全文件 `betakey` / `unstable` 命中数 = **0**（该日志不记录分支名）。

### 时间线（把上面的实测按分钟排好）

| 时刻 | 事件 | 证据 |
|-|-|-|
| 16:10:56 | 玩家最后一次存档 | `个人主义协定.rws`，`<gameVersion>1.6.4871 rev591` |
| 16:48:28 | 玩家那一局退出 | `Player.log` mtime + 第 25 行 `RimWorld 1.6.4871 rev591` |
| 17:01:53 / 17:38:08 | 自动化实例两次启动（同一 1.6 安装） | 进程命令行 |
| 17:35–17:45 | 我读到的仍是 **1.6**：`Version.txt 1.6.4871 rev590`、`Assembly-CSharp 1.6.9676.17735`、manifest 无 `BetaKey`、buildid 23969874 | 本文 1.3 / 2 / 3 节 |
| 17:44:59 | 游戏最后一次被 Steam 记为 LastPlayed | `LastPlayed 1792835099` |
| **17:46:24–17:46:37** | **Steam 把内容换成 1.7 unstable**：新增 Waylith/UnityStubs/.NET 10、删除 `RimWorldWin64.exe`、写 `BetaKey "unstable"`、buildid 26115268 | 文件 mtime 17:46:24–17:46:32、`Version.txt` 17:46:29、acf 17:46:37、`LastUpdated 1792835196` |
| 17:47:55 | 无进程；玩家日志/存档仍是 1.6 | 复查 |

**归因说明**：本子代理全程只做读取（`Get-Content` / `Get-ChildItem` / `Get-Item` / `Get-CimInstance` / 反射），**没有执行任何 Steam 操作、没有启动或停止游戏**。这次分支切换发生在 17:46，**触发者未能查明**（最可能是有人在 Steam 客户端里切了 `unstable` 分支，或此前已在客户端排队的切换在游戏退出后落地）。

---

## 六、「玩家为何认为是 1.7」的可能解释

按可信度排序，前两条有本机证据支撑：

1. **他确实刚切到了 1.7（现在为真）**。17:46:37 后本机安装 = 1.7.4985 rev1269 / `BetaKey "unstable"`。如果他是在 17:46 前后自己在 Steam 里切的，那他的「我在玩 1.7」从现在起是对的，但**截至 16:48 的游玩记录没有任何 1.7 痕迹**（190 个存档全 1.6、日志全 1.6）。
2. **他把「模组的 1.7 分支」当成「游戏的 1.7」**。他是这个 mod 的作者侧：仓库默认构建目标就是 1.7（`Directory.Build.props`），README 写「已部署至本机 1.7 Mods」「1.7 离线回归 709 项通过」，创意工坊包内同时带 `1.6/` 与 `1.7/`，`About.xml` 同时声明 `1.6`+`1.7`。这些「1.7」都在讲模组产物，不是游戏本体。
3. **他在另一台机器/另一份安装上玩 1.7**。`Directory.Build.props` 默认 `RimWorldDir` 是 `E:/Apps/Steam/steamapps/common/RimWorld`（另有 `/mnt/e/Apps/Steam/...` 的 WSL 版本），**本机没有 `E:\Apps`**，说明这套路径来自另一种机器布局；README 的「本机 1.7 Mods」也可能指那台机器。
4. **他把 1.7 的引擎优化（Waylith，官方称最高约 4×）当成自己已经体验到的效果**，而实际体感来自他的性能类 mod（日志与配置里有 PerformanceOptimizer、Dubs Performance Analyzer、SlowPawnTickRate 等）。这一条属**【推断】**，无直接证据。
5. **他曾经切到 unstable 又切回 stable**（1.6 的存档/日志就是切回后留下的）。**【推断】**但缺证据：`content_log.txt` 里 0 处 `betakey`/`unstable`，无法证明也无法否证历史切换；能确定的只是 17:35 那一刻 manifest 里**没有** `BetaKey`。

已排除：磁盘上另有 1.7 安装（全盘 exe / Assembly-CSharp / UnityStubs 三路扫描都只有 D 那一份）、第二套 Ludeon 数据目录（只有一处）、非 Steam 便携版（注册表卸载项为空、全盘无第二份 exe）。

---

## 七、现在有 1.7，能否构建 1.7 程序集

**【实测】可以，但必须覆盖路径。**

| 前置 | 要求（`Directory.Build.props` / `Source\HungerAndHavoc.csproj`） | 本机现状 |
|-|-|-|
| 游戏根 `Assembly-CSharp.dll` | 必须存在 | ✅ `D:\SteamLibrary\steamapps\common\RimWorld\Assembly-CSharp.dll`，`1.7.9790.38077` |
| 游戏根 `UnityStubs.dll` | 1.7 必须存在，否则 `<Error>` | ✅ `963,072 B`，mtime 17:46:24 |
| `Waylith.dll`（第三方文档记载的 1.7 依赖） | — | ✅ `1,562,624 B` |
| `RimWorldDir` | 默认 `E:/Apps/Steam/steamapps/common/RimWorld` | ❌ 不存在 → 需 `-p:RimWorldDir="D:\SteamLibrary\steamapps\common\RimWorld"` 或设 `RIMWORLD_DIR` |
| .NET SDK | `global.json` 要求 `10.0.401`（rollForward latestPatch） | ✅ `dotnet --list-sdks` → `10.0.401 [C:\Program Files\dotnet\sdk]` |
| 1.6 目标 | 需要 1.6 游戏目录 | ❌ 本机已无 1.6 安装（17:46 被替换）；`--rimworld-version 1.6` 现在无法指向任何目录 |

**17:46 之前**这一栏全是 ❌：全盘没有 `UnityStubs.dll`、没有 `Waylith.dll`、没有 1.7 的 `Assembly-CSharp`，NuGet 也没有 Krafs 参考包 → 那时在本机**不可能**构建 1.7；现有的两份 1.7 DLL（工坊 + 仓库，均引用 `1.7.9790.38077`）只能是在别的机器/别的 1.7 安装上编译的。

**副作用（给自动化用）**：
- `src\launcher.js` 第 33 行硬编码 `export const GAME_IMAGE = 'RimWorldWin64.exe'`，第 329–332 行用它拼可执行文件路径并在缺失时报错 → **1.7 下该文件不存在，`live`/启动/`stop` 的按名匹配都会失效**（新入口是 `RimWorld.exe`）。默认根目录 `D:\SteamLibrary\steamapps\common\RimWorld`（第 34 行）仍然正确。
- 旧的注入件（`Doorstop.dll`、`winhttp.dll`、`doorstop_config.ini`，2026-08-01）仍在游戏根目录，1.6 时代靠它加载 RimBridgeServer 之类；在 .NET 10 + Waylith 下**是否仍生效未验证**。
- 1.6 重建产物（`RimworldMods\ratkin_hunger_and_havoc\1.6\Assemblies\HungerAndHavoc.dll`）现在**没有 1.6 本体可实测**了。

---

## 八、未验证 / 不能下的结论

1. **17:46 那次分支切换是谁触发的**：只知发生在 17:46:24–17:46:37，`content_log.txt` 不记录分支名，Steam 客户端 UI 操作无本地日志可查。
2. **玩家是否在 8 月–10 月间用过 1.7 玩过**：`content_log.txt` 不含 `betakey`/`unstable`，无法回溯历史分支；存档与日志只能说「留下的记录全是 1.6」。
3. **1.7 是否只通过 `unstable` 测试分支分发**：只有二手媒体/公告标题，一手发布说明页未能抓到正文（Steam 新闻页是 JS 渲染、SteamDB 403）。
4. **Doorstop/BepInEx 式注入在 1.7（.NET 10 + Waylith）下能否工作**：未启动游戏，未验证。
5. **1.7 下模组是否真能跑**：`1.7\Assemblies\HungerAndHavoc.dll` 与游戏程序集版本逐位匹配，但**未启动游戏**，未验证加载与运行。
6. **`Version.txt` 的 `rev590` 与运行时 `rev591` 的差异成因**：只记录到差异（17:46 前），未追查（推测是 `Version.txt` 未被同一次更新刷新）。
7. **存档文件是否含 1.7 专属字段**：只读了 `<gameVersion>`，未做字段级 diff（结论「无 1.7 存档」仅基于版本行）。
8. 之前那份同类取证 `docs\RimWorld-实际版本取证-20261024.md`（16:33 生成）结论「1.7 在磁盘上没有任何痕迹」**在 17:46 之后已过期**，以本文件为准。
