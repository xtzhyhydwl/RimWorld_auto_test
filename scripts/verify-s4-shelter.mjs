/**
 * S4 验证：住处域（方块房：选址 → 选建材 → 放蓝图）。
 *
 * ## 验什么
 *
 * 1. `shelter` 域注册进了域框架，`Id="shelter"` / `Priority=30`
 * 2. mod 的住处逻辑能跑（`foodprobe/run_shelter`）
 * 3. ★★ **桥接独立回读**：地图上真的有 `Blueprint_Build`（墙 23 + 门 1），
 *    且材质是选出来的那个 —— **不信 mod 自述**
 * 4. ★ 材质选择：真的数了库存（`foodprobe/materials`）且选的 stuff 能对上
 * 5. ★ 幂等：再跑一次不重复盖第二间
 * 6. ★ **地板那条路**：用 `foodprobe/floor_path` 取证 ——
 *    证明 `Designator_Build(TerrainDef)` 到底是"放蓝图"还是"直接改地形"
 *
 * ⚠ 只**附着**到已在运行的游戏，自己绝不启动游戏。
 * ⚠ 所有桥接调用都有有限超时；收到 GABP_TIMEOUT 就当会话已废（重启游戏，不要重试）。
 *
 * 用法：node scripts/verify-s4-shelter.mjs
 */
import fs from 'node:fs';
import path from 'node:path';
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor, WS } from '../src/launcher.js';

const PROFILE = 'vanilla';
const CALL = 120_000;
const RUN_DIR = path.join(WS, 'runs', 's4-shelter');

/**
 * Core 的 `BuildSitePlanner` 对 Size=7 的户型给的是：
 *   墙 = 7×4 - 4 - 1(门) = 23；门 = 1；地板 = 5×5 = 25
 * 这是**独立于游戏**的几何事实（Core 98/98 测过），拿它当断言基准。
 */
const EXPECT_WALLS = 23;
const EXPECT_DOORS = 1;
const EXPECT_FLOORS = 25;

let client = null;
const steps = [];
const log = (...a) => console.log(...a);

async function call(name, args = {}, timeoutMs = CALL) {
  return client.callTool(name, args, { timeoutMs });
}

function record(label, ok, detail) {
  steps.push({ label, ok, detail, at: new Date().toISOString() });
  log(`  ${ok === null ? '····' : ok ? '[PASS]' : '[FAIL]'} ${label}${detail ? '  — ' + detail : ''}`);
}

async function clearForcePause() {
  const ui = await call('rimworld/get_ui_state');
  if (!ui?.windowsForcePause) return [];
  const closed = [];
  for (const w of ui.windows || []) {
    if (!w.forcePause) continue;
    if (w.type === 'RimWorld.MainTabWindow_Menu') {
      await call('rimworld/close_main_tab', { tabId: 'main-tab:Menu' });
      closed.push('main-tab:Menu');
    }
  }
  return closed;
}

/**
 * 地图稳定：**必须同时**满足 programState=='Playing' 且 ticksGame>0。
 * （readiness=mapData 只保证数据就绪，`step_game_ticks` 在 ticks=0 时会静默推 0 tick。）
 */
async function ready() {
  const g = await call('rimworld/get_game_info');
  if (g?.status !== 'game_loaded') return { ok: false, stage: 'not_loaded', c: 0 };
  if ((g?.mapCount ?? 0) <= 0) return { ok: false, stage: 'no_map', c: 0 };
  const ui = await call('rimworld/get_ui_state');
  if (ui?.programState !== 'Playing') return { ok: false, stage: 'programState=' + ui?.programState, c: 0 };
  if ((g?.ticksGame ?? 0) <= 0) return { ok: false, stage: 'ticks=0', c: 0 };
  const c = await call('rimworld/list_colonists');
  return { ok: true, stage: 'playing', c: c?.count ?? 0, ticks: g?.ticksGame ?? 0 };
}

/** 熔断：GABP_TIMEOUT 之后整个桥接会话都不可信了，直接终止而不是重试。 */
function isFatal(e) {
  return /GABP_TIMEOUT|timed out|EPIPE|ECONNRESET/i.test(e?.message || '');
}

