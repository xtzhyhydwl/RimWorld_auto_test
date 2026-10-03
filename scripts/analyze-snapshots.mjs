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
 * ## 新格式（录制器 2026-10-03 追加面）
 * - 头部追加 8 个字段：`absTick` / `year` / `quadrum` / `dayOfQuadrum` / `dayOfYear`
 *   / `dateText` / `colonyName` / `mapName`（在 `wallClock` 之后、`zones` 之前）。
 * - 追加三个顶层面：`pawnDetails[]` / `rooms[]` / `materials{}`。
 * - ⚠ **`day` 的语义**：`day = tick/60000` 是「**殖民地建立以来**的天数」，
 *   **不是游戏内日期**。有 `absTick` 时报告必须同时给出 `dateText` / `year` /
 *   `quadrum` / `dayOfYear`，并且**把两者分开放**（用户被这个绕过）。
 * - **老格式必须一字不变**：没有新字段的快照走的是**完全相同的渲染路径**，
 *   只是在末尾**追加**新节的渲染；老样本的输出应与旧版逐字节一致。
 *
 * ## `--focus`（"用不同的模式去切换我们关注的点"）
 * `--focus all|rooms|materials|buildings|pawns|basics`（默认 `all`）：
 * 决定**第 4 节哪些表要展开**、以及控制台摘要与"关注点速览"要突出什么。
 * 每个变更点都带一个 `focus` 归属（见 `FOCUS_OF`），因此切换关注点 =
 * 换一个过滤器，而不是换一套读法。
 *
 * ## 多文件拼接
 * 同一个殖民地**跨会话会分成多个文件**（读档新开文件）。允许多个文件：
 * 按 `absTick`（无则 `tick`）排序、**去重叠**（同一 tick 出现两次保留后一次），
 * 并在报告里说清"拼了哪几个文件、有没有重叠 / 缺口"。单文件行为不变
 * （不生成拼接列表，不改标题与文件名）。
 *
 * 用法：
 *   node scripts/analyze-snapshots.mjs [文件...] [--out <报告路径>] [--json]
 *                                      [--focus all|rooms|materials|buildings|pawns|basics]
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

const VERSION = '1.1.0';

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

// ──────────────────────────────────────────────── 关注点（--focus）

/** 合法关注点（`all` 之外每个都对应一条"只看这一面"的过滤器） */
const FOCUS_LEVELS = ['all', 'rooms', 'materials', 'buildings', 'pawns', 'basics'];

/**
 * 变更点 → 它属于哪个关注点。
 * ★ 这是"切换关注点"的全部机制：**读法不变，只换过滤器**。
 *   分类只按 `category` + `field` 判断，不看数值，所以不会因为数据不同而漂移。
 */
function focusOfChange(c) {
  if (c.category === 'rooms' || c.category === 'zones') return 'rooms';
  if (c.category === 'materials') return 'materials';
  if (c.category === 'things' || c.category === 'bills' || c.category === 'designations' || c.category === 'wealth') return 'buildings';
  if (c.category === 'pawns' || c.category === 'pawnDetails') return 'pawns';
  if (c.category === 'research' || c.category === 'storageGroups') return 'buildings';
  return 'buildings';
}

const FOCUS_LABEL = {
  all: '全部',
  rooms: '房间（职能变化 / 隔开合并 / 墙地材质）',
  materials: '建筑材料迁移',
  buildings: '建筑与蓝图增减',
  pawns: '殖民者明细',
  basics: '只看基本面（概览/采样/覆盖度/health）',
};

/** `rooms` 里"值得点名"的事件（隔开 / 职能变化 / 换墙 / 地板从无到有） */
const ROOM_NOTABLE = new Set(['隔开', '合并', 'role', 'wallMaterials', 'floorNew']);
/** 房间材质的"从无到有"判定：上一张该地板为 0 格 */
const MATERIAL_SHARE_BIG = 0.10;   // 材质占比跨过 10% 就算"关键切换点"
const MATERIAL_MIN_BUILDINGS = 5;  // 样本太少的材质不参与时间线（避免 1 个木墙刷屏）

/** `pawnDetails.kind` 的判读（只记玩家殖民地的人） */
const PAWN_KIND_LABEL = {
  Colonist: '殖民者', Slave: '奴隶', Prisoner: '囚犯', Guest: '访客',
  ColonistMech: '殖民者机械体', Subhuman: '亚人', Animal: '动物', Other: '其他',
};
const kindLabel = (k) => PAWN_KIND_LABEL[String(k || '')] || String(k || '(空)');

/** 大数组的防御性上限（内存有界：异常快照不能把整张 map 拖进来） */
const MAX_ROOMS = 400;
const MAX_PAWN_DETAILS = 300;
const MAX_MATERIAL_DEFS = 300;

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

/** 字符串数组顺序无关的稳定键（`apparel` 的顺序不保证跨快照一致） */
const sortedKey = (arr) => (Array.isArray(arr) ? [...arr].map(String).sort() : []).join('\u0001');

/** `{k:count}` 计数 map：稳定的"键:值"串（用于"材质构成有没有变"） */
function countMapKey(m) {
  if (!m || typeof m !== 'object') return '';
  return Object.keys(m).sort().map((k) => k + ':' + m[k]).join('\u0001');
}

/** `{k:v}` 单层 map 的浅比较（材质 / 建筑构成用） */
const countMapEqual = (a, b) => countMapKey(a) === countMapKey(b);

/** 判断一份快照是不是**新格式**（有追加字段 / 追加面） */
function detectNewFormat(snap) {
  if (Number.isFinite(snap.absTick) && snap.absTick > 0) return true;
  if (typeof snap.dateText === 'string' && snap.dateText) return true;
  if (Array.isArray(snap.rooms) || Array.isArray(snap.pawnDetails)) return true;
  if (snap.materials && typeof snap.materials === 'object' && Object.keys(snap.materials).length) return true;
  if (typeof snap.year === 'number' && snap.year > 0) return true;
  return false;
}

/** 新格式头部的 8 个字段（**逐字**按录制器 `ToJsonLine` 的名字） */
function readNewHeader(snap) {
  return {
    absTick: n1(snap.absTick),
    year: n1(snap.year),
    quadrum: String(snap.quadrum || ''),
    dayOfQuadrum: n1(snap.dayOfQuadrum),
    dayOfYear: n1(snap.dayOfYear),
    dateText: String(snap.dateText || ''),
    colonyName: String(snap.colonyName || ''),
    mapName: String(snap.mapName || ''),
  };
}

/** 日期文本：优先 `dateText`，否则用 year/quadrum/dayOfYear 拼一个（**标明是拼的**） */
function dateTextOf(h) {
  if (!h) return '';
  if (h.dateText) return h.dateText;
  if (h.year !== null && h.quadrum) return `${h.year} ${h.quadrum} 第${h.dayOfYear ?? '?'}天（脚本拼接）`;
  return '';
}

// ──────────────────────────────────────────────────────────── 新格式的抽取与判定

/**
 * ★ **材质迁移**：把 `materials{建筑def:{材质def:个数}}` 聚合成
 * "这一张里每种材质用了多少个建筑"，并算占比。
 * ★ 同时算一个**不依赖材质字典**的交叉校验量 `byDefWood`：
 *   `materials` 那一面若读法失效（空 map），这里就是 0，报告要能看出来。
 */
function computeMaterialTotals(materials) {
  const byMat = new Map();
  let total = 0, defs = 0;
  for (const [def, inner] of Object.entries(materials || {})) {
    if (!inner || typeof inner !== 'object') continue;
    defs++;
    for (const [mat, n] of Object.entries(inner)) {
      const v = n0(n);
      if (!v) continue;
      byMat.set(mat, (byMat.get(mat) || 0) + v);
      total += v;
    }
  }
  const shares = new Map();
  for (const [mat, v] of byMat) shares.set(mat, total ? v / total : 0);
  return { byMat, shares, total, defs };
}

/** 房间职责的紧凑摘要：`{ id: "Role" }`（用于"同一批 id 的职能变没变"） */
function summarizeRoomRoles(rooms) {
  const o = {};
  for (const r of rooms) {
    if (!r || typeof r !== 'object') continue;
    o[n0(r.id)] = String(r.role || '');
  }
  return o;
}

/** 一张快照的紧凑房间记录（只留隔开/合并判定要用的量） */
function readRoomFrame(rooms) {
  const out = [];
  for (const r of rooms) {
    if (!r || typeof r !== 'object') continue;
    const rect = Array.isArray(r.rect) ? r.rect.map(n0) : [0, 0, 0, 0];
    out.push({
      id: n0(r.id), role: String(r.role || ''), roleLabel: String(r.roleLabel || ''),
      label: String(r.label || ''), cellCount: n0(r.cellCount),
      rect: rect.slice(0, 4), rectKey: rect.slice(0, 4).join(','),
      roofed: r.roofed === true, openRoofCount: n0(r.openRoofCount),
      isPrisonCell: r.isPrisonCell === true,
      temperature: n1(r.temperature), beauty: n1(r.beauty),
      wallKey: countMapKey(r.wallMaterials), wallMaterials: r.wallMaterials || {},
      floorKey: countMapKey(r.floorMaterials), floorMaterials: r.floorMaterials || {},
      buildKey: countMapKey(r.buildings), buildings: r.buildings || {},
    });
  }
  return out;
}

/** `[x,z,w,h]` 的包围盒面积（只用于报告里的"原来那间多大"参考） */
const rectArea = (r) => (r ? Math.max(0, r[2]) * Math.max(0, r[3]) : 0);

/**
 * ★ **房间的"出现 / 隔开 / 合并 / 边界突变"判定** —— 用户最在意的那几条。
 *
 * 做法（全部只看**对象**，不做过程推断）：
 * - 一把快照里**消失**的房间 vs **新增**的房间，按"格数与包围盒面积是否吻合"配对；
 * - 一个消失 + 两个（或更多）新增、且新增的**合起来和原来差不多大** → **隔开**；
 * - 反向（两个消失 + 一个新增）→ **合并**；
 * - 同一个 id 前后 `rect` 变了 → **边界突变**；
 * - 同一个 id 前后 `role` 变了 → 在 `diffObjectState` 里就记了（职能变化）。
 *
 * 为什么单独一遍：`role` / 边界这类是"**同一个 id** 的属性变化"，逐张 diff 就够；
 * 而"隔开"是"**一个 id 消失 + 其它 id 出现**"的**跨对象**关系，必须把
 * "这一张新增的房间"和"上一张消失的房间"放在一起配对 —— 所以收尾时统一算。
 *
 * ※ 判定基于 `rooms[].id`：RimWorld 的 `Room.ID` 在同一存档内稳定，
 *   但**读档后可能重排**；所以"隔开"必须跟"id 消失+新增"一起看，不能只看 id。
 */
function deriveRoomEvents(st) {
  const idxs = [...st.roomFrames.keys()].sort((a, b) => a - b);
  let prevList = null;
  for (const i of idxs) {
    const list = st.roomFrames.get(i);
    const f = st.frames[i];
    const at = { index: i, tick: f.tick, day: f.day, absTick: f.absTick ?? null, dateText: f.dateText || '' };
    const curIds = new Set(list.map((r) => r.id));
    const prevIds = prevList ? new Set(prevList.map((r) => r.id)) : null;

    if (prevList) {
      const appeared = list.filter((r) => !prevIds.has(r.id));
      const vanished = prevList.filter((r) => !curIds.has(r.id));

      // 逐个登记"出现"（与"消失"同一套措辞，便于对照）
      for (const r of appeared) {
        pushChange(st, i, {
          ...at, category: 'rooms', subject: `房间#${r.id}`, field: '出现', from: '-',
          to: `${r.role || '(空)'} ${r.cellCount}格 [${r.rect.join(',')}]`,
          note: '首个快照之后新增（是否由"隔开"造成见本张的隔开事件）', focus: 'rooms',
        });
      }
      // 逐个登记"消失"
      for (const r of vanished) {
        pushChange(st, i, {
          ...at, category: 'rooms', subject: `房间#${r.id}`, field: '消失',
          from: `${r.role || '(空)'} ${r.cellCount}格 [${r.rect.join(',')}]`, to: '-',
          note: '该 id 本张不再出现（是否"被合并"见本张的合并事件）', focus: 'rooms',
        });
      }

      // ── 隔开：1 个消失 + ≥2 个新增，且格数吻合（±35%） ──
      if (vanished.length === 1 && appeared.length >= 2) {
        const gone = vanished[0];
        const sumCells = appeared.reduce((a, r) => a + r.cellCount, 0);
        const close = gone.cellCount > 0 && Math.abs(sumCells - gone.cellCount) <= Math.max(4, gone.cellCount * 0.35);
        st.roomSplits.push({
          ...at, kind: '隔开', goneId: gone.id, goneRole: gone.role, goneLabel: gone.label,
          goneCells: gone.cellCount, newRooms: appeared.map((r) => ({ id: r.id, role: r.role, roleLabel: r.roleLabel, cellCount: r.cellCount, rect: r.rect })),
          cellsClose: close, confidence: close ? '高' : '中',
        });
        pushChange(st, i, {
          ...at, category: 'rooms', subject: `房间#${gone.id}（${gone.role || '(空)'}）`, field: '隔开',
          from: `1 间 ${gone.cellCount} 格 [${gone.rect.join(',')}]`,
          to: `${appeared.length} 间 ` + appeared.map((r) => `#${r.id}(${r.role || '(空)'},${r.cellCount}格)`).join(' + '),
          note: close
            ? `★ 格数吻合（${gone.cellCount} ≈ ${sumCells}）→ 高置信"被一隔为二"`
            : `格数不吻合（${gone.cellCount} vs ${sumCells}）→ 可能是重排/多次改动叠加`,
          focus: 'rooms',
        });
      }
      // ── 合并：≥2 个消失 + 1 个新增，格数吻合 ──
      if (vanished.length >= 2 && appeared.length === 1) {
        const now = appeared[0];
        const sumCells = vanished.reduce((a, r) => a + r.cellCount, 0);
        const close = now.cellCount > 0 && Math.abs(sumCells - now.cellCount) <= Math.max(4, sumCells * 0.35);
        st.roomSplits.push({
          ...at, kind: '合并', goneId: vanished.map((r) => r.id).join('+'), goneCells: sumCells,
          newRooms: [{ id: now.id, role: now.role, roleLabel: now.roleLabel, cellCount: now.cellCount, rect: now.rect }],
          cellsClose: close, confidence: close ? '高' : '中',
        });
        pushChange(st, i, {
          ...at, category: 'rooms', subject: vanished.map((r) => `房间#${r.id}`).join('+'), field: '合并',
          from: `${vanished.length} 间 ${sumCells} 格`, to: `1 间 #${now.id}（${now.role || '(空)'}，${now.cellCount}格）`,
          note: close ? '★ 格数吻合 → 高置信"合并成一间"' : '格数不吻合 → 可能是重排/多次改动叠加', focus: 'rooms',
        });
      }
      // ── 边界突变（同一 id 的 rect 变了）──
      const prevById = new Map(prevList.map((r) => [r.id, r]));
      for (const r of list) {
        const p = prevById.get(r.id);
        if (!p) continue;
        if (p.rectKey !== r.rectKey) {
          pushChange(st, i, {
            ...at, category: 'rooms', subject: `房间#${r.id}`, field: 'rect',
            from: `[${p.rectKey}] cellCount=${p.cellCount}`, to: `[${r.rectKey}] cellCount=${r.cellCount}`,
            note: '同一 id 的边界 / 格数变了（可能被并进/切出一块）', focus: 'rooms',
          });
        }
      }
    } else {
      // 基线：只登记首见，不产生变更点（与其它对象一致）
      for (const r of list) {
        const meta = st.roomMeta.get(String(r.id));
        if (meta) { meta.firstIndex = i; meta.firstTick = f.tick; meta.firstDay = f.day; }
      }
    }
    prevList = list;
  }
}

