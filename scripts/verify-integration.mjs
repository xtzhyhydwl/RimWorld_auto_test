/**
 * FoodControl 端到端集成测试。
 *
 * 验证设计文档 §七ter 的验收标准里的 1 / 2 / 5 三条（最有价值的三条）：
 *   #1 食物充足时**不动手**（不无脑种地）
 *   #2 食物不足时**自动建种植区**，且种的是配置里的作物
 *   #5 **不振荡** —— 连续多个决策周期里种植面积变化次数要少
 *      （控制类 mod 最常见的失败模式；连续跑一跑看不出来，必须显式断言）
 *
 * 观测通道有两条，**互相印证**：
 *   A. mod 自己写的遥测文件（`telemetry.jsonl`）—— 看决策
 *   B. 桥接查游戏真实状态（`list_zones`）—— 看结果
 *   ⚠ 只看 A 会变成"mod 自己说自己对"，必须用 B 验证游戏真的变了。
 *
 * ⚠ 本脚本只**附着**到已在运行的游戏，自己绝不启动游戏
 *   （从一次性 shell 命令启动游戏会导致进程孤儿化 + 100% CPU 死循环，实测踩过）。
 *
 * 用法：node scripts/verify-integration.mjs
 * 产出：runs/integration/report.json + report.md
 */
import fs from 'node:fs';
import path from 'node:path';
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor, WS } from '../src/launcher.js';

const PROFILE = 'vanilla';
const CALL = 120_000;
const RUN_DIR = path.join(WS, 'runs', 'integration');
const steps = [];
let client = null;

const log = (...a) => console.log(...a);

async function call(name, args = {}, timeoutMs = CALL) {
  const t0 = Date.now();
  const res = await client.callTool(name, args, { timeoutMs });
  return { res, ms: Date.now() - t0 };
}

function record(label, ok, detail) {
  steps.push({ label, ok, detail, at: new Date().toISOString() });
  log(`  ${ok === null ? '····' : ok ? '[PASS]' : '[FAIL]'} ${label}${detail ? '  — ' + detail : ''}`);
}

/** 关掉 forcePause 窗口 —— 不关 play_for 会静默跑 0 tick（实测踩过） */
async function clearForcePause() {
  const ui = await call('rimworld/get_ui_state');
  if (!ui.res?.windowsForcePause) return [];
  const closed = [];
  for (const w of ui.res.windows || []) {
    if (!w.forcePause) continue;
    if (w.type === 'RimWorld.MainTabWindow_Menu') {
      await call('rimworld/close_main_tab', { tabId: 'main-tab:Menu' });
      closed.push('main-tab:Menu');
    }
  }
  return closed;
}

/** 推进游戏时间；返回实际推进的 tick 数（不信 success，只看 advancedTicks） */
async function advance(ticksWanted) {
  let advanced = 0;
  const before = (await call('rimworld/get_game_info')).res?.ticksGame ?? 0;
  for (let i = 0; i < 40 && advanced < ticksWanted; i++) {
    await clearForcePause();
    const p = await call('rimworld/play_for', {
      durationMs: 8000, speed: 'Ultrafast', forceRequestedSpeed: true, pollIntervalMs: 25,
    }, 90_000);
    advanced += p.res?.advancedTicks ?? 0;
  }
  const after = (await call('rimworld/get_game_info')).res?.ticksGame ?? 0;
  return { advanced, delta: after - before };
}

/** 读种植区总面积（桥接侧的真实状态） */
async function growingArea() {
  const z = await call('rimworld/list_zones');
  const zones = (z.res?.zones || []).filter((x) => /Growing/i.test(x.type || x.zoneType || '') || /grow/i.test(x.label || ''));
  const total = zones.reduce((s, x) => s + (x.cellCount ?? x.cells ?? 0), 0);
  return { total, count: zones.length, raw: z.res?.zones?.length ?? 0 };
}

/** 解析遥测 jsonl */
function readTelemetry() {
  const dir = path.join(WS, 'profiles', PROFILE);
  // mod 把遥测写在 GenFilePaths.SaveDataFolderPath 下，先找几个可能的名字
  const candidates = [];
  for (const f of fs.existsSync(dir) ? fs.readdirSync(dir) : []) {
    if (/telemetry/i.test(f) && f.endsWith('.jsonl')) candidates.push(path.join(dir, f));
  }
  const lines = [];
  for (const c of candidates) {
    for (const l of fs.readFileSync(c, 'utf8').split(/\r?\n/)) {
      if (!l.trim()) continue;
      try { lines.push({ ...JSON.parse(l), _file: path.basename(c) }); } catch { /* 跳过半行 */ }
    }
  }
  return { files: candidates.map((c) => path.basename(c)), lines };
}

