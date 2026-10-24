#!/usr/bin/env node
/**
 * RimWorld 自动测试桥 —— 命令行入口。
 *
 *   node src/cli.js start [--profile vanilla] [--port 5174] [--minimized] [--width 1600] [--height 900]
 *   node src/cli.js status [--profile vanilla]
 *   node src/cli.js bridge [--profile vanilla]
 *   node src/cli.js tools [关键词] [--profile vanilla]
 *   node src/cli.js call <工具名> ['{"参数":...}'] [--profile vanilla]
 *   node src/cli.js wait --profile voiced --probe rimworld/list_colonists --condition '{"path":"result.colonists","countEquals":3}' [--timeout 60000]
 *   node src/cli.js logs [行数] [--profile vanilla]
 *   node src/cli.js live --profile voiced --until 'kind=colony_started' [--timeout 120000]
 *   node src/cli.js stop [--profile vanilla] [--force]
 */
import fs from 'node:fs';
import path from 'node:path';
import { connectGabp, DEFAULT_REQUEST_TIMEOUT_MS } from './gabp.js';
import { assertToolNameExists } from './toolname.js';
import { waitFor } from './wait.js';
import { buildSampler, resolveEventFile } from './wait-sources.js';
import { CONDITIONS_FILE, fillArgs, listScenarios, loadRegistry, resolveScenario } from './conditions.js';
import {
  startGame,
  stopGame,
  waitForBridge,
  readBridgeInfo,
  resolveBridge,
  readProcessRecord,
  gameStatus,
  probeBridge,
  logPathFor,
  tailFile,
  profileDir,
  gameRoot,
  WS,
} from './launcher.js';

function parseArgs(argv) {
  const positional = [];
  const flags = {};
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a.startsWith('--')) {
      const key = a.slice(2);
      const next = argv[i + 1];
      if (next !== undefined && !next.startsWith('--')) {
        // --arg 要能重复给多次（`--arg x=139 --arg z=231`），单个对象存不下
        if (key === 'arg') (flags.arg ??= []).push(next);
        else flags[key] = next;
        i++;
      } else {
        flags[key] = true;
      }
    } else {
      positional.push(a);
    }
  }
  return { positional, flags };
}

function out(obj) {
  console.log(typeof obj === 'string' ? obj : JSON.stringify(obj, null, 2));
}

/**
 * `--arg k=v`（可重复）→ `{k: 'v'}`；也接受一次给多个：`--arg x=139,z=231`。
 *
 * 存在的理由只有一个：登记表里的参数是静态 JSON，而坐标 / `pawnId` / `Letter_0` 每次都不同，
 * 用 `$x` 这类占位留口、由命令行填值（见 `src/conditions.js` 的 `fillArgs`）。
 * 值一律先当字符串，`fillArgs` 再把纯数字转成数字 —— 别在参数里偷偷做类型猜测。
 */
function parseArgPairs(value) {
  if (value === undefined) return {};
  const items = Array.isArray(value) ? value : [value];
  const pairs = {};
  for (const item of items) {
    if (item === true) throw new Error('--arg 需要 k=v 形式，例 --arg x=139');
    for (const chunk of String(item).split(',')) {
      const at = chunk.indexOf('=');
      if (at <= 0) throw new Error(`--arg 需要 k=v 形式，收到「${chunk}」`);
      pairs[chunk.slice(0, at).trim()] = chunk.slice(at + 1).trim();
    }
  }
  return pairs;
}

/** 读 JSON 文件并去掉 BOM —— Windows 工具（含 PowerShell 5.1）写 UTF-8 时常带 BOM */function readJsonFile(file) {
  let text = fs.readFileSync(file, 'utf8');
  if (text.charCodeAt(0) === 0xfeff) text = text.slice(1);
  return JSON.parse(text);
}

/**
 * 连上「本 profile 自己的」桥接。
 *
 * 2026-10-24：不再直接 `readBridgeInfo`（那只是解析层的猜测），先过 `resolveBridge()` 的
 * 四道校验（记录可信 / PID 命令行确实是本 profile 的实例 / 端口一致 / token 一致）。
 * 不通过就**不发命令**并说清判据 —— 多实例并行时「打进了别人的局而返回看着正常」比报错糟得多。
 */
