# RimWorld 1.7「模组设置页没有分页」只读诊断（2026-10-25）

对象：`profile=rhah` 的 **1.7.4985 rev1269（Waylith）** 运行中实例（PID 14904，桥接 127.0.0.1:5174，`status --profile rhah` 的 `bridgeResolution.ok=true`）。
方式：**只读**——读文件、读 `logs/Player.log`、只读桥接调用（`list_mods` / `get_mod_configuration_status` / `list_mod_settings_surfaces`）、反编译既有 DLL 与既有 `runs\tmp\ilspy17\full\Assembly-CSharp.decompiled.cs`。未启动/停止游戏、未点 UI、未改任何文件（本报告除外）、未发动作命令。

## 结论（3 行）

1. **分页没了的直接原因**：RHAH 的 IrisMenus 适配在启动时走进了「未激活」分支，17 个分页一次都没注册，RHAH 的设置退回原版单页。
   决定性证据：`Player.log` `2026-10-25 09:49:41.677 [RHAH] IrisMenus is not active. Menu pages were not registered.`（调用栈指向 `HungerAndHavoc.Pawn.Compat.RHAH_IrisMenusCompat.TryRegister`），且整份日志**没有** `[RHAH] IrisMenus … pages registered.`。
2. **不是没装、不是旧构建、也不是 1.6/1.7 API 变了**：IrisMenus 已启用并加载（bridge：`packageId=nanaloveyuki.irismenus`，`loadedInSession=true`，`activeLoadOrder=13`，依赖全满足，无 ordering issue / 无 restartRequired）。真正卡住的是 `ModsConfig.xml` 里这一条写成了**混合大小写** `Nanaloveyuki.IrisMenus`：1.7 的 `ModsConfig.IsActive` 用**小写**去查一个**大小写敏感**的 `HashSet`，而该集合里存的是 XML 原文（未小写），于是 `ModMetaData.Active == false` → `ModLister.GetActiveModWithIdentifier("Nanaloveyuki.IrisMenus", false)` 返回 null。**实测+代码证死**，见第 4 节。
3. **归类：环境/配置（我们写 ModsConfig 的写法）× 原版大小写敏感的实现细节**；同时是**1.6 就已经存在**的问题（1.6 会话日志同一条消息）。**不重启无法在运行中直接观测** `ModMetaData.Active`（桥接的 `enabled` 取的是 `ActiveModsInLoadOrder`，绕开了 `Active`），但只读判据（第 7 节）足以定案；重启后验证的具体动作**本次未做**。

## 1. activeMods 检查（实测）

`profiles\rhah\Config\ModsConfig.xml`：`<version>1.7.4985 rev1269</version>`，共 16 条 activeMods。
第 18 行 = `<li>Nanaloveyuki.IrisMenus</li>`（**混合大小写**）；第 16、17 行 `erdelf.HumanoidAlienRaces`、`Solaris.RatkinRaceMod` 同样混合大小写；其余（含 `nanaloveyuki.ratkin.hungerandhavoc`）全小写。
该文件 `LastWriteTime = 2026-10-25 00:52:45`，**早于**本实例启动（09:49），即游戏读到的就是这份原文，启动后没被重写。
底座 `base-mods.json` 的 id 全是小写；`src\cli.js:775-781`（`applyModsConfig`）把 id **原样**写回 `activeMods`，不做小写化——混合大小写是从别处（历史手改/旧清单）带进来的，不是这次的底座补齐。

## 2. IrisMenus 的 About / LoadFolders（实测）

安装位置：`D:\SteamLibrary\steamapps\workshop\content\294100\3798502474`（Steam 工坊，`PublishedFileId.txt` 同目录）。
`About\About.xml`：`packageId=Nanaloveyuki.IrisMenus`、`modVersion=1.1.2`、`supportedVersions=[1.6, 1.7]`、`modDependencies=[brrainz.harmony]`（带 steamWorkshopUrl）。
`LoadFolders.xml`：`<v1.6>`→`1.6`；`<v1.7>`→`1.7, Common`。
仓库 `RimworldMods\iris_menus\About\About.xml` 与 `LoadFolders.xml` 与工坊版**逐字一致**（同 1.1.2 / 同 1.6+1.7）。
→ **「装的是不是旧构建」= 否**：工坊这份就是声明支持 1.7 的 1.1.2。

## 3. 运行时加载状态（只读桥接，实测）

