#!/usr/bin/env node
/**
 * 编译 FoodControl 的 **Game 层**（`src/foodcontrol-game/`）—— 默认产物写 **工作区**
 * `tmp/foodcontrol-game/`，不碰游戏可见的 Mods 目录。
 *
 * ## 为什么这样设计
 *
 * 本机没有 .NET SDK，但有 VS BuildTools 自带的 Roslyn csc.exe。RimWorld 跑在 Unity 的 Mono 上，
 * 所以**引用游戏安装目录里的 `Managed\` 程序集**（而不是 net472 参考程序集）ABI 最稳 ——
 * 这套命令和 `scripts/build-cebridge.mjs`、以及 `docs/游戏API符号确证.md` §E 的编译探针完全一致。
 *
 * 两个关键开关：
 *   - `/nostdlib+` + 显式引用游戏的 `mscorlib.dll`：保证用游戏那一套基础库
 *   - `/langversion:7.3`：游戏运行时是旧 Mono，C# 8+ 语法（switch 表达式、`??=`、范围索引）不许用
 *
 * ## 为什么编成两个 DLL
 *
 *   `FoodControl.Core`（`src/foodcontrol-core/`）是**纯逻辑层**：不引用游戏程序集，
 *   所以同一份源码还能被 `scripts/build-core.mjs` 用 .NET Framework 的 csc 另编一份，在
 *   本机毫秒级跑离线测试。Game 层反过来必须引用 `Assembly-CSharp.dll`。
 *   编成两个 DLL 让这条边界在**产物**上看得见（而不是靠人自觉）：
 *       FoodControl.Core.dll  ← 不含任何 Verse/RimWorld 引用
 *       FoodControl.Game.dll  ← 引用 Core + Assembly-CSharp
 *   RimWorld 会把 `1.6/Assemblies/` 下的 DLL 全部加载，两个文件没问题。
 *
 * ## 为什么不默认写进 mods 目录
 *
 *   `<游戏目录>\Mods\FoodControl` 是 junction → 工作区 `mods/FoodControl`，写进去游戏立刻看得见，
 *   但**正在运行的游戏会把已加载的 DLL 锁住**，之后每次编译都失败（"无法打开 xxx.dll 进行写入"）。
 *   默认写 `tmp/` 就不会互相干扰；要部署时显式 `--deploy`（带锁检测与明确提示）。
 *
 * ⚠ 沙箱下**不能**用 `execFileSync`（管道 stdio 会 EPERM），所以走仓库自己的 `src/proc.js` 的 `capture()`。
 *
 * 用法：
 *   node scripts/build-mod.mjs              # 编译 Core + Game → tmp/foodcontrol-game/
 *   node scripts/build-mod.mjs --check      # 只做静态检查（Core 是否混进游戏类型）
 *   node scripts/build-mod.mjs --core-only  # 只编 Core（快速定位是不是 Game 层的错）
 *   node scripts/build-mod.mjs --deploy     # 编完再拷进 mods/FoodControl/1.6/Assemblies/
 *                                           # ⚠ 需要游戏已停，否则 DLL 被锁
 */
import fs from 'node:fs';
import path from 'node:path';
import { gameRoot, WS } from '../src/launcher.js';
import { capture } from '../src/proc.js';

const CORE_DIR = path.join(WS, 'src', 'foodcontrol-core');
const GAME_DIR = path.join(WS, 'src', 'foodcontrol-game');
const OUT_DIR = path.join(WS, 'tmp', 'foodcontrol-game');
const DEPLOY_DIR = path.join(WS, 'mods', 'FoodControl', '1.6', 'Assemblies');

/** mod 的内容根目录（junction 目标），样板要同步到这里 */
const MOD_DIR = path.join(WS, 'mods', 'FoodControl');

const CORE_DLL = path.join(OUT_DIR, 'FoodControl.Core.dll');
/**
 * Game 层的产物**故意叫 `FoodControl.dll`**（程序集名 = 文件名）。
 *
 * 原因：测试探针 `bridge/DSH.FoodProbe/DSH.FoodProbe.cs` 的 `FindMod()` 是按
 * `a.GetName().Name == "FoodControl"` 精确匹配加载的程序集的 —— 叫 `FoodControl.Game.dll`
 * 会让探针报「程序集 FoodControl 未加载」。（备选是改探针里的 MOD_ASM 常量，但那是测试侧的文件。）
 */
const GAME_DLL = path.join(OUT_DIR, 'FoodControl.dll');

const CSC_CANDIDATES = [
  'C:\\Program Files (x86)\\Microsoft Visual Studio\\2022\\BuildTools\\MSBuild\\Current\\Bin\\Roslyn\\csc.exe',
  'C:\\Program Files\\Microsoft Visual Studio\\2022\\BuildTools\\MSBuild\\Current\\Bin\\Roslyn\\csc.exe',
  'C:\\Program Files (x86)\\Microsoft Visual Studio\\2019\\BuildTools\\MSBuild\\Current\\Bin\\Roslyn\\csc.exe',
];

