# ColonyPilot —— 通用殖民地自动化 mod（命名与结构改造设计）

> **用户 2026-10-04 的定性（这是本文件的总纲）**：
>
> > 「你不要被现在这个 mod 的名字给**误导**了。我本来的打算就是要做一个**几乎能够覆盖所有方面**，
> >   就是至少我们能覆盖的都给它覆盖到的**那种自动化操作的 mod**，
> >   而不只是和实物和 food 相关的。那么在考虑的时候就要考虑**之后会接入更多的操作**。」
>
> 所以这个 mod 的定位是：**殖民地的驾驶员（Pilot），不是食物控制器**。
> 食物只是它现在会开的第一条线。
>
> 状态：**设计（不动代码）**。执行分三阶段，见 §八 —— 现在不做，因为另一个进程正在改同一批域文件。

---

## 一、一句话结构：三层可独立扩展

```
 ① 触发层   TriggerPolicy / IEventSource     —— 什么时候轮到它
 ② 执行层   INeedDomain / IOperationList     —— 轮到了做什么
 ③ 闸门层   IDomainGate                      —— 谁允许它做（托管/威胁/优先级）
```

**判据**：这三层里任何一层的扩展，都不应该让另外两层改代码。
现在的框架**只有②是可扩展的**（`INeedDomain`），①和③是硬编码在协调器里的
（"每天一次 + 时段判断 + 全局开关"）。这就是"加更多操作"会卡住的地方。

---

## 二、命名映射（FoodControl → ColonyPilot）

**触发原因**：名字会持续误导所有后来的设计决定（"这该放哪？" → "这是食物的事吗？"）。
现在改的成本是 67 个文件 / 443 处；越晚改越贵。

### 2.1 映射表

| 现在 | 改成 | 说明 |
| --- | --- | --- |
| mod 名 / packageId `dsh.foodcontrol` | `dsh.colonypilot` | ⚠ 换 packageId = 换了一个 mod，老存档的 `ModsConfig` 条目失效 |
| 目录 `src/foodcontrol-core/` | `src/colonypilot-core/` | 纯逻辑层（不引用游戏程序集） |
| 目录 `src/foodcontrol-game/` | `src/colonypilot-game/` | 游戏适配层 |
| 目录 `mods/FoodControl/` | `mods/ColonyPilot/` | ⚠ 游戏目录里那个 **junction 要重建** |
| DLL `FoodControl.dll` | `ColonyPilot.dll` | ⚠ 探针按**程序集名精确匹配**找 mod → 必须同步改 `MOD_ASM` |
| DLL `FoodControl.Core.dll` | `ColonyPilot.Core.dll` | — |
| 命名空间 `FoodControl.Core` | `ColonyPilot.Core` | 机械替换 |
| 命名空间 `FoodControl.Game` | `ColonyPilot.Game` | 机械替换 |
| 类 `FoodControlGameComponent` | `PilotGameComponent` | 机械替换 |
| 类 `ModBoot` | `PilotMod` | 与 `RecorderMod` 对得上（两个 mod 各自的入口同名风格） |
| 探针 `DSH.FoodProbe` / `foodprobe/*` | `DSH.ColonyProbe` / `colonyprobe/*` | ⚠ **50 个工具名 + 全部脚本**都引用它 |
| 探针常量 `MOD_ASM = "FoodControl"` | `"ColonyPilot"` | ★ 漏了这行**所有**探针工具都会报"mod 未加载" |
| 脚本 `build-core.mjs` / `build-mod.mjs` | 名字可留，**内部路径常量**要改 | `CORE_DIR` / `GAME_DIR` / `DEPLOY_DIR` / `MOD_DIR` / `CORE_DLL` |
| 遥测文件 `foodcontrol-telemetry.jsonl` | `pilot-telemetry.jsonl` | ⚠ 脚本 `analyze-*` / `verify-*` 里有硬编码路径 |
| 意图流 `foodcontrol-intents.jsonl` | `pilot-intents.jsonl` | 同上 |
| 账本 `foodcontrol-buildings.json` | `pilot-buildings.json` | 同上 |
| 日志前缀 `[FoodControl]` | `[ColonyPilot]` | 纯文本，机械替换 |

