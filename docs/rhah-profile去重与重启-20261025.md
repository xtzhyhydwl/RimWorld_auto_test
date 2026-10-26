# rhah profile ModsConfig 去重 + 重启验证（2026-10-25）

> 全部数字/日志/SHA 都是本次**实测**读到的；凡是推断都标了【推断】。命令在 `E:\2026.7以后的新工程\DSH\RimWorld_auto_test` 下执行。

## 结论

1. **去重成功**：`activeMods` 19 条 → **16 条**，删掉的是三条混合大小写重复项，保留同名全小写三条；`knownExpansions` 5 条、`<version>1.7.4985 rev1269` 未动；文件仍是 **961 B / UTF-8 BOM / CRLF**。
2. **重启成功**：新实例 **PID 15204**、桥接 **127.0.0.1:5174**（`bridgeResolution.ok=true`，token 归属 rhah），1600x900 在 monitor 2。
3. **duplicate key 消失**：改前日志 `An item with the same key has already been added` **1957 次**；改后整份新日志 **0 次**（`duplicate key` 也是 0）。`rimworld/list_mods` 与 `rimworld/get_mod_configuration_status` 均 `success:true`（改前这两个调不通）。
4. **三条 mod 正常**：HAR / NewRatkinPlus / IrisMenus 与 RHAH 全部 `loadedInSession=true`、`matchesLoadedSession=true`、`unsatisfiedDependencies=[]`，加载序 12/13/14/15，均不再被判未激活。
5. **IrisMenus 分页恢复**：`[RHAH] IrisMenus 1.1.2 pages registered.`（10:17:49.263）在；`is not active` **0 次**；并有活证据——RHAH 的 IrisMenus 页面正在被绘制（栈 `RHAH_IrisMenusPages.DrawDevEvents → IrisMenus.MenuSession.Draw`），截图底栏有 `IrisMenus` 分页按钮。**注意**：改前那一份脏配置日志里 `pages registered` 就已经在了（见 §5.4），所以**分页恢复不是本次去重带来的**，去重只解决 duplicate key 崩溃。
6. **人类现在可直接接手**：已进图（`Map_0`，3 名殖民者 Ceri/Julia/Neon 正在干活）、**未暂停**、无强制暂停窗口、无模态对话框、dev mode 开；之后我没有再发任何游戏操作命令。

---

## 1. 停实例过程

| 项 | 实测 |
|---|---|
| 命令 | `node src/cli.js stop --profile rhah`（**未加 `--force`**） |
| 返回 | `alreadyStopped: true`，`forced: false`，`recordedPid: 14904`，`skipped:[{pid:14904,reason:"PID 14904 不存在"}]`，exit 0 |
| 我到达现场时的实况 | `Get-CimInstance Win32_Process -Filter "name='RimWorld.exe'"` → **无输出**；`Get-Process -Name RimWorld` → 空；`Get-CimInstance Win32_Process` 全表按 `Name/CommandLine` 匹配 `RimWorld` → **只有我这条 PowerShell 命令自己**；`(… -Filter "name='RimWorld.exe'").Count` = **0** |
| 5174 端口 | `Get-NetTCPConnection -LocalPort 5174` → 无记录；5100–5200 无监听 ⇒ 桥接早就没了 |
| 是否动过别的东西 | **没有**。一个 RimWorld 进程都没杀，`Stop-Process` 一次没用过 |

**记录与实际不一致（如实报告）**：派单说"当前有 RimWorld 在跑：PID 23268（10:01:28 自我重启）"，但**实测 10:16 时全机没有任何 RimWorld.exe**，且 CLI 记录里是 **PID 14904**（不是 23268）。也就是说实例在我接手前已经自己退了。因此第 1 步的"停"是**空操作**，没有可用 PID 需要定位。

---

## 2. 文件稳定性（停完后未被二次改写）

| 时刻 | 字节 | mtime | SHA256 |
|---|---|---|---|
| 10:16:06（首次读） | 1074 | 2026-10-25T10:01:27.1692274+08:00 | `0E60C2447D3B17E0ED0F981C21CE2D48CADB5C2EAB1CB9CBE72CD9CE33F86DAD` |
| 10:20 前后（复查） | 1074 | 2026-10-25T10:01:27.1692274+08:00 | 同上 |

