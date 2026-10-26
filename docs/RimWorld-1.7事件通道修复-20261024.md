# RimWorld 1.7（.NET 10 + Waylith）下 `dsh.gamelink`「不产出事件」——查证与结论

- 日期：2026-10-24
- 环境：`Version.txt = 1.7.4985 rev1269`；`Runtime: .NET 10.0.13-waylithclr.dev`；入口 `RimWorld.exe`
- profile：`rhah`（14 项，含 `dsh.gamelink`）
- 证据目录：`runs\20261024-gamelink-1.7-events\`

## 结论（5 行）

1. **根因不是加载失败、也不是启动器丢环境变量**：1.6 那份 `net472` 的 `DSH_GameLink.dll` 在 1.7 下**加载成功**（游戏进程模块表里有它），而且 `game_loaded / colony_started / heartbeat / window / log` **五种事件全部实测写出**。
2. **「不产出事件」是观测时机问题**：事件全部由 `GameComponent` 产生，而 `GameComponent` 只在**有 `Game` 之后**才被 `Game.FillComponents()` 创建 ⇒ 停在**主菜单**时事件文件既不创建也不更新。实测本次启动主菜单静默 **59 秒**（18:02:01 进程起 → 18:03:00 第一条事件），`runs\live\rhah.ndjson` 一个字节没动。
3. **改了什么**：**没有改代码**（`src\launcher.js:585-591` 本来就把 `GABP_SERVER_PORT / GABP_TOKEN / GABS_GAME_ID / DSH_LIVE_DIR` 传给了子进程，实测生效）；只改了 skill 的一处说明（`.dsh\skills\rimworld-auto-test\SKILL.md`：主菜单阶段等 `Player.log`，不要等事件文件），外加本报告。
4. **事件通道不需要「恢复」——它没坏**：本次进图后 `runs\live\rhah.ndjson` 新增 **29 行**（840 → 869，seq 从 1 重来 = 确为本次进程所写），含 `colony_started` 与触发的 `log` 事件。
5. **不需要重编**；但记录一条硬事实：**1.6 的参考程序集在 RimWorld 安装里已经不存在**（`RimWorldWin64_Data\Managed` 已随 1.7 更新消失，`Test-Path` = False；未全盘搜索其它备份），所以这份 `net472` DLL **今后无法在安装目录这套引用下重建** —— 一旦要改源码，就必须按 §5 改成 `net10.0`。主菜单阶段仍无 mod 侧事件（要补得重编，需用户拍板，见 §7）。

## 一、日志证据原文

### 1.1 本次运行的头（确认版本、运行时、PID、命令行）

`C:\Users\MikanseiP\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Logs\Player.log`：

```
1: === Waylith log ===
2: Started:   2026-10-24 18:02:01.124 +08:00
3: Version:   1.7.4985.1269
4: OS:        Microsoft Windows 10.0.26200 (X64)
5: Runtime:   .NET 10.0.13-waylithclr.dev
6: Process:   pid 22344
7: Log file:  C:\Users\MikanseiP\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Logs\Player.log
8: Command:   D:\SteamLibrary\steamapps\common\RimWorld\RimWorld.dll -savedatafolder=E:\2026.7以后的新工程\DSH\RimWorld_auto_test\profiles\rhah
             -logFile "C:\Users\MikanseiP\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Logs\Player.log"
             -screen-fullscreen 0 -screen-width 1600 -screen-height 900 --doorstop-enabled false
