using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using Audio.SpatialSystem;
using HarmonyLib;
using UnityEngine;

namespace SoundEngineBackport.LiveEngine
{
    /// <summary>
    /// Rooms, portals and routes of the locations: loads the live routes transcribed into SPT's
    /// scenes by tools/extract_live_acoustics.py instead of SPT's bake file.
    /// </summary>
    /// <remarks>
    /// A bake file holds, for each pair of audio rooms, the routes a sound takes between them, as
    /// IDs of the scene's rooms and portals. The tool rewrites the live file with SPT's IDs and
    /// writes <c>&lt;file&gt;.scene</c> next to it: the ID and world position of every room and
    /// portal the file uses. Before the game reads the routes, the plugin checks these against the
    /// rooms and portals the game has just registered. If one is missing or elsewhere (SPT changed
    /// the scene), the raid uses SPT's own file: wrong IDs would send sounds through other doors.
    /// The game files are never modified; only the path given to the loader changes.
    /// </remarks>
    internal static class LiveAcousticData
    {
        internal static bool Enabled = true;

        private const string Folder = "acoustics";
        private const string BakeFolder = "AudioBakeData";
        private const string CheckExtension = ".scene";
        // positions come from the same transforms: anything beyond rounding is another object
        private const float PositionTolerance = 0.05f;
        private const int Examples = 5;

        internal sealed class LoadState
        {
            internal string Map;
            internal string SptPath;
            internal string LivePath;
            internal SceneCheck Check;
            internal bool UsingLive;
            internal readonly Stopwatch Watch = new Stopwatch();
        }

        internal sealed class SceneCheck
        {
            internal int Version;
            internal long SptBakeSize = -1;
            internal readonly List<KeyValuePair<short, Vector3>> Rooms = new List<KeyValuePair<short, Vector3>>();
            internal readonly List<KeyValuePair<short, Vector3>> Portals = new List<KeyValuePair<short, Vector3>>();
        }

        private static readonly ConditionalWeakTable<SpatialAudioDataLoader, LoadState> States =
            new ConditionalWeakTable<SpatialAudioDataLoader, LoadState>();

        internal static string Dir => Path.Combine(Path.GetDirectoryName(Plugin.Location) ?? "", Folder, BakeFolder);

        internal static bool TryGetState(SpatialAudioDataLoader loader, out LoadState state)
        {
            return States.TryGetValue(loader, out state);
        }

        /// <summary>Chooses the file for this raid. Called once the loader knows SPT's path.</summary>
        internal static void Prepare(SpatialAudioDataLoader loader, string dataPath)
        {
            // the location info stores a Windows path: AudioBakeData\city_sound.audiobakedata
            string file = Path.GetFileName((dataPath ?? "").Replace('\\', '/'));
            var state = new LoadState
            {
                Map = Path.GetFileNameWithoutExtension(file),
                SptPath = loader._fullPath,
            };
            state.Watch.Start();
            States.Remove(loader);
            States.Add(loader, state);

            if (!Enabled)
            {
                Plugin.Log.LogInfo($"[acoustics] {state.Map}: SPT 4.1.6 routes (LiveAcousticDataEnabled is off)");
                return;
            }
            string live = Path.Combine(Dir, file);
            if (!File.Exists(live))
            {
                Plugin.Log.LogInfo($"[acoustics] {state.Map}: SPT 4.1.6 routes (no EFT 1.0 file for this map)");
                return;
            }
            string checkPath = live + CheckExtension;
            if (!File.Exists(checkPath))
            {
                Plugin.Log.LogWarning($"[acoustics] {state.Map}: {Path.GetFileName(checkPath)} is missing, " +
                                      "the EFT 1.0 routes can't be checked against the scene: SPT 4.1.6 routes used");
                return;
            }
            state.Check = ReadCheck(checkPath);
            state.LivePath = live;
            // the path changes only once Verify passed: if that patch doesn't run, SPT's file is read
            state.UsingLive = true;
        }

        /// <summary>
        /// Runs after the game registered the scene's rooms and portals, before it reads the file.
        /// Returns the path to read.
        /// </summary>
        internal static string Verify(LoadState state, SpatialAudioDataContainer container, string path)
        {
            if (!state.UsingLive)
            {
                return path;
            }
            var problems = new StringBuilder();
            int missing = 0, moved = 0;
            Compare("room", state.Check.Rooms, container.RoomsByID, ref missing, ref moved, problems);
            Compare("portal", state.Check.Portals, container.PortalsByID, ref missing, ref moved, problems);
            if (missing + moved > 0)
            {
                state.UsingLive = false;
                Plugin.Log.LogWarning(
                    $"[acoustics] {state.Map}: the scene doesn't match the EFT 1.0 routes ({missing} missing, {moved} moved " +
                    $"out of {state.Check.Rooms.Count} rooms and {state.Check.Portals.Count} portals). SPT 4.1.6 routes used. " +
                    $"Regenerate with tools/extract_live_acoustics.py. First differences: {problems}");
                return state.SptPath;
            }
            Plugin.Log.LogInfo(
                $"[acoustics] {state.Map}: EFT 1.0 routes, scene check passed " +
                $"({N(state.Check.Rooms.Count)} rooms and {N(state.Check.Portals.Count)} portals at their place)");
            try
            {
                long size = new FileInfo(state.SptPath).Length;
                if (state.Check.SptBakeSize >= 0 && size != state.Check.SptBakeSize)
                {
                    Plugin.Log.LogWarning(
                        $"[acoustics] {state.Map}: SPT's own bake changed since the EFT 1.0 file was made " +
                        $"({N(size)} bytes, {N(state.Check.SptBakeSize)} expected). The scene still matches, so the file is used; " +
                        "the pairs it keeps from SPT come from the old bake.");
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"[acoustics] {state.Map}: could not read SPT's bake size: {e.Message}");
            }
            return state.LivePath;
        }

