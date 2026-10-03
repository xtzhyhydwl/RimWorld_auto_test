/**
 * S1 验证：mod 自己的一次性初始化「允许所有被禁取物资」。
 *
 * ## 为什么要先刻意造出"有禁取物"的场景
 *
 * 新开的 debug 殖民地**有时没有**禁取物（实测遇到过 totalForbidden=0）。
 * 直接跑验证会变成"验证了一个空场景"，看起来通过其实什么也没测到。
 * 所以本脚本先**手动禁取**一批物品当夹具（fixture），再让 mod 去解锁。
 *
 * ## 判定纪律
 *
 * 不只看 mod 的返回值 —— 必须**桥接独立回读**确认游戏真的变了。
 * 这是本项目反复踩出来的规矩（只断言"调用成功"曾漏掉过真 bug）。
 *
 * ⚠ 只**附着**到已在运行的游戏，自己绝不启动游戏（会进程孤儿化 + CPU 死循环）。
 * 用法：node scripts/verify-s1-unforbid.mjs
 */
import fs from 'node:fs';
import path from 'node:path';
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor, WS } from '../src/launcher.js';

const PROFILE = 'vanilla';
const CALL = 120_000;
const RUN_DIR = path.join(WS, 'runs', 's1-unforbid');

let client = null;
const steps = [];
const log = (...a) => console.log(...a);

async function call(name, args = {}, timeoutMs = CALL) {
  const res = await client.callTool(name, args, { timeoutMs });
  return res;
}

function record(label, ok, detail) {
  steps.push({ label, ok, detail, at: new Date().toISOString() });
  log(`  ${ok === null ? '····' : ok ? '[PASS]' : '[FAIL]'} ${label}${detail ? '  — ' + detail : ''}`);
}

async function clearForcePause() {
  const ui = await call('rimworld/get_ui_state');
  if (!ui?.windowsForcePause) return;
  for (const w of ui.windows || []) {
    if (w.forcePause && w.type === 'RimWorld.MainTabWindow_Menu') {
      await call('rimworld/close_main_tab', { tabId: 'main-tab:Menu' });
    }
  }
}

