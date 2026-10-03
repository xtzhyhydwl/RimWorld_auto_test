#!/usr/bin/env node
/** 把 DSH 的 contextBreakdown / contextPressure 解读出来：到底什么在吃上下文。 */
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';

const HOME = process.env.DSH_HOME || path.join(os.homedir(), '.dsh');
const id = process.argv[2] || process.env.DSH_SESSION_ID;
const hits = [];
(function walk(d, depth = 0) {
  if (depth > 4) return;
  let ents = [];
  try { ents = fs.readdirSync(d, { withFileTypes: true }); } catch { return; }
  for (const e of ents) {
    const p = path.join(d, e.name);
    if (e.isDirectory()) walk(p, depth + 1);
    else if (e.name.includes(id) && e.name.endsWith('.json')) hits.push(p);
  }
})(path.join(HOME, 'storages'));
const file = hits.sort((a, b) => fs.statSync(b).size - fs.statSync(a).size)[0];
const data = JSON.parse(fs.readFileSync(file, 'utf8'));
const rows = data.record.rows;

const cp = rows.contextPressure?.val;
if (cp) {
  console.log('=== 上下文压力 ===');
  for (const [k, v] of Object.entries(cp)) {
    if (typeof v === 'number') console.log(`  ${k.padEnd(24)} ${v.toLocaleString()}`);
  }
  if (cp.contextWindow) {
    console.log(`  ${'占用率'.padEnd(22)} ${((cp.surfaceTokens / cp.contextWindow) * 100).toFixed(1)}%`);
  }
}

const tu = rows.tokenUsage?.val;
if (tu?.totals) {
  console.log('\n=== 累计 token 用量（整个会话）===');
  for (const [k, v] of Object.entries(tu.totals)) console.log(`  ${k.padEnd(24)} ${Number(v).toLocaleString()}`);
}

const nodes = rows.contextBreakdown?.val?.nodes;
if (Array.isArray(nodes)) {
  console.log(`\n=== 上下文构成（${nodes.length} 个节点）===`);
  // 先看节点长什么样
  console.log('节点字段:', Object.keys(nodes[0]).join(', '));
  console.log('\n样例:');
  for (const n of nodes.slice(0, 3)) console.log('  ' + JSON.stringify(n).slice(0, 220));

  const key = 'heuristicTokens' in nodes[0] ? 'heuristicTokens' : 'tokens';
  const sorted = [...nodes].sort((a, b) => (b[key] ?? 0) - (a[key] ?? 0));
  const total = nodes.reduce((s, n) => s + (n[key] ?? 0), 0);
  console.log(`\n${key} 合计: ${total.toLocaleString()}`);

  // 按 kind / type 汇总
  const byKind = {};
  for (const n of nodes) {
    const k = n.kind ?? n.type ?? n.category ?? n.label ?? n.name ?? '?';
    byKind[k] = byKind[k] ?? { n: 0, tok: 0 };
    byKind[k].n += 1;
    byKind[k].tok += n[key] ?? 0;
  }
  console.log('\n按类别汇总:');
  for (const [k, v] of Object.entries(byKind).sort((a, b) => b[1].tok - a[1].tok)) {
    console.log(`  ${String(k).padEnd(28)} ${String(v.n).padStart(4)} 个  ${String(v.tok).padStart(9)} tok  ${((v.tok / total) * 100).toFixed(1)}%`);
  }

  console.log('\n最占地方的 25 个节点:');
  for (const n of sorted.slice(0, 25)) {
    const label = n.label ?? n.name ?? n.title ?? n.kind ?? n.type ?? '';
    const extra = n.toolName ?? n.command ?? n.path ?? '';
    console.log(`  ${String(n[key] ?? 0).padStart(8)} tok  ${String(label).slice(0, 30).padEnd(32)} ${String(extra).slice(0, 60)}`);
  }
}

const spans = rows.contextTimeline?.val?.spans;
if (Array.isArray(spans) && spans.length) {
  console.log(`\n=== contextTimeline spans（${spans.length}）===`);
  console.log('字段:', Object.keys(spans[0]).join(', '));
  for (const s of spans.slice(0, 5)) console.log('  ' + JSON.stringify(s).slice(0, 200));
}

const reqs = rows.contextTimeline?.val?.requests;
if (Array.isArray(reqs) && reqs.length) {
  console.log(`\n=== 请求历史（${reqs.length} 次模型调用）===`);
  console.log('字段:', Object.keys(reqs[0]).join(', '));
  const last = reqs[reqs.length - 1];
  console.log('最后一次:', JSON.stringify(last).slice(0, 400));
}
