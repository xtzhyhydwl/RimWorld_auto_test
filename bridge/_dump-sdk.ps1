# 反射 dump RimBridgeServer.Sdk 的公开 API，用来写 companion DLL
$dir = 'D:\SteamLibrary\steamapps\common\RimWorld\Mods\RimBridgeServer\1.6\Assemblies'

# 让依赖能被解析
$onResolve = [System.ResolveEventHandler] {
  param($s, $e)
  $name = ($e.Name -split ',')[0]
  $p = Join-Path $dir "$name.dll"
  if (Test-Path $p) { return [System.Reflection.Assembly]::LoadFrom($p) }
  return $null
}
[System.AppDomain]::CurrentDomain.add_AssemblyResolve($onResolve)

function Dump-Assembly($path) {
  Write-Output "`n############ $([IO.Path]::GetFileName($path)) ############"
  try { $asm = [Reflection.Assembly]::LoadFrom($path) } catch { Write-Output "  加载失败: $($_.Exception.Message)"; return }
  Write-Output "  版本: $($asm.GetName().Version)"
  $types = $null
  try { $types = $asm.GetTypes() } catch [Reflection.ReflectionTypeLoadException] {
    $types = $_.Exception.Types | Where-Object { $_ -ne $null }
    Write-Output "  （部分类型加载失败，取了 $($types.Count) 个）"
  }
  foreach ($t in $types | Where-Object { $_.IsPublic } | Sort-Object FullName) {
    Write-Output "`n  === $($t.FullName)  ($($t.Attributes -band 0x7)) ==="
    if ($t.IsEnum) {
      Write-Output "    enum: $([string]::Join(', ', [Enum]::GetNames($t)))"
      continue
    }
    foreach ($c in $t.GetConstructors()) {
      $ps = ($c.GetParameters() | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ', '
      Write-Output "    ctor($ps)"
    }
    foreach ($p in $t.GetProperties([Reflection.BindingFlags]'Public,Instance,Static,DeclaredOnly')) {
      Write-Output "    prop $($p.PropertyType.Name) $($p.Name) { $(if($p.CanRead){'get;'})$(if($p.CanWrite){'set;'}) }"
    }
    foreach ($m in $t.GetMethods([Reflection.BindingFlags]'Public,Instance,Static,DeclaredOnly') | Where-Object { -not $_.IsSpecialName }) {
      $ps = ($m.GetParameters() | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)$(if($_.IsOptional){' = opt'})" }) -join ', '
      Write-Output "    method $($m.ReturnType.Name) $($m.Name)($ps)"
    }
  }
}

foreach ($f in 'RimBridgeServer.Sdk.dll','RimBridgeServer.Extensions.Abstractions.dll') {
  $p = Join-Path $dir $f
  if (Test-Path $p) { Dump-Assembly $p }
}

[System.AppDomain]::CurrentDomain.remove_AssemblyResolve($onResolve)
