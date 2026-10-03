# RimWorld × AI Agent / MCP 生态调研报告

调研时间：2026-10-03 · 方法：GitHub Search API（多轮、分页）+ README 正文抓取核实 + 网页搜索
数据口径：star 数 / pushed_at 取自 GitHub API 快照；「已核实」= 抓到了 README 正文；「未核实」= 只有仓库描述或搜索摘要

---

## 0. 已知项复核（不重复展开）

| 项目 | 快照 | 备注 |
|---|---|---|
| pardeike/RimBridgeServer | 37★ · pushed 2026-08-24 · MIT | 游戏内 MCP，GABP 之上 |
| pardeike/GABS / GABP / Rimworld-Doorstop / DecompilerServer | — | 宿主侧多路复用 / 协议 / Mono 调试热重载 / 反编译 MCP |
| Hcrab/rimUIMCP | 21★ · created 2026-09-22 · MIT | 中文作者，状态探索 + 真实 UI 自动化 + TS/Python SDK |
| ShugokiFable/RimWorldForge | — | 静态侧工作台；注意 `SenjuWoo/RimWorldForge` 是同一项目的镜像/fork（homepage 指回 ShugokiFable） |

---

## 1. 新发现项目清单

### A 类：直接补足「mod 开发 → 构建 → 隔离启动 → 判据」这一层（最高价值）

| 仓库 | URL | 一句话 | 成熟度 | 与本项目关系 |
|---|---|---|---|---|
| **mushroomTW/rimworld-mod-mcp** | https://github.com/mushroomTW/rimworld-mod-mcp | 本地-only MCP：查 Def/C# 符号、反编译已装 mod、构建 mod、**在隔离会话里跑测试** | 0★ · created 2026-09-26 · pushed 2026-10-01 · MIT · 有 release（自包含单文件 exe） | **互补（强）**，见第 2 节 |
| **Jeffrharr/RimWorldTestHarness** | https://github.com/Jeffrharr/RimWorldTestHarness | 场景化端到端测试框架：JSON scenario → 启动真游戏 → probe 断言 / 截图 / timelapse / **LLM vision 断言** / 性能门禁 → JSON 报告，非 0 退出 | 1★ · created 2026-07-24 · pushed 2026-08-31 · MIT | **互补（强）**，见第 2 节 |
| **Jeffrharr/RimWorldDevMCP** | https://github.com/Jeffrharr/RimWorldDevMCP | 配套 MCP + `rwdev` CLI：对**正在运行**的游戏执行原生 dev-action、读数值探针、截图、改时间 | 0★ · created 2026-07-25 · pushed 2026-07-29 · MIT | 与 RimBridgeServer **部分重复**，机制不同（XDG 缓存下的原子 JSON 文件队列，游戏侧由 harness mod 的 `LiveCommandDriver` 每 tick 排空） |
| **1264600905/RimDevTools** | https://github.com/1264600905/RimDevTools | 中文 Windows 工具链：环境体检 / 原子部署 / 事务化改 `ModsConfig.xml` / **AutoTestRunner（游戏内 UI·战斗·视图·配方·技能自动测试，出 JSON 结果 + Evidence + 截图）** / 发布打包 | 10★ · created 2026-08-12 · pushed 2026-08-17 · MIT | **互补（强）**；`GAME_HUNG`（60s 无主线程心跳即判卡死并恢复环境）值得直接借鉴 |
| **cryptiklemur/rimworld-modder-mcp** | https://www.nuget.org/packages/cryptiklemur.rimworld-modder-mcp （源码声明在 github.com/cryptiklemur/rimworld-modder-mcp，但该路径经 GitHub API 返回 404，疑改名/私有） | .NET 10 MCP：XML Def 与继承、**补丁冲突分诊**、mod 兼容性与依赖检查、**发布就绪检查**、XPath 校验、改动文件审计、load order 影响分析 | NuGet v4.0.0（2026-08-25），累计 1.2K 下载，9 个版本，MIT，CI + semantic-release | **互补**（唯一专注「补丁冲突 + 发布门禁」的） |

