# 快照捕捉 · 状态

> ⚠️ **本文件里提到的 `scripts\*.mjs` 命令已在 2026-10-17 被用户全部删除。**
> 那些命令是**历史记录**，现在跑不了；需要时按标准重新写。**原文未改。**
> 本文从仓库根 `STATUS.md` 拆出（2026-10-17）。**内容原样搬运，未做取舍。**
> ⚠ 用户本次拆分**没有提到这条线**；暂放顶层目录，归属待用户定。
> 归属：静默、按游戏 tick、可拓展地录殖民地状态（只读，不改地图） ｜ 上级：（待用户定） ｜ 交接文件：[../docs/交接-快照捕捉.md](../docs/交接-快照捕捉.md)

---

## ⚠ 本项目已分成**两条独立的线**（2026-10-03）

| 线 | 做什么 | 交接文件 |
| --- | --- | --- |
| **① 快照捕捉** | 静默、按游戏 tick、可拓展地录殖民地状态；分析"玩家为什么这么做" | **[../docs/交接-快照捕捉.md](../docs/交接-快照捕捉.md)** |

- 做快照时**不要**引入自动化逻辑（用户原话：「你就纯净地去读」）

★ **快照线现状（2026-10-03 晚 → 10-04 凌晨，与自动化线已物理隔离）**：
录制器已独立成 mod `mods/ColonyRecorder`（`dsh.colonyrecorder`），源码在 `src/colonyrecorder/`，
编译 `node scripts/build-recorder.mjs --deploy`；程序集与 FoodControl **零引用**（二进制扫描确证）。
隔离实例的 ModsConfig **只加载它**、不加载 `dsh.foodcontrol`。
⚠ 因此 `src/foodcontrol-game/` 与 `src/foodcontrol-core/` 里**已经没有**录制器那几个文件（含 `ColonySnapshot.cs`）。

**10-04 凌晨新增（详见 `docs/交接-快照捕捉.md`）**：
- **两套并列系统**：快照（时间驱动，`colony-<名>-<mapId>.jsonl`，**一个殖民地一个文件、跨会话追加**）
  + **操作事件观察者**（事件驱动，`actions-<名>-<mapId>.jsonl`，`PlayerActionObserver`，每 30 tick 差分，
  含一次性 `buildability` 第 0 层 RLE 地图）。
- **10-04 上午又加了第三套**（源码已改，**未部署**）：信件时间线 `events-<名>-<mapId>.jsonl`
  （`EventTimelineObserver`，由操作观察者每轮末尾驱动）—— 详见下面「快照线本轮增量」。
- 快照新面：游戏内日期（`absTick/year/quadrum/dayOfYear/dateText`）、`rooms`（职能/隔开/**墙材质**/地板/家具）、
  `materials`（建筑按 def+材质）、`pawnDetails`（身份/装备/技能/健康）。
- 分析端：`node scripts/analyze-snapshots.mjs [文件…] --focus all|rooms|materials|buildings|pawns|basics`（多文件拼接）；
  单张查看：`node scripts/show-snapshot.mjs [--index N|--last]`。

### ★★ 快照线本轮增量（2026-10-04 上午，**源码已改、未部署**）

> 本轮只碰 `src/colonyrecorder/`（录制器源码）与 `tmp/` 下的只读核查脚本；
> **`STATUS.md` 这一节就是本轮的全部落盘**。设计背景在
> [../docs/交接-快照捕捉.md](../docs/交接-快照捕捉.md)，目标（连续 3–5 局相似开局、每局约 20 天）见下面 §5。
>
> **10-04 下午补一轮（只改注释/schema 与 STATUS.md，一行运行逻辑没动）**：
> `EventTimelineObserver.cs` 里两条**与实际行为矛盾**的说法已改正 ——
> ① 旧注释称"基线里已挂着的信不产生 `arrive`"（**错的**：`Seen` 不持久化 ⇒ 首轮会把当前
> `LetterStack` 里所有没见过的信写成 `arrive`，基线行**不过滤**任何事件）；
> ② schema 称 `lagTicks ≤ 轮询间隔`（**只在会话内真到达时成立**：重启/读档首轮重放的旧信
> 可以滞后几十万 tick）。编译产物 `tmp/colonyrecorder/ColonyRecorder.dll` 71.5 KB / 7 源文件，
> **仍不部署**。

#### ① designation JSON 非法 —— **已修完，但还没部署**

**真数据里的病灶（本轮实测，只读解析）**：
`…\foodcontrol-snapshots\actions-殖民地-0.jsonl`（1,335,860 B / 6,078 行）里
**882 行非法 JSON，全部是 `kind:"designation"`**，从**第 29 行**就开始：

```text
{"kind":"designation","op":"add","tick":6241,"absTick":621241,"day":0,"wallClock":"09:15:36",Haul}
                                                                                              ^ 裸 defName，JSON.parse 抛
```

根因：`ReadDesignations` 只写了 `info[key] = defName;`（裸字符串），而 `WriteEntityEvent`
把 `info` 当**已拼好的 JSON 片段**直接 `Append` ⇒ 整行语法错误。
**一行坏 = 分析端读到那一行就断**，"文件解析失败"看起来像"文件坏了"，其实是这一处。

修法（两处，互相兜底）：
1. 新增 `EncodeDesignationInfo(defName, hasCell, x, z, thingId)` —— `def` 一律走 `JsonString` 转义，
   形状与 blueprint/frame **同名同形**（`def` / `cells` / `count[:thingId]`）；
2. 新增 `AppendInfoFragment` —— 所有 `Encode*` 的出口都过这道闸：
   不以 `"` 开头（= 不是合法字段片段）就**降级成 `"def":…`** 并**一次性警告点名**
   （不静默摆烂，也不每 30 tick 刷爆 `Player.log`）。

**⚠ 尚未部署（本轮实测确证）**：

