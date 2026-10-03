#!/usr/bin/env node
/**
 * 隔离测试配置的 ModsConfig 管理器。
 *
 * 用途：不同测试场景需要不同的 mod 组合（纯净基线 / 加被测 mod / 复刻主力配置）。
 *
 * 用法：
 *   node scripts/modsconfig.mjs list                    # 看当前启用列表
 *   node scripts/modsconfig.mjs add dsh.autotest        # 追加一个
 *   node scripts/modsconfig.mjs remove dsh.autotest     # 移除
 *   node scripts/modsconfig.mjs set a,b,c               # 精确设定
 *   node scripts/modsconfig.mjs baseline                # 恢复纯净基线
 *   node scripts/modsconfig.mjs copy-main               # 复刻主力配置的 mod 列表
 *   ...都可加 --profile <名字>
 */
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import { WS } from '../src/launcher.js';

/** 纯净基线：原版 + 全 6 DLC + Harmony + 桥接 */
export const BASELINE = [
  'brrainz.harmony',
  'ludeon.rimworld',
  'ludeon.rimworld.royalty',
  'ludeon.rimworld.ideology',
  'ludeon.rimworld.biotech',
  'ludeon.rimworld.anomaly',
  'ludeon.rimworld.odyssey',
  'brrainz.rimbridgeserver',
];

/**
 * 鼠族测试环境。
 * 依赖链：Harmony → Core+DLC → XML Extensions → HAR → NewRatkinPlus
 * （HAR 只把 XML Extensions 写在 loadAfter 里，不写 modDependencies，容易漏）
 * 工具类（RimBridgeServer / 测试 mod / CE）放最后。
 */
export const RATKIN = [
  'brrainz.harmony',
  'ludeon.rimworld',
  'ludeon.rimworld.royalty',
  'ludeon.rimworld.ideology',
  'ludeon.rimworld.biotech',
  'ludeon.rimworld.anomaly',
  'ludeon.rimworld.odyssey',
  'imranfish.xmlextensions',
  'erdelf.HumanoidAlienRaces',
  'Solaris.RatkinRaceMod',
  'brrainz.rimbridgeserver',
  'dsh.autotest',
  'void.charactereditor',
];

/** 命名预设，用 `node scripts/modsconfig.mjs preset <名字>` 应用 */
export const PRESETS = { baseline: BASELINE, ratkin: RATKIN };

const DLC = [
  'ludeon.rimworld',
  'ludeon.rimworld.royalty',
  'ludeon.rimworld.ideology',
  'ludeon.rimworld.biotech',
  'ludeon.rimworld.anomaly',
  'ludeon.rimworld.odyssey',
];

const argv = process.argv.slice(2);
const cmd = argv[0];
const profileIdx = argv.indexOf('--profile');
const profile = profileIdx >= 0 ? argv[profileIdx + 1] : 'vanilla';
const positional = argv.slice(1).filter((a) => a !== '--profile' && a !== profile);
const arg = positional[0];

const dir = path.join(WS, 'profiles', profile, 'Config');
const file = path.join(dir, 'ModsConfig.xml');
const GAME_VERSION = '1.6.4871 rev590';

export function readConfig() {
  if (!fs.existsSync(file)) return { version: GAME_VERSION, activeMods: [], knownExpansions: DLC };
  const xml = fs.readFileSync(file, 'utf8');
  const version = /<version>([^<]*)<\/version>/.exec(xml)?.[1] ?? GAME_VERSION;
  const grab = (tag) => {
    const m = new RegExp(`<${tag}>([\\s\\S]*?)</${tag}>`).exec(xml);
    if (!m) return [];
    return [...m[1].matchAll(/<li>([^<]*)<\/li>/g)].map((x) => x[1].trim()).filter(Boolean);
  };
  return { version, activeMods: grab('activeMods'), knownExpansions: grab('knownExpansions') };
}

