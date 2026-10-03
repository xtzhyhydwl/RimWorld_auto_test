#!/usr/bin/env node
/**
 * 阶段 8 验证：用 CE 自动改小人的**四肢健全程度**（断肢）。
 *
 * 这是"条件贴图替换"类 mod 的前置能力：
 * 如果 agent 能自动把某个部位变成"缺失"，就能自动构造出各种断肢状态去验证贴图。
 *
 * 验证点：
 *   1. 能读出身体部位树与缺失状态
 *   2. 能把指定部位变成缺失（left leg / right arm …）
 *   3. 能恢复
 *   4. **截图对比**看画面上腿是不是真的没了（这才是贴图相关的关键）
 *   5. CE 自己的 HealthTool/BodyTool 到底提供了什么方法
 *   6. CE 的 Health 标签页 UI 里能不能看到变化
 */
import fs from 'node:fs';
import path from 'node:path';
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor, WS } from '../src/launcher.js';

const stamp = new Date().toISOString().replace(/[:.]/g, '-');
const outDir = path.join(WS, 'runs', `verify-limb-${stamp}`);
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

/** 把镜头对准某个小人并截图 */
async function shootPawn(pawnName, label) {
  await call('rimworld/frame_pawns', { pawnNamesCsv: pawnName }).catch(() => {});
  await new Promise((r) => setTimeout(r, 700));
  const r = await call('rimworld/take_screenshot', { suppressMessage: true, fileName: `limb_${label}` });
  if (r?.path && fs.existsSync(r.path)) fs.copyFileSync(r.path, path.join(outDir, path.basename(r.path)));
  return r?.path ?? null;
}

// ── 准备 ─────────────────────────────────────────────────────
const st = (await call('rimbridge/get_bridge_status')).state;
if (!st.hasCurrentGame) {
  log('开调试殖民地…');
  await call('rimworld/start_debug_game_ready', { readiness: 'mapData', pauseIfNeeded: true, timeoutMs: 300_000 }, { timeoutMs: 330_000 });
}
await call('rimbridge/wait_for_game_loaded', { readiness: 'currentMap', pauseIfNeeded: true, timeoutMs: 180_000 }, { timeoutMs: 210_000 });
await call('rimworld/pause_game', { pause: true });

const cols = await call('ce/colonists');
const target = cols.colonists?.[0];
if (!target) throw new Error('没有殖民者');
log(`测试对象: ${target.fullName} (${target.pawnId})\n`);

// ── 1. 读身体基线 ────────────────────────────────────────────
log('═══ 1. 读身体部位树 ═══');
const body0 = await call('ce/body', { pawnId: target.pawnId });
log(`  bodyDef=${body0.bodyDef}  部位总数=${body0.partCount}  缺失=${body0.missingCount}`);
log(`  缺失部位: ${JSON.stringify(body0.missingParts)}`);
const legParts = (body0.parts ?? []).filter((p) => /leg|foot|toe/i.test(`${p.label} ${p.defName}`));
log(`  腿部相关部位:`);
for (const p of legParts) log(`    ${p.label} [${p.defName}] parent=${p.parent} depth=${p.depth} missing=${p.missing}`);
record('能读出身体部位树', body0.success === true && body0.partCount > 0, `部位 ${body0.partCount} 个`);
record('初始没有缺失部位', body0.missingCount === 0, JSON.stringify(body0.missingParts));

// ── 2. CE 自己的 API 能做什么 ────────────────────────────────
log('\n═══ 2. CE 自己的 HealthTool / BodyTool 提供了什么 ═══');
const api = await call('ce/ce_api');
for (const [name, info2] of Object.entries(api.tools ?? {})) {
  if (typeof info2 === 'string') {
    log(`  ${name}: ${info2}`);
    continue;
  }
  log(`  ${name}  (public=${info2.isPublic}, ${info2.methodCount} 个方法)`);
  const interesting = (info2.methods ?? []).filter((m) => /part|body|hediff|injur|missing|destroy|restore|add|remove|cut|amput/i.test(m));
  for (const m of interesting.slice(0, 12)) log(`      ${m}`);
}
const apiHasPartOps =
  JSON.stringify(api.tools ?? {})
    .toLowerCase()
    .match(/missingpart|bodypart|restore|amputat|destroy.*part/) != null;
record('CE 内部存在部位相关方法', Boolean(apiHasPartOps), apiHasPartOps ? '' : '没在签名里看到明显的部位操作');

// ── 3. 截肢：左腿 ────────────────────────────────────────────
log('\n═══ 3. 把「左腿」变成缺失 ═══');
const beforeShot = await shootPawn(target.name, '01_before');
log(`  截肢前截图: ${beforeShot}`);

