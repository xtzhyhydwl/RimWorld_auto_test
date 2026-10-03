/**
 * recon-food-B8 —— FloatMenu 行点击的可靠做法（label 与 button rect 不重合）。
 * 并用它把种植区作物改成 Rice plant，回读验证。
 */
import fs from 'node:fs';
import path from 'node:path';
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor, WS } from '../src/launcher.js';
import { layout } from '../src/ui.js';

const OUT = path.join(WS, 'runs', 'food-recon');
const info = readBridgeInfo(logPathFor('vanilla'));
const { client } = await connectGabp(info, { requestTimeoutMs: 300_000 });
const call = (n, a, o) => client.callTool(n, a, o);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const save = (n, o) => fs.writeFileSync(path.join(OUT, 'dumps', `${n}.json`), JSON.stringify(o, null, 2), 'utf8');
const R = { at: new Date().toISOString(), evidence: {} };
const Z = { x: 19, z: 10 };

/** FloatMenu 行点击：找 label → 找「y 覆盖 label 中心、x 覆盖 label 中心」的 actionable 元素 */
async function clickMenuRow(labelText) {
  const snap = await layout(client);
  const surfaces = (snap.surfaces ?? []).filter((s) => /FloatMenu/.test(s.type ?? ''));
  const all = surfaces.flatMap((s) => s.elements ?? []);
  const label = all.find((e) => e.label === labelText || e.label?.startsWith(labelText));
  if (!label) return { clicked: false, reason: 'label not found', labels: all.map((e) => e.label).filter(Boolean) };
  const r = label.screenRect;
  const cx = r.x + r.width / 2, cy = r.y + r.height / 2;
  const cands = all.filter((e) => e.actionable && e.screenRect &&
    e.screenRect.y <= cy && e.screenRect.y + e.screenRect.height >= cy &&
    e.screenRect.x <= cx && e.screenRect.x + e.screenRect.width >= cx);
  if (!cands.length) return { clicked: false, reason: 'no actionable covering label', rect: r };
  // 取面积最小的那个（最贴近行的按钮）
  cands.sort((a, b) => a.screenRect.width * a.screenRect.height - b.screenRect.width * b.screenRect.height);
  const target = cands[0];
  const res = await call('rimworld/click_ui_target', { targetId: target.targetId, timeoutMs: 8000 });
  return { clicked: res?.success !== false, targetId: target.targetId, kind: target.kind, rect: target.screenRect, raw: { success: res?.success, message: res?.message } };
}

// 确保菜单开着
let uist = await call('rimworld/get_ui_state');
if (!uist.floatMenuOpen) {
  await call('rimworld/press_cancel'); await sleep(250);
  await call('rimworld/click_cell', { x: Z.x + 1, z: Z.z + 1, button: 'left' }); await sleep(500);
  const gz = await call('rimworld/list_selected_gizmos');
  const g = (gz.gizmos ?? []).find((x) => /SetPlantToGrow/.test(x.gizmoType ?? ''));
  await call('rimworld/execute_gizmo', { gizmoId: g.id });
  await sleep(800);
}
console.log('▶ 点 FloatMenu 行 "Rice plant"');
const r1 = await clickMenuRow('Rice plant');
console.log('  ' + JSON.stringify(r1));
R.evidence.clickRiceRow = r1;
await sleep(900);
uist = await call('rimworld/get_ui_state');
console.log('  点后 UI: floatMenu=' + uist.floatMenuOpen + ' windowCount=' + uist.windowCount);
R.evidence.uiStateAfter = { floatMenuOpen: uist.floatMenuOpen, windowCount: uist.windowCount, topWindowType: uist.topWindowType };

// 回读 gizmo
const gz2 = await call('rimworld/list_selected_gizmos');
const pg = (gz2.gizmos ?? []).find((g) => /SetPlantToGrow/.test(g.gizmoType ?? ''));
console.log('  ★ Plant gizmo label = ' + JSON.stringify(pg?.label));
R.evidence.plantGizmoAfter = pg?.label ?? null;

// 回读 inspect 文本
const l2 = await call('rimworld/get_ui_layout');
const texts = (l2.surfaces ?? []).flatMap((s) => (s.elements ?? []).map((e) => e.label ?? e.valueText).filter(Boolean));
console.log('  inspect 文本: ' + JSON.stringify(texts.filter((t) => /Size:|Growing|sowing|Plant:/i.test(t))));
R.evidence.inspectAfter = texts;

// 推进 6000 tick 看是否开始播种/生长（白天）
console.log('\n▶ 推进 6000 tick 后看 zone 里出什么');
const t0 = Date.now();
const s = await call('rimworld/step_game_ticks', { ticks: 6000, pauseFirst: true, timeoutMs: 600_000 }, { timeoutMs: 660_000 });
console.log(`  step: status=${s.status} advanced=${s.advancedTicks} ${Date.now() - t0}ms  gameTick ${s.startTicksGame}→${s.endTicksGame}`);
await sleep(400);
const cells = await call('rimworld/get_cells_info', { x: Z.x, z: Z.z, width: 4, height: 4 });
const plantsNow = (cells.cells ?? []).filter((c) => (c.things ?? []).length);
console.log(`  zone 里有东西的格 ${plantsNow.length}`);
for (const c of plantsNow) console.log(`   (${c.x},${c.z}) ${JSON.stringify(c.things.map((t) => t.defName))}`);
R.evidence.zoneCellsAfter = plantsNow.map((c) => ({ x: c.x, z: c.z, defs: c.things.map((t) => t.defName) }));
save('cells_zone_after_rice', cells);

// 选一株读生长
if (plantsNow.length) {
  const p = plantsNow[0];
  await call('rimworld/press_cancel'); await sleep(200);
  await call('rimworld/click_cell', { x: p.x, z: p.z, button: 'left' }); await sleep(500);
  const l3 = await call('rimworld/get_ui_layout');
  const t3 = (l3.surfaces ?? []).flatMap((x) => (x.elements ?? []).map((e) => e.label ?? e.valueText).filter(Boolean));
  console.log('  植物 inspect: ' + JSON.stringify(t3.filter((t) => /grown|growth|matur|harvest|sow/i.test(t))));
  R.evidence.plantInspect = t3;
}

fs.writeFileSync(path.join(OUT, 'B8.json'), JSON.stringify(R, null, 2), 'utf8');
client.close();
console.log('\n报告：' + path.join(OUT, 'B8.json'));
