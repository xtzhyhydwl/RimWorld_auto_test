# GameLink 协议 v1（`dsh.gamelink`）

> 一个 mod，两条通道：**事件出向**（NDJSON，已有）＋**命令入向**（本文件，2026-10-25 新增）。
> 真源：`mods/DSH_GameLink/Source/Protocol.cs`（常量）、`CommandChannel.cs`（传输）、`CommandRouter.cs`（分派）、
> `TickDriver.cs`（执行器）；上位机侧 `src/gamelink.js`。改协议**先改 Protocol.cs**，再改这三处 + 本文。

## 1 目录

根目录 `ROOT`：`$DSH_LIVE_DIR` 优先，否则 `<存档目录>/dsh-live`。profile 名取 `GABS_GAME_ID` 去掉 `dsh-rimworld-` 前缀，
缺省 `game`。**两条通道同根、同 profile**。

```
ROOT/<profile>.ndjson          事件出向（已有，8 MB 轮转成 .1）
ROOT/<profile>.link/           命令入向（本文件）
  hello.json                   游戏写，一次性：版本/会话/命令表/探针种类
  state.json                   游戏写，每 100 ms 或状态变化时原子替换
  cmd/<seq>.req                上位机写，一条命令一个文件（kv 行）
  ack/<seq>.ack                游戏写，处理完立刻写，保留最近 200 条
```

**原子写**：两边都先写 `*.tmp` 再 `File.Move/rename`；读侧遇到解析失败就重试（替换窗口只有微秒级）。

## 2 `hello.json`（游戏 → 上位机，能力协商）

```json
{"v":1,"session":"9f3c…","modVersion":"0.2.0","startedAt":1761400000.123,
 "commands":[{"name":"time.advance","mutating":true},{"name":"time.status","mutating":false},
             {"name":"time.cancel","mutating":true},{"name":"time.probe","mutating":false}],
 "probeKinds":["letter","message"],
 "eventKinds":["game_loaded","colony_started","window","heartbeat","log","error","probe_hit"]}
```

- `v` 不匹配 ⇒ 上位机直接报错（不做半吊子兼容）。
- `session` 每次进程启动都不同：上位机用它判断"游戏重启了，这一程作废"。
- `commands[].mutating` = 这条命令会改游戏状态（开源后别人一眼能看出谁动游戏）。
- `commands` 里没有 `time.advance` ⇒ mod 太老，上位机拒绝并提示重建。

## 3 `cmd/<seq>.req`（上位机 → 游戏，**kv 行**）

```
v=1
seq=1761400000123001
cmd=time.advance
toTick=62000
speed=Ultrafast
stopWhen=letter:ThreatBig;message:离开了
watch=letter
forceSpeed=0
maxWallClockMs=1200000
```

- **为什么入向用 kv 不用 JSON**：游戏侧刻意零第三方 dll，手写 JSON 解析器不值得；kv 还能人手写、diff。
  出向用 JSON 是因为**上位机读**，那边解析零成本。
- `seq`：单调递增（上位机用 `毫秒×1000+序号`）。游戏记 `lastSeq`，`seq <= lastSeq` 的文件**直接丢弃**（重发幂等）。
- 处理完游戏删掉 `.req`；删不掉下轮再删。

### 命令参数

| 命令 | 参数 | 说明 |
| --- | --- | --- |
| `time.advance` | `toTick` / `maxTicks` / `speed` / `stopWhen` / `watch` / `forceSpeed` / `maxWallClockMs` | 三者（toTick、maxTicks、stopWhen）至少给一个；`speed` 缺省 `Fast` |
| `time.status` | —— | 只读快照 |
| `time.cancel` | —— | 停 + 暂停 |
| `time.probe` | —— | 自检（补丁状态、消息列表成员名、当前档位/boost、信件/消息条数） |

- `toTick` 必须**大于**当前 tick（tick 不能倒推）；`maxTicks` 是相对上限（防"事件永远不来"）；
  `maxWallClockMs` 默认 20 分钟。
- **`ticks=N` 是相对模式，优先用它**：换算发生在**游戏内**（`toTick = TicksGame + N`）。
  为什么：上位机读到的 tick 与命令生效之间，游戏可能已经跑了几十到几百 tick（Ultrafast 约 11k tick/s，
  50 ms 就是 550 tick），由上位机算 `当前+N` 会偏 —— 实测第一次用 `--to-tick 当前+3000` 只走了 2992 tick。
