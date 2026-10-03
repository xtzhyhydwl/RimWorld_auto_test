// DSH.ApiProbe - minimal compile probe against RimWorld 1.6 Assembly-CSharp.
// Every symbol used here was confirmed by IL/metadata inspection (see docs/游戏API符号确证.md).
// This file is NOT a mod; it only has to COMPILE.
using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace DSH.ApiProbe
{
    public static class Probe
    {
        // ---- A. food & nutrition -------------------------------------------------
        public static float NutritionOf(Thing t)
        {
            // expectation: extension method in RimWorld.StatExtension
            return t.GetStatValue(StatDefOf.Nutrition) * t.stackCount;
        }

        public static int StackLimitOf(ThingDef d)
        {
            return d.stackLimit;
        }

        public static bool IsFoodDef(ThingDef d)
        {
            return d.IsNutritionGivingIngestible
                && d.thingCategories != null
                && d.thingCategories.Contains(ThingCategoryDefOf.Foods);
        }

        public static float NeedFoodMax(Pawn p)
        {
            Need_Food n = p.needs != null ? p.needs.food : null;
            return n == null ? 0f : n.MaxLevel;
        }

        public static float HumanBaseHungerPerTick(ThingDef pawnDef, LifeStageDef stage)
        {
            return Need_Food.BaseHungerRate(stage, pawnDef);   // static
        }

        // ---- B. crops & growth ---------------------------------------------------
        public static float GrowthOf(Thing plantThing)
        {
            Plant p = plantThing as Plant;
            if (p == null) return 0f;
            float g = p.Growth;
            float rate = p.GrowthRate;
            PlantLifeStage ls = p.LifeStage;
            float lightFactor = p.GrowthRateFactor_Light;
            float fertFactor = p.GrowthRateFactor_Fertility;
            float tempFactor = p.GrowthRateFactor_Temperature;
            // NOTE: Plant.GrowthPerTick / Resting / HasEnoughLightToGrow are PROTECTED -> not callable
            // from outside. Reproduce GrowthPerTick from public data (formula confirmed from IL):
            float perTick = (ls == PlantLifeStage.Growing)
                ? (1f / (60000f * p.def.plant.growDays)) * rate
                : 0f;
            p.Growth = g;                       // setter exists
            PlantProperties props = p.def.plant;
            float growDays = props.growDays;
            float minGlow = props.growMinGlow;
            float optGlow = props.growOptimalGlow;
            float yield = props.harvestYield;
            float minGrowth = props.harvestMinGrowth;
            float afterGrowth = props.harvestAfterGrowth;
            return g + perTick + rate + lightFactor + fertFactor + tempFactor + growDays
                 + minGlow + optGlow + yield + minGrowth + afterGrowth + (float)(int)ls;
        }

        public static int YieldOf(Plant p)
        {
            if (!p.HarvestableNow) return 0;
            return p.YieldNow();
        }

        public static float GrowthFactors(ThingDef plantDef, float glow, float cellTemp, float fertilityAtCell)
        {
            return PlantUtility.GrowthRateFactorFor_Light(plantDef, glow)
                 * PlantUtility.GrowthRateFactorFor_Temperature(plantDef, cellTemp)
                 * PlantUtility.GrowthRateFactorFor_Fertility(plantDef, fertilityAtCell);
        }

        public static float AvgGrowthPerDay(Map map, ThingDef plantDef, TerrainDef terrain)
        {
            MapPlantGrowthRateCalculator calc = new MapPlantGrowthRateCalculator();
            calc.BuildFor(map);
            return calc.GrowthRateForDay(GenTicks.TicksGame, plantDef, terrain);
        }

        // ---- C. growing zone ------------------------------------------------------
        public static void SetCrop(Zone_Growing zone, ThingDef plantDef)
        {
            ThingDef cur = zone.PlantDefToGrow;      // get-only property
            zone.SetPlantDefToGrow(plantDef);        // the only way to write it
            bool ok = zone.CanAcceptSowNow();
            if (cur != null && cur != plantDef && ok) { /* ... */ }
        }

        public static Zone MakeGrowingZone(Map map, List<IntVec3> cells)
        {
            Zone_Growing z = new Zone_Growing(map.zoneManager);
            return ZoneMaker.MakeZoneWithCells(z, cells);   // map.zoneManager has NO CreateZone
        }

        public static AcceptanceReport CanPlantHere(ThingDef plantDef, IntVec3 c, Map map)
        {
            // byref parameter: metadata has no [Out] flag, so 'out' vs 'ref' is checked by the compiler
            Thing blocker;
            AcceptanceReport r = PlantUtility.CanEverPlantAt(plantDef, c, map, out blocker, false, true, false);
            if (!r.Accepted) return r;
            if (!PlantUtility.SandAllowsPlanting(c, map)) return false;
            if (!PlantUtility.SnowAllowsPlanting(c, map)) return false;
            TerrainDef terr = c.GetTerrain(map);
            float fertility = map.fertilityGrid.FertilityAt(c);
            float glow = map.glowGrid.GroundGlowAt(c, false, false);   // confirmed by compile-probe
            IPlantToGrowSettable settable = c.GetPlantToGrowSettable(map);
            TerrainAffordanceDef growSoil = DefDatabase<TerrainAffordanceDef>.GetNamed("GrowSoil", false);
            if (growSoil != null && terr.affordances != null && terr.affordances.Contains(growSoil)) { /* ... */ }
            return PlantUtility.CanEverPlantAt(plantDef, c, map, false, true)
                && settable != null && fertility >= plantDef.plant.fertilityMin;
        }

        // ---- D. pawns & work ------------------------------------------------------
        public static int GrowingPriority(Pawn p)
        {
            WorkTypeDef growing = WorkTypeDefOf.Growing;
            bool active = p.workSettings.WorkIsActive(growing);
            int prio = p.workSettings.GetPriority(growing);     // 0 == disabled
            if (!active) p.workSettings.SetPriority(growing, Pawn_WorkSettings.DefaultPriority);
            return prio;
        }

        public static int PlantSkill(Pawn p)
        {
            SkillRecord rec = p.skills.GetSkill(SkillDefOf.Plants);
            int lvl = rec.Level;
            Passion passion = rec.passion;
            return lvl + (int)passion;
        }

        public static void Designate(ThingDef plantDef, Map map, DesignationDef des)
        {
            JobDef harvestJob = JobDefOf.Harvest;
            DesignationDef harvestDes = DesignationDefOf.HarvestPlant;
            if (harvestJob != null || harvestDes != null || des != null || plantDef != null || map != null) { /* ... */ }
        }
    }
}
