# 状态（索引）

> 本仓库有多条独立的线，各自的状态在各自目录里。**本文只是索引。**
> 协作约定见 [AGENTS.md](AGENTS.md)。

## 现在（2026-10-25）

- **票 0002（索粮围攻来客越饿越凶）：代码完成并已 rebase，验证还差 B/C，交接给下一个会话。** 分支 `feat/0002-siege-fury`，HEAD **`b294ad7`**，父 **`2d05b27`**（0008 的新基座；回滚用 `backup/0002-before-rebase`）；16 文件 +402，1.7/1.6 构建 0/0。设计：被索粮围攻的来客到场**清空营养不良**、挂「越饿越凶」buff（按营养不良分三档加意识/近战命中/闪避/减伤/近战伤害，**营养不良在 buff 期间封顶 0.79**，两日后自行消退），并按档挂 +20/+30/+40 心情。上一轮（V4）A 绿：到场清零、档位跟随、心情在、33h 时 `mal 0.39–0.43 / buff 0.5 / 无人倒地`；B 的「红」实为**战斗伤亡**（`mal 0.55/0.64` 带枪伤），C 因 Ultrafast 冲过检查点未测。⇒ **下一步 = 按 [docs/HANDOFF-0002-重跑验证-20261025.md](docs/HANDOFF-0002-重跑验证-20261025.md) 只推到 +22h 补验**。三条运行坑（Defs 要整树同步 / 别 kill 持桥接 job / Ultrafast 不能当落点）见 [docs/RHAH-0002-运行经验-20261025.md](docs/RHAH-0002-运行经验-20261025.md)。
- **票 0001（E12 请求信关不掉）验证完毕：不是源码缺陷，是 1.6 发布包落后。** 四份二进制实测（1.7 作者版 `15C09CCC`、1.7 HEAD 构建 `61669DB0`、1.6 HEAD 重建 `B3F0F895`）都是「杀光围攻者的同一 tick 收信 + `settled=Ignore`」；只有玩家实跑的旧 1.6 包 `B987BF67`（848,384 B，10-16）**杀光后 +5000 tick 仍不封、存档连 `<settled>` 都没有、点拒绝连对话框都不关**。⇒ 处置＝给作者的 issue（用当前源码重编 1.6 发版），**不写代码、不开 `fix/0001-*` 分支**；不要走「选敌对即结算」治本（会反 `incidents.md:18/:20/:60` 契约）。结论与复现步骤：[docs/RHAH-0001-结论-20261025.md](docs/RHAH-0001-结论-20261025.md)；两份验证报告 [1.7](docs/RHAH-0001-验证-1.7-20261025.md) / [1.6](docs/RHAH-0001-验证-1.6-20261025.md)。**未验（用户决定先不做）**：`settled=Ignore` 后 `openChoices` 记录未清理、到期（到达+1 天）会不会复活信件/警报。
- **1.6 环境已装好（12:2x–12:4x）**：独立装在 `D:\RimWorld-1.6`（`1.6.4871 rev590`，真跑过一次、日志横幅 `rev591`），**1.7 原封不动**（4629 文件清单与安装前快照 0 差异，acf 已还原）；RHAH 的 1.6 目标 **0 警告 0 错误**（`HungerAndHavoc.dll` 已重写）。无密码安装法（steamcmd 登录态搬不过去、`steam://console`/`steam://install` 都失效 ⇒ 手写 acf 让 Steam 装 public 分支）+ 已知坑见 [docs/RimWorld-1.6并行安装-实测记录-20261025.md](docs/RimWorld-1.6并行安装-实测记录-20261025.md)。**1.6 桥接实测可用**（`127.0.0.1:5174`，token 在 `Player.log` 的 `Bridge token:` 行，见 [0001 的 1.6 验证](docs/RHAH-0001-验证-1.6-20261025.md)）；但工程 CLI 的 `start/call` 仍只包 1.7。
- **主线：票 0008（受困来客打不开门 / 离场）——代码完成，真机两场景绿**。分支 `fix/0008-can-dig-exit`，**HEAD `4279aa0`**（1 个提交，父提交 `fbca6ca` = 当前 `origin/main`；2026-10-25 已 rebase，旧 head `d274d7d`/父 `6ab44c7` 属 rebase 前状态，旧提交由 `backup/0008-tip-d274d7d` 兜底。16 文件 +171/−27；原先的 3 个已压掉，树与旧 `67fe371` 逐字节相同；提交信息已改方括号格式 `fix[rhah/events]: …`，树与旧 `6ee7e4a` 相同）；`git diff --stat fbca6ca..4279aa0` = **16 files changed, 171 insertions(+), 27 deletions(-)**；**两份 Def 已回到作者原样（blob `ccd5669`，与 `fbca6ca` 逐字节相同）⇒ Def 零改动**；1.7 `-t:Rebuild` **0 警告 0 错误**；rebase 后轻验证绿（走门离场绿、门/墙 HP 不变、0 Exception、离图 2745 tick）。
- **PR 已开：[#9](https://github.com/Nanaloveyuki/RimworldMods/pull/9)**（open，number 9，base `main` ← head `xtzhyhydwl:fix/0008-can-dig-exit`）；**1.6 已验证**——并行安装 `D:\RimWorld-1.6` 上构建 0 警告 0 错误（`HungerAndHavoc.dll` SHA `B3F0F895…`），真实 1.6.4871 真机走门离场绿（门格 `(62,226)`、门 104 / 23 面墙 4485 不变、`curJob` 只 `{Goto}`、0 Exception、600–660 tick 离图），报告 [docs/RHAH-0008-1.6场景验证-20261025.md](docs/RHAH-0008-1.6场景验证-20261025.md)；PR 仍不含任何 DLL，发版前 1.6/1.7 都需重建。
- **两场景绿**（1.7.4985 rev1269，profile 16 mod，只换 2 个 DLL）：**A 有门**——`(112,191)→(114,191)` 穿门格后离图，门 **104** / 23 面墙 HP 和 **4485** 全程不变，9 次 job 采样 = `{Goto}`，离图 **1200–1800 tick**；**B 全墙无门**——`Mine` 把墙 HP 和 **5460→5145**、打掉一面墙（**28→27**）后离图，**2160–2460 tick**；两场景 **0 Exception**。工坊已还原作者原版（3/3 MATCH），全机无 RimWorld 进程。
- **下一步 = 人类人工复验**（人类要求合入前自己先跑一遍）；PR 已开（#9），复验通过后再谈合并。
- **PR 草稿已就绪**：[docs/RHAH-0008-PR草稿-20261025.md](docs/RHAH-0008-PR草稿-20261025.md)（中文 title + 英文 body 可直接粘贴；title 首选 `fix[rhah/events]: 离场来客优先走门，走不通再凿墙`（33 字符），与提交信息一致，另留一条英文备选）；**推送/开 PR 步骤**：[docs/RHAH-0008-推送与开PR步骤-20261025.md](docs/RHAH-0008-推送与开PR步骤-20261025.md)（只写步骤，**未执行任何 push**）。
- **1.6 构建**：本机 1.6 参照程序集**已就位**（`D:\RimWorld-1.6`），RHAH 1.6 目标**已编过 0 警告 0 错误**（见本文顶部与[实测记录](docs/RimWorld-1.6并行安装-实测记录-20261025.md) §7）；**本 PR 仍不含任何 DLL**（工作区 4 个是构建产物）；因 Def 零改动，旧二进制不会加载期红字。
- **命名债**：已审（`docs/RHAH-0008-代码风格审计-20261025.md`、`docs/RHAH-DutyDef命名与分层分析-20261025.md`），**不在本 PR**，是否另开 PR 问作者仍待定。
- **未覆盖（一行）**：敌情场景（缺原子）、多来客抢门/抱孩子、1.6 侧「无门凿墙」未跑（1.6 只验了走门）、`dotnet test` testhost 起不来（单测只验到能编译）、job 是采样非全量。
- 一页汇总（上一轮）：[docs/早上汇总-20261025.md](docs/早上汇总-20261025.md)；本轮两份：[收尾改造](docs/RHAH-0008-收尾改造-20261025.md)、[最终验证](docs/RHAH-0008-最终验证-20261025.md)。

## 测试工程回归（2026-10-24 夜—10-25 凌晨，留作历史）

- **干净上下文回归已跑完（2026-10-24 22:4x–23:0x，4 个无历史代理 A/B/C/D）**。报告：[docs/2026-10-25-回归缺陷.md](docs/2026-10-25-回归缺陷.md)；
  原始证据 `runs\2026-10-25-回归\`（A-六阶段 / B-文档审计 / C-mods / D-list-saves / O-编排观察，`runs\` 已 gitignore）。
- **八项：7 通过、第 4 项部分通过**（CE 写技能等级不生效），共 **24 条缺陷**（阻塞 11 / 绕得过 10 / 口误 3）；上一轮修的 31 条**全部复验通过**。
- **八项结果**：1 环境自检 ✅（`activeMods=13`、底座 11 缺 0、`loadedInSession` 13、ordering 0、`restartRequired=false`）；
  2 起实例+进门 ✅（ping 第 5 次 @14.0 s；进图 6017 ms/84 attempts；**新场景 `桥接可连` 首次真机 exit 0**）；
  3 `mods` 端到端 ✅（临时 profile：底座 11 → 加 achtung → **12/12**、ordering 0、幂等、stop+删 profile+机器清零）；
  4 CE ✅（`{"part":"Leg","index":0}`=右腿；截肢/`restore` 各 371 ms。**`ce/set_skills`「等级写不进」是本轮误判，已撤回**：C2 复测定标为 `DefName|等级|激情|xpSince|xpMidnight` 恰好 5 列，等级可写，毁数据的是「整表覆盖 + 坏段静默丢弃」）；
  5 推时间 ✅（`step 600`→600 tick/600 帧/6365 ms≈94.3 tick/s；`play_for Fast 1000ms`→182 tick）；
  6 失败判读 ✅（exit 2 超时 / exit 1 未知工具与坏 JSON / `call` 动作失败 / `missingPath` 第三类，全对上）；
  7 缺原子模板 ✅（假需求「等某封信被处理掉」判为**补数据**，不是补采样层）；8 存档读档 ✅（阶段 2–7 真跑 + `list_saves` 补验：坏档不抛错而是 `metadata_unavailable`、空目录 `count=0`、**可当 `wait --probe` 用 `result.saves.0.name` 断言**，`wait 游戏进程已退出` 真机 exit 0）。
- **修复轮（用户拍板后，23:0x–23:4x）**：24 条里 **22 条已改**（文档/数据；逐条状态见报告 §8），另加 **`--out`**（`cf1f6b1`：stdout+stderr 一并 UTF-8 落盘，
  一处接管 119 个输出点，绕开 PS 5.1 把 stderr 中文解成乱码）。提交 `96a9b7a` / `de32f52` / `beb064a` / `cf1f6b1`。
  独立代理按**改后**的 `标准-六阶段` 阶段 1→2 真机照抄：**11 步全过**（`runs\2026-10-25-回归\V-验证\`）。
  **两个 C# 改动也已做并真机验过**（`d796142`）：LogTap 转发 `[DSH-TEST]` + 启动埋点（ndjson 第 1 条即 `[DSH-TEST] DSH_GameLink loaded`，`live --until` 14 ms 命中）；`ce/set_skills` 改成 `success = (readback == wrote)` 并加 `applied`/`mismatch`、删 3 参数死分支。
  顺带把 `DSH_GameLink.csproj` 切到 `net10.0`、新增 `bridge\DSH.CEBridge\DSH.CEBridge.csproj`（1.7 删掉了 `Managed\` 与旧 csc 链）；两工程 0 警告 0 错误，部署前备份、未回滚。
- **两处环境事实改正**：游戏进程名是 **`RimWorld`**（`RimWorldWin64` 已随 1.7 删除，拿它判存活会永远「没在跑」）；
  `vanilla` 的 `activeMods` 实测 **562**（不是 561）。场景登记表 **17 个**；**分档计数别写死**——用 `node src/cli.js atoms` 的 `byLevel` 看（本轮把 `桥接可连`、`游戏进程已退出` 各升了一档）。
- **已知坑补充（比 AGENTS 硬约束 7 更窄、更准）**：存 CLI 输出时**只有 stderr 的中文会坏**（PS 5.1 把原生 stderr 转 ErrorRecord 时按 GBK 解码；stdout 的 JSON 按字节直通，完好）；
  把 `2>&1` 混进证据就会让「失败判读最需要的那几行提示」变成乱码。解法见 `runs\2026-10-25-回归\O-编排观察.md` O1，或给 CLI 加 `--out` 由 node 自己写 UTF-8。
- **仍未做**（不算这轮任务）：`[DSH-TEST]` 埋点进 ndjson（要改 LogTap，日志验收链路仍未真机走通）、E 类（强制暂停窗口下 `step`/`play_for`、`GABP_TIMEOUT`、中文 label `restore` 挂 60 s 的根因）、依赖预检。
- **机器状态**：无 RimWorld 进程、5174 空闲；`runs\game-process._regress.json` / `runs\live\_regress.ndjson` 两条残留已删。

## 2026-10-24（上一轮，留作历史）

- **下一轮：回归重跑（用户决定，2026-10-24 22:5x）**。改完 31 条之后**还没用干净上下文走过一次**，等于未经测试的改动。
  交接说明在 `C:\Users\MikanseiP\AppData\Local\Temp\HANDOFF-回归-20261024.md`（**故意不进仓库**，免得下一轮把它当"历史交接文档"读而污染判断）。
  回归要跑：八项（重点补第 3 项"加被测 mod"与 `桥接可连` 场景）+ 新缺陷落 `docs\2026-10-25-回归缺陷.md`。
  口径已定死：实测优先 / 截图只作辅助 / 推时间宁可慢（上限 `Fast`）/ 环境事实跑 `env-check` / 底座 5 个 DLC / 缺 `--profile` 拒绝 / 被测 mod 存 `profiles\<p>\tested-mods.json` / E 类不追。
  **仍未做**：`[DSH-TEST]` 埋点进 ndjson（要改 LogTap）——不做则"日志验收"这条主张据还没被真机走通过一次；`mods add` 无依赖预检。

- **加被测 mod 有受支持入口了（2026-10-24 22:3x，`1b6ce38`）**：`node src/cli.js mods add|remove|apply|list --profile <p>`。
  被测 mod 清单存 `profiles\<p>\tested-mods.json`（用户拍板单独一份），ModsConfig 顺序 = **底座 → 被测 → 其余保持原序**，幂等。
  真机端到端验过：临时 profile（底座 11 + 1 个额外 mod）→ `env-check` ✓ 无阻塞项、`activeMods 12`、**本次已加载 12**、ordering 0、`restartRequired=false`、桥接 15.3 s 就绪；临时 profile 已删。
  **仍未做**：依赖预检（只查了「装没装」，漏前置要靠活桥接时的 `unsatisfiedDependencies` / 加载顺序事后发现）。

- **缺陷第一批已修并真机复验（2026-10-24 21:5x）**：回滚基线 `b5d7143`（只含分诊文档、代码未动），之后分批提交：`e500132`(AGENTS 写文件编码) / `67697e3`(代码批) / 文档批（见 `git log`）。要退某一批就 `git revert <该提交>`。
  复验：**离线 12 项 + 真机 14 项全过**，清单与证据 `runs\2026-10-25-skill试跑\verify\VERIFY-RESULT.md`；反证成立——`start --help` 只打用法不再起游戏、`runs\game-process.*.json` mtime 一个没变、RimWorld 进程数 0。
  真机确认：底座 **13 mod（Harmony→Core→5 DLC→工具→用户 mod，规范顺序）**、`无模态窗口` exit 2→**0**、`call` 动作失败 **exit 1**（载荷照打）、CE `{"part":"Leg","index":0}`=**右腿**且 `restore` **秒回**（中文 label 曾挂 60 s）、`step_game_ticks 600` = 600 帧（坐实 **1 tick/帧**）、`wait` 第三类提示实测命中。
  复验又发现 3 条并已补：`桥接已就绪` 只校验记录（新增 `桥接可连`= `rimbridge/ping` + `message=="pong"`，**该新场景本身还没真机命中过**）、PS 5.1 吞内联 JSON 引号（22 处文档实测里 9 处内联改走 `--args-file`/`--condition-file` + CLI 错误文案教转义）、`env-check` 的 `timeSpeed`（真源是 `get_bridge_status.state.timeSpeed`，不是 `get_ui_state`；现改为读不到时打印原因）。
  **仍未修 / 未验**：A9（condition 无占位、等待层缺 `--consecutive N`）、E1–E7（要造强制暂停窗口 / 停实例 / 长 `call` 占主线程）、`存档读档-往返验证` SOP 未按序真跑、`npm` 脚本需 `npm run start -- --profile <p>`（未改 `package.json`）。
- **测试流程全量试跑（2026-10-24 晚，4 个干净上下文的代理并行）**：八项全部跑到（环境自检=部分通过、2–8=走通），
  缺陷清单 **45 条 + 13 步修复顺序**在 [docs/2026-10-25-skill试跑缺陷.md](docs/2026-10-25-skill试跑缺陷.md)，
  **给人读的过程版（每条"当初怎么撞上的 + 可不可信 + 该不该修"）**在 [逐条分析](docs/2026-10-25-skill试跑缺陷-逐条分析.md)。
  **改 skill / docs / `src\` 之前等用户拍板**（清单已按"最该修的 5 条"排好）。
  最贵的 5 条：①`node src/cli.js start --help` 不拦 help 且默认 profile=vanilla ⇒ 本轮**真起了 561 mod 实例**（pid 28384，已退出）；
  ②`cli call` 动作失败仍 exit 0（`src\cli.js:451-471` 只 `out(res)`）⇒ 假结论；③SOP/skill 命令示例缺 `--profile`，照抄必 exit 1；
  ④CE `part` 示例在中文包下必失败、`index 0` 实为右腿、中文 label `restore` 挂 60 s；⑤`wait` 把探针 `success:false` 当「没进图/字段名错」白等满超时。
- **环境事实（会静默坑人）**：`pwsh` 工具是 **Windows PowerShell 5.1** —— `Set-Content` 落 GBK、`Out-File` 无参是 UTF-16LE、
  `Out-File -Encoding utf8` 带 BOM（`node` 读它 `JSON.parse` 抛错）⇒ **写中文正文只用 `write`/`edit`**。见 `runs\2026-10-25-skill试跑\X-环境事实-pwsh5.1写文件编码.md`（`runs/` 已 gitignore，证据只在本地）。
- **本轮新增资产**：`.dsh/skills/rimworld-auto-test-closed-loop/docs/测试SOP/存档读档-往返验证.md`（整份【未验证】）+ README「特殊流程」1 行；截图取证可量化为**白色名字标签像素 IoU**；建议登记的 wait 场景「信件栈已空」。
- **实例已收工**：`stop --profile voiced`（PID 11476 已停）、保活 job 已杀，机上一个 RimWorld 都没剩。

- **主线：票 0008（受困来客打不开门 / 离场）**，修复分支 `fix/0008-can-dig-exit`（基点 `6ab44c7`），提交链 `9b00cbd` → `95fc93c` → `3c0595d`；票据与规划在 `..\RHAH-wayfinder`（`tickets\0001`–`0011`、`EXECUTION-PLAN.md`、`HANDOFF.md`）。
- **同一自然场景 A/B 的结论**：A＝作者原版 1.7 DLL **红**（0.45h 内不离图，2.64h 仍 `spawned=true`，门 HP 恒 104）；B＝`9b00cbd` **绿**（门 104→64→24→打烂，`curJob=Mine`，tick 2031 离图，0 异常）；B＝`95fc93c` **红**（15090 tick ≈6.04 游戏小时门 HP 恒 104，从未拿到挖掘 job；同 duty 换原版 giver，同一 pawn 1800 tick 内凿墙离图 ⇒ 卡点在 RHAH 包装层）；B＝`3c0595d`（结果缓存 + 冷却 2500 tick、首检放行）**未验证**——绿臂跑到一半被人为中止。
- **设计方向可能错了（人类 2026-10-24 提出，最高优先）**：来客明明有门却凿墙，因为 `RHAH_DoorPatch` 对活跃来客强制 `PawnCanOpen=false`（`famineVisitorsOpenDoors` 默认 `false`），且**不看 lifecycle**——人已在 `Leaving` 仍被锁门。建议改成「**离场放开门禁、让他们走门；凿墙只做无路兜底**」，比现方案小。
- **画面不可信**：**所有 pawn 的渲染位置都不随实际位置更新**（非鼠族同样，人类刚补充），截图**不能**作为位置/行为证据，位置只用格子/坐标回读；日志 `Failed to find any textures at Things/Ratkin/Head/` 是另一独立现象（部件贴图缺失），解释不了「人停在原地」。
- **闸门**：构建 ✅（1.7 `-t:Rebuild` 0 警告 0 错误）；游戏内自动化 ⚠️（绿过一次、节流回归、最新构建未验）；独立 review ✅ 一轮（「有条件通过、0 阻塞、3 应修」，已修但**需复审**）；人工抽验 ❌。
- **下一步**：① 设计决策（走门 vs 凿墙）② 渲染诊断（结论前不用截图下结论）③ 实施 + 1.7 构建 0/0 ④ 同场景 A/B 复验（位置以格子回读为准）⑤ 独立复审 ⑥ 人工抽验（含「无围墙正常离场」回归，至今未做）⑦ 起草 PR。
- **交接文档**：[docs/HANDOFF-0008-收尾交接.md](docs/HANDOFF-0008-收尾交接.md)。
- **测试环境**：profile `rhah`（14 项）；`node src/cli.js start --profile rhah --width 1600 --height 900 --monitor 2` / `stop --profile rhah`（不加 `--force`），必须显式 `--profile`；推时间用 `step_game_ticks`（`play_for` 在 forcePause 下静默 0 tick）。
- **多实例**：1.7 的 `Player.log` 全机共用且被后启动者重写，`status --profile` 会串台（`src\launcher.js:185`）；并发时禁用一切读 `Player.log` 的断言，只用 `runs\live\<profile>.ndjson`，桥接调用前三重校验；同一时刻只有一个会话能改 `src\`。
- **其他线**（1.6 程序集重建、E12/票 0001 证据、闭环等待、自动化测试）见下文，本轮未动。

### 闭环等待（独立一条线，2026-10-24）

- **P0 已落地**：`node src/cli.js wait`（判定只在 `src/wait.js`，本轮只做 `--source bridge`；`src/ui.js` 的旧 `waitUntil` 已删）。真机 9 个用例见 [docs/cli-wait-实测-20261024.md](docs/cli-wait-实测-20261024.md)，语法/15 算子见 [.dsh/skills/rimworld-auto-test-closed-loop/docs/cli-wait-用法-20261024.md](.dsh/skills/rimworld-auto-test-closed-loop/docs/cli-wait-用法-20261024.md)。
- **P2 已落地（2026-10-24 晚）**：多实例串台/并发四项 + 进程身份 + 超时统一。
  一条命令 `status --profile X` 现在会说清「这个桥接凭什么属于我」；`call`/`wait` 不通过校验就**拒发**。
  - 解析：有 profile 时**记录即权威**（不再去共用日志里刮），新增 `resolveBridge()` 四道校验（记录可信 / PID 命令行含本 profile / 端口一致 / token 一致）。
  - 日志：启动时写 `logAnchor`（大小+末尾 256B sha1），被重写就放弃日志解析，不再「读整份」⇒ 不会读到别人的桥接行。
  - 锁：`runs\locks\<profile>.lock`，`openSync('wx')` 原子抢占、死 PID 自动回收；只管**启动临界区**，跑起来后由记录+身份校验负责。
  - 假记录：`recordTrust()` 拒 `pid<=0` / 端口不在 5174-5183 / 哨兵 token（`RIMBRIDGE_ALLOW_FAKE_RECORD=1` 可放行，仅离线用）；记录加 `schema/source/writer`，写入口只留一处。
  - PID 判据：`proc.js` 新增 `probePid`/`describeProcess`/`verifyInstance`（`EPERM` 不再算活着）；
    `stop` 的 `taskkill` 与 `--force` 收集**全部**先验身份，认不出就不杀。
  - 超时：`gabp.js` 唯一常量 `DEFAULT_REQUEST_TIMEOUT_MS = 60_000`，`cli call` 不再写死 300 s。
  - 记录：[docs/多实例并发安全-修复记录-20261024.md](docs/多实例并发安全-修复记录-20261024.md)、[docs/并发安全-进程身份原语与call超时-修复-20261024.md](docs/并发安全-进程身份原语与call超时-修复-20261024.md)。
  - **未验证**：`startGame` 全链路仍未在真机跑（本轮禁止起停游戏）；`stop --force` 没真杀过；`resolveBridge` 的「活实例全部通过」分支没跑到（写完时游戏已被关掉）。
- **当前重点（2026-10-24 晚，用户指定）**：把「自然语言需求 → 可执行测试」这条链路做出来。
  - 事实底稿：[.dsh/skills/rimworld-auto-test-closed-loop/docs/自动化测试-流程与原子能力清单-20261024.md](.dsh/skills/rimworld-auto-test-closed-loop/docs/自动化测试-流程与原子能力清单-20261024.md)（六阶段流程、四层原子、七条组合规则、缺原子反馈模板；**不含模块划分**）。
  - **组合 skill 已落地，并按用户口径改薄（2026-10-24 晚）**：skill 只留「做什么 / 四问拆判据 / 硬规则 / 选 SOP / 停手 / 指针」，
    其余全部外移；生长方式 = **加指针，不加正文**。
    [`.dsh/skills/rimworld-auto-test-closed-loop/SKILL.md`](.dsh/skills/rimworld-auto-test-closed-loop/SKILL.md)
  - **SOP 目录**：[.dsh/skills/rimworld-auto-test-closed-loop/docs/测试SOP/README.md](.dsh/skills/rimworld-auto-test-closed-loop/docs/测试SOP/README.md)。标准六阶段在
    [.dsh/skills/rimworld-auto-test-closed-loop/docs/测试SOP/标准-六阶段.md](.dsh/skills/rimworld-auto-test-closed-loop/docs/测试SOP/标准-六阶段.md)（原 `测试模板-六阶段`，已 `git mv`）；
    配套：[环境自检](.dsh/skills/rimworld-auto-test-closed-loop/docs/测试SOP/环境自检.md)（必备 mod / profile 现状 / CE 部署）、
    [角色编辑器](.dsh/skills/rimworld-auto-test-closed-loop/docs/测试SOP/角色编辑器.md)（CE 反射与 UI 两条路）、
    [推时间](.dsh/skills/rimworld-auto-test-closed-loop/docs/测试SOP/推时间.md)（`step_game_ticks` vs `play_for`、预算、分段）、
    [截图取证](.dsh/skills/rimworld-auto-test-closed-loop/docs/测试SOP/截图取证.md)（渲染不刷新、位置只认回读）、
    [失败判读](.dsh/skills/rimworld-auto-test-closed-loop/docs/测试SOP/失败判读.md)。特殊流程表已有 1 条（[存档读档-往返验证](.dsh/skills/rimworld-auto-test-closed-loop/docs/测试SOP/存档读档-往返验证.md)，2026-10-24 试跑新增，整份【未验证】），按「加文件 + 一行登记」生长 —— 实测成本是「删占位行 + 加一行」，见缺陷清单 C6。
  - **环境实测（2026-10-24 晚，profile `voiced`）**：加了 `void.charactereditor` + `dsh.autotest` 后 8 个 mod，
    Core/Harmony/RimBridgeServer/GameLink/CE/AutoTest 六项 `loadedInSession=true`；`ce/probe` 通过（CE 1.7.1.0、42 个数据工具）。
    游戏侧 `BridgeTools\DSH.CEBridge` 是指向仓库 `bridge\DSH.CEBridge` 的 **junction**（部署已就位，`-Recurse` 不会穿过 junction，别误判为空）。
  - **渲染结论（定性）**：暂停态 `step_game_ticks` 后 **pawn 层不刷新**（UI/新建墙/相机正常、动物同样冻）；
    `play_for` 400 ms（≈74 tick）追平，再 step 又冻。⇒ 截图前先 `play_for`，位置只认坐标回读 + 新旧格裁剪；**此前"截图证明"的结论降级为【未验证】**。
  - **底座 mod 自动补齐（2026-10-24 晚）**：唯一真源 `base-mods.json`（仓库根，6 项：Core/Harmony/RimBridgeServer/GameLink/CE/AutoTest）；
    `start` 前按 profile 补齐（只加不删、幂等、补了才打印一行），`node src/cli.js base-mods [--profile <p>]` 只读对账。
    已验：`node --check` 三文件、临时 profile 首次 +4 / 二次 +0（顺序与缩进正确）、`rhah` 报缺 2 且未写文件。
    **未验**：真机 `start` 的补齐打印路径（`voiced` 已齐，不用补）。
  - **下一会话任务（2026-10-24 交接）**：空窗期，用**全新上下文**把整条测试流程全量试跑一遍（环境自检 → 起实例/进图 → CE 摆读 → 推时间 → 截图取证 → 故意造失败判读 → 停手缺原子 → 按需新建 SOP），产出缺陷清单 `docs\2026-10-25-skill试跑缺陷.md`。
    交接文档：`%TEMP%\HANDOFF-skill试跑-20261024.md`（含八项清单、已知坑、建议 skills）。当前无实例在跑。
  - 旧 skill `rimworld-auto-test` **已删**（用户 2026-10-24 决定；删前 HEAD `8ee9bae`，需要时可从 git 历史取回）。
  - **模块线（用户已定）**：代码按**四层原子**切（`condition.js` 判定 / `wait-sources.js` 取样与聚合 / `conditions.json` 登记 / 游戏内工具 mod 侧）。
  - 设计过程与取舍（含试跑发现）：[docs/组合skill-草案-20261024.md](docs/组合skill-草案-20261024.md)、
    [.dsh/skills/rimworld-auto-test-closed-loop/docs/自动化测试-流程与原子能力清单-20261024.md](.dsh/skills/rimworld-auto-test-closed-loop/docs/自动化测试-流程与原子能力清单-20261024.md)、
    [docs/组合skill-试跑-20261024.md](docs/组合skill-试跑-20261024.md)。
  - 用户已定：`args` 变量化＝做（已落地 `--arg`/`$占位`）；`verifiedLevel` 保留但**不作准入**，只当"还差哪几步没验"的工作清单。
- **P1 已落地（2026-10-24 晚）**：四类等待收敛到**同一个** `waitFor`，`live` 不再有自己的循环。
  - `--source event` / `process` / `bridge-ready` 全部实现（`src\wait-sources.js` 负责取样与聚合：事件窗口、
    按 kind 计数、进程三态、桥接就绪归属校验），`live --until` 现在只是把谓词**翻译**成条件 JSON（`e7dfa7a`）。
  - 条件登记表 `src\conditions.json` + `wait --scenario/--list/--arg`（`f620d7b`）；求值器独立成 `src\condition.js`（`4250ac8`）。
  - 用法见 [.dsh/skills/rimworld-auto-test-closed-loop/docs/cli-wait-用法-20261024.md](.dsh/skills/rimworld-auto-test-closed-loop/docs/cli-wait-用法-20261024.md)（四类来源表）；设计稿 §2.4/§6.1、
    候选数据 [docs/条件登记表-候选数据-20261024.md](docs/条件登记表-候选数据-20261024.md)。
  - **实测**：`live` 的 `--last` / `=` / `~` / 超时打印尾部 / `--new-only` 五项；`wait` 的 process（alive 与不存在）、
    event（NDJSON 计数、日志行 `contains`）、bridge-ready（无游戏时 exit 2）四项。
  - **未验证**：真实游戏事件流（本轮禁止起停游戏）；`docs/cli-wait-实测-20261024.md` 里 9 个用例未重跑；
    `waitForBridge` 仍留在 `launcher.js`（Q2 未定，没搬）。
  - **仍待用户拍板**：Q2 `waitForBridge` 是否搬出 `launcher.js`；Q4 `--scenario` 覆盖规则（当前实现＝禁改身份/允许调参）；
    Q6/Q7/Q8 已按推荐做成**可回退的增量**（`$占位` 参数、`verifiedLevel` 四档、中文场景键）。
- **多实例硬规矩（仍然有效）**：发命令前先证明「我连的是自己那一局」——现在可以直接看 `status` 的 `bridgeResolution`，但**不要**用 `logs` 判断自己死活（共用日志会看到别人的行）。`--force`/`--all` 仍会杀用户的局，只有用户明确要求才用。
- **交接**：[docs/HANDOFF-闭环等待-交接-20261024.md](docs/HANDOFF-闭环等待-交接-20261024.md)。

| 线 | 是什么 | 状态文件 | 当前一句话 |
| --- | --- | --- | --- |
| 自动化测试 | 顶层线：用户用自然语言描述测试流程 → 翻译成游戏操作；测试用的机器（启动器/桥接/调试殖民地/UI 驱动/断言）都属于它 | [自动化测试/STATUS.md](自动化测试/STATUS.md) | 待用户确认（原文件没有单独写这条线的进度，只留下"桥接与测试基础设施"一节） |
| pilot | 自动化测试的特化版：加一个上层自主决策，目标把游戏玩好 | [自动化测试/pilot/STATUS.md](自动化测试/pilot/STATUS.md) | **未放弃，仍在进行中。** 原文只记到 2026-10-04：框架就绪但未部署、Core 357/357、S5 和平模式 10/11（带袭击 9/14）⇒ 之后的进展待用户确认 |
| 共享单车 | 一个 mod（地位与今后每个 mod 相同） | [共享单车/STATUS.md](共享单车/STATUS.md) | **用户已决定暂停这条线。** 设计已定稿，处「Step 0.5 地基探针」前置阶段；运行时实测 0 行 |
| 快照捕捉 | 只读地录制殖民地状态 | [快照捕捉/STATUS.md](快照捕捉/STATUS.md) | 录制器源码已改（runId/run 目录）但**未部署**；三流里 `events-*.jsonl` 至今零真数据 |
| 副官语音 v2 | 一个 mod：把屏幕提示用语音播报出来（新线，2026-10-17 开；**已独立成 `语音副官/` 目录**） | [语音副官/STATUS.md](语音副官/STATUS.md) | **第 1–8 步写完、已三轮真游戏验收、三条 P0 已修并验收（`cc60bb4`）**。下一步：**等用户说「开始优化体验」**（句子排布 / 复读抑制 / 语速停顿） |

## 脚本状态（2026-10-17）

**用户决定：所有脚本全部删除，下次需要时从头重写。**

- `scripts\` 已**清空**（86 个文件；连同更早删掉的 23 个一次性脚本）
- `data\` 已**清空**（157 文件 / 10.9 MB），其结论已整理进 [docs/mod列表整理-结论.md](docs/mod列表整理-结论.md)
- **备份**（在 `tmp\`，随时可删）：`backup-scripts-final-20261017.zip`（86 个）、`backup-scripts-20261017.zip`（109 个，含更早删的）、`backup-data-20261017.zip`（157 个）
- **`src\` 不受影响**：它对 `scripts\` 只有 4 处注释提及，没有 import

**规则**：写任何探测脚本**之前必须先问用户**（见 [AGENTS.md](AGENTS.md)）。

## 线之间的关系（用户 2026-10-17 明确）

- **mod 开发的初期与自动化测试无关**——各做各的
- **到需要测试的阶段**，才把自动化测试拉起来；**缺什么就按标准重新写**
- **pilot 从未放弃**（它是自动化测试的特化版，仍在进行中）
- **共享单车已暂停**

## 原文件
952 行的原 `STATUS.md` 已按线拆到上面四个文件。**内容未删**。
（原文件里"两条独立的线"那段是 2026-10-03 的历史声明，已随内容进入各自文件。）
⚠ 快照捕捉这条线用户本次没提，暂放顶层 `快照捕捉/`，归属待用户定。

## 原文件头部（L1–6，原文照搬）
```text
# 状态

> 这是给下一个会话看的接力文件。每完成一个阶段就更新它。
> 规则见 [docs/协作规范.md](docs/协作规范.md)。

---
```