- `forceSpeed=1`：每 32 tick 把档位压回请求值（压掉事件的 `ForceNormalSpeed`），次数记在 `speedResets`。

## 4 `ack/<seq>.ack`（游戏 → 上位机）

```json
{"v":1,"seq":1761400000123001,"cmd":"time.advance","ok":true,"at":1761400000.9,"tick":20000,
 "result":{"planId":"p1","fromTick":20000,"toTick":62000,"active":true,"requestedSpeed":"Ultrafast", …}}
```

失败：

```json
{"ok":false,"error":{"code":"E_BUSY","message":"已经有一程在跑（planId=p1，to=62000）…"}}
```

**长命令语义**：`time.advance` 的 ack 是「开始/失败」，**不代表跑完**；跑完的判据是 `state.json` 里
`game.run.planId` 相同且 `game.run.active == false`。

### 错误码

**游戏侧**（写在 `ack.error.code`）：

| 码 | 含义 | 谁来处置 |
| --- | --- | --- |
| `E_UNKNOWN_CMD` | 命令名不认识 | 上位机/版本不匹配 |
| `E_BAD_ARGS` | 参数缺失或非法（含 tick 不能倒推、三个停止条件一个都没给） | 调用方 |
| `E_BUSY` | 已有一程在跑 | 调 `time.cancel` 或等 |
| `E_NO_GAME` | 没有正在运行的游戏 | 先开局 |
| `E_HARMONY` | 补丁挂不上（`TickManager.DoSingleTick` 不存在等） | 看游戏红字 + `time.probe` |
| `E_EXTERNAL_PAUSE` | 档位写进去了但游戏仍暂停（教学步骤/模态窗） | 人先处理外部暂停 |
| `E_INTERNAL` | 执行器抛异常（游戏红字里有原文） | 看红字 |

**上位机侧**（`src/gamelink.js` 自己造的，不来自游戏）：

| 码 | 含义 |
| --- | --- |
| `E_NO_ACK` | 命令发出去了但游戏没回 ack（没在泵命令：实例没跑 / mod 没启用 / 卡长事件） |
| `E_CLIENT_TIMEOUT` | 上位机等这一程结束等到超时（游戏侧 `maxWallClockMs` 之外的兜底） |
| `E_SESSION_CHANGED` | `hello.session` 变了：游戏进程重启，这一程作废 |

> 注意：**超时不是 ack 错误**，它是 `state.game.run.stopReason=timeout` —— 命令本身发出去了、也确实被处理了。

## 5 `state.json`（游戏 → 上位机）

```json
{"v":1,"session":"9f3c…","updatedAt":1761400001.2,
 "game":{"v":1,"patch":"ok","hasGame":true,"tick":23049,"paused":true,"speed":"Paused","active":false,
   "run":{"planId":"p1","fromTick":3046,"toTick":23049,"ticksAdvanced":20003,"remaining":0,
          "ticksPerSecond":10972.6,"stopReason":"target","stopDetail":"ticksGame=23049 toTick=23049",
          "stopPayload":null,"speedResets":0,"letterBase":0,"messageBase":0,
          "requestedSpeed":"Ultrafast","appliedSpeed":"Ultrafast","elapsedMs":1823,"active":false}}}
```

- `updatedAt` 超过 5 秒没变 ⇒ 游戏没在泵命令（卡长事件 / 崩了）。
- 主菜单阶段也是**正常返回**：`hasGame:false`、`tick:-1`、`run:null`（不是错误）。
- `run` 为 `null` = 这次会话还没跑过。
- `stopReason`：`target`（到点）/ `probe`（停点判据命中）/ `timeout` / `maxTicks` / `cancelled` / `stalled`（2 秒没进展）。
- 判「真的站在目标上」：`stopReason=target` 且 `game.tick == run.toTick`（`overrun` 恒 0）。

### ⚠ 同一个引擎快照有两处出现，路径不一样

