#!/usr/bin/env node
/**
 * show-snapshot.mjs —— **单张快照逐项解读**
 *
 * 目的：让用户**第一次真正看清"录下来的到底是什么"**。
 *   `analyze-snapshots.mjs` 看的是"294 张之间的变化"；
 *   本脚本反过来：**只拿一张**，把它的每个字段逐项摊开成人能读的样子。
 *
 * 用法：
 *   node scripts/show-snapshot.mjs [文件] [--index N | --last] [--full] [--out 文件.md] [--quiet]
 *
 *   文件      省略 → 快照目录里**最新**的 .jsonl
 *   --index N 第 N 张（1 基，与 seq 对齐）
 *   --last    最后一张（**默认**）
 *   --full    大字典全打（默认只打前 15 个 def；格子坐标多于 6 个时默认只留前 3 个）
 *   --out P   把同一份文本写进 P（报告用；.md 后缀可省）
 *   --quiet   不往 stdout 打，只写 --out 的文件（方便拼报告）
 *
 * 硬约束（本项目的纪律）：
 *   - **零第三方依赖**，只用 node 内置 ESM。
 *   - **流式**：逐行 `readline` 读，只对命中的那一行 `JSON.parse`。
 *     其余行读完即弃，几百 MB 也不会吃爆内存；内存占用 ≈ 单行大小。
 *   - **只读**：不写快照文件、不改任何源文件。
 *
 * 字段权威定义：
 *   - `src/colonyrecorder/ColonySnapshot.cs`   —— 写了什么字段
 *   - `src/colonyrecorder/SnapshotReaders.cs`  —— 每个字段是谁读的、为什么读不到
 *
 * 报告纪律：文中凡"实测"都直接来自数据行；凡"推测"都显式标注 —— 两者不混。
 */

import fs from 'node:fs';
import path from 'node:path';
import readline from 'node:readline';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(__dirname, '..');
const SNAPSHOT_DIR = path.join(ROOT, 'profiles', 'vanilla', 'foodcontrol-snapshots');

const VERSION = '1.0.0';
const TOP_DEFS = 15;              // 大字典默认展示前几个 def
const SAMPLE_ITEMS = 3;           // 每面结构示例取前几条
const TICKS_PER_DAY = 60000;

// ──────────────────────────────────────────────────────────── 小工具

const fmt = (v, d = 1) => (typeof v === 'number' && Number.isFinite(v) ? v.toFixed(d) : String(v ?? '-'));
const int = (v) => (typeof v === 'number' && Number.isFinite(v) ? String(Math.trunc(v)) : String(v ?? '-'));
const pad = (s, n) => String(s).padEnd(n, ' ');

function ensureDir(p) { fs.mkdirSync(p, { recursive: true }); }

/** 输出收集器：同时喂 stdout 和（可选的）markdown 文件；--quiet 时只写文件 */
function makeSink(outFile, quiet) {
  let fh = null;
  if (outFile) { ensureDir(path.dirname(outFile)); fh = fs.createWriteStream(outFile, { encoding: 'utf8' }); }
  return {
    /** line(fmt, ...args)：支持 %d/%s 占位（与既有脚本风格一致；无参时原样输出） */
    line(s = '', ...args) {
      let out = String(s);
      if (args.length) {
        let i = 0;
        out = out.replace(/%[ds]/g, () => String(args[i++]));
      }
      if (!quiet) process.stdout.write(out + '\n');
      if (fh) fh.write(out + '\n');
    },
    async close() { if (fh) await new Promise((r) => fh.end(r)); },
  };
}

/**
 * 紧凑 JSON：坐标数组 `[[x,z],...]` 压成像 `[109,155]` 这样一行一个，
 * 否则大字典的结构示例会刷屏（cells 几百个会把整屏吃掉）。
 * 超过 INLINE_MAX 个坐标就只留前 15 个 + 省略标记 —— 示例只为"看清结构"。
 */
