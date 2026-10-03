/**
 * S5 验收：3 个殖民者撑过 5 游戏天（全部有饭吃 + 心情不崩）。
 *
 * ## 判据设计见 docs/s5-5天生存验收设计.md，核心两条
 *
 * 1. **必须造出"真的需要 mod 干活"的场景**
 *    debug 开局自带约 10.7 天口粮 —— 什么都不做也能撑过 5 天，
 *    那样的验收**什么也没证明**。所以默认**把存粮削到 1.5 天**，
 *    逼 mod 必须种地，而 1.5 天又够撑到第一茬收获（水稻约 2 天到可收获）。
 *
 * 2. **必须能指出"mod 做了什么、结果粮食曲线怎么变的"**
 *    只断言"没人死"是不够的 —— 那可能是靠存粮活下来的。
 *
 * ## 时间推进
 *
 * 5 游戏天 = 300000 tick，`play_for(Ultrafast)` 实测约 7000 tick/s ⇒ 约 43 秒。
 * 但 `play_for` 会提前返回、还可能被 forcePause 窗口弄成"静默跑 0 tick"，
 * 所以必须**分片 + 重试**，且**按 advancedTicks 累计计数，不信 success**。
 *
 * ⚠ 只**附着**到已在运行的游戏，自己绝不启动游戏。
 * 用法：node scripts/verify-s5-survival.mjs [--days 5] [--no-starve]
 */
import fs from 'node:fs';
import path from 'node:path';
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor, WS } from '../src/launcher.js';

const PROFILE = 'vanilla';
const CALL = 120_000;
const RUN_DIR = path.join(WS, 'runs', 's5-survival');
const DAY_TICKS = 60_000;

const argv = process.argv.slice(2);
const argNum = (name, dflt) => {
  const i = argv.indexOf(name);
  return i >= 0 && argv[i + 1] ? Number(argv[i + 1]) : dflt;
};
const DAYS = argNum('--days', 5);
const STARVE = !argv.includes('--no-starve');   // 默认削存粮（否则验收没证明力）

let client = null;
const steps = [];
const samples = [];
const log = (...a) => console.log(...a);

async function call(name, args = {}, timeoutMs = CALL) {
  return client.callTool(name, args, { timeoutMs });
}

function record(label, ok, detail) {
  steps.push({ label, ok, detail, at: new Date().toISOString() });
  log(`  ${ok === null ? '····' : ok ? '[PASS]' : '[FAIL]'} ${label}${detail ? '  — ' + detail : ''}`);
}

async function clearForcePause() {
  const ui = await call('rimworld/get_ui_state');
  if (!ui?.windowsForcePause) return [];
  const closed = [];
  for (const w of ui.windows || []) {
    if (!w.forcePause) continue;
    if (w.type === 'RimWorld.MainTabWindow_Menu') {
      await call('rimworld/close_main_tab', { tabId: 'main-tab:Menu' });
      closed.push('main-tab:Menu');
    }
  }
  // ★ 还有别的会强制暂停的窗口（实测：新殖民地弹的
  //   `Dialog_NamePlayerFactionAndSettlement`）—— 它挡住 play_for，
  //   表现为"连续 20 次推进 0 tick"。
  //   探针提供了通用关闭入口，别再逐个 type 硬编码。
  if (closed.length === 0) {
    const r = await call('foodprobe/close_force_pause', {}, 30000).catch(() => null);
    for (const c of r?.closed || []) closed.push(c);
  }
  return closed;
}

async function ready() {
  const g = await call('rimworld/get_game_info');
  if (g?.status !== 'game_loaded') return { ok: false, stage: 'not_loaded', c: 0 };
  if ((g?.mapCount ?? 0) <= 0) return { ok: false, stage: 'no_map', c: 0 };
  const ui = await call('rimworld/get_ui_state');
  if (ui?.programState !== 'Playing') return { ok: false, stage: 'programState=' + ui?.programState, c: 0 };
  if ((g?.ticksGame ?? 0) <= 0) return { ok: false, stage: 'ticks=0', c: 0 };
  const c = await call('rimworld/list_colonists');
  return { ok: true, stage: 'playing', c: c?.count ?? 0, ticks: g?.ticksGame ?? 0 };
}

