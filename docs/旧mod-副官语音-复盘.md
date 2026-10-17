# 旧 mod 复盘：副官语音 / Voice Deputy（xiayuan2004.VoiceDeputy）

> 取证对象：`D:\SteamLibrary\steamapps\common\RimWorld\Mods\demo`（只读，未修改/未移动/未删除任何文件）。
> 取证时间：2026-10-17。游戏环境：RimWorld **1.6.4871 rev590**，Unity **2022.3.35f1**（实测，`Version.txt` / `UnityPlayer.dll`）。
> 标注约定：**【实测】**= 我读到的文件/数据直接支持；**【推测】**= 我的判断；**【未确证】**= 需要跑游戏或反编译才能确定。
> 文件简称：`RVH`=`Source/RaidVoiceHandler.cs`(1300 行)、`TTS`=`Source/TTSClient.cs`(465)、`MM`=`Source/ModMain.cs`(138)、`SET`=`Source/VoiceDeputySettings.cs`(38)、`EV`=`Defs/SoundDefs/Events_Voices.xml`(973)、`SFX`=`Defs/SoundDefs/Voices_SFX.xml`(665)。

---

## 1. 一句话概述

**【实测】** 一个"事件监听 → 拼语音序列 → 播出来"的 mod：Harmony 钩住 `IncidentWorker.TryExecute` 等 7 个点，把袭击/疾病/死亡/分娩/精神崩溃等事件翻译成一串有序片段（`SlotList`），每个片段要么是 121 个预录 `.ogg` 之一，要么是运行时去本地 HTTP 服务（Edge TTS / Piper / VoxCPM，端口 8765/8766/8767）合成的 WAV；片段按 `AudioSettings.dspTime` 游标**无缝排队**播完，动态内容（人名、派系名、商人类型、疾病名）走 TTS 并落盘缓存。
代码量很小：C# 4 文件 1941 行 + csproj 70；Python 2 文件 222 行；Rust 1 文件 130 行。体积 177.5 MB / 434 文件，绝大多数是音频与模型（**实测**）。

---

## 2. 功能清单（实测）

| # | 触发点（游戏内） | 播报内容 | 证据 |
|---|---|---|---|
| 1 | 任何 `IncidentWorker` 执行成功（`TryExecute` 返回 true） | 总入口：`RaidEnemy`+敌对派系 → 走袭击拼装；其它 → 查表播单条预录 | `RVH:14-35` |
| 2 | 敌人袭击 | 5 维拼接：`[警示] [派系] [策略] [抵达方式] [规模]`。`CenterDrop`/`Siege` 用"警报"音，否则"侦测到"；points `<400` 小 / `<1200` 中 / 否则大 | `RVH:453-507` |
| 3 | 商队抵达 | 预录"商队抵达" + TTS `派系名，商人类型`；商人类型靠 `Traverse` 反射读 `parms.traderKind` 或 `__instance.traderKind`（15 种映射）；`CaravanArrivalTributeCollector` 跳过（它有自己的预录） | `RVH:41-98`，映射表 `:44-62` |
| 4 | 疾病事件 | 两路并发：① 事件表播预录"X病爆发"（`EV:456-598`）；② `Patch_Disease` 扫 `FreeColonists` 的 hediff 猜感染者 → TTS `名字列表` + `疾病名` | `RVH:103-160`、`:512-528`、`:550-591` |
| 5 | 殖民者倒地 | 麻醉/镇静 → 完全静音；紧张性木僵 → 预录"精神崩溃" + TTS `名字`+"紧张性木僵"；其它 → 预录"有人倒地" + TTS `名字` | `RVH:167-214` |
| 6 | 殖民者死亡 | 预录"殖民者死亡" + TTS `名字`（Prefix，死亡前取名字） | `RVH:222-231` |
| 7 | 分娩（`Hediff_Pregnant.StartLabor`） | 已倒地：预录"殖民者分娩"+"已倒地"+TTS 名字；未倒地：预录分娩 + TTS 名字 | `RVH:239-270` |
| 8 | 精神崩溃（`TryStartMentalState`） | 8 s 内同人去重；1.5 s 内不重播预录；**2 s 窗口 + 同崩溃类型聚簇**，2.5 s 后 flush 成一条 TTS：`名字1、名字2、… + 崩溃类型 label` | `RVH:276-358` |
| 9 | 其余 74 个事件 defName（60 个原版 1.6 + 14 个鼠族系） | 查 `EventVoices` 播单条预录；`VisitorGroup` 额外 TTS 派系名 | `RVH:550-627`、`:632-678` |
| 10 | 未映射事件（mod 兼容兜底） | 直接取 `IncidentDef.label` 送 TTS（中文环境念中文，英文环境念英文） | `RVH:662-677` |
| 11 | 空闲时（无事件、无批量请求） | 每 3 s 预热 1 条：世界可见派系名 + 当前地图殖民者名，一次性收集 | `RVH:844-912` |
| 12 | TTS 服务器断联 | 每 10 s(600 tick) 健康检查；连续 10 次失败（≈100 s）→ 主线程播预录"服务器断联" | `RVH:1242-1297` |

