# RimBridgeServer 实战手册

在 RimWorld 1.6 + RimBridgeServer 2.1.1 上实测总结的操作经验。
每一条都有实测依据；标 ⚠ 的是**会把桥接搞死或结果骗人**的坑。

配套命令：`node src/cli.js call <工具> --args-file <json文件>`

---

## 0. 会话纪律（最重要，先看这条）

**桥接把每个调用排在游戏主线程上。某个调用一旦挂住，后续所有调用都会跟着挂死，包括只读的。
只能重启游戏实例才恢复。**

因此：

- 任何调用都必须设**有限超时**。本仓库默认 60 秒（`src/gabp.js`），长操作在调用处单独放大。
- 一旦收到 `GABP_TIMEOUT`，**当成「会话已废」**，重启游戏，不要重试。
- 排查「卡住了」的第一步永远是 `node scripts/diag.mjs`（每步 15 秒超时），
  它读 `rimbridge/get_bridge_status` + `list_operations`，能看到最后完成到哪一步。
- 别用 15 分钟这种超时去「等它」，那只会让你什么都看不到。

---

## 1. ⚠ 已知地雷清单

| 工具 | 症状 | 证据 | 对策 |
| --- | --- | --- | --- |
| `rimworld/create_allowed_area` | **无限挂起**，且之后 `list_areas` 等只读调用一起挂死 | 实测 `select=false/true` 都挂；挂起后 `list_areas` 也 20s 超时 | **不要用**。要建区域就用别的方式（或手动）；一旦调用就准备重启 |
| `rimworld/spawn_thing` 传 pawn | 返回 `success:false` + `Verse.Pawn.SpawnSetup` NullReferenceException | `Human` / `Muffalo` / `Thrumbo` / `Boomrat` 全部失败；`Silver` / `Wall` 正常 | **只用来生成物品**。要生成 pawn 走 debug action |
| `rimworld/search_debug_actions` 某些查询 | 会挂死桥接 | 查 `"spawn pawn"`（带空格）30s 超时；查 `"spawn"` 正常（返回 18748 条） | 用**单个关键词**，别用短语；先 `limit` 小一点 |
| `rimworld/get_camera_state` 无地图时 | `success:false` + NullReferenceException | 主菜单下调用 | 先确认 `hasCurrentGame=true` 再调 |
| Doorstop 与 RimBridgeServer | mod 加载失败 `TypeLoadException`，桥接完全起不来 | 见 README 坑 1 | 启动加 `--doorstop-enabled false` |
| 人工与自动化抢窗口 | 游戏卡死 | 实测发生过 | agent 驱动期间别碰那个窗口 |

---

## 2. 开一个干净的测试场景

```js
// 主菜单 → 调试殖民地，一步到位（实测约 4 秒）
await call('rimworld/start_debug_game_ready',
  { readiness: 'mapData', pauseIfNeeded: true, timeoutMs: 300000 },
  { timeoutMs: 330000 });          // 请求超时要大于工具自身的 timeoutMs
await call('rimbridge/wait_for_long_event_idle', { timeoutMs: 120000 });
```

`readiness` 取值：`gameData` < `mapData` < `currentMap` < `playable` < `visual`。
只要数据不要画面就用 `mapData`（快得多）；要截图/UI 才用 `visual`。

殖民者名字每次随机（Crashlanded 剧本），**不要硬编码小人名字**，一律 `list_colonists` 现取。

---

## 3. 造建筑（用建筑师设计器）

```js
await call('rimworld/set_god_mode', { enabled: true });

// 1) 拿分类
const cats = (await call('rimworld/list_architect_categories')).categories;
const structure = cats.find(c => c.categoryDefName === 'Structure');

// 2) 拿设计器
const list = (await call('rimworld/list_architect_designators',
  { categoryId: structure.id })).designators;
const wall = list.find(d => /^wall/i.test(d.label));
//   wall.id            = architect-designator:structure:build-wall
//   wall.stuffDefName  = "WoodLog"   ← 材料已经默认选好了
//   wall.childCount    = 0           ← 不需要再选子项

// 3) 落图（支持一次落一片）
await call('rimworld/apply_architect_designator',
  { designatorId: wall.id, x, z, width: 9, height: 1 });
```

**关键点，全是踩过的**：

