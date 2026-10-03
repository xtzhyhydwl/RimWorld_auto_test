#!/usr/bin/env node
/**
 * Character Editor 接入探索。
 *
 * 目标：搞清楚 CE 在桥接下长什么样、能不能被 UI 自动化驱动。
 *  - 是否加载
 *  - 它加的主面板叫什么
 *  - 打开后 UI 布局的结构（surfaces / elements / 可点控件）
 *  - 有没有稳定的 targetId 可以点
 */
import fs from 'node:fs';
import path from 'node:path';
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor, WS } from '../src/launcher.js';
import { layout, describeSurfaces, elements, clickRowByLabel } from '../src/ui.js';

const stamp = new Date().toISOString().replace(/[:.]/g, '-');
const outDir = path.join(WS, 'runs', `ce-explore-${stamp}`);
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

async function shot(label) {
  const r = await call('rimworld/take_screenshot', { suppressMessage: true, fileName: `ce_${label}` });
  if (r?.path && fs.existsSync(r.path)) {
    fs.copyFileSync(r.path, path.join(outDir, path.basename(r.path)));
  }
  return r?.path ?? null;
}

// ── 1. mod 是否加载 ──────────────────────────────────────────
log('═══ 1. Character Editor 是否加载 ═══');
const mods = await call('rimworld/list_mods', { compatibleOnly: false });
const ce = (mods.mods ?? []).find((m) => /void\.charactereditor/i.test(m.packageId ?? ''));
if (ce) {
  log(`  ${ce.name}  v${ce.version || '?'}  source=${ce.source}  loadOrder=${ce.activeLoadOrder}`);
  log(`  enabled=${ce.enabled} loadedInSession=${ce.loadedInSession} 警告=${ce.configurationWarning || '无'}`);
} else {
  log('  ✗ 没找到');
}
record('Character Editor 已加载', Boolean(ce?.enabled && ce?.loadedInSession), `enabled=${ce?.enabled} loaded=${ce?.loadedInSession}`);
log(`  活动 mod 数 = ${mods.activeCount}`);

// ── 2. 确保在游戏里 ──────────────────────────────────────────
const st = (await call('rimbridge/get_bridge_status')).state;
if (st.inEntryScene) {
  log('\n开调试殖民地…');
  await call('rimworld/start_debug_game_ready', { readiness: 'mapData', pauseIfNeeded: true, timeoutMs: 300_000 }, { timeoutMs: 330_000 });
}
await call('rimbridge/wait_for_game_loaded', { readiness: 'currentMap', pauseIfNeeded: true, timeoutMs: 180_000 }, { timeoutMs: 210_000 });
await call('rimbridge/wait_for_long_event_idle', { timeoutMs: 60_000 });

// ── 3. 主面板里有没有 CE ─────────────────────────────────────
log('\n═══ 2. 主面板清单 ═══');
const tabs = (await call('rimworld/list_main_tabs')).tabs ?? [];
for (const t of tabs) log(`  ${t.id}  label="${t.label}"  defName=${t.defName ?? '-'}  type=${t.tabWindowType ?? t.type ?? '-'}`);
const ceTab = tabs.find((t) => /character|editor/i.test(`${t.label} ${t.defName ?? ''} ${t.tabWindowType ?? ''}`));
log(`  → CE 面板: ${ceTab ? ceTab.label : '（没找到，可能需要在设置里开 Show in main tabs）'}`);
record('CE 注册了主面板', Boolean(ceTab), ceTab ? ceTab.id : '未找到');

// ── 4. 打开它，看 UI 结构 ────────────────────────────────────
log('\n═══ 3. 打开 Character Editor ═══');
let opened = false;
if (ceTab) {
  try {
    const r = await call('rimworld/open_main_tab', { mainTabId: ceTab.id });
    log(`  open_main_tab: ${JSON.stringify(r).slice(0, 200)}`);
    opened = true;
  } catch (e) {
    log(`  ✗ ${e.message}`);
  }
} else {
  // 退路：CE 也可能注册了一个窗口类型
  for (const t of ['CharacterEditor.Dialog_Editor', 'CharacterEditor.MainTabWindow_Editor', 'Dialog_Editor']) {
    try {
      await call('rimworld/open_window_by_type', { typeName: t });
      log(`  open_window_by_type(${t}) 成功`);
      opened = true;
      break;
    } catch {
      /* 继续试 */
    }
  }
}
await new Promise((r) => setTimeout(r, 800));

