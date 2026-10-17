# 副官语音 v2 — STATUS

> 这条线**唯一的状态文件**。流程见 skill `rimworld-mod-pipeline`。最后更新：2026-10-17。
> 入口：`README.md`（目录与文档地图）。

## 现在

- **第 1–8 步全部写完**：6 次提交（`a6a5b7f`→`27e4f93`），`dotnet build -t:Rebuild` **0 错误 0 警告**。
- **已进游戏（2026-10-17 16:10 起）**，两轮运行时验收：
  - 第一轮报告 `runs/游戏内验收-20261017.md`：自检 `28/23/32/22` + 17 sample 全 ok；8 个补丁点里 6 个被真实触发（incident 3 类 / 自算 3 类 / message 1 类 / letter 丢弃表）；合成闸（无 key 时）`5.1→5.3 未配置`、0 请求、0 异常；发现 F1（死亡句富文本）与 `VoicePlayer` 告警，均已修（`fe2b0e9`）。
  - 第二轮（进行中）：配好 `ApiKey` 后真合成成功（`5.2` 45 条、`Cache/Voice` 46 个 wav / 2.36 MB）、`[VD] 3a Ready`→`3b`→`4`→`6` 首次跑通；`[VD] 4 Taisia阵亡。Taisia已死亡。` 证明 F1 已修、第 7 步开始真播放。
  - 新装 `DSH_GameLink`（事件通道，见 `docs/提案-游戏侧事件通道-DSH_GameLink.md`）：`runs/live/voiced.ndjson` 163 行、0 行非法 JSON；`node src/cli.js live --until …` 实测 12.6 s 命中并退出。
- **下一步**：按第二轮报告补齐剩余项：
- **第三轮（2026-10-17 16:50 收尾，全部实测）——两个持久化 bug 已修并验完**：
  - `1a9b772`：`VoiceDeputySettings` 全部改走 `Look<T>`（`Scribe_Values.Look(..., true)`）。修前 XML 永远是空对象；修后 `update_mod_settings` 落全 40 项（22 个 `enabled_*` + 全部标量），**重启**后 `get_mod_settings` 读回 `QueueLimit=3` / `MaxNamedInBatch=1` / `TraceEnabled=true` / `ApiKey` 非空 ⇒ `06 §10` 第 4 条成立。
  - `1a9b772`：`SpeechLibrary.IndexDirty` + `LibraryJanitor.SaveIndexIfDue`（5 s 节流）。实测连播后 `Cache\Voice\index.json` 5 s 内非空；**重启后 25 个 wav 全部保留**（修前那 5 个未入索引的孤立文件被 reconcile 删掉，两阶段/对账都对）。
  - `1a9b772`：`cli live --until` 默认从头扫 + `--new-only`；实测历史命中 0.2 s、新事件等待正常。
- **还没验**：人耳听感（停顿/爆音/语速）、`3b drop-new`、`5.3 HTTP 401` 错误串、`鼠条.18` 名字清洗、精神崩溃/分娩两个钩子、22 类全覆盖。


- **进游戏看这几行判定第 8 步**（先在调试页打开 `TraceEnabled`）：
  - 启动首帧：`[VD] janitor roster new=<n> gone=0`；2 s 后：`[VD] warmup first-batch <入队>/<总数>` → 一批 `[VD] 5.1 <常量片段>` + `[VD] 5.2 <片段>`（**不伴随** `[VD] 4`），随后 `[VD] warmup sent <n> left=<n>`
  - 把库上限调到 5：连续出现 `[VD] janitor evict L3 <文本>`；L3/L2 清完仍超限时 `[VD] janitor l1-protect over=<n>`，L1 条目仍在
  - 名字的主人死亡：本轮 `[VD] janitor roster new=… gone=<n>` → **下一轮**（再等一个清理周期）`[VD] janitor vanish <名字>`（两阶段判据）
  - 点「清缓存」：`[VD] janitor clear entries=<n> files=<n>`，调试页占用归零、`Cache/Voice` 里 .wav 全没
  - 整句缓存：同一句第二次播 ⇒ `[VD] 3a sentence-hit <整句>`；第一次播完 ⇒ `[VD] 6 <整句>` 之后 `[VD] 5.2 <整句>`（整句作业，`warmup sent` 里顺带看到）
  - 对账 / 坏文件：`[VD] janitor reconcile orphan=<n> stale=<n>`、`[VD] janitor broken-register <文本>`、`[VD] janitor broken <文本>`、`[VD] janitor index-write-failed`、`[VD] janitor reconcile-skip`
