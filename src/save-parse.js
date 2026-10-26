/**
 * `parse-save <存档.rws> [--def <hediffDefName>] [--json]`：**从存档里读 buff 取证**。
 *
 * 为什么要有这条命令：buff 类验证（severity / 两日倒计时 / healthState / 营养不良）唯一可信的来源是
 * 存档原文 —— 桥接读不到 `ticksToDisappear`，也不给"这一条 hediff 属于哪只 pawn"。以前每次都在 `runs\`
 * 里现抄一个 parser（`parse-v4.cjs` / `parse-v5.cjs` / `parse-buff.cjs` 三代），而且 defName 一改名
 * 旧脚本就静默返回 `carriers=0`（假阴性，不是 buff 没了）。所以把它固化成一条子命令，defName 由参数给。
 *
 * 三个**存档格式**的坑（都是从实测里踩出来的，改这里之前先读）：
 *   1. hediff 的 `<li>` 里，`<severity>`/`<tickAdded>` 写在 `<def>` **之前**，`<ticksToDisappear>`
 *      写在**之后** ⇒ 取值窗口必须是整段 `<li>…</li>`，不能只截 `<def>` 之后。
 *   2. `<healthState>` 只在 pawn **不是 Alive** 时才写，位置是 `<healthTracker>` 的第一个子元素
 *      ⇒ 判定必须锚在"这条 hediff 所属 pawn 的 healthTracker"上；拿 pawn 块去找会读到邻居的
 *      healthState（曾把健康小人误报成 Down）。
 *   3. 营养不良要在这个 pawn 的 healthTracker 作用域里找 `Malnutrition`，再回退到它自己的 `<li>`。
 *   4. `kindDef` 只能给"最近的块里出现的那一个"，**会落到邻居身上**（实测把 Ratkin 报成 Crow/Boomrat）
 *      ⇒ 输出里标成 `kindGuess`，别拿它当身份判据；身份用 `loadID` + 存档里的 `<faction>` 自己核。
 */
import fs from 'node:fs';

export const PARSE_SAVE_USAGE = [
  '用法: node src/cli.js parse-save <存档.rws> [--def <hediffDefName>] [--json]',
  '',
  '  从 .rws 存档原文里读一条 hediff 的取证信息：每个载体的 severity / ticksToDisappear / tickAdded /',
  '  loadID、所属 pawn 的 kindDef / healthState / 营养不良，外加三档心情的计数与全档营养不良分布。',
  '',
  '  --def   要读的 hediff defName，默认 RHAH_SiegeFury（改名前的存档用 RHAH_ArmedDemandEndurance）',
  '  --json  给机器读的完整对象（默认是人读的逐行输出）',
  '',
  '  退出码：0 读成功（carriers 可以是 0，那是"没有载体"不是失败）/ 1 文件读不到或 --def 缺失',
  '  存档从哪来：node src/cli.js call rimworld/save_game → 载荷里的 "path"',
].join('\n');

/** 出现次数（单参数闭包，误写成两参数会静默返回 0 —— 上一代脚本踩过） */
function makeCounter(text) {
  return (needle) => {
    let n = 0;
    let p = -1;
    while ((p = text.indexOf(needle, p + 1)) >= 0) n += 1;
    return n;
  };
}

/** 取 `<tag>值</tag>` 的第一个值；没有就是 undefined（别拿空串冒充"读到了") */
function pick(xml, tag) {
  const m = xml.match(new RegExp(`<${tag}>([^<]*)</${tag}>`));
  return m ? m[1] : undefined;
}

/**
 * 解析一份存档。
 * @returns {{file:string, def:string, carriers:Array<object>, thoughts:object, global:object}}
 */
