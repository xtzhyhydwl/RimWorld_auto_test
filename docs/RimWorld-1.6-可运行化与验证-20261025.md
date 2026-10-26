# RimWorld 1.6 可运行化与验证（2026-10-25）

把并行安装的 1.6（`D:\RimWorld-1.6`）从「一加载 mod 就崩」修到「能进主界面、桥接可用」的记录。
凡标「实测」的都有日志/截图/命令输出为证；标「推理」的是从证据推出、未单独验证的结论。

## 结论

1. **清场**：开工时有 1 个遗留 1.6 实例（pid 444，由另一条并行命令起，`-savedatafolder=D:\rw16data`），另有并行会话的 `D:\rw16data4` 实例；最终 `RimWorldWin64.exe` = 0，`RimWorld.exe`(1.7) = 0（实测）。
2. **1.6 编译 = 0 警告 0 错误**（实测，`-c Release -p:RimWorldVersion=1.6 -p:RimWorldDir=D:\RimWorld-1.6`）。
3. **IrisMenus 路线**：直接用 **1.6 版 IrisMenus 1.1.1.0** 编译就成功，**没有**去掉 `RHAH_IRISMENUS` 宏（实测，反射确认 AssemblyRef = `IrisMenus 1.1.1.0`）。
4. **装了哪份 DLL**：`HungerAndHavoc.dll` B3F0F895…（865792 B）+ `HungerAndHavoc.Api.dll` C8514173…（14848 B）。注意 `Mods\ratkin_hunger_and_havoc` 是指向仓库的 junction，**构建输出目录 = 部署目录**（实测）。
5. **是否到主界面**：到（实测，截图 `runs\ascii-path-test\v16-mainmenu.png` 可见 `RimWorld 1.6.4871` 与主菜单按钮）。
6. **关键错误计数**（实测，`D:\rw16data2\Player.log`）：Exception 8 / `Illegal byte sequence` **0** / TypeLoad 7 / `Could not resolve` 6 / `get_JustPressed` 0 / `Root level exception` 0；`Applied essential Harmony patches` **1**；`Could not find a type named … IncidentWorker_Sequel` **0**。
7. **桥接**：可用（实测）。GABP 监听 `127.0.0.1:5174`，`tools/list` 返回 125 个工具，`rimbridge/ping` → `pong`，`rimworld/list_mods` → `activeCount 14 / loadedSessionModCount 14 / restartRequired false`。
8. **能否自动验证**：能。1.6 实例可以直接用现有 `src/gabp.js` 客户端驱动（实测）。

剩余 8 条异常**不是 RHAH 的**：全部来自 OxygenDSL 的 IrisMenus 适配层 `Oxygendsl.IrisMenus`，它按 `IrisMenus, Version=0.0.1.0` 编译，而运行期加载的是 1.1.1.0（实测，原文见下）。不致命，游戏照常进主界面。

## 真正的根因：进程工作目录含中文（不是存档路径）

| 运行 | `-savedatafolder` | 进程 CWD | `Illegal byte sequence` | Exception | 结果 |
|---|---|---|---|---|---|
| 并行那次（`run0-preexisting-ascii.log`） | `D:\rw16data`（纯 ASCII） | 中文（继承自中文工程目录） | 7 | 1069 | Harmony 静态初始化崩，进不去 |
| M2b（Harmony+OxygenDSL+CharacterEditor） | `D:\rw16data` | 中文 | 2 | 16 | 同上，3 秒即崩 |
| M2c/M2d（同 mod 集） | `D:\rw16data` | `D:\RimWorld-1.6`（ASCII） | **0** | **0** | 正常 |
| M6（全 13 mod） | `D:\rw16data` | ASCII | **0** | 8 | 正常 |

- `Command line arguments:` 行**不再乱码**，`Save data folder overridden to D:\rw16data` 正常 ⇒ **`-savedatafolder` 从来没问题**（实测）。
- 崩溃栈是 `HarmonyLib.Tools.FieldRefAccess` → `MonoMod.Utils…GetName()` → `System.Reflection.RuntimeAssembly.get_code_base` → `System.ExecutionEngineException: String conversion error: Illegal byte sequence`（实测）。即 Mono 在把**程序集路径（由 CWD 派生）**转字符串时失败（推理）。
- 因此：**任何 1.6 启动都必须带 `-WorkingDirectory 'D:\RimWorld-1.6'`**。

## 步骤与命令

```powershell
# 1) 清场：只杀 1.6 的 RimWorldWin64.exe，绝不动 1.7 的 RimWorld.exe
Get-CimInstance Win32_Process -Filter "Name='RimWorldWin64.exe'" | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }

# 2) 编译 1.6（两个覆盖缺一不可）
dotnet build RimworldMods\ratkin_hunger_and_havoc\Source\HungerAndHavoc.csproj -c Release `
  -p:RimWorldVersion=1.6 -p:RimWorldDir=D:\RimWorld-1.6 `
  -p:IrisMenusDll=D:\RimWorld-1.6\Mods\IrisMenus\1.6\Assemblies\IrisMenus.dll `
  -p:OxygenDslBuildDir=D:\RimWorld-1.6\Mods\OxygenDSL\1.6\Assemblies