| | 时间/大小 |
| --- | --- |
| `mods/ColonyRecorder/1.6/Assemblies/ColonyRecorder.dll`（游戏真正加载的） | **10-03 23:56:18 / 65,536 B** |
| `src/colonyrecorder/PlayerActionObserver.cs` + 新文件 `EventTimelineObserver.cs` | **10-04 10:58** |
| 本轮只编译不部署：`node scripts/build-recorder.mjs`（**不带 `--deploy`**） | ✅ 成功 → `tmp/colonyrecorder/ColonyRecorder.dll`（71.5 KB，7 个源文件） |

⇒ **修好的代码没进游戏**：现在起游戏录出来的 designation 行**仍然坏**。
部署那一步（`--deploy` + 重启游戏）本轮**故意没做**（任务约束：不启动/停止游戏、不调桥接）。

#### ② `events-*.jsonl` 信件时间线 MVP —— 已实现，schema 如下，**从未在真游戏里跑过**

**当前有★三条并列的数据流**（互不影响、同一个输出目录、共用同一套"一个殖民地一个文件"约定；
`IntervalTicks` 默认 30，三套里只有第一套是**时间驱动**）：

| # | 线 | 驱动 | 产物 | 状态 |
| --- | --- | --- | --- | --- |
| 1 | `ColonyRecorder`（`GameComponent`） | 时间驱动，每 **6000 tick** | `colony-<名>-<mapId>.jsonl`（关键帧） | 已在真游戏里产出（28 MB） |
| 2 | `PlayerActionObserver`（`MapComponent`） | 事件驱动，每 **30 tick** 状态差分 | `actions-<名>-<mapId>.jsonl`（增量） | 已在真游戏里产出（1.3 MB，**含 882 行非法 designation**） |
| 3 | `EventTimelineObserver`（静态类，由第 2 条每轮末尾驱动） | 事件驱动，**轮询时未见的信件** | `events-<名>-<mapId>.jsonl`（增量，**目前只有信件**） | ⛔ **从未产出过一行真数据**（旧 DLL 里没有这个类） |

第三套并列系统：`EventTimelineObserver`（静态类，由 `PlayerActionObserver.MapComponentTick`
**每轮末尾**驱动，两条线共用同一个 `IntervalTicks` 节拍）。**只读** `Find.LetterStack.LettersListForReading`，
按 `Letter.ID` 去重，输出 `events-<殖民地名>-<mapId>.jsonl`（与另两套同目录、同命名约定）。

| kind | 字段（**字段名逐字固定**，分析端依赖） |
| --- | --- |
| `session` / `op:start` | `pollSeq / tick / absTick / day / wallClock / tickSource:"game" / detectedTick / detectedAbsTick / detectedWallClock` + `colonyName / colonyFileSafe / mapId / intervalTicks / actionPollSeq / letterSource / dedupeKey` |
| `baseline` / `op:init` | 同上公共头 + `mapId / letters / seen`（**会话起点现场记录**：`letters` = 那一刻 `LetterStack` 里读到几封、`seen` = 去重集合里已有几封。⚠ **它不过滤事件** —— 见下面「基线 ≠ 不产生 arrive」） |
| `letter` / `op:arrive` | 同上公共头 + `arrivalTick / arrivalAbsTick / lagTicks / arrivalTime / letterId / letterUid / letterDef / letterClass / labelRaw / relatedFaction{def,name,loadId}\|null / mapId` |

- ★★ **基线 ≠ 不产生 arrive**（本轮修正掉的一条**与实际行为矛盾**的注释/schema，只改文档、没动代码）：
  会话第一轮 `Seen` 是空的，所以**基线里那些已经挂着的信照样会被逐封写成 `op:"arrive"`** ——
  这不是 bug，是"`Seen` 不持久化"这一取舍的必然结果（宁可重复、绝不丢）。
  ⇒ **不要**写"基线里的信件不会产生 arrive"（旧注释曾这么写）；`baseline` 行只是把
  那一刻的现场（有几封）记下来，供分析端识别"同一轮这批 arrive 里哪些是重放的旧信"。
- ★ `op:"arrive"` 的**两种来源**（分析端必须分开）：
  ① **会话内真到达**：`lagTicks ≤ 轮询间隔`（默认 30）**且** `arrivalAbsTick ≥` 本会话的 `absTick`；
  ② **首轮重放**（重启游戏 / 读档后第一轮）：当时 `LetterStack` 里挂着、本会话还没见过的信全部各写一行 ——
  这些信可能已挂了**几个游戏日** ⇒ **`lagTicks` 会远大于轮询间隔**（几十万 tick），
  且推出来的 `arrivalAbsTick` **可以早于本会话的 `absTick`**。
  ⇒ 只统计"真正的新信件"：先用 `session` 切段，再按上面两条同时过滤。
- ★ **两线对齐的精确锚点**是会话头的 `actionPollSeq`，不是事件行的 `pollSeq`：
  事件文件的 `pollSeq` 是**按本文件（本殖民地名 + 本 mapId）**计数的，而
  `PlayerActionObserver.PollSeq` 是**全进程静态计数**（多地图共用）⇒ 同一次会话里两个
  `events-*` 文件并存时，两张账的 `pollSeq` 不保证逐值相等。

- `tickSource:"game"` + `detectedTick` 是**刻意成对**的：一条事件里有两个时刻
  （游戏自带 `Letter.arrivalTick` vs 我们轮询到它的时刻），不显式写清分析端一定会混。
- `relatedFaction` **恒定存在**（没有关联派系就是 `null`），分析端不用写"字段可能在不在"的分支。
- 去重集合**不持久化**：重启后第一轮会把"当前还挂着的信"**全部**重新写成一次 `arrive`
  （宁可重复一次，也不漏），分析端按 `(会话, letterId)` 去重；
  ⇒ 首轮那批 `arrive` 里 **`lagTicks` 可以远超轮询间隔**、`arrivalAbsTick` 可以早于会话起点，
  这不是"采样慢了"，是**重复记账**（判定口径见上一节两条）。
