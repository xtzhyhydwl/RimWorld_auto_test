#!/usr/bin/env node
/**
 * 分析 CE 窗口的控件结构，找出「按行」的组织规律。
 *
 * 目标：搞清楚怎么定位「某个技能的 − / + 按钮」这类控件 ——
 * 它们自己没有文字，只能靠和文字标签在同一行（screenRect.y 接近）来配对。
 */
import fs from 'node:fs';
import path from 'node:path';
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor, WS } from '../src/launcher.js';
import { layout, describeSurfaces, elements } from '../src/ui.js';

const stamp = new Date().toISOString().replace(/[:.]/g, '-');
const outDir = path.join(WS, 'runs', `ce-analyze-${stamp}`);
fs.mkdirSync(outDir, { recursive: true });

const info = readBridgeInfo(logPathFor('vanilla'));
const { client } = await connectGabp(info, { requestTimeoutMs: 25_000 });
const call = (n, a, o) => client.callTool(n, a, o);
const log = (...a) => console.log(...a);

// 确保 CE 开着
const tabs = await call('rimworld/list_main_tabs');
const ceTab = (tabs.tabs ?? []).find((t) => /character/i.test(t.label ?? ''));
if (!ceTab) throw new Error('找不到 CE 面板');
const st0 = await call('rimbridge/get_bridge_status');
if (st0.state?.inEntryScene) throw new Error('游戏在菜单');
await call('rimworld/open_main_tab', { mainTabId: ceTab.targetId });
await new Promise((r) => setTimeout(r, 1500));

const snap = await layout(client);
fs.writeFileSync(path.join(outDir, 'layout.json'), JSON.stringify(snap, null, 2), 'utf8');
log('界面:');
for (const s of describeSurfaces(snap)) log(`  ${s.id}  ${s.type}  元素 ${s.elements} 可点 ${s.actionable}`);

const ceSurface = (snap.surfaces ?? []).find((s) => /CharacterEditor/i.test(s.type ?? ''));
if (!ceSurface) throw new Error('没有 CE 的 surface');
const els = ceSurface.elements ?? [];
log(`\nCE 窗口元素 ${els.length} 个\n`);

// ── 1. 按行分组（screenRect.y 接近的算一行） ─────────────────
const rows = new Map();
for (const e of els) {
  const y = Math.round((e.screenRect?.y ?? 0) / 4) * 4;
  if (!rows.has(y)) rows.set(y, []);
  rows.get(y).push(e);
}
const sortedRows = [...rows.entries()].sort((a, b) => a[0] - b[0]);
log(`按 y 分成 ${sortedRows.length} 行\n`);

log('=== 含「Shooting」的那几行 ===');
for (const [y, list] of sortedRows) {
  if (list.some((e) => /shooting/i.test(String(e.label ?? '')))) {
    log(`  y≈${y}:`);
    for (const e of list.sort((a, b) => (a.screenRect?.x ?? 0) - (b.screenRect?.x ?? 0))) {
      log(
        `    x=${String(Math.round(e.screenRect?.x)).padStart(5)} w=${String(Math.round(e.screenRect?.width)).padStart(4)} ` +
          `${e.actionable ? '可点' : '不可'} kind=${e.kind} src=${e.source} label=${JSON.stringify(e.label)} id=${e.targetId}`,
      );
    }
  }
}

// ── 2. 看看技能区整体长什么样 ────────────────────────────────
log('\n=== 技能名所在行及相邻可点元素 ===');
const SKILLS = ['Shooting', 'Melee', 'Construction', 'Mining', 'Cooking', 'Plants', 'Animals', 'Crafting', 'Artistic', 'Medical', 'Social', 'Intellectual'];
for (const sk of SKILLS) {
  const rowEntry = sortedRows.find(([, list]) => list.some((e) => e.label === sk));
  if (!rowEntry) {
    log(`  ${sk}: 没找到该行`);
    continue;
  }
  const [y, list] = rowEntry;
  const sorted = list.sort((a, b) => (a.screenRect?.x ?? 0) - (b.screenRect?.x ?? 0));
  const desc = sorted
    .map((e) => `${e.label ?? (e.actionable ? '[' + e.kind + ']' : '[' + e.kind + '·不可]')}@x${Math.round(e.screenRect?.x)}`)
    .join('  |  ');
  log(`  ${sk.padEnd(14)} y=${y}  ${desc}`);
}

// ── 3. 特质区 ────────────────────────────────────────────────
log('\n=== 特质区 ===');
for (const [y, list] of sortedRows) {
  if (list.some((e) => /ascetic|misogynist|chemical interest/i.test(String(e.label ?? '')))) {
    const sorted = list.sort((a, b) => (a.screenRect?.x ?? 0) - (b.screenRect?.x ?? 0));
    log(`  y=${y}: ${sorted.map((e) => `${e.label ?? '[' + e.kind + ']'}@x${Math.round(e.screenRect?.x)}`).join('  |  ')}`);
  }
}

// ── 4. 顶部标签页按钮 ────────────────────────────────────────
log('\n=== 顶部标签页（Bio/Inventory/Health/...） ===');
for (const [y, list] of sortedRows.slice(0, 20)) {
  if (list.some((e) => /^(Bio|Inventory|Health|Needs|Social|Log|Info|Records)$/.test(String(e.label ?? '')))) {
    const sorted = list.sort((a, b) => (a.screenRect?.x ?? 0) - (b.screenRect?.x ?? 0));
    log(`  y=${y}:`);
    for (const e of sorted) log(`    ${e.actionable ? '可点' : '不可'} ${e.kind.padEnd(7)} label=${JSON.stringify(e.label)} id=${e.targetId} x=${Math.round(e.screenRect?.x)}`);
  }
}

// ── 5. 殖民者列表 ────────────────────────────────────────────
log('\n=== 左侧殖民者列表（含名字的行） ===');
for (const [y, list] of sortedRows) {
  const hit = list.find((e) => /^(Alex|Guillermo|Troy)$/.test(String(e.label ?? '')));
  if (hit) {
    const sorted = list.sort((a, b) => (a.screenRect?.x ?? 0) - (b.screenRect?.x ?? 0));
    log(`  y=${y}: ${sorted.map((e) => `${JSON.stringify(e.label) ?? '[' + e.kind + ']'}@x${Math.round(e.screenRect?.x)}${e.actionable ? '(可点)' : ''}`).join('  |  ')}`);
  }
}

// ── 6. 元素来源统计（了解 CE 用什么绘制） ────────────────────
log('\n=== 元素来源（source）统计 ===');
const bySrc = {};
for (const e of els) bySrc[e.source ?? '?'] = (bySrc[e.source ?? '?'] ?? 0) + 1;
for (const [k, v] of Object.entries(bySrc).sort((a, b) => b[1] - a[1])) log(`  ${String(k).padEnd(20)} ${v}`);
log('\n=== kind 统计 ===');
const byKind = {};
for (const e of els) byKind[e.kind ?? '?'] = (byKind[e.kind ?? '?'] ?? 0) + 1;
for (const [k, v] of Object.entries(byKind).sort((a, b) => b[1] - a[1])) log(`  ${String(k).padEnd(20)} ${v}`);

client.close();
log(`\n产物: ${outDir}`);
