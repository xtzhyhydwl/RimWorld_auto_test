/**
 * recon-food-A —— A 块：食物数据来源实测。
 *
 *  A1. get_game_info 字段（已在 report2 记录，这里复核）
 *  A2. 生成 3 堆食物 + 1 个营养膏，读 get_cell_info，dump 全部字段
 *  A3. 同格叠放多种食物，看 things[] 是否都列出
 *  A4. get_cells_info 批量计时：16x16(256) / 32x32(1024) / 超限 33x33
 *  A5. 全图扫描外推（样本计时）
 */
import fs from 'node:fs';
import path from 'node:path';
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor, WS } from '../src/launcher.js';

const OUT = path.join(WS, 'runs', 'food-recon');
const DUMP = path.join(OUT, 'dumps');
fs.mkdirSync(DUMP, { recursive: true });

const info = readBridgeInfo(logPathFor('vanilla'));
const { client } = await connectGabp(info, { requestTimeoutMs: 60_000 });
const call = (n, a, o) => client.callTool(n, a, o);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const result = { at: new Date().toISOString(), steps: [], timings: {}, evidence: {} };
let dead = false;
function save(n, o) { fs.writeFileSync(path.join(DUMP, `${n}.json`), JSON.stringify(o, null, 2), 'utf8'); }
function head(o, n = 1500) { const s = typeof o === 'string' ? o : JSON.stringify(o); return s.length > n ? s.slice(0, n) + ` …(+${s.length - n})` : s; }
async function step(name, fn) {
  const s = Date.now();
  process.stdout.write(`\n▶ ${name}\n`);
  try { const v = await fn(); result.steps.push({ name, ok: true, ms: Date.now() - s }); return v; }
  catch (err) {
    const isTimeout = err.code === 'GABP_TIMEOUT';
    result.steps.push({ name, ok: false, ms: Date.now() - s, error: err.message, timeout: isTimeout });
    console.log(`  ✗ ${err.message}`);
    if (isTimeout) { dead = true; throw Object.assign(new Error('BRIDGE_TIMEOUT'), { fatal: true }); }
    return null;
  }
}

const FOODS = ['MealSimple', 'Pemmican', 'RawRice', 'RawPotatoes'];
const BASE = { x: 140, z: 145 };

