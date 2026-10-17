# 调查：Doorstop 与 RimBridgeServer 启动疑点

> **文档性质**：只读调查记录 + 给后续 agent 的研究任务书。
> **本轮不做的事**（写此文档时遵守）：不改任何源码、不启动/停止游戏、不改 Steam 配置、不改任何已采集数据。
> **证据标注约定**：`【已证实】`= 本仓库文件/日志/本机文件可直接查证；`【推断】`= 有证据支撑的因果解释，但未做对照实验；`【未验证】`= 只有口头观察或完全没有证据，**不得当结论使用**。
> **创建时间**：2026-10-04 12:xx（Asia/Shanghai）。此前 `docs/` 下**不存在**同名文件，本文为新建，未覆盖任何既有内容。

---

## 1. 摘要（先看这一段）

- **Doorstop 是"游戏启动注入层"**：本机 RimWorld 根目录的 `winhttp.dll` + `Doorstop.dll` + `doorstop_config.ini` 让 Unity 在**任何 mod 之前**先执行托管入口 `Doorstop.Entrypoint.Start()`。它加载的是 [Unity Doorstop](https://github.com/NeighTools/UnityDoorstop)（注入器）+ [pardeike/Rimworld-Doorstop](https://github.com/pardeike/Rimworld-Doorstop)（`Doorstop.dll` 本体：Mono 调试服务器 + `ILReloaderLib` 热重载）。**它和 RimBridgeServer / ColonyRecorder 是完全不同的东西**，唯一的关系是"它的热重载补丁会改动 RimWorld 加载 mod 程序集的路径"。
- **可复现的对照事实**【已证实】：Doorstop 开着时，`RimBridgeServer.Core.dll` 在加载期抛 `TypeLoadException`（缺 `RimBridgeServer.Extensions.Abstractions.IRimBridgeCapabilityProvider`），整个桥接 mod 加载失败 ⇒ 5174 无监听；加 `--doorstop-enabled false` 后同一台机器、同一份 mod 文件、同一个 Steam 启动方式，桥接**正常起来**（`GABP server running standalone on port 5174` + token）。
- **对正常游玩的影响**【推断，工作假设】：RimBridge 是**自动化测试/桥接控制面**，不是 RimWorld 本体的一部分，也不是 ColonyRecorder 的依赖。日志显示 ColonyRecorder 在 Doorstop 开着的会话里**照样加载成功并写出 runId**；因此"桥接起不来"主要影响**自动化验证**，不必然影响手动游玩。**这是工作假设，不是结论**——尚未做"关掉桥接后游戏本体功能/存档兼容"的对照验证。
- **最不确定的一点**【未验证】：带开关启动后曾有一个实例（父会话报 PID 11008）在约 5 秒内退出/崩溃，**原因未定**；本机未找到 RimWorld 崩溃转储，Windows 应用日志里同时刻的错误记录属于我们自己的探针 `ScanSession.exe`，不是 RimWorld。
- **当前安全状态**：Steam 的 RimWorld 启动项**临时含 `--doorstop-enabled false`**（原值为 `-popupwindow`，已备份）；当前实例 PID 12792 正在运行、可手动游玩；**不再自动重启 Steam/游戏**；旧采集数据一个字节都没动（基线哈希见 §12）。

---

## 2. Doorstop 是什么（与 RimBridgeServer / ColonyRecorder / Steam 的分工）

| 组件 | 层次 | 加载时机 | 作用 | 坏了会怎样 |
| --- | --- | --- | --- | --- |
| **Unity Doorstop**（`winhttp.dll`） | **原生注入层**（DLL 劫持/代理） | Unity 进程初始化最早期，早于任何托管代码 | 在游戏进程里把托管的 `Doorstop.dll` 拉起来 | 不注入 ⇒ 调试器/热重载不可用（对游戏本体无影响） |
| **Rimworld-Doorstop**（`Doorstop.dll`） | **托管入口 + 补丁** | Doorstop 注入后立刻 | 启动 Mono 调试服务器（`debug_address`）；用 `ILReloaderLib` 给 `[Reloadable]` 方法做 IL 热重载；**patch `Verse.ModAssemblyHandler.ReloadAll`**，于是每次加载 mod 程序集都会被它"过一遍" | 它的补丁会在加载期提前解析程序集自定义特性 ⇒ 可能连带拖垮别的 mod 的加载（本文疑点） |
| **RimBridgeServer** | **普通 RimWorld mod**（`Mods\RimBridgeServer`） | 正常 mod 加载顺序里 | 把运行中的游戏变成外部可控的桥接（GABP，默认 5174 + token）；内含自己的 Harmony 补丁与 `BridgeTools` 伴生 DLL 加载 | 桥接不可用 ⇒ 无法用 agent 驱动游戏（自动化测试断链） |
| **ColonyRecorder**（`dsh.colonyrecorder`） | **普通 RimWorld mod** | 正常 mod 加载顺序里 | 只读录制殖民地快照/操作/事件三条流到 `foodcontrol-snapshots\runs\run-<runId>\` | 没有录像数据；与 RimBridge 无依赖关系 |
| **Steam** | **启动器** | 最外层 | 读取 `LaunchOptions` 并**把它拼进游戏命令行**、启动 `RimWorldWin64.exe` | Steam **不注入任何东西**，也**不是故障根因**；它只负责"把开关传下去" |

要点【已证实，来源见 §13 一手资料 + 本机文件】：

- Doorstop v4 的配置有两种途径：`doorstop_config.ini` 与 `--doorstop-*` 命令行参数；`--doorstop-enabled bool` 是官方参数（`--doorstop-mono-debug-enabled / -address / -suspend` 同样官方）。
- `Rimworld-Doorstop` 的 README 要求：**不再自带 Harmony**，改为在 ini 里指向 RimWorld 已经加载的 `0Harmony.dll`（本机 ini 正是这么写的）。
- `DOORSTOP_ENABLED` **环境变量**不是 v4 的配置入口（v4 只列了 ini 与 CLI）。本仓库 [src/launcher.js](src/launcher.js) 第 233 行还在设 `DOORSTOP_ENABLED: '0'`，README 第 196 行已注明"在这个 fork 上无效" ⇒ **同一件事有两处口径，且其中一处是死代码**（详见 §8-Q7，本轮不改）。

---

## 3. 事实证据表

### 3.1 本机环境与文件（全部 `【已证实】`，直接读文件/日志得到）

| 项 | 值 | 证据 |
| --- | --- | --- |
| RimWorld 版本 | `2016.3.35f1` Unity 上跑 **1.6.4871**；`Version.txt` 写 **rev590**，而 `Player.log` 自报 **rev591**（两处不一致，属正常现象，勿当故障） | `D:\SteamLibrary\steamapps\common\RimWorld\Version.txt`；LocalLow `Player.log` 第 25/102 行 |
| 游戏根目录注入文件 | `winhttp.dll`（26,112 B，2026-08-01 10:42）、`Doorstop.dll`（3,014,656 B，同日）、`doorstop_config.ini`（307 B，10:56） | 根目录列目录 |
| `doorstop_config.ini` | `[General] enabled=true`、`target_assembly=Doorstop.dll`、`redirect_output_log=false`；`[Harmony] harmony_library=..\..\workshop\content\294100\2009463077\Current\Assemblies\0Harmony.dll`；`[UnityMono] debug_enabled=true`、`debug_address=127.0.0.1:56000`、`debug_suspend=false` | 该 ini 全文（13 行，与 Rimworld-Doorstop README 示例逐字同构） |
| 已装 mod 规模 | **385 个 mod 的正式档**（不是隔离纯净档）：`ModsConfig.xml` 里同时启用 `brrainz.rimbridgeserver` 与 `dsh.colonyrecorder` | 日志 mod 列表；[STATUS.md](STATUS.md#L412-L416) |
| RimBridgeServer 版本 | **2.1.1**（9 个 DLL，程序集版本全部 2.1.1；sha256 见 §12.3） | `Mods\RimBridgeServer\1.6\Assemblies\` |
| 伴生工具目录 | `RimWorld\BridgeTools\DSH.CEBridge\`、`BridgeTools\DSH.FoodProbe\DSH.FoodProbe.dll`（245,248 B） | `D:\...\RimWorld\BridgeTools` |
| Doorstop 是否在跑 | **能从日志判定**：Doorstop 的 `ILReloaderLib` 会打印 `[Info] loading <绝对路径>` 行。开关打开的会话里这种行**大量存在**；关掉开关的会话里**恰好 0 条**，而 `Prepatcher:` 行两边都有（9 条） | 见 §3.2 对照 |

### 3.2 两次启动的对照（核心证据）

| 观测点 | **无开关**（Doorstop 生效；PID 34076，会话 runId `…114941`） | **带 `--doorstop-enabled false`**（PID 12792，会话 runId `…115726`） |
| --- | --- | --- |
| 实际命令行 | `-disable-compute-shaders -popupwindow`（**无** doorstop 开关） | `-disable-compute-shaders -popupwindow --doorstop-enabled false` |
| Doorstop 痕迹 | 有：`at ILReloaderLib.Tools.IsReloadable` / `ILReloaderLib.AssemblyLoadingPatcher.RegisterTypes` / `ILReloaderLib.AssemblyLoadingPatcher.LoadFrom` / `MonoMod.Utils.DynamicMethodDefinition.Verse.ModAssemblyHandler.ReloadAll_Patch1`；`[Info] loading …` 行大量出现 | 无任何 `ILReloaderLib` 行；`[Info] loading` **0 条** |
| RimBridgeServer | `Exception loading RimBridgeServer.Core.dll: System.TypeLoadException: Could not resolve type with token 0100001f from typeref (expected class 'RimBridgeServer.Extensions.Abstractions.IRimBridgeCapabilityProvider' in assembly 'RimBridgeServer.Extensions.Abstractions, Version=2.1.1.0')` → 随后 `ReflectionTypeLoadException getting types in assembly RimBridgeServer`（另有 6 个 `RimBridgeServer.Sdk.IRimBridge*` 类型解析失败） | **无任何 TypeLoadException**；顺序打印 `[RimBridge] Applied essential Harmony patches` → `Applied 56 optional Harmony patch classes` → `Applied 6 late virtual-input patch classes` → `capabilities.initialize` → `gabp.server.start-async` |
| 桥接端口 | **5174 无监听**【父会话实测 + 为此写的探针 `tmp/probe-steam-bridge.mjs`】；当时 **56000 在监听**（Doorstop 的 Mono 调试器）【父会话实测，未在本轮复核】 | `[RimBridge] GABP server running standalone on port 5174`、`[RimBridge] Bridge token: 0afd7907…`（完整值见 Player.log 第 252 行，此处刻意不复制明文）；本轮复核端口：**5174 OPEN，56000 无监听** |
| 顺带被波及的第三方 mod | `Exception loading BetterModSort.dll: ReflectionTypeLoadException` → `TypeLoadException: … expected class 'Newtonsoft.Json.JsonConverter' in assembly 'Newtonsoft.Json, Version=13.0.0.0'` | **无 TypeLoadException**（该 mod 的错误一并消失） |
| ColonyRecorder | **正常加载**：`[ColonyRecorder][run] … runId=20261004-114941-0-c942`（三流文件已创建） | **正常加载**：`runId=20261004-115726-0-ce9c`，`colony/actions/events` 三流均已建文件并开始写 |
| 游戏本体 | 正常进入（能开正式档、能玩） | 正常进入；写文档时仍在运行（PID 12792，`Player.log` 仍在增长） |

**上表的落点**：**"Doorstop 生效"与"RimBridgeServer 加载失败"在同一台机器上、同一份 mod 文件上强相关，且加一个开关就能翻转**。这足以支撑"Doorstop 的加载期补丁是触发条件"这一**推断**，但**还不足以**断言唯一根因（见 §4、§8）。

### 3.3 Steam 侧（`【已证实】`，只读读取，未做修改）

| 项 | 值 |
| --- | --- |
| 当前 RimWorld（appid 294100）`LaunchOptions` | `-popupwindow --doorstop-enabled false` |
| 改动前原值（备份 `tmp/localconfig.vdf.bak-orig`） | `-popupwindow` |
| 备份时间 | 2026-10-04 11:55:23（`tmp/localconfig.vdf.bak-orig`） |
| 当前 `localconfig.vdf` | `C:\Program Files (x86)\Steam\userdata\1018749175\config\localconfig.vdf`（mtime 11:57:21） |
| Steam 进程 | 当前 `steam` PID 39300，启动于 11:56:38（**说明 11:56 前后做过一次 Steam 重启**） |

⇒ **Steam 的角色就是把这段字符串拼到游戏命令行**：`Player.log` 第 24 行"Command line arguments"里的 `--doorstop-enabled false` 与 `LaunchOptions` 完全一致【已证实】。**"Steam 是根因"是错误方向**，不要在后续调查里重犯。

---

## 4. 推断（有证据支持的因果解释，但**未**做对照实验）

1. **`【推断】`RimBridgeServer.Core 失败的直接机制是"加载顺序"**：RimWorld 按文件名遍历 `Assemblies\`，而 Doorstop 的热重载补丁在**每个程序集被加载的那一刻**就用反射去读它的自定义特性；读到 `Core.dll` 时，排在它后面的 `RimBridgeServer.Extensions.Abstractions.dll`（`Core` < `Extensions…` < `Sdk` < `RimBridgeServer.dll`）**还没进加载上下文**，于是 `IRimBridgeCapabilityProvider` 解析失败。
   - 支持：异常栈顶端正是 `ILReloaderLib.Tools.IsReloadable` ← `AssemblyLoadingPatcher.RegisterTypes` ← `Verse.ModAssemblyHandler.ReloadAll_Patch1`；且失败发生在 **Core 那一条加载记录上**，而不是在 mod 初始化阶段。
   - **反证/替代假设**（未排除）：① Mono 的按名解析路径问题——该 mod 故意把 Newtonsoft 改名成 `0Newtonsoft.Json.dll` 来排顺序，同一会话里 `BetterModSort` 也报 Newtonsoft 解析失败，说明"名字/搜索路径"这类问题确实在本机存在；② `Core` 与 `Abstractions` 的**程序集版本一致性**问题（本轮核对 9 个 DLL 全是 2.1.1，暂不支持此假设，但未做二进制引用表核对）；③ mod 目录安装不完整/被 Steam 覆盖过。
2. **`【推断】`Doorstop 的 `[Harmony]` 段（指向 workshop 的 `0Harmony.dll`）不是本疑点的必要因素**：失败栈落在 `ILReloaderLib`，而 ini 里 Harmony 的用途是"热重载时用 RimWorld 已加载的 Harmony"。**未做**"ini 里去掉 Harmony 段"的对照。**不要据此断言 Harmony 无关**。
3. **`【推断】`关掉 `debug_enabled` 不会修好桥接**：`debug_enabled` 只控制 56000 上的 Mono 调试服务器；类型解析失败发生在程序集**加载**路径上（由 `ILReloaderLib` 的补丁触发，而不是由调试服务器触发）。同一份 ini 里 `enabled=true` 才是总开关。**未验证**。
4. **`【推断，工作假设】`"运行时无感"**：RimBridge 是外部控制面；ColonyRecorder 与 RimWorld 本体都不依赖它。两次会话里 ColonyRecorder 都成功初始化并写盘，游戏本体也都能正常进入/游玩 ⇒ 桥接失败**主要影响自动化验证**。**证据边界**：本轮**没有**做"关掉桥接后跑一局、验证本体行为/存档/性能"的对照；也没验证"桥接失败时 RimWorld 是否打了别的降级补丁"。**不得把它写成"对游戏完全无影响"的结论。**

---

## 5. 时间线（2026-10-04，Asia/Shanghai）

| 时间 | 事件 | 证据 / 标注 |
| --- | --- | --- |
| 11:49:41 | **无开关**会话启动（父会话报 PID 34076）；runId `20261004-114941-0-c942` | `profiles/steam-attach-20261004/Player.log`（556 万字节，会话日志副本，mtime 11:52:52）`【已证实】` |
| 该会话内 | `RimBridgeServer.Core` TypeLoadException → 桥接整体失败；`BetterModSort` 同类失败；ColonyRecorder 正常 | 同上日志第 116/119、87–94、751 行 `【已证实】` |
| 11:52:46 | 写下只读探针 `tmp/probe-steam-bridge.mjs`（注释点名 PID 34076），用于确认 5174/56000 | 文件本身 `【已证实】` |
| 11:52:52 | 该会话日志被复制到仓库 `profiles/steam-attach-20261004/Player.log` | 文件 mtime `【已证实】` |
| — | 探针结果：**5174 无监听、56000 在监听** | 父会话实测 `【未验证】`（本轮未能复现该时刻） |
| 11:54:47 | 记录旧根目录两份老采集数据的 sha256（防改动基线） | `tmp/oldroot-hashes-before.json` `【已证实】` |
| 11:55:23 | 备份 Steam `localconfig.vdf`（备份里 `LaunchOptions=-popupwindow`） | `tmp/localconfig.vdf.bak-orig` `【已证实】` |
| ~11:55–11:56 | **带 `--doorstop-enabled false`** 的实例启动后**约 5 秒退出/崩溃**；父会话报 **PID 11008**，**原因未定** | PID 与"5 秒"来自父会话 `【未验证】`；候选痕迹 = `…\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Player-prev.log`（20,183 B，mtime **11:56:16**，命令行确实含开关，内容只有启动头 + Unity 内存分配报告，**无** GABP 行、**无** TypeLoadException）`【推断，非定论】`；未找到 RimWorld 崩溃转储；Windows 应用日志 11:56:41 的 `Application Error` 属于 **ScanSession.exe**（我们自己的探针），与 RimWorld 无关 `【已证实】` |
| 11:56:38 | **Steam 重启**（当前 steam PID 39300） | 进程启动时间 `【已证实】` |
| 11:57:20 | 新实例启动（**PID 12792**，父进程 Steam）；命令行含 `-popupwindow --doorstop-enabled false` | 进程表 + LocalLow `Player.log` 第 24 行 `【已证实】` |
| 11:57:26 | 桥接正常：`[RimBridge]` 启动计时、capabilities、**`GABP server running standalone on port 5174`**、`Bridge token: 0afd7907…` | 同上日志第 143–252 行 `【已证实】` |
| 11:57:26 | ColonyRecorder 新 runId `20261004-115726-0-ce9c`，三流落盘 | 同上日志第 157、346、348、458 行 `【已证实】` |
| 12:0x（本轮复核） | 端口复查：**5174 OPEN / 56000 无监听**；`Player.log` 仍在增长（25,088 → 50,158 B）⇒ 用户在正常游玩 | 只读探针 / 文件 mtime `【已证实】` |

**两个易被误读的点**：

- `Player.log` 里"Command line arguments / RimWorld 1.6.4871 rev591"这一对**各出现两次**（第 24/101 行）——同一进程的启动头被写了两遍，**不是两个进程**（两个会话的日志分别在两份文件里）。`【已证实】`
- 11:56 那个 `Application Error` **不是游戏崩溃**（是 `ScanSession.exe`）。`【已证实】`

---

## 6. 组件关系图（文字版）

```
   Steam（只做一件事：读 LaunchOptions → 拼命令行 → 起 exe）
        │   "… -popupwindow [--doorstop-enabled false] …"
        ▼
   RimWorldWin64.exe
        │
        ├──[原生注入] winhttp.dll  ──────►  Doorstop.dll（pardeike/Rimworld-Doorstop）
        │                                     ├─ Mono 调试服务器 @127.0.0.1:56000   （debug_enabled）
        │                                     └─ ILReloaderLib（热重载；patch ModAssemblyHandler.ReloadAll）
        │                                              │  每个 mod 程序集加载时都会被"过一遍"（读特性）
        │                                              ▼
        ├──[普通 mod 加载顺序]  Mods\RimBridgeServer\1.6\Assemblies\*.dll
        │        RimBridgeServer.Contracts → .Core → .Extensions.Abstractions → .Sdk → RimBridgeServer
        │        + 0Newtonsoft.Json / Gabp.Runtime / Lib.GAB / MoonSharp
        │        └─► GABP 服务 @127.0.0.1:5174 + token ──►  外部 agent / 自动化测试
        │        └─► BridgeTools\*（DSH.CEBridge / DSH.FoodProbe 伴生 DLL）
        │
        └──[普通 mod 加载顺序]  Mods\ColonyRecorder（dsh.colonyrecorder）
                 └─► foodcontrol-snapshots\runs\run-<runId>\{colony,actions,events}-*.jsonl

  故障路径（推断）：Doorstop 的 ILReloader 在加载期读 Core 的特性
                     → 那时 Extensions.Abstractions 还没加载 → TypeLoadException → RimBridgeServer 整体不加载 → 5174 无监听
```

**三条链路的独立性**（后续验证要分开看）：① 游戏本体（能不能进游戏/读档/正常玩）；② ColonyRecorder（runId + 三流文件是否在写）；③ RimBridge（5174 + token + 至少一个工具调用成功）。**历史上②在①③都出问题时都是好的** —— 这正说明三者不该混为一谈。

---

## 7. 影响判断：为什么"实际游玩可能无感"（工作假设 + 证据边界）

**支持"无感"的证据**：

1. RimBridge 是**外部控制面**（GABP 服务端），不是 RimWorld 本体的一部分；RimWorld 的 mod 加载失败被 `Verse.ModAssemblyHandler` 捕获后**继续加载其他 mod**（日志里失败之后 ColonyRecorder 与其余 380+ mod 照常加载）。
2. **ColonyRecorder 在 Doorstop 开着的失败会话里依然成功**（runId `…114941`，三流文件写成）⇒ 录制器不依赖桥接。
3. 用户在"无开关"会话里也能进游戏（该会话跑到 11:52 仍有 ColonyRecorder 录制行）。
4. GABP/桥接只对**agent 驱动**有用；手动游玩不需要它。

**证据边界（必须写进结论里的话）**：

- 本轮**没有**做"桥接失败 vs 桥接正常"的**本体功能对照**（存档读写、性能、UI、其他 mod 行为）。
- 未验证"桥接失败时 RimWorld 是否打了别的降级补丁"、"是否有 mod 强依赖 RimBridgeServer"（本机 `FoodControl` 的 `About.xml` 里就写了 `brrainz.rimbridgeserver` 依赖）。
- 因此**只能写**："现有证据**指向**桥接失败主要影响自动化验证，**不必然**影响正常游玩"；**不能写**"对游玩没有影响"。

---

## 8. 待验证问题清单（每个都写清"怎么判定"）

| # | 问题 | 判定方式（可观测） |
| --- | --- | --- |
| **Q1** | `TypeLoadException` 的**精确机制**：是"加载顺序"还是"程序集解析/版本"问题？ | 用 Mono.Cecil/`monodis`/`ildasm` 读 `RimBridgeServer.Core.dll` 的 `AssemblyRef`/`TypeRef` 表（确认 `IRimBridgeCapabilityProvider` 的 token 0100001f、目标程序集与版本），并读 `Core` 是否有 `[assembly: …]`/自定义特性在类型解析前被读；再对照"把 `Extensions.Abstractions` 改名成 `0…` 提到 Core 前面"的实验结果 |
| **Q2** | 触发者是 **Doorstop 本体**还是 **ILReloaderLib 热重载**？ | 看 Rimworld-Doorstop / ILReloader 是否提供"只开调试服务器、不开热重载"的配置位；有则做单变量实验 |
| **Q3** | **启动参数解析**在这里起了什么作用？`--doorstop-enabled false` 到底是"关掉整个 Doorstop"还是"只关注入但调试服务器仍起"？ | 对比带/不带开关时的 56000 端口（本轮：关掉后 56000 **无**监听 ⇒ 参数确实关掉了整套加载） |
| **Q4** | **PID 11008 的约 5 秒退出/崩溃**：是崩溃、被 Steam 重启带走、还是被我们自己杀掉？ | 保留并比对 `Player-prev.log`（11:56:16）与 Steam `logs\console_log.txt`；查 WER/崩溃转储；**不要**靠反复重启来"复现"，那会污染现场 |
| **Q5** | 是否应**永久禁用 Doorstop**？ | 需回答代价：失去 Mono 调试 + IL 热重载；以及"谁在用"（本仓库的自动化链路是否依赖调试器） |
| **Q6** | 是否应改成**改 RimBridgeServer 部署**（重排/改名 DLL 或升级版本）或**更新依赖**？ | 查 upstream 是否已有修复版本/issue（`IRimBridgeCapabilityProvider` + Doorstop/ILReloader 关键词）；注意改 mod 目录会被 Steam 更新覆盖 |
| **Q7** | **关掉 `debug_enabled` 是否安全**？ | `【推断】`不该影响桥接（§4.3），但需实测：改 ini → 只启一次 → 看 5174/56000/日志；**改 ini 前必备份，且需用户同意** |
| **Q8** | **配置恢复策略**：`LaunchOptions` 要不要恢复成 `-popupwindow`？`doorstop_config.ini` 要不要动？ | 明确"临时桥接规避"与"长期默认"的区别；恢复唯一依据 = `tmp/localconfig.vdf.bak-orig` |
| **Q9** | `src/launcher.js:233` 的 `DOORSTOP_ENABLED: '0'` 是不是死代码？ | 与 upstream v4 文档对照（只列 ini + CLI）⇒ 很可能是；但**本轮不改代码** |

---

## 9. 建议的后续研究计划（给接手的 agent；每一步都写清"能不能碰现场"）

> **总原则**：先只读收集 → 再设计单变量矩阵 → **每次实验前征得用户同意** → 一次只改一项且有备份 → 每项只启一次、**绝不自动连续重启 Steam/游戏**。

### 阶段 0：只读收集（**不启动任何东西**）

1. **一手文档/仓库**：Unity Doorstop 的 README（`--doorstop-*` 全集语义、`enabled` 与 CLI 的优先级）；pardeike/Rimworld-Doorstop README（`Doorstop.dll` 职责、ILReloaderLib 版本）；pardeike/ILReloader 的 release notes；RimBridgeServer README + `docs/architecture.md`；在 RimBridgeServer / RMWorld-Doorstop 的 issue 区搜 `IRimBridgeCapabilityProvider`、`TypeLoadException`、`Doorstop`。
2. **本机程序集取证**（只读）：`RimBridgeServer\1.6\Assemblies\` 全部 9 个 DLL 的名称/大小/程序集版本/文件版本/sha256（**本轮已取一份，见 §12.3，务必复核并补"程序集版本"与引用表**）；`Doorstop.dll` 的文件/产品版本（README 声称 1.8.2.0，**本轮未核对**）；ILReloaderLib 的来源与版本。
3. **配置全文**：`doorstop_config.ini`（13 行）、`Version.txt`、Steam `localconfig.vdf` 里 294100 的 `LaunchOptions`（**只读**）。
4. **日志全文**（都要保原始副本，不要只留 tail）：§12.2 的三份 `Player.log` / `Player-prev.log`；重点抽取——完整"程序集加载序列"、`Command line arguments` 行、`TypeLoadException` 全文与栈、`[Info] loading` 行数（用于判定 Doorstop 是否生效）、`[RimBridge]` 全部行、`[ColonyRecorder]` 入口行。
5. **Steam console 日志**（只读）：`C:\Program Files (x86)\Steam\logs\console_log.txt`（及 `content_log.txt`），确认启动参数是怎么传的、以及 11:56 那次重启期间发生了什么。

### 阶段 1：单变量矩阵（**先征得用户同意**；每格只改一项）

| # | Doorstop 开关 | 额外变量 | 想验证什么 | 每格必记录 |
| --- | --- | --- | --- | --- |
| A | **现状**（`--doorstop-enabled false`） | — | 基线：桥接应起（5174 + token） | 命令行、5174/56000、`[RimBridge]` 行、runId |
| B | 移除开关（恢复 `-popupwindow`） | — | 复现失败：应有 TypeLoadException、5174 无监听、56000 监听 | 同上 |
| C | 开（保留开关为 true/删除） | 只关调试服务器（`debug_enabled=false` 或对应 CLI） | Q7：debug 与 TypeLoad 有无因果 | 同上 + 是否仍 TypeLoadException |
| D | 开 | **不动 ini**，把 `RimBridgeServer.Extensions.Abstractions.dll` 改名成 `0RimBridgeServer.Extensions.Abstractions.dll`（**改 mod 文件，必须先备份**） | Q1：加载顺序假设 | 同上 + 是否仍有 TypeLoadException |
| E | 开 | 从 `ModsConfig.xml` 临时禁用 RimBridgeServer | 分离问题：Doorstop 是否对**第三方 mod 普遍有害**（看 `BetterModSort` 是否仍报 Newtonsoft TypeLoadException） | 同上（无桥接是预期的） |

### 阶段 2：判定与决策选项（把结论写成"选项 + 代价"，不要自行拍板）

1. **保持现状**（命令行临时开关）：零改动，但"手动玩"与"自动化"两套启动方式长期分裂。
2. **永久禁用 Doorstop**：改 `doorstop_config.ini` 的 `enabled=false`（**需同意 + 备份**）；代价＝失去 Mono 调试与 IL 热重载。
3. **改 RimBridgeServer 部署**（重排/改名 DLL 或升级到修复版本）：能同时保住热重载，但**会被 Steam/workshop 更新覆盖**。
4. **升级/替换依赖**（Abstractions/Sdk 或改 RimBridgeServer 版本）。
5. **关闭 `debug_enabled`**（若 Q7 证明无关，则不是解药；若用户不再用调试器，可作为"顺手减负"）。
6. **配置恢复策略**：明确"临时桥接规避"结束条件；恢复依据是 `tmp/localconfig.vdf.bak-orig`；**恢复动作要用户点头**。

### 阶段 3：三条链路**独立**验证（别用一条的成功掩盖另一条的失败）

- **本体**：进游戏 → 读档/存档一轮 → 观察有无新报错。（不需要桥接）
- **ColonyRecorder**：`runId` + `runs\run-<runId>\` 下 `colony-/actions-/events-*.jsonl` 三份**同时**在长。
- **RimBridge**：5174 可连 + token 正确 + 至少一次真实工具调用成功（如 `rimbridge/ping`，或 `node src/cli.js tools`）。

---

## 10. 约束 / 安全规则（后续 agent 必须遵守）

1. **不自动重启 Steam、不自动重启游戏**；不写"循环重试直到复现"的脚本。
2. 任何实验**先征得用户同意**，并且**一次只改一项**；改动前先备份（Steam `localconfig.vdf`、`doorstop_config.ini`、`ModsConfig.xml`、被改名的 DLL）。
3. **不动 `D:\SteamLibrary\steamapps\common\RimWorld\` 下的 mod 文件与 ini**，除非用户明确批准并已备份。
4. **不改任何已采集数据**：旧根目录两份文件保持原样（基线哈希见 §12.4）；`profiles/` 与 `runs/` 下的历史日志只增不改。
5. **不做"关掉现场/清理现场"式的整理**（例如把 `Player-prev.log` 覆盖、把 `profiles/steam-attach-20261004/` 改名）。
6. 结论必须区分 **已证实 / 推断 / 未验证**；**不要把推断写成定论**，尤其不要写"对游戏完全无影响"。
7. 不要把 `Bridge token` 明文写进新文档/提交（本轮已刻意只写前 8 位）。
8. 本轮**只写文档**：不改源码、不改插件、不部署 DLL。

---

## 11. 当前安全状态（写文档时刻）

- **不再自动重启 Steam/游戏**（本轮全程只读；最后一轮只做了三件只读事：读文件、读事件日志、连一次本地 5174/56000 端口探针）。
- **当前用户可手动游玩**：PID 12792 正在运行，`Player.log` 在持续增长，ColonyRecorder 在写 `run-20261004-115726-0-ce9c`。
- **Steam `LaunchOptions` 临时含 `--doorstop-enabled false`**：这是**桥接规避**，**不是 ColonyRecorder 的必要条件**（门控仅在 Doorstop × RimBridgeServer 这条链上）。原值 `-popupwindow` 已备份在 `tmp/localconfig.vdf.bak-orig`，**是否恢复待用户决定**。
- **旧采集数据未被改动**（基线哈希见 §12.4），本轮也没有对 `runs/`、`profiles/` 做任何写入或移动。

---

## 12. 证据路径清单

### 12.1 仓库内文档（先前结论，含本文引用的因果叙述）
| 路径 | 相关位置 | 说明 |
| --- | --- | --- |
| [README.md](README.md#L162-L201) | §"踩过的坑 1. Doorstop 和 RimBridgeServer 冲突" | 最完整的既有叙述：加载顺序假设、异常原文、`--doorstop-enabled false` 解法、"环境变量无效"备注、副作用提醒 |
| [docs/mod开发基础.md](docs/mod开发基础.md#L95-L137) | Doorstop 段落 | "本机装了 Doorstop，提供 IL 热重载 + 调试器"、"实测导致 RimBridgeServer 加载失败"、`doorstop_config.ini` 的 `debug_enabled/debug_address` |
| [docs/RIMBRIDGE-技巧手册.md](docs/RIMBRIDGE-技巧手册.md#L33) | 故障表 | "Doorstop 与 RimBridgeServer ⇒ TypeLoadException，桥接完全起不来" |
| [docs/CHARACTER-EDITOR-可编程驱动调研.md](docs/CHARACTER-EDITOR-可编程驱动调研.md#L489-L545) | SDK / Abstractions 契约 | `IRimBridgeCapabilityProvider` / `RimBridgeCapabilityRegistration` 的本机实测签名 |
| [STATUS.md](STATUS.md#L412-L416) | 用户改口径 | 不再用隔离实例、直接从 Steam 目录启动正常游戏；`ColonyRecorder` 与 `RimBridgeServer` 都在默认档启用 |
| [docs/交接-快照捕捉-接手.md](docs/交接-快照捕捉-接手.md#L53) | 环境表 | RimBridgeServer 在 `127.0.0.1:5174` 提供工具 |
| [src/launcher.js](src/launcher.js#L223) / [#L233](src/launcher.js#L233) | 启动器 | 传 `--doorstop-enabled false`；同时设 `DOORSTOP_ENABLED: '0'`（疑似死代码） |

### 12.2 本机日志（**只读，勿覆盖**）
| 路径 | 内容要点 |
| --- | --- |
| `E:\2026.7以后的新工程\DSH\RimWorld_auto_test\profiles\steam-attach-20261004\Player.log`（5,568,556 B，mtime 11:52:52） | **Doorstop 生效**那次会话的副本：`ILReloaderLib` 栈、`TypeLoadException`（Core/IRimBridgeCapabilityProvider）、`BetterModSort` Newtonsoft 失败、ColonyRecorder runId `…114941-0-c942` |
| `E:\2026.7以后的新工程\DSH\RimWorld_auto_test\runs\Player.log.before-steam-relaunch-20261004.log`（6,991,049 B，mtime 01:58） | 早前一份同类会话（同样 Doorstop 生效 + 同类 TypeLoadException），可作"反复出现"的第二个样本 |
| `C:\Users\MikanseiP\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Player.log`（写文档时 25 KB→50 KB，仍在增长） | **当前 PID 12792**（`--doorstop-enabled false`）：无任何 TypeLoadException；`GABP server running standalone on port 5174`、`Bridge token:`、`[RimBridge]` 启动计时、ColonyRecorder runId `…115726-0-ce9c` |
| `…\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Player-prev.log`（20,183 B，mtime 11:56:16） | 命令行含 `--doorstop-enabled false`，但**内容只有启动头 + Unity 内存报告**，无 GABP、无 TypeLoadException ⇒ PID 11008 的候选痕迹（**推断，非定论**） |
| `C:\Program Files (x86)\Steam\logs\console_log.txt` | **本轮未读**（阶段 0 待办）：用于 argparse 传播与 11:56 重启 |

### 12.3 本机程序集（`D:\SteamLibrary\steamapps\common\RimWorld\Mods\RimBridgeServer\1.6\Assemblies\`，全部程序集版本 2.1.1）
| 文件 | 字节 | SHA256 |
| --- | --- | --- |
| `RimBridgeServer.Contracts.dll` | 22,528 | `909D04D90F65FC48AA0725CBA77784F6D4CB49C564E165873D6F43A97BF7224F` |
| `RimBridgeServer.Core.dll` | 252,928 | `00EADC7D9C487EF39E5C1B4AC004D6E3BE74C4355778E82EC62F720B1DAA8BA1` |
| `RimBridgeServer.Extensions.Abstractions.dll` | 5,632 | `0C41610EB42516D1E02701CDFD3E978AD6951EE8F5A9222212F557A5EFDDB481` |
| `RimBridgeServer.Sdk.dll` | 33,280 | `73AC3CE373B732B86ECC54737193583DA2FFC72DDA1A3788D57F49AD27EBF6FF` |
| `RimBridgeServer.dll` | 1,305,600 | `8B7D8AC6A6D2F7D3F8E63250226D0291B80540524EAB8435BAE018110938E4EE` |
| `0Newtonsoft.Json.dll` | 721,320 | `F0C07AF0E84D4DD4DA4BD7823BA4535BC0481B3BF623ED40B659B68147A6BB75`（文件版本 13.0.4.30916） |
| `Gabp.Runtime.dll` | 25,600 | `D548E8665EFB033E36B5985738D865BCBAF50F9384674D0340B5C5C834FF7696` |
| `Lib.GAB.dll` | 82,944 | `BE3AECCABD93C29271425F146C0EAAD8E37DB66C08F9883A4A5A4A44B9E51014` |
| `MoonSharp.Interpreter.dll` | 366,592 | `1DB76110F21698639F55D28E21BDDB536C0C497CEB741DEE49FEDCCA9BCD1588` |

其他本机文件：`D:\SteamLibrary\steamapps\common\RimWorld\doorstop_config.ini`（13 行）、`winhttp.dll`（26,112 B）、`Doorstop.dll`（3,014,656 B）、`Version.txt`（`1.6.4871 rev590`）、`BridgeTools\`（`DSH.CEBridge`、`DSH.FoodProbe`）。

### 12.4 仓库内的取证/备份文件
| 路径 | 用途 |
| --- | --- |
| `tmp/probe-steam-bridge.mjs` | 只读端口探针（5174 / 56000，各 2.5 s 超时）。本轮复用它做只读复核 |
| `tmp/oldroot-hashes-before.json` | 旧根目录两份采集数据的 sha256 基线（`actions-殖民地-0.jsonl` 1,335,860 B；`colony-殖民地-0.jsonl` 28,052,923 B）⇒ **"旧数据不改"的判定依据** |
| `tmp/localconfig.vdf.bak-orig` | Steam 启动项原值备份（`LaunchOptions=-popupwindow`）⇒ **恢复的唯一依据** |
| `C:\Program Files (x86)\Steam\userdata\1018749175\config\localconfig.vdf` | 当前启动项（`-popupwindow --doorstop-enabled false`）。**只读** |

---

## 13. 引用的一手在线资料（本轮实际抓取过）

- [Unity Doorstop — README（NeighTools/UnityDoorstop）](https://github.com/NeighTools/UnityDoorstop)：Doorstop 定义（"execute managed .NET assemblies inside Unity as early as possible"）、`winhttp.dll` 注入、`doorstop_config.ini` 与 `--doorstop-*` CLI 两套配置、`--doorstop-enabled bool`、`--doorstop-mono-debug-enabled/-address/-suspend`、UnityMono 下 `Doorstop.Entrypoint.Start()` 约定、"early execution 允许重定向部分游戏程序集加载"。
- [RimWorld Doorstop — README（pardeike/Rimworld-Doorstop）](https://github.com/pardeike/Rimworld-Doorstop)：`Doorstop.dll` 的职责＝启用 Mono 调试服务器 + 基于 **ILReloaderLib v1.0.5** 的 `[Reloadable]` 热重载；不再自带 Harmony、改为 ini 指向 RimWorld 已加载的 `0Harmony.dll`；示例 ini 与本机 `doorstop_config.ini` **逐字同构**（含 `debug_address=127.0.0.1:56000`）；"launcher 在别的目录启动会绕过 `winhttp.dll`"。
- [RimBridgeServer — README（pardeike/RimBridgeServer）](https://github.com/pardeike/RimBridgeServer)：定位＝把运行中的游戏变成桥接；**direct 模式**会打印 `[RimBridge] GABP server running standalone on port 5174` 与 `[RimBridge] Bridge token: …`（与本机日志逐字一致）；`BridgeTools` 伴生 DLL 加载规则；`RimBridgeServer.Sdk` 是唯一共享依赖；GABS 为推荐模式。

> 本轮**没有**抓取 Steam / Ludeon 官方文档，也没有查 RimBridgeServer 的 issue 区（留给阶段 0）。

---

## 附录 A：关键日志原文（节选，`Bridge token` 已截断）

**Doorstop 生效时（`profiles/steam-attach-20261004/Player.log`）**

```
[Info] loading …\Mods\RimBridgeServer\1.6\Assemblies\RimBridgeServer.Contracts.dll
[Info] loading …\Mods\RimBridgeServer\1.6\Assemblies\RimBridgeServer.Core.dll
Exception loading RimBridgeServer.Core.dll: System.TypeLoadException: Could not resolve type with token 0100001f
    from typeref (expected class 'RimBridgeServer.Extensions.Abstractions.IRimBridgeCapabilityProvider'
    in assembly 'RimBridgeServer.Extensions.Abstractions, Version=2.1.1.0, Culture=neutral, PublicKeyToken=null')
  at ILReloaderLib.Tools.IsReloadable (System.Reflection.MethodBase method)
  …
  at ILReloaderLib.AssemblyLoadingPatcher.RegisterTypes (System.Reflection.Assembly assembly)
  at ILReloaderLib.AssemblyLoadingPatcher.LoadFrom (System.String path)
  at (wrapper dynamic-method) MonoMod.Utils.DynamicMethodDefinition.Verse.ModAssemblyHandler.ReloadAll_Patch1(Verse.ModAssemblyHandler)
```

**桥接正常时（当前 PID 12792，LocalLow `Player.log`）**

```
[RimBridge] GABP server running standalone on port 5174
[RimBridge] Bridge token: 0afd7907…
[ColonyRecorder][run] 本次启动 runId=20261004-115726-0-ce9c；三条流（colony/actions/events）都写到
    C:/Users/MikanseiP/AppData/LocalLow/Ludeon Studios/RimWorld by Ludeon Studios\foodcontrol-snapshots\runs\run-20261004-115726-0-ce9c（第 1 次尝试成功）
```

## 附录 B：术语速查

| 术语 | 一句话 |
| --- | --- |
| Doorstop | Unity 的**托管代码早期注入**层（`winhttp.dll` 代理 + 托管入口） |
| Rimworld-Doorstop / `Doorstop.dll` | pardeike 的 Doorstop 配套：Mono 调试服务器 + ILReloader 热重载；**本文疑点的补丁方** |
| ILReloaderLib | 热重载库；其 `AssemblyLoadingPatcher.RegisterTypes` 会在加载期读自定义特性 |
| GABP | RimBridgeServer 的桥接协议；direct 模式在 `127.0.0.1:5174` 监听并打印 token |
| 5174 / 56000 | 5174＝桥接（RimBridgeServer）；56000＝Doorstop 的 Mono 调试器。**端口能区分"谁活着"** |
| ColonyRecorder（`dsh.colonyrecorder`） | 只读录制器，与桥接**无依赖**；以 `runId` 分目录写三条流 |
