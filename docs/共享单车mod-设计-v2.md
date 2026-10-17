# 共享单车 mod —— 最终设计文档 v2（施工图）

> **这份文档的定位**：给"准备开始写代码的人"用的施工图。它**不重新取证**，只把前几轮散落的结论收敛成可据以写代码的形态。
>
> **符号来源唯一**：所有类名 / 方法名 / 字段名 / Def 名都逐字符取自
> [共享单车-机制真值表.md](共享单车-机制真值表.md)。真值表里没有的，本文一律写"**未确证**"并列入 §9 待办，**不编造**。
>
> **引用纪律（2026-10-13）**：本文**一律用节号（`§1.3` 这种）引用真值表与其它文档，不用行号**。真值表是 **append-only** 台账且正被其它工作持续追加，**行号必然失效**——行号引用已实际失效 4 次。
> **符号真值 = 真值表；设计取值 = 本文**（`Mass=7`、`v_b=6.0`、~~阈值 `D>22`~~ → **阈值 = `D > 10 + 6·O`（`O = 0 → D > 10`；见 §7.2 实测更正③，2026-10-13 实测更正）**、`naturalPriority=320` 一类）。
> **更正历史已全部迁出**：本文只留当前有效结论；反例（原错误写法）与实测更正记录见真值表 **§8 设计文档更正历史（append-only）**。
>
> **本文取代 / 补充**：[共享单车mod-开发流程与设计.md](共享单车mod-开发流程与设计.md)（其 §1 环境、§2 流程、§3 贴图线、§7 发布与 AI 美术部分**原则有效、本文不重复**；其 Def 规格与架构部分以本文为准）。
> ⚠ **但该文档 §2 的 Step 4 已作废**：那里的 `"小人 ↔ 车 双向引用"` 与 `"双向引用一律 Scribe_References.Look(...)"` **是旧方案**；**本文档已推翻为「单向 + `PostLoadInit` 自愈」**（见 §3.4 / §3.6），**以本文为准**。
>
> **标注纪律**：每个技术断言后面跟 `【IL确证】` / `【原版XML确证】` / `【推断】`；标注"**未确证**"= 真值表里查不到该条。
> ⚠ ~~**本工程到目前为止 0 行运行时实测**——所有证据都是只读的（读 IL / 元数据 / 原版 XML）。**没有一条【运行时实测】。**~~
> **→ 已被探针运行时实测推翻，现为「只对产品代码成立」（2026-10-13 实测更正）**：一次性探针 `mods/BikeProbe` 已在 `profiles/bike` 隔离实例里跑过三轮，**真值表 §2.9 已有【运行时实测】条目**；**产品代码 `Source/` 仍是 0 行**，本文**除 §2.3 / §7.2 的实测更正块外**仍无【运行时实测】。依据：真值表 §2.9 + `tmp\bike-probe\FINAL-12-ITEMS.md` §③。
> 真值表已知但与本项目无关的坑（`--usages` 假阴性、`Map` 上没有 `Notify_*`、`RecalcuateCellDirect` 拼写等）见真值表 §3 / §4，本文只引用需要的那几条。

---

## 0. 设计简报（用户原话要点，逐条落实位置）

| # | 用户要求（原话要点） | 落实位置 | 一句话方案 |
|---|---|---|---|
| 1 | 车辆在**锻造台用钢铁**造出来，造出来时是**物品形态**（没人骑） | §1 / §2 / §7.1 | `RecipeDef` 产物 = 自定义 `RimWorld.MinifiedThing` 子类【IL确证】 |
| 2 | **部署**到地面后变成**可骑的另一种形态** | §1 / §5.3 | 原版安装链：`Designator_Install` → `JobDefOf.HaulToContainer` → `Blueprint_Install.MakeSolidThing`【IL确证】 |
| 3 | 前期可造、成本低 | §7.1 | 钢铁 40 + 工作量 ≈60，无研究前置 |
| 4 | 品质区分（未定） | §10-M1 | 第一版**不做**；`ThingDefCountClass.quality` 存在但"自定义子类 + 品质"未确证 |
| 5 | **新增一个工作类型**，专职整理乱停车辆 | §6.1 / §6.2 | 纯 XML `WorkTypeDef` + `RimWorld.WorkGiver_Scanner` 子类【IL确证】 |
| 6 | 现实用三轮车拉一堆，但"**那肯定不用**" | §6.4 | 用**小人背包** `Verse.Pawn_InventoryTracker.innerContainer`（`Verse.ThingOwner\`1<Verse.Thing>`，混装不同 def）替代【IL确证】 |
| 7 | 希望**像划区域一样**划共享单车储存区域 | **§5.3（已决定）** / §5.2 | **自定义 `Verse.Zone` 子类 `Zone_SharedBikePark`** + `Designator_ZoneAdd` 子类 + 一条 `PatchOperationAdd`（**`Verse.ZoneDef` 不存在**）【IL确证】 |
| 8 | 里面车"以**比较特殊的方式**停在那儿" | **§5.3** / §10-M4 | 暂定：车在停放区内时**不参与整理工的乱停扫描**；视觉排列方式未定。⚠ **`Zone.get_Cells` 会 `GenList.Shuffle(cells)`**，顺序语义一律不能用 |
| 9 | 骑行：①乱停乱放 ②固定停在停放区 | **§5.1（已决定）** / §5.2 | **第一版只做 ①**；② 是"① + 玩家划了 Zone"的**自然结果**，不是第二套代码 |
| 10 | 征召骑车（跑得快） | §4 | 小人 `ThingComp.CompGetGizmosExtra` 给按钮 + `Pawn.TicksPerMove` Postfix【IL确证】 |
| 11 | 征召时"不符合只能从 A 到 B 的逻辑" | §4.2 / §10-M3 | 第一版：征召骑行 = **只能骑到目的地**，下了车恢复征召；自由驾驶不做 |
| 14 | **不采用 VF / VVE** | §9.3 | 已评估排除（VF 官方 FAQ：AI 不会用载具跑图；且硬依赖含 CC BY-NC-ND 的 VFE） |

---

## 1. 架构总览

### 1.1 全链路一图

```
                        【原版机制】                        【自己写】
  ┌───────────────────────────────────────────┐
  │ ① 锻造台制作（RecipeDef）                   │
  │    products → SharedBikeMinified             │  ← 配方本身是原版
  └──────────────────┬────────────────────────┘     【原版XML确证：原版没有任何
                     │                               RecipeDef 产物是 MinifiedThing】
                     ▼
  ┌───────────────────────────────────────────┐
  │ ② 物品形态 = MinifiedThing 子类（未部署）    │  ← MinifiedBike.PostMake()
  │    InnerThing = 一个未生成的 Bike 建筑       │     装内层【IL确证 + 原版
  │    （可从锻造台搬到任何地方）                 │     MinifiedTree 先例】
  └──────────────────┬────────────────────────┘
                     │ 玩家选中物品 → 原版 "Install" gizmo
                     ▼
  ┌───────────────────────────────────────────┐
  │ ③ 部署（安装蓝图）                          │  ← 全原版
  │    MinifiedThing.GetGizmos                  │     Designator_Install
  │      → InstallationDesignatorDatabase       │     GenConstruct.CanPlaceBlueprintAt_NewTemp
  │      → GenConstruct.PlaceBlueprintForInstall│     PlaceWorker.AllowsPlacing（可自定义）
  │      → RimWorld.Blueprint_Install           │
  └──────────────────┬────────────────────────┘
                     │ 原版工作分配
                     ▼
  ┌───────────────────────────────────────────┐
  │ ④ 建筑形态（可骑的 Bike）                   │  ← 全原版
  │    JobDefOf.HaulToContainer                 │     JobDriver_HaulToContainer
  │      → Blueprint_Install.MakeSolidThing     │     → GenSpawn.Spawn
  │    def.minifiedDef 指回 ②                    │     ← "能不能打包"的唯一开关
  └──────────────────┬────────────────────────┘
                     │
        ┌────────────┴─────────────┐
        ▼                          ▼
  【自发骑行】                  【征召骑行】
  ┌──────────────────────┐   ┌──────────────────────┐
  │ ⑤a ThinkTreeDef 插入   │   │ ⑤b 玩家点 Gizmo        │
  │ <insertTag>           │   │ ThingComp.             │
  │  Humanlike_PostMain   │   │  CompGetGizmosExtra   │
  │ → JobGiver_…(自己写)   │   │ → jobs.TryTakeOrderedJob│
  │ → JobDriver_…(自己写)  │   │   (job, JobTag.DraftedOrder)│
  └──────────┬───────────┘   └──────────┬───────────┘
             └───────────┬─────────────┘
                         ▼
  ┌───────────────────────────────────────────┐
  │ ⑥ 骑行中（= 车进了 carryTracker）            │  ← 1 个 Patch 同时覆盖 ⑤a/⑤b
  │    ★ 车 = 小人的 carryTracker.CarriedThing   │     【用户已拍板；见 §3.5】
  │    Patch: Pawn.TicksPerMove(bool) Postfix   │     【IL确证：MoveSpeed 的唯一玩法出口】
  │    内部必须自己判断"是否在骑车"              │
  │    ⚠ 贴图**连续跟随**（不是留原地、不是        │     【IL确证：PawnRenderNodeWorker_
  │       DeSpawn 瞬时出现）—— 见 §3.5           │      Carried::PostDraw 每帧自动画】
  └──────────────────┬────────────────────────┘
                     │
                     ▼
  ┌───────────────────────────────────────────┐
  │ ⑦ 下车停放（"乱停乱放"模式）                 │  ← 自己写
  │    GenPlace.TryPlaceThing(Near/Radius)      │     落点合法性 / 最近合法格【IL确证】
  │    或 CellFinder.FindNoWipeSpawnLocNear     │     （见 §5.4 / §5.5-H1）
  │    ★ 解绑 = Harmony **前缀**干预 ExitMap：    │     ★ 必须幂等 + 必须同时决定车去哪
  │      先幂等摘车 → 再 base.ExitMap(...)       │     （否则 IL_0101 会 Destroy 掉车）
  └──────────────────┬────────────────────────┘
                     │ 车落地 = 一条"待整理的活"
                     ▼
  ┌───────────────────────────────────────────┐
  │ ⑧ 整理工作（新 WorkType）                    │  ← 自己写 WorkGiver + JobDriver
  │    WorkGiver_TidySharedBikes                 │     WorkTypeDef 是纯 XML
  │    → 走到车 → 收进背包（可多辆）             │     背包 = Pawn_InventoryTracker
  └──────────────────┬────────────────────────┘
                     │
                     ▼
  ┌───────────────────────────────────────────┐
  │ ⑨ 回到停放区                                │  ← 自己写
  │    Zone_SharedBikePark（自定义 Zone）        │     停放区是"对整理工的指令"，
  │    车放进去后不再被 ⑧ 扫到                    │     不是"对骑车人的约束"（见下方核心主张）
  └───────────────────────────────────────────┘
```


### 1.2 机制来源对照（**用户最关心的成本问题**）

| # | 环节 | 原版机制 | 自己写 | 成本 |
|---|---|---|---|---|
| ① | 锻造台配方 | `Verse.RecipeDef` + `products`【IL确证】 | 配方 XML（零 C#） | **低** |
| ② | 物品形态 | `RimWorld.MinifiedThing` 基类【IL确证】 | 一个子类 + `PostMake()` 约 10 行 | **低** |
| ③ | 部署 | `Designator_Install` / `GenConstruct.PlaceBlueprintForInstall` / `CanPlaceBlueprintAt_NewTemp` / `PlaceWorker.AllowsPlacing` / 安装蓝图 def 自动生成【IL确证】 | 可选一个 `PlaceWorker`（若默认判定不够） | **极低（几乎白送）** |
| ④ | 建筑成形 | `JobDefOf.HaulToContainer` + `Blueprint_Install.MakeSolidThing`【IL确证】 | 零 | **零** |
| ④b | 反向打包 | `JobDefOf.Uninstall` / `JobDriver_Uninstall.FinishedRemoving` / `MinifyUtility.Uninstall`【IL确证】 | 零（`WorkToBuild == 0` 还能瞬时打包） | **零** |
| ⑤a | 自发骑行决策 | 思考树插槽 `<insertTag>Humanlike_PostMain</insertTag>`【原版XML确证】 | JobGiver + 自己的 `ThinkTreeDef` | **中** |
| ⑤b | 征召骑行入口 | `ThingComp.CompGetGizmosExtra` → `ThingWithComps.GetGizmos`【IL确证】 | 一个 `ThingComp` + 一条 Def patch | **低** |
| ⑥a | 加速 | `Verse.Pawn::TicksPerMove(bool)`【IL确证】（`MoveSpeed` 的唯一玩法出口） | 一个 Harmony Postfix（必须自己判断"是否在骑车"，O(1)） | **低** |
| ⑥ | 骑行中（车进 `carryTracker`） | `Verse.Pawn_CarryTracker` + 原版渲染链 `Verse.PawnRenderNodeWorker_Carried::PostDraw` → `Verse.PawnRenderUtility::DrawCarriedThing` → `Verse.Thing::DrawNowAt`（**每帧自动画，贴图连续跟随**）【IL确证】 | 自己的 `JobDriver.ModifyCarriedThingDrawPos` 覆写（调骑行姿态偏移）+ 1 个 Harmony **前缀**（`ExitMap`） | **低** |
| ⑦ | 下车落点 | `Verse.GenPlace.TryPlaceThing`（`ThingPlaceMode.Near`/`Radius`）/ `Verse.CellFinder.FindNoWipeSpawnLocNear` / `Verse.GenDrop.TryDropSpawn`（**均已确证，见 §5.4 / §5.5-H1**） | 复用原版落点搜索 + `Thing.Position` setter（§5.6） | **低**（不再自写螺旋） |
| ⑧ | 整理工作 | `WorkGiver_Scanner` 骨架 + `WorkTypeDef.ResolveReferences()` 自动收集【IL确证】 | WorkGiver + JobDriver + 落点 | **高（本项目最重的一环）** |
| ⑨ | 停放区 | 无（**`ZoneDef` 不存在**）【IL确证】 | `Zone` 子类 + `Designator_ZoneAdd` 子类 + 1 条 XML patch | **中** |

### 1.3 一句话成本结论

> **"部署"那一半几乎是白送的**（③④ 全原版，含 UI ghost 和合法性提示）。
> **真正要写的只有四块**：骑行中（⑥，含渲染与解绑）、加速（⑥a）、停车落点（⑦⑨）、整理工作（⑧）。
> 其中 **⑧ 整理工作最重**，而它恰好是**低频路径**——这正是 §5.1 推荐"乱停乱放"的技术理由。
> **Harmony 补丁数 = 3**（`TicksPerMove` + `ExitMap` + `Kill`，见 §3.5）：都不是"因为改不了才打"，而是**用户已拍板接受**的最小集合。
> **贴图规格不在本文档**：`pxPerCell = 128` / `texPath` 写法 / 方向后缀 / 判级一律以
> [贴图流水线-设计.md](贴图流水线-设计.md) 为权威（本文只引用，不重复；见 §3.5 与 §5.3）。

### 1.4 ★ ★ 核心设计主张：**停放区不是对骑车人的约束，而是对整理工人的指令**（先读这一条，再看剩下的）

> **停放区不是对骑车人的约束，而是对整理工人的指令。**
>
> | 角色 | 面对停放区时的行为 |
> |---|---|
> | **骑车的小人** | **就地停**。只做**一次**合法落点检查（§5.4 的投放路径），**完全不查 Zone、不知道停放区存在** |
> | **整理车的小人** | 把车从"不该停的地方"搬到**玩家划定的 `Zone_SharedBikePark`** 里 |
> | **玩家没划 Zone 时** | 整理工**什么都不做**（第一版；可选的例外"只清理堵住通道的车"见 §5.2） |
>
> **这一条同时解决三个问题**，所以它是本文档最重要的架构决定：
>
> 1. **解释"为什么两种停车模式不冲突"**：骑车人那条路里**没有任何 Zone 查询**，所以"乱停乱放"（①）与"固定停在区里"（②）是**两条互不干扰的路径**——① 永远可用，② 只是"玩家划了区之后，整理工会把车搬进去"的自然结果，**不需要给骑车人加任何判断**（§5.1 / §5.2）。
> 2. **把复杂度放到低频路径**：骑行结束路径（高频）只有"一次合法格检查"；Zone 相关逻辑全在整理工作（低频）里。
> 3. **让"整理工"这个新工作类型有真实活源**：乱停 = 活源；划了 Zone = 目的地；不划 = 没活（玩家的选择，而不是 bug）。
>
> 这条主张是【推断】，不是 IL 结论。

---

## 2. 文件清单与职责

> 目录布局沿用 [共享单车mod-开发流程与设计.md](共享单车mod-开发流程与设计.md) §6 的双 TFM 结构：
> `Defs/ Patches/ Textures/ Languages/` 放根目录（版本无关）；`1.6/Assemblies/`、`Source/1.6/`、`Source/1.7/` 分版本。

### 2.1 Def 文件（XML）

| 文件 | 一句话职责 | 关键内容 / 风险 |
|---|---|---|
| `Defs/ThingDefs/ThingDef_SharedBike.xml` | 定义**建筑形态** `SharedBike` | `<thingClass>Building</thingClass>`、`<minifiedDef>SharedBikeMinified</minifiedDef>`、**`<passability>Standable</passability>`（见 §5.4 / §5.5-H3：`Standable` 或 `PassThroughOnly` 都行，就是**不能** `Impassable`）**、`<building><uninstallWork>…</uninstallWork></building>`、`<placeWorkers>`（可选）。⚠ 同时是**乱停乱放的载体**（§5.2） |
| `Defs/ThingDefs/ThingDef_SharedBikeMinified.xml` | 定义**物品形态** def | `<ParentName>MinifiedThing</ParentName>` + `<thingClass>SharedBikeMod.MinifiedBike</thingClass>`；**必须** `ParentName`（`RimWorld.MinifyUtility::MakeMinified` 里是 `castclass RimWorld.MinifiedThing`，不是 `isinst`）【IL确证】 |
| `Defs/Recipes/Recipe_MakeSharedBike.xml` | 锻造台配方：钢铁 → `SharedBikeMinified` | `Verse.RecipeDef.products` 是 `List<Verse.ThingDefCountClass>`【IL确证】；**原版无先例，见 §2.3** |
| `Defs/WorkTypeDefs/WorkTypes_SharedBike.xml` | 新增工作类型 `SharedBikeTidying` | **纯 XML，零 C# 注册**【IL确证】；`naturalPriority` 必须 **0..10000**（§6.1-K1） |
| `Defs/WorkGiverDefs/WorkGivers_SharedBike.xml` | 把整理工作挂到新 WorkType 上 | `giverClass` / `workType` / `verb` / `gerund` / `priorityInType`；缺 `verb`/`gerund` 直接红字【IL确证】 |
| `Defs/ThinkTreeDefs/ThinkTree_RideSharedBike.xml` | 把自发骑行决策插进人类思考树 | `<insertTag>Humanlike_PostMain</insertTag>` + `<insertPriority>`【原版XML确证，Ludeon 亲手标注 "Insertion hook for modders"】 |
| `Defs/JobDefs/Jobs_SharedBike.xml` | 三个 JobDef | `RideSharedBike`（**`playerInterruptible=false`**，见 §3.1-3 / §4.4）、`TidySharedBike`、`ReturnSharedBike` |
| `Patches/Patch_DesignationCategory.xml` | 把停放区 Designator 塞进 Architect 的 Zone 分类 | `PatchOperationAdd` → `/Defs/DesignationCategoryDef[defName="Zone"]/specialDesignatorClasses` 加 `<li>Designator_ZoneAdd_SharedBikePark</li>`【原版XML确证：`DesignationCategories.xml:31-55`】 |
| `Patches/Patch_Human_Comps.xml` | 给**人类** ThingDef 加我们的小人 Comp | 人类 def 已有 `<comps>` 段【原版XML确证：`Races_Humanlike.xml:216-226`】；**不需要 Harmony** |

### 2.2 C# 类

| 文件 | 一句话职责 | 关键类型真名（全名，逐字符取自真值表） |
|---|---|---|
| `Source/1.6/Bike/CompSharedBike.cs` | **车上的 Comp**：骑手引用、是否被骑、上次还车 tick；`ExposeData` 持久化 | `Verse.ThingComp`；**`Verse.ThingComp::PostExposeData()`（`public virtual`，可覆写）**；`Verse.Scribe_References.Look<Verse.Pawn>(ref rider, "rider")`（**单向**；⚠ 泛型约束 `where T : Verse.ILoadReferenceable`，见 §3.1-P1 / §3.4 / §3.6） |
| `Source/1.6/Bike/MinifiedBike.cs` | **物品形态**：`PostMake()` 里装内层 `SharedBike` | `RimWorld.MinifiedThing : Verse.ThingWithComps`【IL确证】；`Verse.ThingMaker::MakeThing`【IL确证】；覆写 `public override void PostMake()`（**`Verse.Thing::PostMake` 是 `public virtual`**）。<br>⚠ 这里用的是 **`Verse.Thing::PostMake`**（继承自 `Thing`），**不是** `Verse.ThingComp::PostMake`（后者**不存在**；`ThingComp` 上的兄弟是 **`PostPostMake`**） |
| `Source/1.6/Bike/CompSharedBikeRider.cs` | **小人身上的 Comp**：提供征召骑车的 Gizmo | `Verse.ThingComp::CompGetGizmosExtra()`【IL确证】；`Verse.Command_Action`【IL确证】 |
| `Source/1.6/Bike/MapComponent_SharedBikeRegistry.cs` | **全图索引**：空间分桶 + 脏标记；提供"离我最近的一辆闲车"（见 §6.5） | `Verse.MapComponent`（`kind=class abstract base=System.Object impl=IExposable`；`FIELD public Verse.Map map`；`CTOR public MapComponent(Verse.Map map)`）+ **8 个 `public virtual` 无参 void**（全部 `flags=0x01c6`）：`MapComponentUpdate()` / `MapComponentTick()` / `MapComponentOnGUI()` / `MapComponentDraw()` / `ExposeData()` / `FinalizeInit()` / `MapGenerated()` / `MapRemoved()` ⇒ ✅ 全部可覆写，0 Harmony（真值表 §2.8 / §3.7a）<br>⚠ 命名空间是 **`Verse.MapComponent`**（`RimWorld.MapComponent` **不存在**） |
| `Source/1.6/Bike/JobGiver_RideSharedBike.cs` | 自发骑行**决策**：这个人现在该不该骑车 | `Verse.AI.ThinkNode_JobGiver`（`kind=class abstract base=Verse.AI.ThinkNode`）：**`protected abstract Verse.AI.Job TryGiveJob(Verse.Pawn pawn)`**（`flags=0x05c4`）+ **`public virtual Verse.AI.ThinkResult TryIssueJobPackage(Verse.Pawn pawn, Verse.AI.JobIssueParams jobParams)`**（`flags=0x00c6`）→ ✅ 都不需要 Harmony（真值表 §2.8 / §3.7a）<br>⚠ **"怎么挑"的正确位置是 `ThinkNode_JobGiver::TryIssueJobPackage`，不是 `JobDriver.TryIssueJobPackage`（后者不存在）** |
| `Source/1.6/Bike/JobDriver_RideSharedBike.cs` | 骑行**步骤**：走去取车 → 上车（`TryStartCarry`）→ 骑到目的地 → 下车停放 | `Verse.AI.JobDriver`（`kind=class abstract base=System.Object impl=IExposable, IJobEndable`）：**`protected abstract IEnumerable<Verse.AI.Toil> MakeNewToils()`**（`0x05c4`）、**`public abstract bool TryMakePreToilReservations(bool errorOnFailed)`**（`0x05c6`）、`public virtual string GetReport()` / `Notify_PatherArrived()` / `Notify_PatherFailed()`（`0x01c6`）、**`public virtual bool ModifyCarriedThingDrawPos(ref UnityEngine.Vector3 drawPos, ref bool flip)`（骑行姿态偏移的唯一虚挂点，见 §3.5）** → ✅ 可覆写；<br>❌ **非虚**：`DriverTick()` / `DriverTickInterval(int)` / `EndJobWith(JobCondition)` ⇒ 想改 Job 主循环必须 Harmony<br>⚠ `TargetA/B/C` / `TargetThingA/B/C` 等**全是 `protected` 且非虚** ⇒ 子类**能读、不能 override** |
| `Source/1.6/Bike/JobDriver_TidySharedBike.cs` | 整理**步骤**：走到乱停车 → 收进背包 → 走到停放区 → 放下 | `Verse.AI.JobDriver_TakeInventory` / `RimWorld.JobDriver_TakeCountToInventory`【IL确证】可作为抄写模板 |
| `Source/1.6/Bike/WorkGiver_TidySharedBikes.cs` | 整理工作的**活源**：谁去整理哪辆车 | `RimWorld.WorkGiver_Scanner`（**不是 `Verse.AI.*`**）/ `RimWorld.WorkGiver_Haul`【IL确证】可抄 |
| `Source/1.6/Bike/Zone_SharedBikePark.cs` | **停放区**：一片格子 = 停放位 | `Verse.Zone`（**唯一抽象成员 `protected abstract UnityEngine.Color NextZoneColor { get; }`**）【IL确证】 |
| `Source/1.6/Bike/Designator_ZoneAdd_SharedBikePark.cs` | 让玩家能**划**停放区 | `RimWorld.Designator_ZoneAdd`：ctor 里 `zoneTypeToPlace = typeof(Zone_SharedBikePark)`，覆写 `MakeNewZone()` / `NewZoneLabel`【IL确证】 |
| `Source/1.6/Bike/PlaceWorker_SharedBike.cs` | （可选）自定义"车能不能停这格"的附加规则 | `Verse.PlaceWorker`（`abstract`，公开可继承）/ `AllowsPlacing(...)` 返回 `Verse.AcceptanceReport`【IL确证】 |
| `Source/1.6/Bike/Patch_TicksPerMove.cs` | 唯一一个**加速** Harmony 补丁：骑车加速 | Postfix on `Verse.Pawn.TicksPerMove(bool)`【IL确证】 |
| `Source/1.6/Bike/Patch_TicksPerMove.cs` | **Harmony 补丁 1/3**：骑车加速 | Postfix on `Verse.Pawn::TicksPerMove(bool)`【IL确证】；必须自己判断"是否在骑车"，且 O(1)、零分配（§4.5） |
| `Source/1.6/Bike/Patch_ExitMap.cs` | **Harmony 补丁 2/3**：`ExitMap` **前缀**干预（离图解绑） | Prefix on `Verse.Pawn::ExitMap(bool allowedToJoinOrCreateCaravan, Verse.Rot4 exitDir)`：**幂等摘车 → 再放行原版**（完整方案见 §3.5；两条硬要求见同节） |
| `Source/1.6/Bike/Patch_Kill.cs` | **Harmony 补丁 3/3**：`Kill` **前缀**干预（死亡解绑） | Prefix on `Verse.Pawn::Kill(Nullable<Verse.DamageInfo> dinfo, Verse.Hediff exactCulprit = null)`：**先抓引用 → 幂等摘车 → 再放行原版 `Kill`**（真值表 §2.8 已确证 `Kill` 内部**零** `carryTracker`/`inventory` 解绑，故摘车不会与 base 重叠） |
| `Source/1.6/Bike/CompSharedBike` 的 `Unbind` | 解绑总线的**唯一写入口**（§3.2），由上面三个补丁与 Job 结束共同调用 | 幂等（P3）：重复调用不报错、不二次销毁车 |

