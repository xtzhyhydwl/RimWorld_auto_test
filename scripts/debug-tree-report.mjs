#!/usr/bin/env node
/**
 * 分析 debug action 树，提炼结构规律 + 按用途整理出可用的动作清单。
 * 读取 runs/debug-tree-latest.json，输出 PATTERNS.md / CURATED.md / index.json
 */
import fs from 'node:fs';
import path from 'node:path';
import { WS } from '../src/launcher.js';

const src = path.join(WS, 'runs', 'debug-tree-latest.json');
const tree = JSON.parse(fs.readFileSync(src, 'utf8'));
const nodes = tree.nodes;
const byPath = new Map(nodes.map((n) => [n.path, n]));
const childrenOf = new Map();
for (const n of nodes) {
  if (!n.parentPath) continue;
  if (!childrenOf.has(n.parentPath)) childrenOf.set(n.parentPath, []);
  childrenOf.get(n.parentPath).push(n);
}

console.log(`节点 ${nodes.length}，截断=${tree.truncated}`);
console.log(`顶层: ${tree.roots.join(', ')}`);

// ── 1. 深度 1 / 2 的结构 ─────────────────────────────────────
function directChildren(p) {
  return (childrenOf.get(p) ?? []).sort((a, b) => a.label?.localeCompare(b.label ?? '') ?? 0);
}

const d1 = directChildren('Actions');
console.log(`\nActions 直属子节点 ${d1.length} 个`);

// ── 2. 找出「参数化」节点（子节点特别多的） ──────────────────
const param = d1
  .map((n) => ({ n, kids: childrenOf.get(n.path)?.length ?? 0 }))
  .filter((x) => x.kids >= 20)
  .sort((a, b) => b.kids - a.kids);
console.log(`其中子节点 >=20 的参数化动作 ${param.length} 个`);
for (const p of param.slice(0, 25)) {
  const kids = childrenOf.get(p.n.path) ?? [];
  console.log(`  ${String(p.kids).padStart(6)}  ${p.n.label}   [${p.n.execution?.kind ?? '?'}]`);
  console.log(`          例: ${kids.slice(0, 3).map((k) => k.label).join(' / ')}`);
}

// ── 3. 按主题找可用动作 ──────────────────────────────────────
const THEMES = {
  '人物 · 生成': [/^Spawn Pawn/i, /^Spawn Pawn With Lifestage/i, /^Spawn Newborn/i, /^Spawn Child/i, /^Spawn Human/i, /^Water Emerge Pawn/i],
  '人物 · 属性': [/^Set Skill/i, /^Max Skill/i, /^Set Passion/i, /^Max Passion/i, /^Set Backstory/i, /^Add Gene/i, /^Set Xenotype/i, /^Give Trait/i, /^Set Trait/i, /^Change Age/i, /^Set Age/i],
  '人物 · 健康与状态': [/^Add Hediff/i, /^Apply damage/i, /^Mental state/i, /^Mental break/i, /^Give Ability/i, /^Kill/i, /^Down/i, /^Resurrect/i],
  '人物 · 装备': [/^Wear apparel/i, /^Equip primary/i, /^Equip secondary/i, /^Give .*gear/i, /^Make .*equipment/i],
  '事件与袭击': [/^Do incident$/i, /^Do incident w\/ points/i, /^Do incident x/i, /^Execute raid/i, /^Generate quest/i, /^QuestPart test/i],
  '世界与环境': [/^Change weather/i, /^Set biome/i, /^Set terrain/i, /^Set landmark/i, /^Lightning Strike/i, /^Volcanic/i, /^Set Season/i, /^Set Time/i],
  '物品': [/^Spawn thing$/i, /^Spawn full thing stack/i, /^Spawn thing with style/i, /^Spawn unminified thing/i, /^Spawn stack/i, /^Spawn fill area/i],
  '音画与特效': [/^Play sound/i, /^Play song/i, /^Play Animation/i, /^Edit effecter/i, /^Trigger effecter/i, /^Trigger Maintained/i],
  '机械与异常': [/^Spawn Mech/i, /^Mech/i, /^Anomaly/i, /^Entity/i],
};