### B 类：静态源码 / Def 研究类 MCP（不与 RimBridgeServer 竞争，是它的前置知识层）

| 仓库 | URL | 一句话 | 成熟度 | 关系 |
|---|---|---|---|---|
| **kearril/RimSearcher** | https://github.com/kearril/RimSearcher | V3 用 **Skill + CLI** 取代单体 MCP：游戏内 DataMod 在**运行时**把「已继承+已 Patch 合并」的 Def 拓扑导出为 `defs.db`(SQLite)，CLI 毫秒级查询；源码层交给 pardeike/DecompilerServer | **45★** · created 2026-02-07 · pushed **2026-10-02**（很活跃）· MIT | **互补（很有价值）**：直接针对「读磁盘原始 XML 会被未生效 override / 未应用继承 / 运行时注入骗到」这一问题 |
| **Azetus/RimSourceHelper** | https://github.com/Azetus/RimSourceHelper | 19 个 MCP 工具：符号检索、**调用图**(callers/callees/call_tree)、Def 检索与引用、**Harmony 补丁查找**、**XML Patch 查找**、反编译 | 0★ · created 2026-06-08 · pushed 2026-08-18 · MIT · C# analyzer + Node MCP | 互补（静态理解层） |
| **realloon/RimSage** | https://github.com/realloon/RimSage | MCP 源码/Def 检索与浏览，有托管服务 mcp.rimsage.com 与自托管 stdio | **87★**（该生态 star 最高）· created 2025-11-30 · pushed 2026-08-28 · MIT · TS/Bun | 互补；**已被第三方 skill 当成默认知识源**（见 Kon-on） |
| **h7lu/RiMCP_hybrid** | https://github.com/h7lu/RiMCP_hybrid | 混合检索 RAG（Lucene 粗筛 + 向量重排 + 图关系），4 个工具 `rough_search`/`get_uses`/`get_used_by`/`get_item`，中文作者「五步蛇」 | **44★** · created 2025-11-22 · pushed 2026-02-19 · MIT · C# | 互补（静态理解层）；作者在 README 里坦承了 Lucene 预筛掉正确答案、图边噪声等实战教训，很有参考价值 |
| **KralisXC/rimcp-v2** | https://github.com/KralisXC/rimcp-v2 | RiMCP_hybrid 的重写衍生：8 个 MCP 工具、ONNX e5-base-v2 向量 + Lucene + 知识图谱(PageRank)、约 15.2 万符号 | 0★ · created 2026-09-16 · MIT · v0.1.0 · C# | 与 h7lu 重复（README 已声明代码移植自其项目） |
| **afbqwer/RimworldDataMCP** | https://github.com/afbqwer/RimworldDataMCP | C# MCP（描述为空；README 字段出现 `changedFiles/addedFiles/removedFiles` 增量更新语义） | 2★ · pushed 2026-08-23 · MIT | 未核实（未抓到正文） |
| **sam2332/mcp-rimworld-def-search** / **-dll-search** | https://github.com/sam2332/mcp-rimworld-def-search · https://github.com/sam2332/mcp-rimworld-dll-search | Def 搜索 / DLL 搜索两个小 MCP | 0★ · JS · 2025-06 创建，2026-02 有 push | 与 RimSage 重复，低优先级 |
| gmtisrad/RimWorld-LLM-Sandbox | https://github.com/gmtisrad/RimWorld-LLM-Sandbox | 「An agent connected to RimWorld state via MCPs and APIs」 | 0★ · 2026-06-12（单次提交） | 早期脚手架 |

### C 类：接管「运行中游戏」的 MCP（与 RimBridgeServer 功能重叠）