### 2.3 ⚠ 最反常识的一条：**原生 XML 里没有任何 `RecipeDef` 产出 `MinifiedThing`**【原版XML确证】

- 全 `Data\` 目录 grep `MinifiedThing` 共 **80 处命中**，全部在 ThingDef 上下文（`<minifiedDef>` 或其 def 定义）。
- 精确 grep `<thingDef>MinifiedThing<` → **0 命中**。
- `RecipeDefOf` 只有 `ExtractHemogenPack / ImplantEmbryo / ImplantXenogerm / InstallPegLeg / RemoveBodyPart / SmeltOrDestroyThing / Sterilize`，没有任何"打包/安装"配方。
- **后果**：原版获得微型化物品的唯一途径是"卸载一个已放置的建筑"。
  → **"在锻造台造出一个物品形态的车"这一步是本项目唯一必须自己造的机制**。

**照抄路径（原版 `MinifiedTree` 先例）**【原版XML确证 + IL确证】：

```xml
<!-- Items_Unfinished.xml:261-265 的原版原型：
     <ThingDef ParentName="MinifiedThing">
       <defName>MinifiedTree</defName>
       <thingClass>MinifiedTree</thingClass>  -->
<ThingDef ParentName="MinifiedThing">
  <defName>SharedBikeMinified</defName>
  <label>shared bike (packed)</label>
  <thingClass>SharedBikeMod.MinifiedBike</thingClass>
  <stackLimit>1</stackLimit>          <!-- 原版 MinifiedThing 不写 → 默认 1 -->
