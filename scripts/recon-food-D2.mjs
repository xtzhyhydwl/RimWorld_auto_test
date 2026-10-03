/**
 * recon-food-D2 —— 两个卡点定位：
 *  A) step_game_ticks 大 tick 数是否被工具默认 timeoutMs=10000 截断
 *  B) 选中植物读 "% grown" 的正确姿势（先清掉设计器选择）
 */
import fs from 'node:fs';
import path from 'node:path';
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor, WS } from '../src/launcher.js';

const OUT = path.join(WS, 'runs', 'food-recon');
const info = readBridgeInfo(logPathFor('vanilla'));
const { client } = await connectGabp(info, { requestTimeoutMs: 1_800_000 });
const call = (n, a, o) => client.callTool(n, a, o);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const save = (n, o) => fs.writeFileSync(path.join(OUT, 'dumps', `${n}.json`), JSON.stringify(o, null, 2), 'utf8');
const R = { at: new Date().toISOString(), evidence: {} };
async function uiTexts() {
  const l = await call('rimworld/get_ui_layout');
  return (l.surfaces ?? []).flatMap((s) => (s.elements ?? []).map((e) => e.label ?? e.valueText).filter(Boolean));
}

// ── A) tick 上限 ───────────────────────────────────────────
console.log('▶ A) step_game_ticks 上限测试（显式给 timeoutMs）');
R.evidence.tickRuns = [];
for (const n of [15000, 15000, 30000]) {
  for (const toolTimeout of [10_000, 300_000]) {
    const t0 = Date.now();
    const r = await call('rimworld/step_game_ticks',
      { ticks: n, pauseFirst: true, timeoutMs: toolTimeout },
      { timeoutMs: toolTimeout + 120_000 });
    const ms = Date.now() - t0;
    const rec = { requested: n, toolTimeoutMs: toolTimeout, elapsedMs: ms, status: r.status, advanced: r.advancedTicks, frames: r.advancedFrames, startTicksGame: r.startTicksGame, endTicksGame: r.endTicksGame };
    R.evidence.tickRuns.push(rec);
    console.log(`  req=${n} toolTimeout=${toolTimeout} → advanced=${r.advancedTicks} status=${r.status} ${ms}ms (${(r.advancedTicks / (ms / 1000)).toFixed(0)} tick/s)`);
    if (r.advancedTicks !== n) break;
  }
}

// ── B) 选中植物读 inspect ─────────────────────────────────
console.log('\n▶ B) 选中植物读 % grown');
await call('rimworld/press_cancel').catch(() => {});
await sleep(400);
const ds = await call('rimworld/get_designator_state');
console.log('  designator_state.hasSelection = ' + ds?.designatorState?.hasSelection);
R.evidence.designatorAfterCancel = { hasSelection: ds?.designatorState?.hasSelection, id: ds?.designatorState?.selectedDesignatorId };

// 找一株植物
const Z = { x: 19, z: 10 };
const cc = await call('rimworld/get_cells_info', { x: Z.x - 4, z: Z.z - 4, width: 14, height: 14 });
const cands = (cc.cells ?? []).filter((c) => (c.things ?? []).some((t) => /^Plant_(Rice|Potato|Grass)/.test(t.defName)));
console.log(`  候选植物格 ${cands.length}`);
for (const c of cands.slice(0, 3)) console.log(`   (${c.x},${c.z}) ${JSON.stringify(c.things.map((t) => t.defName))}`);
const target = cands.find((c) => (c.things ?? []).some((t) => t.defName === 'Plant_Rice')) ?? cands[0];
console.log('  选中目标 (' + target.x + ',' + target.z + ')');
const clk = await call('rimworld/click_cell', { x: target.x, z: target.z, button: 'left' });
await sleep(600);
const sem = await call('rimworld/get_selection_semantics').catch((e) => ({ error: e.message }));
console.log('  selection: hasSelection=' + sem?.hasSelection + ' kinds=' + JSON.stringify(sem?.selectedKinds) + ' obj=' + JSON.stringify(sem?.selectedObjects?.[0]?.label ?? sem?.error));
R.evidence.plantSelection = { hasSelection: sem?.hasSelection, kinds: sem?.selectedKinds, label: sem?.selectedObjects?.[0]?.label, inspect: sem?.selectedObjects?.[0]?.inspectString };
const gz = await call('rimworld/list_selected_gizmos');
console.log('  gizmos: ' + JSON.stringify((gz.gizmos ?? []).map((g) => g.label)));
const tx = await uiTexts();
console.log('  UI 文本: ' + JSON.stringify(tx.slice(0, 30)));
const grow = tx.find((t) => /%\s*grown/.test(t));
console.log('  ★ grown 文本 = ' + JSON.stringify(grow));
R.evidence.grownText = grow ?? null;
R.evidence.uiTexts = tx;
save('ui_layout_plant_d2', await call('rimworld/get_ui_layout'));

// 也试 select_pawn / get_map_target_info 找植物 id
const mti = await call('rimworld/get_map_target_info', { thingId: (await call('rimworld/get_cell_info', { x: target.x, z: target.z })).cell.things.find((t) => /^Plant_/.test(t.defName))?.thingId ?? undefined });
console.log('  map_target_info: ' + JSON.stringify(mti).slice(0, 400));

fs.writeFileSync(path.join(OUT, 'D2.json'), JSON.stringify(R, null, 2), 'utf8');
client.close();
console.log('\n报告：' + path.join(OUT, 'D2.json'));