`rimworld/list_mods`:
```
packageId=nanaloveyuki.irismenus  packageIdPlayerFacing=Nanaloveyuki.IrisMenus
name=IrisMenus  version=1.1.2  rootDir=...\workshop\content\294100\3798502474  source=SteamWorkshop
enabled=true  loadedInSession=true  activeLoadOrder=13  loadedSessionOrder=13
versionCompatible=true  hasOrderingIssues=false  unsatisfiedDependencies=[]
```
（RHAH 自身：order=15，`version=1.1.4`，`loadedInSession=true`。）
`rimworld/get_mod_configuration_status`：`orderingIssueCount=0`、`sessionMismatchCount=0`、`restartRequired=false`、`restartReasons=[]`。
`rimworld/list_mod_settings_surfaces`：`handleType=IrisMenus.IrisMenusMod`、`settingsType=IrisMenus.MenuSettings`、`settingsCategory=IrisMenus`、`hasSettingsWindow=true`（**IrisMenus 本体活着、设置窗在**）；RHAH 只有**一个** surface：`settingsType=HungerAndHavoc.Core.RHAH_Settings`、`settingsCategory=Ratkin: Hunger and Havoc`。
`runs\live\rhah.ndjson`：`IrisMenus` 命中 **0** 条（该文件 mtime 03:14，早于本实例启动，未深究）。

## 4. RHAH 怎么判定 IrisMenus（实测源码 + 1.7 反编译）

**是运行时探测，不是编译期宏**（宏只决定「这段代码编不编进去」）：

| 位置 | 内容 |
|---|---|
| `RimworldMods\ratkin_hunger_and_havoc\Source\HungerAndHavoc.csproj:87` | `IrisMenusDll = $(RimWorldDir)/Mods/IrisMenus/$(RimWorldVersion)/Assemblies/IrisMenus.dll` |
| 同上 `:89-92` | 该 DLL **不存在**时 `Compile Remove` 掉 `RHAH_IrisMenusCompat.cs`、`RHAH_IrisMenusWidgets.cs`（整段功能消失） |
| 同上 `:93-100` | 存在时加 `ReferencePath`（别名 `iris`）并追加 `RHAH_IRISMENUS` |
| `Source\Pawn\Compat\RHAH_IrisMenusCompat.cs:22` | `PackageId = "Nanaloveyuki.IrisMenus"` |
| 同上 `:23` | `SupportedVersion = "1.6"`（要求对方**声明支持 1.6**） |
| 同上 `:34` | `ModLister.GetActiveModWithIdentifier(PackageId, false)` ← **判的是「在 activated 列表且 Active」** |
| 同上 `:35-38` | `null` ⇒ `Log.Message("[RHAH] IrisMenus is not active. Menu pages were not registered."); return;` |
| 同上 `:42-49` | Active 了但 supportedVersions 不含 1.6 ⇒ `Log.Warning("... does not declare RimWorld 1.6 support ...")` 并 return |
| 同上 `:51-61` | `new RHAH_IrisMenusPages(meta.ModVersion).Register(owner)`，成功印 `pages registered`，异常被 `catch` 成 `Log.Error("... registration failed ...")` |
| 同上 `:124-146` | `Register` 里 `RegisterPage(...)` ×17 + `MenuRegistry.RegisterSubItem("settlement-progress", ...)` |
| 同上 `:149-159` | 每个分页 = `MenuRegistry.RegisterSubItem` + `MenuRegistry.RegisterSearchProvider` |

**三种失败的回退都是同一个用户可见结果**：不注册 → IrisMenus 窗口里没有 RHAH 分页 → RHAH 只剩原版 `Dialog_ModSettings` 单页（就是现在看到的样子）。区别只在日志级别：`not active`=Message、`does not declare`=Warning、注册异常=Error。本次是**第一种**。

**为什么能断定是 `Active==false` 而不是别的**（1.7 反编译 `runs\tmp\ilspy17\full\Assembly-CSharp.decompiled.cs`，实测）：

- `:97639-97653` `GetActiveModWithIdentifier(id, ignorePostfix)`：先按 `modsByPackageId`/`modsByPackageIdIgnorePostfix` 取 value，再 `if (item.Active) return item;` 否则返回 null。
- `:99018-99034` `ActiveModsInLoadOrder` 用 `ModLister.GetModWithIdentifier(data.activeMods[i])` 解析——**和上面同一个字典**。既然 IrisMenus 被加载进了 `runningMods`（bridge `loadedInSession=true`），说明**字典里有这条**。⇒ 返回 null 只能是 `item.Active == false`。
- `:98268-98272` `ModMetaData.Active => ModsConfig.IsActive(this)`；`:99383-99386` → `IsActive(mod.PackageId)`；`:99428-99431` `IsActive(string id) => activeModsHashSet.Contains(id.ToLower())`（`HashSet<string>` 默认**序数比较**）。
- `:99673-99679` `RecacheActiveMods()`：`foreach (string activeMod in data.activeMods) activeModsHashSet.Add(activeMod);` —— **原文加入，不小写**。
- `:98984-98989` `ModsConfigData.activeMods` 是普通 `List<string>`；静态构造 `:99095-99111` 只在「该 id 解析不到 mod」时才回写规范化值；`Nanaloveyuki.IrisMenus` 能解析到，所以**保持混合大小写**。
- ⇒ `IsActive("nanaloveyuki.irismenus")` 在集合里查 `"Nanaloveyuki.IrisMenus"` → **false** → `Active=false` → RHAH 的 `GetActiveModWithIdentifier` 返回 null → 打出「not active」。

