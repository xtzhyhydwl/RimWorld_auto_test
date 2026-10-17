# Character Editor 能否被 AI agent 程序化驱动 —— 调研报告

- 调研时间：2026-10-03
- RimWorld 1.6.4871（`D:\SteamLibrary\steamapps\common\RimWorld`）
- 被调研对象：Character Editor（`void.charactereditor`），本地版本 `CharacterEditor.dll` **1.6.3.3**
- 桥接：RimBridgeServer **2.1.1**（`D:\SteamLibrary\steamapps\common\RimWorld\Mods\RimBridgeServer`）
- 证据分级：【本机实测】= 我在本机文件/DLL 元数据/IL 里直接读到的；【网上】= web 搜索；【推测】= 我的推断
- 原始产出物（本仓库内）：
  - `tmp/ce/types.txt` —— CE 全部 337 个类型的可见性 + 基类
  - `tmp/ce/members-full.txt` —— 全部类型的所有成员（含 get/set 可见性），7968 行
  - `tmp/ce/il-*.txt` / `tmp/ce/ilcall-*.txt` —— IL 调用图（我解析 call/callvirt token 得到）
  - `tmp/ce/asm-rbs*.txt` —— RimBridgeServer 各程序集的类型/成员
  - `tmp/ce/Dump-CETypes.ps1` / `Dump-Asm.ps1` / `Dump-IL.ps1` —— 可复现的抽取脚本

---

## 0. 一句话结论

**能，而且比预期好得多。** Character Editor 的数据层和 UI 层是分开的：它有一整套
`internal static` 的 `*Tool` 类（全部以显式 `Pawn` 参数操作，不依赖窗口），
外加 `PresetPawn.SavePawn(pawn, slot)` / `LoadPawn(...)` 这一对**"把整只 pawn 的状态序列化成一段文本 / 再从文本还原"**的接口。

推荐路径是**写 RimBridgeServer companion DLL，反射调用这些内部 API**，
而**不是**走 UI 自动化 —— 因为桥接**没有文字输入工具、也没有 UI 滑块拖拽工具**，UI 路线存在硬天花板。

本机有一个**必须先解决的阻塞项**：**没有任何 C# 编译器**（无 .NET SDK / Mono / MSBuild / Roslyn / NuGet 缓存）。
详见 §5.0。

---

## 1. 定位结果（本机实测）

### 1.1 主包

遍历 `D:\SteamLibrary\steamapps\workshop\content\294100\*\About\About.xml` 搜 `void.charactereditor`，命中 3 个：

| 工坊 ID | 路径 | 说明 |
| --- | --- | --- |
| **1874644848** | `...\294100\1874644848` | **Character Editor 本体** |
| 2985662865 | `...\294100\2985662865` | 汉化 `void.charactereditor.zhpatch`（cedaro）——**附 C# 源码** |
| 3028061958 | `...\294100\3028061958` | 汉化 `nb.charactereditor.chinese`（NB） |

**本体 `About.xml` 关键事实**【本机实测】：
- `<name>Character Editor</name>`，`<author>void</author>`，`<modVersion>1.6.1</modVersion>`
- `<packageId>void.charactereditor</packageId>`
- `<supportedVersions>`：1.0 / 1.1 / 1.2 / 1.3 / 1.4 / 1.5 / **1.6**
- **`<modDependencies>` 只有一项：`brrainz.harmony`（Harmony）** → **Harmony 是唯一硬依赖**
- `<loadAfter>` 里虽然列了 `UnlimitedHugs.HugsLib`，但**不在 modDependencies 里 → HugsLib 是可选**，不是必需
- 描述里写 "v1.6.3 build on 2025-07-21 for RimWorld v1.6.4523"

**`LoadFolders.xml`**【本机实测】：按版本加载 `/` + `v1.6`（1.6 下还有条件项 `IfModActive="automatic.gradienthair"` → `v1.6/Gradients`）。

**实际加载的 DLL**【本机实测】：

```
D:\SteamLibrary\steamapps\workshop\content\294100\1874644848\v1.6\Assemblies\CharacterEditor.dll
  1,170,944 bytes   Assembly Version = 1.6.3.3   PublicKeyToken = null（无强名）
```

（同级还有 v1.4 = 1,111,552 B、v1.5 = 1,016,832 B，未加载。）

**Defs**【本机实测】`Defs/CharEditor.xml`：
```xml
<JobDef>
  <defName>EnterZGrave</defName>
  <driverClass>CharacterEditor.JobDriver_EnterZGrave</driverClass>
</JobDef>
<DesignatorDropdownGroupDef><defName>Zombrella</defName></DesignatorDropdownGroupDef>
```
→ 这本身就泄露了 CE 的命名空间是 `CharacterEditor`。

### 1.2 汉化包 2985662865 —— 一个现成的"反射打 CE"先例【本机实测】

`...\2985662865\Source\CETrans\CETrans.cs`（全文 50 行）里：

```csharp
using CharacterEditor;
...
harm.Patch(AccessTools.Method("CharacterEditor.Label:LangCN"), postfix: ...);
harm.Patch(AccessTools.Method("CharacterEditor.CustomLabel:SetLabel"), prefix: ...);
CharacterEditor.CustomLabel.LoadLabelsFromFile(path);
```

**这是本次调研里价值第二高的发现**：证明第三方 mod 已经在用 **Harmony `AccessTools` 按字符串名字**打 CE 的方法，
而且 `CharacterEditor.Label` 在我读到的 1.6.3.3 里是 **internal** —— 即**反射打 internal 类型在本机是已验证可行的**。

它的 `.csproj`（`CETrans.csproj`）也是一份可直接抄的**编译模板**：
- `<TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion>`
- 引用：`0Harmony`（`...\294100\2009463077\Current\Assemblies\0Harmony.dll`）、
  `Assembly-CSharp`（`...\RimWorldWin64_Data\Managed\Assembly-CSharp.dll`）、
  **`CharacterEditor`（`...\294100\1874644848\v1.4\Assemblies\CharacterEditor.dll`，`<Private>False</Private>`）**

### 1.3 参考程序集在本机的位置【本机实测】