function findCsc() {
  for (const p of CSC_CANDIDATES) if (fs.existsSync(p)) return p;
  throw new Error('找不到 VS BuildTools 的 Roslyn csc.exe（见 docs/mod开发基础.md 第一节）');
}

function managedDir() {
  const managed = path.join(gameRoot(), 'RimWorldWin64_Data', 'Managed');
  if (!fs.existsSync(managed)) throw new Error('找不到游戏 Managed 目录：' + managed);
  return managed;
}

/** 游戏程序集引用表（与确证文档 §E 的探针命令一致） */
function gameRefs(managed) {
  const names = [
    'mscorlib.dll',
    'System.dll',
    'System.Core.dll',
    'netstandard.dll',
    'UnityEngine.CoreModule.dll',
    'Assembly-CSharp.dll',
  ];
  const refs = names.map((n) => path.join(managed, n));
  const missing = refs.filter((r) => !fs.existsSync(r));
  if (missing.length) throw new Error('缺少游戏引用程序集：\n  ' + missing.join('\n  '));
  return refs;
}

/**
 * 静态检查（**只对 Core**）：Core 不许引用游戏类型。
 *
 * 与 `scripts/build-core.mjs` 同源。这里再做一遍是因为**本脚本是用游戏程序集编 Core 的**
 * （为了让 Game 层能引用它），此时编译器不会拦"Core 误用游戏类型"，必须靠这条兜住。
 * 注释里提到游戏类型是允许的（我们在注释里大量解释游戏机制）。
 */
