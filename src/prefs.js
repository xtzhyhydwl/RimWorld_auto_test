/**
 * 隔离测试实例的画面/音频偏好（Prefs.xml）。
 *
 * 为什么需要这个：
 * RimWorld 启动时会用 Prefs.xml 里的分辨率覆盖 Unity 的 -screen-width/-screen-height 命令行参数。
 * 而我们的实例每次都是被强杀的，Prefs 从来没被保存过 —— 于是每次都是「首次运行」的默认值：
 * 全屏 + 显示器原生分辨率（4K 上就是铺满整块屏）+ 音乐开着。
 *
 * 做法：启动前把这些关键项强制写进 profiles/<name>/Prefs.xml。
 * 只动我们关心的键，其余原样保留，所以你在游戏里调过的其他设置不会被冲掉。
 */
import fs from 'node:fs';
import path from 'node:path';

/**
 * ★ **每次启动都强制**的偏好：只放"窗口 / 安全"类。
 *
 * 为什么只有这些：它们关系到**自动化能不能稳定跑**（不占屏、后台运行、
 * 出错不弹窗卡住），而且玩家一般也不会去调。
 *
 * ⚠ 其余设置**不许**再每次强制 —— 用户 2026-10-04 抱怨：
 *   「每一次进这个游戏，设置都会被重置，包括我调的中文」。
 *   根因就是原来把语言/dev mode/UI 缩放/音量 也放在"每次强制"里。
 */
export const FORCE_ALWAYS_PREFS = {
  fullscreen: false,
  screenWidth: 1600,
  screenHeight: 900,
  // 窗口失焦也继续跑 —— 自动化必需
  runInBackground: true,
  // 出错误不要弹窗/暂停，否则自动化会卡死在那里
  pauseOnError: false,
  openLogOnWarnings: false,
};

/**
 * ★ **只在"这个键还没有值"时播种一次**的偏好。
 *
 * 之后一律**以游戏自己存下来的为准** —— 用户在游戏里把语言调成中文、
 * 打开 dev mode、改 UI 缩放，下次启动都不会再被冲掉。
 *
 * 需要"恢复测试默认"时：把 `Config/Prefs.xml` 里对应的键删掉（下次启动会重新播种），
 * 或者直接在 prefs.json 里显式改（注意：prefs.json 现在也只能**播种**这些键）。
 */
export const SEED_ONLY_PREFS = {
  // 关掉音乐（你本来的偏好也是 0）；音效保留一点点，方便判断游戏是否真在跑
  volumeMusic: 0,
  volumeMaster: 0.6,
  // 小窗口下 1.75 的缩放会撑爆界面
  uiScale: 1,
  // 读档后不自动暂停（我们要自己控制时间）
  pauseOnLoad: false,
  // 语言：自动化测试想要可预测的英文界面 —— 但这只在**第一次**播种，
  // 之后你在游戏里设的中文会被保留（要跑英文测试就自己把 Prefs.xml 里的
  // langFolderName 改回 English，或删掉那个键让本文件重新播种）
  langFolderName: 'English',
  logVerbose: false,
  // dev mode 默认不开，保持接近原版的行为；需要时在游戏里开，开了就会保留
  devMode: false,
};

/**
 * 兼容旧引用：默认偏好 = 强制键 + 播种键。
 * （真正决定"强制还是播种"的是上面两个集合的归属，不是这个合并结果。）
 */
export const DEFAULT_TEST_PREFS = {
  ...FORCE_ALWAYS_PREFS,
  ...SEED_ONLY_PREFS,
};

