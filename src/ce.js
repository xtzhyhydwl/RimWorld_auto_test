/**
 * Character Editor（角色编辑器）的 agent 友好接口。
 *
 * 背景：CE 没有任何程序化 API，它是个纯 UI mod（实测 GitHub 上也没有任何人做过自动化）。
 * 但它用的是标准 Unity IMGUI（widgets.label / button_image / button_invisible / button_text），
 * 所以可以通过 RimBridgeServer 的 UI 自动化精确驱动：
 *   get_ui_layout 拿结构化快照 → 按位置规律找到控件 → click_ui_target 点它。
 *
 * 实测摸到的布局规律（2560x1440，CE 窗口 rect 约 1226x800）：
 *   - 顶部标签页 Bio/Inventory/Health/Needs/Social/Log/Info/Records 自带文字，可直接按文字点
 *   - 殖民者列表：名字 label 在左侧，同一行有一个 button_invisible 可以点
 *   - 技能行：技能名 label（x≈1371）右边固定两个 icon_button —— x≈1550 是「−」，x≈1575 是「+」
 *   - Traits / Psy-Talents 的操作按钮在各自的标题旁边
 *
 * ⚠ 这些坐标是相对屏幕的，窗口大小或 UI 缩放变了就会偏移。
 *   本模块全部按「同行的相对位置」定位，不硬编码绝对坐标，但**仍然依赖 CE 的排版顺序**。
 *   CE 升级后如果排版变了，ce-analyze.mjs 可以重新摸一遍规律。
 */
import { layout } from './ui.js';

export const CE_TAB_ID = 'main-tab:HotkeyEditor';
export const CE_SURFACE_MATCH = /CharacterEditor/i;

/** CE 主标签页名字 */
export const CE_TABS = ['Bio', 'Inventory', 'Health', 'Needs', 'Social', 'Log', 'Info', 'Records'];

/** 取 CE 那个 surface（没开就返回 null） */
export async function ceSurface(client) {
  const snap = await layout(client);
  const s = (snap.surfaces ?? []).find((x) => CE_SURFACE_MATCH.test(String(x.type ?? '')));
  return s ? { surface: s, snapshot: snap } : null;
}

/** 把 CE 窗口里的元素按「行」分组（screenRect.y 相近的算一行） */
function rowsOf(surface) {
  const rows = new Map();
  for (const e of surface.elements ?? []) {
    const y = Math.round((e.screenRect?.y ?? 0) / 4) * 4;
    if (!rows.has(y)) rows.set(y, []);
    rows.get(y).push(e);
  }
  for (const list of rows.values()) list.sort((a, b) => (a.screenRect?.x ?? 0) - (b.screenRect?.x ?? 0));
  return [...rows.entries()].sort((a, b) => a[0] - b[0]);
}

/** CE 开着吗 */
export async function isOpen(client) {
  return Boolean(await ceSurface(client));
}

/** 确保 CE 打开；已开则原样返回 */
export async function ensureOpen(client, { timeoutMs = 8000 } = {}) {
  if (await isOpen(client)) return true;
  await client.callTool('rimworld/open_main_tab', { mainTabId: CE_TAB_ID });
  // 给它一点时间画出来（实测立刻截图会拿到还没绘制完的帧）
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    await new Promise((r) => setTimeout(r, 400));
    if (await isOpen(client)) {
      await new Promise((r) => setTimeout(r, 600)); // 再等一帧，确保内容画完
      return true;
    }
  }
  throw new Error(`打开 Character Editor 超时（${timeoutMs}ms）`);
}

/** 关掉 CE */
export async function close(client) {
  await client.callTool('rimworld/close_window', {}).catch(() => {});
}

/** 点一个自带文字的控件（标签页、按钮等） */
export async function clickLabeled(client, text, { exact = true, scope = null } = {}) {
  const found = scope ?? (await ceSurface(client))?.surface;
  if (!found) throw new Error('CE 没开');
  const hit = (found.elements ?? []).find(
    (e) =>
      e.actionable &&
      e.label &&
      (exact ? e.label === text : String(e.label).includes(text)),
  );
  if (!hit) {
    const labels = [...new Set((found.elements ?? []).filter((e) => e.actionable && e.label).map((e) => e.label))];
    throw new Error(`CE 里找不到可点的「${text}」。可点的文字有：${labels.join(' / ')}`);
  }
  await client.callTool('rimworld/click_ui_target', { targetId: hit.targetId, timeoutMs: 5000 });
  return hit;
}

/** 切到某个标签页 */
export async function openTab(client, tab) {
  await ensureOpen(client);
  await clickLabeled(client, tab);
  await new Promise((r) => setTimeout(r, 700));
  return tab;
}

/** 读左侧殖民者列表里的名字 */
export async function listPawns(client) {
  const s = (await ceSurface(client))?.surface;
  if (!s) throw new Error('CE 没开');
  const rows = rowsOf(s);
  // 殖民者名字所在行：左侧有一个 label 有文字 + 同一行有一个可点元素
  const names = [];
  for (const [, list] of rows) {
    const label = list.find((e) => e.kind === 'label' && e.label && (e.screenRect?.x ?? 0) < 900);
    if (!label) continue;
    const clickable = list.find((e) => e.actionable && Math.abs((e.screenRect?.x ?? 0) - label.screenRect.x) < 60);
    if (clickable) names.push({ name: String(label.label).replace(/<[^>]+>/g, ''), targetId: clickable.targetId });
  }
  return names;
}

