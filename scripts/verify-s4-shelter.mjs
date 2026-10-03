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
const FLOOR_DEF = 'WoodPlankFloor';

let client = null;
const steps = [];
const log = (...a) => console.log(...a);

/** 报告里要引用的量，声明在 main 作用域外层（免得被 try 块作用域挡掉）。 */
let REPORT = { rj: null, rjAfter: null, after: null, before: null, run: null, delta: null };

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
    //
    // ★ 从这里开始**暂停游戏**，一直保持到第 6 节结束。
    //   理由（实测踩过）：小人一直在建造，蓝图会被变成 Frame 再从地图上消失；
    //   如果不暂停，"放之前"和"放之后"两次读数根本不是同一个世界状态，
    //   算出来的增量会是 +0 甚至负数 —— 我第一版就是这么被骗的。
    log('\n=== 2. 基线（⏸ 先暂停：小人建造会让蓝图数在两次读之间变化）===');
    const pauseRes = await call('foodprobe/set_paused', { paused: true });
    record('★ 能暂停游戏（测量一致性）', pauseRes?.success === true && pauseRes?.paused === true,
      `paused=${pauseRes?.paused} ${pauseRes?.message || ''}`);
    const before = await call('foodprobe/list_buildings', { maxSamples: 10 });
    record('list_buildings 可调用', before?.success === true, before?.message || '');
    log(`  基线：蓝图总 ${before?.blueprintBuild_total} byBuildDef=${JSON.stringify(before?.byBuildDef)}，`
      + `框架 ${before?.frames}，已建成 墙 ${before?.wallsBuilt} / 门 ${before?.doorsBuilt}`);
    record('基线可读（已存在的蓝图/框架数会写进报告；调试开局/上一轮可能自带）',
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
    //
    // 两种跑法，按基线状态自动选：
    //   (a) 基线干净（蓝图 0）→ 直接跑**生产路径** `run_shelter`
    //   (b) 基线已有蓝图（上一轮验证留下的）→ 跑 `run_shelter_force`（测试专用），
    //       并用 **before/after 增量**做断言。
    //
    // ⚠⚠ **测量期间必须暂停游戏**（实测踩过的陷阱）：
    //   小人一直在建造 —— 蓝图会被变成 Frame 再从地图上消失。
    //   我第一版在"放之前"和"放之后"各数一次，中间隔了几十秒，
    //   结果 delta 算出来是 +0（before 和 after 根本不是同一个世界状态）。
    //   `foodprobe/set_paused` 就是为此加的。
    //
    // ⚠ **debug 开局自带几百面墙**（实测 203~662 面不等），所以"有墙有门"不能被无条件
    //   当成"已有住处"——`ShelterDomain.HasAnyShelter` 只在**我们自己盖过**（`_placedOnce`）
    //   之后才这么判。这条正是被实测逼出来的修正。
    log('\n=== 5. ★ 跑住处域（放蓝图）===');
    const frozen = await call('foodprobe/list_buildings', { maxSamples: 40 });
    const baseBP = frozen?.blueprintBuild_total ?? 0;
    const useForce = baseBP > 0;
    log(`  冻结基线：蓝图总 ${baseBP} byBuildDef=${JSON.stringify(frozen?.byBuildDef)}`
      + ` 框架 ${frozen?.frames} → 用 ${useForce ? 'run_shelter_force' : 'run_shelter'} 路径`);

    const run = await call(useForce ? 'foodprobe/run_shelter_force' : 'foodprobe/run_shelter');
    record(`run_shelter${useForce ? '_force' : ''} 可调用`, run?.success === true,
      (run?.result || run?.message || '').slice(0, 420));
    log(`  mod 自述：${run?.result || run?.message}`);

    // ★ mod 自己的记账（"我们放了几个"）
    const rep = await call('foodprobe/shelter_report');
    let rj = null;
    try { rj = JSON.parse(rep?.result || 'null'); } catch { rj = null; }
    log(`  mod 记账 JSON：${rep?.result}`);
    record('★ shelter_report 可读且是合法 JSON', rj != null, (rep?.result || rep?.message || '').slice(0, 240));
    record('★★ mod 记账：墙 23 / 门 1 / 地板 25（与 Core 户型一致）',
      (rj?.wallsPlaced ?? -1) === EXPECT_WALLS
        && (rj?.doorsPlaced ?? -1) === EXPECT_DOORS
        && (rj?.floorsPlaced ?? -1) === EXPECT_FLOORS,
      `墙${rj?.wallsPlaced} 门${rj?.doorsPlaced} 地板${rj?.floorsPlaced}（户型 墙${rj?.plan?.wallCells} 门${rj?.plan?.doorCells} 地板${rj?.plan?.floorCells}）`);
    record('★ mod 自己的回读验证通过（verified=true）', rj?.verified === true, `verified=${rj?.verified}`);
    record('★ 选材质：真的数了库存、且在偏好顺序里命中（木头优先）',
      rj?.material?.def === 'WoodLog' && (rj?.material?.available ?? 0) > 0,
      `${rj?.material?.def} 库存${rj?.material?.available} allowedForWall=${rj?.material?.allowedForWall} why=${rj?.material?.why}`);

    // ── 6. ★★ 桥接独立回读（不信 mod 自述）──
    log('\n=== 6. ★★ 桥接独立回读（不信 mod 自述）===');
    const after = await call('foodprobe/list_buildings', { maxSamples: 40 });
    log(`  回读：蓝图总 ${after?.blueprintBuild_total}`
      + `（墙 ${after?.blueprintWall} / 门 ${after?.blueprintDoor}），框架 ${after?.frames}`);
    log(`  byBuildDef = ${JSON.stringify(after?.byBuildDef)}`);
    log(`  byStuff    = ${JSON.stringify(after?.byStuff)}`);
    if (after?.blueprintSamples?.length) {
      log('  前几个蓝图：');
      for (const s of after.blueprintSamples.slice(0, 10)) {
        log(`    (${s.x},${s.z}) ${s.blueprintDef} → ${s.buildsDef} stuff=${s.stuff} door=${s.isDoorBlueprint}`);
      }
    }

    const nWallBp = after?.byBuildDef?.Wall ?? 0;
    const nDoorBp = after?.byBuildDef?.Door ?? 0;
    const nFloorBp = after?.byBuildDef?.[FLOOR_DEF] ?? 0;
    const nTotalBp = after?.blueprintBuild_total ?? 0;
    const byDefSum = Object.values(after?.byBuildDef || {}).reduce((a, b) => a + b, 0);

    // ★ 增量基线用**冻结后的那次读数**（`before` 之后到 `frozen` 之间可能刚好有自动域动作）
    const bWall = frozen?.byBuildDef?.Wall ?? 0;
    const bDoor = frozen?.byBuildDef?.Door ?? 0;
    const bFloor = frozen?.byBuildDef?.[FLOOR_DEF] ?? 0;
    const bTotal = frozen?.blueprintBuild_total ?? 0;
    const delta = {
      wall: nWallBp - bWall, door: nDoorBp - bDoor, floor: nFloorBp - bFloor, total: nTotalBp - bTotal,
    };
    REPORT = { rj, rjAfter: rj, after, before, run, delta };
    log(`  ★ 增量（before → after）：墙 ${bWall}→${nWallBp} (+${delta.wall})，`
      + `门 ${bDoor}→${nDoorBp} (+${delta.door})，地板 ${bFloor}→${nFloorBp} (+${delta.floor})，`
      + `合计 ${bTotal}→${nTotalBp} (+${delta.total})`);

    record('★★★ 桥接独立回读：本轮**新增**墙蓝图 = 23（Core 户型几何）',
      delta.wall === EXPECT_WALLS, `+${delta.wall}（期望 +${EXPECT_WALLS}）`);
    record('★★★ 桥接独立回读：本轮**新增**门蓝图 = 1',
      delta.door === EXPECT_DOORS, `+${delta.door}（期望 +${EXPECT_DOORS}）`);
    record(`★★★ 桥接独立回读：本轮**新增**地板蓝图 = 25（${FLOOR_DEF}）`,
      delta.floor === EXPECT_FLOORS, `+${delta.floor}（期望 +${EXPECT_FLOORS}）`);
    record('★★★ 桥接独立回读：本轮新增蓝图合计 = 49（23 墙 + 1 门 + 25 地板）',
      delta.total === EXPECT_WALLS + EXPECT_DOORS + EXPECT_FLOORS,
      `+${delta.total}（期望 +${EXPECT_WALLS + EXPECT_DOORS + EXPECT_FLOORS}）`);
    record('★★ 地图上确实存在这些蓝图（按 def 分组求和 ≥ 基准）',
      byDefSum >= EXPECT_WALLS + EXPECT_DOORS + EXPECT_FLOORS,
      `byBuildDef=${JSON.stringify(after?.byBuildDef)} 合计=${byDefSum}（total=${nTotalBp}，差 ${byDefSum - nTotalBp}）`);

    // 被 mod 拒掉的格数也要看（全被拒的话数字对不上）
    const rejMatch = /被拒(\d+)/.exec(run?.result || '');
    log(`  mod 报告的"被拒"格数：${rejMatch ? rejMatch[1] : '（未报告）'}`);

    // 材质一致性：**必须读 `EntityToBuildStuff()`**（蓝图把材质放在 `Blueprint_Build.stuffToUse`，
    // 读 `Thing.Stuff` 永远是 null —— 这是本轮实测抓出来的第二个坑）
    log(`  蓝图材质分布：${JSON.stringify(after?.byStuff)}`);
    const hasWood = (after?.byStuff?.WoodLog ?? 0) >= EXPECT_WALLS + EXPECT_DOORS;
    record('★★ 墙/门蓝图确实带上了选出来的材质（WoodLog）—— 读 EntityToBuildStuff()',
      hasWood,
      `byStuff=${JSON.stringify(after?.byStuff)}（期望 WoodLog ≥ ${EXPECT_WALLS + EXPECT_DOORS}）`);
    record('★ 地板蓝图不带 stuff（"(无)"）—— 它的用料是 TerrainDef.costStuffCount，语义不同',
      (after?.byStuff?.['(无)'] ?? 0) >= EXPECT_FLOORS,
      `"(无)"=${after?.byStuff?.['(无)']}（期望 ≥ ${EXPECT_FLOORS}）`);

    const repAfter = await call('foodprobe/shelter_report');
    let rjAfter = null;
    try { rjAfter = JSON.parse(repAfter?.result || 'null'); } catch { }
    log(`  mod 记账：${repAfter?.result}`);
    REPORT.rjAfter = rjAfter;
    // ★★ 最关键的一条：mod 自己说放了 23 墙，桥接**独立**也读到 **+23** 面墙蓝图
    record('★★★ mod 记账的"墙 23"与桥接**独立回读的增量 +23** 一致',
      (rjAfter?.wallsPlaced ?? -1) === EXPECT_WALLS && delta.wall === EXPECT_WALLS,
      `mod=${rjAfter?.wallsPlaced} 桥接增量=+${delta.wall}`);
    record('★★★ mod 记账的"门 1"与桥接独立回读的增量 +1 一致',
      (rjAfter?.doorsPlaced ?? -1) === EXPECT_DOORS && delta.door === EXPECT_DOORS,
      `mod=${rjAfter?.doorsPlaced} 桥接增量=+${delta.door}`);
    record('★★★ mod 记账的"地板 25"与桥接独立回读的增量 +25 一致',
      (rjAfter?.floorsPlaced ?? -1) === EXPECT_FLOORS && delta.floor === EXPECT_FLOORS,
      `mod=${rjAfter?.floorsPlaced} 桥接增量=+${delta.floor}`);

    // ── 7. 幂等 ──
    log('\n=== 7. 幂等：再跑一次不该盖第二间 ===');
    const again = await call('foodprobe/run_shelter');
    const after2 = await call('foodprobe/list_buildings', { maxSamples: 2 });
    log(`  第二次（正常路径）自述：${again?.result || again?.message}`);
    record('★★ 重复调用被"已经有一处在盖"守卫拦住（这才是生产路径的正确行为）',
      again?.success === true && /已经有一处在盖|已有住处|不重复|不再盖/.test(again?.result || ''),
      (again?.result || '').slice(0, 200));
    record('★ 蓝图数没有增加（不重复盖）',
      (after2?.blueprintBuild_total ?? 0) === nTotalBp,
      `${nTotalBp} → ${after2?.blueprintBuild_total}`);

    // ── 8. ★ 地板的干净实验（一格、一次、前后记账）──
    //
    // 这是被用户打回来重做的那一节：先前我"读 IL"断言地板是"瞬间免费改地形"，
    // 实测（Blueprint_WoodPlankFloor）推翻了它。根因是 tools/asmdump/IlDump.cs 的
    // ShortInlineBrTarget off-by-one，已修。这里用**一格**的干净实验把结论钉死。
    log('\n=== 8. ★ 地板的干净实验（一格，前后各记一次地形与蓝图账）===');
    // 8a. 先 dry-run：只查不动
    const dry = await call('foodprobe/floor_path',
      { terrainDefName: FLOOR_DEF, count: 1, mode: 'designator_dry', index: 0 });
    record('floor_path(dry) 可调用', dry?.success === true, dry?.message || '');
    log(`  dry: 目标格=${JSON.stringify(dry?.targetCells)} 地形前=${JSON.stringify(dry?.terrainBefore)}`
      + ` 蓝图前=${JSON.stringify(dry?.blueprintCountBefore)}`);
    record('★ dry-run 真的没动手（蓝图计数不变）',
      dry?.blueprintDeltaTotal === 0 && (dry?.terrainChanged === false),
      `蓝图Δ=${dry?.blueprintDeltaTotal} 地形变=${dry?.terrainChanged}`);

    // 8b. 真动手：Designator_Build(TerrainDef) 一格
    const fp = await call('foodprobe/floor_path',
      { terrainDefName: FLOOR_DEF, count: 1, mode: 'designator', index: 1 });
    log(`  designator 一格：`);
    log(`    目标格        = ${JSON.stringify(fp?.targetCells)}`);
    log(`    地形 before   = ${JSON.stringify(fp?.terrainBefore)}`);
    log(`    地形 after    = ${JSON.stringify(fp?.terrainAfter)}`);
    log(`    terrainChanged= ${fp?.terrainChanged}`);
    log(`    蓝图 before   = ${JSON.stringify(fp?.blueprintCountBefore)}`);
    log(`    蓝图 after    = ${JSON.stringify(fp?.blueprintCountAfter)}`);
    log(`    蓝图Δ(按def)  = ${JSON.stringify(fp?.blueprintDeltaByDef)}`);
    log(`    该地形新增蓝图 = ${fp?.newBlueprintsForTargetTerrain}`);
    log(`    terrain.blueprintDef = ${fp?.terrain_blueprintDef} / frameDef=${fp?.terrain_frameDef}`);
    log(`    verdict: ${fp?.verdict}`);

    record('floor_path(designator) 可调用', fp?.success === true, fp?.message || '');
    record('★★ TerrainDef **有** blueprintDef（地板不是"没有蓝图形态"）',
      fp?.terrain_blueprintDef != null && fp?.terrain_hasBlueprintForm === true,
      `blueprintDef=${fp?.terrain_blueprintDef ?? 'null'} frameDef=${fp?.terrain_frameDef ?? 'null'}`);
    record('★★ 干净实验：一格 designator 后，**地形没变**、蓝图 +1（buildsDef==WoodPlankFloor）',
      fp?.terrainChanged === false
        && (fp?.newBlueprintsForTargetTerrain ?? 0) >= 1
        && (fp?.blueprintDeltaByDef?.[FLOOR_DEF] ?? 0) >= 1,
      `地形 ${JSON.stringify(fp?.terrainBefore)} → ${JSON.stringify(fp?.terrainAfter)}，`
        + `蓝图Δ=${JSON.stringify(fp?.blueprintDeltaByDef)}`);

    // 8c. 另一条路：GenConstruct.PlaceBlueprintForBuild(TerrainDef)
    const gc = await call('foodprobe/floor_path',
      { terrainDefName: FLOOR_DEF, count: 1, mode: 'genconstruct', index: 2 });
    log(`  genconstruct 一格：蓝图Δ=${JSON.stringify(gc?.blueprintDeltaByDef)}`
      + ` 地形变=${gc?.terrainChanged} accepted=${gc?.accepted} rejected=${gc?.rejected}`);
    log(`    verdict: ${gc?.verdict}`);
    record('★ GenConstruct.PlaceBlueprintForBuild(TerrainDef) 也能放地板蓝图（更正规的路）',
      gc?.success === true && gc?.terrainChanged === false
        && (gc?.newBlueprintsForTargetTerrain ?? 0) >= 1,
      `蓝图Δ=${JSON.stringify(gc?.blueprintDeltaByDef)} errors=${JSON.stringify(gc?.errors)}`);

    // 8d. 排除"是别的 mod Harmony patch 改写了行为"
    const pat = await call('foodprobe/patches');
    log(`  patches: harmonyLoaded=${pat?.harmonyLoaded} 全游戏被patch方法数=${pat?.totalPatchedMethodsInGame}`
      + ` 我们的目标被patch数=${pat?.ourTargetsPatched}`);
    if (pat?.patchedMethods?.length) {
      for (const p of pat.patchedMethods) {
        if (p.patched) log(`    ⚠ 被 patch: ${p.target} ← ${(p.owners || []).join(',')}`);
      }
    }
    record('★★ 排除 Harmony patch：TerrainGrid.SetTerrain / Designator_Build 没被别的 mod 改写',
      pat?.success === true && (pat?.ourTargetsPatched ?? 99) === 0,
      `ourTargetsPatched=${pat?.ourTargetsPatched}（${(pat?.patchedMethods || []).filter((p) => p.patched).map((p) => p.target).join(',') || '无'}）`);

    // 最终再回读一次总账（地板实验跑完之后 —— 仍是暂停状态，读数稳定）
    const finalB = await call('foodprobe/list_buildings', { maxSamples: 2 });
    log(`\n  最终回读：蓝图总 ${finalB?.blueprintBuild_total}`
      + ` byBuildDef=${JSON.stringify(finalB?.byBuildDef)}，`
      + `框架 ${finalB?.frames}，已建成 墙 ${finalB?.wallsBuilt} / 门 ${finalB?.doorsBuilt}`);
    record('★★ 最终：墙蓝图仍 ≥ 23 且门蓝图 ≥ 1（地板实验没有破坏房子的蓝图）',
      (finalB?.byBuildDef?.Wall ?? 0) >= EXPECT_WALLS && (finalB?.byBuildDef?.Door ?? 0) >= EXPECT_DOORS,
      `墙 ${finalB?.byBuildDef?.Wall} / 门 ${finalB?.byBuildDef?.Door} / 地板 ${finalB?.byBuildDef?.[FLOOR_DEF]}`);

    // 收尾：恢复运行（别把游戏留在暂停状态，用户还要用）
    const resume = await call('foodprobe/set_paused', { paused: false });
    record('★ 收尾恢复游戏运行（不把游戏留在暂停状态）',
      resume?.success === true && resume?.paused === false, `paused=${resume?.paused}`);
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
    `> 断言基准 ${EXPECT_WALLS} 墙 / ${EXPECT_DOORS} 门 / ${EXPECT_FLOORS} 地板是 Core 的几何事实` +
      '（7×7 → 外墙 24 格，其中 1 格是门；内部 5×5 = 25 格地板）。',
    '',
    '## 交叉验证（本报告最有价值的一栏）',
    '',
    `基线：byBuildDef=${JSON.stringify(REPORT.before?.byBuildDef || {})} total=${REPORT.before?.blueprintBuild_total}`,
    `回读：byBuildDef=${JSON.stringify(REPORT.after?.byBuildDef || {})} total=${REPORT.after?.blueprintBuild_total}`,
    '',
    '| 量 | mod 自己的记账 | 桥接**独立回读的增量** | 一致？ |',
    '| --- | --- | --- | --- |',
    `| 墙蓝图 | ${REPORT.rjAfter?.wallsPlaced ?? '?'} | +${REPORT.delta?.wall ?? '?'} | ${(REPORT.rjAfter?.wallsPlaced === REPORT.delta?.wall) ? '✅' : '❌'} |`,
    `| 门蓝图 | ${REPORT.rjAfter?.doorsPlaced ?? '?'} | +${REPORT.delta?.door ?? '?'} | ${(REPORT.rjAfter?.doorsPlaced === REPORT.delta?.door) ? '✅' : '❌'} |`,
    `| 地板蓝图 | ${REPORT.rjAfter?.floorsPlaced ?? '?'} | +${REPORT.delta?.floor ?? '?'} | ${(REPORT.rjAfter?.floorsPlaced === REPORT.delta?.floor) ? '✅' : '❌'} |`,
    `| 材质 | ${REPORT.rjAfter?.material?.def ?? '?'}（库存 ${REPORT.rjAfter?.material?.available ?? '?'}） | ${JSON.stringify(REPORT.after?.byStuff)} | — |`,
    `| 选址 | (${REPORT.rjAfter?.plan?.x},${REPORT.rjAfter?.plan?.z}) ${REPORT.rjAfter?.plan?.size}×${REPORT.rjAfter?.plan?.size} | — | — |`,
    '',
    '> ⚠ 材质要用 `Blueprint_Build.EntityToBuildStuff()` 读（= `stuffToUse` 字段）。',
    '> 读 `Thing.Stuff` 对蓝图永远是 `null` —— 我先前因此得出过"蓝图没有材质"的错结论。',
    '> 地板蓝图确实不带 stuff（byStuff 里是 `(无)`），它的用料走 `TerrainDef.costStuffCount`。',
    '',
    '## 地板路径的结论（★ 被实测推翻了两次）',
    '',
    '### 事实',
    '',
    '- `TerrainDefOf.WoodPlankFloor.blueprintDef` == **`Blueprint_WoodPlankFloor`**（**不是 null**）',
    '- `Blueprint_WoodPlankFloor.frameDef` == `Frame_WoodPlankFloor`',
    '- 干净实验（一格、一次、前后记账）：`designator` 路径下 **地形没变、蓝图 +1**；',
    '  `GenConstruct.PlaceBlueprintForBuild(TerrainDef)` 同样 **地形没变、蓝图 +1**。',
    '- ⇒ **地板就是蓝图**，和墙门走同一条路：要耗料（`costStuffCount`）、要小人来铺。',
    '',
    '### 我先前为什么搞错了（留档）',
    '',
    '1. 我用 `tools/asmdump/IlDump.exe` 反汇编 `Designator_Build.DesignateSingleCell`，',
    '   看到 `IL_008E: brtrue.s IL_00AF`，把它读成"godMode 成立就跳到地形分支"，',
    '   于是写下"地板 = `TerrainGrid.SetTerrain` = 瞬间免费改地形"。',
    '2. **根因**：`IlDump.cs` 的 `ShortInlineBrTarget` 把跳转目标算成 `操作数起点 + d`，',
    '   而 CLI 的定义是 `操作数起点 + 1 + d` —— **每一个条件跳转的目标都偏了 1 字节**，',
    '   正好落在"操作数中间"，于是**条件成立时走哪条路被读反**。',
    '   修好后真实结构是：`godMode ? 地形分支 : (WorkToBuild != 0 ? 蓝图分支 : 地形分支)`，',
    '   木地板的 `WorkToBuild != 0` ⇒ 走 **`PlaceBlueprintForBuild`**。',
    '3. **三条教训**：',
    '   - **IL/元数据只是旁证，行为必须实测。**',
    '   - **"IL 说不通但实测有结果"时，先怀疑工具**，别怀疑游戏。',
    '   - **断言要对着数据写。** 回读已经打印出 `Blueprint_WoodPlankFloor` 了，',
    '     我还断言 `blueprintDef == null` 并判"失败" —— 报告和数据自相矛盾早该被发现。',
    '',
    '> 本脚本只**附着**到已在运行的游戏，自己绝不启动游戏。',
    '> ⚠ debug 开局自带 662 面墙 + 28 扇门 ⇒ 验收必须走 `run_shelter_force`（测试专用）。',
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
