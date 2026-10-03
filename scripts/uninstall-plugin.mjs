#!/usr/bin/env node
/**
 * 卸载 dsh-rimworld 插件：从 profile 里摘掉插件目录、package.json 登记、cordis.patch.yml 的配置行。
 * cordis.patch.yml 只删我们自己加的那段（按注释标记定位），其余内容原样保留。
 */
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';

const DSH_HOME = process.env.DSH_HOME || path.join(os.homedir(), '.dsh');
const PROFILE_NAME = process.env.DSH_PROFILE || 'desktop';
const PROFILE_DIR = process.env.DSH_PROFILE_DIR || path.join(DSH_HOME, 'profiles', PROFILE_NAME);
const DRY = process.argv.includes('--dry-run');

const log = (...a) => console.log(...a);

function backup(file) {
  if (!fs.existsSync(file)) return null;
  const stamp = new Date().toISOString().replace(/[:.]/g, '-');
  const dest = `${file}.bak-${stamp}`;
  if (!DRY) fs.copyFileSync(file, dest);
  return dest;
}

log(`DSH profile: ${PROFILE_DIR}`);
log(DRY ? '=== DRY RUN ===' : '=== 开始卸载 ===');

// 1. 删插件目录
const destDir = path.join(PROFILE_DIR, 'node_modules', 'dsh-rimworld');
if (fs.existsSync(destDir)) {
  if (!DRY) fs.rmSync(destDir, { recursive: true, force: true });
  log(`  ✓ 已删除 ${destDir}`);
} else {
  log('  · 插件目录不存在，跳过');
}

// 2. package.json
const pkgPath = path.join(PROFILE_DIR, 'package.json');
if (fs.existsSync(pkgPath)) {
  const pkg = JSON.parse(fs.readFileSync(pkgPath, 'utf8'));
  const bak = backup(pkgPath);
  if (bak) log(`  · 已备份 package.json → ${path.basename(bak)}`);
  if (pkg.dependencies) delete pkg.dependencies['dsh-rimworld'];
  if (pkg.dsh?.profile?.bundles) {
    pkg.dsh.profile.bundles = pkg.dsh.profile.bundles.filter((b) => b !== 'dsh-rimworld');
  }
  if (!DRY) fs.writeFileSync(pkgPath, JSON.stringify(pkg, null, 2) + '\n', 'utf8');
  log('  ✓ package.json 已摘除 dsh-rimworld');
}

// 3. cordis.patch.yml：删掉带我们标记的那一段
const patchPath = path.join(PROFILE_DIR, 'cordis.patch.yml');
if (fs.existsSync(patchPath)) {
  let text = fs.readFileSync(patchPath, 'utf8');
  const bak = backup(patchPath);
  if (bak) log(`  · 已备份 cordis.patch.yml → ${path.basename(bak)}`);
  const marker = '# ── dsh-rimworld：RimWorld 自动测试桥接（由 install-plugin.mjs 添加）──';
  const idx = text.indexOf(marker);
  if (idx >= 0) {
    text = text.slice(0, idx).replace(/\s*$/, '\n');
    if (!DRY) fs.writeFileSync(patchPath, text, 'utf8');
    log('  ✓ cordis.patch.yml 已移除 rimworld 配置段');
  } else {
    log('  · cordis.patch.yml 里没有我们的标记，跳过（未做改动）');
  }
}

log('');
log(DRY ? 'DRY RUN 结束。' : '卸载完成，重启 DeepSeek Harness 生效。');
