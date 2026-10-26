/**
 * 上位机侧的 GameLink 客户端（命令通道 + 时间推进 API）。
 *
 * 这一层是**唯一真源**：`cli advance` 只是它的薄壳，其他脚本想推时间也 import 这里。
 *
 * 协议（与 `mods/DSH_GameLink/Source/CommandChannel.cs` 对应，v1）：
 *
 *   <root>/<profile>.link/
 *     hello.json      ← 游戏写：{v, session, modVersion, commands:[{name,mutating}], probeKinds, eventKinds}
 *     state.json      ← 游戏写：{v, session, updatedAt, game:{tick,paused,speed,active,run:{...}}}
 *     cmd/<seq>.req   ← 上位机写：`key=value` 行（入向 kv，游戏侧不带 JSON 解析器）
 *     ack/<seq>.ack   ← 游戏写：{v, seq, cmd, ok, at, tick, result|error}
 *
 * 根目录跟事件通道同源：`$DSH_LIVE_DIR` 优先，否则 `<存档目录>/dsh-live`；
 * 文件名的 profile 来自 `GABS_GAME_ID`（launcher 设成 `dsh-rimworld-<profile>`），拿不到就是 `game`。
 * 所以这里按「候选目录」去找，谁先有 hello.json 就是谁（换启动方式也不用改代码）。
 *
 * 纪律：本文件只做 I/O + 轮询，**不判断业务**；语义（推没推到、为什么停）由返回值如实带回。
 */

import fs from 'node:fs';
import path from 'node:path';
import { WS, profileDir } from './launcher.js';

/** 命令通道找不到时的统一说法：调用方（CLI）负责把它变成人话。 */
export class LinkUnavailableError extends Error {
  constructor(message, { tried = [], profile = null } = {}) {
    super(message);
    this.name = 'LinkUnavailableError';
    this.tried = tried;
    this.profile = profile;
  }
}

const PROTOCOL_VERSION = 1;
const ACK_TIMEOUT_MS = 15_000;
const POLL_MS = 100;
const STATE_STALE_MS = 5_000; // state.json 超过这么久没更新就说「游戏那边没在泵」
const ACK_KEEP = 200;

/** 本进程内的 seq 生成器：毫秒时间戳 + 3 位序号，跨进程重启也单调（游戏侧按 seq 去重）。 */
let seqCounter = 0;
function nextSeq() {
  seqCounter = (seqCounter + 1) % 1000;
  return Date.now() * 1000 + seqCounter;
}

/**
 * 命令通道的候选目录，按优先级：
 *   1. $DSH_LIVE_DIR/<profile>.link          —— launcher 显式给的（runs/live）
 *   2. <WS>/runs/live/<profile>.link         —— launcher 的默认值（没设 env 时）
 *   3. <profileDir>/dsh-live/<profile>.link  —— 手工启动、但设了 GABS_GAME_ID
 *   4. <profileDir>/dsh-live/game.link       —— 手工启动、也没设 GABS_GAME_ID
 */
export function linkDirCandidates(profile) {
  const dirs = [];
  const env = process.env.DSH_LIVE_DIR;
  if (env) dirs.push(path.join(env, `${profile}.link`));
  dirs.push(path.join(WS, 'runs', 'live', `${profile}.link`));
  dirs.push(path.join(profileDir(profile), 'dsh-live', `${profile}.link`));
  dirs.push(path.join(profileDir(profile), 'dsh-live', 'game.link'));
  return [...new Set(dirs)];
}

/** 选目录：有 hello.json 的第一个；都没有就返回首个候选（写命令前会报错，报错里列全部候选）。 */
export function resolveLinkDir(profile) {
  const candidates = linkDirCandidates(profile);
  for (const dir of candidates) {
    if (fs.existsSync(path.join(dir, 'hello.json'))) return dir;
  }
  return candidates[0];
}

function readJsonLenient(file) {
  let text;
  try {
    text = fs.readFileSync(file, 'utf8');
  } catch {
    return null;
  }
  try {
    return JSON.parse(text);
  } catch {
    return null; // 半写（原子替换的窗口）/ 正在被覆盖：调用方重试
  }
}

