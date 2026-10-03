/**
 * recon-food-C1 —— C 块：工作优先级 / 技能 / 工作类型 的可读性。
 */
import fs from 'node:fs';
import path from 'node:path';
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor, WS } from '../src/launcher.js';

const OUT = path.join(WS, 'runs', 'food-recon');
const info = readBridgeInfo(logPathFor('vanilla'));
const { client } = await connectGabp(info, { requestTimeoutMs: 300_000 });
const call = (n, a, o) => client.callTool(n, a, o);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const head = (o, n = 2500) => { const s = typeof o === 'string' ? o : JSON.stringify(o); return s.length > n ? s.slice(0, n) + ` …(+${s.length - n})` : s; };
const save = (n, o) => fs.writeFileSync(path.join(OUT, 'dumps', `${n}.json`), JSON.stringify(o, null, 2), 'utf8');
const R = { at: new Date().toISOString(), evidence: {} };
async function step(name, fn) {
  console.log(`\n▶ ${name}`);
  try { const v = await fn(); R.evidence[name] = v; return v; } catch (e) { console.log('  ✗ ' + e.message); R.evidence[name] = { error: e.message }; return null; }
}

const cols = (await call('rimworld/list_colonists', { currentMapOnly: true })).colonists ?? [];
const c0 = cols[0];
R.evidence.colonistKeys = Object.keys(c0 ?? {});

// 1. list_colonists 里有没有工作优先级/技能
await step('C10 list_colonists 字段全集', async () => {
  console.log('  keys: ' + Object.keys(c0).join(', '));
  const hasWork = /work|priorit|skill/i.test(JSON.stringify(c0));
  console.log('  含 work/priorit/skill 字样: ' + hasWork);
  return { keys: Object.keys(c0), hasWorkRelated: hasWork };
});

// 2. ce/* 路径
await step('C11a ce/probe + ce/colonists + ce/get_skills', async () => {
  const p = await call('ce/probe').catch((e) => ({ error: e.message }));
  console.log('  ce/probe: ' + head(p, 500));
  const cs = await call('ce/colonists').catch((e) => ({ error: e.message }));
  console.log('  ce/colonists: ' + head(cs, 900));
  const sk = await call('ce/get_skills', { pawnId: c0.pawnId }).catch((e) => ({ error: e.message }));
  console.log('  ce/get_skills: ' + head(sk, 1200));
  return { probe: p, colonists: cs, skills: sk };
});

// 3. 原生路径：inspect tab 里的技能
await step('C11b 原生 inspect（Bio tab）+ selection_semantics', async () => {
  await call('rimworld/press_cancel'); await sleep(200);
  await call('rimworld/select_pawn', { pawnId: c0.pawnId }); await sleep(400);
  const tabs = await call('rimworld/list_inspect_tabs');
  console.log('  tabs: ' + JSON.stringify((tabs.tabs ?? []).map((t) => t.label)));
  const open = await call('rimworld/open_inspect_tab', { inspectTabId: 'Bio' }).catch((e) => ({ error: e.message }));
  console.log('  open Bio: ' + head(open, 400));
  await sleep(600);
  const lay = await call('rimworld/get_ui_layout');
  const texts = (lay.surfaces ?? []).flatMap((s) => (s.elements ?? []).map((e) => e.label ?? e.valueText).filter(Boolean));
  console.log('  Bio 文本: ' + JSON.stringify(texts.slice(0, 50)));
  save('ui_layout_bio', lay);
  return { tabs: (tabs.tabs ?? []).map((t) => t.label), texts };
});

// 4. Work 主标签
await step('C10b Work 主标签 UI', async () => {
  const mt = await call('rimworld/list_main_tabs');
  console.log('  main tabs: ' + JSON.stringify((mt.mainTabs ?? mt.tabs ?? []).map((t) => ({ id: t.id ?? t.targetId, label: t.label }))));
  await call('rimworld/close_main_tab', {}).catch(() => {});
  const o = await call('rimworld/open_main_tab', { targetId: 'Work' }).catch((e) => ({ error: e.message }));
  console.log('  open Work: ' + head({ success: o?.success, message: o?.message, error: o?.error }, 400));
  await sleep(900);
  const lay = await call('rimworld/get_ui_layout');
  save('ui_layout_work', lay);
  for (const s of lay.surfaces ?? []) {
    console.log(`  surface ${s.type} elements=${(s.elements ?? []).length} actionable=${s.actionableElementCount}`);
  }
  const all = (lay.surfaces ?? []).flatMap((s) => (s.elements ?? []));
  const labels = all.map((e) => e.label).filter(Boolean);
  console.log('  全部文本(' + labels.length + '): ' + JSON.stringify(labels.slice(0, 80)));
  R.evidence.workTabElementKinds = [...new Set(all.map((e) => e.kind))];
  R.evidence.workTabLabels = labels;
  return { labels: labels.slice(0, 120), kinds: [...new Set(all.map((e) => e.kind))] };
});

// 5. mod settings 里的 allowedWorkTypes（自动化改优先级的一条路）
await step('C12a list_mod_settings_surfaces', async () => {
  const s = await call('rimworld/list_mod_settings_surfaces');
  console.log('  ' + head(s, 2000));
  save('mod_settings_surfaces', s);
  return s;
});

fs.writeFileSync(path.join(OUT, 'C1.json'), JSON.stringify(R, null, 2), 'utf8');
client.close();
console.log('\n报告：' + path.join(OUT, 'C1.json'));
