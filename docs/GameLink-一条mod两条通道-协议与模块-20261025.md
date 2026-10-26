# GameLink 一 mod 两通道：命令协议 + 模块划分（2026-10-25 讨论稿，未动游戏）

> 用户口径（10-25 晚）：**不新开 mod**。自动化测试这套东西最终要开源出去，用户不该为了用它装一堆 mod；
> 所以全部并进 `dsh.gamelink`，靠**模块划分**分开。定位是：**DSH 是上位机，GameLink 是游戏侧执行器**，
> 上位机发命令、中间模块把命令翻译成游戏行为；想用这套测试的人，在自己的 mod 里按协议**埋点**即可，两边协议对上就能用。

## 0 前置事实（先看清现在是什么）

- `dsh.gamelink` 现在是**单向**的：`LiveChannel` 只往 `<DSH_LIVE_DIR>/<profile>.ndjson` 或
  `<存档目录>/dsh-live/game.ndjson` **追加事件**，**没有任何入口**。要放 tick 驱动，必须加一条反向通道。
- 但它的壳体条件已经齐了：`GameLinkComponent : GameComponent` 已有 `GameComponentUpdate()`（每帧入口）、
  csproj **已经引用 `0Harmony`**（About.xml 也 `modDependencies brrainz.harmony`）⇒ 刹车模块**零新依赖**。
- 通道路径逻辑现成：`DSH_LIVE_DIR` → 否则存档目录 `dsh-live/`，profile 名取 `GABS_GAME_ID` 去前缀。

## 1 一个 mod，五个模块（文件级划分，不是新 mod）

| 模块 | 文件 | 职责 | 状态 |
| --- | --- | --- | --- |
| `Events` | `LiveChannel.cs` / `EventLine.cs` / `LogTap.cs` | 出向：游戏 → 上位机（NDJSON 事件） | 已有 |
| `Commands` | `CommandChannel.cs` / `CommandRouter.cs` | 入向：上位机 → 游戏（命令、ack、状态） | 新增 |
| `Executors` | `TickDriver.cs`（+ 以后 `Spawn` / `Speed` …） | 把命令翻译成游戏动作；每个执行器只做一件事 | 新增（搬现成代码） |
| `Protocol` | `Protocol.cs`（常量 + 命令表 + 错误码）、`docs/……协议.md` | 版本、能力表、字段名——**唯一真源** | 新增 |
| `Instrumentation` | `Api.dll`（可选）+ 文档 | 给**别人的 mod** 用的埋点面 | 待定 |

原则：**执行器不认识传输，传输不认识执行器**。`CommandRouter` 只做 `cmd 名 → 处理器` 的查表，
所以以后加「生成东西」「改档位」「跑脚本」都是加一个文件 + 一行注册（符合"改一处"）。

## 2 协议草案（文件传输，v1）

选文件而不是端口：profile 隔离天然（每 profile 一个目录）、没有端口/token 冲突、和出向通道同构、
进程重启后残留可清理；代价是延迟 1 帧（16 ms）和"半写"——后者用 **写 tmp + rename** 解决。

```
<dir>/                       # = $DSH_LIVE_DIR 或 <存档目录>/dsh-live
  <profile>.ndjson           # 出向事件（已有）
  <profile>.link/            # 新增：命令通道（每 profile 一个目录）
    hello.json               # 游戏写：{v, tick, commands:[...], eventKinds:[...], modVersion}
    state.json               # 游戏写：{v, seq, tick, busy, active:{...}, lastResult:{...}}（原子替换）
    cmd/<seq>.json           # 上位机写：{v, id, seq, cmd, args, issuedAt}（写 tmp 再 rename）
    ack/<seq>.json           # 游戏写：{v, id, seq, ok, at, tick, result|error}
```

- **序号 + 幂等**：`seq` 单调递增；游戏记住 `state.json.lastSeq`，`seq <= lastSeq` 直接忽略
  （上位机重发不会重复执行）。启动时 `lastSeq=0`，从目录里最大的 `cmd/<seq>.json` 续。
- **长命令**语义（`tick.run_to` 这种）：ack **立刻**回 `{ok:true, started:true, planId}`，进度看 `state.json.active`；
  `tick.status` 只是"读 state.json"，`tick.cancel` 是发一条短命令。
- **能力协商**：上位机读 `hello.json.commands`；**没有 `tick.run_to` 就自动退回 `cli advance`**（老 mod / 老版本游戏）。
  这就是"协议对上就能用"的机制化说法，不是靠约定。