</ThingDef>
```

```csharp
public class MinifiedBike : RimWorld.MinifiedThing {
    public override void PostMake() {          // Verse.Thing::PostMake 是 public virtual【IL确证】
        base.PostMake();
        InnerThing = Verse.ThingMaker.MakeThing(SharedBikeDefOf.SharedBike, SharedBikeDefOf.Steel);   // ⚠ 2026-10-13 实测更正①：本行是反例，不要照抄（正确写法见下方更正块）
        //                                    ^ ~~RimWorld.ThingMaker::MakeThing(def, stuff)~~ → 见下方「实测更正①」
    }
}
```

> #### ⚠ 2026-10-13 实测更正①：上面那行 `MakeThing(…, SharedBikeDefOf.Steel)` **是错的**（反例保留，不要照抄）
>
> **反例（原错误写法，逐字保留）**：`InnerThing = Verse.ThingMaker.MakeThing(SharedBikeDefOf.SharedBike, SharedBikeDefOf.Steel);`
> **→ 已被探针运行时实测推翻：换掉这段示例代码里的 `stuff` 实参**（依据：`tmp\bike-probe\run-3\RUN3-REPORT.md` §1.4 / §1.8-(a)；探针现场 `tmp\bike-probe\run-2\RUN2-REPORT.md` §4-C1、`tmp\bike-probe\run-1\RUN-REPORT.md` §2-C1）。
>
> **现在正确的写法（run-3 报告 §1.8-(a) 的推荐修法 ①：不传 `stuff`）**：
>
> ```csharp
> InnerThing = Verse.ThingMaker.MakeThing(SharedBikeDefOf.SharedBike);   // ← 只传 def，不传 stuff
> ```
>
> **根因（IL确证，`Verse.ThingMaker::MakeThing` 的三条守卫）**：
> ① `stuff != null && !stuff.IsStuff` → Error + 换默认 stuff；
> ② `MadeFromStuff && stuff == null` → Error + 换默认 stuff；
> ③ **`!MadeFromStuff && stuff != null` → `Log.Error("… is not madeFromStuff but stuff=…")` + 把 `stuff` 置 `null`**。
> 而 **`MadeFromStuff == !stuffCategories.NullOrEmpty()`**（`Verse.BuildableDef::get_MadeFromStuff` 的 IL 只有这 4 条指令）⇒ **凡是"用 `<costList>` 固定材料"的 def，都不许给它传 `stuff`**。
>
> **为什么本项目必然踩中**：`SharedBike` 的 def 走的是 `<costList><Steel>10</Steel></costList>`（**钢铁是配方成本，不是材质**），而 §2.1 给的 def 字段清单里**没有 `stuffCategories`** ⇒ `MadeFromStuff = false` ⇒ 传 `Steel` 必报红字。
>
> **实测红字原文**（真红字，`Verse.Log::Error` 级）：
> `MakeThing error: ProbeBike is not madeFromStuff but stuff=Steel. Setting to null.`
> （run-2 十对、run-1 两对，逐辆一次；探针是**逐字照抄本示例**才踩中的。）
>
> **备选修法（可行但不推荐做第一版）**：给 def 补 `<stuffCategories><li>Metallic</li></stuffCategories>` + `<costStuffCount>`，并把 `stuff` 换成**外壳自己的 `this.Stuff`**（不要硬编码 `ThingDefOf.Steel`）。**连锁代价**：`MadeFromStuff = true` 会改 `MinifiedThing::get_Graphic` 的取图路径、`Mass` 的来源，并产生 `costList` 与 `costStuffCount` 的重复计费问题（run-3 报告 §1.8-(a) 方案 ②）。
>
> **纪律**：`<costList>` 固定材料的 def ⇒ **永远不要给它传 `stuff`**。

**四个必须知道的连带事实**（都是 IL 确证，写错就是 bug）：

| # | 事实 | 后果 / 纪律 |
|---|---|---|
| a | `InnerThing` 的 setter：**已有内层时再装 → `Log.Warning("Assigned 2 things to the same MinifiedThing ...")` + `ClearAndDestroyContents()`**【IL确证】 | **`PostMake` 里只装一次**；不要在 `ExposeData`/读档复位里再装 |
| b | `get_Graphic()` 第一行就解引用 `InnerThing` → **内层为空直接 `NullReferenceException`**【IL确证】 | 任何路径都不许让空壳长期存在 |
| c | `Tick()` 里 `if (InnerThing == null) { Log.Error("MinifiedThing with null InnerThing. Destroying."); Destroy(Vanish); }`【IL确证】 | 直接把 `MinifiedThing` 当配方产物 = **造出来就自毁 + 刷红字** |
| d | `ThingMaker.MakeThing` = `Activator.CreateInstance(def.thingClass)` → `def = def` → `SetStuffDirect(stuff)` → **`PostMake()`** → **`PostPostMake()`**【IL确证】 | `PostMake` 里造内层时，`Bike.def` 已被赋值，`Mass` 一类 stat 可正常求值 |

> ### ⚠ 2026-10-13 实测更正：**别把 `Thing.PostMake` 写成 `ThingComp.PostMake`**（两者不是一回事）
>
> | 类 | 造出来时的钩子 | 可覆写？ |
> |---|---|---|
> | **`Verse.Thing` / `Verse.ThingWithComps`**（← **我们的 `MinifiedBike` 走这条**） | **`public virtual void PostMake()`** | ✅ 就是本文用的那个 |
> | **`Verse.ThingComp`**（车/小人身上的 Comp） | **`public virtual void PostPostMake()`**（⚠ **`ThingComp` 上没有 `PostMake`**，写它会编译失败） | ✅ 兄弟方法用 `PostPostMake` |
>
> 真值表 §3.5bis 已把 `Verse.ThingComp::PostMake` 登记为"**确证不存在**"，并把 `PostPostMake` 列为正确替代。
> **本节的 `MinifiedBike.PostMake` 设计仍然成立**（它继承 `RimWorld.MinifiedThing : Verse.ThingWithComps`，继承链上有 `Thing.PostMake`）。
> ⚠ **但"这个 `PostMake` 能不能跟 `minifiedDef` 共用一个 def"另有专条更正 —— 见本节的「实测更正②」**：装内层这个**动作**没问题，**错的是 def 布局**。

> ### ⚠ 2026-10-13 实测更正②：**同一个 def 既当"配方产物"又当建筑的 `minifiedDef` = 必刷红字 + 白造白销毁一个 Thing**
>
> **反例（原设计写法，保留）**：`Defs/ThingDefs/ThingDef_SharedBike.xml` 里 `<minifiedDef>SharedBikeMinified</minifiedDef>`，而 `SharedBikeMinified` 的 `<thingClass>` 就是 `SharedBikeMod.MinifiedBike` —— **同一个 def 同时承担两个用途**（§2.1 的两个 Def 行 + §2.2 的 `MinifiedBike.cs` 行就是这么写的）。
> **→ 已被探针运行时实测 + IL 推翻：把它拆成两个不同的 def**（依据：`tmp\bike-probe\run-3\RUN3-REPORT.md` §1.5 / §1.6-(b) / §1.8-(b)；现场日志见 `tmp\bike-probe\run-2\RUN2-REPORT.md` §4-C1）。
>
> **现在正确的 def 布局（run-3 报告 §1.8-(b) 的推荐修法 ①：拆 def）**：
> | def | `ParentName` | `thingClass` | 内层由谁装 |
> |---|---|---|---|
> | **建筑的打包产物**（`SharedBike` 的 `<minifiedDef>` 指向它） | `MinifiedThing` | **不写**（= 原版 `RimWorld.MinifiedThing`） | **原版 `MakeMinified` 唯一一次**（无红字） |
> | **配方产物**（§2.1 的 `Recipe_MakeSharedBike.xml` 产出） | `MinifiedThing` | `SharedBikeMod.MinifiedBike`（覆写 `PostMake`） | 我们自己的 `PostMake`（这条路上**没有任何原版代码**会再装一次，也无红字） |
>
> **根因（IL确证）**：`RimWorld.MinifyUtility::MakeMinified` 的顺序是
> `ThingMaker::MakeThing(thing.def.minifiedDef, null)`（`IL_006A–0076`，**stuff 传 null**；内部跑一次 `PostMake` = **第 1 次装内层**）→ 返回后**原版自己又调一次** `MinifiedThing::set_InnerThing(原建筑)`（`IL_0081–0083` = **第 2 次**）。
> 而 `set_InnerThing` 发现容器非空时：**`Log.Warning("Assigned 2 things to the same MinifiedThing …")`**，并且**接着调 `ThingOwner::ClearAndDestroyContents()`** —— 它是逐条 `callvirt Verse.Thing::Destroy(mode)`。
> ⇒ **第二次装内层会把第一次装进去的那个内层真正 `Destroy` 掉**（不是"多装一个"，是"**白造一个 Thing 又把它销毁**"，ThingID 计数被吃掉一次）。
>
> **实测红字原文**（注意级别：`Verse.Log::Warning`，**不是 Error**）：
> `Assigned 2 things to the same MinifiedThing ProbeBikeMinified9960 (first=ProbeBike9961 second=ProbeBike9956)`
> （`first` = 我们 `PostMake` 造的那个一次性内层；`second` = 那辆原建筑。run-2 十对、run-1 两对。）
>
> **三条已排除的错误归因（run-3 报告 §1.5，别再去查它们）**：① `PostMake` 被调两次 —— **排除**（每对红字只对应一条 `C1 PostMake` 日志，1:1:1）；② 存档重载又装一次 —— **排除**（`MinifiedThing::ExposeData` 全文只有 `ThingWithComps::ExposeData` + `Scribe_Deep::Look(ref innerContainer, …)`，**直写字段、绕开 setter**）；③ 探针在别处又装一次 —— **排除**（全仓 `InnerThing =` 只有一处）。
>
> **⚠ 同时必须说清的另一半（否则会矫枉过正）**：`PostMake` 里装内层这条**动作本身未必错** —— 原版 `MinifiedTree` 的先例里 `PostMake` 不装内层，**只是因为那条路上的内层由原版装**；"配方产出"这条路上**没有任何原版代码会装内层**，所以在那条路上 `PostMake` 是**唯一写入者**，不会刷红字。**问题只出在"两个用途共用一个 def"。**
>
> **连带更正**：本节「四个必须知道的连带事实」的 **a 行**纪律（"`PostMake` 里只装一次；不要在 `ExposeData`/读档复位里再装"）**只防住了 `ExposeData` 那条路**（而 IL 已证明 `ExposeData` 根本不会走 setter ⇒ 它防的是一个不存在的威胁），**完全没有提到"原版 `MakeMinified` 会装第二次"** —— 这一条缺口已按真值表 §6.1 的格式追加进 **真值表 §2.9**。

---

### 2.4 更正历史（已迁出）

> 本节的更正历史已迁至 **机制真值表 §8（append-only）**。本节只保留当前有效结论。
>
> 原 §2.4「2026-10-13 实测更正总账（14 处）」的**全部反例（原错误写法）与逐条实测更正**，已按真值表 §6.1 的格式逐条写入
> `tmp\doc-migration\v2-evidence-layer.md`，将由另一路工作追加进真值表 **§8 设计文档更正历史（append-only）**。
>
> **本文档只保留当前有效结论**：符号全名一律以真值表 §1 / §2 为准；本文写"未确证"= 真值表里查不到该条。
> **本节唯一的诚实标注（保留在正文，因为它改变"要不要现在写代码"的判断）**：`Verse.Thing::PostMake` 能跑 + 会 `callvirt` + `RimWorld.MinifiedTree` 有先例 + `InnerThing` 的 setter 是 public，**都是 IL 确证**；~~但"**自定义 `MinifiedThing` 子类在 `PostMake` 里装内层，然后在游戏里真的正常工作**"这条是 **IL 层面的强推论，从未运行时实测**（真值表 §5 待确证第 12 项，见本文 §9.1-12）。~~
> **→ 2026-10-14 实测更正：这一条已在真实运行的游戏里跑通（真值表 §5 第 12 项已结案）。** 判据与证据见下方「run-4 实测」块；真值表侧落点为 **§2.9.4** / **§2.9.5**。

> ### ✅ 2026-10-14 实测：**"物品形态 / `MinifiedBike` / 配方"这条链路已跑通**
>
> **结论**：§2.3 这条反常识结论（"原版没有任何产出 `MinifiedThing` 的 `RecipeDef`，所以这一步必须自己造"）**设计上仍然成立**，而**"自己造"这条路本身已经实测可行** —— 探针 mod 里那条"锻造自行车"配方，**真的能让一个小人造出一个正确的打包好的车**。
>
> **判据（三条，全部来自同一轮实测）**：
> | # | 判据 | 实测值 |
> |---|---|---|
> | 1 | **产物 def = 配方声明的那个** | 格 `(120,121)` 回读 `{ defName:"ProbeBikeMinified", className:"BikeProbe.MinifiedBike", stuffDefName:null, isBlueprint:false, isFrame:false, hitPoints:100 }` = 配方 `<products><ProbeBikeMinified>1</ProbeBikeMinified></products>` |
> | 2 | **内层唯一**（只有一个内层，不是"装了两次"） | ① `PostMake` 打点 `inner=ProbeBike InnerThing=ProbeBike stuff=null`；② 产物 `Label` 是内层的 `Probe bike`；③ 产物自带 `Install (RimWorld.Designator_Install)` gizmo（由 `InnerThing` 现算）；④ 真 Install 之后**原版日志自己打出** `A2 T1 到位 第0/1辆 bike=ProbeBike9485 def=ProbeBike spawned=True pos=(117, 0, 121) minifiable=True` |
> | 3 | **配方路径红字 0 条** | `not madeFromStuff = 0`、`MinifiedThing null InnerThing = 0`（**改探针之前是 1 条** `MakeThing error: ProbeBike is not madeFromStuff but stuff=Steel.`，即上面「实测更正①」那一行示例代码被逐字照抄的后果） |
>
> **证据路径**：`tmp\bike-probe\run-4\RUN4-REPORT.md`（**一切以它为准**：§2 阶段 B 的 B0–B12、§3 判据表与"必须讲清的时间线"、§4 产物正确性、§5 探针改动与重编译记录）。逐次调用原文在 `tmp\bike-probe\run-4\evidence\`；一键复跑脚本 `tmp\bike-probe\run-4\p20-full.mjs`；截图 `tmp\bike-probe\run-4\shots\`（原版自己弹出的 `Bill complete: Make probe bike.` 在 `B-01-product-runB.png`）。
> **探针侧改动（只 1 个文件、2 处，且只到"能验证 C1"为止）**：`mods/BikeProbe/Source/1.6/ProbeBike.cs` 的 `MinifiedBike.PostMake` —— `stuffDef = ProbeDefs.Steel` → **`stuffDef = null`**（即把本文「实测更正①」的修法落到实处），另加一行日志 `innerMadeFromStuff=` 让判据可在游戏内回读；重编译 `BikeProbe.dll` **56.0 KB 成功**。
> **仍未确证 2 项（不许当"已验"读）**：① 产物的**"被搬运"没有直接观测到**（两次"划储藏区 → 等小人搬"都失败，原因已查明：基础 `MinifiedThing` def 没有 `thingCategories`、那片储藏区的 `filter.allowed` 只有 7 个 def ⇒ **打包车不被该过滤器接受**，不是产物的缺陷；第三种仓储口径没做）；② **逆路径**（`Uninstall` / 探针 AI 整理）仍打 1 条 `Assigned 2 things to the same MinifiedThing` 红字 —— **这属于本文「实测更正②」，不属于本条配方链**（修法仍是"拆成两个 def"）。完整清单见 `tmp\bike-probe\run-4\RUN4-REPORT.md` §7。

---

## 3. 人车解绑总线（**用户最关心、也是风险最大的部分**）

> 占比判断：前几轮把这条定为**整个项目约 40% 的成败**。漏一条 = "车永远挂在死人身上"，而且是偶发。
> **参照系：Giddy-up 只覆盖了"倒地 + 死亡"两个点——那是下限，不是标准**（它连读档都没覆盖）。
>
> ### 🔴 2026-10-13 实测更正（人车解绑轮）
>
> 本节上一版（v2 初稿）有 **8 处**与本轮实测不符，其中 **3 处会直接导致恶性 bug**。
> **反例（原错误写法）与逐条实测更正已迁出**：见 **机制真值表 §8（append-only）** / 交接件 `tmp\doc-migration\v2-evidence-layer.md`。**本节只保留更正后的当前结论。**

**表 A · ❌ 不可覆写 / 必须 Harmony —— 这是完整清单，一条都不能少**

| 成员 | 签名 | 结论 |
|---|---|---|
| `Verse.Pawn_HealthTracker::MakeDowned` | `private void MakeDowned(Nullable<Verse.DamageInfo> dinfo, Verse.Hediff hediff)`（`flags=0x0081`） | ❌ **非虚且 private ⇒ 子类覆写这条路不存在**。⚠ 整个 `Verse.Pawn_HealthTracker` **零 virtual/abstract** ⇒ 没有更省的替代挂点；它的 2 个调用者 `CheckForStateChange` / `ForceDeathrestOrComa` **也都是 private**。<br>**本项目第一版不挂它**：倒地解绑由 `Kill` 前缀 + 「倒地但没死」记为已知缺口 + 启动自检（§3.6）；要不要补成**第 4 个** Harmony 补丁是第一版之后的决定 |
| `Verse.Pawn_HealthTracker::SetDead` | `public void SetDead()`（`flags=0x0086`，无 `0x0040`） | ❌ 非虚 ⇒ 想 hook 只能 Harmony；**但不要挂它**（它不是死亡总线，见 §3.3-8） |
| `Verse.AI.JobDriver::DriverTick` / `DriverTickInterval` / `EndJobWith` | `public void ...`（非虚） | ❌ 想改 Job 主循环必须 Harmony |
| `Verse.Pawn::Notify_Teleported` | `public void Notify_Teleported(bool endCurrentJob = True, bool resetTweenedPos = True)`（非虚） | ❌ 想 hook 必须 Harmony（**只是"传送后清状态"则直接调用即可**） |
| `Verse.Scribe_References`（**整个类，含 `Look<T>` 全部重载**） | `static class`，4 个 `public static` 成员 | ❌ **零可覆写成员；且不注册任何解绑回调**（见 §3.4） |
| `RimWorld.ActiveTransporter::Tick` | `protected void Tick()`（**非虚**） | ❌ 想改运输舱主循环必须 Harmony |
| `Verse.Pawn::teleporting` | `public bool teleporting`（**public 字段**） | — **读写即可**（见检查表第 14 条） |

> ⚠ **本文档仍有一处"真值表里也查不到"的符号留在正文**（按纪律标"未确证"，**不要照抄**）：
> `map.events.Notify_ThingSpawned` / `Notify_ThingDespawned`（用在 §3.2 方案 C 的说明里）。
> 真值表**没有收录**这一对方法；`Verse.Map` 的完整字段表本轮被工具坑挡住（见附：工具坑行）。
> **它们不影响任何推荐方案**（推荐方案用 `carryTracker` + `Thing.Position`，不依赖 spawn/despawn 事件）；**要用方案 C 前必须先确证**。

**表 B · ✅ 可子类覆写 / 0 Harmony**

| 挂点 | 签名 | 结论 |
|---|---|---|
| `Verse.Pawn::Kill`（**死亡总线**） | `public virtual void Kill(Nullable<Verse.DamageInfo> dinfo, Verse.Hediff exactCulprit = null)` | ✅ 可覆写；**本项目改用 Harmony 前缀**（用户已拍板用 carryTracker，见 §3.5） |
| `Verse.Pawn::ExitMap`（**离图 / 远行队收口**） | `public virtual void ExitMap(bool allowedToJoinOrCreateCaravan, Verse.Rot4 exitDir)` | ✅ 可覆写；**本项目改用 Harmony 前缀**（同上，见 §3.5） |
| `Verse.ThingComp::PostDeSpawn`（**车被拆 / 被毁**） | `public virtual void PostDeSpawn(Verse.Map map, Verse.DestroyMode mode = Vanish)` | ✅ **覆写，0 Harmony**（引擎 `callvirt` 派发，raw 字节已复核） |
| `Verse.ThingComp::PostSpawnSetup` / `::PostExposeData` / `::PostPostMake` | 均 `public virtual` | ✅ 覆写，0 Harmony |
| `Verse.MapComponent` 的 8 个生命周期方法 | `MapComponentUpdate` / `MapComponentTick` / `MapComponentOnGUI` / `MapComponentDraw` / `ExposeData` / `FinalizeInit` / `MapGenerated` / `MapRemoved`（**全部 `flags=0x01c6`**） | ✅ **全部可覆写，0 Harmony** |
| `Verse.AI.ThinkNode_JobGiver::TryGiveJob` | `protected abstract Verse.AI.Job TryGiveJob(Verse.Pawn pawn)` | ✅ 必须实现 |
| `Verse.AI.ThinkNode_JobGiver::TryIssueJobPackage` | `public virtual Verse.AI.ThinkResult TryIssueJobPackage(Verse.Pawn pawn, Verse.AI.JobIssueParams jobParams)` | ✅ 覆写，0 Harmony |
| `Verse.AI.JobDriver::MakeNewToils` | `protected abstract IEnumerable<Verse.AI.Toil> MakeNewToils()` | ✅ 必须实现 |
| `Verse.AI.JobDriver::TryMakePreToilReservations` | `public abstract bool TryMakePreToilReservations(bool errorOnFailed)` | ✅ 必须实现（**真名以此为准**） |
| `Verse.AI.JobDriver::GetReport` / `Notify_PatherArrived` / `Notify_PatherFailed` | `public virtual` | ✅ 覆写，0 Harmony |
| `Verse.AI.JobDriver::ModifyCarriedThingDrawPos`（**骑行姿态偏移**） | `public virtual bool ModifyCarriedThingDrawPos(ref UnityEngine.Vector3 drawPos, ref bool flip)`（默认 `return false`） | ✅ **覆写，0 Harmony**（见 §3.5） |

> **本节其余内容（P1–P3 原则、3.2 解绑动作、3.3 检查表、3.4 读档）已按上述两张表逐条更正。**
> **Harmony 补丁总数 = 3**（`TicksPerMove` §4.5 + `ExitMap` §3.5 + `Kill` §3.5）。表 A 里其余的"必须 Harmony"项**本项目一条都不挂**（`MakeDowned` 是第一版之后的可选第 4 个，见 §3.6）。

### 3.1 三条贯穿全局的设计原则（先立规矩，再列清单）

| # | 原则 | 为什么 |
|---|---|---|
| **P1** | **骑行状态只存"车 → 骑手"一个方向；反向引用不持久化** | 双向引用在存档里是"两个引用必须互相指对"的问题 ⇒ 任一端先被删（小人死了、车烧了）就会留下**"一半引用为 null、另一半还在"**。单向 + `PostLoadInit` 自愈 = 读档时**只需修复活的一端**。<br>✅ **放行条件已确证**：`Verse.Thing` **确实实现** `Verse.ILoadReferenceable`（`GetUniqueLoadID()` = `"Thing_" + ThingID`），全程序集有 **80 个原版调用点**把 `Verse.Thing` 当 `T` 传给 `Verse.Scribe_References.Look<T>` ⇒ 存车引用也合法（真值表 §2.8 / 容器取证 `tmp\il-evidence-container\REPORT.md` §D.1）。**第一版仍按单向走**：只存 `Verse.Scribe_References.Look<Verse.Pawn>(ref rider, "rider")` |
| **P2** | **`JobDriver_RideSharedBike` 的 `AddFinishAction` 是"下限兜底"，不是主路径** | Job 结束有 `Succeeded` / `InterruptForced`(16) / `Incompletable`(4) / `Errored`(64) / `ErroredPather`(128) 多种 `JobCondition`【IL确证】；`AddFinishAction` 涵盖全部，但**不涵盖"车被销毁"、"小人倒地/死亡"、"离图"和"读档"** |
| **P3** | **解绑动作必须幂等** | 同一次中断可能触发多个钩子（例：小人中弹 → Job 结束 + `ExitMap` 商队路径重入 + 读档校验）。`Unbind()` 重复调用不得报错、不得二次销毁车 |

### 3.2 解绑动作的唯一定义

```csharp
// 伪代码：整个总线只有这一个写入口
void Unbind(Pawn rider, Thing bike, IntVec3? dropAt) {
    if (bike == null || bike.Destroyed) { ClearRiderState(rider); return; }   // P3 幂等
    if (bike.Spawned) {                                                       // 车已在地图上（正常情形）
        if (dropAt.HasValue) MoveThingOnMap(bike, dropAt.Value);              // §5.6 方案 ①/②
    }
    ClearRiderState(rider);   // 清 Comp 上的 rider / 清小人身上的 riding 标记
}
```

**"车在骑行期间是什么状态？"——用户已拍板：车进 `carryTracker`（= 被 `DeSpawn` 的容器内容物）。**

| 方案 | 代价 |
|---|---|
| **★ 车进 `carryTracker`（已拍板）** | ✅ **渲染免费**：原版渲染链每帧自动画它，**贴图连续跟随小人**（`Verse.PawnRenderNodeWorker_Carried::PostDraw` → `Verse.PawnRenderUtility::DrawCarriedThing` → `Verse.Thing::DrawNowAt`），**不需要自己写插值**（原版 `Verse.Pawn_DrawTracker::DrawPos` 本来就是每帧插值）。<br>✅ **存档免费**：`Verse.Pawn_CarryTracker::ExposeData` = `Verse.Scribe_Deep.Look<Verse.ThingOwner<Verse.Thing>>(ref innerContainer, "innerContainer", new object[]{ this })`，容器内容物随小人一起存 ⇒ **不需要手写往返存档**。<br>⚠ **进入时必须先 `bike.DeSpawn(DestroyMode.Vanish)`**（容器**绝不**帮调用者 `DeSpawn`）。<br>⚠ **代价**：车从 `map.thingGrid` / `listerThings` / `listerHaulables` / 储区 / 预约 / 寻路代价里**全部消失**（"在地图上被别人看见"的共享语义在骑行期间失效）；`carryTracker` 的 `maxStacks = 1` ⇒ **同时只能抱 1 辆**。<br>⚠ **离图/死亡路径必须打补丁**（`ExitMap` 会对非 `Pawn` 的车 `Destroy(Vanish)`）⇒ 见 §3.5 |
| A. 保持 spawned，骑行期间每 tick 把 `Position` 设到小人脚下 | ❌ 已否：会让"车跟着一个已放弃骑行的小人跑"，且每 tick 移动 Thing 是高频路径 |
| B. 保持 spawned，骑行期间车停在原地不动（视觉上车"留在原地"，小人自己加速走） | ❌ 已否（**用户不接受这个视觉**；原 §10-M6 的取舍已被用户拍板否掉） |
| C. 骑行期间保持 spawned 且 `DeSpawn` 掉、下车时 `GenSpawn.Spawn` | ❌ 已否：`DeSpawn` 后一样会丢共享语义，却**拿不到**渲染与存档白送项 |

> **本文其余部分按「★ 车进 `carryTracker`」这条写**（用户已拍板）。上表 A / B / C 三条**均已否**，仅作反例保留（原始取舍记录见真值表 §8）。
> **进入 `carryTracker` 的完整动作序列**：① `bike.DeSpawn(DestroyMode.Vanish)` → ② `pawn.carryTracker.TryStartCarry(bike)`（**进容器必须由调用者先 `DeSpawn`**）→ ③ 记 `rider` + 设骑行状态（§3.4）。
> **退出时**：`pawn.carryTracker.TryDropCarriedThing(...)`（**它内部会调 `GenPlace.TryPlaceThing` 找落点**）→ 再走 §3.2 的 `Unbind`。
> ⚠ `carryTracker` 是**原版共用通道**（搬东西、抱小孩），所以每次操作前**必须**判 `CarriedThing` 是不是我们的车。

### 3.3 解绑检查表（**每条都必须实现 + 必须验证**）

> **2026-10-13 实测更正**：本表第 7 / 8 / 15 行的挂点**全部改掉了**（死亡不挂 `SetDead`、倒地 `MakeDowned` 是 `private`、离图是 `ExitMap`）；第 5 / 9 / 12 / 14 行的触发点按实测补齐。
> **反例与原错误写法已迁出** → 真值表 §8 / `tmp\doc-migration\v2-evidence-layer.md`。触发点与"可覆写 / 需 Harmony"的对照见 §3 开头的**表 A / 表 B**。

| # | 场景 | 触发点（哪个方法/事件） | 要做的解绑动作 | 怎么验证 |
|---|---|---|---|---|
| 1 | **Job 正常完成** | `JobDriver_RideSharedBike` 最后一个 toil 完成 → 走 `AddFinishAction` | `Unbind(rider, bike, 目的地落点)`；车落到合法格（§5.3） | 下令小人骑车到 20 格外 → 到点后：人速回 3.0、车在地上、`Comp` 的 `rider == null` |
| 2 | **Job 被打断（强制）** | `Verse.AI.Pawn_JobTracker.EndCurrentJob(JobCondition.InterruptForced)`（值 **16**）【IL确证】——一切"打断"的最后必经之路 | `AddFinishAction` 兜底 → `Unbind` | 骑车途中右键点别处派活 → 同上三条 |
| 3 | **玩家按 D 征召** | `RimWorld.Pawn_DraftController.set_Drafted(bool)`【IL确证：`ClearQueuedJobs` + 若 `IsCurrentJobPlayerInterruptible()` 则 `EndCurrentJob`】 | ⚠ **这里是我们主动筑的墙**：`JobDef.RideSharedBike` 设 `<playerInterruptible>false</playerInterruptible>` → `IsCurrentJobPlayerInterruptible()` 返回 false → **征召不会打断骑行 Job**【IL确证：闸门 = `JobDef.playerInterruptible` → `JobDriver.PlayerInterruptable` → 着火检查】。于是"征召中途打断"这一条**不存在**，不需单独解绑 | 骑车途中按 D → 骑行**继续**、速度**仍然**是车速、车不掉 |
| 4 | **饥饿 / 疲惫** | 由 `ThinkTree` 的 need 子树经 `JobCondition.InterruptOptional`(8) 触发 Job 切换【IL确证枚举值】 | 走第 2 条的同一路径（`EndCurrentJob` → `AddFinishAction`） | 把 `Need_Food` 调低到临界，骑车途中触发进食 → 人速回 3.0、车在原地 |
| 5 | **精神崩溃** | 正确读法是 **`Verse.Pawn::MentalState`（非虚属性）**，它转发到 **`mindState.mentalStateHandler`（`Verse.AI.MentalStateHandler`）的 `CurState`**；`Verse.AI.Pawn_MindState` 本身**没有 `MentalState` 成员**。判定边界仍可用 `Verse.AI.JobTag.InMentalState = 4`【IL确证】 | 同第 2 条；**另外** mental state 结束后不要恢复骑行（状态已清） | 用 debug action 触发 mental break 于骑行途中 → 崩溃结束后小人不再骑车、车在地上 |
| 6 | **lord 征召 / 集结** | `Verse.AI.Group.Lord` 给成员派 job（`GetGizmos` 在 `Pawn.GetGizmos` 状态机里被枚举，IL_0FD6/IL_104F）【IL确证】 | 同第 2 条 | 用"召集"类 lord job（袭击/远行队集合）于骑行途中 → 同上 |
| 7 | **倒地** | 真名 **`Verse.Pawn_HealthTracker::MakeDowned(Nullable<Verse.DamageInfo> dinfo, Verse.Hediff hediff)`**（**`private`**，`flags=0x0081`）。<br>✅ 正确命名空间是 **`Verse`**（`RimWorld.Pawn_HealthTracker` **不存在**）。<br>❌ **`private` 非虚 ⇒ 不能覆写**。它的全部 2 个调用者 `CheckForStateChange` / `ForceDeathrestOrComa` **也都是 private** ⇒ 没有更省的替代挂点。<br>**第一版决定：不挂它**（避免第 4 个 Harmony 补丁）——「倒地但没死」由 `Kill` 前缀**兜不住**，登记为**已知缺口**并在启动自检里明示（§3.6） | **主动 `Unbind` 才是完备的**：`MakeDowned` 之后 Job 可能仍在（`JobDef.isCrawlingIfDowned` 存在），不能只靠 `AddFinishAction`。<br>⚠ 第一版走"降级"：把"倒地但没死"当作已知缺口；若实测发现它真的常见，再补第 4 个 Harmony 补丁 | 骑行途中 debug 打伤到倒地 → 车在地上、人速恢复；复活后再检查一次 |
| 8 | **死亡** | **2026-10-13 实测更正（🔴 最高危）**：正确挂点 = **`Verse.Pawn::Kill(Nullable<Verse.DamageInfo> dinfo, Verse.Hediff exactCulprit = null)`**。<br>原因：`Verse.Pawn_HealthTracker::SetDead()` 是 `public` **非虚**（`flags=0x0086`），且**函数体只有 4 条指令**（`if (get_Dead()) Log.Error("{0} set dead while already dead.")`；`stfld healthState = 0`；`ret`）—— **不触发任何通知、不掉落物品、不做解绑**。把解绑挂它上面 ⇒ **小人死了但车还挂着**（资源永久泄漏，且只偶发）。<br>`SetDead` 的全部 5 个调用者中，`Verse.Pawn::Kill`（@447）是最主要的那个。<br>真值表 §2.8 另已确证：**`Kill` 全程不访问 `carryTracker`**（144 个 `carryTracker` 读取者名单里没有它，`DoKillSideEffects` 也没有），且它内部**对随身容器零解绑** ⇒ 我们的摘车动作**不会与 base 重叠** | `Unbind`（先抓引用 → 摘车 → 再放行原版 `Kill`）；**小人尸体/装备处理完之后车必须还在**。<br>✅ 实现方式 = **Harmony 前缀**（用户已拍板，与 `ExitMap`、`TicksPerMove` 合计 **3 个补丁**；理由见 §3.5「为什么不走 Pawn 子类覆写」） | 骑行途中 debug 杀死小人 → 车在地上、地图上车的总数不变 |
| 9 | **被囚禁 / 被俘** | 真名 **`Verse.Pawn::GuestStatus`**，类型是 **`Nullable<RimWorld.GuestStatus>`**（⚠ 枚举在 **`RimWorld`**，写 `Verse.GuestStatus` 会编译失败）。<br>**它是"有 getter 的属性"，不是字段也不是方法**（`P Nullable<GuestStatus> GuestStatus get=public set=- virt=False`）→ **不能写 `pawn.GuestStatus()`**，**也不能赋值**（无 setter ⇒ 想"改"只能 Harmony）。枚举值：`Guest = 0`、`Prisoner = 1`、`Slave = 2` | `Unbind`；车留在原地（若当时车在 `carryTracker` 里，先 `TryDropCarriedThing` 放到地上再 `Unbind`）。<br>⚠ 但"变成囚犯"这件事本身**不经过我们的任何可覆写挂点**（属性非虚）⇒ 第一版可先在**被俘后的 Job 结束**路径上兜（第 2 条），**把"必然解绑"标为未达成**（见 §9.2-Q15） | 用 debug"俘虏"于骑行途中 → 车在地上 |
| 10 | **麻醉手术 / 手术中** | `Bill`（手术）派 job；或 `Need_Anesthesia`（**均未在真值表登记**） | `Unbind`；与第 2 条同路径 | 安排手术 → 骑行状态已清 |
| 11 | **读档** | ⚠ **最容易漏的一条**：没有任何"事件"发生，状态直接从 XML 恢复 | 见 §3.4 / §3.5 专门两节：**单向持久化** + `PostLoadInit` 自愈校验 | 骑行途中存档 → 退到主菜单 → 读档 → 人速、车、`Comp` 三者与存档前一致 |
| 12 | **车被摧毁（战争/火）** | ✅ **确证**：**`Verse.ThingComp::PostDeSpawn(Verse.Map map, Verse.DestroyMode mode = Vanish)` 是 `public virtual`**（`flags=0x01c6`），且 `Verse.ThingWithComps::DeSpawn` 的 raw 字节确认它对 `comps` **逐项 `callvirt ThingComp::PostDeSpawn`** ⇒ **子类覆写一定被调到、0 Harmony**。（该虚方法 `CALLER HITS: 14`）<br>⚠ 注意：**`Verse.ThingWithComps` 自己不声明 `PostDeSpawn`**（别覆写错类）；`Thing` 侧的兄弟是 `Verse.Thing::DeSpawn(DestroyMode mode = Vanish)`（`public virtual`） | 清小人的骑行标记（**不要去动已销毁的车**，`rider` 置 null 即可）；若车是"被骑的那辆"，让骑行 Job 走 `Errored`(64) 或提前结束 | 用 debug 炸掉正在被骑的车 → 小人速度恢复、不再引用坏车、不刷异常 |
| 13 | **车被玩家拆除（Deconstruct）** | `JobDefOf.Uninstall` → `JobDriver_Uninstall.FinishedRemoving()`【IL确证】；或 `Thing.Destroy(DestroyMode.Deconstruct)` → 同样走 `ThingComp::PostDeSpawn` | 同第 12 条 | 对正在被骑的车下"卸载/拆除" → 小人解绑 |
| 14 | **被 Teleport** | ✅ 确证：**不存在 `Pawn.Teleport` / `Thing.Teleport*`**（`findtype TeleportTo` → **0 命中**；`Verse.Pawn` / `Verse.Thing` 的 declared-only 成员表里没有任何 `Teleport*` 方法）。真实入口三条：<br>① **传送 = `RimWorld.PawnFlyer::MakeFlyer(ThingDef flyingDef, Pawn pawn, IntVec3 destCell, EffecterDef flightEffecterDef, SoundDef landingSound, bool flyWithCarriedThing = false, Nullable<Vector3> overrideStartVec = null, Ability triggeringAbility = null, LocalTargetInfo target = null)`（`public static` 工厂）**<br>② **通知 = `Verse.Pawn::Notify_Teleported(bool endCurrentJob = True, bool resetTweenedPos = True)`（**非虚** ⇒ 想 hook 必须 Harmony；只"清状态"就直接调用）**<br>③ **判别开关 = `Verse.Pawn::teleporting`（`public bool` 字段）** | `Unbind` + 摘车落地（**车不进 `innerContainer` 长期滞留**）；若需要"车跟着走"由 `ExitMap` 前缀与 `carryTracker` 天然覆盖（§3.5） | 有传送手段时测：传送后小人不再引用旧坐标的车 |
| 15 | **被远行队带离地图** | **2026-10-13 实测更正（挂点换了）**：✅ 确证：**`Verse.Pawn::ExitMap(bool allowedToJoinOrCreateCaravan, Verse.Rot4 exitDir)`** 是"离图"的**唯一收口**（全程序集 **10 个调用者**）。<br>`ExitMap` 内部顺序（raw-body 逐条读，见真值表 §2.8）：① 世界 pawn → `Log.Warning("Called ExitMap() on world pawn ")` + `ret`；② `Ideo.Notify_MemberLost`；③ `if (allowedToJoinOrCreateCaravan && RimWorld.Planet.CaravanExitMapUtility::CanExitMapAndJoinOrCreateCaravanNow(pawn)) { RimWorld.Planet.CaravanExitMapUtility::ExitMapAndJoinOrCreateCaravan(pawn, exitDir); return; }`；④ `LordUtility.GetLord(pawn)` → `Lord.Notify_PawnLost(pawn, 6, default(DamageInfo))`；⑤ **`if (carryTracker?.CarriedThing != null)` 块**：<br>&nbsp;&nbsp;• 抱的是敌对 `Pawn` → `Faction.kidnapped.Kidnap(child, this)`；<br>&nbsp;&nbsp;• 抱的是同阵营 `Pawn` → `if (!teleporting) carryTracker.innerContainer.Remove(child)` → `child.ExitMap(false, exitDir)` 递归；<br>&nbsp;&nbsp;• ⚠ **抱的不是 `Pawn`（自行车就是这里）→ `carryTracker.CarriedThing.Destroy(DestroyMode.Vanish)`**（`IL_0101`，**无条件销毁**）；<br>&nbsp;&nbsp;• 然后 `if (teleporting && child != null) 跳过; else carryTracker.innerContainer.Clear();`；⑥ `ThingOwnerUtility::AnyParentIs(...)`；⑦ 客房逻辑<br>❌ **`Caravan` 不是挂点**：原版走 **`RimWorld.Planet.CaravanExitMapUtility`（static，不可覆写）** + **`RimWorld.Planet.TravellingTransporters::AddTransporter`**；运输舱容器是 **`RimWorld.ActiveTransporter`**（`base=Verse.Thing`，成员 `age` / `Contents` / `GetDirectlyHeldThings`；**`protected void Tick()` 非虚 ⇒ 改动需 Harmony**） | ✅ **实现：Harmony 前缀干预 `ExitMap` —— 先幂等地把车从 `carryTracker` 摘出来（`TryDropCarriedThing` 放到地上 / 或按规则处置），再放行原版。**<br>**完整方案与两条落地硬要求见 §3.5**。<br>⚠ **车绝不能留在 `innerContainer` 里过 `base.ExitMap`**（会被 `Destroy(Vanish)`，数据丢失） | 组队远行（带上一个骑行中的小人）→ 车留在地图、商队里没有车、地图上车的总数不变 |
| 16 | **小人背包里还装着车**（整理工） | 与骑行无关，但同一份存档语义 | 背包里的 `MinifiedThing` 走 `ThingOwner` 深存【IL确证：`Verse.Pawn_InventoryTracker` ctor 用 `new Verse.ThingOwner\`1<Verse.Thing>(this, oneStackOnly: false, contentsLookMode: 2(Deep), removeContentsIfDestroyed: true)`】；**读档后校验 `innerContainer` 里的车没丢** | 整理工背包里带 3 辆车存档 → 读档 → 背包里还是 3 辆 |
| 17 | **异常 / 红字** | 任意 `Log.Error` 路径 | `Unbind` 必须能在**任何状态下**安全执行（P3） | 主动断言：连续调用 `Unbind` 3 次无异常、无二次销毁 |

> ### 🆕 2026-10-13 实测更正：`Verse.Pawn::teleporting` —— **"传送 vs 正常离图"的唯一判别开关**
>
> 真值（逐条读 `Verse.Pawn::ExitMap` 的 IL，**含分支目标**）：
>
> | 位置 | 真值 |
> |---|---|
> | `IL_00C7` | `!teleporting` 才执行 `carryTracker.innerContainer.Remove(child)`（`child` 是 `Verse.Pawn`） |
> | `IL_0113-011D` | **`teleporting && child != null` 时 `carryTracker.innerContainer.Clear()` 被跳过**；其余情形一律 `Clear()` |
> | `IL_007F` | **外层 null 守卫**：`carryTracker?.CarriedThing == null` 时直接跳到 `IL_012E` —— **Destroy 与 Clear 都不会执行** |
>
> ⚠ **更正（真值表 §2.8 原措辞不准确，已登记进 §8 更正历史）**：旧说法"`teleporting == true` 时随身的人被 `Clear()` **整批清空**"**不准确** —— 逐条读 IL 的结论是 **`teleporting && child != null` 时 `Clear()` 被跳过**。
>
> **对本项目的意义**：既然骑行 = 车在 `carryTracker` 里（§3.2），我们的 `ExitMap` 前缀就**必须**先判 `carryTracker.CarriedThing == 我的车` 再摘车（幂等，见 §3.5），**不要**去猜 `teleporting` 的语义。

### 3.4 读档（第 11 条）专门设计

**问题本质**："骑行中"是跨对象、跨 tick 的状态（小人 ↔ 车）。写错一个引用 → 读档后**人速度永久异常**或**车永久被占用**。

> #### 🔴 2026-10-13 实测更正 A：`Scribe_References.Look` 的泛型约束
>
> 真值（真值表 §2.8）：
> ```
> public static void Look<T>(ref T refee, string label, bool saveDestroyedThings = false)
>     where T : Verse.ILoadReferenceable;
> public static void Look<T>(ref System.WeakReference<T> refee, string label, bool saveDestroyedThings = false)
>     where T : Verse.ILoadReferenceable, class;
> ```
> ⚠ **`T` 必须实现 `Verse.ILoadReferenceable`**。
> ✅ **2026-10-13 实测更正（原以为这条路被约束挡死，是错的）**：**`Verse.Thing` 确实实现 `Verse.ILoadReferenceable`** —— `InterfaceImpl` 原始元数据里明确列着它，且 `Verse.Thing::GetUniqueLoadID()` = `"Thing_" + ThingID` 已实现；全程序集有 **80 个原版调用点**把 `Verse.Thing` 当 `T` 传给 `Verse.Scribe_References.Look<T>`（先例含 `Verse.Pawn_EquipmentTracker::ExposeData`、`Verse.ThingCountClass::ExposeData`、`RimWorld.TransportShip::ExposeData`、`RimWorld.PawnFlyer::ExposeData` …）⇒ **`Scribe_References.Look<Verse.Thing>(ref bike, "bike")` 是原版标准做法**（真值表 §2.8 / 容器取证 `tmp\il-evidence-container\REPORT.md` §D.1）。
> ⇒ **第一版仍按 P1 走单向**：只写 `Verse.Scribe_References.Look<Verse.Pawn>(ref rider, "rider")`，**不写"人 → 车"的反向引用**（理由见本节末）。
> `Verse.Scribe_References` 的完整成员只有 4 个：`Look<T>` ×2 + `CheckSaveReferenceToDestroyedThing(Thing, string, bool)` + `CheckSaveReferenceToDestroyedWorldObject(WorldObject, string, bool)`；它是 **`static class`，零可覆写成员**。
>
> #### 🔴 2026-10-13 实测更正 B：`Scribe_References.Look` 不注册任何销毁回调
>
> ✅ 实测：`Scribe_References` 是**存档期工具**，只决定"引用写不写"，**不提供任何回调 / 事件**。
> ⇒ **解绑只能写在载体侧**：`Verse.Pawn::ExitMap`（离图 / 死亡 / 传送）、`Verse.Pawn::Kill`（死亡）、`Verse.ThingComp::PostDeSpawn`（车被拆 / 被毁）、或 `Verse.MapComponent` 的 8 个生命周期方法。
> `Scribe_References.Look` 的唯一职责是：**保证"车 → 人"的引用在存档里能被正确解析 / 清掉**。

