#!/usr/bin/env node
/**
 * 阶段 2 验证：Outputs（247 个诊断读取）怎么把内容拿到手。
 *
 * 实测发现 execute_debug_action 执行 Outputs\* 只返回节点元数据，真正的文本在
 * 弹出的窗口里。所以要「执行 → 读 UI 布局 → 取文字 → 关窗」。
 */
import fs from 'node:fs';
import path from 'node:path';
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor, WS } from '../src/launcher.js';
import { layout, describeSurfaces } from '../src/ui.js';

const stamp = new Date().toISOString().replace(/[:.]/g, '-');
const outDir = path.join(WS, 'runs', `verify-outputs-${stamp}`);
fs.mkdirSync(outDir, { recursive: true });

const info = readBridgeInfo(logPathFor('vanilla'));
const { client } = await connectGabp(info, { requestTimeoutMs: 25_000 });
const call = (n, a, o) => client.callTool(n, a, o);
const log = (...a) => console.log(...a);
const results = [];
const record = (name, ok, detail) => {
  results.push({ name, ok, detail: detail ?? null });
  log(`  ${ok ? '✓' : '✗'} ${name}${detail && !ok ? ` — ${detail}` : ''}`);
};

/** 执行一个 Output 动作，然后从弹出的窗口里把文字抠出来 */
async function readOutput(outputPath) {
  await call('rimworld/close_window', {}).catch(() => {});
  const exec = await call('rimworld/execute_debug_action', { path: outputPath }, { timeoutMs: 20_000 });
  // 给它一点时间开窗
  await new Promise((r) => setTimeout(r, 400));
  const snap = await layout(client);
  const surfaces = snap.surfaces ?? [];
  const texts = [];
  for (const s of surfaces) {
    for (const e of s.elements ?? []) {
      if (e.label) texts.push(e.label);
    }
  }
  const uniq = [...new Set(texts)];
  await call('rimworld/close_window', {}).catch(() => {});
  return { exec, surfaces: describeSurfaces(snap), texts: uniq };
}

const st = (await call('rimbridge/get_bridge_status')).state;
log(`游戏状态: ${st.programState} hasCurrentGame=${st.hasCurrentGame}\n`);

// ── 1. 单个 Output 的读取闭环 ─────────────────────────────────
log('═══ 1. 读 Outputs\\Current Threat Points ═══');
const r1 = await readOutput('Outputs\\Current Threat Points');
log(`  执行 success=${r1.exec.success}`);
log(`  界面: ${r1.surfaces.map((s) => `${s.id}(${s.elements})`).join(', ')}`);
log(`  取到 ${r1.texts.length} 行文字：`);
for (const t of r1.texts.slice(0, 20)) log(`    | ${String(t).slice(0, 110)}`);
record('能通过窗口读到 Output 的文字', r1.texts.length > 0, `取到 ${r1.texts.length} 行`);

// ── 2. 批量读几个有代表性的 ──────────────────────────────────
log('\n═══ 2. 批量读取 ═══');
const targets = [
  'Outputs\\Biomes',
  'Outputs\\Weapons Ranged',
  'Outputs\\Research Projects',
  'Outputs\\Raid Strategy Sampled',
  'Outputs\\Quest Selection Weights Now',
  'Outputs\\Incident Chances',
];
const collected = {};
for (const t of targets) {
  try {
    const r = await readOutput(t);
    collected[t] = r.texts;
    log(`  ${t}: ${r.texts.length} 行`);
    for (const x of r.texts.slice(0, 6)) log(`      | ${String(x).slice(0, 100)}`);
  } catch (e) {
    log(`  ${t}: ✗ ${e.message}`);
    collected[t] = null;
    record(`读取 ${t}`, false, e.message);
    break;
  }
}
record(
  `批量读取 ${targets.length} 个 Output`,
  targets.every((t) => (collected[t]?.length ?? 0) > 0),
  targets.filter((t) => !(collected[t]?.length > 0)).join(', '),
);

// ── 3. 确认窗口都关干净了 ────────────────────────────────────
log('\n═══ 3. 确认没有残留窗口 ═══');
await call('rimworld/close_window', {}).catch(() => {});
const snapAfter = await layout(client);
const windows = (snapAfter.surfaces ?? []).filter((s) => s.surfaceKind === 'window');
log(`  剩余窗口 ${windows.length} 个: ${windows.map((w) => w.type).join(', ') || '（无）'}`);
record('调试输出窗口已关闭', windows.length === 0, windows.map((w) => w.type).join(', '));

// ── 4. 顺带验证 Outputs 的总量 ───────────────────────────────
log('\n═══ 4. Outputs 全量清单 ═══');
const outs = await call('rimworld/get_debug_action', { path: 'Outputs', includeChildren: true, includeHiddenChildren: true });
const list = outs.children ?? outs.action?.children ?? [];
log(`  共 ${list.length} 项`);
fs.writeFileSync(
  path.join(outDir, 'outputs-list.json'),
  JSON.stringify(list.map((n) => ({ label: n.label, path: n.path, kind: n.execution?.kind, category: n.category })), null, 2),
  'utf8',
);
record('Outputs 清单已导出', list.length > 100, `${list.length} 项`);

client.close();

const passed = results.filter((r) => r.ok).length;
log(`\n${'═'.repeat(62)}`);
log(`阶段 2：${passed}/${results.length} 通过`);
for (const r of results.filter((x) => !x.ok)) log(`  ✗ ${r.name} — ${r.detail}`);
log('═'.repeat(62));
fs.writeFileSync(
  path.join(outDir, 'result.json'),
  JSON.stringify({ stamp, stage: 'outputs', results, collected }, null, 2),
  'utf8',
);
log(`报告: ${path.join(outDir, 'result.json')}`);
