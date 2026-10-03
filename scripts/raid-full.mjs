#!/usr/bin/env node
/**
 * 完整跑通「空投袭击」debug action：势力 → 点数 → 地图落点 → 半径（可能还有更多层）。
 *
 * 教训：不要假设对话框有几层、什么顺序。写成一个贪心循环 ——
 *   有弹窗就按候选池挑一个能匹配上的选项点掉；
 *   没弹窗就补一次地图点击；
 *   直到「点了地图之后也不再弹」为止。
 */
import path from 'node:path';
import fs from 'node:fs';
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor, WS } from '../src/launcher.js';
import { clickRowByLabel, describeSurfaces, layout } from '../src/ui.js';

const stamp = new Date().toISOString().replace(/[:.]/g, '-');
const outDir = path.join(WS, 'runs', `raid-${stamp}`);
fs.mkdirSync(outDir, { recursive: true });

const DLG = 'Dialog_DebugOptionListLister';
const DROP = { x: 134, z: 134 };

const info = readBridgeInfo(logPathFor('vanilla'));
const { client } = await connectGabp(info, { requestTimeoutMs: 900_000 });
const call = (n, a, o) => client.callTool(n, a, o);
const log = (...a) => console.log(...a);
let shotN = 0;
async function shot(label) {
  shotN += 1;
  const r = await call('rimworld/take_screenshot', {
    suppressMessage: true,
    fileName: `raid_${String(shotN).padStart(2, '0')}_${label}`,
  });
  if (r?.path && fs.existsSync(r.path)) fs.copyFileSync(r.path, path.join(outDir, path.basename(r.path)));
  return r?.path;
}

/** 当前是否还有 debug 选择框 */
async function dialogOptions() {
  const snap = await layout(client);
  const surfaces = snap.surfaces.filter((s) => String(s.type ?? '').includes(DLG));
  if (!surfaces.length) return null;
  const labels = [...new Set(surfaces.flatMap((s) => (s.elements ?? []).filter((e) => e.label).map((e) => e.label)))];
  return { snap, labels };
}

// 候选池：按优先级排列，哪层弹出就用池里第一个能匹配上的
const pool = [
  { text: 'Pirate', exact: false, note: '海盗势力' },
  { text: '800 points', exact: true, note: '袭击点数' },
  { text: '2 tile radius', exact: true, note: '投放半径' },
];

log('▶ 触发 debug action：Drop pod raid at location...');
await call('rimworld/execute_debug_action', { path: 'Actions\\Drop pod raid at location...', x: DROP.x, z: DROP.z });
await new Promise((r) => setTimeout(r, 700));

let mapClicks = 0;
for (let round = 1; round <= 12; round++) {
  const dlg = await dialogOptions();

  if (!dlg) {
    if (mapClicks >= 2) {
      log(`\n✓ 第 ${round} 轮：已无弹窗，且地图点击后不再弹 —— 流程结束`);
      break;
    }
    log(`\n▶ 无弹窗，向地图 (${DROP.x},${DROP.z}) 发落点点击（第 ${mapClicks + 1} 次）`);
    await call('rimworld/click_cell', { x: DROP.x, z: DROP.z, button: 'left' });
    mapClicks += 1;
    await new Promise((r) => setTimeout(r, 900));
    continue;
  }

  log(`\n▶ 第 ${round} 轮：弹窗有 ${dlg.labels.length} 个选项（${dlg.labels.slice(0, 3).join(' / ')} …）`);

  // 池里第一个能匹配上的
  let chosen = null;
  for (const cand of pool) {
    const hit = dlg.labels.some((l) => (cand.exact ? l === cand.text : l.includes(cand.text)));
    if (hit) {
      chosen = cand;
      break;
    }
  }
  if (!chosen) {
    log(`  ✗ 候选池都匹配不上。全部选项：`);
    for (const l of dlg.labels.slice(0, 40)) log(`      ${l}`);
    break;
  }

  const r = await clickRowByLabel(client, chosen.text, { surfaceType: DLG, exact: chosen.exact });
  if (!r.clicked) {
    log(`  ✗ 点不到「${chosen.text}」`);
    break;
  }
  log(`  ✓ ${chosen.note}：${r.matchedLabel}`);
  await shot(`dialog_${round}`);
  await new Promise((x) => setTimeout(x, 800));
}

// ── 推进时间看结果 ────────────────────────────────────────────
log('\n▶ 极速推进 20 秒游戏时间');
await call('rimworld/set_time_speed', { speed: 'Ultrafast', ultraSpeedBoost: true });
await call('rimworld/play_for', { durationMs: 20_000, speed: 'Ultrafast' }, { timeoutMs: 300_000 });

const letters = await call('rimworld/list_letters', { limit: 25 });
log('\n▶ 信件栈：');
for (const l of (letters.letters ?? []).slice(0, 12)) {
  log(`  《${l.label}》 ${String(l.text ?? '').replace(/<[^>]+>/g, '').replace(/\s+/g, ' ').slice(0, 100)}`);
}

const alerts = await call('rimworld/list_alerts', { limit: 25 });
log('\n▶ 告警：');
for (const a of (alerts.alerts ?? []).slice(0, 12)) log(`  ⚠ ${a.label}`);

const logs = await call('rimbridge/list_logs', { minimumLevel: 'warning', limit: 200 });
log(`\n▶ 新增日志 ${(logs.logs ?? []).length} 条`);
for (const e of (logs.logs ?? []).slice(0, 10)) {
  log(`  [${e.level}] ${String(e.message).replace(/\s+/g, ' ').slice(0, 140)}`);
}

// 检查落点周边有没有敌对单位
const area = await call('rimworld/get_cells_info', {
  x: DROP.x - 16,
  z: DROP.z - 16,
  width: 32,
  height: 32,
});
const kinds = [...new Set((area.cells ?? []).flatMap((c) => (c.things ?? []).map((t) => t.defName)))];
log(`\n▶ 落点 32x32 区域内的物体（${kinds.length} 种）：`);
log(`  ${kinds.slice(0, 40).join(', ')}`);

await call('rimworld/frame_cell_rect', { x: DROP.x, z: DROP.z, width: 40, height: 40, marginCells: 14 });
const final = await shot('final');

const cols = (await call('rimworld/list_colonists', { currentMapOnly: true })).colonists ?? [];
log('\n▶ 殖民者：');
for (const c of cols) {
  log(`  ${c.fullName.padEnd(22)} @(${c.position?.x},${c.position?.z}) ${c.job ?? '-'} ${c.downed ? '【倒地】' : ''}${c.dead ? '【死亡】' : ''}`);
}

log(`\n截图目录：${outDir}`);
log(`最终截图：${final}`);
client.close();
