# 调研：RimTalk 的接口配置与语音播放模式

标注约定：**【实测】**＝本机读到文件/目录；**【源码】**＝读到公开仓库源码（网络，等价于反编译，但不是本机二进制）；**【推测】**＝由实测/源码推断；**【未确证】**＝没读到证据。

## 1. 定位结论

- **本机 Mods 目录里没有 RimTalk 本体【实测】**。`D:\SteamLibrary\steamapps\common\RimWorld\Mods` 下 19 个目录、20 个 `About\About.xml`，packageId 全查过，没有 `cj.rimtalk`。三个数字目录分别是 `Neronix17.TweaksGalore`(2695164414)、`aota.ratkin.affiliative.behavior`(3767050969)、`YourName.RatkinPrisonerBandage`(3768208853)。
- **但本机有一份解包副本【实测】**：`E:\2026.7以后的新工程\环世界mod\rimworld original refs\rimtalk`
  - `packageId=cj.rimtalk`，`name=RimTalk`，`modVersion=1.0.11`，作者 juicy，supportedVersions 1.5/1.6，依赖 Harmony + Jaxe.Bubbles，`PublishedFileId=3551203752`。
  - 含 `1.5/`、`1.6/` 两套 `RimTalk.dll`(392KB) + `Scriban.dll` + `IndexRange.dll`，以及 Defs、7 种语言 Keyed/DefInjected、Textures。
  - 这是"参考库"而不是游戏加载目录；游戏实际加载的是 Mods 下。
- **`ModsConfig.xml` 里 `cj.rimtalk` 是 active，但 Mods 下没有对应目录【实测】→ 启动时该 mod 会被判缺失【推测】**（同列表里还有数百个本机不存在的 mod，这份 ModsConfig 与当前 Mods 目录不匹配）。
- `RimtalkToddlers无人格版`（`cj.rimtalk.toddlers`）自身只是补丁/扩展，`loadAfter cj.rimtalk`，**反推不出 RimTalk 的配置结构**【实测】。
- **版本差异**：本机副本 1.0.11 的 Keyed 只有 398 行/约 19KB，只有 Google 简单模式；GitHub main 的 Keyed 约 39.5KB，多供应商、Custom、多套配置都是后加的【实测对比】。下文 2、4 节主要是 **main（≈1.3.x）源码**的结论；照抄前需按本机版本核对。

## 2. 模型接口配置模式

统一走 **OpenAI 兼容协议**（`POST /v1/chat/completions`），Google/Claude 也是绕到各自的 OpenAI 兼容端点或加兼容头【源码】。

| 字段 | 存哪 | 默认值 | 用户可改 |
|---|---|---|---|
| 供应商 | `ApiConfig.Provider`（enum `AIProvider`，Scribe 键 `provider`） | `Google` | 可改，下拉 15 项 |
| Endpoint/URL | **不单独存**：内置值查静态表 `AIProviderRegistry.Defs[provider].EndpointUrl`；仅 `Custom`/`Local` 用 `ApiConfig.BaseUrl` | 各厂商硬编码 | Custom/Local 可改，内置不可改 |
| API Key | `ApiConfig.ApiKey`；简易模式另有 `SimpleApiKey`/`SimplePlayer2ApiKey` | `""` | 可改，普通 TextField 明文显示 |
| 模型名 | `ApiConfig.SelectedModel` + `CustomModelName`（Local 只用后者） | `Constant.DefaultCloudModel` / 占位 `ChooseModel` | 可改（拉列表或手填 Model ID） |
| 额外请求参数 | `ApiConfig.CustomRequestJson`，与基础 JSON 做 `MergeJson`（可覆盖 `thinking`/`reasoning_effort`） | `""`＝不覆盖 | 可改（每行齿轮→`Dialog_CustomizeRequest`） |
| 多套配置/回退 | `List<ApiConfig> CloudConfigs` + `CurrentCloudConfigIndex`，失败 `TryNextConfig()` 切下一条 | 1 条 | 可改（+ / ▲▼ / 勾选启用 / ×） |
| 简易/高级模式 | `UseSimpleConfig` + `SimpleProvider`（Google / Player2） | `true` / Google | 可改（三张卡片选模式） |

