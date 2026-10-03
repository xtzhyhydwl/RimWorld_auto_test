#!/usr/bin/env node
/** 验证 pawn 属性类 debug action 的层级结构与可执行性。 */
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor } from '../src/launcher.js';

const info = readBridgeInfo(logPathFor('vanilla'));
const { client } = await connectGabp(info, { requestTimeoutMs: 20_000 });
const call = (n, a, o) => client.callTool(n, a, o);
const log = (...a) => console.log(...a);

async function kids(p) {
  const r = await call('rimworld/get_debug_action', {
    path: p,
    includeChildren: true,
    includeHiddenChildren: true,
  });
  return r.children ?? r.action?.children ?? [];
}

const cols = (await call('rimworld/list_colonists', { currentMapOnly: true })).colonists ?? [];
const p0 = cols[0];
log(`测试对象: ${p0.fullName} (${p0.pawnId})\n`);

// 1. Max Skill 的层级
log('=== Max Skill... 的层级 ===');
const skillKids = await kids('Actions\\Max Skill...');
const shooting = skillKids.find((k) => /Shooting/i.test(k.label ?? ''));
log(`  ${skillKids.length} 个技能: ${skillKids.map((k) => k.label).join(', ')}`);
log(`  选中: ${shooting?.label} -> ${shooting?.path}`);
log(`  执行类型: ${shooting?.execution?.kind} / 需要 ${shooting?.execution?.requiredTargetKind}`);
const lvl = await kids(shooting.path);
log(`  第三层子项 ${lvl.length} 个: ${lvl.slice(0, 10).map((k) => k.label).join(', ')}`);

// 2. 执行
log('\n=== 执行 Max Skill (Shooting) ===');
try {
  const r = await call(
    'rimworld/execute_debug_action',
    { path: shooting.path, pawnId: p0.pawnId },
    { timeoutMs: 15_000 },
  );
  log(`  success=${r.success}  message=${r.message ?? ''}`);
} catch (e) {
  log(`  FAIL ${e.message}`);
}

// 3. 回读
await call('rimworld/select_pawn', { pawnId: p0.pawnId });
const sem = await call('rimworld/get_selection_semantics');
log('\n=== 回读 get_selection_semantics ===');
log(`  顶层键: ${Object.keys(sem).join(', ')}`);
log(`  inspectStrings 数量: ${(sem.inspectStrings ?? []).length}`);
log(`  响应中的非 operation 字段预览:`);
for (const [k, v] of Object.entries(sem)) {
  if (k === 'operation') continue;
  const s = JSON.stringify(v);
  log(`    ${k}: ${s ? s.slice(0, 200) : v}`);
}

// 4. Give Trait
log('\n=== 试 Give Trait...\\nudist ===');
const traits = await kids('Actions\\Give Trait...');
const nudist = traits.find((k) => /nudist/i.test(k.label ?? ''));
log(`  ${nudist?.label} -> ${nudist?.path} [${nudist?.execution?.kind}/${nudist?.execution?.requiredTargetKind ?? '-'}]`);
try {
  const r = await call(
    'rimworld/execute_debug_action',
    { path: nudist.path, pawnId: p0.pawnId },
    { timeoutMs: 15_000 },
  );
  log(`  success=${r.success}`);
} catch (e) {
  log(`  FAIL ${e.message}`);
}
await call('rimworld/select_pawn', { pawnId: p0.pawnId });
const sem2 = await call('rimworld/get_selection_semantics');
log(`  之后 inspectStrings=${JSON.stringify((sem2.inspectStrings ?? []).slice(0, 5))}`);

// 5. 拿一张选中面板的截图当证据
await call('rimworld/take_screenshot', { suppressMessage: true, fileName: 'pawn_after_debug' });
log('\n截图: profiles/vanilla/Screenshots/pawn_after_debug.png');

client.close();
