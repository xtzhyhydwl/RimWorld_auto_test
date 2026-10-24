---
name: rimworld-auto-test-closed-loop
description: RimWorld 测试闭环（设计 + 执行一体）：把自然语言需求拆成动作与可观测判据、用四层原子（算子 / 采样源 / 场景登记 / 游戏内工具）组合成测试、跑完出可复查报告；缺原子时按模板停手反馈。用户说「测一下这个功能」「验证某个行为」「这个需求怎么测」「跑一遍游戏测试」时使用。它是 rimworld-auto-test 的超集，试用有效后取代之（旧文件暂时保留作细节参考）。
---

# RimWorld 测试闭环（设计 + 执行）

一次测试 = **动作**（改状态）+ **等待**（等到可观测条件）+ **取证**（截图/回读/事件）+ **断言**（证据 vs 期望）。
本文件同时管**设计**（怎么拆、怎么组合、什么时候停手）与**执行**（命令怎么敲）。
旧手册 `rimworld-auto-test`（工具耗时表、调试殖民地细节）暂时保留作细节参考；两者冲突以本文件为准。

仓库：`E:\2026.7以后的新工程\DSH\RimWorld_auto_test`（下称 `$WS`）。**所有命令都在这个目录执行。**

## 0 开工前先拿三样东西

```powershell
node src/cli.js atoms            # 原子清单：算子 / 四类来源的快照字段 / 登记表统计（代码导出，不会漂）
node src/cli.js wait --list      # 已登记的场景 + 证据等级
node src/cli.js tools [关键词]    # 游戏内工具；单个参数用 describe <工具名>
```

**要照着走一遍**：`docs\测试模板-六阶段-20261024.md`（复制即用，给下限；四层原子自由组合给上限）。

## 1 把需求拆成判据（四问，先答完）

用户说的是**人话结果**（"离场了"、"bill 挂上了"、"机制生效了"），你要等的是**一个可观测字段**。

1. 这个结果**改动了什么可观测的东西**？物体存在/消失？字段值？UI 元素？事件？
2. 在**什么范围**可观测？全图 / 该 pawn / 该建筑 / 该进程？
3. 有没有**更早、更便宜**的等价判据？（`hasCurrentGame` → `playable` → 业务字段）
4. **只能人眼看**吗？如果是（"看起来对不对"），截图交给人，**不要**伪装成断言。

答完还写不出「探针 + 一次单值比较」⇒ 走 §6 停手。

**需求只能测一半**：分段交付，能测的先测给证据，测不了的按 §6 报。不许用"截图看起来对"糊过去。

## 2 七条硬规则

1. **禁止 `Start-Sleep` / 固定延时**：动作后一律等可观测判据。
2. **`success: true` 不是证据**：必须回读状态或截图。
3. **先查表再猜字段**：有场景用 `--scenario`；没有才实测，**并回填** `src/conditions.json`。
4. **多实例先归属**：`status --profile <p>` 的 `bridgeResolution.ok` 为 false ⇒ 不发任何命令。
5. **证据落盘** `runs/<日期>-<主题>/`；报告只给路径 + 结论 + `【实测】/【未验证】`。
6. **资源纪律**：只开轻 profile；推时间 `Fast` 上限；`--force`/`--all` 只在用户明确要求时用；
   **不写 `runs/game-process.*.json`**（假记录会让别的会话打进假桥，真发生过）。
7. **不造一次性脚本留仓库**：用 `cli call` / `wait` / `live` 组合。

## 3 六阶段 SOP

### 3.1 起实例

```powershell
node src/cli.js start --profile <p> --width 1600 --height 900 --monitor 2   # 抢一次窗口焦点，先说一声
node src/cli.js wait --profile <p> --source bridge-ready --condition-file ready.json   # {"path":"result.ready","equals":true}
```

**注意**：单独一条 `start` 命令跑完，游戏会连进程树被杀 ⇒ 要**多步驱动**就把启动挂成**保活 job**
（`start` 之后接 `node -e "setInterval(()=>{},900000)"`），收工时先 `stop --profile <p>` 再杀 job。
「端口能连」≠「这个桥接是我的」——归属校验（记录可信 / PID 命令行含本 profile / 端口一致 / token 一致）通过才算。

### 3.2 进门

```powershell
node src/cli.js wait --profile <p> --scenario 已进图可玩
```

`playable` 比 `list_colonists` 更早更稳（没进图时后者连 `colonists` 字段都没有）。

### 3.3 摆场景

用桥接动作类工具（debug action / 建造 / spawn / 选择 / 开面板…，清单以 `tools` 为准）。
**每个动作后必接一次 §3.5**。参数（坐标 / `pawnId` / 目标格）先在 `describe` 里看清。

### 3.4 推进时间

`step_game_ticks`（能穿 `forcePause`）；**不要** `play_for`（暂停下静默 0 tick）。
看 `advancedTicks`：为 0 ⇒ 先去等「无强制暂停窗口」。
**数万 tick 不要一把梭**：分段推 + 每段确认落地 + 段间查有没有被窗口/暂停掐住；单次上限现场测。

### 3.5 等判据

