# AI 托管开关 + 意图窗口 + 进程间通信（框架设计）

> 用户 2026-10-03 要的三件事：
> ① 局内一个**是否让 AI 托管**的小按钮（灵活开关）
> ② 局内一个窗口，**把 AI 打算做什么打印出来**（尤其"即将要做什么"）
> ③ 一个**进程间通信**通道把内容发过去 —— **落点以后再看，但框架现在就要有**
>
> 本文记的是**结构**（谁负责什么、扩展点在哪），不是实现细节。
> 状态：**已编译通过，未部署**（另一个进程正在用游戏做快照捕捉）。

---

## 一、一句话结构

```
        域（FoodDomain / TemplateBuildDomain / …）
             │  只做一件事：在结果里填一条 Intent
             ▼
        DomainCycleResult.Intent          ← 域**不认识**任何 UI / 文件 / IPC
             │
             ▼
        NeedCoordinator.PublishIntent     ← 框架统一：登记 + 推进状态 + 广播
             │
             ▼
   ┌─────── IntentBoard（单一事实来源） ───────┐
   │  环形缓冲 + 生命周期状态                   │
   └───┬───────────────┬───────────────────┬──┘
       │               │                   │
    IntentWindow    IIntentSink ×N      探针/脚本
   （游戏内窗口）   （日志/JSONL/HTTP…）  （foodprobe/intents）
```

**判据**：加一条"AI 要做的事"只需要在域里填一个字段；加一个"出口"只需要实现一个接口。
两边**都不需要认识对方**。

---

## 二、为什么是"有状态的看板"而不是"日志"

用户要的是「**即将**要做什么」。日志只能事后知道。所以意图必须有生命周期：

```
Planned ──→ Running ──→ Done
   │            │
   │            └──→ Failed
   └──→ Vetoed   （被托管开关/守卫拦下 —— ★ 必须带原因）
```

`Pending()` = `Planned + Running` = **窗口最上面那一栏**。

---

## 三、AI 托管开关：为什么是三态 + 闸门放哪

### 3.1 三态（不是一个 bool）

| 模式 | 自动闭环 | 手动测试钩子 |
| --- | --- | --- |
| `Off` | ❌ | ❌ |
| `Auto`（默认） | ✅ | ❌ |
| `Manual` | ❌ | ✅ |

一个 bool 分不清「**AI 被关掉了**」和「**AI 开着但现在不该动手**」——
这两件事在窗口里必须看得出区别，否则玩家会以为 mod 坏了。

### 3.2 ★ 闸门放在 `GameComponentTick` 的**最前面**

```
GameComponentTick
   └─ ⓪ TakeoverController.ShouldActAndRecord(...)   ← 一个开关管住所有域
        └─ ① NeedCoordinator.AnyDomainDue
             └─ ② RunAllDomains（域在这里跑）
```

**为什么不放在各个域里**：那样"新加一个域忘了问开关"就是一个**静默漏洞**——
托管关了它还在动，而且没人会发现。放在唯一入口上，加域**不可能**绕过去。

这也顺带回答了用户那句「加/撤一个功能只需要加接口」：
**闸门是流程级的，不是功能级的**。

### 3.3 被拦下要留痕

每次拦下都记一条 `Vetoed` 意图，`Reason` 里写清**是哪一关拦的**。
否则窗口上只剩"没有待办"，玩家分不清是"AI 觉得没事做"还是"我把 AI 关了"。

---

## 四、游戏内 UI：★ 为什么逻辑全在 `Activate()` 里（IL 确证，不是猜的）

原版 1.6 实测（`tools/asmdump` 的 `AsmDump` + `IlDump`）：

| 成员 | 结论 |
| --- | --- |
| `MainButtonWorker.get_Visible()` | **非 virtual**，只读 `def.buttonVisible` → **可见性只能用 XML 控** |
| `MainButtonWorker.DoButton(Rect)` | **非 virtual**（主按钮栏用基类引用调它）→ 覆写它**不会被调到** |
| `MainButtonWorker.Activate()` | **abstract** → 子类覆写一定被调到 |
| `InterfaceTryActivate()` | 末尾 `callvirt MainButtonWorker::Activate` |

**结论**：只有 `override Activate()` 是保证被调到的入口。
（如果按直觉去覆写 `DoButton`，按钮会**看起来正常但点了没反应** —— 这类坑不实测是查不出来的。）

交互（一个物理按钮三个动作）：

| 操作 | 结果 |
| --- | --- |
| 左键 | 切换 AI 托管（开 ↔ 关） |
| 右键 | 开/关「AI 在做什么」窗口 |
| 中键 | 三态循环（测试用） |

⚠ 按钮**文字**来自 `Defs/MainButtonDefs.xml` 的 `label`（"AI托管"）。
想让它显示"开/关"就必须覆写 `DoButton`，而那需要原版字段是 virtual（现在不是）。
替代方案已实现：每次切换都往意图流写一条 `Notify`，窗口与 `Player.log` 都能看到。

### 窗口里有什么