**没有实现 / 不可达（实测）**：越狱、奴隶叛乱、邪教徒袭击 —— 代码里只有注释 "TODO — LordMaker.MakeNewLord 泛型歧义"（`RVH:233-236`）。对应音频 `vo_event_prison_break / slave_rebellion / cultist_raid` 存在但永不播放；同类不可达片段还有 `VoiceDeputy_Alert_Attention`、`VoiceDeputy_Faction_Insect`（在 121 个 defName 里只出现 1 次，即只出现在 `ClipPaths` 表内）。

---

## 3. 架构（实测）

### 3.1 Harmony patch 台账（符号台账种子）

| # | 目标类型:方法 | 补丁类型 | 签名 | 为什么打这里 |
|---|---|---|---|---|
| 1 | `IncidentWorker:TryExecute` | Postfix | `(IncidentParms parms, IncidentWorker __instance, bool __result)` | 唯一"所有 incident 都经过"的公共出口；`__result` 保证只在真执行后播；`__instance.def.defName` 拿事件名 → `RVH:14-35` |
| 2 | `IncidentWorker_TraderCaravanArrival:TryExecuteWorker` | Postfix | `(IncidentParms, IncidentWorker_TraderCaravanArrival, bool)` | 需要 `__instance`/`parms` 的 `traderKind`（私有字段）才能说出商人类型 → `RVH:41-98` |
| 3 | `IncidentWorker_Disease:TryExecuteWorker` | Postfix | `(IncidentParms, IncidentWorker, bool)` | 必须执行后才能从 `parms.target as Map` 扫到"谁染病了" → `RVH:103-160` |
| 4 | `Pawn:Notify_Downed` | Postfix | `(Pawn __instance)` | 倒地不是 incident，没有事件入口，只能拦通知方法 → `RVH:167-214` |
| 5 | `Pawn:Kill` | **Prefix** | `(Pawn __instance)` | 死亡后 Pawn 名字/状态不可靠，必须死前取 → `RVH:222-231` |
| 6 | `Hediff_Pregnant:StartLabor` | Prefix | `(Hediff_Pregnant __instance)` | 1.6 分娩不是 incident，只能拦 Hediff 方法；Prefix 为了读"是否已倒地" → `RVH:239-270` |
| 7 | `MentalStateHandler:TryStartMentalState` | Postfix | `(MentalStateHandler __instance, bool __result)` | 需要 `__result` 判断真的开始了；`pawn` 是私有字段要用 `Traverse` 读 → `RVH:276-358` |

反射依赖（新 mod 可直接换公开 API）：`parms.traderKind` / `__instance.traderKind`（`RVH:79-86`）、`MentalStateHandler.pawn`（`:290`）、`Pawn.MentalState`（`:312`）、`MentalState.def`、`MentalStateDef.label`（`:315-321`）—— 全部 `Traverse`，共 8 处。

**【实测】** 上表 7 个目标符号在 `RimWorldWin64_Data\Managed\Assembly-CSharp.dll` 的元数据字符串里全部存在（`Notify_Downed` / `StartLabor` / `TryStartMentalState` / `TryExecuteWorker` / `IncidentWorker_Disease` / `IncidentWorker_TraderCaravanArrival` / `Hediff_Pregnant` / `MentalStateHandler`）。
**【未确证】** 是否存在**重载歧义**：`Pawn.Kill`、`IncidentWorker.TryExecute`、`Hediff_Pregnant.StartLabor` 在 1.6 是否只有一个重载未验证；若有多个，Harmony 按名匹配会抛 `AmbiguousMatchException`，该 patch 静默失效。

### 3.2 进程 / 线程模型

```
游戏主线程                          后台线程(ThreadPool)            外部进程
─────────────────────────────────────────────────────────────────────────────
Incident patch ──> SlotList ──> EnqueueSlots ─┬─> RequestBatchFor ──> SynthesizeBatch ──HTTP POST──> 127.0.0.1:8765/tts (Edge)
                                              │                                             8766/tts (Piper)
GameComponentUpdate (每帧) ──> AdvanceSlots ──┘                        8767/tts (VoxCPM Rust)
   └─ 用 ContentFinder<AudioClip>.Get 载预录 → new GameObject+AudioSource → PlayScheduled(dspTime)
```

