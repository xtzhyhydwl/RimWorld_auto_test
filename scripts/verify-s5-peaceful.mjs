/**
 * 真正的 S5 验收：**和平模式**（关掉所有威胁事件）下跑 5 天。
 *
 * ## 为什么需要这一版
 *
 * 第一次 S5 验收（`verify-s5-survival.mjs`）人死了 —— 但死因是**袭击/发狂动物**
 * （Chaz 被撕碎、Lego/Whistler 失血），不是饿死。
 *
 * 用户明确要求：
 * > 「你需要想办法创造一种环境，**永远不触发袭击和动物发疯事件**，
 * >  我想要看**纯粹的运营表现**。你先找找有没有办法，如果没办法，
 * >  危险出现的时候我会托管。」
 *
 * 找到办法了：`foodprobe/peaceful` 把 34 个威胁类 `IncidentDef` 的 `baseChance` 设 0
 * （判定用"分类名 + defName 关键词"双条件，且排除友方/疾病）。
 *
 * 所以这一版：
 *   ① 先开和平模式
 *   ② 削存粮到约 1.5 天（否则验收没证明力）
 *   ③ 推 5 天，逐帧采样
 *   ④ 逐条判据
 *
 * ★ 和原版的区别：**这个脚本只测运营能力**。
 *   判断"能不能扛袭击"是另一个问题（需要威胁响应域），不该混在运营验收里。
 *
 * ⚠ 只**附着**到已在运行的游戏。
 * 用法：node scripts/verify-s5-peaceful.mjs [--days 5]
 */
import fs from 'node:fs';
import path from 'node:path';
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor, WS } from '../src/launcher.js';

const PROFILE = 'vanilla';
const CALL = 120_000;
const DAY_TICKS = 60_000;
const RUN_DIR = path.join(WS, 'runs', 's5-peaceful');

const argv = process.argv.slice(2);
const argNum = (name, dflt) => {
  const i = argv.indexOf(name);
  if (i < 0) return dflt;
  const v = Number(argv[i + 1]);
  return Number.isFinite(v) ? v : dflt;
};
const DAYS = argNum('--days', 5);

const steps = [];
const samples = [];
const log = (...a) => console.log(...a);

let client = null;
async function call(name, args = {}, timeoutMs = CALL) {
  return client.callTool(name, args, { timeoutMs });
}

function record(label, ok, detail) {
  steps.push({ label, ok, detail, at: new Date().toISOString() });
  log(`  ${ok === null ? '····' : ok ? '[PASS]' : '[FAIL]'} ${label}${detail ? '  — ' + detail : ''}`);
}

/** 关掉所有会强制暂停的窗口（含新殖民地的起名框） */
async function clearForcePause() {
  const r = await call('foodprobe/close_force_pause', {}, 30000).catch(() => null);
  const closed = r?.closed || [];
  if (closed.length) log(`    关掉了挡时间的窗口：${closed.join(', ')}`);
  return closed;
}

async function ready() {
  const g = await call('rimworld/get_game_info');
  if (g?.status !== 'game_loaded') return { ok: false, stage: 'not_loaded' };
  if ((g?.mapCount ?? 0) <= 0) return { ok: false, stage: 'no_map' };
  const ui = await call('rimworld/get_ui_state');
  if (ui?.programState !== 'Playing') return { ok: false, stage: 'programState=' + ui?.programState };
  if ((g?.ticksGame ?? 0) <= 0) return { ok: false, stage: 'ticks=0' };
  return { ok: true, stage: 'playing', ticks: g?.ticksGame ?? 0 };
}

/** 采一帧 */
async function sample(label) {
  const g = await call('rimworld/get_game_info');
  const fs2 = await call('foodprobe/forbidden_stats', {}, 60000).catch(() => null);
  const st = await call('foodprobe/read_state', {}, 60000).catch(() => null);
  const col = fs2?.colonists || [];
  const foods = col.map((c) => (typeof c.food === 'number' ? c.food : 1));
  const moods = col.map((c) => (typeof c.mood === 'number' ? c.mood : 1));
  const s = {
    label,
    ticks: g?.ticksGame ?? 0,
    colonistCount: col.length,
    stock: st?.state?.Stock ?? -1,
    reserveDays: st?.state?.ReserveDays ?? -1,
    minFood: foods.length ? Math.min(...foods) : -1,
    minMood: moods.length ? Math.min(...moods) : -1,
    people: col.map((c) => ({ name: c.name, food: c.food, mood: c.mood, downed: c.downed, dead: c.dead })),
    mental: col.filter((c) => c.mentalState).map((c) => `${c.name}:${c.mentalState}`),
    planted: st?.state?.PlantedCells ?? null,
  };
  samples.push(s);
  log(`    [${label}] tick=${s.ticks} 人=${s.colonistCount} 粮=${(s.stock).toFixed(1)} 最低food=${s.minFood.toFixed(3)} 最低mood=${s.minMood.toFixed(3)}`);
  return s;
}

