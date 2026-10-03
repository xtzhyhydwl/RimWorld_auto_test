#!/usr/bin/env node
/** 排查：测试 mod 的 Def 到底有没有加载进游戏。 */
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor } from '../src/launcher.js';

const info = readBridgeInfo(logPathFor('vanilla'));
const { client } = await connectGabp(info, { requestTimeoutMs: 25_000 });
const call = (n, a, o) => client.callTool(n, a, o);
const log = (...a) => console.log(...a);

const mods = await call('rimworld/list_mods', { compatibleOnly: false });
log(`modCount=${mods.modCount} activeCount=${mods.activeCount} loadedSession=${mods.loadedSessionModCount}`);
const mine = (mods.mods ?? []).find((m) => /dsh\.autotest/i.test(m.packageId ?? ''));
log('\nmod 记录全字段:');
log(JSON.stringify(mine, null, 2));

log('\n=== Actions\\Spawn thing... 里的 DSH 项 ===');
const r = await call('rimworld/get_debug_action', {
  path: 'Actions\\Spawn thing...',
  includeChildren: true,
  includeHiddenChildren: true,
});
const kids = r.children ?? r.action?.children ?? [];
log(`  共 ${kids.length} 个子项`);
const dsh = kids.filter((k) => /DSH|AutoTest/i.test(k.label ?? '') || /DSH|AutoTest/i.test(k.path ?? ''));
log(`  含 DSH/AutoTest 的: ${dsh.length} 个`);
for (const d of dsh.slice(0, 5)) {
  log(`    ${d.label}  ${d.path}  [${d.execution?.kind}/${d.execution?.requiredTargetKind ?? '-'}]`);
}

log('\n=== 直接执行 Spawn thing...DSH_AutoTestThing ===');
try {
  const e = await call(
    'rimworld/execute_debug_action',
    { path: 'Actions\\Spawn thing...\\DSH_AutoTestThing', x: 140, z: 140 },
    { timeoutMs: 20_000 },
  );
  log(`  success=${e.success} message=${e.message ?? ''}`);
} catch (err) {
  log(`  FAIL ${err.message}`);
}

await call('rimbridge/wait_for_long_event_idle', { timeoutMs: 15_000 }).catch(() => {});
const cell = await call('rimworld/get_cell_info', { x: 140, z: 140 });
log(`  (140,140) 上的东西: ${JSON.stringify((cell.cell?.things ?? []).map((t) => `${t.defName}=${t.label}`))}`);

log('\n=== 换个方式：找 Spawn full thing stack / Spawn unminified thing ===');
for (const parent of ['Actions\\Spawn full thing stack...', 'Actions\\Spawn unminified thing...']) {
  const rr = await call('rimworld/get_debug_action', { path: parent, includeChildren: true, includeHiddenChildren: true });
  const kk = rr.children ?? rr.action?.children ?? [];
  const hit = kk.find((k) => /DSH_AutoTest/i.test(k.label ?? ''));
  log(`  ${parent}: ${kk.length} 项，DSH 命中=${hit ? hit.path : '无'}`);
}

client.close();
