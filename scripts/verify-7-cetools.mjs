#!/usr/bin/env node
/**
 * 阶段 7 验证：我们自己写的 Character Editor 伴生工具（ce/*）。
 *
 * 这组工具走的是反射直接调 CE 内部数据 API，**完全绕过 UI** ——
 * 补上了 UI 路线的两个硬伤（滑块点不到、没有文字输入工具）。
 */
import fs from 'node:fs';
import path from 'node:path';
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor, WS } from '../src/launcher.js';

const stamp = new Date().toISOString().replace(/[:.]/g, '-');
const outDir = path.join(WS, 'runs', `verify-cetools-${stamp}`);
fs.mkdirSync(outDir, { recursive: true });

const info = readBridgeInfo(logPathFor('vanilla'));
const { client } = await connectGabp(info, { requestTimeoutMs: 30_000 });
const call = (n, a, o) => client.callTool(n, a, o);
const log = (...a) => console.log(...a);
const results = [];
const record = (name, ok, detail) => {
  results.push({ name, ok, detail: detail ?? null });
  log(`  ${ok ? '✓' : '✗'} ${name}${detail && !ok ? ` — ${detail}` : ''}`);
};

// ── 准备 ─────────────────────────────────────────────────────
const st = (await call('rimbridge/get_bridge_status')).state;
if (!st.hasCurrentGame) {
  log('开调试殖民地…');
  await call('rimworld/start_debug_game_ready', { readiness: 'mapData', pauseIfNeeded: true, timeoutMs: 300_000 }, { timeoutMs: 330_000 });
}
await call('rimbridge/wait_for_game_loaded', { readiness: 'currentMap', pauseIfNeeded: true, timeoutMs: 180_000 }, { timeoutMs: 210_000 });
await call('rimworld/pause_game', { pause: true });

// ── 1. ce/colonists ──────────────────────────────────────────
log('═══ 1. ce/colonists ═══');
const cols = await call('ce/colonists');
log(`  ${JSON.stringify(cols).slice(0, 400)}`);
record('ce/colonists 返回殖民者列表', cols.success === true && cols.count > 0, JSON.stringify(cols).slice(0, 150));

const target = cols.colonists?.[0];
if (!target) {
  log('没有殖民者，无法继续');
  client.close();
  process.exit(1);
}
log(`  测试对象: ${target.fullName} (${target.pawnId})`);

// ── 2. ce/get_skills —— 读出 CE 的原生技能串 ─────────────────
log('\n═══ 2. ce/get_skills（读 CE 的原始技能串） ═══');
const before = await call('ce/get_skills', { pawnId: target.pawnId });
log(`  success=${before.success}  pawn=${before.pawn}  rawLength=${before.rawLength}`);
log(`  ★ 原始格式：`);
log(`    ${JSON.stringify(before.raw)}`);
log(`  按 | 拆分: ${JSON.stringify(before.splitByPipe)}`);
record('ce/get_skills 能读到技能串', before.success === true && before.rawLength > 0, JSON.stringify(before).slice(0, 200));

fs.writeFileSync(path.join(outDir, 'skills-before.json'), JSON.stringify(before, null, 2), 'utf8');

// ── 3. 改技能串并写回 ────────────────────────────────────────
log('\n═══ 3. ce/set_skills（改完写回） ═══');
const raw = String(before.raw ?? '');
// 在原始串里把 Shooting 后面的数字 +3（不改变格式，只改数值）
let modified = raw;
let found = null;
const m = /(Shooting[^0-9]*)(\d+)/.exec(raw);
if (m) {
  const oldV = Number(m[2]);
  const newV = oldV + 3;
  modified = raw.slice(0, m.index) + m[1] + String(newV) + raw.slice(m.index + m[0].length);
  found = { oldV, newV };
  log(`  在串里找到 Shooting=${oldV}，改成 ${newV}`);
  log(`  改后串: ${JSON.stringify(modified)}`);
} else {
  log(`  ⚠ 没能在串里定位 Shooting 的数字，改为整体替换测试`);
}

if (found) {
  const wrote = await call('ce/set_skills', { skills: modified, pawnId: target.pawnId });
  log(`  success=${wrote.success}  ${JSON.stringify(wrote).slice(0, 300)}`);
  record('ce/set_skills 写入成功', wrote.success === true);

  await new Promise((r) => setTimeout(r, 400));
  const after = await call('ce/get_skills', { pawnId: target.pawnId });
  log(`  回读: ${JSON.stringify(after.raw)}`);
  record('回读确认技能串已变', after.raw === modified, `期望 ${modified}，实际 ${after.raw}`);
  record(
    `Shooting ${found.oldV} → ${found.newV} 已生效`,
    new RegExp(`Shooting[^0-9]*${found.newV}`).test(String(after.raw ?? '')),
    String(after.raw),
  );
  fs.writeFileSync(path.join(outDir, 'skills-after.json'), JSON.stringify(after, null, 2), 'utf8');
}

// ── 4. ce/get_traits ─────────────────────────────────────────
log('\n═══ 4. ce/get_traits ═══');
const traits = await call('ce/get_traits', { pawnId: target.pawnId });
log(`  ${JSON.stringify(traits).slice(0, 500)}`);
record('ce/get_traits 有响应', traits !== null && traits !== undefined);

// ── 5. 交叉验证：用 CE 的 UI 读同一个技能 ────────────────────
log('\n═══ 5. 交叉验证：CE 的 UI 面板里 Shooting 是多少 ═══');
try {
  const ce = await import('../src/ce.js');
  await ce.ensureOpen(client, { timeoutMs: 15_000 });
  await ce.openTab(client, 'Bio');
  await ce.selectPawn(client, target.name);
  const uiLevel = await ce.getSkillLevel(client, 'Shooting');
  log(`  UI 面板读到: ${JSON.stringify(uiLevel)}`);
  if (found) {
    record(
      `UI 面板与写回值一致（都是 ${found.newV}）`,
      uiLevel.level === found.newV,
      `UI=${uiLevel.level} 写入=${found.newV}`,
    );
  }
  await ce.close(client);
} catch (e) {
  record('CE UI 交叉验证', false, e.message);
}

// ── 6. 确认没把 UI 路线弄坏 ──────────────────────────────────
log('\n═══ 6. 最终状态 ═══');
const finalSkills = await call('ce/get_skills', { pawnId: target.pawnId });
log(`  ${target.fullName}: ${finalSkills.raw}`);
const finalCols = (await call('rimworld/list_colonists', { currentMapOnly: true })).colonists ?? [];
log(`  殖民者数 ${finalCols.length}: ${finalCols.map((c) => c.name).join(', ')}`);
record('改动后游戏仍正常（殖民者都还在）', finalCols.length === cols.count, `${cols.count} → ${finalCols.length}`);

client.close();
const passed = results.filter((r) => r.ok).length;
log(`\n${'═'.repeat(62)}`);
log(`阶段 7（ce/* 伴生工具）：${passed}/${results.length} 通过`);
for (const r of results.filter((x) => !x.ok)) log(`  ✗ ${r.name} — ${r.detail}`);
log('═'.repeat(62));
fs.writeFileSync(path.join(outDir, 'result.json'), JSON.stringify({ stamp, results, before, modified: typeof modified !== 'undefined' ? modified : null }, null, 2), 'utf8');
log(`报告: ${path.join(outDir, 'result.json')}`);