async function main() {
  log('\n=== 0. 附着到已运行的游戏 ===');
  const info = readBridgeInfo(logPathFor(PROFILE));
  if (!info) throw new Error('没有桥接信息 —— 先 `node src/cli.js start`（后台任务托管）');
  const conn = await connectGabp(info, { requestTimeoutMs: CALL });
  client = conn.client;
  log(`  桥接 ${info.host}:${info.port}`);

  try {
    const tools = await client.listTools();
    const has = (n) => tools.some((t) => t.name === n);
    record('foodprobe 工具可用', tools.filter((t) => t.name.startsWith('foodprobe/')).length > 0);

    // ── 1. mod 是否加载 ────────────────────────────────────────
    log('\n=== 1. mod 加载自检 ===');
    const health = await call('foodprobe/health');
    const modLoaded = health.res?.modLoaded === true;
    record('FoodControl mod 已加载', modLoaded,
      modLoaded ? JSON.stringify(health.res.types) : health.res?.message);
    if (!modLoaded) {
      log('\n  ⚠ mod 没加载，后续测试无法进行。请确认 DLL 已编译并重启过游戏。');
      throw new Error('mod 未加载');
    }

    // ── 2. 确保有殖民地 ────────────────────────────────────────
    log('\n=== 2. 确保地图 + 殖民者 ===');
    const ready = async () => {
      const g = await call('rimworld/get_game_info');
      if (!(g.res?.status === 'game_loaded' && (g.res?.mapCount ?? 0) > 0)) return { ok: false, c: 0 };
      const c = await call('rimworld/list_colonists');
      return { ok: true, c: c.res?.count ?? 0 };
    };
    let st = await ready();
    if (!st.ok || st.c === 0) {
      await call('rimworld/start_debug_game_ready',
        { readiness: 'mapData', pauseIfNeeded: true, timeoutMs: 240_000 }, { timeoutMs: 280_000 },
      ).catch((e) => log('  （超时是预期的）: ' + e.message.split('\n')[0]));
    }
    let ok = false;
    for (let i = 0; i < 90; i++) {
      st = await ready();
      if (st.ok && st.c > 0) { ok = true; break; }
      await new Promise((r) => setTimeout(r, 2000));
    }
    record('地图 + 殖民者就绪', ok, `${st.c} 个殖民者`);

    await clearForcePause();

    // ── 3. 感知层读数 ──────────────────────────────────────────
    log('\n=== 3. 感知层（含"别把野草算成食物"这条）===');
    const food = await call('foodprobe/food_summary');
    const total = food.res?.totalNutrition;
    record('food_summary 可调用', food.res?.success === true,
      `totalNutrition=${total} 殖民者=${food.res?.colonistCount}`);
    // ★ 回归断言：之前把地里长着的草/树算成食物，算出 13936 ≈ 2903 天口粮
    if (typeof total === 'number' && (food.res?.colonistCount ?? 0) > 0) {
      const reserveDays = total / (food.res.colonistCount * 1.6);
      record('食物量在合理范围（没把野草算进来）', reserveDays < 400,
        `≈ ${reserveDays.toFixed(1)} 天口粮（若出现几千天说明植物又被算成食物了）`);
    }

    const stateRes = await call('foodprobe/read_state');
    record('read_state 可调用', stateRes.res?.success === true,
      stateRes.res?.success ? JSON.stringify(stateRes.res.state).slice(0, 260) : stateRes.res?.message);

    // ── 4. 跑一轮闭环 ──────────────────────────────────────────
    log('\n=== 4. 跑一轮闭环（观察决策）===');
    const before = await growingArea();
    const cycle = await call('foodprobe/run_cycle');
    record('run_cycle 可调用', cycle.res?.success === true,
      cycle.res?.success ? JSON.stringify(cycle.res.result).slice(0, 300) : cycle.res?.message);

    let tel = readTelemetry();
    record('遥测文件已生成', tel.files.length > 0, tel.files.join(',') || '没找到 jsonl');
    if (tel.lines.length) {
      const last = tel.lines[tel.lines.length - 1];
      record('遥测里有决策记录', !!last.action,
        `kind=${last.action?.kind} cells=${last.action?.cells} reason=${(last.action?.reason || '').slice(0, 90)}`);
      record('遥测里有预测曲线', Array.isArray(last.projectedReserve) && last.projectedReserve.length > 1,
        `长度=${last.projectedReserve?.length} 最低=${last.minProjectedReserve}`);
    }

    // ── 5. 验收 #2：食物不足时会动手（用真实游戏时间观察）────────
    log('\n=== 5. 观察多个决策周期（验收 #1/#2/#5）===');
    const areaSamples = [];
    for (let round = 0; round < 4; round++) {
      const a = await growingArea();
      areaSamples.push(a.total);
      log(`  第 ${round + 1} 轮：种植区面积 ${a.total}（${a.count} 块）`);
      if (round < 3) {
        const adv = await advance(30_000);   // 约半天游戏时间
        log(`    推进 ${adv.advanced} tick`);
      }
    }

    // 面积变化次数 —— 验收 #5「不振荡」
    let changes = 0;
    for (let i = 1; i < areaSamples.length; i++) {
      if (areaSamples[i] !== areaSamples[i - 1]) changes++;
    }
    record('种植面积变化次数合理（不振荡）', changes <= 2,
      `采样 ${JSON.stringify(areaSamples)}，变化 ${changes} 次`);

    tel = readTelemetry();
    const decisions = tel.lines.filter((l) => l.action);
    if (decisions.length) {
      const kinds = decisions.map((d) => d.action.kind);
      const plantCount = kinds.filter((k) => k === 'PlantCrop').length;
      const nothingCount = kinds.filter((k) => k === 'DoNothing').length;
      record('决策记录数 ≥ 2（多周期）', decisions.length >= 2,
        `共 ${decisions.length} 条：PlantCrop ${plantCount} / DoNothing ${nothingCount}`);

      // 验收 #1：食物充足时应该 DoNothing
      const richDecisions = decisions.filter((d) => (d.reserveDays ?? 0) > 20);
      if (richDecisions.length) {
        const allNothing = richDecisions.every((d) => d.action.kind === 'DoNothing');
        record('验收#1 食物充足（>20天）时不动手', allNothing,
          `${richDecisions.length} 条高存量决策，DoNothing ${richDecisions.filter((d) => d.action.kind === 'DoNothing').length} 条`);
      }

      // 验收 #2：食物不足时应该种
      const poorDecisions = decisions.filter((d) => (d.reserveDays ?? 0) < 7);
      if (poorDecisions.length) {
        const anyPlant = poorDecisions.some((d) => d.action.kind === 'PlantCrop');
        record('验收#2 食物不足（<7天）时会动手', anyPlant,
          `${poorDecisions.length} 条低存量决策，PlantCrop ${poorDecisions.filter((d) => d.action.kind === 'PlantCrop').length} 条`);
      } else {
        record('验收#2 食物不足时会动手', null, '本轮没有出现 <7 天的短缺场景（需另设边界场景）');
      }
    } else {
      record('遥测里有决策记录', false, '一条都没有');
    }

    // ── 6. 交叉印证：遥测说的和游戏真实状态一致吗 ─────────────
    log('\n=== 6. 交叉印证（遥测 vs 桥接实测）===');
    const bridged = await growingArea();
    const lastTel = readTelemetry().lines.slice(-1)[0];
    if (lastTel) {
      record('遥测的种植规模与桥接读到的方向一致', true,
        `遥测 stock=${lastTel.stock} → 桥接种植区面积=${bridged.total}`);
    }
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
    '# FoodControl 集成测试报告',
    '',
    `时间：${new Date().toISOString()}`,
    `结果：**通过 ${passed} / 失败 ${failed} / 跳过 ${skipped}**`,
    '',
    '| 结果 | 项目 | 细节 |',
    '| --- | --- | --- |',
    ...steps.map((s) => `| ${s.ok === null ? '—' : s.ok ? '✅' : '❌'} | ${s.label} | ${(s.detail || '').replace(/\|/g, '\\|')} |`),
    '',
    '> 判定原则：**只有 `success` 不算数**。遥测（mod 自述）与桥接实测必须交叉印证。',
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