- **协议**：纯 HTTP/1.1 + JSON，无管道/无 socket 自定义协议。请求 `{"text":..,"voice":..,"rate":"+30%"}`；Edge 服务回**整段 WAV 字节**（`rvh:TTS:352-417`、`tts_server.py:60-88`）。VoxCPM 回**长度前缀流**：`[u32 len][wav][u32 len][wav]…`，`0` 表示该条失败（`TTSClient.cs:313-336`、`voxcpm_server/src/main.rs:111-124`）。
- **端口硬编码**：8765 Edge / 8766 Piper / 8767 VoxCPM（`TTSClient.cs:360,376,291`；`tts_server.py:15`；`tts_server_local.py:15`；`main.rs:35`）。Provider 3 "自定义 URL" 默认也指向 8765。
- **服务进程生命周期**：mod 构造时先 `TcpClient` 探 8765，连上就不启动；否则 `Process.Start(Tools\tts_server.exe, 当前游戏 PID)`（`MM:26-42`）。Python 侧有线程每 3 s 用 `OpenProcess+GetExitCodeProcess` 查该 PID，游戏退出即自杀（`tts_server.py:19-47`）。`StopTTSServer()` 定义了但**从未被调用**（死代码，`MM:44-47`）。
- **音频回到游戏的方式**：**不走 SoundDef**。C# 里存一份 `defName → "Voices/xxx"` 路径表（`RVH:1068-1195`），用 `ContentFinder<AudioClip>.Get(path, true)` 直接拿 clip（`:742-747`），然后每个片段 `new GameObject("VoiceDeputySlot")` + `AddComponent<AudioSource>()` + `PlayScheduled(_cursor)`，`Destroy(go, clip.length+0.5)`（`:1001-1012`）。**121 个 SoundDef XML 只是元数据镜像，运行时从未被查询或 PlayOneShot**（全仓无 `SoundDef`、`PlayOneShot` 引用；**实测** grep）。
  → 后果：完全绕过原版混音器，`<context>MapOnly</context>` 无意义，玩家主音量/音效音量滑块对它无效。
- **游标排队**：全局 `_cursor`（dspTime 空间）串起所有片段，`_cursor += clip.length + 0.05`（`RVH:1006-1009`）；上一串播完（`dspTime >= _activeFinishDspTime`）才出队下一串（`:943-1031`）。这是"零间隙连播"的实现核心，也是全 mod 最值得保留的一段代码。

### 3.3 缓存

- **内存**：`Dictionary<string, AudioClip> _cache`，键=**待播原文**（`TTS:40`）。
- **磁盘**：`<mod>\Cache\TTS\<Uri.EscapeDataString(文本)>.wav` —— **文件名就是文本的百分号编码，不是 hash**（`TTS:75-78,104-119`）。实测 275 个文件 100% 是百分号编码，含 2 个英文名（`Marrow%20Frenzy%20Band.wav`、`Spit%20Rabble.wav`），最长文件名 121 字符（无 MAX_PATH 风险）。WAV 全为 24 kHz / 单声道 / 16-bit（Edge 服务端产物，`tts_server.py:103-107`）。
- **"同文本零延迟复用"**：命中内存就直接塞进 slot（`LookupCache`，`RVH:803-806`）；未命中才发 HTTP（`RequestBatchFor`，`:792-837`）；HTTP 回来的 clip 写内存+写盘（`TTS:230-234`）。
- **重启恢复**：`InitCache` → `LoadCacheFromDisk()` 在 **Mod 构造函数**里同步读全部 `.wav` + `AudioClip.Create`（`MM:19-24`、`TTS:80-102`）。实测 275 文件 / 19.36 MB → 每次启动都是一次主线程批量解码。
- **容量控制**：`MAX_CACHE_FILES=5000`，只在 Init 时按 LastWriteTime 删最旧 20%（`TTS:54-73`）。内存无淘汰。
- **预热（pre-warm）**：唯一触发点是 `ProcessIdleCache`，在主线程每帧被调用，条件"无活动序列且无在飞批量"，节流 180 tick=3 s 一条，token 列表**只收集一次**（`RVH:844-912`）。实测佐证：缓存文件时间戳大量成簇、间隔 2–3 s（如 2026-07-04 09:03:03→09:03:23 连续 20 个派系名），与 3 s 节流吻合；2026-07-26 16:58 那段 1 s 内连落 3 个则是事件批量请求。
- **实测使用证据**：缓存时间跨度 2026-06-24 → 2026-08-02，键里既有派系名（万代兰、佛罗伦萨联盟…）也有殖民者/动物名（`小畜生-25`、`小畜生.65`、`鼠条.18`、`保姆鼠条一号`）——证明它确实在实战里跑过。

