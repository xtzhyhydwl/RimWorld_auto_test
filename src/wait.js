/**
 * 唯一的等待原语：反复采样一个探针（`async () => 载荷`），直到条件 JSON 成立
 * （`ok:true`）或护栏到期（`ok:false`，并把最后一次采样带回来）。
 *
 * 为什么判定留在 Node 侧、不下发给游戏内的 `rb.poll`：
 *   1) 能离线测——条件写对写错不用开游戏就能验，改一条条件不必等几十分钟；
 *   2) 追溯链——字段路径和「谁在用」在同一个仓库里可 grep；塞进 `rb.poll` 的
 *      JSON 里，字段名写错只会**静默不匹配**（设计稿 §4.2）。
 * 条件语法刻意与桥接的 15 个条件算子同构，所以同一份 JSON 以后可以原样下发
 * 给 `rb.poll`（`runs/food-recon/lua_reference.json` 的 pollingGuidance 例子）。
 *
 * 本文件不连 GABP、不读日志、不查进程：探针由调用方给，所以同一套判定能用于
 * 桥接工具、日志尾部、PID 存活（设计稿 §4.4 要点 3）。
 * 也不做「等它做完」的固定延时——唯一的定时是条件轮询的间隔。
 */
import { editDistance } from './toolname.js';

export const DEFAULT_TIMEOUT_MS = 60_000;
export const DEFAULT_INTERVAL_MS = 200;

/**
 * 条件里的路径根。
 *
 * `result` 指**这次探针调用返回的载荷根对象**：`callTool` 原样返回载荷
 * （`rimbridge/get_bridge_status` 的根上就有 `state`，`rimworld/list_colonists`
 * 的根上就有 `colonists`），与 `rb.poll` 的 `result.` 同一含义。
 * 不认相对路径：字段名只能靠实测反推，替人「猜一个」等于静默失败。
 */
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
  'allItems',
  'anyItem',
];
const COMPARE = {
  greaterThan: (a, b) => a > b,
  greaterThanOrEqual: (a, b) => a >= b,
  lessThan: (a, b) => a < b,
  lessThanOrEqual: (a, b) => a <= b,
};

/**
 * 判断一次采样是否满足条件。返回布尔：条件本身写错（不认识的算子、值类型不对、
 * 路径没带 `result.` 前缀）会**抛错**；路径只是这一次取不到，算「没成立」（见 tryMatch）。
 *
 * @param {object} condition 条件 JSON
 * @param {any} sample 探针返回的载荷
 * @returns {boolean}
 */
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

/**
 * 主原语。`probe` 是任意异步探针；`condition` 是条件 JSON。
 *
 * 超时**返回**而不是抛错（设计稿 §4.4 要点 1：超时是分支不是异常），调用方拿
 * `ok:false` + `attempts` + `sample` 出证据；探针自己坏掉则抛错，因为「取不到
 * 观测」和「观测到没成立」是两回事，再等也是白等（设计稿 §2.2）。
 * 字段这一次不存在（例：还没进图时 `list_colonists` 没有 `colonists`）算「没成立」，
 * 继续轮询；超时把最后一次断点从 `missingPath` 带回，让调用方能说清「字段一直没出现」
 * 而不是「条件一直不成立」。
 *
 * @param {() => Promise<any>} probe
 * @param {object} opts
 * @param {object} opts.condition 条件 JSON（见 docs/cli-wait-用法-20261024.md）
 * @param {number} [opts.timeoutMs]
 * @param {number} [opts.intervalMs]
 * @param {string} [opts.what] 人话名字，进错误信息
 * @returns {Promise<{ok:boolean, elapsedMs:number, attempts:number, sample:any,
 *   missingPath: {path:string, brokenAt:string, segment:string, available:string[]}|null}>}
 *   `missingPath` 非空 = 最后一次采样是「路径取不到」（不是「值不匹配」）。
 */
export async function waitFor(
  probe,
  { condition, timeoutMs = DEFAULT_TIMEOUT_MS, intervalMs = DEFAULT_INTERVAL_MS, what = '条件' } = {},
) {
  if (typeof probe !== 'function') throw new Error('waitFor 需要 probe（一个返回采样的函数）');
  if (condition === undefined || condition === null) throw new Error('waitFor 需要 condition（条件 JSON）');
  if (!Number.isFinite(timeoutMs) || timeoutMs <= 0) {
    throw new Error(`timeoutMs 必须是正数（毫秒），收到「${timeoutMs}」`);
  }
  if (!Number.isFinite(intervalMs) || intervalMs <= 0) {
    throw new Error(`intervalMs 必须是正数（毫秒），收到「${intervalMs}」`);
  }

  const startedAt = Date.now();
  let attempts = 0;
  let sample;
  // 最后一次采样是「路径取不到」时把它带回去：超时要能说清是「字段一直没出现」而不是
  // 「条件一直不成立」——这两件事给人的下一步动作完全不同（等进图 vs 改条件）。
  let lastMissing = null;

  for (;;) {
    // 预算用完就不再发新探针：否则会拿 1ms 的超时去打桥接，把「时间到了」误报成「桥接挂了」
    if (attempts > 0 && Date.now() - startedAt >= timeoutMs) {
      return { ok: false, elapsedMs: Date.now() - startedAt, attempts, sample, missingPath: lastMissing };
    }
    attempts += 1;
    try {
      sample = await probe();
    } catch (err) {
      const elapsedMs = Date.now() - startedAt;
      if (sample !== undefined && elapsedMs >= timeoutMs) {
        // 最后一轮探针正好把预算用光：这是我们没时间再看了，不是桥接坏了
        return { ok: false, elapsedMs, attempts, sample, missingPath: lastMissing };
      }
      const wrapped = new Error(
        `探针故障（第 ${attempts} 次采样，${elapsedMs}ms）：${err.message}\n` +
          `  这不是「${what}」没达成，是取不到观测；再等也不会变好。`,
      );
      wrapped.cause = err;
      Object.assign(wrapped, { attempts, elapsedMs, sample });
      throw wrapped;
    }

    const { matched, missing } = tryMatch(condition, sample);
    lastMissing = missing;
    if (matched) {
      return { ok: true, elapsedMs: Date.now() - startedAt, attempts, sample, missingPath: null };
    }

    const remainingMs = timeoutMs - (Date.now() - startedAt);
    if (remainingMs <= 0) {
      return { ok: false, elapsedMs: Date.now() - startedAt, attempts, sample, missingPath: lastMissing };
    }
    // 唯一的定时：条件轮询的间隔（这是闭环——每轮都重新采样，不是「等它做完」）
    await new Promise((resolve) => setTimeout(resolve, Math.min(intervalMs, remainingMs)));
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
