/**
 * 样板建造端到端验证：从 `templates/*.json` **真的盖出蓝图**。
 *
 * ## 验的是什么
 *
 *   ① 游戏内能读到样板库（`TemplateLibrary` 从 mod 目录的 templates/ 读）
 *   ② 域注册里有 `templatebuild`
 *   ③ 跑一次 → **桥接独立回读**地图上真的多出了蓝图
 *   ④ 蓝图数量与样板几何对得上（13×13 四门 = 44 墙 + 4 门）
 *
 * ## 判定纪律
 *
 * **不信 mod 自述**：`run_template_build` 返回的"放下 N 格"只是它的说法，
 * 必须用 `foodprobe/list_buildings` 独立数一遍蓝图，两边对账。
 *
 * ⚠ 只**附着**到已在运行的游戏，自己绝不启动游戏。
 * 用法：node scripts/verify-template-build.mjs
 */
import fs from 'node:fs';
import path from 'node:path';
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor, WS } from '../src/launcher.js';

const PROFILE = 'vanilla';
const CALL = 120_000;
const RUN_DIR = path.join(WS, 'runs', 'template-build');

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

async function countBlueprints() {
  const b = await call('foodprobe/list_buildings', {}, 60000).catch(() => null);
  if (!b || b.success === false) return null;
  return {
    total: b.blueprintBuild_total ?? 0,
    doors: b.blueprintDoor ?? 0,
    walls: b.blueprintWall ?? 0,
    byDef: b.byBuildDef ?? {},
  };
}

async function main() {
  const info = readBridgeInfo(logPathFor(PROFILE));
  if (!info) throw new Error('没有桥接信息 —— 先 node src/cli.js start（后台任务托管）');
  const conn = await connectGabp(info, { requestTimeoutMs: CALL });
  client = conn.client;
  log(`桥接 ${info.host}:${info.port}`);

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

    const health = await call('foodprobe/health');
    record('mod 已加载', health?.modLoaded === true, health?.message || '');

    // ── 1. 域注册 ──
    log('\n=== 1. 域注册 ===');
    const dom = await call('foodprobe/domains');
    const ids = (dom?.domains || []).map((d) => d.id);
    record('★ 注册了 templatebuild 域', ids.includes('templatebuild'), `已注册：${ids.join(',')}`);

    // ── 2. 样板库 ──
    log('\n=== 2. 样板库（游戏内读到了吗）===');
    const lib = await call('foodprobe/templates', {}, 60000).catch((e) => ({ success: false, message: e.message }));
    if (lib?.success === true) {
      record('★ 样板库可读', (lib.count ?? 0) > 0,
        `${lib.count} 份，来自 ${lib.loadedFrom || '?'}：${(lib.ids || []).join(',')}`);
      if (lib.errors?.length) for (const e of lib.errors) log(`  ⚠ ${e}`);
    } else {
      record('★ 样板库可读', false, lib?.message || '探针没有 templates 工具');
    }

    // ── 3. 基线蓝图数 ──
    log('\n=== 3. 基线 ===');
    const before = await countBlueprints();
    record('能读到基线蓝图数', before != null, JSON.stringify(before));
    log(`  基线：蓝图 ${before?.total}（墙 ${before?.walls} / 门 ${before?.doors}）`);

    // ── 4. ★ 跑一次样板建造（**强制模式**）──
    //
    // ⚠ 为什么必须用 force：debug 开局的殖民地**自带墙/蓝图**（实测 12~22 个），
    //   所以"已有住处就跳过"的守卫会**正确地**拒绝盖房 —— 那样就永远验不到建造本身。
    //   守卫是对的，是测试需要一条绕过它的路。
    //   （我第一版就卡在这里：脚本报"蓝图数没增加"，实际是守卫按预期工作，
    //     测的不是"能不能盖"而是"守卫拦不拦"。测错了对象。）
    log('\n=== 4. 强制跑样板建造（绕过"已有住处"守卫）===');
    const run = await call('foodprobe/run_template_build', { force: true }, 120000)
      .catch((e) => ({ success: false, message: e.message }));
    record('run_template_build(force) 可调用', run?.success === true,
      (run?.result || run?.message || '').slice(0, 320));
    log(`  结果：${run?.result || run?.message}`);

    // ── 5. ★★ 桥接独立回读 ──
    log('\n=== 5. ★ 桥接独立回读（不信 mod 自述）===');
    const after = await countBlueprints();
    log(`  回读：蓝图 ${after?.total}（墙 ${after?.walls} / 门 ${after?.doors}）`);
    log(`  按 def 分组：${JSON.stringify(after?.byDef)}`);

    const delta = (after?.total ?? 0) - (before?.total ?? 0);
    record('★★ 地图上蓝图数真的增加了', delta > 0, `+${delta}（${before?.total} → ${after?.total}）`);

    // 样板是 13×13 四门 → 期望多出 44 墙 + 4 门 = 48 个蓝图
    // （不硬断言 48：可能被拒几格，或地图上原本就有别的蓝图。给合理区间）
    record('★★ 蓝图增量与样板几何相符（期望约 48）',
      delta >= 40 && delta <= 60,
      `+${delta}（样板 13x13 四门 = 44 墙 + 4 门 = 48）`);

    // 门也必须真的放下了（不是只放下墙 —— 没有门的房子小人进不去）
    const doorDelta = (after?.doors ?? 0) - (before?.doors ?? 0);
    record('★★ 门也放下了（没有门的房子进不去）', doorDelta >= 3,
      `门 +${doorDelta}（期望 4）`);

    // 两边对账：mod 自述 vs 桥接实测
    if (run?.success === true && run.result) {
      const m = /放下 (\d+) 格/.exec(String(run.result));
      if (m) {
        const claimed = Number(m[1]);
        record('★ mod 自述的"放下格数"与桥接增量一致',
          claimed === delta,
          `mod 说 ${claimed}，桥接实测 +${delta}`);
      } else {
        record('★ mod 自述的"放下格数"与桥接增量一致', null, '自述里没解析到"放下 N 格"');
      }
    }

    // ── 6. ★ 守卫：非强制模式**不应该**再盖一间 ──
    log('\n=== 6. 守卫（这条测的是"该拦的有没有拦住"）===');
    const guarded = await call('foodprobe/run_template_build', {}, 120000)
      .catch((e) => ({ success: false, message: e.message }));
    const afterGuard = await countBlueprints();
    record('非强制模式调用不报错', guarded?.success === true, (guarded?.result || '').slice(0, 160));
    record('★ 已有住处时不再盖第二间（守卫生效）', (afterGuard?.total ?? 0) === (after?.total ?? 0),
      `${after?.total} → ${afterGuard?.total}；自述：${(guarded?.result || '').slice(0, 80)}`);
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
    '# 样板建造端到端验证',
    '',
    `时间：${new Date().toISOString()}`,
    `结果：**通过 ${passed} / 失败 ${failed} / 跳过 ${skipped}**`,
    '',
    '| 结果 | 项目 | 细节 |',
    '| --- | --- | --- |',
    ...steps.map((s) => `| ${s.ok === null ? '—' : s.ok ? '✅' : '❌'} | ${s.label} | ${(s.detail || '').replace(/\|/g, '\\|')} |`),
    '',
    '> 判定原则：**不信 mod 自述**。它说"放下 N 格"不算数，',
    '> 必须用 `list_buildings` 独立数一遍蓝图，两边对账。',
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