async function withBridge(profile, fn, opts = {}) {
  const resolved = await resolveBridge(profile);
  if (!resolved.ok) {
    throw new Error(
      `拒绝连接 profile「${profile}」的桥接：${resolved.reason}\n` +
        `  判据：\n${resolved.evidence.map((e) => `  - ${e}`).join('\n')}\n` +
        '  先跑 `node src/cli.js status --profile ' +
        `${profile}\` 看清状态；别用别的 profile 的端口硬连。`,
    );
  }
  const { info } = resolved;
  const { client, welcome } = await connectGabp(info, opts);
  try {
    return await fn(client, welcome, { ...info, evidence: resolved.evidence, record: resolved.record });
  } finally {
    client.close();
  }
}

/**
 * 读一段 JSON 参数：优先文件，其次行内文本，其次位置参数。
 *
 * 为什么要文件这一路：PowerShell 会把 JSON 里的双引号吃掉（`'{"a":1}'` 到手可能已经变形），
 * 所以 `--args-file` / `--condition-file` 与 `'@文件'` 不是可选项，而是让命令能被
 * 子代理稳定敲出来的唯一保险；`@` 写法在 PowerShell 里**必须带引号**，否则会被当成 splatting。
 */
function readJsonInput({ inline, file, positional, flag }) {
  const fromFile = (path) => readJsonFile(path);
  const fromText = (text) => {
    if (text === true) throw new Error(`${flag} 需要一个值（JSON 文本，或 '@文件.json'）`);
    try {
      return text.startsWith('@') ? fromFile(text.slice(1)) : JSON.parse(text);
    } catch (err) {
      throw new Error(`${flag} 的 JSON 解析失败：${err.message}\n  收到：${String(text).slice(0, 300)}`);
    }
  };

  if (file !== undefined) return fromFile(file);
  if (inline !== undefined) return fromText(inline);
  if (positional !== undefined) return fromText(positional);
  return undefined;
}

const WAIT_USAGE = [
  '用法（两条路）:',
  '  node src/cli.js wait --profile <p> --scenario <场景名> [--arg k=v …] [--timeout 60000]',
  '  node src/cli.js wait --profile <p> --probe <工具名> (--condition \'<JSON>\' | --condition-file <文件>) [选项]',
  '  node src/cli.js wait --list            列出登记表里的全部场景（不连桥接）',
  '',
  '  --scenario 已进图可玩                 场景来自 src/conditions.json；探针/参数/条件都由它给，不许再给 --probe/--source',
  '  --arg x=139 [--arg z=231]            给场景 args 里的 $占位填值（可重复；也可 --arg x=139,z=231）',
  '  --source bridge                      目前只认 bridge（event/process/bridge-ready 属 P1 未实现）',
  '  --probe rimworld/list_colonists      要反复调用的只读工具名',
  '  --args \'{"currentMapOnly":true}\'      探针参数（也可 --args-file f.json 或 \'@f.json\'）',
  '  --condition \'{"path":"result.colonists","countEquals":3}\'   条件 JSON（也可 --condition-file f.json 或 \'@f.json\'）',
  '  --timeout 60000                      等多久（毫秒；场景自带 timeoutMs 时以场景为准，显式给则覆盖）',
  '  --interval 200                       两次采样的间隔（毫秒，默认 200）',
  '  退出码：0=条件命中 / 2=超时（打印条件+最后采样+attempts+elapsedMs）/ 1=参数错、工具名不存在、桥接未就绪等',
  '  条件与路径写法见 docs/cli-wait-用法-20261024.md',
].join('\n');

/** `wait` 打印采样时的上限：真载荷（get_bridge_status 带 companions/operation）能有 8 KB */
const SAMPLE_PRINT_LIMIT = 2000;

/**
 * 打印层的采样截断。为什么要截：一次超时把整个载荷糊到 stderr，子代理的上下文就被吃掉
 * 一大截，而它要看的只是条件涉及的那几个字段；完整载荷随时能用 `call` 重取。
 * 只截这一层——`src/wait.js` 返回的 `sample` 保持完整，离线测试与调用方都拿原对象。
 */
