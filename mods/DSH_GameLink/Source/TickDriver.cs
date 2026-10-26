// DSH_GameLink · 推时间引擎（tick 边界刹车 + 停点判据）。
//
// 来历：这份逻辑原来是 RimBridgeServer 的伴生 DLL（bridge\DSH.TickDriver）里的，
// 2026-10-25 搬进 GameLink —— 因为"给游戏加一条命令"属于 GameLink 的职责（它是 agent↔游戏 的通道），
// 而不是挂在别人的 mod 上（别人家 SDK 一升级就得重编、卸载了就没了）。
//
// 核心手法没变：**不自己推进 tick，只在 tick 边界上刹车**。
// Harmony 在 `TickManager.DoSingleTick` 上挂前缀，每 tick 执行前问一次「该停了没有」；
// 该停就不执行这一 tick，并把游戏暂停。所以：
//   · 落点严格等于目标 tick（overrun 恒 0）；
//   · 游戏按自己的档位跑、帧照出（截图是真实帧，不是暂停态 step 的冻结帧）；
//   · 停点判据（信件/消息）在同一个前缀里判，命中即停，不靠轮询、没有盲窗。
//
// 为什么判据默认用轮询而不是 Harmony 钩子：钩子最多让我们早 1 tick，却要 patch 别人的方法
// （冲突 + 版本脆弱）；信件/消息列表都只有个位数，每 tick 读一次 Count 可以忽略。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace DSH_GameLink
{
    /// <summary>
    /// Harmony 挂载点。单独一个类是为了让"挂补丁"和"跑状态机"分开：
    /// 补丁只做转发，状态机（TickDriver）不认识 Harmony。
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class TickBrake
    {
        static TickBrake()
        {
            TickDriver.Install();
        }

        /// <summary>返回 false = 吃掉这一 tick。绝不抛异常（抛出去会打断游戏的 tick 循环）。</summary>
        internal static bool Prefix(TickManager __instance)
        {
            try
            {
                return TickDriver.OnTick(__instance);
            }
            catch
            {
                return true;
            }
        }
    }

    internal static class TickDriver
    {
        private const string HarmonyId = "dsh.gamelink.tickbrake";
        private const string TickMethodName = "DoSingleTick";
        /// <summary>被 `ForceNormalSpeed` 压回 1× 时，每 32 tick 把档位压回去一次（并记账）。</summary>
        private const int SpeedReassertEveryTicks = 32;
        /// <summary>2 秒没有任何 tick 进展就判定「不是我们在慢，是外面把它停住了」。</summary>
        private const long StallMs = 2000;

        private static readonly object Gate = new object();

        private static Harmony harmony;
        private static string patchState = "not-attempted";
        private static MethodInfo prefixMethod;

        private static Run active;
        private static Run last;
        private static int planSeq;

        private static FieldInfo curTimeSpeedField;
        private static FieldInfo ultraSpeedBoostField;

        // ── 挂补丁 ───────────────────────────────────────────────

        /// <summary>幂等；返回 ok 或具体原因（`time.probe` 会把原因原样给上位机）。</summary>
        internal static string Install()
        {
            lock (Gate)
            {
                if (patchState == "ok") return patchState;
                try
                {
                    if (prefixMethod == null)
                    {
                        prefixMethod = typeof(TickBrake).GetMethod(
                            "Prefix", BindingFlags.Static | BindingFlags.NonPublic);
                        if (prefixMethod == null)
                        {
                            patchState = "找不到 TickBrake.Prefix";
                            return patchState;
                        }
                    }

                    MethodBase target = AccessTools.Method(typeof(TickManager), TickMethodName);
                    if (target == null)
                    {
                        patchState = "TickManager." + TickMethodName + " 不存在（游戏版本变了？）";
                        LiveChannel.Err("推时间不可用：" + patchState);
                        return patchState;
                    }

                    if (harmony == null) harmony = new Harmony(HarmonyId);
                    harmony.Patch(target, new HarmonyMethod(prefixMethod));
                    patchState = "ok";
                }
                catch (Exception ex)
                {
                    patchState = LiveChannel.Describe(ex);
                    LiveChannel.Err("推时间补丁挂载失败：" + patchState);
                }

                return patchState;
            }
        }

        // ── 每一 tick 的判据 ─────────────────────────────────────

        /// <summary>由 Harmony 前缀调用；返回 false = 不吃这一 tick。</summary>
        internal static bool OnTick(TickManager tm)
        {
            Run run = active;
            if (run == null || !run.Active) return true;

            int tick = tm.TicksGame;

            // ① 目标 tick（绝对）：走到这里 tick 已经 == toTick 时，再走一步就超了
            if (run.ToTick > 0 && tick >= run.ToTick)
            {
                Complete(run, Protocol.StopReasons.Target, "ticksGame=" + tick + " toTick=" + run.ToTick, null);
                return false;
            }

            // ② 相对上限（maxTicks）：给"只跑到某个事件、但怕它永远不来"兜底
            if (run.MaxTicks > 0 && tick - run.FromTick >= run.MaxTicks)
            {
                Complete(run, Protocol.StopReasons.MaxTicks, "跑满 maxTicks=" + run.MaxTicks, null);
                return false;
            }

            // ③ 停点判据（stopWhen）：命中即停，顺手把现场打包
            if (run.StopSpecs.Count > 0)
            {
                int letterCount = ProbeSource.LetterCount();
                int messageCount = ProbeSource.MessageCount();
                for (int i = 0; i < run.StopSpecs.Count; i++)
                {
                    ProbeSpec spec = run.StopSpecs[i];
                    if (spec.Kind == "letter")
                    {
                        if (letterCount <= run.LetterBase) continue;
                        Letter hit = ProbeSource.FindNewLetter(spec, run.LetterBase, letterCount);
                        if (hit == null) continue;
                        Complete(run, Protocol.StopReasons.Probe, spec.Raw, spec.DescribeLetter(hit));
                        return false;
                    }

                    if (messageCount <= run.MessageBase) continue;
                    Message msg = ProbeSource.FindNewMessage(spec, run.MessageBase, messageCount);
                    if (msg == null) continue;
                    Complete(run, Protocol.StopReasons.Probe, spec.Raw, spec.DescribeMessage(msg));
                    return false;
                }
            }

            // ④ 只观察不停（watch）：命中就往事件通道写一条 probe_hit
            if (run.WatchSpecs.Count > 0)
            {
                int letterCount = ProbeSource.LetterCount();
                int messageCount = ProbeSource.MessageCount();
                for (int i = 0; i < run.WatchSpecs.Count; i++)
                {
                    WatchSpec watch = run.Watches[i];
                    if (watch.Spec.Kind == "letter")
                    {
                        if (letterCount <= watch.Seen) continue;
                        Letter hit = ProbeSource.FindNewLetter(watch.Spec, watch.Seen, letterCount);
                        watch.Seen = letterCount;
                        if (hit == null) continue;
                        EmitHit(watch.Spec, hit, null);
                        continue;
                    }

                    if (messageCount <= watch.Seen) continue;
                    Message msg = ProbeSource.FindNewMessage(watch.Spec, watch.Seen, messageCount);
                    watch.Seen = messageCount;
                    if (msg == null) continue;
                    EmitHit(watch.Spec, null, msg);
                }
            }

            run.TickCounter++;
            if ((run.TickCounter & 63) == 0)
            {
                run.CurrentTick = tick;
                run.LastProgressMs = NowMs();
            }

            // ⑤ 被事件钉回 1× 时把档位压回去（forceSpeed 才做）
            if (run.ForceSpeed && (run.TickCounter & (SpeedReassertEveryTicks - 1)) == 0 && !tm.Paused
                && tm.CurTimeSpeed != run.RequestedSpeed)
            {
                SetTimeSpeed(run.RequestedSpeed);
                run.SpeedResets++;
            }

            return true;
        }

        private static void EmitHit(ProbeSpec spec, Letter letter, Message message)
        {
            try
            {
                // 字段名不能叫 kind：事件行本身已经有 `"kind":"probe_hit"`（LiveChannel 写的），
                // 再加一个 kind 就是同名键，JSON.parse 会取后一个，事件种类直接丢掉（2026-10-25 实测踩到）。
                EventLine line = new EventLine("probe_hit")
                    .Num("tick", SafeTicksGame())
                    .Text("probe", spec.Raw)
                    .Text("probeKind", spec.Kind);
                if (letter != null)
                {
                    line.Text("defName", letter.def != null ? letter.def.defName : null)
                        .Text("label", SafeLabel(letter));
                }
                else if (message != null)
                {
                    line.Text("text", message.text)
                        .Text("defName", message.def != null ? message.def.defName : null);
                }

                line.Emit();
            }
            catch (Exception ex)
            {
                LiveChannel.ReportError("probe_hit", ex);
            }
        }

        private static string SafeLabel(Letter letter)
        {
            try { return letter.Label; } catch { return null; }
        }

        // ── 起 / 停 / 读 ─────────────────────────────────────────

        internal static string Start(KvArgs args, out bool ok, out string code, out string message)
        {
            ok = false;
            code = null;
            message = null;

            string install = Install();
            if (install != "ok")
            {
                code = Protocol.Errors.Harmony;
                message = "Harmony 挂不进 TickManager." + TickMethodName + "：" + install;
                LiveChannel.Err(message);
                return "{}";
            }

            TickManager tm = SafeTickManager();
            if (Current.Game == null || tm == null)
            {
                code = Protocol.Errors.NoGame;
                message = "没有正在运行的游戏（Current.Game / TickManager 为空）";
                return "{}";
            }

            int toTick;
            if (!args.TryInt("toTick", out toTick)) toTick = 0;
            // 相对模式：`ticks=N` 在**游戏内**换算成 toTick = 当前 + N。
            // 为什么要有它：上位机读到的 tick 到命令生效之间，游戏可能已经跑了几十~几百 tick
            // （Ultrafast 11k tick/s，50 ms 就是 550 tick），"推 N tick" 由上位机算就会偏；
            // 只有游戏自己能原子地取到那个起点。
            int relativeTicks;
            if (!args.TryInt("ticks", out relativeTicks)) relativeTicks = 0;
            int maxTicks;
            if (!args.TryInt("maxTicks", out maxTicks)) maxTicks = 0;
            int maxWallClockMs;
            if (!args.TryInt("maxWallClockMs", out maxWallClockMs) || maxWallClockMs <= 0) maxWallClockMs = 1_200_000;
            bool forceSpeed = args.Bool("forceSpeed");

            string speedName = args.Get("speed");
            if (string.IsNullOrEmpty(speedName)) speedName = "Fast";
            TimeSpeed speed;
            if (!TryParseSpeed(speedName, out speed))
            {
                code = Protocol.Errors.BadArgs;
                message = "speed 只接受 Normal / Fast / Superfast / Ultrafast，收到：" + speedName;
                return "{}";
            }

            string stopRaw = args.Get("stopWhen");
            string watchRaw = args.Get("watch");
            string parseError;
            List<ProbeSpec> stopSpecs = ProbeSpec.ParseList(stopRaw, out parseError);
            if (stopSpecs == null)
            {
                code = Protocol.Errors.BadArgs;
                message = "stopWhen 解析失败：" + parseError;
                return "{}";
            }

            List<ProbeSpec> watchSpecs = ProbeSpec.ParseList(watchRaw, out parseError);
            if (watchSpecs == null)
            {
                code = Protocol.Errors.BadArgs;
                message = "watch 解析失败：" + parseError;
                return "{}";
            }

            if (toTick <= 0 && relativeTicks <= 0 && maxTicks <= 0 && stopSpecs.Count == 0)
            {
                code = Protocol.Errors.BadArgs;
                message = "至少要给一个停止条件：toTick / ticks / maxTicks / stopWhen 四者之一";
                return "{}";
            }

            int from = tm.TicksGame;
            if (toTick <= 0 && relativeTicks > 0) toTick = from + relativeTicks;
            if (toTick > 0 && toTick <= from)
            {
                code = Protocol.Errors.BadArgs;
                message = "toTick 必须大于当前 tick（tick 不能倒推）：当前 " + from + "，收到 " + toTick;
                return "{}";
            }

            lock (Gate)
            {
                if (active != null && active.Active)
                {
                    code = Protocol.Errors.Busy;
                    message = "已经有一程在跑（planId=" + active.PlanId + "，to=" + active.ToTick +
                              "）。先 time.cancel 或等它结束。";
                    return active.ToJson().ToString();
                }

                Run run = new Run
                {
                    PlanId = "p" + (++planSeq),
                    FromTick = from,
                    ToTick = toTick > 0 ? toTick : 0,
                    MaxTicks = maxTicks > 0 ? maxTicks : 0,
                    RequestedSpeed = speed,
                    RequestedSpeedName = speedName,
                    ForceSpeed = forceSpeed,
                    StopSpecs = stopSpecs,
                    WatchSpecs = watchSpecs,
                    LetterBase = ProbeSource.LetterCount(),
                    MessageBase = ProbeSource.MessageCount(),
                    MaxWallClockMs = maxWallClockMs,
                    SpeedBeforeName = tm.CurTimeSpeed.ToString(),
                    CurrentTick = from,
                    StartedMs = NowMs(),
                    LastProgressMs = NowMs(),
                    Active = true
                };

                run.Watches = new List<WatchSpec>();
                for (int i = 0; i < watchSpecs.Count; i++)
                {
                    run.Watches.Add(new WatchSpec
                    {
                        Spec = watchSpecs[i],
                        Seen = watchSpecs[i].Kind == "letter" ? run.LetterBase : run.MessageBase
                    });
                }

                SetTimeSpeed(speed);
                run.AppliedSpeedName = tm.CurTimeSpeed.ToString();

                // 档位写进去了却还是暂停 ⇒ 外面钉住了（教学步骤要你动镜头、模态窗……）：不该起跑等超时。
                if (tm.Paused)
                {
                    code = Protocol.Errors.ExternalPause;
                    message = "档位已写成 " + run.AppliedSpeedName + "，但游戏仍是暂停态（教学步骤/模态窗/别处 force pause）。" +
                              "本驱动只会在 tick 边界刹车，解不开外面的暂停。";
                    LiveChannel.Err(message);
                    return "{}";
                }

                active = run;
                ok = true;
                return run.ToJson().ToString();
            }
        }

        internal static string Status(out bool ok, out string code, out string message)
        {
            ok = true;
            code = null;
            message = null;
            return StatusJson();
        }

        /// <summary>状态快照（JSON）。读它顺手做「超时 / 跑不动」的收尾判定，所以它是最便宜的轮询口。</summary>
        /// <summary>是否有一程在跑（给命令通道决定 state.json 的重写频率）。</summary>
        internal static bool IsActive
        {
            get
            {
                lock (Gate)
                {
                    return active != null && active.Active;
                }
            }
        }

        internal static string StatusJson()
        {
            lock (Gate)
            {
                if (active != null && active.Active) EvaluateStalledOrOverdue(active);
                return SnapshotJson(active != null && active.Active ? active : last);
            }
        }

        internal static string Cancel(out bool ok, out string code, out string message)
        {
            ok = true;
            code = null;
            message = null;
            Run run;
            lock (Gate)
            {
                run = active;
                if (run == null || !run.Active)
                {
                    return SnapshotJson(last);
                }
            }

            Complete(run, Protocol.StopReasons.Cancelled, "time.cancel", null);
            lock (Gate)
            {
                return SnapshotJson(last);
            }
        }

        internal static string ProbeJson()
        {
            TickManager tm = SafeTickManager();
            Run run = active != null && active.Active ? active : last;
            // 先读一次条数：它会触发消息列表的反射解析，之后 MessageSourceName 才有值。
            // （2026-10-25 真机：先读名字后读条数 ⇒ 第一次 probe 永远 null。）
            int messages = ProbeSource.MessageCount();
            int letters = ProbeSource.LetterCount();
            return new JsonBuf()
                .Num("v", Protocol.Version)
                .Str("patch", patchState)
                .Str("tickMethod", TickMethodName)
                .Bool("hasGame", Current.Game != null)
                .Bool("hasTickManager", tm != null)
                .Num("tick", tm != null ? tm.TicksGame : -1)
                .Bool("paused", tm != null && tm.Paused)
                .Str("speed", tm != null ? tm.CurTimeSpeed.ToString() : null)
                .Raw("ultraSpeedBoost", ReadUltraSpeedBoostLiteral(tm))
                .Str("messageSource", ProbeSource.MessageSourceName)
                .Num("letters", letters)
                .Num("messages", messages)
                .Raw("run", run != null ? run.ToJson().ToString() : "null")
                .ToString();
        }

        // ── 收尾 / 快照 ─────────────────────────────────────────

        private static void Complete(Run run, string reason, string detail, string payloadJson)
        {
            lock (Gate)
            {
                if (run == null || !run.Active) return;
                TickManager tm = SafeTickManager();
                int tick = tm != null ? tm.TicksGame : run.CurrentTick;
                run.Active = false;
                run.CurrentTick = tick;
                run.TicksAdvanced = tick - run.FromTick;
                run.StoppedBy = reason;
                run.StopDetail = detail;
                run.StopPayload = payloadJson;
                run.StoppedMs = NowMs();
                last = run;
                if (ReferenceEquals(active, run)) active = null;
            }

            Pause();
        }

        /// <summary>
        /// 「跑不动了」的判定放在读状态时做（上位机在轮询，不需要额外的帧钩子）：
        ///   · 已经到点 ⇒ 正常收尾（target）；
        ///   · 超了墙钟预算 ⇒ timeout；
        ///   · 2 秒没有任何 tick 进展 ⇒ stalled（被外面的模态窗/教学步骤钉住，或世界被换掉了）。
        /// </summary>
        private static void EvaluateStalledOrOverdue(Run run)
        {
            long now = NowMs();
            TickManager tm = SafeTickManager();

            if (run.ToTick > 0 && tm != null && tm.TicksGame >= run.ToTick)
            {
                Complete(run, Protocol.StopReasons.Target, "ticksGame=" + tm.TicksGame + " 已到 toTick=" + run.ToTick, null);
                return;
            }

            if (now - run.StartedMs > run.MaxWallClockMs)
            {
                Complete(run, Protocol.StopReasons.Timeout,
                    "elapsed=" + (now - run.StartedMs) + "ms > maxWallClockMs=" + run.MaxWallClockMs, null);
                return;
            }

            bool notTicking = tm == null || tm.Paused || tm.TicksGame == run.CurrentTick;
            if (notTicking && now - run.LastProgressMs > StallMs)
            {
                Complete(run, Protocol.StopReasons.Stalled,
                    "tick 在 " + run.CurrentTick + " 停了 " + (now - run.LastProgressMs) + "ms（不是本驱动让它停的）", null);
            }
        }

        private static string SnapshotJson(Run run)
        {
            TickManager tm = SafeTickManager();
            return new JsonBuf()
                .Num("v", Protocol.Version)
                .Str("patch", patchState)
                .Bool("hasGame", Current.Game != null)
                .Num("tick", tm != null ? tm.TicksGame : -1)
                .Bool("paused", tm != null && tm.Paused)
                .Str("speed", tm != null ? tm.CurTimeSpeed.ToString() : null)
                .Bool("active", run != null && run.Active)
                .Raw("run", run != null ? run.ToJson().ToString() : "null")
                .ToString();
        }

        // ── 游戏状态读写 ─────────────────────────────────────────

        /// <summary>
        /// 写速度档位。**坑（FoodProbe 实测）**：`CurTimeSpeed` 的 setter 会先过 `PlayerCanControl` 那一关，
        /// 某些 UI 状态下会被**无声拒绝**；所以写完必须回读，读不回来才写私有字段 `curTimeSpeed`。
        /// </summary>
        private static void SetTimeSpeed(TimeSpeed speed)
        {
            TickManager tm = SafeTickManager();
            if (tm == null) return;
            try
            {
                tm.CurTimeSpeed = speed;
                if (tm.CurTimeSpeed == speed) return;
            }
            catch
            {
                // 落到写字段
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
                // 两种办法都失败：交给 status 的「跑不动」判定去暴露
            }
        }

        private static void Pause()
        {
            SetTimeSpeed(TimeSpeed.Paused);
        }

        internal static string ReadUltraSpeedBoostLiteral(TickManager tm)
        {
            try
            {
                if (tm == null) return "null";
                if (ultraSpeedBoostField == null)
                {
                    ultraSpeedBoostField = typeof(TickManager).GetField(
                        "UltraSpeedBoost",
                        BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
                }

                if (ultraSpeedBoostField == null) return "null";
                object v = ultraSpeedBoostField.GetValue(ultraSpeedBoostField.IsStatic ? null : (object)tm);
                return v is bool && (bool)v ? "true" : "false";
            }
            catch
            {
                return "null";
            }
        }

        private static bool TryParseSpeed(string text, out TimeSpeed speed)
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

        /// <summary>
        /// `Find.TickManager` 在**主菜单**会抛 NRE（它是 `Current.Game.tickManager` 之类的直取），
        /// 不是返回 null —— 所以"判空"救不了，只能 try/catch（2026-10-25 真机：主菜单泵每帧 3046 条 NRE）。
        /// 所有取 tick / 取 TickManager 的地方都必须走这两个口。
        /// </summary>
        internal static TickManager SafeTickManager()
        {
            try
            {
                return Find.TickManager;
            }
            catch
            {
                return null;
            }
        }

        internal static int SafeTicksGame()
        {
            TickManager tm = SafeTickManager();
            return tm != null ? tm.TicksGame : -1;
        }

        private static long NowMs()
        {
            return Stopwatch.GetTimestamp() * 1000L / Stopwatch.Frequency;
        }

        private sealed class WatchSpec
        {
            internal ProbeSpec Spec;
            /// <summary>已经看到过的条数：只报新增的，不重复报。</summary>
            internal int Seen;
        }

        private sealed class Run
        {
            internal string PlanId;
            internal int FromTick;
            internal int ToTick;
            internal int MaxTicks;
            internal TimeSpeed RequestedSpeed;
            internal string RequestedSpeedName;
            internal bool ForceSpeed;
            internal List<ProbeSpec> StopSpecs;
            internal List<ProbeSpec> WatchSpecs;
            internal List<WatchSpec> Watches;
            internal int LetterBase;
            internal int MessageBase;
            internal int MaxWallClockMs;

            internal string SpeedBeforeName;
            internal string AppliedSpeedName;

            internal bool Active;
            internal string StoppedBy;
            internal string StopDetail;
            internal string StopPayload;
            internal int CurrentTick;
            internal int TicksAdvanced;
            internal int TickCounter;
            internal int SpeedResets;

            internal long StartedMs;
            internal long LastProgressMs;
            internal long StoppedMs;

            internal JsonBuf ToJson()
            {
                long elapsed = (Active ? NowMs() : StoppedMs) - StartedMs;
                int liveAdvanced = Active ? Math.Max(0, CurrentTick - FromTick) : TicksAdvanced;
                return new JsonBuf()
                    .Str("planId", PlanId)
                    .Num("fromTick", FromTick)
                    .Num("toTick", ToTick)
                    .Num("maxTicks", MaxTicks)
                    .Str("requestedSpeed", RequestedSpeedName)
                    .Str("appliedSpeed", AppliedSpeedName)
                    .Bool("forceSpeed", ForceSpeed)
                    .Num("letterBase", LetterBase)
                    .Num("messageBase", MessageBase)
                    .Num("ticksAdvanced", liveAdvanced)
                    .Num("remaining", ToTick > 0 ? Math.Max(0, ToTick - CurrentTick) : 0)
                    .Num("ticksPerSecond", elapsed > 0 ? Math.Round(liveAdvanced * 1000.0 / elapsed, 1) : 0d, 1)
                    .Num("speedResets", SpeedResets)
                    .Str("stopReason", StoppedBy)
                    .Str("stopDetail", StopDetail)
                    .Raw("stopPayload", StopPayload ?? "null")
                    .Num("elapsedMs", elapsed)
                    .Bool("active", Active);
            }
        }
    }
}