/** 新格式小人的紧凑记录 → `pawns` 明细的交叉核对 */
function buildPawnDetailMap(pawnDetails, capped) {
  const m = new Map();
  for (const d of pawnDetails) {
    if (!d || typeof d !== 'object') continue;
    if (m.size >= MAX_PAWN_DETAILS) { capped.hit = true; break; }
    const name = String(d.name || '');
    const skills = new Map();
    for (const s of Array.isArray(d.skills) ? d.skills : []) {
      if (!s || typeof s !== 'object') continue;
      skills.set(String(s.def || ''), { level: n0(s.level), passion: String(s.passion || '') });
    }
    const caps = new Map();
    const h = d.health && typeof d.health === 'object' ? d.health : {};
    for (const c of Array.isArray(h.capacities) ? h.capacities : []) {
      if (!c || typeof c !== 'object') continue;
      caps.set(String(c.def || ''), n1(c.level));
    }
    const wp = new Map();
    for (const w of Array.isArray(d.workPriorities) ? d.workPriorities : []) {
      if (!w || typeof w !== 'object') continue;
      wp.set(String(w.workType || ''), n0(w.priority));
    }
    const apparel = Array.isArray(d.apparel) ? d.apparel.map(String) : [];
    m.set(name, {
      name, kind: String(d.kind || ''), gender: String(d.gender || ''), age: n1(d.age),
      weapon: String(d.weapon || ''), apparel, apparelKey: sortedKey(apparel),
      traits: Array.isArray(d.traits) ? d.traits.map(String) : [],
      skills, caps,
      healthSummary: String(h.summary || ''), downed: h.downed === true,
      mood: n1(d.mood), food: n1(d.food), expectation: String(d.expectation || ''),
      wp, wpKey: [...wp.entries()].sort((a, b) => (a[0] < b[0] ? -1 : 1)).map(([k, v]) => k + ':' + v).join('\u0001'),
    });
  }
  return m;
}

// ──────────────────────────────────────────────────────────────── 分析主体

/**
 * 从一张"对象状态"里抽出**轻量预览**：跨文件拼接时要按 tick 重排后再 diff，
 * 所以要把"能重建 diff 输入的最小信息"留在内存里（不是原始 JSON）。
 * ★ 有界：只留计数 / 哈希 / ≤200 的对象集合（与 `buildObjectState` 的口径一致）。
 */
function previewOf(st, cur, frame, index, tick) {
  return {
    index,
    fileName: st.file,
    fileIndex: index,
    tick,
    zones: cur.zones,
    storageGroups: cur.storageGroups,
    things: cur.things,
    bills: cur.bills,
    pawns: cur.pawns,
    rooms: cur.rooms,
    materials: cur.materials,
    pawnDetails: cur.pawnDetails,
    researchFinished: cur.researchFinished,
    designations: cur.designations,
    wealthTotal: frame.wealth.total,
    researchCurrent: frame.researchCurrent,
  };
}

/** 把 `health` 里每个 reader 的一条记录累加进"合并后的覆盖度"（多文件拼接用） */
function bumpMergedReader(st, id, h, index) {
  let r = st.mergedReaders.get(id);
  if (!r) { r = newReaderStat(id); r.items = []; st.mergedReaders.set(id, r); st.mergedReaderOrder.push(id); }
  r.present++;
  if (h.ran) r.ran++;
  r.items.push({ i: index, v: n0(h.items) });   // 带序号，拼接后要按新序号排序
  const err = String(h.error || '');
  if (err) r.errors.set(err, (r.errors.get(err) || 0) + 1);
  const warns = Array.isArray(h.warnings) ? h.warnings : [];
  if (warns.length >= 20) r.warnTruncated++;
  for (const w of warns) {
    const kind = normalizeWarning(w);
    let rec = r.warnKinds.get(kind);
    if (!rec) { rec = { count: 0, snapshots: new Set(), sample: String(w) }; r.warnKinds.set(kind, rec); }
    rec.count++;
    rec.snapshots.add(index);
  }
}

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
    // ── ★ 多文件拼接（单文件时这两个都空着，一切照旧）──
    previews: [],              // 可重建 diff 的最小状态（只在多文件模式下收集）
    mergedReaders: new Map(),  // 合并后的覆盖度累加（含警告出现的"合并序号"）
    mergedReaderOrder: [],
    thingDefsSeen: new Set(),
    seqAlwaysZero: true,
    seqValues: new Set(),
    warningsTotal: 0,
    // ── ★ 新格式（录制器追加面）。老文件全程保持"无"状态，一个字都不印 ──
    hasNewFormat: false,       // 见过至少一张带追加字段/面的快照
    newFormatRows: 0,          // 带追加字段的行数（用于"部分新格式"的提示）
    header: null,              // { absTick, year, quadrum, dayOfQuadrum, dayOfYear, dateText, colonyName, mapName }
    headerFirstDate: '',       // 首张的日期文本（末张由 header 自己带）
    focus: 'all',              // --focus 的取值（渲染时读）
    absTickAlwaysMatchesTick: true, // absTick - tick 是否恒定（跨会话拼接要靠它）
    absTickOffsets: new Set(), // absTick - tick 的取值集合（读档后重置 → 多个值）
    roomMeta: new Map(),       // 房间对象时序（key = id）
    roomFrames: new Map(),     // index → [{id, cellCount, rectKey}]（隔开/合并判定用）
    roomSplits: [],            // 隔开 / 合并事件
    roomRoleChanges: [],       // 房间职能变化（用户最在意的那一条）
    materialSeries: [],        // 每张的材质聚合 { index, tick, day, absTick, total, byMat, shares }
    materialSwitches: [],      // 关键切换点
    pawnDetailMeta: new Map(), // 殖民者明细的对象时序（key = name）
    pawnDetailIssues: [],      // pawnDetails 与 pawns 对不上的地方
    pawnDetailCapped: 0,
    materialsDefsSeen: new Set(),
    roomsSeen: 0,
  };
}

