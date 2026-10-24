/**
 * RimWorld 启动器 —— 用独立存档目录跑测试实例，绝不碰主力存档。
 *
 * 全部依赖 RimWorld / Unity 原生命令行参数：
 *   -savedatafolder=<dir>  把 Config / Saves / Prefs 重定向到独立目录
 *   -logFile <path>        日志写到指定文件，从这里抓桥接端口和 token
 *   -screen-fullscreen 0   窗口化（隐藏窗口时 Unity 可能不渲染，截图会全黑）
 *   -screen-width/-height  固定窗口尺寸，让截图尺寸稳定可比
 *
 * 桥接信息由 RimBridgeServer 在 direct 模式写进日志：
 *   [RimBridge] GABP server running standalone on port 5174
 *   [RimBridge] Bridge token: xxxxx
 */
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import net from 'node:net';
import { randomBytes, createHash } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import {
  capture,
  spawnDetached,
  isAlive,
  probePid,
  verifyInstance,
  killForce,
  killGraceful,
  tryListProcessesByName,
  listProcessesByPowerShell,
} from './proc.js';
import { connectGabp } from './gabp.js';
import { ensurePrefs, resolvePrefs, prefsPath } from './prefs.js';
import { loadBaseMods, ensureBaseMods } from './base-mods.js';

export const WS = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');

/**
 * 游戏入口 exe 的候选名，按优先级排列。
 *
 * 为什么不能写死一个：2026-10-24 Steam 把本机 RimWorld 换成 1.7 unstable，
 * 入口从 `RimWorldWin64.exe`（Unity 版）换成 `RimWorld.exe`（.NET 10 宿主）并删掉了旧文件。
 * 写死旧名会让启动、进程枚举、`--force` 三处一起失效。探测一次，三处共用。
 */
export const GAME_IMAGE_CANDIDATES = ['RimWorld.exe', 'RimWorldWin64.exe'];
export const DEFAULT_GAME_ROOT = 'D:\\SteamLibrary\\steamapps\\common\\RimWorld';

export function gameRoot() {
  return process.env.RIMWORLD_ROOT || DEFAULT_GAME_ROOT;
}

/**
 * 探测真实入口 exe —— 启动 / 进程枚举 / stop 的进程匹配**全部**只用这里的结果。
 *
 * 返回 `{ image, exe, found, searched }`：
 *   - `found:false`（两个候选都不存在）时仍给出首选名，好让进程枚举有个名字可用、且不抛错；
 *     需要严格报错的是启动路径（`startGame` 用 `searched` 列出找过的全部路径）。
 *   - `searched` 是报错要用的完整路径清单，避免调用方各自再拼一遍。
 */
export function detectGameImage(root = gameRoot()) {
  const searched = GAME_IMAGE_CANDIDATES.map((name) => path.join(root, name));
  const foundName = GAME_IMAGE_CANDIDATES.find((name) => fs.existsSync(path.join(root, name)));
  const image = foundName ?? GAME_IMAGE_CANDIDATES[0];
  return { image, exe: path.join(root, image), found: Boolean(foundName), searched };
}

/** 游戏自己写的版本串（`Version.txt`，如 `1.7.4985 rev1269`）；读不到返回 null */
export function readGameVersionText(root = gameRoot()) {
  try {
    const text = fs.readFileSync(path.join(root, 'Version.txt'), 'utf8').trim();
    return text || null;
  } catch {
    return null;
  }
}

/**
 * 1.7（Waylith）起日志落点变了：游戏**忽略** `-logFile`，固定写到**默认存档目录**下的
 * `Logs\Player.log`（不是 `-savedatafolder` 指的那个 profile 目录）。
 * 实测依据：2026-10-24 17:50 那次 1.7 启动，日志头第 7 行 `Log file:` 就是本路径，
 * 而 `-logFile profiles\rhah\Player.log` 一个字节都没写。
 */
export const WAYLITH_LOG_DIR = path.join(
  process.env.USERPROFILE || os.homedir() || '',
  'AppData',
  'LocalLow',
  'Ludeon Studios',
  'RimWorld by Ludeon Studios',
  'Logs',
);

/**
 * 游戏「布局」探测 —— **一处**决定 exe/进程名、日志落点、ModsConfig 的 `<version>` 戳。
 *
 * 判据优先级：`Version.txt` 的主次版本号 → 没有它就用「哪个 exe 存在」兜底
 * （`RimWorld.exe` 只出现在 1.7+）。别在别处再判一次版本：启动、status、stop、logs、
 * `live --source log`、`waitForBridge` 全部从这里拿结论。
 */
export function detectGameLayout(profile = 'vanilla', root = gameRoot()) {
  const image = detectGameImage(root);
  const versionText = readGameVersionText(root);
  const m = /^(\d+)\.(\d+)/.exec(versionText || '');
  const majorMinor = m ? `${m[1]}.${m[2]}` : null;
  // 1.7 起是 Waylith（.NET 10）宿主：日志落点、exe 名都跟 1.6 不同
  const waylith = majorMinor ? Number(m[1]) > 1 || Number(m[2]) >= 7 : image.image === 'RimWorld.exe';
  const logFile = waylith
    ? path.join(WAYLITH_LOG_DIR, 'Player.log')
    : path.join(profileDir(profile), 'Player.log');
  return { ...image, root, versionText, majorMinor, waylith, logFile };
}

export function profileDir(profile = 'vanilla') {
  return path.join(WS, 'profiles', profile);
}

/** 该 profile 的 `Player.log` 落在哪 —— 一律由 detectGameLayout 决定（1.6 在 profile 里，1.7 在默认存档目录） */
export function logPathFor(profile = 'vanilla') {
  return detectGameLayout(profile).logFile;
}

/**
 * 2026-10-17 多实例：记录**按 profile** 落盘，后启动的实例不再覆盖前一个。
 * 旧的全量文件 `runs/game-process.json` 不再读写（见 legacyProcessRecordPath）。
 */
export function processRecordPath(profile = 'vanilla') {
  return path.join(WS, 'runs', `game-process.${profile}.json`);
}

/** 改造前的单实例记录路径：只用来在 start 时提示一句，绝不删、不读它的内容 */
export function legacyProcessRecordPath() {
  return path.join(WS, 'runs', 'game-process.json');
}

export function profileLockPath(profile = 'vanilla') {
  return path.join(WS, 'runs', 'locks', `${profile}.lock`);
}

/**
 * 按 profile 的**启动锁**（③）—— 堵住 `startGame` 里「检查没有实例」到「写下记录」之间的竞态。
 *
 * 为什么需要它：两个会话同时 `start --profile X` 时，两边都在写完记录之前看到「没有实例在跑」，
 * 于是各起一个实例、抢同一批端口与同一份存档目录。2026-10-24 实测过这种并发（两个会话各起一局）。
 *
 * 锁的寿命只覆盖**启动临界区**（claim → 记录落盘），不是「实例活着就一直持有」：
 * 之后由记录 + PID 身份校验（`resolveBridge`/`gameStatus`）负责，锁再长就跟记录重复了。
 *
 * 拿着锁的进程死掉（PID 不再存在）⇒ 判定为陈旧锁、自动回收，不会被一次崩溃永久挡住。
 */
