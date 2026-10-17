# tools/asmref —— 只读程序集引用读取器（AssemblyRef）

> 2026-10-13 建立。**这个目录替代 `tmp/probe17/dump-asmref.ps1`**（后者行尺寸算错、会静默读出乱码）。

## 登记信息（可直接并入工程工具登记表）

| 字段 | 内容 |
|---|---|
| **名称** | `tools/asmref/Get-AssemblyRef.ps1`（互校器：`tools/asmref/Verify-VsReflection.ps1`） |
| **类别** | 取证 / 只读（**不改任何文件**，只读字节；不加载程序集、不执行代码、不解析依赖） |
| **前置条件** | Windows PowerShell 5.1（本机没有 pwsh 7，**所有 .ps1 都由 5.1 跑**）。无需 .NET SDK、无需 net10 运行时、无需 ILSpy/Cecil。互校器另需 .NET Framework 4.x（5.1 自带） |
| **效果** | 读出指定托管 PE 的：①Assembly 表（自身名字+版本）②AssemblyRef 表（每一条引用的名字+版本）；可 `-MatchName` 过滤、可 `-AsJson` 供脚本消费 |
| **可观测后置条件** | ①stdout 每行一条 `名字 版本`（`-AsJson` 时为 JSON 对象，含 `refs[]`/`checks[]`/`selfCheck`）②退出码 0=通过，1=**自检不通过（stderr 有具体原因）**③`-ShowChecks` 时 stderr 出现 `[自检] A./B./C.` 三行。**没有第三种状态：不通过就不输出结论** |
| **证据来源** | ①规范：ECMA-335 II.22（表列定义）、II.24.2.6（`#~` 流头 + 索引宽度规则）②实测：108 个真实程序集的行尺寸自检（delta=0/2/4）、24 个与 CLR 互校逐条一致（见下）③变异测试：把历史错公式塞回去，看自检能否拦下（见下） |
| **已知边界** | 只读 Assembly/AssemblyRef 两张表；不解析 TypeRef/MemberRef/IL；`#-`（ENC 未压缩流）头结构相同也接住了，但 ENC 增量（`0x1E`/`0x1F`）未在真实文件里遇到过（规范值 8/4） |

## 为什么会有它：一类事故，两个实例

工程里反复出现同一类事故：**工具在受限环境下不报错，却给出错误结论**。已知三例：

1. `IlDump.exe --usages` 传短名 → `USAGE HITS: 0` 假阴性
2. `tmp/probe17/dump-asmref.ps1` 行尺寸算错 → 对含 `ClassLayout` 的程序集读出乱码
3. `node src/cli.js status` 的 `gameRunning` 在沙箱下恒为 `false`（且它把枚举结果对象当数组用，`obj.length > 0` 恒假）

本目录对付的是第 2 例。它的要害不是"算错"，而是**算错了还打印**——打印出来的东西看起来像程序集名（`__12_0`、`d`、`s` 都真的是 `#Strings` 堆里存在的字符串，只是不是那一行的），
所以"看着像结论"就冒充了结论。上一轮把 FoodControl.dll 的引用读成乱码，如果没人拿另一条路对一下，谱系结论就错了。

## 用法

```powershell
# 单文件（默认带自检；退出码 1 = 判据不足/结构不符，stderr 有原因）
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\asmref\Get-AssemblyRef.ps1 `
  -Path tmp\foodcontrol-game\FoodControl.dll -ShowChecks

# 只要 System.* 的引用；要 JSON 供脚本消费
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\asmref\Get-AssemblyRef.ps1 `
  -Path <dll> -MatchName 'System.*' -AsJson

# 与 CLR 互校（第二实现）：每行 OK / MISMATCH / SKIP / READER-FAIL；有 MISMATCH 时退出码 1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\asmref\Verify-VsReflection.ps1 `
  -Path <dll>            # 也可 -ListFile <每行一个路径的文本文件>
```

真实输出（2026-10-13 实测，含 `ClassLayout` 的真实程序集）：

```
=== AssemblyRef of FoodControl.dll  (selfCheck=on) ===
本程序集: FoodControl 0.0.0.0
AssemblyRef count = 7
  Assembly-CSharp                                    1.6.9676.17735
  FoodControl.Core                                   0.0.0.0
  mscorlib                                           4.0.0.0
  System.Core                                        4.0.0.0
  UnityEngine.CoreModule                             0.0.0.0
  UnityEngine.IMGUIModule                            0.0.0.0
  UnityEngine.TextRenderingModule                    0.0.0.0
```

同一个文件用旧脚本 `tmp/probe17/dump-asmref.ps1` 的输出（**乱码，且不报错**）：

```
=== AssemblyRef of FoodControl.dll ===
AssemblyRef count = 7
                                                          0.15360.14654.24415
  __12_0                                                  30.53.33.54
  0                                                       28.48.30.49
  5_0                                                     38.58.39.137
  d                                                       2057.1261.4617.1265
  s                                                       20084.30575.25150.24415
  ss41_0                                                  17.43.17.44