async function main() {
  const info = readBridgeInfo(logPathFor(PROFILE));
  if (!info) throw new Error('没有桥接信息 —— 先 node src/cli.js start（后台任务托管）');
  const conn = await connectGabp(info, { requestTimeoutMs: CALL });
  client = conn.client;
  log(`桥接 ${info.host}:${info.port}`);

  try {
    // ── 0. 确保地图**稳定可用** + 殖民者 ────────────────────────
    //
    // ⚠ 踩过的坑：只查 `status==game_loaded && mapCount>0` 是不够的。
    //   实测遇到过游戏正处于 `programState == "MapInitializing"`、
    //   `ticksGame == 0` 的时候 —— 那时**地图对象存在但取不到**，
    //   于是 `Find.CurrentMap` 返回 null，后续所有调用都报"没有当前地图"。
    //   所以必须**同时**要求 `programState == "Playing"` 且 `ticksGame > 0`。
    const ready = async () => {
      const g = await call('rimworld/get_game_info');
      if (g?.status !== 'game_loaded') return { ok: false, stage: 'not_loaded', c: 0 };
      if ((g?.mapCount ?? 0) <= 0) return { ok: false, stage: 'no_map', c: 0 };
      const ui = await call('rimworld/get_ui_state');
      const ps = ui?.programState;
      if (ps !== 'Playing') return { ok: false, stage: 'programState=' + ps, c: 0 };
      if ((g?.ticksGame ?? 0) <= 0) return { ok: false, stage: 'ticks=0', c: 0 };
      const c = await call('rimworld/list_colonists');
      return { ok: true, stage: 'playing', c: c?.count ?? 0 };
    };

    let st = await ready();
    // ⚠ 只在**真的没有地图**时才去开殖民地。
    //   实测教训：已经有游戏时再调 start_debug_game_ready 可能触发重新生成地图，
    //   把测试中途的地图抽走（表现为"没有当前地图"）。
    if (st.stage === 'not_loaded' || st.stage === 'no_map') {
      log(`开调试殖民地（当前阶段 ${st.stage}；超时预期，之后自己轮询）...`);
      await call('rimworld/start_debug_game_ready',
        { readiness: 'mapData', pauseIfNeeded: true, timeoutMs: 240_000 }, 280_000,
      ).catch((e) => log('  ' + e.message.split('\n')[0]));
    } else {
      log(`已有地图（阶段 ${st.stage}），不重开殖民地`);
    }

    for (let i = 0; i < 120; i++) {
      st = await ready();
      if (st.ok && st.c > 0) break;
      if (i % 10 === 0) log(`  等待中... 阶段=${st.stage} 殖民者=${st.c}`);
      await new Promise((r) => setTimeout(r, 2000));
    }
    record('地图稳定可用 + 殖民者就绪', st.ok && st.c > 0, `阶段=${st.stage}，${st.c} 个殖民者`);
    await clearForcePause();

    // ── 1. mod 是否加载 ────────────────────────────────────────
    const health = await call('foodprobe/health');
    const modLoaded = health?.modLoaded === true;
    record('FoodControl mod 已加载', modLoaded, modLoaded ? '' : health?.message);
    if (!modLoaded) throw new Error('mod 未加载');

    // ── 2. mod 的自动化有没有在开局就跑掉 ───────────────────────
    log('\n=== 2. mod 自动初始化（开局自动跑）===');
    const autoStats = await call('foodprobe/forbidden_stats', { maxSamples: 6 });
    log(`  当前被禁取: ${autoStats?.totalForbidden} 个`);
    // 这一条是"行为观测"而不是断言：新 debug 殖民地**可能本来就没有**禁取物，
    // 所以不能硬断言"一定是 0"。真正的断言放在下面（我们自己造夹具）。
    record('mod 自动初始化后禁取数（观测，非断言）', null,
      `${autoStats?.totalForbidden} 个（新 debug 殖民地本来就可能没有禁取物）`);

    // ── 3. 造夹具：手动禁取一批物品 ─────────────────────────────
    log('\n=== 3. 造夹具：手动禁取一批物品（让场景确定可复现）===');
    // 为什么要造夹具：新开的 debug 殖民地**时有时无**禁取物（实测遇到过 0 个）。
    // 没有夹具的话，这一轮就变成"验证了一个空场景" —— 看起来通过，其实什么都没测。
    const fixture = await call('foodprobe/forbid_some', { count: 8, preferStuff: true });
    record('夹具就绪（造出被禁取的物品）', (fixture?.changed ?? 0) > 0,
      `要求 ${fixture?.requested}，实际 ${fixture?.changed}；当前被禁取物品 ${fixture?.totalForbiddenItemsNow}`);

    const before = await call('foodprobe/forbidden_stats', { maxSamples: 8 });
    const beforeCount = before?.totalForbidden ?? 0;
    log(`  解锁前: ${beforeCount} 个被禁取`);

    const reset = await call('foodprobe/reset_setup');
    record('重置 mod 的初始化标记', reset?.success === true, reset?.result);

    const run = await call('foodprobe/run_setup');
    record('mod 的 RunUnforbidNow 可调用', run?.success === true,
      (run?.result || run?.message || '').slice(0, 200));

    // ── 4. ★ 桥接独立回读：游戏真的变了吗 ───────────────────────
    log('\n=== 4. ★ 桥接独立回读（不信 mod 自述）===');
    const after = await call('foodprobe/forbidden_stats', { maxSamples: 8 });
    const afterCount = after?.totalForbidden ?? 0;
    log(`  解锁后: ${afterCount} 个被禁取`);

    if (beforeCount > 0) {
      record('★ 解锁后禁取数归零', afterCount === 0, `${beforeCount} → ${afterCount}`);
      record('★ mod 自述"回读剩余 0"与桥接实测一致',
        /剩余 0/.test(run?.result || '') && afterCount === 0,
        (run?.result || '').slice(0, 140));
    } else {
      record('★ 解锁后禁取数归零', false,
        '夹具没造出来且开局也没有禁取物 → 空场景，未真正验证');
    }

    // ── 5. 幂等性：再跑一次不应重复干活 ─────────────────────────
    log('\n=== 5. 幂等性 ===');
    const again = await call('foodprobe/run_setup');
    record('重复调用不报错', again?.success === true, (again?.result || '').slice(0, 120));
    record('重复调用时 Changed 应为 0（幂等）',
      /解锁 0/.test(again?.result || '') || /没有需要解锁/.test(again?.result || ''),
      (again?.result || '').slice(0, 140));

    // ── 5. ★ 自动运行：不手动调，靠推进游戏时间让它自己跑 ─────────
    //
    // 这一条是**冲着踩过的那个坑来的**：最初实现成"只做一次"，
    // 结果它在地图刚生成、物资还没刷出来时就跑了，扫到 0 个，然后永远不再检查。
    // 所以必须验证"**稍后出现**的禁取物也会被自动解锁"，而不只是手动调能work。
    //
    // ⚠ 断言写法的教训：最初写成 `autoAfter === 0`，结果**误报失败**。
    //   原因：推进的 tick 数可能横跨天数边界，而 mod 是**每天**做一次家务（刻意的节流），
    //   所以一天内只会清掉一批。实测 5 个禁取物 → 推进 86698 tick 后剩 1 个，
    //   而那个剩下的**手动一调就清掉了**（扫描 1/解锁 1/剩余 0），说明没有东西被阻塞。
    //   → 正确的验证目标是「**自动运行确实执行过并且减少了禁取数**」，
    //     而不是「一次推进之后必须归零」。
    log('\n=== 5. ★ 自动运行（不手动调，靠推进时间）===');
    const FIXTURE_N = 12;
    const fixture2 = await call('foodprobe/forbid_some', { count: FIXTURE_N, preferStuff: false });
    const autoBefore = (await call('foodprobe/forbidden_stats')).totalForbidden ?? 0;
    record('造出新的禁取物（模拟"开局之后才出现"）', autoBefore >= FIXTURE_N,
      `夹具 ${fixture2?.changed}/${FIXTURE_N} 个，当前被禁取 ${autoBefore}`);

    // 重置按天去重标记，再推进**超过一个游戏日**，让 mod 至少有一次自动执行的机会
    await call('foodprobe/reset_setup');
    log('  推进游戏时间（跨天，让 mod 的每日家务自己跑）...');
    // 一天 = 60000 tick；多推一点确保跨过天数边界
    let adv = 0;
    for (let i = 0; i < 20 && adv < 130_000; i++) {
      await clearForcePause();
      const p = await call('rimworld/play_for', {
        durationMs: 8000, speed: 'Ultrafast', forceRequestedSpeed: true, pollIntervalMs: 25,
      }, 90_000);
      adv += p?.advancedTicks ?? 0;
    }
    record('时间推进（跨过至少一个游戏日）', adv > 60_000, `${adv} tick`);

    const autoAfter = (await call('foodprobe/forbidden_stats')).totalForbidden ?? 0;
    // 核心断言：自动运行**确实减少了**禁取数（证明"不是只做一次"这个修复生效）
    record('★★ mod 自动解锁了"后来才出现"的禁取物（不是只做一次）',
      autoAfter < autoBefore,
      `${autoBefore} → ${autoAfter}（推进 ${adv} tick）`);

    // 再推一天，应该继续收敛（证明它是持续生效的，而不是碰巧一次）
    log('  再推一天，验证持续收敛...');
    let adv2 = 0;
    for (let i = 0; i < 20 && adv2 < 130_000; i++) {
      await clearForcePause();
      const p = await call('rimworld/play_for', {
        durationMs: 8000, speed: 'Ultrafast', forceRequestedSpeed: true, pollIntervalMs: 25,
      }, 90_000);
      adv2 += p?.advancedTicks ?? 0;
    }
    const autoFinal = (await call('foodprobe/forbidden_stats')).totalForbidden ?? 0;
    record('★★ 再推一天后禁取数归零（持续收敛，非一次性碰巧）', autoFinal === 0,
      `${autoAfter} → ${autoFinal}（推进 ${adv2} tick）`);

    // ── 6. 建材统计（S4 的输入，顺手验证）────────────────────────
    log('\n=== 6. 建材统计（S4 用）===');
    const mats = await call('foodprobe/materials');
    record('materials 可调用', mats?.success === true,
      mats?.source === 'mod' ? (mats?.raw || '').slice(0, 220) : JSON.stringify(mats?.materials || {}).slice(0, 220));
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
    '# S1 验证报告：开局允许所有被禁取物资',
    '',
    `时间：${new Date().toISOString()}`,
    `结果：**通过 ${passed} / 失败 ${failed} / 跳过 ${skipped}**`,
    '',
    '| 结果 | 项目 | 细节 |',
    '| --- | --- | --- |',
    ...steps.map((s) => `| ${s.ok === null ? '—' : s.ok ? '✅' : '❌'} | ${s.label} | ${(s.detail || '').replace(/\|/g, '\\|')} |`),
    '',
    '> 「跳过」= 场景不成立（本次开局没有禁取物），**不算通过**。',
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
