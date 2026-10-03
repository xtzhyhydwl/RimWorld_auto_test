# RimWorld 1.6 游戏 API 符号确证（写「自动种田」mod 用）

> **取证时间**：2026-10-03 16:55–17:05（Asia/Shanghai）
> **目标程序集**：`D:\SteamLibrary\steamapps\common\RimWorld\RimWorldWin64_Data\Managed\Assembly-CSharp.dll`
> **程序集版本**：`Assembly-CSharp, Version=1.6.9676.17735, Culture=neutral, PublicKeyToken=null`
> （文件 15 777 280 字节，LastWriteTime 2026-07-01 08:11:34；`GetTypes()` 报 16 130 个可用类型）
> **游戏实例**：**全程没有动**。没 start/stop、没调桥接、没往游戏目录写任何文件。本轮只读程序集与游戏 `Data\*.xml`。

---

## 0. 怎么取证的（以及这套手段的边界）

本机**没有** dnSpy / ILSpy / Mono.Cecil / DecompilerServer（已全盘确认），所以本轮现造了两个只读工具，
放在 **`tools\asmdump\`**（`README.md` 里有完整复现命令）：

| 工具 | 干什么 | 证据类型 |
| --- | --- | --- |
| `AsmDump.cs` | 反射列类型/字段/属性（**含 get/set 各自可见性**）/方法（完整参数+默认值）/枚举值/`const` 字面量 | **【确证·元数据】** |
| `IlDump.cs` | 极简 IL 反汇编（token 解析成名字）+ `--usages` 全程序集扫某字段的读写点 | **【确证·IL】** |
| `ApiProbe.cs` | 把符号写成真代码用 Roslyn 编一遍 | **【确证·编译】** |

两个工具都**不执行游戏代码**（只读元数据、`GetMethodBody()`、const 用 `GetRawConstantValue()`）。
原始 dump 全在 `tmp\asmdump\out\*.txt`，可用 README 里的命令重生成。

**证据等级**（全文统一）：

- **【确证·元数据】** = 从 DLL 元数据反射出来的签名，精确到可见性与参数默认值
- **【确证·IL】** = 从方法体 IL 逐条读出来的实现（不是"我印象里是这样"）
- **【确证·编译】** = 该写法真的被 Roslyn 编译通过（最强：连 `out`/`ref`、扩展方法、可见性都验证了）
- **【确证·XML】** = 游戏安装目录里 `Data\**\*.xml` 的原文
- **【未确证】** = 本轮没拿到证据，**不要照抄**

\* 数值型默认值（字段初始化）用 IL 读构造函数拿到，标 **【确证·IL】**。

> ### ⚠ 先纠正一条方法论：`findstr` 命中 ≠ 符号存在
> `findstr /C:"PlantGrowthRateCalculator" Assembly-CSharp.dll` **有 2 行命中**，
> 但那只是子串命中了 **`MapPlantGrowthRateCalculator`**。
> 本轮**精确匹配确认 `RimWorld.PlantGrowthRateCalculator` 这个类型根本不存在**。
> 本仓库 `docs\mod开发基础.md` 里"findstr 够用来确认某类/某方法/某字段存不存在"这句
> **过于乐观** —— 字符串字面量、子串、调试名都会命中。**要下结论必须走元数据或编译。**

---

## A. 食物与营养

### A1. 营养值怎么取

| 符号 | 结论 | 证据 |
| --- | --- | --- |
| `RimWorld.StatDefOf.Nutrition` | **【确证·元数据】** `public static RimWorld.StatDef Nutrition` | `AsmDump … "=RimWorld.StatDefOf"` → `13_StatDefOf.txt:149` |
| `Verse.Thing.stackCount` | **【确证·元数据+编译】** `public int stackCount`（**字段**，不是属性） | `11_Thing.txt:23` |
| `Verse.Thing.def` | **【确证·元数据+编译】** `public Verse.ThingDef def`（字段） | `11_Thing.txt:11` |
| `Verse.ThingDef.stackLimit` | **【确证·元数据+编译】** `public int stackLimit`（字段） | `09_ThingDef.txt:181` |

**`Thing.GetStatValue` 不存在！** 它是 `RimWorld.StatExtension` 里的**扩展方法**：

```
【确证·元数据+编译】RimWorld.StatExtension（abstract sealed 静态类）
  public static float GetStatValue(this Verse.Thing thing, RimWorld.StatDef stat,
                                   bool applyPostProcess = True, int cacheStaleAfterTicks = -1)   [EXTENSION]
  public static float GetStatValueForPawn(this Verse.Thing thing, StatDef stat, Pawn pawn, bool applyPostProcess = True)
  public static float GetStatValueAbstract(this Verse.BuildableDef def, StatDef stat, ThingDef stuff = null)
  public static float GetStatValueAbstract(this RimWorld.AbilityDef def, StatDef stat, Pawn forPawn = null)
  public static void  SetStatBaseValue(this BuildableDef def, StatDef stat, float newBaseValue)
  public static bool  StatBaseDefined(this BuildableDef def, StatDef stat)