- **上帝模式开着时是瞬间建成**，所以验证要看 `cell.things`，**不是** `cell.blueprintBuildDefs`。
  我一开始查 blueprint 得到空数组，误判"没生效"，其实墙已经造好了。
  - 上帝模式开：`things` 里直接出现 `"Wooden wall"`
  - 上帝模式关：才会是蓝图，看 `blueprintBuildDefs`
- 标签带省略号（`wall...` / `door...` / `bed...`）**不代表要选材料** —— 看 `stuffDefName` 是否已有值。
  这里 `wall...` 已经有 `WoodLog`，直接 apply 就行。
- **`list_architect_designators` 返回的是全部设计器**，包括 `Cancel` / `Deconstruct` 这类非建造项，
  而且是**平铺的**（165 个地板项挤在一起）。要么按 `kind === 'build'` 过滤，要么精确匹配 label。
- **不要靠「哪个分类里有匹配项」来找** —— 我这样写第一次匹配到了 Furniture 的 "wall torch lamp"。
  正确做法是先按 `categoryDefName` 定位分类，再在分类内精确匹配。
- `apply_architect_designator` 支持 `dryRun: true` 预演，但要看清返回里的 `actionable`/`supportsCellApplication`。
- `success: true` **只表示设计器被应用了**，不保证格子真的变了。**永远回读格子确认。**

---

## 4. 生成东西

```js
// ✅ 物品：可以
await call('rimworld/spawn_thing', { defName: 'Silver', x, z, stackCount: 1000 });
await call('rimworld/spawn_thing', { defName: 'Wall', x, z });          // Def 名，不是 label

// ❌ pawn：会崩，别用
await call('rimworld/spawn_thing', { defName: 'Muffalo', x, z });       // NullReferenceException
```

要生成 pawn，用 debug action 树（见下一节），例如 `Actions\Spawn world pawn...` 这类。
选之前先用 `search_debug_actions` 看 `node.execution.supported` 和 `requiredTargetKind`。

**已实测验证的正确做法**（替代会崩的 `spawn_thing`）：

```js
await call('rimworld/execute_debug_action', {
  path: 'Actions\\Spawn Pawn...\\Thrumbo',   // PawnKindDef 名
  x, z,                                      // 该叶子 requiredTargetKind = map
});
```

`Actions\Spawn Pawn...` 下有 282 个 pawn（Thrumbo / Muffalo / Elephant / Bear_Grizzly …），
执行类型统一是 `MapTarget`。

---

## 5. 读状态

```js
const c = (await call('rimworld/get_cell_info', { x, z })).cell;
c.terrainDefName      // 不是 terrain
c.walkable
c.things[]            // { defName, label, className, isBlueprint, ... }
c.blueprintBuildDefs  // 未建成时的蓝图 Def 名
c.frameBuildDefs
c.designationCount
```

- 批量：`get_cells_info({x, z, width, height})` —— **上限 1024 格**。20x20=400 格很轻。
- 殖民者：`list_colonists({currentMapOnly:true})` → `colonists[]`，含 `pawnId`（稳定 id，形如 `Thing_Human802`）、
  `position`、`job`、`downed`、`dead`。
- 选中语义：`get_selection_semantics`。**inspect 文本在 `selectedObjects[].inspectString`**，
  不是 `inspectStrings`（后者实测一直是空数组）。这是验证 pawn 状态最直接的文本通道。
  顶层键：`hasSelection` / `selectionFingerprint` / `selectedCount` / `selectedObjects` / `uiState`。
- Gizmo：`list_selected_gizmos` → 未征召时只有 1 个（`Draft`）。要先 `set_draft` 才能看到更多。
  拿到 `gizmoId` 后 `execute_gizmo`。

---

## 6. 时间控制

| 方式 | 用途 | 实测 |
| --- | --- | --- |
| `pause_game({pause:true})` | 先暂停 | — |
| `step_game_ticks({ticks, pauseFirst:true})` | **精确推进，可断言** | 推 120 tick = 120 帧，返回 `advancedTicks`/`advancedFrames` |
| `set_time_speed({speed:'Ultrafast', ultraSpeedBoost:true})` + `play_for({durationMs, speed})` | 真实速度跑一段 | **12 秒实时推进了 18129 tick**（约 4700 tick/秒） |
| `play_until_letter` | 跑到出现新信件 | 适合等事件 |

- `step_game_ticks` 的返回里 `startTicksGame` / `endTicksGame` / `advancedTicks` 可以直接当断言，最稳。
- 做回归测试优先用 `step_game_ticks`（确定性）；`play_for` 快但不确定。
- RimBridgeServer 启动时会打开 RimWorld 私有的 `UltraSpeedBoost`，所以 Ultrafast 才会这么快。

