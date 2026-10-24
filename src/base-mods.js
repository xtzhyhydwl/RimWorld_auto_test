/**
 * 测试 profile 的底座 mod —— 唯一真源是仓库根的 `base-mods.json`，start 前自动补齐。
 *
 * 为什么要抽成一份数据文件：底座 mod 一直靠手改 `ModsConfig.xml`，漏一个的表现是
 * 「工具凭空消失」（漏 Character Editor ⇒ `ce/*` 全没了；漏 autotest ⇒ 没有 Def 热重载），
 * 而日志里只会表现为「工具不存在」，很难反推是清单漏了。清单只有一处，用户改这一处即可。
 *
 * 只加不删：profile 自己的被测 mod 由 profile 主人排好，这里绝不替他们做减法。
 */
import fs from 'node:fs';
import path from 'node:path';

/** 底座清单文件的路径（CLI 与启动器共用这一处拼接，避免各写一份路径） */
export function baseModsFile(ws) {
  return path.join(ws, 'base-mods.json');
}

function stripBom(text) {
  // Windows 上手工保存的 JSON 常带 BOM，JSON.parse 会因为 BOM 直接抛错
  return text.charCodeAt(0) === 0xfeff ? text.slice(1) : text;
}

/**
 * 读整份底座清单（含 `why` 与 `description`，CLI 要展示）。
 *
 * 文件不存在不算错误（还没建 profile 的新机器上不该连 start 都起不来）；
 * 但**存在却读不动/写坏了 JSON** 是配置错误，照常抛 —— 由调用方决定是警告还是报错。
 */
export function readBaseModsFile(ws) {
  const file = baseModsFile(ws);
  let text;
  try {
    text = fs.readFileSync(file, 'utf8');
  } catch (err) {
    if (err.code === 'ENOENT' || err.code === 'ENOTDIR') {
      return { file, missingFile: true, description: '', mods: [], packageIds: [] };
    }
    throw err;
  }
  const data = JSON.parse(stripBom(text));
  const mods = [];
  const seen = new Set();
  for (const entry of Array.isArray(data?.mods) ? data.mods : []) {
    const packageId = String(entry?.packageId ?? '').trim();
    if (!packageId) continue;
    // 同一 packageId 写两遍是数据笔误；按第一处的位置只留一个，否则会插两行同样的 <li>
    if (seen.has(packageId.toLowerCase())) continue;
    seen.add(packageId.toLowerCase());
    mods.push({ packageId, why: String(entry?.why ?? '') });
  }
  return {
    file,
    missingFile: false,
    description: String(data?.description ?? ''),
    mods,
    packageIds: mods.map((m) => m.packageId),
  };
}

/**
 * 给启动器用的最小形态（只有 id 列表 + 缺文件标志）。
 * 单独一个入口是为了让 start 路径不依赖 `why` 之类的展示字段。
 */
export function loadBaseMods(ws) {
  const { packageIds, missingFile } = readBaseModsFile(ws);
  return { packageIds, missingFile };
}

function modsConfigPath(profileDir) {
  return path.join(profileDir, 'Config', 'ModsConfig.xml');
}

/** 取出 `<activeMods>` 里的 packageId；没有这一段返回 null（调用方据此跳过，而不是猜一个位置插） */
function readActiveMods(xml) {
  const section = /<activeMods>([\s\S]*?)<\/activeMods>/.exec(xml);
  if (!section) return null;
  return [...section[1].matchAll(/<li>([^<]*)<\/li>/g)].map((m) => m[1].trim()).filter(Boolean);
}

/**
 * 只算「差哪些」，不落盘 —— 启动器拿它决定要不要写，CLI 拿它做只读自检。
 * 顺序即插入顺序：底座清单里 Harmony 排第一，补进来的 `<li>` 也跟在它之后、按数组顺序排列。
 */
