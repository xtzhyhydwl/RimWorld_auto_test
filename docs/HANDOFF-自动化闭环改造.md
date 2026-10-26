# HANDOFF：把等待从「固定延时」改成「状态闭环」

> 给一个**全新会话**。本文只是只读调研的产物：**没有启动游戏、没有改任何代码、没有跑任何测试**。
> 标注口径：`【代码】`=本次读到源码/文档的事实（带 `文件:行号`）；`【产物】`=读了磁盘上既有文件；
> `【建议】`=未验证的设计主张；`【未验证】`=没证据。行号一律指**当前版本**。

## 1. 要解决的问题

人类原话要点：子代理执行慢，因为每做一个动作都留了充足 delay 去赌它做完；
要改成「做一个操作 → 及时读到它有没有生效 → 生效就下一步」，用**闭环检测**替代 delay 保证流程推进。
这件事**单独一条线**推进，不要塞进 wayfinder 地图。

## 2. 现状诊断

### 2.1 还在用「固定延时 / 固定超时」的地方

| 位置 | 是什么 | 问题 |
| --- | --- | --- |
| `src/launcher.js:633-678` `waitForBridge` | 轮询桥接就绪：`timeoutMs=240_000`（`:635`）、`pollMs=500`（`:636`）；判据=日志行 / TCP 可连（`:650-662`）/ PID 存活（`:665`） | **判定本身是闭环**，但上界 240 s、失败只说"超时 + 日志尾"；典型只要 5–18 s（SKILL.md:144） |
| `src/cli.js:131-159` `start` | `startGame` + `waitForBridge`，阻塞到桥接就绪 | 不是 sleep，但同样吃 240 s 上界 |
| `src/live.js:26-28`、`:251-254`、`:302-311` | `POLL_MS=200`、`--timeout` 默认 **120000**；超时只打文件尾 10 行 | `live` 等的是**事件**，kind 只有 6 种（SKILL.md:58-65），表达不了 tick/pawn/物件状态 |
| `src/cli.js:317,319` `call` | 默认 `--timeout 300000` | 与 SKILL.md:35「本仓库默认 60 秒」、`src/gabp.js:87` 的 60_000 **不一致**；一次挂住白等 5 分钟 |
| `src/smoke.js:29,134,156,172,181,213,220` | `sleep` 定义；600 s / 300 s / 180 s / 120 s / 600 s / 90 s 上界；`:223` 800 ms 重试退避 | 都是护栏，但**每个工具各一个数字，没有统一常量**（同类历史统计见 tmp/audit-test/AUDIT.md:448：GABP 超时曾有 12 种取值） |
| `src/ce.js:58,60,94,121,214` | 400 / 600 / 700 / 900 / 250 ms **无条件 settle** | 反模式；SKILL.md:418-420 自己也这么说 |
| `src/ui.js:94-107` `waitUntil(fn,{timeoutMs,intervalMs,what})` | 全仓唯一通用等待原语 | 【代码】AUDIT.md:273 判它"教科书式护栏"；但 **0 个使用者**（AUDIT.md:449：`waitUntil` 零导入） |
| skill 教的做法 | SKILL.md:44-88 只给 `live --until` 一条路；`:82-85` 兜底写"只读能力 + 有界超时轮询"但**没给命令**；`:447` 只在 CE 一节脚注里提过 `rb.poll`（**有，但不在"等状态"那节，也没有 CLI 形式**） | 子代理拿到 skill 后，等状态的唯一可执行手段就是 `live`（事件）或自己 sleep |

