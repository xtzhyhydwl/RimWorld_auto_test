/**
 * recon-food-A3 —— A 块补充：
 *  1. 地图尺寸（jump_camera_to_cell 二分探边界）
 *  2. 全图 get_cells_info 扫描实测耗时（每 1024 格计时）+ 食物统计
 *  3. 选中食物 → get_selection_semantics / inspect 文本里有没有 nutrition
 *  4. list_zones 结构（空殖民地）
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
const head = (o, n = 1600) => { const s = typeof o === 'string' ? o : JSON.stringify(o); return s.length > n ? s.slice(0, n) + ` …(+${s.length - n})` : s; };
const save = (n, o) => fs.writeFileSync(path.join(OUT, 'dumps', `${n}.json`), JSON.stringify(o, null, 2), 'utf8');
const R = { at: new Date().toISOString(), timings: {}, evidence: {} };

// ── 1. 地图尺寸 ──────────────────────────────────────────────
async function probeCell(x, z) {
  const r = await call('rimworld/jump_camera_to_cell', { x, z }).catch((e) => ({ success: false, msg: e.message }));
  const cs = await call('rimworld/get_camera_state').catch(() => null);
  return { x, z, ok: r?.success !== false, clamped: cs?.mapPosition };
}
console.log('▶ 1. 探地图边界');
const probes = [];
for (const p of [[0, 0], [1, 1], [248, 248], [249, 249], [250, 250], [251, 251], [255, 255], [300, 300]]) {
  probes.push(await probeCell(p[0], p[1]));
  await sleep(60);
}
for (const p of probes) console.log(`  jump(${p.x},${p.z}) ok=${p.ok} → camera(${p.clamped?.x},${p.clamped?.z})`);
R.evidence.mapBoundsProbe = probes;

// 用 clamped 位置二分找真正的 x 上界
let lo = 200, hi = 400;
while (hi - lo > 1) {
  const mid = Math.floor((lo + hi) / 2);
  const r = await probeCell(mid, 125);
  if (r.ok) lo = mid; else hi = mid;
  await sleep(40);
}
console.log(`  x 上界：最后一个可跳 = ${lo}，第一个失败 = ${hi}`);
const upperX = lo;
let lo2 = 200, hi2 = 400;
while (hi2 - lo2 > 1) {
  const mid = Math.floor((lo2 + hi2) / 2);
  const r = await probeCell(125, mid);
  if (r.ok) lo2 = mid; else hi2 = mid;
  await sleep(40);
}
console.log(`  z 上界：最后一个可跳 = ${lo2}，第一个失败 = ${hi2}`);
R.evidence.mapSize = { maxX: upperX, maxZ: lo2, note: 'jump 在超出地图时返回 success:false 且相机停在边界' };

// ── 2. 全图扫描计时 ─────────────────────────────────────────
console.log('\n▶ 2. 全图 get_cells_info 扫描（1024 格/次）');
const W = upperX + 1, H = lo2 + 1;
const tAll = Date.now();
let cells = 0, calls = 0, bytes = 0, foodStacks = 0;
const foods = {};
const BATCH = 32; // 32x32 = 1024
const t0 = Date.now();
const callTimes = [];
for (let z = 0; z < H; z += BATCH) {
  for (let x = 0; x < W; x += BATCH) {
    const w = Math.min(BATCH, W - x), h = Math.min(BATCH, H - z);
    const s0 = Date.now();
    const r = await call('rimworld/get_cells_info', { x, z, width: w, height: h });
    callTimes.push(Date.now() - s0);
    calls++;
    const list = r.cells ?? [];
    cells += list.length;
    bytes += JSON.stringify(r).length;
    for (const c of list) for (const t of c.things ?? []) {
      // 只统计「看起来是食物」的 defName，避免把石头算进来
      if (/^(Meal|Pemmican|Raw|Kibble|Hay|Silage|Milk|Egg|InsectJelly|BabyFood|Chocolate|Ambrosia|Meat_)/.test(t.defName ?? '')) {
        foodStacks++;
        foods[t.defName] = (foods[t.defName] ?? 0) + 1;
      }
    }
  }
}
const totalMs = Date.now() - t0;
callTimes.sort((a, b) => a - b);
R.timings.fullMapScan = {
  mapSize: `${W}x${H}`, cellsRequested: W * H, cellsReturned: cells, calls,
  totalMs, avgCallMs: +(totalMs / calls).toFixed(1),
  medianCallMs: callTimes[Math.floor(callTimes.length / 2)],
  maxCallMs: callTimes.at(-1),
  approxBytes: bytes, bytesPer1024Cells: Math.round(bytes / calls),
};
console.log(`  地图 ${W}x${H} = ${W * H} 格（=1024 格的 ${(W * H / 1024).toFixed(1)} 批）`);
console.log(`  ${calls} 次调用，共 ${totalMs}ms（平均 ${R.timings.fullMapScan.avgCallMs}ms/次，中位 ${R.timings.fullMapScan.medianCallMs}ms，最大 ${R.timings.fullMapScan.maxCallMs}ms）`);
console.log(`  传输量约 ${(bytes / 1024 / 1024).toFixed(1)} MB`);
console.log(`  找到食物堆 ${foodStacks}：${JSON.stringify(foods)}`);
R.evidence.foodStacksOnMap = foods;

// 只扫含食物区域的开销对比
{
  const t = Date.now();
  await call('rimworld/get_cells_info', { x: 120, z: 130, width: 32, height: 32 });
  R.timings.oneBatch = Date.now() - t;
  console.log(`  单批 1024 格：${R.timings.oneBatch}ms`);
}

// ── 3. 食物营养值：选中 + inspect 文本 ───────────────────────
console.log('\n▶ 3. 食物营养值可读性');
const cellFood = await call('rimworld/get_cell_info', { x: 146, z: 135 });
console.log('  cell.things[0] keys: ' + Object.keys(cellFood.cell.things[0] ?? {}).join(', '));
R.evidence.thingKeys = Object.keys(cellFood.cell.things[0] ?? {});

// 尝试选中格子里的东西再读 inspect
console.log('  → click_cell(146,135) 然后 get_selection_semantics');
const cc = await call('rimworld/click_cell', { x: 146, z: 135, button: 'left' });
console.log('  ' + head(cc, 700));
await sleep(300);
const sem = await call('rimworld/get_selection_semantics');
console.log('  ' + head(sem, 2500));
save('selection_food', sem);
R.evidence.selectionOnFood = { hasSelection: sem?.hasSelection, selectedCount: sem?.selectedCount, inspectString: sem?.selectedObjects?.[0]?.inspectString };

// 也试 map_target_info 用 thingId
const mti = await call('rimworld/get_map_target_info', { thingId: 'Thing_MealSimple11991' });
console.log('  get_map_target_info(thingId) ' + head(mti, 900));
R.evidence.mapTargetInfoThing = mti?.target ?? mti;

// ── 4. list_zones ──────────────────────────────────────────
console.log('\n▶ 4. list_zones（新殖民地，应为空/无）');
const z = await call('rimworld/list_zones', { includeEmpty: true });
console.log('  ' + head(z, 1500));
save('zones_empty', z);
R.evidence.zonesEmpty = z;

// ── 5. list_areas（home area 等） ───────────────────────────
const ar = await call('rimworld/list_areas');
console.log('\n▶ 5. list_areas ' + head(ar, 1500));
save('areas', ar);

// ── 6. 殖民者需求（食物需求在哪） ────────────────────────────
console.log('\n▶ 6. 选中殖民者读 inspect（看有没有 food need）');
const cols = await call('rimworld/list_colonists', { currentMapOnly: true });
const first = (cols.colonists ?? [])[0];
await call('rimworld/select_pawn', { pawnId: first.pawnId });
await sleep(250);
const semP = await call('rimworld/get_selection_semantics');
console.log('  ' + head(semP, 3000));
save('selection_pawn', semP);
const tabs = await call('rimworld/list_inspect_tabs');
console.log('  inspect tabs: ' + head(tabs, 1200));
save('inspect_tabs', tabs);

fs.writeFileSync(path.join(OUT, 'A3.json'), JSON.stringify(R, null, 2), 'utf8');
client.close();
console.log('\n报告：' + path.join(OUT, 'A3.json'));