function pushChange(st, index, c) {
  if (st.changes.length >= MAX_CHANGE_EVENTS) { st.changeOverflow++; return; }
  const rec = {
    index, tick: c.tick, day: c.day, category: c.category, subject: c.subject, field: c.field,
    from: c.from, to: c.to, note: c.note || '',
    // ★ 关注点归属：不是调用方显式指定，就是按 category/field 推
    focus: c.focus || focusOfChange(c),
  };
  if (c.absTick !== undefined && c.absTick !== null) rec.absTick = c.absTick;
  if (c.dateText) rec.dateText = c.dateText;
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

/**
 * 逐行流式处理一个文件。
 * @param {object} [opt]
 * @param {boolean} [opt.keepPreviews] 多文件拼接时留"可重建 diff 的最小状态"
 * @param {boolean} [opt.collectMergedReaders] 同时累加"合并后的覆盖度"
 * @param {number}  [opt.ordinal] 输入顺序（拼接时的稳定 tie-break）
 */
async function analyzeFile(file, opt = {}) {
  const st = newState(file);
  st.ordinal = n0(opt.ordinal);
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

    prev = processSnapshot(st, snap, { index, tick, day, wallClock, line: st.lineCount, keepPreviews: !!opt.keepPreviews }, prev, opt);
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
  // ★ 单文件：覆盖度累加已经是"当前的"，直接交给 `analyzeCoverage`
  if (!opt.multi) {
    for (const [id, r] of st.mergedReaders) st.readers.set(id, r);
    st.readerOrder = st.mergedReaderOrder.slice();
  }
  // ★ 房间的"出现 / 隔开 / 合并 / 边界突变"要等所有张都读完才能配对
  if (st.roomFrames.size) deriveRoomEvents(st);

  st.hypotheses = deriveHypotheses(st);
  return st;
}

/**
 * ★ **多文件拼接**（同一个殖民地跨会话会分成多个文件，因为读档会新开文件）。
 *
 * 做法：
 * 1. 把每个文件的快照按 `absTick`（没有则 `tick`）排序；同序号的按**输入顺序** tie-break
 *    —— 命令行顺序就是"会话先后"的人提供的证据，不许被 tick 排序打乱；
 * 2. **去重叠**：同一个 (`tick`) 出现两次时**保留后一次**（后一次是更新的一份）；
 * 3. 在**拼好的序列**上重跑一遍对象 diff —— 跨文件的边界（一张在文件 A 末尾、
 *    下一张在文件 B 开头）也算一次真实的相邻比较，否则跨会话的变化会被漏掉；
 * 4. 报告里说清"拼了哪几个文件、哪个文件到哪个文件接得上、有没有重叠 / 缺口"。
 *
 * @returns {object} 合并后的 state（与 `analyzeFile` 的返回同构，多一个 `merge` 字段）
 */
function mergeStates(states) {
  const merged = newState('<merged>');
  merged.ordinal = 0;
  merged.merge = {
    files: states.map((s, i) => ({
      ordinal: i, file: s.file, relative: path.relative(ROOT, s.file).replace(/\\/g, '/'),
      fileSizeBytes: s.fileSize, snapshots: s.frames.length,
      tickFrom: s.frames.length ? s.frames[0].tick : null,
      tickTo: s.frames.length ? s.frames[s.frames.length - 1].tick : null,
      dayFrom: s.frames.length ? s.frames[0].day : null,
      dayTo: s.frames.length ? s.frames[s.frames.length - 1].day : null,
      absTickFrom: s.frames.length ? (s.frames[0].absTick ?? null) : null,
      absTickTo: s.frames.length ? (s.frames[s.frames.length - 1].absTick ?? null) : null,
      dateFrom: s.headerFirstDate || '',
      dateTo: s.header ? dateTextOf(s.header) : '',
      hasNewFormat: s.hasNewFormat,
      blankLines: s.blankLines, badLines: s.badLines,
    })),
    boundaries: [],
    overlaps: [],
    overlapsDropped: 0,
    gaps: [],
    gapsDropped: 0,
    orderNote: '',
  };

  // ── 1/2 排序 + 去重叠 ──
  const recs = [];
  for (const s of states) for (const p of s.previews) recs.push(p);
  recs.sort((a, b) => (a.tick - b.tick) || (a.ordinal - b.ordinal) || (a.fileIndex - b.fileIndex));
  const seenTick = new Set();
  const kept = [];
  for (const r of recs) {
    if (seenTick.has(r.tick)) {
      merged.merge.overlapsDropped++;
      if (merged.merge.overlaps.length < 50) {
        merged.merge.overlaps.push({ tick: r.tick, droppedFrom: path.basename(r.fileName) });
      }
      continue;    // 保留后一次（`recs` 已排序，后出现的覆盖前面的）
    }
    seenTick.add(r.tick);
    kept.push(r);
  }
  merged.merge.inputSnapshots = recs.length;
  merged.merge.mergedSnapshots = kept.length;

  // ── 3 重跑 diff（注意：`kept` 里的 `index` 是**文件内**序号，要换成合并序号）──
  if (kept.length) {
    const idxRemap = new Map();
    for (let i = 0; i < kept.length; i++) {
      idxRemap.set(kept[i].fileName + '#' + kept[i].index, i);
      kept[i].mergedIndex = i;
    }
    merged.merge.idxRemap = idxRemap;
    for (const s of states) {
      for (const [id, r] of s.mergedReaders) {
        let m = merged.mergedReaders.get(id);
        if (!m) { m = newReaderStat(id); m.items = []; merged.mergedReaders.set(id, m); merged.mergedReaderOrder.push(id); }
        for (const it of r.items) {
          const nk = s.file + '#' + it.i;
          if (idxRemap.has(nk)) m.items.push({ i: idxRemap.get(nk), v: it.v });
        }
        m.present += r.present; m.ran += r.ran;
        for (const [k, v] of r.errors) m.errors.set(k, (m.errors.get(k) || 0) + v);
        m.warnTruncated += r.warnTruncated;
        for (const [kind, rec] of r.warnKinds) {
          let mr = m.warnKinds.get(kind);
          if (!mr) { mr = { count: 0, snapshots: new Set(), sample: rec.sample }; m.warnKinds.set(kind, mr); }
          mr.count += rec.count;
          for (const si of rec.snapshots) {
            const nk = s.file + '#' + si;
            if (idxRemap.has(nk)) mr.snapshots.add(idxRemap.get(nk));
          }
        }
      }
    }

    let prev = null;
    for (const r of kept) {
      const i = r.mergedIndex;
      const f = states[r.ordinal].frames[r.index];
      f.index = i;                      // ★ 合并序号写回 frame（第 5 节 / 密度 / 房间事件都依赖它）
      merged.frames.push(f);
      r.index = i;
      if (prev) {
        diffObjectState(merged, prev, r, { index: i, tick: r.tick, day: f.day, absTick: f.absTick ?? null, dateText: f.dateText || '' });
      } else {
        seedBaseline(merged, r, { index: i, tick: r.tick, day: f.day });
      }
      f.changes = (merged.changesByIndex.get(i) || []).length;
      prev = r;
    }
    const lastIndex = merged.frames.length - 1;
    for (const [name, meta] of merged.pawnMeta) {
      if (meta.lastIndex < lastIndex) {
        merged.pawnsLeft.push({ name, firstIndex: meta.firstIndex, firstTick: meta.firstTick, lastIndex: meta.lastIndex, lastTick: meta.lastTick });
      }
    }
    // 房间的"紧凑记录"也跟着新序号走
    for (const [i, list] of [...merged.roomFrames.entries()]) merged.roomFrames.set(i, list);
  }
  if (merged.roomFrames.size === 0) {
    // 拼接模式下 roomFrames 是上面按新序号填的；若为空则显式从各文件搬（保险）
    for (const s of states) {
      for (const [fi, list] of s.roomFrames) {
        const nk = s.file + '#' + fi;
        if (merged.merge.idxRemap && merged.merge.idxRemap.has(nk)) merged.roomFrames.set(merged.merge.idxRemap.get(nk), list);
      }
    }
  }
  if (merged.roomFrames.size) deriveRoomEvents(merged);

  // ── 元信息：是否新格式 / 头部 / absTick 偏移（用最后一张为准）──
  merged.fileSize = states.reduce((a, s) => a + s.fileSize, 0);
  merged.blankLines = states.reduce((a, s) => a + s.blankLines, 0);
  merged.badLines = states.reduce((a, s) => a + s.badLines, 0);
  for (const s of states) {
    for (const b of s.badLineSamples) merged.badLineSamples.push({ file: path.basename(s.file), ...b });
    merged.warningsTotal += s.warningsTotal;
    merged.hasNewFormat = merged.hasNewFormat || s.hasNewFormat;
    merged.newFormatRows += s.newFormatRows;
    for (const o of s.absTickOffsets) merged.absTickOffsets.add(o);
    for (const d of s.truncatedDefs) merged.truncatedDefs.add(d);
    for (const d of s.thingDefsSeen) merged.thingDefsSeen.add(d);
    for (const d of s.materialsDefsSeen) merged.materialsDefsSeen.add(d);
    for (const v of s.seqValues) merged.seqValues.add(v);
    if (s.seqAlwaysZero === false) merged.seqAlwaysZero = false;
  }
  const lastState = states[states.length - 1];
  merged.header = lastState.header || null;
  const firstNew = states.find((s) => s.hasNewFormat);
  merged.headerFirst = firstNew ? (firstNew.headerFirst || firstNew.header) : null;
  merged.headerFirstDate = merged.headerFirst ? dateTextOf(merged.headerFirst) : '';
  merged.absTickAlwaysMatchesTick = merged.absTickOffsets.size <= 1 && ![...merged.absTickOffsets].some((o) => o !== 0);
  const g = states.reduce((a, s) => ({
    minX: Math.min(a.minX, s.geo.minX), maxX: Math.max(a.maxX, s.geo.maxX),
    minZ: Math.min(a.minZ, s.geo.minZ), maxZ: Math.max(a.maxZ, s.geo.maxZ),
  }), { minX: 1e9, maxX: -1e9, minZ: 1e9, maxZ: -1e9 });
  merged.geo = g;
  merged.roomsSeen = Math.max(...states.map((s) => s.roomsSeen), 0);

  // ── 4 文件边界 / 重叠 / 缺口（报告要讲清楚的部分）──
  const F = merged.frames;
  const nominal = (() => {
    for (const f of F) if (f.sampling && f.sampling.intervalTicks > 0) return f.sampling.intervalTicks;
    return 6000;
  })();
  merged.merge.intervalNominal = nominal;
  for (let i = 1; i < kept.length; i++) {
    const a = kept[i - 1], b = kept[i];
    const fa = states[a.ordinal].frames[a.index], fb = states[b.ordinal].frames[b.index];
    const fileA = states[a.ordinal].file, fileB = states[b.ordinal].file;
    const gap = b.tick - a.tick;
    if (fileA !== fileB) {
      merged.merge.boundaries.push({
        atIndex: i, fromFile: path.basename(fileA), toFile: path.basename(fileB),
        prevTick: a.tick, nextTick: b.tick, gapTicks: gap,
        prevDate: fa.dateText || '', nextDate: fb.dateText || '',
      });
      if (gap > nominal * 1.5) {
        merged.merge.gaps.push({
          atIndex: i, kind: '跨文件', fromFile: path.basename(fileA), toFile: path.basename(fileB),
          prevTick: a.tick, nextTick: b.tick, gapTicks: gap, threshold: Math.round(nominal * 1.5),
        });
      }
    }
  }
  if (kept.length && merged.merge.boundaries.length === 0 && states.length > 1) {
    merged.merge.orderNote = '拼接后的序列里**没有**出现"文件 A → 文件 B"的相邻边界：说明其中一个文件的快照整体被去重叠掉或被包含在另一个里。';
  }

  merged.hypotheses = deriveHypotheses(merged);
  return merged;
}

/**
 * 处理一张快照：健康/基本面 + 对象时序 diff（不保留大数组）
 */
function processSnapshot(st, snap, ctx, prev, opt = {}) {
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
  // ── ★ 追加面（老文件里全为 false / 空数组，下面所有分支都不进）──
  const isNew = detectNewFormat(snap);
  const pawnDetails = Array.isArray(snap.pawnDetails) ? snap.pawnDetails : [];
  const rooms = Array.isArray(snap.rooms) ? snap.rooms : [];
  const materials = snap.materials && typeof snap.materials === 'object' && !Array.isArray(snap.materials) ? snap.materials : {};
  const hdr = isNew ? readNewHeader(snap) : null;
  if (isNew) {
    st.hasNewFormat = true;
    st.newFormatRows++;
    if (!st.header) {
      st.header = {
        ...hdr,
        firstIndex: index, firstTick: ctx.tick, firstDay: ctx.day, firstWallClock: ctx.wallClock,
        lastIndex: index, lastTick: ctx.tick, lastDay: ctx.day, lastWallClock: ctx.wallClock,
      };
      st.headerFirstDate = dateTextOf(hdr);
      // 首张的日期字段单独留一份（`st.header` 的日期字段会被后面的张覆盖成"末张"）
      st.headerFirst = { ...hdr };
    } else {
      st.header.lastIndex = index; st.header.lastTick = ctx.tick; st.header.lastDay = ctx.day; st.header.lastWallClock = ctx.wallClock;
      // 日期字段以**最后一张**为准（跨会话时最关键的是"数据到哪儿了"）
      st.header.absTick = hdr.absTick; st.header.year = hdr.year; st.header.quadrum = hdr.quadrum;
      st.header.dayOfQuadrum = hdr.dayOfQuadrum; st.header.dayOfYear = hdr.dayOfYear;
      st.header.dateText = hdr.dateText; st.header.colonyName = hdr.colonyName || st.header.colonyName;
      st.header.mapName = hdr.mapName || st.header.mapName;
    }
    if (hdr.absTick !== null) {
      const off = hdr.absTick - ctx.tick;
      st.absTickOffsets.add(off);
      if (off !== 0) st.absTickAlwaysMatchesTick = false;
    }
  }

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
    if (opt.collectMergedReaders) bumpMergedReader(st, id, h, index);
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
    // ★ 下面这些只在**新格式**才写上（老文件里字段**不存在**，
    //   渲染端一律用 `frame.rooms === undefined` 判断"没有这个面"）
    ...(isNew ? {
      absTick: hdr.absTick, year: hdr.year, quadrum: hdr.quadrum,
      dayOfQuadrum: hdr.dayOfQuadrum, dayOfYear: hdr.dayOfYear,
      dateText: hdr.dateText, colonyName: hdr.colonyName, mapName: hdr.mapName,
      countsNew: { pawnDetails: pawnDetails.length, rooms: rooms.length, materialDefs: Object.keys(materials).length },
      roomRoles: summarizeRoomRoles(rooms),
      materialByMat: null,   // 下面填（这一张的材质聚合）
    } : {}),
  };
  if (isNew) {
    frame.counts = { ...frame.counts, pawnDetails: pawnDetails.length, rooms: rooms.length, materials: Object.keys(materials).length };
    const mt = computeMaterialTotals(materials);
    frame.materialByMat = Object.fromEntries(mt.byMat);
    frame.materialTotal = mt.total;
    frame.materialDefs = mt.defs;
    st.materialSeries.push({
      index, tick: ctx.tick, day: ctx.day, absTick: hdr.absTick, dateText: hdr.dateText,
      total: mt.total, defs: mt.defs, byMat: Object.fromEntries(mt.byMat),
      shares: Object.fromEntries(mt.shares),
    });
  }

  // ── ③ 对象时序（面向对象：同一对象跨快照比较）──
  const cur = buildObjectState(st, { snap, index, zones, sgs, things, pawns, bills, research, designations, rooms, materials, pawnDetails, isNew });
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
    diffObjectState(st, prev, cur, { index, tick: ctx.tick, day: ctx.day, absTick: hdr ? hdr.absTick : null, dateText: hdr ? hdr.dateText : '' });
  } else {
    // 基线（第 1 张）只登记"首见"，**不产生变更点**：
    // 否则第 2 张会把"一直都在"的对象误报成"新增"（实测踩到过：3 个小人被报成"加入"）
    seedBaseline(st, cur, { index, tick: ctx.tick, day: ctx.day });
  }
  st.frames.push(frame);
  frame.changes = (st.changesByIndex.get(index) || []).length;
  // 房间的紧凑记录留在内存里，收尾时统一判定"隔开 / 合并"（见 deriveRoomEvents）
  if (isNew) st.roomFrames.set(index, readRoomFrame(rooms));
  // ★ 多文件拼接：把"能重建 diff 输入"的最小状态留下（收尾后按 tick 重排再 diff 一遍）
  if (ctx.keepPreviews) {
    const p = previewOf(st, cur, frame, index, ctx.tick);
    p.ordinal = st.ordinal;      // 输入顺序（排序的稳定 tie-break）
    st.previews.push(p);
  }
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
  // ★ 新格式追加面：基线只登记"首见"，不产生变更点（与上面同一口径）
  for (const [key, r] of cur.rooms) {
    st.roomMeta.set(key, {
      id: r.id, key, role: r.role, roleLabel: r.roleLabel, label: r.label,
      firstIndex: at.index, firstTick: at.tick, firstDay: at.day,
      lastIndex: at.index, lastTick: at.tick, present: 1,
      roleHistory: [], cellCount: r.cellCount, rectKey: r.rectKey,
      wallKey: r.wallKey, floorKey: r.floorKey, buildKey: r.buildKey,
      observedCells: [{ index: at.index, tick: at.tick, cellCount: r.cellCount }],
      wallHistory: [],
    });
  }
  for (const [name, q] of cur.pawnDetails) {
    const meta = {
      name, kind: q.kind, kindHistory: [], gender: q.gender, age: q.age,
      firstIndex: at.index, firstTick: at.tick, lastIndex: at.index, lastTick: at.tick, present: 1,
      weapon: q.weapon, apparelKey: q.apparelKey, apparel: q.apparel,
      traits: q.traits, skills: new Map(), skillGains: [],
      mood: { min: q.mood, max: q.mood }, food: { min: q.food, max: q.food },
      expectation: q.expectation, healthSummary: q.healthSummary, downed: q.downed,
      downedHistory: [], wpKey: q.wpKey, wpChanges: [],
    };
    for (const [def, s] of q.skills) meta.skills.set(def, { first: s.level, last: s.level, passion: s.passion });
    st.pawnDetailMeta.set(name, meta);
  }
}

/** 从一张快照抽出"对象状态"（大数组只留计数 / 哈希 / ≤200 的小集合） */
function buildObjectState(st, { index, zones, sgs, things, pawns, bills, research, designations, rooms, materials, pawnDetails, isNew }) {
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

  // ── ★ rooms（按 `Room.ID`；只留判定"职能/边界/材质"变化的量）──
  const roomMap = new Map();
  if (isNew) {
    let n = 0;
    for (const r of rooms) {
      if (!r || typeof r !== 'object') continue;
      if (n++ >= MAX_ROOMS) break;
      const rect = Array.isArray(r.rect) ? r.rect.map(n0) : [0, 0, 0, 0];
      roomMap.set(String(n0(r.id)), {
        id: n0(r.id), role: String(r.role || ''), roleLabel: String(r.roleLabel || ''),
        label: String(r.label || ''), cellCount: n0(r.cellCount),
        rect, rectKey: rect.slice(0, 4).join(','),
        roofed: r.roofed === true, openRoofCount: n0(r.openRoofCount),
        isPrisonCell: r.isPrisonCell === true,
        temperature: n1(r.temperature), beauty: n1(r.beauty),
        wallKey: countMapKey(r.wallMaterials), wallMaterials: r.wallMaterials || {},
        floorKey: countMapKey(r.floorMaterials), floorMaterials: r.floorMaterials || {},
        buildKey: countMapKey(r.buildings),
        wallTotal: Object.values(r.wallMaterials || {}).reduce((a, b) => a + n0(b), 0),
        floorTotal: Object.values(r.floorMaterials || {}).reduce((a, b) => a + n0(b), 0),
      });
    }
    st.roomsSeen = Math.max(st.roomsSeen, roomMap.size);
  }

  // ── ★ materials（顶层的 `{建筑def:{材质def:个数}}`）──
  const matByDef = new Map();
  if (isNew) {
    let n = 0;
    for (const [def, inner] of Object.entries(materials)) {
      if (n++ >= MAX_MATERIAL_DEFS) break;
      if (!inner || typeof inner !== 'object') continue;
      const m = new Map();
      for (const [mat, v] of Object.entries(inner)) m.set(String(mat), n0(v));
      matByDef.set(def, { def, byMat: m, key: countMapKey(inner), total: [...m.values()].reduce((a, b) => a + b, 0) });
    }
    for (const def of matByDef.keys()) st.materialsDefsSeen.add(def);
  }

  // ── ★ pawnDetails（按 name；`pawns[]` 的追加面，交叉核对用）──
  const pawnDetailMap = new Map();
  let detailTrimmed = 0;
  if (isNew) {
    const capped = { hit: false };
    const built = buildPawnDetailMap(pawnDetails, capped);
    if (capped.hit) detailTrimmed = Math.max(0, pawnDetails.length - MAX_PAWN_DETAILS);
    for (const [k, v] of built) pawnDetailMap.set(k, v);
  }

  return {
    index, zones: zoneMap, storageGroups: sgMap, things: thingMap, bills: billMap,
    pawns: pawnMap, researchFinished, designations: desigMap,
    rooms: roomMap, materials: matByDef, pawnDetails: pawnDetailMap, detailTrimmed,
  };
}

