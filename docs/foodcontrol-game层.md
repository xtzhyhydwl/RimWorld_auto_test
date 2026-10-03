# FoodControl Game 层（游戏内适配层）

> 产出：`src/foodcontrol-game/*.cs`（7 个文件，namespace `FoodControl.Game`）
> 编译脚本：`scripts/build-mod.mjs`
> 产物：`tmp/foodcontrol-game/FoodControl.dll` + `tmp/foodcontrol-game/FoodControl.Core.dll`
> 状态：**编译通过**（Roslyn，`/langversion:7.3`，引用游戏 Mono 程序集）。**未在游戏里运行过**。
> 日期：2026-10-03

---

## 一、怎么编译

```powershell
cd E:\2026.7以后的新工程\DSH\RimWorld_auto_test
node scripts/build-mod.mjs              # 编译 Core + Game → tmp/foodcontrol-game/
node scripts/build-mod.mjs --check      # 只做静态检查（Core 有没有混进游戏类型）
node scripts/build-mod.mjs --core-only  # 只编 Core（快速定位是不是 Game 层的错）
node scripts/build-mod.mjs --deploy     # 编完拷进 mods/FoodControl/1.6/Assemblies/
```

实际用的命令（脚本内部拼的，与 `docs/游戏API符号确证.md` §E 的探针命令同构）：

```
csc.exe /nologo /target:library /nostdlib+ /langversion:7.3 /optimize+ /deterministic+
        /out:<WS>\tmp\foodcontrol-game\FoodControl.Core.dll
        /reference:<Managed>\{mscorlib,System,System.Core,netstandard,UnityEngine.CoreModule,Assembly-CSharp}.dll
        <src\foodcontrol-core\*.cs（不含 tests）>

csc.exe ... /out:<WS>\tmp\foodcontrol-game\FoodControl.dll
        /reference:<上面那一堆> /reference:<WS>\tmp\foodcontrol-game\FoodControl.Core.dll
        <src\foodcontrol-game\*.cs>
```

**编译结果**：

```
静态检查：Core 无游戏类型引用 ✓（4 个文件）
✓ Core（供 Game 层引用） 编译成功   tmp\foodcontrol-game\FoodControl.Core.dll  (14.0 KB)
✓ Game（引用 Core + Assembly-CSharp） 编译成功  tmp\foodcontrol-game\FoodControl.dll  (38.0 KB)
```

同时回归了一遍离线测试，确认 Core 没被碰坏：`node scripts/build-core.mjs` → **通过 59 / 失败 0**。

### 为什么编成两个 DLL

`FoodControl.Core` 是纯逻辑层（不引用游戏程序集），同一份源码还能被 `build-core.mjs` 用
.NET Framework 的 csc 另编一份在本机毫秒级跑离线测试。Game 层反过来必须引用 `Assembly-CSharp.dll`。
编成两个 DLL 让这条边界在**产物**上看得见，而不是靠人自觉。

⚠ **Game 层的产物故意叫 `FoodControl.dll`（不是 `FoodControl.Game.dll`）**：
测试探针 `bridge/DSH.FoodProbe/DSH.FoodProbe.cs` 的 `FindMod()` 是按
`a.GetName().Name == "FoodControl"` **精确匹配**加载的程序集的，改成别的名字探针会报
「程序集 FoodControl 未加载」。（备选是改探针里的 `MOD_ASM` 常量；那是测试侧的文件，我没动。）

### 为什么默认输出到 tmp 而不是 mods/

`<游戏目录>\Mods\FoodControl` 是 **junction** → 工作区 `mods/FoodControl`，写进去游戏立刻看得见。
但**正在运行的游戏会把已加载的 DLL 锁住**，之后每次编译都失败（"无法打开 xxx.dll 进行写入"）。
默认写 `tmp/` 就不会互相干扰；要部署时显式 `--deploy`（带锁检测）。

已执行的部署（父 agent 要求确认最终产物就位）：

```
mods\FoodControl\1.6\Assemblies\FoodControl.Core.dll   14336 B
mods\FoodControl\1.6\Assemblies\FoodControl.dll        38912 B
```