| 仓库 | URL | 说明 | 成熟度 |
|---|---|---|---|
| M4x28/RimAPI_MCP_Server | https://github.com/M4x28/RimAPI_MCP_Server | 基于 RIMAPI mod 的 MCP | 0★级 · 2025-12 起 |
| allenmonkey970/rimworld-mcp | https://github.com/allenmonkey970/rimworld-mcp | 自称 **85 个 MCP 工具**，C# mod 暴露本地 HTTP 桥 | 0★ · created 2026-05-19 · pushed 2026-09-19 · GPL-3.0 |
| Critical-Reynolds/autorim-mcp | https://github.com/Critical-Reynolds/autorim-mcp | C# mod loopback 桥 + TS MCP，**24 个工具**，自然语言控制殖民地 | 4★ · created 2026-07-25 · pushed 2026-07-25 |
| Bayarddevries/rimworld-mcp | https://github.com/Bayarddevries/rimworld-mcp | 「RimWorld MCP Bridge — real-time AI agent control」 | 0★ · 2026-07-01 · 199KB |
| She11code/Mcp-of-RimWorld | https://github.com/She11code/Mcp-of-RimWorld | C#，无描述 | 1★ · 2026-03-19 |
| cenbai/RimWorld-MCP | https://github.com/cenbai/RimWorld-MCP | C#，无描述 | 1★ · 2026-05-21（仅数小时活动） |
| yuyaryshev/rimworld_performance_mcp | https://github.com/yuyaryshev/rimworld_performance_mcp | **空仓库**（size 0） | 0★ |
| jolsso/rimworld-mcp-plugin | https://github.com/jolsso/rimworld-mcp-plugin | **空仓库**（size 0，无语言） | 0★ |

### D 类：底层桥接 / 发布 / 桌面一体化（非 MCP 但现在相关）

| 仓库 | URL | 说明 | 成熟度 |
|---|---|---|---|
| **IlyaChichikov/RIMAPI** | https://github.com/IlyaChichikov/RIMAPI | RimWorld 1.5–1.6 mod，提供 **REST API + SSE** 的游戏内服务器（Steam Workshop 3593423732） | **29★** · created 2025-10-05 · pushed 2026-08-03 · GPL-3.0 · 9 forks |
| pardeike/SteamWorkshopAgent | https://github.com/pardeike/SteamWorkshopAgent | **本地 MCP：把 mod 发布到 Steam Workshop** | 0★ · created 2026-05-15 · pushed 2026-07-17 · MIT |
| **lebek/modmixer** | https://github.com/lebek/modmixer | 桌面 app（Electron/TS）+ 内置 agent：反编译/索引游戏 → 聊天式写 XML/C# → 启动游戏测试 → **Modmixer Bridge 监控报错并自动修** → 发布 Workshop | **27★** · created 2026-04-29 · pushed 2026-08-12 · MIT · modmixer.com |
| Mezo1122/RiMCP_hybrid | fork of h7lu | — | fork |
| SenjuWoo/RimWorldForge | fork/mirror of ShugokiFable | — | fork |

### E 类：让 LLM/agent **玩** RimWorld（游戏玩法侧，非 mod 开发；仅作参考与接口设计借鉴）

