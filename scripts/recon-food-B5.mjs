/**
 * recon-food-B5 —— 复核种植区到底建成没有 + 正确的 UI 交互方式（先取消设计器选择）。
 */
import fs from 'node:fs';
import path from 'node:path';
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor, WS } from '../src/launcher.js';

const OUT = path.join(WS, 'runs', 'food-recon');
const info = readBridgeInfo(logPathFor('vanilla'));
const { client } = await connectGabp(info, { requestTimeoutMs: 300_000 });
const call = (n, a, o) => client.callTool(n, a, o);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const head = (o, n = 2500) => { const s = typeof o === 'string' ? o : JSON.stringify(o); return s.length > n ? s.slice(0, n) + ` …(+${s.length - n})` : s; };
const save = (n, o) => fs.writeFileSync(path.join(OUT, 'dumps', `${n}.json`), JSON.stringify(o, null, 2), 'utf8');
const R = { at: new Date().toISOString(), evidence: {} };

console.log('▶ 0. 现状');
const z0 = await call('rimworld/list_zones', { includeEmpty: true, includeHidden: true });
console.log('  zones: ' + JSON.stringify(z0.zones));
R.evidence.zonesBefore = z0.zones;
const dstate = await call('rimworld/get_designator_state');
console.log('  designator_state: ' + head(dstate, 1000));
R.evidence.designatorState = dstate;

// 看 (15,8)-(18,11) 的 zone 字段
const grid = await call('rimworld/get_cells_info', { x: 15, z: 8, width: 5, height: 5 });
const zoneMap = {};
for (const c of grid.cells ?? []) zoneMap[`${c.x},${c.z}`] = c.zone ? `${c.zone.id}:${c.zone.className}` : null;
console.log('  20 格 zone 分布: ' + JSON.stringify(zoneMap));
R.evidence.zoneMapAfterGrowing = zoneMap;

// ── 1. 干净地建一个 growing zone：先取消当前设计器 ──────────
console.log('\n▶ 1. press_cancel 清掉设计器，再建 growing zone');
const pc = await call('rimworld/press_cancel');
await sleep(300);
const ds2 = await call('rimworld/get_designator_state');
console.log('  取消后 designator_state: ' + head(ds2, 800));
R.evidence.designatorStateAfterCancel = ds2;

const GROW = 'architect-designator:zone:highlight-designator-zoneadd-growing';
// 找一块干净的 Soil/Gravel 5x5（避开已有 Zone_0）
let origin = null;
for (let z = 0; z < 250 && !origin; z += 32) {
  for (let x = 0; x < 250 && !origin; x += 32) {
    const r = await call('rimworld/get_cells_info', { x, z, width: 32, height: 32 });
    const usable = (r.cells ?? []).filter((c) => /Gravel|Soil/.test(c.terrainDefName ?? '') && (c.things ?? []).length === 0 && c.zone == null);
    const set = new Set(usable.map((c) => `${c.x},${c.z}`));
    for (const c of usable) {
      let ok = true;
      for (let dx = 0; dx < 5 && ok; dx++) for (let dz = 0; dz < 5 && ok; dz++) if (!set.has(`${c.x + dx},${c.z + dz}`)) ok = false;
      if (ok) { origin = { x: c.x, z: c.z }; break; }
    }
  }
}
console.log('  起点 = ' + JSON.stringify(origin));
const ZX = origin.x, ZZ = origin.z;
const ap = await call('rimworld/apply_architect_designator', { designatorId: GROW, x: ZX, z: ZZ, width: 4, height: 4, keepSelected: false });
console.log(`  apply: success=${ap.success} requested=${ap.requestedCellCount} accepted=${ap.acceptedCellCount} applied=${ap.appliedCellCount} rejected=${ap.rejectedCellCount}`);
await sleep(400);
const z1 = await call('rimworld/list_zones', { includeEmpty: true });
console.log('  list_zones: ' + JSON.stringify(z1.zones));
save('zones_after_grow2', z1);
R.evidence.zonesAfterGrowing2 = z1.zones;
R.evidence.growingZone2 = { origin: { x: ZX, z: ZZ }, accepted: ap.acceptedCellCount, applied: ap.appliedCellCount };

const g2 = await call('rimworld/get_cells_info', { x: ZX, z: ZZ, width: 4, height: 4 });
console.log('  格 zone 字段: ' + JSON.stringify((g2.cells ?? []).map((c) => ({ x: c.x, z: c.z, zone: c.zone?.className ?? null, id: c.zone?.id ?? null }))));
R.evidence.zoneMap2 = (g2.cells ?? []).map((c) => ({ x: c.x, z: c.z, zone: c.zone?.className ?? null, id: c.zone?.id ?? null }));

// ── 2. 现在选中 zone 读 gizmos ────────────────────────────
await call('rimworld/press_cancel');
await sleep(250);
await call('rimworld/click_cell', { x: ZX + 1, z: ZZ + 1, button: 'left' });
await sleep(400);
const sem = await call('rimworld/get_selection_semantics').catch((e) => ({ error: e.message }));
console.log('\n▶ 2. 选中 growing zone');
console.log('  ' + head(sem, 2000));
R.evidence.growingZoneSelection = { hasSelection: sem.hasSelection, kinds: sem.selectedKinds, obj: sem.selectedObjects?.[0] };
const gz = await call('rimworld/list_selected_gizmos');
console.log('  gizmos: ' + head(gz, 2500));
save('gizmos_growing2', gz);
R.evidence.growingZoneGizmos2 = (gz.gizmos ?? []).map((g) => ({ label: g.label, gizmoId: g.gizmoId, className: g.className, disabled: g.disabled }));
const lay = await call('rimworld/get_ui_layout');
const texts = (lay.surfaces ?? []).flatMap((s) => (s.elements ?? []).map((e) => e.label ?? e.valueText).filter(Boolean));
console.log('  UI 文本: ' + JSON.stringify(texts.slice(0, 40)));
save('ui_layout_zone2', lay);

fs.writeFileSync(path.join(OUT, 'B5.json'), JSON.stringify(R, null, 2), 'utf8');
client.close();
console.log('\n报告：' + path.join(OUT, 'B5.json'));