junction 目标就是工作区，所以物理写入落在 `E:\...\RimWorld_auto_test\mods\...`，
没有往 `D:\...\RimWorld\*` 写任何字节。RimWorld 会把该目录下的 DLL 全部加载，两个文件都被加载。

---

## 二、模块划分

| 文件 | 职责 | 关键点 |
| --- | --- | --- |
| `GameState.cs` | 纯数据：`GameReadings`（原始读数）+ `PlantBucket`（作物分桶） | 允许游戏类型（它不属于 Core） |
| `StateReader.cs` | 游戏 API → `Core.FoodState`；另有 `ReadCurrentMap()` 只读快照给测试 | 食物判据、消耗常量、作物分桶 |
| `PlotPlanner.cs` | 游戏地图 → `Core.PassabilityMap` + 基地中心 + 距离场 + 已有区候选 + 最佳地块 | `PlotPlan` 存下全部中间产物供遥测 |
| `Executor.cs` | `Core.FoodAction` → 真的建区 / 扩格 / 设作物 | **回读验证**，不假定成功 |
| `Telemetry.cs` | 每轮一行 JSON 追加到 `foodcontrol-telemetry.jsonl` | 手写极简 JSON，容错降级 |
| `FoodControlGameComponent.cs` | `GameComponent`：每游戏日跑一轮闭环 + 测试钩子 | 全流程 try/catch |
| `ModBoot.cs` | `Mod` 子类 + 静态钩子 | 类名必须是 `ModBoot`（探针按名字找） |

---

## 三、游戏 API 依据表（每条都标来源）

**依据等级**：
- **【确证·文档 §X】** = `docs/游戏API符号确证.md` 里有实测证据（元数据 / IL / 编译探针）
- **【确证·本轮 IL】** = 本轮我自己用 `tools/asmdump/IlDump.exe` 反汇编读出来的（新证据，文档里没有）
- **【确证·本轮编译】** = 本轮我的代码真的编过了，等于符号/签名/可见性都对
- **【推断】** = 我自己的判断，**没在游戏里跑过**

### A. 感知（StateReader）

| 用法 | 依据 |
| --- | --- |
| `Thing.GetStatValue(StatDefOf.Nutrition)`（扩展方法，`using RimWorld`） | 【确证·文档 §A1】（编译探针级） |
| `thing.stackCount`（字段） | 【确证·文档 §A1】 |
| `ThingDef.IsNutritionGivingIngestible` | 【确证·文档 §A2】 |
| `ThingCategoryDefOf.Foods`（命名空间是 **RimWorld**） | 【确证·文档 §A2】 |
| `ThingCategoryDefOf.Foods.ContainedInThisOrDescendant(def)` | 【确证·文档 §A2】 |
| `def.category != ThingCategory.Item` 用来**排除长在地里的植物** | 【确证·本轮 IL】`Plants_Bases.xml` 里 `PlantBase` 自带 `<ingestible>`；`get_IsNutritionGivingIngestible` 的 IL = `ingestible != null && ingestible.CachedNutrition > 0` ⇒ 对活植物是 **true**。不排除会把野草/树算进存粮（实测踩到：13936 nutrition ≈ 2903 天口粮） |
| `t is Corpse` 排除尸体 | 【推断】尸体是 Item 且带营养，语义上不是"存粮" |
| `map.listerThings.AllThings` | 【确证·文档 §七.2】 |
| `map.mapPawns.FreeColonistsSpawned`（`List<Pawn>`） | 【确证·本轮编译】+ 元数据 |
| **1.6 nutrition/殖民者/天** | 【确证·文档 §A3】的 **IL 推导**（1.0 × 1.0 × 2.6666667e-5 × 60000）。⚠ **数值本身从未实测** |
| `Plant.Growth` / `Plant.GrowthRate`（public） | 【确证·文档 §B4】 |
| `Plant.def.plant.growDays` | 【确证·文档 §B4】 |
| `Plant.GrowthPerTick` **不能用**（protected） | 【确证·文档 §B4】 |
| `map.zoneManager.ZoneAt(c)` / `c.GetPlantToGrowSettable(map)` 判"人种的" | 【确证·文档 §C10 / §C9】 |
| `GenTicks.TicksGame` / `GenLocalDate.{DayOfYear,Year,HourOfDay,Season}(map)` | 【确证·本轮编译】+ 元数据（`GenDate.TicksPerDay = 60000`、`DaysPerYear = 60` 是 const） |