async function main() {
  const info = readBridgeInfo(logPathFor(PROFILE));
  if (!info) throw new Error('没有桥接信息 —— 先 node src/cli.js start');
  const { client: c } = await connectGabp(info, { requestTimeoutMs: CALL });
  client = c;
  log(`桥接 ${info.host}:${info.port}`);
  log(`配置：${DAYS} 游戏天，**和平模式**（关掉威胁事件）`);

  const out = { at: new Date().toISOString(), days: DAYS };

  try {
    // ── 0. 就绪 ──
    let st = await ready();
    if (st.stage === 'not_loaded' || st.stage === 'no_map') {
      log('开调试殖民地（超时预期）...');
      await call('rimworld/start_debug_game_ready',
        { readiness: 'mapData', pauseIfNeeded: true, timeoutMs: 240_000 }, 280_000,
      ).catch((e) => log('  ' + e.message.split('\n')[0]));
    }
    for (let i = 0; i < 120; i++) {
      st = await ready();
      if (st.ok) break;
      if (i % 10 === 0) log(`  等待中... 阶段=${st.stage}`);
      await new Promise((r) => setTimeout(r, 2000));
    }
    await clearForcePause();
    record('地图稳定 + 殖民者就绪', st.ok, `阶段=${st.stage}`);

    // ── 1. ★ 开和平模式 ──
    log('\n=== 1. 开和平模式（关掉所有威胁事件）===');
    const pc = await call('foodprobe/peaceful', { enable: true }, 60000);
    record('★ 威胁事件已关闭', pc?.success === true && (pc?.changed ?? 0) > 0,
      `改了 ${pc?.changed} 个事件定义（备份 ${pc?.backupSize}）`);
    log(`  ${pc?.difficulty || ''}`);

    // 顺手清账本，让建造能真的发生
    await call('foodprobe/ledger_reset', {}, 30000).catch(() => null);

    // ── 2. 基线 ──
    log('\n=== 2. 基线 ===');
    const base = await sample('基线');
    record('基线采到了殖民者状态', base.people.length > 0,
      base.people.map((p) => `${p.name}(${p.food},${p.mood})`).join(' '));

    // ── 3. 削存粮 ──
    log('\n=== 3. 削存粮到约 1.5 天（否则验收没有证明力）===');
    let starved = false;
    let sdetail = '';
    for (let round = 0; round < 12; round++) {
      const r = await call('foodprobe/read_state', {}, 60000).catch(() => null);
      const days = r?.state?.ReserveDays;
      sdetail = `round ${round}: 可撑 ${days} 天`;
      log(`    ${sdetail}`);
      if (typeof days === 'number' && days <= 2.2) { starved = true; break; }
      const f = await call('foodprobe/forbid_food', { targetNutrition: 7.2 }, 60000).catch(() => null);
      log(`      forbid_food: changed=${f?.changed} ${f?.allowedNutritionBefore} → ${f?.allowedNutritionAfter}`);
      if ((f?.changed ?? 0) === 0) {
        if (typeof days === 'number' && days <= 2.2) { starved = true; }
        break;
      }
    }
    record('削存粮到约 1.5～2.2 天', starved, sdetail);

    // ── 4. 推进 ──
    log(`\n=== 4. 推进 ${DAYS} 游戏天（和平模式）===`);
    const totalTicks = DAYS * DAY_TICKS;
    const sampleEvery = 15_000;
    let advanced = 0;
    let nextSampleAt = sampleEvery;
    let stalls = 0;

    while (advanced < totalTicks && stalls < 25) {
      await clearForcePause();
      const p = await call('rimworld/play_for', {
        durationMs: 8000, speed: 'Ultrafast', forceRequestedSpeed: true, pollIntervalMs: 25,
      }, 90_000);
      const dt = p?.advancedTicks ?? 0;
      advanced += dt;

      if (dt === 0) {
        stalls++;
        if (stalls === 3) {
          const ui = await call('rimworld/get_ui_state').catch(() => null);
          log(`    ⚠ 卡住 #${stalls}，窗口：${JSON.stringify((ui?.windows || []).map((w) => w.type))}`);
        }
      } else stalls = 0;

      while (advanced >= nextSampleAt && nextSampleAt <= totalTicks) {
        await sample(`+${(nextSampleAt / DAY_TICKS).toFixed(2)}天`);
        nextSampleAt += sampleEvery;
      }
    }

    record(`推进 ${DAYS} 游戏天`, advanced >= totalTicks * 0.9,
      `实际推进 ${advanced} tick（目标 ${totalTicks}）`);

    // ── 5. 末帧 ──
    log('\n=== 5. 末帧 ===');
    const final = await sample('结束');
    const baseNames = base.people.map((p) => p.name).sort();
    const finalNames = final.people.map((p) => p.name).sort();

    // ── 6. 判据（**只针对运营，不针对战斗**）──
    log('\n=== 6. 判据（和平模式：只测运营能力）===');

    record('① 没人死（名单一致）',
      JSON.stringify(baseNames) === JSON.stringify(finalNames),
      `基线 ${baseNames.join(',')} → 结束 ${finalNames.join(',')}`);

    let minFood = Infinity, minFoodWho = '', minFoodAt = '';
    let minMood = Infinity, minMoodWho = '', minMoodAt = '';
    let everMental = null;
    for (const s of samples) {
      if (s.minFood >= 0 && s.minFood < minFood) {
        minFood = s.minFood;
        const worst = s.people.reduce((a, b) => ((a?.food ?? 2) < (b?.food ?? 2) ? a : b), null);
        minFoodWho = worst?.name || '';
        minFoodAt = s.label;
      }
      if (s.minMood >= 0 && s.minMood < minMood) {
        minMood = s.minMood;
        const worst = s.people.reduce((a, b) => ((a?.mood ?? 2) < (b?.mood ?? 2) ? a : b), null);
        minMoodWho = worst?.name || '';
        minMoodAt = s.label;
      }
      if (!everMental && s.mental?.length) everMental = `${s.label}: ${s.mental.join(',')}`;
    }

    record('② 全程人人有饭吃（food > 0.05）', minFood > 0.05,
      `最低 ${minFood.toFixed(3)} (${minFoodWho} @ ${minFoodAt})`);
    record('③ 全程没人精神崩溃', everMental === null, everMental || '从没出现 MentalState');
    record('③b 心情没触底（mood > 0.15）', minMood > 0.15,
      `最低 ${minMood.toFixed(3)} (${minMoodWho} @ ${minMoodAt})`);
    record('④ 全程粮食存量 > 0（没断粮）',
      samples.every((s) => s.stock > 0),
      `最低存量 ${Math.min(...samples.map((s) => s.stock)).toFixed(1)}`);
    record('⑤ 没人倒地', samples.every((s) => s.people.every((p) => !p.downed && !p.dead)));

    // 种植区趋势（mod 有没有在自主工作）
    const planted = samples.map((s) => s.planted).filter((v) => typeof v === 'number');
    if (planted.length >= 2) {
      const first = planted[0], last = planted[planted.length - 1];
      record('⑥ ★ mod 真的在自主种地', last > first, `种植格 ${first} → ${last}（Δ${last - first}）`);
      // 不振荡：单调不减的次数
      let drops = 0;
      for (let i = 1; i < planted.length; i++) if (planted[i] < planted[i - 1] - 5) drops++;
      record('⑦ 种植面积不振荡', drops === 0, `下降 ${drops} 次，序列 [${planted.join(',')}]`);
    }

    out.base = base;
    out.final = final;
    out.minFood = minFood;
    out.minMood = minMood;
    out.peaceful = { changed: pc?.changed, backupSize: pc?.backupSize };
  } finally {
    client.close();
  }

  const passed = steps.filter((s) => s.ok === true).length;
  const failed = steps.filter((s) => s.ok === false).length;
  const skipped = steps.filter((s) => s.ok === null).length;

  fs.mkdirSync(RUN_DIR, { recursive: true });
  fs.writeFileSync(path.join(RUN_DIR, 'report.json'),
    JSON.stringify({ passed, failed, skipped, steps, samples, ...out }, null, 2), 'utf8');

  const md = [
    '# S5 验收（和平模式）：纯粹运营表现',
    '',
    `时间：${out.at}`,
    `配置：${DAYS} 游戏天，**已关闭 ${out.peaceful?.changed} 个威胁事件定义**`,
    `结果：**通过 ${passed} / 失败 ${failed} / 跳过 ${skipped}**`,
    '',
    '> 这一版**只测运营能力**。判断"能不能扛袭击"是另一个问题（需要威胁响应域），',
    '> 不该混在运营验收里 —— 用户明确要求"我想要看纯粹的运营表现"。',
    '',
    '## 逐条断言',
    '',
    '| 结果 | 项目 | 细节 |',
    '| --- | --- | --- |',
    ...steps.map((s) => `| ${s.ok === null ? '—' : s.ok ? '✅' : '❌'} | ${s.label} | ${(s.detail || '').replace(/\|/g, '\\|')} |`),
    '',
    '## 采样',
    '',
    '| 时点 | tick | 人数 | 粮 | 最低food | 最低mood | 种植格 |',
    '| --- | --- | --- | --- | --- | --- | --- |',
    ...samples.map((s) => `| ${s.label} | ${s.ticks} | ${s.colonistCount} | ${s.stock.toFixed(1)} | ${s.minFood.toFixed(3)} | ${s.minMood.toFixed(3)} | ${s.planted ?? '—'} |`),
  ].join('\n');
  fs.writeFileSync(path.join(RUN_DIR, 'report.md'), md, 'utf8');

  log(`\n=== 通过 ${passed} / 失败 ${failed} / 跳过 ${skipped} ===`);
  log(`报告: ${path.join(RUN_DIR, 'report.md')}`);
  process.exitCode = failed === 0 ? 0 : 1;
}

main().catch((e) => {
  console.error('\n[错误] ' + e.message);
  process.exitCode = 1;
});
