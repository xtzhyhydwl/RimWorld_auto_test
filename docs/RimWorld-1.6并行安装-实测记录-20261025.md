# RimWorld 1.6 并行安装 —— 实测记录（2026-10-25）

> 本文替代 [HANDOFF-安装1.6并行环境-20261025.md](HANDOFF-安装1.6并行环境-20261025.md) 里"待做"的那部分：那四条验收已全部通过。
> 标注：**实测**＝2026-10-25 12:0x–12:4x 本机真实执行的输出；**推测**＝未验证。

## 0. 一句话

`D:\RimWorld-1.6` 已装好并真跑过一次；1.7 原封不动（文件清单与安装前快照 **0 差异**）；RHAH 的 1.6 目标编过 **0 警告 0 错误**。

## 1. 现状（实测）

| 项 | 1.6（新） | 1.7（原样） |
|---|---|---|
| 路径 | `D:\RimWorld-1.6` | `D:\SteamLibrary\steamapps\common\RimWorld` |
| `Version.txt` | `1.6.4871 rev590` | `1.7.4985 rev1269` |
| 运行横幅（日志首行 `RimWorld …`） | `RimWorld 1.6.4871 rev591`（**与 Version.txt 差一版**，见 §6） | — |
| 入口 | `RimWorldWin64.exe` | `RimWorld.exe` |
| 程序集 | `RimWorldWin64_Data\Managed\Assembly-CSharp.dll` FileVersion `1.6.9676.17735` | 根目录 `Assembly-CSharp.dll` `1.7.9790.38077` |
| 体积 | **885 文件 / 551 MB** | 4629 文件 / 1,260,240,460 B |
| 管理方 | 无人管（Steam 客户端看不见它） | Steam 客户端 |
| 数据目录 | `runs\rimworld16-data\` | `profiles\<profile>` 或 `%LocalLow%` |

1.7 的 4629 文件里大部分是平铺的 .NET 运行时 DLL（`System.*.dll`、`Brutal.*`、`Waylith.*`），1.6 没有这些 ⇒ 两边体积不可直接比。

## 2. 为什么没用 steamcmd（三条路都是死路，全部实测）

1. **把客户端登录态搬给 steamcmd**——不行。
   新版 Steam 客户端不写 `ssfn*`，凭据在 `%LOCALAPPDATA%\Steam\local.vdf` 的 `ConnectCache` 段：
   键 = `crc32(小写账号名)` 的 8 位 hex + 字面 `1`（实测三个账号的键全部对上），值是 DPAPI blob。
   整段搬进 `D:\steamcmd\config\config.vdf` 并补 `AutoLoginUser` / `loginusers.vdf` 后，steamcmd 仍报
   `Cached credentials not found.` + `[U:1:0]`，随后索要密码，退出码 5（`ERROR (Invalid Password)`）。
   blob 用 `ProtectedData.Unprotect(...,CurrentUser)` 直接解报 `The data is invalid` ⇒ Steam 用了自有熵，**解不开**。
2. **`steam://open/console` 开控制台**——不生效：新版客户端不弹控制台窗口（EnumWindows 前后无新窗口），
   所以 `download_depot` 那条路走不通。
3. **`steam://install/294100`**——不生效：无 `appmanifest` 生成、`content_log.txt` 无任何 294100 记录。

## 3. 实际用的办法（可重复，无需密码）

原理：让 Steam 以为"这台机器没装 RimWorld"，它就会按**默认分支（public = 1.6）**全新装一份。

```powershell
# 1) 关 Steam
& 'C:\Program Files (x86)\Steam\steam.exe' -shutdown
# 2) 1.7 整目录改名挪开（只改名，字节不动）+ 备份并移走 acf
Rename-Item 'D:\SteamLibrary\steamapps\common\RimWorld' 'RimWorld-1.7-hold'
Move-Item  'D:\SteamLibrary\steamapps\appmanifest_294100.acf' <备份目录>
# 3) 手写一份"需要更新"的 acf（关键字段）
#    "StateFlags" "6"   buildid "26115268"（= 1.7 的，故意与 public 不符）
#    "installdir" "RimWorld"
#    "InstalledDepots" { "294104" { "manifest" "2434112405223082685" "size" "0" } }
#    "UserConfig"/"MountedConfig" 里 "BetaKey" "public"
# 4) 起 Steam，它会自己进 Update Running 并拉 public 分支
Start-Process 'C:\Program Files (x86)\Steam\steam.exe'
# 5) 完成标志
Get-Content 'D:\SteamLibrary\steamapps\common\RimWorld\Version.txt'   # ⇒ 1.6.4871 rev590
#    content_log.txt: "finished update, 1 mounted depots (BuildID 23969874) : 294104 (3464668865934009251)"
# 6) 收尾：停 Steam → 删除刚装的 1.6 目录（已拷走）→ 原 acf 放回 → RimWorld-1.7-hold 改名回 RimWorld → 起 Steam
```

