---
name: rimworld-auto-test-closed-loop
description: RimWorld 测试闭环：把「测一下 X」拆成动作、等待、取证、断言，用四层原子组合成一次可复查的测试；判据写不成单值比较就停手报缺原子。要验证游戏内状态（小人 / 物件 / UI / 地图 / 时间 / 事件）或跑一遍游戏测试时使用。
---

# RimWorld 测试闭环

仓库 `E:\2026.7以后的新工程\DSH\RimWorld_auto_test`（下称 `$WS`），所有命令在 `$WS` 下执行。

一次测试 = **动作**（改状态）+ **等待**（等到可观测判据）+ **取证**（截图 / 回读 / 事件）+ **断言**（证据 vs 期望）。
你的工作是**把需求拆成这四件事，逐件用现成原子表达**；表达不了就停手（§6），没有匹配的 SOP 就按 §4 自建并登记。

## 1 开工先拿原子清单

```powershell
node src/cli.js atoms --profile <p>        # 算子 / 四类采样源快照字段 / 登记表统计（代码导出，不会漂）
node src/cli.js wait --list --profile <p>  # 已登记场景 + 证据等级
node src/cli.js tools --profile <p>        # 游戏内工具；单个参数用 describe <工具名> --profile <p>
```

参数不确定就先 `describe`，再回 §7 查用法文档。

`tools` / `describe` **要活桥接**；没有实例时离线目录只剩 `runs\tools.json`（184 条，2026-10-17 快照），用它要标「快照日期」。

## 2 开测前自检（不过就停手）

- 起实例：`node src/cli.js start --profile <p> --width 1600 --height 900 --monitor <n>`。**必须显式给 `--profile`**，否则起的是默认的满配 `vanilla`。
- 底座 mod 的唯一真源是仓库根 `base-mods.json`：`start` 前自动幂等补进 `profiles/<p>/Config/ModsConfig.xml`（只加不删）；加/查用 `node src/cli.js base-mods --profile <p>`（只读对账）。
- 环境事实别凭记忆，跑 `node src/cli.js env-check --profile <p>`（一次输出进程 / 桥接 / 激活 mod / 加载顺序 / 能力 / 路径）。

| 检查 | 判据 |
| --- | --- |
| 桥接与归属 | `status --profile <p>` 的 `bridgeResolution.ok = true` |
| 底座 mod 已加载 | `call rimworld/list_mods`：Core / Harmony / RimBridgeServer / 被测 mod 都 `loadedInSession = true`、`unsatisfiedDependencies` 为空 |
| 加载顺序健康 | `call rimworld/get_mod_configuration_status`：无 ordering issue、`restartRequired = false` |
| 按需能力 | 要用事件 → `dsh.gamelink`；要摆/读小人 → `void.charactereditor`；要用 `ce/*` → `call ce/probe` 有返回 |

**按需项缺了也停手**，不降级成"用别的方式凑合"。
必备清单的唯一真源是仓库根 `base-mods.json`（`start` 前自动补齐到 profile；`node src/cli.js base-mods [--profile <p>]` 对账）。
profile 现状、缺了怎么补：`docs\测试SOP\环境自检.md`。**在缺底座的实例里跑出来的结论不可信。**

## 3 把需求拆成判据（四问，答完再动手）

用户给的是人话结果（"离场了"、"bill 挂上了"），你要等的是**一个可观测字段**。

1. 这个结果改动了什么可观测的东西？物体存在/消失、字段值、UI 元素、事件？
2. 在什么范围可观测？全图 / 该 pawn / 该建筑 / 该进程？
3. 有没有更早更便宜的等价判据？`hasCurrentGame` → `playable` → 业务字段。
4. 只有人眼能判断吗？是 ⇒ 截图交给人，那一部分不进断言。

答完要落成一句「探针 + 一次单值比较」。落不成 ⇒ §6。

需求只能测一半时：能测的那半先测、给证据；测不了的单独报缺原子。

## 4 走一条 SOP

- 普通需求（起实例 → 进图 → 摆场景 → 推时间 → 等判据 → 取证）走 `docs\测试SOP\标准-六阶段.md`，照件填空。
- 特殊流程（多实例对撞、存档读档、长时推进……）看 `docs\测试SOP\README.md`，按"适用条件"挑一条。
- 都不匹配 ⇒ 用 `docs\测试SOP\README.md` 里的骨架自建一条；跑通后登记进去（新增文件 + 目录里一行）。

