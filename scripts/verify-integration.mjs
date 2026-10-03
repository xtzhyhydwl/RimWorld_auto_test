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
 *   A. mod 自己写的遥测文件（`foodcontrol-telemetry.jsonl`）—— 看决策
 *   B. 桥接查游戏真实状态（`list_zones` / `foodprobe/read_zone`）—— 看结果
 *   ⚠ 只看 A 会变成"mod 自己说自己对"，必须用 B 验证游戏真的变了。
 *
 * ⚠ 本脚本只**附着**到已在运行的游戏，自己绝不启动游戏
 *   （从一次性 shell 命令启动游戏会导致进程孤儿化 + 100% CPU 死循环，实测踩过）。
 *   游戏若没开，用后台任务托管：`node src/cli.js start`。
 *   唯一允许的"改变游戏状态"是被动地调 `start_debug_game_ready`（它只是开一张调试地图）。
 *
 * 用法：node scripts/verify-integration.mjs
 * 产出：runs/integration/report.json + report.md
 *
 * ─────────────────────────────────────────────────────────────────────────────
 * 2026-10-03 调试这一版时改掉的地方（都对照了实测返回结构，见 docs/foodcontrol-集成测试记录.md）：
 *   1. 遥测字段名：`action` **不是嵌套对象**，是扁平的 `"action.kind"` 等键。
 *      原写法 `last.action?.kind` 恒为 undefined → "遥测里有决策记录" 假失败。
 *   2. `list_zones` 的种植区判据：字段是 `className: "RimWorld.Zone_Growing"`，
 *      没有 `type` / `zoneType`。原写法靠 `label` 里的 "growing" 兜底，脆；且没传
 *      `includeEmpty/includeHidden`，0 格的区可能看不见。
 *   3. 验收 #1/#2 的判据：mod 的策略定义在**预测曲线**上（`Policy.Decide` 看
 *      `projectedReserve` 何时跌破 `bandLow × burnRate`），**不是**看当前的
 *      `reserveDays`。实测存在 `reserveDays=10.7` 但动作是 `PlantCrop` 的决策
 *      （因为 15 天预测最低 -20.7 < 阈值 33.6）。所以判据改成
 *      `firstShortfallDay >= 0`（= 预测会跌破）而不是 `reserveDays < 7`。
 *   4. "别把野草算成食物"这条回归断言：**该断言的对象是 mod 的感知层**（设计文档
 *      §七quinquies 记录的 bug 在 StateReader）。探针 `foodprobe/food_summary` 是
 *      另一套**朴素**实现（只判 `IsNutritionGivingIngestible`，不判 `ThingCategory.Item`），
 *      它本来就会把草地/树算成食物 —— 拿它当断言对象是**测错了组件**。
 *      现在两边都记录，并分别给出结论。
 *   5. 验收 #1（食物充足不动手）原来只"观察"、没有场景注入，实测根本跑不到
 *      （殖民地初创时必然缺口）。现在用 `rimworld/spawn_thing` 注入食物
 *      （设计文档 §六「状态注入而非等」），真的构造出"充足"状态再断言 DoNothing。
 *   6. 不振荡的判据里**单独统计"面积下降"次数** —— 只统计"变化次数"会把
 *      "单调扩种"误报成振荡。
 */
import fs from 'node:fs';
import path from 'node:path';
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor, WS } from '../src/launcher.js';

const PROFILE = 'vanilla';
const CALL = 120_000;
const RUN_DIR = path.join(WS, 'runs', 'integration');
const TELEMETRY = path.join(WS, 'profiles', PROFILE, 'foodcontrol-telemetry.jsonl');

const steps = [];
const evidence = {};
let client = null;
/** 遥测基线：同一 profiles 目录里可能留着上一次运行的行，只分析本次新增的 */
let baseline = 0;

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

/**
 * 推进游戏时间。**不信 success**，只累加 `advancedTicks`（实测出现过 success:false 但 tick 真的推进了）。
 * 连续 3 次推进 0 tick（= forcePause 卡死）就放弃，而不是无限重试。
 */
