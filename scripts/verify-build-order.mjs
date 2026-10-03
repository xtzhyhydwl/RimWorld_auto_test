/**
 * 实测：**建造顺序 == 放置蓝图的顺序**？
 *
 * ## 为什么
 *
 * 用户（2026-10-03）：
 *   「如果我没记错的话，环世界小人执行这些命令的顺序，应该就是你**放置蓝图的顺序**。
 *    也就是说，如果你是先放的床后放的外墙……实际上就是**先造床，再再造外墙**的。」
 *
 * 这是「样板语义层 + 分段材料决策 + 建造顺序」的**承重假设**，**不能猜**。
 *
 * ## 实验设计（排除两个干扰）
 *
 * 干扰一：**位置偏好**（小人可能就近造） → 用**交叉配对**：
 *   每格都在「先放」和「后放」两个角色上各出现一次，各跑一轮。
 *   若结果跟着**放置顺序**走而不是跟着**位置**走 → 假设成立。
 *
 * 干扰二：**同 tick 完成**（两个工人都去建、同一采样间隔内都完工） →
 *   不只记录"谁先变成框架"，还记录 **Frame.workDone**（进度）：
 *   进度大的那个开始得更早。两者互相印证。
 *
 * 干扰三：**放置本身失败**（上一轮实测出现过 `count:1`、第二个失败）→
 *   每次放置都检查 `placed[i].ok`，**失败就明确报出来**，不把"没放上"当成"没先建"。
 *
 * ## 判定
 *
 *   两轮都是「先放的先建」→ 放置顺序决定 ✓
 *   两轮都由同一个**位置**先建 → 由位置决定 ✗
 *
 * ⚠ 只**附着**到已在运行的游戏，自己绝不启动/重启游戏。
 * 用法：node scripts/verify-build-order.mjs
 */
import fs from 'node:fs';
import path from 'node:path';
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor, WS } from '../src/launcher.js';

const PROFILE = 'vanilla';
const CALL = 120_000;
const RUN_DIR = path.join(WS, 'runs', 'build-order');

// 实测区域（debug 殖民地约在 100,100 附近；先探测再定）
const REGION = { x: 140, z: 140, w: 40, h: 30 };
const WALL_DEF = 'Wall';
// 建材**不写死**：实测这个 debug 殖民地开局**没有木头**（只有 Silver/Steel/ComponentIndustrial），
// 写死 WoodLog 会导致"两格都没建起来"，然后被误读成"顺序不影响"。先读库存再定。
let WALL_STUFF = null;

const PAIRS = [
  { id: 'R1', order: ['A', 'B'], cells: { A: { x: 150, z: 150 }, B: { x: 155, z: 150 } } },
  { id: 'R2', order: ['B', 'A'], cells: { A: { x: 150, z: 150 }, B: { x: 155, z: 150 } } },
  { id: 'R3', order: ['A', 'B'], cells: { A: { x: 160, z: 155 }, B: { x: 165, z: 155 } } },
  { id: 'R4', order: ['B', 'A'], cells: { A: { x: 160, z: 155 }, B: { x: 165, z: 155 } } },
];

const MAX_MS = Number(process.env.MAX_MS || 360_000); // 每轮最多跑多久（墙上时间）

let client = null;
const log = (...a) => console.log(...a);

