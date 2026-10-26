# RHAH 1.7 程序集重建报告（20261024）

从源码重建 `ratkin_hunger_and_havoc`（RHAH）**1.7 / net10.0 / Waylith** 程序集并验证产物。全部操作在本机 Windows 完成；未启动游戏、未向游戏目录/工坊部署、未改仓库源码或 csproj、未提交 git。标注「实测」的均为本机命令输出；「推测」为未验证判断。

## 结论

1. **成功**：主目标 1.7 构建通过，原文 `已成功生成。` + `0 个警告` + `0 个错误`（`runs\tmp\rhah-build-1.7.log`、`runs\tmp\rhah-rebuild-1.7.log`），产物写回仓库 `RimworldMods\ratkin_hunger_and_havoc\1.7\Assemblies\`（`HungerAndHavoc.dll` 868,352 B / `HungerAndHavoc.Api.dll` 14,336 B）。
2. **产物含全部关键成员**（实测反射，587 个类型 0 加载错误）：`RHAH_ArmedDemand.Owner/Refresh/Finish/Tick`、`RHAH_DoorPatch.Postfix`、`JobGiver_RHAH_Leave` 均 PRESENT；与旧 DLL 的成员表逐一相同（60 条成员零差异）。
3. **必须先编 Oxygendsl**：仓库 `oxygen_dsl_framework\tmp\build\1.7` 不存在，`Oxygendsl.Mod.csproj` 本身已是 1.7/net10.0 目标，构建后把 `OxygenDslBuildDir` 指向 `runs\tmp\odf17\out` 即可，**仓库内未生成 `tmp/`**。
4. **可回滚**：构建前已备份到 `runs\build-backup-1.7-20261024\`（SHA256 与备份一致，见第六节回滚命令）。
5. **无需额外下载**：`dotnet restore` 全部命中本机 NuGet 缓存（没有出现任何 NU 下载/警告）；未联网访问除 NuGet 之外的资源。

## 一、1.7 下的 MSBuild 属性映射

布局事实（实测）：`D:\SteamLibrary\steamapps\common\RimWorld` 根目录有 `RimWorld.exe`、`Assembly-CSharp.dll`、`UnityStubs.dll`、`RimWorld.dll`、`Waylith*.dll`、`Brutal.*.dll`；`RimWorldWin64_Data\Managed` **不存在**（`Test-Path` = False）。`Version.txt` = `1.7.4985 rev1269`；`Assembly-CSharp` = `1.7.9790.38077`；`UnityStubs` = `1.0.0.0`；`RimWorld.dll` = `1.7.4985.1269`。

| MSBuild 属性 | 1.7 应指向 | 依据 |
|---|---|---|
| `RimWorldVersion` | `1.7`（必须显式传） | `ratkin_hunger_and_havoc/Directory.Build.props:3-4` 虽兜底 1.7，但 `deploy.py:331,357` 与 `rhah_pipeline.py:384` 都按显式版本走；它决定 `TargetFramework=net10.0`（`HungerAndHavoc.csproj:4`）与 `RIMWORLD_1_7` 常量（props:13） |
| `RimWorldDir` | `D:\SteamLibrary\steamapps\common\RimWorld` | 两个 `Directory.Build.props` 的默认值是 `E:/Apps/Steam/...`（外置硬编码，本机不存在），必须覆盖（`ratkin…/props:6`、`oxygen…/props:9`） |
| `RimWorldManagedDir` | **就是安装根**：`D:\SteamLibrary\steamapps\common\RimWorld` | `deploy.py:365`：`managed = game if version == "1.7"`；`rhah_pipeline.py:386` 同款；props:8 也推导成 `$(RimWorldDir)`。1.7 无 `Managed` 子目录 |
| `Assembly-CSharp.dll` 引用 | `$(RimWorldManagedDir)/Assembly-CSharp.dll` → 安装根 | `HungerAndHavoc.csproj:57-60`；props:19 的存在性检查 |
| `UnityStubs.dll` 引用 | `$(RimWorldManagedDir)/UnityStubs.dll` → 安装根 | `HungerAndHavoc.csproj:62-67`（仅 1.7 分支）；props:20 强制要求 |
| `OxygenDslDir` | `<monorepo>\oxygen_dsl_framework` | props:10 / csproj:18 兜底；本次显式传 |
| `OxygenDslBuildDir` | **仓库默认值不可用**（`$(OxygenDslDir)/tmp/build/1.7` 不存在）→ 覆盖为 `runs\tmp\odf17\out` | props:11 直接推导、csproj:49-56 用它做 `HintPath`；`rhah_pipeline.py:424-438` 会在 build 后校验这两个 DLL 存在 |
| `HarmonyPath` | `D:\SteamLibrary\steamapps\workshop\content\294100\2009463077\1.7\Assemblies\0Harmony.dll` | `oxygen…/props:12` 与 `rhah_pipeline.py:387` 默认值；实测该文件存在。**主 csproj 不用它**：`HungerAndHavoc.csproj:29-32` 走 NuGet `Lib.Harmony 2.4.1`（csc 行里是 `~\.nuget\packages\lib.harmony\2.4.1\lib\net9.0\0Harmony.dll`），`HarmonyPath` 只喂 `Oxygendsl.Mod.csproj:13` |
| `IrisMenusDll` | 默认 `$(RimWorldDir)/Mods/IrisMenus/1.7/Assemblies/IrisMenus.dll` **本机不存在** → 覆盖为 `runs\tmp\iris17\IrisMenus.dll` | `HungerAndHavoc.csproj:87-100`：文件存在才编入 `Pawn/Compat/RHAH_IrisMenusCompat.cs`/`RHAH_IrisMenusWidgets.cs` 并定义 `RHAH_IRISMENUS`。旧 1.7 DLL 的 AssemblyRef 含 `IrisMenus 1.1.2.0`，为保持功能对齐必须提供（否则重编会静默剔除 compat） |
| `TargetFramework` | `net10.0`（由 `RimWorldVersion=1.7` 决定，不要手传） | `HungerAndHavoc.csproj:3-4`；`TargetFramework` 为空时 restore 直接失败 |
| `OutputPath` | `../1.7/Assemblies/`（csproj:12，未覆盖） | 产物按设计写回仓库 `1.7\Assemblies` |
| `GenerateDependencyFile` / `CopyLocalLockFileAssemblies` | `false` / `false` | props:14-15，避免把游戏/ODF DLL 拷进产物目录 |

## 二、前置 Oxygendsl（1.7）构建（实测）

`oxygen_dsl_framework\tmp\` 在本机**不存在**；`Source/Mod/Oxygendsl.Mod.csproj` 已是 `net10.0 + OutputPath=../../tmp/build/1.7/ + 引用 UnityStubs`，所以 1.7 **只需要原样编译**，不用像 1.6 那样改副本。为了不往仓库写 `tmp/`，用 `-p:OutputPath` 重定向到 `runs\tmp\odf17\out\`：

```powershell
cd RimworldMods
dotnet build oxygen_dsl_framework\Source\Mod\Oxygendsl.Mod.csproj -c Release --nologo -v m `
  -p:RimWorldVersion=1.7 `
  -p:RimWorldDir='D:\SteamLibrary\steamapps\common\RimWorld' `
  -p:RimWorldManagedDir='D:\SteamLibrary\steamapps\common\RimWorld' `
  -p:OxygenDslDir='<workspace>\RimworldMods\oxygen_dsl_framework' `
  -p:HarmonyPath='D:\SteamLibrary\steamapps\workshop\content\294100\2009463077\1.7\Assemblies\0Harmony.dll' `
  -p:OutputPath='<workspace>\runs\tmp\odf17\out\'