- **错误码**（`ack.error.code`）：`E_UNKNOWN_CMD` / `E_BAD_ARGS` / `E_BUSY` / `E_NO_GAME` / `E_HARMONY` /
  `E_EXTERNAL_PAUSE` / `E_TIMEOUT`；`ack.error.message` 给人看。
- **失败边界**（沿用现有契约）：任何解析/执行异常只写 ack + 一次 `Log.Error`，**绝不向 Unity Update 冒泡**；
  通道坏掉以后只停写，不影响游戏。
- **版本**：`v` 整数；上位机不认识的 `v` 就只读 `hello.json` 报错，不做半吊子兼容。

## 3 埋点（用户提的"要求用这套系统的人在自己 mod 里埋点"）

两种给法，建议**先做第一种、留第二种当兜底**：

1. **`DSH_GameLink.Api.dll`**：几十行的独立小程序集，暴露 `DshLink.Emit(kind, fields)` 与
   `DshLink.TrySendCommand(...)`（后者一般用不到）。第三方 mod 引用它 → 埋点就是一行调用。
2. **免引用**：反射找已加载的 `DSH_GameLink.Telemetry.Emit(string, IDictionary<string,string>)`，
   找不到就静默不埋（mod 不因缺 GameLink 而崩）。适合"只想试一下"的人。

配套要写一份 `EVENTS.md`：每个 `kind` 的必填字段、类型、出现时机、幂等性（会不会重复发）。
现有 `[DSH-TEST]` 日志通道保留：它是**没埋点时的降级路径**（从日志里抓，代价是字符串匹配）。

## 4 落地顺序

- **P0（把能力搬进来，最小可跑）**：`CommandChannel` + `CommandRouter` + `TickDriver`（现成代码搬）+ `hello/state/ack`；
  上位机侧 `cli advance` 加一个分支走这条通道；**删掉 `BridgeTools\DSH.TickDriver` 伴生 DLL**（不再依赖别人）。
- **P1（协议固化）**：`Protocol.cs` 常量 + 协议文档 + `hello.commands` 协商 + 错误码全量；
  `cli call` 之外补一个 `cli cmd`（发任意命令、读 ack）方便手工调试。
- **P2（埋点 API）**：`Api.dll` + `EVENTS.md` + 一个 10 行的示例 mod（证明第三方 mod 只需引用 + 一行调用）。

## 5 待拍板（这三条决定 P0 长什么样）

1. **传输**：文件（推荐，上面这套）还是游戏内自建 TCP 监听（延迟低、但要端口/token/线程，且和现有桥接管理重复）？
2. **第三方埋点**：发 `Api.dll`（干净、要引用），还是只靠反射 + `[DSH-TEST]` 日志（零安装、脏）？
3. **P0 范围**：只搬 tick（先能推时间），还是连 `cli cmd` 通用命令入口一起做？

## 5.1 已拍板（2026-10-25 19:1x）

- **D1「API」是双向的**（用户口径）：游戏要跟上位机说话 ⇒ 调**上位机 API**；上位机要跟游戏说话 ⇒ 调**游戏侧 API**。
  所以两层各有一条：上位机侧（Node 导出函数 + CLI）、游戏侧（C# 静态入口）。**同一份状态机，两个门**。
- **D4 埋点走 `Api.dll`**（用户："给 API 没问题，每个 mod 写的时候考虑一下那个 API"）。
- **D5 失败语义 = 结果对象**（`ok/reason/...`），不抛异常；这属于协议 v1 的一部分，允许以后演进（"走一步看一步"）。
- **D6 验收去掉"卸载 RimBridgeServer 也能推"**（用户：没理由卸，GameLink 存在是因为"别人给的不够好用，补充而不是取代"）。
  **独立性检查留到"语义上真的能独立用"之后再做。** 验收变成 3 条：
  ① 一次 `advanceTo` 精确落点（`overrun=0`）+ 结束暂停；② `cli advance --to-tick` 走新通道；
  ③ 日志零红字、帧照出。

## 5.2 事件停点的成本评估（用户问"难不难"；结论：不难，而且比想象的便宜）

| 停点 | 怎么实现 | 每 tick 成本 |
| --- | --- | --- |
| 小人倒地 | 遍历 `map.mapPawns.FreeColonists` 读 `p.Downed` | 3–20 次属性读；11k tick/s ⇒ 20 万次/s，可忽略 |
| 新信件（任意） | `Find.LetterStack.LettersListForReading.Count` 比大小 | 1 次属性读（**已实现**） |
| 指定信件（Raid 等） | 同上 + 命中后扫 defName（列表通常 <10） | O(1) + 偶发小扫 |
| **手动触发一个事件**（用户举的例子） | 它通常产物就是 letter/message ⇒ 复用上面两条 | 同上 |
| 某 thing 出现在地图 | 地图级扫描 | **必须限频**（每 15 tick），否则能吃光帧率 |
| 某个 job 开始 | 比较 `p.CurJobDef` | O(1) |