- **落盘形态**：标准 `ModSettings.ExposeData()` → `Config\Mod_<mod标识>_<Settings类名>.xml`，API key 以 **明文** 存在 `<simpleApiKey>` / `cloudConfigs` 等节点【源码 + 同生态实测：`Config\Mod_3612632140_RimTalkEventPlus.xml` 就是这个形态】。RimTalk 本体本机从未生成过该文件（Config 里没有）【实测】，所以 RimTalk 那份的具体文件名是【推测】。
- **明文风险提示：没找到**。README、创意工坊描述、Keyed 文案都没有"key 会明文存本地/别分享 Config"之类提醒【未确证：只读了这些入口，没查全部 1400+ 条评论】。
- **连通性检查**：不是独立"测试连接"按钮，而是**选模型时 GET `ListModelsUrl`**（10s 超时），失败菜单显示 `(no models found - check API Key)`；本地供应商多一个 `Local Auto Detect`，并发探 `localhost:11434/1234/8080/9931/8000/5000/5001/1337` 的 `/v1/models`；没配 key 时 `TickManager` 弹一次 `RimTalk.TickManager.ApiKeyMissing`【源码】。
- **失败提示**：`TalkService.APIError` / `QuotaReached`（429）/ `TryingNextAPI`；Debug 窗口可按行打开 `Dialog_ApiLog`（左请求 JSON、右响应+状态码+耗时），支持改 prompt 后 **Resend**【源码 + 工坊描述】。
- **语气/人设 prompt 放哪**：**不在 Def 里**。三处：① `SimpleModeInstruction`（简单模式一段基础指令，默认 `Constant.DefaultInstruction`）；② `PromptSystem`（`PromptManager` 的 preset/entry 树，Scriban 语法，支持导入导出 `.json`）；③ 每个 pawn 的人格存在 **Hediff** `RimTalk_PersonaData`（`Hediff_Persona`）上，预设人格名字池在 Keyed `RimTalk.Persona.*`【源码 + 本机 Defs 实测】。
- 合法请求判定：`ApiConfig.IsValid()` = 启用 +（Local 有 BaseUrl / 云端有 key 且模型已选）；Player2 允许只靠本地 App 在线【源码】。

## 3. 音频播放实现（TTS 不在本体）

- **RimTalk 本体不做 TTS【实测 + 源码】**：本机副本无任何音频资源、Defs 无 SoundDef、源码树无 audio 相关文件；README 与工坊描述也没提语音。
- 语音是生态层的 **RimTalk TTS Addon**（工坊 3618965326，`whatismyname0/RimTalkTTSModule`，MIT-ish 开源），Harmony 非侵入式，不改 RimTalk 核心【源码 + 工坊】。
- 供应商：**EdgeTTS（免 key 免费）/ Fish Audio / CosyVoice2、IndexTTS2（经 SiliconFlow）/ Azure / Gemini TTS / 自定义 WebUI 端点**；Fish Audio 需本机 Python 3.9+ 与 `fish-audio-sdk`（C# 起 Python 子进程）【工坊 + 源码树】。
- 请求体是自建 DTO，不是某个固定协议：`TTSRequest { ApiKey, Model, Input, InstructText, Voice, Speed, Volume, Temperature, TopP, References[{Audio,Text}] }`，各 provider 客户端自己拼 JSON / 走 WebSocket（EdgeTTS）/ 走子进程（Fish）【源码】。
- **播放：完全自建，绕过原版混音器——是【实测源码】。**
  - `[StaticConstructorOnStartup]` 里 `new GameObject("RimTalkAudioPlayer")` + `DontDestroyOnLoad` + `AddComponent<AudioSource>()`，`spatialBlend=0f`（2D）、`dopplerLevel=0`。
  - 直接 `_audioSource.clip = clip; volume=...; Play()`。**不用** `SoundDef`/`SoundStarter.PlayOneShot`/`ContentFinder<AudioClip>`，**不设** `outputAudioMixerGroup`；音量只受自己的滑条控制，不吃游戏"音乐/音效音量"滑条【源码 + 推测：未显式设 mixer group 即挂默认组】。