- 反射已复核（本轮实测）：`Letter.ID / def / relatedFaction / arrivalTick / arrivalTime` 都是 **public 字段**；
  `Label` 是 public 属性（`TaggedString`）；`LetterStack.LettersListForReading` 是 **public 属性**；
  `LetterDef.letterClass` 是 **public 字段**。⇒ 不需要反射、不需要 Harmony。
- `PollSeq` 是**新账**：`PlayerActionObserver.PollSeq` 每轮自增；事件线的会话头另给一个
  `actionPollSeq`（= 同一轮里操作线那本账上的号）——**两线对齐的精确锚点就是它**。
  ⚠ 事件行的 `pollSeq` 是**按本文件（本殖民地名 + 本 mapId）**计数的，而
  `PlayerActionObserver.PollSeq` 是**全进程静态计数**（多地图共用一本）⇒
  同一次会话里多个 `events-*` 文件并存时，两张账的 `pollSeq` **不保证逐值相等**。

**⚠ 未验证（重要）**：默认存档目录里**只有 `colony-殖民地-0.jsonl` 与 `actions-殖民地-0.jsonl`**
（本轮实测 `Get-ChildItem -Force` 只有这 2 个文件）—— **`events-*.jsonl` 一个都不存在**，
因为旧 DLL 里**没有**这个类。⇒ 信件 schema 目前只有**编译 + 元数据确证**，
**一次真数据都没有**：`arrivalAbsTick` 的推导、`lagTicks` 的实际分布（真到达 vs 首轮重放两种形态）、
一件信会不会被读两遍、`Letter.Label` 在中文界面下长什么样，**全部待真机核对**。

#### ③ 现在的产物是「关键帧 + 增量时间线」，**不是完整回放**

| 产物 | 是什么 | 为什么**不能**叫回放 |
| --- | --- | --- |
| `colony-*.jsonl` | **关键帧**：每 6000 tick（0.1 游戏天）一整张快照 | 帧间状态**不在**文件里，中间发生了什么只能靠下一帧反推 |
| `actions-*.jsonl` | **增量**：每 30 tick 一次状态差分，只写"变了的地方" | 两次轮询之间的"建了又立刻拆"看不见；单事件 `cells` 上限 400（超了给 `count` + `truncated:true`）；`MaxEventsPerTick=60` 会截断极端批量划线 |
| `events-*.jsonl` | **增量**：目前**只有信件**这一种 | 没有袭击/天气/精神崩溃/研究完成…；**信件也不等于"玩家的操作"**；且**每个会话第一轮会把当时 `LetterStack` 里所有没见过的信重写一遍 `arrive`**（`Seen` 不持久化）⇒ 直接数 `arrive` 会把旧信算成新事件 |

⇒ 现有的三方拼起来能回答「**什么时候、地图上多了什么、同时基本面怎么变**」，
**不能**回答「玩家当时点了哪个按钮、鼠标在哪、小人一步步做了什么」。
**想要"完整回放"就得另加面**（第四套），别把现在这套当回放用。

#### ④ ★ 每局必须**开局前清空实时目录**、**单局归档**；用 `absTick` + 会话边界区分，避免跨局追加

**这不是洁癖，是本轮真数据里已经暴露的坑（实测）**：

```text
actions-殖民地-0.jsonl 里有 3 条 session，全部是 tick=1（三次开局各从 tick=1 起）：
  L2 session tick=1 absTick=15001   colonyName=殖民地 mapId=0
  L4 session tick=1 absTick=70001   colonyName=殖民地 mapId=0
  L7 session tick=1 absTick=615001  colonyName=殖民地 mapId=0
colony-殖民地-0.jsonl 同一个文件里 seq 也从 1 重开（L1 seq=1 / L3 seq=1）
```

| 事实 | 后果 |
| --- | --- |
| 文件名只由 `殖民地名 + mapId` 决定，**同名 + `mapId=0` 就追加到同一个文件** | 3–5 局相似开局如果都叫同一个名，会**全部混进一个文件** |
| `map.uniqueID` 每个新殖民地都是 **0** | 靠 mapId 区分换局**不可靠**（既有坑，已记在下方表里） |
| "tick 倒退"只捕**同一进程内**的倒退 | **重启游戏**后 tick 从 1 重新开始，进程内状态全没了，**采集端不认为换局** ⇒ 直接续写同一个文件 |
| 采集端没有任何"清空/归档"逻辑 | 上一局的行**永远留着**，时序分析会把两局拼成一条假时间线 |

**所以每局流程固定成这样（下一步真机验收就按它做）**：