结论：mtime 停在 **10:01:27**（游戏自己最后一次写盘），此后 19 分钟无变化，且无进程可写 ⇒ 判定稳定。

---

## 3. 文件差异（19 → 16）

### 3.1 备份

| 文件 | 字节 | SHA256 |
|---|---|---|
| `runs\modsconfig-fix-20261025\ModsConfig.dirty-19entries.xml`（脏版原样字节拷贝） | 1074 | `0E60C2447D3B17E0ED0F981C21CE2D48CADB5C2EAB1CB9CBE72CD9CE33F86DAD` |
| `runs\modsconfig-fix-20261025\ModsConfig.before.xml`（更早的 16 条混合大小写版，未动） | 933 | `BEB0FC408C1768E2BF2A4A3C30D201B81E62EDA8EC4F2F74F80240B77537123E` |
| `runs\modsconfig-fix-20261025\ModsConfig.fixed-16entries.xml`（我改完的 16 条版快照） | 961 | `5996346EDEC6D4943E4925BE9B0F69EC509B192914721C7BC937BF628030E7FA` |

### 3.2 删掉的三条（原第 11–13 位，混合大小写）

```
erdelf.HumanoidAlienRaces     ← 删（保留 erdelf.humanoidalienraces）
Solaris.RatkinRaceMod         ← 删（保留 solaris.ratkinracemod）
Nanaloveyuki.IrisMenus        ← 删（保留 nanaloveyuki.irismenus）
```

其余条目、相对顺序、`knownExpansions` 5 条、`<version>1.7.4985 rev1269` 一律未动。

### 3.3 改后内容（16 条，实测）

```
brrainz.harmony, ludeon.rimworld, ludeon.rimworld.royalty, ludeon.rimworld.ideology,
ludeon.rimworld.biotech, ludeon.rimworld.anomaly, ludeon.rimworld.odyssey,
brrainz.rimbridgeserver, dsh.gamelink, void.charactereditor, dsh.autotest,
nanaloveyuki.oxygendsl, erdelf.humanoidalienraces, solaris.ratkinracemod,
nanaloveyuki.irismenus, nanaloveyuki.ratkin.hungerandhavoc
```

### 3.4 写文件是怎么做的（BOM/换行的坑，实测）

派单要求"保持 BOM + CRLF，且只用 read/write/edit"。先在**副本**上把两种工具的行为测了出来：

| 工具 | 结果（副本实测字节） |
|---|---|
| `edit` 删三行 | 换行 **保住**（CRLF=28、LF-only=0），**BOM 丢了**（`first3=3C 3F 78` = `<?x`） |
| `write` 整体重写 | **BOM 丢了**，**换行也变了**（CRLF=0、LF-only=29） |

⇒ 两者单独都满足不了"BOM + CRLF"。最终做法：**两次 `edit`**——第一次删三行（CRLF 保住），第二次把行首的 `<?xml` 替换成 `\uFEFF<?xml`（U+FEFF 以 UTF-8 编码就是 `EF BB BF`，等于把 BOM 补回），再用 PowerShell 只读校验字节。全程没走 shell 重定向写文件。

### 3.5 ⚠ 启动器又重写了一次（内容等价、顺序微调）

`start` 跑完后再读该文件，**mtime 变成 `10:17:32.298`、SHA 变了**：

| 版本 | 字节 | BOM | CRLF | SHA256 |
|---|---|---|---|---|
| 我改完（快照） | 961 | `EF BB BF` | 28 | `5996346EDEC6D4943E4925BE9B0F69EC509B192914721C7BC937BF628030E7FA` |
| **当前盘上** | 961 | `EF BB BF` | 28 | `8913C1EECA6DB4E3AD176B4EF1A57B92317F9BFE97ABE2E3863BF5BD136126CA` |

逐字节比对：**唯一差异在 offset 473**——`nanaloveyuki.oxygendsl` 与 `dsh.autotest` 互换位置（当前是 autotest 在前）。**条目集合、条数、大小写、BOM、CRLF、version 全一致**。

