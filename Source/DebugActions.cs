using LudeonTK;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace WorldRoadCleanup
{
    public static class DebugActions
    {
        private const string Category = "World Road Cleanup";

        [DebugAction(Category, "Show candidates", allowedGameStates = AllowedGameStates.Playing)]
        private static void ShowCandidates()
        {
            // Tinted stubs: red = past the stillness period, green = still waiting. Blue: links that would change type.
            Report("Candidates", RoadCleaner.RunPass(apply: false, ignoreStillness: false, flash: true));
        }

        [DebugAction(Category, "Run one step now", allowedGameStates = AllowedGameStates.Playing)]
        private static void RunStep()
        {
            Report("Step", RoadCleaner.RunPass(apply: true, ignoreStillness: false, flash: false));
        }

        [DebugAction(Category, "Run one step, ignore stillness", allowedGameStates = AllowedGameStates.Playing)]
        private static void RunStepIgnoringStillness()
        {
            Report("Step (no stillness)", RoadCleaner.RunPass(apply: true, ignoreStillness: true, flash: false));
        }

        [DebugAction(Category, "Clean everything now", allowedGameStates = AllowedGameStates.Playing)]
        private static void CleanAll()
        {
            int removed = 0, unified = 0, passes = 0;
            PassResult result;
            do
            {
                result = RoadCleaner.RunPass(apply: true, ignoreStillness: true, flash: false);
                removed += result.removedLinks;
                unified += result.unifiedLinks;
                passes++;
            }
            while (result.Changed && passes < 10000);
            string text = $"[World Road Cleanup] Clean everything: {passes} passes, removed {removed} links, retyped {unified} links.";
            Log.Message(text);
            Messages.Message(text, MessageTypeDefOf.NeutralEvent, false);
        }

        [DebugAction(Category, "Inspect road tile", actionType = DebugActionType.ToolWorld, allowedGameStates = AllowedGameStates.PlayingOnWorld)]
        private static void InspectTile()
        {
            PlanetTile tile = GenWorld.MouseTile();
            if (!tile.Valid)
                return;
            string text = RoadCleaner.Describe(tile);
            Log.Message(text);
            Messages.Message(text, MessageTypeDefOf.NeutralEvent, false);
        }

        private static void Report(string label, PassResult result)
        {
            string text = $"[World Road Cleanup] {label}: {result}";
            Log.Message(text);
            Messages.Message(text, MessageTypeDefOf.NeutralEvent, false);
        }
    }
}