const INLINE_MAX = 6;
const INLINE_STR_ARRAY_CHARS = 110;
function compact(v, indent = 0, pad = '  ') {
  const p = pad.repeat(indent);
  const p1 = pad.repeat(indent + 1);
  const scalar = (x) => (typeof x === 'string' ? JSON.stringify(x) : String(x));
  const isPair = (x) => Array.isArray(x) && x.length === 2 && x.every((n) => typeof n === 'number');
  const isStr = (x) => typeof x === 'string';
  if (Array.isArray(v)) {
    if (v.every(isPair)) {
      const shown = v.length > INLINE_MAX ? v.slice(0, 3) : v;
      const tail = v.length > INLINE_MAX ? `, …(+${v.length - 3} 个)` : '';
      return '[' + shown.map((x) => `[${x[0]},${x[1]}]`).join(', ') + tail + ']';
    }
    if (!v.length) return '[]';
    // 短的字符串数组（如 allowed/disallowed）挤成一行，避免把示例撑成几十行
    if (v.every(isStr)) {
      const one = '[' + v.map((s) => JSON.stringify(s)).join(', ') + ']';
      if (one.length <= INLINE_STR_ARRAY_CHARS) return one;
    }
    // 其余数组：**贪心打包**，尽量一行塞几个（成员列表之类不再一行一个）
    const parts = v.map((x) => compact(x, indent + 1, pad));
    const lines = [];
    let cur = p1;
    for (let i = 0; i < parts.length; i++) {
      const piece = parts[i] + (i < parts.length - 1 ? ',' : '');
      const multi = piece.includes('\n');
      if (multi) {
        if (cur.trim()) { lines.push(cur); cur = p1; }
        lines.push(p1 + piece);
        continue;
      }
      if (cur.trim() && cur.length + 1 + piece.length > 110) { lines.push(cur); cur = p1; }
      cur += (cur.trim() ? ' ' : '') + piece;
    }
    if (cur.trim()) lines.push(cur);
    return '[\n' + lines.join('\n') + '\n' + p + ']';
  }
  if (v && typeof v === 'object') {
    const ks = Object.keys(v);
    if (!ks.length) return '{}';
    return '{\n' + ks.map((k) => `${p1}${JSON.stringify(k)}: ${compact(v[k], indent + 1, pad)}`).join(',\n') + '\n' + p + '}';
  }
  return scalar(v);
}

/** 把 compact 出来的多行块整体缩进 */
function indented(text, n = 4) {
  const pad = ' '.repeat(n);
  return text.split('\n').map((l) => pad + l).join('\n');
}

function cellStr(c) { return `(${c[0]},${c[1]})`; }

/** tick → 游戏内时间（RimWorld：1 天 = 60000 tick，1 小时 = 2500 tick） */
function gameTime(tick) {
  const t = tick % TICKS_PER_DAY;
  const h = Math.floor(t / 2500), m = Math.floor((t % 2500) / 2500 * 60);
  return `第 ${Math.floor(tick / TICKS_PER_DAY)} 天 ${String(h).padStart(2, '0')}:${String(m).padStart(2, '0')}`;
}

// ──────────────────────────────────────────────────────────── 参数

function parseArgs(argv) {
  const o = { file: null, index: null, last: true, full: false, out: null };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--index') { o.index = Number(argv[++i]); o.last = false; }
    else if (a === '--last') { o.last = true; o.index = null; }
    else if (a === '--full') o.full = true;
    else if (a === '--quiet' || a === '-q') o.quiet = true;
    else if (a === '--out') o.out = argv[++i];
    else if (a === '--help' || a === '-h') { o.help = true; }
    else if (!a.startsWith('-') && !o.file) o.file = a;
    else throw new Error('无法识别的参数：' + a);
  }
  if (o.out && !/\.(md|txt)$/i.test(o.out)) o.out += '.md';
  if (o.out) o.out = path.resolve(o.out);
  return o;
}

function pickLatestFile() {
  if (!fs.existsSync(SNAPSHOT_DIR)) throw new Error('快照目录不存在：' + SNAPSHOT_DIR);
  const files = fs.readdirSync(SNAPSHOT_DIR)
    .filter((f) => f.toLowerCase().endsWith('.jsonl'))
    .map((f) => {
      const p = path.join(SNAPSHOT_DIR, f);
      const st = fs.statSync(p);
      return { p, mtime: st.mtimeMs, size: st.size };
    })
    .sort((a, b) => b.mtime - a.mtime);
  if (!files.length) throw new Error('快照目录里没有 .jsonl：' + SNAPSHOT_DIR);
  return files[0];
}

/**
 * 流式扫描，只挑出一行。
 * `index === null` → 取最后一张（顺带数总行数 / 校验 seq）。
 * 返回 {obj, raw, lineNo, totalLines, sizeBytes, head}`。
 */
