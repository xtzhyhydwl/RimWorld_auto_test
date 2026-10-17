# 共享单车 mod · 开发流程与设计（「一次做对」版）

> ## ⚠ 本文档已被 [共享单车mod-设计-v2.md](共享单车mod-设计-v2.md) 取代 / 补充
>
> **先读 v2**：[共享单车mod-设计-v2.md](共享单车mod-设计-v2.md) 是本项目的**最终设计文档（施工图）**。
> 本文**保留原文不删**，其中以下部分**仍然有效、v2 不重复**；另有若干节已**迁出**本文件（去向见下表）：
>
> | 本文章节 | 状态 |
> | --- | --- |
> | §1 环境（干净测试实例、桥接操作、工具坑、编译现状） | ✅ **仍然有效**（v2 §8.2 只补了双 TFM 的对照表） |
> | §2 六步流程与每步验收门 | ✅ **仍然有效**（v2 是"设计内容"，本文是"流程与验收门"） |
> | §3 两条并行线 + 贴图硬事实 | ✅ **仍然有效**（v2 不涉及贴图） |
> | §4 承重数字 | **已迁出** → [docs/archive/共享单车-开发流程-已作废章节.md](archive/共享单车-开发流程-已作废章节.md) §4（已被 v2 §7 取代；v2 复核了算式，并补了"人口越多越划算"的坑） |
> | §5 难点分级 | **已迁出** → [docs/archive/共享单车-开发流程-已作废章节.md](archive/共享单车-开发流程-已作废章节.md) §5（已被 v2 §3 / §5 细化取代） |
> | §6 双 TFM 结构 | **已迁出** → [docs/archive/共享单车-开发流程-已作废章节.md](archive/共享单车-开发流程-已作废章节.md) §6（已被 v2 §8 取代；含 1.7 的 Harmony 3.0 / 去掉 `UnityEngine.*` 引用 / `ProduceReferenceAssembly=false`） |
> | §7 发布渠道与 AI 美术 | **已迁出** → [docs/archive/共享单车-开发流程-已作废章节.md](archive/共享单车-开发流程-已作废章节.md) §7（v2 不涉及；本文 Step 6 的「AI 美术披露」见该处 §7） |
> | §8 待确证清单 | **已迁出** → [docs/archive/共享单车-开发流程-已作废章节.md](archive/共享单车-开发流程-已作废章节.md) §8（已被 v2 §9 取代；v2 从真值表 §5 重抄了相关项，并标注了**真值表里查不到的**符号） |
> | §9 竞品与空位 | ✅ **仍然有效**（v2 §9.3 只补了"为什么不用 VF/VVE"的取证结论） |
> | 附：当前运行状态 | ⚠ 时效性内容，以实际为准 |
>
> **v2 新增（本文没有的）**：架构总览图 + 机制来源对照、"停放区是**对整理工的指令**不是对骑车人的约束"这一核心主张、
> 人车解绑的**可执行检查表**（17 条，每条含触发点/动作/验证）、停放区**已决定用自定义 `Verse.Zone`**、
> 移动已存在车辆的正确做法、`Zone.get_Cells` 顺序随机化的编码纪律、整理工作的性能与确定性规则、未决问题（M1–M9）。
>
> **符号来源纪律**：所有类名 / 方法名 / 字段名 / Def 名以
> [共享单车-机制真值表.md](共享单车-机制真值表.md) 为准（v2 逐字符照抄；真值表里查不到的写"未确证"）。

> 本文回答的是「**怎么一次做对，不要来回改**」，不是「怎么写字面代码」。
> 载具机制的**技术调研全文**在 [短途载具与共享单车-技术调研.md](短途载具与共享单车-技术调研.md)（70 KB，含逐条 IL/源码出处）。
>
> **证据标注**：【实测】= 本会话在本机真实验证过；【IL】= 反编译 `Assembly-CSharp.dll` 确证；
> 【原版XML】= 读游戏自带 XML 确证；【调研】= 见技术调研文档；【推断】= 判断，无直接证据。

