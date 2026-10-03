/**
 * recon-food-B2 —— 实测画种植区：
 *  1. 找一块可种的空地（soil/gravel，不在水里）
 *  2. Growing zone designator: dryRun → 真 apply 4x4
 *  3. list_zones 回读结构；get_cell_info 里 zone 字段长什么样
 *  4. 试 set_zone_target 看能不能指定作物
 *  5. 读生长进度：cell.things 里的 Plant_Rice 有什么字段
 */
import fs from 'node:fs';
import path from 'node:path';
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor, WS } from '../src/launcher.js';

const OUT = path.join(WS, 'runs', 'food-recon');
const info = readBridgeInfo(logPathFor('vanilla'));
const { client } = await connectGabp(info, { requestTimeoutMs: 60_000 });
const call = (n, a, o) => client.callTool(n, a, o);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const head = (o, n = 2500) => { const s = typeof o === 'string' ? o : JSON.stringify(o); return s.length > n ? s.slice(0, n) + ` …(+${s.length - n})` : s; };
const save = (n, o) => fs.writeFileSync(path.join(OUT, 'dumps', `${n}.json`), JSON.stringify(o, null, 2), 'utf8');
const R = { at: new Date().toISOString(), evidence: {}, steps: [] };
async function step(name, fn) {
  const s = Date.now();
  console.log(`\n▶ ${name}`);
  try { const v = await fn(); R.steps.push({ name, ok: true, ms: Date.now() - s }); return v; }
  catch (e) { R.steps.push({ name, ok: false, ms: Date.now() - s, error: e.message }); console.log('  ✗ ' + e.message); return null; }
}

// ── 1. 找可种地 ────────────────────────────────────────────
const soil = await step('找可种空地（扫 60x60 @ 100,110）', async () => {
  const r = await call('rimworld/get_cells_info', { x: 100, z: 110, width: 32, height: 32 });
  const best = (r.cells ?? []).filter((c) => c.walkable && (c.things ?? []).length === 0 && c.zone == null &&
    /Soil|Gravel|Sand|Marshy|Mossy/i.test(c.terrainDefName ?? ''));
  const fert = {};
  for (const c of best) fert[c.terrainDefName] = (fert[c.terrainDefName] ?? 0) + 1;
  console.log(`  可种格（无 thing、无 zone）${best.length} 个，地形分布 ${JSON.stringify(fert)}`);
  return best;
});
// 找 4x4 连续块
function findBlock(cells, w, h) {
  const set = new Set(cells.map((c) => `${c.x},${c.z}`));
  for (const c of cells) {
    let ok = true;
    for (let dx = 0; dx < w && ok; dx++) for (let dz = 0; dz < h && ok; dz++) if (!set.has(`${c.x + dx},${c.z + dz}`)) ok = false;
    if (ok) return { x: c.x, z: c.z };
  }
  return null;
}
const blk = findBlock(soil ?? [], 4, 4);
console.log(`  4x4 连续块起点：${JSON.stringify(blk)}`);
const ZX = blk?.x ?? 118, ZZ = blk?.z ?? 138;
R.evidence.zoneOrigin = { x: ZX, z: ZZ, terrain: (soil ?? []).find((c) => c.x === ZX && c.z === ZZ)?.terrainDefName };

// ── 2. Growing zone designator ─────────────────────────────
const GROW = 'architect-designator:zone:highlight-designator-zoneadd-growing';
await step('apply_architect_designator dryRun 4x4', async () => {
  const r = await call('rimworld/apply_architect_designator', { designatorId: GROW, x: ZX, z: ZZ, width: 4, height: 4, dryRun: true });
  console.log('  ' + head(r, 1500));
  R.evidence.dryRun = { success: r?.success, message: r?.message, actionable: r?.actionable, supportsCellApplication: r?.supportsCellApplication };
  return r;
});
await step('apply_architect_designator 真做 4x4', async () => {
  const r = await call('rimworld/apply_architect_designator', { designatorId: GROW, x: ZX, z: ZZ, width: 4, height: 4 });
  console.log('  ' + head(r, 1500));
  R.evidence.apply = { success: r?.success, message: r?.message, appliedCells: r?.appliedCells, cells: r?.cells?.length };
  return r;
});
await sleep(400);

await step('回读 list_zones', async () => {
  const z = await call('rimworld/list_zones', { includeEmpty: true });
  console.log('  ' + head(z, 2500));
  save('zones_after_grow', z);
  R.evidence.zonesAfterGrow = z.zones;
  return z;
});
await step('回读 get_cell_info(zone 内)', async () => {
  const c = await call('rimworld/get_cell_info', { x: ZX + 1, z: ZZ + 1 });
  console.log('  ' + head(c.cell, 2000));
  save('cell_in_growing_zone', c);
  R.evidence.cellInZone = c.cell;
  const out = await call('rimworld/get_cell_info', { x: ZX + 20, z: ZZ + 20 });
  console.log('  区外对照: ' + head({ terrain: out.cell.terrainDefName, zone: out.cell.zone, things: out.cell.things }, 600));
  return c;
});

// ── 3. set_zone_target 能不能指定作物 ────────────────────────
await step('试 set_zone_target 指定 zone 到 growing designator', async () => {
  const zid = (R.evidence.zonesAfterGrow ?? [])[0]?.id ?? (R.evidence.zonesAfterGrow ?? [])[0]?.zoneId;
  console.log('  zoneId = ' + zid);
  if (!zid) return null;
  const r = await call('rimworld/set_zone_target', { designatorId: GROW, zoneId: zid });
  console.log('  ' + head(r, 1200));
  R.evidence.setZoneTarget = r;
  return r;
});

// ── 4. 生长进度可读性 ───────────────────────────────────────
await step('在种植区里手动 spawn 一株 Plant_Rice 看字段', async () => {
  const r = await call('rimworld/spawn_thing', { defName: 'Plant_Rice', x: ZX + 2, z: ZZ + 2 });
  console.log('  spawn: ' + head(r, 700));
  await sleep(300);
  const c = await call('rimworld/get_cell_info', { x: ZX + 2, z: ZZ + 2 });
  console.log('  ' + head(c.cell, 2000));
  save('cell_with_plant', c);
  R.evidence.plantCell = c.cell;
  return c;
});

// 也许种植区自己会长出 Plant_Rice？看整个区
await step('扫种植区 4x4 看有没有自动长出的植物', async () => {
  const r = await call('rimworld/get_cells_info', { x: ZX, z: ZZ, width: 4, height: 4 });
  for (const c of r.cells ?? []) {
    if ((c.things ?? []).length) console.log(`  (${c.x},${c.z}) zone=${JSON.stringify(c.zone)} things=${JSON.stringify(c.things)}`);
  }
  save('cells_growing_zone', r);
  return r;
});

// ── 5. Orders\Hunt designator 存在性（C 块预研） ────────────
console.log('\n▶ 附：Orders\\Hunt designator id = architect-designator:orders:highlight-designator-tutortagnotset-4');

fs.writeFileSync(path.join(OUT, 'B2.json'), JSON.stringify(R, null, 2), 'utf8');
client.close();
console.log('\n报告：' + path.join(OUT, 'B2.json'));
