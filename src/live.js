/**
 * 游戏侧事件通道的读取端 —— `node src/cli.js live ...`
 *
 * 干什么：跟读 DSH_GameLink 写的 NDJSON（`runs/live/<profile>.ndjson`），
 * 等一个条件成立就打印那行原文并退出 0 —— 上层编排因此不必「睡固定时长 + 事后读日志」。
 * 也可以跟读 Player.log（`--source log`）：mod 还没部署时同样能用（按纯文本行匹配）。
 *
 * 退出码约定（调用方就靠它区分三种结局，别看文本）：
 *   0 = 命中（或 `--last` 正常打完）
 *   2 = 超时，没等到
 *   1 = 参数错（抛异常，由 cli.js 的 main().catch 统一置 1）
 *
 * **2026-10-24：本文件不再有自己的等待循环**。以前它用 `setInterval` + 自己的 `matches()`，
 * 与 `wait.js` 是两套判定。现在：`--until` 只是**翻译**成条件 JSON，循环用 `waitFor`，
 * 取样用 `wait-sources.js` 的 `eventSampler`（逐行预判、命中行粘在 `lineMatch` 上）。
 * 这样「同一个条件在 live 与 wait 里答案一致」是结构保证，而不是靠两处对齐。
 *
 * 为什么默认从头扫：`--until` 问的常常是「那个事件发生了没有」，而 watcher 往往比游戏**晚起**
 * （先开殖民地、再挂 watcher 是常态）—— 从末尾跟会永远等不到已经落盘的那条。只看新内容就加 `--new-only`。
 *
 * 谓词语法（够用就行，不上表达式引擎）：
 *   `key=value`  顶层字段相等（数字/布尔按 JSON 字面量转字符串比，所以 `tick=123`、`speed=1`、`opened=true` 都成立）
 *   `key~value`  顶层字段包含子串
 *   多个条件用空格或 `&&` 连接，全部成立才算命中；值里可以有空格（`text~[VD] 1 incident`）。
 */
import path from 'node:path';
import { waitFor } from './wait.js';
import { eventSampler, resolveEventFile, tailLines } from './wait-sources.js';
import { logPathFor } from './launcher.js';

export const POLL_MS = 200;
export const DEFAULT_TIMEOUT_MS = 120000;
export const TIMEOUT_TAIL_LINES = 10;

const USAGE = [
  '用法: node src/cli.js live --profile <名字> (--until \'<条件>\' | --last N) [选项]',
  '',
  '  --until \'kind=colony_started\'            等到条件成立（命中打印该行并退出 0；超时退出 2）',
  '                                            默认从文件开头扫：历史里已有 ⇒ 立刻命中',
  '  --until \'kind=log text~[VD] 1 incident\'  多个条件用空格或 && 连接，全部成立才命中',
  '  --last N                                 只打印最后 N 条，立即退出 0（不等待）',
  '  --new-only                               只看从现在起新增的行（跳过历史；watcher 已确定事件还没发生才用）',
  '  --timeout 120000                         等多久（毫秒，默认 120000）',
  '  --source events|log                      跟读事件文件（默认）还是 Player.log',
  '  --file <路径>                            直接指定要跟读的文件',
].join('\n');

/**
 * 把 `--until` 文本拆成条件数组。
 *
 * 值里允许有空格，所以不能简单按空白切：先找出所有 `key<op>` 位置，
 * 每个条件的值 = 这个位置到**下一个** `key<op>` 位置之间的文本（去掉连接符 `&&`）。
 * 已知限制：值里不要出现 `x=` / `x~` 这种形状（会被当成下一个条件）。
 */
export function parsePredicate(text) {
  const re = /([A-Za-z_][A-Za-z0-9_]*)\s*(=|~)\s*/g;
  const heads = [];
  let m;
  while ((m = re.exec(text)) !== null) {
    heads.push({ key: m[1], op: m[2], start: m.index, valueStart: m.index + m[0].length });
  }

  const conditions = [];
  for (let i = 0; i < heads.length; i++) {
    const head = heads[i];
    const end = i + 1 < heads.length ? heads[i + 1].start : text.length;
    const value = text
      .slice(head.valueStart, end)
      .replace(/\s*&&\s*$/, '')
      .trim();
    conditions.push({ key: head.key, op: head.op, value });
  }
  return conditions;
}

/**
 * `--until` 谓词 → 条件 JSON（**纯翻译**，没有第二套判定）。
 *
 * 老实现把自己的字段**字符串化**再比（`fieldText`：数字/布尔按 JSON 字面量、对象转 JSON 文本、
 * `null`→`"null"`），所以这里统一走采样器的 `texts` 视图（同样字符串化）：
 *   `key=value` → `{"path":"result.texts.key","equals":"value"}`（两边都是字符串 ⇒ 与老实现同义）
 *   `key~value` → `{"path":"result.texts.key","contains":"value"}`（`contains` 就是为此加的算子）
 * 多个条件用 `all` 组合。字段缺失时 `equals`/`contains` 都按「路径取不到 = 未成立」处理，
 * 与老实现的 `fieldText === undefined ⇒ 不匹配` 同义。
 */
