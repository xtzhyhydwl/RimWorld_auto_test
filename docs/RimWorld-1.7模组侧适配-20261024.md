# RimWorld 1.7 模组侧适配（2026-10-24 17:49–18:05 Asia/Shanghai）

> 范围：**只改文件**，本轮**没有启动游戏**。17:50:41 那次 1.7 启动是**另一个代理**发起的（`runs/rhah-1.7-startup-20261024/start-console.txt`），我只读了它留下的日志与产物。
> 未打开/未加载/未保存玩家任何存档；未改 Steam 分支；**第三方 mod 一个文件都没改**；未改游戏安装目录（只读）。
> 全文区分 **实测**（读到的文件原文 / 命令输出）与 **推测**（按机制推理）。
> 现场原文：`runs/rimworld-1.7-mod-adapt-20261024/`。

---

## 结论（5 行）

1. **14 项里 7 项的 `supportedVersions` 实测含 `1.7`**：`brrainz.harmony`、`erdelf.HumanoidAlienRaces`、`Solaris.RatkinRaceMod`、`Nanaloveyuki.IrisMenus`、`nanaloveyuki.oxygendsl`、`nanaloveyuki.ratkin.hungerandhavoc`，以及**本轮修好的 `dsh.gamelink`**；`ludeon.rimworld`（Core）**没有** `supportedVersions` 元素（游戏只打一条 WARN，Core 照常加载）。
2. **我们自己的 mod 改了两行**：`mods\DSH_GameLink\About\About.xml` 的 `supportedVersions` 加 `<li>1.7</li>`、`LoadFolders.xml` 加 `<v1.7><li>/</li></v1.7>`（`git diff` = 2 文件 4 行新增）。**DLL 未重编**：判断是「**大概率不必重编，未验证**」——依据见 §3.3（源码用到的 18 个 RimWorld 符号在 1.7 `Assembly-CSharp.dll` 里全部仍存在；`UnityEngine*=UnityStubs` 有运行时重定向）；但**若确需重编，现有 `csproj` 有三处必须先改**（§3.4）。
3. **profile 版本串 `1.6.4871 rev591` → `1.7.4985 rev1269`**。这个串**不是猜的**：17:50:46 的 1.7 会话自己写过一份同值文件，且日志第 158 行逐字写出「we are running 1.7.4985 rev1269」。
4. **硬阻塞只有一个第三方：`brrainz.rimbridgeserver`（RimBridgeServer 2.1.1）**——它不只是"声明"问题，**二进制层就不兼容 1.7**：引用 `Assembly-CSharp 1.6.9655.19392` 和 5 个 `UnityEngine.*Module` 程序集，而这些 Unity 模块程序集在 1.7 安装里**已全部不存在**（只剩 `UnityStubs.dll`）。次要项：5 个 Ludeon DLC 的 `About.xml` 仍只写 `1.6`（Ludeon 自己的文件，我们不动）。
5. **"不改第三方能不能起 1.7"**：**游戏本体能起（17:50 已起过一次，跑到 mod 列表重建成即被关闭）；但桥接起不来 ⇒ 自动化测试跑不通**。另有一条**比版本串更致命的实测**：`ModsConfig` 版本串与游戏不一致时，1.7 会**把 mod 列表整个重置**（日志 158 行 `Resetting.`），14 项被削成只剩 Core——这正是本轮必须改版本串的原因（§4.2）。

---

## 一、14 项核对表（全部实测读自 About.xml）

搜过的位置（4 个根、共 **885 份** `About\About.xml`，用 `-Filter About.xml` + 只认 `\About\About.xml$`）：