1. **开局前**：把实时目录 `…\Ludeon Studios\RimWorld by Ludeon Studios\foodcontrol-snapshots\`
   里上一局的三个文件**移走归档**到 `runs/snapshot-runs/<局名>/`（**归档，不删** —— 那是有价值的数据），
   让实时目录**清空**。⚠ 必须在**游戏关着**的时候操作，别在录制中挪文件。
2. **一局一归档**：每局结束后同样整目录归档并标注（局名 / 时间 / 游戏内天数）。
3. 区分跨局的**两个可用锚点**（别再想靠 mapId）：
   - `events-*.jsonl` / `actions-*.jsonl` 里的 **`kind:"session"`**（每次开局/读档一条）+
     紧跟的 **`kind:"baseline"`**（本会话起点的现场记录：那一刻 `LetterStack` 有几封 / `seen` 有几封）；
   - **`absTick`**（= `ticksGame + gameStartAbsTick`，真实世界时间轴）：**同一局内单调递增、跨局必然跳变**。
   - ⚠ `day` 与快照 `seq` **都不是**跨局锚点：`day = tick/60000` 是"殖民地建立以来"，每局都从 0 起；
     `seq` 每次会话从 1 重开（实测同一文件里 `seq=1` 出现过两次）。
4. **逐局分析**（不是"3–5 局混在一起算一遍"）：分析端**先按 `session`/`baseline` 切段**、
   用 `absTick` 校验段内单调，**每一段单独出一份 §⑤ 的表**；只有"同指标在 3–5 局上的排序与差异"
   这一步才需要跨局并列 —— 且必须**先分段再拼**，否则两局会被拼成一条假时间线。

**3–5 局 / 每局 20 天的采集纪律（一次不漏地照做）**：

| # | 纪律 | 为什么 |
| --- | --- | --- |
| 1 | **每局开始前清空实时目录**（把上一局的 `colony-*` / `actions-*` / `events-*` 移走到 `runs/snapshot-runs/<局名>/`，归档不删）—— ⚠ 游戏关着时操作 | 三套文件名只由"殖民地名 + mapId"决定，`mapId` 新殖民地恒为 0 ⇒ 同名开局会**追加进同一个文件**，跨局无法靠文件名分辨 |
| 2 | **单局归档**：一局一目录，目录名带局号 + 时间 + 游戏内天数；归档时**三个前缀一起收** | 三套产物（`colony-` / `actions-` / `events-`）是同一局的三个视角，分开存会丢对齐 |
| 3 | **按 `absTick` / `session` 分段**：每份文件在分析前先切段，段内用 `absTick` 单调性自检 | "tick 倒退"只捕**同一进程内**的倒退；**重启游戏**后进程内状态全没，采集端不认为换局 |
| 4 | **逐局分析**：每段单独出统一表，最后一步才做跨局排序/差异 | 混算会把两局拼成假时间线（已实测：同一 `actions-` 文件里有 3 条 `session`，全是 `tick=1`） |
| 5 | **首轮信件不算"新事件"**：分析 `events-*` 时先剔掉"`arrivalAbsTick` 早于本会话 `absTick`"或 `lagTicks > intervalTicks` 的行 | `Seen` 不持久化 ⇒ 重启/读档首轮会把 `LetterStack` 里所有旧信写一遍 `arrive`（重复记账，不是真到达） |

#### ⑤ 20 天统一指标（每局同一张表）+ 下一步真机验收

**"20 天"的口径先钉死**（既有坑：用户被 29 天 vs 年内第 40 天绕过一次）：
`day = tick / 60000` = **殖民地建立以来的天数**；**游戏内日期**看 `absTick/year/quadrum/dayOfYear/dateText`
（本轮实测快照里 `dateText` 已是人话：`5500年翠象第1天` / `5500年赫象第15天`）。
⇒ 每局跑满 `tick = 20 × 60000 = 1,200,000`（≈ 100 张快照 / ≈ 40,000 轮操作轮询）。

**每局出的统一表**（同一套判据、并列 3–5 局）：

| 指标 | 数据来源 | 具体量 |
| --- | --- | --- |
| 覆盖度 | `colony-*.jsonl` | 行数 / tick 区间 / absTick 区间 / dateText 首末 / 平均间隔与"漏采"次数 |
| 读取健康 | 每张快照 `health[]` | 各块 `items`，点名"可疑 0 项"与 `warnings` |
| 人口与基本面 | `pawns` / `pawnDetails` | 起始→结束人数、mood / food / expectation 最低值、死亡与倒地 |
| 建造 | `actions-*.jsonl` | `blueprint` / `frame` / `transition` 条数；`transition` 里"建成 vs 取消"分别多少 |
| 标记 | `actions-*.jsonl` | `designation` add/remove 条数（**修好后才能统计**，现在这 882 行是废的） |
| 区域与存储 | `zone` / `cells±` / `storageGroup` | 区数与格子增减、存储组改名、bill 目标量变化 |
| 房间与材质 | `rooms` / `materials` | 房间数、职能变化点、墙材质迁移（木→石→钢）、地板从无到有 |
| 游戏事件 | `events-*.jsonl` | 信件条数按 `letterDef` 分组 + `arrivalAbsTick` 落点（**数据待产生**） |
| 跨局可比 | 上面全部 | 同一指标在 3–5 局上的**排序**与差异点，而不是只看单局 |

**下一步真机验收（待用户开游戏时做，本轮没做也不该做）**：

1. `node scripts/build-recorder.mjs --deploy` → 重启游戏 → 确认 `Player.log` 里有
   `[ColonyRecorder][录制]` / `[操作]` / **`[事件] 本次会话追加到 …events-…jsonl`** 三组行。
2. **重放同一段操作**，用只读核查脚本确认 **designation 非法行 = 0**
   （[../tmp/check-actions-json.mjs](../tmp/check-actions-json.mjs)，本轮已写好：
   原本 6078 行里 882 行坏、现状"ok=5196 / bad=882"）。
3. 核对**新面**：快照 `rooms/materials/pawnDetails` + 头部 8 个日期字段（Steam 直启那份实测已有：
   `rooms=25..60`、`things=38..99`、`dateText` 通；但**开局前几帧 `pawnDetails=0`** ——
   是真还没殖民者、还是读法失效，要按每一帧的 `health[]` "可疑(0 项)" 点名来判，**别靠猜**）。
4. 核对**信件 schema**：`events-*.jsonl` 第一条应是 `session`，第二条 `baseline`；
   一封真信到达后看 `arrivalTick` 与 `detectedTick` 的差（**会话内真到达**应
   `lagTicks ≤ intervalTicks`，默认 ≤ 30；⚠ **重启/读档后首轮那批重放的旧信必然超**，
   那不是故障，要按 `arrivalAbsTick <` 会话 `absTick` 把它们认出来），
   以及 `labelRaw` 在中文界面下的原样文本。
5. 然后才按 §④ 的流程连做 3–5 局 × 20 天，按 §⑤ 出统一表。

#### ⑥ 顺手从真数据里核出来的四个细节（都是**待验证项**，别当成已通过）

| 发现（本轮实测） | 说明 / 待做什么 |
| --- | --- |
| **`pawnDetail` 的 warning 里有 `skills 为 null（沙阿）` / `workSettings 为 null（公野山羊 1）`** | 说明 `pawnDetail` **把殖民者以外的 pawn 也收进来了**（"公野山羊"是动物）。动物没有技能/工作设置 ⇒ 那些字段是 `null` 且**有 warning**（好消息：不是静默的）。⇒ 20 天统计"技能/工作优先级"时必须**按 `kind` 过滤**，否则会被动物污染 |
| **`things` 的 warning 里有 `Slate 超过 1500 个，已截断`（开局两张快照各 20 条）** | `things` 面是**按 def 截断**的（每 def ≤1500），开采出的石块/矿物会顶到上限 ⇒ **按 def 的坐标清单不完整**。健康栏**有** warning（可发现），但分析端必须把"截断"当**已知失真**，不要把它当"总共只有 1500 个" |
| **`rooms` 的 warning 里有 `房间#19 超过 2500 格，统计已截断` / `边界格超过 1200，已截断`** | 大地图的开阔/未隔开区域会触发；**房间的格数与墙材质段数在这种房间里不完整**。20 天指标取"房间数 + 职能变化"是稳的，取"总格数"要小心 |
| **开局头两张快照 `pawns items=0` / `pawnDetail items=0`** | 这是**能力度量在报"没读到"**（`items=0` 但 `err` 空 ⇒ 正是"可疑 0 项"那一类）。第 11 天那张起 `pawns=3 / pawnDetails=4`、第 20 天 `pawnDetails=5` ⇒ 大概率是**开局瞬间殖民者还没落地**，不是读法坏了；但**要有一次真机对照**再定性（对应 §⑤ 第 3 条） |

