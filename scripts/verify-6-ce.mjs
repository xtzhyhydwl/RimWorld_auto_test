#!/usr/bin/env node
/**
 * 阶段 6 验证：Character Editor 的 agent 驱动。
 *
 * 用 src/ce.js 把 CE 当成可编程接口用：
 *   开 CE → 列小人 → 选中小人 → 读面板 → 改技能 → 回读确认 → 切标签页 → 截图
 */
import fs from 'node:fs';
import path from 'node:path';
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor, WS } from '../src/launcher.js';
import * as ce from '../src/ce.js';

const stamp = new Date().toISOString().replace(/[:.]/g, '-');
const outDir = path.join(WS, 'runs', `verify-ce-${stamp}`);
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
let shotN = 0;
async function shot(label) {
  shotN += 1;
  const r = await call('rimworld/take_screenshot', { suppressMessage: true, fileName: `cev_${String(shotN).padStart(2, '0')}_${label}` });
  if (r?.path && fs.existsSync(r.path)) fs.copyFileSync(r.path, path.join(outDir, path.basename(r.path)));
  return r?.path ?? null;
}

// ── 准备 ─────────────────────────────────────────────────────
const st = (await call('rimbridge/get_bridge_status')).state;
if (!st.hasCurrentGame) {
  await call('rimworld/start_debug_game_ready', { readiness: 'mapData', pauseIfNeeded: true, timeoutMs: 300_000 }, { timeoutMs: 330_000 });
}
await call('rimbridge/wait_for_game_loaded', { readiness: 'currentMap', pauseIfNeeded: true, timeoutMs: 180_000 }, { timeoutMs: 210_000 });
await call('rimworld/pause_game', { pause: true });

const colonists = (await call('rimworld/list_colonists', { currentMapOnly: true })).colonists ?? [];
log(`殖民者: ${colonists.map((c) => c.name).join(', ')}\n`);

// ── 1. 打开 CE ───────────────────────────────────────────────
log('═══ 1. 打开 Character Editor ═══');
await ce.ensureOpen(client, { timeoutMs: 15_000 });
const desc = await ce.describe(client);
log(`  窗口 rect=${JSON.stringify(desc.rect)}  元素 ${desc.elementCount} / 可点 ${desc.actionableCount}`);
record('CE 窗口成功打开', desc.open);
record('CE 列出了殖民者', desc.pawns.length > 0, `列表: ${desc.pawns.join(', ')}`);
log(`  自带文字的可点控件: ${desc.clickableLabels.join(' / ')}`);

// ── 2. 读当前面板 ────────────────────────────────────────────
log('\n═══ 2. 读 Bio 面板 ═══');
const rows = desc.rows;
log(`  ${rows.length} 行文字，摘录:`);
for (const r of rows.slice(0, 24)) log(`    | ${r.text.slice(0, 110)}`);
record('能读出面板内容', rows.length > 5, `${rows.length} 行`);

// ── 3. 选中小人 ──────────────────────────────────────────────
log('\n═══ 3. 选中小人 ═══');
const targetName = colonists[0].name;
const picked = await ce.selectPawn(client, targetName);
log(`  已选中: ${picked.name} (${picked.targetId})`);
record(`能在 CE 里选中 ${targetName}`, Boolean(picked));
const afterSelect = await ce.describe(client);
log(`  选中后面板 ${afterSelect.rows.length} 行`);
const nameRow = afterSelect.rows.find((r) => /Human, Colonist|Backstory|Traits/i.test(r.text));
if (nameRow) log(`    | ${nameRow.text.slice(0, 140)}`);

// ── 4. 改技能并回读 ──────────────────────────────────────────
log('\n═══ 4. 用 CE 改技能（这是关键验证） ═══');
const skill = 'Shooting';
const before = await ce.getSkillLevel(client, skill);
log(`  ${skill} 改前: ${JSON.stringify(before)}`);
try {
  const r = await ce.bumpSkill(client, skill, +3);
  log(`  点击了 ${r.clicked} 次「+」（targetId=${r.targetId}）`);
  await new Promise((x) => setTimeout(x, 700));
  const after = await ce.getSkillLevel(client, skill);
  log(`  ${skill} 改后: ${JSON.stringify(after)}`);
  const changed = JSON.stringify(before) !== JSON.stringify(after);
  record(`CE 里改 ${skill} 生效（数值变化）`, changed, changed ? '' : `前后都是 ${JSON.stringify(before)}`);
  record('技能等级提升了', (after.level ?? -1) > (before.level ?? -1), `${before.level} → ${after.level}`);
} catch (e) {
  record(`CE 里改 ${skill}`, false, e.message);
}
await shot('after_bump');

// ── 5. 切换标签页 ────────────────────────────────────────────
log('\n═══ 5. 切换标签页 ═══');
for (const tab of ['Health', 'Needs', 'Info', 'Bio']) {
  try {
    await ce.openTab(client, tab);
    const d = await ce.describe(client);
    const sample = d.rows.slice(0, 8).map((r) => r.text).join(' / ');
    log(`  ${tab}: ${d.rows.length} 行  | ${sample.slice(0, 130)}`);
    record(`切换到 ${tab} 标签页`, d.rows.length > 0);
    if (tab === 'Health') await shot('health_tab');
  } catch (e) {
    record(`切换到 ${tab}`, false, e.message);
  }
}

// ── 6. 回到 Bio，看改完的样子 ────────────────────────────────
log('\n═══ 6. 最终状态 ═══');
await ce.openTab(client, 'Bio');
await new Promise((r) => setTimeout(r, 500));
const final = await ce.describe(client);
const lvl = await ce.getSkillLevel(client, skill);
log(`  ${skill} 最终: ${JSON.stringify(lvl)}`);
const finalShot = await shot('final');
log(`  截图: ${finalShot}`);

// 存一份完整描述
fs.writeFileSync(path.join(outDir, 'ce-describe.json'), JSON.stringify(final, null, 2), 'utf8');

client.close();
const passed = results.filter((r) => r.ok).length;
log(`\n${'═'.repeat(62)}`);
log(`阶段 6（Character Editor）：${passed}/${results.length} 通过`);
for (const r of results.filter((x) => !x.ok)) log(`  ✗ ${r.name} — ${r.detail}`);
log('═'.repeat(62));
fs.writeFileSync(path.join(outDir, 'result.json'), JSON.stringify({ stamp, results, before, final }, null, 2), 'utf8');
log(`报告: ${path.join(outDir, 'result.json')}`);
