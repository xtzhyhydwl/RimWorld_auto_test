# 取证 · RimWorld 1.6 里 `Verse.GameComponent` 什么时候、怎样注册才可靠

> 对象：`D:\SteamLibrary\steamapps\common\RimWorld\RimWorldWin64_Data\Managed\Assembly-CSharp.dll`（**1.6.9676.17735**）。
> 手段：仓库只读工具 `tools/asmdump/{AsmDump,IlDump,RefDump}.exe`。**未写脚本、未启动游戏、未修改任何现有文件**。
> 标注：**实测** = 元数据/IL 直接读到；**推测** = 由 IL 推出、无运行时观察；**未确证** = 工具答不了。

## 1. 结论速览

1. ✔ **实测**：**1.6 不需要手工注册**。`Game..ctor` 末尾自己调 `FillComponents()`：`GenTypes.AllSubclassesNonAbstract(typeof(GameComponent))` → 逐个 `GetComponent(type)==null` 就 `Activator.CreateInstance(type, new object[]{ this })` 后 `Add`。**只要是"非抽象 GameComponent 子类 + 有 `ctor(Game)`"，它就会自动出现在每个 Game 里**。
2. ✔ **实测**：读档路径 = `Game.LoadGame` → `ExposeSmallComponents` 里 `Scribe_Collections.Look(ref components, "components", LookMode.Deep, new object[]{ this })`；加载完 `if (Scribe.mode == LoadingVars)` 再跑一次 **`FillComponents()`** → 老存档里没有的组件类型被补一个默认实例。**不会重复**：Scribe 加载列表时把整个 `ref list` 换成 `new List<T>(childCount)`（IL 里是 `ldnull`/`newobj List` + `stind.ref`），加上 `FillComponents` 开头 `components.RemoveAll(c => c == null)` 与 `GetComponent(type)==null` 判重。
3. ✔ **实测**：1.6 **已无** `GameComponentUtility.InitGameComponents / LoadGameComponents`（该类只剩 6 个静态方法，全程序集也扫不到这两个名字）。职责合并进私有方法 `Game.FillComponents()`，**唯一调用者**：`Game..ctor`（IL_0179）与 `Game.ExposeSmallComponents`（IL_02B5，仅 LoadingVars）。
4. ✔ **实测（IL）/ 推测（运行时值）**：**`Mod` 构造函数里 `Current.Game` 一定是 null**。`Root.Start` → `PlayDataLoader.LoadAllPlayData` → `DoPlayLoad` → `LoadedModManager.LoadAllActiveMods` → `InitializeMods` / `LoadModContent` / `CreateModClasses`（`Activator.CreateInstance(type, { modContentPack })`）；全程序集 `newobj Verse.Game::.ctor` 只有 6 处，**没有一处在 mod 加载链上**；`Verse.Current` 没有 `.cctor`（`gameInt` 只能是默认 null）。⇒ 方案 (a) 的"Mod ctor 里注册"分支永远走不到。
5. ✔ **实测**：`Game.ExposeData` **只管存**（`Scribe.mode==LoadingVars` 直接 `LogError("You must use special LoadData method to load Game.")` 返回）；`Game.InitNewGame` 全方法体无 Scribe / SaveGame 调用 ⇒ **新开局时 `GameComponent.ExposeData()` 在 `StartedNewGame` 之前不会被调用**，第一次调用是第一次存盘（`AutosaverTick` / 手动存 / 永久死亡下 `NamePlayerFactionDialogUtility.Named`）。所以"自铸 GUID 写在 ExposeData 里"**不在** `StartedNewGame` 的保证范围内（与既有 `取证-存档标识.md` 的写法一致：在回调里就地补铸）。

## 2. 证据（方法名 + IL 关键片段 + 命令）

命令统一前缀：`$M="D:\SteamLibrary\steamapps\common\RimWorld\RimWorldWin64_Data\Managed"`、`$T="E:\2026.7以后的新工程\DSH\RimWorld_auto_test\tools\asmdump"`、`$A="$M\Assembly-CSharp.dll"`。

### 2.1 `components` 从哪来：ctor 建空表 → 立刻被 FillComponents 填满（实测）

