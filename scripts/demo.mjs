#!/usr/bin/env node
/**
 * 混乱操作演示：在隔离测试殖民地里跑一串真实游戏操作，顺带当成集成测试。
 *
 * 每一步都留截图/证据；单步失败不中断整体，最后汇总。
 * 用法：node scripts/demo.mjs
 */
import path from 'node:path';
import fs from 'node:fs';
import { fileURLToPath } from 'node:url';
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor, WS } from '../src/launcher.js';

const stamp = new Date().toISOString().replace(/[:.]/g, '-');
const outDir = path.join(WS, 'runs', `demo-${stamp}`);
fs.mkdirSync(outDir, { recursive: true });

const info = readBridgeInfo(logPathFor('vanilla'));
if (!info) {
  console.error('找不到桥接信息，先启动游戏。');
  process.exit(1);
}
const { client } = await connectGabp(info, { requestTimeoutMs: 900_000 });
console.log(`已连接 ${info.host}:${info.port}\n`);

const results = [];
let shotSeq = 0;

async function call(name, args = {}, opts) {
  return client.callTool(name, args, opts);
}

/** 跑一步，记录成败，不中断 */
async function step(title, fn) {
  process.stdout.write(`▶ ${title} … `);
  try {
    const value = await fn();
    console.log('✓');
    results.push({ title, ok: true });
    return value;
  } catch (err) {
    console.log(`✗ ${err.message}`);
    results.push({ title, ok: false, error: err.message });
    return null;
  }
}

async function shot(label) {
  shotSeq += 1;
  const r = await call('rimworld/take_screenshot', {
    suppressMessage: true,
    fileName: `demo_${String(shotSeq).padStart(2, '0')}_${label}`,
  });
  if (r?.path && fs.existsSync(r.path)) {
    fs.copyFileSync(r.path, path.join(outDir, path.basename(r.path)));
  }
  return r?.path;
}

// ─────────────────────────────────────────────────────────
console.log('═══ 第一幕：先看看这是什么地方 ═══\n');

const gameInfo = await step('读取游戏信息', () => call('rimworld/get_game_info'));
if (gameInfo) {
  console.log(
    `     ${gameInfo.defName ?? gameInfo.scenario ?? ''} ` +
      `tick=${gameInfo.ticksGame ?? gameInfo.gameTick ?? '?'} ` +
      `地图=${gameInfo.mapSize ?? '?'}`,
  );
}

const cell = await step('看 (125,125) 那格有什么', () =>
  call('rimworld/get_cell_info', { x: 125, z: 125 }),
);
if (cell) {
  const things = (cell.things ?? []).map((t) => t.defName ?? t.label).slice(0, 8);
  console.log(`     地形=${cell.terrain ?? '?'} 物品=[${things.join(', ')}]`);
}

const cam = await step('读相机状态', () => call('rimworld/get_camera_state'));

// ─────────────────────────────────────────────────────────
console.log('\n═══ 第二幕：作弊与空投 ═══\n');

await step('打开上帝模式', async () => {
  const r = await call('rimworld/set_god_mode', { enabled: true });
  return r;
});

const spawned = await step('空投 500 白银 + 一把电荷步枪 + 一头敲击兽', async () => {
  const a = await call('rimworld/spawn_thing', { defName: 'Silver', x: 126, z: 124, stackCount: 500 });
  const b = await call('rimworld/spawn_thing', { defName: 'Gun_ChargeRifle', x: 127, z: 124 });
  const c = await call('rimworld/spawn_thing', { defName: 'Thrumbo', x: 130, z: 130 });
  return { a, b, c };
});

await step('把镜头对准空投点', () =>
  call('rimworld/frame_cell_rect', { x: 126, z: 126, width: 10, height: 10, marginCells: 6 }),
);
await step('截图：空投现场', () => shot('airdrop'));

// ─────────────────────────────────────────────────────────
console.log('\n═══ 第三幕：指挥殖民者 ═══\n');

const colonists = (await call('rimworld/list_colonists', { currentMapOnly: true })).colonists ?? [];
const names = colonists.map((c) => c.name);
console.log(`     在场：${names.join(', ')}`);

const first = colonists[0];
if (first) {
  await step(`选中 ${first.fullName}`, () => call('rimworld/select_pawn', { pawnId: first.pawnId }));
  const sem = await step('读取选中语义', () => call('rimworld/get_selection_semantics'));
  if (sem) {
    const inspect = (sem.inspectStrings ?? []).slice(0, 4);
    console.log(`     ${inspect.join(' | ').replace(/<[^>]+>/g, '')}`);
  }
  await step(`让 ${first.name} 进入征召状态`, () =>
    call('rimworld/set_draft', { pawnId: first.pawnId, drafted: true }),
  );
  await step('截图：选中并征召', () => shot('drafted'));
  await step('取消征召', () => call('rimworld/set_draft', { pawnId: first.pawnId, drafted: false }));
}

