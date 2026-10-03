#!/usr/bin/env node
/**
 * 控制台/调试技巧巡览。
 *
 * 目的：在一个临时调试殖民地里，把 RimBridgeServer 各类能力系统地试一遍，
 * 记录「什么能用、什么有坑、正确姿势是什么」，为以后固化成 skill 打底。
 *
 * 每一步都独立容错：失败不中断，最后出一份 markdown 经验总结。
 * 用法：node scripts/console-tour.mjs
 */
import path from 'node:path';
import fs from 'node:fs';
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor, WS } from '../src/launcher.js';
import { clickRowByLabel, describeSurfaces, layout } from '../src/ui.js';

const stamp = new Date().toISOString().replace(/[:.]/g, '-');
const outDir = path.join(WS, 'runs', `tour-${stamp}`);
fs.mkdirSync(outDir, { recursive: true });

const info = readBridgeInfo(logPathFor('vanilla'));
if (!info) throw new Error('找不到桥接，先启动游戏。');
// 默认 60 秒超时。桥接把调用排在游戏主线程上：某个调用挂住，后续全会挂。
// 需要长时间的操作在调用处单独传 timeoutMs。
const { client } = await connectGabp(info, { requestTimeoutMs: 60_000 });
const call = (n, a, o) => client.callTool(n, a, o);

const notes = [];
let shotN = 0;
const log = (...a) => console.log(...a);

function note(topic, finding, evidence) {
  notes.push({ topic, finding, evidence: evidence ?? null });
  log(`    📝 ${finding}`);
}

async function shot(label) {
  shotN += 1;
  try {
    const r = await call('rimworld/take_screenshot', {
      suppressMessage: true,
      fileName: `tour_${String(shotN).padStart(2, '0')}_${label}`,
    });
    if (r?.path && fs.existsSync(r.path)) fs.copyFileSync(r.path, path.join(outDir, path.basename(r.path)));
    return r?.path ?? null;
  } catch (e) {
    return null;
  }
}

const results = [];
async function experiment(title, fn) {
  log(`\n═══ ${title} ═══`);
  try {
    const r = await fn();
    results.push({ title, ok: true });
    return r;
  } catch (err) {
    log(`  ✗ ${err.message}`);
    results.push({ title, ok: false, error: err.message });
    note(title, `失败：${err.message}`);
    return null;
  }
}

// ═══════════════════════════════════════════════════════════
await experiment('1. 开一个调试殖民地', async () => {
  const st = (await call('rimbridge/get_bridge_status')).state;
  log(`  当前 programState=${st.programState} inEntryScene=${st.inEntryScene}`);
  if (st.inEntryScene) {
    const r = await call(
      'rimworld/start_debug_game_ready',
      { readiness: 'mapData', pauseIfNeeded: true, timeoutMs: 300_000 },
      { timeoutMs: 330_000 },
    );
    log(`  ${r.message}`);
    note('开调试殖民地', '主菜单状态下用 start_debug_game_ready(readiness=mapData, pauseIfNeeded=true) 一步到位，约 4 秒。');
  } else {
    log('  已在游戏中，跳过');
  }
  await call('rimbridge/wait_for_long_event_idle', { timeoutMs: 120_000 });
  await call('rimbridge/wait_for_game_loaded', { readiness: 'currentMap', pauseIfNeeded: true, timeoutMs: 60_000 });
  const cols = (await call('rimworld/list_colonists', { currentMapOnly: true })).colonists ?? [];
  log(`  殖民者 ${cols.length} 人: ${cols.map((c) => c.name).join(', ')}`);
  await shot('colony_start');
  return cols;
});

