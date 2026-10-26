# HANDOFF-0008 收尾交接：离场来客「该走门还是凿墙」

> 用途：给**全新会话**接手票 0008，自包含，不需要任何对话上下文。
> 快照：2026-10-24 19:30（+08:00）。口径：**实测** = 本机跑过或读到文件；**未验证** = 没跑过。
> 对象：第三方模组 `ratkin_hunger_and_havoc`（RHAH），参考仓 `RimworldMods\ratkin_hunger_and_havoc`（**改代码只在分支上，不动 `main`**）。

## 1. 一句话现状

设计方向已定为「**离场优先走门、凿墙只做兜底**」；分支 `fix/0008-can-dig-exit` HEAD = **`f1e97c0`**；1.7 构建 **0/0**；**游戏内验证已绿**（门 HP 104 与 23 面墙 HP 4485 全程不变、全程没有 `Mine`/`AttackMelee` job、来客从门格走出，见「最新验证结果」一节）。仍未做的是**独立复审**（`95fc93c`/`3c0595d`/`f1e97c0` 都没被复审）、**人工抽验**、**1.6 构建**。另外两条环境结论没变：**画面不能作为位置证据**（1.7 暂停态 `step_game_ticks` 不刷新 pawn 绘制），**多实例并发下 `Player.log` 不可用于断言**。

## 2. 必读文件（按顺序）

1. 票：`E:\2026.7以后的新工程\DSH\RHAH-wayfinder\tickets\0008-neutral-visitors-cannot-open-doors.md` —— 问题定义、`RHAH_DoorPatch` 的 IL 实证、`canDig` 是唯一有效开关；票里已把「走门 vs 凿墙」列为待人类决定的问题。
2. [docs/RHAH-0008-游戏内验证-20261024.md](RHAH-0008-游戏内验证-20261024.md) —— 唯一一次成功绿臂（A 红 / B 绿，tick 2031 离图）。
3. [docs/RHAH-0008-独立评审-20261024.md](RHAH-0008-独立评审-20261024.md) —— 独立评审「有条件通过、0 阻塞、3 应修」，含对着 `Assembly-CSharp.dll` 的原版 IL 核对。
4. [docs/RHAH-0008-修复后复验-20261024.md](RHAH-0008-修复后复验-20261024.md) —— `95fc93c` 红臂 + 换原版 giver 的对照实验（1800 tick 内凿墙离图）。
5. [docs/RHAH-0008-节流修复-20261024.md](RHAH-0008-节流修复-20261024.md) —— `3c0595d` 的改法与反射探针（运行时未验证）。
6. [docs/多实例并发安全-给运行中会话的补充说明-20261024.md](多实例并发安全-给运行中会话的补充说明-20261024.md) —— 多实例串台、禁用 `Player.log` 断言。

背景两份（按需）：[docs/RimWorld-被困机制与离场接入方案-20261024.md](RimWorld-被困机制与离场接入方案-20261024.md)（机制底稿：门被锁 → `Region.Allows` 的 `ByPawn` 分支断链 → 只有 duty 的 `canDig` 能换 `TraverseMode`）；[docs/RHAH-1.7重建报告-20261024.md](RHAH-1.7重建报告-20261024.md)（1.7 构建参数）。

## 3. 代码与提交链

分支 `fix/0008-can-dig-exit`，基点 `6ab44c7`：

