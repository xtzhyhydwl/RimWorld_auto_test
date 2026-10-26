/**
 * `advance --to-tick <N>`：把游戏推进到**指定 tick**，一次调用完成。
 *
 * 实现已经换过一次（2026-10-25 晚）：
 *   · 旧实现（本文件的历史）：上位机逐片 `play_for` + 每片回读 tick + 末尾 `step_game_ticks` 兜底。
 *     它的毛病是把代价挪到墙钟上（实测 20000 tick 要 61.5 s，末段只能按 ≈100 tick/s 爬），
 *     而且"停得准不准"取决于上位机切片的算术。
 *   · 现在：**游戏自己闭环**（`mods/DSH_GameLink` 的 `time.advance`）——给档位 + 绝对 tick，
 *     游戏在 `TickManager.DoSingleTick` 的前缀上逐 tick 判「到点了没有」，到点就暂停。
 *     落点严格等于目标（overrun 恒 0），期间帧照出，实测 20000 tick 1.8–5.4 s。
 *
 * 这一层只剩「参数校验 + 把结果翻成人话」；真正的轮询在 `src/gamelink.js`（唯一真源）。
 * 别的脚本想推时间请直接 `import { advanceTo } from './gamelink.js'`。
 */
import { advanceTo, LinkUnavailableError, SPEEDS } from './gamelink.js';

/** 推时间时上位机的默认等待上限（游戏侧 `maxWallClockMs` 之外再留 90 s 余量，见 gamelink.advanceTo） */
export const ADVANCE_REQUEST_TIMEOUT_MS = 120_000;

const DEFAULT_SPEED = 'auto';

export const ADVANCE_USAGE = [
  '用法: node src/cli.js advance --profile <名字> --to-tick <目标tick> [--speed auto|Fast|Superfast|Ultrafast] [--force-speed] [--max-ms 1200000]',
  '      node src/cli.js advance --profile <名字> --stop-when "letter:ThreatBig" [--max-ticks 200000]',
  '',
  '  把游戏推进到**指定的 ticksGame**（或推到某个事件发生），**成功后自动暂停**。',
  '  速度/落点/暂停都由游戏侧（mods/DSH_GameLink 的 time.advance）决定，调用方只给目标。',
  '',
  '  --to-tick    目标 tick（绝对；必须大于当前 ticksGame，用 call rimworld/get_game_info 看当前值）',
  '  --ticks      相对 tick 数（在游戏内换算成「当前+N」，不受上位机读到的 tick 过期影响；--to-tick 二选一）',
  '  --stop-when  停点判据（可多条，用 ; 分隔）：letter / letter:<defName> / message / message:<包含词>',
  '  --watch      只观察不停：命中就往事件通道写一条 probe_hit（同样用 ; 分隔）',
  '  --speed      默认 auto = 按距离选档：剩余 > 6000 tick 走 Ultrafast，否则 Fast',
  '  --force-speed 压掉游戏的强制慢速（ForceNormalSpeed）。**默认关**；按 AGENTS.md 硬约束 6，开它要用户当次允许',
  '  --max-ms     游戏侧墙钟预算，默认 1200000（20 分钟）；超了算失败（会先暂停再返回）',
  '  --max-ticks  相对 tick 上限：给"只等某个事件、但怕它永远不来"兜底',
  '',
  '  成功：exit 0，stdout 打 {"ok":true,"from":…,"to":…,"reached":…,"exact":true,"stopReason":"target",…}',
  '  被停点判据命中：stopReason="probe"，stopPayload 里是命中的信件/消息现场（标题、文本、defName）。',
  '  失败：非零退出 —— 外部暂停（教学/模态窗）会在游戏里打红字；处理完用**同一个 --to-tick** 再调一次即可（绝对 tick，不会重复推进）。',
].join('\n');

function num(v, name) {
  const n = Number(v);
  if (!Number.isFinite(n)) throw new Error(`--${name} 需要数字，收到「${v}」\n\n${ADVANCE_USAGE}`);
  return n;
}

/**
 * 解析并校验参数。抛错即"用法不对"，不碰游戏。
 * @returns {{toTick:number|null, speed:string, maxMs:number, forceSpeed:boolean, stopWhen:string|null, watch:string|null, maxTicks:number|null}}
 */
