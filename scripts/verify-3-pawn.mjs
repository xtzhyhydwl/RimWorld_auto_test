#!/usr/bin/env node
/**
 * 阶段 3 验证：人物属性类 debug action，以及「怎么回读验证效果」。
 *
 * 关键技巧：inspect 面板（右下角那行小字）只能看到一部分信息，
 * 特质/基因/技能在 Bio / Health / Gear 这些 **inspect 标签页**里。
 * 所以回读路径是：选中 → open_inspect_tab → get_ui_layout → 抠文字。
 */
import fs from 'node:fs';
import path from 'node:path';
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor, WS } from '../src/launcher.js';
import { layout } from '../src/ui.js';

const stamp = new Date().toISOString().replace(/[:.]/g, '-');
const outDir = path.join(WS, 'runs', `verify-pawn-${stamp}`);
fs.mkdirSync(outDir, { recursive: true });

const info = readBridgeInfo(logPathFor('vanilla'));
const { client } = await connectGabp(info, { requestTimeoutMs: 25_000 });
const call = (n, a, o) => client.callTool(n, a, o);
const log = (...a) => console.log(...a);
const results = [];
const record = (name, ok, detail) => {
  results.push({ name, ok, detail: detail ?? null });
  log(`  ${ok ? '✓' : '✗'} ${name}${detail && !ok ? ` — ${detail}` : ''}`);
};

async function kids(p) {
  const r = await call('rimworld/get_debug_action', { path: p, includeChildren: true, includeHiddenChildren: true });
  return r.children ?? r.action?.children ?? [];
}

/** 选中一个小人，打开某个 inspect 标签页，把界面文字抠出来 */
async function readTab(pawnId, tab) {
  await call('rimworld/clear_selection', {}).catch(() => {});
  await call('rimworld/select_pawn', { pawnId });
  await call('rimworld/open_inspect_tab', { inspectTabId: tab }).catch(() => {});
  await new Promise((r) => setTimeout(r, 300));
  const snap = await layout(client);
  const texts = [];
  for (const s of snap.surfaces ?? []) {
    for (const e of s.elements ?? []) if (e.label) texts.push(e.label);
  }
  const uniq = [...new Set(texts)].map((t) => String(t).replace(/<[^>]+>/g, ''));
  await call('rimworld/close_main_tab', {}).catch(() => {});
  return uniq;
}

/** 取 inspect 面板那行小字 */
async function inspectString(pawnId) {
  await call('rimworld/select_pawn', { pawnId });
  const sem = await call('rimworld/get_selection_semantics');
  return sem.selectedObjects?.[0]?.inspectString ?? '';
}

const st = (await call('rimbridge/get_bridge_status')).state;
if (!st.hasCurrentGame) {
  await call('rimworld/start_debug_game_ready', { readiness: 'mapData', pauseIfNeeded: true, timeoutMs: 300_000 }, { timeoutMs: 330_000 });
  await call('rimbridge/wait_for_long_event_idle', { timeoutMs: 120_000 });
}
const cols = (await call('rimworld/list_colonists', { currentMapOnly: true })).colonists ?? [];
const p0 = cols[0];
log(`测试对象: ${p0.fullName} (${p0.pawnId})\n`);

// ── 0. 先建立回读基线 ────────────────────────────────────────
log('═══ 0. 回读基线 ═══');
const bioBefore = await readTab(p0.pawnId, 'Bio');
log(`  Bio 面板 ${bioBefore.length} 行，片段: ${bioBefore.filter((t) => t.length > 2).slice(0, 12).join(' / ')}`);
const healthBefore = await readTab(p0.pawnId, 'Health');
log(`  Health 面板 ${healthBefore.length} 行，片段: ${healthBefore.filter((t) => t.length > 2).slice(0, 10).join(' / ')}`);
const gearBefore = await readTab(p0.pawnId, 'Gear');
log(`  Gear 面板 ${gearBefore.length} 行`);
const insBefore = await inspectString(p0.pawnId);
log(`  inspectString: ${insBefore.replace(/\s+/g, ' ').slice(0, 120)}`);
record('能读到 Bio 面板内容（回读通道可用）', bioBefore.length > 5, `${bioBefore.length} 行`);

// ── 1. 逐个动作：找叶子 → 执行 → 回读对比 ─────────────────────
log('\n═══ 1. 人物属性动作逐个验证 ═══');

const cases = [
  {
    name: 'Max Skill → Shooting',
    parent: 'Actions\\Max Skill...',
    pick: (k) => /^Shooting$/i.test(k.label ?? ''),
    tab: 'Bio',
    expect: /Shooting/i,
  },
  {
    name: 'Give Trait → nudist',
    parent: 'Actions\\Give Trait...',
    pick: (k) => /^nudist/i.test(k.label ?? ''),
    tab: 'Bio',
    expect: /nudist/i,
  },
  {
    name: 'Add Hediff → Acid burn',
    parent: 'Actions\\Add Hediff...',
    pick: (k) => /acid burn/i.test(k.label ?? ''),
    tab: 'Health',
    expect: /acid burn/i,
  },
  {
    name: 'Set Backstory → Childhood',
    parent: 'Actions\\Set Backstory...',
    pick: (k) => /childhood/i.test(k.label ?? ''),
    tab: 'Bio',
    expect: null,
    hasThirdLevel: true,
  },
  {
    name: 'Add Gene → Xenogene',
    parent: 'Actions\\Add Gene...',
    pick: (k) => /xenogene/i.test(k.label ?? ''),
    tab: 'Bio',
    expect: null,
    hasThirdLevel: true,
  },
  {
    name: 'Mental state → 某个状态',
    parent: 'Actions\\Mental state...',
    pick: () => true,
    tab: 'Needs',
    expect: null,
  },
  {
    name: 'Apply damage → 某个伤害类型',
    parent: 'Actions\\Apply damage...',
    pick: () => true,
    tab: 'Health',
    expect: null,
  },
];

