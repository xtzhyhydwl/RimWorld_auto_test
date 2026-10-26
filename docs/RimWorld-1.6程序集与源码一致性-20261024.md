# RimWorld 1.6 实跑程序集与当前源码一致性核查（2026-10-24）

目的：核对玩家实跑的那份 **1.6 DLL** 与 HEAD 源码是否同构，并据此判断 E12（乞食围攻 I-014 / I-030 / I-045）「选敌对后请求信永久挂着」的根因分析是否仍适用于玩家的二进制。
结论按三类标注：**【实测】**= 真跑出来的输出；**【弱证据】**= 间接推断（字节/字符串/日志侧面）；**【无法判定】**= 本机手段不足。

---

## 结论（3 行）

1. **1.6 的 DLL 与 HEAD 源码不同构**：1.6 DLL 编译于 2026-10-16 22:15，其 `RHAH_ArmedDemand` 成员集 = HEAD 减去 2026-10-18 / 10-23 才加进来的 `NoteFood / Remaining / FoodRequest / Owner / Refresh / Finish / Tick / NotifyPlayerHarm`；`RHAH_ChoiceRecord` 也缺 HEAD 才有的 `ArmedFoodNutritionDelivered`。
2. **E12 根因结论在实跑二进制上成立，而且更严重**：IL 实测「选敌对（Reject=2）」只走到 `job.ArmedAssault = true`，整段攻击分支没有任何 `stfld Settled`，记录恒 `Open` → 信不摘除；并且 1.6 DLL 里**根本不存在** `Owner / Refresh / Finish` 这套「Owner 为 null 就 Finish(Ignore)」的兜底，所以玩家那份二进制连这个唯一自动出口都没有。
3. **本机能 build 但不能离线重建 1.6 DLL**：`dotnet` 10.0.401 与游戏程序集都在，但缺 .NET Framework 4.8 参考程序集、且 NuGet 缓存为空（`Lib.Harmony 2.4.1` 需 restore）；需联网一次或装 net48 targeting pack。

---

## 1. 1.6 / 1.7 程序集清单与时间戳【实测】