export async function claimProfileLock(profile = 'vanilla', { staleAfterMs = 10 * 60_000 } = {}) {
  const file = profileLockPath(profile);
  fs.mkdirSync(path.dirname(file), { recursive: true });
  const payload = {
    schema: 1,
    profile,
    pid: process.pid,
    startedAt: new Date().toISOString(),
    session: process.env.DSH_SESSION_ID ?? null,
    note: '启动临界区锁；实例跑起来之后由记录与 PID 身份校验负责，不是长期锁',
  };
  for (let attempt = 0; attempt < 2; attempt += 1) {
    try {
      const fd = fs.openSync(file, 'wx'); // 原子创建：两个进程只有一个能赢
      fs.writeFileSync(fd, JSON.stringify(payload, null, 2), 'utf8');
      fs.closeSync(fd);
      return { ok: true, file, lock: payload };
    } catch (err) {
      if (err.code !== 'EEXIST') throw err;
      let holder = null;
      try {
        holder = JSON.parse(fs.readFileSync(file, 'utf8'));
      } catch {
        holder = null;
      }
      const holderPid = Number(holder?.pid);
      const state = Number.isInteger(holderPid) ? probePid(holderPid).state : 'none';
      const ageMs = holder?.startedAt ? Date.now() - Date.parse(holder.startedAt) : Infinity;
      const stale = state !== 'alive' || !Number.isFinite(ageMs);
      if (stale) {
        warnOnce(
          `[警告] 回收陈旧的启动锁 ${file}（持有者 PID ${holder?.pid ?? '未知'}，状态 ${state}，` +
            `起始 ${holder?.startedAt ?? '未知'}）`,
        );
        fs.rmSync(file, { force: true });
        continue;
      }
      if (ageMs > staleAfterMs) {
        warnOnce(`[警告] 启动锁 ${file} 已存在 ${Math.round(ageMs / 1000)}s 但持有进程仍活着，按占用处理`);
      }
      const err2 = new Error(
        `profile「${profile}」正在被另一个进程启动：PID ${holderPid}` +
          `（起始 ${holder.startedAt ?? '未知'}${holder.session ? `，会话 ${holder.session}` : ''}）。\n` +
          `锁文件：${file}\n` +
          '等它启动完（或确认它已死：删掉锁文件、或用 `node src/cli.js stop --profile ' +
          `${profile}\`）后重试。`,
      );
      err2.code = 'EPROFILELOCKED';
      err2.holder = holder;
      throw err2;
    }
  }
  throw new Error(`拿不到 profile「${profile}」的启动锁：回收陈旧锁之后仍然冲突（${file}）`);
}

/** 释放启动锁；只删「自己持有的那把」，用完顺手清掉 `locks\` 里的空目录 */
export function releaseProfileLock(profile = 'vanilla') {
  const file = profileLockPath(profile);
  let holder = null;
  try {
    holder = JSON.parse(fs.readFileSync(file, 'utf8'));
  } catch {
    return { released: false, reason: '没有锁文件' };
  }
  if (Number(holder?.pid) !== process.pid) {
    return { released: false, reason: `锁属于 PID ${holder?.pid}，不是本进程，不动它` };
  }
  fs.rmSync(file, { force: true });
  return { released: true, file };
}

/**
 * 从 `<WS>/profiles/<profile>/Player.log` 反推 profile —— **只在 1.6 的私有日志布局下有效**。
 * 1.7 的日志是全机共用的默认存档目录（路径里没有 profile 名），所以那一路必须显式传 profile
 * （见 readBridgeInfo 的 `profile` 参数与 resolveBridge）。
 */
function profileFromLogPath(logFile) {
  if (!logFile) return 'vanilla';
  return path.basename(path.dirname(path.resolve(logFile)));
}

/**
 * 把 profile 的 `ModsConfig.xml` 里 `<version>` 戳成游戏当前的版本串（只改这一行）。
 *
 * 为什么必须做：1.7 启动时发现 `<version>` 与自己在跑的版本不一致，会
 * **把整个 mod 清单重置成只有 Core**（实测日志原文见 docs 里的报告）。测试 profile
 * 的清单是人工排好的，被重置等于白干，所以先在启动前把版本追平。
 *
 * 边界：文件不存在 / 没有 `<version>` / 已经一致 → 什么都不做，返回 null。
 */
export function stampModsConfigVersion(profile = 'vanilla', versionText = null) {
  if (!versionText) return null;
  const file = path.join(profileDir(profile), 'Config', 'ModsConfig.xml');
  let xml;
  try {
    xml = fs.readFileSync(file, 'utf8');
  } catch {
    return null;
  }
  const m = /<version>([^<]*)<\/version>/.exec(xml);
  if (!m || m[1] === versionText) return null;
  fs.writeFileSync(file, xml.replace(m[0], `<version>${versionText}</version>`), 'utf8');
  return { changed: true, from: m[1], to: versionText, file };
}

export function sleep(ms) {
  return new Promise((r) => setTimeout(r, ms));
}

/**
 * start 前的底座 mod 补齐（策略层；补齐本身在 `src/base-mods.js`）。
 *
 * 单独成函数是因为它要吞掉三种**不该让 start 失败**的情况：清单文件缺、JSON 写坏、
 * profile 的 ModsConfig 不可写。三种都只警告一行 —— 缺 mod 的后果由使用者判断，
 * 不该被一条辅助步骤卡死在命令行。
 *
 * 没有补齐时**什么都不打印**：每次 start 都刷一行「没变」是噪音，真变化反而被埋掉。
 */
function ensureBaseModsBeforeStart(profile, prof) {
  let loaded;
  try {
    loaded = loadBaseMods(WS);
  } catch (err) {
    console.warn(`[警告] 读不了 base-mods.json（${err.message}），本次跳过底座 mod 补齐`);
    return;
  }
  if (loaded.missingFile) {
    console.warn('[警告] 没有 base-mods.json，本次跳过底座 mod 补齐（按现有 ModsConfig 启动）');
    return;
  }
  let result;
  try {
    result = ensureBaseMods({ profileDir: prof, baseMods: loaded.packageIds });
  } catch (err) {
    console.warn(`[警告] 底座 mod 补齐失败（${err.message}），继续启动（ModsConfig 保持原样）`);
    return;
  }
  if (result.skipped) {
    console.warn(`[警告] 跳过底座 mod 补齐：${result.skipped}`);
    return;
  }
  if (result.added.length) {
    console.log(`底座 mod 自动补齐（profile ${profile}）：${result.added.map((id) => `+${id}`).join(', ')}`);
  }
}

/** 每次进程内只唠叨一次的告警（回退路径会反复被调用，重复告警会把 stderr 淹掉） */
const warnedOnce = new Set();
function warnOnce(msg) {
  if (warnedOnce.has(msg)) return;
  warnedOnce.add(msg);
  console.warn(msg);
}