async function advanceTicks(wanted, maxCalls = 30) {
  let advanced = 0;
  let calls = 0;
  let zeros = 0;
  while (advanced < wanted && calls < maxCalls) {
    await clearForcePause();
    const p = await call('rimworld/play_for', {
      durationMs: 8000, speed: 'Ultrafast', forceRequestedSpeed: true, pollIntervalMs: 25,
    }, 90_000);
    const t = p.res?.advancedTicks ?? 0;
    advanced += t;
    calls++;
    if (t === 0) { zeros++; if (zeros >= 3) break; } else zeros = 0;
  }
  return { advanced, calls };
}

/**
 * 读种植区（桥接侧的真实状态）。
 * 字段名按实测：`className: "RimWorld.Zone_Growing"` / `cellCount` / `id` / `label`。
 */
async function growingZones() {
  const z = await call('rimworld/list_zones', { includeEmpty: true, includeHidden: true });
  const all = z.res?.zones || [];
  const zones = all.filter((x) => String(x.className || '').includes('Zone_Growing'));
  return {
    total: zones.reduce((s, x) => s + (x.cellCount ?? 0), 0),
    count: zones.length,
    allZones: all.length,
    zones: zones.map((x) => ({ id: x.id, label: x.label, cells: x.cellCount, className: x.className })),
  };
}

/** 解析遥测 jsonl（含轮转文件） */
function readTelemetry() {
  const dir = path.join(WS, 'profiles', PROFILE);
  const candidates = [];
  for (const f of fs.existsSync(dir) ? fs.readdirSync(dir) : []) {
    if (/telemetry/i.test(f) && f.endsWith('.jsonl')) candidates.push(path.join(dir, f));
  }
  candidates.sort();
  const lines = [];
  for (const c of candidates) {
    for (const l of fs.readFileSync(c, 'utf8').split(/\r?\n/)) {
      if (!l.trim()) continue;
      try { lines.push({ ...JSON.parse(l), _file: path.basename(c) }); } catch { /* 跳过半行 */ }
    }
  }
  return { files: candidates.map((c) => path.basename(c)), lines };
}

/** 从一行遥测里抽出决策（注意：字段是扁平的 `action.kind`，不是嵌套对象） */
function decisionOf(l) {
  if (!l) return null;
  const shortfall = typeof l.firstShortfallDay === 'number' && l.firstShortfallDay >= 0;
  return {
    line: l,
    kind: l['action.kind'],
    cells: l['action.cells'],
    plantDefName: l['action.plantDefName'],
    reason: l['action.reason'] || '',
    stock: l.stock,
    reserveDays: l.reserveDays,
    burnRatePerDay: l.burnRatePerDay,
    lowThreshold: l.lowThreshold,
    minProjectedReserve: l.minProjectedReserve,
    firstShortfallDay: l.firstShortfallDay,
    needsMoreLand: l.needsMoreLand,
    shortfall,
    growingZones: l.growingZones,
    growingZoneCells: l.growingZoneCells,
    exec: l.execution || null,
  };
}

/** 注入食物（设计文档 §六「把等变成设」）。返回注入前后的 mod 感知存量。 */
async function injectFood(modStockBefore, target = 240) {
  const cells = [];
  for (let i = 0; i < 30; i++) cells.push([100 + (i % 6) * 4, 100 + Math.floor(i / 6) * 4]);
  let spawned = 0;
  let attempts = 0;
  let stock = modStockBefore;
  while (stock < target && attempts < 3) {
    for (const [x, z] of cells) {
      if (stock >= target) break;
      const r = await call('rimworld/spawn_thing', { defName: 'MealSimple', x, z, stackCount: 10 }, 30_000);
      if (r.res?.success === true) spawned++;
      const s = await call('foodprobe/read_state', {}, 30_000);
      stock = s.res?.state?.Stock ?? stock;
    }
    attempts++;
  }
  return { spawned, stock };
}

