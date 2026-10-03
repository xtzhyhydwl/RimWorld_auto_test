#!/usr/bin/env node
/**
 * 断肢视觉对比（放大特写）—— 用 list_colonists 的坐标，可靠。
 */
import fs from 'node:fs';
import path from 'node:path';
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor, WS } from '../src/launcher.js';

const stamp = new Date().toISOString().replace(/[:.]/g, '-');
const outDir = path.join(WS, 'runs', `limbvis2-${stamp}`);
fs.mkdirSync(outDir, { recursive: true });

const info = readBridgeInfo(logPathFor('vanilla'));
const { client } = await connectGabp(info, { requestTimeoutMs: 30_000 });
const call = (n, a, o) => client.callTool(n, a, o);
const log = (...a) => console.log(...a);

const st = (await call('rimbridge/get_bridge_status')).state;
if (!st.hasCurrentGame) {
  await call('rimworld/start_debug_game_ready', { readiness: 'mapData', pauseIfNeeded: true, timeoutMs: 300_000 }, { timeoutMs: 330_000 });
}
await call('rimbridge/wait_for_game_loaded', { readiness: 'currentMap', pauseIfNeeded: true, timeoutMs: 180_000 }, { timeoutMs: 210_000 });
await call('rimworld/pause_game', { pause: true });

const cols = (await call('rimworld/list_colonists', { currentMapOnly: true })).colonists ?? [];
const A = cols[0];
const B = cols[1];
log(`A = ${A.fullName} @(${A.position.x},${A.position.z})`);
log(`B = ${B.fullName} @(${B.position.x},${B.position.z})`);

async function closeUp(pawn, label, rootSize = 8) {
  const r = await call('rimworld/screenshot_cell_rect', {
    x: pawn.position.x,
    z: pawn.position.z,
    width: 1,
    height: 1,
    paddingCells: 2,
    rootSize,
    fileName: `lv2_${label}`,
    suppressMessage: true,
  });
  if (r?.path && fs.existsSync(r.path)) fs.copyFileSync(r.path, path.join(outDir, path.basename(r.path)));
  log(`    ${label}: ${path.basename(r?.path ?? '?')}  clipped=${r?.clipped}  clipRect=${JSON.stringify(r?.clipRect)}`);
  return r?.path;
}

log('\n═══ A：整条腿 vs B：小腿以下 ═══');
await closeUp(A, 'A1_before');
await closeUp(B, 'B1_before');

const ca = await call('ce/amputate', { part: 'left leg', pawnId: A.pawnId });
log(`  A 截 left leg: success=${ca.success}  → 缺失 ${(await call('ce/body', { pawnId: A.pawnId })).missingCount} 个`);
const cb = await call('ce/amputate', { part: 'left foot', pawnId: B.pawnId });
log(`  B 截 left foot: success=${cb.success} → 缺失 ${(await call('ce/body', { pawnId: B.pawnId })).missingCount} 个`);

await closeUp(A, 'A2_leg_gone');
await closeUp(B, 'B2_foot_gone');

// 再放大一点看细节
await closeUp(A, 'A3_leg_gone_zoom', 5);
await closeUp(B, 'B3_foot_gone_zoom', 5);

client.close();
log(`\n证据目录: ${outDir}`);
