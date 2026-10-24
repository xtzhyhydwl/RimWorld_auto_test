/**
 * 条件求值器：**只有原子判定**，不认识任何数据来源。
 *
 * 为什么单独一个文件（2026-10-24）：等待要合并四类来源（桥接工具 / 事件流 / 进程 / 桥接就绪），
 * 采样层需要「逐行预判」同一个条件，而采样层又要被等待循环调用 —— 两者放进一个文件就是
 * 双向 import。把判定挪出来，依赖变成单向：`condition.js` ← `wait.js`、`wait-sources.js`。
 *
 * 边界（docs/cli-wait-P1-事件进程等待与条件登记表-设计-20261024.md §2.4）：
 *   算子只做「一个值 vs 一个期望值」；
 *   窗口 / 计数 / 派生量一律由采样层算好，变成这里能直接比的数据。
 * 所以算子位要**少而正交**：`all/any/not` + `allItems/anyItem` 已布尔完备。
 *
 * 本文件不碰 IO、不连桥接、不读日志：输入是一次采样载荷，输出是布尔（或「路径取不到」）。
 */
import { editDistance } from './toolname.js';

const ROOT = 'result';

/** 路径这一轮取不到（字段还没出现 / 名字写错）——只有 waitFor 认这个 code，见 tryMatch */
const MISSING_PATH_CODE = 'CONDITION_PATH_MISSING';

/** 组合子：节点里只有这一个键，值是子条件（数组或单个节点） */
const COMBINATORS = ['all', 'any', 'not'];
/** 叶子算子：与 `path` 并列，值是被比较的目标 */
const LEAF_OPERATORS = [
  'exists',
  'equals',
  'notEquals',
  'in',
  'notIn',
  'greaterThan',
  'greaterThanOrEqual',
  'lessThan',
  'lessThanOrEqual',
  'countEquals',
  'contains',
  'allItems',
  'anyItem',
];
const COMPARE = {
  greaterThan: (a, b) => a > b,
  greaterThanOrEqual: (a, b) => a >= b,
  lessThan: (a, b) => a < b,
  lessThanOrEqual: (a, b) => a <= b,
};

export function matchesCondition(condition, sample) {
  return tryMatch(condition, sample).matched;
}

/**
 * 求值一次采样。
 *
 * 为什么「路径取不到」不算错：字段不存在常常只是「时候还没到」——`rimworld/list_colonists`
 * 在没进图时载荷里根本没有 `colonists`。把它当异常会逼调用方自己再写一层重试循环，
 * 那正是「动作后睡固定时长」又回来了。写错的条件（算子、值类型、缺 `result.` 前缀）仍然
 * 当场抛，因为那些再等一万年也不会变。
 */
function tryMatch(condition, sample) {
  try {
    return { matched: evaluate(condition, { value: sample, root: true }), missing: null };
  } catch (err) {
    if (err?.code === MISSING_PATH_CODE) return { matched: false, missing: err.missingPath };
    throw err;
  }
}

// ---------------------------------------------------------------------------
// 条件求值
// ---------------------------------------------------------------------------

function evaluate(node, scope) {
  if (node === null || typeof node !== 'object' || Array.isArray(node)) {
    throw badCondition(`条件节点必须是对象，收到 ${preview(node)}`);
  }
  const keys = Object.keys(node);
  if (!keys.length) throw badCondition('条件节点是空对象');

  const combinator = keys.find((k) => COMBINATORS.includes(k));
  if (combinator) {
    if (keys.length > 1) {
      throw badCondition(`组合子「${combinator}」只能单独出现，这个节点还带着：${keys.filter((k) => k !== combinator).join(', ')}`);
    }
    const sub = node[combinator];
    if (combinator === 'not') return !evaluate(sub, scope);
    if (!Array.isArray(sub)) throw badCondition(`「${combinator}」的值必须是条件数组，收到 ${preview(sub)}`);
    // 空数组按集合论的恒等元处理：all=[] 恒真、any=[] 恒假
    return combinator === 'all'
      ? sub.every((child) => evaluate(child, scope))
      : sub.some((child) => evaluate(child, scope));
  }

  const opKeys = keys.filter((k) => k !== 'path');
  if (opKeys.length !== 1) {
    throw badCondition(
      `叶子条件必须恰好有一个算子，收到「${opKeys.join(', ') || '无'}」。` +
        `可用算子：${LEAF_OPERATORS.join(' ')}；组合子：${COMBINATORS.join(' ')}`,
    );
  }
  const op = opKeys[0];
  if (!LEAF_OPERATORS.includes(op)) {
    throw badCondition(
      `不认识的算子「${op}」。可用算子：${LEAF_OPERATORS.join(' ')}；组合子：${COMBINATORS.join(' ')}`,
    );
  }
  return evaluateLeaf(node, op, scope);
}