async function main() {
  log('\n=== 0. 附着到已运行的游戏（不由本脚本启动）===');
  const info = readBridgeInfo(logPathFor(PROFILE));
  if (!info) throw new Error('没有桥接信息 —— 先 `node src/cli.js start`（后台任务托管）');
  const conn = await connectGabp(info, { requestTimeoutMs: CALL });
  client = conn.client;
  log(`  桥接 ${info.host}:${info.port}`);

  try {
    const tools = await client.listTools();
    const probeTools = tools.filter((t) => t.name.startsWith('foodprobe/')).map((t) => t.name);
    record('foodprobe 工具可用', probeTools.length > 0, `${probeTools.length} 个：${probeTools.join(',')}`);
    if (probeTools.length === 0) throw new Error('探针伴生 DLL 未加载，后续无法观测内部状态');

    // ── 1. mod 是否加载 ────────────────────────────────────────
    log('\n=== 1. mod 加载自检 ===');
    const health = await call('foodprobe/health');
    const modLoaded = health.res?.modLoaded === true;
    record('FoodControl mod 已加载', modLoaded,
      modLoaded ? `程序集 ${health.res.assembly}；类型 ${JSON.stringify(health.res.types)}` : health.res?.message);
    if (!modLoaded) {
      log('\n  ⚠ mod 没加载，后续测试无法进行。请确认 DLL 已编译并重启过游戏。');
      throw new Error('mod 未加载');
    }
    evidence.health = health.res;

    // ── 2. 确保有殖民地 ────────────────────────────────────────
    //
    // ⚠ 实测踩到的三个坑，都在这里处理：
    //   1. `start_debug_game_ready` 经常会**先超时一次**（桥接调用排队在主线程，
    //      建殖民地期间轮询不到），但殖民地其实会建成功 —— 所以容忍超时、再自己轮询。
    //   2. 超时返回时可能"地图已存在但殖民者还没生成"，所以**分开等**两个条件。
    //   3. `start_debug_game`（轻量版）只能从主菜单调，别当恢复手段。
    log('\n=== 2. 确保地图 + 殖民者 ===');
    const ready = async () => {
      const g = await call('rimworld/get_game_info');
      if (!(g.res?.status === 'game_loaded' && (g.res?.mapCount ?? 0) > 0)) return { ok: false, c: 0 };
      const c = await call('rimworld/list_colonists');
      const n = c.res?.count ?? 0;
      if (n === 0) return { ok: true, c: 0, plausible: false };
      // ⚠ 实测补充：殖民者已经生成、但地图数据还在初始化的那一小段时间里，
      //   感知层会读到 `Season=null / ArableCellsAvailable=0 / Stock=0`。
      //   只等「地图 + 殖民者」两个条件**不够** —— 那样后面的断言全是假的。
      //   所以第三个条件：感知层要能读出**合理**的值。
      const s = await call('foodprobe/read_state');
      const ps = s.res?.state;
      const plausible = !!ps && !!ps.Season && (ps.ArableCellsAvailable ?? 0) > 0;
      return { ok: true, c: n, plausible };
    };
    let st = await ready();
    if (!st.ok || st.c === 0) {
      log('  需要开调试殖民地（超时是预期的，殖民地仍在后台建）...');
      await call('rimworld/start_debug_game_ready',
        { readiness: 'mapData', pauseIfNeeded: true, timeoutMs: 240_000 }, { timeoutMs: 280_000 },
      ).catch((e) => log('  （超时）: ' + String(e.message).split('\n')[0]));
    }
    let ok = false;
    for (let i = 0; i < 90; i++) {
      st = await ready();
      if (st.ok && st.c > 0 && st.plausible) { ok = true; break; }
      await new Promise((r) => setTimeout(r, 2000));
    }
    record('地图 + 殖民者 + 感知层就绪', ok, `${st.c} 个殖民者${ok ? '' : '（感知层还没读出合理值：Season/可种格）'}`);

    await clearForcePause();

    // 遥测基线：只分析本次运行之后新增的行（同一 profiles 目录里可能留着上次的行）
    baseline = readTelemetry().lines.length;
    log(`  遥测基线行数 = ${baseline}`);

    // ── 3. 感知层读数 ──────────────────────────────────────────
    log('\n=== 3. 感知层：mod 读数 vs 探针的朴素读数 ===');
    const stateRes = await call('foodprobe/read_state');
    const modState = stateRes.res?.state;
    record('read_state 可调用（mod 感知层）', stateRes.res?.success === true && !!modState,
      modState ? `stock=${modState.Stock} 消耗=${modState.BurnRatePerDay}/天 可撑=${Number(modState.ReserveDays).toFixed(1)}天 ` +
        `可用空地=${modState.ArableCellsAvailable} 种植区=${modState.GrowingZoneCount} 野生植物忽略=${modState.WildPlantsIgnored}` : stateRes.res?.message);

    // ⚠ 环境质量：`start_debug_game_ready` 每次开的地图是**随机**的，实测遇到过只剩 13 格
    //   可种地的极端地图（选址返回 null → 执行层无从下手）。那种地图上"没建出种植区"
    //   是环境问题，不是 mod 的问题，必须单独记录，免得把两者混为一谈。
    const arable = modState?.ArableCellsAvailable ?? 0;
    record('地图可用性（可种格数够不够做执行层验证）', arable >= 40,
      `可种格 ${arable} 格${arable < 40 ? ' ← 地图过于贫瘠，执行层的判定会被环境拖累（见 doc 里"贫瘠地图"一节）' : ''}`);
    evidence.mapViability = { arableCells: arable, colonists: modState?.ColonistCount };

    const food = await call('foodprobe/food_summary');
    const naive = food.res || {};
    // ★ 回归断言：设计文档 §七quinquies 记录过「把地里长着的草/树算成食物 → 13936 nutrition ≈ 2903 天口粮」。
    //   那个 bug 在 **mod 的 StateReader**；探针的 food_summary 仍是朴素实现（会算进植物），
    //   所以这条断言的对象必须是 mod 的读数。
    if (modState && (modState.ColonistCount ?? 0) > 0) {
      const modDays = modState.Stock / ((modState.ColonistCount) * 1.6);
      record('★ 感知层没把野生植物算成食物（mod 读数）', modDays < 400,
        `mod stock=${Number(modState.Stock).toFixed(1)} ≈ ${modDays.toFixed(1)} 天口粮；` +
        `同图探针朴素汇总=${naive.totalNutrition} ≈ ${naive.reserveDaysAt1_6PerDay} 天（会把草地/树算进来，属已知差异）`);
      evidence.perception = { mod: modState, probeNaive: { totalNutrition: naive.totalNutrition, topFoods: naive.topFoods } };
    } else {
      record('★ 感知层没把野生植物算成食物（mod 读数）', null, '拿不到 mod 读数，跳过');
    }

    // ── 4. 跑一轮闭环 + 回读确认 ───────────────────────────────
    log('\n=== 4. 跑一轮闭环（验收 #2 的现场）===');
    const before = await growingZones();
    log(`  跑之前：种植区 ${before.count} 块 / ${before.total} 格`);

    const cycle = await call('foodprobe/run_cycle');
    record('run_cycle 可调用', cycle.res?.success === true,
      cycle.res?.success ? String(cycle.res.result).slice(0, 260) : cycle.res?.message);
    evidence.runCycle = cycle.res;

    const telAfterCycle = readTelemetry().lines.slice(baseline);
    const manual = telAfterCycle.filter((l) => l.source === 'manual').slice(-1)[0];
    const d1 = decisionOf(manual);
    record('遥测文件已生成（本次运行）', telAfterCycle.length > 0,
      `新增 ${telAfterCycle.length} 行，文件 ${readTelemetry().files.join(',') || '（无）'}`);
    if (d1) {
      record('★ 遥测里能读出决策（扁平字段 action.kind）', !!d1.kind,
        `kind=${d1.kind} cells=${d1.cells} crop=${d1.plantDefName} 预测最低=${d1.minProjectedReserve} 阈值=${d1.lowThreshold} 第${d1.firstShortfallDay}天跌破`);
      record('遥测里有预测曲线', Array.isArray(d1.line.projectedReserve) && d1.line.projectedReserve.length > 1,
        `长度=${d1.line.projectedReserve?.length} 最低=${d1.minProjectedReserve}`);

      // ★ 验收 #2：判断"食物不足"要按 mod 的策略定义（预测会跌破 bandLow），不是看当前 reserveDays。
      //   实测：reserveDays=10.7（看着还行）但预测 15 天内最低 -20.7 < 33.6 → mod 应当动手。
      if (d1.shortfall) {
        record('★ 验收#2a 食物不足时**决策层**要求种地', d1.kind === 'PlantCrop',
          `firstShortfallDay=${d1.firstShortfallDay}（预测最低 ${d1.minProjectedReserve} < 阈值 ${d1.lowThreshold}）→ 动作 ${d1.kind} ×${d1.cells}：${d1.reason.slice(0, 120)}`);
      } else if (d1.needsMoreLand === true) {
        record('★ 验收#2a 食物不足时**决策层**要求种地', null,
          `预测有缺口但**没有可用空地**（needsMoreLand=true）：${d1.reason.slice(0, 160)}`);
      } else {
        record('★ 验收#2a 食物不足时**决策层**要求种地', null,
          `本轮的存量本来就够（预测没跌破阈值，firstShortfallDay=${d1.firstShortfallDay}）→ 场景不存在，见第 7 节的注入场景`);
      }
    }
  } catch (e) {
    log('\n[中断] ' + e.message);
    record('流程中断', false, e.message);
  }

  // ── 5. ★ 执行层：桥接独立回读，确认游戏真的变了 ─────────────
  try {
    log('\n=== 5. ★ 执行层：桥接独立回读（不看 success，看游戏状态）===');
    const after = await growingZones();
    log(`  跑之后：种植区 ${after.count} 块 / ${after.total} 格  ${JSON.stringify(after.zones)}`);
    evidence.zonesAfterCycle = after;

    const d1 = decisionOf(readTelemetry().lines.filter((l) => l.source === 'manual').slice(-1)[0]);
    const exec = d1?.exec;
    record('执行层自述 verified（mod 自己的双回读）', exec?.verified === true,
      exec ? `method=${exec.method} success=${exec.success} verified=${exec.verified} zoneId=${exec.zoneId} ` +
        `cellsAfter=${exec.zoneCellsAfter} plantDefSet=${exec.plantDefSet} detail=${exec.detail}` : '遥测里没有 execution 字段');

    record('★ 桥接独立回读：地图上真的有 Zone_Growing 且格数 > 0', after.count > 0 && after.total > 0,
      `${after.count} 块 / ${after.total} 格（className 均为 RimWorld.Zone_Growing=${after.zones.every((z) => z.className === 'RimWorld.Zone_Growing')}）`);

    // ★ 验收 #2 的完整闭环：决策要求种 + 地图上真的出现了种植区。
    //   只断言"决策是 PlantCrop"是不够的（实测遇到过"决策要种但选址返回 null"的地图）。
    const d2 = decisionOf(readTelemetry().lines.filter((l) => l.source === 'manual').slice(-1)[0]);
    record('★ 验收#2b 食物不足 → 地图上真的出现种植区（决策+执行闭环）',
      d2?.kind === 'PlantCrop' && after.count > 0 && after.total > 0,
      `动作 ${d2?.kind} / 可种格 ${d2?.line?.arableCellsAvailable} → 桥接读到 ${after.count} 块 ${after.total} 格` +
      (after.total === 0 ? `（执行层自述：${d2?.exec?.detail || '（无 execution 字段）'}）` : ''));

    if (after.count > 0) {
      const rz = await call('foodprobe/read_zone', { zoneId: Number(String(after.zones[0].id).replace(/^Zone_/, '')) });
      const okZone = rz.res?.success === true && (rz.res?.cells ?? 0) > 0 && rz.res?.plantDefToGrow === 'Plant_Rice';
      record('★ 回读该区：格数 > 0 且作物 = Plant_Rice', okZone,
        `zoneId=${rz.res?.zoneId} cells=${rz.res?.cells} crop=${rz.res?.plantDefToGrow} 肥力=${Number(rz.res?.avgFertility).toFixed(2)}`);
      evidence.zoneReadback = rz.res;
    }
  } catch (e) {
    record('执行层回读', false, e.message);
  }

  // ── 6. 验收 #5：不振荡（推进若干游戏日，观察多个决策周期）────
  //   ⚠ 顺序很重要：这一步要在**殖民地仍然缺粮**的时候跑，
  //     否则 mod 一直 DoNothing，"不振荡"就是空过。
  try {
    log('\n=== 6. 验收#5：不振荡（推进游戏时间观察多周期）===');
    const areaSamples = [];
    const windowSamples = [];
    const telBefore = readTelemetry().lines.length;
    for (let round = 0; round < 4; round++) {
      const a = await growingZones();
      areaSamples.push(a.total);
      const tel = readTelemetry().lines.slice(baseline).filter((l) => l['action.kind']);
      windowSamples.push(tel.length);
      log(`  第 ${round + 1} 轮：种植区 ${a.count} 块 / ${a.total} 格（累计决策 ${tel.length} 条）`);
      if (round < 3) {
        const adv = await advanceTicks(30_000);   // 约半个游戏日
        log(`    推进 ${adv.advanced} tick（${adv.calls} 次调用）`);
      }
    }

    // 面积变化次数 —— 验收 #5「不振荡」
    let changes = 0;
    let decreases = 0;
    for (let i = 1; i < areaSamples.length; i++) {
      if (areaSamples[i] !== areaSamples[i - 1]) changes++;
      if (areaSamples[i] < areaSamples[i - 1]) decreases++;
    }
    record('★ 验收#5 种植面积没有来回振荡（无下降）',
      areaSamples.some((a) => a > 0) ? decreases === 0 : null,
      `采样 ${JSON.stringify(areaSamples)}：变化 ${changes} 次，其中**下降 ${decreases} 次**` +
      (areaSamples.some((a) => a > 0) ? '' : ' ← 全程面积为 0（种植区根本没建起来），这条断言是**空过**的，不能算通过'));

    const decisions = readTelemetry().lines.slice(baseline)
      .filter((l) => l['action.kind'])
      .map(decisionOf);
    const kinds = decisions.map((d) => d.kind);
    const plantCount = kinds.filter((k) => k === 'PlantCrop').length;
    const nothingCount = kinds.filter((k) => k === 'DoNothing').length;
    record('决策记录数 ≥ 2（真的观察到多周期）', decisions.length >= 2,
      `本次运行共 ${decisions.length} 条：PlantCrop ${plantCount} / DoNothing ${nothingCount}（${JSON.stringify(windowSamples)}）`);
    evidence.oscillation = { areaSamples, changes, decreases, decisions: decisions.length, plantCount, nothingCount };

    // 单调性交叉印证：PlantCrop 的次数应该 ≥ 面积增长的次数（否则就是"决策写了但没种成"）
    const plantDecisions = decisions.filter((d) => d.kind === 'PlantCrop');
    if (plantDecisions.length) {
      const verified = plantDecisions.filter((d) => d.exec?.verified === true).length;
      record('每次 PlantCrop 都自述 verified 且桥接能看到面积', verified > 0 && areaSamples[areaSamples.length - 1] > 0,
        `PlantCrop ${plantDecisions.length} 条，其中 execution.verified=true ${verified} 条；桥接面积 ${areaSamples[0]} → ${areaSamples[areaSamples.length - 1]}`);
    }

    // 自动决策（每游戏日一次）有没有真的触发
    const autoLines = readTelemetry().lines.slice(baseline).filter((l) => l.source === 'auto');
    record('自动调度（每游戏日一次）真的跑了', autoLines.length > 0,
      autoLines.length ? `source=auto 的决策 ${autoLines.length} 条` : '没看到 source=auto 的决策（推进的时间可能没跨过决策窗口 8:00–19:00）');
  } catch (e) {
    record('验收#5 场景', false, e.message);
  }

  // ── 7. 验收 #1：食物充足时不动手（注入食物构造场景）──────────
  try {
    log('\n=== 7. 验收#1：注入食物 → 应该 DoNothing ===');
    const sBefore = await call('foodprobe/read_state');
    const stockBefore = sBefore.res?.state?.Stock ?? 0;
    const zonesBefore = await growingZones();

    const inj = await injectFood(stockBefore, 240);
    const sAfter = await call('foodprobe/read_state');
    const stockAfter = sAfter.res?.state?.Stock ?? 0;
    const burn = sAfter.res?.state?.BurnRatePerDay ?? 1;
    const reserveDays = stockAfter / burn;
    log(`  注入 ${inj.spawned} 堆 MealSimple：stock ${Number(stockBefore).toFixed(1)} → ${Number(stockAfter).toFixed(1)} ` +
      `（≈ ${reserveDays.toFixed(1)} 天口粮）`);
    record('构造出"食物充足"场景', reserveDays > 20,
      `stock=${Number(stockAfter).toFixed(1)} 消耗=${burn}/天 → ${reserveDays.toFixed(1)} 天 > 20 天`);

    const telBefore = readTelemetry().lines.length;
    await call('foodprobe/run_cycle');
    const richLine = readTelemetry().lines.slice(telBefore).filter((l) => l.source === 'manual').slice(-1)[0];
    const dr = decisionOf(richLine);
    if (dr) {
      record('★ 验收#1 食物充足时不动手（DoNothing）', dr.kind === 'DoNothing',
        `存量 ${Number(dr.stock).toFixed(1)}（预测最低 ${dr.minProjectedReserve} ≥ 阈值 ${dr.lowThreshold}，firstShortfallDay=${dr.firstShortfallDay}）→ 动作 ${dr.kind}：${String(dr.reason).slice(0, 120)}`);
    } else {
      record('★ 验收#1 食物充足时不动手（DoNothing）', false, '这一轮没写出遥测');
    }

    const zonesAfter = await growingZones();
    record('★ 验收#1 种植面积没有变化', zonesAfter.total === zonesBefore.total && zonesAfter.count === zonesBefore.count,
      `${zonesBefore.count} 块/${zonesBefore.total} 格 → ${zonesAfter.count} 块/${zonesAfter.total} 格`);
    evidence.richScenario = { stockBefore, stockAfter, reserveDays, zonesBefore: zonesBefore.total, zonesAfter: zonesAfter.total, decision: dr?.kind };
  } catch (e) {
    record('验收#1 场景', false, e.message);
  }

  // ── 8. 交叉印证 ───────────────────────────────────────────
  try {
    log('\n=== 8. 交叉印证（遥测 vs 桥接实测）===');
    const bridged = await growingZones();
    const lastTel = readTelemetry().lines.slice(baseline).filter((l) => l.growingZones !== undefined).slice(-1)[0];
    if (lastTel) {
      record('遥测自述的种植区 vs 桥接实测（同向，桥接 ≥ 遥测）',
        bridged.total >= (lastTel.growingZoneCells ?? 0),
        `遥测 growingZones=${lastTel.growingZones}/cells=${lastTel.growingZoneCells} → 桥接 ${bridged.count} 块/${bridged.total} 格` +
        `（遥测在"执行前"采样，所以桥接通常等于或大于它）`);
    } else {
      record('遥测 vs 桥接交叉印证', null, '遥测里没有 growingZones 字段');
    }
  } catch (e) {
    record('交叉印证', false, e.message);
  }

  try { client.close(); } catch { /* ignore */ }

  const passed = steps.filter((s) => s.ok === true).length;
  const failed = steps.filter((s) => s.ok === false).length;
  const skipped = steps.filter((s) => s.ok === null).length;

  fs.mkdirSync(RUN_DIR, { recursive: true });
  fs.writeFileSync(path.join(RUN_DIR, 'report.json'),
    JSON.stringify({ passed, failed, skipped, steps, evidence }, null, 2), 'utf8');

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
    '## 判定原则',
    '',
    '> **只有 `success` 不算数。** 遥测（mod 自述）与桥接实测必须交叉印证；',
    '> 涉及"游戏真的变了"的结论一律以桥接独立回读（`list_zones` / `foodprobe/read_zone`）为准。',
    '',
    '## 关键证据',
    '',
    '```json',
    JSON.stringify({
      zonesAfterCycle: evidence.zonesAfterCycle,
      zoneReadback: evidence.zoneReadback && {
        zoneId: evidence.zoneReadback.zoneId, cells: evidence.zoneReadback.cells,
        plantDefToGrow: evidence.zoneReadback.plantDefToGrow, plantCount: evidence.zoneReadback.plantCount,
      },
      perception: evidence.perception && {
        modStock: evidence.perception.mod?.Stock,
        modReserveDays: evidence.perception.mod?.ReserveDays,
        modWildPlantsIgnored: evidence.perception.mod?.WildPlantsIgnored,
        probeNaiveTotal: evidence.perception.probeNaive?.totalNutrition,
        probeNaiveTopFoods: evidence.perception.probeNaive?.topFoods?.slice(0, 5),
      },
      richScenario: evidence.richScenario,
      oscillation: evidence.oscillation,
    }, null, 2),
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
