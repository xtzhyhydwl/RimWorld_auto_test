/**
 * recon-food-C4 —— 补完：Work 标签页优先级格子 + debug 右键菜单 + execute_context_menu_option。
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
const head = (o, n = 1800) => { const s = o === undefined || o === null ? String(o) : (typeof o === 'string' ? o : JSON.stringify(o)); return s.length > n ? s.slice(0, n) + ` …(${s.length - n} more)` : s; };
const save = (n, o) => fs.writeFileSync(path.join(OUT, 'dumps', `${n}.json`), JSON.stringify(o, null, 2), 'utf8');
const R = { at: new Date().toISOString(), evidence: {} };

// ── 1. Work 标签页 ─────────────────────────────────────────
console.log('▶ 1. Work 标签页优先级格子');
await call('rimworld/press_cancel').catch(() => {});
await sleep(300);
const o = await call('rimworld/open_main_tab', { mainTabId: 'main-tab:Work' });
console.log('  open: ' + JSON.stringify({ success: o?.success, message: o?.message }));
await sleep(1300);
const wl = await call('rimworld/get_ui_layout');
save('ui_layout_work_c4', wl);
const wsurf = (wl.surfaces ?? []).find((s) => /MainTabWindow_Work/.test(s.type ?? ''));
const els = wsurf?.elements ?? [];
const cells = els.filter((e) => e.actionable && !e.label);
console.log(`  Work surface 元素 ${els.length}，无文字可点元素 ${cells.length}`);
const states = [...new Set(cells.map((c) => JSON.stringify({ kind: c.kind, src: c.source, checked: c.isChecked, disabled: c.disabled, value: c.valueText })))];
console.log('  状态组合: ' + states.join(' | '));
R.evidence.workCellStates = states;
// tooltip 里可能有优先级数字：用 set_hover_target 悬停一个格子
if (cells.length > 40) {
  const probe = cells[40];
  await call('rimworld/set_hover_target', { targetId: probe.targetId, settleMs: 900 }).catch(() => {});
  await sleep(1200);
  const hl = await call('rimworld/get_ui_layout');
  const htexts = (hl.surfaces ?? []).flatMap((s) => (s.elements ?? []).map((e) => e.label ?? e.valueText).filter(Boolean));
  const tooltipish = htexts.filter((t) => /priorit|Priority|^\d$|work type/i.test(t));
  console.log('  hover 后含 priority 的文本: ' + JSON.stringify(tooltipish.slice(0, 12)));
  R.evidence.workHoverTooltip = tooltipish.slice(0, 20);
  await call('rimworld/clear_hover_target').catch(() => {});
  save('ui_layout_work_hover', hl);
  // 点它
  const before = els.length;
  const r = await call('rimworld/click_ui_target', { targetId: probe.targetId, timeoutMs: 5000 }).catch((e) => ({ error: e.message }));
  console.log('  点格子 ' + probe.targetId + ' → ' + head({ success: r?.success, message: r?.message, error: r?.error }, 300));
  await sleep(700);
  const al = await call('rimworld/get_ui_layout');
  const asurf = (al.surfaces ?? []).find((s) => /MainTabWindow_Work/.test(s.type ?? ''));
  console.log(`  点后元素数 ${(asurf?.elements ?? []).length}（点前 ${before}）`);
  R.evidence.workCellClick = { targetId: probe.targetId, rect: probe.rect, before, after: (asurf?.elements ?? []).length };
  const shot = await call('rimworld/take_screenshot', { suppressMessage: true, fileName: 'recon_worktab' });
  console.log('  截图: ' + (shot?.path ?? '失败'));
  R.evidence.workTabShot = shot?.path ?? null;
}
await call('rimworld/close_main_tab', {}).catch(() => {});
await sleep(400);

// ── 2. debug 右键菜单 ──────────────────────────────────────
console.log('\n▶ 2. debug 右键菜单（open_context_menu mode=debug）');
// 找 Muffalo
let animal = null;
for (const reg of [[134, 134], [24, 30]]) {
  const a2 = await call('rimworld/get_cells_info', { x: reg[0], z: reg[1], width: 20, height: 20 });
  for (const c of a2.cells ?? []) for (const t of c.things ?? []) if (t.defName === 'Muffalo') { animal = { x: c.x, z: c.z }; break; }
  if (animal) break;
}
console.log('  动物: ' + JSON.stringify(animal));
if (animal) {
  for (const mode of ['debug']) {
    const oc = await call('rimworld/open_context_menu', { x: animal.x, z: animal.z, mode }).catch((e) => ({ error: e.message }));
    console.log(`  open_context_menu(${mode}): optionCount=${oc?.optionCount} ` + head(oc?.options, 900));
    R.evidence['openContextMenu_' + mode] = { optionCount: oc?.optionCount, options: oc?.options };
    await sleep(500);
    const g = await call('rimworld/get_context_menu_options').catch((e) => ({ error: e.message }));
    console.log('    get_context_menu_options: ' + head(g?.options ?? g, 1200));
    R.evidence['ctxOptions_' + mode] = g?.options ?? g;
    const st = await call('rimworld/get_screen_targets');
    console.log('    screenTargets.contextMenu: ' + head(st?.targets?.contextMenu, 1200));
    R.evidence['screenTargets_' + mode] = st?.targets?.contextMenu;
    save('screen_targets_ctx_' + mode, st);
    const lay = await call('rimworld/get_ui_layout');
    const labels = (lay.surfaces ?? []).flatMap((s) => (s.elements ?? []).map((e) => e.label).filter(Boolean));
    console.log('    UI 文本: ' + JSON.stringify(labels.slice(0, 50)));
    R.evidence['ctxUiTexts_' + mode] = labels;
    save('ui_layout_ctx_' + mode, lay);
    await call('rimworld/close_context_menu').catch(() => {});
    await sleep(300);
  }
}

fs.writeFileSync(path.join(OUT, 'C4.json'), JSON.stringify(R, null, 2), 'utf8');
client.close();
console.log('\n报告：' + path.join(OUT, 'C4.json'));
