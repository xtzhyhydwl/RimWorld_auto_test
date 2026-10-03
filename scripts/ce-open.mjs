#!/usr/bin/env node
/** 找出 open_main_tab 能接受的正确标识，并打开 Character Editor。 */
import fs from 'node:fs';
import path from 'node:path';
import { connectGabp } from '../src/gabp.js';
import { readBridgeInfo, logPathFor, WS } from '../src/launcher.js';
import { layout, describeSurfaces, elements } from '../src/ui.js';

const stamp = new Date().toISOString().replace(/[:.]/g, '-');
const outDir = path.join(WS, 'runs', `ce-open-${stamp}`);
fs.mkdirSync(outDir, { recursive: true });

const info = readBridgeInfo(logPathFor('vanilla'));
const { client } = await connectGabp(info, { requestTimeoutMs: 25_000 });
const call = (n, a, o) => client.callTool(n, a, o);
const log = (...a) => console.log(...a);

const st = (await call('rimbridge/get_bridge_status')).state;
if (st.inEntryScene) {
  await call('rimworld/start_debug_game_ready', { readiness: 'mapData', pauseIfNeeded: true, timeoutMs: 300_000 }, { timeoutMs: 330_000 });
}
await call('rimbridge/wait_for_game_loaded', { readiness: 'currentMap', pauseIfNeeded: true, timeoutMs: 180_000 }, { timeoutMs: 210_000 });
await call('rimbridge/wait_for_long_event_idle', { timeoutMs: 60_000 });

log('=== list_main_tabs 里 CE 那条的完整字段 ===');
const tabsRes = await call('rimworld/list_main_tabs');
log(`  顶层键: ${Object.keys(tabsRes).join(', ')}`);
const tabs = tabsRes.tabs ?? tabsRes.mainTabs ?? [];
const ce = tabs.find((t) => /character/i.test(`${t.label} ${t.defName} ${t.tabWindowType}`));
log(JSON.stringify(ce, null, 2));
log(`\n  所有键: ${Object.keys(ce ?? {}).join(', ')}`);

log('\n=== 逐个候选试 open_main_tab ===');
const cands = [
  ce?.mainTabId,
  ce?.targetId,
  ce?.id,
  ce?.defName,
  ce?.label,
  ce?.tabWindowType,
  'CharacterEditor.MainTabWindow_CharacterEditor',
].filter(Boolean);
for (const c of [...new Set(cands)]) {
  try {
    const r = await call('rimworld/open_main_tab', { mainTabId: c });
    const opened = r.success !== false;
    log(`  ${opened ? '✓' : '✗'} mainTabId="${c}"  → ${r.message ?? 'ok'}`);
    if (opened) {
      await new Promise((x) => setTimeout(x, 900));
      const snap = await layout(client);
      log('    界面:');
      for (const s of describeSurfaces(snap)) log(`      ${s.id}  type=${s.type}  元素 ${s.elements} / 可点 ${s.actionable}`);
      const all = elements(snap);
      const clickable = all.filter(({ element }) => element.actionable);
      log(`    可点 ${clickable.length} 个`);
      if (all.length > 20) {
        fs.writeFileSync(path.join(outDir, 'ce-layout.json'), JSON.stringify(snap, null, 2), 'utf8');
        const labels = [...new Set(all.filter(({ element }) => element.label).map(({ element }) => String(element.label).replace(/<[^>]+>/g, '')))];
        log(`    可见文字 ${labels.length} 条，前 50:`);
        for (const t of labels.slice(0, 50)) log(`      | ${t}`);
        const shot = await call('rimworld/take_screenshot', { suppressMessage: true, fileName: 'ce_opened' });
        if (shot?.path && fs.existsSync(shot.path)) fs.copyFileSync(shot.path, path.join(outDir, path.basename(shot.path)));
        log(`    截图: ${shot?.path}`);
        break;
      }
    }
  } catch (e) {
    log(`  ✗ mainTabId="${c}"  → ${e.message}`);
  }
}

client.close();
log(`\n产物: ${outDir}`);