/** 两个路径是否指同一个文件（大小写不敏感 + 绝对化；记录里的路径可能是相对写法） */
function samePath(a, b) {
  if (!a || !b) return false;
  return path.resolve(String(a)).toLowerCase() === path.resolve(String(b)).toLowerCase();
}

/** 哨兵 token：离线测试/假桥留下的记录，绝不能当成真实例（2026-10-24 那次 4 发打进假桥） */
const SENTINEL_TOKEN = /fake|offline|test|dummy|placeholder/i;

/**
 * 一条启动记录**可不可信**（④）。这是纯数据判据，不需要进程枚举，所以能用在同步路径上。
 *
 * 三条拒绝理由都来自实测事故：`pid=0`（有人为了离线测 `wait` 手写记录）、
 * 端口 `5288`（不在 5174..5183）、token `fake-token-for-offline-check`。
 * 逃生口：`RIMBRIDGE_ALLOW_FAKE_RECORD=1` 时只告警不拒绝（离线测函数用），默认必须拒绝。
 */
export function recordTrust(rec, label = '记录') {
  if (!rec || typeof rec !== 'object') return { ok: false, reason: `${label} 不是对象` };
  const pid = Number(rec.pid);
  if (!Number.isInteger(pid) || pid <= 0) return { ok: false, reason: `${label} 的 pid=${rec.pid} 不是正整数` };
  // 没有 bridge 段是合法的（1.6 自挑端口的那条路要靠日志），只有「写了但不能信」才拒绝
  const bridge = rec.bridge;
  if (!bridge) return { ok: true, reason: `${label} 通过基本校验（无桥接段）` };
  const port = Number(bridge.port);
  if (!Number.isInteger(port) || port < PORT_RANGE[0] || port > PORT_RANGE[1]) {
    return { ok: false, reason: `${label} 的桥接端口 ${bridge.port} 不在 ${PORT_RANGE[0]}-${PORT_RANGE[1]}` };
  }
  if (SENTINEL_TOKEN.test(String(bridge.token ?? ''))) {
    return { ok: false, reason: `${label} 的桥接 token 是哨兵值（看似离线测试留下的）` };
  }
  return { ok: true, reason: `${label} 通过基本校验` };
}

function acceptRecord(rec, label) {
  const trust = recordTrust(rec, label);
  if (trust.ok) return { rec, trust };
  if (process.env.RIMBRIDGE_ALLOW_FAKE_RECORD === '1') {
    warnOnce(`[警告] ${trust.reason}；因为设置了 RIMBRIDGE_ALLOW_FAKE_RECORD=1，仍然采用（只该用于离线测试）`);
    return { rec, trust: { ok: true, reason: `${trust.reason}（被环境变量放行）` } };
  }
  warnOnce(`[警告] 已忽略一条不可信的启动记录：${trust.reason}`);
  return { rec: null, trust };
}

/**
 * 找出「这次要用的那条记录」，并把**怎么找到的**一并返回，好让上层能说清判据来源。
 *
 * 顺序（2026-10-24 收紧）：给了 profile 就**只认这个 profile 的记录**，绝不跨 profile 猜；
 * 没给 profile 时才按「日志路径反推」→「logFile 匹配」两级兜底（1.7 的共用日志路径里没有 profile 名）。
 */
function resolveRecordFor({ logFile, profile }) {
  if (profile) {
    // readProcessRecord 已经过 recordTrust 过滤（不可信的返回 null 并告警），这里不再重复校验
    const rec = readProcessRecord(profile);
    return { rec, how: rec ? `profile「${profile}」的记录` : `profile「${profile}」没有可用记录（或记录不可信）` };
  }
  const guess = profileFromLogPath(logFile);
  const own = readProcessRecord(guess);
  if (own && samePath(own.logFile, logFile)) {
    return { rec: own, how: `按日志路径反推的 profile「${guess}」` };
  }
  const byLog = findRecordByLogFile(logFile);
  if (byLog) return { rec: byLog, how: `按 logFile 匹配到的 profile「${byLog.profile}」` };
  return { rec: null, how: '没有匹配的启动记录' };
}

/**
 * 日志锚点：启动那一刻记下「文件大小 + 末尾 256 字节的 sha1」。
 *
 * 为什么需要：1.7 的 Player.log 全机共用、**每次启动整份重写**（实测记录写 `logStartOffset: 320607`，
 * 而同一时段文件只有 ~127 KB）。只比「大小 < 偏移」不够 —— 重写后的文件完全可能比原偏移更大，
 * 那时从旧偏移往后读到的就是**别人的行**。拿锚点比一次末尾内容，就能确定「还是不是我当时那份」。
 */
export function logAnchorOf(buf) {
  const anchor = { size: buf.length, sha1: null };
  if (buf.length === 0) return anchor;
  const start = Math.max(0, buf.length - 256);
  anchor.sha1 = createHash('sha1').update(buf.subarray(start)).digest('hex');
  return anchor;
}

/** 当前文件内容是否仍与启动时的锚点一致（一致 ⇒ 还是我这一份，可以按偏移往后读） */
export function logAnchorHolds(buf, anchor) {
  if (!anchor || !Number.isFinite(Number(anchor.size))) return false;
  const size = Number(anchor.size);
  if (size < 0 || buf.length < size) return false; // 比启动时还短 ⇒ 必然被重写/截断
  if (size === 0) return true; // 启动时是空文件，任何追加都算我的
  if (!anchor.sha1) return false;
  const start = Math.max(0, size - 256);
  return createHash('sha1').update(buf.subarray(start, size)).digest('hex') === anchor.sha1;
}

/**
 * 桥接信息：端口 + 令牌。
 *
 * **优先级（2026-10-24 收口）**：
 *  1. `profile` 的记录 —— 有可信记录时它就是权威。1.7 的端口与令牌是我们通过
 *     `GABP_SERVER_PORT`/`GABP_TOKEN` 环境变量发下去的，记录里写的就是真相；
 *     再去共用日志里刮，只会刮到别的实例的行。
 *  2. 日志 —— 只在没有可信记录时用（1.6 自挑端口、或记录丢了）。且必须过日志锚点：
 *     文件已不是启动时那一份 ⇒ **不解析、直接放弃日志这条路**（以前会「读整份」⇒ 串台）。
 *  3. 记录里的预定值兜底（`source:'env'`）。
 *
 * 注意：这个函数是**同步**的，只做「解析 + 数据层校验」。要证明「这个端口真的属于我这一局」
 * 必须用异步的 `resolveBridge()`（它加 PID 身份校验）。
 */