- **格式解析全是手写【实测源码】**：
  - WAV：手写 RIFF 遍历 `fmt `/`data` chunk，只处理 8/16 bit PCM，`float[]` 归一到 ±1，`AudioClip.Create(samples/channels, channels, sampleRate, false)` + `SetData`。
  - MP3：先写临时文件 `%TEMP%\rimtalk_audio_<guid>.mp3`，再 `UnityWebRequestMultimedia.GetAudioClip("file:///...", AudioType.MPEG)`，用完删文件。
  - **只接受直接返回音频字节**；返回 URL 不支持（作者在评论区明确说过）【工坊评论】。
- **缓存/队列**：无磁盘缓存；`Dictionary<Guid, byte[]>` 按对话 id 暂存，播完即删；单例 AudioSource **串行**播放——播放中无限等前一条，等 TTS 最多 30s（超时跳过该句），播放结束用 `Task.Delay(clip.length*1000)` 猜【实测源码】。
- **阻塞与取消**：补丁把该句对话的显示挂起直到音频就绪（`RimTalkPatches.IsBlocked`）；RimTalk 丢弃/忽略对话时用 `RemovePendingAudio` 同步取消【源码 + 工坊】。
- 已知坑（来自作者/评论）：微软 2026-10 换 EdgeTTS 端点导致全体静音（1.7 版修）；Alt+F4 可能留下 TTS 服务进程；改 `AllowSimultaneousConversations` 建议关掉，否则排队。

## 4. 事件触发结构（概述）

**驱动**：`TickManager.DoSingleTick` 的 Harmony Postfix（`TickManagerPatch`）是总时钟：每 0.5s 调 `TalkService.DisplayTalk()` 与 `CustomDialogueService.Tick()`，每 `TalkInterval` 秒挑一个 pawn（`PawnSelector.SelectNextAvailablePawn`），顺序是「通用池 `TalkRequestPool` → 该 pawn 自己的请求队列 → 否则凭空 Chitchat」；`User/Announcement/Interaction/Urgent` 属 fast-track，走 `UserRequestPool`，每 tick 处理且可抢占当前低优先级请求（`AIService.CancelCurrent`/`CanCancelFor`）。**触发源**是一批 Harmony 补丁在各处 `new TalkRequest(...)` 塞进队列：社交互动、事件/信件（`ArchivePatch`）、战斗日志、精神状态、想法、技能升级、生物页签、右键浮动菜单（玩家主动聊）、玩家公告。**组装**：`ContextBuilder` 在主线程把 pawn+环境序列化成文本（按 Context Filter 开关裁剪）→ `PromptManager`/`ScribanParser` 渲染 Scriban 模板 → `Task.Run` 后台 HTTP（流式增量解析 `JsonStreamParser`）→ 回主线程 `PawnState.QueueIncomingResponse` → `DisplayTalk` 造 `PlayLogEntry_RimTalkInteraction`（InteractionDef `RimTalkInteraction`）进 `Find.PlayLog`，由 Response 气泡/Interaction Bubbles 显示；可选按 `ApplyMoodAndSocialEffects` 加 `RimTalk_Chitchat/KindWords/Slighted` 想法与好感变化。