function evidenceForPrint(evidence) {
  const sampleJson = JSON.stringify(evidence.sample) ?? String(evidence.sample);
  if (sampleJson.length <= SAMPLE_PRINT_LIMIT) return { ...evidence, sampleTruncated: false };
  return {
    ...evidence,
    sampleTruncated: true,
    sample: `${sampleJson.slice(0, SAMPLE_PRINT_LIMIT)}…`,
  };
}

async function main() {
  const [cmd, ...rest] = process.argv.slice(2);
  const { positional, flags } = parseArgs(rest);
  const profile = flags.profile || 'vanilla';

  switch (cmd) {
    case 'start': {
      const rec = await startGame({
        profile,
        // 不传就让 launcher 在 5174..5183 里挑第一个空闲端口（多实例各拿一个）
        port: flags.port ? Number(flags.port) : undefined,
        visible: !flags.minimized,
        width: flags.width ? Number(flags.width) : undefined,
        height: flags.height ? Number(flags.height) : undefined,
        fullscreen: flags.fullscreen ? true : flags.windowed ? false : undefined,
        monitor: flags.monitor ? Number(flags.monitor) : undefined,
        doorstop: Boolean(flags.doorstop),
      });
      console.log(`游戏已启动 PID=${rec.pid}  profile=${profile}`);
      console.log(`  安装目录: ${gameRoot()}`);
      console.log(`  存档目录: ${rec.profileDir}   (独立，不影响主力存档)`);
      console.log(`  日志:     ${rec.logFile}`);
      console.log(`  记录:     ${rec.recordPath}`);
      console.log(`  端口:     ${rec.bridge.port}（${rec.portSource}）`);
      console.log(
        `  窗口:     ${rec.window.fullscreen ? '全屏' : '窗口化'} ` +
          `${rec.window.width}x${rec.window.height}  ` +
          `显示器=${rec.window.monitor ?? '默认'}   (改 profiles/${profile}/prefs.json 可调)`,
      );
      process.stdout.write('等待游戏内桥接就绪');
      const info = await waitForBridge({ profile, onTick: () => process.stdout.write('.') });
      console.log('');
      console.log(`桥接就绪：127.0.0.1:${info.port}  (等待 ${(info.waitedMs / 1000).toFixed(1)}s，来源=${info.source ?? 'log'})`);
      out({ bridge: { host: info.host, port: info.port }, tokenPreview: info.token.slice(0, 8) + '…' });
      break;
    }

    case 'status': {
      /**
       * 2026-10-13 修复：这里以前会给出**假阴性**。
       *
       * 旧代码：`const procs = await listGameProcesses(); ... gameRunning: procs.length > 0`。
       * 两个错叠在一起：
       *   1) `listGameProcesses()` 返回的是 `{known, processes, reason}` **对象**，不是数组
       *      （见 `data/auto-bisect.mjs:122` 的同一条备注），`obj.length` 是 `undefined`，
       *      `undefined > 0` 恒为 `false` —— 所以 `gameRunning` **任何时候都是 false**，
       *      连"枚举成功且真的找到了进程"也一样；
       *   2) 沙箱下 tasklist 被拒 → `known:false`，这本来只是"查不到"，却被第 1) 条
       *      掩盖成一个看起来确定的 `false`。有人据此写过"游戏未运行"的结论（实际在跑），
       *      代价是一次无谓的游戏重启。
       *
       * 现在：`gameRunning` 是**三态** `true | false | null`（null = 判据不足），
       * 并额外给出 `runningState`（'running'|'not-running'|'unknown'）与 `runningEvidence`
       * （逐条判据），让调用方必须自己面对"不知道"这个答案。
       * 正向证据优先取**不依赖 tasklist** 的两条：记录的 PID 存活、桥接握手可达。
       */
      const st = await gameStatus(profile);
      const rec = await readProcessRecord(profile);
      // ② 这里必须用**权威**解析（带 PID 身份校验），不能用 readBridgeInfo 的解析层猜测：
      // 竞态现场实测过「voiced 真实端口 5174，解析出 5175（别人的记录）」。
      const resolved = await resolveBridge(profile);
      const info = resolved.info;
      const bridge = await probeBridge(profile); // 有界超时（默认 1.5s）
      const evidence = [...st.evidence];
      if (info || resolved.evidence.length) {
        evidence.push(
          resolved.ok
            ? `桥接归属校验通过：${resolved.reason}`
            : `桥接归属校验未通过：${resolved.reason}`,
        );
        for (const line of resolved.evidence) evidence.push(`  · ${line}`);
      }
      if (bridge.reachable) {
        evidence.push(
          `桥接可达 ${bridge.host}:${bridge.port}（GABP session/hello 握手成功，` +
            `${bridge.app?.name ?? '?'} ${bridge.app?.version ?? ''}）—— 桥接跑在游戏进程内部，这是独立的"游戏活着"证据`,
        );
      } else if (bridge.available) {
        evidence.push(`桥接不可达 ${bridge.host}:${bridge.port}（${bridge.reason}）—— 弱否定证据，不足以单独结论"没在跑"`);
      } else {
        evidence.push(`桥接未知：${bridge.reason}`);
      }

      // 判定：任一条正向证据成立 → true。
      // 2026-10-17 多实例：`st.state` 只看本 profile 的记录（其它 RimWorld 进程不算），
      // 所以「记录里的 PID 不存活」就能说 false；桥接可达仍是最强的反向补证。
      let gameRunning;
      let runningState;
      if (st.state === 'running' || bridge.reachable) {
        gameRunning = true;
        runningState = 'running';
      } else if (st.state === 'not-running') {
        gameRunning = false;
        runningState = 'not-running';
      } else {
        gameRunning = null;
        runningState = 'unknown';
      }

      if (runningState === 'unknown') {
        process.stderr.write(
          '[警告] 无法判断游戏是否在运行（判据不足）—— gameRunning=null。\n' +
            '       不要把 null 当成 false；下面是逐条判据：\n' +
            evidence.map((e) => `       - ${e}`).join('\n') +
            '\n',
        );
      }

      out({
        profile,
        gameRunning,
        runningState,
        runningEvidence: evidence,
        // 兼容字段：旧调用方读的 `processes` 原样保留（它就是枚举结果对象本身，
        // 名字有歧义，但改结构会把读 `processes.processes` 的人弄坏）。
        processes: st.enumeration,
        processList: st.enumeration.processes,
        // 2026-10-17：其它 RimWorld 进程（可能是用户自己在玩的那一局）只报不判、不杀
        otherGamePids: st.otherGamePids,
        recordedPid: st.recordedPid,
        recordedAlive: st.recordedAlive,
        bridge,
        lastLaunch: rec,
        bridgeInLog: info ? { host: info.host, port: info.port, source: info.source } : null,
        // ② 给调用方一个可机器判的结论：这个桥接到底是不是本 profile 的
        bridgeResolution: {
          ok: resolved.ok,
          reason: resolved.reason,
          evidence: resolved.evidence,
          port: info?.port ?? null,
          recordedPort: rec?.bridge?.port ?? null,
        },
      });
      break;
    }

    case 'bridge': {
      // 与 status 同源：给结论（这份桥接凭什么属于我）+ 解析结果
      const resolved = await resolveBridge(profile);
      out(resolved.info ? { ...resolved.info, ok: resolved.ok, reason: resolved.reason, evidence: resolved.evidence } : {
        error: '没有可用的桥接信息',
        ok: false,
        reason: resolved.reason,
        evidence: resolved.evidence,
      });
      break;
    }

    case 'tools': {
      const kw = (positional[0] || '').toLowerCase();
      await withBridge(profile, async (client, welcome) => {
        const tools = await client.listTools();
        const filtered = kw
          ? tools.filter((t) =>
              `${t.name} ${t.description || ''}`.toLowerCase().includes(kw),
            )
          : tools;
        console.log(`游戏: ${welcome?.app?.name} ${welcome?.app?.version}  agentId=${welcome?.agentId}`);
        console.log(`能力方法: ${(welcome?.capabilities?.methods || []).join(', ')}`);
        console.log(`工具总数: ${tools.length}${kw ? `（匹配 "${kw}" 的 ${filtered.length} 个）` : ''}\n`);
        for (const t of filtered) {
          const desc = (t.description || '').replace(/\s+/g, ' ').slice(0, 90);
          console.log(`  ${t.name}${desc ? '  — ' + desc : ''}`);
        }
      });
      break;
    }

    case 'dump-tools': {
      await withBridge(profile, async (client, welcome) => {
        const tools = await client.listTools();
        const file = path.join(WS, 'runs', 'tools.json');
        fs.mkdirSync(path.dirname(file), { recursive: true });
        fs.writeFileSync(
          file,
          JSON.stringify({ welcome, tools }, null, 2),
          'utf8',
        );
        console.log(`已导出 ${tools.length} 个工具的完整 schema → ${file}`);
      });
      break;
    }

    case 'describe': {
      const name = positional[0];
      if (!name) throw new Error('用法: node src/cli.js describe <工具名>');
      await withBridge(profile, async (client) => {
        const tools = await client.listTools();
        const t = tools.find((x) => x.name === name);
        if (!t) {
          console.log(`未找到工具 ${name}`);
          process.exitCode = 1;
          return;
        }
        out(t);
      });
      break;
    }

    case 'call': {
      const name = positional[0];
      if (!name) throw new Error('用法: node src/cli.js call <工具名> [\'{"参数":...}\'] [--args-file f.json]');
      // 从文件读参数：绕开 PowerShell/bash 对 JSON 双引号的吞引号问题
      const args =
        readJsonInput({ inline: flags.args, file: flags['args-file'], positional: positional[1], flag: '--args' }) ?? {};
      // 默认超时与 gabp.js 同一个常量（60 s）：以前这里写死 300 s，一次挂住白等 5 分钟，
      // 而复用同一桥接的后续调用会跟着挂住（桥接把调用排在游戏主线程上）。长操作显式传 --timeout。
      // 连接与单次调用共用这一个值，所以必须在 withBridge 之前算好。
      const callTimeoutMs = flags.timeout === undefined ? DEFAULT_REQUEST_TIMEOUT_MS : Number(flags.timeout);
      if (!Number.isFinite(callTimeoutMs) || callTimeoutMs <= 0) {
        throw new Error(`--timeout 必须是正数（毫秒），收到「${flags.timeout}」`);
      }
      await withBridge(profile, async (client) => {
        // 2026-10-13：先校验工具名再调用，避免「调用不存在的工具名 + 吞掉错误」的静默失效。
        await assertToolNameExists(client, name);
        const res = await client.callTool(name, args, { timeoutMs: callTimeoutMs });
        out(res);
      }, { requestTimeoutMs: callTimeoutMs });
      break;
    }

    case 'wait': {
      /**
       * 等「探针采样满足条件」，而不是睡固定时长（设计稿 §3.2.5 的替代做法）。
       * 退出码与 live 同一套：0=命中 / 2=超时 / 1=参数错、工具名不存在、桥接未就绪等。
       * 判定只在 Node 侧做一套（src/wait.js），条件 JSON 与游戏内 rb.poll 的算子同构，
       * 以后可以原样下发；本轮只做 --source bridge。
       */
      // ② 登记表：--list 不连桥接、也不需要场景名
      const registry = loadRegistry();
      if (flags.list) {
        if (registry.errors.length) {
          console.error(`[wait] 登记表有 ${registry.errors.length} 处错误（下面仍列出可用场景）：`);
          for (const e of registry.errors) console.error(`  - ${e}`);
        }
        const rows = listScenarios(registry);
        console.log(`[wait] 条件登记表：${CONDITIONS_FILE}（${rows.length} 个场景）`);
        const mark = {
          'wait-verified': '✔ 真机 wait 验过',
          'field-verified': '～ 字段真机读过、条件没跑',
          'doc-example': '？ 文档示例',
          inferred: '× 推断',
        };
        for (const r of rows) {
          console.log(`  ${r.name}  [${r.source}] ${mark[r.verifiedLevel] ?? r.verifiedLevel}  — ${r.description}`);
        }
        break;
      }
      if (registry.errors.length && flags.scenario !== undefined) {
        throw new Error(
          `登记表有错，先修数据再按场景等：\n${registry.errors.map((e) => `  - ${e}`).join('\n')}\n  文件：${CONDITIONS_FILE}`,
        );
      }

      /**
       * ③ 场景 → (source, probe, args, condition, timeout)。
       * 场景身份（source / probe）**不许**被命令行覆盖 —— 否则就会出现「拿 A 的条件打 B 的探针」，
       * 那正是「字段名靠猜」的老毛病换了个入口。调参类（--timeout/--interval）与占位参数允许覆盖。
       */
      let scenario = null;
      if (flags.scenario !== undefined) {
        if (flags.scenario === true) throw new Error(`--scenario 需要一个场景名（用 --list 看有哪些）\n\n${WAIT_USAGE}`);
        const found = resolveScenario(registry, String(flags.scenario));
        if (!found.ok) throw new Error(`${found.reason}\n\n用 \`node src/cli.js wait --list\` 看全部场景。`);
        scenario = found.scenario;
        if (flags.probe !== undefined) {
          throw new Error(`--scenario 已经含探针（${scenario.probe ?? scenario.source}），不要再给 --probe`);
        }
        if (!scenario.verified) {
          console.error(`[wait] 场景「${scenario.name}」未经真机 wait 验证（级别 ${scenario.verifiedLevel}）：${scenario.evidence}`);
        }
      }

      const source = flags.source !== undefined ? String(flags.source) : (scenario?.source ?? 'bridge');
      if (scenario && source !== scenario.source) {
        throw new Error(`--source ${source} 与场景「${scenario.name}」的 source=${scenario.source} 冲突（场景身份不能覆盖）`);
      }
      if (!['bridge', 'event', 'process', 'bridge-ready'].includes(source)) {
        throw new Error(`--source 只认 bridge / event / process / bridge-ready，收到「${source}」\n\n${WAIT_USAGE}`);
      }

      // 四类来源各自的「指向谁」：bridge 要探针名，process 要 PID，event 要文件，bridge-ready 什么都不用
      let probeName = null;
      let pid = null;
      let eventFile = null;
      let eventFormat = 'events';
      if (source === 'bridge') {
        if (flags.probe === true) throw new Error(`--probe 需要一个工具名，例 rimworld/list_colonists\n\n${WAIT_USAGE}`);
        probeName = scenario ? scenario.probe : flags.probe === undefined ? positional[0] : String(flags.probe);
        if (!probeName) throw new Error(`缺少 --probe（要反复调用的只读工具名），或用 --scenario <名字>\n\n${WAIT_USAGE}`);
      } else if (source === 'process') {
        // 不给 --pid 就盯本 profile 记录里的那个 PID（`stop` 之后等它真的没了，就是这条）
        const recordPid = flags.pid === undefined ? readProcessRecord(profile)?.pid : Number(flags.pid);
        pid = Number(recordPid);
        if (!Number.isInteger(pid) || pid <= 0) {
          throw new Error(`--source process 需要 --pid <正整数>，或该 profile 有启动记录\n\n${WAIT_USAGE}`);
        }
      } else if (source === 'event') {
        eventFile = flags.file === undefined ? resolveEventFile(profile) : path.resolve(String(flags.file));
        // 事件文件有两种格式：DSH_GameLink 的 NDJSON（默认），以及整行当 `text` 的纯文本（Player.log）
        const format = flags['event-format'] === undefined ? 'events' : String(flags['event-format']);
        if (format !== 'events' && format !== 'log') {
          throw new Error(`--event-format 只认 events|log，收到「${format}」\n\n${WAIT_USAGE}`);
        }
        eventFormat = format;
      }

      // 参数：场景自带 args（`$名字` 占位由 --arg 填）；没有场景时整份来自 --args/--args-file
      const providedArgs = readJsonInput({
        inline: flags.args,
        file: flags['args-file'],
        positional: positional[1],
        flag: '--args',
      });
      const argPairs = parseArgPairs(flags.arg);
      let args = providedArgs ?? {};
      if (scenario) {
        const filled = fillArgs(scenario.args, argPairs);
        if (!filled.ok) {
          throw new Error(
            `场景「${scenario.name}」需要这些参数：${filled.missing.map((k) => `--arg ${k}=…`).join(' ')}`,
          );
        }
        if (filled.unused.length) {
          console.error(`[wait] 这些 --arg 用不上（场景里没有对应占位）：${filled.unused.join(', ')}`);
        }
        args = { ...filled.args, ...(providedArgs ?? {}) };
      } else if (Object.keys(argPairs).length) {
        throw new Error('--arg 只能配合 --scenario 用：没有登记表条目时，占位符无从对应');
      }

      const condition = scenario
        ? scenario.condition
        : readJsonInput({
            inline: flags.condition,
            file: flags['condition-file'],
            positional: undefined,
            flag: '--condition',
          });
      if (condition === undefined) {
        throw new Error(
          `缺少 --condition（条件 JSON，例 '{"path":"result.colonists","countEquals":3}'）或用 --scenario\n\n${WAIT_USAGE}`,
        );
      }
      if (condition === null || typeof condition !== 'object' || Array.isArray(condition)) {
        throw new Error(`--condition 必须是一个 JSON 对象，收到：${JSON.stringify(condition).slice(0, 200)}\n\n${WAIT_USAGE}`);
      }

      const timeoutMs = flags.timeout === undefined ? (scenario?.timeoutMs ?? 60_000) : Number(flags.timeout);
      if (!Number.isFinite(timeoutMs) || timeoutMs <= 0) {
        throw new Error(`--timeout 必须是正数（毫秒），收到「${flags.timeout}」\n\n${WAIT_USAGE}`);
      }
      const intervalMs = flags.interval === undefined ? 200 : Number(flags.interval);
      if (!Number.isFinite(intervalMs) || intervalMs <= 0) {
        throw new Error(`--interval 必须是正数（毫秒），收到「${flags.interval}」\n\n${WAIT_USAGE}`);
      }

      // 总预算从这条命令开始算（含连接桥接的时间），单次调用也不许越过它
      const deadline = Date.now() + timeoutMs;
      const target =
        source === 'bridge' ? `probe=${probeName}` : source === 'process' ? `pid=${pid}` : source === 'event' ? `file=${eventFile}` : '(本 profile)';
      process.stderr.write(
        `[wait] profile=${profile} source=${source} ${target} timeout=${timeoutMs}ms interval=${intervalMs}ms\n`,
      );

      // 四类来源共用**同一个** waitFor 循环：只有「取样器」与「谁提供 client」不同
      const spec = {
        source,
        profile,
        probe: probeName,
        args,
        deadline,
        pid,
        file: eventFile,
        eventSource: eventFormat,
        fromNow: Boolean(flags['new-only']),
        windowMs: flags['window-ms'] === undefined ? null : Number(flags['window-ms']),
      };
      const result =
        source === 'bridge'
          ? await withBridge(
              profile,
              async (client) => {
                // 名字写错要立刻响亮失败，不要等满超时（与 call 同一条纪律）
                await assertToolNameExists(client, probeName);
                return waitFor(buildSampler(spec, { client }), {
                  condition,
                  timeoutMs: Math.max(1, deadline - Date.now()),
                  intervalMs,
                  what: probeName,
                });
              },
              { requestTimeoutMs: timeoutMs },
            )
          : await waitFor(buildSampler(spec), {
              condition,
              timeoutMs,
              intervalMs,
              what: source === 'process' ? `PID ${pid}` : source === 'event' ? eventFile : `profile ${profile}`,
            });

      const evidence = {
        ok: result.ok,
        elapsedMs: result.elapsedMs,
        attempts: result.attempts,
        source,
        ...(probeName ? { probe: probeName } : {}),
        ...(source === 'process' ? { pid } : {}),
        ...(source === 'event' ? { file: eventFile } : {}),
        ...(scenario ? { scenario: scenario.name, verifiedLevel: scenario.verifiedLevel } : {}),
        condition,
        ...(result.missingPath ? { missingPath: result.missingPath } : {}),
        sample: result.sample,
      };
      const argsHint = flags['args-file']
        ? `--args-file '${flags['args-file']}'`
        : `--args '${JSON.stringify(args)}'`;
      const printed = evidenceForPrint(evidence);
      if (printed.sampleTruncated && probeName) {
        // 采样截断只发生在打印层：真载荷动辄 8 KB，一次超时就能吃掉子代理半个上下文
        console.error(
          `[wait] 采样已截断（只印前 ${SAMPLE_PRINT_LIMIT} 字符）；` +
            `要看完整载荷：node src/cli.js call ${probeName} ${argsHint}`,
        );
      }
      if (result.ok) {
        out(printed);
      } else {
        // 超时必须能诊断：条件、最后一次采样、attempts、elapsedMs 一个都不能少
        console.error(
          `[wait] 超时：${timeoutMs}ms 内没等到条件成立（attempts=${result.attempts}，elapsedMs=${result.elapsedMs}）`,
        );
        if (result.missingPath) {
          // 字段一直没出现 ≠ 值不匹配：前者多半是还没进图，后者要改条件。分开说才不会让人去改错东西。
          const m = result.missingPath;
          console.error(
            `路径 ${m.path} 从头到尾都没取到；断在 ${m.brokenAt} 这一段，` +
              `那一层的可用字段：${m.available.length ? m.available.join(', ') : '（空）'}`,
          );
          console.error(
            '通常是还没进图（先等 {"path":"result.state.playable","equals":true}），或字段名写错；' +
              '这条不是参数错误，是条件一直没成立。',
          );
        }
        console.error(JSON.stringify(printed, null, 2));
        process.exitCode = 2;
      }
      break;
    }

    case 'logs': {
      const n = Number(positional[0] || 60);
      const file = logPathFor(profile);
      const lines = tailFile(file, 4_000_000).split(/\r?\n/);
      console.log(lines.slice(-n).join('\n'));
      break;
    }

    case 'live': {
      /**
       * 等一个游戏侧事件，而不是「睡固定时长 + 事后读日志」。
       * 退出码就是结果：0=命中 / 2=超时 / 1=参数错（见 src/live.js 顶部）。
       * 挂成后台 job 时，job 完成的通知本身就是「事件到了」的推送。
       */
      const { runLive } = await import('./live.js');
      process.exitCode = await runLive({ profile, flags });
      break;
    }

    case 'stop': {
      // 默认只杀本 profile 记录里的 PID；--force / --all 才是「枚举全机 RimWorld 并强杀」的破坏性出口。
      const r = await stopGame({ profile, force: Boolean(flags.force || flags.all) });
      if (r.alreadyStopped) {
        console.log(
          r.recordedPid
            ? `profile「${profile}」记录里的 PID ${r.recordedPid} 已不在运行（没有需要关的）`
            : `没有 ${profile} 的运行记录`,
        );
      }
      out(r);
      break;
    }

    case 'smoke': {
      const { runSmoke } = await import('./smoke.js');
      const report = await runSmoke({
        profile,
        ticks: Number(flags.ticks || 300),
        keepRunning: Boolean(flags['keep-running']),
        launch: !flags.attach,
      });
      process.exitCode = report.passed ? 0 : 1;
      break;
    }

    default:
      console.log(
        [
          'RimWorld 自动测试桥（每条命令都接受 --profile <名字>，默认 vanilla）',
          '',
          '  start    启动隔离测试实例并等待桥接就绪（--port N 显式指定端口，否则自动挑 5174..5183 里空闲的）',
          '  status   查看该 profile 的进程 / 上次启动 / 日志中的桥接信息',
          '  bridge   只打印该 profile 的桥接端口与 token',
          '  tools    列出游戏暴露的工具（可加关键词过滤）',
          '  describe 显示单个工具的完整参数 schema',
          '  dump-tools  把所有工具 schema 导出到 runs/tools.json',
          '  call     调用一个游戏内工具',
          '  wait     等条件成立（反复调探针 + 条件 JSON；0 命中 / 2 超时 / 1 其它），见 docs/cli-wait-用法-20261024.md',
          '  logs     打印该 profile 的 Player.log 尾部',
          '  live     等游戏侧事件（DSH_GameLink 的 NDJSON 或 Player.log），命中即返回',
          '  stop     关闭该 profile 的测试实例（--force 才会强杀全机 RimWorld，破坏性）',
          '  smoke    跑冒烟测试闭环',
        ].join('\n'),
      );
  }
}

main().catch((err) => {
  console.error(`\n[错误] ${err.message}`);
  process.exitCode = 1;
});
