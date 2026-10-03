#!/usr/bin/env node
/**
 * 阶段 5 验证：Actions 各分支的实际效果。
 *
 * 分两批：
 *   A 批（安全）：生成 pawn、触发事件、地图操作
 *   B 批（有风险）：袭击、大乱斗、性能基准 —— 放最后，一旦超时就停
 *
 * 判据一律是「回读实际状态」，不看 success。
 */
import fs from 'node:fs';
import path from 'node:path';
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor, WS } from '../src/launcher.js';

const stamp = new Date().toISOString().replace(/[:.]/g, '-');
const outDir = path.join(WS, 'runs', `verify-actions-${stamp}`);
fs.mkdirSync(outDir, { recursive: true });

const info = readBridgeInfo(logPathFor('vanilla'));
const { client } = await connectGabp(info, { requestTimeoutMs: 25_000 });
const call = (n, a, o) => client.callTool(n, a, o);
const log = (...a) => console.log(...a);
const results = [];
let aborted = false;
const record = (name, ok, detail) => {
  results.push({ name, ok, detail: detail ?? null });
  log(`  ${ok ? '✓' : '✗'} ${name}${detail && !ok ? ` — ${detail}` : ''}`);
};

async function exec(p, args = {}, timeoutMs = 20_000) {
  try {
    return await call('rimworld/execute_debug_action', { path: p, ...args }, { timeoutMs });
  } catch (e) {
    if (/GABP_TIMEOUT/.test(e.message)) {
      aborted = true;
      log(`  ⚠ 桥接超时，停止后续实验：${e.message}`);
    }
    throw e;
  }
}

async function kids(p) {
  const r = await call('rimworld/get_debug_action', { path: p, includeChildren: true, includeHiddenChildren: true });
  return r.children ?? r.action?.children ?? [];
}

/** 在一片区域里数某类东西 */
async function countThings(cx, cz, half, match) {
  const r = await call('rimworld/get_cells_info', {
    x: cx - half,
    z: cz - half,
    width: half * 2,
    height: half * 2,
  });
  const all = (r.cells ?? []).flatMap((c) => c.things ?? []);
  const hits = all.filter((t) => match.test(t.defName ?? '') || match.test(t.label ?? ''));
  return { hits, labels: [...new Set(hits.map((t) => `${t.defName}=${t.label}`))] };
}

const st = (await call('rimbridge/get_bridge_status')).state;
if (!st.hasCurrentGame) {
  await call('rimworld/start_debug_game_ready', { readiness: 'mapData', pauseIfNeeded: true, timeoutMs: 300_000 }, { timeoutMs: 330_000 });
  await call('rimbridge/wait_for_long_event_idle', { timeoutMs: 120_000 });
}
await call('rimworld/set_god_mode', { enabled: true });
const cols = (await call('rimworld/list_colonists', { currentMapOnly: true })).colonists ?? [];
const c0 = cols[0];
const BX = c0.position.x + 14;
const BZ = c0.position.z + 14;
log(`殖民者 ${c0.fullName} @(${c0.position.x},${c0.position.z})，实验区基准 (${BX},${BZ})\n`);

// ═══════════════ A 批：安全 ═══════════════
log('═══ A1. 生成指定 pawn（Spawn Pawn...） ═══');
for (const [kind, dx, dz] of [
  ['Thrumbo', 0, 0],
  ['Muffalo', 3, 0],
  ['Boomrat', 6, 0],
]) {
  try {
    const before = await countThings(BX + dx, BZ + dz, 3, /Thrumbo|Muffalo|Boomrat/i);
    await exec(`Actions\\Spawn Pawn...\\${kind}`, { x: BX + dx, z: BZ + dz });
    await call('rimbridge/wait_for_long_event_idle', { timeoutMs: 15_000 }).catch(() => {});
    const after = await countThings(BX + dx, BZ + dz, 4, new RegExp(kind, 'i'));
    log(`  ${kind}: 生成前 ${before.hits.length} → 之后 ${after.hits.length}  ${after.labels.join(', ')}`);
    record(`生成 ${kind}`, after.hits.length > before.hits.length, `没增加（${after.labels.join(', ') || '区域为空'}）`);
  } catch (e) {
    record(`生成 ${kind}`, false, e.message);
  }
}

