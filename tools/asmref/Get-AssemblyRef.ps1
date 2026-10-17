<#
.SYNOPSIS
  只读取一个托管 PE 的 Assembly 表与 AssemblyRef 表（程序集名 + 版本），不加载、不解析依赖。

.DESCRIPTION
  纯字节级 ECMA-335 元数据解析，零依赖：不需要 .NET SDK、不需要 net10 运行时、
  不需要 ILSpy/Cecil。只要 Windows PowerShell 5.1 就能跑。

  为什么要有它：`tmp/probe17/dump-asmref.ps1` 对**含 ClassLayout(0x0F) 表**的真实程序集
  会算错 `#~` 表的行偏移，读出乱码却**不报错**——它把 FoodControl.dll 的引用读成
  `"__12_0"` / `"d"` / `"s"`（见 tmp/probe17/1.7工程准备-结论.md 引用的那次结论）。
  乱码冒充结论比报错危险得多，所以本脚本把"读得对不对"变成**可执行的自检**：
  任何一条自检不过就 `throw`（退出码 1、stderr 有说明），绝不把可疑字符串当结论打印。

  自检（三道，见 -ShowChecks）：
    A. 结构自检：按行尺寸累加出的表区末尾，必须落在 `#~` 流末尾附近（0..8 字节的
       对齐padding）。实测 25+ 个真实程序集 delta ∈ {0,2,4}；若某张表的行尺寸写错，
       误差 = 每行错字节数 × 行数，大表立刻爆出容差（例：ImplMap 少 2 字节 × 122 行
       = 244 字节）。
    B. 锚点自检：Assembly(0x20) 表里的程序集名，必须等于 .NET 自己算出的
       `[System.Reflection.AssemblyName]::GetAssemblyName($Path).Name`。
       这一条覆盖 **0x00..0x1F 全部表的行尺寸**——ClassLayout 行尺寸错的 2 字节
       正是从这里被抓住的（Assembly 表的偏移被推歪，名字就对不上）。
    C. 输出自检：每个 AssemblyRef 的名字必须是**看起来像程序集名**的字符串：
       非空、长度 ≥ 2、至少含一个 ASCII 字母、字符集在 [A-Za-z0-9_.\-+`] 内、
       且索引落在 `#Strings` 堆内、且它前面一个字节是字符串终止符（即它真的是
       一个字符串的开头）。历史事故里的 `__12_0`（无字母）、`d`/`s`（长度 1）、
       `0`（长度 1）、`5_0`（无字母）全部被 C 拦下。

.PARAMETER Path
  托管程序集（.dll/.exe）路径。原生 DLL 会明确报"不是托管 PE"。

.PARAMETER MatchName
  只输出名字匹配该通配符的程序集（`-like` 语义），例如 `System.*`。

.PARAMETER AsJson
  以 JSON 输出（便于脚本消费）：{ path, assemblyName, assemblyVersion, refs:[{name,version}], checks:[] }。

.PARAMETER ShowChecks
  把三道自检的明细打到 stderr（stdout 保持只有结果，便于管道消费）。

.PARAMETER NoSelfCheck
  跳过 A/B 两道结构自检（C 永远保留）。只在"你怀疑自检本身有误"时用，用了就在输出
  里带 `"selfCheck":"off"`，避免有人把没自检的结果当成有自检的。

.EXAMPLE
  powershell.exe -NoProfile -File tools\asmref\Get-AssemblyRef.ps1 -Path tmp\foodcontrol-game\FoodControl.dll
.EXAMPLE
  powershell.exe -NoProfile -File tools\asmref\Get-AssemblyRef.ps1 -Path <dll> -MatchName 'System.*' -AsJson

.NOTES
  2026-10-13 创建（同日修掉 cli.js status 假阴性、run-probe.ps1 期望值两处同类缺陷）。
  规范依据：ECMA-335 II.22（元数据表的列定义）与 II.24.2.6（#~ 流头部）。
  独立互校：同目录 Verify-VsReflection.ps1（用 .NET Framework 的
  `Assembly.ReflectionOnlyLoadFrom().GetReferencedAssemblies()` 做第二实现对照）。
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory = $true, Position = 0)][string]$Path,
  [string]$MatchName = '',
  [switch]$AsJson,
  [switch]$ShowChecks,
  [switch]$NoSelfCheck
)

