/**
 * RimWorld mod 冒烟测试闭环。
 *
 * 一条命令跑完：启动隔离实例 → 连桥 → 开调试殖民地 → 推进 N tick
 *               → 收集新产生的日志 → 快照告警/信件 → 截图 → 断言 → 出报告
 *
 * 报告落盘在 runs/<runId>/：report.json（机器可读）+ report.md（人看）+ 截图副本。
 *
 * 设计原则：
 *  - 隔离：一切都在 profiles/<name>/ 里，绝不碰主力存档
 *  - 只认「本次运行新产生的」错误（用日志游标 afterSequence 卡基线）
 *  - 每步都留证据，失败也出报告，不用猜
 */
import fs from 'node:fs';
import path from 'node:path';
import { connectGabp } from './gabp.js';
import {
  WS,
  startGame,
  stopGame,
  waitForBridge,
  logPathFor,
  tailFile,
  profileDir,
  gameRoot,
} from './launcher.js';

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

function runIdNow(label) {
  const t = new Date();
  const pad = (n) => String(n).padStart(2, '0');
  const stamp =
    `${t.getFullYear()}${pad(t.getMonth() + 1)}${pad(t.getDate())}-` +
    `${pad(t.getHours())}${pad(t.getMinutes())}${pad(t.getSeconds())}`;
  return `${stamp}_${label}`;
}

/** 一次断言 */
class Checks {
  constructor() {
    this.items = [];
  }
  add(name, ok, detail) {
    this.items.push({ name, ok: Boolean(ok), detail: detail ?? null });
    const mark = ok ? '  ✓' : '  ✗';
    console.log(`${mark} ${name}${detail && !ok ? `  —— ${detail}` : ''}`);
    return Boolean(ok);
  }
  get passed() {
    return this.items.every((c) => c.ok);
  }
  get failed() {
    return this.items.filter((c) => !c.ok);
  }
}

