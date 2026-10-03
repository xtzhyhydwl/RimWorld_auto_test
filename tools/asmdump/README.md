# asmdump / IlDump — RimWorld 程序集符号确证工具

不是什么 mod，是**只读**的取证工具：直接从 `Assembly-CSharp.dll` 的**元数据**和 **IL** 里读出
「某个类/成员到底存不存在、签名是什么、默认值是多少、方法体怎么写的」，
用来替代反编译（本机没装 dnSpy / ILSpy / Cecil / DecompilerServer）。

- **AsmDump.cs**：反射列类型 / 字段 / 属性（含 get/set 各自可见性）/ 方法（完整参数与默认值）/
  枚举值 / `const` 字面量值。
- **IlDump.cs**：极简 IL 反汇编器（把 metadata token 解析成名字）+ `--usages` 全程序集扫描
  「某个字段在哪里被读写」。

两者都**不执行游戏代码**：只走反射取元数据、`GetMethodBody().GetILAsByteArray()`，
`const` 用 `GetRawConstantValue()`（不会触发静态构造函数）。

## 为什么必须用系统 .NET Framework 编译

**不要**用游戏目录里的 `mscorlib.dll` / `System.Core.dll` 当引用 —— 那是 Mono 版，
编出来的 exe 一跑就 `CLR exception 0xE0434352`（`Exception.ToString()` 都失败，看不到任何信息）。
用 `%windir%\Microsoft.NET\Framework64\v4.0.30319\` 下的程序集，exe 由 .NET Framework 4.x 跑。

```powershell
$FW  = "$env:windir\Microsoft.NET\Framework64\v4.0.30319"
$CSC = "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe"

& $CSC /nologo /target:exe /optimize+ /out:AsmDump.exe `
    /reference:$FW\mscorlib.dll /reference:$FW\System.dll /reference:$FW\System.Core.dll AsmDump.cs
& $CSC /nologo /target:exe /optimize+ /out:IlDump.exe  `
    /reference:$FW\mscorlib.dll /reference:$FW\System.dll /reference:$FW\System.Core.dll IlDump.cs
```

目标程序集是 net472 的 `Assembly-CSharp.dll`，由 `AssemblyResolve` 从 `Managed\` 目录解析依赖。

## 用法

```powershell
$M = "D:\SteamLibrary\steamapps\common\RimWorld\RimWorldWin64_Data\Managed"

# 类型成员清单；类型过滤器支持 | 分隔，"=全名" 为精确匹配，否则子串匹配
# 末位可选 "np" = 连非 public 成员一起列
.\AsmDump.exe "$M\Assembly-CSharp.dll" $M "=RimWorld.Plant" "*"
.\AsmDump.exe "$M\Assembly-CSharp.dll" $M "=Verse.Map" "Grid" np

# 单方法 IL（成员过滤器同样支持 |）
.\IlDump.exe "$M\Assembly-CSharp.dll" $M "RimWorld.Plant" "GrowthPerTick|get_GrowthRate"

# 全程序集扫某字段的读写点
.\IlDump.exe --usages "$M\Assembly-CSharp.dll" $M "harvestAfterGrowth" "RimWorld.PlantProperties"
```

## 已知坑

1. **`PropertyInfo` 的可见性只反映访问器**。`PROP float X { protected get; }` 表示属性本身可能
   是 public、但 getter 是 protected —— 所以**外部调不到**。早期版本的工具没打印访问器可见性，
   导致把 `Plant.GrowthPerTick` 误判成可用；`ApiProbe.cs` 的编译实验正是靠 `CS0122` 抓出来的。
   **结论：任何"能不能用"的判断都要过一遍编译（见下）。**
2. 输出**必须显式 `Console.Out.Flush()`**（工具里每个类型后都 flush）：某个类型加载失败抛异常时，
   未 flush 的 stdout 缓冲会整段丢失，表现为"什么都没输出"。
3. 少数类型在本宿主里 `TypeLoadException`（如 `Verse.GlowGrid`，Mono 元数据与 .NET Framework
   对接口方法的抽象位解释不同），工具会打印 `!! TYPE DUMP FAILED` 而不是崩掉。
4. `ReflectionTypeLoadException` 是常态（16130 个类型可用），不是失败。

## ApiProbe.cs — 编译验证

把「你以为存在的符号」写成真代码编一遍，是唯一能证明签名正确的手段：

```powershell
$M = "D:\SteamLibrary\steamapps\common\RimWorld\RimWorldWin64_Data\Managed"
$CSC = "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe"
& $CSC /nologo /target:library /nostdlib+ /langversion:7.3 /optimize+ /deterministic+ `
    /out:DSH.ApiProbe.dll `
    /reference:$M\mscorlib.dll /reference:$M\System.dll /reference:$M\System.Core.dll `
    /reference:$M\netstandard.dll /reference:$M\UnityEngine.CoreModule.dll `
    /reference:$M\Assembly-CSharp.dll ApiProbe.cs
```

`ApiProbe.cs` 里覆盖了食物/营养、作物生长、种植区、土壤判定、工作优先级、技能六大块，
编译通过 = 这些符号名与签名（含 `out`/`ref`、扩展方法、可见性）都对得上。
它**不是** mod，只要求能编译；产物写在工作区，不碰游戏目录。