/** 材质 map → `{材质:个数}` 的稳定键 */

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

  // ════════════════════════════════════════════════════════════════
  // ★ 以下只处理**新格式**的追加面。`prev`/`cur` 里这些 map 全程为空 →
  //   循环一次都不进，老文件的 output 逐字节不受影响。
  // ════════════════════════════════════════════════════════════════

  // ── ★ rooms（按 `Room.ID`：职能 / 边界 / 墙 / 地板）──
  for (const [key, r] of cur.rooms) {
    let meta = st.roomMeta.get(key);
    if (!meta) {
      meta = {
        id: r.id, key, role: r.role, roleLabel: r.roleLabel, label: r.label,
        firstIndex: index, firstTick: tick, firstDay: day,
        lastIndex: index, lastTick: tick, present: 0,
        roleHistory: [], cellCount: r.cellCount, rectKey: r.rectKey,
        wallKey: r.wallKey, floorKey: r.floorKey, buildKey: r.buildKey,
        observedCells: [], wallHistory: [],
      };
      st.roomMeta.set(key, meta);
    }
    meta.present++;
    meta.lastIndex = index; meta.lastTick = tick; meta.label = r.label;
    meta.cellCount = r.cellCount; meta.rectKey = r.rectKey; meta.wallKey = r.wallKey;
    meta.floorKey = r.floorKey; meta.buildKey = r.buildKey;
    const obs = meta.observedCells;
    if (!obs.length || obs[obs.length - 1].cellCount !== r.cellCount) obs.push({ index, tick, cellCount: r.cellCount });
    if (obs.length > 40) obs.splice(0, obs.length - 40);

    const p = prev.rooms.get(key);
    if (!p) continue;   // 新增房间的"出现"由 deriveRoomEvents 统一处理（要跟消失的配对）

    // ★ 职能变化（用户最在意："一个房间在不同时间段承担多种职责"）
    if (p.role !== r.role) {
      meta.roleHistory.push({ index, tick, day, from: p.role, to: r.role });
      st.roomRoleChanges.push({
        index, tick, day, absTick: at.absTick ?? null, id: r.id,
        from: p.role, to: r.role, toLabel: r.roleLabel, cellCount: r.cellCount,
      });
      pushChange(st, index, {
        ...at, category: 'rooms', subject: `房间#${r.id}`, field: 'role',
        from: p.role || '(空)', to: r.role || '(空)',
        note: `★ 职能变化：${p.role || '(空)'} → ${r.role || '(空)'}（${r.roleLabel || '无标签'}，${r.cellCount} 格）`,
        focus: 'rooms',
      });
    }
    // 墙材质（段数构成变了 → 点名"从 X 换成 Y"）
    if (p.wallKey !== r.wallKey) {
      meta.wallHistory.push({ index, tick, from: p.wallMaterials, to: r.wallMaterials });
      pushChange(st, index, {
        ...at, category: 'rooms', subject: `房间#${r.id}`, field: 'wallMaterials',
        from: shortCountMap(p.wallMaterials), to: shortCountMap(r.wallMaterials),
        note: '墙/门材质构成变化' + wallSwapNote(p.wallMaterials, r.wallMaterials),
        focus: 'rooms',
      });
    }
    // 地板：从"无"到"有"
    const pf = p.floorTotal, cf = r.floorTotal;
    if (pf === 0 && cf > 0) {
      pushChange(st, index, {
        ...at, category: 'rooms', subject: `房间#${r.id}`, field: 'floorNew',
        from: '无地板（0 格）', to: shortCountMap(r.floorMaterials),
        note: `★ 地板从无到有（${cf} 格）`, focus: 'rooms',
      });
    } else if (p.floorKey !== r.floorKey) {
      pushChange(st, index, {
        ...at, category: 'rooms', subject: `房间#${r.id}`, field: 'floorMaterials',
        from: shortCountMap(p.floorMaterials), to: shortCountMap(r.floorMaterials),
        note: '地板材质变化', focus: 'rooms',
      });
    }
    // 其它属性（只记变化，不展开）
    if (p.roofed !== r.roofed || p.openRoofCount !== r.openRoofCount) {
      pushChange(st, index, {
        ...at, category: 'rooms', subject: `房间#${r.id}`, field: 'roof', from: `roofed=${p.roofed} open=${p.openRoofCount}`, to: `roofed=${r.roofed} open=${r.openRoofCount}`, focus: 'rooms',
      });
    }
    if (p.isPrisonCell !== r.isPrisonCell) {
      pushChange(st, index, { ...at, category: 'rooms', subject: `房间#${r.id}`, field: 'isPrisonCell', from: p.isPrisonCell, to: r.isPrisonCell, focus: 'rooms' });
    }
    for (const [f, label] of [['temperature', '温度'], ['beauty', '美观']]) {
      const a = p[f], b = r[f];
      if (a === null || b === null) continue;
      if (Math.abs(b - a) >= (f === 'temperature' ? 5 : 5)) {
        pushChange(st, index, { ...at, category: 'rooms', subject: `房间#${r.id}`, field: f, from: fmt(a, 1), to: fmt(b, 1), note: `${label}变化 ≥ 阈值`, focus: 'rooms' });
      }
    }
  }
  for (const [key, p] of prev.rooms) {
    if (!cur.rooms.has(key)) {
      const meta = st.roomMeta.get(key);
      if (meta) meta.lastIndex = index - 1;
      // ⚠ "消失"这个变更点**不在这里记**：它必须和"本张哪些 id 新增"放在一起
      //   才能判定"被隔开 / 被合并"，所以统一由 `deriveRoomEvents()` 收尾时产生。
    }
  }

  // ── ★ materials（顶层 `{建筑def:{材质def:个数}}`：逐 def 的材质构成 + 全局占比迁移）──
  {
    const defs = new Set([...cur.materials.keys(), ...prev.materials.keys()]);
    for (const def of defs) {
      const c = cur.materials.get(def), p = prev.materials.get(def);
      const cKey = c ? c.key : '', pKey = p ? p.key : '';
      if (cKey === pKey) continue;
      const from = p ? shortCountMap(Object.fromEntries(p.byMat)) : '(无)';
      const to = c ? shortCountMap(Object.fromEntries(c.byMat)) : '(无)';
      pushChange(st, index, {
        ...at, category: 'materials', subject: def, field: 'materials',
        from, to,
        note: `构成变化（总数 ${p ? p.total : 0} → ${c ? c.total : 0}）；材质迁移看 4.10 的时间线`,
        focus: 'materials',
      });
    }
    // 全局占比的关键切换点（"木头占比逐张下降、石砖上升"就落在这里）
    const ms = st.materialSeries;
    if (ms.length >= 2) {
      const prevRow = ms[ms.length - 2], curRow = ms[ms.length - 1];
      const mats = new Set([...Object.keys(prevRow.shares), ...Object.keys(curRow.shares)]);
      for (const mat of mats) {
        const a = prevRow.shares[mat] || 0, b = curRow.shares[mat] || 0;
        const d = b - a;
        const crossed = (a < MATERIAL_SHARE_BIG && b >= MATERIAL_SHARE_BIG) || (a >= MATERIAL_SHARE_BIG && b < MATERIAL_SHARE_BIG);
        if (Math.abs(d) >= 0.15 || crossed) {
          const rec = {
            index, tick, day, absTick: at.absTick ?? null, mat,
            fromShare: a, toShare: b, fromCount: (prevRow.byMat[mat] || 0), toCount: (curRow.byMat[mat] || 0),
            total: curRow.total, crossed,
          };
          st.materialSwitches.push(rec);
          pushChange(st, index, {
            ...at, category: 'materials', subject: `占比:${mat}`, field: 'share',
            from: pct(a, 1) + `（${rec.fromCount} 个）`, to: pct(b, 1) + `（${rec.toCount} 个）`,
            note: `★ ${mat} 全图占比 ${d > 0 ? '+' : ''}${(d * 100).toFixed(1)} 个百分点${crossed ? '（跨过 10%）' : ''}；分母 ${curRow.total} 个建筑`,
            focus: 'materials',
          });
        }
      }
    }
  }

  // ── ★ pawnDetails（身份 / 装备 / 技能 / 期望；并与 `pawns[]` 交叉核对）──
  for (const [name, q] of cur.pawnDetails) {
    let meta = st.pawnDetailMeta.get(name);
    if (!meta) {
      meta = {
        name, kind: q.kind, kindHistory: [], gender: q.gender, age: q.age,
        firstIndex: index, firstTick: tick, lastIndex: index, lastTick: tick, present: 0,
        weapon: q.weapon, apparelKey: q.apparelKey, apparel: q.apparel,
        traits: q.traits, skills: new Map(), skillGains: [],
        mood: { min: q.mood, max: q.mood }, food: { min: q.food, max: q.food },
        expectation: q.expectation, healthSummary: q.healthSummary, downed: q.downed,
        downedHistory: [], wpKey: q.wpKey, wpChanges: [],
      };
      for (const [def, s] of q.skills) meta.skills.set(def, { first: s.level, last: s.level, passion: s.passion });
      st.pawnDetailMeta.set(name, meta);
    }
    meta.present++;
    meta.lastIndex = index; meta.lastTick = tick;
    meta.mood.min = meta.mood.min === null ? q.mood : Math.min(meta.mood.min, q.mood ?? meta.mood.min);
    meta.mood.max = meta.mood.max === null ? q.mood : Math.max(meta.mood.max, q.mood ?? meta.mood.max);
    meta.food.min = meta.food.min === null ? q.food : Math.min(meta.food.min, q.food ?? meta.food.min);
    meta.food.max = meta.food.max === null ? q.food : Math.max(meta.food.max, q.food ?? meta.food.max);

    const p = prev.pawnDetails.get(name);
    // ★ 交叉核对：必须跟**同一张**的 `pawns[]` 比（两份是同一刻的两条读法）。
    //   ⚠ 曾错拿 `prev.pawns`（上一张）来比 —— 那会把"心情本来就变了"全部误报成"读法不一致"。
    const sameSnap = cur.pawns.get(name);
    if (sameSnap) {
      if (q.expectation !== sameSnap.expectation) {
        st.pawnDetailIssues.push({ kind: 'expectation 不一致', name, index, tick, pawnDetail: q.expectation, pawns: sameSnap.expectation });
      }
      if (q.mood !== null && sameSnap.mood !== null && Math.abs(q.mood - sameSnap.mood) > 0.005) {
        st.pawnDetailIssues.push({ kind: 'mood 不一致', name, index, tick, pawnDetail: q.mood, pawns: sameSnap.mood });
      }
      if (q.food !== null && sameSnap.food !== null && Math.abs(q.food - sameSnap.food) > 0.005) {
        st.pawnDetailIssues.push({ kind: 'food 不一致', name, index, tick, pawnDetail: q.food, pawns: sameSnap.food });
      }
    }
    if (!p) continue;

    // ★ 身份变化（殖民者 ↔ 奴隶），以及 kind 首次出现
    if (p.kind !== q.kind) {
      const notable = /Colonist|Slave|Prisoner|Guest/.test(p.kind) || /Colonist|Slave|Prisoner|Guest/.test(q.kind);
      meta.kindHistory.push({ index, tick, day, from: p.kind, to: q.kind });
      pushChange(st, index, {
        ...at, category: 'pawnDetails', subject: name, field: 'kind',
        from: `${kindLabel(p.kind)}（${p.kind || '(空)'}）`, to: `${kindLabel(q.kind)}（${q.kind || '(空)'}）`,
        note: notable ? `★ 身份变化：${kindLabel(p.kind)} → ${kindLabel(q.kind)}` : '身份变化',
        focus: 'pawns',
      });
      meta.kind = q.kind;
    }
    // 装备 / 服装
    if (p.weapon !== q.weapon) {
      pushChange(st, index, {
        ...at, category: 'pawnDetails', subject: name, field: 'weapon',
        from: p.weapon || '(徒手)', to: q.weapon || '(徒手)',
        note: '★ 武器变化', focus: 'pawns',
      });
      meta.weapon = q.weapon;
    }
    if (p.apparelKey !== q.apparelKey) {
      const added = q.apparel.filter((x) => !p.apparel.includes(x));
      const removed = p.apparel.filter((x) => !q.apparel.includes(x));
      pushChange(st, index, {
        ...at, category: 'pawnDetails', subject: name, field: 'apparel',
        from: `+${removed.length}/-${added.length}`,
        to: `+${added.length}/-${removed.length}`,
        note: `★ 服装变化：新增 ${added.map((x) => '`' + x + '`').join('、') || '无'}；脱下 ${removed.map((x) => '`' + x + '`').join('、') || '无'}`,
        focus: 'pawns',
      });
      meta.apparelKey = q.apparelKey; meta.apparel = q.apparel;
    }
    // 技能提升（只记上升，下降单独标）
    for (const [def, s] of q.skills) {
      const m = meta.skills.get(def);
      if (!m) { meta.skills.set(def, { first: s.level, last: s.level, passion: s.passion }); continue; }
      if (s.level > m.last) {
        meta.skillGains.push({ index, tick, def, from: m.last, to: s.level });
        pushChange(st, index, {
          ...at, category: 'pawnDetails', subject: name, field: 'skill+' + def,
          from: String(m.last), to: String(s.level),
          note: `★ 技能提升 ${m.last} → ${s.level}${s.passion ? '（' + s.passion + '）' : ''}`, focus: 'pawns',
        });
        m.last = s.level;
      } else if (s.level < m.last) {
        pushChange(st, index, {
          ...at, category: 'pawnDetails', subject: name, field: 'skill-' + def,
          from: String(m.last), to: String(s.level), note: '技能下降（伤势/年龄？需其它数据佐证）', focus: 'pawns',
        });
        m.last = s.level;
      }
      if (s.passion && s.passion !== m.passion) {
        pushChange(st, index, { ...at, category: 'pawnDetails', subject: name, field: 'passion:' + def, from: m.passion || '(空)', to: s.passion, focus: 'pawns' });
        m.passion = s.passion;
      }
    }
    // 健康
    if (p.healthSummary !== q.healthSummary) {
      pushChange(st, index, { ...at, category: 'pawnDetails', subject: name, field: 'health', from: p.healthSummary || '(空)', to: q.healthSummary || '(空)', focus: 'pawns' });
      meta.healthSummary = q.healthSummary;
    }
    if (p.downed !== q.downed) {
      meta.downedHistory.push({ index, tick, downed: q.downed });
      pushChange(st, index, { ...at, category: 'pawnDetails', subject: name, field: 'downed', from: p.downed ? '倒地' : '正常', to: q.downed ? '倒地' : '正常', focus: 'pawns' });
      meta.downed = q.downed;
    }
    // 期望（`pawns[]` 里也有，这里做交叉核对；不同步就点名）
    if (p.expectation !== q.expectation) {
      const rk = (e) => EXPECTATION_RANK.indexOf(e);
      const dir = rk(q.expectation) > rk(p.expectation) ? '↑' : (rk(q.expectation) < rk(p.expectation) ? '↓' : '?');
      pushChange(st, index, { ...at, category: 'pawnDetails', subject: name, field: 'expectation', from: p.expectation || '(空)', to: q.expectation || '(空)', note: '期望' + dir, focus: 'pawns' });
    }
    // 工作优先级（整体构成变了 → 列出变了哪几条）
    if (p.wpKey !== q.wpKey) {
      const changed = [];
      for (const [k, v] of q.wp) {
        const old = p.wp.get(k);
        if (old === undefined) changed.push(`${k}=${v}(新)`);
        else if (old !== v) changed.push(`${k}:${old}→${v}`);
      }
      for (const [k, v] of p.wp) if (!q.wp.has(k)) changed.push(`${k}=${v}(移除)`);
      meta.wpKey = q.wpKey;
      meta.wpChanges.push({ index, tick, changed });
      pushChange(st, index, {
        ...at, category: 'pawnDetails', subject: name, field: 'workPriorities',
        from: `共 ${p.wp.size} 条`, to: `共 ${q.wp.size} 条`,
        note: '工作优先级变化：' + (changed.slice(0, 8).join('、') || '（构成键变化但逐项一致）'), focus: 'pawns',
      });
    }
  }
  for (const [name, p] of prev.pawnDetails) {
    if (!cur.pawnDetails.has(name)) {
      const meta = st.pawnDetailMeta.get(name);
      if (meta) meta.lastIndex = index - 1;
      pushChange(st, index, {
        ...at, category: 'pawnDetails', subject: name, field: '离开明细',
        from: `${kindLabel(p.kind)}（${p.kind || '(空)'}）`, to: '-',
        note: '该小人本张不再出现在 pawnDetails（与 pawns 的"离开"交叉看 4.11）', focus: 'pawns',
      });
    }
  }

  return;
}