| 从哪读 | tick 的路径 | 说明 |
| --- | --- | --- |
| `state.json`（轮询用） | `game.tick` / `game.run.…` | 外面包了一层通道信息（`session` / `updatedAt`） |
| `ack/<seq>.ack` 的 `result`（`time.status` / `time.cancel` / `time.probe` 的回答） | `result.tick` / `result.run.…` | **没有** `game` 这层：它就是这条命令的返回值 |

`src/gamelink.js` 已经把这两条路都封好了（`status()` 返回 state，`call()` 返回 ack）；手写文件调试时注意别抄错路径。

## 6 探针（`stopWhen` / `watch` 的文法）

`;` 分隔多条，大小写不敏感：

| 写法 | 命中条件 |
| --- | --- |
| `letter` | 任何**新的**信件入栈 |
| `letter:<defName>` | 指定 defName 的信件（`ThreatBig` / `RaidEnemy` …） |
| `letter:<defName>:<包含词>` | 指定信件且标题含词 |
| `message` | 任何**新的**右上角消息（访客离开、季节变化、警报…都是它） |
| `message:<包含词>` | 消息文本含词 —— **第二段是文本，不是 defName** |
| `message:def=<defName>[:<包含词>]` | 按消息大类过滤（`ThreatBig` / `NegativeEvent` / `NeutralEvent`） |

- 同一份探针两种用途：`stopWhen` 命中即停（`stopReason=probe`，`stopPayload` 里是现场）；
  `watch` 命中只写一条事件 `{"kind":"probe_hit","tick":…,"probe":"message:离开了","probeKind":"message","text":…}`。
- **事件行的字段名不许叫 `ts` / `seq` / `kind`**：这三个键由事件通道自己写，重复会造同名键
  （`JSON.parse` 取后一个 ⇒ 事件种类 `probe_hit` 直接丢失，2026-10-25 实测踩到）。探针种类因此叫 `probeKind`。
- **停点语义**：检测发生在 tick N，tick N 跑完，在 tick N+1 的前缀上停（不在 tick 中间抢停，
  否则会留下没跑完的 tick 系统）。所以相对"事件第一次为真"最多滞后 1 tick（实测消息停点与消息 `startingTick` **同 tick**）。
- 目前只有 `letter` / `message` 两种探针（2026-10-25 两轮真机验证）。加一种 = `Probes.cs` 加分支 +
  `Protocol.ProbeKinds` 登记 + 本文补一行（三处，刻意保持显式）。

## 7 上位机入口（谁调、怎么调）

协议是文件，但**人不该手写文件**：`src/gamelink.js` 是唯一真源，CLI 只是它的薄壳。

| 入口 | 用途 |
| --- | --- |
| `import { advanceTo, waitUntil, status, cancel, probe } from './src/gamelink.js'` | 脚本里用（`advanceTo` 等到落地才返回，轮询藏在里面） |
| `node src/cli.js advance --profile <p> --to-tick N` / `--stop-when "letter:ThreatBig"` | 人/代理命令行推时间 |
| `node src/cli.js link --profile <p> --cmd time.status` | 只发一条命令、打 ack（`time.status` / `time.probe` / `time.cancel`） |

```js
const r = await advanceTo({ profile: 'voiced', toTick: 62000, speed: 'Ultrafast' });
// r = {ok, from, to, reached, exact, stopReason, stopPayload, paused, wallClockMs, averageTicksPerSecond, …}
```

## 8 兼容与边界
- **版本**：`v` 整数；不认识的版本一律报错，不做兼容层。
- **失败**：通道内任何异常只写一次 `Log.Error`（游戏红字）然后停通道，**绝不向 Unity Update 冒泡**。
- **并发**：一个 profile 一个实例（既有约定）。同目录多进程写会串，协议不做跨进程互斥。
- **重放**：`lastSeq` 只在内存里；游戏重启后 seq 归零，旧 `.req` 文件会被重新执行 ——
  所以游戏**处理完就删 `.req`**，这条是硬约束。
- **清理**：ack 保留最近 200 条（游戏侧自动 prune，上位机 `pruneAcks()` 可手动清）。

## 9 原子能力清单 + 组合配方（给 agent 看的）

**先看这张表：你能用的原子只有这些。** 复杂操作 = 这些原子的排列组合；表里没有的，就是现在做不到（见 §9.3）。

### 9.1 原子