部署位置 `D:\SteamLibrary\steamapps\workshop\content\294100\3807233439\`

| 路径 | 文件 | 大小 | 时间戳 |
|-|-|-|-|
| `1.6\Assemblies\` | `HungerAndHavoc.dll` | 848,384 B | 2026-10-16 22:15:02 |
| `1.6\Assemblies\` | `HungerAndHavoc.Api.dll` | 14,848 B | 2026-10-16 22:15:03 |
| `1.7\Assemblies\` | `HungerAndHavoc.dll` | 868,352 B | 2026-10-24 08:40:00 |
| `1.7\Assemblies\` | `HungerAndHavoc.Api.dll` | 14,336 B | 2026-10-23 18:51:13 |
| `Guard\Assemblies\` | `HungerAndHavocGuard.dll` | 6,656 B | 2026-10-14 19:12:38 |
| `1.7\Guard\Assemblies\` | `HungerAndHavocGuard.dll` | 6,656 B | 2026-10-23 18:51:13 |

- 目录里除主 DLL 只有 `HungerAndHavoc.Api.dll`，没有打包 0Harmony 等第三方 DLL。
- SHA256 与仓库副本对比【实测】：
  - `1.6.SHA256(repo) == 1.6.SHA256(deployed)`（`B987BF67…` / Api `0167D1B1…`）→ 部署的 1.6 DLL 与仓库提交的那份是同一文件（该文件在 `8806f09` 2026-10-18 首次入库，内容/时间戳仍是 10-16 的构建）。
  - `1.7` 主 DLL **不相等**（repo `AB61FAAF…` vs deployed `15C09CCC…`）→ 部署的 1.7 DLL 在仓库提交之后又被本地重编过（08:40）；Api DLL 相等。
- 加载哪一份【实测】：`LoadFolders.xml` 的 `v1.6` 段加载 `Guard`、`/`、`1.6`、`Biotech`，但后三项都带 `IfModActive="nanaloveyuki.oxygendsl"`；根目录**没有** `Assemblies` 目录，所以 DLL 只在 Oxygendsl 激活时来自 `1.6\Assemblies`。`Player.log` 有 `[Oxygendsl] RHAH behavior adapter registered.`，`ModsConfig.xml` 含 `nanaloveyuki.oxygendsl` → 条件满足。
- 版本前提复核【实测】：`Player.log` 第 25 行 `RimWorld 1.6.4871 rev591`（玩家实跑 1.6）。

---

## 2. 反射结果：1.6 DLL 里到底有哪些相关成员【实测】

方法：Windows PowerShell 5.1（.NET Framework 4.8 宿主）+ `Assembly.LoadFrom`，先预加载 `D:\SteamLibrary\steamapps\common\RimWorld\RimWorldWin64_Data\Managed` 下 106 个 DLL，再加载模组 DLL。`GetTypes()` 成功（557 类型，22 个 LoaderException，均为缺 IrisMenus 等模组侧依赖，不影响本节类型枚举）。

程序集标识：`HungerAndHavoc, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null`，`ImageRuntimeVersion = v4.0.30319`。
引用：`mscorlib, Assembly-CSharp, 0Harmony, HungerAndHavoc.Api, Oxygendsl.Core, UnityEngine.CoreModule, UnityEngine.TextRenderingModule, UnityEngine.IMGUIModule, System.Core, System.Xml, IrisMenus, System, System.Xml.Linq, Oxygendsl.Mod`。

### 2.1 六个相关类型

| 类型 | 是否存在 | 关键成员 |
|-|-|-|
| `HungerAndHavoc.Pawn.RHAH_ArmedDemand` | 存在 | `CanPay` / `Find(int)` / `get_Enabled` / `Holds(Pawn)` / `Initialize(record,List)` / `IsRequest` / `IsSiege(string)` / `PlanPayment` / `Refused(Pawn)` / **`Resolve(record, action)`** / `StartsAssault(bool,bool,action)` / `TryPay` |
| `HungerAndHavoc.Incidents.RHAH_ChoiceRuntime` | 存在 | **`Tick(GameComponent_RHAH_Game,int)`**、`TrySettle`、`TrySettleRequest`、`Open`、`Apply`、`Release`、`NoteEnding`、`Present`、`Records`… |
| `HungerAndHavoc.Core.GameComponent_RHAH_Game` | 存在（基类 `Verse.GameComponent`） | `GameComponentTick()` / `GameComponentUpdate()` / `TickStays` / `TickPlague` / `OpenChoices` … |
| `HungerAndHavoc.Incidents.ChoiceLetter_RHAH_Request` | 存在（基类 `Verse.ChoiceLetter`） | `get_Choices()` / **`Settle(action, Pawn, bool)`** / **`Wrapped(DiaOption, action, Pawn, bool)`** / `FindRecord` / `get_CanDismissWithRightClick()` / 字段 `choiceId, amount, expireTick, kind, mapId, site` |
| `HungerAndHavoc.Pawn.RHAH_BatchAttitude` | 存在 | `AcceptFood` / `Attack` / `SetHostile` / `StartArmedAssault` / `OrderLeave` / `OrderVisitorLeave` / `TryShift` … |
| `HungerAndHavoc.Pawn.LordJob_RHAH_Visitor` | 存在（基类 `Verse.AI.Group.LordJob`） | `ArmedAssault get/set`、`ArmedChoiceId get/set`、`CreateGraph` / `LordJobTick` / `BeginFoodWait` … |

### 2.2 `Tick` / `Refresh` / `Owner` / `Finish` 是否存在

- **`RHAH_ArmedDemand`：一个都没有。** 无 `Tick`、无 `Refresh`、无 `Owner`、无 `Finish`（因此"签名参数个数"不适用：方法不存在）。
- **`GameComponent_RHAH_Game` / `ChoiceLetter_RHAH_Request`：也没有** `Tick`/`Refresh`/`Owner`/`Finish`（后者有 `get_CanDismissWithRightClick` 恒 `false`）。
- **`RHAH_ChoiceRuntime` 只有 `Tick(GameComponent_RHAH_Game, int)`**（1 个参数 + 接收者），没有 `Refresh`/`Owner`/`Finish`。
- 全 557 类型里名字为 `Tick`/`Refresh`/`Finish` 的方法都属于别的子系统（`Suiyin.*`、`RHAH_GrainHole`、`WorldObject_RHAH_Interception` 等）；**全程序集没有任何方法叫 `Owner`**。
- `RHAH_ChoiceRecord.Settled` 是**公有字段**（类型 `RHAH_ChoiceAction`），不是属性 → 不存在 `set_Settled` 方法；`get_Open()` = `Settled == None(0)`。若按 `set_Settled` 去扫会得到假阴性【实测，重要方法论修正】。

### 2.3 对照：1.7 的 DLL 里没有这些类型【实测】

`1.7\Assemblies\HungerAndHavoc.dll` 只有 **75 个类型**，且 `RHAH_ArmedDemand / RHAH_ChoiceRuntime / RHAH_ChoiceRecord / ChoiceLetter_RHAH_Request / RHAH_BatchAttitude / LordJob_RHAH_Visitor` **一个都不存在**（只有 `IncidentWorker_BeggarSiege`、`GameComponent_RHAH_Game`、`MapComponent_RHAH_Map` 等）。
⇒ 之前那份「对 1.7 DLL 反射确认 `Tick→Refresh→{Owner,Finish}`」的描述与实测不符：1.7 那份 DLL 里没有承载这套成员的 E12 类型；1.6 那份也没有 `Owner/Refresh/Finish`。这个"已排除旧 DLL"的论据本身不能成立。

---

## 3. IL 层：选敌对时是否写 `Settled`【实测】

方法：自写 IL 走查（`MethodBody.GetILAsByteArray` + `System.Reflection.Emit.OpCodes` 表 + `Module.ResolveMember` 解析 token），先做全程序集扫描，再对关键方法全量反汇编。
覆盖度自检：【实测】`METHODS=3007  INSNS=118847  UNDECODED_BREAKS=0`（`0xFE` 前缀指令已正确处理；首版解码器会在 `ceq` 处中断，已被修正，否则会漏报 `TrySettleRequest`）。

### 3.1 全程序集 `RHAH_ChoiceRecord.Settled` 访问点【实测】

写入（`stfld`）只出现在 4 个方法：

| 方法 | 位置 |
|-|-|
| `RHAH_ArmedDemand::Resolve` | `IL_0046`、`IL_0071` |
| `RHAH_ChoiceRuntime::TrySettle` | `IL_00D2`、`IL_017D` |
| `RHAH_ChoiceRuntime::TrySettleRequest` | `IL_024F` |
| `RHAH_ChoiceRecord::ExposeData` | 反序列化装载路径 |

读取（`ldfld`）：`get_Open`、`Apply`、`NoteEnding`、`ChildExchange.FoodAlternative`、`RHAH_EntrustCare`、`RHAH_EventFollowMood`。

### 3.2 `TrySettleRequest` 对武装乞食请求提前返回【实测】

`RHAH_ChoiceRuntime::TrySettleRequest`（247 条指令）关键路径：

- `IL_007B`：`if (RHAH_ArmedDemand.IsRequest(record))` → 进入请求专用分支；
- `IL_0088`：`if (!Enabled) { Resolve(record, Ignore); return Ignore; }`
- `IL_009D`：`if (action == Deliver(1) && !TryPay(record)) return 0;`
- `IL_00B0`：`Resolve(record, action);` **`IL_00BC ldarg.2 / IL_00BD ret`** ← 直接返回；
- 于是**永远到不了** `IL_024F stfld Settled`（那是非请求记录走 `IL_00BE…IL_02A3` 通用分支时的写入）。

⇒ 对 `I-014/I-030/I-045` 且 `Kind == SimpleMeal` 的记录，`Settled` **只能由 `RHAH_ArmedDemand.Resolve` 写**。

### 3.3 `Resolve` 在攻击分支不写 `Settled`【实测，核心】

`RHAH_ArmedDemand::Resolve(record, action)` 全量反汇编要点：

```
IL_0000  if (record == null || !record.Open) return;
IL_000C  present = RHAH_ChoiceRuntime.Present(record);
IL_003B  lord = present[0].GetLord()?.LordJob as LordJob_RHAH_Visitor;
IL_003C  if (!RHAH_ArmedDemand.get_Enabled()) Settled = Ignore(3);     // IL_0043 ldc.i4.3 / IL_0045 stfld
IL_004A  if (lord == null) return;                                     // IL_005B ret —— 不写 Settled
IL_004E  if (lord.ArmedChoiceId != record.Id) return;
IL_005C  if (Enabled && action != Deliver) goto IL_00BA;               // → 攻击分支
IL_0067  Settled = (action == Deliver ? action : Ignore(3));           // IL_006A..IL_0070 stfld
IL_0075  AcceptFood(present[0]); ArmedAssault = false; ArmedChoiceId = 0; OrderVisitorLeave(...);
IL_0099  if (action == Deliver) Messages.Message("RHAH_ArmedDemand_Paid", PositiveEvent);
IL_00B9  ret;
IL_00BA  if (!StartsAssault(Enabled, lord.ArmedAssault, action)) return;   // IL_00CD ret
IL_00CE  lord.ArmedAssault = true;              // IL_00D0 set_ArmedAssault
IL_00D5  RHAH_BatchAttitude.StartArmedAssault(present);
IL_00E2  lord.ReceiveMemo("RHAH_ArmedAssault");
IL_00F7  Messages.Message("RHAH_ArmedDemand_Attack", ThreatBig);
IL_0128  ret;      // ← 整段没有任何 stfld Settled，也没有对 Settled 的赋值
```

`RHAH_ArmedDemand::StartsAssault(enabled, alreadyAssaulting, action)`【实测】：
`return enabled && !alreadyAssaulting && (action == Reject(2) || action == Timeout(7));`

`RHAH_ChoiceAction` 枚举值【实测】：`None=0, Deliver=1, Reject=2, Ignore=3, Join=4, Hire=5, Feed=6, Timeout=7, Recruit=8, Enslave=9, Capture=10, Attack=11, Ally=13`。

### 3.4 玩家点「敌对」= `Reject(2)`【实测】

`ChoiceLetter_RHAH_Request+<get_Choices>d__9` 的显示类 lambda：

```
<>c__DisplayClass9_0::<get_Choices>b__0 → Wrapped(rejectOption, 2 /*Reject*/, null, false)
<>c__DisplayClass9_0::<get_Choices>b__1 → Wrapped(ignoreOption, 3 /*Ignore*/, null, false)
<>c__DisplayClass9_1::<get_Choices>b__2 → Wrapped(foodOption,   1 /*Deliver*/, null, true)
<>c__DisplayClass9_3::<get_Choices>b__4 → Wrapped(deliverOption,1 /*Deliver*/, null, false)
```

`ChoiceLetter_RHAH_Request::Settle(action, pawn, flag)`【实测】：
`FindRecord` → 若本信已不在 `LetterStack` 直接返回 → `allowed = Settings?.AllowsRequest(record.Choice) ?? true` → `TrySettleRequest(...)` → `settled==0` 提示 `RHAH_Choice_Stale/RHAH_Transaction_Unavailable` 并返回 → `settled==1` 提示完成 → **`IL_00FF–IL_010D`：只有 `!record.Open` 才 `Find.LetterStack.RemoveLetter(this)`**。

`Wrapped`（30 条指令）【实测】：调 `Settle` 后，若信已不在栈中则 `option.dialog.Close()`，否则直接返回（对话与信都留着）。
`get_CanDismissWithRightClick()` = 常量 `false` → 右键关不掉。

### 3.5 于是三条出口都不通（1.6 二进制）【实测 + 推演】

| 玩家动作 | IL 走向 | `Settled` | 信 |
|-|-|-|-|
| 交付食物 `Deliver(1)` | `TryPay` 成功 → `Resolve(record,1)` → `IL_0067` 写 `Settled=1` | 写入 | 摘除（`!Open`） |
| **敌对 `Reject(2)`** | `Resolve(record,2)` → `IL_00BA` 攻击分支 → `ArmedAssault=true` | **不写** | **永久挂着** |
| 忽略 `Ignore(3)` | `Resolve(record,3)` → `StartsAssault` 为 false → `IL_00CD ret` | **不写** | **永久挂着**（比"只有敌对"更广） |
| 超时 `Timeout(7)` | `ChoiceRuntime::Tick` `IL_0068` → `Resolve(record,7)` → 若已 `ArmedAssault=true`，`StartsAssault` 因 `alreadyAssaulting` 返回 false → `ret`；若未敌对则走攻击分支 | **不写** | **超时不是出口** |
| Lord 消失（来客全灭/离图） | `IL_004A` `lord == null` → `IL_005B ret` | **不写** | 仍然挂着（**没有** `Owner==null → Finish` 兜底） |

代码名对照（HEAD 源码）：`Source/Pawn/RHAH_ArmedDemand.cs:164-168` 正是「`StartsAssault` 后只置 `ArmedAssault`、`StartArmedAssault`、`ReceiveMemo`、`Message`」这一段，与二进制 `IL_00BA–IL_0128` 逐句对应。

---

## 4. 源码自 10-16 以来的相关提交【实测】

仓库根 `E:\2026.7以后的新工程\DSH\RimWorld_auto_test\RimworldMods`（模块子目录 `ratkin_hunger_and_havoc`），HEAD `6ab44c7`（2026-10-23 23:08 `deploy: v1.1.4`），工作区干净。

- **1.6 DLL 对应的源码状态 = `16057e6`（2026-10-16 16:20 `deploy: v1.1.2`，构建前最后一个提交）**。证据：`git show 16057e6:Ratkin_Hunger_and_Havoc/Source/Pawn/RHAH_ArmedDemand.cs`（当时路径大写、160 行）的 `Resolve` 与二进制 IL 逐句一致（`if (!Enabled) Settled=Ignore` / `job==null||ArmedChoiceId!=Id` 直接 return / `!Enabled||Deliver` 才写 `Settled` / 攻击分支无写入），且该版本没有 `Owner/Refresh/Finish/Tick/NoteFood/Remaining/FoodRequest/NotifyPlayerHarm`，`RHAH_ChoiceRecord` 也没有 `ArmedFoodNutritionDelivered`——与反射结果完全吻合。
- 对该三个文件的后续提交（`git log --follow`）：

| 提交 | 日期 | 对 E12 的影响 |
|-|-|-|
| `8806f09` | 10-18 13:36 | `chore: organize monorepo layout`，仅目录改名 `Ratkin_Hunger_and_Havoc/` → `ratkin_hunger_and_havoc/` |
| `fd412f9` | 10-18 13:56 | `fix(rhah): close request dialogs…`｜`RHAH_ArmedDemand.cs` 在 `job == null || ArmedChoiceId != Id` 分支**补写** `record.Settled = Ignore`（其余逻辑未变）；`ChoiceLetter_RHAH_Request.cs` 28 行改动 |
| `b061823` | 10-23 11:40 | `fix: … demand lifecycle bugs`｜**新增** `Owner` / `Refresh` / `Finish` / `Tick` / `NoteFood` / `Remaining` / `FoodRequest` / `NotifyPlayerHarm`；`Resolve` 全部改为经 `Finish` 写 `Settled`；`ChoiceRuntime.Tick` 接入 `RHAH_ArmedDemand.Tick`（`Source/Incidents/RHAH_ChoiceRuntime.cs:122-124`、`:202-205`） |
| `6ab44c7` | 10-23 23:08 | `deploy: v1.1.4`｜**新增** `RHAH_ChoiceRecord.ArmedFoodNutritionDelivered`（HEAD 在 `Source/Pawn/RHAH_ArmedDemand.cs:137` 使用） |

- 体量差：`git diff -M --stat 16057e6 HEAD -- '*RHAH_ArmedDemand.cs' '*RHAH_ChoiceRuntime.cs' '*ChoiceLetter_RHAH_Request.cs'` → `RHAH_ArmedDemand.cs` 160 删 / 253 增（等价于整文件重写），`ChoiceLetter_RHAH_Request.cs` 31 行，`RHAH_ChoiceRuntime.cs` 109 行。
- HEAD 自身是否还有该 bug【实测】：`Source/Pawn/RHAH_ArmedDemand.cs` 里 `Settled` 只有一处写入（`L96`，在 `Finish` 内），攻击分支 `L164-168` 仍然不调 `Finish` → 根因在 HEAD 依旧存在；差别是 HEAD 多了 `Tick → Refresh → Finish(Ignore)`（OWNER 为 null 时每小时一次兜底），二进制里没有。
- 因此：**用 HEAD 源码推根因，机制上说得通且与二进制一致；但不能把 HEAD 的兜底路径当成"玩家实跑时的行为"**。

---

## 5. 重建流程与 `dotnet` 可用性【实测】

### 5.1 有没有重建 1.6 程序集的现成流程：有，两条入口

1. 仓库级：`RimworldMods\scripts\deploy.sh` → `scripts\python\deploy.py`，模块配置 `RimworldMods\scripts\deploy\ratkin_hunger_and_havoc.json`。其 `build` 段就是：
   `{python} {source}/scripts/rhah_pipeline.py {pipeline_command} --rimworld-dir {game} --configuration {configuration} --rimworld-version {version} --oxygen-dsl-dir {root}/oxygen_dsl_framework`；
   `owned_assemblies` 覆盖 `1.6/Assemblies`、`Guard/Assemblies`、`1.7/Assemblies`、`1.7/Guard/Assemblies`。
2. 模块内：`make build`（`Makefile`）→ `python scripts/rhah.py build` → `scripts/rhah_pipeline.py`。`build()`（`scripts/rhah_pipeline.py:425`）顺序：`Oxygendsl.Mod.csproj` → `Source/HungerAndHavoc.csproj` → `Guard/Source/HungerAndHavocGuard.csproj`，统一传 `-p:RimWorldVersion / RimWorldDir / RimWorldManagedDir / OxygenDslDir / HarmonyPath`。
   - 输出路径写死在 `Source/HungerAndHavoc.csproj:14`：`<OutputPath>../$(RimWorldVersion)/Assemblies/</OutputPath>`，即 **build 会直接覆盖仓库里的 `1.6/Assemblies/`**（不是 `artifacts/`）。
   - `global.json` 要求 SDK `10.0.401`（`rollForward: latestPatch`）。

### 5.2 是否需要 RimWorld 程序集才能编译：需要

- `1.6` → `net48`，引用 `Assembly-CSharp.dll` + `UnityEngine(.CoreModule/.IMGUIModule/.TextRenderingModule).dll`（`Source/HungerAndHavoc.csproj` 的 1.6 条件组）。
- `1.7` → `net10.0`，引用 `UnityStubs.dll`（游戏根目录）。
- `Directory.Build.props` 有前置检查：`RimWorldVersion` 必须是 1.6/1.7；缺 `Assembly-CSharp.dll` 或 1.7 缺 `UnityStubs.dll` 直接 `Error`。
- 还需要先构建 `oxygen_dsl_framework`（`Oxygendsl.Core/Mod.dll`；`tmp/` 下**当前没有**预编译产物），以及 Harmony：默认取 workshop `294100\2009463077\<version>\Assemblies\0Harmony.dll`（`scripts/rhah_pipeline.py:387`）——`1.6\Assemblies\0Harmony.dll` 实测存在。
- `IrisMenus.dll` 存在时自动加 `RHAH_IRISMENUS` 与两个 `Pawn\Compat\RHAH_IrisMenus*.cs`（`$(RimWorldDir)/Mods/IrisMenus/<ver>/Assemblies/IrisMenus.dll`），不存在则跳过——即编译产物会随环境不同，复现"完全等价的二进制"需要同样的 IrisMenus 状态。

### 5.3 `dotnet` 可用性【实测】

- `dotnet --info`：`.NET SDK 10.0.401`，MSBuild 18.9.11；`dotnet --version` → `10.0.401`。
- `global.json` 的 `10.0.401` 命中。

### 5.4 离线重建的缺口【实测，静态检查，未编译】

| 需求 | 现状 |
|-|-|
| .NET Framework 4.8 参考程序集（net48 编译必需） | **缺**：`C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework` 不存在（只有 VS 2022 目录，无该 targeting pack） |
| `Lib.Harmony 2.4.1`（`Source/HungerAndHavoc.csproj` 的 `PackageReference`） | **缺缓存**：`%USERPROFILE%\.nuget\packages` 不存在；仓库内无 `NuGet.config`/本地源/`.nupkg`；用户级源只有 `nuget.org` |
| 游戏程序集 | 有（`D:\SteamLibrary\...\RimWorldWin64_Data\Managed`） |
| SDK | 有（10.0.401） |

⇒ **联网 restore 一次**（拿 `Lib.Harmony` + `Microsoft.NETFramework.ReferenceAssemblies.net48`，或装 net48 targeting pack）后即可重建 1.6 DLL；1.7（net10.0）不需要 net48 包，只需 `Lib.Harmony`。

---

## 6. 对 E12 根因结论的影响

| 原结论 | 复核结果 |
|-|-|
| 「选敌对只置 `ArmedAssault = true`，从不写 `Settled`」 | **成立**（1.6 二进制 IL 实测：`IL_00BA–IL_0128` 无 `stfld Settled`；HEAD 源码同样位置也不写） |
| 「记录永远 `Open`，信永远 `Pending`（禁右键、无 Close、超时被跳过）」 | **成立**：`get_Open = Settled==None`、`CanDismissWithRightClick=false`、超时走 `StartsAssault(alreadAssaulting=true)` 返回 false；且不只敌对，**忽略**也卡 |
| 「唯一自动出口是 `Owner(record)==null → Refresh → Finish(Ignore)`」 | **对 HEAD 成立，对玩家实跑的 1.6 二进制不成立**：二进制没有 `Owner/Refresh/Finish`，`lord==null` 时 `Resolve` 直接 `ret`，不写 `Settled` → 比 HEAD 描述更死 |
| 「已对 1.7 DLL 反射排除『装的是旧 DLL』」 | **该论据无效**：1.7 的 DLL 里没有这些类型（75 类型，无 E12 选择系统），1.6 的 DLL 里也没有 `Owner/Refresh/Finish`。结论方向（玩家装的是最新 1.6 构建）由时间戳/SHA 另证：「1.6 DLL 是最近一次 1.6 构建」 |
| 修复落点 | 只在 HEAD 改**不会**影响玩家：玩家加载 `1.6\Assemblies\HungerAndHavoc.dll`（10-16 构建）。要在 1.6 线生效必须改源码后重编 1.6 DLL 并按 `scripts/deploy/*.json` 部署 |

补充（供修复参考，非本任务要求）：1.6 线最小修法是在 `Resolve` 的攻击分支补 `Settled`（或 `Finish`）写入，并处理 `lord == null` 分支；同时建议核对 `Ignore(3)` 与 `Timeout(7)` 两条路径，它们同样不写 `Settled`。

---

## 7. 未验证 / 无法判定

1. **未运行游戏**（按要求禁止启动）：没有在运行时观察 `RHAH_ChoiceRecord.Settled`、`LetterStack` 的真实状态；上述均为静态 IL 结论。
2. **未反编译到 C#**：用自写 opcode 走查 + token 解析，未做栈类型推导；泛型实例化只显示到 `List\`1` 这一层；`IL_00..` 偏移以方法体为基准。
3. **二进制与 `16057e6` 不是逐字节证明**：只证明"成员集一致 + `Resolve`/`StartsAssault`/`TrySettleRequest`/`Settle`/`Wrapped` 逻辑逐句一致 + 无 HEAD 独有成员"，不排除 10-16 16:20 之后未提交改动。
4. **22 个 LoaderException 未逐条解析**（缺 IrisMenus 等依赖导致），不影响本报告涉及的类型；未反射 1.7 的 Guard 程序集内容。
5. **未读存档内部值**：没有验证玩家存档里 `I-014/I-030/I-045` 记录的 `Settled` 实际值（可以读存档 XML 的 `Settled` 字段进一步确证，本次未做）。
6. **未实测 rebuild**：5.4 的 net48/NuGet 缺口是静态检查结论（`Test-Path` / 目录枚举），没有跑 `dotnet restore/build`（按要求不编译）。
7. 本机**没有** `monodis` / `ikdasm` / `ildasm` / `ilspycmd` / Mono.Cecil；全部 IL 结论来自 `System.Reflection` + 自写解码，未安装任何工具、未联网。

---

### 复现命令（单行 PowerShell，未落地任何脚本）

```powershell
# 反射（.NET Framework 宿主，可加载 net48 模组 DLL）
$s=@'
$m='D:\SteamLibrary\steamapps\common\RimWorld\RimWorldWin64_Data\Managed'
foreach($f in Get-ChildItem $m -Filter *.dll){ try{[void][System.Reflection.Assembly]::LoadFrom($f.FullName)}catch{} }
$a=[System.Reflection.Assembly]::LoadFrom('D:\SteamLibrary\steamapps\workshop\content\294100\3807233439\1.6\Assemblies\HungerAndHavoc.dll')
try{$t=$a.GetTypes()}catch [System.Reflection.ReflectionTypeLoadException]{ $t=$_.Exception.Types|Where-Object{$_} }
$t|Where-Object{$_.Name -eq 'RHAH_ArmedDemand'}|ForEach-Object{$_.GetMethods('Public,NonPublic,Instance,Static,DeclaredOnly')}|Select-Object Name
'@
$b=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($s)); powershell.exe -NoProfile -NonInteractive -EncodedCommand $b
```

IL 反汇编与全库 `Settled` 扫描用的是同一套内联解码器（`OpCodes` 表 + `Module.ResolveMember` + `MethodBody.GetILAsByteArray`），按硬性要求只在 `pwsh` 调用内联执行，未生成 `.ps1`/`.py`。
