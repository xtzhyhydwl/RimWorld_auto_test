# 模块 I/O 草案 · 修订指令（2026-10-17）

> 依据：`docs/审查-模块IO-20261017.md` 的 H1–H21 + 用户 2026-10-17 的两条决定。
> 这份文件是**唯一**的处置口径：改草案、画 HTML 都以它为准。冲突时以本文为准。

## 用户已定（覆盖草案原有写法）

- **U3 `Notice` 形态 = 模板 + 参数（Q3 已定，作废 [待确认]）。** 并且划清一条职责线：
  - **钩子只做两件事**：取到事实、把事实填进参数。钩子**不切分、不拼中文、不决定哪段是名字**。
  - **切分与排列归独立模块**（`SegmentPlanner`）：它吃 `{Phrase, Args[], Names[], Count}`，吐 `Segment[]`。这是它的唯一职责，别的模块不许有第二套切分。
  - 因此"钩子直出片段（方案 C）"不采用：它不是与 B 并列的形态，而是把 `SegmentPlanner` 的活搬进钩子，与"取信息"耦合。

- **U1 疾病/群体事件不点名。** 不扫地图、不从 `lookTargets` 取名单、不在钩子里解析受害者。群体事件只报数量（"8 人感染"）。`Notice.Names` 只对该钩子天然就有集合的通道使用（如 `TryStartMentalState` 的单个 pawn、信件自带的 `lookTargets`），拿不到就留空，只念 `Count`。
  ⇒ 草案 §5.2 的 A/B/C 与 §5.3 的 A/B/C 全部作废，统一成"能给就给、拿不到就报数"。
- **U2 补丁点 = 7 个，5 个文件。**（R2-15 已修正）

## 逐条处置

