#!/usr/bin/env node
/** 排查：CE 窗口在 UI 布局里存在，但截图里看不到？ */
import fs from 'node:fs';
import path from 'node:path';
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor, WS } from '../src/launcher.js';

const info = readBridgeInfo(logPathFor('vanilla'));
const { client } = await connectGabp(info, { requestTimeoutMs: 25_000 });
const call = (n, a, o) => client.callTool(n, a, o);
const log = (...a) => console.log(...a);

log('=== 当前界面 ===');
const snap = await call('rimworld/get_ui_layout');
for (const s of snap.surfaces ?? []) {
  log(`  ${s.surfaceTargetId}`);
  log(`     type=${s.type}  kind=${s.surfaceKind}  元素=${s.elementCount} 可点=${s.actionableElementCount}`);
  log(`     rect=${JSON.stringify(s.rect)}  screenRect=${JSON.stringify(s.screenRect)}`);
}

log('\n=== 主面板状态 ===');
const tabs = await call('rimworld/list_main_tabs');
log(`  openMainTabId=${tabs.openMainTabId ?? '(无)'}  label=${tabs.openMainTabLabel ?? ''}  type=${tabs.openMainTabType ?? ''}`);
const ce = (tabs.tabs ?? []).find((t) => /character/i.test(t.label ?? ''));
log(`  CE 面板: isOpen=${ce?.isOpen} minimized=${ce?.minimized} rect=${JSON.stringify(ce?.rect)}`);

log('\n=== 屏幕目标（含各窗口坐标） ===');
const st = await call('rimworld/get_screen_targets');
for (const w of st.targets?.windows ?? []) {
  log(`  ${w.windowTargetId}  type=${w.type}  layer=${w.layer}`);
  log(`     rect=${JSON.stringify(w.rect)} getsInput=${w.getsInput}`);
}

log('\n=== 截图（不带 suppressMessage，看游戏有没有提示） ===');
const r = await call('rimworld/take_screenshot', { suppressMessage: true, fileName: 'ce_probe2' });
log(`  path=${r.path}`);
log(`  sizeBytes=${r.sizeBytes} clipped=${r.clipped}`);
if (r.screenTargets) {
  log(`  截图时的 uiState: windowCount=${r.screenTargets.uiState?.windowCount} top=${r.screenTargets.uiState?.topWindowType}`);
}

client.close();
