# 状态（索引）

> 本仓库有多条独立的线，各自的状态在各自目录里。**本文只是索引。**
> 协作约定见 [AGENTS.md](AGENTS.md)。

## 现在（2026-10-24）

- **主线：RHAH（鼠族：饥与祸）模组返修**。规划与票据在另一个仓库 `..\RHAH-wayfinder`（`map.md` / `EXECUTION-PLAN.md` / `tickets\0001`–`0010` / `HANDOFF.md`）；本仓库只出测试机器与 `docs\` 分析报告。
- **已完成：1.6 程序集重建**。产物在 `RimworldMods\ratkin_hunger_and_havoc\1.6\Assemblies\HungerAndHavoc.dll`（865,280 B / SHA256 `D38461F0…C5FCC2` / InfoVersion 1.1.4），旧版备份在 `runs\build-backup-20261024\`。**只落仓库，未部署**到游戏/工坊。
- **正在做：票 0008（门 / 离场）**（由另一个会话推进）。方向已定：用原版「被困 → 凿墙/跑掉」的行为，**不传送、不消失**；倒地不强制；被囚/招募/死亡不处理。下一步是接入点调研 → 实施。
- **E12（票 0001）已有存档级证据**：`Letter_0` 仍 `Pending`；`openChoices` 里 `id=1 / I-014 / expireTick 62349` 未结算；`LordJob_RHAH_Visitor` 的 `armedChoiceId=1 / armedAssault=True`（拒绝已生效）。测试档 `profiles\rhah\Saves\probe-choice-record-20261024.rws`。
- **测试环境可用：profile `rhah`（14 项）**。起 `node src/cli.js start --profile rhah --width 1600 --height 900 --monitor 2`；停 `node src/cli.js stop --profile rhah`（**不加 `--force`**）；**必须显式 `--profile`**（默认是 `vanilla` 561 项全量档，且 `profiles\` 不入库）。
- **本轮踩的坑**（细节见 [docs/RimWorld-工具耗时与暂停行为测量-20261024.md](docs/RimWorld-工具耗时与暂停行为测量-20261024.md)）：推游戏时间用 `step_game_ticks`（能穿 forcePause，实测 600/600），`play_for` 在 forcePause 下静默 0 tick；信件对话框开着会冻结游戏时间（30 秒 Δ=0，`ESC` 无效）；`search_debug_actions` 常态 349–437 ms，曾一次 68 秒主线程停摆（占用者未定性）。
- **下一步**：0008 的调研（原版「被困 → 凿墙」接入点）与实施；其后按 `EXECUTION-PLAN.md` §2 的顺序推进其余票。

### 闭环等待（独立一条线，2026-10-24）

- **P0 已落地**：`node src/cli.js wait`（判定只在 `src/wait.js`，本轮只做 `--source bridge`；`src/ui.js` 的旧 `waitUntil` 已删）。真机 9 个用例见 [docs/cli-wait-实测-20261024.md](docs/cli-wait-实测-20261024.md)，语法/15 算子见 [docs/cli-wait-用法-20261024.md](docs/cli-wait-用法-20261024.md)。
- **P2 已落地（2026-10-24 晚）**：多实例串台/并发四项 + 进程身份 + 超时统一。
  一条命令 `status --profile X` 现在会说清「这个桥接凭什么属于我」；`call`/`wait` 不通过校验就**拒发**。
  - 解析：有 profile 时**记录即权威**（不再去共用日志里刮），新增 `resolveBridge()` 四道校验（记录可信 / PID 命令行含本 profile / 端口一致 / token 一致）。
  - 日志：启动时写 `logAnchor`（大小+末尾 256B sha1），被重写就放弃日志解析，不再「读整份」⇒ 不会读到别人的桥接行。
  - 锁：`runs\locks\<profile>.lock`，`openSync('wx')` 原子抢占、死 PID 自动回收；只管**启动临界区**，跑起来后由记录+身份校验负责。
  - 假记录：`recordTrust()` 拒 `pid<=0` / 端口不在 5174-5183 / 哨兵 token（`RIMBRIDGE_ALLOW_FAKE_RECORD=1` 可放行，仅离线用）；记录加 `schema/source/writer`，写入口只留一处。
  - PID 判据：`proc.js` 新增 `probePid`/`describeProcess`/`verifyInstance`（`EPERM` 不再算活着）；
    `stop` 的 `taskkill` 与 `--force` 收集**全部**先验身份，认不出就不杀。
  - 超时：`gabp.js` 唯一常量 `DEFAULT_REQUEST_TIMEOUT_MS = 60_000`，`cli call` 不再写死 300 s。
  - 记录：[docs/多实例并发安全-修复记录-20261024.md](docs/多实例并发安全-修复记录-20261024.md)、[docs/并发安全-进程身份原语与call超时-修复-20261024.md](docs/并发安全-进程身份原语与call超时-修复-20261024.md)。
  - **未验证**：`startGame` 全链路仍未在真机跑（本轮禁止起停游戏）；`stop --force` 没真杀过；`resolveBridge` 的「活实例全部通过」分支没跑到（写完时游戏已被关掉）。
- **P1 已落地（2026-10-24 晚）**：四类等待收敛到**同一个** `waitFor`，`live` 不再有自己的循环。
  - `--source event` / `process` / `bridge-ready` 全部实现（`src\wait-sources.js` 负责取样与聚合：事件窗口、
    按 kind 计数、进程三态、桥接就绪归属校验），`live --until` 现在只是把谓词**翻译**成条件 JSON（`e7dfa7a`）。
  - 条件登记表 `src\conditions.json` + `wait --scenario/--list/--arg`（`f620d7b`）；求值器独立成 `src\condition.js`（`4250ac8`）。
  - 用法见 [docs/cli-wait-用法-20261024.md](docs/cli-wait-用法-20261024.md)（四类来源表）；设计稿 §2.4/§6.1、
    候选数据 [docs/条件登记表-候选数据-20261024.md](docs/条件登记表-候选数据-20261024.md)。
  - **实测**：`live` 的 `--last` / `=` / `~` / 超时打印尾部 / `--new-only` 五项；`wait` 的 process（alive 与不存在）、
    event（NDJSON 计数、日志行 `contains`）、bridge-ready（无游戏时 exit 2）四项。
  - **未验证**：真实游戏事件流（本轮禁止起停游戏）；`docs/cli-wait-实测-20261024.md` 里 9 个用例未重跑；
    `waitForBridge` 仍留在 `launcher.js`（Q2 未定，没搬）。
  - **仍待用户拍板**：Q2 `waitForBridge` 是否搬出 `launcher.js`；Q4 `--scenario` 覆盖规则（当前实现＝禁改身份/允许调参）；
    Q6/Q7/Q8 已按推荐做成**可回退的增量**（`$占位` 参数、`verifiedLevel` 四档、中文场景键）。
- **多实例硬规矩（仍然有效）**：发命令前先证明「我连的是自己那一局」——现在可以直接看 `status` 的 `bridgeResolution`，但**不要**用 `logs` 判断自己死活（共用日志会看到别人的行）。`--force`/`--all` 仍会杀用户的局，只有用户明确要求才用。
- **交接**：[docs/HANDOFF-闭环等待-交接-20261024.md](docs/HANDOFF-闭环等待-交接-20261024.md)。

| 线 | 是什么 | 状态文件 | 当前一句话 |
| --- | --- | --- | --- |
| 自动化测试 | 顶层线：用户用自然语言描述测试流程 → 翻译成游戏操作；测试用的机器（启动器/桥接/调试殖民地/UI 驱动/断言）都属于它 | [自动化测试/STATUS.md](自动化测试/STATUS.md) | 待用户确认（原文件没有单独写这条线的进度，只留下"桥接与测试基础设施"一节） |
| pilot | 自动化测试的特化版：加一个上层自主决策，目标把游戏玩好 | [自动化测试/pilot/STATUS.md](自动化测试/pilot/STATUS.md) | **未放弃，仍在进行中。** 原文只记到 2026-10-04：框架就绪但未部署、Core 357/357、S5 和平模式 10/11（带袭击 9/14）⇒ 之后的进展待用户确认 |
| 共享单车 | 一个 mod（地位与今后每个 mod 相同） | [共享单车/STATUS.md](共享单车/STATUS.md) | **用户已决定暂停这条线。** 设计已定稿，处「Step 0.5 地基探针」前置阶段；运行时实测 0 行 |
| 快照捕捉 | 只读地录制殖民地状态 | [快照捕捉/STATUS.md](快照捕捉/STATUS.md) | 录制器源码已改（runId/run 目录）但**未部署**；三流里 `events-*.jsonl` 至今零真数据 |
| 副官语音 v2 | 一个 mod：把屏幕提示用语音播报出来（新线，2026-10-17 开；**已独立成 `语音副官/` 目录**） | [语音副官/STATUS.md](语音副官/STATUS.md) | **第 1–8 步写完、已三轮真游戏验收、三条 P0 已修并验收（`cc60bb4`）**。下一步：**等用户说「开始优化体验」**（句子排布 / 复读抑制 / 语速停顿） |

## 脚本状态（2026-10-17）

**用户决定：所有脚本全部删除，下次需要时从头重写。**

- `scripts\` 已**清空**（86 个文件；连同更早删掉的 23 个一次性脚本）
- `data\` 已**清空**（157 文件 / 10.9 MB），其结论已整理进 [docs/mod列表整理-结论.md](docs/mod列表整理-结论.md)
- **备份**（在 `tmp\`，随时可删）：`backup-scripts-final-20261017.zip`（86 个）、`backup-scripts-20261017.zip`（109 个，含更早删的）、`backup-data-20261017.zip`（157 个）
- **`src\` 不受影响**：它对 `scripts\` 只有 4 处注释提及，没有 import

**规则**：写任何探测脚本**之前必须先问用户**（见 [AGENTS.md](AGENTS.md)）。

## 线之间的关系（用户 2026-10-17 明确）

- **mod 开发的初期与自动化测试无关**——各做各的
- **到需要测试的阶段**，才把自动化测试拉起来；**缺什么就按标准重新写**
- **pilot 从未放弃**（它是自动化测试的特化版，仍在进行中）
- **共享单车已暂停**

## 原文件
952 行的原 `STATUS.md` 已按线拆到上面四个文件。**内容未删**。
（原文件里"两条独立的线"那段是 2026-10-03 的历史声明，已随内容进入各自文件。）
⚠ 快照捕捉这条线用户本次没提，暂放顶层 `快照捕捉/`，归属待用户定。

## 原文件头部（L1–6，原文照搬）
```text
# 状态

> 这是给下一个会话看的接力文件。每完成一个阶段就更新它。
> 规则见 [docs/协作规范.md](docs/协作规范.md)。

---
```