---

## 0. 先诊断：以前为什么会来回改

三条具体原因，每一条都可以在流程上堵住：

| 症状 | 根因 | 流程上的堵法 |
| --- | --- | --- |
| 贴图卡了很久 | 把贴图当**第 1 步** | 贴图是**第 5 步**；美术是**并行线**，但不阻塞逻辑线（§3） |
| 功能反复重构 | **边写代码边想机制** | Step 0 侦察 + Step 1 冻结 Def 规格，写代码前先把接口钉死（§2） |
| 打包/依赖出问题 | 依赖声明**最后才补** | `About.xml` 的 `modDependencies` / `loadAfter` 在 Step 1 就写（§2） |

**核心纪律**：先用【实测】和【IL】把未知量消掉，再动手。这个仓库里已有的正确范例是
[食物自动化-技术侦察.md](食物自动化-技术侦察.md)——它严格区分【实测】/【推测】，还专门列了
「**失败/不存在的路径，别再试**」。

---

## 1. 环境（已就绪，实测通过）

### 1.1 干净测试实例 ✅

```powershell
cd 'E:\2026.7以后的新工程\DSH\RimWorld_auto_test'
node scripts/modsconfig.mjs preset ratkin --profile bike   # 13 项
node src/cli.js start --profile bike                       # 桥接 24.5 秒就绪
node src/cli.js call rimworld/list_colonists --profile bike
```

| 项 | 值 |
| --- | --- |
| profile | `profiles/bike`（**独立存档，不碰主力配置**） |
| mod 数 | **13**（Harmony → Core + 6 DLC → XML Extensions → HAR → Ratkin → RimBridgeServer → dsh.autotest → CharacterEditor） |
| 桥接就绪 | **24.5 秒**【实测】 |
| 对照 | 原来的 `profiles/vanilla` 挂着 **567 个 mod**，桥接 **240 秒超时**——它名字叫 vanilla，内容却是满配 |
| 游戏版本 | `1.6.4871 rev590`，Mono / CLR4，`Assembly-CSharp 1.6.9676.17735` |

**这就是你选「先在干净环境做」的价值**：满配下桥接根本起不来，任何报错都要在几百个 mod 里找根因。

### 1.2 两个必须知道的操作事实

1. **游戏必须由长驻宿主托管。** 一次性命令里启动的游戏会随该命令结束被进程树清理掉。
   正确做法是后台任务里跑 `start`，再等游戏进到某状态：`node src/cli.js live --profile <p> --until '<条件>' --timeout <ms>`（后台 job，job 完成即命中；**不要** `Start-Sleep`）。
2. **`call` 必须带 `--profile bike`**，否则它会去找 `vanilla` 的桥接信息。

### 1.3 一个已经踩过的工具坑（会影响你以后所有取证）⚠

`tools/asmdump/AsmDump.exe` / `IlDump.exe` 对 **27 个类型静默失效**：
`Assembly.GetTypes()` 抛 `ReflectionTypeLoadException`（根因是 **C# 默认接口方法**，Mono 支持、
.NET Framework 4.x 反射不支持），而源码里 `Where(t => t != null)` **把 null 直接丢掉**。

**症状**：查这些类输出空白，**看起来像"这个类不存在"**。受影响的恰好全是寻路核心：
`Verse.AI.PathGrid`、`Verse.PathFinder`、`Verse.PathFinderJob`、`Verse.PathFinderMapData`、
`Verse.AI.PathingContext`、`Verse.AI.PawnPath`。

**补位工具**：`tmp/il-evidence-vehicles/mdb.py`（纯 ECMA-335 元数据解析，不加载类型，自带 `layout OK` 自检）。

另外：类型过滤器**没有 glob**。`=` 前缀 = 精确匹配，否则 = **大小写不敏感子串**。
所以 `"*PathFollow*"` 匹配不到东西是**语法问题**，不是类不存在。

### 1.4 编译（现状与 1.7 的冲突）

