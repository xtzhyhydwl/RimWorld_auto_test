#!/usr/bin/env node
/**
 * 编译 FoodControl 的验证探针（RimBridgeServer 伴生 DLL）。
 *
 * 为什么探针要单独一个 DLL、而不是塞进 mod：
 *   - 伴生 DLL 放游戏 `BridgeTools/`，**不污染 mod 本体**（mod 还是要能干净发布的）
 *   - 探针用**反射**调 mod，不硬引用 mod 程序集 → mod 没加载时能给出清楚诊断，
 *     而不是整个 DLL 加载失败
 *
 * 流程刻意分两步，因为一步失败代价很高：
 *   1. **编译**（写工作区，便宜）
 *   2. **部署**（拷到游戏 BridgeTools/，可逆）
 *   3. 重启游戏（约 12 秒 + 重开殖民地，**贵**）
 * 所以编译这一步同时会编 `CompileProbe.cs` —— 编得过就证明探针里用到的
 * 每一个游戏 API 都真实存在且签名对得上，不至于白重启一轮。
 *
 * 用法：
 *   node scripts/build-probe.mjs            # 编译（不部署）
 *   node scripts/build-probe.mjs --deploy   # 编译 + 部署到 BridgeTools
 */
import fs from 'node:fs';
import path from 'node:path';
import { gameRoot, WS } from '../src/launcher.js';
import { capture } from '../src/proc.js';

const PROJ = path.join(WS, 'bridge', 'DSH.FoodProbe');
const OUT_DIR = path.join(WS, 'tmp', 'foodprobe');
const DLL = path.join(OUT_DIR, 'DSH.FoodProbe.dll');
const DEPLOY_DIR = path.join(gameRoot(), 'BridgeTools', 'DSH.FoodProbe');

const ROSLYN = [
  'C:\\Program Files (x86)\\Microsoft Visual Studio\\2022\\BuildTools\\MSBuild\\Current\\Bin\\Roslyn\\csc.exe',
  'C:\\Program Files\\Microsoft Visual Studio\\2022\\BuildTools\\MSBuild\\Current\\Bin\\Roslyn\\csc.exe',
];

function findRoslyn() {
  for (const p of ROSLYN) if (fs.existsSync(p)) return p;
  throw new Error('找不到 VS BuildTools 的 Roslyn csc.exe');
}

async function build() {
  fs.mkdirSync(OUT_DIR, { recursive: true });
  const csc = findRoslyn();
  const managed = path.join(gameRoot(), 'RimWorldWin64_Data', 'Managed');
  const rbs = path.join(gameRoot(), 'Mods', 'RimBridgeServer', '1.6', 'Assemblies');

  const refs = [
    path.join(managed, 'mscorlib.dll'),
    path.join(managed, 'System.dll'),
    path.join(managed, 'System.Core.dll'),
    path.join(managed, 'netstandard.dll'),
    path.join(managed, 'UnityEngine.CoreModule.dll'),
    path.join(managed, 'Assembly-CSharp.dll'),
    path.join(rbs, 'RimBridgeServer.Sdk.dll'),
    path.join(rbs, 'RimBridgeServer.Extensions.Abstractions.dll'),
    path.join(rbs, 'RimBridgeServer.Contracts.dll'),
  ];

  const missing = refs.filter((r) => !fs.existsSync(r));
  if (missing.length) throw new Error('缺少引用程序集：\n  ' + missing.join('\n  '));

  const sources = fs
    .readdirSync(PROJ)
    .filter((f) => f.endsWith('.cs'))
    .map((f) => path.join(PROJ, f))
    .sort();

  const args = [
    '/nologo',
    '/target:library',
    '/nostdlib+',
    '/langversion:7.3',
    '/optimize+',
    '/deterministic+',
    `/out:${DLL}`,
    ...refs.map((r) => `/reference:${r}`),
    ...sources,
  ];

  // ⚠ 刻意**不加** /main:CompileProbe —— `csc` 不允许 `library` 配 `/main`。
  // 不需要它：库编译会把 `DSH.FoodProbe.cs` 与 `CompileProbe.cs` 一起编，
  // 两者都用到了全部游戏 API，**编得过就等于签名都对**。

  console.log(`编译器: ${csc}`);
  console.log(`源文件: ${sources.length} 个`);
  for (const s of sources) console.log('  ' + path.relative(WS, s));
  console.log(`输出:   ${path.relative(WS, DLL)}`);
  console.log('（含 CompileProbe.cs：编得过 = 探针用到的游戏 API 签名都对）\n');

  const res = await capture(csc, args, { timeoutMs: 240_000 });
  if (res.stdout.trim()) console.log(res.stdout.trim());
  if (res.stderr.trim()) console.error(res.stderr.trim());

  if (res.code !== 0 || !fs.existsSync(DLL)) {
    throw new Error(`编译失败（退出码 ${res.code}）—— 不要部署`);
  }
  console.log(`\n✓ 编译成功 (${(fs.statSync(DLL).size / 1024).toFixed(1)} KB)`);
  console.log('  → 游戏 API 签名全部有效（编译探针通过）\n');
}

async function deploy() {
  fs.mkdirSync(DEPLOY_DIR, { recursive: true });
  fs.copyFileSync(DLL, path.join(DEPLOY_DIR, 'DSH.FoodProbe.dll'));
  // 一起拷 pdb 便于读堆栈
  const pdb = DLL.replace(/\.dll$/, '.pdb');
  if (fs.existsSync(pdb)) fs.copyFileSync(pdb, path.join(DEPLOY_DIR, 'DSH.FoodProbe.pdb'));
  console.log(`✓ 已部署到 ${DEPLOY_DIR}`);
  console.log('  ⚠ 需要**重启游戏**才会加载伴生 DLL');
}

try {
  await build();
  if (process.argv.includes('--deploy')) await deploy();
} catch (err) {
  console.error('\n[错误] ' + err.message);
  process.exit(1);
}