$ErrorActionPreference = 'Stop'

function Fail([string]$msg) {
  throw (New-Object System.Exception $msg)
}

# ─────────────────────────── PE / CLI / 流的定位 ───────────────────────────
$blob = [System.IO.File]::ReadAllBytes($Path)

function U16([int]$o) { [int]$blob[$o] -bor ([int]$blob[$o + 1] -shl 8) }
function U32([int]$o) {
  [uint32]([int]$blob[$o] -bor ([int]$blob[$o + 1] -shl 8) -bor ([int]$blob[$o + 2] -shl 16) -bor ([int]$blob[$o + 3] -shl 24))
}
function U64([int]$o) { [uint64](U32 $o) -bor ([uint64](U32 ($o + 4)) -shl 32) }

if ($blob.Length -lt 0x40) { Fail "'$Path' 太小，不像 PE 文件。" }
if ((U16 0) -ne 0x5A4D) { Fail "'$Path' 不是 PE 文件（没有 MZ 签名）。" }
$peOff = [int](U32 0x3C)
if ((U32 $peOff) -ne 0x00004550) { Fail "'$Path' 不是 PE 文件（没有 PE\0\0 签名）。" }

$coff = $peOff + 4
$numSec = U16 ($coff + 2)
$optSize = U16 ($coff + 16)
$optOff = $coff + 20
$pe32plus = ((U16 $optOff) -eq 0x20B)
$ddOff = if ($pe32plus) { $optOff + 112 } else { $optOff + 96 }
$cliRva = U32 ($ddOff + 14 * 8)   # 数据目录 14 = CLR Runtime Header
if ($cliRva -eq 0) { Fail "'$Path' 不是托管程序集（CLR 数据目录为空）。" }

$secOff = $optOff + $optSize
$secs = @()
for ($i = 0; $i -lt $numSec; $i++) {
  $s = $secOff + $i * 40
  $secs += [pscustomobject]@{
    VSize = U32 ($s + 8); VA = U32 ($s + 12); RawSize = U32 ($s + 16); Raw = U32 ($s + 20)
  }
}
function RvaToOff([uint32]$rva) {
  foreach ($s in $secs) {
    $span = [Math]::Max($s.VSize, $s.RawSize)
    if ($rva -ge $s.VA -and $rva -lt ($s.VA + $span)) { return [int]($s.Raw + ($rva - $s.VA)) }
  }
  return -1
}

$cliOff = RvaToOff $cliRva
if ($cliOff -lt 0) { Fail "CLR 头 RVA 0x$('{0:X}' -f $cliRva) 不在任何节里。" }
$mdRva = U32 ($cliOff + 8)
$mdOff = RvaToOff $mdRva
if ($mdOff -lt 0) { Fail "元数据 RVA 0x$('{0:X}' -f $mdRva) 不在任何节里。" }
if ((U32 $mdOff) -ne 0x424A5342) { Fail "'$Path' 的元数据根没有 BSJB 签名。" }

# 元数据根：4 签名 + 4 大版本 + 4 小版本 + 4 保留 + 4 版本串长 + 版本串 + 2 flags + 2 流数
$verLen = U32 ($mdOff + 12)
$p = $mdOff + 16 + $verLen
$numStreams = U16 ($p + 2)
$p += 4
$streams = @{}
for ($i = 0; $i -lt $numStreams; $i++) {
  $off = U32 $p; $size = U32 ($p + 4); $p += 8
  $n = ''
  while ($blob[$p] -ne 0) { $n += [char]$blob[$p]; $p++ }
  $p++                                   # 跳过结尾的 0
  $p = $p + ((4 - ($p % 4)) % 4)         # 名字串按 4 字节对齐
  $streams[$n] = [pscustomobject]@{ Off = $mdOff + $off; Size = $size }
}

# #~ 是压缩表流；#- 是未压缩（ENC）流，头部结构相同，也接住
$tbl = $streams['#~']
if (-not $tbl) { $tbl = $streams['#-'] }
if (-not $tbl) { Fail "'$Path' 里没有 #~ / #- 表流（不是常规托管程序集）。" }
$strHeap = $streams['#Strings']
if (-not $strHeap) { Fail "'$Path' 里没有 #Strings 堆。" }

$heapSizes = $blob[$tbl.Off + 6]
$valid = U64 ($tbl.Off + 8)
$sorted = U64 ($tbl.Off + 16)