---

## 7. 真实 UI 驱动

```js
const snap = await call('rimworld/get_ui_layout');
// snap.surfaces[] 每项: surfaceTargetId / kind / type / elementCount / actionableElementCount / elements[]
// elements[] 每项: targetId / kind(button|label|scroll_view…) / label / valueText / actionable /
//                  screenRect{x,y,width,height} / scroll / disabled / isChecked

await call('rimworld/click_ui_target', { targetId: e.targetId, timeoutMs: 5000 });
```

**反直觉的点（踩过）**：

- RimWorld 一个列表行在快照里是 **2 个 label + 1 个 button**（文字画两遍做描边），
  而 **button 自己的 `label` 是 `null`**。所以不能"找着文字就点"，要靠
  **`screenRect` 完全一致**把 label 和它的 button 配对。见 `src/ui.js` 的 `clickRowByLabel()`。
- 文字匹配**默认要精确**：`"800 points"` 会命中 `"1800 points"`，务必用 `exact: true`。
- 对话框层数**不可预测**。实测「空投袭击」是 **势力 → 点数 → 点地图 → 投放半径** 四步，
  而且点了地图还会再弹一次。写成**贪心循环**：有弹窗就按候选池挑一个匹配的选项点掉，
  没弹窗就补一次地图点击，直到点击后不再弹。参考 `scripts/raid-full.mjs`。
- `click_ui_target` 在**下一个真实绘制帧**激活，**不需要窗口有焦点** —— 这正是它能自动化的原因。
- 右键菜单：`open_context_menu({x,z})` 实测返回 0 项（菜单没留住）。要读菜单用
  `get_context_menu_options`，或用 `mode: 'debug'`。这块还不稳，用前先小范围验证。

---

## 8. 截图

- **截的是游戏渲染画面，不是桌面**。已用置顶洋红窗口覆盖验证：截图里一个洋红像素都没有。
- 分辨率 = 游戏窗口的渲染分辨率，由隔离 profile 的 **`Config/Prefs.xml`** 里的
  `screenWidth`/`screenHeight` 决定（**不是** `-screen-width` 命令行参数，RimWorld 会用 Prefs 覆盖它）。
- 换分辨率要**用显示器支持的模式**（如 4K 屏上用 2560x1440）。实测 1600x900 曾被忽略成 2539x1439。
- `screenshot_cell_rect({x,z,width,height,paddingCells})` 会自动移镜头 → 按格裁剪 → 还原镜头，
  返回里 `clipped: true`。适合做视觉断言。
- 想按 UI 元素裁剪就传 `clipTargetId`。

---

## 9. 存读档

```js
await call('rimworld/save_game', { saveName: 'xxx' });
await call('rimworld/list_saves', { compatibleOnly: false });
await call('rimworld/load_game_ready',
  { saveName: 'xxx', readiness: 'mapData', pauseIfNeeded: true, ignoreModCompatibility: true },
  { timeoutMs: 200000 });
```

`load_game_ready` 默认会校验存档里记录的 mod 是否都已启用；测试环境用
`ignoreModCompatibility: true` 放宽。

---

## 10. 排错流程

1. `node scripts/diag.mjs` —— 游戏活着吗？桥接响应吗？最后完成到哪一步？
2. 没响应 → 重启游戏实例（进程级重置），别重试。
3. 有响应但结果不对 → **回读状态确认**，不要相信 `success`。
4. 结果字段名不确定 → `node src/cli.js call <工具>` 打印完整 JSON，别猜。
5. 想看某个工具的精确参数 → `node src/cli.js describe <工具>`，或看 `runs/tools.json`。

---

## 11. 窗口与环境

- 隔离 profile 的画面设置在 `profiles/<name>/prefs.json`，启动时合并进 `Config/Prefs.xml`。
- 关键项：`fullscreen:false`（别占满屏）、`screenWidth/Height`（2K 屏建议 2560x1440）、
  `runInBackground:true`（**切屏后世界还要继续跑**，自动化必需）、
  `pauseOnError:false` / `openLogOnWarnings:false`（出错别弹窗卡住自动化）、
  `volumeMusic:0`、`langFolderName:"English"`（UI 文字可预测，按文字点控件才稳）。
- 只强制这几个键，你在游戏里调的其它设置会原样保留。
