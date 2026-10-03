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
 *   node src/cli.js stop
 */
import fs from 'node:fs';
import path from 'node:path';
import { connectGabp } from './gabp.js';
import {
  startGame,
  stopGame,
  waitForBridge,
  readBridgeInfo,
  listGameProcesses,
  readProcessRecord,
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
      const procs = await listGameProcesses();
      const rec = await readProcessRecord();
      const info = readBridgeInfo(logPathFor(profile));
      out({
        gameRunning: procs.length > 0,
        processes: procs,
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