function xmlEscape(v) {
  return String(v).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

/**
 * 这些键只用于启动参数，**不是** RimWorld 的 Prefs 键，不该写进 Prefs.xml。
 * （RimWorld 会忽略不认识的键，但写进去很脏，还会掩盖"这个键到底有没有生效"。）
 */
const LAUNCH_ONLY_KEYS = new Set(['monitor', 'doorstop', 'visible']);

function toXmlValue(v) {
  if (typeof v === 'boolean') return v ? 'True' : 'False';
  return xmlEscape(v);
}

/** 替换或插入一个顶层标量键，其余内容原样保留 */
function setScalar(xml, key, value) {
  const line = `  <${key}>${toXmlValue(value)}</${key}>`;
  // 匹配顶层（两个空格缩进）的 <key>...</key> 或 <key />
  const re = new RegExp(`^[ \\t]*<${key}(?:\\s*/>|>[\\s\\S]*?</${key}>)[ \\t]*$`, 'm');
  if (re.test(xml)) return xml.replace(re, line);
  return xml.replace(/[ \t]*<\/PrefsData>\s*$/, `${line}\n</PrefsData>\n`);
}

/**
 * Prefs.xml 在 <存档目录>/Config/ 下，不是存档目录根下。
 * （对应主力配置就是 ...\RimWorld by Ludeon Studios\Config\Prefs.xml）
 */
export function prefsPath(profileDir) {
  return path.join(profileDir, 'Config', 'Prefs.xml');
}

export function readPrefs(profileDir) {
  try {
    return fs.readFileSync(prefsPath(profileDir), 'utf8');
  } catch {
    return null;
  }
}

/** 这个键在 XML 里已经有值了吗 */
function hasScalar(xml, key) {
  const re = new RegExp(`^[ \\t]*<${key}(?:\\s*/>|>[\\s\\S]*?</${key}>)[ \\t]*$`, 'm');
  return re.test(xml);
}

/**
 * 把偏好写进（或建到）隔离 profile。
 *
 * ★ 两种待遇（用户 2026-10-04 明确要求）：
 *   - `FORCE_ALWAYS_PREFS` 里的键：**每次启动都强制**（窗口/安全）
 *   - 其余键：**只在还没有值时播种一次** —— 之后用户在游戏里调什么就是什么
 * @param {string} profileDir
 * @param {object} overrides 要设置的键
 * @returns {{path:string, created:boolean, applied:object, seeded:object, skipped:string[]}}
 */
export function ensurePrefs(profileDir, overrides = {}) {
  fs.mkdirSync(path.join(profileDir, 'Config'), { recursive: true });
  let xml = readPrefs(profileDir);
  const created = xml === null;
  if (created) {
    xml = '<?xml version="1.0" encoding="utf-8"?>\n<PrefsData>\n</PrefsData>\n';
  }
  const seeded = {};
  const skipped = [];
  for (const [k, v] of Object.entries(overrides)) {
    if (v === undefined || v === null) continue;
    if (k.startsWith('_')) continue; // 下划线开头是给人看的注释，不是游戏偏好
    if (LAUNCH_ONLY_KEYS.has(k)) continue; // 只用于启动参数，不写进 Prefs.xml

    const forced = Object.prototype.hasOwnProperty.call(FORCE_ALWAYS_PREFS, k);
    // ★ 非强制键：游戏里已经写过值了就别动它（这就是"设置不再被重置"的关键一行）
    if (!forced && hasScalar(xml, k)) {
      skipped.push(k);
      continue;
    }
    xml = setScalar(xml, k, v);
    if (!forced) seeded[k] = v;
  }
  fs.writeFileSync(prefsPath(profileDir), xml, 'utf8');
  return { path: prefsPath(profileDir), created, applied: overrides, seeded, skipped };
}

/**
 * 解析出这次启动真正要用的偏好：
 *   默认测试偏好 ← profiles/<name>/prefs.json ← 显式传入的参数
 * prefs.json 里可以覆盖任意键，例如 {"screenWidth": 1280, "devMode": true}。
 */
export function resolvePrefs(profileDir, explicit = {}) {
  let fromFile = {};
  const file = path.join(profileDir, 'prefs.json');
  if (fs.existsSync(file)) {
    try {
      fromFile = JSON.parse(fs.readFileSync(file, 'utf8'));
    } catch (err) {
      throw new Error(`prefs.json 不是合法 JSON：${file}\n${err.message}`);
    }
  }
  const merged = { ...DEFAULT_TEST_PREFS, ...fromFile, ...explicit };
  for (const k of Object.keys(merged)) {
    // 下划线开头当注释；undefined/null 表示"这次不要强制这个键"
    if (k.startsWith('_') || merged[k] === undefined || merged[k] === null) delete merged[k];
  }
  return merged;
}