```

```
=== AssemblyRef of Assembly-CSharp.dll  (selfCheck=on) ===   # 游戏目录 Managed\ 下的本体
本程序集: Assembly-CSharp 1.6.9676.17735
AssemblyRef count = 26   （含 mscorlib 4.0.0.0 / System.Core 4.0.0.0 / Unity.* …）
```

## 正确的行尺寸（ECMA-335 II.22）与依据

**依据说明**：本机没有 ECMA-335 规范文本，以下列的是**规范已知的列结构 + 本机实测反推并验证**的结论
（验证方式：把错的公式塞回脚本，看三道自检能否拦下该文件——能拦下就说明这个尺寸是真的被用到了；
外加与 CLR `GetReferencedAssemblies()` 逐条互校）。带"同宽"标注的：**公式错但在这台机器的
2 字节索引文件上字节数恰好相同**，所以它们单独不会立刻出错，只在 4 字节堆的文件上显形。

| 表 | 列（字节） | 行尺寸 | 旧脚本的错 | 影响面（108 个真实程序集里） |
|---|---|---|---|---|
| `0x0F` ClassLayout | PackingSize(2) + ClassSize(4) + Parent(TypeDef idx 2) | **8** | 按 6 算 | **43 个文件受影响**（有 ≥1 行就被推歪 2 字节） |
| `0x15` PropertyMap | Parent(TypeDef idx 2) + PropertyList(Property idx 2) | **4** | 用 strIdxSize 当 PropertyList（4 字节堆时 6） | 13 个文件的自检拦下老公式 |
| `0x22` AssemblyOS | OSPlatformId(4) + OSMajorVersion(4) + OSMinorVersion(4) | **12** | 按 4+blob+str+str=10 算 | 0 个文件（该表在本机样本里从不出现） |
| `0x25` AssemblyRefOS | 上面 12 + AssemblyRef idx(2) | **14** | 按 4+IdxSize(0x23)=6 算 | 0 个文件（从不出现） |
| `0x28` ManifestResource | Offset(4) + Flags(4) + Name(str 2) + Implementation(Implementation coded 2) | **12** | 按 IdxSize(0x02)+IdxSize(0x02)=4 算 | mscorlib/System.dll/System.Data.dll 等有该表，但都在 AssemblyRef 之后 → 不影响引用结论 |
| `0x29` NestedClass | NestedClass(TypeDef idx 2) + EnclosingClass(TypeDef idx 2) | **4** | 按 2+2+TypeOrMethodDef+str=8 算 | 67 个文件的自检拦下老公式 |
| `0x2B` MethodSpec | Method(MethodDefOrRef coded 2) + Instantiation(blob 2) | **4** | 用 IdxSize(0x29)+TypeDefOrRef（同宽，4 字节 blob 堆时 6） | 10 个文件的自检拦下老公式 |

**旧脚本还错、但上一轮没列出来的表**（这次一并修正）：

| 表 | 列 | 行尺寸 | 旧脚本 | 影响 |
|---|---|---|---|---|
| `0x12` EventMap | Parent(TypeDef idx) + EventList(Event idx) | 4 | 用 TypeDefOrRef 编码索引 | 有 Event 表的文件 |
| `0x13` EventPtr | Event idx | 2 | 按 TypeDefOrRef+IdxSize(0x02) 算 | 少见 |
| `0x16` PropertyPtr | Property idx | 2 | 按 TypeDefOrRef+IdxSize(0x02) 算 | 少见 |
| `0x1A` ModuleRef | Name(str) | 2 | 按两个 str=4 算 | 自检拦下 12/108（mscorlib、NAudio、steamworks…） |
| `0x1B` TypeSpec | Signature(blob) | 2 | 按 str+blob=4 算 | **自检拦下 75/108 —— 这是旧脚本最大的单一污染源，比 ClassLayout(43) 还广**。小探针 `ProbeB.dll` 就只栽在这张表上（它没有 ClassLayout） |
| `0x1C` ImplMap | MappingFlags(2)+MemberForwarded(coded)+ImportName(str)+**ImportScope(ModuleRef idx)** | 6 | 漏了 ImportScope（-2/行） | NAudio 122 行、System.dll 386 行 → 自检拦下 |
| `0x1E` ENCLog | Token(4)+FuncCode(4) | 8 | 按 4 算 | 只在 ENC 流里，样本中从不出现 |
| `0x24` AssemblyRefProcessor | Processor(4)+AssemblyRef idx | 6 | 按 IdxSize(0x23)+blob 算 | 从不出现 |
| `0x26` File | Flags(4)+Name(str)+HashValue(blob) | 8 | 多算了两个索引为 4+4+str+str+Implementation | 本机样本无该表，靠规范定值 |
| `0x2A` GenericParam | Number(2)+Flags(2)+Owner(TypeOrMethodDef)+Name(str) | 8 | 按 MethodDefOrRef+blob 算 | 有泛型的文件 |
| `0x2C` GenericParamConstraint | Owner(GenericParam idx)+Constraint(TypeDefOrRef) | 4 | 按 IdxSize(0x02) 算 | 有泛型约束的文件 |

> 说明：下面每行的分母是那一次扫描的文件数（108 或 109，差别只是有没有算上 1.7 的 Assembly-CSharp.dll）。
>
> **旧脚本的错不止 ClassLayout**。上一轮"小文件上看起来是对的、互校通过"这个观察只对**部分**探针成立：
> `tmp/probe17/ProbeB.dll` 里没有 ClassLayout，却有 1 行 TypeSpec(0x1B)，于是 AssemblyRef 表照样被推歪 2 字节，
> 旧脚本对它输出的就是两行**空名字**（`0.0.0.0` / `0.0.0.0`，见上文）——"小文件"不是安全区。
> 真正安全的判据只有一个：**自检通过**。
>
> 注：`0x0C` CustomAttribute 的 `HasCustomAttribute` 编码索引，旧脚本只拿了 3 张表求最大值（规范是 **22 张**）。
> 本工具列全了——列少会低估最大值，遇到"某张表行数 ≥ 65536"的文件就会按 2 字节读 4 字节索引。

## 三道自检（这是这个工具的真正价值）

| | 自检 | 抓什么 | 覆盖哪些表 |
|---|---|---|---|
| A. 结构 | 按行尺寸累加出的表区末尾，必须落在 `#~` 流末尾 0..7 字节内（实测真实程序集 delta ∈ {0,2,4}） | 任何"每行错 K 字节 × 行数"足够大的错 | 全部 |
| B. 锚点 | Assembly(0x20) 表的 名字+版本，必须等于 `[System.Reflection.AssemblyName]::GetAssemblyName($Path)` | **0x00..0x1F 全部表的行尺寸**——ClassLayout 那 2 字节就是在这里被抓住的 | 0x00..0x1F |
| C. 输出 | 每条 AssemblyRef 的名字：非空、长度 ≥ 2、含 ASCII 字母、字符集合法；PublicKeyOrToken/HashValue 的 blob 索引在 `#Blob` 堆内；Culture 索引在 `#Strings` 堆内 | 历史事故里的 `__12_0`（无字母）/`d`/`s`（长度 1）/`0`/`5_0`（无字母）全被拦下 | 0x23 本体 |

