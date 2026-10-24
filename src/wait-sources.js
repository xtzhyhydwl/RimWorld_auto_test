/**
 * 四类采样来源：把「怎么取一次样」从「怎么判一个条件」里分出去。
 *
 * 分工（与 `src/condition.js` 的边界一致）：
 *   - `condition.js`：原子判定，输入一次采样载荷，输出真/假；
 *   - 本文件：**取样与聚合**（事件窗口、按 kind 计数、进程三态、桥接就绪、桥接工具调用），
 *     输出一个形状稳定的快照，让同一套条件 JSON 对四类来源都成立（`result.` 一律指本次快照根）；
 *   - `wait.js`：唯一的循环。
 *
 * 为什么聚合在这层而不加算子：窗口/计数/派生量是「取多少数据、怎么算」，不是「怎么比」。
 * 详见 docs/cli-wait-P1-事件进程等待与条件登记表-设计-20261024.md §2.4。
 */
import fs from 'node:fs';
import path from 'node:path';
import { StringDecoder } from 'node:string_decoder';
import { matchesCondition, stringForm } from './condition.js';
import { probePid, verifyInstance } from './proc.js';
import { WS, logPathFor, profileDir, resolveBridge } from './launcher.js';

const TAIL_MAX_BYTES = 262144;

// ---------------------------------------------------------------------------
// 事件文件（NDJSON / Player.log）
// ---------------------------------------------------------------------------

/**
 * 事件文件的默认位置。优先级（与 mod 侧 LiveChannel.ComputePath 的规则对齐）：
 *   1. `$DSH_LIVE_DIR/<profile>.ndjson` —— launcher.startGame() 会把这个变量交给游戏进程
 *   2. `<profile 存档目录>/dsh-live/game.ndjson` —— mod 在没有 DSH_LIVE_DIR 时的落点（存在才用）
 *   3. `<工作区>/runs/live/<profile>.ndjson` —— 本仓库的约定位置
 *
 * 这是目前**唯一**真正按 profile 隔离的通道（1.7 的 Player.log 是全机共用的）。
 */
export function resolveEventFile(profile = 'vanilla') {
  const dir = process.env.DSH_LIVE_DIR;
  if (dir) return path.join(dir, `${profile}.ndjson`);

  const inProfile = path.join(profileDir(profile), 'dsh-live', 'game.ndjson');
  if (fs.existsSync(inProfile)) return inProfile;

  return path.join(WS, 'runs', 'live', `${profile}.ndjson`);
}

/** 把一行原文解释成事件：log 源整行算 `text`；events 源必须是 JSON 对象（半行/坏行跳过） */
export function toEvent(raw, source = 'events') {
  // 个别 Windows 写入路径会给**第一行**加 BOM（PowerShell 的 UTF8 就是一例）
  const line = raw.charCodeAt(0) === 0xfeff ? raw.slice(1) : raw;
  if (source === 'log') return { kind: 'log', text: line };
  try {
    const parsed = JSON.parse(line);
    return parsed && typeof parsed === 'object' && !Array.isArray(parsed) ? parsed : null;
  } catch {
    return null;
  }
}

/**
 * 增量跟读者：记住偏移量，每次 poll 只返回**新增**的完整行。
 * 文件还没出现时什么都不返回；文件变小 = 被轮转/截断 ⇒ 从 0 重新读。
 * 用 StringDecoder 接半行/半个多字节字符，避免把中文字符劈成乱码。
 */
export function createFollower(file) {
  const decoder = new StringDecoder('utf8');
  let offset = 0;
  let pending = '';
  let ino = null;

  const readFrom = (start) => {
    let buf;
    try {
      buf = fs.readFileSync(file);
    } catch {
      // 文件还没出现：不改 offset，下次再试（游戏一起来就会自建）
      return [];
    }
    if (buf.length < start) {
      offset = 0;
      pending = '';
      return readFrom(0);
    }
    offset = buf.length;
    const text = pending + decoder.write(buf.subarray(start));
    const parts = text.split(/\r?\n/);
    pending = parts.pop() ?? '';
    return parts.filter((l) => l.length > 0);
  };

  return {
    poll() {
      const lines = readFrom(offset);
      return lines;
    },
    seekToEnd() {
      try {
        offset = fs.statSync(file).size;
        pending = '';
      } catch {
        offset = 0;
      }
    },
    get offset() {
      return offset;
    },
    /** inode/大小变化检测留给调用方：本工具只按偏移读，轮转由「变小」启发式处理 */
    get ino() {
      return ino;
    },
  };
}

