/**
 * 只看 `foodprobe/peaceful` 的结果：会关掉哪些事件、难度开关状态。
 * 用法：node scripts/show-peaceful.mjs [--enable]
 */
import path from 'node:path';
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor } from '../src/launcher.js';

const info = readBridgeInfo(logPathFor('vanilla'));
if (!info) { console.error('没有桥接信息'); process.exit(1); }

const enable = !process.argv.includes('--restore');
const { client } = await connectGabp(info, { requestTimeoutMs: 60000 });
try {
  const r = await client.callTool('foodprobe/peaceful', { enable, dryRun: true }, { timeoutMs: 60000 });
  console.log(`模式：${enable ? '关掉威胁' : '恢复威胁'}  dryRun=${r.dryRun}`);
  console.log(`会改动 ${r.changed} 个事件定义（备份表 ${r.backupSize} 项）`);
  console.log(`难度：${r.difficulty}`);
  console.log('');
  console.log('会关掉的事件：');
  for (const t of r.touched || []) {
    console.log(`  ${String(t.defName).padEnd(38)} cat=${t.category}`);
  }
} finally {
  client.close();
}
