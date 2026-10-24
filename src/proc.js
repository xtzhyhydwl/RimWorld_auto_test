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

/**
 * PID 三态探针（纯 Node，不启动子进程，约 0.01ms）。
 *
 * 为什么不是布尔：Windows 上 `EPERM` 的真实含义是「这个 PID 被别的、我们拿不到权限的进程占着」，
 * 和「我们的游戏还活着」是两件事。2026-10-24 实测的故障就是：记录里 pid 被一个受保护进程复用，
 * `EPERM` 被当成「活着」⇒ `start` 一直报「该 profile 已有实例在运行」。
 * 所以这里把「存在但不可控」单独成态，调用方自己决定怎么用；要判「是不是我们的游戏」
 * 用 `verifyInstance()`，不要用这个。
 *
 * @returns {{state:'alive'|'foreign'|'none'|'unknown', code?:string}}
 *   alive   = PID 存在且我们发得动信号
 *   foreign = PID 存在但我们无权（EPERM）—— 极可能不是我们的进程
 *   none    = PID 不存在（ESRCH）
 *   unknown = 其它错误，别当成确定结论
 */
export function probePid(pid) {
  if (!pid) return { state: 'none' };
  try {
    process.kill(Number(pid), 0);
    return { state: 'alive' };
  } catch (err) {
    if (err.code === 'EPERM') return { state: 'foreign', code: err.code };
    if (err.code === 'ESRCH') return { state: 'none', code: err.code };
    return { state: 'unknown', code: err.code };
  }
}

/**
 * 用 PID 判断「我们自己的进程是否还活着」。
 *
 * 与 2026-10-13 那版的区别：`EPERM`（foreign）**不再**算活着。这样 PID 被复用后陈旧记录
 * 不会挡住 `start`；同时因为它返回 false，`stop` 里「按记录 pid 收集待杀进程」也不会
 * 把一个无关进程收进去。要更严格的身份判断用 `verifyInstance()`。
 */
export function isAlive(pid) {
  return probePid(pid).state === 'alive';
}

/**
 * 查一个 PID 的真实身份（映像名 + 完整命令行）。要起一个 powershell.exe（约 0.2~1s），
 * 只在「决定要不要相信一条记录 / 要不要杀这个 PID」时付这个代价。
 *
 * @returns {Promise<null | {pid:number, image:string, commandLine:string}>} 不存在返回 null
 */
export async function describeProcess(pid, { timeoutMs = 8000 } = {}) {
  const n = Number(pid);
  if (!Number.isInteger(n) || n <= 0) return null;
  const { code, stdout, stderr } = await capture(
    'powershell.exe',
    [
      '-NoProfile',
      '-NonInteractive',
      '-Command',
      `Get-CimInstance Win32_Process -Filter "ProcessId=${n}" | Select-Object ProcessId,Name,CommandLine | ConvertTo-Json -Compress`,
    ],
    { timeoutMs },
  );
  // 「查不到」是正常结论（进程已退出），只有 PowerShell 自己失败才算查询故障
  if (code !== 0 || stderr.trim()) {
    throw Object.assign(new Error(`查询 PID ${n} 的身份失败（code=${code}）：${stderr.trim().slice(0, 200) || '无输出'}`), {
      code: 'EPROCENUM',
    });
  }
  const text = stdout.trim();
  if (!text) return null;
  let obj;
  try {
    obj = JSON.parse(text);
  } catch {
    throw Object.assign(new Error(`查询 PID ${n} 的身份：输出不是 JSON（${text.slice(0, 120)}）`), {
      code: 'EPROCENUM',
    });
  }
  const row = Array.isArray(obj) ? obj[0] : obj;
  if (!row || !row.ProcessId) return null;
  return {
    pid: Number(row.ProcessId),
    image: String(row.Name || ''),
    commandLine: String(row.CommandLine || ''),
  };
}

/**
 * 判定「这个 PID 现在是**我们某个 profile 的游戏实例**吗」—— 多实例并发时唯一可信的判据。
 *
 * 只看 PID 是否存活不够（PID 会被复用），必须三件事同时成立：PID 找得到、命令行里带着
 * 我们给它的 `-savedatafolder=<某个 profiles 下的目录>`、且（给了 expectedProfile 时）
 * 就是那个 profile。
 *
 * 为什么只比 `profiles\<profile>` 这一段而不比整条路径：实测记录里的 `profileDir` 会带乱码
 * （`2026.7浠ュ悗鐨勬柊宸ョ▼`），整路径比较会假阴性。
 *
 * @param {number} pid
 * @param {{processesDir?:string, expectedProfile?:string}} [opts] `processesDir` 是本仓库
 *   `profiles\` 的绝对路径（默认由调用方给，proc.js 不知道工作区布局）
 * @returns {Promise<{ok:boolean, reason:string, process?:object, profile?:string|null}>}
 */
export async function verifyInstance(pid, { processesDir, expectedProfile } = {}) {
  const probe = probePid(pid);
  if (probe.state === 'none') return { ok: false, reason: `PID ${pid} 不存在` };
  if (probe.state === 'unknown') {
    return { ok: false, reason: `PID ${pid} 状态未知（${probe.code}），不当作我们的实例` };
  }
  let info;
  try {
    info = await describeProcess(pid);
  } catch (err) {
    // 查身份本身失败时不能给出「是/不是」的结论，也不能因此去杀它
    return { ok: false, reason: `PID ${pid} 身份查询失败：${err.message}` };
  }
  if (!info) return { ok: false, reason: `PID ${pid} 已退出` };
  const m = /-savedatafolder=(?:"([^"]*)"|(\S+))/i.exec(info.commandLine);
  if (!m) {
    return { ok: false, reason: `PID ${pid}（${info.image}）命令行里没有 -savedatafolder，不是本工具的实例`, process: info };
  }
  const dir = (m[1] ?? m[2]).replace(/[\\/]+$/, '');
  const profile = /[\\/]profiles[\\/]([^\\/]+)$/i.exec(dir)?.[1] ?? null;
  if (!profile) {
    return { ok: false, reason: `PID ${pid} 的存档目录不在 profiles\\ 下：${dir}`, process: info, profile: null };
  }
  if (processesDir) {
    const want = path.resolve(processesDir).toLowerCase() + path.sep;
    if (!path.resolve(dir).toLowerCase().startsWith(want)) {
      return { ok: false, reason: `PID ${pid} 的存档目录不在本仓库的 profiles\\ 下：${dir}`, process: info, profile };
    }
  }
  if (expectedProfile && profile !== expectedProfile) {
    return { ok: false, reason: `PID ${pid} 是 profile「${profile}」的实例，不是「${expectedProfile}」`, process: info, profile };
  }
  return { ok: true, reason: `PID ${pid} 是 profile「${profile}」的实例（${info.image}）`, process: info, profile };
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
