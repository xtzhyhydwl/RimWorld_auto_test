#!/usr/bin/env node
/**
 * RimWorld 自动测试桥 —— 命令行入口。
 *
 *   node src/cli.js --help                                                          看总表（不执行任何动作）
 *   node src/cli.js <子命令> --help                                                 看该子命令用法（不执行任何动作）
 *   node src/cli.js env-check --profile voiced [--json]                             开测先跑：本次实测环境事实
 *   node src/cli.js start --profile voiced [--port 5174] [--minimized] [--width 1600] [--height 900]   （--profile 必填）
 *   node src/cli.js status [--profile vanilla]
 *   node src/cli.js bridge [--profile vanilla]
 *   node src/cli.js tools [关键词] [--profile vanilla]
 *   node src/cli.js call <工具名> ['{"参数":...}'] [--profile vanilla]
 *   node src/cli.js wait --profile voiced --probe rimworld/list_colonists --condition '{"path":"result.colonists","countEquals":3}' [--timeout 60000]
 *   node src/cli.js advance --profile rhah --to-tick 79200                             推时间到指定 tick（成功即暂停）
 *   node src/cli.js logs [行数] [--profile vanilla]
 *   node src/cli.js live --profile voiced --until 'kind=colony_started' [--timeout 120000]
 *   node src/cli.js stop --profile voiced [--force]                                  （--profile 必填）
 *   node src/cli.js base-mods [--profile vanilla]
 *   node src/cli.js mods list|add|remove|apply --profile voiced [--package-id <id>] [--apply]   被测 mod 清单（--profile 必填）
 */
import fs from 'node:fs';
import path from 'node:path';
import { connectGabp, DEFAULT_REQUEST_TIMEOUT_MS } from './gabp.js';
import { assertToolNameExists } from './toolname.js';
import { waitFor } from './wait.js';
import { buildSampler, resolveEventFile, SOURCE_SPECS } from './wait-sources.js';
import { COMBINATORS, LEAF_OPERATORS, OPERATOR_NOTES } from './condition.js';
import { CONDITIONS_FILE, fillArgs, listScenarios, loadRegistry, resolveScenario } from './conditions.js';
import {
  startGame,
  stopGame,
  waitForBridge,
  readBridgeInfo,
  resolveBridge,
  readProcessRecord,
  gameStatus,
  probeBridge,
  logPathFor,
  tailFile,
  profileDir,
  gameRoot,
  processRecordPath,
  WS,
} from './launcher.js';
import { baseModsFile, readBaseModsFile, diffBaseMods, rewriteActiveMods } from './base-mods.js';
import { ADVANCE_USAGE, parseAdvanceFlags, runAdvance } from './advance.js';
import { PARSE_SAVE_USAGE, formatParseSave, parseSave } from './save-parse.js';
import { TEXT_CHECK_USAGE, checkTextFile, formatTextCheck, textCheckPassed } from './text-check.js';

/**
 * 必须带值的全局 flag。少了值不能退化成 `true`（否则文件会真叫 "true"），
 * 也不能让它掉进 positional 被当成子命令的位置参数。
 */
const VALUE_REQUIRED_FLAGS = new Set(['out']);

function parseArgs(argv) {
  const positional = [];
  const flags = {};
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a.startsWith('--')) {
      const key = a.slice(2);
      const next = argv[i + 1];
      if (VALUE_REQUIRED_FLAGS.has(key) && (next === undefined || next.startsWith('--'))) {
        throw new Error(
          `--${key} 需要一个文件路径（例：--out runs\\2026-10-25-回归\\F-out\\01-wait.txt）\n\n${GENERAL_USAGE}`,
        );
      }
      if (next !== undefined && !next.startsWith('--')) {
        // --arg 要能重复给多次（`--arg x=139 --arg z=231`），单个对象存不下
        if (key === 'arg') (flags.arg ??= []).push(next);
        else flags[key] = next;
        i++;
      } else {
        flags[key] = true;
      }
    } else {
      positional.push(a);
    }
  }
  return { positional, flags };
}

function out(obj) {
  console.log(typeof obj === 'string' ? obj : JSON.stringify(obj, null, 2));
}

/**
 * `--out <file>`：一次接管 stdout 与 stderr，两条流**同时**以 UTF-8 追加写进同一个文件。
 *
 * 为什么要在 CLI 里做：本机 shell 是 Windows PowerShell 5.1，它按 `[Console]::OutputEncoding`（GBK/936）
 * 解码原生命令的 **stderr** 再交给下游，stdout 却是按字节直通 —— 所以 `node … 2>&1 | …` 存下来的文件里
 * 「stdout 中文好好的、stderr 中文全乱码」（`runs\2026-10-25-回归\O-编排观察.md` 的 O1，同一文件内两种共存）。
 * 由 node 自己写盘就绕开了 shell 这一层解码。
 *
 * 为什么必须同步写：各子命令（含 `live.js` / `smoke.js`）里有 `process.exit(...)`，异步写流会在退出的
 * 那一刻丢掉尾巴 —— 丢的往往正是最后那句「为什么失败」。`fs.writeSync` + 同一个 fd 还顺带保证
 * 两条流在文件里的先后顺序与终端里一致。
 */
function installOutputTee(file, argv) {
  let fd;
  try {
    fd = fs.openSync(file, 'a'); // 不存在则创建；已存在则追加（一个文件里能放多次运行）
  } catch (err) {
    // 存不成证据不该让命令本身失败：警告一行，照原样继续，退出码由命令自己决定
    console.error(`[--out] 打不开「${file}」（${err.message}）—— 继续执行，但这次没有存盘证据。`);
    return;
  }

  // 分隔行：这次的命令行 + 时间。一个文件里放多次运行时靠它切开，不必每次换文件名。
  fs.writeSync(fd, `\n===== ${new Date().toISOString()}  node src/cli.js ${argv.join(' ')} =====\n`);

  for (const stream of [process.stdout, process.stderr]) {
    const writeToStream = stream.write.bind(stream);
    stream.write = (chunk, encoding, callback) => {
      const result = writeToStream(chunk, encoding, callback); // 原流先写：终端里看到的一个字节都不变
      try {
        fs.writeSync(fd, chunk); // string 默认 utf8、Buffer 原样；不加 BOM
      } catch {
        // 写证据失败（磁盘满 / 句柄失效）同样不改命令的退出码
      }
      return result;
    };
  }
}

/**
 * `--arg k=v`（可重复）→ `{k: 'v'}`；也接受一次给多个：`--arg x=139,z=231`。
 *
 * 存在的理由只有一个：登记表里的参数是静态 JSON，而坐标 / `pawnId` / `Letter_0` 每次都不同，
 * 用 `$x` 这类占位留口、由命令行填值（见 `src/conditions.js` 的 `fillArgs`）。
 * 值一律先当字符串，`fillArgs` 再把纯数字转成数字 —— 别在参数里偷偷做类型猜测。
 */
function parseArgPairs(value) {
  if (value === undefined) return {};
  const items = Array.isArray(value) ? value : [value];
  const pairs = {};
  for (const item of items) {
    if (item === true) throw new Error('--arg 需要 k=v 形式，例 --arg x=139');
    for (const chunk of String(item).split(',')) {
      const at = chunk.indexOf('=');
      if (at <= 0) throw new Error(`--arg 需要 k=v 形式，收到「${chunk}」`);
      pairs[chunk.slice(0, at).trim()] = chunk.slice(at + 1).trim();
    }
  }
  return pairs;
}

/** 读 JSON 文件并去掉 BOM —— Windows 工具（含 PowerShell 5.1）写 UTF-8 时常带 BOM */function readJsonFile(file) {
  let text = fs.readFileSync(file, 'utf8');
  if (text.charCodeAt(0) === 0xfeff) text = text.slice(1);
  return JSON.parse(text);
}

/**
 * 连上「本 profile 自己的」桥接。
 *
 * 2026-10-24：不再直接 `readBridgeInfo`（那只是解析层的猜测），先过 `resolveBridge()` 的
 * 四道校验（记录可信 / PID 命令行确实是本 profile 的实例 / 端口一致 / token 一致）。
 * 不通过就**不发命令**并说清判据 —— 多实例并行时「打进了别人的局而返回看着正常」比报错糟得多。
 */
async function withBridge(profile, fn, opts = {}) {
  const resolved = await resolveBridge(profile);
  if (!resolved.ok) {
    throw new Error(
      `拒绝连接 profile「${profile}」的桥接：${resolved.reason}\n` +
        `  判据：\n${resolved.evidence.map((e) => `  - ${e}`).join('\n')}\n` +
        '  先跑 `node src/cli.js status --profile ' +
        `${profile}\` 看清状态；别用别的 profile 的端口硬连。`,
    );
  }
  const { info } = resolved;
  const { client, welcome } = await connectGabp(info, opts);
  try {
    return await fn(client, welcome, { ...info, evidence: resolved.evidence, record: resolved.record });
  } finally {
    client.close();
  }
}

/**
 * 「像 JSON，但键名没带引号」——只看 `{a:b}` / `{a:b,c:d}` 这种形状，**只用于错误文案**。
 *
 * 为什么要这个判据：本机 pwsh 是 5.1，`'{"readiness":"playable"}'` 到 node 手上已经是 `{readiness:playable}`，
 * 只报「JSON 解析失败」会让人以为是 CLI 坏了。先把带引号的字符串整段挖掉再看结构位置，
 * 正常 JSON（`{"a":1}`）永远不会命中。
 */
