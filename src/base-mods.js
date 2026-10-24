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

/**
 * 把缺的 `<li>` 按行插到 `</activeMods>` 之前（每行缩进 4 空格）。
 *
 * 为什么用文本插入而不是 XML 序列化器：序列化器会把整个文件重排（属性引号、
 * 自闭合标签、knownExpansions 的位置都会变），真 profile 的 diff 会炸成整文件，
 * 而这种改动只该多出几行。
 */
function insertBaseMods(xml, missing) {
  const at = xml.indexOf('</activeMods>');
  const head = xml.slice(0, at);
  const tail = xml.slice(at);
  const lines = missing.map((packageId) => `    <li>${packageId}</li>`).join('\n');
  const lineStart = head.lastIndexOf('\n') + 1;
  const closingIndent = head.slice(lineStart);
  if (/^[ \t]*$/.test(closingIndent)) {
    // 常见形态：`</activeMods>` 独占一行、前面是它的缩进 —— 插在这行之前，闭标签原样保留
    return head.slice(0, lineStart) + lines + '\n' + closingIndent + tail;
  }
  // 单行写的 ModsConfig：没有可用的缩进，退化成「新起一行、闭标签按两空格收尾」
  return head + '\n' + lines + '\n  ' + tail;
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
 * 补齐底座 mod（幂等，只加不删）。返回 `{ added, already, modsConfigPath }`；
 * ModsConfig.xml 不存在或没有 `<activeMods>` 时不写文件，返回 `{ added: [], already: [], skipped }`。
 */
export function ensureBaseMods({ profileDir, baseMods }) {
  const plan = planBaseMods({ profileDir, baseMods });
  if (plan.skipped) return { added: [], already: [], skipped: plan.skipped };
  if (!plan.missing.length) {
    return { added: [], already: plan.already, modsConfigPath: plan.file };
  }
  // 'utf8' 写出去就是无 BOM；游戏与 Read 工具都按 UTF-8 读这份 XML
  fs.writeFileSync(plan.file, insertBaseMods(plan.xml, plan.missing), 'utf8');
  return { added: plan.missing, already: plan.already, modsConfigPath: plan.file };
}