# 3) 启动（三条参数缺一不可）
Start-Process -FilePath 'D:\RimWorld-1.6\RimWorldWin64.exe' `
  -ArgumentList '-savedatafolder=D:\rw16data2 -logFile D:\rw16data2\Player.log' `
  -WorkingDirectory 'D:\RimWorld-1.6' -PassThru

# 4) 只读验证桥接
node --input-type=module -e "import {GabpClient} from './src/gabp.js'; const c=new GabpClient({port:5174, token:'<日志里的 token>'}); await c.connect(); console.log(await c.callTool('rimbridge/ping')); c.close();"
```

两个坑（实测）：

- `OxygenDslBuildDir` 默认指向 `RimworldMods\oxygen_dsl_framework\tmp\build`，该目录**不存在**，不覆盖会报 44 个 `CS0246: 未能找到类型或命名空间名"Oxygendsl"`。
- `Mods\ratkin_hunger_and_havoc` 是 junction → 编译直接写进「已部署」位置，**不存在"先备份再覆盖"的窗口**。

## 日志关键行原文（`D:\rw16data2\Player.log`）

```
Command line arguments: -savedatafolder=D:\rw16data2 -logFile D:\rw16data2\Player.log
RimWorld 1.6.4871 rev591
Save data folder overridden to D:\rw16data2
[RimBridge] Applied essential Harmony patches.
[RimBridge] Applied 56 optional Harmony patch classes.
Alien race successfully completed 280 patches (50 pre, 82 post, 148 trans) with harmony.
[RimBridge] Startup conditions satisfied after play-data load; initializing bridge services.
[RimBridge] Applied 6 late virtual-input patch classes.
[RimBridge] GABP server running standalone on port 5174
[RimBridge] Bridge token: 6b560f2d6c9446439158865f1f6ae091
```

仅剩的 8 条异常（非致命，OxygenDSL 侧）：

```
ReflectionTypeLoadException getting types in assembly Oxygendsl.IrisMenus: System.Reflection.ReflectionTypeLoadException
Could not resolve type with token 0100003e from typeref (expected class 'IrisMenus.IMarkdownParser' in assembly 'IrisMenus, Version=0.0.1.0, ...')
Could not resolve type with token 01000020 from typeref (expected class 'IrisMenus.MenuSearchEntry' in assembly 'IrisMenus, Version=0.0.1.0, ...')
```

## 产物 SHA-256

| 用途 | 文件 | 大小 | SHA-256 |
|---|---|---|---|
| 新构建·主程序 | `HungerAndHavoc.dll` | 865792 | `B3F0F8956E594E80F4E71632802BDD4002E80469CF4626CE2BD06B0D2356F610` |
| 新构建·API | `HungerAndHavoc.Api.dll` | 14848 | `C8514173A19CC47993C4144835BBDFC0E2CFEFB5B6A14D321FCC3B86BEF5014A` |
| 备份·git HEAD 主程序 | `HungerAndHavoc.dll.HEAD` | 848384 | `B987BF67D8EB42B8AD7FDE6A97871BB336AC9888A4B84BC2F0C2E24D0AD35BC6` |
| 备份·git HEAD API | `HungerAndHavoc.Api.dll.HEAD` | 14848 | `0167D1B1F3D6160FBEB4B44C76A9140CFD4A949A943428A794B7E8362DEC9691` |

符号自检（字节串）：新构建含 `JobGiver_RHAH_DigOutWhenNoExit`、含 `IncidentWorker_Sequel`、**不含** `famineVisitorsOpenDoors`；git HEAD 旧版恰好相反（不含 DigOut、含 famineVisitorsOpenDoors）——说明工作树里那份构建**本来就比 HEAD 新**。

备份目录：`runs\1.6-rhah-dll-backup-20261025\`

## 回退步骤

1. 把两份 `.HEAD` 文件改名回 `HungerAndHavoc.dll` / `HungerAndHavoc.Api.dll`，覆盖 `RimworldMods\ratkin_hunger_and_havoc\1.6\Assemblies\`（即游戏看到的目录）。
2. 或直接 `git -C RimworldMods checkout -- ratkin_hunger_and_havoc/1.6/Assemblies`（回到 HEAD 提交版）。
3. 回退后需带 `-WorkingDirectory 'D:\RimWorld-1.6'` 重启才谈得上对照。

## 未验证

- **RHAH 自己的 IrisMenus 界面**没在游戏里打开过（只确认它编译进 1.6 版依赖、加载无 TypeLoad）；RHAH 任何游戏内行为均未跑（只到主界面 + 只读桥接调用）。
- OxygenDSL 的 `Oxygendsl.IrisMenus` 适配层那 8 条异常没修（属 `oxygen_dsl_framework` 侧，需按 1.1.1.0 重编）。
- 没能保留「构建前工作树里那份 1.6 DLL」的原件：junction 导致构建即覆盖，我拿到的是 git HEAD 版（更旧）。
- 未跑 M0（Core+5 DLC 单跑）基线，也未验证开殖民地 / 存档 / 读档。
- `Version.txt` 写 `1.6.4871 rev590`，但运行期自报 `rev591`；ModsConfig 我用的是 `rev590`，未见版本相关报错，但两者为何不一致未查。