- 实测：mtime `10:17:32.298` 比游戏进程启动时间 `10:17:36.467`（日志 `Started:` 行）**早约 4 秒**。
- 【推断】改写者是 `start` 自己（其帮助文本写明"底座 mod 按 base-mods.json 自动补齐"，会重写该文件），不是游戏回写。**未逐行验证 launcher 源码**。

---

## 4. 校验结果（当前盘上的文件，实测）

| 校验项 | 结果 |
|---|---|
| `[xml]` 解析 | **通过** |
| 字节数 | 961 |
| BOM | `EF BB BF` ✔ |
| 换行 | CRLF **28**、LF-only **0** ✔ |
| `activeMods` 条数 | **16** ✔ |
| 重复（区分大小写去重） | 16 → **16**，无重复 ✔ |
| 是否还有大写字母条目（`-cmatch '[A-Z]'`） | **0** ✔ |
| 全部 `<li>` 匹配 `^[a-z0-9._]+$` | 违规 **0** 条 ✔ |
| `<version>` | `1.7.4985 rev1269` ✔ |
| `knownExpansions` | 5 条 ✔ |

> 自查纠错：我第一次那条"大写条目数"用了 `-match`，PowerShell 默认**不区分大小写**，误报 16；换 `-cmatch` 后实测 0。记在这里以免后人复用错判据。

---

## 5. 启动与验证证据

### 5.1 启动

```
node src/cli.js start --profile rhah --width 1600 --height 900 --monitor 2
→ 游戏已启动 PID=15204  profile=rhah
  安装目录: D:\SteamLibrary\steamapps\common\RimWorld
  存档目录: …\profiles\rhah   (独立)
  端口: 5174（auto）
  窗口: 窗口化 1600x900  显示器=2
→ 桥接就绪：127.0.0.1:5174  (等待 36.9s，来源=record)   exit 0
```

`status --profile rhah`：`gameRunning:true`、`runningState:running`、`otherGamePids:[]`、`bridge.app = RimBridgeServer 2.1.1.0`、`bridgeResolution.ok=true`（记录/profile/端口/token 四项一致）。

**主菜单等待是"失败"的，但不是故障**：`wait --scenario 主菜单待开局` 60s 超时（`inEntryScene:false`），因为实例**直接进了一个新殖民地**——`hasCurrentGame:true`、`currentMapId:Map_0`、`playable:true`。日志 `L1981 10:18:16.312 INFO [Verse] Initializing new game with mods:` 可证是**新建局**（不是读档），且**我没有发过任何开局命令**（谁触发的→§8 未验证）。

### 5.2 a) 两个桥接调用能调通

| 调用 | 结果 |
|---|---|
| `rimworld/list_mods` | exit 0，`success:true`，操作 `rimbridge.core/mod_configuration/list-mods` 完成，载荷约 1.11 MB（862 个已装 mod 全表），落盘 `runs\modsconfig-fix-20261025\list_mods.json` |
| `rimworld/get_mod_configuration_status` | exit 0，`success:true`；`activeModCount=16`、`loadedSessionModCount=16`、`unsatisfiedDependencyCount=0`、`restartRequired=false`、`currentConfigurationHash == loadedSessionHash == 713df252f4051fd4`；另有 `configurationIssueCount=1`（oxygendsl 顺序）、`versionWarningCount=7`、`orderingIssueCount=1` |

改前这两条"全调不通"（派单给的现场结论），现在都通 ⇒ duplicate key 那条崩链路断了。

### 5.3 b) duplicate key 消失（含改前对照）

**改前**（这份旧日志被游戏改名成 `Player-prev.log`，3383222 B；已拷到 `runs\modsconfig-fix-20261025\Player.prefix.log`）：

- 命中 `An item with the same key has already been added`：**1957 次**（与派单给的 1957 一致）
- 位置：第 **2126** 行 ~ 第 **35458** 行；时间 **10:01:51.121 ~ 10:02:00.248**
- 原文首条：

