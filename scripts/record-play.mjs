/**
 * **人类示范录制器** —— 记录你玩殖民地时做的每一件事。
 *
 * ## 用户的想法（2026-10-03）
 *
 * > 「我想去另外写一个插件，就是待会我自己开一个测试的殖民地，然后**实际的玩一次**，
 * >  就是度过 15 天的时间。我会做我认为一个殖民地开局**必须要做的那几件事**，包括建造。
 * >  你也不用把袭击屏蔽掉，我可以接袭击，正常难度就行。
 * >  然后我想让你写的那个插件，就是他会**记录我的每一个行为**……
 * >  比如说我在哪做了，对哪个部分做了什么样的操作。现在就关注**存储区放哪**，
 * >  然后每个存储区我给他**设置了什么样的内容**，等等这些。
 * >  如果能够把这些流程记录下来之后，可以**从里面剥离出**一些
 * >  **每一个殖民地都适用的流程**。那我所谓的流程就是说……
 * >  至少一开始我会做哪几件事情，先把这个东西明确下来，
 * >  然后我们再给这些事情**上一些规则**，让这些事情在每一个殖民地开局都能够被执行到。
 * >  我感觉这有点像是那种**人类教学，然后 AI 学习**的这样的一种模式。」
 *
 * ## 为什么用「快照 + 差分」
 *
 * 游戏**不提供**"用户操作历史"。你划的种植区、勾的 filter、改的优先级，
 * 都是**已经发生完**的状态，没有事件可以监听。
 *
 * 所以只能：**周期拉全图快照 → 和上一张对比 → 差异 = 你做的操作**。
 * 好处是不用 hook 鼠标/键盘，也不依赖任何 UI 事件，而且**记录的是真实结果**
 * （而不是"你点了哪个按钮"）。
 *
 * ## 记录什么
 *
 * | 类别 | 差分出来的"行为" |
 * | --- | --- |
 * | 区域 | 新增/删除/改大小（种植区、储藏区） |
 * | 过滤器 | 储藏区**允许/禁止的 Def 变了**（用户特别关心的） |
 * | 建筑 | 新增蓝图/框架/已建成（按 Def + 位置） |
 * | 工作优先级 | 某人某个工作类型改了 |
 * | 地图标记 | 新增采矿/拆除标记 |
 *
 * ⚠ 只**附着**到已在运行的游戏。录制期间**不要**让 mod 的域动手（否则分不清是谁干的）
 *   —— 脚本开始时会先关掉 mod 的自动调度。
 *
 * 用法：
 *   node scripts/record-play.mjs --minutes 60          # 录 60 分钟
 *   node scripts/record-play.mjs --interval 4          # 每 4 秒拍一次
 *   node scripts/record-play.mjs --tag human-demo-1
 */
import fs from 'node:fs';
import path from 'node:path';
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor, WS } from '../src/launcher.js';

const PROFILE = 'vanilla';
const argv = process.argv.slice(2);
const argNum = (n, d) => {
  const i = argv.indexOf(n);
  if (i < 0) return d;
  const v = Number(argv[i + 1]);
  return Number.isFinite(v) ? v : d;
};
const argStr = (n, d) => {
  const i = argv.indexOf(n);
  return i >= 0 && argv[i + 1] ? argv[i + 1] : d;
};

const MINUTES = argNum('--minutes', 60);
const INTERVAL_SEC = argNum('--interval', 4);
const TAG = argStr('--tag', 'human-demo');

const OUT_DIR = path.join(WS, 'runs', 'human-demo');
const STAMP = new Date().toISOString().replace(/[:.]/g, '-');
const EVENTS = path.join(OUT_DIR, `${TAG}-${STAMP}-events.jsonl`);
const SNAPS = path.join(OUT_DIR, `${TAG}-${STAMP}-snapshots.jsonl`);
const REPORT = path.join(OUT_DIR, `${TAG}-${STAMP}-report.md`);

const log = (...a) => console.log(...a);

