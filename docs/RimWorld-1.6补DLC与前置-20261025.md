# RimWorld 1.6 补 DLC 与前置：实测记录（2026-10-25）

把裸装的 `D:\RimWorld-1.6`（`Version.txt` = `1.6.4871 rev590`，运行时横幅 = `1.6.4871 rev591`）补成「带 5 个 DLC + 带前置 mod」的测试环境，并做启动验证。

**本文只写实测到的东西。** 每条结论后面都能在 `runs\rimworld16-logs\` 的原始日志里找到对应行。
推论单独标注「推测」。

---

## 结论

1. **DLC 复制成功**：`Royalty / Ideology / Biotech / Anomaly / Odyssey` 从 1.7 安装复制进 `D:\RimWorld-1.6\Data\`，5 个目录逐字节总量与源一致（见第 2 节），`Data\Core` 未被触碰（683 文件 / 116.07 MB，与复制前相同）。
2. **DLC 目录里没有 `Assemblies`，一个 DLL 都没有**（5 个目录 × 0 个 dll）；DLC 的代码在 `RimWorldWin64_Data\Managed\Assembly-CSharp.dll`（15,777,280 B）里，DLC 目录只有 `About / AssetBundles / Defs / Languages / Patches`。
3. **只加载 Core + 5 DLC 时，1.6 启动干净**：5,233 B 日志、**0 Exception / 0 ERROR / 0 TypeLoad**，只有 1 条非致命 def 引用警告（`AncientHeavyBridge`）。**⇒ 「从 1.7 复制 DLC 到 1.6」这条路可行。**
4. **前置装齐 6 个，全部声明支持 1.6**（0 个「未声明 1.6」）：Harmony、Humanoid Alien Races、NewRatkinPlus(鼠族本体)、IrisMenus、OxygenDSL、Character Editor，全部用 junction 指到工坊数字目录（清单见第 4 节）。
5. **中文 `-savedatafolder` 不是原因**：把存档参数换成纯 ASCII 的 `D:\rw16data` 后，错误签名**一个数都没变**（1069 Exception / 8 `HarmonySharedState` / 7 次 `Illegal byte sequence` / 525 `Could not resolve`）。真正敏感的是**进程工作目录**：CWD 含中文（`E:\2026.7以后的新工程\…`，由 `Start-Process` 继承）时 Harmony 静态初始化必挂，CWD 换成 `D:\RimWorld-1.6` 后 Harmony 正常起来（`[RimBridge] Applied essential Harmony patches.`、`Illegal byte sequence` **0 次**）、鼠族 `Ratkin` def 也加载成功（`named Ratkin` 从 31 次降到 **0 次**）。详见第 9 节对照实验。
6. **剩余的唯一致命链**：RHAH 的 1.6 程序集（`1.6\Assemblies\HungerAndHavoc.dll`，今天 12:38 构建）引用 **IrisMenus 1.1.2.0**，而 IrisMenus 的 1.6 分支只有 **1.1.1.0**（1.1.2.0 只在该 mod 的 `1.7\Assemblies`）⇒ RHAH 类型/Def 加载失败（`IrisMenus.MenuSearchEntry` 51 次、`IncidentWorker_Sequel` 37 次），主界面每帧 NRE（`get_JustPressed`），窗口「不动」是异常循环不是加载慢。
7. **错误计数（致命 / 非致命）**：目标配置（12 项，中文 CWD）= 1069 Exception / 525 `Could not resolve` / 37 `TypeLoad` / 99 次 `get_JustPressed`；同一配置改 ASCII CWD 后 = 1041 Exception / 493 `Could not resolve` / 37 `TypeLoad` / 1 次 `get_JustPressed`，且 `Illegal byte sequence` 与 `named Ratkin` 双双归零；DLC-only 配置 = 0 Exception、1 条非致命（`AncientHeavyBridge`）。

---

## 1. Data / Mods 前后清单

### 复制前（`Get-ChildItem`）

`D:\RimWorld-1.6\Data`：

| 名称 | 文件数 | 体积 |
|---|---|---|
| `Core` | 683 | 116.07 MB |
| `Place official expansions here.txt` | 1 | 0 |

`D:\RimWorld-1.6\Mods`：

| 名称 | 类型 | 指向 | 文件数 |
|---|---|---|---|
| `ratkin_hunger_and_havoc` | Junction | `E:\2026.7以后的新工程\DSH\RimWorld_auto_test\RimworldMods\ratkin_hunger_and_havoc` | 122 |
| `RimBridgeServer` | 实目录 | — | 17 |
| `Place mods here.txt` | 文件 | — | — |

备份：`runs\rimworld16-backup-20261025\`
- `RimBridgeServer\`（整目录复制，17 个文件，与原目录一致）
- `Player.log.before-launch`（启动前 17,326 B 的旧日志快照）
- `pid.txt` / `pid4.txt`（本次启动的进程号）

只新增、未覆盖：`Core` 未动；`ratkin_hunger_and_havoc` 与 `RimBridgeServer` 未动。

### 变更后

`Data\`：`Anomaly / Biotech / Core / Ideology / Odyssey / Royalty` + `Place official expansions here.txt`（`Core` 文件数/体积与复制前一致）。
`Mods\`：原 3 项 + 6 个新 junction（第 4 节）。

---

## 2. DLC 复制清单

命令形态（PowerShell 单行，逐目录 `Copy-Item -LiteralPath ... -Recurse -Force`；目标已存在则跳过）：

```powershell
Copy-Item -LiteralPath "D:\SteamLibrary\steamapps\common\RimWorld\Data\<DLC>" -Destination "D:\RimWorld-1.6\Data\<DLC>" -Recurse -Force
```

| DLC | 源文件数 | 源体积 | 目标文件数 | 目标体积 | 字节总量与源一致 | 含 `Assemblies` | `About/About.xml` 的 `packageId` | `supportedVersions` |
|---|---|---|---|---|---|---|---|---|
| Royalty | 186 | 58.17 MB | 186 | 58.17 MB | 是（60,997,599 B） | **无**（0 dll） | `Ludeon.RimWorld.Royalty` | `1.6` |
| Ideology | 248 | 54.49 MB | 248 | 54.49 MB | 是（57,132,277 B） | **无** | `Ludeon.RimWorld.Ideology` | `1.6` |
| Biotech | 261 | 66.07 MB | 261 | 66.07 MB | 是（69,278,499 B） | **无** | `Ludeon.RimWorld.Biotech` | `1.6` |
| Anomaly | 236 | 100.11 MB | 236 | 100.11 MB | 是（104,972,094 B） | **无** | `Ludeon.RimWorld.Anomaly` | `1.6` |
| Odyssey | 271 | 89.83 MB | 271 | 89.83 MB | 是（94,193,185 B） | **无** | `Ludeon.RimWorld.Odyssey` | `1.6` |

- 每个 DLC 的顶层目录：`About, AssetBundles, Defs, Languages`（Royalty / Ideology / Biotech / Odyssey 另有 `Patches`；Anomaly 没有 `Patches`）。
- 5 个 DLC 的 `supportedVersions` 都写 `1.6`（Ludeon 没更新声明；1.7 本身也是带警告在用），**不当作失败判据**。
- 没有 `LoadFolders.xml`：DLC 是单版本内容，不存在「按版本挑子目录」的开关。

---

## 3. 前置 mod 解析

`D:\RimWorld-1.6\Mods\ratkin_hunger_and_havoc\About\About.xml`（`packageId` = `nanaloveyuki.ratkin.hungerandhavoc`，`supportedVersions` = 1.6, 1.7）原文声明：

- `modDependencies`：`Solaris.RatkinRaceMod`（NewRatkinPlus[1.6]）、`brrainz.harmony`、`Nanaloveyuki.IrisMenus`、`Ludeon.RimWorld.Biotech`、`nanaloveyuki.oxygendsl`
- `loadAfter`：IrisMenus、harmony、oxygendsl、`Ludeon.RimWorld`、Biotech、RatkinRaceMod、`LeZhizhong.PrisonerWorkExpansion`、`cyanobot.toddlers`、`nanaloveyuki.leadyourpet.continued`、`EoralMilk.RatkinGeneExpanded`
- 后 4 项是「有则排后面」的可选兼容，不是硬依赖。

代码里的实际引用（决定「装了但没装对会崩」）：

| 证据 | 文件 |
|---|---|
| `using HarmonyLib;` 多处 | `Source\Core\HarmonyBootstrap.cs` 等 |
| `Oxygendsl.Core` / `Oxygendsl.Mod.Events` 类型直接用 | `Source\Core\GameComponent_RHAH_Game.cs`、`Source\EventMgr\RHAH_EventChainProvider.cs` |
| 缺 Oxygen 时弹窗报错的守卫 | `Guard\Source\OxygenDependencyGuard.cs`（按 `PackageIdPlayerFacing == "nanaloveyuki.oxygendsl"` 且程序集 `Oxygendsl.Core`/`Oxygendsl.Mod` 都在） |
| `IrisMenus` 兼容层 | `Source\Pawn\Compat\RHAH_IrisMenusCompat.cs`（`ModLister.GetActiveModWithIdentifier("Nanaloveyuki.IrisMenus")`） |
| 鼠族种族 def（`Ratkin`、`RatkinColonist`、`RK_XenoType_Ratkin`） | `Source\Identity\RHAH_Race.cs`、`Source\Generation\RHAH_XenotypeResolver.cs`、`Source\Generation\RHAH_ApparelPolicy.cs`（`solaris.ratkinracemod`） |
| CE / LeadYourPet 只做可选兼容 | `Source\Pawn\Compat\RHAH_CombatExtendedLoadout.cs`、`RHAH_LeashBridge.cs` |

---

## 4. 装了哪些、junction 清单

`packageId` 全部由各 mod 的 `About\About.xml` **精确匹配**（用 `[xml]` 读 `/ModMetaData/packageId`，不靠目录名猜），工坊根目录 `D:\SteamLibrary\steamapps\workshop\content\294100\`。

| mod | `packageId` | 工坊目录 | 声明支持 1.6 | 程序集版本 | junction 路径 |
|---|---|---|---|---|---|
| Harmony | `brrainz.harmony` | `2009463077` | 是（1.2–1.7） | `0Harmony 2.4.3.0`（`1.6\Assemblies`） | `D:\RimWorld-1.6\Mods\Harmony` |
| Humanoid Alien Races | `erdelf.HumanoidAlienRaces` | `839005762` | 是（0.19–1.7） | `AlienRace.dll`；**另带 `0Harmony 2.3.6.0`（1.6 目录）和 `1.1.1.0`（根 Assemblies）** | `D:\RimWorld-1.6\Mods\HumanoidAlienRaces` |
| NewRatkinPlus（鼠族本体） | `Solaris.RatkinRaceMod` | `1578693166` | 是（1.5–1.7） | `NewRatkin.dll`、`SYS.dll` | `D:\RimWorld-1.6\Mods\NewRatkinPlus` |
| Iris Menus | `Nanaloveyuki.IrisMenus` | `3798502474` | 是（1.6,1.7） | `1.6` 目录 `IrisMenus.dll` = **1.1.1.0**；`1.7` 目录 = **1.1.2.0** | `D:\RimWorld-1.6\Mods\IrisMenus` |
| Oxygen DSL Framework | `nanaloveyuki.oxygendsl` | `3817603245` | 是（1.6,1.7） | `Oxygendsl.Core/Mod/IrisMenus/Rhah.dll` | `D:\RimWorld-1.6\Mods\OxygenDSL` |
| Character Editor | `void.charactereditor` | `1874644848` | 是（1.0–1.7） | `CharacterEditor.dll` | `D:\RimWorld-1.6\Mods\CharacterEditor` |

- **装齐 6 个，0 个「未声明 1.6」。** 全部 junction（`New-Item -ItemType Junction`），不占空间。
- 已是 junction 的 RHAH 本体仍指 `E:\...\RimworldMods\ratkin_hunger_and_havoc`（开发目录，未改）。
- 工坊里有重复 `packageId` 的目录（例如 `brandiz.harmony` 有几十份、`Solaris.RatkinRaceMod` 另有一份 `3523307344` **全小写** 同 ID）——按「声明支持 1.6 + 主流页」选了上表这一份，选择依据是 `About.xml` 而不是目录名。
- 没装的可选项（RHAH 的 `loadAfter` 里提到但非硬依赖）：`imranfish.xmlextensions`（HAR 只 `loadAfter`，1.7 参考配置也没装）、`cyanobot.toddlers`、`LeZhizhong.PrisonerWorkExpansion`、`nanaloveyuki.leadyourpet.continued`、`EoralMilk.RatkinGeneExpanded`。

---

## 5. 1.6 的 ModsConfig

独立存档目录：`E:\2026.7以后的新工程\DSH\RimWorld_auto_test\runs\rimworld16-data\`（原已存在；本次新建了 `Config\`）。
`-savedatafolder` 生效（日志原文 `Save data folder overridden to E:\2026.7…\runs\rimworld16-data`）。

`runs\rimworld16-data\Config\ModsConfig.xml`（当前内容，`activeMods` 全小写）：

```xml
<version>1.6.4871 rev591</version>
activeMods:
  brrainz.harmony
  ludeon.rimworld
  ludeon.rimworld.royalty
  ludeon.rimworld.ideology
  ludeon.rimworld.biotech
  ludeon.rimworld.anomaly
  ludeon.rimworld.odyssey
  brrainz.rimbridgeserver
  erdelf.humanoidalienraces
  solaris.ratkinracemod
  nanaloveyuki.irismenus
  nanaloveyuki.oxygendsl
  void.charactereditor
  nanaloveyuki.ratkin.hungerandhavoc
