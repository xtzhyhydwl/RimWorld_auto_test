# 副官语音 / Voice Deputy

> **本目录是这条线的根**：mod 本体在根下，设计与取证文档在 `docs/`。
> 游戏侧 `Mods\副官语音` 是指向本目录的 junction。
> 当前阶段见 [STATUS.md](STATUS.md)。

## 现在到哪了

- 设计已定稿（6 份文档），代码只有骨架：`About` + Mod 入口 + 设置窗口 4 页签。
- 编译通过、**从未进过游戏**。按现状只算试写。
- 下一步：按 `docs/06-编码顺序与验收.md` 的第 3 步开工；进游戏前先把探针 P1–P10 排上。

## 设计文档（按读的顺序）

| # | 文档 | 管什么 |
|---|---|---|
| 01 | [架构与模块](docs/01-架构与模块.md) | 28 个模块的输入/输出契约、18 组跨模块类型、信息流图、线程边界 |
| 02 | [类别与来源](docs/02-类别与来源.md) | 22 类播报各自绑哪个通道、def 判定、丢弃项、优先级、模板键 |
| 03 | [文案与片段](docs/03-文案与片段.md) | 模板表、片段切分、聚合、整句缓存、预热 |
| 04 | [语音库与清理](docs/04-语音库与清理.md) | 缓存键、作用域、保留等级、两阶段清理、上限 |
| 05 | [设置界面](docs/05-设置界面.md) | 四个页签逐字段 + 默认值 + 落到哪个字段 |
| 06 | [编码顺序与验收](docs/06-编码顺序与验收.md) | 8 步 + 每步判据 + 探针 P1–P10 |

看图的入口：[模块与信息流（HTML）](docs/模块-输入输出-信息流.html)——双击即可，离线可看。

取证（事实来源，写代码时按需查）：`docs/取证-22类来源分布.md`、`取证-死亡播报出口.md`、`取证-GameComponent注册.md`、`取证-存档标识.md`、`取证-事件真值表.md`、`取证-提示信息出口.md`；背景：`docs/旧mod-副官语音-复盘.md`、`docs/调研-RimTalk接入模式.md`。

## 怎么做

- 代码：`Source/`（每个模块一个文件），设置界面在 `Source/UI/`。
- 产物：`Assemblies/VoiceDeputy.dll`（游戏默认扫描 mod 根下的 `Assemblies`）。
- 编译（实测通过，0 警告 0 错误）：

```powershell
$env:DOTNET_CLI_HOME="$PWD\tmp\dotnethome"
dotnet build '语音副官\Source\VoiceDeputy.csproj'
```

本机没有 net472 目标包也没网：csproj 用 `NoStdLib` + 直接引用游戏 `Managed\` 下的程序集；`Source/NuGet.config` 清空包源，断网也能 build。换机器传 `/p:RimWorldManagedDir=...`。

## 硬约束（踩过的）

- `packageId` = `xiayuan2004.VoiceDeputy`（沿用旧 mod 的 id）。
- Mod `GameComponent` **不注册**：非抽象子类 + `public ctor(Game game)` 即被 `Game.FillComponents()` 自动收养（见 `docs/取证-GameComponent注册.md`）。
- SiliconFlow 返回的 WAV 头长度字段是占位值，必须按实际字节数重算（见 `docs/取证-死亡播报出口.md` 同族取证与 `04`）。
- `Messages.Message` / `LetterStack.ReceiveLetter` 都有多个重载，Harmony patch 必须写全参数类型。