- **`9b00cbd` 初版（7 文件 +50/−7）**：新增 `Source\Pawn\JobGiver_RHAH_Departure.cs`（`JobGiver_ExitMapBest` 子类）；`RHAH_VisitorRules.DepartureDuty()` 把 `canDig = true` 收一处；`RHAH_Duties.xml` 1.6/1.7 双写（在 `RHAH_VisitorLeave` 的 `ThinkNode_Priority` 里、排在 `JobGiver_RHAH_Visitor` 之后）。**游戏内绿**：门 HP 104→64→24→打烂（rubble），`curJob = Mine`，**tick 2031 离图**（距 `Leaving` 起点 tick 904 约 1127 tick，文档记作 ≈0.45 游戏小时），0 异常。
- **`95fc93c` 按评审修 3 处（+30/−1）**：`CanWalkOut` 守卫、`CanDepartAlone` 守卫、`IsHashIntervalTick(60)` 节流。**游戏内红**：15090 tick ≈ 6.04 游戏小时（`Leaving` 起点 9634 → 末次采样 24724）门 HP 恒 104，从未拿到挖掘 job，0 异常。同场景把**原版** `JobGiver_ExitMapBest` 换进同一 duty，同一 pawn **1800 tick 内凿墙离图** ⇒ 卡点在我们自己的包装层；最可能是 `IsHashIntervalTick(60)` 窗口撞不上「总有短 job」的 pawn（**推测**）。
- **`3c0595d` 重写节流（1 文件 +62/−6）**：删 `IsHashIntervalTick(60)`，改「结果缓存 + 冷却」——静态 `ConditionalWeakTable<Pawn, Retry>` 只记 `nextSearchTick`，**只在基类返回 null（搜不到）时写**，冷却 2500 tick（1 游戏小时），首检必然放行，冷却期不进基类。1.7 `-t:Rebuild` **0 警告 0 错误**（已实测）；**运行时行为未验证**。
- **`f1e97c0` 优先走门（1 文件 +13/−1）**：只改 `Source\Pawn\RHAH_DoorPatch.cs`——`Lifecycle == Leaving` 时**直接返回原版 `PawnCanOpen` 的结果**（允许开门），`Arriving`/`SeekingFood`/`Fed` 仍禁门；该放行位于 `HostileThreat` 判定**之前**（有敌情时也放行，理由：锁门只会逼他把墙凿出永久缺口）。1.7 `-t:Rebuild` 0 警告 0 错误；**运行时已验证**（见「最新验证结果」一节）。

工作树现状（实测）：`1.6/Assemblies`、`1.7/Assemblies` 下 4 个 DLL 显示 modified，是测试构建产物，**不提交**。

## 最新验证结果（2026-10-24 f1e97c0，绿）