### B. 选址（PlotPlanner）

| 用法 | 依据 |
| --- | --- |
| `cropDef.CanEverPlantAt(c, map, out Thing blocker, false, true)` 返回 `AcceptanceReport`、`.Accepted` | 【确证·文档 §C10】（第 4 参确定是 `out`，`ref` 会 CS1620） |
| 游戏按**肥力**判可种，**不看 affordances** | 【确证·文档 §C10】 |
| `TerrainAffordanceDefOf.GrowSoil` **不存在** → 没走这条路 | 【确证·文档 §C10】 |
| `map.fertilityGrid.FertilityAt(c)` | 【确证·文档 §C10】 |
| `Verse.GenGrid.Walkable(this IntVec3, Map)`（扩展方法） | 【确证·本轮编译】——`PathingContext.Walkable` **不存在**，编译探针报错信息反过来指出了 `GenGrid.Walkable` |
| `c.InBounds(map)` | 【确证·本轮编译】 |
| `c.GetTerrain(map)` / `c.GetEdifice(map)` | 【确证·文档 §C10】 |
| `TerrainDef.pathCost` / `BuildingDef.pathCost`（都在 `Verse.BuildableDef` 上） | 【确证·本轮 IL】+ 元数据（默认 0，XML 里 Soil=2 / Sand=4 / Gravel=2 / MarshyTerrain=3…） |
| `map.areaManager.Home`（`RimWorld.Area_Home`）/ `Area.ActiveCells` / `Area.TrueCount` | 【确证·本轮编译】+ 元数据 |
| `Zone_Stockpile.cells` 求质心 | 【确证·本轮编译】 |
| `map.Size`（`IntVec3`，用 `.x` / `.z`） | 【确证·本轮编译】+ 元数据 |
| `map.zoneManager.AllZones` / `zone.cells` / `zone.CellCount` / `zone.GetUniqueLoadID()` | 【确证·本轮编译】+ 元数据 |
| `GenAdj.CardinalDirections`（`IntVec3[]`）+ `IntVec3 + IntVec3` | 【确证·本轮 IL】（`Designator_ZoneAdd::DesignateMultiCell` 里就是这么用的） |
| Core 侧算法（`ComputeDistanceField` / `ResolveBaseCenter` / `FindBestExistingZone` / `FindBestPlot` / `Score`） | 纯逻辑层，**离线 59/59 测过** |

### C. 执行（Executor）★ 本轮最重要的两条新证据

| 用法 | 依据 |
| --- | --- |
| `new Zone_Growing(map.zoneManager)` / `SetPlantDefToGrow(def)`（`PlantDefToGrow` 是 get-only） | 【确证·文档 §C9】 |
| `ZoneMaker.MakeZoneWithCells(z, cells)` 的真实行为 | 【确证·本轮 IL】**全文就是** `if (cells != null) foreach (c in cells) z.AddCell(c); return z;` —— **完全不碰 zoneManager，也不注册** |
| `Zone.AddCell(c)` 的真实行为 | 【确证·本轮 IL】`cells.Add` + `zoneManager.AddZoneGridCell(this,c)` + 网格脏标记 + **`AutoHomeAreaMaker.Notify_ZoneCellAdded(c, this)`** |
| `ZoneManager.RegisterZone(z)` 的真实行为 | 【确证·本轮 IL】`allZones.Add(z)` + `z.PostRegister()` |
| **必须先 `RegisterZone`** | 【确证·本轮 IL】`WorkGiver_Grower.PotentialWorkCellsGlobal` 的 MoveNext：`pawn.Map.zoneManager.get_AllZones()` → `isinst Zone_Growing` → 遍历 `zone.cells`。**不在 AllZones 里的区永远不会被播种**（父 agent 也实测了：不加 RegisterZone 时 `AllZones.OfType<Zone_Growing>().Count() == 0`；加上后推进 92555 tick，36 格被种上） |
| 顺序：`new` → `RegisterZone` → `MakeZoneWithCells` → `SetPlantDefToGrow` | 【确证·本轮 IL】游戏自己的 `Designator_ZoneAdd.DesignateMultiCell` 就是这个顺序（`MakeNewZone` → `RegisterZone` → `AddCell` → … → `CheckContiguous`） |
| 注册过的**空**种植区会出错误日志 | 【确证·本轮 IL】`WorkGiver_Grower` 里 `if (growZone.cells.Count == 0) Log.ErrorOnce("Grow zone has 0 cells: ...")` ⇒ 所以 Executor 在 `CellCount <= 0` 时会 `Delete(false)` 回滚 |
| `Designator_ZoneAdd.IsZoneableCell(IntVec3, Map)`（public static）判"这格允许被画进区" | 【确证·本轮编译】+ 元数据（语义细节未读 IL → 【推断】按名字与 `CanDesignateCell` 用法理解） |
| `Zone.Delete(bool)` / `ZoneManager.DeregisterZone(Zone)` 用于回滚 | 【确证·本轮编译】+ 元数据 |
| 回读验证：`AllZones` 里 `ReferenceEquals` 找到 + `CellCount > 0` + 格级 `ZoneAt(c)` 指回同一区 | 【推断】这是我加的硬性要求（"不要假定成功"）；三项全过才 `verified=true` |

