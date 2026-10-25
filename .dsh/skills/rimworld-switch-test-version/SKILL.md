---
name: rimworld-switch-test-version
description: 在 RimWorld 1.7（Steam 安装）与 1.6（D:\RimWorld-1.6 并行安装）之间切换自动化测试环境，并保证知道自己正在跑哪个版本。用户说「切到 1.6」「切回 1.7」「现在跑的是哪个版本」「1.6 起不来」时使用。
---

# 在 RimWorld 1.6 / 1.7 之间切换测试环境

**用途**：在 1.6 / 1.7 两套环境间切换测试，并保证每一步都知道「现在跑的是哪个版本」。

- **1.7**：`D:\SteamLibrary\steamapps\common\RimWorld`，入口 `RimWorld.exe`，程序集平铺在根目录（无 `Managed`），日志 `C:\Users\MikanseiP\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Logs\Player.log`。
- **1.6**：`D:\RimWorld-1.6`，入口 `RimWorldWin64.exe`，有 `RimWorldWin64_Data\Managed\`；`Data\` 已放 5 个 DLC（代码在 Managed 里），`Mods\` 有 Harmony/HAR/NewRatkinPlus/IrisMenus/OxygenDSL/CharacterEditor 的 junction + RHAH + RimBridgeServer。
- **测试工程**：`E:\2026.7以后的新工程\DSH\RimWorld_auto_test`（下文 `$w`）；1.7 用 `profiles\rhah`（16 个 activeMods、全小写），1.6 的存档与 Config 在 `D:\rw16data2`，桥接 `127.0.0.1:5174`。

## 第一步：判别当前状态
| 检查项 | 1.7 的期望值 | 1.6 的期望值 | 命令 |
|---|---|---|---|
| `Version.txt` | `1.7.4985 rev1269` | `1.6.4871 rev590`（运行期日志自报 `rev591`） | `Get-Content 'D:\SteamLibrary\steamapps\common\RimWorld\Version.txt','D:\RimWorld-1.6\Version.txt'` |
| 入口 exe | `RimWorld.exe` | `RimWorldWin64.exe` | `Get-ChildItem 'D:\SteamLibrary\steamapps\common\RimWorld\RimWorld.exe','D:\RimWorld-1.6\RimWorldWin64.exe'` |
| 是否有 `Managed` | `False`（程序集平铺） | `True`（`RimWorldWin64_Data\Managed`） | `Test-Path 'D:\SteamLibrary\steamapps\common\RimWorld\RimWorldWin64_Data\Managed','D:\RimWorld-1.6\RimWorldWin64_Data\Managed'` |
| 当前进程 | `RimWorld` | `RimWorldWin64` | `Get-Process RimWorld,RimWorldWin64 -ErrorAction SilentlyContinue \| Select-Object Id,ProcessName` |
| 日志落点 | `…\LocalLow\…\Logs\Player.log` | `D:\rw16data2\Player.log` | `Get-Item 'C:\Users\MikanseiP\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Logs\Player.log','D:\rw16data2\Player.log' \| Select-Object FullName,LastWriteTime` |

## 第二步：按方向执行
### 1.7 → 1.6
```powershell
$w='E:\2026.7以后的新工程\DSH\RimWorld_auto_test'
cd $w; node src/cli.js stop --profile rhah   # ⓪ 1.7 在跑先关它（只关 rhah 记录的 PID，不加 --force）
cd "$w\RimworldMods"   # ① 编译 1.6：cwd 必须是这里；IrisMenusDll、OxygenDslBuildDir 缺一不可（后者缺报 44 个 CS0246）
dotnet build ratkin_hunger_and_havoc\Source\HungerAndHavoc.csproj -c Release -p:RimWorldVersion=1.6 -p:RimWorldDir=D:\RimWorld-1.6 `
  -p:IrisMenusDll=D:\RimWorld-1.6\Mods\IrisMenus\1.6\Assemblies\IrisMenus.dll -p:OxygenDslBuildDir=D:\RimWorld-1.6\Mods\OxygenDSL\1.6\Assemblies
Get-FileHash "$w\RimworldMods\ratkin_hunger_and_havoc\1.6\Assemblies\*.dll" -Algorithm SHA256   # ② 产物落 1.6\Assemblies\（junction = 游戏目录，构建即部署）；核对 SHA 与大小
# ③ 写 D:\rw16data2\Config\ModsConfig.xml（用 write 工具，UTF-8 无 BOM）：<version>1.6.4871 rev590</version> + 下面 14 项全小写 <activeMods> + <knownExpansions> = 5 个 DLC；游戏退出会覆写，每次启动前重写
#    brrainz.harmony, ludeon.rimworld, ludeon.rimworld.royalty, ludeon.rimworld.ideology, ludeon.rimworld.biotech, ludeon.rimworld.anomaly, ludeon.rimworld.odyssey,
#    brrainz.rimbridgeserver, erdelf.humanoidalienraces, solaris.ratkinracemod, nanaloveyuki.oxygendsl, void.charactereditor, nanaloveyuki.irismenus, nanaloveyuki.ratkin.hungerandhavoc
# ④ 启动：三条参数缺一不可（ASCII 工作目录 / 独立存档 / 独立日志）；不要用工程 CLI 起 1.6（`node src/cli.js start` 只认 1.7），只能直接 Start-Process
$p16=Start-Process -FilePath 'D:\RimWorld-1.6\RimWorldWin64.exe' -PassThru `
  -ArgumentList '-savedatafolder=D:\rw16data2 -logFile D:\rw16data2\Player.log' -WorkingDirectory 'D:\RimWorld-1.6'