```

输出（`runs\tmp\odf17-build.log`，原文节选）：

```
  Oxygendsl.Core -> E:\2026.7以后的新工程\DSH\RimWorld_auto_test\runs\tmp\odf17\out\Oxygendsl.Core.dll
  Oxygendsl.Mod  -> E:\2026.7以后的新工程\DSH\RimWorld_auto_test\runs\tmp\odf17\out\Oxygendsl.Mod.dll

已成功生成。
    0 个警告
    0 个错误
已用时间 00:00:01.99
```

| 产物 | 大小 | SHA256 |
|---|---|---|
| `Oxygendsl.Core.dll` | 262,656 B | `E6AB2E5E0691D4748CAEB83D7FE1218813C67BF3A94F5513F6353707BAED786D` |
| `Oxygendsl.Mod.dll` | 169,984 B | `9D0AB988E0F4105C39511CCF263B04431A6B86998EDF3DF8ADD559FFC4E36B4A` |

`Test-Path RimworldMods\oxygen_dsl_framework\tmp` = **False**（仓库未被写入构建产物）。

### 2.1 附：IrisMenus 1.7（旧 DLL 的既有依赖，实测）

本机 `D:\…\RimWorld\Mods\IrisMenus` 不存在，仓库 `iris_menus` 也没有 1.7 产物（只有 1.6 构建残留的 `Source\obj\Release\IrisMenus.dll`）。为保持与旧产物一致的 `IrisMenus 1.1.2.0` 引用，从 HEAD 源码编到临时目录：

```powershell
dotnet build iris_menus\Source\IrisMenus.csproj -c Release --nologo -v m `
  -p:RimWorldVersion=1.7 -p:RimWorldManagedDir='D:\SteamLibrary\steamapps\common\RimWorld' `
  -p:OutputPath='<workspace>\runs\tmp\iris17\'