### ★★ 承接（2026-10-04 中午，快照线）：录制器 runId/run 目录改造（**未部署**）

> ⚠ 上面这一节（"10-04 凌晨新增 / 上午增量"）描述的是**改动前**的状态；本章描述**当前源码状态**。
> 两者冲突时**以本章为准**（源码已改、但 DLL 还没部署 ⇒ 游戏里跑的还是老行为）。
>
> 本节是**施工记录**。目标：**按每次 RimWorld 进程启动生成独立 runId**，
> 让 `colony` / `actions` / `events` **三条流写进同一个 run 目录**，同时**保留 session 边界**。
> 用户额外要求（已一并做掉）：analyze/show 支持**递归与显式路径**；`RecorderMod` 入口日志要包含**三流 + runId**。
> 本轮**不部署、不启动游戏**（游戏当前关着；部署与启动由父会话决定）。

#### ① 目录结构（**新三层**）

```text
%USERPROFILE%\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\
  foodcontrol-snapshots\                        ← 老根目录：**只留历史**，新数据不再写进来，老文件一个不动
    colony-殖民地-0.jsonl                        （28.0 MB，10-04 10:35 的老 DLL 产出）
    actions-殖民地-0.jsonl                       （1.3 MB，10-04 10:36）
    runs\                                      ← 新的一层：run 的容器
      run-20261004-113045-123-4f2a\            ← ★ 一次**进程启动** = 一个 run 目录
        colony-<殖民地名>-<mapId>.jsonl         ← 三条流**共用这个目录**
        actions-<殖民地名>-<mapId>.jsonl
        events-<殖民地名>-<mapId>.jsonl
```

- **runId** = `yyyyMMdd-HHmmss-<AppDomain.Id>-<4 位十六进制>`（例 `20261004-113045-123-4f2a`）
  ⇒ ① 进程内恒定（只生成一次）② 字典序 = 时间序（可排序）③ 同秒同进程也能区分（不撞）。
- **文件名保持简洁**（用户要求）：`<前缀>-<殖民地名>-<mapId>.jsonl`，**不含** runId / sessionSeq。
- **老根目录名 `foodcontrol-snapshots` 故意不变**（分析端脚本与既有数据都依赖它）；老文件**不迁移、不改写、不续写**。

#### ② 三层身份（run × session × map —— 别混）

| 层 | 是什么 | 落在哪 | 什么时候变 |
| --- | --- | --- | --- |
| **run** | 一次**进程启动** | **目录名** `runs/run-<runId>/`（三流共享） | 进程重启 |
| **session** | 进程内**换局 / 读档**（游戏 tick 倒退） | **行字段** `runId` / `sessionId` / `sessionSeq` | tick 倒退 |
| **map** | 地图 | **文件名**里的 `<mapId>` | 换地图 |

⇒ **一个 run 目录里的一个文件可以跨多个 session**（用户要文件名简洁 ⇒ session 只进行字段）。
分析端**按 `sessionId` / `sessionSeq` 切段**，不要按文件名切。
★ 这正好解决了 §④ 记的老坑（真数据里同一文件 **3 条 `session` 全是 `tick=1`**）：现在三次开局是**三个 run 目录**，
而且**跨启动不再靠分析端纪律**（清目录/归档）来分辨 —— 结构上就分开了。

#### ③ 会话号是**一本账**（三流共用，防多算）

- `RunScope.EnsureSessionForPoll(tick)` 内部按 **tick 节流**（`_beginCalledTick`）：
  操作流与事件流**都会**检测 tick 倒退，若不节流，同一次换局会吃掉**两个**会话号 ⇒ 两边的 `sessionSeq` 对不上。
  现在：**一次换局 = 一个号**；多地图同 tick 只递增一次；`mapId` 只用于文件名。
- 会话号**从 1 开始**（不是 0），`sessionId = <runId>-s0001`（四位零填充 ⇒ 字典序 = 会话序）。
- **快照流补上了自己的 tick 倒退处理**（用户明确要求）：老实现只有操作流有。
  现在 `ColonyRecorder` 看到 tick 倒退会 `_file = null` + `_lastTick` 重置 + 重开会话
  ⇒ 不再出现"同进程读档**续写旧文件**"。
- 事件流的 `_buildabilityWritten` 去重 key 改成 **`RunScope.KeyForMap(mapId)` = `runId#mapId`**（带 runId），
  避免跨 run 误判"这张图已经写过第 0 层"。

