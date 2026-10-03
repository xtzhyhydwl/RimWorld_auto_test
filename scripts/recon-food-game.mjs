#!/usr/bin/env node
/**
 * recon-food-game —— 开调试殖民地并采集「食物数据源」侦察数据。
 *
 * 阶段：
 *   0. 关掉挡着的弹窗 → start_debug_game_ready(mapData) → wait currentMap
 *   1. get_game_info 全量 dump
 *   2. 生成几堆食物（MealSimple / Pemmican / RawRice …）
 *   3. get_cell_info 看食物格子返回什么（字段全 dump）
 *   4. get_cells_info 批量计时（16x16=256、32x32=1024）
 *   5. list_zones / list_colonists 结构
 */
import fs from 'node:fs';
import path from 'node:path';
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor, WS } from '../src/launcher.js';

const OUT = path.join(WS, 'runs', 'food-recon');
fs.mkdirSync(OUT, { recursive: true });
const DUMP = path.join(OUT, 'dumps');
fs.mkdirSync(DUMP, { recursive: true });

const info = readBridgeInfo(logPathFor('vanilla'));
const { client } = await connectGabp(info, { requestTimeoutMs: 60_000 });
const call = (n, a, o) => client.callTool(n, a, o);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const report = { at: new Date().toISOString(), steps: [] };
let dead = false;
function save(name, obj) {
  fs.writeFileSync(path.join(DUMP, `${name}.json`), JSON.stringify(obj, null, 2), 'utf8');
}
function head(o, n = 1500) {
  const s = typeof o === 'string' ? o : JSON.stringify(o);
  return s.length > n ? s.slice(0, n) + ` …(+${s.length - n})` : s;
}
async function step(name, fn, { keep = true } = {}) {
  const s = Date.now();
  process.stdout.write(`\n▶ ${name}\n`);
  try {
    const v = await fn();
    report.steps.push({ name, ok: true, ms: Date.now() - s });
    if (keep && v !== undefined && v !== null) save(name.replace(/[^\w\u4e00-\u9fa5]+/g, '_'), v);
    return v;
  } catch (err) {
    const isTimeout = err.code === 'GABP_TIMEOUT';
    report.steps.push({ name, ok: false, ms: Date.now() - s, error: err.message, timeout: isTimeout });
    console.log(`  ✗ ${err.message}`);
    if (isTimeout) { dead = true; throw Object.assign(new Error('BRIDGE_TIMEOUT'), { fatal: true }); }
    return null;
  }
}

try {
  // ── 0. 环境准备 ────────────────────────────────────────────
  const bs0 = await step('bridge_status(before)', () => call('rimbridge/get_bridge_status'));
  console.log('  ' + head({ ...bs0?.state }, 400));

  if (bs0?.state?.inEntryScene) {
    const us = await call('rimworld/get_ui_state').catch(() => null);
    if (us?.windowCount > 0) {
      await step('关闭挡住的弹窗(Page_ModsConfig)', async () => {
        await call('rimworld/press_cancel', {}, { timeoutMs: 15_000 });
        await sleep(600);
        const s = await call('rimworld/get_ui_state');
        console.log(`  关后 windowCount=${s.windowCount} top=${s.topWindowType}`);
        return s;
      });
    }
    await step('start_debug_game_ready(mapData)', async () => {
      const r = await call('rimworld/start_debug_game_ready',
        { readiness: 'mapData', pauseIfNeeded: true, timeoutMs: 300_000 },
        { timeoutMs: 330_000 });
      console.log('  ' + head({ success: r?.success, message: r?.message, wait: r?.wait }, 600));
      return r;
    });
  } else {
    console.log('  (已有游戏，跳过开局)');
  }

  await step('wait_for_game_loaded(currentMap)', async () => {
    const w = await call('rimbridge/wait_for_game_loaded',
      { readiness: 'currentMap', pauseIfNeeded: true, timeoutMs: 180_000 },
      { timeoutMs: 210_000 });
    console.log('  ' + head({ satisfied: w?.satisfied, message: w?.message, blockingReason: w?.blockingReason }, 400));
    return w;
  });
  await step('wait_for_long_event_idle', () =>
    call('rimbridge/wait_for_long_event_idle', { timeoutMs: 120_000 }, { timeoutMs: 150_000 }));

  const bs1 = await step('bridge_status(after)', () => call('rimbridge/get_bridge_status'));
  console.log('  ' + head({ ...bs1?.state }, 500));

  // ── 1. get_game_info ───────────────────────────────────────
  const gi = await step('A1 get_game_info', () => call('rimworld/get_game_info'));
  console.log('  ' + head(gi, 4000));
  console.log('  top-level keys: ' + Object.keys(gi ?? {}).join(', '));

  // ── 2. 地图边界 & 殖民者 ────────────────────────────────────
  const cols = await step('A2 list_colonists', () => call('rimworld/list_colonists', { currentMapOnly: true }));
  const c0 = (cols?.colonists ?? [])[0];
  console.log(`  殖民者 ${(cols?.colonists ?? []).length} 人; 第1人 keys: ${Object.keys(c0 ?? {}).join(', ')}`);
  console.log('  ' + head(c0, 1200));

  const cam = await step('A2b get_camera_state', () => call('rimworld/get_camera_state'));
  console.log('  ' + head(cam, 900));

  const mti = await step('A2c get_map_target_info(pawn)', () => call('rimworld/get_map_target_info', { pawnId: c0?.pawnId }));
  console.log('  ' + head(mti, 900));

  fs.writeFileSync(path.join(OUT, 'report2.json'), JSON.stringify(report, null, 2), 'utf8');
} catch (e) {
  report.fatal = e.message;
  console.log(`\n[FATAL] ${e.message}`);
} finally {
  report.dead = dead;
  report.finishedAt = new Date().toISOString();
  fs.writeFileSync(path.join(OUT, 'report2.json'), JSON.stringify(report, null, 2), 'utf8');
  client.close();
  console.log(`\n报告：${path.join(OUT, 'report2.json')}${dead ? '  ⚠ 桥接可能已挂死' : ''}`);
}
