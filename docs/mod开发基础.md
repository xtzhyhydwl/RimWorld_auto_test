# RimWorld mod 开发 · 环境与机制

写给「准备开始写 mod」的人（或 agent）。**与你要写什么 mod 无关**，只讲这个环境里已经就绪的东西、
以及 RimWorld modding 绕不开的通用机制。

> **证据标注**：标 ✅ 的是本机**实机验证过**的；标 📖 的是 RimWorld modding 的通用常识，
> 本会话没专门验证。别把 📖 当成实测结论。

仓库位置：`E:\2026.7以后的新工程\DSH\RimWorld_auto_test`（已是 git 仓库）

---

## 一、构建 C#：环境已就绪，不用装任何东西 ✅

### 编译器

本机**没有 .NET SDK**，但有 Visual Studio BuildTools 自带的 Roslyn：

```
C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe
```

够用，而且**不要**去装 .NET SDK（装完反而要处理 net472 目标框架的事）。

### 引用程序集：用游戏自己的

RimWorld 跑在 Unity 的 Mono 上，**引用游戏安装目录里的程序集**最保险，
比引用 NuGet 上的 net472 参考程序集更不容易出 ABI 问题：

```
D:\SteamLibrary\steamapps\common\RimWorld\RimWorldWin64_Data\Managed\
    mscorlib.dll
    System.dll
    System.Core.dll
    netstandard.dll
    UnityEngine.CoreModule.dll
    Assembly-CSharp.dll        ← Verse / RimWorld 命名空间都在这
    ...
```

### 一个能直接用的编译命令 ✅

`scripts/build-cebridge.mjs` 是现成的参考实现（我写 CE 伴生工具时用的），核心是：

```
csc.exe /nologo /target:library /nostdlib+ /langversion:7.3 /optimize+ /deterministic+
        /out:<你的.dll>
        /reference:<Managed>\mscorlib.dll
        /reference:<Managed>\System.dll
        /reference:<Managed>\System.Core.dll
        /reference:<Managed>\netstandard.dll
        /reference:<Managed>\UnityEngine.CoreModule.dll
        /reference:<Managed>\Assembly-CSharp.dll
        <你的.cs>
```

两个关键点：
- **`/nostdlib+`** + 显式引用游戏的 mscorlib —— 保证用游戏那套基础库
- **`/langversion:7.3`** —— 别用 C# 8+ 语法（游戏运行时是旧 Mono）

> ⚠ **在 DSH 沙箱里编译，不能用 `execFileSync`**（管道 stdio 会 EPERM）。
> 用仓库的 `src/proc.js` 里的 `capture()`，它把输出重定向到临时文件。`build-cebridge.mjs` 就是这么写的。

---

## 二、迭代循环：这是开发效率的全部

### 改 XML（Def / Patch）→ **热重载，亚秒级** ✅

RimWorld 内置 `Actions\Hot reload Defs`，实测改完 XML 执行它**立即生效**，
不用重启游戏（重启一次约 12 秒，还要重开殖民地）。

```bash
node src/cli.js call rimworld/execute_debug_action --args-file a.json
# a.json: {"path":"Actions\\Hot reload Defs"}
```

**实测流程**（`scripts/verify-4-hotreload.mjs` 是完整参考）：

```
改 Defs/ThingDefs.xml 里的 label → 执行 Hot reload Defs → 重新生成该物品 → 读 label
  v1 → v2，亚秒生效 ✅
```

### 改 C# → 必须重启

DLL 会被游戏进程锁住，编译前必须先停游戏：

```
1. 停游戏（否则 csc 报 "无法打开 xxx.dll 以进行写入 —— 用户映射区域打开的文件"）✅
2. 编译
3. 重启（约 12 秒）
```

> 你机器上装了 **Doorstop**（`Rimworld-Doorstop`，pardeike 出品），它提供 **IL 热重载**和
> **Mono 调试器**。但它的 mod-loading patch 会提前解析程序集特性，
> 已经**实测导致 RimBridgeServer 加载失败** ✅。
> 本仓库的测试启动器默认用 `--doorstop-enabled false` 关掉它来保证确定性。
> 如果你要用 IL 热重载 / 调试器，就自己用 Steam 正常启动。

### 部署：用目录联接免拷贝 ✅

RimWorld **只从游戏安装目录的 `Mods/` 和工坊目录加载 mod**，
**不扫描** `-savedatafolder` 指向的目录 ✅（实测：放进去的 mod 的 `activeCount` 不会增加）。