/** 读 hello.json；不存在或版本不认就抛 LinkUnavailableError（带候选目录，便于人排查）。 */
export async function readHello(profile) {
  const candidates = linkDirCandidates(profile);
  for (const dir of candidates) {
    const hello = readJsonLenient(path.join(dir, 'hello.json'));
    if (!hello) continue;
    if (hello.v !== PROTOCOL_VERSION) {
      throw new LinkUnavailableError(
        `命令通道协议版本不匹配：游戏侧 v=${hello.v}，上位机只认 v=${PROTOCOL_VERSION}（${dir}）。` +
          '把 mods/DSH_GameLink 更新到与本仓库一致后重启实例。',
        { tried: candidates, profile },
      );
    }
    return { ...hello, dir };
  }

  throw new LinkUnavailableError(
    [
      `找不到 dsh.gamelink 的命令通道（profile=${profile}）。可能原因：`,
      '  · 这个实例没启用 dsh.gamelink（底座 mod 之一），或起的不是本仓库启动器的实例；',
      '  · 游戏刚起来还没写 hello.json（等几秒再试）；',
      '  · DSH_LIVE_DIR / GABS_GAME_ID 与启动时不一致。',
      `候选目录：${candidates.join('  |  ')}`,
    ].join('\n'),
    { tried: candidates, profile },
  );
}

/** 读 state.json（游戏每 100 ms 重写一次；读不到就 null）。 */
export function readState(profile) {
  const dir = resolveLinkDir(profile);
  return readJsonLenient(path.join(dir, 'state.json'));
}

/** 把一条命令写成 `cmd/<seq>.req`（先写 .tmp 再 rename，游戏侧永远不会读到半行）。 */
function writeCommand(dir, seq, cmd, args) {
  const cmdDir = path.join(dir, 'cmd');
  fs.mkdirSync(cmdDir, { recursive: true });
  const lines = [`v=${PROTOCOL_VERSION}`, `seq=${seq}`, `cmd=${cmd}`];
  for (const [key, value] of Object.entries(args ?? {})) {
    if (value === undefined || value === null || value === '') continue;
    const text = typeof value === 'boolean' ? (value ? '1' : '0') : String(value);
    lines.push(`${key}=${text.replace(/[\r\n]+/g, ' ')}`);
  }
  const file = path.join(cmdDir, `${seq}.req`);
  fs.writeFileSync(`${file}.tmp`, `${lines.join('\n')}\n`, 'utf8');
  fs.renameSync(`${file}.tmp`, file);
  return seq;
}

function readAck(dir, seq) {
  return readJsonLenient(path.join(dir, 'ack', `${seq}.ack`));
}

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

/**
 * 发一条命令并等它的 ack（ack 只是「收到了、开始/失败了」，不代表长命令已跑完）。
 * @returns {Promise<{ok:boolean, result?:any, error?:{code:string,message:string}, tick?:number, seq:number}>}
 */
export async function call(profile, cmd, args = {}, { timeoutMs = ACK_TIMEOUT_MS, dir = null } = {}) {
  const hello = dir ? { dir } : await readHello(profile);
  const seq = writeCommand(hello.dir, nextSeq(), cmd, args);

  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const ack = readAck(hello.dir, seq);
    if (ack) return { ...ack, seq };
    await sleep(25);
  }

  return {
    ok: false,
    seq,
    error: {
      code: 'E_NO_ACK',
      message:
        `等了 ${timeoutMs} ms 没等到 ack（${path.join(hello.dir, 'ack', `${seq}.ack`)}）。` +
        '游戏没在泵命令：确认实例在跑、dsh.gamelink 已启用、且没有卡在长事件里。',
    },
  };
}

/** 只读状态：`state.game` 是游戏侧原始快照。 */
export async function status(profile) {
  const state = readState(profile);
  if (!state) {
    throw new LinkUnavailableError(`读不到 state.json（profile=${profile}）：游戏侧还没写过，或通道目录不对。`, {
      tried: linkDirCandidates(profile),
      profile,
    });
  }
  return state;
}

/** 停掉当前这一程并暂停。 */
export async function cancel(profile) {
  return call(profile, 'time.cancel');
}

/** 自检（Harmony 挂上了吗、信件/消息列表读得到吗）。 */
export async function probe(profile) {
  return call(profile, 'time.probe');
}

/** 速度档位白名单（与游戏侧 TimeSpeed 对齐）。 */
export const SPEEDS = ['Normal', 'Fast', 'Superfast', 'Ultrafast'];

