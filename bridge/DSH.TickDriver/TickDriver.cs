using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace DSH.TickDriver
{
    /// <summary>
    /// 游戏内闭环推时间的驱动。
    ///
    /// 要解决的问题（外部调用方视角）：以前「推到某个 tick」只有两条烂路 ——
    /// ① 算时间给延时然后让游戏自己跑（期间发生了什么没人知道，冲过检查点就只能重开）；
    /// ② 暂停态逐帧 `step_game_ticks`（精确但只有 ≈100 tick/s，且暂停态不出帧，画面是冻结的）。
    ///
    /// 这里换一个挂法：**不自己推进 tick，而是在 tick 边界上刹车**。
    /// 用 Harmony 在 `TickManager.DoSingleTick` 上挂一个 prefix —— 每一 tick 执行前问一次
    /// 「到点了没有」。到了就不执行这一 tick 并把游戏暂停。于是：
    ///   - 落点精确到 tick（不是"算个延时大概到这儿"，也不是"这一帧多跑了 180 tick"）；
    ///   - 游戏按自己的节奏跑速度档位，**帧照出**（与 `play_for` 一样的渲染，可截图）；
    ///   - 调用方只给两个数：速度档位 + 目标 tick；期间不需要任何往返。
    ///
    /// 状态机放在游戏进程里（`TickGate`），工具只负责「下命令 / 读状态」。所以工具调用不阻塞主线程：
    /// 谁读状态谁顺手做超时/停滞判定（见 `ReadStatus`）。
    /// </summary>
    public sealed class TimeDriverTools
    {
        // ── tick/run_to ──────────────────────────────────────────────

        [Tool("tick/run_to",
            Description = "Start an in-game closed loop: the game itself plays at the given speed tier and stops exactly on the target TicksGame (tick-boundary brake), then pauses. Returns immediately; poll tick/status until active=false. Absolute target, so calling it again with the same toTick after an interruption simply continues.",
            ResultDescription = "Plan handle: planId, fromTick, toTick, speed, plus whether the run started. success=false means it did not start (busy / bad arguments / Harmony unavailable).")]
        public object RunTo(
            [ToolParameter(Description = "Absolute target TicksGame, must be greater than the current tick", Required = true)] int toTick = 0,
            [ToolParameter(Description = "Speed tier while running: Normal, Fast, Superfast or Ultrafast (default Fast)")] string speed = "Fast",
            [ToolParameter(Description = "When true, re-assert the requested tier every 32 ticks, overriding RimWorld's ForceNormalSpeed (events pinning the game to 1x). Default false = let the game slow down.")] bool forceSpeed = false,
            [ToolParameter(Description = "When true, also stop on the tick where a new right-side letter appears (tick-precise, no polling blind window)")] bool stopOnLetter = false,
            [ToolParameter(Description = "Give up after this much wall clock; the run then ends as stopReason=timeout, paused, with the ticks it did advance")] int maxWallClockMs = 1200000)
        {
            try
            {
                var tm = Find.TickManager;
                if (Current.Game == null || tm == null)
                    return Fail("没有正在运行的游戏（Current.Game / TickManager 为空）");

                TimeSpeed requested;
                if (!TryParseSpeed(speed, out requested))
                    return Fail("speed 只接受 Normal / Fast / Superfast / Ultrafast，收到：" + speed);

                string patch = TickGate.EnsurePatched();
                if (patch != "ok")
                    return Fail("Harmony 挂不进 TickManager.DoSingleTick：" + patch);

                int from = tm.TicksGame;
                if (toTick <= from)
                    return Fail("toTick 必须大于当前 tick（tick 不能倒推）：当前 " + from + "，收到 " + toTick);
                if (maxWallClockMs <= 0) maxWallClockMs = 1200000;

                return TickGate.Start(toTick, requested, speed, forceSpeed, stopOnLetter, maxWallClockMs);
            }
            catch (Exception e)
            {
                return Fail(e.GetType().Name + ": " + e.Message);
            }
        }

        // ── tick/status ──────────────────────────────────────────────

        [Tool("tick/status",
            Description = "Read the tick run state. Read-only for the game: it does not advance ticks. Also closes out a run that overran its wall-clock budget or stopped making progress (external pause / modal window), so a stuck run cannot be polled forever.",
            ResultDescription = "active plus fromTick/toTick/currentTick/ticksAdvanced, stopReason and the landing verdict (reachedTarget, exact), effective ticksPerSecond, speedResets, letter counts.")]
        public object ReadStatus()
        {
            try
            {
                return TickGate.Snapshot();
            }
            catch (Exception e)
            {
                return Fail(e.GetType().Name + ": " + e.Message);
            }
        }

        // ── tick/cancel ──────────────────────────────────────────────

        [Tool("tick/cancel",
            Description = "Stop the active tick run now and pause the game. Use when a stop is decided from outside (agent saw an alert, user wants control back).",
            ResultDescription = "The final snapshot of the cancelled run (same shape as tick/status).")]
        public object Cancel()
        {
            try
            {
                TickGate.Cancel();
                return TickGate.Snapshot();
            }
            catch (Exception e)
            {
                return Fail(e.GetType().Name + ": " + e.Message);
            }
        }

        // ── tick/probe ───────────────────────────────────────────────

        [Tool("tick/probe",
            Description = "Self-check for the tick driver: is the Harmony patch live on TickManager.DoSingleTick, what tick/speed/pause state is the game in, how many letters are on screen. Read-only.",
            ResultDescription = "patch state, game clock, speed tier, UltraSpeedBoost flag, letter count, last run summary.")]
        public object Probe()
        {
            var r = new Dictionary<string, object>();
            try
            {
                string patch = TickGate.EnsurePatched();
                r["patch"] = patch;
                r["patchOk"] = patch == "ok";
                r["doSingleTickFound"] = TickGate.DoSingleTickFound;

                var tm = Find.TickManager;
                r["hasGame"] = Current.Game != null;
                r["hasTickManager"] = tm != null;
                if (tm != null)
                {
                    r["ticksGame"] = tm.TicksGame;
                    r["paused"] = tm.Paused;
                    r["curTimeSpeed"] = tm.CurTimeSpeed.ToString();
                    r["playerHasControl"] = Current.Game != null && Current.Game.PlayerHasControl;
                }
                r["ultraSpeedBoost"] = TickGate.ReadUltraSpeedBoost();
                r["letterCount"] = TickGate.LetterCount();
                r["run"] = TickGate.Snapshot();
                r["success"] = true;
            }
            catch (Exception e)
            {
                r["success"] = false;
                r["error"] = e.GetType().Name + ": " + e.Message;
            }
            return r;
        }

        private static object Fail(string message)
        {
            return new { success = false, error = message };
        }

        internal static bool TryParseSpeed(string text, out TimeSpeed speed)
        {
            speed = TimeSpeed.Fast;
            if (string.IsNullOrEmpty(text)) return false;
            switch (text.Trim().ToLowerInvariant())
            {
                case "normal": speed = TimeSpeed.Normal; return true;
                case "fast": speed = TimeSpeed.Fast; return true;
                case "superfast": speed = TimeSpeed.Superfast; return true;
                case "ultrafast": speed = TimeSpeed.Ultrafast; return true;
                default: return false;
            }
        }
    }

    /// <summary>
    /// 一次推进的运行状态 + tick 边界刹车。
    /// 为什么状态放在静态字段里：刹车跑在游戏主线程的 tick 循环里，工具调用在别的时机进来，
    /// 两边只能靠一块共享状态 + 锁交接；把状态塞进某个组件反而要在存档里存一份没意义的东西。
    /// </summary>
    internal static class TickGate
    {
        private const string HarmonyId = "dsh.tickdriver";
        private const string TickMethodName = "DoSingleTick";

        private static readonly object Sync = new object();

        private static Harmony harmony;
        private static string patchState = "not-attempted";
        private static bool doSingleTickFound;
        private static MethodInfo prefixMethod;

        private static Run active;
        private static Run last;

        // 反射拿到的私有字段：`curTimeSpeed` 是 RimWorld 真正被 tick 循环读的那个值；
        // 属性 setter 在某些 UI 状态下会被忽略（FoodProbe 踩过），所以写属性后要回读，读不回来再写字段。
        private static FieldInfo curTimeSpeedField;
        private static FieldInfo ultraSpeedBoostField;

        internal static bool DoSingleTickFound
        {
            get { lock (Sync) return doSingleTickFound; }
        }

        // ── 挂 Harmony ───────────────────────────────────────────────

        /// <summary>幂等；返回 ok / 具体原因。每次进来都先确保挂上，失败原因直接交给调用方看。</summary>
        internal static string EnsurePatched()
        {
            lock (Sync)
            {
                if (patchState == "ok") return patchState;
                try
                {
                    if (prefixMethod == null)
                    {
                        prefixMethod = typeof(TickGate).GetMethod(
                            "OnSingleTickPrefix", BindingFlags.Static | BindingFlags.NonPublic);
                        if (prefixMethod == null) { patchState = "找不到 OnSingleTickPrefix"; return patchState; }
                    }

                    MethodBase target = AccessTools.Method(typeof(TickManager), TickMethodName);
                    if (target == null)
                    {
                        doSingleTickFound = false;
                        patchState = "TickManager." + TickMethodName + " 不存在（游戏版本变了？）";
                        return patchState;
                    }
                    doSingleTickFound = true;

                    if (harmony == null) harmony = new Harmony(HarmonyId);
                    harmony.Patch(target, new HarmonyMethod(prefixMethod));
                    patchState = "ok";
                    Log.Message("[DSH.TickDriver] patched TickManager." + TickMethodName + " (tick-boundary brake)");
                }
                catch (Exception e)
                {
                    patchState = e.GetType().Name + ": " + e.Message;
                }
                return patchState;
            }
        }

        // ── 刹车：每一 tick 执行前问一次「到点了没有」 ─────────────────

        /// <summary>返回 false = 吃掉这一 tick。绝不抛异常（Harmony prefix 抛异常会把游戏 tick 循环打断）。</summary>
        private static bool OnSingleTickPrefix(TickManager __instance)
        {
            try
            {
                Run run;
                lock (Sync)
                {
                    run = active;
                    if (run == null || !run.Active) return true;
                }

                int tick = __instance.TicksGame;

                // 判据只看绝对 tick：`>=` 成立就说明「这一 tick 走出去必然越过目标」。
                // 从 from=33770 / to=36770 走到这里时 tick 正好是 36770 —— 前 4000 个 tick 都放行，
                // 第 4001 次进来才发现已到点，于是落点严格等于 to。
                if (tick >= run.ToTick)
                {
                    Complete(run, "target", "currentTick=" + tick + " toTick=" + run.ToTick);
                    return false;
                }

                if (run.StopOnLetter)
                {
                    int letters = LetterCount();
                    if (letters > run.LetterCountAtStart)
                    {
                        Complete(run, "letter", "letters " + run.LetterCountAtStart + " -> " + letters + " at tick " + tick);
                        return false;
                    }
                }

                run.TickCounter++;
                if ((run.TickCounter & 63) == 0)
                {
                    run.CurrentTick = tick;
                    run.LastProgressMs = NowMs();
                }

                // 事件会把速度钉回 1×（ForceNormalSpeed）。forceSpeed=true 时每 32 tick 把档位压回去，
                // 并记账 —— speedResets 就是「这一程被游戏拉回去过几次」的证据。
                if (run.ForceSpeed && (run.TickCounter & 31) == 0 && !__instance.Paused
                    && __instance.CurTimeSpeed != run.RequestedSpeed)
                {
                    SetTimeSpeed(run.RequestedSpeed);
                    run.SpeedResets++;
                }

                return true;
            }
            catch
            {
                return true;
            }
        }

        // ── 启动 / 收尾 / 快照 ───────────────────────────────────────

        internal static object Start(int toTick, TimeSpeed speed, string speedName, bool forceSpeed,
            bool stopOnLetter, int maxWallClockMs)
        {
            lock (Sync)
            {
                var tm = Find.TickManager;
                if (active != null && active.Active)
                {
                    return new
                    {
                        success = false,
                        busy = true,
                        error = "已经有一程在跑（planId=" + active.PlanId + "，to=" + active.ToTick + "）。先 tick/cancel 或等它结束。",
                        run = SnapshotLocked()
                    };
                }

                var run = new Run
                {
                    PlanId = "t" + (++planSeq),
                    FromTick = tm.TicksGame,
                    ToTick = toTick,
                    RequestedSpeed = speed,
                    RequestedSpeedName = speedName,
                    ForceSpeed = forceSpeed,
                    StopOnLetter = stopOnLetter,
                    MaxWallClockMs = maxWallClockMs,
                    SpeedBefore = tm.CurTimeSpeed,
                    SpeedBeforeName = tm.CurTimeSpeed.ToString(),
                    LetterCountAtStart = LetterCount(),
                    CurrentTick = tm.TicksGame,
                    StartedMs = NowMs(),
                    LastProgressMs = NowMs(),
                    Active = true
                };

                SetTimeSpeed(speed);   // 顺便解暂停：正常路面下这里就是「放开时间」
                run.SpeedAfterStart = tm.CurTimeSpeed.ToString();

                // 档位写进去了、可游戏还是暂停 ⇒ 外面把它钉住了（教学步骤要你动镜头、模态窗等）。
                // 这种情况不该起跑等超时：直接失败，并说清楚是谁钉的。
                if (tm.Paused)
                {
                    return new
                    {
                        success = false,
                        blockedByExternalPause = true,
                        error = "档位已写成 " + run.SpeedAfterStart + "，但游戏仍是暂停态（教学步骤/模态窗/别处 force pause）。"
                              + "本驱动只会在 tick 边界刹车，解不开外面的暂停：先关窗口、或用 move_camera 走完教学步骤，再调一次。",
                        ticksGame = tm.TicksGame,
                        paused = true,
                        currentSpeed = run.SpeedAfterStart
                    };
                }

                active = run;

                return new
                {
                    success = true,
                    started = true,
                    planId = run.PlanId,
                    fromTick = run.FromTick,
                    toTick = run.ToTick,
                    speed = run.RequestedSpeedName,
                    appliedSpeed = run.SpeedAfterStart,
                    paused = tm.Paused,
                    forceSpeed = run.ForceSpeed,
                    stopOnLetter = run.StopOnLetter,
                    maxWallClockMs = run.MaxWallClockMs,
                    letterCountAtStart = run.LetterCountAtStart,
                    note = "已放开时间，游戏自己在 tick 边界停在 toTick。轮询 tick/status 直到 active=false。"
                };
            }
        }

        internal static void Cancel()
        {
            Run run;
            lock (Sync)
            {
                run = active;
                if (run == null || !run.Active) return;
            }
            Complete(run, "cancelled", "tick/cancel");
        }

        /// <summary>结束一程：记账 + 暂停。多个线程可能同时到点（状态读线程 + tick 线程），所以用 Active 做一次性的闸。</summary>
        private static void Complete(Run run, string reason, string detail)
        {
            lock (Sync)
            {
                if (run == null || !run.Active) return;
                var tm = Find.TickManager;
                int tick = tm != null ? tm.TicksGame : run.CurrentTick;
                run.Active = false;
                run.CurrentTick = tick;
                run.TicksAdvanced = tick - run.FromTick;
                run.StoppedBy = reason;
                run.StopDetail = detail;
                run.StoppedMs = NowMs();
                last = run;
                if (ReferenceEquals(active, run)) active = null;
            }
            // 暂停放在锁外：写游戏状态不该占着锁，而且写失败也不影响记账。
            Pause();
        }

        internal static Dictionary<string, object> Snapshot()
        {
            lock (Sync)
            {
                if (active != null && active.Active) EvaluateStalledOrOverdue(active);
                return SnapshotLocked();
            }
        }

        /// <summary>
        /// 调用方在轮询，所以「跑不动了」这件事在这里顺手判掉，不用额外的帧钩子：
        ///   - 超了 maxWallClockMs ⇒ timeout（暂停，保留已走 tick）；
        ///   - 2 秒没有任何 tick 进展 ⇒ externalPause（模态窗 / 别处强制暂停）。
        /// </summary>
        private static void EvaluateStalledOrOverdue(Run run)
        {
            long now = NowMs();
            var tm = Find.TickManager;

            // 已经到点、但游戏在外面先停了（这一帧根本没有「下一 tick」可以拦下来说"到点了"）：
            // 落点是对的，理由必须记成 target，不能记成 externalPause —— 否则读日志的人会以为没到达。
            if (tm != null && tm.TicksGame >= run.ToTick)
            {
                Complete(run, "target", "ticksGame=" + tm.TicksGame + " 已到 toTick=" + run.ToTick + "（先到点后才停的）");
                return;
            }

            if (now - run.StartedMs > run.MaxWallClockMs)
            {
                Complete(run, "timeout", "elapsed=" + (now - run.StartedMs) + "ms > maxWallClockMs=" + run.MaxWallClockMs);
                return;
            }

            bool notTicking = tm == null || tm.Paused || tm.TicksGame == run.CurrentTick;
            if (notTicking && now - run.LastProgressMs > 2000)
            {
                Complete(run, "externalPause", "tick 在 " + run.CurrentTick + " 停了 " + (now - run.LastProgressMs)
                    + "ms（暂停态/教学步骤等外部 force pause），不是本驱动让它停的");
            }
        }

        private static Dictionary<string, object> SnapshotLocked()
        {
            var tm = Find.TickManager;
            var run = (active != null && active.Active) ? active : last;

            var d = new Dictionary<string, object>();
            d["success"] = true;
            d["patch"] = patchState;
            d["hasGame"] = Current.Game != null;
            d["paused"] = tm != null && tm.Paused;
            d["currentSpeed"] = tm != null ? tm.CurTimeSpeed.ToString() : null;

            if (run == null)
            {
                d["active"] = false;
                d["hasRun"] = false;
                d["currentTick"] = tm != null ? tm.TicksGame : (int?)null;
                return d;
            }

            bool activeNow = run.Active;
            int tickNow = tm != null ? tm.TicksGame : run.CurrentTick;
            long elapsed = (activeNow ? NowMs() : run.StoppedMs) - run.StartedMs;
            int advanced = tickNow - run.FromTick;

            d["active"] = activeNow;
            d["hasRun"] = true;
            d["planId"] = run.PlanId;
            d["fromTick"] = run.FromTick;
            d["toTick"] = run.ToTick;
            d["currentTick"] = tickNow;
            d["ticksAdvanced"] = advanced;
            d["ticksRequested"] = run.ToTick - run.FromTick;
            d["remaining"] = Math.Max(0, run.ToTick - tickNow);
            d["requestedSpeed"] = run.RequestedSpeedName;
            d["forceSpeed"] = run.ForceSpeed;
            d["stopOnLetter"] = run.StopOnLetter;
            d["maxWallClockMs"] = run.MaxWallClockMs;
            d["elapsedMs"] = elapsed;
            d["ticksPerSecond"] = elapsed > 0 ? Math.Round(advanced * 1000.0 / elapsed, 1) : (double?)null;
            d["speedResets"] = run.SpeedResets;
            d["speedBefore"] = run.SpeedBeforeName;
            d["appliedSpeed"] = run.SpeedAfterStart;
            d["letterCountAtStart"] = run.LetterCountAtStart;
            d["letterCountNow"] = LetterCount();
            d["stopReason"] = run.StoppedBy;
            d["stopDetail"] = run.StopDetail;
            d["reachedTarget"] = tickNow >= run.ToTick;
            d["exact"] = tickNow == run.ToTick;
            if (!activeNow)
            {
                d["landedAt"] = run.CurrentTick;
                d["overrunTicks"] = Math.Max(0, run.CurrentTick - run.ToTick);
            }
            return d;
        }

        private static int planSeq;

        // ── 游戏状态读写 ─────────────────────────────────────────────

        /// <summary>写速度档位：先走属性（引擎自己的记账），回读不对再写私有字段（UI 状态会挡住属性 setter）。</summary>
        private static void SetTimeSpeed(TimeSpeed speed)
        {
            var tm = Find.TickManager;
            if (tm == null) return;
            try
            {
                tm.CurTimeSpeed = speed;
                if (tm.CurTimeSpeed == speed) return;
            }
            catch
            {
                // 落下去写字段
            }
            try
            {
                if (curTimeSpeedField == null)
                {
                    curTimeSpeedField = typeof(TickManager).GetField(
                        "curTimeSpeed", BindingFlags.NonPublic | BindingFlags.Instance);
                }
                if (curTimeSpeedField != null) curTimeSpeedField.SetValue(tm, speed);
            }
            catch
            {
                // 两种办法都失败：留给 status 的「跑不动」判定去暴露
            }
        }

        private static void Pause()
        {
            SetTimeSpeed(TimeSpeed.Paused);
        }

        internal static object ReadUltraSpeedBoost()
        {
            try
            {
                var tm = Find.TickManager;
                if (tm == null) return null;
                if (ultraSpeedBoostField == null)
                {
                    // 只读诊断用：实例/静态都试一遍，名字对不上就返回 null（不影响推进）。
                    ultraSpeedBoostField = typeof(TickManager).GetField(
                        "UltraSpeedBoost",
                        BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
                }
                return ultraSpeedBoostField != null ? ultraSpeedBoostField.GetValue(tm) : null;
            }
            catch
            {
                return null;
            }
        }

        internal static int LetterCount()
        {
            try
            {
                var stack = Find.LetterStack;
                if (stack == null) return 0;
                var letters = stack.LettersListForReading;
                return letters == null ? 0 : letters.Count;
            }
            catch
            {
                return 0;
            }
        }

        private static long NowMs()
        {
            return Stopwatch.GetTimestamp() * 1000L / Stopwatch.Frequency;
        }

        private sealed class Run
        {
            public string PlanId;
            public int FromTick;
            public int ToTick;
            public TimeSpeed RequestedSpeed;
            public string RequestedSpeedName;
            public bool ForceSpeed;
            public bool StopOnLetter;
            public int MaxWallClockMs;

            public TimeSpeed SpeedBefore;
            public string SpeedBeforeName;
            public string SpeedAfterStart;
            public int LetterCountAtStart;

            public bool Active;
            public string StoppedBy;
            public string StopDetail;
            public int CurrentTick;
            public int TicksAdvanced;
            public int TickCounter;
            public int SpeedResets;

            public long StartedMs;
            public long LastProgressMs;
            public long StoppedMs;
        }
    }
}
