# rhah `ModsConfig.xml` 现状核对（2026-10-25 10:05，只读）

对象：`profiles\rhah\Config\ModsConfig.xml`（RimWorld 1.7.4985 rev1269 测试 profile `rhah`）

## 结论

1. **文件被游戏改写**：933 B（`A716E9…`，LF）→ 1074 B（`0E60C2…`，CRLF），写入时间 **10:01:27.169**，就在旧实例 PID 14904 退出、新实例 PID 23268 启动（10:01:28.571，带 `-WaitForExitingPid=14904`）之间 —— 是**退出时保存 + 自我重启**，不是我们改的。
2. **小写化还在，但多了重复**：我们写的 3 条全小写条目原样保留（第 16–18 位）；**但被小写化掉的 3 条混合大小写又回来了**（第 11–13 位）⇒ 同一个 mod 在 `activeMods` 里出现两次（16 → 19 条）。
3. **三条目标 mod 本次会话已加载**：`HAR` / `NewRatkinPlus(Solaris.RatkinRaceMod)` / `IrisMenus` 都出现在 10:02:04 的 "Initializing new game with mods" 列表里，`[RHAH] IrisMenus 1.1.2 pages registered.`（10:01:31），全日志 **0 条** `is not active`。但重复条目让 `ModsConfig.GetModWarnings()` 抛 `ArgumentException: An item with the same key has already been added. Key: erdelf.humanoidalienraces` —— mod 列表页每帧报错 **1957 次**（10:01:51–10:02:00），**桥接 `rimworld/list_mods` 与 `rimworld/get_mod_configuration_status` 现在都调不通**，所以 `enabled` / `loadedInSession` 字段没取到。
4. **不能「直接重启」**：先删掉 3 条混合大小写重复项（**必须在游戏关闭时做**，否则退出又被覆写），再重启；否则 mod 列表页继续刷异常、我们的 env-check/list_mods 也一直坏。

## 文件状态（实测）

