/**
 * GABP (Game Agent Bridge Protocol) v1 客户端 —— 零依赖实现。
 *
 * 协议要点（见 https://github.com/pardeike/GABP 的 SPEC/1.0）：
 *   - 传输：TCP 127.0.0.1:<port>，仅本机
 *   - 分帧：LSP 风格，`Content-Length: N\r\nContent-Type: application/json\r\n\r\n<json>`
 *   - 信封：{ v: "gabp/1", id: <uuid>, type: "request"|"response"|"event" }
 *   - 握手：请求 session/hello（带 token）→ 响应 session/welcome（带 capabilities）
 *   - 调用：tools/list、tools/call { name, arguments }
 *
 * 本文件只负责协议层，不关心 RimWorld 语义。
 */
import net from 'node:net';
import { randomUUID } from 'node:crypto';
import { EventEmitter } from 'node:events';

/** 把收到的字节流切成一条条 GABP 消息 */
class FrameDecoder {
  constructor(onMessage, onError) {
    this.buf = Buffer.alloc(0);
    this.onMessage = onMessage;
    this.onError = onError;
  }

  push(chunk) {
    this.buf = Buffer.concat([this.buf, chunk]);
    for (;;) {
      const idx = this.buf.indexOf('\r\n\r\n');
      if (idx === -1) return; // 头还没收全
      const header = this.buf.subarray(0, idx).toString('ascii');
      const m = /content-length:\s*(\d+)/i.exec(header);
      if (!m) {
        // 头非法：丢掉这段头，继续找下一个
        this.buf = this.buf.subarray(idx + 4);
        continue;
      }
      const len = Number(m[1]);
      const bodyStart = idx + 4;
      if (this.buf.length < bodyStart + len) return; // 体还没收全
      const body = this.buf.subarray(bodyStart, bodyStart + len).toString('utf8');
      this.buf = this.buf.subarray(bodyStart + len);
      try {
        this.onMessage(JSON.parse(body));
      } catch (err) {
        this.onError?.(new Error(`GABP 消息不是合法 JSON: ${err.message} (前 200 字节: ${body.slice(0, 200)})`));
      }
    }
  }
}

/** 按 GABP 分帧规则编码一条消息 */
export function encodeFrame(obj) {
  const json = Buffer.from(JSON.stringify(obj), 'utf8');
  const header = Buffer.from(
    `Content-Length: ${json.length}\r\nContent-Type: application/json\r\n\r\n`,
    'ascii',
  );
  return Buffer.concat([header, json]);
}

export class GabpError extends Error {
  constructor(code, message, data, method) {
    super(message);
    this.name = 'GabpError';
    this.code = code;
    this.data = data;
    this.method = method;
  }
}

export class GabpClient extends EventEmitter {
  /**
   * @param {object} opts
   * @param {string} opts.port      游戏内桥接监听的端口
   * @param {string} opts.token     握手令牌（来自游戏日志）
   * @param {string} [opts.host]    默认 127.0.0.1
   * @param {number} [opts.requestTimeoutMs] 单次请求超时，默认 60s。
   *   注意：桥接把调用排在游戏主线程上，某个调用一旦挂住，后续调用会一起挂住。
   *   所以超时必须给得有界；需要长时间的操作（开殖民地、play_for）单独传更大的值。
   */
  constructor({
    host = '127.0.0.1',
    port,
    token,
    bridgeVersion = 'dsh-rimworld/0.1.0',
    launchId = randomUUID(),
    requestTimeoutMs = 60_000,
  } = {}) {
    super();
    if (!port) throw new Error('GabpClient 需要 port');
    if (!token) throw new Error('GabpClient 需要 token');
    this.host = host;
    this.port = Number(port);
    this.token = token;
    this.bridgeVersion = bridgeVersion;
    this.launchId = launchId;
    this.requestTimeoutMs = requestTimeoutMs;

    this.socket = null;
    this.decoder = null;
    this.pending = new Map(); // id -> { resolve, reject, timer, method }
    this.welcome = null;
    this.closed = false;
  }

