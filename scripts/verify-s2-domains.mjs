/**
 * S2 验证：重构出 `INeedDomain` 域框架之后，**行为有没有变**。
 *
 * ## 这次重构的声明是「零行为变化（纯搬家）」，本脚本就是来证伪它的
 *
 * 重构前（git HEAD `13eb305`，S1）：
 *   `GameComponentTick` 里先把家务（`ColonySetup.Tick`）跑掉，然后
 *   `if (hour < 8 || hour > 19) return; if (day == _lastRunDay) return; _lastRunDay = day;`
 *   —— **一天恰好一轮**闭环。
 *
 * 重构后（工作区）：
 *   `GameComponentTick` → `NeedCoordinator.RunAllDomains(ctx)`
 *   → `FoodDomain.ShouldRunToday`（只管时段）→ `FoodDomain.RunCycle`。
 *   按天去重改由协调器的 `_lastRunDay` 字典负责。
 *
 * ## 判定纪律
 *
 * 1. **不信 success**，一律回读：遥测行、日志行、`get_game_info`。
 * 2. **区分「域路径」和「旧路径」**：两条路径写的遥测 `source` 不同 ——
 *    `FoodDomain.RunCycle` 恒写 `"auto"`；组件里遗留的 `RunCycle(map, source)`
 *    只被 `RunCycleNow()`（→ 桥接 `foodprobe/run_cycle`）用，写 `"manual"`。
 *    所以 **只有 `source=="auto"` 的行才是域框架自己跑出来的**。
 * 3. 只**附着**到已在运行的游戏，自己绝不启动游戏。
 *
 * ⚠ 用法：先 `node src/cli.js start`（长驻后台任务托管），再跑本脚本。
 */
import fs from 'node:fs';
import path from 'node:path';
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor, WS } from '../src/launcher.js';

const PROFILE = 'vanilla';
const CALL = 120_000;
const RUN_DIR = path.join(WS, 'runs', 's2-domains');
const TELEMETRY = path.join(WS, 'profiles', PROFILE, 'foodcontrol-telemetry.jsonl');
const LOGFILE = logPathFor(PROFILE);

/** 一天 = 60000 tick；调度节流是 64 tick 一个桶（`(GenTicks.TicksGame & 0x3F) == 0`） */
const TICKS_PER_DAY = 60000;
const TICK_BUCKET = 64;
/** 决策窗口 8:00–19:00 == 12 小时 == 12 * 2500 tick */
const WINDOW_TICKS = 12 * 2500;

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

/** 解析 JSONL（mod 会写出 Number.MAX_VALUE 的完整十进制展开，JS 能解析，PowerShell 不能） */
function readTelemetry() {
  if (!fs.existsSync(TELEMETRY)) return [];
  const raw = fs.readFileSync(TELEMETRY, 'utf8');
  const out = [];
  for (const line of raw.split(/\r?\n/)) {
    const s = line.trim();
    if (!s) continue;
    try {
      const o = JSON.parse(s);
      o.__line = s;
      out.push(o);
    } catch {
      out.push({ __unparsed: s });   // 解析不了也要计数，否则会漏掉"写了但坏了"的行
    }
  }
  return out;
}

const autoOf = (rows) => rows.filter((r) => r.source === 'auto');
const manualOf = (rows) => rows.filter((r) => r.source === 'manual');

/** 每天各跑了多少轮（只看域路径 "auto"）*/
function perDayCounts(rows) {
  const m = new Map();
  for (const r of autoOf(rows)) {
    if (typeof r.day !== 'number') continue;
    m.set(r.day, (m.get(r.day) || 0) + 1);
  }
  return m;
}

/** 相邻 auto 行的 tick 增量（用来判断它是不是"每个 64-tick 桶都跑了一次"）*/
function tickDeltas(rows) {
  const ticks = autoOf(rows).map((r) => r.tick).filter((t) => typeof t === 'number');
  const d = new Map();
  for (let i = 1; i < ticks.length; i++) {
    const k = ticks[i] - ticks[i - 1];
    d.set(k, (d.get(k) || 0) + 1);
  }
  return d;
}