1. 托管状态一行（模式 / 上次谁切的 / 被拦了几次）
2. **即将要做**（`Planned` / `Running`）
3. 最近发生（**含失败与被否决，以及原因**）
4. 出口状态（写到哪个文件、写了几条、有没有出错）
5. 三个按钮：托管开关 / 写游戏日志开关 / 清空记录

### ⚠ 窗口踩过的两个坑

- `Window` 的 `doWindowBackground` / `draggable` / `forcePause` 等是**字段不是属性**
  → 必须在构造里赋值。`forcePause` 必须 `false`（否则 `play_for` 会静默跑 0 tick，
  这是本项目花过代价的坑）。
- `IsShown` **不能**叫 `IsOpen`：`Verse.Window` 上已有同名成员 → CS0108，还容易调错实现。

---

## 五、进程间通信：出口是可插拔的

| 出口 | 类 | 状态 |
| --- | --- | --- |
| 游戏日志 | `IntentIpc.LogIntentSink` | ✅（默认**关**：决策多会刷屏） |
| **JSONL 文件** | `IntentIpc.JsonlIntentSink` | ✅ 默认开 |
| HTTP 推送 | `IntentIpc.HttpIntentSink` | ⬜ 预留空壳（**落点以后再看**） |
| 其他 | 实现 `IIntentSink` + `IntentBoard.AddSink` | ⬜ 扩展点 |

### 为什么先做文件

本项目已经有一条**验证过的**外部观测通道：mod 写文件、外部脚本读文件
（隔离实例的 `-savedatafolder` → 我们**有完全的文件访问权**）。
它不需要网络、不需要 DLL、不依赖桥接，而且**天然可回放**。

落点：`<隔离存档目录>/foodcontrol-intents.jsonl`，与 `foodcontrol-telemetry.jsonl` 同级。

### 为什么是 jsonl（一行一条）而不是一个 JSON 数组

1. **追加写** O(1)，不用重写整个文件，写到一半崩也不怕
2. 外部可以 `tail` 实时跟（`Get-Content -Wait`）
3. 一行坏了不影响别的行

⚠ 与遥测一样：UTF-8 **无 BOM**（BOM 会让第一行解析失败）。
⚠ 首行是 `#` 注释（写了路径与用途）—— **解析端必须跳过 `#` 开头的行**。

### 将来要接 HTTP 怎么做（不改任何产生意图的代码）

1. 把 `HttpIntentSink.OnIntent` 写成真正的 POST
   ⚠ **绝不能在游戏主线程做网络调用** —— 桥接调用排在主线程上，会挂死整个游戏；
   要用后台线程 + 队列
2. `IntentIpc.InstallDefaults()` 里 `AddSink(new HttpIntentSink(...))`
3. 完 —— 域、窗口、看板一行都不用动

---

## 六、★ 扩展点清单（"加东西"分别要动哪里）

| 想加的东西 | 动几个地方 | 具体 |
| --- | --- | --- |
| 一条新的"AI 要做的事" | **1** | 域里填 `DomainCycleResult.Intent` |
| 一类新的动作大类 | **1** | `IntentKind` 加一个枚举值（UI/IPC 自动跟上） |
| 一个新的出口（HTTP/管道/DB） | **1** | 实现 `IIntentSink` + `AddSink` |
| 一个新的域 | **1** | 实现 `INeedDomain` + `RegisterBuiltIn` |
| 分域托管（食物托管、建造不托管） | **1** | `TakeoverController` 里加一个集合 —— 调用方问的永远是 `ShouldAct(domainId, source)` |
| 新的 UI 展示方式 | **0** | 读 `IntentBoard.LinesForUi()` 即可 |

---

## 七、@外部怎么用（探针已就绪，但**需重启游戏后才生效**）

```bash
# 读/切托管开关
node src/cli.js call foodprobe/takeover                      # status（只读）
node src/cli.js call foodprobe/takeover --args-file a.json    # {"mode":"off"}
node src/cli.js call foodprobe/takeover --args-file a.json    # {"mode":"auto"}

# 读意图流
node src/cli.js call foodprobe/intents                        # pending（= 即将要做）
node src/cli.js call foodprobe/intents --args-file b.json      # {"which":"recent","limit":30}
node src/cli.js call foodprobe/intents --args-file c.json      # {"which":"selftest"} ← 10 条断言

# 或者完全不经过桥接：直接读文件
Get-Content profiles/vanilla/foodcontrol-intents.jsonl -Wait
```

★ 这三条通道（探针 / 文件 / 窗口）读的是**同一份** `IntentBoard` —— 不会出现
"窗口显示的和文件里写的不一样"这种对不上账的情况（DRY）。

---

## 八、本轮**没有**做的（有意留白）

| 没做 | 为什么 |
| --- | --- |
| 把 mod 的域真正填上 `Intent` | 属于"接线"，要重启游戏才能验；而且各域现在还在被另一条线改 |
| 实机验证按钮点得动、窗口画得出来 | **必须重启**（DLL + Defs 都是重启才加载） |
| HTTP 出口的真实实现 | 用户明确说落点「之后再看」 |
| 按钮显示"开/关"字样 | 需要原版 `DoButton` 是 virtual（现在不是）；替代方案已实现 |
