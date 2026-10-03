/**
 * 地基验证：在真实游戏里确认「建种植区 + 设作物 + 小人真去种」这条路径可行。
 *
 * 为什么这一步必须先做：
 *   `ZoneMaker.MakeZoneWithCells` 的**签名**已确证（编译探针通过），
 *   但它的**内部行为**（是否自动 RegisterZone / 逐格 AddCell / 有校验）
 *   **无法从元数据判断** —— 只能真的跑一遍。
 *   它是整个自动种田的地基，所以先验，失败就早改架构。
 *
 * ⚠ **本脚本只「附着」到已经在运行的游戏，自己绝不启动游戏。**
 *   踩过的坑：从一次性 shell 命令里 `startGame()`，进程被孤儿化后会
 *   **100% CPU 死循环、窗口不出现、日志停止增长**（实测烧了 80+ 秒 CPU 无进展）。
 *   游戏必须由长驻后台任务托管：
 *     node src/cli.js start     ← 用 run_in_background 托管
 *   然后再跑本脚本。
 *
 * 用法：
 *   1. 先确保桥接可用：node src/cli.js status
 *   2. node scripts/verify-foundation.mjs
 * 产出：runs/foundation/report.json + report.md
 */
import fs from 'node:fs';
import path from 'node:path';
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor, WS } from '../src/launcher.js';

const PROFILE = 'vanilla';
const CALL = 120_000;

const RUN_DIR = path.join(WS, 'runs', 'foundation');
const steps = [];
let client = null;

function log(...a) {
  console.log(...a);
}

async function call(name, args = {}, timeoutMs = CALL) {
  const t0 = Date.now();
  const res = await client.callTool(name, args, { timeoutMs });
  return { res, ms: Date.now() - t0 };
}

function record(label, ok, detail) {
  steps.push({ label, ok, detail, at: new Date().toISOString() });
  log(`  ${ok === null ? '····' : ok ? '[PASS]' : '[FAIL]'} ${label}${detail ? '  — ' + detail : ''}`);
}

/** 关掉 forcePause 的窗口 —— 不关的话 play_for 会静默跑 0 tick（实测踩过） */
async function clearForcePause() {
  const ui = await call('rimworld/get_ui_state');
  if (!ui.res?.windowsForcePause) return [];
  const closed = [];
  for (const w of ui.res.windows || []) {
    if (!w.forcePause) continue;
    const id = w.type === 'RimWorld.MainTabWindow_Menu' ? 'main-tab:Menu' : null;
    if (!id) continue;
    await call('rimworld/close_main_tab', { tabId: id });
    closed.push(id);
  }
  return closed;
}