```

### 1.2 mod 被识别（About.xml 解析到了）

```
181: 2026-10-24 18:02:03.428  WARN [Verse] Mod DSH GameLink dependency (brrainz.harmony) needs to have <downloadUrl> and/or <steamWorkshopUrl> specified.
```

进图时的 mod 清单里，`dsh.gamelink` 被列进加载顺序，且**不带** `(incompatible version)` 标记（说明刚改的 `About.xml` 的 `<li>1.7</li>` 生效了）：

```
1172: 2026-10-24 18:02:57.911  INFO [Verse] Initializing new game with mods:
1184:   - brrainz.rimbridgeserver (incompatible version)
1186:   - dsh.gamelink
```

### 1.3 没有任何「加载失败」类错误

在 17:55 那次与 18:02 这次两份 1.7 日志里搜 `DSH_GameLink|gamelink|Failed to load|TypeLoadException|MissingMethod|Could not`：

- 命中只有 **`Mod DSH GameLink dependency (...)`** 这一条 WARN（About.xml 元数据校验）；
- **没有** `Failed to load assembly`、**没有** `TypeLoadException`、**没有** `MissingMethod`、**没有** `Could not instantiate`；
- 也**没有** `[GameLink]` 前缀的红字（mod 自己的写盘失败会打这个前缀，见 `LiveChannel.cs:23,107`）。

⇒ 判定：**不是「没加载」，也不是「加载失败」，也不是「加载了但写盘失败」**。

### 1.4 铁证：DLL 确实被加载进了进程

```
game pid: 22344  started: 10/24/2026 18:02:00
  0Harmony.dll        -> D:\SteamLibrary\steamapps\workshop\content\294100\2009463077\1.7\Assemblies\0Harmony.dll
  DSH_GameLink.dll    -> D:\SteamLibrary\steamapps\common\RimWorld\Mods\DSH_GameLink\Assemblies\DSH_GameLink.dll
  Assembly-CSharp.dll -> D:\SteamLibrary\steamapps\common\RimWorld\Assembly-CSharp.dll
  UnityStubs.dll      -> D:\SteamLibrary\steamapps\common\RimWorld\UnityStubs.dll
