/**
 * dsh-rimworld —— 把 RimWorld 自动测试桥接注册成 DSH 原生工具。
 *
 * 设计说明：
 *  - 这是一个 Cordis 插件，形态照抄本机已验证可用的 dsh-win-toolkit。
 *  - 真正的协议/启动/测试逻辑在 RimWorld_auto_test 仓库的 src/ 里，
 *    这里用「按配置里的绝对路径动态 import」的方式复用，避免插件和仓库互相拷贝。
 *  - 懒加载：即使 libraryPath 配错，也不会拖垮 DSH 启动，只会让这些工具报错。
 *  - 子进程用仓库里的 proc.js（stdout/stderr 重定向到临时文件，不用管道），
 *    这样即使在受限沙箱里也能正常工作。
 */
import path from 'node:path';
import fs from 'node:fs';
import { pathToFileURL } from 'node:url';
import { defineTool } from '@deepseek-ai/dsh-tools';

const DEFAULT_LIBRARY =
  'E:\\2026.7以后的新工程\\DSH\\RimWorld_auto_test\\src';
const DEFAULT_GAME_ROOT = 'D:\\SteamLibrary\\steamapps\\common\\RimWorld';

function meta(result) {
  return result.meta;
}

export default Object.assign(
  function rimworld(ctx, config = {}) {
    const libraryPath = config.libraryPath || DEFAULT_LIBRARY;
    const profile = config.profile || 'vanilla';

    // 让仓库里的 launcher 找到游戏
    if (config.gameRoot) process.env.RIMWORLD_ROOT = config.gameRoot;
    else if (!process.env.RIMWORLD_ROOT) process.env.RIMWORLD_ROOT = DEFAULT_GAME_ROOT;

    let libPromise = null;
    const lib = () => {
      if (!libPromise) {
        const load = (file) => import(pathToFileURL(path.join(libraryPath, file)).href);
        libPromise = Promise.all([load('launcher.js'), load('gabp.js'), load('smoke.js')]).then(
          ([launcher, gabp, smoke]) => ({ launcher, gabp, smoke }),
        );
      }
      return libPromise;
    };

    const connect = async () => {
      const { launcher, gabp } = await lib();
      const info = launcher.readBridgeInfo(launcher.logPathFor(profile));
      if (!info) {
        throw new Error(
          `日志里没有桥接信息。先调用 rimworld_launch 启动测试实例，` +
            `或确认游戏正在运行。日志：${launcher.logPathFor(profile)}`,
        );
      }
      const { client, welcome } = await gabp.connectGabp(info, { requestTimeoutMs: 900_000 });
      return { client, welcome, info, launcher };
    };

    const withClient = async (fn) => {
      const { client, welcome, info, launcher } = await connect();
      try {
        return await fn(client, welcome, info, launcher);
      } finally {
        client.close();
      }
    };

    // ───────────────────────── rimworld_status ─────────────────────────
    ctx.tools.register(
      defineTool({
        name: 'rimworld_status',
        description:
          'Check whether the RimWorld test instance is running and whether the in-game GABP bridge is reachable. ' +
          'Read-only; does not start or stop anything.',
        parameters: {},
        output: {
          schema: {
            type: 'object',
            additionalProperties: false,
            properties: {
              text: { type: 'string', required: true },
              running: { type: 'boolean', required: true },
              bridgeUp: { type: 'boolean', required: true },
            },
          },
          render: (_a, v) => [{ type: 'text', text: v.text }],
          presentationMeta: (_a, v) => v,
        },
        presentCall: () => ({ card: 'generic', title: 'RimWorld 状态', kind: 'read' }),
        presentResult: (_a, r) => ({ card: 'generic', title: meta(r)?.text ?? '状态' }),
        async execute() {
          const { launcher } = await lib();
          const status = await launcher.gameStatus();
          const info = launcher.readBridgeInfo(launcher.logPathFor(profile));
          let bridgeUp = false;
          let detail = '';
          if (info) {
            try {
              const { client, welcome } = await connect();
              bridgeUp = true;
              detail = `${welcome?.app?.name ?? '?'} ${welcome?.app?.version ?? ''}`.trim();
              client.close();
            } catch (err) {
              detail = err.message;
            }
          }
          const lines = [
            `游戏进程: ${status.running ? `运行中 (PID ${status.recordedPid ?? '未知'})` : '未运行'}`,
            `桥接: ${info ? `${info.host}:${info.port}` : '日志中无记录'}${bridgeUp ? `  ✓ ${detail}` : ''}`,
            `隔离存档目录: ${launcher.profileDir(profile)}`,
            `游戏日志: ${launcher.logPathFor(profile)}`,
          ];
          if (info && !bridgeUp) lines.push(`桥接不可达: ${detail}`);
          return {
            text: lines.join('\n'),
            running: status.running,
            bridgeUp,
          };
        },
      }),
    );

    // ───────────────────────── rimworld_launch ─────────────────────────
    ctx.tools.register(
      defineTool({
        name: 'rimworld_launch',
        description:
          'Launch an isolated RimWorld test instance and wait until the in-game bridge is ready. ' +
          'Uses a separate save-data folder so the real save is never touched. ' +
          'Doorstop is disabled by default because its IL hot-reload breaks RimBridgeServer loading.',
        parameters: {
          profileName: { type: 'string', description: 'Profile folder under profiles/ (default from config)' },
          minimized: { type: 'boolean', description: 'Start with a minimized window' },
          doorstop: { type: 'boolean', description: 'Keep Doorstop enabled (default false)' },
        },
        output: {
          schema: {
            type: 'object',
            additionalProperties: false,
            properties: {
              text: { type: 'string', required: true },
              pid: { type: 'integer', required: true },
              port: { type: 'integer', required: true },
              waitedSeconds: { type: 'number', required: true },
            },
          },
          render: (_a, v) => [{ type: 'text', text: v.text }],
          presentationMeta: (_a, v) => v,
        },
        presentCall: () => ({ card: 'generic', title: '启动 RimWorld 测试实例', kind: 'other' }),
        presentResult: (_a, r) => ({ card: 'generic', title: meta(r)?.text ?? '已启动' }),
        async execute(args) {
          const { launcher } = await lib();
          const prof = args?.profileName || profile;
          const rec = await launcher.startGame({
            profile: prof,
            visible: !args?.minimized,
            doorstop: Boolean(args?.doorstop),
          });
          const info = await launcher.waitForBridge({ profile: prof, pid: rec.pid });
          const text =
            `RimWorld 测试实例已就绪\n` +
            `  PID: ${rec.pid}\n` +
            `  隔离存档目录: ${rec.profileDir}\n` +
            `  桥接: ${info.host}:${info.port}（等待 ${(info.waitedMs / 1000).toFixed(1)}s）`;
          return { text, pid: rec.pid, port: info.port, waitedSeconds: info.waitedMs / 1000 };
        },
      }),
    );

    // ───────────────────────── rimworld_stop ─────────────────────────
    ctx.tools.register(
      defineTool({
        name: 'rimworld_stop',
        description: 'Stop the RimWorld test instance (graceful close first, then force).',
        parameters: {},
        output: {
          schema: {
            type: 'object',
            additionalProperties: false,
            properties: { text: { type: 'string', required: true } },
          },
          render: (_a, v) => [{ type: 'text', text: v.text }],
          presentationMeta: (_a, v) => v,
        },
        presentCall: () => ({ card: 'generic', title: '关闭 RimWorld 实例', kind: 'other' }),
        presentResult: (_a, r) => ({ card: 'generic', title: meta(r)?.text ?? '已关闭' }),
        async execute() {
          const { launcher } = await lib();
          const res = await launcher.stopGame();
          const text = res.alreadyStopped
            ? '没有正在运行的实例。'
            : `已关闭 PID ${res.stopped.join(', ')}${res.forced ? '（强制）' : '（正常退出）'}`;
          return { text };
        },
      }),
    );

    // ───────────────────────── rimworld_tools ─────────────────────────
    ctx.tools.register(
      defineTool({
        name: 'rimworld_tools',
        description:
          'List the tools the running RimWorld session exposes through the bridge (125+ tools). ' +
          'Use a keyword filter to narrow it down, e.g. "colonist", "screenshot", "architect".',
        parameters: {
          filter: { type: 'string', description: 'Case-insensitive keyword matched against tool name and description' },
        },
        output: {
          schema: {
            type: 'object',
            additionalProperties: false,
            properties: {
              text: { type: 'string', required: true },
              count: { type: 'integer', required: true },
            },
          },
          render: (_a, v) => [{ type: 'text', text: v.text }],
          presentationMeta: (_a, v) => v,
        },
        presentCall: (a) => ({
          card: 'generic',
          title: `列出游戏工具${a?.filter ? ` (${a.filter})` : ''}`,
          kind: 'read',
        }),
        presentResult: (_a, r) => ({ card: 'generic', title: meta(r)?.text?.split('\n')[0] ?? '工具列表' }),
        async execute(args) {
          return withClient(async (client) => {
            const tools = await client.listTools();
            const kw = (args?.filter || '').toLowerCase();
            const hits = kw
              ? tools.filter((t) => `${t.name} ${t.description || ''}`.toLowerCase().includes(kw))
              : tools;
            const body = hits
              .map((t) => `  ${t.name} — ${String(t.description || '').replace(/\s+/g, ' ').slice(0, 110)}`)
              .join('\n');
            return {
              text: `共 ${tools.length} 个工具${kw ? `，匹配 "${kw}" 的 ${hits.length} 个` : ''}\n${body}`,
              count: hits.length,
            };
          });
        },
      }),
    );

    // ───────────────────────── rimworld_call ─────────────────────────
    ctx.tools.register(
      defineTool({
        name: 'rimworld_call',
        description:
          'Call any tool exposed by the running RimWorld session through the bridge. ' +
          'Pass arguments as a JSON object string. Use rimworld_tools to discover tool names and ' +
          'their input schemas first. Example: tool="rimworld/list_colonists", args="{}".',
        parameters: {
          tool: { type: 'string', description: 'Exact game tool name, e.g. rimworld/list_colonists', required: true },
          args: { type: 'string', description: 'JSON object string of arguments, e.g. {"ticks":300}. Defaults to {}' },
        },
        output: {
          schema: {
            type: 'object',
            additionalProperties: false,
            properties: {
              text: { type: 'string', required: true },
              json: { type: 'string', required: true },
            },
          },
          render: (_a, v) => [{ type: 'text', text: v.text }],
          presentationMeta: (_a, v) => v,
        },
        presentCall: (a) => ({ card: 'generic', title: `调用 ${a?.tool ?? '工具'}`, kind: 'other' }),
        presentResult: (_a, r) => ({ card: 'generic', title: meta(r)?.text?.slice(0, 80) ?? '调用结果' }),
        async execute(args) {
          let parsed = {};
          if (args?.args) {
            try {
              parsed = JSON.parse(args.args);
            } catch (err) {
              throw new Error(`args 不是合法 JSON: ${err.message}`);
            }
          }
          return withClient(async (client) => {
            const res = await client.callTool(args.tool, parsed, { timeoutMs: 900_000 });
            const json = JSON.stringify(res, null, 2);
            // 太长的结果截断给模型看，完整内容仍在返回值里
            const shown = json.length > 12_000 ? json.slice(0, 12_000) + '\n… (已截断)' : json;
            return { text: shown, json };
          });
        },
      }),
    );

    // ───────────────────────── rimworld_smoke ─────────────────────────
    ctx.tools.register(
      defineTool({
        name: 'rimworld_smoke',
        description:
          'Run the full RimWorld smoke suite: connect, start a debug colony if needed, advance N ticks, ' +
          'collect new log errors, snapshot alerts, take a screenshot, assert, and write a report. ' +
          'Returns pass/fail plus the report path. This is the main regression entry point.',
        parameters: {
          ticks: { type: 'integer', description: 'Game ticks to advance (default 300)' },
          attach: { type: 'boolean', description: 'Use the already running instance instead of launching a new one' },
          keepRunning: { type: 'boolean', description: 'Do not stop the game when finished' },
          doorstop: { type: 'boolean', description: 'Keep Doorstop enabled when launching (default false)' },
        },
        output: {
          schema: {
            type: 'object',
            additionalProperties: false,
            properties: {
              text: { type: 'string', required: true },
              passed: { type: 'boolean', required: true },
              reportPath: { type: 'string', required: true },
            },
          },
          render: (_a, v) => [{ type: 'text', text: v.text }],
          presentationMeta: (_a, v) => v,
        },
        presentCall: () => ({ card: 'generic', title: '跑 RimWorld 冒烟测试', kind: 'other' }),
        presentResult: (_a, r) => ({
          card: 'generic',
          title: meta(r)?.passed ? '冒烟测试通过 ✓' : '冒烟测试失败 ✗',
        }),
        async execute(args) {
          const { smoke, launcher } = await lib();
          const attach = Boolean(args?.attach);
          const report = await smoke.runSmoke({
            profile,
            ticks: args?.ticks ?? 300,
            launch: !attach,
            keepRunning: Boolean(args?.keepRunning),
            doorstop: Boolean(args?.doorstop),
            label: 'dsh-smoke',
            log: () => {}, // 不打到 DSH 日志里，结果统一在返回值
          });
          const reportPath = path.join(launcher.WS, 'runs', report.runId, 'report.md');
          const lines = [
            report.passed ? '冒烟测试：通过 ✓' : '冒烟测试：失败 ✗',
            '',
            ...report.checks.map((c) => `  ${c.ok ? '✓' : '✗'} ${c.name}${c.detail ? ` — ${c.detail}` : ''}`),
          ];
          if (report.error) lines.push('', `中断：${report.error}`);
          if (report.screenshot?.source) lines.push('', `截图：${report.screenshot.source}`);
          lines.push('', `报告：${reportPath}`);
          return {
            text: lines.join('\n'),
            passed: Boolean(report.passed),
            reportPath,
          };
        },
      }),
    );

    // ───────────────────────── rimworld_logs ─────────────────────────
    ctx.tools.register(
      defineTool({
        name: 'rimworld_logs',
        description:
          'Tail the isolated test instance Player.log, or read the bridge in-memory log journal ' +
          '(filtered by severity) from the running session. Use source="bridge" for structured entries.',
        parameters: {
          lines: { type: 'integer', description: 'For source="file": how many trailing lines (default 80)' },
          source: { type: 'string', description: '"file" (default) or "bridge"' },
          minimumLevel: { type: 'string', description: 'For source="bridge": info | warning | error | fatal' },
        },
        output: {
          schema: {
            type: 'object',
            additionalProperties: false,
            properties: { text: { type: 'string', required: true } },
          },
          render: (_a, v) => [{ type: 'text', text: v.text }],
          presentationMeta: (_a, v) => v,
        },
        presentCall: (a) => ({
          card: 'generic',
          title: a?.source === 'bridge' ? '游戏内日志' : 'Player.log',
          kind: 'read',
        }),
        presentResult: (_a, r) => ({ card: 'generic', title: '日志' }),
        async execute(args) {
          const { launcher } = await lib();
          if (args?.source === 'bridge') {
            return withClient(async (client) => {
              const res = await client.callTool('rimbridge/list_logs', {
                minimumLevel: args?.minimumLevel || 'info',
                limit: 200,
              });
              const entries = res.logs ?? [];
              const text = entries
                .map((e) => `[${e.level}] ${e.message}`)
                .join('\n');
              return { text: text || '(没有日志)' };
            });
          }
          const file = launcher.logPathFor(profile);
          if (!fs.existsSync(file)) return { text: `日志文件不存在：${file}` };
          const n = args?.lines ?? 80;
          const all = fs.readFileSync(file, 'utf8').split(/\r?\n/);
          return { text: all.slice(-n).join('\n') };
        },
      }),
    );
  },
  { inject: ['tools'] },
);