### D. 驱动（FoodControlGameComponent）

| 用法 | 依据 |
| --- | --- |
| `class X : GameComponent` + `public X(Verse.Game game)` | 【确证·本轮 IL】`Game.FillComponents()`：`foreach (t in typeof(GameComponent).AllSubclassesNonAbstract()) try { components.Add((GameComponent)Activator.CreateInstance(t, this)); } catch { Log.Error("Could not instantiate a GameComponent of type " + t + ": " + ex); }` ⇒ 构造函数**必须恰好一个 `Game` 参数** |
| mod 程序集会被自动扫描到 | 【确证·本轮 IL】`GenTypes.AllActiveAssemblies` 的 iterator：先 yield `Assembly.GetExecutingAssembly()`，再 foreach `LoadedModManager.RunningMods` → `mod.assemblies.loadedAssemblies` ⇒ **包含本 mod 的程序集** |
| `GameComponentTick` 用 `override` 才生效 | 【确证·本轮 IL】`GameComponentUtility.GameComponentTick()` 里是 `callvirt` |
| `Current.ProgramState == ProgramState.Playing` | 【确证·文档 §A3】 |
| `map.IsPlayerHome` | 【确证·本轮编译】+ 元数据 |
| `Verse.Game.components`（`List<GameComponent>`，public 字段）用于兜底查找 | 【确证·本轮编译】+ 元数据 |
| `Scribe_Values.Look(ref int, string, int)` | 【确证·本轮编译】**未在真实存读档里验证** |
| `ModBoot : Verse.Mod` + `public ModBoot(ModContentPack)` | 【确证·本轮 IL】`LoadedModManager.CreateModClasses()` 里 `Activator.CreateInstance(...)` |

### E. 遥测（Telemetry）

| 用法 | 依据 |
| --- | --- |
| `GenFilePaths.SaveDataFolderPath`（public static get） | 【确证·本轮编译】+ 元数据 |
| 文件名 `foodcontrol-telemetry.jsonl`，直接放 `SaveDataFolderPath` 根下 | 【约定】与测试探针 `foodprobe/telemetry_tail` 对齐（它按这个路径找）。**不是**从游戏 API 读出来的 |
| 手写 JSON、不引 Newtonsoft | 【决定】避免 mod 依赖冲突（RimBridgeServer 目录里那份 `0Newtonsoft.Json.dll` 是故意改名屏蔽的） |
| 数字用 `InvariantCulture` | 【推断】中文/欧洲区域设置会把小数点写成逗号 → 非法 JSON |

---

## 四、编译时踩到的问题（原文）

### 1. `CS0118: "Game" 是命名空间，但此处被当做类型来使用`（2 处）

```
src\foodcontrol-game\FoodControlGameComponent.cs(81,26): error CS0118: "Game"是 命名空间，但此处被当做 类型 来使用
src\foodcontrol-game\FoodControlGameComponent.cs(92,41): error CS0118: "Game"是 命名空间，但此处被当做 类型 来使用
```

