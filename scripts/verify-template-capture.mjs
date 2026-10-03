/**
 * 样板捕捉链路自测：造一个**已知形状**的方盒 → 扫描 → 转成样板 → 验证分类正确。
 *
 * ## 为什么要先自测
 *
 * 用户接下来要在游戏里手造样板房给我扫。**如果他造完发现我扫不了，那是白费他的时间。**
 * 所以先用一个"我知道答案"的目标把链路验通：
 *
 *   造 13×13 方盒，四边中心各一扇门
 *   → 期望扫到：外圈 48 格 = 44 墙 + 4 门；内部 121 格都是空地
 *   → 转换后：样板尺寸 13×13、4 个门槽、44 个墙槽
 *
 * 这是"**用已知答案验证工具**"，而不是"跑一遍看看有没有报错"。
 *
 * ⚠ 只**附着**到已在运行的游戏，自己绝不启动游戏。
 * 用法：node scripts/verify-template-capture.mjs
 */
import fs from 'node:fs';
import path from 'node:path';
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor, WS } from '../src/launcher.js';

const PROFILE = 'vanilla';
const CALL = 120_000;
const RUN_DIR = path.join(WS, 'runs', 'template-capture-selftest');

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

async function ready() {
  const g = await call('rimworld/get_game_info');
  if (g?.status !== 'game_loaded') return { ok: false, stage: 'not_loaded', c: 0 };
  if ((g?.mapCount ?? 0) <= 0) return { ok: false, stage: 'no_map', c: 0 };
  const ui = await call('rimworld/get_ui_state');
  if (ui?.programState !== 'Playing') return { ok: false, stage: 'programState=' + ui?.programState, c: 0 };
  if ((g?.ticksGame ?? 0) <= 0) return { ok: false, stage: 'ticks=0', c: 0 };
  return { ok: true, stage: 'playing', c: 1, ticks: g?.ticksGame ?? 0 };
}