| # | 想干什么 | 怎么做 | 判据（返回值里看什么） | 动游戏？ |
| --- | --- | --- | --- | --- |
| A1 | 推 N tick | `advance --ticks N` | `ok:true` + `stopReason=="target"` + `ticksAdvanced==N` + `paused:true` | 是 |
| A2 | 推到某个**绝对** tick | `advance --to-tick T` | 同上，`reached==T`、`exact:true` | 是 |
| A3 | 推到**某件事发生**就停 | `advance --stop-when "<探针>" [--max-ticks M]` | `stopReason=="probe"`，`stopPayload` 是现场；跑满则是 `"maxTicks"` | 是 |
| A4 | 只观察、不停 | `advance --ticks N --watch "<探针>"` | `stopReason=="target"`；事件流里多出 `probe_hit` 行 | 是（但按原节奏跑完） |
| A5 | 读当前状态 | `link --profile <p> --cmd time.status` | `result.tick / paused / run.…` | 否 |
| A6 | 中途叫停 | `link --profile <p> --cmd time.cancel` | `result.run.stopReason=="cancelled"`、`paused:true` | 是 |
| A7 | 自检通道/补丁 | `link --profile <p> --cmd time.probe` | `result.patch=="ok"`、`messageSource` 非 null | 否 |
| A8 | 跟读事件（不推时间） | `node src/cli.js live --profile <p> --until '<条件>'` | 命中即返回（exit 0） | 否 |

探针（A3/A4 的 `<探针>`）：`letter` / `letter:<defName>[:<含词>]` / `message` / `message:<含词>` / `message:def=<defName>[:<含词>]`，多条用 `;` 分隔，**先命中者停**。

### 9.2 组合配方（都是真机跑过的形态）

```powershell
# ① 跑 6 游戏小时；中途有信就停（没信就跑到点）
node src/cli.js advance --profile <p> --ticks 15000 --stop-when "letter"

# ② 等「访客离开」这类右上角消息；用 --max-ticks 兜底，看 stopReason 区分"等到"还是"没等到"
node src/cli.js advance --profile <p> --stop-when "message:离开了" --max-ticks 200000

# ③ 分段到检查点：每段一次调用，段间读业务字段（HP / job / 库存），再决定下一段
node src/cli.js advance --profile <p> --ticks 3000
node src/cli.js call rimworld/list_colonists --profile <p>

# ④ 边跑边记录，不打断：跑完再回看发生了什么
node src/cli.js advance --profile <p> --ticks 20000 --watch "letter;message"
#   然后读 runs/live/<p>.ndjson 里的 probe_hit 行（kind=probe_hit / probeKind / text）

# ⑤ 以为要很久、想提前收工：把 advance 挂后台 job，再发 cancel
node src/cli.js advance --profile <p> --ticks 60000 --speed Fast      # 后台
node src/cli.js link --profile <p> --cmd time.cancel
node src/cli.js advance --profile <p> --to-tick <同一个目标>           # 处理完再续跑
```

**组合时只有一条铁律**：一次只跑一程（`E_BUSY` 说明上一程还没结束）；要换目标先 `cancel`，再起新的。

### 9.3 现在**做不到**的事（别绕，直接报）

| 想要 | 现状 | 该怎么办 |
| --- | --- | --- |
| 小人倒地 / 死亡 / 心情 / 换 job 就停 | **没有** pawn 探针 | 报【要加探针】：`Probes.cs` 加一个分支（O(1) 读字段）+ `Protocol.ProbeKinds` 登记 + 本文补一行 + 真机验一次 |
| alert（左侧警报）出现就停 | 没有 alert 探针 | 同上（读 `Find.AlertStack`） |
| 地图上出现/消失某物就停 | 没有 thing 探针 | 同上，但**必须限频**（地图级扫描每 tick 会吃光帧率） |
| 按世界/天气/派系状态停 | 没有对应探针 | 同上 |
| 推时间但不改游戏状态 | 不可能 | 推时间就是让游戏往前走，这是它的定义 |

> 加一条探针的代价：约 20–30 行（读一个字段 + 一个 Matches 判断），不需要改传输/CLI/协议版本。
> 这是**故意留的口子**：事件种类会长，但通道与调用方式不变。
