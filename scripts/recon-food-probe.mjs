#!/usr/bin/env node
/**
 * recon-food-probe —— 食物自动化侦察第 1 步：探明当前游戏/桥接状态、地图信息、
 * get_game_info / list_zones / list_colonists 的真实返回结构。
 *
 * 只用有限超时的调用；任何 GABP_TIMEOUT 立刻退出（当作会话已废）。
 */
import fs from 'node:fs';
import path from 'node:path';
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor, WS } from '../src/launcher.js';

const OUT = path.join(WS, 'runs', 'food-recon');
fs.mkdirSync(OUT, { recursive: true });

const info = readBridgeInfo(logPathFor('vanilla'));
const { client } = await connectGabp(info, { requestTimeoutMs: 45_000 });
const call = (n, a, o) => client.callTool(n, a, o);
const t0 = () => Date.now();

const report = { at: new Date().toISOString(), steps: [] };
let dead = false;

async function step(name, fn) {
  const s = Date.now();
  process.stdout.write(`\n▶ ${name}\n`);
  try {
    const v = await fn();
    report.steps.push({ name, ok: true, ms: Date.now() - s });
    return v;
  } catch (err) {
    const isTimeout = err.code === 'GABP_TIMEOUT';
    report.steps.push({ name, ok: false, ms: Date.now() - s, error: err.message, timeout: isTimeout });
    console.log(`  ✗ ${err.message}`);
    if (isTimeout) {
      dead = true;
      throw Object.assign(new Error('BRIDGE_TIMEOUT'), { fatal: true });
    }
    return null;
  }
}

try {
  const bs = await step('rimbridge/get_bridge_status', () => call('rimbridge/get_bridge_status'));
  console.log('  ' + JSON.stringify(bs?.state ?? bs).slice(0, 600));

  const caps = await step('rimbridge/list_capabilities (全部, 只取元数据)', () =>
    call('rimbridge/list_capabilities', { limit: 400, includeParameters: false }));
  if (caps) {
    const ids = (caps.capabilities ?? caps.items ?? []).map((c) => c.id ?? c.capabilityId ?? c.name);
    console.log(`  能力数 ${ids.length}`);
    fs.writeFileSync(path.join(OUT, 'capabilities.json'), JSON.stringify(caps, null, 2), 'utf8');
  }

  const gi = await step('rimworld/get_game_info', () => call('rimworld/get_game_info'));
  if (gi) {
    console.log('  ' + JSON.stringify(gi).slice(0, 3000));
    fs.writeFileSync(path.join(OUT, 'get_game_info.json'), JSON.stringify(gi, null, 2), 'utf8');
  }

  const lua = await step('rimbridge/get_lua_reference', () => call('rimbridge/get_lua_reference'));
  if (lua) {
    console.log('  ' + JSON.stringify(lua).slice(0, 2000));
    fs.writeFileSync(path.join(OUT, 'lua_reference.json'), JSON.stringify(lua, null, 2), 'utf8');
  }

  const ui = await step('rimworld/get_ui_state', () => call('rimworld/get_ui_state'));
  if (ui) console.log('  ' + JSON.stringify(ui).slice(0, 1200));

  const zones = await step('rimworld/list_zones', () => call('rimworld/list_zones', { includeEmpty: true }));
  if (zones) {
    console.log('  ' + JSON.stringify(zones).slice(0, 2500));
    fs.writeFileSync(path.join(OUT, 'list_zones_empty.json'), JSON.stringify(zones, null, 2), 'utf8');
  }

  const cols = await step('rimworld/list_colonists', () => call('rimworld/list_colonists', { currentMapOnly: true }));
  if (cols) {
    console.log('  ' + JSON.stringify(cols).slice(0, 4000));
    fs.writeFileSync(path.join(OUT, 'list_colonists.json'), JSON.stringify(cols, null, 2), 'utf8');
  }

  const areas = await step('rimworld/list_areas', () => call('rimworld/list_areas'));
  if (areas) console.log('  ' + JSON.stringify(areas).slice(0, 2000));
} catch (e) {
  report.fatal = e.message;
  console.log(`\n[FATAL] ${e.message}`);
} finally {
  report.dead = dead;
  report.finishedAt = new Date().toISOString();
  fs.writeFileSync(path.join(OUT, 'probe1.json'), JSON.stringify(report, null, 2), 'utf8');
  client.close();
  console.log(`\n报告：${path.join(OUT, 'probe1.json')}${dead ? '  ⚠ 桥接可能已挂死' : ''}`);
}
