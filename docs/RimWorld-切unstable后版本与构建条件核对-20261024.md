# RimWorld 切 unstable 后版本与构建条件核对（2026-10-24 17:47–17:50 Asia/Shanghai）

> 只读取证 + 一次隔离实例启动尝试（失败）+ 停止确认。
> **未打开/未加载/未保存玩家任何存档**（`Saves\` 一个文件都没读）；**未改游戏安装**；**未切 Steam 分支**；**未改工程代码**（按派活方要求，本轮只诊断不改）。
> 取证现场原文落在 `runs/rimworld-1.7-version-check-20261024/`。
> 全文区分 **实测**（读到的文件内容/命令输出）与 **推测**（基于机制的推理）。

---

## 结论（5 行）

1. **版本号 = `1.7.4985 rev1269`**（`Version.txt` 原文；`RimWorld.exe` FileVersion `1.7.4985.1269` 一致）。
2. **是真 1.7，不是假切换**：Steam 分支 `BetaKey "unstable"`、`buildid 26115268`、`StateFlags 4`、`LastUpdated` = 2026-10-24 17:46:36；本体内核已换成 **.NET 10 + Waylith（coreclr 自包含，tfm net10.0）**，`Assembly-CSharp.dll` FileVersion `1.7.9790.38077`。
3. **实例在 1.7 下没能起来 —— 但阻断点不是 mod 报错，而是测试工程写死了旧 exe 名**：`src/launcher.js:33` 的 `RimWorldWin64.exe` 在 1.7 已被删除，`start` 在起进程之前就抛「找不到 RimWorld 可执行文件」（退出码 1）。**因此没有截图、没有 1.7 的 `Player.log` 版本行**（全盘不存在 `RimWorld 1.7…` 日志行）。
4. **本地编译 1.7 的引擎侧条件已具备**：net10.0 SDK `10.0.401`、1.7 `Assembly-CSharp.dll`（net10.0）+ `UnityStubs.dll` 都在游戏根目录、Harmony 1.7 就位。**缺的是 Oxygendsl 1.7 构建产物**（`oxygen_dsl_framework/tmp/build/1.7` 整个目录不存在），另有 **游戏路径默认值写错**（props 默认 `E:/Apps/Steam/...`，本机是 `D:\SteamLibrary\...`）。
5. **弃用/风险**：①1.6 存档（~190 个）一旦被 1.7 载入即会升级，不可逆；②`RimBridgeServer`、`DSH GameLink`、`VoiceDeputy` 三者 About 只声明 1.6 且 LoadFolders 只有 `<v1.6>` ⇒ 修好 exe 名之后桥接仍**大概率起不来**；③1.6 编译链（net48 + `RimWorldWin64_Data\Managed`）随这次更新从本机消失，**本机已无法再按 1.6 目标编译**（除非另备 1.6 Managed 目录）。

---

## 一、文件证据（均为实测原文）

### 1. `D:\SteamLibrary\steamapps\common\RimWorld\Version.txt`

```
1.7.4985 rev1269
```

文件 18 字节（16 字符 + CRLF），LastWriteTime `2026-10-24 17:46:29`。
（切换前同一文件是 `1.6.4871 rev590`。）

### 2. `Assembly-CSharp.dll` —— **注意：位置变了**

1.7 已**不在** `RimWorldWin64_Data\Managed\`（该目录**已被删除**，`Test-Path` = False；`RimWorldWin64_Data` 下只剩 `Plugins`），
所有托管程序集现在**平铺在游戏根目录**：

| 文件 | FileVersion | ProductVersion | LastWriteTime |
|-|-|-|-|
| `D:\SteamLibrary\steamapps\common\RimWorld\Assembly-CSharp.dll` | **`1.7.9790.38077`** | `1.7.9790.38077+535691cc…e46297f6` | `2026-10-24 17:46:24` |
| `…\RimWorld.exe`（新入口，198,656 B） | `1.7.4985.1269` | `1.7.4985.1269+535691cc…` | `2026-10-24 17:46:28` |
| `…\RimWorld.dll` | `1.7.4985.1269` | 同上 | `2026-10-24 17:46:24` |
| `…\UnityStubs.dll`（1.7 新增，963,072 B） | `1.0.0.0` | `1.0.0+535691cc…e46297f6` | `2026-10-24 17:46:24` |

`RimWorld.runtimeconfig.json` 实测关键行：`"tfm": "net10.0"`、`Microsoft.NETCore.App 10.0.13-waylithclr.dev`、`WaylithClr.JitPatchableAssemblies: "Assembly-CSharp"`、`UnityEngine*=UnityStubs` 重定向。
`RimWorld.deps.json` 实测：`"runtimeTarget": {"name": ".NETCoreApp,Version=v10.0/win-x64"}`。
对 `Assembly-CSharp.dll` 本体做字符串统计（实测）：`.NETCoreApp,Version=v10.0` 命中 1 次、`mscorlib` 0 次、`UnityEngine.CoreModule` 0 次 ⇒ **该程序集是 net10.0 编译、并已改吃 UnityStubs**。

### 3. `steamapps\appmanifest_294100.acf`

**注意：1.7 的这份 acf 里没有 `"betas"` 段**，分支名改记在 `UserConfig` / `MountedConfig` 下的 `BetaKey`。原文（节选，全文件见 `runs/…/file-evidence.txt`）：

```
	"StateFlags"		"4"
	"LastUpdated"		"1792835196"
	"LastPlayed"		"1792835099"
	"SizeOnDisk"		"647288980"
	"buildid"		"26115268"
	"UpdateResult"		"0"
	"TargetBuildID"		"0"
	"UserConfig"
	{
		"language"		"schinese"
		"BetaKey"		"unstable"
	}
	"MountedConfig"
	{
		"language"		"schinese"
		"BetaKey"		"unstable"
	}
