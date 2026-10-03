#!/usr/bin/env node
/**
 * 阶段 1 验证：Settings 的确定性与加速开关。
 *
 * 验证点：
 *   - set_debug_setting 写进去之后，get_debug_action 回读是否一致
 *   - 默认值到底是什么（哪些随机源默认开着）
 *   - 配置档能否整批应用并逐项回读通过
 *   - 能否完整恢复原状
 */
import fs from 'node:fs';
import path from 'node:path';
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor, WS } from '../src/launcher.js';
import { PROFILES, applyTestProfile, readSetting, readSettings, restoreSettings } from '../src/testprofile.js';

const stamp = new Date().toISOString().replace(/[:.]/g, '-');
const outDir = path.join(WS, 'runs', `verify-settings-${stamp}`);
fs.mkdirSync(outDir, { recursive: true });

const info = readBridgeInfo(logPathFor('vanilla'));
const { client } = await connectGabp(info, { requestTimeoutMs: 20_000 });
const call = (n, a, o) => client.callTool(n, a, o);
const log = (...a) => console.log(...a);
const results = [];
const record = (name, ok, detail) => {
  results.push({ name, ok, detail: detail ?? null });
  log(`  ${ok ? '✓' : '✗'} ${name}${detail && !ok ? ` — ${detail}` : ''}`);
};

const st = (await call('rimbridge/get_bridge_status')).state;
log(`游戏状态: ${st.programState} hasCurrentGame=${st.hasCurrentGame}\n`);
if (!st.hasCurrentGame) {
  log('没有加载存档，先开调试殖民地…');
  await call('rimworld/start_debug_game_ready', { readiness: 'mapData', pauseIfNeeded: true, timeoutMs: 300_000 }, { timeoutMs: 330_000 });
  await call('rimbridge/wait_for_long_event_idle', { timeoutMs: 120_000 });
}

// ── 1. 单个开关的写入/回读闭环 ────────────────────────────────
log('═══ 1. 写入 → 回读 闭环 ═══');
const probe = 'Settings\\God Mode';
const beforeVal = await readSetting(client, probe);
log(`  ${probe} 当前 = ${beforeVal.on}`);
await call('rimworld/set_debug_setting', { path: probe, enabled: true });
const afterOn = await readSetting(client, probe);
record('set_debug_setting(true) 回读为 true', afterOn.on === true, `实际 ${afterOn.on}`);
await call('rimworld/set_debug_setting', { path: probe, enabled: false });
const afterOff = await readSetting(client, probe);
record('set_debug_setting(false) 回读为 false', afterOff.on === false, `实际 ${afterOff.on}`);
await call('rimworld/set_debug_setting', { path: probe, enabled: beforeVal.on });
log(`  已恢复到 ${beforeVal.on}`);

// ── 2. 默认值调查：哪些随机源默认开着 ────────────────────────
log('\n═══ 2. 关键随机源的默认值 ═══');
const randomSources = [
  'Settings\\Enable Storyteller',
  'Settings\\Enable Random Mental States',
  'Settings\\Enable Random Diseases',
  'Settings\\Enable Damage',
  'Settings\\Enable Player Damage',
  'Settings\\Pause On Error',
];
const defaults = await readSettings(client, randomSources);
for (const [p, v] of Object.entries(defaults)) {
  log(`  ${v.on ? '【开】' : '【关】'} ${p}`);
}
record(
  '默认状态已记录（用于判断是否需要关随机源）',
  true,
  JSON.stringify(Object.fromEntries(Object.entries(defaults).map(([k, v]) => [k, v.on]))),
);

// ── 3. 整批应用配置档 ─────────────────────────────────────────
log('\n═══ 3. 应用配置档 deterministic + fast ═══');
const t0 = Date.now();
const applied = await applyTestProfile(client, ['deterministic', 'fast'], { log });
log(`  耗时 ${((Date.now() - t0) / 1000).toFixed(1)}s`);
record(
  `deterministic + fast 全部生效（${applied.applied.length} 项）`,
  applied.failed.length === 0,
  applied.failed.map((f) => `${f.path}: ${f.reason}`).join('; '),
);

// ── 4. 确认随机源真的关了 ────────────────────────────────────
log('\n═══ 4. 复查随机源 ═══');
const afterApply = await readSettings(client, randomSources);
for (const [p, v] of Object.entries(afterApply)) {
  log(`  ${v.on ? '【开】' : '【关】'} ${p}`);
}
record(
  'Enable Storyteller 已关',
  afterApply['Settings\\Enable Storyteller'].on === false,
);
record(
  'Enable Random Mental States 已关',
  afterApply['Settings\\Enable Random Mental States'].on === false,
);
record(
  'Enable Random Diseases 已关',
  afterApply['Settings\\Enable Random Diseases'].on === false,
);

// ── 5. 确定性是否真的生效：跑一段固定 tick，看事件有没有刷 ────
log('\n═══ 5. 确定性实测：极速跑 15 秒，看有没有随机事件 ═══');
const cursor = (await call('rimbridge/get_bridge_status')).latestLogSequence ?? 0;
const lettersBefore = ((await call('rimworld/list_letters', { limit: 50 })).letters ?? []).length;
await call('rimworld/set_time_speed', { speed: 'Ultrafast', ultraSpeedBoost: true });
await call('rimworld/play_for', { durationMs: 15_000, speed: 'Ultrafast' }, { timeoutMs: 300_000 });
const lettersAfter = (await call('rimworld/list_letters', { limit: 50 })).letters ?? [];
const newLetters = lettersAfter.slice(0, Math.max(0, lettersAfter.length - lettersBefore));
const newLogs = (await call('rimbridge/list_logs', { minimumLevel: 'warning', limit: 100, afterSequence: cursor })).logs ?? [];
log(`  新增信件 ${newLetters.length} 封: ${newLetters.map((l) => l.label).join(' | ') || '（无）'}`);
log(`  新增警告/错误 ${newLogs.length} 条`);
record(
  '关掉随机源后没有刷出随机事件信件',
  newLetters.length === 0,
  newLetters.map((l) => l.label).join(', '),
);

// ── 6. 恢复原状 ──────────────────────────────────────────────
log('\n═══ 6. 恢复设置 ═══');
const restored = await restoreSettings(client, applied.before);
log(`  恢复 ${restored.length} 项`);
const recheck = await readSettings(client, randomSources);
let allRestored = true;
for (const [p, v] of Object.entries(recheck)) {
  const want = defaults[p]?.on;
  const ok = v.on === want;
  if (!ok) allRestored = false;
  log(`  ${ok ? '✓' : '✗'} ${p}: ${v.on}（原为 ${want}）`);
}
record('设置已完整恢复', allRestored);

// ── 7. 可用的配置档清单 ──────────────────────────────────────
log('\n═══ 7. 可用配置档 ═══');
for (const [name, cfg] of Object.entries(PROFILES)) {
  log(`  ${name}: ${Object.keys(cfg).length} 项`);
}

client.close();

const passed = results.filter((r) => r.ok).length;
log(`\n${'═'.repeat(62)}`);
log(`阶段 1：${passed}/${results.length} 通过`);
for (const r of results.filter((x) => !x.ok)) log(`  ✗ ${r.name} — ${r.detail}`);
log('═'.repeat(62));

fs.writeFileSync(
  path.join(outDir, 'result.json'),
  JSON.stringify({ stamp, stage: 'settings', results, defaults, applied: applied.applied, failed: applied.failed }, null, 2),
  'utf8',
);
log(`报告: ${path.join(outDir, 'result.json')}`);
process.exitCode = passed === results.length ? 0 : 1;