**根因**：我们把自己的命名空间取成了 `FoodControl.Game`，而游戏里有 `Verse.Game`。
在 `namespace FoodControl.Game { ... }` 内部写裸的 `Game`，C# 名称解析**优先匹配命名空间**。
**修法**：写全 `Verse.Game`（构造函数参数与字段类型）。

### 2. `CS0119: "FoodControlGameComponent.Find()" 是一个方法，这在给定的上下文中无效`（3 处）

**根因**：我给组件加了个静态 `Find()` 方法，于是类里的 `Find.CurrentMap` 被解析成"调用方法 Find"，
而不是 `Verse.Find`。**修法**：写全 `Verse.Find.CurrentMap`。

### 3. `CS1061: "PolicyConfig" 未包含 "Decide" 的定义`

**根因**：字段 `public PolicyConfig Policy` **遮蔽**了类型 `FoodControl.Core.Policy`，
于是 `Policy.Decide(...)` 变成在字段上找方法。
**修法**：字段改名 `PolicySettings` / `CalibrationSettings`，调用写成 `FoodControl.Core.Policy.Decide(state, PolicySettings)`。

> 这三条合起来是一条经验：**`FoodControl.Game` 这个命名空间本身就是一个坑**。
> 因为它同时是「命名空间」和「游戏核心类型名」，任何裸写 `Game` / 与 Core 类型同名的字段都会炸。
> 命名空间按任务要求保持不变，但代码里对 `Verse.Game`、`Verse.Find`、`Core.Policy` 一律写全限定名。

### 4. `CS0246: 未能找到类型或命名空间名 "Zone_Growing"`

`Telemetry.cs` 里漏了 `using RimWorld;`（`Zone_Growing` 在 **RimWorld** 命名空间，不在 `Verse`）。

### 5. 编译探针发现的（不是错，是救命信息）

我写了一个临时探针（`tmp/modprobe/Probe2.cs`）去试几个"我以为是这个名字"的 API，结果：

```
error CS1929: "PathingContext"不包含"Walkable"的定义，并且最佳扩展方法重载
              "GenGrid.Walkable(IntVec3, Map)"需要类型为"Verse.IntVec3"的接收器
error CS1061: "PathingContext"未包含"PerceivedPathCostAt"的定义 ...（PathCostAt / CalculatedCostAt / CostAt 同样失败）
```

⇒ 我原计划用 `map.pathing.Normal.Walkable(c)` 取通行性，**这条路是死的**；错误信息反过来告诉我
`Verse.GenGrid.Walkable` 存在（在 `Verse.GenGrid` 的成员表里也确认了，还有 `Impassable`/`Standable`）。
这就是"编过才算数"的价值 —— 元数据 dump 因为 `PathingContext` 的 TypeLoadException 什么都看不到。

---

## 五、⚠ 我不确定 / 没验证的地方（按重要度排）

### 最大的那一条

1. **整条链路从没在游戏里跑过。** 本轮只做到"编译通过 + 离线 Core 测试 59/59"。
   所有运行期行为（读到的数对不对、建区能不能被小人用、遥测能不能落盘）**全部未验证**。

### 涉及数值 / 公式的

2. **`NutritionPerColonistPerDay = 1.6` 是 IL 推导值，未实测。**
   确证文档 §A3 自己就把"营养消耗实测"列在待实测清单里。
   已知偏差来源全部**没处理**：特性/基因（Biotech 代谢率）/卧床/饥饿档位（Hungry=0.5×、Starving=0×）、
   **动物/囚犯/访客/商队完全没算**。
3. **`Calibration.NutritionPerCellAtMaturity = 18.0`（Core 默认）没有依据。**
   它决定"缺 X nutrition → 种几格"，直接影响决策规模。
   真实值应是 `harvestYield × 每单位营养`（水稻 `harvestYield = 6`，确证文档 §B7），**但没算过**。
   ⇒ **缺口→格数的换算现在很可能是系统性偏差的**。这是最该先实测的一个数。
4. **`growDays` 不硬编码**（运行时取 `def.plant.growDays`），所以父 agent 实测到的
   "水稻 growDays = 3 而不是 2"不影响本层代码 —— 但 Core 的注释里写着"水稻 = 2"（见 §七建议）。
