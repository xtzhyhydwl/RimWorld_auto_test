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
import path from 'node:path';
import net from 'node:net';
import { randomBytes } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import {
  capture,
  spawnDetached,
  isAlive,
  killForce,
  killGraceful,
  tryListProcessesByName,
  listProcessesByPowerShell,
} from './proc.js';
import { connectGabp } from './gabp.js';
import { ensurePrefs, resolvePrefs, prefsPath } from './prefs.js';

export const WS = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');

export const GAME_IMAGE = 'RimWorldWin64.exe';
export const DEFAULT_GAME_ROOT = 'D:\\SteamLibrary\\steamapps\\common\\RimWorld';

export function gameRoot() {
  return process.env.RIMWORLD_ROOT || DEFAULT_GAME_ROOT;
}

export function profileDir(profile = 'vanilla') {
  return path.join(WS, 'profiles', profile);
}

export function logPathFor(profile = 'vanilla') {
  return path.join(profileDir(profile), 'Player.log');
}

export function processRecordPath() {
  return path.join(WS, 'runs', 'game-process.json');
}

export function sleep(ms) {
  return new Promise((r) => setTimeout(r, ms));
}

/**
 * 桥接信息：端口 + 令牌。
 *
 * 两个来源，按优先级：
 *  1. 游戏日志里的 `[RimBridge] GABP server running standalone on port N` / `Bridge token: X`
 *     —— 桥接自己挑了端口时会写这两行
 *  2. 我们启动时通过 GABP_SERVER_PORT / GABP_TOKEN 环境变量预定的值（记在 game-process.json）
 *     —— 桥接认这两个变量时就不会写 standalone 日志行
 *
 * 两条路都要接住：只认日志会在"桥接接受环境变量"时永远等不到。
 */
export function readBridgeInfo(logFile) {
  let txt;
  try {
    txt = fs.readFileSync(logFile, 'utf8');
  } catch {
    txt = '';
  }
  if (txt) {
    // 只认最后一次出现的桥接行，避免读到归档/追加造成的旧记录
    const ports = [...txt.matchAll(/GABP server running standalone on port (\d+)/g)];
    const tokens = [...txt.matchAll(/Bridge token:\s*(\S+)/g)];
    if (ports.length && tokens.length) {
      return {
        host: '127.0.0.1',
        port: Number(ports[ports.length - 1][1]),
        token: tokens[tokens.length - 1][1],
        source: 'log',
      };
    }
  }

  // 回退：启动时预定的端口与令牌
  try {
    const rec = JSON.parse(fs.readFileSync(processRecordPath(), 'utf8'));
    if (rec?.logFile === logFile && rec?.bridge?.port) {
      return {
        host: '127.0.0.1',
        port: Number(rec.bridge.port),
        token: rec.bridge.token,
        source: 'env',
      };
    }
  } catch {
    /* 没有记录就算了 */
  }
  return null;
}

export function listGameProcesses() {
  return tryListProcessesByName(GAME_IMAGE);
}

/**
 * 游戏进程枚举：先 tasklist，**不可用时用 Get-Process 兜底**。
 * 2026-10-13 新增兜底 —— 见 proc.js 的 listProcessesByPowerShell 注释。
 * 结果里的 `via` 说明这份清单是哪儿来的，调用方（和读报告的人）能看到判据来源。
 * 注意：和 tasklist 一样，这份清单**不区分 profile**（游戏进程看不出是哪个存档目录）。
 */