#### ④ 行尾元数据（**平级字段**，不嵌套 —— 用户明确要求）

三条流**每一行**（含 `session` / `baseline` / `buildability` / 各事件行 / **快照行**）都在**行尾**追加：

```json
… ,"runId":"20261004-113045-123-4f2a","sessionId":"20261004-113045-123-4f2a-s0001","sessionSeq":1}
```

- **老字段的名字/类型/相对顺序一个都没动**（只在整行最后追加）⇒ 老解析器（`analyze-snapshots.mjs` / `show-snapshot.mjs` / `tmp/check-*.mjs`）无感。
- 快照行由 `ColonySnapshot.ToJsonLine()` 追加同一组三字段（`ColonySnapshot` 新增 `RunId/SessionId/SessionSeq` 三个属性）。
- 为什么**不**嵌套成 `{"run":{...}}`：老解析器按名字取值，追加平级字段它无感；嵌套会诱使新老两套读法并存。

#### ⑤ ★ 用户要求的"降级行为"（写清楚，不含糊）

| 情况 | 行为 |
| --- | --- |
| `GenFilePaths.SaveDataFolderPath` 还没就绪，或 run 目录建不出来（前 10 次） | `RunScope.PathFor` 返回 `null` ⇒ **这一轮三条流都不写盘**（内存里的采样/差分/会话号**照常推进**），`Log.Warning` 一次并**写明降级行为**；**间隔约 10 秒重试，最多 10 次** |
| 10 次之后仍失败 | 仍是"这一轮不写"，但**不再重试**（只留最后一条原因）——避免每 30 tick 刷爆 `Player.log` |
| 单条流写文件失败（磁盘满 / 被占） | 那条流置 `_writeFailed` ⇒ **本会话不再重试写盘**，只记一次警告（快照流与操作流都已这样处理） |
| 一切正常 | 三流写进**同一个** `runs/run-<runId>/` 目录 |

⚠ `RunScope` 里**没有任何 `throw`**；`RecorderMod` 构造函数里 `EnsureReady()` 返回 `false` 完全正常（入口日志会点名降级）。

#### ⑥ 代码结构（"深模块"拆分，消掉重复）

| 文件 | 角色 |
| --- | --- |
| **`src/colonyrecorder/RunNaming.cs`（新）** | **纯命名/路径规则**：runId 生成（参数化，便于自测）、run 目录、文件名、`SafeSegment`（从 `ColonyRecorder.FileSafe` **原样搬来**）。零游戏依赖 ⇒ **能单独编出来跑离线自测** |
| **`src/colonyrecorder/RunScope.cs`（新）** | **进程级 run 作用域**：runId（进程内恒定）、run 目录、`EnsureReady`（幂等 + 失败不缓存 + 退避重试 + 只警告）、`PathFor`（**三流唯一路径出口**）、`EnsureSessionForPoll`（会话号一本账）、`KeyForMap`、`SessionMeta`（行尾三字段的唯一拼法） |
| `ColonyRecorder.cs` | 走 `RunScope.PathFor`；**补 tick 倒退处理**；`FileSafe` 只转发到 `RunNaming.SafeSegment`（规则只有一份） |
| `ColonySnapshot.cs` | 新增三个属性 + `ToJsonLine` 行尾追加 |
| `PlayerActionObserver.cs` | `EnsureFile` 走 RunScope；11 处行尾元数据；buildability 去重 key 带 runId；`OutDir` 这个静态成员**删除**（路径收口） |
| `EventTimelineObserver.cs` | `EnsureFile` 走 RunScope；与操作流**共用** RunScope 的会话号；3 类行尾元数据 |
| `RecorderMod.cs` | 入口先 `RunScope.EnsureReady()`；入口日志 = **三流 + runId + run 目录 + 三层身份** |

#### ⑦ analyze / show：递归 + 显式路径（用户额外要求，已做）

- 两个脚本都新增 `collectSnapshotFiles(dir)`：**递归**下钻子目录（深度上限 6），
  且**只认快照流 `colony-*.jsonl`** —— 同一个 run 目录里的 `actions-*.jsonl` / `events-*.jsonl`
  **行不是快照**，混进来会把"字段缺失"报成故障。
- `analyze-snapshots.mjs`：**位置参数给目录**也行（递归展开、按 mtime 排序、打印"递归找到 N 份"）；
  不给文件时"最新一份"也**递归含子目录**（新结构自动被发现）。
- `show-snapshot.mjs`：**位置参数给目录**也行（递归取该目录里最新的 `colony-*.jsonl`）；
  不给文件时同样递归。
- **老用法一字未改**：显式文件名照样可用、老根目录照样被发现 ⇒ 新老数据一套代码。
- 顺手修了一个**被本轮改动暴露出来的老崩溃**：`analyze-snapshots.mjs` 的 `mergeStates` 里
  `states[a.ordinal].frames[a.index]` 用**文件内序号**取**合并后**的 frame ——
  多文件拼接时下标错位 ⇒ `fa` 为 `undefined` ⇒ `TypeError: Cannot read properties of undefined (reading 'dateText')`。
  （触发条件：把老格式与新格式放在**同一个目录**里分析，本轮"给目录"就正好会这样。）
  已改成用合并序号取 + 日期取值走 `dateTextOf()` + 兜底。

#### ⑧ 兼容与隔离（本轮口径）

| 项 | 结论 |
| --- | --- |
| 老数据（默认档 `colony-殖民地-0.jsonl` 28.0 MB / `actions-殖民地-0.jsonl` 1.3 MB；隔离档 5 份老快照） | **一个字节都不动、也不迁移**；新数据**不再写进老根目录**（避免又把两局混起来） |
| 老命名 `colony-<时间戳>.jsonl` | `colony-` 前缀一样 ⇒ 递归收集/分析端**照样认** |
| `src/foodcontrol-*` | **零改动**（本轮只碰 `src/colonyrecorder/` + 两个分析脚本 + `STATUS.md`） |
| `docs/交接-快照捕捉.md` | **零改动**（用户明确要求；接手版 `docs/交接-快照捕捉-接手.md` 也没动） |