export function writeConfig({ version, activeMods, knownExpansions }) {
  const lines = [
    '<?xml version="1.0" encoding="utf-8"?>',
    '<ModsConfigData>',
    `  <version>${version}</version>`,
    '  <activeMods>',
    ...activeMods.map((m) => `    <li>${m}</li>`),
    '  </activeMods>',
    '  <knownExpansions>',
    ...knownExpansions.map((m) => `    <li>${m}</li>`),
    '  </knownExpansions>',
    '</ModsConfigData>',
    '',
  ];
  fs.mkdirSync(dir, { recursive: true });
  fs.writeFileSync(file, lines.join('\n'), 'utf8');
}

function mainConfigPath() {
  const ll = path.join(
    os.homedir(),
    'AppData/LocalLow/Ludeon Studios/RimWorld by Ludeon Studios/Config/ModsConfig.xml',
  );
  return ll;
}

const cfg = readConfig();

switch (cmd) {
  case 'list': {
    console.log(`profile: ${profile}`);
    console.log(`文件: ${file}`);
    console.log(`版本: ${cfg.version}`);
    console.log(`启用 ${cfg.activeMods.length} 项:`);
    for (const m of cfg.activeMods) console.log(`  ${m}`);
    break;
  }

  case 'add': {
    const ids = positional.flatMap((a) => a.split(',')).filter(Boolean);
    const added = [];
    for (const id of ids) {
      if (!cfg.activeMods.includes(id)) {
        cfg.activeMods.push(id);
        added.push(id);
      }
    }
    writeConfig(cfg);
    console.log(`已添加: ${added.join(', ') || '（无变化）'}`);
    console.log(`现在 ${cfg.activeMods.length} 项`);
    break;
  }

  case 'remove': {
    const ids = new Set(positional.flatMap((a) => a.split(',')).filter(Boolean));
    const before = cfg.activeMods.length;
    cfg.activeMods = cfg.activeMods.filter((m) => !ids.has(m));
    writeConfig(cfg);
    console.log(`已移除 ${before - cfg.activeMods.length} 项，现在 ${cfg.activeMods.length} 项`);
    break;
  }

  case 'set': {
    const ids = (arg ?? '').split(',').filter(Boolean);
    cfg.activeMods = ids;
    writeConfig(cfg);
    console.log(`已设定 ${ids.length} 项`);
    break;
  }

  case 'baseline': {
    cfg.activeMods = [...BASELINE];
    cfg.knownExpansions = [...DLC];
    writeConfig(cfg);
    console.log(`已恢复纯净基线（${BASELINE.length} 项）`);
    break;
  }

  case 'preset': {
    const name = arg;
    const p = PRESETS[name];
    if (!p) throw new Error(`未知预设「${name}」。可选：${Object.keys(PRESETS).join(', ')}`);
    cfg.activeMods = [...p];
    cfg.knownExpansions = [...DLC];
    writeConfig(cfg);
    console.log(`已应用预设「${name}」（${p.length} 项）—— 记得重启游戏`);
    break;
  }

  case 'copy-main': {
    const p = mainConfigPath();
    if (!fs.existsSync(p)) throw new Error(`找不到主力配置: ${p}`);
    const xml = fs.readFileSync(p, 'utf8');
    const grab = (tag) => {
      const m = new RegExp(`<${tag}>([\\s\\S]*?)</${tag}>`).exec(xml);
      if (!m) return [];
      return [...m[1].matchAll(/<li>([^<]*)<\/li>/g)].map((x) => x[1].trim()).filter(Boolean);
    };
    cfg.activeMods = grab('activeMods');
    cfg.knownExpansions = grab('knownExpansions');
    cfg.version = /<version>([^<]*)<\/version>/.exec(xml)?.[1] ?? cfg.version;
    writeConfig(cfg);
    console.log(`已复刻主力配置：${cfg.activeMods.length} 项`);
    break;
  }

  default:
    console.log(
      [
        '用法:',
        '  node scripts/modsconfig.mjs list',
        '  node scripts/modsconfig.mjs add <id>[,<id>...]',
        '  node scripts/modsconfig.mjs remove <id>[,<id>...]',
        '  node scripts/modsconfig.mjs set <id>,<id>,...',
        '  node scripts/modsconfig.mjs baseline',
        '  node scripts/modsconfig.mjs preset <baseline|ratkin>',
        '  node scripts/modsconfig.mjs copy-main',
        '  均可加 --profile <名字>',
      ].join('\n'),
    );
}
