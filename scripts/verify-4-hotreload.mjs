#!/usr/bin/env node
/**
 * 阶段 4 验证：Hot reload Defs 热重载。
 *
 * 这是对 mod 开发价值最大的一条 —— 如果能不重启游戏就重载 XML，
 * 改 Def 的迭代速度会快一个数量级。
 *
 * 验证方法（端到端，不看 success，只看实际效果）：
 *   1. 确认测试 mod 已加载，Def DSH_AutoTestThing 存在
 *   2. 生成一个，读它的 label（应为 v1）
 *   3. 直接改磁盘上的 XML，把 label 改成 v2
 *   4. 执行 Actions\Hot reload Defs
 *   5. 再生成一个，读 label —— 如果是 v2，说明热重载真的生效
 */
import fs from 'node:fs';
import path from 'node:path';
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor, WS, gameRoot } from '../src/launcher.js';

const stamp = new Date().toISOString().replace(/[:.]/g, '-');
const outDir = path.join(WS, 'runs', `verify-hotreload-${stamp}`);
fs.mkdirSync(outDir, { recursive: true });

// 测试 mod：工作区里是真正的源，游戏目录里那个通常是指向它的目录联接（junction）。
// 这样改工作区里的文件，游戏就能看到，不需要工作区外写权限。
const SRC_MOD = path.join(WS, 'profiles', 'vanilla', 'Mods', 'DSH_AutoTest');
const GAME_MOD = path.join(gameRoot(), 'Mods', 'DSH_AutoTest');
const SRC_XML = path.join(SRC_MOD, 'Defs', 'ThingDefs.xml');
const GAME_XML = path.join(GAME_MOD, 'Defs', 'ThingDefs.xml');

const info = readBridgeInfo(logPathFor('vanilla'));
const { client } = await connectGabp(info, { requestTimeoutMs: 25_000 });
const call = (n, a, o) => client.callTool(n, a, o);
const log = (...a) => console.log(...a);
const results = [];
const record = (name, ok, detail) => {
  results.push({ name, ok, detail: detail ?? null });
  log(`  ${ok ? '✓' : '✗'} ${name}${detail && !ok ? ` — ${detail}` : ''}`);
};

/** 生成一个测试物，回读它在地图上的 label。会挨个试候选格，避免格子被占。 */
async function spawnAndReadLabel(baseX, baseZ) {
  const candidates = [
    [baseX, baseZ],
    [baseX + 2, baseZ],
    [baseX, baseZ + 2],
    [baseX + 2, baseZ + 2],
    [baseX + 4, baseZ + 4],
  ];
  const tried = [];
  for (const [x, z] of candidates) {
    try {
      await call(
        'rimworld/execute_debug_action',
        { path: 'Actions\\Spawn thing...\\DSH_AutoTestThing', x, z },
        { timeoutMs: 20_000 },
      );
    } catch (e) {
      tried.push(`(${x},${z}) 执行失败: ${e.message}`);
      continue;
    }
    await call('rimbridge/wait_for_long_event_idle', { timeoutMs: 15_000 }).catch(() => {});
    const cell = await call('rimworld/get_cell_info', { x, z });
    const things = cell.cell?.things ?? [];
    const mine = things.filter((t) => /DSH_AutoTestThing/i.test(t.defName ?? ''));
    if (mine.length) {
      return {
        ok: true,
        at: [x, z],
        label: mine[0].label,
        things: things.map((t) => `${t.defName}=${t.label}`),
      };
    }
    tried.push(`(${x},${z}) 只有 ${things.map((t) => t.label).join(', ') || '空'}`);
  }
  return { ok: false, tried };
}

const st = (await call('rimbridge/get_bridge_status')).state;
if (!st.hasCurrentGame) {
  log('开调试殖民地…');
  await call('rimworld/start_debug_game_ready', { readiness: 'mapData', pauseIfNeeded: true, timeoutMs: 300_000 }, { timeoutMs: 330_000 });
  await call('rimbridge/wait_for_long_event_idle', { timeoutMs: 120_000 });
}
await call('rimworld/set_god_mode', { enabled: true });
const cols = (await call('rimworld/list_colonists', { currentMapOnly: true })).colonists ?? [];
const c0 = cols[0];
log(`殖民者 ${c0.fullName} @(${c0.position.x},${c0.position.z})\n`);

// ── 1. 测试 mod 是否加载 ─────────────────────────────────────
log('═══ 1. 测试 mod 是否被加载 ═══');
const mods = await call('rimworld/list_mods', { compatibleOnly: false });
const mine = (mods.mods ?? []).find((m) => /dsh\.autotest/i.test(m.packageId ?? ''));
if (mine) {
  log(`  找到: ${mine.name}  folder=${mine.folderName}  source=${mine.source}`);
  log(`  enabled=${mine.enabled} loadedInSession=${mine.loadedInSession} loadOrder=${mine.activeLoadOrder}`);
  log(`  rootDir=${mine.rootDir}`);
} else {
  log(`  ✗ 列表里没有 dsh.autotest（activeCount=${mods.activeCount}）`);
}
record('测试 mod 已被游戏识别', Boolean(mine));
record('测试 mod 处于启用状态', mine?.enabled === true && mine?.loadedInSession === true, `enabled=${mine?.enabled} loaded=${mine?.loadedInSession}`);
log(`  游戏目录里的 mod 文件:`);
log(`    ${GAME_XML}  存在=${fs.existsSync(GAME_XML)}`);