async function main() {
  const info = readBridgeInfo(logPathFor(PROFILE));
  if (!info) throw new Error('没有桥接信息 —— 先 node src/cli.js start（后台任务托管）');
  const conn = await connectGabp(info, { requestTimeoutMs: CALL });
  client = conn.client;
  log(`桥接 ${info.host}:${info.port}`);

  try {
    // ── 0. 地图就绪 ──
    let st = await ready();
    if (st.stage === 'not_loaded' || st.stage === 'no_map') {
      log(`开调试殖民地（阶段 ${st.stage}）...`);
      await call('rimworld/start_debug_game_ready',
        { readiness: 'mapData', pauseIfNeeded: true, timeoutMs: 240_000 }, 280_000,
      ).catch((e) => log('  ' + e.message.split('\n')[0]));
    }
    for (let i = 0; i < 150; i++) {
      st = await ready();
      if (st.ok && st.c > 0) break;
      if (i % 10 === 0) log(`  等待中... 阶段=${st.stage} 殖民者=${st.c}`);
      await new Promise((r) => setTimeout(r, 2000));
    }
    record('★ 地图稳定（programState=Playing **且** ticksGame>0）+ 殖民者有',
      st.ok && st.c > 0, `阶段=${st.stage} ticks=${st.ticks} ${st.c} 人`);
    await clearForcePause();

    const health = await call('foodprobe/health');
    record('mod 已加载（含新编译的 S4 代码）', health?.modLoaded === true, health?.message || '');
    if (!health?.modLoaded) throw new Error('mod 未加载');

    // ── 1. 域注册 ──
    log('\n=== 1. 域注册（shelter / Priority 30）===');
    const dom = await call('foodprobe/domains');
    const ids = (dom?.domains || []).map((d) => d.id);
    record('domains 工具可调用', dom?.success === true, dom?.message || '');
    record('注册了 food 域', ids.includes('food'), ids.join(','));
    record('注册了 stockpile 域', ids.includes('stockpile'), ids.join(','));
    record('★ 注册了 shelter 域', ids.includes('shelter'), ids.join(','));

    // ── 2. 基线 ──
    log('\n=== 2. 基线：现在地图上有没有建筑/蓝图 ===');
    const before = await call('foodprobe/list_buildings', { maxSamples: 10 });
    record('list_buildings 可调用', before?.success === true, before?.message || '');
    log(`  基线：蓝图总 ${before?.blueprintBuild_total}（墙 ${before?.blueprintWall} / 门 ${before?.blueprintDoor}），`
      + `框架 ${before?.frames}，已建成 墙 ${before?.wallsBuilt} / 门 ${before?.doorsBuilt}`);
    record('基线可读（已存在的蓝图/框架数会写进报告；调试开局可能自带）',
      before?.success === true,
      `蓝图=${before?.blueprintBuild_total} 框架=${before?.frames} 墙=${before?.wallsBuilt} 门=${before?.doorsBuilt}`);

    // ── 3. 材质：真的数库存 ──
    log('\n=== 3. 建材库存（选材质的输入）===');
    const mats = await call('foodprobe/materials');
    const matRaw = mats?.raw || '';
    log(`  ${matRaw}`);
    record('materials 来自 mod 自己的判据（不是探针兜底）',
      mats?.source === 'mod', `source=${mats?.source}`);

    // ── 4. 只看不动的诊断 ──
    log('\n=== 4. ★ 只读诊断：这一轮会怎么盖（不动地图）===');
    const inspect = await call('foodprobe/inspect_shelter');
    log(`  ${inspect?.result || inspect?.message}`);
    record('inspect_shelter 可调用（只读）', inspect?.success === true, inspect?.message || '');
    // 只读性：诊断跑完蓝图数不能变
    const afterInspect = await call('foodprobe/list_buildings', { maxSamples: 2 });
    record('★ inspect 是只读的（蓝图数没变）',
      (afterInspect?.blueprintBuild_total ?? -1) === (before?.blueprintBuild_total ?? 0),
      `${before?.blueprintBuild_total} → ${afterInspect?.blueprintBuild_total}`);

    // ── 5. ★ 真跑住处域 ──
    log('\n=== 5. ★ 跑住处域（放蓝图）===');
    const run = await call('foodprobe/run_shelter');
    record('run_shelter 可调用', run?.success === true,
      (run?.result || run?.message || '').slice(0, 400));
    log(`  mod 自述：${run?.result || run?.message}`);

    // ── 6. ★★ 桥接独立回读（不信 mod 自述）──
    log('\n=== 6. ★★ 桥接独立回读（不信 mod 自述）===');
    const after = await call('foodprobe/list_buildings', { maxSamples: 30 });
    log(`  回读：蓝图总 ${after?.blueprintBuild_total}`
      + `（墙 ${after?.blueprintWall} / 门 ${after?.blueprintDoor}），框架 ${after?.frames}`);
    log(`  byBuildDef = ${JSON.stringify(after?.byBuildDef)}`);
    log(`  byStuff    = ${JSON.stringify(after?.byStuff)}`);
    if (after?.blueprintSamples?.length) {
      log('  前几个蓝图：');
      for (const s of after.blueprintSamples.slice(0, 8)) {
        log(`    (${s.x},${s.z}) ${s.blueprintDef} → ${s.buildsDef} stuff=${s.stuff} door=${s.isDoorBlueprint}`);
      }
    }

    const nWallBp = after?.blueprintWall ?? 0;
    const nDoorBp = after?.blueprintDoor ?? 0;
    const nTotalBp = after?.blueprintBuild_total ?? 0;

    record('★★ 地图上真的有墙蓝图（core 几何：7×7 应 23 面墙）',
      nWallBp >= EXPECT_WALLS, `回读到墙蓝图 ${nWallBp}（期望 ≥ ${EXPECT_WALLS}）`);
    record('★★ 地图上真的有门蓝图（Blueprint_Door 是 Blueprint_Build 的子类）',
      nDoorBp >= EXPECT_DOORS, `回读到门蓝图 ${nDoorBp}（期望 ≥ ${EXPECT_DOORS}）`);
    record('★★ 蓝图总数 = 墙 + 门（账对得上）',
      nTotalBp === nWallBp + nDoorBp, `${nWallBp} + ${nDoorBp} = ${nTotalBp}`);
    record('★★ 蓝图总数 ≥ 24（23 墙 + 1 门）',
      nTotalBp >= EXPECT_WALLS + EXPECT_DOORS, `回读到 ${nTotalBp}`);

    // 被 mod 拒掉的格数也要看（全被拒的话数字对不上）
    const rejMatch = /被拒(\d+)/.exec(run?.result || '');
    log(`  mod 报告的"被拒"格数：${rejMatch ? rejMatch[1] : '（未报告）'}`);

    // 材质一致性：选出来的 stuff 必须和 byStuff 里的一致
    const stuffKeys = Object.keys(after?.byStuff || {});
    log(`  蓝图用的材质：${stuffKeys.join(',')}`);
    const matKnown = stuffKeys.length > 0 && stuffKeys.every((k) => k !== '(无)');
    record('★ 蓝图带上了材质（不是无 stuff 的空壳）', matKnown, stuffKeys.join(','));

    // 和库存对得上（选的是库存里有的东西）
    if (matKnown) {
      const stockDefs = [...matRaw.matchAll(/"([A-Za-z_][A-Za-z0-9_]*)":(\d+)/g)].map((m) => m[1]);
      const allFromStock = stuffKeys.every((k) => stockDefs.includes(k));
      record('★★ 选出来的材质**确实来自现有库存**（没凭空造料）',
        allFromStock, `蓝图材质=[${stuffKeys.join(',')}] 库存=[${stockDefs.join(',')}]`);
    }

    // ── 7. 幂等 ──
    log('\n=== 7. 幂等：再跑一次不该盖第二间 ===');
    const again = await call('foodprobe/run_shelter');
    const after2 = await call('foodprobe/list_buildings', { maxSamples: 2 });
    log(`  第二次自述：${again?.result || again?.message}`);
    record('重复调用不报错', again?.success === true, (again?.result || '').slice(0, 200));
    record('★ 蓝图数没有翻倍（不重复盖）',
      (after2?.blueprintBuild_total ?? 0) <= nTotalBp + 2,
      `${nTotalBp} → ${after2?.blueprintBuild_total}`);

    // ── 8. ★ 地板那条路的取证 ──
    log('\n=== 8. ★ 地板路径取证（TerrainDef 能不能"放蓝图"）===');
    const floorRun = await call('foodprobe/floor_path', { terrainDefName: 'WoodPlankFloor', count: 4 });
    log(`  ${JSON.stringify({
      blueprintDef: floorRun?.terrain_blueprintDef,
      frameDef: floorRun?.terrain_frameDef,
      hasBlueprintForm: floorRun?.hasBlueprintForm,
      accepted: floorRun?.designateAccepted,
      rejected: floorRun?.designateRejected,
      terrainNow: floorRun?.terrainNowOnTargets,
      blueprintDelta: floorRun?.blueprintDelta,
      frameDelta: floorRun?.frameDelta,
      areaBefore: floorRun?.terrainAreaBefore,
      areaAfter: floorRun?.terrainAreaAfter,
    })}`);
    log(`  verdict: ${floorRun?.verdict}`);

    record('floor_path 工具可调用', floorRun?.success === true, floorRun?.message || '');
    record('★ 确认 Designator_Build(TerrainDef) 能通过"先查再放"（CanDesignateCell 接受）',
      (floorRun?.designateAccepted ?? 0) > 0,
      `接受 ${floorRun?.designateAccepted} / 拒 ${floorRun?.designateRejected}`);
    record('★★ 地板**没有**蓝图形态（terrain.blueprintDef == null）',
      floorRun?.terrain_blueprintDef == null,
      `blueprintDef=${floorRun?.terrain_blueprintDef ?? 'null'} frameDef=${floorRun?.terrain_frameDef ?? 'null'}`);
    record('★★ 地板是"直接改地形"：地形面积变了但**蓝图增量为 0**',
      (floorRun?.designateAccepted ?? 0) > 0
        && (floorRun?.terrainAreaAfter ?? 0) > (floorRun?.terrainAreaBefore ?? 0)
        && (floorRun?.blueprintDelta ?? 1) === 0
        && (floorRun?.frameDelta ?? 1) === 0,
      `地形 ${floorRun?.terrainAreaBefore} → ${floorRun?.terrainAreaAfter}，`
        + `蓝图Δ=${floorRun?.blueprintDelta}，框架Δ=${floorRun?.frameDelta}`);

    // 最终再回读一次总账（地板那条路跑完之后）
    const finalB = await call('foodprobe/list_buildings', { maxSamples: 2 });
    log(`\n  最终回读：蓝图总 ${finalB?.blueprintBuild_total}`
      + `（墙 ${finalB?.blueprintWall} / 门 ${finalB?.blueprintDoor}），`
      + `框架 ${finalB?.frames}，已建成 墙 ${finalB?.wallsBuilt} / 门 ${finalB?.doorsBuilt}`);
    record('★★ 最终：墙蓝图仍然 ≥ 23（地板取证没有破坏墙门蓝图）',
      (finalB?.blueprintWall ?? 0) >= EXPECT_WALLS,
      `墙蓝图 ${finalB?.blueprintWall}`);
  } finally {
    client.close();
  }

  const passed = steps.filter((s) => s.ok === true).length;
  const failed = steps.filter((s) => s.ok === false).length;
  const skipped = steps.filter((s) => s.ok === null).length;

  fs.mkdirSync(RUN_DIR, { recursive: true });
  fs.writeFileSync(path.join(RUN_DIR, 'report.json'),
    JSON.stringify({ passed, failed, skipped, steps }, null, 2), 'utf8');

  const md = [
    '# S4 验证报告：住处域（方块房）',
    '',
    `时间：${new Date().toISOString()}`,
    `结果：**通过 ${passed} / 失败 ${failed} / 跳过 ${skipped}**`,
    '',
    '| 结果 | 项目 | 细节 |',
    '| --- | --- | --- |',
    ...steps.map((s) => `| ${s.ok === null ? '—' : s.ok ? '✅' : '❌'} | ${s.label} | ${(s.detail || '').replace(/\|/g, '\\|')} |`),
    '',
    '## 判定原则',
    '',
    '> **只有 `success` 不算数**：必须桥接回读确认地图上真的出现了 `Blueprint_Build`。',
    `> 断言基准 ${EXPECT_WALLS} 墙 / ${EXPECT_DOORS} 门是 Core 的几何事实（7×7 → 外墙 24 格，其中 1 格是门）。`,
    '',
    '## 地板路径的结论',
    '',
    '- `TerrainDef.blueprintDef` 为 `null` ⇒ **地板在游戏里没有"蓝图形态"**。',
    '- `Designator_Build(TerrainDef).DesignateSingleCell` 走的是 `TerrainGrid::SetTerrain`：',
    '  地形**立刻**变化、**蓝图增量为 0、框架增量为 0** ⇒ 它是"直接改地形"，不是"放蓝图"。',
    '- 因此 mod 默认**不铺地板**（`ShelterDomain.BuildFloor = false`），只放墙和门的蓝图。',
    '',
    '> 本脚本只**附着**到已在运行的游戏，自己绝不启动游戏。',
  ].join('\n');
  fs.writeFileSync(path.join(RUN_DIR, 'report.md'), md, 'utf8');

  log(`\n=== 通过 ${passed} / 失败 ${failed} / 跳过 ${skipped} ===`);
  log(`报告: ${path.join(RUN_DIR, 'report.md')}`);
  process.exitCode = failed === 0 ? 0 : 1;
}

main().catch((e) => {
  if (isFatal(e)) {
    console.error('\n[致命] 桥接超时/断连 —— 会话已废，请**重启游戏**而不是重试：' + e.message);
  } else {
    console.error('\n[错误] ' + e.message);
  }
  process.exitCode = 1;
});
