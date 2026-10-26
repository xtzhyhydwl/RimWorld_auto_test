---
name: rimworld-auto-test-closed-loop
description: RimWorld 测试闭环：把「测一下 X」拆成动作、等待、取证、断言，用四层原子组合成一次可复查的测试；判据落不进①单值比较、也落不进②量词/集合条件就停手报缺原子。要验证游戏内状态（小人 / 物件 / UI / 地图 / 时间 / 事件）或跑一遍游戏测试时使用。
---

# RimWorld 测试闭环

仓库根（下称 `$WS`），所有命令在 `$WS` 下执行。
`$SKILL` = 本文件所在目录（`.dsh/skills/rimworld-auto-test-closed-loop/`），下文 `docs/…` 一律相对 `$SKILL`。

一次测试 = **动作**（改状态）+ **等待**（等到可观测判据）+ **取证**（截图 / 回读 / 事件）+ **断言**（证据 vs 期望）。
你的工作是**把需求拆成这四件事，逐件用现成原子表达**；表达不了就停手；没有匹配的 SOP 就自建一条并登记。

## 1 开工先拿原子清单

```powershell
node src/cli.js atoms --profile <p>        # 算子 / 四类采样源快照字段 / 登记表统计（代码导出，不会漂）
node src/cli.js wait --list --profile <p>  # 已登记场景 + 证据等级
node src/cli.js tools --profile <p>        # 游戏内工具；单个参数用 describe <工具名> --profile <p>
```

参数不确定就先 `describe`。

`tools` / `describe` **要活桥接**；没有实例时离线只剩 `runs\tools.json`，用它要标快照日期。

## 2 开测前自检（不过就停手）

- 起实例：`node src/cli.js start --profile <p> --width 1600 --height 900 --monitor <n>`
- 每次跑之前先用探针探一遍环境事实：`node src/cli.js env-check --profile <p>`（进程 / 桥接 / 激活 mod / 加载顺序 / 能力 / 路径）
- 底座 mod 的真源是仓库根 `base-mods.json`，`start` 前自动补进 profile，对账：`node src/cli.js base-mods --profile <p>`
- 加被测 mod：`node src/cli.js mods add --profile <p> --package-id <id> --apply`，清单存 `profiles/<p>/tested-mods.json`，`mods list` 看漂移

| 检查 | 判据 |
| --- | --- |
| 桥接与归属 | `status --profile <p>` 的 `bridgeResolution.ok = true` |
| 底座 mod 已加载 | `call rimworld/list_mods`：Core / Harmony / RimBridgeServer / **`dsh.gamelink`** / 被测 mod 都 `loadedInSession = true`、`unsatisfiedDependencies` 为空 |
| **GameLink 通道可用（必需）** | `node src/cli.js link --profile <p> --cmd time.status` 拿到 `ok:true` 的 ack。**没有它就不能推时间**（推时间走 GameLink 的命令通道，不再有桥接退路）⇒ 缺了停手，先 `mods add --profile <p> --package-id dsh.gamelink --apply` 并重启实例 |
| 加载顺序健康 | `call rimworld/get_mod_configuration_status`：无 ordering issue、`restartRequired = false` |
| 按需能力 | 要摆/读小人 → `void.charactereditor`；要用 `ce/*` → `call ce/probe` 有返回 |

**按需项缺了也停手**，用【缺原子】模板报。
profile 现状、缺了怎么补：`docs/测试SOP/环境自检.md`。缺底座的实例里跑出来的结论不可信。

## 3 把需求拆成判据（四问）

用户给的是人话结果（"离场了"、"bill 挂上了"），你要等的是**一个可观测字段**。

1. 这个结果改动了什么可观测的东西？物体存在/消失、字段值、UI 元素、事件？
2. 在什么范围可观测？全图 / 该 pawn / 该建筑 / 该进程？
3. 有没有更早更便宜的等价判据？`hasCurrentGame` → `playable` → 业务字段。
4. 只有人眼能判断吗？是 ⇒ 截图交给人，那一部分不进断言。

答完要落成「探针 + 一条可判的条件」（优先单值比较，其次量词/集合）。

需求只能测一半时：能测的那半先测、给证据；测不了的单独报缺原子。

## 4 走一条 SOP

- 普通需求（起实例 → 进图 → 摆场景 → 推时间 → 等判据 → 取证）走 `docs/测试SOP/标准-六阶段.md`，照件填空。
- 特殊流程（多实例对撞、存档读档、长时推进……）看 `docs/测试SOP/README.md`，按"适用条件"挑一条。
- 都不匹配 ⇒ 用 `docs/测试SOP/README.md` 里的骨架自建一条；跑通后登记进去（新增文件 + 目录里一行）。

SOP 每一阶段都有完成判据，判据不过不进入下一阶段。动作/等待失败怎么读：`docs/测试SOP/失败判读.md`。

## 5 硬规则