function planBaseMods({ profileDir, baseMods }) {
  const file = modsConfigPath(profileDir);
  let xml;
  try {
    xml = fs.readFileSync(file, 'utf8');
  } catch (err) {
    if (err.code === 'ENOENT' || err.code === 'ENOTDIR') {
      return { skipped: `没有 ${file}` };
    }
    throw err;
  }
  const active = readActiveMods(xml);
  if (active === null) {
    return { skipped: `${file} 里没有 <activeMods> 段` };
  }
  const have = new Set(active.map((id) => id.toLowerCase()));
  const already = [];
  const missing = [];
  const counted = new Set();
  for (const packageId of baseMods) {
    const key = String(packageId).toLowerCase();
    if (counted.has(key)) continue;
    counted.add(key);
    (have.has(key) ? already : missing).push(packageId);
  }
  return { file, xml, active, already, missing };
}

/** `<activeMods>` 段的默认缩进（真 profile 由游戏自己写成 4 空格） */
const DEFAULT_MOD_INDENT = '    ';

/** 清单去重：trim、丢掉空项、按 packageId **大小写不敏感**只留第一次出现的位置 */
function normalizePackageIds(list) {
  const ids = [];
  const seen = new Set();
  for (const raw of Array.isArray(list) ? list : []) {
    const id = String(raw ?? '').trim();
    if (!id || seen.has(id.toLowerCase())) continue;
    seen.add(id.toLowerCase());
    ids.push(id);
  }
  return ids;
}

/**
 * 把 `<activeMods>` 重排成「前置清单（front）→ 第二段清单（then）→ 其余保持原相对顺序」，顺带补进缺的 `<li>`。
 *
 * 两个调用方共用这一处实现（**「谁排最前」全项目只有这一份代码**）：
 *   - `ensureBaseMods`（start 前补齐底座）：`{ front: base-mods.json, then: [] }`；
 *   - `mods apply` / `mods add|remove --apply`（被测 mod）：`{ front: 底座, then: tested-mods.json }`。
 * 改一处就同时改两条路，不会一条改到、另一条漏掉。
 *
 * 目标顺序 = `[front 里的项，按该数组顺序]` + `[then 里的项，按该数组顺序]` +
 * `[原有条目里两段都不属于的，保持原顺序]`。
 * 缺的项直接落在它该在的位置上（不一律追加到段末）；`front` 里已有的 id 再出现在 `then` 里时忽略
 * —— 同一条 mod 只该被排一次，以底座为准。
 *
 * 为什么必须重排、而不只是追加（**有意的行为变更**）：RimWorld 按 `<activeMods>` 的顺序加载，
 * 「Core → 官方 DLC → 其它 mod」是唯一能工作的顺序。旧实现把缺的 `<li>` 一律插到 `</activeMods>`
 * 之前，于是补进来的 5 个 DLC 会排在所有非官方 mod **后面** —— 按 DLC 内容写的 mod 会在 DLC 之前
 * 加载（拿不到那些 Def，表现为静默失效或红字），而清单本身看不出错误。
 * 代价：已有 profile 的 activeMods 顺序会被改动一次（每条 packageId 不变，只是位置变）。
 *
 * 幂等：算出来的顺序与文件里已有的完全一致时，**原样返回传进来的字符串**（调用方据此不写文件）。
 * 没有 `<activeMods>` 段、或 front 与 then 都是空清单时也原样返回 —— 不猜位置。
 * 用文本重排而不是 XML 序列化器：序列化器会把整个文件重排（属性引号、自闭合标签、
 * knownExpansions 的位置都会变），真 profile 的 diff 会炸成整文件。
 */