function logLinesMatching(re) {
  if (!fs.existsSync(LOGFILE)) return [];
  const raw = fs.readFileSync(LOGFILE, 'utf8');
  return raw.split(/\r?\n/).filter((l) => re.test(l));
}

async function clearForcePause() {
  const ui = await call('rimworld/get_ui_state');
  if (!ui?.windowsForcePause) return false;
  log(`  ⚠ windowsForcePause=true（${(ui.windows || []).filter((w) => w.forcePause).map((w) => w.type).join(',')}）→ 关掉，否则 play_for 静默 0 tick`);
  await call('rimworld/close_main_tab', { tabId: 'main-tab:Menu' });
  return true;
}

/** 推进一步并返回真实推进的 tick 数（**不信任 success**）*/
async function advance(durationMs) {
  await clearForcePause();
  const p = await call('rimworld/play_for', {
    durationMs, speed: 'Ultrafast', forceRequestedSpeed: true, pollIntervalMs: 25,
  }, 90_000);
  return { advanced: p?.advancedTicks ?? 0, raw: p };
}

async function main() {
  const info = readBridgeInfo(logPathFor(PROFILE));
  if (!info) throw new Error('没有桥接信息 —— 先 node src/cli.js start（后台任务托管）');
  const conn = await connectGabp(info, { requestTimeoutMs: CALL });
  client = conn.client;
  log(`桥接 ${info.host}:${info.port}`);

  try {
    // ── 0. 地图稳定可用（programState==Playing 且 ticksGame>0）──────────
    const ready = async () => {
      const g = await call('rimworld/get_game_info');
      if (g?.status !== 'game_loaded') return { ok: false, stage: 'not_loaded', c: 0, ticks: 0 };
      if ((g?.mapCount ?? 0) <= 0) return { ok: false, stage: 'no_map', c: 0, ticks: 0 };
      const ui = await call('rimworld/get_ui_state');
      if (ui?.programState !== 'Playing') return { ok: false, stage: 'programState=' + ui?.programState, c: 0, ticks: 0 };
      if ((g?.ticksGame ?? 0) <= 0) return { ok: false, stage: 'ticks=0', c: 0, ticks: 0 };
      const c = await call('rimworld/list_colonists');
      return { ok: true, stage: 'playing', c: c?.count ?? 0, ticks: g.ticksGame };
    };

    let st = await ready();
    for (let i = 0; i < 60 && !(st.ok && st.c > 0); i++) {
      if (i % 5 === 0) log(`  等待地图/殖民者... 阶段=${st.stage} 殖民者=${st.c}`);
      await new Promise((r) => setTimeout(r, 2000));
      st = await ready();
    }
    record('地图稳定可用 + 殖民者就绪（programState=Playing 且 ticksGame>0）',
      st.ok && st.c > 0, `阶段=${st.stage}，${st.c} 个殖民者，ticksGame=${st.ticks}`);
    if (!st.ok) throw new Error('地图/殖民者未就绪');
    await clearForcePause();

    // ── 1. mod 加载 ───────────────────────────────────────────────
    const health = await call('foodprobe/health');
    record('FoodControl mod 已加载', health?.modLoaded === true, health?.assembly || health?.message);
    if (!health?.modLoaded) throw new Error('mod 未加载');

    // ── 2. ★ 域路径是不是真的在跑（靠遥测 source 区分）───────────────
    //
    // 只有 `source=="auto"` 的行才来自 `FoodDomain.RunCycle`（域框架）；
    // `"manual"` 来自组件里遗留的 `RunCycle`，走的是 `foodprobe/run_cycle`。
    log('\n=== 2. 域路径自动运行（不手动调，靠推进时间）===');
    let rows = readTelemetry();
    const baseTotal = rows.length;
    const baseAuto = autoOf(rows).length;
    log(`  遥测快照：共 ${baseTotal} 行，其中 auto ${baseAuto} 行 / manual ${manualOf(rows).length} 行`);

    let inWindow = false;
    let advancedTotal = 0;
    let chunk = 0;
    for (let i = 0; i < 40; i++) {
      const before = autoOf(readTelemetry()).length;
      const a = await advance(1500);
      advancedTotal += a.advanced;
      chunk++;
      const after = autoOf(readTelemetry()).length;
      if (after > before) {
        inWindow = true;
        log(`  第 ${i + 1} 次推进（${a.advanced} tick）后出现新的 auto 行：${before} → ${after}`);
        break;
      }
      if (i % 3 === 0) log(`  第 ${i + 1} 次推进：+${a.advanced} tick，还没有 auto 行（多半在窗口外）`);
      if (a.advanced === 0) { log('    ⚠ 本次推进 0 tick —— 有 forcePause 窗口或游戏卡住'); }
    }
    record('★ 食物域**自己**跑了（出现 source="auto" 的遥测行）', inWindow,
      `累计推进 ${advancedTotal} tick / ${chunk} 次`);

    // ── 3. ★ 白天窗口内的痕迹 + 每条都在 8:00–19:00 之内 ─────────────
    log('\n=== 3. ★ 域路径的白天痕迹（回读遥测内容）===');
    rows = readTelemetry();
    const autoRows = autoOf(rows);
    const fresh = autoRows.slice(baseAuto);          // 本轮新增的 auto 行
    const hours = fresh.map((r) => r.hour).filter((h) => typeof h === 'number');
    const outOfWindow = hours.filter((h) => h < 8 || h > 19);
    log(`  本轮新增 auto 行 ${fresh.length} 条；出现的 hour：${[...new Set(hours)].sort((a, b) => a - b).join(',') || '（无）'}`);
    if (fresh.length) {
      const last = fresh[fresh.length - 1];
      log(`  最后一条：tick=${last.tick} day=${last.day} hour=${last.hour} 动作=${last['action.kind']}` +
        ` 理由=${String(last['action.reason'] || '').slice(0, 60)}`);
    }
    record('★ 域路径写出的行全部落在决策窗口 8:00–19:00 内（ShouldRunToday 生效）',
      fresh.length > 0 && outOfWindow.length === 0,
      fresh.length === 0 ? '本轮没有新增 auto 行（未在窗口内）' : `越界 hour：${outOfWindow.join(',') || '无'}`);

    // ── 4. ★★ 按天去重是否成立（这是本次重构最可疑的地方）─────────────
    //
    // 重构前的实测基线：**一天恰好 1 行 auto**（历史遥测里 day 330015/16/17
    // 的 tick 分别是 245056 / 305024 / 365056，各差 60000 = 恰好一天）。
    // 所以只要某一天出现 >1 行，就说明「每天一次」这条调度没接上。
    log('\n=== 4. ★★ 按天去重（重构前基线：一天恰好 1 轮）===');
    const byDay = perDayCounts(readTelemetry());
    const worst = [...byDay.entries()].sort((a, b) => b[1] - a[1]).slice(0, 5);
    for (const [d, n] of worst) log(`  day=${d} → auto ${n} 轮`);
    const maxPerDay = worst.length ? worst[0][1] : 0;
    const deltas = tickDeltas(readTelemetry());
    const topDeltas = [...deltas.entries()].sort((a, b) => b[1] - a[1]).slice(0, 4);
    log(`  auto 行之间的 tick 增量分布（前 4）：${topDeltas.map(([k, v]) => `${k}×${v}`).join('  ')}`);
    const bucketShare = (deltas.get(TICK_BUCKET) || 0) / Math.max(1, autoRows.length - 1);
    log(`  增量为 ${TICK_BUCKET} tick（= 调度节流粒度）的比例：${(bucketShare * 100).toFixed(1)}%`);

    record('★★ 一天只跑一轮（按天去重成立）', maxPerDay <= 1,
      `单日最多 ${maxPerDay} 轮；理论窗口上限 ${Math.ceil(WINDOW_TICKS / TICK_BUCKET)} 轮/天`);

    // ── 5. run_cycle 这个"手动钩子"走的是哪条路径 ────────────────────
    //
    // 任务书假设 `foodprobe/run_cycle`（→ RunCycleNow → RunCycle）能验证域路径。
    // 静态读代码得出的结论是**不能**：它调的是组件里遗留的 `RunCycle`（source="manual"）。
    // 这里用遥测的 source 字段实测这一点。
    log('\n=== 5. run_cycle 手动钩子走的是哪条路径 ===');
    const beforeManual = manualOf(readTelemetry()).length;
    const beforeAuto2 = autoOf(readTelemetry()).length;
    const rc = await call('foodprobe/run_cycle');
    const t2 = readTelemetry();
    const afterManual = manualOf(t2).length;
    const afterAuto2 = autoOf(t2).length;
    log(`  run_cycle 返回(${(rc?.result || rc?.message || '').toString().slice(0, 110)})`);
    log(`  遥测：manual ${beforeManual}→${afterManual}，auto ${beforeAuto2}→${afterAuto2}`);
    record('run_cycle 产出的是 manual 行（即**没有**走域框架）',
      afterManual > beforeManual && afterAuto2 === beforeAuto2,
      `manual +${afterManual - beforeManual}，auto +${afterAuto2 - beforeAuto2}`);

    // ── 6. Player.log 里的 [FoodControl] 行 ─────────────────────────
    log('\n=== 6. Player.log 里的 [FoodControl] 痕迹 ===');
    const fc = logLinesMatching(/\[FoodControl\]/);
    log(`  共 ${fc.length} 行，最后 5 行：`);
    for (const l of fc.slice(-5)) log('    ' + l.trim().slice(0, 220));
    record('Player.log 里有 [FoodControl] 痕迹', fc.length > 0, `共 ${fc.length} 行`);
  } finally {
    client.close();
  }

  const passed = steps.filter((s) => s.ok === true).length;
  const failed = steps.filter((s) => s.ok === false).length;
  const skipped = steps.filter((s) => s.ok === null).length;

  // 附上关键原始数据，报告可复核
  const rows = readTelemetry();
  const byDay = perDayCounts(rows);
  const evidence = {
    telemetryFile: TELEMETRY,
    totalLines: rows.length,
    autoLines: autoOf(rows).length,
    manualLines: manualOf(rows).length,
    perDayAuto: Object.fromEntries([...byDay.entries()].sort((a, b) => Number(a[0]) - Number(b[0]))),
    tickDeltaHistogram: Object.fromEntries([...tickDeltas(rows).entries()].sort((a, b) => b[1] - a[1]).slice(0, 8)),
    lastAutoLines: autoOf(rows).slice(-6).map((r) => ({
      tick: r.tick, day: r.day, hour: r.hour, kind: r['action.kind'], cells: r['action.cells'],
      reason: r['action.reason'], exec: r.execution,
    })),
  };

  fs.mkdirSync(RUN_DIR, { recursive: true });
  fs.writeFileSync(path.join(RUN_DIR, 'report.json'),
    JSON.stringify({ passed, failed, skipped, steps, evidence }, null, 2), 'utf8');

  const md = [
    '# S2 验证报告：域框架（INeedDomain）行为是否变化',
    '',
    `时间：${new Date().toISOString()}`,
    `结果：**通过 ${passed} / 失败 ${failed} / 跳过 ${skipped}**`,
    '',
    '| 结果 | 项目 | 细节 |',
    '| --- | --- | --- |',
    ...steps.map((s) => `| ${s.ok === null ? '—' : s.ok ? '✅' : '❌'} | ${s.label} | ${(s.detail || '').replace(/\|/g, '\\|')} |`),
    '',
    '## 遥测证据',
    '',
    '```json',
    JSON.stringify(evidence, null, 2),
    '```',
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