- **进游戏看这几行判定第 7 步**（先在调试页打开 `TraceEnabled`）：
  - 单句：`[VD] 3a Ready n <整句>` → `[VD] 3b <队列长度> <类别>` → `[VD] 4 <整句>` → … → `[VD] 6 <整句>`
  - 连发 3 条：`[VD] 3b` 出现 3 次且队列长度不超过 `QueueLimit`；`[VD] 4` 与 `[VD] 6` 交替、不重叠；超限时出现 `[VD] 3b drop-old …` 或 `[VD] 3b drop-new …`
  - 威胁插队：当前句 `[VD] 6` 之后紧接威胁句的 `[VD] 4`（中间不夹普通句的 `4`）
  - 同 tick 两人阵亡：只有一句 `[VD] 2 2 人阵亡：<名字>、<名字>。`，随后 `[VD] 4` 一次
  - 单人阵亡（批内合计 1）：`[VD] 2 <名字>阵亡。<死因>`（走单事件模板，不是"1 人阵亡"）
- **进游戏看这几行判定第 6 步**：
  - 点语音页「试听」⇒ `[VD] 5.1 副官就位` → `[VD] 5.2 副官就位` → `[VD] 3a Ready 1 副官就位` → `[VD] 4 副官就位` → 出声 → `[VD] 6 副官就位`
  - 未配置（`ApiKey` 空）：点试听 ⇒ `[VD] 5.1 …` → `[VD] 5.3 副官就位 未配置`，**没有 5.2、没有请求**，状态条显示「未配置」
  - 事件播报：`[VD] 1 …` → `[VD] 2 <整句>` → `[VD] 3a Missing n <整句>` → `[VD] 5.1 <片段>` → `[VD] 5.2 <片段>` → `[VD] 3a Ready n <整句>` → `[VD] 4 <整句>` → `[VD] 6 <整句>`
  - `[VD] 5.3 <片段> HTTP 401: …` = key/域名不对；`[VD] err drain|plan|player <类型>` = 泵里抛了异常（600 帧一条）

## 待办

