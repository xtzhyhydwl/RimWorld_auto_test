#!/usr/bin/env node
/**
 * scripts/analyze-snapshots.mjs
 * ────────────────────────────────────────────────────────────────────────────
 * **殖民地快照的纯读分析端**。
 *
 * 输入：`profiles/vanilla/foodcontrol-snapshots/colony-<启动时间>.jsonl`
 *       （游戏内 mod `ColonyRecorder.cs` 每 6000 游戏 tick 写一行 = 一张快照）
 * 输出：① 控制台精简摘要（≤ 80 行）
 *       ② `runs/snapshot-analysis/<快照文件名>.report.md`
 *       ③ `runs/snapshot-analysis/<快照文件名>.summary.json`
 *
 * ## 立场（照 docs/ 三篇）
 * - **面向对象**（docs/数据获取…）：读游戏已存好的**对象**，不推断"用户点了什么按钮"。
 * - **对象时序 ≠ 面向过程**（docs/关注面…）：同一对象跨快照比较 = 该对象的历史，
 *   数据源仍是对象；第 4 节做的就是这件事。
 * - **可度量**（docs/快照读取…）：覆盖度 / 采样密度 / **可疑 0 项** 三项必须出结论，
 *   并且必须把「数据里真的没有」和「读法可能失效」分开讲。
 *
 * ## 硬约束
 * - **只读**：不碰除本脚本与 `runs/snapshot-analysis/` 以外的任何文件。
 * - **不引入自动化**：不调 domain / coordinator / 游戏桥接，不调 `node src/cli.js`。
 * - **零第三方依赖**：只用 node 内置模块。
 * - **内存有界**：`readline` 逐行流式；`things` / `designations` 这类大数组
 *   只留**计数 + 哈希**；只有数量 ≤ 200 的 def 才保留格子集合（用于算增删格子），
 *   且只保留"上一张 / 这一张"两份，解析完一张即丢弃。
 *
 * 用法：
 *   node scripts/analyze-snapshots.mjs [文件...] [--out <报告路径>] [--json]
 *   不给文件 → 取 `profiles/vanilla/foodcontrol-snapshots/` 里**最新**的一个。
 *   `--out`：单文件时 = 报告文件路径；多文件时 = 输出目录。
 *   `--json`：stdout 打印机器可读 summary JSON（报告文件照写）。
 */

import fs from 'node:fs';
import path from 'node:path';
import readline from 'node:readline';
import { fileURLToPath } from 'node:url';

// ──────────────────────────────────────────────────────────────── 常量

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(__dirname, '..');
const SNAPSHOT_DIR = path.join(ROOT, 'profiles', 'vanilla', 'foodcontrol-snapshots');
const OUT_DIR = path.join(ROOT, 'runs', 'snapshot-analysis');

const VERSION = '1.0.0';

/** 只给"数量 ≤ 200"的 def 保留格子集合（内存有界；见文件头） */
const MAX_CELL_SET = 200;
/** 最多给多少个 def 保留格子集合（防御性上限） */
const MAX_TRACKED_SETS = 400;
/** 变更事件总量上限（防御性；超出只计数） */
const MAX_CHANGE_EVENTS = 20000;
/** 控制台摘要行数上限 */
const MAX_CONSOLE_LINES = 80;

/** 小人心情/饱食度的"记录阈值"与"显著阈值" */
const PAWN_MOOD_EPS = 0.05;
const PAWN_MOOD_SIG = 0.10;
const PAWN_FOOD_EPS = 0.20;
const PAWN_FOOD_SIG = 0.40;

/** 期望等级排序（"变富 → 期望↑ → 心情↓"那个环要用） */
const EXPECTATION_RANK = [
  'ExtremelyLow', 'VeryLow', 'Low', 'Moderate',
  'High', 'VeryHigh', 'SkyHigh', 'UltraHigh',
];

/**
 * 常见 `IBillGiver`（工作台/炉灶/研究台）Def。
 * ★ 只用于给 `bills` 全程 0 做**内部反证**：
 *   地图上真有工作台而 bills 仍为 0 → 从"可疑"升级为"读法失效嫌疑"。
 *   这份表**不参与**任何读取，只是分析端的交叉校验关键词。
 */
const BILL_GIVER_HINTS = [
  'Campfire', 'ElectricStove', 'FueledStove', 'CraftingSpot',
  'TableButcher', 'TableMachining', 'TableStonecutter', 'TableSculpting',
  'TailorBench', 'HandTailorBench', 'Smithy', 'ElectricSmithy', 'ElectricSmelter',
  'DrugLab', 'FabricationBench', 'ComponentAssemblyBench', 'BioferriteShaper',
  'SimpleResearchBench', 'ResearchBench', 'HiTechResearchBench',
];

/** 口粮类 Def（仅用于基本面脚注"餐食类物品计数"，是库存的**间接**证据） */
const MEAL_DEF_RE = /^(Meal|Pemmican|RawRice|RawPotatoes|RawCorn|RawBerries|Kibble|PackagedSurvivalMeal|MealSurvivalPack)/;

/** 睡眠类负面想法 / 进食类负面想法（动机线索用） */
const SLEEP_THOUGHTS = new Set([
  'SleptOutside', 'SleptInCold', 'SleptInHeat', 'SleptOnGround',
  'SleepDisturbed', 'BedroomOutside', 'SleptInBarracks',
]);
const EAT_THOUGHTS = new Set([
  'AteWithoutTable', 'AteRawFood', 'AteKibble', 'AteCorpse', 'AteRottenFood',
]);

const BUILDING_MATERIALS = new Set([
  'WoodLog', 'Steel', 'Silver', 'Gold', 'Plasteel', 'ComponentIndustrial',
  'Cloth', 'Leather', 'Uranium', 'Jade',
]);

// ──────────────────────────────────────────────────────────────── 小工具

const n0 = (v) => (typeof v === 'number' && Number.isFinite(v) ? v : 0);
const n1 = (v) => (typeof v === 'number' && Number.isFinite(v) ? v : null);
const fmt = (v, d = 1) => (v === null || v === undefined ? '-' : Number(v).toFixed(d));
const pct = (v, d = 0) => (v === null || v === undefined ? '-' : (v * 100).toFixed(d) + '%');
const esc = (s) => String(s ?? '').replace(/\|/g, '\\|').replace(/\r?\n/g, ' ');

function median(arr) {
  if (!arr.length) return null;
  const s = [...arr].sort((a, b) => a - b);
  const m = s.length >> 1;
  return s.length % 2 ? s[m] : (s[m - 1] + s[m]) / 2;
}

/**
 * 格子集合的**顺序无关**哈希（XOR + 求和）｜用于"格子集合有没有变"而不必留下集合。
 * ★ 必须顺序无关：`listerThings` / `zone.cells` 的枚举顺序不保证跨快照稳定，
 *   顺序敏感的哈希会报出"格子变了"的**假变化**（已实测踩到过）。
 */
function cellsHash(cells) {
  let x = 0, s = 0;
  for (let i = 0; i < cells.length; i++) {
    const c = cells[i];
    if (!c) continue;
    let v = (((c[0] & 0xffff) << 16) | (c[1] & 0xffff)) >>> 0;
    v = Math.imul(v ^ (v >>> 16), 0x45d9f3b) >>> 0;
    v = Math.imul(v ^ (v >>> 16), 0x45d9f3b) >>> 0;
    v = (v ^ (v >>> 16)) >>> 0;
    x ^= v;
    s = (s + v) >>> 0;
  }
  return (x >>> 0).toString(16) + '-' + (s >>> 0).toString(16);
}

const cellKey = (c) => c[0] + ',' + c[1];

function setDiff(cur, prev) {
  const added = [], removed = [];
  for (const k of cur) if (!prev.has(k)) added.push(k);
  for (const k of prev) if (!cur.has(k)) removed.push(k);
  return { added, removed };
}

/**
 * 把一条警告归成一个**稳定的种类**，这样"每一张都出现同一类警告"才可度量。
 * 例：`Marble 超过 1500 个，已截断`        → `<def>  超过 1500 个，已截断`
 *     `区域#3 格子超过 1200，已截断`        → `区域#<id> 格子超过 <n>，已截断`
 */
