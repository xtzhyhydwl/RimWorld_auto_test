# HANDOFF：用 steamcmd 并行安装独立 RimWorld 1.6 环境（2026-10-25）

> **✅ 已完成（2026-10-25 12:4x）**：四条验收全绿，但**没用 steamcmd**（登录态搬不过去），实际走的是「手写 acf 让 Steam 装 public 分支」。
> 实测结果、可重复步骤、已知坑见 [RimWorld-1.6并行安装-实测记录-20261025.md](RimWorld-1.6并行安装-实测记录-20261025.md)。本文保留作当时的设计记录（§3/§4.4/§10 里若干"未验证"现已有答案）。

> 自包含交接，面向看不到对话的新会话。目标：**不动现有 1.7**，另装一套能编译、能真跑的 1.6。来源标注：**实测**＝2026-10-25 本机跑命令看到的结果；**记录值**＝仓库 docs 旧记录（未复验）；**未验证**＝推测/待确认。本文不含任何账号、密码、Steam Guard 码。

## 1. 背景（为什么要再装一套）
现有游戏是 **RimWorld 1.7.4985 rev1269（Steam `unstable` 分支，实测 `Version.txt`）**，装在 `D:\SteamLibrary\steamapps\common\RimWorld`。1.7 把程序集平铺到安装根并**删掉了** `RimWorldWin64_Data\Managed`（实测：该目录不存在；根目录 `Assembly-CSharp.dll` 的 FileVersion 为 `1.7.9790.38077`），于是 1.6 目标（`net48`，需要 1.6 版 `Assembly-CSharp` + `UnityEngine.*Module`）在本机**编不了**（全盘只剩 1.7 的程序集）。结论：必须并存一套 1.6——既拿它的参照程序集编 1.6，也用它真跑验证。

## 2. 目标布局：两套独立目录，互不覆盖
| 项 | 1.7（现有，**只读不改**） | 1.6（本次新建） |
|---|---|---|
| 路径 | `D:\SteamLibrary\steamapps\common\RimWorld` | `D:\RimWorld-1.6`（实测：目前不存在，干净） |
| 管理方 | Steam 客户端 | **steamcmd 独立安装目录；Steam 客户端不认它** |
| 入口 exe | `RimWorld.exe`（实测） | `RimWorldWin64.exe`（记录值：1.6 时代入口就是它；本次仍需 §4.1 核） |
| 程序集 | 根目录 `Assembly-CSharp.dll`（实测） | `RimWorldWin64_Data\Managed\Assembly-CSharp.dll`（预期） |
| 启动方式 | Steam / 现有 `src\cli.js` | **手动命令行直启**（§5） |

## 3. 下载（命令级；凭据只由用户本人现场输入）
### 3.1 登录（会要密码 + Steam Guard）
```powershell
D:\steamcmd\steamcmd.exe +login <Steam账号>
```
- 首次要密码 + Steam Guard 码（手机令牌/邮箱），成功后 steamcmd 在自身目录缓存 `ssfn*`（实测：`D:\steamcmd` 下现无 `ssfn*` ⇒ 从未登录过），之后免码。**匿名下载不可行**：RimWorld 是付费游戏（appid `294100`）。非交互式 `+login <账号> <密码> <Guard码>` 可能可用但**未验证**；Guard 码会旋转，失败就退回交互式。

