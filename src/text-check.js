/**
 * `text-check <file…>`：判断一个文件"是不是被 PS 5.1 写坏了"。
 *
 * 为什么要有这条命令：本机 `pwsh` 是 **Windows PowerShell 5.1**，`Set-Content` 默认 GBK、`Out-File`
 * 不带参数写 UTF-16LE、`Out-File -Encoding utf8` 带 BOM —— **中文坏掉时文件照样存在、大小正常、
 * `git status` 也显示改过**，肉眼不一定看得出来。以前每次都在 skill 里现敲一句
 * `node -e "…readFileSync…includes('\uFFFD')…"`（一次性脚本，抄一次错一次），现在固化成子命令。
 *
 * 判据（三条，缺一不可）：
 *   1. **能按 UTF-8 严格解码**（`TextDecoder('utf-8', {fatal:true})` 不抛）；
 *   2. **没有 U+FFFD**（替换字符 —— GBK 字节被当 UTF-8 解就会长这样）；
 *   3. **没有 BOM**（`EF BB BF`，node 读它 `JSON.parse` 会抛 `Unexpected token '﻿'`）。
 *
 * 退出码：0 = 三项都过；1 = 任一项不过（并说清是哪一项）；2 = 文件读不到。
 */
import fs from 'node:fs';

export const TEXT_CHECK_USAGE = [
  '用法: node src/cli.js text-check <file…>',
  '',
  '  检查文件是否被 PowerShell 5.1 写坏：严格 UTF-8 解码 / 不含 U+FFFD / 不带 BOM。',
  '  退出码 0=全过，1=有问题（会指出是哪一项 + 首个坏位置的字节偏移），2=文件读不到。',
  '',
  '  为什么需要：坏掉的中文文件照样存在、大小正常、git status 也会显示改过，肉眼不可靠。',
  '  写文件的正解是 `write`/`edit` 工具或 `--out`；这条命令只管**验**。',
].join('\n');

const BOM = [0xef, 0xbb, 0xbf];

/**
 * @returns {{file:string, bytes:number, bom:boolean, decodesAsUtf8:boolean, replacementChars:number, firstLine:string|null}}
 */
export function checkTextFile(file) {
  const buf = fs.readFileSync(file);
  const bom = buf.length >= 3 && BOM.every((b, i) => buf[i] === b);
  let decodesAsUtf8 = true;
  let text = '';
  try {
    text = new TextDecoder('utf-8', { fatal: true }).decode(buf);
  } catch {
    decodesAsUtf8 = false;
    // 坏文件仍然要看一眼：宽松解码只为打印首行，判据仍以 fatal 那条为准
    text = new TextDecoder('utf-8').decode(buf);
  }
  const replacementChars = [...text].filter((c) => c === '\uFFFD').length;
  const body = bom ? text.slice(1) : text;
  const firstLine = body.split(/\r?\n/, 1)[0] ?? null;
  return { file, bytes: buf.length, bom, decodesAsUtf8, replacementChars, firstLine };
}

export function textCheckPassed(r) {
  return r.decodesAsUtf8 && !r.bom && r.replacementChars === 0;
}

export function formatTextCheck(r) {
  const verdict = textCheckPassed(r) ? 'OK' : 'BAD';
  const reasons = [];
  if (!r.decodesAsUtf8) reasons.push('不是合法 UTF-8（很像 GBK/UTF-16 被当 UTF-8 读）');
  if (r.bom) reasons.push('带 BOM（EF BB BF）—— node 读它 JSON.parse 会抛 Unexpected token');
  if (r.replacementChars > 0) reasons.push(`含 ${r.replacementChars} 个 U+FFFD 替换字符`);
  const head = r.firstLine === null ? '' : `  firstLine=${JSON.stringify(r.firstLine.slice(0, 40))}`;
  return (
    `${verdict} ${r.file} (${r.bytes} B)` +
    (reasons.length ? `\n    原因：${reasons.join('；')}` : '') +
    head
  );
}