`IlDump $A $M "Verse.Game" ".ctor"`（末尾两行）：
```
IL_0013: newobj List`1<Verse.GameComponent>::.ctor   // IL_0018 stfld Verse.Game::components（空表）
...
IL_0178: ldarg.0 ; IL_0179: call Verse.Game::FillComponents ; IL_017E: ret
```
`IlDump $A $M "Verse.Game" "FillComponents"`：
```
IL_0001: ldfld components ; IL_0025: callvirt List`1::RemoveAll   // 谓词 <FillComponents>b__79_0 = "component == null"
IL_002B: ldtoken Verse.GameComponent ; IL_0035: call Verse.GenTypes::AllSubclassesNonAbstract
IL_004C: call Verse.Game::GetComponent(type) ; IL_0051: brtrue.s  // 已有该类型就跳过
IL_0054: ldc.i4.1 / newarr object / ldarg.0(即 Game) / stelem.ref
IL_005E: call System.Activator::CreateInstance   // ← 实参是 new object[]{ this }，等价于要求 ctor(Game)
IL_0068-0070: stloc; ldfld components; callvirt List`1::Add
IL_0077: catch → Log::Error("Could not instantiate a GameComponent of type ...")
```
对照：`AsmDump $A $M "=RimWorld.GameComponent_Bossgroup" "*" np` → `CTOR public GameComponent_Bossgroup(Verse.Game game)`（原版 4 个子类同形）。基类 `Verse.GameComponent` 只有 `CTOR protected GameComponent()`。

### 2.2 读档：整个列表被存档节点替换，再补齐缺失类型（实测）

`IlDump $A $M "Verse.Game" "ExposeSmallComponents"`：
```
IL_0292: ldflda components ; IL_0297: ldstr "components" ; IL_029C: ldc.i4.2 (LookMode.Deep)
IL_029D: ldc.i4.1 ; newarr object ; ldarg.0 ; stelem.ref          // ctorArgs = { game }
IL_02A7: call Verse.Scribe_Collections::Look
IL_02AC: ldsfld Scribe::mode ; IL_02B1: ldc.i4.2 ; IL_02B2: bne.un.s → 非 LoadingVars 跳过
IL_02B5: call Verse.Game::FillComponents                           // 读档后补注册（老档/新装 mod）
```
`AsmDump $A $M "=Verse.LookMode|=Verse.LoadSaveMode" "*" np` → `Deep = 2`；`LoadSaveMode` = `Inactive=0, Saving=1, LoadingVars=2, ResolvingCrossRefs=3, PostLoadInit=4`。
`IlDump $A $M "Verse.Scribe_Collections" "Look"`（列表重载）加载分支：`ldarg.0; ldnull; stind.ref`（找不到节点 → 列表清空）与 `ldarg.0; ...ChildNodes.Count; newobj List`1::.ctor(int); stind.ref`（找到节点 → **换新表**，元素经 `Scribe_Deep.Look` 用 ctorArgs 重建）。
`IlDump $A $M "Verse.ScribeExtractor" "CreateInstance"`：`Activator.CreateInstance(type, BindingFlags=52, null, ctorArgs, null)` ⇒ 读档时同样**按 `ctor(Game)` 找构造器**（public 或 non-public 均可）。`SaveableFromNode` 用 `node["Class"]` 解析类型，抽象类会 `LogError "Can't load abstract class "`。

### 2.3 谁碰 `components` 这个字段（实测，`IlDump --usages $A $M "components" "Verse.Game"`，17 命中）

| 位置 | 动作 |
| --- | --- |
| `Game..ctor` | `stfld`（唯一建表处） |
| `Game.FillComponents` | `RemoveAll` + `Add`（**原版唯一的加组件入口**） |
| `Game.ExposeSmallComponents` | `ldflda` → `Scribe_Collections.Look` |
| `Game.GetComponent<T>()` / `GetComponent(Type)` | 线性扫描 + `isinst` / `Type::IsInstanceOfType` |
| `Game.DebugString` | 只读 |
| `GameComponentUtility` 6 个方法 | 只读遍历（Tick/Update/OnGUI/FinalizeInit/LoadedGame/StartedNewGame） |

