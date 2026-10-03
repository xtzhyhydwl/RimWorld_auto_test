# 建造任务选取顺序 —— IL 只读取证确证

> 目的：验证承重假设「**小人执行建造命令的顺序 = 放置蓝图（Blueprint）的顺序，与位置距离无关**」。
> 手段：只读 IL/元数据取证（不执行游戏代码、不启动/停止游戏、不反编译整个 DLL）。
> 结论标记：【IL 确证】= 有 IL/元数据片段直接支撑；【数据确证】= 有 Core XML 支撑；【推测】= 无直接证据。

---

## 0. 结论速览（TL;DR）

**承重假设不成立。**【IL 确证】+【数据确证】

实际机制是：

> **以执行工匠的当前位置为起点，按「区域（Region）逐跳广度优先」由近及远搜；一旦在某个区域里找到第一个合法候选，就立刻停止搜索，只在这一个区域内比较欧氏距离取最近。全程没有任何地方读"放置顺序"，也没有任何地方按放置顺序/工作量排序。**

放置顺序唯一可能产生影响的地方是：

1. **距离恰好完全相等**时的并列（`ProcessThing` 用严格小于比较，先遇到者保留）；
2. 而"先遇到"的顺序 = 区域内的 `ListerThings` 列表顺序 = **Thing 生成顺序**；
3. ⚠ 而**蓝图变框架是新建 Thing**（`Blueprint.TryReplaceWithSolidThing` → `Blueprint_Build.MakeSolidThing` → `ThingMaker.MakeThing(frameDef)`），所以那个列表顺序是"**框架生成顺序**"，**不是蓝图放置顺序**。

即：**连最弱的那个 tie-break 都不是"蓝图放置顺序"**。

---

## 1. 取证环境（可复现）

| 项 | 值 |
|---|---|
| 程序集 | `D:\SteamLibrary\steamapps\common\RimWorld\RimWorldWin64_Data\Managed\Assembly-CSharp.dll` |
| 版本 | `Assembly-CSharp, Version=1.6.9676.17735` |
| SHA256 | `5CF1B5BE399D5B1C9C56CA72C9D35B4ECF307FEACF5859D04AC5A1AA5926356A` |
| 大小 / 时间 | 15,777,280 B / 2026-07-01 08:11:34 |
| 依赖解析目录 | 同上 `Managed` |
| 工具 | `tools/asmdump/AsmDump.exe`（元数据）、`tools/asmdump/IlDump.exe`（IL + `--usages`） |
| 原始输出（本轮全部证据，未覆盖任何既有文件） | `tmp/il-evidence/*.txt` |

复现命令（`$M` = 上面 Managed 目录，`$A = "$M\Assembly-CSharp.dll"`）：

```powershell
# 成员清单（np = 含非 public）
.\tools\asmdump\AsmDump.exe $A $M "=RimWorld.WorkGiver_ConstructFinishFrames|=RimWorld.WorkGiver_Scanner" "*" np
# 全程序集找某个方法的覆写者（末位 np，成员过滤器只影响打印、类型头全会打）
.\tools\asmdump\AsmDump.exe $A $M "*" "PotentialWorkThingsGlobal" np
# 单方法 IL
.\tools\asmdump\IlDump.exe $A $M "RimWorld.JobGiver_Work" "*"
# 某字段的读写点
.\tools\asmdump\IlDump.exe --usages $A $M "workDone" "RimWorld.Frame"
```

**诚实声明**：本轮**没有做任何游戏内实测**（游戏在运行，按纪律不驱动桥接、不停/启游戏）。
所以本文所有"行为结论"都是 **IL 层面的强证据**，不是运行时观测；文末给出了判别性实测设计。

---

## 2. 逐条结论与证据

### 结论 1【IL 确证】`PotentialWorkThingsGlobal` 基类返回 `null`；**所有建造类 WorkGiver 都没有覆写它**

`RimWorld.WorkGiver_Scanner::PotentialWorkThingsGlobal(Pawn)` 的完整函数体只有两条指令：

```
--- IEnumerable<Verse.Thing> RimWorld.WorkGiver_Scanner::PotentialWorkThingsGlobal(Verse.Pawn pawn)  locals: (none)
  IL_0000: ldnull
  IL_0001: ret
```

全程序集扫「谁声明了 `PotentialWorkThingsGlobal`」，命中的类型有约 50 个
（`WorkGiver_Breastfeed` / `CleanFilth` / `DeconstructForBlueprint` / `Haul` / `Miner` / `Repair` / `Tend` / `Strip` …），
**其中没有任何一个 `RimWorld.WorkGiver_Construct*`**。

这不是"没扫到"：同一次输出里这些类型头是**存在**的（证明类型被成功 dump，不是加载失败被跳过）：

```
### TYPE RimWorld.WorkGiver_ConstructDeliverResources
### TYPE RimWorld.WorkGiver_ConstructDeliverResourcesToBlueprints
### TYPE RimWorld.WorkGiver_ConstructDeliverResourcesToFrames
### TYPE RimWorld.WorkGiver_ConstructFinishFrames
### TYPE RimWorld.WorkGiver_ConstructAffectFloor
...
### TYPE RimWorld.WorkGiver_Scanner
    METHOD public IEnumerable<Verse.Thing> PotentialWorkThingsGlobal(Verse.Pawn pawn)
```

→ **建造类走的是"半径 / 区域扫描"这条路，而不是"全局集合适配"那条路。**
（对照：`WorkGiver_DeconstructForBlueprint` 就覆写了它，还带 `+<PotentialWorkThingsGlobal>d__2` 迭代器状态机 —— 建造类连迭代器都没有。）

---

### 结论 2【IL 确证】`WorkGiver_ConstructFinishFrames` 的成员、父类、以及它**没有**覆写什么

