# 提案：游戏侧事件通道（DSH_GameLink + live watcher）

> 2026-10-17。起因：用户要求"写一个 mod 完全监控游戏进程、和 DSH 通信，别再用强制延时+读日志的笨办法"。
> 先查过：游戏 `Mods\DSH_AutoTest` 只是热重载用的小 Defs 测试 mod（只有 About + 一个 ThingDef，**没有 Assemblies**）；`plugin\dsh-rimworld` 是 DSH 侧的 7 个桥接工具（未在当前会话启用）。git 历史里没有被删掉的监控 mod。**所以要新写。**

## 1. 目标（一句话）

游戏里发生的事 → 立刻变成一条结构化事件 → DSH 这边"等一个条件"就返回，**不睡固定时长、不轮询日志**。

## 2. 两层

| 层 | 东西 | 干什么 |
|---|---|---|
| 游戏侧 | **新 mod `DSH_GameLink`**（独立 mod，packageId `dsh.gamelink`） | 一个 `GameComponent` 把状态变化写成 NDJSON 事件；一个 `Log.Message` 的 Harmony 后置补丁把 `[VD]`/红字转发成事件 |
| DSH 侧 | `src/live.js` + `node src/cli.js live ...` | 跟读 NDJSON（或 `Player.log`），命中条件就退出并打印；作为后台 job 跑 ⇒ **job 完成就是推送通知** |

## 3. 事件形状（NDJSON，一行一条）

`runs/live/<profile>.ndjson`（路径由环境变量 `DSH_LIVE_DIR` 给，默认 `<workspace>/runs/live`）：

```json
{"ts":1760000000.123,"seq":41,"kind":"game_loaded","tick":345,"map":"Map_0"}
{"ts":...,"seq":42,"kind":"colony_started","tick":900,"colonists":3}
{"ts":...,"seq":43,"kind":"window","opened":true,"type":"VoiceDeputy.UI.Dialog_VoiceSettings","forcePause":true}
{"ts":...,"seq":44,"kind":"log","level":"Message","text":"[VD] 1 incident ThreatRaid"}
{"ts":...,"seq":45,"kind":"heartbeat","tick":1200,"programState":"Playing","speed":1}
```

- 事件种类：`game_loaded` / `colony_started` / `window` / `log` / `heartbeat` / `error`。
- 每 60 tick 一条 `heartbeat`（默认；可关），用来判断"游戏卡住/没在跑"。
- 写盘失败要能自愈（丢弃本行、不抛），游戏绝不因这条通道崩。

## 4. DSH 侧命令（长期能力，进 `src/`，不是一次性脚本）

```powershell
# 等到「袭击事件被钩子收到」为止（最多 120 s），命中就打印并退出
node src/cli.js live --profile voiced --until 'kind=log text~\[VD\] 1 incident' --timeout 120000
# 等到殖民地建好
node src/cli.js live --profile voiced --until 'kind=colony_started'
# 看最近 30 条事件
node src/cli.js live --profile voiced --last 30
```

- `--until` 命中即退出码 0，超时退出码 2（区分"没等到"与"出错"）。
- 用法：把它挂成后台 job，**job 完成的通知就是我要的推送**；不用再 `Start-Sleep`。

## 5. 边界

- 不改产品 mod（`语音副官`）一行代码；`[VD]` 事件只读日志文本。
- 不替代桥接：造事件/截图仍走 `node src/cli.js call ...`。
- 第二个 mod 只在隔离 profile 里加载，不影响用户正常游戏。

---

# 实现现状（2026-10-17）

> 标注：**实测** = 本机跑过命令看到结果；**未验证** = 没进游戏，属推断。
> **本次没有启动/重启游戏**（有一个实例在跑验收），也没有部署（junction / ModsConfig 均未动）。

## A. 实际文件