```

⇒ 写法是 `thing.GetStatValue(StatDefOf.Nutrition)`，需要 **`using RimWorld;`**（不是 `Verse`）。
`Verse.Thing` 自己的成员表里**没有** `GetStatValue`（`11_Thing.txt` 全表已核对）。

**一堆食物的营养值：**
```csharp
float nutrition = thing.GetStatValue(StatDefOf.Nutrition) * thing.stackCount;   // 【确证·编译】
```

**相关旁证（都【确证·元数据】）**：`RimWorld.FoodUtility.GetNutrition(Pawn eater, Thing foodSource, ThingDef foodDef)`、
`NutritionForEater(Pawn, Thing)`、`StackCountForNutrition(float, float)`、`WillIngestStackCountOf(...)` 都存在，
它们比裸 stat 多算"吃的人"相关的修正。

**植物类食物的营养会随生长变化**【确证·元数据+XML】：`Nutrition` 这个 StatDef 挂了
`<li Class="StatPart_PlantGrowthNutritionFactor" />`（`Data\Core\Defs\Stats\Stats_Basics_General.xml:295`），
对应代码 `RimWorld.PlantUtility.NutritionFactorFromGrowth(Verse.ThingDef def, float plantGrowth)` 存在。

### A2. 怎么判断一个 Thing 是食物

**命名空间坑**：类叫 **`RimWorld.ThingCategoryDefOf`**（**不是** `Verse.ThingCategoryDefOf` —— 后者不存在）。
41 个 public static 字段全表见 `14_ThingCategoryDefOf.txt`。
【确证·元数据】相关的有：

```
Animals  Apparel  …  FoodCategories: Foods   MeatRaw   PlantFoodRaw   Drugs   Medicine
EggsFertilized  EggsUnfertilized  Fish  Textiles  Wools  Leathers  Corpses*  …
```

| 题目里的符号 | 结论 |
| --- | --- |
| `ThingCategoryDefOf.Foods` | **【确证·元数据+编译】** 存在 |
| `ThingCategoryDefOf.FoodsPlant` | **【确证·不存在】** 该字段不存在；全 `Data` 目录 grep `FoodsPlant` **0 命中** |
| `ThingCategoryDefOf.FoodsAnimal` | **【确证·不存在】** 同上，0 命中 |
| `ThingCategoryDefOf.PlantFoodRaw` / `MeatRaw` | **【确证·元数据】** 存在（这才是"植物性/肉"的原始分类） |
| `Verse.ThingCategoryDefOf` | **【确证·不存在】** 命名空间是 `RimWorld` |

**真实的分类树**【确证·XML】（`Data\Core\Defs\ThingCategoryDefs\ThingCategories.xml`）：
```
Root → Foods → { FoodMeals, FoodRaw → { MeatRaw, PlantFoodRaw, AnimalProductRaw → { EggsUnfertilized, … } } }
```

**`Verse.ThingDef` 相关成员**：

| 成员 | 类型 | 证据 |
| --- | --- | --- |
| `thingCategories` | `List<Verse.ThingCategoryDef>`（public 字段） | 【确证·元数据+编译】 |
| `IsNutritionGivingIngestible` | `public bool`（**属性**，get-only） | 【确证·元数据+编译】 |
| `IsIngestible` / `IsPlant` / `IsDeadPlant` / `IsNutritionGivingIngestibleForHumanlikeBabies` | `public bool` 属性 | 【确证·元数据】 |
| `ingestible` | `RimWorld.IngestibleProperties`（public 字段） | 【确证·元数据】 |
| `plant` | `RimWorld.PlantProperties`（public 字段） | 【确证·元数据】 |
| `race` | `Verse.RaceProperties`（public 字段） | 【确证·元数据】 |
| `BlocksPlanting(bool canWipePlants = False)` | 方法 | 【确证·元数据】 |

**判"是食物"的两种稳妥写法**（都过了编译）：
```csharp
bool a = def.IsNutritionGivingIngestible;
bool b = ThingCategoryDefOf.Foods.ContainedInThisOrDescendant(def);   // 走分类树，含子类
```
`Verse.ThingCategoryDef` 的公开成员【确证·元数据】：`parent`、`childCategories`、`childThingDefs`、
`DescendantThingDefs`、`DescendantSpecialThingFilterDefs`、`Parents`、`ThisAndChildCategoryDefs`、
`ContainedInThisOrDescendant(ThingDef)`、`static Named(string defName)`。

### A3. 一个殖民者每天吃多少 nutrition —— **能算，1.6/天**

**符号表**

| 符号 | 结论 | 证据 |
| --- | --- | --- |
| `RimWorld.Need_Food` : `RimWorld.Need` | 存在 | 【确证·元数据】 |
| `Need_Food.MaxLevel` | `public float get` —— **不是常量 1.0**，见下面 IL | 【确证·元数据 + IL】 |
| `Need_Food.BaseFoodFallPerTick` | `public static const float = 2.6666667E-05` | 【确证·元数据】 |
| `Need_Food.BaseHungerRate(LifeStageDef, ThingDef)` | `public static float` | 【确证·元数据】 |
| `Need_Food.FoodFallPerTick` | `public float get` | 【确证·元数据】 |
| `Need_Food.FoodFallPerTickAssumingCategory(HungerCategory, bool ignoreMalnutrition = False)` | `public float` | 【确证·元数据】 |
| `RimWorld.NeedDef.fallPerDay` / `baseLevel` / `seekerFallPerHour` / `seekerRisePerHour` | 都有 | 【确证·元数据】 |
| `RimWorld.NeedDef.food` / `NeedDef.maxLevel` | **不存在这两个字段** | 【确证·元数据】`17_NeedDef.txt` 全表只 26 个字段 |
| `Verse.RaceProperties.baseHungerRate` | `public float`，**构造函数默认 1.0** | 【确证·元数据 + IL】 |
| `RimWorld.LifeStageDef.hungerRateFactor` / `foodMaxFactor` / `bodySizeFactor` | `public float`，ctor 默认 **都是 1.0** | 【确证·IL】 |
| `RimWorld.HungerCategory` | `Fed=0, Hungry=1, UrgentlyHungry=2, Starving=3` | 【确证·元数据】 |
| `Verse.ProgramState` | `Entry=0, MapInitializing=1, Playing=2` | 【确证·元数据】 |
| `Pawn_NeedsTracker.food` | 可访问（`p.needs.food` 编译通过） | 【确证·编译】 |

**`MaxLevel` 的真实实现**【确证·IL，`Need_Food::get_MaxLevel` 全文】：
```csharp
if (Current.ProgramState != ProgramState.Playing)      // != 2
    return pawn.BodySize * pawn.ageTracker.CurLifeStage.foodMaxFactor;
return pawn.GetStatValue(StatDefOf.MaxNutrition, applyPostProcess: true, cacheStaleAfterTicks: 15);
```

**`BaseHungerRate` 的实现**【确证·IL，全文】：
```csharp
return lifeStage.hungerRateFactor * pawnDef.race.baseHungerRate * 2.6666667E-05f;
```

**`FoodFallPerTick` 的乘算链**【确证·IL】：
```
FoodFallPerTick = FoodFallPerTickAssumingCategory(CurCategory, ignoreMalnutrition: false)
FoodFallPerTickAssumingCategory =
      BaseHungerRate(CurLifeStage, def)
    * HungerLevelUtility.HungerMultiplier(hunger)
    * pawn.health.hediffSet.GetHungerRateFactor([Malnutrition?])
    * (pawn.story?.traits?.HungerRateFactor ?? 1f)
    * (bed != null ? bed.GetStatValue(StatDefOf.BedHungerRateFactor) : 1f)
    [+ Biotech 且有 Lactating：HediffComp_Lactating.AddedNutritionPerDay / 60000f]
    [* Biotech 且有基因：GeneTuning.MetabolismToFoodConsumptionFactorCurve.Evaluate(ΣbiostatMet)]
    [Anomaly 且在 holding platform 上 → 直接置 0]