async function call(name, args = {}, timeoutMs = CALL) {
  return client.callTool(name, args, { timeoutMs });
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const cellKey = (c) => `${c.x},${c.z}`;

async function ready() {
  const g = await call('rimworld/get_game_info');
  if (g?.status !== 'game_loaded') return { ok: false, stage: 'not_loaded' };
  if ((g?.mapCount ?? 0) <= 0) return { ok: false, stage: 'no_map' };
  const ui = await call('rimworld/get_ui_state');
  if (ui?.programState !== 'Playing') return { ok: false, stage: 'programState=' + ui?.programState };
  if ((g?.ticksGame ?? 0) <= 0) return { ok: false, stage: 'ticks=0' };
  return { ok: true, stage: 'playing', ui };
}

/**
 * 清掉 forcePause 窗口（否则 play_for 静默跑 0 tick —— 已实测的坑）。
 * 返回关闭了哪些窗口。
 */
async function clearForcePause() {
  const ui = await call('rimworld/get_ui_state');
  if (!ui?.windowsForcePause) return { needed: false, windows: [] };
  const closed = [];
  const before = ui.forcePauseWindows || [];
  await call('foodprobe/close_force_pause').catch(() => null);
  const after = await call('rimworld/get_ui_state');
  return { needed: true, windows: before, stillPaused: after?.windowsForcePause === true };
}

/** 放一组蓝图（按给定顺序），并**独立回读**确认真的放上了 */
async function placeOne(cell, stuff = null) {
  const res = await call('foodprobe/place_blueprints', {
    cellsJson: JSON.stringify([[cell.x, cell.z]]),
    defName: WALL_DEF,
    stuffDefName: stuff || WALL_STUFF,
  }).catch((e) => ({ success: false, message: e.message }));

  const item = (res?.placed || [])[0] || null;
  return {
    ok: res?.success === true && item?.ok === true,
    error: item?.error || res?.message || '没有返回 placed[0]',
    raw: res,
  };
}

/** 读矩形内每格的状态：missing / blueprint / frame / built */
async function readRect(rect) {
  const r = await call('foodprobe/frames_in_rect', rect).catch((e) => ({ success: false, message: e.message }));
  if (r?.success !== true) return { ok: false, message: r?.message, map: new Map() };

  const map = new Map();
  for (const b of r.blueprints || []) map.set(cellKey(b), { kind: 'blueprint', defName: b.defName });
  for (const f of r.frames || []) {
    map.set(cellKey(f), { kind: 'frame', defName: f.defName, workDone: f.workDone });
  }
  for (const b of r.built || []) map.set(cellKey(b), { kind: 'built', defName: b.defName });
  return { ok: true, map, frameCount: r.frameCount, blueprintCount: r.blueprintCount, builtCount: r.builtCount };
}

function snapshot(cells, rect) {
  return readRect(rect).then((r) => {
    const out = {};
    for (const [k, c] of Object.entries(cells)) {
      const st = r.map?.get(cellKey(c)) || { kind: 'missing' };
      out[k] = { ...c, ...st };
    }
    return { ok: r.ok, cells: out, message: r.message };
  });
}

/**
 * 读建材库存（材料够不够是"没建起来"的一个可能原因，必须能说出来）。
 *
 * ⚠ `foodprobe/materials` 返回的是 `{source:"mod", raw:"<一段 JSON 字符串>"}` ——
 *   数字藏在 **raw 里的 JSON 字符串**里，不是顶层字段。第一版我直接读 `counts.WoodLog`
 *   拿到 undefined，会静默用错建材。这里显式解出来。
 */
async function materialCounts() {
  const m = await call('foodprobe/materials').catch(() => null);
  let counts = m?.counts || m?.materials || null;
  if (!counts && typeof m?.raw === 'string') {
    try {
      const parsed = JSON.parse(m.raw);
      counts = parsed?.materials || parsed?.counts || null;
    } catch { /* 解不出就算了，如实返回 */ }
  }
  return { success: m?.success, source: m?.source, counts, raw: m?.raw };
}

/** 从库存里挑一种建材，并要求够 2 面墙（Wall 每面 5 单位） */
function pickStuff(counts, needUnits = 10) {
  const prefer = ['WoodLog', 'Steel', 'Silver', 'Plasteel', 'Uranium', 'Gold', 'BlocksSandstone', 'BlocksGranite'];
  for (const k of prefer) {
    if ((counts?.[k] ?? 0) >= needUnits) return k;
  }
  // 退路：任何数量够的
  const any = Object.entries(counts || {}).find(([, v]) => v >= needUnits);
  return any ? any[0] : null;
}

/** 一轮：按 order 放两格蓝图，然后观察谁先被施工 */
async function runRound(pair) {
  const label = pair.id;
  log(`\n=== ${label}：放置顺序 ${pair.order.join(' → ')}，位置 ${pair.order.map((k) => {
    const c = pair.cells[k];
    return `${k}(${c.x},${c.z})`;
  }).join('  ')}，建材 ${WALL_STUFF} ===`);

  const rect = {
    x: Math.min(pair.cells.A.x, pair.cells.B.x) - 2,
    z: Math.min(pair.cells.A.z, pair.cells.B.z) - 2,
    w: Math.abs(pair.cells.A.x - pair.cells.B.x) + 5,
    h: Math.abs(pair.cells.A.z - pair.cells.B.z) + 5,
  };

  // 前置检查：这两格现在是空的吗
  const pre = await snapshot(pair.cells, rect);
  const dirty = Object.entries(pre.cells).filter(([, c]) => c.kind !== 'missing');
  if (dirty.length) {
    log(`  ⚠ 前置不干净：${dirty.map(([k, c]) => `${k}=${c.kind}`).join(', ')} —— 本轮结果不可用`);
    return { id: label, usable: false, reason: '前置不干净：' + JSON.stringify(pre.cells) };
  }
  log('  前置干净（两格都是空的）');

  // ── 按顺序放置 ──
  const placement = [];
  for (const k of pair.order) {
    const r = await placeOne(pair.cells[k]);
    placement.push({ key: k, ...r });
    log(`  放置 ${k}(${pair.cells[k].x},${pair.cells[k].z}) → ${r.ok ? '成功' : '★ 失败：' + r.error}`);
  }
  const placeFailed = placement.filter((p) => !p.ok);
  if (placeFailed.length) {
    log(`  ★ 放置阶段就失败了 ${placeFailed.length} 个 —— 说明"没建"不是因为顺序，本轮不可用`);
    return { id: label, usable: false, reason: '放置失败', placement };
  }

  // 确认真的放上了（独立回读，不信 placed[i].ok）
  const afterPlace = await snapshot(pair.cells, rect);
  for (const k of pair.order) {
    const c = afterPlace.cells[k];
    if (c.kind !== 'blueprint') {
      log(`  ⚠ 回读 ${k} 不是蓝图（是 ${c.kind}）—— 独立回读与放置结果不一致`);
      return { id: label, usable: false, reason: '回读不一致', afterPlace: afterPlace.cells };
    }
  }
  log('  独立回读：两格都真的是蓝图 ✓');

  // ── 观察谁先被施工 ──
  const fp = await clearForcePause();
  if (fp.needed) log(`  forcePause 窗口：${(fp.windows || []).join(', ')} → 已尝试关闭`);

  const transitions = []; // {t, key, from, to}
  let prev = { A: 'blueprint', B: 'blueprint' };
  const t0 = Date.now();
  let advanced = 0;
  let lastProbe = null;
  let lastSpeed = null;

  while (Date.now() - t0 < MAX_MS) {
    const sp = await call('rimworld/set_time_speed', { speed: 'Ultrafast', ultraSpeedBoost: true, forceRequestedSpeed: true })
      .catch((e) => ({ success: false, message: e.message }));
    lastSpeed = sp?.speed ?? sp?.message ?? '?';

    const pf = await call('rimworld/play_for', { durationMs: 3000, speed: 'Ultrafast', forceRequestedSpeed: true })
      .catch((e) => ({ success: false, advancedTicks: 0, message: e.message }));
    advanced += pf?.advancedTicks ?? 0;

    const cur = await snapshot(pair.cells, rect);
    if (!cur.ok) { log(`  ⚠ 读状态失败：${cur.message}`); break; }
    lastProbe = cur;

    for (const k of ['A', 'B']) {
      const now = cur.cells[k];
      const was = prev[k];
      if (now.kind !== was) {
        transitions.push({ atMs: Date.now() - t0, advancedTicks: advanced, key: k, from: was, to: now.kind, workDone: now.workDone ?? null });
        log(`    [t=${((Date.now() - t0) / 1000).toFixed(1)}s tick+${advanced}] ${k}：${was} → ${now.kind}${now.workDone != null ? ' (workDone=' + now.workDone + ')' : ''}`);
        prev[k] = now.kind;
      }
    }
    if (prev.A === 'built' || prev.B === 'built') break; // 一个建完就够判定了

    if (advanced === 0 && Date.now() - t0 > 30_000) {
      log('  ★ 推进了 30 秒仍是 0 tick —— play_for 被挡住了，本轮拿不到数据');
      break;
    }
  }

  const final = lastProbe?.cells || afterPlace.cells;
  const result = {
    id: label,
    usable: true,
    order: pair.order,
    cells: pair.cells,
    advancedTicks: advanced,
    lastSpeed,
    transitions,
    final: Object.fromEntries(Object.entries(final).map(([k, v]) => [k, { kind: v.kind, workDone: v.workDone ?? null }])),
    placedFirst: pair.order[0],
    // 谁先被施工：第一个出现 frame/built 的
    builtFirst: (() => {
      const first = transitions.find((t) => t.to === 'frame' || t.to === 'built');
      return first ? first.key : null;
    })(),
    // 交叉印证：进度更大的先开工（同一采样窗口内）
    progressLeader: (() => {
      const fa = final.A?.workDone, fb = final.B?.workDone;
      if (fa == null && fb == null) return null;
      if (fa == null) return 'B';
      if (fb == null) return 'A';
      if (fa === fb) return null;
      return fa > fb ? 'A' : 'B';
    })(),
  };
  log(`  本轮：先放=${result.placedFirst}，先被施工=${result.builtFirst ?? '（无）'}，进度领先=${result.progressLeader ?? '（无）'}，推进 ${advanced} tick`);
  return result;
}

async function main() {
  const info = readBridgeInfo(logPathFor(PROFILE));
  if (!info) throw new Error('没有桥接信息 —— 先 node src/cli.js start（后台任务托管）');
  const conn = await connectGabp(info, { requestTimeoutMs: CALL });
  client = conn.client;
  log(`桥接 ${info.host}:${info.port}`);

  const reports = { startedAt: new Date().toISOString(), rounds: [], steps: [] };
  const steps = reports.steps;
  const record = (label, ok, detail) => {
    steps.push({ label, ok, detail });
    log(`  ${ok === null ? '····' : ok ? '[PASS]' : '[FAIL]'} ${label}${detail ? '  — ' + detail : ''}`);
  };

  try {
    const st = await ready();
    record('地图可用（Playing 且 ticks>0）', st.ok, st.stage);
    if (!st.ok) throw new Error('地图不可用：' + st.stage);

    // 基线：建材 + 现有墙/蓝图
    const mats = await materialCounts();
    reports.materials = mats;
    log(`\n建材：${JSON.stringify(mats.counts ?? mats.raw)}`);
    WALL_STUFF = pickStuff(mats.counts, 10);
    record('建材库存可读（"没建起来"是不是因为没料，必须能说出来）', mats.counts != null,
      `选中的建材 = ${WALL_STUFF ?? '（没有可用的）'}`);
    if (!WALL_STUFF) throw new Error('库存里没有足够做 2 面墙的建材 —— 先摆材料再测，否则测的是"没料"');

    for (const pair of PAIRS) {
      const r = await runRound(pair);
      reports.rounds.push(r);
      if (!r.usable) log(`  → ★ 本轮不可用（${r.reason}）`);
    }

    // ── 判定 ──
    log('\n=== 判定 ===');
    const good = reports.rounds.filter((r) => r.usable && r.builtFirst);
    record('拿到可用数据（至少 2 轮）', good.length >= 2, `${good.length} / ${PAIRS.length} 轮可用`);

    if (good.length >= 2) {
      const followOrder = good.every((r) => r.builtFirst === r.placedFirst);
      const followPosition = new Set(good.map((r) => r.builtFirst)).size === 1
        && new Set(good.map((r) => JSON.stringify(r.cells[r.builtFirst]))).size === 1;

      record('★★ 假设「先放的先建」成立', followOrder ? true : false,
        good.map((r) => `${r.id}: 先放=${r.placedFirst} 先建=${r.builtFirst}`).join(' | '));
      record('（对照）是否由**位置**决定', followPosition ? true : false,
        followPosition ? '★ 两轮都由同一个位置先建 → 位置决定，不是顺序' : '不是位置决定');

      // 交叉印证
      const progressAgrees = good.every((r) => r.progressLeader == null || r.progressLeader === r.placedFirst);
      record('进度领先者与放置顺序一致（交叉印证）', progressAgrees,
        good.map((r) => `${r.id}: 先放=${r.placedFirst} 进度领先=${r.progressLeader ?? '-'}`).join(' | '));
    } else {
      record('★★ 假设「先放的先建」成立', null, '数据不足，不能判定 —— 见每轮的 unusable 原因');
    }
  } finally {
    client.close();
  }

  const passed = steps.filter((s) => s.ok === true).length;
  const failed = steps.filter((s) => s.ok === false).length;
  const skipped = steps.filter((s) => s.ok === null).length;
  reports.finishedAt = new Date().toISOString();
  reports.passed = passed; reports.failed = failed; reports.skipped = skipped;

  fs.mkdirSync(RUN_DIR, { recursive: true });
  fs.writeFileSync(path.join(RUN_DIR, 'report.json'), JSON.stringify(reports, null, 2), 'utf8');

  const md = [
    '# 实测：建造顺序 == 放置蓝图的顺序？',
    '',
    `时间：${reports.startedAt} → ${reports.finishedAt}`,
    `结果：**通过 ${passed} / 失败 ${failed} / 跳过 ${skipped}**`,
    '',
    '## 实验设计',
    '',
    '交叉配对：同一对位置各跑两次，**交换放置顺序**。',
    '若结果跟着放置顺序走而不是跟着位置走 → 「先放的先建」成立。',
    '另用 `Frame.workDone`（进度）交叉印证：同一采样窗口内进度大的先开工。',
    '',
    '## 每轮',
    '',
    '| 轮次 | 放置顺序 | 先放 | 先被施工 | 进度领先 | 推进 tick | 可用 |',
    '| --- | --- | --- | --- | --- | --- | --- |',
    ...reports.rounds.map((r) => `| ${r.id} | ${(r.order || []).join(' → ')} | ${r.placedFirst ?? '-'} | ${r.builtFirst ?? '-'} | ${r.progressLeader ?? '-'} | ${r.advancedTicks ?? '-'} | ${r.usable ? '是' : '否：' + (r.reason || '')} |`),
    '',
    '## 断言',
    '',
    '| 结果 | 项目 | 细节 |',
    '| --- | --- | --- |',
    ...steps.map((s) => `| ${s.ok === null ? '—' : s.ok ? '✅' : '❌'} | ${s.label} | ${(s.detail || '').replace(/\|/g, '\\|')} |`),
    '',
    '## 原始转移记录',
    '',
    '```json',
    JSON.stringify(reports.rounds.map((r) => ({ id: r.id, transitions: r.transitions, final: r.final })), null, 2),
    '```',
  ].join('\n');
  fs.writeFileSync(path.join(RUN_DIR, 'report.md'), md, 'utf8');

  log(`\n=== 通过 ${passed} / 失败 ${failed} / 跳过 ${skipped} ===`);
  log(`报告: ${path.join(RUN_DIR, 'report.md')}`);
}

main().catch((e) => {
  console.error('\n[错误] ' + e.message);
  process.exitCode = 1;
});