async function pickLine(file, index) {
  const total = fs.statSync(file).size;
  const rl = readline.createInterface({ input: fs.createReadStream(file, { encoding: 'utf8' }), crlfDelay: Infinity });
  let n = 0, hit = null, hitNo = 0, first = null;
  for await (const line of rl) {
    n++;
    if (n === 1) first = line;
    if (index === null) { hit = line; hitNo = n; }
    else if (n === index) { hit = line; hitNo = n; break; }
  }
  if (!hit) throw new Error(`文件里只有 ${n} 行，取不到第 ${index ?? n} 张`);
  return { obj: JSON.parse(hit), raw: hit, lineNo: hitNo, totalLines: n, sizeBytes: total, head: first };
}

// ──────────────────────────────────────────────────────────── 判定辅助

/** 每个 ReadingBlock 在快照里对应的"真对象"计数（用于核对 health[].items） */
function actualCounts(o) {
  const sumDict = (d) => (d && typeof d === 'object' ? Object.values(d).reduce((s, v) => s + (Array.isArray(v) ? v.length : 0), 0) : 0);
  return {
    zones: Array.isArray(o.zones) ? o.zones.length : 0,
    storageGroups: Array.isArray(o.storageGroups) ? o.storageGroups.length : 0,
    things: sumDict(o.things),
    pawns: Array.isArray(o.pawns) ? o.pawns.length : 0,
    bills: Array.isArray(o.bills) ? o.bills.length : 0,
    // research 的 items = 已研究完成数（SnapshotReaders: ctx.ItemsRead += finishedCount）
    research: o.research && typeof o.research.finishedCount === 'number' ? o.research.finishedCount : 0,
    wealth: 4,
    designations: sumDict(o.designations),
  };
}

/**
 * 逐块判"可疑 0 项 / 读法失效"。
 *
 * 规则（与 `SnapshotReaders.cs` 的实现一一对应）：
 *   - `ran=false`            → 读法根本没跑（注册表层面出问题）→ **读法失效**。
 *   - `error` 非空           → **读取失败**（异常被注册表捕获）。
 *   - `ran=true, items=0, error=""` → 字面上是"这一局真的没有这个对象"。
 *     但要**再核一次**：该块对应的快照字段是否真的空。
 *       字段也空   → **可信的空**（如早期没有区域/没有生产队列）。
 *       字段非空   → **items 与字段矛盾** → 读法失效（自报数与实际不符）。
 *   - `warnings` 非空        → 部分失败（如某个 Def 读不到），主体仍然读到了。
 */
function auditHealth(o) {
  const act = actualCounts(o);
  const rows = (o.health || []).map((h) => {
    const id = h.reader;
    const a = Object.prototype.hasOwnProperty.call(act, id) ? act[id] : null;
    const flags = [];
    if (!h.ran) flags.push('读法没跑');
    if (h.error) flags.push('读取失败');
    const zero = h.ran && !h.error && !h.items;
    if (zero) flags.push(a === 0 ? '可疑 0 项（字段也空 → 可信的空）' : `可疑 0 项（字段有 ${a} 条 → 矛盾）`);
    if (a !== null && h.items !== a && !(id === 'things')) flags.push(`items(${h.items}) ≠ 字段(${a})`);
    if (h.warnings && h.warnings.length) flags.push(`部分失败 ${h.warnings.length} 条`);
    return { id, ran: h.ran, items: h.items, error: h.error || '', warnings: h.warnings || [], actual: a, flags };
  });
  const bad = rows.filter((r) => r.flags.some((f) => !f.startsWith('可疑 0 项（字段也空')));
  const zeroTrusted = rows.filter((r) => r.flags.some((f) => f.startsWith('可疑 0 项（字段也空')));
  return { rows, bad, zeroTrusted };
}

// ──────────────────────────────────────────────────────────── 渲染

function hr(snk, title) {
  snk.line('');
  snk.line('─'.repeat(78));
  snk.line('▍' + title);
  snk.line('─'.repeat(78));
}

function fieldLine(snk, name, value, note) {
  snk.line(`  ${pad(name, 22)} : ${value}${note ? '    ' + note : ''}`);
}

