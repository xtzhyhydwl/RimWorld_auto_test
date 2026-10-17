# 副官语音 v2 — STATUS

> 这条线**唯一的状态文件**。流程见 skill `rimworld-mod-pipeline`。最后更新：2026-10-17。
> 入口：`README.md`（目录与文档地图）。

## 现在

- **第 3 步（目录表与开关闸）已完成**：`dotnet build` **0 错误 0 警告**；B 类判据还没跑（没有游戏），靠 `[VD] 0` 自检日志在第一次进游戏时补。
- **代码状态**：第 1–3 步的代码全部写完、编译通过、**从未进过游戏**。
- **下一步**：第 4 步——5 个 Hook 文件 + 8 个补丁点 + `Trace`，只打日志不发声；那是第一个可自动验收的点（判据 A + B + C）。
- **阶段 5（地基探针）：未做。** 探针清单 P1–P10 在 `docs/06-编码顺序与验收.md` §9。

## 待办

- **TEMP 自检要删**：`SpeechCatalog.SelfCheck()`（`#region TEMP`，打 `[VD] 0 …`）＋ `VoiceDeputyMod` 构造里的调用，**第 4 步进游戏验收后整段删除**。只在 `Settings.TraceEnabled` 为真时跑（`LongEventHandler.ExecuteWhenFinished`，等语言包就绪）。
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