```

（`Mods\DSH_GameLink` 是指向工程 `mods\DSH_GameLink` 的 junction，所以加载的就是那份 1.6 构建。）

### 1.5 主菜单静默窗口（59 秒，事件文件零改动）

| 时刻 | 事实 | 出处 |
| --- | --- | --- |
| 18:02:01.124 | 游戏进程启动 | `Player.log:2` |
| 18:02:19.882 | 桥接就绪（`GABP server connected to GABS on port 5174`） | `Player.log:1164` |
| 18:02:57.911 | `Initializing new game with mods:`（开调试殖民地） | `Player.log:1172` |
| 18:03:00.583 | **第一条事件** `{"seq":1,"kind":"game_loaded",...}` | `runs\live\rhah.ndjson` |
| 期间 | `runs\live\rhah.ndjson` 保持 89,841 B / 840 行 / mtime 17:44:41（上一次 1.6 运行留下的） | `00-baseline.txt` |

17:55 那次 1.7 启动（只到主菜单，17:57:43 退出）同理：整个进程生命周期里 `rhah.ndjson` 的 mtime 一直停在 17:44:41。

### 1.6 触发出来的 `log` 事件，与日志原文逐字一致

`Player.log:1194`：

```
2026-10-24 18:03:34.078 ERROR [Verse] Failed to find Verse.ThingDef named DSH_NoSuchThing_XYZ. There are 3596 defs of this type loaded.
```

同一秒写进事件文件的那一行（`seq 16`）：

```json
{"ts":1792836214.084,"seq":16,"kind":"log","level":"Error","text":"Failed to find Verse.ThingDef named DSH_NoSuchThing_XYZ. There are 3596 defs of this type loaded."}
```

⇒ 同时证明 `LogTap` 的 Harmony 补丁（打在 `Verse.Log.Error` 上）**挂上了且在工作**。

## 二、写事件机制的依赖（环境变量 / 路径 / 会话 id）

| 依赖 | mod 侧 | DSH 侧 | 本次实测 |
| --- | --- | --- | --- |
| `DSH_LIVE_DIR` | `LiveChannel.cs:26` 读它；`:186-191` `Path.Combine(dir, <profile>.ndjson)` | `src\launcher.js:590` 传 `<WS>\runs\live`；`src\live.js:51-52` 读同一个变量 | ✅ 生效：写到了 `runs\live\rhah.ndjson`，**没有**落到 fallback 目录 |
| `GABS_GAME_ID` | `LiveChannel.cs:27,214-218` 去前缀 `dsh-rimworld-` 当 profile 名 | `src\launcher.js:587` 传 `dsh-rimworld-<profile>` | ✅ 生效：文件名是 `rhah.ndjson`（不是 fallback 的 `game.ndjson`） |
| fallback 路径 | `LiveChannel.cs:193` `GenFilePaths.SaveDataFolderPath\dsh-live\game.ndjson` | `src\live.js:54-55` | ✅ 未用到：`profiles\rhah\dsh-live` 不存在（`Test-Path` = False） |
| Harmony | `LogTap.cs:31-34` 显式 `harmony.Patch(Log.Message/Warning/Error)` | — | ✅ 生效（见 §1.6） |
| GameComponent 注册 | `GameLinkComponent.cs`（无 `Mod` 类，靠 `Game.FillComponents()` 自动收养 `ctor(Game)` 的 `GameComponent`） | — | ✅ 进图后才有；**主菜单时不存在** ⇒ 这是「不产出事件」的真正原因 |

路径与会话 id 完全匹配：`DSH_LIVE_DIR` 解析出的目标 = `runs\live\rhah.ndjson`，正是 `src\live.js` 的默认读取位置；profile 名 `rhah` 一致。

## 三、启动器：不需要改（附 diff = 空）

`src\launcher.js:575-593`（spawn 与环境组装）：

```
575:  const child = spawnDetached(exe, args, {
576:    cwd: root,
...
585:      GABP_SERVER_PORT: String(bridgeEnv.port),
586:      GABP_TOKEN: bridgeEnv.token,
587:      GABS_GAME_ID: `dsh-rimworld-${profile}`,
588:      // 游戏侧事件通道（mods/DSH_GameLink）的落点：给它一个绝对目录，mod 就写 <dir>\<profile>.ndjson。
589:      // DSH 侧的 `node src/cli.js live` 读同一个目录（见 src/live.js 的 resolveEventFile）。
590:      DSH_LIVE_DIR: path.join(WS, 'runs', 'live'),
591:      ...env,
```

`spawnDetached`（`src\proc.js:112-122`）把 `env` 原样交给 `child_process.spawn`，没有丢字段、没有 `detached` 下的环境丢失。

**diff：无。** 两个环境变量都在、都生效（§2 的实测两列），改它只会引入风险。

## 四、DLL 在 1.7 下能否加载（实测判定：能）

`DSH_GameLink.dll`（2026-10-17，12,800 B，net472）：

```
identity: DSH_GameLink, Version=0.0.0.0  runtime: v4.0.30319
  REF: mscorlib v4.0.0.0
  REF: Assembly-CSharp v1.6.9676.17735
  REF: 0Harmony v2.4.3.0
  REF: UnityEngine.CoreModule v0.0.0.0
```

逐项对照 1.7 安装根：

| 引用 | 1.7 实况 | 结果 |
| --- | --- | --- |
| `mscorlib 4.0.0.0` | 安装根有 `mscorlib.dll`（49,152 B，CoreCLR 的门面） | 可解析 |
| `Assembly-CSharp v1.6.9676.17735` | 实际加载 `Assembly-CSharp, Version=1.7.9790.38077`（版本不同） | **实测可解析**：CoreCLR 按简单名绑定到已加载的那份，版本号不参与 |
| `0Harmony v2.4.3.0` | 实际加载 `0Harmony, Version=3.0.0.0`（`…\2009463077\1.7\Assemblies`） | **实测可解析**，且 `Harmony.Patch` 调用成功（§1.6） |
| `UnityEngine.CoreModule v0.0.0.0` | 安装根**没有**任何 `UnityEngine*.dll`，`RimWorldWin64_Data\Managed` 也不存在；Unity 被 `UnityStubs.dll`（identity `UnityStubs`，内含 `namespace UnityEngine`）取代；`Assembly-CSharp 1.7` 自己引用 `UnityStubs v1.0.0.0` | **实测可解析**：`GameLinkComponent.cs:93` 的 `UnityEngine.Time.realtimeSinceStartup` 每 3 秒成功执行（heartbeat 里 tick 在涨）。**解析机制未确证**（游戏侧没找到 `UnityEngine.CoreModule` 这个名字的映射表，可能是 Waylith 的加载器按类型回退），但结果确定：不抛 `TypeLoadException` |

⇒ 任务里担心的「引用 1.6 的 Assembly-CSharp / UnityEngine 模块 ⇒ 加载不了」**没有发生**。判定依据是进程模块表 + 五种事件的实际输出，不是推测。

（顺带一条 1.7 细节：`Assembly-CSharp 1.7` 引的是 `System.Runtime 10` + `UnityStubs`，1.6 那套 `UnityEngine.*Module` 程序集在 1.7 里整体不存在。）

## 五、若要为 1.7 重编：改哪几处（本次**未**改、**未**编译）

源码在：`mods\DSH_GameLink\Source\`（`DSH_GameLink.csproj`、`GameLinkComponent.cs`、`LiveChannel.cs`、`LogTap.cs`、`EventLine.cs`）。本机 `dotnet --list-sdks` = `10.0.401`，具备 `net10.0` 编译能力。

`mods\DSH_GameLink\Source\DSH_GameLink.csproj` 需要动的地方（行号为当前文件）：

| 行 | 现在 | 1.7 要改成 |
| --- | --- | --- |
| 7 | `<TargetFramework>net472</TargetFramework>` | `net10.0` |
| 16-18 | `NoStdLib` / `DisableImplicitFrameworkReferences` / `AutomaticallyUseReferenceAssemblyPackages`（为绕开 net472 目标包） | 删掉，改用 SDK 默认 |
| 20 | `<OutputPath>..\Assemblies\</OutputPath>` | `..\1.7\Assemblies\`（否则会盖掉 1.6 那份） |
| 22 | `RimWorldManagedDir` 默认 `…\RimWorldWin64_Data\Managed` | 安装根 `D:\SteamLibrary\steamapps\common\RimWorld`（1.7 程序集平铺在根） |
| 25 | `HarmonyDir` 默认 `…\2009463077\1.6\Assemblies` | `…\2009463077\1.7\Assemblies` |
| 27-28 | `_TargetFrameworkDirectories` / `_FullFrameworkReferenceAssemblyPaths` | 删掉 |
| 32-47 | `mscorlib` / `System` / `System.Core` / `netstandard` 的 HintPath | 删掉（net10.0 自带） |
| 48-51 | `Assembly-CSharp` HintPath 指 `Managed\` | 指安装根 |
| 57-60 | `UnityEngine.CoreModule` HintPath 指 `Managed\UnityEngine.CoreModule.dll` | 改成 `UnityStubs.dll`（Unity 已被它取代） |

`mods\DSH_GameLink\LoadFolders.xml:6-8` 同步改成：

```xml
  <v1.7>
    <li>1.7</li>
  </v1.7>
```

**为什么这次不做**：(a) 现有 DLL 功能完整（五种事件全部实测写出），重编只会增加回归风险；(b) 一旦把 csproj 切成 `net10.0`，`net472` 那份就**再也编不回来**（安装目录里的 1.6 参考程序集已随更新消失），这是用户级决策（多目标 vs 单目标、要不要弃 1.6），按 AGENTS「往现有结构里塞新东西先问用户」应留给用户拍板。

## 六、验证记录（全部实测）

命令与结果（`runs\20261024-gamelink-1.7-events\05-live-commands.txt`、`06-stop.txt`）：

1. `node src/cli.js start --profile rhah` → `游戏已启动 PID=22344`，`桥接就绪 127.0.0.1:5174（等待 19.4s）`
2. 后台挂 watcher（事件驱动，无 sleep）：
   `node src/cli.js live --profile rhah --until 'kind=game_loaded' --timeout 300000 --new-only`
   → 开调试殖民地（`rimworld/start_debug_game_ready {readiness:mapData}`）后 **命中，等待 9049ms**，打出：
   `{"ts":1792836180.583,"seq":1,"kind":"game_loaded","tick":1,"map":0}`
3. 再挂 watcher：`node src/cli.js live --profile rhah --until 'kind=log' --timeout 90000 --new-only`
   → 触发一次失败生成（`rimworld/spawn_thing {defName:"DSH_NoSuchThing_XYZ"}`）后 **命中，等待 1908ms**
4. 主菜单的等法（日志源）：`node src/cli.js live --profile rhah --source log --until 'text~GABP server connected to GABS' --timeout 20000` → **命中，206ms**
5. `node src/cli.js stop --profile rhah`（**无 `--force`**）→ `{"stopped":[22344],"alreadyStopped":false,"forced":false,"forceMode":false}`
   随后 `node src/cli.js status --profile rhah` → `gameRunning:false`、`processList:[]`、`otherGamePids:[]`、桥接 `ECONNREFUSED`（**无残留进程**）

### `runs\live\rhah.ndjson` 的新增内容（840 行 → 869 行，本次进程共 29 行，原文）

```json
{"ts":1792836180.583,"seq":1,"kind":"game_loaded","tick":1,"map":0}
{"ts":1792836180.584,"seq":2,"kind":"colony_started","tick":1,"colonists":3,"map":0}
{"ts":1792836180.584,"seq":3,"kind":"heartbeat","tick":1,"programState":"Playing","speed":1,"map":0}
{"ts":1792836180.677,"seq":4,"kind":"window","opened":true,"type":"Verse.ImmediateWindow","forcePause":false}
{"ts":1792836183.590,"seq":5,"kind":"heartbeat","tick":171,"programState":"Playing","speed":1,"map":0}
...
{"ts":1792836214.084,"seq":16,"kind":"log","level":"Error","text":"Failed to find Verse.ThingDef named DSH_NoSuchThing_XYZ. There are 3596 defs of this type loaded."}
（其后到 869 行为 heartbeat，间隔 3.0 s，tick 递增）
```

完整新增行见 `runs\20261024-gamelink-1.7-events\03-new-events.txt`。

> 注意：`seq` 从 1 重来，说明这 29 行是**本次 1.7 进程**写的（每次进程启动 mod 侧 `seq` 归零，`LiveChannel.cs:42,132`）。
> 另外 `--until` 默认从**文件头**扫，会在历史里命中上一次运行那条 `colony_started`（实测 213ms 命中 `seq 2` 的旧事件）；只等本次要加 `--new-only`。

## 七、未验证 / 仍然存在的问题

1. **未重编、未产出 `1.7\Assemblies\`**：按 §5 只在文件里留了改法。要落地必须先由用户确认是否弃用 1.6 目标。
2. **`UnityEngine.CoreModule` → `UnityStubs` 的解析机制未确证**：只证明了「结果能用」（heartbeat 里 `UnityEngine.Time.realtimeSinceStartup` 每帧执行成功），没找到游戏侧是按什么规则把该程序集名映射过去的。
3. **主菜单阶段仍然没有任何 mod 侧事件**（设计如此）：想有，需要在 mod 里加一个 entry-scene 的心跳（例如 `[StaticConstructorOnStartup]` 打一条 `mod_loaded`，或补 `Root_Entry.Update` 的 Harmony 后缀），**这需要重编**，属于「往现有结构里塞新东西」，留给用户决定。
4. **未测 1.7 下的事件轮转**（8 MB → `.1`）与多实例并发：本次数据量远未到阈值。
5. **未测读取端 `--source log` 在 1.7 共用日志被下一次启动截断时的行为**（1.7 每次启动会重写 `Player.log`，跟读者会看到文件变小 → `createFollower` 会从头重读；逻辑上处理了，但没有专门造这个场景）。
6. 本次验证只跑了 `rhah` 一个 profile；其他 profile（尤其 `vanilla` 561 项）未验证。
