/**
 * recon-food-B4/D —— 种植区建成后：默认作物、能否改作物、生长进度可读性、tick 推进性能。
 */
import fs from 'node:fs';
import path from 'node:path';
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor, WS } from '../src/launcher.js';

const OUT = path.join(WS, 'runs', 'food-recon');
const info = readBridgeInfo(logPathFor('vanilla'));
const { client } = await connectGabp(info, { requestTimeoutMs: 900_000 });
const call = (n, a, o) => client.callTool(n, a, o);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const head = (o, n = 2500) => { const s = typeof o === 'string' ? o : JSON.stringify(o); return s.length > n ? s.slice(0, n) + ` …(+${s.length - n})` : s; };
const save = (n, o) => fs.writeFileSync(path.join(OUT, 'dumps', `${n}.json`), JSON.stringify(o, null, 2), 'utf8');
const R = { at: new Date().toISOString(), evidence: {} };

const GROW = 'architect-designator:zone:highlight-designator-zoneadd-growing';

// ── 1. 找 Gravel/Soil 空地，建 4x4 种植区 ──────────────────
console.log('▶ 1. 找 Gravel/Soil 空地并建种植区');
let origin = null;
{
  // 扫一大片，收集所有「可种且空」的格，再找 4x4 连续块
  const usable = [];
  for (let z = 0; z < 250 && !origin; z += 32) {
    for (let x = 0; x < 250 && !origin; x += 32) {
      const r = await call('rimworld/get_cells_info', { x, z, width: 32, height: 32 });
      for (const c of r.cells ?? []) {
        if (/Gravel|Soil/.test(c.terrainDefName ?? '') && (c.things ?? []).length === 0 && c.zone == null) usable.push(c);
      }
      const set = new Set(usable.map((c) => `${c.x},${c.z}`));
      for (const c of usable) {
        let ok = true;
        for (let dx = 0; dx < 5 && ok; dx++) for (let dz = 0; dz < 5 && ok; dz++) if (!set.has(`${c.x + dx},${c.z + dz}`)) ok = false;
        if (ok) { origin = { x: c.x, z: c.z }; break; }
      }
      if (origin) break;
    }
  }
  console.log(`  可种空格总计 ${usable.length}；5x5 连续块起点 = ${JSON.stringify(origin)}`);
}
console.log('  起点 = ' + JSON.stringify(origin));
const ZX = origin.x, ZZ = origin.z;
const ap = await call('rimworld/apply_architect_designator', { designatorId: GROW, x: ZX, z: ZZ, width: 4, height: 4 });
console.log(`  apply: success=${ap.success} requested=${ap.requestedCellCount} accepted=${ap.acceptedCellCount} applied=${ap.appliedCellCount} rejected=${ap.rejectedCellCount}`);
R.evidence.growingZoneApply = { success: ap.success, requested: ap.requestedCellCount, accepted: ap.acceptedCellCount, applied: ap.appliedCellCount, rejected: ap.rejectedCellCount, origin: { x: ZX, z: ZZ } };
await sleep(400);

const z1 = await call('rimworld/list_zones', { includeEmpty: true });
console.log('  list_zones: ' + head(z1, 1500));
save('zones_growing_ok', z1);
R.evidence.zonesAfterGrowing = z1.zones;

const gz = (z1.zones ?? []).find((z) => /Growing/i.test(z.className ?? ''));
if (gz) {
  const c = await call('rimworld/get_cell_info', { x: ZX + 1, z: ZZ + 1 });
  console.log('  zone 内 cell.zone = ' + JSON.stringify(c.cell.zone));
  console.log('  zone 内 cell.things = ' + JSON.stringify(c.cell.things));
  R.evidence.zoneCell = { zone: c.cell.zone, things: c.cell.things };
}

// ── 2. 默认种什么 + 能不能改作物 ─────────────────────────────
console.log('\n▶ 2. 种植区默认作物 / 改作物路径');
if (gz) {
  // 选中 zone 看 gizmos
  const cl = await call('rimworld/click_cell', { x: ZX + 1, z: ZZ + 1, button: 'left' });
  await sleep(300);
  const gz2 = await call('rimworld/list_selected_gizmos');
  console.log('  gizmos: ' + head(gz2, 2500));
  save('gizmos_growing_zone', gz2);
  R.evidence.growingZoneGizmos = (gz2.gizmos ?? []).map((g) => ({ label: g.label, id: g.gizmoId ?? g.id, className: g.className, disabled: g.disabled }));
}
// set_zone_target：把 growing designator 指向已有 growing zone
const zid = gz?.id;
if (zid) {
  const st = await call('rimworld/set_zone_target', { designatorId: GROW, zoneId: zid });
  console.log('  set_zone_target: ' + head(st, 1200));
  R.evidence.setZoneTarget = st;
  // 再 apply 到旁边空地看看会不会用同一个 zone / 不同作物
}
// 有没有别的方式设作物？看有没有 designator 带 plant 参数
const allDesig = await call('rimworld/list_architect_designators', { categoryId: 'architect-category:zone', includeHidden: true });
save('designators_zone_full', allDesig);

