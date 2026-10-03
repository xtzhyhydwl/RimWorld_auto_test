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
| S3 储藏区（**两片区 + filter 互补集**） | 🟡 算法与 filter 已修，两片区待实测 | `foodprobe/stockpile_filters` |
| 样板捕捉工具 | ✅ 10/10 | `runs/template-capture-selftest/report.md` |
| 样板数据模型 + 放置规划 | ✅ Core **185/185** | `node scripts/build-core.mjs` |
| 样板 → 蓝图（游戏内） | 🟡 两个根因已修，**尚未重新验证** | 见下 |
| S5 5 天生存验收 | ⬜ | `docs/s5-5天生存验收设计.md` |

### ★ 样板系统（用户新增的核心方向）

```
用户手造样板房 → capture_grid 扫描 → 编码成可读 JSON → mod 直接抄
```

设计见 [docs/蓝图模板系统-设计.md](docs/蓝图模板系统-设计.md)。

- `templates/*.json`：**行字符串**表示形状（人能一眼看懂、能手画、diff 可读）
- `roles[sym].accepts`：「**不必百分百还原，只要物块对得上**」的落点
  （床可以是别的床、光源可以是别的光源）
- **`accepts` 是建筑 Def，材质由库存另挑**（这两个概念混过一次，见下）
- 已有样板：`templates/selftest-box13.json`（13×13 四门，自测产物）

### ⚠ 仍未跑通：样板 → 蓝图（两个根因都已修，待重新验证）

**根因 1：坐标系混用。** `TemplatePlacer.Plan` 把**模板局部坐标**当**地图绝对坐标**
去查一个地图尺寸的网格（等于查了地图左上角）→ 48 个槽位全判"放不下"。
已加回归测试：**还原修复后测试立刻失败**（`期望 18，实际 0`），证明它真能抓住这个 bug。

**根因 2：把「建筑 Def」与「建造材料」混为一谈。** 拿 `"Wall"` 去查库存
（Wall 不是物品，是由木头/钢建的）→ 永远查到 0 → 全判"没货"。
已引入 `NeedsStuff` 区分：墙/门需要材料（不查库存），床/灯是成品（查库存）。

### 用户澄清的两条（已落盘）

1. **地板**：默认**完全不铺**。扫描器也不会扫它（地板是 `TerrainDef`，
   不是"格上的东西"）。将来按"资源富余 + 心情需要"在卧室铺 —— 那属于心情域。
   见 [docs/s4-地板与工具优先级-用户澄清.md](docs/s4-地板与工具优先级-用户澄清.md)
2. **工具优先级**：先要一个**能用的**（方案 A，已有）；
   做成持久的局内动作（方案 B）以后再说，甚至可以另做一个 mod。

### 用户新提出的方向：开局流程清单

> 「开局要做的事情越来越多……至少**开局一定要做的事情是可以完全被规定出来的**，
>  只要做到这些，基本上开局就稳了。」

设计见 [docs/开局流程-设计.md](docs/开局流程-设计.md)。
三层可靠性：**开局清单 > 样板库 > 域**（能用左边解决的，就不要动右边）。
已实现第 1 项（允许物资）；第 2、3 项（两片储藏区）刚改完待实测。

---

## 已完成（本轮，2026-10-03 后半段）

### 储藏区两片区（用户指出的真缺陷）
- [x] **filter 互为补集**：丢弃区只收尸体/垃圾；生活区显式禁止
      （用黑名单而非白名单 —— 白名单会漏 DLC 新增物资）
- [x] **边缘丢弃区算法** `EdgeDumpPlanner`：直接在地图边缘找 10×10 方框
      （用户："靠着最边边画一个 10 乘 10，直接结束了，就不用管了"）
- [x] **丢弃区不参与 K-means 聚类** —— 那套是为"贴着资源的生活区"设计的，
      两者选址标准几乎相反，塞一起会互相干扰（这正是先前 bug 的根因）
- [x] 探针 `foodprobe/stockpile_filters` 回读 filter，实测语义正确
      （丢弃区：`acceptsCorpse=true`、`acceptsWood=false`）
- [ ] **待实测**：两片区同时划出来

### 样板系统
- [x] `BuildTemplate` 数据模型（行字符串 + 角色 + 旋转/镜像/校验）
- [x] `TemplateJson` 自写极简 JSON（Core 不许有依赖）
- [x] `TemplatePlacer` 角色→Def 解析（库存分摊、候选回退、**墙门优先**）
- [x] `capture_grid` 探针 + `template-capture.mjs` 转换脚本
- [x] `templates/README.md` 完整工作流说明

### 前序：S1 / S2 / S3 / 地基 / Core
- [x] **S1** 开局允许所有物资：`ColonySetup` + 14/14 实测
- [x] **S2** 域框架 `INeedDomain` + `NeedCoordinator`；修掉自己引入的
      "一天跑 900 次决策" bug（**断言数量而非有无**，8/8）
- [x] **S3** 储藏区（已升级为两片区）
- [x] **地基**：建区 + `RegisterZone` + 设作物 + 小人真去种，14/14
- [x] **Core**：预测器/决策/选址/储藏区聚类/样板，**185/185 离线测试**
- [x] **API 符号确证**：`docs/游戏API符号确证.md` + `tools/asmdump/`

---

## 用户已定的参数

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

- [ ] **重新验证「样板 → 蓝图」**（两个根因都已修，但还没跑过）
- [ ] 实测储藏区两片区同时划出来
- [ ] 实现 `BootstrapStep` 开局清单（把"允许物资"和"划两片储藏区"变成有序步骤）
- [ ] **收用户的样板房**（卧室/仓库/厨房），存进 `templates/`
- [ ] S5 五天生存在验收

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
| **`SpecialThingFilterDefOf.AllowCorpses` 不存在** | 只有 `AllowCorpsesUnnatural`/`AllowLargeCorpses`；`ThingCategoryDefOf` 一个字段都没有 → 按 defName 查 |
| **`yield return` 不能在带 catch 的 try 里** | CS1626 → 先收集再返回 |
| **测试断言不能只断言"有没有发生"** | 曾因此漏掉"一天跑 900 次决策"。要断言**数量/趋势** |
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

- **Core 离线测试**：**185/185 通过**（`node scripts/build-core.mjs`，毫秒级）
- **样板捕捉链路**：10/10（造已知 13×13 四门方盒 → 扫出 44 墙 + 4 门，门位精确在中点）
- **储藏区 filter**：实测丢弃区 `acceptsCorpse=true` / `acceptsWood=false` ✅
- **地基**：14/14；**S1**：14/14；**S2 去重**：8/8
- 桥接工具数：**176+**（原生 + 自写 CE 工具 + 自写 FoodProbe 工具）
- 游戏实例：副屏运行中（`node src/cli.js start` 恢复；⚠ 必须长驻后台任务托管）