两条关键判断：

1. **不需要"提前注册钩子"**：`stopWhen` 是按名字查表的字符串/表项，判据在游戏侧有一份固定实现，
   用的人不需要编译期知道事件类型 —— 这就是"协议对上就能用"。
2. **默认不用 Harmony 钩子**：我们在 tick 边界停，钩子最多让我们**早 1 tick**；而钩子要 patch 别人的方法，
   换来 patch 冲突 + 版本脆弱。只有"必须事件发生那一 tick 立刻停"时才考虑钩子。
   ⇒ 结论：**事件停点便宜到不值得当成一个大功能**，P1 做 `letter` + `pawnDowned` 两条各约 20 行就够，
   P0 先把 `run_to` 打通、协议里给 `stopWhen` 留位（收到就回 `E_UNSUPPORTED`）。

## 5.3 第二轮拍板（2026-10-25 19:1x）

- **D7 调用语义 = 等落地返回**（用户："轮询太蠢了，还是等返回值"）。轮询只在 API 内部存在。
- **D8 传输 = 文件**（用户放权："这方面我不懂，按你的来"）。
- **D9 上位机 API = 一个 Node 模块 + CLI 薄壳**（模块是唯一真源，CLI 只做参数解析）。
- **D10 游戏侧会改状态的 API 不开放**（按用户的判据"仓库里有没有东西会用"）：
  查全仓库 C#：`TickManager` 的用法只有两种 —— **读 tick**（GameLink/ColonyRecorder/FoodControl 的记账）和
  **FoodProbe 的暂停夹具**（`bridge\DSH.FoodProbe` 里的 `set_paused`）。**没有任何 mod 在驱动时间**，
  也没有任何夹具需要"自己推进 N tick"。⇒ 不写游戏侧 `RunTo(...)`，只留内部执行器。
  （埋点 API 不是这件事：那是给第三方 mod **发事件**用的，见 D4。）
- **D11 世界变了（读档/新开局/tick 回退）= 停下 + 报错，不做专门语义**（用户：AI 全自动跑，人会插进来本身就说明流程有问题，
  多余的恢复逻辑删掉）。实现上不新写 `worldChanged`：现有"2 秒没进展"的停滞判定就会把它停住并报出来。
- **D12 删掉 `BridgeTools\DSH.TickDriver` 伴生 DLL**（"多余的东西就删掉"）。

## 5.4 本次一次交付的范围（不再分 P0/P1/P2）

1. GameLink 新增 `Commands` 模块：`<dir>/<profile>.link/{hello.json, state.json, cmd/<seq>.json, ack/<seq>.json}`，
   原子写、seq 幂等、`hello.commands` 能力协商。
2. `TickDriver` 执行器搬进 GameLink（刹车 + 状态机），命令：`run_to / status / cancel / probe`；
   **保留已有的 `stopOnLetter`**（不算新功能，是搬运）。其它判据不做（没有消费者）。
3. 上位机：Node 模块导出 `advanceTo()`；`cli advance --to-tick` 改走它。
4. 删除：`bridge/DSH.TickDriver`、`BridgeTools\DSH.TickDriver` junction、`tick/*` GABP 工具、
   `src/conditions.json` 里依赖 `tick/status` 的两条场景（`tick推进已结束`/`tick落点精确`）。
5. 文档：协议 v1（`hello/state/ack` 字段、错误码、seq 语义）+ GameLink 的 About 说明更新。
   埋点 API（`Api.dll`）**本次不做**：仓库里现在没有第三方消费者，只在 `hello.eventKinds` 里留位。

## 5.5 第三轮：停点 = 探针目录（2026-10-25 19:2x）

用户澄清（重要，改设计）：

- **推时间只有一个方向**：上位机要求游戏推。所以"会推时间的那个 mod 对上位机暴露 API"就够，**两侧不对等**，
  不强行给游戏侧造一个对称的 `RunTo(...)`（仓库里也没有消费者）。
- **停点不止信件**：例如"访客离开"是右上角的**消息（Message）**，显眼程度低于信件，但同样要能触发"停下/推时间"，
  至少要让**上位机知道发生过**。
- 由此问："能不能 stop on anything —— 任何可被捕捉到的状态变化都能触发？"