function assertCoreStaysPure(sources) {
  const banned = /\b(Verse|RimWorld|UnityEngine|HarmonyLib)\s*\./;
  const offenders = [];
  for (const s of sources) {
    const lines = fs.readFileSync(s, 'utf8').split(/\r?\n/);
    for (let i = 0; i < lines.length; i++) {
      const code = lines[i].replace(/\/\/.*$/, '').replace(/\/\*.*?\*\//g, '');
      if (banned.test(code)) offenders.push(`${path.relative(WS, s)}:${i + 1}  ${lines[i].trim()}`);
    }
  }
  return offenders;
}

function listCs(dir, { skipTests = false } = {}) {
  if (!fs.existsSync(dir)) return [];
  const out = [];
  const walk = (d) => {
    for (const e of fs.readdirSync(d, { withFileTypes: true })) {
      const full = path.join(d, e.name);
      if (e.isDirectory()) {
        if (skipTests && e.name === 'tests') continue;
        walk(full);
      } else if (e.name.endsWith('.cs')) {
        out.push(full);
      }
    }
  };
  walk(dir);
  return out.sort();
}

/** 能不能以读写方式打开（用来检测 DLL 是否被运行中的游戏锁住） */
function isWritable(p) {
  if (!fs.existsSync(p)) return true;
  try {
    const fd = fs.openSync(p, 'r+');
    fs.closeSync(fd);
    return true;
  } catch {
    return false;
  }
}

async function compile({ label, csc, out, refs, sources }) {
  if (!sources.length) throw new Error(`${label}: 没有源文件`);
  fs.mkdirSync(path.dirname(out), { recursive: true });

  const args = [
    '/nologo',
    '/target:library',
    '/nostdlib+',
    '/langversion:7.3',
    '/optimize+',
    '/deterministic+',
    `/out:${out}`,
    ...refs.map((r) => `/reference:${r}`),
    ...sources,
  ];

  console.log(`\n── ${label} ─────────────────────────────`);
  console.log(`源文件 ${sources.length} 个：`);
  for (const s of sources) console.log('   ' + path.relative(WS, s));
  console.log(`输出：${path.relative(WS, out)}`);

  const res = await capture(csc, args, { timeoutMs: 300_000 });
  const stdout = (res.stdout || '').trim();
  const stderr = (res.stderr || '').trim();
  if (stdout) console.log(stdout);
  if (stderr) console.error(stderr);

  if (res.code !== 0 || !fs.existsSync(out)) {
    throw new Error(`${label} 编译失败（退出码 ${res.code}）`);
  }
  const kb = (fs.statSync(out).size / 1024).toFixed(1);
  console.log(`✓ ${label} 编译成功  ${path.relative(WS, out)}  (${kb} KB)`);
  return out;
}

async function main() {
  const argv = process.argv.slice(2);
  const checkOnly = argv.includes('--check');
  const onlyCore = argv.includes('--core-only');
  const deploy = argv.includes('--deploy');

  const csc = findCsc();
  const managed = managedDir();
  const refs = gameRefs(managed);

  console.log(`编译器：${csc}`);
  console.log(`游戏程序集：${managed}`);

  const coreSources = listCs(CORE_DIR, { skipTests: true });
  const gameSources = listCs(GAME_DIR);

  const offenders = assertCoreStaysPure(coreSources);
  if (offenders.length) {
    console.error('\n✗ Core 里出现了游戏类型引用 —— 这会破坏"可离线测试"的前提：\n');
    for (const o of offenders) console.error('  ' + o);
    console.error('\n  游戏相关的读取/执行必须放在 src/foodcontrol-game/ 里。\n');
    process.exit(1);
  }
  console.log(`静态检查：Core 无游戏类型引用 ✓（${coreSources.length} 个文件）`);
  console.log(`Game 层：${gameSources.length} 个文件`);

  if (!gameSources.length && !onlyCore) throw new Error('Game 层没有源文件：' + GAME_DIR);

  if (checkOnly) {
    console.log('\n--check：只做静态检查，不编译。');
    return;
  }

  // ---- Core（用游戏程序集编，供 Game 层引用）----
  await compile({ label: 'Core（供 Game 层引用）', csc, out: CORE_DLL, refs, sources: coreSources });

  if (!onlyCore) {
    // ---- Game ----
    await compile({
      label: 'Game（引用 Core + Assembly-CSharp）',
      csc,
      out: GAME_DLL,
      refs: [...refs, CORE_DLL],
      sources: gameSources,
    });

    console.log('\n✓ 全部编译通过。产物：');
    console.log(`   ${path.relative(WS, CORE_DLL)}`);
    console.log(`   ${path.relative(WS, GAME_DLL)}`);
  }

  if (deploy) {
    for (const dll of [CORE_DLL, GAME_DLL]) {
      if (!isWritable(path.join(DEPLOY_DIR, path.basename(dll)))) {
        console.error(`\n✗ ${path.relative(WS, path.join(DEPLOY_DIR, path.basename(dll)))} 被占用 ——`);
        console.error('  多半是**游戏还在运行**（DLL 被锁）。先执行： node src/cli.js stop');
        process.exit(1);
      }
    }
    fs.mkdirSync(DEPLOY_DIR, { recursive: true });
    for (const dll of [CORE_DLL, GAME_DLL]) {
      const dest = path.join(DEPLOY_DIR, path.basename(dll));
      fs.copyFileSync(dll, dest);
      console.log(`已部署 ${path.relative(WS, dest)}`);
    }
    console.log('\n⚠ 正在运行的游戏不会热加载 DLL —— 要重启实例才生效。');
    console.log('  （`<游戏>\\Mods\\FoodControl` 是 junction，物理文件就在工作区 mods/ 下）');

    // ── 顺便同步建筑样板 ──
    //
    // 为什么要同步：`TemplateLibrary`（游戏内）从 **mod 自己的目录** 读样板，
    // 即 `<游戏>/Mods/FoodControl/templates/*.json`；
    // 而样板受 git 管理、住在仓库根的 `templates/`。junction 打通后拷过去即可见。
    //
    // ⚠ 只拷 `*.json`，**默认不删**目标目录里的其他 json ——
    //   免得把临时试验的样板清掉。要清理得显式加 `--prune`。
    syncTemplates(argv.includes('--prune'));
  }
}

/**
 * 把仓库根 `templates/` 同步到 mod 目录。
 * @param {boolean} prune 是否删除目标目录里"源里没有"的 json
 */
function syncTemplates(prune) {
  const src = path.join(WS, 'templates');
  const dst = path.join(MOD_DIR, 'templates');

  if (!fs.existsSync(src)) {
    console.log('\n模板同步：仓库里没有 templates/ 目录，跳过');
    return;
  }

  fs.mkdirSync(dst, { recursive: true });
  const files = fs.readdirSync(src).filter((f) => f.endsWith('.json'));
  for (const f of files) {
    fs.copyFileSync(path.join(src, f), path.join(dst, f));
  }

  let pruned = 0;
  if (prune) {
    for (const f of fs.readdirSync(dst)) {
      if (!f.endsWith('.json')) continue;
      if (!fs.existsSync(path.join(src, f))) {
        fs.unlinkSync(path.join(dst, f));
        pruned++;
      }
    }
  }

  console.log(`\n模板同步：${files.length} 份 json → ${path.relative(WS, dst)}${pruned ? `（清理了 ${pruned} 份）` : ''}`);
  if (files.length === 0) {
    console.log('  ⚠ 一份样板都没有 —— 住处域会走"程序化兜底"路径（盖 13x13 方块房）');
  }
}

try {
  await main();
} catch (err) {
  console.error('\n[错误] ' + err.message);
  process.exit(1);
}
