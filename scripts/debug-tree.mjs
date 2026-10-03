#!/usr/bin/env node
/**
 * 爬取 RimWorld 原版 debug action 树，做成可检索的目录。
 *
 * debug action 树就是原版开发者控制台的全部内容。RimBridgeServer 把它结构化暴露出来
 * （路径 / 标签 / 分类 / 能否直接执行 / 需要什么目标），比人肉点菜单可靠得多。
 *
 * 用法：
 *   node scripts/debug-tree.mjs                 # 默认最多 4000 个节点
 *   node scripts/debug-tree.mjs --max 8000
 *   node scripts/debug-tree.mjs --depth 3
 *
 * 安全：每次调用 15 秒超时；一旦超时立刻停止并保存已爬到的部分
 * （桥接一旦被挂住，后续调用也会挂，继续爬没有意义）。
 */
import fs from 'node:fs';
import path from 'node:path';
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor, WS } from '../src/launcher.js';

const argv = process.argv.slice(2);
const argOf = (name, dflt) => {
  const i = argv.indexOf(name);
  return i >= 0 && argv[i + 1] ? Number(argv[i + 1]) : dflt;
};
const MAX_NODES = argOf('--max', 4000);
const MAX_DEPTH = argOf('--depth', 8);
const CALL_TIMEOUT = 15_000;

const stamp = new Date().toISOString().replace(/[:.]/g, '-');
const outDir = path.join(WS, 'runs', `debug-tree-${stamp}`);
fs.mkdirSync(outDir, { recursive: true });

const info = readBridgeInfo(logPathFor('vanilla'));
if (!info) throw new Error('找不到桥接，先启动游戏。');
const { client } = await connectGabp(info, { requestTimeoutMs: CALL_TIMEOUT });
const call = (n, a, o) => client.callTool(n, a, o);

const startedAt = Date.now();
const nodes = new Map(); // path -> node record
let calls = 0;
let aborted = null;

function record(n, depth, parentPath) {
  if (!n?.path || nodes.has(n.path)) return false;
  nodes.set(n.path, {
    path: n.path,
    parentPath: parentPath ?? n.parentPath ?? null,
    depth,
    label: n.label ?? null,
    category: n.category ?? null,
    actionType: n.actionType ?? null,
    tabId: n.tabId ?? null,
    tabTitle: n.tabTitle ?? null,
    visible: n.visible ?? null,
    active: n.active ?? null,
    on: n.on ?? null,
    hasChildren: n.hasChildren ?? null,
    childCount: n.childCount ?? null,
    hasDirectAction: n.hasDirectAction ?? null,
    hasPawnAction: n.hasPawnAction ?? null,
    hasSettingsToggle: n.hasSettingsToggle ?? null,
    execution: n.execution
      ? {
          kind: n.execution.kind ?? null,
          supported: n.execution.supported ?? null,
          reason: n.execution.reason ?? null,
          requiredTargetKind: n.execution.requiredTargetKind ?? null,
        }
      : null,
    source: n.source
      ? {
          name: n.source.name ?? null,
          category: n.source.category ?? null,
          allowedGameStates: n.source.allowedGameStates ?? null,
          requiresRoyalty: n.source.requiresRoyalty ?? null,
          requiresIdeology: n.source.requiresIdeology ?? null,
          requiresBiotech: n.source.requiresBiotech ?? null,
          requiresAnomaly: n.source.requiresAnomaly ?? null,
          requiresOdyssey: n.source.requiresOdyssey ?? null,
        }
      : null,
  });
  return true;
}

/** 取一个节点的元数据与直接子节点 */
async function fetchNode(p) {
  const res = await call('rimworld/get_debug_action', {
    path: p,
    includeChildren: true,
    includeHiddenChildren: true,
  });
  calls += 1;
  const node = res.action ?? res.node ?? res;
  const children = res.children ?? node.children ?? res.childActions ?? [];
  return { node, children };
}

// ── 起点 ──────────────────────────────────────────────────────
console.log('列举 debug action 根节点…');
const rootsRes = await call('rimworld/list_debug_action_roots', { includeHidden: true });
calls += 1;
const roots = rootsRes.roots ?? rootsRes.actions ?? [];
console.log(`根节点 ${roots.length} 个: ${roots.map((r) => r.label).join(', ')}`);

for (const r of roots) record(r, 0, null);

const queue = roots.map((r) => ({ path: r.path, depth: 0 }));
const seen = new Set(queue.map((q) => q.path));

// ── 广度优先 ─────────────────────────────────────────────────
let lastLog = Date.now();
while (queue.length && nodes.size < MAX_NODES && !aborted) {
  const { path: p, depth } = queue.shift();
  if (depth >= MAX_DEPTH) continue;

  let fetched;
  try {
    fetched = await fetchNode(p);
  } catch (err) {
    aborted = { at: p, error: err.message };
    console.error(`\n[中断] ${p}: ${err.message}`);
    break;
  }

  record(fetched.node, depth, null);

  for (const c of fetched.children ?? []) {
    const isNew = record(c, depth + 1, p);
    if (isNew && c.path && !seen.has(c.path)) {
      seen.add(c.path);
      queue.push({ path: c.path, depth: depth + 1 });
    }
  }

  if (Date.now() - lastLog > 4000) {
    lastLog = Date.now();
    const pct = ((nodes.size / MAX_NODES) * 100).toFixed(0);
    console.log(
      `  已爬 ${nodes.size} 节点 / 队列 ${queue.length} / 调用 ${calls} 次 / 深度 ${depth}  (${pct}%)`,
    );
  }
}

