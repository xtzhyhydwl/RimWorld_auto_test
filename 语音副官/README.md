# 副官语音 / Voice Deputy —— 骨架 + 设置界面

> **本目录是这条线的根**（2026-10-17 从 `mods/副官语音/` + `docs/` 迁来）：mod 本体在根下，设计与取证文档在 `docs/`。
> 游戏侧 `Mods\副官语音` 是指向本目录的 junction（已重建并验证）。

## 当前这一步
施工图步骤 1→2：能编译、能加载、设置窗口能开、开关能存盘读回。

## 做了什么
- `About/About.xml`：name「副官语音 / Voice Deputy」，packageId **`xiayuan2004.VoiceDeputy`**（沿用旧 mod 的 id），1.6，依赖 `brrainz.harmony`
- `Source/`：`SpokenCategory`（22 类）· `SpeechCatalog`（分组 + 中文名 + 一句话说明，**UI 标签**）·
  `Switchboard`（开关查询）· `VoiceDeputySettings`（全部字段 + `ExposeData`）·
  `VoiceDeputyMod`（设置页里的「打开副官语音设置」按钮）·
  `SpeechLibrary`（缓存占用的只读占位，恒返回 `0 条 / 0 B`，`FolderName = "Cache/Voice"`）
- `Source/UI/`：`Dialog_VoiceSettings`（左 4 页签 + 右内容 + 底部恢复默认/关闭）· 4 个 `Tab_*` ·
  `RowWidgets`（开关行公共绘制器 `ToggleRows.DrawRow` + 小字提示 `HintText`）
- 产物：`Assemblies/VoiceDeputy.dll`（游戏默认扫描 mod 根下的 `Assemblies`，不需要 `LoadFolders.xml`）

## 没做
TTS 客户端、缓存、清理器、钩子、播放器、预热；试听与实际发声（按钮是灰的）。

## 文档在哪（先读哪个）
| 要什么 | 读哪 |
|---|---|
| **审查结论（2026-10-17，先读这个）** | `docs/副官语音v2-设计审查-20261017.md` |
| 进度、决策台账、文档地图 | `docs/副官语音v2-进度.md` |
| 模块划分、编码顺序 | `docs/副官语音v2-施工图.md` |
| 拼接、语音库、清理器 | `docs/副官语音v2-拼接设计.md` |
| 界面每个字段 | `docs/副官语音v2-设置界面-规格草案.md` |
| 自查疑点（编号 D1–D10，与决策台账撞号，见审查 Q7） | `docs/副官语音v2-设计自查-疑点清单.md` |
| 取证：钩子出口 / 事件真值 / 存档标识 / RimTalk 接入 | `docs/取证-*.md`、`docs/调研-RimTalk接入模式.md` |
| 旧 mod 复盘 | `docs/旧mod-副官语音-复盘.md` |
| 审查底稿（三份原始审计） | `docs/审查底稿/` |

> ⚠️ 上述文档正文里的 `docs\…` 路径是**搬家前**的写法，现在这些文档自己就在 `docs/` 下（共 32 处，见审查 Q10）。

## 怎么编译（实测通过，0 警告 0 错误）
```powershell
$env:DOTNET_CLI_HOME="$PWD\tmp\dotnethome"   # dotnet 要写 HOME 下的哨兵，工作区里给它一个
dotnet build '语音副官\Source\VoiceDeputy.csproj'
```
本机没有 net472 目标包、也没网：csproj 用 `NoStdLib` + 直接引用游戏 `Managed\` 下的程序集；
`Source/NuGet.config` 清空包源，断网也能 build。换机器传 `/p:RimWorldManagedDir=...`。

## 已知缺口
- `SpeechLibrary.Usage` 恒为 `0 条 / 0 B`；缓存目录约定 `Cache/Voice`（单一目录，全存档共用）。
- 规格草案里有、本次没实现的：§3「附加参数（JSON）」、§4「预录音量」（后者按 D13 应直接删）。
- **未实机启动游戏**：设置页按钮、窗口排版、中文渲染、改开关后重启是否读回，都还没在游戏里点过。
  已验证的只有：编译通过、`About.xml` 可解析、DLL 的 AssemblyRef 只有 `Assembly-CSharp` / `mscorlib` / UnityEngine 三件。
- `Source/obj/` 是构建产物（已 gitignore）。