### 3.4 队列 / 优先级 / 打断

- **有队列，无优先级、无打断**：`Queue<SlotList> _slotQueue` FIFO（`RVH:732,777`）；新事件只会在"空闲"时立即播，否则排队（`:753-778`）；队列无上限 → 大规模连续事件会积压成几分钟延迟（**推测**，机制如此）。
- **唯一的"抢占"是间接的**：空闲预热仅在 `_activeSlots==null && !_batchInFlight` 时干活（`:855`），所以事件会挤掉预热；事件请求还会 `TTSClient.ResetCooldown()` 清掉失败冷却（`:815`、`TTS:23-26`）。
- **唯一全局闸门**：`Settings.voiceEnabled`（`:758`）。**没有**"只在袭击时开""音量随威胁等级"之类的策略。
- **卡死兜底**：单 slot 等 clip 超 5 s 直接跳过（`:984-990`）；批量请求卡 10 s 允许重发（`:795-797`）。

---

## 4. 三个 TTS 实现的差别

| | Edge（默认，provider 0） | Piper 本地（provider 1） | VoxCPM（provider 2） |
|---|---|---|---|
| 服务 | `tts_server.py`，端口 8765 | `tts_server_local.py`，端口 8766 | `voxcpm_server/src/main.rs`（Rust），端口 8767 |
| 依赖 | `edge_tts` + `miniaudio` + `numpy`（`tts_server.py:13`），需联网 | `piper` CLI + `models/zh_CN-huayan-medium.onnx`(60.3 MB)+tokens+cfg(22050 Hz, espeak voice `cmn`) | `voxcpm-rs 0.5` + `burn 0.20 (wgpu)`，需自备 `VoxCPM2/05B/15B` 权重目录 |
| 启动方式 | PyInstaller 打包成 `Tools/tts_server.exe`（22.9 MB，与 `dist/tts_server.exe` SHA256 完全一致），mod 自动拉起并传游戏 PID | 手动 `python tts_server_local.py`（mod 不会拉起，也不健康检查它） | 手动 `cargo run`；**目录里没有 `target/`，也没有任何模型权重** |
| 延迟相关代码 | 服务侧 mp3→f32→归一化→WAV，一次一请求；客户端 30 s 超时、失败冷却 10 s、HealthCheck 2 s/1 s | 每条都要 `subprocess.run` 起一次 piper 进程（`tts_server_local.py:26-32`），30 s timeout | 模型加载后先跑一次 `generate("hi")` 做 GPU warmup 再 freed（`main.rs:28-33`）；批量串行 + 每条间 `sleep 80 ms`（`:77-78`） |
| 实测能不能跑 | **能**（缓存里 275 个 24 kHz WAV 全是这条路产出的）；但跨机器/断网不可用 | **基本不能**：`piper_speak()` 命令行写了 `--output-raw nul` 把原始 PCM 丢给 Windows 的 NUL 设备，却从 `p.stdout` 读音频（`tts_server_local.py:27-35`）→ 正常情况拿回空字节（**实测代码即可判定**，未实跑）。作者自己在 UI 里写"完全不推荐/可用性未知"（`MM:86-88`） | **没跑通**：UI 原话"作者呕心沥血花了一整天没搞出来，留这个页面纯属不甘心"（`MM:93-94`）；仓库无模型、无编译产物（实测） |
| 批量 | 不支持，逐条 HTTP（`TTS:270-279`） | 不支持 | 支持，长度前缀流（`TTS:281-336`、`main.rs:59-66`） |
| 失败降级 | — | 失败 → 回退 Edge（`TTS:222-228`） | 失败 → 回退 Edge |

补充：provider 3 = 自定义 URL，也可发 `{"text","rate"}` 到任意端点（`TTS:364-369`）。

---

## 5. 设置项清单（`VoiceDeputySettings` 每个字段 = 用户能调什么）