export function parseAdvanceFlags(flags) {
  const hasToTick = flags['to-tick'] !== undefined && flags['to-tick'] !== true;
  const ticks = flags.ticks === undefined ? null : num(flags.ticks, 'ticks');
  const stopWhen = flags['stop-when'] === undefined ? null : String(flags['stop-when']);
  const watch = flags.watch === undefined ? null : String(flags.watch);
  const maxTicks = flags['max-ticks'] === undefined ? null : num(flags['max-ticks'], 'max-ticks');

  if (!hasToTick && !ticks && !stopWhen && maxTicks === null) {
    throw new Error(`缺少 --to-tick（目标 tick，整数），也没给 --stop-when / --max-ticks\n\n${ADVANCE_USAGE}`);
  }

  let toTick = null;
  if (hasToTick) {
    toTick = num(flags['to-tick'], 'to-tick');
    if (!Number.isInteger(toTick) || toTick <= 0) {
      throw new Error(`--to-tick 必须是正整数，收到「${flags['to-tick']}」`);
    }
  }

  const speed = flags.speed === undefined ? DEFAULT_SPEED : String(flags.speed);
  if (speed !== 'auto' && !SPEEDS.includes(speed)) {
    throw new Error(`--speed 只认 auto|${SPEEDS.join('|')}，收到「${speed}」\n\n${ADVANCE_USAGE}`);
  }

  const maxMs = flags['max-ms'] === undefined ? 1_200_000 : num(flags['max-ms'], 'max-ms');
  if (maxMs < 1000) throw new Error(`--max-ms 至少 1000，收到 ${maxMs}`);

  const forceSpeed = flags['force-speed'] !== undefined && flags['force-speed'] !== false;

  if (ticks !== null && (!Number.isInteger(ticks) || ticks <= 0)) {
    throw new Error(`--ticks 必须是正整数，收到「${flags.ticks}」`);
  }

  return { toTick, ticks, speed, maxMs, forceSpeed, stopWhen, watch, maxTicks };
}

/**
 * 推进到目标 tick（或推到某个停点判据命中）。
 *
 * @returns {Promise<{ok:boolean, from:number, to:number|null, reached:number, exact:boolean,
 *   stopReason:string|null, paused:boolean, wallClockMs:number, averageTicksPerSecond:number|null}>}
 */
export async function runAdvance({ profile, toTick, ticks, speed, maxMs, forceSpeed, stopWhen, watch, maxTicks, log }) {
  const write = log ?? ((line) => process.stderr.write(`${line}\n`));

  let result;
  try {
    result = await advanceTo({
      profile,
      toTick,
      ticks,
      speed,
      stopWhen,
      watch,
      forceSpeed,
      maxWallClockMs: maxMs,
      maxTicks,
    });
  } catch (err) {
    if (err instanceof LinkUnavailableError) {
      throw new Error(`${err.message}\n\n如果这个 profile 本来就不该有游戏在跑，先 start。`);
    }
    throw err;
  }

  if (!result.ok) {
    write(
      `[advance] 失败：${result.error?.code ?? result.stopReason ?? 'unknown'} — ${result.error?.message ?? result.stopDetail ?? ''}`,
    );
    if (result.stopReason) {
      write(
        `[advance] 已走 ${result.ticksAdvanced ?? '?'} tick（${result.from ?? '?'} → ${result.reached ?? '?'}），` +
          `停在 ${result.stopReason}，游戏已暂停：处理完用同一个 --to-tick 再调一次即可续跑`,
      );
    }
    return result;
  }

  write(
    `[advance] 到位 ${result.reached}（目标 ${result.to ?? `${result.ticksAdvanced} tick`}），` +
      `${result.stopReason}，墙钟 ${Math.round((result.wallClockMs ?? 0) / 100) / 10}s，` +
      `${result.averageTicksPerSecond ?? '?'} tick/s，已暂停=${result.paused}`,
  );
  if (result.stopPayload) write(`[advance] 停点现场：${JSON.stringify(result.stopPayload)}`);
  return result;
}