$rows = @{}
$q = $tbl.Off + 24                        # 头部 = 24 字节 + 每张存在的表 4 字节行数
for ($i = 0; $i -lt 64; $i++) {
  if ($valid -band ([uint64]1 -shl $i)) {
    $rows[$i] = [int](U32 $q)
    $q += 4
  }
}

$strIdxSize = if ($heapSizes -band 0x01) { 4 } else { 2 }
$guidIdxSize = if ($heapSizes -band 0x02) { 4 } else { 2 }
$blobIdxSize = if ($heapSizes -band 0x04) { 4 } else { 2 }

# 简单索引宽度：行数 ≥ 65536 才用 4 字节（ECMA-335 II.24.2.6）
function IdxSize([int]$t) {
  if ($rows.ContainsKey($t) -and $rows[$t] -ge 65536) { return 4 } else { return 2 }
}
# 编码索引宽度：取被编码各表行数的最大值，≥ 2^(16-tagBits) 才用 4 字节
function CodedSize([int]$tagBits, [int[]]$tabs) {
  $max = 0
  foreach ($t in $tabs) { if ($rows.ContainsKey($t) -and $rows[$t] -gt $max) { $max = $rows[$t] } }
  if ($max -ge (1 -shl (16 - $tagBits))) { return 4 } else { return 2 }
}

# 编码索引的成员表（ECMA-335 II.24.2.6）。注意必须列**全**：
# 只列一部分会低估最大值 → 该用 4 字节却按 2 字节读 → 整张表错位。
$ResolutionScope     = CodedSize 2 @(0x00, 0x1A, 0x23, 0x01)
$TypeDefOrRef        = CodedSize 2 @(0x02, 0x01, 0x1B)
$HasConstant         = CodedSize 2 @(0x04, 0x08, 0x17)
$HasCustomAttribute  = CodedSize 5 @(0x06, 0x04, 0x01, 0x02, 0x08, 0x09, 0x0A, 0x00, 0x0E, 0x17, 0x14, 0x11, 0x1A, 0x1B, 0x20, 0x23, 0x26, 0x27, 0x28, 0x2A, 0x2C, 0x2B)
$CustomAttributeType = CodedSize 3 @(0x06, 0x0A)
$HasFieldMarshal     = CodedSize 1 @(0x04, 0x08)
$HasDeclSecurity     = CodedSize 2 @(0x02, 0x06, 0x20)
$HasSemantics        = CodedSize 1 @(0x14, 0x17)
$MemberRefParent     = CodedSize 3 @(0x02, 0x01, 0x1A, 0x06, 0x1B)
$MethodDefOrRef      = CodedSize 1 @(0x06, 0x0A)
$MemberForwarded     = CodedSize 1 @(0x04, 0x06)
$Implementation      = CodedSize 2 @(0x26, 0x23, 0x27)
$TypeOrMethodDef     = CodedSize 1 @(0x02, 0x06)

<#
  ── 行尺寸表（ECMA-335 II.22）──
  列定义=字节数，逐张写清，便于对着规范复核。带 ★ 的是原 dump-asmref.ps1 写错过的。
  0x00 Module            2 + str + 3*guid                        (Generation,Name,Mvid,EncId,EncBaseId)
  0x01 TypeRef           ResolutionScope + str + str             (ResolutionScope,Name,Namespace)
  0x02 TypeDef           4 + str + str + TypeDefOrRef + Idx(Field) + Idx(MethodDef)
  0x03 FieldPtr          Idx(Field)
  0x04 Field             2 + str + blob                          (Flags,Name,Signature)
  0x05 MethodPtr         Idx(MethodDef)
  0x06 MethodDef         4+2+2 + str + blob + Idx(Param)         (RVA,ImplFlags,Flags,Name,Signature,ParamList)
  0x07 ParamPtr          Idx(Param)
  0x08 Param             2 + 2 + str                             (Flags,Sequence,Name)
  0x09 InterfaceImpl     Idx(TypeDef) + TypeDefOrRef
  0x0A MemberRef         MemberRefParent + str + blob
  0x0B Constant          2 + HasConstant + blob
  0x0C CustomAttribute   HasCustomAttribute + CustomAttributeType + blob
  0x0D FieldMarshal     HasFieldMarshal + blob
  0x0E DeclSecurity     2 + HasDeclSecurity + blob
