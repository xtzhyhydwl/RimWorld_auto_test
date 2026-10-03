#!/usr/bin/env node
/**
 * 阶段 10：在鼠族（HAR 种族）身上验证身体/断肢工具。
 *
 * 关键问题：HAR 种族有自己的 body def（不是原版 Human），
 * 我们按 pawn.RaceProps.body 动态读部位，理论上通用 —— 但要实测。
 */
import fs from 'node:fs';
import path from 'node:path';
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor, WS } from '../src/launcher.js';

const stamp = new Date().toISOString().replace(/[:.]/g, '-');
const outDir = path.join(WS, 'runs', `ratkin-body-${stamp}`);
fs.mkdirSync(outDir, { recursive: true });

const info = readBridgeInfo(logPathFor('vanilla'));
const { client } = await connectGabp(info, { requestTimeoutMs: 30_000 });
const call = (n, a, o) => client.callTool(n, a, o);
const log = (...a) => console.log(...a);
const results = [];
const record = (name, ok, detail) => {
  results.push({ name, ok, detail: detail ?? null });
  log(`  ${ok ? '✓' : '✗'} ${name}${detail && !ok ? ` — ${detail}` : ''}`);
};

const st = (await call('rimbridge/get_bridge_status')).state;
if (st.inEntryScene) {
  await call('rimworld/start_debug_game_ready', { readiness: 'mapData', pauseIfNeeded: true, timeoutMs: 300_000 }, { timeoutMs: 330_000 });
}
await call('rimbridge/wait_for_game_loaded', { readiness: 'currentMap', pauseIfNeeded: true, timeoutMs: 180_000 }, { timeoutMs: 210_000 });
await call('rimworld/pause_game', { pause: true });

// ── 1. 找一只鼠族 ────────────────────────────────────────────
log('═══ 1. 找鼠族 ═══');
// 先看是不是已经在殖民者列表里
const ceCols = await call('ce/colonists');
log(`  ce/colonists（玩家殖民者）: ${(ceCols.colonists ?? []).map((c) => c.name).join(', ')}`);

// 在地图上找 Ratkin
const mapInfo = await call('rimworld/get_camera_state').catch(() => null);
let ratkinCell = null;
const cols = (await call('rimworld/list_colonists', { currentMapOnly: true })).colonists ?? [];
const base = cols[0];
if (base) {
  const area = await call('rimworld/get_cells_info', {
    x: base.position.x - 12,
    z: base.position.z - 12,
    width: 32,
    height: 32,
  });
  for (const c of area.cells ?? []) {
    if ((c.things ?? []).some((t) => /^Ratkin$/i.test(t.defName ?? ''))) {
      ratkinCell = { x: c.x, z: c.z };
      break;
    }
  }
}
log(`  鼠族所在格: ${JSON.stringify(ratkinCell)}`);

if (!ratkinCell) {
  // 现场生成一只
  log('  没找到，现场生成一只 RatkinColonist…');
  await call('rimworld/execute_debug_action', { path: 'Actions\\Spawn Pawn...\\RatkinColonist', x: base.position.x + 4, z: base.position.z + 4 }, { timeoutMs: 25_000 });
  await call('rimbridge/wait_for_long_event_idle', { timeoutMs: 20_000 }).catch(() => {});
  const area = await call('rimworld/get_cells_info', { x: base.position.x - 2, z: base.position.z - 2, width: 12, height: 12 });
  for (const c of area.cells ?? []) {
    if ((c.things ?? []).some((t) => /^Ratkin$/i.test(t.defName ?? ''))) {
      ratkinCell = { x: c.x, z: c.z };
      break;
    }
  }
  log(`  生成后位置: ${JSON.stringify(ratkinCell)}`);
}

if (!ratkinCell) throw new Error('地图上找不到鼠族');
record('地图上找到鼠族', true, `(${ratkinCell.x},${ratkinCell.z})`);

// ── 2. 选中它拿到 pawnId ─────────────────────────────────────
log('\n═══ 2. 选中鼠族拿 pawnId ═══');
await call('rimworld/click_cell', { x: ratkinCell.x, z: ratkinCell.z, button: 'left' });
await new Promise((r) => setTimeout(r, 500));
const sem = await call('rimworld/get_selection_semantics');
const sel = (sem.selectedObjects ?? [])[0];
log(`  选中: ${sel?.label}  id=${sel?.id}  type=${sel?.type}`);
if (!sel?.id) throw new Error('没能选中鼠族');
const ratkinId = sel.id;
record('选中鼠族并拿到 pawnId', Boolean(ratkinId), ratkinId);