| 字段 | 默认 | UI 是否可改 | 说明 / 实际作用 |
|---|---|---|---|
| `voiceEnabled` | true | ✅ 总开关 | **唯一**真正生效的闸门（`RVH:758`） |
| `ttsEnabled` | true | ✅（Edge/Piper 页各一个同名复选框） | **未接线**：全代码没有任何地方读它 → 勾掉也照样发 TTS 请求（`SET:9`；grep 仅 3 处引用全是自身） |
| `ttsProvider` | 0 | ✅ 循环按钮（4 档） | 0 Edge / 1 Piper / 2 VoxCPM / 3 自定义（`MM:15-17,64-65`） |
| `ttsVoice` | `zh-CN-XiaoxiaoNeural` | ✅ 文本框 | 只给 Edge 用（`TTS:173-176`） |
| `ttsRate` | 30 (%) | ✅ 滑块 −50…50 | Edge/Piper 用；拼成 `"+30%"`（`TTS:178-181,356`） |
| `ttsCustomUrl` | `http://127.0.0.1:8765/tts` | ✅ 文本框 | provider 3 |
| `voxTimesteps` / `voxCfg` | 8 / 2.0 | ❌ 无 UI | 只在 provider 2 的 JSON 里用到（`TTS:214-217`） |
| `volumePreRecorded` | 1.0 | ✅ 0…2.0 | 预录片段音量（`RVH:997-999`） |
| `volumeTTS` | 1.0 | ✅ 0…2.0 | TTS 片段音量 |
| `alertServerDown` | true | ✅ | 断联 100 s 播提醒（`RVH:1287-1294`） |

另有按钮：启动 TTS 服务器、清除 TTS 缓存（**只清内存不清磁盘**，`TTS:162-166` vs `SaveToDisk:111` 的 `File.Exists` 早退 → 重启又回来）。

---

## 6. 播报文本的构造方式

- **"多维度拼接"就是 5 个固定槽位的字典查表**，顺序写死在 `BuildPlaylist`（`RVH:453-495`）：`警示(2 选 1) → 派系(19 键) → 策略(13 键；ImmediateAttackFriendly 被故意映射成 null＝静音) → 抵达(11 键) → 规模(3 档整数阈值)`。每维查不到就静默跳过（**没有**兜底 WAV），规模永远有输出。
- **预录 + TTS 的拼法**：一个 `SlotList` 里 `Slot{isTTS,defName|ttsText,clip}`；`AnnounceWithTTS(预录, ttsToken…)` = 先 1 个预录槽再若干 TTS 槽，**顺序即播放顺序**（`RVH:1055-1065`）。
- **中文模板都在 C# 里硬编码**（**无** `Languages/` 目录，实测）：疾病名 `MapDiseaseName` 的 `"，流感"` 等 10 条（`RVH:512-528`，TrimStart('，') 后单独当 TTS token 用）、商人类型 15 条（`:44-62`，另有 `AnnounceTraderCaravan` 里重复一份 switch，`:684-721`，该死代码从未被调用）、人名连接规则 `1人/2人用"、"分隔/≥3人 取首名+"等N人"`（`JoinPawnNames`，`:533-546`）、商人句 `派系名 + "，" + 商人类型`（`:713-714`）。

---

## 7. 资源清单

**预录 121 个 `.ogg`（1.34 MB，`Sounds/Voices/`，全部 2026-06-17 00:15–00:18 一次生成）**

| 类别 | 数量 | 命名规律 | 例子 |
|---|---|---|---|
| 警示/前缀 | 4 | `vo_alert_NN` + `vo_alert_server_down` | `vo_alert_01..03`（detected/attention/alarm） |
| 派系 | 15 | `vo_faction_NN` | `vo_faction_04`=机械族，`vo_faction_15`=敌对古人 |
| 袭击策略 | 7 | `vo_strategy_NN` | `vo_strategy_06`=围攻 |
| 抵达方式 | 6 | `vo_arrival_NN` | `vo_arrival_04`=中心空投 |
| 规模 | 3 | `vo_scale_NN` | 小/中/大 |
| 事件 | 68 | `vo_event_<英文短名>` | `vo_event_mental_break`、`vo_event_colonist_down`、`vo_event_ship_defo` |
| 鼠族系（外 mod） | 18 | `vo_rk_{faction,strategy,arrival,event}_*` | `vo_rk_event_tunnel` |

对应 `SoundDef`：`EV` 68 + `SFX` 53 = **121**，与 C# `ClipPaths` 121 条、磁盘 121 个 ogg **三方完全一一对应，无孤立项**（实测交叉比对）。但其中 5 个运行时不可达（见 §2）。
注意文档漂移：`About.xml:17` 写"103 条"，`EV:3` 注释写"60 event voice clips"，实际分别是 121 / 68。