元数据（`AsmDump`，含非 public）：

```
### TYPE RimWorld.WorkGiver_ConstructFinishFrames
    kind=class  base=RimWorld.WorkGiver_Scanner          ← 父类是 WorkGiver_Scanner
    PROP Verse.AI.PathEndMode PathEndMode { public get; }
    PROP Verse.ThingRequest PotentialWorkThingRequest { public get; }
    CTOR public WorkGiver_ConstructFinishFrames()
    METHOD public Verse.AI.Job JobOnThing(Verse.Pawn pawn, Verse.Thing t, bool forced = False)
    METHOD public Verse.Danger MaxPathDanger(Verse.Pawn pawn)
```

它**只**声明了这 4 个成员。以下全部**没有覆写**（因此用基类默认）：

| 成员 | 基类默认（IL 确证） | 对建造意味着 |
|---|---|---|
| `PotentialWorkThingsGlobal` | `ldnull; ret` → null | 不做全局集合，退回区域扫描 |
| `HasJobOnThing` | `return JobOnThing(...) != null` | 只是包装 |
| `Prioritized` | `ldc.i4.0; ret` → **false** | **不走"按优先级排序"分支** |
| `GetPriority(pawn, TargetInfo)` | `ldc.r4 0; ret` → 0 | 优先级恒为 0 |
| `AllowUnreachable` | `ldc.i4.0; ret` → false | 走"要求可达"分支 |
| `MaxRegionsToScanBeforeGlobalSearch` | `ldc.i4.m1; ret` → **-1** | 见结论 4，决定"允许全局兜底" |
| `PathEndMode` | `ldc.i4.2` → `Touch` | 走到东西旁就行 |
| `PotentialWorkThingRequest` | `ThingRequest.ForGroup(0)` | 被覆写 |

`WorkGiver_ConstructFinishFrames` 自己的 IL（完整，很短）：

```
--- Verse.ThingRequest ...::get_PotentialWorkThingRequest()
  IL_0000: ldc.i4.s 11
  IL_0002: call Verse.ThingRequest::ForGroup          ← 11 = ThingRequestGroup.BuildingFrame
--- Verse.AI.PathEndMode ...::get_PathEndMode()
  IL_0000: ldc.i4.2                                   ← PathEndMode.Touch
--- Verse.Danger ...::MaxPathDanger(Verse.Pawn pawn)
  IL_0000: ldc.i4.3                                   ← Danger.Deadly
--- Verse.AI.Job ...::JobOnThing(pawn, t, forced)   locals: [0] RimWorld.Frame
  ... Thing::get_Faction 相等?  否则返回 null
  IL_0011: isinst RimWorld.Frame → stloc.0
  IL_001D: callvirt RimWorld.Frame::IsCompleted      ← ★ 必须"材料已齐"
  IL_0028: call RimWorld.GenConstruct::CanTouchTargetFromValidCell
  IL_0033: call RimWorld.GenConstruct::FirstBlockingThing
  IL_003D: call RimWorld.GenConstruct::HandleBlockingThingJob   ← 有阻挡就先处理阻挡
  IL_0048: call RimWorld.GenConstruct::CanConstruct
  IL_0051: ldsfld RimWorld.JobDefOf::FinishFrame
  IL_005C: call Verse.JobMaker::MakeJob
```

`ThingRequestGroup` 枚举值（IL/元数据确证）：`Blueprint = 9`、`BuildingFrame = 11`、`Everything = 2`。
`PathEndMode`：`None=0, OnCell=1, Touch=2, ClosestTouch=3, InteractionCell=4`。
`Danger`：`Unspecified=0, None=1, Some=2, Deadly=3`。

**`JobOnThing` 里没有任何距离/序号/优先级比较**：它只做"派系 → 是不是 Frame → 材料是否已齐 → 能不能碰到 → 有没有阻挡 → 能不能建"，然后产出 `FinishFrame`。

另外两个送料 WorkGiver（都继承 `WorkGiver_ConstructDeliverResources`，`np` 全列）：

```
### TYPE RimWorld.WorkGiver_ConstructDeliverResourcesToFrames
    PROP Verse.ThingRequest PotentialWorkThingRequest { public get; }   → ForGroup(11) BuildingFrame
    METHOD public bool HasJobOnThing(pawn, t, forced)
    METHOD public Verse.AI.Job JobOnThing(pawn, t, forced)
### TYPE RimWorld.WorkGiver_ConstructDeliverResourcesToBlueprints
    PROP Verse.ThingRequest PotentialWorkThingRequest { public get; }   → ForGroup(9) Blueprint
    METHOD public bool HasJobOnThing(pawn, t, forced)
    METHOD public Verse.AI.Job JobOnThing(pawn, t, forced)
```

**同样都没有覆写 `PotentialWorkThingsGlobal` / `Prioritized` / `GetPriority` / `AllowUnreachable` / `MaxRegionsToScanBeforeGlobalSearch`。**

#### 关于 `priorityInType`（【IL 确证】+【数据确证】）

`RimWorld.WorkGiverDef` 字段里有 `public int priorityInType` 与 `public bool scanThings` / `scanCells`。
它**不参与"选哪个蓝图"**，只参与"先试哪个 WorkGiver"：

```
--- void Verse.WorkTypeDef::ResolveReferences()
  IL_0006: DefDatabase<WorkGiverDef>::get_AllDefs
  IL_000C: <ResolveReferences>b__19_0        ← Func<WorkGiverDef,bool>: d.workType == this
  IL_003B: call System.Linq.Enumerable::OrderByDescending
  IL_0056: workGiversByPriority.Add(d)
--- int Verse.WorkTypeDef+<>c::<ResolveReferences>b__19_1(RimWorld.WorkGiverDef d)
  IL_0001: ldfld RimWorld.WorkGiverDef::priorityInType
  IL_0006: ret
```