```powershell
node src/cli.js wait --profile <p> --scenario <场景名>                          # 首选
node src/cli.js wait --profile <p> --probe <工具> --condition-file c.json       # 表里没有
node src/cli.js wait --profile <p> --source process --condition-file dead.json  # 进程没了；--verify 连身份一起查
node src/cli.js wait --profile <p> --source event --condition-file cnt.json     # 事件；--new-only 只看新的
```

看退出码，不看文本：

| exit | 含义 | 你该做什么 |
| --- | --- | --- |
| `0` | 命中 | 继续 |
| `2` | 超时没等到 | 看 stderr `missingPath`：**字段一直没出现** ⇒ 前置没做；**值不匹配** ⇒ 判据写错 |
| `1` | 参数错 / 工具名不存在 / 桥接不可用 / 条件写错 | 修命令或修环境，**不要**重试等待 |

### 3.6 取证与断言

截图 / `cli call` 回读 / `runs/live/<p>.ndjson`，落 `runs/`。
断言 = 一句可判真假的话 + 证据路径。写不出可判真假的断言 ⇒ 需求没说清，回去问用户。

## 4 原子四层（缺什么、去哪加）

| 层 | 加一个要动 | 现在 |
| --- | --- | --- |
| 条件算子 | `src/condition.js` 一个 `case` + 文档一行 | 13 叶子 + 3 组合子（合计 16）+ 两个量词 |
| 采样来源 | `src/wait-sources.js` 一个 sampler + `SOURCE_SPECS` 一条 | bridge / event / process / bridge-ready |
| 场景登记 | **只加一条数据** `src/conditions.json`；带运行期参数用 `$占位` + `--arg k=v` | 16 条 |
| 游戏内工具 | mod 侧加 | 以 `cli tools` 为准 |

**算子边界**：算子只做单值比较；窗口（`--window-ms`）、计数（`result.counts.<kind>`）、派生量在采样层算好。
`all/any/not` 已布尔完备 ⇒ 复杂测试应当**树更深**，不是算子更多。
`contains`/`not` 是 Node 侧扩展，`rb.poll` 没有、不能下发游戏内。

## 5 怎么读失败

- `wait` 报「条件写错」= 你的 JSON 有问题（不认识的算子、类型不符、路径没带 `result.`）→ 改条件。
- `wait` 报「拒绝连接 … 记录可信 / PID …」= 多实例归属问题 → 看 `status`，别绕。
- 探针故障（`GABP_TIMEOUT`）= 桥接挂在游戏主线程上 → **重启游戏**，不是重试。
- 断言不成立但状态"看起来对" → 判据选错了，回 §1 重答四问。

## 6 停手规则（最重要）

遇到下面任一情况，**停**下来报，不要硬编：

1. 那个"等"写不成「探针 + 一次单值比较」。
2. 动作需要运行期参数而登记表表达不了（`$占位` 已支持，但还没有真实条目验证过）。
3. 依赖**主观判断** → 截图交给人。
4. 需要 `--force` 强杀全机进程、或要动用户正在玩的那一局。

```
【缺原子】想要：<判据>
- 现在做不到的原因：<探针缺失 / 字段无据 / 需要聚合但采样层没有 / 算子不够>
- 我试过：<命令 + 返回值里的关键字段>
- 需要补：<一个只读探针 / 一个字段 / 一个采样参数 / 一个算子>
- 补上之后能登记成：<场景名> = <探针> + <条件 JSON 形状>
```

**判据**：能写成「探针 + 一次单值比较」的属补数据；写不成单值比较的才属补算子/补采样层。

## 7 反例（都是真踩过的）

| 错误 | 为什么错 | 正确 |
| --- | --- | --- |
| 动作后 `Start-Sleep 2` | 赌时长 | 等可观测判据 |
| `success:true` 当断言 | 调用成功 ≠ 状态变了 | 回读 / 截图 |
| 先等 `result.colonists` | 没进图时字段不存在 | 先等 `playable` |
| `play_for` 推时间 | `forcePause` 下 0 tick | `step_game_ticks` + `advancedTicks` |
| 手写 `runs/game-process.*.json` 造假桥 | 别人的会话会当成真实例（真发生过） | 进程内假对象 |
| 不看 `bridgeResolution` 就发命令 | 可能打进别人的局 | 先 `status` |
| 按 `Player.log` 偏移/ERROR 计数断言 | 1.7 日志全机共用、每次重写 | 桥接探针或 `runs/live/<p>.ndjson` |
| 以为 `--window-ms` 按事件时间 | 已按事件 `ts`；没有 `ts` 的行才用到达时间 | 要"从现在起"用 `--new-only` |
| 长 JSON 贴进对话 | 吃上下文、无法复查 | 落 `runs/`，只给路径 |

## 8 报告

用 `docs\测试模板-六阶段-20261024.md` 填空；产物落 `runs/<日期>-<主题>/`。
每条结论标 `【实测】`（贴输出）/`【未验证】`。

## 9 细节在哪

- 命令与算子逐例：`docs\cli-wait-用法-20261024.md`
- 流程与四层原子：`docs\自动化测试-流程与原子能力清单-20261024.md`
- 设计取舍与待定项：`docs\cli-wait-P1-事件进程等待与条件登记表-设计-20261024.md`
- 旧手册（详细工具清单、调试殖民地、耗时表）：`.dsh\skills\rimworld-auto-test\SKILL.md`
- 多实例并发：`docs\多实例并发安全-给运行中会话的补充说明-20261024.md`
