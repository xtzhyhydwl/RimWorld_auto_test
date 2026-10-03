/**
 * recon-food-B6 —— 换作物 + 狩猎（C 块）+ 生长进度观测。
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

const Z = { x: 19, z: 10, w: 4, h: 4 }; // Zone_1

async function uiTexts() {
  const lay = await call('rimworld/get_ui_layout');
  return { lay, texts: (lay.surfaces ?? []).flatMap((s) => (s.elements ?? []).map((e) => ({ kind: e.kind, label: e.label, valueText: e.valueText, targetId: e.targetId })).filter((e) => e.label || e.valueText)) };
}

// ── 1. 选中 zone 并执行 SetPlantToGrow gizmo ────────────────
console.log('▶ 1. 选中 Zone_1，执行 SetPlantToGrow gizmo');
await call('rimworld/press_cancel');
await sleep(250);
await call('rimworld/click_cell', { x: Z.x + 1, z: Z.z + 1, button: 'left' });
await sleep(400);
const gz = await call('rimworld/list_selected_gizmos');
const plantGizmo = (gz.gizmos ?? []).find((g) => /SetPlantToGrow/.test(g.gizmoType ?? '') || /^Plant:/.test(g.label ?? ''));
console.log('  找到 gizmo: ' + JSON.stringify({ id: plantGizmo?.id, label: plantGizmo?.label, type: plantGizmo?.gizmoType }));
R.evidence.plantGizmo = { id: plantGizmo?.id, label: plantGizmo?.label, type: plantGizmo?.gizmoType };
const eg = await call('rimworld/execute_gizmo', { gizmoId: plantGizmo.id });
console.log('  execute_gizmo: ' + head(eg, 1500));
R.evidence.executeGizmo = { success: eg?.success, message: eg?.message, keys: Object.keys(eg ?? {}) };
await sleep(700);
const after = await uiTexts();
console.log('  UI 文本: ' + JSON.stringify(after.texts.map((t) => t.label ?? t.valueText).slice(0, 60)));
save('ui_layout_plantmenu', after.lay);
R.evidence.plantMenuTexts = after.texts.map((t) => t.label ?? t.valueText);
const uist = await call('rimworld/get_ui_state');
console.log('  ui_state: ' + head({ windowCount: uist.windowCount, top: uist.topWindowType, floatMenu: uist.floatMenuOpen, focused: uist.focusedWindowType }, 500));
R.evidence.uiStateAfterPlantGizmo = { windowCount: uist.windowCount, topWindowType: uist.topWindowType, floatMenuOpen: uist.floatMenuOpen, focusedWindowType: uist.focusedWindowType };
const ctx = await call('rimworld/get_context_menu_options');
console.log('  context menu: ' + head(ctx, 1200));
R.evidence.contextMenuAfterPlantGizmo = ctx;
const st = await call('rimworld/get_screen_targets');
console.log('  screen targets: ' + head(st, 1200));
save('screen_targets_plantmenu', st);

fs.writeFileSync(path.join(OUT, 'B6.json'), JSON.stringify(R, null, 2), 'utf8');
client.close();
console.log('\n报告：' + path.join(OUT, 'B6.json'));