export function rewriteActiveMods(xml, { front = [], then = [] } = {}) {
  const section = /<activeMods>([\s\S]*?)<\/activeMods>/.exec(xml);
  if (!section) return xml;
  const inner = section[1];
  const eol = xml.includes('\r\n') ? '\r\n' : '\n';

  // 每个已存在条目连它这一行的缩进一起记住：重排时原样搬，不重排格式（diff 只该是行位置变化）
  const entries = [];
  for (const m of inner.matchAll(/<li>([^<]*)<\/li>/g)) {
    const id = m[1].trim();
    if (!id) continue;
    const before = inner.slice(inner.lastIndexOf('\n', m.index) + 1, m.index);
    entries.push({ id, text: m[0], indent: /^[ \t]*$/.test(before) ? before : null });
  }

  const frontIds = normalizePackageIds(front);
  const frontKeys = new Set(frontIds.map((id) => id.toLowerCase()));
  // then 里与 front 重复的项丢掉：同一条 mod 排两次会多出一行（底座以 base-mods.json 为准）
  const thenIds = normalizePackageIds(then).filter((id) => !frontKeys.has(id.toLowerCase()));
  if (!frontIds.length && !thenIds.length) return xml; // 没有清单就没有「该在前面的东西」

  const ordered = [];
  const consumed = new Set();
  for (const id of [...frontIds, ...thenIds]) {
    const key = id.toLowerCase();
    const at = entries.findIndex((e, i) => !consumed.has(i) && e.id.toLowerCase() === key);
    if (at >= 0) {
      consumed.add(at);
      ordered.push(entries[at]);
    } else {
      ordered.push({ id, text: `<li>${id}</li>`, indent: null });
    }
  }
  // 两段清单之外的条目原样留在后面；清单项的重复行也一并留在这里（只加不删，不替 profile 主人做减法）
  entries.forEach((e, i) => {
    if (!consumed.has(i)) ordered.push(e);
  });

  if (ordered.length === entries.length && ordered.every((e, i) => e.text === entries[i].text)) {
    return xml;
  }

  const indent = entries.find((e) => e.indent !== null)?.indent ?? DEFAULT_MOD_INDENT;
  const closingIndent = inner.slice(inner.lastIndexOf('\n') + 1);
  // 段里若有 `<li>` 以外的东西（注释等），原样留在段末，不因为重排把它吃掉
  const leftover = inner.replace(/<li>[^<]*<\/li>/g, '').trim();
  const body =
    eol +
    ordered.map((e) => (e.indent === null ? indent : e.indent) + e.text).join(eol) +
    (leftover ? eol + leftover : '') +
    eol +
    (/^[ \t]*$/.test(closingIndent) ? closingIndent : DEFAULT_MOD_INDENT.slice(0, 2));

  return `${xml.slice(0, section.index)}<activeMods>${body}</activeMods>${xml.slice(section.index + section[0].length)}`;
}

/**
 * 只读检查：该 profile 还缺哪几个底座 mod（不写文件）。
 * 返回 `{ modsConfigPath, already, missing }`；文件/段缺失时返回 `{ skipped: '原因' }`。
 */
export function diffBaseMods({ profileDir, baseMods }) {
  const plan = planBaseMods({ profileDir, baseMods });
  if (plan.skipped) return { skipped: plan.skipped };
  return { modsConfigPath: plan.file, already: plan.already, missing: plan.missing };
}

/**
 * 补齐底座 mod（幂等，只加不删；顺带把底座项排到最前面，理由见 `rewriteActiveMods`）。
 * 返回 `{ added, already, reordered, modsConfigPath }`；
 * ModsConfig.xml 不存在或没有 `<activeMods>` 时不写文件，返回 `{ added: [], already: [], skipped }`。
 *
 * **变才写**：`rewriteActiveMods` 在顺序已经规范时原样返回，所以这里可以直接比字符串 ——
 * 没变化就不动文件（每次 start 都摸一遍文件会把 mtime、git 状态、用户的备份工具全都弄脏）。
 */
export function ensureBaseMods({ profileDir, baseMods }) {
  const plan = planBaseMods({ profileDir, baseMods });
  if (plan.skipped) return { added: [], already: [], skipped: plan.skipped };
  // start 路径不认识被测 mod：then 空 ⇒ 行为与泛化前的 insertBaseMods 完全一致（只认底座）
  const nextXml = rewriteActiveMods(plan.xml, { front: baseMods, then: [] });
  const changed = nextXml !== plan.xml;
  // 'utf8' 写出去就是无 BOM；游戏与 Read 工具都按 UTF-8 读这份 XML
  if (changed) fs.writeFileSync(plan.file, nextXml, 'utf8');
  return {
    added: plan.missing,
    already: plan.already,
    // 只重排、没新增：启动器现在只打印 `added`，这一项留给后续的调用方/report 用
    reordered: changed && plan.missing.length === 0,
    modsConfigPath: plan.file,
  };
}