### 2.2 ★ 什么**不会**被改名破坏（重要，防止过度保守）

| 东西 | 为什么安全 |
| --- | --- |
| **存档键**（`foodControlLastRunDay` / `foodControlSetupDay` / `foodControlTotalUnforbidden` / `foodControlTakeoverMode`） | `Scribe_Values.Look` 的键是**字符串**，与类名/命名空间/程序集名无关 → **保留原键字符串，存档照样读得回来**（改键名才会破，所以**不改**） |
| `INeedDomain.Id`（`"food"` / `"shelter"` / …） | 是块内逻辑标识，遥测里的历史数据靠它对齐 → **保留** |
| 域内的食物概念（`FoodDomain` / `FoodState` / `Calibration`） | **本来就应该带 food 字样** —— 泛化不是"删掉食物"，是"让食物只是一条线" |

### 2.3 ⚠ 真的会破的东西（要提前告诉用户）

1. **老存档的 mod 列表条目**：packageId 一改，游戏认为这是另一个 mod。
   缓解：改名**同时**在 `About.xml` 里保留说明；或者接受"改名后开新档"。
2. **游戏目录里 `Mods/FoodControl` 那个 junction**：必须删掉重建（这是工作区外的操作，需要你确认）。
3. **已部署的 DLL 文件名**：被游戏加载中时**锁着**，所以改名**必须在游戏停止时做**。
4. 任何**外部**引用旧路径的东西（例如你手工记的路径、别处脚本）。

---

## 三、① 触发层：让域**声明**自己的节奏与触发条件

### 3.1 现在的问题（三个域各写一遍同一件事）

```csharp
// ShelterDomain.cs:213-225
public bool ShouldRunToday(DomainContext ctx) {
    int every = CheckEveryDays > 0 ? CheckEveryDays : 3;
    return ctx.Day % every == 0;
}
// StockpileDomain.cs:85-89 ……逐字相同
// TemplateBuildDomain.cs:49 CheckEveryDays = 2 ……又是同一套
```

以及 `FoodDomain.ShouldRunToday` 是**时段敏感**的（8:00–19:00）。
→ 三种节奏语义（按天取模 / 时段窗口 / 事件）被塞进同一个 `bool` 返回值的函数里，
**加第四种就必须再写一遍**，而且协调器的"廉价早退"（`AnyDomainDue`）必须与它
**语义一致**——这是已经踩过的坑（去重 bug 就是"两处判定不一致"导致的）。

### 3.2 设计：把节奏变成**数据**

```csharp
/// 一个域什么时候该跑 —— 替代 `ShouldRunToday` 的实现细节
public sealed class TriggerPolicy
{
    public TriggerKind Kind;
    public int EveryDays = 1;        // DayInterval / DayModulo
    public int HourFrom = 0;         // TimeWindow
    public int HourTo = 24;
    public string[] Events;          // EventDriven: 关心的事件名
    public bool RequiresPlayerHome = true;
}

public enum TriggerKind
{
    DayInterval = 0,   // 每 N 天（按"今天是否已跑"记，不是取模！）
    DayModulo   = 1,   // 第 N 天取模 —— ⚠ 保留旧语义，老域行为不变
    TimeWindow  = 2,   // 时段窗口内（食物域用的）
    EventDriven = 3,   // 只在事件到来时（威胁响应域用的）
    Manual      = 4,   // 只由玩家/测试触发
}
```

域通过 **一个** 只读属性声明它：