/** 采一帧：殖民者状态 + 粮食 + 种植区面积 */
async function sample(label) {
  const g = await call('rimworld/get_game_info');
  const cols = await call('rimworld/list_colonists');
  const forb = await call('foodprobe/forbidden_stats').catch(() => null);

  // 殖民者的 food / mood：用 forbidden_stats 顺带返回的 colonists（它带 mood/food）
  let people = [];
  if (forb?.colonists) {
    people = forb.colonists.map((c) => ({
      name: c.name, food: c.food, mood: c.mood, downed: c.downed, dead: c.dead,
    }));
  }

  // 粮食存量：读 mod 的最新遥测
  let stock = null, reserveDays = null, action = null, zoneArea = null;
  const tl = await call('foodprobe/telemetry_tail', { lines: 1 }).catch(() => null);
  if (tl?.tail?.length) {
    try {
      const row = JSON.parse(tl.tail[0]);
      stock = row.stock;
      reserveDays = row.reserveDays;
      action = row.action?.kind ?? null;
    } catch { /* 半行 */ }
  }

  // 种植区面积（桥接回读，不信 mod 自述）
  const z = await call('rimworld/list_zones').catch(() => null);
  if (z?.zones) {
    zoneArea = z.zones.reduce((s, x) => s + (x.cellCount ?? x.cells ?? 0), 0);
  }

  const s = {
    label,
    at: new Date().toISOString(),
    ticks: g?.ticksGame ?? null,
    colonistCount: cols?.count ?? 0,
    people,
    stock,
    reserveDays,
    action,
    zoneArea,
  };
  samples.push(s);

  const minFood = people.length ? Math.min(...people.map((p) => p.food ?? 1)) : null;
  const minMood = people.length ? Math.min(...people.map((p) => p.mood ?? 1)) : null;
  log(`    [${label}] tick=${s.ticks} 人=${s.colonistCount} 粮=${stock} 最低food=${minFood} 最低mood=${minMood} 种植区=${zoneArea}`);
  return s;
}

