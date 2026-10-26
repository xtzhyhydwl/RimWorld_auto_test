# 怎么让 RimWorld 加载「本地从源码重建的 RHAH」，而不动玩家的安装/工坊/存档

> **本文是调研，不是执行记录。** 本轮**没有**建 junction、**没有**复制/覆盖任何文件、**没有**改任何 `ModsConfig.xml`、**没有**启动游戏、**没有**碰玩家存档。
> 全部命令都是「将来照着敲」的待执行方案。
>
> 标注约定：**【实测】** = 本轮读到/跑出的证据（文件内容、命令输出、`Assembly-CSharp.dll` 的 IL）；
> **【常识】** = 原版机制常识，本轮未在本机验证；**【推测】** = 推理。

---

## 0. 结论与推荐（先看这 5 行）

1. **同 packageId 两份并存不会崩**：`Mods\` 先扫到的（本地那份）**保留原 id**，工坊那份被自动加上 `_steam` 后缀，两份并存；两者**同一来源**（都在 `Mods\` 或都在工坊）时才报 `Tried loading mod with the same packageId multiple times` 并**丢弃后来者**。【实测 IL】`Verse.ModLister.TryAddMod`
2. **只有列在 `ModsConfig.xml` 的 `<activeMods>` 里的 mod 才会被加载**（连程序集都不加载）【实测 IL】`LoadedModManager.InitializeMods`；所以「工坊那份不列出来就不加载」这句成立 —— **但救不了 junction 方案**。
3. **junction 法（同 packageId）会伤到玩家**：玩家的 `Config/ModsConfig.xml` **342 项里就有 `nanaloveyuki.ratkin.hungerandhavoc`**【实测】；一旦 `Mods\` 里出现同 id 的本地副本，那个 id 就解析到**本地重建版**，玩家下一局悄悄换成测试构建（存档里的 modIds 不变 ⇒ 连提示都没有）。**不可用**。
4. **推荐：改 packageId 法**（`…hungerandhavoc.test`）+ **junction 指到 `profiles\rhah\Mods\RHAH_Local`**。玩家侧的 id 仍唯一指向工坊，隔离干净；代价只有两个已知的自我引用（菜单版本号回退、save-cleanup 只清 `.test` 那一条），无崩溃风险。
5. **RimWorld 1.6 没有「指定 mod 目录」的启动参数**【实测】：`Assembly-CSharp.dll` 里与目录覆盖相关的字符串只有 `savedatafolder`，`ModsFolderPath` 无任何覆盖入口。可选路径只有 `RimWorld\Data`、`RimWorld\Mods`、工坊三个。

---

## 1. 事实依据 A：mod 发现、同 id 去重、激活判定（全部【实测】IL）

来源：`D:\SteamLibrary\steamapps\common\RimWorld\RimWorldWin64_Data\Managed\Assembly-CSharp.dll`（版本 **1.6.9676.17735**；`Version.txt` = `1.6.4871 rev590`），工具 `tools\asmdump\IlDump.exe`（只读反汇编，不执行游戏代码）。

### A1 扫描顺序与扫描根（`Verse.ModLister.RebuildModList`）

1. `GenFilePaths.OfficialModsFolderPath`（= `RimWorld\Data`）→ `new ModMetaData(path, true)`
2. `GenFilePaths.ModsFolderPath`（= `RimWorld\Mods`）→ `new ModMetaData(path, false)`
3. `Verse.Steam.WorkshopItems.AllSubscribedItems`（= 工坊 `294100\<id>`）→ `new ModMetaData(WorkshopItem)`

每个候选都过 `ModLister.TryAddMod`。**没有 About.xml 的目录照样会被 `new ModMetaData`**，由内部 `Init` 判无效（`Init` 细节本轮没读，标【未验证】）。
扫完调 `ModsConfig.DeactivateNotInstalledMods`：倒序遍历 `<activeMods>`，按 id 找不到就再试「去掉 `_steam` 后缀」找一次，**还找不到就把这一项从 `activeMods` 里删掉**（并 `RecacheActiveMods`）【实测 IL】。
最后只有 `Prefs.LogVerbose == true` 才把整段扫描日志 `Log.Message` 出去。

### A2 同 packageId 的三条分支（`Verse.ModLister.TryAddMod`）

```
if (GetModWithIdentifier(mod.PackageId, ignorePostfix:false) is existing) {
    if (existing.RootDir != mod.RootDir) {
        if (mod.OnSteamWorkshop != existing.OnSteamWorkshop) {
            // 把「工坊那份」的 appendPackageIdSteamPostfix 置 true，然后递归 TryAddMod
        } else {
            Log.Error("Tried loading mod with the same packageId multiple times: " + PackageIdPlayerFacing
                    + ". Ignoring the duplicates.\n" + 新路径 + "\n" + 旧路径);
            return false;                      // 后来者被丢弃
        }
    } else return false;                       // 同一个目录被扫两次，静默忽略
}
```

- `ModMetaData.PackageId` = `packageIdLowerCase + (appendPackageIdSteamPostfix ? "_steam" : "")`；`SteamModPostfix` 的字面量在本程序集里就是 `_steam`【实测字符串】。
- `OnSteamWorkshop` = `source == 3`；`Mods\` 目录构造出来是 `source = 2`，工坊是 `source = 3`。
- **方向要注意**：`Mods\` 先被扫到 ⇒ 本地那份**保留原名**；工坊那份进来时 `this.OnSteamWorkshop = true` ⇒ **postfix 打在工坊那份身上**，它变成 `<id>_steam` 并存。
  反过来的顺序（工坊先进）会走到上面那条 `Log.Error` 分支把本地那份丢掉 —— **所以「扫描顺序」是这条机制的承重墙**，而顺序是写死在代码里的（Data → Mods → 工坊）。
- 佐证：本 mod 自己的 `Guard\Source\DuplicateModGuard.cs:19-23` 就把 `conflictId + "_steam"` 当成同义词来判重 ⇒ 作者按同一套机制写代码【实测源码】。

### A3 谁被加载（`Verse.ModsConfig.ActiveModsInLoadOrder` + `Verse.LoadedModManager.InitializeMods`）

- `ActiveModsInLoadOrder` = 严格按 `<activeMods>` 的顺序，逐项 `ModLister.GetModWithIdentifier(id, false)`，
  而这个方法就是 `modsByPackageId[id.ToLowerInvariant().Trim()].ElementAtOrDefault(0)` ⇒ **同 id 多份时永远取列表第 0 个**（也就是先扫到的那份）。
- `InitializeMods` 只遍历 `ActiveModsInLoadOrder`，为每项建一个 `ModContentPack` 并塞进 `LoadedModManager.runningMods`。
  **不在 activeMods 里的 mod，一个 Def、一个程序集都不会加载。**

> ⇒ 直接回答提问里的两个子问题：
> * 「工坊那份能不能在 `ModsConfig.xml` 里不被列出（就不加载）？」**能**。激活完全由 `activeMods` 决定，不是「工坊目录里有就加载」。
> * 「两份同 id 并存时 RimWorld 会怎样、按什么顺序选？」见 A2：不报错、不崩；`Mods\` 那份拿原名（并赢），工坊那份变 `_steam`（默认不激活）。

---

## 2. 事实依据 B：没有任何「指定 mod 目录」的启动参数（【实测】）

- 全程序集的 UTF-16 用户字符串里，跟「目录」有关的命令行字面量只有 **`savedatafolder`**（以及 `quicktest`、`devMode`）。
  `GenFilePaths.get_SaveDataFolderPath` 的 IL 里只有一处 `GenCommandLine.TryGetCommandLineArg("savedatafolder", out …)`，命中后 `Log.Message("Save data folder overridden to …")`。
- `GenFilePaths.get_ModsFolderPath` = `GetOrCreateModsFolder("Mods")` = `Application.dataPath` 的父目录 + `Mods`；`OfficialModsFolderPath` = 同函数 + `Data`。**两者都没有命令行/配置覆盖入口。**
- `GenFilePaths.get_ConfigFolderPath` = `<saveDataFolder>\Config`，`get_ModsConfigFilePath` = 那之下的 `ModsConfig.xml` ⇒
  **`-savedatafolder` 天然让每个 profile 有独立的 `ModsConfig.xml` / `Prefs.xml` / `Saves`**，本工程 `src\launcher.js:402` 传的就是它（`-savedatafolder=<WS>\profiles\<p>`），`src\launcher.js:403-404` 还把日志用 `-logFile` 指到 `profiles\<p>\Player.log`。
- Unity 侧参数（`-screen-width/-height/-monitor/-logFile`）与 mod 目录无关；`--doorstop-enabled false` 是 Doorstop 的开关。

**结论：想换 mod 来源，只有三条路 —— 改 `RimWorld\Mods`、改工坊目录、改 `RimWorld\Data`（更糟）。**

---

## 3. 事实依据 C：RHAH 自己有没有按 packageId 自查（【实测】grep 源码）

源码根 `RimworldMods\ratkin_hunger_and_havoc\`（`Source\` 下 79 处 packageId 相关命中，逐条看过）：

| 位置 | 内容 | 改 packageId 后会不会坏 |
| - | - | - |
| `Source\Core\RHAH_Runtime.cs:8-9` | `PackageId` 常量 + `HarmonyId = PackageId` | **不会**。Harmony 的 id 只是补丁集的名字，不要求等于 packageId；两份不会同时加载 |
| `Source\Pawn\Compat\RHAH_IrisMenusCompat.cs:3376-3380` | `ContentFinderVersion()` = `ModLister.GetActiveModWithIdentifier("nanaloveyuki.ratkin.hungerandhavoc")`，null 时回退 `"1.0.0"` | **唯一真正的自我引用**。改名后 base id 没有激活项 ⇒ 菜单里版本显示 `1.0.0`。**纯显示** |
| `Source\EventMgr\RHAH_EventChainProvider.cs:16` | `ProviderKey = "nanaloveyuki.ratkin.hungerandhavoc"`（存档里的 `providerId`） | **不会**。这是存档格式键，与 packageId 无关，新旧两份**互相认**同一份事件链状态 |
| `Source\Core\RHAH_SaveExport.cs:101-102` | `plan.PackageId = LoadedModManager.GetMod<RHAH_Mod>().Content.PackageId`（运行时实际值） | 不会崩。只是「导出/清理」时删的是**当前这份**的 id |
| `Source\Core\RHAH_SaveCleanup.cs:671-691 StripPackage` | 按 `plan.PackageId` 删存档 `<meta><modIds>` 里那一条 | 功能差异：改名版写的存档里是 `.test`，用改名版清理就清 `.test`；工坊那条不会被它清 |
| `Source\...\RHAH_ApparelPolicy.cs`、`RHAH_DemandFood.cs`、`RHAH_LeashBridge.cs`、`RHAH_CombatExtendedLoadout.cs` | 比较的是**别的** mod id（`ludeon.rimworld`、`solaris.ratkinracemod`、`CETeam.CombatExtended`、`Nanaloveyuki.IrisMenus`…） | 无关 |
| `Guard\Source\DuplicateModGuard.cs` | 只查 3 个「鼠灾」旧 mod（且把 `<id>_steam` 当同义词） | 无关；**不检查自身重复** |
| `About\About.xml` | `incompatibleWith` / `loadAfter` / `modDependencies` 引用的全是别人的 id；`<packageId>` 是根节点直属；`<name>` 无自我引用 | 改 `<packageId>` 一行即可 |
| `LoadFolders.xml`（根） | `<v1.6>` = `Guard` + 3 条 `IfModActive="nanaloveyuki.oxygendsl" IfModNotActive=<3 个旧鼠灾 mod>` 的 `/`、`1.6`、`Biotech` | **不引用自己的 id** ⇒ 改 packageId 不影响加载目录选择 |

其它核对【实测】：

- 仓库 `About\About.xml` 是**无 BOM 的合法 UTF-8**（首字节 `3C 3F 78 6D`）。⚠ 本机 shell 的 `Get-Content` 会按 ANSI 解出乱码 —— **改这个文件必须显式按 UTF-8 读写**，否则中文 `<name>`/`<description>` 全毁。
- 仓库 `LoadFolders.xml` 只此一份（根目录），`1.6\` 下没有第二个。
- 两份产物：工坊 `1.6\Assemblies\HungerAndHavoc.dll` **848,384 B / 2026-10-16 22:15:02 / SHA256 `B987BF67…`**；仓库重建 **865,280 B / 2026-10-24 16:56:33 / SHA256 `D38461F0…`**（与 `docs\RHAH-1.6重建报告-20261024.md:112` 一致）。
- 新 DLL 独有 `RHAH_ArmedDemand.Owner/Refresh/Finish/Tick`（方法表 12 → 20，`docs\RHAH-1.6重建报告-20261024.md:126-131`【实测】）—— 这是「加载的是重建版」的**行为层**判据来源。
- 仓库 `Guard\Assemblies\HungerAndHavocGuard.dll` 是 **2026-10-24 08:48** 的（本次重建没重编 Guard，两份都是 6,656 B）。

---

## 4. 事实依据 D：玩家现状与本工程既有实践（【实测】）

- 玩家全局配置 `C:\Users\MikanseiP\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Config\ModsConfig.xml`
  （13,743 B，最后写入 **2026-10-24 10:00:31**）**342 项，其中就有 `nanaloveyuki.ratkin.hungerandhavoc`** ⇒ 方案 1 会直接改玩家那局加载什么。
- 游戏 `Mods\` 现有 6 个 junction，全部指向工作区：
  `BikeProbe→mods\BikeProbe`、`ColonyRecorder→mods\ColonyRecorder`、`DSH_GameLink→mods\DSH_GameLink`、`FoodControl→mods\FoodControl`、`副官语音→语音副官`、
  **`DSH_AutoTest→profiles\vanilla\Mods\DSH_AutoTest`**（← 「profile 里放 mod 目录、再 junction 进 `Mods\`」的现成先例）。
- `profiles\rhah\Config\ModsConfig.xml` 现有 14 项，第 13 项是 `nanaloveyuki.ratkin.hungerandhavoc`。
- `.gitignore` 忽略 `profiles/`、`runs/`、`tmp/`、`data/`、`.research/` ⇒ **`profiles\rhah\Mods\` 天然不进 git**，适合放部署目录。
- 体积：仓库 RHAH 共 **950 文件 / 449.7 MB**，其中 `Textures` 218.5 MB、`docs` 218.3 MB ⇒ 只复制运行所需部分约 **226 MB**。
- ⚠ skill 里提到的 `scripts\modsconfig.mjs`、`scripts\diag.mjs` 等**已不存在**（`scripts\` 实测 0 文件，见 `docs\工程腐化盘点-20261024.md`）。本工程统一入口只有 `src\cli.js`。**本轮的步骤一律不依赖 `scripts\*`。**

---

## 5. 四个候选方案逐个评估

### 方案 1：junction（或复制）到 `Mods\`，packageId 不变 — ❌ 不可用

**行为（按 A2 推演 + 已用静态模拟器在本机验证解析结果）**：

| 谁 | 结果 |
| - | - |
| `Mods\RHAH_Local_TEST`（我们的重建版，`source=2`） | 拿到原名 `nanaloveyuki.ratkin.hungerandhavoc` |
| 工坊 `294100\3807233439`（`source=3`） | 变 `nanaloveyuki.ratkin.hungerandhavoc_steam`，并存但**不激活** |
| 玩家 `ModsConfig.xml` 里的 `nanaloveyuki.ratkin.hungerandhavoc` | 解析到 **`Mods\RHAH_Local_TEST`**（我们那份） |
| 存档里的 `<modIds>` | 不变（还是原 id）⇒ **玩家完全看不出自己换了构建** |

**为什么否掉**：这不是「玩家不受影响」，而是「**玩家的游戏被悄悄换成了测试构建**」。
而且按 A2 推演，玩家下次启动会看到 mod 列表里 RHAH 出现两行（第二行带 `_steam`）——这个**呈现方式未验证**；一旦他手动勾上第二行，base 与 `_steam` 两份会同时加载 ⇒ 一整套 `RHAH_*` Def 重名红字。

**顺带一个坑**：如果改「复制」而不是 junction，行为**完全一样**（`source` 都是 2）。所以这条路没有「更安全的变体」。

### 方案 2：改 packageId（`…hungerandhavoc.test`）+ junction 到部署目录 — ✅ 推荐

- 玩家侧：base id 只剩工坊一份持有 ⇒ 玩家的 342 项解析不变，加载不变，存档不看任何提示。
- 我们侧：`.test` 是我们唯一的 RHAH，`ModsConfig` 第 13 项写 `.test`，工坊那份不列 ⇒ 不加载（A3）。
- 已知代价（全部来自 §3 的表，**均非崩溃**）：
  1. IrisMenus 菜单里的「自身版本」显示回退成 `1.0.0`（`ContentFinderVersion()` 查 base id 查不到）；
  2. 「导出/清理存档」只清 `.test` 那条 `modIds`；
  3. 改名版写的存档在工坊版里打开会走「mod 不一致」提示（任何测试构建都如此，非本方案特有）；
  4. mod 设置文件按 packageId 命名（`LoadedModManager.GetSettingsFilename` = `<Config>\Mod_{0}_{1}.xml`【实测 IL】）⇒ 测试档拿到一份**全新默认设置**（本来也在 profile 的 Config 里隔离，无害）。
- 需要复制约 226 MB（可只复制运行所需目录），或用嵌套 junction（未验证，见 §9）。

### 方案 3：直接覆盖工坊目录的 DLL — ⚠ 仅作最后手段

风险清单：
1. **直接改玩家正在玩的那一份**；覆盖期间玩家一启动就是测试构建，且他的存档会被写上新字段（旧构建回不去了）。
2. 玩家游戏正在跑时，`HungerAndHavoc.dll` 被进程占用 ⇒ 复制会失败（要先确认没有 RimWorld 在跑）。【常识】
3. Steam 侧：工坊内容由 Steam 维护，**订阅更新 / 取消再订阅 / 重新下载都会覆盖回去**；「验证游戏文件完整性」管的是游戏安装目录、通常不含工坊内容（**未验证**）。
4. 必须自己写备份 + 还原（备份两份 DLL 与 SHA256、还原后核对哈希），还原窗口期内玩家全程在测试版上。
5. 只在「必须验证工坊目录这条加载路径本身」时才值得做。

### 方案 4：其它路径 — ❌ 不存在「指定 mod 目录」的参数

- 无 `-modfolder` / `-mods` / `-moddir` 之类：见 §2 的 IL 与字符串取证。
- `-savedatafolder` 只挪 `Config`/`Saves`/`Screenshots`，**不挪 mod 来源**。
- 本工程 profile 机制本来就支持「profile 独立 `ModsConfig` + 给该 profile 装什么 mod」，但**装什么**仍要靠 junction 把目录接进全局 `Mods\`（先例：`DSH_AutoTest→profiles\vanilla\Mods\DSH_AutoTest`）。也就是说 profile 机制解决「配置/存档隔离」，**不解决「同一 id 两个来源」**。
- 【推测】【未验证】理论上可以写一个「loader 空壳 mod」（packageId 唯一），在 `[StaticConstructorOnStartup]` 里 `Assembly.LoadFrom` 任意路径的 DLL。它能把**程序集**接进来，但那个 mod 的 `Defs`/`LoadFolders`/`Textures` 拿不到（`ModContentPack` 是按 `activeMods` 建的）⇒ 对 RHAH 这种重 XML 的 mod 不成立。

---

## 6. 推荐方案的可执行步骤（命令级，全部**未执行**）

工作区记作 `$WS = E:\2026.7以后的新工程\DSH\RimWorld_auto_test`；所有命令都在 `$WS` 下跑。

### 6.1 前置检查（玩家那局在不在跑、现状如何）

```powershell
cd 'E:\2026.7以后的新工程\DSH\RimWorld_auto_test'
node src/cli.js status --profile rhah          # 本 profile 有没有在跑
Get-Process RimWorldWin64 -ErrorAction SilentlyContinue | Select-Object Id,StartTime
Get-ChildItem 'D:\SteamLibrary\steamapps\common\RimWorld\Mods' | Where-Object LinkType | Select-Object Name,LinkType,@{n='Target';e={$_.Target}}
```

> junction 的建立本身不写游戏目录里的任何文件，玩家那局在跑也**不影响**；但为稳妥，先确认没有别的 RimWorld 进程在读 `Mods\`。

### 6.2 建部署目录（复制运行所需部分，约 226 MB）

```powershell
$repo = 'E:\2026.7以后的新工程\DSH\RimWorld_auto_test\RimworldMods\ratkin_hunger_and_havoc'
$dst  = 'E:\2026.7以后的新工程\DSH\RimWorld_auto_test\profiles\rhah\Mods\RHAH_Local'
robocopy $repo $dst /E /XD .git docs Source scripts tmp obj bin 1.7 /XF *.pdb
```

- 保留：`About\`、`LoadFolders.xml`、`1.6\`、`Guard\`、`Biotech\`、`Textures\`、`Languages\`。
- 排除 `1.7\` 是安全的：`LoadFolders.xml` 只在 `<v1.7>` 段才引用 `1.7/…`，1.6 游戏走 `<v1.6>`（【实测】`LoadFolders.xml` 内容）。
- `robocopy` 退出码 1 表示「有文件被复制」，**不是失败**。

### 6.3 改 packageId（**必须按 UTF-8 读写**）

```powershell
$about = 'E:\2026.7以后的新工程\DSH\RimWorld_auto_test\profiles\rhah\Mods\RHAH_Local\About\About.xml'
$t = [System.IO.File]::ReadAllText($about, [System.Text.Encoding]::UTF8)
$t = $t.Replace('<packageId>nanaloveyuki.ratkin.hungerandhavoc</packageId>',
                '<packageId>nanaloveyuki.ratkin.hungerandhavoc.test</packageId>')