```csharp
public interface INeedDomain
{
    // …原有的 Id / DisplayName / Priority / IsSurvivalCritical / OnDayStart / RunCycle 不动…
    TriggerPolicy Trigger { get; }        // ★ 新增
    bool ShouldRunToday(DomainContext ctx);   // 保留：默认实现 = Trigger 的判定
}
```

**关键决定：`ShouldRunToday` 不删。**
它的**默认实现**改成读 `Trigger`；要写特殊逻辑的域仍然可以覆写它。
这样：
- 三个"按天取模"的域：删掉方法体，只声明 `Trigger = TriggerPolicy.EveryDays(3)`
- 食物域：声明 `TimeWindow(8,19)`
- 新的威胁域：声明 `EventDriven("ThreatAppeared")`
- **协调器的 `AnyDomainDue` 与 `RunAllDomains` 用同一份判定** → 去重 bug 结构上不可能再出现

**这是"加接口就行"的正例**：加一种节奏 = 加一个枚举值 + 在判定函数里加一个分支，
**所有域都不动**。

---

## 四、① 事件驱动：给框架加"事件源"

现在框架只会在"新的一天"被唤醒（`GameComponentTick` → 每 64 tick 问一次
"是不是新的一天"）。威胁响应域需要"**敌人一出现就动手**"，而**按天轮询做不到**。

### 4.1 设计

```csharp
/// 一个可以被域订阅的"殖民地事件"
public sealed class DomainEvent
{
    public string Name;          // "ThreatAppeared" / "PawnDowned" / "FireStarted" / …
    public IntVec3 Cell;         // 可选：事件发生的位置
    public string Detail;        // 人话
    public int Tick;
}

/// 事件源：每 tick 被框架问一次"有没有新事件"
public interface IDomainEventSource
{
    string Name { get; }                       // 用于遥测/诊断
    List<DomainEvent> Poll(DomainContext ctx);  // 必须廉价：**不许在这里遍历大地图**
}
```

**为什么是 `Poll` 而不是 C# `event`**：
- 游戏是**单线程主循环**，"谁注册了什么"用显式列表比 .NET event 更容易诊断
  （而且事件在**读档/重载程序集**后会留下悬空订阅 —— 这是本项目真实遇到过的类别）
- `Poll` 天然是"框架控制调用时机"，与 `Tick`/`RareTick` 的节奏解耦
- 事件源必须**廉价**：真正的"扫全图找敌人"应该走 `map.listerThings`
  （O(n)）而不是 `get_cells_info` 那种（O(62500)，已实测慢 2~3 个数量级）

### 4.2 调度顺序（③ 闸门的一部分）

```
每 64 tick：
  ① 轮询事件源 → 得到事件集合（廉价）
  ② 事件触发的域**插队**到本轮最前面（Priority 只作为同级别内的次序）
  ③ 威胁类事件出现时：**否决**低优先级的稳态域（"先躲，别种地"）
  ④ 其余按 Trigger 判定
```

★ 威胁响应应该在稳态域**之前**，并且能**暂停**它们 —— 现在没有这个能力。
威胁域自己的规则（"躲进最近的封闭空间并关门"）由那个域实现，
框架只负责"让它插队、并且让稳态域让路"。

---

## 五、③ 闸门层：全局托管 → 按域托管（+ 一个统一的仲裁点）

### 5.1 现在

`TakeoverController.ShouldAct(domainId, source)` —— 签名里**已经有 `domainId`**，
但实现只看一个全局 `Mode`。所以升级成"按域"**不需要改任何调用方的签名**，
只需要把内部换成一张表：

```csharp
/// 托管范围：全局 + 每域覆盖
/// 三态 Off / Auto / Manual（已有）
+ Dictionary<string, TakeoverMode> PerDomain;   // ★ 新增
+ HashSet<string> SuspendedUntil;               // ★ 新增：威胁期间暂停（带到期时刻）
```