```

输出：`IrisMenus -> …\runs\tmp\iris17\IrisMenus.dll`，`0 个警告 0 个错误`，1.08 s；产物 69,632 B，`IrisMenus 1.1.2.0`，引用 `System.Runtime 10.0.0.0` / `Assembly-CSharp 1.7.9790.38077` / `Waylith 1.0.0.0`（即确实是 net10 的 1.7 构建）。

## 三、主目标构建（实测）

```powershell
cd RimworldMods
dotnet build ratkin_hunger_and_havoc\Source\HungerAndHavoc.csproj -c Release --nologo -v m `
  -p:RimWorldVersion=1.7 `
  -p:RimWorldDir='D:\SteamLibrary\steamapps\common\RimWorld' `
  -p:RimWorldManagedDir='D:\SteamLibrary\steamapps\common\RimWorld' `
  -p:OxygenDslDir='<workspace>\RimworldMods\oxygen_dsl_framework' `
  -p:OxygenDslBuildDir='<workspace>\runs\tmp\odf17\out' `
  -p:HarmonyPath='D:\SteamLibrary\steamapps\workshop\content\294100\2009463077\1.7\Assemblies\0Harmony.dll' `
  -p:IrisMenusDll='<workspace>\runs\tmp\iris17\IrisMenus.dll'
```

输出（`runs\tmp\rhah-build-1.7.log`，原文）：

```
  HungerAndHavoc.Api -> E:\2026.7以后的新工程\DSH\RimWorld_auto_test\RimworldMods\ratkin_hunger_and_havoc\1.7\Assemblies\HungerAndHavoc.Api.dll
  HungerAndHavoc -> E:\2026.7以后的新工程\DSH\RimWorld_auto_test\RimworldMods\ratkin_hunger_and_havoc\1.7\Assemblies\HungerAndHavoc.dll

已成功生成。
    0 个警告
    0 个错误
已用时间 00:00:01.99
```

补充证据（`-t:Rebuild -v n`，`runs\tmp\rhah-verify-n.log`）：确实执行了 `csc`，关键开关是
`/define:RIMWORLD_1_7;TRACE;RHAH_IRISMENUS;RELEASE;NET10_0;…`、`/reference:iris="…\runs\tmp\iris17\IrisMenus.dll"`、`/reference:D:\SteamLibrary\steamapps\common\RimWorld\Assembly-CSharp.dll`、`/reference:…\RimWorld\UnityStubs.dll`、`/reference:"…\runs\tmp\odf17\out\Oxygendsl.Core.dll"`、`/reference:"…\runs\tmp\odf17\out\Oxygendsl.Mod.dll"`，并内嵌两个 `.oxy` 资源；末尾同样 `0 个警告 / 0 个错误`。

复现性（实测）：连续三次构建（普通 build → `-t:Rebuild` → `-t:Rebuild -v n`）产物 SHA256 恒为 `60F66585…B94DAE`，同机同输入可复现。

## 四、新旧 DLL 对照

### 4.1 文件级