let client = null;
let stop = false;
const events = [];
let snapCount = 0;

async function call(name, args = {}, timeoutMs = 60000) {
  return client.callTool(name, args, { timeoutMs });
}

// ─────────────────────────────────────────────────────────────
// 差分：把两张快照的差异变成"人类行为"
// ─────────────────────────────────────────────────────────────

/** 把 zones 数组转成 id → zone 的 map */
function zoneMap(snap) {
  const m = new Map();
  for (const z of snap?.zones || []) m.set(String(z.id), z);
  return m;
}

/** 比较两个细胞集合，返回 {added, removed}（用 "x,z" 字符串比对） */
function cellDiff(a, b) {
  const A = new Set((a || []).map(([x, z]) => `${x},${z}`));
  const B = new Set((b || []).map(([x, z]) => `${x},${z}`));
  const added = [...B].filter((k) => !A.has(k));
  const removed = [...A].filter((k) => !B.has(k));
  return { added, removed };
}

/** 把快照差分成人能读的"行为"列表 */
function diffToActions(prev, cur) {
  const acts = [];
  if (!prev) return acts;

  const dTick = (cur.tick ?? 0) - (prev.tick ?? 0);
  const at = { tick: cur.tick, gameDay: Math.floor((cur.tick ?? 0) / 60000) };

  // ① 区域变化
  const pz = zoneMap(prev), cz = zoneMap(cur);
  for (const [id, z] of cz) {
    if (!pz.has(id)) {
      acts.push({
        ...at, kind: 'zone.create', zoneId: id, zoneType: z.type,
        label: z.label, cells: z.cellCount,
        detail: `新建区域 ${z.type}#${id}（${z.cellCount} 格）${z.plantDef ? ' 作物=' + z.plantDef : ''}`,
      });
      // 新建时过滤器内容本身就是"设置"，单独记一条（用户特别关心）
      if (z.filter) {
        acts.push({
          ...at, kind: 'zone.setFilter', zoneId: id, zoneType: z.type,
          allowed: z.filter.allowed, disallowed: z.filter.disallowed,
          detail: `区域#${id} 过滤器：允许 [${z.filter.allowed.join(',')}] 禁止 [${z.filter.disallowed.join(',')}]`,
        });
      }
      continue;
    }
    const p = pz.get(id);

    // 改大小
    const cd = cellDiff(p.cells, z.cells);
    if (cd.added.length || cd.removed.length) {
      acts.push({
        ...at, kind: 'zone.resize', zoneId: id, zoneType: z.type,
        added: cd.added.length, removed: cd.removed.length,
        addedSample: cd.added.slice(0, 8), removedSample: cd.removed.slice(0, 8),
        detail: `区域#${id}（${z.type}）改大小：+${cd.added.length} / -${cd.removed.length} 格`,
      });
    }

    // 换算物
    if ((p.plantDef || null) !== (z.plantDef || null)) {
      acts.push({
        ...at, kind: 'zone.setPlant', zoneId: id,
        from: p.plantDef, to: z.plantDef,
        detail: `区域#${id} 作物 ${p.plantDef} → ${z.plantDef}`,
      });
    }

    // ★ 换过滤器（用户最关心的：存储区"设置了什么内容"）
    const pf = JSON.stringify(p.filter || null);
    const cf = JSON.stringify(z.filter || null);
    if (pf !== cf) {
      const beforeAllowed = new Set(p.filter?.allowed || []);
      const afterAllowed = new Set(z.filter?.allowed || []);
      const nowAllowed = [...afterAllowed].filter((d) => !beforeAllowed.has(d));
      const nowDisallowed = [...beforeAllowed].filter((d) => !afterAllowed.has(d));
      acts.push({
        ...at, kind: 'zone.setFilter', zoneId: id, zoneType: z.type,
        nowAllowed, nowDisallowed,
        allowedAfter: z.filter?.allowed, disallowedAfter: z.filter?.disallowed,
        detail: `区域#${id} 改过滤器：改为允许 [${nowAllowed.join(',')}]，改为禁止 [${nowDisallowed.join(',')}]`,
      });
    }
  }
  for (const [id, z] of pz) {
    if (!cz.has(id)) {
      acts.push({
        ...at, kind: 'zone.delete', zoneId: id, zoneType: z.type,
        detail: `删除区域 ${z.type}#${id}`,
      });
    }
  }

  // ② 建筑/物品变化（按 Def 分组，只报数量变化 + 位置样本）
  const pt = prev.thingGroups || {}, ct = cur.thingGroups || {};
  const allKeys = new Set([...Object.keys(pt), ...Object.keys(ct)]);
  for (const k of allKeys) {
    const a = pt[k] || [], b = ct[k] || [];
    if (a.length === b.length) {
      // 数量没变但位置可能整体变了（比如移动）—— 只在数量变化时报，避免噪音
      continue;
    }
    const d = cellDiff(a, b);
    const delta = b.length - a.length;
    // 过滤掉"物品被搬来搬去"的噪音：只关心建造类 和 数量明显变化
    const isBuild = k.startsWith('BP:') || k.startsWith('FR:');
    if (!isBuild && Math.abs(delta) < 3) continue;

    acts.push({
      ...at, kind: isBuild ? 'build' : 'thing',
      defName: k,
      delta,
      addedSample: d.added.slice(0, 10),
      removedSample: d.removed.slice(0, 10),
      detail: isBuild
        ? `${k} 数量 ${a.length} → ${b.length}（${delta > 0 ? '+' : ''}${delta}）位置样本 ${JSON.stringify(d.added.slice(0, 6))}`
        : `${k} 数量 ${a.length} → ${b.length}`,
    });
  }

  // ③ 工作优先级变化
  const pw = new Map((prev.workPriorities || []).map((w) => [w.name, w.priorities]));
  for (const w of cur.workPriorities || []) {
    const before = pw.get(w.name);
    if (!before) continue;                       // 新人加入，不算改优先级
    const changed = [];
    for (const [k, v] of Object.entries(w.priorities || {})) {
      if (before[k] !== v) changed.push(`${k}: ${before[k] ?? '-'} → ${v}`);
    }
    for (const k of Object.keys(before)) {
      if (!(k in (w.priorities || {}))) changed.push(`${k}: ${before[k]} → 关闭`);
    }
    if (changed.length) {
      acts.push({
        ...at, kind: 'workPriority', pawn: w.name, changed,
        detail: `工作优先级（${w.name}）：${changed.join('；')}`,
      });
    }
  }

  // ④ 地图标记变化
  const pd = prev.designationGroups || {}, cdg = cur.designationGroups || {};
  for (const k of new Set([...Object.keys(pd), ...Object.keys(cdg)])) {
    const a = pd[k] || [], b = cdg[k] || [];
    if (a.length === b.length) continue;
    const d = cellDiff(a, b);
    acts.push({
      ...at, kind: 'designation', defName: k,
      delta: b.length - a.length,
      addedSample: d.added.slice(0, 12),
      detail: `标记 ${k}：${a.length} → ${b.length}（+${d.added.length} / -${d.removed.length}）样本 ${JSON.stringify(d.added.slice(0, 8))}`,
    });
  }

  // ⑤ 殖民者位置（低频，只用于还原"他在哪操作"）
  if (dTick > 600) {
    acts.push({
      ...at, kind: 'whereabouts', pawns: cur.pawns,
      detail: `殖民者位置：${(cur.pawns || []).map((p) => `${p.name}(${p.x},${p.z})`).join(' ')}`,
    });
  }

  return acts;
}