| 文件 | 干什么 |
| --- | --- |
| `mods/DSH_GameLink/About/About.xml` | `packageId=dsh.gamelink`、`name=DSH GameLink`；依赖并 `loadAfter` `brrainz.harmony` |
| `mods/DSH_GameLink/LoadFolders.xml` | `v1.6 → /`（**Assemblies 在 mod 根**，不是 `1.6/Assemblies`） |
| `mods/DSH_GameLink/Source/DSH_GameLink.csproj` | `NoStdLib` + 直接引用游戏 `Managed\`；`HarmonyDir` 借工坊 0Harmony；输出 `..\Assemblies\` |
| `mods/DSH_GameLink/Source/LiveChannel.cs` | 事件文件的**唯一写入口**：路径解析、建目录、追加写、每条 Flush、8 MB 轮转、失败熔断 |
| `mods/DSH_GameLink/Source/EventLine.cs` | 事件字段拼装（`Num/Text/Flag`）+ JSON 转义；`ts/seq` 在写入门里原子分配 |
| `mods/DSH_GameLink/Source/GameLinkComponent.cs` | `public sealed GameLinkComponent : GameComponent`，**不注册**，靠 `ctor(Game)` 被 `Game.FillComponents()` 收养 |
| `mods/DSH_GameLink/Source/LogTap.cs` | `[StaticConstructorOnStartup]` + Harmony **显式** Patch `Verse.Log.Message/Warning/Error(string)` 后缀 |
| `mods/DSH_GameLink/Assemblies/DSH_GameLink.dll` | 编译产物（`-t:Rebuild`：**0 错误 0 警告**） |
| `src/live.js` | 跟读/等待/尾读；谓词解析；三种退出码 |
| `src/cli.js` | 新增 `live` 子命令 + help 一行（其余命令未动） |
| `src/launcher.js` | `startGame()` 的 env 里加一行 `DSH_LIVE_DIR=<工作区>\runs\live`（其余未动） |

## B. 事件字段最终形态

每行都是 `{"ts":<unix 秒,3 位小数>,"seq":<进程内自增>,"kind":"<种类>",...}`：

| kind | 额外字段 |
| --- | --- |
| `game_loaded` | `tick`:int（`OnLoaded`/`StartedNewGame` 后第一次 Update 写）、`map`:int（`Map.uniqueID`，无图 = -1） |
| `colony_started` | `tick`:int、`colonists`:int（`AnyPlayerHomeMap.mapPawns.FreeColonistsCount`）、`map`:int（该地图 uniqueID） |
| `window` | `opened`:bool、`type`:string（顶层窗口**类型全名**，关闭时 = `""`）、`forcePause`:bool |
| `log` | `level`:`"Message"`/`"Warning"`/`"Error"`、`text`:string |
| `heartbeat` | `tick`:int、`programState`:string（`Entry`/`MapInitializing`/`Playing`）、`speed`:int（`TickManager.TickRateMultiplier` 四舍五入，暂停=0）、`map`:int |
| `error` | `where`:string（`game_loaded`/`colony_started`/`window`/`heartbeat`）、`message`:string（`异常类型: 消息`） |

细节（实现时的取舍）：

- `window` 的「顶层」= `Find.WindowStack` 的**最后一个**窗口（最后绘制）；只在**类型名变化**时写一条，所以一帧的代价是「取 Count + 取一个元素 + 一次字符串比较」，且用 `Count`/索引器（`Windows` 属性每次调用都会 `AsReadOnly()` **分配一个对象**，故意不用）。
- `log`：`Message` 只转发含 `[VD]` 的行；`Warning`/`Error` **全转发**（抓红字）。三条都跳过以 `[GameLink] ` 开头的文本 —— 否则「写盘失败 → Log.Error → 转发 → 再写盘失败」会自激。
- `heartbeat` 可用 `GameLinkComponent.HeartbeatEnabled = false` 关掉，间隔是常量 `HeartbeatIntervalTicks = 60`。
- 落点：`$DSH_LIVE_DIR\<profile>.ndjson`（profile = `GABS_GAME_ID` 去掉 `dsh-rimworld-`，拿不到 ⇒ `game`）；无该变量 ⇒ `<存档目录>\dsh-live\game.ndjson`。目录自建。
- 写盘失败 ⇒ 吞掉 + `Log.Error` 一次，之后**永久停写**；超过 8 MB ⇒ 当前文件改名 `<file>.1`（覆盖旧代）后继续写新文件。

## C. 命令用法（DSH 侧）

```powershell
# 等「殖民地建好」（命中打印那一行原文并退出 0；超时退出 2）
node src/cli.js live --profile voiced --until 'kind=colony_started' --timeout 120000
# 等 [VD] 的钩子日志（多条件用空格或 && 连接；值里可以有空格）
node src/cli.js live --profile voiced --until 'kind=log text~[VD] 1 incident'
# 只看最近 30 条（立即退出 0，不等待）
node src/cli.js live --profile voiced --last 30
# mod 还没部署时：直接跟 Player.log（每行 = {kind:"log", text:...}）
node src/cli.js live --profile voiced --source log --until 'text~Bridge token'
```

- 谓词：`key=value`（相等）/ `key~value`（子串），键取事件 JSON 顶层字段；数字与布尔按 JSON 字面量转字符串比（`tick=900`、`speed=1`、`opened=true`）。
- 默认从文件**末尾**开始（只看新内容）；`--from-start` 从头读；`--file <路径>` 直接指定文件（与 `--source log` 合用就是跟任意日志文件）。
- 退出码：**0** 命中 / **2** 超时（打印文件最后 10 条）/ **1** 参数错（含「`--last` 与 `--until` 同时给」）。
- 事件文件查找顺序：`--file` → `$DSH_LIVE_DIR/<profile>.ndjson` → `<profiles>/<profile>/dsh-live/game.ndjson`（存在才用）→ `runs/live/<profile>.ndjson`。

## D. 已实测 / 未验证

**实测（本机命令，临时文件放 `tmp/` 后已删）**：

1. `dotnet build ... -t:Rebuild` ⇒ 0 错误 0 警告，产物 `mods/DSH_GameLink/Assemblies/DSH_GameLink.dll`。
2. `live` 命中：`kind=colony_started`、`kind=log text~[VD] 1 incident`、`colonists=3 && kind=colony_started` 三种都打印原文并 **exit 0**。
3. `live` 超时：`--until kind=never_happens --timeout 900` ⇒ 打印最后 4 条并 **exit 2**。
4. `live --last 2` ⇒ 打印最后 2 条并 **exit 0**。
5. `live` 参数错（缺 `--until`/`--last`、`--until nonsense`、`--last abc`）⇒ 三条都 **exit 1**。
6. 增量跟读：先起 watcher（不 `--from-start`），0.9 s 后追加 heartbeat、0.5 s 后追加 `colony_started` ⇒ watcher 打印该行并 **exit 0**，会计数「读到 2 条新事件」（起始行按设计被跳过）。
7. `--source log`：跟一个普通文本文件，`--until 'kind=log text~[VD] 2 ready'` ⇒ 命中 **exit 0**。

**未验证（没进游戏，属推断）**：

- `GameLinkComponent` 是否真的被 `Game.FillComponents()` 收养、`GameComponentUpdate/Tick` 是否按预期被调 —— 要进游戏后看有 `heartbeat`/`game_loaded` 事件才算数。
- `window` 事件的「顶层 = 栈尾」、`forcePause` 取值、以及窗口类型全名是否与 DSH 侧期望一致。
- Harmony 三条后缀是否挂上（`LogTap` 挂失败会写 `[GameLink]` 红字，进游戏后 grep 即可）。
- 8 MB 轮转、写盘失败熔断、`DSH_LIVE_DIR` 路径解析：只做了代码层保证，**没有**在游戏里触发过。
- mod 根 `Assemblies/` + `LoadFolders v1.6 → /` 能否被 1.6 正常加载（仓库里其它 mod 用的是 `1.6/Assemblies`，本 mod 按交付要求落在 mod 根）。
- 该组件会被写进存档：**存档里留下 `Class="DSH_GameLink.GameLinkComponent"` 节点**，卸载本 mod 后再读该档会报「找不到类」。只应在隔离 profile 里用。

## E. 部署（由上层做，本次未做）

1. 把 `mods\DSH_GameLink` junction 到 `D:\SteamLibrary\steamapps\common\RimWorld\Mods\DSH_GameLink`。
2. 在目标 profile 的 `profiles\<profile>\Config\ModsConfig.xml` 的 `activeMods` 里加上 `dsh.gamelink`（放在 `brrainz.harmony` 之后），重启该实例。
