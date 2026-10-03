/**
 * 复测 play_for(Ultrafast) 的可重复性 —— 上次单次跑出 7110 tick/s，但被外部暂停打断。
 *
 * 回答两个问题：
 *   1) 反复调用 play_for，每次都能拿到 ~7000 tick/s 吗？（还是只有第一次快）
 *   2) 外部暂停发生的频率有多高？（这决定长跑要不要重试逻辑）
 *
 * 输出：runs/food-recon/speed-repeat.json
 */
import fs from 'node:fs';
import path from 'node:path';
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor, WS } from '../src/launcher.js';

const PROFILE = 'vanilla';
const CALL_TIMEOUT = 120_000;
const ROUNDS = 5;
const DURATION_MS = 10_000;

async function main() {
  const info = readBridgeInfo(logPathFor(PROFILE));
  const { client } = await connectGabp(info, { requestTimeoutMs: CALL_TIMEOUT });

  const call = async (name, args = {}, timeoutMs = CALL_TIMEOUT) => {
    const t0 = Date.now();
    const res = await client.callTool(name, args, { timeoutMs });
    return { res, ms: Date.now() - t0 };
  };

  const out = { startedAt: new Date().toISOString(), rounds: [] };

  try {
    // 确保未暂停且 Ultrafast
    await call('rimworld/set_time_speed', { speed: 'Ultrafast', ultraSpeedBoost: true });

    for (let i = 0; i < ROUNDS; i++) {
      const before = await call('rimworld/get_game_info');
      const p = await call(
        'rimworld/play_for',
        { durationMs: DURATION_MS, speed: 'Ultrafast', forceRequestedSpeed: true, pollIntervalMs: 25 },
        DURATION_MS + 60_000,
      );
      const after = await call('rimworld/get_game_info');
      const r = p.res || {};
      const dt = (after.res?.ticksGame ?? 0) - (before.res?.ticksGame ?? 0);
      out.rounds.push({
        round: i + 1,
        wallMs: p.ms,
        successFlag: r.success,
        requestedMs: r.requestedDurationMs,
        elapsedMs: r.elapsedMs,
        gotFullDuration: r.elapsedMs >= DURATION_MS * 0.95,
        advancedTicks: dt,
        advancedTicksReported: r.advancedTicks,
        ticksPerSec: p.ms > 0 ? +(dt / (p.ms / 1000)).toFixed(1) : null,
        pausedExternally: r.message ? /paused externally/i.test(r.message) : null,
        message: r.message ?? null,
        finalPaused: r.paused ?? null,
      });
      // 每轮之间确保恢复
      await call('rimworld/set_time_speed', { speed: 'Ultrafast', ultraSpeedBoost: true });
    }

    const rates = out.rounds.map((r) => r.ticksPerSec).filter((x) => x != null);
    const interrupted = out.rounds.filter((r) => r.pausedExternally).length;
    out.verdict = {
      rounds: ROUNDS,
      ticksPerSecMin: rates.length ? Math.min(...rates) : null,
      ticksPerSecMax: rates.length ? Math.max(...rates) : null,
      ticksPerSecAvg: rates.length ? +(rates.reduce((a, b) => a + b, 0) / rates.length).toFixed(1) : null,
      roundsInterruptedExternally: interrupted,
      reliability: interrupted === 0 ? 'clean' : `interrupted ${interrupted}/${ROUNDS}`,
      minutesFor15Days_atAvg:
        rates.length && rates.reduce((a, b) => a + b, 0) > 0
          ? +((15 * 60000) / (rates.reduce((a, b) => a + b, 0) / rates.length) / 60).toFixed(1)
          : null,
    };
  } finally {
    client.close();
  }

  const file = path.join(WS, 'runs', 'food-recon', 'speed-repeat.json');
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, JSON.stringify(out, null, 2), 'utf8');
  console.log(JSON.stringify(out.verdict, null, 2));
  console.log('\n每轮:');
  for (const r of out.rounds) {
    console.log(
      `  #${r.round}  ${String(r.ticksPerSec).padStart(8)} tick/s  ` +
        `跑了 ${r.elapsedMs}/${r.requestedMs}ms  ` +
        `外部暂停=${r.pausedExternally}  ${r.message || ''}`,
    );
  }
  console.log('\n→ ' + file);
}

main().catch((e) => {
  console.error('FAILED: ' + e.message);
  process.exitCode = 1;
});