$t = $t.Replace('<name>', '<name>[TEST] ')                      # 让 mod 列表一眼可辨（可选但建议）
[System.IO.File]::WriteAllText($about, $t, (New-Object System.Text.UTF8Encoding($false)))
Select-String -Path $about -Pattern '<packageId>|<name>'         # 回读
```

⚠ 不要用 `Set-Content` / `Out-File`（默认编码会把中文写成 ANSI 乱码；本机 `Get-Content` 读出来就是乱码，实测）。

### 6.4 把部署目录接进游戏 `Mods\`（junction，不需要管理员）

```powershell
cmd /c mklink /J "D:\SteamLibrary\steamapps\common\RimWorld\Mods\RHAH_Local_TEST" "E:\2026.7以后的新工程\DSH\RimWorld_auto_test\profiles\rhah\Mods\RHAH_Local"
(Get-Item 'D:\SteamLibrary\steamapps\common\RimWorld\Mods\RHAH_Local_TEST').LinkType   # 回读 = Junction
```

> 目录名随便（mod 的身份由 `About.xml` 的 `packageId` 决定，扫描用的是 `GetDirectories()`）。但**名字要带 `TEST`**，因为玩家会在自己的 mod 列表里看到它（未激活）。

### 6.5 只改 **profile 的** `ModsConfig.xml`（第 13 项换成 `.test`）

```powershell
$mc = 'E:\2026.7以后的新工程\DSH\RimWorld_auto_test\profiles\rhah\Config\ModsConfig.xml'
$t = [System.IO.File]::ReadAllText($mc, [System.Text.Encoding]::UTF8)
$t = $t.Replace('<li>nanaloveyuki.ratkin.hungerandhavoc</li>', '<li>nanaloveyuki.ratkin.hungerandhavoc.test</li>')
[System.IO.File]::WriteAllText($mc, $t, (New-Object System.Text.UTF8Encoding($false)))
Select-String -Path $mc -Pattern 'hungerandhavoc'                 # 回读：只应剩 .test 一条
```

**绝不碰** `C:\Users\<你>\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Config\ModsConfig.xml`（玩家那份）。

### 6.6 启动前静态预检：**不启动游戏，先问「游戏会加载哪一份」**

这段是 A1–A3 的忠实模拟（扫 `Data` → `Mods` → 工坊，同 id 后来者加 `_steam`），只读，本机实测跑通（731 个 mod、549 ms）：

```powershell
$WS='E:\2026.7以后的新工程\DSH\RimWorld_auto_test'
$R='D:\SteamLibrary\steamapps\common\RimWorld'; $wsRoot='D:\SteamLibrary\steamapps\workshop\content\294100'
$map=@{}; $add={param($d) $a=Join-Path $d 'About\About.xml'; if(-not(Test-Path $a)){return}; try{[xml]$x=Get-Content $a -Raw}catch{return}; $id=$x.ModMetaData.packageId; if(-not $id){return}; $id=$id.Trim().ToLower(); if(-not $script:map.ContainsKey($id)){$script:map[$id]=$d} elseif($script:map[$id] -ne $d){$script:map[$id+'_steam']=$d} }
Get-ChildItem "$R\Data" -Directory | %{ & $add $_.FullName }
Get-ChildItem "$R\Mods" -Directory | %{ & $add $_.FullName }
Get-ChildItem $wsRoot -Directory  | %{ & $add $_.FullName }
[xml]$m=Get-Content "$WS\profiles\rhah\Config\ModsConfig.xml" -Raw; $i=0
$m.ModsConfigData.activeMods.li | %{ $i++; $k=$_.Trim().ToLower(); "{0,2}. {1,-42} {2}" -f $i,$k,$(if($map.ContainsKey($k)){$map[$k]}else{'** 未安装 **'}) }
'nanaloveyuki.ratkin.hungerandhavoc      -> ' + $map['nanaloveyuki.ratkin.hungerandhavoc']
'nanaloveyuki.ratkin.hungerandhavoc_steam-> ' + $map['nanaloveyuki.ratkin.hungerandhavoc_steam']
```

**判据**（三种状态一眼分清）：

| 状态 | `…hungerandhavoc` 指向 | `…_steam` |
| - | - | - |
| 今天（未装 junction） | 工坊 `294100\3807233439` | 不存在 |
| 方案 1（同 id junction） | `…\RimWorld\Mods\RHAH_Local_TEST`（❗玩家也要变） | 工坊 |
| **方案 2（本推荐）** | 工坊 `294100\3807233439` | 不存在（`.test` 是另一个 id，不参与去重） |

同时核对「激活的到底是哪份 DLL」的文件级证据：

```powershell
Get-FileHash 'E:\2026.7以后的新工程\DSH\RimWorld_auto_test\profiles\rhah\Mods\RHAH_Local\1.6\Assemblies\HungerAndHavoc.dll' -Algorithm SHA256
Get-FileHash 'D:\SteamLibrary\steamapps\workshop\content\294100\3807233439\1.6\Assemblies\HungerAndHavoc.dll' -Algorithm SHA256
# 期望：前者 D38461F0…C5C5FCC2（865,280 B / 2026-10-24 16:56:33）、后者 B987BF67…D0AD35BC6（848,384 B / 2026-10-16 22:15:02）
```

### 6.7 起游戏（隔离 profile，事件驱动等待，禁 `Start-Sleep`）

```powershell
node src/cli.js start --profile rhah --width 1600 --height 900 --monitor 2
node src/cli.js live  --profile rhah --until 'kind=game_loaded' --timeout 300000   # 挂后台 job，完成即推送
```

重编 DLL 之后不需要重建 junction、也不用重抄整份目录，只同步程序集：

```powershell
Copy-Item 'E:\2026.7以后的新工程\DSH\RimWorld_auto_test\RimworldMods\ratkin_hunger_and_havoc\1.6\Assemblies\*' `
          'E:\2026.7以后的新工程\DSH\RimWorld_auto_test\profiles\rhah\Mods\RHAH_Local\1.6\Assemblies\' -Force
```