function render(snk, ctx) {
  const { o, raw, lineNo, totalLines, sizeBytes, file, pick, opts } = ctx;
  const s = o.sampling || {};
  const now = new Date();

  // ── 头部 ─────────────────────────────────────────────
  snk.line('# 单张快照逐项解读');
  snk.line('');
  snk.line(`- 脚本：\`scripts/show-snapshot.mjs\` v${VERSION}（零依赖 ESM，流式读）`);
  snk.line(`- 文件：\`${path.relative(ROOT, file).replace(/\\/g, '/')}\``);
  snk.line(`- 选中的是：**第 ${lineNo} 张 / 共 ${totalLines} 行**（${pick === 'last' ? '最后一张' : `--index ${lineNo}`}）`);
  snk.line(`- 文件体积：${(sizeBytes / 1048576).toFixed(1)} MB；这一行原始长度：${raw.length.toLocaleString('en-US')} 字符`);
  snk.line(`- 解读时间：${now.toLocaleString('sv-SE')}（本地）`);
  snk.line('');

  hr(snk, '① 头部（这张快照的身份 + 采样元信息）');
  snk.line('  【实测】');
  fieldLine(snk, 'tick', `${int(o.tick)}`, `（= 游戏内 ${gameTime(o.tick)}；采样基准，不用真实时间）`);
  fieldLine(snk, 'day', `${int(o.day)}`, `（tick/60000）`);
  fieldLine(snk, 'mapId', `${int(o.mapId)}`, '（地图 uniqueID，这张是第几张地图）');
  fieldLine(snk, 'seq', `${int(o.seq)}`, '（同一会话内递增序号）');
  fieldLine(snk, 'wallClock', JSON.stringify(o.wallClock), '（真实时间，仅用于排查，不参与采样）');
  snk.line('  sampling（自报的采样元信息）：');
  fieldLine(snk, '  intervalTicks', `${int(s.intervalTicks)}`, '= 设定的采样间隔（游戏 tick）');
  fieldLine(snk, '  sincePrevTick', `${int(s.sincePrevTick)}`, s.intervalTicks ? `（= 间隔的 ${fmt(s.sincePrevTick / s.intervalTicks, 2)} 倍）` : '');
  fieldLine(snk, '  totalSnapshots', `${int(s.totalSnapshots)}`, '= 这一局累计第几张（自报）');
  fieldLine(snk, '  playedTicks', `${int(s.playedTicks)}`, `（= 这一局玩到 ${fmt(s.playedTicks / TICKS_PER_DAY, 2)} 天）`);
  fieldLine(snk, '  looksDropped', `${!!s.looksDropped}`, s.looksDropped ? '⚠ 这一张**漏采**了' : '（未判定为漏采）');
  snk.line('  【说明】`health` 记的是 8 个读取块（zones/storageGroups/things/pawns/bills/research/wealth/designations），');
  snk.line('           `extra` 是"加新读取块不用改老字段"的逃生口 —— 这张里 extra 的内容见 ⑩。');

  // ── ⑨ 先算健康，写到后面用 ────────────────────────────
  const audit = auditHealth(o);

  // ── ② 区域 ───────────────────────────────────────────
  hr(snk, `② zones —— 区域（种植区 / 储藏区 / 其他）  共 ${o.zones.length} 个`);
  snk.line('  能回答：**"这块地是干什么的、种什么、多少个格子、格子在哪"** —— 即用户划定的功能分区。');
  snk.line('  【实测】');
  if (!o.zones.length) snk.line('  （空数组：这一张里没有任何区域）');
  o.zones.forEach((z) => {
    snk.line(`  #${z.id}  ${z.type}  "${z.label}"`);
    snk.line(`      cellCount=${z.cellCount}（实测已记录格子 ${z.cells.length} 个${z.cellCount > z.cells.length ? '，**被截断**' : '，未截断'}）`);
    if (z.plantDef) snk.line(`      plantDef=${z.plantDef}   ← 种植物`);
    if (z.allowed.length || z.disallowed.length) {
      snk.line(`      allowed(${z.allowed.length})=[${z.allowed.join(', ')}]`);
      snk.line(`      disallowed(${z.disallowed.length})=[${z.disallowed.join(', ')}]`);
    }
  });
  snk.line(`  结构示例（前 ${Math.min(SAMPLE_ITEMS, o.zones.length)} 个区域完整展开，坐标多于 6 个时只留前 3 个）：`);
  o.zones.slice(0, SAMPLE_ITEMS).forEach((z) => snk.line(indented(compact(z, 2), 2)));

  // ── ③ 存储组 ─────────────────────────────────────────
  hr(snk, `③ storageGroups —— 存储组（游戏自带对象）  共 ${o.storageGroups.length} 个`);
  snk.line('  能回答：**"游戏里的仓储是怎么分组的、每组收了哪些货架/存储区、在哪、允许/禁止存什么"**。');
  snk.line('  【实测】');
  if (!o.storageGroups.length) snk.line('  （空数组：这一张里没有任何存储组）');
  o.storageGroups.forEach((g) => {
    const pcts = g.members && g.members.length ? g.members.map((m) => m.cells && m.cells[0] ? cellStr(m.cells[0]) : '(无格)') : [];
    snk.line(`  #${g.loadId}  "${g.label}"  memberCount=${g.memberCount}  cellCount=${g.cellCount}`);
    snk.line(`      成员位置：${pcts.join(' ') || '（无）'}`);
    snk.line(`      allowed(${g.allowed.length})=[${g.allowed.join(', ')}]`);
    snk.line(`      disallowed(${g.disallowed.length})=[${g.disallowed.join(', ')}]`);
  });
  snk.line('  结构示例：');
  o.storageGroups.slice(0, SAMPLE_ITEMS).forEach((g) => snk.line(indented(compact(g, 2), 2)));

  // ── ④ things ─────────────────────────────────────────
  const defs = Object.entries(o.things || {}).map(([k, v]) => ({ def: k, n: v.length, cells: v }))
    .sort((a, b) => b.n - a.n);
  const allN = defs.reduce((s2, d) => s2 + d.n, 0);
  const bp = defs.filter((d) => d.def.startsWith('BP:'));
  const fr = defs.filter((d) => d.def.startsWith('FR:'));
  hr(snk, `④ things —— 建筑 / 物品 / 蓝图 / 框架  共 ${defs.length} 个 def、${allN} 个对象`);
  snk.line('  能回答：**"地图上有什么东西、分别有多少、在哪"** —— 按 `defName`（游戏里的"型号"）分组，值是坐标列表。');
  snk.line('  前缀约定（读法决定）：`BP:` = 蓝图（还没有的、正被建造），`FR:` = 框架（建到一半）。');
  snk.line('  【实测】');
  snk.line(`  蓝图（BP:）共 ${bp.reduce((a, b) => a + b.n, 0)} 个，分 ${bp.length} 个 def：${bp.map((d) => `${d.def}×${d.n}`).join(', ') || '（无）'}`);
  snk.line(`  框架（FR:）共 ${fr.reduce((a, b) => a + b.n, 0)} 个，分 ${fr.length} 个 def：${fr.map((d) => `${d.def}×${d.n}`).join(', ') || '（无）'}`);
  const show = opts.full ? defs : defs.slice(0, TOP_DEFS);
  snk.line(`  按数量排序${opts.full ? '（--full：全部）' : `（只列前 ${TOP_DEFS} 个；**--full 才全打**）`}：`);
  show.forEach((d, i) => {
    const tag = d.def.startsWith('BP:') ? ' [蓝图]' : d.def.startsWith('FR:') ? ' [框架]' : '';
    const ex = d.cells.slice(0, opts.full ? d.cells.length : 3).map(cellStr).join(' ');
    snk.line(`  ${pad(String(i + 1) + '.', 5)} ${pad(d.def, 34)} ×${pad(d.n, 5)}${tag}   例：${ex}${!opts.full && d.n > 3 ? ' …' : ''}`);
  });
  if (!opts.full && defs.length > TOP_DEFS) {
    const rest = defs.slice(TOP_DEFS);
    snk.line(`  （其余 ${rest.length} 个 def 合计 ${rest.reduce((a, b) => a + b.n, 0)} 个对象，未列出：${rest.slice(0, 12).map((d) => `${d.def}×${d.n}`).join(', ')}${rest.length > 12 ? ' …' : ''}）`);
  }
  snk.line(`  结构示例（前 ${Math.min(SAMPLE_ITEMS, defs.length)} 个 def 完整展开，坐标多于 6 个时只留前 3 个）：`);
  defs.slice(0, SAMPLE_ITEMS).forEach((d) => {
    snk.line(`    ${JSON.stringify(d.def)}: ${compact(d.cells)}`);
  });

  // ── ⑤ pawns ──────────────────────────────────────────
  hr(snk, `⑤ pawns —— 小人  共 ${o.pawns.length} 人`);
  snk.line('  能回答：**"每个人现在什么状态、心情为什么掉、期望着什么档次的生活、饿不饿"**。');
  snk.line('  （读法只取 `map.mapPawns.FreeColonists` —— 只读**自由殖民者**，不含囚犯/奴隶标签/来客/动物。）');
  snk.line('  【实测】逐人完整展开：');
  o.pawns.forEach((p, i) => {
    snk.line(`  ── ${i + 1}/${o.pawns.length}  ${p.name}`);
    snk.line(`     位置        : (${p.x}, ${p.z})`);
    snk.line(`     心情 mood   : ${fmt(p.mood, 3)}  (${(p.mood * 100).toFixed(0)}%)`);
    snk.line(`     食物 food   : ${fmt(p.food, 3)}  (${(p.food * 100).toFixed(0)}%)`);
    snk.line(`     期望 expectation : ${JSON.stringify(p.expectation)}`);
    snk.line(`     负面想法 (${p.negativeThoughts.length}) : ${p.negativeThoughts.length ? p.negativeThoughts.join('  ') : '（无）'}`);
  });

  // ── ⑥ bills ──────────────────────────────────────────
  hr(snk, `⑥ bills —— 生产队列  共 ${o.bills.length} 条`);
  snk.line('  能回答：**"每张工作台上挂着什么活、临时还是重复、有没有被暂停"**。');
  snk.line('  ⚠ 读法备注：`repeatMode` / `targetCount` 是**反射探测**的（不在 `Bill` 基类上），读不到就留空串 —— 空串≠没有设置。');
  snk.line('  【实测】');
  if (!o.bills.length) snk.line('  （空数组）');
  o.bills.forEach((b) => {
    snk.line(`  ${pad(b.bench, 24)} (${b.benchX},${b.benchZ})  ${pad(b.recipe, 32)} "${b.recipeLabel}"`);
    snk.line(`      suspended=${b.suspended}  repeatMode=${JSON.stringify(b.repeatMode)}  targetCount=${JSON.stringify(b.targetCount)}`);
  });
  if (o.bills.length) {
    snk.line('  结构示例：');
    o.bills.slice(0, SAMPLE_ITEMS).forEach((b) => snk.line(indented(compact(b, 2), 2)));
  }

  // ── ⑦ research ───────────────────────────────────────
  const r = o.research || {};
  hr(snk, '⑦ research —— 研究');
  snk.line('  能回答：**"当前在研什么、已经研完几项、是哪几项"**。');
  snk.line('  【实测】');
  fieldLine(snk, 'current', JSON.stringify(r.current), r.current ? '' : '（空 = 这一张真的没在研；见 ⑨ 的读法说明）');
  fieldLine(snk, 'finishedCount', `${int(r.finishedCount)}`);
  snk.line(`  finished(${(r.finished || []).length}) : ${(r.finished || []).join(', ') || '（无）'}`);
  snk.line('  结构示例：');
  snk.line(indented(compact(r, 2), 2));

  // ── ⑧ wealth ─────────────────────────────────────────
  const w = o.wealth || {};
  hr(snk, '⑧ wealth —— 财富（期望的驱动量）');
  snk.line('  能回答：**"殖民地值多少钱、钱堆在哪（物品/建筑/人）"** —— 财富↑ → 期望↑ → 心情↓ 那个环的量化基础。');
  snk.line('  【实测】');
  fieldLine(snk, 'total', fmt(w.total, 1));
  fieldLine(snk, 'items', fmt(w.items, 1), w.total ? `（占 ${fmt((w.items / w.total) * 100, 1)}%）` : '');
  fieldLine(snk, 'buildings', fmt(w.buildings, 1), w.total ? `（占 ${fmt((w.buildings / w.total) * 100, 1)}%）` : '');
  fieldLine(snk, 'pawns', fmt(w.pawns, 1), w.total ? `（占 ${fmt((w.pawns / w.total) * 100, 1)}%）` : '');

  // ── ⑨ designations ───────────────────────────────────
  const dsg = Object.entries(o.designations || {}).map(([k, v]) => ({ def: k, n: v.length })).sort((a, b) => b.n - a.n);
  hr(snk, `⑨ designations —— 地图标记（采矿/拆除等）  共 ${dsg.length} 个 def、${dsg.reduce((a, b) => a + b.n, 0)} 个标记`);
  snk.line('  能回答：**"玩家点了哪些待办（挖这里、拆那个）"** —— 意图的直接痕迹。');
  snk.line('  【实测】');
  if (!dsg.length) snk.line('  （空字典：这一张里没有任何地图标记）');
  dsg.forEach((d) => snk.line(`  ${pad(d.def, 34)} ×${d.n}`));
  if (dsg.length) {
    snk.line('  结构示例：');
    dsg.slice(0, SAMPLE_ITEMS).forEach((d) => snk.line(`    "${d.def}": ${JSON.stringify((o.designations[d.def] || []).slice(0, 6))}`));
  }

  // ── ⑩ extra ──────────────────────────────────────────
  const ex = o.extra || {};
  hr(snk, `⑩ extra —— 扩展逃生口  共 ${Object.keys(ex).length} 项`);
  snk.line('  能回答：**"后来加的读取块往这里塞了什么"**（不改既有字段就能加新面）。');
  snk.line('  【实测】');
  if (!Object.keys(ex).length) snk.line('  （空字典：目前没有任何读取块往这里写东西）');
  else Object.entries(ex).forEach(([k, v]) => snk.line(`  ${pad(k, 24)} : ${String(v).slice(0, 400)}`));

  // ── ⑪ health ─────────────────────────────────────────
  hr(snk, `⑪ health —— 8 个读取块的自报健康  共 ${(o.health || []).length} 块`);
  snk.line('  能回答：**"这一张到底是『真没有』还是『我的读法坏了』"** —— 这是用户"无法知道是否读到了足量"的正面回答。');
  snk.line('  判定规则：ran=false→读法没跑；error≠空→读取失败；items=0 但字段也空→可信的空；items=0 而字段非空→矛盾。');
  snk.line('  【实测】逐块：');
  audit.rows.forEach((row) => {
    snk.line(`  ── ${row.id}`);
    snk.line(`     ran       : ${row.ran}`);
    snk.line(`     items     : ${row.items}${row.actual !== null ? `   （快照里实际条数：${row.actual}）` : ''}`);
    snk.line(`     error     : ${row.error ? JSON.stringify(row.error) : '(空)'}`);
    snk.line(`     warnings  : ${row.warnings.length ? JSON.stringify(row.warnings) : '(空)'}`);
    snk.line(`     判定      : ${row.flags.length ? row.flags.join('；') : '正常'}`);
  });

  // ── ⑫ 结论 ───────────────────────────────────────────
  hr(snk, '⑫ 这一张读到了什么 / 没读到什么');
  snk.line('  【这一张读到了】');
  snk.line(`  - 区域 ${o.zones.length} 个：${o.zones.map((z) => `${z.label}(${z.type.replace('Zone_', '')},${z.cellCount}格${z.plantDef ? ',' + z.plantDef : ''})`).join('；') || '（无）'}`);
  snk.line(`  - 存储组 ${o.storageGroups.length} 个，共 ${o.storageGroups.reduce((a, g) => a + (g.memberCount || 0), 0)} 个成员`);
  snk.line(`  - 建筑/物品 ${defs.length} 个 def / ${allN} 个对象（蓝图 ${bp.reduce((a, b) => a + b.n, 0)}、框架 ${fr.reduce((a, b) => a + b.n, 0)}）`);
  snk.line(`  - 小人 ${o.pawns.length} 人（心情/食物/期望/负面想法全有）`);
  snk.line(`  - 生产队列 ${o.bills.length} 条；研究：在研 ${r.current ? r.current : '无'}、已完成 ${r.finishedCount} 项`);
  snk.line(`  - 财富 ${fmt(w.total, 1)}；地图标记 ${dsg.reduce((a, b) => a + b.n, 0)} 个`);
  snk.line('');
  snk.line('  【读法健康】');
  if (!audit.bad.length) snk.line('  - ✅ **没有任何"读法失效"**：8 个块全部 ran=true、error 为空、items 与快照字段一一对上。');
  else audit.bad.forEach((b) => snk.line(`  - ⚠ ${b.id}：${b.flags.join('；')}`));
  if (audit.zeroTrusted.length) {
    snk.line(`  - ⚠ 有 ${audit.zeroTrusted.length} 个"可疑 0 项"：${audit.zeroTrusted.map((z) => z.id).join(', ')}`);
    snk.line('    → 已交叉核对：**这些块对应的字段同样是空的**，所以是"这一局真的没有这个对象"，不是读法失效。');
  } else snk.line('  - 这一张里没有"可疑 0 项"。');
  snk.line('  - 读法自身的固定截断（不是失效，但会让数字偏小，读报告时要记住）：');
  snk.line('      · 区域 cells 每区最多记 1200 格（超出记 warning）；');
  snk.line('      · 存储组成员若本身是 Zone，最多记 400 格；');
  snk.line('      · things 每个 def 最多记 1500 个位置（超出记 warning）；');
  snk.line('      · designations 每个 def 最多 1500 个；');
  snk.line('      · pawns 只读 FreeColonists；负面想法每人最多 6 条（且只取 offset<0 的）。');
  snk.line('');
  snk.line('  【没读到 —— 用户点了名、但录制器还没加的关注面】');
  snk.line('  1) **房间职能与隔断**：快照里没有 Room 这一层。有 `Wall`×N、`Door`×N 的坐标，但**没有房间对象**');
  snk.line('     → 读不出"这是卧室/厨房/仓库"、读不出"哪几面墙围成了一个房间"、读不出房间面积/是否封闭。');
  snk.line('  2) **墙/建筑的材质**（木头→石砖）：`things` 的 key 是 `defName`，`Wall` 只是一类；');
  snk.line('     实测这一张里 `Wall` 的 defName 就是 `Wall`，**没有"这段墙是什么做的"字段**。');
  snk.line('     能间接猜（有 `BlocksGranite`/`WoodLog` 之类材料），但**不能确定哪一面墙是哪种材质**。');
  snk.line('  3) **小人装备**：pawns 只有 name/x/z/mood/food/expectation/negativeThoughts；');
  snk.line('     **没有**装备栏、武器、衣物、护甲、背包。生产队列里能看到"在做 duster / body strap"，但那只是**在造的**，不是**穿着的**。');
  snk.line('  4) **奴隶身份 / 身份标签**：没有 `guest`/`slave`/`prisoner`/`trader` 之类字段；');
  snk.line('     pawns 只列 FreeColonists，所以奴隶、囚犯、来客**根本不在列表里**。');
  snk.line('     （旁证：这一张的生产队列里有 "make slave body strap" —— 游戏里在造奴隶用品，但快照里查不到谁是奴隶。）');
  snk.line('  5) **游戏内日期**：只有 `tick`/`day`（day = tick/60000），没有"第 3 年 春 第 5 天"这种');
  snk.line('     带**象限/季节/年份**的日历表达；也没有"当前季节/气温/天气"。');
  snk.line('  6) 顺带（同一类缺口）：**没有**动物/囚犯/来客、没有物品数量与质量（只有位置）、');
  snk.line('     没有建筑耐久/损坏、没有床与所有人的归属、没有美观/清洁度、没有温度与电力网、没有事件/信件（本快照是"对象快照"，不是日志）。');
  snk.line('');
  snk.line('  > 【推测】上面"能回答什么"都是**我基于字段结构的解释**；"实测"段落里的数字则直接来自这一行 JSON。');
  snk.line('  > 二者在本文里已分开标注：**实测 = 数据里直接读到的值；推测 = 我对它含义/用途的解释。**');
}