if (opened) {
  const snap = await layout(client);
  log('\n  界面结构:');
  for (const s of describeSurfaces(snap)) {
    log(`    ${s.id}  type=${s.type}  kind=${s.kind}  元素 ${s.elements} / 可点 ${s.actionable}`);
  }
  const all = elements(snap);
  const clickable = all.filter(({ element }) => element.actionable);
  const withLabel = all.filter(({ element }) => element.label);
  log(`\n  可点控件 ${clickable.length} 个，带文字的 ${withLabel.length} 个`);
  const labels = [...new Set(withLabel.map(({ element }) => element.label))].map((t) => String(t).replace(/<[^>]+>/g, ''));
  log(`  可见文字（前 60 条）:`);
  for (const t of labels.slice(0, 60)) log(`    | ${t}`);
  record('能读取 CE 的 UI 结构', all.length > 0, `${all.length} 个元素`);
  record('CE 有可点控件', clickable.length > 0, `${clickable.length} 个`);
  await shot('opened');

  // 试着点一个带文字的控件（挑"技能"或"特性"这类）
  const target = clickable.find(({ element }) => /skill|trait|health|bio|needs/i.test(String(element.label ?? '')));
  if (target) {
    log(`\n═══ 4. 试点击「${target.element.label}」(${target.element.targetId}) ═══`);
    try {
      const r = await call('rimworld/click_ui_target', { targetId: target.element.targetId, timeoutMs: 5000 });
      log(`  ${JSON.stringify(r).slice(0, 250)}`);
      await new Promise((x) => setTimeout(x, 600));
      const snap2 = await layout(client);
      log('  点击后界面:');
      for (const s of describeSurfaces(snap2)) log(`    ${s.id}  元素 ${s.elements} / 可点 ${s.actionable}`);
      await shot('after_click');
      record('能点击 CE 的控件', true);
    } catch (e) {
      record('能点击 CE 的控件', false, e.message);
    }
  }

  // 存一份完整布局供后续分析
  fs.writeFileSync(path.join(outDir, 'ui-layout.json'), JSON.stringify(snap, null, 2), 'utf8');
  log(`\n  完整布局已存: ${path.join(outDir, 'ui-layout.json')}`);
}

// ── 5. 顺带看 CE 的 mod 设置能不能读 ─────────────────────────
log('\n═══ 5. CE 的 mod 设置 ═══');
try {
  const s = await call('rimworld/list_mod_settings_surfaces');
  const list = s.surfaces ?? s.settings ?? [];
  log(`  有设置界面的 mod ${list.length} 个`);
  const ceS = list.find((x) => /character/i.test(JSON.stringify(x)));
  log(`  CE 的: ${ceS ? JSON.stringify(ceS).slice(0, 300) : '（没找到）'}`);
  record('能读到 CE 的 mod 设置', Boolean(ceS));
} catch (e) {
  record('能读到 CE 的 mod 设置', false, e.message);
}

// 收尾：关掉打开的面板
await call('rimworld/close_main_tab', {}).catch(() => {});
await call('rimworld/close_window', {}).catch(() => {});

client.close();
const passed = results.filter((r) => r.ok).length;
log(`\n${'═'.repeat(62)}`);
log(`CE 探索：${passed}/${results.length} 通过`);
for (const r of results.filter((x) => !x.ok)) log(`  ✗ ${r.name} — ${r.detail}`);
log('═'.repeat(62));
fs.writeFileSync(path.join(outDir, 'result.json'), JSON.stringify({ stamp, results }, null, 2), 'utf8');
log(`报告: ${path.join(outDir, 'result.json')}`);