- **P1（设置写盘成空对象）已修 2026-10-17**：实测 `profiles\voiced\Config\Mod_副官语音_VoiceDeputyMod.xml` 是 `<ModSettings Class="VoiceDeputy.VoiceDeputySettings" />`（内存里 `ApiKey`/`TraceEnabled` 却是非默认值）。根因：`VoiceDeputySettings.ExposeData` 每行 `Scribe_Values.Look(ref X, "k", X)` 把**当前值**当缺省值 ⇒ `forceSave=false` 时保存侧判"等于缺省"全部跳过。修法：新增私有 `Look<T>(ref T field, string key)` ⇒ `Scribe_Values.Look(ref field, key, field, true)`，`ExposeData` 里全部 40 行（含 22 个 `enabled_<Category>`）改走它。读档缺键仍保持当前值（第三参就是当前值）。编译 0/0；**未进游戏验证**（判据：退出游戏后设置 XML 里能见到 `apiKey` / `traceEnabled` / `enabled_*`）。
- **P2（`index.json` 长期 `{}`）已修 2026-10-17**：索引原先只在 `LibraryJanitor` 每 `JanitorIntervalSeconds`（默认 300 s）落一次 ⇒ 崩溃/强杀窗口里刚合成的 wav 在索引里没有，下次进游戏对账当孤儿删掉。修法：`SpeechLibrary.Put` / `RemoveEntry` 只置私有 `indexDirty`（读侧新属性 `SpeechLibrary.IndexDirty`；`SaveIndex()` 写成功即清零），落盘仍只在 `LibraryJanitor`；新增唯一落盘入口 `LibraryJanitor.SaveIndexIfDue(bool force = false)`，节流常量 `IndexSaveMinIntervalSeconds = 5`（tick 换算 `IndexSaveIntervalTicks`，上次落盘 tick 存 `lastIndexSaveTick`，新局 tick 变小也当到期）；调用点四处：`CheckLimit()`（每次 `Put` 后，脏了才写）、`Tick()` 第 ④ 步（`force: true`，每轮无条件但要守 5 s 下限，首帧仍建出 `index.json`）、`DeputyGameComponent.TickTimers()` 每帧一次（追"最后一笔脏数据落在 5 s 窗口内"的情况，否则它要等下一轮 300 s）、`ClearAll()` 仍立即落盘。**未进游戏验证**（判据：连播几句后 5 s 内 `Cache\Voice\index.json` 非空）。
- **F1（死亡句带富文本标签）已修 2026-10-17**：实测 `[VD] 2 Jidai阵亡。(*Name)Jidai(/Name)已死亡。`（同一串还进了 `[VD] 5.1` → TTS）。修法：`SegmentPlanner` 新增私有纯函数 `StripRichText`，在 `AddArg` 里对**每个参数段**各跑一次（`AddArg` 是唯一入口 ⇒ 只改一处），去 `<...>` 标签与括号化标签 `(*Name)…(/Name)`、折叠空白、trim，结果空则整段丢弃（沿用旧规则）；`Const` 字面量不跑。顺序是**先去标签、再走 `PersonName` 清洗**（`SplitPersonName` 入口也跑同一个 `StripRichText`：名字有 `AddArg` / `JoinNames` 两条来路，幂等）。`PlanCore` 的 `RawText` 分支改走 `AddArg`（原先直接 `new Segment`），所以试听/朗读的裸文本也一并洗。**未进游戏验证**：判据是 `[VD] 2 <名字>阵亡。…` 与 `[VD] 5.1 …` 里不再出现 `(*` 或 `</`。
- **F2（`VoicePlayer` 静态资产告警）已修 2026-10-17**：类上加 `[StaticConstructorOnStartup]`，行为不动（仍是首次 `Source()` 才建 `GameObject`）。按 1.6 IL 核对过告警判定只看 `Texture`/`Material`/`Shader`/`Graphic`/`GameObject`/`MaterialPropertyBlock`（数组解到元素类型）⇒ 全 mod 只有 `VoicePlayer.host` 命中，`AssemblyResult.EmptyClips`（`AudioClip[]`）与 `AudioSource source` 不在名单里。顺带给 `About.xml` 的 Harmony 依赖补了 `<downloadUrl>`。**未进游戏验证**。
- **第 8 步与文档不一致 / 补做的 10 处（都记在这里等设计侧决定改文档还是改代码）**：
  1. **`SpeechLibrary.ListFiles()` 读目录失败时返回 `null`（不是空数组）**：04 §2.4 写"失败 ⇒ 空数组"，但 04 §4.1 ① 要求"失败 ⇒ 本轮跳过对账"。空数组会把"读目录失败"变成"索引全部失效"⇒ 缓存被清空。现在 `null` = 跳过、空数组 = 目录里真没有文件。
  2. **`SpeechLibrary.SaveIndex()` 改成返回 `bool`**：04 §4.1 ④ 要"落盘失败 ⇒ 记日志，下轮再试"，不返回成功与否就只能瞎猜；`[VD] janitor index-write-failed` 由它触发。
  3. **预热作业的 `SegmentKind` 按片段的真实种类给，不是"恒 `PersonName`"**：01 §6.12 写"短语恒 `PersonName`"，但 03 §7.1 的启动批是模板字面量（`Const`）。按 `PersonName` 存 ⇒ 键带上 `save:` 作用域，`ClipAssembler` 用 `Const` 永远查不到，预热等于白花钱。现在三种源各按 `Const` / `PersonName` / `Sentence` 建作业。
  4. **`RosterDelta` 放在 `LibraryJanitor.cs`**：06 §11 没给它指定文件；它由清理器产出、只被 `WarmupScheduler` 消费。
  5. **`SegmentPlanner.PersonNameText(raw)` 是新增的公开派生方法**：名单要按"清洗后的名字"匹配条目（条目里存的就是清洗后的 `PersonName` 文本），而名字清洗只允许一处（03 §4.3）⇒ 清理器从这里取，不自己抄规则。`AddPersonName` 与它共用同一个私有切分函数。
  6. **`WarmupScheduler` 的"丢最旧"是两个队列合起来的近似**：`WarmupJob` 没有时间戳（01 §6.12 只有 `Kind/Text/SegmentKind`），所以先丢片段队列（启动批最旧），片段队列空再丢整句队列。
  7. **`VoiceDeputySettings` 新增 `JanitorIntervalSeconds`（默认 300）与 `WarmupJobsPerMinute`（默认 100000）**：05 §6 标"待加字段"，本步的计时器与节流必须有读者；调试页两条滑条已接上（30–3600 s / 1–1000000 条/分）。
  8. **新增两个日志 id：`janitor` / `warmup`**（01 §5.2 的 id 表只到 `6`，沿用 `err` 的先例）：`[VD] janitor …` 记清理与淘汰，`[VD] warmup …` 记预热批次与节流。
  9. **`LibraryJanitor.ClearAll()` 顺带清"坏文件待删名单"**：不清的话，清缓存后重新合成出来的同名文件会被旧名单在下一轮误删（`LibraryKey` 只知道文本，不区分新旧文件）。
  10. **`ClipAssembler` 现在会登记坏文件**：`TryRead` 成功但 `WavReader` 解不开 ⇒ `LibraryJanitor.ReportBroken(key)`（第 6 步注释里留的"留给第 8 步"）；删除仍只归清理器。