async function main() {
  const info = readBridgeInfo(logPathFor(PROFILE));
  if (!info) throw new Error('没有桥接信息 —— 先 node src/cli.js start（后台任务托管）');
  const conn = await connectGabp(info, { requestTimeoutMs: CALL });
  client = conn.client;
  log(`桥接 ${info.host}:${info.port}`);

  try {
    // ── 0. 就绪 ──
    let st = await ready();
    if (st.stage === 'not_loaded' || st.stage === 'no_map') {
      log('开调试殖民地（超时预期）...');
      await call('rimworld/start_debug_game_ready',
        { readiness: 'mapData', pauseIfNeeded: true, timeoutMs: 240_000 }, 280_000,
      ).catch((e) => log('  ' + e.message.split('\n')[0]));
    }
    for (let i = 0; i < 120; i++) {
      st = await ready();
      if (st.ok) break;
      if (i % 10 === 0) log(`  等待中... 阶段=${st.stage}`);
      await new Promise((r) => setTimeout(r, 2000));
    }
    record('地图稳定（programState=Playing 且 ticks>0）', st.ok, `阶段=${st.stage}`);

    // ── 1. 造一个已知的 13×13 方盒（四门）──
    const SIZE = 13;
    const X = 100, Z = 100;      // 找个不太可能撞到东西的位置
    log(`\n=== 1. 造测试方盒 ${SIZE}x${SIZE} @ (${X},${Z})，四边中心各一门 ===`);
    const built = await call('foodprobe/build_box_instant', {
      x: X, z: Z, size: SIZE, doors: true, stuffDefName: 'WoodLog',
    }, 90000);
    record('方盒生成调用完成', built?.success !== false,
      JSON.stringify(built).slice(0, 260));

    // ── 2. 扫描它（框大一点，验证"自动裁边"）──
    log('\n=== 2. 扫描（故意框大 2 格，验证自动裁边）===');
    const cap = await call('foodprobe/capture_grid', {
      x: X - 2, z: Z - 2, w: SIZE + 4, h: SIZE + 4,
    }, 90000);
    record('capture_grid 可调用', cap?.success === true, cap?.message || '');
    if (cap?.success !== true) throw new Error('扫描失败：' + (cap?.message || '?'));

    log(`  统计：${JSON.stringify(cap.counts)}`);
    if (cap.warnings?.length) for (const w of cap.warnings) log(`  ⚠ ${w}`);

    record('★ 自动裁边：裁剪后尺寸 = 13x13',
      cap.trim?.width === SIZE && cap.trim?.height === SIZE,
      `trim=${cap.trim?.width}x${cap.trim?.height} @ (${cap.trim?.offsetX},${cap.trim?.offsetZ})`);

    // ── 3. 逐格核对（这是"已知答案"验证）──
    log('\n=== 3. 逐格核对（期望：外圈 48 = 44 墙 + 4 门；内部全空地）===');
    const rows = cap.trimmedRows || [];
    let wallN = 0, doorN = 0, otherN = 0, innerNonEmpty = 0;
    for (let z = 0; z < rows.length; z++) {
      for (let x = 0; x < rows[z].length; x++) {
        const ch = rows[z][x];
        const onEdge = (x === 0 || x === SIZE - 1 || z === 0 || z === SIZE - 1);
        if (ch === 'W') { wallN++; if (!onEdge) innerNonEmpty++; }
        else if (ch === 'D') { doorN++; if (!onEdge) innerNonEmpty++; }
        else if (ch !== '.') { otherN++; if (!onEdge) innerNonEmpty++; }
      }
    }
    log(`  墙 ${wallN} / 门 ${doorN} / 其他 ${otherN}；内部非空 ${innerNonEmpty}`);

    record('★★ 外圈 = 48 格（13×13 的周长）', wallN + doorN === 48,
      `墙 ${wallN} + 门 ${doorN} = ${wallN + doorN}`);
    record('★★ 门 = 4 扇（四边中心各一）', doorN === 4, `门 ${doorN}`);
    record('★★ 墙 = 44 格（48 − 4 门）', wallN === 44, `墙 ${wallN}`);
    record('★ 内部 121 格都是空地（方盒里没放家具）', innerNonEmpty === 0,
      `内部非空 ${innerNonEmpty}`);

    // 门是否在四条边的中点
    const mid = Math.floor(SIZE / 2);
    const doorPositions = [];
    for (let z = 0; z < rows.length; z++)
      for (let x = 0; x < rows[z].length; x++)
        if (rows[z][x] === 'D') doorPositions.push([x, z]);
    const expected = [
      [mid, 0], [mid, SIZE - 1], [0, mid], [SIZE - 1, mid],
    ];
    const allMatch = expected.every(([ex, ez]) =>
      doorPositions.some(([dx, dz]) => dx === ex && dz === ez));
    record('★★ 4 扇门都在四条边的中点', allMatch,
      `实际门位 ${JSON.stringify(doorPositions)}，期望 ${JSON.stringify(expected)}`);

    // ── 4. 转成样板并核对角色 ──
    log('\n=== 4. 转成样板 ===');
    const { default: _ } = { default: null };
    // 直接内联转换逻辑（与 template-capture.mjs 同源，避免跨模块耦合）
    const symToRole = {};
    for (const g of cap.legend || []) symToRole[g.symbol] = g.role;
    const roles = {};
    const used = new Set();
    for (const r of rows) for (const ch of r) if (ch !== '.') used.add(ch);
    for (const sym of used) roles[sym] = { role: symToRole[sym] || 'unknown' };

    record('★ 扫描给出的角色分类正确（W=wall, D=door）',
      roles['W']?.role === 'wall' && roles['D']?.role === 'door',
      JSON.stringify(roles));

    const tpl = {
      id: 'selftest-box13',
      size: { w: rows[0].length, h: rows.length },
      rows,
      roles,
    };
    const outDir = path.join(WS, 'runs', 'template-capture-selftest');
    fs.mkdirSync(outDir, { recursive: true });
    fs.writeFileSync(path.join(outDir, 'captured.json'), JSON.stringify({ cap, tpl }, null, 2), 'utf8');
    log(`  原始结果已存 runs/template-capture-selftest/captured.json`);

    // 顺便把形状打印出来，人能核
    log('\n  扫描到的形状：');
    for (const r of rows) log('    ' + r);
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
    '# 样板捕捉链路自测',
    '',
    `时间：${new Date().toISOString()}`,
    `结果：**通过 ${passed} / 失败 ${failed} / 跳过 ${skipped}**`,
    '',
    '> 用「已知答案」验证：造一个 13×13 四门方盒，看扫出来对不对。',
    '> 判据是硬数字（48 外圈 = 44 墙 + 4 门、4 扇门在中点），不是"有没有报错"。',
    '',
    '| 结果 | 项目 | 细节 |',
    '| --- | --- | --- |',
    ...steps.map((s) => `| ${s.ok === null ? '—' : s.ok ? '✅' : '❌'} | ${s.label} | ${(s.detail || '').replace(/\|/g, '\\|')} |`),
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