// ── 2. 热重载前的基线 ────────────────────────────────────────
log('\n═══ 2. 热重载前：生成一个并读 label ═══');
const before = await spawnAndReadLabel(c0.position.x + 4, c0.position.z + 4);
log(`  结果: ${JSON.stringify(before).slice(0, 300)}`);
record('能生成测试 Def 的物体', before.ok, JSON.stringify(before.tried ?? before.things));
record('初始 label 是 v1', /v1/.test(before.label ?? ''), `实际 label=${before.label}`);

// ── 3. 改磁盘上的 XML ────────────────────────────────────────
log('\n═══ 3. 改磁盘 XML：v1 → v2 ═══');
// 优先改工作区里的那份（游戏目录通过 junction 指向它，所以游戏也会看到）
const xmlPath = fs.existsSync(SRC_XML) ? SRC_XML : GAME_XML;
let xml = fs.readFileSync(xmlPath, 'utf8');
const hadV1 = /dsh autotest thing v1/.test(xml);
xml = xml.replace(/dsh autotest thing v1/, 'dsh autotest thing v2');
fs.writeFileSync(xmlPath, xml, 'utf8');
log(`  已改: ${xmlPath}`);
log(`  原来含 v1: ${hadV1}，现在含 v2: ${/dsh autotest thing v2/.test(fs.readFileSync(xmlPath, 'utf8'))}`);
log(`  游戏目录里的文件也变了: ${/dsh autotest thing v2/.test(fs.readFileSync(GAME_XML, 'utf8'))}`);
record('磁盘 XML 已改动', hadV1);

// ── 4. 执行热重载 ────────────────────────────────────────────
log('\n═══ 4. 执行 Actions\\Hot reload Defs ═══');
const t0 = Date.now();
try {
  const r = await call('rimworld/execute_debug_action', { path: 'Actions\\Hot reload Defs' }, { timeoutMs: 120_000 });
  log(`  success=${r.success} 耗时 ${((Date.now() - t0) / 1000).toFixed(1)}s`);
  log(`  ${JSON.stringify(r).slice(0, 400)}`);
  record('Hot reload Defs 执行成功', r.success === true, r.message);
} catch (e) {
  log(`  ✗ ${e.message}`);
  record('Hot reload Defs 执行成功', false, e.message);
}
await call('rimworld/close_window', {}).catch(() => {});
await new Promise((r) => setTimeout(r, 1500));

// ── 5. 重载后再生成一个，看 label ────────────────────────────
log('\n═══ 5. 重载后：再生成一个并读 label ═══');
const after = await spawnAndReadLabel(c0.position.x + 5, c0.position.z + 5);
log(`  结果: ${JSON.stringify(after).slice(0, 300)}`);
record('重载后能生成测试物体', after.ok, JSON.stringify(after.tried ?? after.things));
record(
  '★ 热重载生效：新生成的 label 变成 v2',
  /v2/.test(after.label ?? ''),
  `实际 label=${after.label}（若仍是 v1 说明热重载没生效）`,
);

// ── 6. 对照：不改 XML 直接再执行一次热重载，确认幂等 ──────────
log('\n═══ 6. 再执行一次热重载（确认幂等、不崩） ═══');
try {
  const r2 = await call('rimworld/execute_debug_action', { path: 'Actions\\Hot reload Defs' }, { timeoutMs: 120_000 });
  log(`  success=${r2.success}`);
  record('热重载可重复执行', r2.success === true);
} catch (e) {
  record('热重载可重复执行', false, e.message);
}

// ── 7. 相关工具：Loaded Files For Mod ────────────────────────
log('\n═══ 7. 顺带验证 Loaded Files For Mod ═══');
try {
  const r = await call('rimworld/execute_debug_action', { path: 'Actions\\Loaded Files For Mod' }, { timeoutMs: 30_000 });
  log(`  success=${r.success}`);
  await new Promise((x) => setTimeout(x, 500));
  const lay = await call('rimworld/get_ui_layout');
  const texts = (lay.surfaces ?? []).flatMap((s) => (s.elements ?? []).filter((e) => e.label).map((e) => e.label));
  const uniq = [...new Set(texts)].map((t) => String(t).replace(/<[^>]+>/g, ''));
  log(`  界面取到 ${uniq.length} 行: ${uniq.slice(0, 15).join(' | ')}`);
  const dshLine = uniq.find((t) => /autotest|DSH/i.test(t));
  log(`  含 DSH 的行: ${dshLine ?? '（无）'}`);
  await call('rimworld/close_window', {}).catch(() => {});
  record('Loaded Files For Mod 能通过窗口读出来', uniq.length > 0, `${uniq.length} 行`);
} catch (e) {
  record('Loaded Files For Mod', false, e.message);
}

// ── 8. 截图存证 ──────────────────────────────────────────────
await call('rimworld/frame_cell_rect', { x: c0.position.x + 4, z: c0.position.z + 4, width: 12, height: 12, marginCells: 6 });
const shot = await call('rimworld/take_screenshot', { suppressMessage: true, fileName: 'verify_hotreload' });
if (shot?.path && fs.existsSync(shot.path)) fs.copyFileSync(shot.path, path.join(outDir, path.basename(shot.path)));

client.close();

const passed = results.filter((r) => r.ok).length;
log(`\n${'═'.repeat(62)}`);
log(`阶段 4：${passed}/${results.length} 通过`);
for (const r of results.filter((x) => !x.ok)) log(`  ✗ ${r.name} — ${r.detail}`);
log('═'.repeat(62));
fs.writeFileSync(
  path.join(outDir, 'result.json'),
  JSON.stringify({ stamp, stage: 'hotreload', results, before, after, xmlPath }, null, 2),
  'utf8',
);
log(`报告: ${path.join(outDir, 'result.json')}`);
