<#
.SYNOPSIS
  互校：把 Get-AssemblyRef.ps1 的字节级解析结果，与 .NET 自己读出的结果逐行对比。

.DESCRIPTION
  为什么要有它：本工程出过的三类事故都是"工具在受限环境下静默给出假结论"，
  其中一类就是**自写解析器算错而不报错**（ClassLayout 行尺寸错 → 读出乱码）。
  自写解析器必须有**第二个独立实现**兜底，否则"看起来对"就是它唯一的验收标准。

  这里的"第二实现"是 .NET Framework 的 `Assembly.ReflectionOnlyLoadFrom()` +
  `GetReferencedAssemblies()`（CLR 自己的元数据读取器，只读元数据、不执行代码、
  不解析依赖），运行在 Windows PowerShell 5.1 上。两条路径的实现完全不同：
    - Get-AssemblyRef.ps1：手算 PE/CLI/元数据偏移 + 手算行尺寸表
    - 本脚本：CLR 的 AssemblyName / GetReferencedAssemblies
  两者给出的 (名字, 版本) 集合必须**完全一致**（个数也要一致）。

  已知边界（明确写出，别把它当万能）：
    - `ReflectionOnlyLoadFrom` 读不了的（原生 DLL、被裁剪/加壳的元数据）→ 记为 SKIP，
      不算通过；SKIP 的文件只能靠 Get-AssemblyRef.ps1 自己的三道自检。
    - 顺序不保证一致（这里统一排序后比对），因为 AssemblyRef 表的物理顺序不参与结论。

.PARAMETER Path
  要互校的程序集路径。可以多次传（例：`-Path a.dll -Path b.dll`），
  或用 -ListFile 传一个每行一个路径的文本文件。

.PARAMETER ListFile
  文本文件，每行一个程序集路径（`#` 开头的行忽略）。

.PARAMETER Quiet
  只打印不通过的。

.OUTPUTS
  每行 `OK|MISMATCH|SKIP|READER-FAIL <文件名>`；全部 OK 时退出码 0，否则 1。

.EXAMPLE
  powershell.exe -NoProfile -File tools\asmref\Verify-VsReflection.ps1 -Path tmp\foodcontrol-game\FoodControl.dll

.NOTES
  2026-10-13 创建。用法与证据见同目录 README.md。
#>
[CmdletBinding()]
param(
  [string[]]$Path = @(),
  [string]$ListFile = '',
  [string]$Reader = '',
  [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
# 注意：Windows PowerShell 5.1 在 `-File` 模式下求 param() 默认值时 $PSScriptRoot 还是空的
# （2026-10-13 实测：Join-Path 直接报 "Path is an empty string"），所以放在脚本体里算。
if (-not $Reader) { $Reader = Join-Path $PSScriptRoot 'Get-AssemblyRef.ps1' }

$targets = New-Object System.Collections.Generic.List[string]
foreach ($p in $Path) { if ($p) { $targets.Add($p) } }
if ($ListFile) {
  foreach ($line in (Get-Content $ListFile)) {
    $t = $line.Trim()
    if ($t -and -not $t.StartsWith('#')) { $targets.Add($t) }
  }
}
if ($targets.Count -eq 0) { throw '至少要给一个 -Path 或 -ListFile。' }
if (-not (Test-Path $Reader)) { throw "找不到读取器：$Reader" }

$bad = 0
$skipped = 0
foreach ($t0 in $targets) {
  $file = (Resolve-Path $t0).Path
  $name = Split-Path $file -Leaf

  # 第二实现：CLR 自己的元数据读取器
  $oracleRefs = $null
  try {
    $asm = [System.Reflection.Assembly]::ReflectionOnlyLoadFrom($file)
    $oracleRefs = @($asm.GetReferencedAssemblies() | ForEach-Object {
        [pscustomobject]@{ name = $_.Name; version = $_.Version.ToString() }
      } | Sort-Object name, version)
  } catch {
    $skipped++
    if (-not $Quiet) { Write-Output ("SKIP        {0}   （CLR 读不了：{1}）" -f $name, $_.Exception.Message.Split("`n")[0]) }
    continue
  }

  # 第一实现：字节级解析（它自己的自检也在里面，失败会 throw）
  try {
    $json = & $Reader -Path $file -AsJson | Out-String
    $mine = $json | ConvertFrom-Json
  } catch {
    $bad++
    Write-Output ("READER-FAIL {0}   （{1}）" -f $name, $_.Exception.Message.Split("`n")[0])
    continue
  }
  $mineRefs = @($mine.refs | ForEach-Object { [pscustomobject]@{ name = $_.name; version = $_.version } } | Sort-Object name, version)

  $diff = New-Object System.Collections.Generic.List[string]
  if ($mineRefs.Count -ne $oracleRefs.Count) {
    $diff.Add(("条数不同：字节解析 {0} vs CLR {1}" -f $mineRefs.Count, $oracleRefs.Count))
  }
  $max = [Math]::Max($mineRefs.Count, $oracleRefs.Count)
  for ($i = 0; $i -lt $max; $i++) {
    $a = if ($i -lt $mineRefs.Count) { $mineRefs[$i] } else { $null }
    $b = if ($i -lt $oracleRefs.Count) { $oracleRefs[$i] } else { $null }
    if ($null -eq $a -or $null -eq $b) { $diff.Add("第 $($i + 1) 行只在一侧存在"); continue }
    if ($a.name -ne $b.name -or $a.version -ne $b.version) {
      $diff.Add(("第 {0} 行：字节解析='{1} {2}'  CLR='{3} {4}'" -f ($i + 1), $a.name, $a.version, $b.name, $b.version))
    }
  }

  # 本程序集自身也对比一次（锚点）
  $asmNameFromReader = $mine.assemblyName
  $asmNameOracle = $null
  try { $asmNameOracle = [System.Reflection.AssemblyName]::GetAssemblyName($file).Name } catch { }
  if ($asmNameOracle -and $asmNameFromReader -ne $asmNameOracle) {
    $diff.Add("本程序集名不同：字节解析='$asmNameFromReader'  CLR='$asmNameOracle'")
  }

  if ($diff.Count -eq 0) {
    if (-not $Quiet) { Write-Output ("OK          {0}   （{1} 条引用逐条一致；本程序集 {2} {3}）" -f $name, $mineRefs.Count, $mine.assemblyName, $mine.assemblyVersion) }
  } else {
    $bad++
    Write-Output ("MISMATCH    {0}" -f $name)
    foreach ($d in $diff) { Write-Output ("              $d") }
  }
}

Write-Output ("—— 互校结束：不一致 $bad 个，CLR 读不了而跳过 $skipped 个，共 $($targets.Count) 个 ——")
if ($bad -gt 0) { exit 1 }
