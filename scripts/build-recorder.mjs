#!/usr/bin/env node
/**
 * 编译 **殖民地快照录制器**（`src/colonyrecorder/`）—— 一个**完全独立**的 mod，
 * 默认产物写工作区 `tmp/colonyrecorder/`，不碰游戏可见的 Mods 目录。
 *
 * ## 和 `build-mod.mjs` 的关系
 *
 * 录制器原本混在 FoodControl 里（Core 一份 + Game 一份，两个 DLL）。搬出来做成独立 mod 后
 * **只剩一个程序集、不依赖 FoodControl.Core** —— 所以这个脚本比 `build-mod.mjs` 简单：
 * 不需要编 Core、不需要 Core 纯净性静态检查、不需要同步模板。
 * 但**编译参数逐条一致**（同一台机器、同一个运行时，必须用同一套引用与语言版本）：
 *
 *   - `/nostdlib+` + 显式引用游戏 `Managed\` 下的 `mscorlib.dll`：保证用游戏那一套基础库
 *   - `/langversion:7.3`：游戏运行时是旧 Mono，C# 8+ 语法（switch 表达式、`??=`、范围索引）不许用
 *   - 引用游戏目录里的程序集（而不是 net472 参考程序集）：Unity/Mono 下 ABI 最稳
 *
 * ## 为什么编成 `ColonyRecorder.dll`
 *
 *   程序集名 = 文件名 = mod 名，一眼对得上；本 mod 只有这一个 DLL，
 *   `<游戏目录>\Mods\ColonyRecorder\1.6\Assemblies\` 下的它会被 RimWorld 自动加载。
 *
 * ## 为什么不默认写进 mods 目录
 *
 *   正在运行的游戏会把已加载的 DLL 锁住，之后每次编译都失败。默认写 `tmp/` 就不会互相干扰；
 *   要部署时显式 `--deploy`（带锁检测与明确提示）。
 *
 * ⚠ 沙箱下**不能**用 `execFileSync`（管道 stdio 会 EPERM），所以走仓库自己的 `src/proc.js` 的 `capture()`。
 *
 * 用法：
 *   node scripts/build-recorder.mjs             # 编译 → tmp/colonyrecorder/ColonyRecorder.dll
 *   node scripts/build-recorder.mjs --check     # 只做检查（源文件存在、引用程序集齐全），不编译
 *   node scripts/build-recorder.mjs --deploy    # 编完再拷进 mods/ColonyRecorder/1.6/Assemblies/
 *                                               # ⚠ 需要游戏已停，否则 DLL 被锁
 */
import fs from 'node:fs';
import path from 'node:path';
import { gameRoot, WS } from '../src/launcher.js';
import { capture } from '../src/proc.js';

const SRC_DIR = path.join(WS, 'src', 'colonyrecorder');
const OUT_DIR = path.join(WS, 'tmp', 'colonyrecorder');
const DEPLOY_DIR = path.join(WS, 'mods', 'ColonyRecorder', '1.6', 'Assemblies');

const OUT_DLL = path.join(OUT_DIR, 'ColonyRecorder.dll');

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

/**
 * 游戏程序集引用表。
 *
 * ⚠ 只引录制器**真正用到**的那几个 —— 录制器不碰 IMGUI / 字体渲染
 *   （那是 FoodControl 的鼠标坐标覆盖层才要的），少引一个就少一类环境问题。
 */
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