### 3.2 安装到独立目录（拉当前**稳定**分支）
```powershell
D:\steamcmd\steamcmd.exe +force_install_dir D:\RimWorld-1.6 +app_update 294100 +quit
```
- **不带 `-beta`** ⇒ 装 Steam 当前的 default/稳定分支。前提「稳定分支现在仍是 1.6」**必须装完用 `Version.txt` 核实（§4.1）**，不得当成事实。
- `+force_install_dir` 必须在 `+app_update` 之前。实测 `D:\steamcmd\steamapps` 目前只有 `workshop\` 与 `libraryfolders.vdf`，无 `common\`、无 `appmanifest_294100.acf`（该目录另有一个 `dl_294100.txt` 9983 B，说明此前对 294100 有过请求但从未落盘安装）⇒ 这将是首次安装；下载量 ~2–3 GB（前提值，未验证）。

### 3.3 若稳定分支已不是 1.6（备用路径）
1. 查分支名：`D:\steamcmd\steamcmd.exe +login <账号> +app_info_update 1 +app_info_print 294100 +quit`，在 `branches` 段找 1.6 分支，然后给 §3.2 加 `-beta <分支名>`。
2. 或 DepotDownloader 按旧 buildid 拉：buildid **`23969874`**（**记录值，未验证可下载**）；depot 号/manifest 号本机未记录，需从 `app_info_print` 或 SteamDB 取。
3. 都拿不到就停手报用户。**不要**为了凑 1.6 去切换/降级 `D:\SteamLibrary\...\RimWorld`。

## 4. 装完核实 + 与 1.7 的隔离设计
### 4.1 装完立刻核实（非期望值就停）
```powershell
Get-Content 'D:\RimWorld-1.6\Version.txt'                                                            # 期望 1.6.*（记录值 1.6.4871 rev590/rev591）
(Get-Item 'D:\RimWorld-1.6\RimWorldWin64_Data\Managed\Assembly-CSharp.dll').VersionInfo.FileVersion   # 期望 1.6.*（记录值 1.6.9676.17735；本机 1.7 = 1.7.9790.38077）
Test-Path 'D:\RimWorld-1.6\RimWorldWin64.exe','D:\RimWorld-1.6\RimWorldWin64_Data\Managed'            # 期望 True,True；1.6 不应有 UnityStubs.dll
```
判读：`Version.txt` 不是 1.6 ⇒ 回 §3.3；只有根目录 `RimWorld.exe` 而无 `Managed\` ⇒ 装成 1.7 布局，**整目录重装**，别将就。体积量级参照本机 1.7：4629 文件 / 1.17 GB（实测），1.6 应同量级（未验证）。
### 4.2 存档/配置隔离：`-savedatafolder` 把 `Saves` 与 `Config` 一起搬走
该参数重定向 `Saves`、`Config`（含 `ModsConfig.xml`、`Prefs.xml`）、`Screenshots`。1.6 的 `Assembly-CSharp` 内有 `savedatafolder` 字面量，且旧 1.6 实例确实把 `Config\Saves` 写在 `profiles\rhah\` 下（记录值：`docs\RimWorld-如何测试本地重建mod-20261024.md:73`、`docs\RimWorld-隔离测试环境准备-20261024.md:233`）。1.6 固定用 `E:\2026.7以后的新工程\DSH\RimWorld_auto_test\runs\rimworld16-data\`（实测：尚不存在，与 1.7 的 `profiles\*` 零重合）——这是「**同机两套数据**」而非共享：1.7 继续用 `profiles\<profile>` 或默认 `%LocalLow%`，两边 `ModsConfig.xml` 是**两个不同文件**。
必须避免的默认行为：1.6 不传该参数时会用默认 save data 目录，也就是 1.7 正在用的 `%LocalLow%\Ludeon Studios\RimWorld by Ludeon Studios\`（实测其中 `Config\ModsConfig.xml`、`Saves\` 都在）⇒ 两版本互踩配置。**1.6 每次启动都必须带这个参数。**
### 4.3 mod 目录：1.6 的 `Mods\` 是空的，工坊那份不归它管
RimWorld 只从「游戏目录 `Mods`/`Data`」与「工坊目录」读 mod，**不扫 `-savedatafolder` 下的 `Mods\`**（实测记录：`docs\mod开发基础.md:104,283`）；1.6 没有「指定 mod 目录」的启动参数（记录值：`docs\RimWorld-如何测试本地重建mod-20261024.md:17`）。工坊内容在 `D:\SteamLibrary\steamapps\workshop\content\294100\`（实测存在，Steam 客户端管的那份）；独立安装**会不会自动认它未验证**，按「不认」处理。
**方案① junction（只读共享，省空间）**：`New-Item -ItemType Junction -Path 'D:\RimWorld-1.6\Mods\2009463077' -Target 'D:\SteamLibrary\steamapps\workshop\content\294100\2009463077'`。代价：Steam 更新工坊会连带影响 1.6；mod 的 `LoadFolders` 若无 1.6 分支会加载错文件（**未验证**）；删链接用 `cmd /c rmdir "D:\RimWorld-1.6\Mods\2009463077"`，**别**用 `Remove-Item -Recurse`（会删到工坊本体）。
**方案② 复制（完全独立）**：`Copy-Item -Recurse '<工坊mod目录>' 'D:\RimWorld-1.6\Mods\'`。代价：占空间、不随工坊自动更新，需手动同步。
### 4.4 日志：1.7 全机共用一份，1.6 待判定
- 1.7 实测日志固定在 `%USERPROFILE%\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Logs\Player.log`，**后启动者重写**（上一轮被挤成 `Player-prev.log`）；`-savedatafolder` 不搬日志（`src\launcher.js:78` 注释即此意）。
- 1.6 默认落点**未验证**。判定法：两套各传不同 `-savedatafolder`，先后启动，比 `Get-Item <Player.log> | select LastWriteTime` 与头几行 `Command:` / `RimWorld <版本>`——同一路径被改写且版本号在 1.6/1.7 间跳，就是共用。隔离建议：①1.6 启动传 `-logFile=<savedata>\Player.log`（1.6 实例日志曾出现在 `profiles\rhah\Player.log`，记录值 `docs\RimWorld-版本再核验-1.6还是1.7-20261024.md:119`；不传时的默认落点仍未验证）；②或错开时间跑，每轮把日志复制进 `runs\rimworld16-data\snapshots\` 留证。

## 5. 启动 1.6（独立数据目录）
```powershell
& 'D:\RimWorld-1.6\RimWorldWin64.exe' "-savedatafolder=E:\2026.7以后的新工程\DSH\RimWorld_auto_test\runs\rimworld16-data" "-logFile=E:\2026.7以后的新工程\DSH\RimWorld_auto_test\runs\rimworld16-data\Player.log"
```
1.6 安装的 `Mods\` 空 ⇒ **没有 RimBridgeServer、不能用 `src\cli.js` 驱动**；要自动化得先按 §4.3 把桥接 mod 放进去（该 mod 在 1.6 下能否用**未验证**）。1.7 与 1.6 理论上可同时开（不同 exe、不同 savedata），但**未验证**；日志可能互相覆盖（§4.4），并发时按「先后启动 + 快照」处理。

## 6. 启动前检查清单（**任一不过就不要启动**）
| 检查项 | 命令 | 期望 | 不对怎么办 |
|---|---|---|---|
| 版本 | `Get-Content 'D:\RimWorld-1.6\Version.txt'` | `1.6.x` | 不是 1.6 ⇒ 停，回 §3.3 |
| 程序集+入口 | `(Get-Item 'D:\RimWorld-1.6\RimWorldWin64_Data\Managed\Assembly-CSharp.dll').VersionInfo.FileVersion` 与 `Test-Path 'D:\RimWorld-1.6\RimWorldWin64.exe'` | `1.6.*` 与 `True` | 缺文件、或 `1.7.x`、或只有 `RimWorld.exe` ⇒ 装错，停 |
| savedata | `Get-ChildItem '<savedata>' \| select Name` | 首启后出现 `Config`、`Saves`，且 `Config\ModsConfig.xml` 存在 | 新文件落进 `%LocalLow%` ⇒ 参数没生效，查命令行 |
| 激活 mod 数 | `([xml](Get-Content '<savedata>\Config\ModsConfig.xml' -Raw)).modsConfigData.activeMods.li.Count` | 与本轮预期清单一致（同命令在 `profiles\rhah` 实测得 16） | 数量/条目不对 ⇒ 停，先改 ModsConfig |
| 进程 | `Get-CimInstance Win32_Process -Filter "Name='RimWorldWin64.exe'" \| select ProcessId,CommandLine` | 只有你已知的实例（或空） | 有陌生实例 ⇒ 先看命令行归属，**不要** taskkill 全杀 |
| 端口 | `Get-NetTCPConnection -State Listen \| ? {$_.LocalPort -ge 5174 -and $_.LocalPort -le 5183} \| select LocalPort,OwningProcess` | 空（1.6 安装不带桥接，不占端口） | 被占 ⇒ 换端口或先停占用者再启 |
| 本轮日志 | `Get-Item '<savedata>\Player.log' -EA 0 \| select LastWriteTime,Length` | mtime = 本轮启动时刻 | 无此文件 ⇒ `-logFile` 没生效，日志落到共用 `%LocalLow%`，按 §4.4 处理 |

## 7. 用这套 1.6 编 1.6 目标
```powershell
cd 'E:\2026.7以后的新工程\DSH\RimWorld_auto_test'
dotnet build "RimworldMods\ratkin_hunger_and_havoc\Source\HungerAndHavoc.csproj" -c Release --nologo -v m `
  -p:RimWorldVersion=1.6 `
  -p:RimWorldDir="D:\RimWorld-1.6" `
  -p:RimWorldManagedDir="D:\RimWorld-1.6\RimWorldWin64_Data\Managed" `
  -p:HarmonyPath="D:\SteamLibrary\steamapps\workshop\content\294100\2009463077\1.6\Assemblies\0Harmony.dll" `
  -p:OxygenDslDir="E:\2026.7以后的新工程\DSH\RimWorld_auto_test\RimworldMods\oxygen_dsl_framework" `
  -p:OxygenDslBuildDir="E:\2026.7以后的新工程\DSH\RimWorld_auto_test\runs\tmp\odf16\out" `
  -p:IrisMenusDll="E:\2026.7以后的新工程\DSH\RimWorld_auto_test\runs\tmp\iris16\IrisMenus.dll" `
  -p:TargetFrameworkRootPath="E:\2026.7以后的新工程\DSH\RimWorld_auto_test\runs\tmp\refasm\root"
