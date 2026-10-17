# 副官语音 / Voice Deputy —— 骨架 + 设置界面

当前这一步（施工图步骤 1→2）：能编译、能加载、设置窗口能开、开关能存盘读回。

## 做了什么
- `About/About.xml`：name「副官语音 / Voice Deputy」，packageId `xiayuan2004.VoiceDeputy2`（**临时**），1.6，依赖 `brrainz.harmony`
- `Source/`：`SpokenCategory`（22 类）· `SpeechCatalog`（中文名 + 一句话说明 + 分组）· `Switchboard`（全 mod 唯一开关判断）·
  `VoiceDeputySettings`（全部字段 + `ExposeData`）· `VoiceDeputyMod`（设置页里的「打开副官语音设置」按钮）·
  `SpeechLibrary`（缓存占用的只读占位，恒返回 `0 条 / 0 B`，缓存模块接上后只改这里）
- `Source/UI/`：`Dialog_VoiceSettings`（左 4 页签 + 右内容 + 底部恢复默认/关闭）· 4 个 `Tab_*` · `RowWidgets`（开关行公共绘制器 `ToggleRows` + 小字提示 `HintText`）
- 产物：`Assemblies/VoiceDeputy.dll`（游戏默认扫描 mod 根下的 `Assemblies`，所以不需要 `LoadFolders.xml`）

## 没做
TTS 客户端、缓存、钩子、试听与「朗读」的实际发声（两个按钮是灰的）。

## 怎么编译（实测通过，0 警告 0 错误）
```powershell
$env:DOTNET_CLI_HOME="$PWD\tmp\dotnethome"   # dotnet 要写 HOME 下的哨兵，工作区里给它一个
dotnet build 'mods\副官语音\Source\VoiceDeputy.csproj'
```
本机没有 net472 目标包、也没网：csproj 用 `NoStdLib` + 直接引用游戏 `Managed\` 下的程序集（同 `docs/mod开发基础.md` 里那条 `/nostdlib+` 的 csc 命令）；
`Source/NuGet.config` 清空包源，断网也能 build。换机器传 `/p:RimWorldManagedDir=...`。

## 已知缺口
- `SpeechLibrary.Usage` 恒为 `0 条 / 0 B`；缓存目录约定 `Cache/Voice`（单一目录，全存档共用），代码里只有 `SpeechLibrary.FolderName` 这个常量。
- 规格草案里有、但本次字段清单没要的两项没实现：§3「附加参数（JSON）」、§4「预录音量」。
- 未实机启动游戏：设置页按钮、窗口排版、中文渲染、改开关后重启是否读回，**都还没在游戏里点过**。已验证的只有：编译通过、About.xml 可解析、
  DLL 的 AssemblyRef 只有 `Assembly-CSharp` / `mscorlib` / `UnityEngine.CoreModule` / `IMGUIModule` / `TextRenderingModule`。
- `Source/obj/` 是构建产物（已 gitignore）；`Source/UI/RowWidgets.cs` 是任务要求的那一个公共绘制器。
