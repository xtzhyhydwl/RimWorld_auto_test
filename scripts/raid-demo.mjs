#!/usr/bin/env node
/**
 * 两步 debug action 演示：在真实对话框里选势力，然后在地图上点位置，把袭击叫出来。
 *
 * 这演示的是「像真人一样点原生 UI」而不是绕过 UI 直接改数据：
 *   execute_debug_action 打开 Dialog_DebugOptionListLister
 *   → 按文字找到海盗势力那一行 → 点它
 *   → 工具进入地图点击模式 → 点地图格
 *   → 极速推进，观察信件与告警
 */
import path from 'node:path';
import fs from 'node:fs';
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor, WS } from '../src/launcher.js';
import { clickRowByLabel, describeSurfaces, layout, closeTopWindow } from '../src/ui.js';

const stamp = new Date().toISOString().replace(/[:.]/g, '-');
const outDir = path.join(WS, 'runs', `raid-${stamp}`);
fs.mkdirSync(outDir, { recursive: true });

const info = readBridgeInfo(logPathFor('vanilla'));
if (!info) throw new Error('找不到桥接，先启动游戏。');
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

// 先把上一次演示留下的对话框和面板收干净
log('▶ 清理残留窗口');
try {
  await closeTopWindow(client);
  await closeTopWindow(client);
} catch {}
await call('rimworld/close_main_tab', {});
await call('rimworld/pause_game', { pause: true });

// ── 1. 打开 debug action ────────────────────────────────────────
log('\n▶ 执行 debug action：Drop pod raid at location...');
await call('rimworld/execute_debug_action', {
  path: 'Actions\\Drop pod raid at location...',
  x: 132,
  z: 132,
});

const snap0 = await layout(client);
log('  当前界面：');
for (const s of describeSurfaces(snap0)) {
  log(`    ${s.id}  ${s.type ?? s.kind}  元素 ${s.elements} / 可点 ${s.actionable}`);
}
await shot('faction_dialog');

// ── 2. 在对话框里选一个海盗势力 ────────────────────────────────
const target = process.argv[2] || 'Pirate';
log(`\n▶ 在对话框里找含「${target}」的行并点击`);
const clicked = await clickRowByLabel(client, target, {
  surfaceType: 'Dialog_DebugOptionListLister',
});
if (!clicked.clicked) {
  log(`  ✗ 没点到。候选：`);
  for (const c of clicked.candidates ?? []) log(`      ${c}`);
  throw new Error('没能选中势力');
}
log(`  ✓ 已点击「${clicked.matchedLabel}」（targetId=${clicked.targetId}）`);

await new Promise((r) => setTimeout(r, 800));
const snap1 = await layout(client);
log('  点击后界面：');
for (const s of describeSurfaces(snap1)) {
  log(`    ${s.id}  ${s.type ?? s.kind}  元素 ${s.elements} / 可点 ${s.actionable}`);
}

// ── 3. 在地图上点落点 ──────────────────────────────────────────
log('\n▶ 在地图 (134,134) 处点击，投放袭击');
const click = await call('rimworld/click_cell', { x: 134, z: 134, button: 'left' });
log(`  ${JSON.stringify(click).slice(0, 220)}`);
await new Promise((r) => setTimeout(r, 800));

// ── 4. 推时间看结果 ────────────────────────────────────────────
log('\n▶ 以极速推进 30 秒游戏时间');
await call('rimworld/set_time_speed', { speed: 'Ultrafast', ultraSpeedBoost: true });
await call('rimworld/play_for', { durationMs: 30_000, speed: 'Ultrafast' }, { timeoutMs: 300_000 });

const letters = await call('rimworld/list_letters', { limit: 20 });
log('\n▶ 信件栈：');
for (const l of (letters.letters ?? []).slice(0, 10)) {
  log(`  《${l.label}》 ${String(l.text ?? '').replace(/<[^>]+>/g, '').replace(/\s+/g, ' ').slice(0, 90)}`);
}

const alerts = await call('rimworld/list_alerts', { limit: 20 });
log('\n▶ 活动告警：');
for (const a of (alerts.alerts ?? []).slice(0, 10)) log(`  ⚠ ${a.label}`);

const logs = await call('rimbridge/list_logs', { minimumLevel: 'warning', limit: 200 });
const entries = logs.logs ?? [];
log(`\n▶ 新增日志 ${entries.length} 条（错误 ${entries.filter((e) => /error|fatal/i.test(e.level)).length}）`);
for (const e of entries.slice(0, 15)) {
  log(`  [${e.level}] ${String(e.message).replace(/\s+/g, ' ').slice(0, 140)}`);
}

// ── 5. 截图 ────────────────────────────────────────────────────
await call('rimworld/frame_cell_rect', { x: 134, z: 134, width: 30, height: 30, marginCells: 10 });
const finalShot = await shot('after_raid');

const colonists = (await call('rimworld/list_colonists', { currentMapOnly: true })).colonists ?? [];
log('\n▶ 殖民者：');
for (const c of colonists) {
  log(`  ${c.fullName.padEnd(22)} @(${c.position?.x},${c.position?.z}) ${c.job ?? '-'} ${c.downed ? '【倒地】' : ''}${c.dead ? '【死亡】' : ''}`);
}

const pawns = await call('rimworld/list_colonists', { currentMapOnly: false });
log(`\n▶ 总结：${letters.letters?.length ?? 0} 封信、${alerts.alerts?.length ?? 0} 条告警、${entries.length} 条新日志`);

// 地图上现在有多少敌对单位
const hostile = await call('rimworld/get_map_target_info', {});
log(`\n截图目录：${outDir}`);
log(`最终截图：${finalShot}`);

client.close();
fs.writeFileSync(
  path.join(outDir, 'raid.json'),
  JSON.stringify({ letters: letters.letters, alerts: alerts.alerts, logs: entries, colonists }, null, 2),
  'utf8',
);