function looksLikeJsonWithBareKeys(text) {
  if (typeof text !== 'string') return false;
  const trimmed = text.trim();
  if (!/^[{[]/.test(trimmed)) return false;
  const withoutStrings = trimmed.replace(/"(?:[^"\\]|\\.)*"/g, '""');
  return /[{,]\s*[A-Za-z_$][\w$.-]*\s*:/.test(withoutStrings);
}

/**
 * 读一段 JSON 参数：优先文件，其次行内文本，其次位置参数。
 *
 * 为什么要文件这一路：PowerShell 会把 JSON 里的双引号吃掉（`'{"a":1}'` 到手可能已经变形），
 * 所以 `--args-file` / `--condition-file` 与 `'@文件'` 不是可选项，而是让命令能被
 * 子代理稳定敲出来的唯一保险；`@` 写法在 PowerShell 里**必须带引号**，否则会被当成 splatting。
 */
function readJsonInput({ inline, file, positional, flag }) {
  const fromFile = (path) => readJsonFile(path);
  const fromText = (text) => {
    if (text === true) throw new Error(`${flag} 需要一个值（JSON 文本，或 '@文件.json'）`);
    try {
      return text.startsWith('@') ? fromFile(text.slice(1)) : JSON.parse(text);
    } catch (err) {
      // 报错时最贵的不是「解析失败」，是让人去猜为什么失败：本机 pwsh 是 5.1，`'{"a":1}'`
      // 到手会变成 `{a:1}`（双引号被吞），照文档抄一遍必失败。所以识别这个形状并直接给两条出路。
      throw new Error(
        `${flag} 的 JSON 解析失败：${err.message}\n  收到：${String(text).slice(0, 300)}` +
          (looksLikeJsonWithBareKeys(text)
            ? '\n  这通常是 Windows PowerShell 5.1 吞掉了双引号——改用 --args-file <文件> / --condition-file <文件>，或把内层引号写成 \\"'
            : ''),
      );
    }
  };

  if (file !== undefined) return fromFile(file);
  if (inline !== undefined) return fromText(inline);
  if (positional !== undefined) return fromText(positional);
  return undefined;
}

const WAIT_USAGE = [
  '用法（两条路）:',
  '  node src/cli.js wait --profile <p> --scenario <场景名> [--arg k=v …] [--timeout 60000]',
  '  node src/cli.js wait --profile <p> --probe <工具名> (--condition \'<JSON>\' | --condition-file <文件>) [选项]',
  '  node src/cli.js wait --list            列出登记表里的全部场景（不连桥接）',
  '',
  '  --scenario 已进图可玩                 场景来自 src/conditions.json；探针/参数/条件都由它给，不许再给 --probe/--source',
  '  --arg x=139 [--arg z=231]            给场景 args 里的 $占位填值（可重复；也可 --arg x=139,z=231）',
  '  --source bridge                      反复调一次只读桥接工具（要 --probe；不给 --source 时的默认）',
  '  --source event                       跟读 NDJSON 事件文件或 Player.log（--file / --event-format / --new-only / --window-ms）',
  '  --source process                     探一个 PID 的三态 alive|foreign|none（--pid，缺省用本 profile 记录里的 PID）',
  '  --source bridge-ready                只校验「这个桥接是不是我的」（记录/PID 身份/端口/token 四道校验）',
  '  --probe rimworld/list_colonists      要反复调用的只读工具名',
  '  --args \'{"currentMapOnly":true}\'      探针参数（也可 --args-file f.json 或 \'@f.json\'）',
  '  --condition \'{"path":"result.colonists","countEquals":3}\'   条件 JSON（也可 --condition-file f.json 或 \'@f.json\'）',
  '  --timeout 60000                      等多久（毫秒；场景自带 timeoutMs 时以场景为准，显式给则覆盖）',
  '  --interval 200                       两次采样的间隔（毫秒，默认 200）',
  '  退出码：0=条件命中 / 2=超时（打印条件+最后采样+attempts+elapsedMs）/ 1=参数错、工具名不存在、桥接未就绪等',
  '  条件与路径写法见 .dsh/skills/rimworld-auto-test-closed-loop/docs/cli-wait-用法-20261024.md',
].join('\n');

/** `mods` 的四条子命令 —— 用法文案与「缺 --profile」的示例都从这里取，免得两处各写一份 */
const MODS_SUBCOMMANDS = ['list', 'add', 'remove', 'apply'];

const MODS_USAGE = [
  '用法:',
  '  node src/cli.js mods list   --profile <p> [--json]',
  '  node src/cli.js mods add    --profile <p> --package-id <id> [--name <显示名>] [--why <一句话>] [--apply]',
  '  node src/cli.js mods remove --profile <p> --package-id <id> [--apply]',
  '  node src/cli.js mods apply  --profile <p>',
  '',
  '  被测 mod 的清单存在 profiles\\<p>\\tested-mods.json（不存在 ⇒ 空清单）；底座在仓库根 base-mods.json。',
  '  add/remove 只改清单数据文件，**只有 --apply 才动 ModsConfig.xml**（顺序 = 底座 → 被测 → 其余，幂等）。',
  '  --profile 必填：mods 改的是「某个 profile 的文件」，猜一个就等于改错别人的清单。',
  '  packageId 按大小写不敏感比较；写入保留你给的原写法；重复 add 幂等（只更新 name/why，不重复追加）。',
  '  退出码：0=成功 / 1=参数错或数据文件坏了（tested-mods.json 不是合法 JSON、packageId 形状不对）。',
  '  加一个被测 mod 的最小闭环：mods add … --apply → start --profile <p> → env-check --profile <p>',
].join('\n');

/** `wait` 打印采样时的上限：真载荷（get_bridge_status 带 companions/operation）能有 8 KB */
const SAMPLE_PRINT_LIMIT = 2000;

/**
 * 打印层的采样截断。为什么要截：一次超时把整个载荷糊到 stderr，子代理的上下文就被吃掉
 * 一大截，而它要看的只是条件涉及的那几个字段；完整载荷随时能用 `call` 重取。
 * 只截这一层——`src/wait.js` 返回的 `sample` 保持完整，离线测试与调用方都拿原对象。
 */
function evidenceForPrint(evidence) {
  const sampleJson = JSON.stringify(evidence.sample) ?? String(evidence.sample);
  if (sampleJson.length <= SAMPLE_PRINT_LIMIT) return { ...evidence, sampleTruncated: false };
  return {
    ...evidence,
    sampleTruncated: true,
    sample: `${sampleJson.slice(0, SAMPLE_PRINT_LIMIT)}…`,
  };
}

/**
 * 每个子命令的 `--help`。**必须在任何动作之前拦下**：`node src/cli.js start --help` 曾经
 * 真的起了一个 561 mod 的满配实例（2026-10-24 事故）——「查参数」是最便宜的操作，不该有副作用。
 * 这里只放「用法 + 这条命令特有的坑」；详细用法留在实现里（`wait` 直接用上面的 WAIT_USAGE，
 * 免得同一份用法写两遍然后各自漂移）。
 */
const COMMAND_HELP = {
  start: [
    '用法: node src/cli.js start --profile <名字> [--port 5174] [--minimized] [--width 1600] [--height 900]',
    '                                  [--monitor 2] [--fullscreen|--windowed] [--doorstop]',
    '',
    '  --profile 必填：不填不会替你猜（缺省值 vanilla 是满配 profile（几百个 mod，代价最大））；可选值见 profiles\\ 目录。',
    '  会真起一个游戏实例并等桥接就绪（抢一次窗口焦点）。底座 mod 按 base-mods.json 自动补齐（只加不删）。',
    '  起了之后：node src/cli.js status --profile <名字> 看状态 / stop --profile <名字> 关掉它。',
  ].join('\n'),
  stop: [
    '用法: node src/cli.js stop --profile <名字> [--force]',
    '',
    '  --profile 必填：默认只关「这个 profile 记录里的那个 PID」，不动你自己那一局。',
    '  --force 会枚举全机 RimWorld 并强杀（含用户在玩的那局）——只有用户当次明确要求才用。',
  ].join('\n'),
  smoke: [
    '用法: node src/cli.js smoke --profile <名字> [--ticks 300] [--attach] [--keep-running]',
    '',
    '  --profile 必填：它默认会起一个实例、跑完再关掉（--attach 表示连已经在跑的那个实例）。',
    '  报告落 runs\\<时间戳>_smoke\\report.md；退出码 0=全过 / 1=有断言没过。',
  ].join('\n'),
  'env-check': [
    '用法: node src/cli.js env-check --profile <名字> [--json]',
    '',
    '  **每次开测先跑它**：把「本次实测环境事实」（进程 / 桥接归属 / 激活 mod / 底座差额 / 加载顺序 /',
    '  CE 能力 / 时间与强制暂停 / 路径 / 限制）一次问清，替代文档里写死的环境数字。',
    '  只读、绝不启动游戏；游戏没起时除桥接相关项标 unknown 外其余照常输出。',
    '  --json  给机器读的完整对象（默认是人读的短摘要）。',
    '  退出码：0=没有阻塞项 / 1=有阻塞项（verdict.blockers 里是会让你照着做的一句话，不是崩溃）。',
  ].join('\n'),
  status: [
    '用法: node src/cli.js status [--profile <名字>]',
    '',
    '  gameRunning 是三态 true/false/null（null=判据不足，不要当 false）；同时给出逐条判据与桥接归属校验。',
  ].join('\n'),
  bridge: ['用法: node src/cli.js bridge [--profile <名字>]', '', '  只打印「这个 profile 该连哪个桥接 + 凭什么这么认为」，不连它。'].join('\n'),
  tools: [
    '用法: node src/cli.js tools [关键词] [--profile <名字>]',
    '',
    '  关键词按「工具名 + 描述」子串匹配（大小写不敏感），结果分「名字命中 / 仅描述命中」两段显示。',
    '  需要活桥接；没有游戏时只有 runs\\tools.json 那份旧导出可看。',
  ].join('\n'),
  describe: ['用法: node src/cli.js describe <工具名> [--profile <名字>]', '', '  打印单个工具的完整 schema。'].join('\n'),
  'dump-tools': ['用法: node src/cli.js dump-tools [--profile <名字>]', '', '  把全部工具 schema 导出到 runs\\tools.json（离线时唯一可查的目录）。'].join('\n'),
  call: [
    '用法: node src/cli.js call <工具名> [\'{"参数":...}\'] [--args-file f.json | \'@f.json\'] [--profile <名字>] [--timeout 60000]',
    '',
    '  载荷原样打到 stdout；载荷里 success:false（动作失败）时 **exit 1**，别按 exit 0 写成通过。',
    '  --timeout 默认 60 s（与 gabp.js 同一个常量）：桥接把调用排在游戏主线程上，长操作要显式给大值。',
  ].join('\n'),
  wait: WAIT_USAGE,
  advance: ADVANCE_USAGE,
  'parse-save': PARSE_SAVE_USAGE,
  'text-check': TEXT_CHECK_USAGE,
  logs: ['用法: node src/cli.js logs [行数] [--profile <名字>]', '', '  打印该 profile 的 Player.log 尾部（1.7 起这份日志全机共用）。'].join('\n'),
  live: [
    '用法: node src/cli.js live --profile <名字> (--until \'<条件>\' | --last N) [选项]',
    '',
    '  --until \'kind=colony_started\'            条件成立即命中并退出 0（超时退出 2）',
    '  --last N / --new-only / --timeout 120000 / --source events|log / --file <路径>',
    '  挂成后台 job 时，job 完成的通知本身就是「事件到了」的推送 —— 别用固定 sleep 等游戏。',
  ].join('\n'),
  atoms: ['用法: node src/cli.js atoms', '', '  原子能力清单（算子 / 四类来源的快照字段 / 条件登记表统计），写测试前先看这个。'].join('\n'),
  'base-mods': [
    '用法: node src/cli.js base-mods [--profile <名字>]',
    '',
    '  打印 base-mods.json（全项目唯一一份底座清单）；带 --profile 才顺便对账，只报告不写文件。',
  ].join('\n'),
  mods: MODS_USAGE,
};

/** 没有专门 help 时打这份总表；`node src/cli.js`（无参数）打的是同一份 */
const GENERAL_USAGE = [
  'RimWorld 自动测试桥',
  '',
  '  node src/cli.js <子命令> --profile <名字> [选项]   --profile 决定「操作哪个隔离实例」；start/stop/smoke/env-check 必须显式给',
  '  node src/cli.js <子命令> --help                    只看用法，不执行任何动作（查参数不该有副作用）',
  '  node src/cli.js --help                             看这一份总表',
  '  --out <file>  把 stdout+stderr 同时以 UTF-8 追加存盘（存证据用，避免 PS 5.1 把 stderr 中文解码成乱码）',
  '',
  '  env-check 每次开测先跑：进程/桥接归属/激活 mod/加载顺序/CE/时间/路径 一次问清（不启动游戏）',
  '  start    启动隔离测试实例并等待桥接就绪（--port N 显式指定端口，否则自动挑 5174..5183 里空闲的）',
  '  status   查看该 profile 的进程 / 上次启动 / 日志中的桥接信息',
  '  bridge   只打印该 profile 的桥接端口与 token',
  '  tools    列出游戏暴露的工具（可加关键词过滤）',
  '  describe 显示单个工具的完整参数 schema',
  '  dump-tools  把所有工具 schema 导出到 runs/tools.json',
  '  call     调用一个游戏内工具（载荷 success:false ⇒ exit 1）',
  '  wait     等条件成立（反复调探针 + 条件 JSON；0 命中 / 2 超时 / 1 其它），见 .dsh/skills/rimworld-auto-test-closed-loop/docs/cli-wait-用法-20261024.md',
  '  advance  把游戏推进到指定 tick（--to-tick N）或推到某事件发生（--stop-when）：走 GameLink 命令通道，游戏自己刹在 tick 边界，成功即暂停',
  '  link     给命令通道发一条命令（--cmd time.status|time.probe|time.cancel），只打 ack',
  '  parse-save 从 .rws 存档读一条 hediff 的取证信息（severity / ttd / healthState / 营养不良）',
  '  text-check 验文件有没有被 PS 5.1 写坏（严格 UTF-8 / 无 U+FFFD / 无 BOM）',
  '  atoms    原子能力清单（算子 / 四类来源的快照字段 / 登记表统计）—— 写测试前先看这个',
  '  base-mods 底座 mod 清单（base-mods.json）；带 --profile <p> 只报告该 profile 缺哪几个，不写文件',
  '  mods     被测 mod 清单（profiles/<p>/tested-mods.json）：list / add / remove / apply —— 只有 --apply 才改 ModsConfig',
  '  logs     打印该 profile 的 Player.log 尾部',
  '  live     等游戏侧事件（DSH_GameLink 的 NDJSON 或 Player.log），命中即返回',
  '  stop     关闭该 profile 的测试实例（--force 才会强杀全机 RimWorld，破坏性）',
  '  smoke    跑冒烟测试闭环',
].join('\n');

/**
 * 这次 `--help` 该打谁的用法。
 * `node src/cli.js --help start` 也能拿到 start 的用法（人常这么写），否则打总表。
 */
function helpTextFor(cmd, positional) {
  const wanted = COMMAND_HELP[cmd] ? cmd : COMMAND_HELP[positional?.[0]] ? positional[0] : null;
  return wanted ? COMMAND_HELP[wanted] : GENERAL_USAGE;
}

/**
 * profiles\ 下有哪些 profile。清单写死在代码里会在「有人新建 profile」的那天过期，
 * 所以每次现读盘 —— 拒绝执行时列出的「可用 profile」也来自这里。
 */
function listProfileNames() {
  try {
    return fs
      .readdirSync(path.join(WS, 'profiles'), { withFileTypes: true })
      .filter((entry) => entry.isDirectory())
      .map((entry) => entry.name)
      .sort();
  } catch {
    return [];
  }
}

/**
 * 会**真起游戏 / 真关游戏**或**只报某个 profile 的事实**的子命令：`--profile` 必须显式给。
 *
 * 为什么：`flags.profile || 'vanilla'` 这个默认值会把「想试试参数 / 打错一个子命令」变成
 * 「起一个 561 mod 的满配实例并改写它的 Config」——2026-10-24 真发生过（拆开看：默认值 + 没有
 * `--help` 拦截）。读类命令（status/tools/call/wait/logs/live/base-mods）保留原默认，
 * 改它们的默认值会把已有脚本全弄坏。
 */
const PROFILE_REQUIRED = {
  start: '会真起一个游戏实例（还会按 base-mods.json 改写该 profile 的 ModsConfig.xml）；缺省值 vanilla 是满配 profile（几百个 mod），猜错代价最大',
  stop: '会关掉一个游戏实例；缺省值 vanilla 是满配 profile（几百个 mod），猜错代价最大',
  smoke: '默认会起一个实例、跑完再关掉；缺省值 vanilla 是满配 profile（几百个 mod），猜错代价最大',
  'env-check': '报的是「某个 profile 的」环境事实，猜错了等于把别人的事实钉进你的上下文',
  mods: '读写的是「某个 profile 的被测 mod 清单」并会改写它的 ModsConfig.xml，猜错了就是把 mod 加进别人的环境',
  advance: '会长时间推进某个实例的游戏时间（几分钟起），猜错 profile 等于把别人的局推走几万个 tick 且不可逆',
};

function requireExplicitProfile(cmd, flags, positional = []) {
  const why = PROFILE_REQUIRED[cmd];
  if (!why) return;
  if (typeof flags.profile === 'string' && flags.profile.trim()) return;
  const names = listProfileNames();
  // 照抄示例优先给 voiced：它是 AGENTS.md 资源约定里那条「只开轻 profile」的例子
  const sample = names.includes('voiced') ? 'voiced' : (names[0] ?? '<名字>');
  // mods 的四条子命令要的参数各不相同，示例里带上子命令，「照抄」才有意义
  const exampleCmd = cmd === 'mods' && MODS_SUBCOMMANDS.includes(positional[0]) ? `mods ${positional[0]}` : cmd;
  // package.json 里的 "start"/"stop"/"smoke" 就是 `node src/cli.js <同名>`，它们现在也会被这条拒绝：
  // 把「npm 传参要 --」这个坑直接写在错误里，省得人去翻 npm 文档
  const npmHint =
    process.env.npm_lifecycle_event === cmd ? `  你是通过 npm run ${cmd} 跑的：改成 npm run ${cmd} -- --profile ${sample}\n` : '';
  throw new Error(
    `\`${cmd}\` ${why}，所以缺 --profile 时直接拒绝执行。\n` +
      `  可用 profile（profiles\\ 下的目录）：${names.length ? names.join(', ') : '（一个都没有）'}\n` +
      `  底座 mod 清单（所有 profile 共用，start 时自动补齐）：${baseModsFile(WS)}\n` +
      npmHint +
      `  照抄：node src/cli.js ${exampleCmd} --profile ${sample}`,
  );
}

/** 条件登记表的统计 —— `atoms` 与 `env-check` 共用一处，免得两个命令的计数各自漂移 */
function summarizeRegistry(registry) {
  return {
    file: CONDITIONS_FILE,
    scenarios: Object.keys(registry.scenarios).length,
    byLevel: listScenarios(registry).reduce((acc, s) => {
      acc[s.verifiedLevel] = (acc[s.verifiedLevel] ?? 0) + 1;
      return acc;
    }, {}),
    errors: registry.errors,
  };
}

/**
 * 这份载荷是不是「工具自己报错」的形状。
 *
 * 真机实测（2026-10-25 试跑 B2）：探针自己 `success:false` 时，条件路径断在 `result` 那一层，
 * 字段恰好是 `success, message, exception, operation`。这时「字段取不到」只是**后果**，
 * 让人去「等进图 / 改字段名」会白等满一轮还把问题改到别处。
 *
 * 光看字段名会误报：成功载荷也可能带 `success + message + operation`
 * （`start_debug_game_ready` 就是），所以能拿到本次采样时再确认一次 `success === false`。
 * 只看名字像字段的项 —— condition.js 会在 available 末尾追加一句「…共 N 个」的摘要。
 */
function looksLikeToolErrorPayload(available = [], sample = null) {
  const names = new Set(
    available
      .filter((key) => /^[A-Za-z_$][A-Za-z0-9_$]*$/.test(key))
      .map((key) => key.toLowerCase()),
  );
  const envelope = names.has('success') && (names.has('message') || names.has('exception') || names.has('operation'));
  if (!envelope) return false;
  if (sample && typeof sample === 'object' && !Array.isArray(sample) && sample.success !== undefined) {
    return sample.success === false;
  }
  return true;
}

/** `call` 失败时那一行 stderr 要说的话：优先 message，其次 operation.Error / exception */
function toolFailureText(res) {
  const detail = res?.message ?? res?.operation?.Error ?? res?.exception ?? res?.error ?? null;
  if (detail === null || detail === undefined) return null;
  return typeof detail === 'string' ? detail : JSON.stringify(detail);
}

/**
 * `profiles/<p>/Config/ModsConfig.xml` 里 `<activeMods>` 的内容 —— 游戏没起时唯一的事实来源。
 *
 * 解析只有一处（`activeModIdsInXml`）；这里负责「读盘 + 把读不到的原因说清楚」，不另写一套正则。
 */
function readActiveModsInProfile(profile) {
  const file = path.join(profileDir(profile), 'Config', 'ModsConfig.xml');
  let xml;
  try {
    xml = fs.readFileSync(file, 'utf8');
  } catch {
    return {
      file,
      count: null,
      ids: null,
      xml: null,
      missingFile: true,
      reason: '没有 ModsConfig.xml（这个 profile 还没启动过）',
    };
  }
  const ids = activeModIdsInXml(xml);
  if (ids === null) {
    return { file, count: null, ids: null, xml, missingFile: false, reason: 'ModsConfig.xml 里没有 <activeMods> 段' };
  }
  return { file, count: ids.length, ids, xml, missingFile: false }; // xml 给「顺序是否已规范」的判定复用，避免二次读盘
}

/** 显示用：读不到就是 'unknown'，别用 false/null 冒充「已确认」 */
function knownOrUnknown(value) {
  return value === undefined || value === null ? 'unknown' : value;
}

/* ═══════════════════ `mods` 子命令：被测 mod 清单与 ModsConfig 对齐 ═══════════════════ */

/**
 * 被测 mod 的清单落在 `profiles/<p>/tested-mods.json`，底座在仓库根 `base-mods.json` ——
 * 两份数据文件各管一段，谁也不抄谁：
 *   base-mods.json  所有 profile 共用，start 前自动补齐（只加不删）；
 *   tested-mods.json 单个 profile 自己的被测 mod，由 `mods` 子命令读写。
 * 好处是加一个被测 mod 变成「往一份数据文件里加一条」，不用手改 XML（手改没有校验、不幂等、
 * 写坏了要等游戏启动才发现）。
 */
const TESTED_MODS_FILENAME = 'tested-mods.json';
const TESTED_MODS_DESCRIPTION = '本 profile 的被测 mod；底座见仓库根 base-mods.json';

function testedModsFile(profile) {
  return path.join(profileDir(profile), TESTED_MODS_FILENAME);
}

/**
 * 读 `profiles/<p>/tested-mods.json`。
 *
 * 文件不存在 ⇒ 空清单（不是错误：新 profile 还没有被测 mod 是正常状态，`mods list` 不该因此报错）。
 * 文件存在但 JSON 坏了 ⇒ **抛错**，绝不退化成空清单：那样下一次 `mods add` 会把用户手写的内容整体覆盖掉，
 * 而覆盖是静默的（文件照样写得成功）。
 */
function readTestedMods(profile) {
  const file = testedModsFile(profile);
  let text;
  try {
    text = fs.readFileSync(file, 'utf8');
  } catch (err) {
    if (err.code === 'ENOENT' || err.code === 'ENOTDIR') return { file, missingFile: true, mods: [] };
    throw err;
  }
  let data;
  try {
    data = JSON.parse(text.charCodeAt(0) === 0xfeff ? text.slice(1) : text);
  } catch (err) {
    throw new Error(
      `${file} 不是合法 JSON：${err.message}\n` +
        '  它是手写数据文件，CLI 不把它当空清单（那样下一次 mods add 会把你的内容覆盖掉）。\n' +
        `  先修好它，或改名备份后再跑：Move-Item '${file}' '${file}.bak'`,
    );
  }
  const mods = [];
  for (const entry of Array.isArray(data?.mods) ? data.mods : []) {
    const packageId = String(entry?.packageId ?? '').trim();
    if (!packageId) continue;
    // 同一条写两遍是数据笔误：按第一处只留一个，否则 apply 会把它算两次
    if (mods.some((m) => m.packageId.toLowerCase() === packageId.toLowerCase())) continue;
    mods.push({ packageId, name: String(entry?.name ?? ''), why: String(entry?.why ?? '') });
  }
  /**
   * Windows 上的老坑（仓库级，见 AGENTS.md）：用 PowerShell 5.1 的 `Set-Content` / 重定向写这份文件，
   * 中文会落成 GBK，读进来是 U+FFFD。**JSON 照样解析成功**——只有 name/why 变乱码，packageId 是 ASCII
   * 不受影响，所以排序与加载都没事；但不说一句，用户只会看到「名字变成一片问号」而不知道原因。
   * 只警告不拒绝：乱码不是数据损坏到不能用，拒绝会把人挡在门外。
   */
  const garbled = mods.filter((m) => m.name.includes('\ufffd') || m.why.includes('\ufffd'));
  if (garbled.length || String(data?.description ?? '').includes('\ufffd')) {
    console.error(
      `[警告] ${file} 里的中文是乱码（U+FFFD）${garbled.length ? `：${garbled.map((m) => m.packageId).join(', ')}` : ''}\n` +
        '  通常是写这份文件时用了 GBK（PowerShell 5.1 的 Set-Content / shell 重定向）。' +
        'packageId 是 ASCII，排序与加载不受影响；要修就用能写 UTF-8 的工具（本仓库：write/edit 工具）重写 name/why。',
    );
  }
  return { file, missingFile: false, schema: data?.schema ?? null, description: String(data?.description ?? ''), mods };
}

/**
 * 写 `tested-mods.json`（无 BOM UTF-8）。
 *
 * 形状固定成 `{ schema, profile, description, mods: [{ packageId, name, why }] }` ——
 * 空字符串的 name/why 不落盘，免得手改时被一堆空字段淹掉；`packageId` 一律排第一。
 */
function writeTestedMods(profile, mods) {
  const file = testedModsFile(profile);
  fs.mkdirSync(path.dirname(file), { recursive: true });
  const data = {
    schema: 1,
    profile,
    description: TESTED_MODS_DESCRIPTION,
    mods: mods.map(({ packageId, name, why }) => ({
      packageId,
      ...(name ? { name } : {}),
      ...(why ? { why } : {}),
    })),
  };
  fs.writeFileSync(file, `${JSON.stringify(data, null, 2)}\n`, 'utf8');
  return file;
}

/**
 * packageId 的形状检查：非空、无空白、无 `<` `>`。
 *
 * 为什么只查这三样：`<li>${packageId}</li>` 是直接拼进 XML 的，一个 `<` 就能把整段 activeMods 写坏，
 * 而写坏的表现是「游戏启动后 mod 列表里少几条」，很难反推到一条手输命令。除此之外不猜作者的命名习惯
 * ——mod 作者的写法什么都有（点分、下划线、大小写混排）。
 */
function assertPackageIdShape(raw) {
  const example = 'node src/cli.js mods add --profile <p> --package-id xiayuan2004.VoiceDeputy --name 副官语音';
  const given = raw === true ? '' : String(raw ?? '').trim();
  if (!given) throw new Error(`--package-id 必填（mod 的 packageId，不是显示名）。\n  照抄：${example}`);
  const bad = [];
  if (/\s/.test(given)) bad.push('含空格/空白');
  if (/[<>]/.test(given)) bad.push('含 < 或 >');
  if (bad.length) throw new Error(`--package-id「${given}」形状不对（${bad.join('、')}）。\n  照抄：${example}`);
  return given;
}

/** 一次进程内只扫一遍本机 mod 目录（一条 `mods` 子命令只会问一次「装没装」） */
let installedModsScan = null;

/** `<游戏>` = `<库>\steamapps\common\RimWorld` ⇒ 工坊内容在 `<库>\steamapps\workshop\content\294100` */
function workshopContentDir(root) {
  return path.join(path.dirname(path.dirname(root)), 'workshop', 'content', '294100');
}

/**
 * 从一份 `About/About.xml` 里取**这个 mod 自己**的 `<packageId>`。
 *
 * 必须先挖掉 `<li>…</li>`：`<modDependencies>` 的每一项也是 `<li><packageId>…</packageId>…</li>`，
 * 而且**可能排在真 packageId 之前**——实测工坊 `1195427067`（Architect Icons）就是这样，
 * 直接取第一个 `<packageId>` 会把它报成 Harmony，于是「装没装」查到了错目录。
 */
function aboutPackageId(xml) {
  const withoutListItems = xml.replace(/<li>[\s\S]*?<\/li>/g, '');
  return /<packageId>\s*([^<]+?)\s*<\/packageId>/i.exec(withoutListItems)?.[1];
}

/**
 * 扫「本机装了哪些 mod」：游戏自己的 `Mods\` 与 Steam 工坊 `content\294100\`，逐个读 `About\About.xml` 的
 * `<packageId>`。
 *
 * 路径全部从 `gameRoot()` 推，**不新建配置文件**存它：gameRoot() 已经由 `RIMWORLD_ROOT` 管着，
 * 再存一份路径就是又一处会漂的真源。
 * 只代表「盘上有这个文件夹」，不代表「已订阅」——本地 ACF 只是下载账本，订阅列表在 Steam 服务器端
 * （见 `docs\mod列表整理-结论.md`）；所以没扫到只警告、不拒绝。
 */
function scanInstalledMods() {
  if (installedModsScan) return installedModsScan;
  const root = gameRoot();
  const roots = [path.join(root, 'Mods'), workshopContentDir(root)];
  const byId = new Map();
  let scanned = 0;
  for (const dir of roots) {
    let entries;
    try {
      entries = fs.readdirSync(dir, { withFileTypes: true });
    } catch {
      continue; // 目录不存在：由 existingRoots 决定「unknown」还是「没装」
    }
    for (const entry of entries) {
      // 游戏 Mods\ 下的本地 mod 常是 junction（LinkType=Junction），要一起认
      if (!entry.isDirectory() && !entry.isSymbolicLink()) continue;
      const modDir = path.join(dir, entry.name);
      let xml;
      try {
        xml = fs.readFileSync(path.join(modDir, 'About', 'About.xml'), 'utf8');
      } catch {
        continue;
      }
      const packageId = aboutPackageId(xml);
      if (!packageId) continue;
      scanned += 1;
      const key = packageId.toLowerCase();
      if (byId.has(key)) continue; // 同一 mod 在 Mods\ 与工坊各有一份：记第一次看到的
      byId.set(key, { packageId, dir: modDir, name: /<name>\s*([^<]+?)\s*<\/name>/i.exec(xml)?.[1] ?? '' });
    }
  }
  installedModsScan = { roots, existingRoots: roots.filter((d) => fs.existsSync(d)), byId, scanned };
  return installedModsScan;
}

/**
 * 这个 packageId 在本机装没装。三态：`true` / `false`（扫过、没命中）/ `'unknown'`（连 mod 目录都没找到）。
 * `false` 不拒绝 add：用户可能还没订阅，或游戏装在别的路径（`RIMWORLD_ROOT` 能指）。
 */
function locateInstalledMod(packageId) {
  const scan = scanInstalledMods();
  if (!scan.existingRoots.length) {
    return {
      installed: 'unknown',
      dir: null,
      name: '',
      reason: `本机没找到 mod 目录（找过：${scan.roots.join(' / ')}）—— 游戏可能装在别处，用 RIMWORLD_ROOT 指过去再试`,
    };
  }
  const hit = scan.byId.get(packageId.toLowerCase());
  if (!hit) {
    return {
      installed: false,
      dir: null,
      name: '',
      reason: `扫过 ${scan.existingRoots.join(' / ')} 里的 ${scan.scanned} 份 About.xml，没有 packageId「${packageId}」`,
    };
  }
  return { installed: true, dir: hit.dir, name: hit.name, reason: null };
}

/**
 * 从一个 ModsConfig.xml 文本里取 `<activeMods>` 的条目；没有这一段返回 null。
 * `readActiveModsInProfile`（读盘）与 `applyModsConfig`（算完顺序后回读）共用这一处解析。
 */
function activeModIdsInXml(xml) {
  const section = /<activeMods>([\s\S]*?)<\/activeMods>/.exec(xml);
  if (!section) return null;
  return [...section[1].matchAll(/<li>([^<]*)<\/li>/g)].map((m) => m[1].trim()).filter(Boolean);
}

/**
 * 把 ModsConfig.xml 的 `<activeMods>` 对齐成「底座（base-mods.json 顺序）→ 被测 mod（tested-mods.json 顺序）
 * → 其余保持原相对顺序」，返回是否真写了文件。
 *
 * 重排本身复用 `base-mods.js` 的 `rewriteActiveMods`（与 `start` 前补齐底座**同一处实现**），
 * 所以「谁排最前」只有一份代码；幂等也由它保证（顺序已规范就原样返回），这里直接比字符串。
 *
 * `drop` 只给 `mods remove --apply` 用（理由见 `dropFromActiveMods`）。
 */
function applyModsConfig(profile, { drop = null } = {}) {
  const base = readBaseModsFile(WS);
  if (base.missingFile) {
    throw new Error(
      `没有 ${baseModsFile(WS)}：底座清单是「谁排最前」的唯一真源，缺了它算不出顺序。先恢复这份文件。`,
    );
  }
  // 测试清单坏了会抛：不许拿空清单去重排（那会把被测 mod 排到「其余」里去）
  const tested = readTestedMods(profile);
  const file = path.join(profileDir(profile), 'Config', 'ModsConfig.xml');
  let original;
  try {
    original = fs.readFileSync(file, 'utf8');
  } catch (err) {
    if (err.code === 'ENOENT' || err.code === 'ENOTDIR') {
      return {
        modsConfigPath: file,
        written: false,
        skipped: `没有 ${file}（这个 profile 还没启动过）—— 先 node src/cli.js start --profile ${profile} 让它生成`,
      };
    }
    throw err;
  }
  if (activeModIdsInXml(original) === null) {
    return { modsConfigPath: file, written: false, skipped: `${file} 里没有 <activeMods> 段（不猜位置，没写文件）` };
  }

  let xml = original;
  let dropped = 0;
  if (drop) {
    const result = dropFromActiveMods(xml, drop);
    xml = result.xml;
    dropped = result.dropped;
  }
  const beforeIds = activeModIdsInXml(xml) ?? [];
  const beforeKeys = new Set(beforeIds.map((id) => id.toLowerCase()));
  const nextXml = rewriteActiveMods(xml, {
    front: base.packageIds,
    then: tested.mods.map((m) => m.packageId),
  });
  const written = nextXml !== original;
  // 'utf8' 写出去就是无 BOM；游戏与 read 工具都按 UTF-8 读这份 XML
  if (written) fs.writeFileSync(file, nextXml, 'utf8');
  const afterIds = activeModIdsInXml(nextXml) ?? [];
  const afterKeys = new Set(afterIds.map((id) => id.toLowerCase()));
  return {
    modsConfigPath: file,
    written,
    // null = 这次根本没要求删行（`mods apply` / `add --apply`），0 = 要求删但没找到
    dropped: drop ? dropped : null,
    activeIds: afterIds,
    added: afterIds.filter((id) => !beforeKeys.has(id.toLowerCase())),
    removed: beforeIds.filter((id) => !afterKeys.has(id.toLowerCase())),
    baseCount: base.packageIds.length,
    testedCount: tested.mods.length,
  };
}

/**
 * 从 `<activeMods>` 里**删掉**一条 —— 全仓库**唯一**会删 activeMods 的地方。
 *
 * 为什么别处一律只加不删、而这里可以删：底座补齐（`ensureBaseMods`）与 `mods apply` 是在「替 profile
 * 补齐/排序」，替人做减法会让用户的 mod 静默消失；而 `mods remove --apply` 的语义就是**用户亲口说删掉它**。
 * 不删的后果更糟：那条 `<li>` 会从「被测 mod」掉进「其余条目」，apply 之后依旧被游戏加载，
 * 于是「remove 了却还在跑」。
 */
function dropFromActiveMods(xml, packageId) {
  const section = /<activeMods>([\s\S]*?)<\/activeMods>/.exec(xml);
  if (!section) return { xml, dropped: 0 };
  const want = String(packageId).toLowerCase();
  let dropped = 0;
  const inner = section[1].replace(/[ \t]*<li>([^<]*)<\/li>[ \t]*(\r?\n)?/g, (line, id) => {
    if (id.trim().toLowerCase() !== want) return line;
    dropped += 1;
    return '';
  });
  if (!dropped) return { xml, dropped: 0 };
  const next = `${xml.slice(0, section.index)}<activeMods>${inner}</activeMods>${xml.slice(section.index + section[0].length)}`;
  return { xml: next, dropped };
}

/**
 * 游戏侧的真实状态（要活桥接）：`list_mods` 每条 mod 的 `enabled` / `loadedInSession` /
 * `unsatisfiedDependencies`，以及 `get_mod_configuration_status` 的 `restartRequired`。
 *
 * 没有实例时**不崩**：返回 `{ queried: false, reason }`，让上层把这几项标 `unknown` 并说明原因。
 * 两个工具分开 try：一个挂住只让那一项降级（它们都排在游戏主线程上，超时值复用 env-check 那个 8 s 上限
 * ——同一个「桥接调用别把 CLI 拖住」的约定，不另立一个数字）。
 */
async function collectGameModFacts(profile) {
  const probe = await probeBridge(profile);
  if (!probe.reachable) {
    return {
      queried: false,
      reason: `桥接不可达 —— ${probe.reason ?? '没有实例在跑'}（enabled / loadedInSession / unsatisfiedDependencies 是 unknown，不是 false）`,
      byId: new Map(),
      restartRequired: null,
      configStatus: null,
    };
  }
  try {
    const facts = await withBridge(
      profile,
      async (client) => {
        const errors = {};
        const call = async (name) => {
          try {
            return await client.callTool(name, {}, { timeoutMs: ENV_CHECK_CALL_TIMEOUT_MS });
          } catch (err) {
            errors[name] = err.message;
            return null;
          }
        };
        // 顺序给：先拿清单再拿配置状态；任何一条失败只丢它自己
        const listMods = await call('rimworld/list_mods');
        const configStatus = await call('rimworld/get_mod_configuration_status');
        return { listMods, configStatus, errors };
      },
      { requestTimeoutMs: ENV_CHECK_CALL_TIMEOUT_MS },
    );
    const byId = new Map();
    for (const m of Array.isArray(facts.listMods?.mods) ? facts.listMods.mods : []) {
      const key = String(m?.packageId ?? '').toLowerCase();
      if (key && !byId.has(key)) byId.set(key, m);
    }
    return {
      queried: byId.size > 0,
      reason: byId.size ? null : Object.values(facts.errors).join('；') || 'list_mods 没返回 mods 数组',
      byId,
      restartRequired: facts.listMods?.restartRequired ?? facts.configStatus?.restartRequired ?? null,
      configStatus: facts.configStatus,
    };
  } catch (err) {
    // 端口上有人答话但连接没成（归属校验不过 / 桥接被长调用挂住）：只降级，不整条崩
    return {
      queried: false,
      reason: `桥接连接失败：${err.message}`,
      byId: new Map(),
      restartRequired: null,
      configStatus: null,
    };
  }
}

/** env-check 每条桥接调用的上限：开测自检不该被一个挂住的调用拖住（bridge 把调用排在游戏主线程上） */
const ENV_CHECK_CALL_TIMEOUT_MS = 8000;

/**
 * `mods list` 的全部事实（人读表与 `--json` 用同一份对象，两处不各算一遍）。
 *
 * 三件事各自有来源，别混：
 *   底座 = `base-mods.json`（唯一真源）；被测 = `tested-mods.json`（本 profile 的数据文件）；
 *   「人工加入」= ModsConfig 里有、但两份清单都不认领的条目。
 * 游戏侧（enabled / loadedInSession / unsatisfiedDependencies）要活桥接，没有就标 unknown + 原因。
 */
async function collectModsList(profile) {
  const base = readBaseModsFile(WS);
  const tested = readTestedMods(profile);
  const active = readActiveModsInProfile(profile);
  const gameFacts = await collectGameModFacts(profile);
  const activeKeys = new Set((active.ids ?? []).map((id) => id.toLowerCase()));

  const baseRows = base.mods.map((m) => ({
    packageId: m.packageId,
    why: m.why,
    inConfig: activeKeys.has(m.packageId.toLowerCase()),
  }));

  const testedRows = tested.mods.map((m) => {
    const hit = locateInstalledMod(m.packageId);
    const game = gameFacts.byId.get(m.packageId.toLowerCase()) ?? null;
    return {
      packageId: m.packageId,
      // 名字优先级：清单里写的（人给的）> About.xml 里的 > 空
      name: m.name || hit.name || '',
      why: m.why,
      inConfig: activeKeys.has(m.packageId.toLowerCase()),
      installed: hit.installed,
      installedDir: hit.dir,
      installedNote: hit.reason,
      game:
        game === null
          ? null
          : {
              enabled: game.enabled ?? null,
              loadedInSession: game.loadedInSession ?? null,
              unsatisfiedDependencies: Array.isArray(game.unsatisfiedDependencies) ? game.unsatisfiedDependencies : [],
              hasOrderingIssues: game.hasOrderingIssues ?? null,
            },
    };
  });

  // 「人工加入」= ModsConfig 里有、却不在任何清单里（手改留下的）；只报告，绝不删
  const claimed = new Set([...base.packageIds, ...tested.mods.map((m) => m.packageId)].map((id) => id.toLowerCase()));
  const unmanaged = [];
  const seenUnmanaged = new Set();
  for (const id of active.ids ?? []) {
    const key = id.toLowerCase();
    if (claimed.has(key) || seenUnmanaged.has(key)) continue;
    seenUnmanaged.add(key);
    unmanaged.push(id);
  }

  // 顺序是否已经是「底座 → 被测 → 其余」：用同一处重排实现算一遍再比字符串（不另写一套判据）
  const orderNormalized =
    active.ids === null
      ? null
      : rewriteActiveMods(active.xml, { front: base.packageIds, then: tested.mods.map((m) => m.packageId) }) ===
        active.xml;

  const missingBase = baseRows.filter((r) => !r.inConfig).map((r) => r.packageId);
  const missingTested = testedRows.filter((r) => !r.inConfig).map((r) => r.packageId);
  const uninstalled = testedRows.filter((r) => r.installed === false).map((r) => r.packageId);

  // 漂移：每条都写成「照着做」的一句话，只报「不一致」等于没诊断
  const drift = [];
  if (!fs.existsSync(profileDir(profile))) {
    // profile 名字打错是最常见的「什么都查不到」；直接说清，别让人以为是 ModsConfig 坏了
    drift.push(
      `profiles\\ 下没有「${profile}」这个目录（名字打错了？）—— 现有 profile：` +
        `${listProfileNames().join(', ') || '（一个都没有）'}`,
    );
  }
  if (active.ids === null) {
    drift.push(
      `${active.file}：${active.reason}` +
        (active.missingFile
          ? ` —— 先 node src/cli.js start --profile ${profile} 一次让它生成`
          : ' —— 不猜位置，先把这一段写回来（游戏启动时自己会写）'),
    );
  }
  if (missingBase.length) {
    drift.push(
      `底座缺 ${missingBase.length} 个：${missingBase.join(', ')} ⇒ node src/cli.js mods apply --profile ${profile}` +
        '（start 之前也会自动补齐，只加不删）',
    );
  }
  if (missingTested.length) {
    drift.push(
      `被测 mod 不在 ModsConfig 里：${missingTested.join(', ')} ⇒ node src/cli.js mods apply --profile ${profile}`,
    );
  }
  if (orderNormalized === false && !missingBase.length && !missingTested.length) {
    drift.push(
      `顺序不是「底座 → 被测 → 其余」 ⇒ node src/cli.js mods apply --profile ${profile}` +
        '（同一批 packageId 只换位置）',
    );
  }
  if (unmanaged.length) {
    drift.push(
      `ModsConfig 里有 ${unmanaged.length} 条既不是底座也不是被测（人工加入，不在任何清单里）：` +
        `${unmanaged.join(', ')} —— mods 子命令不会动它们`,
    );
  }
  if (uninstalled.length) {
    drift.push(
      `本机没扫到 ${uninstalled.join(', ')}：可能还没订阅，或游戏装在别的路径 —— ` +
        'mods 不拦你，但起来之后它不会出现在 list_mods 里',
    );
  }
  if (gameFacts.queried) {
    for (const row of testedRows) {
      if (!row.game) {
        drift.push(`${row.packageId} 不在 list_mods 的返回里 —— 没装/没启用/没重启，先 mods apply 再重启实例`);
        continue;
      }
      if (row.game.enabled === false) {
        drift.push(
          `${row.packageId} 在游戏侧 enabled=false ⇒ node src/cli.js stop --profile ${profile} 后 start` +
            '（改了 ModsConfig 必须重启才生效）',
        );
      } else if (row.game.loadedInSession === false) {
        drift.push(`${row.packageId} enabled=true 但 loadedInSession=false：本次没加载成功，看 Player.log 的红字`);
      }
      if (row.game.unsatisfiedDependencies.length) {
        drift.push(`${row.packageId} 前置未满足：${row.game.unsatisfiedDependencies.join(', ')}`);
      }
    }
    if (gameFacts.restartRequired === true) {
      drift.push(
        `restartRequired=true：配置改动还没在运行中的实例里生效 ⇒ node src/cli.js stop --profile ${profile} 后 start`,
      );
    }
  } else {
    drift.push(`游戏侧状态未查（${gameFacts.reason}）`);
  }

  return {
    profile,
    testedModsFile: tested.file,
    testedModsMissing: Boolean(tested.missingFile),
    modsConfigFile: active.file,
    activeModsCount: active.count,
    modsConfigNote: active.reason ?? null,
    base: { file: base.file, missingFile: Boolean(base.missingFile), mods: baseRows, missing: missingBase },
    tested: testedRows,
    unmanaged,
    orderNormalized,
    game: {
      queried: gameFacts.queried,
      reason: gameFacts.reason,
      restartRequired: gameFacts.restartRequired,
      activeModCountFromGame: gameFacts.configStatus?.activeModCount ?? null,
    },
    drift,
  };
}

function printModsList(s) {
  console.log(`profile ${s.profile}`);
  console.log(
    `  被测 mod 清单 ${s.testedModsFile}` +
      `${s.testedModsMissing ? '（不存在 ⇒ 按空清单处理）' : `（${s.tested.length} 项）`}`,
  );
  console.log(
    `  ModsConfig    ${s.modsConfigFile}` +
      `${s.activeModsCount === null ? `（${s.modsConfigNote}）` : `（activeMods ${s.activeModsCount} 条）`}`,
  );
  console.log(
    `  游戏侧状态    ${
      s.game.queried ? `已查询（restartRequired=${knownOrUnknown(s.game.restartRequired)}）` : `unknown —— ${s.game.reason}`
    }`,
  );

  const width = Math.max(0, ...s.base.mods.map((m) => m.packageId.length));
  console.log('');
  console.log(`底座（base-mods.json，${s.base.mods.length} 项；√=已在 ModsConfig 里）`);
  if (s.base.missingFile) console.log(`  [警告] 没有 ${s.base.file}：底座清单缺失，start 会跳过自动补齐`);
  for (const m of s.base.mods) console.log(`  ${m.inConfig ? '√' : '✗'} ${m.packageId.padEnd(width)}  ${m.why}`);

  console.log('');
  console.log(`被测 mod（tested-mods.json，${s.tested.length} 项）`);
  if (!s.tested.length) {
    console.log(
      `  （空）加一个：node src/cli.js mods add --profile ${s.profile} --package-id <packageId> --name <显示名> --apply`,
    );
  }
  for (const m of s.tested) {
    console.log(`  ${m.inConfig ? '√' : '✗'} ${m.packageId}${m.name ? `（${m.name}）` : ''}${m.why ? `  why：${m.why}` : ''}`);
    const installedText =
      m.installed === true ? `√ 已装 ${m.installedDir}` : m.installed === false ? `✗ 本机没扫到` : 'unknown';
    console.log(`      installed=${m.installed}  ${installedText}`);
    if (m.game) {
      const deps = m.game.unsatisfiedDependencies.length ? m.game.unsatisfiedDependencies.join(', ') : '无';
      console.log(
        `      game：enabled=${knownOrUnknown(m.game.enabled)} loadedInSession=${knownOrUnknown(m.game.loadedInSession)} ` +
          `unsatisfiedDependencies=${deps}`,
      );
    } else {
      console.log(`      game：unknown（${s.game.queried ? 'list_mods 里没有它' : '桥接不可达'}）`);
    }
  }

  console.log('');
  console.log(`人工加入（ModsConfig 里有、不在任何清单里 —— mods 子命令不会动它们；共 ${s.unmanaged.length} 条）`);
  if (!s.unmanaged.length) console.log('  （没有）');
  for (const id of s.unmanaged) console.log(`  ${id}`);

  console.log('');
  console.log('漂移 / 下一步');
  if (!s.drift.length) console.log('  无（底座、被测、顺序都对得上）');
  for (const d of s.drift) console.log(`  - ${d}`);
}

/** `--apply` 之后统一的一句话（add / remove / apply 三条命令共用，免得三处各写一遍） */
function printApplyResult(r) {
  if (r.skipped) {
    console.log(`  ModsConfig 未改：${r.skipped}`);
    return;
  }
  if (!r.written) {
    console.log(`  ModsConfig 已是「底座 → 被测 → 其余」，没写文件（幂等）：${r.modsConfigPath}`);
  } else {
    const rest = Math.max(0, r.activeIds.length - r.baseCount - r.testedCount);
    console.log(
      `  ModsConfig 已重写：${r.modsConfigPath}\n` +
        `    activeMods ${r.activeIds.length} 条 = 底座 ${r.baseCount} + 被测 ${r.testedCount} + 其余 ${rest}`,
    );
    if (r.added.length) console.log(`    新补进：${r.added.join(', ')}`);
    if (r.removed.length) console.log(`    不再包含：${r.removed.join(', ')}`);
  }
  // dropped 只在「明确 remove --apply」时才不是 null；其余命令不该出现「没有可删的」这种话
  if (r.dropped) console.log(`    从 <activeMods> 删掉 ${r.dropped} 行（你明确 remove + --apply 才会走到这里）`);
  else if (r.dropped === 0) console.log('    <activeMods> 里本来就没有这一条（没有可删的）');
}

/** 三条命令共用的「下一步能照抄的命令」尾巴 */
function printModsNextSteps(profile, { applied }) {
  console.log('下一步（照抄）：');
  if (!applied) {
    console.log(`  node src/cli.js mods apply --profile ${profile}        # 排进 ModsConfig（底座 → 被测 → 其余）`);
  }
  console.log(`  node src/cli.js start --profile ${profile}`);
  console.log(`  node src/cli.js env-check --profile ${profile}`);
}

async function modsList({ flags, profile }) {
  const report = await collectModsList(profile);
  if (flags.json !== undefined) out(report);
  else printModsList(report);
  return { exitCode: 0 };
}

async function modsAdd({ flags, positional, profile }) {
  if (flags['package-id'] === undefined && positional[0]) {
    throw new Error(
      `packageId 用 --package-id 给（不是位置参数）：\n` +
        `  照抄：node src/cli.js mods add --profile ${profile} --package-id ${positional[0]}`,
    );
  }
  const packageId = assertPackageIdShape(flags['package-id']);
  const optedName = typeof flags.name === 'string' ? flags.name.trim() : '';
  const optedWhy = typeof flags.why === 'string' ? flags.why.trim() : '';
  // 先查「装没装」再写文件：警告要在结果那一行之前出现，才知道该不该继续往下走
  const located = locateInstalledMod(packageId);
  const existing = readTestedMods(profile);
  const at = existing.mods.findIndex((m) => m.packageId.toLowerCase() === packageId.toLowerCase());
  const previous = at >= 0 ? existing.mods[at] : null;
  const entry = {
    // 保留用户第一次给的原写法（比较是大小写不敏感的，但清单该长得像用户自己写的）
    packageId: previous?.packageId ?? packageId,
    // 幂等：这次没给的字段沿用已有的；都没给就借用 About.xml 里的显示名
    name: optedName || previous?.name || located.name || '',
    why: optedWhy || previous?.why || '',
  };
  const mods = existing.mods.slice();
  if (at >= 0) mods[at] = entry;
  else mods.push(entry);
  const profileExisted = fs.existsSync(profileDir(profile));
  const unchanged = at >= 0 && JSON.stringify(entry) === JSON.stringify(existing.mods[at]);
  const file = unchanged ? existing.file : writeTestedMods(profile, mods);

  console.log(
    `✓ ${unchanged ? '无变化（幂等，清单文件没动）' : at >= 0 ? '已更新' : '已登记'} ${file}：` +
      `${entry.packageId}${entry.name ? `（${entry.name}）` : ''} 第 ${mods.indexOf(entry) + 1} 项 / 共 ${mods.length} 项`,
  );
  if (entry.why) console.log(`  why：${entry.why}`);
  if (!profileExisted) {
    console.log(`[警告] profiles\\${profile}\\ 原来不存在，已随这次写入新建（start 时会补齐其余文件）`);
  }
  const base = readBaseModsFile(WS);
  if (base.packageIds.some((id) => id.toLowerCase() === entry.packageId.toLowerCase())) {
    console.log(
      `[警告]「${entry.packageId}」已经在底座清单（base-mods.json）里：它会按底座顺序排最前，塞进被测清单没有意义`,
    );
  }
  if (located.installed === true) {
    console.log(`  装没装：√ 本机有这个 mod（${located.dir}）`);
  } else if (located.installed === false) {
    console.log(
      `[警告] 装没装：本机没扫到「${entry.packageId}」，但**已登记**（不拦你 —— 可能还没订阅，或游戏装在别的路径）。\n` +
        `  依据：${located.reason}\n` +
        '  没装的话 start 之后 list_mods 里不会有它，先装/订阅再跑。',
    );
  } else {
    console.log(`[警告] 装没装：unknown —— ${located.reason}`);
  }

  let applied = null;
  if (flags.apply !== undefined) {
    applied = applyModsConfig(profile);
    printApplyResult(applied);
  } else {
    console.log('  没给 --apply：ModsConfig 没动，所以它现在还不会进游戏（add 只登记清单）。');
  }
  printModsNextSteps(profile, { applied });
  return { exitCode: 0 };
}

async function modsRemove({ flags, positional, profile }) {
  if (flags['package-id'] === undefined && positional[0]) {
    throw new Error(
      `packageId 用 --package-id 给（不是位置参数）：\n` +
        `  照抄：node src/cli.js mods remove --profile ${profile} --package-id ${positional[0]} --apply`,
    );
  }
  const packageId = assertPackageIdShape(flags['package-id']);
  const existing = readTestedMods(profile);
  const at = existing.mods.findIndex((m) => m.packageId.toLowerCase() === packageId.toLowerCase());
  if (at < 0) {
    console.log(`「${packageId}」本来就不在 ${existing.file} 里（幂等，清单文件没动）`);
    if (flags.apply !== undefined) {
      console.log(
        `[警告] 它不在被测清单里（很可能是你手加进 ModsConfig 的）：--apply 仍会把它从 <activeMods> 删掉 ——` +
          ' 这是本条命令唯一会删行的路径，删之前请确认你确实要它消失。',
      );
    }
  } else {
    const removed = existing.mods[at];
    const mods = existing.mods.filter((_, i) => i !== at);
    const file = writeTestedMods(profile, mods);
    console.log(`✓ 已从 ${file} 移除 ${removed.packageId}${removed.name ? `（${removed.name}）` : ''}（剩 ${mods.length} 项）`);
  }
  const base = readBaseModsFile(WS);
  if (base.packageIds.some((id) => id.toLowerCase() === packageId.toLowerCase())) {
    console.log(`[警告]「${packageId}」在底座清单（base-mods.json）里：下一次 start 还会把它补回来（底座只加不删）`);
  }

  let applied = null;
  if (flags.apply !== undefined) {
    applied = applyModsConfig(profile, { drop: packageId });
    printApplyResult(applied);
  } else {
    console.log('  没给 --apply：ModsConfig 没动 —— 它可能还在 <activeMods> 里，游戏照样会加载它。');
  }
  printModsNextSteps(profile, { applied });
  return { exitCode: 0 };
}

async function modsApply({ profile }) {
  const tested = readTestedMods(profile); // 坏 JSON 在这里就抛，不带着空清单去重排
  const r = applyModsConfig(profile);
  console.log(`✓ mods apply：profile ${profile}`);
  console.log(
    `  被测 mod 清单 ${tested.file}` +
      `${tested.missingFile ? '（不存在 ⇒ 只有底座会被排到前面）' : `（${tested.mods.length} 项）`}`,
  );
  printApplyResult(r);
  console.log('下一步（照抄）：');
  console.log(`  node src/cli.js start --profile ${profile}`);
  console.log(`  node src/cli.js env-check --profile ${profile}`);
  return { exitCode: 0 };
}

async function runMods({ sub, positional, flags, profile }) {
  if (sub === 'list') return modsList({ flags, profile });
  if (sub === 'add') return modsAdd({ flags, positional: positional.slice(1), profile });
  if (sub === 'remove') return modsRemove({ flags, positional: positional.slice(1), profile });
  if (sub === 'apply') return modsApply({ profile });
  // 没给子命令（或打错）：打用法并 exit 1 —— 不猜「你是想 list 还是 add」
  console.log(MODS_USAGE);
  return { exitCode: 1 };
}

/**
 * 一次 env-check 的全部事实。**只读**：不 start / 不 stop、不写文件、不推时间。
 *
 * 每一项失败都降级成 `queried:false` / `'unknown'` + 原因，整条命令不因某一项而崩 ——
 * 它的作用就是「把本次实测事实钉进 agent 自己的上下文」，崩掉就什么也钉不住。
 * 判据全部复用已有能力：进程身份与桥接归属来自 launcher（同 status），底座差额来自 base-mods.js，
 * 登记表统计与 atoms 共用 summarizeRegistry()。
 */
async function collectEnvCheck(profile) {
  const blockers = [];
  const profilePath = profileDir(profile);
  const profileExists = fs.existsSync(profilePath);
  const paths = {
    profileDir: profilePath,
    screenshotsDir: path.join(profilePath, 'Screenshots'),
    eventsFile: resolveEventFile(profile),
    recordPath: processRecordPath(profile),
    logFile: logPathFor(profile),
  };
  if (!profileExists) {
    blockers.push(
      `profiles\\ 下没有「${profile}」这个目录（名字打错了？）—— 可用 profile：${listProfileNames().join(', ') || '（一个都没有）'}`,
    );
  }

  // 进程 / 桥接：两条判据与 status 同源（进程身份 + 归属校验），不另写一套
  const st = await gameStatus(profile);
  const resolved = await resolveBridge(profile);
  const probe = await probeBridge(profile);
  // verifyInstance 的唯一判据就是命令行里的 -savedatafolder=…\profiles\<p>，
  // 所以 identityOk 与 cmdlineHasProfile 同源（分两个字段只是为了让读报告的人不必再解释一遍）
  const processFacts = {
    running: Boolean(st.recordedAlive || probe.reachable),
    runningState: st.state,
    pid: st.recordedPid,
    identityOk: Boolean(st.recordedAlive),
    cmdlineHasProfile: Boolean(st.recordedAlive),
    otherGamePids: st.otherGamePids,
    evidence: st.evidence,
  };
  const bridge = {
    reachable: Boolean(probe.reachable),
    available: Boolean(probe.available),
    host: probe.host ?? resolved.info?.host ?? null,
    port: probe.port ?? resolved.info?.port ?? null,
    source: probe.source ?? resolved.info?.source ?? null,
    resolutionOk: Boolean(resolved.ok),
    reason: resolved.ok
      ? probe.reachable
        ? resolved.reason
        : `归属校验通过，但握手失败：${probe.reason ?? '未知原因'}`
      : `归属校验未通过：${resolved.reason}`,
  };

  // mods：清单文件读（离线可用）+ 底座差额（base-mods.json 是唯一真源）
  const baseMods = readBaseModsFile(WS);
  const active = readActiveModsInProfile(profile);
  const diff = baseMods.missingFile
    ? { skipped: `没有 ${baseModsFile(WS)}` }
    : diffBaseMods({ profileDir: profilePath, baseMods: baseMods.packageIds });
  const mods = {
    activeModsCount: active.count,
    activeModsFile: active.file,
    baseModsFile: baseMods.file,
    baseModsRequired: baseMods.packageIds,
    baseModsMissing: diff.missing ?? [],
    baseModsNote: diff.skipped ?? null,
    loadedInSession: [],
  };

  // 桥接侧：一次连接问完；任何一个工具失败只让该项降级（记在 errors 里）
  let gameFacts = null;
  if (probe.reachable) {
    try {
      gameFacts = await withBridge(
        profile,
        async (client) => {
          const facts = { bridgeStatus: null, uiState: null, mods: null, toolCount: null, ceToolCount: null, errors: {} };
          const tryCall = async (key, name) => {
            try {
              return await client.callTool(name, {}, { timeoutMs: ENV_CHECK_CALL_TIMEOUT_MS });
            } catch (err) {
              facts.errors[key] = err.message;
              return null;
            }
          };
          facts.bridgeStatus = await tryCall('bridgeStatus', 'rimbridge/get_bridge_status');
          facts.uiState = await tryCall('uiState', 'rimworld/get_ui_state');
          facts.mods = await tryCall('mods', 'rimworld/list_mods');
          try {
            const tools = await client.listTools();
            facts.toolCount = tools.length;
            facts.ceToolCount = tools.filter((t) => String(t.name ?? '').startsWith('ce/')).length;
          } catch (err) {
            facts.errors.tools = err.message;
          }
          return facts;
        },
        { requestTimeoutMs: ENV_CHECK_CALL_TIMEOUT_MS },
      );
    } catch (err) {
      // 端口上有人答话但这次连接没成（归属校验不过 / 桥接被长调用挂住）：只降级桥接项，不整条崩
      gameFacts = { errors: { connect: err.message } };
    }
  }

  // 加载顺序：list_mods 一份载荷就能回答（restartRequired / 每条 mod 的 ordering 问题）
  const listMods = gameFacts?.mods ?? null;
  const loadOrder = {
    queried: Boolean(listMods),
    restartRequired: listMods?.restartRequired ?? null,
    sessionMismatchCount: listMods?.sessionMismatchCount ?? null,
    orderingIssues: [],
    reason: listMods
      ? null
      : (gameFacts?.errors?.mods ??
        gameFacts?.errors?.connect ??
        (probe.reachable ? '没有查询（见 bridge 项）' : '桥接不可达，未查询（原因见桥接项）')),
  };
  if (Array.isArray(listMods?.mods)) {
    for (const m of listMods.mods) {
      if (!m.enabled) continue;
      const issues = [];
      if (m.hasOrderingIssues) issues.push('hasOrderingIssues=true');
      if (Array.isArray(m.unsatisfiedDependencies) && m.unsatisfiedDependencies.length) {
        issues.push(`前置未满足：${m.unsatisfiedDependencies.join(', ')}`);
      }
      if (issues.length) loadOrder.orderingIssues.push(`${m.packageId ?? m.name ?? '?'}：${issues.join('；')}`);
    }
    mods.loadedInSession = listMods.mods
      .filter((m) => m.loadedInSession)
      .map((m) => m.packageId ?? m.name ?? '?');
    mods.activeModsCountFromGame = listMods.activeCount ?? null;
  }

  // 能力：清单文件决定「该有的有没有」，桥接决定「真的能不能调」
  const activeIds = new Set((active.ids ?? []).map((id) => id.toLowerCase()));
  const characterEditor = activeIds.has('void.charactereditor');
  const ceToolCount = gameFacts?.ceToolCount ?? null;
  const capabilities = {
    gameLinkEvents: activeIds.has('dsh.gamelink'),
    characterEditor,
    ceToolCount,
    toolCount: gameFacts?.toolCount ?? null,
    ceProbeNote: !characterEditor
      ? `底座缺 void.charactereditor：ce/* 工具不会存在（漏它的表现就是「工具凭空消失」）；跑 node src/cli.js start --profile ${profile} 会自动按 base-mods.json 补齐`
      : ceToolCount === null
        ? `本 profile 的 mod 清单里有 CharacterEditor；ce/* 能不能调要连桥接才知 —— 起了游戏后跑 node src/cli.js tools ce --profile ${profile}`
        : `ce/* 工具 ${ceToolCount} 个（本次实测清单，可复跑 node src/cli.js tools ce --profile ${profile}）`,
  };

  // 时间：强制暂停窗口会让 play_for 静默跑 0 tick，所以它是「推时间」的前置事实
  const status = gameFacts?.bridgeStatus ?? null;
  const ui = gameFacts?.uiState ?? null;
  /**
   * `timeSpeed` 的唯一真源是 `rimbridge/get_bridge_status` 的 `state.timeSpeed`。
   * 2026-10-25 实测核对过两次 `rimworld/get_ui_state` 载荷（各 25 个字段）：**里面没有 timeSpeed**，
   * 所以别往 ui 上找。桥接在「游戏没跑 / 还在主菜单或加载中」时给的就是 `null` —— 这里必须保持
   * `'unknown'`（不填 `Paused`/`Normal` 冒充），但要把**为什么**写出来：否则读报告的人只看到
   * `timeSpeed=unknown`，会误判成「代码忘了读这个字段」。
   */
  const timeSpeed = status?.state?.timeSpeed;
  const time = {
    windowsForcePause: knownOrUnknown(ui?.windowsForcePause),
    timeSpeed: knownOrUnknown(timeSpeed),
    timeSpeedWhy:
      timeSpeed === undefined || timeSpeed === null
        ? probe.reachable
          ? '桥接可达，但它给的 state.timeSpeed 是 null —— 游戏还在主菜单/加载中（此时 paused 也可能是 false）。进图后再看这一项。'
          : '桥接不可达，没查到时间速度（原因见桥接项）'
        : null,
    paused: knownOrUnknown(status?.state?.paused),
    policyMaxSpeed: 'unknown',
    policyMaxSpeedWhy:
      '速度上限是 AGENTS.md 硬约束 6 的约定，代码里没有机器可读的策略文件 —— 这里不替文档猜值',
    source: status || ui ? 'rimbridge/get_bridge_status + rimworld/get_ui_state' : '未连桥接（unknown）',
  };

  // 限制：tick/帧是引擎事实（实测 600 tick ↔ 600 帧）；tick/s 因帧率而异，env-check 不推时间去测
  const limits = {
    stepGameTicksTicksPerFrame: 1,
    measuredTicksPerSecond: null,
    measuredTicksPerSecondWhy: 'env-check 不推时间（那会改游戏状态）；要标定就自己跑 step_game_ticks 计时',
  };

  const registry = summarizeRegistry(loadRegistry());

  // 阻塞项：每条都要写成「人能照着做的一句话」，只写 false 等于没诊断
  if (baseMods.missingFile) {
    blockers.push(
      `没有 ${baseModsFile(WS)}：底座清单缺失，start 会跳过自动补齐 —— 先恢复这份文件（它是全项目唯一真源）。`,
    );
  }
  if (mods.baseModsMissing.length) {
    blockers.push(
      `底座缺 ${mods.baseModsMissing.length} 个：${mods.baseModsMissing.join(', ')} —— 跑 node src/cli.js start --profile ${profile}` +
        '（start 会按 base-mods.json 自动补齐，只加不删）。',
    );
  }
  if (active.count === null && profileExists) {
    blockers.push(`读不到 ${active.file}（${active.reason}）—— 先 node src/cli.js start --profile ${profile} 一次让它生成。`);
  }
  if (!processFacts.running) {
    blockers.push(
      `profile「${profile}」没有确认在运行的实例：要开测先跑 node src/cli.js start --profile ${profile}` +
        '（会真起游戏、抢一次窗口焦点；先确认没在玩你自己那一局）。',
    );
    if (bridge.reachable) {
      blockers.push(
        `${bridge.host}:${bridge.port} 上有人答话，但本 profile 没有可信记录 —— 那是别的实例，别连它：` +
          `先 node src/cli.js status --profile ${profile} 看清。`,
      );
    }
  } else if (!bridge.resolutionOk) {
    blockers.push(
      `桥接归属校验未通过：${resolved.reason} —— 先 node src/cli.js status --profile ${profile} 看清判据；别拿别的 profile 的端口硬连。`,
    );
  } else if (!bridge.reachable) {
    blockers.push(
      `桥接不可达 ${bridge.host}:${bridge.port}（${probe.reason ?? '握手失败'}）：游戏可能还没到写日志那一步 —— ` +
        `用 node src/cli.js live --profile ${profile} --until 'kind=colony_started' 等事件，别用固定 sleep。`,
    );
  }
  if (probe.reachable && !loadOrder.queried) {
    blockers.push(
      `加载顺序查不到（${loadOrder.reason ?? '未知原因'}）—— 单独读一次：node src/cli.js call rimworld/list_mods --profile ${profile}`,
    );
  }
  if (loadOrder.restartRequired) {
    blockers.push(
      'mod 配置改动还没在运行中的实例里生效（restartRequired=true）：先 node src/cli.js stop --profile ' +
        `${profile} 再 start --profile ${profile}。`,
    );
  }
  if (mods.activeModsCountFromGame !== undefined && active.count !== null && mods.activeModsCountFromGame !== active.count) {
    blockers.push(
      `运行中的实例激活了 ${mods.activeModsCountFromGame} 个 mod，而 ModsConfig.xml 里是 ${active.count} 个 —— ` +
        '配置在启动后被改过，重启（stop 再 start）才会生效。',
    );
  }
  if (loadOrder.orderingIssues.length) {
    blockers.push(
      `有 ${loadOrder.orderingIssues.length} 个 mod 的报告加载顺序问题（前 3 条：${loadOrder.orderingIssues.slice(0, 3).join(' | ')}）—— ` +
        `完整的看 node src/cli.js call rimworld/list_mods --profile ${profile} 的 hasOrderingIssues/unsatisfiedDependencies。`,
    );
  }
  if (time.windowsForcePause === true) {
    blockers.push(
      '有窗口在强制暂停（windowsForcePause=true）：play_for 会静默跑 0 tick —— 先 node src/cli.js call foodprobe/close_force_pause --profile ' +
        `${profile}，回读 windowsForcePause=false 再推时间。`,
    );
  }
  if (registry.errors.length) {
    blockers.push(
      `条件登记表有 ${registry.errors.length} 处错误：node src/cli.js wait --list 看明细（文件 ${registry.file}）。`,
    );
  }

  return {
    profile,
    generatedAt: new Date().toISOString(),
    verdict: { ok: blockers.length === 0, blockers },
    process: processFacts,
    bridge,
    mods,
    loadOrder,
    capabilities,
    time,
    paths,
    limits,
    registry,
  };
}

/** 中文标签在终端里占两列：按显示宽度补空格，几行摘要才对得齐 */
function padLabel(label, width) {
  const cols = [...label].reduce((n, ch) => n + (/[\u3000-\u9fff\uff00-\uffef]/.test(ch) ? 2 : 1), 0);
  return label + ' '.repeat(Math.max(0, width - cols));
}

/** env-check 默认输出：先结论，再几行事实，最后是可照做的阻塞项 */
function printEnvCheck(report) {
  const { verdict } = report;
  const shown = (value) => (value === null || value === undefined ? '-' : String(value));
  const row = (label, text) => console.log(`  ${padLabel(label, 9)}${text}`);
  console.log(`env-check profile=${report.profile}  ${report.generatedAt}`);
  console.log(
    `  结论     ${verdict.ok ? '✓ 没有阻塞项' : `✗ ${verdict.blockers.length} 个阻塞项（退出码 1，不是崩溃）`}`,
  );
  row(
    '进程',
    `running=${shown(report.process.running)} pid=${shown(report.process.pid)} ` +
      `identityOk=${shown(report.process.identityOk)} cmdlineHasProfile=${shown(report.process.cmdlineHasProfile)} ` +
      `runningState=${shown(report.process.runningState)}`,
  );
  row(
    '桥接',
    `reachable=${shown(report.bridge.reachable)} resolutionOk=${shown(report.bridge.resolutionOk)} ` +
      `${shown(report.bridge.host)}:${shown(report.bridge.port)} source=${shown(report.bridge.source)}`,
  );
  if (report.bridge.reason) row('', `原因：${report.bridge.reason}`);
  row(
    'mods',
    `activeModsCount=${shown(report.mods.activeModsCount)} 底座 ${report.mods.baseModsRequired.length} 个、` +
      `缺 ${report.mods.baseModsMissing.length} 个` +
      `${report.mods.baseModsNote ? `（${report.mods.baseModsNote}）` : ''} ` +
      `本次已加载=${report.loadOrder.queried ? `${report.mods.loadedInSession.length} 个` : 'unknown（未连桥接）'}`,
  );
  row(
    '加载顺序',
    `queried=${shown(report.loadOrder.queried)} restartRequired=${shown(report.loadOrder.restartRequired)} ` +
      `问题 ${report.loadOrder.orderingIssues.length} 条`,
  );
  if (report.loadOrder.reason) row('', `原因：${report.loadOrder.reason}`);
  row(
    '能力',
    `GameLink 事件=${shown(report.capabilities.gameLinkEvents)} CharacterEditor=${shown(report.capabilities.characterEditor)} ` +
      `ce/* 工具=${shown(report.capabilities.ceToolCount)} 工具总数=${shown(report.capabilities.toolCount)}`,
  );
  row(
    '时间',
    `windowsForcePause=${shown(report.time.windowsForcePause)} timeSpeed=${shown(report.time.timeSpeed)} ` +
      `paused=${shown(report.time.paused)} 速度上限=${shown(report.time.policyMaxSpeed)}（AGENTS.md 硬约束 6）`,
  );
  if (report.time.timeSpeedWhy) row('', `原因：${report.time.timeSpeedWhy}`);
  row('登记表', `${report.registry.scenarios} 个场景 ${JSON.stringify(report.registry.byLevel)}，错误 ${report.registry.errors.length} 处`);
  row('路径', `profile=${report.paths.profileDir}`);
  row('', `截图=${report.paths.screenshotsDir}`);
  row('', `事件=${report.paths.eventsFile}`);
  row('', `记录=${report.paths.recordPath}`);
  row('限制', `step_game_ticks=${report.limits.stepGameTicksTicksPerFrame} tick/帧；tick/s=${report.limits.measuredTicksPerSecond === null ? '未测（不推时间）' : report.limits.measuredTicksPerSecond}`);
  if (verdict.blockers.length) {
    console.log('  阻塞项（照着做就能清掉）：');
    for (const b of verdict.blockers) console.log(`    - ${b}`);
  } else {
    console.log('  写测试时按上表这些数，不要按文档里写死的旧数字（机器一更新它们就过期）。');
  }
}

async function main() {
  const [cmd, ...rest] = process.argv.slice(2);
  const { positional, flags } = parseArgs(rest);

  /**
   * `--out` **在一切输出之前**接管（连 `--help` 的输出也照存）：证据要的是「这条命令到底吐了什么」，
   * 开始晚一行就少一行。装完之后从 flags 里删掉 —— 它对子命令没有意义，
   * 留着只会在某天被人当成子命令自己的参数读走。
   */
  if (typeof flags.out === 'string') {
    installOutputTee(flags.out, process.argv.slice(2));
    delete flags.out;
  }

  /**
   * `--help` 必须在任何动作之前拦下来：以前全文件没有 help 处理，`start --help` 直接把
   * 「查参数」当成了「起游戏」（561 mod 满配，2026-10-24 事故）。这里统一拦，一律不改任何状态。
   */
  if (flags.help !== undefined || cmd === '--help' || cmd === '-h' || cmd === 'help' || positional[0] === '-h') {
    console.log(helpTextFor(cmd, positional));
    return;
  }

  // 会真起/真关游戏的命令不接受「猜一个 profile」——默认值 vanilla 正是那次事故的放大器
  requireExplicitProfile(cmd, flags, positional);

  const profile = flags.profile || 'vanilla';

  switch (cmd) {
    case 'atoms': {
      /**
       * 原子能力清单（机器可读）——让**没有上下文的代理**一次拿到「能怎么判、能等什么」，
       * 不用读文档也不用猜字段名。这是「原子库应当可检索」的落点：清单由代码导出，
       * 不会与实现漂移（算子从 condition.js 读，来源从 wait-sources.js 读）。
       */
      const registry = loadRegistry();
      out({
        operators: {
          leaf: LEAF_OPERATORS,
          combinators: COMBINATORS,
          notes: OPERATOR_NOTES,
        },
        sources: SOURCE_SPECS,
        registry: summarizeRegistry(registry),
        gameTools: '游戏内可用工具随 mod 变：node src/cli.js tools [关键词]；单个参数：describe <工具名>',
        docs: {
          usage: '.dsh/skills/rimworld-auto-test-closed-loop/docs/cli-wait-用法-20261024.md',
          // 推时间的口径（速率表 / 预算公式 / 时间↔tick）与 advance 的边界都在这份 SOP 里
          advance: '.dsh/skills/rimworld-auto-test-closed-loop/docs/测试SOP/推时间.md',
          compose: 'docs/组合skill-草案-20261024.md',
          flow: '.dsh/skills/rimworld-auto-test-closed-loop/docs/自动化测试-流程与原子能力清单-20261024.md',
        },
      });
      break;
    }

    case 'start': {
      const rec = await startGame({
        profile,
        // 不传就让 launcher 在 5174..5183 里挑第一个空闲端口（多实例各拿一个）
        port: flags.port ? Number(flags.port) : undefined,
        visible: !flags.minimized,
        width: flags.width ? Number(flags.width) : undefined,
        height: flags.height ? Number(flags.height) : undefined,
        fullscreen: flags.fullscreen ? true : flags.windowed ? false : undefined,
        monitor: flags.monitor ? Number(flags.monitor) : undefined,
        doorstop: Boolean(flags.doorstop),
      });
      console.log(`游戏已启动 PID=${rec.pid}  profile=${profile}`);
      console.log(`  安装目录: ${gameRoot()}`);
      console.log(`  存档目录: ${rec.profileDir}   (独立，不影响主力存档)`);
      console.log(`  日志:     ${rec.logFile}`);
      console.log(`  记录:     ${rec.recordPath}`);
      console.log(`  端口:     ${rec.bridge.port}（${rec.portSource}）`);
      console.log(
        `  窗口:     ${rec.window.fullscreen ? '全屏' : '窗口化'} ` +
          `${rec.window.width}x${rec.window.height}  ` +
          `显示器=${rec.window.monitor ?? '默认'}   (改 profiles/${profile}/prefs.json 可调)`,
      );
      process.stdout.write('等待游戏内桥接就绪');
      const info = await waitForBridge({ profile, onTick: () => process.stdout.write('.') });
      console.log('');
      console.log(`桥接就绪：127.0.0.1:${info.port}  (等待 ${(info.waitedMs / 1000).toFixed(1)}s，来源=${info.source ?? 'log'})`);
      out({ bridge: { host: info.host, port: info.port }, tokenPreview: info.token.slice(0, 8) + '…' });
      break;
    }

    case 'mods': {
      /**
       * 被测 mod 的清单入口：list / add / remove / apply。
       *
       * 这一条命令替掉「手改 profiles\<p>\Config\ModsConfig.xml」——手改没有校验（写坏一个 `<` 就少几条 mod）、
       * 不幂等（重复加会多一行）、也不告诉你「这个 packageId 本机装没装」。顺序仍由 base-mods.js 的
       * `rewriteActiveMods` 一处决定（与 start 前补底座同一份实现）。
       * 只有带 `--apply` 才碰 ModsConfig.xml；`list` 与不带 --apply 的 add/remove 只读或只改清单数据文件。
       */
      const result = await runMods({ sub: positional[0], positional, flags, profile });
      process.exitCode = result.exitCode ?? 0;
      break;
    }

    case 'base-mods': {
      /**
       * 只读自检入口：`base-mods.json` 是全项目唯一一份底座 mod 清单，
       * 这里把它和某个 profile 的实际 `ModsConfig.xml` 对一下账 —— **只报告不写文件**
       * （写文件是 `start` 的事，见 src/launcher.js 的 ensureBaseModsBeforeStart）。
       */
      const data = readBaseModsFile(WS);
      if (data.missingFile) {
        console.error(`没有 ${baseModsFile(WS)}：底座 mod 清单缺失，start 时会跳过自动补齐`);
        process.exitCode = 1;
        break;
      }
      console.log(`底座 mod（唯一真源）${data.file}：${data.packageIds.length} 个`);
      if (data.description) console.log(`  ${data.description}`);
      const width = Math.max(...data.mods.map((m) => m.packageId.length), 0);
      for (const m of data.mods) {
        console.log(`  ${m.packageId.padEnd(width)}  ${m.why}`);
      }
      console.log('  数组顺序就是补进 <activeMods> 的顺序（Harmony 必须最靠前）；只加不删。');

      // 不带 --profile 就只看清单本身；带上了才去对 profile 的账（读文件，写由 start 负责）
      if (flags.profile !== undefined) {
        const diff = diffBaseMods({ profileDir: profileDir(profile), baseMods: data.packageIds });
        if (diff.skipped) {
          console.log(`profile ${profile}：跳过（${diff.skipped}）`);
        } else if (!diff.missing.length) {
          console.log(`profile ${profile}：缺 0 个（${data.packageIds.length} 个都在 ${diff.modsConfigPath}）`);
        } else {
          console.log(
            `profile ${profile}：缺 ${diff.missing.length} 个 → ${diff.missing.join(', ')}` +
              `（只报告，不写文件；start 时会自动补）`,
          );
        }
      }
      break;
    }

    case 'env-check': {
      /**
       * 每次开测先跑：把「本次实测环境事实」钉进 agent 自己的上下文，
       * 替掉文档里写死的环境数字（机器一更新，写死的「事实」就成了最大的坑）。
       * 只读、**绝不启动游戏**；桥接不通只降级相关项，不整条崩。
       */
      const report = await collectEnvCheck(profile);
      if (flags.json !== undefined) out(report);
      else printEnvCheck(report);
      process.exitCode = report.verdict.ok ? 0 : 1;
      break;
    }

    case 'status': {
      /**
       * 2026-10-13 修复：这里以前会给出**假阴性**。
       *
       * 旧代码：`const procs = await listGameProcesses(); ... gameRunning: procs.length > 0`。
       * 两个错叠在一起：
       *   1) `listGameProcesses()` 返回的是 `{known, processes, reason}` **对象**，不是数组
       *      （见 `data/auto-bisect.mjs:122` 的同一条备注），`obj.length` 是 `undefined`，
       *      `undefined > 0` 恒为 `false` —— 所以 `gameRunning` **任何时候都是 false**，
       *      连"枚举成功且真的找到了进程"也一样；
       *   2) 沙箱下 tasklist 被拒 → `known:false`，这本来只是"查不到"，却被第 1) 条
       *      掩盖成一个看起来确定的 `false`。有人据此写过"游戏未运行"的结论（实际在跑），
       *      代价是一次无谓的游戏重启。
       *
       * 现在：`gameRunning` 是**三态** `true | false | null`（null = 判据不足），
       * 并额外给出 `runningState`（'running'|'not-running'|'unknown'）与 `runningEvidence`
       * （逐条判据），让调用方必须自己面对"不知道"这个答案。
       * 正向证据优先取**不依赖 tasklist** 的两条：记录的 PID 存活、桥接握手可达。
       */
      const st = await gameStatus(profile);
      const rec = await readProcessRecord(profile);
      // ② 这里必须用**权威**解析（带 PID 身份校验），不能用 readBridgeInfo 的解析层猜测：
      // 竞态现场实测过「voiced 真实端口 5174，解析出 5175（别人的记录）」。
      const resolved = await resolveBridge(profile);
      const info = resolved.info;
      const bridge = await probeBridge(profile); // 有界超时（默认 1.5s）
      const evidence = [...st.evidence];
      if (info || resolved.evidence.length) {
        evidence.push(
          resolved.ok
            ? `桥接归属校验通过：${resolved.reason}`
            : `桥接归属校验未通过：${resolved.reason}`,
        );
        for (const line of resolved.evidence) evidence.push(`  · ${line}`);
      }
      if (bridge.reachable) {
        evidence.push(
          `桥接可达 ${bridge.host}:${bridge.port}（GABP session/hello 握手成功，` +
            `${bridge.app?.name ?? '?'} ${bridge.app?.version ?? ''}）—— 桥接跑在游戏进程内部，这是独立的"游戏活着"证据`,
        );
      } else if (bridge.available) {
        evidence.push(`桥接不可达 ${bridge.host}:${bridge.port}（${bridge.reason}）—— 弱否定证据，不足以单独结论"没在跑"`);
      } else {
        evidence.push(`桥接未知：${bridge.reason}`);
      }

      // 判定：任一条正向证据成立 → true。
      // 2026-10-17 多实例：`st.state` 只看本 profile 的记录（其它 RimWorld 进程不算），
      // 所以「记录里的 PID 不存活」就能说 false；桥接可达仍是最强的反向补证。
      let gameRunning;
      let runningState;
      if (st.state === 'running' || bridge.reachable) {
        gameRunning = true;
        runningState = 'running';
      } else if (st.state === 'not-running') {
        gameRunning = false;
        runningState = 'not-running';
      } else {
        gameRunning = null;
        runningState = 'unknown';
      }

      if (runningState === 'unknown') {
        process.stderr.write(
          '[警告] 无法判断游戏是否在运行（判据不足）—— gameRunning=null。\n' +
            '       不要把 null 当成 false；下面是逐条判据：\n' +
            evidence.map((e) => `       - ${e}`).join('\n') +
            '\n',
        );
      }

      out({
        profile,
        gameRunning,
        runningState,
        runningEvidence: evidence,
        // 兼容字段：旧调用方读的 `processes` 原样保留（它就是枚举结果对象本身，
        // 名字有歧义，但改结构会把读 `processes.processes` 的人弄坏）。
        processes: st.enumeration,
        processList: st.enumeration.processes,
        // 2026-10-17：其它 RimWorld 进程（可能是用户自己在玩的那一局）只报不判、不杀
        otherGamePids: st.otherGamePids,
        recordedPid: st.recordedPid,
        recordedAlive: st.recordedAlive,
        bridge,
        lastLaunch: rec,
        bridgeInLog: info ? { host: info.host, port: info.port, source: info.source } : null,
        // ② 给调用方一个可机器判的结论：这个桥接到底是不是本 profile 的
        bridgeResolution: {
          ok: resolved.ok,
          reason: resolved.reason,
          evidence: resolved.evidence,
          port: info?.port ?? null,
          recordedPort: rec?.bridge?.port ?? null,
        },
      });
      break;
    }

    case 'bridge': {
      // 与 status 同源：给结论（这份桥接凭什么属于我）+ 解析结果
      const resolved = await resolveBridge(profile);
      out(resolved.info ? { ...resolved.info, ok: resolved.ok, reason: resolved.reason, evidence: resolved.evidence } : {
        error: '没有可用的桥接信息',
        ok: false,
        reason: resolved.reason,
        evidence: resolved.evidence,
      });
      break;
    }

    case 'tools': {
      const kw = (positional[0] || '').toLowerCase();
      await withBridge(profile, async (client, welcome) => {
        const tools = await client.listTools();
        const filtered = kw
          ? tools.filter((t) =>
              `${t.name} ${t.description || ''}`.toLowerCase().includes(kw),
            )
          : tools;
        /**
         * 匹配集合不变（还是「名字+描述」子串），只是**分组显示**：
         * `tools ce` 曾一次匹配 63 个，而真正的 `ce/*` 只有 9 个 —— 混在一行里读的人会以为
         * CE 有 63 个工具，然后按错的计数写结论。名字命中才是「这个关键词的工具」。
         */
        const nameHits = kw ? filtered.filter((t) => String(t.name ?? '').toLowerCase().includes(kw)) : [];
        const descOnly = kw ? filtered.filter((t) => !String(t.name ?? '').toLowerCase().includes(kw)) : [];
        const printTool = (t) => {
          const desc = (t.description || '').replace(/\s+/g, ' ').slice(0, 90);
          console.log(`  ${t.name}${desc ? '  — ' + desc : ''}`);
        };
        console.log(`游戏: ${welcome?.app?.name} ${welcome?.app?.version}  agentId=${welcome?.agentId}`);
        console.log(`能力方法: ${(welcome?.capabilities?.methods || []).join(', ')}`);
        console.log(
          `工具总数: ${tools.length}` +
            (kw
              ? `（匹配 "${kw}" 的 ${filtered.length} 个 = 名字命中 ${nameHits.length} + 仅描述命中 ${descOnly.length}）`
              : '') +
            '\n',
        );
        if (!kw) {
          for (const t of filtered) printTool(t);
        } else if (!filtered.length) {
          console.log(`  （没有任何工具的名字或描述里含 "${kw}"）`);
        } else {
          if (nameHits.length) {
            console.log(`—— 名字命中（${nameHits.length}），这些才是带 "${kw}" 的工具 ——`);
            for (const t of nameHits) printTool(t);
          }
          if (descOnly.length) {
            console.log(`${nameHits.length ? '\n' : ''}—— 仅描述命中（${descOnly.length}），名字里没有 "${kw}"，通常不是你要的 ——`);
            for (const t of descOnly) printTool(t);
          }
        }
      });
      break;
    }

    case 'dump-tools': {
      await withBridge(profile, async (client, welcome) => {
        const tools = await client.listTools();
        const file = path.join(WS, 'runs', 'tools.json');
        fs.mkdirSync(path.dirname(file), { recursive: true });
        fs.writeFileSync(
          file,
          JSON.stringify({ welcome, tools }, null, 2),
          'utf8',
        );
        console.log(`已导出 ${tools.length} 个工具的完整 schema → ${file}`);
      });
      break;
    }

    case 'describe': {
      const name = positional[0];
      if (!name) throw new Error('用法: node src/cli.js describe <工具名>');
      await withBridge(profile, async (client) => {
        const tools = await client.listTools();
        const t = tools.find((x) => x.name === name);
        if (!t) {
          console.log(`未找到工具 ${name}`);
          process.exitCode = 1;
          return;
        }
        out(t);
      });
      break;
    }

    case 'call': {
      const name = positional[0];
      if (!name) throw new Error('用法: node src/cli.js call <工具名> [\'{"参数":...}\'] [--args-file f.json]');
      // 从文件读参数：绕开 PowerShell/bash 对 JSON 双引号的吞引号问题
      const args =
        readJsonInput({ inline: flags.args, file: flags['args-file'], positional: positional[1], flag: '--args' }) ?? {};
      // 默认超时与 gabp.js 同一个常量（60 s）：以前这里写死 300 s，一次挂住白等 5 分钟，
      // 而复用同一桥接的后续调用会跟着挂住（桥接把调用排在游戏主线程上）。长操作显式传 --timeout。
      // 连接与单次调用共用这一个值，所以必须在 withBridge 之前算好。
      const callTimeoutMs = flags.timeout === undefined ? DEFAULT_REQUEST_TIMEOUT_MS : Number(flags.timeout);
      if (!Number.isFinite(callTimeoutMs) || callTimeoutMs <= 0) {
        throw new Error(`--timeout 必须是正数（毫秒），收到「${flags.timeout}」`);
      }
      await withBridge(profile, async (client) => {
        // 2026-10-13：先校验工具名再调用，避免「调用不存在的工具名 + 吞掉错误」的静默失效。
        await assertToolNameExists(client, name);
        const res = await client.callTool(name, args, { timeoutMs: callTimeoutMs });
        out(res);
        /**
         * 动作失败 ≠ 命令失败：载荷照样全打（诊断要看 operation/message），但退出码必须非 0。
         * 不改的话「success:false + exit 0」会被照着写成假通过（2026-10-25 试跑缺陷 A2）。
         */
        if (res && res.success === false) {
          const detail = toolFailureText(res);
          console.error(
            `[call] 「${name}」返回 success:false${detail ? `：${detail}` : '（载荷里没有 message/exception）'}；` +
              '退出码 1 —— 命令跑通了但动作失败了，别按 exit 0 写成通过。',
          );
          process.exitCode = 1;
        }
      }, { requestTimeoutMs: callTimeoutMs });
      break;
    }

    case 'wait': {
      /**
       * 等「探针采样满足条件」，而不是睡固定时长（设计稿 §3.2.5 的替代做法）。
       * 退出码与 live 同一套：0=命中 / 2=超时 / 1=参数错、工具名不存在、桥接未就绪等。
       * 判定只在 Node 侧做一套（src/wait.js），条件 JSON 与游戏内 rb.poll 的算子同构，
       * 以后可以原样下发；本轮只做 --source bridge。
       */
      // ② 登记表：--list 不连桥接、也不需要场景名
      const registry = loadRegistry();
      if (flags.list) {
        if (registry.errors.length) {
          console.error(`[wait] 登记表有 ${registry.errors.length} 处错误（下面仍列出可用场景）：`);
          for (const e of registry.errors) console.error(`  - ${e}`);
        }
        const rows = listScenarios(registry);
        console.log(`[wait] 条件登记表：${CONDITIONS_FILE}（${rows.length} 个场景）`);
        const mark = {
          'wait-verified': '✔ 真机 wait 验过',
          'field-verified': '～ 字段真机读过、条件没跑',
          'doc-example': '？ 文档示例',
          inferred: '× 推断',
        };
        for (const r of rows) {
          console.log(`  ${r.name}  [${r.source}] ${mark[r.verifiedLevel] ?? r.verifiedLevel}  — ${r.description}`);
        }
        break;
      }
      if (registry.errors.length && flags.scenario !== undefined) {
        throw new Error(
          `登记表有错，先修数据再按场景等：\n${registry.errors.map((e) => `  - ${e}`).join('\n')}\n  文件：${CONDITIONS_FILE}`,
        );
      }

      /**
       * ③ 场景 → (source, probe, args, condition, timeout)。
       * 场景身份（source / probe）**不许**被命令行覆盖 —— 否则就会出现「拿 A 的条件打 B 的探针」，
       * 那正是「字段名靠猜」的老毛病换了个入口。调参类（--timeout/--interval）与占位参数允许覆盖。
       */
      let scenario = null;
      if (flags.scenario !== undefined) {
        if (flags.scenario === true) throw new Error(`--scenario 需要一个场景名（用 --list 看有哪些）\n\n${WAIT_USAGE}`);
        const found = resolveScenario(registry, String(flags.scenario));
        if (!found.ok) throw new Error(`${found.reason}\n\n用 \`node src/cli.js wait --list\` 看全部场景。`);
        scenario = found.scenario;
        if (flags.probe !== undefined) {
          throw new Error(`--scenario 已经含探针（${scenario.probe ?? scenario.source}），不要再给 --probe`);
        }
        if (!scenario.verified) {
          console.error(`[wait] 场景「${scenario.name}」未经真机 wait 验证（级别 ${scenario.verifiedLevel}）：${scenario.evidence}`);
        }
      }

      const source = flags.source !== undefined ? String(flags.source) : (scenario?.source ?? 'bridge');
      if (scenario && source !== scenario.source) {
        throw new Error(`--source ${source} 与场景「${scenario.name}」的 source=${scenario.source} 冲突（场景身份不能覆盖）`);
      }
      if (!['bridge', 'event', 'process', 'bridge-ready'].includes(source)) {
        throw new Error(`--source 只认 bridge / event / process / bridge-ready，收到「${source}」\n\n${WAIT_USAGE}`);
      }

      // 四类来源各自的「指向谁」：bridge 要探针名，process 要 PID，event 要文件，bridge-ready 什么都不用
      let probeName = null;
      let pid = null;
      let eventFile = null;
      let eventFormat = 'events';
      if (source === 'bridge') {
        if (flags.probe === true) throw new Error(`--probe 需要一个工具名，例 rimworld/list_colonists\n\n${WAIT_USAGE}`);
        probeName = scenario ? scenario.probe : flags.probe === undefined ? positional[0] : String(flags.probe);
        if (!probeName) throw new Error(`缺少 --probe（要反复调用的只读工具名），或用 --scenario <名字>\n\n${WAIT_USAGE}`);
      } else if (source === 'process') {
        // 不给 --pid 就盯本 profile 记录里的那个 PID（`stop` 之后等它真的没了，就是这条）
        const recordPid = flags.pid === undefined ? readProcessRecord(profile)?.pid : Number(flags.pid);
        pid = Number(recordPid);
        if (!Number.isInteger(pid) || pid <= 0) {
          throw new Error(`--source process 需要 --pid <正整数>，或该 profile 有启动记录\n\n${WAIT_USAGE}`);
        }
      } else if (source === 'event') {
        eventFile = flags.file === undefined ? resolveEventFile(profile) : path.resolve(String(flags.file));
        // 事件文件有两种格式：DSH_GameLink 的 NDJSON（默认），以及整行当 `text` 的纯文本（Player.log）
        const format = flags['event-format'] === undefined ? 'events' : String(flags['event-format']);
        if (format !== 'events' && format !== 'log') {
          throw new Error(`--event-format 只认 events|log，收到「${format}」\n\n${WAIT_USAGE}`);
        }
        eventFormat = format;
      }

      // 参数：场景自带 args（`$名字` 占位由 --arg 填）；没有场景时整份来自 --args/--args-file
      const providedArgs = readJsonInput({
        inline: flags.args,
        file: flags['args-file'],
        positional: positional[1],
        flag: '--args',
      });
      const argPairs = parseArgPairs(flags.arg);
      let args = providedArgs ?? {};
      if (scenario) {
        const filled = fillArgs(scenario.args, argPairs);
        if (!filled.ok) {
          throw new Error(
            `场景「${scenario.name}」需要这些参数：${filled.missing.map((k) => `--arg ${k}=…`).join(' ')}`,
          );
        }
        if (filled.unused.length) {
          console.error(`[wait] 这些 --arg 用不上（场景里没有对应占位）：${filled.unused.join(', ')}`);
        }
        args = { ...filled.args, ...(providedArgs ?? {}) };
      } else if (Object.keys(argPairs).length) {
        throw new Error('--arg 只能配合 --scenario 用：没有登记表条目时，占位符无从对应');
      }

      const condition = scenario
        ? scenario.condition
        : readJsonInput({
            inline: flags.condition,
            file: flags['condition-file'],
            positional: undefined,
            flag: '--condition',
          });
      if (condition === undefined) {
        throw new Error(
          `缺少 --condition（条件 JSON，例 '{"path":"result.colonists","countEquals":3}'）或用 --scenario\n\n${WAIT_USAGE}`,
        );
      }
      if (condition === null || typeof condition !== 'object' || Array.isArray(condition)) {
        throw new Error(`--condition 必须是一个 JSON 对象，收到：${JSON.stringify(condition).slice(0, 200)}\n\n${WAIT_USAGE}`);
      }

      const timeoutMs = flags.timeout === undefined ? (scenario?.timeoutMs ?? 60_000) : Number(flags.timeout);
      if (!Number.isFinite(timeoutMs) || timeoutMs <= 0) {
        throw new Error(`--timeout 必须是正数（毫秒），收到「${flags.timeout}」\n\n${WAIT_USAGE}`);
      }
      const intervalMs = flags.interval === undefined ? 200 : Number(flags.interval);
      if (!Number.isFinite(intervalMs) || intervalMs <= 0) {
        throw new Error(`--interval 必须是正数（毫秒），收到「${flags.interval}」\n\n${WAIT_USAGE}`);
      }

      // 总预算从这条命令开始算（含连接桥接的时间），单次调用也不许越过它
      const deadline = Date.now() + timeoutMs;
      const target =
        source === 'bridge' ? `probe=${probeName}` : source === 'process' ? `pid=${pid}` : source === 'event' ? `file=${eventFile}` : '(本 profile)';
      process.stderr.write(
        `[wait] profile=${profile} source=${source} ${target} timeout=${timeoutMs}ms interval=${intervalMs}ms\n`,
      );

      // 四类来源共用**同一个** waitFor 循环：只有「取样器」与「谁提供 client」不同
      const spec = {
        source,
        profile,
        probe: probeName,
        args,
        deadline,
        pid,
        file: eventFile,
        eventSource: eventFormat,
        verifyIdentity: Boolean(flags.verify),
        // 场景可以自带事件语义：从「现在」开始 / 只看最近 N 毫秒。命令行显式给了就以命令行为准。
        fromNow: flags['new-only'] !== undefined ? Boolean(flags['new-only']) : Boolean(scenario?.fromNow),
        windowMs:
          flags['window-ms'] !== undefined ? Number(flags['window-ms']) : (scenario?.windowMs ?? null),
      };
      const result =
        source === 'bridge'
          ? await withBridge(
              profile,
              async (client) => {
                // 名字写错要立刻响亮失败，不要等满超时（与 call 同一条纪律）
                await assertToolNameExists(client, probeName);
                return waitFor(buildSampler(spec, { client }), {
                  condition,
                  timeoutMs: Math.max(1, deadline - Date.now()),
                  intervalMs,
                  what: probeName,
                });
              },
              { requestTimeoutMs: timeoutMs },
            )
          : await waitFor(buildSampler(spec), {
              condition,
              timeoutMs,
              intervalMs,
              what: source === 'process' ? `PID ${pid}` : source === 'event' ? eventFile : `profile ${profile}`,
            });

      const evidence = {
        ok: result.ok,
        elapsedMs: result.elapsedMs,
        attempts: result.attempts,
        source,
        ...(probeName ? { probe: probeName } : {}),
        ...(source === 'process' ? { pid } : {}),
        ...(source === 'event' ? { file: eventFile } : {}),
        ...(scenario ? { scenario: scenario.name, verifiedLevel: scenario.verifiedLevel } : {}),
        condition,
        ...(result.missingPath ? { missingPath: result.missingPath } : {}),
        sample: result.sample,
      };
      const argsHint = flags['args-file']
        ? `--args-file '${flags['args-file']}'`
        : `--args '${JSON.stringify(args)}'`;
      const printed = evidenceForPrint(evidence);
      if (printed.sampleTruncated && probeName) {
        // 采样截断只发生在打印层：真载荷动辄 8 KB，一次超时就能吃掉子代理半个上下文。
        // 复跑命令必须带 --profile：不带就会去打默认的 vanilla（561 mod 那个），白等一轮。
        console.error(
          `[wait] 采样已截断（只印前 ${SAMPLE_PRINT_LIMIT} 字符）；` +
            `要看完整载荷：node src/cli.js call ${probeName} ${argsHint} --profile ${profile}`,
        );
      }
      if (result.ok) {
        out(printed);
      } else {
        // 超时必须能诊断：条件、最后一次采样、attempts、elapsedMs 一个都不能少
        console.error(
          `[wait] 超时：${timeoutMs}ms 内没等到条件成立（attempts=${result.attempts}，elapsedMs=${result.elapsedMs}）`,
        );
        if (result.missingPath) {
          // 字段一直没出现 ≠ 值不匹配：前者多半是还没进图，后者要改条件。分开说才不会让人去改错东西。
          const m = result.missingPath;
          // available 的截断与「共 N 个」摘要在 condition.js 里一次做完，这里只照原样印，不再二次截断（A5）
          console.error(
            `路径 ${m.path} 从头到尾都没取到；断在 ${m.brokenAt} 这一段，` +
              `那一层的可用字段：${m.available.length ? m.available.join(', ') : '（空）'}`,
          );
          if (looksLikeToolErrorPayload(m.available, result.sample)) {
            /**
             * 第三种来源（2026-10-25 试跑缺陷 B2）：探针**自己**报错了。
             * 此时载荷是工具的错误信封（success/message/exception/operation），「字段取不到」只是后果；
             * 按前两种去「等进图 / 改字段名」会白等满一轮，还把问题改到别的字段上。
             */
            console.error(
              '这次是探针自己报错了（那层字段是 success/message/exception/operation 这套错误信封），' +
                '不是「还没进图」，也不是字段名写错。下一步读它的 message：' +
                `node src/cli.js call ${probeName ?? '<探针>'} ${argsHint} --profile ${profile}`,
            );
          } else {
            console.error(
              '通常是还没进图（先等 {"path":"result.state.playable","equals":true}），或字段名写错；' +
                '这条不是参数错误，是条件一直没成立。',
            );
          }
        }
        console.error(JSON.stringify(printed, null, 2));
        process.exitCode = 2;
      }
      break;
    }

    case 'logs': {
      const n = Number(positional[0] || 60);
      const file = logPathFor(profile);
      const lines = tailFile(file, 4_000_000).split(/\r?\n/);
      console.log(lines.slice(-n).join('\n'));
      break;
    }

    case 'live': {
      /**
       * 等一个游戏侧事件，而不是「睡固定时长 + 事后读日志」。
       * 退出码就是结果：0=命中 / 2=超时 / 1=参数错（见 src/live.js 顶部）。
       * 挂成后台 job 时，job 完成的通知本身就是「事件到了」的推送。
       */
      const { runLive } = await import('./live.js');
      process.exitCode = await runLive({ profile, flags });
      break;
    }

    case 'link': {
      /**
       * 命令通道的通用入口（调试/取证用）：`--cmd time.status|time.probe|time.cancel`。
       * 只发一条命令、打回 ack；长命令（time.advance）请用 `advance`（它负责等到落点）。
       */
      const cmd = flags.cmd === undefined ? 'time.status' : String(flags.cmd);
      const { call } = await import('./gamelink.js');
      const ack = await call(profile, cmd, {});
      out(ack);
      if (!ack.ok) process.exitCode = 1;
      break;
    }

    case 'advance': {
      /**
       * 推时间到**指定 tick**（或推到某个事件发生）：参数在 src/advance.js 校验，
       * 执行走 GameLink 的命令通道（`src/gamelink.js`）—— 不再经过桥接，也不需要 RimBridgeServer。
       */
      const opts = parseAdvanceFlags(flags);
      const result = await runAdvance({ profile, ...opts });
      out(result);
      if (!result.ok) process.exitCode = 1;
      break;
    }

    case 'parse-save': {
      /**
       * 存档取证：buff 类验证唯一可信的来源是存档原文（桥接读不到 ticksToDisappear）。
       * defName 由 `--def` 给 —— 写死 defName 的旧脚本在改名后会静默返回 carriers=0（假阴性）。
       */
      const file = positional[0];
      if (!file) throw new Error(`缺少存档路径\n\n${PARSE_SAVE_USAGE}`);
      if (!fs.existsSync(file)) throw new Error(`存档不存在：${file}\n\n${PARSE_SAVE_USAGE}`);
      const def = flags.def === undefined ? 'RHAH_SiegeFury' : String(flags.def);
      const report = parseSave({ file, def });
      if (flags.json !== undefined) out(report);
      else console.log(formatParseSave(report));
      break;
    }

    case 'text-check': {
      /**
       * 验「文件有没有被 PS 5.1 写坏」。以前是 skill 里一句 `node -e`（一次性脚本），现在固化成子命令：
       * 中文坏掉时文件照样存在、大小正常、git 也显示改过，靠肉眼看不住。
       */
      const files = positional;
      if (!files.length) throw new Error(`缺少文件路径\n\n${TEXT_CHECK_USAGE}`);
      let bad = 0;
      let unreadable = 0;
      for (const file of files) {
        if (!fs.existsSync(file)) {
          console.error(`MISSING ${file}`);
          unreadable += 1;
          continue;
        }
        const report = checkTextFile(file);
        console.log(formatTextCheck(report));
        if (!textCheckPassed(report)) bad += 1;
      }
      if (unreadable) process.exitCode = 2;
      else if (bad) process.exitCode = 1;
      break;
    }

    case 'stop': {
      // 默认只杀本 profile 记录里的 PID；--force / --all 才是「枚举全机 RimWorld 并强杀」的破坏性出口。
      const r = await stopGame({ profile, force: Boolean(flags.force || flags.all) });
      if (r.alreadyStopped) {
        console.log(
          r.recordedPid
            ? `profile「${profile}」记录里的 PID ${r.recordedPid} 已不在运行（没有需要关的）`
            : `没有 ${profile} 的运行记录`,
        );
      }
      out(r);
      break;
    }

    case 'smoke': {
      const { runSmoke } = await import('./smoke.js');
      const report = await runSmoke({
        profile,
        ticks: Number(flags.ticks || 300),
        keepRunning: Boolean(flags['keep-running']),
        launch: !flags.attach,
      });
      process.exitCode = report.passed ? 0 : 1;
      break;
    }

    default:
      // 与 `--help` 打同一份总表：两处各写一份迟早漂移（`node src/cli.js <子命令> --help` 看单条用法）
      console.log(GENERAL_USAGE);
  }
}

main().catch((err) => {
  console.error(`\n[错误] ${err.message}`);
  process.exitCode = 1;
});