→ 同一 workType 内的 WorkGiver 顺序是 **`OrderByDescending(priorityInType)` 的静态 def 顺序**，与位置、与放置顺序都无关。

Core XML 实测值（`D:\SteamLibrary\steamapps\common\RimWorld\Data\Core\Defs\WorkGiverDefs\WorkGivers.xml`）：

| defName | workType | priorityInType |
|---|---|---|
| `ConstructFinishFrames` | Construction | **80** |
| `ConstructDeliverResourcesToFrames` | Construction | **70** |
| `ConstructDeliverResourcesToBlueprints` | Construction | **60** |
| `DeliverResourcesToFrames` | Hauling | 10 |
| `DeliverResourcesToBlueprints` | Hauling | 9 |

`scanThings` 的默认值也确证了（这点很关键 —— 它决定 `JobGiver_Work` 走 Thing 扫描还是 Cell 扫描）：

```
--- RimWorld.WorkGiverDef::.ctor()
  IL_0001: ldc.i4.1
  IL_0002: stfld RimWorld.WorkGiverDef::scanThings      ← ★ 默认 true
  IL_0013: ldc.i4.1; IL_0014: stfld directOrderable
  IL_001A: ldc.i4.1; IL_001B: stfld tagToGive
  IL_0021: ldc.i4.m1; IL_0022: stfld autoTakeablePriorityDrafted
  IL_0028: ldc.i4.1; IL_0029: stfld canBeDoneByMechs
--- 全程序集 --usages scanThings / scanCells（声明类型 RimWorld.WorkGiverDef）
  scanThings: 3 命中 → .ctor 里 stfld(1) + JobGiver_Work 的 TryIssueJobPackage / GiverTryGiveJobPrioritized 各一次 ldfld
  scanCells : 2 命中 → 只有上面两个方法的 ldfld，**全程序集没有任何写入点** → 恒为 false
```

Core XML 里 `<scanThings>` 只出现在其他 def 上（11 处，全是 `<scanThings>false</scanThings>`，均不在构造三兄弟中），
且这三个 def **没有** `<scanCells>`。
⇒ **构造类 WorkGiver 的 `scanThings == true`、`scanCells == false`**，所以结论 3 的 Thing 扫描分支是实际执行路径（**已确证，不再有缺口**）。

再叠加 `JobGiver_Work::TryIssueJobPackage` 的外层循环逻辑【IL 确证】：

```
IL_01BF: 当前 giver.def.priorityInType
IL_01CC: beq IL_01DE          ← 与"上次拿到 target 的 priorityInType"相同 → 继续
IL_01D4: bestTargetOfLastPriority.IsValid → brtrue IL_0905   ← 不同且已有 target → 直接跳出
```

→ 语义：**先试 80（造已备料的框架）；一旦在 80 上找到了目标，就不再降到 70/60**。
只有当"全场没有任何材料已齐的框架"时，才会降级去 70（往框架里送料）、再降到 60（往蓝图送料）。

---

### 结论 3【IL 确证】`JobGiver_Work::TryIssueJobPackage` 的选目标分支（`scanThings` 路径）

`RimWorld.JobGiver_Work::TryIssueJobPackage` 的 `scanThings == true` 分支，逐条对应 IL：

```
IL_02C5: scanner.PotentialWorkThingsGlobal(pawn)  → loc13        // 建造 = null
IL_033A: scanner.Prioritized (建造 = false)
IL_034B: 若 Prioritized:
           IL_034E: loc13 ?? pawn.Map.listerThings.ThingsMatching(scanner.PotentialWorkThingRequest)
           IL_03BB/IL_0445: ClosestThing_Global / ClosestThing_Global_Reachable(..., GetPriority(b__1/b__2), ...)
IL_04EC: 否则（建造走这里）:
           IL_04EE: 若"手里拿着合法材料"(loc14) → 目标 = 手里的东西
           IL_0514: 否则若 scanner.AllowUnreachable (建造 = false)
                      IL_0543: loc13 ?? listerThings.ThingsMatching(req)
                      IL_0571: GenClosest::ClosestThing_Global(..., 99999, validator, null, false)
           IL_057D: 否则（★ 建造实际走这条）
                      IL_0589: pawn.Position
                      IL_05A6: scanner.PotentialWorkThingRequest     // BuildingFrame
                      IL_05B2: scanner.PathEndMode                   // Touch
                      IL_05D6: scanner.MaxPathDanger(pawn)           // Deadly
                      IL_05F7: customGlobalSearchSet = loc13 = null
                      IL_05F9: searchRegionsMin = 0
                      IL_0601: searchRegionsMax = scanner.MaxRegionsToScanBeforeGlobalSearch = -1
                      IL_0609: forceAllowGlobalSearch = (loc13 != null) = false
                      IL_060D: traversableRegionTypes = 14 (RegionType.Set_Passable)
                      IL_060F: GenClosest::ClosestThingReachable(...)
```

`RegionType` 枚举（确证）：`Set_Passable = 14`。

配套的 validator / priorityGetter 也在 IL 里读到了：

```
--- JobGiver_Work+<>c__DisplayClass3_1::<TryIssueJobPackage>g__Validator|0(Verse.Thing t)
  IL_000C: ForbidUtility::IsForbidden(pawn, t) → 真则 false
  IL_0026: scanner.HasJobOnThing(pawn, t, false)
--- ...::<TryIssueJobPackage>b__1(Verse.Thing x) / b__2(Verse.Thing x)
  IL_0017: callvirt WorkGiver_Scanner::GetPriority(pawn, (TargetInfo)x)
```

即：**`priorityGetter` = 该 WorkGiver 的 `GetPriority`**（这条对第 6 节的控制手段很关键）。

---