```

解码（实测换算）：`LastUpdated` = **2026-10-24 17:46:36 +08:00**、`LastPlayed` = 2026-10-24 17:44:59（**早于**更新时间 97 秒 ⇒ 玩家最后一次实际游玩仍是更新前的 1.6）。
`StateFlags 4` = 安装完整；`buildid` 从切换前的 `23969874` 变为 **`26115268`**。

### 4. 1.7 相关结构（实测）

| 检查项 | 结果 |
|-|-|
| `RimWorld.exe` | **存在**（新入口） |
| `RimWorldWin64.exe` | **已删除** |
| `UnityPlayer.dll` | **已删除** |
| `RimWorldWin64_Data\Managed` | **已删除** |
| `MonoBleedingEdge` | **已删除** |
| 根目录新增 | `UnityStubs.dll`、`Waylith*.dll`、`Brutal.*.dll`、`coreclr.dll`/`hostfxr.dll`/`System.Private.CoreLib.dll`（自包含 .NET 10）、`RimWorld.deps.json`、`Assets.zip`(358 MB)、`Rendering\Shaders\`、`vulkan\` |
| `Data\` 下 DLC 目录 | `Anomaly, Biotech, Core, Ideology, Odyssey, Royalty` —— **与 1.6 时期一致，没有新增 DLC 目录**（每个 DLC 目录 mtime 仍是 `2026-04-13`） |
| `Data\Core\Defs` | 554 个文件；**只有 `Effects / MaterialDefs / ShaderDefs / SoundDefs` 四个子目录 mtime 是 2026-10-24 17:46:35**，其余 Def 目录仍是 `2026-10-11 12:57:52`；`Languages\*.tar` 全部重写为 17:46:24 |
| `Version.txt` | `1.7.4985 rev1269`（见上） |

**推测（未逐项比对 Def 内容）**：这次「1.7」的改动重心在**运行时/引擎层**（Mono→.NET 10、Unity→Waylith+UnityStubs、托管程序集搬到根目录、资源打 `Assets.zip`），Def/数据层几乎原样沿用 —— 这与"Def 目录 mtime 几乎没变、语言包与 4 个 Def 子目录被重写"一致，但我**没有 1.6 的副本可 diff**，故只能说"mtime 层面如此"。

---

## 二、实例启动与截图

### 2.1 已按 skill 执行的动作

- 命令（实测，`--profile` 显式指定，`voiced` 6 mod）：
  `node src/cli.js start --profile voiced --width 1600 --height 900 --monitor 2`
  （`monitor 2` = 副屏 `\\.\DISPLAY2`，实测 1920x1080；`voiced/prefs.json` 里本来就是 1600x900 + monitor 2）
- 结果：**退出码 1，进程未启动**（原文见 §2.3）。
- 事后确认：`Get-Process RimWorld,RimWorldWin64` = **无 RimWorld 进程**；`runs\game-process.voiced.json` 仍是 `2026-10-24 17:44:06` 的旧记录（PID 32008，已不存在），**不是本次产生的**。
- 因此**没有执行 `stop`**（没有进程可停；也不存在"残留"。`stop --force` 全程未用）。

### 2.2 截图

**未取得截图。** 原因：游戏进程根本没起来（阻断在起进程之前），没有画面可截；主菜单版本号、Esc/About 界面都无从获取。
（本轮**没有**点开始游戏、没有进设置、没有动玩家的局。）

### 2.3 `start` 的报错原文（逐字）

```
[错误] 找不到 RimWorld 可执行文件：D:\SteamLibrary\steamapps\common\RimWorld\RimWorldWin64.exe
请设置环境变量 RIMWORLD_ROOT 指向游戏安装目录。
```

完整文件：`runs/rimworld-1.7-version-check-20261024/start-attempt.txt`（含命令、时间、退出码）。

---

## 三、日志版本行

**没有拿到 1.7 的运行期版本行 —— 全盘不存在。** 实测现有日志全是 1.6，且都早于更新时间（17:46）：

| 文件 | mtime | 版本行原文 | 行号 |
|-|-|-|-|
| `C:\Users\MikanseiP\AppData\LocalLow\…\RimWorld by Ludeon Studios\Player.log` | 2026-10-24 16:48:28 | `RimWorld 1.6.4871 rev591` | 第 25 行、第 316 行（同文件内两次会话） |
| 同上 `Player-prev.log` | 2026-10-24 09:54:40 | （1.6 会话，未再解析） | — |
| `profiles\voiced\Player.log`（测试工程自己的 profile） | 2026-10-24 17:44:57 | `RimWorld 1.6.4871 rev591` | 第 27 行 |

同文件开头仍是 1.6/Unity 的痕迹（实测第 1–4 行）：`Mono path[0] = 'D:/SteamLibrary/steamapps/common/RimWorld/RimWorldWin64_Data/Managed'`、`Initialize engine version: 2022.3.35f1`。
⇒ **"1.7 能跑起来"这件事，目前没有任何运行期证据**，只有文件层证据（§一）。

---

## 四、1.7 适配影响（本轮实测结论，含 `文件:行号`）

### 4.1 新 exe 名 / 进程匹配失效点（实测代码位置）

| 位置 | 原文 | 1.7 下的后果 |
|-|-|-|
| `src/launcher.js:33` | `export const GAME_IMAGE = 'RimWorldWin64.exe';` | 写死旧名 |
| `src/launcher.js:329` | `const exe = path.join(root, GAME_IMAGE);` | 拼出不存在的路径 |
| `src/launcher.js:330-334` | `if (!fs.existsSync(exe)) throw new Error('找不到 RimWorld 可执行文件：…')` | **这就是本次失败点**（在任何副作用之前） |
| `src/launcher.js:120` | `return tryListProcessesByName(GAME_IMAGE);` | 按旧名枚举 ⇒ 1.7 进程名是 `RimWorld.exe`，**枚举不到** |
| `src/launcher.js:133` | `listProcessesByPowerShell(GAME_IMAGE)` | 同上（兜底也失效） |
| `src/cli.js:121` | `await startGame({…})` | 调用入口，未受影响 |
| `src/cli.js:144` | `await waitForBridge({ profile, … })` | 修好 exe 名后才会走到这里 → 见 §4.3 |
| `src/prefs.js` | 只处理 `Prefs.xml` 键（`screenWidth/Height`、`monitor`、`doorstop`），**不含 exe/进程名** | 无需改 |

**⇒ 要起 1.7 实例，最少改两处（同一概念，应集中到一处）**：
1. `launcher.js:33`：`GAME_IMAGE` 从单一常量改为「候选名 → 按 `root` 里实际存在者解析」（`RimWorld.exe` 优先，`RimWorldWin64.exe` 兜底）；
2. `launcher.js:120/133`：进程枚举改用解析后的名字（否则 `status`/`stop` 看不到 1.7 实例）。

### 4.2 启动参数是否仍被认（对二进制做字符串取证，实测）

| 参数 | 证据 | 判读 |
|-|-|-|
| `-savedatafolder` | `Assembly-CSharp.dll` 内 UTF-16 命中 2 处：`…savedatafolder.Save data folder overridden to …` 与 `…rws…xtp…ccg…savedatafolder…` | **大概率仍支持**（隔离存档机制有望保住）—— 仅字符串证据，未跑起来验证 |
| `-logFile` | `UnityStubs.dll` 里唯一命中是构建路径串 `…\rimworld-builds\RimWorldWaylith\UnityStubs\Profiler.cs.logFile`，**不是参数解析处** | **未获支持证据，高-risk**。`start` 的桥接发现（`launcher.js:85-117 readBridgeInfo`）**依赖 `-logFile` 写出的日志**，这条链要重新验证 |
| `-screen-width` / `-screen-height` / `-monitor` / `-popupwindow` / `-screen-fullscreen` | 在 `RimWorld.exe`、`RimWorld.dll`、`Assembly-CSharp.dll`、`Waylith.dll` 中**全部 0 命中** | **可能已不认**（不认也只是被忽略；分辨率还受 `Prefs.xml` 控制） |
| `--doorstop-enabled false` | `cli.js:130` `doorstop: Boolean(flags.doorstop)` ⇒ 默认 `false` ⇒ `launcher.js:419` 会追加 `--doorstop-enabled false` | 参数本身由 Doorstop 消费；1.7 是否仍吃未验证 |

### 4.3 Doorstop / winhttp 注入（.NET 10 + Waylith）

- 盘上仍在：`winhttp.dll`（2026-08-01）、`Doorstop.dll`（2026-08-01）、`doorstop_config.ini`（2026-08-01）——**都早于本次 1.7 更新（2026-10-24 17:46）**，即 1.6 时代遗留，本次更新没有换代。
- `winhttp.dll` 内含 `doorstop_config.ini`、`--doorstop-enabled`、`--doorstop-target-assembly`、`coreclr_path`、`corlib_dir` 等字符串（实测）⇒ 它仍是 Doorstop 加载器，且**代码里已提到 `coreclr_path`**。
- **能否把 .NET 10 自包含宿主（`RimWorld.exe` + `hostfxr`/`coreclr`）注入起来：未验证**（本轮没起进程）。

### 4.4 桥接 mod 对 1.7 的支持情况（实测 About/LoadFolders）

| mod | supportedVersions | LoadFolders | 1.7 下的风险 |
|-|-|-|-|
| `Mods\RimBridgeServer` | **只有 `1.6`**（modVersion 2.1.1） | 只有 `<v1.6>`，程序集只在 `1.6\Assemblies\` | **未实测**，按声明**不会**在 1.7 加载 ⇒ `waitForBridge` 会超时（推测） |
| `Mods\DSH_GameLink` | **只有 `1.6`** | 只有 `<v1.6>` | 同上（事件通道 `live` 会没数据） |
| `语音副官`(VoiceDeputy) | **只有 `1.6`**（且 `About.xml` 本身 XML 不闭合，`[xml]` 解析报错） | — | 1.7 下加载风险 |
| `brrainz.harmony`（工坊 2009463077） | **含 `1.7`** | 有 `<v1.7>` → `1.7\Assemblies` | 可用 |

**⇒ 顺序上要先修的其实是两件**：exe 名（§4.1）+ 桥接/事件 mod 的 1.7 版本；只修 exe 名的话，`start` 也会卡在 `waitForBridge` 直到超时。

---

## 五、1.7 本地构建条件核对（**只核对，未编译**）

目标工程：`RimworldMods\ratkin_hunger_and_havoc\Source\HungerAndHavoc.csproj`
（`TargetFramework`：`RimWorldVersion=1.7` → **`net10.0`**（csproj:6）；`1.6` → `net48`（csproj:5））

| 条件 | 实测 | 判定 |
|-|-|-|
| .NET SDK | `dotnet --list-sdks` → **`10.0.401 [C:\Program Files\dotnet\sdk]`** | **满足 net10.0** |
| 1.7 `Assembly-CSharp.dll` | `D:\SteamLibrary\steamapps\common\RimWorld\Assembly-CSharp.dll`，FileVersion `1.7.9790.38077`，内含 `.NETCoreApp,Version=v10.0` | **满足** |
| `UnityStubs.dll`（1.7 专用引用，csproj:62-66） | 游戏根目录存在，963,072 B | **满足** |
| `$(RimWorldManagedDir)` 指向 | `ratkin_hunger_and_havoc\Directory.Build.props:8` → 1.7 时 = `$(RimWorldDir)`（游戏根目录）✔；`:19` 检查 Assembly-CSharp、`:20` 检查 UnityStubs | 机制正确 |
| **`$(RimWorldDir)` 默认值** | `Directory.Build.props:6` = **`E:/Apps/Steam/steamapps/common/RimWorld`**（本机不存在）；环境变量 `RIMWORLD_DIR` = 空 | **❌ 缺：必须显式传 `-p:RimWorldDir=D:\SteamLibrary\steamapps\common\RimWorld`（或设 `RIMWORLD_DIR`）** |
| Harmony | 工坊 `…\workshop\content\294100\2009463077\1.7\Assemblies\0Harmony.dll` 存在；NuGet 缓存 `lib.harmony\2.4.1` 已在本机 | 满足 |
| **Oxygendsl（DSL 框架）** | `OxygenDslBuildDir`（`Directory.Build.props:11`）= `RimworldMods\oxygen_dsl_framework\tmp\build\1.7`；该 **`tmp` 目录根本不存在**，全仓 `Oxygendsl.*.dll` **0 命中** | **❌ 缺：`Oxygendsl.Core.dll` / `Oxygendsl.Mod.dll` 必须先按 1.7 构建出来**（`HungerAndHavoc.csproj:50/54` 直接引用它们） |
| `oxygen_dsl_framework` 自身默认路径 | `oxygen_dsl_framework\Directory.Build.props:9` = `E:\Apps\Steam\…`（同样不对） | **❌ 需一并覆盖 `RimWorldDir`** |

**一句话**：**引擎侧（SDK + 1.7 Assembly-CSharp + UnityStubs + Harmony）齐了，能编 net10.0**；真正缺的是 **(a) Oxygendsl 的 1.7 构建产物**、**(b) 游戏路径覆盖（props 默认值不指向本机安装）**。按派活要求**未实际编译**。

---

## 六、未验证 / 没做的事

- **未取得任何 1.7 运行期证据**：没有进程、没有截图、没有 `RimWorld 1.7…` 日志行 —— 因为实例被 exe 名阻断，没起来。
- **未改任何工程代码**（`launcher.js:33` 等按派活方要求留待后续任务）。
- **未实测** `-logFile` / `-screen-width` / `-monitor` 在 1.7 是否仍生效（只有二进制字符串证据）。
- **未实测** Doorstop/winhttp 能否注入 .NET 10 + Waylith 宿主。
- **未实测** `RimBridgeServer`/`DSH_GameLink`/`VoiceDeputy` 在 1.7 下的真实加载结果（About 声明 1.6 是文件事实，"不会加载"是推测）。
- **未做 1.6 vs 1.7 的 Def 逐文件 diff**（本机已无 1.6 副本，只有 mtime 证据）。
- **未检查 Steam 云端/其它 Windows 用户**；未联网核对 1.7 是否已公开发布。
- **未打开任何玩家存档**（连文件名都没列）；`Saves\` 目录未触碰。
- **未执行 `stop`**（没有进程），`--force` 全程未用。

---

## 七、本轮产物

| 文件 | 内容 |
|-|-|
| `runs\rimworld-1.7-version-check-20261024\file-evidence.txt` | Version.txt 原文（含转义）、四个程序集版本、appmanifest 全文+时间换算、1.6/1.7 结构检查、runtimeconfig 全文 |
| `runs\rimworld-1.7-version-check-20261024\start-attempt.txt` | `start` 命令、时间、stdout/stderr 原文、退出码 1 |
| 本文件 | 报告 |

截图：**无**（原因见 §2.2）。