- 工坊 `D:\SteamLibrary\steamapps\workshop\content\294100\*`
- 游戏 `D:\SteamLibrary\steamapps\common\RimWorld\Mods\`
- 游戏 `D:\SteamLibrary\steamapps\common\RimWorld\Data\`
- 工程 `E:\2026.7以后的新工程\DSH\RimWorld_auto_test\mods\`

**14 项全部找到 About.xml，没有一项是"未找到"。**

| # | packageId | About.xml 路径 | supportedVersions（原文） | 支持 1.7? | 若要支持 1.7 缺什么 |
|-|-|-|-|-|-|
| 1 | `brrainz.harmony` | `…\workshop\content\294100\2009463077\About\About.xml` | `1.2,1.3,1.4,1.5,1.6,1.7` | **是** | 无。`LoadFolders` 有 `<v1.7>` → `1.7\Assemblies\0Harmony.dll`（**3.0.0.0**，10/15） |
| 2 | `ludeon.rimworld` | `…\RimWorld\Data\Core\About\About.xml` | **无此元素**（只有 packageId / author / forceLoadBefore） | **不受门禁** | 无（Core 特例）。实测日志 316 行只给一条 `WARN … is missing supported versions list in About.xml!`，Core 照常加载 |
| 3 | `ludeon.rimworld.royalty` | `…\Data\Royalty\About\About.xml` | `1.6` | **否（按声明）** | 缺 `<li>1.7</li>`；DLC 数据目录无版本分档（只有 `About/Defs/Languages/Patches/AssetBundles`）。**Ludeon 文件，本任务不改** |
| 4 | `ludeon.rimworld.ideology` | `…\Data\Ideology\About\About.xml` | `1.6` | **否（按声明）** | 同上 |
| 5 | `ludeon.rimworld.biotech` | `…\Data\Biotech\About\About.xml` | `1.6` | **否（按声明）** | 同上 |
| 6 | `ludeon.rimworld.anomaly` | `…\Data\Anomaly\About\About.xml` | `1.6` | **否（按声明）** | 同上 |
| 7 | `ludeon.rimworld.odyssey` | `…\Data\Odyssey\About\About.xml` | `1.6` | **否（按声明）** | 同上 |
| 8 | `erdelf.HumanoidAlienRaces` | `…\workshop\content\294100\839005762\About\About.xml` | `0.19,1.0,1.1,1.2,1.3,1.4,1.5,1.6,1.7` | **是** | 无。`<v1.7>` → `1.7\Assemblies\{AlienRace,0Harmony}.dll`（10/16）；`loadAfter: imranfish.xmlextensions`（本 profile 未装，仅影响顺序偏好） |
| 9 | `Solaris.RatkinRaceMod` | `…\workshop\content\294100\1578693166\About\About.xml` | `1.5,1.6,1.7` | **是** | 无。`<v1.7>` → `/ + 1.6 + 1.7 + Contents`，`1.7\Assemblies\NewRatkin.dll` |
| 10 | `Nanaloveyuki.IrisMenus` | `…\workshop\content\294100\3798502474\About\About.xml` | `1.6,1.7` | **是** | 无。`<v1.7>` → `1.7 + Common`，`1.7\Assemblies\IrisMenus.dll`（10/24 08:40） |
| 11 | `nanaloveyuki.oxygendsl` | `…\workshop\content\294100\3817603245\About\About.xml` | `1.6,1.7` | **是** | 无。`<v1.7>` → `/ + 1.7`，`1.7\Assemblies\{Oxygendsl.Core,Oxygendsl.Mod}.dll`，另带 `1.7\IrisMenus\`、`1.7\Rhah\` 子模块 |
| 12 | `brrainz.rimbridgeserver` | `…\RimWorld\Mods\RimBridgeServer\About\About.xml` | `1.6`（modVersion 2.1.1） | **否** | **三样都缺**：①`<li>1.7</li>`；②`LoadFolders` 的 `<v1.7>`（现只有 `<v1.6>` → `1.6\Assemblies\`）；③**真正的 1.7 二进制**——见 §5.2 |
| 13 | `nanaloveyuki.ratkin.hungerandhavoc` | `…\workshop\content\294100\3807233439\About\About.xml` | `1.6,1.7` | **是** | 无。`<v1.7>` → `1.7/Guard`，`1.7\Assemblies\HungerAndHavoc.dll`（10/24 08:40）+ `1.7\Guard\Assemblies\HungerAndHavocGuard.dll` |
| 14 | `dsh.gamelink` | `…\RimWorld_auto_test\mods\DSH_GameLink\About\About.xml`（游戏 `Mods\DSH_GameLink` 是 junction 指过来） | 原 `1.6` → **改后 `1.6,1.7`** | **是（本轮修）** | 已补 `<li>1.7</li>` + `<v1.7>`；**DLL 未重编**（§3.3） |

### 依赖 / 加载顺序（实测，供排 `ModsConfig` 参考）

| mod | modDependencies | loadAfter / loadBefore |
|-|-|-|
| `brrainz.harmony` | 无 | `loadBefore: Ludeon.RimWorld` |
| `erdelf.HumanoidAlienRaces` | 无 | `loadAfter: imranfish.xmlextensions` |
| `Solaris.RatkinRaceMod` | `erdelf.HumanoidAlienRaces, brrainz.harmony` | `loadAfter: brrainz.harmony, Ludeon.RimWorld, erdelf.HumanoidAlienRaces` |
| `Nanaloveyuki.IrisMenus` | `brrainz.harmony` | — |
| `nanaloveyuki.oxygendsl` | `brrainz.harmony` | `loadAfter: brrainz.harmony, Nanaloveyuki.IrisMenus` |
| `nanaloveyuki.ratkin.hungerandhavoc` | `Solaris.RatkinRaceMod, brrainz.harmony, Nanaloveyuki.IrisMenus, Ludeon.RimWorld.Biotech, nanaloveyuki.oxygendsl` | `loadAfter` 12 项（含未装的 `LeZhizhong.PrisonerWorkExpansion`、`cyanobot.toddlers`、`nanaloveyuki.leadyourpet.continued`、`EoralMilk.RatkinGeneExpanded`——缺 mod 时 `loadAfter` 无害） |
| `dsh.gamelink` | `brrainz.harmony` | `loadAfter: brrainz.harmony` |
| `brrainz.rimbridgeserver` | 无 | — |

> 旁注（实测，与 14 项无关但会刷日志）：日志里 `Mod DSH GameLink dependency (brrainz.harmony) needs to have <downloadUrl> and/or <steamWorkshopUrl> specified.` 是**长期存在的无害 WARN**，不是 1.7 新增；同样的 WARN 出现在 Bike Probe / Food Control / 一堆第三方 mod 上。

---

## 二、1.7 侧关键机制（实测，来自 `Logs\Player.log`）

| 事实 | 证据 |
|-|-|
| 1.7 日志**不在** `-logFile` 指定的路径，改到 `%USERPROFILE%\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Logs\Player.log` | 该文件 17:50:46 / 92184 B，第 2–8 行自报 `Started/Version/Runtime/Process/Log file/Command`；`profiles\rhah\` 下**没有**生成新的 `Player.log` |
| 运行期确实是 1.7 | 第 4 行 `Version: 1.7.4985.1269`、第 5 行 `Runtime: .NET 10.0.13-waylithclr.dev` |
| **版本串不匹配 ⇒ mod 列表被重置** | 第 158 行：`INFO [Verse] Mods config data is from version 1.6.4871 rev591 while we are running 1.7.4985 rev1269. Resetting.` |
| 该会话在 17:54:03 结束（未跑到主菜单之后的桥接启动） | 末行 `2026-10-24 17:54:03.142 INFO Log closed.`；全文**没有任何** RimBridge 启动行 |
| 1.7 里 Unity 只剩 `UnityStubs.dll` | 安装根目录无任何 `UnityEngine*.dll`；`RimWorld.runtimeconfig.json` 有 `"WaylithClr.AssemblyNameRedirects": "UnityEngine*=UnityStubs;…"` |

---

## 三、`dsh.gamelink` 改动

### 3.1 改了什么（`git diff` 原文，2 文件 +4 行，0 删除 ⇒ 回滚 = 删掉这 4 行）

```diff
--- a/mods/DSH_GameLink/About/About.xml
+++ b/mods/DSH_GameLink/About/About.xml
@@ -5,6 +5,7 @@
   <packageId>dsh.gamelink</packageId>
   <supportedVersions>
     <li>1.6</li>