**类名清单**：`TalkService`、`AIService`、`TickManagerPatch`、`PromptManager`、`PromptService`、`ContextBuilder`、`ScribanParser`、`PromptPresetAssembler`、`TalkRequest`/`TalkType`/`TalkResponse`/`TalkRequestPool`/`UserRequestPool`/`PawnState`/`Cache`、`PawnSelector`、`PlayLogEntry_RimTalkInteraction`、`Hediff_Persona`、`BubbleCompatibilityPatch`、`DebugWindow`/`Dialog_ApiLog`、`OpenAIClient`/`AIProviderRegistry`/`ApiConfig`/`RimTalkSettings`、对外 API `RimTalk.API.RimTalkPromptAPI` + `Cache.Get(pawn).AddTalkRequest(prompt, recipient)`。

## 5. 可直接照抄的 5 条

1. **一套 OpenAI 兼容代码吃所有厂商**：provider 只决定 `EndpointUrl/ListModelsUrl/ExtraHeaders` 三件事，URL 只给域名时自动补 `/v1/chat/completions` —— 换供应商不用改请求代码。
2. **端点用静态 ProviderDef 表，设置里只存 Provider + key + model**：新增厂商 = 加一行表，存档结构不变（升级不炸档）。
3. **主线程读游戏状态、后台线程发请求、回主线程入队显示**：`ContextBuilder` 同步 + `Task.Run` 异步 + `QueueIncomingResponse`，绝不把 `Find/Map/Pawn` 带进后台线程。
4. **用 `GET /v1/models` 当连通性测试**：10s 超时 + 明确失败文案 + 可复制的请求/响应日志（比"测试连接"按钮更省事，还顺带拿到模型列表）。
5. **多套 ApiConfig 顺序回退**：一条 429/400 就自动切下一条并保存当前索引，用户无感。

## 6. 明确不要照抄的 3 条

1. **API key 明文写进 `Config\Mod_*.xml` 且 UI 不做任何提示** —— 备份、报 bug 贴 Config、云同步都会泄 key；至少给掩码显示 + 一键清空 + 风险文案。
2. **用 `Task.Delay(clip.length)` 猜播放结束 + 单例 AudioSource 串行** —— 设备采样率/缓冲一偏就错位，两个 pawn 同时说话直接互相堵 30s；应监听 `AudioSource.isPlaying` 或用协程/`OnAudioFilterRead`。
3. **给每种 TTS 都单独写客户端 + 依赖本机 Python 子进程（Fish Audio）** —— 部署面（Python 3.9+/SDK）和体积成本爆炸；先只做「HTTP 返回 WAV/16bit」一条路径 + 一个可配 endpoint。

## 7. 待办问题（要跑游戏/反编译才能答）

1. **本机 `cj.rimtalk` active 但无目录，实际启动会不会红字/丢档？** 跑一次看 `Player.log`。判断：**会反复遇到 → 长期**（应进统一能力库）。
2. **本机 refs 副本 1.0.11 与工坊当前版差多少？** 需对 `RimTalk.dll` 做 IL/元数据对比（设置项、类名是否还在）。判断：**长期**（做兼容/抄接口前每次都要对版本）。
3. **RimTalk 本体设置文件的真实文件名与节点名**（是 `Mod_cj.rimtalk_RimTalkSettings.xml` 还是 `Mod_RimTalk_*.xml`）。需在真机改一次设置并存盘后看。判断：**一次性**，答完即写回本文档。
4. **自建 AudioSource 会不会被游戏主音量静音/影响**（`AudioListener.volume`、混音器默认组）——需真机把"音效音量"拖到 0 听是否还出声。判断：**一次性**，但结论长期复用。
5. **TTS 的"阻塞显示"能否被 RimBridgeServer/自动化断言到**（音频是否真的播、用时多少）。判断：**一次性**，做 TTS 验收时再写。
6. **EdgeTTS 免 key 走非官方接口的合规与稳定性**（已因微软换端点断过一次）。判断：**长期风险跟踪**，不是脚本能答的。

> 本文未写任何探测脚本；以上"需要脚本/工具"的项目全部只登记在待办，未实施。
