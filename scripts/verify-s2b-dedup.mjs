/**
 * S2b 验证：域协调器的**按天去重**（针对一个真 bug 的回归测试）。
 *
 * ## 这个测试是冲着什么来的
 *
 * 我重构 `NeedCoordinator` 时把"按天去重"的检查删掉了，只留"执行后记账"。
 * 后果：白天的**每一次**协调器调用（每 64 tick 一次）都判定 `ShouldRunToday=true`，
 * 于是**一天跑了约 900 次决策**。日志实证（tick 间隔 64、内容完全相同）：
 *
 * ```
 * [FoodControl] tick=154240 day=3(Spring) 存量=34.2 … 动作=PlantCrop Plant_Rice ×41
 * [FoodControl] tick=154304 day=3(Spring) 存量=34.2 … 动作=PlantCrop Plant_Rice ×41
 * ```
 *
 * 它还污染了另一个观察：**看起来像"结论没变"，其实是同一轮被重复执行了 900 次**。
 *
 * ## 判定纪律
 *
 * 不能用"日志里有决策"当通过 —— 那正是漏掉这个 bug 的原因（有日志，但刷了 900 遍）。
 * 必须断言**数量**：推进一个游戏日，同一轮决策的次数应该是 **1**，不是几百。
 *
 * ⚠ 只**附着**到已在运行的游戏，自己绝不启动游戏。
 * 用法：node scripts/verify-s2b-dedup.mjs
 */
import fs from 'node:fs';
import path from 'node:path';
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor, WS } from '../src/launcher.js';

const PROFILE = 'vanilla';
const CALL = 120_000;
const RUN_DIR = path.join(WS, 'runs', 's2b-dedup');
const DAY_TICKS = 60_000;

let client = null;
const steps = [];
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
  return closed;
}

/** 地图必须稳定：programState==Playing 且 ticksGame>0 */
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

