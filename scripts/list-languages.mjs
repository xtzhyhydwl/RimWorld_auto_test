/**
 * 列出可用语言，并标出哪个是当前生效的。
 * 用法：node scripts/list-languages.mjs
 */
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor } from '../src/launcher.js';

const info = readBridgeInfo(logPathFor('vanilla'));
if (!info) { console.error('没有桥接信息'); process.exit(1); }

const { client } = await connectGabp(info, { requestTimeoutMs: 60000 });
try {
  const r = await client.callTool('rimworld/list_languages', {}, { timeoutMs: 60000 });
  const all = r?.languages || r?.items || [];
  console.log(`共 ${all.length} 种语言`);
  const cur = all.filter((l) => l.isCurrent || l.current);
  console.log(`当前：${cur.map((l) => l.displayName || l.id).join(', ') || '(未标出)'}`);
  console.log('');
  console.log('中文相关的：');
  for (const l of all) {
    const n = `${l.displayName || ''} ${l.friendlyNameNative || ''} ${l.folderName || ''}`;
    if (!/Chinese|简体|繁體/i.test(n)) continue;
    console.log(`  id=${l.id}`);
    console.log(`     displayName=${l.displayName}`);
    console.log(`     folderName=${l.folderName}`);
    console.log(`     recommendedQuery=${l.recommendedQuery}`);
    console.log(`     isCurrent=${l.isCurrent}`);
  }
  // 顺便把 English 的也打出来对照
  const en = all.find((l) => l.recommendedQuery === 'English');
  if (en) {
    console.log('');
    console.log('English:');
    console.log(`  id=${en.id}  folderName=${en.folderName}  isCurrent=${en.isCurrent}`);
  }
} finally {
  client.close();
}