| 仓库 | URL | 说明 | 成熟度 |
|---|---|---|---|
| **zorrobyte/rimagent** | https://github.com/zorrobyte/rimagent | LLM 玩殖民地并在两局之间重写自己的 playbook；附 **RimBridge mod**（HTTP 127.0.0.1:8765）+ Steward（工作优先级/库存/常备命令，每 tick 无 LLM）+ 三路 LLM 流（play/improve/**watchdog 自我修 bug**）+ 论文 | **60★** · created 2026-09-16 · pushed 2026-09-22 · MIT · 有 release | 
| **AppSprout-dev/RLE** | https://github.com/AppSprout-dev/RLE | RimWorld Learning Environment：**harness × model 基准测试**，官方榜、9 指标复合评分、6 个场景；MCP 工具服务器 + 每 tick 一次 agent turn | 7★ · created 2026-03-21 · pushed 2026-09-13 · MIT | 
| AppSprout-dev/rle-harness-{opencode,grok-build,claude-code,cursor-agent,template} | 同组织 | 把各家 coding agent 接到 RLE MCP 上 | 0★ 各 |
| oidahdsah0/Rimworld_AI_Core · Rimworld_AI_Framework | 同名仓库 | AI 殖民地管理系统 / LLM 框架 | 未核实 |
| Void-n-Null/Artificial-Rimtelligence | https://github.com/Void-n-Null/Artificial-Rimtelligence | AI 殖民地管理者 mod（第二次尝试，早期脚手架 + OpenRouter OAuth，保留作记录） | 未核实 |
| RimWorld-RimMind-Mod/RimWorld-RimMind-Mod-Core | — | 未核实 | 未核实 |
| Wuxie233/RimWorldMod_RimWorldAI | https://github.com/Wuxie233/RimWorldMod_RimWorldAI | 多 agent 殖民地 OS，**MCP 隔离的游戏工具 + 独立 Agent 运行时** | 0★ · 2026-06→08 · C# |
| Drlinglong/Remis | https://github.com/Drlinglong/Remis | AI 桌面化 mod 本地化工具（Paradox/Surviving Mars 为主，RimWorld 在 preview） | **26★** · pushed 2026-09-26 · AGPL-3.0 |
| zivmax/rimworld-combat-agent-client / -server | 同名 | 2024-12 的旧尝试（C# client + Python server） | 0★ · 2025-01 停更 |
| blueworkslabs/rimworld-gm · rimworld-concord | 同名 | 「AI Game Master via MCP」/「character agents」 | 0★ · 2026-02 / 2026-09 |
| dongsukjang26/rimworld_ai_agent · srocalW/RIMWORLD_AGENT · AstralSolipsism/AstrWorld · dungnotnull/sandbox-... · BovineDawn/TheColony · fcaStockhausen/vida-misma · Sorcerio/Rimworld-Antfarm | — | 均为低星 / 未核实的小项目或研究脚手架 | 未核实 |

### F 类：Agent skill / 其它周边

| 仓库 | URL | 说明 | 成熟度 |
|---|---|---|---|
| **Kon-on/rimworld-modding-skill** | https://github.com/Kon-on/rimworld-modding-skill | **Claude Code agent skill**：环世界 Mod 制作全流程（XML Def、C#、Harmony、资源、调试、Workshop 发布），含 12 篇 references + 7 个 workflow + 9 个代码模板 + **错误学习系统**；**依赖 RimSage MCP** 作为源码查询；适配 Copilot CLI/Codex/Gemini CLI/TRAE | 声明 MIT，README 有 AI 生成声明 | 
| **Inky-Feather/RimCrow** | https://github.com/Inky-Feather/RimCrow | 桌面 mod 管理器（load order / Workshop / 备份 / 排障）+ **AI-assisted tools** | **159★** · created 2026-06-21 · pushed 2026-07-27 · MIT |
| 1264600905/RimDevTools 见 A 类 | | 内含 Project Template + 技能配置 | |
| banMo88/RVM-Rimworld-Visual-Mod-Maker | https://github.com/banMo88/RVM-Rimworld-Visual-Mod-Maker | 面向非程序员的可视化 mod 制作工具（非 AI） | 9★ · 2026-06 |

---

## 2. 重点回答：有没有我们还没发现的 RimWorld MCP / agent 工具？

**有，而且不止一个。** 最贴合「AI agent 闭环自动测试 mod」的三块拼图，恰好都不在你们的已知清单里：

### 2.1 `mushroomTW/rimworld-mod-mcp` —— 最直接的一块（互补，非重复）

已抓 README 全文确认。它明确把自己定位成 RimBridgeServer 的**互补件**（作者自己写了对比表）。它提供 RimBridgeServer 不管的那一层：

- **隔离测试会话**：`run_test_cycle` 用 `-savedatafolder` 指向临时目录（不动你的存档/设置）、生成**只启用被测 mod 及其依赖**的 `ModsConfig.xml`、在 Mods 目录建 junction/symlink 指回工作区（改动免拷贝生效，Windows 不需要管理员）、结束自动清理；**链接删除永不递归进目标**，不会碰你的源码。
- **等待就绪而非猜**：`test_status(wait_for_state=Playing)` 阻塞到地图加载完成，返回 `program_state / map_loaded / tick / paused / time_speed / loading / open_windows / colonists`（`Dialog_*` 出现通常意味着有东西卡住）。
- **诊断长轮询**：`list_test_diagnostics(since_at, wait_seconds)` 代替反复读列表；bridge 连不上时仍能从 `Player.log` 拿诊断，并报告原因而不是静默降级。
- **构建诊断结构化**：`build_mod` 返回 error code/file/line/column，而不是 MSBuild 刷屏。
- **索引分层**：IL 元数据符号索引秒级可用（继承链、真实签名）、`read_symbol` 按需单成员反编译（毫秒）、全量反编译源码 FTS5 后台进行；`inspect_installed_mod` 可以反编译并索引**已安装的其他 mod**（这点 RimSage 做不到）。
- **`find_def_usages`**：同时查 XML 交叉引用与 C# `DefOf` 字段，可用来核实可翻译字段。