5. **`Plant.GrowthRate == 0` 是正常状态**（夜里 `Plant.Resting`），本层只计数
   （`zeroGrowthRateCells` 进遥测），不当错误。**但预测器在这个状态下会退化**：
   所有在种作物的"距成熟天数"变成 +∞ ⇒ 预测以为没有产出 ⇒ 缺口被高估。
   我的缓解办法是**只在 8:00–19:00 之间自动决策**（见下一条），天气/极夜/非生长季的情况**没处理**。

### 涉及时间 / 窗口的（【推断】）

6. **决策窗口 8:00–19:00 的钟点换算是推断。**
   【确证·文档 §B4】只说 `Plant.Resting` 与 `GenLocalDate.DayPercent` 的 **0.25 / 0.8** 比较；
   "0.25 ≈ 6:00、0.8 ≈ 19:12"是我自己换的，**没实测**。
   窗口太宽会在 resting 时段决策（预测退化）；太窄会漏掉整天（如果那天游戏只在这个时段外被 tick）。
7. **读档瞬间可能立刻跑一轮**：`_lastRunDay` 存了盘，但读档后如果当天还没跑过、且时间在窗口内，会立即决策。
   这是有意的（避免读档后长时间不决策），但**没验证过**。
8. **手动 `RunCycleNow()` 不更新 `_lastRunDay`**：所以手动跑完，当天自动那一轮还会再跑一次。
   有意为之（测试不该消费掉当天的自动档位），但会让遥测里出现同一天两行。

### 涉及选址质量的

9. **通行代价是近似值（【推断】）**：不能确证游戏内部 `PathGrid` 的公开读法，
   所以用 `不可通行 = 0；否则 max(1, terrain.pathCost) + edifice.pathCost`。
   距离场只用于**相对比较**（谁更远），所以近似够用；但"河对岸 vs 同侧"这类判断可能与游戏实际不完全一致。
   TODO：找到 `PathGrid` 的公开读法后换成游戏原值。
10. **可种性的便宜预筛（【推断】）**：`!c.Walkable(map)` 直接判不可种，省掉昂贵的 `CanEverPlantAt`。
    逻辑是"不可通行 ⇒ 有阻挡物 ⇒ 不能种"。反过来不成立（水泥地可行走但没肥力），
    所以可行走的格子**仍然走游戏自己的 `CanEverPlantAt`**。这条**没实测**，但方向是保守的。
11. **性能未实测**：只对 `HardMaxDistance`（默认 120 格）内的格子做 `CanEverPlantAt`，
    250×250 地图上最坏约 4.5 万次调用/游戏日。猜是几十毫秒，**没量过**。
    （遥测里有 `arableScanCells` 可以看到实际扫了多少格。）
12. **`ArableCellsAvailable` 的口径**：`HardMaxDistance` 内、可种、未被占用，
    **包含已有种植区内部的空地**。这个口径是我定的（因为它才是真实可播面积），
    设计文档没规定。另一个数 `arableCellsInExistingZones` 也进了遥测。
13. **复用已有区的语义**：如果某个区里有 10 格空地而这一轮只缺 4 格，
    Executor 会去"把该区作物设好"（不动格子）—— 结果是那 10 格都会被播种，**实际新增 > 决策要的 4 格**。
    我认为这符合"优先复用已有空地"的意图，但**这是行为偏差，写在这里备案**。
14. **截断地块会按"离基地近"排序取前 N 格**，可能得到**非连续**的种植区。
    没有调用 `Zone.CheckContiguous()`（游戏在 `DesignateMultiCell` 末尾会调）。
    【确证·本轮 IL】播种是遍历 `zone.cells`，所以非连续应该也能种 —— 但**没实测**。
15. **`IsZoneableCell` 的语义只看了签名**（public static，返回 `AcceptanceReport`），
    没读 IL，不知道它是否会因为"附近有敌人/边缘"之类原因拒绝。按名字用，属【推断】。

### 涉及副作用的

16. **`Zone.AddCell` 会调 `AutoHomeAreaMaker.Notify_ZoneCellAdded`**（【确证·本轮 IL】）。
    也就是说 mod 建种植区**可能自动扩大 Home 区**，而 Home 区正是我们的基地中心来源之一
    ⇒ 存在"建区 → Home 变大 → 基地中心漂移 → 下次选址变化"的反馈回路。
    机制确认了，**影响没量过**。这也是我"优先复用已有区、少开新区"的一个理由。
