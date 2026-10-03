using System;
using FoodControl.Core;
using Verse;

namespace FoodControl.Game
{
    /// <summary>
    /// **快照构建入口**（薄封装）。
    ///
    /// ## 这个类故意保持"薄"
    ///
    /// 所有实际读取逻辑都在 `ISnapshotReader` 的实现里（见 `SnapshotReaders.cs`）。
    /// 本类只负责：
    /// - 组装 `SamplingInfo`（采样密度度量）
    /// - 调 `SnapshotReaders.Build`
    ///
    /// ## ★ 怎么加一个新的快照类型（用户 2026-10-03 的核心要求）
    ///
    /// 用户原话：
    /// > 「我之后在实践的过程中，发现我们还需要**别的类型的快照**，
    /// >  要不然有些事情没法解释。那么就要能够**快速地把那个东西加进去**，
    /// >  而不是要从头写一大坨代码，然后可能**很多接口都变掉**，那就太糟糕了。」
    ///
    /// **加一个新面只需要两步**：
    ///
    /// ```csharp
    /// // ① 在 `SnapshotReaders.cs` 末尾写一个新类（照抄任何一个现有的）
    /// public sealed class RoomBeautyReader : ISnapshotReader
    /// {
    ///     public string Id { get { return "roomBeauty"; } }
    ///     public string DisplayName { get { return "房间美观"; } }
    ///     public int Order { get { return 45; } }          // 插在谁前面/后面
    ///     public string ProvidedFields { get { return "extra[roomBeauty]"; } }
    ///
    ///     public void Read(Map map, ColonySnapshot snap, SnapshotReadContext ctx)
    ///     {
    ///         // 想读什么就读什么；写进 snap 的新字段，或者塞 snap.Extra
    ///         foreach (Room r in map.regionGrid.AllRooms)
    ///         {
    ///             ctx.ItemsRead++;
    ///             // ...
    ///         }
    ///     }
    /// }
    ///
    /// // ② 在 `SnapshotReaders.RegisterBuiltIns()` 里加一行
    /// Register(new RoomBeautyReader());
    /// ```
    ///
    /// **不用改**本类、不用改 `ColonySnapshot` 的既有部分、不用改其他 Reader、
    /// 不用改任何接口。这就是"易于拓展"。
    ///
    /// ★ 如果新面需要往快照里写**结构化**内容 → 在 `ColonySnapshot` 加一个字段（**追加**，不动老的）。
    /// ★ 如果只是零散信息 → 塞 `snap.Extra["key"]`，**连 `ColonySnapshot` 都不用改**。
    /// </summary>
    public static class SnapshotBuilder
    {
        /// <summary>上次采样的 tick（用来算 `sincePrevTick`，即采样密度）</summary>
        private static int _lastTick = int.MinValue;
        private static int _seq;

        /// <summary>测试/复位用：清掉采样状态</summary>
        public static void ResetSampling()
        {
            _lastTick = int.MinValue;
            _seq = 0;
        }

        /// <summary>
        /// 构建一张快照。**只读**。
        /// </summary>
        /// <param name="intervalTicks">设定的采样间隔（进快照，供分析端判断"漏采了没"）</param>
        public static ColonySnapshot Build(Map map, int tick, int intervalTicks)
        {
            var sampling = new SamplingInfo
            {
                IntervalTicks = intervalTicks,
                TotalSnapshots = ++_seq,
                PlayedTicks = tick,
            };
            sampling.SincePrevTick = (_lastTick == int.MinValue) ? 0 : (tick - _lastTick);
            _lastTick = tick;

            ColonySnapshot snap = SnapshotReaders.Build(map, tick, sampling);
            // ⚠ 序号要从 sampling 同步过来 —— 我第一版忘了这一句，
            //   于是 `snap.Seq` 恒为 0，日志里"每 10 张报一次"变成"每张都报"
            //   （因为 `0 % 10 == 0` 永远成立）。
            snap.Seq = sampling.TotalSnapshots;
            return snap;
        }
    }
}