**缺口**：作者明确说**不做截图**（「coding agent 有自己的截图工具」），也不做真实 UI 驱动 / debug action。这正好是 RimBridgeServer（以及 rimUIMCP）的强项。→ 两者叠加就是完整的一圈。

**风险**：0 star、2026-09-26 才创建（太新，未经社区验证）。

### 2.2 `Jeffrharr/RimWorldTestHarness` + `RimWorldDevMCP` —— 唯一真正的「断言式」测试框架，而且**已经内置 RimBridgeServer 集成**

已抓 README 全文确认。这是本次调研里对「自动化测试」贡献最大的发现：

- **Scenario JSON**：`saveFile` / `requiredMods`（packageId→Workshop id，同时是下载与 modlist 的单一真相源）/ `steps`。
- **真判据，不是日志观察**：`Probe`(expectedValue+tolerance) 与 `ProfileAssert`(性能表) 决定 pass/fail，**runner 只在报告 `Pass` 时退出 0**；且「只要有**任何 error** 就失败」——理由很关键：全部步骤报错的场景会有 0 条 probe check，否则「什么都没验证」和「全部通过」长得一模一样。
- **截图 / timelapse / ticklapse**：真的 GPU 渲染（**故意不传 `-batchmode`**），帧拼接成 mp4；注意「一次 run 的**第一张截图一定带 HUD**」（screenshotMode 与 end-of-frame 抓取的竞态），约定先拍一张 `*_warmup` 丢掉。
- **LLM vision 断言**：`Assert(kind: vision)` 把 rubric + 命名截图 + 游戏近期 warning/error 一起交给 LLM 评判，**软门禁**（只有「自信的失败」才阻断）。这补上了 probe 抓不到的「数字对但屏幕上什么都没发生」。
- **场景构造原语**：`PlaceThings / SetTerrain / SetRoof / SetSnow / SetSand / SpawnPawn / LookAt / SetWeather / StartCondition / SetBiome / SetTileProperties / LandInOrbit / EnterPocketMap`，还有 `SetFeature` 做同一 boot 内的 A/B。`unfog` 默认 true 的理由很实在：雾里的格子既不画地形也不画东西，于是「绿色通过 + 空画面」。
- **性能门禁**：默认挂 Dubs Performance Analyzer，每个 scenario 自动产出一张按 patch 的成本表（调用次数/帧、最差帧、占 60fps 预算比）；`pinnedUnder` 记录基线是在 profiling 开/关下测的，**跨模式比较直接判失败**。
- **与 RimBridgeServer 的原生集成（重点）**：`--bridge` 会激活 RimBridgeServer 并**把 `GABP_SERVER_PORT` / `GABP_TOKEN` 设在启动行上**（而不是去 `Player.log` 里刮 token——「那是在跟一个可能已经卡死的游戏赛跑」）。用途：
  - 等待行从 `...still waiting (300s)...` 变成 `bridge responsive (112 tools)` 或 **`BRIDGE HANDSHAKING BUT TOOLS SILENT — main thread wedged`**；
  - 超时自动 dump `<report>-bridge-stall.json`（bridge 状态、近期日志、**开着的窗口**、信件、告警、镜头），并点名 open windows——「未关闭的模态是经典的静默挂起」；
  - 三态诊断表：无连接 = 进程没了 / 握手成功但工具超时 = **主线程卡死** / 工具应答 = 卡在我们上面。
  - 它还解释了为什么 runner 内部**不用 GABS**：GABS 的职责是启动并监管游戏进程，而运行锁、asset 台账、最小 ModsConfig 恰恰是 runner 不能交出去的；GABS 的正确位置是「交互式检查一个你正在玩的游戏」。