  /** 建立 TCP 连接并完成 session/hello 握手，返回 session/welcome 的 result */
  connect({ timeoutMs = 20_000 } = {}) {
    return new Promise((resolve, reject) => {
      const sock = net.connect({ host: this.host, port: this.port });
      sock.setNoDelay(true);

      const failHandshake = (err) => {
        sock.destroy();
        reject(err);
      };

      sock.setTimeout(timeoutMs, () => {
        failHandshake(new Error(`连接 GABP ${this.host}:${this.port} 超时（${timeoutMs}ms）`));
      });

      sock.once('error', (err) => {
        failHandshake(new Error(`无法连接 GABP ${this.host}:${this.port} —— ${err.message}`));
      });

      sock.once('connect', async () => {
        sock.setTimeout(0);
        sock.removeAllListeners('error');

        this.socket = sock;
        this.decoder = new FrameDecoder(
          (msg) => this._onMessage(msg),
          (err) => this.emit('protocolError', err),
        );
        sock.on('data', (c) => this.decoder.push(c));
        sock.on('error', (err) => this.emit('socketError', err));
        sock.on('close', () => {
          this.closed = true;
          for (const [, p] of this.pending) {
            clearTimeout(p.timer);
            p.reject(new Error(`GABP 连接已关闭，未完成的调用: ${p.method}`));
          }
          this.pending.clear();
          this.emit('close');
        });

        try {
          const result = await this.request('session/hello', {
            token: this.token,
            bridgeVersion: this.bridgeVersion,
            platform: process.platform === 'win32' ? 'windows' : process.platform,
            launchId: this.launchId,
          });
          this.welcome = result;
          this.emit('ready', result);
          resolve(result);
        } catch (err) {
          failHandshake(err);
        }
      });
    });
  }

  _onMessage(msg) {
    if (!msg || typeof msg !== 'object') return;

    if (msg.type === 'event') {
      this.emit('event', msg);
      if (msg.channel) this.emit(`event:${msg.channel}`, msg.payload, msg);
      return;
    }

    const entry = this.pending.get(msg.id);
    if (!entry) {
      // 未知 id：多半是服务端主动推的东西，记下来不致命
      this.emit('orphan', msg);
      return;
    }
    this.pending.delete(msg.id);
    clearTimeout(entry.timer);

    if (msg.error) {
      entry.reject(new GabpError(msg.error.code, msg.error.message, msg.error.data, entry.method));
    } else {
      entry.resolve(msg.result);
    }
  }

  /** 发一条请求并等待响应 */
  request(method, params, { timeoutMs } = {}) {
    if (!this.socket) return Promise.reject(new Error('GABP 尚未连接'));
    const id = randomUUID();
    const frame = { v: 'gabp/1', id, type: 'request', method };
    if (params !== undefined) frame.params = params;

    const ms = timeoutMs ?? this.requestTimeoutMs;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        // 这个错误意味着桥接很可能已经被这次调用拖住了：后续调用也会挂。
        // 上层应当把它当成"会话已废"，重启游戏而不是继续重试。
        reject(
          Object.assign(
            new Error(
              `GABP 调用超时（${ms}ms）: ${method} —— 桥接可能已被该操作挂住，后续调用也会失败，建议重启游戏实例`,
            ),
            { code: 'GABP_TIMEOUT', method, timeoutMs: ms },
          ),
        );
      }, ms);
      this.pending.set(id, { resolve, reject, timer, method });
      this.socket.write(encodeFrame(frame));
    });
  }

  /** 列出游戏暴露的全部工具 */
  async listTools() {
    const res = await this.request('tools/list', {});
    if (Array.isArray(res)) return res;
    if (res && Array.isArray(res.tools)) return res.tools;
    return [];
  }

  /**
   * 调用一个游戏内工具。
   * RimBridgeServer 的响应结构一般是 { success, result } 或直接返回载荷，
   * 这里原样返回，由上层决定怎么读。
   */
  callTool(name, args = {}, opts) {
    return this.request('tools/call', { name, arguments: args }, opts);
  }

  close() {
    if (this.socket) {
      this.socket.end();
      this.socket.destroy();
      this.socket = null;
    }
  }
}

/**
 * 便捷函数：连接 + 握手，返回 { client, welcome }。
 */
export async function connectGabp(info, opts = {}) {
  const client = new GabpClient({ ...info, ...opts });
  const welcome = await client.connect(opts);
  return { client, welcome };
}