**历史量化【代码】**：`tmp/audit-test/AUDIT.md:250-263` 统计 154 个真实等待点 → 45 护栏 / **109 处「动作后无条件定时」** / 0 处「启动后睡 5 分钟」。
109 处集中在 `scripts\`（AUDIT.md:293-309），而 `scripts\` 已于 2026-10-17 清空（STATUS.md:26-31）。
⇒ **仓库里留存的固定延时已经不多了；今天的慢主要不是"代码里写着 sleep"，而是"没有 CLI 级状态等待 → 子代理每次现场发明一个"**。

### 2.2 已经有闭环能力（不要重造）

- **`rb.poll` = 桥接侧「轮询到条件成立」**。签名 `rb.poll(alias, argsTable?, policyTable)`，policy = `{timeoutMs, pollIntervalMs, timeoutMessage, condition}`【代码 `runs/food-recon/lua_reference.json:321-346`】；
  官方范式 `:604-612`：*"Mutate first, then poll."*、*"Prefer structured fields … over UI timing assumptions"*；
  条件算子 **15 个**（`all any path exists allItems anyItem countEquals equals notEquals in notIn greaterThan greaterThanOrEqual lessThan lessThanOrEqual`）+ 21 个表达式算子，含两套根上下文（`result.` vs `vars.`）【代码 `docs/自动化测试-变化驱动设计.md:234-253`】；
  可跑的例子带 `countEquals` / `allItems` / `in`【`lua_reference.json:630`】。
  **这就是闭环的底子：判定在游戏内、100 ms 级、一次往返。**
- **桥接自带 3 个有界等待**：`rimbridge/wait_for_game_loaded`、`rimbridge/wait_for_long_event_idle`、`rimbridge/wait_for_operation`（名字逐字取自 `runs/tools.json`；注意**没有** `rimworld/` 版本，写错会静默失败，AUDIT.md:450）。
- **状态读取工具族**（都在 184 里）：`rimbridge/get_bridge_status`（`state` 18 字段）、`rimworld/list_colonists`（28 字段）、`get_cell_info`、`get_ui_state`、`get_selection_semantics`、`list_letters`、`list_alerts`、`get_game_info`、`get_designator_state`、`list_messages.currentGameTick`…（字段清单已在 `docs/自动化测试-变化驱动设计.md:456-499` 登记成「可观测后置条件」表）。
- **进程/桥接侧闭环**：`readBridgeInfo`（launcher.js:185）、`readProcessRecord`（:399）、`probeBridge`（:356，1.5 s 有界 :340）、`status` 三态 `true/false/null`（cli.js:181-238，**null ≠ false**）。
- **事件流**：`runs/live/<profile>.ndjson`，6 种 kind；`heartbeat` 固定 **3 秒**一条【代码 `mods/DSH_GameLink/Source/GameLinkComponent.cs:21`】⇒ 事件通道**粒度 3 s 且不含 pawn/物件字段**，不能当通用状态等待（这就是"四类等待"里第 3 类必须走 `rb.poll` 的原因）。
- **统一层设计稿已存在但未施工**：`docs/自动化测试-变化驱动设计.md:698-747` 给了 `src/wait.js` 的 `waitFor(probe,{criteria,branches,settleMs,failFast,timeoutMs,inject})` 完整签名 + 5 条"施工时不许省"的纪律；`src/` 下**没有 `wait.js`**（本次列目录确认）。
  ⚠ 该文档引用的行号是旧版（它说 `waitForBridge` 在 `launcher.js:270-315`，现为 **633-678**），**引用前必须重定位**。

### 2.3 结论

**不是缺能力，是「有能力但没被组织成协议、也没写进 skill」**，而且缺的落点是**一个 CLI 门面**：

1. 桥接侧判定能力齐（15 算子 + 184 工具里的读状态族 + 3 个 wait_*）；
2. Node 侧有唯一原语 `ui.js:94 waitUntil`（0 使用者）+ 一份未施工的统一层设计；
3. 但**子代理只会敲命令**：它手里是 `live --until`（只覆盖事件）和 `call`（单次调用、不自带轮询）⇒ 只能 `Start-Sleep`；
4. 第二个缺口：**「等什么该读哪个字段」没有随命令一起交付**（登记表存在，但要人去翻 138 KB 的设计文档）。

## 3. 目标形状（可讨论）

一条统一原语：**动作 → 读状态 → 断言生效 → 下一步**。硬要求：成功**立刻**返回；失败给可诊断错误（哪个条件没到、读到了什么、试了几次、多久）；四类等待**一处实现**：

| 类 | 探针 | 现成可复用 |
| --- | --- | --- |
| 进程 / 窗口就绪 | PID 存活、TCP 可连、日志行 | launcher.js:633 / :356 / :690 |
| 桥接就绪 | 同上 + `get_bridge_status` | 同上 |
| 游戏内状态（tick/pawn/物件/事件） | `rb.poll` + 任一只读工具 | lua_reference.json:321 |
| 事件流 | ndjson 增量跟读 | live.js:112-169 |

**候选 ①：`cli wait` —— 一行命令、三种 source【推荐】**

```powershell
node src/cli.js wait --profile <p> --source bridge --probe rimworld/list_colonists `
  --args '{"currentMapOnly":true}' `
  --condition '{"all":[{"path":"result.colonists","countEquals":3}]}' --timeout 60000