function evaluateLeaf(node, op, scope) {
  const want = node[op];
  const pathText = node.path;

  if (op === 'exists') {
    if (typeof want !== 'boolean') throw badCondition(`exists 的值必须是 true/false，收到 ${preview(want)}`);
    if (pathText === undefined) throw badCondition('exists 也要有 path（形如 {"path":"result.x","exists":true}）');
    const r = readPath(scope, pathText);
    return want ? r.ok : !r.ok;
  }

  const target = pathText === undefined ? implicitTarget(scope, op) : readPath(scope, pathText);
  if (!target.ok) throw missingPathError(pathText, target, scope);
  const actual = target.value;

  switch (op) {
    case 'equals':
      return deepEqual(actual, want);
    case 'notEquals':
      return !deepEqual(actual, want);
    case 'in':
      return membership(actual, want, op);
    case 'notIn':
      return !membership(actual, want, op);
    case 'contains': {
      /**
       * 子串匹配。加它只有一个理由：`live --until` 的 `~` 就是子串
       * （`live.js:101` 的 `actual.includes(c.value)`），不给它一个算子，
       * `live` 就永远得留着第二套判定，「合并成一套」也就无从谈起。
       *
       * 语义照抄 live 的 `fieldText`：先把这个字段**字符串化**再比（null→'null'，
       * 对象→JSON 文本，其它→String(v)），这样 `~` 的 5 个等价点（数字/布尔/null/
       * 对象/缺键）在条件 JSON 里能一一对得上。区分大小写，和 `~` 一致。
       * 取反用组合子：`{"not":{"path":"result.x","contains":"y"}}`。
       *
       * 注意：游戏侧 `rb.poll` 没有这个算子（与 `not` 同类），所以含它的条件
       * **不能**原样下发，只能在 Node 侧求值。
       */
      if (typeof want !== 'string') throw badCondition(`contains 的值必须是字符串，收到 ${preview(want)}`);
      return stringForm(actual).includes(want);
    }
    case 'countEquals': {
      if (!Array.isArray(actual)) {
        throw opTypeError(op, pathText, actual, '数组');
      }
      if (typeof want !== 'number') throw badCondition(`countEquals 的值必须是数字，收到 ${preview(want)}`);
      return actual.length === want;
    }
    case 'allItems':
    case 'anyItem': {
      if (!Array.isArray(actual)) throw opTypeError(op, pathText, actual, '数组');
      const test = (element) => evaluate(want, { value: element, root: false });
      return op === 'allItems' ? actual.every(test) : actual.some(test);
    }
    default: {
      if (typeof actual !== 'number' || typeof want !== 'number') {
        throw opTypeError(op, pathText, actual, `数字（条件给的是 ${typeName(want)}）`);
      }
      return COMPARE[op](actual, want);
    }
  }
}

/** 列表项内的叶子可以省略 path，直接对元素本身判定 */
function implicitTarget(scope, op) {
  if (scope.root) {
    throw badCondition(
      `算子「${op}」缺少 path：根条件里的每个叶子都要写 path 并以 ${ROOT}. 开头，` +
        `形如 {"path":"${ROOT}.x","${op}":…}`,
    );
  }
  return { ok: true, value: scope.value };
}

/**
 * 读一个路径。找不到时**不抛**，把断点与那层的可用字段带回来——
 * 调用方要么据此抛「字段名写错了」，要么让 exists 直接回答有没有。
 */
function readPath(scope, pathText) {
  if (typeof pathText !== 'string' || !pathText) {
    throw badCondition(`path 必须是非空字符串，收到 ${preview(pathText)}`);
  }
  let rest = pathText;
  if (scope.root) {
    if (pathText !== ROOT && !pathText.startsWith(`${ROOT}.`)) {
      throw badCondition(`路径必须以 ${ROOT}. 开头（指本次探针返回的载荷根，例 ${ROOT}.state.hasCurrentGame），收到「${pathText}」`);
    }
    rest = pathText.slice(ROOT.length).replace(/^\./, '');
  } else if (pathText === ROOT || pathText.startsWith(`${ROOT}.`)) {
    throw badCondition(`allItems/anyItem 内部的路径是相对该元素的，不要再带 ${ROOT}. 前缀，收到「${pathText}」`);
  }

  const segments = rest.split('.').filter((s) => s.length > 0);
  let cur = scope.value;
  const walked = [];
  for (const seg of segments) {
    walked.push(seg);
    if (cur === null || typeof cur !== 'object') {
      return { ok: false, brokenAt: walked.join('.'), segment: seg, actualType: typeName(cur), available: [] };
    }
    const has = Array.isArray(cur) ? /^\d+$/.test(seg) && Number(seg) < cur.length : Object.prototype.hasOwnProperty.call(cur, seg);
    if (!has) {
      return { ok: false, brokenAt: walked.join('.'), segment: seg, actualType: typeName(cur), available: Object.keys(cur) };
    }
    cur = cur[seg];
  }
  return { ok: true, value: cur };
}