| 编号 | 处置（照这个改） |
|---|---|
| **H1** | **"缺哪些片段"归 `ClipAssembler` 判**。主链改成：`SegmentPlanner → Utterance → ClipAssembler.Plan(utterance)` 返回 `Ready(PlayableUtterance)` 或 `Missing(Segment[])`；缺的交给 `SegmentSynthesizer`；合成结果写库后**再走一次** `ClipAssembler`。`SegmentSynthesizer` 只接"缺片段清单"，不再接整句。§4.1 必须画出"查库 → 缺 → 合成 → 回填 → 再组装"这条回路。 |
| **H2** | 键对象化：`LibraryKey{ VoiceFingerprint, Scope, Text, SegmentKind }`（整句用 `SegmentKind.Sentence`）。`Put(LibraryKey, byte[])` / `TryRead(LibraryKey, out byte[])`。`SaveFingerprint` 由 `DeputyGameComponent` 注入 `SpeechLibrary`，**不进键的构造现场**。 |
| **H3** | **已由取证解决（`docs/取证-GameComponent注册.md`）：不注册。** 写一个非抽象 `GameComponent` 子类，**必须带 `public DeputyGameComponent(Game game)` 构造函数**；`Game..ctor` 的 `FillComponents()` 会用 `GenTypes.AllSubclassesNonAbstract + Activator.CreateInstance(type, {game})` 自动收养它。读档时 `ExposeSmallComponents` 用 `Scribe_Collections.Look(ref components, "components", LookMode.Deep, {game})` 整表替换后再 `FillComponents()` 补缺失类型（自带 `RemoveAll(c => c == null)` + `GetComponent` 判重，不会重复）。⇒ 草案 §2.1 删掉"注册进 `Current.Game.components`"这条职责；`DeputyGameComponent` 的契约里写明"由框架自动创建，构造参数是 `Game`"，并注明**新开局时 `ExposeData` 在 `StartedNewGame` 之后运行**（GUID 就地铸、幂等）。 |
| **H4** | "当前正在播的句子 + clip 游标"整块归 `VoicePlayer`；`SpeechQueue` 只保存**未开始播**的句子（`Peek` + `MarkDone`，或 `TryDequeue` 后由 Player 持有，二者选一并写明）。插队规则改成"插到队首"而不是"插到 `pending[0]` 之后"。 |
| **H5** | 删掉 `SpeechLibrary.Trim()`。超限判据归 `LibraryJanitor`：`Put` 只返回 `LibraryUsage`，由 `DeputyGameComponent` 在每次 Put 后调 `janitor.CheckLimit()`。库永远不知道上限。 |
| **H6** | `Notice` 增加 `Count`（int），与 `Names` 解耦：`Names` 是**要点名的**人（0–N），`Count` 是**总人数**。 |
| **H7** | `SpeechLibrary` 增加只读 `ListFiles()`（返回磁盘上的键与字节数），供 janitor 对账；仍不放开 I2。 |
| **H8** | 索引落盘：二选一并写明调用者——(a) `SpeechLibrary.SaveIndex()` 由 `DeputyGameComponent` 每 5 分钟调一次；(b) 并入 janitor 每轮第 ④ 步。**推荐 (b)**，少一条边。 |
| **H9** | 跨通道去重：按 **"事件身份（类别 + defName + 目标）"** 收敛，窗口 1.5 s，**待用户确认（Q17）**。草案里按这个写，加 `[待确认]` 标记。 |
| **H10** | `Job` 增加 `Total / Arrived / Failed` 三个计数；`Drain` 的就绪判据写死为 `Arrived + Failed == Total`。 |
| **H11** | 粒度统一成**单条**：`SynthesisResult{ JobId, Text, WavBytes?, Error? }`；批量由 `SegmentSynthesizer` 自己聚合，不再有 `Texts[]/Wavs[]`。 |
| **H12** | `TtsClient` 持有 `LastError` 快照（最近一次失败的 状态码 + 响应体前 200 字），UI 读它；`Trace 5.3` 在**主线程**记。 |
| **H13** | `WarmupScheduler` 输入加 `SpeechLibrary`：先查"整句/片段是否已在库"，命中就不排。 |
| **H14** | UI 删两行：`Tab_Volume` 的"合并窗口"、`Tab_Voice` 的"说话风格"；`BatchWindowMs` / `SpeechStyle` 两个字段标"退役，UI 不暴露"。 |
| **H15** | 删掉"`Notice` 是 struct 所以不进堆"的论证；`Notice` 改成 `sealed class`，并写一句"数组字段只读、不共享"。 |
| **H16** | 拆名字：`SaveFingerprint`（存档 GUID）与 `VoiceFingerprint`（`sha1(BaseUrl|Model|Voice)`）。全文不再出现裸"指纹"。 |
| **H17** | `FullText` 只留一个真源（`Utterance` 上的计算属性）；`Scope` 不落库，用时由 `(IsSentence, SegmentKind)` 推。 |
| **H18** | `SpeechCatalog` 的消费者改对：`VoiceDeputySettings` 构造与 `ExposeData` 遍历 `Entries`；补一条启动顺序边 `SpeechCatalog → VoiceDeputySettings`。 |
| **H19** | 名字清洗只归 `SegmentPlanner`；钩子只交出 `Pawn` 或 `LabelShortCap` 原文，不做清洗。 |
| **H20** | 缺料播报只在**玩家手动放置**时触发（叠 `Designator_Build.DesignateSingleCell`），自动重建/样板不播。写进契约 + 探针项 P8。 |
| **H21** | 补一张"22 类 × 产出补丁点 × def 名"总账表，没有产出者的标 `[无产出者]`（预期至少 `BillSuspended`）。 |

## 仍然待用户答

- **Q3** `Notice` 的形态（扁平文本 / 模板+参数 / 钩子直出片段）——用户在要解释，未定。草案暂按"模板+参数"写，加 `[待确认]`。
- **Q17** 跨通道去重是否按"事件身份 + 1.5 s"收敛。