+    <li>1.7</li>
   </supportedVersions>
```
```diff
--- a/mods/DSH_GameLink/LoadFolders.xml
+++ b/mods/DSH_GameLink/LoadFolders.xml
@@ -3,4 +3,7 @@
   <v1.6>
     <li>/</li>
   </v1.6>
+  <v1.7>
+    <li>/</li>
+  </v1.7>
```
- 两文件回读均通过 XML 解析：`supportedVersions = [1.6,1.7]`、`LoadFolders = [v1.6,v1.7]`。
- `1.6` **保留未删**；`<v1.7>` 指向 `/`（同一份内容，无 1.7 专用目录）。
- 刻意**没做**的事：没加 `<v1.7>` 却不加 supportedVersions（那会触发 `ModLoadFolderDefinesUnsupportedGameVersion` 红字），也没删 1.6 块。

### 3.2 工程结构（实测）

```
mods\DSH_GameLink\
  About\About.xml
  Assemblies\DSH_GameLink.dll (+ .pdb)   ← 1.6 编的
  LoadFolders.xml
  Source\DSH_GameLink.csproj  EventLine.cs  GameLinkComponent.cs  LiveChannel.cs  LogTap.cs
```

### 3.3 是否必须重编：**大概率不必，未验证**

| 判据 | 实测 | 读法 |
|-|-|-|
| DLL 引用的 RimWorld 程序集版本 | `Assembly-CSharp, Version=1.6.9676.17735`（1.7 是 `1.7.9790.38077`） | .NET Core 按简单名绑定、且 `WaylithClr.LaxAssemblyBinding=true` ⇒ 版本差**大概率**不致命（推测） |
| DLL 引用的 `0Harmony` | `2.4.3.0`（1.7 Harmony 目录是 `3.0.0.0`） | 大版本差是**最大风险点**；我们只用 `new Harmony(id)` / `harmony.Patch(target, postfix:)` / `AccessTools.Method` 三个 API，未验证 3.x 是否仍原样提供 |
| 源码用到的 RimWorld 符号是否还在 1.7 | 对 1.7 `Assembly-CSharp.dll` 元数据做字符串核对：`GameComponent` / `GameComponentUpdate` / `LoadedGame` / `StartedNewGame` / `GenTicks` / `TicksGame` / `TickRateMultiplier` / `FreeColonistsCount` / `forcePause` / `SaveDataFolderPath` / `StaticConstructorOnStartup` / `ProgramState` / `AnyPlayerHomeMap` / `mapPawns` / `WindowStack` / `TickManager` / `CurrentMap` / `uniqueID` —— **18/18 FOUND** | 没有"1.7 删了我们用的 API"的直接证据 ⇒ **不构成必须重编的理由** |
| 心跳用的 `UnityEngine.Time.realtimeSinceStartup` | 1.7 无 `UnityEngine.CoreModule.dll`，但 `UnityStubs.dll` 里 `realtimeSinceStartup` **FOUND**，且 runtimeconfig 有 `UnityEngine*=UnityStubs` 重定向 | **可能照旧工作**（推测）；失败也只是心跳那条事件丢，且被 `try/catch` 兜住 |
| 目标框架 | csproj `net472`；1.7 宿主 `net10.0`、根目录有 `mscorlib.dll` 兼容 facade | net472 程序集能否被 Waylith CoreCLR 正常加载：**未验证** |

**结论写法**：`需要重编 / 不需要，未验证` → 取「**不需要（未验证）**」；不盲改 `About`/DLL 之外的东西，也不重编（本轮不启动游戏，重编后无法验证反而失去可回滚性）。

### 3.4 若真要重编：现有 `csproj` 有 3 处必须先改（未改，仅记录）

| 位置 | 现值 | 1.7 下为什么不成立 |
|-|-|-|
| `Source\DSH_GameLink.csproj:22` `RimWorldManagedDir` | `…\RimWorld\RimWorldWin64_Data\Managed` | **该目录已被删除**（`RimWorldWin64_Data` 下只剩 `Plugins`）；1.7 托管程序集平铺在**安装根目录** |
| `Source\DSH_GameLink.csproj:25` `HarmonyDir` | `…\294100\2009463077\1.6\Assemblies` | 1.7 应指 `1.7\Assemblies`（Harmony 3.0.0.0） |
| `TargetFramework` / Unity 引用 | `net472` + `UnityEngine.CoreModule` | 1.7 是 `net10.0` + `UnityStubs.dll`（1.6 编译链随本次更新从本机消失） |

---

## 四、`profiles\rhah\Config\ModsConfig.xml` 改动

### 4.1 改动前 / 后

| 项 | 值 |
|-|-|
| 改前（我 17:52 读到） | `<version>1.6.4871 rev591</version>`；`activeMods` 14 项；`knownExpansions` 6 项 |
| 改后（现文件，17:54:24 的最终态） | `<version>1.7.4985 rev1269</version>`；`activeMods` **仍 14 项、内容一字未动**；`knownExpansions` **5 项**（见下方 ⚠） |
| 回读校验 | XML 可解析；`version=1.7.4985 rev1269`、`activeMods=14`、`knownExpansions=5` |

⚠ **`knownExpansions` 的第 6 项（`ludeon.rimworld`）不是我删的**：我写入时是 6 项（照抄原文件），17:54:24 有一次**我没参与的写入**把它去掉了。
游戏自己在 17:50:46 写出的也是 5 项（只有 5 个 DLC，不含 Core），**与现在一致**，所以我**没有改回 6 项**——保留与游戏同形状更安全。若父代理要求严格还原原文件，把 `<li>ludeon.rimworld</li>` 加回 `knownExpansions` 即可（一行）。

`1.7.4985 rev1269` 的三重依据：①`Version.txt` 原文；②`RimWorld.dll` / `RimWorld.exe` FileVersion `1.7.4985.1269`；③**游戏自己在 17:50:46 写过一份同值 ModsConfig** + 日志 158 行自报「we are running 1.7.4985 rev1269」。

### 4.2 中间发生的事（不是我干的，但必须记下来）

17:50:41 另一个代理用 `RimWorld.exe` 起了 profile `rhah`；**17:50:46 游戏把本 profile 的 ModsConfig 重写成**：`<version>1.7.4985 rev1269</version>` + `activeMods` 只剩 `ludeon.rimworld` + `knownExpansions` 只剩 5 个 DLC（少了 `ludeon.rimworld`）。

- 原因：日志 158 行 `Mods config data is from version 1.6.4871 rev591 while we are running 1.7.4985 rev1269. Resetting.` —— **版本串不一致就重置整个 mod 列表**。
- 我**按本任务基线把 14 项原样恢复**（不增不减），并把游戏写的那份留证：
  `runs\rimworld-1.7-mod-adapt-20261024\ModsConfig.rhah.as-written-by-1.7-game-175046.xml`
  —— 若父代理认为应保留「只有 Core」的状态，把它拷回去即可（一条 `Copy-Item`，无需改代码）。
- **给下一次启动的提醒**：只要版本串对上了，这个"重置"就不会再发生；反之**任何** 1.7 启动都会再次清空 14 项。

---

## 五、第三方策略建议（**不改别人的 mod**，只写选项与代价）

### 5.1 三种可选路径

| 策略 | 怎么做 | 代价 / 风险 |
|-|-|-|
| ① **等作者更新**（默认推荐） | 什么都不改；`brrainz.rimbridgeserver` 等作者发 1.7 版 | 0 侵入、可复现；但**自动化测试在等到之前完全不可用**（桥接是全部工具面的前提）。判断"作者是否已发"要联网看 GitHub Releases，本机无法判断 |
| ② **临时改它 `About.xml` 的 `supportedVersions`** | 给 `Mods\RimBridgeServer\About\About.xml` 加 `<li>1.7</li>`，并在 `LoadFolders.xml` 补 `<v1.7><li>1.6</li></v1.7>`（指向它现有的 `1.6\Assemblies`） | **侵入第三方**：Steam 校验/重装、作者更新都会**覆盖**；而且**对 RimBridgeServer 无效**——它的阻塞在二进制（§5.2），改声明只会把"加载不到"变成"加载后报错"，把清晰故障变成模糊故障 |
| ③ **游戏/工具侧的"忽略版本检查"开关** | **RimWorld 没有全局的忽略版本开关**（实测：`Mods` 页只有过滤器 `All incompatible mods`，语言键 `Menus_Main.xml:122`；单 mod 行提示 `ModNotMadeForThisVersion` / `…Short`，是**警告不是开关**）。**真正存在**的三个"绕过"：①**`ModsConfig.<version>` 与游戏一致**（本轮就是这么修的，这才是让 14 项不被重置的正解）；②工坊里有第三方去除版本警告的 mod（`Mlie.NoVersionWarning`，workshop `2599504692`，本机已下载）——但它自己也是第三方 mod，且只压红字、不产生兼容性；③我们工具侧的 `rimworld/load_game_ready` 支持 `ignoreModCompatibility: true`，**只影响读档，不影响启动时的 mod 声明校验** | ①零代价；②等于又引入一个第三方依赖，且误导人以为兼容；③覆盖不了启动阶段 |

### 5.2 明确指认：`RimBridgeServer` 缺的是**源码层 1.7 兼容**，不是声明

实测 `Mods\RimBridgeServer\1.6\Assemblies\RimBridgeServer.dll`（1,305,600 B，modVersion 2.1.1）的 AssemblyRef：

```
Assembly-CSharp, Version=1.6.9655.19392
0Harmony, Version=2.4.2.0
UnityEngine.IMGUIModule / UnityEngine.CoreModule / UnityEngine.TextRenderingModule
UnityEngine.InputLegacyModule / UnityEngine.ImageConversionModule
```

1.7 安装里**一个 `UnityEngine*.dll` 都没有**（只剩 `UnityStubs.dll`，`UnityEngine*=UnityStubs` 重定向）。桥接要驱动的正是 IMGUI/贴图/输入这些模块 —— 这些面在 `UnityStubs` 里能否等价工作**没有证据**，且它引用的 `Assembly-CSharp` 是 1.6 build。

⇒ **改 `About.xml` 声明不能掩盖这件事**：这条路必须等作者按 1.7（.NET 10 + Waylith）出二进制。同一判断也适用于 `Mods\` 里其它自带 DLL 的 mod（Bike Probe / Food Control / 语音副官 等，不在本 14 项内，未纳入核对表）。

### 5.3 其余第三方：文件层都已就绪，无需动它们

`harmony` / `HAR` / `NewRatkinPlus` / `IrisMenus` / `Oxygendsl` / `Ratkin: Hunger and Havoc` 六项**都自带 `<v1.7>` 与 1.7 编译产物**（时间戳 10/15–10/24），不需要任何第三方侵入手段。

### 5.4 5 个官方 DLC 的 `1.6` 声明（需 Ludeon 侧，不是我们能修的）

`Data\{Royalty,Ideology,Biotech,Anomaly,Odyssey}\About\About.xml` 的 `supportedVersions` 仍只有 `1.6`，文件 mtime 均为 **2025-11-01**（本次 1.7 更新没碰它们；只有 `Data\Core\About` 是 2026-10-11）。
后果（**推测**，本次日志里没有出现相应红字，因为 mod 列表被重置成只剩 Core，DLC 根本没进加载流程）：1.7 下这 5 个 DLC 应被标为"不是为这个版本做的"。
建议：这类判定留给起游戏后看 mod 列表红字；**不要**去改 `Data\` 下的 Ludeon 文件（升级即失效，且会让"官方 DLC 是否兼容"这个事实变得不可复核）。

---

## 六、未验证 / 没做的事

1. **本轮没有启动游戏**（按派活要求）。17:50 那次启动不是本代理发起的；我只读了它的日志/产物，且该会话在 17:54:03 被关闭，**没有跑到桥接启动**。
2. **未验证 `DSH_GameLink` 改动后的真实效果**：DLL 是否被 1.7 接受、`StaticConstructorOnStartup` 是否执行、Harmony 2.4.3→3.0 的补丁是否挂上、`live` 事件是否真的落盘 —— **全部未验证**。
3. **未验证"不需要重编"这个判断**（§3.3 只是字符级/元数据级证据 + 重定向配置，不是加载实测）。
4. **未验证** 1.7 下 5 个官方 DLC 被标版本不兼容后，游戏是否仍允许勾选它们（没起游戏，也没看 mod 列表 UI）。
5. **未验证** 恢复后的 14 项在 1.7 下能否留在 `activeMods`（按日志 158 行的机制**推测能**：版本串一致就不再 `Resetting`）。
6. **未验证** 工坊 mod 是否有可用更新（没联网核对 6 个已支持 1.7 的 mod 是否还有更新版；也没核对 RimBridgeServer 作者是否已发 1.7 build）。
7. **未做 1.6 vs 1.7 的 Def 逐文件 diff**（本机已无 1.6 副本）。
8. 未改：玩家存档、Steam 分支、第三方 mod 任何文件、游戏安装目录、`src\*` 代码；**未 commit**。

---

## 七、本轮产物

| 路径 | 内容 |
|-|-|
| `docs\RimWorld-1.7模组侧适配-20261024.md` | 本报告 |
| `runs\rimworld-1.7-mod-adapt-20261024\ModsConfig.rhah.as-written-by-1.7-game-175046.xml` | 17:50:46 游戏重置 mod 列表后的 ModsConfig 原文（留证 + 回滚用） |
| `mods\DSH_GameLink\About\About.xml`（改） | `+<li>1.7</li>` |
| `mods\DSH_GameLink\LoadFolders.xml`（改） | `+<v1.7><li>/</li></v1.7>` |
| `profiles\rhah\Config\ModsConfig.xml`（改） | `<version>` → `1.7.4985 rev1269`；`activeMods` 14 项按原样恢复；`knownExpansions` 现 5 项（第 6 项由别的代理在 17:54:24 去掉，与游戏自写形状一致，未改回——见 §4.1 ⚠） |