export function parseSave({ file, def }) {
  const text = fs.readFileSync(file, 'utf8');
  const countOf = makeCounter(text);

  // 行首 `<li` 的偏移：只用来给 pawn 块定界
  const lineStarts = [];
  {
    const re = /\r\n(\t*)<li/g;
    let m;
    while ((m = re.exec(text))) lineStarts.push(m.index + 2);
  }

  /** 找包含这个位置、且带 healthTracker + hediffs 的最近一个 `<li>` 块（窗口规则 2） */
  function pawnBlockFor(pos) {
    for (let k = lineStarts.length - 1; k >= 0; k -= 1) {
      const start = lineStarts[k];
      if (start > pos) continue;
      const head = text.slice(start, pos + 1);
      if (countOfIn(head, '<healthTracker>') !== 1) continue;
      if (!head.includes('<hediffs>')) continue;
      const nextTracker = text.indexOf('<healthTracker>', pos);
      const end = nextTracker > 0 ? nextTracker : Math.min(text.length, pos + 30_000);
      return text.slice(start, end);
    }
    return null;
  }

  const positions = [];
  let p = -1;
  while ((p = text.indexOf(`<def>${def}</def>`, p + 1)) >= 0) positions.push(p);

  const carriers = positions.map((at, i) => {
    const liStart = text.lastIndexOf('<li', at);
    const liEnd = text.indexOf('</li>', at);
    const hediff = text.slice(liStart, liEnd);
    const block = pawnBlockFor(at) ?? '';

    // 自己的 healthTracker（不是块）：healthState 与营养不良都锚在这
    const trackerAt = text.lastIndexOf('<healthTracker>', at);
    let healthState = 'Alive';
    let malnutrition = 'none';
    if (trackerAt > 0) {
      const head = text.slice(trackerAt + '<healthTracker>'.length, trackerAt + 200).replace(/^\s+/, '');
      if (head.startsWith('<healthState>')) healthState = pick(head.slice(0, 120), 'healthState') ?? 'Alive';
      const nextTracker = text.indexOf('<healthTracker>', trackerAt + 1);
      const scopeEnd = nextTracker > 0 ? nextTracker : Math.min(text.length, trackerAt + 400_000);
      const scope = text.slice(trackerAt, scopeEnd);
      const malAt = scope.indexOf('<def>Malnutrition</def>');
      if (malAt >= 0) {
        const value = pick(scope.slice(scope.lastIndexOf('<li', malAt), malAt), 'severity');
        malnutrition = value === undefined ? 'unreadable' : value;
      }
    }

    return {
      index: i + 1,
      loadID: pick(hediff, 'loadID'),
      kindDef: pick(block, 'kindDef') ?? '?',
      severity: pick(hediff, 'severity'),
      ticksToDisappear: pick(hediff, 'ticksToDisappear'),
      tickAdded: pick(hediff, 'tickAdded'),
      healthState,
      malnutrition,
    };
  });

  // 三档心情：名字写死是因为它们就是这条 buff 的三个 Def（改 Def 名要一起改这里）
  const thoughts = {
    FoodInSight_20: countOf('RHAH_Thought_SiegeFuryFoodInSight'),
    MealSoon_30: countOf('RHAH_Thought_SiegeFuryMealSoon'),
    Starving_40: countOf('RHAH_Thought_SiegeFuryStarving'),
  };

  // 全档营养不良分布：buff 到期后仍然可读，所以它是"buff 任务结束"之后唯一还能用的数
  const malnutritionSeverities = [];
  let q = -1;
  while ((q = text.indexOf('<def>Malnutrition</def>', q + 1)) >= 0) {
    const value = pick(text.slice(text.lastIndexOf('<li', q), q), 'severity');
    if (value !== undefined) malnutritionSeverities.push(Number.parseFloat(value));
  }
  malnutritionSeverities.sort((a, b) => a - b);

  return {
    file,
    def,
    carriers,
    thoughts,
    global: {
      downed: countOf('<healthState>Down</healthState>'),
      dead: countOf('<healthState>Dead</healthState>'),
      malnutritionHediffs: malnutritionSeverities.length,
      malnutritionMin: malnutritionSeverities.length ? malnutritionSeverities[0] : null,
      malnutritionMax: malnutritionSeverities.length ? malnutritionSeverities.at(-1) : null,
      malnutritionSeverities,
    },
  };
}

/** 只看一段文本里的出现次数（pawnBlockFor 定界用，不能闭包到整份存档） */
function countOfIn(slice, needle) {
  let n = 0;
  let p = -1;
  while ((p = slice.indexOf(needle, p + 1)) >= 0) n += 1;
  return n;
}

/** 人读格式：一行一个载体 + 三行汇总；值读不到就打 `-`，不编 */
export function formatParseSave(report) {
  const lines = [`save=${report.file}`, `def=${report.def}`, `carriers=${report.carriers.length}`];
  for (const c of report.carriers) {
    lines.push(
      `#${c.index} loadID=${c.loadID ?? '-'} kindGuess~${c.kindDef} sev=${c.severity ?? '-'} ` +
        `ttd=${c.ticksToDisappear ?? '-'} added@${c.tickAdded ?? '-'} health=${c.healthState} malnutrition=${c.malnutrition}`,
    );
  }
  lines.push(`thoughts=${JSON.stringify(report.thoughts)}`);
  const g = report.global;
  const round = (v) => (v === null ? 'n/a' : v.toFixed(4));
  lines.push(
    `global: Down=${g.downed} Dead=${g.dead} MalnutritionHediffs=${g.malnutritionHediffs} ` +
      `MalMin=${round(g.malnutritionMin)} MalMax=${round(g.malnutritionMax)}`,
  );
  return lines.join('\n');
}