```csharp
// CompSharedBike（挂在车身上）—— 唯一的持久化方（P1）
public override void PostExposeData() {                     // ThingComp::PostExposeData 是 public virtual【IL确证】
    base.PostExposeData();
    // ✅ 目标类型是 Verse.Pawn（实现了 ILoadReferenceable），不是"车"自己 —— 这是 P1 的单向引用
    //    ⚠ 另注：Verse.Thing 其实**也**实现了 ILoadReferenceable（80 处先例），
    //      所以"存车引用"同样合法；第一版仍不存，避免双向（见本节末）
    Verse.Scribe_References.Look(ref rider, "rider");        // where T : Verse.ILoadReferenceable【IL确证】
    Verse.Scribe_Values.Look(ref lastReturnedTick, "lastReturnedTick", 0);
    if (Scribe.mode == LoadSaveMode.PostLoadInit) {          // PostLoadInit = 4【IL确证】
        // 自愈：任一侧不在就当作没在骑（这是"解绑"的兜底，不是挂点）
        if (rider != null && (rider.Dead || rider.Destroyed || !rider.Spawned)) rider = null;
        if (rider != null && rider.Map != parent.Map) rider = null;
    }
}
```

> **为什么只存一个方向**：`Scribe_References.Look` 两处互指时，`ResolvingCrossRefs`(3) 阶段两端各自解析，任一端的对象被删掉（小人死了、车烧了）就会出现"一半引用为 null、另一半还在"。
> 单向 + `PostLoadInit` 自愈 = 只需修一个位置。**小人不持久化"我在骑哪辆车"**，而是运行时问：`bike.CompSharedBike.rider == this pawn`。

### 3.5 骑行中的 `ExitMap` 干预（**用户已拍板的技术路线**）

> **技术路线（用户原话选定）**：**车进 `carryTracker`（渲染 + 存档免费）+ 用 Harmony 前缀干预 `ExitMap`**。
> 本节是**正式设计**，不再有并列选项。

**① 为什么必须干预：`IL_0101` 会销毁非 `Pawn` 的车**

`Verse.Pawn::ExitMap` 里 `if (carryTracker?.CarriedThing != null)` 块的结构是：

```csharp
Verse.Pawn child = carryTracker.CarriedThing as Verse.Pawn;     // IL_0084-0094
if (child == null)                                             // IL_0098 brfalse.s IL_0101 —— 非 Pawn 才下这条路
    carryTracker.CarriedThing.Destroy(DestroyMode.Vanish);      // IL_0101  ★ 无条件销毁
if (!(teleporting && child != null)) carryTracker.innerContainer.Clear();   // IL_0112-012D
```

⇒ **只要不提前摘车，任何真正让小人离图的路径都会把非 `Pawn` 的车 `Destroy(Vanish)`**（`Destroy` 是终局操作：从地图注销、预约占位被清、`destroyed` 置位）。这**不限于远行队**：`IL_003D` 的商队分支提前 `ret` 只是把执行让给嵌套的 `ExitMap(false, exitDir)`，而 `RimWorld.Planet.CaravanExitMapUtility::ExitMapAndJoinOrCreateCaravan` / `ExitMapAndCreateCaravan` **内部都会 `callvirt Pawn::ExitMap(false, dir)` 回调**。

**② 落地硬要求 1 —— 必须幂等**（判据 `carryTracker.CarriedThing == 我的车`）

`ExitMap` **会被 `callvirt` 重入一次**（两条商队路径：`CaravanExitMapUtility::ExitMapAndJoinOrCreateCaravan` @`IL_0018-001B` 与 `ExitMapAndCreateCaravan` @`IL_00B2-00B4`，各自 `callvirt Pawn::ExitMap(false, dir)`）。
⇒ 前缀必须写成：
```csharp
// Harmony Prefix —— 幂等；返回 true = 放行原版
static bool Prefix(Verse.Pawn __instance) {
    var bike = __instance.carryTracker?.CarriedThing as SharedBike;   // 判据：手里的东西就是我的车
    if (bike != null) DropBikeSafely(__instance, bike);               // 幂等：第二次进来时 CarriedThing 已不是车 ⇒ 直接跳过
    return true;                                                      // ★ 永远放行原版，不要吞掉 ExitMap 的其余行为
}
```
**第二次进入时 `CarriedThing` 已经不是车 ⇒ 自然 no-op** —— 不需要额外的"已处理"标志位。

**③ 落地硬要求 2 —— 解绑时必须同时决定车去哪儿**

`Verse.ThingOwner::Clear()` **只 `Remove`、不销毁**（逐条 IL：`for (i = Count-1; i >= 0; i--) Remove(GetAt(i));`，**没有 `Destroy`**；对比 `ClearAndDestroyContents` 才销毁）。所以"只是让容器移除车"会让车变成**无主对象**（`holdingOwner` 被 `ThingOwner\`1::Remove` 清空，`Spawned` 仍为 false）——**效果等同丢失**，而且它仍挂在已离图小人的容器里被 Deep 序列化。
⇒ **摘车动作必须自带去向决策**：① **落地**（`Verse.Pawn_CarryTracker::TryDropCarriedThing(...)` 放到小人当前格 / 就近合法格，见 §5.4、§5.5-H1）；或 ② **显式销毁**并在日志里写原因。**禁止"只是不管"。**

**④ 反例（已实测否掉，保留作反例）：`"车放 carryTracker + 0 补丁"`**

只要不覆写、不提前摘车，`IL_0101-IL_010D` 就会把非 `Pawn` 车 `Destroy(DestroyMode.Vanish)`。⚠ 这条**不是"只在某条路上炸"**：非 `Pawn` 对象**必走 `IL_0101` 的销毁**，而且 `teleporting == true` 也救不了它（走非 `Pawn` 分支时 `child == null`，`IL_011C` 的 `brtrue.s IL_012E` 不会被采纳，`Clear()` 一定还会执行）。
⇒ **"0 补丁"这条方案已被实测否掉；前缀干预是必需项，不是优化项。**

**⑤ Harmony 补丁清单（= 3 个）**

| # | 补丁 | 目标 | 作用 |
|---|---|---|---|
| 1 | `Patch_TicksPerMove` | `Verse.Pawn::TicksPerMove(bool)` **Postfix** | 骑车加速（§4.5） |
| 2 | `Patch_ExitMap` | `Verse.Pawn::ExitMap(bool, Verse.Rot4)` **Prefix** | 幂等摘车 → **再放行原版**（本节 ②③） |
| 3 | `Patch_Kill` | `Verse.Pawn::Kill(Nullable<Verse.DamageInfo>, Verse.Hediff)` **Prefix** | 小人死亡时同理：**先在 `base` 之前把车的引用抓到手里 → 幂等摘车落地 → 再放行原版**。<br>依据：真值表 §2.8 已确证 `Verse.Pawn::Kill` **本身对随身容器零解绑**（它只做 `RemoveFromHoldingContainer`——那是"pawn 自己在平台/运输舱里"——与 `holdingOwner.Remove(this)`），所以我们的摘车**不会与 `base` 重叠**；但 `base` 会掉装备 / 生尸体 / `DeSpawnOrDeselect`，**所以引用必须在 `base` 之前抓**。<br>⚠ 若骑行 = 车在 `carryTracker` 里，`CarryTracker.ExposeData` 走 `LookMode.Deep` ⇒ 小人死亡时车会**跟着他被深存进尸体一侧**，因此这一处**必须有**（否则车随尸体一起进"不存在"状态） |

**⑥ 为什么不走"Pawn 子类覆写"**（`ExitMap` / `Kill` 本来都是 `public virtual`）

改小人种族的 `<thingClass>`（`ThingDef.thingClass`）会**影响游戏里每一个人**：所有小人相关系统的类型检查都跟着变（工作 / 思考树 / 渲染 / 装备 / 社交），存档里写下的类名也变（影响已有存档与第三方 mod 的 `is Pawn` / `is Humanlike` 一类判定），并且会与**所有**改人类 def 的 mod 互相覆盖。
⇒ **代价与收益不成比例**：我们的需求只是"在方法入口摘一辆车"。**3 个 Harmony 前缀补丁**是**面积最小**的干预方式（都不改变原版返回值/流程，始终 `return true` 放行）。

> ⚠ **每次改 `carryTracker` 之前先判 `CarriedThing` 是不是我们的车**：`carryTracker` 是**原版共用通道**（搬东西、抱小孩），我们的补丁**绝不能**碰不是自己车的 `CarriedThing`。这条与 P3 幂等要求一起构成前缀的**唯一判据**。



**验证清单（读档必须走全）**：

| ☐ | 项 |
|---|---|
| ☐ | 骑行途中存档 → 读档 → 人速仍是车速（说明 `rider` 解析成功） |
| ☐ | 骑行途中存档 → 读档 → 把小人 debug 杀死 → 车应解绑且仍在地上（**验的是 `Verse.Pawn::Kill` 那条钩子**） |
| ☐ | 骑行中**车**存档 → 读档 → 把车拆除 → 小人速回 3.0（**验的是 `ThingComp::PostDeSpawn`**） |
| ☐ | 整理工背包带 3 辆车 → 读档 → 背包 3 辆、地图上数量不变 |
| ☐ | 旧存档（**没有**我们的 Comp）→ 读档 → 不崩（`DefMap` / 新 Comp 的兼容，`DefMap.ExposeData` 在 `LoadingVars` 时按 `DefDatabase<D>.DefCount` 自动补齐【IL确证】） |

### 3.6 这条总线本身的设计风险（**无法自洽的地方，必须先说**）

- **用户已拍板：骑行期间车在 `carryTracker` 里（§3.2）**，所以"小人骑着不存在的车"这个视觉问题**不存在**了——原版渲染链每帧画它，贴图连续跟随。
  代价换成了另一组（§3.2）：**骑行期间车从地图的登记体系里消失**（共享语义暂时失效）+ `carryTracker` 只能装 1 辆 + **离图/死亡路径必须打补丁**（§3.5）。
- **红线不变**：**绝对不要用 `Verse.Thing::SetPositionDirect`**（只改 `positionInt`、零登记 ⇒ 鬼影），见 §5.6 与真值表 §3.7d。
- **🆕 2026-10-13：Harmony 补丁 3 个，其中 2 个落在"高频/终局"路径上。**
  本项目明确采用 **3 个** Harmony 补丁：`Verse.Pawn::TicksPerMove(bool)`（Postfix，**每 tick 每人每格**的热点）、
  `Verse.Pawn::ExitMap(bool, Rot4)`（**Prefix**）、`Verse.Pawn::Kill(Nullable<Verse.DamageInfo>, Verse.Hediff)`（**Prefix**）。
  ⚠ 风险点：**"离图解绑"与"死亡解绑"这两条防线的可靠性，取决于补丁是否成功注入**。
  **对策**：
  ① 启动时自检 3 个补丁是否挂上（`Harmony.GetPatchInfo` 一类），失败就 `Log.Error` 明示"哪条防线已降级"；
  ② **已知缺口**：「倒地但没死」（`Verse.Pawn_HealthTracker::MakeDowned` 是 `private`，第一版**不挂**它）—— 这个缺口必须写进 mod 说明，并在第一版**单独跑验收**；
  ③ `TicksPerMove` Postfix 内**不得有任何分配**（不 `new`、不 LINQ），且必须 O(1) 判定（§4.5）。

---

## 4. 征召骑车

### 4.1 两个入口，两条完全不同的路

| | ⑤a 自发骑行 | ⑤b 征召骑行 |
|---|---|---|
| 入口 | 自己的 `ThinkTreeDef` + `<insertTag>Humanlike_PostMain</insertTag>`【原版XML确证】 | 小人身上的 `ThingComp.CompGetGizmosExtra()`【IL确证】 |
| 派发 | `JobGiver_RideSharedBike.TryGiveJob(pawn)` 返回 Job | `pawn.jobs.TryTakeOrderedJob(job, JobTag.DraftedOrder)` |
| 征召状态下可否 | 否（`Drafted` 时思考树走别的分支） | 是 |
| 需要 Harmony 吗 | **不需要**（一行 XML 搞定注入） | **不需要** |

### 4.2 Gizmo 入口的完整已确证链路

**【IL确证】链路是闭合的，不需要 Harmony：**

```
Verse.Pawn.GetGizmos()
  → Verse.Pawn::<>n__2()                      // Roslyn 的 base.GetGizmos() 桥方法
  → Verse.ThingWithComps.GetGizmos()           // 迭代器
  → foreach (comp in ThingWithComps.comps)
        comp.CompGetGizmosExtra()              // Verse.ThingComp，默认 Enumerable.Empty()
```

**做法**：`Patches/Patch_Human_Comps.xml` 用 `PatchOperationAdd` 往人类 ThingDef 的 `<comps>` 里塞我们的
`<li Class="YourMod.CompProperties_SharedBikeRider">`。
**人类 def 上已经有 `<comps>` 段**（`Races_Humanlike.xml:216-226`，`Human` 里有 `CompProperties_HoldingPlatformTarget` 等）【原版XML确证】。

**【原版XML确证 + IL确证】"只在被征召时显示 Gizmo"的三个条件**（照抄 `Verse.Pawn_InventoryTracker.GetGizmos` 状态机开头）：

```
pawn.IsColonistPlayerControlled
  && pawn.Drafted                                              // Verse.Pawn::get_Drafted
  && Find.Selector.SingleSelectedThing == pawn                 // RimWorld.Selector
```

**两个必须知道的注意点**【IL确证】：

| # | 注意点 | 纪律 |
|---|---|---|
| a | `Pawn.GetGizmos()` **每次调用都重跑** `CompGetGizmosExtra()` | **不要缓存 `Command_Action` 实例**（`disabled` 会残留）；每次 `new`，照抄原版做法 |
| b | `CompGetWornGizmosExtra()` 是**穿在身上的装备**用的（由 `Pawn_EquipmentTracker` / apparel 调） | 给 **pawn 本体**加按钮必须用 `CompGetGizmosExtra` |

**Gizmo 对象的真名**【IL确证】：`Verse.Command_Action`（`FIELD public System.Action action` / `onHover`；`base = Verse.Command`）；
`Verse.Command`（`defaultLabel` / `defaultDesc` / `icon` / `hotKey` / `groupKey` / `activateSound`…）；
`Verse.Gizmo`（`FIELD public static const float Height = 75`；`PROP bool Disabled { get; set; }`；`void Disable(string reason = null)`）。
⚠ **`Gizmo.Visible` 只有 getter** → 想隐藏 Gizmo 要**不 yield**，或置 `Disabled`。

### 4.3 点击后：`TryTakeOrderedJob`

```
Verse.AI.Pawn_JobTracker::TryTakeOrderedJob(Job job, Nullable<JobTag> tag = 0, bool requestQueueing = false)
```
【IL确证】**整段 IL 不检查 `Drafted`** → 这就是"征召状态下也能下 Job"的机制根源。
`Verse.AI.JobTag.DraftedOrder = 6`【IL确证】。

调用形状：
```csharp
pawn.jobs.TryTakeOrderedJob(JobMaker.MakeJob(SharedBikeJobDefOf.RideSharedBike, bike, destCell),
                            Verse.AI.JobTag.DraftedOrder);
```

### 4.4 ⚠ **`playerInterruptible = false` 是必设项，不是可选项**

**【IL确证】证据链**：
```
RimWorld.Pawn_DraftController::set_Drafted(bool)
  → jobs.ClearQueuedJobs()
  → if (jobs.curJob != null && jobs.IsCurrentJobPlayerInterruptible())  ← 闸门
         jobs.EndCurrentJob(...)

bool Verse.AI.Pawn_JobTracker::IsCurrentJobPlayerInterruptible()
  → curJob.def.playerInterruptible            ← JobDef 上的字段（false 就直接返回 false）
  → curDriver.PlayerInterruptable              ← JobDriver 的 virtual 属性
  → ThingDefOf.Fire 检查（着火也返回 false）
```

→ **`JobDef` 里写 `<playerInterruptible>false</playerInterruptible>`**，或覆写 `JobDriver.PlayerInterruptable => false`。
**否则按一下 D 就把骑行 Job 打断了**，人会站在原地、车还在原地——而且看起来像"随机失败"。

> ⚠ **不要与 `<suspendable>` 混**：原版 `TakeCountToInventory` 用的是 `<suspendable>false</suspendable>`（`Jobs_Misc.xml:599`）【原版XML确证】，**那是另一个字段**。

### 4.5 加速：**Harmony 补丁 1/3** —— Postfix 同时覆盖两种骑行，但必须自己判断

| 项 | 值 | 证据 |
|---|---|---|
| 唯一正规出口 | `Verse.Pawn::TicksPerMove(bool diagonal)` | 【IL确证】`v:A3i-Pawn-TicksPerMove-IL.txt` |
| 内部取值 | `StatExtension.GetStatValue(thing, StatDefOf.MoveSpeed, ...)` | 【IL确证】 |
| 两个属性转发 | `Pawn.TicksPerMoveCardinal` / `TicksPerMoveDiagonal` **都是属性，不是字段**，且都转发到 `TicksPerMove(bool)` | 【IL确证】 |
| 每 tick 现算 | 是 → "上车加速、下车恢复"天然成立，**无需任何状态重置** | 【IL确证】 |
| 上限 | `Pawn_PathFollower.MaxMoveTicks = 450` | 【IL确证】 |

```csharp
[HarmonyPatch(typeof(Verse.Pawn), nameof(Verse.Pawn.TicksPerMove))]
static class Patch_TicksPerMove {
    static void Postfix(Verse.Pawn __instance, bool diagonal, ref float __result) {
        // ★ 必须自己判断"是否在骑车"——原版征召战斗跑动走的是同一条出口
        var bike = __instance.Map?.GetComponent<MapComponent_SharedBikeRegistry>()?.BikeOf(__instance);
        if (bike == null) return;
        __result = Mathf.Max(1f, __result * __instance.def.race... /* 或直接给目标 ticks */);
    }
}
```

**代价必须写在设计里**：`Pawn.TicksPerMove` 是**全局出口**，原版征召跑动、追击、撤退**全部走它**。
所以：
- Postfix 里**只做一件事**：查"这个人是不是在骑车"，不是就 `return`（原值不变）；
- **判断必须 O(1)**：走 `MapComponent` 的字典查（§6.5），**不许在这里全图扫车**；
- ⚠ `TicksPerMove` 是**每 tick 每人每格**调用的热点，此 Postfix 不得有任何分配（不 `new`、不 LINQ）。

---

## 5. 停车与落点

### 5.1 两种停车模式 —— 都写，但**推荐 ①**

| | ① **乱停乱放 + 专人整理**（第一版做的） | ② **固定停在停放区**（不做，是 ① + 划 Zone 的自然结果） |
|---|---|---|
| 骑行结束做什么 | 把车丢在**小人脚下的合法格**（搜不到就放下车点最近的合法格）——**不查 Zone** | 必须先把车骑到**停放区内的格子**才能结束（**需要给骑车人加 Zone 查询**） |
| 复杂度落在哪 | **低频路径**：整理工每次出勤才处理 | **高频路径**：**每一次骑行的收尾**都要判断 + 可能失败。**① 完全不需要这套逻辑** |
| 结束失败怎么办 | 不会失败（总能就近放下） | 目的地没位子 → Job 卡住 / 车乱丢 / 需要"等位子"逻辑 |
| 战役后效应 | 战场散落的车**自然产生整理需求** → 新 WorkType 有价值 | 无（车都在区里，整理工没活） |
| 与用户想法的关系 | 用户设想 ① | 用户自己说"这个逻辑可能就更复杂一些" |

> **★ 关键：② 不是"另一个模式"，而是"① + 玩家划了 Zone"的效果。**
> 因为停放区**只对整理工下指令、不对骑车人设约束**（见 §1 的核心设计主张），所以：
> - 玩家**不划** Zone → 车一直乱停（①），整理工**什么都不做**；
> - 玩家**划了** Zone → 整理工开始把车搬进去 → **看起来就像车"固定停在停放区"**（②）。
>
> 于是**两套体验是同一个代码路径的两个结果**，而不是 `if (模式 == ②)` 的两段逻辑。这正是推荐 ① 的技术原因。

> ### ★ 推荐 ①，理由是**复杂度位置**而不是"简单"
> 把复杂度放在**低频的整理工作**里，而不是**高频的骑行结束路径**上。
> 再加上一条：**战役/战斗之后，车会自然散落在战场各处**——这恰好让"整理工"这个新工作类型**有真实、可持续的活源**。
> 若反过来选 ②，整理工几乎没有活，用户想做的"新工作类型"会变成一个空壳。

### 5.2 ★ 关键设计主张：**停放区不是对骑车人的约束，而是对整理工人的指令**

```
① 乱停乱放（骑车人）  ──┐
                        ├──→  停放区 = 整理工人的"投放点清单"
② 固定停放（若将来做）──┘
```

**三句可执行的规则（这就是全部）：**

| 角色 | 规则 |
|---|---|
| **骑车的小人** | **就地停**。只做**一次**合法落点检查（§5.4 的"投放"路径），**骑行 Job 里没有任何 Zone 查询** |
| **整理车的小人** | 把车从"不该停的地方"搬到**玩家划定的 `Zone_SharedBikePark`** |
| **玩家没划 Zone 时** | 整理工**什么都不做**（`WorkGiver_TidySharedBikes` 的第一句就是"找不到停放区 → 返回 null"）。<br>可选的例外：只清理**堵住通道**的车 —— ⚠ 但原版**没有**"堵住通道"的判定可复用（`Verse.PassabilityUtility` **不存在**），所以这个例外要自己写；**第一版不做** |

- 骑车人**完全不需要知道停放区存在** → ★ 正因为骑车路径里**没有** Zone 查询，"乱停乱放"与"固定停在区里"才**不冲突**：它们是**同一个代码路径 + 玩家有没有划区**的两个结果，而不是 `if (模式 == ②)` 的两段逻辑。
- 整理工的 `WorkGiver_TidySharedBikes`："收集**不在 `Zone_SharedBikePark` 内**的车 → 搬进区里。"
- **收益**：①的玩家体验（随手停）与②的管理诉求（整齐）**同时满足**，而代码里只有一条规则。

> 这条主张是【推断】，不是 IL 结论——但它把两份需求压成一条代码路径，是本文档最重要的架构决定（§1 的"核心设计主张"已把它提到架构总览）。

### 5.3 停放区形态：**已决定 —— 自定义 `Verse.Zone`**

> **状态：已拍板。** 用户原话选"自定义 Zone（推荐）"，理由是他最初的描述就是
> "**希望能像划定某种区域一样划定共享单车的储存区域**"。本节是**正式设计**，不再是待选方案。

**`Verse.Zone` 侧的已确证事实（逐字符取自真值表 §2.5 / §3）：**

