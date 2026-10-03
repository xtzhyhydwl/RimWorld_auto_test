# 状态

> 这是给下一个会话看的接力文件。每完成一个阶段就更新它。
> 规则见 [docs/协作规范.md](docs/协作规范.md)。

## 当前在做什么

**路线已调整为「样板驱动」**（用户 2026-10-03 决定）：不靠程序化生成户型，
而是**把用户手造的样板房编码下来，需要时直接抄**。

| 模块 | 状态 | 证据 |
| --- | --- | --- |
| S1 开局允许所有物资 | ✅ 14/14 | `runs/s1-unforbid/report.md` |
| S2 域框架 `INeedDomain` | ✅ 含去重 bug 修复 8/8 | `runs/s2b-dedup/report.md` |
| S3 储藏区（**两片区 + filter 互补集**） | ✅ **实测通过** | `foodprobe/stockpile_filters` |
| S4 住处：**样板 → 蓝图** | ✅ **端到端 12/12** | `runs/template-build/report.md` |
| S4 住处：程序化兜底 | ✅ 编译通过（样板优先，它兜底） | — |
| 样板捕捉工具 | ✅ 10/10 | `runs/template-capture-selftest/report.md` |
| **开局清单（三步全通）** | ✅ **14/14** | `runs/bootstrap/report.md` |
| S5 5 天生存验收 | ⬜ **未跑** | `docs/s5-5天生存验收设计.md` |
| Core 离线测试 | ✅ **188/188** | `node scripts/build-core.mjs` |

**四个域全部注册并跑通**：`food` / `stockpile` / `templatebuild`（样板优先）/ `shelter`（程序化兜底）。

---

## ★ 开局清单：三步全通（用户提的方向，已可执行）

用户原话：
> 「至少**开局一定要做的事情是可以完全被规定出来的**，
>  那只要做到这些事情，基本上**开局就稳了**。」

`node scripts/verify-bootstrap.mjs`（**14/14**）就是这份"规定"的可执行版本：

| 步骤 | 结果 | 实测证据 |
| --- | --- | --- |
| ① 允许所有物资被拾取 | ✅ | 禁取 0 → 0 |
| ② 划两片储藏区 | ✅ | 区#0 400格 生活区 `wood=true corpse=false`；区#1 100格 丢弃区 `wood=false corpse=true` |
| ③ 从样板盖出住处 | ✅ | 蓝图 38 → 86，**+48 = 44墙+4门**，门 +4 |

设计见 [docs/开局流程-设计.md](docs/开局流程-设计.md)。
三层可靠性：**开局清单 > 样板库 > 域**（能用左边解决的，就不要动右边）。

---

## ★ 样板系统（用户新增的核心方向）

```
用户手造样板房 → capture_grid 扫描 → 编码成可读 JSON → mod 直接抄
```

设计见 [docs/蓝图模板系统-设计.md](docs/蓝图模板系统-设计.md)。

- `templates/*.json`：**行字符串**表示形状（人能一眼看懂、能手画、diff 可读）
- `roles[sym].accepts`：「**不必百分百还原，只要物块对得上**」的落点
- **`accepts` 是建筑 Def，材质由库存另挑**（这两个概念混过一次，见下）
- 已有样板：`templates/selftest-box13.json`（13×13 四门，自测产物）

### 端到端实测（12/12）

```
基线   蓝图 18（墙17 / 门1）
建造   样板 selftest-box13 @(107,122) 基地距48：计划 48 格 → 放下 48 格（未解决 0）
回读   蓝图 66（墙61 / 门5）   增量 +48 = 44墙+4门   ← 完全吻合
对账   mod 说 48，桥接独立实测 +48
门     门 +4                                    ← 房子有门，小人进得去
```

---

## ★ 修掉的三个根因（每个都配了**能抓住它的回归测试**）