// ═══════════════════════════════════════════════════════════
await experiment('2. 观察地形与地图格', async () => {
  const cols = (await call('rimworld/list_colonists', { currentMapOnly: true })).colonists ?? [];
  const c0 = cols[0];
  const cell = await call('rimworld/get_cell_info', { x: c0.position.x, z: c0.position.z });
  const ci = cell.cell;
  log(`  殖民者所在格: terrain=${ci.terrainDefName} walkable=${ci.walkable} 物品=${ci.thingCount}`);
  log(`  things: ${(ci.things ?? []).map((t) => t.label).join(' | ')}`);
  note(
    '读地图格',
    'get_cell_info 的字段是 cell.terrainDefName / cell.walkable / cell.things[]（不是 terrain）。' +
      '批量看用 get_cells_info(x,z,width,height)，上限 1024 格。',
  );

  const area = await call('rimworld/get_cells_info', {
    x: c0.position.x - 10,
    z: c0.position.z - 10,
    width: 20,
    height: 20,
  });
  const kinds = [...new Set((area.cells ?? []).flatMap((c) => (c.things ?? []).map((t) => t.defName)))];
  log(`  周围 20x20 有 ${kinds.length} 种物体: ${kinds.slice(0, 12).join(', ')}`);
  note('批量读地图', `get_cells_info 返回 cells[]，每格含 things[]。20x20=400 格很轻，1024 格是上限。`);
  return cell;
});

// ═══════════════════════════════════════════════════════════
await experiment('3. 上帝模式 + 用建筑师设计器造房间', async () => {
  await call('rimworld/set_god_mode', { enabled: true });
  log('  上帝模式已开');

  const cats = (await call('rimworld/list_architect_categories')).categories ?? [];
  log(`  建筑师分类 ${cats.length} 个: ${cats.map((c) => c.categoryDefName).join(', ')}`);

  // 找一个含建造项的类别，把它的设计器列出来
  let found = null;
  for (const cat of cats) {
    const d = await call('rimworld/list_architect_designators', { categoryId: cat.id });
    const list = d.designators ?? [];
    const wall = list.find((x) => /^wall$/i.test(x.label ?? '') || /wall/i.test(x.label ?? ''));
    if (wall) {
      found = { cat, list, wall };
      break;
    }
  }
  if (!found) throw new Error('没找到墙的设计器');
  log(`  类别「${found.cat.label}」有 ${found.list.length} 个设计器`);
  log(`  前几个: ${found.list.slice(0, 8).map((d) => `${d.label}[${d.id}]`).join(', ')}`);
  log(`  选中: ${found.wall.label} (${found.wall.id})`);
  note(
    '建筑师设计器',
    `用 list_architect_categories 拿 categoryId，再 list_architect_designators(categoryId) 拿 designatorId，` +
      `然后 apply_architect_designator(designatorId, x, z, width, height) 直接落图，不需要真的点 UI。`,
    `墙 designator id = ${found.wall.id}`,
  );

  // 在殖民者旁边造一个 9x9 的围墙（四条边）
  const cols = (await call('rimworld/list_colonists', { currentMapOnly: true })).colonists ?? [];
  const bx = cols[0].position.x + 5;
  const bz = cols[0].position.z + 5;
  const S = 9;
  const r1 = await call('rimworld/apply_architect_designator', { designatorId: found.wall.id, x: bx, z: bz, width: S, height: 1, dryRun: true });
  log(`  预演一条边: ${JSON.stringify(r1).slice(0, 200)}`);
  const res = [];
  res.push(await call('rimworld/apply_architect_designator', { designatorId: found.wall.id, x: bx, z: bz, width: S, height: 1 }));
  res.push(await call('rimworld/apply_architect_designator', { designatorId: found.wall.id, x: bx, z: bz + S - 1, width: S, height: 1 }));
  res.push(await call('rimworld/apply_architect_designator', { designatorId: found.wall.id, x: bx, z: bz, width: 1, height: S }));
  res.push(await call('rimworld/apply_architect_designator', { designatorId: found.wall.id, x: bx + S - 1, z: bz, width: 1, height: S }));
  log(`  四条边结果: ${res.map((r) => r.success).join(', ')}`);
  note('摆建筑', `apply_architect_designator 支持 width/height 一次落一片；dryRun=true 可以先验证。`);

  // 地板
  const floor = (await call('rimworld/list_architect_designators', { categoryId: found.cat.id })).designators.find(
    (d) => /floor/i.test(d.label ?? ''),
  );
  if (floor) {
    await call('rimworld/apply_architect_designator', { designatorId: floor.id, x: bx + 1, z: bz + 1, width: S - 2, height: S - 2 });
    log(`  地板: ${floor.label}`);
  }

  await call('rimworld/frame_cell_rect', { x: bx + 4, z: bz + 4, width: S, height: S, marginCells: 8 });
  await shot('blueprint_room');

  // 蓝图是否真的落上了
  const cellAfter = await call('rimworld/get_cell_info', { x: bx, z: bz });
  log(`  墙角格现在: blueprintBuildDefs=${JSON.stringify(cellAfter.cell.blueprintBuildDefs)} 设计标记=${cellAfter.cell.designationCount}`);
  note('验证建筑', '落图后立刻用 get_cell_info 看 blueprintBuildDefs / frameBuildDefs / designationCount 确认，别只看 success。');
  return { bx, bz, S };
});

