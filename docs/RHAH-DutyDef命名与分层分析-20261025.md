# RHAH DutyDef 命名与分层分析（2026-10-25）

> 只读分析：未改代码、未 commit、未启动游戏。**实测**＝有 `文件:行号` 支撑的读取结果；**判断**＝分析结论。
> 分支 `fix/0008-can-dig-exit` HEAD `9607e8b`；目标文件 1.6/1.7 两份 SHA256 相同（`69DF58FC…03EDCE`，各 703 字节）。

## 结论

1. **定性：主要是分层/粒度问题，命名不当是它的症状。** 我们的节点是「Visitor 走不出图时的一句兜底」，却和「Visitor 整体行为集」并列成同级子节点（`1.7\Defs\DutyDefs\RHAH_Duties.xml:19-20`）。
2. **推荐方案 A（改结构）**：Def 去掉第二个节点，改由 `JobGiver_RHAH_Leave.TryCreate` 在「找不到可走出口」时调这个兜底，Def 回到单节点，与同文件 `RHAH_VisitorSeek` 对称。
3. **代价**：两份 XML 各删 1 行；`JobGiver_RHAH_Leave.cs` 加约 8 行 + 给兜底类加一个 `internal static` 入口；类名/文件名 + `Tests\RHAH_JobGiverTests.cs:24` 断言同步改名。行为变化只有一处：离场者搜不到出口时直接凿/等，不再继续走乞讨/偷窃分支（需游戏内复验）。
4. **不选方案 B（只改名）**：「两个出图实现、靠 Def 里的先后顺序决定谁生效」这个隐患原样保留；有人调换两行顺序就会复现 `9699fca` 修掉的 bug。
5. **保持现状会被误解成**：这个 duty 的动作就是 DigOut；Visitor 与 DigOut 同级；DigOut 是「允许离图」的开关——真正的开关是 `duty.canDig`（`Source\Pawn\RHAH_VisitorRules.cs:898-904`）。

## 目标 DutyDef 结构（实测）

改前（`6ab44c7`）两个 duty **都只有一个节点**，即身份节点 `JobGiver_RHAH_Visitor`；本分支只给 Leave 追加了一行（`git diff 6ab44c7 HEAD` 单行新增）。

| 文件:行号 | DutyDef | thinkNode 根 | 子节点（按顺序） |
|---|---|---|---|
| `1.7\Defs\DutyDefs\RHAH_Duties.xml:3-13`（1.6 同：`1.6\…\RHAH_Duties.xml:3-13`） | `RHAH_VisitorSeek` | `ThinkNode_Priority` | ① `HungerAndHavoc.Pawn.JobGiver_RHAH_Visitor`（:7）② `JobGiver_WanderNearDutyLocation` radius 10（:8-10） |
| `1.7\Defs\DutyDefs\RHAH_Duties.xml:15-23`（1.6 同：`:15-23`） | `RHAH_VisitorLeave` | `ThinkNode_Priority` | ① `JobGiver_RHAH_Visitor`（:19）② `JobGiver_RHAH_DigOut`（:20，**第 2 位＝新增**） |

- 类定义：`Source\Pawn\JobGiver_RHAH_Visitor.cs:11`（`: ThinkNode_JobGiver`）；`Source\Pawn\JobGiver_RHAH_DigOut.cs:11`（`: JobGiver_ExitMapBest`，`sealed`）。
- 两个节点**不是同粒度**：①是整体派发器，②是「走不出去」这一个分支。`ThinkNode_Priority` 按序问，②只在①返回 null 时才被问到。

## `JobGiver_RHAH_Visitor` 的实际职责（实测）

**它是「按身份分的 catch-all 行为集合」，不是具体动作**，内部再分派：`JobGiver_RHAH_Visitor.cs:58-147` 依次尝试 战斗（`:75`）→ 离场 `JobGiver_RHAH_Leave.TryCreate`（`:83`）→ 鼠群（`:88`）→ 母亲喂幼（`:92`）→ 取食（`:100`）→ 啃食（`:109`）→ 乞讨（`:118`）→ 偷窃（`:127`）→ 等食物（`:138`）。每个分支还有 `RHAH_BehaviorGate.*` 闸门（`:98/116/125/136`）。