SOP 每一阶段都有完成判据，判据不过不进入下一阶段。动作/等待失败怎么读：`docs\测试SOP\失败判读.md`。

## 5 硬规则

1. 动作之后用 `wait` 等一个可观测判据；等价判据拿不准时，先用最便宜的那个。
2. 结论来自回读：`wait` 命中、`cli call` 取回字段、截图。动作返回的 `success` 表示请求发出去了。
3. 字段先查表（`wait --list`、`atoms`），新字段实测后回填 `src/conditions.json` 一条数据。184 个工具里只有 13 个有 `outputSchema`，查不到字段就 `call` 一次再回填。
4. 发命令前先看归属：`status --profile <p>` 的 `bridgeResolution.ok` 为 true；有 profile 时以记录为权威，不要去共用日志里刮。
5. 每条结论带证据：产物落 `runs/<日期>-<主题>/`，正文只给路径 + 结论，并标【实测】/【未验证】。
6. 资源：轻 profile 起实例（一个 profile 一个实例）、推时间 `Fast` 封顶、`--force`/`--all` 要用户当场同意。
7. 探东西用 `cli call` / `wait` / `live` 组合；`runs/game-process.*.json` 只由启动器写，离线验证就传假 `client` 对象。
8. 推时间只按 tick：`step_game_ticks{ticks, pauseFirst:true, timeoutMs:大}`，判据是 `advancedTicks`；推之前先确认 `windowsForcePause = false`。选型、单次预算、分段见 `docs\测试SOP\推时间.md`。
9. 截图或人眼看之前先 `play_for` 400–1000 ms；位置/移动类结论只认坐标回读 + 新旧格裁剪对照（暂停态 step 后 pawn 渲染不刷新）。见 `docs\测试SOP\截图取证.md`。

## 6 停手与缺原子

遇到下面任一情况，**停下报**，不要硬编：

1. 那个"等"写不成「探针 + 一次单值比较」。
2. 动作需要运行期参数，而登记表表达不了。
3. 依赖主观判断 → 截图交给人。
4. 要 `--force` 强杀全机进程，或要动用户正在玩的那一局。

```
【缺原子】想要：<判据>
- 现在做不到的原因：<探针缺失 / 字段无据 / 采样层没有聚合 / 算子不够>
- 我试过：<命令 + 返回值里的关键字段>
- 需要补：<一个只读探针 / 一个字段 / 一个采样参数 / 一个算子>
- 有没有不补也能测的替代方案：<有 ⇒ 先测那一半>
- 阻塞等级：<卡住整条需求 / 只卡一半 / 只是不够便宜>
- 这条判据的可信度：<强判据 / 弱判据（成立不等于需求成立）>
- 补上之后能登记成：<场景名> = <探针> + <条件 JSON 形状>
```

能写成「探针 + 一次单值比较」的属**补数据**（登记表一条）；写不成单值比较的才属**补算子/补采样层**。能写成单值比较、但"该比较成立 ≠ 需求成立"的，属**弱判据**，必须同时写清它漏掉什么。

## 7 指针

| 想知道 | 去哪 |
| --- | --- |
| 命令、算子、四类采样源 | `docs\cli-wait-用法-20261024.md` |
| 标准六阶段 SOP | `docs\测试SOP\标准-六阶段.md` |
| 有哪些 SOP、怎么加一条 | `docs\测试SOP\README.md` |
| 环境自检、必备 mod、profile 现状 | `docs\测试SOP\环境自检.md` |
| 小人状态怎么摆、怎么读（Character Editor） | `docs\测试SOP\角色编辑器.md` |
| 时间怎么推（选型 / 分段 / 判据） | `docs\测试SOP\推时间.md` |
| 截图怎么才可信、位置怎么取证 | `docs\测试SOP\截图取证.md` |
| 动作/等待失败怎么判 | `docs\测试SOP\失败判读.md` |
| 四层原子怎么扩、边界在哪 | `docs\自动化测试-流程与原子能力清单-20261024.md` |

新增知识落**指针文档**，skill 只加指针不加正文。