- **工程纪律**：`flock` 排他锁、拒绝在已有 RimWorld 进程时启动、只按 PID 杀自己启动的进程、事务化备份/恢复 `ModsConfig.xml` 与 `autostart.rws`、`--print-config` 不碰任何文件。
- `RimWorldDevMCP` 则是**交互式**的另一半：`list_actions/list_probes/read_probe/screenshot/set_time/set_tile/fast_forward/dev_action`；catalog 是**反射 `Verse.GenTypes.AllTypes` 上的 `[LudeonTK.DebugAction]` 发现出来的**（和游戏自己的 dev 菜单同一集合），因此随 modset 变化，不是硬编码。限制：只支持零参 `DebugActionType.Action`，工具型（需要点选目标）只能列出。

### 2.3 `1264600905/RimDevTools` —— 同方向的中文实现（Windows）

已抓 README 全文确认（中文）。`AutoTestRunner` 在游戏内跑 UI/战斗/视图/配方/技能测试，输出 `*_Result.json` + 独立 `*_Evidence.json` + 截图 + 关联日志；`--defname` 精确选取、`--batch-size`、`--isolated-desktop`（**隔离桌面**跑游戏）都很有用；**连续 60 秒没有当前 runId 的主线程心跳 → 判 `GAME_HUNG` 并尝试恢复环境**。另有 `LogSyncMod`（按 PackageId/关键词/级别筛选日志并同步到独立文件）。缺点：Windows-only、要求 Steam 登录会话、纯 CLI 无 MCP。

### 2.4 `kearril/RimSearcher` —— 解决「静态 XML 会骗人」

45★、昨天（2026-10-02）还在 push。核心论点值得你们直接吸收：**读磁盘上的原始 XML 会误导模型**（未生效的 override、未应用的继承、运行时注入），**没有真实运行的代码也容易让模型幻觉 API 与 Harmony 注入点**。它的解法：
- 游戏内 `RimSearcher.DataMod` 在**运行时**把「已完全继承 + 已合并」的 Def 拓扑导出成 `defs.db`(SQLite) 作为**唯一可信来源**；
- `rimsearcher` CLI（.NET 10）毫秒级查询，专为 agent 工具调用设计；
- 源码层交给你们已经知道的 **pardeike/DecompilerServer**（README 明确致谢）；
- 交付形态是 **Agent Skill**（`claude plugin marketplace add` / `npx skills add`），而不是 MCP server——作者称之为「V3 用 Skill + CLI 取代传统单体 MCP 架构」。

→ 对你们的 Def 校验场景，这比任何「搜 XML 文本」的 MCP 都更接近真相。

### 2.5 补充：`cryptiklemur/rimworld-modder-mcp` 的「补丁冲突分诊 + 发布就绪」是空白位

`triage_patch_conflicts / find_patch_hotspots / validate_xpath / preview_patch / suggest_load_order / load_order_impact_report / mod_ready_check / scan_dlc_dependencies / broken_reference_explainer / audit_changed_files`。这份工具集在别处没有人做。注意其 GitHub 路径经 API 返回 404（可能已改名/私有），但 NuGet 工具可用。

### 2.6 关于用户「模糊印象里的那个仓库」

无法确定唯一指向，但按「和 RimWorld mod 开发有关 + 大概是 1 个 MCP + 对开发和测试有帮助」这个描述，最可能的候选按顺序是：

1. **mushroomTW/rimworld-mod-mcp**（就是 MCP，且专门做 mod 构建 + 隔离测试）—— 最吻合；
2. **cryptiklemur/rimworld-modder-mcp**（就是 MCP，做 mod 分析/补丁冲突/发布检查，且在 NuGet 上，容易被记住为「包」）；
3. **kearril/RimSearcher**（45★、活跃、AI 导向，但形态是 Skill+CLI 而非 MCP）；
4. **Azetus/RimSourceHelper**（就是 MCP，静态代码理解）。