export function readBridgeInfo(logFile, { profile = null, sinceOffset = null } = {}) {
  const { rec, how } = resolveRecordFor({ logFile, profile });

  // 有 profile 时记录就是权威（不再要求记录的 logFile 与当前布局一致：1.6→1.7 换过日志落点，
  // 同一条记录仍然指同一个实例，拿日志路径去卡它只会把有效记录判死）。
  if (rec?.bridge?.port && (profile || !logFile || samePath(rec.logFile, logFile))) {
    return {
      host: '127.0.0.1',
      port: Number(rec.bridge.port),
      token: rec.bridge.token,
      source: 'record',
      record: rec,
      recordHow: how,
    };
  }

  let txt = '';
  let skipReason = null;
  try {
    const buf = fs.readFileSync(logFile);
    const anchor = rec?.logAnchor;
    const from = Number(sinceOffset ?? rec?.logStartOffset ?? 0) || 0;
    if (anchor) {
      if (!logAnchorHolds(buf, anchor)) {
        skipReason = `日志已不是本实例启动时那一份（锚点 ${anchor.size}B 对不上，当前 ${buf.length}B）⇒ 跳过日志解析`;
      } else if (buf.length <= from) {
        txt = '';
      } else {
        txt = buf.subarray(from).toString('utf8');
      }
    } else if (buf.length < from) {
      skipReason = `日志只有 ${buf.length}B，小于本实例起点 ${from}B ⇒ 已被整份重写，跳过日志解析`;
    } else {
      txt = buf.subarray(from).toString('utf8');
    }
  } catch (err) {
    txt = '';
  }
  if (skipReason) warnOnce(`[警告] ${skipReason}`);

  if (txt) {
    // 只认最后一次出现的桥接行，避免读到归档/追加造成的旧记录
    const ports = [...txt.matchAll(/GABP server (?:running standalone on|connected to GABS on) port (\d+)/g)];
    const tokens = [...txt.matchAll(/Bridge token:\s*(\S+)/g)];
    if (ports.length && tokens.length) {
      return {
        host: '127.0.0.1',
        port: Number(ports[ports.length - 1][1]),
        token: tokens[tokens.length - 1][1],
        source: 'log',
        record: rec,
        recordHow: how,
      };
    }
  }

  // 回退：启动时预定的端口与令牌（按 profile 的记录取）
  if (rec?.logFile && samePath(rec.logFile, logFile) && rec?.bridge?.port) {
    return {
      host: '127.0.0.1',
      port: Number(rec.bridge.port),
      token: rec.bridge.token,
      source: 'env',
      record: rec,
      recordHow: how,
    };
  }
  return null;
}

/**
 * 按「记录的 logFile 等于这个路径」找 profile 记录 —— 1.7 的共用日志路径里没有 profile 名，
 * 靠 profileFromLogPath 反推不出来。
 *
 * 2026-10-24 收紧：**不再**只按 `startedAt` 取最新（那正是串台的根因 —— 假记录/别人的记录更新更晚），
 * 先过 `recordTrust` 数据校验，再按**记录里的 PID 是否活着**排序，最后才用 `startedAt` 决胜负。
 * 调用方要「确定是我的」仍必须走 `resolveBridge()`。
 */
export function findRecordByLogFile(logFile) {
  if (!logFile) return null;
  const dir = path.join(WS, 'runs');
  let names;
  try {
    names = fs.readdirSync(dir);
  } catch {
    return null;
  }
  const candidates = [];
  for (const name of names) {
    if (!/^game-process\..+\.json$/.test(name)) continue;
    let raw;
    try {
      raw = JSON.parse(fs.readFileSync(path.join(dir, name), 'utf8'));
    } catch {
      continue;
    }
    if (!raw?.logFile || !samePath(raw.logFile, logFile)) continue;
    const { rec } = acceptRecord(raw, `runs\\${name}`);
    if (!rec) continue;
    candidates.push({ rec, alive: isAlive(rec.pid) });
  }
  if (!candidates.length) return null;
  candidates.sort((a, b) => {
    if (a.alive !== b.alive) return a.alive ? -1 : 1; // 活着的优先
    return String(b.rec.startedAt ?? '').localeCompare(String(a.rec.startedAt ?? ''));
  });
  if (!candidates[0].alive) {
    warnOnce(
      `[警告] 按 logFile 匹配到的记录（profile「${candidates[0].rec.profile}」）PID ${candidates[0].rec.pid} 已不在运行：` +
        '这个端口/令牌可能已经作废，不要据此断言实例可用',
    );
  }
  return candidates[0].rec;
}

/**
 * **权威**解析：这个 profile 现在该连哪个端口，以及凭什么这么认为（②）。
 *
 * 四道检查，任一不过就 `ok:false` 并给出人能读懂的理由 —— 绝不静默连一个没验过的端口：
 *  1. 记录可信（`recordTrust`：pid 正整数、端口在 5174..5183、token 不是哨兵值）；
 *  2. PID 身份：那个 PID 真的是本仓库 `profiles\<profile>` 的实例（`verifyInstance`，含命令行比对）；
 *  3. 端口一致：解析出的端口 == 记录里的端口（不等说明解析跑到别人记录上去了）；
 *  4. token 一致：解析出的 token == 记录里的 token（不等 ⇒ 那是别人的桥接行）。
 *
 * 为什么不在这一层做 TCP 握手：token 只有本实例知道，握手由调用方（`connectGabp`）做，
 * 失败即报错；这里只负责保证「拿去握手的地址一定是我自己的」。
 */
export async function resolveBridge(profile = 'vanilla') {
  const logFile = logPathFor(profile);
  const record = readProcessRecord(profile);
  const info = readBridgeInfo(logFile, { profile });
  const evidence = [];
  const fail = (reason) => ({ ok: false, profile, info, record, reason, evidence });

  if (!record) {
    return fail(`没有 profile「${profile}」的启动记录（${processRecordPath(profile)} 不存在或不可信）`);
  }
  const trust = recordTrust(record, `profile「${profile}」的记录`);
  if (!trust.ok) return fail(trust.reason);
  evidence.push(trust.reason);
  // 记录内容里的 profile 必须与文件名一致：文件是 `game-process.<profile>.json`，两者不一致
  // 说明这条记录是**被复制/搬动**过来的（离线测试、手工排障都干过这事），此时端口/token 归属不可信。
  if (record.profile && record.profile !== profile) {
    return fail(`记录里的 profile 字段是「${record.profile}」，与文件名要求的「${profile}」不一致（记录被复制过？）`);
  }
  evidence.push(`记录自述 profile「${record.profile ?? '(未写)'}」与文件名一致`);

  const identity = await verifyInstance(record.pid, {
    processesDir: path.join(WS, 'profiles'),
    expectedProfile: profile,
  });
  evidence.push(identity.reason);
  if (!identity.ok) return fail(identity.reason);

  if (!info) return fail('日志与记录里都没有可用的桥接端口/令牌');
  if (Number(info.port) !== Number(record.bridge.port)) {
    return fail(`解析出的端口 ${info.port} 与记录里的 ${record.bridge.port} 不一致（解析跑到别人的记录上了）`);
  }
  evidence.push(`端口 ${info.port} 与记录一致`);
  if (info.token !== record.bridge.token) {
    return fail(`解析出的 token 与记录不一致（来源 ${info.source}：那是别的实例的桥接行）`);
  }
  evidence.push(`token 与记录一致（来源 ${info.source}）`);
  return { ok: true, profile, info, record, reason: `已确认 ${info.host}:${info.port} 属于 profile「${profile}」`, evidence };
}