### 结论：能，但要把它做成「探针目录」，而不是"任意表达式"

RimWorld 里"事件"其实都从**少数几个汇聚点**流出来：

| 汇聚点 | 抓什么 | 你举的例子 |
| --- | --- | --- |
| `Find.LetterStack` | 信件（高显眼、可回看、可分派） | 袭击、任务 |
| `Find.Messages` | **右上角消息**（低显眼、一闪而过） | **"访客离开了"就是这里** |
| `Find.AlertStack` | 左侧警报 | 缺床、饥饿 |
| `[DSH-TEST]` 日志通道 | 任何 mod 自己打的埋点行 | 我们自己 mod 的埋点 |
| 状态探针（pawn / thing / 地图 / 世界） | 字段级状态 | "小人倒地"、"某物出现" |

所以"stop on anything"落地成 **`probe kind` 目录**，每条探针 ≈ 10–30 行，按名字在协议里被引用：
`{"kind":"message","textContains":"离开了"}`、`{"kind":"letter","defName":"ThreatBig"}`、`{"kind":"pawn.downed","pawnId":"all"}`。
**不需要提前注册**：协议里写名字，游戏侧查表。加一种新事件 = 加一条探针，host 不用改。

同一个探针有两种用途，**这就是"各取所需"**：

- `stopWhen: [...]` —— 命中即停（推时间用）
- `watch: [...]` —— 不拦时间，只**报一条事件**给上位机（复用现有 NDJSON 事件通道）

两条用途共用同一份探针实现，所以不存在"两套监控逻辑要同步"。

### 埋点 API 的判断标准（回答"做不做"）

**先不做，理由是可判定的**：探针①~③ 是从**通用汇聚点**抓的，**别人的 mod 也会路过滤**，
所以"访客离开"这类事件根本不需要第三方配合。只有当"某个 mod 的内部状态变化**不经过任何汇聚点**"时，
才需要它自己埋点（`DshLink.Emit`）。⇒ 判据：**有具体需求、且该事件不经过汇聚点**，才做埋点 API。
现在没有这样的需求，所以不开（`hello.eventKinds` 里留字段位）。

### 本次交付怎么调（相对 §5.4）

- 探针**新增 `letter` + `message` 两条**（`message` 覆盖"访客离开"这类右上角消息），其余探针留目录位。
- `watch`（只报事件不停）与 `stopWhen` 共用探针实现；事件走现有 NDJSON 通道（`EventLine`），不新开文件。
- 其余不变：文件通道、`time.advance/status/cancel/probe`、删伴生 DLL、`advance` 改走新通道、找不到通道就
  **游戏里打红字**（用户："找不到通道就报错啊，直接去游戏里打红字"）。

## 5.6 已实现，两轮真机验证通过（2026-10-25 19:3x–20:0x）

**第一轮**（`runs\2026-10-25-gamelink-tick\REPORT.md`）：11 通过 / 2 失败（同一个 bug）/ 3 未完全验证。
落点全精确：`3000@Fast = 16.63 s`、`20000@Ultrafast = 2.31 s`、cancel 后同 toTick 续跑 `59203 tick = 4.90 s`；
`letter` 停 tick 62902；`message` 停 143253（与消息 `startingTick` 同 tick，零滞后）；`watch` 300k tick 出 3 条 `probe_hit`。

**第二轮**（`runs\2026-10-25-gamelink-fix-verify\REPORT.md`）：修掉 3 个根因后 3/3 通过 ——
主菜单 `time.status` 正常回话（`hasGame:false`、`tick:-1`，日志 0 条 ERROR）、`--ticks 3000` 严格 `ticksAdvanced=3000`、
首读 `time.probe.messageSource = "liveMessages"`。

两轮里修掉的 bug（都留在报告与 git 里）：`Find.TickManager` 主菜单 NRE、`--ticks` 被前置校验拦、
`message:<含词>` 被当 defName、`probe_hit` 两个 `kind` 键、`reached` 用实时 tick 导致 `stalled` 后多 12、`messageSource` 首读 null。

**未验证**：`<defName>:<含词>` 三段式、`--force-speed`、`timeout` 停因、世界被换掉、主菜单窗口只覆盖约 26 s。

代码全部写完、`dotnet build -c Release` **0 警告 0 错误**；**尚未启动实例验证**（用户那边有进程在跑环世界）。

