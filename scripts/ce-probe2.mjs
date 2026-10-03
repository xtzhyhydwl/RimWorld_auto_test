#!/usr/bin/env node
/** 探查：CE 技能等级的数字到底以什么元素形式存在。 */
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor } from '../src/launcher.js';
import * as ce from '../src/ce.js';

const info = readBridgeInfo(logPathFor('vanilla'));
const { client } = await connectGabp(info, { requestTimeoutMs: 25_000 });
const call = (n, a, o) => client.callTool(n, a, o);
const log = (...a) => console.log(...a);

await ce.ensureOpen(client, { timeoutMs: 15_000 });
await ce.openTab(client, 'Bio');
await new Promise((r) => setTimeout(r, 600));

// 拿原始 surface
const { surface } = await ce.ceSurface(client);
const els = surface.elements ?? [];
log(`CE 元素 ${els.length} 个`);

// 找 Shooting 那一行的 y
const shooting = els.find((e) => e.kind === 'label' && e.label === 'Shooting');
log(`Shooting label: y=${shooting?.screenRect?.y} x=${shooting?.screenRect?.x}`);
const y0 = shooting.screenRect.y;

log(`\n=== y 在 [${y0 - 12}, ${y0 + 12}] 内的所有元素（不过滤列） ===`);
for (const e of els
  .filter((e) => Math.abs((e.screenRect?.y ?? -999) - y0) <= 12)
  .sort((a, b) => (a.screenRect?.x ?? 0) - (b.screenRect?.x ?? 0))) {
  log(
    `  x=${String(Math.round(e.screenRect.x)).padStart(5)} y=${String(Math.round(e.screenRect.y)).padStart(5)} ` +
      `w=${String(Math.round(e.screenRect.width)).padStart(4)} h=${String(Math.round(e.screenRect.height)).padStart(3)} ` +
      `${e.actionable ? '可点' : '不可'} kind=${String(e.kind).padEnd(11)} src=${String(e.source).padEnd(24)} ` +
      `label=${JSON.stringify(e.label)} valueText=${JSON.stringify(e.valueText)}`,
  );
}

log('\n=== 技能列（x>=1340）所有元素按 y 排 ===');
for (const e of els
  .filter((e) => (e.screenRect?.x ?? 0) >= 1340)
  .sort((a, b) => (a.screenRect?.y ?? 0) - (b.screenRect?.y ?? 0))) {
  log(
    `  y=${String(Math.round(e.screenRect.y)).padStart(5)} x=${String(Math.round(e.screenRect.x)).padStart(5)} ` +
      `${e.actionable ? '可点' : '不可'} kind=${String(e.kind).padEnd(11)} label=${JSON.stringify(e.label)}`,
  );
}

log('\n=== 找所有纯数字的 label ===');
for (const e of els.filter((e) => e.kind === 'label' && /^\d+$/.test(String(e.label ?? '')))) {
  log(`  "${e.label}"  x=${Math.round(e.screenRect.x)} y=${Math.round(e.screenRect.y)}`);
}

client.close();