const elapsed = ((Date.now() - startedAt) / 1000).toFixed(1);
console.log(`\n爬取结束：${nodes.size} 个节点，${calls} 次调用，耗时 ${elapsed}s`);
if (aborted) console.log(`⚠ 提前中断：${aborted.error}`);
if (nodes.size >= MAX_NODES) console.log(`⚠ 达到节点上限 ${MAX_NODES}，树可能没爬完`);

const all = [...nodes.values()];

// ── 落盘 ─────────────────────────────────────────────────────
const payload = {
  capturedAt: new Date().toISOString(),
  elapsedSeconds: Number(elapsed),
  callCount: calls,
  nodeCount: all.length,
  truncated: Boolean(aborted) || nodes.size >= MAX_NODES,
  aborted,
  roots: roots.map((r) => r.path),
  nodes: all,
};
fs.writeFileSync(path.join(outDir, 'tree.json'), JSON.stringify(payload, null, 2), 'utf8');
fs.writeFileSync(
  path.join(WS, 'runs', 'debug-tree-latest.json'),
  JSON.stringify(payload, null, 2),
  'utf8',
);

// ── 统计 ─────────────────────────────────────────────────────
const byTop = {};
for (const n of all) {
  const top = n.path.split('\\')[0];
  byTop[top] = (byTop[top] ?? 0) + 1;
}
const executable = all.filter((n) => n.execution?.supported);
const direct = all.filter((n) => n.execution?.kind === 'Direct' && n.execution?.supported);
const byKind = {};
for (const n of all) {
  const k = n.execution?.kind ?? '(无执行信息)';
  byKind[k] = (byKind[k] ?? 0) + 1;
}
const byTarget = {};
for (const n of all) {
  const k = n.execution?.requiredTargetKind ?? '(不需要)';
  byTarget[k] = (byTarget[k] ?? 0) + 1;
}

// 二级分类统计
const bySecond = {};
for (const n of all) {
  const parts = n.path.split('\\');
  if (parts.length >= 2) {
    const key = `${parts[0]} › ${parts[1]}`;
    bySecond[key] = (bySecond[key] ?? 0) + 1;
  }
}

console.log('\n=== 顶层分布 ===');
for (const [k, v] of Object.entries(byTop).sort((a, b) => b[1] - a[1])) {
  console.log(`  ${k.padEnd(14)} ${v}`);
}
console.log('\n=== 执行类型 ===');
for (const [k, v] of Object.entries(byKind).sort((a, b) => b[1] - a[1])) {
  console.log(`  ${k.padEnd(16)} ${v}`);
}
console.log('\n=== 需要的目标类型 ===');
for (const [k, v] of Object.entries(byTarget).sort((a, b) => b[1] - a[1])) {
  console.log(`  ${k.padEnd(20)} ${v}`);
}
console.log('\n=== 二级分类 Top 30 ===');
for (const [k, v] of Object.entries(bySecond).sort((a, b) => b[1] - a[1]).slice(0, 30)) {
  console.log(`  ${k.padEnd(46)} ${v}`);
}
console.log(`\n可直接执行（Direct）: ${direct.length}`);
console.log(`全部可执行: ${executable.length} / ${all.length}`);
console.log(`证据: ${outDir}`);

// ── markdown 目录 ────────────────────────────────────────────
const md = [];
md.push('# RimWorld 原版 debug action 树目录');
md.push('');
md.push(`- 抓取时间：${payload.capturedAt}`);
md.push(`- 节点数：**${all.length}**${payload.truncated ? '（未爬完，见下方中断原因）' : ''}`);
md.push(`- 调用次数：${calls}，耗时 ${elapsed}s`);
if (aborted) md.push(`- ⚠ 中断于 \`${aborted.at}\`：${aborted.error}`);
md.push('');
md.push('## 顶层分布');
md.push('');
md.push('| 顶层 | 节点数 |');
md.push('| --- | --- |');
for (const [k, v] of Object.entries(byTop).sort((a, b) => b[1] - a[1])) md.push(`| ${k} | ${v} |`);
md.push('');
md.push('## 执行类型');
md.push('');
md.push('| 类型 | 含义 | 数量 |');
md.push('| --- | --- | --- |');
const kindDesc = {
  Direct: '点了就执行，不需要目标',
  PawnTarget: '需要先选一个小人',
  MapTarget: '需要在地图上点一个位置',
  ThingTarget: '需要指定一个物体',
  Toggle: '开关型',
  Unknown: '类型未知',
};
for (const [k, v] of Object.entries(byKind).sort((a, b) => b[1] - a[1])) {
  md.push(`| ${k} | ${kindDesc[k] ?? ''} | ${v} |`);
}
md.push('');
md.push('## 二级分类（节点数 Top 60）');
md.push('');
md.push('| 分类 | 节点数 |');
md.push('| --- | --- |');
for (const [k, v] of Object.entries(bySecond).sort((a, b) => b[1] - a[1]).slice(0, 60)) {
  md.push(`| ${k} | ${v} |`);
}
md.push('');
md.push('## 可直接执行且不需要目标的动作（前 200 个）');
md.push('');
md.push('| 路径 | 标签 |');
md.push('| --- | --- |');
for (const n of direct.slice(0, 200)) {
  md.push(`| \`${n.path}\` | ${n.label ?? ''} |`);
}
md.push('');
md.push('## 完整节点清单');
md.push('');
md.push('见同目录 `tree.json`（含每项的 execution.kind / supported / requiredTargetKind / source）。');
md.push('');
fs.writeFileSync(path.join(outDir, 'CATALOG.md'), md.join('\n'), 'utf8');
console.log(`目录: ${path.join(outDir, 'CATALOG.md')}`);

client.close();
