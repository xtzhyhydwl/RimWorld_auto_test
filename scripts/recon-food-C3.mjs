/**
 * recon-food-C3 —— 补完 C 块：
 *  1. 右键菜单（狩猎的另一条路）—— get_context_menu_options / screen targets
 *  2. run_lua 有没有暴露全局 Game/Find/DefDatabase（决定要不要写 DLL）
 *  3. Work 标签页的优先级格子有没有可读状态
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
const head = (o, n = 1800) => { const s = typeof o === 'string' ? o : JSON.stringify(o); return s.length > n ? s.slice(0, n) + ` …(+${s.length - n})` : s; };
const save = (n, o) => fs.writeFileSync(path.join(OUT, 'dumps', `${n}.json`), JSON.stringify(o, null, 2), 'utf8');
const R = { at: new Date().toISOString(), evidence: {} };

// ── 1. 右键菜单 ────────────────────────────────────────────
console.log('▶ 1. 右键动物→菜单');
await call('rimworld/press_cancel').catch(() => {});
await sleep(300);
// 找 Muffalo
const all = await call('rimworld/get_cells_info', { x: 24, z: 30, width: 32, height: 32 });
let animal = null;
for (const c of all.cells ?? []) for (const t of c.things ?? []) if (t.defName === 'Muffalo') animal = { x: c.x, z: c.z, t };
if (!animal) {
  // 重新生成一只
  const sp = await call('rimworld/execute_debug_action', { path: 'Actions\\Spawn Pawn...\\Muffalo', x: 140, z: 140 });
  console.log('  spawn Muffalo: ' + JSON.stringify({ success: sp?.success }));
  await sleep(800);
  const a2 = await call('rimworld/get_cells_info', { x: 134, z: 134, width: 13, height: 13 });
  for (const c of a2.cells ?? []) for (const t of c.things ?? []) if (t.defName === 'Muffalo') animal = { x: c.x, z: c.z, t };
}
console.log('  动物: ' + JSON.stringify(animal && { x: animal.x, z: animal.z, def: animal.t.defName }));
R.evidence.animal = animal ? { x: animal.x, z: animal.z, defName: animal.t.defName } : null;

if (animal) {
  // 1a) 选中殖民者再右键
  const cols = await call('rimworld/list_colonists', { currentMapOnly: true });
  await call('rimworld/select_pawn', { pawnId: (cols.colonists ?? [])[0].pawnId });
  await sleep(300);
  const rc = await call('rimworld/right_click_cell', { x: animal.x, z: animal.z }).catch((e) => ({ error: e.message }));
  console.log('  right_click_cell(selected pawn): ' + head(rc, 800));
  R.evidence.rightClick = { success: rc?.success, message: rc?.message, optionCount: rc?.optionCount, options: rc?.options, floatMenuOpen: rc?.floatMenuOpen };
  await sleep(800);
  const uist = await call('rimworld/get_ui_state');
  console.log('  UI: floatMenu=' + uist.floatMenuOpen + ' top=' + uist.topWindowType + ' windows=' + uist.windowCount + ' [types: ' + (uist.windows ?? []).map((w) => w.type).join(',') + ']');
  R.evidence.uiAfterRightClick = { floatMenuOpen: uist.floatMenuOpen, topWindowType: uist.topWindowType, windowTypes: (uist.windows ?? []).map((w) => w.type) };
  const lay = await call('rimworld/get_ui_layout');
  const labels = (lay.surfaces ?? []).flatMap((s) => (s.elements ?? []).map((e) => e.label).filter(Boolean));
  console.log('  UI 文本: ' + JSON.stringify(labels.slice(0, 60)));
  R.evidence.rightClickUiTexts = labels;
  save('ui_layout_rightclick_animal', lay);

  // 1b) open_context_menu
  const oc = await call('rimworld/open_context_menu', { x: animal.x, z: animal.z }).catch((e) => ({ error: e.message }));
  console.log('  open_context_menu: ' + head(oc, 1200));
  R.evidence.openContextMenu = oc;
  await sleep(600);
  const gco = await call('rimworld/get_context_menu_options').catch((e) => ({ error: e.message }));
  console.log('  get_context_menu_options: ' + head(gco, 1500));
  R.evidence.getContextMenuOptions = gco;
  const st = await call('rimworld/get_screen_targets');
  console.log('  screen_targets.contextMenu: ' + head(st?.targets?.contextMenu, 1500));
  R.evidence.contextMenuTargets = st?.targets?.contextMenu;
  await call('rimworld/close_context_menu').catch(() => {});

  // 1c) 狩猎：确认之前的 Hunt designation 还在不在
  const ci = await call('rimworld/get_cell_info', { x: animal.x, z: animal.z });
  console.log('  该格 designations: ' + JSON.stringify(ci.cell.designations));
}

// ── 2. run_lua 全局探测 ────────────────────────────────────
console.log('\n▶ 2. run_lua 全局对象探测');
const probes = {
  globalGame: 'return Game',
  globalFind: 'return Find',
  globalDefDb: 'return DefDatabase',
  globalMap: 'return Find.CurrentMap',
  builtinPairs: 'local t = {}\nreturn ipairs',
};
for (const [k, src] of Object.entries(probes)) {
  const r = await call('rimbridge/run_lua', { luaSource: src, includeStepResults: false }).catch((e) => ({ error: e.message }));
  const msg = r?.error?.message ?? r?.message ?? JSON.stringify(r).slice(0, 200);
  console.log(`  ${k}: success=${r?.success} ${head(msg, 260)}`);
  R.evidence['lua_' + k] = { success: r?.success, message: r?.error?.message ?? r?.message ?? null };
}
// run_lua 有没有暴露能跑任意 capability 的旁路
const ref = await call('rimbridge/get_lua_reference');
R.evidence.luaReferenceKeys = Object.keys(ref ?? {});
console.log('  lua reference keys: ' + Object.keys(ref ?? {}).join(', '));
console.log('  limits: ' + head(ref?.limits ?? ref?.rules ?? ref?.restrictions, 900));
save('lua_reference_full', ref);

// ── 3. Work 标签页优先级格子状态 ────────────────────────────
console.log('\n▶ 3. Work 标签页优先级状态');
await call('rimworld/press_cancel').catch(() => {});
await call('rimworld/open_main_tab', { mainTabId: 'main-tab:Work' });
await sleep(1200);
const wl = await call('rimworld/get_ui_layout');
save('ui_layout_work3', wl);
const wsurf = (wl.surfaces ?? []).find((s) => /MainTabWindow_Work/.test(s.type ?? ''));
const cells = (wsurf?.elements ?? []).filter((e) => e.actionable && !e.label);
console.log(`  Work surface 元素 ${(wsurf?.elements ?? []).length}，无文字可点元素 ${cells.length}`);
const states = [...new Set(cells.map((c) => JSON.stringify({ kind: c.kind, src: c.source, checked: c.isChecked, disabled: c.disabled, value: c.valueText })))];
console.log('  状态组合: ' + JSON.stringify(states).slice(0, 900));
R.evidence.workCellStates = states;
// 试着点一个格子看会不会变
if (cells.length > 30) {
  const probe = cells[35];
  const before = await call('rimworld/get_ui_layout');
  const r = await call('rimworld/click_ui_target', { targetId: probe.targetId, timeoutMs: 5000 }).catch((e) => ({ error: e.message }));
  console.log('  试点格子 ' + probe.targetId + ' rect=' + JSON.stringify(probe.rect) + ' → ' + head(r, 300));
  await sleep(500);
  const after = await call('rimworld/get_ui_layout');
  const asurf = (after.surfaces ?? []).find((s) => /MainTabWindow_Work/.test(s.type ?? ''));
  const beforeSurf = (before.surfaces ?? []).find((s) => /MainTabWindow_Work/.test(s.type ?? ''));
  console.log(`  点前元素 ${(beforeSurf?.elements ?? []).length} → 点后 ${(asurf?.elements ?? []).length}`);
  R.evidence.workCellClickProbe = { targetId: probe.targetId, rect: probe.rect, result: r?.success !== false, beforeCount: (beforeSurf?.elements ?? []).length, afterCount: (asurf?.elements ?? []).length };
  save('ui_layout_work_after_click', after);
  // 截图看看颜色
  const shot = await call('rimworld/take_screenshot', { suppressMessage: true, fileName: 'recon_worktab' });
  console.log('  截图: ' + shot?.path);
  R.evidence.workTabShot = shot?.path;
}
await call('rimworld/close_main_tab', {}).catch(() => {});

fs.writeFileSync(path.join(OUT, 'C3.json'), JSON.stringify(R, null, 2), 'utf8');
client.close();
console.log('\n报告：' + path.join(OUT, 'C3.json'));