### 结论 4【IL 确证】`GenClosest.ClosestThingReachable` 的遍历顺序 = 区域 BFS，不是列表顺序，也不是全局最近

**(a) 入口判断**

```
--- GenClosest::ClosestThingReachable(...)
  IL_002B: (searchRegionsMax < 0) || forceAllowGlobalSearch → loc1 = true   // 建造: -1 → true
  IL_004D: arg2.IsUndefined? / arg2.CanBeFoundInRegion?
           若 (!loc1 && !IsUndefined && !CanBeFoundInRegion) → Log.ErrorOnce + return null
  IL_00A1: GenClosest::EarlyOutSearch(...) → 真则 return null
```

`CanBeFoundInRegion`（IL 确证）：

```
--- Verse.ThingRequest::get_CanBeFoundInRegion()
  singleDef != null → true;  group == Nothing(1) → true;  否则 → ThingRequestGroupUtility::StoreInRegion(group)
--- Verse.ThingRequestGroupUtility::StoreInRegion(group)   // switch 表
  index 9  (Blueprint)      → IL_0165: ldc.i4.1 → true
  index 11 (BuildingFrame)  → IL_0169: ldc.i4.1 → true
  index 0/1(Undefined/Nothing) → false;  其余多项 true/false 混合
```

→ **蓝图和框架都是"存在 Region 里"的组** ⇒ 走区域 BFS（而不是直接全局列表）。

**(b) 区域搜索本身**

```
IL_00C0: loc4 = (searchRegionsMax > 0) ? searchRegionsMax : 30          // -1 → 30
IL_00FB: GenClosest::RegionwiseBFSWorker(root, map, req, peMode, traverseParams,
                                          validator, priorityGetter = null /* IL_00EC ldnull */,
                                          minRegions = searchRegionsMin = 0, maxRegions = loc4 = 30,
                                          maxDistance, out regionsSeen, 14, false, false)
IL_0104: loc3 = (结果==null) ? (regionsSeen < loc4) : false
IL_010E: 若 (结果==null && loc1 && !loc3)  → 全局兜底:
           IL_0131: set = customGlobalSearchSet ?? map.listerThings.ThingsMatching(req)
           IL_015F: GenClosest::ClosestThing_Global(root, set, maxDistance, GlobalValidator, null, false)
```

**(c) BFS 的队列结构【IL 确证】：纯 FIFO，没有任何距离排序**

```
### TYPE Verse.RegionTraverser+BFSWorker
    FIELD private Queue<Verse.Region> open          ← System.Collections.Generic.Queue
--- QueueNewOpenRegion(region)  IL_0032: Queue`1::Enqueue
--- BreadthFirstTraverseWork(...)
  IL_003E: Queue`1::Dequeue → stloc.0 (当前区域)
  IL_0056: regionProcessor.Invoke(region) → 返回 true 则 FinalizeSearch + ret   ← ★ 立刻终止
  IL_0065: RegionTraverser::ShouldCountRegion → numRegionsProcessed++
  IL_0080: numRegionsProcessed >= maxRegions → FinalizeSearch + ret
  IL_008F..IL_00F1: 遍历 region.links（RegionLink），每个 link 的 2 个 region 依序 Enqueue
--- Verse.RegionTraverser::ShouldCountRegion(Verse.Region r)
  IL_0001: Region::get_IsDoorway;  IL_0007: ceq → 门洞区域不计入 30 个预算
```

**(d) 每个区域内部怎么挑【IL 确证】：欧氏距离平方，严格小于，先到先得**

```
--- RegionProcessorClosestThingReachable::ProcessThing(Verse.Region reg, Verse.Thing t)
  IL_0018: ReachabilityWithinRegion::ThingFromRegionListerReachable(..., peMode, pawn) 假 → 跳过
  IL_0020: prio = (priorityGetter != null) ? priorityGetter(t) : 0        // 建造 = 0
  IL_0038: if (prio < bestPrio) return;                                   // 建造: bestPrio 起点 -3.4e38
  IL_0042: distSq = (t.PositionHeld - root).LengthHorizontalSquared
  IL_005D: if (prio > bestPrio || distSq < closestDistSquared)            // ★ 严格 <
           && distSq < maxDistSquared
           && (validator == null || validator(t))
             → closestThing = t; closestDistSquared = distSq; bestPrio = prio;
--- RegionProcessorClosestThingReachable::RegionProcessor(Verse.Region reg)
  IL_004D: req = reg.ListerThings.ThingsMatching(this.req)
  IL_005F..IL_007C: for i in 0..req.Count: ProcessThing(reg, req[i])       // 区域内按列表顺序扫
  IL_012D: return (regionsSeenScan >= minRegions(=0)) && (closestThing != null)   ← ★ 有货就 true
```

**⇒ 关键推论（IL 确证）：`RegionProcessor` 一返回 true，`BreadthFirstTraverseWork` 就 `FinalizeSearch` 并返回。
而它在"已扫过 ≥0 个区域且已找到候选"时就返回 true。所以：**

> **搜索在"第一个含有合法候选的区域"处就停了**。胜负 = ① 谁所在的区域在 BFS 逐跳顺序里先被访问到 → ② 在**那一个区域**内谁离工匠更近（严格小于）→ ③ 完全同距时，区域内列表顺序先出现者胜。

这不是"全地图取最近"，而是"**按区域跳数先到的那个区域里取最近**"。
区域划分由地图几何（墙/门/家具）决定，与蓝图放置顺序无关。

**(e) 全局兜底（仅当 30 个区域内一无所获）**

`GenClosest::ClosestThing_Global` 遍历 `searchSet`（`IList<Thing>` 时按 `0..Count-1`，否则走 `IEnumerable` 枚举器，IL_0051-IL_0145），
对每个元素调 `Process`，其 `ValidateThing`：

