#!/usr/bin/env node
/**
 * 针对性排查：把巡览里没搞明白的几处逐个弄清楚。
 * 每步 30 秒超时，绝不让桥接被拖死。
 */
import fs from 'node:fs';
import path from 'node:path';
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor, WS } from '../src/launcher.js';

const info = readBridgeInfo(logPathFor('vanilla'));
const { client } = await connectGabp(info, { requestTimeoutMs: 30_000 });
const call = (n, a, o) => client.callTool(n, a, o);
const log = (...a) => console.log(...a);

async function t(label, fn) {
  const t0 = Date.now();
  try {
    const r = await fn();
    log(`  ✓ ${label} (${Date.now() - t0}ms)`);
    return r;
  } catch (e) {
    log(`  ✗ ${label} (${Date.now() - t0}ms): ${e.message}`);
    return null;
  }
}

const dump = (o, max = 700) => JSON.stringify(o, null, 2).slice(0, max);

// ── 准备：确保在游戏里 ────────────────────────────────────────
const st = (await call('rimbridge/get_bridge_status')).state;
if (st.inEntryScene) {
  log('开调试殖民地…');
  await call('rimworld/start_debug_game_ready', { readiness: 'mapData', pauseIfNeeded: true, timeoutMs: 300_000 }, { timeoutMs: 330_000 });
  await call('rimbridge/wait_for_long_event_idle', { timeoutMs: 120_000 });
}
await call('rimworld/set_god_mode', { enabled: true });
const cols = (await call('rimworld/list_colonists', { currentMapOnly: true })).colonists ?? [];
const c0 = cols[0];
log(`殖民者: ${cols.map((c) => c.name).join(', ')}  基准格 (${c0.position.x},${c0.position.z})\n`);

// ── 1. 建筑师：正确的找法 ─────────────────────────────────────
log('═══ 1. 建筑师设计器：Structure 分类 + 精确匹配 label ═══');
const cats = (await call('rimworld/list_architect_categories')).categories ?? [];
const struct = cats.find((c) => c.categoryDefName === 'Structure') ?? cats.find((c) => /struct/i.test(c.label));
log(`  用分类「${struct.label}」(${struct.id})`);
const dlist = (await call('rimworld/list_architect_designators', { categoryId: struct.id })).designators ?? [];
log(`  ${dlist.length} 个设计器:`);
for (const d of dlist.slice(0, 30)) log(`    ${d.label}   ${d.id}`);
const wall = dlist.find((d) => /^wall$/i.test(d.label)) ?? dlist.find((d) => /^wall/i.test(d.label));
log(`  选中: ${wall?.label} -> ${wall?.id}`);

const bx = c0.position.x + 6, bz = c0.position.z + 6, S = 9;
log(`\n  在 (${bx},${bz}) 起 ${S}x${S} 摆墙:`);
const r1 = await t('apply 上边', () => call('rimworld/apply_architect_designator', { designatorId: wall.id, x: bx, z: bz, width: S, height: 1 }));
log(`    ${dump(r1, 400)}`);
await call('rimworld/apply_architect_designator', { designatorId: wall.id, x: bx, z: bz + S - 1, width: S, height: 1 });
await call('rimworld/apply_architect_designator', { designatorId: wall.id, x: bx, z: bz, width: 1, height: S });
await call('rimworld/apply_architect_designator', { designatorId: wall.id, x: bx + S - 1, z: bz, width: 1, height: S });

const cellW = await call('rimworld/get_cell_info', { x: bx, z: bz });
log(`  墙角 (${bx},${bz}): blueprints=${JSON.stringify(cellW.cell.blueprintBuildDefs)} designations=${cellW.cell.designationCount}`);
const cellIn = await call('rimworld/get_cell_info', { x: bx + 2, z: bz + 2 });
log(`  内部 (${bx + 2},${bz + 2}): blueprints=${JSON.stringify(cellIn.cell.blueprintBuildDefs)} designations=${cellIn.cell.designationCount}`);

