# 共享单车 · Step 0.5 地基探针方案（施工单）

> **定位**：把「会引发重构的未知量」闭环。产出**用完即弃的探针代码**：**不进正式 `Source/`、不许被后续模块 import**。
> **Gate（不过不进 Step 1）**：下面每一行都必须给出**一个实测数字或一个实测结论**，不许写"应该没问题"。
> **【状态头 · 2026-10-14 追加】**：**本文档已于 2026-10-14 跑完并冻结**（它自述是"一次性施工单，跑完就该冻结"）；**结论不在本文**，见 `tmp\bike-probe\FINAL-12-ITEMS.md`（12 项最终状态表：**已答 10 / 部分答 2 / 未答 0**）与 [docs/共享单车-机制真值表.md](共享单车-机制真值表.md) **§2.9**（§2.9.1 符号层 / §2.9.2 运行时行为层 / **§2.9.4 run-4 配方链** / §2.9.5 计数）＋ §6.4 / **§6.5** 追加清单。**正文中标注为"未确证"的条目一律以探针实测为准**；其中 **§② 表内 6 处**（`ThingRequestGroup.MinifiedThing` 能否枚举自定义 def / `MapComponentDef`+`GetCachedMapComponent` 注册方式 / `Job.checkEncumbrance` 默认值 / Harmony 入口形状 / `GenGrid.Standable` 成员名 —— 分别落在 `A2` / `A3` / `A4` / `A17` / `D` 五行的实现列与判据列）**已就地更正**，见下表之后的「2026-10-14 就地更正（6 处）」块：**原措辞保留作反例**。**正文其余部分（判据 / 执行顺序 / 取证纪律 / 不做清单）不改，作为历史施工单保留。**
> **依据**：入口 [docs/README-共享单车.md](README-共享单车.md) §③（Step 0.5 的原始定义与 Gate）；设计权威 [docs/共享单车mod-设计-v2.md](共享单车mod-设计-v2.md) §2.3／§3.2／§3.3／§3.5／§6.3／§6.4／§7.2／§7.4／§9.1／§9.4／§10；
> 符号权威 [docs/共享单车-机制真值表.md](共享单车-机制真值表.md)（§1／§2.2／§2.3／§2.7／§2.8／§3.5bis／§3.7a／§3.7b／§5／§6.1／§6.2）。**引用一律「文件名 §节号」**。
> **本文档状态**：写作时这条线**运行时实测 = 0 行**（见 [docs/共享单车mod-设计-v2.md](共享单车mod-设计-v2.md) §9.4）。本文全部"判据"都是**待跑**，不是结论。

---

## ① 探针的形态：独立最小 mod `mods/BikeProbe/`