| 用途 | 路径 |
| --- | --- |
| 游戏主程序集 | `D:\SteamLibrary\steamapps\common\RimWorld\RimWorldWin64_Data\Managed\Assembly-CSharp.dll`（15,777,280 B） |
| Unity IMGUI | `...\Managed\UnityEngine.IMGUIModule.dll`（188,608 B） |
| Harmony 1.6 | `...\294100\2009463077\Current\Assemblies\0Harmony.dll`（2,456,064 B） |
| HugsLib 1.6（可选） | `...\294100\818773962\v1.6\Assemblies\HugsLib.dll` |
| CE | `...\294100\1874644848\v1.6\Assemblies\CharacterEditor.dll` |

> ⚠️ 注意：**游戏 `Managed` 目录下没有 `0Harmony.dll`**，它来自 Harmony 工坊 mod。

---

## 2. 架构：数据模型与 UI **是分离的**【本机实测】

### 2.1 怎么读出来的（可复现）

`dotnet --list-sdks` 为空（**本机没有 .NET SDK**），所以我没有用 `ildasm`/`ilspy`。
我用的是 **Windows PowerShell 5.1 的 `Assembly.LoadFrom` + `AppDomain.AssemblyResolve`**
（把解析路径指向 `RimWorldWin64_Data\Managed` 和工坊各 `Assemblies` 目录）：

```
ASSEMBLY: CharacterEditor, Version=1.6.3.3, Culture=neutral, PublicKeyToken=null
TOTAL TYPES (non-null): 337
LOADER ERRORS: 0            ← 全量读出，一个类型都没丢
```

脚本：`tmp/ce/Dump-CETypes.ps1`（成员）、`tmp/ce/Dump-Asm.ps1`（任意程序集）、`tmp/ce/Dump-IL.ps1`（IL 调用图）。

**踩过的坑（供复用）**：Windows PowerShell 5.1 按 **GBK/ANSI** 读 `.ps1`，
所以脚本里**不能出现中文路径字面量**（会被解码成乱码导致 `Access is denied`）。
我用 `$PSScriptRoot` 绕开。另外 `$Out` 与 `$out` 在 PowerShell 里是**同一个变量**（大小写不敏感），
我第一版因此把路径变量覆盖成了 List。

### 2.2 分层结构

| 层 | 类型（我实际读到的全名） | 可见性 | 说明 |
| --- | --- | --- | --- |
| 全局单例 | `CharacterEditor.CEditor` | **internal** | `internal static CEditor API { get; private set; }`；`internal Pawn Pawn { get; set; }` |
| 编辑器窗口 | `CharacterEditor.CEditor+EditorUI : Verse.Window` | nested **internal** | `public void DoWindowContents(Rect)`；`internal void Start(Pawn)` |
| 主页签入口 | `CharacterEditor.MainTabWindow_CharacterEditor : RimWorld.MainTabWindow` | **public** | **`public` 无参构造** + `public DoWindowContents(Rect)` |
| Tab 内容块 | `CEditor+EditorUI+BlockBio / BlockHealth / BlockPerson / BlockPawnList / BlockNeeds / BlockRecords / BlockSocial / BlockInventory / BlockLog / BlockInfo` | nested internal | 每个是一个页签 |
| 对话框 | 30+ 个 `CharacterEditor.Dialog*`，全部 `: Verse.Window` | 多为 internal | `DialogAddTrait` / `DialogAddHediff` / `DialogChangeRace` / `DialogColorPicker` / `DialogChoosePawn` / `DialogTemplate<T>` … |
| **数据工具层** | `CharacterEditor.*Tool`（约 40 个） | 大多 **internal static** | **全部接受显式 `Pawn` 参数**（见 §2.3） |
| 自绘控件层 | `SZWidgets` / `FLabel` / `Listing_X : Verse.Listing` / `SZContainers` | internal static | 只是 `Verse.Widgets.*` 的包装（见 §4.2） |
| 反射辅助 | `Reflect` / `ReflectionMembers` / `EnumTool` / `InjectedDefHasher` | internal | CE 自己也在用反射 |
| 序列化 | `PresetPawn` / `PresetLifeStage` / `PresetGene` / `PresetObject` / `FileIO` | internal | **见 §2.4，最重要** |
| 兼容层 | `CompatibilityTool`（**public**）、`AlienRaceTool`（public）、`RaceTool`（public） | public | |
| 场景/物品 | `ScenPart_*`、`GenStep_ScatterThings2`、`CharacterEditorCascet/Grave`、`JobDriver_EnterZGrave` | public | 非核心 |

**核心判断**【本机实测】：`CEditor` 是入口单例，`EditorUI` 只是它的一个视图。
**改数据完全不需要打开窗口。**

### 2.3 数据工具层 —— 这就是"程序化 API"【本机实测】

我 dump 出的真实签名（**没有一个是我编的**）：

```csharp
// CharacterEditor.SkillTool  ← 注意：这个类是 public，方法也是 public static
public static void  SetSkill(Pawn p, SkillDef skd, int level, Passion passion, float xpSince, float xpMidnight);
public static void  AddSkill(Pawn p, SkillDef skd, int level, Passion passion, float xpSince, float xpMidnight);
public static void  EnableAllSkills(Pawn p);
public static void  DisableSkillsFromList(Pawn p, List<...> l);
public static void  SkillDisable(Pawn p, SkillDef skill, BoolUnknown val);
public static void  SkillDisable(Pawn p, SkillRecord skillRecord, BoolUnknown val);
public static string GetAllSkillsAsSeparatedString(Pawn p);       // ← 整包读
public static void   SetSkillsFromSeparatedString(Pawn p, string s); // ← 整包写
public static List<...> ListOfDiabledWorkTagsByBackstory(Pawn p);

// CharacterEditor.TraitTool  ← internal static
internal static void   AddTrait(Pawn pawn, TraitDef traitDef, TraitDegreeData degreeData,
                                bool random, bool doChangeSkillValue, Trait oldTraitToReplace);
internal static void   RemoveTrait(Pawn pawn, Trait t);
internal static bool   HasTrait(Pawn pawn, TraitDef traitDef, TraitDegreeData degreeData);
internal static string GetAllTraitsAsSeparatedString(Pawn p);          // ← 整包读
internal static void   SetTraitsFromSeparatedString(Pawn p, string s); // ← 整包写
internal static TraitDegreeData GetDegreeDataOrFirst(TraitDef t, int degree);

// CharacterEditor.AgeTool  ← internal static
internal static void  SetAge(Pawn p, int age);
internal static void  SetChronoAge(Pawn p, int age);
internal static void  SetAgeTicks(Pawn p, long ageTicks);
internal static long  GetAgeTicks(Pawn p);
internal static long  GetChronoAgeTicks(Pawn p);

// CharacterEditor.SkinTool ← internal static
internal static void SetSkinColor(Pawn p, bool primary, Color color, bool ensureVisible);
internal static void SetFavColor(Pawn pawn, ColorDef col);
internal static void SetMelanin(Pawn pawn, float f);

// CharacterEditor.StyleTool ← internal static
internal static void SetBeard(Pawn p, BeardDef b);
internal static void SetFaceTattoo(Pawn p, TattooDef t);
internal static void SetBodyTattoo(Pawn p, TattooDef t);
```

