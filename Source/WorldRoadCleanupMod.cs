using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace WorldRoadCleanup
{
    public class CleanupSettings : ModSettings
    {
        public bool removeDeadEnds = true;
        public bool unifyTypes = true;
        public bool straightenKinks = true;
        public int stillnessDays = 60;
        public int maxSpurLength = 8;
        public int stepHours = 24;
        // Development aid: no stillness period and a step every second at normal speed.
        public bool debugFast;

        public int StillnessTicks => debugFast ? 0 : stillnessDays * GenDate.TicksPerDay;
        public int StepTicks => debugFast ? 60 : stepHours * GenDate.TicksPerHour;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref removeDeadEnds, "removeDeadEnds", true);
            Scribe_Values.Look(ref unifyTypes, "unifyTypes", true);
            Scribe_Values.Look(ref straightenKinks, "straightenKinks", true);
            Scribe_Values.Look(ref stillnessDays, "stillnessDays", 60);
            Scribe_Values.Look(ref maxSpurLength, "maxSpurLength", 8);
            Scribe_Values.Look(ref stepHours, "stepHours", 24);
            Scribe_Values.Look(ref debugFast, "debugFast", false);
        }
    }

    public class WorldRoadCleanupMod : Mod
    {
        public static CleanupSettings Settings;

        public WorldRoadCleanupMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<CleanupSettings>();
            new Harmony("ifchen0.worldroadcleanup").PatchAll();
        }

        public override string SettingsCategory() => "World Road Cleanup";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var listing = new Listing_Standard();
            listing.Begin(inRect);
            listing.CheckboxLabeled("WRC_RemoveDeadEnds".Translate(), ref Settings.removeDeadEnds, "WRC_RemoveDeadEnds_Desc".Translate());
            listing.CheckboxLabeled("WRC_StraightenKinks".Translate(), ref Settings.straightenKinks, "WRC_StraightenKinks_Desc".Translate());
            listing.CheckboxLabeled("WRC_UnifyTypes".Translate(), ref Settings.unifyTypes, "WRC_UnifyTypes_Desc".Translate());
            listing.Gap();
            listing.Label("WRC_MaxSpurLength".Translate(Settings.maxSpurLength), tooltip: "WRC_MaxSpurLength_Desc".Translate());
            Settings.maxSpurLength = Mathf.RoundToInt(listing.Slider(Settings.maxSpurLength, 1f, 40f));
            listing.Label("WRC_StillnessDays".Translate(Settings.stillnessDays), tooltip: "WRC_StillnessDays_Desc".Translate());
            Settings.stillnessDays = Mathf.RoundToInt(listing.Slider(Settings.stillnessDays, 5f, 180f));
            listing.Label("WRC_StepHours".Translate(Settings.stepHours), tooltip: "WRC_StepHours_Desc".Translate());
            Settings.stepHours = Mathf.RoundToInt(listing.Slider(Settings.stepHours, 1f, 72f));
            if (Prefs.DevMode)
            {
                listing.GapLine();
                listing.CheckboxLabeled("WRC_DebugFast".Translate(), ref Settings.debugFast, "WRC_DebugFast_Desc".Translate());
            }
            listing.End();
        }
    }
}
