#!/usr/bin/env node
/** 摸清会话 JSON 的结构，找到真正存对话和 token 统计的地方。 */
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';

const HOME = process.env.DSH_HOME || path.join(os.homedir(), '.dsh');
const id = process.argv[2] || process.env.DSH_SESSION_ID;
const root = path.join(HOME, 'storages');
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
})(root);
const file = hits.sort((a, b) => fs.statSync(b).size - fs.statSync(a).size)[0];
const raw = fs.readFileSync(file, 'utf8');
const data = JSON.parse(raw);
console.log(`文件: ${(raw.length / 1024).toFixed(1)} KB\n`);

function sizeOf(v) {
  try { return JSON.stringify(v)?.length ?? 0; } catch { return -1; }
}

console.log('=== 顶层键及体积 ===');
for (const [k, v] of Object.entries(data)) {
  const t = Array.isArray(v) ? `array(${v.length})` : typeof v;
  console.log(`  ${k.padEnd(24)} ${String(t).padEnd(14)} ${(sizeOf(v) / 1024).toFixed(1)} KB`);
}

// 递归找最大的几个分支
const branches = [];
(function rec(v, p, depth) {
  if (depth > 6 || v == null) return;
  const sz = sizeOf(v);
  if (sz > 20000) branches.push({ p, sz, type: Array.isArray(v) ? `array(${v.length})` : typeof v });
  if (Array.isArray(v)) {
    v.slice(0, 3).forEach((x, i) => rec(x, `${p}[${i}]`, depth + 1));
    if (v.length > 3) rec(v[v.length - 1], `${p}[last]`, depth + 1);
  } else if (typeof v === 'object') {
    for (const [k, x] of Object.entries(v)) rec(x, `${p}.${k}`, depth + 1);
  }
})(data, '$', 0);

console.log('\n=== 体积 >20KB 的分支 ===');
for (const b of branches.sort((a, b) => b.sz - a.sz).slice(0, 25)) {
  console.log(`  ${(b.sz / 1024).toFixed(1).padStart(8)} KB  ${b.type.padEnd(14)} ${b.p}`);
}

// 找所有 tokens 字段的汇总
let tokenSum = 0;
const tokenHits = [];
(function rec(v, p) {
  if (v == null || typeof v !== 'object') return;
  if (Array.isArray(v)) { for (const x of v) rec(x, p); return; }
  for (const [k, x] of Object.entries(v)) {
    if (k === 'tokens' && typeof x === 'number') { tokenSum += x; tokenHits.push({ p, x }); }
    else if (k.match(/token|usage|context/i) && typeof x === 'number') tokenHits.push({ p: p + '.' + k, x });
    else rec(x, p + '.' + k);
  }
})(data, '$');
console.log(`\n=== token 相关字段 ===`);
console.log(`  形如 .tokens 的字段共 ${tokenHits.filter(h => h.p.endsWith('.tokens')).length} 个，合计 ${tokenSum.toLocaleString()}`);
for (const h of tokenHits.filter(h => !h.p.endsWith('.tokens')).slice(0, 20)) {
  console.log(`  ${h.p} = ${h.x}`);
}
console.log('\n最大的 10 个 tokens 条目:');
for (const h of tokenHits.filter(h => h.p.endsWith('.tokens')).sort((a, b) => b.x - a.x).slice(0, 10)) {
  console.log(`  ${String(h.x).padStart(8)}  ${h.p}`);
}