全仓库**没有任何 `.csproj`**，编译走 node 脚本直调 VS BuildTools 的 Roslyn `csc`：

```
/nologo /target:library /nostdlib+ /langversion:7.3 /optimize+ /deterministic+
/out:<dll> /reference:<游戏 Managed 各程序集>
```

- `/nostdlib+` + 引用**游戏目录的** `mscorlib.dll` → ABI 最稳
- `/langversion:7.3` → **C# 8+ 语法不许用**（游戏跑在旧 Mono 上）
- 静态闸门（正则 `\b(Verse|RimWorld|UnityEngine|HarmonyLib)\s*\.`）**禁止 Core 引用游戏类型**

⚠ **1.7 冲突**：1.7 换 Waylith 引擎 + **.NET 10 CoreCLR**，官方明说 *"You can use the latest C# language features"*，
Harmony 要升到 **3.0**。而**没有 csproj 就没有 `<TargetFrameworks>` 这个双版本载体**。
→ 你既然打算发布，**Step 1 就把 csproj 补上**（双 TFM 结构见 [docs/archive/共享单车-开发流程-已作废章节.md](archive/共享单车-开发流程-已作废章节.md) §6，已被 v2 §8 取代）。

---

## 2. 六步流程与每步的验收门

> 每一步都有**退出门（gate）**。gate 不过就不进下一步——这是「不来回改」的机械保证。

### Step 0 · 侦察（**不写一行产品代码**）

把未知量消掉，产出侦察文档。本项目的未知量已经基本消完（见技术调研文档），剩下 3 个：

| 未知量 | 为什么重要 | 怎么消 |
| --- | --- | --- |
| `Verse.PathRequest+IPathGridCustomizer` 的签名 | 若它是官方 mod 钩子，**可能替代 Harmony 补丁** | ILSpy 打开 `Assembly-CSharp.dll` 看（10 分钟） |
| 1.6 是否还有 map 级 `FindPath` | **不要按"肯定还在"写补丁** | 同上 |
| `Bicycles and more!` 的实现 | 你的直接竞品 | 无公开源码；只能行为观察 |

**其余结论都已确证**：加速出口、思考树插槽、预约 API、贴图规则——都不需要再查。

**Gate**：能明确回答「哪些路径已验证走不通、别再试」。

### Step 1 · 冻结 Def 规格（**接口先行**）

在写 C# 之前，先把 XML 里所有对外契约钉死：

```xml
<!-- ThingDef_SharedBike.xml -->
<ThingDef ParentName="...">
  <defName>SharedBike</defName>
  <thingClass>ThingWithComps</thingClass>
  <comps><li Class="MyMod.CompProperties_SharedBike"> ... </li></comps>
  <graphicData>
    <texPath>Things/Building/Misc/Stool</texPath>   <!-- ← 借原版占位 -->
    <graphicClass>Graphic_Single</graphicClass>
    <drawSize>(1,1)</drawSize>
  </graphicData>
</ThingDef>
```

- **`texPath` 先借原版**（实测可用：`Things/Building/Misc/Stool`、`Things/Building/Misc/EggBox`、
  `Things/Building/Misc/DropBeacon`、`Things/Building/Misc/ToolCabinet`、`Things/Special/DropPod`）
- **`drawSize` 此刻就冻结**——它决定美术的像素尺寸（§3）
- `About.xml` 的 `packageId` / `modDependencies` / `loadAfter` **现在就写**
- 建 `LoadFolders.xml`：`<v1.6><li>/</li><li>1.6</li></v1.6>`

**关键收益**：冻结后，改 `<texPath>` / `<drawSize>` / `<color>` / `<shaderType>` / `graphicClass`
**全部由 `Actions\Hot reload Defs` 亚秒生效，不用重启**。

**Gate**：`About.xml` + `LoadFolders.xml` + Def 骨架齐备；游戏里能 `Hot reload Defs` 后生成出这个物体。

### Step 2 · 逻辑骨架（五件套）