所以迭代时用**目录联接**（junction，Windows 原生，不需要管理员）：

```powershell
cmd /c mklink /J "D:\...\RimWorld\Mods\你的mod" "E:\...\你的mod源码目录"
```

改工作区 = 改游戏看到的文件，不用反复复制。
（RimBridgeServer 的伴生 DLL 同理：放 `<游戏目录>\BridgeTools\<名字>\<名字>.dll` ✅）

---

## 三、调试手段

### 1. Player.log —— 第一现场 ✅

- 位置由 `-logFile` 决定（本仓库的隔离实例在 `profiles/<name>/Player.log`）
- 原版默认在 `%USERPROFILE%\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Player.log`
- 通过桥接读更结构化：`rimbridge/list_logs`（可按等级过滤、有游标）✅

RimWorld 会把**报错堆栈连同 Harmony patch 链**一起写进去，够定位大部分问题。

### 2. Mono 调试器：可以真的下断点 ✅（你机器上有）

`doorstop_config.ini` 里 `[UnityMono] debug_enabled=true, debug_address=127.0.0.1:56000`。
用 dnSpyEx / Rider 连上去就能**在真实的 RimWorld 进程里下断点、看变量**。
这是原版游戏不提供的 —— 对写 Harmony patch 极有价值。

> 代价：开着 Doorstop 时某些 mod（含 RimBridgeServer）会加载失败，见上。

### 3. 把 mod 的内部状态暴露给工具 / agent ✅

RimBridgeServer 支持**伴生 DLL**：把你的程序集丢进 `<游戏目录>\BridgeTools\<名字>\`，
用 SDK 标注的静态方法会自动变成桥接工具。写个 DLL 就能：

```csharp
using RimBridgeServer.Sdk;