async function main() {
  log('\n=== 0. 附着到已在运行的游戏（不由本脚本启动）===');
  const info = readBridgeInfo(logPathFor(PROFILE));
  if (!info) throw new Error('日志里没有桥接信息 —— 先跑 `node src/cli.js start`（用后台任务托管）');
  const conn = await connectGabp(info, { requestTimeoutMs: CALL });
  client = conn.client;
  log(`  桥接 ${info.host}:${info.port}`);

  try {
    const tools = await client.listTools();
    const probeTools = tools.filter((t) => t.name.startsWith('foodprobe/')).map((t) => t.name);
    record('探针伴生 DLL 加载', probeTools.length > 0,
      probeTools.length ? `foodprobe 工具 ${probeTools.length} 个` : '没加载 —— 检查 BridgeTools 与是否重启过游戏');

    const modTools = tools.length;
    log(`  工具总数 ${modTools}`);

    // ── 2. 确保有一张可用的地图 + 殖民者 ──────────────────────
    //
    // ⚠ 实测踩到的三个坑，都在这里处理：
    //   1. `start_debug_game_ready` **几乎总会先超时一次**（桥接调用排队在主线程，
    //      建殖民地期间轮询不到），但**殖民地其实会建成功** —— 所以要容忍超时、
    //      然后自己耐心轮询，而不是把超时当失败。
    //   2. 超时返回时游戏可能"地图已存在但殖民者还没生成"，所以要**分开等**
    //      地图和殖民者两个条件。
    //   3. `start_debug_game`（轻量版）**只能从主菜单调用**，在游戏里调会报
    //      "only supported from the main menu entry scene"。所以不要用它做恢复手段。
    log('\n=== 2. 确保有地图 + 殖民者 ===');

    const mapAndColonistsReady = async () => {
      const g = await call('rimworld/get_game_info');
      const hasMap = g.res?.status === 'game_loaded' && (g.res?.mapCount ?? 0) > 0;
      if (!hasMap) return { hasMap: false, colonists: 0 };
      const c = await call('rimworld/list_colonists');
      return { hasMap: true, colonists: c.res?.count ?? 0 };
    };

    let st1 = await mapAndColonistsReady();
    if (!st1.hasMap || st1.colonists === 0) {
      log('  需要开调试殖民地...');
      await call(
        'rimworld/start_debug_game_ready',
        { readiness: 'mapData', pauseIfNeeded: true, timeoutMs: 240_000 },
        { timeoutMs: 280_000 },
      ).catch((e) => log('  （超时是预期的，殖民地仍在后台建）: ' + e.message.split('\n')[0]));
    }

    let ready = false;
    for (let i = 0; i < 90; i++) {
      st1 = await mapAndColonistsReady();
      if (st1.hasMap && st1.colonists > 0) { ready = true; break; }
      if (i % 10 === 0) log(`  等待中... 地图=${st1.hasMap} 殖民者=${st1.colonists}`);
      await new Promise((r) => setTimeout(r, 2000));
    }

    record('地图可用', st1.hasMap);
    record('殖民地有殖民者（否则没法验证"小人去种"）', st1.colonists > 0, `${st1.colonists} 个`);

    const closed = await clearForcePause();
    if (closed.length) record('清掉 forcePause 窗口', true, closed.join(','));

    // ── 3. 探针自检 ────────────────────────────────────────────
    log('\n=== 3. 探针 / mod 自检 ===');
    const health = await call('foodprobe/health');
    record('foodprobe/health 可调用', health.res?.success !== undefined,
      JSON.stringify(health.res?.types || health.res?.message || '').slice(0, 200));

    // ── 4. 食物感知可行性 ──────────────────────────────────────
    log('\n=== 4. 食物感知（桥接读不到 nutrition，验证 mod 侧能读）===');
    const food = await call('foodprobe/food_summary');
    record('foodprobe/food_summary 可调用', food.res?.success === true,
      `totalNutrition=${food.res?.totalNutrition} 殖民者=${food.res?.colonistCount}`);

    // ── 5. 找一块真能种的空地 ──────────────────────────────────
    // ⚠ 实测发现：**地图上很少有整块全可种的方形地**（最多探到 23/36）。
    //   这是真实地形（碎石 + 沙地零散）。所以判据不能假设"全可种"，
    //   改成"可种比例够高"—— 顺便这也更接近 mod 真实运行时的情形。
    log('\n=== 5. 找可种空地（逐点 dryRun 探测）===');
    let plot = null;
    let bestProbe = null;
    const probes = [];
    for (const p of [
      [60, 60], [80, 80], [100, 100], [120, 120], [70, 110], [110, 70],
      [90, 140], [140, 90], [100, 60], [60, 100], [130, 130], [50, 130],
    ]) {
      const d = await call('foodprobe/zone_test', { x: p[0], z: p[1], side: 6, dryRun: true });
      const arable = d.res?.cellsArable ?? 0;
      probes.push({ at: p, arable });
      if (!bestProbe || arable > bestProbe.arable) bestProbe = { at: p, arable };
      // 够用就行：≥16/36（44%）就是一块可用的地
      if (arable >= 16) { plot = p; break; }
    }
    if (!plot && bestProbe && bestProbe.arable > 0) {
      plot = bestProbe.at;
      record('退而求其次用最密的地块', true, `(${plot}) 可种 ${bestProbe.arable}/36`);
    }
    record('找到可种地块', plot !== null,
      plot ? `(${plot[0]},${plot[1]})` : `探测点: ${JSON.stringify(probes)}`);

    if (plot) {
      // ── 6. ★ 地基验证：真的建区 + 设作物 ────────────────────
      log('\n=== 6. ★ 地基验证：ZoneMaker.MakeZoneWithCells + SetPlantDefToGrow ===');
      const zt = await call('foodprobe/zone_test', {
        x: plot[0], z: plot[1], side: 6, plantDefName: 'Plant_Rice', dryRun: false,
      });
      const r = zt.res || {};
      record('建区 + 设作物 判定', r.success === true, `verdict=${r.verdict}`);
      log('     完整返回: ' + JSON.stringify({
        verdict: r.verdict,
        zonesBefore: r.growingZoneCountBefore,
        zonesAfter: r.growingZoneCountAfter,
        makeOnly: r.createResult,
        register: r.registerResult,
        zone: r.zone,
        createException: r.createException ? r.createException.message : null,
        setCropResult: r.setCropResult,
        arable: `${r.cellsArable}/${r.cellsRequested}`,
      }));
      if (r.createException) {
        record('MakeZoneWithCells 无异常', false,
          `${r.createException.type}: ${r.createException.message}`);
      }
      record('区被登记进 zoneManager', r.zone?.registeredInZoneManager === true,
        `zoneId=${r.zone?.zoneId} 格数=${r.zone?.cellCount}`);
      record('作物设置成功', r.zone?.plantDefToGrow === 'Plant_Rice',
        `plantDefToGrow=${r.zone?.plantDefToGrow}`);

      // ── 7. 回读确认 ────────────────────────────────────────
      log('\n=== 7. 回读确认 ===');
      const rz = await call('foodprobe/read_zone', { zoneId: r.zone?.zoneId ?? -1 });
      // ⚠ 之前这里写成 `success === true` 是**假阳性** —— 传无效 id 时工具也会返回 success。
      //   必须同时检查真的读到了格数与作物。
      record('read_zone 能读回该区',
        rz.res?.success === true && (rz.res?.cells ?? 0) > 0 && rz.res?.plantDefToGrow === 'Plant_Rice',
        `cells=${rz.res?.cells} crop=${rz.res?.plantDefToGrow} 肥力=${rz.res?.avgFertility}`);

      // ── 8. ★ 小人真去种吗 ──────────────────────────────────
      log('\n=== 8. ★ 推进时间，看小人是否真的去种 ===');
      const before = rz.res?.plantCount ?? 0;
      let advanced = 0;
      const target = 60_000;   // 约一个游戏天
      for (let i = 0; i < 12 && advanced < target; i++) {
        await clearForcePause();
        const p = await call('rimworld/play_for', {
          durationMs: 8000, speed: 'Ultrafast', forceRequestedSpeed: true, pollIntervalMs: 25,
        }, 80_000);
        const t = p.res?.advancedTicks ?? 0;
        advanced += t;
      }
      record('时间推进', advanced > 5000, `实际推进 ${advanced} tick`);

      const rz2 = await call('foodprobe/read_zone', { zoneId: r.zone?.zoneId ?? -1 });
      const after = rz2.res?.plantCount ?? 0;
      // ⚠ 这条原来写成 `after > before` 但 before/after 都是 0 时……其实 0>0 是 false，
      //   之所以显示 PASS 是因为植物数取值时机不对。这里显式要求 after > 0。
      record('★ 小人真的去种了（区里出现了植物）', after > 0,
        `植物数 ${before} → ${after}`);
      if (rz2.res?.samples?.length) {
        const s = rz2.res.samples[0];
        record('植物有生长进度可读', s.growth > 0,
          `growth=${s.growth} growthRate=${s.growthRate} growDays=${s.growDays} harvestMinGrowth=${s.harvestMinGrowth}`);
      } else {
        record('植物有生长进度可读', false, '样本为空');
      }
    }

    // ── 9. 遥测通道 ────────────────────────────────────────────
    log('\n=== 9. 遥测通道 ===');
    const tl = await call('foodprobe/telemetry_tail', { lines: 3 });
    record('telemetry 读取工具可用', tl.res?.success !== undefined,
      tl.res?.success ? `${tl.res?.totalLines} 行` : tl.res?.message);
  } finally {
    client.close();
  }

  const passed = steps.filter((s) => s.ok === true).length;
  const failed = steps.filter((s) => s.ok === false).length;
  const skipped = steps.filter((s) => s.ok === null).length;

  fs.mkdirSync(RUN_DIR, { recursive: true });
  fs.writeFileSync(
    path.join(RUN_DIR, 'report.json'),
    JSON.stringify({ passed, failed, skipped, steps }, null, 2),
    'utf8',
  );

  const md = [
    '# 地基验证报告',
    '',
    `时间：${new Date().toISOString()}`,
    `结果：**通过 ${passed} / 失败 ${failed} / 跳过 ${skipped}**`,
    '',
    '| 结果 | 项目 | 细节 |',
    '| --- | --- | --- |',
    ...steps.map((s) => `| ${s.ok === null ? '—' : s.ok ? '✅' : '❌'} | ${s.label} | ${(s.detail || '').replace(/\|/g, '\\|')} |`),
    '',
    '> 判定标准：只有 `success` 不算数，必须回读确认游戏真的变了。',
  ].join('\n');
  fs.writeFileSync(path.join(RUN_DIR, 'report.md'), md, 'utf8');

  log(`\n=== 通过 ${passed} / 失败 ${failed} / 跳过 ${skipped} ===`);
  log(`报告: ${path.join(RUN_DIR, 'report.md')}`);
  process.exitCode = failed === 0 ? 0 : 1;
}

main().catch((e) => {
  console.error('\n[错误] ' + e.message);
  if (e.stack) console.error(e.stack.split('\n').slice(0, 5).join('\n'));
  process.exitCode = 1;
});
