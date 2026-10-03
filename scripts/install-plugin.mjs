#!/usr/bin/env node
/**
 * 把 dsh-rimworld 插件装进当前 DSH profile。
 *
 * 做的事（都会先备份）：
 *   1. 复制 plugin/dsh-rimworld → <profile>/node_modules/dsh-rimworld
 *   2. 把 "dsh-rimworld" 加进 <profile>/package.json 的 dependencies 和 dsh.profile.bundles
 *   3. 把带 config 的行追加到 <profile>/cordis.patch.yml
 *
 * 用法：
 *   node scripts/install-plugin.mjs            # 安装
 *   node scripts/install-plugin.mjs --dry-run  # 只看会改什么，不动文件
 */
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import { fileURLToPath } from 'node:url';

const WS = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const SRC = path.join(WS, 'plugin', 'dsh-rimworld');
const DRY = process.argv.includes('--dry-run');

const DSH_HOME = process.env.DSH_HOME || path.join(os.homedir(), '.dsh');
const PROFILE_NAME = process.env.DSH_PROFILE || 'desktop';
const PROFILE_DIR = process.env.DSH_PROFILE_DIR || path.join(DSH_HOME, 'profiles', PROFILE_NAME);

const GAME_ROOT = process.env.RIMWORLD_ROOT || 'D:\\SteamLibrary\\steamapps\\common\\RimWorld';
const LIBRARY_PATH = path.join(WS, 'src');

const CONFIG_ROW = `
# ── dsh-rimworld：RimWorld 自动测试桥接（由 install-plugin.mjs 添加）──
# libraryPath 指向仓库的 src/；gameRoot 是游戏安装目录；profile 是隔离存档目录名。
- id: rimworld
  name: dsh-rimworld
  config:
    libraryPath: '${LIBRARY_PATH.replace(/\\/g, '\\\\')}'
    gameRoot: '${GAME_ROOT.replace(/\\/g, '\\\\')}'
    profile: 'vanilla'
`;

function log(...a) {
  console.log(...a);
}

function backup(file) {
  if (!fs.existsSync(file)) return null;
  const stamp = new Date().toISOString().replace(/[:.]/g, '-');
  const dest = `${file}.bak-${stamp}`;
  if (!DRY) fs.copyFileSync(file, dest);
  return dest;
}

function main() {
  log(`DSH profile: ${PROFILE_DIR}`);
  log(`插件源:      ${SRC}`);
  log(`游戏目录:    ${GAME_ROOT}`);
  log(`库路径:      ${LIBRARY_PATH}`);
  log(DRY ? '\n=== DRY RUN（不会修改任何文件） ===' : '\n=== 开始安装 ===');

  if (!fs.existsSync(SRC)) throw new Error(`找不到插件源目录：${SRC}`);
  if (!fs.existsSync(PROFILE_DIR)) throw new Error(`找不到 DSH profile：${PROFILE_DIR}`);

  // 1. 复制插件
  const destDir = path.join(PROFILE_DIR, 'node_modules', 'dsh-rimworld');
  if (fs.existsSync(destDir)) {
    log(`  覆盖已存在的 ${destDir}`);
    if (!DRY) fs.rmSync(destDir, { recursive: true, force: true });
  }
  if (!DRY) {
    fs.mkdirSync(path.dirname(destDir), { recursive: true });
    fs.cpSync(SRC, destDir, { recursive: true });
  }
  log(`  ✓ 插件已复制到 ${destDir}`);

  // 2. package.json：dependencies + dsh.profile.bundles
  const pkgPath = path.join(PROFILE_DIR, 'package.json');
  const raw = fs.readFileSync(pkgPath, 'utf8');
  const pkg = JSON.parse(raw);
  const bak1 = backup(pkgPath);
  if (bak1) log(`  · 已备份 package.json → ${path.basename(bak1)}`);

  pkg.dependencies = pkg.dependencies || {};
  if (!pkg.dependencies['dsh-rimworld']) {
    pkg.dependencies['dsh-rimworld'] = 'file:./node_modules/dsh-rimworld';
  }
  pkg.dsh = pkg.dsh || {};
  pkg.dsh.profile = pkg.dsh.profile || {};
  const bundles = (pkg.dsh.profile.bundles = pkg.dsh.profile.bundles || []);
  if (!bundles.includes('dsh-rimworld')) bundles.push('dsh-rimworld');
  if (!DRY) fs.writeFileSync(pkgPath, JSON.stringify(pkg, null, 2) + '\n', 'utf8');
  log(`  ✓ package.json 已登记 dsh-rimworld（bundles 现在 ${bundles.length} 项）`);

  // 3. cordis.patch.yml：追加配置行
  const patchPath = path.join(PROFILE_DIR, 'cordis.patch.yml');
  let patch = fs.existsSync(patchPath) ? fs.readFileSync(patchPath, 'utf8') : '# dsh profile patch layer\n';
  const bak2 = backup(patchPath);
  if (bak2) log(`  · 已备份 cordis.patch.yml → ${path.basename(bak2)}`);

  if (/^\s*-\s*id:\s*rimworld\s*$/m.test(patch)) {
    log('  · cordis.patch.yml 里已有 rimworld 行，跳过（如需改配置请手动编辑）');
  } else {
    if (!DRY) fs.writeFileSync(patchPath, patch.replace(/\s*$/, '\n') + CONFIG_ROW, 'utf8');
    log('  ✓ cordis.patch.yml 已追加 rimworld 配置行');
  }

  log('');
  if (DRY) {
    log('DRY RUN 结束。去掉 --dry-run 即真正安装。');
  } else {
    log('安装完成。');
    log('');
    log('接下来：');
    log('  1. 重启 DeepSeek Harness（插件在启动时加载）');
    log('  2. 新会话里应该能看到 rimworld_status / rimworld_launch / rimworld_call /');
    log('     rimworld_smoke / rimworld_tools / rimworld_logs / rimworld_stop 这几个工具');
    log('  3. 第一次用建议：rimworld_status → rimworld_launch → rimworld_smoke');
    log('');
    log('回滚：node scripts/uninstall-plugin.mjs');
  }
}

main();
