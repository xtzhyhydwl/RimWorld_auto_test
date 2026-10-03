#!/usr/bin/env node
/** 验证：那个依赖警告到底有没有实际影响 —— 鼠族内容是否真的加载了。 */
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor } from '../src/launcher.js';

const info = readBridgeInfo(logPathFor('vanilla'));
const { client } = await connectGabp(info, { requestTimeoutMs: 30_000 });
const call = (n, a, o) => client.callTool(n, a, o);
const log = (...a) => console.log(...a);

// ── 1. Spawn Pawn 列表里有没有鼠族 ───────────────────────────
log('=== Actions\\Spawn Pawn... 里找鼠族 ===');
const r = await call('rimworld/get_debug_action', {
  path: 'Actions\\Spawn Pawn...',
  includeChildren: true,
  includeHiddenChildren: true,
});
const kids = r.children ?? r.action?.children ?? [];
const ratkin = kids.filter((k) => /ratkin/i.test(`${k.label} ${k.path}`));
log(`  共 ${kids.length} 个 pawn 类型，其中鼠族 ${ratkin.length} 个:`);
for (const k of ratkin.slice(0, 15)) log(`    ${k.label}  [${k.execution?.kind}/${k.execution?.requiredTargetKind ?? '-'}]`);

// ── 2. 鼠族相关的 Def 是否存在于游戏里 ───────────────────────
log('\n=== 搜索鼠族相关 def（通过 debug action 树） ===');
for (const q of ['Ratkin', 'Rat']) {
  const s = await call('rimworld/search_debug_actions', { query: q, limit: 8, supportedOnly: false });
  log(`  「${q}」总命中 ${s.totalMatchCount}`);
  for (const m of (s.matches ?? []).slice(0, 6)) log(`    [${m.node.execution?.kind}] ${m.path}`);
}

// ── 3. 实际生成一只鼠族，看能不能成功 ────────────────────────
log('\n=== 实际生成一只鼠族 ===');
const st = (await call('rimbridge/get_bridge_status')).state;
if (st.inEntryScene) {
  log('  开调试殖民地…');
  await call('rimworld/start_debug_game_ready', { readiness: 'mapData', pauseIfNeeded: true, timeoutMs: 300_000 }, { timeoutMs: 330_000 });
}
await call('rimbridge/wait_for_game_loaded', { readiness: 'currentMap', pauseIfNeeded: true, timeoutMs: 180_000 }, { timeoutMs: 210_000 });
const cols = (await call('rimworld/list_colonists', { currentMapOnly: true })).colonists ?? [];
const c0 = cols[0];
log(`  基准殖民者 ${c0?.fullName} @(${c0?.position.x},${c0?.position.z})`);

if (ratkin.length) {
  const pick = ratkin[0];
  const bx = c0.position.x + 5;
  const bz = c0.position.z + 5;
  const t0 = Date.now();
  const exec = await call(
    'rimworld/execute_debug_action',
    { path: pick.path, x: bx, z: bz },
    { timeoutMs: 25_000 },
  );
  log(`  生成 ${pick.label}: success=${exec.success} (${Date.now() - t0}ms)`);
  await call('rimbridge/wait_for_long_event_idle', { timeoutMs: 20_000 }).catch(() => {});
  const cell = await call('rimworld/get_cells_info', { x: bx - 3, z: bz - 3, width: 7, height: 7 });
  const things = [...new Set((cell.cells ?? []).flatMap((c) => (c.things ?? []).map((t) => `${t.defName}`)))];
  log(`  落点附近出现: ${things.join(', ')}`);
  const gotRatkin = things.some((t) => /ratkin/i.test(t));
  log(`  ${gotRatkin ? '✓' : '?'} 出现鼠族相关 def: ${gotRatkin}`);
} else {
  log('  ✗ Spawn Pawn 列表里没有鼠族');
}

// ── 4. 最近日志里有没有鼠族相关报错 ──────────────────────────
log('\n=== 日志（warning 及以上） ===');
const logs = await call('rimbridge/list_logs', { minimumLevel: 'warning', limit: 200 });
const entries = logs.logs ?? [];
log(`  共 ${entries.length} 条`);
for (const e of entries.slice(0, 15)) {
  log(`  [${e.level}] ${String(e.message).replace(/\s+/g, ' ').slice(0, 150)}`);
}

client.close();