$log='D:\rw16data2\Player.log'   # ⑤ 等主界面：桥接在 play-data 载入后起，出现下行即已到主界面（禁 Start-Sleep）
while ((Get-Process -Id $p16.Id -ErrorAction SilentlyContinue) -and -not (Select-String -Path $log -Pattern 'GABP server running standalone' -SimpleMatch -Quiet)) { Wait-Process -Id $p16.Id -Timeout 2 -ErrorAction SilentlyContinue }
$env:RW16_TOKEN=(Select-String -Path $log -Pattern 'Bridge token: (\w+)').Matches[0].Groups[1].Value   # ⑥ 验桥接：ping→pong、tools/list=125、list_mods 的 activeCount 与 loadedSessionModCount 都是 14
node --input-type=module -e "import {GabpClient} from './src/gabp.js'; const c=new GabpClient({port:5174,token:process.env.RW16_TOKEN}); await c.connect(); const j=r=>JSON.stringify(r&&r.result!==undefined?r.result:r); console.log('ping',j(await c.callTool('rimbridge/ping'))); console.log('tools',(await c.listTools()).length); console.log('mods',j(await c.callTool('rimworld/list_mods'))); c.close();"
```

### 1.6 → 1.7
```powershell
$w='E:\2026.7以后的新工程\DSH\RimWorld_auto_test'
if (-not $p16) { $p16 = Get-Process RimWorldWin64 -ErrorAction SilentlyContinue | Select-Object -First 1 }   # ① 只杀 RimWorldWin64.exe 那个 PID，不要动 RimWorld.exe（1.7）
Stop-Process -Id $p16.Id -ErrorAction SilentlyContinue; Wait-Process -Id $p16.Id -Timeout 30 -ErrorAction SilentlyContinue
Get-Process RimWorldWin64 -ErrorAction SilentlyContinue   # 必须为空
$g='D:\SteamLibrary\steamapps\common\RimWorld'; cd "$w\RimworldMods"   # ② 编译 1.7：先 ODF 到 runs\tmp\odf17\out\，再编主目标（共享覆盖都在 $c）
$c=@('-c','Release','--nologo','-v','m','-p:RimWorldVersion=1.7',"-p:RimWorldDir=$g","-p:RimWorldManagedDir=$g","-p:OxygenDslDir=$w\RimworldMods\oxygen_dsl_framework",'-p:HarmonyPath=D:\SteamLibrary\steamapps\workshop\content\294100\2009463077\1.7\Assemblies\0Harmony.dll')
dotnet build oxygen_dsl_framework\Source\Mod\Oxygendsl.Mod.csproj @c "-p:OutputPath=$w\runs\tmp\odf17\out\"
dotnet build ratkin_hunger_and_havoc\Source\HungerAndHavoc.csproj @c "-p:OxygenDslBuildDir=$w\runs\tmp\odf17\out" "-p:IrisMenusDll=$w\runs\tmp\iris17\IrisMenus.dll"
$a='D:\SteamLibrary\steamapps\workshop\content\294100\3807233439\1.7\Assemblies'; $b="$w\runs\workshop-backup-$(Get-Date -Format yyyyMMdd-HHmmss)"   # ③ 备份落 runs\workshop-backup-<时间>\
New-Item -ItemType Directory -Force $b | Out-Null; Copy-Item "$a\*.dll" $b
Copy-Item "$w\RimworldMods\ratkin_hunger_and_havoc\1.7\Assemblies\HungerAndHavoc.dll","$w\RimworldMods\ratkin_hunger_and_havoc\1.7\Assemblies\HungerAndHavoc.Api.dll" $a -Force
cd $w; node src/cli.js start --profile rhah   # ④ 启动 1.7
node src/cli.js status --profile rhah   # ⑤ 看 bridgeResolution.ok=true；env-check 看 mods.activeModsCount=16
$ab="$w\runs\verify-0008-green2-backup-20261024\1.7\Assemblies"; Copy-Item "$ab\HungerAndHavoc.dll","$ab\HungerAndHavoc.Api.dll" $a -Force   # ⑥ 测完还原作者版（备份不在就按 docs\工坊还原-作者原版-20261024.md）
Get-FileHash "$a\*.dll" -Algorithm SHA256   # 必须是 15C09CCC…D6FE5（868352 B）/ 37DB1ECB…892E6F（14336 B）
```

## mod 集合归谁管

本 skill **只切换版本与数据目录**，不改变 mod 集合。

- **1.7**：mod 集合来自 profile `profiles\rhah\Config\ModsConfig.xml`（16 项，全小写）；增删 mod 走闭环 skill 的 `mods add` / `base-mods.json`（**该文件在仓库根** `base-mods.json`）流程，**不要手改** ModsConfig。
- **1.6**：mod 集合来自 `D:\rw16data2\Config\ModsConfig.xml`（当前 14 项，全小写）；1.6 没有等价的 CLI 流程，改它属于「1.6 环境搭建」，见 `docs\RimWorld-1.6补DLC与前置-20261025.md` 与 `docs\RimWorld-1.6-可运行化与验证-20261025.md`。

**切版本不装 mod；装 mod 不切版本。**

## 第三步：每步自检表
| 检查项 | 判据 | 不通过怎么办 |
|---|---|---|
| 版本号 | 1.6 日志 `RimWorld 1.6.4871 rev591`；1.7 `Version.txt` 与日志 `1.7.4985 rev1269` | 停掉重来：入口 exe 或工作目录选错了 |
| 进程唯一性 | `Get-Process RimWorld,RimWorldWin64` 最多一个，且属于目标版本 | 只关自己记录的那个 PID，再重起 |
| Harmony 是否打上 | 日志有 `Applied essential Harmony patches` | 查是否漏 `-WorkingDirectory`；查日志是否被另一实例截断 |
| `Illegal byte sequence` | 计数必须是 0 | 立刻停：1.6 的 CWD 含中文，带 `-WorkingDirectory 'D:\RimWorld-1.6'` 重启 |
| `Could not resolve` | RHAH/Oxygendsl/RimBridge 的关键类型 0 条（1.6 已知基线 6 条全来自 `Oxygendsl.IrisMenus` 引用 `IrisMenus 0.0.1.0`，非致命） | 出现别的类型 ⇒ 编译覆盖不对，重编并核对 SHA |
| 桥接端口 | 1.6：`127.0.0.1:5174` 的 ping→pong 且 tools=125；1.7：`status --profile rhah` 的 `bridgeResolution.ok=true` | 1.6 查是否只剩一个实例、端口是否被占；1.7 看 `node src/cli.js bridge --profile rhah` 的 reason |
| `activeMods` | 1.6 `list_mods` 的 `activeCount=14`；1.7 `env-check --profile rhah` 的 `mods.activeModsCount=16` | 1.6 重写 ModsConfig、1.7 `mods apply` 后重启；`restartRequired=true` 不算通过 |
| 日志落点 | 1.6 只写 `D:\rw16data2\Player.log`；1.7 写 `…\LocalLow\…\Logs\Player.log` | 1.6 漏了 `-logFile`：停掉带 `-logFile` 重起，否则两版本互相覆盖 |

## 相关文档
- `docs\RHAH-1.7重建报告-20261024.md` —— 1.7 完整构建参数与产物 SHA、回滚命令。
- `docs\RimWorld-1.6-可运行化与验证-20261025.md` —— 1.6 启动/桥接实测、日志原文、产物 SHA、回退步骤。
- `docs\工坊还原-作者原版-20261024.md` —— 工坊作者版 SHA 与还原方式。
- `docs\RimWorld-1.6补DLC与前置-20261025.md` —— 1.6 的 DLC/前置与 ModsConfig 写法（`docs\RHAH-0008-1.6场景验证-20261025.md` 当前不存在）。