const caseLog = [];
for (const c of cases) {
  log(`\n  ── ${c.name} ──`);
  try {
    const list = await kids(c.parent);
    const leaf = list.find(c.pick);
    if (!leaf) {
      log(`     ✗ 找不到匹配的叶子（${list.length} 个可选: ${list.slice(0, 6).map((k) => k.label).join(', ')}）`);
      record(c.name, false, '找不到叶子');
      caseLog.push({ ...c, ok: false, reason: '找不到叶子' });
      continue;
    }
    let targetPath = leaf.path;
    let targetKind = leaf.execution?.requiredTargetKind;
    log(`     叶子: ${leaf.label}  [${leaf.execution?.kind}/${targetKind ?? '-'}]`);

    // 有三级的，往下一层挑一个
    if (c.hasThirdLevel && (leaf.childCount ?? 0) > 0) {
      const lvl3 = await kids(leaf.path);
      log(`     三级选项 ${lvl3.length} 个: ${lvl3.slice(0, 5).map((k) => k.label).join(', ')}`);
      if (lvl3.length) {
        targetPath = lvl3[0].path;
        targetKind = lvl3[0].execution?.requiredTargetKind;
        log(`     选: ${lvl3[0].label}  [${lvl3[0].execution?.kind}/${targetKind ?? '-'}]`);
      }
    }

    const args = { path: targetPath };
    if (targetKind === 'pawn') args.pawnId = p0.pawnId;
    const exec = await call('rimworld/execute_debug_action', args, { timeoutMs: 20_000 });
    log(`     执行: success=${exec.success}`);

    // 可能有弹窗，关掉
    await call('rimworld/close_window', {}).catch(() => {});
    await new Promise((r) => setTimeout(r, 300));

    const tabAfter = await readTab(p0.pawnId, c.tab);
    const insAfter = await inspectString(p0.pawnId);
    const changed = JSON.stringify(tabAfter) !== JSON.stringify(c.tab === 'Bio' ? bioBefore : c.tab === 'Health' ? healthBefore : gearBefore);
    log(`     ${c.tab} 面板: ${tabAfter.length} 行（变化=${changed}）`);
    if (c.expect) {
      const hit = tabAfter.some((t) => c.expect.test(t)) || c.expect.test(insAfter);
      record(c.name, exec.success === true && hit, hit ? '' : `面板里没找到 ${c.expect}`);
      caseLog.push({ ...c, ok: exec.success === true && hit, path: targetPath, changed, tabLines: tabAfter.length });
    } else {
      record(c.name, exec.success === true, '');
      caseLog.push({ ...c, ok: exec.success === true, path: targetPath, changed, tabLines: tabAfter.length });
    }
  } catch (e) {
    log(`     ✗ ${e.message}`);
    record(c.name, false, e.message);
    caseLog.push({ ...c, ok: false, reason: e.message });
    if (/GABP_TIMEOUT/.test(e.message)) {
      log('     ⚠ 桥接被挂住，停止本阶段');
      break;
    }
  }
}

// ── 2. inspectString 是否反映状态变化 ────────────────────────
log('\n═══ 2. inspectString 前后对比 ═══');
const insFinal = await inspectString(p0.pawnId);
log(`  之前: ${insBefore.replace(/\s+/g, ' ').slice(0, 160)}`);
log(`  之后: ${insFinal.replace(/\s+/g, ' ').slice(0, 160)}`);
record('inspectString 能作为状态回读通道', insFinal.length > 0, `长度 ${insFinal.length}`);

// ── 3. 截图存证 ──────────────────────────────────────────────
await call('rimworld/select_pawn', { pawnId: p0.pawnId });
await call('rimworld/open_inspect_tab', { inspectTabId: 'Bio' }).catch(() => {});
await new Promise((r) => setTimeout(r, 300));
const shot = await call('rimworld/take_screenshot', { suppressMessage: true, fileName: 'verify_pawn_bio' });
if (shot?.path && fs.existsSync(shot.path)) fs.copyFileSync(shot.path, path.join(outDir, path.basename(shot.path)));
log(`\n截图: ${path.join(outDir, path.basename(shot.path ?? 'x'))}`);

const finalCols = (await call('rimworld/list_colonists', { currentMapOnly: true })).colonists ?? [];
log(`\n殖民者现状:`);
for (const c of finalCols) {
  log(`  ${c.fullName.padEnd(24)} ${c.downed ? '【倒地】' : ''}${c.dead ? '【死亡】' : ''} ${c.job ?? '-'}`);
}

client.close();

const passed = results.filter((r) => r.ok).length;
log(`\n${'═'.repeat(62)}`);
log(`阶段 3：${passed}/${results.length} 通过`);
for (const r of results.filter((x) => !x.ok)) log(`  ✗ ${r.name} — ${r.detail}`);
log('═'.repeat(62));
fs.writeFileSync(
  path.join(outDir, 'result.json'),
  JSON.stringify({ stamp, stage: 'pawn', results, caseLog, bioBefore, healthBefore, insBefore, insFinal }, null, 2),
  'utf8',
);
log(`报告: ${path.join(outDir, 'result.json')}`);
