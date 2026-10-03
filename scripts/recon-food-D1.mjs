/**
 * recon-food-D1 —— 时间推进与生长观测（干净重测）。
 *  1. 关掉 Work 标签、清掉既有植物
 *  2. 种植区作物设为 Potato（默认）→ 观察自动播种
 *  3. 记录 HUD 时钟与 step_game_ticks 的 tick 速率
 *  4. 连续采样植物 inspect 的 "% grown"
 */
import fs from 'node:fs';
import path from 'node:path';
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor, WS } from '../src/launcher.js';

const OUT = path.join(WS, 'runs', 'food-recon');
const info = readBridgeInfo(logPathFor('vanilla'));
const { client } = await connectGabp(info, { requestTimeoutMs: 1_200_000 });
const call = (n, a, o) => client.callTool(n, a, o);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const head = (o, n = 2000) => { const s = typeof o === 'string' ? o : JSON.stringify(o); return s.length > n ? s.slice(0, n) + ` …(+${s.length - n})` : s; };
const save = (n, o) => fs.writeFileSync(path.join(OUT, 'dumps', `${n}.json`), JSON.stringify(o, null, 2), 'utf8');
const R = { at: new Date().toISOString(), evidence: {} };
const Z = { x: 19, z: 10, w: 4, h: 4 };

async function uiTexts() {
  const l = await call('rimworld/get_ui_layout');
  return (l.surfaces ?? []).flatMap((s) => (s.elements ?? []).map((e) => e.label ?? e.valueText).filter(Boolean));
}
async function hudClock() {
  const t = await uiTexts();
  return t.filter((x) => /\d+(st|nd|rd|th) of |Aprimay|Jugust|Septober|Decembary|, \d{1,2}:\d{2}/.test(x)).slice(0, 3);
}

await call('rimworld/close_main_tab', {}).catch(() => {});
await call('rimworld/press_cancel').catch(() => {});
await sleep(300);

// ── 1. 清掉 zone 里所有植物（用 god mode 加速）─────────────
console.log('▶ 1. 清掉 Zone_1 里的植物');
const before = await call('rimworld/get_cells_info', { x: Z.x, z: Z.z, width: Z.w, height: Z.h });
const plantCells = (before.cells ?? []).filter((c) => (c.things ?? []).length);
console.log(`  现有植物格 ${plantCells.length}`);
R.evidence.plantsBefore = plantCells.length;
const DESIG_DEATH = 'architect-designator:orders:highlight-designator-tutortagnotset-3'; // Harvest? 不确定，改用 debug 动作
// 直接用一个可靠的 debug action：Actions\Destroy... 不好用；改走 set_god_mode + 逐个 spawn 覆盖？ 
// 更简单：直接删 zone 再重建，旧的 Zone_Growing 会随之消失（Zone 删除会移除 zone，但植物留着）
// 用 Dev 模式下的 "T: Destroy" 工具？ 先试 apply_architect_designator + designatorId 为 Cut plants
const CUT = 'architect-designator:orders:highlight-designator-tutortagnotset-2'; // Cut plants
const cut = await call('rimworld/apply_architect_designator', { designatorId: CUT, x: Z.x, z: Z.z, width: Z.w, height: Z.h, dryRun: true });
console.log('  Cut plants dryRun: accepted=' + cut.acceptedCellCount + ' rejected=' + cut.rejectedCellCount + ' reason=' + (cut.rejectedCells?.[0]?.reason ?? '-'));
R.evidence.cutDryRun = { accepted: cut.acceptedCellCount, rejected: cut.rejectedCellCount };
const cut2 = await call('rimworld/apply_architect_designator', { designatorId: CUT, x: Z.x, z: Z.z, width: Z.w, height: Z.h });
console.log('  Cut plants apply: accepted=' + cut2.acceptedCellCount + ' applied=' + cut2.appliedCellCount);
await sleep(300);
const ci = await call('rimworld/get_cell_info', { x: Z.x + 1, z: Z.z + 1 });
console.log('  回读 designations: ' + JSON.stringify(ci.cell.designations));
R.evidence.cutApply = { accepted: cut2.acceptedCellCount, applied: cut2.appliedCellCount, designations: ci.cell.designations };

// 强推进让殖民者把植物割掉（或直接再用 debug action）
console.log('  推进 3000 tick 让割除生效…');
let s = await call('rimworld/step_game_ticks', { ticks: 3000, pauseFirst: true, timeoutMs: 600_000 }, { timeoutMs: 660_000 });
console.log(`  status=${s.status} advanced=${s.advancedTicks} tick ${s.startTicksGame}→${s.endTicksGame}`);
await sleep(300);
const after = await call('rimworld/get_cells_info', { x: Z.x, z: Z.z, width: Z.w, height: Z.h });
console.log(`  割除后植物格 ${(after.cells ?? []).filter((c) => (c.things ?? []).length).length}`);
R.evidence.afterCut = (after.cells ?? []).filter((c) => (c.things ?? []).length).length;