同类还有：`NameTool` / `BodyTool` / `HairTool` / `HeadTool` / `GeneTool` / `HealthTool` /
`MindTool` / `IdeoTool` / `FactionTool` / `ThoughtTool` / `RelationTool` / `RecordTool` /
`BackstoryTool` / `ColorTool` / `PosTool` / `WeaponTool` / `ApparelTool` / `ThingTool` /
`PawnKindTool` / `PawnxTool` / `PlacingTool` / `SearchTool` / `WindowTool` / `FileIO`。

**可见性统计**【本机实测，见 `tmp/ce/types.txt`】：

| 项目 | 数量 |
| --- | --- |
| 类型总数 | **337**（其中嵌套类型 222） |
| **顶层类型** | **115** |
| **顶层 public** | **17** |
| **顶层 internal** | **98** |

17 个顶层 public 就是全部（我枚举出来了，一个不漏）：
`AlienRaceTool`、`BlockBio`、`CharacterEditorCascet`、`CharacterEditorGrave`、
`CharacterEditorMod`、`CompatibilityTool`、`CustomLabel`、`DialogPsychology`、
`GenStep_ScatterThings2`、`InjectedDefHasher`、`JobDriver_EnterZGrave`、
`MainTabWindow_CharacterEditor`、`RaceTool`、`ScenPart_ScatterThingsStyleAnywhere`、
`ScenPart_StartingThingStyle_Defined`、**`SkillTool`**、`Teleport_Character`

→ **其余 98 个顶层类型（含 `CEditor`、`CEditor+EditorUI`、绝大多数 `*Tool`、`PresetPawn`）全是 internal**，
**必须反射**（只有 `SkillTool` 等少数可以直接引用）。

### 2.4 ★ 最高价值发现：`PresetPawn` 就是"整只 pawn 的序列化接口"【本机实测】

```csharp
// CharacterEditor.PresetPawn
internal Pawn   LoadPawn(int slot, bool choosePlace, string custom);  // 从文本还原/生成一只 pawn
internal string SavePawn(Pawn p, int slot);                           // 把一只 pawn 序列化成字符串
public   bool   Load(int slot, string custom);                        // 从"槽位"载入
private  string Save(int slot);
private  bool   LoadString(string custom);
internal void   GeneratePawn(bool _setBodyParts, bool setApparel);
internal static void ClearAllSlots();
internal static void RescueOldPawns();
```

`SavePawn` 的 **IL 调用图**（我解析出来的，证明它是"全量序列化"）：

```
SavePawn:
  -> AbilityTool::GetEntropy / GetPsyAbilitiesAsSeparatedString / GetPsyfocus
  -> AgeTool::GetAgeTicks / GetChronoAgeTicks
  -> ApparelTool::GetAllApparelAsSeparatedString
  -> BackstoryTool::GetBackstrory
  -> BodyTool::GetBodyTypeDefName
  -> ColorTool::ColorHexString
  -> CompatibilityTool::GetPersonality / GetPsyche
  -> FacialTool::GetFacialAnimationParams
  -> FactionTool::GetPawnFactionAsSeparatedString
  -> ...（共 173 个不同调用目标）
```

`LoadPawn` 的 IL：
```
LoadPawn:
  -> Capturer::UpdatePawnGraphic
  -> CEditor::Get / get_API / get_Pawn        ← 依赖 CEditor.API 已初始化
  -> PawnxTool::AddOrCreateExistingPawn
  -> PresetPawn::GeneratePawn
  -> PresetPawn::Load
```

**序列化格式是自描述的文本 key/value**。
`PresetPawn+Param` 枚举有 **57 个成员**，名字带编号，我全部读到了：

```
P00_id            P01_pawnkinddef    P02_faction        P03_name
P04_ageticks      P05_chronoticks    P06_gender         P07_bodydefname
P08_headpath      P09_crowntype      P10_crown          P11_headaddons
P12_hairdefname   P13_gradientmask   P14_haircolor1     P15_haircolor2
P16_melanin       P17_skincolor1     P18_skincolor2     P19_eyecolor
P20_skills        P21_traits         P22_hediffs        P23_backstorychild
P24_backstoryadult P25_workpriorities P26_weaponlist    P27_apparellist
P28_itemlist      P29_records        p30_needs          P31_memories
P32_situationalmemories P33_relations P34_royaltitles   P35_beard
P36_facetattoo    P37_bodytattoo     P38_entropy        P39_psyfocus
P40_psyabilities  P41_favcolor       P42_ideoculture    P43_ideoname
P44_modids        P45_facialanimation P46_personality   P47_headtypedef
P48_xenotypedef   P49_customxenoname P50_endogene       P51_xenogene
P52_psyche        P53_eyecolor2      P54_mutantDef      P55_rotStage
P56_favColorDef
```

> **这就是用户想要的东西**：一次 `SavePawn` = 读全部可编辑状态；一次 `LoadPawn` = 写全部状态。
> 而且格式是文本，agent 甚至可以只改其中几个 `Pxx_` 字段再写回。

`FileIO` 提供文件读写：`WriteFile(string, byte[])` / `ReadFile(string)` / `GetDirFileList(...)`，
属性 `PATH_PAWNEX`（导入导出 pawn 的目录）。

---

## 3. 有没有现成的程序化接口？

### 3.1 本机：**没有面向外部的接口**【本机实测】

- CE 没有暴露任何 console 命令、公开 API 文档、或对外 XML/JSON 导入接口。
- `CEditor.API` 与 `*Tool` 类**是 CE 自己 UI 用的内部结构**，不是给第三方设计的
  （名字叫 API 但可见性是 `internal`，且没有任何启动时注册）。
- 唯一"接口"性质的东西就是 §2.4 的 `PresetPawn` + 90 个槽位的自定义 pawn 存档。

### 3.2 他人做过的自动化：**没有找到**【网上】