- **第 8 步的两条实现口径**（不是矛盾，记下来免得下次又猜）：
  1. `LibraryJanitor` 的第一次 `Tick` 在进游戏首帧就跑（否则 `index.json` 要等 `JanitorIntervalSeconds` 才出现，06 §6 的判据没法验）；预热第一次在进游戏 2 s 后（03 §7.3 的"启动批 2 s 才排"）。
  2. 等级表只按 `SegmentKind` + "名字在不在名单里"判（04 §4.2），不显式看 `Scope`；`PersonName` / `Sentence` 的 `save:` 作用域由键本身保证。
- **第 6 步留下的 6 处（都按最字面读法实现，等设计侧决定改文档还是改代码）**：
  1. **TTS 请求格式是假设**：`docs/` 只写了端点 `https://api.siliconflow.cn/v1/audio/speech` 与 `audio/wav` 响应，没写请求体。`TtsClient.Payload` 按 OpenAI 兼容写成 `{"model","input","voice","response_format":"wav","speed"}` + `Authorization: Bearer <key>`。若服务端不认，只改 `TtsClient.Payload` 一处。
  2. **`LastError` 的类型放在命名空间层**：`TtsClient` 里既要类型 `LastError` 又要静态属性 `LastError`（文档要 UI 读 `TtsClient.LastError`），嵌套类型与成员不能同名（CS0102）⇒ 类型 `VoiceDeputy.LastError`、属性 `TtsClient.LastError`。
  3. **`TtsRequest` 多带 `JobId` / `SynthesisResult` 多带 `Kind`**：01 §6.11 / §6.12 的字段表没有这两项，但结果回来时要与作业表和片段种类对上，没有它们就只能靠"文本反查"，同名不同 kind 会错。
  4. ~~**`index.json` 在验证第 6 步时不会出现**~~ **第 8 步已接上**：`SaveIndex()` 现在由 `LibraryJanitor` 每轮调（首帧就跑一次，所以进游戏就有 `index.json`），页签里的「清缓存」也会落一次。
  5. **`PlayableUtterance` 没有指定文件**：与 `AssemblyResult` 一起放在 `AssemblyResult.cs`（它俩同属"组装产物"）。
  6. **`AssemblyResult.Missing` 的字段改名为 `MissingSegments`**：文档同时要字段 `Missing` 与工厂方法 `Missing(Segment[])`，同名编译不过（CS0102）。