const curated = {};
for (const [theme, pats] of Object.entries(THEMES)) {
  const hits = d1.filter((n) => pats.some((re) => re.test(n.label ?? '')));
  curated[theme] = hits.map((n) => {
    const kids = childrenOf.get(n.path) ?? [];
    return {
      label: n.label,
      path: n.path,
      kind: n.execution?.kind ?? null,
      supported: n.execution?.supported ?? null,
      requiredTargetKind: n.execution?.requiredTargetKind ?? null,
      childCount: kids.length,
      sampleChildren: kids.slice(0, 5).map((k) => ({ label: k.label, path: k.path, kind: k.execution?.kind ?? null })),
    };
  });
}

// ── 4. Settings 子树（调试显示开关） ─────────────────────────
const settings = directChildren('Settings').map((n) => {
  const kids = childrenOf.get(n.path) ?? [];
  return {
    label: n.label,
    path: n.path,
    toggle: n.hasSettingsToggle,
    on: n.on,
    childCount: kids.length,
    children: kids.slice(0, 40).map((k) => ({ label: k.label, path: k.path, toggle: k.hasSettingsToggle, on: k.on })),
  };
});

// ── 5. 找「生成 pawn」的确切叶子路径样本 ─────────────────────
const spawnPawnParent = d1.find((n) => /^Spawn Pawn$/i.test(n.label ?? ''));
const spawnPawnLeaves = spawnPawnParent ? (childrenOf.get(spawnPawnParent.path) ?? []).slice(0, 15) : [];
const spawnLifestage = d1.find((n) => /^Spawn Pawn With Lifestage/i.test(n.label ?? ''));
const lifestageLeaves = spawnLifestage ? (childrenOf.get(spawnLifestage.path) ?? []).slice(0, 10) : [];

// ── 输出 ─────────────────────────────────────────────────────
const outDir = path.dirname(src);
const P = [];
P.push('# RimWorld 原版 debug action 树 · 结构规律');
P.push('');
P.push(`基于 ${nodes.length} 个节点（本次抓取${tree.truncated ? '**未爬完**，被上限截断' : '完整'}）。`);
P.push('');
P.push('## 树的形状');
P.push('');
P.push('只有 3 个顶层根：');
P.push('');
P.push('| 根 | 含义 |');
P.push('| --- | --- |');
P.push('| `Actions` | 所有**动作**：生成、事件、改属性、播特效…… |');
P.push('| `Outputs` | 输出/导出类 |');
P.push('| `Settings` | **开关型**调试项（各种 draw* 叠加显示等） |');
P.push('');
P.push('## 关键规律');
P.push('');
P.push('### 1. 带 `...` 的是「参数化动作」，会按 Def 展开成一堆叶子');
P.push('');
P.push('这是树会膨胀到几万节点的原因。实测：');
P.push('');
P.push('| 动作 | 展开出的叶子数 |');
P.push('| --- | --- |');
for (const p of param.slice(0, 12)) P.push(`| \`${p.n.label}\` | ${p.kids} |`);
P.push('');
P.push('**这条规律极其有用**：它意味着你可以**直接按 Def 名调用任意一个**——');
P.push('比如 `Actions\\Spawn thing...\\<ThingDef>`、`Actions\\Add Hediff...\\<HediffDef>`、');
P.push('`Actions\\Add Gene...\\<GeneDef>`、`Actions\\Set Backstory...\\<BackstoryDef>`。');
P.push('不需要在游戏里翻几十层菜单。');
P.push('');
P.push('### 2. 用 `execution.requiredTargetKind` 判断要不要传目标');
P.push('');
P.push('| requiredTargetKind | 数量 | 含义 | 调用时怎么传 |');
P.push('| --- | --- | --- | --- |');
const tcount = {};
for (const n of nodes) {
  const k = n.execution?.requiredTargetKind ?? '(不需要)';
  tcount[k] = (tcount[k] ?? 0) + 1;
}
const tdesc = {
  '(不需要)': '直接执行',
  pawn: '需要小人物件',
  map: '需要地图格',
  world: '需要世界地图格',
};
for (const [k, v] of Object.entries(tcount).sort((a, b) => b[1] - a[1])) {
  P.push(`| \`${k}\` | ${v} | ${tdesc[k] ?? ''} | ${k === '(不需要)' ? '直接调' : k === 'pawn' ? '`pawnName`/`pawnId`' : '`x`,`z`'} |`);
}
P.push('');
P.push('### 3. 路径前缀 `T:` = 工具型动作（需要在地图上画）');
P.push('');
P.push('形如 `Actions\\T: Abandon Thing...`、`Actions\\T: Set Stuff...`，都是 ToolMap 类，');
P.push('调用时要带 `x`/`z`（或 `thingId`）。');
P.push('');
P.push('### 4. 标签带 `...` 结尾的，执行后可能弹出二级选择框');
P.push('');
P.push('实测「Drop pod raid at location...」是四步：势力 → 点数 → 点地图 → 半径，');
P.push('用 `clickRowByLabel` 循环处理，不要硬编码层数。');
P.push('');
fs.writeFileSync(path.join(outDir, 'PATTERNS.md'), P.join('\n'), 'utf8');

