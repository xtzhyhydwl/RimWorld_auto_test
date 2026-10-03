#!/usr/bin/env node
/** 排查：HAR 明明启用了，为什么 NewRatkinPlus 说前置未满足。 */
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor } from '../src/launcher.js';

const info = readBridgeInfo(logPathFor('vanilla'));
const { client } = await connectGabp(info, { requestTimeoutMs: 30_000 });
const call = (n, a, o) => client.callTool(n, a, o);
const log = (...a) => console.log(...a);

const mods = await call('rimworld/list_mods', { compatibleOnly: false });

function show(m) {
  log(`\n--- ${m.name} ---`);
  log(`  packageId            = ${JSON.stringify(m.packageId)}`);
  log(`  packageIdPlayerFacing= ${JSON.stringify(m.packageIdPlayerFacing)}`);
  log(`  packageIdNonUnique   = ${JSON.stringify(m.packageIdNonUnique)}`);
  log(`  folderName           = ${m.folderName}`);
  log(`  rootDir              = ${m.rootDir}`);
  log(`  source               = ${m.source}`);
  log(`  activeLoadOrder      = ${m.activeLoadOrder}`);
  log(`  enabled=${m.enabled} loadedInSession=${m.loadedInSession}`);
  log(`  unsatisfiedDependencies = ${JSON.stringify(m.unsatisfiedDependencies)}`);
  // 其余所有可能和依赖有关的字段
  for (const k of Object.keys(m)) {
    if (/depend|require|missing|load|order|incompat/i.test(k) && !['unsatisfiedDependencies','activeLoadOrder','loadedSessionOrder'].includes(k)) {
      log(`  ${k} = ${JSON.stringify(m[k])}`);
    }
  }
}

const har = (mods.mods ?? []).find((m) => /humanoidalienraces/i.test(m.packageId ?? ''));
const rat = (mods.mods ?? []).find((m) => /ratkinracemod/i.test(m.packageId ?? ''));
if (har) show(har); else log('找不到 HAR');
if (rat) show(rat); else log('找不到 Ratkin');

log('\n=== 所有 packageId 里含 humanoid 的 mod ===');
for (const m of mods.mods ?? []) {
  if (/humanoid/i.test(`${m.packageId} ${m.packageIdNonUnique} ${m.name}`)) {
    log(`  ${m.packageId}  name=${m.name}  enabled=${m.enabled}  activeLoadOrder=${m.activeLoadOrder}  rootDir=${m.rootDir}`);
  }
}

log('\n=== 所有 packageId 里含 ratkin 且已启用的 ===');
for (const m of mods.mods ?? []) {
  if (/ratkin/i.test(`${m.packageId}`) && m.enabled) {
    log(`  ${m.packageId}  name=${m.name}  loadOrder=${m.activeLoadOrder}`);
  }
}

client.close();