```
--- <ClosestThing_Global>g__ValidateThing|5_1(t, distSquared)
  IL_000E: validator(t) 假 → return
  IL_0032: prio = priorityGetter(t)
  IL_0039: if (prio < bestPrio) return;
  IL_0043: if (Mathf.Approximately(prio, bestPrio) && distSquared >= closestDistSquared) return;
  IL_0056: chosen = t; closestDistSquared = distSquared; bestPrio = prio;
--- <ClosestThing_Global>g__Process|5_0(t)
  IL_0037: 若 priorityGetter == null 且 distSquared >= closestDistSquared → 跳过（前置剪枝）
```

→ 建造在这里 `priorityGetter == null` ⇒ **纯距离**；列表顺序只在"距离完全相等"时有意义（先到者留）。

---

### 结论 5【IL 确证】`Frame` / `Blueprint` **没有放置序号字段**；全局唯一的是自增 `thingIDNumber`，而且它会因为"蓝图→框架"而重发

`RimWorld.Frame` 的字段（`np` 全列，摘相关）：

```
FIELD public float workDone
FIELD public Verse.ThingOwner resourceContainer
FIELD public RimWorld.StorageGroup storageGroup
FIELD public RimWorld.StorageSettings storageSettings
FIELD public Nullable<Verse.ColorInt> glowerColorOverride
PROP float WorkLeft { get; }   PROP float WorkToBuild { get; }   PROP float PercentComplete { get; }
METHOD public bool IsCompleted()   ...
```

`RimWorld.Blueprint`（`abstract`，`base=ThingWithComps`）**没有声明任何字段**（只有 `Graphic/Label/WorkTotal` 属性与 `MakeSolidThing` 等抽象/虚方法）。
`Frame` / `Blueprint` 都**没有**"放下顺序 / 序号 / 队列位置"这类字段。

有的只是 `Verse.Thing` 上的全局自增号：

```
### TYPE Verse.Thing
    FIELD public int thingIDNumber
    PROP string ThingID { public get; public set; }
--- static void Verse.ThingIDMaker::GiveIDTo(Verse.Thing t)
  IL_0006: callvirt Verse.ThingDef::get_HasThingIDNumber; 假 → ret
  IL_000F: if (t.thingIDNumber != -1) Log.Error("...already has id...")
  IL_0044: Find::get_UniqueIDsManager
  IL_0049: callvirt RimWorld.UniqueIDsManager::GetNextThingID
  IL_004E: stfld Verse.Thing::thingIDNumber
--- bool Verse.ThingDef::get_HasThingIDNumber()
  IL_0001: ldfld Verse.ThingDef::category; ldc.i4.s 9; ceq; ldc.i4.0; ceq   // category != Mote(9)
--- void Verse.Thing::PostMake()
  IL_0001: call Verse.ThingIDMaker::GiveIDTo      ← 生成时立刻发号
```

`ThingCategory`：`None=0, Pawn=1, Item=2, Building=3, Plant=4, Projectile=5, Filth=6, Gas=7, Attachment=8, Mote=9, ...`
（Frame/Blueprint 是 Building ⇒ `HasThingIDNumber == true` ⇒ **确实有单调递增 ID**。）
写点扫描（`--usages thingIDNumber Verse.Thing`）只有 3 处 `stfld`：`ThingIDMaker::GiveIDTo`、`Thing::set_ThingID`、`Thing::.ctor`。

**但 `thingIDNumber` 不是"蓝图放置序号"**：

```
--- RimWorld.Blueprint::TryReplaceWithSolidThing(workerPawn, ref createdThing, ref jobEnded)
  IL_0032: callvirt RimWorld.Blueprint::MakeSolidThing        // 生成新 Thing
  IL_0073: GenSpawn::WipeExistingThings(...)
  IL_00AA: callvirt Verse.Thing::Destroy                      // 销毁蓝图本体
  IL_00DC: call Verse.GenSpawn::Spawn                         // 新 Thing 入场（新 ID、新列表位）
--- RimWorld.Blueprint_Build::MakeSolidThing(ref bool shouldSelect)   locals: [0] RimWorld.Frame
  IL_0006: ldfld Verse.ThingDef::entityDefToBuild
  IL_000B: ldfld Verse.BuildableDef::frameDef
  IL_0016: call Verse.ThingMaker::MakeThing
  IL_001B: castclass RimWorld.Frame
```

→ **蓝图被"框架"这个新 Thing 顶替**。框架的 `thingIDNumber` 与列表位置对应的是"**何时被生成（≈玩家/系统第一次给它送材料时）**"，
而不是"蓝图何时被放下"。所以即使拿 ID / 列表顺序当顺序，也**得不到蓝图放置顺序**。

---

### 结论 6【IL 确证】`ListerThings` 是纯 append / 保序删除，没有排序

```
### TYPE Verse.ListerThings
    FIELD private readonly Dictionary<ThingDef, List<Thing>> listsByDef
    FIELD private readonly List<Thing>[] listsByGroup
    PROP List<Thing> AllThings { get; }
--- ListerThings::get_AllThings()
  IL_0006: ldc.i4.2; IL_0007: ldelem.ref          → listsByGroup[2]（ThingRequestGroup.Everything = 2）
--- ListerThings::ThingsInGroup(group)
  IL_0002: ThingRequest::ForGroup(group);  IL_0007: call ThingsMatching
--- ListerThings::ThingsMatching(req)
  singleDef != null → listsByDef[singleDef]（缺失则 EmptyList）
  否则 → listsByGroup[(int)req.group]（缺失则 EmptyList）
--- ListerThings::Add(Verse.Thing t)
  IL_000C: EverListable(def, use) 假 → ret
  IL_0041: listsByDef[def]（不存在则 new List 后 Add 进字典）
  IL_0043: List<Thing>::Add                ← append
  IL_0052: haulSources.Add(...)（是 IHaulSource 时）
  IL_0064..IL_00C5: 遍历 ThingListGroupHelper::AllGroups，凡 GroupIncludes 的组
                    listsByGroup[g].Add(t)                       ← append（无排序）
--- ListerThings::Remove(Verse.Thing t)
  IL_002B: List<Thing>::Remove             ← List.Remove，保序
  IL_005D: use == 2(Region) 且 !StoreInRegion(group) → 跳过该组
  IL_0081: listsByGroup[...].Remove
--- ListerThings::Clear()  各列表 Clear + stateHashByGroup 归零
```