function normalizeWarning(w) {
  let s = String(w);
  s = s.replace(/^([A-Za-z_][A-Za-z0-9_:]*)\s+(?=超过)/, '<def> ');
  s = s.replace(/#\d+/g, '#<id>');
  s = s.replace(/\d+(?:\.\d+)?/g, '<n>');
  return s.trim();
}

/** `HH:mm:ss` → 秒（只用于墙钟跨度/空档，不参与采样判断） */
function hms2sec(s) {
  const m = /^(\d{1,2}):(\d{2}):(\d{2})$/.exec(String(s || ''));
  if (!m) return null;
  return (+m[1]) * 3600 + (+m[2]) * 60 + (+m[3]);
}

function ensureDir(d) {
  fs.mkdirSync(d, { recursive: true });
}

// ──────────────────────────────────────────────────────────────── 分析主体

function newReaderStat(id) {
  return {
    id,
    present: 0,        // health 里出现了几次
    ran: 0,            // ran=true 几次
    items: [],         // 每张的 items（O(张数) 个整数，内存可忽略）
    errors: new Map(), // error 文本 → 次数
    warnKinds: new Map(), // 警告种类 → { count, snapshots:Set(index), sample }
    warnTruncated: 0,  // warnings.length 达到 mod 上限 20 的张数
  };
}

function newState(file) {
  return {
    file,
    fileSize: 0,
    bytes: 0,
    lineCount: 0,     // 总行数（含空行与坏行）
    blankLines: 0,
    badLines: 0,
    badLineSamples: [],
    frames: [],       // 每张快照的紧凑基本面（不含大数组）
    changeCount: 0,   // 记录下来的变更点数
    changeOverflow: 0,
    changes: [],      // 变更点（有界）
    changesByIndex: new Map(),
    readers: new Map(),
    readerOrder: [],
    // 对象时序：首末出现
    zonesMeta: new Map(),
    sgMeta: new Map(),
    pawnMeta: new Map(),
    pawnsLeft: [],
    // 规则用的辅助
    researchAdded: [],
    thoughtFirst: [],
    zoneBaselineZero: false,
    geo: { minX: 1e9, maxX: -1e9, minZ: 1e9, maxZ: -1e9 },
    truncatedDefs: new Set(),
    thingDefsSeen: new Set(),
    seqAlwaysZero: true,
    seqValues: new Set(),
    warningsTotal: 0,
  };
}

function pushChange(st, index, c) {
  if (st.changes.length >= MAX_CHANGE_EVENTS) { st.changeOverflow++; return; }
  const rec = { index, tick: c.tick, day: c.day, category: c.category, subject: c.subject, field: c.field, from: c.from, to: c.to, note: c.note || '' };
  st.changes.push(rec);
  st.changeCount++;
  let list = st.changesByIndex.get(index);
  if (!list) { list = []; st.changesByIndex.set(index, list); }
  list.push(rec);
}

function bumpReader(st, id) {
  let r = st.readers.get(id);
  if (!r) { r = newReaderStat(id); st.readers.set(id, r); st.readerOrder.push(id); }
  return r;
}

/** 逐行流式处理一个文件 */
async function analyzeFile(file) {
  const st = newState(file);
  try { st.fileSize = fs.statSync(file).size; } catch { st.fileSize = 0; }

  // 对象时序的"上一张"状态（大数组只留计数/哈希/小集合）
  let prev = null;

  const rl = readline.createInterface({
    input: fs.createReadStream(file, { encoding: 'utf8' }),
    crlfDelay: Infinity,
  });

  for await (const raw of rl) {
    st.lineCount++;
    // 容忍 UTF-8 BOM：有 BOM 时第一行会以 \uFEFF 开头，否则会被误判成"坏行"并丢掉基线
    const line = (st.lineCount === 1 ? raw.replace(/^\uFEFF/, '') : raw).trim();
    if (!line) { st.blankLines++; continue; }

    let snap;
    try {
      snap = JSON.parse(line);
    } catch (e) {
      st.badLines++;
      if (st.badLineSamples.length < 5) {
        st.badLineSamples.push({ line: st.lineCount, bytes: line.length, error: String(e.message).slice(0, 160) });
      }
      continue;
    }
    // JSON 合法但不是"一张快照对象"（null / 数组 / 数字…）—— 同样算坏行，不能让分析崩掉
    if (snap === null || typeof snap !== 'object' || Array.isArray(snap)) {
      st.badLines++;
      if (st.badLineSamples.length < 5) {
        st.badLineSamples.push({ line: st.lineCount, bytes: line.length, error: 'JSON 合法但不是对象（' + (snap === null ? 'null' : Array.isArray(snap) ? 'array' : typeof snap) + '）' });
      }
      continue;
    }

    st.bytes += Buffer.byteLength(raw, 'utf8') + 1;
    const index = st.frames.length;
    const tick = n0(snap.tick);
    const day = n0(snap.day);
    const wallClock = String(snap.wallClock || '');

    if (n0(snap.seq) !== 0) st.seqAlwaysZero = false;
    st.seqValues.add(n0(snap.seq));

    prev = processSnapshot(st, snap, { index, tick, day, wallClock, line: st.lineCount }, prev);
  }

  // ── 收尾：离开殖民地的小人 ──
  const lastIndex = st.frames.length - 1;
  if (lastIndex >= 0) {
    for (const [name, meta] of st.pawnMeta) {
      if (meta.lastIndex < lastIndex) {
        st.pawnsLeft.push({ name, firstIndex: meta.firstIndex, firstTick: meta.firstTick, lastIndex: meta.lastIndex, lastTick: meta.lastTick });
      }
    }
  }

  st.hypotheses = deriveHypotheses(st);
  return st;
}

/** 处理一张快照：健康/基本面 + 对象时序 diff（不保留大数组） */
function processSnapshot(st, snap, ctx, prev) {
  const index = ctx.index;
  const zones = Array.isArray(snap.zones) ? snap.zones : [];
  const sgs = Array.isArray(snap.storageGroups) ? snap.storageGroups : [];
  const things = snap.things && typeof snap.things === 'object' ? snap.things : {};
  const pawns = Array.isArray(snap.pawns) ? snap.pawns : [];
  const bills = Array.isArray(snap.bills) ? snap.bills : [];
  const research = snap.research && typeof snap.research === 'object' ? snap.research : {};
  const wealth = snap.wealth && typeof snap.wealth === 'object' ? snap.wealth : {};
  const designations = snap.designations && typeof snap.designations === 'object' ? snap.designations : {};
  const health = Array.isArray(snap.health) ? snap.health : [];
  const sampling = snap.sampling && typeof snap.sampling === 'object' ? snap.sampling : {};

  // ── ① 覆盖度：每个 reader 一条记录 ──
  const seenReaders = new Set();
  for (const h of health) {
    if (!h || typeof h !== 'object') continue;
    const id = String(h.reader || '?');
    seenReaders.add(id);
    const r = bumpReader(st, id);
    r.present++;
    if (h.ran) r.ran++;
    r.items.push(n0(h.items));
    const err = String(h.error || '');
    if (err) r.errors.set(err, (r.errors.get(err) || 0) + 1);
    const warns = Array.isArray(h.warnings) ? h.warnings : [];
    if (warns.length >= 20) r.warnTruncated++;
    st.warningsTotal += warns.length;
    for (const w of warns) {
      const kind = normalizeWarning(w);
      let rec = r.warnKinds.get(kind);
      if (!rec) { rec = { count: 0, snapshots: new Set(), sample: String(w) }; r.warnKinds.set(kind, rec); }
      rec.count++;
      rec.snapshots.add(index);
    }
  }
  // 某张缺了某个已见过的 reader → 记一次 present 缺失（用于"运行次数"分母）
  for (const id of st.readerOrder) {
    if (!seenReaders.has(id)) continue; // 只统计出现过的
  }

  // ── 采样密度 ──
  const samplingRec = {
    index,
    tick: ctx.tick,
    day: ctx.day,
    wallClock: ctx.wallClock,
    intervalTicks: n0(sampling.intervalTicks),
    sincePrevTick: n0(sampling.sincePrevTick),
    totalSnapshots: n0(sampling.totalSnapshots),
    playedTicks: n0(sampling.playedTicks),
    looksDropped: sampling.looksDropped === true,
  };

  // ── ② 基本面（给第 5 节用）──
  let moodMin = null, moodSum = 0, moodN = 0, foodMin = null;
  const expDist = new Map();
  for (const p of pawns) {
    const m = n1(p.mood), f = n1(p.food);
    if (m !== null) { moodSum += m; moodN++; moodMin = moodMin === null ? m : Math.min(moodMin, m); }
    if (f !== null) foodMin = foodMin === null ? f : Math.min(foodMin, f);
    const e = String(p.expectation || '(空)');
    expDist.set(e, (expDist.get(e) || 0) + 1);
  }
  const mealCount = Object.entries(things)
    .filter(([k]) => MEAL_DEF_RE.test(k))
    .reduce((a, [, v]) => a + (Array.isArray(v) ? v.length : 0), 0);

  let thingTotal = 0;
  for (const v of Object.values(things)) thingTotal += Array.isArray(v) ? v.length : 0;
  let desigTotal = 0;
  for (const v of Object.values(designations)) desigTotal += Array.isArray(v) ? v.length : 0;

  const frame = {
    index,
    line: ctx.line,
    tick: ctx.tick,
    day: ctx.day,
    mapId: n0(snap.mapId),
    seq: n0(snap.seq),
    wallClock: ctx.wallClock,
    sampling: samplingRec,
    counts: {
      zones: zones.length,
      storageGroups: sgs.length,
      things: thingTotal,
      thingDefs: Object.keys(things).length,
      pawns: pawns.length,
      bills: bills.length,
      designations: desigTotal,
      health: health.length,
    },
    wealth: {
      total: n0(wealth.total), items: n0(wealth.items),
      buildings: n0(wealth.buildings), pawns: n0(wealth.pawns),
    },
    pop: pawns.length,
    moodMin, moodAvg: moodN ? moodSum / moodN : null, foodMin,
    meals: mealCount,
    expectations: Object.fromEntries(expDist),
    researchCurrent: String(research.current || ''),
    researchFinishedCount: n0(research.finishedCount),
    warningsThisSnap: health.reduce((a, h) => a + (Array.isArray(h?.warnings) ? h.warnings.length : 0), 0),
    changes: 0,
  };

  // ── ③ 对象时序（面向对象：同一对象跨快照比较）──
  const cur = buildObjectState(st, { snap, index, zones, sgs, things, pawns, bills, research, designations });
  cur.wealthTotal = n0(wealth.total);
  cur.researchCurrent = String(research.current || '');
  for (const def of Object.keys(things)) st.thingDefsSeen.add(def);
  // 几何范围（虚拟地图的骨架信息，顺便给报告一个"地图多大"）
  for (const k of Object.keys(things)) {
    const arr = things[k];
    if (!Array.isArray(arr)) continue;
    for (const c of arr) {
      if (!Array.isArray(c)) continue;
      if (c[0] < st.geo.minX) st.geo.minX = c[0];
      if (c[0] > st.geo.maxX) st.geo.maxX = c[0];
      if (c[1] < st.geo.minZ) st.geo.minZ = c[1];
      if (c[1] > st.geo.maxZ) st.geo.maxZ = c[1];
    }
  }

  if (prev) {
    diffObjectState(st, prev, cur, { index, tick: ctx.tick, day: ctx.day });
  } else {
    // 基线（第 1 张）只登记"首见"，**不产生变更点**：
    // 否则第 2 张会把"一直都在"的对象误报成"新增"（实测踩到过：3 个小人被报成"加入"）
    seedBaseline(st, cur, { index, tick: ctx.tick, day: ctx.day });
  }
  st.frames.push(frame);
  frame.changes = (st.changesByIndex.get(index) || []).length;
  return cur;
}

/**
 * 基线登记：把第 1 张快照里**已经存在**的对象记成"首见"，但不产生变更点。
 * 这样第 2 张才不会把"一直都有的东西"误报成"新增"。
 */
function seedBaseline(st, cur, at) {
  for (const [key, z] of cur.zones) {
    st.zonesMeta.set(key, {
      key, id: z.id, type: z.type, label: z.label,
      firstIndex: at.index, firstTick: at.tick, firstDay: at.day,
      lastIndex: at.index, lastTick: at.tick, present: 1,
    });
  }
  for (const [key, g] of cur.storageGroups) {
    st.sgMeta.set(key, {
      key, loadId: g.loadId, label: g.label,
      firstIndex: at.index, firstTick: at.tick,
      lastIndex: at.index, present: 1,
    });
  }
  for (const [name, q] of cur.pawns) {
    const seen = new Map();
    for (const [def] of q.thoughts) seen.set(def, { firstIndex: at.index, firstTick: at.tick, firstDay: at.day });
    st.pawnMeta.set(name, {
      name, firstIndex: at.index, firstTick: at.tick, firstDay: at.day,
      lastIndex: at.index, lastTick: at.tick, seenThoughts: seen,
      expectation: q.expectation,
      mood: { min: q.mood, max: q.mood, maxDrop: 0, maxDropTick: null, events: 0, sig: 0 },
      food: { min: q.food, max: q.food, maxDrop: 0, maxDropTick: null, events: 0, sig: 0 },
    });
  }
}

/** 从一张快照抽出"对象状态"（大数组只留计数 / 哈希 / ≤200 的小集合） */function buildObjectState(st, { index, zones, sgs, things, pawns, bills, research, designations }) {
  const zoneMap = new Map();
  for (const z of zones) {
    if (!z || typeof z !== 'object') continue;
    const id = n0(z.id), type = String(z.type || '');
    const key = id + '|' + type;
    const cells = Array.isArray(z.cells) ? z.cells : [];
    const allowed = Array.isArray(z.allowed) ? z.allowed.slice() : [];
    const disallowed = Array.isArray(z.disallowed) ? z.disallowed.slice() : [];
    zoneMap.set(key, {
      id, type, label: String(z.label || ''),
      cellCount: n0(z.cellCount), cellsLen: cells.length, cellsHash: cellsHash(cells),
      plantDef: String(z.plantDef || ''),
      allowed, disallowed,
      allowedKey: allowed.join('|'), disallowedKey: disallowed.join('|'),
    });
  }

  const sgMap = new Map();
  for (const g of sgs) {
    if (!g || typeof g !== 'object') continue;
    const loadId = n0(g.loadId);
    const allowed = Array.isArray(g.allowed) ? g.allowed.slice() : [];
    const disallowed = Array.isArray(g.disallowed) ? g.disallowed.slice() : [];
    sgMap.set(String(loadId), {
      loadId, label: String(g.label || ''),
      memberCount: n0(g.memberCount), cellCount: n0(g.cellCount),
      members: Array.isArray(g.members) ? g.members.length : 0,
      allowedKey: allowed.join('|'), disallowedKey: disallowed.join('|'),
      allowed, disallowed,
    });
  }

  // things：只留计数 + 哈希；数量 ≤ 200 才留格子集合（最多 MAX_TRACKED_SETS 个 def）
  const thingMap = new Map();
  for (const [def, arrRaw] of Object.entries(things)) {
    const arr = Array.isArray(arrRaw) ? arrRaw : [];
    let set = null;
    if (arr.length <= MAX_CELL_SET && thingMap.size < MAX_TRACKED_SETS) {
      set = new Set();
      for (const c of arr) if (Array.isArray(c)) set.add(cellKey(c));
    }
    thingMap.set(def, {
      count: arr.length,
      hash: cellsHash(arr),
      set,
      atCap: arr.length >= 1500,   // 源码 `list.Count < 1500` 的截断上限
    });
  }

  // bills：键 = bench@x,z + recipe（spec 指定）
  const billMap = new Map();
  for (const b of bills) {
    if (!b || typeof b !== 'object') continue;
    const bench = String(b.bench || '');
    const key = bench + '@' + n0(b.benchX) + ',' + n0(b.benchZ) + '|' + String(b.recipe || '');
    billMap.set(key, {
      key, bench, x: n0(b.benchX), z: n0(b.benchZ),
      recipe: String(b.recipe || ''), recipeLabel: String(b.recipeLabel || ''),
      suspended: b.suspended === true,
      repeatMode: String(b.repeatMode || ''), targetCount: String(b.targetCount ?? ''),
    });
  }

  // pawns：name 为键（工具保证同一殖民地内不重名；重名会合并，报告里会提示）
  const pawnMap = new Map();
  for (const p of pawns) {
    if (!p || typeof p !== 'object') continue;
    const name = String(p.name || '');
    const thoughts = new Map();
    const negs = Array.isArray(p.negativeThoughts) ? p.negativeThoughts : [];
    for (const t of negs) {
      const s = String(t);
      const m = /^(.+?)\((-?\d+(?:\.\d+)?)\)$/.exec(s);
      const def = m ? m[1] : s;
      const off = m ? Number(m[2]) : null;
      const rec = thoughts.get(def);
      if (rec) rec.n++;
      else thoughts.set(def, { def, off, n: 1 });
    }
    pawnMap.set(name, {
      name, x: n0(p.x), z: n0(p.z),
      mood: n1(p.mood), food: n1(p.food),
      expectation: String(p.expectation || ''),
      thoughts, negCount: negs.length, negCapped: negs.length >= 6,
    });
  }

  const researchFinished = Array.isArray(research.finished) ? research.finished.map(String) : [];

  const desigMap = new Map();
  for (const [def, arr] of Object.entries(designations)) {
    const list = Array.isArray(arr) ? arr : [];
    desigMap.set(def, { def, count: list.length, hash: cellsHash(list) });
  }

  return { index, zones: zoneMap, storageGroups: sgMap, things: thingMap, bills: billMap, pawns: pawnMap, researchFinished, designations: desigMap };
}

/** 同一对象跨快照比较 —— ★ 这是"对象的时序"，不是"面向过程推断事件" */
function diffObjectState(st, prev, cur, at) {
  const { index, tick, day } = at;

  // ── zones（按 id+type）──
  for (const [key, z] of cur.zones) {
    const meta = st.zonesMeta.get(key) || { key, id: z.id, type: z.type, label: z.label, firstIndex: index, firstTick: tick, firstDay: day, lastIndex: index, lastTick: tick, present: 0 };
    meta.label = z.label; meta.present++; meta.lastIndex = index; meta.lastTick = tick;
    st.zonesMeta.set(key, meta);

    const p = prev.zones.get(key);
    if (!p) {
      pushChange(st, index, { ...at, category: 'zones', subject: key, field: '出现', from: '-', to: `${z.type}「${z.label}」cellCount=${z.cellCount}`, note: '首个快照之后新增' });
      continue;
    }
    if (p.label !== z.label) pushChange(st, index, { ...at, category: 'zones', subject: key, field: 'label', from: p.label, to: z.label });
    if (p.cellCount !== z.cellCount) pushChange(st, index, { ...at, category: 'zones', subject: key, field: 'cellCount', from: p.cellCount, to: z.cellCount });
    if (p.plantDef !== z.plantDef) pushChange(st, index, { ...at, category: 'zones', subject: key, field: 'plantDef', from: p.plantDef || '(空)', to: z.plantDef || '(空)' });
    if (p.allowedKey !== z.allowedKey) pushChange(st, index, { ...at, category: 'zones', subject: key, field: 'allowed', from: p.allowed.join(',') || '(空)', to: z.allowed.join(',') || '(空)' });
    if (p.disallowedKey !== z.disallowedKey) pushChange(st, index, { ...at, category: 'zones', subject: key, field: 'disallowed', from: p.disallowed.join(',') || '(空)', to: z.disallowed.join(',') || '(空)' });
    if (p.cellsHash !== z.cellsHash && p.cellsLen === z.cellsLen) {
      pushChange(st, index, { ...at, category: 'zones', subject: key, field: 'cells', from: `hash ${p.cellsHash}`, to: `hash ${z.cellsHash}`, note: '格子数不变但格子集合变了' });
    }
  }
  for (const [key, p] of prev.zones) {
    if (!cur.zones.has(key)) {
      const meta = st.zonesMeta.get(key);
      if (meta) meta.lastIndex = index - 1;
      pushChange(st, index, { ...at, category: 'zones', subject: key, field: '消失', from: `${p.type}「${p.label}」cellCount=${p.cellCount}`, to: '-' });
    }
  }
  if (index === 0 && cur.zones.size === 0) st.zoneBaselineZero = true;

  // ── storageGroups（按 loadId）──
  for (const [key, g] of cur.storageGroups) {
    const meta = st.sgMeta.get(key) || { key, loadId: g.loadId, label: g.label, firstIndex: index, firstTick: tick, lastIndex: index, present: 0 };
    meta.label = g.label; meta.present++; meta.lastIndex = index;
    st.sgMeta.set(key, meta);

    const p = prev.storageGroups.get(key);
    if (!p) {
      pushChange(st, index, { ...at, category: 'storageGroups', subject: `loadId=${key}`, field: '出现', from: '-', to: `「${g.label}」memberCount=${g.memberCount}` });
      continue;
    }
    for (const f of ['label', 'memberCount', 'cellCount', 'members']) {
      if (p[f] !== g[f]) pushChange(st, index, { ...at, category: 'storageGroups', subject: `loadId=${key}`, field: f, from: p[f], to: g[f] });
    }
    if (p.allowedKey !== g.allowedKey) pushChange(st, index, { ...at, category: 'storageGroups', subject: `loadId=${key}`, field: 'allowed', from: p.allowed.join(',') || '(空)', to: g.allowed.join(',') || '(空)' });
    if (p.disallowedKey !== g.disallowedKey) pushChange(st, index, { ...at, category: 'storageGroups', subject: `loadId=${key}`, field: 'disallowed', from: p.disallowed.join(',') || '(空)', to: g.disallowed.join(',') || '(空)' });
  }
  for (const [key, p] of prev.storageGroups) {
    if (!cur.storageGroups.has(key)) {
      const meta = st.sgMeta.get(key); if (meta) meta.lastIndex = index - 1;
      pushChange(st, index, { ...at, category: 'storageGroups', subject: `loadId=${key}`, field: '消失', from: `「${p.label}」memberCount=${p.memberCount}`, to: '-' });
    }
  }

  // ── things（按 def；注意 BP: / FR: 前缀）──
  const thingKeys = new Set([...cur.things.keys(), ...prev.things.keys()]);
  for (const def of thingKeys) {
    const c = cur.things.get(def), p = prev.things.get(def);
    const cn = c ? c.count : 0, pn = p ? p.count : 0;
    if (cn === pn && (!c || !p || c.hash === p.hash)) continue;

    let note = '';
    if (cn === pn && c && p) {
      // 数量不变但格子集合变了（≤200 才有集合）
      if (c.set && p.set) {
        const { added, removed } = setDiff(c.set, p.set);
        note = `数量不变、位置变了（+${added.length}/-${removed.length} 格）`;
        if (c.atCap || p.atCap) note += '；⚠ 该 def 已达 1500 截断上限，位置集合不完整';
      } else {
        note = '数量不变、位置变了（>200，仅哈希可比）';
      }
      pushChange(st, index, { ...at, category: 'things', subject: def, field: 'cells', from: p.hash, to: c.hash, note });
      continue;
    }

    if (c && p && c.set && p.set) {
      const { added, removed } = setDiff(c.set, p.set);
      const sample = added.slice(0, 4).map((s) => '(' + s + ')').join(' ');
      note = `格子 +${added.length}/-${removed.length}` + (sample ? ' 例：' + sample : '');
    }
    if ((c && c.atCap) || (p && p.atCap)) {
      note += (note ? '；' : '') + '⚠ 已达 1500 截断上限，计数是**下界**，Delta 不可信';
      st.truncatedDefs.add(def);
    }
    pushChange(st, index, { ...at, category: 'things', subject: def, field: 'count', from: pn, to: cn, note });
  }

  // ── bills（键 = bench@x,z + recipe）──
  for (const [key, b] of cur.bills) {
    const p = prev.bills.get(key);
    if (!p) {
      pushChange(st, index, { ...at, category: 'bills', subject: key, field: '出现', from: '-', to: `targetCount=${b.targetCount || '(空)'} repeatMode=${b.repeatMode || '(空)'} suspended=${b.suspended}` });
      continue;
    }
    if (p.targetCount !== b.targetCount) pushChange(st, index, { ...at, category: 'bills', subject: key, field: 'targetCount', from: p.targetCount || '(空)', to: b.targetCount || '(空)' });
    if (p.repeatMode !== b.repeatMode) pushChange(st, index, { ...at, category: 'bills', subject: key, field: 'repeatMode', from: p.repeatMode || '(空)', to: b.repeatMode || '(空)' });
    if (p.suspended !== b.suspended) pushChange(st, index, { ...at, category: 'bills', subject: key, field: 'suspended', from: p.suspended, to: b.suspended });
    if (p.recipeLabel !== b.recipeLabel) pushChange(st, index, { ...at, category: 'bills', subject: key, field: 'recipeLabel', from: p.recipeLabel, to: b.recipeLabel });
  }
  for (const [key, p] of prev.bills) {
    if (!cur.bills.has(key)) pushChange(st, index, { ...at, category: 'bills', subject: key, field: '移除', from: `targetCount=${p.targetCount || '(空)'}`, to: '-' });
  }

  // ── research ──
  const prevFinished = prev.researchFinished;
  for (const f of cur.researchFinished) {
    if (!prevFinished.includes(f)) {
      pushChange(st, index, { ...at, category: 'research', subject: f, field: 'finished', from: '未完成', to: '已完成' });
      st.researchAdded.push({ index, tick, day, def: f });
    }
  }
  for (const f of prevFinished) {
    if (!cur.researchFinished.includes(f)) pushChange(st, index, { ...at, category: 'research', subject: f, field: 'finished', from: '已完成', to: '未完成（回退？）' });
  }
  if (prev.researchCurrent !== cur.researchCurrent) {
    pushChange(st, index, { ...at, category: 'research', subject: 'current', field: 'research.current', from: prev.researchCurrent || '(空)', to: cur.researchCurrent || '(空)' });
  }

  // ── pawns（按 name）──
  for (const [name, q] of cur.pawns) {
    let meta = st.pawnMeta.get(name);
    if (!meta) {
      meta = {
        name, firstIndex: index, firstTick: tick, firstDay: day, lastIndex: index, lastTick: tick,
        seenThoughts: new Map(), mood: { min: null, max: null, maxDrop: 0, maxDropTick: null, events: 0, sig: 0 },
        food: { min: null, max: null, maxDrop: 0, maxDropTick: null, events: 0, sig: 0 },
      };
      st.pawnMeta.set(name, meta);
      pushChange(st, index, { ...at, category: 'pawns', subject: name, field: '加入', from: '-', to: `mood=${fmt(q.mood, 2)} food=${fmt(q.food, 2)} 期望=${q.expectation || '(空)'}` });
    }
    meta.lastIndex = index; meta.lastTick = tick;
    meta.expectation = q.expectation;

    // 负面想法首次出现
    for (const [def, rec] of q.thoughts) {
      if (!meta.seenThoughts.has(def)) {
        meta.seenThoughts.set(def, { firstIndex: index, firstTick: tick, firstDay: day });
        st.thoughtFirst.push({ pawn: name, def, index, tick, day, offset: rec.off });
        pushChange(st, index, { ...at, category: 'pawns', subject: name, field: 'negativeThought+', from: '-', to: def, note: `心情 -${rec.off === null ? '?' : Math.abs(rec.off).toFixed(1)}` });
      }
    }
    if (q.negCapped) meta.negCapped = true;

    const p = prev.pawns.get(name);
    if (!p) continue;

    // 期望变化
    if (p.expectation !== q.expectation) {
      const rk = (e) => EXPECTATION_RANK.indexOf(e);
      const dir = rk(q.expectation) > rk(p.expectation) ? '↑' : (rk(q.expectation) < rk(p.expectation) ? '↓' : '?');
      pushChange(st, index, { ...at, category: 'pawns', subject: name, field: 'expectation', from: p.expectation || '(空)', to: q.expectation || '(空)', note: '期望' + dir });
    }

    // 心情 / 饱食度：聚合统计 + 只记显著变化
    for (const [field, eps, sig] of [['mood', PAWN_MOOD_EPS, PAWN_MOOD_SIG], ['food', PAWN_FOOD_EPS, PAWN_FOOD_SIG]]) {
      const a = p[field], b = q[field];
      if (a === null || b === null) continue;
      const s = meta[field];
      s.min = s.min === null ? b : Math.min(s.min, b);
      s.max = s.max === null ? b : Math.max(s.max, b);
      const d = b - a;
      if (d < s.maxDrop) { s.maxDrop = d; s.maxDropTick = tick; }
      if (Math.abs(d) >= eps) {
        s.events++;
        if (Math.abs(d) >= sig) {
          s.sig++;
          pushChange(st, index, { ...at, category: 'pawns', subject: name, field, from: fmt(a, 2), to: fmt(b, 2), note: '单步 Δ=' + fmt(d, 2) });
        }
      }
    }
  }
  for (const [name] of prev.pawns) {
    if (!cur.pawns.has(name)) pushChange(st, index, { ...at, category: 'pawns', subject: name, field: '离开', from: '在场', to: '-' });
  }

  // ── designations（按 def）──
  for (const [def, d] of cur.designations) {
    const p = prev.designations.get(def);
    if (!p) { pushChange(st, index, { ...at, category: 'designations', subject: def, field: '出现', from: '0', to: String(d.count) }); continue; }
    if (p.count !== d.count) pushChange(st, index, { ...at, category: 'designations', subject: def, field: 'count', from: p.count, to: d.count, note: p.hash !== d.hash ? '位置也变了' : '' });
    else if (p.hash !== d.hash) pushChange(st, index, { ...at, category: 'designations', subject: def, field: 'cells', from: p.hash, to: d.hash, note: '数量不变、位置变了' });
  }
  for (const [def, p] of prev.designations) {
    if (!cur.designations.has(def)) pushChange(st, index, { ...at, category: 'designations', subject: def, field: '消失', from: String(p.count), to: '0' });
  }

  // ── wealth（序列 + 显著变化点）──
  if (prev.wealthTotal !== undefined && cur.wealthTotal !== undefined) {
    const a = prev.wealthTotal, b = cur.wealthTotal;
    const d = b - a;
    const sig = Math.max(50, Math.abs(a) * 0.005);
    if (Math.abs(d) >= sig) {
      pushChange(st, index, { ...at, category: 'wealth', subject: 'total', field: 'wealth.total', from: fmt(a, 1), to: fmt(b, 1), note: `Δ=${d > 0 ? '+' : ''}${fmt(d, 1)}（阈值 ${fmt(sig, 0)} = max(50, 0.5%×上一张)）` });
    }
  }

  return;
}

// `buildObjectState` 里带的额外字段（供 wealth diff 使用）
// （在 processSnapshot 里补上，避免在 diff 里再去翻 snap）
// ──────────────────────────────────────────────────────────────── 动机线索（全部为推测）

function deriveHypotheses(st) {
  const rows = [];
  const F = st.frames;
  if (F.length < 2) return rows;
  const clamp = (i) => Math.max(0, Math.min(F.length - 1, i));
  const pop = (i) => F[clamp(i)].pop;
  const foodMin = (i) => F[clamp(i)].foodMin;
  const at = (i) => `tick ${F[i].tick}（第 ${F[i].day} 天，第 ${i + 1} 张）`;
  const add = (i, observed, guess, confidence) => {
    rows.push({ index: i, tick: F[i].tick, day: F[i].day, observed, guess, confidence });
  };

  // ── 规则 1/2/3：生产队列 targetCount 变化 ──
  for (const c of st.changes) {
    if (c.category !== 'bills') continue;
    if (c.field !== 'targetCount') continue;
    const a = Number(c.from), b = Number(c.to);
    if (!Number.isFinite(a) || !Number.isFinite(b)) continue;
    const i = c.index;
    if (b > a) {
      if (pop(i) > pop(i - 1)) {
        add(i, [`${c.subject} 目标量 ${a}→${b}`, `人口 ${pop(i - 1)}→${pop(i)}（tick ${F[i - 1].tick}→${F[i].tick}）`],
          '新增人口 → 消耗上升 → 扩产（反馈环"人口↑"）', '中');
      } else if ((foodMin(i) !== null && foodMin(i) < 0.3) || (foodMin(i - 1) !== null && foodMin(i - 1) < 0.3)) {
        add(i, [`${c.subject} 目标量 ${a}→${b}`, `最饿小人饱食度 ${pct(foodMin(i - 1), 0)}→${pct(foodMin(i), 0)}`],
          '有人快饿到 → 提高目标量补产', '中');
      }
    } else if (b < a) {
      const popSame = pop(i) === pop(i - 1) && pop(i) === pop(i - 2);
      if (popSame) {
        add(i, [`${c.subject} 目标量 ${a}→${b}`, `人口稳定在 ${pop(i)}`], '需求下降/产能过剩 → 下调目标量', '低');
      }
    }
  }

  // ── 规则 4：施工（蓝图/框架↑）伴随建材↓（按快照聚合，避免同一张刷 N 行）──
  {
    const buildUp = st.changes.filter((c) => c.category === 'things' && (c.subject.startsWith('BP:') || c.subject.startsWith('FR:')) && typeof c.to === 'number' && typeof c.from === 'number' && c.to > c.from);
    const matDown = st.changes.filter((c) => c.category === 'things' && BUILDING_MATERIALS.has(c.subject) && typeof c.to === 'number' && typeof c.from === 'number' && c.to < c.from);
    const byIdx = new Map();
    for (const b of buildUp) {
      if (!byIdx.has(b.index)) byIdx.set(b.index, []);
      byIdx.get(b.index).push(b);
    }
    for (const [i, list] of [...byIdx.entries()].sort((a, b) => a[0] - b[0])) {
      const m = matDown.find((x) => Math.abs(x.index - i) <= 1);
      if (!m) continue;
      const names = list.slice(0, 5).map((b) => `${b.subject} ${b.from}→${b.to}`);
      add(i, [
        `施工新增：${names.join('、')}${list.length > 5 ? ` 等 ${list.length} 项` : ''}`,
        `建材减少：${m.subject} ${m.from}→${m.to}（${at(m.index)}）`,
      ], '材料正在被转化成建筑（施工中），蓝图/框架还没变成成品', '中');
    }
  }

  // ── 规则 5：建床 ↔ 睡眠类负面想法（按快照聚合）──
  {
    const bedEvents = st.changes.filter((c) => c.category === 'things' && /(^BP:Blueprint_Bed$|^FR:Frame_Bed$|^Bed$)/.test(c.subject) && typeof c.to === 'number' && typeof c.from === 'number' && c.to > c.from);
    const byIdx = new Map();
    for (const t of st.thoughtFirst) {
      if (!SLEEP_THOUGHTS.has(t.def)) continue;
      if (!byIdx.has(t.index)) byIdx.set(t.index, []);
      byIdx.get(t.index).push(t);
    }
    for (const [i, list] of [...byIdx.entries()].sort((a, b) => a[0] - b[0])) {
      const obs = list.slice(0, 6).map((t) => `${t.pawn} 首次出现 ${t.def}(${t.offset ?? '?'})`);
      if (list.length > 6) obs.push(`…共 ${list.length} 条睡眠类负面想法`);
      const bed = bedEvents.filter((b) => b.index <= i).sort((a, b) => b.index - a.index)[0];
      if (!bed) {
        add(i, obs, '（未给出解释）数据只到"有人抱怨睡眠"，快照里没有任何床位变化可配对', '低');
        continue;
      }
      const gap = i - bed.index;
      if (gap <= 1) {
        add(i, [...obs, `床位类新增：${bed.subject} ${bed.from}→${bed.to}（${at(bed.index)}）`],
          '为消除睡眠类负面想法而建床/加床', '中');
      } else {
        add(i, [...obs, `床位类新增在 ${at(bed.index)}（早 ${gap} 张）：${bed.subject} ${bed.from}→${bed.to}`],
          `床**先于**抱怨出现 ${gap} 张 → 建造没有解决睡眠问题：可能未完工 / 无屋顶无房间 / 小人没去睡（需图或后续快照验证）`, '中');
      }
    }
  }

  // ── 规则 6：进食类负面想法 ↔ 餐桌（按快照聚合）──
  {
    const tableEvents = st.changes.filter((c) => c.category === 'things' && /^Table\d/.test(c.subject) && typeof c.to === 'number' && typeof c.from === 'number' && c.to > c.from);
    const byIdx = new Map();
    for (const t of st.thoughtFirst) {
      if (!EAT_THOUGHTS.has(t.def)) continue;
      if (!byIdx.has(t.index)) byIdx.set(t.index, []);
      byIdx.get(t.index).push(t);
    }
    for (const [i, list] of [...byIdx.entries()].sort((a, b) => a[0] - b[0])) {
      const near = tableEvents.find((x) => x.index >= i - 1 && x.index <= i + 6);
      if (!near) continue;
      const obs = list.slice(0, 6).map((t) => `${t.pawn} 首次出现 ${t.def}(${t.offset ?? '?'})`);
      const kinds = new Set(list.map((t) => t.def));
      add(i, [...obs, `餐桌类新增：${near.subject} ${near.from}→${near.to}（${at(near.index)}）`],
        kinds.has('AteWithoutTable') ? '为消除"站着吃饭"而添置餐桌' : '为改善进食而改动餐桌/食物', '中');
    }
  }

  // ── 规则 7/8：财富 → 期望 → 心情（反馈环）──
  {
    const w0 = F[0].wealth.total, wl = F[F.length - 1].wealth.total;
    const expUp = st.changes.filter((c) => c.category === 'pawns' && c.field === 'expectation' && c.note === '期望↑');
    const expDown = st.changes.filter((c) => c.category === 'pawns' && c.field === 'expectation' && c.note === '期望↓');
    if (expUp.length && wl > w0 * 1.05) {
      const e = expUp[0];
      add(e.index, [`${e.subject} 期望 ${e.from}→${e.to}`, `财富 total ${fmt(w0, 1)} → ${fmt(wl, 1)}（+${fmt((wl / w0 - 1) * 100, 1)}%）`],
        '变富 → 期望基础值上升（反馈环第 1→2 环）', '中');
    }
    for (const e of [...expUp, ...expDown]) {
      const meta = st.pawnMeta.get(e.subject);
      if (!meta) continue;
      const drop = meta.mood.maxDrop;
      if (drop <= -PAWN_MOOD_EPS) {
        add(e.index, [`${e.subject} 期望 ${e.from}→${e.to}`, `该小人最大单步心情跌幅 ${fmt(drop, 2)}（tick ${meta.mood.maxDropTick}）`],
          '期望变化与心情下滑同期 → 期望上升压低心情（反馈环第 3 环）', '低');
      }
    }
  }

  // ── 规则 9：科技完成 ↔ 建筑变化 ──
  for (const r of st.researchAdded) {
    const thingChg = st.changes.filter((c) => c.index === r.index && c.category === 'things' && c.field === 'count' && typeof c.to === 'number' && typeof c.from === 'number' && c.to > c.from);
    if (thingChg.length) {
      add(r.index, [`研究完成 ${r.def}`, `同期新增 ${thingChg.slice(0, 3).map((t) => `${t.subject} ${t.from}→${t.to}`).join('、')}`],
        '科技解锁新建筑 → 立刻开建', '低');
    }
  }

  // ── 规则 10：地图标记（采矿）↔ 矿脉计数 ──
  for (const c of st.changes) {
    if (c.category !== 'designations' || !(c.field === '出现' || c.field === 'count')) continue;
    if (typeof c.to !== 'number' || typeof c.from !== 'number' || c.to <= c.from) continue;
    const mineDown = st.changes.filter((x) => x.category === 'things' && /^Mineable/.test(x.subject) && typeof x.to === 'number' && typeof x.from === 'number' && x.to < x.from && Math.abs(x.index - c.index) <= 1);
    if (mineDown.length) {
      add(c.index, [`标记 ${c.subject} ${c.from}→${c.to}`, `矿脉 ${mineDown[0].subject} ${mineDown[0].from}→${mineDown[0].to}`],
        '玩家划了采矿标记，矿脉正在被挖掉 → 安排资源采集', '中');
    }
  }

  return rows.sort((a, b) => a.index - b.index);
}

// ──────────────────────────────────────────────────────────────── 覆盖度判定

function analyzeCoverage(st) {
  const snapCount = st.frames.length;
  const readers = [];
  const suspectZero = [];
  const broken = [];
  const explained = [];

  for (const id of st.readerOrder) {
    const r = st.readers.get(id);
    const items = r.items;
    const min = items.length ? Math.min(...items) : null;
    const max = items.length ? Math.max(...items) : null;
    const med = median(items);
    const constant = min !== null && min === max;
    const errCount = [...r.errors.values()].reduce((a, b) => a + b, 0);
    const warnKinds = [...r.warnKinds.entries()].map(([kind, rec]) => ({
      kind, count: rec.count, snapshots: rec.snapshots.size, sample: rec.sample,
      everySnap: r.ran >= 2 && rec.snapshots.size >= r.ran,
    }));
    const rec = {
      id,
      present: r.present,
      ran: r.ran,
      missing: snapCount - r.present,
      items: { min, median: med, max, samples: items.length },
      constant,
      errors: [...r.errors.entries()].map(([text, count]) => ({ text, count })),
      errCount,
      warnKinds,
      warnTruncatedSnapshots: r.warnTruncated,
      allZero: max === 0,
      itemsSeries: items,
    };
    readers.push(rec);

    if (rec.allZero && errCount === 0) suspectZero.push(rec);
    if (errCount > 0) broken.push({ rec, why: 'error 非空' });
    const everyKinds = warnKinds.filter((w) => w.everySnap);
    if (everyKinds.length) broken.push({ rec, why: '每张都有同类警告', kinds: everyKinds });
  }

  // ── 对"可疑 0 项"做交叉校验（能不能在这份数据内部自证）──
  const zoneTypes = new Set();
  for (const [, z] of st.zonesMeta) zoneTypes.add(z.type);
  const defSeen = st.thingDefsSeen;
  const billGiverPresent = BILL_GIVER_HINTS.filter((d) => defSeen.has(d));
  const stockpileZones = [...st.zonesMeta.values()].filter((z) => /Zone_Stockpile|Building_Storage/.test(z.type));

  const verdicts = [];
  for (const rec of suspectZero) {
    if (rec.id === 'storageGroups' && stockpileZones.length) {
      verdicts.push({
        reader: rec.id, level: '高度可疑 → 读法失效嫌疑',
        why: `zones 里有 ${stockpileZones.length} 个存储类区域（${[...new Set(stockpileZones.map((z) => z.type))].join('/')}），但 storageGroups 全程 0`,
        evidence: '源码路径 StorageGroupsReader.Read：`var src = mgr.StorageGroupsForReading; if (src == null) return;` —— **静默返回、不记警告**，所以"0 项 + 无警告"完全可能是这里失效而不是"真的没有组"',
      });
    } else if (rec.id === 'bills' && billGiverPresent.length) {
      verdicts.push({
        reader: rec.id, level: '高度可疑 → 读法失效嫌疑',
        why: `things 里出现了工作台 def：${billGiverPresent.join('、')}，但 bills 全程 0`,
        evidence: 'BillsReader 与 ThingsReader 遍历同一个 listerThings；有 IBillGiver 却没读到任何 Bill，说明读法有问题',
      });
    } else if (rec.id === 'bills' && defSeen.size) {
      verdicts.push({
        reader: rec.id, level: '可疑（数据内无反证，倾向"真没有"）',
        why: `things 里没有常见工作台 def（检查了 ${BILL_GIVER_HINTS.length} 个），本局可能确实没建任何生产设施`,
        evidence: '读法是否失效无法在这份数据内部自证 → 需在游戏里放一个炉灶/工作台再录一次',
      });
    } else if (rec.id === 'designations') {
      verdicts.push({
        reader: rec.id, level: '可疑（数据内无反证，倾向"真没有"）',
        why: 'designations 全程 0 且无警告；things 里 Mineable* 一直存在（矿脉在地图上）说明地图正常',
        evidence: '本局可能确实一个采矿/拆除标记都没划',
      });
    } else {
      verdicts.push({ reader: rec.id, level: '可疑', why: `items 全程 0 且 error 为空`, evidence: '无法在数据内部区分"真没有"与"读法失效"' });
    }
  }

  // ── 把"机械命中但已解释"的单列出来（不许含糊）──
  for (const b of broken) {
    if (b.kinds) {
      for (const k of b.kinds) {
        if (/截断/.test(k.kind)) {
          explained.push({
            reader: b.rec.id, kind: k.kind, count: k.count,
            why: '这是源码里的**设计上限**（`ThingsReader`: `if (list.Count < 1500) list.Add(...)`；`ZonesReader`: 1200 格），不是读法失效；后果是该 def 的计数是**下界**，Delta 不可信',
          });
        }
      }
    }
  }

  return { readers, suspectZero, broken, explained, verdicts, billGiverPresent, stockpileZoneTypes: [...zoneTypes] };
}

// ──────────────────────────────────────────────────────────────── 报告渲染

function renderMarkdown(st) {
  const F = st.frames;
  const cov = st.coverage;
  const L = [];
  const w = (s = '') => L.push(s);

  const first = F[0] || null;
  const last = F[F.length - 1] || null;

  w(`# 殖民地快照分析报告`);
  w();
  w(`- **输入**：\`${path.relative(ROOT, st.file).replace(/\\/g, '/')}\`（${(st.fileSize / 1048576).toFixed(2)} MB）`);
  w(`- **生成**：${new Date().toISOString()}｜脚本 \`scripts/analyze-snapshots.mjs\` v${VERSION}`);
  w(`- **口径**：\`mood\` / \`food\` 是**小人需求条的百分比**（\`needs.mood\` / \`needs.food\`），**不是殖民地库存**；`);
  w(`  "食物 min" = 最饿的那个小人的饱食度。库存只能从 \`things\` 里口粮类 def 的计数**间接**看（见第 5 节脚注）。`);
  w(`- **只读**：本报告不改动任何快照数据。`);
  w();

  // ── 1 概览 ──
  w(`## 1. 概览`);
  w();
  w(`| 项 | 值 |`);
  w(`| --- | --- |`);
  w(`| 文件 | ${esc(path.basename(st.file))} |`);
  w(`| 行数（有效快照） | ${F.length} |`);
  w(`| 空行 / 坏行 | ${st.blankLines} / ${st.badLines} |`);
  w(`| tick 范围 | ${first ? first.tick : '-'} → ${last ? last.tick : '-'} |`);
  w(`| day 范围 | ${first ? first.day : '-'} → ${last ? last.day : '-'} |`);
  w(`| wallClock 首/末 | ${first ? first.wallClock : '-'} → ${last ? last.wallClock : '-'} |`);
  w(`| mapId 集合 | ${[...new Set(F.map((f) => f.mapId))].join(', ') || '-'} |`);
  w(`| seq 取值 | ${[...st.seqValues].join(', ')}${st.seqAlwaysZero ? '（★ 恒为 0；源码 `SnapshotBuilder` 已加 `snap.Seq = sampling.TotalSnapshots`，**说明这份数据来自旧构建**）' : ''} |`);
  w(`| 地图坐标范围（things 格子的包围盒） | x ${st.geo.minX}..${st.geo.maxX}，z ${st.geo.minZ}..${st.geo.maxZ} |`);
  if (st.badLines) {
    w();
    w(`**坏行样本**（最多 5 条）：`);
    for (const b of st.badLineSamples) w(`- 第 ${b.line} 行（${b.bytes} 字节）：${esc(b.error)}`);
  }
  w();

  // ── 2 采样密度 ──
  const dens = st.density;
  w(`## 2. 采样密度（快照够不够密）`);
  w();
  w(`| 指标 | 值 |`);
  w(`| --- | --- |`);
  w(`| intervalTicks（设定间隔） | ${dens.intervalList.join('/')} |`);
  w(`| 快照数 | ${dens.count} |`);
  w(`| Δ 自报（\`sampling.sincePrevTick\`）平均 / 最小 / 最大 | ${fmt(dens.recStat.avg, 1)} / ${dens.recStat.min ?? '-'} / ${dens.recStat.max ?? '-'} |`);
  w(`| Δ **实测**（文件里相邻两张的 tick 差）平均 / 最小 / 最大 | ${fmt(dens.obsStat.avg, 1)} / ${dens.obsStat.min ?? '-'} / ${dens.obsStat.max ?? '-'} |`);
  w(`| 自报 vs 实测 不一致 | ${dens.mismatches.length} 处${dens.mismatches.length ? '（★ 说明录制端记错或文件被动过）' : ''} |`);
  w(`| 与设定间隔的偏差（按自报均值） | ${dens.recStat.avg === null ? '-' : fmt((dens.recStat.avg / (dens.intervalNominal || 6000) - 1) * 100, 2) + '%'} |`);
  w(`| **漏采次数**（\`looksDropped\` ∨ 自报 Δ > 1.5×interval ∨ **实测** tick 差 > 1.5×interval） | **${dens.dropped.length}** |`);
  w(`| 覆盖游戏时长 | ${dens.coveredTicks} tick = ${fmt(dens.coveredDays, 2)} 游戏天 |`);
  w(`| 每游戏天几张 | ${fmt(dens.perDay, 2)} 张/天（间隔 ${dens.intervalNominal} tick → 理论 ${fmt(dens.theoreticalPerDay, 2)}） |`);
  w(`| wallClock 实际跨度 | ${dens.wallSpanSec === null ? '-' : dens.wallSpanSec + ' 秒（' + fmt(dens.wallSpanSec / 60, 1) + ' 分钟）'} |`);
  w();
  if (dens.dropped.length) {
    w(`**漏采位置**：`);
    w();
    w(`| 第几张 | tick | day | 自报 Δ | 实测 tick 差 | 1.5×interval | looksDropped | 判定理由 |`);
    w(`| --- | --- | --- | --- | --- | --- | --- | --- |`);
    for (const d of dens.dropped) w(`| ${d.index + 1} | ${d.tick} | ${d.day} | ${d.sincePrevTick} | ${d.observedDelta} | ${d.threshold} | ${d.looksDropped} | ${esc(d.reasons.join('；'))} |`);
    w();
  } else {
    w(`**漏采位置**：无。所有相邻快照的**自报 Δ 与实测 tick 差都等于**设定间隔（两边同时对上，才算自证）。`);
    w();
  }
  if (dens.mismatches.length) {
    w(`**自报 vs 实测 不一致的位置**（前 20 处）：`);
    w();
    w(`| 第几张 | tick | 上一张 tick | 自报 Δ | 实测 tick 差 |`);
    w(`| --- | --- | --- | --- | --- |`);
    for (const m of dens.mismatches.slice(0, 20)) w(`| ${m.index + 1} | ${m.tick} | ${m.previousTick} | ${m.recorded} | ${m.observed} |`);
    if (dens.mismatches.length > 20) w(`| … | | | | 共 ${dens.mismatches.length} 处 |`);
    w();
  }
  if (dens.wallGaps.length) {
    w(`**墙钟空档**（同一份数据内真实时间跨度异常，仅用于排查，不参与采样判断）：`);
    w();
    w(`| 第几张 → 第几张 | tick | wallClock | 真实秒数 | 对应 tick 增量 | 隐含速度（tick/秒） |`);
    w(`| --- | --- | --- | --- | --- | --- |`);
    for (const g of dens.wallGaps) {
      const tps = g.sec > 0 ? (g.tickTo - g.tickFrom) / g.sec : null;
      w(`| ${g.i + 1} → ${g.j + 1} | ${g.tickFrom} → ${g.tickTo} | ${g.wcFrom} → ${g.wcTo} | ${g.sec} | ${g.tickTo - g.tickFrom} | ${fmt(tps, 1)} |`);
    }
    w();
    w(`> 说明：\`sincePrevTick\` 正常但墙钟空档很大 → tick **没有漏采**，是**真实时间里游戏跑得慢/被暂停/录制进程卡住**。`);
    w(`> 参考：RimWorld 正常速度约 **60 tick/秒**（1 倍速）；明显低于它说明这段真实时间里游戏在卡/暂停/低倍速。`);
    w();
  }
  w(`**结论：${dens.verdict}**`);
  w();
  w(`依据：${dens.verdictReason}`);
  w();

  // ── 3 覆盖度 ──
  w(`## 3. 覆盖度（可度量）`);
  w();
  w(`每个读取块一行。\`items\` 是该 reader 每张快照报告读到的对象个数。`);
  w();
  w(`| reader | 运行次数 | items 最小 | 中位 | 最大 | 全程恒定 | error | 警告种类（次数） |`);
  w(`| --- | --- | --- | --- | --- | --- | --- | --- |`);
  for (const r of cov.readers) {
    const wk = r.warnKinds.map((k) => `${k.kind}×${k.count}${k.everySnap ? '（每张）' : ''}`).join('；') || '-';
    w(`| \`${r.id}\` | ${r.ran}/${F.length}${r.missing ? `（缺 ${r.missing}）` : ''} | ${r.items.min ?? '-'} | ${r.items.median ?? '-'} | ${r.items.max ?? '-'} | ${r.constant ? '是' : '否'} | ${r.errCount ? r.errCount + ' 次' : '无'} | ${esc(wk)} |`);
  }
  w();
  if (st.warningsTotal) w(`> 全文件共 ${st.warningsTotal} 条警告。⚠ 源码 SnapshotReadContext.Warn 有**每块每张最多 20 条**的上限，达到上限的张数见 \`warnTruncatedSnapshots\`（summary.json）。`);
  w();

  // ★ 可疑 0 项
  w(`### 3.1 ★ 可疑 0 项（全程 items=0 且 error 为空）`);
  w();
  if (!cov.suspectZero.length) {
    w(`无。`);
  } else {
    w(`按 docs/快照读取-可拓展与可度量.md 的定义，以下 reader **全程 items=0 且 error 为空** —— `);
    w(`这**既可能是"这局真的没有这种对象"，也可能是"读法失效"**，两者在数据里长得一模一样，所以必须点名：`);
    w();
    w(`| reader | 判定 | 依据 |`);
    w(`| --- | --- | --- |`);
    for (const v of cov.verdicts) w(`| \`${v.reader}\` | ${esc(v.level)} | ${esc(v.why)}<br>${esc(v.evidence)} |`);
  }
  w();

  // ★ 读法失效
  w(`### 3.2 ★ 读法失效（error 非空，或**每一张都**出现同一类警告）`);
  w();
  if (!cov.broken.length) {
    w(`无。`);
  } else {
    w(`| reader | 症状 | 次数 | 出现张数 | 原文 | 判读 |`);
    w(`| --- | --- | --- | --- | --- | --- |`);
    for (const b of cov.broken) {
      const kinds = b.kinds || [];
      for (const k of kinds) {
        const isTrunc = /截断/.test(k.kind);
        w(`| \`${b.rec.id}\` | 每张都有同类警告 | ${k.count} | ${k.snapshots}/${b.rec.ran} | ${esc(k.sample)} | ${isTrunc ? '**已解释**：设计上的截断上限（见 3.3）' : '**读法失效**'} |`);
      }
      if (b.rec.errCount) {
        for (const e of b.rec.errors) w(`| \`${b.rec.id}\` | error 非空 | ${e.count} | - | ${esc(e.text)} | **读法失效** |`);
      }
    }
  }
  w();
  if (cov.explained.length) {
    w(`### 3.3 机械命中"每张同类警告"但**已解释**的（不许含糊，单独列）`);
    w();
    w(`| reader | 警告种类 | 次数 | 为什么不是读法失效 |`);
    w(`| --- | --- | --- | --- |`);
    for (const e of cov.explained) w(`| \`${e.reader}\` | ${esc(e.kind)} | ${e.count} | ${esc(e.why)} |`);
    w();
    w(`> 额外精度：\`things\` reader 的 \`items\` 是**真实对象个数**（\`ctx.ItemsRead++\` 在截断判断之外），`);
    w(`> 只有快照里 \`things[def]\` 的**数组长度**受 1500 上限影响。所以 \`items\` 可以校验覆盖率，`);
    w(`> 而单个 def 的计数在触顶时只能当**下界**用。`);
    w();
  }
  if (st.truncatedDefs.size) {
    w(`**已达截断上限的 def**（计数是下界，增减不可信）：${[...st.truncatedDefs].map((d) => '`' + d + '`').join('、')}`);
    w();
  }
  w(`### 3.4 "数据里真的没有" vs "读法可能失效"—— 本文件的判定`);
  w();
  w(`| 现象 | 判定 | 理由 |`);
  w(`| --- | --- | --- |`);
  const sgZero = cov.readers.find((r) => r.id === 'storageGroups' && r.allZero);
  const billZero = cov.readers.find((r) => r.id === 'bills' && r.allZero);
  const desZero = cov.readers.find((r) => r.id === 'designations' && r.allZero);
  if (sgZero) {
    const v = cov.verdicts.find((x) => x.reader === 'storageGroups');
    w(`| storageGroups 全程 0 | ${esc(v ? v.level : '可疑')} | zones 里有存储类区域，而源码在 \`StorageGroupsForReading == null\` 时**静默 return**（无警告） |`);
  }
  if (billZero) {
    const v = cov.verdicts.find((x) => x.reader === 'bills');
    w(`| bills 全程 0 | ${esc(v ? v.level : '可疑')} | 地图上没有常见工作台 def，倾向真没有；但不是自证 |`);
  }
  if (desZero) {
    w(`| designations 全程 0 | 可疑（倾向"真没有"） | 无任何内部反证 |`);
  }
  w(`| research.current 全程空 | **读法失效** | 每张都有 \`currentProj 读不到\` 警告 |`);
  w();

  // ── 4 对象变化点 ──
  w(`## 4. 对象变化点（面向对象时序）`);
  w();
  w(`做法：**同一对象跨快照比较**（zones 按 id+type，storageGroups 按 loadId，things 按 def，`);
  w(`bills 按 \`bench@x,z\` + recipe，pawns 按 name，research 按 finished 集合）。数据源始终是**对象**，`);
  w(`时序只是"同一个对象的历次取值" —— 这不是"从事件反推过程"。`);
  w();
  w(`共记录 **${st.changeCount}** 个变更点${st.changeOverflow ? `（另有 ${st.changeOverflow} 个超出上限只计数）` : ''}。`);
  w();

  const byCat = (cat) => st.changes.filter((c) => c.category === cat);
  const section = (title, cat, extra) => {
    const list = byCat(cat);
    w(`### 4.${extra} ${title}`);
    w();
    if (!list.length) {
      w(`**本会话无变化。**（该对象类型在 ${F.length} 张快照里逐字段一致，或全程为空）`);
      w();
      return;
    }
    w(`| 第几张 | tick | day | 对象 | 字段 | 从 | 到 | 备注 |`);
    w(`| --- | --- | --- | --- | --- | --- | --- | --- |`);
    const cap = 120;
    for (const c of list.slice(0, cap)) {
      w(`| ${c.index + 1} | ${c.tick} | ${c.day} | \`${esc(c.subject)}\` | ${esc(c.field)} | ${esc(String(c.from)).slice(0, 90)} | ${esc(String(c.to)).slice(0, 90)} | ${esc(c.note)} |`);
    }
    if (list.length > cap) w(`| … | | | | | | | 共 ${list.length} 条，其余见 summary.json |`);
    w();
  };

  section('zones（按 id+type）', 'zones', 1);
  // zones 首末出现
  if (st.zonesMeta.size) {
    w(`zones **首末出现**：`);
    w();
    w(`| 区域 | 类型 | 标签 | 首次出现 | 末次出现 | 出现张数 |`);
    w(`| --- | --- | --- | --- | --- | --- |`);
    for (const [, z] of st.zonesMeta) {
      w(`| #${z.id} | ${esc(z.type)} | ${esc(z.label)} | 第 ${z.firstIndex + 1} 张（tick ${z.firstTick}） | 第 ${z.lastIndex + 1} 张（tick ${z.lastTick}） | ${z.present} |`);
    }
    if (st.zoneBaselineZero) w(`> 第 1 张（tick ${first.tick}）的 zones 是空的 —— **这是基线快照，不是"区域被删了"**。`);
    w();
  }
  section('storageGroups（按 loadId）', 'storageGroups', 2);
  if (!byCat('storageGroups').length) {
    w(`> storageGroups 全程 ${F.every((f) => f.counts.storageGroups === 0) ? '0 个' : '存在但无字段变化'}。见 3.1 / 3.4 的判定：这是**数据显示的 0**，`);
    w(`> 不是"分析端没看"。源码里 \`StorageGroupsForReading == null\` 会**静默返回**，所以 0 与读法失效无法在本数据内区分。`);
    w();
  }
  section('things（按 def；注意 BP: 蓝图 / FR: 框架前缀）', 'things', 3);
  section('bills（键 = bench@x,z + recipe）', 'bills', 4);
  if (!byCat('bills').length) {
    w(`> bills 全程为空（0 条），所以**最有信息量的变更点这一层在本文件里拿不到** —— 这是数据缺口，不是分析端的结论。`);
    w();
  }
  section('research（current 变化 / finished 新增）', 'research', 5);
  if (!byCat('research').length) {
    w(`> \`current\` 全程为空（且每张都有 \`currentProj 读不到\` 警告 → 见 3.2 判定为**读法失效**）；`);
    w(`> \`finishedCount\` 全程 ${F[0] ? F[0].researchFinishedCount : '-'}，本会话没有新完成的研究。`);
    w();
  }
  section('wealth（序列 + 显著变化点）', 'wealth', 6);
  w(`wealth 序列（total / items / buildings / pawns）：`);
  w();
  w(`| 第几张 | tick | day | total | items | buildings | pawns |`);
  w(`| --- | --- | --- | --- | --- | --- | --- |`);
  for (const f of F) w(`| ${f.index + 1} | ${f.tick} | ${f.day} | ${fmt(f.wealth.total, 1)} | ${fmt(f.wealth.items, 1)} | ${fmt(f.wealth.buildings, 1)} | ${fmt(f.wealth.pawns, 1)} |`);
  w();
  w(`> 显著变化点阈值：\`|Δtotal| ≥ max(50, 0.5%×上一张)\`。`);
  if (F.length) {
    const t0 = F[0].wealth.total, t1 = F[F.length - 1].wealth.total;
    const all = F.map((f) => f.wealth.total);
    w(`> 全期：${fmt(t0, 1)} → ${fmt(t1, 1)}（${t1 - t0 >= 0 ? '+' : ''}${fmt(t1 - t0, 1)}）｜区间 min ${fmt(Math.min(...all), 1)} / max ${fmt(Math.max(...all), 1)}｜单步最大跌幅 ${fmt(Math.min(0, ...F.slice(1).map((f, i) => f.wealth.total - F[i].wealth.total)), 1)}`);
  }
  w();
  section('pawns（按 name：加入/离开、期望、心情、饱食、负面想法首次出现）', 'pawns', 7);
  if (st.pawnsLeft.length) {
    w(`**离开殖民地的小人**：${st.pawnsLeft.map((p) => `${p.name}（第 ${p.lastIndex + 1} 张后消失，tick ${p.lastTick}）`).join('、')}`);
    w();
  }
  if (st.pawnMeta.size) {
    w(`**每个小人的聚合统计**（mood/food 的逐张变化太多，只留统计 + 显著变化点）：`);
    w();
    w(`| 小人 | 在场张数 | 期望（末） | mood min/max | 最大单步 mood 跌幅 | mood 变化(≥${PAWN_MOOD_EPS}) | food min/max | 最大单步 food 跌幅 | 负面想法种类 |`);
    w(`| --- | --- | --- | --- | --- | --- | --- | --- | --- |`);
    for (const [, m] of st.pawnMeta) {
      w(`| ${esc(m.name)} | ${m.lastIndex - m.firstIndex + 1} | ${esc(m.expectation || '(空)')} | ${fmt(m.mood.min, 2)}/${fmt(m.mood.max, 2)} | ${fmt(m.mood.maxDrop, 2)}（tick ${m.mood.maxDropTick ?? '-'}） | ${m.mood.events} 次（显著 ${m.mood.sig}） | ${fmt(m.food.min, 2)}/${fmt(m.food.max, 2)} | ${fmt(m.food.maxDrop, 2)}（tick ${m.food.maxDropTick ?? '-'}） | ${m.seenThoughts.size}${m.negCapped ? ' ⚠达 6 条上限，可能被截断' : ''} |`);
    }
    w();
  }
  section('designations（按 def）', 'designations', 8);

  // ── 5 基本面时序表 ──
  w(`## 5. 基本面时序表（每张快照一行）`);
  w();
  w(`| # | day | tick | 人口 | 财富 total | 心情 min | 心情 avg | 食物 min（饱食度） | 期望分布 | 研究 | 本张变更数 |`);
  w(`| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |`);
  for (const f of F) {
    const exp = Object.entries(f.expectations).map(([k, v]) => `${k}×${v}`).join(' ') || '-';
    const res = f.researchCurrent ? `${f.researchCurrent}（完成 ${f.researchFinishedCount}）` : `(空)｜完成 ${f.researchFinishedCount}`;
    w(`| ${f.index + 1} | ${f.day} | ${f.tick} | ${f.pop} | ${fmt(f.wealth.total, 1)} | ${pct(f.moodMin, 0)} | ${pct(f.moodAvg, 0)} | ${pct(f.foodMin, 0)} | ${esc(exp)} | ${esc(res)} | ${f.changes} |`);
  }
  w();
  w(`> 脚注：\`食物 min\` 是**最饿小人的饱食度百分比**（\`needs.food.CurLevelPercentage\`），不是库存。`);
  w(`> 库存的间接指标 —— 地图上口粮类物品计数（Meal*/Pemmican/RawRice/Kibble…）：${F.map((f) => `${f.tick}:${f.meals}`).join(' ')}`);
  w();

  // ── 6 动机线索 ──
  w(`## 6. 动机线索（**★ 推测**）`);
  w();
  w(`**实测**（在数据里直接读到的）与**推测**（我给的解释）分栏。推测全部由**固定规则**产生，`);
  w(`规则见 \`deriveHypotheses()\`：只在"变更点 + 同期/前一期的基本面"两个条件**同时**成立时才给候选解释 ——`);
  w(`**宁可少给，不编。**`);
  w();
  if (!st.hypotheses.length) {
    w(`本次**没有触发任何推测规则**（变更点不足以配对出解释）。这本身是一个结论：`);
    w(`本文件的关键面（bills / research.current / storageGroups）都是空的或失效的，`);
    w(`可配对的变更点主要是建筑与物品计数，不足以推断动机。`);
  } else {
    w(`| 时刻 | 实测（数据里直接读到） | 推测（★ 未经证实） | 置信 |`);
    w(`| --- | --- | --- | --- |`);
    for (const h of st.hypotheses) {
      w(`| tick ${h.tick}（day ${h.day}） | ${h.observed.map(esc).join('；')} | ★ ${esc(h.guess)} | ${h.confidence} |`);
    }
    w();
    w(`> 本节只给出 **${st.hypotheses.length}** 条推测；第 4 节共有 **${st.changes.length}** 个变更点。`);
    w(`> 其余变更点**没有触发任何规则** —— 规则刻意保守（两个条件必须同时成立），用来看"数据在，只是我还没有解释它的规则"。`);
  }
  w();

  // ── 附录：方法 ──
  w(`## 附录 A. 方法与内存口径`);
  w();
  w(`- **流式**：\`readline\` 逐行读，一行 \`JSON.parse\` 一次，解析完立刻降维，**不保留任何一行原始 JSON**。`);
  w(`- **有界**：\`things\` / \`designations\` 只留 \`{count, hash}\`；只有数量 ≤ ${MAX_CELL_SET} 的 def 才materialize 格子集合，`);
  w(`  且只保留"上一张 / 这一张"两份；最多 ${MAX_TRACKED_SETS} 个 def 进入该通道。逐张内存占用与文件总大小**无关**。`);
  w(`- **变更点**：上限 ${MAX_CHANGE_EVENTS} 条，超出只计数。`);
  w(`- **无自动化**：脚本不调用 domain / coordinator / 游戏桥接，不写任何快照数据。`);
  w();

  return L.join('\n') + '\n';
}

// ──────────────────────────────────────────────────────────────── 汇总数字

function computeDensity(st) {
  const F = st.frames;
  const recDeltas = [];      // 快照里自己报的 sincePrevTick
  const obsDeltas = [];      // 用文件里相邻两张的 tick 差**实测**的间隔
  const mismatches = [];     // 自报与实测不一致 → 录制端可能记错/丢行
  const intervals = new Set();
  let intervalNominal = 0;
  for (const f of F) {
    if (f.sampling.intervalTicks > 0) { intervals.add(f.sampling.intervalTicks); intervalNominal = Math.max(intervalNominal, f.sampling.intervalTicks); }
  }
  for (let i = 1; i < F.length; i++) {
    const rec = F[i].sampling.sincePrevTick;
    const obs = F[i].tick - F[i - 1].tick;
    recDeltas.push(rec);
    obsDeltas.push(obs);
    if (rec !== obs) mismatches.push({ index: i, tick: F[i].tick, previousTick: F[i - 1].tick, recorded: rec, observed: obs });
  }
  const threshold = Math.round(intervalNominal * 1.5);
  const dropped = [];
  for (const f of F) {
    if (f.index === 0) continue;
    const rec = f.sampling.sincePrevTick;
    const obs = f.tick - F[f.index - 1].tick;
    const reasons = [];
    if (f.sampling.looksDropped) reasons.push('looksDropped=true');
    if (intervalNominal > 0 && rec > threshold) reasons.push(`自报 Δ=${rec} > 1.5×${intervalNominal}=${threshold}`);
    if (intervalNominal > 0 && obs > threshold) reasons.push(`实测 tick 差=${obs} > ${threshold}`);
    if (reasons.length) {
      dropped.push({ index: f.index, tick: f.tick, day: f.day, sincePrevTick: rec, observedDelta: obs, threshold, looksDropped: f.sampling.looksDropped, reasons });
    }
  }
  const coveredTicks = F.length ? F[F.length - 1].tick - F[0].tick : 0;
  const coveredDays = coveredTicks / 60000;
  const perDay = coveredDays > 0 ? (F.length - 1) / coveredDays : null;   // 用区间数/天数

  // 墙钟空档
  const wallGaps = [];
  for (let i = 1; i < F.length; i++) {
    const a = hms2sec(F[i - 1].wallClock), b = hms2sec(F[i].wallClock);
    if (a === null || b === null) continue;
    let d = b - a; if (d < 0) d += 86400;
    if (d > 60) wallGaps.push({ i: i - 1, j: i, wcFrom: F[i - 1].wallClock, wcTo: F[i].wallClock, sec: d, tickFrom: F[i - 1].tick, tickTo: F[i].tick });
  }
  const wallSpanSec = F.length > 1 ? (() => {
    const a = hms2sec(F[0].wallClock), b = hms2sec(F[F.length - 1].wallClock);
    if (a === null || b === null) return null;
    let d = b - a; if (d < 0) d += 86400; return d;
  })() : null;

  const stat = (arr) => ({
    avg: arr.length ? arr.reduce((a, b) => a + b, 0) / arr.length : null,
    min: arr.length ? Math.min(...arr) : null,
    max: arr.length ? Math.max(...arr) : null,
  });
  const recStat = stat(recDeltas), obsStat = stat(obsDeltas);
  const avg = recStat.avg, min = recStat.min, max = recStat.max;

  // 结论
  let verdict, verdictReason;
  const targetPerDay = 8;      // "每游戏天至少 8 张" ← 作为"够"的下限口径
  if (F.length < 2) {
    verdict = '不够：只有基线快照，无法做任何时序分析';
    verdictReason = '快照数 < 2。';
  } else if (dropped.length > 0) {
    const worst = dropped.reduce((a, b) => (b.observedDelta > a.observedDelta ? b : a), dropped[0]);
    verdict = `不够：有 ${dropped.length} 处漏采（最长一段 实测 Δ=${worst.observedDelta} tick ≈ ${fmt(worst.observedDelta / 6000, 1)} 张的跨度）`;
    verdictReason = `漏采位置见上表；那段时间里发生了什么**永久丢失**（快照法的固有代价，只能靠补采或提高频率缓解）。`;
  } else if (mismatches.length > 0) {
    verdict = `需注意：${mismatches.length} 处"自报 Δ"与"实测 tick 差"不一致`;
    verdictReason = '录制端记的 sincePrevTick 与文件里实际的 tick 差不等 → 要么录制端记错，要么文件被动过（拼接/删行）。此时的密度数字要以"实测 tick 差"为准。';
  } else if (avg !== null && intervalNominal > 0 && Math.abs(avg / intervalNominal - 1) > 0.1) {
    verdict = `不够：平均间隔 ${fmt(avg, 1)} 与设定 ${intervalNominal} 偏差 > 10%`;
    verdictReason = '采样有漂移，跨快照的"每张 = 固定游戏时长"这个前提不成立。';
  } else if (coveredDays < 1) {
    verdict = `勉强：采样均匀（每 ${intervalNominal} tick 一张，无漏采），但只覆盖 ${fmt(coveredDays, 2)} 游戏天 < 1 天`;
    verdictReason = `每游戏天 ${fmt(perDay, 2)} 张 ≥ ${targetPerDay}，**密度够**；但**时长不够** —— 看不出"日周期"（睡眠/进食/作物生长），也看不出中长期的反馈环。要回答"玩家为什么改配置"，至少需要若干游戏天。`;
  } else if (perDay !== null && perDay >= targetPerDay) {
    verdict = `够：每游戏天 ${fmt(perDay, 2)} 张（≥ ${targetPerDay}），无漏采，覆盖 ${fmt(coveredDays, 2)} 游戏天`;
    verdictReason = `采样均匀、间隔 = 设定值。可以支撑"对象时序 + 变更点配对基本面"的分析口径。`;
  } else {
    verdict = `不够：每游戏天 ${fmt(perDay, 2)} 张 < ${targetPerDay}`;
    verdictReason = '间隔偏大，两次采样之间的动作会被合并，变更点会丢。';
  }

  return {
    intervals, intervalList: [...intervals], intervalNominal, count: F.length,
    avg, min, max, recStat, obsStat, mismatches, dropped,
    coveredTicks, coveredDays, perDay,
    theoreticalPerDay: intervalNominal ? 60000 / intervalNominal : null,
    wallSpanSec, wallGaps, verdict, verdictReason,
    deltas: recDeltas,
  };
}

function summarize(st) {
  const F = st.frames;
  const cov = st.coverage;
  return {
    file: st.file,
    relativeFile: path.relative(ROOT, st.file).replace(/\\/g, '/'),
    fileSizeBytes: st.fileSize,
    overview: {
      snapshots: F.length,
      blankLines: st.blankLines,
      badLines: st.badLines,
      badLineSamples: st.badLineSamples,
      tickFrom: F.length ? F[0].tick : null,
      tickTo: F.length ? F[F.length - 1].tick : null,
      dayFrom: F.length ? F[0].day : null,
      dayTo: F.length ? F[F.length - 1].day : null,
      wallClockFrom: F.length ? F[0].wallClock : null,
      wallClockTo: F.length ? F[F.length - 1].wallClock : null,
      mapIds: [...new Set(F.map((f) => f.mapId))],
      seqValues: [...st.seqValues],
      seqAlwaysZero: st.seqAlwaysZero,
      geo: st.geo,
      warningsTotal: st.warningsTotal,
      truncatedDefs: [...st.truncatedDefs],
    },
    density: {
      intervals: st.density.intervalList,
      intervalNominal: st.density.intervalNominal,
      count: st.density.count,
      sincePrevRecorded: { avg: st.density.recStat.avg, min: st.density.recStat.min, max: st.density.recStat.max },
      sincePrevObserved: { avg: st.density.obsStat.avg, min: st.density.obsStat.min, max: st.density.obsStat.max },
      selfReportMismatches: st.density.mismatches,
      dropped: st.density.dropped,
      droppedCount: st.density.dropped.length,
      coveredTicks: st.density.coveredTicks,
      coveredGameDays: st.density.coveredDays,
      snapshotsPerGameDay: st.density.perDay,
      theoreticalPerGameDay: st.density.theoreticalPerDay,
      wallSpanSec: st.density.wallSpanSec,
      wallGaps: st.density.wallGaps,
      verdict: st.density.verdict,
      verdictReason: st.density.verdictReason,
    },
    coverage: {
      readers: cov.readers.map((r) => ({
        id: r.id, ran: r.ran, present: r.present, missing: r.missing,
        itemsMin: r.items.min, itemsMedian: r.items.median, itemsMax: r.items.max,
        constant: r.constant, allZero: r.allZero,
        errors: r.errors, errCount: r.errCount,
        warnings: r.warnKinds,
        warnTruncatedSnapshots: r.warnTruncatedSnapshots,
        itemsSeries: r.itemsSeries,
      })),
      suspectZero: cov.suspectZero.map((r) => r.id),
      verdicts: cov.verdicts,
      broken: cov.broken.map((b) => ({ reader: b.rec.id, why: b.why, kinds: (b.kinds || []).map((k) => k.kind), errors: b.rec.errors })),
      explainedWarnings: cov.explained,
      billGiverHintsFound: cov.billGiverPresent,
      zoneTypes: cov.stockpileZoneTypes,
    },
    objectTimeline: {
      zones: [...st.zonesMeta.values()],
      storageGroups: [...st.sgMeta.values()],
      pawns: [...st.pawnMeta.entries()].map(([name, m]) => ({
        name, firstIndex: m.firstIndex, firstTick: m.firstTick, lastIndex: m.lastIndex, lastTick: m.lastTick,
        expectation: m.expectation || '', thoughts: [...m.seenThoughts.keys()],
        negCapped: !!m.negCapped,
        mood: m.mood, food: m.food,
      })),
      pawnsLeft: st.pawnsLeft,
      researchAdded: st.researchAdded,
      thoughtFirst: st.thoughtFirst,
    },
    changes: {
      count: st.changeCount,
      overflow: st.changeOverflow,
      byCategory: Object.fromEntries([...new Set(st.changes.map((c) => c.category))].map((c) => [c, st.changes.filter((x) => x.category === c).length])),
      items: st.changes,
    },
    fundamentals: F.map((f) => ({
      index: f.index, line: f.line, tick: f.tick, day: f.day, wallClock: f.wallClock, mapId: f.mapId, seq: f.seq,
      counts: f.counts, wealth: f.wealth, pop: f.pop,
      moodMin: f.moodMin, moodAvg: f.moodAvg, foodMin: f.foodMin, meals: f.meals,
      expectations: f.expectations,
      researchCurrent: f.researchCurrent, researchFinishedCount: f.researchFinishedCount,
      warnings: f.warningsThisSnap, changes: f.changes,
      sampling: f.sampling,
    })),
    hypotheses: st.hypotheses,
  };
}

// ──────────────────────────────────────────────────────────────── 控制台摘要

function renderConsole(results, opts) {
  const lines = [];   // {t, p}  p 越大越先被裁掉
  const add = (t = '', p = 9) => lines.push({ t, p });

  add(`▼ 快照分析  v${VERSION}`, 1);
  for (const st of results) {
    const F = st.frames;
    const d = st.density;
    const cov = st.coverage;
    if (results.length > 1) add('');
    add(`■ ${path.basename(st.file)}  (${(st.fileSize / 1048576).toFixed(2)} MB)`, 1);
    add(`  ${F.length} 张快照 / 坏行 ${st.badLines} / tick ${F.length ? F[0].tick : '-'}→${F.length ? F[F.length - 1].tick : '-'} / day ${F.length ? F[0].day : '-'}→${F.length ? F[F.length - 1].day : '-'} / mapId ${[...new Set(F.map((f) => f.mapId))].join(',') || '-'}`, 1);
    add(`  wallClock ${F.length ? F[0].wallClock : '-'}→${F.length ? F[F.length - 1].wallClock : '-'}（${d.wallSpanSec ?? '-'} 秒）| seq ${st.seqAlwaysZero ? '恒 0（旧构建）' : [...st.seqValues].join(',')}`, 1);

    add(`  采样：间隔 ${d.intervalList.join('/') || '-'} tick | 快照 ${d.count} | Δ自报 avg/min/max ${fmt(d.recStat.avg, 1)}/${d.recStat.min ?? '-'}/${d.recStat.max ?? '-'} | Δ实测 ${fmt(d.obsStat.avg, 1)}/${d.obsStat.min ?? '-'}/${d.obsStat.max ?? '-'}`, 2);
    add(`        漏采 ${d.dropped.length} 次 | 自报/实测不一致 ${d.mismatches.length} 处 | 覆盖 ${fmt(d.coveredDays, 2)} 游戏天 | ${fmt(d.perDay, 2)} 张/天`, 2);
    add(`        结论：${d.verdict}`, 1);
    if (d.wallGaps.length) add(`        ⚠ 墙钟空档 ${d.wallGaps.length} 处（最大 ${Math.max(...d.wallGaps.map((g) => g.sec))} 秒，tick 未漏）`, 2);

    add(`  覆盖度：`, 2);
    for (const r of cov.readers) {
      const wk = r.warnKinds.length ? ` 警告 ${r.warnKinds.length} 种(${r.warnKinds.map((k) => k.count).reduce((a, b) => a + b, 0)})` : '';
      add(`    ${r.id.padEnd(15)} ${String(r.ran).padStart(3)}/${F.length}  items ${r.items.min}/${fmt(r.items.median, 1)}/${r.items.max}${r.constant ? ' 恒定' : ''}${r.errCount ? ' error×' + r.errCount : ''}${wk}`, 2);
    }

    add(`  ★ 可疑 0 项（全程 items=0 且 error 空）：${cov.suspectZero.length ? cov.suspectZero.map((r) => r.id).join(', ') : '无'}`, 1);
    for (const v of cov.verdicts) add(`      - ${v.reader}：${v.level}｜${v.why}`, 1);

    const realBroken = cov.broken.filter((b) => !(b.kinds || []).every((k) => /截断/.test(k.kind)) || b.rec.errCount);
    add(`  ★ 读法失效：${realBroken.length ? realBroken.map((b) => b.rec.id).join(', ') : '无'}`, 1);
    for (const b of realBroken) {
      const k = (b.kinds || [])[0];
      add(`      - ${b.rec.id}：${b.why}${k ? `｜"${k.sample}" ×${k.count}（${k.snapshots}/${b.rec.ran} 张）` : ''}`, 1);
    }
    for (const e of cov.explained) add(`  ⚠ 机械命中但已解释：${e.reader}｜${e.kind} ×${e.count}（设计截断上限，计数为下界）`, 2);

    const cats = ['zones', 'storageGroups', 'things', 'bills', 'research', 'wealth', 'pawns', 'designations'];
    const cnt = Object.fromEntries(cats.map((c) => [c, st.changes.filter((x) => x.category === c).length]));
    add(`  对象变化点 ${st.changeCount} 条：` + cats.map((c) => `${c} ${cnt[c]}`).join(' | '), 2);
    const notable = st.changes.filter((c) => c.category === 'things' || c.category === 'bills').slice(0, 6);
    for (const c of notable) add(`    · [${c.category}] ${c.subject} ${c.field} ${c.from}→${c.to}（tick ${c.tick}）${c.note ? '｜' + c.note : ''}`, 3);
    if (F.length) {
      const f0 = F[0], f1 = F[F.length - 1];
      add(`  基本面：人口 ${f0.pop}→${f1.pop}｜财富 ${fmt(f0.wealth.total, 0)}→${fmt(f1.wealth.total, 0)}｜心情min ${pct(f0.moodMin, 0)}→${pct(f1.moodMin, 0)}｜饱食min ${pct(f0.foodMin, 0)}→${pct(f1.foodMin, 0)}｜研究 ${f1.researchCurrent || '(空)'}`, 2);
    }
    add(`  动机线索（★推测）：${st.hypotheses.length} 条` + (st.hypotheses.length ? `（首个：tick ${st.hypotheses[0].tick} ★${st.hypotheses[0].guess.slice(0, 46)}…）` : '（规则未触发，见报告第 6 节）'), 2);
    add(`  → ${path.relative(ROOT, st.outMd).replace(/\\/g, '/')}`, 1);
    add(`  → ${path.relative(ROOT, st.outJson).replace(/\\/g, '/')}`, 1);
  }

  if (lines.length > MAX_CONSOLE_LINES) {
    const keep = [...lines].sort((a, b) => (a.p - b.p) || 0).slice(0, MAX_CONSOLE_LINES - 1);
    const keepSet = new Set(keep);
    const out = lines.filter((x) => keepSet.has(x)).map((x) => x.t);
    out.push(`… 控制台摘要已按优先级裁剪（原 ${lines.length} 行 → ${MAX_CONSOLE_LINES} 行上限）；完整内容见报告的 2/3/4/5/6 节。`);
    return out;
  }
  return lines.map((x) => x.t);
}

// ──────────────────────────────────────────────────────────────── 入口

function printHelp() {
  console.log(`用法：node scripts/analyze-snapshots.mjs [文件...] [--out <报告路径>] [--json]

  不给文件 → 用 ${path.relative(ROOT, SNAPSHOT_DIR).replace(/\\/g, '/')} 里最新的一份 .jsonl
  --out     单文件时=报告 .md 路径；多文件时=输出目录（默认 runs/snapshot-analysis/）
  --json    stdout 输出机器可读 summary（报告文件照写）

输出：
  runs/snapshot-analysis/<快照文件名>.report.md
  runs/snapshot-analysis/<快照文件名>.summary.json`);
}

function pickLatest() {
  if (!fs.existsSync(SNAPSHOT_DIR)) return null;
  const files = fs.readdirSync(SNAPSHOT_DIR).filter((f) => f.endsWith('.jsonl'));
  if (!files.length) return null;
  const withStat = files.map((f) => {
    const p = path.join(SNAPSHOT_DIR, f);
    return { p, m: fs.statSync(p).mtimeMs };
  });
  withStat.sort((a, b) => b.m - a.m);
  return withStat[0].p;
}

function outPathsFor(file, outArg, multi) {
  const base = path.basename(file);
  ensureDir(OUT_DIR);
  let mdPath, jsonPath;
  if (outArg) {
    const resolved = path.resolve(outArg);
    const isDir = multi || /[\\/]$/.test(outArg) || (fs.existsSync(resolved) && fs.statSync(resolved).isDirectory());
    if (isDir) {
      ensureDir(resolved);
      mdPath = path.join(resolved, base + '.report.md');
      jsonPath = path.join(resolved, base + '.summary.json');
    } else {
      ensureDir(path.dirname(resolved));
      mdPath = resolved;
      jsonPath = /\.md$/i.test(resolved) ? resolved.replace(/\.md$/i, '') + '.summary.json' : resolved + '.summary.json';
    }
  } else {
    mdPath = path.join(OUT_DIR, base + '.report.md');
    jsonPath = path.join(OUT_DIR, base + '.summary.json');
  }
  return { mdPath, jsonPath };
}

async function main() {
  const argv = process.argv.slice(2);
  const files = [];
  let outArg = null, jsonOut = false;
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--out') { outArg = argv[++i]; if (!outArg) { console.error('--out 需要一个路径'); process.exit(2); } }
    else if (a.startsWith('--out=')) outArg = a.slice(6);
    else if (a === '--json') jsonOut = true;
    else if (a === '-h' || a === '--help') { printHelp(); return; }
    else if (a.startsWith('--')) { console.error('未知参数：' + a); printHelp(); process.exit(2); }
    else files.push(a);
  }

  let inputs = files.map((f) => path.resolve(f));
  if (!inputs.length) {
    const latest = pickLatest();
    if (!latest) { console.error(`没找到快照文件：${SNAPSHOT_DIR}`); process.exit(1); }
    inputs = [latest];
  }
  for (const f of inputs) {
    if (!fs.existsSync(f)) { console.error('文件不存在：' + f); process.exit(1); }
  }

  const multi = inputs.length > 1;
  const results = [];
  for (const f of inputs) {
    const st = await analyzeFile(f);
    st.coverage = analyzeCoverage(st);
    st.density = computeDensity(st);
    const { mdPath, jsonPath } = outPathsFor(f, outArg, multi);
    st.outMd = mdPath;
    st.outJson = jsonPath;

    const md = renderMarkdown(st);
    fs.writeFileSync(mdPath, md, 'utf8');
    const summary = {
      generatedAt: new Date().toISOString(),
      script: 'scripts/analyze-snapshots.mjs',
      version: VERSION,
      workspaceRoot: ROOT,
      files: [summarize(st)],
    };
    fs.writeFileSync(jsonPath, JSON.stringify(summary, null, 2) + '\n', 'utf8');
    st.summary = summary;
    results.push(st);
  }

  if (jsonOut) {
    const merged = {
      generatedAt: new Date().toISOString(),
      script: 'scripts/analyze-snapshots.mjs',
      version: VERSION,
      inputs: inputs.map((f) => path.relative(ROOT, f).replace(/\\/g, '/')),
      files: results.flatMap((r) => r.summary.files),
    };
    process.stdout.write(JSON.stringify(merged, null, 2) + '\n');
  } else {
    process.stdout.write(renderConsole(results, { multi }).join('\n') + '\n');
  }
}

main().catch((e) => {
  console.error('[analyze-snapshots] 失败：' + (e && e.stack ? e.stack : e));
  process.exit(1);
});
