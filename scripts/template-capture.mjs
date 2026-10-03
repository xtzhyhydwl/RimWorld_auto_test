/**
 * 把 `foodprobe/capture_grid` 的扫描结果转成建筑样板模板（JSON）。
 *
 * ## 工作流（用户 2026-10-03 定的）
 *
 *   1. 用户在游戏里开上帝模式，手造样板房（卧室 / 仓库 / 厨房 …）
 *   2. `node scripts/template-capture.mjs <id> --x .. --z .. --w .. --h ..`
 *      —— 本脚本调桥接扫描那块矩形，转成模板并写入 `templates/<id>.json`
 *   3. 用户在编辑器里看 / 微调 `accepts`（"这个槽位允许哪些 Def"）
 *   4. 之后 mod 就可以"直接抄"这份样板
 *
 * ## 为什么要有这个脚本（而不是让 mod 直接读）
 *
 * 样板是**规范化的数据**：扫描出来的是"游戏里实际用了什么"，
 * 而模板要的是"**这一类**东西都行"（用户明确要求"床可以是别的床"）。
 * 这一步转换需要**人的判断**，所以放在脚本里、产出可读 JSON、进 git。
 *
 * ⚠ 只**附着**到已在运行的游戏，自己绝不启动游戏。
 *
 * 用法：
 *   node scripts/template-capture.mjs bedroom13 --x 100 --z 100 --w 15 --h 15
 *   node scripts/template-capture.mjs bedroom13 --x 100 --z 100 --w 15 --h 15 --tags bedroom,starter
 *   node scripts/template-capture.mjs --raw runs/capture.json      # 从已保存的扫描结果转换
 */
import fs from 'node:fs';
import path from 'node:path';
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor, WS } from '../src/launcher.js';

const TEMPLATE_DIR = path.join(WS, 'templates');

/**
 * **角色 → 可接受的 Def 列表**（按偏好排序）。
 *
 * 这是"不必百分百还原、只要物块对得上"的落点（用户原话）。
 * 扫描只告诉我们"用户实际用了什么"，这里补上"还允许用什么"。
 *
 * ⚠ 这些列表是**默认值**，用户可以在生成的 JSON 里手改。
 */
const DEFAULT_ACCEPTS = {
  wall: ['Wall'],                                  // 任何墙都行（含 mod 墙，靠 defName 含 wall 判）
  door: ['Door'],
  bed: ['Bed', 'DoubleBed'],                       // ★ 别的床也行
  light: ['TorchLamp', 'StandingLamp', 'Campfire'],// ★ 别的光源也行
  table: ['Table2x2c', 'Table2x4c', 'Table3x3c'],
  seat: ['Stool', 'DiningChair'],
  storage: ['ShelfSmall', 'Shelf'],
  workbench: ['TableButcher', 'ElectricStove', 'CraftingSpot', 'TableStonecutter'],
  furniture: [],                                   // 未知家具：留空，让用户填
};

/**
 * **角色 → 附加标记**（决定放置时怎么判"有没有货"）。
 *
 * ⚠ `needsStuff` 是**踩过坑才有的区分**：
 *   - **墙/门：由材料建成** → `needsStuff: true`。
 *     "库存里有没有 Wall"是**无意义的问题** —— Wall 不是物品，是由木头/钢建的。
 *   - **床/灯：多数是成品** → `needsStuff: false`。
 *     "库存里有没有 Bed 这个物品"**才有意义**（1 个 Bed 物品 → 1 张床蓝图）。
 *
 *   我最初把两者混为一谈，拿 `"Wall"` 去查库存 → 永远 0
 *   → 所有墙槽判"没货" → 48 个槽位全放不下。
 */
const ROLE_FLAGS = {
  wall: { isWallLike: true, needsStuff: true },
  door: { isDoorLike: true, needsStuff: true },
  bed: { isFurniture: true, needsStuff: false },
  light: { isFurniture: true, needsStuff: false },
  table: { isFurniture: true, needsStuff: true },       // 桌子也是材料建的
  seat: { isFurniture: true, needsStuff: true },        // 凳子多数是材料建的
  storage: { isFurniture: true, needsStuff: true },     // 架子是材料建的
  workbench: { isFurniture: true, needsStuff: true },   // 工作台是材料建的
  furniture: { isFurniture: true, needsStuff: true },   // 未知家具：保守按"要材料"
};

function parseArgs(argv) {
  const positional = [];
  const flags = {};
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a.startsWith('--')) {
      const k = a.slice(2);
      const nx = argv[i + 1];
      if (nx !== undefined && !nx.startsWith('--')) { flags[k] = nx; i++; }
      else flags[k] = true;
    } else positional.push(a);
  }
  return { positional, flags };
}

async function callBridge(name, args, timeoutMs = 60000) {
  const info = readBridgeInfo(logPathFor('vanilla'));
  if (!info) throw new Error('没有桥接信息 —— 游戏在跑吗？先 node src/cli.js start（后台任务托管）');
  const { client } = await connectGabp(info, { requestTimeoutMs: timeoutMs });
  try {
    return await client.callTool(name, args, { timeoutMs });
  } finally {
    client.close();
  }
}