/**
 * 按剩余距离选档：> 6000 tick 走 Ultrafast，否则 Fast（老 `advance` 的同一条策略，保持行为一致）。
 * @returns {string}
 */
export function pickSpeed(remainingTicks, requested = 'auto') {
  if (requested && requested !== 'auto') return requested;
  return remainingTicks > 6000 ? 'Ultrafast' : 'Fast';
}

/**
 * 推时间到 `toTick`（**等到落点才返回**，轮询藏在这里）。
 *
 * @param {object} o
 * @param {string} o.profile
 * @param {number} [o.toTick]        绝对目标 tick（与 ticks / stopWhen 至少给一个）
 * @param {number} [o.ticks]         相对 tick 数（在**游戏内**换算成 `当前+N`，不会因为上位机读到的 tick 过期而偏）
 * @param {string} [o.speed]         Normal|Fast|Superfast|Ultrafast|auto（默认 auto）
 * @param {string} [o.stopWhen]      停点判据，`;` 分隔，例如 `letter:ThreatBig;message:离开了`
 * @param {string} [o.watch]         只观察不停，命中写 probe_hit 事件
 * @param {boolean} [o.forceSpeed]   压掉游戏的 ForceNormalSpeed
 * @param {number} [o.maxWallClockMs] 游戏侧墙钟预算（默认 20 分钟）
 * @param {number} [o.maxTicks]      相对 tick 上限（防「事件永远不来」）
 * @param {number} [o.timeoutMs]     上位机侧的总等待上限（默认 maxWallClockMs + 90 s）
 * @returns {Promise<object>} 结果对象：{ok, fromTick, toTick, reached, ticksAdvanced, exact, stopReason, paused, elapsedMs, ticksPerSecond, planId, ...}
 */
export async function advanceTo({
  profile,
  toTick = null,
  ticks = null,
  speed = 'auto',
  stopWhen = null,
  watch = null,
  forceSpeed = false,
  maxWallClockMs = 1_200_000,
  maxTicks = null,
  timeoutMs = null,
}) {
  const hello = await readHello(profile);
  const hasAdvance = (hello.commands ?? []).some((c) => c.name === 'time.advance');
  if (!hasAdvance) {
    throw new LinkUnavailableError(
      `游戏侧的 dsh.gamelink 没有 time.advance 命令（hello.commands=${JSON.stringify(hello.commands)}）。` +
        'mod 版本太老：重新构建并重启实例。',
      { tried: linkDirCandidates(profile), profile },
    );
  }

  const before = readState(profile);
  const currentTick = before?.game?.tick ?? null;

  if ((toTick === null || toTick === undefined) && !ticks && !stopWhen && !maxTicks) {
    throw new Error('advanceTo 至少需要 toTick / ticks / stopWhen / maxTicks 之一');
  }
  if (ticks && (!Number.isInteger(ticks) || ticks <= 0)) {
    throw new Error(`ticks 必须是正整数，收到 ${ticks}`);
  }
  if (toTick !== null && toTick !== undefined && currentTick !== null && toTick <= currentTick) {
    throw new Error(`toTick 必须大于当前 tick（不能倒推）：当前 ${currentTick}，收到 ${toTick}`);
  }

  const remaining = ticks ?? (toTick != null && currentTick != null ? toTick - currentTick : 0);
  const chosenSpeed = pickSpeed(remaining, speed);

  const ack = await call(
    profile,
    'time.advance',
    {
      toTick: toTick ?? 0,
      ticks: ticks ?? 0,
      speed: chosenSpeed,
      stopWhen,
      watch,
      forceSpeed,
      maxWallClockMs,
      maxTicks: maxTicks ?? 0,
    },
    { dir: hello.dir },
  );

  if (!ack.ok) {
    return {
      ok: false,
      profile,
      source: 'gamelink',
      planId: null,
      fromTick: currentTick,
      toTick,
      speed: chosenSpeed,
      error: ack.error ?? { code: 'E_UNKNOWN', message: 'ack 里没有 error 也没有 ok' },
    };
  }

  const planId = ack.result?.planId ?? null;
  const budget = timeoutMs ?? maxWallClockMs + 90_000;
  const deadline = Date.now() + budget;
  let lastState = null;

  while (Date.now() < deadline) {
    const state = readState(profile);
    if (state) {
      lastState = state;
      const run = state.game?.run;
      if (run && run.planId === planId && run.active === false) {
        return normalizeResult({ profile, planId, run, state, chosenSpeed, ack });
      }
      // game 进程重启 / 存档被换掉：planId 消失 ⇒ 立刻如实报，不装死
      if (!run || run.planId !== planId) {
        const helloNow = readJsonLenient(path.join(hello.dir, 'hello.json'));
        if (helloNow && helloNow.session && state.session && helloNow.session !== hello.session) {
          return {
            ok: false,
            profile,
            source: 'gamelink',
            planId,
            error: { code: 'E_SESSION_CHANGED', message: '游戏进程重启了（hello.session 变了），这一程作废' },
            state,
          };
        }
      }
    }
    await sleep(POLL_MS);
  }

  return {
    ok: false,
    profile,
    source: 'gamelink',
    planId,
    error: {
      code: 'E_CLIENT_TIMEOUT',
      message: `上位机等了 ${budget} ms 没等到这一程结束（游戏侧 maxWallClockMs=${maxWallClockMs}）`,
    },
    state: lastState,
  };
}

