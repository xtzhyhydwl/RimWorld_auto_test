/**
 * 工具名预校验 + 「最接近候选」建议。
 *
 * 2026-10-13 新增。存在理由（来自只读审计 `tmp/audit-test/AUDIT.md` 的 2.7 / A8 两条）：
 * `scripts/verify-5-actions.mjs` 调用了**不存在**的工具 `rimworld/wait_for_long_event_idle`
 * ——该工具只在 `rimbridge/` 命名空间下——并且失败被 `.catch(() => {})` 吞掉。
 * 结果：脚本「以为自己等过了」，实际**零等待、零记录**，报告看起来有效却什么都没发生。
 * 调用不存在的工具名在桥接层不会给出响亮的错误，所以校验放在 CLI 这一层，
 * 让写错的名字在**发起调用之前**就暴露出来。
 *
 * 本模块刻意做成**纯函数**（不碰网络/文件），这样在没有游戏在跑的情况下也能离线单测。
 */

/** 拆出命名空间与子名：`rimbridge/wait_for_long_event_idle` → {namespace:'rimbridge', local:'wait_for_long_event_idle'} */
export function splitToolName(name) {
  const s = String(name ?? '');
  const i = s.lastIndexOf('/');
  if (i < 0) return { namespace: '', local: s };
  return { namespace: s.slice(0, i), local: s.slice(i + 1) };
}

/** 经典 Levenshtein 编辑距离（用于「拼错几个字」的候选匹配） */
export function editDistance(a, b) {
  const x = String(a ?? '');
  const y = String(b ?? '');
  if (x === y) return 0;
  if (!x.length) return y.length;
  if (!y.length) return x.length;
  let prev = Array.from({ length: y.length + 1 }, (_, j) => j);
  for (let i = 1; i <= x.length; i++) {
    const cur = [i];
    for (let j = 1; j <= y.length; j++) {
      const cost = x[i - 1] === y[j - 1] ? 0 : 1;
      cur[j] = Math.min(prev[j] + 1, cur[j - 1] + 1, prev[j - 1] + cost);
    }
    prev = cur;
  }
  return prev[y.length];
}

/**
 * 找出最接近的工具名候选（按相似度升序，分数越小越近）。
 *
 * 判据分三档：
 *   1. **子名完全相同、只是命名空间不同**（分数 0/1）——这正是 `rimworld/` 与 `rimbridge/` 混用的形态；
 *   2. 子名互为前缀（分数 2）——用户常写缩写，如 `rimworld/get_u` → `rimworld/get_ui_state`；
 *   3. 退回编辑距离（子名优先，其次整个名字）。
 */
export function suggestToolNames(name, names, { limit = 3 } = {}) {
  const { namespace, local } = splitToolName(name);
  const scored = [];
  for (const cand of names ?? []) {
    const c = splitToolName(cand);
    const sameLocal = local.length > 0 && c.local === local;
    const prefixMatch =
      !sameLocal && local.length >= 3 && (c.local.startsWith(local) || local.startsWith(c.local));
    const dLocal = editDistance(local, c.local);
    const dFull = editDistance(name, cand);
    const score = sameLocal
      ? c.namespace === namespace
        ? 0
        : 1
      : (prefixMatch ? 2 : 3) + Math.min(dLocal, dFull);
    scored.push({
      name: String(cand),
      score,
      sameLocal,
      prefixMatch,
      namespaceDiffers: sameLocal && c.namespace !== namespace,
      distance: sameLocal ? 0 : dLocal,
    });
  }
  scored.sort((a, b) => a.score - b.score || a.name.localeCompare(b.name));
  const maxDistance = Math.max(2, Math.ceil(local.length / 3));
  return scored
    .filter((s) => s.sameLocal || s.prefixMatch || s.distance <= maxDistance || editDistance(name, s.name) <= maxDistance)
    .slice(0, limit);
}

/**
 * 校验一个工具名是否存在于清单里。
 *
 * @param {string} name   用户输入的工具名
 * @param {string[]} names 可用的工具名清单（**必须是在线拿到的**；离线快照会过期，见下方说明）
 * @returns {{checked:boolean, ok:boolean, name:string, candidates?:object[], hints?:string[], namespaces?:string[]}}
 *   `checked:false` 表示**没能校验**（清单为空/不可用）——调用方应当跳过校验而不是报错，
 *   因为用过期快照硬判会产生假失败。
 */
export function checkToolName(name, names) {
  const list = (names ?? []).map(String).filter(Boolean);
  if (!list.length) {
    return { checked: false, ok: false, name, reason: '清单为空或不可用' };
  }
  if (list.includes(String(name))) {
    return { checked: true, ok: true, name: String(name) };
  }

  const candidates = suggestToolNames(name, list);
  const { namespace } = splitToolName(name);
  const namespaces = [...new Set(list.map((n) => splitToolName(n).namespace).filter(Boolean))].sort();
  const hints = [];

  const nsMiss = candidates.find((c) => c.namespaceDiffers);
  if (nsMiss) {
    const candNs = splitToolName(nsMiss.name).namespace;
    if (namespace) hints.push(`命名空间可能是 ${candNs}/ 而不是 ${namespace}/。`);
    else hints.push(`这个工具名带命名空间，例如 ${candNs}/${splitToolName(nsMiss.name).local}。`);
  } else if (namespace && !namespaces.includes(namespace)) {
    hints.push(
      `桥接里没有 ${namespace}/ 这个命名空间。现有命名空间：${namespaces.map((n) => `${n}/`).join(' ')}`,
    );
  }
  hints.push('查实时清单：`node src/cli.js tools <关键词>`（或 `node src/cli.js describe <工具名>`）。');

  return { checked: true, ok: false, name: String(name), candidates, hints, namespaces };
}
