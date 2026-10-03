using System;
using Verse;

namespace FoodControl.Game
{
    /// <summary>
    /// Mod 入口（`Verse.Mod` 子类）。
    ///
    /// **⚠ 类名必须叫 `ModBoot`**：测试侧的探针 `bridge/DSH.FoodProbe/DSH.FoodProbe.cs` 是按
    ///   `asm.GetType("FoodControl.Game.ModBoot")`（失败再按 `x.Name == "ModBoot"` 遍历）去找入口的，
    ///   并且要求入口上有一个**静态无参** `RunCycleNow()`。改类名会让测试钩子断掉。
    ///
    /// **它是怎么被造出来的（IL 确证）**：`Verse.LoadedModManager.CreateModClasses()` 会在每个已加载的
    /// 程序集里找 `Verse.Mod` 的子类，然后 `Activator.CreateInstance(type, modContentPack)`
    /// ⇒ 只要有个 `public X(ModContentPack) : base(content)` 的构造函数就会被自动实例化，
    /// 不需要在 About.xml 里声明什么。
    ///
    /// **它不负责"注册 GameComponent"** —— 那件事是游戏自己做的：`Game.FillComponents()` 的 IL 是
    /// `foreach (t in typeof(GameComponent).AllSubclassesNonAbstract()) Activator.CreateInstance(t, this)`，
    /// 而 `GenTypes.AllActiveAssemblies` 明确会 yield `LoadedModManager.RunningMods` 里每个 mod 的
    /// `assemblies.loadedAssemblies` —— 也就是**包含本 mod 的程序集**。
    /// 所以 <see cref="FoodControlGameComponent"/> 会被自动 new 出来并挂到 `Game.components` 上。
    ///
    /// 那这个类还留着干什么（三条，都跟测试有关）：
    ///   1. **静态钩子**：让 RimBridgeServer 伴生 DLL 有个稳定入口能拿到组件实例
    ///      （`Component` / `RunCycleNow()` / `StatusText()`），不用反射去猜类型名。
    ///   2. **保险绳**：`EnsureComponent()` 在自动发现万一失效时手动挂上（正常路径用不到）。
    ///   3. **身份标识**：About/packageId 与运行时实例对得上的地方。
    /// </summary>
    public sealed class ModBoot : Mod
    {
        public const string PackageId = "dsh.foodcontrol";

        /// <summary>静态钩子 —— 桥接/测试入口。</summary>
        public static ModBoot Instance;

        public ModBoot(ModContentPack content)
            : base(content)
        {
            Instance = this;

            // 注册内置的"需求域"（S2 起闭环由 NeedCoordinator 统一调度）。
            // ⚠ 注册是**幂等**的：游戏可能重新加载程序集，重复注册会被忽略，
            //   不会导致同一个域跑两遍。
            try
            {
                NeedCoordinator.RegisterBuiltIns();
            }
            catch (Exception e)
            {
                try { Log.Error("[FoodControl] 注册需求域失败（已吞掉）：" + e); } catch (Exception) { }
            }

            // 装上"意图出口"（日志 + JSONL 文件）。幂等。
            // ⚠ 放在这里而不是首次写意图时：出口要**从第一条意图起**就存在，
            //   否则"第一条意图去哪了"会变成一个查不清的问题。
            try
            {
                IntentIpc.InstallDefaults();
            }
            catch (Exception e)
            {
                try { Log.Error("[FoodControl] 装意图出口失败（已吞掉）：" + e); } catch (Exception) { }
            }
        }

        /// <summary>
        /// 拿当前的闭环组件实例（找不到返回 null）。
        /// 桥接伴生 DLL 的用法：`FoodControl.Game.ModBoot.Component` → `.ReadStateJson()` / `.TelemetryLocation`。
        /// </summary>
        public static FoodControlGameComponent Component
        {
            get
            {
                try
                {
                    return FoodControlGameComponent.Find();
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        /// <summary>
        /// **静态测试钩子**：立即跑一轮闭环（不等游戏日）。
        /// 探针 `foodprobe/run_cycle` 就是 `CallStatic(boot, "RunCycleNow")` 调进来的 ——
        /// 所以这里必须是 **public static、0 个参数**（实例上的同名方法不算）。
        /// </summary>
        public static string RunCycleNow()
        {
            try
            {
                FoodControlGameComponent c = FoodControlGameComponent.Find();
                if (c == null) c = FoodControlGameComponent.EnsureRegistered();
                if (c == null) return "没有 FoodControlGameComponent 实例（当前不在游戏里？）";
                return c.RunCycleNow();
            }
            catch (Exception e)
            {
                try { Log.Error("[FoodControl] ModBoot.RunCycleNow 异常：" + e); } catch (Exception) { }
                return "ModBoot.RunCycleNow 异常：" + e.Message;
            }
        }

        /// <summary>**静态测试钩子**：只读感知（不决策、不执行、不写遥测）。</summary>
        public static string ReadStateJson()
        {
            try
            {
                FoodControlGameComponent c = FoodControlGameComponent.Find();
                if (c == null) return "{\"error\":\"没有 FoodControlGameComponent 实例\"}";
                return c.ReadStateJson();
            }
            catch (Exception e)
            {
                return "{\"error\":\"" + e.Message.Replace("\"", "'") + "\"}";
            }
        }

        /// <summary>保险绳（见类注释第 2 条）。可以在桥接里调一次确保组件在。</summary>
        public static FoodControlGameComponent EnsureComponent()
        {
            try
            {
                return FoodControlGameComponent.EnsureRegistered();
            }
            catch (Exception e)
            {
                try { Log.Error("[FoodControl] EnsureComponent 失败：" + e); } catch (Exception) { }
                return null;
            }
        }

        /// <summary>一行状态，给桥接直接返回给人看。</summary>
        public static string StatusText()
        {
            try
            {
                FoodControlGameComponent c = Component;
                if (c == null) return "FoodControl: 组件不存在（当前没有进行中的游戏？）";
                return string.Format(
                    "FoodControl(packageId={0}) cycleCount={1} enabled={2} window={3}:00-{4}:00 | {5}",
                    PackageId, c.CycleCount, FoodControlGameComponent.Enabled,
                    FoodControlGameComponent.DecisionHourFrom, FoodControlGameComponent.DecisionHourTo,
                    c.TelemetryLocation);
            }
            catch (Exception e)
            {
                return "FoodControl: StatusText 异常 " + e.Message;
            }
        }
    }
}
