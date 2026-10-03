/**
 * 对比两种长跑推进方式的真实速度：
 *   1) step_game_ticks —— 精确但每 tick 一帧
 *   2) set_time_speed(Ultrafast) + play_for —— 真实速度跑
 *
 * 目的：决定「15 游戏天」的回归测试跑不跑得起。
 * 输出：runs/food-recon/speed-compare.json
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

  const out = { startedAt: new Date().toISOString(), methods: {} };

  try {
    const g0 = await call('rimworld/get_game_info');
    out.startTicks = g0.res?.ticksGame;

    // --- 方法 1：step_game_ticks ---
    // 注意：工具级 timeoutMs 默认 10000，必须显式传大值
    const STEP_TICKS = 15000;
    const s = await call('rimworld/step_game_ticks', {
      ticks: STEP_TICKS,
      timeoutMs: 110000,
      pauseFirst: true,
    });
    const afterStep = await call('rimworld/get_game_info');
    out.methods.step = {
      requestedTicks: STEP_TICKS,
      wallMs: s.ms,
      status: s.res?.status,
      advancedTicks: s.res?.advancedTicks ?? s.res?.advancedFrames,
      ticksBefore: g0.res?.ticksGame,
      ticksAfter: afterStep.res?.ticksGame,
      ticksPerSec: null,
    };
    const dtStep = (out.methods.step.ticksAfter ?? 0) - (out.methods.step.ticksBefore ?? 0);
    out.methods.step.ticksActuallyAdvanced = dtStep;
    out.methods.step.ticksPerSec = s.ms > 0 ? +(dtStep / (s.ms / 1000)).toFixed(1) : null;

    // --- 方法 2：Ultrafast + play_for ---
    const PLAY_MS = Math.round(s.ms); // 用同样长的墙钟时间对比
    const spd = await call('rimworld/set_time_speed', {
      speed: 'Ultrafast',
      ultraSpeedBoost: true,
    });
    out.methods.play = { setSpeedStatus: spd.res?.status ?? spd.res, playForMs: PLAY_MS };
    const before = await call('rimworld/get_game_info');
    const p = await call(
      'rimworld/play_for',
      {
        durationMs: PLAY_MS,
        speed: 'Ultrafast',
        forceRequestedSpeed: true,
        pollIntervalMs: 25,
      },
      PLAY_MS + 60_000,
    );
    const after = await call('rimworld/get_game_info');
    out.methods.play.wallMs = p.ms;
    out.methods.play.status = p.res?.status ?? p.res;
    out.methods.play.ticksBefore = before.res?.ticksGame;
    out.methods.play.ticksAfter = after.res?.ticksGame;
    const dtPlay = (after.res?.ticksGame ?? 0) - (before.res?.ticksGame ?? 0);
    out.methods.play.ticksActuallyAdvanced = dtPlay;
    out.methods.play.ticksPerSec = p.ms > 0 ? +(dtPlay / (p.ms / 1000)).toFixed(1) : null;

    // 结论
    const st = out.methods.step.ticksPerSec || 0;
    const pl = out.methods.play.ticksPerSec || 0;
    out.verdict = {
      faster: pl > st ? 'play_for(Ultrafast)' : 'step_game_ticks',
      ratio: st > 0 ? +(pl / st).toFixed(2) : null,
      gameDayTicks: 60000,
      minutesFor15Days_play: pl > 0 ? +((15 * 60000) / pl / 60).toFixed(1) : null,
      minutesFor15Days_step: st > 0 ? +((15 * 60000) / st / 60).toFixed(1) : null,
    };
  } finally {
    client.close();
  }

  const file = path.join(WS, 'runs', 'food-recon', 'speed-compare.json');
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, JSON.stringify(out, null, 2), 'utf8');
  console.log(JSON.stringify(out, null, 2));
  console.log('\n→ ' + file);
}

main().catch((e) => {
  console.error('FAILED: ' + e.message);
  process.exitCode = 1;
});
