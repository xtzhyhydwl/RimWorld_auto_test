#!/usr/bin/env node
/** 排查：刚开殖民地后 step_game_ticks 为什么失败。 */
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor } from '../src/launcher.js';

const info = readBridgeInfo(logPathFor('vanilla'));
const { client } = await connectGabp(info, { requestTimeoutMs: 30_000 });
const call = (n, a, o) => client.callTool(n, a, o);
const log = (...a) => console.log(...a);

const st = (await call('rimbridge/get_bridge_status')).state;
log('当前状态:');
for (const k of ['programState', 'hasCurrentGame', 'mapCount', 'longEventPending', 'paused', 'gameDataReady', 'mapDataReady', 'currentMapReady', 'playable', 'visualReady', 'automationReady']) {
  log(`  ${k} = ${st[k]}`);
}

log('\n=== 直接试 step_game_ticks(10) ===');
try {
  const r = await call('rimworld/step_game_ticks', { ticks: 10, pauseFirst: true, timeoutMs: 30_000 }, { timeoutMs: 45_000 });
  log(JSON.stringify(r, null, 2).slice(0, 1200));
} catch (e) {
  log(`FAIL ${e.message}`);
}

log('\n=== 等 currentMap 就绪后再试 ===');
try {
  const w = await call('rimbridge/wait_for_game_loaded', { readiness: 'currentMap', pauseIfNeeded: true, timeoutMs: 60_000 }, { timeoutMs: 90_000 });
  log(`  wait: satisfied=${w.satisfied} ${w.message ?? ''} state.programState=${w.state?.programState} currentMapReady=${w.state?.currentMapReady}`);
} catch (e) {
  log(`  wait FAIL ${e.message}`);
}
try {
  const r2 = await call('rimworld/step_game_ticks', { ticks: 10, pauseFirst: true, timeoutMs: 30_000 }, { timeoutMs: 45_000 });
  log(`  step: status=${r2.status} advanced=${r2.advancedTicks} start=${r2.startTicksGame} end=${r2.endTicksGame} msg=${r2.message ?? ''}`);
} catch (e) {
  log(`  step FAIL ${e.message}`);
}

log('\n=== 等 playable 再试 ===');
try {
  const w = await call('rimbridge/wait_for_game_loaded', { readiness: 'playable', pauseIfNeeded: true, timeoutMs: 60_000 }, { timeoutMs: 90_000 });
  log(`  wait: satisfied=${w.satisfied} programState=${w.state?.programState} playable=${w.state?.playable}`);
} catch (e) {
  log(`  wait FAIL ${e.message}`);
}
try {
  const r3 = await call('rimworld/step_game_ticks', { ticks: 10, pauseFirst: true, timeoutMs: 30_000 }, { timeoutMs: 45_000 });
  log(`  step: status=${r3.status} advanced=${r3.advancedTicks} start=${r3.startTicksGame} end=${r3.endTicksGame} msg=${r3.message ?? ''}`);
} catch (e) {
  log(`  step FAIL ${e.message}`);
}

client.close();
