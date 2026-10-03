/**
 * 诊断：样板建造为什么"48 个槽位全解决不了"。
 *
 * 已知：选址成功、库存有 Steel 1170 / WoodLog 300、模板里有 wall/door 两个角色。
 * 所以问题在中间某一环 —— 这个脚本把每一环的中间量都打出来，**不靠猜**。
 *
 * ⚠ 只附着到已在运行的游戏。
 * 用法：node scripts/diagnose-template-build.mjs
 */
import fs from 'node:fs';
import path from 'node:path';
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor, WS } from '../src/launcher.js';

const PROFILE = 'vanilla';
const CALL = 120_000;
const OUT = path.join(WS, 'runs', 'diagnose-template-build');

let client = null;
const log = (...a) => console.log(...a);

async function call(name, args = {}, timeoutMs = CALL) {
  return client.callTool(name, args, { timeoutMs });
}

async function ready() {
  const g = await call('rimworld/get_game_info');
  if (g?.status !== 'game_loaded') return { ok: false, stage: 'not_loaded' };
  if ((g?.mapCount ?? 0) <= 0) return { ok: false, stage: 'no_map' };
  const ui = await call('rimworld/get_ui_state');
  if (ui?.programState !== 'Playing') return { ok: false, stage: 'programState=' + ui?.programState };
  if ((g?.ticksGame ?? 0) <= 0) return { ok: false, stage: 'ticks=0' };
  return { ok: true, stage: 'playing' };
}

async function main() {
  const info = readBridgeInfo(logPathFor(PROFILE));
  if (!info) throw new Error('没有桥接信息 —— 先 node src/cli.js start');
  const conn = await connectGabp(info, { requestTimeoutMs: CALL });
  client = conn.client;
  log(`桥接 ${info.host}:${info.port}`);

  const out = { at: new Date().toISOString() };

  try {
    let st = await ready();
    if (st.stage === 'not_loaded' || st.stage === 'no_map') {
      log(`开调试殖民地（阶段 ${st.stage}，超时预期）...`);
      await call('rimworld/start_debug_game_ready',
        { readiness: 'mapData', pauseIfNeeded: true, timeoutMs: 240_000 }, 280_000,
      ).catch((e) => log('  ' + e.message.split('\n')[0]));
    }
    for (let i = 0; i < 120; i++) {
      st = await ready();
      if (st.ok) break;
      if (i % 10 === 0) log(`  等待中... 阶段=${st.stage}`);
      await new Promise((r) => setTimeout(r, 2000));
    }
    log(`地图就绪：${st.ok}（${st.stage}）`);
    out.mapReady = st;

    // 顺手解锁所有物资（M1 的行为），排除"禁取"这个变量
    const unlock = await call('foodprobe/run_setup', {}, 60000).catch((e) => ({ message: e.message }));
    log(`解锁物资：${unlock?.result || unlock?.message}`);
    out.unlock = unlock?.result || unlock?.message;

    // ★ 诊断
    log('\n=== 诊断中间量 ===');
    const dbg = await call('foodprobe/template_plan_debug', {}, 90000);
    out.debug = dbg;

    if (dbg?.success !== true) {
      log(`诊断调用失败：${dbg?.message}`);
      log(JSON.stringify(dbg).slice(0, 600));
    } else {
      log(`样板：${dbg.templateId}  ${dbg.size?.w}x${dbg.size?.h}`);
      log(`角色表：${JSON.stringify(dbg.roles)}`);
      log(`可用材料：${JSON.stringify(dbg.availableMaterials)}`);
      log(`网格 ok=${dbg.gridOk}  ${JSON.stringify(dbg.grid)}`);
      log(`未解决样本：`);
      for (const u of dbg.unresolvedSample || []) log(`   - ${u}`);
    }
  } finally {
    client.close();
  }

  fs.mkdirSync(OUT, { recursive: true });
  fs.writeFileSync(path.join(OUT, 'report.json'), JSON.stringify(out, null, 2), 'utf8');
  log(`\n→ ${path.join(OUT, 'report.json')}`);
}

main().catch((e) => {
  console.error('\n[错误] ' + e.message);
  process.exitCode = 1;
});