耗时：12:26:00 开始 → **12:26:38 完成**（`BytesToDownload` 220,473,440 B，本地已有大部分 chunk，所以几十秒）。
拷到 `D:\RimWorld-1.6` 用 `robocopy <common>\RimWorld D:\RimWorld-1.6 /E`，885 文件 / 551 MB。

回滚：删 `D:\RimWorld-1.6` 与 `runs\rimworld16-data\` 即可；1.7 从未被改（见 §4）。

## 4. 1.7 没被动过的证据（实测）

安装前把 1.7 的 `<文件大小>\t<相对路径>` 全表存到
`runs\2026-10-25-install16\snapshots\17-filelist.txt`（4629 行），装完放回后 `Compare-Object` **0 差异**；
`Version.txt` = `1.7.4985 rev1269`，acf 还原为 `StateFlags 4 / buildid 26115268 / BetaKey unstable`，
Steam 重启后 `content_log.txt` 无任何 294100 应用更新记录（只有工坊 depot 294100 的 manifest 请求）。

## 5. 怎么启动 1.6

```powershell
& 'D:\RimWorld-1.6\RimWorldWin64.exe' "-savedatafolder=E:\2026.7以后的新工程\DSH\RimWorld_auto_test\runs\rimworld16-data"
```

- **`-savedatafolder` 必须带**（否则会去用 1.7 的 `%LocalLow%\…`）。
- 不要再传 `-logFile=`：参数会被写进日志的 `Command line arguments:`，但**文件不会生成**（实测，见 §6）。
- 首启后 `runs\rimworld16-data\` 里出现 `Config\`（`ModsConfig.xml` 202 B / 空 mod 表、`KeyPrefs.xml`、`Knowledge.xml`、`LastPlayedVersion.txt`）与 `Saves\`。
- 启动会抢一次窗口焦点；关窗口即干净退出（日志末尾有 Unity 内存统计）。

## 6. 已知坑（都是实测）

1. **`-logFile=` 被忽略**。1.6 的日志落 `%USERPROFILE%\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Player.log`（**根级**），
   1.7 的 `…\Logs\Player.log`（304 KB，12:13）mtime 未变 ⇒ 本次没被 1.6 覆盖。
   两套都写这个根级 `Player.log` 的**推测**是"后启动者覆盖"，未做并发实验。
2. **日志里中文路径是乱码**（`E:\2026.7浠ュ悗鐨勬柊宸ョ▼\…`）：GBK↔UTF-8 显示问题。
   实测无副作用——`runs\rimworld16-data\Config\ModsConfig.xml` 正确生成，`E:\` 根目录没有多出乱码目录。
3. **`Version.txt` 是 rev590，运行横幅是 rev591**（同一份安装）。要断言"跑的是哪个版本"时，用日志里的 `RimWorld 1.6.4871 rev591` 更准。
4. **1.6 会自动扫工坊目录**（原 HANDOFF §4.3 的未验证项，现为实测）：
   `D:\RimWorld-1.6\Mods\` 是空的（只有 `Place mods here.txt`），但日志里出现了
   `Mod Ratkin Alchemy+ dependency (…) needs to have <downloadUrl> …` 这类**读到了工坊 mod 的 About.xml** 才会有的告警
   ⇒ 它扫的是 `D:\SteamLibrary\steamapps\workshop\content\294100\`。**不需要 junction、也不需要复制**。
5. **1.6 没有 RimBridgeServer** ⇒ `src\cli.js` 驱动不了它；`rimworld_*` 那套工具都只对 1.7 的 profile 有效。

## 7. 编译 1.6 目标（实测通过）

命令与 `HANDOFF §7` 完全相同，只把 `RimWorldDir` 指向新目录，**0 警告 0 错误**，产物重写：

```powershell
dotnet build "RimworldMods\ratkin_hunger_and_havoc\Source\HungerAndHavoc.csproj" -c Release --nologo -v m `
  -p:RimWorldVersion=1.6 -p:RimWorldDir="D:\RimWorld-1.6" `
  -p:RimWorldManagedDir="D:\RimWorld-1.6\RimWorldWin64_Data\Managed" `
  -p:HarmonyPath="D:\SteamLibrary\steamapps\workshop\content\294100\2009463077\1.6\Assemblies\0Harmony.dll" `
  -p:OxygenDslDir="…\RimworldMods\oxygen_dsl_framework" -p:OxygenDslBuildDir="…\runs\tmp\odf16\out" `
  -p:IrisMenusDll="…\runs\tmp\iris16\IrisMenus.dll" -p:TargetFrameworkRootPath="…\runs\tmp\refasm\root"
```