export async function runSmoke({
  profile = 'vanilla',
  ticks = 300,
  launch = false,
  keepRunning = false,
  visible = true,
  doorstop = false,
  skipStartGame = false,
  label = 'smoke',
  log = console.log,
} = {}) {
  const runId = runIdNow(label);
  const runDir = path.join(WS, 'runs', runId);
  fs.mkdirSync(runDir, { recursive: true });

  const report = {
    runId,
    startedAt: new Date().toISOString(),
    config: { profile, ticks, launch, keepRunning, doorstop },
    environment: {
      gameRoot: gameRoot(),
      profileDir: profileDir(profile),
      logFile: logPathFor(profile),
      node: process.version,
    },
    steps: [],
    checks: [],
    passed: false,
    error: null,
  };

  const checks = new Checks();
  const step = async (name, fn) => {
    const t0 = Date.now();
    log(`\n▶ ${name}`);
    try {
      const value = await fn();
      report.steps.push({ name, ok: true, durationMs: Date.now() - t0 });
      return value;
    } catch (err) {
      report.steps.push({
        name,
        ok: false,
        durationMs: Date.now() - t0,
        error: err.message,
      });
      throw err;
    }
  };

  let client = null;
  let launched = null;

  try {
    if (launch) {
      launched = await step('启动隔离测试实例', async () => {
        const rec = await startGame({ profile, visible, doorstop });
        log(`   PID=${rec.pid}  存档目录=${rec.profileDir}`);
        return rec;
      });
      report.environment.gamePid = launched.pid;
    }

    const info = await step('等待游戏内桥接就绪', async () => {
      const bridge = await waitForBridge({
        profile,
        pid: launched?.pid,
        onTick: () => {},
      });
      log(`   ${bridge.host}:${bridge.port}  (${(bridge.waitedMs / 1000).toFixed(1)}s)`);
      report.environment.bridge = { host: bridge.host, port: bridge.port };
      return bridge;
    });

    await step('GABP 握手', async () => {
      const conn = await connectGabp(info, { requestTimeoutMs: 600_000 });
      client = conn.client;
      log(`   agentId=${conn.welcome?.agentId}  应用=${conn.welcome?.app?.name}`);
      report.environment.welcome = conn.welcome;
      checks.add('桥接握手成功', Boolean(conn.welcome));
      return conn;
    });

    const call = (name, args, opts) => client.callTool(name, args, opts);

    const status0 = await step('读取桥接状态', async () => {
      const s = await call('rimbridge/get_bridge_status');
      return s;
    });
    const st0 = status0.state ?? {};
    log(`   programState=${st0.programState}  hasCurrentGame=${st0.hasCurrentGame}`);

    if (!skipStartGame && st0.inEntryScene) {
      await step('启动调试殖民地', async () => {
        const r = await call('rimworld/start_debug_game_ready', {
          readiness: 'mapData',
          pauseIfNeeded: true,
          timeoutMs: 300_000,
        });
        log(`   ${r.message ?? r.wait?.message ?? 'ok'}`);
        checks.add('调试殖民地已启动', r.success !== false, r.message);
        return r;
      });
    } else if (st0.hasCurrentGame) {
      log('\n(游戏里已有存档，跳过开新殖民地)');
      checks.add('使用已加载的存档', true);
    }

    await step('等待地图就绪', async () => {
      // 只用 readiness=mapData 不够：那时地图可能还在初始化，
      // 立刻 step_game_ticks 会返回 status=failed、推进 0 个 tick（实测踩过）。
      const w = await call(
        'rimbridge/wait_for_game_loaded',
        { readiness: 'currentMap', pauseIfNeeded: true, timeoutMs: 180_000 },
        { timeoutMs: 210_000 },
      );
      log(`   ${w.message ?? ''}  (${w.elapsedMs ?? '?'}ms)`);
      checks.add('地图已完全就绪', w.satisfied !== false, w.blockingReason);
      return w;
    });

    await step('等待地图空闲', async () => {
      const r = await call('rimbridge/wait_for_long_event_idle', { timeoutMs: 120_000 });
      return r;
    });

    const colonists = await step('读取殖民者', async () => {
      const r = await call('rimworld/list_colonists', { currentMapOnly: true });
      const list = r.colonists ?? [];
      log(`   ${list.length} 人：${list.map((c) => c.name).join(', ')}`);
      checks.add('殖民地至少有 1 个殖民者', list.length >= 1, `实际 ${list.length}`);
      return r;
    });
    report.colonists = (colonists.colonists ?? []).map((c) => ({
      pawnId: c.pawnId,
      name: c.name,
      fullName: c.fullName,
      position: c.position,
      job: c.job,
      downed: c.downed,
      dead: c.dead,
    }));

    // 日志基线：只关心这之后的错误
    const cursor = (await call('rimbridge/get_bridge_status')).latestLogSequence ?? 0;
    log(`   日志游标基线 = ${cursor}`);

    await step(`推进 ${ticks} tick`, async () => {
      // 地图刚初始化完时第一次推进可能返回 failed（推进 0 tick），
      // 等一下就正常。所以这里带重试，不要一次失败就判死。
      let r = null;
      for (let attempt = 1; attempt <= 3; attempt++) {
        r = await call(
          'rimworld/step_game_ticks',
          { ticks, pauseFirst: true, timeoutMs: 600_000 },
          { timeoutMs: 900_000 },
        );
        if (r.status === 'completed') break;
        log(`   第 ${attempt} 次未完成（status=${r.status}，推进 ${r.advancedTicks}），等地图就绪后重试…`);
        await call(
          'rimbridge/wait_for_game_loaded',
          { readiness: 'currentMap', pauseIfNeeded: true, timeoutMs: 60_000 },
          { timeoutMs: 90_000 },
        ).catch(() => {});
        await new Promise((res) => setTimeout(res, 800));
      }
      log(
        `   游戏 tick ${r.startTicksGame} → ${r.endTicksGame}（+${r.advancedTicks}），` +
          `跨越 ${r.advancedFrames} 帧，${r.status}`,
      );
      checks.add('推进的 tick 数与请求一致', r.advancedTicks === ticks, `请求 ${ticks}，实际 ${r.advancedTicks}`);
      checks.add('推进状态为 completed', r.status === 'completed', r.status);
      report.ticks = {
        requested: ticks,
        advanced: r.advancedTicks,
        startTicksGame: r.startTicksGame,
        endTicksGame: r.endTicksGame,
        advancedFrames: r.advancedFrames,
        status: r.status,
      };
      return r;
    });

    const logs = await step('收集本次运行新产生的日志', async () => {
      const r = await call('rimbridge/list_logs', {
        minimumLevel: 'warning',
        limit: 200,
        afterSequence: cursor,
      });
      const entries = r.logs ?? [];
      const errors = entries.filter((e) => /error|fatal/i.test(e.level ?? ''));
      const warnings = entries.filter((e) => /warn/i.test(e.level ?? ''));
      log(`   新增 ${entries.length} 条（错误 ${errors.length}，警告 ${warnings.length}）`);
      for (const e of entries.slice(0, 10)) {
        log(`     [${e.level}] ${String(e.message ?? '').slice(0, 150)}`);
      }
      checks.add('本次运行没有新的错误日志', errors.length === 0, `${errors.length} 条错误`);
      return { entries, errors, warnings };
    });
    report.logs = {
      total: logs.entries.length,
      errors: logs.errors.map((e) => ({ level: e.level, message: e.message })),
      warnings: logs.warnings.map((e) => ({ level: e.level, message: e.message })),
    };

    await step('快照告警与信件', async () => {
      const alerts = await call('rimworld/list_alerts', { limit: 40 });
      const letters = await call('rimworld/list_letters', { limit: 40 });
      report.alerts = (alerts.alerts ?? []).map((a) => ({
        label: a.label,
        severity: a.severity,
      }));
      report.letters = (letters.letters ?? []).map((l) => ({
        label: l.label,
        defName: l.defName,
      }));
      log(`   告警 ${report.alerts.length} 条，信件 ${report.letters.length} 封`);
      return { alerts, letters };
    });

    await step('截图取证', async () => {
      const r = await call('rimworld/take_screenshot', {
        suppressMessage: true,
        fileName: `${runId}_full`,
      });
      const src = r.path;
      let copied = null;
      if (src && fs.existsSync(src)) {
        copied = path.join(runDir, path.basename(src));
        fs.copyFileSync(src, copied);
      }
      log(`   ${src}  (${r.sizeBytes ?? '?'} 字节)`);
      checks.add('截图已生成', Boolean(src) && fs.existsSync(src), String(src));
      report.screenshot = { source: src, copied, sizeBytes: r.sizeBytes ?? null };
      return r;
    });

    checks.add('整轮没有致命异常', true);
  } catch (err) {
    report.error = err.message;
    log(`\n[中断] ${err.message}`);
    if (err.stdout) log(err.stdout.slice(-2000));
  } finally {
    if (client) client.close();
    if (launched && !keepRunning) {
      try {
        const r = await stopGame({ profile });
        report.teardown = r;
        log(`\n游戏已关闭：${JSON.stringify(r)}`);
      } catch (e) {
        report.teardown = { error: e.message };
      }
    }
  }

  report.checks = checks.items;
  report.passed = checks.passed && !report.error;
  report.finishedAt = new Date().toISOString();

  fs.writeFileSync(path.join(runDir, 'report.json'), JSON.stringify(report, null, 2), 'utf8');
  fs.writeFileSync(path.join(runDir, 'report.md'), renderMarkdown(report), 'utf8');

  log('');
  log('─'.repeat(60));
  log(report.passed ? '冒烟测试：通过 ✓' : '冒烟测试：失败 ✗');
  for (const c of checks.items) log(`  ${c.ok ? '✓' : '✗'} ${c.name}`);
  log(`报告：${path.join(runDir, 'report.json')}`);
  log('─'.repeat(60));

  return report;
}

