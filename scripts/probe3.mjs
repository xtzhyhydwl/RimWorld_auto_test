#!/usr/bin/env node
/**
 * 第三轮：把「用建筑师设计器造东西」彻底搞对。
 * 假设：带省略号（wall... / door...）的是下拉设计器，必须先选材料/子项。
 */
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor } from '../src/launcher.js';

const info = readBridgeInfo(logPathFor('vanilla'));
const { client } = await connectGabp(info, { requestTimeoutMs: 20_000 });
const call = (n, a, o) => client.callTool(n, a, o);
const log = (...a) => console.log(...a);
const dump = (o, n = 2500) => JSON.stringify(o, null, 2).slice(0, n);

const st = (await call('rimbridge/get_bridge_status')).state;
if (st.inEntryScene) {
  log('开调试殖民地…');
  await call('rimworld/start_debug_game_ready', { readiness: 'mapData', pauseIfNeeded: true, timeoutMs: 300_000 }, { timeoutMs: 330_000 });
  await call('rimbridge/wait_for_long_event_idle', { timeoutMs: 120_000 });
}
await call('rimworld/set_god_mode', { enabled: true });
const cols = (await call('rimworld/list_colonists', { currentMapOnly: true })).colonists ?? [];
const c0 = cols[0];
log(`殖民者 ${cols.map((c) => c.name).join(', ')} @(${c0.position.x},${c0.position.z})\n`);

// ── A. wall... 的完整结构，看有没有 children ─────────────────
log('═══ A. "wall..." 设计器的完整结构 ═══');
const dlist = (await call('rimworld/list_architect_designators', { categoryId: 'architect-category:structure' })).designators ?? [];
const wall = dlist.find((d) => /^wall/i.test(d.label));
log(dump(wall, 3000));

// ── B. 对照组：一个不需要材料的简单建筑 ──────────────────────
log('\n═══ B. 对照组：Furniture 里的 sleeping spot（不需要材料）═══');
const furn = (await call('rimworld/list_architect_designators', { categoryId: 'architect-category:furniture' })).designators ?? [];
const spot = furn.find((d) => /sleeping spot/i.test(d.label));
log(`  ${spot?.label} -> ${spot?.id}`);
if (spot) {
  const bx = c0.position.x + 3, bz = c0.position.z + 3;
  const r = await call('rimworld/apply_architect_designator', { designatorId: spot.id, x: bx, z: bz });
  log(`  apply: success=${r.success}`);
  const c = await call('rimworld/get_cell_info', { x: bx, z: bz });
  log(`  格子 (${bx},${bz}) 现在: things=${JSON.stringify((c.cell.things ?? []).map((t) => t.label))}`);
  log(`    blueprints=${JSON.stringify(c.cell.blueprintBuildDefs)} designations=${c.cell.designationCount}`);
  await call('rimworld/frame_cell_rect', { x: bx, z: bz, width: 8, height: 8, marginCells: 5 });
  await call('rimworld/take_screenshot', { suppressMessage: true, fileName: 'probe3_spot' });
}

// ── C. 墙体：试各种可能的 id / 材料写法 ──────────────────────
log('\n═══ C. 造墙：逐个试 ═══');
const bx = c0.position.x + 6, bz = c0.position.z + 6;
const candidates = [
  ['直接用父设计器 build-wall', 'architect-designator:structure:build-wall'],
  ['猜测子设计器 build-wall-wood', 'architect-designator:structure:build-wall-wood'],
  ['猜测子设计器 build-wall-stone', 'architect-designator:structure:build-wall-stone'],
];
for (const [label, id] of candidates) {
  const t0 = Date.now();
  try {
    const r = await call('rimworld/apply_architect_designator', { designatorId: id, x: bx, z: bz }, { timeoutMs: 15_000 });
    const c = await call('rimworld/get_cell_info', { x: bx, z: bz });
    const things = (c.cell.things ?? []).map((t) => t.label);
    log(`  ${label}: success=${r.success} (${Date.now() - t0}ms)`);
    log(`     格子: things=${JSON.stringify(things)} blueprints=${JSON.stringify(c.cell.blueprintBuildDefs)}`);
  } catch (e) {
    log(`  ${label}: ✗ ${e.message} (${Date.now() - t0}ms)`);
    break;
  }
}

// ── D. 用 select + 真实 UI 下拉来选材料 ─────────────────────
log('\n═══ D. 先 select_architect_designator，再抓 UI 看下拉选项 ═══');
try {
  const s = await call('rimworld/select_architect_designator', { designatorId: 'architect-designator:structure:build-wall' });
  log(`  select: ${dump(s, 600)}`);
  const lay = await call('rimworld/get_ui_layout');
  log(`  界面: ${(lay.surfaces ?? []).map((x) => `${x.surfaceTargetId}(${x.elementCount})`).join(', ')}`);
  const labels = (lay.surfaces ?? [])
    .flatMap((x) => (x.elements ?? []).filter((e) => e.label).map((e) => e.label));
  const uniq = [...new Set(labels)];
  log(`  可见文字 ${uniq.length} 条: ${uniq.slice(0, 30).join(' | ')}`);
} catch (e) {
  log(`  ✗ ${e.message}`);
}

// ── E. spawn_thing 对物品 vs 生物的边界 ─────────────────────
log('\n═══ E. spawn_thing 能力边界 ═══');
for (const def of ['Silver', 'Wall', 'Blueprint_Build', 'Human', 'Muffalo']) {
  try {
    const r = await call('rimworld/spawn_thing', { defName: def, x: c0.position.x + 12, z: c0.position.z + 12 }, { timeoutMs: 15_000 });
    log(`  ${def}: success=${r.success}${r.success === false ? ` — ${String(r.message).slice(0, 70)}` : ''}`);
  } catch (e) {
    log(`  ${def}: ✗ ${e.message}`);
    break;
  }
}

client.close();
log('\n完成');
