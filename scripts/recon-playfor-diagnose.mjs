/**
 * 诊断：为什么 play_for 会「被外部暂停」？
 *
 * 逐步打印状态，定位是谁在按暂停。
 * 输出：runs/food-recon/playfor-diagnose.json
 */
import fs from 'node:fs';
import path from 'node:path';
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor, WS } from '../src/launcher.js';

const PROFILE = 'vanilla';
const CALL_TIMEOUT = 120_000;

const KEYS = [
  'programState', 'inEntryScene', 'hasCurrentGame', 'paused', 'timeSpeed',
  'longEventPending', 'screenFading', 'fadeOverlayAlpha',
  'gameDataReady', 'mapDataReady', 'currentMapReady', 'screenFadeClear',
  'playable', 'visualReady', 'automationReady',
];

const pick = (s) => {
  if (!s) return null;
  const o = {};
  for (const k of KEYS) if (k in s) o[k] = s[k];
  return o;
};

async function main() {
  const info = readBridgeInfo(logPathFor(PROFILE));
  const { client } = await connectGabp(info, { requestTimeoutMs: CALL_TIMEOUT });

  const call = async (name, args = {}, timeoutMs = CALL_TIMEOUT) => {
    const t0 = Date.now();
    const res = await client.callTool(name, args, { timeoutMs });
    return { res, ms: Date.now() - t0 };
  };

  const steps = [];
  const rec = (label, data) => {
    steps.push({ label, at: new Date().toISOString(), ...data });
    console.log(`\n### ${label}`);
    console.log(JSON.stringify(data.step ?? data, null, 2).slice(0, 2000));
  };

  try {
    // 0. 有没有弹窗/对话框在拦着
    const ui = await call('rimworld/get_ui_state');
    rec('0. UI 状态（看有没有模态窗口）', { step: ui.res });

    // 1. 当前速度状态
    const s1 = await call('rimworld/set_time_speed', { speed: 'Ultrafast', ultraSpeedBoost: true });
    rec('1. set_time_speed(Ultrafast, boost)', { step: { msg: s1.res?.message, state: pick(s1.res?.state) } });

    // 2. 立即再查一次（谁把它改回暂停了？）
    await new Promise((r) => setTimeout(r, 500));
    const s2 = await call('rimworld/set_time_speed', { speed: 'Ultrafast', ultraSpeedBoost: true });
    rec('2. 500ms 后再查/再设', { step: { msg: s2.res?.message, state: pick(s2.res?.state) } });

    // 3. 一次很短的 play_for
    const p1 = await call('rimworld/play_for', { durationMs: 3000, speed: 'Ultrafast', forceRequestedSpeed: true }, 60_000);
    rec('3. play_for(3000ms)', {
      step: {
        success: p1.res?.success, elapsedMs: p1.res?.elapsedMs, advancedTicks: p1.res?.advancedTicks,
        initiallyPaused: p1.res?.initiallyPaused, paused: p1.res?.paused,
        message: p1.res?.message, state: pick(p1.res?.state),
      },
    });

    // 4. 再来一次相同的，看是否可重复
    const p2 = await call('rimworld/play_for', { durationMs: 3000, speed: 'Ultrafast', forceRequestedSpeed: true }, 60_000);
    rec('4. play_for(3000ms) 第二次', {
      step: {
        success: p2.res?.success, elapsedMs: p2.res?.elapsedMs, advancedTicks: p2.res?.advancedTicks,
        initiallyPaused: p2.res?.initiallyPaused, paused: p2.res?.paused,
        message: p2.res?.message, state: pick(p2.res?.state),
      },
    });

    // 5. 不强制速度的 play_for
    const p3 = await call('rimworld/play_for', { durationMs: 3000, speed: 'Ultrafast' }, 60_000);
    rec('5. play_for(3000ms) 不传 forceRequestedSpeed', {
      step: {
        success: p3.res?.success, elapsedMs: p3.res?.elapsedMs, advancedTicks: p3.res?.advancedTicks,
        message: p3.res?.message, state: pick(p3.res?.state),
      },
    });

    // 6. 用 play_for 之前先显式 unpause（看有没有 unpause 类工具）
    const tools = await client.listTools();
    const pauseish = tools.filter((t) => /pause|time|speed/i.test(t.name + ' ' + (t.description || '')));
    rec('6. 所有和暂停/时间相关的工具', { step: pauseish.map((t) => t.name) });
  } finally {
    client.close();
  }

  const file = path.join(WS, 'runs', 'food-recon', 'playfor-diagnose.json');
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, JSON.stringify({ steps }, null, 2), 'utf8');
  console.log('\n→ ' + file);
}

main().catch((e) => {
  console.error('FAILED: ' + e.message);
  process.exitCode = 1;
});
