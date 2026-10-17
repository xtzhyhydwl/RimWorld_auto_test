#!/usr/bin/env node
/**
 * RimWorld 自动测试桥 —— 命令行入口。
 *
 *   node src/cli.js start [--profile vanilla] [--minimized] [--width 1600] [--height 900]
 *   node src/cli.js status
 *   node src/cli.js bridge
 *   node src/cli.js tools [关键词]
 *   node src/cli.js call <工具名> ['{"参数":...}']
 *   node src/cli.js logs [行数]
 *   node src/cli.js live --profile voiced --until 'kind=colony_started' [--timeout 120000]
 *   node src/cli.js stop
 */
import fs from 'node:fs';
import path from 'node:path';
import { connectGabp } from './gabp.js';
import { checkToolName } from './toolname.js';
import {
  startGame,
  stopGame,
  waitForBridge,
  readBridgeInfo,
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
        flags[key] = next;
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

/** 读 JSON 文件并去掉 BOM —— Windows 工具（含 PowerShell 5.1）写 UTF-8 时常带 BOM */
function readJsonFile(file) {
  let text = fs.readFileSync(file, 'utf8');
  if (text.charCodeAt(0) === 0xfeff) text = text.slice(1);
  return JSON.parse(text);
}

async function withBridge(profile, fn, opts = {}) {
  const info = readBridgeInfo(logPathFor(profile));
  if (!info) throw new Error('日志里还没有桥接信息，先跑 `node src/cli.js start`。');
  const { client, welcome } = await connectGabp(info, opts);
  try {
    return await fn(client, welcome, info);
  } finally {
    client.close();
  }
}

/**
 * 2026-10-13 新增：`call` 路径的**工具名预校验**，在真正发起调用之前执行。
 *
 * 为什么要有它：`tmp/audit-test/AUDIT.md`（2.7 / A8）记录了一次**静默失效**——
 * `scripts/verify-5-actions.mjs:201/220` 调用了**不存在**的工具
 * `rimworld/wait_for_long_event_idle`（该工具只在 `rimbridge/` 命名空间下，
 * 见 `tmp/audit-test/tool-names.txt:26`），失败又被 `.catch(() => {})` 吞掉，
 * 脚本「以为自己等过了」，实际零等待、零记录，报告看起来有效却什么都没发生。
 * 这比固定等待更糟，所以把校验提到 CLI 层：写错名字必须**响亮地失败**。
 *
 * 判据：**优先用实时清单** `client.listTools()`（与 `describe` 分支同一用法）。
 * 拿不到实时清单（桥接未连接 / 查询失败）就**跳过校验**并提示一句——
 * 宁可漏报，也不用 2026-10-03 的 `runs/tools.json` 过期快照硬判，那会产生假失败。
 *
 * 明确**不**做的事：不自动把名字改成「最接近的那个」再继续调用。人必须知道名字写错了。
 */
async function assertToolNameExists(client, name) {
  let names;
  try {
    const tools = await client.listTools();
    names = (tools ?? []).map((t) => t.name);
  } catch (err) {
    console.warn(`[警告] 取不到实时工具清单（${err.message}），跳过工具名预校验，直接发起调用。`);
    return;
  }
  if (!names.length) {
    console.warn('[警告] 实时工具清单为空，跳过工具名预校验，直接发起调用。');
    return;
  }

  const r = checkToolName(name, names);
  if (r.ok) return;

  console.error(`工具名不存在：${name}`);
  console.error(`  清单来源：实时清单 client.listTools()（共 ${names.length} 个工具）`);
  if (r.candidates?.length) {
    console.error('  最接近的候选名（按相似度）：');
    for (const c of r.candidates) {
      console.error(`    - ${c.name}${c.namespaceDiffers ? '   ← 子名相同，命名空间不同' : ''}`);
    }
  } else {
    console.error('  最接近的候选名：没找到相近的（名字可能整体写错了）');
  }
  for (const h of r.hints ?? []) console.error(`  提示：${h}`);
  console.error('  本次调用未发起；也不会自动改用上面任何一个候选名。');
  process.exitCode = 1;
  throw new Error(`未知工具「${name}」——已阻止调用（不会自动改用候选名）`);
}

async function main() {
  const [cmd, ...rest] = process.argv.slice(2);
  const { positional, flags } = parseArgs(rest);
  const profile = flags.profile || 'vanilla';

  switch (cmd) {
    case 'start': {
      const rec = await startGame({
        profile,
        visible: !flags.minimized,
        width: flags.width ? Number(flags.width) : undefined,
        height: flags.height ? Number(flags.height) : undefined,
        fullscreen: flags.fullscreen ? true : flags.windowed ? false : undefined,
        monitor: flags.monitor ? Number(flags.monitor) : undefined,
        doorstop: Boolean(flags.doorstop),
      });
      console.log(`游戏已启动 PID=${rec.pid}`);
      console.log(`  安装目录: ${gameRoot()}`);
      console.log(`  存档目录: ${rec.profileDir}   (独立，不影响主力存档)`);
      console.log(`  日志:     ${rec.logFile}`);
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
      const st = await gameStatus();
      const rec = await readProcessRecord();
      const info = readBridgeInfo(logPathFor(profile));
      const bridge = await probeBridge(profile); // 有界超时（默认 1.5s）
      const evidence = [...st.evidence];
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

      // 判定：任一条正向证据成立 → true；只有在"能枚举且没枚举到 + 记录里的 PID 也不存活"时才敢说 false
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
        gameRunning,
        runningState,
        runningEvidence: evidence,
        // 兼容字段：旧调用方读的 `processes` 原样保留（它就是枚举结果对象本身，
        // 名字有歧义，但改结构会把读 `processes.processes` 的人弄坏）。
        processes: st.enumeration,
        processList: st.enumeration.processes,
        recordedPid: st.recordedPid,
        recordedAlive: st.recordedAlive,
        bridge,
        lastLaunch: rec,
        bridgeInLog: info ? { host: info.host, port: info.port } : null,
      });
      break;
    }

    case 'bridge': {
      const info = readBridgeInfo(logPathFor(profile));
      out(info ?? { error: '日志中未找到桥接信息' });
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
      let args;
      if (flags['args-file']) {
        // 从文件读参数：绕开 PowerShell/bash 对 JSON 双引号的吞引号问题
        args = readJsonFile(flags['args-file']);
      } else if (positional[1]) {
        // 也支持 @file.json 简写
        args = positional[1].startsWith('@')
          ? readJsonFile(positional[1].slice(1))
          : JSON.parse(positional[1]);
      } else {
        args = {};
      }
      await withBridge(profile, async (client) => {
        // 2026-10-13：先校验工具名再调用，避免「调用不存在的工具名 + 吞掉错误」的静默失效。
        await assertToolNameExists(client, name);
        const res = await client.callTool(name, args, { timeoutMs: Number(flags.timeout || 300000) });
        out(res);
      }, { requestTimeoutMs: Number(flags.timeout || 300000) });
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
      const r = await stopGame();
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
          'RimWorld 自动测试桥',
          '',
          '  start    启动隔离测试实例并等待桥接就绪',
          '  status   查看进程 / 上次启动 / 日志中的桥接信息',
          '  bridge   只打印桥接端口与 token',
          '  tools    列出游戏暴露的工具（可加关键词过滤）',
          '  describe 显示单个工具的完整参数 schema',
          '  dump-tools  把所有工具 schema 导出到 runs/tools.json',
          '  call     调用一个游戏内工具',
          '  logs     打印 Player.log 尾部',
          '  live     等游戏侧事件（DSH_GameLink 的 NDJSON 或 Player.log），命中即返回',
          '  stop     关闭测试实例',
          '  smoke    跑冒烟测试闭环',
        ].join('\n'),
      );
  }
}

main().catch((err) => {
  console.error(`\n[错误] ${err.message}`);
  process.exitCode = 1;
});
