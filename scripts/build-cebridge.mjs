#!/usr/bin/env node
/**
 * 编译 RimBridgeServer companion DLL。
 *
 * 用 Visual Studio BuildTools 自带的 Roslyn csc.exe 直接编译，
 * 引用游戏自己的 Managed 程序集（而不是目标框架包），保证和游戏 Mono 运行时 ABI 一致。
 * 这样不需要安装 .NET SDK，也不需要 MSBuild。
 */
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { gameRoot, WS } from '../src/launcher.js';
// 不能在沙箱里用管道 stdio（会 spawnSync EPERM），用仓库自己的无管道执行器
import { capture } from '../src/proc.js';

const CSC_CANDIDATES = [
  'C:\\Program Files (x86)\\Microsoft Visual Studio\\2022\\BuildTools\\MSBuild\\Current\\Bin\\Roslyn\\csc.exe',
  'C:\\Program Files\\Microsoft Visual Studio\\2022\\BuildTools\\MSBuild\\Current\\Bin\\Roslyn\\csc.exe',
  'C:\\Program Files (x86)\\Microsoft Visual Studio\\2019\\BuildTools\\MSBuild\\Current\\Bin\\Roslyn\\csc.exe',
  'C:\\WINDOWS\\Microsoft.NET\\Framework64\\v4.0.30319\\csc.exe',
];

function findCsc() {
  for (const p of CSC_CANDIDATES) if (fs.existsSync(p)) return p;
  throw new Error('找不到 csc.exe。装一个 Visual Studio BuildTools 或 .NET SDK 即可。');
}

const PROJ = path.join(WS, 'bridge', 'DSH.CEBridge');
const SRC = path.join(PROJ, 'DSH.CEBridge.cs');
const OUT = path.join(PROJ, 'DSH.CEBridge.dll');

const managed = path.join(gameRoot(), 'RimWorldWin64_Data', 'Managed');
const rbsAsm = path.join(gameRoot(), 'Mods', 'RimBridgeServer', '1.6', 'Assemblies');

// 引用游戏自己的程序集，保证与运行时一致
const REFS = [
  path.join(managed, 'mscorlib.dll'),
  path.join(managed, 'System.dll'),
  path.join(managed, 'System.Core.dll'),
  path.join(managed, 'netstandard.dll'),
  path.join(managed, 'UnityEngine.CoreModule.dll'),
  path.join(managed, 'Assembly-CSharp.dll'),
  path.join(rbsAsm, 'RimBridgeServer.Sdk.dll'),
  path.join(rbsAsm, 'RimBridgeServer.Extensions.Abstractions.dll'),
  path.join(rbsAsm, 'RimBridgeServer.Contracts.dll'),
];

const missing = REFS.filter((r) => !fs.existsSync(r));
if (missing.length) {
  console.error('缺少引用程序集：');
  for (const m of missing) console.error('  ' + m);
  process.exit(1);
}

const csc = findCsc();
const args = [
  '/nologo',
  '/target:library',
  '/nostdlib+',
  '/langversion:7.3',
  '/optimize+',
  '/deterministic+',
  `/out:${OUT}`,
  ...REFS.map((r) => `/reference:${r}`),
  SRC,
];

console.log(`编译器: ${csc}`);
console.log(`源文件: ${SRC}`);
console.log(`输出:   ${OUT}`);
console.log(`引用 ${REFS.length} 个程序集\n`);

const res = await capture(csc, args, { timeoutMs: 240_000 });
if (res.stdout.trim()) console.log(res.stdout.trim());
if (res.stderr.trim()) console.error(res.stderr.trim());

if (res.code === 0 && fs.existsSync(OUT)) {
  const size = fs.statSync(OUT).size;
  console.log(`\n✓ 编译成功：${OUT}  (${(size / 1024).toFixed(1)} KB)`);
} else {
  console.error(`\n✗ 编译失败（退出码 ${res.code}）`);
  process.exit(1);
}