| 项 | 事实 | 证据 |
|---|---|---|
| 抽象性 | `kind=class abstract`，`impl = IExposable, ISelectable, ILoadReferenceable, IRenameable, IHideable`；**唯一抽象成员 `protected abstract UnityEngine.Color NextZoneColor { get; }`**（`w:28-Zone-IL.txt` 全文只有这一处 `<abstract/extern - no IL>`） | 【IL确证】 |
| 真实状态 | `FIELD public List<Verse.IntVec3> cells`（+ `FIELD public int ID` / `UnityEngine.Color color` / `string label` / `Verse.ZoneManager zoneManager`） | 【IL确证】 |
| 可覆写 | `CreateMaterial` / `ExposeData` / `AddCell(IntVec3)` / `RemoveCell(IntVec3)` / `Delete(bool)` / `PostRegister` / `PostDeregister` / `GetInspectString` / `GetInspectTabs` / `GetGizmos` / `GetZoneAddGizmos` / `GetFloatMenuOptions(Pawn)` / `ToString` / `GetUniqueLoadID` | 【IL确证】 |
| 枚举区内 Thing | `PROP IEnumerable<Thing> AllContainedThings` / `int HeldThingsCount` / `int CellCount` / `List<IntVec3> Cells` / `Map Map` | 【IL确证】 |
| ⚠ **`Cells` 顺序** | `get_Cells`：`if (!cellsShuffled) GenList.Shuffle(cells)` → **顺序被随机化** | 【IL确证】 |
| ⚠ `AddCell` | 逐格检查 `def.CanOverlapZones`，不可重叠时报 `"Added zone over zone-incompatible thing ..."` 并**放弃该格**；成功则 `zoneManager.AddZoneGridCell` + `MapMeshDirty(..., MapMeshFlagDefOf.Zone)` + `AutoHomeAreaMaker.Notify_ZoneCellAdded` | 【IL确证】 |
| `RemoveCell` | 清空后**自动 `Deregister()`** | 【IL确证】 |

> ### ⚠ 由"`Cells` 随机化"推出的**硬性编码纪律**
> **任何依赖停放区内格子顺序的逻辑，都必须避开 `Zone.Cells` 属性，或自己维护一份排序后的副本。**
> 本项目受影响的地方有三处：
> 1. 整理工"往区里放车"时优先选哪一格 → **必须自己排序**（建议按 `(x, z)` 字典序），否则行为看起来随机；
> 2. "区里还有没有空位"的判定 → 不要用 `Cells[0]`/顺序语义；
> 3. 单元测试/存档往返断言 → **不要断言 `Cells` 的顺序**。
> （若需要"稳定枚举"，直接用 `FIELD public List<Verse.IntVec3> cells` 字段本身，**但要清楚它是引擎的内部状态**，更稳的做法是自建一份 `SortedList` 副本，在 `AddCell`/`RemoveCell` 的覆写里同步。）

**为什么不用 `Verse.Area`（这条要写清楚）**【IL确证】：

- **唯一状态载体是 `private Verse.BoolGrid innerGrid`**，`ICellBoolGiver` 只能回答"这格在不在区域内"；
- **没有 `cells` 列表**、**没有 `SlotGroup`**、**没有 `IHaulDestination`**、**没有"枚举区域内 Thing"的 API**（`Zone` 有 `AllContainedThings` / `HeldThingsCount`，`Area` 一个都没有）；
- `AreaManager` 是**固定枚举**（`Home` / `BuildRoof` / `NoRoof` / `PollutionClear` / `SnowOrSandClear`）+ 动态 `Area_Allowed`，且 **`FIELD public static const int MaxAllowedAreas = 10`**；`TryMakeNewAllowed(out Area_Allowed)` **只能造 `Area_Allowed`** → **mod 侧没有扩展点**；
- 且 **`Verse.AreaDef` 不存在**（子串扫描 0 命中）；
- `Area` 与 `Zone` **不共享基类**（都直接继承 `System.Object`），`Area` 靠 `BoolGrid`，**不能枚举"区域内的东西"**。

→ **结论：`Area` 连"这片区域里有哪些车"都答不出来，无法承载停放区的物品归属语义。不用。**

**为什么自定义 Zone 必须写 C#（而不是纯 XML）**【IL确证】：

- **`Verse.ZoneDef` 不存在**：`=Verse.ZoneDef` 精确查只有表头；子串 `"ZoneDef"` 扫描 **0 命中**；
  而同样方式的子串查 `"Designator_ZoneAdd"` **能命中 11 个类型** → **工具正常，是 `ZoneDef` 真的不存在**。
- 也就是说 **Zone 不是 Def 驱动的**，没有"写个 XML 就多一种 Zone"这条路；创建入口是**硬编码的 Designator + `ZoneManager`**。
- 代价是**一次性**的，而且**已经全部确证**，没有未知量。

**创建链（已确证）**【IL确证】：
```
Designator_ZoneAdd 子类 ctor：ldtoken Zone_X → stfld Designator_ZoneAdd::zoneTypeToPlace
Designator_ZoneAdd.DesignateMultiCell(...)：
    SelectedZone == null → MakeNewZone()  →  ZoneManager.RegisterZone(SelectedZone)
                        →  SelectedZone.AddCell(unsetCells[0]) → 逐格 AddCell
                        → 不连通时自动再开一个新 Zone
                        →  SelectedZone.CheckContiguous() → Finalize(createdNewZone)
ZoneManager：RegisterZone(newZone) → allZones.Add + newZone.PostRegister()
             DeregisterZone(oldZone) → allZones.Remove + oldZone.PostDeregister()
                                      + if (Find.Selector.SelectedZone == oldZone) Find.Selector.ClearSelection()
```

**进建造菜单：只需一个 `PatchOperationAdd`**【原版XML确证 + IL确证】

```xml
<Patch>
  <Operation Class="PatchOperationAdd">
    <xpath>/Defs/DesignationCategoryDef[defName="Zone"]/specialDesignatorClasses</xpath>
    <value><li>SharedBikeMod.Designator_ZoneAdd_SharedBikePark</li></value>
  </Operation>
</Patch>
```
- 原版清单位置：**`Core\Defs\Misc\Designations\DesignationCategories.xml:31-55`**（`<DefName>Zone</DefName>`、`order 800`、`specialDesignatorClasses` 列表里已有 `Designator_ZoneAddStockpile_Resources` / `Designator_ZoneAdd_Growing` / `Designator_ZoneAdd_Fishing` / `Designator_ZoneDelete` …）。
- 收集机制：`RimWorld.DesignationCategoryDef.ResolveDesignators()` 里 `foreach (Type t in specialDesignatorClasses) { d = (Designator)Activator.CreateInstance(t); d.isOrder = true; }`，调用时机 = `ResolveReferences()` → `LongEventHandler.ExecuteWhenFinished(ResolveDesignators)`（延迟到加载完成，因为要等 mod 的 `BuildableDef`）。
- `RimWorld.DesignationCategoryDefOf` 只有 `Floors` / `Production` / `Zone` 三个静态字段 → **patch 的 xpath 用 `defName="Zone"` 字符串匹配，不依赖 `DesignationCategoryDefOf`**。

**存档链（已确证，逐层）**【IL确证】：
```
Verse.Map::ExposeData
  → Scribe_Deep.Look(ref zoneManager, "zoneManager", ctorArgs=[this Map])
    → Verse.ZoneManager::ExposeData
      → Scribe_Collections.Look(ref allZones, "allZones", LookMode.Deep)   // Deep = 2
        → 每个元素的 Verse.Zone::ExposeData()（★ virtual，按运行时类型调用 → 你的覆写会被调到）
      → if (Scribe.mode == LoadingVars(2)) { UpdateZoneManagerLinks(); RebuildZoneGrid(); }
Verse.Zone::ExposeData（基类）：
  Scribe_Values.Look(ref ID,"ID",-1) / label / baseLabel / color / hidden
  + Scribe_Collections.Look(ref cells,"cells")
  + BackCompatibility.PostExposeData(this)
  → if (Scribe.mode == PostLoadInit(4)) CheckAddHaulDestination()
```
→ **纪律：`Zone_SharedBikePark.ExposeData()` 必须先 `base.ExposeData()`，再写自己的字段。**
（`LookSaveMode`：`Inactive=0, Saving=1, LoadingVars=2, ResolvingCrossRefs=3, PostLoadInit=4`【IL确证】）

**自定义 Zone 的最小实现（已确证的形状）**：
```csharp
public class Zone_SharedBikePark : Verse.Zone {
    // 唯一的抽象成员：protected abstract UnityEngine.Color NextZoneColor { get; }
    public Zone_SharedBikePark(Verse.ZoneManager zoneManager) : base("共享单车停放区", zoneManager) { }
    protected override UnityEngine.Color NextZoneColor => UnityEngine.Color.cyan;

    public override void ExposeData() {                 // virtual【IL确证】
        base.ExposeData();                              // ★ 必须先调基类
        // Scribe_Values.Look / Scribe_Collections.Look 自己的字段（若有）
    }

    // ⚠ Cells 顺序被 Shuffle：需要稳定顺序就自己维护一份副本
    public override void AddCell(Verse.IntVec3 c)    { base.AddCell(c);    /* 同步自己的有序副本 */ }
    public override void RemoveCell(Verse.IntVec3 c) { base.RemoveCell(c); /* 同步自己的有序副本 */ }
}

public class Designator_ZoneAdd_SharedBikePark : RimWorld.Designator_ZoneAdd {
    public Designator_ZoneAdd_SharedBikePark() { zoneTypeToPlace = typeof(Zone_SharedBikePark); }  // protected 字段【IL确证】
    protected override string NewZoneLabel => "共享单车停放区";                                    // abstract 属性【IL确证】
    protected override Verse.Zone MakeNewZone() =>                                                 // abstract 方法【IL确证】
        new Zone_SharedBikePark(Verse.Find.CurrentMap.zoneManager);
}
```

**整理工怎么读停放区（落地形状）**：
```csharp
// ① "这辆车在停放区里吗？"
Verse.Zone z = bike.Map.zoneManager.ZoneAt(bike.Position);       // ZoneManager.ZoneAt(IntVec3)【IL确证】
bool parked = z is Zone_SharedBikePark;

// ② "找一个区内的落点"（不要用 Cells 的顺序！）
var cells = zone.cells.OrderBy(c => c.x).ThenBy(c => c.z);        // 自己排序（§6.5 确定性规则）
```

### 5.3.1 备选方案 / 未来扩展：**停车桩**（不作并列选项）

> **第一版不做。** 这里只记录它是**可叠加的扩展**，以及做它的已确证事实，避免将来重新取证。

| 项 | 事实 |
|---|---|
| 形态 | 一个 1×1 `Verse.ThingDef` + 自定义 `Verse.ThingComp` |
| 白送项 | `RimWorld.CompAssignableToPawn` 提供 `assignedPawns` / `maxAssignedPawnsCount` / `HasFreeSlot` / `TotalSlots` / `CanAssignTo` + 指认对话框 + `PostExposeData` 存档；原版 `Grave` / `Sarcophagus` / `MeditationSpot` / `DeathrestCasket` 都是这么做的【IL确证 + 原版XML确证】 |
| ⚠ **语义不匹配** | `RimWorld.CompAssignableToPawn.assignedPawns` 装的是 **`Verse.Pawn`**，不是 **`Verse.Thing`** → "**自行车**归属某个停车桩"**不能**用它，仍要自写 `Verse.ThingComp`。<br>✅ **2026-10-13 实测更正**："要存"车"引用"这条路**是通的**：**`Verse.Thing` 确实实现 `Verse.ILoadReferenceable`**（`InterfaceImpl` 原始元数据 + `GetUniqueLoadID()` = `"Thing_" + ThingID` + 全程序集 **80 个**原版 `Verse.Scribe_References.Look<Verse.Thing>` 调用点）⇒ **直接写 `Verse.Scribe_References.Look<Verse.Thing>(ref bike, "bike")` 即可**（见 §3.4 更正 A；真值表 §2.8） |
| 进菜单 | 不用 patch：ThingDef 会自动生成 `Designator_Build` |
| 与 Zone 的关系 | **叠加**而非替代：`Zone_SharedBikePark` 管"哪一片区域算停放位"，桩管"精确到某一格的落点/外观"。整理工的落点选择可以优先挑"有桩的空位" |

### 5.4 落点合法性：**两条路径用两个不同的判定**（写混了就白送 UI 就没了）

| 路径 | 该用哪个 | 证据 | 为什么 |
|---|---|---|---|
| **投放 / 直接在地上生成一辆车**（还车、整理工放下车 / 从 `carryTracker` 摘车落地） | ✅ **已确证**：`Verse.GenSpawn.CanSpawnAt(Verse.ThingDef thingDef, Verse.IntVec3 c, Verse.Map map, Nullable<Verse.Rot4> rot = null, bool canWipeEdifices = true)`（`public static`，**不需要 Harmony**） | 布局取证 `tmp\il-evidence-bike\placement\README.md` §1.1 / §6-④（真值表 §2.4 尚未收录该条 → **本行以取证报告为准**） | 语义正好是"把这类东西生成到这格"；覆盖边界 / `GenGrid.Walkable` / 既有建筑 / 无法抹掉的阻挡物 / 地形可达 / 交互格 |
| **蓝图安装（原版部署链）** | `RimWorld.GenConstruct.CanPlaceBlueprintAt_NewTemp(BuildableDef entDef, IntVec3 center, Rot4 rot, Map map, bool godMode = false, Thing thingToIgnore = null, Thing thing = null, ThingDef stuffDef = null, bool ignoreEdgeArea = false, bool ignoreInteractionSpots = false, bool ignoreClearableFreeBuildings = false, Predicate<Thing> skipBlockingThing = null)` | 【IL确证】真值表 §2.4 | `Designator_Install.CanDesignateCell` 就是调它【IL确证】 |
| 追加自定义规则 | `Verse.PlaceWorker.AllowsPlacing(BuildableDef checkingDef, IntVec3 loc, Rot4 rot, Map map, Thing thingToIgnore = null, Thing thing = null)` → `Verse.AcceptanceReport` | 【IL确证】真值表 §2.4 | `CanPlaceBlueprintAt_NewTemp` 的判据链**最后一步**就是 `BuildableDef.get_PlaceWorkers` → `PlaceWorker.AllowsPlacing`【IL确证】 |

> **★ 后者的白送项**：`CanPlaceBlueprintAt_NewTemp` 是通过 `Designator_Install.CanDesignateCell` 直接拿到 `AcceptanceReport` 的，
> 所以**红/绿 ghost、不能放置的原因提示（`"OutOfBounds"` / `"TooCloseToMapEdge"` / `"CannotPlaceInUndiscovered"`）全部免费**【IL确证】。
> **不要自己写格子检查**——只要把 `<placeWorkers>` 挂在内层 Bike 建筑 def 上，就同时管住了 UI 和实际放置。
>
> ⚠ **反例纪律**：**不要**用 `CanPlaceBlueprintAt` 做"往地上丢一个小东西"的判定——它的语义是"放一张建筑蓝图"，
> 会额外拿**地图边缘、迷雾、交互格**说事，对"还车"偏重。

### 5.5 "车停在哪"的三条硬约束

| # | 约束 | 事实（已确证） | 设计对策 |
|---|---|---|---|
| **H1** | 还车要搜合法格 | 原版"放下手里的东西"用的是 `ThingPlaceMode.Direct`，**失败就整个 Job `Incompletable`**（`Verse.AI.Toils_Haul.DropCarriedThing`）；**原版自己不做"就近找合法格"** | **不能抄 `DropCarriedThing`**（会硬失败/销毁）。**两个都可用，且都已确证**：① `Verse.GenPlace.TryPlaceThing(..., ThingPlaceMode.Near` / `Radius`, ...)`（内部就是 `Verse.GenRadial.RadialPattern` + `PlaceSpotQuality` 打分 + "放不下就放旁边"）；② `Verse.CellFinder.FindNoWipeSpawnLocNear(Verse.IntVec3 near, Verse.Map map, Verse.ThingDef thingToSpawn, Verse.Rot4 rot, int maxDist = 2, Predicate<Verse.IntVec3> extraValidator = null)`（确定性、由近及远）。<br>**更省事的整包入口 = `Verse.GenDrop.TryDropSpawn(...)`**（8 参，内部调 `GenPlace.TryPlaceThing` + 播放 `soundDrop`）<br>证据：`tmp\il-evidence-bike\placement\README.md` §0 / §2.1 / §3.5（真值表 §6.3.1-A14 已把 `CellFinder::FindNoWipeSpawnLocNear` 列为**已确证的 `RadialPattern` 读者**） |
| **H2** | 目的地会形成车的堆积 | 所有人骑到同一个仓库 → 一地是车 | 这是**设计上的正常现象**，不是 bug：堆积 = 整理工的活源（§5.1）。**不要试图自动分散** |
| **H3** | 车是可通行 Thing，会被**直接踩过去** | `Verse.AI.PathGrid.CalculatedCostAt` **只对 `passability == Impassable(2)`** 判 `10000`（`ImpassableCost = 10000` 是哨兵值）；`PassThroughOnly(1)` / `Standable(0)` 只是加一点 `pathCost`（`Verse.BuildableDef::pathCost`），**不阻挡** | **★ 唯一的设计对策：把车的 `passability` 设成 `Standable`(0) 或 `PassThroughOnly`(1)，就是不能设 `Impassable`(2) —— "车不堵路"就这一条。**<br>⚠ 原版**没有**"堵住通道"的判定可复用（`Verse.PassabilityUtility` / `Verse.WillCollideWithPawnAt` **不存在**；「堵住唯一通路」检查【未确证存在】）⇒ 只能靠"不挡路"。<br>证据：布局取证 `tmp\il-evidence-bike\placement\README.md` §4.1 / §4.2 / §6-④ |

⚠ **H3 的展开（这条最容易写错，单独说清）**：
- `Verse.GenGrid.Standable(c, map)` 的判据是 **`该格没有任何 def.passability != Standable(0) 的 Thing`**；而 `Verse.AI.PathGrid` 的判据是 **`passability != Impassable(2)`** —— **两者不等价**：`PassThroughOnly`(1) 的 Thing 会让 `GenGrid.Standable` 为 **false**，但**不挡路**。
- ⚠ **反例（原 §5.5-H3 的旧写法，已实测更正）**：旧写法要求"把车的 `passability` 设成 `Standable`"，并同时声称"这样车就可以被直接踩过去"。**这两句不能同时成立**：`passability == Standable(0)` 的 Thing 会让该格的 `GenGrid.Standable` 判为 **false**。**所以选 `PassThroughOnly`(1) 语义更准确**（"能穿过去、但格子不算可站立"）；选 `Standable`(0) 也不会堵路，但要让其它 `Standable` 语义的读者不误判就得自己承担。（真值表**没有**收录 `Verse.GenGrid` 一族的判定结论 → 本节以布局取证报告 `tmp\il-evidence-bike\placement\README.md` §4.1 / §4.2 为准。）
- 另外 `Verse.ThingDef.fillPercent`（属性 `Fillage`）= `Full` 会让**落点质量**降为 `Bad`，但**不影响寻路**。

> ### 🆕 2026-10-13 实测补充：环状扫描要**复用**原版，**不要自写螺旋**
>
> - **`Verse.GenRadial::RadialCellsAround(IntVec3 center, float radius, bool useCenter)`**（`public static IEnumerable<Verse.IntVec3>`）+ 重载 **`RadialCellsAround(IntVec3 center, float minRadius, float maxRadius)`** ⇒ ✅ **"由近及远枚举格子"的正规入口，直接调用**。
> - **`Verse.GenRadial::RadialPattern` 是 `public static Verse.IntVec3[]` 字段，不是方法**（❌ 别写成 `RadialPattern(...)`）。兄弟：`ManualRadialPattern` / `RadialPatternRadii`（`public static float[]`）、常量 `RadialPatternCount = 20000` / `MAX_RADIUS = 80`；集合类入口 `RadialPatternInRadius(float radius)`。
> - **`RadialPattern` 有 116 个读者 / 107 个调用者**——"环状扫描"是原版落点选择的主力；读者里直接含 **`Verse.CellFinder::FindNoWipeSpawnLocNear`**、`Verse.CellFinder::StandableCellNear`、**`Verse.GenPlace::TryFindPlaceSpotNear`** 等（真值表 §2.8「`RadialPattern` 的读者面」）。
> - ⚠ **它与 `GenDrop` / `GenRadial` 全家都是 `static` ⇒ 不可覆写**（真值表 §3.7b）。
> - **三条纪律**：① §6.5 的"由近及远查询最近的车"直接用 `RadialCellsAround`，**不要自写螺旋**；② 想"改"这套扫描**没有虚挂点** ⇒ 只能改调用点或 Harmony，**本项目不改**；③ 直接改 `RadialPattern` 数组的值会**影响全程序集所有 116 个读者** ⇒ **绝对不要动它**。

### 5.6 ★ 移动一辆**已存在**的车的正确做法

**方案 ①：`thing.Position = cell`（最轻）**

【IL确证】`Verse.Thing.set_Position(IntVec3)` 会**自动做 6 件事**：
```
1  DirtyMapMesh(Map)
2  RegionListersUpdater.DeregisterInRegions(this, Map)   / 之后 RegisterInRegions
3  Map.thingGrid.Deregister(this)                          / 之后 Register
4  Map.coverGrid.DeRegister(this)                           / 之后 Register
5  Map.gasGrid.Notify_ThingSpawned(this)
6  Map.reachability.ClearCache()  （当 def.AffectsReachability）
```
**它不会做的事**：**重算寻路代价**（`set_Position` 里**没有** `Pathing.RecalculatePerceivedPathCost*` 调用——用"扫全程序集 90840 个 MethodDef 的 IL、匹配 call/callvirt token"独立验证过，`Thing.set_Position` 不在 16 个调用点名单里）。

→ **必须自己补两次**（`public`、幂等）：
```csharp
map.pathing.RecalculatePerceivedPathCostAt(oldCell);   // Verse.AI.Pathing::RecalculatePerceivedPathCostAt(IntVec3)【IL确证】
map.pathing.RecalculatePerceivedPathCostAt(newCell);
```
（若车的 `passability == Standable` 且 `pathCost == 0`，不补也无影响；但**别依赖这一点**——将来把车改成 `Impassable` 就会静默出错。）

⚠ **两条红线**【IL确证】：
- `def.AffectsRegions == true` 的 Thing **不能这么搬**（`set_Position` 明确只打 warning 且"不支持"）；
- **绝对不要用 `Thing.SetPositionDirect(IntVec3)`** —— 它的 IL 全文只有 `ldarg.0; ldarg.1; stfld positionInt; ret`：**只改字段、零登记**，会直接破坏 thingGrid / coverGrid / region。

**方案 ②：`DeSpawn` + `GenSpawn.Spawn`（原版先例，推荐）**

```
thing.DeSpawn(DestroyMode.Vanish);
thing.Position = newCell;                       // 或交给 Spawn 定位
GenSpawn.Spawn(thing, newCell, map, thing.Rotation, WipeMode.Vanish);
```
- 【IL确证】**自带两处** `Pathing.RecalculatePerceivedPathCostUnderThing`（`Thing.SpawnSetup` 与 `Thing.DeSpawn` 各一次）；
- 【IL确证】**原版没有任何"改 Position 搬一个已生成 Thing"的业务先例**；原版等价操作一律是 `DeSpawn + SpawnSetup`；
- 代价：重量级（`SpawnSetup` 26 步、`DeSpawn` ~20 步）。

> ### ★ 推荐：**方案 ②**（`DeSpawn` + `GenSpawn.Spawn`）
> 理由：它**自带寻路代价重算**、与原版 `SpawnSetup`/`DeSpawn` 的登记链**完全对齐**（含 `map.events.Notify_ThingSpawned/Despawned` 广播、
> `listerHaulables` / `listerMergeables` / tickability / slot group），而车的位置移动是**低频**操作（只在下车/整理两个瞬时点），
> 性能代价可以忽略。
> **方案 ① 留给"高频小位移"的场景**——但本项目骑行期间车在 `carryTracker` 里（§3.2），**骑行期间根本不移动地图上的车**，于是没有高频场景。

---

## 6. 整理工作（WorkType + WorkGiver）

### 6.1 新 `WorkTypeDef`：**纯 XML，零 C# 注册**【IL确证】

**加载器侧无白名单**：`Verse.DefDatabase<T>.AddAllInMods` 遍历所有 mod 的 `Defs` 目录 → **`WorkTypeDef` 和 `ThingDef` 走同一套收集器**。

```xml
<WorkTypeDef>
  <defName>SharedBikeTidying</defName>       <!-- ★ 必须带自己的前缀 -->
  <labelShort>tidy bikes</labelShort>        <!-- Work 页列头用这个 -->
  <pawnLabel>Bike tidier</pawnLabel>
  <gerundLabel>tidying bikes</gerundLabel>
  <verb>tidy</verb>
  <naturalPriority>320</naturalPriority>     <!-- ★ 必须 0..10000 -->
  <workTags>
    <li>ManualDumb</li><li>Hauling</li><li>Commoner</li><li>AllWork</li>
  </workTags>
  <alwaysStartActive>false</alwaysStartActive>
  <visible>true</visible>