```
- 出处：**已知可行的属性组合** = `docs\RHAH-1.6重建报告-20261024.md:82-93`（当时 1.6 游戏还在，实测 0 警告 0 错误），本次只把 `RimWorldDir` 换成新装目录。注意仓库里 mod 的 `Directory.Build.props` 默认游戏路径是**错的**（`E:/Apps/Steam/steamapps/common/RimWorld`，实测 8 个 mod 的 props 都这么写）⇒ **必须**命令行覆盖 `RimWorldDir`；1.6 下 `RimWorldManagedDir` 会被推导成 `$(RimWorldDir)/RimWorldWin64_Data/Managed`（实测 `ratkin_hunger_and_havoc\Directory.Build.props:9`），显式传更稳。
- 三个临时依赖**今天实测仍在**：`runs\tmp\refasm\root`（net48 参考程序集根，全局 NuGet 无 `Microsoft.NETFramework.ReferenceAssemblies.net48`，缺了报 MSB3644）、`runs\tmp\odf16\out`（ODF 1.6 产物）、`runs\tmp\iris16\IrisMenus.dll`；丢了按该报告 §3.1/§3.2 重建。`dotnet restore` 也必须带 `-p:RimWorldVersion=1.6`（否则 TFM 为空，报告 `:54/:147`）。产物落到 `RimworldMods\ratkin_hunger_and_havoc\1.6\Assemblies\`（git 跟踪文件，会显示 `M`）。

## 8. 风险与回滚
- steamcmd 要登录、可能卡 Steam Guard、下载 ~2–3 GB；失败就重跑同一条命令。本机 `D:\steamcmd\steamcmd.exe` 实测存在。1.6 安装**不受 Steam 客户端管理**：客户端看不见它，不会自动更新/校验；要更新只能手动重跑 §3.2（`+app_update 294100`）。
- 回滚 = 删本产物：`Remove-Item -Recurse -Force 'D:\RimWorld-1.6'`，再删 `runs\rimworld16-data\`；1.7 不受影响（两处目录无交集）。
- **绝不为装 1.6 去动 `D:\SteamLibrary\...\RimWorld`**：不切分支、不降级、不删它的 `Assembly-CSharp.dll`、不往它里面塞 1.6 文件。

## 9. 验收标准（四条全绿才算这条 handoff 做完）
1. `D:\RimWorld-1.6\Version.txt` 是 `1.6.x`。
2. 该目录有 `RimWorldWin64.exe` 与 `RimWorldWin64_Data\Managed\Assembly-CSharp.dll`（FileVersion `1.6.*`）。
3. 用 §5 命令跑起来后 `runs\rimworld16-data\` 出现 `Saves\` 与 `Config\`（含 `ModsConfig.xml`），且 **1.7 的 `%LocalLow%\...\Config\ModsConfig.xml` mtime 未被改动**。
4. §7 的 `dotnet build … -p:RimWorldVersion=1.6 -p:RimWorldDir=D:\RimWorld-1.6` 编过，**0 警告 0 错误**。

## 10. 未验证清单（别当事实用）
稳定分支当前是否仍为 1.6（及具体版本号）；1.6 不传 `-logFile` 时日志是否也落共用 `%LocalLow%`；junction 进来的 mod 其 `LoadFolders` 是否覆盖 1.6；steamcmd 非交互登录是否必须有 Steam Guard 码；独立安装是否自动扫描 Steam 客户端的工坊目录；buildid `23969874` 是否仍可下载。