/** `{材质:个数}` → 一行短文本（报告里不刷屏） */
function shortCountMap(m) {
  if (!m || typeof m !== 'object') return '(空)';
  const es = Object.entries(m).filter(([, v]) => n0(v) > 0).sort((a, b) => n0(b[1]) - n0(a[1]));
  if (!es.length) return '(空)';
  return es.slice(0, 6).map(([k, v]) => `${k}×${n0(v)}`).join(' ') + (es.length > 6 ? ` …共${es.length}种` : '');
}

/** 墙材质的"换材"判定：旧的主导材质 → 新的主导材质（用户："木头 → 石砖"） */
function wallSwapNote(prevM, curM) {
  const dom = (m) => {
    let best = null, bv = -1;
    for (const [k, v] of Object.entries(m || {})) if (n0(v) > bv) { bv = n0(v); best = k; }
    return best;
  };
  const a = dom(prevM), b = dom(curM);
  if (!a || !b || a === b) return '';
  return `｜★ 主导墙材质 ${a} → ${b}`;
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
    // ★ `items` 可能是纯数字（单文件）或 `{i,v}`（多文件拼接：带"合并序号"）——
    //   这里统一取值，拼接模式下顺带按序号排好，`itemsSeries` 才是按时间顺序的
    const rawItems = r.items;
    const items = rawItems.length && typeof rawItems[0] === 'object'
      ? [...rawItems].sort((a, b) => a.i - b.i).map((x) => x.v)
      : rawItems;
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

// ──────────────────────────────────────────────────────────── 多文件拼接的报告节

/**
 * ★ **1.1 拼接清单** —— 用户看多文件时第一个要问的就是
 * "你拼了哪几个文件、哪里接得上、有没有重叠/缺口"。
 *
 * 去重叠口径：同一 `tick` 出现两次时**保留后一次**（后一次是更新的一份）。
 */
function renderMergeSection(w, st) {
  const M = st.merge;
  w(`### 1.1 ★ 多文件拼接（同一个殖民地跨会话被分成多个文件）`);
  w();
  w(`读档会**新开一个快照文件**，所以同一个殖民地会跨若干文件。本报告把它们拼成**一条时间线**后再做对象时序比较。`);
  w();
  w(`**拼了哪几个文件**（按命令行顺序 = 会话先后）：`);
  w();
  w(`| # | 文件 | 快照数 | tick 范围 | day 范围 | absTick 范围 | 游戏内日期（首→末） | 新格式 |`);
  w(`| --- | --- | --- | --- | --- | --- | --- | --- |`);
  for (const f of M.files) {
    w(`| ${f.ordinal + 1} | \`${esc(f.relative)}\`（${(f.fileSizeBytes / 1048576).toFixed(2)} MB） | ${f.snapshots} | ${f.tickFrom} → ${f.tickTo} | ${f.dayFrom} → ${f.dayTo} | ${f.absTickFrom ?? '-'} → ${f.absTickTo ?? '-'} | ${esc(f.dateFrom || '-')} → ${esc(f.dateTo || '-')} | ${f.hasNewFormat ? '是' : '否'} |`);
  }
  w();
  w(`**拼接结果**：输入 ${M.inputSnapshots} 张 → 排序 + 去重叠后 **${M.mergedSnapshots}** 张`);
  w(`（去重叠丢掉 **${M.overlapsDropped}** 张；口径：同一 \`tick\` 出现两次**保留后一次**）。`);
  w();
  if (M.orderNote) {
    w(`> ⚠ ${M.orderNote}`);
    w();
  }
  if (M.overlapsDropped) {
    w(`**被去重叠掉的（前 20 条）**：`);
    w();
    w(`| tick | 丢弃的那一份来自 |`);
    w(`| --- | --- |`);
    for (const o of M.overlaps.slice(0, 20)) w(`| ${o.tick} | \`${esc(o.droppedFrom)}\` |`);
    if (M.overlaps.length > 20) w(`| … | 共 ${M.overlapsDropped} 张 |`);
    w();
  } else {
    w(`**没有重叠**：所有输入文件的 \`tick\` 互不相交（说明这些文件是同一条线上的**连续分段**）。`);
    w();
  }
  w(`**文件边界（时间线上"哪几段是不同文件"）**：`);
  w();
  if (!M.boundaries.length) {
    w(`无（只拼出一个文件段）。`);
    w();
  } else {
    w(`| 时间线第几张 | 从 | 到 | tick | 游戏内日期 | 相邻 tick 差 |`);
    w(`| --- | --- | --- | --- | --- | --- |`);
    for (const b of M.boundaries) {
      w(`| ${b.atIndex + 1} | \`${esc(b.fromFile)}\` | \`${esc(b.toFile)}\` | ${b.prevTick} → ${b.nextTick} | ${esc(b.prevDate || '-')} → ${esc(b.nextDate || '-')} | ${b.gapTicks} |`);
    }
    w();
    w(`> 上面的 tick 差就是**跨文件的接缝**。它 ≈ 采样间隔说明两个会话接得很好；`);
    w(`> 明显大于 1.5×间隔说明中间那段**没有快照**（游戏没开 / 录制器没起）。`);
    w();
  }
  w(`**缺口（相邻两张的 tick 差 > 1.5×${M.intervalNominal}）**：`);
  w();
  if (!M.gaps.length) {
    w(`无。拼接后的时间线是连续的（每个相邻对的 tick 差都在阈值内）。`);
  } else {
    w(`| 时间线第几张 | 类型 | 从 | 到 | tick | 差 |`);
    w(`| --- | --- | --- | --- | --- | --- |`);
    for (const g of M.gaps.slice(0, 30)) w(`| ${g.atIndex + 1} | ${g.kind} | \`${esc(g.fromFile)}\` | \`${esc(g.toFile)}\` | ${g.prevTick} → ${g.nextTick} | ${g.gapTicks} |`);
    w();
    w(`> ⚠ 缺口期间发生了什么**永久丢失**（快照法的固有代价）。`);
  }
  w();
  if (M.mergedSnapshots !== M.inputSnapshots) {
    w(`> 注意：**去重叠后**的密度/覆盖度数字与"逐文件分别看"不同 —— 本报告的 2/3 节都是按拼接后的序列算的。`);
    w();
  }
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
  // ★ 只在"新格式"或"显式用了 --focus"（或拼接）时印这一行 —— 保证老样本 + 默认参数下
  //   报告与旧版**逐字节一致**（回归测试就拿这个当判据）
  if (st.hasNewFormat || st.focus !== 'all' || st.merge) {
    w(`- **关注点**：\`--focus ${st.focus}\`（${FOCUS_LABEL[st.focus] || st.focus}）`);
  }
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
  if (st.hasNewFormat) {
    const h = st.header || {};
    const h0 = st.headerFirst || h;
    w(`| 殖民地 / 地图名 | ${esc(h.colonyName || '(空)')} / ${esc(h.mapName || '(空)')} |`);
    w(`| **游戏内日期**（首张） | ${esc(dateTextOf(h0) || '-')}（year ${h0.year ?? '-'}｜${esc(h0.quadrum || '-')}｜年内第 ${h0.dayOfYear ?? '-'} 天｜象限内第 ${h0.dayOfQuadrum ?? '-'} 天） |`);
    w(`| **游戏内日期**（末张） | ${esc(dateTextOf(h) || '-')}（year ${h.year ?? '-'}｜${esc(h.quadrum || '-')}｜年内第 ${h.dayOfYear ?? '-'} 天｜象限内第 ${h.dayOfQuadrum ?? '-'} 天） |`);
    w(`| 绝对 tick（absTick）首/末 | ${h0.absTick ?? '-'} → ${h.absTick ?? '-'} |`);
  }
  if (st.badLines) {
    w();
    w(`**坏行样本**（最多 5 条）：`);
    for (const b of st.badLineSamples) w(`- 第 ${b.line} 行（${b.bytes} 字节）：${esc(b.error)}`);
  }
  w();
  // ★ 老格式 / 新格式：把"day 是什么"讲清楚（用户被这个绕过）
  //   ⚠ 老格式 + 默认 --focus + 单文件时**一个字都不印**（与旧版逐字节一致）
  if (st.hasNewFormat || st.focus !== 'all' || st.merge) {
    if (!st.hasNewFormat) {
      w(`> ⚠ **本文件没有新字段**（\`absTick\` / \`dateText\` / \`rooms\` / \`materials\` / \`pawnDetails\` 都不存在）`);
      w(`> —— 这份数据来自**旧构建的录制器**。因此这里**只能给出 \`day\`，给不出游戏内日期**。`);
      w(`> \`day = tick/60000\` 是「**殖民地建立以来**的天数」，**不是游戏内日期**（不是 year/quadrum）。`);
      w(`> 要游戏内日期，必须重开游戏让新录制器录一份（见 docs/快照读取-可拓展与可度量.md）。`);
    } else {
      w(`> ★ **\`day\` 与游戏内日期是两回事**（本报告严格分开）：`);
      w(`> - \`day = tick/60000\` = 「**殖民地建立以来**的天数」→ 就是上面的 \`day 范围\`；`);
      w(`> - 「游戏内日期」= \`dateText\` / \`year\` / \`quadrum\` / \`dayOfYear\`（由 \`absTick\` 推得）→ 上面的"游戏内日期"两行。`);
      w(`> 两者之差 = 游戏**开局时的绝对 tick**（本文件：${[...st.absTickOffsets].join(' / ') || '-'}）。`);
      if (st.absTickOffsets.size > 1) {
        w(`> ⚠ 出现了 **${st.absTickOffsets.size} 个不同的偏移** → 这些张来自**不同的存档会话**（读档会重置 \`tick\`）。`);
        w(`> 跨会话拼接时以 \`absTick\` 为准更可靠。`);
      }
      if (st.newFormatRows < F.length) {
        w(`> ⚠ 只有 **${st.newFormatRows}/${F.length}** 张带新字段 —— **新老格式混在同一个文件里**（录制器升级后重启游戏会出现）。`);
        w(`> 缺新字段的张一律按老路径处理，不会报错；但"房间/材质/明细"的时间线在那几张是空的。`);
      }
    }
    w();
  }

  // ── 1.1 ★ 多文件拼接（只在多文件模式下出现）──
  if (st.merge) renderMergeSection(w, st);

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
  let rowCount34 = 0;
  if (sgZero) {
    const v = cov.verdicts.find((x) => x.reader === 'storageGroups');
    w(`| storageGroups 全程 0 | ${esc(v ? v.level : '可疑')} | ${esc(v && v.evidence ? v.evidence : '源码在 `StorageGroupsForReading == null` 时静默 return（无警告）')} |`);
    rowCount34++;
  }
  if (billZero) {
    const v = cov.verdicts.find((x) => x.reader === 'bills');
    w(`| bills 全程 0 | ${esc(v ? v.level : '可疑')} | 地图上没有常见工作台 def，倾向真没有；但不是自证 |`);
    rowCount34++;
  }
  if (desZero) {
    w(`| designations 全程 0 | 可疑（倾向"真没有"） | 无任何内部反证 |`);
    rowCount34++;
  }
  // ⚠ 这一行必须**看数据**再决定印不印 —— 曾经无条件印，结果在"读得好好的"文件里
  //   也照样写"research.current 全程空 → 读法失效"，直接把报告变成假话。
  const researchAlwaysEmpty = F.every((f) => !f.researchCurrent);
  if (researchAlwaysEmpty) {
    const v = cov.verdicts.find((x) => x.reader === 'research');
    const brokenResearch = v && /失效/.test(String(v.level));
    w(`| research.current 全程空 | ${brokenResearch ? '**读法失效**' : '可疑（倾向"真没有"）'} | ${brokenResearch ? '每张都有 \`currentProj 读不到\` 警告' : '无警告：读法自证可用，只是确实没有正在研究的项目'} |`);
    rowCount34++;
  }
  if (!rowCount34) w(`| — | — | 没有任何"全程为 0 / 读法失效"的面：${cov.readers.length} 个读取块都有数据且在变化 |`);
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
  /**
   * ★ **关注点过滤器**：`--focus rooms` 时只把 `focus === rooms` 的变更点铺成表，
   * 其余的只报个数（**不丢数据** —— summary.json 里一条没少）。
   * `--focus all` 时**完全等于旧行为**（一张表都不少）。
   */
  const focusSection = (title, cat, extra, focusKey) => {
    const all = byCat(cat);
    if (st.focus !== 'all' && st.focus !== focusKey) {
      if (!all.length) return;
      w(`### 4.${extra} ${title}`);
      w();
      w(`> 本关注点（\`--focus ${st.focus}\`）不展开这一节。该面在 ${F.length} 张快照里有 **${all.length}** 个变更点，`);
      w(`> 要看它请改用 \`--focus ${focusKey}\`（或 \`--focus all\`）；数据在 \`summary.json\` 里一条没少。`);
      w();
      return;
    }
    section(title, cat, extra);
  };

  focusSection('zones（按 id+type）', 'zones', 1, 'rooms');
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
  focusSection('storageGroups（按 loadId）', 'storageGroups', 2, 'buildings');
  if (!byCat('storageGroups').length) {
    w(`> storageGroups 全程 ${F.every((f) => f.counts.storageGroups === 0) ? '0 个' : '存在但无字段变化'}。见 3.1 / 3.4 的判定：这是**数据显示的 0**，`);
    w(`> 不是"分析端没看"。源码里 \`StorageGroupsForReading == null\` 会**静默返回**，所以 0 与读法失效无法在本数据内区分。`);
    w();
  }
  focusSection('things（按 def；注意 BP: 蓝图 / FR: 框架前缀）', 'things', 3, 'buildings');
  // ★ 4.3.1 只在**新格式**或**显式 --focus buildings** 时出现
  if (st.hasNewFormat || st.focus === 'buildings') renderBuildingsFocus(w, st);
  focusSection('bills（键 = bench@x,z + recipe）', 'bills', 4, 'buildings');
  if (!byCat('bills').length) {
    w(`> bills 全程为空（0 条），所以**最有信息量的变更点这一层在本文件里拿不到** —— 这是数据缺口，不是分析端的结论。`);
    w();
  }
  focusSection('research（current 变化 / finished 新增）', 'research', 5, 'buildings');
  if (!byCat('research').length) {
    w(`> \`current\` 全程为空（且每张都有 \`currentProj 读不到\` 警告 → 见 3.2 判定为**读法失效**）；`);
    w(`> \`finishedCount\` 全程 ${F[0] ? F[0].researchFinishedCount : '-'}，本会话没有新完成的研究。`);
    w();
  }
  focusSection('wealth（序列 + 显著变化点）', 'wealth', 6, 'buildings');
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
  focusSection('pawns（按 name：加入/离开、期望、心情、饱食、负面想法首次出现）', 'pawns', 7, 'pawns');
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
  focusSection('designations（按 def）', 'designations', 8, 'buildings');

  // ════════════════════════════════════════════════════════════════
  // ★ 以下 4.9 / 4.10 / 4.11 是**新格式追加面**的聚焦视图。
  //   ⚠ 老格式（没有 rooms / materials / pawnDetails）时**一行都不印** ——
  //     这是"老文件输出不变"的保证。
  // ════════════════════════════════════════════════════════════════
  if (st.hasNewFormat && (st.focus === 'all' || st.focus === 'rooms')) renderRoomsSection(w, st);
  if (st.hasNewFormat && (st.focus === 'all' || st.focus === 'materials')) renderMaterialsSection(w, st);
  if (st.hasNewFormat && (st.focus === 'all' || st.focus === 'pawns')) renderPawnDetailsSection(w, st);

  // ── 5 基本面时序表（`--focus basics` 跳过：那一节是"每张一行"的长表，属于"慢"的部分）──
  if (st.focus === 'basics') {
    w(`## 5. 基本面时序表 —— \`--focus basics\` 已跳过`);
    w();
    w(`> 该节是"每张快照一行"的长表（${F.length} 行）。要看它请用 \`--focus all\`（或 \`buildings\` / \`pawns\`）。`);
    w(`> 同样的数字在 \`summary.json\` 的 \`fundamentals\` 里**一条没少**。`);
    w();
  } else {
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
  }

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

  renderMarkdownAppendix(w, st, F);

  return L.join('\n') + '\n';
}

function renderMarkdownAppendix(w, st, F) {
  // ── 附录：方法 ──
  w(`## 附录 A. 方法与内存口径`);
  w();
  w(`- **流式**：\`readline\` 逐行读，一行 \`JSON.parse\` 一次，解析完立刻降维，**不保留任何一行原始 JSON**。`);
  w(`- **有界**：\`things\` / \`designations\` 只留 \`{count, hash}\`；只有数量 ≤ ${MAX_CELL_SET} 的 def 才materialize 格子集合，`);
  w(`  且只保留"上一张 / 这一张"两份；最多 ${MAX_TRACKED_SETS} 个 def 进入该通道。逐张内存占用与文件总大小**无关**。`);
  w(`- **变更点**：上限 ${MAX_CHANGE_EVENTS} 条，超出只计数。`);
  if (st.hasNewFormat || st.focus !== 'all' || st.merge) {
    w(`- **新格式追加面**：\`rooms\` 单张最多留 ${MAX_ROOMS} 间、\`pawnDetails\` 最多 ${MAX_PAWN_DETAILS} 人、\`materials\` 最多 ${MAX_MATERIAL_DEFS} 个 def（防御性上限）；`);
    w(`  房间的"隔开/合并"需要**上下两张的房间列表**才能配对，所以房间列表在内存里留到文件读完（每张只留 id/role/格数/rect/材质键）。`);
    w(`- **老格式**：没有追加字段的快照走**完全相同的路径**，新节的渲染函数一次都不被调用。`);
  }
  w(`- **无自动化**：脚本不调用 domain / coordinator / 游戏桥接，不写任何快照数据。`);
  w();
}

// ──────────────────────────────────────────────── 新格式追加面的渲染

/** `第 N 张（tick X，day D）` + 有游戏内日期时补上（**明确标出这是日期，不是 day**） */
function whenOf(st, index, tick, day) {
  const f = st.frames[index];
  const d = f && f.dateText ? `，${f.dateText}` : '';
  return `第 ${index + 1} 张（tick ${tick}，day ${day}${d}）`;
}

/** `roleLabel` 常常就是 defName 的镜像；这里只在它非空时印，避免噪音 */
function kindRoleLabel(l) {
  return String(l || '') || '(无人话名)';
}

/**
 * ★ 4.9 房间（`rooms[]`）—— 职能变化 / 被隔开 / 被合并 / 换墙 / 地板从无到有。
 * 用户原话：「一个房间在不同时间段承担多种职责」「13×13 的大房间可能被一隔为二」。
 */
function renderRoomsSection(w, st) {
  const F = st.frames;
  w(`### 4.9 ★ rooms（房间：职能变化、被隔开/合并、墙与地板材质）`);
  w();
  w(`数据源：快照的 \`rooms[]\`（\`Room.ID\` / \`RoomRoleDef.defName\` / \`ExtentsClose\` / \`wallMaterials\` / \`floorMaterials\`）。`);
  w(`对象身份 = \`Room.ID\`；**"隔开"是跨对象关系**（一个 id 消失 + 若干个 id 出现），`);
  w(`所以判定统一放在 \`deriveRoomEvents()\` 里配对，而不是逐 id 看。`);
  w();
  if (!F.length) return;
  const roomCounts = F.map((f) => (f.counts && f.counts.rooms) || 0);
  w(`房间数：${Math.min(...roomCounts)}..${Math.max(...roomCounts)}（末张 ${roomCounts[roomCounts.length - 1]}）｜跟踪到 ${st.roomMeta.size} 个 id｜隔开/合并事件 ${st.roomSplits.length} 起`);
  w();

  // ── 职能变化（用户最在意）──
  w(`#### 4.9.1 ★ 房间职能变化（role 变了）`);
  w();
  if (!st.roomRoleChanges.length) {
    w(`**无职能变化。**${st.roomMeta.size ? `（${st.roomMeta.size} 个房间的 role 全程各自不变）` : '（本文件 rooms 全程为空）'}`);
    w();
  } else {
    w(`| 时刻 | 房间 | 从 | 到 | 人话 | 当时格数 |`);
    w(`| --- | --- | --- | --- | --- | --- |`);
    for (const r of st.roomRoleChanges) {
      w(`| ${whenOf(st, r.index, r.tick, r.day)} | #${r.id} | \`${esc(r.from || '(空)')}\` | \`${esc(r.to || '(空)')}\` | ${esc(kindRoleLabel(r.toLabel))} | ${r.cellCount} |`);
    }
    w();
    w(`> 这就是用户说的「**一个房间在不同时间段承担多种职责**」—— 同一个 \`Room.ID\`，\`role\` 变了。`);
    w();
  }

  // ── 隔开 / 合并 ──
  w(`#### 4.9.2 ★ 房间被隔开 / 被合并（一个 id 消失 + 若干 id 出现）`);
  w();
  if (!st.roomSplits.length) {
    w(`**无隔开/合并事件。**`);
    w();
  } else {
    w(`| 时刻 | 事件 | 原来的房间 | 现在的房间 | 格数是否吻合 | 置信 |`);
    w(`| --- | --- | --- | --- | --- | --- |`);
    for (const s of st.roomSplits) {
      const from = s.kind === '隔开'
        ? `#${s.goneId}（${esc(s.goneRole || '(空)')}，${s.goneCells} 格）`
        : `${s.goneId}（合计 ${s.goneCells} 格）`;
      const to = s.newRooms.map((r) => `#${r.id}（${esc(r.role || '(空)')}，${r.cellCount} 格，rect[${r.rect.join(',')}]）`).join('<br>');
      w(`| ${whenOf(st, s.index, s.tick, s.day)} | **${s.kind}** | ${from} | ${to} | ${s.cellsClose ? '是' : '否'} | ${s.confidence} |`);
    }
    w();
    w(`> 判定口径：**1 个消失 + ≥2 个新增**且"新房间格数之和 ≈ 原房间格数"（±35%）→ 隔开；反向 → 合并。`);
    w(`> 「格数是否吻合 = 否」的条目**不要当成结论**：可能是多次改动叠在两次采样之间，也可能是读档后 id 重排。`);
    w();
  }

  // ── 墙材质 ──
  w(`#### 4.9.3 墙材质变化（房间边界上的墙/门）`);
  w();
  const wallChgs = st.changes.filter((c) => c.category === 'rooms' && c.field === 'wallMaterials');
  if (!wallChgs.length) {
    w(`**无墙材质变化。**`);
    w();
  } else {
    w(`| 时刻 | 房间 | 从 | 到 | 备注 |`);
    w(`| --- | --- | --- | --- | --- |`);
    for (const c of wallChgs.slice(0, 80)) {
      w(`| ${whenOf(st, c.index, c.tick, c.day)} | \`${esc(c.subject)}\` | ${esc(String(c.from))} | ${esc(String(c.to))} | ${esc(c.note)} |`);
    }
    if (wallChgs.length > 80) w(`| … | | | | 共 ${wallChgs.length} 条 |`);
    w();
  }

  // ── 地板从无到有 ──
  w(`#### 4.9.4 地板"从无到有"（用户点名的关注点）`);
  w();
  const floorNew = st.changes.filter((c) => c.category === 'rooms' && (c.field === 'floorNew' || c.field === 'floorMaterials'));
  if (!floorNew.length) {
    w(`**无地板变化。**`);
    w();
  } else {
    w(`| 时刻 | 房间 | 类型 | 从 | 到 | 备注 |`);
    w(`| --- | --- | --- | --- | --- | --- |`);
    for (const c of floorNew.slice(0, 80)) {
      w(`| ${whenOf(st, c.index, c.tick, c.day)} | \`${esc(c.subject)}\` | ${c.field === 'floorNew' ? '**从无到有**' : '材质变化'} | ${esc(String(c.from))} | ${esc(String(c.to))} | ${esc(c.note)} |`);
    }
    if (floorNew.length > 80) w(`| … | | | | | 共 ${floorNew.length} 条 |`);
    w();
  }

  // ── 每个房间的对象时序 ──
  if (st.roomMeta.size) {
    w(`#### 4.9.5 每个房间的对象时序（首末出现 + 格数取值序列）`);
    w();
    w(`| 房间 | role（末） | 人话 | 首次出现 | 末次出现 | 出现张数 | 格数序列（变化点） | 职能历史 |`);
    w(`| --- | --- | --- | --- | --- | --- | --- | --- |`);
    let n = 0;
    for (const [, m] of st.roomMeta) {
      if (n++ >= 80) { w(`| … | | | | | | | 共 ${st.roomMeta.size} 个房间 |`); break; }
      const seq = m.observedCells.map((o) => `${o.cellCount}@${o.index + 1}`).join(' → ');
      const rh = m.roleHistory.map((r) => `${r.from}→${r.to}@${r.index + 1}`).join('，') || '-';
      w(`| #${m.id}${m.label ? '「' + esc(m.label) + '」' : ''} | \`${esc(m.role || '(空)')}\` | ${esc(kindRoleLabel(m.roleLabel))} | 第 ${m.firstIndex + 1} 张 | 第 ${m.lastIndex + 1} 张 | ${m.present} | ${esc(seq || '-')} | ${esc(rh)} |`);
    }
    w();
  }
}

/**
 * ★ 4.10 建筑材料迁移（`materials{建筑def:{材质def:个数}}`）。
 * 用户原话：「早期用木头，后来逐步换成石砖…钢铁有战略意义」→ 要的是**占比时间线 + 关键切换点**。
 */
function renderMaterialsSection(w, st) {
  const ms = st.materialSeries;
  w(`### 4.10 ★ materials（建筑材料迁移：木头 → 石砖 → 钢）`);
  w();
  w(`数据源：快照顶层的 \`materials{建筑defName:{材质defName:个数}}\`（无材质记作 \`(none)\`）。`);
  w(`两种粒度：**顶层总占比**（4.10.1 / 4.10.2）与**逐 def 的材质构成**（4.10.3）。`);
  w();
  if (!ms.length) {
    w(`**本文件没有 \`materials\` 数据**（该面缺席或全程为空 map）。`);
    w();
    return;
  }
  const maxBy = new Map();
  for (const r of ms) for (const [k, v] of Object.entries(r.byMat)) maxBy.set(k, Math.max(maxBy.get(k) || 0, v));
  const mats = [...maxBy.entries()].filter(([, v]) => v >= MATERIAL_MIN_BUILDINGS).sort((a, b) => b[1] - a[1]).map(([k]) => k);
  const tiny = [...maxBy.keys()].filter((k) => !mats.includes(k));
  w(`含材质的建筑总个数：${ms[0].total} → ${ms[ms.length - 1].total}（逐张 min ${Math.min(...ms.map((r) => r.total))} / max ${Math.max(...ms.map((r) => r.total))}）`);
  w();
  w(`#### 4.10.1 材质占比时间线（只列峰值 ≥ ${MATERIAL_MIN_BUILDINGS} 个的材质）`);
  w();
  if (!mats.length) {
    w(`**没有任何材质达到 ${MATERIAL_MIN_BUILDINGS} 个的样本量下限。**${tiny.length ? '样本太少的：' + tiny.map((t) => '`' + t + '`').join('、') : ''}`);
    w();
  } else {
    // ★ 抽样：均匀抽 + **必留最后 10 张**，否则"木头→石砖"的迁移会在后段被抽掉
    const stride = Math.max(1, Math.ceil(ms.length / 40));
    const tailN = Math.min(10, ms.length);
    const showIdx = new Set();
    for (let i = 0; i < ms.length; i += stride) showIdx.add(i);
    for (let i = Math.max(0, ms.length - tailN); i < ms.length; i++) showIdx.add(i);
    const rows = [...showIdx].sort((a, b) => a - b);
    w(`| # | tick | 游戏内日期 | 含材质建筑数 | ${mats.map((m) => '`' + m + '`').join(' | ')} |`);
    w(`| --- | --- | --- | --- | ${mats.map(() => '---').join(' | ')} |`);
    for (const i of rows) {
      const r = ms[i];
      w(`| ${r.index + 1} | ${r.tick} | ${esc(r.dateText || '-')} | ${r.total} | ${mats.map((m) => pct(r.shares[m] || 0, 1)).join(' | ')} |`);
    }
    w();
    if (stride > 1) w(`> 表按每 ${stride} 张抽一行、**并强制保留最后 ${tailN} 张**（首末行必在）；完整逐张数据见 \`summary.json\` 的 \`newFaces.materials.series\`。`);
    w();
    w(`首末占比对照：`);
    w();
    w(`| 材质 | 首张 | 末张 | 变化 |`);
    w(`| --- | --- | --- | --- |`);
    for (const m of mats) {
      const a = ms[0].shares[m] || 0, b = ms[ms.length - 1].shares[m] || 0;
      const d = b - a;
      w(`| \`${m}\` | ${pct(a, 1)}（${ms[0].byMat[m] || 0} 个） | ${pct(b, 1)}（${ms[ms.length - 1].byMat[m] || 0} 个） | ${d > 0 ? '↑' : d < 0 ? '↓' : '='} ${(d * 100).toFixed(1)} pp |`);
    }
    w();
  }
  w(`#### 4.10.2 ★ 关键切换点（占比单步 Δ ≥ 15pp，或跨过 10%）`);
  w();
  if (!st.materialSwitches.length) {
    w(`**没有关键切换点。**（所有材质占比都是逐张平滑变化，或全程没有变化）`);
    w();
  } else {
    w(`| 时刻 | 材质 | 从 | 到 | 单步 Δ | 分母（含材质建筑数） |`);
    w(`| --- | --- | --- | --- | --- | --- |`);
    for (const s of st.materialSwitches) {
      const d = s.toShare - s.fromShare;
      w(`| ${whenOf(st, s.index, s.tick, s.day)} | \`${s.mat}\` | ${pct(s.fromShare, 1)}（${s.fromCount}） | ${pct(s.toShare, 1)}（${s.toCount}） | ${d > 0 ? '+' : ''}${(d * 100).toFixed(1)} pp${s.crossed ? ' ★跨 10%' : ''} | ${s.total} |`);
    }
    w();
    const domSeq = ms.map((r) => {
      let best = null, bv = -1;
      for (const [k, v] of Object.entries(r.byMat)) if (v > bv) { bv = v; best = k; }
      return best;
    });
    const domChanges = [];
    for (let i = 1; i < domSeq.length; i++) if (domSeq[i] !== domSeq[i - 1]) domChanges.push({ index: i, from: domSeq[i - 1], to: domSeq[i] });
    if (domChanges.length) {
      w(`**主导材质切换（用户说的"木头 → 石砖 → 钢"就是这条线）**：`);
      w();
      for (const c of domChanges) {
        const f = ms[c.index];
        w(`- ${whenOf(st, c.index, f.tick, f.day)}：\`${c.from || '(无)'}\` → \`${c.to || '(无)'}\``);
      }
      w();
    }
  }
  const defChgs = st.changes.filter((c) => c.category === 'materials' && c.field === 'materials');
  w(`#### 4.10.3 逐 def 的材质构成变化（哪个建筑在换材）`);
  w();
  if (!defChgs.length) {
    w(`**无逐 def 的构成变化。**（跟踪到 ${st.materialsDefsSeen.size} 个建筑 def）`);
    w();
  } else {
    const byDef = new Map();
    for (const c of defChgs) {
      if (!byDef.has(c.subject)) byDef.set(c.subject, []);
      byDef.get(c.subject).push(c);
    }
    w(`共 ${st.materialsDefsSeen.size} 个 def 有材质数据，其中 **${byDef.size}** 个发生过构成变化：`);
    w();
    w(`| 建筑 def | 变化次数 | 首次 | 末次 | 首次构成 → 末次构成 |`);
    w(`| --- | --- | --- | --- | --- |`);
    for (const [def, list] of [...byDef.entries()].sort((a, b) => b[1].length - a[1].length).slice(0, 60)) {
      const a = list[0], b = list[list.length - 1];
      w(`| \`${esc(def)}\` | ${list.length} | ${whenOf(st, a.index, a.tick, a.day)} | ${whenOf(st, b.index, b.tick, b.day)} | ${esc(String(a.from).slice(0, 60))} → ${esc(String(b.to).slice(0, 60))} |`);
    }
    w();
  }
}

/**
 * ★ 4.11 殖民者明细（`pawnDetails[]`）—— 加入/离开、身份变化、装备、技能、期望与心情。
 * 与 `pawns[]` 的交叉核对也放在这里（两份同名数据对不上 = 有一边读法坏了）。
 */
function renderPawnDetailsSection(w, st) {
  w(`### 4.11 ★ pawnDetails（殖民者明细：身份 / 装备 / 技能 / 期望）`);
  w();
  w(`数据源：快照的 \`pawnDetails[]\`（一人一条；\`kind\` ∈ Colonist/Slave/Prisoner/Guest/ColonistMech/Subhuman/Animal/Other，**只记玩家殖民地的人**）。`);
  w(`它与 \`pawns[]\` 是**两份**：\`mood\`/\`food\`/\`expectation\` 同名重复，方便交叉核对。`);
  w();
  if (!st.pawnDetailMeta.size) {
    w(`**本文件没有 \`pawnDetails\` 数据**（该面缺席，或全程为空数组）。`);
    w();
    return;
  }
  const F = st.frames;
  w(`明细人数：${Math.min(...F.map((f) => (f.counts && f.counts.pawnDetails) || 0))}..${Math.max(...F.map((f) => (f.counts && f.counts.pawnDetails) || 0))}｜跟踪到 ${st.pawnDetailMeta.size} 人`);
  w();

  const joined = st.changes.filter((c) => c.category === 'pawns' && c.field === '加入');
  const left = st.changes.filter((c) => c.category === 'pawns' && c.field === '离开');
  const detailLeft = st.changes.filter((c) => c.category === 'pawnDetails' && c.field === '离开明细');
  w(`#### 4.11.1 加入 / 离开（\`pawnDetails\` 与 \`pawns\` 交叉）`);
  w();
  const fmtJoin = (list) => (list.length ? list.map((c) => `${esc(c.subject)}（${whenOf(st, c.index, c.tick, c.day)}）`).join('、').slice(0, 600) : '无');
  w(`| 事件 | 人数 | 明细 |`);
  w(`| --- | --- | --- |`);
  w(`| \`pawns\` 记录"加入" | ${joined.length} | ${fmtJoin(joined)} |`);
  w(`| \`pawns\` 记录"离开" | ${left.length} | ${fmtJoin(left)} |`);
  w(`| \`pawnDetails\` 记录"离开明细" | ${detailLeft.length} | ${fmtJoin(detailLeft)} |`);
  w();
  if (joined.length !== detailLeft.length && detailLeft.length) {
    w(`> ⚠ 两个面的"加入/离开"条数不同（\`pawns\` ${joined.length} / \`pawnDetails\` ${detailLeft.length}）——`);
    w(`> 两者**各自的基线张不同**，也可能某个小人在其中一份里读不到（例如没有 \`Pawn_PlayerCategory\`）。`);
    w(`> 这里**只报数，不下结论**；要对上就逐人看 4.11.5 的"在场张数"与 4.7 的"出现张数"。`);
    w();
  }
  if (st.pawnDetailIssues.length) {
    w(`**★ 交叉核对不一致（同名 \`mood\`/\`food\`/\`expectation\` 对不上）**：共 ${st.pawnDetailIssues.length} 处，前 15 条：`);
    w();
    w(`| 时刻 | 小人 | 字段 | pawnDetails | pawns |`);
    w(`| --- | --- | --- | --- | --- |`);
    for (const it of st.pawnDetailIssues.slice(0, 15)) w(`| 第 ${it.index + 1} 张（tick ${it.tick}） | ${esc(it.name)} | ${esc(it.kind)} | ${esc(String(it.pawnDetail))} | ${esc(String(it.pawns))} |`);
    w();
    w(`> 两份都是独立读取，对不上就是**有一边读法坏了**（不是"数据没有"）。`);
    w();
  }

  w(`#### 4.11.2 ★ 身份变化（殖民者 ↔ 奴隶 ↔ 囚犯 ↔ 访客）`);
  w();
  const kindChgs = st.changes.filter((c) => c.category === 'pawnDetails' && c.field === 'kind');
  if (!kindChgs.length) {
    w(`**无身份变化。**（${st.pawnDetailMeta.size} 人的 \`kind\` 全程各自不变）`);
    w();
  } else {
    w(`| 时刻 | 小人 | 从 | 到 |`);
    w(`| --- | --- | --- | --- |`);
    for (const c of kindChgs) w(`| ${whenOf(st, c.index, c.tick, c.day)} | ${esc(c.subject)} | ${esc(String(c.from))} | ${esc(String(c.to))} |`);
    w();
  }

  w(`#### 4.11.3 装备与服装变化`);
  w();
  const gearChgs = st.changes.filter((c) => c.category === 'pawnDetails' && (c.field === 'weapon' || c.field === 'apparel'));
  if (!gearChgs.length) {
    w(`**无装备/服装变化。**`);
    w();
  } else {
    w(`| 时刻 | 小人 | 类型 | 从 | 到 | 备注 |`);
    w(`| --- | --- | --- | --- | --- | --- |`);
    for (const c of gearChgs.slice(0, 100)) {
      w(`| ${whenOf(st, c.index, c.tick, c.day)} | ${esc(c.subject)} | ${c.field === 'weapon' ? '武器' : '服装'} | ${esc(String(c.from))} | ${esc(String(c.to))} | ${esc(c.note)} |`);
    }
    if (gearChgs.length > 100) w(`| … | | | | | 共 ${gearChgs.length} 条 |`);
    w();
  }

  w(`#### 4.11.4 技能提升（只记上升；下降会单独标）`);
  w();
  const skillChgs = st.changes.filter((c) => c.category === 'pawnDetails' && /^skill[+-]/.test(c.field));
  if (!skillChgs.length) {
    w(`**无技能变化。**`);
    w();
  } else {
    w(`| 时刻 | 小人 | 技能 | 从 | 到 |`);
    w(`| --- | --- | --- | --- | --- |`);
    for (const c of skillChgs.slice(0, 120)) {
      w(`| ${whenOf(st, c.index, c.tick, c.day)} | ${esc(c.subject)} | \`${esc(c.field.replace(/^skill[+-]/, ''))}\`${c.field.startsWith('skill-') ? ' ⚠下降' : ''} | ${esc(String(c.from))} | ${esc(String(c.to))} |`);
    }
    if (skillChgs.length > 120) w(`| … | | | | 共 ${skillChgs.length} 条 |`);
    w();
  }
  const gains = [...st.pawnDetailMeta.values()].flatMap((m) => m.skillGains.map((g) => ({ ...g, name: m.name })));
  if (gains.length) {
    const top = [...gains].sort((a, b) => (b.to - b.from) - (a.to - a.from)).slice(0, 5);
    w(`> 单次提升最大的 5 条：${top.map((g) => `${g.name} ${g.def} ${g.from}→${g.to}（${whenOf(st, g.index, g.tick, F[g.index].day)}）`).join('；')}`);
    w();
  }

  w(`#### 4.11.5 每人聚合（身份 / 期望 / 心情 / 健康 / 工作优先级变化次数）`);
  w();
  w(`| 小人 | 身份（末） | 性别 | 年龄 | 在场张数 | 期望（末） | mood min/max | food min/max | 武器（末） | 技能数 | 技能提升次数 | 健康（末） | 倒地次数 | 优先级变化 | 特质 |`);
  w(`| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |`);
  for (const [, m] of st.pawnDetailMeta) {
    w(`| ${esc(m.name)} | ${esc(kindLabel(m.kind))} | ${esc(m.gender || '-')} | ${fmt(m.age, 0)} | ${m.present} | ${esc(m.expectation || '(空)')} | ${fmt(m.mood.min, 2)}/${fmt(m.mood.max, 2)} | ${fmt(m.food.min, 2)}/${fmt(m.food.max, 2)} | ${esc(m.weapon || '(徒手)')} | ${m.skills.size} | ${m.skillGains.length} | ${esc(m.healthSummary || '-')} | ${m.downedHistory.filter((d) => d.downed).length} | ${m.wpChanges.length} | ${esc((m.traits || []).join('、') || '-')} |`);
  }
  w();
  if (st.pawnDetailCapped) w(`> ⚠ 有 ${st.pawnDetailCapped} 条明细超出单张 ${MAX_PAWN_DETAILS} 条的上限被丢弃（防御性上限；正常殖民地不会碰到）。`);
  w();
}

/**
 * ★ `buildings` 关注点的聚焦视图：蓝图 / 框架的增减（4.3 层的窄视图）。
 * 用户要的是"`things`/蓝图/框架的增减"，所以这里只挑 `BP:` / `FR:` 与"建筑类 def"。
 */
function renderBuildingsFocus(w, st) {
  const chgs = st.changes.filter((c) => c.category === 'things' && typeof c.to === 'number' && typeof c.from === 'number');
  const bp = chgs.filter((c) => c.subject.startsWith('BP:'));
  const fr = chgs.filter((c) => c.subject.startsWith('FR:'));
  const done = chgs.filter((c) => !c.subject.startsWith('BP:') && !c.subject.startsWith('FR:'));
  w(`#### 4.3.1 ★ buildings 聚焦：蓝图 / 框架 / 成品的增减`);
  w();
  w(`| 类型 | 变化点数 | 净增合计 | 例 |`);
  w(`| --- | --- | --- | --- |`);
  const net = (l) => l.reduce((a, c) => a + (c.to - c.from), 0);
  const ex = (l) => l.slice(0, 3).map((c) => `${esc(c.subject)} ${c.from}→${c.to}@${c.index + 1}`).join('、') || '-';
  w(`| 蓝图 \`BP:\` | ${bp.length} | ${net(bp) >= 0 ? '+' : ''}${net(bp)} | ${ex(bp)} |`);
  w(`| 框架 \`FR:\` | ${fr.length} | ${net(fr) >= 0 ? '+' : ''}${net(fr)} | ${ex(fr)} |`);
  w(`| 其它（成品/物品/矿脉…） | ${done.length} | ${net(done) >= 0 ? '+' : ''}${net(done)} | ${ex(done)} |`);
  w();
  const inProgress = bp.filter((c) => c.to > c.from).length;
  const toProduct = fr.filter((c) => c.to < c.from).length;
  w(`> **施工中**（蓝图增加）${inProgress} 次；**框架减少**（转成品）${toProduct} 次。`);
  w(`> ⚠ 蓝图/框架转成成品会在**同一张或相邻张**里表现为"BP↓ + 成品↑"，采样间隔是 ${st.density ? st.density.intervalNominal : '-'} tick；`);
  w(`>   两次采样之间的完工过程**不可见**，只能看到前后计数差。`);
  if (st.truncatedDefs.size) w(`> ⚠ 已触 1500 截断上限的 def：${[...st.truncatedDefs].map((d) => '`' + d + '`').join('、')}（计数是下界，Delta 不可信）`);
  w();
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
    focus: st.focus || 'all',
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
      // ★ 新格式头部（老文件里 hasNewFormat=false，这些字段全是 null / 空串）
      hasNewFormat: !!st.hasNewFormat,
      newFormatRows: st.newFormatRows,
      absTickOffsets: [...st.absTickOffsets],
      colonyName: st.header ? st.header.colonyName : '',
      mapName: st.header ? st.header.mapName : '',
      inGameDateFirst: st.headerFirst ? { ...st.headerFirst, dateText: dateTextOf(st.headerFirst) } : null,
      inGameDateLast: st.header ? { ...st.header, dateText: dateTextOf(st.header) } : null,
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
      // ★ 新格式追加面的对象时序（老文件里全为空，数组长度 0 —— 便于后续脚本判"这个面有没有"）
      rooms: [...st.roomMeta.values()],
      pawnDetails: [...st.pawnDetailMeta.entries()].map(([name, m]) => ({
        name, kind: m.kind, kindHistory: m.kindHistory, gender: m.gender, age: m.age,
        firstIndex: m.firstIndex, firstTick: m.firstTick, lastIndex: m.lastIndex, lastTick: m.lastTick,
        present: m.present, weapon: m.weapon, apparel: m.apparel, traits: m.traits,
        expectation: m.expectation || '', healthSummary: m.healthSummary, downed: m.downed,
        downedEvents: m.downedHistory.filter((d) => d.downed).length,
        // Map 不能直接 JSON 化 → 转成普通对象
        skills: Object.fromEntries([...m.skills.entries()].map(([k, v]) => [k, v])),
        skillGains: m.skillGains, wpChanges: m.wpChanges,
        mood: m.mood, food: m.food,
      })),
    },
    /** ★ 新格式追加面的汇总（老文件里 hasNewFormat=false 且各数组为空） */
    newFaces: {
      hasNewFormat: !!st.hasNewFormat,
      roomRoleChanges: st.roomRoleChanges,
      roomSplits: st.roomSplits,
      materials: {
        defsTracked: st.materialsDefsSeen.size,
        switches: st.materialSwitches,
        series: st.materialSeries.map((r) => ({ index: r.index, tick: r.tick, day: r.day, absTick: r.absTick, dateText: r.dateText, total: r.total, defs: r.defs, byMat: r.byMat, shares: r.shares })),
      },
      pawnDetailIssues: st.pawnDetailIssues,
      pawnDetailCapped: st.pawnDetailCapped,
      roomsSeen: st.roomsSeen,
    },
    /** ★ 多文件拼接的账（单文件时为 null） */
    merge: st.merge
      ? {
        files: st.merge.files,
        inputSnapshots: st.merge.inputSnapshots,
        mergedSnapshots: st.merge.mergedSnapshots,
        overlapsDropped: st.merge.overlapsDropped,
        overlaps: st.merge.overlaps,
        boundaries: st.merge.boundaries,
        gaps: st.merge.gaps,
        intervalNominal: st.merge.intervalNominal,
        orderNote: st.merge.orderNote,
      }
      : null,
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
    // seq：连续时只印区间，别把 294 个号全贴出来（曾经贴满一整屏）
    let seqText;
    if (st.seqAlwaysZero) seqText = '恒 0（旧构建）';
    else {
      const seqs = [...st.seqValues].map(Number);
      const contiguous = seqs.length > 1 && seqs.every((v, i) => i === 0 || v === seqs[i - 1] + 1);
      seqText = seqs.length <= 6 ? seqs.join(',')
        : contiguous ? `${seqs[0]},${seqs[0] + 1},…,${seqs[seqs.length - 1]}（连续 ${seqs.length} 个）`
          : `${seqs.slice(0, 4).join(',')}…${seqs[seqs.length - 1]}（共 ${seqs.length} 个）`;
    }
    add(`  wallClock ${F.length ? F[0].wallClock : '-'}→${F.length ? F[F.length - 1].wallClock : '-'}（${d.wallSpanSec ?? '-'} 秒）| seq ${seqText}`, 1);
    // ★ 游戏内日期（只在新格式印）—— 与 `day` 严格分开，别让用户再被绕晕
    if (st.hasNewFormat) {
      const h = st.header || {}, h0 = st.headerFirst || h;
      add(`  ★ 游戏内日期（≠ 上面的 day）：${dateTextOf(h0) || '-'} → ${dateTextOf(h) || '-'}｜absTick ${h0.absTick ?? '-'}→${h.absTick ?? '-'}｜${h.colonyName || '(无名)'} / ${h.mapName || '(无名)'}`, 1);
    }
    // ★ 多文件拼接的账
    if (st.merge) {
      const M = st.merge;
      add(`  ★ 拼接 ${M.files.length} 个文件：输入 ${M.inputSnapshots} 张 → 去重叠后 ${M.mergedSnapshots} 张（丢 ${M.overlapsDropped}）｜文件边界 ${M.boundaries.length} 处｜缺口 ${M.gaps.length} 处`, 1);
      for (const f of M.files) add(`      - ${path.basename(f.file)}：${f.snapshots} 张，tick ${f.tickFrom}→${f.tickTo}，${f.dateFrom || '-'}→${f.dateTo || '-'}`, 2);
    }

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
    // ★ 新格式追加面的变化点 + 关注点归属
    if (st.hasNewFormat) {
      const newCats = ['rooms', 'materials', 'pawnDetails'];
      const ncnt = Object.fromEntries(newCats.map((c) => [c, st.changes.filter((x) => x.category === c).length]));
      add(`  ★ 追加面变化点：` + newCats.map((c) => `${c} ${ncnt[c]}`).join(' | ') +
        `｜房间 id ${st.roomMeta.size}｜职能变化 ${st.roomRoleChanges.length}｜隔开/合并 ${st.roomSplits.length}｜材质切换 ${st.materialSwitches.length}｜明细 ${st.pawnDetailMeta.size} 人`, 1);
      const byFocus = {};
      for (const c of st.changes) byFocus[c.focus] = (byFocus[c.focus] || 0) + 1;
      add(`  关注点归属（--focus 切换的就是这个）：` + ['rooms', 'materials', 'buildings', 'pawns'].map((k) => `${k} ${byFocus[k] || 0}`).join(' | '), 2);
    }
    const notable = st.changes.filter((c) => c.category === 'things' || c.category === 'bills').slice(0, 6);
    for (const c of notable) add(`    · [${c.category}] ${c.subject} ${c.field} ${c.from}→${c.to}（tick ${c.tick}）${c.note ? '｜' + c.note : ''}`, 3);
    // ★ 关注点聚焦时，把该面的"重点事件"顶上来（≤5 行）
    if (st.focus !== 'all') {
      const pri = st.changes.filter((c) => c.focus === st.focus && /★/.test(String(c.note)));
      const pick = (pri.length ? pri : st.changes.filter((c) => c.focus === st.focus)).slice(0, 5);
      if (pick.length) {
        add(`  ★ ${st.focus} 面的重点（前 ${pick.length} 条）：`, 1);
        for (const c of pick) add(`    · [${c.category}] ${c.subject} ${c.field} ${c.from}→${c.to}（tick ${c.tick}，${c.dateText || 'day ' + c.day}）`, 1);
      }
    }
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
                                        [--focus ${FOCUS_LEVELS.join('|')}]

  不给文件 → 用 ${path.relative(ROOT, SNAPSHOT_DIR).replace(/\\/g, '/')} 里最新的一份 .jsonl
  多个文件 → **同一个殖民地跨会话被分成多个文件**时把它们拼起来：
             按 absTick / tick 排序、去重叠（同一 tick 保留后一次），
             并在报告里列出"拼了哪几个文件 / 重叠 / 缺口"。
  --out     单文件时=报告 .md 路径；多文件时=输出目录（默认 runs/snapshot-analysis/）
  --json    stdout 输出机器可读 summary（报告文件照写）
  --focus   切换关注点（默认 all）；每个变更点都带一个 focus 归属，切换=换过滤器：
             all        全部（老文件 + 默认值 → 输出与旧版逐字节一致）
             basics     只看概览/采样/覆盖度/health（快）
             rooms      房间：职能变化 / 被隔开 / 被合并 / 墙地材质
             materials  建筑材料迁移（木头→石砖→钢）+ 关键切换点
             buildings  things / 蓝图 / 框架的增减（聚焦视图）
             pawns      殖民者明细：加入离开 / 身份变化 / 装备 / 技能 / 期望

输出：
  runs/snapshot-analysis/<快照文件名>.report.md
  runs/snapshot-analysis/<快照文件名>.summary.json
  多文件时：<输出目录>/merged-<N>sessions.report.md / .summary.json`);
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
  let outArg = null, jsonOut = false, focus = 'all';
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--out') { outArg = argv[++i]; if (!outArg) { console.error('--out 需要一个路径'); process.exit(2); } }
    else if (a.startsWith('--out=')) outArg = a.slice(6);
    else if (a === '--json') jsonOut = true;
    else if (a === '--focus') {
      focus = String(argv[++i] || '');
      if (!FOCUS_LEVELS.includes(focus)) {
        console.error(`--focus 只能是：${FOCUS_LEVELS.join(' | ')}（收到 "${focus}"）`);
        process.exit(2);
      }
    }
    else if (a.startsWith('--focus=')) {
      focus = a.slice(8);
      if (!FOCUS_LEVELS.includes(focus)) {
        console.error(`--focus 只能是：${FOCUS_LEVELS.join(' | ')}（收到 "${focus}"）`);
        process.exit(2);
      }
    }
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
  for (let i = 0; i < inputs.length; i++) {
    const f = inputs[i];
    const st = await analyzeFile(f, { keepPreviews: multi, collectMergedReaders: true, multi, ordinal: i });
    st.focus = focus;
    if (!multi) {
      st.coverage = analyzeCoverage(st);
      st.density = computeDensity(st);
    }
    const { mdPath, jsonPath } = outPathsFor(f, outArg, multi);
    st.outMd = mdPath;
    st.outJson = jsonPath;
    results.push(st);
  }

  // ── ★ 多文件：拼成一个序列，只出一份报告（单文件路径不受影响）──
  let reportStates = results;
  if (multi) {
    const merged = mergeStates(results);
    merged.focus = focus;
    merged.file = `merged-${results.length}sessions`;
    merged.coverage = analyzeCoverage(merged);
    merged.density = computeDensity(merged);
    const base = merged.file + '.jsonl';
    const { mdPath, jsonPath } = outPathsFor(base, outArg, false);
    merged.outMd = mdPath;
    merged.outJson = jsonPath;
    reportStates = [merged];
    // 单文件报告不再单独出（多文件模式下 `--out` 指的是输出目录，不复用）
    results.length = 0;
    results.push(merged);
  }

  for (const st of reportStates) {
    const md = renderMarkdown(st);
    fs.writeFileSync(st.outMd, md, 'utf8');
    const summary = {
      generatedAt: new Date().toISOString(),
      script: 'scripts/analyze-snapshots.mjs',
      version: VERSION,
      workspaceRoot: ROOT,
      focus: st.focus,
      files: [summarize(st)],
    };
    fs.writeFileSync(st.outJson, JSON.stringify(summary, null, 2) + '\n', 'utf8');
    st.summary = summary;
  }

  if (jsonOut) {
    const mergedOut = {
      generatedAt: new Date().toISOString(),
      script: 'scripts/analyze-snapshots.mjs',
      version: VERSION,
      focus,
      inputs: inputs.map((f) => path.relative(ROOT, f).replace(/\\/g, '/')),
      files: results.flatMap((r) => r.summary.files),
    };
    process.stdout.write(JSON.stringify(mergedOut, null, 2) + '\n');
  } else {
    process.stdout.write(renderConsole(results, { multi, focus }).join('\n') + '\n');
  }
}

main().catch((e) => {
  console.error('[analyze-snapshots] 失败：' + (e && e.stack ? e.stack : e));
  process.exit(1);
});
