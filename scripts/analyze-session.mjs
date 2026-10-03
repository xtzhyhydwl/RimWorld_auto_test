#!/usr/bin/env node
/**
 * 分析 DSH 会话文件，看上下文体积花在哪。
 *
 * 用途：决定「该不该重开会话」「该把什么交给子代理」。
 * 用法：node scripts/analyze-session.mjs [会话id或文件路径]
 *       不带参数 = 当前会话（读 DSH_SESSION_ID）
 */
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';

const HOME = process.env.DSH_HOME || path.join(os.homedir(), '.dsh');
const arg = process.argv[2];

function findSessionFile(idOrPath) {
  if (idOrPath && fs.existsSync(idOrPath)) return idOrPath;
  const id = idOrPath || process.env.DSH_SESSION_ID;
  if (!id) throw new Error('没有 DSH_SESSION_ID，也没给参数');
  const root = path.join(HOME, 'storages');
  const hits = [];
  const walk = (dir, depth = 0) => {
    if (depth > 4) return;
    let ents;
    try {
      ents = fs.readdirSync(dir, { withFileTypes: true });
    } catch {
      return;
    }
    for (const e of ents) {
      const p = path.join(dir, e.name);
      if (e.isDirectory()) walk(p, depth + 1);
      else if (e.name.includes(id) && e.name.endsWith('.json')) hits.push(p);
    }
  };
  walk(root);
  if (!hits.length) throw new Error(`找不到会话文件：${id}`);
  return hits.sort((a, b) => fs.statSync(b).size - fs.statSync(a).size)[0];
}

const file = findSessionFile(arg);
const raw = fs.readFileSync(file, 'utf8');
const size = fs.statSync(file).size;

console.log(`会话文件: ${file}`);
console.log(`文件大小: ${(size / 1024).toFixed(1)} KB`);
console.log('');

// 粗略 token 估算：中文约 1.5 字符/token，英文约 4 字符/token。
// 这里用「字符数 / 2.5」做混合估算，只用于量级判断。
function estTokens(s) {
  if (!s) return 0;
  let cjk = 0;
  for (const ch of s) {
    const c = ch.codePointAt(0);
    if (c >= 0x3000 && c <= 0x9fff) cjk++;
  }
  const other = s.length - cjk;
  return Math.round(cjk / 1.5 + other / 4);
}

let data;
try {
  data = JSON.parse(raw);
} catch (e) {
  console.log(`不是单个 JSON（${e.message}）—— 可能是 JSONL，按行分析`);
  const lines = raw.split(/\r?\n/).filter(Boolean);
  console.log(`行数: ${lines.length}`);
  let total = 0;
  const rows = [];
  for (const [i, l] of lines.entries()) {
    total += l.length;
    rows.push({ i, len: l.length });
  }
  rows.sort((a, b) => b.len - a.len);
  console.log('\n最大的 15 行:');
  for (const r of rows.slice(0, 15)) {
    console.log(`  行 ${r.i}: ${(r.len / 1024).toFixed(1)} KB  ≈${estTokens(lines[r.i])} tokens`);
  }
  process.exit(0);
}

// 找出消息数组
function findMessages(obj, depth = 0) {
  if (depth > 5 || !obj || typeof obj !== 'object') return null;
  for (const key of ['messages', 'items', 'entries', 'events', 'history']) {
    if (Array.isArray(obj[key]) && obj[key].length > 3) return obj[key];
  }
  for (const v of Object.values(obj)) {
    const r = findMessages(v, depth + 1);
    if (r) return r;
  }
  return null;
}

const msgs = findMessages(data) ?? (Array.isArray(data) ? data : null);
if (!msgs) {
  console.log('顶层键:', Object.keys(data).join(', '));
  console.log('没找到消息数组，逐键统计体积：');
  for (const [k, v] of Object.entries(data)) {
    const s = JSON.stringify(v);
    console.log(`  ${k}: ${(s.length / 1024).toFixed(1)} KB  ≈${estTokens(s)} tokens`);
  }
  process.exit(0);
}

console.log(`消息条数: ${msgs.length}`);

const byRole = {};
const rows = [];
let totalChars = 0;
for (const [i, m] of msgs.entries()) {
  const s = JSON.stringify(m);
  totalChars += s.length;
  const role = m.role ?? m.type ?? m.kind ?? '?';
  byRole[role] = byRole[role] ?? { n: 0, chars: 0 };
  byRole[role].n += 1;
  byRole[role].chars += s.length;
  rows.push({ i, role, bytes: s.length, tokens: estTokens(s), preview: s.slice(0, 90).replace(/\s+/g, ' ') });
}

const totalTokens = estTokens(raw);
console.log(`总字符: ${(totalChars / 1024).toFixed(1)} K char`);
console.log(`★ 估算 tokens: ≈${totalTokens.toLocaleString()}  (按中英混合 1.5/4 字符每 token)`);
console.log('');
console.log('按角色分布:');
for (const [r, v] of Object.entries(byRole).sort((a, b) => b[1].chars - a[1].chars)) {
  console.log(`  ${r.padEnd(12)} ${String(v.n).padStart(4)} 条   ${(v.chars / 1024).toFixed(1).padStart(8)} KB   ≈${estTokens('x'.repeat(0) || '')}${Math.round(v.chars / 2.5).toLocaleString()} tokens`);
}

rows.sort((a, b) => b.bytes - a.bytes);
console.log('');
console.log('最大的 20 条消息:');
for (const r of rows.slice(0, 20)) {
  console.log(
    `  #${String(r.i).padStart(4)} [${String(r.role).padEnd(10)}] ${(r.bytes / 1024).toFixed(1).padStart(7)} KB  ≈${String(r.tokens).padStart(7)} tok  ${r.preview}`,
  );
}

// 看看有没有内联的图片/base64
const b64 = raw.match(/"data:image\/[a-z]+;base64,/g)?.length ?? 0;
const attRefs = raw.match(/attachments/g)?.length ?? 0;
console.log('');
console.log(`内联 base64 图片: ${b64} 处`);
console.log(`attachments 引用: ${attRefs} 处`);