// ═══════════════════════════════════════════════════════════
await experiment('4. 生成物品与生物', async () => {
  const cols = (await call('rimworld/list_colonists', { currentMapOnly: true })).colonists ?? [];
  const c0 = cols[0];
  const x = c0.position.x, z = c0.position.z;
  const spawned = [];
  for (const [def, dx, dz, n] of [
    ['Silver', 2, 0, 1000],
    ['Steel', 3, 0, 500],
    ['MedicineHerbal', 4, 0, 20],
    ['Gun_ChargeRifle', 2, 1, 1],
    ['MealSurvivalPack', 3, 1, 10],
  ]) {
    const r = await call('rimworld/spawn_thing', { defName: def, x: x + dx, z: z + dz, stackCount: n });
    spawned.push({ def, ok: r.success !== false });
  }
  log(`  ${spawned.map((s) => `${s.def}:${s.ok ? '✓' : '✗'}`).join(' ')}`);
  note('生成物品', 'spawn_thing(defName, x, z, stackCount) 直接落物。defName 必须是真实 Def 名（如 Silver / Gun_ChargeRifle），写错会报错。');

  const animals = ['Thrumbo', 'Muffalo', 'Boomrat'];
  for (const [i, a] of animals.entries()) {
    const r = await call('rimworld/spawn_thing', { defName: a, x: x + 8 + i, z: z + 8 });
    log(`  ${a}: ${r.success !== false ? '✓' : '✗'}`);
  }
  note('生成生物', 'spawn_thing 同样能生成动物/人类（传对应的 PawnKindDef 或 ThingDef）。');
  await call('rimworld/frame_cell_rect', { x: x + 4, z: z + 3, width: 14, height: 14, marginCells: 6 });
  await shot('spawned_stuff');
  return spawned;
});

// ═══════════════════════════════════════════════════════════
await experiment('5. 选中 / Gizmo / 检查面板', async () => {
  const cols = (await call('rimworld/list_colonists', { currentMapOnly: true })).colonists ?? [];
  const c0 = cols[0];
  await call('rimworld/select_pawn', { pawnId: c0.pawnId });
  const sem = await call('rimworld/get_selection_semantics');
  log(`  选中: ${c0.fullName}`);
  log(`  inspect 文本条数: ${(sem.inspectStrings ?? []).length}`);
  for (const s of (sem.inspectStrings ?? []).slice(0, 5)) {
    log(`    - ${String(s).replace(/<[^>]+>/g, '').slice(0, 90)}`);
  }
  const gz = await call('rimworld/list_selected_gizmos');
  const gizmos = gz.gizmos ?? [];
  log(`  gizmo ${gizmos.length} 个: ${gizmos.slice(0, 10).map((g) => g.label ?? g.id).join(' | ')}`);
  note('Gizmo', `list_selected_gizmos 拿到 id 后 execute_gizmo 执行，等价于点左下角那些按钮。共 ${gizmos.length} 个。`);
  return { sem, gizmos };
});