```
CompSharedBike.cs          IExposable；是否被骑 / 当前骑手 / 上次还车 tick
SharedBikeManager.cs       MapComponent：空间分桶索引、最近车查询缓存、熔断
JobGiver_RideSharedBike.cs ThinkNode_JobGiver；决策
JobDriver_RideSharedBike.cs Goto车 → 上车(Instant) → Goto终点 → 还车(Instant) → AddFinishAction
Patch_TicksPerMove.cs      Postfix Pawn.TicksPerMove(bool)
```

**三个已确证的机制，直接照抄，不要自己发明**：

1. **加速**：`StatDefOf.MoveSpeed` 的唯一玩法出口是 `Verse.Pawn::TicksPerMove(bool diagonal)`【IL】。
   它**每 tick 现算**，所以"上车加速、下车恢复"天然成立，无需任何状态重置。
   ⚠ **不要用 Hediff 当载体**——会进健康面板，带 `tendable`/`isBad`/`makesSickThought`/`makesAlert`
   一堆可见副作用；而且 **`HediffDef` 上根本没有 `statOffsets`**，它们在 `HediffStage` 上（容易写错层级）。
   ⚠ **不要用 Apparel**——倒地/被剥光/囚犯化/麻醉时会脱落 → **车凭空消失**。
2. **决策**：用自己的 `ThinkTreeDef` + `<insertTag>Humanlike_PostMain</insertTag>` + `<insertPriority>`
   → **一行 XML 插入，不用 patch 原版树**。这是 Ludeon 亲手在 `SubTrees_Misc.xml` 里写注释
   标注为 *"Insertion hook for modders"* 的官方机制【原版XML】。
   **不要用 WorkGiver**——共享单车是移动行为，不是工作。
3. **共享语义**：`ReservationUtility.Reserve(pawn, bike, job, maxPawns: 1, stackCount: 1, layer, ...)`
   → `maxPawns:1` = 一人一辆；预约**绑定 Job 生命周期**，Job 一结束**框架自动释放** = 用完归还。
   **不要用 `CompAssignableToPawn`**（那是玩家指派私有物，和你的需求正好相反）。

**计算"骑车是否更快"用直线距离估算，不要接寻路。**
1.6 刚重构过寻路（`Map.pathing` + `PathRequest` + `IPathFinderDataSource`），
且**没有已确证的"只算代价不返回路径"的公开入口**。把"骑车"变成 Job 序列里的一步，而不是寻路器的一个变量。

**Gate**：一个小人能在没有人为指令的情况下，自己走到车、骑上、到目的地、还车。

### Step 3 · 承重行为验证：**人车解绑总线**

这是**整个项目 80% 的风险所在**，必须在第一版就设计出来，否则第二版必然重构。

必须覆盖的解绑场景（漏一个就出现"车永远挂在死人身上"）：

```
☐ Job 正常完成
☐ Job 被打断（战斗 / Draft / 饥饿 / 疲惫 / 精神崩溃 / lord 征召）
☐ 倒地（Pawn_HealthTracker.MakeDowned）
☐ 死亡（SetDead）
☐ 被囚禁
☐ 麻醉 / 手术
☐ 读档（!!! 最容易漏）
☐ 车本身被毁 / 被拆（Comp.PostDeSpawn）
☐ 被 Teleport
☐ 被远行队带离地图
```

**参照**：Giddy-up 只打了 `MakeDowned` + `SetDead` 两个 Postfix——**那是下限，不是标准**，它连读档都没覆盖。

**Gate**：逐条走完上表，每一条之后回读确认「人恢复原速 + 车停在合法格且未被占用」。

### Step 4 · 存档往返

「骑行中」是跨对象跨 tick 状态（~~小人 ↔ 车 **双向引用**~~ → **改为单向**）。写错一个引用 →
读档后「人速度永久异常」或「车永久被占用」。

