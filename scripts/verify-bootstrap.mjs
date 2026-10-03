/**
 * **开局流程端到端验证** —— 用户提的"开局清单"方向的可执行形式。
 *
 * 用户原话：
 *   「至少**开局一定要做的事情是可以完全被规定出来的**，
 *    那只要做到这些事情，基本上**开局就稳了**。」
 *
 * 这个脚本就是那份"规定"的可执行版本：
 *
 *   ① 允许地图上所有物资被拾取            （M1 域：unforbid）
 *   ② 划两片储藏区：生活区 + 边缘丢弃区    （S3 域：stockpile）
 *   ③ 从样板抄一个住处                    （样板域：templatebuild）
 *
 * ## 判定纪律（本项目反复踩过的坑）
 *
 * - **每条都回读游戏状态**，不信 mod 自述的 `success`
 * - 储藏区要**分别断言两片区的 filter 语义**（生活区拒尸体、丢弃区收尸体）
 *   —— 这是用户指出的真缺陷，不能只断言"划了 2 个区"
 * - 住处要**用桥接独立数蓝图**，且**门也要真的放下**（没门的房子进不去）
 *
 * ⚠ 只**附着**到已在运行的游戏，自己绝不启动游戏。
 * 用法：node scripts/verify-bootstrap.mjs
 */
import fs from 'node:fs';
import path from 'node:path';
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor, WS } from '../src/launcher.js';

const PROFILE = 'vanilla';
const CALL = 120_000;
const RUN_DIR = path.join(WS, 'runs', 'bootstrap');

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
  if (g?.status !== 'game_loaded') return { ok: false, stage: 'not_loaded' };
  if ((g?.mapCount ?? 0) <= 0) return { ok: false, stage: 'no_map' };
  const ui = await call('rimworld/get_ui_state');
  if (ui?.programState !== 'Playing') return { ok: false, stage: 'programState=' + ui?.programState };
  if ((g?.ticksGame ?? 0) <= 0) return { ok: false, stage: 'ticks=0' };
  return { ok: true, stage: 'playing' };
}