export function listGameProcesses() {
  return tryListProcessesByName(detectGameImage().image);
}

/**
 * 游戏进程枚举：先 tasklist，**不可用时用 Get-Process 兜底**。
 * 2026-10-13 新增兜底 —— 见 proc.js 的 listProcessesByPowerShell 注释。
 * 结果里的 `via` 说明这份清单是哪儿来的，调用方（和读报告的人）能看到判据来源。
 * 注意：和 tasklist 一样，这份清单**不区分 profile**（游戏进程看不出是哪个存档目录）。
 */
export async function listGameProcessesWithFallback() {
  const { image } = detectGameImage();
  const primary = await listGameProcesses();
  if (primary.known) return { ...primary, via: 'tasklist' };
  try {
    const processes = await listProcessesByPowerShell(image);
    return { known: true, processes, via: 'Get-Process（tasklist 被拒时的兜底）' };
  } catch (err) {
    return {
      known: false,
      processes: [],
      via: 'none',
      reason: `${primary.reason}；兜底 Get-Process 也不可用：${err.message}`,
    };
  }
}

/**
 * 综合判断游戏是否在跑。
 * 沙箱下 tasklist 会被拒绝，所以以「我们记录的 PID 是否还活着」为主，
 * 进程枚举只作为补充，并且明确标注它是否可用。
 *
 * 2026-10-13 修改：新增 `state` 三态字段。原因是同一天发现的**假阴性事故**——
 * `cli.js status` 只取进程枚举、且写错了取值方式，在沙箱下稳定输出
 * `gameRunning: false`，而同一时刻 `isAlive(29908)`、`Get-Process`、GABP 握手
 * 三条证据都说明游戏在跑；有人据此写了「游戏未运行」的结论，导致一次无谓重启。
 * `state` 把「确认在跑 / 确认没跑 / 判不了」三者分开：
 *   - 'running'     : 有**正向**证据（本 profile 记录的 PID 存活）
 *   - 'not-running' : 有**排他**证据（记录里的 PID 不存活；或没有记录且枚举可用）
 *   - 'unknown'     : 只有「查不到」这一条（枚举被拒 + 没有记录）
 * 旧字段 `running` / `recordedAlive` / `enumeration` 原样保留。
 *
 * 2026-10-17 多实例：`running` 只表示**这个 profile 的实例**在跑。枚举到的其它
 * RimWorld 进程可能是用户自己在玩的那一局，**不参与判定**（只列进 otherGamePids
 * 供报告），否则用户一开游戏就会让 start 拒绝启动、让 status 报「在跑」。
 */
export async function gameStatus(profile = 'vanilla') {
  const record = await readProcessRecord(profile);
  // 2026-10-24：判据从「PID 还活着」改成「那个 PID 就是本 profile 的实例」。
  // 旧判据的两个坑都是实测过的：PID 被别的受保护进程复用后 `process.kill` 报 EPERM，
  // 旧 `isAlive` 把 EPERM 当活着 ⇒ 陈旧记录一直挡 `start`；反过来删数据也不对。
  // 现在用命令行里的 `-savedatafolder=…\profiles\<profile>` 认人，认不出就不算在跑。
  const identity = record?.pid
    ? await verifyInstance(record.pid, { processesDir: path.join(WS, 'profiles'), expectedProfile: profile })
    : null;
  const recordedAlive = Boolean(identity?.ok);
  // 2026-10-13：枚举走「tasklist → Get-Process 兜底」，让"查不到"尽量变成"查得到"，
  // 剩下的才叫 unknown。兜底只在 tasklist 被拒时才多花那 0.2~1s。
  const enumeration = await listGameProcessesWithFallback();
  const via = enumeration.via ?? 'tasklist';
  const others = enumeration.processes.filter((p) => p.pid !== record?.pid);
  const running = recordedAlive;
  const state = running ? 'running' : record || enumeration.known ? 'not-running' : 'unknown';
  return {
    profile,
    running,
    state,
    // 判据清单（给人看的，不是给机器判的）：说明 state 是从哪条证据推出来的
    evidence: [
      ...(record
        ? [
            recordedAlive
              ? `profile「${profile}」记录的 PID ${record.pid} 已确认是本实例（${identity.reason}）`
              : `profile「${profile}」记录里的 PID ${record.pid} 不是本 profile 的运行实例（${identity?.reason ?? '无记录' }）`,
          ]
        : [`没有 profile「${profile}」的启动记录（${processRecordPath(profile)} 不存在）`]),
      ...(enumeration.known
        ? [
            others.length
              ? `${via} 另枚举到 ${others.length} 个其它 RimWorld 进程（PID ${others.map((p) => p.pid).join(', ')}）—— 可能是你自己的游戏，不参与本 profile 的判定`
              : `${via} 可用，且没有其它 RimWorld 进程`,
          ]
        : [`进程枚举不可用：${enumeration.reason}`]),
      ...(state === 'unknown'
        ? ['没有任何可用的正向证据，也无法枚举进程 —— 判据不足，不能结论「没在跑」']
        : []),
    ],
    recordedPid: record?.pid ?? null,
    recordedAlive,
    otherGamePids: others.map((p) => p.pid),
    enumeration,
    record,
  };
}

/** 桥接探测的默认超时：status 是交互命令，绝不能挂住（实测握手 20ms 级，1.5s 足够宽松） */
export const BRIDGE_PROBE_TIMEOUT_MS = 1500;

/**
 * 有界超时地探测桥接是否可达 —— 2026-10-13 新增。
 *
 * 为什么要它：桥接跑在**游戏进程内部**，能完成 `session/hello` 握手本身就是
 * 「游戏活着」的独立证据，而且这条证据**不依赖 tasklist**（沙箱拒绝的是 tasklist，
 * 不是 TCP）。所以它和「记录的 PID 是否存活」是两条不同来源的正向证据。
 *
 * 边界（明确写清，别拿它当万能判据）：
 *   - `available:false` = 我们连「桥接应该在哪」都不知道（日志和记录里都没有端口），
 *     **不能**据此说游戏没跑 —— 刚启动还没写日志、或日志被轮转都会这样。
 *   - `available:true, reachable:false` = 知道地址但握手失败。这是**弱否定**证据：
 *     游戏可能没跑，也可能只是桥接还没起来 / 被某个长调用挂住。判据不足时不要下结论。
 *   - 超时是**有界**的（默认 1.5s），任何情况下都会返回，不会挂住调用方。
 */
export async function probeBridge(profile = 'vanilla', { timeoutMs = BRIDGE_PROBE_TIMEOUT_MS } = {}) {
  // 解析层收 profile：1.7 的共用日志里没有 profile 名，不传就只能猜（→ 串台）
  const info = readBridgeInfo(logPathFor(profile), { profile });
  if (!info) {
    return {
      available: false,
      reachable: false,
      reason: '日志与启动记录里都没有桥接端口/令牌（游戏可能还没启动到写日志那一步）',
      probeTimeoutMs: timeoutMs,
    };
  }
  try {
    const { client, welcome } = await connectGabp(info, { timeoutMs });
    try {
      return {
        available: true,
        reachable: true,
        host: info.host,
        port: info.port,
        source: info.source,
        app: welcome?.app ?? null,
        agentId: welcome?.agentId ?? null,
        probeTimeoutMs: timeoutMs,
      };
    } finally {
      client.close();
    }
  } catch (err) {
    return {
      available: true,
      reachable: false,
      host: info.host,
      port: info.port,
      source: info.source,
      reason: err.message,
      probeTimeoutMs: timeoutMs,
    };
  }
}