// CURATED
const C = [];
C.push('# 按用途整理的 debug action 清单');
C.push('');
C.push('路径可直接喂给 `node src/cli.js call rimworld/execute_debug_action --args-file <json>`。');
C.push('');
for (const [theme, items] of Object.entries(curated)) {
  C.push(`## ${theme}`);
  C.push('');
  if (!items.length) {
    C.push('（本次没匹配到）');
    C.push('');
    continue;
  }
  C.push('| 动作 | 路径 | 执行类型 | 需要目标 | 叶子数 |');
  C.push('| --- | --- | --- | --- | --- |');
  for (const it of items) {
    C.push(
      `| ${it.label} | \`${it.path}\` | ${it.kind ?? '-'} | ${it.requiredTargetKind ?? '不需要'} | ${it.childCount} |`,
    );
  }
  C.push('');
  const withKids = items.filter((i) => i.childCount > 0);
  if (withKids.length) {
    C.push('<details><summary>展开看叶子示例</summary>');
    C.push('');
    for (const it of withKids.slice(0, 6)) {
      C.push(`- **${it.label}**`);
      for (const k of it.sampleChildren) C.push(`  - \`${k.path}\``);
    }
    C.push('');
    C.push('</details>');
    C.push('');
  }
}
C.push('## Settings（开关型调试项）');
C.push('');
for (const s of settings) {
  C.push(`### ${s.label}  \`${s.path}\``);
  C.push('');
  if (s.children.length) {
    for (const k of s.children.slice(0, 20)) {
      C.push(`- \`${k.path}\`  (toggle=${k.toggle ?? false})`);
    }
  } else {
    C.push('（无子项）');
  }
  C.push('');
}
fs.writeFileSync(path.join(outDir, 'CURATED.md'), C.join('\n'), 'utf8');

fs.writeFileSync(
  path.join(WS, 'runs', 'debug-tree-index.json'),
  JSON.stringify(
    {
      capturedAt: tree.capturedAt,
      nodeCount: nodes.length,
      truncated: tree.truncated,
      roots: tree.roots,
      actionsTopLevel: d1.map((n) => ({
        label: n.label,
        path: n.path,
        kind: n.execution?.kind ?? null,
        supported: n.execution?.supported ?? null,
        requiredTargetKind: n.execution?.requiredTargetKind ?? null,
        childCount: childrenOf.get(n.path)?.length ?? 0,
      })),
      curated,
      settings,
    },
    null,
    2,
  ),
  'utf8',
);

console.log(`\n=== 主题命中 ===`);
for (const [t, items] of Object.entries(curated)) {
  console.log(`  ${t.padEnd(18)} ${items.length} 个`);
  for (const it of items.slice(0, 6)) {
    console.log(`      ${String(it.childCount).padStart(5)} 叶子  ${it.label}  [${it.kind}/${it.requiredTargetKind ?? '-'}]`);
  }
}
console.log(`\n=== 生成 pawn 的叶子路径样本 ===`);
for (const l of spawnPawnLeaves) console.log(`  ${l.path}`);
console.log(`\n=== Spawn Pawn With Lifestage 叶子样本 ===`);
for (const l of lifestageLeaves) console.log(`  ${l.path}`);

console.log(`\n输出：`);
console.log(`  ${path.join(outDir, 'PATTERNS.md')}`);
console.log(`  ${path.join(outDir, 'CURATED.md')}`);
console.log(`  ${path.join(WS, 'runs', 'debug-tree-index.json')}`);