我跑了多轮 web 搜索（"Character Editor programmatic API" / "CEditor" / "void.charactereditor github"
/ "apply pawn changes without UI automation" 等），命中的全是 Steam 讨论区、Nexusmods、
Skymods 的**普通玩家反馈页**，没有任何 API 文档、开源仓库或自动化项目。

- <https://steamcommunity.com/sharedfiles/filedetails/?id=1874644848>
- <https://www.nexusmods.com/rimworld/mods/326>
- <https://catalogue.smods.ru/archives/43130>

**结论：没有现成接口，必须自己写。**
（说明：搜索是浅层的，不能 100% 排除冷门项目；但没有任何公开迹象。）

### 3.3 CE 是否开源：**没找到官方源码**【网上】

作者 void 只发 Steam 工坊，我没找到官方 GitHub。
→ 只能靠**反射 / 反编译**。**本报告的结论全部来自本机 DLL 的元数据与 IL，不是来自源码。**

---

## 4. 能不能靠 RimBridgeServer 的 UI 自动化驱动？

**能开窗、能点按钮、能看结构；但有硬天花板：不能打字、不能拖滑块。**

### 4.1 RimBridgeServer 是怎么"看见"UI 的【本机实测】

从 `RimBridgeServer.dll` 的类型表我读到了它的 Harmony patch 类名（节选，共 100+ 个 patch 类型）：

**IMGUI 原语（`UnityEngine.GUI`）**
`Gui_Button_String / Content / StringStyle / ContentStyle`、
`Gui_Label_*`（4 个重载）、`Gui_Toggle_*`（4 个）、`Gui_TextField` / `Gui_TextField_Limited`

**RimWorld 控件（`Verse.Widgets`）**
`Widgets_ButtonImage`、`Widgets_ButtonImageColored`、`Widgets_ButtonImageHoverColored`、
`Widgets_ButtonInvisible`、`Widgets_ButtonInvisibleDraggable`、`Widgets_ButtonText`、
`Widgets_ButtonTextColored`、`Widgets_Checkbox_Floats`、`Widgets_Checkbox_Vector`、
`Widgets_CheckboxLabeled`、`Widgets_Label_String`、`Widgets_Label_TaggedString`、
`Widgets_RadioButtonLabeled`、`Widgets_TextEntryLabeled`、`Widgets_TextField`、
`Widgets_TextField_Validated`、`Widgets_BeginGroup`、`Widgets_BeginScrollView`

**容器/其它**
`Listing_Begin/End/GetRect/Gap/GapLine`、`ListingStandard_*`（9 个）、
`Window_InnerWindowOnGUI`、`GizmoGridDrawer_DrawGizmoGrid`、`InspectPaneUtility_DoTabs`

元素模型 `RimBridgeServer.UiLayoutElementSnapshot`
【本机实测字段】：`TargetId / Kind / Source / Label / ValueText / Actionable / ClipCapable /
IsChecked / Disabled / Depth / ParentTargetId / Rect / ScreenRect / Scroll`

> 也就是说：**它不是靠 UGUI/Canvas，而是靠 patch IMGUI 绘制调用**。
> 这解释了为什么自绘 mod 也能被看见 —— 只要它调 `Verse.Widgets.*`。

### 4.2 CE 的自绘层最终落到哪【本机实测：IL 调用图】

我解析了 CE 全部 337 个类型的 IL，统计 **`SZWidgets` / `FLabel` / `Listing_X`** 里的调用目标：

| 被调用的（外部） | 次数 | 是否被 RimBridgeServer patch |
| --- | --- | --- |
| `Verse.Widgets::ButtonImage` | 52 | ✅ |
| `Verse.Widgets::Label` | 48 | ✅ |
| `Verse.Widgets::ButtonInvisible` | 28 | ✅ |
| `Verse.Widgets::ButtonText` | 16 | ✅ |
| `Verse.Widgets::TextField` | 11 | ✅ |
| `Verse.Widgets::BeginScrollView` / `EndScrollView` | 4 / 4 | ✅ |
| `Verse.Widgets::CheckboxLabeled` | 2 | ✅ |
| `Verse.Widgets::RadioButtonLabeled` | 1 | ✅ |
| `Verse.Widgets::TextEntryLabeled` | 1 | ✅ |
| `Verse.Listing::Begin/End/Gap/GapLine/GetRect` | 34+ | ✅ |
| **`Verse.Widgets::HorizontalSlider`** | **14** | ❌ **不在 patch 列表里** |
| `UnityEngine.GUI::DrawTexture` / `set_color` | 34 / 69 | ❌（非交互，无所谓） |
| `Verse.Widgets::DrawRectFast` / `DrawBoxSolid` / `DrawHighlight` | 若干 | ❌（纯绘制） |

**关键的具体对应关系**（解释你们手册里那个坑）：

```csharp
// CharacterEditor.SZWidgets.Label(Rect rect, string text, Action action, string tooltip)
//   IL:  -> SZWidgets::ButtonInvisible
//        -> Verse.Widgets::Label
```
→ CE 的文字控件 = **画一次 `Widgets.Label` + 在同一个 rect 上叠一个 `Widgets.ButtonInvisible`**。
两者都被 patch，且后者的 label 必然是 `null` ——
**这正是你们 `docs/RIMBRIDGE-技巧手册.md` §7 记录的"一个列表行 = 2 个 label + 1 个 label 为 null 的 button，
必须用 `screenRect` 完全一致来配对"的现象**。两边独立测得一致，互相印证。

同理：
```
SZWidgets.ButtonInvisibleMouseOver / ButtonInvisibleMouseOverVar / ButtonInvisibleVar
   -> Verse.Widgets::ButtonInvisible        ✅ 可点
SZWidgets.ButtonHighlight / ButtonImage / ButtonImageCol
   -> Verse.Widgets::ButtonImage            ✅ 可点
SZWidgets.IntField / FloatField / NumericIntBox / NumericFloatBox / NumericTextField
   -> Verse.Widgets::TextField              ✅ 可见（但见下方 4.3-②）
```

**结论**：CE **不是**"大量用 IMGUI 自绘到不可见"。它的交互控件绝大多数能被 `get_ui_layout` 看见，
`click_ui_target` 也能点到。**但下面三条把 UI 路线的上限锁死了。**

### 4.3 UI 路线的硬天花板