// ──────────────────────────────────────────────────────────── 入口

async function main() {
  let opts;
  try { opts = parseArgs(process.argv.slice(2)); }
  catch (e) { console.error('参数错误：' + e.message + '\n\n' + help()); process.exit(2); }
  if (opts.help) { console.log(help()); return; }

  let file;
  if (opts.file) {
    file = path.resolve(opts.file);
    if (!fs.existsSync(file)) { console.error('文件不存在：' + file); process.exit(2); }
  } else {
    file = pickLatestFile().p;
  }

  const pick = opts.index === null ? 'last' : `index ${opts.index}`;
  const { obj, raw, lineNo, totalLines, sizeBytes } = await pickLine(file, opts.index);

  const snk = makeSink(opts.out, opts.quiet);
  try {
    render(snk, { o: obj, raw, lineNo, totalLines, sizeBytes, file, pick, opts });
    if (opts.out && !opts.quiet) console.log(`\n[show-snapshot] 已写出：${opts.out}`);
  } finally {
    await snk.close();
  }
}

function help() {
  return `show-snapshot.mjs v${VERSION} —— 把**一张**快照逐项打印成人能读的样子

用法：
  node scripts/show-snapshot.mjs [文件] [--index N | --last] [--full] [--out 文件.md] [--quiet]

  文件       省略 → 快照目录里最新的 .jsonl
             （${path.relative(ROOT, SNAPSHOT_DIR).replace(/\\/g, '/')}）
  --index N  取第 N 张（1 基，与 seq 对齐）
  --last     取最后一张（默认）
  --full     大字典全打；默认 things 只列前 ${TOP_DEFS} 个 def
  --out P    同一份文本写进 P（报告用；默认不加 .md 会自动补）
  --quiet    不往 stdout 打，只写 --out 的文件（方便管道拼报告）

示例：
  node scripts/show-snapshot.mjs --last
  node scripts/show-snapshot.mjs --index 200 --out runs/snapshot-analysis/中期快照.md
  node scripts/show-snapshot.mjs profiles/vanilla/foodcontrol-snapshots/colony-20261003-220321.jsonl --index 294 --full

内存：流式逐行读，只 parse 命中的那一行 —— 几百 MB 的文件也不会吃爆内存。
`;
}

main().catch((e) => { console.error('[show-snapshot] 失败：' + (e && e.stack || e)); process.exit(1); });