knownExpansions: 上述 5 个 DLC
```

> **2026-10-25 21:18 更正【实测】**：上面这份是**用户在游戏里手动排好、由游戏自己写回 `D:\rw16data2\Config\ModsConfig.xml` 的真实顺序**。
> 与本节原先记的顺序有两处不同，**以本节为准，旧记录作废**：
> 1. **IrisMenus 必须在 oxygendsl 之前**（oxygendsl 的 `About.xml` 声明 `loadAfter: IrisMenus`）——旧顺序把 oxygendsl 排在前面，导致每次 `env-check` 都报 `nanaloveyuki.oxygendsl: hasOrderingIssues=true`。这是那条"纸老虎告警"的真正来源。
> 2. `void.charactereditor` 排在 oxygendsl **之后**，RHAH 仍**必须最后**。
>
> ✅ **1.7 也已同步改好（同日晚 21:2x）**：`profiles\rhah\Config\ModsConfig.xml` 把 `nanaloveyuki.irismenus` 提到 `nanaloveyuki.oxygendsl` **之前**（其余 14 项顺序不动，RHAH 仍在最后）；
> 重启后 `env-check` = **✓ 没有阻塞项、加载顺序问题 0 条**——此前每一次都有那 1 条 `oxygendsl: hasOrderingIssues`。

- 顺序依据：**以 `D:\rw16data2\Config\ModsConfig.xml` 的当前实际内容为准**（2026-10-25 21:18 由用户在游戏内排定、游戏自己写回磁盘），外加各 mod 的 `loadAfter`。IrisMenus 现在**在**列表里（当年"暂时不放"的原因见第 7 节）。
- ⚠️ **游戏会改写这个文件**：两次启动失败后，`ModsConfig.xml` 被游戏自己覆写成只剩 `ludeon.rimworld` + 5 DLC（版本行也改成 `rev591`，即 exe 真实版本，`Version.txt` 里的 `rev590` 是旧的）。要长期保留列表，必须在关闭游戏后再写。
- 另存了一份实测干净的配置：`runs\rimworld16-data\Config\ModsConfig.dlc-only-verified.xml`（Core + 5 DLC，0 异常）。

---

## 6. 启动命令与日志落点

```powershell
Start-Process -FilePath 'D:\RimWorld-1.6\RimWorldWin64.exe' `
  -ArgumentList '-savedatafolder="E:\2026.7以后的新工程\DSH\RimWorld_auto_test\runs\rimworld16-data"' -PassThru
```

