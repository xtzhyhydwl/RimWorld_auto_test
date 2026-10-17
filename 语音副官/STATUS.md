# 副官语音 v2 — STATUS

> 这条线**唯一的状态文件**。流程见 skill `rimworld-mod-pipeline`。最后更新：2026-10-17。
> 入口：`README.md`（目录与文档地图）。

## 现在

- **第 7 步（播放调度与聚合）已完成**：`SpeechQueue`（上限 / 优先级插队 / 丢最旧最低）、`BatchAggregator`（同 tick 合并 + tick 末出句）、`VoicePlayer` 完整版（`clipIndex` + `nextDspTime` + 最近 20 条 + `CurrentPriority`）、`BatchSummary.Single`、`Announcer.Tick` 串聚合。`dotnet build` **0 错误 0 警告**；**从未进游戏**（A 过，B/C/D 类没跑）。
- **代码状态**：第 1–7 步全部写完、编译通过、**未进过游戏**；配置齐全且 key 有效时理论上会出声、会排队。
- **下一步**：第 8 步——`LibraryJanitor` / `WarmupScheduler` / `DeputyGameComponent` 两个计时器 / `VoicePlayer` 播完交整句 / `Tab_Debug` 接真。
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

- **第 6 步留下的 6 处（都按最字面读法实现，等设计侧决定改文档还是改代码）**：
  1. **TTS 请求格式是假设**：`docs/` 只写了端点 `https://api.siliconflow.cn/v1/audio/speech` 与 `audio/wav` 响应，没写请求体。`TtsClient.Payload` 按 OpenAI 兼容写成 `{"model","input","voice","response_format":"wav","speed"}` + `Authorization: Bearer <key>`。若服务端不认，只改 `TtsClient.Payload` 一处。
  2. **`LastError` 的类型放在命名空间层**：`TtsClient` 里既要类型 `LastError` 又要静态属性 `LastError`（文档要 UI 读 `TtsClient.LastError`），嵌套类型与成员不能同名（CS0102）⇒ 类型 `VoiceDeputy.LastError`、属性 `TtsClient.LastError`。
  3. **`TtsRequest` 多带 `JobId` / `SynthesisResult` 多带 `Kind`**：01 §6.11 / §6.12 的字段表没有这两项，但结果回来时要与作业表和片段种类对上，没有它们就只能靠"文本反查"，同名不同 kind 会错。
  4. **`index.json` 在验证第 6 步时不会出现**：06 §6 的 B 判据写"`Cache/Voice/` 出现 `.wav` 与 `index.json`"，但 04 §2.3 / §4.1 ④ 规定 `SaveIndex()` 只由 `LibraryJanitor` 调 ⇒ 第 8 步之前只有 `.wav`、没有 `index.json`（重启后缓存等于清零，功能不受影响）。要么接受"第 8 步才有 index.json"，要么允许 `Put` 顺手落索引（违反 04 §4.6）。
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

- 2026-10-17 夜：第 6 步（合成与落库，第一次出声）+ 第 7 步（队列、聚合、播放调度）完成，编译 0/0，**未进游戏**。
- 2026-10-17 晚：第 4 步（8 钩子，只打 `[VD] 1`）+ 第 5 步（`SegmentPlanner` 切分，打 `[VD] 2`）完成，编译 0/0，**未进游戏**。
- 2026-10-17 下午：两轮拷打 → 模块 I/O 契约 → 两轮独立审查（21 + 23 条）→ 设计定稿 6 份。
- 2026-10-17 上午：一轮 grilling 产出初版设计；发现"被推翻的结论与推翻后的结论并列"。
- 2026-10-17 早：骨架 + 设置窗口；旧 mod 归档；整条线迁到 `语音副官/`。