/** 文件最后 n 行（超时诊断与 `live --last` 用）。maxBytes 限定读多少尾巴，避免读整个大日志 */
export function tailLines(file, n, maxBytes = TAIL_MAX_BYTES) {
  let buf;
  try {
    buf = fs.readFileSync(file);
  } catch {
    return [];
  }
  const slice = buf.subarray(Math.max(0, buf.length - maxBytes)).toString('utf8');
  const lines = slice.split(/\r?\n/).filter((l) => l.length > 0);
  return n === Number.MAX_SAFE_INTEGER ? lines : lines.slice(-n);
}

/**
 * 事件源采样器（同步：只读文件，不碰网络）。
 *
 * 快照形状（条件 JSON 就按这个写）：
 *   `result.events[]`   窗口内的事件对象（最近的在后）
 *   `result.eventTexts[]`  同上，但每个字段都字符串化了（承载 `~`/子串语义，与 live 的 `fieldText` 一致）
 *   `result.counts`     `{<kind>: 窗口内条数}` —— 「某类事件至少 N 次」用 `greaterThanOrEqual`
 *   `result.lineMatch`  第一次命中的那一行原文 `{raw, index, at}`；**命中前不带这个键**
 *                       （`exists` 只看 hasOwnProperty，不能让 null 冒充「有」）
 *   `result.readCount`  本次等待一共读到多少条事件（诊断用）
 *
 * `perLineCondition` 用同一套 `matchesCondition` 逐行预判，作用域是 `{texts}`（**这一行**的字符串化字段表，
 * 所以 `live` 的 `--until` 翻译成 `{"path":"result.texts.<key>",...}`）；快照里的事件表叫 `eventTexts`
 * 以免两个 `texts` 含义打架。这不是第二套判定，
 * 只是把「已经成立过的那一行」记住，好让 `live` 能把原文打印出来。
 */
export function eventSampler({
  file,
  source = 'events',
  fromNow = false,
  perLineCondition = null,
  windowMs = null,
  follower = null,
} = {}) {
  const reader = follower ?? createFollower(file);
  if (fromNow) reader.seekToEnd();
  let readCount = 0;
  let lineMatch;
  const recent = [];

  const poll = () => {
    for (const raw of reader.poll()) {
      const event = toEvent(raw, source);
      if (!event) continue;
      readCount += 1;
      /**
       * 窗口裁剪用**事件自己的时间戳**（`ts`，Unix 秒），没有才退回到达时间。
       * 为什么不能一律用到达时间：扫历史文件时所有事件都是「刚才读到的」，
       * 用到达时间会让 `--window-ms` 形同虚设（实测：旧文件也能命中「最近 1 秒内出现过」）。
       */
      const ownTs = Number(event.ts);
      recent.push({ raw, event, ts: Number.isFinite(ownTs) ? ownTs * 1000 : Date.now() });
      if (perLineCondition && !lineMatch) {
        const texts = Object.fromEntries(Object.entries(event).map(([k, v]) => [k, stringForm(v)]));
        if (matchesCondition(perLineCondition, { texts })) {
          lineMatch = { raw, index: readCount, at: new Date().toISOString() };
        }
      }
    }
    // 只有给了 windowMs 才裁剪：没给 = 保留这次等待里读到的全部事件（与 live 的「从头扫」语义一致）
    if (windowMs) {
      const cutoff = Date.now() - windowMs;
      while (recent.length && recent[0].ts < cutoff) recent.shift();
    }
    const counts = {};
    for (const r of recent) {
      const kind = r.event.kind ?? (source === 'log' ? 'log' : null);
      if (kind !== null && kind !== undefined) counts[kind] = (counts[kind] ?? 0) + 1;
    }
    const snapshot = {
      events: recent.map((r) => r.event),
      eventTexts: recent.map((r) =>
        Object.fromEntries(Object.entries(r.event).map(([k, v]) => [k, stringForm(v)])),
      ),
      counts,
      readCount,
    };
    if (lineMatch) snapshot.lineMatch = lineMatch;
    return snapshot;
  };
  poll.readCount = () => readCount;
  return poll;
}

// ---------------------------------------------------------------------------
// 进程
// ---------------------------------------------------------------------------

/**
 * 进程源采样器。快照形状：
 *   `result.pid` / `result.state`（`alive|foreign|none|unknown`）/ `result.alive`（只有 alive 为真）
 *   给 `verifyIdentity` 时再加 `result.verified` / `result.profile` / `result.reason`
 *
 * `alive` 只用 `probePid`，不认 `foreign`：PID 被别的进程复用时 `process.kill` 会报 EPERM，
 * 当它是「活着」正是上次「陈旧记录挡住 start」的根因。
 */
