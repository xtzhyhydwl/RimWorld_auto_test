# 共享单车 · 状态

> ⚠️ **本文件里提到的 `scripts\*.mjs` 命令已在 2026-10-17 被用户全部删除。**
> 那些命令是**历史记录**，现在跑不了；需要时按标准重新写。**原文未改。**
> 本文从仓库根 `STATUS.md` 拆出（2026-10-17）。**内容原样搬运，未做取舍。**
> 归属：一个 **mod**（地位与今后每个 mod 相同）；用户已暂时不做，文件放它自己的目录。
> 上级：（无，与各 mod 并列） ｜ 交接文件：（原文未给独立交接文件；文档地图见 [../docs/README-共享单车.md](../docs/README-共享单车.md)）

---

**★ 还有第三条线（2026-10-13 登记）：③ 共享单车 mod** —— 与上面两条**并列且独立**，
既不是快照线的子项、也不是自动化线的子项。它做的是「殖民地内共享单车 / 停放区」这个 mod 本身。
**文档地图：[../docs/README-共享单车.md](../docs/README-共享单车.md)。本节见下方「共享单车 mod 线」。**

## ★★ 共享单车 mod 线（第三条独立线 · 2026-10-13 登记）

> 这条线的文档地图：**[../docs/README-共享单车.md](../docs/README-共享单车.md)**
> 符号权威：[../docs/共享单车-机制真值表.md](../docs/共享单车-机制真值表.md) ｜ 设计权威：[../docs/共享单车mod-设计-v2.md](../docs/共享单车mod-设计-v2.md)
> ⚠ 旧说法（本节之前）称本项目"已分成两条独立的线" —— **现在读作三条**。

### 当前在做什么

设计已定稿，处于**「Step 0.5 · 地基探针」前置阶段**：还没写任何产品代码，下一步是**先做三件小样实测**。

### 已完成

- **文档层**：这条线的文档共 **6 份**，角色 / 优先级 / 读哪份的索引**全部在 [../docs/README-共享单车.md](../docs/README-共享单车.md)**（本文件不复述）。
- **已拍板的技术决策（用户已定）**：`pxPerCell = 128`（已冻结）｜骑行时车进 `carryTracker` 且贴图连续跟随小人｜
  `ExitMap` 用 **Harmony 前缀**干预（最终 **3 个补丁**：`TicksPerMove` + `ExitMap` + `Kill`）｜停放区用**自定义 Zone**｜**不采用 VF / VVE**。
- **工具层**：贴图流水线脚本｜`tools/asmref/`｜`src/toolname.js`｜机制真值表（append-only 台账）｜**干净测试实例 `profiles/bike`**。

### 待办

- [ ] **Step 0.5 地基探针**（三件必须先实测的事：`MinifiedBike.PostMake` 装内层后能否正常工作 / 骑行 Job 的上下车开销 `O` /
      整理工能否一次背 5 辆车）。**定义与 Gate 见 [../docs/README-共享单车.md](../docs/README-共享单车.md) §③**（已登记为**独立步骤**，不属 Step 0）。
- [ ] 剩余未决项 **M1–M9**：见 [../docs/共享单车mod-设计-v2.md](../docs/共享单车mod-设计-v2.md) **§10**（M6 / M10 / M11 / M12 已拍板并移出未决）。

### 已知问题 / 坑

- **隔离实例 `profiles/bike` 曾自行退出**（跑约 **48 分钟**后）—— 跑长任务时需**死亡检测**（另见本文件末尾"游戏实例"一行）。

### 最近一次验证

**到目前为止，共享单车这条线 0 行运行时实测。** 全部结论都是 **IL / 元数据 / 原版 XML** 层面的
（真值表里没有一行是【运行时实测】，见 [../docs/共享单车-机制真值表.md](../docs/共享单车-机制真值表.md) 文首 + 设计-v2 §9.4）。
最近做过的**真实**验证是基础设施层的：
① `.NET 10 SDK` / 引用面探针 `tmp/probe17/run-probe.ps1` → **0 FAIL（退出码 0）**（真值表 §7 工具登记表 T4，已修 / 在用）；
② 干净实例 `profiles/bike` 能起来并载入殖民地（3 名殖民者 / `Map_0`，桥接端口 5174，见 [../docs/共享单车mod-开发流程与设计.md](../docs/共享单车mod-开发流程与设计.md) 附）。
⚠ **符号权威里"确证"= IL 确证，不等于"在游戏里跑过"**。

### ★ 探针销毁清单（发布前必做）

> **探针 = `mods/BikeProbe/`（`packageId = dsh.bikeprobe`），它是"用完即弃"的测试资产，不是产品代码。**
> **现状：暂不销毁。** 原因：它**是目前唯一能在真实游戏里跑通「工作台 → bill → 造物」整条链的资产**（见 `tmp\bike-probe\run-4\RUN4-REPORT.md`），后续验证还要用它。
> **但发布前必须逐条清掉**（清单来源：真值表 §6.5「探针状态」＋ 设计-v2 §2.4 ＋ `docs\共享单车-Step0.5-地基探针方案.md` §⑥）：

- [ ] **① 源码 + Defs + dll**：删 `$WS\mods\BikeProbe\`（整目录：`Source\1.6\*.cs`、`Defs\**`、`1.6\Assemblies\BikeProbe.dll`、`About\`、`LoadFolders.xml`）。**证据不删**（`tmp\bike-probe\` 保留）。
- [ ] **② 游戏目录的 junction**：删 `D:\SteamLibrary\steamapps\common\RimWorld\Mods\BikeProbe` —— ⚠ **删 junction，不要删目标**（用 `cmd /c rmdir "<游戏>\Mods\BikeProbe"`；**别用会穿透的删除**，否则会连带删掉工作区里的 `mods\BikeProbe`）。
- [ ] **③ ModsConfig 里的条目**：`node scripts/modsconfig.mjs remove dsh.bikeprobe --profile bike`（改的是 `profiles\bike\Config\ModsConfig.xml` 的 `activeMods`；**本项目从不自动写 ModsConfig**，所以这一步是显式的）。
- [ ] **④ 探针自己落的结构化日志**：`profiles\bike\BikeProbe\bikeprobe.jsonl`（**实测 174,896 B ≈ 171 KB**；早期登记过的"约 149 KB"是旧值，**以实测为准**）。这是运行时产物，不属于仓库资产。
- [ ] **⑤ 两条硬性要求（不只是删文件）**：**探针代码不得被产品 `Source/` import**（探针类名一律进"禁止引用清单"）；**探针的存在不得出现在发布产物里**（`.dll` / `Defs` / `About.xml` / `ModsConfig` / 截图与日志，一处都不许）。
- [ ] **⑥ 销毁后核对**：`node src/cli.js status --profile bike` 里不再出现探针 mod；`profiles\bike\Config\ModsConfig.xml` grep `dsh.bikeprobe` = **0 命中**；游戏 `Mods\` 下**没有**遗留 junction。