async function main() {
  const info = readBridgeInfo(logPathFor(PROFILE));
  if (!info) throw new Error('没有桥接信息 —— 先 node src/cli.js start（后台任务托管）');
  const conn = await connectGabp(info, { requestTimeoutMs: CALL });
  client = conn.client;
  log(`桥接 ${info.host}:${info.port}`);

  const out = { at: new Date().toISOString(), stages: {} };

  try {
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
    record('地图稳定（Playing 且 ticks>0）', st.ok, `阶段=${st.stage}`);
    if (!st.ok) throw new Error('地图没就绪，无法继续');

    // ══════════════════════════════════════════════════════════════
    // 步骤 ① 允许所有物资被拾取
    // ══════════════════════════════════════════════════════════════
    log('\n=== 步骤 1/3：允许地图上所有物资被拾取 ===');
    const beforeForbid = await call('foodprobe/forbidden_stats', {}, 40000).catch(() => null);
    const setup = await call('foodprobe/run_setup', {}, 60000).catch((e) => ({ message: e.message }));
    log(`  ${setup?.result || setup?.message}`);

    const afterForbid = await call('foodprobe/forbidden_stats', {}, 40000).catch(() => null);
    record('★ 禁取物资清零（回读断言，不信自述）',
      (afterForbid?.totalForbidden ?? 999) === 0,
      `${beforeForbid?.totalForbidden} → ${afterForbid?.totalForbidden}`);
    out.stages.unforbid = { before: beforeForbid?.totalForbidden, after: afterForbid?.totalForbidden };

    // ══════════════════════════════════════════════════════════════
    // 步骤 ② 划两片储藏区
    // ══════════════════════════════════════════════════════════════
    log('\n=== 步骤 2/3：划两片储藏区（生活区 + 边缘丢弃区）===');
    const spRun = await call('foodprobe/run_stockpile', {}, 120000).catch((e) => ({ message: e.message }));
    log(`  ${spRun?.result || spRun?.message}`);

    const filters = await call('foodprobe/stockpile_filters', {}, 60000).catch(() => null);
    log(`  回读 ${filters?.zoneCount} 个区：`);
    for (const z of filters?.zones || []) {
      log(`    区#${z.zoneId} ${z.cells}格  ${z.kind}  wood=${z.acceptsWood} corpse=${z.acceptsCorpse}`);
    }

    const zones = filters?.zones || [];
    record('★ 至少划出 2 个储藏区', zones.length >= 2, `${zones.length} 个`);
    out.stages.stockpile = zones;

    // ★ 这是用户指出的真缺陷，必须逐条断言，不能只断言"有 2 个区"
    const living = zones.filter((z) => z.kind.includes('生活区'));
    const dump = zones.filter((z) => z.kind.includes('丢弃区'));

    record('★★ 有生活区', living.length >= 1, `${living.length} 个`);
    record('★★ 有丢弃区', dump.length >= 1, `${dump.length} 个`);

    if (living.length > 0) {
      const L = living[0];
      record('★★ 生活区**拒收尸体**（这正是用户指出的缺陷）', L.acceptsCorpse === false,
        `acceptsCorpse=${L.acceptsCorpse}`);
      record('★ 生活区收正常物资', L.acceptsWood === true, `acceptsWood=${L.acceptsWood}`);
    }
    if (dump.length > 0) {
      const D = dump[0];
      record('★★ 丢弃区**收尸体**', D.acceptsCorpse === true, `acceptsCorpse=${D.acceptsCorpse}`);
      record('★ 丢弃区不收正常物资', D.acceptsWood === false, `acceptsWood=${D.acceptsWood}`);
      record('★ 丢弃区够大（≥ 40 格）', D.cells >= 40, `${D.cells} 格`);
    }

    // ══════════════════════════════════════════════════════════════
    // 步骤 ③ 从样板抄一个住处
    // ══════════════════════════════════════════════════════════════
    log('\n=== 步骤 3/3：从样板抄一个住处 ===');
    const lib = await call('foodprobe/templates', {}, 60000).catch(() => null);
    record('样板库可读', (lib?.count ?? 0) > 0, `${lib?.count} 份：${(lib?.ids || []).join(',')}`);

    const bpBefore = await call('foodprobe/list_buildings', {}, 60000).catch(() => null);
    log(`  基线蓝图 ${bpBefore?.blueprintBuild_total}（墙 ${bpBefore?.blueprintWall} / 门 ${bpBefore?.blueprintDoor}）`);

    // 强制模式：debug 殖民地自带墙，守卫会（正确地）拒绝盖房
    const build = await call('foodprobe/run_template_build', { force: true }, 120000)
      .catch((e) => ({ message: e.message }));
    log(`  ${build?.result || build?.message}`);

    const bpAfter = await call('foodprobe/list_buildings', {}, 60000).catch(() => null);
    const totalDelta = (bpAfter?.blueprintBuild_total ?? 0) - (bpBefore?.blueprintBuild_total ?? 0);
    const doorDelta = (bpAfter?.blueprintDoor ?? 0) - (bpBefore?.blueprintDoor ?? 0);
    log(`  回读蓝图 ${bpAfter?.blueprintBuild_total}（墙 ${bpAfter?.blueprintWall} / 门 ${bpAfter?.blueprintDoor}）`);

    record('★★ 蓝图数真的增加了', totalDelta > 0, `+${totalDelta}`);
    record('★★ 增量与 13×13 四门样板相符（约 48）', totalDelta >= 40 && totalDelta <= 60,
      `+${totalDelta}（期望 44 墙 + 4 门）`);
    record('★★ 门也真的放下了（没门的房子进不去）', doorDelta >= 3, `门 +${doorDelta}`);

    out.stages.shelter = { totalDelta, doorDelta, before: bpBefore, after: bpAfter };

    // ══════════════════════════════════════════════════════════════
    // 汇总：开局清单三步都做成了吗
    // ══════════════════════════════════════════════════════════════
    log('\n=== 开局清单汇总 ===');
    const checks = [
      ['① 允许所有物资', (afterForbid?.totalForbidden ?? 999) === 0],
      ['② 两片区（生活区禁尸体 / 丢弃区收尸体）',
        living.length >= 1 && dump.length >= 1
        && living[0].acceptsCorpse === false && dump[0].acceptsCorpse === true],
      ['③ 从样板盖出住处（含门）', totalDelta >= 40 && doorDelta >= 3],
    ];
    for (const [name, ok] of checks) log(`  ${ok ? '✅' : '❌'} ${name}`);
    out.checklist = checks.map(([name, ok]) => ({ name, ok }));
  } finally {
    client.close();
  }

  const passed = steps.filter((s) => s.ok === true).length;
  const failed = steps.filter((s) => s.ok === false).length;
  const skipped = steps.filter((s) => s.ok === null).length;

  fs.mkdirSync(RUN_DIR, { recursive: true });
  fs.writeFileSync(path.join(RUN_DIR, 'report.json'),
    JSON.stringify({ passed, failed, skipped, steps, stages: out.stages }, null, 2), 'utf8');

  const md = [
    '# 开局流程端到端验证',
    '',
    `时间：${out.at}`,
    `结果：**通过 ${passed} / 失败 ${failed} / 跳过 ${skipped}**`,
    '',
    '> 这份脚本就是用户说的「**开局一定要做的事情可以完全被规定出来**」的可执行版本。',
    '> 判定纪律：每条都**回读游戏状态**，不信 mod 自述的 success。',
    '',
    '## 开局清单',
    '',
    '| 步骤 | 结果 |',
    '| --- | --- |',
    ...(out.checklist || []).map((c) => `| ${c.name} | ${c.ok ? '✅' : '❌'} |`),
    '',
    '## 逐条断言',
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