**TTS 缓存 275 个 `.wav`（19.36 MB，`Cache/TTS/`）**：命名 = `Uri.EscapeDataString(原文) + ".wav"`，**不是 hash**（`TTS.cs:75-78`；`HashText()`/MD5 是死代码，`:121-126`）。实测无字面中文文件名（100% 编码）、最长 121 字符、全部 24 kHz/mono/16-bit。内容分布：派系名（多数）、殖民者与动物名（`小畜生-25`…`小畜生.98`、`鼠条.18`、`保姆鼠条一号` 这类带序号的名字说明键来自 `Name.ToStringShort`）、少量英文 mod 派系名。

---

## 8. 已知缺陷 / 技术债 / 明显 bug（只列代码里看得到的）

**线程安全（最严重）**
1. `_cache` 是普通 `Dictionary`，主线程读（`RVH:803,867`）、后台线程写（`TTS:232,333`）→ 无锁，理论可损坏字典/读到半初始化 clip。
2. `ParseWav` 在**后台线程**调 `AudioClip.Create` + `SetData`（`TTS:459-461`），Unity 对象创建不在主线程，属未定义行为。
3. 后台线程回调里直接改播放状态：`ThreadPool` 里 `FlushCluster()` → `SpeakTTS` → `EnqueueSlots`，触碰 `_slotQueue/_cursor/AudioSettings.dspTime` 并打 Log（`RVH:339-343`）。
4. `lock(_slotQueue)` 只出现在批量回调（`:820`），而 `EnqueueSlots/AdvanceSlots` 完全不加锁 → 这把锁实际上没保护任何东西。
5. `HashSet` 被主线程增、ThreadPool 线程删：`PawnsInLabor`（`:251-255`）、`_recentPawns`、`RecentMentalBreakPawns`（`:297-302`）→ 非线程安全集合的并发读写。
6. `_pendingAlert/_serverDown/_consecutiveFailures` 后台写、主线程读，无 `volatile`（`:1238-1294`）。

**主线程 / 卡顿**
7. Mod 构造函数里同步加载 275 个 WAV 并建 AudioClip（`MM:22` → `TTS:80-102`）→ 启动卡顿，且随缓存增长线性恶化。
8. 每个片段 `new GameObject` + `AddComponent<AudioSource>`，靠 `Destroy(go, delay)` 回收（`:1001-1011`）→ 每句话 5 个音频源就 5 个 GO/帧的分配。
9. 设置窗口 `if (!voiceEnabled) { … Settings.Write(); return; }`（`MM:59`）→ 总开关关掉后**每帧写一次盘**。

**逻辑 bug**
10. 疾病双播：`TryExecute` 已播预录"流感爆发"，`IncidentWorker_Disease.TryExecuteWorker` 的 postfix 又 TTS 一遍"张三流感"（`RVH:103-160` + `:582`），且名字在前、病名在后。
11. `Settings.ttsEnabled` 无任何读取点 → UI 上的"启用 TTS"是假开关（§5）。
12. "清除 TTS 缓存"只清内存，磁盘留存并在下次启动全部回灌（`TTS:162-166`、`:111`）。
13. Piper 服务写 `--output-raw nul` 却读 stdout → 永远空音频（`tts_server_local.py:27-35`）。
14. 疾病感染者识别是纯启发式：靠 `hediffClass.Name.Contains("HediffWithComps")` + `tendable` + `!isBad` 猜（`RVH:130-149`），注释自己都写 "Simpler check"；会漏（如已免疫/刚感染）也会误（`isBad==false` 的可治疗 hediff）。
15. 死亡播报用 `Pawn.Kill` Prefix，且不判 `def.isWithinCategory`，被"复活机械液"救回也已播过（**推测**）；同时凡 `IsColonist` 都播，包括被处决/处刑（**推测**，未区分）。
16. `Patch_MentalBreak` 与 `Patch_ColonistDowned` 用 3 s/30 s 的定时清标记互斥（`:176-177,301-302,251-255`）→ 30 s 内二次倒地会被误判为"分娩/崩溃"而漏播。
17. 未映射事件的兜底是 `IncidentDef.label` 直接送 TTS → 英文环境下会念英文（`RVH:664-676`），对中文语音模型是噪声。