**① 滑块完全没有被 instrument**【本机实测】
`Verse.Widgets.HorizontalSlider` **不在** RimBridgeServer 的 patch 列表里（我把 patch 类名全列出来核对过）。
CE 有 14 处调用它（`SZWidgets.FloatSlider` / `IntSlider`）。
→ **滑块不会出现在 `get_ui_layout` 里，`click_ui_target` 点不到。**
而 CE 里技能值、需求、心情、颜色分量等大量数值都是滑块。

**② 桥接没有任何文字输入工具**【本机实测：`runs/tools.json` 全量 125 个工具】
我用正则筛 `key|type|text|input|mouse|drag|press`，**只有这些**：

```
rimworld/click_ui_target       按 targetId 激活控件
rimworld/click_screen_target   按 targetId 激活"屏幕目标"
rimworld/set_hover_target      悬停（支持 screenX/screenY，但不按下）
rimworld/scroll_ui_target      滚动 scroll_view
rimworld/press_accept          语义化"确认"
rimworld/press_cancel          语义化"取消"
rimworld/drag_cell             地图格 → 地图格的鼠标拖拽
```

**没有 `type_text` / `send_keys` / 键盘输入。**
而且 `drag_cell` 的官方描述原文是 *"Dispatch a literal live **map** mouse drag from one
**current-map cell** to another"* —— **只作用于地图格子，不能拖 UI 滑块**。

→ 即使 `Widgets.TextField` 被捕获成元素，**也没办法往里打字**；
→ **"把射击技能改成 20 / 年龄改成 25 / 改名字 / 改肤色"这类数值与字符串编辑，UI 路线做不到。**

**③ 唯一剩下的 UI 手段是"点 ± 步进按钮 N 次"**【本机实测，可行但很笨】
`SZWidgets.NumericIntBox` / `NumericFloatBox` 的 IL 里有 `Widgets::ButtonImage`（± 键）；
`LabelFloatFieldSlider` 的 ± 走 `SZWidgets.ButtonInvisibleMouseOver` → `Widgets::ButtonInvisible`（也可点）。
所以理论上可以"点 20 次 + 把技能从 0 加到 20"，但每次都要重新 snapshot、配对 rect，慢且脆。

**④ 开窗路径**【本机实测】
`CEditor+EditorUI` 的构造函数是 `private/internal`，而 `rimworld/open_window_by_type` 的官方描述明确要求
*"when the window exposes a **public parameterless constructor**"* → **这个工具打不开 CE 编辑器**。
可行的是：
- `CharacterEditor.MainTabWindow_CharacterEditor`（**public + public 无参构造**）→用
  `rimworld/list_main_tabs` + `rimworld/open_main_tab`，或 `rimworld/open_window_by_type({windowType:'CharacterEditor.MainTabWindow_CharacterEditor'})`
- 或反射 `CEditor.API.StartEditor(pawn)`

  > ⚠️ 【推测】`MainTabWindow` 通常需要 `def`（MainButtonDef）被赋值，直接 `CreateInstance` 可能 NRE。
  > **这条我没有实机验证，标为待验证**，见 §6 Step 1。

**⑤ targetId 稳定性未知**【推测】
IMGUI 的 rect 每帧重算，CE 编辑器窗口可缩放/滚动。桥接里有
`RimBridgeWorkbench.RegisteredElementFingerprint` 类型，说明它有指纹/去重机制，
但**跨帧稳定性我没有实测**。

### 4.4 UI 路线小结

| 操作 | UI 路线 |
| --- | --- |
| 打开 CE 编辑器 | ✅（走 main tab，待验证） |
| 切换页签 / 打开子对话框 | ✅ 大概率 |
| 点击按钮（随机、复制、移除…） | ✅ |
| 勾选 checkbox | ✅ |
| 在列表里选行 | ✅（用 screenRect 配对） |
| **设定具体数值（技能/年龄/需求）** | ❌ 滑块不可见 + 无文字输入 |
| **设定字符串（名字）** | ❌ 无文字输入 |
| **批量改多只 pawn** | ❌ 每个都要走一遍 UI |

→ **UI 路线只能做"导航 + 点按钮"，不足以实现"像用角色编辑器那样随意调整"的目标。**

---

## 5. 写 companion DLL 反射改数据 —— **推荐路径**

### 5.0 ⚠️ 先说阻塞项：本机**没有 C# 编译器**【本机实测】

| 检查项 | 结果 |
| --- | --- |
| `C:\Program Files\dotnet\dotnet.exe --list-sdks` | **空**（只有 runtime 5.0.17 / 6.0.21 / 6.0.36） |
| `C:\Program Files\dotnet\sdk\` | 目录不存在 |
| 游戏目录下 `csc.exe` / `mcs.exe` / `mono.exe` | **无** |
| `RimWorldWin64_Data\MonoBleedingEdge` | **不存在** |
| `C:\Program Files( x86)` 下 `MSBuild.exe` / `csc.exe` | **无** |
| `~\.nuget\packages\rimbridgeserver.sdk` | **不存在** |

→ **要写 companion DLL，必须先装编译器**。可选：
1. `winget install Microsoft.DotNet.SDK.8`（需网络）
   然后照 `CETrans.csproj` 用 `TargetFrameworkVersion v4.7.2`（需 `Microsoft.NETFramework.ReferenceAssemblies` 包，也需网络）
2. 装 Mono / 用 Roslyn `csc`（从 NuGet `Microsoft.Net.Compilers` 取，需网络）
3. 直接在用户主力机器上开发、只把编好的 DLL 拷进测试环境

> 这一条我**无法自行解决**（沙箱只允许写工作区，且审批被禁用），需要上层决定。

### 5.1 companion DLL 的注册机制【本机实测】

从 `RimBridgeServer.dll` 读到的类型/成员名：
```
RimBridgeServer.RimBridgeExtensionDiscovery
  BridgeToolsRoot { get; set; }
  DiscoverModBridgeTools(...)      // IAsyncEnumerable
  DiscoverCompanionCandidates(...) // IAsyncEnumerable
  LoadCompanionAssemblies(...)
  CompanionAssemblyCandidate / LoadedCompanionAssembly / CompanionRootKind
