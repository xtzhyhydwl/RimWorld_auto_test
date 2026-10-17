using System;
using Verse;

namespace ColonySnapshots
{
    /// <summary>
    /// **殖民地快照录制器 —— mod 入口。**
    ///
    /// 这个类做两件事：
    /// ① ★ **在最早的时机初始化 `RunScope`**（本次进程启动的 runId + run 目录）；
    /// ② 往日志里打一段"我加载了"，把**三条流**、**采样间隔**、**本次 runId 与 run 目录**
    ///    一次说清（用户 2026-10-04 要求：入口日志必须包含三流 / runId）。
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
    /// ⚠ **这里为什么敢调 `RunScope.EnsureReady()`**：入口是最早的"安全时机"，
    ///   但它**也可能太早**（`GenFilePaths.SaveDataFolderPath` 取决于启动参数，
    ///   早期为空的时机是存在的）——
    ///   `EnsureReady()` 正是为此设计的：**幂等 + 失败不缓存 + 退避重试 + 只警告不抛**。
    ///   这里拿到 `false` 完全正常（日志里会写明"暂未就绪、写盘稍后重试"），
    ///   三条流各自在首次写盘前还会再调一次。
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
                // ① 最早时机初始化 run 作用域（拿不到就下次再试，见类注释）
                bool ready = RunScope.EnsureReady();
                string runId = RunScope.RunId;
                string runDir = RunScope.RunDir;
                string rootDir = RunScope.OutDirRoot;

                // ② 三条流 + 间隔 + runId 一次说清
                Log.Message(string.Format(
                    "[ColonyRecorder] 只读殖民地录制器已加载（packageId={0}）；**三条并列的数据流**，都只读、都不改地图："
                    + "\n  ① 时间驱动·快照：每 {1} 游戏 tick 追加一行 JSONL → colony-<殖民地名>-<mapId>.jsonl"
                    + "\n  ② 事件驱动·操作：每 {2} 游戏 tick 轮询一次状态差分，把**变化**写成事件 → actions-<殖民地名>-<mapId>.jsonl"
                    + "（首次进入地图额外写一条第 0 层 `buildability` 可建造性；单条事件最多 400 格，超出给 count+truncated）"
                    + "\n  ③ 事件驱动·游戏事件：**跟着②的节拍**（同一轮末尾）读 Find.LetterStack，把**新信件**写成 → events-<殖民地名>-<mapId>.jsonl"
                    + "\n  ★ 三条流写在**同一个 run 目录**里（一次进程启动 = 一个 run；换局/读档只开新 session，不再混文件）："
                    + "\n      runId   = {3}"
                    + "\n      run 目录 = {4}"
                    + "\n      老根目录（新数据不写这里，老文件不动）= {5}"
                    + "\n  ★ 三层身份：run（进程启动，目录名）× session（tick 倒退，行字段 sessionId/sessionSeq）× map（文件名里的 mapId）"
                    + "\n  失败只记警告，绝不抛给游戏。",
                    PackageId,
                    ColonyRecorder.IntervalTicks,
                    PlayerActionObserver.IntervalTicks,
                    string.IsNullOrEmpty(runId) ? "（暂时取不到存档目录，写盘稍后自动重试）" : runId,
                    string.IsNullOrEmpty(runDir) ? "（同上：run 目录尚未就绪）" : runDir,
                    string.IsNullOrEmpty(rootDir) ? "（取不到存档目录）" : rootDir));

                if (!ready)
                {
                    try
                    {
                        Log.Warning("[ColonyRecorder][run] 入口这次没能定下 run 目录（存档目录可能还没就绪）；"
                            + "**降级行为**：三条流这一段的采样照常推进、但**不写盘**，"
                            + "`RunScope` 会自动重试（最多 10 次、每次隔约 10 秒），成功后立刻开始写。");
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception e)
            {
                try { Log.Warning("[ColonyRecorder] 入口日志失败（已吞）：" + e.Message); } catch (Exception) { }
            }
        }
    }
}