> ~~「骑行中」是跨对象跨 tick 状态（小人 ↔ 车 **双向引用**）。~~
> → **已被 [共享单车mod-设计-v2.md](共享单车mod-设计-v2.md) §3.4 推翻（2026-10-13）**：
> **只存单向**（只存「车 → 骑手」的 `Verse.Pawn` 引用），读档靠 `Scribe.mode == LoadSaveMode.PostLoadInit` **自愈**。
> **「双向引用」这个说法不再成立**——两处互指时 `ResolvingCrossRefs` 会让两端各自解析，
> 任一端对象被删就出现「一半引用为 null、一半还在」。
> ⚠ 另：`Scribe_References.Look<T>` 有泛型约束 `where T : Verse.ILoadReferenceable`，
> 所以「车 → 人」存的是 `Verse.Pawn`，**不是「车」自己**（详见 v2 §3.4 更正 A / B）。

- 所有跨 tick 状态 `IExposable`
- ~~双向引用一律 `Scribe_References.Look(...)`~~
  → **已被 [共享单车mod-设计-v2.md](共享单车mod-设计-v2.md) §3.4 取代（2026-10-13）**：
  **单向**（仅「车 → 骑手」一侧）`Verse.Scribe_References.Look(ref rider, "rider")`；
  小人**不持久化**「我在骑哪辆车」，运行时反查 `bike.CompSharedBike.rider == this pawn`。
  `Scribe_References` 是存档期工具、**不注册任何回调** ⇒ 解绑必须写在**载体侧**（v2 §3.4 更正 B）。
- 读档后**校验引用两边都在**（任一侧死亡/销毁要能自愈）

**Gate**：骑行中存档 → 退出 → 读档 → 人车状态与存档前一致；再走一遍 Step 3 的抽查项。

### Step 5 · 贴图替换（**并行线，见 §3**）

**Gate**：`占位 texPath 清单` 归零（每一条都换成自己的图）。

### Step 6 · 发布准备

```
☐ 自己的 license（必须回答 Ludeon 规则里的两个问题：
    能否被放进 modpack？是否允许他人做衍生？）
☐ Steam 工坊的 "Required Items" 也要写（工坊不读 About.xml 的 modDependencies，两套系统）
☐ 双 TFM 结构留位（net472 现在能编 / net10.0 留空，等 1.7 进正式分支）
☐ AI 美术披露（见 [docs/archive/共享单车-开发流程-已作废章节.md](archive/共享单车-开发流程-已作废章节.md) §7）
```

---

## 3. 两条并行线（这是治「贴图卡死」的药）

以前卡住是因为把两类改动混在一个循环里。**它们的重启成本差一个数量级**：

| 你改的东西 | 要重启吗 |
| --- | --- |
| `<texPath>` 指向另一个**已存在**的贴图 | **不用**，亚秒 |
| `<drawSize>` / `<color>` / `<colorTwo>` / `<shaderType>` / `graphicClass` | **不用**，亚秒 |
| **PNG 的像素本身** | **必须重启** |

机制根因【IL】：`ModContentPack.ReloadContentInt(bool hotReload)` 第一行就是 `if (hotReload) return;`，
而 `Actions\Hot reload Defs` 走 `hotReload:true`。贴图在装载期被读进
`ModContentHolder<Texture2D>` 的 `Dictionary` 并绑到 `GraphicData.cachedGraphic`，之后全流程只查内存表。

**规则：改像素攒批，一次重启验证全部；结构改动随时改，不重启。**

| | 逻辑线（你 / agent） | 美术线（你 / AI） |
| --- | --- | --- |
| Step 1 后 | 用占位 texPath 跑通全部逻辑；**冻结 `drawSize`** | 拿到**贴图规格清单**：像素尺寸 / 方向数 / 锚点 |
| Step 2-4 | 只改 XML 和 C#（C# 改完要重启） | 按规格出图，不要求立刻进游戏 |
| 每批 | 攒够 → **重启 1 次** → 批量 spawn + 截图验证 | 按批次交图 |

**关键契约**：让美术只做「与占位图同尺寸、同锚点、同方向数」的图。这样替换 = **纯文件替换，不改任何 XML**，
所以**不需要重新验证逻辑**。

