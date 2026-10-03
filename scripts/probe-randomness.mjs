#!/usr/bin/env node
/** 调查：关掉 Storyteller 之后，还有什么在产生随机事件？ */
import fs from 'node:fs';
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor } from '../src/launcher.js';

const info = readBridgeInfo(logPathFor('vanilla'));
const { client } = await connectGabp(info, { requestTimeoutMs: 20_000 });
const call = (n, a, o) => client.callTool(n, a, o);
const log = (...a) => console.log(...a);

const letters = (await call('rimworld/list_letters', { limit: 30 })).letters ?? [];
log(`当前信件栈 ${letters.length} 封：`);
for (const l of letters) {
  log(`  《${l.label}》 defName=${l.defName ?? '?'} letterDef=${l.letterDef ?? '?'}`);
  log(`     ${String(l.text ?? '').replace(/<[^>]+>/g, '').replace(/\s+/g, ' ').slice(0, 160)}`);
  log(`     键: ${Object.keys(l).join(', ')}`);
}

// 搜 debug action 里跟它相关的
log('\n搜 debug action：vision / monolith / anomaly');
for (const q of ['vision', 'monolith', 'anomaly']) {
  try {
    const r = await call('rimworld/search_debug_actions', { query: q, limit: 6, supportedOnly: true });
    log(`  「${q}」命中 ${r.totalMatchCount}：`);
    for (const m of (r.matches ?? []).slice(0, 5)) log(`    [${m.node.execution?.kind}] ${m.path}`);
  } catch (e) {
    log(`  「${q}」✗ ${e.message}`);
  }
}

// Settings 里有没有 anomaly 相关的开关
log('\nSettings 里含 anomaly/monolith/event/incident 的开关：');
const roots = await call('rimworld/get_debug_action', { path: 'Settings', includeChildren: true, includeHiddenChildren: true });
const settings = roots.children ?? roots.action?.children ?? [];
for (const s of settings) {
  if (/anomal|monolith|incident|event|random|story|psychic|vision/i.test(s.label ?? '')) {
    log(`  ${s.on ? '【开】' : '【关】'} ${s.label}   ${s.path}`);
  }
}
log(`（Settings 共 ${settings.length} 项）`);

// 看看 storyteller 的实际状态
log('\n游戏内的 storyteller / incident 相关输出：');
for (const p of ['Outputs\\Current Threat Points', 'Outputs\\Future Incidents', 'Outputs\\Future Incidents Current Map', 'Outputs\\Incident Chances']) {
  try {
    const r = await call('rimworld/execute_debug_action', { path: p }, { timeoutMs: 15_000 });
    const txt = typeof r === 'string' ? r : JSON.stringify(r);
    log(`  ${p}: ${txt.slice(0, 300)}`);
  } catch (e) {
    log(`  ${p}: ✗ ${e.message}`);
  }
}

client.close();