```
`HungerLevelUtility.HungerMultiplier`【确证·IL】：`Fed=1, Hungry=0.5, UrgentlyHungry=0.25, Starving=0`
⇒ **吃饱时才是满速率**，越饿消耗越慢。

**⇒ 成年人类每天消耗多少：**

| 量 | 值 | 依据 |
| --- | --- | --- |
| `race.baseHungerRate` | 1.0 | 人类 ThingDef 未覆盖（`Races_Humanlike.xml` 无 `baseHungerRate`）【确证·XML】；`RaceProperties` ctor 默认 1.0【确证·IL】 |
| `LifeStageDef.hungerRateFactor`（HumanlikeAdult） | 1.0 | XML 未设【确证·XML】；ctor 默认 1.0【确证·IL】 |
| `BaseHungerRate` | 2.6666667e-5 /tick | 1.0 × 1.0 × 2.6666667e-5【确证·IL】 |
| 每游戏天 | 60000 tick | 通用知识 + 本仓库 `docs\时间推进实测.md` |
| **消耗速率** | **1.6 need 单位/天** | 2.6666667e-5 × 60000【确证·IL】 |
| `MaxLevel` | **1.0** | `MaxNutrition` StatDef `defaultBaseValue=1`，parts = `StatPart_BodySize` + `StatPart_LifeStageMaxFood`；人类 bodySize=1、foodMaxFactor=1 ⇒ 1.0【确证·XML】+【确证·IL】 |
| **⇒ 成年人类** | **≈ 1.6 nutrition / 游戏天**（吃饱档、无特性/基因/卧床修正） | 上述推导 |

**其他可用成员**【确证·元数据】：`NutritionWanted = MaxLevel - CurLevel`【确证·IL】、
`NutritionBetweenHungryAndFed = (1 - PercentageThreshHungry) * MaxLevel`【确证·IL】、
`Starving`、`TicksStarving`、`TicksUntilHungryWhenFed`、`CurCategory`、`GetTipString()`。

---

## B. 作物与生长（核心）

### B4. `RimWorld.Plant` 的成员与可见性

`RimWorld.Plant : Verse.ThingWithComps`。**注意可见性——题目里点名的三个其实外部调不到：**

| 成员 | 签名 | 能不能外部调 |
| --- | --- | --- |
| `Growth` | `public float get; public set;` | ✅ 可读**可写** 【确证·元数据+编译】 |
| `GrowthRate` | `public float get;` | ✅ 【确证·元数据+编译】 |
| `GrowthPerTick` | **`protected float get;`** | ❌ **CS0122**【确证·编译】 |
| `Resting` | **`protected bool get;`** | ❌ 【确证·元数据】 |
| `HasEnoughLightToGrow` | **`protected bool get;`** | ❌ 【确证·元数据】 |
| `TicksUntilFullyGrown` | **`protected int get;`** | ❌ 【确证·元数据】 |
| `GrowthPercentString` / `LeaflessTemperatureThresh` | `protected get` | ❌ 【确证·元数据】 |
| `GrowthRateFactor_Light` / `_Fertility` / `_Temperature` / `_Drought` / `_NoxiousHaze` | `public float get` | ✅ 【确证·元数据+编译】 |
| `LifeStage` | `public RimWorld.PlantLifeStage get` | ✅ 【确证·元数据+编译】 |
| `HarvestableNow` / `HarvestableSoon` / `IsCrop` / `IngestibleNow` | `public bool get` | ✅ 【确证·元数据】 |
| `YieldNow()` | `public int YieldNow()` | ✅ 【确证·元数据+编译】 |
| `CanYieldNow()` | `public bool CanYieldNow()` | ✅ 【确证·元数据】 |
| `def.plant` | `Verse.ThingDef.plant` 是 **public 字段**，类型 `RimWorld.PlantProperties` | ✅ 【确证·元数据+编译】 |

`RimWorld.PlantLifeStage`【确证·元数据】：**`Sowing = 0, Growing = 1, Mature = 2`**
（`Plant.LifeStage` 的 IL：`growth < 0.0001` → Sowing；`growth > 0.999` → Mature；否则 Growing【确证·IL】）

**每 tick 长多少 —— 入口就是 `GrowthPerTick`，全文如下**【确证·IL】：
```csharp
protected float GrowthPerTick {
  get {
    if (LifeStage != PlantLifeStage.Growing) return 0f;                       // 幼苗/成熟株不"生长"
    if (Resting) return 0f;
    if (!def.plant.vacuumResistant
        && VacuumUtility.GetVacuum(Position, Map) >= 0.5f) return 0f;         // 真空
    return (1f / (60000f * def.plant.growDays)) * GrowthRate;                 // ★ 核心公式
  }
}
```
⇒ **`GrowthPerTick = GrowthRate / (60000 × def.plant.growDays)`**，
也就是"**growDays 天、且 GrowthRate 恒为 1 时长满**"。

`GrowthPerTick` 是 protected，外部要自己算就用公开的 `GrowthRate` + `def.plant.growDays` 复刻上式（已过编译）。

`Resting` 的 IL【确证·IL】：与 `GenLocalDate.DayPercent(...)` 的 **0.25 / 0.8** 两个阈值比较
（早于 0.25 或晚于 0.8 即 resting）——和实机 inspect 里夜里 `"Growth rate: 0% (resting, needs light level 51%)"` 吻合。

### B5. 生长速率的影响因子（本次调研核心）

> ❌ **`RimWorld.PlantGrowthRateCalculator` 这个类不存在**【确证·元数据】。
> `findstr` 的 2 行命中是子串命中了 `MapPlantGrowthRateCalculator`。
> 你记忆里的 `TemperatureFactor` / `FertilityFactor` / `LightFactor` 这些**方法名也都不存在**。

真实的两个东西：

#### (1) `RimWorld.PlantUtility`（`abstract sealed` 静态类）——**单因子公式在这里**【确证·元数据】

```csharp
public static float GrowthRateFactorFor_Fertility(Verse.ThingDef def, float fertilityAtCell)
public static float GrowthRateFactorFor_Light(Verse.ThingDef def, float glow)
public static float GrowthRateFactorFor_Temperature(Verse.ThingDef plant, float cellTemp)
```

**逐条实现**【确证·IL】：
```csharp
// Fertility
if (def.plant.completelyIgnoreFertility) return 1f;
return fertilityAtCell * def.plant.fertilitySensitivity + (1f - def.plant.fertilitySensitivity);

// Light
if (def.plant.growMinGlow == def.plant.growOptimalGlow && glow == def.plant.growOptimalGlow) return 1f;
return GenMath.InverseLerp(def.plant.growMinGlow, def.plant.growOptimalGlow, glow);

// Temperature
if (cellTemp < plant.plant.minOptimalGrowthTemperature)
    return Mathf.InverseLerp(plant.plant.minGrowthTemperature, plant.plant.minOptimalGrowthTemperature, cellTemp);
if (cellTemp > plant.plant.maxOptimalGrowthTemperature)
    return Mathf.InverseLerp(plant.plant.maxGrowthTemperature, plant.plant.maxOptimalGrowthTemperature, cellTemp);
return 1f;
```
`Verse.GenMath.InverseLerp(a,b,value)`【确证·IL】：`a == b ? (value >= a ? 1f : 0f) : Mathf.InverseLerp(a, b, value)`。

#### (2) `Plant.GrowthRate` = 五个因子的乘积【确证·IL，全文】

```csharp
public float GrowthRate {
  get {
    if (Blighted) return 0f;                                        // 枯萎病 → 完全不长
    if (Spawned && !PlantUtility.GrowthSeasonNow(Position, Map, def)) return 0f;   // 非生长季 → 0
    return GrowthRateFactor_Fertility
         * GrowthRateFactor_Temperature
         * GrowthRateFactor_Light
         * GrowthRateFactor_NoxiousHaze
         * GrowthRateFactor_Drought;
  }
}
```

**`Plant` 上各因子的实现**【确证·IL】：
```csharp
GrowthRateFactor_Fertility =
    PlantUtility.GrowthRateFactorFor_Fertility(def, map.fertilityGrid.FertilityAt(Position));

GrowthRateFactor_Light =
    PlantUtility.GrowthRateFactorFor_Light(def, map.glowGrid.GroundGlowAt(Position, false, false));

GrowthRateFactor_Temperature =
    GenTemperature.TryGetTemperatureForCell(Position, Map, out t)          // 取不到温度就直接返回 1f
        ? PlantUtility.GrowthRateFactorFor_Temperature(def, t) : 1f;

GrowthRateFactor_NoxiousHaze = NoxiousHazeUtility.IsExposedToNoxiousHaze(this) ? 0.5f : 1f;

GrowthRateFactor_Drought =
      !ModsConfig.OdysseyActive                                        ? 1f
    : (CurrentlyCultivated() && (c.GetEdifice(Map)?.def.building.SupportsPlants ?? false)) ? 1f
    : def.plant.cavePlant                                              ? 1f
    : map.gameConditionManager.ConditionIsActive(GameConditionDefOf.Drought) ? 0.3f
    : 1f;

HasEnoughLightToGrow = GrowthRateFactor_Light > 0.001f;                 // protected
```
> `map.glowGrid.GroundGlowAt(IntVec3, bool, bool) → float` 是**用编译探针确定的**
> （`GameGlowAt` / `TotalGlowAt` / `GlowAt` / `GetGlow` 都编译失败，只有 `GroundGlowAt` 过）【确证·编译】。

#### (3) `Verse.MapPlantGrowthRateCalculator` —— 长期规划用，不是实时生长率【确证·元数据】

```
Verse.MapPlantGrowthRateCalculator                              (class)
  public void  BuildFor(RimWorld.Planet.PlanetTile tile)
  public void  BuildFor(Verse.Map map)
  public float GrowthRateForDay(int nowTicks, Verse.ThingDef plantDef, Verse.TerrainDef terrainDef)
  public float QuadrumGrowthRateFor(RimWorld.Quadrum quadrum, ThingDef plantDef, TerrainDef terrainDef)
  public static bool IsEdibleByPastureAnimals(ThingDef foodDef)
  public static bool IsPastureAnimal(ThingDef td)
  props: List<TerrainDef> TerrainDefs / List<ThingDef> WildGrazingPlants / List<ThingDef> GrazingAnimals

