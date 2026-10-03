using System;
using Verse;

namespace ColonySnapshots
{
    /// <summary>
    /// **殖民地快照录制器 —— mod 入口。**
    ///
    /// 这个类只做一件事：往日志里打一行"我加载了"（含输出目录与采样间隔），
    /// 让人一眼能在 Player.log 里确认本 mod 被加载、以及快照写到哪。
    ///
    /// **它是怎么被造出来的（IL 确证）**：`Verse.LoadedModManager.CreateModClasses()`
    /// 会在每个已加载的程序集里找 `Verse.Mod` 的子类，然后
    /// `Activator.CreateInstance(type, modContentPack)` ⇒ 只要有个
    /// `public X(ModContentPack) : base(content)` 的构造函数就会被自动实例化，
    /// 不需要在 About.xml 里声明什么。
    ///
    /// **它不负责"注册 GameComponent"** —— 那件事是游戏自己做的：
    /// `Game.FillComponents()` 的 IL 是
    /// `foreach (t in typeof(GameComponent).AllSubclassesNonAbstract()) Activator.CreateInstance(t, this)`，
    /// 而 `GenTypes.AllActiveAssemblies` 会包含 `LoadedModManager.RunningMods` 里每个 mod
    /// 的 `assemblies.loadedAssemblies` —— 也就是本 mod 的程序集。
    /// 所以 <see cref="ColonyRecorder"/> 会被自动 new 出来并挂到 `Game.components` 上。
    ///
    /// ⚠ 本 mod 是**只读**的：只是录，不改地图、不做任何决策；
    ///   构造函数里所有可能抛异常的调用都被 try/catch 吞掉（mod 绝不能让游戏崩）。
    /// </summary>
    public sealed class RecorderMod : Mod
    {
        /// <summary>About.xml 里声明的 packageId</summary>
        public const string PackageId = "dsh.colonyrecorder";

        public RecorderMod(ModContentPack content)
            : base(content)
        {
            // ★ 入口绝不能让游戏崩 —— 整段吞异常
            try
            {
                string dir = ColonyRecorder.OutDir;
                Log.Message(string.Format(
                    "[ColonyRecorder] 只读殖民地快照录制器已加载（packageId={0}）；"
                    + "每 {1} 游戏 tick 追加一行 JSONL 到：{2}（不改地图，失败只记警告）",
                    PackageId,
                    ColonyRecorder.IntervalTicks,
                    string.IsNullOrEmpty(dir) ? "（暂时取不到存档目录，首次采样时再试）" : dir));
            }
            catch (Exception e)
            {
                try { Log.Warning("[ColonyRecorder] 入口日志失败（已吞）：" + e.Message); } catch (Exception) { }
            }
        }
    }
}