export function predicatesToCondition(predicates) {
  const leaves = predicates.map(({ key, op, value }) =>
    op === '='
      ? { path: `result.texts.${key}`, equals: value }
      : { path: `result.texts.${key}`, contains: value },
  );
  if (leaves.length === 1) return leaves[0];
  return { all: leaves };
}

/** 命中后才成立的条件：`lineMatch` 只在第一次命中时出现（不能用 null 冒充，`exists` 只看 hasOwnProperty） */
const LINE_MATCH_CONDITION = { path: 'result.lineMatch', exists: true };

function writeOut(line) {
  process.stdout.write(line + '\n');
}

function note(text) {
  process.stderr.write(text + '\n');
}

/**
 * live 子命令的实现。返回退出码（0 命中 / 2 超时；参数错与读文件失败会 throw，由 cli.js 置 1）。
 */
export async function runLive({ profile = 'vanilla', flags = {} } = {}) {
  const source = flags.source === undefined ? 'events' : String(flags.source);
  if (source !== 'events' && source !== 'log') {
    throw new Error(`--source 只认 events|log，收到「${source}」\n\n${USAGE}`);
  }

  const hasLast = flags.last !== undefined;
  const last = hasLast ? Number(flags.last) : null;
  if (hasLast && (!Number.isInteger(last) || last <= 0)) {
    throw new Error(`--last 必须是正整数，收到「${flags.last}」\n\n${USAGE}`);
  }

  const untilRaw = flags.until === undefined ? null : String(flags.until);
  if (hasLast && untilRaw !== null) {
    throw new Error(`--last 与 --until 不能同时用（一个是翻历史，一个是等未来）\n\n${USAGE}`);
  }
  if (!hasLast && untilRaw === null) {
    throw new Error(`要么给 --until，要么给 --last N\n\n${USAGE}`);
  }

  let predicates = [];
  if (untilRaw !== null) {
    predicates = parsePredicate(untilRaw);
    if (!predicates.length) {
      throw new Error(`--until 解析不出任何条件：「${untilRaw}」（形如 kind=colony_started）\n\n${USAGE}`);
    }
  }

  const timeoutMs = flags.timeout === undefined ? DEFAULT_TIMEOUT_MS : Number(flags.timeout);
  if (!Number.isFinite(timeoutMs) || timeoutMs <= 0) {
    throw new Error(`--timeout 必须是正数（毫秒），收到「${flags.timeout}」\n\n${USAGE}`);
  }

  const newOnly = Boolean(flags['new-only']);
  if (newOnly && flags['from-start']) {
    throw new Error(`--new-only 与 --from-start 互斥（默认已是从头扫，--from-start 只剩兼容意义）\n\n${USAGE}`);
  }

  const file =
    flags.file === undefined
      ? source === 'log'
        ? logPathFor(profile)
        : resolveEventFile(profile)
      : path.resolve(String(flags.file));

  if (hasLast) {
    const lines = tailLines(file, last, Number.MAX_SAFE_INTEGER);
    if (!lines.length) note(`[live] ${file} 里没有可打印的行（文件不存在或还是空的）`);
    for (const line of lines) writeOut(line);
    return 0;
  }

  note(
    `[live] 等待 ${predicates.map((c) => `${c.key}${c.op}${c.value}`).join(' && ')}` +
      `  ← profile=${profile} source=${source} from=${newOnly ? 'now' : 'start'} file=${file}`,
  );

  const sampler = eventSampler({
    file,
    source,
    fromNow: newOnly,
    perLineCondition: predicatesToCondition(predicates),
  });

  const result = await waitFor(sampler, {
    condition: LINE_MATCH_CONDITION,
    timeoutMs,
    intervalMs: POLL_MS,
    what: `事件（${file}）`,
  });

  if (result.ok) {
    const hit = result.sample?.lineMatch;
    writeOut(hit?.raw ?? '');
    note(`[live] 命中（等待 ${result.elapsedMs}ms，共读到 ${result.sample?.readCount ?? 0} 条事件）`);
    return 0;
  }

  const tail = tailLines(file, TIMEOUT_TAIL_LINES);
  note(
    `[live] 超时 ${timeoutMs}ms 没等到（读到 ${result.sample?.readCount ?? 0} 条事件）；` +
      `下面是文件最后 ${tail.length} 条：`,
  );
  for (const line of tail) writeOut(line);
  return 2;
}