async function main() {
  const info = readBridgeInfo(logPathFor(PROFILE));
  if (!info) throw new Error('没有桥接信息 —— 先 node src/cli.js start（后台任务托管）');
  const conn = await connectGabp(info, { requestTimeoutMs: CALL });
  client = conn.client;
  log(`桥接 ${info.host}:${info.port}`);

  try {
    // ── 0. 地图就绪 ────────────────────────────────────────────
    let st = await ready();
    if (st.stage === 'not_loaded' || st.stage === 'no_map') {
      log(`开调试殖民地（阶段 ${st.stage}，超时预期）...`);
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
    record('地图稳定可用 + 殖民者就绪', st.ok && st.c > 0, `阶段=${st.stage}，${st.c} 人，tick=${st.ticks}`);
    await clearForcePause();

    const health = await call('foodprobe/health');
    record('mod 已加载', health?.modLoaded === true, health?.modLoaded ? '' : health?.message);

    // ── 1. 域注册情况 ──────────────────────────────────────────
    log('\n=== 1. 域协调器状态 ===');
    const d0 = await call('foodprobe/domains');
    record('domains 工具可调用', d0?.success === true, d0?.message || '');
    record('至少注册了 1 个域', (d0?.domainCount ?? 0) >= 1,
      `注册 ${d0?.domainCount} 个：${(d0?.domains || []).map((x) => x.id).join(',')}`);
    log(`  当前 day=${d0?.day} hour=${d0?.hour} totalCycles=${d0?.totalCycles}`);
    log(`  域: ${JSON.stringify(d0?.domains)}`);

    // ── 2. ★ 核心：推进一个游戏日，同一轮决策只能跑 1 次 ─────────
    log('\n=== 2. ★ 按天去重（bug 回归测试）===');

    // 清掉去重记录，让它今天可以再跑，便于观察
    await call('foodprobe/reset_domain_days');

    const before = await call('foodprobe/domains');
    const cyclesBefore = before?.totalCycles ?? 0;
    const dayBefore = before?.day ?? 0;
    log(`  前: day=${dayBefore} totalCycles=${cyclesBefore}`);

    // 推进**一个游戏日**。注意：总周期数只在 DidWork 时增加，
    // 而食物域在"不需要动手"时 DidWork=false —— 所以只靠 totalCycles 不够，
    // 还要看**日志里同一轮出现的次数**。
    log('  推进一个游戏日（60000 tick）...');
    let adv = 0;
    for (let i = 0; i < 30 && adv < DAY_TICKS + 10_000; i++) {
      await clearForcePause();
      const p = await call('rimworld/play_for', {
        durationMs: 8000, speed: 'Ultrafast', forceRequestedSpeed: true, pollIntervalMs: 25,
      }, 90_000);
      adv += p?.advancedTicks ?? 0;
    }
    record('时间推进（覆盖一个游戏日）', adv >= DAY_TICKS, `${adv} tick`);

    const after = await call('foodprobe/domains');
    const cyclesAfter = after?.totalCycles ?? 0;
    const dayAfter = after?.day ?? 0;
    log(`  后: day=${dayAfter} totalCycles=${cyclesAfter}`);

    // ★ 关键断言：totalCycles 的增长不能超过"跨过的天数 × 域数"太多。
    //   旧 bug 下这里是几百（每 64 tick 一次）。
    const daysCrossed = Math.max(1, dayAfter - dayBefore);
    const domainCount = Math.max(1, after?.domainCount ?? 1);
    const maxReasonable = daysCrossed * domainCount + 2;   // 留一点余量
    record('★★ totalCycles 增长在合理范围（不是一天几百次）',
      cyclesAfter - cyclesBefore <= maxReasonable,
      `增长 ${cyclesAfter - cyclesBefore}，上限 ${maxReasonable}（跨 ${daysCrossed} 天 × ${domainCount} 域）`);

    // 另一个角度：每个域"今天是否已跑过"应该是 true（说明去重生效了）
    const ranToday = (after?.domains || []).filter((x) => x.ranToday).length;
    record('★ 推进后各域标记为"今天已跑过"（去重表生效）', ranToday >= 1,
      `${ranToday}/${after?.domainCount} 个域 ranToday=true`);

    // ── 3. 日志实证：同一 tick 附近不应重复刷同样的决策 ──────────
    log('\n=== 3. 日志实证（数一下有没有在同一天刷几百遍）===');
    const logFile = logPathFor(PROFILE);
    const text = fs.readFileSync(logFile, 'utf8');
    const lines = text.split(/\r?\n/).filter((l) => l.includes('[FoodControl]'));
    // 只统计"决策轮"那种带 tick= 的行
    const decisionLines = lines.filter((l) => /tick=\d+/.test(l));
    // 按 (day, 动作摘要) 分组，数每组出现几次
    const groups = new Map();
    for (const l of decisionLines) {
      const m = /tick=(\d+)\s+day=(\d+)/.exec(l);
      if (!m) continue;
      const day = m[2];
      // 用"去掉 tick 之后的整行"当指纹：内容相同 = 同一轮被重复执行
      const fingerprint = day + '|' + l.replace(/tick=\d+/g, 'tick=N').slice(0, 200);
      groups.set(fingerprint, (groups.get(fingerprint) || 0) + 1);
    }
    let worst = 0;
    let worstKey = '';
    for (const [k, v] of groups) {
      if (v > worst) { worst = v; worstKey = k; }
    }
    log(`  决策日志共 ${decisionLines.length} 行，不同指纹 ${groups.size} 组`);
    log(`  同一指纹最多重复 ${worst} 次`);
    if (worstKey) log(`    最重复的一组: ${worstKey.slice(0, 160)}`);

    // 判定：同一天内同一个指纹不该出现几十上百次。
    // 允许少量重复（比如跨天边界、或参数在不同时刻略有不同），所以阈值给 20。
    record('★★ 日志里没有"同一天同一指纹刷几十遍"', worst <= 20,
      `最多重复 ${worst} 次（旧 bug 下会是几百）`);
  } finally {
    client.close();
  }

  const passed = steps.filter((s) => s.ok === true).length;
  const failed = steps.filter((s) => s.ok === false).length;
  const skipped = steps.filter((s) => s.ok === null).length;

  fs.mkdirSync(RUN_DIR, { recursive: true });
  fs.writeFileSync(path.join(RUN_DIR, 'report.json'),
    JSON.stringify({ passed, failed, skipped, steps }, null, 2), 'utf8');

  const md = [
    '# S2b 验证报告：域协调器按天去重（bug 回归测试）',
    '',
    `时间：${new Date().toISOString()}`,
    `结果：**通过 ${passed} / 失败 ${failed} / 跳过 ${skipped}**`,
    '',
    '| 结果 | 项目 | 细节 |',
    '| --- | --- | --- |',
    ...steps.map((s) => `| ${s.ok === null ? '—' : s.ok ? '✅' : '❌'} | ${s.label} | ${(s.detail || '').replace(/\|/g, '\\|')} |`),
    '',
    '> 背景：重构时删掉了按天去重，导致**一天跑约 900 次决策**（日志实证 tick 间隔 64、内容相同）。',
    '> 本测试断言**数量**而不是"有没有决策" —— 后者正是当初漏掉这个 bug 的原因。',
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