// ─────────────────────────────────────────────────────────────

async function main() {
  const info = readBridgeInfo(logPathFor(PROFILE));
  if (!info) throw new Error('没有桥接信息 —— 先 node src/cli.js start');
  const { client: c } = await connectGabp(info, { requestTimeoutMs: 90000 });
  client = c;

  fs.mkdirSync(OUT_DIR, { recursive: true });

  // ── `--dry-run`：只拍一张快照，打印它到底读到了什么 ──
  //
  // 为什么要这个模式：**差分的质量完全取决于快照的内容**。
  // 如果快照里"区域/过滤器/优先级"都是空的，那录一天也差分不出东西来。
  // 所以录之前必须先确认"原料够不够"。
  if (argv.includes('--dry-run')) {
    log('=== 快照试拍（--dry-run）===');
    const snap = await call('foodprobe/colony_snapshot', { includeCells: true }, 90000);
    if (snap?.success !== true) {
      log(`  ✗ 快照失败：${snap?.message}`);
      return;
    }
    log(`  tick=${snap.tick}  mapId=${snap.mapId}`);
    log(`  区域 ${snap.zoneCount} 个`);
    for (const z of snap.zones || []) {
      log(`    #${z.id} ${z.type} ${z.cellCount}格 植物=${z.plantDef ?? '-'}`);
      if (z.filter) {
        log(`       过滤器 允许[${(z.filter.allowed || []).join(',')}] 禁止[${(z.filter.disallowed || []).join(',')}]`);
      }
    }
    const tg = snap.thingGroups || {};
    const keys = Object.keys(tg);
    log(`  建筑/物品分组 ${keys.length} 类（共 ${keys.reduce((n, k) => n + tg[k].length, 0)} 个）`);
    for (const k of keys.slice(0, 12)) log(`    ${k}: ${tg[k].length}`);
    log(`  地图标记 ${Object.keys(snap.designationGroups || {}).length} 类`);
    for (const [k, v] of Object.entries(snap.designationGroups || {})) log(`    ${k}: ${v.length}`);
    log(`  工作优先级 ${(snap.workPriorities || []).length} 人`);
    for (const w of snap.workPriorities || []) {
      log(`    ${w.name}: ${Object.keys(w.priorities || {}).length} 个工作类型已启用`);
    }
    log(`  殖民者 ${(snap.pawns || []).length} 人`);
    for (const p of snap.pawns || []) log(`    ${p.name} (${p.x},${p.z})`);
    log('');
    log('  ★ 判定"原料够不够"：');
    log(`     区域数 ${snap.zoneCount}（你划的区会出现在这里）`);
    log(`     物品分组 ${keys.length}（你下的蓝图/建的墙会出现）`);
    log(`     优先级人数 ${(snap.workPriorities || []).length}（你改的优先级会出现）`);
    log('     如果这些都 > 0，差分就能录出行为。');
    const one = JSON.stringify(snap);
    log(`     快照体积约 ${(one.length / 1024).toFixed(1)} KB`);
    return;
  }

  const evStream = fs.createWriteStream(EVENTS, { flags: 'a' });
  const snStream = fs.createWriteStream(SNAPS, { flags: 'a' });

  log('════════════════════════════════════════════');
  log('  人类示范录制器');
  log('════════════════════════════════════════════');
  log(`  时长    ${MINUTES} 分钟`);
  log(`  间隔    ${INTERVAL_SEC} 秒`);
  log(`  事件    ${EVENTS}`);
  log(`  快照    ${SNAPS}`);
  log('');
  log('  ⚠ 录之前请确认：');
  log('    ① 游戏里已经是你要玩的那张殖民地');
  log('    ② 你可以随时 Ctrl+C 停止（已录的部分不会丢）');
  log('');

  // 先关掉 mod 的自动调度，免得它的行为和你的混在一起
  try {
    const r = await call('foodprobe/disable_autorun', {}, 30000);
    log(`  已关掉 mod 自动调度：${r?.result ?? r?.message ?? '（没有这个工具，请手动确认）'}`);
  } catch (e) {
    log(`  ⚠ 关自动调度失败：${e.message}（录制仍会继续，但日志里可能混入 mod 的行为）`);
  }
  log('');

  let prev = null;
  const deadline = Date.now() + MINUTES * 60 * 1000;
  let lastLog = 0;

  process.on('SIGINT', () => { stop = true; log('\n  收到 Ctrl+C，收尾中...'); });

  while (!stop && Date.now() < deadline) {
    let snap = null;
    try {
      snap = await call('foodprobe/colony_snapshot', { includeCells: true }, 90000);
    } catch (e) {
      log(`  ⚠ 快照失败：${e.message.split('\n')[0]}`);
      await new Promise((r) => setTimeout(r, INTERVAL_SEC * 1000));
      continue;
    }

    if (snap?.success !== true) {
      await new Promise((r) => setTimeout(r, INTERVAL_SEC * 1000));
      continue;
    }

    snapCount++;
    snStream.write(JSON.stringify(snap) + '\n');

    if (prev) {
      const acts = diffToActions(prev, snap);
      // 丢掉纯位置噪音
      const meaningful = acts.filter((a) => a.kind !== 'whereabouts');
      for (const a of meaningful) {
        events.push(a);
        evStream.write(JSON.stringify(a) + '\n');
      }
      if (meaningful.length) {
        const day = ((snap.tick ?? 0) / 60000).toFixed(2);
        for (const a of meaningful) log(`  [+${day}天] ${a.detail}`);
      }
    }
    prev = snap;

    const now = Date.now();
    if (now - lastLog > 60000) {
      lastLog = now;
      const day = ((snap.tick ?? 0) / 60000).toFixed(2);
      const left = Math.max(0, Math.round((deadline - now) / 60000));
      log(`  ── 录制中：第 ${day} 天，快照 ${snapCount} 张，行为 ${events.length} 条，剩余 ${left} 分钟 ──`);
    }

    await new Promise((r) => setTimeout(r, INTERVAL_SEC * 1000));
  }

  evStream.end();
  snStream.end();

  // ── 汇总报告 ──
  const byKind = {};
  for (const e of events) byKind[e.kind] = (byKind[e.kind] || 0) + 1;

  const firstDays = events.filter((e) => (e.gameDay ?? 0) <= 3);

  const lines = [
    '# 人类示范录制报告',
    '',
    `时间：${new Date().toISOString()}`,
    `快照：${snapCount} 张（每 ${INTERVAL_SEC} 秒一张）`,
    `行为：${events.length} 条`,
    '',
    '## 行为分类统计',
    '',
    '| 类别 | 条数 |',
    '| --- | --- |',
    ...Object.entries(byKind).sort((a, b) => b[1] - a[1]).map(([k, n]) => `| ${k} | ${n} |`),
    '',
    `## 开局前 3 天的行为（${firstDays.length} 条）`,
    '',
    '> 这一段是**最关键**的 —— 用户说的"开局一定要做的事情"就在这里。',
    '',
    '```',
    ...firstDays.map((e) => `[+${(e.gameDay ?? 0).toFixed(2)}天] ${e.kind}: ${e.detail}`),
    '```',
    '',
    '## 全部行为',
    '',
    '```',
    ...events.map((e) => `[+${(e.gameDay ?? 0).toFixed(2)}天] ${e.kind}: ${e.detail}`),
    '```',
    '',
    '## 原始文件',
    '',
    `- 事件流：\`${path.relative(WS, EVENTS)}\`（JSONL，一行一条）`,
    `- 快照流：\`${path.relative(WS, SNAPS)}\`（JSONL，可重新差分出不同粒度）`,
  ];
  fs.writeFileSync(REPORT, lines.join('\n'), 'utf8');

  log('');
  log('════════════════════════════════════════════');
  log(`  录制结束：${snapCount} 张快照，${events.length} 条行为`);
  log(`  报告：${REPORT}`);
  log('════════════════════════════════════════════');
}

main().catch((e) => {
  console.error('\n[错误] ' + e.message);
  process.exitCode = 1;
});
