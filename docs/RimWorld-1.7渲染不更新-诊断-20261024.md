# RimWorld 1.7「所有 pawn 画面位置不更新」诊断 —— 2026-10-24

> 环境：RimWorld **1.7.4985 rev1269**（.NET 10 `10.0.13-waylithclr.dev` + Waylith/Vulkan），入口 `RimWorld.exe`，安装 `D:\SteamLibrary\steamapps\common\RimWorld`。
> 用到的 profile：**`voiced`（6 项 mod）**（`rhah` 被另一个会话的实例占用，PID 12612，未抢占、未 stop；`vanilla` 561 项按项目规定不用）。
> 取证目录：`runs\render-diag-20261024\`（截图在 `screens\`，日志快照两份，读数 JSON 若干）。
> 口径：**【实测】**＝本机跑出来/读到的；**【推测】**＝未验证的推断，不当结论。
> 起停：本轮起了两次 `voiced`（PID 26080、2204），两次都用 `node src/cli.js stop --profile voiced`（**不带 `--force`**）结束，收尾后 `Get-CimInstance` 只剩别人的 PID 12612。全程未碰玩家存档、未改游戏安装/工坊、未改 `src\`。

---

## 0. 结论（3 行）

1. **能复现，而且能稳定复现**：在 `voiced`（6 项 mod、三个原版 Human 殖民者 + 一只 Muffalo，**没有 Ratkin/HAR/RHAH**）里，用 `step_game_ticks` 在暂停态推进 tick 后，pawn 的**绘制位置（贴图 + 名字标签）停在推进前的老格子上**，而坐标回读已经变了；镜头平移/缩放、生成新物件都不改变这个状态。
2. **不是 mod 引起，也不是我们的启动参数/窗口引起**：现象在最小 profile 里同样出现；1.7 的渲染器是 **Waylith + Vulkan**（日志可证），Unity 时代的 `-force-glcore` 之类参数根本没有解析者；`-monitor` 确实被忽略（窗口落在主屏），`-screen-width/height` 生效（游戏内部屏幕 1600×900），日志里 813×486 是 **DPI 虚拟化读数**（窗口 DPI=192），不是被忽略的证据。
3. **触发条件是「暂停态推进 tick」，不是「1.7 渲染整体坏了」**：只要让游戏**真实跑帧**（`play_for` ≥ 约 400 ms / 74 tick），同一个实例里 pawn 立刻回到真实格子（两次独立验证）；随后再 `step_game_ticks`，又会冻住。⇒ 截图/人眼观察**只能在真实跑帧后**做，暂停态 step 之后的画面不能作为位置证据。

---

## 1. 现象确认：截图像素位置 vs 坐标回读

### 1.1 方法（为什么这套判据可信）

- 坐标：`rimworld/list_colonists`（`position.x/z`）、`rimworld/get_cells_info`（扫物件的真实格子）。
- 「画面上在哪」用两条互相独立的判据：
  - **① 整帧截图里的名字标签包围盒**（自动检测近白文字簇，输出像素中心）。同一相机下可直接比较；**跨相机不可比**（相机有亚格偏移，实测同一批 pawn 的标签会整体平移 ±10 px）。
  - **② `rimworld/screenshot_cell_rect` 按格子裁剪**（工具自己移镜头 + 按格裁切）：把某个**已知格子**裁下来，看 pawn 在不在里面。这条判据不依赖我算像素映射，**是最硬的一条**，并且它自带反证：同一工具在「活帧」状态下给出的正是真实格子（见 §1.3）。
- 标签中心有 ±1 格噪声（pawn 走路中标签会随插值位置浮动），所以标签只用来判「**有没有动**」（像素完全相同 ⇒ 没更新），绝对位置以格子裁剪为准。

### 1.2 冻结的对照组（第一次会话，PID 26080，19:32 起）

相机 `(107,111)`、`rootSize 24` 全程未动；t0 是开局暂停帧，t1 是 `step_game_ticks{600}` 之后。

| pawn | t0 坐标【实测】 | t0 标签像素中心 | t1 坐标【实测】 | t1 标签像素中心 | 坐标变化 | 像素变化 |
|-|-|-|-|-|-|-|
| Avila（Human612） | (104,110) | (745,486) | **(110,111)** | (745,486) | **+6 格 X** | **0 px** |
| Ren（Human609） | (110,115) | (857,396) | **(114,113)** | (857,396) | **+4 格 X、−2 格 Z** | **0 px** |
| Vanua（Human615） | (106,112) | (779,451) | (106,112) | (777,451) | 0（对照，站着） | ≤2 px |

截图：`runs\render-diag-20261024\screens\t0-paused.png`、`t1-after600.png`（1600×900，桥接 `take_screenshot`，同一相机）。
t0 的标签像素是**和坐标吻合**的（三点拟合 ≈17.5–18.3 px/格，X 与 Z 都自洽），说明**t0 那一帧是活帧**；t1 开始不吻合。

**格子裁剪的硬证据**（t1 时刻，坐标回读 Avila=(110,111)、Ren=(114,113)）：

| 裁剪中心格 | 截图 | 里面有没有那个 pawn |
|-|-|-|
| (104,110)＝Avila 的**旧**格 | `screens\t1-crop-avila-oldcell-104-110__cell_rect.png` | **有**（"Avila" 标签 + 人都在画面中心） |
| (110,111)＝Avila 的**真实**格 | `screens\t1-crop-avila-newcell-110-111__cell_rect.png` | **没有**（只有 Ren 的标签和植被） |
| (110,115)＝Ren 的**旧**格 | `screens\t1-crop-ren-oldcell-110-115__cell_rect.png` | **有**（Ren 在裁剪中心） |

冻结不是「一次性的」：t4（又 step 600）、t5（draft + step 60）与 t1 一样冻着。t4 有一个**自洽性反证**：那一刻 Avila 与 Vanua 的坐标**同为 (108,112)**，但两人画在两个不同的像素（标签 (754,476) 与 (789,442)）⇒ 绘制位置**不等于**真实坐标。

### 1.3 活帧对照（同一实例，真实跑帧后）

| 时刻 | 动作 | 三个 pawn 的坐标【实测】 | 标签像素中心 | 判定 |
|-|-|-|-|-|
| t7 | `play_for{20000, Fast}`＝**3603 tick 真实帧** | Ren(115,124) / Vanua(111,114) / Avila(108,112) | (950,233) / (879,414) / (818,452) | **活**：三点拟合 X 17.75 px/格、Z ≈18 px/格，自洽 |
| t8 | 暂停，不推进 | 同上 | 与 t7 完全相同 | 正常（暂停不推进本来就不该动） |
| t9 | `step_game_ticks{60}` | Ren(**115,123**) / Vanua(**112,114**) / Avila(108,112) | 与 t8 **完全相同** | **冻** |
| t10 | `step_game_ticks{120}` | Ren(**112,121**) / Vanua(**114,113**) / Avila(108,112) | (950,233)/(879,414)/(818,452) 仍不变 | **冻** |
| t11 | `play_for{400, Fast}`＝**74 tick** | Ren(111,120) / Vanua(114,113) / Avila(108,112) | (875,309)/(930,433)/(818,452) | **活**：X 18.7–19 px/格、Z −17.9/−19 px/格，自洽；没动的 Avila 像素不变 |

截图：`screens\t7-after-play20s.png`、`t8-paused-no-step.png`（在 `profiles\voiced\Screenshots\`）、`t9-after-step60.png`、`t10-after-step120.png`、`t11-after-play400ms.png`。

**恢复条件**：只要真实帧循环跑过（400 ms＝74 tick 就够），绘制位置立刻追平；`screenshot_cell_rect` 那种「移镜头再截」**不能**恢复（t4/t6 期间多次移镜头、改缩放，pawn 仍停在旧格）。**【实测】**

---

## 2. 是哪一层不更新

| 层 | 观测 | 结论 |
|-|-|-|
| ① UI（时钟/温度/光照/警报/教学助手/TPS） | 同一次 600 tick 里：室外 `6C→7C`、光照 `64%→72%`、右侧新增警报 `缺少床铺`、教学助手新增 `镜头移动`；t9→t10 的 UI 文本区也照常变化（见 t0/t1、t9/t10 截图） | **UI 是活的**，每帧都在重画 |
| ② 物件/建筑 | 会话中途 `spawn_thing Wall @(100,106)`，下一帧的裁剪图 `screens\t4-crop-wall-100-106__cell_rect.png` 里就出现了那面墙（位置正确）⇒ 新建/静态物件照常上屏。开门动画/受击闪烁**未测**（没有搭门与伤害场景） | **静态物件层正常**；动态动画未验证 |
| ③ 动物/机械体 | 会话中途用调试动作 `Actions\Spawn Pawn...\Muffalo` 在 **(120,111)** 生成一只雪牛；600 tick 后 `get_cells_info` 扫 961 格确认它的真实格已是 **(120,116)**（(120,111) 已空）。裁剪图：`(120,111)`＝`screens\t6-crop-muffalo-spawn-120-111-t6__cell_rect.png` **有雪牛**；`(120,116)`＝`screens\t6-crop-muffalo-true-120-116__cell_rect.png` **没有** | **动物和殖民者一样冻**（不是鼠族/人形专属） |
| ④ 相机 | 同一冻结期内多次 `screenshot_cell_rect`（移镜头 + 还原，含 `rootSize 24→14→24`）：每张图的地形/植被都是**新位置、新缩放**，`screens\t6-zoom14.png` 是干净的缩放后画面 | **相机移动/缩放后的重绘正常**；pawn 的绘制位置不受影响，照旧停着 |

**一句话**：不更新的是 **pawn（Pawn 这个可动实体）的绘制位置**；UI、地形、静态物件、以及相机重绘都是正常的。

> 方法论提醒【实测】：**整图 diff 不能用来判 pawn** —— 120 tick 内草地/光照本身就在变（t9→t10 全图 203k 像素变化，其中地图区 146k），会把「人没动」淹没掉。要判位置只能用「标签文本包围盒是否像素级不变」或「按格子裁剪里有没有人」。

---

## 3. 干净环境对照（是不是 mod 引起的）

- 用的 profile：**`voiced`**，`profiles\voiced\Config\ModsConfig.xml` 里 6 项：`brrainz.harmony` / `ludeon.rimworld` / `rwzh.chinesepack.achtung` / `brrainz.rimbridgeserver` / `xiayuan2004.VoiceDeputy` / `dsh.gamelink`；`rimworld/list_mods{activeOnly:true}` 回读 `loadedInSession:true` **正好这 6 项**（其余是装了但没启用）。**【实测】**
- 这一局里出场的全是**原版种族**：3 个 `Human` 殖民者 + 1 只 `Muffalo`。没有 Ratkin、没有 HAR、没有 RHAH、没有 Oxygendsl/IrisMenus。**【实测】**
- 结果：**同样的冻结照旧出现**（§1）。⇒ **与鼠族贴图缺失、与 RHAH 无关**；`Things/Ratkin/Head/` 那条只是「部件没画出来」，是另一个独立现象。
- 剩下的可疑者只有 Core + 5 个非 Core 项（含我们的 `dsh.gamelink` 与 `brrainz.rimbridgeserver`），逐个排除的证据：
  - `dsh.gamelink` 源码里**只** patch `Verse.Log.Message/Warning/Error` 三个方法（`mods\DSH_GameLink\Source\LogTap.cs:32-34`、`86-104`），不可能影响绘制。
  - `RimBridgeServer` 全部 DLL 扫符号：**只有 `RimBridgeServer.dll` 出现 1 次字符串 `DrawPos`**，`PawnTweener`/`DrawTracker`/`PawnRenderer`/`Pawn_Draw` 均为 0 命中 ⇒ 没有证据表明它改写了 pawn 绘制链。
  - 现象与「暂停/推进方式」强相关（§1.3、§4），与 mod 内容无关。
- **未能做 Core-only 对照**：任务禁止新建 profile，`rhah` 被别的会话占着（PID 12612），`vanilla` 561 项禁用。所以「这 5 个非 Core mod 里没有一个是共犯」**未 100% 排除**，但上面的源码/符号证据与「触发条件随暂停/推进变化」都指向引擎侧。

---

## 4. 启动参数核对（我们到底传了什么、有没有被收到/生效）

`src\launcher.js:867-887` 组装的参数（**唯一**一处组装）：

| 行号 | 参数 | 说明 |
|-|-|-|
| `:868` | `-savedatafolder=<profileDir>` | profile 隔离 |
| `:869-870` | `-logFile <logFile>` | 1.7 **忽略**它（日志固定写默认存档目录，`launcher.js:104-107` 已按版本分流） |
| `:871-872` | `-screen-fullscreen 0/1` | 来自 `effectivePrefs.fullscreen` |
| `:873-876` | `-screen-width/-screen-height` | 来自 `Prefs.xml` 的 `screenWidth/Height`（`prefs.js:25-34` 每次强制 1600×900） |
| `:879` | `-monitor N`（可选） | 1-based |
| `:885` | `--doorstop-enabled false` | 关 Doorstop |
| `:886` | `extraArgs` | 目前没人传过渲染类参数 |

游戏侧收到的原文（两次会话的日志头都有，**逐字**）【实测】：

```
Command:   D:\SteamLibrary\steamapps\common\RimWorld\RimWorld.dll -savedatafolder=E:\…\profiles\voiced -logFile "C:\Users\MikanseiP\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Logs\Player.log" -screen-fullscreen 0 -screen-width 1600 -screen-height 900 -monitor 2 --doorstop-enabled false
```

逐项判定【实测】：

| 参数 | 是否收到 | 是否生效 | 证据 |
|-|-|-|-|
| `-screen-fullscreen 0` | 是 | **生效**（窗口化） | `GetWindowRect`/`IsIconic` 可查，是普通窗口 |
| `-screen-width 1600 -screen-height 900` | 是 | **生效**（桥接截图 1600×900＝游戏内部屏幕尺寸） | `take_screenshot` 输出 1600×900；`Prefs.xml` 也是 1600×900，**两者同值，无法分离谁起作用**（未验证） |
| `-monitor 2` | 是 | **未生效** | 窗口落在 `\\.\DISPLAY1`（主屏）：`[System.Windows.Forms.Screen]::FromHandle(hwnd).DeviceName = \\.\DISPLAY1` |
| 窗口 813×486 之谜 | — | **不是被忽略** | 该显示器 **DPI=192（200%）**；我的 PowerShell 进程 DPI-unaware，看到的是虚拟化坐标：外框 813×486＝物理 1600×900 客户区 800×450 的 2× 折算。游戏内部实际就是 1600×900 |
| `-force-glcore` / Unity 图形开关 | — | **无意义** | 1.7 已不是 Unity Player：日志是 `=== Waylith log ===`、`Runtime: .NET 10.0.13-waylithclr.dev`、`[VulkanStartup]`、`[GraphicsDevice] … Vulkan`；Unity 仅以 `Waylith.UnityCompat.*` 兼容层存在。**换这些参数不可能排除本现象**（本轮未实测传过，属推断） |

**建议**：不用再改启动参数去「排除渲染问题」——参数这条线已被排除（现象与暂停/推进方式相关，与窗口/显示器/Vulkan 设备无关）。真要排查引擎侧，唯一值得做的对照是「同一存档、同样 step，换一个 GPU 或换全屏」，而不是 Unity 参数。

---

## 5. 日志证据（原文）

两份快照（全机共用 `Player.log`，按启动头判定归属）：

- `runs\render-diag-20261024\player-log-rhah-14mods-20261024-1915.log`（**别人那局** 14 项 mod，pid 12612，19:15:21 起，我复制时 19:30）
- `runs\render-diag-20261024\player-log-voiced-20261024-1932.log`（**我这一局** 6 项 mod，pid 26080，19:32:15 起）

**有意义的行（逐字）**：

```
2026-10-24 19:15:21.400  INFO [VulkanStartup] Vulkan loader: bundled 'D:\SteamLibrary\steamapps\common\RimWorld\vulkan\vulkan-1.dll' (file version 1.4.363.0, system 1.4.341.0, bundled 1.4.363.0).
2026-10-24 19:15:22.073  INFO [GraphicsDevice] GPU [0] 'NVIDIA GeForce RTX 5080' (DiscreteGpu, Vulkan 1.4.341, driver NvidiaProprietaryKHR 'NVIDIA' '610.88', 15977 MB device-local, uuid 544F79CECED3B6D99E0310E37FB270A7) - usable (score 288230376151777279).
2026-10-24 19:15:22.075  INFO [GraphicsDevice] Selected GPU 'NVIDIA GeForce RTX 5080' (driver NvidiaProprietaryKHR 'NVIDIA' '610.88') - best automatic match of 2 device(s).
2026-10-24 19:15:23.726  WARN [Verse] Type FrostSceneBinding probably needs a StaticConstructorOnStartup attribute, because it has a field Texture of type SceneTexture. All assets must be loaded in the main thread.
2026-10-24 19:21:48.215  INFO [Vulkan] Rendering resumed after 243.4 s minimized, 0.0 MiB of texture disposals pending
2026-10-24 19:26:48.097  INFO [Vulkan] Rendering resumed after 2.6 s minimized, 0.0 MiB of texture disposals pending
2026-10-24 19:15:26.453 ERROR [Verse] Failed to find Verse.TerrainDef named AncientHeavyBridge. There are 383 defs of this type loaded.
```

我这一局（6 项 mod）**全量 WARN/ERROR 清单**只有：1 条 Vulkan loader 噪音 + 一堆「mod 缺 downloadUrl」的 About.xml 警告 + 2 条重复 packageId + 1 条中文翻译 39 错；**没有任何** `Shader`/`GL`/`D3D`/`HarfBuzz`/`Exception` 级别的渲染报错：

```
2026-10-24 19:32:16.295 ERROR [Vulkan] [General] [Loader Message] loader_get_json: Failed to open JSON file C:\Program Files\Rockstar Games\Social Club\SocialClubVulkanLayer.json
2026-10-24 19:32:18.232 ERROR [Verse] Tried loading mod with the same packageId multiple times: wuren.ToddlersFeed. Ignoring the duplicates.
2026-10-24 19:32:18.278 ERROR [Verse] Tried loading mod with the same packageId multiple times: Fluffy.FollowMe. Ignoring the duplicates.
2026-10-24 19:32:22.026  WARN [Verse] Translation data for language Simplified Chinese has 39 errors. Generate translation report for more info.
```

- `Failed to find any textures at Things/Ratkin/Head/`：**在我抓到的这两份日志窗口内都没有出现**（rhah 那份只覆盖 19:15 启动→19:30，那次没有进图）。⇒ 这条来自别的时段/别的会话，本轮无法复述原文，也不参与结论。
- `Rendering resumed after … minimized`：**Waylith 会在窗口最小化期间暂停渲染**（别人那局 243.4 s / 2.6 s 两次）。我在自己局里直接验证了后果：**窗口最小化时 `take_screenshot` 直接失败**——`"Timed out waiting for RimWorld to finish writing the screenshot file."`，而同期逻辑照跑（15 s 推进 2704 tick）。恢复窗口后截图立刻正常。
- 结论：**日志里没有任何渲染错误能解释 pawn 冻结**，它是一条「静默」的行为差异。

---

## 6. 判定与绕过建议

**① 最可能的原因（按证据强度排序）**

1. 【实测结论】**1.7/Waylith 下 pawn 的绘制位置只在真实帧循环里更新；在暂停态推进 tick（`step_game_ticks`，我们所有自动化都在用）时，逻辑位置变了、绘制位置不刷新。** 触发/恢复都是可重复的：step→冻，`play_for` 400 ms→活。**与 mod、与启动参数、与窗口尺寸/显示器无关**（6 项 mod 的最小 profile、原版 Human/Muffalo 同样复现）。
2. 【推测，未验证】机制层面最像「pawn 的插值/绘制位置（`PawnTweener` / `DrawPos` 一类）在 1.6 里随 Tick 推进，在 1.7 里改成了随帧/预绘制阶段推进」，于是「暂停 + 强行 DoSingleTick」这条路径不再刷新它。1.6 已从本机消失，无法做同场景对照。
3. 【实测，旁证】窗口最小化时 Waylith 暂停渲染（日志 + 截图失败）：任何「最小化期间取画面/观察画面」的做法都拿不到新画面，这会让「所有 pawn 不动」看起来更严重。这是**加成因素**，不是根因。

**② 能不能绕过（能，而且很简单）**

- **观察/截图前必须让游戏真实跑帧**：`step_game_ticks{...}` 之后追加一次 `play_for{durationMs:400~1000, speed:"Fast"}`（实测 74 tick 足够），再 `take_screenshot`/人工看。反过来：**只在暂停态 step 过的画面上判定位置＝必然错**。
- **位置判据改用「坐标回读 + 按格裁剪」**：`list_colonists.position` / `get_cells_info` 定坐标；要视觉佐证就用 `screenshot_cell_rect` 裁「真实格」和「旧格」各一张，两张对照比任何全屏截图都硬（本轮就是这么定性的）。
- 不要用「整图 diff」「pawn 在屏幕中心」这类判据（草在飘、标签位置还有 ±1 格浮动）。
- 窗口尺寸/渲染参数/换显示器**不需要动**：它们与现象无关（`-monitor` 被忽略只影响窗口落哪块屏，不影响 pawn 位置）。
- 若只是要人眼看行为：让游戏正常播放（不暂停）+ 不最小化窗口，pawn 位置是准的（t7/t11、m3 三次验证）。

**③ 如果按 1.7 unstable 的已知缺陷上报，需要带什么**

1. 版本头：`Version: 1.7.4985.1269` / `Runtime: .NET 10.0.13-waylithclr.dev` / `=== Waylith log ===` 前 8 行（含 `Command:` 行）。
2. 环境：GPU 选择行（`Selected GPU 'NVIDIA GeForce RTX 5080' … driver '610.88'`）、Vulkan 版本行（`Effective Vulkan version 1.2.0 (instance 1.2.0, device 1.4.341)`）、显示器 DPI=192、窗口 1600×900 窗口化。
3. **最小复现**（本报告 §1.3 就是现成的）：开调试殖民地 → 记录 3 个殖民者坐标 → `step_game_ticks 600`（暂停态）→ 截图 + 再读坐标（两者不一致）→ `play_for 400` → 再截图（一致）。附 4 张图（step 前 / step 后 / play 后 / 按格裁剪的旧格与新格）。
4. 明确指出：**无任何日志报错**（贴 §5 的完整 WARN/ERROR 清单，证明是静默行为差异），且**在 6 项 mod 的最小 profile 下同样复现**（附 `list_mods` 的 6 项清单），因此与第三方 mod 无关。
5. 说明影响面：所有自动化/截图判位置的做法在 1.7 下都会得到错误结论（这是本轮最重要的工程后果）。

---

## 7. 未验证 / 明确没做

1. **Core-only（0 非 Core mod）对照没做**：不能新建 profile，`rhah` 被别的会话占着，`vanilla` 561 项禁用。所以「Harmony / RimBridgeServer / VoiceDeputy / 中文包 / GameLink 五者皆非共犯」只有源码与符号层面的间接证据。
2. **1.6 对照没做**（本机 1.6 已被 Steam 删掉）。「1.6 里 step 之后画面是准的」只是历史文档的用法推断，未实测。
3. **机制层没定位**：没有读 1.7 的 `Assembly-CSharp`/Waylith 反编译去确认 `DrawPos`/`PawnTweener` 到底改了什么（只做了行为学定性）。
4. **门的开合动画、受击闪烁**等「已有物件的动画」没测（只测了「新生成的墙/雪牛能不能正常上屏」）。
5. **`-screen-width/height` 与 `Prefs.xml` 谁在起作用**：两者同值（1600×900），无法分离；**未验证**。
6. **Unity 图形参数（`-force-glcore` 等）**：判断为「1.7 已无解析者」，但**没有真的传进去试**（属推断，不是实测）。
7. **最小化 → 恢复** 是否会让 pawn 停在旧位置：只测到「最小化期间截不出图、逻辑照跑」，恢复后的第一帧（m2）标签位置已接近真实位置，看不出冻结；**未做逐格裁剪确认**。
8. 人类「亲眼看屏幕」那一次的具体状态（当时是否暂停中、是否刚 step 过、窗口是否最小化）无法复原——**本报告只证明「暂停态 step 之后画面位置是假的」以及「真实跑帧后是真的」**。
9. 我这一局里 `m0`（4 s 真实播放后）三个标签的像素不满足单一仿射模型，与「活帧」判定不一致；原因未查（可能是行走中标签浮动 ±1 格 + 我按宽度分配名字），**不作为结论使用**。
10. 过程中有一次 `Start-Sleep -Milliseconds 300`（`ShowWindow(SW_MINIMIZE)` 之后确认最小化生效，属 OS 调用后确认，不是等游戏状态）；其余等待全部走 `live`/桥接回读，无固定延时轮询。

---

## 附：证据文件清单（`runs\render-diag-20261024\`）

| 文件 | 是什么 |
|-|-|
| `screens\t0-paused.png` / `t1-after600.png` | 冻结对照：同一相机、间隔 600 tick 的两帧（Avila +6 格、Ren +4 格，标签 0 px 位移） |
| `screens\t7-after-play20s.png` / `t9-after-step60.png` / `t10-after-step120.png` / `t11-after-play400ms.png` | 活→冻→冻→活 的四帧；`t11` 三点拟合 18.7–19 px/格 |
| `screens\t6-zoom14.png` | 相机缩放到 rootSize 14 后的整帧（地形正确重绘，pawn 仍冻） |
| `screens\t1-crop-avila-oldcell-104-110__cell_rect.png` / `…-newcell-110-111…` | Avila 在**旧格**、不在**真实格** |
| `screens\t1-crop-ren-oldcell-110-115__cell_rect.png` / `…-newcell-114-113…` | Ren 在**旧格** |
| `screens\t4-crop-wall-100-106__cell_rect.png` | 中途 `spawn_thing Wall` 的墙出现在正确格子（物件层正常） |
| `screens\t6-crop-muffalo-spawn-120-111-t6__cell_rect.png` / `…-true-120-116…` | 雪牛在**生成格**、不在**真实格**（动物同样冻） |
| `screens\m0-live-baseline.png` / `m2-right-after-restore.png` / `m3-after-play400ms.png` | 第二次会话（最小化实验）的三帧 |
| `screens\m3-domi-true-130-131__cell_rect.png` / `m3-element-true-125-123__cell_rect.png` | 活帧自证：按**真实格**裁剪，Domi/Element 就在里面 |
| `player-log-rhah-14mods-20261024-1915.log` | 别人那局（14 项 mod）19:15–19:30 的共用日志快照（含 `Rendering resumed after 243.4 s minimized`） |
| `player-log-voiced-20261024-1932.log` | 我这一局（6 项 mod）的完整日志快照 |
| `t0/t1/t3/t4/t5/t7/t9/t10/t11-*.json`、`m0/m1/m3-colonists.json`、`t6-cells-scan.json` | 逐时刻坐标/相机/物件读数原文 |
| `diff-stats.json` | 整图 diff 分区域计数（用来说明「整图 diff 不可用」） |
