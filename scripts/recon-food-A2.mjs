/**
 * recon-food-A2 —— 复核 A3「同格叠放」究竟发生了什么 + 找食物去处。
 * 扫描 152,135 周边 24x12，列出所有带 things 的格子。
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

const r = await call('rimworld/get_cells_info', { x: 144, z: 130, width: 24, height: 14 });
fs.writeFileSync(path.join(OUT, 'dumps', 'cells_around_food.json'), JSON.stringify(r, null, 2), 'utf8');
const withThings = (r.cells ?? []).filter((c) => (c.things ?? []).length);
console.log(`扫描 24x14=336 格，有东西的 ${withThings.length} 格：`);
for (const c of withThings) {
  console.log(`  (${c.x},${c.z}) ${c.terrainDefName} -> ` + c.things.map((t) => `${t.defName}["${t.label}"]`).join(' + '));
}
console.log('\n食物 defName 统计：');
const counts = {};
for (const c of withThings) for (const t of c.things) counts[t.defName] = (counts[t.defName] ?? 0) + 1;
console.log(JSON.stringify(counts));

// 再试一次：同格连续放两种食物，看合并行为
console.log('\n▶ 同格 (160,132) 放 MealSimple x2 然后 Pemmican x2');
const c1 = await call('rimworld/spawn_thing', { defName: 'MealSimple', x: 160, z: 132, stackCount: 2 });
console.log('  1: ' + JSON.stringify(c1).slice(0, 300));
await sleep(200);
const g1 = await call('rimworld/get_cell_info', { x: 160, z: 132 });
console.log('  格1: ' + JSON.stringify(g1.cell.things));
const c2 = await call('rimworld/spawn_thing', { defName: 'Pemmican', x: 160, z: 132, stackCount: 2 });
console.log('  2: ' + JSON.stringify(c2).slice(0, 300));
await sleep(200);
const g2 = await call('rimworld/get_cell_info', { x: 160, z: 132 });
console.log('  格2: ' + JSON.stringify(g2.cell.things));

console.log('\n▶ 同 def 两堆能否共格：(162,132) RawRice x3 两次');
await call('rimworld/spawn_thing', { defName: 'RawRice', x: 162, z: 132, stackCount: 3 });
await sleep(150);
await call('rimworld/spawn_thing', { defName: 'RawRice', x: 162, z: 132, stackCount: 4 });
await sleep(200);
const g3 = await call('rimworld/get_cell_info', { x: 162, z: 132 });
console.log('  ' + JSON.stringify(g3.cell.things));

// 检查 colonist 是否在搬运
const cols = await call('rimworld/list_colonists', { currentMapOnly: true });
console.log('\n▶ 殖民者当前 job：');
for (const c of cols.colonists ?? []) console.log(`  ${c.fullName} @(${c.position?.x},${c.position?.z}) job=${JSON.stringify(c.job)}`);

client.close();
