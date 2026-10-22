using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace WorldRoadCleanup
{
    /// <summary>Result of one cleanup pass over every surface layer.</summary>
    public class PassResult
    {
        public int trackedEnds;
        public int stillEnds;
        public int longEnds;
        public int removedLinks;
        public int trackedSegments;
        public int stillSegments;
        public int unifiedSegments;
        public int unifiedLinks;
        public int straightened;
        public int merged;
        public int fans;

        public bool Changed => removedLinks > 0 || unifiedLinks > 0 || straightened > 0 || merged > 0 || fans > 0;

        public override string ToString() =>
            $"dead ends {trackedEnds} (still {stillEnds}; {longEnds} longer ones kept), removed {removedLinks} links; straightened {straightened} kinks; merged {merged} parallel; collapsed {fans} fans; " +
            $"mixed segments {trackedSegments} (still {stillSegments}), unified {unifiedSegments} ({unifiedLinks} links)";
    }

    /// <summary>
    /// Finds world-map road ends that lead nowhere and road stretches with mixed types.
    /// Both are only touched after they have stayed unchanged for the stillness period,
    /// so roads that another mod is still building (and therefore still changing) are left alone.
    /// </summary>
    public static class RoadCleaner
    {
        private struct Link
        {
            public int a;
            public int b;
            public RoadDef road;
        }

        private static readonly List<Link> walk = new List<Link>();
        private static readonly List<int> spurTiles = new List<int>();

        // Debug highlight colors, as indices into the debug spectrum (0 red, 50 green, 75 blue).
        private const int ColorStill = 0;
        private const int ColorWaiting = 50;
        private const int ColorRetype = 75;
        private const int ColorKink = 99;
        private const int ColorParallel = 25;
        // Longest stretch (in links) checked for a parallel twin.
        private const int MaxParallelLinks = 20;
        // Longest detour (in links) that is shortcut when it comes back next to where it left.
        private const int MaxDetourLinks = 6;
        private const int FlashTicks = 600;
        private static readonly Dictionary<RoadDef, int> counts = new Dictionary<RoadDef, int>();

        /// <param name="apply">False only reports and highlights what would change.</param>
        /// <param name="ignoreStillness">Treat every candidate as having been still long enough.</param>
        /// <param name="flash">Highlight candidates on the world map.</param>
        public static PassResult RunPass(bool apply, bool ignoreStillness, bool flash)
        {
            CleanupState state = CleanupState.Get();
            var result = new PassResult();
            if (state == null || Find.WorldGrid == null)
                return result;

            int now = Find.TickManager.TicksGame;
            int stillTicks = ignoreStillness ? 0 : WorldRoadCleanupMod.Settings.StillnessTicks;
            var newEnds = new Dictionary<long, int>();
            var newSegments = new Dictionary<long, SegmentRecord>();

            foreach (KeyValuePair<int, PlanetLayer> kv in Find.WorldGrid.PlanetLayers)
            {
                if (!(kv.Value is SurfaceLayer layer))
                    continue;
                HashSet<int> occupied = OccupiedTiles(layer);
                bool changed = false;
                if (WorldRoadCleanupMod.Settings.removeDeadEnds)
                    changed |= PruneDeadEnds(layer, occupied, state, newEnds, now, stillTicks, apply, flash, result);
                if (WorldRoadCleanupMod.Settings.straightenKinks)
                {
                    changed |= StraightenKinks(layer, occupied, apply, flash, result);
                    changed |= MergeParallelRoads(layer, occupied, apply, flash, result);
                    changed |= CollapseFans(layer, occupied, apply, flash, result);
                }
                if (WorldRoadCleanupMod.Settings.unifyTypes)
                    changed |= UnifySegments(layer, occupied, state, newSegments, now, stillTicks, apply, flash, result);
                if (changed)
                    Find.World.renderer.SetDirty<WorldDrawLayer_Roads>(layer);
            }

            // A dry run must not reset timers, so the tracking only moves on when changes are allowed.
            if (apply)
            {
                state.endSince = newEnds;
                state.segments = newSegments;
            }
            return result;
        }

        /// <summary>
        /// Tiles that anchor a road: stationary world objects by default, or every world object when the setting asks for it.
        /// Moving objects (caravans, travellers) would otherwise split stretches and restart their timers.
        /// </summary>
        private static HashSet<int> OccupiedTiles(PlanetLayer layer)
        {
            var set = new HashSet<int>();
            bool allObjects = WorldRoadCleanupMod.Settings.anchorAllWorldObjects;
            List<WorldObject> all = Find.WorldObjects.AllWorldObjects;
            for (int i = 0; i < all.Count; i++)
            {
                if (!allObjects && IsMoving(all[i]))
                    continue;
                PlanetTile tile = all[i].Tile;
                if (tile.Valid && tile.Layer == layer)
                    set.Add(tile.tileId);
            }
            return set;
        }

        /// <summary>
        /// Caravans and transporters move by type; any other object drawn away from its tile centre is a modded traveller mid-trip.
        /// </summary>
        private static bool IsMoving(WorldObject obj)
        {
            if (obj is Caravan || obj is TravellingTransporters)
                return true;
            if (!obj.Tile.Valid)
                return false;
            return (obj.DrawPos - Find.WorldGrid.GetTileCenter(obj.Tile)).sqrMagnitude > 0.01f;
        }

        private static bool PruneDeadEnds(PlanetLayer layer, HashSet<int> occupied, CleanupState state,
            Dictionary<long, int> newEnds, int now, int stillTicks, bool apply, bool flash, PassResult result)
        {
            int layerId = layer.LayerID;
            int maxLength = WorldRoadCleanupMod.Settings.maxSpurLength;
            var still = new List<int>();
            for (int i = 0; i < layer.TilesCount; i++)
            {
                if (!IsDeadEnd(layer, i, occupied))
                    continue;
                // Long roads into empty land read as real routes; only short stubs and fragments look broken.
                if (SpurLength(layer, i, occupied, maxLength) > maxLength)
                {
                    result.longEnds++;
                    continue;
                }
                long key = TileKey(layerId, i);
                int since = state.endSince.TryGetValue(key, out int s) ? s : now;
                newEnds[key] = since;
                result.trackedEnds++;
                if (now - since >= stillTicks)
                {
                    still.Add(i);
                    result.stillEnds++;
                }
                if (flash)
                {
                    // Translucent tint over the whole stub, so the road under it stays visible.
                    Material mat = DebugMatsSpectrum.Mat(now - since >= stillTicks ? ColorStill : ColorWaiting, transparent: true);
                    foreach (int tile in spurTiles)
                        Find.World.debugDrawer.FlashTile(layer.PlanetTileForID(tile), mat, null, FlashTicks);
                }
            }
            if (!apply)
                return false;

            bool changed = false;
            foreach (int tile in still)
            {
                // An earlier removal in this pass may have emptied this tile already.
                if (!IsDeadEnd(layer, tile, occupied))
                    continue;
                long key = TileKey(layerId, tile);
                int since = newEnds[key];
                var surface = (SurfaceTile)layer[tile];
                int next = surface.potentialRoads[0].neighbor.tileId;
                RemoveLink(layer, tile, next);
                newEnds.Remove(key);
                result.removedLinks++;
                changed = true;
                // The new end inherits the old one's age, so the spur keeps shrinking one step per pass
                // instead of waiting out the whole stillness period again.
                if (IsDeadEnd(layer, next, occupied))
                    newEnds[TileKey(layerId, next)] = since;
            }
            return changed;
        }

        /// <summary>
        /// A road that wanders off and comes back next to where it left (a sharp V where a removed branch used to
        /// meet it, or two parallel roads joined at the tip) is shortcut: the detour tiles are cut out and the two
        /// adjacent tiles joined directly. Repeated passes flatten long V shapes from the tip down.
        /// Detours are fixed shapes, not something a builder grows, so no stillness period is needed.
        /// </summary>
        private static bool StraightenKinks(PlanetLayer layer, HashSet<int> occupied, bool apply, bool flash, PassResult result)
        {
            var touched = new HashSet<int>();
            bool changed = false;
            for (int x = 0; x < layer.TilesCount; x++)
            {
                if (Degree(layer, x) == 0 || touched.Contains(x) || IsWater(layer, x))
                    continue;
                List<SurfaceTile.RoadLink> roads = ((SurfaceTile)layer[x]).potentialRoads;
                for (int r = 0; r < roads.Count; r++)
                {
                    if (!TryFindDetour(layer, x, roads[r], occupied, touched, out int y, out RoadDef road))
                        continue;
                    result.straightened++;
                    if (flash)
                    {
                        Material mat = DebugMatsSpectrum.Mat(ColorKink, transparent: true);
                        foreach (int tile in spurTiles)
                            Find.World.debugDrawer.FlashTile(layer.PlanetTileForID(tile), mat, null, FlashTicks);
                    }
                    // Mark the detour either way, so a dry run does not count the same detour from both ends.
                    touched.Add(x);
                    touched.Add(y);
                    touched.UnionWith(spurTiles);
                    if (apply)
                    {
                        int prev = x;
                        foreach (int tile in spurTiles)
                        {
                            RemoveLink(layer, prev, tile);
                            prev = tile;
                        }
                        RemoveLink(layer, prev, y);
                        PlanetTile px = layer.PlanetTileForID(x);
                        PlanetTile py = layer.PlanetTileForID(y);
                        if (Find.WorldGrid.GetRoadDef(px, py, visibleOnly: false) == null)
                            Find.WorldGrid.OverlayRoad(px, py, road);
                        changed = true;
                    }
                    // This tile's road list has changed, so move on to the next tile.
                    break;
                }
            }
            return changed;
        }

        /// <summary>
        /// A three-way junction whose middle neighbour touches both other neighbours fans three roads out of one
        /// pointed tip. The tip is dropped and the outer two roads attached to the middle one, which moves the
        /// junction one tile out. Every road still reaches the same places, and the network loses one link each
        /// time, so this cannot undo itself.
        /// </summary>
        private static bool CollapseFans(PlanetLayer layer, HashSet<int> occupied, bool apply, bool flash, PassResult result)
        {
            var touched = new HashSet<int>();
            bool changed = false;
            for (int x = 0; x < layer.TilesCount; x++)
            {
                if (Degree(layer, x) != 3 || occupied.Contains(x) || touched.Contains(x) || IsWater(layer, x))
                    continue;
                List<SurfaceTile.RoadLink> roads = ((SurfaceTile)layer[x]).potentialRoads;
                int middle = -1;
                for (int m = 0; m < 3 && middle < 0; m++)
                {
                    PlanetTile pm = roads[m].neighbor;
                    if (Find.WorldGrid.IsNeighbor(pm, roads[(m + 1) % 3].neighbor)
                        && Find.WorldGrid.IsNeighbor(pm, roads[(m + 2) % 3].neighbor))
                        middle = m;
                }
                if (middle < 0)
                    continue;
                SurfaceTile.RoadLink toMiddle = roads[middle];
                SurfaceTile.RoadLink outer1 = roads[(middle + 1) % 3];
                SurfaceTile.RoadLink outer2 = roads[(middle + 2) % 3];
                int m0 = toMiddle.neighbor.tileId, a = outer1.neighbor.tileId, c = outer2.neighbor.tileId;
                if (touched.Contains(m0) || touched.Contains(a) || touched.Contains(c) || IsWater(layer, m0))
                    continue;
                result.fans++;
                if (flash)
                    Find.World.debugDrawer.FlashTile(layer.PlanetTileForID(x), DebugMatsSpectrum.Mat(ColorKink, transparent: true), null, FlashTicks);
                touched.Add(x);
                touched.Add(m0);
                touched.Add(a);
                touched.Add(c);
                if (!apply)
                    continue;
                RemoveLink(layer, x, m0);
                RemoveLink(layer, x, a);
                RemoveLink(layer, x, c);
                if (Find.WorldGrid.GetRoadDef(toMiddle.neighbor, outer1.neighbor, visibleOnly: false) == null)
                    Find.WorldGrid.OverlayRoad(toMiddle.neighbor, outer1.neighbor, outer1.road);
                if (Find.WorldGrid.GetRoadDef(toMiddle.neighbor, outer2.neighbor, visibleOnly: false) == null)
                    Find.WorldGrid.OverlayRoad(toMiddle.neighbor, outer2.neighbor, outer2.road);
                changed = true;
            }
            return changed;
        }

        /// <summary>
        /// Two stretches between the same pair of junctions where one runs right alongside the other (every tile of
        /// it touches the other road) look like a single doubled road. The longer one (or the lower road type on a
        /// tie) is removed; the other still connects the two junctions. One pair per junction pair per pass.
        /// </summary>
        private static bool MergeParallelRoads(PlanetLayer layer, HashSet<int> occupied, bool apply, bool flash, PassResult result)
        {
            int layerId = layer.LayerID;
            var visited = new HashSet<long>();
            var byEnds = new Dictionary<long, List<List<Link>>>();
            var looseSpurs = new List<List<Link>>();
            for (int i = 0; i < layer.TilesCount; i++)
            {
                if (Degree(layer, i) == 0 || !IsBreak(layer, i, occupied))
                    continue;
                List<SurfaceTile.RoadLink> roads = ((SurfaceTile)layer[i]).potentialRoads;
                for (int r = 0; r < roads.Count; r++)
                {
                    if (visited.Contains(LinkKey(layerId, i, roads[r].neighbor.tileId)))
                        continue;
                    Walk(layer, i, roads[r], occupied, visited, out int end);
                    if (end == i)
                        continue;
                    // A long spur that ends nowhere but runs right alongside another road is a doubled road too.
                    // Short ones are left to the dead-end pruning, which waits out the stillness period.
                    if (IsLooseEnd(layer, i, occupied) || IsLooseEnd(layer, end, occupied))
                    {
                        if (walk.Count > WorldRoadCleanupMod.Settings.maxSpurLength)
                            looseSpurs.Add(new List<Link>(walk));
                        continue;
                    }
                    if (walk.Count > MaxParallelLinks)
                        continue;
                    long key = LinkKey(layerId, i, end);
                    if (!byEnds.TryGetValue(key, out List<List<Link>> list))
                        byEnds[key] = list = new List<List<Link>>();
                    list.Add(new List<Link>(walk));
                }
            }

            var touched = new HashSet<int>();
            bool changed = false;
            foreach (List<Link> spur in looseSpurs)
            {
                if (AnyTouched(spur, touched) || !SpurHugsOtherRoads(layer, spur, occupied))
                    continue;
                result.merged++;
                if (flash)
                {
                    Material mat = DebugMatsSpectrum.Mat(ColorParallel, transparent: true);
                    foreach (Link link in spur)
                    {
                        Find.World.debugDrawer.FlashTile(layer.PlanetTileForID(link.a), mat, null, FlashTicks);
                        Find.World.debugDrawer.FlashTile(layer.PlanetTileForID(link.b), mat, null, FlashTicks);
                    }
                }
                foreach (Link link in spur)
                {
                    touched.Add(link.a);
                    touched.Add(link.b);
                }
                if (!apply)
                    continue;
                foreach (Link link in spur)
                    RemoveLink(layer, link.a, link.b);
                changed = true;
            }
            foreach (List<List<Link>> group in byEnds.Values)
            {
                if (group.Count < 2)
                    continue;
                bool done = false;
                for (int p = 0; p < group.Count && !done; p++)
                {
                    for (int q = p + 1; q < group.Count && !done; q++)
                    {
                        List<Link> remove = group[p], keep = group[q];
                        if (remove.Count < keep.Count || (remove.Count == keep.Count && MaxPriority(remove) > MaxPriority(keep)))
                            (remove, keep) = (keep, remove);
                        if (AnyTouched(remove, touched) || AnyTouched(keep, touched) || !Hugs(layer, remove, keep))
                            continue;
                        done = true;
                        result.merged++;
                        if (flash)
                        {
                            Material mat = DebugMatsSpectrum.Mat(ColorParallel, transparent: true);
                            for (int i = 0; i < remove.Count - 1; i++)
                                Find.World.debugDrawer.FlashTile(layer.PlanetTileForID(remove[i].b), mat, null, FlashTicks);
                        }
                        foreach (Link link in remove)
                        {
                            touched.Add(link.a);
                            touched.Add(link.b);
                        }
                        foreach (Link link in keep)
                        {
                            touched.Add(link.a);
                            touched.Add(link.b);
                        }
                        if (!apply)
                            continue;
                        foreach (Link link in remove)
                            RemoveLink(layer, link.a, link.b);
                        changed = true;
                    }
                }
            }
            return changed;
        }

        /// <summary>True when every inner tile of <paramref name="side"/> is on or next to <paramref name="other"/>.</summary>
        private static bool Hugs(PlanetLayer layer, List<Link> side, List<Link> other)
        {
            var otherTiles = new List<PlanetTile>();
            foreach (Link link in other)
            {
                otherTiles.Add(layer.PlanetTileForID(link.a));
                otherTiles.Add(layer.PlanetTileForID(link.b));
            }
            for (int i = 0; i < side.Count - 1; i++)
            {
                int inner = side[i].b;
                if (IsWater(layer, inner))
                    return false;
                PlanetTile tile = layer.PlanetTileForID(inner);
                bool near = false;
                foreach (PlanetTile o in otherTiles)
                {
                    if (o == tile || Find.WorldGrid.IsNeighbor(o, tile))
                    {
                        near = true;
                        break;
                    }
                }
                if (!near)
                    return false;
            }
            return true;
        }

        /// <summary>
        /// True when every tile of the spur except the junction it hangs from borders some road tile outside the spur.
        /// </summary>
        private static bool SpurHugsOtherRoads(PlanetLayer layer, List<Link> spur, HashSet<int> occupied)
        {
            var own = new HashSet<int>();
            foreach (Link link in spur)
            {
                own.Add(link.a);
                own.Add(link.b);
            }
            var neighbours = new List<PlanetTile>();
            foreach (int tile in own)
            {
                // The junction (or object) the spur hangs from is shared with the other road by definition.
                if (!IsLooseEnd(layer, tile, occupied) && IsBreak(layer, tile, occupied))
                    continue;
                if (IsWater(layer, tile))
                    return false;
                Find.WorldGrid.GetTileNeighbors(layer.PlanetTileForID(tile), neighbours);
                bool near = false;
                foreach (PlanetTile n in neighbours)
                {
                    if (!own.Contains(n.tileId) && Degree(layer, n.tileId) > 0)
                    {
                        near = true;
                        break;
                    }
                }
                if (!near)
                    return false;
            }
            return true;
        }

        private static int MaxPriority(List<Link> links)
        {
            int max = int.MinValue;
            foreach (Link link in links)
                max = System.Math.Max(max, link.road.priority);
            return max;
        }

        private static bool AnyTouched(List<Link> links, HashSet<int> touched)
        {
            foreach (Link link in links)
            {
                if (touched.Contains(link.a) || touched.Contains(link.b))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Walks from <paramref name="x"/> along <paramref name="first"/> through plain road tiles and stops at the
        /// first tile <paramref name="y"/> (two or more links away) that borders <paramref name="x"/>. The tiles in
        /// between are left in <see cref="spurTiles"/>; <paramref name="road"/> is the highest road type on the way.
        /// </summary>
        private static bool TryFindDetour(PlanetLayer layer, int x, SurfaceTile.RoadLink first, HashSet<int> occupied,
            HashSet<int> touched, out int y, out RoadDef road)
        {
            spurTiles.Clear();
            y = -1;
            road = first.road;
            PlanetTile px = layer.PlanetTileForID(x);
            int prev = x;
            int cur = first.neighbor.tileId;
            for (int links = 1; links < MaxDetourLinks; links++)
            {
                if (Degree(layer, cur) != 2 || occupied.Contains(cur) || touched.Contains(cur) || IsWater(layer, cur))
                    return false;
                spurTiles.Add(cur);
                List<SurfaceTile.RoadLink> roads = ((SurfaceTile)layer[cur]).potentialRoads;
                SurfaceTile.RoadLink next = roads[0].neighbor.tileId == prev ? roads[1] : roads[0];
                if (next.road.priority > road.priority)
                    road = next.road;
                prev = cur;
                cur = next.neighbor.tileId;
                if (cur == x || touched.Contains(cur) || IsWater(layer, cur))
                    return false;
                if (Find.WorldGrid.IsNeighbor(px, layer.PlanetTileForID(cur)))
                {
                    y = cur;
                    return true;
                }
            }
            return false;
        }

        private static bool IsDeadEnd(PlanetLayer layer, int tile, HashSet<int> occupied)
        {
            if (!(layer[tile] is SurfaceTile surface) || surface.potentialRoads == null || surface.potentialRoads.Count != 1)
                return false;
            return !occupied.Contains(tile);
        }

        private static bool UnifySegments(PlanetLayer layer, HashSet<int> occupied, CleanupState state,
            Dictionary<long, SegmentRecord> newSegments, int now, int stillTicks, bool apply, bool flash, PassResult result)
        {
            int layerId = layer.LayerID;
            var visited = new HashSet<long>();
            bool changed = false;

            // Stretches that start and end at a junction, a road end or an occupied tile.
            for (int i = 0; i < layer.TilesCount; i++)
            {
                if (Degree(layer, i) == 0 || !IsBreak(layer, i, occupied))
                    continue;
                List<SurfaceTile.RoadLink> roads = ((SurfaceTile)layer[i]).potentialRoads;
                for (int r = 0; r < roads.Count; r++)
                {
                    if (visited.Contains(LinkKey(layerId, i, roads[r].neighbor.tileId)))
                        continue;
                    Walk(layer, i, roads[r], occupied, visited, out int end);
                    changed |= HandleSegment(layer, occupied, state, newSegments, now, stillTicks, apply, flash, result, i, end);
                }
            }
            // Loops without any junction.
            for (int i = 0; i < layer.TilesCount; i++)
            {
                if (Degree(layer, i) != 2)
                    continue;
                SurfaceTile.RoadLink first = ((SurfaceTile)layer[i]).potentialRoads[0];
                if (visited.Contains(LinkKey(layerId, i, first.neighbor.tileId)))
                    continue;
                Walk(layer, i, first, occupied, visited, out int end);
                changed |= HandleSegment(layer, occupied, state, newSegments, now, stillTicks, apply, flash, result, i, end);
            }
            return changed;
        }

        /// <summary>Follows the road from <paramref name="start"/> until the next break tile; fills <see cref="walk"/>.</summary>
        private static void Walk(PlanetLayer layer, int start, SurfaceTile.RoadLink firstLink, HashSet<int> occupied,
            HashSet<long> visited, out int end)
        {
            int layerId = layer.LayerID;
            walk.Clear();
            int prev = start;
            int cur = firstLink.neighbor.tileId;
            walk.Add(new Link { a = prev, b = cur, road = firstLink.road });
            visited.Add(LinkKey(layerId, prev, cur));
            while (cur != start && !IsBreak(layer, cur, occupied))
            {
                List<SurfaceTile.RoadLink> roads = ((SurfaceTile)layer[cur]).potentialRoads;
                SurfaceTile.RoadLink next = roads[0].neighbor.tileId == prev ? roads[1] : roads[0];
                prev = cur;
                cur = next.neighbor.tileId;
                walk.Add(new Link { a = prev, b = cur, road = next.road });
                visited.Add(LinkKey(layerId, prev, cur));
            }
            end = cur;
        }

        private static bool HandleSegment(PlanetLayer layer, HashSet<int> occupied, CleanupState state,
            Dictionary<long, SegmentRecord> newSegments, int now, int stillTicks, bool apply, bool flash, PassResult result,
            int start, int end)
        {
            // A short spur ending nowhere is the dead-end pruning's job; long ones are kept, so they get evened out too.
            if ((IsLooseEnd(layer, start, occupied) || IsLooseEnd(layer, end, occupied))
                && walk.Count <= WorldRoadCleanupMod.Settings.maxSpurLength)
                return false;

            counts.Clear();
            long key = long.MaxValue;
            ulong print = 0;
            for (int i = 0; i < walk.Count; i++)
            {
                Link link = walk[i];
                if (IsWater(layer, link.a) || IsWater(layer, link.b))
                    return false;
                counts[link.road] = counts.TryGetValue(link.road, out int c) ? c + 1 : 1;
                long linkKey = LinkKey(layer.LayerID, link.a, link.b);
                if (linkKey < key)
                    key = linkKey;
                print += Mix((ulong)linkKey ^ ((ulong)link.road.shortHash << 48));
            }
            if (counts.Count < 2)
                return false;

            int since = state.segments.TryGetValue(key, out SegmentRecord old) && old.print == (long)print ? old.since : now;
            newSegments[key] = new SegmentRecord { print = (long)print, since = since };
            result.trackedSegments++;
            bool isStill = now - since >= stillTicks;
            if (isStill)
                result.stillSegments++;

            RoadDef majority = null;
            int best = 0;
            bool tie = false;
            foreach (KeyValuePair<RoadDef, int> kv in counts)
            {
                if (kv.Value > best)
                {
                    majority = kv.Key;
                    best = kv.Value;
                    tie = false;
                }
                else if (kv.Value == best)
                    tie = true;
            }
            if (flash)
            {
                Material mat = DebugMatsSpectrum.Mat(ColorRetype, transparent: true);
                foreach (Link link in walk)
                {
                    if (link.road == majority || tie)
                        continue;
                    Find.World.debugDrawer.FlashTile(layer.PlanetTileForID(link.a), mat, null, FlashTicks);
                    Find.World.debugDrawer.FlashTile(layer.PlanetTileForID(link.b), mat, null, FlashTicks);
                }
            }
            if (tie || !isStill || !apply)
                return false;

            foreach (Link link in walk)
            {
                if (link.road == majority)
                    continue;
                RemoveLink(layer, link.a, link.b);
                Find.WorldGrid.OverlayRoad(layer.PlanetTileForID(link.a), layer.PlanetTileForID(link.b), majority);
                result.unifiedLinks++;
            }
            newSegments.Remove(key);
            result.unifiedSegments++;
            return true;
        }

        /// <summary>
        /// Links from a road end to the next junction, occupied tile or other end; stops counting past <paramref name="max"/>.
        /// The tiles that would be removed (all but the last) are left in <see cref="spurTiles"/>.
        /// </summary>
        private static int SpurLength(PlanetLayer layer, int tip, HashSet<int> occupied, int max)
        {
            spurTiles.Clear();
            spurTiles.Add(tip);
            int prev = tip;
            int cur = ((SurfaceTile)layer[tip]).potentialRoads[0].neighbor.tileId;
            int length = 1;
            while (length <= max && !IsBreak(layer, cur, occupied))
            {
                spurTiles.Add(cur);
                List<SurfaceTile.RoadLink> roads = ((SurfaceTile)layer[cur]).potentialRoads;
                int next = roads[0].neighbor.tileId == prev ? roads[1].neighbor.tileId : roads[0].neighbor.tileId;
                prev = cur;
                cur = next;
                length++;
            }
            return length;
        }

        /// <summary>Debug text: the tile's road links, world objects, and whether its road neighbours touch each other.</summary>
        public static string Describe(PlanetTile tile)
        {
            PlanetLayer layer = tile.Layer;
            var sb = new System.Text.StringBuilder();
            sb.Append($"[World Road Cleanup] tile {tile.tileId}: {DescribeOne(layer, tile.tileId)}");
            if (layer[tile.tileId] is SurfaceTile surface && surface.potentialRoads != null)
            {
                List<SurfaceTile.RoadLink> roads = surface.potentialRoads;
                for (int i = 0; i < roads.Count; i++)
                    sb.Append($"\n  -> {roads[i].neighbor.tileId} ({roads[i].road.defName}): {DescribeOne(layer, roads[i].neighbor.tileId)}");
                for (int i = 0; i < roads.Count; i++)
                {
                    for (int j = i + 1; j < roads.Count; j++)
                    {
                        if (Find.WorldGrid.IsNeighbor(roads[i].neighbor, roads[j].neighbor))
                            sb.Append($"\n  neighbours {roads[i].neighbor.tileId} and {roads[j].neighbor.tileId} touch (sharp corner)");
                    }
                }
                HashSet<int> occupied = OccupiedTiles(layer);
                if (IsBreak(layer, tile.tileId, occupied))
                {
                    for (int i = 0; i < roads.Count; i++)
                    {
                        Walk(layer, tile.tileId, roads[i], occupied, new HashSet<long>(), out int end);
                        DescribeSegment(sb, layer, occupied, tile.tileId, end);
                    }
                }
                else
                {
                    // Walk to one end of the stretch first, then back over all of it.
                    Walk(layer, tile.tileId, roads[0], occupied, new HashSet<long>(), out int start);
                    if (start == tile.tileId)
                    {
                        DescribeSegment(sb, layer, occupied, start, start);
                    }
                    else
                    {
                        int back = walk[walk.Count - 1].a;
                        SurfaceTile.RoadLink link = ((SurfaceTile)layer[start]).potentialRoads.Find(l => l.neighbor.tileId == back);
                        Walk(layer, start, link, occupied, new HashSet<long>(), out int end);
                        DescribeSegment(sb, layer, occupied, start, end);
                    }
                }
            }
            return sb.ToString();
        }

        /// <summary>Debug text for the stretch in <see cref="walk"/>: type counts and why it is or is not retyped.</summary>
        private static void DescribeSegment(System.Text.StringBuilder sb, PlanetLayer layer, HashSet<int> occupied, int start, int end)
        {
            var typeCounts = new Dictionary<string, int>();
            bool water = false;
            long key = long.MaxValue;
            ulong print = 0;
            foreach (Link link in walk)
            {
                water |= IsWater(layer, link.a) || IsWater(layer, link.b);
                typeCounts[link.road.defName] = typeCounts.TryGetValue(link.road.defName, out int c) ? c + 1 : 1;
                long linkKey = LinkKey(layer.LayerID, link.a, link.b);
                if (linkKey < key)
                    key = linkKey;
                print += Mix((ulong)linkKey ^ ((ulong)link.road.shortHash << 48));
            }
            sb.Append($"\n  segment {start} .. {end}: {walk.Count} links, types [{string.Join(", ", typeCounts.Select(kv => $"{kv.Key} {kv.Value}"))}]");
            sb.Append($"\n    start: {DescribeOne(layer, start)}; end: {DescribeOne(layer, end)}");

            bool looseEnd = IsLooseEnd(layer, start, occupied) || IsLooseEnd(layer, end, occupied);
            int best = typeCounts.Values.Max();
            string verdict;
            if (looseEnd && walk.Count <= WorldRoadCleanupMod.Settings.maxSpurLength)
                verdict = "skipped: short stretch to a loose end (dead-end pruning's job)";
            else if (water)
                verdict = "skipped: crosses water";
            else if (typeCounts.Count < 2)
                verdict = "single type, nothing to do";
            else if (typeCounts.Values.Count(v => v == best) > 1)
                verdict = "skipped: tie between the most common types";
            else
            {
                CleanupState state = CleanupState.Get();
                int now = Find.TickManager.TicksGame;
                int stillTicks = WorldRoadCleanupMod.Settings.StillnessTicks;
                if (state != null && state.segments.TryGetValue(key, out SegmentRecord old) && old.print == (long)print)
                {
                    float days = (now - old.since) / (float)GenDate.TicksPerDay;
                    float needed = stillTicks / (float)GenDate.TicksPerDay;
                    verdict = now - old.since >= stillTicks
                        ? $"due: unchanged for {days:F1} days, retyped on the next step"
                        : $"waiting: unchanged for {days:F1} of {needed:F1} days";
                }
                else
                {
                    verdict = "not tracked yet (new or changed since the last step), timer starts on the next step";
                }
            }
            sb.Append($"\n    {verdict}");
        }

        private static string DescribeOne(PlanetLayer layer, int tileId)
        {
            PlanetTile tile = layer.PlanetTileForID(tileId);
            var objects = new List<string>();
            foreach (WorldObject o in Find.WorldObjects.ObjectsAt(tile))
                objects.Add(o.def.defName);
            return $"degree {Degree(layer, tileId)}, water {IsWater(layer, tileId)}, objects [{string.Join(", ", objects)}]";
        }

        private static int Degree(PlanetLayer layer, int tile) =>
            layer[tile] is SurfaceTile surface && surface.potentialRoads != null ? surface.potentialRoads.Count : 0;

        private static bool IsBreak(PlanetLayer layer, int tile, HashSet<int> occupied) =>
            Degree(layer, tile) != 2 || occupied.Contains(tile);

        private static bool IsLooseEnd(PlanetLayer layer, int tile, HashSet<int> occupied) =>
            Degree(layer, tile) == 1 && !occupied.Contains(tile);

        private static bool IsWater(PlanetLayer layer, int tile) => layer[tile].WaterCovered;

        /// <summary>
        /// Removes the link from both tiles. An emptied list must go back to null: the game assumes
        /// non-null means non-empty, and the world inspect pane throws on an empty list.
        /// </summary>
        private static void RemoveLink(PlanetLayer layer, int a, int b)
        {
            PlanetTile ta = layer.PlanetTileForID(a);
            PlanetTile tb = layer.PlanetTileForID(b);
            var sa = (SurfaceTile)layer[a];
            var sb = (SurfaceTile)layer[b];
            if (sa.potentialRoads != null)
            {
                sa.potentialRoads.RemoveAll(l => l.neighbor == tb);
                if (sa.potentialRoads.Count == 0)
                    sa.potentialRoads = null;
            }
            if (sb.potentialRoads != null)
            {
                sb.potentialRoads.RemoveAll(l => l.neighbor == ta);
                if (sb.potentialRoads.Count == 0)
                    sb.potentialRoads = null;
            }
        }

        public static long TileKey(int layerId, int tile) => ((long)layerId << 32) | (uint)tile;

        private static long LinkKey(int layerId, int a, int b)
        {
            if (a > b)
                (a, b) = (b, a);
            return ((long)layerId << 48) | ((long)a << 24) | (uint)b;
        }

        private static ulong Mix(ulong x)
        {
            x += 0x9E3779B97F4A7C15UL;
            x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
            x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
            return x ^ (x >> 31);
        }
    }
}