`Verse.ListerThingsUse`：`Undefined=0, Global=1, Region=2`。

`map.listerThings` 的构造（在 `Verse.Map` 的某个方法里，IL 片段）：

```
  IL_0019: ldc.i4.1                                  ← ListerThingsUse.Global
  IL_001B: ldfld Verse.Map::thingListChangedCallbacks
  IL_0020: newobj Verse.ListerThings::.ctor
```

`Verse.Map` 字段：`FIELD public Verse.ListerThings listerThings`。

→ **`map.listerThings` 的组列表 = 按"Thing 生成顺序"append 的数组；`AllThings` 就是 Everything 组那张表。**
而且 `use = Global` ⇒ `Add`/`Remove` 对**所有**组都维护（不会因为 `use==2` 的跳过逻辑漏掉）。

---

### 结论 7【IL 确证】没有"按 workLeft/工作量排序"，也没有"按放置顺序排序"；建造类不是 `Prioritized`

- **`workLeft` 字段在 `RimWorld.Frame` 上的读写点：0 命中。**
  （原因：`Frame` 里根本没有名为 `workLeft` 的字段；`WorkLeft` 是属性，后备字段叫 `workDone`。）
- **`workDone`（`RimWorld.Frame`）的 6 个命中，全部与"选哪个"无关**：

  ```
  JobDriver_ConstructFinishFrame+<>c__DisplayClass8_0::<MakeNewToils>b__1(int delta)  [ldfld/stfld/ldfld]
  Frame::get_WorkLeft()          [ldfld]
  Frame::get_PercentComplete()   [ldfld]
  Frame::ExposeData()            [ldflda]
  ```

  → 只有"把活干到哪儿"的进度推进、以及存档/UI 显示读它。**没有任何选取或排序逻辑读 workLeft/workDone。**
- **全程序集覆写 `Prioritized`（= 用 `GetPriority` 压过距离）的 WorkGiver 只有 4 个**：
  `WorkGiver_PruneGauranlenTree`、`WorkGiver_RemoveRoof`、`WorkGiver_Researcher`、`WorkGiver_StudyBase`（abstract，其子类继承该覆写）。
  **建造类一个都不是** ⇒ 建造全程是 `Priority = 0` 的纯距离模式。
- 建造路径上唯一出现的"排序类"调用是送料内部选最近的"需要材料的框架"：

  ```
  --- WorkGiver_ConstructDeliverResources::ResourceDeliverJobFor(...)
    IL_02FD: call Verse.GenCollection::MinBy
  --- ...+<>c__DisplayClass16_2::<ResourceDeliverJobFor>b__2(Verse.Thing needer)
    IL_0011: call Verse.IntVec3Utility::ManhattanDistanceFlat(foundRes.Position, needer.Position)
  ```

  → **按曼哈顿距离**取最近；`workLeft` 不参与。★ 注意它是在 `HashSet<Thing>` 上跑 `MinBy`
  （`FindNearbyNeeders` 返回 `HashSet`），**HashSet 的枚举顺序与放置顺序无关**，同距并列时结果不可预测。
  而"先去找哪个框架/蓝图"本身仍然是结论 4 的 `ClosestThingReachable`：

  ```
    IL_0214: call Verse.GenClosest::ClosestThingReachable
             （searchRegionsMax = ldc.i4.m1 = -1、customGlobalSearchSet = null、forceAllowGlobalSearch = false、14）
  ```

---

### 结论 8【IL 确证】"指定造哪一个"的唯一机制：强制点单（`GiverTryGiveJobPrioritized`）

```
--- RimWorld.JobGiver_Work::GiverTryGiveJobPrioritized(pawn, giver, cell)
  IL_004D: def.scanThings
  IL_0076: call Verse.GridsUtility::GetThingList(cell)     ← ★ 只取"目标格上的东西列表"
  IL_007E..IL_00ED: 逐个：ThingRequest.Accepts → 自定义 Predicate → scanner.JobOnThing(pawn, t, false)
  IL_00EF: 否则若 def.scanCells && !ForbidUtility::IsForbidden(cell) → HasJobOnCell/JobOnCell
```

调用者是 `TryIssueJobPackage` 的 emergency 分支（`IL_0058 PriorityWork::get_IsPrioritized` / `IL_00DE GiverTryGiveJobPrioritized`）。
`Verse.PriorityWork` 持有 `prioritizedCell` / `prioritizedWorkGiver` / `IsPrioritized`。

→ 玩家右键"优先执行"某格时，游戏**不再扫描全图**，而是精确地拿该格上的 Thing 去问 WorkGiver。
这是**唯一一条能绕开"距离决定"的原生路径**（详见第 6 节）。

---

## 3. 重建出来的完整决策链（每行标注证据出处）

