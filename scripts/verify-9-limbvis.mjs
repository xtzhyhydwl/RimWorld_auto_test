#!/usr/bin/env node
/**
 * 阶段 9：断肢的**视觉验证**（放大特写对比）。
 *
 * 这是"条件贴图替换"最关心的：改了身体状态之后，画面上到底变没变。
 * 用两个小人分别构造两种断肢程度：
 *   A：截掉 left leg  → 整条腿（大腿+小腿+脚+脚趾）都没了
 *   B：只截 left foot → 小腿以下没了
 * 每一步都拍放大特写。
 */
import fs from 'node:fs';
import path from 'node:path';
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor, WS } from '../src/launcher.js';

const stamp = new Date().toISOString().replace(/[:.]/g, '-');
const outDir = path.join(WS, 'runs', `verify-limbvis-${stamp}`);
fs.mkdirSync(outDir, { recursive: true });

const info = readBridgeInfo(logPathFor('vanilla'));
const { client } = await connectGabp(info, { requestTimeoutMs: 30_000 });
const call = (n, a, o) => client.callTool(n, a, o);
const log = (...a) => console.log(...a);

const st = (await call('rimbridge/get_bridge_status')).state;
if (!st.hasCurrentGame) {
  log('开调试殖民地…');
  await call('rimworld/start_debug_game_ready', { readiness: 'mapData', pauseIfNeeded: true, timeoutMs: 300_000 }, { timeoutMs: 330_000 });
}
await call('rimbridge/wait_for_game_loaded', { readiness: 'currentMap', pauseIfNeeded: true, timeoutMs: 180_000 }, { timeoutMs: 210_000 });
await call('rimworld/pause_game', { pause: true });

const cols = await call('ce/colonists');
const list = cols.colonists ?? [];
log(`殖民者: ${list.map((c) => c.name).join(', ')}`);
if (list.length < 2) throw new Error('需要至少 2 个殖民者');

/** 放大特写某个小人 */
async function closeUp(pawnId, pawnName, label) {
  // 先拿到小人的地图坐标
  const sem = await call('rimworld/get_map_target_info', { pawnId }).catch(() => null);
  let pos = sem?.position ?? sem?.cell;
  if (!pos) {
    const c = list.find((x) => x.pawnId === pawnId);
    pos = null;
  }
  // 退路：用 frame_pawns 把镜头对准，然后靠 rootSize 放大
  await call('rimworld/frame_pawns', { pawnNamesCsv: pawnName }).catch(() => {});
  await new Promise((r) => setTimeout(r, 500));
  const r = await call('rimworld/take_screenshot', { suppressMessage: true, fileName: `limbvis_${label}` });
  if (r?.path && fs.existsSync(r.path)) fs.copyFileSync(r.path, path.join(outDir, path.basename(r.path)));
  return { path: r?.path, pos };
}

/** 用格子裁剪拍特写（会自动缩放并裁剪） */
async function cellCloseUp(pawnName, pawnId, label) {
  const areaInfo = await call('rimworld/get_map_target_info', { pawnId }).catch(() => null);
  const pos = areaInfo?.position ?? areaInfo?.cell ?? areaInfo?.mapPosition;
  if (!pos || pos.x === undefined) {
    log(`    （拿不到 ${pawnName} 的坐标，退回普通截图）`);
    return closeUp(pawnId, pawnName, label);
  }
  const r = await call('rimworld/screenshot_cell_rect', {
    x: pos.x,
    z: pos.z,
    width: 1,
    height: 1,
    paddingCells: 2,
    rootSize: 4,
    fileName: `limbvis_${label}`,
    suppressMessage: true,
    doNotResetCamera: false,
  });
  if (r?.path && fs.existsSync(r.path)) fs.copyFileSync(r.path, path.join(outDir, path.basename(r.path)));
  log(`    特写: ${r?.path} (clipped=${r?.clipped} rect=${JSON.stringify(r?.clipRect)})`);
  return { path: r?.path, pos };
}

const A = list[0];
const B = list[1];
log(`\nA = ${A.fullName} (${A.pawnId})   用于"整条腿"`);
log(`B = ${B.fullName} (${B.pawnId})   用于"小腿以下"`);

// ── A：整条腿 ────────────────────────────────────────────────
log('\n═══ A. 截掉 left leg（整条腿） ═══');
await cellCloseUp(A.name, A.pawnId, 'A1_before');
const cutA = await call('ce/amputate', { part: 'left leg', pawnId: A.pawnId });
log(`  截肢: success=${cutA.success}`);
const bodyA = await call('ce/body', { pawnId: A.pawnId });
log(`  现在缺失 ${bodyA.missingCount} 个: ${JSON.stringify(bodyA.missingParts)}`);
await cellCloseUp(A.name, A.pawnId, 'A2_leg_gone');

// ── B：只截脚 ────────────────────────────────────────────────
log('\n═══ B. 只截掉 left foot（小腿以下） ═══');
await cellCloseUp(B.name, B.pawnId, 'B1_before');
const cutB = await call('ce/amputate', { part: 'left foot', pawnId: B.pawnId });
log(`  截肢: success=${cutB.success}  目标=${JSON.stringify(cutB.part)}`);
const bodyB = await call('ce/body', { pawnId: B.pawnId });
log(`  现在缺失 ${bodyB.missingCount} 个: ${JSON.stringify(bodyB.missingParts)}`);
await cellCloseUp(B.name, B.pawnId, 'B2_foot_gone');

// ── 恢复验证 ─────────────────────────────────────────────────
log('\n═══ C. 恢复验证（修好的 restore） ═══');
const rA = await call('ce/restore', { part: 'left leg', pawnId: A.pawnId });
log(`  A 恢复: success=${rA.success}  清掉 ${rA.removedCount} 条: ${JSON.stringify(rA.removed)}`);
const bodyA2 = await call('ce/body', { pawnId: A.pawnId });
log(`  A 现在缺失 ${bodyA2.missingCount} 个`);
const rB = await call('ce/restore', { part: 'left foot', pawnId: B.pawnId });
log(`  B 恢复: success=${rB.success}  清掉 ${rB.removedCount} 条`);
const bodyB2 = await call('ce/body', { pawnId: B.pawnId });
log(`  B 现在缺失 ${bodyB2.missingCount} 个`);

const allClean = bodyA2.missingCount === 0 && bodyB2.missingCount === 0;
log(`\n${allClean ? '✓' : '✗'} 两个小人都恢复干净: ${allClean}`);

client.close();
log(`\n证据目录: ${outDir}`);
fs.writeFileSync(
  path.join(outDir, 'result.json'),
  JSON.stringify({ stamp, A, B, bodyA, bodyB, bodyA2, bodyB2, restoreA: rA, restoreB: rB, allClean }, null, 2),
  'utf8',
);
