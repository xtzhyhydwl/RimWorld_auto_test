/**
 * recon-food-final —— 收尾：
 *  1. list_capabilities 全量，按关键词筛「食物/营养/物品/工作」相关能力
 *  2. lua reference 的 runtimeModel / accessRules（确认没有旁路）
 *  3. get_selection_semantics 对非 pawn 是否会崩（已知 bug 复核）
 *  4. 确认 Zone_Growing 默认作物在 XML 里找不到 → 只有 UI 一条路
 */
import fs from 'node:fs';
import path from 'node:path';
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor, WS } from '../src/launcher.js';

const OUT = path.join(WS, 'runs', 'food-recon');
const info = readBridgeInfo(logPathFor('vanilla'));
const { client } = await connectGabp(info, { requestTimeoutMs: 300_000 });
const call = (n, a, o) => client.callTool(n, a, o);
const save = (n, o) => fs.writeFileSync(path.join(OUT, 'dumps', `${n}.json`), JSON.stringify(o, null, 2), 'utf8');
const R = { at: new Date().toISOString(), evidence: {} };
const head = (o, n = 1500) => { const s = o == null ? String(o) : (typeof o === 'string' ? o : JSON.stringify(o)); return s.length > n ? s.slice(0, n) + ` …(${s.length - n} more)` : s; };

const caps = await call('rimbridge/list_capabilities', { limit: 400 });
save('capabilities_full', caps);
const list = caps.capabilities ?? caps.items ?? [];
console.log(`能力总数 ${list.length}`);
const kw = /nutrition|food|meal|hunger|stock|inventory|item|thing|zone|work|prior|skill|hunt|plant|grow|def|category|resource/i;
const relevant = list.filter((c) => kw.test(JSON.stringify({ id: c.id, summary: c.summary, title: c.title, aliases: c.aliases })));
console.log(`关键词相关信息：${relevant.length}`);
for (const c of relevant) console.log(`  ${c.id}  ::  ${String(c.summary ?? c.title ?? '').slice(0, 120)}`);
R.evidence.relevantCapabilities = relevant.map((c) => ({ id: c.id, summary: c.summary ?? c.title ?? null }));
R.evidence.capabilityCount = list.length;

// 2. lua reference
const ref = await call('rimbridge/get_lua_reference');
save('lua_reference', ref);
console.log('\nlua runtimeModel: ' + head(ref.runtimeModel, 900));
console.log('lua accessRules: ' + head(ref.accessRules, 900));
console.log('lua unsupported: ' + head(ref.unsupported, 900));
console.log('lua hostApi: ' + head(ref.hostApi, 700));
R.evidence.luaReference = { runtimeModel: ref.runtimeModel, accessRules: ref.accessRules, unsupported: ref.unsupported, hostApi: ref.hostApi };

// 3. 非 pawn 的 selection semantics
console.log('\n▶ get_selection_semantics 对 thing/zone 的行为');
await call('rimworld/press_cancel').catch(() => {});
await new Promise((r) => setTimeout(r, 300));
for (const [name, args] of [['食物', { x: 146, z: 135 }], ['种植区', { x: 20, z: 11 }]]) {
  await call('rimworld/click_cell', { ...args, button: 'left' });
  await new Promise((r) => setTimeout(r, 400));
  const sem = await call('rimworld/get_selection_semantics').catch((e) => ({ error: e.message }));
  const rec = { target: name, success: sem.success, hasSelection: sem.hasSelection, error: sem.error, message: sem.message, kinds: sem.selectedKinds };
  console.log(`  ${name}: success=${sem.success} hasSelection=${sem.hasSelection} kinds=${JSON.stringify(sem.selectedKinds)} ${sem.message ? 'message=' + String(sem.message).slice(0, 80) : ''}`);
  R.evidence['selection_' + name] = rec;
}

// 4. 最终确认：食物格子里能否读到 nutrition / stackCount
const cell = await call('rimworld/get_cell_info', { x: 146, z: 135 });
console.log('\n食物格 thing 全字段: ' + JSON.stringify(cell.cell.things));
R.evidence.foodThingFull = cell.cell.things;
console.log('hasNutritionField: ' + JSON.stringify(cell.cell.things).includes('nutrition'));
console.log('hasStackCountField: ' + JSON.stringify(cell.cell.things).includes('stackCount'));

// 5. 全图扫描复核（最终数字）
const t0 = Date.now();
let calls = 0, bytes = 0, foods = 0;
const byDef = {};
for (let z = 0; z < 250; z += 32) for (let x = 0; x < 250; x += 32) {
  const r = await call('rimworld/get_cells_info', { x, z, width: 32, height: 32 });
  calls++; bytes += JSON.stringify(r).length;
  for (const c of r.cells ?? []) for (const t of c.things ?? []) {
    if (/^(Meal|Pemmican|Raw|Kibble|Hay|Silage|Milk|Egg|InsectJelly|BabyFood|Chocolate|Ambrosia|Meat_)/.test(t.defName ?? '')) { foods++; byDef[t.defName] = (byDef[t.defName] ?? 0) + 1; }
  }
}
const ms = Date.now() - t0;
console.log(`\n全图扫描复核：${calls} 次调用 / ${ms}ms / ${(bytes / 1024 / 1024).toFixed(1)}MB / ${foods} 堆食物 ${JSON.stringify(byDef)}`);
R.evidence.fullScanFinal = { calls, ms, mb: +(bytes / 1024 / 1024).toFixed(1), foodStacks: foods, byDef };

// 6. 游戏内状态快照
const bs = await call('rimbridge/get_bridge_status');
R.evidence.bridgeStatus = bs.state;
console.log('\n桥接状态: ' + head(bs.state, 400));

fs.writeFileSync(path.join(OUT, 'final.json'), JSON.stringify(R, null, 2), 'utf8');
client.close();
console.log('\n报告：' + path.join(OUT, 'final.json'));