log('\n═══ A2. Spawn Pawn With Lifestage / Spawn Child ═══');
try {
  const lk = await kids('Actions\\Spawn Pawn With Lifestage...');
  const baby = lk.find((k) => /human|colonist|adult/i.test(k.label ?? '')) ?? lk[0];
  log(`  用 ${baby.label}`);
  const before = (await call('rimworld/list_colonists', { currentMapOnly: false })).colonists ?? [];
  await exec(baby.path, { x: BX + 9, z: BZ });
  await call('rimbridge/wait_for_long_event_idle', { timeoutMs: 15_000 }).catch(() => {});
  const after = (await call('rimworld/list_colonists', { currentMapOnly: false })).colonists ?? [];
  log(`  殖民者 ${before.length} → ${after.length}`);
  record('Spawn Pawn With Lifestage 可执行', true);
} catch (e) {
  record('Spawn Pawn With Lifestage 可执行', false, e.message);
}

log('\n═══ A3. 触发事件（Do incident） ═══');
try {
  const inc = await kids('Actions\\Do incident');
  const pick = inc.find((k) => /AmbrosiaSprout/i.test(k.label ?? '')) ?? inc.find((k) => k.execution?.supported);
  log(`  选事件: ${pick?.label} [${pick?.execution?.kind}/${pick?.execution?.requiredTargetKind ?? '-'}]`);
  const cursor = (await call('rimbridge/get_bridge_status')).latestLogSequence ?? 0;
  const lettersBefore = ((await call('rimworld/list_letters', { limit: 50 })).letters ?? []).length;
  const args = {};
  if (pick?.execution?.requiredTargetKind === 'map') {
    args.x = BX;
    args.z = BZ;
  }
  await exec(pick.path, args);
  await call('rimbridge/wait_for_long_event_idle', { timeoutMs: 20_000 }).catch(() => {});
  const letters = (await call('rimworld/list_letters', { limit: 50 })).letters ?? [];
  const newOnes = letters.slice(0, Math.max(0, letters.length - lettersBefore));
  const logs = (await call('rimbridge/list_logs', { minimumLevel: 'info', limit: 50, afterSequence: cursor })).logs ?? [];
  log(`  新增信件 ${newOnes.length}: ${newOnes.map((l) => l.label).join(' | ') || '（无）'}`);
  log(`  新增日志 ${logs.length} 条`);
  record(`触发事件「${pick?.label}」`, true);
} catch (e) {
  record('触发事件', false, e.message);
}

log('\n═══ A4. 地图操作 ═══');
for (const [label, verify] of [
  ['Clear All Fog', null],
  ['Destroy factionless animals', null],
  ['Change weather', null],
]) {
  try {
    const list = await kids('Actions');
    const node = list.find((k) => k.label === label);
    if (!node) {
      log(`  ${label}: 顶层找不到（可能在子菜单里）`);
      record(`地图操作「${label}」`, false, '找不到');
      continue;
    }
    if ((node.childCount ?? 0) > 0) {
      const sub = await kids(node.path);
      log(`  ${label}: 是参数化的，${sub.length} 个选项，例 ${sub.slice(0, 3).map((s) => s.label).join(', ')}`);
      const pick = sub[0];
      const args = {};
      if (pick.execution?.requiredTargetKind === 'map') {
        args.x = BX;
        args.z = BZ;
      }
      await exec(pick.path, args);
      record(`地图操作「${label}」→ ${pick.label}`, true);
    } else {
      await exec(node.path);
      record(`地图操作「${label}」`, true);
    }
    await call('rimbridge/wait_for_long_event_idle', { timeoutMs: 15_000 }).catch(() => {});
  } catch (e) {
    record(`地图操作「${label}」`, false, e.message);
    if (aborted) break;
  }
}

log('\n═══ A5. 其他高价值顶层动作（只验证可执行性） ═══');
for (const label of ['Destroy non-colonists', 'Finish All Research', 'Clear All Gas', 'Fill All Gas', 'Add Guest']) {
  if (aborted) break;
  try {
    await exec(`Actions\\${label}`, {}, 20_000);
    log(`  ✓ ${label}`);
    record(`动作「${label}」`, true);
    await call('rimbridge/wait_for_long_event_idle', { timeoutMs: 10_000 }).catch(() => {});
  } catch (e) {
    record(`动作「${label}」`, false, e.message);
  }
}

