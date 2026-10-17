# mod 列表整理 — 结论汇总

> 来源：`data/` 目录（2026-10-04 ~ 2026-10-11「mod 合集整理」阶段的产物，157 个文件 / 10.9 MB）。脚本、中间结果、快照已按用户决定删除，**本文件是唯一保留的结论**。
> 标记：**【实测·当时】**= 当时从 JSON/报告直接读出的数字；**【未验证】**= 现在是否仍成立没验证；**【不确定】**= 当时也没读出来。

## 0. 这段历史是什么

- **时期**：2026-10-04 ~ 2026-10-11，项目最早一阶段。目标是把 Steam 工坊合集「金的自用mod」([3811784951](https://steamcommunity.com/sharedfiles/filedetails/?id=3811784951)) 理顺到能用来做自动化测试。
- **面对的问题（实测·当时）**：本机 mod 装得很乱（工坊 716 个物品 + 本地 `Mods` 252 个，大量是工坊的重复副本）；合集 397 项里只有 22 项真正加载、151 项没订阅；启动后日志反复刷 `Root level exception in OnGUI(): ArgumentNullException (Parameter name: type)`。
- **结局 —— 主要问题都落地了**：① 订阅/补齐：合集里还存在的 387 项全部订上并进了 mod 列表，10 项已下架永久无解；② 崩溃：二分定位到 `vr.missilegirl`，剔除后该异常归零；③ 重复/孤儿：算出清理计划，孤儿清理做了两轮。
- **遗留**：加载顺序当时还是「新加的按合集顺序堆在末尾，等你跑自动排序」；10 项已下架物品若正好是别人的前置则无解（**【未验证】**是否真引发过红字）。10-11 之后配置又缩到 517/516，转为排查 Prepatcher。

## 1. mod 规模底账（全是【实测·当时】，口径不同不可直接相减）

| 口径 | 数字 | 说明 | 来源 |
|---|---:|---|---|
| 工坊物品（磁盘目录数） | 716 | 订阅补齐后的终态 | `data/local-mod-inventory.json` |
| 本地 `Mods` 目录顶层 | 252 | 与工坊大量重复 | 同上 |
| 官方 Data 目录条目 | 6 | 6 个 DLC/官方包 | 同上 |
| 文件系统级条目合计 | 974 | 716+252+6，`items` 键数 | 同上 |
| 去重后 packageId / 重复组 | 735 / 237 | `allPackageIds` / `duplicatePackageIds` | 同上 |
| 无 About.xml 的条目 | 1 | 工坊物品 `3767050969` | 同上 |
| 游戏自己枚举到的 mod | 970 | 工坊 713 + 本地 251 + 官方 6 | `data/game-mods.json` |
| 游戏内「已安装」 | 737 | `installedCount`；未启用 278 | `data/game-state-now.json` |
| 当时启用 | 135 | `game-mods.enabled`（另一个时点是 275） | 同上 |
| 合集条目 | 397 | 页面标 396，Steam API 实际返回 397 | `data/collection-3811784951.json` |
| 启用 mod（合集补齐终态） | 414 | 合集覆盖 387 已加载 + 10 已下架 | `data/订阅与补齐-最终报告.md` |

## 2. 重复与孤儿

**重复副本**（同一 packageId 同时在本地 `Mods` 与工坊）：来源 `data/duplicate-locations.json`、`data/duplicate-move-plan.json`

| 项 | 数字 | 项 | 数字 |
|---|---:|---|---:|
| 本地顶层 / 工坊顶层扫描 | 259 / 728 | 同时存在两处的 packageId | **236 组** |
| 待移到备份的纯数字名副本 | **234 个** | 保留：非数字名（疑似手改） | 3 |
| 保留：目录内文件被改动过 | 1（`2695164414` / `neronix17.tweaksgalore`） | 保留：其他日期的数字名副本 | 0 |

- 判定规则（保守）：目录名纯数字（= 工坊 publishedFileId，批量下载特征）**且** packageId 在工坊也有**且** mtime 是 2026-10-02**且**目录内所有文件都停在 10-02。目标 `D:\ModsDuplicatesBackup_20261004`。
- **是否真的执行了移动：【不确定】**（只剩计划文件，无移动结果记录）。

**孤儿** —— 定义「仍启用，但 About.xml 的 `modDependencies`/`loadAfter` 指向的 mod 已被删除」：

| 轮次 | 结果 |
|---|---|
| 第一轮 10-05 01:41（`data/orphans.json`） | 真孤儿 **0** 个；另有 4 个启用项无法映射到任何目录（`dsh.colonyrecorder`、`cj.rimtalk.toddlers_steam`、`dsh.autotest`、`aota.ratkin.animalera.factionsplus_steam`），属假阴性 |
| 第二轮 `clean-orphans` 10-05 02:07（`data/orphans-latest.json`） | 从 9 份**真实配置快照**汇总出「累积已删除」= **47 个** packageId；本次孤儿 = **0 个** |
| 快照链推出的动作 | 10-05 01:42「剔除孤儿」→ 548；10-05 02:07「清理孤儿」→ 528（再减 20） |

- **那 20 个具体是谁：【不确定】**（`orphans-latest.json` 本次孤儿为 0，与 548→528 的减量对不上）。
- **方法上的坑（可迁移）**：不能把用户自己手存的 `.rml`（新开档 / 鼠族 dev / 冰工厂）算进「历史快照」——那些列表里大量 mod 从没在当前配置启用过，会被误判成「已删除」而产生假孤儿。只取「我生成的 `当前配置*.rml` + `data/backups` 备份」。

## 3. 订阅缺口

| 项 | 数字 | 来源 |
|---|---:|---|
| 需要新订阅的目标 | **141**（= 未安装 151 − 已下架 10） | `data/to-subscribe.json` |
| 对照 ACF 后：已安装 / 已订阅 / 磁盘有目录 | 0 / 0 / 0 —— **141 个全部要新订阅** | `data/subscription-gap.json` |
| 补齐后：订阅数 / 工坊目录数 | 575 → **716** | `data/订阅与补齐-最终报告.md` |
| 再核对：启用 413 个中拿到工坊 ID 的 | 406（工坊 405 + 本地 1 + 官方 6） | `data/verify-available.json` |
| ACF 已安装表条目 / 不在 ACF 的 | 715 / **0** | 同上 |
| 查不到的启用项 | 1 个：自研 mod `dsh.colonyrecorder` | 同上 |

**两条最可迁移的机制结论**（【未验证】是否仍是当前机制，但当时实测成立）：

1. **ACF（`appworkshop_294100.acf`）只是下载账本，订阅列表在 Steam 服务器端。** 只看本地 ACF / 工坊目录，会把「已订阅但没下载」误判成「没订阅」。
2. **用 RimWorld 自带的 `steam_api64.dll` 调 Steamworks `ISteamUGC::SubscribeItem`，复用 Steam 客户端已登录的本地会话即可订阅 —— 不需要账号密码，也不需要 Steam Guard。** 证据链：`GetNumSubscribedItems` 575→716；工具自检「目标已订阅」141/141；工坊目录实际多出 141 个新文件夹；ACF 的 `WorkshopItemsInstalled` 574→593→…

**永久拿不到的 10 个**（作者已删除，Steam API `result=9`）：`2706106189` `2706106456` `2706106672` `2706107774` `2706108037` `2706109583` `2707247216` `3300268222` `3306196369` `3768208853`

## 4. 崩溃二分的结论（这一段最值钱）

- **症状**：日志里 `Root level exception in OnGUI(): ArgumentNullException (Parameter name: type)`，开调试殖民地后每轮刷 **103 次**。
- **判据**：隔离实例 + 调试殖民地跑起来后数日志里该异常次数，**≥5 次算复现，<5 次算消失**。
- **方法**：每轮都从全量配置出发、只剔掉候选集的一半（标准二分），每轮重开隔离实例观察 30s。

| # | 轮次 | 启用数 | OnGUI 异常 | 殖民地 | 备注 |
|---:|---|---:|---:|:--:|---|
| 1 | baseline-full | 562 | 103 | ✅ | 固定剔掉 `seohyeon.autotranslation` |
| 2 | drop-all-hookers | 555 | −1 | ❌ | **作废**：检测到已在运行的 RimWorld（PID 12392） |
| 3 | drop-all-hookers-retry | 555 | −1 | ❌ | **作废**：同上 |
| 1 | drop-dubs-pa | 560 | 202 | ✅ | Dubs PA 被排除 |
| 2 | drop-safe-hookers | 559 | **0** | ✅ | |
| 3 | safe-hooker-1-drop2 | 560 | 103 | ✅ | 只留下 missilegirl |
| 4 | verify-only | 561 | **0** | ✅ | |

来源：`data/auto-bisect-report.md`、`data/auto-bisect-results.json`

- **元凶：`vr.missilegirl`** —— 单独剔掉它后 OnGUI 异常 = 0。**Dubs Performance Analyzer 被排除**（剔它反而升到 202）。
- `seohyeon.autotranslation` **不是**原因：轮次 3 剔了它但仍保留 missilegirl，异常仍是 103。它在日志里表现为 AutoTranslation 反复 429 限流重试（30/40/80s 退避），是**另一个独立的噪音源**，会严重拖慢启动。
- **两条教训（可迁移）**：① **测试实例必须独占** —— 用户自己的游戏在跑时（PID 12392），两轮直接作废，这是当时最贵的浪费；② **「桥接没起来」≠「mod 崩了」**：`data/repro-baseline-full.json` 那次复现 `failed=true`，原因是「等待桥接超过 240000ms」，日志尾部是 429 重试和一串正常的 mod 初始化输出。
- **并列的另一批嫌疑（未闭环）**：`data/xml-class-suspects.json` 找到 3 个 mod 的 `MainButtonDef` 缺/空 `tabWindowClass` —— 正是 OnGUI 抛 `ArgumentNullException(type)` 的典型形态：`kongkim.archotechlogs`(`KK_Archotech_LogShortcut`)、`dubwise.dubsperformanceanalyzer.steam`(`DubsOptimizer`)、`lordfelix.rimcontrol`(`ItemControl_MainButton`)。它们与 missilegirl 的关系**【未验证】**。

## 5. 合集差异：397 项 vs 只加载 22

| 状态 | 数量 | 状态 | 数量 |
|---|---:|---|---:|
| 合集总数 | 397 | 已加载（在当前配置里） | 22 |
| 已安装但未启用 | 224 | 完全没装（未订阅/未下载） | 151（含 10 已下架） |
| 无元数据 | 0 | 大小写不同的同名重复 | 16 组 |
| 已启用但不在合集里 | 28 个 | 其中含 6 个 `ludeon.*` 核心 + 自研/工具 mod | — |

来源：`data/collection-diff.json`、`data/collection-diff-report.md`

- **口径提醒**：`collection-diff.json` 记录的 `liveProfile.activeModCount` 是 **50**（18:35 抓的），但同目录的 `collection-diff-report.md`（19:06 重生成）头部写「当前配置启用 414 个 mod」。两处不一致，**以 JSON 为准**（22 / 224 / 151 / 10）。
- 「合集项大量未加载」是长期状态：9/21 清理前的 485 个配置里仍有 249 项合集 mod 未加载；`backup_ModsConfig`（463 个）里有 252 项。
- **这个结论现在还有效吗：【未验证】。** 但要看性质：22 之所以这么低，是因为那一刻配置只有 **50** 个 mod，**不是合集有问题**；补齐后同一份合集达到 387/397（= 397 − 10 已下架）。

## 6. RML / mod 列表基线

`.rml` 是 RimWorld `ModLists\*.rml` 的存档列表格式（`<savedModList><meta><modIds>` + `<modList><ids>`）。下表就是当时的**状态转换记录**：

| 快照 | 条数 | 时间 | 含义 |
|---|---:|---|---|
| `全量414-含前置补全.rml` | 414 | 10-04 19:53 | 把合集里「已安装未启用」的补进来 |
| `当前配置563-已剔除问题mod.rml` | 563 | 10-04 20:29 | 从 567 剔掉 4 个问题 mod |
| `当前配置562-已剔除missilegirl.rml` | 562 | 10-04 22:22 | **二分结论落地** |
| `当前配置548-剔除孤儿.rml` | 548 | 10-05 01:42 | 按已删前置剔除孤儿 |
| `当前配置528-清理孤儿.rml` | 528 | 10-05 02:07 | 再减 20 个 |
| `当前配置517-测试前基线.rml` | 517 | 10-11 12:00 | 新一轮测试起点 |
| `当前配置516-移除Prepatcher前.rml` | 516 | 10-11 12:25 | 移除前 |
| `当前配置515-移除Prepatcher.rml` | 515 | 10-11 12:25 | 移除 Prepatcher 后（−1） |
| `当前配置516-恢复Prepatcher.rml` | 516 | 10-11 12:37 | 恢复 |
| `当前配置516-装诊断mod前.rml` | 516 | 10-11 12:40 | 装诊断 mod 前 |
| `当前配置517-装诊断mod后.rml` | 517 | 10-11 12:40 | 装诊断 mod 后（+1） |

- `data/modlist-记录/20261011-*.json` = 同一份快照的结构化版（`time / label / gameVersion / count / sha256 / backup / mods`，mods 是**启用顺序**）；`.txt` = 人读版（含行号前缀、带 BOM，PowerShell 默认读会乱码，须按 UTF-8 读）。
- **sha256 的实测结论**：`移除Prepatcher前`、`恢复Prepatcher`、`装诊断mod前` 三份 sha256 **完全相同**（`db2c81b3…`，count 均 516）→ 恢复动作把配置还原到了移除前的**逐字节同一状态**。`测试前基线` 是 `a18d9395…`(517)，`装诊断mod后` 是 `29cdab82…`(517)。**方法可迁移**：用 ModsConfig 的 sha256 判断「配置到底有没有真的变」。
- 10-11 的备份链显示中途出过事：`ModsConfig.备份-我改坏后-20261011-123724.xml` 只有 **634 字节**（正常约 20 KB），随后靠备份恢复；另有 `ModsConfig.备份-写回前-20261011-125306.xml`，说明最后一步是「写回」。

## 7. 健康检查（`data/rml414-health.json`）

**方法**：拿**游戏自己的报告**（`game-mods.json`，游戏看到 970 个 mod，含 `versionCompatible` / `unsatisfiedDependencies`）去核对我那份 414 列表。

| 指标 | 414 列表内 | 全局（970 个 mod） |
|---|---:|---:|
| 游戏完全看不到 | **1**（`OffsetTest.serval`） | — |
| `versionCompatible = false`（游戏会标红） | **3** | **13** |
| 有未满足前置依赖 | **56** | **145** |

- 标红的 3 个：`MarpatNinja.Wheelchairs`(Wheelchairs)、`Cassie.HiddenEyes`(Hidden Eyes)、`Krkr.RocketMan`(RocketMan - Performance Mod)。
- **说明的规律**：① 版本不兼容是少数（414 里 3 个、全局 13 个 ≈ 1.3%）；② **未满足依赖才是主要问题**（414 里 56 个 ≈ 13.5%）；③「游戏看不到」几乎只发生在「一个工坊物品里捆绑多个 mod」（`OffsetTest.serval` 是 `3414843012` 里捆的第二个 mod）。
- 当时的**瓶颈前置**（未满足依赖最集中）：Vanilla Expanded Framework(10)、Nivarian Race: Core(4)、Kiiro Race + Kiiro Race Gene Patch(4)、[NL] Facial Animation - WIP(4)、Milira Race(3)。**现在是否仍成立：【未验证】**（基于当时的 414 列表 + 1.6.4871 rev591）。

## 8. UI 风险排序 —— 以及它为什么失败

- **方法**（`data/added-with-dll.mjs`、`rank-added-by-ui.mjs`）：先在「后加的 154 个 mod」里筛出带 C# DLL 的（只有它们能抛托管异常，纯 XML mod 排除）→ **95 个**；再对每个 DLL 做**二进制字符串扫描**，看是否出现 13 个 UI 符号（`UIRootOnGUI`/`UIRoot_Play`/`MainButtonDef`/`MainTabWindow`/`ITab`/`InspectPane`/`WindowStack`/`OnGUI`/`Dialog_`/`Gizmo`/`Activator.CreateInstance`/`AccessTools`/`Delegate.CreateDelegate`），按命中数降序 —— 目的是给二分排优先级。
- **命中分布（95 个）**：8 个符号 → 2 个 mod；7 → 3；6 → 5；5 → 7；4 → 12；3 → 12；2 → 11；1 → 18；**0 → 25**。
- 榜单前列（`data/added-ui-ranking.json`）：`dubwise.dubsperformanceanalyzer.steam`(8，含"直接挂 `UIRootOnGUI`")、`avius.prisonlabor`(8，34 个 DLL)、`fuu.bloodanimations`(7)、`aoba.framework`(7)、`andromeda.nicehealthtab`(7)。
- **它没能解决问题 —— 这才是这个方法的结论**：真正的元凶 `vr.missilegirl` **既不在这 154 个「后加」里，也不在这 95 个里**（已逐项核对）。而且排名第一的 Dubs PA 在二分里被明确排除。⇒ **① 二进制字符串命中的只是"碰了这条路"，不是"会崩"，不能当嫌疑排序用；② 把范围先限死在「后加的 mod」里本身就是错误前提。**
- `data/added-with-dll.json` 的剩余价值：95 个后加带 DLL 的 mod 名单 + 各自动态 DLL 文件名。

## 9. 桥接能力清单（`data/capabilities-raw.json`）

- 一次 `list_capabilities` 的原始返回：`totalCount = 184` / `returnedCount = 184` / `truncated = false` → 当时桥接暴露 **184 个能力**（**【未验证】现在肯定已变**）。
- 分布：`extension` 59（food-probe 50 + CE bridge 9）、`input` 18、`architect` 18、`lifecycle` 13、`view` 13、`diagnostics` 12、`scripting` 7、`debug_actions` 7、`context_menu` 7、`selection` 6、`profiling` 6、`notifications` 6、`mod_settings` 5、`mod_configuration` 4、`inspection` 3。
- 一句话：桥接的面已经很宽 —— **调试动作执行、UI 点击/布局、截图、存读档、时间推进、mod 配置读写、Lua 脚本**都有。这份 JSON 只是「能做什么」的**清单**，签名/参数随文件删了；**需要时用 `rimbridge.core/diagnostics/list-capabilities` 重新拉即可**。

## 10. 桥接 Lua DSL 参考 ★ —— 唯一「以后一定会再用」的东西

`data/lua-reference.json` **不是快照，是桥接 Lua 子集的语言手册**（`rimbridge/get_lua_reference` 的返回，`version = lua-script-v1`，取于 2026-10-11T03:12:01Z）。要点：

- **定位**：Lua 只是**已有 JSON script runner 的前端**（lowering 目标是 `rimbridge/run_script`，运行时参考 `rimbridge/get_script_reference`），**不是第二套自动化运行时**。
- **四个工具**：`run_lua` / `run_lua_file` / `compile_lua` / `compile_lua_file`；参数 `luaSource`(必需)、`parameters`(可选，注入只读全局 `params`)、`includeStepResults`(默认 true)。
- **推荐工作流**：新脚本形状先 `get_lua_reference` → 用 `compile_lua` 先编译、**看 lowered JSON** 再执行 → 从「local 变量 + `rb.call`/`rb.poll`/`rb.print`/`rb.assert`」这个最小形状起步，能编过再加控制流。
- **支持的子集** —— 语句：`local` 赋值 / 对已存在 local 的单值再赋值 / `if-elseif-else` / `while` / 数值 `for` / `for ... in ipairs()` / `return` / 独立 `print`|`rb.print` / 独立 `rb.call`|`rb.poll`|`rb.assert`|`rb.fail`；表达式：字面量、局部变量、**纯数组或纯对象**的表构造、静态字段访问 `a.b.c`、**静态一基索引** `names[1]`、一元 `-`/`not`、二元算术/比较、`and`/`or`（Lua 式返回操作数）。
- **host 函数只有 7 个**：`rb.call(alias, args?)`、`rb.poll(alias, args?, policy)`（policy 含 `timeoutMs`/`pollIntervalMs`/`timeoutMessage`/JSON 式 `condition`）、`rb.print(msg?, value?)`、`print`、`rb.assert(cond, msg?)`、`rb.fail(msg, value?)`、`ipairs(collection)`。
- **最容易踩的 5 条（背下来）**：

| 不能写 | 为什么 | 改成 |
|---|---|---|
| `count = 1` | v1 拒绝任意全局赋值 | `local count = 1` |
| `names[i]` | 只支持静态一基索引 | `for i, name in ipairs(names) do`，或写死 `names[1]` |
| `rb.call(aliasVar, args)` | capability alias 必须是**字面量字符串** | `rb.call("rimworld/list_colonists", args)` |
| `local n = rb.call(...).result.count` | host 调用**不能嵌在表达式里** | 先 `local snap = rb.call(...)`，再 `snap.result.count` |
| `rb.assert(cond, msgVar)` | 消息必须是**字面量字符串** | `rb.assert(cond, "Expected ...")` |

- **不支持**：`break`、`require`、元表、协程、模块加载、直接 CLR 访问、任意全局赋值、动态表键、动态索引、混合数组/对象的表构造、多重赋值。
- **编译错误码**：`lua.invalid_source`、`lua.invalid_parameters`、`lua.compile_error`、`lua.syntax_error`（MoonSharp 判语法错）、`lua.unsupported_statement`、`lua.unsupported_expression`。
- **返回形状**：`success / message / returned / result / error / output[] / script`（file 版额外带 `scriptPath` / `resolvedScriptPath`）。
- **等待模型（官方推荐）**：**先做一次变更，再用 `rb.poll` 轮询只读能力**直到有界状态成立；**不要循环变更型能力**；优先用现成 wait 工具（`wait_for_game_loaded` / `wait_for_long_event_idle`）；条件要窄、要基于状态字段（`position`/`drafted`/`selectedDesignatorId`/`inEntryScene`）而不是 UI 时序假设；把关键派生值和轮询次数用 `rb.print` 打出来，失败时才有依据。
- **`params` 细节**：只读对象；缺字段读作 `nil`（所以 `params.retryLimit or 6` 可用）；**重赋值/遮蔽 `params` 在编译期被拒**。
- **内置 3 个模式 + 6 个示例**：`wait_for_entry_scene`（轮询 `get_bridge_status` 回主菜单）、`wait_for_colonists_in_area`（`list_colonists` + `countEquals` + `allItems` 判 job）、`bounded_search_and_validate`（有界 while + `find_random_cell_near` 先验证落点再动地图）；另有 `minimal`（列殖民者并返回数量）、`poll_and_assert`、`foreach_selection`、`params_binding`。
- 起点模板（`minimal`）：`local snapshot = rb.call("rimworld/list_colonists", { currentMapOnly = true })` → `local colonists = snapshot.result.colonists` → `rb.assert(colonists ~= nil, "Expected colonists.")` → `return colonists[1]`。

## 11. 其它一次性扫描（已被删的 txt/xml 是什么）

它们**不是结论，是证据** —— 都是「哪个 mod 的哪个方法，在 IL 里调到了哪个游戏 API」的调用点扫描，用途是把嫌疑面缩小。要重查就重跑（用户已接受这个成本）。

| 文件 | 规模 | 是什么 |
|---|---|---|
| `dll-list.xml` | 9,945 行 | PowerShell CliXml 序列化的 DLL 清单（`refdump-*.ps1` 的输入） |
| `droppod-dll-scan.txt`(3,987 行) / `droppod-xml-scan.txt`(259 行) | — | `DropCellFinder::*` 调用点扫描（空投/降落排查）+ 按 mod 列出 XML 里含 `GenStep` 等关键字的文件 |
| `region-dll-scan.txt`(3,654) / `region-scan.txt`(3,226) / `cellfinder-scan.txt`(615) | — | `Room`/`ReachabilityUtility`/`Verse.CellFinder::*` 调用点 |
| `refdump-sweep.txt`(2,613 行) / `terraindef-scan.txt`(1,802 行) | — | `PawnsArrivalModeWorker` / `Verse.TerrainDef::*` 等符号调用点 |
| `worldgen-mods.json` | 84 个 mod | 二进制里含世界生成符号（`WorldGenStep`/`GenStep`/`WorldComponent`/`MapGenerator`/`PlanetLayer`/`PocketMap`），头部是 VFE Core、Geological Landforms、核心/奥德赛 |
| `steam-subscribed-715.txt`(715 行) / `newly-added-vs-touched.txt`(182 行) / `logs/Player-20261004-1919.log`(2.5 MB) | — | 当时已订阅的工坊 ID 清单 / 后加且启用的 mod 名单 / 10-04 19:19 那次启动的完整游戏日志 |

## 12. 已经过期 / 不再成立的东西

**一句话**：凡「**N 个 mod**」的计数，都是**特定配置时点的快照**，配置已变 ⇒ **不再作为任何依据**；凡「当时扫出来的文件/符号/顺序」，都绑定当时的 mod 版本。

| 过期的数字 / 结论 | 为什么过期 |
|---|---|
| **515 / 516 / 517 / 562 / 563 / 567 个 mod** | 全是特定配置时点的快照，且当时还夹着 Prepatcher 与诊断 mod 的增删 |
| **528 / 548 个 mod** ｜ **414 / 275 / 135 / 50 个 mod** | 10-05 孤儿清理后 / 10-04 各中间态的计数，同上一类 |
| 「合集 397 项只加载了 22」 | 是那一刻配置只有 50 个 mod 时的快照；补齐后是 387/397，**不是合集的问题** |
| 「重复副本 236 组 / 待移动 234 个」 | 本地 `Mods` 是否已清理过**【未验证】**；若已清理则整份计划作废 |
| 「累积已删除 47 个、本次孤儿 0 个」 | 10-05 的配置口径 |
| 「56 个未满足依赖 / 3 个版本不兼容」「瓶颈前置」 | 绑定当时的 414 列表 + 游戏版本 1.6.4871 rev591 |
| 「已订阅 716 / 工坊目录 716 / ACF 已安装 715」 | 10-04 的数字，之后必然变 |
| 「桥接 184 个能力」 | 桥接在持续开发，数字必然已变 |
| UI 命中排序、各 DLL/XML 扫描结果、`dll-list.xml` | 绑定当时的 DLL 版本；文件已被删，要重查就重跑 |

**仍然有效的（少数，值得保留）**：

1. **Lua DSL 参考（§10）** —— 语言手册，不随配置变；但「是否仍是 v1、有没有新增语法」**【未验证】**。
2. **机制类**：ACF 只是下载账本、订阅列表在 Steam 服务器端；`SubscribeItem` 可复用本地已登录会话，不需要账号密码/Steam Guard（§3）。
3. **方法论类**：崩溃二分判据（≥5 次算复现）；**测试实例必须独占**；区分「桥接超时」与「真 mod 异常」；**UI 符号命中的启发式排序不能当嫌疑排序**（§8 的反例）；健康检查里「未满足依赖远多于版本不兼容」（§7）；`MainButtonDef` 缺 `tabWindowClass` 是 OnGUI 抛 `ArgumentNullException(type)` 的典型形态（§4）。
4. **配置状态校验法**：`.rml` 格式、用 ModsConfig 的 sha256 判断配置是否真的变了（§6）。

> **退路（有备份，可以放心删）**：原始的 157 个文件（10.9 MB）已删除；删除前的完整备份在 **`tmp\backup-data-20261017.zip`**（仓库内、gitignored）。**随时可删**——结论已全部整理进本文。

## 13. 未验证 / 不确定清单（诚实交代）

| 项 | 状态 |
|---|---|
| 上面所有「现在是否仍成立」的判断 | **未验证**（本文件只做整理，没有重跑任何检查） |
| 234 个重复副本是否真的移走了 ｜ 548→528 那 20 个孤儿是谁 | **不确定** |
| `vr.missilegirl` 崩的具体代码路径 | **不确定**（只定位到 mod 粒度，没定位到方法） |
| 3 个 `MainButtonDef` 缺 `tabWindowClass` 的 mod 与 missilegirl 的关系 | **未验证** |
| 那 10 个已下架物品是否真导致过「缺前置」红字 | **未验证** |
| `OffsetTest.serval` 是否已从配置里删掉 ｜ `dsh.colonyrecorder` 无工坊 ID 是否正常 | **未验证**（后者为自研 mod，推测正常） |
| Lua DSL 是否仍是 `lua-script-v1` ｜ 本地 `Mods` 目录现在的重复情况 | **未验证** |

---

**本文件的定位**：`data/` 里的 157 个文件已删除，结论集中在这里。要再写脚本，**从零重建**（用户已明确接受这个成本）。