- **`DeputyGameComponent` 的泵异常日志 id 是 `err`**（01 §5.2 的 id 表只到 `6`）：每段各自 try/catch，同一段 600 帧最多一条，形如 `[VD] err drain NullReferenceException`。
- **`VoicePlayer` 的音量被 `Mathf.Clamp01` 截到 1.0**：`MasterVolume × TtsVolume` 最大可到 4（两个滑条都 200%），但 `AudioSource.volume` 上限是 1。

- **`DiseaseOutbreak` 的 `Args` 比 02 §4.5 多一个人数**：03 §3 的模板 `疾病爆发，{0}，{1}人感染。` 有两个槽（`Enum` + `Number`），只给 `def.label` 会命中"Args ≠ ArgKinds ⇒ 不播半句"。所以 `IncidentHooks` 交 `[def.label, Count]`（`Count` 字段仍是 `max(1, parms.pawnCount)`）。02 §4.5 那一行要补。
- **`Pawn.LabelShort` 可能带富文本**（实测 `Pawn.get_LabelNoCount` IL：有头衔时走 `ColoredText.Colorize`）⇒ 带 Royalty 头衔的殖民者名字里会有 `<color=#…>…</color>`；名字清洗只删 `<` `>`，剩下的 `color=#…` 会念出来。进游戏若真见到，只改 `PawnHooks.NameOf` 一处（改用 `pawn.Name.ToStringShort`）。
- **第 5 步留下的 4 处**：
  1. `BatchSummary`（01 §6.5）在 06 §11 没有指定文件 ⇒ 和 `Utterance`/`UtteranceSource` 一起放在 `Utterance.cs`；第 7 步加的 `Single` 字段也在原处。
  2. ~~`Plan(BatchSummary)` 没有 `Single` 分支~~ **第 7 步已补**：`Single != null` ⇒ 走该 `Notice` 的单事件模板（`UtteranceSource.From(Notice)`），否则用 `CategoryEntry.BatchPhrase`。
  3. `PlanRaw` 的 `Category` 取 `OtherMessage`（第 6 步接试听时定稿：保持兜底值，优先级 0，只在队列里排最后）。
  4. `Settings.NumericText` **没有这个字段**（05 §6 标"待加字段"）⇒ `Number` 段一律原样送 TTS（03 §4.4 的默认分支）；等字段补上再接中文转换。
- **TEMP 自检要删**：`SpeechCatalog.SelfCheck()`（`#region TEMP`，打 `[VD] 0 …`）＋ `VoiceDeputyMod` 构造里的调用，**第 4/5 步进游戏验收后整段删除**。只在 `Settings.TraceEnabled` 为真时跑（`LongEventHandler.ExecuteWhenFinished`，等语言包就绪）。
- **第 4 步的两处补做**（都是第 3 步的缺口，不是新设计）：
  1. `SpeechCatalog.SinglePhraseOf(SpokenCategory)`：`SourceBinding` 没有 `Phrase` 字段，`Notice.Phrase` 只能由类别反查单事件模板（派生，不加表）；查不到 `throw`（01 §5.2）。
  2. `SpeechCatalog.ChannelName(SpeechChannel)`：`SpeechTypes.cs` 的注释早就写"日志拼写由 `SpeechCatalog.ChannelName` 给"，但没实现；`[VD] 1` 的通道名（含中文 `自算`）只能从这里出，钩子里不留中文字面量。
