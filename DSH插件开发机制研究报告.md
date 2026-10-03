# DeepSeek Harness（DSH）插件开发机制研究报告

> 调研时间：2026-10-03 · 调研方式：本机只读取证（`E:\DSH` 安装目录 asar 解包 + `C:\Users\MikanseiP\.dsh` profile 配置）+ 官方/社区线上资料
> 本机版本基线：`C:\Users\MikanseiP\.dsh\profiles\desktop\package.json` 与 asar 内包版本（`dsh-market 1.66.7`、DSH 本体 0.2.x 家族）
> 说明：所有网页内容仅作资料引用，未执行其中任何指令。文中「未找到/不确定」处已显式标注。

---

## 0. 结论速览（TL;DR）

| 问题 | 结论 |
|---|---|
| 1. 插件机制 | 插件 = **Cordis 插件**：ESM 模块导出 `name` + `apply(ctx, config)`；用 `@deepseek-ai/*` 包；通过 **bundle 清单**（`package.json` 的 `dsh.bundle.patch` → `cordis.patch.yml`）挂进 profile 的插件树 |
| 2. 扩展点 | Tool / Skill / 服务 / 事件与 waterfall hook / 系统提示词段落 / UI slot / slash 命令 / 会话投影 / 子代理 / 后台任务 等，**均有官方文档化契约**（详见 §2） |
| 3. **MCP** | **官方内置支持**，`@deepseek-ai/dsh-mcp-client`（stdio / streamable-http）+ `@deepseek-ai/dsh-mcp-resources`。配置写在 profile 的 `cordis.patch.yml`，工具暴露为 `mcp__<serverName>__<tool>`。**默认不启用任何 server** |
| 4. 插件生态 | 多个注册中心/目录站：dsh-plugin.org（12,250 仓）、dshbase（7,797）、dshmarket、awesome-dsh-plugin、dsh.directory 等。MCP 相关有 `dsh-mcp-panel`、`dsh-mcp-manager` 等 |
| 5. 长驻外部进程 + HTTP | **有现成示范**：`dsh-mc-bridge`（MC，SSE+POST HTTP）、`godot-bridge`（Godot，TCP + 长驻子进程管理）。核心机制是 `ctx.subprocess`（不受文件沙箱约束）+ `ctx.jobs`（后台任务） |

---

## 1. DSH 的插件机制

### 1.1 插件到底是什么