function normalizeResult({ profile, planId, run, state, chosenSpeed, ack }) {
  const stopReason = run.stopReason ?? null;
  // tick 在 state.game.tick（不是 state.tick）—— 写错过一次，exact/reached 会永远是 null
  // 落点只用**这一程自己的记账**（fromTick + ticksAdvanced），不要用 state 里那个"现在"的 tick：
  // stalled/外部暂停之后游戏可能又走了几 tick，用实时 tick 会算出"到达了 X+12"这种自相矛盾的数字（2026-10-25 实测）。
  const landed = run.fromTick + (run.ticksAdvanced ?? 0);
  const exact = run.toTick > 0 ? landed === run.toTick : stopReason === 'probe' || stopReason === 'maxTicks';
  const reached = run.toTick > 0 ? Math.min(landed, run.toTick) : landed;
  return {
    ok: stopReason === 'target' || stopReason === 'probe' || stopReason === 'maxTicks',
    profile,
    source: 'gamelink',
    planId,
    from: run.fromTick,
    to: run.toTick || null,
    reached,
    ticksAdvanced: run.ticksAdvanced,
    exact,
    stopReason,
    stopDetail: run.stopDetail ?? null,
    stopPayload: run.stopPayload ?? null,
    paused: state.game?.paused === true,
    wallClockMs: run.elapsedMs,
    averageTicksPerSecond: run.ticksPerSecond,
    speed: chosenSpeed,
    speedResets: run.speedResets ?? 0,
    appliedAt: ack?.tick ?? null,
  };
}

/**
 * 等一个「事件型」结果：`advanceTo({stopWhen})` 的糖，把 stopPayload 提上来当第一等公民。
 * 例如：等右上角出现「离开了」的消息 —— `waitUntil({profile, stopWhen:'message:离开了', maxTicks:200000})`。
 */
export async function waitUntil({ profile, stopWhen, watch = null, maxTicks = 200_000, speed = 'auto', forceSpeed = false, maxWallClockMs = 1_200_000 }) {
  return advanceTo({ profile, toTick: null, speed, stopWhen, watch, forceSpeed, maxTicks, maxWallClockMs });
}

/** 清掉旧的 ack 文件（游戏侧也会自己保留最近 200 条；这里是给「手工调试后收摊」用的）。 */
export function pruneAcks(profile, keep = ACK_KEEP) {
  const dir = resolveLinkDir(profile);
  const ackDir = path.join(dir, 'ack');
  if (!fs.existsSync(ackDir)) return 0;
  const files = fs
    .readdirSync(ackDir)
    .filter((f) => f.endsWith('.ack'))
    .map((f) => ({ f, seq: Number.parseInt(f, 10) }))
    .filter((x) => Number.isFinite(x.seq))
    .sort((a, b) => a.seq - b.seq);
  const remove = Math.max(0, files.length - keep);
  for (let i = 0; i < remove; i += 1) {
    try {
      fs.unlinkSync(path.join(ackDir, files[i].f));
    } catch {
      /* 删不掉就留着 */
    }
  }
  return remove;
}
