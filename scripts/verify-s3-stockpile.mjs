/**
 * S3 验证：储藏区域（储藏区 domain）。
 *
 * ## 验什么
 *
 * 1. mod 的储藏区逻辑能跑（`foodprobe/run_stockpile`）
 * 2. ★ **桥接独立回读**：地图上真的有 `Zone_Stockpile` 且格数 > 0（不信 mod 自述）
 * 3. filter 允许常见物品（不然小人不会往里放东西）
 * 4. ★ **不是"把整张地图划成一个区"** —— 这是刻意加的断言，
 *    因为最初实现时所有候选格权重都是 1，K-means 退化成"把大地图切两半"，
 *    建出了 7001 格和 8055 格的巨型区（见 `StockpileDomain` 类注释）
 * 5. 域框架里确实注册了 stockpile 域
 *
 * ⚠ 只**附着**到已在运行的游戏，自己绝不启动游戏。
 * 用法：node scripts/verify-s3-stockpile.mjs
 */
import fs from 'node:fs';
import path from 'node:path';
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor, WS } from '../src/launcher.js';

const PROFILE = 'vanilla';
const CALL = 120_000;
const RUN_DIR = path.join(WS, 'runs', 's3-stockpile');

/** 单区格数上限：超过这个数说明"把大地图切一半"了，不是"贴着资源划小区" */
const MAX_REASONABLE_ZONE_CELLS = 1500;

let client = null;
const steps = [];
const log = (...a) => console.log(...a);

async function call(name, args = {}, timeoutMs = CALL) {
  return client.callTool(name, args, { timeoutMs });
}

function record(label, ok, detail) {
  steps.push({ label, ok, detail, at: new Date().toISOString() });
  log(`  ${ok === null ? '····' : ok ? '[PASS]' : '[FAIL]'} ${label}${detail ? '  — ' + detail : ''}`);
}

async function clearForcePause() {
  const ui = await call('rimworld/get_ui_state');
  if (!ui?.windowsForcePause) return [];
  const closed = [];
  for (const w of ui.windows || []) {
    if (!w.forcePause) continue;
    if (w.type === 'RimWorld.MainTabWindow_Menu') {
      await call('rimworld/close_main_tab', { tabId: 'main-tab:Menu' });
      closed.push('main-tab:Menu');
    }
  }
  return closed;
}

async function ready() {
  const g = await call('rimworld/get_game_info');
  if (g?.status !== 'game_loaded') return { ok: false, stage: 'not_loaded', c: 0 };
  if ((g?.mapCount ?? 0) <= 0) return { ok: false, stage: 'no_map', c: 0 };
  const ui = await call('rimworld/get_ui_state');
  if (ui?.programState !== 'Playing') return { ok: false, stage: 'programState=' + ui?.programState, c: 0 };
  if ((g?.ticksGame ?? 0) <= 0) return { ok: false, stage: 'ticks=0', c: 0 };
  const c = await call('rimworld/list_colonists');
  return { ok: true, stage: 'playing', c: c?.count ?? 0, ticks: g?.ticksGame ?? 0 };
}