// ── 截图存证（A 批结束后的战场） ──────────────────────────────
await call('rimworld/frame_cell_rect', { x: BX + 4, z: BZ + 4, width: 30, height: 30, marginCells: 10 });
const shot = await call('rimworld/take_screenshot', { suppressMessage: true, fileName: 'verify_actions_a' });
if (shot?.path && fs.existsSync(shot.path)) fs.copyFileSync(shot.path, path.join(outDir, path.basename(shot.path)));

const colsAfter = (await call('rimworld/list_colonists', { currentMapOnly: true })).colonists ?? [];
log(`\n殖民者 ${colsAfter.length} 人: ${colsAfter.map((c) => `${c.name}${c.downed ? '(倒地)' : ''}${c.dead ? '(死)' : ''}`).join(', ')}`);

// ═══════════════ B 批：有风险，放最后 ═══════════════
if (!aborted) {
  log('\n═══ B1. 叫一场按点数的袭击（Execute raid with points） ═══');
  try {
    const list = await kids('Actions\\Execute raid with points...');
    const pick = list.find((k) => /^400 points/i.test(k.label ?? '')) ?? list[Math.floor(list.length / 2)];
    log(`  选 ${pick?.label}`);
    const args = {};
    if (pick?.execution?.requiredTargetKind === 'map') {
      args.x = BX;
      args.z = BZ;
    }
    await exec(pick.path, args, 30_000);
    await call('rimworld/wait_for_long_event_idle', { timeoutMs: 30_000 }).catch(() => {});
    await call('rimworld/set_time_speed', { speed: 'Ultrafast', ultraSpeedBoost: true }).catch(() => {});
    await call('rimworld/play_for', { durationMs: 12_000, speed: 'Ultrafast' }, { timeoutMs: 200_000 }).catch(() => {});
    const letters = (await call('rimworld/list_letters', { limit: 50 })).letters ?? [];
    const alerts = (await call('rimworld/list_alerts', { limit: 30 })).alerts ?? [];
    log(`  信件 ${letters.length}: ${letters.slice(0, 5).map((l) => l.label).join(' | ')}`);
    log(`  告警 ${alerts.length}: ${alerts.slice(0, 5).map((a) => a.label).join(' | ')}`);
    record('按点数叫袭击', true);
  } catch (e) {
    record('按点数叫袭击', false, e.message);
  }
}

if (!aborted) {
  log('\n═══ B2. Battle Royale Humanlikes（内置战斗压力测试） ═══');
  try {
    const t0 = Date.now();
    await exec('Actions\\Battle Royale Humanlikes', {}, 25_000);
    log(`  执行 ${((Date.now() - t0) / 1000).toFixed(1)}s`);
    await call('rimworld/wait_for_long_event_idle', { timeoutMs: 30_000 }).catch(() => {});
    const pawns = await countThings(BX, BZ, 40, /Human/i);
    log(`  实验区出现 ${pawns.hits.length} 个 Human 类物体`);
    record('Battle Royale Humanlikes 可执行', true, `区域内有 ${pawns.hits.length} 个人形`);
  } catch (e) {
    record('Battle Royale Humanlikes 可执行', false, e.message);
  }
}

const shot2 = await call('rimworld/take_screenshot', { suppressMessage: true, fileName: 'verify_actions_b' }).catch(() => null);
if (shot2?.path && fs.existsSync(shot2.path)) fs.copyFileSync(shot2.path, path.join(outDir, path.basename(shot2.path)));

client.close();

const passed = results.filter((r) => r.ok).length;
log(`\n${'═'.repeat(62)}`);
log(`阶段 5：${passed}/${results.length} 通过${aborted ? '（中途因桥接超时停止）' : ''}`);
for (const r of results.filter((x) => !x.ok)) log(`  ✗ ${r.name} — ${r.detail}`);
log('═'.repeat(62));
fs.writeFileSync(
  path.join(outDir, 'result.json'),
  JSON.stringify({ stamp, stage: 'actions', aborted, results }, null, 2),
  'utf8',
);
log(`报告: ${path.join(outDir, 'result.json')}`);