Verse.MapPlantGrowthRateCalculator+GrowthRateAccumulator
  public readonly ThingDef plantDef
  public float GrowthRateForGlow { get; }   public float GrowthRateForTemperature { get; }
  public void Accumulate(GrowthRateAccumulator other) / Accumulate(float grTemp, float grGlow)

Verse.MapPlantGrowthRateCalculator+PlantGrowthRates
  public readonly Dictionary<ThingDef, GrowthRateAccumulator> byPlant
  public GrowthRateAccumulator For(ThingDef plantDef)
```
用法（过了编译）：`new MapPlantGrowthRateCalculator(); calc.BuildFor(map); calc.GrowthRateForDay(GenTicks.TicksGame, plantDef, terrainDef);`

**其他 PlantUtility 公开方法**【确证·元数据】（完整表见 `07_PlantUtility.txt`）：
`CanEverPlantAt` / `CanNowPlantAt` / `CanSowOnGrower` / `SandAllowsPlanting` / `SnowAllowsPlanting` /
`GrowthSeasonNow` / `AdjacentSowBlocker` / `ValidPlantTypesForGrowers` / `NutritionFactorFromGrowth` /
`GetWindExposure` / `PawnWillingToCutPlant_Job` / `TreeMarkedForExtraction` / `LogPlantProportions`。

### B6. 光照

- **`PlantProperties` 里没有 `needsLight` 这个字段**（完整字段表 81 个已 dump，见 `02_PlantProperties.txt`）【确证·元数据】。
  相关的是 **`growMinGlow`** 与 **`growOptimalGlow`**（都是 `public float`）。
- **构造函数默认值**【确证·IL】：**`growMinGlow = 0.51`**、**`growOptimalGlow = 1`**
  —— 正好对上实机 inspect 文本里的 `"needs light level 51%"`（见 `docs\食物自动化-技术侦察.md`），互相印证。
- **"低于多少光照就停止生长"**：
  - 一般情况 `LightFactor = InverseLerp(growMinGlow, growOptimalGlow, glow)`，且 `Mathf.InverseLerp` 会夹到 `[0,1]`
    ⇒ **`glow ≤ growMinGlow`（默认 0.51）时因子为 0，完全不生长**；`glow ≥ growOptimalGlow`（默认 1）时因子为 1。
  - 特例：`growMinGlow == growOptimalGlow` 时（如 `Nutrifungus`: 0/0、`Plants_Cave`），
    **只有 glow 恰好等于该值**才返回 1，否则 0。
  - 另外 `Plant.Resting`（时段 0.25–0.8 之外）会让 `GrowthPerTick` 直接返回 0 —— **夜里不长是这条**，与光照因子无关。
- `PlantProperties` 里另外还有：`cavePlant`、`cavePlantWeight`、`dieIfNoSunlight`、`diesToLight`、
  `showInFrozenWater`、`mustBePermanentDarknessToSow` 等【确证·元数据】。

### B7. 产量

| 题目里的符号 | 结论 |
| --- | --- |
| `PlantProperties.harvestYield` | **【确证·元数据】** `public float harvestYield`（ctor 默认 0，各植物 XML 给值；`Plant_Rice` = **6**） |
| `Plant.YieldNow()` | **【确证·元数据+编译+IL】** `public int YieldNow()` 存在，实现见下 |
| `PlantProperties.harvestAfterGrowth` | **【确证·元数据】** 字段**存在**（`public float`，ctor **默认 0**）——**但它不是成熟阈值！** 见下 |
| `RimWorld.StatDefOf.PlantHarvestYield` | **【确证·元数据】** `public static RimWorld.StatDef PlantHarvestYield` 存在 |

**`YieldNow()` 的确切算法**【确证·IL，全文】：
```csharp
public int YieldNow() {
  if (!CanYieldNow()) return 0;
  float yield = def.plant.harvestYield;
  float t = Mathf.InverseLerp(def.plant.harvestMinGrowth, 1f, growthInt);   // ★ 阈值是 harvestMinGrowth
  t = 0.5f + t * 0.5f;                                                      //   最低也有 50%
  yield *= t;
  yield *= Mathf.Lerp(0.5f, 1f, HitPoints / (float)MaxHitPoints);           //   受伤减产，最低 50%
  if (def.plant.harvestYieldAffectedByDifficulty)
      yield *= Find.Storyteller.difficulty.cropYieldFactor;
  return GenMath.RoundRandom(yield);
}
public bool CanYieldNow() => HarvestableNow && def.plant.harvestYield > 0f && !Blighted;
public bool HarvestableNow => def.plant.Harvestable && growthInt > def.plant.harvestMinGrowth;
// PlantProperties.Harvestable => harvestYield > 0.001f       【确证·IL】
```

### ⚠ `harvestAfterGrowth` 的真相（题面假设不成立）

题面写「`harvestAfterGrowth`（成熟阈值，默认 1.0？）」——**两条都不对**：

1. **默认不是 1.0**：`PlantProperties` 构造函数**根本没给它赋值**，默认 **0f**【确证·IL】。
2. **它不是收获阈值**：`HarvestableNow` 用的是 **`harvestMinGrowth`**（ctor 默认 **0.65**），不是它。

**全程序集扫描 `harvestAfterGrowth` 的读写点，总共只有 2 处**【确证·IL，`IlDump --usages`】：
```
bool RimWorld.PlantProperties::get_HarvestDestroys()                       [ldfld harvestAfterGrowth]
void RimWorld.Plant::PlantCollected(Verse.Pawn by, PlantDestructionMode m) [ldfld harvestAfterGrowth]
```
⇒ 它管的是"**收获后植株是否被摧毁 / 能否再生**"，跟"什么时候能收"无关。
（这两处方法体的**具体判断逻辑本轮没逐行读** → 语义细节【未确证】。）

**`PlantHarvestYield` 这个 stat 用在哪**【确证·IL，全程序集扫描，只有 1 处】：
```
void RimWorld.JobDriver_PlantWork+<>c__DisplayClass11_0::<MakeNewToils>b__1(int delta)
```
⇒ 它是在**收获作业（由小人执行）**里施加的（技能/属性修正），
**`Plant.YieldNow()` 内部不读这个 stat**。想在 mod 里预估"实际入库多少"，两条都要算。

**收获阈值相关默认值一览**【确证·IL，`PlantProperties` ctor】：
`growDays=2`、`harvestMinGrowth=0.65`、`harvestWork=10`、`sowWork=10`、`growMinGlow=0.51`、
`growOptimalGlow=1`、`minOptimalGrowthTemperature=6`、`maxOptimalGrowthTemperature=42`、
`maxGrowthTemperature=58`、`fertilityMin=0.9`、`fertilitySensitivity=0.5`、
`harvestFailable=true`、`harvestYieldAffectedByDifficulty=true`、`autoHarvestable=true`、
`dieIfNoSunlight=true`、`dieFromToxicFallout=true`、`lifespanDaysPerGrowDays=8`。

**几个作物的真实数值**【确证·XML，`Plants_Cultivated_Farm.xml`】：

| ThingDef | growDays | harvestYield | harvestMinGrowth | fertilitySensitivity | sowMinSkill | sowTags |
| --- | --- | --- | --- | --- | --- | --- |
| `Plant_Rice` | 3 | 6 | 0.65(默认) | 0.5(默认) | — | Ground, Hydroponic |
| `Plant_Potato` | 5.8 | 11 | 0.65 | 0.4 | — | Ground, Hydroponic |
| `Plant_Corn` | 11.3 | 22 | 0.65 | 0.5 | — | Ground |
| `Plant_Haygrass` | 7 | 18 | 0.65 | 0.6 | — | Ground |
| `Plant_Strawberry` | 4.6 | 8 | 0.65 | 0.5 | 5 | Ground, Hydroponic |
| `Plant_Healroot` | 7 | 1 | 0.65 | 0.5 | 8 | Ground, Hydroponic |
| `Plant_Devilstrand` | 22.5 | 6 | 0.65 | 0.5 | 10 | Ground |

> ⚠ **两条读 XML 时必须知道的继承关系**（否则会读错）：
> 1. **`harvestMinGrowth` 的"实际默认"分两种**：
>    - 上表这些**一年生作物**都没在 XML 里写它 → 用 **C# ctor 默认 0.65**
>    - **树**继承 `TreeBase`，那里把它改成 **0.40**（`Plants_Bases.xml:110`，同处还有
>      `harvestFailable=false`、`blockAdjacentSow=true`、`mustBeWildToSow=true`、
>      `sowWork=4000`、`sowMinSkill=6`、`sowResearchPrerequisites=[TreeSowing]`）【确证·XML】
>    - 全 Data 目录里显式写 `harvestMinGrowth` 的只有 3 处：`TreeBase`(0.40)、
>      `Plants_Wild_Arid.xml:103`(0.2)、`Plants_Special.xml:25`(0)
> 2. **`ParentName` 继承**：`Plant_Healroot` 的 `harvestYield` / `harvestWork` /
>    `harvestedThingDef` **不在它自己的块里**，而在抽象父类 `HealrootBase` 里（`growDays` 被它覆盖成 7）
>    【确证·XML】。只读 `Plant_Healroot` 那一段会漏掉 `harvestYield=1`、`harvestedThingDef=MedicineHerbal`。
>    ⇒ **不要在 XML 里逐块手抄数值；要读"合并后的 Def"**（这也正是 `docs\mod开发基础.md` 第四节说的）。

### B8. 成熟与收获

| 符号 | 结论 |
| --- | --- |
| `RimWorld.Plant.HarvestableNow` | **【确证·元数据】** `public bool get` |
| `RimWorld.Plant.HarvestableSoon` | **【确证·元数据】** `public bool get` |
| `RimWorld.JobDefOf.Harvest` | **【确证·元数据】** 存在；同族还有 `HarvestDesignated`、`CutPlant`、`CutPlantDesignated`、`Sow`、`PlantSeed` |
| `RimWorld.DesignationDefOf.HarvestPlant` | **【确证·元数据】** 存在；同族 `CutPlant` |
| `RimWorld.Plant.PlantCollected(Pawn by, PlantDestructionMode)` | **【确证·元数据】** |
| `RimWorld.PlantDestructionMode` | **【确证·元数据】** 枚举存在 |

---

## C. 种植区

### C9. `RimWorld.Zone_Growing`

```
RimWorld.Zone_Growing : Verse.Zone
    FIELD  public bool allowCut
    FIELD  public bool allowSow
    FIELD  private Verse.ThingDef plantDefToGrow          ← 真正的存储
    PROP   public Verse.ThingDef PlantDefToGrow { public get; }        ★ 没有 setter
    CTOR   public Zone_Growing(Verse.ZoneManager zoneManager)          ★ 可用
    CTOR   public Zone_Growing()
    METHOD public void SetPlantDefToGrow(Verse.ThingDef plantDef)
    METHOD public Verse.ThingDef GetPlantDefToGrow()
    METHOD public bool CanAcceptSowNow()
    METHOD public void AddCell(Verse.IntVec3 c)
    METHOD public void ContentsStatistics(out int totalPlants, out float averagePlantAgeTicks,
                                          out int oldestPlantAgeTicks, out float averagePlantGrowth,
                                          out float maxPlantGrowth)
    METHOD public static string GrowingQuadrumsDescription(Verse.Map map, ThingDef plantDef = null)
    METHOD public static string GrowingQuadrumsDescription(RimWorld.Planet.PlanetTile tile, ThingDef plantDef = null)