- ~~**`BatchKey != null` 的 Notice 现在也走去重→丢弃**~~ **第 7 步已补**：去重通过后进 `BatchAggregator`，tick 一变由 `Announcer.Tick` 冲成句。
- **第 7 步与文档不一致的 3 处**：
  1. **`BatchAggregator` 的持有状态是"暂存成员表"而不是"当前批"**：01 §5.3 写的是单批 `{BatchTick, Category, BatchKey, 成员, TotalCount}`，但同一 tick 里可能同时存在多个 `(Category, BatchKey)`（同一帧死人 + 崩溃）。实现改成"暂存 `Notice` 列表 + Flush 时按 `(Category, BatchKey)` 分组"，合并键与"同 tick"边界都没变。
  2. **插队多了一个 `pending.Count > 0` 条件**：01 §5.4 写"`ThreatInterrupts` 且新句优先级更高 ⇒ `pending.Insert(0, u)`"；队列为空时 `Insert(0)` 与 `Add` 等价，加这个条件是为了不让空闲时的入队打乱同级 FIFO。
  3. **`SpeechCatalog.PriorityOf` 是派生方法**：01 §5.2 / §5.4 引用它但代码里没有，按"从 `EntryOf(category).Priority` 派生、查不到回 0"补上（不新增数据表）。
- **1.6 API 与文档不一致的 4 处**（实测本机 `Assembly-CSharp 1.6.9676.17735`；按能编过的写法实现，等设计侧决定改文档还是改代码）：
  1. `Pawn.LabelShortCap` **不存在**（`Thing`/`Pawn` 都没有）⇒ 用 `LabelShort.CapitalizeFirst()`（`PawnHooks.NameOf` 一处）。
  2. `IncidentParms.def` **不存在** ⇒ 用 `IncidentWorker.def`（`__instance.def`）；`IncidentParms.pawns` **不存在** ⇒ 用 `parms.pawnCount`；`pawnsArrivalMode` 实名是 `raidArrivalMode`。
  3. `Pawn_HealthTracker.pawn` 与 `MentalStateHandler.pawn` 都是 **private 字段**（01 §5.3 写成 `this.pawn`）⇒ `PawnHooks` 里用缓存的 `FieldInfo` 读一次。
  4. `Hediff_Pregnant.StartLabor` 与 `Pawn_HealthTracker.MakeDowned` 在 1.6 都只有 1 个重载；`MakeDowned` 是 **private** ⇒ 方法名只能写字符串 `"MakeDowned"`（`nameof` 过不了可见性）。
- **⑥ 自行加了一条过滤**：`PawnUtility.ShouldSendNotificationAbout(pawn)`。01 §5.3 只写了 `pawn == null` 才丢，但 `MakeDowned` 对任何 pawn 都调（袭击者倒地也会念），与 02 §2.1「殖民者 / 宠物倒地即播」冲突。进游戏确认是否要保留这把尺子。
- **④ 没过滤 `transitionSilently`**（取证-22类来源分布 §2.2 说原版该参数为真时不发信）：01 §5.3 只给了 `IsColonist && beginLetterDef != null`。进游戏听有没有"玩家没收到信却播了崩溃"。
- **⑧ 的调用口径**：`CostListAdjusted(stuff, errorOnNullStuff: false)`，**没有**照 01 §5.3 的"`stuff == null` ⇒ 不产出"——太阳能板这类非 stuff 建筑 `stuff` 恒为 null，照做会漏掉大部分建筑。缺口判据改为"造价 0 或缺口 0 ⇒ 不产出"。
- **`[VD] 1` 的拼装集中在 `Announcer.Receive(channel, category?, defName, …)` 重载里**（钩子只交事实）。01 §8.2 说埋点者是 5 个钩子文件——语义没变，位置集中，理由：否则 5 个文件各抄一遍 `Notice` 构造与日志格式（DRY）。
- **第 3 步与文档不一致的 6 处**（已按最字面读法实现，等设计侧决定改文档还是改代码）：
  1. `SegmentKind` 被 `06 §11` 排在第 5 步的 `Segment.cs`，但第 3 步 `PhraseEntry.ArgKinds` 就要它 ⇒ 现定义在 `SpeechTypes.cs`，第 5 步 `Segment.cs` 只建 `Segment`。
  2. `02 §3.1` 的 `ForIncident` 签名只有 4 个字符串，但判定第 3 步要 `def.workerClass` 是 `IncidentWorker_Disease` 的子类 ⇒ 加第 5 个可选参数 `workerClassName = null`（4 参调用照旧）。
  3. 「`Bindings` 落 22 类」与 `02 §2.2`「`自算` 类没有 `SourceBinding` 行」冲突 ⇒ 实际 23 行覆盖 14 类（incident 9 / letter 2 / message 3），其余 8 类属自算或待实现。
  4. `02 §1.1` 说「三张丢弃表」，其中 incident 那张是「无」⇒ 代码只有 `LetterDiscardDefNames` / `MessageDiscardDefNames` 两张。
  5. `SourceBinding.TextSuffix` 要从 `MessageRottedAwayInStorage.Translate()` 派生，静态字段会在 Mod 构造期（语言包未读）求值 ⇒ `Bindings` 改成首次查询时创建；后缀不可用时 Rotten 行不建（退化成 `OtherMessage`，`02 §3.3` 允许）。
  6. `05 §6.0` 说本版删掉 `ResponseFormat` / `BatchWindowMs` / `SpeechStyle`，现有代码与 `Tab_Voice`/`Tab_Volume` 仍在用；本步不动，留给第 2 步补做（与 P4 一起看）。
