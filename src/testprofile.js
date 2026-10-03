/**
 * 测试环境配置助手：把「确定性 + 加速」这套开关一次性应用上去。
 *
 * 为什么需要它：RimWorld 的 Settings 子树里有 181 个调试开关，其中好几个随机源
 * **默认是开着的**（Enable Storyteller / Enable Random Mental States / Enable Random Diseases）。
 * 不关掉它们，同一份测试每次跑出来的结果都不一样，回归测试就没意义。
 *
 * 用法：
 *   import { applyTestProfile, readSettings } from './testprofile.js';
 *   await applyTestProfile(client, ['deterministic', 'fast']);
 *   const before = await readSettings(client);
 *   ...跑测试...
 *   await restoreSettings(client, before);
 */

/** 命名配置档 */
export const PROFILES = {
  /** 关掉随机源，让「同样输入 → 同样输出」成立 */
  deterministic: {
    'Settings\\Enable Storyteller': false,
    'Settings\\Enable Random Mental States': false,
    'Settings\\Enable Random Diseases': false,
    'Settings\\Enable Damage': false,
    'Settings\\Enable Player Damage': false,
    // 出错不要弹窗/暂停，否则自动化会卡死在那里
    'Settings\\Pause On Error': false,
  },

  /** 加速：把要等很久的进度条拉快 */
  fast: {
    'Settings\\Fast Research': true,
    'Settings\\Fast Learning': true,
    'Settings\\Fast Crafting': true,
    'Settings\\Fast Caravans': true,
    'Settings\\Fast Ecology': true,
    'Settings\\Never Force Normal Speed': true,
  },

  /** 上帝模式 + 常用便利开关 */
  sandbox: {
    'Settings\\God Mode': true,
    'Settings\\Unlimited Power': true,
    'Settings\\Instant Recruit': true,
    'Settings\\Show Hidden Info': true,
  },

  /** 性能测试：人为制造压力 */
  stress: {
    'Settings\\LowFPS': true,
    'Settings\\Single Threaded Drawing': true,
    'Settings\\Show Fps Counter': true,
    'Settings\\Show Tps Counter': true,
  },
};

/** 读取单个开关的当前状态 */
export async function readSetting(client, path) {
  const r = await client.callTool('rimworld/get_debug_action', {
    path,
    includeChildren: false,
    includeHiddenChildren: true,
  });
  const node = r.action ?? r.node ?? r;
  return { path, on: node.on ?? null, toggle: node.hasSettingsToggle ?? null, error: r.success === false };
}

/** 批量读取一批开关 */
export async function readSettings(client, paths) {
  const out = {};
  for (const p of paths) {
    try {
      out[p] = await readSetting(client, p);
    } catch (err) {
      out[p] = { path: p, error: true, message: err.message };
    }
  }
  return out;
}

/**
 * 应用一个或多个配置档，并**回读验证**每个开关真的生效了。
 * @returns {{applied:Array, failed:Array, before:object}}
 */
export async function applyTestProfile(client, names, { verify = true, log = () => {} } = {}) {
  const wanted = {};
  for (const n of names) {
    const p = PROFILES[n];
    if (!p) throw new Error(`未知的配置档：${n}（可选：${Object.keys(PROFILES).join(', ')}）`);
    Object.assign(wanted, p);
  }

  const before = await readSettings(client, Object.keys(wanted));
  const applied = [];
  const failed = [];

  for (const [path, value] of Object.entries(wanted)) {
    try {
      await client.callTool('rimworld/set_debug_setting', { path, enabled: value });
      if (verify) {
        const now = await readSetting(client, path);
        if (now.on === value) {
          applied.push({ path, value });
          log(`  ✓ ${path} = ${value}`);
        } else {
          failed.push({ path, wanted: value, got: now.on, reason: '回读值不符' });
          log(`  ✗ ${path} 期望 ${value} 实际 ${now.on}`);
        }
      } else {
        applied.push({ path, value });
      }
    } catch (err) {
      failed.push({ path, wanted: value, reason: err.message });
      log(`  ✗ ${path}: ${err.message}`);
    }
  }
  return { applied, failed, before };
}

/** 恢复到之前读到的状态 */
export async function restoreSettings(client, snapshot) {
  const restored = [];
  for (const [path, info] of Object.entries(snapshot ?? {})) {
    if (info?.on === null || info?.on === undefined) continue;
    try {
      await client.callTool('rimworld/set_debug_setting', { path, enabled: info.on });
      restored.push({ path, on: info.on });
    } catch {
      // 恢复失败不致命，记下即可
      restored.push({ path, on: info.on, failed: true });
    }
  }
  return restored;
}