async function main() {
  const info = readBridgeInfo(logPathFor(PROFILE));
  if (!info) throw new Error('没有桥接信息 —— 先 node src/cli.js start（后台任务托管）');
  const conn = await connectGabp(info, { requestTimeoutMs: CALL });
  client = conn.client;
  log(`桥接 ${info.host}:${info.port}`);

  try {
    // ── 0. 地图就绪 ──
    let st = await ready();
    if (st.stage === 'not_loaded' || st.stage === 'no_map') {
      log(`开调试殖民地（阶段 ${st.stage}，超时预期）...`);
      await call('rimworld/start_debug_game_ready',
        { readiness: 'mapData', pauseIfNeeded: true, timeoutMs: 240_000 }, 280_000,
      ).catch((e) => log('  ' + e.message.split('\n')[0]));
    }
    for (let i = 0; i < 120; i++) {
      st = await ready();
      if (st.ok && st.c > 0) break;
      if (i % 10 === 0) log(`  等待中... 阶段=${st.stage} 殖民者=${st.c}`);
      await new Promise((r) => setTimeout(r, 2000));
    }
    record('地图稳定可用 + 殖民者就绪', st.ok && st.c > 0, `阶段=${st.stage}，${st.c} 人`);
    await clearForcePause();

    const health = await call('foodprobe/health');
    const modLoaded = health?.modLoaded === true;
    record('mod 已加载', modLoaded, modLoaded ? '' : health?.message);
    if (!modLoaded) throw new Error('mod 未加载');

    // ── 1. 域注册：应该有 food 和 stockpile 两个 ──
    log('\n=== 1. 域注册 ===');
    const dom = await call('foodprobe/domains');
    const ids = (dom?.domains || []).map((d) => d.id);
    record('domains 工具可调用', dom?.success === true, dom?.message || '');
    record('注册了 food 域', ids.includes('food'), `已注册：${ids.join(',')}`);
    record('★ 注册了 stockpile 域', ids.includes('stockpile'), `已注册：${ids.join(',')}`);

    // ── 2. 基线：现在有几个储藏区 ──
    log('\n=== 2. 基线 ===');
    const before = await call('foodprobe/list_stockpiles');
    record('list_stockpiles 可调用', before?.success === true, before?.message || '');
    log(`  基线：${before?.count} 个储藏区，共 ${before?.totalCells} 格`);

    // ── 3. ★ 跑一次储藏区逻辑 ──
    log('\n=== 3. 跑储藏区逻辑 ===');
    const run = await call('foodprobe/run_stockpile');
    record('run_stockpile 可调用', run?.success === true,
      (run?.result || run?.message || '').slice(0, 260));
    log(`  结果：${run?.result || run?.message}`);

    // ── 4. ★★ 桥接独立回读 ──
    log('\n=== 4. ★ 桥接独立回读（不信 mod 自述）===');
    const after = await call('foodprobe/list_stockpiles');
    const countAfter = after?.count ?? 0;
    const cellsAfter = after?.totalCells ?? 0;
    log(`  回读：${countAfter} 个储藏区，共 ${cellsAfter} 格`);
    if (after?.zones?.length) {
      for (const z of after.zones) {
        log(`    区#${z.zoneId} ${z.cells} 格 registered=${z.registered}`
          + ` 允许常见物品=${z.acceptsCommonItems}(${z.allowedProbeCount}) 首格=${JSON.stringify(z.firstCell)}`);
      }
    }

    record('★★ 地图上真的有 Zone_Stockpile（格数 > 0）',
      countAfter > 0 && cellsAfter > 0,
      `${countAfter} 个区 / ${cellsAfter} 格`);

    record('★ 区被登记进 zoneManager（registered=true）',
      (after?.zones || []).every((z) => z.registered === true),
      JSON.stringify((after?.zones || []).map((z) => ({ id: z.zoneId, reg: z.registered }))));

    record('★ filter 允许常见物品（不然小人不会往里放）',
      (after?.zones || []).some((z) => z.acceptsCommonItems === true),
      (after?.zones || []).map((z) => `#${z.zoneId}:${z.allowedProbeCount}`).join(' '));

    // ★★ 关键：不能是"把整张地图划成一个区"
    const oversize = (after?.zones || []).filter((z) => z.cells > MAX_REASONABLE_ZONE_CELLS);
    record('★★ 没有"整张地图切两半"式的巨型区（资源附近的小区才对）',
      oversize.length === 0,
      oversize.length === 0
        ? `最大区 ${Math.max(0, ...(after?.zones || []).map((z) => z.cells))} 格（上限 ${MAX_REASONABLE_ZONE_CELLS}）`
        : `发现超大区：${oversize.map((z) => `#${z.zoneId}=${z.cells}格`).join(',')} —— 候选格权重八成又没起作用`);

    // ── 5. 幂等：再跑一次不应无限增殖 ──
    log('\n=== 5. 幂等 / 上限 ===');
    const again = await call('foodprobe/run_stockpile');
    const after2 = await call('foodprobe/list_stockpiles');
    record('重复调用不报错', again?.success === true, (again?.result || '').slice(0, 160));
    record('区数不会无限增殖（有 MaxZones 上限）',
      (after2?.count ?? 0) <= 3,
      `${after2?.count} 个区（上限设的是 2）`);
  } finally {
    client.close();
  }

  const passed = steps.filter((s) => s.ok === true).length;
  const failed = steps.filter((s) => s.ok === false).length;
  const skipped = steps.filter((s) => s.ok === null).length;

  fs.mkdirSync(RUN_DIR, { recursive: true });
  fs.writeFileSync(path.join(RUN_DIR, 'report.json'),
    JSON.stringify({ passed, failed, skipped, steps }, null, 2), 'utf8');

  const md = [
    '# S3 验证报告：储藏区域',
    '',
    `时间：${new Date().toISOString()}`,
    `结果：**通过 ${passed} / 失败 ${failed} / 跳过 ${skipped}**`,
    '',
    '| 结果 | 项目 | 细节 |',
    '| --- | --- | --- |',
    ...steps.map((s) => `| ${s.ok === null ? '—' : s.ok ? '✅' : '❌'} | ${s.label} | ${(s.detail || '').replace(/\|/g, '\\|')} |`),
    '',
    '> 判定原则：**只有 `success` 不算数**，必须桥接回读确认地图上真的出现了 Zone_Stockpile。',
    `> 单区格数上限 ${MAX_REASONABLE_ZONE_CELLS} 是为了抓"把整张地图切成两半"那个 bug。`,
  ].join('\n');
  fs.writeFileSync(path.join(RUN_DIR, 'report.md'), md, 'utf8');

  log(`\n=== 通过 ${passed} / 失败 ${failed} / 跳过 ${skipped} ===`);
  log(`报告: ${path.join(RUN_DIR, 'report.md')}`);
  process.exitCode = failed === 0 ? 0 : 1;
}

main().catch((e) => {
  console.error('\n[错误] ' + e.message);
  process.exitCode = 1;
});