### 贴图的硬事实（实测/IL）

- **`texPath` 不带 `Textures/` 前缀、不带 `.png` 后缀**。入表前会剥掉前缀和扩展名【IL】。
  `Textures/Buildings/Bike/Bike.png` → `<texPath>Buildings/Bike/Bike</texPath>`
- **方向后缀**：`_north` / `_east` / `_south` / `_west`（小写）；遮罩 `_m`【IL】
- **只有 `_north` 是必需的**：缺 north 会依次回退 `south`(+180°) → `east`(-90°) → `west`(+90°) → **无后缀单图**【IL】。
  → **AI 生图只出一张 `_north` 就能跑**，这是 AI 路线最大的利好（AI 保证四视角一致性几乎不可能）
- **朝西默认镜像，但受 `GraphicData.allowFlip` 门控**【IL】。硬证据：`_east` 875 个 vs `_west` **仅 30 个**【实测】
- **官方贴图不在磁盘上**：`Data\{Core,...}\Textures\` **不存在**，全在 Unity 包内
  （`resources.assets.resS` 等）。DLC 的 `AssetBundles\resources_<dlc>.manifest` 是**纯文本资源清单**，
  可直接 grep——这是查原版贴图路径的权威工具【实测】
- ~~**「128 px/格」不成立为通用规则**。三个已发布 mod 同为 `drawSize=(1,1)`，像素却是
  **64×64 / 256×256 / 500×500**【实测】。`drawSize` 单位是**格子**，与像素**解耦**，引擎按 `drawSize` 缩放~~
  → **已被 [贴图流水线-设计.md](贴图流水线-设计.md) §2.3.1 取代（2026-10-13）**：
  「`drawSize` 单位是格子、像素密度与占格**解耦**」这条**仍然成立**（这是它当初想说的，778 个样本复核过）；
  但**本项目已冻结 `pxPerCell = 128`**。**出图一律按 128**，否则会 `size_mismatch`。
- **白边坑**：松散 PNG 进游戏会被**内存压缩**，而画图软件常丢掉全透明像素的颜色数据 → 压缩渗色。
  修法：让软件**在透明像素里保留颜色（推荐黑）**，或描边外加一圈 **1% 不透明度黑描边**
- ⚠ **发布红线**：同名贴图路径**后加载者胜**。借来的原版 texPath **绝不能留在发布版**，
  否则轻则图不对，重则**覆盖掉原版贴图**

---

> ⚠ **§4 / §5 / §6 / §8 已作废并迁至 [docs/archive/共享单车-开发流程-已作废章节.md](archive/共享单车-开发流程-已作废章节.md)（原因：被 [共享单车mod-设计-v2.md](共享单车mod-设计-v2.md) 取代——§4→v2 §7、§5→v2 §3/§5、§6→v2 §8、§8→v2 §9）。原文逐字保留、不再维护。同一归档文件里还有 §7「发布渠道与 AI 美术」——该节本身**仍然有效**（v2 不涉及），但也已随本次剪裁移出本文。**

## 9. 竞品与空位

| Mod | 依赖 | 状态 |
| --- | --- | --- |
| Simply Bicycle (Vehicle) | **必需 Vehicle Framework** | 只到 1.4/1.5 |
| Bicycles and more! | **只依赖 Harmony** | 1.5 + 1.6，8,375 订阅 |

**两个都没做「共享 / 停放」语义**——玩家在评论区问的正是"能不能停车"。**这是真空位。**

---

## 附：当前运行状态

- 干净实例**正在运行**：PID 14668，`profiles/bike`，桥接 `127.0.0.1:5174`，
  3 名殖民者（Arnold / Buffy / Yuzuki）在 250×250 的 `Map_0` 上
- 它由后台任务托管（等状态用 `node src/cli.js live --profile bike --until '<条件>' --timeout <ms>`，不挂 `Start-Sleep`），**停掉：`node src/cli.js stop --profile bike`**
- 满配的 `profiles/vanilla`（567 项）**未被改动**