export function processSampler({ pid, profile = null, verifyIdentity = false } = {}) {
  return async () => {
    const probe = probePid(pid);
    const snapshot = { pid: Number(pid), state: probe.state, alive: probe.state === 'alive' };
    if (!verifyIdentity) return snapshot;
    const identity = await verifyInstance(pid, {
      processesDir: path.join(WS, 'profiles'),
      ...(profile ? { expectedProfile: profile } : {}),
    });
    return { ...snapshot, verified: identity.ok, profile: identity.profile ?? null, reason: identity.reason };
  };
}

// ---------------------------------------------------------------------------
// 桥接就绪 / 桥接工具
// ---------------------------------------------------------------------------

/**
 * 桥接就绪采样器（异步：要查进程身份）。快照形状：
 *   `result.ready`（归属校验通过）/ `result.source`（`record|log|env`）/ `result.host` / `result.port`
 *   `result.reason` / `result.evidence[]`
 *
 * 「就绪」= `resolveBridge` 四道校验全过（记录可信 / PID 是本 profile / 端口一致 / token 一致），
 * 而不是「端口能连上」——后者在多实例下可能连到别人的局。
 */
export function bridgeReadySampler({ profile = 'vanilla' } = {}) {
  return async () => {
    const resolved = await resolveBridge(profile);
    return {
      ready: resolved.ok,
      source: resolved.info?.source ?? null,
      host: resolved.info?.host ?? null,
      port: resolved.info?.port ?? null,
      logFile: logPathFor(profile),
      reason: resolved.reason,
      evidence: resolved.evidence,
    };
  };
}

/** 桥接工具采样器：一次只读工具调用，快照就是工具返回的载荷根（与 `cli call` 完全一致） */
export function bridgeSampler({ client, probe, args = {}, deadline }) {
  return () => client.callTool(probe, args, { timeoutMs: Math.max(1, deadline - Date.now()) });
}

/**
 * 四类来源的「说明书」（给 `cli atoms` 导出，也是 `buildSampler` 的合法取值来源）。
 * 快照字段必须与各 sampler 实际返回的一致 —— 这是代理写条件时的唯一依据。
 */
export const SOURCE_SPECS = {
  bridge: {
    what: '调一次只读桥接工具',
    needs: ['--probe（或用 --scenario）'],
    snapshot: '工具返回的载荷本身（与 cli call 一致），路径以 result. 开头',
    example: { 'result.state.playable': true },
  },
  event: {
    what: '跟读 NDJSON 事件文件或 Player.log 的行',
    needs: ['--file（默认 runs/live/<profile>.ndjson）', '--event-format events|log', '--new-only', '--window-ms'],
    snapshot:
      'events[]（事件对象）、eventTexts[]（同上的字符串化视图，承载子串语义）、counts{}（按 kind 计数）、lineMatch（命中后才出现）、readCount',
    example: { 'result.counts.colony_started': '>= 1' },
  },
  process: {
    what: '探一个 PID 的三态',
    needs: ['--pid（缺省用本 profile 记录里的 PID）', '--verify（连身份一起校验，约 +0.3s/次）'],
    snapshot: 'pid、state（alive|foreign|none|unknown）、alive；开 --verify 时另有 verified/profile/reason',
    example: { 'result.alive': false },
  },
  'bridge-ready': {
    what: '校验「这个桥接是不是我的」（四道校验）',
    needs: [],
    snapshot: 'ready、source（record|log|env）、host、port、logFile、reason、evidence[]',
    example: { 'result.ready': true },
  },
};

/**
 * 按来源造采样器。`deps.client` 只有 `bridge` 需要。
 * 返回值直接喂 `waitFor(probe, {condition, ...})`。
 */
export function buildSampler(spec = {}, deps = {}) {
  const { source, profile = 'vanilla' } = spec;
  switch (source) {
    case 'bridge':
      if (!deps.client) throw new Error('bridge 来源需要 deps.client');
      return bridgeSampler({ client: deps.client, probe: spec.probe, args: spec.args, deadline: spec.deadline });
    case 'event':
      return eventSampler({
        file: spec.file ?? resolveEventFile(profile),
        source: spec.eventSource ?? 'events',
        fromNow: Boolean(spec.fromNow),
        windowMs: spec.windowMs ?? null,
        perLineCondition: spec.perLineCondition ?? null,
      });
    case 'process':
      return processSampler({ pid: spec.pid, profile, verifyIdentity: Boolean(spec.verifyIdentity) });
    case 'bridge-ready':
      return bridgeReadySampler({ profile });
    default:
      throw new Error(`不认识的 source「${source}」（可用：${Object.keys(SOURCE_SPECS).join(' / ')}）`);
  }
}
