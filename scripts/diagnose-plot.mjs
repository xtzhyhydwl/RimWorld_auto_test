/**
 * 诊断：为什么 mod 的 `FindBestPlot` 返回 null？以及"物资被禁取"假设是否成立？
 *
 * 背景：集成测试 17/20 通过，三个失败项都指向同一件事 ——
 *   决策层**正确地**说"要种 4 格"，但执行层报
 *   `没有可用的新地块（Core 的 FindBestPlot 返回 null）`，
 *   于是地图上一块 Zone_Growing 都没有。
 *
 * 两种可能，必须区分：
 *   (a) 地图真的没有可种的连片地（坏地形）→ 测试场景问题
 *   (b) 选址算法的阈值太严（MaxNonArableRatio=0.25 / MinSide=5）→ 算法问题
 *
 * 顺带验证用户提出的假设：**debug 开局刷的物资默认 forbidden，小人拿不到**，
 * 如果是，那"饿死"是测试场景缺陷而不是 mod bug。
 *
 * ⚠ 本脚本只**附着**到已在运行的游戏，自己绝不启动游戏。
 * 用法：node scripts/diagnose-plot.mjs
 */
import fs from 'node:fs';
import path from 'node:path';
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor, WS } from '../src/launcher.js';

const PROFILE = 'vanilla';
const CALL = 120_000;
const OUT = path.join(WS, 'runs', 'diagnose-plot');

let client = null;
const log = (...a) => console.log(...a);

async function call(name, args = {}, timeoutMs = CALL) {
  const res = await client.callTool(name, args, { timeoutMs });
  return res;
}

async function clearForcePause() {
  const ui = await call('rimworld/get_ui_state');
  if (!ui?.windowsForcePause) return;
  for (const w of ui.windows || []) {
    if (w.forcePause && w.type === 'RimWorld.MainTabWindow_Menu') {
      await call('rimworld/close_main_tab', { tabId: 'main-tab:Menu' });
    }
  }
}

