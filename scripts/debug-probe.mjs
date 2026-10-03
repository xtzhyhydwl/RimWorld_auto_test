#!/usr/bin/env node
/**
 * 验证：用 debug action 生成 pawn（替代会崩的 spawn_thing）。
 * 同时打印 Actions 下全部顶层动作，作为原版控制台的地图。
 */
import fs from 'node:fs';
import path from 'node:path';
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor, WS } from '../src/launcher.js';

const info = readBridgeInfo(logPathFor('vanilla'));
const { client } = await connectGabp(info, { requestTimeoutMs: 20_000 });
const call = (n, a, o) => client.callTool(n, a, o);
const log = (...a) => console.log(...a);

const st = (await call('rimbridge/get_bridge_status')).state;
log(`状态: ${st.programState} hasCurrentGame=${st.hasCurrentGame}`);
const cols = (await call('rimworld/list_colonists', { currentMapOnly: true })).colonists ?? [];
const c0 = cols[0];
log(`殖民者 ${cols.map((c) => c.name).join(', ')} @(${c0.position.x},${c0.position.z})\n`);

// ── 1. 打印 Actions 的全部顶层动作 ────────────────────────────
log('═══ Actions 下的顶层动作 ═══');
const roots = await call('rimworld/list_debug_action_roots', { includeHidden: true });
const actionsRoot = (roots.roots ?? []).find((r) => r.path === 'Actions');
const actions = await call('rimworld/get_debug_action', {
  path: 'Actions',
  includeChildren: true,
  includeHiddenChildren: true,
});
const list = (actions.children ?? actions.action?.children ?? []).slice();
log(`共 ${list.length} 个顶层动作`);
const lines = list
  .map((n) => {
    const k = n.execution?.kind ?? '?';
    const t = n.execution?.requiredTargetKind ?? '-';
    const c = n.childCount ?? 0;
    return `${String(c).padStart(6)}  [${k}/${t}]  ${n.label}\n            ${n.path}`;
  })
  .sort();
fs.writeFileSync(path.join(WS, 'runs', 'debug-actions-top.md'), '# Actions 顶层动作\n\n```\n' + lines.join('\n') + '\n```\n', 'utf8');
log(lines.slice(0, 60).join('\n'));
log(`…（完整列表写入 runs/debug-actions-top.md）\n`);

// ── 2. 找 Spawn Pawn... 下的动物 ─────────────────────────────
log('═══ 找 Spawn Pawn... 下的动物叶子 ═══');
const sp = await call('rimworld/get_debug_action', {
  path: 'Actions\\Spawn Pawn...',
  includeChildren: true,
  includeHiddenChildren: true,
});
const spKids = sp.children ?? sp.action?.children ?? [];
log(`  Spawn Pawn... 有 ${spKids.length} 个子项`);
const animals = spKids.filter((k) => /Thrumbo|Muffalo|Boomrat|Boomalope|Elephant|Megasloth|Bear|Wolf|Cougar/i.test(k.label ?? ''));
log(`  其中的动物: ${animals.map((a) => `${a.label}[${a.execution?.kind}/${a.execution?.requiredTargetKind ?? '-'}]`).join(', ')}`);

// ── 3. 实际生成一只 ───────────────────────────────────────────
const target = animals.find((a) => /Thrumbo/i.test(a.label ?? '')) ?? spKids.find((k) => /Muffalo/i.test(k.label ?? '')) ?? spKids[0];
log(`\n═══ 尝试生成「${target?.label}」 ═══`);
log(`  路径: ${target?.path}`);
log(`  执行类型: ${target?.execution?.kind}  需要目标: ${target?.execution?.requiredTargetKind ?? '不需要'}`);

const bx = c0.position.x + 8;
const bz = c0.position.z + 4;
const args = { path: target.path };
if (target.execution?.requiredTargetKind === 'map') {
  args.x = bx;
  args.z = bz;
}
const t0 = Date.now();
try {
  const r = await call('rimworld/execute_debug_action', args, { timeoutMs: 15_000 });
  log(`  执行: success=${r.success} (${Date.now() - t0}ms) ${r.message ?? ''}`);
  log(`  ${JSON.stringify(r).slice(0, 300)}`);
} catch (e) {
  log(`  ✗ ${e.message}`);
}

// ── 4. 回读确认 ───────────────────────────────────────────────
await call('rimbridge/wait_for_long_event_idle', { timeoutMs: 30_000 });
const area = await call('rimworld/get_cells_info', { x: bx - 8, z: bz - 8, width: 20, height: 20 });
const kinds = [...new Set((area.cells ?? []).flatMap((c) => (c.things ?? []).map((t) => t.label)))];
log(`\n═══ 落点周围 20x20 现在有 ═══`);
log(`  ${kinds.join(' | ')}`);

await call('rimworld/frame_cell_rect', { x: bx, z: bz, width: 20, height: 20, marginCells: 8 });
const shot = await call('rimworld/take_screenshot', { suppressMessage: true, fileName: 'debug_spawn_pawn' });
log(`  截图: ${shot.path}`);

// ── 5. 顺手验证几个「人物 · 属性」类动作是否能拿到叶子 ────────
log('\n═══ 关键属性类动作的叶子样本 ═══');
for (const label of ['Set Skill...', 'Max Skill...', 'Give Trait...', 'Add Hediff...', 'Set Backstory...', 'Add Gene...']) {
  try {
    const res = await call('rimworld/get_debug_action', {
      path: `Actions\\${label}`,
      includeChildren: true,
      includeHiddenChildren: true,
    });
    const kids = res.children ?? res.action?.children ?? [];
    log(`  ${label.padEnd(20)} ${String(kids.length).padStart(5)} 子项  例: ${kids.slice(0, 4).map((k) => k.label).join(' / ')}`);
  } catch (e) {
    log(`  ${label}: ✗ ${e.message}`);
  }
}

client.close();
log('\n完成');
