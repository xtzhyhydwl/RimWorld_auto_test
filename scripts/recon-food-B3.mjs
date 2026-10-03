/**
 * recon-food-B3 —— 为什么 Growing zone 被拒？扫描更广范围找接受的格子。
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
const save = (n, o) => fs.writeFileSync(path.join(OUT, 'dumps', `${n}.json`), JSON.stringify(o, null, 2), 'utf8');
const R = { at: new Date().toISOString(), evidence: {} };
const GROW = 'architect-designator:zone:highlight-designator-zoneadd-growing';

// 1) 全图地形分布
console.log('▶ 全图地形分布（1024/批）');
const terr = {};
const W = 250, H = 250;
for (let z = 0; z < H; z += 32) for (let x = 0; x < W; x += 32) {
  const r = await call('rimworld/get_cells_info', { x, z, width: 32, height: 32 });
  for (const c of r.cells ?? []) terr[c.terrainDefName] = (terr[c.terrainDefName] ?? 0) + 1;
}
const sorted = Object.entries(terr).sort((a, b) => b[1] - a[1]);
console.log('  ' + sorted.map(([k, v]) => `${k}=${v}`).join('  '));
R.evidence.terrainHistogram = Object.fromEntries(sorted);

// 2) 每种地形取一个代表格做 dryRun
const tries = ['Soil', 'Gravel', 'Sand', 'MossyTerrain', 'MarshyTerrain', 'RichSoil', 'Ice'];
const samples = {};
for (let z = 100; z < 170 && Object.keys(samples).length < tries.length; z += 8) {
  const r = await call('rimworld/get_cells_info', { x: 100, z, width: 32, height: 8 });
  for (const c of r.cells ?? []) {
    if (tries.includes(c.terrainDefName) && !samples[c.terrainDefName] && (c.things ?? []).length === 0 && c.zone == null) {
      samples[c.terrainDefName] = { x: c.x, z: c.z };
    }
  }
}
console.log('▶ 各地形样本格：' + JSON.stringify(samples));
R.evidence.terrainSamples = samples;
for (const [t, p] of Object.entries(samples)) {
  const r = await call('rimworld/apply_architect_designator', { designatorId: GROW, x: p.x, z: p.z, width: 1, height: 1, dryRun: true });
  console.log(`  ${t} @(${p.x},${p.z}) accepted=${r.acceptedCellCount} rejected=${r.rejectedCellCount} reason=${r.rejectedCells?.[0]?.reason ?? '-'}`);
  R.evidence[`growingDryRun_${t}`] = { accepted: r.acceptedCellCount, rejected: r.rejectedCellCount, reason: r.rejectedCells?.[0]?.reason ?? null };
  await sleep(60);
}

// 3) 殖民者附近试
const cols = await call('rimworld/list_colonists', { currentMapOnly: true });
const c0 = (cols.colonists ?? [])[0];
console.log('\n▶ 殖民者 ' + c0.fullName + ` @(${c0.position.x},${c0.position.z}) 附近试种`);
const around = await call('rimworld/get_cells_info', { x: c0.position.x - 12, z: c0.position.z - 12, width: 24, height: 24 });
const freeNear = (around.cells ?? []).filter((c) => (c.things ?? []).length === 0 && c.zone == null && c.walkable);
console.log(`  附近空格 ${freeNear.length}`);
for (const c of freeNear.slice(0, 6)) {
  const r = await call('rimworld/apply_architect_designator', { designatorId: GROW, x: c.x, z: c.z, width: 1, height: 1, dryRun: true });
  console.log(`  (${c.x},${c.z}) ${c.terrainDefName} → accepted=${r.acceptedCellCount} reason=${r.rejectedCells?.[0]?.reason ?? '-'}`);
  await sleep(60);
}

// 4) 大范围找第一个 accepted 的格子（网格采样 dryRun 1x1）
console.log('\n▶ 全图网格采样（每 6 格）找 accepted 的 Growing 格');
let found = null; let tried = 0; let accepted = 0;
outer:
for (let z = 2; z < H; z += 6) {
  for (let x = 2; x < W; x += 6) {
    tried++;
    const r = await call('rimworld/apply_architect_designator', { designatorId: GROW, x, z, width: 1, height: 1, dryRun: true });
    if ((r.acceptedCellCount ?? 0) > 0) {
      accepted++;
      if (!found) { found = { x, z }; console.log(`  ✓ 第一个 accepted: (${x},${z})`); }
    }
    if (accepted >= 5) break outer;
  }
}
console.log(`  采样 ${tried} 格，accepted ${accepted}${found ? '' : ' —— 全图没有任何格子被接受'}`);
R.evidence.growingAcceptance = { sampled: tried, accepted, firstFound: found };

// 5) 对照：Stockpile zone 会不会被接受（排除「所有 zone 都不行」）
const STOCK = 'architect-designator:zone:highlight-designator-zoneaddstockpile-resources';
const r2 = await call('rimworld/apply_architect_designator', { designatorId: STOCK, x: 120, z: 130, width: 2, height: 2, dryRun: true });
console.log(`\n▶ 对照 Stockpile dryRun 2x2 @ (120,130): accepted=${r2.acceptedCellCount} rejected=${r2.rejectedCellCount} reason=${r2.rejectedCells?.[0]?.reason ?? '-'}`);
R.evidence.stockpileDryRun = { accepted: r2.acceptedCellCount, rejected: r2.rejectedCellCount, reason: r2.rejectedCells?.[0]?.reason ?? null };

// 6) 也用真实 apply 试一次 stockpile（不是 dryRun），看 zone 是否真的建出来
const r3 = await call('rimworld/apply_architect_designator', { designatorId: STOCK, x: 120, z: 130, width: 2, height: 2 });
console.log(`  真实 apply: success=${r3.success} applied=${r3.appliedCellCount} accepted=${r3.acceptedCellCount}`);
await sleep(300);
const z1 = await call('rimworld/list_zones', { includeEmpty: true });
console.log('  list_zones: ' + JSON.stringify({ count: z1.count, zones: z1.zones }));
save('zones_stockpile', z1);
R.evidence.stockpileApplied = { result: { success: r3.success, applied: r3.appliedCellCount }, zones: z1.zones };
const ccell = await call('rimworld/get_cell_info', { x: 120, z: 130 });
console.log('  cell(120,130).zone = ' + JSON.stringify(ccell.cell.zone));

fs.writeFileSync(path.join(OUT, 'B3.json'), JSON.stringify(R, null, 2), 'utf8');
client.close();
console.log('\n报告：' + path.join(OUT, 'B3.json'));
