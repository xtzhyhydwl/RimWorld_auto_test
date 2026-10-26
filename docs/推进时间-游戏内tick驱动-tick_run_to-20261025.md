# 游戏内闭环推时间：`tick/run_to`

> **已被取代（2026-10-25 晚）**：`tick/run_to` 是挂在 RimBridgeServer 伴生 DLL 上的，用户决定"不新开 mod、不挂别人家的东西"，
> 所以引擎已搬进 `mods/DSH_GameLink`，命令改叫 `time.advance`，CLI 入口仍是 `node src/cli.js advance`。
> **当前契约看 [`GameLink-协议-v1.md`](GameLink-协议-v1.md)**；本文保留作为**机制说明与实测数据来源**（tick 边界刹车为什么精确、速率是多少）。

> 一天一句话：**给游戏一条命令「档位 + 目标 tick」，游戏自己在 tick 边界停下来并暂停。**
> 调用方不算时间、不给延时、不分段、不轮询 tick —— 只轮询「跑完了没有」。
> 实现：`bridge\DSH.TickDriver\TickDriver.cs`（RimBridgeServer BridgeTools 伴生 DLL，4 个工具）。
> 实测环境：RimWorld 1.7.4985 / profile `voiced`（13 mod）/ 2026-10-25 本机。数字见 §5，**换机器要重测**。

## 1 它替换掉的是什么

以前的三种推时间手段，每一种都有结构性的毛病：

| 手段 | 毛病 |
| --- | --- |
| 算时间 + 给延时（人或代理自己 `Start-Sleep`） | 期间发生什么完全不知道；冲过检查点只能重开（`runs\2026-10-25-0002-v4\push-ultra3.log` 一次冲出 120000 tick，两个检查点当场丢） |
| 暂停态 `step_game_ticks` | 精确到 tick，但 **1 tick/帧 ≈ 100 tick/s**，而且暂停态不出帧 ⇒ 画面冻结，截图只能是推之前那一帧 |
| `advance --to-tick`（客户端分段） | 落点靠「1 秒片 + 每片回读 + 末尾 `step` 补齐」，安全但**把代价挪到墙钟上**：20000 tick 实测 61.5 s（`runs\2026-10-25-tickdriver\80-advance-compare.txt`）；且每片一次往返、末段几千 tick 只能按 100 tick/s 爬 |

`tick/run_to` 换了一个挂法：**不自己推进 tick，只在 tick 边界上刹车。**

## 2 机制：tick 边界刹车

```
TickManager.DoSingleTick（每执行 1 个 tick 前）
   └─ Harmony prefix（DSH.TickDriver）
        ticksGame >= toTick ?  → 暂停游戏 + 记账(stopReason=target) + return false（吃掉这一 tick）
        否则                   → return true（让游戏自己跑）
```

为什么落点**严格**等于 `toTick`（不是"大概"）：

```
from=33770, to=36770
prefix 第 4000 次：ticksGame=36769 < 36770 → 放行 → ticksGame 变 36770
prefix 第 4001 次：ticksGame=36770 >= 36770 → 不吃这一 tick，暂停
⇒ 最终 ticksGame == 36770，overrun 恒为 0
```

关键取舍与理由：

1. **让游戏自己跑帧，而不是自己调 `DoSingleTick`。** 速度档位、事件、`ForceNormalSpeed`（事件把速度钉回 1×）、
   渲染管线全部保持原版语义；我们只加一个「这一 tick 不准走」的闸。实测跑动期间右侧 TPS 读数（180.0）与实际吻合、
   截图为真实帧（`profiles\voiced\Screenshots\tickdriver-mid-run.png`）。
2. **判据是绝对 tick，不是增量。** 所以断点续跑天然成立：中途被取消/被外部暂停后，用**同一个 `toTick`** 再调一次即可
   （实测：取消于 41214 → 再调 toTick=60000 → 落到 60000）。
3. **刹车在 tick 层，不在帧层。** 一帧里跑多少个 tick 都无所谓（Ultrafast+boost 一帧 ≈ 180 tick），
   照样一个都不越过目标；这也是它与 `play_until_letter`（250 ms 轮询）的本质区别：后者在最坏情况下一整个轮询窗口都看不见。
4. **解不开外面的暂停就不起跑。** 档位写成 Fast 但游戏仍 `Paused`（教学步骤要你动镜头、模态窗等）⇒ 直接返回
   `success:false / blockedByExternalPause:true`，而不是跑起来等超时。

## 3 工具契约（4 条）

