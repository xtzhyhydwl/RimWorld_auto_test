# RHAH 1.6 程序集重建报告（20261024）

从源码重建 `ratkin_hunger_and_havoc`（RHAH）1.6 程序集，并验证产物内容。全部操作在 Windows 本机完成，未部署到游戏/Workshop，未提交 git。标注「实测」均为本机命令输出；标注「推测」为未验证判断。

## 结论

1. **成功**：1.6 目标构建通过（`已成功生成。0 个警告 0 个错误`），产物写回 `RimworldMods\ratkin_hunger_and_havoc\1.6\Assemblies\`；重复 `-t:Rebuild` 得到相同 SHA256（同机可复现）。
2. **含兜底方法**：新 DLL 的 `HungerAndHavoc.Pawn.RHAH_ArmedDemand` 有 `Owner`/`Refresh`/`Finish`/`Tick`/`NotifyPlayerHarm`（旧 DLL 一个都没有），并新增 `FoodRequest`/`NoteFood`/`Remaining`；`RHAH_ChoiceRecord.ArmedFoodNutritionDelivered` 字段名与 Scribe 键 `armedFoodNutritionDelivered` 均出现（旧 DLL 全无）。
3. **可回滚**：构建前已把原两个 DLL 备份到 `runs\build-backup-20261024\`（SHA256 逐一核对），复制回去并删掉新生成的 PDB 即可还原。

## 一、构建链与所需属性（实测）

### 官方入口
- `scripts/deploy.sh` → `python scripts/python/deploy.py <project>`；单 mod 配置在 `scripts/deploy/ratkin_hunger_and_havoc.json`。
- RHAH 的配置用 `"rhah": true` 走模块专用流水线：调用 `python <mod>/scripts/rhah_pipeline.py build --rimworld-dir … --rimworld-version 1.6 --oxygen-dsl-dir <monorepo>/oxygen_dsl_framework`（`deploy.py:157-181`、配置 `build` 段）。
- `ratkin_hunger_and_havoc/Makefile` 的 `make build` = `python3 scripts/rhah.py build`，最终同样进 `rhah_pipeline.py`。
- 真正常用的最小入口是 `python scripts/rhah_pipeline.py build --rimworld-version 1.6 --rimworld-dir <game>`（`Pipeline.build()`，`rhah_pipeline.py:425-431`），实际执行两步 dotnet 构建：`Source/HungerAndHavoc.csproj`（内含对 `Source/Api/HungerAndHavoc.Api.csproj` 的 ProjectReference），然后 `Guard/Source/HungerAndHavocGuard.csproj`。

### 属性分别从哪来
| MSBuild 属性 | 来源 | 备注 |
|---|---|---|
| `RimWorldVersion` | 命令行 `--rimworld-version`（默认 `RIMWORLD_VERSION` 环境变量，兜底 `1.7`）；`Directory.Build.props:3-4` 兜底 `1.7` | 必须显式传 `1.6`，否则按 1.7/net10.0 构建 |
| `RimWorldDir` | `--rimworld-dir`（默认 `RIMWORLD_DIR`/`RimWorldDir` 环境变量）；`Directory.Build.props:5-7` 兜底 `E:/Apps/Steam/steamapps/common/RimWorld`（本机不存在） | 本机必须显式 `D:\SteamLibrary\steamapps\common\RimWorld` |
| `RimWorldManagedDir` | `Directory.Build.props:8-9` 推导为 `$(RimWorldDir)/RimWorldWin64_Data/Managed`（1.6）；流水线也直接传同值 | csproj 用它拼 `Assembly-CSharp.dll` 等引用 |
| `OxygenDslDir` | `--oxygen-dsl-dir`（默认 `OXYGEN_DSL_DIR` 或 monorepo 同级 `oxygen_dsl_framework`）；csproj `:18` 与 props `:10` 也有兜底 | — |
| `OxygenDslBuildDir` | **只能由 `Directory.Build.props:11-12` 推导**：1.6 → `$(OxygenDslDir)/tmp/build`，1.7 → `$(OxygenDslDir)/tmp/build/1.7` | csproj 没有对它做兜底，命令行 `-p:OxygenDslBuildDir=` 可覆盖 |
| `HarmonyPath` | `--harmony-path`（默认 `HARMONY_PATH`），流水线默认 `<game>/../../workshop/content/294100/2009463077/<version>/Assemblies/0Harmony.dll` | 主 csproj 实际用 NuGet `Lib.Harmony 2.4.1`；`HarmonyPath` 只被 ODF/Guard 使用 |
| `TargetFramework` | csproj `:3-4`：1.6→`net48`；`Api`/`Guard` 为 `net472` | 两者都要 .NET Framework 参考程序集 |

### ODF（oxygen_dsl_framework）前置：1.6 **不编译**，只要求已有产物
- `rhah_pipeline.py:427-438`：**只有 1.7 才**执行 `dotnet build Source/Mod/Oxygendsl.Mod.csproj`；1.6 直接检查 `$(OxygenDslDir)/tmp/build/Oxygendsl.Core.dll` 与 `Oxygendsl.Mod.dll` 是否存在，缺失即 `Missing … ODF build did not produce …`。
- `docs/agents/automation.md:36` 明写：1.6 需要「保有旧版 `tmp/build` 产物的 ODF 目录」，1.7 构建不重编译 1.6。
- 仓库里 `Oxygendsl.Mod.csproj` 是 **硬编码 `net10.0` + `OutputPath=../../tmp/build/1.7/` + 引用 `UnityStubs.dll`**，没有任何 1.6 目标；`oxygen_dsl_framework/.gitignore` 忽略 `tmp/` 与 `1.6/Assemblies/`。
- **本机现状（实测）**：全新 checkout 里 `oxygen_dsl_framework/tmp/` 根本不存在 → 官方 1.6 流水线在本机必然在 `odf-outputs` 阶段失败。这是本次构建必须绕开的第一个缺口（见第三节）。

### 本机环境（实测）
- `dotnet` 10.0.401；`ratkin_hunger_and_havoc/global.json` 固定 10.0.401，匹配。
- 游戏 `D:\SteamLibrary\steamapps\common\RimWorld`，`Version.txt` = **1.6.4871 rev590**（与任务前提的 rev591 不一致，按实测记录）；`RimWorldWin64_Data\Managed` 内 `Assembly-CSharp.dll` 版本 = **1.6.9676.17735**；无 `UnityStubs.dll`（1.7 专用）。
- Harmony 1.6：`D:\SteamLibrary\steamapps\workshop\content\294100\2009463077\1.6\Assemblies\0Harmony.dll` 存在。
- 环境变量已有 `http_proxy/https_proxy=http://127.0.0.1:7897`，`nuget.org` 直连测试返回 200（走该代理）。
- `%USERPROFILE%\.nuget\packages` 初始不存在；`C:\Program Files (x86)\Reference Assemblies\...\.NETFramework` 不存在（无 VS/Build Tools 开发包）。
- 只有 Windows PowerShell 5.1，**没有 `pwsh`**（`deploy.py` 的 powershell 步骤要求 pwsh；本任务未走该分支）。

