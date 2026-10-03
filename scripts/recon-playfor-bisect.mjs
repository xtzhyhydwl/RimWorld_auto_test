/**
 * 二分定位 play_for 秒退（elapsedMs≈6, advancedTicks=0）的触发条件。
 *
 * 复测脚本 5/5 秒退；诊断脚本正常。两者差别之一：复测脚本在 play_for 前调了 get_game_info。
 * 本脚本用受控序列矩阵验证「是不是它」。
 *
 * 输出：runs/food-recon/playfor-bisect.json
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

  /** 跑一个 play_for 试验，返回结果摘要 */
  const trial = async (label, { before = [], speed = 'Ultrafast', durationMs = 2500 } = {}) => {
    const pre = [];
    for (const step of before) {
      if (step.type === 'sleep') { await sleep(step.ms); pre.push(`sleep(${step.ms})`); continue; }
      if (step.type === 'setSpeed') {
        const r = await call('rimworld/set_time_speed', { speed: step.speed, ultraSpeedBoost: true });
        pre.push(`setSpeed(${step.speed}) -> paused=${r.res?.state?.paused}`);
        continue;
      }
      if (step.type === 'getInfo') {
        const r = await call('rimworld/get_game_info');
        pre.push(`getInfo -> ticks=${r.res?.ticksGame}`);
        continue;
      }
    }
    const p = await call('rimworld/play_for', { durationMs, speed, forceRequestedSpeed: true }, durationMs + 60_000);
    return {
      label,
      pre,
      elapsedMs: p.res?.elapsedMs,
      advancedTicks: p.res?.advancedTicks,
      advancedTicksReported: p.res?.advancedTicks,
      initiallyPaused: p.res?.initiallyPaused,
      success: p.res?.success,
      message: p.res?.message,
      secondsFromDiagnose: p.res?.message?.match(/after (\d+)ms/)?.[1] ?? null,
    };
  };

  const results = [];
  const push = (r) => {
    results.push(r);
    const ok = (r.advancedTicks ?? 0) > 1000;
    console.log(
      `[${ok ? ' OK ' : 'FAIL'}] ${r.label.padEnd(46)} elapsed=${String(r.elapsedMs).padStart(5)}ms ` +
        `ticks=${String(r.advancedTicks).padStart(6)} initiallyPaused=${r.initiallyPaused} :: ${r.pre.join(' | ')}`,
    );
  };

  try {
    // A: 当前状态直接 play_for（不预置任何东西）
    push(await trial('A. 裸 play_for'));

    // B: get_game_info -> play_for   （怀疑对象）
    push(await trial('B. getInfo -> play_for', { before: [{ type: 'getInfo' }] }));

    // C: setSpeed -> play_for
    push(await trial('C. setSpeed -> play_for', { before: [{ type: 'setSpeed', speed: 'Ultrafast' }] }));

    // D: setSpeed -> getInfo -> play_for （复测脚本的确切序列）
    push(
      await trial('D. setSpeed -> getInfo -> play_for', {
        before: [{ type: 'setSpeed', speed: 'Ultrafast' }, { type: 'getInfo' }],
      }),
    );

    // D2: 重复 D，确认不是偶然
    push(
      await trial('D2. 同 D 重跑', {
        before: [{ type: 'setSpeed', speed: 'Ultrafast' }, { type: 'getInfo' }],
      }),
    );

    // E: setSpeed -> getInfo -> sleep -> play_for
    push(
      await trial('E. setSpeed -> getInfo -> sleep500 -> play_for', {
        before: [{ type: 'setSpeed', speed: 'Ultrafast' }, { type: 'getInfo' }, { type: 'sleep', ms: 500 }],
      }),
    );

    // F: setSpeed -> sleep -> play_for
    push(
      await trial('F. setSpeed -> sleep500 -> play_for', {
        before: [{ type: 'setSpeed', speed: 'Ultrafast' }, { type: 'sleep', ms: 500 }],
      }),
    );

    // G: 连续三次裸 play_for（看是否第一次必失败）
    for (let i = 1; i <= 3; i++) {
      push(await trial(`G${i}. 裸 play_for 第${i}次`));
    }

    // H: play_for 被要求 Paused 时行为
    push(await trial('H. 裸 play_for（speed=Frozen/Paused）', { speed: 'Paused' }));
  } finally {
    client.close();
  }

  const okCount = results.filter((r) => (r.advancedTicks ?? 0) > 1000).length;
  const verdict = {
    total: results.length,
    ok: okCount,
    failed: results.length - okCount,
    failingLabels: results.filter((r) => (r.advancedTicks ?? 0) <= 1000).map((r) => r.label),
    okLabels: results.filter((r) => (r.advancedTicks ?? 0) > 1000).map((r) => r.label),
  };

  const file = path.join(WS, 'runs', 'food-recon', 'playfor-bisect.json');
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, JSON.stringify({ verdict, results }, null, 2), 'utf8');
  console.log('\n' + JSON.stringify(verdict, null, 2));
  console.log('\n→ ' + file);
}

main().catch((e) => {
  console.error('FAILED: ' + e.message);
  process.exitCode = 1;
});