**三者不一致时会怎样**（回答原问题）：宏定义了（代码在 `RHAH_IRISMENUS` 下编进去了，实测给 DLL 做了 UTF-16 字节搜索，`"IrisMenus is not active"` / `"pages registered"` / `"registration failed"` / `"Nanaloveyuki.IrisMenus"` 都在）、类型也存在（IrisMenus 程序集加载成功，见 `list_mod_settings_surfaces` 的 `IrisMenus.IrisMenusMod`），但**运行时探测说 inactive** ⇒ 走 Message 分支静默回退到单页，**不抛异常、不进 catch、不影响其它功能**。

## 5. 编译期 DLL 与运行时 DLL 比对（实测）

| | 编译期引用 `runs\tmp\iris17\IrisMenus.dll` | 运行时加载（工坊 1.7）`...\3798502474\1.7\Assemblies\IrisMenus.dll` | 工坊 1.6 `...\1.6\Assemblies\IrisMenus.dll` |
|---|---|---|---|
| AssemblyVersion | 1.1.2.0 | 1.1.2.0 | 1.1.1.0 |
| PublicKeyToken | null（未签名） | null（未签名） | null（未签名） |
| SHA256 | `D2A44912E1714A8331EBFE80309FF206BCFA559F84892B69FC7F8506DF9FE072` | `8078A5FE2BA108D63404ADABB610479E722644427A26CA33CE88F58B94357A6F` | `8D445D6D018046B38D45F1025D4B7CEF832D6C1B6E61C05C2632B670BB71F3F2` |
| 大小 / mtime | 69632 / 2026-10-25 02:52 | 69632 / 2026-10-24 08:40 | 70144 / 2026-10-13 20:42 |

- `runs\tmp\iris17\IrisMenus.dll` 与仓库 `RimworldMods\iris_menus\Source\obj\Release\IrisMenus.dll` **SHA 完全相同**（就是那份编译产物）。
- 编译期 1.1.2 与运行时 1.1.2：**同 AssemblyVersion、同 58 个类型（类型名集合 diff 0 行）、`MenuRegistry` / `SettingsAdapter` 公开成员 diff 0 行**；仅**字节不同**（不同时刻的两次编译/打包）。
- 影响判断：两者**未签名且版本号相同**，运行时绑定不会因版本失败；由于公开 API 面无差异，也**不会**出现 `MissingMethodException` 之类。若真出现 API 漂移，`RHAH_IrisMenusCompat.cs:57-61` 会 `catch` 并 `Log.Error("... registration failed")`，用户仍然看到单页——**静默回退，不崩**。⇒ 本次 DLL 字节差异**不是**本次故障的原因，只是潜在风险（编译期验的是一份、跑的是另一份）。

## 6. 1.6 / 1.7 的 IrisMenus 差异（实测）

- 类型集合：1.6（1.1.1）58 个 vs 1.7（1.1.2）58 个。**只在 1.6**：`IrisMenus.FrostPipeline`（+ 两个编译器内嵌属性）；**只在 1.7**：`IrisMenus.FrostSceneBinding`(+`SceneTexture`/`BorrowedTexture`)。差异集中在 Frost 背景渲染层。
- **分页相关 API 完全没变**：`MenuRegistry` 的 17 个公开静态方法在 1.6/1.7 两份 DLL 上反编译文本 diff = **0 行**；RHAH 用到的 `RegisterSubItem(Mod,string,Func<string>,Action<Rect>,Action)`、`RegisterSearchProvider(...)` 两份都在。
- 目录：1.6 有 `Assemblies/AssetBundles/Defs/Languages`；1.7 只有 `Assemblies/Rendering/Frost17.frag.glsl`，Defs/Languages 由 `Common` 提供（`LoadFolders.xml` 的 `<v1.7>` 同时挂 `1.7` 和 `Common`）。**不是「1.7 版分页未完成」**。
- 结论：**「1.6 vs 1.7 的 IrisMenus 差异」解释不了分页消失**；而且 RHAH 的这条适配**两种版本都失败**——1.6 会话日志 `profiles\rhah\Player-2026-10-24T09-38-08-362Z.log`（`:27` `RimWorld 1.6.4871 rev591`，`:72` 同一条 `[RHAH] IrisMenus is not active…`，`:101` 载入清单里有 `- Nanaloveyuki.IrisMenus`）。