export async function listGameProcessesWithFallback() {
  const primary = await listGameProcesses();
  if (primary.known) return { ...primary, via: 'tasklist' };
  try {
    const processes = await listProcessesByPowerShell(GAME_IMAGE);
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
 *   - 'running'     : 有**正向**证据（记录的 PID 存活，或枚举到了进程）
 *   - 'not-running' : 有**排他**证据（枚举可用且没枚举到、且记录里的 PID 不存活）
 *   - 'unknown'     : 只有「查不到」这一条（枚举被拒 + 没有记录/记录里的 PID 不存活）
 * 旧字段 `running` / `recordedAlive` / `enumeration` 原样保留，语义不变。
 */
export async function gameStatus() {
  const record = await readProcessRecord();
  const recordedAlive = Boolean(record?.pid) && isAlive(record.pid);
  // 2026-10-13：枚举走「tasklist → Get-Process 兜底」，让"查不到"尽量变成"查得到"，
  // 剩下的才叫 unknown。兜底只在 tasklist 被拒时才多花那 0.2~1s。
  const enumeration = await listGameProcessesWithFallback();
  const via = enumeration.via ?? 'tasklist';
  const running = recordedAlive || (enumeration.known && enumeration.processes.length > 0);
  const state = running ? 'running' : enumeration.known ? 'not-running' : 'unknown';
  return {
    running,
    state,
    // 判据清单（给人看的，不是给机器判的）：说明 state 是从哪条证据推出来的
    evidence: [
      ...(recordedAlive ? [`记录的 PID ${record.pid} 仍存活（process.kill(pid,0) 未报 ESRCH）`] : []),
      ...(enumeration.known
        ? [
            enumeration.processes.length
              ? `${via} 枚举到 ${enumeration.processes.length} 个进程（PID ${enumeration.processes.map((p) => p.pid).join(', ')}）`
              : `${via} 可用，且没有枚举到该映像名的进程`,
          ]
        : [`进程枚举不可用：${enumeration.reason}`]),
      ...(state === 'unknown'
        ? ['没有任何可用的正向证据，也无法枚举进程 —— 判据不足，不能结论「没在跑」']
        : []),
    ],
    recordedPid: record?.pid ?? null,
    recordedAlive,
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
  const info = readBridgeInfo(logPathFor(profile));
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

export async function readProcessRecord() {
  try {
    return JSON.parse(fs.readFileSync(processRecordPath(), 'utf8'));
  } catch {
    return null;
  }
}

/**
 * 启动一个隔离的 RimWorld 测试实例。
 * @param {object} opts
 * @param {string}  [opts.profile='vanilla']  profiles/<name> 作为存档目录
 * @param {boolean} [opts.visible=true]       是否显示窗口
 * @param {number}  [opts.width]              窗口宽（不传则用 profile 的 prefs.json / 默认 1600）
 * @param {number}  [opts.height]             窗口高
 * @param {boolean} [opts.fullscreen]         是否全屏（默认 false —— 测试实例不该占满屏幕）
 * @param {boolean} [opts.keepLog=false]      默认把旧日志改名归档，避免误读上次的端口
 * @param {object}  [opts.prefs]              额外要强制的 Prefs 键
 */
export async function startGame({
  profile = 'vanilla',
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
  const exe = path.join(root, GAME_IMAGE);
  if (!fs.existsSync(exe)) {
    throw new Error(
      `找不到 RimWorld 可执行文件：${exe}\n请设置环境变量 RIMWORLD_ROOT 指向游戏安装目录。`,
    );
  }

  const status = await gameStatus();
  if (status.recordedAlive) {
    throw new Error(
      `已有 RimWorld 测试实例在运行（PID ${status.recordedPid}）。先执行 stop，或复用它。`,
    );
  }
  if (status.enumeration.known && status.enumeration.processes.length) {
    throw new Error(
      `检测到已在运行的 RimWorld 进程（PID ${status.enumeration.processes.map((p) => p.pid).join(', ')}）。` +
        `测试实例会和你的实际游戏互相干扰，请先关闭它。`,
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

  const logFile = logPathFor(profile);
  if (!keepLog && fs.existsSync(logFile)) {
    const stamp = new Date().toISOString().replace(/[:.]/g, '-');
    fs.renameSync(logFile, path.join(prof, `Player-${stamp}.log`));
  }

  // 预先定好端口和令牌，通过环境变量交给桥接
  const bridgeEnv = {
    port: Number(process.env.GABP_SERVER_PORT || 5174),
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

  const child = spawnDetached(exe, args, {
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

  const record = {
    pid: child.pid,
    startedAt: new Date().toISOString(),
    profile,
    profileDir: prof,
    logFile,
    exe,
    args,
    doorstop,
    bridge: bridgeEnv,
    prefsFile: prefsResult.path,
    window: {
      fullscreen: Boolean(effectivePrefs.fullscreen),
      width: effectivePrefs.screenWidth,
      height: effectivePrefs.screenHeight,
      monitor: effectivePrefs.monitor ?? null,
    },
  };
  fs.writeFileSync(processRecordPath(), JSON.stringify(record, null, 2), 'utf8');
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
  const record = pid ? null : await readProcessRecord();
  const targetPid = pid ?? record?.pid;
  // 启动时我们通过 GABP_SERVER_PORT/GABP_TOKEN 预定了端口和令牌。
  // 桥接可能认（那就没有 standalone 日志行），也可能不认（那就会写日志）。
  // 两条路都接住，避免任何一条失效就永远等下去。
  const expectHost = expected ?? record?.bridge ?? null;
  const started = Date.now();
  for (;;) {
    const info = readBridgeInfo(logFile);

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

    // 用我们记录的 PID 判断存活 —— 沙箱下 tasklist 不可用，这是唯一可靠的办法
    if (targetPid && !isAlive(targetPid)) {
      throw new Error(
        `游戏进程 ${targetPid} 已退出，但桥接始终没出现。日志尾部：\n${tailFile(logFile, 6000) || '(日志为空)'}`,
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

/** 关闭测试实例：先发 WM_CLOSE（让游戏正常退出），超时再强杀 */
export async function stopGame({ timeoutMs = 20_000 } = {}) {
  const status = await gameStatus();
  const pids = new Set();
  if (status.recordedAlive) pids.add(status.recordedPid);
  for (const p of status.enumeration.processes) pids.add(p.pid);
  if (!pids.size) return { stopped: [], alreadyStopped: true };

  const list = [...pids];
  for (const pid of list) {
    // taskkill 不带 /F 会给主窗口发 WM_CLOSE，属于正常退出；沙箱拒绝就忽略
    await capture('taskkill.exe', ['/PID', String(pid)], { timeoutMs: 15_000 }).catch(() => {});
  }

  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (!list.some((pid) => isAlive(pid))) return { stopped: list, forced: false };
    await sleep(500);
  }

  for (const pid of list) {
    killForce(pid);
    await capture('taskkill.exe', ['/PID', String(pid), '/T', '/F'], { timeoutMs: 15_000 }).catch(
      () => {},
    );
  }
  await sleep(500);
  return { stopped: list, forced: true, stillAlive: list.filter((pid) => isAlive(pid)) };
}

/** 记录里那个 PID 是否还活着 */
export async function recordedProcessAlive() {
  const status = await gameStatus();
  return status.recordedAlive;
}