17. **`listerThings.AllThings` 的覆盖范围**是【推断】：小人手上扛的、商队里的、其它地图的、
    容器里的食物都不在其中（前三种确定不在）。对"还能吃几天"这个指标影响量级小，但没验证。
18. **食物判据没考虑**腐烂进度、温度、`preferability == NeverForNutrition` 之类
    "有营养但人不能吃"的边角（例如生肉对人可吃、干草对人不可吃但仍是存粮 —— 我按"是食物"算，
    这在"给人吃"的语义下偏乐观）。

### 涉及接口约定的

19. **遥测文件路径 / 文件名是约定**（与 `DSH.FoodProbe` 的 `telemetry_tail` 对齐），
    不是从游戏读出来的。如果测试侧改了，两边要一起改。
20. **`day` 字段 = `年 × 60 + 年内天`**（我的定义，因为没找到 `GenDate.DaysPassed` 的公开读法）。
    遥测里同时写了 `year` / `dayOfYear` / `tick`，不会对不上账。
21. **`Scribe_Values.Look` 编过了但没跑过**：真实存读档是否按预期恢复 `_lastRunDay` 未验证。

---

## 六、测试钩子接口（给 RimBridgeServer 伴生 DLL）

按 `bridge/DSH.FoodProbe/DSH.FoodProbe.cs` 现有的反射约定**逐条对齐**：

| 探针怎么找 | 本层提供 |
| --- | --- |
| `asm.GetName().Name == "FoodControl"` | Game 层产物文件名 = `FoodControl.dll` ⇒ 程序集名就是 `FoodControl` |
| `asm.GetType("FoodControl.Game.ModBoot")` | 类名 **`ModBoot`**（不是 `FoodControlMod`），namespace `FoodControl.Game` |
| `CallStatic(boot, "RunCycleNow")`（**0 参数静态**） | `public static string ModBoot.RunCycleNow()` —— 立即跑一轮，忽略"每天一次/时段"两条调度 |
| `CallStatic(reader, "Read")` → `"ReadCurrentMap"`（**0 参数静态**） | `public static FoodStateSnapshot StateReader.ReadCurrentMap()`（`Read` 需要 2 参，探针会自然跳过它） |
| 探针 `Describe()` **只反射 public 属性**（不反射字段） | 上面返回的 `FoodStateSnapshot` **全部是属性**，所以能打印出来（`FoodState` 全是字段，直接返回会是空字典） |
| `foodprobe/telemetry_tail` 读 `<SaveDataFolderPath>/foodcontrol-telemetry.jsonl` | `Telemetry.TelemetryPath` 就是这条路径 |

另外便利入口（不依赖反射）：

```csharp
FoodControl.Game.ModBoot.Component          // → FoodControlGameComponent，找不到返回 null
FoodControl.Game.ModBoot.EnsureComponent()  // 保险绳：万一自动发现失效，手动挂上
FoodControl.Game.ModBoot.ReadStateJson()    // 只读感知，返回一行 JSON
FoodControl.Game.ModBoot.StatusText()       // 一行人类可读状态（含遥测路径与已写行数）
component.TelemetryLocation                 // "路径 (lines=N, lastError=...)"
```

### 遥测字段（`foodcontrol-telemetry.jsonl`，每轮一行）

任务要求的字段全在，另外加了一批诊断字段：

```
source, tick, day, dayOfYear, year, hour, season,
stock, burnRatePerDay, reserveDays, colonistCount, nutritionPerColonistPerDay, foodStacks, foodDefs,
projectedReserve[], minProjectedReserve, firstShortfallDay, lowThreshold, highThreshold,
neededCells, needsMoreLand,
action.kind, action.plantDefName, action.cells, action.reason,
arableCellsAvailable, arableCellsInExistingZones, arableScanCells, plannedCropDef, mapSize,
baseCenter{kind,x,z,note}, bestPlot{x,z,w,h,area,avgDistance,avgFertility,score},
growingZones, growingZoneCells, zones[{id,plant,cells,free,extendable,score}],
plantedGroups[{def,cells,growth,growthRate,growDays}],
cultivatedCells, wildPlantsIgnored, zeroGrowthRateCells,
execution{method,success,verified,zoneId,zoneCellsAfter,cellsRequested,cellsAdded,cellsAlreadyFree,plantDefSet,detail},
error, planNote
```