## 二、restore 记录（唯一联网步骤，全部指向 nuget.org）

| 包 | 版本 | 大小 | 用途 | 结果 |
|---|---|---|---|---|
| `Lib.Harmony` | 2.4.1 | 49.54 MB（42 文件） | `HungerAndHavoc.csproj:29-32` 的 PackageReference | 由 `dotnet restore` 自动下载 |
| `Lib.Harmony.Ref` | 2.4.1 | 0.53 MB（10 文件） | `iris_menus` 项目的 PackageReference | 同上（构建 IrisMenus 时） |
| `Microsoft.NETFramework.ReferenceAssemblies.net48` | 1.0.3 | 20,997,929 B（20.0 MB） | net48 参考程序集 | `Invoke-WebRequest` 直接下载 nupkg |
| `Microsoft.NETFramework.ReferenceAssemblies.net472` | 1.0.3 | 20,790,130 B（19.8 MB） | `Api`/`Guard` 是 net472 | 同上 |
| 传递依赖 | — | `netstandard.library` 21.42 MB、`system.reflection.emit` 3.22 MB、`microsoft.netcore.platforms` 0.05 MB | netstandard2.0 的 ODF Core | 自动 |

- `dotnet restore Source\HungerAndHavoc.csproj -p:RimWorldVersion=1.6 -p:RimWorldDir=… -p:TargetFrameworkRootPath=…`：**成功，5.5 s**（Api 54 ms，主工程 4.45 s）。注意：不传 `-p:RimWorldVersion=1.6` 时两个 `TargetFramework` 条件都不成立，restore 会因缺 TFM 失败。
- 最终 `~/.nuget/packages` 合计 **74.76 MB**（lib.harmony 49.54 + netstandard.library 21.42 + system.reflection.emit 3.22 + lib.harmony.ref 0.53 + 0.05）。
- 下载耗时：两个参考程序集包合计 3.2 s（3.2 s 内完成重试后成功；首次索引请求有一次 25 s 超时，重试即成功）。
- **没有任何 restore 错误**；也无 NU 警告。
- 参考程序集不落进 csproj（不改仓库文件）：两个包解压后在 `runs\tmp\refasm\root\.NETFramework\{v4.8,v4.7.2}` 合并成一个「参考程序集根」，构建时用 `-p:TargetFrameworkRootPath=<该根>` 传入。这是解决 MSB3644 的关键，也是本机 `dotnet build` 与原版命令唯一的功能性差异之一。