        /// <summary>Summary of what the game ended up with, once the routes are in memory.</summary>
        internal static void Report(LoadState state, SpatialAudioDataContainer container)
        {
            state.Watch.Stop();
            int routes = 0, unresolved = 0, portals = 0;
            var rooms = new HashSet<short>();
            foreach (var pair in container.RoomPairsByID.Values)
            {
                rooms.Add(pair.FirstRoomID);
                rooms.Add(pair.SecondRoomID);
                foreach (var route in pair.Routes)
                {
                    routes++;
                    foreach (short id in route.PortalIDs)
                    {
                        portals++;
                        // RoomPair.Route.Read leaves 0 for an ID the scene doesn't have
                        if (id == 0)
                        {
                            unresolved++;
                        }
                    }
                }
            }
            int sceneRooms = container.RoomsByID.Count;
            int roomsWithout = 0;
            foreach (short id in container.RoomsByID.Keys)
            {
                if (!rooms.Contains(id))
                {
                    roomsWithout++;
                }
            }
            string source = state.UsingLive ? "EFT 1.0 routes" : "SPT 4.1.6 routes";
            Plugin.Log.LogInfo(
                $"[acoustics] {state.Map}: {source} loaded in {state.Watch.Elapsed.TotalSeconds:0.0} s: " +
                $"{N(container.RoomPairsByID.Count)} room pairs, {N(routes)} routes, {N(portals)} portal steps, " +
                $"{N(sceneRooms - roomsWithout)}/{N(sceneRooms)} rooms reachable from another room, {N(container.PortalsByID.Count)} portals in the scene");
            if (unresolved > 0)
            {
                Plugin.Log.LogWarning($"[acoustics] {state.Map}: {N(unresolved)} route steps point at a portal the scene doesn't have");
            }
        }

        private static void Compare<T>(string kind, List<KeyValuePair<short, Vector3>> expected, Dictionary<short, T> scene,
            ref int missing, ref int moved, StringBuilder problems) where T : Component
        {
            foreach (var entry in expected)
            {
                if (!scene.TryGetValue(entry.Key, out T obj) || obj == null)
                {
                    if (missing + moved < Examples)
                    {
                        problems.Append($"{kind} {entry.Key} missing; ");
                    }
                    missing++;
                    continue;
                }
                Vector3 pos = obj.transform.position;
                if ((pos - entry.Value).sqrMagnitude > PositionTolerance * PositionTolerance)
                {
                    if (missing + moved < Examples)
                    {
                        problems.Append($"{kind} {entry.Key} '{obj.name}' at {pos}, expected {entry.Value}; ");
                    }
                    moved++;
                }
            }
        }

        private static SceneCheck ReadCheck(string path)
        {
            var check = new SceneCheck();
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }
                string[] f = line.Split(' ');
                switch (f[0])
                {
                    case "version":
                        check.Version = int.Parse(f[1], CultureInfo.InvariantCulture);
                        break;
                    case "spt_bake_size":
                        check.SptBakeSize = long.Parse(f[1], CultureInfo.InvariantCulture);
                        break;
                    case "room":
                    case "portal":
                        var entry = new KeyValuePair<short, Vector3>(
                            short.Parse(f[1], CultureInfo.InvariantCulture),
                            new Vector3(F(f[2]), F(f[3]), F(f[4])));
                        (f[0] == "room" ? check.Rooms : check.Portals).Add(entry);
                        break;
                }
            }
            if (check.Version != 1 || check.Rooms.Count == 0)
            {
                throw new InvalidDataException($"{path}: unknown version {check.Version} or no room");
            }
            return check;
        }

        private static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

        private static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);
    }

    public static class LiveAcousticPatches
    {
        // SpatialAudioSystem creates one loader per raid, from its location info
        [HarmonyPatch(typeof(SpatialAudioDataLoader), MethodType.Constructor, typeof(string), typeof(MonoBehaviour))]
        [HarmonyPostfix]
        public static void LoaderPostfix(SpatialAudioDataLoader __instance, string dataPath)
        {
            try
            {
                LiveAcousticData.Prepare(__instance, dataPath);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogError($"[acoustics] could not prepare the EFT 1.0 routes, SPT's are used: {e}");
            }
        }

        // LoadDataAsync registers the scene's rooms and portals, then calls this to read the file
        [HarmonyPatch(typeof(SpatialAudioDataLoader), nameof(SpatialAudioDataLoader.LoadBakeDataAsync))]
        [HarmonyPrefix]
        public static void LoadBakeDataPrefix(SpatialAudioDataLoader __instance, ref string path, SpatialAudioDataContainer container)
        {
            if (!LiveAcousticData.TryGetState(__instance, out var state))
            {
                return;
            }
            try
            {
                path = LiveAcousticData.Verify(state, container, path);
            }
            catch (System.Exception e)
            {
                state.UsingLive = false;
                path = state.SptPath;
                Plugin.Log.LogError($"[acoustics] {state.Map}: scene check failed, SPT 4.1.6 routes used: {e}");
            }
            __instance._fullPath = path;
        }

        [HarmonyPatch(typeof(SpatialAudioDataLoader), nameof(SpatialAudioDataLoader.DataLoadedHandler))]
        [HarmonyPostfix]
        public static void DataLoadedPostfix(SpatialAudioDataLoader __instance, SpatialAudioDataContainer dataContainer)
        {
            try
            {
                if (dataContainer != null && !dataContainer.IsDisposed && LiveAcousticData.TryGetState(__instance, out var state))
                {
                    LiveAcousticData.Report(state, dataContainer);
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"[acoustics] summary failed: {e.Message}");
            }
        }
    }
}