| 项 | 取值 | 理由 |
|---|---|---|
| 目录 | **`$WS/mods/BikeProbe/`**（**不**放 `tmp/`） | ① 游戏**只**从 `<游戏>\Mods\` 与工坊加载，本工程靠 **junction** 把工作区 `mods/` 映进游戏（[docs/mod开发基础.md](mod开发基础.md) §二）；`mods/ColonyRecorder`、`mods/FoodControl` 已在这个 junction 约定里，**放这里零额外机制**。② `tmp/` 下没有 `About/`、`LoadFolders.xml` 的惯例，放那里要另造一套描述。③ 销毁同样简单：删目录 + 删 junction |
| `packageId` | **`dsh.bikeprobe`**（独立、全局唯一） | 与 `dsh.autotest` / `dsh.colonyrecorder` 同风格；发布前整目录可删 |
| junction | `cmd /c mklink /J "<游戏>\Mods\BikeProbe" "$WS\mods\BikeProbe"` | 与 `DSH_AutoTest` 同款（`scripts/verify-4-hotreload.mjs` 就是按这个约定写的） |
| 编译 | VS BuildTools 的 Roslyn `csc`：`/target:library /nostdlib+ /langversion:7.3 /optimize+ /deterministic+`，引用游戏 `RimWorldWin64_Data\Managed\` 下的 `mscorlib` / `System` / `System.Core` / `netstandard` / `UnityEngine.CoreModule` / `Assembly-CSharp` | [docs/mod开发基础.md](mod开发基础.md) §一。⚠ 沙箱里**不要用 `execFileSync`**（管道 stdio EPERM），用 `src/proc.js` 的 `capture()` |
| 内容 | **只有探针**：`About/About.xml`、`LoadFolders.xml`、`1.6/Assemblies/BikeProbe.dll`、`Defs/**`、**不写 `Patches/`**、**不做贴图**（用原版 `Things/Building/...` 占位即可） | 见 ⑧ 不做清单 |

**探针内部结构（只此四块，别扩）**：`ProbeLog.cs`（结构化日志，唯一出口）、`Probe_MapComponent.cs`、`Probe_JobDriver_Tidy.cs` + `Probe_JobGiver_Tidy.cs`、`Probe_Harmony.cs`（`ExitMap`／`Kill` 前缀 + 自检）。

---

## ② 逐项探针表（主体）

> **符号纪律**：`最小实现` 列里的类名/方法名/Deful 名**逐字符**取自真值表。真值表里查不到的，本文只写**形状**并标 **未确证**。

| 编号 | 要验证什么 | 最小实现（符号取自真值表；**未确证**的已标注） | 怎么判定通过（可观测判据） | 错了说明什么 | 预估工作量 |
|---|---|---|---|---|---|
| **A1a** | 骑手中途**被远行队带走**时，`ExitMap` 前缀能否拿到 `carryTracker` 里的车、并把它放到地上 | `Verse.Pawn::ExitMap(bool allowedToJoinOrCreateCaravan, Verse.Rot4 exitDir)`（`public virtual`，真值表 §3.7a）Harmony **Prefix**；判据 `__instance.carryTracker.CarriedThing as ProbeBike`；落地用 `Verse.Pawn_CarryTracker::TryDropCarriedThing(...)`（成员表见真值表 §2.3 相邻行／渲染报告） | 地图上车的**总数不变**（带走前后各数一次）；车**`Spawned == true`** 且在某合法格；车**没有** `Destroyed`；`Player.log` 无 `MinifiedThing with null InnerThing`／无 Harmony 异常 | 前缀没挂上（→ 车被 `ExitMap` 的 `Destroy(Vanish)` 无条件销毁）；或挂上了但摘车没定去向（→ 车变**无主对象**：`Spawned == false` 且不在任何容器里，**等同丢失**） | 0.5 天 |
| **A1b** | 同一条前缀在 **`ExitMap` 被 `callvirt` 重入**时是否幂等（商队路径会重入一次） | 同上，**不加任何"已处理"标志位**：第二次进入时 `CarriedThing` 已不是车 ⇒ 自然 no-op | 日志里 `Prefix` 命中计数 **≥ 1**、`carryTracker.CarriedThing != null 且是本车` 的真命中计数**恰好 1**；第二次进入**没有**第二次落地／第二次日志；只调用 1 次解绑逻辑 | 幂等不成立 ⇒ 会**二次销毁车**或**把车落两次**；一旦商队路径变了（非 Pawn 分支）就会偶发丢车 | 含在 A1a |
| **A1c** | 骑手**中途死亡**（`Kill` 前缀）时车不能丢、不能随尸体进"不存在"状态 | `Verse.Pawn::Kill(Nullable<Verse.DamageInfo> dinfo, Verse.Hediff exactCulprit = null)`（`public virtual`，真值表 §3.7a）Prefix：**先抓 `bike`／`rider` 引用 → 幂等摘车落地 → `return true` 放行原版** | 小人死后：地图上车的总数不变、车 `Spawned == true`、尸体／装备处理完之后车**仍在地上**；读档后车**没有被深存进尸体一侧** | 挂错位置（挂 `Verse.Pawn_HealthTracker::SetDead()` —— 它是 `public` **非虚**、函数体只有 4 条指令、**不触发任何解绑**，真值表 §3.5bis／§3.7b）⇒ "小人死了但车还挂着"，资源永久泄漏且**只偶发** | 0.5 天 |
| **A2** | 地面上的车是**建筑形态**时，整理工能不能"收起 → 进背包 → 放下"整条链走通 | `RimWorld.MinifyUtility::Uninstall(this Verse.Thing th)`（真值表 §2.3）；枚举用 `Verse.ThingRequestGroup.MinifiedThing = 39`（真值表 §2.1 枚举真值）；进背包用 `Verse.AI.Toils_Haul::TakeToInventory(...)`（真值表 §2.7）；放下用 `Verse.GenPlace::TryPlaceThing(...)`（真值表 §2.4）或 `Verse.GenDrop::TryDropSpawn(...)`（**只有 1 个 8 参重载**，真值表 §3.5bis）。⚠ **`ThingRequestGroup.MinifiedThing` 能否枚举到自定义 `SharedBikeMinified` 未确证** | 逐段打点：`MinifyUtility.Uninstall` 返回值非 null → `map.listerThings.ThingsInGroup(MinifiedThing)` 计数 **+1** → 背包 `Verse.Pawn_InventoryTracker::innerContainer` 计数 +1 → 放下后地面**建筑形态**数 +1、背包计数 −1；全程无红字 | ① 建筑被 `Uninstall` 后**枚举不到**（`thingClass` 不一 / 不在该组）⇒ 整理工**永远找不到活**（静默失效）；② `Uninstall` 不是整理工该走的正路 ⇒ 整条整理链要重设计 | 0.5 天 |
| **A3** | `Verse.MapComponent` 子类**会不会被引擎自动实例化**；`FinalizeInit`／`MapGenerated` 哪个时机能安全建索引 | `Verse.MapComponent`（`kind=class abstract`，`FIELD public Verse.Map map`，`CTOR MapComponent(Verse.Map map)`，8 个 `public virtual` 无参 void：`MapComponentUpdate`／`MapComponentTick`／`MapComponentOnGUI`／`MapComponentDraw`／`ExposeData`／`FinalizeInit`／`MapGenerated`／`MapRemoved`，全 `0x01c6`，真值表 §2.8／§3.7a）。**注册方式未确证**：按原版形状写一个 **`MapComponentDef`（`<mapComponentClass>`）+ 地图 `<components>` 引用**，并**在候选时机各打一条日志**（`ExposeData` / `FinalizeInit` / `MapGenerated` / `MapComponentTick` 首帧） | 4 条时机日志的**出现顺序与次数**被记录；在地图跑起来后 `map.GetComponent<Probe_MapComponent>()`（或 `GetCachedMapComponent` 一类，**方法名未确证**）**非 null**；索引表条数 == 地图上车的实际条数 | 注册写错 ⇒ **registry 永远 null** ⇒ 加速（`TicksPerMove` Postfix 里查不到车）与"最近的车"查询**全部静默失效**：编译过、不报错、功能没有（真值表 §5 第 29 项原文承认"注册/生命周期时序**没追**"） | 0.5 天 |
| **A4** | 整理 Job 的**单辆** toil 形状（一次一辆即可，批量留第二版） | `Verse.AI.JobDriver` 覆写 `protected abstract IEnumerable<Verse.AI.Toil> MakeNewToils()` 与 `public abstract bool TryMakePreToilReservations(bool errorOnFailed)`（真值表 §2.8／§3.7a）；抄模板 `Verse.AI.JobDriver_TakeInventory`／`RimWorld.JobDriver_TakeCountToInventory`、`RimWorld.WorkGiver_Scanner`／`RimWorld.WorkGiver_Haul`（真值表 §1.2／§1.3／§2.2）；预约 `Verse.AI.ReservationUtility::CanReserve(...)` | 把 toil 名与顺序打出来（去取车 → 收起 → 进背包 → 走到停放区 → 放下）；每段结束有断言；`Job.checkEncumbrance` 的实际取值被记录（⚠ **默认值未确证**，真值表 §5 第 14 项） | Job 形状与设计不符 ⇒ 整理工卡在第一个 toil（表现为"派了活但不动"，**无声失败**） | 1 天（与 A2／C3 共用同一 Job） |
| **A9** | `ThinkTreeDef` 的 **XML 结构**（`insertTag`／`insertPriority` 的取值与根节点写法）正确 ⇒ 自发骑行决策真的被触发 | 一棵**纯 XML** 的 `Verse.ThinkTreeDef`（字段 `thinkRoot`／`insertTag`／`insertPriority`，来源 [docs\短途载具与共享单车-技术调研.md](短途载具与共享单车-技术调研.md) §2.4，**未进真值表 §2 ⇒ 视为未确证**）：`<insertTag>Humanlike_PostMain</insertTag>` + `<thinkRoot Class="Probe_JobGiver_Tidy" />`。`JobGiver` 侧用 `Verse.AI.ThinkNode_JobGiver::TryGiveJob(Verse.Pawn)`（protected abstract，真值表 §3.7a），**只打日志 + 恒返回 null**，先只验"被调到" | 候选 `insertPriority`（0／100／500）**各跑一遍热重载**，日志里记得下"`TryGiveJob` 被调到"的次数与它拿到的 `priority` 顺序；**判通过 = 至少一个取值能让 `TryGiveJob` 在殖民者身上被周期调用** | 结构写错 ⇒ **骑行决策永不触发**：编译过、游戏不报错、小人永远不骑车（模板静默失效）；`insertPriority` 取错 ⇒ 被原版/别的 mod 的选择永远压住 | 0.5 天（**纯 XML，零重启**） |
| **A17** | **Harmony 注入入口**能否挂上，以及"补丁是否挂上"的**自检**是否有效（v2 §3.6 明确要求） | 一个 `HarmonyLib.Harmony` 实例 + `PatchAll` 入口（**入口形状未确证**：`Harmony` 的 `Id`／`PatchAll(Assembly)` 参数面不在真值表里）；自检按 v2 §3.6-① 的方向：拿到 3 个目标的 `PatchInfo` 并断言 `Prefixes.Count >= 1`（**方法名未确证**，写探针时以实际 API 为准） | 启动后日志**点名 3 个补丁各自"已挂上/未挂上"**；故意把一个目标类型名写错 → 自检**必须报"未挂上"**（阴性对照，防"自检本身静默失效"） | 挂不上或自检无效 ⇒ **加速与两条解绑防线同时静默失效**：游戏能跑、无报错、车该丢还是丢 | 0.5 天 + **1 次重启** |
| **C1** | `MinifiedBike.PostMake` 装内层后，**造出来的物品是否正常工作**（真值表 §5 第 12 项，v2 §2.4 唯一的诚实标注） | `public class MinifiedBike : RimWorld.MinifiedThing`，覆写 **`Verse.Thing::PostMake`（`public virtual`）**：`base.PostMake()` + `InnerThing = Verse.ThingMaker::MakeThing(SharedBike, Steel)`（真值表 §2.3）。Def 走 `<ParentName>MinifiedThing</ParentName>` + `<thingClass>` 指向它 | 造一个出来：`InnerThing != null`、`get_Graphic()` 不抛、**第一 tick 之后仍存在**；`Player.log` **没有** `MinifiedThing with null InnerThing. Destroying.`、**没有** `Assigned 2 things to the same MinifiedThing`；再把它落成建筑 → 建筑形态正常 | 装内层这条路不成立 ⇒ **"在锻造台造出物品形态的车"整个机制崩**（原版**没有任何** `RecipeDef` 产出 `MinifiedThing` 的先例，真值表 §3）；错法的后果是**造出来就自毁 + 刷红字**（`Tick()` 的 null 内层自毁分支） | 0.5 天（**纯 C#，含在 A17 那次重启里**） |
| **C2** | 骑行 Job 的**上下车开销 `O`**（真值表 §5 第 12 项同族／v2 §7.2、§7.4） | 最小骑行 Job：`JobDriver` 的 toil 用 `Verse.AI.ToilCompleteMode.Instant`（= 1，真值表 §2.8；字段真名 **`Verse.AI.Toil::defaultCompleteMode`，public 字段不是属性**）；上车 `bike.DeSpawn(DestroyMode.Vanish)` → `pawn.carryTracker.TryStartCarry(bike)`；下车 `TryDropCarriedThing(...)` | 用游戏 tick 打点：从"进入上车 toil"到"下车 toil 结束"的 **tick 差**，除以 60 得**秒**；**给出一个数**（例如 `O = 0.05 s`）并说明它是**上下车总开销**还是单程 | `O` 决定 v2 §7.2 的阈值：`O = 2 s → D > 22`、`O ≈ 0.3 s → D > 12`（**"值不值得骑"翻倍变化**）。测不出来 ⇒ `v_b = 6.0` 这个平衡取值没有依据 | 0.5 天 |
| **C3** | WorkGiver 派活后整理工**是否真能一次背 5 辆车**（真值表 §5 第 13 项，v2 §6.4） | 与 A2／A4 同一个 Job 的循环：`RimWorld.MassUtility::FreeSpace(Pawn)` 与 `RimWorld.StatDefOf::Mass` 比较后决定还能不能再塞一辆（**`Capacity = Pawn.BodySize × 35`，函数体内没有任何 `ldsfld RimWorld.StatDefOf::*`**，真值表 §2.7）；车的 `Mass = 7` ⇒ 1.0 `BodySize` 理论上 5 辆 | 让整理工从地上连收 N 辆：背包 `innerContainer` 的**实际计数**（要 ≥ 1，理想 = 5）；每辆进包前后的 `FreeSpace` 被记录；若只到 1～2 辆，把**卡住的判据**（哪个比较先失败）打出来 | 只到 1 辆 ⇒ v2 §6.4 的"整理工一次背 5 辆"是**空设计**，要么改车重、要么改成"手抱 + 多趟"，**JobDriver 要重写** | 含在 A4 |
| **D** | 骑行时**车的贴图跟着小人连续移动**、层级正确（不被小人挡住）、**下车后车出现在合法格** | 上车 = `pawn.carryTracker.TryStartCarry(bike)`（真值表 §2.7）；姿态偏移用 **`Verse.AI.JobDriver::ModifyCarriedThingDrawPos(ref UnityEngine.Vector3 drawPos, ref bool flip)` —— ⚠ 真值表里没有这一行，标"未确证"**（渲染取证 [tmp\il-evidence-bike\render\REPORT.md](../tmp/il-evidence-bike/render/REPORT.md) 判它是 `public virtual`、默认 `return false`）。若它确实可覆写：在骑行 JobDriver 里返回 `true` 并自己写 `drawPos`（**只改 x/z，`y` 别动**） | **截图序列**（≥ 3 张、间隔 ≥ 1 秒，同一个小人在移动中）：车**始终贴在小人身上**、无跳格、无残影；层级 = 车画在小人之上（若被挡，说明必须覆写 `ModifyCarriedThingDrawPos` 或用 `CompPushable` 那条原版分支）；下车截图：车在**合法格**（`Verse.GenGrid::Standable` 一类判定，**具体成员未确证**）、没卡墙里 | 若"原版自动画"这条不成立（例如被 `DrawEquipmentAndApparelExtras` 顶掉），则渲染必须自己接管 ⇒ **多一个 Harmony 补丁**，且"渲染免费"这个卖点不成立 | 0.5 天 |

**覆盖核对**：A（重构级 4 项）→ A1a／A1b／A1c／A2／A3／A4 全覆盖；B（静默失效 3 项）→ A9／A17／A3 全覆盖；C（§9.4 三件）→ C1／C2／C3 全覆盖；D（骑行表现）→ D 覆盖。

**2026-10-14 就地更正（6 处）**（依据 = 探针三轮实测；**下列"原措辞"逐字保留作反例，每条给更正后的结论**；更正文本身不是"未确证"了）：

| # | 出处（本文 §② 表内） | 原措辞（**保留作反例**） | 2026-10-14 更正后的结论 | 证据（文件 §节号） |
|---|---|---|---|---|
| 1 | `A2` 行的"最小实现"列 | "⚠ **`ThingRequestGroup.MinifiedThing` 能否枚举到自定义 `SharedBikeMinified` 未确证**" | **能**。`Verse.ThingListGroupHelper::Includes` 的第 39 号分支 = `ldtoken RimWorld.MinifiedThing` → `ldfld Verse.ThingDef::thingClass` → **`System.Type::IsAssignableFrom`** ⇒ 判据是 **`typeof(RimWorld.MinifiedThing).IsAssignableFrom(def.thingClass)`**；且探针实测**枚举计数确实 +1** | [共享单车-机制真值表.md](共享单车-机制真值表.md) §2.9.1；`tmp/bike-probe/run-1/RUN-REPORT.md` §2-A2、`tmp/bike-probe/run-2/RUN2-REPORT.md` §4-A2 |
| 2 | `A3` 行的"最小实现"列 | "**注册方式未确证**：按原版形状写一个 **`MapComponentDef`（`<mapComponentClass>`）+ 地图 `<components>` 引用**" | **两者都不存在** ⇒ 不需要任何 XML / Def 注册。真机制 = **`Verse.Map::FillComponents()` 走 `GenTypes::AllSubclassesNonAbstract(typeof(Verse.MapComponent))` + `Activator.CreateInstance(t, new object[]{ map })`** ⇒ **非抽象 `Verse.MapComponent` 子类 + `public ctor(Verse.Map)` 就自动实例化** | [共享单车-机制真值表.md](共享单车-机制真值表.md) §2.9.1（`Verse.MapComponent` 行、~~`MapComponentDef`~~ 行）；`tmp/bike-probe/evidence/01-Map-FillComponents-IL.txt` |
| 3 | `A3` 行的判据列 | "`map.GetComponent<Probe_MapComponent>()`（或 `GetCachedMapComponent` 一类，**方法名未确证**）" | **真名 = `Verse.Map::GetComponent<T>()`**（另有 `GetComponent(System.Type)`）；**`GetCachedMapComponent` 不存在**（调研文档里那个是 Vehicle Framework 自己的扩展方法）。探针实测 `map.GetComponent<Probe_MapComponent>()` **non-null**、`components.Count=12` | 同上一行；`tmp/bike-probe/evidence/12-Map-members.txt` |
| 4 | `A4` 行的判据列 | "`Job.checkEncumbrance` 的实际取值被记录（⚠ **默认值未确证**，真值表 §5 第 14 项）" | **默认值 = `false`**（`Verse.AI.Job` 的 7 个 ctor 里**零** `stfld checkEncumbrance` ⇒ CLR 默认）；探针**读回默认 `False`**，置 `True` 后也能读回 ⇒ **我们的整理 Job 必须自己置 `true`**。⚠ 真值表 §5 第 14 项的**另一半**（"原版哪些派发点置 true"）**仍未被扫** | [共享单车-机制真值表.md](共享单车-机制真值表.md) §2.9.1（`Job::checkEncumbrance` 行）、§5 第 14 项；`tmp/bike-probe/evidence/04-Job-ctor-IL.txt` |
| 5 | `A17` 行的"最小实现"列 | "（**入口形状未确证**：`Harmony` 的 `Id`／`PatchAll(Assembly)` 参数面不在真值表里）；……（**方法名未确证**，写探针时以实际 API 为准）" | **`HarmonyLib.Harmony.GetPatchInfo` 是 `public static Patches GetPatchInfo(MethodBase)`**（原始元数据 `flags=0x0096` = public+**Static**；对无补丁的方法返回 `null`，**不是错误**）。⚠ **`PatchAll()` 在没有 `[HarmonyPatch]` 注解可扫时静默不挂任何补丁、不抛异常** ⇒ **独立判定手段 = `Harmony.GetPatchedMethods()`（实例方法）**，不要靠"PatchAll 没报错" | [共享单车-机制真值表.md](共享单车-机制真值表.md) §2.9.1（`GetPatchInfo` 行、`PatchAll` 静默失效行）；`tmp/bike-probe/evidence/09-Harmony-members.txt` |
| 6 | `D` 行的"判据"列 | "车在**合法格**（`Verse.GenGrid::Standable` 一类判定，**具体成员未确证**）" | **成员名确证 = `Verse.GenGrid.Standable(this Verse.IntVec3 c, Verse.Map map)`**（扩展方法；命名空间就是 `Verse.GenGrid`）；兄弟 `Walkable` / `WalkableBy` / `WalkableAny` / `StandableBy` / `StandableByAny` / `Impassable` / `InBounds` / `OnEdge`。探针下车行打 `posStandable=True` 实测一致 | [共享单车-机制真值表.md](共享单车-机制真值表.md) §2.9.1（`GenGrid.Standable` 行）；`tmp/bike-probe/evidence/06-GenGrid-members.txt` |

> **顺带更正一条与 §② 表无关、但同属"本文过期口径"的**：§④「⚠ 起点事实」与 §⑤ 第 3 条的"**到目前为止这条线 0 行运行时实测**"**已过期**（探针三轮实测 + run-4 配方链实测之后，真值表自 §2.9 起确有【运行时实测】行）；该句现在**只对产品代码 `Source/` 成立**。**本节其余内容不改。**

---

## ③ 执行顺序：目标是把重启压到 **≤ 2 次**

> 铁律（[docs/mod开发基础.md](mod开发基础.md) §二）：**改 C# 必须重启游戏（DLL 被进程锁住）**；**改 XML 执行 `Actions\Hot reload Defs` 亚秒生效、零重启**。所以顺序按"XML 先、C# 一次写齐"排。

| 阶段 | 做什么 | 重启 | 依赖 |
|---|---|---|---|
| **R0 部署** | 建 junction、编译 `BikeProbe.dll`、把 `dsh.bikeprobe` 加进 `profiles/bike/Config/ModsConfig.xml` 的 `activeMods`（**本项目从不自动写 ModsConfig**，要手工加）、`node src/cli.js start --profile bike` | **第 1 次**（必须） | A17／C1 的 C# 必须在这次重启**之前**写齐 |
| **X1 纯 XML** | 跑 **A9**：`insertPriority` 取 0／100／500 各改一次 + `Hot reload Defs` 各一次 | 0 | 无 |
| **X2 纯 XML** | 跑 **A2 的枚举一半**：确认 `Verse.ThingRequestGroup.MinifiedThing` 能不能枚举到自定义 `SharedBikeMinified` | 0 | 无 |
| **R1 主体** | 一次性验 **A17／C1／A3／A4+C3／A2／C2／D／A1a-b-c**：先看启动自检（A17）→ 造一辆（C1）→ 看 registry 是否非 null 与时机日志（A3）→ 派整理活（A2／A4／C3）→ 下一次骑行令并计时（C2）＋截图（D）→ 最后做"远行队带走"与"debug 杀死骑手"两条（A1a／A1c） | 0 | R0 |
| **R2 兜底** | **只在 A17 自检报"未挂上"或 A3 报 `null` 时**才发生的修 C# 重启 | 最多 1 次 | R1 |

**⇒ 目标：1 次必需重启 + 最多 1 次兜底重启 = ≤ 2 次。**

---

## ④ 环境前提

| 项 | 内容 |
|---|---|
| 实例 | **`profiles/bike`**（干净测试实例，`activeMods` **13 个**：Harmony／RimWorld + 5 DLC／XmlExtensions／HAR／Ratkin／RimBridgeServer／dsh.autotest／CharacterEditor）；桥接端口历史值 **5174** |
| 起 | `node src/cli.js start --profile bike`（隔离存档目录 = `profiles/bike`，**不碰主力存档**）；探针 mod 的 `packageId` 先手工加进 `profiles/bike/Config/ModsConfig.xml` |
| 日志 | `profiles/bike/Player.log`（`start` 会把上一份改名为 `Player-<时间戳>.log`）；结构化读用 `rimbridge/list_logs` |
| ⚠ 死亡检测 | **`profiles/bike` 曾自行退出**（跑约 **48 分钟**后；该坑 2026-10-26 随 STATUS 收编迁进 `.dsh\skills\rimworld-mod-pipeline\环境陷阱.md`）。**每次长验证必须带死亡检测**：用 PID + `process.kill(pid, 0)` 判活（**`tasklist` 在受限沙箱下返回 Access denied，会被误读成"进程不存在"**，[docs/mod开发基础.md](mod开发基础.md) §七-4）；进程一没就**立刻中止本轮并记下"死于哪一步"**，别把"游戏退出了"当成"验证通过" |
| ⚠ 起点事实 | **到目前为止这条线 0 行运行时实测**（[docs/共享单车mod-设计-v2.md](共享单车mod-设计-v2.md) §9.4、真值表文首）。本文所有判据都**没跑过** |
| 截图 | `rimworld/take_screenshot`（截的是**游戏渲染画面**，不是桌面），文件名落在游戏截图目录，跑完**拷进 `runs/`** |

---

## ⑤ 取证纪律（每项都要留可复核证据）

1. **证据落盘**：截图 → `runs/bike-probe-<时间戳>/shots/`；结构化返回值（时机日志、计数、tick 差）→ 同目录 `*.jsonl`；`Player.log` 片段 → 同目录 `*.log`；**`runs/` 与 `tmp/` 之外不落盘**。
2. **一行证据一行结论**：每项写清 `编号 → 命令 → 原始输出路径 → 结论`。**追不到证据文件的结论不许写进真值表**（真值表 §6.1 第 5 条）。
3. **区分【运行时实测】与【推测】**：本文写"推测"的地方（例如 D 行"原版自动画"、A2 枚举可行性）**只有拿到截图/日志才准升级**；升级时按真值表 §6.1 第 4 条附**实测报告路径 + 截图/日志文件**，否则一律按旧的 `【IL确证】` 写。
4. **阴性对照**：A17 的自检必须**故意写错一个目标**看它会不会报"未挂上"；A9 的 `insertPriority` 必须**至少试 3 个值**。判据"0 命中"之前先做阳性对照（真值表 §4.4.3-R8）。
5. **落盘后自检（规程 R9）**：本文件与后续证据文件写入后**立即复读**——行数、**逐字符遍历 `[char]124` 数表格列**（**不要用 `split('|')`**）、确认 **LF-only 无 BOM**。

---

## ⑥ 探针的销毁 + 结论写到哪

**销毁（可 100% 清理，不影响正式 `Source/`）**：
1. `Remove-Item -Recurse $WS\mods\BikeProbe`（**编码产物与源码一起删**）；
2. `cmd /c rmdir "<游戏>\Mods\BikeProbe"` —— **删 junction 用 `rmdir` 不用 `del`**，否则会穿透删掉工作区内容；
3. 从 `profiles/bike/Config/ModsConfig.xml` 的 `activeMods` 去掉 `dsh.bikeprobe`；
4. 保留 `runs/bike-probe-<时间戳>/`（**证据不删**）；
5. ⚠ 本仓库当前**没有 `Source/` 目录**（v2 §2.2 的 `Source/1.6/` 尚未创建）⇒ 探针**物理上无处可被 import**；写正式代码时把"探针类名"列入**禁止引用清单**。

**结论写进哪（建议）**：
- **主落点 = 真值表新开一节 `§8 设计文档更正历史（append-only）`**（v2 §2.4／§9.2 已多次引用它，但真值表目前**只到 §7**，那个落点**至今不存在** ⇒ 本轮补上）。**理由**：① 真值表是**符号权威**，探针的核心产出正是"符号与命题的真伪"，放这里下次写代码的人**一定**会看到；② 它已经是 append-only 台账，天然支持"今天推翻昨天的结论"；③ 它还有 **§6.1／§6.2 的追加规程**（一行一证据、降级不删除），别的文档没有。
- **同步动作**：真值表 **§5** 用 §6.2 第 6 条的格式给第 12／13／29 项标 `→ 已确证/已推翻，见 §8`（**不要删原行**）；v2 **§9.4** 那三件各补一个指向 `runs/bike-probe-<时间戳>/` 的实测数字。
- ⚠ **不建议**把结论只写进本文档：本文是"一次性施工单"，跑完就该冻结，不是台账。

---

## ⑦ 不做清单（明确不做）

1. **不实现产品功能**：不写正式 `Source/1.6/**`、不做 Zone_SharedBikePark／Designator_ZoneAdd、不做 `Patch_TicksPerMove`、不做 Gizmo 征召骑车。
2. **不为好看加贴图**：不放任何 `Textures/`，用原版占位图；D 行只验"跟着走 + 层级 + 落地"三件事。
3. **不碰共享／停放区**：不写预约仲裁、不做空间分桶性能优化、不碰 Zone／Area 语义。
4. **不做批量整理**：C3 只验"能不能带 5 辆"，不验"一次带 5 辆的完整调度"。
5. **不改任何现有文档的正文**：探针结论只在真值表里 **append**（§8 + §5 标注）与 v2 §9.4 补数字；其余文档一律不动。
6. **不做 IL 取证**：符号问题一律回查真值表；真值表没有的标记"未确证"，**不在本轮重新反编译**。
7. **不顺手修别的线**：不碰 `src/colonyrecorder/`、`mods/ColonyRecorder`、`mods/FoodControl`。
