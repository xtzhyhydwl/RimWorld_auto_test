/**
 * 条件登记表：**数据**（`src/conditions.json`）+ 一小段校验/查表逻辑。
 *
 * 为什么要有它：条件 JSON 里的字段路径（`result.state.playable` 之类）以前只能靠现场实测反推，
 * 反推出来又只留在聊天和文档里 ⇒ 每写一步测试都要重猜一次。登记表把这些「场景 → 探针 + 参数 + 条件」
 * 变成机器能读的数据：**加一个场景 = 加一条数据**，不改代码（边界见设计稿 §4.3）。
 *
 * 边界（同 `src/condition.js` 的分工）：这里只做**数据校验与查表**，不连桥接、不求值条件。
 * 「这条条件成立吗」由 `condition.js` 回答；「去哪采样」由调用方（`cli.js`，将来 `wait-sources.js`）回答。
 */
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { editDistance } from './toolname.js';
import { SOURCE_SPECS } from './wait-sources.js';

const WS = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');

export const CONDITIONS_FILE = path.join(WS, 'src', 'conditions.json');

/** 四类采样来源：这份清单的唯一出处是 wait-sources.js 的 SOURCE_SPECS（抄两份必然漂移） */
export const SOURCES = Object.keys(SOURCE_SPECS);

/** 证据等级。`verified` 是派生布尔（只为 wait-verified），保留是为了兼容只读布尔的调用方。 */
export const VERIFIED_LEVELS = ['wait-verified', 'field-verified', 'doc-example', 'inferred'];

/** 占位符：`"$x"` 这种写法由命令行 `--arg x=139` 填 */
const PLACEHOLDER = /^\$([A-Za-z_][A-Za-z0-9_]*)$/;

export function loadRegistry({ file = CONDITIONS_FILE } = {}) {
  let raw;
  try {
    raw = JSON.parse(fs.readFileSync(file, 'utf8'));
  } catch (err) {
    return { ok: false, file, scenarios: {}, errors: [`读不到登记表 ${file}：${err.message}`] };
  }
  const errors = [];
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) {
    return { ok: false, file, scenarios: {}, errors: [`${file} 的顶层必须是对象`] };
  }
  if (raw.schema !== 1) errors.push(`schema 应为 1，收到 ${JSON.stringify(raw.schema)}`);
  const table = raw.conditions;
  if (!table || typeof table !== 'object' || Array.isArray(table)) {
    return { ok: false, file, scenarios: {}, errors: [...errors, '缺少 conditions 对象'] };
  }
  const scenarios = {};
  for (const [name, entry] of Object.entries(table)) {
    const checked = validateScenario(name, entry);
    if (checked.ok) scenarios[name] = checked.scenario;
    else errors.push(...checked.errors);
  }
  return { ok: errors.length === 0, file, note: raw.note ?? null, scenarios, errors };
}

/**
 * 校验一条场景。**只报错、不猜**：字段缺了就说缺哪个，来源与探针的矛盾也当场拒
 * （`source: 'event'` 却写了 `probe` 是典型的复制粘贴错误，静默容忍会让 `--list` 骗人）。
 */