```
工匠要工作（正常路径）
 ├─ Pawn_WorkSettings.WorkGiversInOrderNormal                     [IL: WorkTypeDef.ResolveReferences → OrderByDescending(priorityInType)]
 │   = 先按工种优先级，工种内按 def.priorityInType 静态排序
 ├─ for giver in 该顺序:
 │    ├─ JobGiver_Work.TryIssueJobPackage                          [IL: IL_01B5 起]
 │    ├─ 若 giver.def.scanThings:                                 [IL: IL_02A8]
 │    │    things = giver.PotentialWorkThingsGlobal(pawn)         [IL: IL_02C5；建造 = null，基类 ldnull]
 │    │    if (giver.Prioritized) ...                             [IL: IL_0341；建造 = false → 不走]
 │    │    else if (手里拿着合法材料) 目标 = 手里的东西              [IL: IL_04EC..IL_0508]
 │    │    else if (giver.AllowUnreachable) ...                   [IL: IL_0514；建造 = false]
 │    │    else 目标 = GenClosest.ClosestThingReachable(          [IL: IL_057D..IL_060F]
 │    │              root = pawn.Position, map, req = BuildingFrame|Blueprint,
 │    │              peMode = Touch, traverse = For(pawn, Deadly),
 │    │              maxDistance = 9999, validator = (!Forbidden && HasJobOnThing),
 │    │              customGlobalSearchSet = null,
 │    │              searchRegionsMin = 0, searchRegionsMax = -1, forceGlobal = false, types = Set_Passable)
 │    │       ├─ flag = (searchRegionsMax < 0) = true             [IL: IL_002B]
 │    │       ├─ CanBeFoundInRegion(BuildingFrame/Blueprint) = true [IL: StoreInRegion, index 9/11 → true]
 │    │       ├─ RegionwiseBFSWorker(minRegions=0, maxRegions=30, priorityGetter=null) [IL: IL_00FB]
 │    │       │    ├─ Queue<Region> 逐跳 BFS（纯 FIFO）            [IL: BFSWorker.QueueNewOpenRegion / BreadthFirstTraverseWork]
 │    │       │    ├─ 每个区域：ThingsMatching(req)，按列表顺序逐个 ProcessThing [IL: RegionProcessor IL_004D..IL_007C]
 │    │       │    │    └─ 取 (t.PositionHeld - root).LengthHorizontalSquared 最小者（严格 <，先到先得）[IL: ProcessThing IL_0042..IL_00A3]
 │    │       │    └─ 一旦本区域产出候选 → RegionProcessor 返回 true → 立即终止 BFS [IL: IL_012D + BFSWorker IL_0056]
 │    │       └─ 若 30 个区域都没货 → ClosestThing_Global(root, map.listerThings.ThingsMatching(req), ..., null)
 │    │            纯距离、严格 <、遍历顺序 = 列表 append 顺序（仅同距并列时才影响结果） [IL: ValidateThing 5_1]
 │    ├─ 若拿到 target → 记录 bestTargetOfLastPriority，跳出"更低 priorityInType"的 giver [IL: IL_01BF..IL_01D9]
 │    └─ 若无 → 降级到下一个 priorityInType（80 → 70 → 60）
 └─ 命中后 JobOnThing 产出 Job:
      FinishFrames: 仅 isinst Frame && Frame.IsCompleted() 才给 FinishFrame 活 [IL: CFF.JobOnThing]
      DeliverResources*: ResourceDeliverJobFor → 先 ClosestThingReachable 找最近的材料（同样最近优先）
                         → FindNearbyNeeders → MinBy(曼哈顿距离) 选最近的"需要它的框架/蓝图"
```

**放置顺序在上述链路里没有出现任何一次。**

---

## 4. 没读到 / 不能确证的部分（如实列出）

1. **没有做游戏内实测。** 本文所有结论都是 IL/元数据/XML 静态证据。IL 分支方向曾在本项目出过错
   （`tools/asmdump/README.md` 记录过 `brtrue.s` off-by-one 导致"条件走哪条路"读反的教训），
   所以下面这条我特别标注为"**建议实测复核**"：
   - `RegionProcessorClosestThingReachable.ProcessThing` 的并列语义（严格 `<` ⇒ 同距保留先遇到者）——
     我读的是 `IL_006D: bge.un.s IL_00A3`（`distSq >= closestDistSquared` 则跳过）。
     这条**只影响"完全同距"**，不影响主结论。
2. **没有反编译源码**，也没有读 `Assembly-CSharp.dll` 之外的程序集（Unity/其他 mod）。
3. **区域（Region）的实际划分**取决于地图几何与存档，IL 无法断言"某两个蓝图谁的区域先被 BFS 访问到"——
   只能断言算法形式。哪个蓝图"先"是运行期几何问题。
4. ~~Core XML 的 `scanThings` 默认值未确认~~ → **已在收尾时补证**：
   `WorkGiverDef..ctor()` 里 `scanThings = 1`（默认 true），`scanCells` 全程序集无写入点（恒 false），
   Core XML 的 11 处 `<scanThings>false</scanThings>` 都不属于构造三兄弟。
   ⇒ 构造类走 Thing 扫描分支，**此条不再是不确定项**。
   剩余未确证：`WorkGiverDef` 除 Core 之外的 Def 来源（其他 DLC/mod）可能 patch `priorityInType`/`giverClass`，
   会改变"80/70/60"这组数值；本文只对 **Core 原版未打 patch** 的情形负责。
5. **`ThingDef.category` 在运行期对 Frame/Blueprint 的实际取值**未实测（IL 只证明判定公式是 `category != Mote`）。
6. **存档加载路径**（`ExposeData` 里 ID 是否重排）未追。

---

## 5. 对我们的设计意味着什么

### 5.1 先放的蓝图会不会先被建？——**不能保证，默认几乎一定不是**

- 决定因素是**位置**：工匠当前所在 → 区域 BFS 先到的区域 → 该区域内欧氏最近的框架/蓝图。
- 于是同一批蓝图，**谁先被建取决于当时是哪个工匠、站在哪里**；工匠一走，顺序就变。
- 更细的两层顺序也会干扰"按放置顺序施工"的直觉：
  1. **先送料还是先造**：`priorityInType` 80/70/60 决定"有已备料框架就先造"；没有任何已备料框架时，
     **最近的蓝图/框架先拿到材料**（距离），而不是先放的先拿到材料。
  2. **蓝图→框架会换 Thing**（新 `thingIDNumber`、新的列表位），放置顺序信息在 IL 里**根本没有被保存**。
