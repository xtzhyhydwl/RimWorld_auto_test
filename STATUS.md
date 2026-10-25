# 状态（索引）

> 多线仓库，各线详情在各自的 `STATUS.md`（见文末表）。本文只留**现在**＋**极简历史**。
> 规则：**越久远越短**。删掉的不心疼——`git log -p -- STATUS.md`、`docs/`、`runs/`、skill 里都有。

## 现在（2026-10-25 18:2x 核）

**机器 / 环境**
- 干净：无 `RimWorld` 进程、僵尸 PID 23600 已自灭、`*.dll.locked` 已删；工坊 `1.7\Assemblies` 只剩 `HungerAndHavoc.dll`(753,152 B) + `.Api.dll`。
- **工坊基线已换**（10-25 16:46 作者发版，Steam 停止游戏后自动覆盖）：`HungerAndHavoc.dll`=`88DDBDC3…`、Defs **24** 文件。整树快照 = `runs\2026-10-25-0002-v8\workshop-upstream-88DDBDC3\`。**不强推回旧版 `15C09CCC`**（会被再覆盖），旧备份 `runs\2026-10-25-0002-v5\workshop-backup\`。新版无 `SiegeFury` / `RHAH_SiegeFury` / `ArmedDemandEndurance` ⇒ 与 0002 不冲突。
- **推时间速度：不再优化**（用户自己做）。留档：无 boost 时 `Ultrafast`≈240 tick/s、`Fast`≈177（仅 1.35×）；8800–12000 tick/s 是 boost(`forceRequestedSpeed`) 给的，已默认关。
- 1.6 可用：`D:\RimWorld-1.6`（1.6.4871）真机跑过，桥接 `127.0.0.1:5174`；但 CLI 的 `start/call` **仍只包 1.7**。[实测记录](docs/RimWorld-1.6并行安装-实测记录-20261025.md)

**在飞**
- **票 0002（围城来客越饿越凶）＝ 收尾中**：[HANDOFF](docs/HANDOFF-0002-收尾-20261025.md)。PR **#9 已合并**（`origin/main`=`2799c57`）⇒ rebase → 解 7 文件冲突（留我们的、接受上游删的）→ 1.7/1.6 双版本 0/0 → **只跑 3 站**最小复验 → PR 草稿写好但**不 push 不开 PR**（用户要求攒着）。
  - 分支已压成 1 commit（`16 files +402`，1.7 DLL `29F1A490…`）；现行档界 mal **0.25 / 0.50**（旧 0.40/0.75）、sev 0.10/0.50/0.90、buff 期 mal **不封顶**（0.79 钳制已删）。
  - V8 三结论（[REPORT](runs\2026-10-25-0002-v8\REPORT.md)）：阈值**生效**、0.79 钳制**已删**、55 个 (mal,sev) 采样对**无硬回归**（2 对滞后一格，次小时自愈）。
  - ⚠ **未验收**：stat 修正（命中/伤害/移速/耐痛/意识）——没有读 pawn stat 的原子，用户自验。
- **票 0008（受困来客走门/凿墙）＝ 已完成**：PR #9 合并；1.7 真机两场景绿（有门 → 穿门格 1200–1800 tick 离图、门 104 / 23 面墙 4485 不变；全墙 → `Mine` 凿墙 2160–2460 tick；均 0 Exception），1.6 也真机绿。遗留一行：敌情场景缺原子、多来客抢门/抱孩子、1.6「无门凿墙」未跑。
- **票 0001（E12 请求信关不掉）＝ 不是源码缺陷，是 1.6 发布包落后**。处置＝给作者提 issue，**不写代码、不开分支**（「选敌对即结算」会反 `incidents.md` 契约）。[结论](docs/RHAH-0001-结论-20261025.md)。未验（用户决定先不做）：`settled=Ignore` 后 `openChoices` 未清理、到期会否复活信件。

**10-25 新增能力**：`cli advance --to-tick`（速度/分段/落点/暂停全内部决定，实测 45000 tick / 49.8 s）、`parse-save`（存档 hediff 取证，**defName 必须传参**，写死改名后会静默 `carriers=0`）、`text-check`（严格 UTF-8、无 U+FFFD、无 BOM）。口径在 skill `rimworld-auto-test-closed-loop` 的 `docs\测试SOP\`。

**跨轮有效的大坑**
- **截图不能当位置/行为证据**：pawn 渲染位置不随实际位置更新（暂停态 step 后不刷新、动物同样）；位置只认格子/坐标回读。
- `play_for` 的**请求档 ≠ 实际档**（`Superfast` 会被引擎拉回 1×）；要落点用 `advance --to-tick`。
- 多实例共用 `Player.log`：只信 `status --profile` 的 `bridgeResolution` + `runs\live\<profile>.ndjson`；`--force/--all` 会杀用户的局。
- Defs 改动**整树同步**到工坊目录；只换 dll 不换 Defs = 静默用旧数据。
- 中文落盘只用 `write`/`edit` 或 `cli … --out`（PS 5.1 的 `Set-Content`/`Out-File` 落 GBK/UTF-16LE/BOM）。

## 历史（越老越短；细节在 git / docs / runs）

- **10-25 早**：回归八项 7 通过（第 4 项部分通过），24 条缺陷改 22 条并复验；V5/V6/V7 三轮 0002 复验（第 3 档能到、0.79 被瞬时突破）⇒ 判「饿倒」改看「是否因营养不良 Down/Death」。→ `docs/2026-10-25-回归缺陷.md`、`runs\2026-10-25-0002-v7\REPORT.md`
- **10-24**：闭环等待落地（`wait` 四类来源 + `conditions.json`）、多实例并发安全（`resolveBridge` 四道校验 / 启动锁 / 假记录拦截）、标准六阶段 SOP、45 条试跑缺陷、`mods add|remove|apply|list` 入口。→ `docs/cli-wait-实测-20261024.md`、`docs/多实例并发安全-修复记录-20261024.md`
- **≤10-17**：`scripts\` / `data\` 全删（规则：写探测脚本前先问用户）；原 952 行 `STATUS.md` 按线拆成下表；mod 开发在「需要测试」前与测试无关；pilot 未放弃、共享单车暂停。
- 更早的原文只在 git：`git log -p -- STATUS.md`。

## 线索引

| 线 | 是什么 | 状态 | 一句话 |
| --- | --- | --- | --- |
| 自动化测试 | 顶层：自然语言需求 → 可执行测试 | [自动化测试/STATUS.md](自动化测试/STATUS.md) | 机器（启动器/桥接/调试殖民地/UI 驱动/断言）已齐 |
| pilot | 加一层自主决策，把游戏玩好 | [自动化测试/pilot/STATUS.md](自动化测试/pilot/STATUS.md) | 未放弃；进展只记到 10-04，待确认 |
| 共享单车 | 一个 mod | [共享单车/STATUS.md](共享单车/STATUS.md) | 用户已决定暂停；设计定稿，运行时 0 行 |
| 快照捕捉 | 只读录制殖民地状态 | [快照捕捉/STATUS.md](快照捕捉/STATUS.md) | 录制器改完**未部署**；`events-*.jsonl` 至今零真数据 |
| 副官语音 v2 | 把屏幕提示播报出来 | [语音副官/STATUS.md](语音副官/STATUS.md) | 第 1–8 步完、三轮真机验收、三条 P0 已修；等用户说「开始优化体验」 |