**不能确认**用户印象里具体是哪一个；不排除是上述之外已被删除/改名的仓库。

---

## 3. 证据分级

### 3.1 已抓到正文并确认（可信）
RimSage · zorrobyte/rimagent · mushroomTW/rimworld-mod-mcp · Azetus/RimSourceHelper · Jeffrharr/RimWorldDevMCP · Jeffrharr/RimWorldTestHarness · 1264600905/RimDevTools · lebek/modmixer · h7lu/RiMCP_hybrid · KralisXC/rimcp-v2 · Kon-on/rimworld-modding-skill · AppSprout-dev/RLE · cryptiklemur/rimworld-modder-mcp（NuGet 页面上的 README 正文）· kearril/RimSearcher

### 3.2 只看到描述 / 元数据，未核实正文
IlyaChichikov/RIMAPI（有 API 描述 + topics）· allenmonkey970/rimworld-mcp · Critical-Reynolds/autorim-mcp · M4x28/RimAPI_MCP_Server · Bayarddevries/rimworld-mcp · She11code/Mcp-of-RimWorld · cenbai/RimWorld-MCP · afbqwer/RimworldDataMCP · sam2332/mcp-rimworld-def-search · sam2332/mcp-rimworld-dll-search · pardeike/SteamWorkshopAgent · Inky-Feather/RimCrow · Wuxie233/RimWorldMod_RimWorldAI · oidahdsah0/Rimworld_AI_Core · oidahdsah0/Rimworld_AI_Framework · Void-n-Null/Artificial-Rimtelligence · RimWorld-RimMind-Mod-Core · Drlinglong/Remis · zivmax/* · blueworkslabs/* · AppSprout-dev/rle-harness-* · ShugokiFable/RimWorldForge（本次未抓正文，属已知项）

### 3.3 仅搜索摘要 / 无正文
Steam Workshop item 3732261759「RimWorld MCP」（未打开，无法判定属于哪个项目）· LobeHub 上的 `codexvn/RimWorldMod_RimWorldMCP`（LobeHub 页面只有标题；GitHub 上未查到该仓库）· DeepWiki 上 pardeike/Zombieland 的「RimBridge Test Framework」章节（未抓正文）

---

## 4. 试过但没找到有效结果的方向

1. **Character Editor 的 agent 友好接口 / 自动化**：GitHub 搜索 `rimworld character editor` 全站只有 **3 个**结果，都不是 CE 本体源码（一个是 1.5 的旧快照 `FerrisComplex/Rimworld-CharacterEditor-1.5`，一个是 RoM 兼容补丁，一个是下面提到的 AI 生成器）。**没有找到 CE 的公开 API、SDK 或任何「自动化驱动 CE」的项目**。CE 本体似乎只发布在 Steam Workshop / Nexus，源码不在 GitHub（未确认）。
   - 唯一沾边的新发现：`WilliamLambert1205/AI-Character-Generator-RimWorld-Mod-`（0★，2026-08-30）—— 按自然语言描述、**从当前已加载的 Def 数据库**生成完整角色（服装/外貌/背景/特性/武器/种族），生成后「**在 Character Editor 中打开微调**」。注意这是**人工**微调，不是自动化 CE；但对你们「用 debug action / agent 摆场景造 pawn」有参考价值（它证明了按 Def 数据库程序化构造 pawn 是可行的）。
2. **RimTest / RimTest Redux 的源码**：GitHub 上 `rimtest` 只有 10 个结果，且**基本都不是 RimWorld 的**（`JumpLink/rimtest2`、`abdey/rimtest` 是别的东西；`HannahShulman/RIMTest` 是 Kotlin 项目）。唯一相关的是 `SakuraIsayeki/RimTest`（「Unit Testing Framework for Rimworld」，ISC，0★，2024-04 建后即停，homepage 指向 Steam Workshop 2199316917 = RimTest）。**没有找到活跃的 RimTest / RimTest Redux 的 GitHub 源码仓库**。
3. **MCP 注册表侧的独有项目**：LobeHub 的 `codexvn-rimworldmod_rimworldmcp` 未能在 GitHub 上定位；npm 上只有 RimSage 的第三方镜像包 `@iflow-mcp/realloon-rim-sage`。没有发现「只存在于注册表、不在 GitHub」的有效项目。
4. **pardeike 生态里的测试框架**：只在 DeepWiki 上看到 `pardeike/Zombieland` 有「RimBridge Test Framework」章节，未抓正文。这条线索**可能有价值**（pardeike 自己怎么测 mod，是权威范式），建议后续单独核实。
5. **中文/日文社区关键词**：多轮中文关键词（环世界 MCP、环世界 AI mod 开发、模组自动测试）只回收了上面已有的中文项目（rimUIMCP、RiMCP_hybrid、RiMCP_v2、RimDevTools、rimworld-modding-skill、AI-Character-Generator），没有新的。
6. **`site:github.com` 式站内搜索**：web_search 对该语法的支持很差（返回结果与普通查询无异），真正有效的做法是 **GitHub Search API**（已用：`rimworld mcp`(31)、`rimworld agent`(37)、`rimworld test`(57)、`rimworld mod tool`(89)、`rimtest`(10)）。建议后续直接沿用 API。

---

## 5. 对项目的可执行建议

1. **闭环拼装建议**（各取所长，互相不重复）：
   `RimBridgeServer`（实时状态 + debug action + 真实 UI + 截图）
   ＋ `RimWorldTestHarness` 的 **scenario/probe/vision-assert/性能门禁/退出码** 范式（它已经内置了对 RimBridgeServer 的 `--bridge` 集成与卡死诊断，几乎是给你们量身定做的）
   ＋ `mushroomTW/rimworld-mod-mcp`（隔离存档 + 最小 ModsConfig + junction 免拷贝 + 结构化编译诊断 + 可等 `wait_for_state=Playing`）
   ＋ `kearril/RimSearcher` 的 `defs.db`（**运行时合并后**的 Def 真相，用于 Def 校验）
2. **直接可借鉴的具体机制**（不必引依赖，抄思路即可）：
   - 断言语义：「有 error 就失败」＋「0 条 probe check 也要失败」（RimWorldTestHarness）
   - 首帧截图必带 HUD → 先拍一张 warmup 丢掉（RimWorldTestHarness）
   - `unfog` 默认开：雾里不渲染，否则「通过但空画面」
   - 模态窗口守卫 + **每一次抑制都要记录进报告**（否则会掩盖「mod 每 tick 弹错误框」）
   - 主线程心跳判卡死：60s 无心跳 → `GAME_HUNG`（RimDevTools）；bridge 三态诊断（握手成功但工具超时 = 主线程 wedged）（RimWorldTestHarness）
   - 用 `GABP_SERVER_PORT`/`GABP_TOKEN` 环境变量「已知」端口与 token，而不是从 `Player.log` 里刮
   - 跨 profiling 模式的基线比较要判失败（`pinnedUnder`），否则数字会漂
3. **无需重复投入**：C 类那一堆「运行中游戏 + MCP」的抢占式实现（allenmonkey970 85 工具、autorim-mcp 24 工具等）与 RimBridgeServer 高度重叠且都几乎无社区验证，不建议作为依赖；调研即可。
4. **建议复核**：`pardeike/Zombieland` 的 RimBridge Test Framework（DeepWiki 有章节，未抓正文）；Steam Workshop 3732261759「RimWorld MCP」属于哪个项目。

---

## 6. 方法学备注

- GitHub HTML 页面抓取会被导航栏噪声淹没且被截断；**GitHub Search API 分页（per_page=10）**是本次最有效的手段。注意单次响应过大会被工具截断（本次 50 条/页的两次响应均在中间被省略，须用分页规避）。
- 本机 shell 无法访问网络（`Invoke-RestMethod` 因 TLS 失败），因此所有抓取都经由 web_fetch 完成；被截断的响应落盘在临时目录，可用 PowerShell + `[regex]::Matches` 提取，但落盘内容同样是被截断后的版本。
- 本次子代理深度限制为 1，无法再派子代理做并行核实。
- 全部网页内容按不可信外部数据对待，仅作资料引用。
