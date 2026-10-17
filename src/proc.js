/**
 * 无管道（pipe-free）子进程执行。
 *
 * 为什么不用 child_process 默认的管道 stdio：
 * 在受限的 Windows 沙箱里，创建匿名管道会被拒绝（spawn EPERM）。把子进程的
 * stdout/stderr 直接重定向到临时文件则不受影响，行为与管道等价。
 * 附带好处：不需要 shell、不需要引号转义规则，参数按数组原样传递。
 */
import { spawn } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

/**
 * 运行一个程序、等待结束、拿回它的输出。
 * @param {string} cmd
 * @param {string[]} args
 * @param {{timeoutMs?:number, cwd?:string, env?:object}} [opts]
 * @returns {Promise<{code:number, stdout:string, stderr:string}>}
 */
export function capture(cmd, args = [], { timeoutMs = 20_000, cwd, env } = {}) {
  return new Promise((resolve, reject) => {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'rimtest-'));
    const outFile = path.join(dir, 'stdout.txt');
    const errFile = path.join(dir, 'stderr.txt');
    const outFd = fs.openSync(outFile, 'w');
    const errFd = fs.openSync(errFile, 'w');

    let settled = false;
    let child;
    let timer;

    const closeFds = () => {
      try {
        fs.closeSync(outFd);
      } catch {}
      try {
        fs.closeSync(errFd);
      } catch {}
    };
    const cleanDir = () => {
      try {
        fs.rmSync(dir, { recursive: true, force: true });
      } catch {}
    };
    // 顺序很重要：先关 fd（内容落盘）→ 再读 → 再删目录
    const finish = (fn) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      closeFds();
      let stdout = '';
      let stderr = '';
      try {
        stdout = fs.readFileSync(outFile, 'utf8');
      } catch {}
      try {
        stderr = fs.readFileSync(errFile, 'utf8');
      } catch {}
      cleanDir();
      fn(stdout, stderr);
    };

    try {
      child = spawn(cmd, args, {
        cwd,
        env,
        windowsHide: true,
        stdio: ['ignore', outFd, errFd],
      });
    } catch (err) {
      settled = true;
      closeFds();
      cleanDir();
      return reject(err);
    }

    timer = setTimeout(() => {
      try {
        child.kill();
      } catch {}
      finish((stdout, stderr) =>
        reject(
          Object.assign(new Error(`${cmd} 执行超时（${timeoutMs}ms）`), {
            stdout,
            stderr,
            timedOut: true,
          }),
        ),
      );
    }, timeoutMs);

    child.on('error', (err) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      closeFds();
      cleanDir();
      reject(err);
    });

    child.on('close', (code) => {
      finish((stdout, stderr) => resolve({ code, stdout, stderr }));
    });
  });
}

/**
 * 启动一个长期运行的进程并立刻返回；不捕获它的输出。
 * 用于拉起游戏这类「启动后就交给它自己跑」的场景。
 */
export function spawnDetached(cmd, args = [], { cwd, env, hideWindow = false } = {}) {
  const child = spawn(cmd, args, {
    cwd,
    env,
    detached: true,
    stdio: 'ignore',
    windowsHide: hideWindow,
  });
  child.unref();
  return child;
}

/** 用 PID 判断进程是否还活着（纯 Node，不启动任何子进程） */
export function isAlive(pid) {
  if (!pid) return false;
  try {
    process.kill(pid, 0);
    return true;
  } catch (err) {
    return err.code === 'EPERM'; // 存在但没权限发信号，仍算活着
  }
}

/** 温和结束进程；返回是否成功发出信号 */
export function killGraceful(pid) {
  try {
    process.kill(pid);
    return true;
  } catch {
    return false;
  }
}

/** 强制结束进程 */
export function killForce(pid) {
  try {
    process.kill(pid, 'SIGKILL');
    return true;
  } catch {
    return false;
  }
}