## 三、构建命令与输出摘要（实测）

### 3.1 先补 ODF 1.6（仓库里没有 1.6 目标，用临时副本）
把 `oxygen_dsl_framework/Source` 整树复制到 `runs\tmp\odf16\Source`（**仓库内文件未改**），只改副本里两个 csproj：
- `Core/Oxygendsl.Core.csproj`：保持 `netstandard2.0`，补 `Version 0.0.1 / AssemblyVersion 0.0.1.0`（对齐上游 `Directory.Build.props`），`OutputPath=..\..\out\`。
- `Mod/Oxygendsl.Mod.csproj`：`net10.0` → **`net48`**，`UnityStubs` 引用换成 1.6 的 `UnityEngine(.CoreModule/.IMGUIModule/.TextRenderingModule)`，引用 `Assembly-CSharp 1.6.9676.17735` 与 1.6 `0Harmony`，输出到同一 `out\`。

```powershell
dotnet build runs\tmp\odf16\Source\Mod\Oxygendsl.Mod.csproj -c Release --nologo -v m -p:TargetFrameworkRootPath="…\runs\tmp\refasm\root"
# 实测：已成功生成，0 警告 0 错误，32.7 s（含首次 restore/分析器预热）
```
产物：`Oxygendsl.Core.dll` 262,656 B（`0.0.1.0`，SHA256 `8C1DB5E9…B2A8F9`）、`Oxygendsl.Mod.dll` 169,984 B（`0.0.1.0`，SHA256 `1F2AEAF9…CEE39B4`）。HEAD 的 ODF 源码对 1.6 游戏程序集**直接编译通过**（0 警告），说明 1.7 源码没有用到 1.6 缺失的 API。
> 这两个 DLL **只放在 `runs\tmp\odf16\out\`**，没有写进 `oxygen_dsl_framework/tmp/build/`，仓库保持干净；构建时用 `-p:OxygenDslBuildDir=` 指过去。

### 3.2 IrisMenus（旧 DLL 引用了它，为保持功能对齐而补）
- 旧 DLL 的引用表含 `IrisMenus 1.1.1.0`；本机 `D:\SteamLibrary\…\RimWorld\Mods\IrisMenus` **不存在**。若不管它，csproj `:89-100` 会剔除 `Pawn/Compat/RHAH_IrisMenusCompat.cs`（192 KB）与 `RHAH_IrisMenusWidgets.cs`（16 KB）——实测这样构建出的 DLL 只有 742,400 B，比旧版小 106 KB。
- 因此从源码构建 1.6 IrisMenus 到临时目录（`-p:OutputPath=runs\tmp\iris16\`，未写进 `iris_menus/1.6/Assemblies`）：
```powershell
dotnet build RimworldMods\iris_menus\Source\IrisMenus.csproj -c Release -p:RimWorldVersion=1.6 -p:RimWorldManagedDir="…\RimWorldWin64_Data\Managed" -p:OutputPath="…\runs\tmp\iris16\" -p:TargetFrameworkRootPath="…\refasm\root"
# 实测：成功，0 警告 0 错误，9.3 s；IrisMenus.dll 70,656 B（1.1.2.0，SHA256 89A67DF7…7B4A1FD）
```

### 3.3 RHAH 主构建（等价于 `rhah_pipeline.py` 的 1.6 命令 + 3 个环境补丁）
```powershell
dotnet build "RimworldMods\ratkin_hunger_and_havoc\Source\HungerAndHavoc.csproj" -c Release --nologo -v m `
  -p:RimWorldVersion=1.6 `
  -p:RimWorldDir="D:\SteamLibrary\steamapps\common\RimWorld" `
  -p:RimWorldManagedDir="D:\SteamLibrary\steamapps\common\RimWorld\RimWorldWin64_Data\Managed" `
  -p:OxygenDslDir="…\RimworldMods\oxygen_dsl_framework" `
  -p:HarmonyPath="D:\SteamLibrary\steamapps\workshop\content\294100\2009463077\1.6\Assemblies\0Harmony.dll" `
  -p:OxygenDslBuildDir="…\runs\tmp\odf16\out" `
  -p:IrisMenusDll="…\runs\tmp\iris16\IrisMenus.dll" `
  -p:TargetFrameworkRootPath="…\runs\tmp\refasm\root"