const cut = await call('ce/amputate', { part: 'left leg', pawnId: target.pawnId });
log(`  success=${cut.success}`);
log(`  匹配到: ${JSON.stringify(cut.matched)}`);
log(`  目标: ${JSON.stringify(cut.part)}`);
record('左腿截肢成功（PartIsMissing 为 true）', cut.success === true, JSON.stringify(cut).slice(0, 250));

const body1 = await call('ce/body', { pawnId: target.pawnId });
log(`  现在缺失 ${body1.missingCount} 个部位: ${JSON.stringify(body1.missingParts)}`);
record('回读确认缺失部位增加了', body1.missingCount > body0.missingCount, `${body0.missingCount} → ${body1.missingCount}`);

const afterShot = await shootPawn(target.name, '02_left_leg_gone');
log(`  截肢后截图: ${afterShot}`);

// ── 4. 再截一条：右臂 ────────────────────────────────────────
log('\n═══ 4. 再截掉右臂，构造更复杂的状态 ═══');
const cut2 = await call('ce/amputate', { part: 'right arm', pawnId: target.pawnId });
log(`  success=${cut2.success}  目标: ${JSON.stringify(cut2.part)}`);
const body2 = await call('ce/body', { pawnId: target.pawnId });
log(`  缺失 ${body2.missingCount} 个: ${JSON.stringify(body2.missingParts)}`);
record('能连续构造多个断肢', body2.missingCount >= 2, `${body2.missingCount} 个`);
await shootPawn(target.name, '03_leg_and_arm');

// ── 5. 恢复 ──────────────────────────────────────────────────
log('\n═══ 5. 恢复左腿 ═══');
const restore = await call('ce/restore', { part: 'left leg', pawnId: target.pawnId });
log(`  success=${restore.success}  移除的 hediff: ${JSON.stringify(restore.removed)}`);
log(`  目标现在: ${JSON.stringify(restore.part)}`);
record('左腿已恢复', restore.success === true, JSON.stringify(restore).slice(0, 250));

const body3 = await call('ce/body', { pawnId: target.pawnId });
log(`  缺失 ${body3.missingCount} 个: ${JSON.stringify(body3.missingParts)}`);
await shootPawn(target.name, '04_restored');

// 全部恢复，保持环境干净
await call('ce/restore', { part: 'right arm', pawnId: target.pawnId });
const body4 = await call('ce/body', { pawnId: target.pawnId });
record('全部恢复后回到初始状态', body4.missingCount === 0, `${body4.missingCount} 个缺失`);

// ── 6. CE 的 Health 标签页里能不能看到 ───────────────────────
log('\n═══ 6. CE 的 Health 标签页交叉验证 ═══');
try {
  const ce = await import('../src/ce.js');
  // 先造一个断肢，看 CE 自己的 UI 怎么显示
  await call('ce/amputate', { part: 'left leg', pawnId: target.pawnId });
  await ce.ensureOpen(client, { timeoutMs: 15_000 });
  await ce.openTab(client, 'Health');
  await ce.selectPawn(client, target.name);
  await new Promise((r) => setTimeout(r, 700));
  const rows = await ce.readPanel(client);
  const texts = rows.map((r) => r.text).filter(Boolean);
  log(`  Health 面板 ${texts.length} 行，找"腿/leg/missing"相关:`);
  for (const t of texts.filter((x) => /leg|missing|part|腿|缺/i.test(x)).slice(0, 10)) log(`    | ${t}`);
  record('CE 的 Health 面板里能看到断肢', texts.some((t) => /leg|missing|缺/i.test(t)), '面板里没找到相关文字');
  await shootPawn(target.name, '05_ce_health_tab');
  await ce.close(client);
  await call('ce/restore', { part: 'left leg', pawnId: target.pawnId });
} catch (e) {
  record('CE 的 Health 面板交叉验证', false, e.message);
}

// ── 7. 最终状态 ──────────────────────────────────────────────
await call('rimworld/pause_game', { pause: true });
const finalBody = await call('ce/body', { pawnId: target.pawnId });
log(`\n最终: 缺失 ${finalBody.missingCount} 个部位`);
const finalCols = (await call('rimworld/list_colonists', { currentMapOnly: true })).colonists ?? [];
record('游戏仍正常', finalCols.length >= 3, `${finalCols.length} 个殖民者`);

client.close();
const passed = results.filter((r) => r.ok).length;
log(`\n${'═'.repeat(62)}`);
log(`阶段 8（CE 断肢编辑）：${passed}/${results.length} 通过`);
for (const r of results.filter((x) => !x.ok)) log(`  ✗ ${r.name} — ${r.detail}`);
log('═'.repeat(62));
fs.writeFileSync(
  path.join(outDir, 'result.json'),
  JSON.stringify({ stamp, results, body0, api, target }, null, 2),
  'utf8',
);
log(`证据目录: ${outDir}`);
