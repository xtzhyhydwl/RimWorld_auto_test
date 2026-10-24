/**
 * 真实 UI 操作的辅助层。
 *
 * RimBridgeServer 的语义是「像真人一样点游戏原生界面」：先 get_ui_layout 拿结构化快照，
 * 再用 click_ui_target 按 targetId 激活。
 *
 * 踩过的坑：RimWorld 的一个「列表行」在快照里是**多个元素**——通常是 2 个 label（文字被
 * 画两遍做描边）+ 1 个 button，而 button 自己 label 是 null。所以不能靠 label 找可点元素，
 * 要靠**屏幕矩形相同**把 label 和它对应的 button 配对。
 */

/** 抓一份 UI 布局快照 */
export async function layout(client, args = {}) {
  return client.callTool('rimworld/get_ui_layout', args);
}

/** 从快照里把界面摊平成 [{surface, element}] */
export function elements(snapshot) {
  const out = [];
  for (const s of snapshot?.surfaces ?? []) {
    for (const e of s.elements ?? []) {
      out.push({ surface: s, element: e });
    }
  }
  return out;
}

/** 列出快照里的界面概要 */
export function describeSurfaces(snapshot) {
  return (snapshot?.surfaces ?? []).map((s) => ({
    id: s.surfaceTargetId,
    kind: s.surfaceKind,
    type: s.type,
    label: s.label,
    semantic: s.semanticKind,
    elements: s.elementCount,
    actionable: s.actionableElementCount,
  }));
}

function sameRect(a, b) {
  if (!a || !b) return false;
  return (
    Math.abs(a.x - b.x) < 0.5 &&
    Math.abs(a.y - b.y) < 0.5 &&
    Math.abs(a.width - b.width) < 0.5 &&
    Math.abs(a.height - b.height) < 0.5
  );
}

/**
 * 在一个（或所有）界面里，按可见文字找到那一行，再点它对应的按钮。
 * @param {string} needle 要匹配的文字
 * @param {object} [opts]
 * @param {string} [opts.surfaceType] 限定界面类型（子串匹配）
 * @param {boolean} [opts.exact] true 则要求完全相等，避免「800 points」命中「1800 points」
 * @returns {Promise<{clicked:boolean, targetId?:string, matchedLabel?:string, candidates?:string[]}>}
 */
export async function clickRowByLabel(client, needle, { surfaceType = null, exact = false, timeoutMs = 5000 } = {}) {
  const snap = await layout(client);
  const flat = elements(snap);

  const match = (text) => (exact ? text === needle : text.includes(needle));
  const labelEls = flat.filter(({ surface, element }) => {
    if (surfaceType && !String(surface.type ?? '').includes(surfaceType)) return false;
    return element.kind === 'label' && element.label && match(element.label);
  });

  if (!labelEls.length) {
    return {
      clicked: false,
      candidates: flat
        .filter(({ element }) => element.kind === 'label' && element.label)
        .map(({ element }) => element.label)
        .filter((v, i, a) => a.indexOf(v) === i)
        .slice(0, 40),
    };
  }

  const { surface, element: labelEl } = labelEls[0];
  // 找屏幕矩形和这行文字完全一致的可点元素
  const button = (surface.elements ?? []).find(
    (e) => e.actionable && sameRect(e.screenRect, labelEl.screenRect),
  );
  if (!button) {
    return { clicked: false, matchedLabel: labelEl.label, candidates: ['找到文字但没有对应的可点元素'] };
  }

  await client.callTool('rimworld/click_ui_target', { targetId: button.targetId, timeoutMs });
  return { clicked: true, targetId: button.targetId, matchedLabel: labelEl.label };
}

/** 关掉当前最上层窗口 */
export async function closeTopWindow(client) {
  return client.callTool('rimworld/close_window', {});
}