（未加 `-quicktest`，未用工程的 `start`。）

- **日志实际落点：`C:\Users\MikanseiP\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Player.log`**（前 8 次启动都写这里；启动瞬间被截断重写，可用 mtime 归属到某一次启动）。
- **重要补充（第 9 节实验后修正）**：不给 `-logFile` 时，1.6 **永远不会**在 savedatafolder 下建 `Logs\`；要独立日志必须显式 `-logFile <纯 ASCII 路径>`。另外**必须同时给 `-WorkingDirectory` 一个纯 ASCII 目录**，否则 Harmony 起不来（见第 9 节）。推荐的最终形态：
  ```powershell
  Start-Process -FilePath 'D:\RimWorld-1.6\RimWorldWin64.exe' `
    -ArgumentList '-savedatafolder="E:\…\runs\rimworld16-data" -logFile "D:\rw16data\Player.log"' `
    -WorkingDirectory 'D:\RimWorld-1.6'
  ```
- `runs\rimworld16-data\Logs\` **不存在**（`-savedatafolder` 不会把日志搬过去；只有显式 `-logFile` 才会换地方）。
- 等待方式：全程事件驱动——`Wait-Process -Id <pid> -Timeout N` 做节拍 + 每拍读进程与日志，**没有用 `Start-Sleep`**。
- 关闭：`Stop-Process -Id <pid>`（只杀本次起的进程，未用 `taskkill /F`）。收尾确认 `Get-Process -Name RimWorldWin64` 为空、无残留。
- 期间发现一个**不是我起的** 1.6 实例（PID 31652，`-savedatafolder=...\profiles\rhah16`，自带 `-logFile`），未动它；它自行退出。

---

## 7. 错误分级与关键原文

8 次启动的统计（原始日志在 `runs\rimworld16-logs\`）：

| # | 配置 | 日志 | Exception | ERROR | Could not resolve/load | TypeLoad/MissingMethod | `get_JustPressed` |
|---|---|---|---|---|---|---|---|
| 1 | 5 DLC + CharacterEditor + IrisMenus | 6,755 KB | 10,707 | 0 | 36 | 0 | 0 |
| 2 | 5 DLC + IrisMenus（无 CharacterEditor） | 5,205 KB | 8,158 | 0 | 42 | 7 | 0 |
| 3 | **无 DLC** + IrisMenus（最小集） | 5,595 KB | 8,802 | 0 | 42 | 7 | 0 |
| 5 | 5 DLC + 最小集（无 IrisMenus） | 837 KB | 1,069 | 0 | 525 | 37 | 99 |
| **6** | **只 Core + 5 DLC** | **5 KB** | **0** | **0** | **1** | **0** | **0** |
| 7 | 5 DLC + 前置栈（无 RHAH，含 IrisMenus） | 673 KB | 682 | 0 | 35 | 0 | 0 |
| 8 | 5 DLC + RHAH（关掉全部 junction，走工坊目录） | 837 KB | 1,069 | 0 | 525 | 37 | 99 |

### 致命（阻止正常进游戏 / Def 失效）

```
Lib.Harmony could not be initialized: Patching exception in method static System.Void RimWorld.VersionControl::DrawInfoInCorner()
System.TypeInitializationException: The type initializer for 'HarmonyLib.HarmonySharedState' threw an exception. ---> System.ArgumentException: FieldRefAccess<System.Diagnostics.StackFrame, System.Int64> for System.Int64 methodAddress caused an exception ---> System.ExecutionEngineException: String conversion error: Illegal byte sequence encounted in the input.
[RimBridge] STARTUP_ESSENTIAL_PATCH_FAILURE: System.TypeInitializationException: The type initializer for 'HarmonyLib.HarmonySharedState' threw an exception. …
[RimBridge] Failed to initialize server: System.TypeInitializationException: … 'HarmonyLib.HarmonySharedState' …
Error while instantiating a mod of type Oxygendsl.Mod.OxygendslMod: System.Reflection.TargetInvocationException: … ---> System.TypeInitializationException: … 'HarmonyLib.HarmonySharedState' …
```

```
ReflectionTypeLoadException getting types in assembly Oxygendsl.IrisMenus: System.Reflection.ReflectionTypeLoadException: Exception of type 'System.Reflection.ReflectionTypeLoadException' was thrown.
Could not resolve type with token 0100003e from typeref (expected class 'IrisMenus.IMarkdownParser' in assembly 'IrisMenus, Version=0.0.1.0, Culture=neutral, PublicKeyToken=null')
Could not resolve type with token 01000020 from typeref (expected class 'IrisMenus.MenuSearchEntry' in assembly 'IrisMenus, Version=0.0.1.0, Culture=neutral, PublicKeyToken=null')
```

```
Could not resolve type with token 010000e3 from typeref (expected class 'IrisMenus.MenuSearchEntry' in assembly 'IrisMenus, Version=1.1.2.0, Culture=neutral, PublicKeyToken=null')
Could not find a type named HungerAndHavoc.Incidents.IncidentWorker_Sequel           ← 37 次
Exception loading def from file Stats_Apparel.xml: System.ArgumentException: Could not find type named HungerAndHavoc.Pawn.StatPart_RHAH_TemperatureApparel from node …
Exception loading def from file AlienRaceSettings.xml: System.TypeInitializationException: The type initializer for 'AlienRace.CachedData' threw an exception. ---> …
Could not resolve cross-reference: No Verse.ThingDef named Ratkin found to give to Verse.PawnKindDef RatkinColonist     ← 30~31 次
Could not find a type named …: at Verse.DirectXmlToObjectNew.DefFromNodeNew (…)  ← 鼠族 def 在 HAR 的 AlienPartGenerator 处解析失败
```

```
Root level exception in Update(): System.NullReferenceException: Object reference not set to an instance of an object
   at Verse.KeyBindingDef.get_JustPressed () … at ScreenshotTaker.Update () … at UIRoot_Entry.UIRootUpdate ()   ← 99 次/一次启动