- 也就是说 **「离场」这条 lane 已经在 Visitor 里了**（`:83-87`，`JobGiver_RHAH_Leave` 自己 `RCellFinder.TryFindBestExitSpot` + `exitMapOnArrival`，`JobGiver_RHAH_Leave.cs:59-85`）。DigOut 与它是同一个职责的第二个实现。
- Gate 事实：`ExitMap` 只在 `Lifecycle == Leaving` 放行（`Source\Identity\RHAH_PawnDefaults.cs:62-63`）；`JobGiver_RHAH_Leave.cs:33`、`LordJob_RHAH_Visitor.cs:756` 等处把 pawn 推到 `Leaving`。
- 使用点（实测）：`RHAH_VisitorRules.DigOutDuty()` 给 duty 设 `canDig=true`（`RHAH_VisitorRules.cs:899-904`），调用点 `LordJob_RHAH_Visitor.cs:643,727,757,790`、`RHAH_BatchAttitude.cs:265`、`RHAH_VisitorStay.cs:134`。
- 生命周期语义：`RHAH_VisitorLeave` **按 pawn 发**——无论 lord 在 Travel/Seek/WaitFood 哪个 toil，只要该 pawn 是 `Leaving` 就换成这个 duty；另外整 lord 进 `LordToil_RHAH_VisitorLeave`（`LordJob_RHAH_Visitor.cs:742-770`）。`leave` 是**汇点 toil**，只有进没有出（`CreateGraph`：`:241/266-271/300` 三处进，无出）。所以这个 duty 的节点表必须能独立完成「这个人要离图」。

## 仓库命名规律（实测 + 统计）

