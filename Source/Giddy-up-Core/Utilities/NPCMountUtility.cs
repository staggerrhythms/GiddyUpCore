﻿using GiddyUpCore.Jobs;
using GiddyUpCore.Storage;
using RimWorld;
using RimWorld.Planet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Verse;
using Verse.AI;

namespace GiddyUpCore.Utilities
{
    public class NPCMountUtility
    {

        public static bool generateMounts(ref List<Pawn> list, IncidentParms parms, int inBiomeWeight, int outBiomeWeight, int nonWildWeight, int mountChance, int mountChanceTribal)
        {
            Map map = parms.target as Map;
            if (map == null)
            {
                Caravan caravan = (Caravan)parms.target;
                int tile = caravan.Tile;
                map = Current.Game.FindMap(tile);
                if (map == null)
                {
                    return false;
                }
            }

            Predicate<PawnKindDef> isAnimal = (PawnKindDef d) => d.race != null && d.race.race.Animal;

            mountChance = getMountChance(parms, mountChance, mountChanceTribal);
            if (mountChance == -1)//wrong faction
            {
                return false;
            }
            List<string> factionFarmAnimalRestrictions = new List<string>();
            List<string> factionWildAnimalRestrictions = new List<string>();
            if (parms.faction.def.HasModExtension<FactionRestrictionsPatch>())
            {
                FactionRestrictionsPatch factionRestrictions = parms.faction.def.GetModExtension<FactionRestrictionsPatch>();
                factionFarmAnimalRestrictions = factionRestrictions.getAllowedNonWildAnimalsAsList();
                factionWildAnimalRestrictions = factionRestrictions.getAllowedWildAnimalsAsList();

                if(factionRestrictions.mountChance > -1)
                {
                    mountChance = factionRestrictions.mountChance;
                }

                if(!factionWildAnimalRestrictions.NullOrEmpty() && factionFarmAnimalRestrictions.NullOrEmpty() && factionRestrictions.wildAnimalWeight >= 0)
                {
                    inBiomeWeight = 0;
                    nonWildWeight = 0;
                    outBiomeWeight = factionRestrictions.wildAnimalWeight;
                }
                if (factionWildAnimalRestrictions.NullOrEmpty() && !factionFarmAnimalRestrictions.NullOrEmpty() && factionRestrictions.nonWildAnimalWeight >= 0)
                {
                    inBiomeWeight = 0;
                    outBiomeWeight = 0;
                    nonWildWeight = factionRestrictions.nonWildAnimalWeight;
                }
                if (!factionWildAnimalRestrictions.NullOrEmpty() && !factionFarmAnimalRestrictions.NullOrEmpty())
                {
                    inBiomeWeight = 0;
                    if(factionRestrictions.wildAnimalWeight >= 0)
                    outBiomeWeight = factionRestrictions.wildAnimalWeight;
                    if (factionRestrictions.nonWildAnimalWeight >= 0)
                    nonWildWeight = factionRestrictions.nonWildAnimalWeight;
                }
            }

            Random rand = new Random(DateTime.Now.Millisecond);

            int totalWeight = inBiomeWeight + outBiomeWeight + nonWildWeight;
            float inBiomeWeightNormalized = (float)inBiomeWeight / (float)totalWeight * 100f;
            float outBiomeWeightNormalized = (float)outBiomeWeight / (float)totalWeight * 100f;

            List<Pawn> animals = new List<Pawn>();
            foreach (Pawn pawn in list)
            {
                //TODO add chance
                if (!pawn.RaceProps.Humanlike)
                {
                    continue;
                }

                int rndInt = rand.Next(1, 100);
                if (mountChance <= rndInt || !pawn.RaceProps.Humanlike)
                {
                    continue;
                }
                int pawnHandlingLevel = pawn.skills.GetSkill(SkillDefOf.Animals).Level;

                PawnKindDef pawnKindDef = determinePawnKind(map, isAnimal, inBiomeWeightNormalized, outBiomeWeightNormalized, rndInt, pawnHandlingLevel, factionFarmAnimalRestrictions, factionWildAnimalRestrictions);

                if (pawnKindDef == null)
                {
                    Log.Error("No spawnable animals right now.");
                    return false;
                }
                Pawn animal = PawnGenerator.GeneratePawn(pawnKindDef, parms.faction);
                GenSpawn.Spawn(animal, pawn.Position, map, parms.spawnRotation, false);
                ConfigureSpawnedAnimal(pawn, ref animal);
                animals.Add(animal);

            }
            list = list.Concat(animals).ToList();
            return true;
        }