```csharp
bool ShouldAct(string domainId, string source)
{
    if (source == "player") return true;                 // 玩家自己动手永远放行
    if (Suspended(domainId)) { RecordVeto(...); return false; }
    TakeoverMode m = PerDomain.TryGet(domainId, out var x) ? x : Mode;
    ...
}
```

**扩展点不变，语义变细**：这就是"加接口就行"的样子 ——
`ShouldAct(domainId, source)` 一个字都不用改。

### 5.2 资源冲突（将来）

域之间会抢同一样东西：**小人的工时**。现在完全没有仲裁，靠 `Priority` 排序
"先到先得"。加操作线之后这一定会出问题（食物要种植工、建造要建造工、
威胁响应要所有人）。

设计上**只加声明，不加算法**（继续吃"套公式优于局部最优"那条原则）：

```csharp
/// 这个域本轮需要哪类工时 —— 让"谁抢谁"变成数据，而不是让框架去算最优
public sealed class WorkClaim
{
    public string[] WorkTypes;     // "Grower" / "Construct" / "Haul" / …
    public int PawnCount;          // 需要几个人（0 = 不占人）
    public bool Exclusive;         // true = 独占（威胁响应就是独占）
}
```

框架只做两件事：**（a）冲突时报出"谁和谁抢"（b）按 Priority + IsSurvivalCritical 让路**。
不做全局最优分配 —— 那是 P2「工作优先级自动化」的题目，不是这里的。

---

## 六、② 操作清单：把 `BootstrapStep` 升成一等公民

`docs/开局流程-设计.md` 已经定了 `BootstrapStep { Id, Description, Order, IsDone, Run }`。
泛化点是：**它不该叫"开局"清单** —— 中期扩张（"再加一间卧室"、"铺地板"）用的是同一个形状。

```csharp
/// 一条**有序、幂等、可诊断**的操作。开局清单、中期扩张、手动指令都用它。
public interface IPilotOperation
{
    string Id { get; }              // 稳定标识（幂等键）
    string Description { get; }     // 人话（窗口里显示"即将做：…"）
    int Order { get; }              // 顺序（小的先）
    bool IsDone(ColonyContext ctx); // 已经做过了吗（**每步自己判断**）
    OperationResult Run(ColonyContext ctx);
}

/// 一份清单 = 一组有序操作 + 前置/结束条件
public sealed class OperationList
{
    public string Id;               // "bootstrap" / "expand-bedrooms"
    public string Description;
    public bool IsComplete(ColonyContext ctx);   // 整份清单完成了没
    public List<IPilotOperation> Operations;     // 框架按 Order 排序后依次跑
}
```

可靠性排序（用户定过的，写进代码而不是注释）：

```
操作清单（规定死）  >  样板库（抄已知设计）  >  域（持续反应）
   最可靠                 可靠                   灵活
→ 能用左边解决的，就不要动右边
```

**与域的关系**：清单负责"把地基打好"、域负责"之后持续维持"。
两者的**意图**都走同一套 `IntentBoard`（所以窗口里看到的是同一种东西）。

### 6.1 `DomainContext` 膨胀问题（顺手要处理的）

`DomainContext` 现在混了通用量（`Map` / `Day` / `Hour` / `Season` / `SiteOptions`）
和**食物专属**量（`CropDef` / `Calibration` / `Policy`）。加操作线之后它会继续膨胀。

设计：拆成
```csharp
ColonyContext        // 通用：Map / Day / Hour / Season / 殖民地规模 / SiteOptions
  + DomainContext    // 域专属：现在那些食物字段（将来各域可以有自己的 payload 类型）
```
⚠ 这是**行为不变的结构调整**，但会碰到所有域文件的签名 → 和 §八 的撞车风险绑在一起。

---

## 七、这些设计**不改**什么（防止过度重构）

