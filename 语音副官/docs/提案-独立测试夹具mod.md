# 提案：独立测试夹具 mod（VoiceDeputyTestRig）

> 写于 2026-10-17。**未实现，等用户拍板。**
> 起因：用户提出"自动化测试可以单独成一个 mod，有阻碍就写一个独立 mod，先把内容说清楚"。

## 1. 它解决什么、不解决什么

| 问题 | 现有 harness 怎么做 | 夹具 mod 能做吗 |
|---|---|---|
| 造事件（袭击/倒地/死亡/疾病/缺料） | `execute_debug_action` 爬 4 万节点找叶子，参数靠猜 | **能**：代码里直接造，一步一个场景 |
| 断言 `[VD]` 行 | 外部读 `Player.log` 文本比对 | **不能替代**：仍以 `Player.log` 为准，但可以产出"我造了什么、什么时候"的机读清单，让比对确定化 |
| 驱动 UI / 截图 | 桥接 184 个工具（已实测可用） | 不需要 |
| **游戏起不来**（Steam 覆盖层 0xc0000005，启动即崩） | 无解 | **无解**——崩溃发生在 mod 扫描之前。必须由用户侧关覆盖层，或改用 Steam 正常启动 |

结论：夹具 mod 是**让场景可复现、可机读**，不是绕过环境崩溃。

## 2. 最小实现（约 250–350 行，一个独立 mod 目录）

- 位置：`<root>\语音副官-test-rig\`（独立 mod，`About/About.xml` + `Source/` + `Assemblies/`；packageId `xiayuan2004.VoiceDeputyTestRig`；`loadAfter` 副官语音）。
- 触发：只在 **dev mode + 参数 `--vd-rig`**（或 `-quicktest`）时起跑，正常游玩零副作用。
- 一个 `GameComponent`，`ctor(Game)` 自动收养（与产品 mod 同一套事实）。
- 场景表（`RigScenario[]`，加一步 = 加一行）：

| # | 场景 | 造法 | 预期 `[VD]` 前缀 |
|---|---|---|---|
| S1 | 袭击 | `IncidentDef RaidEnemy` + `IncidentParms`（points × 派系） | `[VD] 1 incident ThreatRaid` |
| S2 | 倒地 | 对殖民者 `TakeDamage` 至 `Downed` | `[VD] 1 自算 PawnDowned` |
| S3 | 死亡 | `Pawn.Kill` 一名殖民者 | `[VD] 1 自算 PawnDied` |
| S4 | 疾病爆发 | `IncidentDef Disease_Flu` | `[VD] 1 incident DiseaseOutbreak` |
| S5 | 缺料蓝图 | `GenConstruct.PlaceBlueprintForBuild` 一件买不起的墙 | `[VD] 1 自算 BuildMissingMaterials` |
| S6 | 商队 | `IncidentDef TraderCaravanArrival` | `[VD] 1 incident TraderArrival` |

- 每步之间 `WaitTicks`（如 60 tick），保证日志可区分。
- 产物：`<savedatafolder>\rig-report.json`：`{scenario, firedAtTick, expectedPrefix, facts}`。外部只做一件事：读 `Player.log`，按这份清单核 `[VD]` 行（有/无/数量）。
- 不做：不读产品 mod 的内存、不改产品代码、不自己下判断（判断留给外部比对，避免"测试自己说自己过"）。

## 3. 什么时候写

- 优先：先修 Steam 覆盖层，用现有 harness 跑通第 4/5 步验收（`execute_debug_action` + `step_game_ticks` 是唯一没实测的两个动作）。
- 若那两个动作不稳、或以后要回归（每次改文案/绑定都跑一遍），再写本夹具——它是长期用的，不是一次性脚本。