`execution.method` 取值：`none` / `reuse-zone` / `reuse-zone+extend` / `extend-zone` / `new-zone`。
**测试要断言"建区成功"时别只看 `success`，要看 `verified`**（`verified` 才是三项回读都过）。

---

## 七、建议（**我没有改 Core**，按约束只提建议）

1. **`src/foodcontrol-core/Models.cs:64` 的注释错了一个数**：
   `PlantGroup.GrowDays` 注释写「对应 `ThingDef.plant.growDays`（**水稻 = 2**）」，
   实测（父 agent 在真实游戏里）是 **3**。同时 `PlantGroup` 构造函数的默认值 `5.8` 是土豆，
   容易被误读成水稻。建议改成 3 并注明来源。
2. **`Calibration.NutritionPerCellAtMaturity = 18.0` 缺依据**，建议加注释标"待实测"，
   并写清它的定义（= `harvestYield × 每单位营养 × 收获系数`）。它是决策规模的关键标定。
3. **`SiteSelection` 的候选语义不对称**：`CentroidOfKind`（Home）允许 `CenterIndex = -1`（让 Core 求质心），
   而 `PickNearestStockpile` **会跳过** `CenterIndex < 0` 的贮藏区候选。我按这个不对称性填了数据，
   但它对调用方是个陷阱，建议在 `CenterCandidate` 上写清楚。
4. **Core 的模型里缺"复用已有区"这个概念**：`FoodAction` 只有「种 N 格」，
   没有「在已有区里补种」还是「新开一块」的区别，所以复用策略只能由 Game 层自己定
   （我写成了"优先复用"）。如果以后想让策略层显式表达"别开新地，去填旧区"，
   需要给 `FoodAction` 加一个字段（例如 `PreferExistingZone`），并补离线测试。
5. **建议给 Core 补一条离线测试**：`BurnRatePerDay == 0`（没有殖民者）时
   `ReserveDays == double.MaxValue`、`lowThreshold == 0`、策略输出 `DoNothing`。
   现在的行为是对的，但没有测试钉住它。

---

## 八、复现命令速查

```powershell
cd E:\2026.7以后的新工程\DSH\RimWorld_auto_test

node scripts/build-mod.mjs                # 编译（产物在 tmp/foodcontrol-game/）
node scripts/build-core.mjs               # Core 离线测试（应急：59/59）
node scripts/build-mod.mjs --deploy       # 部署到 mods/FoodControl/1.6/Assemblies/

# 本轮用到的只读取证（不碰游戏、不启停游戏）
$m = "D:\SteamLibrary\steamapps\common\RimWorld\RimWorldWin64_Data\Managed"
tmp\asmdump\IlDump.exe "$m\Assembly-CSharp.dll" $m "Verse.ZoneMaker" "MakeZoneWithCells"
tmp\asmdump\IlDump.exe "$m\Assembly-CSharp.dll" $m "Verse.Zone" "AddCell"
tmp\asmdump\IlDump.exe "$m\Assembly-CSharp.dll" $m "Verse.Game" "FillComponents"
tmp\asmdump\IlDump.exe "$m\Assembly-CSharp.dll" $m "Verse.GenTypes" "get_AllTypes"
tmp\asmdump\IlDump.exe "$m\Assembly-CSharp.dll" $m "RimWorld.WorkGiver_Grower+<PotentialWorkCellsGlobal>d__4" "MoveNext"
tmp\asmdump\IlDump.exe "$m\Assembly-CSharp.dll" $m "RimWorld.Designator_ZoneAdd" "DesignateMultiCell"
```

原始 dump 在 `tmp/asmdump/out/`（本轮新增 `30_` ~ `72_*.txt`）。
临时编译探针在 `tmp/modprobe/`（`Probe2.cs` / `Probe3.cs` / `SurfaceProbe.cs`），都是"只要求编过"的探针，不是 mod。