// ═══════════════════════════════════════════════════════════
await experiment('6. Debug action 树', async () => {
  const roots = await call('rimworld/list_debug_action_roots', { includeHidden: false });
  const rl = roots.roots ?? [];
  log(`  根节点 ${rl.length} 个: ${rl.map((r) => r.label).join(', ')}`);
  note('Debug action 树', `根节点用 list_debug_action_roots 拿；实测共 ${rl.length} 个根。`);

  const s1 = await call('rimworld/search_debug_actions', { query: 'spawn', limit: 8, supportedOnly: true });
  log(`  搜「spawn」命中 ${s1.totalMatchCount} 个，前几个：`);
  for (const m of (s1.matches ?? []).slice(0, 6)) {
    log(`    [${m.node.execution?.kind}/${m.node.execution?.supported ? '可执行' : '不可'}] ${m.path}`);
  }
  note(
    '搜 debug action',
    'search_debug_actions(query, supportedOnly) 全局搜索，node.execution.supported 标明能否直接执行、' +
      'node.execution.requiredTargetKind 标明是否需要指定格子/小人。',
  );

  const s2 = await call('rimworld/search_debug_actions', { query: 'log', limit: 5, supportedOnly: true });
  log(`  搜「log」：${(s2.matches ?? []).map((m) => m.path).join(' | ')}`);
  return { roots: rl, spawnMatches: s1.matches };
});

// ═══════════════════════════════════════════════════════════
await experiment('7. Debug setting 开关', async () => {
  const roots = await call('rimworld/list_debug_action_roots', { includeHidden: true });
  const s = await call('rimworld/search_debug_actions', { query: 'god', limit: 5, includeHidden: true });
  for (const m of (s.matches ?? []).slice(0, 5)) {
    log(`  ${m.path}  toggle=${m.node.hasSettingsToggle} on=${m.node.on}`);
  }
  note('Debug setting', 'set_debug_setting(path, enabled) 用来确定性开关调试项（如 draw* / god mode 相关），比 execute 一个 toggle 更稳。');
  return s.matches;
});

// ═══════════════════════════════════════════════════════════
await experiment('8. 时间控制', async () => {
  const before = (await call('rimworld/get_game_info'));
  const t0 = before.ticksGame ?? before.gameTick;

  await call('rimworld/pause_game', { pause: true });
  const step = await call('rimworld/step_game_ticks', { ticks: 120, pauseFirst: true, timeoutMs: 120_000 });
  log(`  step_game_ticks: ${step.startTicksGame} -> ${step.endTicksGame}（+${step.advancedTicks}，${step.advancedFrames} 帧）`);
  note(
    '精确推进',
    'step_game_ticks(ticks, pauseFirst=true) 一个 tick 一帧地推，结果里 advancedTicks/advancedFrames 可直接当断言。' +
      '要真实速度跑用 play_for(durationMs, speed)。',
  );

  await call('rimworld/set_time_speed', { speed: 'Ultrafast', ultraSpeedBoost: true });
  const t1 = Date.now();
  await call('rimworld/play_for', { durationMs: 12_000, speed: 'Ultrafast' }, { timeoutMs: 300_000 });
  const after = await call('rimworld/get_game_info');
  const t2 = after.ticksGame ?? after.gameTick;
  log(`  极速跑 12 秒实时: tick ${t0} -> ${t2}（真实耗时 ${((Date.now() - t1) / 1000).toFixed(1)}s）`);
  note(
    '极速推进',
    `RimBridgeServer 启动时会打开 RimWorld 私有的 UltraSpeedBoost；speed=Ultrafast 时 12 秒实时能推进 ${t2 - t0} tick。` +
      `注意：play_for 会按真实时间跑，长时间测试要留够请求超时。`,
  );
  await call('rimworld/pause_game', { pause: true });
  return { t0, t2 };
});

