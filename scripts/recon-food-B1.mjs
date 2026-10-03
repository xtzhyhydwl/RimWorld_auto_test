/**
 * recon-food-B1 —— architect designators: Zone 与 Orders 分类全量；lua 子集边界测试。
 */
import fs from 'node:fs';
import path from 'node:path';
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor, WS } from '../src/launcher.js';

const OUT = path.join(WS, 'runs', 'food-recon');
const info = readBridgeInfo(logPathFor('vanilla'));
const { client } = await connectGabp(info, { requestTimeoutMs: 60_000 });
const call = (n, a, o) => client.callTool(n, a, o);
const head = (o, n = 2000) => { const s = typeof o === 'string' ? o : JSON.stringify(o); return s.length > n ? s.slice(0, n) + ` …(+${s.length - n})` : s; };
const save = (n, o) => fs.writeFileSync(path.join(OUT, 'dumps', `${n}.json`), JSON.stringify(o, null, 2), 'utf8');
const R = { at: new Date().toISOString(), evidence: {} };

for (const cat of ['Zone', 'Orders']) {
  console.log(`\n▶ list_architect_designators (${cat})`);
  const d = await call('rimworld/list_architect_designators', { categoryId: `architect-category:${cat.toLowerCase()}`, includeHidden: true });
  save(`designators_${cat}`, d);
  console.log(`  ${d.designators?.length ?? 0} 个`);
  for (const x of d.designators ?? []) {
    console.log(`  [${x.kind}] ${x.label}  id=${x.id}  stuff=${x.stuffDefName} childCount=${x.childCount} actionable=${x.actionable} supportsCellApplication=${x.supportsCellApplication} zoneType=${x.zoneType ?? x.zoneDefName ?? '-'}`);
  }
  R.evidence[`designators_${cat}`] = (d.designators ?? []).map((x) => ({ label: x.label, id: x.id, kind: x.kind, stuffDefName: x.stuffDefName, childCount: x.childCount, actionable: x.actionable, supportsCellApplication: x.supportsCellApplication }));
}

// Order 分类也可能叫别的 id，从容错角度再按 DefName 直连试一次
console.log('\n▶ 按 raw defName 拉 Zone/Orders');
for (const raw of ['Zone', 'Orders', 'Order']) {
  const d = await call('rimworld/list_architect_designators', { categoryId: raw }).catch((e) => ({ error: e.message }));
  console.log(`  ${raw}: ${d.error ?? (d.designators?.length ?? 0) + ' 个'}${d.error ? '' : '  ' + (d.designators ?? []).slice(0, 6).map((x) => x.label).join(' / ')}`);
}

// ── lua 子集边界 ────────────────────────────────────────────
console.log('\n▶ lua 子集边界测试');
const cases = {
  forLoop: { src: `local s = rb.call("rimworld/get_cell_info", { x = 146, z = 135 })\nlocal n = 0\nfor i = 1, 3 do\n  n = n + i\nend\nreturn n` },
  staticField: { src: `local s = rb.call("rimworld/get_cell_info", { x = 146, z = 135 })\nreturn s.result.cell.thingCount` },
  staticIndex: { src: `local s = rb.call("rimworld/get_cell_info", { x = 146, z = 135 })\nreturn s.result.cell.things[1].defName` },
  dynIndex: { src: `local s = rb.call("rimworld/get_cell_info", { x = 146, z = 135 })\nlocal i = 1\nreturn s.result.cell.things[i].defName` },
  ifStmt: { src: `local s = rb.call("rimworld/get_cell_info", { x = 146, z = 135 })\nif s.result.cell.thingCount > 0 then\n  return "has"\nend\nreturn "none"` },
  localVarArith: { src: `local a = 1\nlocal b = a + 2\nreturn b` },
  paramsUse: { src: `return params.n` },
};
for (const [name, c] of Object.entries(cases)) {
  const r = await call('rimbridge/run_lua', { luaSource: c.src, parameters: { n: 42 }, includeStepResults: false }).catch((e) => ({ error: e.message }));
  const ok = r?.success !== false;
  console.log(`  ${ok ? '✓' : '✗'} ${name}: ${head(r?.error?.message ?? r?.message ?? r, 200)}`);
  R.evidence[`lua_${name}`] = { ok, raw: r?.error?.message ?? r?.message ?? null };
}

fs.writeFileSync(path.join(OUT, 'B1.json'), JSON.stringify(R, null, 2), 'utf8');
client.close();
console.log('\n报告：' + path.join(OUT, 'B1.json'));