/** 选中一个小人（点左侧列表里那行） */
export async function selectPawn(client, name) {
  await ensureOpen(client);
  const pawns = await listPawns(client);
  const hit = pawns.find((p) => p.name === name) ?? pawns.find((p) => p.name.includes(name));
  if (!hit) throw new Error(`CE 列表里没有「${name}」。有：${pawns.map((p) => p.name).join(', ')}`);
  await client.callTool('rimworld/click_ui_target', { targetId: hit.targetId, timeoutMs: 5000 });
  await new Promise((r) => setTimeout(r, 900));
  return hit;
}

/** 把 CE 面板读成一组文本行（按行拼接 label） */
export async function readPanel(client) {
  const s = (await ceSurface(client))?.surface;
  if (!s) throw new Error('CE 没开');
  return rowsOf(s).map(([y, list]) => ({
    y,
    text: list
      .filter((e) => e.label)
      .map((e) => String(e.label).replace(/<[^>]+>/g, '').replace(/\s+/g, ' ').trim())
      .filter(Boolean)
      .join('  '),
  }));
}

/**
 * 找到某个技能那一行的元素，**只返回技能列**。
 *
 * 两个坑（都踩过）：
 *  1. CE 把左栏（殖民者列表）、中栏（背故事）、右栏（技能）排在同一批 y 上，
 *     只按 y 分组会把三栏混在一起 → 必须同时限制 x 在技能列（实测从 x≈1365 开始）。
 *  2. 同一行的元素 y 并不完全相等：技能名在 y=503，等级数字在 y=506。
 *     按 y 取整分组会把它们拆开 → 必须用**容差**（±8px）判断"同一行"。
 */
const SKILL_COLUMN_MIN_X = 1340;
const ROW_TOLERANCE = 8;

async function skillRow(client, skillName) {
  const s = (await ceSurface(client))?.surface;
  if (!s) throw new Error('CE 没开');
  const nameEl = (s.elements ?? []).find(
    (e) => e.kind === 'label' && e.label === skillName && (e.screenRect?.x ?? 0) >= SKILL_COLUMN_MIN_X,
  );
  if (!nameEl) return null;
  const y0 = nameEl.screenRect.y;
  const list = (s.elements ?? [])
    .filter(
      (e) =>
        (e.screenRect?.x ?? 0) >= SKILL_COLUMN_MIN_X &&
        Math.abs((e.screenRect?.y ?? -999) - y0) <= ROW_TOLERANCE,
    )
    .sort((a, b) => (a.screenRect?.x ?? 0) - (b.screenRect?.x ?? 0));
  return { y: y0, list, label: nameEl };
}

/**
 * 读一个技能的等级。
 * 实测等级是技能名右边（x≈1485）的一个数字 label，y 比技能名偏 3px 左右。
 */
export async function getSkillLevel(client, skillName) {
  const row = await skillRow(client, skillName);
  if (!row) return null;
  const nameX = row.label.screenRect.x;
  const numeric = row.list
    .filter(
      (e) =>
        e.kind === 'label' &&
        /^\d+$/.test(String(e.label ?? '').trim()) &&
        (e.screenRect?.x ?? 0) > nameX,
    )
    .sort((a, b) => (a.screenRect?.x ?? 0) - (b.screenRect?.x ?? 0));
  return {
    level: numeric.length ? Number(String(numeric[0].label).trim()) : null,
    second: numeric.length > 1 ? Number(String(numeric[1].label).trim()) : null,
    found: numeric.map((e) => ({ v: String(e.label).trim(), x: Math.round(e.screenRect.x), y: Math.round(e.screenRect.y) })),
  };
}

/**
 * 增减一个技能的等级。
 * 定位方式：技能名所在行里，两个 icon_button 中靠左的是「−」、靠右的是「+」。
 */
export async function bumpSkill(client, skillName, delta = 1) {
  const row = await skillRow(client, skillName);
  if (!row) throw new Error(`CE 里没有技能「${skillName}」`);
  const icons = row.list
    .filter((e) => e.actionable && e.kind === 'icon_button')
    .sort((a, b) => (a.screenRect?.x ?? 0) - (b.screenRect?.x ?? 0));
  if (icons.length < 2) {
    throw new Error(
      `技能「${skillName}」那行只找到 ${icons.length} 个 icon_button，无法判断加减` +
        `（CE 排版可能变了，跑 scripts/ce-analyze.mjs 重新摸规律）`,
    );
  }
  const minus = icons[0];
  const plus = icons[icons.length - 1];
  const steps = Math.abs(delta);
  const target = delta >= 0 ? plus : minus;
  for (let i = 0; i < steps; i++) {
    await client.callTool('rimworld/click_ui_target', { targetId: target.targetId, timeoutMs: 5000 });
    await new Promise((r) => setTimeout(r, 250));
  }
  return { skillName, delta, clicked: steps, targetId: target.targetId };
}

/** 列出当前 CE 面板里所有可点的自带文字控件（用于探索/自检） */
export async function clickableLabels(client) {
  const s = (await ceSurface(client))?.surface;
  if (!s) throw new Error('CE 没开');
  return [...new Set((s.elements ?? []).filter((e) => e.actionable && e.label).map((e) => String(e.label)))];
}

/** 一份人类可读的 CE 现状快照（写报告用） */
export async function describe(client) {
  const s = (await ceSurface(client))?.surface;
  if (!s) return { open: false };
  const rows = await readPanel(client);
  const pawns = await listPawns(client);
  return {
    open: true,
    rect: s.screenRect ?? s.rect,
    elementCount: s.elementCount,
    actionableCount: s.actionableElementCount,
    pawns: pawns.map((p) => p.name),
    clickableLabels: await clickableLabels(client),
    rows: rows.filter((r) => r.text),
  };
}