```
- 输出（原始日志 `runs\tmp\rhah-build-1.6-with-iris.log`）：
```
HungerAndHavoc.Api -> …\ratkin_hunger_and_havoc\1.6\Assemblies\HungerAndHavoc.Api.dll
HungerAndHavoc     -> …\ratkin_hunger_and_havoc\1.6\Assemblies\HungerAndHavoc.dll
已成功生成。 0 个警告 0 个错误   已用时间 00:00:01.13
```
- 首次（无 IrisMenus）构建 3.1 s；带 IrisMenus 增量 1.4 s；`-t:Rebuild` 1.1 s。
- 复现性：`-t:Rebuild` 前后 SHA256 完全相同（`D38461F0…C5C5FCC2`）→ 同机同输入可复现。**注意**：跨机器/跨 checkout 路径不保证同哈希（未设置 `DeterministicSourcePaths`，MVID 受源文件路径影响）。
- 产物目录最终内容：`HungerAndHavoc.dll`、`HungerAndHavoc.Api.dll`、两个 `.pdb`（`.pdb` 被模块 `.gitignore` 覆盖，不进 git）。
- 未构建 `Guard/Source/HungerAndHavocGuard.csproj`（任务只要求主体与 Api；且它会改写 `Guard/Assemblies` 中另一批跟踪文件）。
- 未部署：从未向 `D:\SteamLibrary\…\RimWorld\Mods`、`workshop` 或任何玩家目录复制过产物。

## 四、新旧 DLL 对照表

### 4.1 文件级
| 项 | 旧（构建前，已备份） | 新（本次构建） |
|---|---|---|
| `HungerAndHavoc.dll` 大小 | 848,384 B | **865,280 B** |
| SHA256 | `B987BF67D8EB42B8AD7FDE6A97871BB336AC9888A4B84BC2F0C2E24D0AD35BC6` | **`D38461F02E50EDCBA90DECB733C73A7641B5B01BF416D96ACD01562F45C5FCC2`** |
| MVID | `a51e5cda-c5dc-4bd7-9d9a-ef2b623c186e` | `1e20c74f-12b6-41c5-b413-aaa9dcc4a481` |
| `AssemblyInformationalVersion` | `1.1.0` | **`1.1.4`**（= `About/About.xml` 的 `modVersion`） |
| 编译时间 | PE `TimeDateStamp` 为确定性哈希（解出 2069-06-16，无意义）；文件 mtime 2026-10-24 08:48:33 是 git checkout 写入时间，**不能**当编译时间 | 文件 mtime 2026-10-24 16:56:12（本地）；PE 时间戳同样是哈希，不可用 |
| 引用 `Assembly-CSharp` | 1.6.9438.37837（**旧游戏构建**） | 1.6.9676.17735（本机现有游戏） |
| 引用 `Oxygendsl.Core` / `.Mod` | 0.0.1.0 / 0.0.1.0 | 0.0.1.0 / 0.0.1.0（对齐） |
| 引用 `IrisMenus` | 1.1.1.0 | 1.1.2.0（仓库 HEAD 源码） |
| `HungerAndHavoc.Api.dll` 大小 / SHA256 | 14,848 B / `0167D1B1F3D6160FBEB4B44C76A9140CFD4A949A943428A794B7E8362DEC9691` | 14,848 B / `C8514173A19CC47993C4144835BBDFC0E2CFEFB5B6A14D321FCC3B86BEF5014A` |

### 4.2 关键成员（反射，`Assembly.Load(bytes)` + `GetTypes()` 容错）
`GetTypes()` 在本机必然抛 `ReflectionTypeLoadException`（缺 `Assembly-CSharp` 解析路径），因此用 `ex.Types` 取已加载类型（旧 388 个、新 398 个），并对两个 DLL 用**同一套方法**取证，保证可比。

| 成员 | 旧 DLL | 新 DLL |
|---|---|---|
| `RHAH_ArmedDemand.Owner` | **无** | **有**（static） |
| `RHAH_ArmedDemand.Refresh` | **无** | **有** |
| `RHAH_ArmedDemand.Finish` | **无** | **有** |
| `RHAH_ArmedDemand.Tick` | **无** | **有** |
| `RHAH_ArmedDemand.NotifyPlayerHarm` | **无**；#Strings 里连字符串都不存在（count=0） | **有**（`static bool NotifyPlayerHarm(Int32)`，字符串 count=1） |
| `RHAH_ArmedDemand` 方法总数 | 12（CanPay, Find, get_Enabled, Holds, Initialize, IsRequest, IsSiege, PlanPayment, Refused, Resolve, StartsAssault, TryPay） | **20**（另加 Finish, FoodRequest, NoteFood, NotifyPlayerHarm, Owner, Refresh, Remaining, Tick） |
| `RHAH_ChoiceRecord.ArmedFoodNutritionDelivered`（字段名 #Strings，UTF-8） | **无**（count=0） | **有**（count=1） |
| Scribe 键 `armedFoodNutritionDelivered`（#US，UTF-16LE） | **无**（count=0） | **有**（count=1） |
| `RHAH_ChoiceRecord` 类型名 | 有（count=1） | 有（count=1） |
| `RHAH_IRISMENUS` / `IrisMenus` 痕迹 | 引用 `IrisMenus 1.1.1.0`（compat 已编入） | 引用 `IrisMenus 1.1.2.0`（compat 已编入） |

证据文件：`runs\build-backup-20261024\old-dll-reflection.txt`、`new-dll-reflection.txt`、`method-compare.txt`。

> 局限（如实标注）：`RHAH_ChoiceRecord` 的实现 `IExposable` 依赖 `Assembly-CSharp`，在本机 PS 5.1 里无法完成类型解析（尝试预加载 Managed 目录 106 个 DLL 仍失败，报告 `Assembly-CSharp 1.6.9438.37837` 找不到；用 `AppDomain.AssemblyResolve` 处理器反而触发 StackOverflow 进程崩溃，已放弃），所以该类型只能给出**元数据字符串级**证据（字段名 + Scribe 键，旧 0/新 1），没有拿到字段表本身。`RHAH_ArmedDemand` 是纯 static 类、不依赖游戏类型，其方法表是完整反射结果。

## 五、失败记录

构建本身**没有失败**，也没有需要重试的错误。过程中的三个真实缺口与非致命问题：

1. **（已绕过）仓库无 ODF 1.6 构建目标**。`oxygen_dsl_framework/tmp/` 不存在，官方 1.6 流水线会在 `odf-outputs` 检查处报 `ODF build did not produce Oxygendsl.Core.dll`；而 `Oxygendsl.Mod.csproj` 焊死 `net10.0`/`UnityStubs`，无法用 `-p:` 直接改成 1.6。绕过方式：`runs\tmp\odf16` 临时副本重定向为 net48 编译，产物不进仓库。
2. **（已绕过）缺 .NET Framework 参考程序集**。若不处理，`net48`/`net472` 构建会报 MSB3644「找不到 .NETFramework,Version=v4.8 的参考程序集」。用两个 NuGet 参考程序集包 + `-p:TargetFrameworkRootPath=` 解决，未安装任何全局软件、未改 csproj。
3. **（说明）`dotnet restore` 必须带 `-p:RimWorldVersion=1.6`**，否则 `TargetFramework` 为空。ps1/py 脚本按约束未落地，探测全部用单行 PowerShell；`AssemblyResolve` 方案曾把进程打崩（`exit code 3221225725` StackOverflow），已改用 `ex.Types` 容错路径，未再重试。

## 六、回滚方法

```powershell
# 1) 还原两个 DLL（备份已核对 SHA256）
$b='E:\2026.7以后的新工程\DSH\RimWorld_auto_test\runs\build-backup-20261024'
$t='E:\2026.7以后的新工程\DSH\RimWorld_auto_test\RimworldMods\ratkin_hunger_and_havoc\1.6\Assemblies'
Copy-Item "$b\HungerAndHavoc.dll","$b\HungerAndHavoc.Api.dll" -Destination $t -Force
# 2) 删掉本次构建新增、原本不存在的 PDB
Remove-Item "$t\HungerAndHavoc.pdb","$t\HungerAndHavoc.Api.pdb" -ErrorAction SilentlyContinue
# 3) 校验
Get-FileHash "$t\HungerAndHavoc.dll"   # 应为 B987BF67D8EB42B8AD7FDE6A97871BB336AC9888A4B84BC2F0C2E24D0AD35BC6
Get-FileHash "$t\HungerAndHavoc.Api.dll" # 应为 0167D1B1F3D6160FBEB4B44C76A9140CFD4A949A943428A794B7E8362DEC9691
```
- 备份目录：`runs\build-backup-20261024\`（`HungerAndHavoc.dll`、`HungerAndHavoc.Api.dll`、两份反射证据、`method-compare.txt`）。
- git 侧：`git status` 只显示这两个**被跟踪**的 DLL 为 `M`，没有任何源码/csproj 改动（`Source\obj`、`1.6\Assemblies\*.pdb` 均被 `.gitignore` 覆盖）。未 commit、未 push。
- 临时目录（可随时删）：`runs\tmp\nupkg`（nupkg）、`runs\tmp\refasm`（参考程序集）、`runs\tmp\odf16`（ODF 1.6 副本+产物）、`runs\tmp\iris16`（IrisMenus 1.6）、`runs\tmp\*.log`（构建日志）。

## 七、未验证项

1. **未运行官方入口**（`scripts/rhah_pipeline.py build` / `deploy.py --build-only`）。我执行的是它内部逐字相同的 `dotnet build …` 加 3 个补丁属性；官方入口在本机会先因缺 `tmp/build` ODF 产物、再因缺 net48 参考程序集失败（前者需仓库内补 ODF 1.6 产物，后者需 `TargetFrameworkRootPath`）。`deploy.py` 还要 `pwsh`，本机没有。
2. **未构建 Guard**（`Guard/Assemblies/HungerAndHavocGuard.dll`）——不在本次目标内。
3. **未做运行时验证**：没有启动游戏、没有加载 mod、没有验证 `NotifyPlayerHarm`/`Owner` 等新逻辑的实际行为，也没有验证新 DLL 在 1.6.4871 游戏里能否正常附着 Harmony 补丁。
4. **ODF 1.6 产物非官方来源**：`Oxygendsl.Core/Mod` 由 HEAD 源码以 net48 在临时副本里编出（Core 仍是 netstandard2.0）。仓库原本依赖的「旧版 tmp/build 产物」不在本机，无法对比两者 API 是否一致；若上游 HEAD ODF 的 1.6 行为与出厂 1.6 产物不同，本 DLL 会继承该差异。
5. **IrisMenus 版本漂移**：本次对齐到仓库 HEAD 的 `1.1.2`，旧 DLL 引用 `1.1.1`；IrisMenus 1.6 运行时是否与本 DLL 的 compat 代码完全匹配未验证（本机未安装该 mod）。
6. **跨机复现性未验证**：只验证了同机同输入两次构建哈希一致。
7. 游戏版本与任务前提差一版（实测 `Version.txt` = `1.6.4871 rev590`，任务写 rev591）；另旧 DLL 引用的 `Assembly-CSharp 1.6.9438.37837` 与现有游戏 `1.6.9676.17735` 不同，说明旧产物也是在更早的游戏构建下编出的。