function listCs(dir) {
  if (!fs.existsSync(dir)) return [];
  const out = [];
  const walk = (d) => {
    for (const e of fs.readdirSync(d, { withFileTypes: true })) {
      const full = path.join(d, e.name);
      if (e.isDirectory()) walk(full);
      else if (e.name.endsWith('.cs')) out.push(full);
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

async function compile({ csc, out, refs, sources }) {
  if (!sources.length) throw new Error(`录制器：没有源文件（${SRC_DIR}）`);
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

  console.log('\n── ColonyRecorder（独立 mod，单程序集）─────');
  console.log(`源文件 ${sources.length} 个：`);
  for (const s of sources) console.log('   ' + path.relative(WS, s));
  console.log(`输出：${path.relative(WS, out)}`);

  const res = await capture(csc, args, { timeoutMs: 300_000 });
  const stdout = (res.stdout || '').trim();
  const stderr = (res.stderr || '').trim();
  if (stdout) console.log(stdout);
  if (stderr) console.error(stderr);

  if (res.code !== 0 || !fs.existsSync(out)) {
    throw new Error(`ColonyRecorder 编译失败（退出码 ${res.code}）`);
  }
  const kb = (fs.statSync(out).size / 1024).toFixed(1);
  console.log(`✓ 编译成功  ${path.relative(WS, out)}  (${kb} KB)`);
  return out;
}

async function main() {
  const argv = process.argv.slice(2);
  const checkOnly = argv.includes('--check');
  const deploy = argv.includes('--deploy');

  const csc = findCsc();
  const managed = managedDir();
  const refs = gameRefs(managed);

  console.log(`编译器：${csc}`);
  console.log(`游戏程序集：${managed}`);

  const sources = listCs(SRC_DIR);
  if (!sources.length) throw new Error('录制器没有源文件：' + SRC_DIR);
  console.log(`源文件：${sources.length} 个（${path.relative(WS, SRC_DIR)}）`);

  if (checkOnly) {
    console.log('\n--check：源文件与引用程序集都齐全，不编译。');
    return;
  }

  await compile({ csc, out: OUT_DLL, refs, sources });

  if (deploy) {
    const dest = path.join(DEPLOY_DIR, path.basename(OUT_DLL));
    if (!isWritable(dest)) {
      console.error(`\n✗ ${path.relative(WS, dest)} 被占用 ——`);
      console.error('  多半是**游戏还在运行**（DLL 被锁）。先执行： node src/cli.js stop');
      process.exit(1);
    }
    fs.mkdirSync(DEPLOY_DIR, { recursive: true });

    // ⚠ 实测坑（2026-10-03）：`fs.copyFileSync` 覆盖一个**已被运行中的 Mono 映射**的 DLL 时，
    //   Node 报 `UNKNOWN: unknown error`（底层是 Windows 的
    //   `ERROR_USER_MAPPED_FILE`：请求的操作无法在使用用户映射区域打开的文件上执行）。
    //   但那个文件在**目录项层面可以删除**（删完剩下的映射锚在已删除的 inode 上，
    //   运行中的游戏不受影响，反正它也不会热加载 DLL）。
    //   ⇒ 所以复制失败就退化为 **先删后拷**；再不行才真的报错（让用户 stop 游戏）。
    let copied = false;
    let firstErr = null;
    try {
      fs.copyFileSync(OUT_DLL, dest);
      copied = true;
    } catch (err) {
      firstErr = err;
      try {
        if (fs.existsSync(dest)) fs.rmSync(dest, { force: true });
        fs.copyFileSync(OUT_DLL, dest);
        copied = true;
      } catch {
        copied = false;
      }
    }

    if (!copied) {
      console.error(`\n✗ 部署失败：${path.relative(WS, dest)} 写不进去`);
      console.error('  底层错误：' + (firstErr?.message || '未知'));
      console.error('  ⇒ 先执行： node src/cli.js stop  再重新 --deploy');
      process.exit(1);
    }

    const kb = (fs.statSync(dest).size / 1024).toFixed(1);
    console.log(`\n已部署 ${path.relative(WS, dest)}  (${kb} KB)`);
    console.log('\n⚠ 正在运行的游戏不会热加载 DLL —— 要重启实例才生效。');
    console.log('  ⚠ 游戏目录里还需要一个 junction 指向 mods/ColonyRecorder 才能被游戏看到（那步不在本脚本里）。');
  } else {
    console.log('\n（未加 --deploy：只写 tmp/，没碰 mods/ColonyRecorder/1.6/Assemblies/）');
  }

  console.log('\n产物：');
  console.log(`   ${path.relative(WS, OUT_DLL)}`);
}

try {
  await main();
} catch (err) {
  console.error('\n[错误] ' + err.message);
  process.exit(1);
}