async function main() {
  const info = readBridgeInfo(logPathFor(PROFILE));
  if (!info) throw new Error('没有桥接信息 —— 先 node src/cli.js start（后台任务托管）');
  const conn = await connectGabp(info, { requestTimeoutMs: CALL });
  client = conn.client;
  log(`桥接 ${info.host}:${info.port}`);
  log(`配置：${DAYS} 游戏天，削存粮=${STARVE}`);

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
      if (st.ok && st.c > 0) break;
      if (i % 10 === 0) log(`  等待中... 阶段=${st.stage} 殖民者=${st.c}`);
      await new Promise((r) => setTimeout(r, 2000));
    }
    record('地图稳定 + 殖民者就绪', st.ok && st.c > 0, `阶段=${st.stage}，${st.c} 人`);
    if (!st.ok || st.c === 0) throw new Error('没有可用殖民地，无法验收');
    await clearForcePause();

    const health = await call('foodprobe/health');
    record('mod 已加载', health?.modLoaded === true, health?.modLoaded ? '' : health?.message);

    // ── 1. 基线 ──
    log('\n=== 1. 基线 ===');
    const base = await sample('基线');
    const baseNames = base.people.map((p) => p.name).sort();
    record('基线采到了殖民者状态', base.people.length > 0, JSON.stringify(base.people).slice(0, 200));

    // ── 2. 造场景：削存粮（关键！否则验收没有证明力）──
    //
    // ⚠ 刻意**不复用**探针的 forbid_some 一次性调用，而是**循环**调用直到存粮降到目标：
    //   因为 forbid_some 每次只禁取固定条数，而食物可能有很多堆。
    //   判据用探针回读的"当前可用食物营养总量"（food_summary 会算，但它把野草也算进去，
    //   所以这里改用 mod 自己的感知值 —— mod 的食物判据是可信的那个）。
    if (STARVE) {
      log('\n=== 2. 造场景：把存粮削到约 1.5 天（逼 mod 必须种地）===');
      let ok = false;
      let lastDetail = '';
      for (let round = 0; round < 12; round++) {
        // mod 感知到的可用存粮（这是"小人真的拿得到"的量）
        const stateRes = await call('foodprobe/read_state').catch(() => null);
        const st2 = stateRes?.state;
        const stock = st2?.Stock;
        const burn = st2?.BurnRatePerDay;
        const days = st2?.ReserveDays;
        lastDetail = `round ${round}: stock=${stock} burn=${burn} days=${days}`;
        log(`    ${lastDetail}`);

        if (typeof days === 'number' && days <= 2.2) { ok = true; break; }

        // ★ 必须用 `forbid_food`，不能用 `forbid_some`。
        //
        //   实测教训：`forbid_some` 带 `preferStuff:false` 时**打不到食物上** ——
        //   它改了 8 个物品（totalForbidden 0→8），但 `forbiddenFoodNutrition` **仍然是 0**，
        //   存粮一点没降。因为它的两遍挑选先挑建材、再挑"任意物品"，
        //   而地图上任意物品（钢/银/零件）比食物多，挑不到食物。
        //
        //   没有这一步，验收就变成"验证了一个不吃紧的场景" —— 看起来"活下来了"，
        //   其实是靠 11 天的存粮撑的，什么也没证明。
        const f = await call('foodprobe/forbid_food', { targetNutrition: 7.2 }, 60000).catch(() => null);
        log(`      forbid_food: changed=${f?.changed} 可用营养 ${f?.allowedNutritionBefore} → ${f?.allowedNutritionAfter}`);

        // ★ `changed === 0` **不一定**是失败 —— 也可能是**已经在目标之下了**
        //   （比如上一轮跑过、食物已经被禁掉）。这时候应该算**通过**。
        //   我第一版直接把它当失败，于是"已经削好了"反而报 FAIL（假失败）。
        if ((f?.changed ?? 0) === 0) {
          // 再读一次存粮确认
          const re = await call('foodprobe/read_state').catch(() => null);
          const d2 = re?.state?.ReserveDays;
          if (typeof d2 === 'number' && d2 <= 2.2) {
            ok = true;
            lastDetail += ` ｜ 无需再削（当前 ${d2.toFixed(2)} 天已达标）`;
          } else {
            lastDetail += ` ｜ forbid_food 改不动了，但存粮仍 ${d2} 天`;
          }
          break;
        }
      }
      record('削存粮到约 1.5～2.2 天', ok, lastDetail);
    } else {
      record('跳过削存粮（--no-starve）', null, '注意：不削存粮时本次验收的证明力很弱');
    }

    // ── 3. 推进 5 游戏天，边推边采样 ──
    log(`\n=== 3. 推进 ${DAYS} 游戏天（约 ${Math.round(DAYS * DAY_TICKS / 7000)} 秒）===`);
    const totalTicks = DAYS * DAY_TICKS;
    const sampleEvery = 15_000;      // 约 0.25 游戏天采一次
    let advanced = 0;
    let nextSampleAt = sampleEvery;
    let stalls = 0;
    let stallDiag = null;

    while (advanced < totalTicks && stalls < 20) {
      await clearForcePause();
      const before = (await call('rimworld/get_game_info'))?.ticksGame ?? 0;
      const p = await call('rimworld/play_for', {
        durationMs: 8000, speed: 'Ultrafast', forceRequestedSpeed: true, pollIntervalMs: 25,
      }, 90_000);
      const dt = p?.advancedTicks ?? 0;
      advanced += dt;

      if (dt === 0) {
        stalls++;
        // ★ 卡住时必须能说出**是什么挡着**（本项目的纪律：任何"没做成"都要能说为什么）。
        //   我第一版只报"连续 20 次推进 0 tick"，完全不知道该关什么 —— 那种诊断等于没有。
        if (stalls === 1 || stalls === 5 || stalls === 20) {
          const ui = await call('rimworld/get_ui_state').catch(() => null);
          stallDiag = {
            stalls,
            windowsForcePause: ui?.windowsForcePause,
            programState: ui?.programState,
            speed: ui?.speed,
            paused: ui?.paused,
            windows: (ui?.windows || []).map((w) => ({
              type: w.type, forcePause: w.forcePause, id: w.id,
            })),
          };
          log(`    ⚠ 卡住 #${stalls}：${JSON.stringify(stallDiag)}`);
        }
      } else {
        stalls = 0;
        stallDiag = null;
      }

      while (advanced >= nextSampleAt && nextSampleAt <= totalTicks) {
        await sample(`+${(nextSampleAt / DAY_TICKS).toFixed(2)}天`);
        nextSampleAt += sampleEvery;
      }
      if (before === 0) break;    // 没有地图了
    }

    record(`推进 ${DAYS} 游戏天`, advanced >= totalTicks * 0.9,
      `实际推进 ${advanced} tick（目标 ${totalTicks}）`);

    if (stalls >= 20) {
      record('推进过程没有卡死', false,
        `连续 ${stalls} 次 play_for 推进 0 tick —— 诊断：${JSON.stringify(stallDiag)}`);
    }

    // ── 4. 末帧 ──
    log('\n=== 4. 末帧 ===');
    const final = await sample('结束');
    const finalNames = final.people.map((p) => p.name).sort();

    // ── 5. 逐条判据 ──
    log('\n=== 5. 判据 ===');

    // 判据 1：没人死 / 没人失踪
    const sameRoster = baseNames.length === finalNames.length
      && baseNames.every((n, i) => n === finalNames[i]);
    record('① 没人死（名单一致）', sameRoster,
      `基线 ${baseNames.join(',')} → 结束 ${finalNames.join(',')}`);

    // 判据 2：人人有饭吃（全程）
    const allFoods = [];
    for (const s of samples) for (const p of s.people) if (typeof p.food === 'number') allFoods.push({ who: p.name, v: p.food, label: s.label });
    const minFoodRec = allFoods.length ? allFoods.reduce((a, b) => (b.v < a.v ? b : a)) : null;
    record('② 全程人人有饭吃（food > 0.05）',
      minFoodRec != null && minFoodRec.v > 0.05,
      minFoodRec ? `最低 ${minFoodRec.v} (${minFoodRec.who} @ ${minFoodRec.label})` : '没采到 food 数据');

    // 判据 3：没人崩溃（心情不触底）
    const allMoods = [];
    for (const s of samples) for (const p of s.people) if (typeof p.mood === 'number') allMoods.push({ who: p.name, v: p.mood, label: s.label });
    const minMoodRec = allMoods.length ? allMoods.reduce((a, b) => (b.v < a.v ? b : a)) : null;
    // 心情 0.30 以下开始有崩溃风险；这里用 0.15 作为"触底"的硬线
    record('③ 全程没人心情触底（mood > 0.15）',
      minMoodRec != null && minMoodRec.v > 0.15,
      minMoodRec ? `最低 ${minMoodRec.v} (${minMoodRec.who} @ ${minMoodRec.label})` : '没采到 mood 数据');

    const anyoneDowned = samples.some((s) => s.people.some((p) => p.downed || p.dead));
    record('③b 没人倒地/死亡', !anyoneDowned,
      anyoneDowned ? '有人倒地或死亡' : '全程正常');

    // 判据 4：粮食没断
    const stocks = samples.map((s) => s.stock).filter((x) => typeof x === 'number');
    const minStock = stocks.length ? Math.min(...stocks) : null;
    record('④ 全程粮食存量 > 0（没断粮）',
      minStock != null && minStock > 0,
      minStock != null ? `最低存量 ${minStock}` : '没采到存量数据');

    // 判据 5：★ mod 真的在自主工作（不然"活下来"不是它的功劳）
    const plantActions = samples.filter((s) => s.action === 'PlantCrop').length;
    const areaGrowth = (final.zoneArea ?? 0) - (base.zoneArea ?? 0);
    record('⑤ ★ mod 真的在自主种地（不是靠存粮撑过去的）',
      plantActions > 0 || areaGrowth > 0,
      `采样里 PlantCrop ${plantActions} 次；种植区面积 ${base.zoneArea} → ${final.zoneArea}（Δ${areaGrowth}）`);

    // 判据 6：不振荡
    const areas = samples.map((s) => s.zoneArea).filter((x) => typeof x === 'number');
    let drops = 0;
    for (let i = 1; i < areas.length; i++) if (areas[i] < areas[i - 1]) drops++;
    record('⑥ 种植面积不振荡（没有反复砍了又种）', drops <= 1,
      `面积序列 ${JSON.stringify(areas)}，下降 ${drops} 次`);

    // ── 6. 把结论写进输出 ──
    log('\n=== 6. 采样总览 ===');
    for (const s of samples) {
      const mn = s.people.length ? Math.min(...s.people.map((p) => p.food ?? 1)) : null;
      const mm = s.people.length ? Math.min(...s.people.map((p) => p.mood ?? 1)) : null;
      log(`  ${s.label.padEnd(9)} tick=${String(s.ticks).padStart(7)} 人=${s.colonistCount} 粮=${String(s.stock).padStart(7)} 最低food=${mn} 最低mood=${mm} 种植区=${s.zoneArea} 动作=${s.action}`);
    }
  } finally {
    client.close();
  }

  const passed = steps.filter((s) => s.ok === true).length;
  const failed = steps.filter((s) => s.ok === false).length;
  const skipped = steps.filter((s) => s.ok === null).length;

  fs.mkdirSync(RUN_DIR, { recursive: true });
  fs.writeFileSync(path.join(RUN_DIR, 'report.json'),
    JSON.stringify({ passed, failed, skipped, days: DAYS, starve: STARVE, steps, samples }, null, 2), 'utf8');

  const md = [
    `# S5 验收报告：3 个殖民者撑过 ${DAYS} 游戏天`,
    '',
    `时间：${new Date().toISOString()}`,
    `配置：跑 ${DAYS} 游戏天，削存粮=${STARVE}`,
    `结果：**通过 ${passed} / 失败 ${failed} / 跳过 ${skipped}**`,
    '',
    '| 结果 | 判据 | 细节 |',
    '| --- | --- | --- |',
    ...steps.map((s) => `| ${s.ok === null ? '—' : s.ok ? '✅' : '❌'} | ${s.label} | ${(s.detail || '').replace(/\|/g, '\\|')} |`),
    '',
    '## 采样明细',
    '',
    '| 采样点 | tick | 人数 | 粮食存量 | 最低 food | 最低 mood | 种植区面积 | 动作 |',
    '| --- | --- | --- | --- | --- | --- | --- | --- |',
    ...samples.map((s) => {
      const mn = s.people.length ? Math.min(...s.people.map((p) => p.food ?? 1)) : '';
      const mm = s.people.length ? Math.min(...s.people.map((p) => p.mood ?? 1)) : '';
      return `| ${s.label} | ${s.ticks} | ${s.colonistCount} | ${s.stock} | ${mn} | ${mm} | ${s.zoneArea} | ${s.action} |`;
    }),
    '',
    '> ⚠ 判据 ⑤ 是关键：如果殖民者是靠开局自带的存粮活下来的，这次验收**什么也没证明**。',
    '> 所以默认会先把存粮削到约 1.5 天，逼 mod 必须种地。',
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
