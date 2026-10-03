#!/usr/bin/env node
/**
 * 在 DSH 之外验证 dsh-rimworld 插件。
 *
 * 做法：造一个假的 Cordis ctx（只实现 tools.register），加载插件，收集它注册的工具，
 * 然后真的调用几个工具 —— 包括对正在运行的游戏发真实请求。
 *
 * 用法：
 *   node scripts/test-plugin.mjs            # 只测不需要游戏的工具
 *   node scripts/test-plugin.mjs --live     # 连真实游戏，测 status/tools/call
 */
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const WS = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const LIVE = process.argv.includes('--live');

const pluginUrl = pathToFileURL(path.join(WS, 'plugin', 'dsh-rimworld', 'lib', 'index.js')).href;
const mod = await import(pluginUrl);
const plugin = mod.default;

console.log(`插件导出类型: ${typeof plugin}`);
console.log(`inject: ${JSON.stringify(plugin.inject)}`);

const registered = [];
const ctx = {
  tools: {
    register(def) {
      registered.push(def);
    },
  },
};

plugin(ctx, {
  libraryPath: path.join(WS, 'src'),
  gameRoot: process.env.RIMWORLD_ROOT || 'D:\\SteamLibrary\\steamapps\\common\\RimWorld',
  profile: 'vanilla',
});

console.log(`\n注册了 ${registered.length} 个工具：`);
for (const t of registered) {
  const params = Object.keys(t.parameters || {});
  console.log(`  ${t.name.padEnd(18)} 参数[${params.join(', ')}]`);
}

function get(name) {
  const t = registered.find((x) => x.name === name);
  if (!t) throw new Error(`没有注册 ${name}`);
  return t;
}

// 每个工具都验证一下 presentCall / render 的形状，避免真机上才炸
console.log('\n=== 校验展示层（presentCall / render / presentResult） ===');
for (const t of registered) {
  const fakeArgs = {};
  for (const [k, v] of Object.entries(t.parameters || {})) {
    if (v.required) fakeArgs[k] = v.type === 'integer' ? 1 : 'x';
  }
  if (t.presentCall) {
    const c = t.presentCall(fakeArgs);
    if (!c || typeof c !== 'object') throw new Error(`${t.name}.presentCall 返回值不合法`);
  }
  const fakeResult = { meta: {} };
  if (t.presentResult) t.presentResult(fakeArgs, fakeResult);
  if (t.output?.render) {
    const blocks = t.output.render(fakeArgs, {});
    if (!Array.isArray(blocks)) throw new Error(`${t.name}.render 必须返回数组`);
  }
  console.log(`  ✓ ${t.name}`);
}

console.log('\n=== 调用不需要游戏的工具 ===');
const status = get('rimworld_status');
const statusRes = await status.execute({});
console.log('rimworld_status:');
console.log(
  statusRes.text
    .split('\n')
    .map((l) => '    ' + l)
    .join('\n'),
);

const logs = get('rimworld_logs');
const logsRes = await logs.execute({ lines: 5, source: 'file' });
console.log('\nrimworld_logs(file, 5 行):');
console.log(
  logsRes.text
    .split('\n')
    .map((l) => '    ' + l)
    .join('\n'),
);

if (LIVE) {
  if (!statusRes.bridgeUp) {
    console.log('\n[跳过 live 测试] 桥接未就绪，先启动游戏。');
    process.exit(0);
  }
  console.log('\n=== 对真实游戏发请求 ===');
  const tools = get('rimworld_tools');
  const toolsRes = await tools.execute({ filter: 'colonist' });
  console.log(`rimworld_tools(filter="colonist") → ${toolsRes.count} 个`);
  console.log(
    toolsRes.text
      .split('\n')
      .slice(0, 8)
      .map((l) => '    ' + l)
      .join('\n'),
  );

  const call = get('rimworld_call');
  const callRes = await call.execute({ tool: 'rimworld/list_colonists', args: '{}' });
  const parsed = JSON.parse(callRes.json);
  console.log(`\nrimworld_call(rimworld/list_colonists) → ${parsed.count} 个殖民者`);
  for (const c of parsed.colonists ?? []) {
    console.log(`    ${c.fullName}  @(${c.position?.x}, ${c.position?.z})  ${c.job}`);
  }

  const bridgeLogs = await logs.execute({ source: 'bridge', minimumLevel: 'warning' });
  console.log('\nrimworld_logs(bridge, warning+):');
  console.log(
    bridgeLogs.text
      .split('\n')
      .slice(0, 6)
      .map((l) => '    ' + l)
      .join('\n'),
  );
}

console.log('\n插件自检通过 ✓');
