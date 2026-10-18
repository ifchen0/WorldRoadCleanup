using System.Collections.Generic;
using HarmonyLib;
using RimWorld.Planet;
using Verse;

namespace WorldRoadCleanup
{
    public struct SegmentRecord
    {
        public long print;
        public int since;
    }

    /// <summary>
    /// Per-world tracking of when each road end and each mixed stretch was first seen unchanged.
    /// Saved as plain value lists inside the world node (no WorldComponent), so removing the mod
    /// leaves only ignored XML behind instead of a missing-class error.
    /// </summary>
    public class CleanupState
    {
        private static CleanupState current;

        public World world;
        public Dictionary<long, int> endSince = new Dictionary<long, int>();
        public Dictionary<long, SegmentRecord> segments = new Dictionary<long, SegmentRecord>();

        public static CleanupState Get()
        {
            World world = Find.World;
            if (world == null)
                return null;
            if (current == null || current.world != world)
                current = new CleanupState { world = world };
            return current;
        }

        public void ExposeData()
        {
            List<long> endKeys = null, segKeys = null, segPrints = null;
            List<int> endTicks = null, segTicks = null;
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                endKeys = new List<long>(endSince.Keys);
                endTicks = new List<int>(endSince.Values);
                segKeys = new List<long>();
                segPrints = new List<long>();
                segTicks = new List<int>();
                foreach (KeyValuePair<long, SegmentRecord> kv in segments)
                {
                    segKeys.Add(kv.Key);
                    segPrints.Add(kv.Value.print);
                    segTicks.Add(kv.Value.since);
                }
            }
            Scribe_Collections.Look(ref endKeys, "roadCleanupEndKeys", LookMode.Value);
            Scribe_Collections.Look(ref endTicks, "roadCleanupEndTicks", LookMode.Value);
            Scribe_Collections.Look(ref segKeys, "roadCleanupSegmentKeys", LookMode.Value);
            Scribe_Collections.Look(ref segPrints, "roadCleanupSegmentPrints", LookMode.Value);
            Scribe_Collections.Look(ref segTicks, "roadCleanupSegmentTicks", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                endSince.Clear();
                segments.Clear();
                if (endKeys != null && endTicks != null && endKeys.Count == endTicks.Count)
                {
                    for (int i = 0; i < endKeys.Count; i++)
                        endSince[endKeys[i]] = endTicks[i];
                }
                if (segKeys != null && segPrints != null && segTicks != null
                    && segKeys.Count == segPrints.Count && segKeys.Count == segTicks.Count)
                {
                    for (int i = 0; i < segKeys.Count; i++)
                        segments[segKeys[i]] = new SegmentRecord { print = segPrints[i], since = segTicks[i] };
                }
            }
        }

        public static void Expose(World world)
        {
            if (Scribe.mode == LoadSaveMode.LoadingVars || current == null || current.world != world)
                current = new CleanupState { world = world };
            current.ExposeData();
        }
    }

    [HarmonyPatch(typeof(World), nameof(World.ExposeData))]
    public static class World_ExposeData_Patch
    {
        public static void Postfix(World __instance)
        {
            if (Scribe.mode == LoadSaveMode.Saving || Scribe.mode == LoadSaveMode.LoadingVars)
                CleanupState.Expose(__instance);
        }
    }

    [HarmonyPatch(typeof(World), nameof(World.WorldTick))]
    public static class World_WorldTick_Patch
    {
        public static void Postfix()
        {
            int interval = WorldRoadCleanupMod.Settings.StepTicks;
            // Offset from the hour boundary so the pass does not land on the same tick as other daily work.
            if ((Find.TickManager.TicksGame + 7919) % interval != 0)
                return;
            PassResult result = RoadCleaner.RunPass(apply: true, ignoreStillness: false, flash: false);
            if (result.Changed && Prefs.DevMode)
                Log.Message("[World Road Cleanup] " + result);
        }
    }
}