/**
 * 读某个 profile 的记录（同步读：readBridgeInfo 是同步函数，要在这里直接取）。
 * 调用方照旧可以 `await` 它。
 */
export function readProcessRecord(profile = 'vanilla') {
  const file = processRecordPath(profile);
  // 「文件不存在」是正常状态（这个 profile 从没启动过），不要报警；只有「文件在但内容不可信」才告警。
  if (!fs.existsSync(file)) return null;
  let raw;
  try {
    raw = JSON.parse(fs.readFileSync(file, 'utf8'));
  } catch {
    warnOnce(`[警告] 已忽略一条不可信的启动记录：${file} 不是合法 JSON`);
    return null;
  }
  const { rec } = acceptRecord(raw, `profile「${profile}」的记录`);
  return rec;
}

/** 端口区间：多实例时每个 profile 各拿一个（5174..5183） */
export const PORT_RANGE = [5174, 5183];

/** 在 127.0.0.1 上试 listen 后立刻 close，判断端口是否空闲（纯 Node，不依赖 netstat 等外部命令） */
export function isPortFree(port) {
  return new Promise((resolve) => {
    const srv = net.createServer();
    srv.once('error', () => resolve(false));
    srv.once('listening', () => srv.close(() => resolve(true)));
    srv.listen({ host: '127.0.0.1', port, exclusive: true });
  });
}

/**
 * 挑一个空闲端口：先试 preferred（`--port` 没给时用 GABP_SERVER_PORT），
 * 再按 PORT_RANGE 顺序找第一个空闲的。多个实例因此各拿一个端口，不会都挤 5174。
 */
export async function pickFreePort(preferred = null) {
  const [lo, hi] = PORT_RANGE;
  const candidates = [];
  if (preferred) candidates.push(Number(preferred));
  for (let p = lo; p <= hi; p++) if (!candidates.includes(p)) candidates.push(p);
  for (const p of candidates) if (await isPortFree(p)) return p;
  throw new Error(
    `端口 ${lo}..${hi} 全部被占用，且没有显式指定 --port；请用 --port <空闲端口> 启动。`,
  );
}

/**
 * 启动一个隔离的 RimWorld 测试实例。
 * @param {object} opts
 * @param {string}  [opts.profile='vanilla']  profiles/<name> 作为存档目录（也是记录/端口的归属）
 * @param {number}  [opts.port]               显式指定桥接端口（不传则从 5174..5183 挑空闲的）
 * @param {boolean} [opts.visible=true]       是否显示窗口
 * @param {number}  [opts.width]              窗口宽（不传则用 profile 的 prefs.json / 默认 1600）
 * @param {number}  [opts.height]             窗口高
 * @param {boolean} [opts.fullscreen]         是否全屏（默认 false —— 测试实例不该占满屏幕）
 * @param {boolean} [opts.keepLog=false]      默认把旧日志改名归档，避免误读上次的端口
 * @param {object}  [opts.prefs]              额外要强制的 Prefs 键
 */