★ 0x0F ClassLayout      2 + 4 + Idx(TypeDef)                    = PackingSize(2),ClassSize(4),Parent —— 8 字节（原脚本按 6 算）
  0x10 FieldLayout      4 + Idx(Field)                          (Offset,Field)
  0x11 StandAloneSig    blob
  0x12 EventMap         Idx(TypeDef) + Idx(Event)                (Parent,EventList) ★原脚本这里用的是 TypeDefOrRef 编码索引
  0x13 EventPtr         Idx(Event)
  0x14 Event            2 + str + TypeDefOrRef
★ 0x15 PropertyMap     Idx(TypeDef) + Idx(Property)             (Parent,PropertyList)
  0x16 PropertyPtr      Idx(Property)
  0x17 Property         2 + str + blob
  0x18 MethodSemantics  2 + Idx(MethodDef) + HasSemantics
  0x19 MethodImpl       Idx(TypeDef) + MethodDefOrRef + MethodDefOrRef
  0x1A ModuleRef        str                                      ★原脚本按两个 str 算
  0x1B TypeSpec         blob                                     ★原脚本按 str+blob 算
  0x1C ImplMap          2 + MemberForwarded + str + Idx(ModuleRef)   (Flags,MemberForwarded,ImportName,ImportScope)
  0x1D FieldRVA         4 + Idx(Field)
  0x1E ENCLog           4 + 4                                    (Token,FuncCode) ★原脚本按 4 算
  0x1F ENCMap           4                                        (Token)
  0x20 Assembly         4+2+2+2+2+4 + blob + str + str          (HashAlgId,Major,Minor,Build,Revision,Flags,PublicKey,Name,Culture)
  0x21 AssemblyProcessor 4                                      (Processor)
★ 0x22 AssemblyOS       12                                     (OSPlatformId,OSMajorVersion,OSMinorVersion 三个 U32) —— 原脚本按 4+blob+str+str 算
  0x23 AssemblyRef      2+2+2+2+4 + blob + str + str + blob     (Major..Revision,Flags,PublicKeyOrToken,Name,Culture,HashValue)
  0x24 AssemblyRefProcessor 4 + Idx(AssemblyRef)
★ 0x25 AssemblyRefOS    12 + Idx(AssemblyRef)
  0x26 File             4 + str + blob                          (Flags,Name,HashValue) ★原脚本多算了两个索引
  0x27 ExportedType     4+4 + str + str + Implementation
★ 0x28 ManifestResource 4+4 + str + Implementation             (Offset,Flags,Name,Implementation)
★ 0x29 NestedClass      Idx(TypeDef) + Idx(TypeDef)             (NestedClass,EnclosingClass)
  0x2A GenericParam     2+2 + TypeOrMethodDef + str             (Number,Flags,Owner,Name)
★ 0x2B MethodSpec      MethodDefOrRef + blob
  0x2C GenericParamConstraint Idx(GenericParam) + TypeDefOrRef
  0x2D-0x3F 规范里没有定义（保留/未分配）→ 视为错误，不猜。
