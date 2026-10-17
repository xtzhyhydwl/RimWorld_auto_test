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
 * 为什么默认从头扫：`--until` 问的常常是「那个事件发生了没有」，而 watcher 往往比游戏**晚起**
 * （先开殖民地、再挂 watcher 是常态）—— 从末尾跟会永远等不到已经落盘的那条。只看新内容就加 `--new-only`。
 *
 * 谓词语法（够用就行，不上表达式引擎）：
 *   `key=value`  顶层字段相等（数字/布尔按 JSON 字面量转字符串比，所以 `tick=123`、`speed=1`、`opened=true` 都成立）
 *   `key~value`  顶层字段包含子串
 *   多个条件用空格或 `&&` 连接，全部成立才算命中；值里可以有空格（`text~[VD] 1 incident`）。
 */
import fs from 'node:fs';
import path from 'node:path';
import { StringDecoder } from 'node:string_decoder';
import { logPathFor, profileDir, WS } from './launcher.js';

export const POLL_MS = 200;
export const DEFAULT_TIMEOUT_MS = 120000;
export const TIMEOUT_TAIL_LINES = 10;
const TAIL_MAX_BYTES = 262144;

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
 * 事件文件的默认位置。优先级（与 mod 侧 LiveChannel.ComputePath 的规则对齐）：
 *   1. `$DSH_LIVE_DIR/<profile>.ndjson` —— launcher.startGame() 会把这个变量交给游戏进程
 *   2. `<profile 存档目录>/dsh-live/game.ndjson` —— mod 在没有 DSH_LIVE_DIR 时的落点（存在才用）
 *   3. `<工作区>/runs/live/<profile>.ndjson` —— 本仓库的约定位置
 */
export function resolveEventFile(profile = 'vanilla') {
  const dir = process.env.DSH_LIVE_DIR;
  if (dir) return path.join(dir, `${profile}.ndjson`);

  const inProfile = path.join(profileDir(profile), 'dsh-live', 'game.ndjson');
  if (fs.existsSync(inProfile)) return inProfile;

  return path.join(WS, 'runs', 'live', `${profile}.ndjson`);
}

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

/** 字段转成可比较的字符串：数字/布尔按 JSON 字面量，对象转 JSON 文本，缺失返回 undefined。 */
function fieldText(event, key) {
  if (!Object.prototype.hasOwnProperty.call(event, key)) return undefined;
  const v = event[key];
  if (v === null) return 'null';
  if (typeof v === 'object') return JSON.stringify(v);
  return String(v);
}

export function matches(event, conditions) {
  for (const c of conditions) {
    const actual = fieldText(event, c.key);
    if (actual === undefined) return false;
    if (c.op === '=' ? actual !== c.value : !actual.includes(c.value)) return false;
  }
  return true;
}

/**
 * 增量跟读者：记住偏移量，每次 poll 只返回**新增**的完整行。
 * 文件还没出现时什么都不返回（游戏一起来就会自建目录和文件）；
 * 文件变小 = 被轮转/截断 ⇒ 从 0 重新读。
 * 用 StringDecoder 接半行/半个多字节字符，避免把中文字符劈成乱码。
 */
export function createFollower(file) {
  const decoder = new StringDecoder('utf8');
  let offset = 0;
  let pending = '';

  return {
    /** 跳到当前末尾（`--new-only`）：只关心「从现在起」发生的事。 */
    seekToEnd() {
      try {
        offset = fs.statSync(file).size;
      } catch {
        offset = 0;
      }
      pending = '';
    },

    poll() {
      let stat;
      try {
        stat = fs.statSync(file);
      } catch {
        return [];
      }

      if (stat.size < offset) {
        offset = 0;
        pending = '';
        decoder.end();
      }
      if (stat.size === offset) return [];

      const length = stat.size - offset;
      const buf = Buffer.alloc(length);
      let fd;
      let read = 0;
      try {
        fd = fs.openSync(file, 'r');
        read = fs.readSync(fd, buf, 0, length, offset);
      } catch {
        return [];
      } finally {
        if (fd !== undefined) {
          try {
            fs.closeSync(fd);
          } catch {
            /* 关不掉就算了，下一次会重新开 */
          }
        }
      }

      offset += read;
      pending += decoder.write(buf.subarray(0, read));
      const parts = pending.split('\n');
      pending = parts.pop();
      return parts.map((s) => s.replace(/\r$/, '')).filter((s) => s.length > 0);
    },
  };
}

/** 读文件最后 n 条（有上界：只读末尾 TAIL_MAX_BYTES，避免为了 10 行读几百 MB 日志）。 */
export function tailLines(file, n, maxBytes = TAIL_MAX_BYTES) {
  let fd;
  try {
    fd = fs.openSync(file, 'r');
    const size = fs.fstatSync(fd).size;
    const start = Math.max(0, size - maxBytes);
    const buf = Buffer.alloc(size - start);
    const read = fs.readSync(fd, buf, 0, buf.length, start);
    const lines = buf
      .subarray(0, read)
      .toString('utf8')
      .split(/\r?\n/)
      .filter((s) => s.length > 0);
    return lines.slice(-n);
  } catch {
    return [];
  } finally {
    if (fd !== undefined) {
      try {
        fs.closeSync(fd);
      } catch {
        /* 同上 */
      }
    }
  }
}

/** 只按「事件」解释时，一行必须是合法 JSON 对象；半行/坏行直接跳过（轮转时可能撞上）。 */
function toEvent(raw, source) {
  // 个别 Windows 写入路径会给**第一行**加 BOM（PowerShell 的 UTF8 编码就是一例）；不是我们写的，但读的时候顺手容忍。
  const line = raw.charCodeAt(0) === 0xfeff ? raw.slice(1) : raw;
  if (source === 'log') return { kind: 'log', text: line };
  try {
    const parsed = JSON.parse(line);
    return parsed && typeof parsed === 'object' ? parsed : null;
  } catch {
    return null;
  }
}

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

  let conditions = [];
  if (untilRaw !== null) {
    conditions = parsePredicate(untilRaw);
    if (!conditions.length) {
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
    `[live] 等待 ${conditions.map((c) => `${c.key}${c.op}${c.value}`).join(' && ')}` +
      `  ← profile=${profile} source=${source} from=${newOnly ? 'now' : 'start'} file=${file}`,
  );

  const follower = createFollower(file);
  if (newOnly) follower.seekToEnd();

  const startedAt = Date.now();
  let newEvents = 0;

  return await new Promise((resolve) => {
    const timer = setInterval(() => {
      const lines = follower.poll();

      for (const raw of lines) {
        const event = toEvent(raw, source);
        if (!event) continue;
        newEvents++;
        if (!matches(event, conditions)) continue;
        clearInterval(timer);
        writeOut(raw);
        note(`[live] 命中（等待 ${Date.now() - startedAt}ms，共读到 ${newEvents} 条事件）`);
        resolve(0);
        return;
      }

      if (Date.now() - startedAt >= timeoutMs) {
        clearInterval(timer);
        const tail = tailLines(file, TIMEOUT_TAIL_LINES);
        note(
          `[live] 超时 ${timeoutMs}ms 没等到（读到 ${newEvents} 条事件）；` +
            `下面是文件最后 ${tail.length} 条：`,
        );
        for (const line of tail) writeOut(line);
        resolve(2);
      }
    }, POLL_MS);
  });
}