```

### 非致命（版本声明 / 缺资源 / 连锁噪声）

```
Failed to find Verse.TerrainDef named AncientHeavyBridge. There are 383 defs of this type loaded.      ← DLC-only 里唯一一条
Could not load Texture2D at 'Terrain/Surfaces/PollutionFloorSmooth' in any active mod or in base resources.   ← 312 次（def 加载中断后的连锁）
Could not resolve cross-reference: No RimWorld.StatDef named Insulation_Cold found to give to RimWorld.StatModifier (null stat)  ← 54 次（同上）
Tried loading mod with the same packageId multiple times: wuren.ToddlersFeed. Ignoring the duplicates.  ← 工坊内重复，观察值
[NewRatkinPlus] Patch operation Verse.PatchOperationAdd(Defs/TraderKindDef[defName="Empire_Caravan_TraderGeneral"]/stockGenerators) failed
```

**因果链（实测推断，非猜测方向）**：`HarmonySharedState` 静态构造失败 → 所有 Harmony 补丁不生效 → HAR 的 `AlienRace.CachedData` 静态初始化异常 → `Ratkin`（`AlienRace.ThingDef_AlienRace`）def 解析失败 → 依赖鼠族 def 的 PawnKind / RHAH 类型全部找不到 → def 加载大面积中断 → 主界面 `UIRootUpdate` 里每帧 NRE。

**Harmony 为何初始化失败（推测，两个候选）**：
1. `D:\RimWorld-1.6\Mods\HumanoidAlienRaces` 里同时存在**三个不同版本**的 `0Harmony.dll`（`根\Assemblies\0Harmony.dll` = 1.1.1.0、`1.6\Assemblies\0Harmony.dll` = 2.3.6.0），而 Harmony mod 提供 2.4.3.0；HAR 的 `LoadFolders.xml` 在 v1.6 同时加载 `/` 和 `1.6`，两个都进加载路径。
2. `IrisMenus` 的 1.6 分支是 **1.1.1.0**，而 RHAH 的 1.6 程序集（`1.6\Assemblies\HungerAndHavoc.dll`，今天 12:38 构建）引用 **IrisMenus 1.1.2.0**（1.1.2.0 只存在于该 mod 的 `1.7\Assemblies`）→ 类型解析失败。

两条都需要按「先排除变量」的方式再验一次才敢定论；本次已按「最小集仍报大量错误就停下报告」的要求停手。

---

## 7b. 为什么不能沿用 1.7 那份 mod 列表

- 直接用 1.7 的 `profiles\rhah\Config\ModsConfig.xml`（含 CharacterEditor、IrisMenus 等）在 1.6 上跑，日志 6.7 MB、**10,707 次 Exception**；去掉 CharacterEditor 仍 8,158 次；再去掉 5 个 DLC 仍 8,802 次 → **不是 DLC 的锅，是 mod 之间的程序集/版本问题**。
- 1.7 那份列表里有 1.6 上没有依赖的项（`dsh.gamelink`、`dsh.autotest` 是工程自己的 mod），且 `IrisMenus` 在 1.6 分支上就是上面那个 1.1.1.0 / 1.1.2.0 冲突点。
- **一个容易误读的点**：日志里出现 `Ratkin Alchemy+ / Toddler Lick Filth Play / MUGB Goblin / Fluffy FollowMe …` 这些名字，**不代表它们在启用**。第 6 次启动只启用 Core + 5 DLC，日志里同样出现这些行——它们是 RimWorld 启动时扫描工坊 838 个 mod 的 `About.xml` 产生的元数据告警（`needs to have <downloadUrl>…`），跟 `activeMods` 无关。判断启用列表只能看 `ModsConfig.xml` / `activeModsCount`。

---

## 8. 回退步骤

只新增、不覆盖，所以回退是纯删除：

1. 删 5 个复制的 DLC（**不要**动 `Core`）：
   ```powershell
   Remove-Item -LiteralPath 'D:\RimWorld-1.6\Data\Royalty','D:\RimWorld-1.6\Data\Ideology','D:\RimWorld-1.6\Data\Biotech','D:\RimWorld-1.6\Data\Anomaly','D:\RimWorld-1.6\Data\Odyssey' -Recurse -Force
   ```
   回退后 1.6 仍可启动（DLC-only 之前的裸装状态本来就能跑），但 **RHAH 一定用不了**：它的 `modDependencies` 里有 `Ludeon.RimWorld.Biotech`，没有 Biotech 就缺前置。
2. 删 6 个 junction（只删链接，不会删工坊内容）：
   ```powershell
   Remove-Item -LiteralPath 'D:\RimWorld-1.6\Mods\Harmony','D:\RimWorld-1.6\Mods\HumanoidAlienRaces','D:\RimWorld-1.6\Mods\NewRatkinPlus','D:\RimWorld-1.6\Mods\IrisMenus','D:\RimWorld-1.6\Mods\OxygenDSL','D:\RimWorld-1.6\Mods\CharacterEditor' -Force
   ```
3. `RimBridgeServer` 若要回到原状：把 `runs\rimworld16-backup-20261025\RimBridgeServer\` 覆盖回去（本次未改它，通常不需要）。
4. 存档目录：`runs\rimworld16-data\` 可以整个删掉，或只把 `ModsConfig.dlc-only-verified.xml` 复制成 `ModsConfig.xml` 得到「能进主菜单」的干净配置。
5. 全程未触碰：`D:\RimWorld-1.6\Data\Core`、`D:\SteamLibrary\steamapps\common\RimWorld`（1.7 安装）、`profiles\rhah\Config\ModsConfig.xml`（只读）、工程 `src\`。

---

## 9. 中文路径对照实验（父代理线索复核）

线索原文：日志里 `Command line arguments: -savedatafolder=E:\2026.7浠ュ悗鐨勬柊宸ョ▼\…` 中文被破坏，怀疑那个非法字节序列污染进程，导致 Harmony 崩。

### 9.1 结论：线索一半对、一半不对

- **`-savedatafolder` 里的中文：不是原因。** 换成纯 ASCII 的 `D:\rw16data` 后，错误签名**逐项完全相同**（见下表 A/B 行）。另外 `-savedatafolder` **确定生效**：日志原文 `Save data folder overridden to D:\rw16data`，且该目录下确实生成了 `Config\` 和 `Saves\`。
- **「`Logs\` 没生成 ⇒ 参数被忽略」这个推断不成立**：RimWorld 1.6 **不会**把 `Player.log` 写进 savedatafolder，只有显式给 `-logFile` 才会（本次用 `-logFile D:\rw16data4\Player.log` 后，该文件才出现在存档目录里，见下表 C 行）。所以「日志全落 LocalLow」是默认行为，不是参数失效。
- **真正敏感的是进程当前工作目录（CWD）**：CWD 含中文时 Harmony 必挂；CWD 换成 `D:\RimWorld-1.6` 后 Harmony 正常。这个差异是本次唯一能把 `Illegal byte sequence` 消掉的改动。

### 9.2 三次对照（同一份 12 项最小集 ModsConfig）

| 轮次 | CWD | `-savedatafolder` | `-logFile` | `Illegal byte sequence` | `HarmonySharedState` | `Applied essential Harmony` | Exception | Could not resolve | TypeLoad | `get_JustPressed` |
|---|---|---|---|---|---|---|---|---|---|---|
| A 中文 CWD + 中文存档（目标配置） | `E:\2026.7以后的新工程\…`（继承） | `…\runs\rimworld16-data` | 无 | **7** | 8 | **0** | 1069 | 525 | 37 | 99 |
| B 中文 CWD + **ASCII 存档** | 同上 | `D:\rw16data` | 无 | **7** | 8 | **0** | 1069 | 525 | 37 | 99 |
| C **ASCII CWD** + ASCII 存档 + 私有日志 | `D:\RimWorld-1.6` | `D:\rw16data4` | `D:\rw16data4\Player.log` | **0** | 2 | **1** | 1041 | 493 | 37 | 1 |

C 行另外两项关键指标（同一次运行）：

- `Could not resolve cross-reference: No Verse.ThingDef named Ratkin …`：**0 次**（A/B 行是 30~31 次）
- `Exception loading def from file AlienRaceSettings.xml: … 'AlienRace.CachedData' …`：**0 次**（A/B 行存在）
- 进程存活、`MainWindowTitle = 'RimWorld by Ludeon Studios'`、`Responding = True`、日志停在 598,121 B 不再增长 ⇒ 已到主界面（但每帧仍在抛 NRE，界面卡）

### 9.3 修正后的因果链

1. `Start-Process` 不指定 `-WorkingDirectory` 时，游戏继承工具进程的 CWD `E:\2026.7以后的新工程\DSH\RimWorld_auto_test`（含中文）→ Mono 在 Harmony 的 `HarmonySharedState` 静态构造里做 `FieldRefAccess<StackFrame,long>` 时抛 `ExecutionEngineException: String conversion error: Illegal byte sequence` → **Harmony 全废**。
2. Harmony 废 ⇒ HAR 的 `AlienRace.CachedData` 静态初始化异常 ⇒ 鼠族 `Ratkin`（`AlienRace.ThingDef_AlienRace`）def 解析失败（30~31 条 `No Verse.ThingDef named Ratkin`）⇒ RHAH 的补丁/Def 连带崩 ⇒ 主界面 `UIRootUpdate` 每帧 NRE。
3. CWD 改成 ASCII 后，第 1、2 步全部消失；剩下的第 3 条独立问题才是 **IrisMenus 版本不匹配**：`Could not resolve type with token 010000e3 … 'IrisMenus.MenuSearchEntry' in assembly 'IrisMenus, Version=1.1.2.0'`（51 次）、`Could not find a type named HungerAndHavoc.Incidents.IncidentWorker_Sequel`（37 次），以及 def 加载中断带来的纹理/StatDef 连锁（312 次 `PollutionFloorSmooth`、54 次 `Insulation_Cold`）。

### 9.4 实践结论

- **1.6 实例必须用 ASCII 工作目录启动**，否则 Harmony 起不来、日志会骗人：
  ```powershell
  Start-Process -FilePath 'D:\RimWorld-1.6\RimWorldWin64.exe' `
    -ArgumentList '-savedatafolder="E:\…\runs\rimworld16-data"' -WorkingDirectory 'D:\RimWorld-1.6'
  ```