// ── 3. ce/body 读 HAR 种族的身体 ─────────────────────────────
log('\n═══ 3. ce/body 读鼠族的身体结构 ═══');
let body0;
try {
  body0 = await call('ce/body', { pawnId: ratkinId });
  log(`  success=${body0.success}  bodyDef=${body0.bodyDef}  部位总数=${body0.partCount}`);
  log(`  缺失=${body0.missingCount} ${JSON.stringify(body0.missingParts)}`);
  const legs = (body0.parts ?? []).filter((p) => /leg|foot|toe|tail|ear/i.test(`${p.label} ${p.defName}`));
  log(`  腿/脚/尾/耳相关 ${legs.length} 个:`);
  for (const p of legs.slice(0, 18)) log(`    ${p.label} [${p.defName}] parent=${p.parent} missing=${p.missing}`);
  record('ce/body 能读鼠族身体', body0.success === true && body0.partCount > 0, JSON.stringify(body0).slice(0, 200));
  record('鼠族的 bodyDef 不是原版 Human（说明 HAR 生效）', body0.bodyDef !== 'Human', `bodyDef=${body0.bodyDef}`);
} catch (e) {
  record('ce/body 读鼠族身体', false, e.message);
}

// ── 4. 在鼠族身上断肢 ────────────────────────────────────────
log('\n═══ 4. 在鼠族身上截掉左腿 ═══');
try {
  const cut = await call('ce/amputate', { part: 'left leg', pawnId: ratkinId });
  log(`  success=${cut.success}  匹配=${JSON.stringify(cut.matched)}`);
  log(`  目标: ${JSON.stringify(cut.part)}`);
  const body1 = await call('ce/body', { pawnId: ratkinId });
  log(`  现在缺失 ${body1.missingCount} 个: ${JSON.stringify(body1.missingParts)}`);
  record('鼠族截肢成功', cut.success === true, JSON.stringify(cut).slice(0, 200));
  record('鼠族缺失部位增加', body1.missingCount > body0.missingCount, `${body0.missingCount} → ${body1.missingCount}`);

  log('\n═══ 5. 恢复 ═══');
  const rest = await call('ce/restore', { part: 'left leg', pawnId: ratkinId });
  log(`  success=${rest.success}  清掉 ${rest.removedCount} 条`);
  const body2 = await call('ce/body', { pawnId: ratkinId });
  log(`  现在缺失 ${body2.missingCount} 个`);
  record('鼠族恢复成功', body2.missingCount === 0, `还剩 ${body2.missingCount}`);
} catch (e) {
  record('鼠族截肢/恢复', false, e.message);
}

// ── 6. 对照：原版人类 ────────────────────────────────────────
log('\n═══ 6. 对照：原版人类 ═══');
const humanBody = await call('ce/body', { pawnId: base.pawnId });
log(`  人类 bodyDef=${humanBody.bodyDef} 部位数=${humanBody.partCount}`);
log(`  鼠族 bodyDef=${body0?.bodyDef} 部位数=${body0?.partCount}`);

// ── 7. 日志 ──────────────────────────────────────────────────
const logs = await call('rimbridge/list_logs', { minimumLevel: 'warning', limit: 100 });
log(`\n日志 warning+ : ${(logs.logs ?? []).length} 条`);
for (const e of (logs.logs ?? []).slice(0, 10)) log(`  [${e.level}] ${String(e.message).slice(0, 140)}`);
record('过程中无新增错误', (logs.logs ?? []).filter((e) => /error|fatal/i.test(e.level)).length === 0);

client.close();
fs.writeFileSync(path.join(outDir, 'result.json'), JSON.stringify({ stamp, results, body0, ratkinId }, null, 2), 'utf8');
const passed = results.filter((r) => r.ok).length;
log(`\n${'═'.repeat(62)}`);
log(`阶段 10（鼠族身体编辑）：${passed}/${results.length} 通过`);
for (const r of results.filter((x) => !x.ok)) log(`  ✗ ${r.name} — ${r.detail}`);
log('═'.repeat(62));
log(`证据: ${outDir}`);