/**
 * 列出指定映像名的进程（Windows，走 tasklist，输出重定向到文件而非管道）。
 *
 * 注意：在受限沙箱下 tasklist 会返回 "ERROR: Access denied"。
 * 这种「查不到」和「确实没有」是两回事，调用方必须区分，否则会把
 * 正在运行的进程误判成已退出。所以这里抛出一个带 code 的错误。
 */
export async function listProcessesByName(imageName) {
  const { code, stdout, stderr } = await capture(
    'tasklist.exe',
    ['/FI', `IMAGENAME eq ${imageName}`, '/FO', 'CSV', '/NH'],
    { timeoutMs: 20_000 },
  );
  if (/access is denied|access denied/i.test(stdout + stderr)) {
    throw Object.assign(new Error('tasklist 被拒绝访问（沙箱限制），无法枚举进程'), {
      code: 'EPROCENUM',
    });
  }
  if (code !== 0 && !stdout.trim()) {
    throw Object.assign(new Error(`tasklist 失败(code=${code}): ${stderr.trim() || '无输出'}`), {
      code: 'EPROCENUM',
    });
  }
  const procs = [];
  for (const line of stdout.split(/\r?\n/)) {
    const m = /^"([^"]+)","(\d+)"/.exec(line.trim());
    if (m) procs.push({ name: m[1], pid: Number(m[2]) });
  }
  return procs;
}

/** 枚举进程，但把「枚举不了」降级成 known:false，而不是抛错 */
export async function tryListProcessesByName(imageName) {
  try {
    return { known: true, processes: await listProcessesByName(imageName) };
  } catch (err) {
    if (err.code === 'EPROCENUM') return { known: false, processes: [], reason: err.message };
    throw err;
  }
}

/**
 * 兜底枚举：用 Windows PowerShell 的 `Get-Process` 拿 PID 列表。
 *
 * 2026-10-13 新增。背景：本机沙箱下 `tasklist` 被拒绝访问，但 `Get-Process` **能用**
 * （实测 224ms、exit 0、拿到 PID 29908）。以前只有 tasklist 一条路，于是"查不到"
 * 被当成"没在跑"，让 `cli.js status` 报了假阴性，有人据此写了"游戏未运行"的结论。
 *
 * 为什么是**兜底**而不是首选：它要起一个 powershell.exe 子进程（约 0.2~1s），
 * 比 tasklist 贵；只有 tasklist 不可用时才值得付这个代价。
 *
 * 为什么能拿它当判据：`Get-Process -Name X -ErrorAction SilentlyContinue` 在
 * **没有**该进程时返回空（这是"确认没有"），只有在 PowerShell 自己失败时才输出 stderr /
 * 非 0 退出码；后者按 EPROCENUM 抛出（"查不到"），调用方必须区别对待。
 *
 * 只支持按映像名取 PID（不取 ProcessName），这样命令里不会出现 `$_`，
 * 免得经过多层引号转义后语义漂移。映像名先过白名单，绝不把它拼进命令。
 */
export async function listProcessesByPowerShell(imageName, { timeoutMs = 8000 } = {}) {
  const base = String(imageName).replace(/\.exe$/i, '');
  if (!/^[A-Za-z0-9_.\-]+$/.test(base)) {
    throw Object.assign(new Error(`映像名含不安全字符，拒绝拼进 PowerShell 命令：${imageName}`), {
      code: 'EPROCENUM',
    });
  }
  const { code, stdout, stderr } = await capture(
    'powershell.exe',
    [
      '-NoProfile',
      '-NonInteractive',
      '-Command',
      `(Get-Process -Name '${base}' -ErrorAction SilentlyContinue).Id -join [char]10`,
    ],
    { timeoutMs },
  );
  if (code !== 0 || stderr.trim()) {
    throw Object.assign(
      new Error(`Get-Process 兜底枚举失败（code=${code}）：${stderr.trim().slice(0, 200) || '无输出'}`),
      { code: 'EPROCENUM' },
    );
  }
  const procs = [];
  for (const line of stdout.split(/\r?\n/)) {
    const pid = Number(line.trim());
    if (Number.isInteger(pid) && pid > 0) procs.push({ name: imageName, pid });
  }
  return procs;
}