// ── 2. 时钟 + tick 速率 ──────────────────────────────────
console.log('\n▶ 2. HUD 时钟 / tick 速率');
console.log('  时钟: ' + JSON.stringify(await hudClock()));
const rates = [];
for (const n of [600, 3000]) {
  const t0 = Date.now();
  const r = await call('rimworld/step_game_ticks', { ticks: n, pauseFirst: true, timeoutMs: 900_000 }, { timeoutMs: 950_000 });
  const ms = Date.now() - t0;
  const rec = { ticks: n, ms, status: r.status, advanced: r.advancedTicks, frames: r.advancedFrames, rate: +(n / (ms / 1000)).toFixed(0) };
  rates.push(rec);
  console.log(`  ${n} tick: advanced=${r.advancedTicks} frames=${r.advancedFrames} ${ms}ms → ${rec.rate} tick/s`);
}
R.evidence.tickRates = rates;
console.log('  时钟: ' + JSON.stringify(await hudClock()));

// 再测 15000（1 游戏小时 ≈ 2500 tick；6 小时 ≈ 15000）
{
  const n = 15000;
  const t0 = Date.now();
  const r = await call('rimworld/step_game_ticks', { ticks: n, pauseFirst: true, timeoutMs: 1_200_000 }, { timeoutMs: 1_250_000 });
  const ms = Date.now() - t0;
  rates.push({ ticks: n, ms, status: r.status, advanced: r.advancedTicks, frames: r.advancedFrames, rate: +(n / (ms / 1000)).toFixed(0) });
  console.log(`  ${n} tick: advanced=${r.advancedTicks} ${ms}ms → ${(n / (ms / 1000)).toFixed(0)} tick/s`);
}
save('tick_rates', rates);
console.log('▶ 15 天需要 tick 数 & 预计耗时：');
const ticksPerDay = 60000;
console.log(`  15 天 = ${15 * ticksPerDay} tick；按实测速率约 ${(15 * ticksPerDay / (rates.at(-1).rate)).toFixed(0)} 秒 = ${(15 * ticksPerDay / rates.at(-1).rate / 3600).toFixed(2)} 小时`);
R.evidence.extrapolation15Days = { ticks: 15 * ticksPerDay, rate: rates.at(-1).rate, seconds: Math.round(15 * ticksPerDay / rates.at(-1).rate) };

// ── 3. 自动播种了吗 ─────────────────────────────────────
console.log('\n▶ 3. 种植区是否自动播种');
const zc = await call('rimworld/get_cells_info', { x: Z.x, z: Z.z, width: Z.w, height: Z.h });
const plantNow = (zc.cells ?? []).filter((c) => (c.things ?? []).length);
console.log(`  有东西的格 ${plantNow.length}: ` + JSON.stringify(plantNow.slice(0, 6).map((c) => ({ x: c.x, z: c.z, defs: c.things.map((t) => t.defName) }))));
R.evidence.zoneAfterCut = plantNow.map((c) => ({ x: c.x, z: c.z, defs: c.things.map((t) => t.defName) }));
save('cells_zone_after_cut', zc);

// ── 4. 采样生长进度 ──────────────────────────────────────
console.log('\n▶ 4. 生长进度采样（每 5000 tick 读一次 inspect）');
const samples = [];
for (let round = 0; round < 5; round++) {
  // 找一株植物
  const cc = await call('rimworld/get_cells_info', { x: Z.x - 6, z: Z.z - 6, width: 16, height: 16 });
  const tp = (cc.cells ?? []).find((c) => (c.things ?? []).some((t) => /^Plant_/.test(t.defName)));
  if (!tp) { console.log(`  round ${round}: 附近没有植物，跳过`); samples.push({ round, plant: null }); }
  else {
    await call('rimworld/press_cancel').catch(() => {});
    await call('rimworld/click_cell', { x: tp.x, z: tp.z, button: 'left' });
    await sleep(450);
    const tx = await uiTexts();
    const grow = tx.find((t) => /%\s*grown/.test(t));
    const def = tp.things.find((t) => /^Plant_/.test(t.defName));
    console.log(`  round ${round} @(${tp.x},${tp.z}) ${def?.defName}: ${JSON.stringify(grow)}`);
    samples.push({ round, x: tp.x, z: tp.z, def: def?.defName, growText: grow ?? null });
  }
  if (round < 4) {
    const t0 = Date.now();
    const r = await call('rimworld/step_game_ticks', { ticks: 5000, pauseFirst: true, timeoutMs: 900_000 }, { timeoutMs: 950_000 });
    console.log(`    +5000 tick (${Date.now() - t0}ms) 时钟=${JSON.stringify(await hudClock())}`);
  }
}
R.evidence.growthSamples = samples;
save('growth_samples', samples);

fs.writeFileSync(path.join(OUT, 'D1.json'), JSON.stringify(R, null, 2), 'utf8');
client.close();
console.log('\n报告：' + path.join(OUT, 'D1.json'));