**健壮性 / 降级**
18. 9 处 `catch { }` 空吞（`TTS:38,72,98,118`；`RVH:91,325,877,932`；`MM:29`）→ 缓存写失败、反射失败、HTTP 失败全部无声（`MM:41` 至少还打了 Warning）。
19. 服务器不在时：`ResetCooldown()` 每次事件都重置失败冷却（`:815`），会反复发 30 s 超时的请求；slot 5 s 跳过保证不卡队列，但后台线程仍被占（**推测**，机制如此）。
20. 端口冲突即误连：只 `TcpClient.Connect(8765)` 判活（`MM:28`），任何占着 8765 的进程都算"服务已就绪"。
21. 硬编码路径：csproj 里 5 条 `D:\SteamLibrary\...` 绝对 HintPath，其中 **`0Harmony.dll` 的 workshop 路径在本机不存在**（`D:\SteamLibrary\steamapps\workshop\content\261550` 不存在，实测；`Mods\` 下也搜不到 `0Harmony.dll`）→ 当前机器**无法按原样重建**（**实测**）。另 `Assembly-CSharp.dll` 路径存在。
22. `Tools/tts_server.exe` 22.9 MB 二进制入库、`build/`+`dist/`+`models/` 共 111 MB 一起打包进 mod 目录 → 创意工坊体积/审查成本；作者在 `About.xml:28` 明确说"用这种手段规避病毒审查"。
23. `StopTTSServer()` 从不调用（`MM:44`）；`SendHeartbeat()`（`RVH:916-941`）从不调用（`GameComponent` 自己另写了一套健康检查）；`AnnounceTraderCaravan()`（`:684`）从不调用；`HashText()` 从不调用 → 4 处死代码 + 同名逻辑两份。
24. **仓库与源码不同步**：`Assemblies/VoiceDeputy.dll` 时间戳 2026-06-09 06:43，而 `RVH` 最后修改 2026-06-17 00:07；实测 DLL 内含 `SlotList/ProcessIdleCache/"启用语音播报"/"TTS batch: ["`，**但不含**源码 `RVH:1020` 的 `"SLOTS SCHEDULED"` → 发布用的 DLL 至少落后一处（**实测**）。新 mod 必须重建，不要试图复活这个 DLL。
25. 无 README / 无 todolist / 无 LICENSE / 无 `.git` / 无 `Languages/`（实测，全目录无 `.md`、无版本文档）。

---

## 9. 可复用的设计理念（**以下全是我的判断，标注"推测"**）

**值得带走**
1. **"5 维拼接 + 槽位序列"**（推测：好）——把一句话拆成可替换的原子片段，是这套东西最核心的价值：语料只要 121 条就能覆盖 60+ 事件 × 19 派系 × 13 策略。新 mod 应升级为"**数据驱动的片段模板**"（json/xml 定义槽位顺序与条件），而不是 C# 里 5 个字典 + 一个写死顺序的函数。
2. **预录优先、TTS 只补动态字段**（推测：好）——延迟的主因是 TTS；固定话术全部预录，实测缓存里 275 条中绝大多数是名字而不是句子，说明这个分工是被真实使用验证过的。
3. **文本即键的磁盘缓存**（推测：值得保留，但要换实现）——"同文本零延迟复用"体验正确；用 `SHA1(文本)` 做文件名可以规避中文/长名/路径非法字符，同时保留可读的 `index.json` 映射。
4. **dspTime 游标无缝排队**（推测：好，必须保留）——`PlayScheduled(_cursor)` 是"副官连播不打架"的正解；重写时只替换成"一个有上限、带优先级、可打断"的调度器。
5. **空闲预热派系名/人名**（推测：好，但触发点太弱）——一次性收集改成"事件驱动增量预热"（新派系出现/新殖民者加入/新商人类型首次出现时入队）。
6. **单点总闸 + 断联语音提醒**（推测：好）——`voiceEnabled` 与"服务器挂了要让人知道"是产品意识，比代码本身珍贵。

**建议扔掉**
7. 121 个 `SoundDef` XML 与 C# `ClipPaths` 双份数据（实测两者完全重复）——保留 XML 作为唯一真值，用 `SoundDef`/`PlayOneShot` 走原版混音器，玩家音量才生效。
8. `Traverse` 反射读私有字段（8 处）——1.6 大多数目标字段是 public 或已有公开 API；反射是"当年不敢查源码"的遗留。
9. 每片段一个 GameObject/AudioSource——用一个常驻池（或原版 `AudioSourcePool`）。
10. Piper / VoxCPM 两套失败路线（Piper 有硬 bug，VoxCPM 从未跑通）——新 mod 只留"一个本地实现 + 一个远端实现"，接口一致即可。
11. 20 条 `Log.Message` 逐句打印（实测 20 Message + 13 Warning）——改成可开关的调试级别。
12. `tts_server.exe` 二进制入库。

---

## 10. 1.6 兼容性风险点

**实测（对 1.6.4871 逐项核对）**
- 游戏 1.6.4871 rev590 / Unity 2022.3.35f1；`Managed` 下 `mscorlib.dll` 存在 → csproj 的 `.NET Framework 4.7.2` 目标仍可用；`LangVersion 7.3` 无碍。
- 7 个 patch 目标的方法名在 `Assembly-CSharp.dll` 里全部存在（§3.1）。
- defName 对齐（把 mod 的映射表 key 与 `Data\**\Defs` 里的 def 集合比对）：事件 **60/74** 命中原版 1.6 `IncidentDef`，其余 14 个是鼠族系 mod；派系 **15/19**、策略 **11/13**、抵达方式 **10/11**（唯一缺的是 `OA_RK_CenterDrop`）→ **原版部分 0 处 defName 漂移**，说明作者确实按 1.6 写过。
- 覆盖缺口：1.6 存在但未映射的抵达方式 `EdgeWalkInHateChanters`、`MechClusterDrop`（查不到就静默少一句，不会崩）。
- `DevelopmentalStage.Baby`（`RVH:293`）、`pawnsArrivalModeDef`/`raidStrategy`/`parms.points` 都是 1.5/1.6 及更早的稳定 API。
- `About.xml` 声明 `1.5` + `1.6` 双版本、无 `loadFolders.xml`（单程序集，共用可行）。

**风险 / 老写法**
- `Traverse.Create(x).Field(...).GetValue<T>()` 属 Harmony 2 早期 API，2.3 起被标 obsolete（Windows 下仍能跑，只是警告）→ 新 mod 用 `AccessTools`/公开字段。
- `Traverse.Create(ms).Property("MentalState")`、`Field("def")`：1.6 里 `Pawn.MentalState` 是属性、`MentalState.def` 是 public 字段，反射纯属多余，字段改名就静默失灵（`RVH:310-325` 用 `try{}catch{}` 包着 → **失败了也不会报**）。
- **重载歧义未确证**：`Pawn.Kill`、`IncidentWorker.TryExecute`、`Hediff_Pregnant.StartLabor` 若在 1.6 有多重载，Harmony 按名 patch 会抛 `AmbiguousMatchException`（**未确证**；若是，则死亡播报整条链路静默失效）。
- `Pawn.Notify_Downed` 是否为 1.6 唯一倒地通知点未确证；若 1.6 改走 `Pawn_HealthTracker`，则会漏播（**未确证**）。
- `HediffDef.tendable` / `makesSickThought` / `Hediff.Visible` 存在（元数据字符串命中），但语义在 1.6 是否仍等同于"传染病"未确证。
- 中文全硬编码、无 `Languages/`：1.6 无影响，但"想做多语言"等于重做文案层。

---

## 11. 待办问题（我判断"必须写脚本/跑游戏才能回答"，故未做）

| # | 问题 | 寿命判断 |
|---|---|---|
| 1 | `Pawn.Kill` / `IncidentWorker.TryExecute` / `Hediff_Pregnant.StartLabor` 在 1.6 各有几个重载？按名 patch 会不会 `AmbiguousMatchException`？ | **长期**：应做成"符号台账"能力（一次性列出候选方法的签名），新 mod 每个 patch 点都要查 |
| 2 | 121 条预录的实际时长分布（决定"一句话要排多久"与打断阈值） | 一次性：跑一次 ffprobe 出表即可，但结论要进新 mod 设计文档 |
| 3 | Edge 服务在**当前**机器/网络下是否仍可用（`edge_tts` 版本、微软端点是否变更）；旧 exe 是 2026-06 打的包 | 长期：新 mod 必然要重新验证一次，且要测断网降级 |
| 4 | 真实游戏内端到端延迟（前置片段长度 + TTS 请求 + 首帧播放）在 1.6 下的实测值 | 长期：这类 mod 的核心指标，建议进 `runs/` 的固定验收项 |
| 5 | 1.6 里"殖民者倒地/死亡/精神崩溃/分娩"是否有比 `Notify_Downed`、`Pawn.Kill` 更稳的官方钩子（`Pawn_HealthTracker`、`TaleRecorder`、`Messages`） | 长期：决定新 mod 的 patch 台账要不要换骨架 |
| 6 | 缓存里几个带 `.68`、`.65` 后缀的名字（`小畜生.68`）来源：是 pawn 名本身含点，还是旧版本文件名规则残留 | 一次性：对重写无影响，只需知道"键必须是完整原文" |