#>
function RowSize([int]$t) {
  switch ($t) {
    0x00 { return 2 + $strIdxSize + 3 * $guidIdxSize }
    0x01 { return $ResolutionScope + 2 * $strIdxSize }
    0x02 { return 4 + 2 * $strIdxSize + $TypeDefOrRef + (IdxSize 0x04) + (IdxSize 0x06) }
    0x03 { return (IdxSize 0x04) }
    0x04 { return 2 + $strIdxSize + $blobIdxSize }
    0x05 { return (IdxSize 0x06) }
    0x06 { return 4 + 2 + 2 + $strIdxSize + $blobIdxSize + (IdxSize 0x08) }
    0x07 { return (IdxSize 0x08) }
    0x08 { return 2 + 2 + $strIdxSize }
    0x09 { return (IdxSize 0x02) + $TypeDefOrRef }
    0x0A { return $MemberRefParent + $strIdxSize + $blobIdxSize }
    0x0B { return 2 + $HasConstant + $blobIdxSize }
    0x0C { return $HasCustomAttribute + $CustomAttributeType + $blobIdxSize }
    0x0D { return $HasFieldMarshal + $blobIdxSize }
    0x0E { return 2 + $HasDeclSecurity + $blobIdxSize }
    0x0F { return 2 + 4 + (IdxSize 0x02) }
    0x10 { return 4 + (IdxSize 0x04) }
    0x11 { return $blobIdxSize }
    0x12 { return (IdxSize 0x02) + (IdxSize 0x14) }
    0x13 { return (IdxSize 0x14) }
    0x14 { return 2 + $strIdxSize + $TypeDefOrRef }
    0x15 { return (IdxSize 0x02) + (IdxSize 0x17) }
    0x16 { return (IdxSize 0x17) }
    0x17 { return 2 + $strIdxSize + $blobIdxSize }
    0x18 { return 2 + (IdxSize 0x06) + $HasSemantics }
    0x19 { return (IdxSize 0x02) + $MethodDefOrRef + $MethodDefOrRef }
    0x1A { return $strIdxSize }
    0x1B { return $blobIdxSize }
    0x1C { return 2 + $MemberForwarded + $strIdxSize + (IdxSize 0x1A) }
    0x1D { return 4 + (IdxSize 0x04) }
    0x1E { return 4 + 4 }
    0x1F { return 4 }
    0x20 { return 4 + 2 + 2 + 2 + 2 + 4 + $blobIdxSize + $strIdxSize + $strIdxSize }
    0x21 { return 4 }
    0x22 { return 12 }
    0x23 { return 2 + 2 + 2 + 2 + 4 + $blobIdxSize + $strIdxSize + $strIdxSize + $blobIdxSize }
    0x24 { return 4 + (IdxSize 0x23) }
    0x25 { return 12 + (IdxSize 0x23) }
    0x26 { return 4 + $strIdxSize + $blobIdxSize }
    0x27 { return 4 + 4 + $strIdxSize + $strIdxSize + $Implementation }
    0x28 { return 4 + 4 + $strIdxSize + $Implementation }
    0x29 { return (IdxSize 0x02) + (IdxSize 0x02) }
    0x2A { return 2 + 2 + $TypeOrMethodDef + $strIdxSize }
    0x2B { return $MethodDefOrRef + $blobIdxSize }
    0x2C { return (IdxSize 0x2A) + $TypeDefOrRef }
    default { return -1 }
  }
}

$tableOff = @{}
$cur = $q
foreach ($t in ($rows.Keys | Sort-Object)) {
  $sz = RowSize $t
  if ($sz -lt 0) {
    Fail ("表 0x{0:X2} 的行尺寸未知（规范里没有这张表，或本脚本还没实现）—— 不猜、不继续。" -f $t)
  }
  $tableOff[$t] = $cur
  $cur += $sz * $rows[$t]
}
$tablesEnd = $cur
$streamEnd = $tbl.Off + $tbl.Size

# ─────────────────────────── 字符串堆 ───────────────────────────
function GetStr([int]$idx) {
  if ($idx -le 0) { return '' }
  $o = $strHeap.Off + $idx
  $end = $strHeap.Off + $strHeap.Size
  if ($o -ge $end) { return $null }   # 越界 → 返回 $null 让调用方自检报错
  $sb = New-Object System.Text.StringBuilder
  while ($o -lt $end -and $blob[$o] -ne 0) { [void]$sb.Append([char]$blob[$o]); $o++ }
  if ($o -ge $end) { return $null }   # 没找到结尾的 0 → 不是合法字符串
  return $sb.ToString()
}
function ReadIdx([int]$off, [int]$size) { if ($size -eq 4) { return [int](U32 $off) } else { return (U16 $off) } }

# ─────────────────────────── 自检 ───────────────────────────
$checks = New-Object System.Collections.Generic.List[string]

# A. 结构自检：表区末尾必须落在流末尾（允许 0..7 字节对齐 padding）
if (-not $NoSelfCheck) {
  $delta = $streamEnd - $tablesEnd
  $checks.Add("A.结构: 表区末尾相对 #~ 流末尾 delta=$delta 字节（实测真实程序集为 0/2/4，容差 0..7）")
  if ($delta -lt 0 -or $delta -gt 7) {
    Fail ("结构自检失败：按行尺寸算出的表区末尾($tablesEnd) 与 #~ 流末尾($streamEnd) 相差 $delta 字节。`n" +
      "这几乎一定是**行尺寸表有误**（或文件不是普通 ECMA-335 元数据）——拒绝输出任何解析结果。")
  }
}

