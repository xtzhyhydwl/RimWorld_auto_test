// BikeProbe · MapComponent 生命周期探针（A3）
//
// ★ A3 的问题原文：“Verse.MapComponent 子类**会不会被引擎自动实例化**；
//   FinalizeInit / MapGenerated 哪个时机能安全建索引。”注册方式原本**未确证**。
//
// ★ 本轮 IL 已把这个未知量钉死，探针的职责是把它变成运行时数字：
//   `Verse.Map::FillComponents()`（构造函数族里被调）的做法是
//       components.RemoveAll(<predicate>)
//       foreach (Type t in GenTypes.AllSubclassesNonAbstract(typeof(Verse.MapComponent)))
//           if (typeof(Verse.CustomMapComponent).IsAssignableFrom(t)) continue;
//           if (this.GetComponent(t)) continue;
//           components.Add((MapComponent)Activator.CreateInstance(t, new object[]{ this }));
//   ⇒ **注册方式 = 只要写一个非抽象 `Verse.MapComponent` 子类 + 一个 `Verse.Map` 参数的 public ctor，
//     零 XML、零 Def、零注册。**`Verse.MapComponentDef` 这个类型在全程序集里**不存在**
//     （类型名子串 "MapComponentDef" 0 命中）。
//   ⇒ 而 `Verse.CustomMapComponent` 那一支（必须由 `MapGeneratorDef.customMapComponents` 声明）
//     本探针**故意不继承**，好让“自动实例化”这条路径被单独检验。
//
// ★ `GetCachedMapComponent` 这个函数名也**不存在**（全程序集成员名 0 命中）；
//   真名是 `Verse.Map.GetComponent<T>()` 与 `Verse.Map.GetComponent(System.Type)`。
//
// ★ IL 已看到的 8 个 public virtual（全部 flags=0x01c6，可直接 override，不需要 Harmony）：
//   MapComponentUpdate / MapComponentTick / MapComponentOnGUI / MapComponentDraw /
//   ExposeData / FinalizeInit / MapGenerated / MapRemoved

using System.Collections.Generic;
using Verse;

namespace BikeProbe
{
    public class Probe_MapComponent : MapComponent
    {
        private readonly List<Thing> bikeIndex = new List<Thing>();
        private int tickLogged;
        private int updateLogged;
        private int drawLogged;

        public Probe_MapComponent(Map map) : base(map)
        {
            ProbeLog.Line("A3", "ctor mapId=" + (map == null ? -1 : map.uniqueID)
                          + " tick=" + GenTicks.TicksGame);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            ProbeLog.Line("A3", "ExposeData mode=" + Scribe.mode + " tick=" + GenTicks.TicksGame
                          + " indexCount=" + bikeIndex.Count);
        }

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            ProbeLog.Line("A3", "FinalizeInit tick=" + GenTicks.TicksGame);
            RebuildIndex("FinalizeInit");
        }

        public override void MapGenerated()
        {
            base.MapGenerated();
            ProbeLog.Line("A3", "MapGenerated tick=" + GenTicks.TicksGame);
            RebuildIndex("MapGenerated");
        }

        public override void MapComponentTick()
        {
            base.MapComponentTick();
            if (tickLogged < 3)
            {
                tickLogged++;
                ProbeLog.Line("A3", "MapComponentTick #" + tickLogged + " tick=" + GenTicks.TicksGame);
            }
        }

        public override void MapComponentUpdate()
        {
            base.MapComponentUpdate();
            if (updateLogged < 2)
            {
                updateLogged++;
                ProbeLog.Line("A3", "MapComponentUpdate #" + updateLogged + " tick=" + GenTicks.TicksGame);
            }
        }

        public override void MapComponentDraw()
        {
            base.MapComponentDraw();
            if (drawLogged < 2)
            {
                drawLogged++;
                ProbeLog.Line("A3", "MapComponentDraw #" + drawLogged + " tick=" + GenTicks.TicksGame);
            }
        }

        public override void MapRemoved()
        {
            ProbeLog.Line("A3", "MapRemoved tick=" + GenTicks.TicksGame + " indexCount=" + bikeIndex.Count);
            base.MapRemoved();
        }

        /// <summary>
        /// “能不能在这个时机安全建索引”的探针：重建索引并断言
        /// 「索引条数 == 地图上实际的 ProbeBike 条数」。
        /// </summary>
        public void RebuildIndex(string when)
        {
            bikeIndex.Clear();

            int actual = 0;
            ThingDef def = ProbeDefs.Bike;
            if (map != null && def != null)
            {
                List<Thing> found = map.listerThings.ThingsOfDef(def);
                if (found != null)
                {
                    actual = found.Count;
                    for (int i = 0; i < found.Count; i++)
                    {
                        bikeIndex.Add(found[i]);
                    }
                }
            }

            bool ok = bikeIndex.Count == actual;
            string line = "index@" + when + " indexCount=" + bikeIndex.Count + " actual=" + actual
                          + " match=" + ok + " listerNull=" + (map == null || map.listerThings == null)
                          + " tick=" + GenTicks.TicksGame;
            if (ok)
            {
                ProbeLog.Line("A3", line);
            }
            else
            {
                ProbeLog.Fail("A3", line);
            }
        }

        public int IndexCount
        {
            get { return bikeIndex.Count; }
        }

        /// <summary>给下一阶段（游戏内）用的自检入口：map.GetComponent&lt;Probe_MapComponent&gt;() 是不是非 null。</summary>
        public static void SelfCheck(Map map, string when)
        {
            if (map == null)
            {
                ProbeLog.Fail("A3", "SelfCheck(" + when + ") map=null");
                return;
            }

            Probe_MapComponent c = map.GetComponent<Probe_MapComponent>();
            ProbeLog.Line("A3", "SelfCheck(" + when + ") GetComponent<Probe_MapComponent>()="
                          + (c == null ? "null" : "non-null")
                          + " indexCount=" + (c == null ? -1 : c.IndexCount)
                          + " componentsFieldCount=" + (map.components == null ? -1 : map.components.Count));
        }
    }
}