报告：[docs/RHAH-0008-走门验证-20261024.md](RHAH-0008-走门验证-20261024.md)（证据目录 `runs\20261024-RHAH-0008-doors\`）。

- **场景**：真实事件来客（`RHAH_FamineRefugees`）→ **不选收留** → 自己走进 **7×7 封闭房**（23 面墙 + 1 扇木门，门 HP **104**）→ 信件里点 `Refuse`（tick **7577**）→ 存档确认 `lifecycle=Leaving` / `duty=RHAH_VisitorLeave` / `canDig=True`。受测体 `Thing_Ratkin37843` 成年（40.95 岁）、无孕、可走（自己走了 142.5 格进房）。
- **断言①门 HP 全程不变：绿**——从 tick 7577 到离图，木门 `(121,103)` 每次采样都是 **104**（两轮采样：600 tick×8 + 60 tick×60）。
- **断言②墙 HP 不变：绿**——23 面 `Wall` 逐格 HP 之和恒 **4485**、面数恒 23，没有一面墙掉血、没有格子变成瓦砾。
- **断言③从门走出后 `spawned=false`：绿**——tick **9565** 采到他在门格 `(121,103)`、9625 已在门外 `(122,103)`；离图窗口 `(11665, 11725]`，距 `Leaving` 起点 7577 为 **≤4148 tick ≈ 1.66 游戏小时**。
- **断言④异常计数：绿**——`Exception=0` / `TryIssueJobPackage=0` / `JobGiver=0`；桥接内存日志 24 条 error **全是 `spawn_thing` 砌墙/放门造成的 `madeFromStuff but stuff=null`**（测试手法）。
- **关键结论**：两轮 68 个采样里 `curJob` 集合只有 `{TendPatient, Goto}`，**没有任何 `Mine` / `AttackMelee` / `UseVerbOnThing` job** ⇒ **没再凿门也没凿墙**，即「优先走门」成立。
- **回归（无围墙直接 `Refuse`）：绿**——另一名成年来客只挂 `Goto`、**0 个挖掘/攻击 job**、`(7844, 7904]` 离图（距 `Leaving` ≈ **0.9 游戏小时**）。
- **注意**：本轮**没有做同一实验内的新旧 A/B**；A 臂（红侧）结论来自更早一轮——作者原版 DLL **2.64 h 不离图**、门 HP 恒定。

## 4. 两条新问题与建议方向（最重要）

### 4.1 门禁 vs 凿墙：方向可能是错的

实测现象：来客确实凿墙逃出，但**房间有门**，他们不走门。
根因：`Source\Pawn\RHAH_DoorPatch.cs` 对 `Building_Door.PawnCanOpen` 打 Postfix，对「活跃来客」强制 `false`；开关 `famineVisitorsOpenDoors` 默认 `false`（`RHAH_Settings.cs:72`，Scribe 默认同为 false）。判定**不看 lifecycle**，所以 `RestoreDeparture` 把 lifecycle 设成 `Leaving`、duty 设成 `RHAH_VisitorLeave` 之后，禁门照旧生效。
人类判断：禁门本意是防止来客进仓库偷吃，但**人已经在 `Leaving` 了还锁门**，就等于逼他们拆玩家的房子。
⇒ 建议新方向：**`lifecycle == Leaving`（或处于离场 duty）时放开门禁，让他们走门；凿墙只作为「真的没有路」的兜底**。比当前方案小，也更符合直觉。

风险与待答问题：
- 放开门禁后他们会不会在离场途中拿殖民地的食物？**未验证**。机理上离场 duty 只有 `JobGiver_RHAH_Visitor`（`Leaving` 时直接给离图 job），没有进食 job；但路径可能穿过冷库，需人工抽验。
- `RHAH_DoorPatch` 怎么改才只影响 Leaving：最小改法是 Postfix 里**在强制 `false` 之前**加一次提前返回，例如 `if (RHAH_Api.Get(p)?.Lifecycle == RHAH_Lifecycle.Leaving) return;`（`RHAH_Api.Get` 有 null 保护，`RHAH_Lifecycle` 在 Api 程序集，文件已 `using HungerAndHavoc.Api`）。**未编译、未验证**。
- 门一开 `CanReachMapEdge` 变真 ⇒ `canDig` 不再生效；现有 `JobGiver_RHAH_Departure` 可以留作「真没路」兜底，也可以整支回退——这是本决策的第二个岔口，回退时 1.6/1.7 两份 `RHAH_Duties.xml` 的节点必须一起撤。

### 4.2 画面不可信：所有 pawn 的渲染位置都不更新

实测：小人贴图停在屏幕中心，实际已在墙边凿墙（位置用格子回读确认）。人类刚补充：**非鼠族的小人也定在原地一动不动**，而逻辑位置早已跑到别处 ⇒ **渲染层整体与逻辑层脱节**，不是个别贴图问题。
日志里的 `Failed to find any textures at Things/Ratkin/Head/` 是**另一个独立现象**（鼠族部件贴图缺失），**不足以解释**「人停在原地」。
⇒ **截图完全不能作为位置/行为证据**；位置只认格子/坐标回读，行为只认 `curJob`、门 HP、`spawned` 这类数值断言。

## 5. 闸门状态表

顺序是人类定的：构建 → 临时埋点 + 游戏内自动化 → 读日志/断言 → 独立 review → 人工抽验 → 合并。

| 闸门 | 状态 | 依据 |
|-|-|-|
| 构建（**1.7 必过，否则退回**；1.6 也要 0 警告 0 错误） | ✅ | 1.7 `-t:Rebuild` 0 警告 0 错误（`f1e97c0`）；**1.6 未验证**（本机无 1.6 参照程序集） |
| 游戏内自动化 | ✅ | `f1e97c0` 绿（见「最新验证结果」一节）；`9b00cbd` 曾绿、`95fc93c` 红、`3c0595d` 未单独进游戏 |
| 独立 review（新上下文，只给 diff 与验收标准） | ✅ 一轮（覆盖 6ab44c7..f1e97c0）；结论有条件通过；1 阻塞 + 3 应修，见票 0008 |
| 人工抽验（人做） | ❌ | 未做 |

自动化闭环在闸门内部；**不做对抗 agent**；不合格就退回，不打分加权。

## 6. 下一步建议（按优先级）

**先解决 B1（二进制是否随分支提交）**，再决定 S1/S2/S3 是否本轮修，然后人工抽验、起草 PR。（B1 与 S1/S2/S3 的原文见票 0008 的「独立复审（2026-10-24，f1e97c0）」一节；B1 = `f1e97c0` 提交的两个版本 `HungerAndHavoc.dll` 都不含 `JobGiver_RHAH_Departure`，只有工作区未提交的 1.7 汇编有。）

1. **独立复审**：新上下文、只给 `6ab44c7..f1e97c0` 的 diff 与验收标准，不给推理过程。重点看三件事：`95fc93c` 的三处守卫是否已被 `3c0595d` 正确取代、`f1e97c0` 的早退位置（`HostileThreat` 之前）与状态机不可逆性、有没有引入分配/每 tick 开销。**这是当前唯一还缺的代码闸门。**
2. **人工抽验**（人类做）：重点看「**有门时是否真的走门**」「**无围墙时是否正常离场**」「**有敌情时开门是否引入问题**」；顺带看离场路径会不会穿过冷库（观感）。
3. **起草 PR**：建设性、给可执行说明（现象、复现、改法、验证记录、已知边界）；作者是高中生，**不要写成指责**。
4. **1.6 构建仍未验证**（本机无 1.6 参照程序集）：要么找一台有 1.6 程序集的机器补 `-t:Rebuild`，要么在 PR 里如实标注「只验 1.7」。

## 7. 禁止事项

- 不动玩家存档、游戏安装 `D:\SteamLibrary\steamapps\common\RimWorld`、Steam 分支。
- 不 `stop --force`（会强杀全机 RimWorld，含玩家自己在玩的那局）。
- 不在模组仓库 `main` 上提交；改动只留 `fix/0008-can-dig-exit` 或新分支。
- **不提交 DLL**（工作树里那 4 个 modified 的 DLL 是测试构建产物）。
- 不写一次性 `.ps1` / `.py`；用现成的 `node src/cli.js`、`live --until`、`wait --source bridge`。

## 8. 未验证清单

- `3c0595d` 的运行时行为——整条「结果缓存 + 冷却」路径都没在游戏里单独跑过（`f1e97c0` 之后走门路径先命中，凿墙兜底在实战里基本轮不到）。
- **同一实验内的新旧 A/B 未做**：本轮绿臂只跑我们的构建；红臂（作者原版 DLL **2.64 h 不离图**、门 HP 恒定）来自更早一轮 ⇒ 「新 vs 旧」是**跨轮对比**。（走门验证 §8.3）
- 多来客同时抢一扇门 / 幼童 / 被抱持者 / `HostileThreat` 分支**均未构造**；`f1e97c0` 把 `Leaving` 放行放在敌情判断**之前**，这条没有任何验证（回归局里跟来的捕食者不算「对玩家的敌意威胁」）。（走门验证 §8.1–8.2）
- 离图 tick 只有 **60 tick 采样窗口**的精度（`(11665, 11725]`），不是单值。
- **全图建筑 HP 未审计**：回归局用「job 序列里没有任何挖掘/攻击 job」当证据，不是全图前后 HP 对比。（走门验证 §六）
- `TendPatient` job 语义未深究：`Refuse` 后前 1800 tick 一直挂这个 job（房里没有病人），没读 `targetA`，不排除是 RHAH 复用。（走门验证 §8.4）
- 1.6 构建（本机无 1.6 参照程序集）。
- `Moving` / `Consciousness` 数值本机读不到（读数缺失，不等于小人状态有问题）。
- **渲染不更新**：现象与触发条件已定性（暂停态 `step_game_ticks` 不刷新 pawn 绘制，`play_for` 400 ms 即恢复；813×486 是 DPI=192 的虚拟化读数，`-monitor` 被 1.7 忽略），**机制层根因未定位**（没读 1.7/Waylith 反编译）；**Core-only 对照未做**。见 `docs\RimWorld-1.7渲染不更新-诊断-20261024.md`。
- 贴图缺失（`Things/Ratkin/Head/`）是否就是 HAR 缺资源——只有日志线索，未定位。
- **S3（`Reachability` 缓存）未实测**：复审标 `[推测]`，「禁门→放行」后同一 pawn 的「不可达」缓存是否仍生效、封闭基地里会不会反走凿墙，都没在游戏里验证（见票 0008「独立复审」）。
- **S1 的 `JobGiver_GetFood` 回退路径未构造**：冷却期内饥饿的离场者会不会真的走原版取食、绕过模组闸门，没构造过场景。
- **S2 的旧档读回行为未测**：`PawnDuty.canDig` 读档回 false 后凿墙兜底是否真的不生效、旧档困客装上后能否自愈，都没在旧档里试过。

## 附：环境与工具（新会话必看）

- RimWorld **1.7.4985 rev1269**（.NET 10 + Waylith），入口 **`RimWorld.exe`**（旧 `RimWorldWin64.exe` 已删）；安装 `D:\SteamLibrary\steamapps\common\RimWorld`。
- 1.7 构建：先编 `oxygen_dsl_framework` 1.7 产物到 `runs\tmp\odf17\out\`；两个 `Directory.Build.props` 的游戏路径硬编码错误，必须传 `-p:RimWorldDir=...`；`RimWorldManagedDir` = 安装根（1.7 无 `Managed` 目录）；`IrisMenusDll` 要覆盖（`runs\tmp\iris17\IrisMenus.dll`）。
- 测试 profile `rhah`（14 项）：`node src/cli.js start --profile rhah --width 1600 --height 900 --monitor 2`；`node src/cli.js stop --profile rhah`（不加 `--force`）；**必须显式 `--profile`**。
- 覆盖验证：备份工坊 `...\workshop\content\294100\3807233439\` 的 `1.7\Assemblies\HungerAndHavoc.dll`、`HungerAndHavoc.Api.dll`、`1.7\Defs\DutyDefs\RHAH_Duties.xml` → 覆盖 → 验证 → **逐个还原并 SHA256 核对**；Def 必须一起覆盖，否则新 giver 不被实例化。
- 等状态：事件用 `node src/cli.js live --until ...`，游戏内状态用 `node src/cli.js wait --source bridge`；**禁 `Start-Sleep`**；推时间用 `step_game_ticks`（`play_for` 在 forcePause 下静默 0 tick）；速度上限 `Fast`。
- 多实例：1.7 的 `Player.log` 全机共用且被后启动者重写；`status --profile` 会串台（根因 `src\launcher.js:185` 兜底按「最新启动」选记录，可能选到假记录 5288/FakeBridge）。并发时**禁用一切读 `Player.log` 的断言**，只用 `runs\live\<profile>.ndjson`；桥接调用前做「监听端口 PID ↔ 命令行 `-savedatafolder` ↔ 记录 pid/port/token」三重校验。
- 同一时刻只允许一个会话改 `src\`；其他会话在此期间不得 `start/stop`（实测：另一会话改文件改到一半，本会话 `stop` 崩在语法错误上）。
- **测试工程自身的问题**（CLI 在受限沙箱下被 `probePid` 误判、陈旧记录、假记录串台、挂死实例杀不掉、`step_game_ticks` 冻结绘制、`-monitor` 无效、`dotnet test` 被拒、共用 `Player.log`）：见 [docs/测试工程-自身问题清单-20261024.md](测试工程-自身问题清单-20261024.md)。
- 2026-10-24 早些时候的 1.6 结论（旧二进制缺 `Owner/Refresh/Finish/Tick`）**已不适用于现在**——机器已整体升到 1.7，工坊 1.7 DLL 是作者 10-23 的新构建。
