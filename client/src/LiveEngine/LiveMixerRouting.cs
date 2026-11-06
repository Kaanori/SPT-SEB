// Scenes hold direct references to SPT's mixer in resources.assets, and the game settings write to
// it. Replacing BetterAudio.Master does not change those: without this file, the ambience keeps
// playing through a second mixer that nothing blends, next to the live one.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.SceneManagement;

namespace SoundEngineBackport.LiveEngine
{
    /// <summary>Moves the sources and serialized group fields of SPT's mixer to the live group of
    /// the same name. The menu sounds stay: GUISounds plays them on SPT's mixer outside raids.</summary>
    internal static class LiveMixerRouting
    {
        private const string GameMixerName = "MasterMixer";
        private const string AllGroups = "Master";

        private static readonly HashSet<string> GameMixerGroups = new HashSet<string>
        {
            "Master", "InGame", "Inventory", "Music", "UI", "Chat",
        };

        private static readonly Dictionary<string, AudioMixerGroup> LiveGroups = new Dictionary<string, AudioMixerGroup>();
        private static readonly Dictionary<Type, FieldInfo[]> GroupFields = new Dictionary<Type, FieldInfo[]>();
        private static readonly HashSet<string> ReportedUnknown = new HashSet<string>();

        private static AudioMixer _live;
        private static bool _subscribed;

        public static void Install(AudioMixer live)
        {
            _live = live;
            LiveGroups.Clear();
            // a parent and its child can share a name: the last one is the child, the one sources use
            foreach (AudioMixerGroup group in live.FindMatchingGroups(AllGroups))
            {
                LiveGroups[group.name] = group;
            }
            if (!_subscribed)
            {
                SceneManager.sceneLoaded += OnSceneLoaded;
                _subscribed = true;
            }

            var watch = Stopwatch.StartNew();
            int moved = 0;
            foreach (AudioSource source in Resources.FindObjectsOfTypeAll<AudioSource>())
            {
                moved += RerouteSource(source);
            }
            foreach (MonoBehaviour behaviour in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
            {
                moved += RerouteFields(behaviour);
            }
            Plugin.Log.LogInfo($"live mixer routing: {LiveGroups.Count} groups, {moved} references moved in {watch.ElapsedMilliseconds} ms");
        }

        public static void RerouteHierarchy(GameObject root)
        {
            if (_live == null)
            {
                return;
            }
            int moved = 0;
            foreach (AudioSource source in root.GetComponentsInChildren<AudioSource>(true))
            {
                moved += RerouteSource(source);
            }
            foreach (MonoBehaviour behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                moved += RerouteFields(behaviour);
            }
            if (moved > 0 && PluginConfig.LogShotDetails)
            {
                Plugin.Log.LogInfo($"[mixer] {root.name}: {moved} references moved to the live mixer");
            }
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (_live == null || !LiveMixer.Active)
            {
                return;
            }
            try
            {
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    RerouteHierarchy(root);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"live mixer routing failed in scene {scene.name}: {e}");
            }
        }

        private static int RerouteSource(AudioSource source)
        {
            AudioMixerGroup group = source.outputAudioMixerGroup;
            AudioMixerGroup live = Reroute(group);
            if (live == group)
            {
                return 0;
            }
            source.outputAudioMixerGroup = live;
            return 1;
        }

        private static int RerouteFields(MonoBehaviour behaviour)
        {
            if (behaviour == null)
            {
                return 0;
            }
            FieldInfo[] fields = GetGroupFields(behaviour.GetType());
            int moved = 0;
            for (int i = 0; i < fields.Length; i++)
            {
                var group = (AudioMixerGroup)fields[i].GetValue(behaviour);
                AudioMixerGroup live = Reroute(group);
                if (live != group)
                {
                    fields[i].SetValue(behaviour, live);
                    moved++;
                }
            }
            return moved;
        }

        private static FieldInfo[] GetGroupFields(Type type)
        {
            if (GroupFields.TryGetValue(type, out FieldInfo[] cached))
            {
                return cached;
            }
            var fields = new List<FieldInfo>();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (Type current = type; current != null && current != typeof(MonoBehaviour); current = current.BaseType)
            {
                foreach (FieldInfo field in current.GetFields(flags))
                {
                    if (field.FieldType == typeof(AudioMixerGroup))
                    {
                        fields.Add(field);
                    }
                }
            }
            FieldInfo[] result = fields.ToArray();
            GroupFields[type] = result;
            return result;
        }

        private static AudioMixerGroup Reroute(AudioMixerGroup group)
        {
            if (group == null || group.audioMixer == _live || group.audioMixer == null || group.audioMixer.name != GameMixerName)
            {
                return group;
            }
            string name = group.name;
            if (GameMixerGroups.Contains(name))
            {
                return group;
            }
            if (LiveGroups.TryGetValue(LiveMixer.MapLeafName(name), out AudioMixerGroup live))
            {
                return live;
            }
            if (ReportedUnknown.Add(name))
            {
                Plugin.Log.LogWarning($"live mixer has no group {name}: its sounds stay on SPT's mixer");
            }
            return group;
        }
    }
}