/** 扫描结果 → 模板对象 */
function toTemplate(capture, id, name, tags) {
  const rows = capture.trimmedRows;
  const legend = capture.legend || [];

  if (!rows || rows.length === 0) {
    throw new Error('扫描结果里没有 trimmedRows —— 那块矩形里没有建筑？');
  }

  // 代号 → 角色
  const symToRole = {};
  for (const g of legend) {
    symToRole[g.symbol] = { role: g.role, exampleDef: g.exampleDef };
  }

  // 生成 roles：每个用到的代号 → 可接受 Def 列表
  const roles = {};
  const usedSyms = new Set();
  for (const row of rows) for (const ch of row) if (ch !== '.') usedSyms.add(ch);

  const acceptsFromExample = {};
  for (const sym of usedSyms) {
    const info = symToRole[sym];
    const roleName = info ? info.role : 'furniture';
    const example = info ? info.exampleDef : null;

    let accepts = (DEFAULT_ACCEPTS[roleName] || []).slice();
    // 把用户实际用的那个放在最前面（优先用它）
    if (example) {
      accepts = [example, ...accepts.filter((d) => d !== example)];
    }

    const flags = ROLE_FLAGS[roleName] || { isFurniture: true };
    const entry = {
      name: roleName,
      accepts,
      ...flags,
    };
    if (example) entry.exampleDef = example;
    roles[sym] = entry;

    acceptsFromExample[sym] = example;
  }

  const t = {
    id,
    name: name || id,
    origin: 'user',
    size: { w: rows[0].length, h: rows.length },
    tags: tags && tags.length ? tags : ['untagged'],
    rotatable: true,
    mirrorable: true,
    rows,
    roles,
    requires: {
      minAreaCells: rows.length * rows[0].length,
      needsRoofAfterBuild: true,
      minClearance: 1,
    },
    capture: {
      // 保留来源信息，便于回溯"这份样板是从地图哪一块扫出来的"
      requested: capture.requested,
      trim: capture.trim,
      counts: capture.counts,
      defsSeen: capture.defsSeen,
      warnings: capture.warnings || [],
      capturedAt: new Date().toISOString(),
    },
  };
  return t;
}

async function main() {
  const { positional, flags } = parseArgs(process.argv.slice(2));
  fs.mkdirSync(TEMPLATE_DIR, { recursive: true });

  let capture;
  let id = positional[0];
  let name = flags.name || id;
  let tags = flags.tags ? String(flags.tags).split(',').map((s) => s.trim()).filter(Boolean) : [];

  if (flags.raw) {
    // 从已保存的扫描结果转换（离线，不需要游戏在跑）
    const raw = JSON.parse(fs.readFileSync(path.resolve(WS, String(flags.raw)), 'utf8'));
    // 兼容几种包装：桥接原始返回 {result:{...}}、自测存的 {cap:{...}, tpl:{...}}、或直接就是扫描结果
    capture = raw;
    if (capture.trimmedRows === undefined && capture.result) capture = capture.result;
    if (capture.trimmedRows === undefined && capture.cap) capture = capture.cap;
    if (capture.trimmedRows === undefined) {
      throw new Error(
        '这个文件里找不到 trimmedRows。它可能不是 capture_grid 的结果。\n' +
        '  顶层键：' + Object.keys(raw).join(', '));
    }
    console.log(`从 ${flags.raw} 读入扫描结果`);
  } else {
    if (!id) {
      console.error('用法: node scripts/template-capture.mjs <id> --x N --z N --w N --h N [--name 显示名] [--tags a,b]');
      console.error('      node scripts/template-capture.mjs --raw runs/capture.json [--name ...]');
      process.exit(1);
    }
    const x = Number(flags.x), z = Number(flags.z), w = Number(flags.w), h = Number(flags.h);
    if (![x, z, w, h].every((v) => Number.isFinite(v))) {
      console.error('缺少坐标参数：--x --z --w --h 都是必需的');
      process.exit(1);
    }

    console.log(`扫描 (${x},${z}) 起 ${w}x${h} ...`);
    capture = await callBridge('foodprobe/capture_grid', { x, z, w, h }, 90000);
    if (!capture || capture.success !== true) {
      throw new Error('扫描失败：' + (capture?.message || JSON.stringify(capture).slice(0, 200)));
    }

    // 顺手存一份原始扫描，便于回溯
    const rawDir = path.join(WS, 'runs', 'template-captures');
    fs.mkdirSync(rawDir, { recursive: true });
    const rawFile = path.join(rawDir, `${id || 'capture'}-${Date.now()}.json`);
    fs.writeFileSync(rawFile, JSON.stringify(capture, null, 2), 'utf8');
    console.log(`原始扫描已存：${path.relative(WS, rawFile)}`);
  }

  console.log('');
  console.log(`扫描统计：${JSON.stringify(capture.counts)}`);
  if (capture.warnings?.length) {
    console.log('⚠ 警告：');
    for (const w of capture.warnings) console.log('   - ' + w);
  }

  console.log('');
  console.log('裁剪后的样板形状：');
  for (const r of capture.trimmedRows || []) console.log('   ' + r);
  if (capture.trim) console.log(`   在图上位置：(${capture.trim.offsetX},${capture.trim.offsetZ}) 尺寸 ${capture.trim.width}x${capture.trim.height}`);

  if (!id) {
    id = 'template-' + Date.now();
    name = name === undefined ? id : name;
  }

  const t = toTemplate(capture, id, name, tags);
  const outFile = path.join(TEMPLATE_DIR, `${id}.json`);
  fs.writeFileSync(outFile, JSON.stringify(t, null, 2), 'utf8');

  console.log('');
  console.log(`✓ 样板已写入 ${path.relative(WS, outFile)}`);
  console.log(`  ${t.size.w}x${t.size.h}，角色：${Object.entries(t.roles).map(([s, r]) => `${s}=${r.name}(${r.accepts.length}个候选)`).join(' ')}`);
  console.log('');
  console.log('⚠ 请打开那份 JSON 检查 `roles` 里的 `accepts`：');
  console.log('   扫描只知道你**实际用了什么**；`accepts` 决定"还允许用什么替代"。');
  console.log('   比如床槽写 ["Bed","DoubleBed"] 就表示两种床都能用。');
}

main().catch((e) => {
  console.error('\n[错误] ' + e.message);
  process.exitCode = 1;
});