1. **坐标系混用** —— `TemplatePlacer.Plan` 把**模板局部坐标**当**地图绝对坐标**
   去查一个地图尺寸的网格（等于查了地图左上角）→ 48 个槽位全判"放不下"。
   回归测试：还原修复后**立刻失败**（`期望 18，实际 0`）。
2. **建筑 Def 与建造材料混淆** —— 拿 `"Wall"` 去查库存（Wall 是木头/钢建的，
   不是物品）→ 永远查到 0 → 全判"没货"。引入 `NeedsStuff`：
   墙/门需要材料（不查库存），床/灯是成品（查库存）。
   ★「门 +4」就是这条修好的直接证据。
3. **`MaxCellsPerCluster` 只当否决条件，从没被真正约束** —— K-means 把 25220 个候选
   分 3 簇（每簇约 8400 格），全部 > 400 被否 → **一个区都建不出来**。
   改成"先截断到上限、再算 Area/Score"。
   回归测试：禁用截断后失败（`最大簇 820，上限 50`）。
   （历史记录里"建出 7001 格和 8055 格两个区"是同一个根因。）

---

## ★ 一条贯穿全项目的教训：**摘要必须带原因**

`MaxCellsPerCluster` 那个 bug 之所以难查，是因为摘要只说"没有达标的" ——
而 `Score` 在 Area **过大**和**过小**时都返回 `-Infinity`，两种原因长得一模一样。
改成一并列**每个簇的实况**（格数/基地距/填充率/分数）+ 上限下限之后，一眼就定位了。

**同类问题在本项目已出现三次**（去重 bug、坐标系 bug、这个），所以它是**纪律**不是技巧：
> 任何"没做成"的分支，都必须能说出**为什么**，而不只是"没做成"。

---

## 用户已澄清 / 已定的参数

### 澄清
1. **地板**：默认**完全不铺**。扫描器也不会扫它（地板是 `TerrainDef`，
   不是"格上的东西"）。将来按"资源富余 + 心情需要"在卧室铺 —— 那属于心情域。
   见 [docs/s4-地板与工具优先级-用户澄清.md](docs/s4-地板与工具优先级-用户澄清.md)
2. **工具优先级**：先要一个**能用的**（方案 A，已有）；
   做成持久的局内动作（方案 B）以后再说，甚至可以另做一个 mod。

### 参数
| 参数 | 值 |
| --- | --- |
| 目标带（食物） | 常备 **10** 天，低于 **7** 补，高于 **20** 停 |
| 狩猎 | 不进 MVP |
| 作物 | 固定水稻（`growDays=3`、`harvestYield=6`、`RawRice` 营养 0.05） |
| 住房尺寸 | **13×13**，四边中心各一扇门（共 4） |
| 地板 | **默认完全不铺** |
| 材质 | **有什么用什么，可混用** |
| 储藏区 | **两片**：生活区（近基地，禁尸体垃圾）+ 边缘丢弃区（10×10 方框） |

---

## 待办（按优先级）

- [ ] **S5 五天生存在验收**（当前唯一未完成的阶段目标）
- [ ] 实现 `BootstrapStep` 开局清单（把三步从"各域各自动手"变成**显式有序清单**）
- [ ] **收用户的样板房**（卧室/仓库/厨房），存进 `templates/`
- [ ] 样板旋转/镜像接入放置路径（`Rotated`/`Mirrored` 已有 + 测试，未接）
- [ ] 家具槽位实测（当前样板只有墙/门，床/灯/桌椅还没验过）

---

## 已知问题 / 坑（详见 docs/）

