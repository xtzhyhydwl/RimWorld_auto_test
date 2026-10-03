/**
 * 验证 play_for 秒退的根因：某个 forcePause 的窗口开着（实测是 MainTabWindow_Menu）。
 *
 * 假设：close_main_tab 关掉它之后，play_for 立刻恢复。
 *
 * 输出：runs/food-recon/playfor-rootcause.json
 */
import fs from 'node:fs';
import path from 'node:path';
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor, WS } from '../src/launcher.js';

const PROFILE = 'vanilla';
const CALL_TIMEOUT = 120_000;

async function main() {
  const info = readBridgeInfo(logPathFor(PROFILE));
  const { client } = await connectGabp(info, { requestTimeoutMs: CALL_TIMEOUT });

  const call = async (name, args = {}, timeoutMs = CALL_TIMEOUT) => {
    const t0 = Date.now();
    const res = await client.callTool(name, args, { timeoutMs });
    return { res, ms: Date.now() - t0 };
  };

  const log = [];
  const say = (label, data) => {
    log.push({ label, data });
    console.log(`\n### ${label}\n` + JSON.stringify(data).slice(0, 900));
  };

  const uiPause = async () => {
    const ui = await call('rimworld/get_ui_state');
    return {
      windowsForcePause: ui.res?.windowsForcePause,
      forcePauseWindows: (ui.res?.windows || []).filter((w) => w.forcePause).map((w) => `${w.type}(${w.layer})`),
      openMainTabId: ui.res?.openMainTabId,
      openMainTabLabel: ui.res?.openMainTabLabel,
      windowCount: ui.res?.windowCount,
    };
  };

  const probe = async (label) => {
    const r = await call('rimworld/play_for', { durationMs: 2000, speed: 'Ultrafast', forceRequestedSpeed: true }, 60_000);
    const ticks = r.res?.advancedTicks ?? 0;
    const ok = ticks > 500;
    say(`${label} -> ${ok ? '✅ OK' : '❌ FAIL'}`, {
      advancedTicks: ticks, elapsedMs: r.res?.elapsedMs, initiallyPaused: r.res?.initiallyPaused, message: r.res?.message,
    });
    return { ok, ticks };
  };

  try {
    // 0. 先看看有没有 main tab 相关的工具
    const tools = await client.listTools();
    const tabTools = tools.filter((t) => /main_tab|main-tab|window/i.test(t.name)).map((t) => t.name);
    say('0. 相关工具', { tabTools });

    say('1. 修复前 UI', await uiPause());
    const before = await probe('2. 修复前 play_for');

    // 3. 关掉主菜单标签页
    const close = await call('rimworld/close_main_tab', { tabId: 'main-tab:Menu' });
    say('3. close_main_tab(Menu)', { status: close.res?.status ?? close.res, success: close.res?.success, message: close.res?.message });

    say('4. 修复后 UI', await uiPause());
    const after = await probe('5. 修复后 play_for');

    // 6. 复查一次，确认不是一次性的
    const again = await probe('6. 再次 play_for');

    // 7. 对照实验：重新打开 Menu 标签页，看是否再次卡死
    const open = await call('rimworld/open_main_tab', { tabId: 'main-tab:Menu' });
    say('7. 重新 open_main_tab(Menu)', { status: open.res?.status ?? open.res, success: open.res?.success });
    say('8. 重开后的 UI', await uiPause());
    const regress = await probe('9. 重开后 play_for（预期再次 FAIL）');

    // 8. 收尾：再关掉，保证后续测试环境干净
    await call('rimworld/close_main_tab', { tabId: 'main-tab:Menu' });
    const final = await probe('10. 收尾关闭后 play_for');

    const conclusion = {
      hypothesis: 'forcePause 窗口（MainTabWindow_Menu）开着会让 play_for 无法解除暂停',
      beforeFix: before,
      afterClose: after,
      repeatAfterClose: again,
      afterReopen_expectFail: regress,
      afterFinalClose: final,
      confirmed:
        before.ok === false && after.ok === true && again.ok === true && regress.ok === false && final.ok === true,
      ticksPerSec_afterFix: after.ticks > 0 ? Math.round(after.ticks / 2) : null,
    };
    say('结论', conclusion);

    const file = path.join(WS, 'runs', 'food-recon', 'playfor-rootcause.json');
    fs.mkdirSync(path.dirname(file), { recursive: true });
    fs.writeFileSync(file, JSON.stringify({ conclusion, log }, null, 2), 'utf8');
    console.log('\n→ ' + file);
  } finally {
    client.close();
  }
}

main().catch((e) => {
  console.error('FAILED: ' + e.message);
  process.exitCode = 1;
});