| 工具 | 作用 |
| --- | --- |
| `tick/run_to` | 起跑：`{toTick, speed, forceSpeed, stopOnLetter, maxWallClockMs}`，**立即返回**，不阻塞主线程 |
| `tick/status` | 读状态（不动游戏）；顺手收尾「超预算 / 2 秒没进展」的运行 |
| `tick/cancel` | 立刻停 + 暂停，返回最终快照 |
| `tick/probe` | 自检：Harmony 挂上了吗、当前 tick/速度/暂停/信件数、boost 状态 |

`tick/run_to` 参数语义：

- `toTick`（必填）：**绝对** `TicksGame`，必须大于当前 tick（tick 不能倒推）。
- `speed`：`Normal / Fast / Superfast / Ultrafast`，默认 `Fast`。写的是游戏自己的 `TimeSpeed` 档位。
- `forceSpeed`（默认 false）：每 32 tick 把档位压回请求值，压掉事件触发的 `ForceNormalSpeed`；
  压回去几次记在 `speedResets` 里。默认**不压** —— 事件让游戏变慢是原版行为，落点仍然精确。
- `stopOnLetter`（默认 false）：新信件出现的那一 tick 就停（`stopReason=letter`），比轮询没有盲窗。
- `maxWallClockMs`（默认 20 分钟）：超了按 `stopReason=timeout` 停，保留已走的 tick 并暂停。

`tick/status` 关键字段：`active / fromTick / toTick / currentTick / ticksAdvanced / remaining / ticksPerSecond /
stopReason(target|letter|timeout|externalPause|cancelled) / reachedTarget / exact / overrunTicks / speedResets /
letterCountAtStart / letterCountNow / speedBefore / appliedSpeed`。

## 4 调用姿势（两条命令）

```powershell
# ① 起跑（立即返回；参数走 --args-file，PS 5.1 会吞 --args 里的双引号）
node src/cli.js call tick/run_to --profile <p> --args-file runs\<日期>-<主题>\args-run.json
#    args-run.json = {"toTick": 62000, "speed": "Ultrafast"}

# ② 等跑完（等的是游戏内状态，不是固定延时）
node src/cli.js wait --profile <p> --source bridge --probe tick/status `
     --condition-file runs\<日期>-<主题>\cond-active-false.json      # {"path":"result.active","equals":false}