**变异测试（把历史错公式塞回去，看自检会不会沉默）**——2026-10-13 实测：

| 塞回的错误 | 自检结果 |
|---|---|
| `0x0F ClassLayout` 按 6 算 | 108 个真实程序集里 **43 个被拦下**（其余 65 个没有 ClassLayout 行，字节数不变）。拦下的原因：小文件走 B（锚点名对不上，读出空串），大文件走 A（delta 超出容差） |
| `0x29 NestedClass` 按 8 算 | 拦下 67/109 |
| `0x1C ImplMap` 漏 ImportScope | 拦下（NAudio：delta=248 字节） |
| `0x15 PropertyMap` 用 strIdxSize | 拦下 13/109（4 字节字符串堆的文件） |
| `0x2B MethodSpec` 用 0x29 索引 | 拦下 10/109（4 字节 blob 堆的文件） |

**这就是"静默假结论"的修法**：不是让解析更准就完事，而是让"不准"必须变成**失败**。

## 与 CLR 的互校（第二实现）

`Verify-VsReflection.ps1` 拿 `Assembly.ReflectionOnlyLoadFrom().GetReferencedAssemblies()`
（CLR 自己的元数据读取器）逐条比对名字+版本+条数。2026-10-13 实测 **24 个程序集全部一致**：

```
OK  FoodControl.dll（7 条）／FoodControl.Core.dll（2 条）／Assembly-CSharp.dll（26 条）
OK  mscorlib.dll（0 条，含 100 行 ImplMap）／netstandard.dll（15 条）／System.Data.dll（9 条）
OK  System.Core/System.Configuration/Mono.Security/NAudio/NVorbis/ISharpZipLib/…
OK  tmp\probe17\ 下 6 个探针（LegacyNet6Lineage/ProbeA/B/C/D/F/G）
—— 互校结束：不一致 0 个 ——
```

## 已知坑（留给下一个人）

1. **`#Strings` 堆允许共享后缀**：名字索引的前一个字节**不一定**是 0（例：`Configuration` 可以是
   `System.Configuration` 的后缀）。第一版自检要求"前一字节为 0"，在 9 个 .NET Framework 程序集上
   误报——**过严的自检和过松的自检一样糟**，都会让人不再相信工具。
2. **`delta` 不是恒等于 0**：真实程序集是 0/2/4（写者会做对齐 padding），所以容差给 0..7；
   但正因为有 padding，**光靠 A 抓不住 2 字节级的错**——真正兜住 ClassLayout 的是 B（锚点）。
3. **`$PSScriptRoot` 在 `-File` 模式下求 `param()` 默认值时是空的**（PS 5.1 实测：
   `Join-Path` 报 "Path is an empty string"）。要在脚本体里算。
4. **别在这个仓库里写无 BOM 的中文 .ps1**：Windows PowerShell 5.1 按 ANSI 读，
   中文串断裂 → `ParserError: The string is missing the terminator`，脚本根本跑不起来
   （`tmp/probe17/run-probe.ps1` 就是这么坏的）。本目录两个脚本都是 **UTF-8 with BOM**。