function missingPathError(pathText, r, scope) {
  const lines = [
    `条件里的路径取不到：${pathText}`,
    `  断在「${r.brokenAt}」这一段；那层的实际内容是 ${r.actualType}，可用字段：${
      r.available.length ? r.available.slice(0, 12).join(', ') : '（空）'
    }`,
  ];
  const guesses = r.available
    .map((k) => ({ k, d: editDistance(r.segment, k) }))
    .filter((x) => x.d <= Math.max(2, Math.ceil(r.segment.length / 3)))
    .sort((a, b) => a.d - b.d)
    .slice(0, 3)
    .map((x) => x.k);
  if (guesses.length) lines.push(`  相近的字段名：${guesses.join(' / ')}`);
  lines.push(`  这次采样：${preview(scope.value)}`);
  const err = new Error(lines.join('\n'));
  // 机器可判：waitFor 靠这个 code 把「字段还没出现」降级成「这次没成立」，其它错误照旧抛
  err.code = MISSING_PATH_CODE;
  err.missingPath = { path: pathText, brokenAt: r.brokenAt, segment: r.segment, available: r.available.slice(0, 12) };
  return err;
}

function membership(actual, want, op) {
  if (!Array.isArray(want)) throw badCondition(`「${op}」的值必须是数组，收到 ${preview(want)}`);
  return want.some((v) => deepEqual(actual, v));
}

function opTypeError(op, pathText, actual, expected) {
  return new Error(
    `算子「${op}」要求 ${pathText ?? '该值'} 是${expected}，实际是 ${typeName(actual)}（${preview(actual)}）`,
  );
}

function deepEqual(a, b) {
  if (a === b) return true;
  if (typeof a !== 'object' || typeof b !== 'object' || a === null || b === null) return false;
  if (Array.isArray(a) !== Array.isArray(b)) return false;
  const ka = Object.keys(a);
  const kb = Object.keys(b);
  if (ka.length !== kb.length) return false;
  return ka.every((k) => Object.prototype.hasOwnProperty.call(b, k) && deepEqual(a[k], b[k]));
}

/**
 * 把一个字段值转成「可做子串比较的文本」。
 *
 * 与 `live.js` 的 `fieldText` 同一套规则（null→'null'、对象→JSON 文本、其它→String(v)）：
 * `live --until` 合并进来之后两边必须是同一个转换，否则同一个 `~` 写法会在两处给出不同答案。
 */
function stringForm(v) {
  if (v === null) return 'null';
  if (typeof v === 'object') return JSON.stringify(v);
  return String(v);
}

function typeName(v) {
  if (v === null) return 'null';
  if (Array.isArray(v)) return `数组(${v.length})`;
  return typeof v === 'object' ? '对象' : typeof v;
}

function preview(v) {
  let text;
  try {
    text = JSON.stringify(v);
  } catch {
    text = String(v);
  }
  if (text === undefined) text = String(v);
  return text.length > 400 ? `${text.slice(0, 400)}…` : text;
}

function badCondition(message) {
  return new Error(`条件写错了：${message}`);
}


/**
 * 求值一次采样，返回 `{matched, missing}`。
 * 单独导出是因为采样层要「逐行预判」（拿到命中的那一行原文），而不只是知道真/假。
 */
export { tryMatch };
/** 字符串化规则（`contains` 与事件源的 `texts` 视图共用，保证两处答案一致） */
export { stringForm };
/**
 * 算子清单与注意事项——给 `cli atoms` 导出，别在别处再抄一份（抄了就会漂移）。
 */
const OPERATOR_NOTES = {
  contains: 'Node 侧扩展：先字符串化再比子串；rb.poll 没有这个算子，含它的条件不能下发游戏内',
  not: 'Node 侧扩展：rb.poll 没有这个算子，含它的条件不能下发游戏内',
  exists: '只判「有没有这个键」，null 也算有；所以「命中后才出现」的字段用 exists 是对的',
  countEquals: '只支持「等于」；要「至少 N 个」请让采样层给出计数（如 result.counts.<kind>）再用数值比较',
  allItems: '对空数组恒真；要同时断言非空请加 {"path":"...","countEquals":N} 之类的条件',
};
export { LEAF_OPERATORS, COMBINATORS, OPERATOR_NOTES };
