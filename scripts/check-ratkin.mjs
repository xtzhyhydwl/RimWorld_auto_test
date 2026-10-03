#!/usr/bin/env node
/** 检查鼠族相关 mod 的加载状态与报错。 */
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor } from '../src/launcher.js';

const info = readBridgeInfo(logPathFor('vanilla'));
const { client } = await connectGabp(info, { requestTimeoutMs: 30_000 });
const call = (n, a, o) => client.callTool(n, a, o);
const log = (...a) => console.log(...a);

const st = await call('rimworld/get_mod_configuration_status').catch(() => null);
if (st) {
  log('=== 配置状态 ===');
  log(JSON.stringify(st, null, 2).slice(0, 1500));
}

const mods = await call('rimworld/list_mods', { compatibleOnly: false });
log(`\n=== mod 加载 ===`);
log(`  modCount=${mods.modCount} activeCount=${mods.activeCount} loadedSession=${mods.loadedSessionModCount}`);
log(`  重启原因数=${mods.restartReasonCount} 会话不一致=${mods.sessionMismatchCount}`);

const interesting = /ratkin|humanoidalien|xmlextensions|harmony|charactereditor|rimbridge|autotest/i;
log(`\n=== 关键 mod 明细 ===`);
for (const m of mods.mods ?? []) {
  if (!interesting.test(`${m.packageId} ${m.name}`)) continue;
  const flags = [];
  if (m.enabled) flags.push('enabled');
  if (m.loadedInSession) flags.push('loaded');
  if (m.versionCompatible === false) flags.push('!!版本不兼容');
  if (m.hasVersionWarning) flags.push('!!版本警告');
  if (m.hasOrderingIssues) flags.push('!!排序问题');
  if (m.hasConfigurationWarning) flags.push(`!!配置警告:${m.configurationWarning}`);
  if ((m.unsatisfiedDependencies ?? []).length) flags.push(`!!缺依赖:${m.unsatisfiedDependencies.join('+')}`);
  log(`  [${String(m.activeLoadOrder).padStart(2)}] ${m.name.padEnd(30)} ${flags.join(' ')}`);
}

log(`\n=== 日志里的错误/警告（Ratkin/HAR 相关） ===`);
const logs = await call('rimbridge/list_logs', { minimumLevel: 'warning', limit: 300 });
const entries = logs.logs ?? [];
log(`  共 ${entries.length} 条警告及以上`);
for (const e of entries.slice(0, 25)) {
  log(`  [${e.level}] ${String(e.message).replace(/\s+/g, ' ').slice(0, 160)}`);
}

client.close();