```
全部【确证·元数据】（`04_ZoneGrowing.txt` + `il_ZoneGrowing.txt`）。

**逐条回答题目：**

1. **`PlantDefToGrow` 是属性，类型 `ThingDef`** —— 但它是 **get-only**（`PROP … { public get; }`，**连 private setter 都没有**）【确证·元数据】。
2. **没有 `sowingDef` 这个成员**【确证·元数据】。真正写值的入口是 `SetPlantDefToGrow(Verse.ThingDef)`。
3. **❌ `zone.PlantDefToGrow = def;` 编译不过**（get-only 属性，CS0200）。必须：
   ```csharp
   zone.SetPlantDefToGrow(plantDef);      // 【确证·编译】
   ```
4. **`SetPlantDefToGrow` 里没有任何前置检查**——IL 全文就三条【确证·IL】：
   ```
   IL_0000: ldarg.0
   IL_0001: ldarg.1
   IL_0002: stfld RimWorld.Zone_Growing::plantDefToGrow
   IL_0007: ret
   ```
   纯字段赋值。`CanAcceptSowNow()` 更直接：**`return true;`（常量）**【确证·IL】。
   ⇒ 代码层**不校验**"这作物能不能在这种地/这人会不会种/研究解锁没"。
   这些约束在别处：`PlantProperties.sowTags`（`Sowable` = sowTags 非空）、`sowMinSkill`、
   `sowResearchPrerequisites`、`PlantUtility.ValidPlantTypesForGrowers(...)`，
   以及实际播种作业里的 `PlantUtility.CanEverPlantAt/CanNowPlantAt`。

**`RimWorld.IPlantToGrowSettable`（接口，抽象出"能设定种什么"）**【确证·元数据+编译】：
```csharp
public interface IPlantToGrowSettable {
    IEnumerable<IntVec3> Cells { get; }
    Verse.Map Map { get; }
    bool CanAcceptSowNow();
    Verse.ThingDef GetPlantDefToGrow();
    void SetPlantDefToGrow(Verse.ThingDef plantDef);
}
```
`RimWorld.Zone_Growing` 和 **`RimWorld.Building_PlantGrower`（水培盆）** 都实现它。
`Building_PlantGrower` 的公开成员【确证·元数据】：`SetPlantDefToGrow(ThingDef)`、`GetPlantDefToGrow()`、
`CanAcceptSowNow()`、`PlantsOnMe`、`PostMake()`、`TickRare()`。
⇒ **写 mod 时面向 `IPlantToGrowSettable` 就能一套代码同时支持露天地和大棚。**

### C10. 土壤 / 地表能不能种

| 题目里的符号 | 结论 |
| --- | --- |
| `Verse.TerrainDef.affordances` | **【确证·元数据】** `public List<Verse.TerrainAffordanceDef> affordances`（字段） |
| `RimWorld.TerrainAffordanceDefOf.GrowSoil` | **【确证·不存在】** ❌ 该类**只有 7 个字段**：`Bridgeable, Heavy, Light, Medium, MovingFluid, SmoothableStone, Walkable` |
| `RimWorld.TerrainAffordanceDefOf.Gravel` | **【确证·不存在】** ❌ 同上（`Gravel` 是 **TerrainDef**，不是 affordance） |
| `Verse.GenPlant` | **【确证·不存在】** ❌ 这个类没有（相关工具类叫 `RimWorld.PlantUtility`） |

**但 `GrowSoil` 确实存在** —— 它是 XML 里的一个 `TerrainAffordanceDef` defName：
`Data\Core\Defs\Misc\TerrainAffordanceDefs\TerrainAffordances.xml:57`（label = "growable"，order 3200）【确证·XML】。
同一份 XML 里还有 `ShallowWater`、`Diggable`、`WaterproofConduitable` —— 这些都**没有**对应的 `DefOf` 字段。

取它的正确姿势（已过编译）：
```csharp
TerrainAffordanceDef growSoil = DefDatabase<TerrainAffordanceDef>.GetNamed("GrowSoil", false);
// DefDatabase<T>: public static T GetNamed(string defName, bool errorOnFail = true)  // 【确证·元数据】
//                 public static T GetNamedSilentFail(string defName)
bool ok = c.GetAffordances(map).Contains(growSoil);   // GridsUtility.GetAffordances 是扩展方法
TerrainDef gravel = c.GetTerrain(map);                // Gravel 是 TerrainDef（Terrain_Natural.xml:238）
```

**⚠ 关键：游戏自己判"能不能种"根本不看 affordances，看的是「肥力」。**
`RimWorld.PlantUtility.CanEverPlantAt` 的 IL 前 6 步【确证·IL】：
```csharp
blockingThing = null;
if (plantDef.category != ThingCategory.Plant) Log.Error("Checking CanGrowAt with " + x + " which is not a plant.");
if (!GenGrid.InBounds(c, map))                       return "OutOfBounds";
TerrainDef terrain = c.GetTerrain(map);
if (!plantDef.plant.completelyIgnoreFertility
    && map.fertilityGrid.FertilityAt(c) < plantDef.plant.fertilityMin)          // fertilityMin 默认 0.9
                                                     return "MessageWarningNotEnoughFertility";