| 保持不变 | 理由 |
| --- | --- |
| `INeedDomain` 的 7 个原有成员 | 四个域都在用、都有实测；删/改签名会一次性碰所有域 |
| `NeedCoordinator` 的"异常隔离 + 按天去重"语义 | 这两条是踩坑换来的（一天跑 900 次决策），不能顺手改 |
| `IntentBoard` / `IIntentSink` / `TakeoverController` 的**对外签名** | 刚落地，签名就是扩展点 |
| 域内的食物概念（`FoodState` / `Calibration` / `FoodDomain`） | 泛化 ≠ 删掉食物，是让食物只是**一条线** |
| 存档键字符串 | 改了会破存档（见 §2.2） |
| Core 的"不许引用 Verse/RimWorld" | 离线可测性的地基 |

---

## 八、执行计划（三阶段，按"撞车风险"排序）

> ⚠ **现在一步都不做**：另一个进程（快照线）正在改 `src/colonyrecorder/`，
> 而且刚动过 `ColonyRecorder.cs` / `SnapshotReaders.cs`。改名会扫到 `src/` 全域。
> 本文件先作为**待执行的清单**存在。

### 阶段 1：机械改名（一次集中做完，不存在安全中间态）
- **不做**任何逻辑改动，只改名字与路径（§2.1 整张表）
- 存档键**不动**
- 必须在**游戏停止**时做（DLL 被锁 + junction 要重建）
- 验收：`node scripts/build-core.mjs`（357 条）+ `build-mod.mjs` + `build-probe.mjs` 全绿；
  重启后 `colonyprobe/health` 能认出 mod
- ⚠ 需要你确认：**是否接受"改名后老存档从 mod 列表里消失"**（可以在同一次里开新档）

### 阶段 2：触发层 + 事件层（纯新增，不动现有域的行为）
- 新增 `TriggerPolicy` / `DomainEvent` / `IDomainEventSource`（新文件）
- `INeedDomain` **加一个** `Trigger` 属性，但给它默认实现 → 现有四个域**一行不改**也能编译
- `NeedCoordinator` 内部改成"用同一份 Trigger 判定"，`AnyDomainDue` 与实跑共用
- 逐个域把 `ShouldRunToday` 的手写取模换成 `Trigger`（**每个域独立提交，可单独回滚**）
- 先离线测 `TriggerPolicy` 判定（纯函数），再实机验"域按声明节奏跑"
- 验收断言：三个按天取模的域行为**与改造前逐日完全相同**（这是回归的关键）

### 阶段 3：闸门 + 清单
- `TakeoverController` 内部换成分域表 + `SuspendedUntil`（对外签名不变）
- `IPilotOperation` / `OperationList` 落地；把"允许物资 + 划两片储藏区"搬成前两步
- `ColonyContext` 拆分（行为不变的结构调整）
- **第一次**做威胁响应域（用事件驱动 + 独占声明 + 暂停稳态域）

---

## 九、验收清单（这三阶段做完要能回答的问题）

| # | 问题 | 怎么验 |
| --- | --- | --- |
| 1 | 加**第五个域**要改几个文件？ | 目标：**1 个**（新文件 + 注册一行）。现在是 4~6 个（域 + 主组件 + 探针 + 类型名数组） |
| 2 | 加**一种新节奏**要改几个文件？ | 目标：**1 个**（枚举值 + 判定分支） |
| 3 | 关掉**某一个域**的托管，它真的不动手，而别的域照跑？ | 探针切分域开关 + 断言该域当天零动作、其他域有动作 |
| 4 | 敌人出现时，稳态域**让路**了吗？ | 事件注入 + 断言"稳态域本轮被否决"且"威胁域跑在最前" |
| 5 | 开局清单的每一步都能**单独**说"做了/没做/为什么没做"？ | 清单报告 + `IntentBoard` 里每步一条意图 |
| 6 | 改名之后，**存档里的开关状态和账本还在**？ | 读档后看 `TakeoverController.Mode` 与账本记录（验证 §2.2 的判断） |