// 地板
const floors = cats.find((c) => c.categoryDefName === 'Floors');
const fl = (await call('rimworld/list_architect_designators', { categoryId: floors.id })).designators ?? [];
log(`\n  Floors 分类 ${fl.length} 个，前 5: ${fl.slice(0, 5).map((d) => d.label).join(', ')}`);
const concrete = fl.find((d) => /concrete|paved|tile/i.test(d.label)) ?? fl[0];
if (concrete) {
  const rf = await t(`apply 地板「${concrete.label}」`, () =>
    call('rimworld/apply_architect_designator', { designatorId: concrete.id, x: bx + 1, z: bz + 1, width: S - 2, height: S - 2 }),
  );
  log(`    ${dump(rf, 300)}`);
}
await call('rimworld/frame_cell_rect', { x: bx + 4, z: bz + 4, width: S + 4, height: S + 4, marginCells: 6 });
await call('rimworld/take_screenshot', { suppressMessage: true, fileName: 'probe_room' });

// ── 2. 生成动物 ───────────────────────────────────────────────
log('\n═══ 2. 生成动物：到底要怎么传 ═══');
for (const def of ['Thrumbo', 'Muffalo', 'Boomrat', 'Animal_Thrumbo']) {
  const r = await t(`spawn_thing defName=${def}`, () =>
    call('rimworld/spawn_thing', { defName: def, x: c0.position.x + 10, z: c0.position.z + 10 }),
  );
  if (r) log(`    ${dump(r, 400)}`);
}
log('  改用 debug action 找生成 pawn 的入口:');
const sp = await call('rimworld/search_debug_actions', { query: 'spawn pawn', limit: 10, supportedOnly: true });
for (const m of sp.matches ?? []) {
  log(`    [${m.node.execution?.kind}] ${m.path}`);
}
const sp2 = await call('rimworld/search_debug_actions', { query: 'Thrumbo', limit: 8, supportedOnly: false });
log(`  搜 Thrumbo: ${(sp2.matches ?? []).map((m) => m.path).join(' | ') || '无'}`);

// ── 3. 选中语义 / gizmo 的真实字段 ────────────────────────────
log('\n═══ 3. get_selection_semantics / list_selected_gizmos 的真实结构 ═══');
await call('rimworld/select_pawn', { pawnId: c0.pawnId });
const sem = await call('rimworld/get_selection_semantics');
log(`  get_selection_semantics 顶层键: ${Object.keys(sem).join(', ')}`);
log(`  ${dump(sem, 1200)}`);
const gz = await call('rimworld/list_selected_gizmos');
log(`\n  list_selected_gizmos 顶层键: ${Object.keys(gz).join(', ')}`);
log(`  ${dump(gz, 900)}`);

// 征召后再看 gizmo 有没有变化
await call('rimworld/set_draft', { pawnId: c0.pawnId, drafted: true });
const gz2 = await call('rimworld/list_selected_gizmos');
const arr2 = gz2.gizmos ?? gz2.items ?? [];
log(`  征召后 gizmo ${arr2.length} 个: ${arr2.map((g) => g.label ?? g.id).join(' | ')}`);
await call('rimworld/set_draft', { pawnId: c0.pawnId, drafted: false });

// ── 4. 右键菜单 ───────────────────────────────────────────────
log('\n═══ 4. open_context_menu 返回了什么 ═══');
const menu = await call('rimworld/open_context_menu', { x: c0.position.x, z: c0.position.z });
log(`  顶层键: ${Object.keys(menu).join(', ')}`);
log(`  ${dump(menu, 1500)}`);
const g = await call('rimworld/get_context_menu_options');
log(`\n  get_context_menu_options: ${dump(g, 700)}`);
await call('rimworld/close_context_menu', {});

// 换个模式试试
log('\n  用 mode=debug 再试:');
const menu2 = await call('rimworld/open_context_menu', { x: c0.position.x, z: c0.position.z, mode: 'debug' });
log(`  ${dump(menu2, 900)}`);
await call('rimworld/close_context_menu', {}).catch(() => {});

client.close();
log('\n完成');