# 退出码沿用 live 的约定：0 命中 / 2 超时 / 1 参数错
```

实现 = `src/wait.js` 一个 `waitFor`（直接用设计稿 §4.4 签名）+ CLI 薄壳；`--source bridge` 内部只发**一次** `rimbridge/run_lua`（一次往返，游戏内 100 ms 轮询）；`--source event` 复用 live 的 follower；`--source process` 复用 launcher 探针。
代价 ~120 行 + 一份 wait 表；风险：条件 JSON 里的字段名会**静默不匹配**（设计稿 §4.2 的"断掉追溯链"警告成立）⇒ 必须配条件登记表 + 超时打印最后一次采样。

**候选 ②：`cli step --file steps/x.json`（一个动作脚本的原子协议）**
`[{call,args},{wait:{probe,args,condition}},{assert:…}]`，一跑出证据包。
代价：要再造 step DSL + 执行器，约 300 行；且与桥接自带 `rimbridge/run_script`（有序 capability 调用 + `continueUntil`）**职责重叠** ⇒ `【建议】`先不造，等 ① 稳定后再决定用 `run_script` 还是自己一套。

**候选 ③：`src/steps/*.js` 场景库（`startColony()`、`advanceTicks()`…）**
好处：可离线单测、带证据包、与设计稿一致；代价：必须先有 `waitFor`，且子代理得敲 `node --input-type=module` 长命令。
致命处：**它不能替代 CLI**——临时等一个状态时没人会新建 step 文件，这正是当初 `scripts\` 涨到 109 处定时的机制。

**推荐**：① 先行（一周内可落地），②③ 等"同一场景跑第二遍"时才固化；三者**共用** `src/wait.js` 的 `waitFor`，CLI 只是薄壳（"一处实现、多处复用"）。
另一个必须拍板的取舍：**判定放 Node 侧还是桥接侧？** 设计稿 §4.2 主张 Node 侧（可离线测 + 可 grep）；但子代理场景里 Node 侧意味着每次等待起一个 node 进程。
`【建议】`折中：`waitFor` 是唯一实现，`--source bridge` 时把条件**下发给 `rb.poll`**（省往返），但字段路径必须来自登记表，且超时把 `rb.poll` 的 report（`attempts` + 最后采样）原样打出来。

## 4. 多实例并发安全（2026-10-24 实测）

> 素材：`docs\多会话抢进程-排查-20261024.md`（只读排查，未起停进程）。**一句话定性：能并存，但按 profile 隔离并不充分** —— 1.7 日志全机共用 + 桥接解析取「最后启动的那条记录」，后果是「断言读到别人的局」「命令打进别人的局」。不是互杀进程，是互踩状态。**正在跑、没上下文的会话**直接读 `docs\多实例并发安全-给运行中会话的补充说明-20261024.md`（≤80 行、自包含、带三条自查命令）。
> 先排除的误会【代码】：`stop` 默认只杀本 profile 记录里的 PID（`launcher.js:725`，`taskkill /PID` 不带 `/F`，`:741`）；`--force`/`--all` 才按映像名全机 `/T /F` 强杀（`:713-723`、`:752-757`，`cli.js:426` 透传）。端口避让是**成功的**（实测 voiced=5174 / rhah=5175；`PORT_RANGE=[5174,5183]` `launcher.js:408`）。真正的隐性风险是报错文案教人「先 `stop --profile` 再 start」（`:481-484`）—— 那正是另一个会话正在用的实例。

### 4.1 四条必修（问题 / 改成 / 改哪 / 验收）

**① 日志隔离** —— 1.7 忽略 `-logFile`，所以不能靠参数
- 问题【实测】：waylith 下日志固定是 `%LocalLow%\…\Logs\Player.log`（`launcher.js:79-106`）；共用日志不敢归档，只记 `logStartOffset`（`:515-530`）。两个 profile 记录的 `logFile` 相同、`logStartOffset` 同为 `320607`；18:52:48 后启动者把整份**重写**，18:52:30 起的 voiced 实例**一行不剩**。更硬的证据：记录写 `logShared:true` / `logStartOffset:320607` 时文件实际只有 ~127 KB（另一路会话实测），本文件作者 18:56 再测是 163,706 B 且 mtime 就在那一分钟 ⇒ 偏移能落在 EOF 之外；而 `readBridgeInfo`（`:190-192`）遇 `sinceOffset >= 文件长度` 会**回退读整份**，把别人的行当成自己的。⇒「日志里没有我的行」不等于我失败，「日志里有某行」也不等于**我**的证据。
- 改成【建议】：三选一，代价与不可靠点都写清。**(a) 按实例快照**：启动后把共用日志复制到 `runs\<session>\Player-<startedAt>.log`，记录 `startedAt` + 当时偏移，之后只读本段；代价=一次拷贝（今天量级 320 KB）+ 一个 session 目录约定；不可靠点=快照后的新行要再同步、与重写竞态时可能拿到半份。**(b) 启动前轮转**：改名让游戏重建；代价最小，但**动了用户那一局的文件**（`:516-518` 注释正因此不敢归档）⇒ 不建议。**(c) 不读日志、只信桥接**：判据全走 `runs\live\<profile>.ndjson`（`live.js:50-58`，唯一真正按 profile 隔离的通道）+ `rb.poll`；代价=丢掉「桥接上线前的失败原因」（B2 的 `(日志为空)` 即此）；不可靠点=主菜单阶段无事件（§7）。
- 改哪：`launcher.js:515-530` 加快照落点与 session 标识；`readBridgeInfo` 的 `sinceOffset` 改指**快照内**偏移。
- 验收：后启动实例起来后，前一实例的 `Player-*.log` 字节哈希不变，且仍能解析出**它自己**的桥接行。

**② 桥接解析必须绑身份**（最危险的一条：命令会打进别人的局）
- 问题【实测】：根因就在 `launcher.js:185` `readBridgeInfo()` —— 本 profile 的记录/日志里刮不到 GABP 行时，兜底 `findRecordByLogFile(<1.7 共用日志路径>)`（`:226-249`）按 **`startedAt` 最新**挑记录。于是：①`status --profile voiced` 报到 **5175（rhah 的端口）并握手成功**（token 也来自 rhah 记录）；②同目录若有 `dshwait-check`（pid=0 / port=**5288** / token=`fake-token-for-offline-check`，「启动」时间更晚），「取最新」就把 **5288 的 FakeBridge 当成 rhah** —— 实测 4 次桥接调用（incident / list_letters / list_messages / ping）全部打到假桥，返回 `success:false, message:"No current map"`，**看起来像业务失败，其实是打错了对象**。
- 改成：`status`/`call`/`wait`/`logs` 一律做**三段交叉校验**：本 profile 记录的 PID 存活 ↔ 该 PID 的监听端口（`Get-NetTCPConnection -OwningProcess`）↔ 该 PID 命令行含 `-savedatafolder=<本 profile>`（`Win32_Process.CommandLine`，`proc.js` 需加 `listRimWorldCommandLines()`）；任一不成立就返回 `null` 并报「该 profile 没有可用桥接信息，先 start」，**禁止退回「最新记录」**。
- 改哪：**两件事**：**(a)** `readBridgeInfo`（`launcher.js:185-220`）显式收 `profile`、**删掉/收紧** `findRecordByLogFile` 兜底（`:226-249`；至少要禁止「跨 profile 取最新」），调用点 `cli.js:67 withBridge`、`:191`、`:251`、`launcher.js:357 probeBridge`、`:650` 全改传 profile；**(b)** 采集记录时排除 `pid<=0`、端口不在分配区间、哨兵 token 的条目（见 §4.1④）。
- 验收：两实例并行时 `status --profile A` 的 `bridge.port` 恒等于 A 的 `lastLaunch.bridge.port`；不等即报错，绝不假绿。

**③ profile 锁**
- 问题【代码】：`start` 只有「读记录 + 判 PID 存活」（`launcher.js:479-485`）与 `spawn`（`:575`）之间**无锁**，记录是普通覆盖写（`:625`）⇒ 同秒并发双方都过守卫；后写记录的把前一个变成无人跟踪的野实例，且两者共用同一 `-savedatafolder`（`:554`）。
- 改成【建议】：`runs\locks\<profile>.lock`，`fs.openSync(path,'wx')` 拿不到就抛「profile X 已被 PID n（起始 T，会话 s）持有」；JSON 内容 `{profile, pid, port, startedAt, session, recordPath}`；`stop` 成功或 `start` 失败回滚时删除，`isAlive` 为假的陈旧锁自动回收重试。1.7（`layout.waylith`）再加一条：枚举到任何非本记录存活的 RimWorld **默认拒绝启动**，需 `--allow-concurrent` 才放行（现仅警告，`:488-492`）。
- 改哪：`launcher.js:479` 守卫处取锁 + `:596-625` 记录里带 `session`；`cli.js:122-132` 加 flag；`stopGame`（`launcher.js:710`）返回前解锁。
- 验收：同 profile 第二个 `start` 被拒并说清「谁持锁、何时起」；进程崩溃后重跑不会被陈旧锁卡死。

**④ 清理 / 忽略假记录**
- 问题【实测】：`runs\game-process.*.json` 是**谁都能写的共享命名空间** —— `dshwait-check`（pid=0 / port 5288 / app=FakeBridge / token=fake-token-for-offline-check）会让所有 profile 的解析指向 5288，`status`/`bridge` 假绿。
- 改成：读记录时**显式排除**，规则 `pid<=0` ∥ 端口不在 `PORT_RANGE`（5174-5183）∥ token 是哨兵值（如含 `fake`/`offline`/`test`）∥ `gameImage` 不是真游戏 ∥ `profileDir` 不存在 ⇒ 一律不采信并 `console.warn` 点名文件（当前 `runs\` 下已无 `dshwait-check`，但任何人都能再写一条）。防再犯：记录加 `schema` 版本 + `source:'startGame'` + `writer`（host/pid），读侧只认 `schema>=1 && source==='startGame'`；写入口收拢到 `launcher.js:596-625` 一处，禁止旁路手写。
- 改哪：`readProcessRecord`（`launcher.js:399-405`）、`findRecordByLogFile`（`:226-249`）、记录构造（`:596-625`）。
- 验收：手工丢一条 `pid=0/port=5288` 的记录进 `runs\`，`status --profile <任意>` 不受影响，且输出里有一条忽略告警。

### 4.2 临时规避（不改代码也能用）

1. **同一时刻只让一个会话起游戏**（1.7 共享日志决定「不同 profile 并发」也不安全）。**已验证有效**的连法：**不走** `status`/`readBridgeInfo`，直接读自己的 `runs\game-process.<profile>.json` 取 `port`+`token` 连；每批动作前重跑三重校验「监听该端口的 PID」↔「该 PID 命令行含 `-savedatafolder=…\profiles\<本 profile>`」↔「记录里的 pid/port/token 三者一致」，不一致就**不发命令**（命令见补充说明文档 §0）。
2. 等状态只用 `runs\live\<profile>.ndjson` 与桥接探针；**不用** `logs` 判断自己实例死活（它读共享日志，会看到别人的行，`cli.js:392-398`）。
3. **`--force` 一律禁用**；要停只停自己的 profile，且「记录里的 `pid` 是自己的」要靠直接读 JSON/`readProcessRecord` 判定，**不要**用 `status` 的解析结果判。
4. 并行测试各用独立 `runs\` 子目录；**别手工写/删 `runs\game-process.*.json`**（它不只是记录，还是解析输入）；要试 `wait` 用假 NDJSON 事件文件。

### 4.3 验收（可测判定）

- **A 的 `call` 绝不会到 B**：每次 `call` 打印解析出的 `pid / port / savedatafolder` 三元组，A 与 B 的三元组零交集；`status --profile A` 端口恒等于 A 自己的 `lastLaunch.bridge.port`。
- **后启动实例不清空前一实例的可读日志副本**：A 的 `runs\<session>\Player-*.log` 哈希在 B 启动前后一致，且 A 段里仍有 A 自己的 `GABP server … port` 行。
- **同 profile 第二个 `start` 被拒**且错误含持锁 PID 与起始时间；无 `--allow-concurrent` 时，1.7 下第二个**不同** profile 的 `start` 也被拒（或明示「已知并发」）。

## 5. 必修清单（按优先级）

| # | 做什么 | 为什么是这一步 | 代价 |
| --- | --- | --- | --- |
| **P0** | 桥接身份绑定：`readBridgeInfo` 显式收 `profile`、删/收紧「取 `startedAt` 最新记录」兜底，改 PID↔端口↔`-savedatafolder` 三段校验（§4.1②） | 实测已把 4 次调用打到 5288 假桥（报 `No current map`）、把 voiced 报到 rhah 的 5175 ⇒ 让**所有**断言与 `call` 落到错的对象，比封装 wait 更致命 | 小（~10 行 + 一次端口/命令行查询） |
| **P0** | 日志隔离：按实例快照共用 `Player.log` 到 `runs\<session>\`（§4.1①，方案需拍板；偏移已被重写废掉） | 1.7 共享日志 ⇒ 日志类判据**全失效**，后启动者抹掉先启动者 | 中（方案 (a) 小而稳） |
| **P0** | profile 锁 `runs\locks\<profile>.lock`（`wx` + 死 PID 回收）+ 1.7 默认拒绝并发、`--allow-concurrent` 放行（§4.1③） | 堵 `launcher.js:479`→`:575` 的 TOCTOU；现在并发 `start` 会静默覆盖记录、泄漏野实例 | 小（~30 行） |
| **P0** | 假记录排除规则（`pid<=0` / 端口不在 5174-5183 / 哨兵 token / 非真游戏 / `profileDir` 不存在）+ 记录加 `schema`/`source`/`writer`（§4.1④） | `dshwait-check` 已实测让所有 profile 假绿；代价极小 | 小 |
| **P0** | `src/wait.js` + `cli wait --source bridge`（把 `rb.poll` 封装成一行） | 唯一能**立刻**让子代理变快的改动；现在他们只有 live/call | 小 |
| **P0** | 改 SKILL.md §「等游戏状态」：加一张「要等什么 → 哪条命令」表，把动作后的等待全指向 `wait`；`AGENTS.md` 硬约束 5 追加 `wait` | skill 每轮进上下文，不写进去等于没做 | 小（≤40 行） |
| P1 | 条件登记表（场景 → probe + 字段路径 + condition JSON），落 `docs\` 或 `src\steps\conditions.json` | 消灭"字段名靠猜"和每次重新反推 | 中 |
| P1 | `--source event` / `--source process` 接进同一 `waitFor`；`live` 与它合并成一套（别留两套） | 四类等待一处实现 | 中 |
| P2 | `cli call` 默认 300 s → 对齐 gabp 的 60 s（或强制显式传） | 挂住白等 5 分钟 | 小 |
| P2 | `src/ce.js` 的 400/600/700/900/250 ms 换成轮询可观测特征（判据见设计稿 §3.2.5，`自动化测试-变化驱动设计.md:503-510`） | 现成的 5 处固定延时 | 中 |
| P3 | `src/steps/` 常见场景库（起游戏 → 开殖民地 → 推 N tick 一条命令） | 才能量化墙钟、做回归 | 大 |

## 6. 怎么算改造成功（可量化）

1. **墙钟**：固定流程「`start --profile voiced` → 进图（`colony_started`）→ `step_game_ticks 600` → 截图」端到端秒数，改造前后各 3 次取中位数，目标 **≥30% 下降**；下降必须来自"等待更早命中"，不是砍断言。【未验证】本文**没有基线**（未启动游戏）。
2. **等待命中分布**：每次 `wait` 输出 `elapsedMs / attempts`，看 p50/p95；若某个状态 p50 远大于预期（如 `list_colonists` > 2 s），说明条件选错或字段名写错（静默不匹配）。
3. **失败可诊断率**：故意给不可能条件（如 `greaterThan 999999999`）⇒ 必须**在 timeout 内**以 exit 2 结束，并打印条件 + 最后一次采样 + attempts。**0 次"只打印超时四个字"**。
4. **固定 sleep 计数**：新增/改动文件里 `Start-Sleep` / `await sleep(` 命中 = 0（`grep` 自检）；沿用 AUDIT 口径（AUDIT.md:250）统计"动作后无检查的定时"，目标：新增 0（历史 109）。
5. **超时上界收敛**：`waitForBridge` 的 240 s、`cli call` 的 300 s 这类"唯一护栏"是否换成"有意义的小上界 + 可诊断失败"（例如 60 s）。

## 7. 约束与坑

- **禁止 `Start-Sleep`**（AGENTS.md:29-31）；等待逻辑只准出现在一处（SKILL.md:85）。
- **`step_game_ticks` 穿 forcePause，`play_for` 不能**：SKILL.md:105-135（`step_game_ticks` ≈592 tick/s，`play_for` ≈7100–7800 但 forcePause 下静默 0 tick）；工具级 `timeoutMs` 默认 **10000**【代码 `runs/tools.json:4666-4670`】⇒ 必须显式传大值（SKILL.md:106）。
- `step_game_ticks.pauseFirst`：schema 默认 **true**【`runs/tools.json:4676-4680`】，但 `docs/盘点-自动化测试现状与最小原子操作.md:92` 记过"不传时三次全 failed" ⇒ **仍显式传**。【未验证】两者冲突原因。
- **信件/对话框冻结游戏时间**：对话框开着 30 s Δ=0（`docs/RimWorld-工具耗时与暂停行为测量-20261024.md:13,60-69`）。长跑前查 `get_ui_state.windowsForcePause`，为 true 先 `close_main_tab{tabId}` 或 `close_window{windowType}`；`press_cancel` 对 `closeOnCancel:false` 无效。
- **1.7 日志路径**：`%USERPROFILE%\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Logs\Player.log`（launcher.js:79-105；1.6 在 profile 目录）。`-logFile` 在 1.7 被忽略。
- **`ModsConfig.xml` 的 `<version>` 不一致会让游戏把清单重置成只剩 Core**；launcher 启动前自动追平（launcher.js:145-158）。手改清单前先读这段。
- **主菜单阶段没有事件**（事件文件不创建）：SKILL.md:67-76 ⇒ 等主菜单用 `--source log --until 'text~GABP server connected to GABS'`。
- **`GABP_TIMEOUT` = 会话已废**：重启游戏，不要重试（SKILL.md:34-35、gabp.js:197-208）。
- **不要碰玩家存档**；`stop` **不加 `--force`**（launcher.js:710-726、AGENTS.md:37）；只开轻 profile、推时间上限 `Fast`（AGENTS.md:33-36）。
- 会挂死整条桥接：`rimworld/create_allowed_area`、`search_debug_actions` 传短语/某些单词（SKILL.md:96-99）。

## 8. 给新会话的启动指令

1. 先读：本文件 → `.dsh\skills\rimworld-auto-test\SKILL.md`（44-88 等状态 / 105-135 推时间 / 443-448 现成机制）→ `docs\自动化测试-变化驱动设计.md` §4.2（657-676）与 §4.4（698-747）。
2. 再只读验证（**不启动游戏**）：`node src/cli.js status --profile voiced`；若在跑，`node src/cli.js tools lua`、`node src/cli.js describe rimbridge/run_lua`；确认 `src/wait.js` 仍不存在。
3. 第一个实做：**P0 的 `src/wait.js` + `cli wait --source bridge`**，不要把 skill 先写长。
4. **人类已确认、别重复问**：RimWorld **1.7.4985 rev1269**（.NET 10 + Waylith），装在 `D:\SteamLibrary\steamapps\common\RimWorld`；桥接 mod `RimBridgeServer` 可用、**184 个工具**（`runs\tools.json` 2026-10-17 快照，命名空间 rimworld 107 / foodprobe 50 / rimbridge 18 / ce 9；**在线数以 `dump-tools` 为准，184 不是常量**，见 `docs/工程腐化盘点-20261024.md:56`）；`dsh.gamelink` 事件通道可用；慢的锅**不在**桥接本身（`search_debug_actions` 实测 349–437 ms，`docs/RimWorld-工具耗时与暂停行为测量-20261024.md:12`）。
5. 写探测脚本前**先问人类**（AGENTS.md:50-68）；但 `wait` / `steps` 属**长期能力**（进统一能力库），不是一次性脚本 —— 这条可以直接判。

## 9. 未验证清单

- **没启动游戏**：`rb.poll`、`rimbridge/run_lua`、`wait_for_*` **一次都没实跑**；所有结论来自 `lua_reference.json`、`runs/tools.json`、设计稿与既有 docs。
- **184 是 2026-10-17 15:34 的 `runs\tools.json` 快照**（本次用正则数 `"命名空间/名"` 得 184，分布 107/50/18/9）；**在线清单未复核**。
- `rb.poll` 的 `condition` 各算子行为只读过文档与示例；`greaterThan` 的真实使用证据来自历史实测（`docs/RimWorld-工具耗时与暂停行为测量-20261024.md:55-56`），**不是本次**。
- **没有墙钟基线**：§6 的提速目标全是**目标**，不是测量结果。
- `src/ce.js:58,60,94,121,214` 的毫秒数是 `grep` 读到的行，**未通读函数上下文**。
- `cli call` 300 s 与 skill「60 秒」谁对：未查历史，不知是否有意。
- `step_game_ticks.pauseFirst` schema=`true` 与"不传必失败"的记录冲突：未验证。
- 设计稿 `docs/自动化测试-变化驱动设计.md` 大量行号指向 2026-10-13 的旧代码，**引用前按当前版本重定位**。
- **【多实例】同 profile 并发 `start` 的真实行为没跑**：§4.1③ 的「双方都过守卫 → 记录被覆盖 → 前一个变野实例」是`【代码】`推断（本次禁止起进程）。
- **【多实例】`stop --force` 未实测**：全机枚举 + `/T /F` 的匹配只从代码读定（`launcher.js:713-723`/`:752-757`），没有当天的运行输出。
- **【多实例】18:49:15 那个 voiced 实例的死因未定**：已排除 RimWorld 的 WER 崩溃记录与 4689 进程结束审计（后者未开启）；也**没找到**任何会话的 `stop` 输出能对上该时刻。18:49:18 那个实例（PID 23352）整段无日志，只有「日志文件被独占」一条间接旁证（未复现）。
- **【多实例】两实例同时进图的 CPU/内存/GPU 影响未测**：18:52 起两个实例（rhah 23688 + voiced 28920）都在跑，报告收笔时仍未结束；资源竞争对 tick 速率的影响无数据。
- **【多实例】`call --profile voiced` 是否真在 rhah 的局里执行未验**：只验证到桥接握手成功（5175 + rhah 的 token）；打到 5288 假桥的 4 次是**另一路会话**的实测，本文件未复核。
- **【多实例】假记录 `runs\game-process.dshwait-check.json` 的作者与用途未查明**（无命令行证据，1 分多钟后即被删）；同秒并发选端口（两个 `start` 都选 5174）的竞态也未验证，只有 18 s 间隔避让成功（5174/5175）。
- **【多实例】`logs` / `--source log` 在并发下的错行率未测**：只从代码判定「读共享日志会看到别人的行」（`cli.js:392-398`），没有统计比例。