字符串常量： "RimBridgeTools"
```
→ **工坊 mod 侧的目录名是 `RimBridgeTools`**（mod 自己的 `BridgeToolsRoot`）。

**SDK 契约**【本机实测，`RimBridgeServer.Sdk.dll` 全部 35 个类型我都读了】：

```csharp
public sealed class ToolAttribute : Attribute {
    public ToolAttribute(string name);
    public string Name { get; }            // 只读
    public string Title { get; set; }
    public string Description { get; set; }
    public string[] Tags { get; set; }
    public bool RequiresAuth { get; set; }
    public string ResultDescription { get; set; }
}
public sealed class ToolParameterAttribute : Attribute { Description; Required; DefaultValue; }
public sealed class ToolResponseAttribute : Attribute {
    public ToolResponseAttribute(string name, string type, string description);
    public bool Always; public bool Nullable;
}

public static class RimBridge {              // 运行期环境
    public static IRimBridgeContext Current;
    public static IRimBridgeGameClock Game;  // StepTicksAsync / RunUntilAsync / NextFrameAsync ...
    public static IRimBridgeMainThread MainThread;   // ← 必须在主线程改游戏状态
    public static IRimBridgeToolClient Tools;        // ← companion 工具可以回调别的桥接工具
    public static bool IsReady;
}
public interface IRimBridgeMainThread {
    bool IsMainThread { get; }
    Task InvokeAsync(Action action, CancellationToken ct);
    Task<T> InvokeAsync<T>(Func<T> func, CancellationToken ct);
}
public interface IRimBridgeToolClient {
    Task<IRimBridgeToolCallResult> CallAsync(string idOrAlias, object args, RimBridgeToolCallOptions options, CancellationToken ct);
    bool Exists(string idOrAlias);
    RimBridgeToolDescriptor Get(string idOrAlias);
}
```

**附带彩蛋**：SDK 里有一整套**证据/断言 API**，正好对上用户的"闭环自动测试"目标：
```csharp
RimBridgeEvidence.CreateManifest(suite, runId)
RimBridgeEvidence.AreEqual / IsTrue / Fail / Pass / ToolSucceeded
RimBridgeEvidence.AllPassed(assertions)
RimBridgeEvidence.Complete(manifest)
// RimBridgeEvidenceManifest { assertions, captures, environment, logs, errors, success, ... }
```

**另一条注册通道**【本机实测，`RimBridgeServer.Extensions.Abstractions.dll`】：
```csharp
public interface IRimBridgeCapabilityProvider {
    string ProviderId { get; }
    IEnumerable<RimBridgeCapabilityRegistration> GetCapabilities();
}
public sealed class RimBridgeCapabilityRegistration {
    public CapabilityDescriptor Descriptor { get; }   // 只读
    public RimBridgeCapabilityHandler Handler { get; } // 只读
}
```

### 5.2 关键类型与方法的可见性【本机实测 —— 这是决策依据】

| 目标 | 可见性 | 能否 companion 里直接引用 | 反射可行性 |
| --- | --- | --- | --- |
| `CEditor`（类本身） | **internal** | ❌ | ✅ |
| `CEditor.API` | get **internal**，set private | ❌ | ✅ 读 |
| `CEditor.Pawn` | get **internal**，set **internal** | ❌ | ✅ **可读可写** |
| `CEditor.StartEditor(Pawn)` | **internal** | ❌ | ✅ |
| `CEditor.CloseEditor()` / `Has` / `Get` / `ListOf` / `GetO` / `GetI` / `GetCustom` / `SetCustom` / `Toggle` | **internal** | ❌ | ✅ |
| `CEditor.Initialize(Mod)` / `StartNewGame()` | internal static | ❌ | ✅ |
| **`SkillTool` + 它的方法** | **public + public static** | ✅ **可直接引用** | — |
| `AlienRaceTool` / `RaceTool` / `CompatibilityTool` / `CustomLabel` | **public** | ✅ | — |
| `TraitTool` / `AgeTool` / `SkinTool` / `StyleTool` / `GeneTool` / `HealthTool` / `NameTool` / … | **internal static** | ❌ | ✅ |
| `PresetPawn.SavePawn` / `LoadPawn` / `LoadString` | **internal**（`Load` 是 public） | 部分 | ✅ |
| `MainTabWindow_CharacterEditor` | **public + public ctor** | ✅ | — |
| `CEditor+EditorUI` | nested **internal**，ctor **private/internal** | ❌ | ✅ |

### 5.3 前置条件（**不需要先开窗**）

**这是最重要的判断，有 IL 证据**【本机实测】：

```
CEditor.StartEditor(Pawn):
    -> CEditor+ModData::StartEditor
CEditor+ModData.StartEditor(Pawn):
    -> CEditor+EditorUI::Start
```
→ `StartEditor` **只负责开窗**，不碰数据。
而所有 `*Tool.SetXxx(Pawn, ...)` 都是**纯静态、显式传 Pawn** 的方法。
→ **改数据不需要编辑器窗口处于打开状态。**

需要的前置只有：
1. **必须先有一只 `Pawn` 对象**。用桥接 `rimworld/list_colonists` 取 `pawnId`
   （形如 `Thing_Human802`），再在 DLL 里用 `Find.CurrentMap` / `PawnsFinder` 解析成 `Pawn`。
2. **`CEditor.API` 必须非 null**（在 `CEditor.Initialize(Mod)` 之后才被赋值）——
   只有用到 `CEditor.API.Pawn` / `LoadPawn` 这条捷径时才需要。
   **纯 `*Tool` 静态方法不依赖它，更稳。**
3. **必须在游戏主线程执行** → 用 `RimBridge.MainThread.InvokeAsync(...)`。

### 5.4 建议暴露的工具（agent 友好的形状）

设计原则：**优先用 CE 自己的"字符串进 / 字符串出"整包接口**，把内部复杂度留在 CE 里。

```
ce/get_pawn_state(pawnId)              -> { skills, traits, ageTicks, chronoageTicks,
                                            apparel, hediffs, backstories, ... }
      内部：反射调用 SkillTool.GetAllSkillsAsSeparatedString / TraitTool.GetAllTraitsAsSeparatedString
            / AgeTool.GetAgeTicks / ApparelTool.GetAllApparelAsSeparatedString ...
      或者更省事：PresetPawn.SavePawn(pawn, slot) -> 直接拿到 57 字段的整包文本

