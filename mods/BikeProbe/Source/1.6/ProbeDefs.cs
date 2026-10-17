// BikeProbe · Def 查表 + 计数（探针 A2 / C1 / C3 的“可观测输出”）
//
// 计数口径（方案 A2 的判据）：
//   ① 地上（建筑形态）的 ProbeBike 有几辆 —— map.listerThings.ThingsOfDef(bikeDef).Count
//   ② map.listerThings.ThingsInGroup(ThingRequestGroup.MinifiedThing) 有几件
//      ★ 这一组到底能不能枚举到**自定义 MinifiedThing 子类**，是本轮要测的未知量之一。
//        （本轮 IL 已确证 ThingListGroupHelper::Includes 的第 39 号分支是
//          `typeof(RimWorld.MinifiedThing).IsAssignableFrom(def.thingClass)`，
//          所以理论上“能”；探针的职责是把这个理论变成实测数字。）
//   ③ 每个殖民者背包 innerContainer 里有几件 ProbeBikeMinified
//
// 一切输出都走 ProbeLog，前缀统一 [BikeProbe]。

using System.Collections.Generic;
using System.Text;
using RimWorld;
using Verse;

namespace BikeProbe
{
    public static class ProbeDefs
    {
        public const string BikeDefName = "ProbeBike";
        /// <summary>C3 的对照车：Mass=0（探针工具 def，不改产品参数）。</summary>
        public const string BikeMasslessDefName = "ProbeBikeMassless";
        public const string MinifiedDefName = "ProbeBikeMinified";
        public const string TidyJobDefName = "ProbeTidyBike";
        public const string RideJobDefName = "ProbeRideBike";
        public const string ThinkTreeDefName = "ProbeThinkTree_Tidy";

        public static ThingDef Bike
        {
            get { return DefDatabase<ThingDef>.GetNamed(BikeDefName, false); }
        }

        public static ThingDef BikeMassless
        {
            get { return DefDatabase<ThingDef>.GetNamed(BikeMasslessDefName, false); }
        }

        public static ThingDef MinifiedBike
        {
            get { return DefDatabase<ThingDef>.GetNamed(MinifiedDefName, false); }
        }

        public static JobDef TidyJob
        {
            get { return DefDatabase<JobDef>.GetNamed(TidyJobDefName, false); }
        }

        public static JobDef RideJob
        {
            get { return DefDatabase<JobDef>.GetNamed(RideJobDefName, false); }
        }

        public static ThingDef Steel
        {
            get { return ThingDefOf.Steel; }
        }
    }

    public static class ProbeCounts
    {
        /// <summary>A2 的“逐段打点”用：把三类计数一次性打一行。</summary>
        public static void LogAll(string probe, Map map, string tag)
        {
            if (map == null)
            {
                ProbeLog.Line(probe, tag + " map=null");
                return;
            }

            int buildingOnGround = 0;
            ThingDef bikeDef = ProbeDefs.Bike;
            if (bikeDef != null)
            {
                List<Thing> bikes = map.listerThings.ThingsOfDef(bikeDef);
                buildingOnGround = bikes != null ? bikes.Count : 0;
            }

            List<Thing> minifiedGroup = map.listerThings.ThingsInGroup(ThingRequestGroup.MinifiedThing);
            int groupTotal = minifiedGroup != null ? minifiedGroup.Count : 0;
            int groupOurs = 0;
            if (minifiedGroup != null)
            {
                ThingDef miniDef = ProbeDefs.MinifiedBike;
                for (int i = 0; i < minifiedGroup.Count; i++)
                {
                    if (miniDef != null && minifiedGroup[i].def == miniDef)
                    {
                        groupOurs++;
                    }
                }
            }

            StringBuilder sb = new StringBuilder();
            sb.Append(tag);
            sb.Append(" buildingOnGround=").Append(buildingOnGround);
            sb.Append(" groupMinifiedTotal=").Append(groupTotal);
            sb.Append(" groupMinifiedOurs=").Append(groupOurs);
            sb.Append(" tick=").Append(GenTicks.TicksGame);
            sb.Append(" colonists=").Append(DescribeInventories(map));
            ProbeLog.Line(probe, sb.ToString());
        }

        private static string DescribeInventories(Map map)
        {
            List<Pawn> pawns = map.mapPawns != null ? map.mapPawns.FreeColonistsSpawned : null;
            if (pawns == null || pawns.Count == 0)
            {
                return "(none)";
            }

            ThingDef miniDef = ProbeDefs.MinifiedBike;
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn p = pawns[i];
                int invCount = p.inventory != null && p.inventory.innerContainer != null
                    ? p.inventory.innerContainer.Count
                    : -1;
                int ours = 0;
                if (p.inventory != null && p.inventory.innerContainer != null && miniDef != null)
                {
                    ThingOwner<Thing> owner = p.inventory.innerContainer;
                    for (int j = 0; j < owner.Count; j++)
                    {
                        if (owner[j].def == miniDef)
                        {
                            ours++;
                        }
                    }
                }

                float carried = p.carryTracker != null && p.carryTracker.CarriedThing != null
                    ? 1f
                    : 0f;

                if (i > 0)
                {
                    sb.Append(';');
                }
                sb.Append(p.ThingID).Append("{inv=").Append(invCount)
                  .Append(",ours=").Append(ours)
                  .Append(",carrying=").Append(carried)
                  .Append(",freeSpace=").Append(MassUtility.FreeSpace(p).ToString("0.##"))
                  .Append(",capacity=").Append(MassUtility.Capacity(p, null).ToString("0.##"))
                  .Append('}');
            }
            return sb.ToString();
        }
    }

    public static class ProbeCells
    {
        /// <summary>从 from 附近找一个 InBounds 且 Standable 的格子（找不到就返回 from）。</summary>
        public static IntVec3 FindNear(Map map, IntVec3 from, int radius)
        {
            for (int r = 1; r <= radius; r++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    for (int dz = -r; dz <= r; dz++)
                    {
                        if (dx != -r && dx != r && dz != -r && dz != r)
                        {
                            continue;
                        }
                        IntVec3 c = new IntVec3(from.x + dx, 0, from.z + dz);
                        if (GenGrid.InBounds(c, map) && c.Standable(map))
                        {
                            return c;
                        }
                    }
                }
            }
            return from;
        }

        /// <summary>
        /// 从 from 附近找一个"**距离在 [minR, maxR] 之间**且 InBounds 且 Standable"的格子。
        /// run-2 新增：D 的"移动中携带"视觉判据需要一个**足够长的走路窗口**
        /// （run-1 的落点半径=4 ⇒ 实测 travelTicks 只有 0/49，40 张截图没抓到一帧清晰证据）。
        /// 找不到就退回 from（宁可原地放下，也不要抛异常打断整条链）。
        /// </summary>
        public static IntVec3 FindFar(Map map, IntVec3 from, int minR, int maxR)
        {
            for (int r = maxR; r >= minR; r--)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    for (int dz = -r; dz <= r; dz++)
                    {
                        if (dx != -r && dx != r && dz != -r && dz != r)
                        {
                            continue;
                        }
                        IntVec3 c = new IntVec3(from.x + dx, 0, from.z + dz);
                        if (GenGrid.InBounds(c, map) && c.Standable(map))
                        {
                            return c;
                        }
                    }
                }
            }
            return from;
        }
    }
}
