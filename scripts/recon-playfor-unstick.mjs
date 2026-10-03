/**
 * 定位「play_for 用几次后永久秒退」的机制。
 *
 * 二分脚本的观察：前 4 次 trial 全部 OK，从第 5 次起全部 FAIL（elapsed≈6ms, advancedTicks=0,
 * initiallyPaused=true），且此后无法恢复。
 *
 * 新假设：游戏被一个「持久化暂停」卡住了（RimWorld 有 paused 标志 / 强制暂停源），
 * play_for 的 unpause 动作被它覆盖。
 *
 * 本脚本：
 *   1. 确认当前是否卡在秒退状态
 *   2. 试 rimworld/pause_game（toggle）能否解开
 *   3. 试关掉可能强制暂停的 dev 设置
 *   4. 检查 UI 里是否有 forcePause 的窗口（forcePause 字段）
 *   5. 试 play_for 的其它参数组合
 *
 * 输出：runs/food-recon/playfor-unstick.json
 */
import fs from 'node:fs';
import path from 'node:path';
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor, WS } from '../src/launcher.js';

const PROFILE = 'vanilla';
const CALL_TIMEOUT = 120_000;

async function main() {
  const info = readBridgeInfo(logPathFor(PROFILE));
  const { client } = await connectGabp(info, { requestTimeoutMs: CALL_TIMEOUT });

  const call = async (name, args = {}, timeoutMs = CALL_TIMEOUT) => {
    const t0 = Date.now();
    const res = await client.callTool(name, args, { timeoutMs });
    return { res, ms: Date.now() - t0 };
  };
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

  const log = [];
  const say = (label, data) => {
    log.push({ label, data });
    console.log(`\n### ${label}`);
    console.log(JSON.stringify(data).slice(0, 1200));
  };

  const probe = async (label) => {
    const r = await call('rimworld/play_for', { durationMs: 2000, speed: 'Ultrafast', forceRequestedSpeed: true }, 60_000);
    const ok = (r.res?.advancedTicks ?? 0) > 500;
    say(`${label} -> ${ok ? 'OK' : 'FAIL'}`, {
      elapsedMs: r.res?.elapsedMs,
      advancedTicks: r.res?.advancedTicks,
      initiallyPaused: r.res?.initiallyPaused,
      paused: r.res?.paused,
      finalNeverForceNormalSpeed: r.res?.finalNeverForceNormalSpeed,
      restoredNeverForceNormalSpeed: r.res?.restoredNeverForceNormalSpeed,
      message: r.res?.message,
    });
    return ok;
  };

  try {
    // 1. 基线：现在是否卡死
    await probe('1. 基线 play_for');

    // 2. UI 现状：有没有 forcePause 的窗口
    const ui = await call('rimworld/get_ui_state');
    say('2. UI 快照', {
      windowsForcePause: ui.res?.windowsForcePause,
      nonImmediateDialogWindowOpen: ui.res?.nonImmediateDialogWindowOpen,
      floatMenuOpen: ui.res?.floatMenuOpen,
      mainTabOpen: ui.res?.mainTabOpen,
      openMainTabId: ui.res?.openMainTabId,
      windowCount: ui.res?.windowCount,
      forcePauseWindows: (ui.res?.windows || []).filter((w) => w.forcePause).map((w) => w.type),
      focusedWindowType: ui.res?.focusedWindowType,
    });

    // 3. 用 pause_game toggle 一次
    const pg = await call('rimworld/pause_game');
    say('3. pause_game()', { status: pg.res?.status ?? pg.res, success: pg.res?.success, paused: pg.res?.paused, message: pg.res?.message });
    await sleep(300);
    await probe('3b. pause_game 之后 play_for');

    // 4. 再 toggle 一次
    const pg2 = await call('rimworld/pause_game');
    say('4. pause_game() 第二次', { paused: pg2.res?.paused, message: pg2.res?.message });
    await sleep(300);
    await probe('4b. 再 toggle 之后 play_for');

    // 5. 试试不传 forceRequestedSpeed
    const p5 = await call('rimworld/play_for', { durationMs: 2000, speed: 'Ultrafast' }, 60_000);
    say(`5. play_for 不传 forceRequestedSpeed -> ${(p5.res?.advancedTicks ?? 0) > 500 ? 'OK' : 'FAIL'}`, {
      elapsedMs: p5.res?.elapsedMs, advancedTicks: p5.res?.advancedTicks,
      initiallyPaused: p5.res?.initiallyPaused, message: p5.res?.message,
      finalNeverForceNormalSpeed: p5.res?.finalNeverForceNormalSpeed,
    });

    // 6. 试试较低速度
    for (const spd of ['Normal', 'Fast', 'Superfast']) {
      const p = await call('rimworld/play_for', { durationMs: 2000, speed: spd, forceRequestedSpeed: true }, 60_000);
      say(`6. play_for speed=${spd} -> ${(p.res?.advancedTicks ?? 0) > 200 ? 'OK' : 'FAIL'}`, {
        elapsedMs: p.res?.elapsedMs, advancedTicks: p.res?.advancedTicks,
        initiallyPaused: p.res?.initiallyPaused, message: p.res?.message,
      });
    }

    // 7. step_game_ticks 还能用吗（确认不是整个时间系统坏了）
    const st = await call('rimworld/step_game_ticks', { ticks: 600, timeoutMs: 60000, pauseFirst: true }, 90_000);
    say('7. step_game_ticks 复核', {
      status: st.res?.status, advancedTicks: st.res?.advancedTicks, advancedFrames: st.res?.advancedFrames, wallMs: st.ms,
    });

    // 8. 再试一次 play_for（看 step 之后能不能恢复）
    await probe('8. step 之后 play_for');

    // 9. 有没有「自动暂停」相关 dev 设置
    const ds = await call('rimworld/get_mod_settings');
    say('9. 相关 dev 设置（搜 pause/autopause）', { keys: Object.keys(ds.res || {}).slice(0, 40) });
  } finally {
    client.close();
  }

  const file = path.join(WS, 'runs', 'food-recon', 'playfor-unstick.json');
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, JSON.stringify({ log }, null, 2), 'utf8');
  console.log('\n→ ' + file);
}

main().catch((e) => {
  console.error('FAILED: ' + e.message);
  process.exitCode = 1;
});