        private static void ConfigureSpawnedAnimal(Pawn pawn, ref Pawn animal)
        {
            ExtendedPawnData pawnData = Base.Instance.GetExtendedDataStorage().GetExtendedDataFor(pawn);
            ExtendedPawnData animalData = GiddyUpCore.Base.Instance.GetExtendedDataStorage().GetExtendedDataFor(animal);
            pawnData.mount = animal;
            TextureUtility.setDrawOffset(pawnData);
            animal.mindState.duty = new PawnDuty(DutyDefOf.Defend);
            if (animal.jobs == null)
            {
                animal.jobs = new Pawn_JobTracker(animal);
            }
            Job jobAnimal = new Job(GUC_JobDefOf.Mounted, pawn);
            jobAnimal.count = 1;
            animal.jobs.TryTakeOrderedJob(jobAnimal);
            animalData.ownedBy = pawn;
            animal.playerSettings = new Pawn_PlayerSettings(animal);
            animal.training.Train(TrainableDefOf.Obedience, pawn);
            pawnData.owning = animal;

        }

        private static PawnKindDef determinePawnKind(Map map, Predicate<PawnKindDef> isAnimal, float inBiomeWeightNormalized, float outBiomeWeightNormalized, int rndInt, int pawnHandlingLevel, List<string> factionFarmAnimalRestrictions, List<string> factionWildAnimalRestrictions)
        {
            PawnKindDef pawnKindDef = null;



            if (factionWildAnimalRestrictions.NullOrEmpty() && rndInt <= inBiomeWeightNormalized)
            {
                (from a in map.Biome.AllWildAnimals
                               where map.mapTemperature.SeasonAcceptableFor(a.race) 
                               && IsMountableUtility.isAllowedInModOptions(a.defName)
                               select a).TryRandomElementByWeight((PawnKindDef def) => calculateCommonality(def, map, pawnHandlingLevel), out pawnKindDef);
            }
            else if (rndInt <= inBiomeWeightNormalized + outBiomeWeightNormalized)
            {
                (from a in DefDatabase<PawnKindDef>.AllDefs
                               where isAnimal(a) 
                               && a.wildSpawn_spawnWild 
                               && map.mapTemperature.SeasonAcceptableFor(a.race) 
                               && IsMountableUtility.isAllowedInModOptions(a.defName)
                               && (factionWildAnimalRestrictions.NullOrEmpty() || factionWildAnimalRestrictions.Contains(a.defName))
                               select a).TryRandomElementByWeight((PawnKindDef def) => calculateCommonality(def, map, pawnHandlingLevel), out pawnKindDef);
            }
            else
            {
                (from a in DefDatabase<PawnKindDef>.AllDefs
                               where isAnimal(a) 
                               && !a.wildSpawn_spawnWild 
                               && map.mapTemperature.SeasonAcceptableFor(a.race) 
                               && IsMountableUtility.isAllowedInModOptions(a.defName)
                               && (factionFarmAnimalRestrictions.NullOrEmpty() || factionFarmAnimalRestrictions.Contains(a.defName))
                 select a).TryRandomElementByWeight((PawnKindDef def) => 1 - def.RaceProps.wildness, out pawnKindDef);
            }

            return pawnKindDef;
        }

        private static float calculateCommonality(PawnKindDef def, Map map, int pawnHandlingLevel)
        {
            float commonality = map.Biome.CommonalityOfAnimal(def);
            //minimal level to get bonus. 
            pawnHandlingLevel = pawnHandlingLevel > 5 ? pawnHandlingLevel - 5 : 0;

            //Common animals more likely when pawns have low handling, and rare animals more likely when pawns have high handling.  
            float commonalityAdjusted = commonality * ((15f - (float)commonality)) / 15f + (1 - commonality) * ((float)pawnHandlingLevel) / 15f;
            //Wildness decreases the likelyhood of the mount being picked. Handling level mitigates this. 
            float wildnessPenalty = 1 - (def.RaceProps.wildness * ((15f - (float)pawnHandlingLevel) / 15f));

            //Log.Message("name: " + def.defName + ", commonality: " + commonality + ", pawnHandlingLevel: " + pawnHandlingLevel + ", wildness: " + def.RaceProps.wildness + ", commonalityBonus: " + commonalityAdjusted + ", wildnessPenalty: " + wildnessPenalty + ", result: " + commonalityAdjusted * wildnessPenalty);
            return commonalityAdjusted * wildnessPenalty;
        }

        private static int getMountChance(IncidentParms parms, int mountChance, int mountChanceTribal)
        {
            if(parms.faction == null)
            {
                return -1;
            }
            if (parms.faction.def == FactionDefOf.Tribe)
            {
                return mountChanceTribal;
            }
            else if (parms.faction.def != FactionDefOf.Spacer && parms.faction.def != FactionDefOf.SpacerHostile && parms.faction.def != FactionDefOf.Mechanoid)
            {
                return mountChance;
            }
            else
            {
                return -1;
            }
        }
    }
}

