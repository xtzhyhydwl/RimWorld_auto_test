/**
 * recon-food-B7 —— 在 FloatMenu 里点 "Rice plant"，回读 zone 作物（验证换作物闭环）。
 */
import fs from 'node:fs';
import path from 'node:path';
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor, WS } from '../src/launcher.js';
import { clickRowByLabel, layout } from '../src/ui.js';

const OUT = path.join(WS, 'runs', 'food-recon');
const info = readBridgeInfo(logPathFor('vanilla'));
const { client } = await connectGabp(info, { requestTimeoutMs: 300_000 });
const call = (n, a, o) => client.callTool(n, a, o);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const head = (o, n = 2500) => { const s = typeof o === 'string' ? o : JSON.stringify(o); return s.length > n ? s.slice(0, n) + ` …(+${s.length - n})` : s; };
const save = (n, o) => fs.writeFileSync(path.join(OUT, 'dumps', `${n}.json`), JSON.stringify(o, null, 2), 'utf8');
const R = { at: new Date().toISOString(), evidence: {} };
const Z = { x: 19, z: 10 };

// 确认菜单还开着
const uist = await call('rimworld/get_ui_state');
console.log('▶ 当前 UI: windowCount=' + uist.windowCount + ' top=' + uist.topWindowType + ' floatMenu=' + uist.floatMenuOpen);
if (!uist.floatMenuOpen) {
  console.log('  菜单已关，重新打开（选中 zone → execute_gizmo）');
  await call('rimworld/press_cancel'); await sleep(250);
  await call('rimworld/click_cell', { x: Z.x + 1, z: Z.z + 1, button: 'left' }); await sleep(400);
  const gz = await call('rimworld/list_selected_gizmos');
  const g = (gz.gizmos ?? []).find((x) => /SetPlantToGrow/.test(x.gizmoType ?? ''));
  await call('rimworld/execute_gizmo', { gizmoId: g.id });
  await sleep(700);
}
const lay = await layout(client);
const fm = (lay.surfaces ?? []).filter((s) => /FloatMenu/.test(s.type ?? ''));
console.log('  FloatMenu surfaces: ' + fm.length);
for (const s of fm) {
  console.log('   surface ' + s.type + ' type=' + s.kind + ' elements=' + (s.elements ?? []).length);
  for (const e of s.elements ?? []) if (e.label) console.log(`     [${e.kind}] "${e.label}" rect=${JSON.stringify(e.screenRect)}`);
}
save('ui_layout_floatmenu', lay);

const r = await clickRowByLabel(client, 'Rice plant', { surfaceType: 'FloatMenu', exact: true });
console.log('  clickRowByLabel: ' + JSON.stringify(r));
R.evidence.clickRice = r;
await sleep(800);
const uist2 = await call('rimworld/get_ui_state');
console.log('  click 后: windowCount=' + uist2.windowCount + ' top=' + uist2.topWindowType + ' floatMenu=' + uist2.floatMenuOpen);
R.evidence.uiStateAfterClick = { windowCount: uist2.windowCount, topWindowType: uist2.topWindowType, floatMenuOpen: uist2.floatMenuOpen };

// 回读 gizmo label
const gz2 = await call('rimworld/list_selected_gizmos');
const pg = (gz2.gizmos ?? []).find((g) => /SetPlantToGrow/.test(g.gizmoType ?? ''));
console.log('  ★ 回读 Plant gizmo label = ' + JSON.stringify(pg?.label));
R.evidence.plantGizmoAfter = pg ? { label: pg.label, type: pg.gizmoType } : null;

// UI 文本
const lay2 = await call('rimworld/get_ui_layout');
const texts = (lay2.surfaces ?? []).flatMap((s) => (s.elements ?? []).map((e) => e.label ?? e.valueText).filter(Boolean));
console.log('  UI 文本: ' + JSON.stringify(texts.slice(0, 40)));
R.evidence.uiTextsAfter = texts;

// ── 生长观测：zone 里的植物 ────────────────────────────────
console.log('\n▶ 生长观测');
const cells = await call('rimworld/get_cells_info', { x: Z.x, z: Z.z, width: 4, height: 4 });
const plants = (cells.cells ?? []).filter((c) => (c.things ?? []).some((t) => /Plant_/.test(t.defName)));
console.log(`  zone 里有植物的格：${plants.length}`);
for (const c of plants) console.log(`   (${c.x},${c.z}) ${JSON.stringify(c.things)}`);
R.evidence.plantsInZone = plants.map((c) => ({ x: c.x, z: c.z, things: c.things }));

// 手动种一株水稻进区，然后按作物需要的天数推进
const px = Z.x + 1, pz = Z.z + 1;
const hasPlant = plants.some((c) => c.x === px && c.z === pz);
if (!hasPlant) {
  const sp = await call('rimworld/spawn_thing', { defName: 'Plant_Rice', x: px, z: pz });
  console.log('  spawn Plant_Rice @ (' + px + ',' + pz + '): ' + JSON.stringify({ success: sp.success, thingId: sp.thingId }));
}
await sleep(300);
// 读初始 inspect
await call('rimworld/press_cancel'); await sleep(200);
await call('rimworld/click_cell', { x: px, z: pz, button: 'left' }); await sleep(500);
let l = await call('rimworld/get_ui_layout');
let tx = (l.surfaces ?? []).flatMap((s) => (s.elements ?? []).map((e) => e.label ?? e.valueText).filter(Boolean));
console.log('  T0 inspect 文本: ' + JSON.stringify(tx.filter((t) => /growth|grow|matur|%|rice|plant|harvest|sow/i.test(t)).slice(0, 20)));
R.evidence.plantInspectT0 = tx;
save('ui_layout_plant_T0', l);

// 推进 15000 tick（水稻生长期约 3 天=180000 tick，这里抽 15000）
const t0 = Date.now();
const s1 = await call('rimworld/step_game_ticks', { ticks: 15000, pauseFirst: true, timeoutMs: 800_000 }, { timeoutMs: 880_000 });
console.log(`  step 15000: status=${s1.status} advanced=${s1.advancedTicks} 耗时 ${Date.now() - t0}ms`);
await sleep(400);
await call('rimworld/click_cell', { x: px, z: pz, button: 'left' }); await sleep(500);
l = await call('rimworld/get_ui_layout');
tx = (l.surfaces ?? []).flatMap((s) => (s.elements ?? []).map((e) => e.label ?? e.valueText).filter(Boolean));
console.log('  T+15000 inspect 文本: ' + JSON.stringify(tx.filter((t) => /growth|grow|matur|%|rice|plant|harvest|sow/i.test(t)).slice(0, 20)));
R.evidence.plantInspectT15000 = tx;
save('ui_layout_plant_T15000', l);
const c2 = await call('rimworld/get_cell_info', { x: px, z: pz });
console.log('  cell: ' + JSON.stringify(c2.cell.things));
R.evidence.plantCellT15000 = c2.cell;

fs.writeFileSync(path.join(OUT, 'B7.json'), JSON.stringify(R, null, 2), 'utf8');
client.close();
console.log('\n报告：' + path.join(OUT, 'B7.json'));