ce/set_skills(pawnId, skillsString)     -> 反射 SkillTool.SetSkillsFromSeparatedString(pawn, s)
ce/set_traits(pawnId, traitsString)     -> 反射 TraitTool.SetTraitsFromSeparatedString(pawn, s)
ce/set_age(pawnId, years)               -> 反射 AgeTool.SetAge / SetChronoAge
ce/clone_pawn(srcPawnId, destPawnId)    -> SavePawn(src) + LoadPawn/LoadString 到 dest
ce/open_editor(pawnId)                  -> 反射 CEditor.API.Pawn = pawn; StartEditor(pawn)
ce/probe()                              -> 自检：报告每个反射目标是否解析成功 + CE 版本
```

`ce/probe` 应该**第一个写**：只读、零风险、且能一次性证明"反射进 CE internal 类型"这条路通不通。

### 5.5 风险评估

| 风险 | 说明 | 缓解 |
| --- | --- | --- |
| **CE 升级改名/改签名** | 全部依赖字符串类型名/方法名。1.6.3.3 → 新版可能变。 | 启动时自检（`ce/probe`）；反射失败 → 工具返回 `success:false` + 明确错误，**不要抛异常**（抛异常会污染桥接日志） |
| **重载解析** | `AddTrait` 需要 `TraitDef` + `TraitDegreeData` + `oldTraitToReplace`，surface 上很简单，实际很绕 | **避开单点方法，用 `GetAll*AsSeparatedString` / `Set*FromSeparatedString` 整包接口** |
| **枚举类型** | `Passion`、`BoolUnknown`、`Gender`、`ColorDef` 等 | CE 自己也有 `EnumTool.GetAllEnumsOfType`；companion 里从 `Assembly-CSharp` 取枚举再 `Enum.Parse` |
| **改动被后续逻辑覆盖** | CE 自己 patch 了 `Pawn_AgeTracker` 的 `OnPreRecalcIndex`/`OnRecalcIndex` 与 gene 的 `OnPostAddGene`/`OnPostRemoveGene` | 改完 **推进若干 tick 再回读**（`step_game_ticks`） |
| **`success:true` 骗人** | 桥接的通病（你们手册已记录） | **永远回读**：`get_selection_semantics().selectedObjects[0].inspectString` 或直接读 `Pawn` 字段 |
| **`PresetPawn.LoadPawn` 的副作用** | IL 显示它会 `PawnxTool::AddOrCreateExistingPawn` + `Capturer::UpdatePawnGraphic`，**可能新建/改动 pawn** | 先在测试存档里用它，别在主力存档上试 |
| **`MessageTool.Show` 弹窗** | `LoadString` 失败时会 `MessageTool::Show`（走 UI） | 无头/自动化时留意会弹消息 |

---

## 6. 推荐路径 & 最小可行验证方案

### 6.1 路径排序

1. **【推荐】companion DLL + 反射**（`RimBridgeTools/` + `RimBridgeServer.Sdk`）
   —— 唯一能真正实现"随意调整所有人物的状态"的路。核心用 `PresetPawn` 做整包读/写 +
   `*Tool` 做精细改。
2. 【次选】**把 CE 当"生成器"，用原版 debug action 兜底**
   —— 你们仓库已经有 `Actions\Max Skill...\<skill>`、`Actions\Give Trait...\<trait>`、
   `Actions\Add Hediff...\<hediff>`、`Actions\Set Backstory...\*`、`Actions\Add Gene...\*` 等。
   **在只关心"技能/特质/健康"这几个维度时，这些原版动作已经覆盖了 CE 的一大半能力，零开发成本。**
   CE 的独有价值主要是：外貌（体型/头/头发/肤色/纹身/颜色）、
   **整只 pawn 的克隆与批量复制**、名字、派系、心灵能力、记录（records）。
3. 【不推荐】**UI 自动化** —— 只能导航/点按钮，改不了数值和字符串（§4.3）。

### 6.2 MVP：按"信息量/成本"排序，先做 Step 1

#### Step 1（半天，零 C# 代码，最高性价比）—— 用真实数据回答"UI 路线到底行不行"

**目的**：不是要"用 UI 改人物"，而是要**实测拿到 CE 编辑器在 `get_ui_layout` 里的真实样子**，
一次性确认 §4.2 的预测（label 是否可读、图标按钮是否 `label:null`、targetId 是否跨帧稳定）。

```js
// 0) 确认 ModsConfig 里有 void.charactereditor + brrainz.harmony
node scripts/modsconfig.mjs list
node scripts/modsconfig.mjs add void.charactereditor      // Harmony 需要一起在
// 1) 起会话 + 开调试殖民地
node src/cli.js start
await call('rimworld/start_debug_game_ready', { readiness: 'visual', pauseIfNeeded: true, timeoutMs: 300000 }, { timeoutMs: 330000 });
await call('rimbridge/wait_for_long_event_idle', { timeoutMs: 120000 });
// 2) 打开 CE 编辑器（待验证的三个候选，按顺序试）
const tabs = await call('rimworld/list_main_tabs');       // 找 Character Editor 那一项
await call('rimworld/open_main_tab', { /* tabId */ });
//   或： await call('rimworld/open_window_by_type', { windowType: 'CharacterEditor.MainTabWindow_CharacterEditor' });
// 3) 抓 UI 结构
const snap = await call('rimworld/get_ui_layout');
//   断言：存在 type 含 'CharacterEditor' 的 surface
//   统计：elementCount / actionableElementCount；
//   对 actionable 元素统计有多少 label 为 null
// 4) 跨帧稳定性：连续抓 3 次，比对同一元素的 targetId 是否一致
```

**判定标准**：
- 若 surface 存在且 `actionableElementCount > 20` → UI 路线"看得见"，但**依然受限于 §4.3 ①②**，
  所以结论不会变成"用 UI 做"；这一步的价值是**给 Step 2 提供 rect/label 参照**，并确认 main tab 能开。
- 若连 surface 都没有（例如 main tab 打不开、CE 编辑器不是 `Verse.Window`） → UI 路线直接出局，
  更加坚定走 DLL。

#### Step 2（关键判据，需要先解决编译器）—— **只读** companion 工具 `ce/probe`

写一个**只有读操作**的 companion DLL 工具，一次性证明"从游戏内反射进 CE internal 类型"可行：

```csharp
[Tool("ce/probe", Title = "Character Editor probe", Description = "Reflectively verify CharacterEditor internals are reachable")]
public static async Task<object> Probe(CancellationToken ct) => await RimBridge.MainThread.InvokeAsync(() =>
{
    var report = new Dictionary<string, object>();
    var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "CharacterEditor");
    report["ceAssembly"] = asm?.GetName().Version?.ToString();          // 期望 "1.6.3.3"
    var ced = asm?.GetType("CharacterEditor.CEditor");
    var api = ced?.GetProperty("API", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);
    report["CEditor.API"] = api != null;
    var skillTool = asm?.GetType("CharacterEditor.SkillTool");         // public，但同样走反射更统一
    report["SkillTool.GetAllSkillsAsSeparatedString"] = skillTool?.GetMethod("GetAllSkillsAsSeparatedString") != null;
    var traitTool = asm?.GetType("CharacterEditor.TraitTool");
    report["TraitTool.SetTraitsFromSeparatedString"] = traitTool?.GetMethod("SetTraitsFromSeparatedString") != null;
    var preset = asm?.GetType("CharacterEditor.PresetPawn");
    report["PresetPawn.SavePawn"] = preset?.GetMethod("SavePawn", BindingFlags.NonPublic|BindingFlags.Instance) != null;
    // 真正跑一次只读：拿第一只殖民者的技能串
    var pawn = Find.CurrentMap?.mapPawns?.FreeColonists?.FirstOrDefault();
    if (pawn != null && skillTool != null)
        report["sampleSkills"] = skillTool.GetMethod("GetAllSkillsAsSeparatedString")
                                        .Invoke(null, new object[]{ pawn });
    return report;
}, ct);
```

**判定标准**：
- `ceAssembly = 1.6.3.3` 且各项 `true` 且 `sampleSkills` 返回非空技能串
  → **反射路线通了**，剩下只是照着 §5.4 加写工具。
- 任何一项 `false` → CE 换了版本/命名，改字符串重试；不需要改架构。

**Step 2 是真正的"可行性判据"**，因为它同时验证了：类型解析、`internal` 反射、
`CEditor.API` 初始化、主线程调度、`Pawn` 对象获取。

#### Step 3（在 Step 2 通过之后）—— 加写工具 + 回读断言

先加 `ce/set_skills` 与 `ce/set_traits`（都是"整包字符串"接口，最不容易踩重载坑），
写完立刻用 `get_selection_semantics` 的 `inspectString` 或再调 `ce/get_pawn_state` 回读，
并 `step_game_ticks` 若干 tick 后再回读一次，确认没有被 CE 的 `RecalcIndex` 逻辑冲掉。

### 6.3 给上层的一句话行动建议

> **不要投入 UI 自动化。** 桥接没有文字输入、也没有 UI 滑块拖拽，CE 的核心编辑动作（数值/字符串）
> 走不通。**先花半天做 Step 1 把 CE 的 UI 结构抓下来存档**（为将来定位 rect 用），
> **同时并行解决 C# 编译器**（装 .NET SDK），然后直接做 Step 2 的只读 `ce/probe`。
> 顺带一提：**在编译器就绪之前，原版 debug action（`Max Skill...` / `Give Trait...` /
> `Add Hediff...`）已经能覆盖 CE 的一半能力**，可以先把测试基建跑起来。

---

## 附录 A：本次调研的复现命令

```powershell
# 1) 抓类型与成员（0 loader error）
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
& '<WS>\tmp\ce\Dump-CETypes.ps1'
# 2) 抓任意程序集的类型/成员（已对 RimBridgeServer 4 个 DLL 跑过）
& '<WS>\tmp\ce\Dump-Asm.ps1' -Asm '<...>\RimBridgeServer.dll' -Tag 'rbs'
# 3) 抓 IL 调用图（解析 call/callvirt token，证明 CE 控件落到 Verse.Widgets.*）
& '<WS>\tmp\ce\Dump-IL.ps1' -Asm '<...>\CharacterEditor.dll' -Tag 'ce-widgets' `
    -TypeFilter 'CharacterEditor.SZWidgets','CharacterEditor.FLabel','CharacterEditor.Listing_X'
```