export function validateScenario(name, entry) {
  const errors = [];
  const bad = (msg) => errors.push(`场景「${name}」：${msg}`);
  if (!entry || typeof entry !== 'object' || Array.isArray(entry)) return { ok: false, errors: [`场景「${name}」必须是对象`] };

  if (!SOURCES.includes(entry.source)) bad(`source 必须是 ${SOURCES.join('/')} 之一，收到 ${JSON.stringify(entry.source)}`);
  if (entry.source === 'bridge') {
    if (typeof entry.probe !== 'string' || !entry.probe) bad('source=bridge 时必须给 probe（探针工具名）');
  } else if (entry.probe !== undefined) {
    bad(`source=${entry.source} 时不能写 probe`);
  }
  if (!entry.condition || typeof entry.condition !== 'object' || Array.isArray(entry.condition)) {
    bad('condition 必须是对象（条件 JSON）');
  }
  for (const field of ['description', 'evidence']) {
    if (typeof entry[field] !== 'string' || !entry[field]) bad(`${field} 必填（${field === 'evidence' ? '出处，写成「文件:行号」开头' : '一句人话'}）`);
  }
  if (entry.verifiedLevel !== undefined && !VERIFIED_LEVELS.includes(entry.verifiedLevel)) {
    bad(`verifiedLevel 必须是 ${VERIFIED_LEVELS.join('/')} 之一`);
  }
  if (entry.verifiedLevel === undefined && typeof entry.verified !== 'boolean') {
    bad('verified 必填（布尔），或改填 verifiedLevel');
  }
  if (entry.args !== undefined && (typeof entry.args !== 'object' || Array.isArray(entry.args))) bad('args 必须是对象');
  if (entry.args && entry.source !== 'bridge') bad('args 只对 source=bridge 有意义');

  if (errors.length) return { ok: false, errors };
  const level = entry.verifiedLevel ?? (entry.verified ? 'wait-verified' : 'inferred');
  return {
    ok: true,
    scenario: {
      name,
      source: entry.source,
      probe: entry.probe ?? null,
      args: entry.args ?? {},
      condition: entry.condition,
      description: entry.description,
      evidence: entry.evidence,
      verified: level === 'wait-verified',
      verifiedLevel: level,
      profile: entry.profile ?? null,
      timeoutMs: entry.timeoutMs ?? null,
      fromNow: entry.fromNow ?? false,
      windowMs: entry.windowMs ?? null,
    },
  };
}

/** `--list` 用的行：按名字排序，未验证的明确标出来 */
export function listScenarios(registry) {
  return Object.values(registry.scenarios)
    .map((s) => ({
      name: s.name,
      source: s.source,
      probe: s.probe,
      verified: s.verified,
      verifiedLevel: s.verifiedLevel,
      description: s.description,
    }))
    .sort((a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0));
}

/** 查一个场景；名字写错时用编辑距离给近似名（与工具名报错的同一套手感） */
export function resolveScenario(registry, name) {
  const hit = registry.scenarios[name];
  if (hit) return { ok: true, scenario: hit };
  const names = Object.keys(registry.scenarios);
  const near = names
    .map((n) => ({ n, d: editDistance(String(name), n) }))
    .filter((x) => x.d <= Math.max(2, Math.ceil(String(name).length / 3)))
    .sort((a, b) => a.d - b.d)
    .slice(0, 3)
    .map((x) => x.n);
  return {
    ok: false,
    reason:
      `没有场景「${name}」。` +
      (near.length ? `\n  你是不是想要：${near.join(' / ')}` : `\n  现有场景共 ${names.length} 个，用 --list 看全部。`),
  };
}

/**
 * 把 `args` 里的 `$占位` 换成命令行给的值。
 *
 * 为什么需要：坐标、`pawnId`、`targetId`、`Letter_0`、存档名每次都不一样，而登记表是静态 JSON。
 * 没有这条，「等某个小人走到 (139,231)」这类验收永远登记不进来（候选数据 16 条里 7 条被它挡住）。
 * 缺值立刻报错并**列出缺哪几个**，不猜默认值。
 */
export function fillArgs(args, provided = {}) {
  const missing = [];
  const used = new Set();
  const walk = (value) => {
    if (typeof value === 'string') {
      const m = PLACEHOLDER.exec(value);
      if (!m) return value;
      const key = m[1];
      if (!Object.prototype.hasOwnProperty.call(provided, key)) {
        missing.push(key);
        return value;
      }
      used.add(key);
      const given = provided[key];
      // 纯数字的给值转成数字，别让 `"x": "139"` 这种字符串混进桥接参数
      return typeof given === 'string' && /^-?\d+(\.\d+)?$/.test(given) ? Number(given) : given;
    }
    if (Array.isArray(value)) return value.map(walk);
    if (value && typeof value === 'object') {
      return Object.fromEntries(Object.entries(value).map(([k, v]) => [k, walk(v)]));
    }
    return value;
  };
  const filled = walk(args ?? {});
  const unused = Object.keys(provided).filter((k) => !used.has(k));
  return { ok: missing.length === 0, args: filled, missing, unused };
}
