# 隔离测试 profile `rhah` 已创建 —— 2026-10-24

**范围**：本轮**只**创建了 `profiles\rhah\` 下的文件与本文。
**没有**启动游戏、**没有**改玩家自己的 `ModsConfig.xml`（`%USERPROFILE%\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Config\ModsConfig.xml`，最后写入仍是 2026-10-24 10:00:31）、
**没有**碰 `profiles\vanilla` / `voiced` / `bike` / `dropbisect-selftest` / `steam-attach-20261004`、**没有**碰游戏安装目录/工坊目录/用户存档（`Saves\` 最后写入 16:10:56，早于本轮）。

标注约定：**【实测】**= 本轮读到的文件内容或命令输出；**【未验证】**= 按约束没做或本机手段不足。

---

## 1. 产出

| 项 | 值【实测】 |
|-|-|
| profile 目录 | `E:\2026.7以后的新工程\DSH\RimWorld_auto_test\profiles\rhah\`（创建于 2026-10-24 16:54） |
| 目录骨架 | `Config\`、`Saves\`、`Screenshots\`（照 `profiles\voiced`；`Config`/`Saves` 本来也是 `launcher.js:360-361` 启动时会 mkdir 的两个） |
| ModsConfig | `profiles\rhah\Config\ModsConfig.xml`，**899 字节**，无 BOM，LF 换行 |
| 内容 | `<ModsConfigData>` + `<version>1.6.4871 rev591</version>` + `<activeMods>`（**14 项**）+ `<knownExpansions>`（**6 项**） |
| 变更 | 16:54 首次写入为 13 项（准备文档 §4.1）；随后按 AGENTS.md 硬约束 5 的要求补入第 14 项 `dsh.gamelink`（事件通道），见 §2 与 §4.4 |
| 未创建 | `prefs.json`（可选：`src/prefs.js:159-169` 的 `resolvePrefs` 只在文件存在时读取，缺省用 `DEFAULT_TEST_PREFS`；窗口 1600x900、`runInBackground`、`pauseOnError:false` 是每次强制的，见 `src/prefs.js:25-34`） |

格式模板来源【实测】：`profiles\voiced\Config\ModsConfig.xml` 与 `profiles\vanilla\Config\ModsConfig.xml`。
两者字段集合、字段顺序、大小写与本文件**完全一致**（`version, activeMods, knownExpansions`），`<version>` 也都是 `1.6.4871 rev591`。

---

## 2. 14 项清单（顺序 = 加载顺序；1–13 = 准备文档 §4.1，14 = 事件通道，追加在末尾）

1. `brrainz.harmony`
2. `ludeon.rimworld`
3. `ludeon.rimworld.royalty`
4. `ludeon.rimworld.ideology`
5. `ludeon.rimworld.biotech`
6. `ludeon.rimworld.anomaly`
7. `ludeon.rimworld.odyssey`
8. `erdelf.HumanoidAlienRaces`
9. `Solaris.RatkinRaceMod`
10. `Nanaloveyuki.IrisMenus`
11. `nanaloveyuki.oxygendsl`
12. `brrainz.rimbridgeserver`
13. `nanaloveyuki.ratkin.hungerandhavoc`（RHAH：它的 `loadAfter` 覆盖上面全部内容 mod ⇒ 内容 mod 的最底）
14. `dsh.gamelink`（事件通道；`loadAfter: brrainz.harmony` 已在第 1 位满足；放在最末第 14 位 —— 与 `profiles\voiced` 的「放最后」先例一致，也避开 RHAH 的 `loadAfter` 集合）

**为什么第 14 项不是可选项**：AGENTS.md 硬约束 5 要求「等游戏状态一律事件驱动，禁止 `Start-Sleep`，用 `node src/cli.js live --profile <p> --until '<条件>'`」。
`live` 的事件源就是 DSH_GameLink 写的 NDJSON（`src/live.js:4,46-48` 读 `runs/live/<profile>.ndjson`；`src/launcher.js:435-438` 启动时设置 `GABS_GAME_ID=dsh-rimworld-<profile>` 与 `DSH_LIVE_DIR=<WS>\runs\live`）⇒ 不装它，`live` 只能退回读 `Player.log`，事件驱动的约定就无法按设计执行。

---

## 3. packageId → 本机落点 → 1.6 兼容性核对【全部实测】

搜索范围：`D:\SteamLibrary\steamapps\workshop\content\294100`（838 个子目录）、
`D:\SteamLibrary\steamapps\common\RimWorld\Mods`、`...\RimWorld\Data`、
`E:\...\RimWorld_auto_test\RimworldMods`、`E:\...\RimWorld_auto_test\mods`；共扫到 **913 个 `About.xml`**（字节级 latin1 解码后按 `<packageId>` 取**根节点直属**的那一个，避免把 `modDependencies` 里的依赖误当自身 id）。

| # | packageId | 找到的 About.xml | 名称 / modVersion | supportedVersions 含 1.6？ |
|-|-|-|-|-|
| 1 | `brrainz.harmony` | 工坊 `294100\2009463077\About\About.xml` | Harmony / 3.0.1.0 | ✅（1.2–1.7） |
| 2 | `ludeon.rimworld` | 游戏 `Data\Core\About\About.xml` | （Core，无 name/modVersion） | ✅ 随游戏 1.6.4871（该文件无 `supportedVersions` 段） |
| 3 | `ludeon.rimworld.royalty` | 游戏 `Data\Royalty\About\About.xml` | — | ✅ 1.6 |
| 4 | `ludeon.rimworld.ideology` | 游戏 `Data\Ideology\About\About.xml` | — | ✅ 1.6 |
| 5 | `ludeon.rimworld.biotech` | 游戏 `Data\Biotech\About\About.xml` | — | ✅ 1.6 |
| 6 | `ludeon.rimworld.anomaly` | 游戏 `Data\Anomaly\About\About.xml` | — | ✅ 1.6 |
| 7 | `ludeon.rimworld.odyssey` | 游戏 `Data\Odyssey\About\About.xml` | — | ✅ 1.6 |
| 8 | `erdelf.HumanoidAlienRaces` | 工坊 `294100\839005762\About\About.xml` | Humanoid Alien Races | ✅（0.19–1.7） |
| 9 | `Solaris.RatkinRaceMod` | 工坊 `294100\1578693166\About\About.xml` | NewRatkinPlus | ✅ 1.5 / 1.6 / 1.7 |
| 10 | `Nanaloveyuki.IrisMenus` | 工坊 `294100\3798502474\About\About.xml`（仓库副本 `RimworldMods\iris_menus`，同 1.1.2） | IrisMenus / 1.1.2 | ✅ 1.6 / 1.7 |
| 11 | `nanaloveyuki.oxygendsl` | 工坊 `294100\3817603245\About\About.xml`（仓库副本 `RimworldMods\oxygen_dsl_framework`，同 0.0.1） | Oxygen DSL Framework / 0.0.1 | ✅ 1.6 / 1.7 |
| 12 | `brrainz.rimbridgeserver` | 游戏 `Mods\RimBridgeServer\About\About.xml` | RimBridgeServer / 2.1.1 | ✅ 1.6 |
| 13 | `nanaloveyuki.ratkin.hungerandhavoc` | 工坊 `294100\3807233439\About\About.xml`（仓库副本 `RimworldMods\ratkin_hunger_and_havoc`，同 1.1.4） | 鼠族: 饥与祸 Ratkin: Hunger and Havoc / 1.1.4 | ✅ 1.6 / 1.7 |
| 14 | `dsh.gamelink` | **游戏 `Mods\DSH_GameLink\About\About.xml`**（junction，真实目标 `E:\...\RimWorld_auto_test\mods\DSH_GameLink`；工坊/仓库 `RimworldMods` 里都没有同名包） | DSH GameLink（无 modVersion） | ✅ 1.6（`supportedVersions` 只有 1.6） |

**结论：14 项全部在本机找到，无一「本机未找到」，也无需修正任何 packageId 写法。**

补充核对：
- **依赖闭合【实测】**：`Solaris.RatkinRaceMod` 的 `modDependencies` = `erdelf.HumanoidAlienRaces` + `brrainz.harmony`；
  `erdelf.HumanoidAlienRaces` 无 `modDependencies`（只有 `loadAfter: imranfish.xmlextensions`，不在清单里，是顺序偏好不是依赖）；
  `Nanaloveyuki.IrisMenus` / `nanaloveyuki.oxygendsl` 都只依赖 `brrainz.harmony`；
  `dsh.gamelink` 的 `modDependencies` 只有 `brrainz.harmony`（**没有引入任何新前置**，已在第 1 位）；
  `nanaloveyuki.ratkin.hungerandhavoc` 的 `modDependencies` = `Solaris.RatkinRaceMod, brrainz.harmony, Nanaloveyuki.IrisMenus, Ludeon.RimWorld.Biotech, nanaloveyuki.oxygendsl` ⇒ 14 项清单**在声明层面自给自足**。
- **加载顺序程序化检查【实测】**：读出 8 个第三方 mod 的 `loadAfter`，凡目标也在清单内的，逐对比较索引 ⇒ **0 条违反**（`dsh.gamelink` 的 `loadAfter: brrainz.harmony` 也是 OK）。
- **同 packageId 双副本【实测】**：`Nanaloveyuki.IrisMenus`、`nanaloveyuki.oxygendsl`、`nanaloveyuki.ratkin.hungerandhavoc` 各有 2 份（工坊 + `RimworldMods` 仓库源码）。
  游戏只扫 `游戏\Mods\` 与工坊，**不扫仓库**（`Mods\` 下 6 个 junction 里没有它们）⇒ 运行时只可能看见工坊那份，不算冲突。
  其余 11 项在全部 5 个搜索根里各只有 **1 个**自身 packageId 的声明者。
- 顺带排除一个假警报：工坊 `294100\3523307344`（Ratkin Hair Plus）文本里出现过 `solaris.ratkinracemod`，但那是它 `modDependencies` 里的依赖项，它**自身** packageId 是 `RPowder.RatkinHairPlus` ⇒ 不与鼠族本体撞 id。
- `3497673755`（RHAH 点名的 NewRatkinPlus[1.6]）本机仍**不存在**（`Test-Path` = False），与准备文档 §0 第 2 条一致；只能用 `1578693166`。
- `dsh.gamelink` 的运行形态【实测】：`Mods\DSH_GameLink` 是 junction → `mods\DSH_GameLink`；内含 `Assemblies\DSH_GameLink.dll`（12800 B，写于 2026-10-17 16:26）与 `LoadFolders.xml`（只有 `<v1.6><li>/</li></v1.6>`）⇒ 1.6 下加载根目录内容。

---

## 4. 与准备文档 / 你口头描述的差异

1. **「6 个原版 DLC」的口径不同（不冲突，只是说法）**：本机 `Data\` 是 **1 个 Core（`Ludeon.RimWorld`）+ 5 个 DLC**（Royalty/Ideology/Biotech/Anomaly/Odyssey）= 6 个 Ludeon 条目。
   所以 14 项 = 6 个 Ludeon 条目 + `brrainz.harmony` + 5 个第三方内容 mod + 2 个自动化件（`brrainz.rimbridgeserver`、`dsh.gamelink`）。前 13 项按文档 §4.1 原样写。
2. **大小写按本机 `About.xml` 实际写法**：你写的是 `solaris.ratkinracemod` / `nanaloveyuki.irismenus`（全小写），本机实际是 **`Solaris.RatkinRaceMod`** / **`Nanaloveyuki.IrisMenus`**；其余 4 个第三方项本身就是小写、与实测一致。
   游戏比较 packageId 不区分大小写，但为可读性与对齐 About.xml，写入文件时用了实测写法。**没有出现「文档写法与本机不符」的项。**
3. **`<version>` 用 `1.6.4871 rev591`**：`Version.txt` 里是 `rev590`（老字符串），但 `voiced`/`vanilla` 的 `ModsConfig.xml` 与准备文档 §4.1、风险 ④ 都是 `rev591`；`bike\Config\ModsConfig.xml` 则写着 `rev590`。本文件取 **rev591**（与游戏自己写出的两份一致）。
4. **第 14 项 `dsh.gamelink` 已加入**（准备文档 §4.1 把它列为「可选」并建议放最后；本轮按「不是可选」处理，理由见 §2）。
   位置 = 末尾第 14 位（第 12 位是 `brrainz.rimbridgeserver`，第 13 位是 RHAH，它排在两者之后）。顺序约束：它自己 `loadAfter: brrainz.harmony`，Harmony 在第 1 位 ⇒ 满足；
   RHAH（第 13 位）的 `loadAfter` 列了内容 mod 与 Harmony，另有 4 个**不在本清单里**的目标（`LeZhizhong.PrisonerWorkExpansion`、`cyanobot.toddlers`、`nanaloveyuki.leadyourpet.continued`、`EoralMilk.RatkinGeneExpanded` —— 未装时游戏忽略，不构成缺口），**没有** `dsh.gamelink`，也没有 `loadBefore` 指向它 ⇒ 放在 RHAH 之后不产生循环或倒序。
   （准备文档 §6 第 5 条提到另一种先例「放 harmony 之后」，`voiced` 实际是放最后；本轮取 `voiced` 先例 + 文档 §3.2 建议。）
5. 准备文档说 `prefs.json` 不必写 —— 本轮照此执行，未创建。`--monitor 2` 由命令行传入。

---

## 5. 下一步：起这个 profile（命令已核实）

```powershell
cd 'E:\2026.7以后的新工程\DSH\RimWorld_auto_test'
node src/cli.js start --profile rhah --width 1600 --height 900 --monitor 2
```

依据（【实测】源码，不是猜）：
- `src/cli.js:131-142`：`start` 支持 `--profile` / `--port` / `--minimized` / `--width` / `--height` / `--monitor` / `--doorstop`（`--width/--height/--monitor` 会转成 Number 传给 `startGame`）。
- `src/cli.js:128`：**默认 profile 是 `vanilla`** ⇒ 必须显式写 `--profile rhah`，否则会拿 561 项的全量档去跑。
- 不传 `--doorstop` ⇒ 自动加 `--doorstop-enabled false`（`src/cli.js:141` + `src/launcher.js:419`），桥接能起来的前提。
- `src/launcher.js:401-413`：启动参数含 `-savedatafolder=<profiles\rhah>`（存档/Config/Prefs 全部重定向，碰不到主力档）、`-screen-width/height`、`-monitor`。
- `src/launcher.js:360-361`：`Config\` 与 `Saves\` 已存在时不会重建、不会覆盖 `ModsConfig.xml`（`src/` 里没有任何写 ModsConfig 的代码）。

后续常用：

```powershell
node src/cli.js status --profile rhah    # 看是否在跑 / PID / 端口
node src/cli.js stop   --profile rhah    # 只关本 profile（千万不要加 --force，会强杀用户那一局）
```

一条命令「起 + 开新局 + 出报告」：`node src/cli.js smoke --profile rhah --keep-running`
（`src/cli.js:356-361` 实测存在，`--keep-running` 就是 `flags['keep-running']`。）

事件驱动等待（AGENTS.md 硬约束 5；靠第 14 项 `dsh.gamelink` 提供事件）：

```powershell
node src/cli.js live --profile rhah --until 'kind=colony_started' --timeout 300000
```

依据：`src/cli.js:331-339` 把 `live` 交给 `src/live.js`；`src/live.js:32-39` 的用法文本里 `--until` / `--timeout` 就是这两个开关（`--timeout` 默认 120000 ms）；
事件落点 `runs\live\rhah.ndjson` 由 `src/launcher.js:435-438`（`GABS_GAME_ID=dsh-rimworld-<profile>`、`DSH_LIVE_DIR=<WS>\runs\live`）与 `src/live.js:46-48` 对齐。
挂后台 job 时「job 完成」本身就是推送，不需要 `Start-Sleep`。

---

## 6. 验证了什么 / 没验证什么

**验证了【实测】**
- 写出的 `ModsConfig.xml` 用 `[xml]` 解析成功；根节点 `ModsConfigData`；`version = 1.6.4871 rev591`。
- 字段集合与顺序与 `voiced` 模板逐项相同（`version, activeMods, knownExpansions`）；`activeMods` **14 项**、`knownExpansions` 6 项、**无重复项**。
- 14 项的顺序为「1–13 = 准备文档 §4.1 原序 + 14 = `dsh.gamelink`」，`Compare-Object -SyncWindow 0` 结果为空（完全一致）。
- 文件 **899 字节**、无 BOM、LF 换行（与现有两份 ModsConfig 一致）。
- 14 个 packageId 每个都在本机找到自身 `About.xml`；除 3 个「工坊 + 仓库源码」双份外无多余同 id 声明者；所有第三方项的 `supportedVersions` 都含 `1.6`（`dsh.gamelink` 只有 1.6）。
- 依赖闭合：逐份读了 6 个第三方 `About.xml` 的 `modDependencies`，全部落在 14 项之内（`dsh.gamelink` 只依赖 `brrainz.harmony`，不引入新前置）。
- 加载顺序约束程序化核对：8 个第三方 mod 的 `loadAfter` × 清单内目标，**0 条违反**。
- 起游戏与等事件的命令、开关在 `src/cli.js` / `src/live.js` / `src/launcher.js` 里真实存在（含默认 profile = vanilla 这个坑）。
- 用户侧文件未被触碰：用户 `ModsConfig.xml` 最后写入 2026-10-24 10:00:31、存档目录最后写入 16:10:56，均早于本轮 16:54 的创建动作（本轮没有向那些路径写任何东西）。

**没验证**
- **没有启动游戏** ⇒ 「14 项是否真的够」「RHAH 在 1.6 下能否无红字加载」「`Solaris.RatkinRaceMod`(1578693166) 的 Def 是否满足 RHAH 1.6 的期望」全部未验证。
- `dsh.gamelink` 的 DLL 是 2026-10-17 的构建快照，**没在 1.6.4871 上实跑验证**它能把事件写到 `runs\live\rhah.ndjson`（只能确认 mod 的 About/LoadFolders/Assembly 齐全、CLI 侧读法匹配）。
- **放末位的代价【推测，未实测】**：`LogTap` 的 Harmony patch 在它自己的 `[StaticConstructorOnStartup]` 里挂上；排在第 14 位 ⇒ 它之前 13 个 mod 的静态构造期间打出的红字理论上抓不到（只会留在 `Player.log`）。若要连加载期错误也进事件流，应挪到 Harmony 之后（第 2 位）。本轮按 `voiced` 先例取末位。
- `erdelf.HumanoidAlienRaces` 运行时是否真的需要 `imranfish.xmlextensions`（About 无声明 ⇒ 判非硬依赖，但只是推断；`profiles\bike` 这个可用先例里装了它）。
- `--monitor 2` 是否合适：**没核实副屏分辨率**（若副屏不是 1080p，1600x900 窗口会溢出）。
- `3497673755` 与 `1578693166` 的 1.6 内容是否等价（前者本机不存在）。
- `knownExpansions` 里 6 项的写法是否为游戏「最优」：照抄现有 profile，未与游戏内实际展开列表比对。