public sealed class MyModTools
{
    [Tool("mymod/state", Description = "读我的 mod 的内部状态")]
    public object State([ToolParameter(Description = "小人的 id")] string pawnId = null)
    {
        return new { success = true, /* 你 mod 的内部数据 */ };
    }
}
```

引用 `RimBridgeServer.Sdk.dll`（在 `Mods\RimBridgeServer\1.6\Assemblies\`）。
**这是自己 mod 的测试钩子 —— 不用改测试框架，直接把内部状态变成可读接口。**

`bridge/DSH.CEBridge/DSH.CEBridge.cs` 是完整可跑的范例（含反射调用第三方 mod 内部 API 的写法）✅

### 4. 反编译：读别人的 mod 怎么做的 📖

- `pardeike/DecompilerServer`（MCP 形态）
- dnSpyEx / ILSpy 桌面版
- 本会话验证过的替代方案：**用 `findstr` 直接搜 DLL 里的类型名/字符串** ✅
  （够用来确认「某个类/方法存不存在」，成本极低）

---

## 四、怎么"读"这个游戏

### Def 的真相不在磁盘 XML 里 ⚠

读原始 XML 会被骗：**没生效的 override、没应用的继承、运行时注入的 Def**。
要拿真相，两条路：

1. **运行时读**：同一个 Def 名，游戏里看到的才是合并后的结果
2. **`kearril/RimSearcher`** 的思路：游戏内 mod 把**已合并的** Def 拓扑导出成 SQLite

### debug action 树 = 全游戏的索引 ✅

原版开发者菜单有 **341 个顶层动作**，我用桥接完整爬下来了（`runs/debug-tree-latest.json`，约 4 万节点）。

它的用法不只是"执行动作"，还能当**索引**：

- **找 Def 名**：带 `...` 的动作会**按 Def 展开成叶子**。
  想知道有哪些 ThingDef/PawnKindDef/HediffDef/GeneDef/TraitDef，
  直接列 `Actions\Spawn thing...` / `Spawn Pawn...` / `Add Hediff...` 的叶子就行 ✅
- **试功能**：想验证一个 Def 长什么样，直接生成一个看 ✅
- **找事件**：`Actions\Do incident...` 列出所有 incident ✅

结构规律：

| 规律 | 含义 |
| --- | --- |
| 标签带 `...` | 参数化动作，按 Def 展开成一堆叶子 |
| 父节点 `BrowseOnly` | 只是菜单容器，**能执行的是叶子** |
| `execution.requiredTargetKind` | `map`=传 x,z；`pawn`=传 pawnId；空=直接调 |
| `T:` 前缀 | 工具型，需要在地图上画/点 |
| 标签 `...` 结尾执行后 | 可能弹二级对话框，**层数不可预测**，要循环处理 |

---

## 五、和写什么 mod 无关的通用规律

### 两种主要做法 📖

| 做法 | 适用 | 迭代速度 |
| --- | --- | --- |
| **Def XML**（+ Patch） | 加物品/种族/基因/事件/研究… | **亚秒热重载** ✅ |
| **C# + Harmony** | 改已有行为、加新逻辑 | 要重启（12 秒） |

能用 XML 表达的优先用 XML —— 迭代快一个数量级。

### 种族的身体结构是**按种族定义**的 ✅

这一点实测过，而且很多人会踩：

```
原版人类   bodyDef = Human            64 个部位
鼠族(HAR)  bodyDef = RK_Body_Ratkin   65 个部位，多一条 Tail [RK_BodyPart_Tail]
```

**HAR 种族可以自定义 BodyDef 和 BodyPartDef**。
所以任何和身体部位相关的逻辑（断肢、义体、贴图替换、装备槽位）
**都不能假定人类那套**，必须按 `pawn.RaceProps.body` 动态取。

「某个部位缺失」在游戏里是挂在 `BodyPartRecord` 上的 `Hediff_MissingPart`；
而且**父部位缺失时，子部位通过 `hediffSet.PartIsMissing()` 也算缺失** ✅
（截掉一条腿 → 腿 + 股骨 + 胫骨 + 脚 + 5 个脚趾，共 9 个部位都算缺失）。

### Mod 元数据 📖

- `About/About.xml`：`packageId`（**全局唯一，大小写敏感**）、`name`、`supportedVersions`、`modDependencies`、`loadAfter`
- `LoadFolders.xml`：按游戏版本指定加载哪些子目录（`<v1.6><li>1.6</li></v1.6>`）
- **packageId 大小写一定要和实际一致** —— 写错会让所有依赖你的 mod 报「前置未满足」
  （NewRatkinPlus 就踩了这个坑，详见 skill `rimworld-mod-compat`）✅

### 加载顺序 📖

```
Harmony → Core + DLC → 基础框架(XML Extensions/HugsLib) → 种族框架(HAR) → 内容 → 补丁 → 工具
```

你依赖的东西必须排在你前面。改顺序后**必须重启**才生效。
工具：`node scripts/modsconfig.mjs preset <baseline|ratkin>` / `set` / `copy-main`

---

## 六、现成可复用的东西

| 想要 | 用什么 |
| --- | --- |
| 启动一个隔离测试实例（不碰主力存档） | `node src/cli.js start`，配置在 `profiles/<name>/prefs.json` |
| 让游戏跑到副屏、不打扰你 | `prefs.json` 的 `"monitor": 2` ✅ |
| 改完 Def 立即验证 | `Actions\Hot reload Defs` + 回读断言 ✅ |
| 迭代时免拷贝部署 | 目录联接 ✅ |
| 把 mod 内部状态暴露出来 | RimBridgeServer 伴生 DLL ✅ |
| 查有哪些 Def | debug action 树的叶子列表 ✅ |
| 把游戏状态读成结构化数据 | 125 个原生桥接工具 + 自写的 `ce/*` ✅ |
| 全自动跑一遍冒烟 | `node src/cli.js smoke --attach` ✅ |
| 遇到 mod 不生效/红字 | skill `rimworld-mod-compat` |

---

## 七、几条别踩的坑（都实测过）✅

1. **`Prefs.xml` 在 `<存档目录>/Config/` 下**，不是存档目录根下。放错位置游戏完全不读，也不报错 ——
   表现是"分辨率设置没生效"。
2. **RimWorld 用 Prefs 里的分辨率覆盖 Unity 的 `-screen-width/-height`**。
   想控制窗口大小要改 Prefs。
3. **分辨率要用目标显示器支持的尺寸**。4K 屏上写 1600x900 曾被忽略成 2539x1439；
   1080p 副屏上写 2560x1440 会溢出到主屏。
4. **`tasklist` 在受限沙箱下返回 Access denied**，会被误读成"进程不存在"。
   判断进程存活用 PID + `process.kill(pid, 0)`。
5. **RimWorld 不扫描 `-savedatafolder` 里的 `Mods/`**。mod 必须放游戏目录或工坊目录。
6. **Windows 窗口有一圈 ~7px 隐形拖拽边框**（DWM）。用 `GetWindowRect` 判断"窗口是否在屏内"要给容差，
   否则会误判成溢出。