// ─────────────────────────────────────────────────────────
console.log('\n═══ 第四幕：打开真实 UI 面板并点它 ═══\n');

const tabs = await step('列出主面板', () => call('rimworld/list_main_tabs'));
if (tabs) {
  const list = tabs.tabs ?? tabs.mainTabs ?? [];
  console.log(`     ${list.map((t) => t.label).join(', ')}`);
}

await step('打开「工作」面板', () => call('rimworld/open_main_tab', { mainTabId: 'work' }));

const layout = await step('抓 UI 布局', () => call('rimworld/get_ui_layout'));
let clickable = [];
if (layout) {
  const surfaces = layout.surfaces ?? layout.uiSurfaces ?? [];
  for (const s of surfaces) {
    for (const el of s.elements ?? []) {
      if (el.actionable && el.id) clickable.push(el);
    }
  }
  console.log(`     ${surfaces.length} 个界面，${clickable.length} 个可点控件`);
  console.log(
    `     例如：${clickable.slice(0, 5).map((e) => `${(e.label ?? '').slice(0, 18)}`).join(' | ')}`,
  );
}
await step('截图：工作面板', () => shot('worktab'));

// ─────────────────────────────────────────────────────────
console.log('\n═══ 第五幕：叫一场袭击来看看 ═══\n');

const raid = await step('执行 debug action：轨道空投袭击', () =>
  call('rimworld/execute_debug_action', {
    path: 'Actions\\Drop pod raid at location...',
    x: 130,
    z: 130,
  }),
);
if (raid) console.log(`     ${raid.message ?? JSON.stringify(raid).slice(0, 160)}`);

await step('以极速推进 25 秒游戏时间', () =>
  call('rimworld/play_for', { durationMs: 25_000, speed: 'Ultrafast' }, { timeoutMs: 300_000 }),
);

const letters = await step('读信件栈', () => call('rimworld/list_letters', { limit: 20 }));
if (letters) {
  for (const l of (letters.letters ?? []).slice(0, 8)) {
    console.log(`     《${l.label}》 — ${String(l.text ?? '').replace(/<[^>]+>/g, '').slice(0, 70)}`);
  }
}

const alerts = await step('读活动告警', () => call('rimworld/list_alerts', { limit: 20 }));
if (alerts) {
  for (const a of (alerts.alerts ?? []).slice(0, 8)) console.log(`     ⚠ ${a.label}`);
}

await step('把镜头对准袭击点', () => call('rimworld/frame_cell_rect', { x: 135, z: 135, width: 20, height: 20, marginCells: 8 }));
await step('截图：袭击现场', () => shot('raid'));

// ─────────────────────────────────────────────────────────
console.log('\n═══ 收尾：状态快照 ═══\n');

const finalLogs = await step('收集警告与错误', () =>
  call('rimbridge/list_logs', { minimumLevel: 'warning', limit: 200 }),
);
if (finalLogs) {
  const entries = finalLogs.logs ?? [];
  const errors = entries.filter((e) => /error|fatal/i.test(e.level));
  console.log(`     共 ${entries.length} 条，其中错误 ${errors.length} 条`);
  for (const e of entries.slice(0, 12)) {
    console.log(`     [${e.level}] ${String(e.message).replace(/\s+/g, ' ').slice(0, 130)}`);
  }
}

const finalColonists = (await call('rimworld/list_colonists', { currentMapOnly: true })).colonists ?? [];
console.log(`\n殖民者现状：`);
for (const c of finalColonists) {
  console.log(
    `  ${c.fullName.padEnd(22)} @(${c.position?.x},${c.position?.z})  ` +
      `${c.job ?? '-'}  ${c.downed ? '【倒地】' : ''}${c.dead ? '【死亡】' : ''}`,
  );
}

await step('截图：最终全景', async () => {
  await call('rimworld/frame_pawns', { pawnNamesCsv: finalColonists.map((c) => c.name).join(',') });
  return shot('final');
});

client.close();

// ─────────────────────────────────────────────────────────
const ok = results.filter((r) => r.ok).length;
console.log(`\n${'═'.repeat(60)}`);
console.log(`共 ${results.length} 步，成功 ${ok}，失败 ${results.length - ok}`);
for (const r of results.filter((x) => !x.ok)) console.log(`  ✗ ${r.title} — ${r.error}`);
console.log(`截图与记录：${outDir}`);
console.log('═'.repeat(60));

fs.writeFileSync(
  path.join(outDir, 'demo.json'),
  JSON.stringify({ startedAt: stamp, results }, null, 2),
  'utf8',
);