| 层 | 文件 | 干什么 |
| --- | --- | --- |
| 协议常量 | `mods/DSH_GameLink/Source/Protocol.cs` | 版本 v1、命令名、错误码、停因、探针/事件种类、`mutating` 判定 |
| 传输 | `Source/CommandChannel.cs` | `\.link\{hello.json,state.json,cmd,ack}`、原子写、seq 幂等、50 ms 轮询限频 |
| 分派 | `Source/CommandRouter.cs` | 命令名 → 处理器（只查表，不认识传输） |
| 执行器 | `Source/TickDriver.cs` | 推时间引擎：Harmony 在 `TickManager.DoSingleTick` 前缀逐 tick 刹车 + 停点判据 + `forceSpeed` + 停滞判定 |
| 探针 | `Source/Probes.cs` | `letter` / `message` 两种；消息列表用**反射解析**（1.7 里成员不叫 `messages`，实测 CS0117），解析到的名字由 `time.probe.messageSource` 如实报 |
| JSON 写 | `Source/Json.cs` | 手写 JSON（出向），`EventLine` 的转义也收敛到这里 |
| 复用改造 | `Source/LiveChannel.cs` | 抽出 `ResolveDir()` / `ResolveProfileName()`，两条通道路径同源 |
| 挂载 | `Source/GameLinkComponent.cs` | 每帧调 `CommandChannel.Pump()`（内部限频） |
| 上位机 | `src/gamelink.js` | `advanceTo()` / `waitUntil()` / `call()` / `status()` / `cancel()` / `probe()`；目录候选解析、ack/state 轮询、`session` 变化检测 |
| CLI | `src/advance.js` + `src/cli.js` | `advance` 改走新通道（不再经桥接），新增 `--stop-when` / `--watch` / `--max-ticks`；旧的逐片 `play_for` 实现已删 |
| 协议文档 | `docs/GameLink-协议-v1.md` | 字段级契约（hello/state/cmd/ack、错误码、探针文法、兼容边界） |

删掉的东西：`bridge/DSH.TickDriver`（伴生 DLL 源码与工程）、`BridgeTools\DSH.TickDriver` junction、
`src/conditions.json` 里依赖 `tick/status` 的两条场景。**`bridge/DSH.TickDriver/DSH.TickDriver.dll` 这个文件
还被正在跑的实例锁着，等关掉实例后删**（junction 已删，下次启动不会加载它）。

## 6 风险 / 未验证

### 6.0 真机摸出来的游戏侧事实（2026-10-25，改代码前先看这几条）

| 事实 | 后果 / 我们怎么做的 |
| --- | --- |
| **`Find.TickManager` 在主菜单会抛 NRE**（不是返回 null） | 判空救不了；所有取 tick 的地方改成 `TickDriver.SafeTickManager()/SafeTicksGame()`（try/catch）。踩的时候表现是主菜单每帧一条 ERROR（实测 3046 条/90 s） |
| 1.7 里消息列表成员**不叫 `messages`**，实测是 **`liveMessages`** | 用反射找 `List<Message>` 型静态成员，解析到的名字由 `time.probe.messageSource` 报出来 |
| `Letter.Label` 是 **`TaggedString`** | 不能 `?? string`（CS0019），要先隐式转 `string` 再判空 |
| `message:<含词>` 的第二段**必须当文本**（不是 defName） | 之前当成 defName ⇒ 永不命中；defName 过滤写成 `message:def=<defName>[:<含词>]` |
| 主菜单阶段 `time.status` 是**正常返回**（`hasGame:false`、`tick:-1`），不是错误 | 判据别写成"应该报错" |

### 6.1 其余

- **已知限制（待改）**：命令通道的泵挂在 `GameLinkComponent.GameComponentUpdate` 上 ⇒ **只在局内跑**。
  主菜单阶段命令没人处理（`advance` 会等 15 s 拿 `E_NO_ACK`），`hello.json` 也要等进图才出现。
  改法：给 `Verse.Root.Update` 挂一个 prefix 只做 `CommandChannel.Pump()`（无游戏时 `time.advance` 会正确回 `E_NO_GAME`）。
  **现在不动**：真机测试正在跑，改代码会让结论对不上。- `GameComponentUpdate` 里做文件 I/O 必须**限频**（例如 50 ms 一次）且 try/catch；
  现有 `LiveChannel` 是"每帧常数级、只在变化时写"，命令通道要维持同样的纪律。
- Harmony 在 mod 程序集里挂 `TickManager.DoSingleTick` 与在 companion 里挂是否完全等价：**【未验证】**，需要一次真机重启（本轮不动游戏）。
- 两个进程同时写同一个 `<profile>.link/` 目录会更乱 ⇒ 沿用"一个 profile 一个实例"的既有约定，通道不做跨进程互斥。