```
2026-10-25 10:01:51.121 ERROR [Verse] Exception filling window for RimWorld.Page_ModsConfig: System.ArgumentException: An item with the same key has already been added. Key: erdelf.humanoidalienraces
   at System.Collections.Generic.Dictionary`2.Add(TKey key, TValue value)
   at Verse.ModsConfig.GetModWarnings()
   at RimWorld.Page_ModsConfig.DoWindowContents(Rect rect)
```

**改后**（新日志 `Player.log`，拷到 `runs\modsconfig-fix-20261025\Player.postfix.log`，182001 B / 2066 行）：

| 关键字 | 命中 |
|---|---|
| `An item with the same key has already been added` | **0** |
| `duplicate key` | **0** |
| `Exception filling window for RimWorld.Page_ModsConfig` | **0** |

### 5.4 c) 三条目标 mod + RHAH

来自 `get_mod_configuration_status`（每条都实测字段）：

| mod | packageId | loadedInSession | loadedSessionOrder | matchesLoadedSession | unsatisfiedDependencies |
|---|---|---|---|---|---|
| Humanoid Alien Races | `erdelf.humanoidalienraces` | **true** | 12 | true | `[]` |
| NewRatkinPlus | `solaris.ratkinracemod` | **true** | 13 | true | `[]` |
| IrisMenus 1.1.2 | `nanaloveyuki.irismenus` | **true** | 14 | true | `[]` |
| RHAH 1.1.4 | `nanaloveyuki.ratkin.hungerandhavoc` | **true** | 15 | true | `[]` |

`loadedSessionModIds` 与 `currentActiveModIds` 两个列表**逐元素相同**（16 条），`sessionMismatchCount=0` ⇒ 没有任何一条被判"配置里有、本次没加载"。

### 5.5 d) IrisMenus：`not active` 消失 + 分页注册的正面证据

**新日志原文（L1266）**：

```
2026-10-25 10:17:49.263  INFO [Verse] [RHAH] IrisMenus 1.1.2 pages registered.
```

紧跟其下的调用栈（L1269）指向 `HungerAndHavoc.Pawn.Compat.RHAH_IrisMenusCompat.TryRegister(RHAH_Mod owner)` —— 即走的是**注册成功分支**。

**更强的一条：页面真的在被绘制**（L2029 起，10:18:38.529）：

```
2026-10-25 10:18:38.529  INFO [Verse] [RHAH] Spawned queued I-011. def=RHAH_FamineRefugees points=350 target=map:0
    at HungerAndHavoc.Core.GameComponent_RHAH_Game.ExecutePending(…)
    at HungerAndHavoc.Core.GameComponent_RHAH_Game.SpawnDebugOnMap(…)
    at HungerAndHavoc.Core.RHAH_Scheduler.SpawnDebugIncidentOnMap(…)
    at HungerAndHavoc.Pawn.Compat.RHAH_IrisMenusPages.SpawnInstant(RHAH_IncidentEntry entry) [0x55]
    at HungerAndHavoc.Pawn.Compat.RHAH_IrisMenusPages.DrawDevEvents(Listing_Standard list) [0x1B]
    at IrisMenus.MenuScrollView.Draw(Rect viewport, Action`1 draw) [0xE5]
    at IrisMenus.MenuSession.Draw(Rect rect) [0x0]
    at IrisMenus.MenuWindow.DrawSelected(Rect rect) [0x103]
    at IrisMenus.MenuWindow.DoWindowContents(Rect inRect) [0x322]
```

`RHAH_IrisMenusPages.DrawDevEvents` 能被执行 ⇒ RHAH 往 IrisMenus 注册的那个页面**确实存在并在窗口里被画**。此外截图底栏可见 `IrisMenus` 分页按钮（`profiles\rhah\Screenshots\rhah-handoff-20261025.png`）。

**`is not active` 计数**：新日志 **0 次**（`is not active` 全文命中 0）。

> **诚实边界**：同一个判据在**改前**那份脏配置日志里就已经成立了——`2026-10-25 10:01:31.713 INFO [Verse] [RHAH] IrisMenus 1.1.2 pages registered.`，且那份日志 `is not active` 也是 **0** 次。也就是说分页恢复是**更早那轮工坊构建修复**的结果，**不能记在本次去重头上**。历史上 `[RHAH] IrisMenus is not active. Menu pages were not registered.` 出现在 09:49 那轮（见 `docs\RimWorld-1.7设置页无分页-诊断-20261025.md`）和 1.6 会话（`profiles\rhah\Player-2026-10-24T09-38-08-362Z.log`）。

### 5.6 e) env-check

`node src/cli.js env-check --profile rhah`（exit **1**，两个阻塞项，均非崩溃）：

```
进程     running=true pid=15204 identityOk=true cmdlineHasProfile=true
桥接     reachable=true resolutionOk=true 127.0.0.1:5174 source=record
mods     activeModsCount=16 底座 11 个、缺 0 个 本次已加载=16 个
加载顺序 queried=true restartRequired=false 问题 1 条
能力     GameLink 事件=true CharacterEditor=true ce/* 工具=9 工具总数=184
阻塞项：
 - 有 1 个 mod 报告加载顺序问题（nanaloveyuki.oxygendsl：hasOrderingIssues=true）
 - 有窗口在强制暂停（windowsForcePause=true）…（此条为当时快照，随后复查已 false）
```

- `mods.activeModsCount = 16` ✔
- **unsatisfiedDependencies 为空**：`get_mod_configuration_status.unsatisfiedDependencyCount = 0`，且 16 条 activeMods 每条的 `unsatisfiedDependencies` 都是 `[]` ✔
- 唯一残余问题是 `nanaloveyuki.oxygendsl`：`configurationWarning = "This must be loaded after: IrisMenus."`，但它在 11、IrisMenus 在 14 ⇒ **顺序确实反了**（运行时它还抛了 `ReflectionTypeLoadException`，见 §5.7）。这是**既有问题，未修**。

### 5.7 新日志里剩下的 4 条 ERROR（都与 duplicate key 无关，如实列出）

| # | 时间 | 原文摘要 |
|---|---|---|
| 1 | 10:17:43.769 | `Tried loading mod with the same packageId multiple times: wuren.ToddlersFeed. Ignoring the duplicates.`（工坊两个目录 `3693600798` / `3702348043`） |
| 2 | 10:17:45.037 | `Tried loading mod with the same packageId multiple times: Fluffy.FollowMe. Ignoring the duplicates.`（`3382255579` / `715759739`） |
| 3 | 10:17:48.159 | `ReflectionTypeLoadException getting types in assembly Oxygendsl.IrisMenus`：`Could not load type 'IrisMenus.IMarkdownParser'` / `'IrisMenus.MenuSearchEntry'` from `IrisMenus, Version=1.1.1.0`（oxygendsl 按 1.1.1 编的，装的是 1.1.2） |
| 4 | 10:17:54.441 | `Failed to find Verse.TerrainDef named AncientHeavyBridge.`（某 mod 的 Def 引用缺失） |

---

## 6. 当前可操作状态（交给人）

| 项 | 实测值 |
|---|---|
| PID | **15204**（`status` 确认：`known:true`、`otherGamePids:[]`） |
| 端口 | **5174**（RimBridgeServer 2.1.1.0，token 与记录一致） |
| 窗口 | 窗口化 **1600x900**，`-monitor 2`（副屏 `\\.\DISPLAY2`，bounds `{-1920,563,1920x1080}`） |
| 局面 | `hasCurrentGame=true`、`currentMapId=Map_0`、`playable=true` |
| 暂停 | **`paused:false`**；`timeSpeed` 最终读到 `Normal` |
| 阻挡 | `nonImmediateDialogWindowOpen=false`、`windowsForcePause=false`、`anyWindowAbsorbingAllInput=false`、`floatMenuOpen=false`；主界面开着 Architect 分页（`mainTabOpen=true`） |
| 殖民者 | 3 名：**Ceri**（`Thing_Human118`，job `GiveToPawn`）、**Julia**（`Thing_Human121`，`Ingest`）、**Neon**（`Thing_Human115`，`BuildRoof`）——都有在跑的 job，说明时间真的在走 |
| dev mode | `profiles\rhah\Config\Prefs.xml` → `<devMode>True</devMode>` |
| 截图 | `profiles\rhah\Screenshots\rhah-handoff-20261025.png`（3840x2019，20.4 MB）；另有一张 `rhah-handoff-20261025-fast.png`（21.1 MB，超出读图工具 20 MB 上限，我没打开看过） |

**速度这件事要说清**：桥接启动时把 Ultrafast 打开了（`set_time_speed` 描述原文："RimBridgeServer enables the boost for speed 4 (Ultrafast) at startup"），实测读到 `timeSpeed:"Ultrafast"`。按 AGENTS.md 硬约束 6（上限 Fast、禁 Ultrafast）我调了一次：

```
call rimworld/set_time_speed {"speed":"Fast","ultraSpeedBoost":false}
→ success:true, timeSpeed:"Fast", previousUltraSpeedBoost:true, currentUltraSpeedBoost:false, paused:false
```

随后几秒再读变成 `timeSpeed:"Normal"`（**游戏自己改的，原因未验证**；`Prefs` 里 `automaticPauseMode=MajorThreat`，期间确有 RHAH 事件被排队/生成）。最终落在 `Normal` 且未暂停，符合约束。

**这是本次最后一批游戏操作命令**；此后我只做了只读调用（`get_bridge_status` / `get_ui_state` / `list_colonists` / `take_screenshot`）。

---

## 7. 工坊状态与还原命令

工坊目录：`D:\SteamLibrary\steamapps\workshop\content\294100\3807233439`（RHAH）。**当前装的是我们的 1.7 构建**（实测，未还原）：

| 文件（相对 mod 根） | 字节 | SHA256 | mtime |
|---|---|---|---|
| `1.7\Assemblies\HungerAndHavoc.dll` | 869 376 | `DD873F9DB6EF040EC94CAC563611946F1E3E4DE38F825FF45AC2FF9005548EB3` | 2026-10-25 02:59:22 |
| `1.7\Assemblies\HungerAndHavoc.Api.dll` | 14 336 | `093F60C52780C1A67C10FB9FCB4A3DBBED4143BBC9CEDA34EBEB800268840A7C` | 2026-10-25 02:59:21 |
| `1.7\Defs\DutyDefs\RHAH_Duties.xml` | 703 | `69DF58FC53351CEE4617F1CD5145F0DA6A58CCF44A3B3CF4A9F7C15B9303EDCE` | 2026-10-25 02:55:40 |

三个哈希与派单给的 `DD873F9D…` / `093F60C5…` / `69DF58FC…` **逐个吻合** ⇒ **不需要还原，我也没有还原**（人类还要测）。

顺带核实（未动）：`1.6\` 与 `Guard\` 下的文件仍是旧版（`B987BF67…` / `0167D1B1…` / `4C1AEAA1…` / `2991D9A7…` / `E1ABC339…`），本次一律没碰。

### 需要还原成作者原版时（**别现在跑**，先确认人类测完）

备份根（已核实三份都在、哈希与文档一致）：

```
E:\2026.7以后的新工程\DSH\RimWorld_auto_test\runs\verify-0008-green2-backup-20261024\1.7\
```

| 备份文件 | 字节 | SHA256（作者原版） |
|---|---|---|
| `Assemblies\HungerAndHavoc.dll` | 868 352 | `15C09CCC3099B1A722113F20D365ECDE3E4A66F9B211CD2FE22A3601EF7D6FE5` |
| `Assemblies\HungerAndHavoc.Api.dll` | 14 336 | `37DB1ECBC2FEAF556D8E6661F7B47430EC8B4C4A3D0EA1417EFBFF418B892E6F` |
| `Defs\DutyDefs\RHAH_Duties.xml` | 615 | `4C1AEAA1D059C966350D20C471193868F0C46738131F459B8A9528B381B2602B` |

还原命令（三条，逐文件覆盖，不做目录级同步）：

```powershell
$bak = 'E:\2026.7以后的新工程\DSH\RimWorld_auto_test\runs\verify-0008-green2-backup-20261024\1.7'
$ws  = 'D:\SteamLibrary\steamapps\workshop\content\294100\3807233439\1.7'
Copy-Item -LiteralPath "$bak\Assemblies\HungerAndHavoc.dll"     -Destination "$ws\Assemblies\HungerAndHavoc.dll"     -Force
Copy-Item -LiteralPath "$bak\Assemblies\HungerAndHavoc.Api.dll" -Destination "$ws\Assemblies\HungerAndHavoc.Api.dll" -Force
Copy-Item -LiteralPath "$bak\Defs\DutyDefs\RHAH_Duties.xml"     -Destination "$ws\Defs\DutyDefs\RHAH_Duties.xml"     -Force
```

想从我们的构建**重新装回去**（反向），源是仓库里的：

```
E:\2026.7以后的新工程\DSH\RimWorld_auto_test\RimworldMods\ratkin_hunger_and_havoc\1.7\{Assemblies\HungerAndHavoc.dll, Assemblies\HungerAndHavoc.Api.dll, Defs\DutyDefs\RHAH_Duties.xml}
```

（这三份我实测哈希 = `DD873F9D…` / `093F60C5…` / `69DF58FC…`，与工坊现状一致。）

---

## 8. 未验证 / 没做的事

1. **PID 23268 是谁**：派单说在跑 23268，我 10:16 到场时全机没有 RimWorld.exe，CLI 记录是 14904。我无法确认 23268 是何时、怎么退的（也没有它的日志切片）。**没杀任何进程**。
2. **新殖民地是谁开的**：日志只有 `Initializing new game with mods:`（10:18:16），没有 `-quicktest` 参数、`src\` 里也没有自动开局代码（grep `quicktest|autostart` 只命中 `smoke.js` 一处，未参与本次启动）。触发者**未验证**（怀疑 1.7 引擎/某 mod 自动开局，纯属推测）。
3. **`nanaloveyuki.oxygendsl` 顺序问题没修**：它要求排在 IrisMenus 之后，实际在 11、IrisMenus 在 14，且抛 `ReflectionTypeLoadException`（IrisMenus 1.1.1.0 vs 1.1.2）。不在本次任务范围，未动 ModsConfig 顺序（派单明确要求"顺序不动"）。**注意：若调顺序，会影响已稳定的 16 条配置。**
4. **`timeSpeed` 从 Fast 变回 Normal 的原因**未验证。
5. **两次 `[RHAH] Spawned queued I-011`（10:18:38、10:19:35）是谁触发的**未验证——栈显示是 IrisMenus 窗口里 RHAH 页面的 `SpawnInstant`，但我当时没发过点击命令；可能是鼠标停在那个按钮上导致的重复触发。10:19:35 之后没有再出现。
6. **没进主菜单**：实例自进游戏，所以"主菜单待开局"这条 wait 场景没验成（超时），与去重无关。
7. **没验证 `stop` 之后能否再正常停**（这轮只验了启动/运行）。
8. 第二张截图 `rhah-handoff-20261025-fast.png` 超过读图工具上限，**我没亲眼确认其内容**。

---

## 附：本次产物一览（`runs\modsconfig-fix-20261025\`）

| 文件 | 说明 |
|---|---|
| `ModsConfig.before.xml` | 更早的 16 条混合大小写版（933 B，未动） |
| `ModsConfig.dirty-19entries.xml` | 脏版 19 条原样备份（1074 B） |
| `ModsConfig.fixed-16entries.xml` | 我改完的 16 条快照（961 B，`5996346E…`） |
| `Player.prefix.log` | 改前日志（3383222 B，1957 条 duplicate key） |
| `Player.postfix.log` | 改后日志（182001 B，duplicate key 0） |
| `list_mods.json` | `rimworld/list_mods` 原始返回 |
| `mod_config_status.json` | `rimworld/get_mod_configuration_status` 原始返回 |
| `env-check.json` | `env-check` 原始返回 |
| `ui_state.json` | `get_ui_state` 原始返回 |
| `colonists.json` / `start-output.txt` | 殖民者 / 启动输出 |