- 所以「按期望顺序放蓝图」**不足以**控制施工顺序；把它当承重假设会直接塌。

### 5.2 可行的替代控制手段（按"可靠 / 侵入性"排序）

**(A) 分阶段放 + 等上一阶段完成（最稳，零侵入；推荐作为默认方案）**
- mod 侧把大样板切成阶段（墙 → 门 → 床 → 桌 → 光源 → 娱乐），
  一个阶段全部**建成/材料到位**后再放下一阶段。
- 为什么能成立：阶段内并行顺序随便，**阶段之间由我们控制**；不需要碰游戏内部机制。
- 代价：工期变长、工匠可能闲置（需要配合"待机时补料/搬货"的其它工作类型）。
- 判据可从现有工具拿：`foodprobe/frames_in_rect`（列出矩形内框架/蓝图/已建成）。

**(B) 强制点单（精准，半侵入）**
- 对具体 Thing 走 `Pawn.jobs` 的 ordered/prioritized 路径
  （对应 `JobGiver_Work::GiverTryGiveJobPrioritized`，它只用 `GridsUtility.GetThingList(cell)` 精确取目标格上的 Thing）。
- 效果：**能指定"下一个造哪个"**，完全绕开距离。
- 代价：一次只能派一个工匠一个目标，需要自己写调度器逐个派单；
  要考虑阻挡物处理 Job（`GenConstruct::HandleBlockingThingJob`）会插队。

**(C) 自定义 WorkGiver 子类 + 覆写三个成员（最贴合游戏机制，需要 XML patch）**
- IL 已经证明存在一条"**优先级压过距离**"的通道：
  `JobGiver_Work` 的 `Prioritized == true` 分支会调
  `GenClosest.ClosestThing_Global_Reachable(..., priorityGetter = scanner.GetPriority)`，
  而 `ValidateThing` 的判定是
  `if (prio < bestPrio) return; if (prio ≈ bestPrio && distSq >= closestDistSquared) return;`
  —— **prio 严格更大时，无论多远都会替换当前最优**。即 `GetPriority` 完全可以压过距离。
- 落地方式（不改原版方法体、只扩展 + patch def）：
  1. 写 `class WorkGiver_ConstructFinishFrames_BySeq : WorkGiver_ConstructFinishFrames`；
  2. 覆写 `PotentialWorkThingsGlobal(pawn)` → 返回按**我们自己写入的序号**排序的全局框架集合
     （如 `map.listerThings.ThingsInGroup(BuildingFrame)` 过滤后排序；序号可用 `Thing` 上的自建 `Comp`/`MapComponent` 字典保存）；
  3. 覆写 `Prioritized => true`；
  4. 覆写 `GetPriority(pawn, TargetInfo t)` → 返回与序号单调相关的分数（**必须让不同序号拿到不同分数**，否则退化成比距离）；
  5. XML patch 把 `ConstructFinishFrames` / `ConstructDeliverResourcesToFrames` / `ToBlueprints` 的 `giverClass` 换成子类。
- 代价/风险：
  - 全局集合 + 全图扫描的性能（`ClosestThing_Global_Reachable` 会做可达性检查，蓝图多时开销大）；
  - `PotentialWorkThingsGlobal` 返回非 null 会**改变扫描语义**（`forceAllowGlobalSearch = true`、`searchRegionsMin/Max` 全走全局），
    必须自己保证返回集合的可达性/合法性，否则会出现"小人跑去够不着的地方"；
  - 序号要在**放蓝图时**写入（我们自己的放置 API 天然有顺序，正好可用）；注意蓝图→框架换 Thing，
    序号要随 `Blueprint.TryReplaceWithSolidThing` 传递给新 `Frame`（可以按坐标/`Map` 查表转移）。

**(D) 不控制顺序、控制"谁能被建"（折中）**
- 一次只放"允许并行"的那几个蓝图（阶段内并行），本质上是 (A) 的弱化版。

### 5.3 给下一轮实测的判别性实验设计（建议照抄）

现有记录（`docs/样板语义与建造顺序-实验记录.md`）里的 A(150,150) / B(155,150) 两点太近，
无法区分"距离"与"放置顺序"。建议改成**让两个因素指向相反**：

1. 先放 **远点** `F1=(x+12, y)`，后放 **近点** `F2=(x+2, y)`（工匠初始在 `x` 附近）；
2. 观察谁先出框架进度：
   - 若 **F2（后放但近）先** ⇒ 支持本文结论（距离决定），**否定**承重假设；
3. 再做**换位**对照：把两者位置对调（先放近的、后放远的），若"近的先"依旧成立 ⇒ 距离决定，铁证；
4. 追加"同距并列"实验：让两个蓝图**对称等距**（如 `(x-2,y)` 与 `(x+2,y)`），重复多次看是否随机/是否偏向某侧 ——
   用来观察结论 4 的 tie-break（并验证"列表顺序/严格小于"这条 IL 读法）。

---

## 6. 一句话总结

> **IL 里根本不存在"蓝图放置顺序"这个概念**：
> 建造是"`JobGiver_Work` → `ClosestThingReachable`（区域 BFS + 区域内欧氏最近）"，
> priorityInType 只决定"先试造还是先试送材料"，
> 唯一的 tie-break 是"框架生成顺序"而不是"蓝图放置顺序"。
> 想按顺序施工，只能**分阶段放**，或**用 `Prioritized + PotentialWorkThingsGlobal + GetPriority` 自定义 WorkGiver 把优先级顶过距离**。