#### ⑨ 本轮验证（**离线全绿**；真机 = 未做）

| 验证 | 命令 | 结果 |
| --- | --- | --- |
| 编译（9 源文件 = 7 老 + 2 新） | `node scripts/build-recorder.mjs`（**不带 `--deploy`**） | ✅ `✓ 编译成功 tmp\colonyrecorder\ColonyRecorder.dll (78.5 KB)`；`show-snapshot.mjs`/`analyze-snapshots.mjs` 未碰 DLL |
| **RunNaming 行为自测（编译真源码）** | `tmp/runscope-naming-test.ps1`（`Add-Type` 编 `RunNaming.cs` 再断言） | ✅ **18/18**：runId 可排序/不撞/无非法字符；三层目录；文件名简洁 + 老命名兼容；**三流同目录**；空参 ⇒ null；路径长度 < 200；SafeSegment 与老 FileSafe 一致、**绝不含路径分隔符** |
| **三流元数据 / 行形状自测** | `node tmp/run-meta-json-test.mjs` | ✅ **20 组**：14 类行（10 操作 + 3 事件 + 1 快照）全部合法 JSON；元数据**恒在行尾且同序**；**老字段键序逐字未变**；元数据是平级 string/string/number；同一 session 号三流一致；+ 13 条源码不变量（路径只经 RunScope、快照 tick 倒退处理、11+3 处行尾追加、去重 key 带 runId、入口日志含三流+runId、递归收集只认 `colony-*`、帮助模板不许有裸反引号…） |
| **analyze/show 递归 + 显式路径** | 合成数据 `tmp/run-dir-selftest/`（老格式 1 份 + 新 run 目录 1 份 + `actions`/`events` 各 1 份） | ✅ 给**目录**→"递归找到 2 份 colony-*.jsonl（actions/events 已跳过）"并出合并报告；给**单个新文件**→照常出报告；`show` 给目录→递归取最新、给文件→照常 |
| 真机（游戏里真跑一遍） | — | ⛔ **未做**（游戏关着；本轮不部署/不启动） |

#### ⑩ 下一步（**部署与真机验收清单** —— 父会话决定，别顺手做）

1. 部署：`node scripts/build-recorder.mjs --deploy`（游戏必须关着；老 DLL 在 `mods/ColonyRecorder/1.6/Assemblies/`，
   实测仍是 **10-03 23:56 / 65,536 B**，**没有** `RunScope`/`RunNaming` 这两个类 ⇒ 现在启动游戏录出来的仍是老行为）。
2. 启动游戏（Steam 直启，按 `docs/交接-快照捕捉-接手.md`），确认 `Player.log` 里：
   - 入口那一行**含 runId 与 run 目录**（三流 + 三层身份都点名）；
   - 每组 `[ColonyRecorder][录制]/[操作]/[事件] 本次会话追加到 …` 指向**同一个** `runs/run-<runId>/`。
3. 确认磁盘上出现 `foodcontrol-snapshots\runs\run-<runId>\`，里面**同一时刻**有三份 `colony-/actions-/events-*.jsonl`。
4. 逐条核对行尾元数据：三份文件的 `session` 行 `sessionId`/`sessionSeq`/`runId` **逐字相同**；
   快照行也有这三个字段；用 `node tmp/check-actions-json.mjs` 复查 designation 非法行仍为 0。
5. **读档验证 session 边界**（这条是本轮 `ColonyRecorder` 新逻辑的关键验收）：
   进游戏 → 存档 → 读档 ⇒ tick 倒退 ⇒ 应看到 **`sessionSeq` 从 1 变 2**、且**没有**"续写旧文件"的迹象
   （文件里 `seq` 从 1 重开但 `sessionSeq` 也变了 ⇒ 分析端能切段）。
6. **重启验证 run 边界**：关游戏再启动 ⇒ 应出现**第二个** `run-<新 runId>` 目录，老 run 目录**不再被写**。
7. 然后才按 §④ 的纪律连做 3–5 局 × 20 天（现在**不用再手工清空实时目录**了 —— run 目录天然隔离；
   归档仍建议整目录移走）。

⚠ **未验证项（别当已通过）**：真机上的 runId/目录/行尾字段、读档后的 session 递增、
多地图同 tick 只递增一次、`events-*.jsonl`（旧 DLL 里没有这个类，**至今零行真数据**）。

用户 2026-10-04 改了口径：**不再用隔离纯净实例，直接从 Steam 目录启动正常游戏**来测。
启动前要确认游戏 `Mods\` 目录 + **默认档**的 `ModsConfig.xml` 里有 `ColonyRecorder`（录制器）与 `RimBridgeServer`（桥接）。
**我已把 `<li>dsh.colonyrecorder</li>` 追加到默认档 `ModsConfig.xml` 的 `activeMods` 末尾**（2026-10-04 01:38:48，`RimBridgeServer` 本来就在）。
数据会写到**默认存档目录**的 `foodcontrol-snapshots\`（不是 `profiles/vanilla/…`）⇒ 分析时必须显式传路径。
⚠ 新面（`rooms/materials/pawnDetails/游戏内日期`）与整个操作事件流**从未在真游戏里产出过数据** —— 接手第一件事就是按那份交接文件核验。

## 已知问题 / 坑（详见 docs/）

| 坑 | 说明 |
| --- | --- |
| **`info[...]` 必须是合法 JSON 片段，不是 defName** | `designation` 曾塞裸 `Haul` ⇒ 整行 `JSON.parse` 抛；**一行坏 = 分析端读到那行就断**（真数据里 6,078 行坏了 882 行）。已加 `AppendInfoFragment` 兜底 + 一次性点名 |
| **跨局追加：文件名只有`殖民地名+mapId`** | `mapId` 新殖民地恒为 0 + 重启游戏后 tick 从 1 重来（进程内状态全失）⇒ 采集端不认为换局，**五局会混进一个文件**。对策见上文 §④（开局前清空实时目录 + 单局归档 + 用 `session`/`absTick` 切段） |
| **`things`/`rooms` 面会按上限截断** | 真数据里见过 `Slate 超过 1500 个，已截断`（每张快照 20 条）与 `房间#19 超过 2500 格，统计已截断`。**有 warning 可发现**，但分析端必须把"截断"当已知失真 |
| **`pawnDetail` 混进动物** | 真数据里 `skills 为 null（公野山羊 1）` ⇒ 统计技能/工作优先级时按 `kind` 过滤，别被动物污染 |