export async function startGame({
  profile = 'vanilla',
  port,
  visible = true,
  width,
  height,
  fullscreen,
  monitor,
  keepLog = false,
  extraArgs = [],
  env = {},
  doorstop = true,
  prefs: extraPrefs = {},
} = {}) {
  const root = gameRoot();
  const layout = detectGameLayout(profile, root);
  const { exe, found, searched } = layout;
  if (!found) {
    throw new Error(
      `找不到 RimWorld 可执行文件。已按顺序查找：\n${searched.map((p) => `  - ${p}`).join('\n')}\n` +
        `请设置环境变量 RIMWORLD_ROOT 指向游戏安装目录（当前解析为 ${root}）。`,
    );
  }

  // 旧的全量记录不再读；只提示一句，文件留给用户自己处理。
  const legacy = legacyProcessRecordPath();
  if (fs.existsSync(legacy)) {
    console.warn(
      `[提示] 发现旧的单实例记录 ${legacy}，已忽略（现在按 profile 记在 ${processRecordPath(profile)}）；文件保留未删。`,
    );
  }

  // ③ 先拿锁再检查：检查与写记录之间必须互斥，否则两个会话会各起一局。
  await claimProfileLock(profile);
  // 任何一条失败退出都要把锁还回去，否则下一次 start 会被自己的陈旧锁挡住。
  const abortStart = (err) => {
    releaseProfileLock(profile);
    return err;
  };

  const status = await gameStatus(profile);
  if (status.recordedAlive) {
    throw abortStart(
      new Error(
        `profile「${profile}」已有测试实例在运行（PID ${status.recordedPid}，记录：${processRecordPath(profile)}）。\n` +
          `先执行 \`node src/cli.js stop --profile ${profile}\`，或用 --profile 换一个 profile。`,
      ),
    );
  }
  // 2026-10-17：不再因为「机器上有别的 RimWorld」而拒绝启动 —— 用户可能正开着自己的那一局。
  // 存档靠 -savedatafolder 隔离、端口靠 pickFreePort 避让，两边互不影响。
  if (status.otherGamePids.length) {
    console.warn(
      `[警告] 检测到其它 RimWorld 进程 PID ${status.otherGamePids.join(', ')}（可能是你自己的游戏）；并行启动，互不影响存档`,
    );
  }

  const prof = profileDir(profile);
  fs.mkdirSync(path.join(prof, 'Config'), { recursive: true });
  fs.mkdirSync(path.join(prof, 'Saves'), { recursive: true });
  fs.mkdirSync(path.join(WS, 'runs'), { recursive: true });

  // 写 Prefs.xml：RimWorld 会用这里的分辨率覆盖 Unity 命令行参数，
  // 而强杀导致 Prefs 从不落盘，所以必须在每次启动前铺好。
  const explicitPrefs = { ...extraPrefs };
  if (width !== undefined) explicitPrefs.screenWidth = width;
  if (height !== undefined) explicitPrefs.screenHeight = height;
  if (fullscreen !== undefined) explicitPrefs.fullscreen = fullscreen;
  if (monitor !== undefined) explicitPrefs.monitor = monitor;
  const effectivePrefs = resolvePrefs(prof, explicitPrefs);
  const prefsResult = ensurePrefs(prof, effectivePrefs);

  // 1.6 → 1.7 换版本时游戏会把**整个** ModsConfig 重置成只有 Core（实测日志：
  // "Mods config data is from version 1.6.4871 rev591 while we are running 1.7.4985 rev1269. Resetting."），
  // 14 项清单会被抹掉。所以启动前把 `<version>` 戳成游戏自己的版本串 —— 只改这一行，
  // mod 清单、knownExpansions 一个字节都不动。版本一致时什么都不做。
  const modsConfigStamp = stampModsConfigVersion(profile, layout.versionText);

  // 底座 mod 按 base-mods.json 补齐（只加不删、幂等），与上面戳版本互不影响：
  // 一个只改 `<version>`、一个只在 `</activeMods>` 前插行。
  // 这里绝不能挡住启动：清单写了错、profile 的 Config 不可写，都只警告 ——
  // 用户要的是「起一局看看」，不是被一条辅助步骤卡死在命令行。
  ensureBaseModsBeforeStart(profile, prof);

  const logFile = layout.logFile;
  // 只归档**profile 目录内**的日志。1.7 的日志是全机共用的默认存档目录那份，
  // 那可能是用户自己那一局的日志，绝不能改名/移动。
  const logInsideProfile = path.resolve(logFile).toLowerCase().startsWith(path.resolve(prof).toLowerCase() + path.sep);
  if (!keepLog && logInsideProfile && fs.existsSync(logFile)) {
    const stamp = new Date().toISOString().replace(/[:.]/g, '-');
    fs.renameSync(logFile, path.join(prof, `Player-${stamp}.log`));
  }
  // 共用日志不能归档，就记下启动那一刻的大小：之后只认这次启动新增的内容，
  // 免得把上一次实例的桥接端口当成这一次的（见 readBridgeInfo 的 sinceOffset）。
  let logStartOffset = 0;
  let logAnchor = null;
  try {
    const buf = fs.readFileSync(logFile);
    logStartOffset = buf.length;
    logAnchor = logAnchorOf(buf);
  } catch {
    logStartOffset = 0;
    logAnchor = { size: 0, sha1: null };
  }

  // 预先定好端口和令牌，通过环境变量交给桥接。
  // 端口按 profile 避让：显式 --port 优先，否则在 5174..5183 里挑第一个空闲的。
  let chosenPort;
  let portSource;
  const explicitPort = port === undefined || port === null || port === '' ? null : Number(port);
  if (explicitPort !== null) {
    chosenPort = explicitPort;
    portSource = 'explicit';
    if (!(await isPortFree(chosenPort))) {
      console.warn(`[警告] 显式指定的端口 ${chosenPort} 已被占用，仍按 --port 使用（桥接可能连不上）。`);
    }
  } else {
    const preferred = process.env.GABP_SERVER_PORT ? Number(process.env.GABP_SERVER_PORT) : null;
    chosenPort = await pickFreePort(preferred);
    portSource = preferred === chosenPort ? 'env' : 'auto';
  }
  const bridgeEnv = {
    port: chosenPort,
    token: randomBytes(16).toString('hex'),
  };

  const args = [
    `-savedatafolder=${prof}`,
    '-logFile',
    logFile,
    '-screen-fullscreen',
    effectivePrefs.fullscreen ? '1' : '0',
    '-screen-width',
    String(effectivePrefs.screenWidth),
    '-screen-height',
    String(effectivePrefs.screenHeight),
    // 启动到第几个显示器（Unity 参数）。1=主屏，2=副屏。
    // 注意 Unity 的编号是 1-based，和 Display.displays 的 0-based 不同。
    ...(effectivePrefs.monitor ? ['-monitor', String(effectivePrefs.monitor)] : []),
    // Unity Doorstop v4 的命令行开关。Rimworld-Doorstop（Brrainz）会 patch
    // ModAssemblyHandler.ReloadAll，其 RegisterTypes 会提前解析自定义特性，
    // 当 RimBridgeServer.Core.dll 排在 RimBridgeServer.Extensions.Abstractions.dll
    // 之前加载时就会抛 TypeLoadException，导致整个桥接 mod 加载失败。
    // 自动化测试要的是确定性 + 不被热重载改写 Mods 下的 DLL，所以默认关掉它。
    ...(doorstop ? [] : ['--doorstop-enabled', 'false']),
    ...extraArgs,
  ];

  let child;
  try {
    child = spawnDetached(exe, args, {
      cwd: root,
      hideWindow: !visible,
      env: {
        ...process.env,
        // 双保险：Doorstop/UnityDoorstop 也会读这个环境变量
        ...(doorstop ? {} : { DOORSTOP_ENABLED: '0' }),
        // 用环境变量把端口和令牌"告诉"桥接，别让对方自己挑再让我们去日志里刮
        // （GABS 就是这么做的）。若 RimBridgeServer 不认，会退回 standalone 模式并写日志，
        // waitForBridge 两种都能接。
        GABP_SERVER_PORT: String(bridgeEnv.port),
        GABP_TOKEN: bridgeEnv.token,
        GABS_GAME_ID: `dsh-rimworld-${profile}`,
        // 游戏侧事件通道（mods/DSH_GameLink）的落点：给它一个绝对目录，mod 就写 <dir>\<profile>.ndjson。
        // DSH 侧的 `node src/cli.js live` 读同一个目录（见 src/live.js 的 resolveEventFile）。
        DSH_LIVE_DIR: path.join(WS, 'runs', 'live'),
        ...env,
      },
    });
  } catch (err) {
    throw abortStart(err);
  }

  const recordPath = processRecordPath(profile);
  // 记录文件：**唯一的写入口**就是这里（④）。schema/source/writer 三个字段让「这条记录是谁写的」
  // 可查 —— 以前任何人都能往 runs\ 丢一条 pid=0 的记录，读的一侧无从分辨。
  const record = {
    schema: 2,
    source: 'launcher.startGame',
    writer: 'src/launcher.js',
    pid: child.pid,
    startedAt: new Date().toISOString(),
    profile,
    profileDir: prof,
    logFile,
    // 日志是 profile 私有的，还是 1.7 那份全机共用的？共用的只从 logStartOffset 往后读。
    logStartOffset,
    logShared: !logInsideProfile,
    // ① 日志锚点：启动那一刻的「大小 + 末尾 256 字节 sha1」。读的时候对不上就说明这份共用日志
    // 已被别的实例整份重写，此时**不能**按偏移往后读（那读到的是别人的行）。
    logAnchor,
    // 布局探测的结论（读报告的人能看到判据来源，不用再猜版本）
    gameVersion: layout.versionText,
    gameMajorMinor: layout.majorMinor,
    gameImage: layout.image,
    waylith: layout.waylith,
    modsConfigStamp,
    recordPath,
    exe,
    args,
    doorstop,
    bridge: bridgeEnv,
    portSource,
    prefsFile: prefsResult.path,
    window: {
      fullscreen: Boolean(effectivePrefs.fullscreen),
      width: effectivePrefs.screenWidth,
      height: effectivePrefs.screenHeight,
      monitor: effectivePrefs.monitor ?? null,
    },
  };
  fs.writeFileSync(recordPath, JSON.stringify(record, null, 2), 'utf8');
  // 记录已落盘 = 启动临界区结束，锁的使命完成（之后由记录 + PID 身份校验负责）。
  releaseProfileLock(profile);
  return record;
}