// 直接给种植区 spawn 一株作物，看 zone 是否接管
console.log('\n▶ 2b. 往种植区 spawn Plant_Rice x1，看生长进度可读性');
const sp = await call('rimworld/spawn_thing', { defName: 'Plant_Rice', x: ZX + 1, z: ZZ + 1 });
console.log('  ' + head(sp, 500));
await sleep(400);
const pc = await call('rimworld/get_cell_info', { x: ZX + 1, z: ZZ + 1 });
console.log('  cell: ' + head(pc.cell, 1500));
save('cell_plant_t0', pc);
R.evidence.plantCellT0 = pc.cell;

// 选中植物读 inspect
await call('rimworld/click_cell', { x: ZX + 1, z: ZZ + 1, button: 'left' });
await sleep(400);
const sem = await call('rimworld/get_selection_semantics').catch((e) => ({ error: e.message }));
console.log('  selection_semantics: ' + head(sem, 1500));
R.evidence.plantSelectionSemantics = sem?.selectedObjects?.[0] ?? sem;
const lay = await call('rimworld/get_ui_layout');
const texts = (lay.surfaces ?? []).flatMap((s) => (s.elements ?? []).map((e) => e.label ?? e.valueText).filter(Boolean));
console.log('  UI 文本: ' + JSON.stringify(texts.filter((t) => /growth|grow|plant|rice|%|harvest/i.test(t)).slice(0, 20)));
R.evidence.plantInspectTexts = texts;
save('ui_layout_plant_t0', lay);

// ── 3. tick 推进性能 ────────────────────────────────────────
console.log('\n▶ 3. tick 推进性能');
async function stepTicks(n) {
  const s = Date.now();
  const r = await call('rimworld/step_game_ticks', { ticks: n, pauseFirst: true, timeoutMs: 800_000 }, { timeoutMs: 880_000 });
  const ms = Date.now() - s;
  console.log(`  ${n} tick: status=${r.status} advanced=${r.advancedTicks} frames=${r.advancedFrames} 耗时 ${ms}ms (${(n / (ms / 1000)).toFixed(0)} tick/s)`);
  return { n, ms, status: r.status, advanced: r.advancedTicks, frames: r.advancedFrames, startTicksGame: r.startTicksGame, endTicksGame: r.endTicksGame };
}
R.evidence.tickTimings = [];
R.evidence.tickTimings.push(await stepTicks(300));
R.evidence.tickTimings.push(await stepTicks(2500));
R.evidence.tickTimings.push(await stepTicks(15000));

// ── 4. 生长有没有推进 ───────────────────────────────────────
console.log('\n▶ 4. 生长进度（15000 tick 之后）');
const pc2 = await call('rimworld/get_cell_info', { x: ZX + 1, z: ZZ + 1 });
console.log('  cell: ' + head(pc2.cell, 1500));
save('cell_plant_t15000', pc2);
R.evidence.plantCellT15000 = pc2.cell;
await call('rimworld/click_cell', { x: ZX + 1, z: ZZ + 1, button: 'left' });
await sleep(400);
const lay2 = await call('rimworld/get_ui_layout');
const texts2 = (lay2.surfaces ?? []).flatMap((s) => (s.elements ?? []).map((e) => e.label ?? e.valueText).filter(Boolean));
console.log('  UI 文本: ' + JSON.stringify(texts2.filter((t) => /growth|grow|plant|rice|%|harvest|mature/i.test(t)).slice(0, 20)));
R.evidence.plantInspectTextsAfter = texts2;
save('ui_layout_plant_t15000', lay2);
// zone 里有没有长出别的植物
const zc = await call('rimworld/get_cells_info', { x: ZX, z: ZZ, width: 4, height: 4 });
for (const c of zc.cells ?? []) if ((c.things ?? []).length) console.log(`  (${c.x},${c.z}) ${JSON.stringify(c.things)}`);
save('cells_growing_after', zc);
R.evidence.growingZoneCellsAfter = (zc.cells ?? []).filter((c) => (c.things ?? []).length).map((c) => ({ x: c.x, z: c.z, things: c.things }));

fs.writeFileSync(path.join(OUT, 'B4.json'), JSON.stringify(R, null, 2), 'utf8');
client.close();
console.log('\n报告：' + path.join(OUT, 'B4.json'));