# B. 锚点自检：Assembly(0x20) 表的 Name/Version 必须等于 .NET 自己算出的程序集名/版本
$assemblyName = $null
$assemblyVersion = $null
if ($tableOff.ContainsKey(0x20)) {
  $ao = $tableOff[0x20]
  # Assembly 行：HashAlgId(4) Major(2) Minor(2) Build(2) Revision(2) Flags(4) PublicKey(blob) Name(str) Culture(str)
  # （第一版这里把 Major 读到了 HashAlgId 的低 16 位，读出 "32772.0.0.0" —— 已修，行内注释留痕）
  $maj = U16 ($ao + 4); $min = U16 ($ao + 6); $bld = U16 ($ao + 8); $rev = U16 ($ao + 10)
  $assemblyVersion = "$maj.$min.$bld.$rev"
  $nameIdx = ReadIdx ($ao + 16 + $blobIdxSize) $strIdxSize
  $assemblyName = GetStr $nameIdx
}
$nameFromDotNet = $null
$versionFromDotNet = $null
try {
  $an = [System.Reflection.AssemblyName]::GetAssemblyName($Path)
  $nameFromDotNet = $an.Name
  if ($an.Version) { $versionFromDotNet = $an.Version.ToString() }
} catch { }

if (-not $NoSelfCheck) {
  if ($null -eq $assemblyName) {
    Fail ("锚点自检失败：读不出 Assembly(0x20) 表的程序集名（索引越界或字符串未终止）。`n" +
      "说明 0x00..0x1F 里的行尺寸至少有一张是错的 —— 拒绝输出任何 AssemblyRef。")
  }
  $checks.Add("B.锚点: 字节解析=$assemblyName $assemblyVersion  .NET 读取=$nameFromDotNet $versionFromDotNet")
  if ($nameFromDotNet -and $assemblyName -ne $nameFromDotNet) {
    Fail ("锚点自检失败：字节解析出的程序集名 '$assemblyName' 与 .NET 读取的 '$nameFromDotNet' 不一致。`n" +
      "这是**行尺寸错**的典型症状（表偏移被推歪，读到堆里的另一个字符串），拒绝输出结论。")
  }
  if ($versionFromDotNet -and $assemblyVersion -ne $versionFromDotNet) {
    Fail ("锚点自检失败：字节解析出的版本 '$assemblyVersion' 与 .NET 读取的 '$versionFromDotNet' 不一致。`n" +
      "同样是表偏移被推歪的症状（版本字段读错列），拒绝输出结论。")
  }
  if (-not $nameFromDotNet) {
    $checks.Add("B.锚点: .NET 读不出该文件的程序集名（跨代/奇异元数据？），本项退化为只检查非空")
  }
}