async function main() {
  const info = readBridgeInfo(logPathFor(PROFILE));
  if (!info) throw new Error('没有桥接信息 —— 先 node src/cli.js start（后台任务托管）');
  const conn = await connectGabp(info, { requestTimeoutMs: CALL });
  client = conn.client;
  log(`桥接 ${info.host}:${info.port}`);

  const report = { at: new Date().toISOString() };

  try {
    // ── 0. 确保地图 ────────────────────────────────────────────
    const ready = async () => {
      const g = await call('rimworld/get_game_info');
      if (!(g?.status === 'game_loaded' && (g?.mapCount ?? 0) > 0)) return { ok: false, c: 0 };
      const c = await call('rimworld/list_colonists');
      return { ok: true, c: c?.count ?? 0 };
    };
    let st = await ready();
    if (!st.ok || st.c === 0) {
      log('开调试殖民地（超时是预期的，之后自己轮询）...');
      await call('rimworld/start_debug_game_ready',
        { readiness: 'mapData', pauseIfNeeded: true, timeoutMs: 240_000 }, 280_000,
      ).catch((e) => log('  ' + e.message.split('\n')[0]));
    }
    for (let i = 0; i < 90; i++) {
      st = await ready();
      if (st.ok && st.c > 0) break;
      await new Promise((r) => setTimeout(r, 2000));
    }
    log(`地图=${st.ok} 殖民者=${st.c}`);
    report.colonists = st.c;
    await clearForcePause();

    // ── 1. ★ 禁取假设验证 ──────────────────────────────────────
    log('\n=== 1. 禁取（forbidden）假设验证 ===');
    const forb = await call('foodprobe/forbidden_stats', { maxSamples: 10 });
    report.forbidden = forb;
    log(`  被禁取总数: ${forb?.totalForbidden}`);
    log(`    可搬运 ${forb?.haulable} / 建筑 ${forb?.buildings} / 植物 ${forb?.plants} / 其它 ${forb?.other}`);
    log(`  ★ 被禁取的**食物**营养总量: ${forb?.forbiddenFoodNutrition}`);
    log(`  top defs: ${JSON.stringify((forb?.topDefs || []).slice(0, 8))}`);
    log(`  殖民者: ${JSON.stringify(forb?.colonists)}`);

    // ── 2. 食物感知（mod 侧 vs 探针侧）──────────────────────────
    log('\n=== 2. 食物感知 ===');
    const food = await call('foodprobe/food_summary');
    report.foodSummary = food;
    log(`  探针汇总: totalNutrition=${food?.totalNutrition} 殖民者=${food?.colonistCount}`);
    const stateRes = await call('foodprobe/read_state');
    report.modState = stateRes;
    log(`  mod 感知: ${JSON.stringify(stateRes?.state).slice(0, 400)}`);

    // ── 3. ★ 地形普查：地图到底有多少可种的地 ──────────────────
    log('\n=== 3. 地形/可种性普查（决定是"坏地形"还是"阈值太严"）===');
    const mapInfo = await call('rimworld/get_game_info');
    // 先用一次 dryRun 拿地图尺寸
    const probe0 = await call('foodprobe/zone_test', { x: 0, z: 0, side: 1, dryRun: true });
    log(`  地图尺寸参考: ${JSON.stringify(probe0?.mapSize ?? '(未知)')}`);

    // 粗粒度网格扫描：每 3 格取一个 3x3 块，统计可种比例
    const GRID = 3;
    const samples = [];
    let scanned = 0, blocksFullyArable = 0, blocksMostlyArable = 0, blocksAny = 0;
    const terrainHist = {};
    const arableBySize = { '3x3': 0, '6x6': 0 };

    for (let x = 20; x <= 230; x += 12) {
      for (let z = 20; z <= 230; z += 12) {
        const d = await call('foodprobe/zone_test', { x, z, side: GRID, dryRun: true });
        if (d?.success !== true) continue;
        scanned++;
        const a = d.cellsArable ?? 0;
        const total = d.cellsRequested ?? 9;
        if (a > 0) blocksAny++;
        if (a === total) blocksFullyArable++;
        if (a / total >= 0.75) blocksMostlyArable++;

        for (const s of d.sample || []) {
          if (s.terrain) terrainHist[s.terrain] = (terrainHist[s.terrain] || 0) + 1;
        }
        if (samples.length < 40 && a > 0) samples.push({ at: [x, z], arable: a, total });
      }
    }

    report.terrainScan = {
      scannedBlocks: scanned,
      blockSize: GRID,
      blocksWithAnyArable: blocksAny,
      blocksFullyArable,
      blocksAtLeast75Pct: blocksMostlyArable,
      terrainHistogram: terrainHist,
      arableSamples: samples,
    };

    log(`  扫描 ${scanned} 个 ${GRID}x${GRID} 块（每 12 格取一个）:`);
    log(`    有可种格: ${blocksAny}  全可种: ${blocksFullyArable}  ≥75%: ${blocksMostlyArable}`);
    log(`    地表分布: ${JSON.stringify(terrainHist)}`);
    log(`    有可种格的样本: ${JSON.stringify(samples.slice(0, 12))}`);

    // ── 4. 试不同尺寸，看是否存在可用的连片地 ──────────────────
    log('\n=== 4. 不同地块尺寸下的可种情况（定位阈值问题）===');
    const bestSpots = samples.slice(0, 6).map((s) => s.at);
    const sizeProbe = [];
    for (const spot of bestSpots) {
      const row = { at: spot };
      for (const side of [3, 4, 5, 6, 8]) {
        const d = await call('foodprobe/zone_test', { x: spot[0], z: spot[1], side, dryRun: true });
        row['side' + side] = `${d?.cellsArable ?? 0}/${d?.cellsRequested ?? 0}`;
      }
      sizeProbe.push(row);
    }
    report.sizeProbe = sizeProbe;
    for (const r of sizeProbe) log(`  ${JSON.stringify(r.at)}  →  ${[3, 4, 5, 6, 8].map((s) => `${s}:${r['side' + s]}`).join('  ')}`);

    // ── 5. 标定：每格产量 ──────────────────────────────────────
    log('\n=== 5. 标定：水稻每格值多少 nutrition ===');
    const ypc = await call('foodprobe/yield_per_cell', { plantDefName: 'Plant_Rice', sampleCells: 6 });
    report.yieldPerCell = ypc;
    log(`  ${JSON.stringify(ypc).slice(0, 500)}`);

    // ── 6. 直接看 mod 的选址为什么失败 ──────────────────────────
    log('\n=== 6. 跑一轮闭环，看执行层报什么 ===');
    const cycle = await call('foodprobe/run_cycle');
    report.cycle = cycle;
    log(`  ${JSON.stringify(cycle).slice(0, 600)}`);

    const tl = await call('foodprobe/telemetry_tail', { lines: 1 });
    report.telemetryTail = tl;
    if (tl?.tail?.length) log(`  遥测末行: ${tl.tail[0].slice(0, 800)}`);
  } finally {
    client.close();
  }

  fs.mkdirSync(OUT, { recursive: true });
  const file = path.join(OUT, 'report.json');
  fs.writeFileSync(file, JSON.stringify(report, null, 2), 'utf8');
  log(`\n→ ${file}`);
}

main().catch((e) => {
  console.error('\n[错误] ' + e.message);
  process.exitCode = 1;
});
