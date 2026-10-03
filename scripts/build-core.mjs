#!/usr/bin/env node
/**
 * 编译并运行 FoodControl.Core 的**离线**测试。
 *
 * ## 为什么这样设计
 *
 * 本机没有 .NET SDK，但有：
 *   - **Roslyn csc**（VS BuildTools 自带）→ 用来编 mod 本体（引用游戏的 Mono 程序集）
 *   - **.NET Framework 4.8 自带的 Roslyn csc** → 用来编离线测试
 *   - `dotnet`（只有 5.0/6.0 运行时，没有 SDK）
 *
 * 游戏只带 Mono 的**嵌入运行时**（`mono-2.0-bdwgc.dll`），**没有独立的 mono.exe**，
 * 所以「编个 exe 用游戏 mono 跑」这条路走不通（实测走不通）。
 *
 * 关键点：`FoodControl.Core` **刻意不引用 Assembly-CSharp**，
 * 所以同一份源码可以用 .NET Framework 的 csc **另编一份**，
 * 直接在 .NET Framework 上跑，完全不需要游戏运行时。
 *
 * → **决策/预测/选址逻辑可以在毫秒级离线验证，不用开游戏。** 这是本项目可测试性的地基。
 *
 * ⚠ 这意味着 Core 里**不许出现任何游戏类型**（Verse.* / RimWorld.*），
 *   也不许用 C# 8+ 语法。游戏相关的读取/执行放在 Game/ 层。
 *
 * 用法：
 *   node scripts/build-core.mjs          # 编译 + 跑测试
 *   node scripts/build-core.mjs --build  # 只编译
 *   node scripts/build-core.mjs --test   # 只跑（不重新编译）
 */
import fs from 'node:fs';
import path from 'node:path';
import { WS } from '../src/launcher.js';
import { capture } from '../src/proc.js';

const CORE_DIR = path.join(WS, 'src', 'foodcontrol-core');
const OUT_DIR = path.join(WS, 'tmp', 'foodcontrol-core');
const EXE = path.join(OUT_DIR, 'CoreTests.exe');

/**
 * 编译器选择。
 *
 * ⚠ 踩过的坑：
 *   - `C:\WINDOWS\Microsoft.NET\Framework64\v4.0.30319\csc.exe` **只支持 C# 5**
 *     （它的版本号 4.8.9221.0 是 .NET Framework 的版本，不是 Roslyn 的版本，很容易看串）。
 *   - 所以离线测试也必须用 **VS BuildTools 的 Roslyn (4.14)**，
 *     但把 `/nostdlib+` 指向 **.NET Framework 的 mscorlib**，而不是游戏那份。
 *
 * 这样同一份 Core 源码编两次：
 *   - 离线测试 → .NET Framework mscorlib（可在本机直接跑）
 *   - mod 本体 → 游戏的 Mono mscorlib（见 scripts/build-mod.mjs）
 */
const ROSLYN_CSC = [
  'C:\\Program Files (x86)\\Microsoft Visual Studio\\2022\\BuildTools\\MSBuild\\Current\\Bin\\Roslyn\\csc.exe',
  'C:\\Program Files\\Microsoft Visual Studio\\2022\\BuildTools\\MSBuild\\Current\\Bin\\Roslyn\\csc.exe',
  'C:\\Program Files (x86)\\Microsoft Visual Studio\\2019\\BuildTools\\MSBuild\\Current\\Bin\\Roslyn\\csc.exe',
];

/** .NET Framework 的**运行时**程序集（本机无 Reference Assemblies 包，实测） */
const FW_DIR = 'C:\\WINDOWS\\Microsoft.NET\\Framework64\\v4.0.30319';

function findRoslyn() {
  for (const p of ROSLYN_CSC) if (fs.existsSync(p)) return p;
  throw new Error('找不到 VS BuildTools 的 Roslyn csc.exe');
}

function frameworkRefs() {
  const refs = ['mscorlib.dll', 'System.dll', 'System.Core.dll'].map((f) => path.join(FW_DIR, f));
  const missing = refs.filter((r) => !fs.existsSync(r));
  if (missing.length) throw new Error('.NET Framework 引用程序集缺失：\n  ' + missing.join('\n  '));
  return refs;
}