// ═══════════════════════════════════════════════════════════
await experiment('9. 真实 UI 面板操作', async () => {
  const tabs = (await call('rimworld/list_main_tabs')).tabs ?? [];
  log(`  主面板: ${tabs.map((t) => t.label).join(', ')}`);
  const work = tabs.find((t) => /work/i.test(t.label));
  await call('rimworld/open_main_tab', { mainTabId: work?.id ?? 'work' });
  const l = await layout(client);
  log('  界面:');
  for (const s of describeSurfaces(l)) log(`    ${s.id} ${s.type ?? s.kind} 元素${s.elements}/可点${s.actionable}`);
  note(
    '读 UI 布局',
    'get_ui_layout 返回 surfaces[]（每个含 elements[]）。元素字段：targetId / kind(button|label|…) / label / ' +
      'actionable / screenRect / scroll。列表行 = 2 个 label + 1 个 button，要靠 screenRect 配对。',
  );

  // 找个可点的按钮点一下（挑非"关闭"类）
  const clickable = l.surfaces.flatMap((s) => s.elements.filter((e) => e.actionable && e.kind === 'button'));
  log(`  可点按钮 ${clickable.length} 个`);
  if (clickable.length) {
    const t = clickable[Math.min(2, clickable.length - 1)];
    const r = await call('rimworld/click_ui_target', { targetId: t.targetId, timeoutMs: 5000 });
    log(`  点了 ${t.targetId}: ${JSON.stringify(r).slice(0, 150)}`);
    note('点 UI', 'click_ui_target(targetId) 会在下一个真实绘制帧激活控件，不需要窗口有焦点 —— 这是「像真人一样操作」的关键。');
  }
  await shot('work_tab');
  await call('rimworld/close_main_tab', {});
  return tabs;
});

// ═══════════════════════════════════════════════════════════
await experiment('10. 右击菜单', async () => {
  const cols = (await call('rimworld/list_colonists', { currentMapOnly: true })).colonists ?? [];
  const c0 = cols[0];
  await call('rimworld/select_pawn', { pawnId: c0.pawnId });
  const menu = await call('rimworld/open_context_menu', { x: c0.position.x + 1, z: c0.position.z });
  const opts = menu.options ?? menu.contextMenu?.options ?? [];
  log(`  菜单项 ${opts.length} 个:`);
  for (const o of opts.slice(0, 10)) log(`    [${o.index}] ${o.label}`);
  note('右键菜单', 'open_context_menu(x,z) 会真的派发一次地图右键并捕获菜单；execute_context_menu_option 按 index 或 label 选项。');

  const g = await call('rimworld/get_context_menu_options');
  log(`  get_context_menu_options: ${(g.options ?? []).length} 项`);
  await call('rimworld/close_context_menu', {});
  return opts;
});

// ═══════════════════════════════════════════════════════════
await experiment('11. 只读查看区域与地带（不新建，原因见最后一步）', async () => {
  const zones = await call('rimworld/list_zones', { includeEmpty: true });
  const areas = await call('rimworld/list_areas');
  log(`  zones=${(zones.zones ?? []).length}  areas=${(areas.areas ?? []).length}`);
  note('读区域', 'list_zones / list_areas 是安全的只读调用，返回当前地图的地带与区域。');
  return { zones, areas };
});

// ═══════════════════════════════════════════════════════════
await experiment('12. 截图：全屏 + 指定格子裁剪', async () => {
  const cols = (await call('rimworld/list_colonists', { currentMapOnly: true })).colonists ?? [];
  const c0 = cols[0];
  const full = await call('rimworld/take_screenshot', { suppressMessage: true, fileName: 'tour_full' });
  const cropped = await call('rimworld/screenshot_cell_rect', {
    x: c0.position.x,
    z: c0.position.z,
    width: 6,
    height: 6,
    paddingCells: 3,
    fileName: 'tour_cropped',
    suppressMessage: true,
  });
  for (const [k, r] of [['全屏', full], ['格子裁剪', cropped]]) {
    log(`  ${k}: ${r.path} ${r.sizeBytes ?? ''} 字节 clamped=${r.clipped ?? false}`);
    if (r.path && fs.existsSync(r.path)) fs.copyFileSync(r.path, path.join(outDir, path.basename(r.path)));
  }
  note(
    '截图',
    'take_screenshot 截的是【游戏渲染画面】不是桌面（已用置顶洋红窗口验证过）；分辨率 = 游戏窗口的渲染分辨率，' +
      '与隔离 profile 的 Config/Prefs.xml 里 screenWidth/Height 一致。' +
      'screenshot_cell_rect 会自动把镜头移过去、按格裁剪、再还原，适合做视觉断言。',
  );
  return { full, cropped };
});

