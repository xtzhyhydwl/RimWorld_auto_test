/**
 * recon-food-A4 —— A 块最后几问：
 *  1. 选中食物后，inspect 面板的 UI 文本里有没有 nutrition（get_ui_layout）
 *  2. rimbridge/run_lua 能不能做「聚合统计」（读快照字段 + 循环）
 *  3. list_architect_categories 全量（找 Zone 分类）
 */
import fs from 'node:fs';
import path from 'node:path';
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor, WS } from '../src/launcher.js';

const OUT = path.join(WS, 'runs', 'food-recon');
const info = readBridgeInfo(logPathFor('vanilla'));
const { client } = await connectGabp(info, { requestTimeoutMs: 60_000 });
const call = (n, a, o) => client.callTool(n, a, o);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const head = (o, n = 1800) => { const s = typeof o === 'string' ? o : JSON.stringify(o); return s.length > n ? s.slice(0, n) + ` …(+${s.length - n})` : s; };
const save = (n, o) => fs.writeFileSync(path.join(OUT, 'dumps', `${n}.json`), JSON.stringify(o, null, 2), 'utf8');
const R = { at: new Date().toISOString(), evidence: {} };

// ── 1. 选中食物看 UI 文本 ─────────────────────────────────
console.log('▶ 1. 选中 MealSimple，看 inspect 面板 UI 文本');
await call('rimworld/click_cell', { x: 146, z: 135, button: 'left' });
await sleep(400);
const lay = await call('rimworld/get_ui_layout');
save('ui_layout_food', lay);
for (const s of lay.surfaces ?? []) {
  console.log(`  surface ${s.kind} ${s.type} elements=${(s.elements ?? []).length}`);
  for (const e of s.elements ?? []) {
    const txt = [e.label, e.valueText].filter(Boolean).join(' | ');
    if (txt && /nutri|meal|pemmican|x7|rice|potato|hunger|food/i.test(txt)) console.log(`     [${e.kind}] ${txt}`);
  }
}
R.evidence.inspectPaneTexts = (lay.surfaces ?? []).flatMap((s) => (s.elements ?? []).map((e) => ({ kind: e.kind, label: e.label, valueText: e.valueText }))).filter((e) => e.label || e.valueText);

// ── 2. run_lua 聚合能力 ──────────────────────────────────
console.log('\n▶ 2. rimbridge/run_lua 聚合尝试');
const lua1 = `local snap = rb.call("rimworld/get_cells_info", { x = 144, z = 130, width = 24, height = 14 })
local cells = snap.result.cells
rb.assert(cells ~= nil, "no cells")
local n = 0
for i = 1, 10 do
  local c = cells[i]
  if c.thingCount > 0 then
    n = n + 1
  end
end
rb.print("cells with things in first 10: " .. n)
return n`;
const c1 = await call('rimbridge/compile_lua', { luaSource: lua1 }).catch((e) => ({ error: e.message }));
console.log('  compile: ' + head(c1, 1200));
const r1 = await call('rimbridge/run_lua', { luaSource: lua1, includeStepResults: false }).catch((e) => ({ error: e.message }));
console.log('  run: ' + head(r1, 1500));
R.evidence.luaAggregate = { compile: c1, run: r1 };

// 试 rb.call 多次 + 累加（跨调用状态）
const lua2 = `local total = 0
local s1 = rb.call("rimworld/get_cell_info", { x = 146, z = 135 })
local s2 = rb.call("rimworld/get_cell_info", { x = 147, z = 135 })
local t1 = s1.result.cell.things[1]
local t2 = s2.result.cell.things[1]
rb.print("t1=" .. t1.defName .. " t2=" .. t2.defName)
return t1.defName`;
const r2 = await call('rimbridge/run_lua', { luaSource: lua2, includeStepResults: false }).catch((e) => ({ error: e.message }));
console.log('  run2: ' + head(r2, 1500));
R.evidence.luaTwoCalls = r2;

// ── 3. architect categories ──────────────────────────────
console.log('\n▶ 3. list_architect_categories');
const cats = await call('rimworld/list_architect_categories', { includeEmpty: true, includeHidden: true });
save('architect_categories', cats);
console.log(`  ${cats.categories?.length ?? 0} 个分类`);
for (const c of cats.categories ?? []) console.log(`  ${c.categoryDefName} | ${c.label} | id=${c.id} | designators=${c.designatorCount ?? c.count ?? '?'}`);

fs.writeFileSync(path.join(OUT, 'A4.json'), JSON.stringify(R, null, 2), 'utf8');
client.close();
console.log('\n报告：' + path.join(OUT, 'A4.json'));