## 最近一次验证

**2026-10-04 上午（快照线增量，只读核查 + 只编译不部署）**：

| 验证 | 结果 |
| --- | --- |
| 真数据里 designation 非法行 | ❌ **882 / 6,078 行**（`actions-殖民地-0.jsonl`，全在 `kind:"designation"`，第 29 行起）⇒ 修复**已写进源码** |
| 修复是否已部署 | ❌ **没有**：部署 DLL 仍是 10-03 23:56 / 65,536 B，新源码 10-04 10:58 |
| 只编译不部署 | ✅ `node scripts/build-recorder.mjs` → `tmp/colonyrecorder/ColonyRecorder.dll`（71.5 KB，7 源文件） |
| `events-*.jsonl`（信件时间线） | ⛔ **文件不存在** ⇒ 从未在真游戏里产出过一次数据 |
| 跨局混装（会话边界） | ❌ **已发生**：同一 `actions-`/`colony-` 文件里有 **3 条 `session` 全是 tick=1**（absTick 15001 / 70001 / 615001，同名同 mapId） |
| 三套产物现状 | 只有 `colony-殖民地-0.jsonl`（28 MB）与 `actions-殖民地-0.jsonl`（1.3 MB）两个文件 |
| 本轮改动的文件 | `STATUS.md`（上午那轮）＋ `src/colonyrecorder/EventTimelineObserver.cs`（下午那轮**只改注释/schema**） |
| 三套并列数据流是否都在 STATUS 里写清 | ✅ colony / actions / events 三条（§②开头一张表）；designation 修复**未部署**、letters MVP 字段与限制、可回放边界（§③）、3–5 局 × 20 天采集纪律（§④）都在 |

**2026-10-04 下午（注释/schema 纠错 + 再次只编译不部署）**：

| 验证 | 结果 |
| --- | --- |
| `EventTimelineObserver.cs` 里"基线里的信不产生 `arrive`" | ❌ **与实际行为矛盾** ⇒ 已改为"基线行不过滤事件，首轮会把当前 `LetterStack` 里所有未见过的信写成 `arrive`" |
| `lagTicks ≤ 轮询间隔` 的说法 | ❌ **只在会话内真到达时成立** ⇒ 已标明"重启/读档首轮重放的旧信可远超间隔，且 `arrivalAbsTick` 可早于会话起点" |
| 运行逻辑是否被改动 | ✅ **一行没动**（改动全部落在 `///` 注释里；`git diff` 只有注释行） |
| 只编译不部署（本轮再跑一次） | ✅ `node scripts/build-recorder.mjs`（**不带 `--deploy`**）→ `✓ 编译成功 tmp\colonyrecorder\ColonyRecorder.dll (71.5 KB)`；mods 目录未被碰 |

**2026-10-04 中午（runId / run 目录 / 三层身份 —— 源码+脚本改完，**仍未部署**）**：

| 验证 | 命令 | 结果 |
| --- | --- | --- |
| 编译（9 源文件 = 7 老 + 2 新） | `node scripts/build-recorder.mjs`（**不带 `--deploy`**） | ✅ `✓ 编译成功 tmp\colonyrecorder\ColonyRecorder.dll (78.5 KB)`（`tmp/` 里那份 `80,384 B / 11:38`） |
| **RunNaming 行为自测（编译真源码跑）** | `tmp/runscope-naming-test.ps1`（Windows PowerShell 5.1；文件**带 UTF-8 BOM**，否则中文被按 ANSI 读 ⇒ 解析失败） | ✅ **18/18** |
| **三流元数据 + 源码不变量自测** | `node tmp/run-meta-json-test.mjs` | ✅ **20 组**（行形状 7 + 源码不变量 13） |
| **analyze/show 递归 + 显式路径** | `node scripts/analyze-snapshots.mjs tmp/run-dir-selftest …`（合成新旧混装数据） | ✅ 目录→递归 2 份 colony（`actions`/`events` 已跳过）、单文件→照常；`node scripts/show-snapshot.mjs <目录>` ✅ |
| 语法检查 | `node --check scripts/analyze-snapshots.mjs` / `scripts/show-snapshot.mjs` | ✅ 两个都 0 |
| 顺手修的老崩溃 | `analyze-snapshots.mjs:mergeStates` 用**文件内序号**取**合并后** frame ⇒ `TypeError: ... reading 'dateText'`（同一目录里老格式+新格式必崩） | ✅ 改成合并序号 + `dateTextOf()` + 兜底 |
| 真机（游戏里真跑一遍） | — | ⛔ **未做**（游戏关着；本轮不部署/不启动） |
| 部署的 DLL vs 源码 | `mods/ColonyRecorder/1.6/Assemblies/ColonyRecorder.dll` 实测 **10-03 23:56 / 65,536 B**（无 `RunScope`/`RunNaming`） | ⛔ 游戏里**仍是老行为**（不生成 run 目录、无行尾元数据） |
| 老数据是否被动过 | 默认档 `colony-殖民地-0.jsonl` 28,052,923 B / `actions-殖民地-0.jsonl` 1,335,860 B；隔离档 5 份老快照 | ✅ **一个字节都没动**（新数据不再写老根目录） |
| `src/foodcontrol-*` / `docs/交接-快照捕捉.md` | — | ✅ **零改动** |