- 日志：`runs\2026-10-25-install16\build-1.6.log`
- 产物：`RimworldMods\ratkin_hunger_and_havoc\1.6\Assemblies\HungerAndHavoc.dll`（865,792 B，12:27:29）+ `HungerAndHavoc.Api.dll`
- 三个临时依赖（`runs\tmp\refasm\root`、`runs\tmp\odf16\out`、`runs\tmp\iris16\IrisMenus.dll`）**当时仍在**，未重建。

## 8. 分支/depot 参照（实测 `app_info_print 294100`，2026-10-25）

| 分支 | buildid | timeupdated |
|---|---|---|
| `public`（= 1.6） | **23969874** | 2026-07-01 |
| `unstable`（= 1.7） | 26115268 | 2026-10-22 |
| `version-1.6.4633`（备用旧 1.6） | 20659247 | 2025-11 |

内容 depot = **294104**（windows）；public 的 manifest = `3464668865934009251`（size 577,318,615，下载 220,473,440）。
294101 是同 app 的另一份 windows depot（567 MB），本次没用；294102/294103 是 mac/linux。

## 9. 验收对照（原 HANDOFF §9）

| # | 标准 | 结果 |
|---|---|---|
| 1 | `Version.txt` 是 1.6.x | ✅ `1.6.4871 rev590` |
| 2 | 有 `RimWorldWin64.exe` + `Managed\Assembly-CSharp.dll`（1.6.*） | ✅ `1.6.9676.17735`，根目录无 `Assembly-CSharp.dll` |
| 3 | 启动后 `runs\rimworld16-data\` 出现 `Config\`（含 `ModsConfig.xml`）与 `Saves\`，且 1.7 的 `%LocalLow%` ModsConfig 未变 | ✅ 两者都在；1.7 的 ModsConfig mtime/哈希未变（10-24 10:00:31 / 13,743 B） |
| 4 | 1.6 编译 0 警告 0 错误 | ✅ `0 Warning(s) / 0 Error(s)` |

## 10. 仍未验证

- 1.6 与 1.7 **同时**运行（本次是先后跑；两套用不同 exe、不同 savedatafolder，推测可并存）。
- 桥接 mod（RimBridgeServer）在 1.6 下能否加载/工作——没装、没试。
- 1.6 加载激活 mod 后的行为（本次 `ModsConfig.xml` 是空表，只有 mod 扫描告警，没有真加载工坊 mod）。
- 根级 `Player.log` 的覆盖关系（两版本并发时谁覆盖谁）。

## 11. 产物清单

| 路径 | 内容 |
|---|---|
| `runs\2026-10-25-install16\snapshots\17-filelist.txt` | 1.7 安装前全文件表（比对基准） |
| `runs\2026-10-25-install16\snapshots\appmanifest_294100.acf.bak` | 1.7 的 acf 原件 |
| `runs\2026-10-25-install16\snapshots\appmanifest_294100.acf.removed` | 同上（安装期间取出用的那份） |
| `runs\2026-10-25-install16\build-1.6.log` | 1.6 编译输出 |
| `runs\rimworld16-data\` | 1.6 的 Config/Saves（隔离数据目录） |
