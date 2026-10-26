# launcher cwd 修复报告（2026-10-25）

## 结论

1. **原来有 cwd**：`src\launcher.js:938` 的 `spawnDetached(exe, args, { cwd: root })`，`root = gameRoot()`（exe 所在目录），自初始提交 `e86e9e6` 起就在；`src\proc.js:112-122` 的 `spawnDetached` 也原样把它传给 `child_process.spawn`。⇒ 任务前提「没有设置工作目录」与代码不符，**缺陷不在 launcher 这条路径上**。
2. **改成什么**：行为不变（本来就对）。本次只在 `cwd: root` 上方加 2 行中文注释，把「为什么必须钉死游戏根」写成代码约定，防止后人当冗余删掉。
3. **自测结果**：截获 `child_process.spawn` 实测，1.6 风格假根（`%TEMP%\rw-cwd-selftest`）与 1.7 真根两条路径均 `cwd === gameRoot() === path.dirname(exe)` → **true**；`detached/stdio/windowsHide` 仍为 `true / ignore / true`。
4. **影响范围**：仅 `src\launcher.js` +2 行注释，无逻辑变化；未启动游戏、未 stop、未动 1.6 目录、未 commit。自测产生的临时 profile/记录已删除。

## 改动前后

改动前（`HEAD` 版，933-937 行）：

```js
  let child;
  try {
    child = spawnDetached(exe, args, {
      cwd: root,
      hideWindow: !visible,
```

改动后（935-939 行）：

```js
    child = spawnDetached(exe, args, {
      // cwd 必须显式钉在游戏根（exe 所在目录），不能继承本进程的工作目录：继承来的路径含中文时，
      // Mono 会在 Harmony 初始化里抛 Illegal byte sequence，Harmony 全废（1.6 实测 A/B 见 docs\工作目录-cwd-必须指向游戏根-20261025.md）。
      cwd: root,
      hideWindow: !visible,
```

## 自测命令与输出

把 `child_process.spawn` 换掉（只截获 `RimWorld.*`，其余透传），再调真实的 `startGame()`；脚本经 stdin 交给 node，不落文件：

```powershell
$js = @'
import { createRequire } from 'node:module';
import path from 'node:path';
const require = createRequire(import.meta.url);
const cp = require('node:child_process');
const orig = cp.spawn;
let captured = null;
cp.spawn = function (cmd, args, opts) {
  if (String(cmd).includes('RimWorld.')) {
    captured = { cmd, cwd: opts && opts.cwd, detached: opts && opts.detached, stdio: opts && opts.stdio, windowsHide: opts && opts.windowsHide };
    return { pid: 4242, unref() {}, on() {} };
  }
  return orig.apply(this, arguments);
};
const L = await import('./src/launcher.js');
const root = L.gameRoot();
const layout = L.detectGameLayout('cwdselftest', root);
try { await L.startGame({ profile: 'cwdselftest', visible: false }); } catch (e) { console.log('startGame threw:', String(e.message).split('\n')[0]); }
console.log('spawn cwd         =', captured && captured.cwd);
console.log('cwd===gameRoot    :', Boolean(captured && captured.cwd === root));
console.log('cwd===dirname(exe):', Boolean(captured && captured.cwd === path.dirname(layout.exe)));
'@
$env:RIMWORLD_ROOT = Join-Path $env:TEMP 'rw-cwd-selftest'; $js | node --input-type=module -   # A 组
Remove-Item Env:\RIMWORLD_ROOT;                                  $js | node --input-type=module -   # B 组
```

实测输出（A 组用假根 `RimWorld.exe`+`Version.txt`，B 组用真 1.7 根）：

```
=== A) 1.6 风格假根（ASCII） ===
gameRoot()        = C:\Users\MIKANS~1\AppData\Local\Temp\rw-cwd-selftest
exe               = C:\Users\MIKANS~1\AppData\Local\Temp\rw-cwd-selftest\RimWorld.exe
spawn cwd         = C:\Users\MIKANS~1\AppData\Local\Temp\rw-cwd-selftest
cwd===gameRoot    : true
cwd===dirname(exe): true
options           : {"detached":true,"stdio":"ignore","windowsHide":true}

=== B) 默认 1.7 真根 ===
gameRoot()        = D:\SteamLibrary\steamapps\common\RimWorld
exe               = D:\SteamLibrary\steamapps\common\RimWorld\RimWorld.exe
spawn cwd         = D:\SteamLibrary\steamapps\common\RimWorld
cwd===gameRoot    : true
cwd===dirname(exe): true
options           : {"detached":true,"stdio":"ignore","windowsHide":true}
```

踩坑记录（自测本身）：先用 `import cp from 'node:child_process'` 再赋值 `cp.spawn = …` **拦不到**——
ESM 的具名导出在第一次 import 时就把原函数快照走了，仍会真的去 spawn（表现为 `spawn UNKNOWN`）。
必须用 `createRequire` 拿 CJS 对象、先打补丁、**再**动态 `import('./src/launcher.js')`。

## 为何不影响 1.7

- 1.7 与 1.6 走的是**同一个** `spawnDetached(exe, args, { cwd: root })`；`root` 就是 `gameRoot()`，
  1.7 下等于 `D:\SteamLibrary\steamapps\common\RimWorld`（纯 ASCII），cwd 一直是它。本次零逻辑改动 ⇒ 行为逐字节相同（B 组已实测确认）。
- 1.7 没暴露该错误，按任务陈述是运行时不同（.NET 宿主 vs Mono）；这属于**未验证的推测**，本报告不当结论用。

## 未验证

- **没有真启动游戏**（按要求），所以「cwd 设对后 1.6 的 Harmony 真的恢复」是**引用既有实测**（`docs\RimWorld-1.6-可运行化与验证-20261025.md` 的 M2c/M2d/M6，`Illegal byte sequence` 0、`Applied essential Harmony` 1），不是本次复测。
- 1.7 在中文 CWD 下是否也会炸：未测。
- repo 之外的启动路径（DSH 的 `rimworld_launch` 工具、手工 `Start-Process`）不经过 `src\launcher.js`，未纳入本次范围；若那两条路不设工作目录，坑照旧。
- `RIMWORLD_ROOT` 若被设成**相对路径**，cwd 会相对本进程 CWD（中文工程目录）解析，坑会回来。当前未加 `path.isAbsolute` 校验——属建议项，未实施。
