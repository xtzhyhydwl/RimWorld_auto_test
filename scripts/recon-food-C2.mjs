/**
 * recon-food-C2 —— Work 标签页 UI（工作优先级可读/可写？）+ 狩猎驱动。
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

// ── 1. Work 标签页 ─────────────────────────────────────────
console.log('▶ 1. 打开 Work 主标签');
await call('rimworld/press_cancel').catch(() => {});
await sleep(250);
const o = await call('rimworld/open_main_tab', { mainTabId: 'main-tab:Work' });
console.log('  open: ' + head({ success: o?.success, message: o?.message, mainTabId: o?.mainTabId, label: o?.label }, 400));
await sleep(1200);
const lay = await call('rimworld/get_ui_layout');
save('ui_layout_work2', lay);
const all = (lay.surfaces ?? []).flatMap((s) => (s.elements ?? []).map((e) => ({ ...e, surface: s.type })));
console.log(`  surfaces: ${(lay.surfaces ?? []).map((s) => `${s.type}(${(s.elements ?? []).length})`).join(' ')}`);
console.log(`  元素 ${all.length}，kind 分布: ` + JSON.stringify([...new Set(all.map((e) => e.kind))]));
const texts = all.map((e) => e.label ?? e.valueText).filter(Boolean);
console.log('  文本: ' + JSON.stringify(texts.slice(0, 100)));
R.evidence.workTab = {
  surfaceTypes: (lay.surfaces ?? []).map((s) => ({ type: s.type, elements: (s.elements ?? []).length, actionable: s.actionableElementCount })),
  elementKinds: [...new Set(all.map((e) => e.kind))],
  labels: texts,
};
// 有没有表示优先级的元素（数字/checkbox/button）
const prioish = all.filter((e) => /priority|Priority|^\d+$/.test(String(e.label ?? '')) || /priority/i.test(String(e.source ?? '')));
console.log(`  疑似优先级元素 ${prioish.length}` + (prioish.length ? '：' + JSON.stringify(prioish.slice(0, 12).map((e) => ({ kind: e.kind, label: e.label, src: e.source, rect: e.screenRect, actionable: e.actionable }))) : ''));
R.evidence.workTabPriorityElements = prioish.slice(0, 30).map((e) => ({ kind: e.kind, label: e.label, source: e.source, actionable: e.actionable, targetId: e.targetId }));
// 所有 button 类元素的 source
const btns = all.filter((e) => e.actionable);
console.log(`  可点元素 ${btns.length}：` + JSON.stringify([...new Set(btns.map((b) => b.source))]));
R.evidence.workTabActionableSources = [...new Set(btns.map((b) => b.source))];
R.evidence.workTabActionableSample = btns.slice(0, 20).map((b) => ({ kind: b.kind, source: b.source, label: b.label, rect: b.screenRect, targetId: b.targetId }));
await call('rimworld/close_main_tab', { mainTabId: 'main-tab:Work' }).catch(() => {});
await sleep(400);

// ── 2. 狩猎：找动物 → 施 Hunt designator ────────────────────
console.log('\n▶ 2. 狩猎');
const HUNT = 'architect-designator:orders:highlight-designator-tutortagnotset-4';
// 找野生/动物 pawn
const mtiTry = [];
const colsAll = await call('rimworld/list_colonists', { currentMapOnly: true });
console.log(`  list_colonists(PlayerColony) ${(colsAll.colonists ?? []).length}；它只列玩家殖民者`);
// 扫描地图找动物：用 spawn Pawn 造一只，保证一定有
const spawnPt = { x: 130, z: 140 };
const sp = await call('rimworld/execute_debug_action', { path: 'Actions\\Spawn Pawn...\\Muffalo', x: spawnPt.x, z: spawnPt.z });
console.log('  spawn Muffalo: ' + head({ success: sp?.success, message: sp?.message }, 400));
await sleep(600);
// 扫附近找它
const area = await call('rimworld/get_cells_info', { x: spawnPt.x - 6, z: spawnPt.z - 6, width: 13, height: 13 });
const animals = [];
for (const c of area.cells ?? []) for (const t of c.things ?? []) if (/Muffalo|Animal/i.test(t.defName ?? '') || /Muffalo/i.test(t.label ?? '')) animals.push({ x: c.x, z: c.z, ...t });
console.log('  找到动物: ' + JSON.stringify(animals));
R.evidence.spawnedAnimal = animals;

if (animals.length) {
  const A = animals[0];
  // 2a) 用 Hunt designator 施加到动物格
  const d1 = await call('rimworld/apply_architect_designator', { designatorId: HUNT, x: A.x, z: A.z, width: 1, height: 1, dryRun: true });
  console.log('  Hunt dryRun: ' + head({ success: d1.success, accepted: d1.acceptedCellCount, rejected: d1.rejectedCellCount, reason: d1.rejectedCells?.[0]?.reason }, 500));
  R.evidence.huntDryRun = { success: d1.success, accepted: d1.acceptedCellCount, rejected: d1.rejectedCellCount, reason: d1.rejectedCells?.[0]?.reason ?? null };
  const d2 = await call('rimworld/apply_architect_designator', { designatorId: HUNT, x: A.x, z: A.z, width: 1, height: 1 });
  console.log('  Hunt apply: ' + head({ success: d2.success, accepted: d2.acceptedCellCount, applied: d2.appliedCellCount, reason: d2.rejectedCells?.[0]?.reason }, 500));
  R.evidence.huntApply = { success: d2.success, accepted: d2.acceptedCellCount, applied: d2.appliedCellCount };
  await sleep(500);
  const ci = await call('rimworld/get_cell_info', { x: A.x, z: A.z });
  console.log('  回读 cell: ' + head({ things: ci.cell.things, designations: ci.cell.designations, designationCount: ci.cell.designationCount }, 900));
  R.evidence.huntCellAfter = { designations: ci.cell.designations, designationCount: ci.cell.designationCount, things: ci.cell.things };

  // 2b) 右键动物（列出右键菜单）
  const rc = await call('rimworld/right_click_cell', { x: A.x, z: A.z }).catch((e) => ({ error: e.message }));
  console.log('  right_click_cell: ' + head(rc, 900));
  R.evidence.rightClickAnimal = rc && { success: rc.success, floatMenuOpen: rc.floatMenuOpen, optionCount: rc.optionCount, options: rc.options };
  await sleep(700);
  const uist = await call('rimworld/get_ui_state');
  console.log('  ui after right click: floatMenu=' + uist.floatMenuOpen + ' top=' + uist.topWindowType);
  const ctx = await call('rimworld/get_context_menu_options').catch((e) => ({ error: e.message }));
  console.log('  context menu options: ' + head(ctx, 1500));
  R.evidence.contextMenuAnimal = ctx;
  const st = await call('rimworld/get_screen_targets');
  const cm = st?.targets?.contextMenu;
  console.log('  screen targets.contextMenu: ' + head(cm, 1500));
  R.evidence.contextMenuTargets = cm;
  save('screen_targets_animal', st);
  if (cm) {
    console.log('  keys: ' + Object.keys(cm).join(', '));
    const opts = cm.options ?? cm.items ?? [];
    console.log('  options: ' + JSON.stringify(opts).slice(0, 1200));
    R.evidence.contextMenuOptions = opts;
  }
}

fs.writeFileSync(path.join(OUT, 'C2.json'), JSON.stringify(R, null, 2), 'utf8');
client.close();
console.log('\n报告：' + path.join(OUT, 'C2.json'));