1. 动作之后用 `wait` 等一个可观测判据；等价判据拿不准时，先用最便宜的那个。
2. 结论来自回读：`wait` 命中、`cli call` 取回字段、截图。动作返回的 `success` 表示请求发出去了。
3. 字段先查表（`wait --list`、`atoms`），新字段实测后回填 `src/conditions.json` 一条数据。多数工具没有 `outputSchema`，查不到就 `call` 一次再回填。
4. 发命令前先看归属：`status --profile <p>` 的 `bridgeResolution.ok` 为 true；有 profile 时以记录为权威（共用日志只作旁证）。
5. 每条结论带证据：产物落 `runs/<日期>-<主题>/`，正文只给路径 + 结论，并标【实测】/【未验证】。
6. 资源：轻 profile 起实例（一个 profile 一个实例）、推时间 `Fast` 封顶、`--force`/`--all` 要用户当场同意。
7. 探东西用 `cli call` / `wait` / `live` 组合；`runs/game-process.*.json` 只由启动器写，离线验证就传假 `client` 对象。
8. **推时间只用 `node src/cli.js advance`**（相对 `--ticks N` / 绝对 `--to-tick N`）：走 GameLink 命令通道、不经桥接，游戏在 tick 边界自己刹车。
   照这样做就不会踩坑：**给相对 tick 数**（别自己读 tick 再算 `当前+N`）、**看返回 `stopReason="target"` + `paused=true`**、**失败就处理外部暂停后用同一参数再跑一次**。
   这样做走不通 ⇒ **停手报【缺原子】**（§6），**不要**改用 `play_for` / `step_game_ticks` / 自己算延时去推时间。
   **可以推到「某件事发生就停」**：`--stop-when "letter[:defName[:含词]]"` / `"message[:含词]"` / `"message:def=<defName>"`；只观察不停用 `--watch`。
   判据：`stopReason=="probe"` + `stopPayload`；跑满没等到是 `"maxTicks"`（**别把"没等到"当"到了"**）。
   现只有**信件 / 右上角消息**两种探针；要别的（pawn 状态 / alert / 地图上出现某物）⇒ 停手报【缺原子】（§6），不要自己绕。
   原子清单 + 组合配方：`docs/GameLink-协议-v1.md` §9（仓根 `docs\GameLink-协议-v1.md`）。
   步骤与判据见 `docs/测试SOP/推时间.md` §0。
   `step_game_ticks` / `play_for` 只剩两个正当用途：**取一个精确 tick 做数值断言**（`step`，不出帧）、**让画面动起来再截图**（`play_for` 400–1000 ms）——它们不是推时间手段。
9. **位置类结论只认坐标回读**（+ 新旧格裁剪对照）；截图只能证「画面上有东西」，不能证「谁在哪」。截图或人眼看之前硬性 `play_for` 400–1000 ms——暂停态 step 之后截的是冻结帧。见 `docs/测试SOP/截图取证.md`。

## 6 停手与缺原子

遇到下面任一情况，**停下报**：

1. 那个"等"落不进①单值比较、也落不进②带量词/集合的条件（分档见下）。
2. 动作需要运行期参数，而登记表表达不了。
3. 依赖主观判断 → 截图交给人。
4. 要 `--force` 强杀全机进程，或要动用户正在玩的那一局。

```
【缺原子】想要：<判据>
- 现在做不到的原因：<探针缺失 / 字段无据 / 采样层没有聚合 / 算子不够>
- 我试过：<命令 + 返回值里的关键字段>
- **字段有没有据**：这个字段（或列表元素）在 `runs\tools.json` 的快照/活的 `describe <tool>` 里有没有实测载荷？没有就先补一次 `call` 把载荷落盘，再谈判据。
- 需要补：<一个只读探针 / 一个字段 / 一个采样参数 / 一个算子>
- 有没有不补也能测的替代方案：<有 ⇒ 先测那一半>
- 阻塞等级：<卡住整条需求 / 只卡一半 / 只是不够便宜>
- 这条判据的可信度：<强判据 / 弱判据（成立不等于需求成立）>
- 补上之后能登记成：<场景名> = <探针> + <条件 JSON 形状>
```

判据能落成①单值比较 → ②带量词/集合的条件（任取一 / 全满足 / 计数 / 列表里没有某元素）→ ③必须新写采样代码。只有落到③才算**补采样层**，①②都算**补数据**（往条件登记表加一条）。能落成①②、但"该比较成立 ≠ 需求成立"的，属**弱判据**，必须同时写清它漏掉什么。

## 7 指针

| 想知道 | 去哪 |
| --- | --- |
| 命令、算子、四类采样源 | 本技能 docs/cli-wait-用法-20261024.md |
| 标准六阶段 SOP | 本技能 docs/测试SOP/标准-六阶段.md |
| 有哪些 SOP、怎么加一条 | 本技能 docs/测试SOP/README.md |
| 环境自检、必备 mod、profile 现状 | 本技能 docs/测试SOP/环境自检.md |
| 小人状态怎么摆、怎么读（Character Editor） | 本技能 docs/测试SOP/角色编辑器.md |
| 时间怎么推（选型 / 分段 / 判据）；**step 不出帧**、step 与 `play_for` 怎么混 | 本技能 docs/测试SOP/推时间.md |
| 截图怎么才可信、位置怎么取证；**截图前 `play_for` 硬规则**、位置证据强度分档 | 本技能 docs/测试SOP/截图取证.md |
| 动作/等待失败怎么判 | 本技能 docs/测试SOP/失败判读.md |
| 探针有哪些、四类采样源怎么读 | 本技能 docs/测试SOP/环境探针.md |
| 日志与事件取证怎么取 | 本技能 docs/测试SOP/日志与事件取证.md |
| 埋点协议（[DSH-TEST] 通道） | 本技能 docs/测试SOP/埋点协议.md |
| 四层原子怎么扩、边界在哪 | 本技能 docs/自动化测试-流程与原子能力清单-20261024.md |

新增知识落**指针文档**，skill 只加指针不加正文。