function listSources() {
  const out = [];
  for (const f of fs.readdirSync(CORE_DIR)) {
    if (f.endsWith('.cs')) out.push(path.join(CORE_DIR, f));
  }
  const testDir = path.join(CORE_DIR, 'tests');
  if (fs.existsSync(testDir)) {
    for (const f of fs.readdirSync(testDir)) {
      if (f.endsWith('.cs')) out.push(path.join(testDir, f));
    }
  }
  return out.sort();
}

/** 静态检查：Core 不许引用游戏类型 —— 这是"可离线测试"的前提，违反了就报错 */
function assertNoGameTypes(sources) {
  const banned = /\b(Verse|RimWorld|UnityEngine|HarmonyLib)\s*\./;
  const offenders = [];
  for (const s of sources) {
    if (s.includes(path.sep + 'tests' + path.sep)) continue;
    const text = fs.readFileSync(s, 'utf8');
    const lines = text.split(/\r?\n/);
    for (let i = 0; i < lines.length; i++) {
      const line = lines[i];
      // 注释里提到是允许的（我们在注释里大量解释游戏机制）
      const code = line.replace(/\/\/.*$/, '').replace(/\/\*.*?\*\//g, '');
      if (banned.test(code)) {
        offenders.push(`${path.relative(WS, s)}:${i + 1}  ${line.trim()}`);
      }
    }
  }
  return offenders;
}

async function build() {
  fs.mkdirSync(OUT_DIR, { recursive: true });
  const csc = findRoslyn();
  const refs = frameworkRefs();
  const sources = listSources();
  if (!sources.length) throw new Error('没有找到任何 .cs 源文件于 ' + CORE_DIR);

  const offenders = assertNoGameTypes(sources);
  if (offenders.length) {
    console.error('\n✗ Core 里出现了游戏类型引用 —— 这会破坏离线可测试性：\n');
    for (const o of offenders) console.error('  ' + o);
    console.error('\n  游戏相关的读取/执行请放到 Game/ 层。\n');
    process.exit(1);
  }

  const args = [
    '/nologo',
    '/target:exe',
    '/nostdlib+',
    '/langversion:7.3',
    '/optimize+',
    '/deterministic+',
    '/main:CoreTests',
    `/out:${EXE}`,
    ...refs.map((r) => `/reference:${r}`),
    ...sources,
  ];

  console.log(`编译器: ${csc}  (Roslyn)`);
  console.log(`目标运行时: .NET Framework (${FW_DIR})`);
  console.log(`源文件: ${sources.length} 个`);
  for (const s of sources) console.log('  ' + path.relative(WS, s));
  console.log(`输出:   ${path.relative(WS, EXE)}`);
  console.log('静态检查: Core 无游戏类型引用 ✓\n');

  const res = await capture(csc, args, { timeoutMs: 180_000 });
  if (res.stdout.trim()) console.log(res.stdout.trim());
  if (res.stderr.trim()) console.error(res.stderr.trim());

  if (res.code !== 0 || !fs.existsSync(EXE)) {
    throw new Error(`编译失败（退出码 ${res.code}）`);
  }
  console.log(`\n✓ 编译成功 (${(fs.statSync(EXE).size / 1024).toFixed(1)} KB)\n`);
}

async function runTests() {
  if (!fs.existsSync(EXE)) throw new Error('还没编译，先跑一次不带 --test 的');
  // 直接跑 exe；.NET Framework 是系统自带的，不需要额外运行时
  const res = await capture(EXE, [], { timeoutMs: 120_000 });
  const out = (res.stdout || '') + (res.stderr || '');
  console.log(out.trim());

  const m = /=== 通过 (\d+) \/ 失败 (\d+) ===/.exec(out);
  if (!m) {
    console.error('\n✗ 没解析到测试结果 —— 可能崩了');
    process.exit(1);
  }
  const pass = Number(m[1]);
  const fail = Number(m[2]);
  console.log(`\n总结：通过 ${pass}，失败 ${fail}，退出码 ${res.code}`);
  process.exit(fail === 0 && res.code === 0 ? 0 : 1);
}

const only = process.argv[2];
try {
  if (only !== '--test') await build();
  if (only !== '--build') await runTests();
} catch (err) {
  console.error('\n[错误] ' + err.message);
  process.exit(1);
}