if (checkMapTemperature
    && (map.TileInfo.MinTemperature > plantDef.plant.maxGrowthTemperature
     || map.TileInfo.MaxTemperature < plantDef.plant.minGrowthTemperature))
                                                     return "CannotPlantExtremeTemp";
// … 后面还有：terrainBlacklist / TerrainDef.tags（"CannotPlantMissingTerrainTag"）/ 污染 / thingGrid 阻挡（"BlockedBy…"）
```
⇒ **这正好解释了实机里「Growing zone 拒收 Sand、接受 Gravel/Soil」**：`
Sand` 的肥力低于 `fertilityMin`（默认 0.9），跟 affordance 无关。
`RimWorld.FertilityGrid.FertilityAt(Verse.IntVec3 loc) → float` 是公开方法【确证·元数据】；
`FertilityGrid.MaxPollutedFertility = 0.5`（const）—— 污染土肥力 ≤0.5 < 0.9，默认作物种不了。

**判可种的全部公开入口**【确证·元数据+编译】（都是 `PlantUtility` 的**扩展方法**，`this ThingDef` 在首位）：
```csharp
public static bool CanEverPlantAt(this ThingDef plantDef, IntVec3 c, Map map,
                                  bool canWipePlantsExceptTree = false, bool checkMapTemperature = true)
public static AcceptanceReport CanEverPlantAt(this ThingDef plantDef, IntVec3 c, Map map,
                                  out Thing blockingThing, bool canWipePlantsExceptTree = false,
                                  bool checkMapTemperature = true, bool writeNoReason = false)
public static bool CanNowPlantAt(this ThingDef plantDef, IntVec3 c, Map map, bool canWipePlantsExceptTree = false)
public static bool CanSowOnGrower(ThingDef plantDef, object obj)
public static bool SandAllowsPlanting(IntVec3 c, Map map)     // IL: c.GetSandDepth(map) < 0.2f
public static bool SnowAllowsPlanting(IntVec3 c, Map map)     // IL: c.GetSnowDepth(map) < 0.2f
public static bool GrowthSeasonNow(Map map, ThingDef plantDef)
public static bool GrowthSeasonNow(IntVec3 c, Map map, ThingDef plantDef)
public static Thing AdjacentSowBlocker(ThingDef plantDef, IntVec3 c, Map map)
public static IEnumerable<ThingDef> ValidPlantTypesForGrowers(List<IPlantToGrowSettable> sel)
```
> 第 4 个参数**确定是 `out`**：`ref` 版本编译报 `CS1620: 参数 4 必须与关键字"out"一起传递`【确证·编译】。

**`Verse.GridsUtility` —— 60 个 `this IntVec3` 扩展方法，全是现成的格子查询**【确证·元数据】：
`GetTerrain`、`GetFertility`、`GetAffordances`、`GetSandDepth`、`GetSnowDepth`、`GetTemperature`、
`GetPlant`、`GetPlantToGrowSettable`、`GetZone`、`GetFirstThing`、`GetEdifice`、`Roofed`、`IsPolluted`、
`GetRoof`、`GetRoom`、`Filled`、`GetCover`、`GetFirstPawn`、`GetItems`、`GetItemCount`…
**但里面没有** "`CanPlant` / `CanGrow`" 这类现成的布尔判定（成员表已全量核对）——
要判就调 `PlantUtility.CanEverPlantAt` / `CanNowPlantAt`。

**`Verse.Map` 地图网格字段**【确证·元数据+编译】：`fertilityGrid`(公开)、`glowGrid`(公开)、`fogGrid`(公开)、
`terrainGrid`、`thingGrid`、`edificeGrid`、`roofGrid`、`snowGrid`、`sandGrid`、`gasGrid`、`regionGrid` 等。

### C11. 怎么创建一个种植区

> ### ❌ 题面的写法在 1.6 不成立
> **`Verse.ZoneManager` 根本没有 `CreateZone` 方法。** 它的**全部**成员只有：
> ```
> FIELD  public readonly Verse.Map map
> PROP   List<Verse.Zone> AllZones { get; }
> CTOR   public ZoneManager(Verse.Map map)
> METHOD public void DeregisterZone(Verse.Zone oldZone)
> METHOD public void RegisterZone(Verse.Zone newZone)
> METHOD public Verse.Zone ZoneAt(Verse.IntVec3 c)
> METHOD public string NewZoneName(string nameBase)
> METHOD public void ExposeData()
> ```
> 全部【确证·元数据】（`26_ZoneManager.txt`）。
> **`map.zoneManager.CreateZone(ZoneDef, IEnumerable<IntVec3>)` 编译不过。**
>
> 而且 **`RimWorld.ZoneDefOf` 不存在，`ZoneDef` 这个类型也不存在**（全类型名搜索 0 命中）
> ⇒ `RimWorld.ZoneDefOf.Growing` **不存在**【确证·元数据】。1.6 的 zone 是**硬编码类**（`Zone_Growing` / `Zone_Stockpile` / `Zone_Fishing`），不是 Def 驱动。

**真正能建区的两条路：**

**路 1（推荐，纯代码）**【确证·元数据+编译】：
```csharp
var z = new Zone_Growing(map.zoneManager);        // CTOR public Zone_Growing(ZoneManager)
ZoneMaker.MakeZoneWithCells(z, cells);            // Verse.ZoneMaker（abstract sealed 静态类）
// public static Verse.Zone MakeZoneWithCells(Verse.Zone z, IEnumerable<Verse.IntVec3> cells)
```
`Verse.ZoneMaker` 的**全部**公开成员就这一个方法【确证·元数据】。

**路 2（走设计器）**：`RimWorld.Designator_ZoneAdd_Growing`
（`NewZoneLabel`/`CanDesignateCell(IntVec3)`/`MakeNewZone()`；**`MakeNewZone()` 是 `protected`**，外部调不到）
另可用 `RimWorld.Designator_ZoneAdd.IsZoneableCell(IntVec3 c, Map map) → AcceptanceReport`（**public static**）判格子。
【确证·元数据】

**`Verse.Zone`（基类）公开成员**【确证·元数据】：
```
FIELD  public int ID / public List<IntVec3> cells / public ZoneManager zoneManager / public string label / public Color color
PROP   CellCount, Cells, Map, Position, Hidden{get;set;}, RenamableLabel{get;set;}, AllContainedThings, BaseLabel, InspectLabel …
METHOD AddCell(IntVec3), RemoveCell(IntVec3), ContainsCell(IntVec3), Delete(bool playSound), Delete(),
       GetUniqueLoadID(), GetGizmos(), GetInspectString(), CheckContiguous()