try {
  // ── 找一块空地：用 get_cells_info 扫一片，选 walkable 且没东西的 ──
  const scan = await step('A0 找空地 (get_cells_info 20x20 @ 130,135)', async () => {
    const t = Date.now();
    const r = await call('rimworld/get_cells_info', { x: 130, z: 135, width: 20, height: 20 });
    result.timings.cells20x20 = Date.now() - t;
    const cells = r.cells ?? [];
    console.log(`  ${cells.length} 格，${result.timings.cells20x20}ms，返回体 ${JSON.stringify(r).length} 字节`);
    save('cells_20x20', r);
    console.log('  第一格 keys: ' + Object.keys(cells[0] ?? {}).join(', '));
    console.log('  ' + head(cells[0], 900));
    return r;
  });
  const empty = (scan?.cells ?? []).filter(
    (c) => c.walkable && (c.things ?? []).length === 0 && c.terrainDefName && !/Water|Marsh|Sand|Rock|Ice/i.test(c.terrainDefName));
  console.log(`  walkable 空格 ${empty.length} 个；候选 ${empty.slice(0, 5).map((c) => `(${c.x},${c.z}) ${c.terrainDefName}`).join('  ')}`);
  const spot = empty.find((c) => c.x > 135 && c.z > 140) ?? empty[0];
  const S = spot ? { x: spot.x, z: spot.z } : BASE;
  console.log(`  使用落点 (${S.x},${S.z}) 地形=${spot?.terrainDefName}`);

  // ── A2 逐格生成食物 ──
  for (let i = 0; i < FOODS.length; i++) {
    const def = FOODS[i];
    const x = S.x + i, z = S.z;
    await step(`A2 spawn_thing ${def} @ (${x},${z}) stackCount=7`, async () => {
      const r = await call('rimworld/spawn_thing', { defName: def, x, z, stackCount: 7 });
      console.log('  ' + head({ success: r?.success, message: r?.message, thingId: r?.thingId, stackCount: r?.stackCount, defName: r?.defName }, 700));
      return r;
    });
    await sleep(120);
    const ci = await step(`A2 get_cell_info (${x},${z})`, () => call('rimworld/get_cell_info', { x, z }));
    console.log('  ' + head(ci?.cell, 2500));
    await sleep(80);
  }
  // 单独读第 1 个格子的完整 JSON（判断有没有隐藏字段）
  save('cell_MealSimple', await call('rimworld/get_cell_info', { x: S.x, z: S.z }));
  save('cell_Pemmican', await call('rimworld/get_cell_info', { x: S.x + 1, z: S.z }));

  // ── A3 同格叠放：先在 (S.x+6,S.z) 放 MealSimple，再放 Pemmican ──
  await step(`A3 同格叠放 MealSimple+Pemmican @ (${S.x + 6},${S.z})`, async () => {
    await call('rimworld/spawn_thing', { defName: 'MealSimple', x: S.x + 6, z: S.z, stackCount: 3 });
    await sleep(120);
    await call('rimworld/spawn_thing', { defName: 'Pemmican', x: S.x + 6, z: S.z, stackCount: 11 });
    await sleep(150);
    const ci = await call('rimworld/get_cell_info', { x: S.x + 6, z: S.z });
    console.log('  ' + head(ci?.cell, 2500));
    save('cell_stacked', ci);
    return ci;
  });

  // ── A4 批量上限与耗时 ──
  await step('A4a get_cells_info 16x16 = 256', async () => {
    const t = Date.now();
    const r = await call('rimworld/get_cells_info', { x: 120, z: 130, width: 16, height: 16 });
    result.timings.cells16x16 = Date.now() - t;
    const bytes = JSON.stringify(r).length;
    result.evidence.cells16x16 = { ms: result.timings.cells16x16, bytes, count: (r.cells ?? []).length, success: r.success };
    console.log(`  ${result.timings.cells16x16}ms  ${bytes} 字节  ${(r.cells ?? []).length} 格`);
    return r;
  });
  await step('A4b get_cells_info 32x32 = 1024（文档上限）', async () => {
    const t = Date.now();
    const r = await call('rimworld/get_cells_info', { x: 120, z: 130, width: 32, height: 32 });
    result.timings.cells32x32 = Date.now() - t;
    const bytes = JSON.stringify(r).length;
    result.evidence.cells32x32 = { ms: result.timings.cells32x32, bytes, count: (r.cells ?? []).length, success: r.success };
    console.log(`  ${result.timings.cells32x32}ms  ${bytes} 字节  ${(r.cells ?? []).length} 格`);
    save('cells_32x32', r);
    return r;
  });
  await step('A4c 超限 33x33 = 1089（看怎么报错）', async () => {
    const r = await call('rimworld/get_cells_info', { x: 120, z: 130, width: 33, height: 33 });
    console.log('  ' + head(r, 700));
    result.evidence.overflow33 = { success: r.success, message: r.message, count: (r.cells ?? []).length };
    return r;
  });
  await step('A4d 最大合法 1024 (64x16)', async () => {
    const t = Date.now();
    const r = await call('rimworld/get_cells_info', { x: 110, z: 130, width: 64, height: 16 });
    result.timings.cells64x16 = Date.now() - t;
    const bytes = JSON.stringify(r).length;
    result.evidence.cells64x16 = { ms: result.timings.cells64x16, bytes, count: (r.cells ?? []).length, success: r.success };
    console.log(`  ${result.timings.cells64x16}ms  ${bytes} 字节  ${(r.cells ?? []).length} 格`);
    return r;
  });

  // ── A5 地图大小：用相机 viewRect + flood_fill 摸边 ──
  await step('A5 探地图边界（jump_camera_to_cell 到极端坐标看返回）', async () => {
    const probes = [];
    for (const [x, z] of [[0, 0], [249, 249], [300, 300], [125, 125]]) {
      const r = await call('rimworld/jump_camera_to_cell', { x, z }).catch((e) => ({ error: e.message }));
      const cs = await call('rimworld/get_camera_state').catch(() => null);
      probes.push({ x, z, jumpOk: r?.success !== false, cameraMapPosition: cs?.mapPosition, viewRect: cs?.viewRect });
      await sleep(80);
    }
    console.log('  ' + head(probes, 1400));
    return probes;
  });

  fs.writeFileSync(path.join(OUT, 'A.json'), JSON.stringify(result, null, 2), 'utf8');
} catch (e) {
  result.fatal = e.message;
  console.log(`\n[FATAL] ${e.message}`);
} finally {
  result.dead = dead;
  result.finishedAt = new Date().toISOString();
  fs.writeFileSync(path.join(OUT, 'A.json'), JSON.stringify(result, null, 2), 'utf8');
  client.close();
  console.log(`\n报告：${path.join(OUT, 'A.json')}${dead ? '  ⚠ 桥接可能已挂死' : ''}`);
}