| 项 | 旧（构建前，已备份） | 新（本次构建） |
|---|---|---|
| `HungerAndHavoc.dll` 大小 | 868,352 B | **868,352 B** |
| SHA256 | `AB61FAAF9F6752CF7FFC98756D5EECCD43FA233EAD3D387CC1C6DFEF4DC85FBE` | **`60F6658583FE172C26286D03A83AE7F8B2C6631342857E83C978363302B94DAE`** |
| 文件 mtime | 2026-10-24 08:48:33（git checkout 时间，不是编译时间） | 2026-10-24 18:08:15 |
| 字节级差异 | — | 975 / 868,352 字节不同（约 0.11%，与 MVID/路径哈希一致，其余内容同构） |
| `AssemblyInformationalVersion` | `1.1.4` | `1.1.4` |
| `HungerAndHavoc.Api.dll` 大小 / SHA256 | 14,336 B / `37DB1ECBC2FEAF556D8E6661F7B47430EC8B4C4A3D0EA1417EFBFF418B892E6F` | 14,336 B / `093F60C52780C1A67C10FB9FCB4A3DBBED4143BBC9CEDA34EBEB800268840A7C` |
| 产物目录内容 | 两个 DLL | 两个 DLL + `HungerAndHavoc.pdb`(292,252 B) + `HungerAndHavoc.Api.pdb`(14,576 B)（`1.7/**/*.pdb` 已被 `.gitignore:13` 忽略，不进 git） |

参照的 AssemblyRef（新旧完全一致，实测）：`0Harmony 2.4.1.0`、`Assembly-CSharp 1.7.9790.38077`（= 本机现行游戏）、`HungerAndHavoc.Api 1.0.0.0`、`IrisMenus 1.1.2.0`、`Oxygendsl.Core/Mod 0.0.1.0`、`UnityStubs 1.0.0.0`、`System.Runtime/Collections/Linq/Threading/Xml.* 10.0.0.0`。

### 4.2 关键成员（反射实测）

方法：因本机只有 PowerShell 5.1（.NET Framework 4.x，无法加载 net10 程序集，`Assembly.LoadFrom` 报 `HRESULT: 0x80131515`），改用 `dotnet run reflect.cs`（.NET 10 file-based app，`runs\tmp\probe17\reflect.cs`，用 `AssemblyLoadContext` + `Resolving` 从游戏根、`1.7\Assemblies`、`odf17\out`、`iris17` 解析依赖）对两个 DLL 用**同一套代码**取证。原始输出：`runs\build-backup-1.7-20261024\old-dll-reflection.txt` / `new-dll-reflection.txt`。

| 成员 | 旧 DLL | 新 DLL |
|---|---|---|
| 可加载类型数 | 570 + 17 个加载失败（合计 587；失败原因是 `IrisMenus 1.1.2.0` 当时未就位） | **587，0 加载错误** |
| `HungerAndHavoc.Pawn.RHAH_ArmedDemand` | 存在（internal） | 存在（internal） |
| `RHAH_ArmedDemand.Owner` | PRESENT（`static Lord Owner(RHAH_ChoiceRecord)`） | **PRESENT** |
| `RHAH_ArmedDemand.Refresh` | PRESENT（`static Boolean Refresh(RHAH_ChoiceRecord)`） | **PRESENT** |
| `RHAH_ArmedDemand.Finish` | PRESENT（`static Void Finish(RHAH_ChoiceRecord, RHAH_ChoiceAction)`） | **PRESENT** |
| `RHAH_ArmedDemand.Tick` | PRESENT（`static Void Tick(RHAH_ChoiceRecord, Int32)`） | **PRESENT** |
| `RHAH_DoorPatch.Postfix` | PRESENT | **PRESENT** |
| `HungerAndHavoc.Pawn.JobGiver_RHAH_Leave` | 存在（public，含 `.ctor`） | 存在（public，含 `.ctor`） |
| `HungerAndHavoc.Incidents.RHAH_ChoiceRecord` | 存在（public） | 存在（public） |
| 上述四类型的成员总数 | 60 | 60（`Compare-Object` 零差异） |

## 五、失败记录

构建**未失败**，无需重试。过程中的真实缺口与处理：

1. **（已绕过）`OxygenDslBuildDir` 默认指向不存在的 `oxygen_dsl_framework/tmp/build/1.7`。** 若照抄 1.6 命令而不覆盖该属性，`HungerAndHavoc.csproj:49-56` 的 `HintPath` 会指向不存在的文件（`rhah_pipeline.py` 的 `odf-outputs` 阶段也会报 `ODF build did not produce …`）。处理：先编 ODF 到 `runs\tmp\odf17\out` 再覆盖属性。
2. **（已绕过）`IrisMenusDll` 默认路径在本机不存在。** 若不处理，csproj:89-92 会静默剔除两个 compat 源文件、丢掉 `RHAH_IRISMENUS` 常量，产物体积与功能都会与旧 DLL 不一致（旧 DLL 引用 `IrisMenus 1.1.2.0`）。处理：从 HEAD 编到 `runs\tmp\iris17` 并覆盖。
3. **（说明）游戏路径默认值错误**（`E:/Apps/Steam/...`）——两个 `Directory.Build.props` 都是硬编码，必须每次传 `RimWorldDir`；1.7 还要额外保证 `RimWorldManagedDir` 是安装根。
4. **（说明）本机无 `pwsh`、PS 5.1 无法反射 net10 程序集**，因此验证改用 `dotnet run` 的 file-based app（`runs\tmp\probe17\reflect.cs`，非 `.ps1/.py`，留在 `runs\tmp` 临时区）。1.6 报告里 `Assembly.Load(bytes)+ex.Types` 的 PS 路线在 1.7 不可用。

