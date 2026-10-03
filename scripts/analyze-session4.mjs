#!/usr/bin/env node
/** 按轮次看上下文增长曲线：哪一轮跳得最猛，谁贡献的。 */
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
const reqs = JSON.parse(fs.readFileSync(file, 'utf8')).record.rows.contextTimeline.val.requests;

console.log(`共 ${reqs.length} 次模型调用\n`);

// 每一轮取最后一次调用（该轮结束时的上下文规模）
const byTurn = new Map();
for (const r of reqs) {
  const t = r.turn ?? 0;
  const prev = byTurn.get(t);
  if (!prev || (r.step ?? 0) >= (prev.step ?? 0)) byTurn.set(t, r);
}
const turns = [...byTurn.entries()].sort((a, b) => a[0] - b[0]);

console.log('轮次   总上下文   本轮增量   assistant    tool   inject   其他   step');
console.log('─'.repeat(88));
let prevTotal = 0;
for (const [t, r] of turns) {
  const total = r.total ?? 0;
  const delta = total - prevTotal;
  const other = (r.system ?? 0) + (r.tools ?? 0) + (r.skill ?? 0) + (r.user ?? 0);
  console.log(
    `${String(t).padStart(4)}  ${String(total).padStart(9)}  ${String(delta >= 0 ? '+' + delta : delta).padStart(9)}  ` +
      `${String(r.assistant ?? 0).padStart(9)}  ${String(r.tool ?? 0).padStart(7)}  ${String(r.inject ?? 0).padStart(6)}  ` +
      `${String(other).padStart(5)}  ${String(r.step ?? 0).padStart(4)}`,
  );
  prevTotal = total;
}

// 增量最大的几轮
const deltas = [];
prevTotal = 0;
for (const [t, r] of turns) {
  deltas.push({ t, delta: (r.total ?? 0) - prevTotal, total: r.total ?? 0, a: r.assistant ?? 0, tool: r.tool ?? 0 });
  prevTotal = r.total ?? 0;
}
console.log('\n增量最大的 6 轮:');
for (const d of deltas.sort((a, b) => b.delta - a.delta).slice(0, 6)) {
  console.log(`  第 ${d.t} 轮: +${d.delta.toLocaleString()} tok  (assistant ${d.a.toLocaleString()}, tool ${d.tool.toLocaleString()})`);
}

const last = reqs[reqs.length - 1];
console.log('\n=== 最终占比 ===');
const tot = last.total || 1;
for (const k of ['assistant', 'tool', 'inject', 'tools', 'skill', 'system', 'user']) {
  const v = last[k] ?? 0;
  console.log(`  ${k.padEnd(12)} ${String(v).padStart(9)}  ${((v / tot) * 100).toFixed(1).padStart(5)}%  ${'█'.repeat(Math.round((v / tot) * 40))}`);
}
console.log(`  ${'总计'.padEnd(12)} ${String(last.total).padStart(9)}`);
console.log(`\n  窗口 1,000,000，已用 ${((last.total / 1000000) * 100).toFixed(1)}%`);