## 7. 只读验证判据（不重启能做到哪一步）

**已经做完的只读判据（可复现）**：
1. `logs/Player.log` 里 `TryRegister` 打的是 `not active`（Message 分支），不是 `does not declare 1.6`（Warning 分支），也不是 `registration failed`（Error 分支）⇒ 只可能是 `meta == null`。
2. `list_mods` 证明 IrisMenus 在 `ActiveModsInLoadOrder` 里（`loadedInSession=true`/order 13），而该列表与 `GetActiveModWithIdentifier` 共用同一字典 ⇒ 字典不缺键 ⇒ 只能是 `ModMetaData.Active == false`。
3. `ModsConfig.xml` 第 18 行大小写 + 1.7 `IsActive` 的大小写敏感实现 ⇒ `Active=false` 的机制成立。
4. `list_mod_settings_surfaces` 证明 IrisMenus 本体与 RHAH 设置对象都活着，只是 RHAH 没进 Iris 的分页体系。

**运行中无法直接读到的**：`ModMetaData.Active` 的真值。桥接的 `enabled` 字段取的是 `ModsConfig.ActiveModsInLoadOrder`（反编译 `RimBridgeServer.dll` 的 `RimWorldModConfiguration` 可见：`source = ModsConfig.ActiveModsInLoadOrder`），**绕开了 `Active`**，所以 `enabled=true` 不能拿来否证。桥接也没有通用反射/eval 工具（`rimbridge/run_script` 只跑受限能力脚本）。

**人在界面上可以做的只读观察（推测性判据，未做）**：原版「选项 → 模组」列表里 IrisMenus 的勾选态；若显示为未勾选却已在运行，就与 `Active=false` 一致。

**必须重启才能做的（本次不做）**：
- 把 `ModsConfig.xml` 里 `Nanaloveyuki.IrisMenus` / `Solaris.RatkinRaceMod` / `erdelf.HumanoidAlienRaces` 改成小写后重启，看是否出现 `[RHAH] IrisMenus 1.1.2 pages registered.`（这是唯一 100% 的判决性实验）。
- 或改 RHAH 侧：`GetActiveModWithIdentifier(..., ignorePostfix: true)` 不解决 `Active` 问题，应改判据——用 `ModLister.GetModWithIdentifier(PackageId, ignorePostfix: true) != null` 或 `ModsConfig.ActiveModsInLoadOrder` 里查 `PackageId`，而不是依赖 `ModMetaData.Active`。
- 顺带风险：`Solaris.RatkinRaceMod`、`erdelf.HumanoidAlienRaces` 同样混合大小写，任何按 `ModMetaData.Active` / `GetActiveModWithIdentifier` 判它们的代码也会被判成「未启用」（同一机制，未逐条验证影响面）。

## 未验证 / 未找到

- **1.6 的 `ModsConfig.IsActive` 实现**：本机只有 1.7 的 `Assembly-CSharp.dll`（`D:\SteamLibrary\steamapps\common\RimWorld\Assembly-CSharp.dll`，15,632,896 B，2026-10-24 17:46），没有 1.6 程序集可反编译。1.6 同样失败只有**日志证据**（同一条消息），机制是**推测**其与 1.7 相同。
- **运行中 `ModMetaData.Active` 的实际取值**：没有只读 eval 工具，未直接读出（只能代码推）。
- **两份 1.1.2 DLL 的字节差异来源**：只确认「同版本、同类型集、同 API」，没有逐行反编译比对实现差异，也未确认是否只是重编译。
- **混合大小写三条是谁写进去的**：`profiles/rhah` 无 `tested-mods.json`，`profiles/` 被 `.gitignore` 忽略（`git check-ignore` 命中 `.gitignore:2`），无历史可查。
- **桥接监听端口 PID ↔ `-savedatafolder` 的原始命令行**未再单独核对：`status --profile rhah` 已给出 `bridgeResolution.ok=true`（PID 14904 + 端口 5174 + token 与记录一致），并且 `Player.log:8` 的命令行里 `-savedatafolder=…\profiles\rhah` 与记录一致。