# ─────────────────────────── AssemblyRef 表 ───────────────────────────
$refs = @()
if ($tableOff.ContainsKey(0x23)) {
  $arRow = RowSize 0x23
  $ao = $tableOff[0x23]
  $strHeapEnd = $strHeap.Off + $strHeap.Size
  $blobHeapSize = if ($streams['#Blob']) { [int]$streams['#Blob'].Size } else { -1 }   # -1 = 没有该堆，不做这项检查
  $suffixShared = 0
  for ($r = 0; $r -lt $rows[0x23]; $r++) {
    $o = $ao + $r * $arRow
    $maj = U16 $o; $min = U16 ($o + 2); $bld = U16 ($o + 4); $rev = U16 ($o + 6)
    $pkIdxOff = $o + 12
    $pkIdx = ReadIdx $pkIdxOff $blobIdxSize
    $nameIdxOff = $o + 12 + $blobIdxSize
    $nameIdx = ReadIdx $nameIdxOff $strIdxSize
    $cultureIdxOff = $o + 12 + $blobIdxSize + $strIdxSize
    $cultureIdx = ReadIdx $cultureIdxOff $strIdxSize
    $hashIdxOff = $cultureIdxOff + $strIdxSize
    $hashIdx = ReadIdx $hashIdxOff $blobIdxSize
    $name = GetStr $nameIdx

    # C. 输出自检：名字必须像程序集名。
    #    注意**不能**要求"前一字节是 0"：ECMA-335 II.24.2.3 的 #Strings 堆允许字符串
    #    共享后缀（例：'Configuration' 可以是 'System.Configuration' 的后缀），
    #    2026-10-13 实测 9 个 .NET Framework 程序集就是这样，那个判据会误报。
    #    所以改判：索引在堆内 + 以 0 结尾（GetStr 已保证）+ 内容像程序集名。
    $why = $null
    if ($null -eq $name) { $why = "字符串索引 $nameIdx 越出 #Strings 堆（堆大小 $($strHeap.Size)）或字符串未终止" }
    elseif ($name.Length -lt 2) { $why = "长度为 $($name.Length)（'$name'）" }
    elseif ($name -notmatch '[A-Za-z]') { $why = "不含任何 ASCII 字母（'$name'）" }
    elseif ($name -match '[^A-Za-z0-9_.\-+`]') { $why = "含非法字符（'$name'）" }
    elseif ($blobHeapSize -ge 0 -and $pkIdx -ge $blobHeapSize) { $why = "PublicKeyOrToken 的 blob 索引 $pkIdx 越出 #Blob 堆（大小 $blobHeapSize）" }
    elseif ($blobHeapSize -ge 0 -and $hashIdx -ge $blobHeapSize) { $why = "HashValue 的 blob 索引 $hashIdx 越出 #Blob 堆（大小 $blobHeapSize）" }
    elseif ($cultureIdx -ne 0 -and ($null -eq (GetStr $cultureIdx))) { $why = "Culture 的字符串索引 $cultureIdx 越出 #Strings 堆" }
    if ($why) {
      Fail ("输出自检失败：AssemblyRef 第 $($r + 1) 行的名字可疑 —— $why`n" +
        "乱码冒充结论比报错危险，所以这里直接失败而不是把它打印出来。`n" +
        "（先跑 -ShowChecks 看 A/B 两项；若 A/B 通过而这里失败，多半是这张文件的元数据不常规。）")
    }
    if ($nameIdx -gt 0 -and $blob[$strHeap.Off + $nameIdx - 1] -ne 0) { $suffixShared++ }

    $refs += [pscustomobject]@{ name = $name; version = "$maj.$min.$bld.$rev" }
  }
  $checks.Add("C.输出: AssemblyRef $($rows[0x23]) 行全部通过名字自检（其中 $suffixShared 行的名字坐落在一个共享后缀上，合法）")
} else {
  $checks.Add("C.输出: 该程序集没有 AssemblyRef 表")
}
$checks.Add("   判定依据: 表 0x0F ClassLayout rows=$(if ($rows.ContainsKey(0x0F)) { $rows[0x0F] } else { 'absent' });" +
  " 0x1C ImplMap rows=$(if ($rows.ContainsKey(0x1C)) { $rows[0x1C] } else { 'absent' });" +
  " 0x25 $(if ($rows.ContainsKey(0x25)) { $rows[0x25] } else { 'absent' });" +
  " 0x28 $(if ($rows.ContainsKey(0x28)) { $rows[0x28] } else { 'absent' });" +
  " 0x29 $(if ($rows.ContainsKey(0x29)) { $rows[0x29] } else { 'absent' });" +
  " 0x2B $(if ($rows.ContainsKey(0x2B)) { $rows[0x2B] } else { 'absent' })")

if ($MatchName) { $refs = @($refs | Where-Object { $_.name -like $MatchName }) }
$refs = @($refs | Sort-Object name)

if ($ShowChecks) { foreach ($c in $checks) { [Console]::Error.WriteLine("[自检] $c") } }

if ($AsJson) {
  $obj = [pscustomobject]@{
    path             = (Resolve-Path $Path).Path
    assemblyName     = $assemblyName
    assemblyVersion  = $assemblyVersion
    selfCheck        = if ($NoSelfCheck) { 'off' } else { 'on' }
    refCount         = $refs.Count
    refs             = $refs
    checks           = $checks
  }
  $obj | ConvertTo-Json -Depth 5
} else {
  Write-Output "=== AssemblyRef of $(Split-Path $Path -Leaf)  (selfCheck=$(if ($NoSelfCheck) { 'off' } else { 'on' })) ==="
  Write-Output ("本程序集: {0} {1}" -f $assemblyName, $assemblyVersion)
  Write-Output ("AssemblyRef count = {0}" -f $refs.Count)
  foreach ($e in $refs) { Write-Output ("  {0,-50} {1}" -f $e.name, $e.version) }
}
