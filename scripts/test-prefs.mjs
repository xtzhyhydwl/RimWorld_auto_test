#!/usr/bin/env node
/** Prefs 合并逻辑自检：强制项要生效，非强制项要保留。 */
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import { resolvePrefs, ensurePrefs, prefsPath } from '../src/prefs.js';

const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'prefs-test-'));
let failures = 0;
function check(name, ok, detail) {
  console.log(`  ${ok ? '✓' : '✗'} ${name}${ok ? '' : ` — ${detail}`}`);
  if (!ok) failures++;
}

// 1. 全新 profile
const prefs = resolvePrefs(dir, {});
const r1 = ensurePrefs(dir, prefs);
const xml1 = fs.readFileSync(prefsPath(dir), 'utf8');
console.log('=== 首次生成 ===');
console.log(xml1);
check('标记为新建', r1.created);
check('窗口化已写入', /<fullscreen>False<\/fullscreen>/.test(xml1));
check('分辨率已写入', /<screenWidth>1600<\/screenWidth>/.test(xml1));
check('音乐已关', /<volumeMusic>0<\/volumeMusic>/.test(xml1));
check('语言固定英文', /<langFolderName>English<\/langFolderName>/.test(xml1));

// 2. 模拟用户在游戏里改了别的设置 + 同样的键
let xml2 = xml1
  .replace('<volumeMaster>0.6</volumeMaster>', '<volumeMaster>1</volumeMaster>')
  .replace('</PrefsData>', '  <temperatureMode>Celsius</temperatureMode>\n  <someComplexThing>\n    <li>a</li>\n    <li>b</li>\n  </someComplexThing>\n</PrefsData>');
fs.writeFileSync(prefsPath(dir), xml2, 'utf8');

const r2 = ensurePrefs(dir, { screenWidth: 1280 });
const xml3 = fs.readFileSync(prefsPath(dir), 'utf8');
console.log('\n=== 二次合并（只强制 screenWidth=1280） ===');
console.log(xml3);
check('不是新建', !r2.created);
check('screenWidth 被改成 1280', /<screenWidth>1280<\/screenWidth>/.test(xml3));
check('用户改的 volumeMaster 保留', /<volumeMaster>1<\/volumeMaster>/.test(xml3));
check('用户加的 temperatureMode 保留', /<temperatureMode>Celsius<\/temperatureMode>/.test(xml3));
check('复杂嵌套块保留', /<someComplexThing>[\s\S]*<li>b<\/li>[\s\S]*<\/someComplexThing>/.test(xml3));
check('没有把 fullscreen 改回去', /<fullscreen>False<\/fullscreen>/.test(xml3));
check('XML 结构完好', (xml3.match(/<\/PrefsData>/g) || []).length === 1);

// 3. prefs.json 覆盖默认值
fs.writeFileSync(path.join(dir, 'prefs.json'), JSON.stringify({ devMode: true, screenHeight: 720 }), 'utf8');
const prefs3 = resolvePrefs(dir, {});
console.log('\n=== prefs.json 覆盖 ===');
console.log(JSON.stringify(prefs3, null, 2));
check('prefs.json 的 devMode 生效', prefs3.devMode === true);
check('prefs.json 的 screenHeight 生效', prefs3.screenHeight === 720);
check('未覆盖的 screenWidth 用默认', prefs3.screenWidth === 1600);

fs.rmSync(dir, { recursive: true, force: true });
console.log(`\n${failures === 0 ? '全部通过 ✓' : `${failures} 项失败 ✗`}`);
process.exit(failures === 0 ? 0 : 1);