| 坑 | 说明 |
| --- | --- |
| **`create_allowed_area` 会挂死桥接** | 不要调用 |
| **`spawn_thing` 生成 pawn 会崩** | 用 `execute_debug_action` |
| **`play_for` 会静默跑 0 tick** | 有 `forcePause` 窗口时（实测 `MainTabWindow_Menu`）。先查 `get_ui_state` |
| **`step_game_ticks` 的 `timeoutMs` 默认 10000** | 必须显式传大值 |
| **从一次性命令启动游戏** | 进程孤儿化 + 100% CPU 死循环。必须长驻后台任务托管 |
| **`start_debug_game_ready` 几乎总会先超时** | 但殖民地其实会建成功 → 要容忍超时 + 自己轮询 |
| **readiness 必须查 `programState=='Playing'` 且 `ticksGame>0`** | 只看 `mapCount>0` 不够（曾遇到 `MapInitializing`） |
| **PowerShell `Get-Content` 读中文会乱码** | 文件本身是好的；用 `read` 工具 |
| **PowerShell 里 `git commit -m "多行中文"` 会被引号拆坏** | 写进 `tmp/commit-msg.txt` 用 `git commit -F` |
| **`SpecialThingFilterDefOf.AllowCorpses` 不存在** | 只有 `AllowCorpsesUnnatural`/`AllowLargeCorpses`；`ThingCategoryDefOf` 一个字段都没有 → 按 defName 查 |
| **`yield return` 不能在带 catch 的 try 里** | CS1626 → 先收集再返回 |
| **测试断言不能只断言"有没有发生"** | 曾因此漏掉"一天跑 900 次决策"。要断言**数量/趋势** |
| **测"某功能"时被它自己的守卫拦住** | debug 殖民地自带墙/蓝图 → "已有住处"守卫会（正确地）拒绝盖房。测建造用 force，测守卫用非 force |
| **`ZoneMaker.MakeZoneWithCells` 不注册** | 必须补 `RegisterZone`，否则是"僵尸区" |
| **`GetStatValue` 是扩展方法，接收者必须是 `Thing` 实例** | 拿 `ThingDef` 调报 CS1929 |
| **`DWM` 隐形边框约 7px** | 判断窗口是否在屏内要留容差 |

---

## 桥接与测试基础设施（前序会话成果）

- [x] GABP v1 客户端（零依赖 `src/gabp.js`）；隔离启动器（`-savedatafolder` + 独立 Prefs）
- [x] 无管道子进程执行（沙箱下必须，`src/proc.js`）
- [x] 冒烟测试闭环（`node src/cli.js smoke`，9/9）
- [x] 多显示器：`prefs.json` 的 `monitor`（1=主屏，2=副屏）
- [x] `dsh-rimworld` Cordis 插件（7 个工具，**需重启 DSH 生效**）
- [x] 两个 skill：`rimworld-auto-test`、`rimworld-mod-compat`（已装全局）
- [x] debug action 树完整爬取（341 顶层 / 约 4 万节点，`runs/debug-tree-latest.json`）
- [x] Character Editor 两条驱动路径（UI + 自写伴生 DLL 反射）
- [x] Hot reload Defs 实测亚秒生效
- [x] 鼠族（NewRatkinPlus + HAR）安装验证；`modsconfig.mjs preset`

---

## 最近一次验证

| 验证 | 结果 |
| --- | --- |
| **开局清单端到端** | ✅ **14/14**（`node scripts/verify-bootstrap.mjs`） |
| **样板 → 蓝图端到端** | ✅ **12/12**（`node scripts/verify-template-build.mjs`） |
| **储藏区两片区 filter** | ✅ 生活区 `corpse=false` / 丢弃区 `corpse=true` |
| **样板捕捉链路** | ✅ 10/10（已知 13×13 四门方盒 → 44 墙 + 4 门） |
| **Core 离线测试** | ✅ **188/188**（毫秒级） |
| S1 / S2 去重 / 地基 | ✅ 14/14 / 8/8 / 14/14 |
| **S5 五天生存在** | ⬜ **未跑** |

- 桥接工具数：**176+**（原生 + 自写 CE 工具 + 自写 FoodProbe 工具）
- 游戏实例：副屏运行中（`node src/cli.js start` 恢复；⚠ 必须长驻后台任务托管）