// ═══════════════════════════════════════════════════════════
await experiment('13. 存档 / 读档', async () => {
  const name = 'tour-autosave';
  const s = await call('rimworld/save_game', { saveName: name });
  log(`  存档: ${s.success} ${s.path ?? ''}`);
  const list = await call('rimworld/list_saves', { compatibleOnly: false });
  const saves = list.saves ?? [];
  log(`  存档列表 ${saves.length} 个: ${saves.slice(0, 5).map((x) => x.name).join(', ')}`);
  note(
    '存档',
    'save_game(saveName) / load_game_ready(saveName, readiness, pauseIfNeeded) 都走真实存档系统；' +
      'load_game_ready 默认会校验存档里记录的 mod 是否都启用，测试环境要放宽就传 ignoreModCompatibility=true。',
  );
  return { s, saves };
});

// ═══════════════════════════════════════════════════════════
// 必须放在最后：这个调用会把桥接整个挂死，之后连只读调用都不响应。
await experiment('14. ⚠ 已知地雷：create_allowed_area', async () => {
  const t = Date.now();
  try {
    const r = await call('rimworld/create_allowed_area', { label: '地雷测试', select: false }, { timeoutMs: 15_000 });
    log(`  本次返回了（${Date.now() - t}ms）：${JSON.stringify(r).slice(0, 120)}`);
    note('⚠ 地雷', 'create_allowed_area 本次正常返回 —— 行为不稳定，不能依赖。');
    return r;
  } catch (e) {
    const ms = Date.now() - t;
    note(
      '⚠ 地雷（务必记住）',
      `create_allowed_area 会无限挂起（${ms}ms 后我们主动放弃）。` +
        `更严重的是：它占住游戏主线程调度，之后连 list_areas 这类只读调用也一起挂死，只能重启游戏实例才恢复。` +
        `结论：任何可能挂住的调用都必须设有限超时；一旦出现 GABP_TIMEOUT，就当会话已废，重启游戏而不是重试。`,
      `实测：select=false 与 select=true 都挂；挂起后 list_areas 也超时`,
    );
    throw e;
  }
});

client.close();

const okCount = results.filter((r) => r.ok).length;
log(`\n${'═'.repeat(64)}`);
log(`实验 ${results.length} 项，成功 ${okCount}，失败 ${results.length - okCount}`);
for (const r of results.filter((x) => !x.ok)) log(`  ✗ ${r.title} — ${r.error}`);
log(`证据目录：${outDir}`);
log('═'.repeat(64));

fs.writeFileSync(path.join(outDir, 'tour.json'), JSON.stringify({ stamp, results, notes }, null, 2), 'utf8');

const md = [
  `# RimWorld 控制台技巧巡览`,
  ``,
  `- 时间：${stamp}`,
  `- 结果：${okCount}/${results.length} 步成功`,
  ``,
  `## 实验步骤`,
  ``,
  `| 结果 | 步骤 |`,
  `| --- | --- |`,
  ...results.map((r) => `| ${r.ok ? '✓' : '✗'} | ${r.title}${r.error ? ` — ${r.error}` : ''} |`),
  ``,
  `## 经验小结`,
  ``,
  ...notes.map((n) => `### ${n.topic}\n\n${n.finding}\n${n.evidence ? `\n\`${n.evidence}\`\n` : ''}`),
  ``,
].join('\n');
fs.writeFileSync(path.join(outDir, 'NOTES.md'), md, 'utf8');
log(`经验总结：${path.join(outDir, 'NOTES.md')}`);