⇒ 除 `FillComponents` 外，**原版没有任何代码往 `components` 里加东西**；mod 若自己 `Add` 也不会被原版清掉（保存时整表落盘）。

### 2.4 时机与调用者（实测）

`RefDump $A $M "=Verse.GameComponentUtility" "*"`（HITS 7）：
`Game::InitNewGame[IL_01FD]→StartedNewGame`、`Game::LoadGame[IL_02AE]→LoadedGame`、`Game::FinalizeInit[IL_0015]→FinalizeInit`、`TickManager::DoSingleTick[IL_0198]→GameComponentTick`、`Game::UpdatePlay[IL_006B]`/`Game::UpdateEntry[IL_0000]`→`GameComponentUpdate`、`UIRoot::UIRootOnGUI[IL_00A0]→GameComponentOnGUI`。
`RefDump $A $M "=Verse.Game" "FillComponents"` → `Game::.ctor[IL_0179]`、`Game::ExposeSmallComponents[IL_02B5]`（其余 5 个是 `isinst Verse.Game` 的假阳性）。
`IlDump $A $M "Verse.Game" "LoadGame"`（读档时序，节选）：`IL_0033 ExposeSmallComponents` → `IL_004C "world"` → `IL_00A7 World.FinalizeInit` → `"maps"` → `IL_0279 Game.FinalizeInit`（内含 `GameComponentUtility.FinalizeInit` + `ProgramState=Playing`）→ **`IL_02AE GameComponentUtility.LoadedGame`** → `BackCompatibility.PostLoadSavegame`。
`IlDump $A $M "Verse.Game" "InitNewGame"`（尾巴）：`IL_0194 Game.FinalizeInit` → `IL_01E3 Scenario.PostGameStart` → `IL_01F3 history.FinalizeInit` → **`IL_01FD StartedNewGame`** → `IL_0204 initData=null`；**全程无 Scribe/SaveGame**。
`IlDump $A $M "Verse.Game" "ExposeData"`：`IL_0005 mode==2(LoadingVars) → LogError + ret`；否则 `ExposeSmallComponents` → `world` → `maps`。

### 2.5 `Mod` ctor 时 `Current.Game` 为 null（实测 IL）

`RefDump $A $M "=Verse.Game" ".ctor"` 的 `newobj Verse.Game::.ctor`：`SavedGameLoaderNow::LoadGameFromSaveFileNow[IL_005B,IL_0097]`、`GameDataSaveLoader+<>c__DisplayClass30_0::<LoadGame>g<PreLoadAct>[IL_0005]`、`Root_Play::SetupForQuickTestPlay[IL_000B]`、`MainMenuDrawer::InitLearnToPlay[IL_0005]`、`Page_SelectScenario::BeginScenarioConfiguration[IL_0005]`。
`RefDump $A $M "=Verse.PlayDataLoader" "LoadAllPlayData|DoPlayLoad"` → `Root+<>c::<Start>b__10_1[IL_0001]`、`LanguageDatabase+<>c::<SelectLanguage>b__6_0`。
`RefDump $A $M "=Verse.LoadedModManager" "InitializeMods|CreateModClasses|LoadModContent|LoadAllActiveMods"` → 只在 `PlayDataLoader::DoPlayLoad[IL_0027]` 与 `HotReloadDefs`。
`IlDump $A $M "Verse.LoadedModManager" "CreateModClasses"` → `IL_000A GenTypes::InstantiableDescendantsAndSelf(typeof(Verse.Mod))` → `IL_00A0 Activator.CreateInstance(type, new object[]{ modContentPack })`（`ContainsKey` 已存在则跳过）。
`IlDump $A $M "Verse.Current" "cctor"` → 输出只有类型头，**无 .cctor** ⇒ `gameInt` 无静态初始化。

## 3. 推荐做法（一条：**不注册，靠 `ctor(Game)` 自动被 `FillComponents` 收养**）