```
> ⚠ `ZoneMaker.MakeZoneWithCells` 的**内部行为**（是否自动 `RegisterZone`、是否逐格 `AddCell`、
> 是否校验格子合法性）**本轮没有读 IL** → 【未确证】。这是最该在游戏里实测的一条（见末节）。

---

## D. 殖民者与工作

### D12. 工作优先级

`RimWorld.Pawn_WorkSettings`【确证·元数据】：
```
FIELD  public static const int DefaultPriority = 3
FIELD  public static const int LowestPriority  = 4
PROP   Initialized, EverWork, LimitInitialActiveWorks, WorkGiversInOrderNormal, WorkGiversInOrderEmergency
CTOR   public Pawn_WorkSettings() / Pawn_WorkSettings(Verse.Pawn pawn)
METHOD public int  GetPriority(Verse.WorkTypeDef w)
METHOD public void SetPriority(Verse.WorkTypeDef w, int priority)
METHOD public bool WorkIsActive(Verse.WorkTypeDef w)
METHOD public void Disable(Verse.WorkTypeDef w) / DisableAll()
METHOD public void EnableAndInitialize() / EnableAndInitializeIfNotAlreadyInitialized()
METHOD public void Notify_DisabledWorkTypesChanged() / Notify_UseWorkPrioritiesChanged()
```
**没有 `PriorityOf` 这个成员**【确证·元数据】。

**IL 语义（逐条读出来的）**【确证·IL】：
```csharp
WorkIsActive(w)  => GetPriority(w) > 0;          // ★ 0 == 不参与，确认了
Disable(w)       => SetPriority(w, 0);
SetPriority(w, p) {
    ConfirmInitializedDebug();
    if (p != 0 && pawn.WorkTypeIsDisabled(w)) { Log.Error("Tried to change priority on disabled worktype …"); return; }  // ★ 不生效
    if (p < 0 || p > 4) Log.Message("Trying to set work to invalid priority " + p);                                       // ★ 只警告，仍会写
    priorities[w] = p;  workGiversDirty = true;
    if (p == 0 && pawn.jobs != null) pawn.jobs.Notify_WorkTypeDisabled(w);
}
GetPriority(w) {                                   // ★ 有个坑
    int v = priorities[w];
    if (pawn.RaceProps.Humanlike && v > 0 && !Find.PlaySettings.useWorkPriorities) return 3;   // 非"手动优先级"模式下假装是 3
    return v;
}
```
**结论**：合法优先级 **0..4**，`0 = 不参与`，默认 **3**；`SetPriority` 对 0 有额外副作用（通知 job tracker）。
所有方法开头都 `ConfirmInitializedDebug()` ⇒ **pawn 必须已初始化**（正常 spawn 时游戏会初始化）。

`RimWorld.WorkTypeDefOf.Growing`【确证·元数据+编译】（同族 `PlantCutting`、`Hauling`、`Hunting`、
`Firefighter`、`Doctor`、`Construction`、`Mining`、`Research`… 全表 19 个在 `31_WorkSkills.txt`）。

### D13. 技能读取

`RimWorld.Pawn_SkillTracker`【确证·元数据】：
```
FIELD  public List<RimWorld.SkillRecord> skills
CTOR   public Pawn_SkillTracker(Verse.Pawn newPawn)
METHOD public RimWorld.SkillRecord GetSkill(RimWorld.SkillDef skillDef)
METHOD public void Learn(SkillDef sDef, float xp, bool direct = False, bool ignoreLearnRate = False)
METHOD public float AverageOfRelevantSkillsFor(Verse.WorkTypeDef workDef)
METHOD public RimWorld.Passion MaxPassionOfRelevantSkillsFor(WorkTypeDef workDef)
```
`RimWorld.SkillRecord`【确证·元数据】：
```
FIELD  public RimWorld.SkillDef def / public int levelInt / public RimWorld.Passion passion
       public float xpSinceLastLevel / public float xpSinceMidnight
       const int MaxLevel = 20, MinLevel = 0, MasterSkillThreshold = 14, IntervalTicks = 200
PROP   public int Level { get; set; }      public int Aptitude { get; }    public float XpProgressPercent { get; }
METHOD public int GetLevel(bool includeAptitudes = True) / GetLevelForUI(bool = True) / GetUnclampedLevel()
```
`RimWorld.SkillDefOf.Plants` 存在【确证·元数据】；`RimWorld.SkillDef` 有 `listOrder`、
`disablingWorkTags`、`usuallyDefinedInBackstories` 等字段。

**可用写法（过了编译）**：
```csharp
SkillRecord rec = pawn.skills.GetSkill(SkillDefOf.Plants);
int lvl = rec.Level;              // ⚠ 默认含 aptitude；要"纯训练等级"用 rec.levelInt 或 rec.GetLevel(false)
Passion p = rec.passion;
```
> ⚠ `SkillRecord.Level` 的 getter 等价于 `GetLevel(includeAptitudes: true)`（aptitude 来自基因/植入物），
> `levelInt` 是原始值。**这一条是从成员表推断的**（`GetLevel(bool includeAptitudes = True)` 默认 true +
> `Level` 与 `levelInt` 并存）→ 语义细节标【未确证】。

---

## E. 编译可行性（最小实测）

### 结论：**编译成功** ✅

- 源文件：**`tools\asmdump\ApiProbe.cs`**（这是个**探针，不是 mod**，只要求能编过）
- 产物：`tmp\apiprobe\DSH.ApiProbe.dll`（7168 字节）
- **`COMPILE EXIT=0`**，覆盖 A/B/C/D 四大块的关键符号

**用的命令**（就是 `scripts\build-cebridge.mjs` 那套，改成 `target:library`）：
```
csc.exe /nologo /target:library /nostdlib+ /langversion:7.3 /optimize+ /deterministic+
        /out:<WS>\tmp\apiprobe\DSH.ApiProbe.dll
        /reference:<Managed>\mscorlib.dll
        /reference:<Managed>\System.dll
        /reference:<Managed>\System.Core.dll
        /reference:<Managed>\netstandard.dll
        /reference:<Managed>\UnityEngine.CoreModule.dll
        /reference:<Managed>\Assembly-CSharp.dll
        <WS>\tmp\apiprobe\ApiProbe.cs