## 六、回滚命令

备份目录 `runs\build-backup-1.7-20261024\` 内两个 DLL 的 SHA256 已与构建前逐一核对（见 4.1）。回滚：

```powershell
$b='E:\2026.7以后的新工程\DSH\RimWorld_auto_test\runs\build-backup-1.7-20261024'
$t='E:\2026.7以后的新工程\DSH\RimWorld_auto_test\RimworldMods\ratkin_hunger_and_havoc\1.7\Assemblies'
Copy-Item "$b\HungerAndHavoc.dll","$b\HungerAndHavoc.Api.dll" -Destination $t -Force
# 本次构建新增、原本不存在的 PDB（已被 .gitignore 忽略）
Remove-Item "$t\HungerAndHavoc.pdb","$t\HungerAndHavoc.Api.pdb" -ErrorAction SilentlyContinue
# 校验：应为 AB61FAAF…4DC85FBE / 37DB1ECB…B892E6F
Get-FileHash "$t\HungerAndHavoc.dll","$t\HungerAndHavoc.Api.dll" -Algorithm SHA256
```

git 侧（实测 `git -C RimworldMods status --porcelain`）：只有 `ratkin_hunger_and_havoc/1.6/Assemblies/*.dll`（1.6 重建的既有改动，本次未触碰）与 `1.7/Assemblies/*.dll`（本次）为 `M`，**无源码/csproj 改动**；`1.7/**/*.pdb` 被忽略。

可随时删除的临时目录：`runs\tmp\odf17`（ODF 1.7 产物）、`runs\tmp\iris17`（IrisMenus 1.7 产物）、`runs\tmp\probe17`（反射探针）、`runs\tmp\*.log`、`runs\tmp\probe-test`（`dotnet run file.cs` 能力试探）。

## 七、未验证

1. **未跑官方入口**（`scripts\deploy.sh` / `deploy.py --build-only` / `rhah_pipeline.py build`）。我执行的是 `rhah_pipeline.py:421-431` 逐字对应的 `dotnet build` 加 3 个覆盖属性（`OxygenDslBuildDir`、`IrisMenusDll` 和显式 `RimWorldManagedDir`）；`deploy.py` 的 powershell 分支还需 `pwsh`，本机没有。`rhah_pipeline.py build` **本机应可直接跑通 1.7**（它会先编 ODF 到仓库 `tmp/build/1.7`，再编主目标）——未实测，因为它会往仓库写 `tmp/`。
2. **未做运行时验证**：没启动游戏、没加载 mod，未验证新 DLL 在 1.7.4985 rev1269 下能否被 Harmony 正常补丁、`RHAH_ArmedDemand`/`JobGiver_RHAH_Leave` 的实际行为、以及 ODF 运行时是否与 DLL 版本匹配。
3. **未构建 Guard**（`Guard/Source/HungerAndHavocGuard.csproj`）与 `Source/Tests`（`HungerAndHavoc.Tests`）——不在本次目标内。
4. **IrisMenus 为本地自编**（HEAD 源码，`1.1.2.0`），旧 DLL 引用的也是 `1.1.2.0`，但两者是否逐字节同源未知（旧产物编出的 `IrisMenus.dll` 不在本机）；若 HEAD 与出厂版本的成员不同，本 DLL 的 compat 代码会继承该差异。
5. **未比对 IL**：只做了成员表（60 条，零差异）与大小（完全相同）层面的比对；975 字节差异具体落在 MVID/PDB 路径/timestamp 未逐段确认（推测，未验证）。
6. **跨机复现性未验证**：只验证了同机、同源路径下三次构建哈希一致（`deterministic+` 已开，但未设 `DeterministicSourcePaths`，换路径哈希会变）。