</WorkTypeDef>
```

**三个坑（都是 IL确证，必须写进代码 review 清单）：**

| # | 坑 | 事实 | 对策 |
|---|---|---|---|
| K1 | `naturalPriority` 必须 **0..10000** | `ConfigErrors` 打 `"naturalPriority is N, but it must be between 0 and 10000"`；`WorkTypeDef` ctor 不赋默认值（=0） | 取值落在原版梯度里（`Hauling` = 300、`Cleaning` = 200、`Fishing` = 350）→ 建议 **320** |
| K2 | `defName` **跨 mod 重名** / **同 mod 内重名** | **跨 mod**：`DefDatabase<T>.Add` 里 `while(defsByName.ContainsKey(def.defName)) { Log.Error("Adding duplicate " + ...); def.defName += Rand.Range(1,100000); }` → **改名 + 报红字**，于是 `WorkGiverDef.workType` 的引用（按 defName 解析）可能**对不上**。<br>**同 mod 内**：`usedNames` **每个 mod 开头 `Clear()`**，`Add` 失败则 `Log.Error("Mod X has multiple T s named Y. Skipping.")` 并**静默丢弃该 def** | **`defName` 一律加 mod 前缀** |
| K3 | **不标 `alwaysStartActive` 会参与"新殖民地默认激活工作"的竞争** | `RimWorld.Pawn_WorkSettings`（**已确证的**）：`DefaultPriority = 3`、`LowestPriority = 4`、`FIELD private Verse.DefMap<Verse.WorkTypeDef,int> priorities`；`EnableAndInitialize` 第一轮按技能均值降序给未激活的 WorkType 设 `SetPriority(w, 3)`，第二轮才是全部 `alwaysStartActive`（真值表 §2.2）。<br>⚠ **"第一轮只给前 6 个"里的常量名 `MaxInitialActiveWorks` 与数值 `6` —— 未确证**：该名字在真值表里 **0 命中**，真值表 §2.2 只列了 `DefaultPriority` / `LowestPriority`。**旧版本文档曾把这个数值标成"取自真值表 §1.3 / §2.2"，那个出处是假的，已删除。** | 若 **不填 `relevantSkills`**（空列表），`AverageOfRelevantSkillsFor` 的返回**未确证**（真值表 §5 待确证第 9 项）→ 可能默认关着，**玩家在新殖民地找不到这个工作**。<br>**对策（第一版）**：不写 `relevantSkills`，但**在 mod 描述/说明里写清"工作默认可能未开启，请到 Work 页勾上"**；或后续实测后决定是否 `alwaysStartActive`。<br>⚠ **不要在任何地方引用 `MaxInitialActiveWorks` 这个名字**（未确证） |

**⚠ 另一条已确证、但要主动用上的机制**：
- `RimWorld.WorkTypeDefOf` **只是便利缓存，不是注册表**（原版 23 个 WorkType 只登记了 17 个，`Patient`/`PatientBedRest`/`BasicWorker`/`Cooking`/`Tailoring`/`Art` 都不在里面也照跑）。
  → **新增 WorkType 完全不需要碰它。** 只有当**自己的 C# 要写 `WorkTypeDefOf.X`** 时，才自己写一个 `[DefOf]` 静态类。
- `Pawn_WorkSettings.priorities` 是 `Verse.DefMap<WorkTypeDef,int>`，索引器按 `Verse.Def::index`（加载期由 `DefDatabase<T>.Add` 自动赋值）；
  `DefMap.ExposeData` 在 `LoadingVars` 时按 `DefDatabase<D>.DefCount` **自动补齐/裁剪** → **给已有存档加新 WorkType 也会得到默认值**。
- **`workGiversByPriority` 不要写进 XML**（`ResolveReferences` 填的）。

### 6.2 `WorkGiver` 骨架

**【IL确证】命名空间纠正（这条最容易写错）**：
`RimWorld.WorkGiver` / `RimWorld.WorkGiver_Scanner` —— **不是** `Verse.AI.WorkGiver_Scanner`。
（`Verse.AI` 下 200 个 WorkGiver 类型**一个都没有**。）

**直接抄 `RimWorld.WorkGiver_Haul` + `RimWorld.WorkGiver_HaulGeneral`**【IL确证】：

| 覆写点 | `WorkGiver_Haul` 的做法 |
|---|---|
| `JobOnThing(Pawn, Thing, bool)` | 产出搬运 Job |
| `MaxPathDanger(Pawn)` | `ldc.i4.3` = **`Verse.Danger.Deadly`** |
| `PotentialWorkThingsGlobal(Pawn)` | `pawn.Map.listerHaulables.ThingsPotentiallyNeedingHauling()` ← **我们换成"乱停的车集合"** |
| `ShouldSkip(Pawn, bool)` | 集合为空则跳过 |

**`WorkGiver_Scanner` 的默认值（别搞错）**【IL确证】：
`AllowUnreachable = false`、`MaxRegionsToScanBeforeGlobalSearch = -1`、`PathEndMode = Touch(2)`、
`PotentialWorkThingRequest = ForGroup(0)`、`Prioritized = false`、`GetPriority(...) = 0`、
`GetReservationLayer(...) = null`、`JobOnThing(...) = null`、`JobOnCell(...) = null`、
`PostProcessedGerund(Job) = def.gerund`、`PotentialWorkCellsGlobal(...) = Enumerable.Empty<IntVec3>()`、
**`PotentialWorkThingsGlobal(...)` 默认 `ldnull`**（走半径/区域扫描）。

⚠ **`RimWorld.WorkGiver` 上没有任何名为 `Priority` 的成员**【IL确证】：
- Def 侧 → `RimWorld.WorkGiverDef.priorityInType`（`int`）
- Scanner 侧 → `RimWorld.WorkGiver_Scanner.GetPriority(Pawn, TargetInfo|IntVec3)`（`float`，默认 0）

**★ `WorkGiver_TidySharedBikes` 的第一句必须是"有没有停放区"（落实 §5.2）**：

```csharp
public override bool ShouldSkip(Verse.Pawn pawn, bool forced = false) {
    // 落实"没划 Zone → 整理工什么都不做"
    var map = pawn.Map;
    return !map.zoneManager.AllZones.Any(z => z is Zone_SharedBikePark);   // ZoneManager.AllZones【IL确证】
}
```
- 这一句同时是**性能闸门**：没划区时整个 WorkGiver 直接短路，**连车都不用枚举**。
- ⚠ `ZoneManager.AllZones` 是 `PROP List<Verse.Zone>`（**可读**）【IL确证】；`Zone` 侧**不要**用 `Cells` 判断顺序（§5.3 的纪律）。
- "这辆车在不在停放区里"用 `map.zoneManager.ZoneAt(cell)` 判 + `is Zone_SharedBikePark`【IL确证】。

**`WorkGiverDef` 的必填与默认值**【IL确证】：
`giverClass`（`System.Type`，XML 写短类名）、`workType`（`Verse.WorkTypeDef`，`ResolveReferences` 用**引用相等 `ceq`** 匹配）、
**`verb` / `gerund` 缺了直接 `ConfigErrors` 红字**（IL 里 `ldstr` 只有 `" lacks a verb."` / `" lacks a gerund."`）、
`priorityInType`（`int`，同 WorkType 内**降序**）、`scanThings`（ctor 默认 **true**）、`scanCells`（ctor 默认 **false**，且全程序集**无写入点**）、
`directOrderable`（默认 true）、`canBeDoneByMechs`（默认 true）、`tagToGive`（默认 1）、`autoTakeablePriorityDrafted`（默认 −1）、`emergency`、`prioritizeSustains`、`requiredCapacities`、`workTags`…
❌ **不存在**：`fixedWeights`、`prioritizeSkipped`。

**原版 `Hauling` 组内的 `priorityInType` 梯度（照抄模板）**【原版XML确证】：
`EmptyEggBox=20`、`Refuel=19`、**`HaulGeneral=15`**、`DeliverResourcesToFrames=10`、`DeliverResourcesToBlueprints=9`、`HaulMerge=5`。
→ 我们**挂在自己的新 WorkType 上**（不是 `Hauling`），所以**完全没有冲突**，第一版取 `priorityInType = 10`。

**收集是自动的**【IL确证】：`Verse.WorkTypeDef::ResolveReferences()` =
```
DefDatabase<WorkGiverDef>.AllDefs
  .Where(d => d.workType == this)                    // ceq 引用相等
  .OrderByDescending(d => d.priorityInType)
  → 逐个 workGiversByPriority.Add(d)
```
**唯一写入者就是它**，mod 侧**零登记**。

⚠ **一个连带风险**：`OrderByDescending` 在 `priorityInType` / `naturalPriority` **相等时的稳定性**只能算**【部分确证】**
（IL 只看到 `Enumerable.OrderByDescending` 调用；"同值按加载序"是 LINQ 契约**推论**，不是 IL 可见事实）。
→ 若同值顺序对你的功能有承重作用，**改用显式 tie-break**（别依赖）。

### 6.3 整理 Job 的形状

参考已确证的原版件：

| 环节 | 用什么 | 证据 |
|---|---|---|
| 取货进背包 | `Verse.AI.JobDriver_TakeInventory`（`RimWorld.JobDefOf.TakeInventory`）/ `RimWorld.JobDriver_TakeCountToInventory`（`JobDefOf.TakeCountToInventory`） | 【IL确证】`Jobs_Misc.xml:272-277` / `:595-608`【原版XML确证】 |
| 进背包的唯一真实入口 | `Verse.AI.Toils_Haul::TakeToInventory(...)`（4 个重载）→ 内部闭包 `ErrorCheckForCarry` → `Job.checkEncumbrance` → `MassUtility.CountToPickUpUntilOverEncumbered` → `Math.Min` → `Thing.SplitOff(count)` → `ThingOwner.TryAdd(..., canMergeWithExistingStacks: true)`；**中间没有任何 def 过滤** | 【IL确证】 |
| 走到停放区放下 | `Verse.AI.Toils_Haul::PlaceHauledThingInCell(TargetIndex cellInd, Toil nextToilOnPlaceFailOrIncomplete, bool storageMode, bool tryStoreInSameStorageIfSpotCantHoldWholeStack = false)` | 【IL确证】 |
| 落点 | ⚠ **不要抄 `Toils_Haul.DropCarriedThing`**（它用 `ThingPlaceMode.Direct`，失败就 `Incompletable`，**不找旁边**）→ 用 `Verse.GenPlace.TryPlaceThing`（`ThingPlaceMode.Near` / `Radius`）或 `Verse.GenDrop.TryDropSpawn`（**均已确证**，见 §5.4 / §5.5-H1） | 【IL确证】+ 布局取证 `tmp\il-evidence-bike\placement\README.md` |
| 枚举全图的车 | **`Verse.ThingRequestGroup.MinifiedThing = 39`**（原版专门给微型化物品留的组） | 【IL确证】 |
| 预约一辆车 | `Verse.AI.ReservationUtility::CanReserve(Pawn p, LocalTargetInfo target, PathEndMode peMode, Danger maxDanger, int maxPawns, int stackCount, ReservationLayerDef layer, bool ignoreOther)`；参照 `InstallJob` 的实际调用 `CanReserve(pawn, miniThing, 1, -1, null, false)` | 【IL确证】 |
| 共享语义 | **`ReservationUtility.Reserve(..., maxPawns: 1, ...)`** → 一人一辆；**预约绑定 Job 生命周期，Job 一结束框架自动释放** | 【IL确证】 |
| ❌ 不要用 | `RimWorld.CompAssignableToPawn`（那是**玩家指派私有物**，语义与"共享"正好相反；且它的 `assignedPawns` 装 `Pawn` 不装 `Thing`） | 【IL确证】 |

### 6.4 携带多辆车：**用背包，不用手抱**

**【IL确证】为什么必须用背包：**

| 容器 | 能力 | 结论 |
|---|---|---|
| `Verse.Pawn_CarryTracker`（手抱） | `innerContainer` 也是 `ThingOwner<Thing>`，但 `AvailableStackSpace(ThingDef)` / `MaxStackSpaceEver(ThingDef)` **都按 `ThingDef` 算** → 机制上"**同一个 ThingDef 的一个栈**" | ❌ **不能同时抱 3 辆不同 def 的车**（除非同 def 且合并成一栈） |
| **`Verse.Pawn_InventoryTracker`（背包）** | `FIELD public ThingOwner<Verse.Thing> innerContainer`；ctor 里 `new ThingOwner<Thing>(this, oneStackOnly: false, contentsLookMode: 2(Deep), removeContentsIfDestroyed: true)`，`ThingOwner::InfMaxStacks = 999999`；`ThingOwner\`1::GetCountCanAccept` 的**唯一门槛是 `maxStacks`（999999）**，`T` 检查对 `ThingOwner<Thing>` 恒真 | ✅ **能混装不同 def** |

**容量真值**【IL确证】：
```
RimWorld.MassUtility.Capacity(Pawn p) = p.BodySize × 35        (MassCapacityPerBodySize = 35)
```
> ⚠ **关键纠正**：`Capacity` 的**函数体内没有任何 `ldsfld RimWorld.StatDefOf::*` 指令**（逐条读 IL 确证）
> ⇒ **它不读 `StatDefOf.CarryingCapacity`**。
> `RimWorld.StatDefOf.CarryingCapacity` **存在**，但全程序集只有 **3 个读者**（`Pawn_CarryTracker.MaxStackSpaceEver`、
> `HaulAIUtility.HaulToCellStorageJob`、`JobGiver_Steal.TryGiveJob`）——那是"**手/嘴能拿多少**"，**不是背包质量上限**。
> ⚠ 真值表 §3.5 明确记录了"0 个读者"的旧说法**已被同一轮的证据文件推翻**（3 hits），**不要再用 0**。

**"车的质量定多少 → 一次能带几辆"对照表**（`BodySize = 1.0` 的人类 → **35 kg**）：

| 车的 `Mass`（kg） | 一次能带几辆 | 评价 |
|---|---|---|
| **20** | **1** | 太少，"整理工"没意义 |
| **12** | **2** | 偏少 |
| **8** | **4** | ✅ **推荐区间** |
| **7** | **5** | ✅ 推荐（刚好是"现实三轮车拉一堆"的感觉） |
| **5** | **7** | 上限感偏松 |
| **35** | **1** | ❌ 千万别（正好一件就满） |

> **推荐：车 `Mass = 7`（钢铁单车）→ 一个 1.0 BodySize 的殖民者一次带 5 辆。**
> 想让整理工带更多：原版正路是**给他提高 `BodySize` 的途径**（基因 / hediff / `StatDefOf.BodySize` 的 statOffset），
> 或者**在自己 Job 的 `checkEncumbrance = false` 上做手脚**——⚠ 但那是**你自己的 Job 路径**，原版搬运 Job 的 `checkEncumbrance` 由派发者决定。
> ⚠ **`Verse.AI.Job.checkEncumbrance` 在哪些原版派发点被置 true，未确证**（真值表 §5 待确证第 14 项）。

**"一次塞 N 辆"要自己写调度**【IL确证 + 推断】：
- 原版安装 job 是**单件**：`InstallJob` 里 `j.count = 1`、`j.targetB = <单个 Blueprint_Install>`，**且没有设 `targetQueueB`**；
- `Toils_Haul.JumpIfAlsoCollectingNextTargetInQueue` 依赖 `job.targetQueueB`，**对安装 job 是空转**；
- 原版确有"多件齐搬"机制（`WorkGiver_ConstructDeliverResources` 的 `MultiPickupRadius = 5` / `NearbyConstructScanRadius = 8`），但**只服务建筑材料**；
- → **"一个工人一次抱好几辆车"原版没有实现**（真值表 §5 待确证第 13 项），必须自写 Job/WorkGiver 的循环：
  ```
  重复 {
      找最近的一辆乱停车（§6.5）
      if (它在停放区内) break;
      if (MassUtility.FreeSpace(pawn) < bike.GetStatValue(StatDefOf.Mass)) break;   // RimWorld.StatDefOf.Mass 存在【IL确证】
      Job: TakeInventory(bike) → 进背包
  } while (背包没满)
  → 走到停放区 → 逐辆放下
  ```

### 6.5 **性能**：禁止每 tick 全图找车；必须有确定性规则

**红线**：`O(小人 × 车)` 的"每 tick 全图找最近的车"在 20 人 + 100 车时是 2000 次/扫描，且**会随玩家存档规模恶化**。

**设计（MapComponent + 空间分桶 + 脏标记）**：

| 组件 | 职责 |
|---|---|
| `MapComponent_SharedBikeRegistry` | 一个 `Dictionary<long, Dictionary<int, List<Thing>>>`；外层 key = `map.uniqueID`（其实 MapComponent 本身就是 per-map），内层 key = **区块索引** `(c.x >> 4) * 64 + (c.z >> 4)`（16×16 格一桶） |
| 每个 `CompSharedBike` | `PostSpawnSetup` / `PostDeSpawn` / 位置变化时**通知** Registry 更新它所在的桶 |
| **脏标记** | 桶只在"车被 spawn / despawn / 换桶 / 预约状态变化"时改；**查询不清空** |
| 查询"离我最近的一辆闲车" | **只扫 `GenRadial` 由近及远**（`Verse.GenRadial.RadialCellsAround`，只提供按距离排序的格子枚举、不含合法性）→ 逐桶短路：首个命中的桶内取最近，立刻停 |
| **熔断** | 一次扫描最多 8 个桶 / 最多 64 辆车，超了就放弃（返回 null） |

**★ 确定性规则（"先收哪辆"必须是确定的，否则行为看起来随机）**：

```
排序键 = ( 到小人的距离（GenRadial 序，天然确定）,
           车的 thingIDNumber（升序） )     ← Thing 的 ID 是生成时分配的，稳定且确定
```
→ **不许用"列表顺序"**（那取决于 spawn 顺序/桶内插入顺序，跨存档不稳定）。
→ 同理，`Zone.Cells` 的顺序是**随机化过的**（`get_Cells` 里 `GenList.Shuffle(cells)`【IL确证】）——**任何依赖停放区内格子顺序的逻辑都必须自己排序**（建议按 `(x, z)` 字典序）。

> **三级缓存层次**（从便宜到贵）：① `Comp` 上直接读"这辆车有没有主"（O(1)）；② Registry 的桶（O(桶大小)）；
> ③ **只在 `WorkGiver` 里做一次全图枚举**（`ThingRequestGroup.MinifiedThing`，低频）——**绝不在 `TicksPerMove` Postfix 里做 ② 以上**。

---

## 7. 承重数字（平衡设计）

> **本节所有数字都从已确证常量推出**【IL确证 + 原版XML确证】。**复核算式**写在每一行下面。

### 7.1 基础真值

| 量 | 值 | 算式 / 来源 |
|---|---|---|
| `MoveSpeed` 的 `defaultBaseValue` | **3.0** | 原版 XML |
| `MoveSpeed` 的单位 | **格/秒** | 由下面的 clamp 推出 |
| 每格 tick 数 | `ticks = Clamp(1/(v/60), 1, 450)` | `Verse.AI.Pawn_PathFollower::MaxMoveTicks = 450`【IL确证】 |
| **复核算式** | `v=3.0` → `1/(3/60) = 20` tick/格 | ✅ 与"默认 20 tick/格"一致 |
| 上限 | `v` 再高，`ticks` 也不小于 **1** → **有效车速上限 = 60 格/秒** | `Clamp(...,1,450)` 的下界 1 |
| 下限 | `ticks` 最大 450 → **最低速 = 1/450×60 ≈ 0.133 格/秒** | 上界 450 |
| 另有修正 | 束缚 ×0.35、搬人 ×0.6、对角 ×1.41421、天气 | 原版 |
| ✅ **没有的** | `Pawn.MoveSpeed` **字段不存在**；`MoveSpeedBase` **不存在** | 【IL确证】真值表 §3 |

### 7.2 `骑车更快` 的条件（复核算式）

设：行程 `D` 格、绕路取车 `d` 格、车载速度 `v_b` 格/秒、上下车开销 `O` 秒。

```
直接走：  T_walk = D / 3
骑车：    T_bike = d / 3 + D / v_b + O

骑车更快的条件：
      D/3  >  d/3 + D/v_b + O
  ⇔   D/3 − D/v_b  >  d/3 + O
  ⇔   D · (1/3 − 1/v_b)  >  d/3 + O
  ⇔   D · (v_b − 3) / (3·v_b)  >  d/3 + O
  ⇔   D · (v_b − 3) / v_b  >  d + 3O
  ⇔   D · (1 − 3/v_b)  >  d + 3O
  ⇔   d  <  D · (1 − 3/v_b) − 3O          ✅ 与旧文档一致
```

| 车速 `v_b` | 加速比 | 可接受绕路 |
|---|---|---|
| 4.5 | 1.5× | `d < D/3 − 3O` |
| **6.0** | **2×** | **`d < D/2 − 3O`** |
| 9.0 | 3× | `d < 2D/3 − 3O` |

**代入真实场景**（**2026-10-13 实测更正③**：原先写死的 `O = 2 秒 → D > 22 格` 已被推翻，见下方更正块）：
```
v_b = 6.0 格/秒（2×）、期望绕路 d = 5 格
⇒  d < D·(1 − 3/v_b) − 3·O  变成   D > 10 + 6·O
```