/**
 * 轮询日志，等桥接就绪。
 * @returns {Promise<{host:string,port:number,token:string,waitedMs:number}>}
 */
export async function waitForBridge({
  profile = 'vanilla',
  timeoutMs = 240_000,
  pollMs = 500,
  onTick,
  pid,
  expected,
} = {}) {
  const logFile = logPathFor(profile);
  const record = pid ? null : await readProcessRecord(profile);
  const targetPid = pid ?? record?.pid;
  // 启动时我们通过 GABP_SERVER_PORT/GABP_TOKEN 预定了端口和令牌。
  // 桥接可能认（那就没有 standalone 日志行），也可能不认（那就会写日志）。
  // 两条路都接住，避免任何一条失效就永远等下去。
  const expectHost = expected ?? record?.bridge ?? null;
  const started = Date.now();
  for (;;) {
    const info = readBridgeInfo(logFile, { profile });

    // 日志里真的有 standalone 行 → 桥接自己在报，直接采信
    if (info && info.source === 'log') {
      return { ...info, waitedMs: Date.now() - started };
    }

    // 否则是"启动时预定的端口/令牌"：必须真的能连上才算就绪，
    // 不然会在桥接还没起来时就误报 ready。
    const candidate = info ?? (expectHost ? { host: '127.0.0.1', port: Number(expectHost.port), token: expectHost.token, source: 'env' } : null);
    if (candidate?.port && (await canConnect({ host: '127.0.0.1', port: candidate.port }, 700))) {
      return { ...candidate, waitedMs: Date.now() - started };
    }

    // 用我们记录的 PID 判断存活 —— 沙箱下 tasklist 不可用，这是唯一可靠的办法。
    // 只报「不存在」；EPERM/unknown 不能当成「已退出」（PID 可能已被别的进程占用，
    // 那时游戏确实死了，但结论要说清是「不可控」而不是「已退出」）。
    const probe = targetPid ? probePid(targetPid) : null;
    if (probe && probe.state === 'none') {
      throw new Error(
        `游戏进程 ${targetPid} 已退出，但桥接始终没出现。日志尾部：\n${tailFile(logFile, 6000) || '(日志为空)'}`,
      );
    }
    if (probe && probe.state === 'foreign') {
      throw new Error(
        `PID ${targetPid} 已被别的进程占用（EPERM，拿不到权限）：本实例已经不在，` +
          `但那个 PID 现在属于别人，不要去 stop 它。日志尾部：\n${tailFile(logFile, 6000) || '(日志为空)'}`,
      );
    }
    if (Date.now() - started > timeoutMs) {
      throw new Error(
        `等待桥接超过 ${timeoutMs}ms。日志尾部：\n${tailFile(logFile, 12_000) || '(日志为空)'}`,
      );
    }
    onTick?.(Date.now() - started);
    await sleep(pollMs);
  }
}

export function tailFile(file, maxBytes = 8000) {
  try {
    const buf = fs.readFileSync(file);
    return buf.subarray(Math.max(0, buf.length - maxBytes)).toString('utf8');
  } catch {
    return '';
  }
}

/** 探测某个端口上有没有人在监听（用于确认桥接真的起来了） */
export function canConnect({ host = '127.0.0.1', port }, timeoutMs = 1000) {
  return new Promise((resolve) => {
    const sock = net.connect({ host, port });
    const done = (ok) => {
      sock.destroy();
      resolve(ok);
    };
    sock.setTimeout(timeoutMs, () => done(false));
    sock.once('error', () => done(false));
    sock.once('connect', () => done(true));
  });
}

/**
 * 关闭测试实例：先发 WM_CLOSE（让游戏正常退出），超时再强杀。
 *
 * 2026-10-17 多实例：默认**只杀本 profile 记录里的那个 PID**，绝不枚举全机 RimWorld
 * —— 用户自己也开着游戏时，旧行为会把那一局一起关掉。
 * `force: true` 是唯一的破坏性出口：枚举全机 RimWorld 并强杀，且**先打印要杀的 PID**。
 */
export async function stopGame({ profile = 'vanilla', force = false, timeoutMs = 20_000 } = {}) {
  const record = await readProcessRecord(profile);
  // ⑤ 杀之前必须确认「这个 PID 现在还是我们的实例」：PID 会被复用，taskkill 不带 /F 也会
  // 给任意进程发 WM_CLOSE。2026-10-24 之前这里只看 isAlive，而 isAlive 把 EPERM 当活着。
  const verifyForKill = async (pid) =>
    verifyInstance(pid, { processesDir: path.join(WS, 'profiles'), expectedProfile: force ? undefined : profile });
  let list;
  const skipped = [];
  if (force) {
    const enumeration = await listGameProcessesWithFallback();
    const pids = new Set(enumeration.processes.map((p) => p.pid));
    if (record?.pid && (await verifyForKill(record.pid)).ok) pids.add(record.pid);
    list = [...pids];
    if (list.length) {
      console.warn(
        `[stop --force] 破坏性操作：将强制结束以下 RimWorld 进程（含你自己那一局）：PID ${list.join(', ')}` +
          `${enumeration.known ? '' : `（进程枚举不可用：${enumeration.reason}，只能靠记录 PID）`}`,
      );
    }
  } else {
    if (record?.pid) {
      const check = await verifyForKill(record.pid);
      if (check.ok) list = [record.pid];
      else {
        skipped.push({ pid: record.pid, reason: check.reason });
        list = [];
      }
    } else {
      list = [];
    }
  }

  if (!list.length) {
    return {
      profile,
      stopped: [],
      alreadyStopped: true,
      forced: false,
      forceMode: force,
      recordedPid: record?.pid ?? null,
      skipped,
    };
  }

  for (const pid of list) {
    // taskkill 不带 /F 会给主窗口发 WM_CLOSE，属于正常退出；沙箱拒绝就忽略
    await capture('taskkill.exe', ['/PID', String(pid)], { timeoutMs: 15_000 }).catch(() => {});
  }

  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (!list.some((pid) => probePid(pid).state !== 'none')) {
      return { profile, stopped: list, alreadyStopped: false, forced: false, forceMode: force, skipped };
    }
    await sleep(500);
  }

  for (const pid of list) {
    const check = await verifyForKill(pid);
    if (!check.ok) {
      // 等待期间 PID 可能已被别人复用 —— 此时强杀 = 杀无关进程
      skipped.push({ pid, reason: `放弃强杀：${check.reason}` });
      continue;
    }
    killForce(pid);
    await capture('taskkill.exe', ['/PID', String(pid), '/T', '/F'], { timeoutMs: 15_000 }).catch(
      () => {},
    );
  }
  await sleep(500);
  return {
    profile,
    stopped: list,
    alreadyStopped: false,
    forced: true,
    forceMode: force,
    stillAlive: list.filter((pid) => probePid(pid).state !== 'none'),
    skipped,
  };
}

/** 记录里那个 PID 是否还活着（默认 vanilla，多实例时传 profile） */
export async function recordedProcessAlive(profile = 'vanilla') {
  const status = await gameStatus(profile);
  return status.recordedAlive;
}
