/**
 * recon-food-D3 —— 补齐 D 块：
 *  - step_game_ticks 用大 timeoutMs（工具级）能推进多少 tick
 *  - 2500 tick / 15000 tick 实测秒数
 *  - 生长进度随时间变化（活着采样）
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
const Z = { x: 19, z: 10 };

async function readPlant() {
  const cc = await call('rimworld/get_cells_info', { x: Z.x - 3, z: Z.z - 3, width: 12, height: 12 });
  const c = (cc.cells ?? []).find((x) => (x.things ?? []).some((t) => /^Plant_/.test(t.defName)));
  if (!c) return null;
  await call('rimworld/press_cancel').catch(() => {});
  await sleep(150);
  await call('rimworld/click_cell', { x: c.x, z: c.z, button: 'left' });
  await sleep(450);
  const tx = await uiTexts();
  const grow = tx.find((t) => /%\s*grown/.test(t));
  const def = c.things.find((t) => /^Plant_/.test(t.defName));
  return { x: c.x, z: c.z, def: def?.defName, growText: grow ?? null };
}

console.log('▶ 初始状态');
let p = await readPlant();
console.log('  ' + JSON.stringify(p));
R.evidence.t0 = p;

async function step(ticks) {
  const t0 = Date.now();
  const r = await call('rimworld/step_game_ticks', { ticks, pauseFirst: true, timeoutMs: 600_000 }, { timeoutMs: 660_000 });
  const ms = Date.now() - t0;
  const rec = { requested: ticks, advanced: r.advancedTicks, status: r.status, ms, start: r.startTicksGame, end: r.endTicksGame, frames: r.advancedFrames };
  console.log(`  step(${ticks}) → advanced=${r.advancedTicks} status=${r.status} ${ms}ms (${(r.advancedTicks / (ms / 1000)).toFixed(0)} tick/s) gameTick ${r.startTicksGame}→${r.endTicksGame}`);
  return rec;
}

R.evidence.runs = [];
console.log('\n▶ 2500 tick（≈1 游戏小时）');
R.evidence.runs.push(await step(2500));
p = await readPlant(); console.log('  ' + JSON.stringify(p)); R.evidence.after2500 = p;

console.log('\n▶ 15000 tick（≈6 游戏小时）');
R.evidence.runs.push(await step(15000));
p = await readPlant(); console.log('  ' + JSON.stringify(p)); R.evidence.after17500 = p;

console.log('\n▶ 再来 15000 tick');
R.evidence.runs.push(await step(15000));
p = await readPlant(); console.log('  ' + JSON.stringify(p)); R.evidence.after32500 = p;

console.log('\n▶ 150000 tick（≈2.5 游戏天，一次调用看上限）');
R.evidence.runs.push(await step(150000));
p = await readPlant(); console.log('  ' + JSON.stringify(p)); R.evidence.after175000 = p;

const totTicks = R.evidence.runs.reduce((a, b) => a + b.advanced, 0);
const totMs = R.evidence.runs.reduce((a, b) => a + b.ms, 0);
R.evidence.summary = { totalAdvancedTicks: totTicks, totalMs: totMs, avgTickPerSec: +(totTicks / (totMs / 1000)).toFixed(0) };
console.log(`\n合计推进 ${totTicks} tick，用时 ${(totMs / 1000).toFixed(1)}s，平均 ${R.evidence.summary.avgTickPerSec} tick/s`);
console.log(`外推 15 天（900000 tick）：约 ${(900000 / R.evidence.summary.avgTickPerSec / 60).toFixed(1)} 分钟`);

fs.writeFileSync(path.join(OUT, 'D3.json'), JSON.stringify(R, null, 2), 'utf8');
client.close();
console.log('\n报告：' + path.join(OUT, 'D3.json'));
