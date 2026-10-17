# 副官语音 v2 — STATUS

> 这条线**唯一的状态文件**。流程见 skill `rimworld-mod-pipeline`。最后更新：2026-10-17。
> 入口：`README.md`（目录与文档地图）。

## 现在

- **第 4 步（5 个 Hook 文件 + 8 个补丁点 + 只打日志）已完成**：`dotnet build` **0 错误 0 警告**；Harmony 引用已加到 csproj（编译借 `0Harmony.dll`，跑游戏仍靠 `brrainz.harmony`）。判据 **A 过**；**B/C 类没跑**（没进游戏）。
- **代码状态**：第 1–4 步的代码全部写完、编译通过、**从未进过游戏**。
- **下一步**：第 5 步——`Segment` / `Utterance` / `UtteranceSource` / `SegmentPlanner`，`[VD] 2` 从 `accept <类别> <模板键>` 换成整句原文。
- **进游戏先看**：`[VD] 1 incident ThreatRaid`（袭击）、`[VD] 1 自算 PawnDowned`（倒地）、`[VD] 1 自算 PawnDied`（死亡）、`[VD] 1 incident DiseaseOutbreak`（疾病）、`[VD] 1 自算 BuildMissingMaterials`（缺料蓝图）、`[VD] 1 drop Death`（命中丢弃表）。`Settings.TraceEnabled` 默认**关**，先在调试页打开。
- **阶段 5（地基探针）：未做。** 探针清单 P1–P10 在 `docs/06-编码顺序与验收.md` §9。

## 待办

- **TEMP 自检要删**：`SpeechCatalog.SelfCheck()`（`#region TEMP`，打 `[VD] 0 …`）＋ `VoiceDeputyMod` 构造里的调用，**第 4 步进游戏验收后整段删除**。只在 `Settings.TraceEnabled` 为真时跑（`LongEventHandler.ExecuteWhenFinished`，等语言包就绪）。
- **第 4 步的两处补做**（都是第 3 步的缺口，不是新设计）：
  1. `SpeechCatalog.SinglePhraseOf(SpokenCategory)`：`SourceBinding` 没有 `Phrase` 字段，`Notice.Phrase` 只能由类别反查单事件模板（派生，不加表）；查不到 `throw`（01 §5.2）。
  2. `SpeechCatalog.ChannelName(SpeechChannel)`：`SpeechTypes.cs` 的注释早就写"日志拼写由 `SpeechCatalog.ChannelName` 给"，但没实现；`[VD] 1` 的通道名（含中文 `自算`）只能从这里出，钩子里不留中文字面量。
- **`BatchKey != null` 的 Notice 现在也走去重→丢弃**（还没有同 tick 合并）；`BatchAggregator` 与 `Announcer.Tick` 是第 7 步。
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

- 2026-10-17 下午：两轮拷打 → 模块 I/O 契约 → 两轮独立审查（21 + 23 条）→ 设计定稿 6 份。
- 2026-10-17 上午：一轮 grilling 产出初版设计；发现"被推翻的结论与推翻后的结论并列"。
- 2026-10-17 早：骨架 + 设置窗口；旧 mod 归档；整条线迁到 `语音副官/`。