> ⚠️ 每个新的 `pwsh` 进程都要重设 `ExecutionPolicy`（我踩过）。
> ⚠️ `.ps1` 里不要写中文路径字面量（PS 5.1 按 GBK 读脚本）。

## 附录 B：明确"没读到 / 没验证"的部分

- ❌ 我**没有反编译方法体逻辑**。我只读了**元数据（类型/成员/可见性）**和 **IL 的调用目标**。
  方法内部算法、常量、字符串拼接格式（如 `SavePawn` 产出的具体文本长相）**我没读到**。
- ❌ **没有实机运行**。游戏当前未启动（`node src/cli.js status` → `gameRunning:false`），
  所以 `get_ui_layout` 看到 CE 的真实元素、`MainTabWindow_CharacterEditor` 能否用
  `open_window_by_type` 打开、targetId 跨帧稳定性 —— **全部未验证**，已在 §4.3-④/⑤ 标注。
  > ⚠ 2026-10-13 更正：这里的 `gameRunning:false` **不是证据**。当时的 `cli.js status` 把
  > `listGameProcesses()` 的 `{known,processes,reason}` 对象当数组用（`obj.length > 0` 恒假），
  > 所以那个 `false` 是**恒假的假阴性**，与游戏是否在跑无关。已修成三态
  > `true|false|null`（null=判据不足）+ `runningState`/`runningEvidence`/桥接握手/`Get-Process` 兜底。
  > "未实机运行"这个结论本身在该文档写作时可能是对的，但**判据必须换成重新跑一次 status**。
- ❌ **没有验证 companion DLL 能真的被加载**。`RimBridgeTools` 目录的确切位置、
  DLL 命名/加载顺序/是否要求特定 TFM，我只从字符串和类型名推断，**没有实证**。
- ✅ **HugsLib 是否真的可选 —— 已验证**：我解析了 CE 全部 337 个类型的 IL 调用图
  （`tmp/ce/ilcall-ce-all.txt`，**5684 个不同的调用目标**），
  搜 `HugsLib` → **NONE**（一次都没调用）；搜 `Harmony` → 10 处（确实用了 Harmony）。
  结合 `About.xml` 里 HugsLib 只出现在 `loadAfter`、不在 `modDependencies`，
  **结论：CE 1.6.3.3 完全不依赖 HugsLib，HugsLib 纯属多余的历史 loadAfter 条目。**
  这对搭建最小测试环境是好消息：**只需 Harmony，不需要 HugsLib**。
- ❌ 网上**没有**找到 CE 的 API 文档或既有自动化项目。
- ⚠️ `CharacterEditor.dll` 的 `modVersion` 有两处不一致：`About.xml` 写 `1.6.1`，
  描述里写 `v1.6.3 build`，程序集版本是 `1.6.3.3`。以**程序集版本 1.6.3.3** 为准。