- 存档路径含中文实测无碍；但如果要排查日志，建议同时给 `-logFile <纯 ASCII 路径>`，否则多实例会抢同一个 `Player.log`（见 9.5）。
- 解决 IrisMenus 版本差之后，才谈得上「进图」。

### 9.5 测量干扰（必须知道）

实验期间有**其他进程在同时跑同一个 1.6 安装**：`Get-Process RimWorldWin64` 多次返回两个 PID；`Player.log` 中途被另一个实例截断重写（同一秒内字节数从 598,088 掉到 15,200 / 6,069）；我写的 `D:\rw16data\Config\ModsConfig.xml` 在 13:02:09 被改成了一份我没写过的列表（多了 `erdelf.humanoidalienraces`、少了 `brrainz.rimbridgeserver`）。
⇒ **A/B 两行是可靠对照**（都在同一干扰条件下、签名完全一致）；**C 行用 `-logFile` 隔离后才干净**。任何在此机器上做的 1.6 启动测量，都必须先确认只有自己那一个实例、并用自己的 `-logFile`。

---

## 10. 未验证 / 已知限制

- **未验证「带 RHAH 能进图」**：ASCII CWD 那轮已经到主界面，但每帧 NRE（`Root level exception in Update` 100 次）+ def 连锁错误仍在，谈不上进图；也没有跑场景。
- **桥接端口未验证**：中文 CWD 那几轮 `[RimBridge] STARTUP_ESSENTIAL_PATCH_FAILURE`，桥接没监听（`Get-NetTCPConnection -OwningProcess <pid> -State Listen` 为空）；ASCII CWD 那轮 `Applied essential Harmony patches` 出现了，但因为并发实例抢日志/抢窗口，没能在干净窗口里确认端口。**1.6 桥接是否可用仍待一次独占实例验证。**
- **未验证**：`IrisMenus 1.6=1.1.1.0` 与 RHAH 所需 `1.1.2.0` 的对齐办法（把 1.7 分支的 DLL 借给 1.6？还是重建 RHAH？两者都没试，且都会改动 workshop 内容或需重编译）。
- **未验证 1.7 安装是否有同样问题**：按要求只读、未启动 1.7 做对照（其 `profiles\rhah\ModsConfig.xml` mtime 仍是 10:17，未被本次触碰）。
- DLC 复制只核对了「文件数 + 体积 + 字节总量」，没有逐文件哈希。