# ③ 读结果
node src/cli.js call tick/status --profile <p>
```

- 成功判据：`stopReason=target` 且 `exact=true`（`overrunTicks=0`）且 `paused=true`。
- `stopReason=externalPause` 不是缺陷：游戏被外面钉住了（教学/模态窗），此时 `currentTick` 就是真实停点，
  解决后**用同一个 `toTick` 再调一次**即可续跑。
- 不要与 `play_for` / `step_game_ticks` / `advance` 同时用：它们在跑自己的时钟，两边会互相打断。

## 5 实测（2026-10-25，1.7.4985 / profile voiced / 本机）

| 用例 | 请求 | 结果 | 证据 |
| --- | --- | --- | --- |
| Fast 短程 | 46 → 3046 | 3000 tick / **16670 ms** / 180 tick/s / `exact=true` / paused | `runs\2026-10-25-tickdriver\10-run-fast.json`、`13-status-fast-done.json` |
| Fast 中程 | 23138 → 29049 | 6000 tick / 33325 ms / 180 tick/s / `exact=true` | `30-run-fast6k.json`、`33-wait-fast6k.txt` |
| Ultrafast（含 boost） | 3046 → 23049 | **20003 tick / 1823 ms / 10972.6 tick/s** / `exact=true`（目标是奇数，排除帧粒度漂移） | `20-run-ultra.json`、`22-wait-ultra.txt` |
| Ultrafast | 20000 → 62000 | 42000 tick / 3056 ms / **13743.5 tick/s** / `exact=true` | `73-wait-62000.txt` |
| 取消 | 41049 → 60000 中途 cancel | 停在 41214，`stopReason=cancelled`，paused | `50-cancel.json` |
| 取消后续跑 | 同 toTick=60000 再调 | 到 60000，`exact=true` | `51-resume-done.txt` |
| 外部暂停 | 起跑时游戏被教学钉住 | `externalPause`，`ticksAdvanced=0`，paused（不是"跑飞了"） | `52-timeout.txt` |
| busy 保护 | 一程未结束时再起一程 | `success:false / busy:true`，附当前 planId | 命令输出（t6 期间） |
| 画面 | Fast 跑动中截图 | 1600×900 真实帧，右侧 TPS 180.0 与 `ticksPerSecond` 一致 | `profiles\voiced\Screenshots\tickdriver-mid-run.png` |
| **对照：老工具** | `advance --to-tick 82000`（62000 起） | 20000 tick / **61539 ms** / 325 tick/s（seg1 900 ms 冲 13972 tick，其余 6028 tick 只能 step，≈100 tick/s） | `80-advance-compare.txt` |

结论：**同样的 20000 tick，`advance` 61.5 s，`tick/run_to` 1.8–5.4 s（11–34 倍）**；而且落点不需要 `step` 兜底，
末段不再按 100 tick/s 爬。短程 Fast 的墙钟与 `advance` 同量级（都被游戏 3× 档位限制），差别在"零往返 + 零越界"。

## 6 政策（沿用仓库既有约束，不因这个工具放宽）

- **默认 `Fast`**；`Superfast` / `Ultrafast` 仍按 AGENTS 硬约束 6：**要用户当次同意**。
  本工具把「快了会冲过检查点」这个风险去掉了，但**没有**去掉「快了会吞掉事件窗口」—— 窗口敏感用例照样要拆成多个 `run_to`。
- 收尾一律暂停（`paused=true`），采样/存档前不用再补 `pause_game`。
- 不碰用户的实例：仍是「一个 profile 一个实例」，`stop` 默认只关自己。

## 7 已知边界 / 未验证

- **【未实测】`stopOnLetter`**：实现走同一条 tick 前缀（与 `target` 同一处 Complete），但没有造出"信件恰好在跑动中到达"的场景
  （调试殖民地前 62000 tick 无信件，`list_letters` 恒 0）。要验就用 `rimworld/execute_debug_action` 的
  `Actions\Do incident…` 系列在跑动中触发一个带信件的 incident 再复跑。
- **【未实测】`blockedByExternalPause` 的拒绝分支**：代码在，但没有稳定复现"档位写成 Fast 仍被钉住"的场景
  （上一次是教学步骤造成的，重启后没再出现）。
- **【实测到的环境怪癖】** `rimworld/take_screenshot` 在本次会话中多次报
  `Timed out waiting for RimWorld to finish writing the screenshot file.`（游戏窗口失焦/被遮挡时更容易出现），
  与驱动无关。要截图就在**跑动中**截（帧是活的），别指望暂停态必成功。
- 一次只允许一程；`save/load` 中途发生会把 `ticksGame` 整体换掉，本驱动不认这种场景（需要用绝对 tick 重起）。
- `tick/run_to` 不检查"这一程跨过了什么"：**它只保证精确到点**。窗口敏感用例仍按
  `推时间.md §2.1` 拆成多个边界。

## 8 文件与部署

| 东西 | 位置 |
| --- | --- |
| 源码 | `bridge\DSH.TickDriver\TickDriver.cs`、`DSH.TickDriver.csproj` |
| 构建 | `cd bridge\DSH.TickDriver; dotnet build -c Release` → `bin\Release\DSH.TickDriver.dll` |
| 部署 | 拷到 `bridge\DSH.TickDriver\DSH.TickDriver.dll`；游戏侧 `D:\...\RimWorld\BridgeTools\DSH.TickDriver` 是指向本目录的 junction |
| 生效 | BridgeTools 在**游戏启动时**加载 ⇒ 改完必须重启实例（`stop` → `start`） |
| 换机器要改 | csproj 里 `GameDir` / `RimBridgeServerDir` / `HarmonyDir`（Harmony = `workshop\294100\2009463077\1.7\Assemblies`） |

依赖：Assembly-CSharp + UnityStubs + `RimBridgeServer.Sdk` + `0Harmony`（都 `Private=false`，运行时不额外带 dll）。
挂载点用 `AccessTools.Method(typeof(TickManager), "DoSingleTick")`（按名字找，游戏改名时 `tick/probe` 会直接报
`TickManager.DoSingleTick 不存在`）。

## 9 下一步（没做）

1. `advance --engine game`：让 `advance` 内部改调 `tick/run_to` + `wait`，对外仍是 `--to-tick <N>` 一个数，
   但墙钟从分钟级掉到秒级、末段不再 step。
2. **游戏内条件停点**：把 `stopOnLetter` 泛化成一张「每 tick 求值的条件表」（信件 / alert 出现 / 小人进某 job /
   需求越界 / 敌对状态翻转），这样「停下来」才真正变成 `推时间.md §2.1` 说的硬件触发，而不是靠轮询。
3. `tick/run_to` 可选 `pauseGame=false`（不停在暂停态，留给需要连续画面的用例）。