```
`csc.exe` = `C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe`

**探针覆盖到的符号**（全部编译通过）：`GetStatValue` 扩展方法 / `StatDefOf.Nutrition` / `stackCount` /
`stackLimit` / `IsNutritionGivingIngestible` / `thingCategories` / `ThingCategoryDefOf.Foods` /
`Need_Food.MaxLevel` / `Need_Food.BaseHungerRate` / `Plant.Growth`(读+写) / `Plant.GrowthRate` /
`Plant.LifeStage` / 5 个 `GrowthRateFactor_*` / `YieldNow()` / `HarvestableNow` / `PlantProperties.*` /
`PlantUtility.GrowthRateFactorFor_*` / `PlantUtility.CanEverPlantAt(... out Thing ...)` /
`SandAllowsPlanting` / `SnowAllowsPlanting` / `c.GetTerrain` / `c.GetPlantToGrowSettable` /
`map.fertilityGrid.FertilityAt` / `map.glowGrid.GroundGlowAt` / `DefDatabase<TerrainAffordanceDef>.GetNamed` /
`MapPlantGrowthRateCalculator` / `Zone_Growing.PlantDefToGrow`(读) + `SetPlantDefToGrow` /
`new Zone_Growing(zoneManager)` + `ZoneMaker.MakeZoneWithCells` / `Pawn_WorkSettings.*` /
`WorkTypeDefOf.Growing` / `SkillDefOf.Plants` / `SkillRecord.Level` / `SkillRecord.passion` /
`JobDefOf.Harvest` / `DesignationDefOf.HarvestPlant`。

**过程中的三次真实报错（很有价值，都写进结论了）**：

1. `CS0122: "Plant.GrowthPerTick" 不可访问` / `Plant.Resting` / `Plant.HasEnoughLightToGrow`
   → 抓出这三个是 **protected**。起因是 AsmDump 早期版本打印属性时**没区分 getter 可见性**，
   把 `protected get` 显示成 `get` 了。已修（现在打印 `{ protected get; }`），
   `tools\asmdump\README.md` 里把这条列为"已知坑 #1"。
2. `ref` 变体 → `CS1620: 参数 4 必须与关键字"out"一起传递` ⇒ `canEverPlantAt(..., out Thing blockingThing, ...)` 是 **`out`**。
3. 用编译探针确定了光照读数的入口是 **`map.glowGrid.GroundGlowAt(IntVec3, bool, bool)`**
   （`GameGlowAt`/`TotalGlowAt`/`GlowAt`/`GetGlow` 全部 CS1061 失败）。

**约束遵守情况**：
- ✅ 产物只写 `$WS\tmp\apiprobe\` 和 `$WS\tmp\asmdump\`，**没有一个字节写进游戏安装目录**
- ✅ **没有**停/启游戏、**没有**调桥接、**没有**动那个正在运行的测试实例
- ⚠ 沙箱下没用 `execFileSync`；用的是 pwsh 直接调用 + `cmd /c` 重定向到工作区文件

**一个侧记（会影响后续编译脚本）**：`csc.exe` **不能**用游戏目录里的 `System.Core.dll` / `mscorlib.dll`
去编**宿主工具**（那样编出来的 exe 一跑就 `CLR exception 0xE0434352`，连异常信息都打不出来）。
编**工具**时用 `%windir%\Microsoft.NET\Framework64\v4.0.30319\` 下的程序集；
编 **mod** 时按 `docs\mod开发基础.md` 引用游戏的 `Managed\`（探针就是这么编过的，没问题）。

---

## ❌ 我没能确证的（不要照抄）

1. **`RimWorld.PlantGrowthRateCalculator`**、**`PlantGrowthRateCalculator.TemperatureFactor/FertilityFactor/LightFactor`**
   —— 精确匹配确证**不存在**。真实名字是 `PlantUtility.GrowthRateFactorFor_*` 和 `Verse.MapPlantGrowthRateCalculator`。
2. **`Verse.GenPlant`** —— 精确匹配确证**不存在**。
3. **`RimWorld.ZoneDefOf` / `ZoneDef` / `map.zoneManager.CreateZone(...)` / `Zone_Growing.sowingDef`** —— 全部确证**不存在**。
4. **`ThingCategoryDefOf.FoodsPlant` / `FoodsAnimal`**、**`Verse.ThingCategoryDefOf`** —— 确证**不存在**。
5. **`TerrainAffordanceDefOf.GrowSoil` / `.Gravel`** —— 确证**不存在**（`GrowSoil` 只能走 `DefDatabase<TerrainAffordanceDef>`）。
6. **`PlantProperties.needsLight`** —— 确证**不存在**（是 `growMinGlow` / `growOptimalGlow`）。
7. **`Pawn_WorkSettings.PriorityOf`** —— 确证**不存在**。
8. **`harvestAfterGrowth` 的语义细节** —— 只确证了它的 2 个读写点（`PlantProperties.get_HarvestDestroys`、
   `Plant.PlantCollected`），**没有逐行读这两处判断逻辑**。只知道它**不是**收获阈值。
9. **`Verse.ZoneMaker.MakeZoneWithCells` 的行为** —— 签名确证，但**内部是否 Register/AddCell/校验未读 IL**。
10. **`RimWorld.Passion` 的枚举值** —— 只确证类型存在（`(int)passion` 能编译）。
11. **`SkillRecord.Level` 是否真的含 aptitude** —— 从成员表推断，未读 IL。
12. **`PlantUtility.CanEverPlantAt` 第 6 步之后**（terrainBlacklist / terrain tags / 污染 / 阻挡物）的
    完整判断顺序 —— 只读到了调用摘要（`terrainBlacklist`、`TerrainDef.tags`、`MessageWarningNotPollutedCell` 等），
    没逐行读完全部 350 行 IL。
13. **`RimWorld.StatDefOf` 的完整字段表** —— 只逐个确认了 `Nutrition` / `PlantHarvestYield` /
    `PlantWorkSpeed` / `MaxNutrition` / `BedHungerRateFactor` 等关键项。
14. **任何运行期数值**（某格 fertility/glow、某 pawn 的 MaxLevel 实测值）—— 本轮**只读程序集，没碰游戏**。
15. **未经游戏验证的假设**：`Zone_Growing` 的播种前置（研究解锁、技能、sowTags vs 地形）是否会
    在实际作业层阻止"已设定但不可种"的作物 —— 代码层没校验，但作业层行为未实测。

---

## 🔬 建议在游戏里实测的

优先级从高到低（都能用现成的 RimBridgeServer 伴生 DLL + 桥接做，**不用改游戏**）：

1. **`ZoneMaker.MakeZoneWithCells` 建出来的 zone 到底能不能用**
   —— `new Zone_Growing(map.zoneManager)` + `MakeZoneWithCells(z, cells)` 之后：
   `z.CellCount` 对不对？`map.zoneManager.AllZones` 里有没有？`get_cells_info` 能不能回读到 `zone`？
   小人会不会去播种？（这是"自动种田"的地基，风险最高的一条）
2. **`SetPlantDefToGrow` 之后小人是否真去种** —— 设成 `Plant_Rice` 推几万 tick，看是否出苗。
3. **`map.glowGrid.GroundGlowAt(c, false, false)` 的读数** vs `Plant.GrowthRateFactor_Light` 的关系，
   以及白天/夜晚 `GrowthPerTick` 实测值 vs 公式 `GrowthRate/(60000×growDays)`。
4. **`Plant.GrowthRate` 的乘算验证** —— 把 5 个因子乘起来和实际 `GrowthRate` 比对（夜/昼/不同肥力各取一组）。
5. **营养消耗实测** —— 给一个殖民者足够食物，观察 `p.needs.food.CurLevel` 的下降速率是否 ≈ **1.6/游戏天**；
   顺带验证 `MaxLevel` 是否 = 1.0。
6. **`harvestMinGrowth`（0.65）与 UI 上"可收获"时机是否一致** —— 顺便验证 `harvestAfterGrowth` 到底
   在 `PlantCollected` 里做了什么（改造 `Plant_*` 的 XML 值做对照实验最快）。
7. **`SetPriority` 的边界** —— 对 disabled work type 调 `SetPriority(Growing, 2)` 是否真的被拒绝（IL 说会 `Log.Error` 并返回）。

---

*本文件由程序集只读取证产出：`tools\asmdump\`（工具源码 + 复现命令），
原始 dump 在 `tmp\asmdump\out\*.txt`，编译探针在 `tools\asmdump\ApiProbe.cs`。*