---

## 7. 还原步骤（顺序无关，都只读/只删自己造的东西）

```powershell
# 1) 删 junction —— 用 cmd 的 rmdir（只删链接，不碰目标）
cmd /c rmdir "D:\SteamLibrary\steamapps\common\RimWorld\Mods\RHAH_Local_TEST"
Test-Path 'D:\SteamLibrary\steamapps\common\RimWorld\Mods\RHAH_Local_TEST'     # 期望 False
Test-Path 'E:\2026.7以后的新工程\DSH\RimWorld_auto_test\profiles\rhah\Mods\RHAH_Local\About\About.xml'  # 期望 True（目标还在）

# 2) ModsConfig 第 13 项改回原 id
$mc = 'E:\2026.7以后的新工程\DSH\RimWorld_auto_test\profiles\rhah\Config\ModsConfig.xml'
$t = [System.IO.File]::ReadAllText($mc,[System.Text.Encoding]::UTF8).Replace('<li>nanaloveyuki.ratkin.hungerandhavoc.test</li>','<li>nanaloveyuki.ratkin.hungerandhavoc</li>')
[System.IO.File]::WriteAllText($mc,$t,(New-Object System.Text.UTF8Encoding($false)))
Select-String -Path $mc -Pattern 'hungerandhavoc'

# 3) 删部署目录（可选，想留着下次直接用就别删）
Remove-Item 'E:\2026.7以后的新工程\DSH\RimWorld_auto_test\profiles\rhah\Mods\RHAH_Local' -Recurse -Force
```