> #### ⚠ 2026-10-13 实测更正③：阈值**不是一个数**，是一张**依赖 `O` 的表**
>
> **反例（原错误写法，逐字保留）**：
> ```
> 取 2× 车速（v_b = 6.0）、O = 2 秒上下车：
>      5  <  D·(1 − 3/6) − 6
>      5  <  D/2 − 6
>      D  >  22 格
> ```
> 以及原结论句 ~~「殖民地跨度不到 ~22 格时，共享单车是负收益」~~。
> **→ 已被探针实测推翻（2026-10-13）**：`O = 2 秒` 是**没有依据的假设**（原版成例区间是 **0 ~ 500 tick**，**没有任何原版先例落在 `O = 2 秒` 这个取值上**），据它推出的 `D > 22` 因此**没有依据**。依据：`tmp\bike-probe\run-3\RUN3-REPORT.md` §2.3 / §2.4；原版成例取证在 `tmp\bike-probe\run-3\il\`（`IL-RimWorld_JobDriver_EnterTransporter.txt`、`Y-RimWorld_JobDriver_ManTurret__MakeNewToils_d__13.txt`、`X-RimWorld_JobDriver_EnterCryptosleepCasket__MakeNewToils_d__1.txt`、`X-Verse_AI_Toils_General.txt`）。
>
> **现在正确的写法（区间 + 表）**：
> ```
> 公式：d < D·(1 − 3/v_b) − 3·O
> 单位：【格】= D、d；【格/秒】= v_b（与直走的 3）；【秒】= O
> 换算：60 tick = 1 秒（探针打点 oTicks / 60f；与 §7.4 的"6.0 格/秒 → 10 tick/格"自洽）
> 代入 v_b = 6、d = 5  ⇒  D > 10 + 6·O
> ```
>
> | `O`（**秒**） | 折合 **tick** | 临界 `D`（`d = 5`、`v_b = 6`） | 依据 |
> |---|---|---|---|
> | **0** | 0 | **`D > 10` 格** | **有原版成例**：`RimWorld.JobDriver_EnterTransporter`（进运输舱/穿梭机）**全程 `Toils_General::Wait` 0 命中、`defaultDuration` 0 命中** ⇒ 登乘动作 **0 tick**。⇒ **本设计选 `Instant` 上下车、按 `O = 0` 定阈值是可辩护的设计选择** |
> | 0.5 | 30 | **`D > 13` 格** | 无原版先例（区间内的类比取值） |
> | 1 | 60 | **`D > 16` 格** | 类比原版 XML `ticksToActivate = 60`（炮塔/设备族最小档位，`Buildings_Security_Turrets.xml`） |
>
> **⚠ 口径（必须与阈值一起读，否则会重犯原错）**：**`O` 没有被有效测出** —— 探针实测打出的 `O = 0 tick = 0.00 s` **不是一次有效测量**：① 上车 toil 按设计是 `ToilCompleteMode.Instant`（v2 §7.4）；② **下车项 `tEnd − tDismountStart` 在同一个 `initAction` 委托内前后取样，中间没有任何 tick 边界 ⇒ 结构性恒 0**（**无论下车 toil 用什么 `ToilCompleteMode`，这一项都恒为 0** ⇒ 下车开销在这套打点下**结构上永远测不出**）。
> ⇒ 正确表述是：「**选择 `Instant` 上下车时 `O = 0` 有原版依据**（`JobDriver_EnterTransporter` = 0 tick），因此**按 `O = 0` 设计阈值是可辩护的设计选择，不是实测事实**」。**不要写"实测 `O = 0`"**。依据：run-3 报告 §2.2。
>
> **`O` 是一个"设计上可选"的量（量级跨度极大，两端都有原版先例）**：
>
> | 原版成例 | 实际 toil 时长 | 性质 |
> |---|---|---|
> | `RimWorld.JobDriver_EnterTransporter`（进运输舱/穿梭机） | **0 tick** | 支持"上下车零耗时"这个设计选择 |
> | `RimWorld.JobDriver_ManTurret`（进炮塔值守） | **240 tick** | 支持"上一个载具位并停留" |
> | `RimWorld.JobDriver_EnterCryptosleepCasket`（进休眠舱） | **500 tick** | 支持"钻进容器并被封存" |
> | 原版 XML "操作装置"设计档位（`<ticksToActivate>` / `<activeTicks>` 共 10 处） | **60 / 120 / 180 / 240 / 500 …** | 60 tick（= 1 秒）是炮塔/设备族的最小档位 |
>
> **阈值全表（把原版档位都代进去，`d = 5`、`v_b = 6`）**：
>
> | `O`（秒） | 相当于 | 临界 `D` |
> |---|---|---|
> | 0（`Instant`，`EnterTransporter` 成例） | 0 tick | **`D > 10`** |
> | 1（单程 30 tick） | — | `D > 16` |
> | **2（单程 60 tick）** | ~~原写法~~ | ~~`D > 22`~~ **← 反例：`O = 2 秒` 无原版先例，本行只作对照** |
> | 4（单程 120 tick，Odyssey 引力舰档） | — | `D > 34` |
> | 8（单程 240 tick，`ManTurret` 成例） | — | `D > 58` |
> | 16.7（单程 500 tick，休眠舱成例） | — | `D > 110`（**阈值几乎消失**） |
>
> **交叉验证（说明公式没写反）**：原写法自己算的 `O = 2 ⇒ 5 < D/2 − 6 ⇒ D > 22`，用通式 `D > 10 + 6O` 也是 `22` ✅；原写法另写的 `O ≈ 0.3 秒 ⇒ D ≈ 12`，通式给 `10 + 1.8 = 11.8 ≈ 12` ✅。
>
> **⚠ 单位纪律（本条的教训）**：`O` 的单位是**秒**，而游戏 tick 换算 = **60 tick/秒**。探针方案的旧版本里出现过按 **30 tick/秒** 折算的括号（`O = 0.5 秒` 被写成"15 tick"、`O = 1 秒` 被写成"30 tick"），**那是错的** —— 引用时**以秒为准**。
> ⚠ **本表自身的一处残留错误（2026-10-13 实测更正，本行其余照旧）**：上一张表里的 **`1（单程 30 tick）`** 是按 **30 tick/秒** 折算的**旧口径**，按 **60 tick/秒** 应为 **`1（单程 60 tick）`**（保留旧写法作反例）。**`D > 16` 这个数值不受影响**（它由 `O = 1` 秒算出）。

> **结论（替换原那句写死的 22 格）**：**阈值 = 一张区间表**：取 `O = 0`（有原版成例）⇒ **`D > 10` 格**；取 `O = 0.5 秒` ⇒ **`D > 13` 格**；取 `O = 1 秒` ⇒ **`D > 16` 格**。
> **`O = 2 秒` / `D > 22` 已作废**（它建立在无依据的假设上）。
> （原写法的这条备注仍成立，只是数值要按上表读：`O` 越小阈值越低 —— ~~若实测 `O ≈ 0.3 秒`，阈值降到 `D > 2×(5 + 0.9) ≈ 12` 格~~。而"这件事必须实测"这条**已被证伪一半**：下线项在本打点口径下**结构上测不出**（见上方口径块）。）

> **⚠ 但阈值本身不是"做不做"的判据**：用户要的是"共享/停放/整理"这套**玩法语义**，不是纯提速。
> 阈值只用来定 **`v_b` 该给多少**：给太高（>9）× 阈值消失，车就变成无脑必选；给 6.0×（2×）能保留"值不值得骑"的选择感。

### 7.3 ⚠ "人口越多越划算"这条需求本身的坑

> **车的数量固定 + 人口上升 → 争用上升 → 等待上升 → 收益反而下降。**

要真的随人口产生正收益，需要三件事**同时**成立：

| # | 条件 | 本项目的现状 |
|---|---|---|
| ① | **车数随人口增长** | 车要玩家造；**没有任何自动补充机制** → 第一版不满足 |
| ② | **目的地分散** | 如果所有人都挤向同一个仓库，该建的其实是**公交线**不是共享单车 |
| ③ | **轻量调度**（按最近 / 最久未用选车） | §6.5 的确定性排序规则就是这个（按距离 + `thingIDNumber`），**满足** |

**写进设计结论**：
- **不要承诺"人口越多越划算"**；
- 第一版用 `ReservationUtility.Reserve(maxPawns: 1)` 天然限流（一人一辆），**争用时小人退回走路**——这是正确行为，不是 bug；
- 若将来要做"随人口增长"，正确做法是**给玩家一个"车数不足"的提示/自动补充策略**，而不是让 AI 更聪明。

### 7.4 加速实现：给多少

| 项 | 建议值 | 理由 |
|---|---|---|
| `v_b` | **6.0**（2× → 20 tick/格变 **10 tick/格**） | 阈值 `D > 10`（`O = 0`，见 §7.2 实测更正③；~~原写 `D > 22`~~ = 反例）保留"值不值得骑"的选择感 |
| `O` | **目标 < 0.5 秒**（两个 Instant toil） | 直接决定阈值 ⇒ §7.2 现在给的是**依赖 `O` 的表**（`O = 0 → D > 10`；`0.5 → 13`；`1 → 16`）。⚠ **`O` 在本探针的打点口径下结构上测不出**（下车项同委托内取样恒 0，见 §7.2 实测更正③）⇒ 按 `O = 0`（`Instant`，`JobDriver_EnterTransporter` 成例）设计**是设计选择，不是实测事实** |
| 上下车的 toil | `ToilCompleteMode.Instant` | 无等待 |

> **实现细节**：Postfix 直接把 `__result` 改成"目标 tick 数"（`6.0 格/秒 → 10 tick/格`），
> **不要**去改 `StatDefOf.MoveSpeed`（那会污染所有读该 stat 的系统）。用 `Mathf.Max(1f, ...)` 兜住下界。

---

## 8. 1.6 / 1.7 双版本

### 8.1 对照表（两边的硬约束）

| | **1.6**（主开发，唯一可编译可测试） | **1.7**（留位） |
|---|---|---|
| TargetFramework | **`net472`** | **`net10.0`** |
| LangVersion | **必须 `/langversion:7.3`**（C# 8+ 语法不许用） | **`LangVersion latest`**（保守可用 **12.0**） |
| 引擎 | Mono / CLR4 | Waylith + .NET 10 CoreCLR |
| Harmony | **2.4.x** | **3.0** |
| 引用目录 | **`RimWorldWin64_Data\Managed\`**（`Assembly-CSharp.dll` / `mscorlib.dll` / `UnityEngine*.dll`…） | **游戏根目录**的 `Assembly-CSharp.dll` + `UnityStubs.dll` + `Waylith.dll` |
| 必须**去掉**的引用 | — | **`UnityEngine.*` / `mscorlib` / `netstandard`** |
| 额外属性 | — | **`ProduceReferenceAssembly=false`** |
| 编译方式 | node 脚本直调 VS BuildTools 的 Roslyn `csc`：`/nostdlib+ /langversion:7.3 /optimize+ /deterministic+` | csproj（`<TargetFrameworks>net472;net10.0</TargetFrameworks>`） |

### 8.2 本机现状（**说清做不到什么**）

- ⚠ **本机没有 .NET SDK**（只有 5.0/6.0 **运行时**）→ **`net10.0` 那一支现在编不出来**。
  **这不是架构问题，是缺工具链**——**SDK 待用户安装**。
- **主开发锁 1.6**（唯一可编译、可测试）。
  ⚠ 而且 1.6 侧目前也**没有 `.csproj`**（全仓库零 csproj），走的是 node 脚本直调 Roslyn `csc`。
- `Source/1.7` 先留空。

### 8.3 `Core` / `Game` 分层（双版本的最大红利）

```
Source/1.6/
├─ Core/      ← 纯逻辑：不引用任何 Verse/RimWorld/UnityEngine 类型
│                （静态闸门：正则 \b(Verse|RimWorld|UnityEngine|HarmonyLib)\s*\. 必须零命中）
│   例：骑车是否更划算的距离估算（纯浮点数学）、
│        "先收哪辆"的排序键计算（距离 + thingIDNumber）、
│        质量 → 能带几辆的换算
└─ Game/      ← 引用 Assembly-CSharp：Comp / JobGiver / JobDriver / WorkGiver / Patch / Zone
```

> **`Core` 在两种框架下同一份源码各编一遍、一行不改**；1.7 真正要动的只有 `Game` 层（引用列表 + Harmony 版本 + 少量 API 差异）。
> **纪律**：把 §7 的全部算式放进 `Core`——这样它们是**可单元测试的**（不启动游戏就能验算），
> 而且 §7.3 那类"算法层面的坑"能在编译期就被发现。

---

## 9. 待确证清单与风险

> 真值表 §5 在 **2026-10-13 人车解绑轮**从 26 条追加到 **34 条**（新增 27–34，并给第 4、6、8 项做了"已确证/已结案"标注）。
> 这里是**与本项目相关**的那些（其余见真值表原文）。
> ⚠ **最重要的一条已经结案**：真值表 §5 第 6 项（`IPathGridCustomizer` 是不是 mod 钩子）**答案是否**，见 §9.2 与真值表 §3.7c。

### 9.1 从真值表 §5 抄入的相关项（**不许写成肯定句**）

| 真值表 §5 # | 待确证项 | 对本项目的影响 | 下一步 |
|---|---|---|---|
| **12** | "自定义 `MinifiedThing` 子类在 `PostMake` 里装内层，**游戏内真的能跑**" | ★ **最关键**：这是"锻造台造出物品形态"的唯一路径 | **必须在游戏里实测**：造一辆 → 看它是不是一个带内容物的物品（而不是红字+自毁）。<br>**→ 已结案（2026-10-14）**：**已实测成立** —— 配方产物 def = `ProbeBikeMinified`、thingClass = `BikeProbe.MinifiedBike`、**内层唯一**、Install 后原版日志 `spawned=True`、**配方路径红字 0 条**；判据与证据见 **§2.3 的「run-4 实测」块**、真值表 **§2.9.4** / `tmp\bike-probe\run-4\RUN4-REPORT.md`。仍未确证的只剩"产物被搬运未观测"与"逆路径红字"2 条 |
| **6** | `Verse.PathRequest+IPathGridCustomizer` 的方法签名（是否 mod 可用钩子） | 若它可用，会**替代** `TicksPerMove` Postfix 成为更正规的介入方式 | 读 `v:A4c-PathGridCustomizer-members.txt` 的原始元数据；**第一版不依赖它** |
| **3** | `Verse.PathFinder::FindPathNow` / `CreateRequest` 的参数类型 | 我们**不用寻路的 map 级 API**，影响低 | — |
| **2** | `Verse.AI.PathGrid::CalculatedCostAt` 的第 2/3/4 个参数类型 | 影响"车不挡路"的**验证手段**（想验算代价需要它） | `mdb.py` 解码器待修 |
| **4** | `Verse.TraverseParms` 的成员清单 | 影响"整理工能不能走到车"的判定（若要显式判可达性） | 未 dump |
| **5** | `Verse.PathGridDef::workerType` 期望的基类 | ❌ 与本项目无关（我们不自定义 PathGrid） | — |
| **1** | `OrderByDescending` 在 `priorityInType`/`naturalPriority` **相等时**的稳定性（**部分确证**） | 若同值顺序承重 → 行为看起来随机 | **用显式 tie-break**，别依赖 |
| **8** | `RimWorld.WorkTypeDefOf` 的字段**怎么被填的** | 不影响"它不是注册表"的结论 | — |
| **9** | `relevantSkills` 为空列表时 `AverageOfRelevantSkillsFor` 返回什么 | ★ 影响"新 WorkType 会不会被默认激活"（§6.1-K3） | 实测 / 读 `SkillRecord` |
| **13** | "一个工人一次抱好几辆自行车" | ★ 原版**无实现**（IL 推论）；背包路线也**未实测** | 自写 Job 循环（§6.4），实测 5 辆 |
| **14** | `Verse.AI.Job::checkEncumbrance` 的默认值 / 原版哪些派发点置 true | 影响"整理工会不会半路停手" | 完整扫 `stfld checkEncumbrance` |
| **15** | `Verse.ThingDef::category` 在运行期对 `Frame`/`Blueprint` 的实际取值 | 低（我们不改 Blueprint 生成） | — |
| **16** | 存档加载路径里 `thingIDNumber` 是否重排 | ★ **影响 §6.5 的确定性排序键**（若读档后重排，"同距离按 ID"在存档前后会变） | 读 `Verse.Thing::ExposeData` 的 ID 恢复路径 |
| **17** | Vehicle Framework 的 `VehicleReservationManager` 是否真实存在（**未能复核**，非已证伪） | 不影响——**我们不用 VF**（见 §9.3） | — |
| **19** | "谁调用 `Verse.Pawn::GetGizmos`" | 低（Gizmo 链路本身已闭合确证） | 调用点在 UI 程序集 / 经 `IGizmoOwner` |
| **21** | `RimWorld.BuildingProperties` 的**完整成员清单** | ★ 我们要用 `uninstallWork` / `alwaysUninstallable`（**已确证存在**），但完整清单没 dump | 补一份 `=RimWorld.BuildingProperties` dump |
| **23** | `Verse.CellFinder` / `Verse.CellFinderLoose` / `Verse.GenPlace` / `Verse.GenSpawn` / `Verse.GenGrid` / `Verse.ReachabilityUtility` / `RimWorld.RCellFinder`（**落点搜索一族**） | ✅ **本轮已补到"可据以写代码"的级别**（真值表 §2.4 **仍只收 `GenConstruct` 一族** ⇒ 本节以取证报告为准）：`GenSpawn.CanSpawnAt` / `GenPlace.TryPlaceThing`（两个重载）/ `GenDrop.TryDropSpawn` / `CellFinder.FindNoWipeSpawnLocNear` / `CellFinder.TryFindRandomCellNear` / `GenGrid.Standable` / `GenGrid.Walkable` / `GenGrid.Impassable` / `PathGrid.CalculatedCostAt` 的判据链**全部已确证**（见 §5.4 / §5.5） | 证据：`tmp\il-evidence-bike\placement\README.md`（**含反射签名 + IL + 原始元数据三路**）。**待办**：把这一族正式追加进真值表 §2.4 |
| **24** | `MassUtility::Capacity` 的 IL 指令条数（15 vs 30） | 口径冲突，**不影响结论**（不引用条数） | — |
| **25** | "`CarryingCapacity` 无读者"的原结论 | **已被同一轮的证据文件推翻**（实为 **3** 个读者） | 引用时写 **3** |
| **26** | `Verse.PathFinderJob` / `Verse.PathFinderMapData` / `Verse.PathGridJob` 的成员**语义** | 低 | — |

### 9.2 待确证 / 已结案（**真值表 2026-10-13 人车解绑轮**）

> ⚠ 本表的 **Q7–Q14 已在 2026-10-13 人车解绑轮全部结案**（真值表新增 §1.5 / §2.8 / §3.5bis / §3.7）。
> **这些条目的"原始错误写法"（反例）已迁出**：见 **机制真值表 §8（append-only）** / 交接件 `tmp\doc-migration\v2-evidence-layer.md`。**本节只留当前结论。**
> 仍未结案的只有 **Q15**（"被俘"没有可覆写挂点；**Q8 与 Q16 已在本轮结案**）。

**① 已结案（可据以写代码）**

| # | 原待确证项（本节不再复述反例） | ✅ 结案结论（真值表位置） |
|---|---|---|
| **Q7** | `Verse.Pawn_HealthTracker::MakeDowned` / `SetDead` 的签名 | **`Verse.Pawn_HealthTracker`**（`RimWorld.*` **不存在**，本工程第 6 次同型事故）；`MakeDowned(Nullable<Verse.DamageInfo>, Verse.Hediff)` **private**；`SetDead()` **public 非虚、4 条指令、不是死亡总线**；**死亡总线 = `Verse.Pawn::Kill`（public virtual）**（§1.5 / §2.8 / §3.7a-b） |
| **Q9** | `Verse.ThingComp.PostDeSpawn` / `PostSpawnSetup` 的签名与调用时机 | **`public virtual void PostDeSpawn(Verse.Map, Verse.DestroyMode = Vanish)`** / **`PostSpawnSetup(bool)`** / **`PostExposeData()`** / **`PostPostMake()`** 全部 `public virtual`；`ThingWithComps::DeSpawn` raw 字节确认 `callvirt ThingComp::PostDeSpawn` 逐 comp 派发（§2.8 / §3.7a） |
| **Q10** | `Verse.MapComponent` 的成员与生命周期 | `Verse.MapComponent`（abstract，`FIELD public Verse.Map map`，`CTOR MapComponent(Map)`）+ **8 个 `public virtual` 无参 void**：`MapComponentUpdate` / `MapComponentTick` / `MapComponentOnGUI` / `MapComponentDraw` / `ExposeData` / `FinalizeInit` / `MapGenerated` / `MapRemoved`（全 `0x01c6`）⇒ 全部可覆写（§2.8 / §3.7a）。⚠ 命名空间是 `Verse`（`RimWorld.MapComponent` 不存在） |
| **Q11** | `Verse.AI.ThinkNode_JobGiver` / `Verse.AI.JobDriver` 的成员 | `ThinkNode_JobGiver::{TryGiveJob(protected abstract), TryIssueJobPackage(public virtual)}`；`JobDriver::{MakeNewToils(protected abstract), TryMakePreToilReservations(public abstract), GetReport/Notify_PatherArrived/Notify_PatherFailed(public virtual), ModifyCarriedThingDrawPos(public virtual)}`；**非虚** = `DriverTick` / `DriverTickInterval` / `EndJobWith`（§2.8 / §3.7a-b） |
| **Q12** | 远行队带走小人 / 传送 的确切触发点 | **离图收口 = `Verse.Pawn::ExitMap(bool, Rot4)`（public virtual，10 个调用者）**；**传送 = `RimWorld.PawnFlyer::MakeFlyer(...)`（public static）** + **`Verse.Pawn::Notify_Teleported(bool = true, bool = true)`（非虚）** + **`Verse.Pawn::teleporting`（public bool 字段）**；**不存在 `Pawn.Teleport` / `Thing.Teleport*`**（§2.8 / §3.5bis）；运输舱容器 = **`RimWorld.ActiveTransporter`**（`base=Verse.Thing`，`protected void Tick()` **非虚**），原版走 **`RimWorld.Planet.CaravanExitMapUtility`（static）+ `RimWorld.Planet.TravellingTransporters::AddTransporter`**。<br>⚠ **本条另有两处实测更正**（详见 §3.5 / 机制真值表 §8）：① `ExitMap` 对**非 `Pawn` 的 `CarriedThing`** 有 **`Destroy(Vanish)`** 分支（旧摘要漏了它）；② `teleporting` 的真实语义是 **`teleporting && child != null` 时跳过 `Clear()`**，不是"整批清空" |
| **Q13** | `Verse.Scribe_References.Look` 的确切签名 | `public static void Look<T>(ref T, string label, bool = false) **where T : Verse.ILoadReferenceable**`；第二重载收 `ref System.WeakReference<T>` + `where T : Verse.ILoadReferenceable, class`；类共 4 个 static 成员，**零可覆写成员、不注册解绑回调**（§2.8 / §3.5bis） |
| **Q14** | `Verse.DefOfHelper` / `[DefOf]` 的生成机制 | ✅ **自定义 `[DefOf]` 可用**：**`RimWorld.DefOf : System.Attribute`**（**不在 `Verse`**）+ **`RimWorld.DefOfHelper::RebindAllDefOfs(bool earlyTryMode)`（public static）**；走 `GenTypes.AllTypesWithAttribute` → `Parallel.ForEach(types, BindDefsFor)`；`BindDefsFor` 用 BindingFlags `0x18`（Public+Static+FlattenHierarchy）只读静态字段，字段类型须过 `Verse.GenTypes::IsDefThreaded`。**全程序集 165 个类型带 `[DefOf]`** ⇒ **属性驱动、无需注册、无需改 XML**（§2.8） |

**② 仍未结案**

| # | 待确证项 | 现状 | 下一步 |
|---|---|---|---|
| **Q8** | `Verse.GenSpawn.CanSpawnAt` / `Verse.CellFinder.FindNoWipeSpawnLocNear` / `Verse.GenPlace.TryPlaceThing` / `Verse.GenDrop.TryDropSpawn` 的**完整签名** | ✅ **2026-10-13 全部结案**（布局取证报告，反射签名 + IL + 原始元数据三路）：`Verse.GenSpawn.CanSpawnAt(Verse.ThingDef, Verse.IntVec3, Verse.Map, Nullable<Verse.Rot4> = null, bool canWipeEdifices = true)`；`Verse.CellFinder.FindNoWipeSpawnLocNear(Verse.IntVec3 near, Verse.Map, Verse.ThingDef, Verse.Rot4, int maxDist = 2, Predicate<Verse.IntVec3> = null)`；`Verse.GenPlace.TryPlaceThing(...)`（两个重载，`ThingPlaceMode.Near = 1` / `Radius = 2`）；`Verse.GenDrop.TryDropSpawn(...)`（**只有 1 个 8 参重载**）。<br>⚠ 真值表 §2.4 **仍只收 `GenConstruct` 一族**（§6.3.1-A14 已把 `CellFinder::FindNoWipeSpawnLocNear` 列为**已确证的 `RadialPattern` 读者**） | 已完成；**待办**：把这一族正式追加进真值表 §2.4；签名详见 [tmp\il-evidence-bike\placement\README.md](../tmp/il-evidence-bike\placement\README.md) §0 / §1.1 / §2.1 / §3.5 |
| **Q15** | 🆕 **"被囚禁 / 被俘"有没有可覆写挂点** | 本轮**没有解决**：`Verse.Pawn::GuestStatus` 是**非虚属性、且无 setter**（真值表 §2.8）⇒ 它**不是挂点**。本轮也没有找到"变囚犯"的收口方法 | 需要单独一轮取证：找 `Verse.Pawn_GuestTracker` 一侧的改状态方法，或接受"靠被俘后的 Job 结束路径兜"（§3.3-9）。枚举本身已确证：**`RimWorld.GuestStatus`**：`Guest = 0`、`Prisoner = 1`、`Slave = 2`（⚠ 在 `RimWorld`，不是 `Verse`） |
| **Q16** | `Verse.Pawn::Kill` **内部**都做了什么（死亡时对随身容器 / 装备 / 车 / 尸体的处理顺序） | ✅ **2026-10-13 已结案**（读全 `Verse.Pawn::Kill` 的 11 段 body）：**`Kill` 对随身容器（`carryTracker` / `inventory` / `equipment` / 自定义 holder）零解绑** —— 它唯一的容器操作是 `Verse.Pawn::RemoveFromHoldingContainer(ref Map, DamageInfo?, out bool)`（只处理"pawn 自己在 `RimWorld.Building_HoldingPlatform` 或 `RimWorld.CompTransporter` 里"，83 字节）与 `holdingOwner.Remove(this)`（`InContainerEnclosed` 时）。`DropBeforeDying` 处理的是**"垂死的 pawn 自己正躺在别人的 `Pawn_CarryTracker` 里"**，**不碰"垂死者自己手里的东西"** | ⇒ 我们的摘车**不会与 `base.Kill` 重叠**；**顺序要求 = 前缀里先处理**（见上方排序风险表）。证据：`tmp\il-evidence-container\REPORT.md` §4.2（`c:21`/`c:22`/`c:23`）/ `tmp\il-evidence-exitmap\REPORT.md` §4.1 |

> ### ⚠ 排序风险（2026-10-13 已结案：三个挂点的顺序要求如下）
>
> **每个挂点的执行顺序不是"建议"，是落地要求**（依据：`tmp\il-evidence-exitmap\REPORT.md` §3 / 容器取证 §C.5 / 真值表 §2.8）。
>
> | 挂点 | 要求的顺序 | 依据 |
> |---|---|---|
> | `Verse.Pawn::ExitMap` | **Harmony 前缀：先幂等摘车 + 落地 → 再放行原版** | `base.ExitMap` 的 `IL_0101` 会 `Destroy(Vanish)` 非 `Pawn` 的 `CarriedThing`；顺序反了车就没了（§3.5） |
> | `Verse.Pawn::Kill` | **Harmony 前缀：先抓 `bike` / `rider` 引用 → 摘车 → 再放行原版 `Kill`** | `Verse.Pawn::Kill` **不访问 `carryTracker`**（144 个读取者名单里没有它），但它会掉装备 / 生尸体 / `DeSpawnOrDeselect`；⚠ **车型在 `carryTracker` 里（`LookMode.Deep`）⇒ 小人死亡时车会随他被深存**，所以必须在前缀里处理 |
> | `Verse.ThingComp::PostDeSpawn` | **`Unbind` → 再无条件调 `base.PostDeSpawn(map, mode)`** | 车即将被销毁；"清小人身上的标记"绝不能依赖车的实例仍可用（P3 幂等） |
>
> ✅ **"先解绑安全吗"已经验证过，不再是未知量**（`tmp\il-evidence-exitmap\REPORT.md` §1 / §3）：
> - `ExitMap` 的外层守卫是 **`IL_007F brfalse IL_012E`**（`if (carryTracker?.CarriedThing != null)`）——摘车之后 `CarriedThing == null` ⇒ **`IL_0101` 的 `Destroy` 与 `IL_011E` 的 `Clear()` 都不会执行**；
> - `ExitMap` 方法体内对 `carryTracker` 的**全部 5 处引用**都在那个块内，**没有第 6 处**；
> - 唯一的下游读者 `Verse.AI.Pawn_JobTracker::CleanupCurrentJob` 用 `PawnUtility::IsCarrying` 兜底（空手时整段掉落逻辑被跳过）；
> - `Verse.Thing::DeSpawn` / `Verse.Pawn::DeSpawn` / `ThingOwner::set_UnloadEverything` **都不碰容器**。
> - ⚠ **残留风险**：**第三方 mod** 的 `JobDriver.Cleanup` 若隐式假设 `CarriedThing != null`，可能在我们摘车后看到 null（**【未确证】**，原版无此反例）。


**还有一批符号**——2026-10-13 人车解绑轮之后，**这张表里全部已经结案**（下方逐项 ✅）：

| 符号 | 本文用在哪 | 真值表状态（2026-10-13 后） |
|---|---|---|
| `ToilCompleteMode`（如 `Instant`） | §7.4 上下车 toil 用 `Instant` | ✅ **已结案**：**`Verse.AI.ToilCompleteMode`**（**在 `Verse.AI`，不在 `Verse` 根**；underlying = `System.Byte`）：`Undefined = 0`、**`Instant = 1`**、`PatherArrival = 2`、`Delay = 3`、`FinishedBusy = 4`、`Never = 5`。字段真名 **`Verse.AI.Toil::defaultCompleteMode`（public 字段，不是属性）**（真值表 §1.5 / §2.8） |
| `Thing.SetPositionDirect(IntVec3)` | §5.6 的**红线**（禁止使用） | ✅ **已结案（且升格为"禁用"）**：**`Verse.Thing::SetPositionDirect(IntVec3)`**，raw 8 bytes 只有 `ldarg.0; ldarg.1; stfld positionInt; ret` ⇒ **不更新 ThingGrid / CoverGrid / Regions / mapMesh**。真值表 **§3.7d 已专门列表说明为什么不能用**（§3.7b 也列它非虚） |
| `Verse.GenRadial.RadialCellsAround` / `RadialPattern` | §6.5 空间分桶的由近及远枚举 | ✅ **已结案**：`RadialCellsAround(IntVec3, float, bool)` + 重载 `(IntVec3, float, float)`（`public static`）；**`RadialPattern` 是 `public static Verse.IntVec3[]` 字段，不是方法**；另有 `ManualRadialPattern` / `RadialPatternRadii` / `RadialPatternCount = 20000` / `MAX_RADIUS = 80`；**116 个读者 / 107 个调用者** ⇒ 见 §5.5 补充（**复用，不要自写螺旋**）（真值表 §2.8 / §3.5bis） |
| `Verse.Scribe_References`（类本身） | §3.4 单向持久化 / §3.5 | ✅ **已结案**：`static class`，4 个成员（`Look<T>` ×2 + `CheckSaveReferenceToDestroyedThing` + `CheckSaveReferenceToDestroyedWorldObject`），**零可覆写成员、不注册解绑回调**；泛型约束 `where T : Verse.ILoadReferenceable`（真值表 §2.8） |
| `Verse.AI.Pawn_MindState`（mental state 入口） | §3.3-5 | ✅ **已结案**：**`Verse.AI.Pawn_MindState`**（`Verse.Pawn_MindState` **不存在**）；**没有 `MentalState` 成员**；关键成员是 public 字段 **`mentalStateHandler`**（`Verse.AI.MentalStateHandler`）/ `mentalBreaker` / `mentalFitGenerator` / `duty` / `thinkData` / `exitMapAfterTick`；`MentalState` 是 **`Verse.Pawn::MentalState`（非虚属性）**（真值表 §1.5 / §2.8） |
| `Verse.Map::uniqueID` | §6.5 分桶 key | ✅ **已结案**：**`FIELD public int uniqueID`**（**public 字段，不是属性**；**不是 `UniqueID`、不是 `uniqueIDNumber`**）；旁边有 `FIELD private int generatedId`；类另有 `public virtual string GetUniqueLoadID()`（真值表 §2.8） |
| `Verse.TraverseParms` 的成员清单 | §9.1 真值表 §5-4 项 | ✅ **已结案**：`kind=struct base=System.ValueType impl=IEquatable<Verse.TraverseParms>`；**11 个 public 字段**（`pawn` / `mode` / `maxDanger` / `canBashDoors` / `canBashFences` / `alwaysUseAvoidGrid` / `fenceBlocked` / `avoidPersistentDanger` / `avoidDarknessDanger` / `avoidFog` / `targetBuildable`）；**`static For(...)` ×2 重载**；实例方法 `WithFenceblocked(bool)` / `WithFenceblockedOf(Verse.Pawn)` / `Validate()` / `Equals` / `GetHashCode` / `ToString`（真值表 §2.8） |
| `Verse.IPathFindCostProvider`（🆕 本轮新增的正面情报） | §4.5 加速 / 寻路侧介入 | ✅ **已结案（但限制很大）**：`Verse.IPathFindCostProvider`（**顶层**接口）＝ `System.UInt16 PathFindCostFor(Verse.Pawn pawn)` + `Verse.CellRect GetOccupiedRect()`；**实现者 6 个，全是原版陷阱建筑**（`Building_Trap` / `Building_TrapExplosive` / `Building_TrapReleaseEntity` / `Building_TrapReleaseHunter` / `Building_TrapReleaseWasp` / `Building_TrapDamager`）。<br>⇒ ✅ **可被 mod 实现**（这才是"官方承认、可被 mod 实现"的寻路侧接口）<br>⚠ **但它只能改"某格对某 pawn 的代价"，改不了路网拓扑 / 可通行性** ⇒ **不能替代 Harmony**，架构结论不变（真值表 §2.8 / §3.7c / §6.3.2） |
| `Verse.PathRequest+IPathGridCustomizer` | §9.1 真值表 §5-6 项 | ✅ **已结案：不是 mod 钩子**（形式上是 public 嵌套接口，实质是死代码）：唯一方法 `NativeArray<ushort> GetOffsetGrid()`（`flags=0x05c6`）；**全程序集 0 个调用点**；实现者只有 2 个原版内部类（`Verse.AI.BreachingGrid+CustomTuning`、`RimWorld.UsedRectPathGridCustomizer`）；`Verse.PathRequest::customizer` 全程序集只有 **2 个读写点**；唯一注入路径是 `Verse.PathRequest` 的 public ctor 第 12 个可选参数＝**重建整个 PathRequest**（真值表 §3.7c / §6.3.2）<br>⚠ **形态**：是**嵌套类型**（`Verse.PathRequest+IPathGridCustomizer`），**没有顶层 `Verse.IPathGridCustomizer`**（§1.5 / §3.5bis） |

> **不属于"未确证"的**（它们本来就是**我方自建的名字**，不是原版 API）：`MinifiedBike` / `CompSharedBike` /
> `CompSharedBikeRider` / `MapComponent_SharedBikeRegistry` / `JobGiver_RideSharedBike` /
> `JobDriver_RideSharedBike` / `JobDriver_TidySharedBike` / `WorkGiver_TidySharedBikes` /
> `Zone_SharedBikePark` / `Designator_ZoneAdd_SharedBikePark` / `PlaceWorker_SharedBike` /
> `Patch_TicksPerMove` / `Patch_ExitMap` / `Patch_Kill` / JobDef `RideSharedBike` / Zone 名等。
> **命名纪律**：自建 Def 名一律带 mod 前缀（§6.1-K2）；自建类名带 mod 命名空间。

### 9.3 选型风险（已评估、已排除的路线）

| 路线 | 结论 | 依据 |
|---|---|---|
| **Vehicle Framework / VVE** | ❌ **不采用** | VF 官方 FAQ 原文："**Can colonists use vehicles to get around faster on the map? → Not from AI behavior, no.**"；VF 不在本机（`About.xml` 精确匹配 `SmashPhil.VehicleFramework` **0 命中**）；VF 有 3 个硬依赖（Harmony + VF + VFE，VFE 是 **CC BY-NC-ND 4.0**：不许分享改编作品）；官方自述"very big, so there are going to be incompatibilities"；社区记录加载时间 10s → 35s~53s。见 [tmp\vve\EVIDENCE.md](../tmp\vve\EVIDENCE.md) |
| VF 的 `VehicleReservationManager` | **未能复核（非已证伪）** | VF 仓库文件清单接口返回被截断（258 个 `.cs`，末条被切断）；已回传的 123 个 `Source/Vehicles/**/*.cs` 里**没有任何文件名含 `Reserv`** → **采样不完整** |
| 自建载具 Pawn（despawn 小人、上载具） | ❌ 不采用 | 那是 VF 路线；且会引出一整类"小人被 despawn 时的需求/健康/心情"问题 |
| Hediff 当加速载体 | ❌ 不采用 | 会进健康面板，带 `tendable`/`isBad`/`makesSickThought`/`makesAlert` 一堆可见副作用；且 **`statOffsets` 在 `HediffStage` 上、不在 `HediffDef` 上**（容易写错层级） |
| Apparel 当加速载体 | ❌ 不采用 | 倒地/被剥光/囚犯化/麻醉时会脱落 → **车凭空消失** |

### 9.4 ⚠ ~~**本项目到目前为止 0 行运行时实测**~~（放大的诚实声明）**→ 本句话已过期，现为「只对产品代码成立」（2026-10-13 实测更正）**

> ~~本工程**没有任何运行时实测**。所有取证都是**只读**：读 IL / 元数据 / 原版 XML，**不驱动游戏**。~~
> **→ 已被探针运行时实测推翻（2026-10-13 实测更正）**：一次性探针 `mods/BikeProbe` 已在 `profiles/bike` 隔离实例里跑过三轮（**真值表 §2.9** 的【运行时实测】行、`tmp\bike-probe\FINAL-12-ITEMS.md`）。**下面这一整节只对产品代码 `Source/` 成立** —— 产品代码仍然是 0 行运行时实测。
> ⚠ **另注：本节下面列的"三件事"也已部分过期**（C3 已实测成立 / C1 已根因化但主命题未测 / `O` 是设计选择而非实测事实），逐条现状见真值表 §2.9.2 与 `tmp\bike-probe\FINAL-12-ITEMS.md` §③④；**本节原文保留作反例，不要在别处引用它的"一条都没跑过"口径**。
> ~~因此真值表里**没有一行**是【运行时实测】，本文也**没有一行**是【运行时实测】。~~
> **→ 已过期（2026-10-13 实测更正）**：真值表自 **§2.9** 起有【运行时实测】行；**本文的§2.3「实测更正①②」与 §7.2「实测更正③」本身就是探针运行时实测的直接产物**（产品代码仍是 0 行）。
>
> 具体到本项目，~~**以下三件事一条都没在游戏里跑过**~~ → **2026-10-13 实测更正：这句话也已过期，逐条现状如下**（三件都**跑过**了，但只有一件完整答掉）：
> 1. ~~`MinifiedBike.PostMake` 装内层后，造出来的物品**是否正常工作**（§9.1-12）；~~ → **2026-10-13 实测更正：已部分实测** —— `PostMake` 确实执行、内层确实装上、物品**没自毁**；两条红字根因已钉死（`not madeFromStuff` + 同 def 兼作 `minifiedDef`）。⚠ **但"配方产出"主命题仍未测**（12 次 `PostMake` 全来自 `MinifyUtility.Uninstall`）⇒ **仍未结案**（真值表 §2.9.2 末行 / §5 第 12 项）。
> 2. ~~骑行 Job 的 **`O`（上下车开销）**到底是多少（§7.2 的阈值全靠它）；~~ → **2026-10-13 实测更正：已定性** —— `O` 在探针打点口径下**结构上测不出**（下车项同 `initAction` 委托内取样恒 0）；现按"**`O` 是设计选择**"处理（`O = 0` 有 `JobDriver_EnterTransporter` 成例）⇒ **§7.2 不再依赖它**（设计 v2 §7.2 实测更正③）。
> 3. ~~`WorkGiver` 派活后，整理工**是否真能一次背 5 辆车**（§6.4）。~~ → **2026-10-13 实测更正：已实测成立** —— 逐辆累积到 `inv=5`（`freeSpace 33.4→…→12.4`，各 −7），容量 = `BodySize × 35`；⚠ 口径 = **探针 Job 的容器实测**，不是产品侧"5 辆批量调度"（`tmp\bike-probe\FINAL-12-ITEMS.md` §③-C3）。
>
> **纪律**：~~这三条**必须先做一个小样实测**，再投入整合工作。~~ → **2026-10-13 实测更正：探针轮已把这三条跑过一遍**（Step 0.5 地基探针，落点见真值表 §2.9 / §6.4）；**剩下的未答项 = `C1` 的"配方产出"主命题 + A9 的 `insertPriority` 0/100 + D 的视觉面**（见 `tmp\bike-probe\FINAL-12-ITEMS.md` §④）。**"任何'照本文实现就一定行'的说法都是错的"这句仍然成立**。

---

## 10. 未决问题（要用户拍板 / 明确留待第一版之后）

> **编号约定**：本节用 **M1…M8**（M = 待决机制问题），与 §9.2 的 **Q7…Q16**（待确证 / 已结案的符号）区分开——两处都叫 Q 会混。
> ⚠ **2026-10-13 更正**：上一版本文首写"本节用 M1…M9"，但表里实际有 **M1…M11**（编号声明与表格不符，已修正）；其中 **M6 / M10 / M11 已由用户拍板**，**M12 已由取证结案**，**已全部移出本节**，转入下方「✅ 已决定」表。

### ✅ 已决定（**已从"未决问题"移出，进入正式设计**）

| 已决定项 | 决定 | 正式设计位置 |
|---|---|---|
| **M6**（原）｜骑行期间车"留在原地"还是"跟着人" | **贴图跟着小人走**（**连续跟随**，像抱小孩）——**不是**留原地、**不是** `DeSpawn` 后瞬时出现。<br>用户原话如此；技术落地 = 车进 `carryTracker`，由原版渲染链每帧绘制 ⇒ **天然连续**（原版 `Verse.Pawn_DrawTracker::DrawPos` 本身每帧插值） | **§3.2 / §3.4**（+ [贴图流水线-设计.md](贴图流水线-设计.md)：贴图层权威） |
| **M10**（原）｜要不要接受"依赖 Harmony" | **接受**：技术路线 = **车进 `carryTracker`（渲染 + 存档免费）+ 用 Harmony 前缀干预 `ExitMap`**（用户明确选 (b)）。<br>⚠ **不是"0 Harmony"**：最终 = **3 个补丁**（`TicksPerMove` + `ExitMap` + `Kill`） | **§3.4 / §3.6 / §4.5** |
| **M11**（原）｜`Unbind` 与 `base.X()` 的顺序 | **三个挂点各自固定顺序**（`ExitMap`/`Kill` 都是**前缀里先摘车**；`PostDeSpawn` 是 `Unbind` → `base`）。原选项里"先 `base` 再 `Unbind`"（`c`）**已否**——`ExitMap` 会先销毁车 | **§9.2「排序风险」表** |
| **M12**（原）｜"倒地解绑"要不要接受"依赖 Harmony" | **第一版不挂 `Verse.Pawn_HealthTracker::MakeDowned`**（避免把补丁数推到 4）：「倒地但没死」**降级为已知缺口**（写明在 mod 说明里），**启动自检**明示该防线已降级；实测发现常见再补第 4 个补丁 | **§3.3-7 / §3.5** |
| 停放区形态 | **用自定义 `Verse.Zone`**（`Zone_SharedBikePark`）。理由：用户最初的描述就是"像划定某种区域一样划定共享单车的储存区域"。停车桩降级为**备选 / 未来扩展**（§5.3.1），**不作并列选项** | **§5.3** |
| 停放区对谁生效 | **对整理工人下指令，不对骑车人设约束**；骑车人**就地停**；**没划 Zone 时整理工什么都不做** | **§1.4 核心主张 / §5.2 / §6.2** |
| 两种停车模式选哪个 | **① 乱停乱放 + 专人整理**（② 是"① + 划了 Zone"的自然结果，不是第二套代码） | **§5.1** |
| `pxPerCell` | **128**（已冻结）。本文档**不重复**贴图规格 —— 贴图层权威是 [贴图流水线-设计.md](贴图流水线-设计.md) | **贴图流水线-设计.md**（§2.3.1） |
| Vehicle Framework / VVE | **不采用**（已评估排除：VF 官方 FAQ 说 AI 不会用载具跑图；硬依赖含 CC BY-NC-ND 的 VFE） | **§9.3** |

> ⚠ **迁移记录（append-only 纪律）**：以上 4 条原未决项（**M6 / M10 / M11 / M12**）的**当时写法与各选项**已按真值表 §6.1 的格式写入
> `tmp\doc-migration\v2-evidence-layer.md`，将由另一路工作追加进真值表 **§8 设计文档更正历史（append-only）**。
> **本节只保留当前仍未决的问题。**

| # | 问题 | 选项 | 我的建议 | 为什么现在无法定 |
|---|---|---|---|---|
| **M1** | **车有没有品质？** | (a) 无品质；(b) 有品质（普通/良好/大师…） | **(a) 第一版不做** | `Verse.ThingDefCountClass` 有 `QualityCategory quality` 字段、`Verse.RecipeDef.specialProducts` 存在，但"**自定义 `MinifiedThing` 子类 + 品质**"这条路**未确证**（内层 `Bike` 才是被 eval 的 `Thing`，品质该挂内层还是外壳没查）；而且品质会立刻引出"按品质影响车速吗"的连锁问题 |
| **M2** | **车的质量（`Mass`）定多少** | 见 §6.4 对照表 | **7 kg → 一次带 5 辆** | 取决于 M5（整理工一次带几辆）和"整理工应该是专职还是兼职"。质量同时决定"能不能被征召的小人搬走" |
| **M3** | **第一版要不要做"征召自由驾驶"？** | (a) 只能"骑到某处"就下；(b) 可以在车上自由移动 | **(a)** | 用户自己指出"征召骑车不符合只能 A→B 的逻辑"。**自由驾驶**意味着"骑行 Job 永不结束"，会引出"何时下车的判定"、"征召解除后的收尾"、"车是否要每 tick 跟随"——**这是第二版的功能** |
| **M4** | **停放区里"特殊停放方式"具体是什么？** | (a) 只是"不被整理工再扫到"+ 不乱堆；(b) 视觉上排列整齐（按格对齐 / 统一朝向）；(c) 变成"堆"（多辆压成一格） | **(a) 第一版**；(b) 第二版 | 用户原话是"以一种比较特殊的方式停在那儿"，**这句话本身没有具体化**。⚠ 要做 (b)/(c) 就必须遵守 §5.3 的纪律：**`Verse.Zone::get_Cells` 会先 `GenList.Shuffle(cells)`**，排序 / 堆叠逻辑一律自己维护有序副本 |
| **M5** | **整理工的搬运是"一次多辆"还是"一次一辆"？** | (a) 多辆（背包路线，§6.4）；(b) 单辆（简单，但整理工整天在跑） | **(a)** | 用户说"不用三轮车"，但**没说"整理工也不许一次拉一堆"**。多辆 = 更像现实、更快；单辆 = 代码小一半 |
| **M7** | **车能不能被原版 Haul 任务搬走？** | (a) 可以（`alwaysHaulable = true`）；(b) 禁止（`<alwaysHaulable>false</alwaysHaulable>` + 自己的 WorkGiver 才能搬） | **(b)** | 原版 `RimWorld.MinifiedThing` def 是 `alwaysHaulable = true`【原版XML确证】，会被**原版搬运工**搬去储存区——那会与"整理工"抢活、且车会被搬进仓库。⚠ "禁止搬运后自己的 WorkGiver 还能不能搬"**未确证**，需要实测 |
| **M8** | **`naturalPriority` 取多少 / 要不要 `alwaysStartActive`？** | — | `320` / 第一版 `false` + 文档提示玩家手动开 | 取决于 §9.1-9（`relevantSkills` 空时的返回值未确证） |
| **M9** | **要不要"禁止骑车人进入战斗"？** | (a) 允许（骑车冲上去）；(b) 玩家按 D 征召时自动下车；(c) 敌方出现时自动下车 | **(a) 第一版** | 与 M3 耦合；`playerInterruptible = false`（§4.4）决定"按 D 不会下车"，所以 (b) 需要额外逻辑 |

> **原 M6 / M10 / M11 / M12 的编号在本节已作废**（它们已成已决项，见上表）；**不再新启用这些号**，以免与历史文档对不上。

---

## 附：符号全名与证据去哪儿查

> **符号全名 / 签名 / 可覆写性 / 数值真值** → 一律查 [共享单车-机制真值表.md](共享单车-机制真值表.md)：**§1** 命名空间真值、**§2** 已确证符号、**§3** 明确"不存在"、**§4** 取证工具约束、**§5** 待确证清单、**§6** 维护约定、**§7** 工具缺陷登记。
> **贴图规格**（`pxPerCell = 128` / `texPath` 写法 / 方向后缀 / 判级） → [贴图流水线-设计.md](贴图流水线-设计.md)（**本文档不重复**）。
> **更正历史 / 反例** → 真值表 **§8 设计文档更正历史（append-only）**；本次交接的原始文件 = `tmp\doc-migration\v2-evidence-layer.md`。
> **测试操作定义**（能力名 / 前置 / 后置 / 护栏） → [自动化测试-变化驱动设计.md](自动化测试-变化驱动设计.md)。

---

## 变更记录（append-only）

| 日期 | 变更 | 说明 |
|---|---|---|
| 2026-10-13 | **结构重排 + 冲突修复** | ① 全文跨文档引用**改为节号**（原行号引用已失效 4 次）；② 证据层 / 更正历史**整体迁出**（→ 真值表 §8，交接件 `tmp\doc-migration\v2-evidence-layer.md`），本文不再内联反例；③ 修掉"子代理会静默读错"的 5 处与 A 类真冲突 14 处；④ 用户已拍板的 5 项决定（骑行贴图跟随 / `carryTracker` + Harmony 前缀 / 自定义 Zone / `pxPerCell = 128` / 不采用 VF-VVE）写进正文，并从 §10 未决问题移出；⑤ Harmony 补丁数由"1 个"更正为 **3 个**（`TicksPerMove` + `ExitMap` + `Kill`），并说明为什么不走 Pawn 子类；⑥ §6.5 的"`Verse.Thing` 不实现 `ILoadReferenceable`"是**事实错误**，已更正为"`Scribe_References.Look<Verse.Thing>` 是原版标准做法（80 处先例）"；⑦ §10 的"M1…M9"编号声明更正为实际范围 |
| 2026-10-13 | **行尾转回 LF-only** | 本文件此前为 **CRLF（整个文件）**，是写入通道缺陷（`WriteAllLines` 顺手改行尾）的产物——本仓库 47 份文档里它是唯一一份 CRLF。**同日转回 LF-only**（内容未改），使 git diff 恢复可读、并满足规程 R9 的"行尾"不变量。<br>**实测字节数：150,273 B（CRLF） → 148,886 B（纯去掉 `\r` 后） → 149,070 B（本「变更记录」写入后）**；`150,273 − 148,886 = 1,387 B` = 被去掉的 **1387 个 `\r`**（含末尾那个）。<br>**行数不变**（`LF = 1385`，以 LF 结尾）；**`CR = 0`**；**无 BOM**（前 3 字节 `23 20 E5` = `# 共`）。 |
| 2026-10-14 | **配方链实测落盘（run-4）** | 本轮是**本阶段最后一次文档工作**。① **§2.3 新增「✅ 2026-10-14 实测：这条链路已跑通」块**（三条判据：产物 def / **内层唯一** / **配方路径红字 0 条**；证据路径 `tmp\bike-probe\run-4\RUN4-REPORT.md`；探针只改 1 文件 2 处并重编译 56 KB 成功；**仍未确证 2 项**一并写明）；② **§2.4 的"唯一的诚实标注"就地更正**（那句"从未运行时实测"保留作反例，并指向 §2.3 的实测块）；③ **§9.1 第 12 项**追加"已结案"标注（**原行不删**）。<br>**引用纪律**：本轮起跨文档引用**继续只用「文件名 §节号」** —— run-4 的行号与 run-1/2/3 的**互不可抄**（`Player.log` 含 3 个裸 `\r`，三套口径）。 |