function renderMarkdown(r) {
  const L = [];
  L.push(`# RimWorld 冒烟测试报告`);
  L.push('');
  L.push(`- 运行 ID：\`${r.runId}\``);
  L.push(`- 开始：${r.startedAt}`);
  L.push(`- 结束：${r.finishedAt}`);
  L.push(`- 结论：**${r.passed ? '通过 ✓' : '失败 ✗'}**`);
  if (r.error) L.push(`- 中断原因：${r.error}`);
  L.push('');
  L.push(`## 环境`);
  L.push('');
  L.push('| 项 | 值 |');
  L.push('| --- | --- |');
  L.push(`| 游戏目录 | \`${r.environment.gameRoot ?? '(默认)'}\` |`);
  L.push(`| 隔离存档目录 | \`${r.environment.profileDir}\` |`);
  L.push(`| 游戏日志 | \`${r.environment.logFile}\` |`);
  if (r.environment.gamePid) L.push(`| 游戏 PID | ${r.environment.gamePid} |`);
  if (r.environment.bridge) {
    L.push(`| 桥接 | ${r.environment.bridge.host}:${r.environment.bridge.port} |`);
  }
  if (r.environment.welcome?.app) {
    L.push(`| 桥接版本 | ${r.environment.welcome.app.name} ${r.environment.welcome.app.version} |`);
  }
  L.push('');
  L.push(`## 断言`);
  L.push('');
  L.push('| 结果 | 断言 | 详情 |');
  L.push('| --- | --- | --- |');
  for (const c of r.checks) {
    L.push(`| ${c.ok ? '✓' : '✗'} | ${c.name} | ${c.detail ?? ''} |`);
  }
  L.push('');
  if (r.colonists?.length) {
    L.push(`## 殖民者（${r.colonists.length}）`);
    L.push('');
    L.push('| 名字 | 位置 | 当前工作 | 倒地 | 死亡 |');
    L.push('| --- | --- | --- | --- | --- |');
    for (const c of r.colonists) {
      L.push(
        `| ${c.fullName ?? c.name} | (${c.position?.x}, ${c.position?.z}) | ${c.job ?? '-'} | ` +
          `${c.downed ? '是' : '否'} | ${c.dead ? '是' : '否'} |`,
      );
    }
    L.push('');
  }
  if (r.ticks) {
    L.push(`## 时间推进`);
    L.push('');
    L.push(
      `- 请求 ${r.ticks.requested} tick，实际推进 ${r.ticks.advanced} tick（状态 ${r.ticks.status}）`,
    );
    L.push(`- 游戏 tick：${r.ticks.startTicksGame} → ${r.ticks.endTicksGame}`);
    L.push(`- 跨越 Unity 帧：${r.ticks.advancedFrames}`);
    L.push('');
  }
  L.push(`## 日志`);
  L.push('');
  L.push(`- 本次运行新增：${r.logs?.total ?? 0} 条`);
  L.push(`- 错误：${r.logs?.errors?.length ?? 0} 条`);
  L.push(`- 警告：${r.logs?.warnings?.length ?? 0} 条`);
  if (r.logs?.errors?.length) {
    L.push('');
    L.push('### 错误明细');
    L.push('');
    for (const e of r.logs.errors) L.push(`- \`[${e.level}]\` ${e.message}`);
  }
  if (r.logs?.warnings?.length) {
    L.push('');
    L.push('### 警告明细');
    L.push('');
    for (const e of r.logs.warnings.slice(0, 30)) L.push(`- \`[${e.level}]\` ${e.message}`);
  }
  L.push('');
  if (r.alerts?.length) {
    L.push(`## 活动告警（${r.alerts.length}）`);
    L.push('');
    for (const a of r.alerts) L.push(`- ${a.label}`);
    L.push('');
  }
  if (r.screenshot) {
    L.push(`## 截图`);
    L.push('');
    L.push(`- 源文件：\`${r.screenshot.source}\``);
    if (r.screenshot.copied) {
      L.push('');
      L.push(`![截图](${path.basename(r.screenshot.copied)})`);
    }
    L.push('');
  }
  L.push(`## 步骤耗时`);
  L.push('');
  L.push('| 步骤 | 结果 | 耗时 |');
  L.push('| --- | --- | --- |');
  for (const s of r.steps) {
    L.push(`| ${s.name} | ${s.ok ? '✓' : '✗'} | ${(s.durationMs / 1000).toFixed(1)}s |`);
  }
  L.push('');
  return L.join('\n');
}