`Source\` 下 `JobGiver_RHAH_*` 共 **12** 个（`grep "class JobGiver_RHAH_"`）：

| 类 | 依据 | 类 | 依据 |
|---|---|---|---|
| `Beg` | 动作 | `Leave` | 动作 |
| `Steal` | 动作 | `Gnaw` | 动作 |
| `Feed` | 动作 | `WaitFood` | 动作（等待求食） |
| `MotherFeed` | 动作+角色限定 | `DropChild` | 动作 |
| `Scavenge` | 动作 | `TailBite` | 动作 |
| `DigOut`（新增） | 动作 | `Visitor` | **身份（唯一一个）** |

- **11/12 按行为动词命名，1/12 按身份命名**。`Visitor` 是身份名，正因为它是派发器（见上节）。
- **只有 4 个类进入 Def**：`Visitor`（DutyDefs 2 次 + `ThinkTreeDefs\RHAH_ThinkTrees.xml:9`）、`Scavenge`（`:20`）、`TailBite`（`:21`）、`DigOut`（`RHAH_Duties.xml:20`）。其余 8 个纯内部实现。
- **分层样板在 ThinkTree**：`RHAH_ThinkTrees.xml:18-22` = `ThinkNode_ConditionalPrisoner`（身份）→ `Scavenge`/`TailBite`（行为）——**身份在容器节点，行为在叶子**。
- 全仓库只有本 mod 定义 DutyDef，共 2 个，改前都是单节点。`Source\AGENTS.md` 明文要求「沿用 `RHAH_` 及原版扩展点的命名形式，一个文件一个主概念」。
- **判断**：仓库惯例是「**Def 层放身份/派发节点，行为动词留给内部**」。我们是「按行为命名」，但把行为名提升到了 Def 层、与身份节点并列 ⇒ 违反的是分层，不只是用词偶然。

## 原版惯例（实测，读本机 `D:\SteamLibrary\steamapps\common\RimWorld\Data`）

- 全 DLC 合计 **108 个 DutyDef**；节点数分布 0:8 / 1:26 / 2:32 / 3:16 / 4:7 / 5:7 / 6:8 / 7:1 / 8:2 / 10:1。8 个 duty 直接把 `JobGiver_*` 当根（如 `ExitMapBest`，`Duties_Misc.xml:516-522`）。
- 节点类去重 **99 个：`JobGiver_*` 78 个 + `ThinkNode_*` 21 个，没有一个是身份名**。身份/角色层在原版由 ThinkTreeDef 承担，不在 DutyDef 里。
- 同粒度并列的例子：`ExitMapNearDutyTarget` = `JobGiver_ExitMapNearDutyTarget` + `JobGiver_ExitMapBest`（`Duties_Misc.xml:541-555`，都是"出图"，窄的先试）；`Steal` = `JobGiver_Steal` + `JobGiver_ExitMapBest`（`:261-273`）。
- **关键：原版把"凿墙出图"当作 exit giver 的参数字段，不是新类**——`<forceCanDig>true</forceCanDig>`（`Steal`，`:269`）、`<forceCanDigIfCantReachMapEdge>`（`Kidnap`，`:240`）、`<forceCanDigIfAnyHostileActiveThreat>`（`TakeWoundedGuest`，`:254-255`）。
- **判断**：原版允许「粗粒度兜底 + 细粒度特例」，但两者必须**同类同粒度**（如两个 exit giver）；不接受"整体派发器 + 单个分支"并列。

## 两个方案对比与推荐

| | 方案 A：收进 Leave（推荐） | 方案 B：只改名 |
|---|---|---|
| 动作 | Def 删 `RHAH_Duties.xml:20`（两份）；`JobGiver_RHAH_Leave.TryCreate` 在 `TryFindBestExitSpot` 失败时调兜底；兜底类改名为 `JobGiver_RHAH_ExitMapFallback`（或 `…LeaveFallback`），说明"走不出去时的出图兜底" | 保留结构，类改名为 `JobGiver_RHAH_VisitorExit` 之类，再加注释解释顺序 |
| 成本 | XML 2 行 → 类名 + 文件名 + `RHAH_JobGiverTests.cs:24` + 两处注释；Leave 约 8 行；一次游戏内复验 | 最小：XML 2 处 + 类名/文件名 + 测试断言，无行为变化 |
| 收益 | Def 回到「一个身份节点」的仓库既有形状，`RHAH_VisitorSeek`/`Leave` 对称；「谁先谁后」写进代码而非 Def 顺序；顺带修掉"离场者搜不到出口时继续乞讨/偷窃"（`JobGiver_RHAH_Visitor.cs:88-143` 的回退链） | 只解决读名字的困惑 |
| 风险 | `JobGiver_RHAH_Leave` 也被无 Lord 路径调用（`RHAH_BatchAttitude.cs:294`），兜底会跟着生效，需确认符合预期 | 换序、给 `RHAH_VisitorSeek` 也加一个节点时静默改行为 |

**推荐 A**，三个理由：① 仓库已有两层样板（`RHAH_ThinkTrees.xml:18-22`：身份容器 + 行为叶子），A 把 DutyDef 拉回同一形状；② 原版把 dig 当参数而不是新类，说明"凿墙"不该有独立门面；③ `JobGiver_RHAH_Leave` 本来就是"怎么离场"的唯一负责人，兜底放它内部符合 `Easy to Change`（改一处）。**更彻底的 vanilla 化（把离场 lane 从 Visitor 剥离、Def 只列 `[Visitor, 出图 giver]`）不选**：`RHAH_VisitorFallback` 无 Lord 路径（`RHAH_ThinkTrees.xml:4-11`）依赖 Visitor 自带离场，剥离会动到所有来客的离场。

## 未验证

- 未读 `Assembly-CSharp.dll`（本机无反编译源）：`JobGiver_ExitMapBest` 里 `canDig` / `forceCanDig*` / `duty.canDig` 的**具体判定代码**未亲见，只用原版 XML 字段存在性与本 mod 注释（`JobGiver_RHAH_DigOut.cs:17-20`）作为间接证据。
- 未跑游戏/未跑测试：方案 A 的行为变化（离场者搜不到出口时不再乞讨/偷窃）、以及 `RHAH_BatchAttitude.cs:294` 无 Lord 路径受兜底影响的实际表现，均未实测。
- `JobGiver_RHAH_DigOut` 在"Visitor 先返回了乞讨/偷窃 job"时是否真被跳过：按代码顺序（`JobGiver_RHAH_Visitor.cs:83` → `:118/127`）**判断**会被跳过，但未在游戏里构造该场景确认。
