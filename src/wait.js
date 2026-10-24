/**
 * 唯一的等待原语：反复采样一个探针（`async () => 载荷`），直到条件 JSON 成立
 * （`ok:true`）或护栏到期（`ok:false`，并把最后一次采样带回来）。
 *
 * 判定不在这个文件里：求值器与算子住在 `src/condition.js`（原子判定，不认识数据来源）。
 * 本文件只负责「循环 + 间隔 + 超时 + 探针故障」这一件事 —— 这样任何来源（桥接工具 / 事件流 /
 * 进程 / 桥接就绪）都能复用同一个循环。
 *
 * 为什么判定留在 Node 侧、不下发给游戏内的 `rb.poll`：
 *   1) 能离线测——条件写对写错不用开游戏就能验；
 *   2) 追溯链——字段路径与「谁在用」在同一个仓库里可 grep；塞进 `rb.poll` 的 JSON 里，
 *      字段名写错只会**静默不匹配**。
 * 条件语法刻意与桥接的 15 个条件算子同构（本仓库另加 `not` / `contains`），
 * 同一份 JSON 以后可原样下发（含那两类的除外）。
 *
 * 本文件不连 GABP、不读日志、不查进程：探针由调用方给。
 * 也不做「等它做完」的固定延时——唯一的定时是条件轮询的间隔。
 */
import { tryMatch } from './condition.js';

export const DEFAULT_TIMEOUT_MS = 60_000;
export const DEFAULT_INTERVAL_MS = 200;

/** 既有对外名（`wait.js` 的 `matchesCondition` 有调用方），实现已搬到 condition.js */
export { matchesCondition } from './condition.js';

/**
 * 主原语。`probe` 是任意异步探针；`condition` 是条件 JSON。
 *
 * 超时**返回**而不是抛错（设计稿 §4.4 要点 1：超时是分支不是异常），调用方拿
 * `ok:false` + `attempts` + `sample` 出证据；探针自己坏掉则抛错，因为「取不到
 * 观测」和「观测到没成立」是两回事，再等也是白等（设计稿 §2.2）。
 * 字段这一次不存在（例：还没进图时 `list_colonists` 没有 `colonists`）算「没成立」，
 * 继续轮询；超时把最后一次断点从 `missingPath` 带回，让调用方能说清「字段一直没出现」
 * 而不是「条件一直不成立」。
 *
 * @param {() => Promise<any>} probe
 * @param {object} opts
 * @param {object} opts.condition 条件 JSON（见 .dsh/skills/rimworld-auto-test-closed-loop/docs/cli-wait-用法-20261024.md）
 * @param {number} [opts.timeoutMs]
 * @param {number} [opts.intervalMs]
 * @param {string} [opts.what] 人话名字，进错误信息
 * @returns {Promise<{ok:boolean, elapsedMs:number, attempts:number, sample:any,
 *   missingPath: {path:string, brokenAt:string, segment:string, available:string[]}|null}>}
 *   `missingPath` 非空 = 最后一次采样是「路径取不到」（不是「值不匹配」）。
 */
export async function waitFor(
  probe,
  { condition, timeoutMs = DEFAULT_TIMEOUT_MS, intervalMs = DEFAULT_INTERVAL_MS, what = '条件' } = {},
) {
  if (typeof probe !== 'function') throw new Error('waitFor 需要 probe（一个返回采样的函数）');
  if (condition === undefined || condition === null) throw new Error('waitFor 需要 condition（条件 JSON）');
  if (!Number.isFinite(timeoutMs) || timeoutMs <= 0) {
    throw new Error(`timeoutMs 必须是正数（毫秒），收到「${timeoutMs}」`);
  }
  if (!Number.isFinite(intervalMs) || intervalMs <= 0) {
    throw new Error(`intervalMs 必须是正数（毫秒），收到「${intervalMs}」`);
  }

  const startedAt = Date.now();
  let attempts = 0;
  let sample;
  // 最后一次采样是「路径取不到」时把它带回去：超时要能说清是「字段一直没出现」而不是
  // 「条件一直不成立」——这两件事给人的下一步动作完全不同（等进图 vs 改条件）。
  let lastMissing = null;

  for (;;) {
    // 预算用完就不再发新探针：否则会拿 1ms 的超时去打桥接，把「时间到了」误报成「桥接挂了」
    if (attempts > 0 && Date.now() - startedAt >= timeoutMs) {
      return { ok: false, elapsedMs: Date.now() - startedAt, attempts, sample, missingPath: lastMissing };
    }
    attempts += 1;
    try {
      sample = await probe();
    } catch (err) {
      const elapsedMs = Date.now() - startedAt;
      if (sample !== undefined && elapsedMs >= timeoutMs) {
        // 最后一轮探针正好把预算用光：这是我们没时间再看了，不是桥接坏了
        return { ok: false, elapsedMs, attempts, sample, missingPath: lastMissing };
      }
      const wrapped = new Error(
        `探针故障（第 ${attempts} 次采样，${elapsedMs}ms）：${err.message}\n` +
          `  这不是「${what}」没达成，是取不到观测；再等也不会变好。`,
      );
      wrapped.cause = err;
      Object.assign(wrapped, { attempts, elapsedMs, sample });
      throw wrapped;
    }

    const { matched, missing } = tryMatch(condition, sample);
    lastMissing = missing;
    if (matched) {
      return { ok: true, elapsedMs: Date.now() - startedAt, attempts, sample, missingPath: null };
    }

    const remainingMs = timeoutMs - (Date.now() - startedAt);
    if (remainingMs <= 0) {
      return { ok: false, elapsedMs: Date.now() - startedAt, attempts, sample, missingPath: lastMissing };
    }
    // 唯一的定时：条件轮询的间隔（这是闭环——每轮都重新采样，不是「等它做完」）
    await new Promise((resolve) => setTimeout(resolve, Math.min(intervalMs, remainingMs)));
  }
}

