#!/usr/bin/env node
/**
 * 跑**探针侧的离线测试**（目前只有 `ParseCellPairs` 的回归测试）。
 *
 * 为什么值得单独一个脚本：探针里的小工具函数也会静默出错，而它的后果是
 * **实验数据本身不可信**（`place_blueprints` 传 2 个坐标只放下 1 个，
 * 还返回 success:true → 上一轮的"建造顺序"实验只跑了一半）。
 *
 * 测试源码在 `bridge/DSH.FoodProbe/tests/`，用 .NET Framework 的 csc 编成 exe 直接跑，
 * **不需要开游戏**（和 `build-core.mjs` 同一个套路）。
 *
 * 用法：node scripts/test-probe-parser.mjs
 */
import fs from 'node:fs';
import path from 'node:path';
import { WS } from '../src/launcher.js';
import { capture } from '../src/proc.js';

const SRC_DIR = path.join(WS, 'bridge', 'DSH.FoodProbe', 'tests');
const OUT_DIR = path.join(WS, 'tmp', 'probe-tests');
const EXE = path.join(OUT_DIR, 'ProbeParserTests.exe');

const ROSLYN_CSC = [
  'C:\\Program Files (x86)\\Microsoft Visual Studio\\2022\\BuildTools\\MSBuild\\Current\\Bin\\Roslyn\\csc.exe',
  'C:\\Program Files\\Microsoft Visual Studio\\2022\\BuildTools\\MSBuild\\Current\\Bin\\Roslyn\\csc.exe',
];
const FW_DIR = 'C:\\WINDOWS\\Microsoft.NET\\Framework64\\v4.0.30319';

function findRoslyn() {
  for (const p of ROSLYN_CSC) if (fs.existsSync(p)) return p;
  throw new Error('找不到 VS BuildTools 的 Roslyn csc.exe');
}

const sources = fs.readdirSync(SRC_DIR).filter((f) => f.endsWith('.cs')).map((f) => path.join(SRC_DIR, f));
if (!sources.length) throw new Error('没有测试源文件于 ' + SRC_DIR);

fs.mkdirSync(OUT_DIR, { recursive: true });

const refs = ['mscorlib.dll', 'System.dll', 'System.Core.dll'].map((f) => path.join(FW_DIR, f));
for (const r of refs) if (!fs.existsSync(r)) throw new Error('缺 .NET Framework 引用：' + r);

// 哪个类有 Main 就编哪个（目前只有一个）
const mainClass = process.env.MAIN_CLASS || 'ProbeParserTests';

const args = [
  '/nologo', '/target:exe', '/nostdlib+', '/langversion:7.3', '/optimize+', '/deterministic+',
  `/main:${mainClass}`, `/out:${EXE}`,
  ...refs.map((r) => `/reference:${r}`),
  ...sources,
];

const csc = findRoslyn();
console.log(`编译器: ${csc}`);
console.log(`源文件: ${sources.map((s) => path.relative(WS, s)).join(', ')}\n`);

const built = await capture(csc, args, { timeoutMs: 180_000 });
if (built.stdout.trim()) console.log(built.stdout.trim());
if (built.stderr.trim()) console.error(built.stderr.trim());
if (built.code !== 0 || !fs.existsSync(EXE)) {
  console.error(`\n✗ 测试代码编译失败（退出码 ${built.code}）`);
  process.exit(1);
}

const res = await capture(EXE, [], { timeoutMs: 60_000 });
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