| 项 | 值 |
|-|-|
| SHA256 | `0E60C2447D3B17E0ED0F981C21CE2D48CADB5C2EAB1CB9CBE72CD9CE33F86DAD` |
| 字节数 | **1074**（改后版是 933） |
| mtime | **2026-10-25 10:01:27.169**（本地 +08；UTC 02:01:27.169） |
| BOM | **有**（`EF BB BF`），与改后版一致 |
| 换行 | **CRLF ×31，无 lone LF、末尾无换行**（改后版是 LF ×28） |
| 行数 | 32 行 |
| 同目录备份/临时文件 | **无**（`Config\` 只有 KeyPrefs / Knowledge / LastPlayedVersion / Mod_3798502474_IrisMenusMod / Mod_3807233439_RHAH_Mod / ModsConfig / Prefs） |

mtime 对照：本次小写化的落地证据（`runs\modsconfig-fix-20261025\` 与 `docs\rhah-profile-id小写化-20261025.md`）时间戳是 **09:58:47 / 09:59:52**；文件 mtime **10:01:27 晚于它** ⇒ 改写发生在我们改文件之后。

## `activeMods` 原文（19 条，按文件顺序；✅=全小写，❌=含大写）

| # | 条目 | 大小写 |
|-|-|-|
| 1 | `brrainz.harmony` | ✅ |
| 2 | `ludeon.rimworld` | ✅ |
| 3 | `ludeon.rimworld.royalty` | ✅ |
| 4 | `ludeon.rimworld.ideology` | ✅ |
| 5 | `ludeon.rimworld.biotech` | ✅ |
| 6 | `ludeon.rimworld.anomaly` | ✅ |
| 7 | `ludeon.rimworld.odyssey` | ✅ |
| 8 | `brrainz.rimbridgeserver` | ✅ |
| 9 | `dsh.gamelink` | ✅ |
| 10 | `void.charactereditor` | ✅ |
| 11 | `erdelf.HumanoidAlienRaces` | ❌ **重复项（旧写法回来了）** |
| 12 | `Solaris.RatkinRaceMod` | ❌ **重复项（旧写法回来了）** |
| 13 | `Nanaloveyuki.IrisMenus` | ❌ **重复项（旧写法回来了）** |
| 14 | `nanaloveyuki.oxygendsl` | ✅ |
| 15 | `dsh.autotest` | ✅ |
| 16 | `erdelf.humanoidalienraces` | ✅ **我们写的** |
| 17 | `solaris.ratkinracemod` | ✅ **我们写的** |
| 18 | `nanaloveyuki.irismenus` | ✅ **我们写的** |
| 19 | `nanaloveyuki.ratkin.hungerandhavoc` | ✅ |

统计：全小写 **16** 条、含大写 **3** 条（第 11–13 条）。

## `knownExpansions` 原文（5 条，全部 ✅ 全小写，未变）

`ludeon.rimworld.royalty` / `ludeon.rimworld.ideology` / `ludeon.rimworld.biotech` / `ludeon.rimworld.anomaly` / `ludeon.rimworld.odyssey`

`<version>1.7.4985 rev1269</version>` 未变。

## 与「改后版」的差异

改后版已用备份 `runs\modsconfig-fix-20261025\ModsConfig.before.xml` 逐字符复原验证：把 3 条改小写 + 补回 BOM ⇒ **933 B / `A716E9F…0321`**，与任务给的 SHA 完全一致，所以下面的 diff 是字节级可信的。

| 类型 | 具体 |
|-|-|
| 新增 | **3 条**：`erdelf.HumanoidAlienRaces`、`Solaris.RatkinRaceMod`、`Nanaloveyuki.IrisMenus`（就是被小写化掉的老写法） |
| 删除 | 无 |
| 大小写变化 | 3 条小写条目本身没再被改回；变化是「旧写法作为**新条目**又加了一遍」 |
| 顺序变化 | 有：这 3 条旧写法插在 10 号之后（11–13）；我们的 3 条小写被推到 16–18；`dsh.autotest` 从 11 → 15 |
| 字节构成 | +141 B = 3 条新 `<li>` 行 107 字符 + 换行 LF(28)→CRLF(62) 的 +34；BOM 与「末尾无换行」都没变 |

## 桥接查询结果

- 先说拦路：正常路径 **失败**——`node src/cli.js call rimworld/list_mods --profile rhah` ⇒ exit 1，`[错误] 拒绝连接 profile「rhah」的桥接：PID 14904 不存在`（记录还是旧实例的 PID，游戏已自我重启成 PID 23268）。`status --profile rhah` 同时报「桥接可达 127.0.0.1:5174（GABP 握手成功，RimBridgeServer 2.1.1.0）」。
- 于是改用 `src\gabp.js` **直连同一端口 5174（只读，不写文件、不动 UI）**：
  - `rimworld/list_mods` ⇒ `success:false`，`An item with the same key has already been added. Key: erdelf.humanoidalienraces`（`Verse.ModsConfig.GetModWarnings` → `RimBridgeServer.RimWorldModConfiguration.DescribeConfiguration`）
  - `rimworld/get_mod_configuration_status` ⇒ 同上同点抛错
- **因此 `enabled` / `loadedInSession` 字段本次未取到**（不是「false」，是查询直接抛异常）。替代证据（日志实测）：
  - 10:02:04 `Initializing new game with mods:` 列表含 19 条，`erdelf.HumanoidAlienRaces`、`Solaris.RatkinRaceMod`、`Nanaloveyuki.IrisMenus` **各出现两次**（对应那 6 条重复条目），`nanaloveyuki.ratkin.hungerandhavoc` 一次；
  - 10:01:31 `[RHAH] IrisMenus 1.1.2 pages registered.`；
  - 全日志 `is not active` 命中 **0** 次（此前那条 `[RHAH] IrisMenus is not active` 已不再出现）。

## 推断（未实测，标明为推断）

1. 旧实例 PID 14904（09:49:22 启动）内存里是 16 条混合大小写；人类在 mod 列表里把「看起来没启用」的那 3 个勾上，游戏按自己的规范 id（小写）**追加**了 3 条 ⇒ 内存 19 条；退出（10:01:27）整体写回 ⇒ 就是我们看到的文件；随后自我重启（10:01:28）。
2. 混合大小写条目在 1.7 内部是被当作「未解析的遗留字符串」还是「第二次激活」，本轮没读源码，未证实；`GetModWarnings` 用**小写 key** 建字典时撞车是日志钉死的。

## 建议

1. **不要在游戏运行时改这个文件**（退出会覆写；备份文档里已有同样的提醒）。
2. 让游戏**正常退出**（由用户决定何时；本轮按要求没有 kill、没有点 UI），退出后再核一次 SHA/mtime ——大概率会再被写一次。
3. 关闭后二选一（都只在游戏关闭时做）：
   - **A（推荐）**：删掉第 11–13 条混合大小写重复项，保留 16–18 条小写 ⇒ 回到 16 条、全小写（与游戏自己写 vanilla 配置的习惯一致：那 348 条全小写）；
   - **B**：用 `runs\modsconfig-fix-20261025\ModsConfig.before.xml` 回滚，再重做一次小写化。
4. 改完备份当前值，再启动，验收三件事：`list_mods` 能调通、mod 列表页不再刷 `same key` 异常、`activeMods` = 16 条且无大小写重复。

## 未验证

- 未验证游戏退出时是否**再次**改写（本轮游戏仍在运行，日志停在 10:02:06，之后无新行）。
- 未验证三条 mod 是否真被**加载两遍**：`InitNewGame` 列表里各列两次，但日志没有为它们打 `Tried loading mod with the same packageId multiple times`（该报错只对 `wuren.ToddlersFeed`、`Fluffy.FollowMe` 打过），也没有重复 Def 报错。
- 未验证 `GetModWarnings` 崩溃除「mod 列表页 + 桥接 mod 配置查询」外是否影响玩法/存档（1957 条异常全是 `Page_ModsConfig` 的 `DoWindowContents`）。
- 未验证 RHAH 设置页多页是否真恢复（只看到 `IrisMenus pages registered` 与 0 条 `not active`，没进 UI 看）。
- 未读 1.7 源码确认重复条目在 `activeMods` 集合里的语义（推断部分）。
- 未核对其它 profile（`voiced` / `bike` / `vanilla`）是否有大小写重复。