1. 写类（**必须非抽象**，否则读档时原版报 `Can't load abstract class`）：
   - `public sealed class MyComp : Verse.GameComponent`
   - **必须有 `public MyComp(Verse.Game game)`**（`Activator.CreateInstance(type, new object[]{game})` 与读档 `ScribeExtractor.CreateInstance(type, {game})` 都按这一个签名找；只写无参 ctor ⇒ 每次 new Game / 读档都会 `LogError "Could not instantiate a GameComponent of type ..."` 且组件丢失）。
   - `public override void ExposeData()` 里用 `Scribe_Values.Look(ref id, "myCompId")` 等把状态落盘（`ExposeData` 只在存/读时跑）。
   - `public override void LoadedGame()` / `StartedNewGame()`：**就地补铸**（`if (id.NullOrEmpty()) id = Guid.NewGuid().ToString("N");`）+ 依赖 world/map 的初始化（`LoadedGame` 时 world/maps 已就绪，见 §2.4）。`FinalizeInit()` 两条路径都会跑，放它可以省一次分支，但那时 `LoadedGame` 还没跑，别在这里读"LoadedGame 才有的东西"。
   - 每帧/每 tick 入口直接用 `GameComponentTick()`（每逢 `DoSingleTick`）、`GameComponentUpdate()`（`Root_Play.Update` 每帧）、`GameComponentOnGUI()`。
2. **不要**在 `Mod` ctor 里 `Current.Game.components.Add(...)`（`Current.Game` 为 null），**不要**为此写 Harmony patch，**也不要**去找 XML/Def 注入（原版没有 `GameComponentDef` 之类的 Def 类型，`Anomaly` 等 4 个子类都是硬编码类）。
3. 唯一值得保留的"保险"是读档后的幂等自检，且放在 `LoadedGame()` 里：
   `if (Find.Game.GetComponent<MyComp>() == null) Find.Game.components.Add(new MyComp(Find.Game));`
   —— 正常情况**永远不触发**（`FillComponents` 已补过）；它只兜住"类被条件编译/加载顺序搞坏"这类边缘情况，代价是几行死代码。**不要**用 `components.Add` 代替 `ctor(Game)`。

## 4. 未确证 / 需开游戏才能答

1. **未确证**：`GameComponent.ExposeData / LoadedGame / StartedNewGame / GameComponentTick` 是否为 `virtual`（`AsmDump` 不打印方法 flags，`IlDump` 只打印签名）。**实测**只能证明：基类这 7 个方法体全是 `IL_0000: ret`（空实现）、`GameComponentUtility` 用 `callvirt` 调它们、`Scribe_Deep` 保存时 `isinst Verse.IExposable` 再 `callvirt IExposable::ExposeData`。⇒ `public override ...` 能不能编译需一次编译验证（**推测**：能，原版子类都是裸 `public void ExposeData()` 且要经接口派发）。
2. **未确证**：`GenTypes.AllSubclassesNonAbstract` 是否包含 **mod 程序集**里的类型（**推测**：包含——它枚举已加载程序集，且 `InitializeMods` 每加一个 mod 就 `GenTypes::ClearCache`，见其 IL_00D9）。开游戏后 `Find.Game.GetComponent<MyComp>()` 非 null 即可定论。
3. **未确证（运行时值）**：`Mod` ctor 里 `Current.Game == null` 是 IL 推断（§2.5），没真跑。一行 `Log.Message((Current.Game == null).ToString())` 可证实。
4. **未确证**：新开局"第一次存盘"到底在哪一刻（`AutosaverTick` 间隔 / 永久死亡 `NamePlayerFactionDialogUtility.Named` / 手动存）——**实测**只有 `GameDataSaveLoader.SaveGame` 的 4 个调用点（`Dialog_SaveFileList_Save`、`MainMenuDrawer` 2 处、`Autosaver.DoAutosave`）。若要"开局即有 GUID"，必须在 `StartedNewGame()` 里铸，不能等 `ExposeData`。
5. **未确证**：`GameComponentTick` 是否只在主线程被调（`TickManager.DoSingleTick` 在 `TickManagerUpdate` 与 `TimeControls` 里同步调用，**推测**主线程）；若在后台线程读 UI/`Find` 仍有风险。
6. **未实施**：按要求未启动游戏、未写探测脚本 ⇒ 以上"运行时"结论均为 IL 推断。