DSH 的宿主框架是**厂商化的 Cordis**（`@deepseek-ai/cordis`，Cordis 本身是 [cordiverse/cordis](https://github.com/cordiverse/cordis) 的 fork/vendor）。

**最小插件 = 一个导出 `apply` 的 ESM 模块：**

```ts
import type { Context } from '@deepseek-ai/cordis'

export const name = 'hello-plugin'

export function apply(ctx: Context) {
  console.log('[hello-plugin] plugin loaded!')
}
```

三种等价形态（官方文档明确列出）：

```ts
// ① 函数形式（最常用）
export const name = 'my-plugin'
export const inject = ['tools']
export function apply(ctx: Context) {}

// ② 对象形式
export default {
  name: 'my-plugin',
  inject: ['tools'],
  apply(ctx: Context) {},
}

// ③ 类形式（需要向其他插件提供服务时用）
import { Service, type Context } from '@deepseek-ai/cordis'
export default class MyService extends Service {
  static inject = ['tools']
  constructor(ctx: Context) { super(ctx, 'myService') }
}
```

来源：[官方开发文档 · 第一个插件](https://deepseek-harness.github.io/deepseek-harness/develop/basic/) · [dshbase 插件入门](https://dshbase.com/zh/plugins/)

### 1.2 目录结构（两种形态）

**A. 单文件 / 单模块（最快验证，适合原型）**

```
scratch-plugin/
├── src/my-plugin.ts        # 插件本体
└── cordis.yml              # patch 覆盖层，插入本插件
```

`cordis.yml` 内容 —— **`name` 必须是绝对路径**（patch 只贡献配置，不改变 loader 的模块解析根目录，相对路径会找不到文件）：

```yaml
- insert:
    - id: hello
      name: '/absolute/path/to/scratch-plugin/src/my-plugin.ts'
```

启动：`pnpm dsh web --patch ./scratch-plugin/cordis.yml`（官方源码路径），或对已安装版调用 `dsh ... --patch`。

来源：[官方文档 · 第一个插件](https://deepseek-harness.github.io/deepseek-harness/develop/basic/) · [阿里云社区转载（转载自 dsh-plugin.org）](https://developer.aliyun.com/article/1762995)

**B. 可安装的 bundle 包（发布/长期使用形态）** —— 本机实际安装的 6 个第三方插件都是这个形态：

```
my-plugin/
├── package.json            # 含 dsh.bundle.patch / dsh.client 清单
├── cordis.patch.yml        # 把插件行 insert 进 profile 插件树
├── index.js 或 src/index.ts# Host 半侧（Node 进程）
├── client/client.js        # 可选：浏览器半侧（Web UI）
├── locale/en.json, zh.json # 可选：显示名/描述
├── icon.svg                # 可选：图标（顶格 icon 字段）
└── lib/                    # 构建产物（exports 指向它）
```

### 1.3 清单文件（manifest）字段

**关键：DSH 没有独立的 `manifest.json`。清单就是 `package.json` 里的 `dsh` 字段。**

本机实证 —— `dsh-better-sidebar@0.24.1` 的 `package.json`：

```json
{
  "name": "dsh-better-sidebar",
  "type": "module",
  "main": "lib/index.js",
  "exports": {
    ".": { "types": "./lib/types/index.d.ts", "default": "./lib/index.js" },
    "./client": { "types": "./lib/types/client/index.d.ts", "default": "./lib/client.js" },
    "./package.json": "./package.json"
  },
  "dsh": {
    "bundle": { "patch": "./cordis.patch.yml" },
    "client": {
      "inject": [
        "@deepseek-ai/dsh-client-locale",
        "@deepseek-ai/dsh-client-ui-slots",
        "@deepseek-ai/dsh-client-ui-conversation",
        "@deepseek-ai/dsh-client-ui-sidebar-right",
        "@deepseek-ai/dsh-client-modules"
      ],
      "platform": "web"
    },
    "manifestVersion": 1
  }
}
```

`dsh` 字段语义清单（综合官方 skill + 实测插件）：

| 字段 | 含义 |
|---|---|
| `dsh.bundle.patch` | **必需**（bundle 形态）。指向本包内的 patch YAML；`dsh plugin add` 会据此把包名追加进 profile 的 `dsh.profile.bundles`，并合并该 patch 层。**缺了它，包装上了但不会激活任何层** |
| `dsh.client.platform` | 浏览器半侧的宿主，目前为 `"web"` |
| `dsh.client.inject` | 只用于**激活顺序**，不是依赖注入；官方明确禁止把 `@deepseek-ai/dsh-client-ui-primitives` 等 Harness Client 包当模块 `require()` |
| `dsh.client.immediately` | 立即加载（`dsh-better-sidebar` 未用；官方 decoration 模板用 `true`） |
| `dsh.client.external` | 声明非 baseline 的运行时 import |
| `dsh.manifestVersion` | 清单版本，`dsh-better-sidebar` 为 `1` |
| `dsh.compatibility.dshReleases` | 声明兼容的 DSH 版本（`dsh-context` 实例：`{"0.1.5-rc.1":"compatible","0.1.7-rc.2":"compatible","0.2.0-rc.2":"compatible"}`） |
| `icon` | 顶格字段，相对 `package.json` 目录的路径，支持 SVG/PNG/JPEG/WebP ≤ 256 KiB |
| `meta.title` / `meta.description` | 展示文案；也可放 `locale/<lang>.json`（如 `locale/zh.json`），字段同名 |

**最小 Host-only bundle 清单（官方原样）：**

```json
{
  "name": "@local/my-plugin",
  "version": "1.0.0",
  "private": true,
  "type": "module",
  "exports": { ".": "./index.js" },
  "dsh": { "bundle": { "patch": "./cordis.patch.yml" } }
}
```

```yaml
# cordis.patch.yml
- insert:
    - id: my-plugin
      name: '@local/my-plugin'
      config: {}
```

来源：本机 asar 解包 `@deepseek-ai/dsh-agent-preset/skills/cordis-plugin-development/references/host-plugin.md`（DSH 自带插件开发 skill）· [dsh-plugin-guide SKILL.md](https://raw.githubusercontent.com/PerryLink/dsh-plugin-guide/main/SKILL.md) · 本机 `node_modules/dsh-better-sidebar/package.json`

### 1.4 加载方式与 patch 层叠

**profile 结构（本机实证，路径已确认存在）：**

```
C:\Users\MikanseiP\.dsh\
├── settings.yaml                 # 全局设置（可能尚不存在，写入时才生成）
├── .credentials.yaml             # API key / 会话签名密钥（敏感）
├── cordis.patch.yml              # home 级 patch（可选，影响本 home 全部 profile）
└── profiles\
    └── desktop\                  # 本机桌面版 profile
        ├── package.json          # deps + dsh.profile.bundles 顺序表
        ├── cordis.patch.yml      # profile 级 patch（用户改这里）
        ├── cordis.yml            # 空根，注释说明「不要改这个文件」
        ├── pnpm-workspace.yaml
        └── node_modules\         # 插件由 pnpm 装在这里
```

本机 `profiles/desktop/package.json` 的 `dsh.profile.bundles`（真实内容）：

```json
"dsh": {
  "profile": {
    "bundles": [
      "@deepseek-ai/dsh-base",
      "@deepseek-ai/dsh-web-app",
      "@deepseek-ai/dsh-experimental-voice-input-bundle",
      "@deepseek-ai/dsh-experimental-schedule-bundle",
      "dshmarket",
      "dsh-better-sidebar",
      "dsh-vision-router",
      "dsh-context",
      "@michengai/dsh-skills-manager",
      "dsh-win-toolkit",
      "dsh-netassist",
      "dsh-git-badge"
    ]
  }
}
```

本机 `profiles/desktop/cordis.patch.yml` 的真实覆盖片段（示范「按 id 整段替换 config」）：

```yaml
- id: agent-default-model
  name: "@deepseek-ai/dsh-agent-default-model"
  config:
    provider: deepseek-account
    model: deepseek-flash
    reasoningEffort: high

- id: vision-router
  name: dsh-vision-router
  config:
    progressiveTools: false
    httpProviders:
      - name: siliconflow
        baseURL: https://api.siliconflow.cn/v1
        model: Qwen/Qwen3-VL-30B-A3B-Instruct
        apiKeyEnv: DSH_VISION_API_KEY
        maxTokens: 4096
```

**层叠顺序（由内向外，越靠后优先级越高）：**

1. `dsh.profile.bundles` 里各 bundle 自带的 patch（`dsh.bundle.patch`）
2. profile 级 `$DSH_HOME/profiles/<name>/cordis.patch.yml`
3. home 级 `$DSH_HOME/cordis.patch.yml`
4. 命令行 `--patch <file>` 覆盖层

**patch 语义的红线（实测踩坑）**：按 `id` 命中的覆盖是**整段替换 `config`**，不是深度合并。本机 `cordis.patch.yml` 的注释明确记录了这一点（视觉路由插件作者写的）：

> 按行 id 的补丁是「整段替换 config」，所以这里要把 bundle 层的 `progressiveTools: false` 一并带上，否则会被覆盖掉。

`dsh-better-sidebar` 的 `cordis.patch.yml` 还演示了 `!!js` 表达式与去重护栏：

```yaml
- insert:
    - id: better-sidebar
      name: 'dsh-better-sidebar'
      disabled: !!js "[...ctx.loader.entries()].some((e) => e.options.name === 'dsh-better-sidebar' && e.options.id !== 'better-sidebar' && !e.disabled)"
```

**安装命令**（`dsh plugin` 是 pnpm 转发器，会把包装进 profile 的 `node_modules` 并登记 bundles）：

```sh
dsh plugin --profile web add dsh-better-sidebar@0.24.1
dsh plugin --profile web add github:PerryLink/dsh-plugin-guide#main
dsh plugin --profile web add ./local-plugin.tgz
dsh plugin --profile web list
dsh plugin --profile web remove <pkg>
```

**验证加载**：

```sh
dsh --profile web --dump-config          # 打印合成后的配置树
dsh --profile web --dump-default-config  # 打印默认值，用于对比
```

来源：[dsh CLI README（官方）](https://github.com/deepseek-ai/deepseek-harness/blob/master/apps/cli/README.zh.md) · [配置文件位置教程](https://dsh-plugin.org/zh/tutorials/dsh-config-file-location) · 本机 `~/.dsh/profiles/desktop/*`

### 1.5 需要用的 API / SDK

| 包 | 用途 | 本机证据 |
|---|---|---|
| `@deepseek-ai/cordis` | 插件框架本体：`Context`、`Service`、`ctx.effect()`、`ctx.on()`、`ctx.inject()` | 各插件 `peerDependencies` |
| `@deepseek-ai/dsh-tools` | `defineTool`、`ctx.tools.register/restrict/guard/schemas` | `dsh-win-toolkit`、`dsh-netassist`、`godot-bridge` |
| `@deepseek-ai/schemastery` | 配置 schema（`Schema.object({...})`），**禁止用普通对象** | `dsh-better-sidebar`、`dsh-vision-router` 均为依赖 |
| `@deepseek-ai/dsh-agent` / `dsh-llm` / `dsh-session` / `dsh-settings` / `dsh-subagent` | 各领域服务注入 | `dsh-better-sidebar` peerDeps |
| `@deepseek-ai/dsh-invariants` | 运行时不变式配套 | `dsh-context`、`dsh-vision-router` |
| `@deepseek-ai/dsh-client-*` | 浏览器半侧（`dsh.client.inject` 里只做激活排序） | `dsh-context`、`dsh-better-sidebar` |

**硬约束（官方红线，来自 DSH 自带 skill 与官方文档）：**

1. **注册即 effect**：一切贡献走 `ctx.effect()` / `ctx.on()` / 服务 `register()` 并返回 disposer；**绝不手写 `removeListener` / `clearInterval`**。
2. **waterfall 监听器必须调 `next()`**，不调 = 故意短路（拦截语义）。
3. **模型可见 ⟺ 已记录**：进入模型请求的一切必须能从会话日志重建；新增模型可见输入必须新增 `SessionEventMap` 会话事件。
4. **配置用 Schemastery `Schema<Config>`**；非法配置在加载期响亮失败；**不得硬编码可调参数**。
5. **工具走 `defineTool`**：`execute` 只返回 `output.schema` 声明的规范 JSON 值；尊重 `exec.signal`；人类可读内容放 `output.render`；UI 卡片 presenter 必须是**纯函数**（禁 I/O / 时钟 / 随机）。
6. **`apply` 里 `await` 之后注册会被拒**（`dsh-plugin-guide` 明确的红线：fail `async apply` functions that register after their first `await`）。

来源：[dsh-plugin-guide SKILL.md](https://raw.githubusercontent.com/PerryLink/dsh-plugin-guide/main/SKILL.md) · 本机 asar `dsh-agent-preset/skills/cordis-plugin-development/`

### 1.6 官方还自带一个「插件开发 skill」

**本机 DSH 内置了 `cordis-plugin-development` skill**（位于 asar 内），内容包括：

```
@deepseek-ai/dsh-agent-preset/skills/cordis-plugin-development/
├── SKILL.md
├── references/host-plugin.md      # bundle 清单、导出形态、Config
├── references/ui-plugin.md        # 浏览器半侧：Client manifest、slot 注册
├── references/mcp-bundle.md       # ★ MCP 配置专用 bundle
├── references/practices.md        # 扩展点选型、性能、UI 规范
├── references/user-actions.md
├── references/verification.md
└── templates/
    ├── mcp/{package.json, cordis.patch.yml}          # ★ 两文件即可接 MCP
    └── decoration/{package.json, cordis.patch.yml, index.js, client.js}
```

它规定：持久化插件用 `plugin_manager` 工具（`action: install_bundle`）安装，**不要手写 profile 的 `package.json` / `cordis.patch.yml`，也不要在 profile 目录跑 pnpm**；探查 API 用 `cordis_inspect_list` / `cordis_inspect_query`（可查 `Service`、`Event`、`Tool`、`Slots`、`Theme`、`Config`）。

**注意**：该 skill 的说明指出，Desktop 版里此目录位于 `app.asar` 内，**只有 Host 进程自己的文件读取能打开它**；shell（`ls`/`cat`）、glob/grep（走原生 ripgrep）、`node`、pnpm 都会失败。本次调研是通过自行解析 asar 头拿到内容的（见 §6 方法论）。

---

## 2. DSH 插件可用的扩展点清单

### 2.1 扩展点总表

| 扩展点 | 注册方式 | 官方文档 |
|---|---|---|
| **Tool（工具）** | `ctx.tools.register(defineTool({...}))`，需 `inject: ['tools']` | 有（cookbook） |
| **Skill（技能）** | `ctx.skills` 注册 `SKILL.md` | 有 |
| **Service（服务）** | 类形式 `extends Service` + `super(ctx, 'name')` | 有（service.md） |
| **事件监听 / waterfall hook** | `ctx.on('tools/pre-execute', (…, next) => …)` | 有（events.md / event-producer-consumer.md） |
| **工具策略门禁（单调拒绝）** | `ctx.tools.guard(fn)` | 有 |
| **按 agent 限制工具** | `ctx.tools.restrict(filter)` | 有 |
| **系统提示词段落** | `ctx.systemPrompt.section({ name, order, text })` | practices.md |
| **按 agent 注入上下文** | `agent.inject({ content, source })` | cookbook |
| **后台长任务** | `ctx.jobs.start({ kind, label, owner, run })` | 有（long-running tool runtime） |
| **子进程** | `ctx.subprocess.spawn({...})` / `resolveExecutable()` | godot-bridge 实证 |
| **UI 面板 / slot** | `ctx.slots.inject(ownerKey, () => ctx.slots.register({name, id, order}, Component))` | ui-plugin.md |
| **会话 Chat 行** | `ctx.uiConversation.events.register()` + `conversation.chat.node` slot | practices.md |
| **Session 投影** | `ctx.sessionProjections` 单元（`apply(state,event)` 纯函数 + `stateVersion`） | practices.md |
| **slash 命令** | 官方 bundle 有 `commands` / `command-*` 行（`dsh-mc-bridge` 实测注册 `/mc-bridge status`） | 部分 |
| **子代理** | `@deepseek-ai/dsh-subagent` 服务 | 有 |
| **定时任务** | `@deepseek-ai/dsh-experimental-schedule-bundle` + `schedule_create/list/update/delete` | 有 |

### 2.2 「新增工具」最小可运行示例

**这是官方文档原样给出的可运行代码：**

```ts
import type { Context } from '@deepseek-ai/cordis'
import { defineTool } from '@deepseek-ai/dsh-tools'

export const name = 'greet-tool'
export const inject = ['tools']

export function apply(ctx: Context) {
  ctx.tools.register(defineTool({
    name: 'greet',
    description: 'Greet someone by name.',
    parameters: {
      name: { type: 'string', required: true, description: 'The name to greet' },
    },
    output: {
      schema: { type: 'string' },
      render: (_args, value) => [{ type: 'text', text: value }],
    },
    async execute(args) {
      return `Hello, ${args.name}!`
    },
  }))
}
```

要在 Web UI 里看到它，配一个 patch 层：

```yaml
- insert:
    - id: greet
      name: '/absolute/path/to/scratch-plugin/src/my-tool-plugin.ts'
```

启动 `dsh web --patch ./scratch-plugin/cordis.yml`，然后对模型说 `Use the greet tool to greet Ada.`

来源：[官方文档 · 开发一个 Tool](https://deepseek-harness.github.io/deepseek-harness/develop/basic/tool)

**更完整的形态（带可选参数、exec.signal、错误处理）：**

```ts
import { readFile } from 'node:fs/promises'
import { defineTool } from '@deepseek-ai/dsh-tools'

ctx.tools.register(defineTool({
  name: 'read_file',
  description: 'Read a file from disk.',
  parameters: {
    path: { type: 'string', required: true, description: 'Absolute file path' },
    offset: { type: 'number' },
    limit: { type: 'number' },
  },
  output: {
    schema: { type: 'string' },
    render: (_args, value) => [{ type: 'text', text: value }],
  },
  async execute(args, exec) {
    // args 已被框架按 schema 校验并推断类型
    return readFile(args.path, { encoding: 'utf8', signal: exec.signal })
  },
}))
```

### 2.3 「Skill」最小示例

按 `dsh-plugin-guide` 的实测，Skill 通过 `ctx.skills` 注册，交付物是一个带 YAML frontmatter 的 `SKILL.md`：

```markdown
---
name: my-skill
description: Use when the user asks about X. Does Y.
---

# My Skill

步骤 1 ...
步骤 2 ...
```

Skill 文件系统发现路径（六级，rank 100→600，官方 bundle 有 `skill-filesystem` 行）：
- 项目级 `<project>/.dsh/skills`、`<project>/.agents/skills`
- 自定义 `customSkillDirs`
- 用户级 `$DSH_HOME/skills`（rank 400）
- bundled（DSH 安装内自带）

来源：[Skills 目录与优先级](https://dsh-plugin.org/zh/tutorials/configure-skills-directories) · 本机 `dsh-git-badge` 包内含 `skill/gh/SKILL.md`（实证插件可携带 skill）

### 2.4 「UI 面板 / decoration」最小示例（四文件，官方模板原样）

```json
// package.json
{
  "name": "@local/my-decoration",
  "version": "1.0.0",
  "private": true,
  "type": "module",
  "exports": { ".": "./index.js", "./client": "./client.js" },
  "dsh": {
    "bundle": { "patch": "./cordis.patch.yml" },
    "client": {
      "platform": "web",
      "immediately": true,
      "inject": ["@deepseek-ai/dsh-client-ui-conversation"]
    }
  }
}
```

```yaml
# cordis.patch.yml
- insert:
    - id: my-decoration
      name: '@local/my-decoration'
```

```js
// index.js —— Host 半侧（渲染在 Client 半侧）
export function apply() {}
```

```js
// client.js —— 浏览器半侧，注册到 conversation.composer.dock slot
window.__ModuleLoader__.load({
  id: '@local/my-decoration',
  factory(require) {
    const React = require('react');
    const h = React.createElement;
    function Decoration() {
      return h('svg', {
        viewBox: '0 0 64 64', width: 48, height: 48,
        'aria-hidden': true,
        style: { display: 'block', pointerEvents: 'none' },
      }, h('circle', { cx: 32, cy: 32, r: 24, fill: '#247bbf' }));
    }
    return {
      inject: ['slots'],
      apply(ctx) {
        ctx.slots.inject('conversation.composer.dock', () => ctx.slots.register({
          name: 'conversation.composer.dock', id: 'my-decoration', order: 5,
        }, Decoration));
      },
    };
  },
});
```

来源：本机 asar `.../templates/decoration/*`（官方模板原文）· [官方 ui-plugin 参考](https://deepseek-harness.github.io/deepseek-harness/develop/basic/)

**UI 红线**：用主题 token（`--dsw-alias-*`），**不要 `require` 任何 Harness Client 包**，不要 iframe 嵌 Host HTML（拿不到主题/locale），不要替换 app root 或往 `document.body` 追加第二个应用。

### 2.5 「事件 / hook」扩展点

**工具执行流水线（固定顺序，来自官方 `dsh-tools` README）：**

```
tools/pre-execute   → 可扩展的 allow / deny / ask 门禁（waterfall，必须调 next()）
已注册的单调 guard  → ctx.tools.guard()，后续监听器无法翻转拒绝
tools/execute       → 环绕分发：加截止时间、重试、指标（唯一能替换 exec.signal 的视图）
tools/post-execute  → 检查/替换内容或值、通过反馈阻止、附加有序上下文
finalizeContent     → 定义持有，策略之后
tools/result        → 仅观测，不可变更的最终冻结结果
```

**其他可监听的 waterfall 事件（官方 practices.md 明确列出）**：
`agent/pre-step`、`agent/request`、`llm/stream`、`system-prompt/assemble`。

**选型梯度（官方原则：用最弱的够用机制）**：

> 从弱到强：`ctx.tools.restrict()` 只能移除工具 → `ctx.tools.guard()` 只能拒绝 → waterfall 监听器可以改写且依赖注册顺序 → `system-prompt/assemble` 替换整个组装。机制越强，你要保留的其他插件贡献就越多。

工具策略门禁范例在官方 `docs/cookbook/extension-cookbook.md`（permission-gate 示例），源码参考 `packages/shell/tool-bash`。

来源：[dsh-tools README.zh.md（官方）](https://github.com/deepseek-ai/deepseek-harness/blob/master/packages/core/tools/README.zh.md) · 本机 asar `references/practices.md`

---

## 3. ★ MCP 支持（最关键问题）

### 3.1 结论：**官方内置支持，不是第三方方案**

**硬证据（本机 asar 解包，路径确凿）：**

```
/dsh/node_modules/@deepseek-ai/dsh-mcp-client/
├── package.json      (2271 B)
├── README.md         (18723 B)  ← 完整配置字段表
├── README.zh.md      (17313 B)
├── lib/index.js      (35041 B)  ← 实现
└── LICENSE

/dsh/node_modules/@deepseek-ai/dsh-mcp-resources/
├── package.json      (1475 B)
├── README.md         (8402 B)
└── lib/index.js      (6196 B)
```

**官方文档也在线上**：[MCP 记忆指南（官方站点）](https://deepseek-harness.github.io/deepseek-harness/guide/mcp-memory) · [官方仓库 docs/user/guide/mcp-memory.zh.md](https://github.com/deepseek-ai/deepseek-harness/blob/master/docs/user/guide/mcp-memory.zh.md)

**分工：**

| 包 | 职责 |
|---|---|
| `@deepseek-ai/dsh-mcp-client` | 连 MCP server，把 server 的工具注册到 `ctx.tools`，模型侧工具名 `mcp__<serverName>__<tool>` |
| `@deepseek-ai/dsh-mcp-resources` | 三个共享资源工具：`list_mcp_resources`、`list_mcp_resource_templates`、`read_mcp_resource`。**本机 base bundle 已挂载此包**（`id: mcp-resources`），无配置字段，只要配了 client 就自动生效 |

### 3.2 配置文件在哪

**profile 级 `cordis.patch.yml`**（推荐）：

```
C:\Users\MikanseiP\.dsh\profiles\desktop\cordis.patch.yml
```

（按官方口径：`$DSH_HOME/profiles/<name>/cordis.patch.yml`；`$DSH_HOME` 默认 `~/.dsh`）

也可以用：
- home 级 `$DSH_HOME/cordis.patch.yml`（影响该 home 全部 profile）
- 命令行 `--patch <file>` 一次性覆盖
- 做成**仅配置的 bundle 包**（官方模板 `templates/mcp/`，只需两个文件）
- 官方示例 overlay `apps/cli/config/examples/mcp-memory/{memorix,mcp-reference-memory,engram}.cordis.yml` —— **默认关闭，必须用 `--patch` 加载或搬进 profile patch**

### 3.3 字段格式（来自 `dsh-mcp-client/README.md` 原样字段表）

**最小配置（一个 server 一条 `insert` 条目）：**

```yaml
- insert:
    - id: mcp-github
      name: '@deepseek-ai/dsh-mcp-client'
      config:
        serverName: github
        transport: stdio
        command: npx
        args: ['-y', '@modelcontextprotocol/server-github']
        env:
          GITHUB_TOKEN: !!js process.env.GITHUB_TOKEN

    - id: mcp-web
      name: '@deepseek-ai/dsh-mcp-client'
      config:
        serverName: web
        transport: streamable-http
        url: http://localhost:3000/mcp
        headers:
          Authorization: !!js '`Bearer ${process.env.MCP_TOKEN}`'
```

**完整字段表：**

| 字段 | 默认 | 含义 |
|---|---|---|
| `transport` | **必填** | `stdio` 或 `streamable-http` |
| `serverName` | **必填** | 该 server 工具名的命名空间；`[A-Za-z0-9_-]{1,32}`，同一注册作用域内唯一 |
| `command` / `args` / `env` / `cwd` | — | stdio：可执行文件、参数、追加环境变量、工作目录 |
| `url` / `headers` | — | streamable-http：端点 URL 与额外请求头 |
| `toolCallTimeoutMs` | `60,000` | 每次 `tools/call` 或资源请求的超时 |
| `maxInstructionBytes` | `32,768` | server instructions 的最大 UTF-8 字节数；超限拒绝连接 |
| `failOnStartupError` | `false` | 初次连接或工具同步失败时是否拒绝插件激活 |
| `reconnect.enabled` | `true` | 断线后自动重连 |
| `reconnect.initialDelayMs` | `500` | 首次重连延迟，每次失败翻倍 |
| `reconnect.maxDelayMs` | `30,000` | 退避上限；也是重置尝试预算的在线时长 |
| `reconnect.maxAttempts` | `10` | 一次中断内连续失败上限 |

> 完整穷尽字段以生成的配置目录为准：`docs/config-catalog.md#deepseek-aidsh-mcp-client`

**重要行为细节：**

- **默认不启用任何 server**（"No server is enabled by default"）。base bundle 里**只有 `mcp-resources` 行，没有 `mcp-client` 行** —— 这一点在本机 `dsh-base/cordis.patch.yml` 的 row id 列表里得到充分验证。
- **工具命名**：`mcp__<serverName>__<rawName>`，例：`mcp__github__create_issue`。与 Claude Code / Codex 同名形态。
- **命名是钉死的契约**：有损归一化会追加 12 位十六进制 SHA-256 后缀，保证不同身份不塌缩；session 历史与权限规则跨重启/HMR 存活。
- **stdio 环境清理**：子进程环境基于 `scrubbedParentEnv()` —— 匹配 `/KEY|PASSWORD|SECRET|TOKEN/i` 的环境变量与所有 `DSH_*` 被丢弃，配置里的 `env` 叠加在上。
- **协议版本**：官方 SDK 优先选 `2026-07-28`，回退到受支持的旧修订。
- **不支持**：MCP prompt templates、resource subscriptions、task-based execution extension（需要 task 的工具在调用时被拒）。
- 图片（PNG/JPEG/WebP/GIF）在模型支持图片输入且附件功能开启时可进入上下文；音频/嵌入资源只给诊断文本。

来源（本机解包原文）：`@deepseek-ai/dsh-mcp-client/README.md` · `@deepseek-ai/dsh-mcp-resources/README.md`；线上同源：[MCP 记忆（官方）](https://github.com/deepseek-ai/deepseek-harness/blob/master/docs/user/guide/mcp-memory.zh.md)

### 3.4 官方「仅配置」MCP bundle 模板（两文件，本机 asar 原样）

```json
// package.json
{
  "name": "@local/demo-mcp",
  "version": "1.0.0",
  "private": true,
  "type": "module",
  "dsh": { "bundle": { "patch": "./cordis.patch.yml" } }
}
```

```yaml
# cordis.patch.yml
- insert:
    - id: demo-mcp
      name: '@deepseek-ai/dsh-mcp-client'
      config:
        serverName: demo
        transport: streamable-http
        url: http://127.0.0.1:3000/mcp
        failOnStartupError: true
```

> 配置型 bundle 的清单需要唯一的 `name`、`version` 与 `dsh.bundle.patch`，**但不需要 Host/Client 入口文件**。

验证：装好后调用 `mcp__demo__ping` 或任一新增的 `mcp__<serverName>__<tool>`。

来源：本机 asar `.../cordis-plugin-development/templates/mcp/*` 与 `references/mcp-bundle.md`

### 3.5 第三方 MCP 相关插件（生态补充）

| 插件 | 作用 |
|---|---|
| [`dsh-mcp-panel`](https://github.com/PerryLink/dsh-mcp-panel)（[npm](https://www.npmjs.com/package/dsh-mcp-panel)） | **只读** MCP 运行时面板：`/mcp` 命令 + 设置页 tab，显示状态、工具与错误 |
| [`dsh-cert-mcp`](https://github.com/PerryLink/dsh-cert-mcp) | DSH 插件**作为 MCP server**（反向）：只读暴露认证注册表 |
| [`@ctl456/dsh-mcp-manager`](https://www.npmjs.com/package/@ctl456/dsh-mcp-manager) / [`@zhengjunyao/dsh-mcp-manager`](https://www.npmjs.com/package/@zhengjunyao/dsh-mcp-manager) | MCP server 管理 |
| [`dsh-mcp-oauth-client`](https://www.npmjs.com/package/dsh-mcp-oauth-client) | 带 OAuth 的 MCP 客户端 |
| `dsh-claude-move` | 把 Claude Code 的 sessions / memory / skills / CLAUDE.md 迁进 DSH（含 MCP 配置迁移） |

**不确定项**：上述第三方包的 README 我**未逐一实际抓取正文**（仅从 npm/GitHub 搜索结果与 dsh-plugin-guide 家族清单确认存在）。`dsh-mcp-oauth-client` 与两个 `dsh-mcp-manager` 的作用描述来自包名与搜索结果摘要，**未做正文核实**。

---

## 4. DSH 插件生态：注册中心 / 目录站

| 站点 / 仓库 | 规模 | 备注 |
|---|---|---|
| [dsh-plugin.org](https://dsh-plugin.org/zh/plugins) | **12,250** 个仓库、10 大分类、226 篇教程 | 社区维护，非官方。内置「设置 → 插件市场」的 [dsh-plugin-hub](https://github.com/dshplugin/dsh-plugin-hub)。分类：界面体验 1330 / 会话消息 837 / 记忆上下文 195 / 工具能力 5608 / 技能智能体 369 / 工作流 340 / 集成连接 582 / 模型推理 1294 / 开发运维 1036 / 数据知识 359 / 娱乐 300 |
| [dshbase.com](https://dshbase.com/zh/plugins/) | **7,797** 精选，7 大类 | 有[插件目录](https://dshbase.com/zh/plugins/directory/)、[审计](https://dshbase.com/zh/audit/)、[场景包](https://dshbase.com/zh/packs/)。提供 `dshbase-catalog` 目录插件（`dsh plugin add dshbase-catalog`），装一次后让 agent 自行装其他插件 |
| [dshmarket](https://dshmarket.com) / `dshmarket` npm 包 | 应用内可视化插件市场 | **本机已安装** `dshmarket@1.66.7`；注册表快照源 `https://awesome-dsh-plugin.com/plugins.json` |
| [awesome-dsh-plugin](https://awesome-dsh-plugin.com) / [awesome-dsh-plugin/awesome-dsh-plugin](https://github.com/awesome-dsh-plugin/awesome-dsh-plugin) | 社区 registry | 提供 GitHub badge；`dsh-plugin` 话题收录 |
| [dshworks/awesome-dsh-plugins](https://github.com/dshworks/awesome-dsh-plugins) | 开源数据注册表 | "Spam-filtered, open-data registry" |
| [kejixiaoliang/awesome-dsh-plugins](https://github.com/kejixiaoliang/awesome-dsh-plugins) | **280+** 社区插件、14 类 | 明确覆盖 MCP / Skill / TUI / 多 Agent / 上下文记忆 / UI 皮肤 |
| [AdamPlatin123/awesome-dsh-plugins](https://github.com/AdamPlatin123/awesome-dsh-plugins) · [stephenlzc/awesome-dsh-plugin-beancookie](https://github.com/stephenlzc/awesome-dsh-plugin-beancookie) | 同类精选 | — |
| [dsh.directory](https://dsh.directory/plugins/smalldy/godot-bridge) | 目录站 | 提供 badge |
| **官方话题** | [`github.com/topics/dsh-plugin`](https://github.com/topics/dsh-plugin) | 官方约定：仓库加此 topic 即加入生态 |
| **官方仓库** | [deepseek-ai/deepseek-harness](https://github.com/deepseek-ai/deepseek-harness) | 官方文档 + Discussions（`dsh-plugin-guide` 归档了 1654 条讨论） |

**PerryLink 插件家族**：45 个 Apache-2.0 插件（`dsh-mcp-panel`、`dsh-click`、`dsh-cert-mcp`、`dsh-plugin-guide`、`dsh-plugin-doctor`、`dsh-background-agents`、`dsh-permission-rules`、`dsh-reach` 等）。可用 Desktop Market 自定义源：
`https://perrylink-dsh-catalog.perrylink.workers.dev/catalog-source.json`

**知识库（本次调研的主要二手源）**：[`PerryLink/dsh-plugin-guide`](https://github.com/PerryLink/dsh-plugin-guide)（[npm](https://www.npmjs.com/package/dsh-plugin-guide)）—— 含官方文档 EN/ZH 归档、Cordis primer、114 个社区仓库归档（15 个深读）、20+ 实测坑、`dsh-plugin-dev` CLI（`new` / `check` / `verify`）。**注意**：它自述为「社区维护，非官方 DeepSeek 产品」，且做插件的 `dsh` CLI 在其文档中有时写作 `dsh1024`（1024 store channel）。

### 与「MCP / 游戏自动化 / 外部进程桥接」直接相关的插件

| 插件 | 类别 | 说明 |
|---|---|---|
| [`Smalldy/godot-bridge`](https://github.com/Smalldy/godot-bridge) | **游戏自动化 + 外部进程** | 17 个 `godot_*` 工具，驱动运行中的 Godot 4.x，**明确取代 `godot-mcp` 的 MCP server**，直连游戏内 TCP 交互服务器 |
| [`xiaozhaoz1/dsh-mc-bridge`](https://github.com/xiaozhaoz1/dsh-mc-bridge) | **游戏自动化 + HTTP 桥接** | DSH ↔ Minecraft，SSE 订阅 + POST 下发，纯后端零渲染 |
| [`Neutron3529/dsh-plugin-tool-gaming-host`](https://github.com/Neutron3529/dsh-plugin-tool-gaming-host) | 宿主机访问 | 让 agent 借 gaming 程序直接访问宿主机 |
| [`PerryLink/dsh-click`](https://github.com/PerryLink/dsh-click) | 桌面控制 | 跨平台原生桌面控制（Windows 优先） |
| [`ZhijiangTang/dsh-http`](https://github.com/ZhijiangTang/dsh-http) | HTTP 工具 | 结构化 HTTP 请求工具（status/timing/size、auth helper、JSON 解析） |
| [`ysr666/dsh-vision-router`](https://github.com/ysr666/dsh-vision-router) | 外部进程 + HTTP provider | **本机已安装**。自带 `puppeteer-core`，通过 HTTP 调视觉模型；`httpProviders` 配置在本机 `cordis.patch.yml` 实证 |
| [`Smalldy` 同作者 / `dshpet-mc`](https://github.com/xiaozhaoz1/dshpet-mc) | MC 桌宠 mod | `dsh-mc-bridge` 的游戏侧对端 |

---

## 5. 如何注册「需要长期运行的外部进程 + HTTP 通信」的工具

### 5.1 结论：有两条官方路径 + 两个现成示范插件

| 机制 | 用途 | 服务 |
|---|---|---|
| `ctx.subprocess` | 启动并管理外部进程（**不受文件沙箱约束**） | `@deepseek-ai/dsh-subprocess-local`（base bundle `id: subprocess`） |
| `ctx.jobs` | 注册**跨工具调用长期运行**的后台任务 | `@deepseek-ai/dsh-jobs-local`（base bundle `id: jobs`）+ `@deepseek-ai/dsh-tool-jobs`（`id: tool-jobs`） |

### 5.2 官方后台任务契约（`ctx.jobs.start`）

来自官方 cookbook 原文：

> 通过 producer 配置控制 `run_in_background`，然后使用 `ctx.jobs.start({ kind, label, owner: exec.agent, run })` 注册任务。注册表会在进入 producer 主体前将已预先中止的调用判为失败；运行时会在 `run()` 启动工作前校验 owner 和任务控制器是否可用，随后提供 id、会话围栏、通用控制工具、通知和 owner cleanup。成功的后台分支会返回类型化的规范句柄，如 `{ kind: 'background', jobId }`。
>
> `ctx.jobs.start()` 发布 id 后，**应使用任务自有的取消信号，而不是 `exec.signal`**：之后取消外层调用只会停止等待本次调用，不会终止已经发布的工作；该生命周期归 `job_kill`、owner dispose 和服务的 teardown 所有。前台工作仍与 `exec.signal` 耦合。

spec 提供：同步 `cancel`、资源清理后 settle 且不 reject 的 `done`、以及由注册表泵入 job 输出环的拉取式 `output` 源。

参考实现：`@deepseek-ai/dsh-tool-bash`；设计文档：[generic-long-running-tool-runtime Agent Note](https://github.com/deepseek-ai/deepseek-harness/blob/master/.agents/notes/implemented/architecture/2026-06-20-generic-long-running-tool-runtime.zh.md)。

来源：[官方工具编写参考](https://deepseek-harness.github.io/deepseek-harness/reference/cookbook/adding-a-tool.md) · [tool-jobs README](https://github.com/deepseek-ai/deepseek-harness/blob/master/packages/jobs/tool-jobs/README.md)

### 5.3 `ctx.subprocess.spawn` 的真实调用形态（godot-bridge 源码实证）

**模式 A —— 一次性桥接进程，通过它转发一次命令到外部服务（TCP/HTTP）。** 这是「长驻外部进程 + 通信」最实用的写法：DSH 侧不长期持有 socket，每次工具调用起一个短命桥接进程，由它连外部服务、发一行、读一行、退出。

```js
handle = subprocess.spawn({
  argv: [node, '-e', BRIDGE, String(command), JSON.stringify(params || {}),
         String(timeoutMs || 20000), String(targetPort)],
  cwd: root || '.',
  stdio: {
    stdin: 'ignore',
    stdout: { collect: { maxBytes: 8 * 1024 * 1024 } },
    stderr: 'inherit',
  },
  graceMs: 3000,
  signal: signal,
})
const outcome = await handle.done
let text = handle.collected.stdout.readFrom(0).text || ''
const line = text.split('\n')[0].trim()
return JSON.parse(line)
```

**模式 B —— 启动并持有长驻游戏进程（collect 模式增量读输出）。**

```js
handle = subprocess.spawn({
  argv: argv,                       // 例：[godotExe, '-d', '--path', projectPath]
  cwd: projectPath,
  stdio: {
    stdin: 'ignore',
    stdout: { collect: { maxBytes: 8 * 1024 * 1024 } },
    stderr: { collect: { maxBytes: 8 * 1024 * 1024 } },
  },
  graceMs: 3000,
  signal: signal,
})
```

godot-bridge 用一个 `godot = { handle, outOffset, errOffset, ... }` 对象持有进程句柄，`godot_get_debug_output` 通过**增量 offset**（`outOffset` / `errOffset`）只读新增的 stdout/stderr —— 这是「长驻进程日志流」的标准写法。

**其他用到的服务方法：**
- `subprocess.resolveExecutable('node')` / `('godot')` —— 解析可执行文件路径
- `ctx.timer` —— 定时器（测试端口存活）
- `ctx.fs.resolve/stat/readText/writeFile` —— 在插件 Config 指定范围内做文件 I/O（`fs` 服务默认套用部署默认策略；godot-bridge 显式把策略根设在目标项目而非 session workspace）
- `ctx.sandboxPolicy.resolve({ session })` —— 取 session 的 workspace root
- `ctx.systemPrompt.section({ name, order, text })` —— 注入常驻指引（godot-bridge 注入了 3 段：引擎路径指引、**沙箱崩溃预防**、实例归属）
- `ctx.inject(['configEditor'], (cctx) => {...})` —— 拿到配置编辑器，把设置写回 profile patch

### 5.4 ★ 两个最重要的实测踩坑（直接关系到「连本地游戏」）

**(1) 文件沙箱 vs 长驻 GUI 进程 —— 必须走 `subprocess`，不能走 shell 工具**

`godot-bridge` README 原文：

> **DSH file sandbox vs Godot `user://`**：通过沙箱化的 shell 执行器（pwsh/bash 工具）启动 Godot 会传播受限令牌，Godot 在启动时崩溃（`Failed to open 'user://logs/…'`，signal 11）。godot-bridge 通过**原生 `subprocess` 服务**派生进程，它**不受文件限制** —— 这就是它能工作的原因。

它甚至为此注册了一段常驻 system prompt 段落，明确要求模型：

> 始终通过 `godot_*` 工具启动 Godot，**严禁**用 pwsh/bash shell 工具运行 godot。……若某个 `godot_*` 结果返回 `diagnosis:"sandbox-crash"`，根因是启动通道而非项目 bug。

**(2) 真实 exe，不要用版本管理器 shim**

> 一个 shim 会立即退出并让真正的 Godot 变成孤儿进程；进程管理会误判它已死亡。

**(3) 进程归属**（godot-bridge 用随机 token 区分自己启动的实例与用户/编辑器启动的实例，`external: true` 表示接管外部实例，`godot_stop_project` 对外部实例拒绝执行）。

### 5.5 ★ 第二个示范：HTTP + SSE 长连接（最贴近「HTTP 通信连本地游戏」）

[`xiaozhaoz1/dsh-mc-bridge`](https://github.com/xiaozhaoz1/dsh-mc-bridge) —— **纯后端、零渲染**的转达层：

```
AI（DSH 的模型）
  ↕ 工具调用（ctx.tools）
dsh-mc-bridge（本插件：转达层）
  ↕ SSE 订阅 + POST 下发
dshpet-mc（MC mod：感官 + 手脚）
```

它的配置（`cordis.patch.yml` 覆盖层写法，**这是用户在 profile 里改配置的标准姿势**）：

```yaml
- id: mc-bridge
  config:
    enabled: true
    endpoint: 'http://127.0.0.1:25580'
    token: ''          # 本机可空；云端必填
    readOnly: false
    allowTiers: ['act.tier1']
```

**它沉淀出的、任何「长驻外部进程 + HTTP」插件都值得抄的工程要点：**

| 机制 | 做法 |
|---|---|
| **安全门（硬）** | 非回环 endpoint + 无 token ⇒ **拒绝启用**（不静默放行） |
| **默认只读** | `readOnly=true` 时**一个动作工具都不注册** |
| **逐项授权** | 工具须在 `allowTiers` 内；未授权返回明确的 `capability_not_granted`（不静默忽略） |
| **限流** | 每工具独立最小间隔（`rateLimit.say/anim/actTier1/screenshot` = 5000/2000/2000/30000 ms），超限返回 `rate_limited` + `retryAfterMs` |
| **SSE 心跳超时** | SSE 无「请求超时」概念，必须自己用心跳（`heartbeatTimeoutMs: 45000`，3 次丢包判死） |
| **退避重连** | `reconnectMinMs/reconnectMaxMs` = 2000/30000，2→4→8→16→30s 封顶 |
| **停止矩阵** | **401/403 与其他 4xx ⇒ 停止重连**（不无限重试）；仅网络/5xx 才退避重连 |
| **审计** | 每次调用留痕 `[DSH-MC/AUDIT] tool=… ok=… ms=…`，默认开启 |
| **敏感值** | token 绝不入日志（只打前 4 位）；审计里 `token/secret/key/password` 一律 `***` |
| **非法配置不失效** | 按字段回落到默认并**告警说明原因**；「配置缺失」静默取默认，只有「存在但非法」才告警 |

**重要：它与官方 `dsh-mcp-client` 完全无关**。README 明确说它是「转达层」，不自己起 MCP server、不存 API key、不碰 MC 进程内存 —— 也就是说，**当你只需要连一个本地游戏/本地服务时，写一个直接的 DSH 工具插件比包一层 MCP 更简单**。`godot-bridge` 的定位表述更直接：*"replacing the `godot-mcp` MCP server with first-class agent tools. No MCP protocol, no Python server, no editor addon."*

### 5.6 针对本工作区（RimWorld 自动测试）的可行性判断

RimWorld 是 Unity 游戏，没有像 Godot `McpInteractionServer` 那样现成的游戏内 TCP 服务器。因此可选路线是：

1. **RimWorld mod 侧暴露 HTTP/TCP**（类似 `dshpet-mc` 的 mod 侧做法）+ DSH 侧写 `ctx.tools.register` 的工具插件，用 `ctx.subprocess` / `fetch` 与 mod 通信 —— 与 `dsh-mc-bridge` 架构完全同构。
2. **屏幕截图 + 输入注入**（`dsh-mc-bridge` 的 L0 窗口层已验证：`GLFW*`/`LWJGL` 类名评分识别窗口、`PrintWindow` 对 OpenGL/DirectComposition 窗口返回全黑所以只能区域截图、`mouse_event` 合成点击进不到 MC 所以以键盘为主）。
3. **MCP 路线**：先写一个独立的本地 MCP server（Python/TS）包住 RimWorld 桥，再用 `@deepseek-ai/dsh-mcp-client` 的 `transport: streamable-http` 接进来 —— **零插件代码**，只要一段 YAML。

> **未找到**：我没有找到任何现成的 RimWorld DSH 插件或 RimWorld MCP server。上述路线是基于 `dsh-mc-bridge`（Minecraft）与 `godot-bridge`（Godot）的已验证架构做的类推，**不是已验证事实**。

---

## 6. 方法论与可信度说明

### 本机只读取证（未修改安装目录任何文件）

- `E:\DSH\resources\app.asar`（121 MB）是打包文件。`npx @electron/asar` 因 npm cache EPERM 失败，故自行按 asar 头格式解析：
  - `u32@0 = 4`（pickle header size）
  - `u32@4 = headerStringSize`（含 4 字节长度前缀的补齐长度）
  - `u32@8 = jsonLen`，`u32@12 = jsonLen`（重复）
  - JSON 头字节从 **offset 16** 开始
  - 文件内容基址 = `8 + headerStringSize`
  - 解析脚本留在工作区：`.research/asar3.mjs`（`list` / `cat` / `dump` / `grep`）
  - asar 内共 **12,967** 个文件
- 只读查看 `C:\Users\MikanseiP\.dsh\profiles\desktop\{package.json, cordis.yml, cordis.patch.yml, pnpm-workspace.yaml}` 与 7 个已安装第三方插件的 `package.json` / `cordis.patch.yml`。

### 抓取过的线上来源

- 官方文档站 <https://deepseek-harness.github.io/deepseek-harness/>
- 官方仓库 <https://github.com/deepseek-ai/deepseek-harness>
- dsh-plugin-guide <https://github.com/PerryLink/dsh-plugin-guide>（README / SKILL.md 原文）
- dsh-plugin.org 插件市场与教程 <https://dsh-plugin.org/zh/plugins>
- dshbase.com <https://dshbase.com/zh/plugins/>
- godot-bridge 源码 <https://github.com/Smalldy/godot-bridge>（`plugin/godot-bridge.mjs` 全文）
- dsh-mc-bridge README <https://github.com/xiaozhaoz1/dsh-mc-bridge>
- 阿里云社区转载文 <https://developer.aliyun.com/article/1762995>

### 明确的「不确定 / 未找到」清单

| 项 | 状态 |
|---|---|
| 页面标题为 `dshbase.com/plugins/` 的英文版 | 只抓到了 `/zh/plugins/`；英文根路径 `/plugins/` 存在（页面有 EN 切换）但未单独抓取 |
| 第三方 MCP 插件（`dsh-mcp-oauth-client`、两个 `dsh-mcp-manager`）的正文 | **未抓取 README**，仅从 npm/搜索摘要确认存在，作用描述未核实 |
| `dsh-plugin-guide` 中 `dsh1024` CLI 与 `dsh` CLI 的关系 | 未核实（该仓库 README 提到 1024 store channel 与 `npm i -g dsh1024`） |
| `dsh-plugin.org` 的 `verified` 判定标准与人工审核流程 | 站点自述「专业团队人工审核」，未找到可核查的判定细则 |
| DSH 官方是否有**官方**插件注册中心 | 官方只有 GitHub topic `dsh-plugin` 约定；市场上的目录站均为社区维护。官方仓库 README 未见官方 registry |
| `godot-bridge` 完整源码中 `godot_run_project` / `godot_command` 等前半段函数体 | 抓取被截断（约 50 KB），已拿到 `subprocess.spawn` 全部调用点与 `defineTool` 后半段；**长驻进程的端口等待逻辑未逐行核实** |
| RimWorld 现成 DSH 插件 / MCP server | **未找到**，见 §5.6 |
| `hooks` 概念的官方术语 | 官方用「事件 / waterfall / 扩展点」，未见单列名为 "hook" 的机制；`extension-cookbook.md` 里有 "A hook plugin: permission gate example" 章节标题 |
| 本机 DSH 桌面版具体版本号 | 未从 `E:\DSH\version` 或 asar 内 DSH 本体 `package.json` 中确认；从生态与 `dsh-context` 的 `dshReleases.0.2.0-rc.2` 推断为 0.2.x 家族 |

---

## 7. 一页速查

```text
# 插件 = ESM 模块
export const name = 'my-plugin'
export const inject = ['tools']
export function apply(ctx, config) { ctx.tools.register(defineTool({...})) }

# 目录（bundle 形态）
my-plugin/
├── package.json      → {"dsh":{"bundle":{"patch":"./cordis.patch.yml"}}}
├── cordis.patch.yml  → [- insert: [{id: my-plugin, name: '@local/my-plugin'}]]
├── index.js          → 上面那个模块
└── client/client.js  → 可选，window.__ModuleLoader__.load({id, factory})

# 安装
dsh plugin --profile web add my-plugin
dsh --profile web --dump-config        # 验证

# ── MCP（官方内置，默认关闭）────────────────────────
# 文件：$DSH_HOME/profiles/<name>/cordis.patch.yml
- insert:
    - id: my-mcp
      name: '@deepseek-ai/dsh-mcp-client'
      config:
        serverName: demo            # 必填，决定 mcp__demo__* 前缀
        transport: streamable-http  # 或 stdio
        url: http://127.0.0.1:3000/mcp
        # stdio 用：command / args / env / cwd
        failOnStartupError: true
# 模型侧工具名：mcp__<serverName>__<tool>
# 资源工具（base bundle 已挂载 dsh-mcp-resources）：
#   list_mcp_resources / list_mcp_resource_templates / read_mcp_resource

# ── 长驻外部进程 + HTTP ────────────────────────────
inject: ['subprocess', 'jobs', 'tools', 'timer', 'systemPrompt']
ctx.subprocess.spawn({ argv, cwd, stdio: {stdout:{collect:{maxBytes}}}, graceMs, signal })
await handle.done
handle.collected.stdout.readFrom(offset).text     # 增量读长驻进程日志
ctx.jobs.start({ kind, label, owner: exec.agent, run })  # 跨调用后台任务
# ⚠ 必须用 subprocess，不能用 pwsh/bash shell —— 文件沙箱会让 GUI 子进程崩溃
```