- **`ConstantTexts()` 含纯标点片段**：按 `03 §4.2` 字面量原样切分，32 条里有「，」「。」「 个」这类片段，预热会把它们也送 TTS。是否符合预期待第 6 步听感（D 类）确认。

## 已定的硬事实（不要再翻案）

- **补丁点 8 个 / 5 个文件**：`Messages.Message`、`LetterStack.ReceiveLetter`、`IncidentWorker.TryExecute`、`MentalStateHandler.TryStartMentalState`、`Hediff_Pregnant.StartLabor`、`Pawn_HealthTracker.MakeDowned`、`Pawn_HealthTracker.NotifyPlayerOfKilled`、`GenConstruct.PlaceBlueprintForBuild`。
- **死亡只挂一个点**（`NotifyPlayerOfKilled`）：两支互斥都从这里出。
- **一个事件只在一个通道播**：incident 是主分类器，其余通道按 def 丢弃重复项。不做"事件身份"，不做跨通道合并。
- **每类只做一种文案**：模板 + 参数；钩子只取事实填参数，切分唯一归 `SegmentPlanner`。
- **存储只落磁盘**，内存不常驻 `AudioClip`；等级与淘汰只归 `LibraryJanitor`。
- **默认值**：开发期全开；产品默认由"跑一局 → 关掉吵的 → 回读存盘"定稿。

## 文档（只在 `docs/`）

| 文件 | 用途 |
|---|---|
| `01-架构与模块.md` | 模块契约、类型、信息流（入口） |
| `02-类别与来源.md` | 22 类 × 通道 × def |
| `03-文案与片段.md` | 模板、切分、聚合、整句缓存、预热 |
| `04-语音库与清理.md` | 键、等级、清理、上限 |
| `05-设置界面.md` | 四页签逐字段 |
| `06-编码顺序与验收.md` | 8 步 + 探针 P1–P10 |
| `模块-输入输出-信息流.html` | 上面 01 的可视化 |
| `取证-*.md`（6 份） | 静态取证，写代码时按需查 |

## 历史（只记里程碑）

- 2026-10-17 深夜：第 8 步（清理、预热、整句缓存）完成，编译 0/0，**未进游戏**；8 步代码全部写完。
- 2026-10-17 夜：第 6 步（合成与落库，第一次出声）+ 第 7 步（队列、聚合、播放调度）完成，编译 0/0，**未进游戏**。
- 2026-10-17 晚：第 4 步（8 钩子，只打 `[VD] 1`）+ 第 5 步（`SegmentPlanner` 切分，打 `[VD] 2`）完成，编译 0/0，**未进游戏**。
- 2026-10-17 下午：两轮拷打 → 模块 I/O 契约 → 两轮独立审查（21 + 23 条）→ 设计定稿 6 份。
- 2026-10-17 上午：一轮 grilling 产出初版设计；发现"被推翻的结论与推翻后的结论并列"。
- 2026-10-17 早：骨架 + 设置窗口；旧 mod 归档；整条线迁到 `语音副官/`。