⚠ **不要用 `Remove-Item -Recurse` 删 junction 本身**：Windows PowerShell 5.1 有「跟着链接进去删目标内容」的旧问题（【常识/未验证】），一律用 `cmd /c rmdir` 或 `[System.IO.Directory]::Delete($path)`。

⚠ 还原后重跑一次 §6.6 的预检，确认 `…hungerandhavoc` 又指回工坊 `3807233439`。

> 补充（【实测 IL】）：`ModsConfig.DeactivateNotInstalledMods` 会自动把「本机装不上」的 `<activeMods>` 项删掉。
> 所以**先删 junction 再改 `ModsConfig`** 也不会坏事（`.test` 那项会在下次启动时被静默移除）；反过来，若想保留证据就按上面顺序先改配置。

> 方案 3（覆盖法）如果真要做，还原是：先 `Copy-Item` 备份 → 覆盖 → 测完 `Copy-Item` 备份回去 →
> `Get-FileHash` 核对 `B987BF67D8EB42B8…D0AD35BC6`。备份放 `$WS\runs\workshop-backup-<日期>\`，并在文件里记下原 SHA256。

---

## 8. 怎么确认「加载的是我们重建的那份」

按「先静态、再运行时、最后行为」三层，越往下越贵：

**① 静态（启动前，§6.6）** — 证明「游戏只会看到哪几个候选、activeMods 会解析到哪个目录」。

**② 运行时·桥接（最直接）**

```powershell
node src/cli.js call rimworld/list_mods --profile rhah
node src/cli.js call rimworld/get_mod_configuration_status --profile rhah
```

- `rimworld/list_mods` 每行的字段含 `index / id / folderName / legacyFolderName / displayName / … / isActive / hasErrors / loa…`（格式串取自 `RimBridgeServer.dll` 字符串，**未在活游戏里跑过**）。
  判据：出现 `folderName = RHAH_Local_TEST`、`id/packageId = …hungerandhavoc.test`、`isActive = true`；
  同时那条 `…hungerandhavoc`（工坊版）应为 `isActive = false`，且**列表里不应出现 `_steam`**。
- `rimworld/get_mod_configuration_status` 回读 `activeMods` / `loadedSessionModIds`：应含 `.test`、不含 `_steam`；若出现 `active_mod_set_differs_from_loaded_session` 说明改完 `ModsConfig` 没重启。

**③ 运行时·日志（可留存取证）**

1. 把 `profiles\rhah\Config\Prefs.xml` 里 `<logVerbose>False</logVerbose>` 改成 `True`；
2. 重启后看 `profiles\rhah\Player.log`（`src\launcher.js:403-404` 用 `-logFile` 指定，实测）。
   会打出 `Rebuilding mods list` + 每份 mod 的 `Adding <packageIdPlayerFacing>(<RootDir>)`
   （`ModMetaData.ToStringLong()` = `PackageIdPlayerFacing + "(" + RootDir + ")"`【实测 IL】）。
   判据：出现 `Adding …hungerandhavoc.test(E:\…\profiles\rhah\Mods\RHAH_Local)`；若还看到第二行同名 `hungerandhavoc(D:\…\workshop\…)`，说明**工坊那份也被扫到了**（正常，未激活即可，但出现 `_steam` 相关红字就说明去重被触发了 ⇒ 说明 packageId 没真的改成功）。

**④ 行为层（证明「跑的是新代码」而不只是「路径是新的」）**

- 新构建独有 `RHAH_ArmedDemand.Owner/Refresh/Finish/Tick`（旧构建一个都没有，`docs\RHAH-1.6重建报告-20261024.md:126-131`【实测】）。
- 现成手段里**没有**「反射 dump 某类型方法表」的桥接工具（`rimworld/tools` 里没有；`ce/*` 只服务 Character Editor），所以便宜的替代是**组合证据**：
  §6.6 的 SHA256（重建那份）+ §8② 的 `folderName` ⇒「唯一激活的 RHAH 的根目录是部署目录，而该目录 `1.6\Assemblies` 里只有一个 DLL，哈希 = 重建产物」。
- 反向判别器（若愿意驱动 UI）：RHAH 的 IrisMenus 菜单页里「自身版本」在 `.test` 激活时会显示 `1.0.0`（因为 `ContentFinderVersion()` 查的是 base id，查不到 ⇒ 回退值），显示 `1.1.4` 反而说明**加载的是工坊那份**。这是 §3 表里那条自我引用带来的副作用，可以当探针用。

---

## 9. 未验证 / 已知不确定（照实列）

1. **本轮没启动游戏**：§8 的 ②③④ 全部是「设计好的验证方法」，没有一条真的跑过；`list_mods` 的字段名来自 DLL 字符串而非一次真实返回。
2. **「同 packageId 两份并存」的运行时表现没在活游戏里看过**：结论完全由 `Assembly-CSharp.dll` 的 IL（`RebuildModList` / `TryAddMod` / `ActiveModsInLoadOrder` / `InitializeMods`）+ 静态模拟器得出，**没有 Player.log 佐证**。
3. 若在 `ModsConfig` 里**同时**列 base 与 `_steam`（两份都激活），会发生什么（多半是一整套 Def 重名红字）**未验证**。
4. `ModMetaData.Init` 对「目录里没有 About.xml / About.xml 坏掉」的具体处置**没读**（`TryAddMod` 之前就构造），只确认扫描会把每个子目录都交给它。
5. 「thin 部署目录里再嵌套 junction（只复制 About+LoadFolders，`1.6`/`Textures` 等 junction 回仓库）」**未验证**：能省 226 MB 且重编后零同步，但嵌套 junction 在 mod 根目录里的行为我没实测过。
6. `Remove-Item -Recurse` 删 junction 是否会跟进目标（PS 5.1 vs PS 7 差异）**未验证**，所以一律建议 `cmd /c rmdir`。
7. Steam 是否会在订阅刷新/重新下载时覆盖工坊目录、以及「验证文件完整性」是否涉及工坊内容：**未验证**（方案 3 的还原风险只写了常识判断）。
8. 玩家那 342 项目录下，若他自己**正好也在 `Mods\` 放了别的 RHAH 副本**（本轮实测没有）会怎样：未验证。
9. 方案 2 的 `.test` 存档在工坊版里打开时的**具体**提示文案/是否需要 `ignoreModCompatibility`：未验证（属原版存档兼容提示逻辑）。
10. 本轮**没有**核对 `About\About.xml` 的 `<name>` 加 `[TEST]` 是否影响任何功能（应该纯显示，未验证）。
11. 方案 1 里「工坊那份被加 `_steam` 后在玩家 mod 列表里怎么显示、能不能被勾选」**未验证**（推演依据是 A2 的 IL，不是界面观察）。
