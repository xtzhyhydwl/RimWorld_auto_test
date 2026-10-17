// DSH_GameLink · 游戏侧状态事件（game_loaded / colony_started / window / heartbeat / error）。
//
// 不注册：`Game.FillComponents()` 自己用 `Activator.CreateInstance(type, new object[]{ game })`
// 收养所有「非抽象 GameComponent 子类 + 有 ctor(Game)」的类型（见 语音副官/docs/取证-GameComponent注册.md）。
// 所以下面的 public 无状态 ctor 是硬要求，删了就会变成「Could not instantiate a GameComponent」。
//
// 帧率：GameComponentUpdate 每帧只做三件常数级的事（读三个字段 + 比较一个字符串），没有遍历、没有分配；
// 真正拼字符串只发生在「状态真的变了」的那一帧。GameComponentTick 只在到点时才动。

using System;
using Verse;

namespace DSH_GameLink
{
    public sealed class GameLinkComponent : GameComponent
    {
        /// <summary>心跳开关：把它置 false（或用下面的常量改间隔）就不写 heartbeat。默认开。</summary>
        public static bool HeartbeatEnabled = true;

        /// <summary>心跳间隔（tick）。60 tick = 1 游戏秒（正常速度下约 1 实时秒）。</summary>
        public const int HeartbeatIntervalTicks = 60;

        private const string NoWindow = "";

        private bool gameLoadedPending;
        private bool colonyStartedReported;
        private int nextHeartbeatTick;
        private string lastWindowType = NoWindow;

        public GameLinkComponent(Game game)
        {
        }

        public override void LoadedGame()
        {
            gameLoadedPending = true;
        }

        public override void StartedNewGame()
        {
            gameLoadedPending = true;
            colonyStartedReported = false;
        }

        public override void GameComponentUpdate()
        {
            // 三件事各自 try/catch：任何一件炸了都不能把异常丢进 Unity 的 Update 里（那会掉帧甚至崩游戏）。
            try
            {
                EmitGameLoaded();
            }
            catch (Exception ex)
            {
                LiveChannel.ReportError("game_loaded", ex);
            }

            try
            {
                EmitColonyStarted();
            }
            catch (Exception ex)
            {
                LiveChannel.ReportError("colony_started", ex);
            }

            try
            {
                EmitWindowChange();
            }
            catch (Exception ex)
            {
                LiveChannel.ReportError("window", ex);
            }
        }

        public override void GameComponentTick()
        {
            if (!HeartbeatEnabled)
            {
                return;
            }

            int now = GenTicks.TicksGame;
            if (nextHeartbeatTick == 0)
            {
                nextHeartbeatTick = now + HeartbeatIntervalTicks;
            }

            if (now < nextHeartbeatTick)
            {
                return;
            }

            nextHeartbeatTick = now + HeartbeatIntervalTicks;
            try
            {
                EmitHeartbeat(now);
            }
            catch (Exception ex)
            {
                LiveChannel.ReportError("heartbeat", ex);
            }
        }

        // 读档 / 新开局之后的第一次 Update：此时 world、maps 都已就绪（见 取证 §2.4），map 才有意义。
        private void EmitGameLoaded()
        {
            if (!gameLoadedPending)
            {
                return;
            }

            gameLoadedPending = false;
            new EventLine("game_loaded")
                .Num("tick", GenTicks.TicksGame)
                .Num("map", CurrentMapId())
                .Emit();
        }

        // 「殖民地建好了」的唯一判据：有了玩家主基地地图。只报第一次。
        private void EmitColonyStarted()
        {
            if (colonyStartedReported)
            {
                return;
            }

            Map home = Find.AnyPlayerHomeMap;
            if (home == null)
            {
                return;
            }

            colonyStartedReported = true;
            new EventLine("colony_started")
                .Num("tick", GenTicks.TicksGame)
                .Num("colonists", home.mapPawns.FreeColonistsCount)
                .Num("map", home.uniqueID)
                .Emit();
        }

        // 顶层窗口 = 窗口栈的最后一个（它是最后画的那个）。只在「类型名变了」时写一条，
        // 所以每帧的代价就是：取栈长度 + 取一个元素 + 一次字符串比较。
        private void EmitWindowChange()
        {
            WindowStack stack = Find.WindowStack;
            string type = NoWindow;
            bool forcePause = false;
            if (stack != null)
            {
                int count = stack.Count;
                if (count > 0)
                {
                    Window top = stack[count - 1];
                    if (top != null)
                    {
                        Type topType = top.GetType();
                        type = topType.FullName ?? topType.Name;
                        forcePause = top.forcePause;
                    }
                }
            }

            if (type == lastWindowType)
            {
                return;
            }

            lastWindowType = type;
            new EventLine("window")
                .Flag("opened", type.Length > 0)
                .Text("type", type)
                .Flag("forcePause", forcePause)
                .Emit();
        }

        private static void EmitHeartbeat(int tick)
        {
            float rate = 0f;
            TickManager tickManager = Find.TickManager;
            if (tickManager != null)
            {
                rate = tickManager.TickRateMultiplier;
            }

            new EventLine("heartbeat")
                .Num("tick", tick)
                .Text("programState", Current.ProgramState.ToString())
                .Num("speed", (int)Math.Round(rate))
                .Num("map", CurrentMapId())
                .Emit();
        }

        private static int CurrentMapId()
        {
            Map map = Find.CurrentMap;
            return map == null ? -1 : map.uniqueID;
        }
    }
}
