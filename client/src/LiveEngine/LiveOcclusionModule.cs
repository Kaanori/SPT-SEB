// LiveOcclusionModule: optional extra muffling, off by default. It does not exist on live:
// the cutoff calculation and the gunshot EQ presets are the same on both games. It stays
// for those who prefer a heavier muffling, set by ear.
using System;
using Audio.Effects;
using EFT.CameraControl;
using HarmonyLib;
using UnityEngine;

namespace SoundEngineBackport.LiveEngine
{
    public static class LiveOcclusionModule
    {
        private static readonly AccessTools.FieldRef<AudioEQFilterBase, float> TargetLowPassRef =
            AccessTools.FieldRefAccess<AudioEQFilterBase, float>("_targetLpf");
        private static readonly AccessTools.FieldRef<AudioEQFilterBase, float> TargetLowPassQRef =
            AccessTools.FieldRefAccess<AudioEQFilterBase, float>("_targetLpfQ");

        // config-driven (set by Plugin on load)
        public static float MuffleStrength;   // 0 = same as live, 1 = strongest
        public static float MinCutoffFrequency = 500f;
        public static bool Enabled = true;
        public static bool LogCutoffChanges;
        private static int _logCounter;

        // the extra muffling is meant for distant sounds behind walls: a nearby source
        // keeps the muffling computed by SPT alone, then the extra part fades in
        private const float CloseRangeStart = 15f;
        private const float CloseRangeEnd = 40f;

        /// <summary>
        /// Postfix of SpatialAudioOccluder.ApplyOcclusionToSource: deepens the low-pass the
        /// system just asked for. Runs every frame per occluded source.
        /// </summary>
        public static void Postfix(BetterSource source, bool updateImmediately)
        {
            if (!Enabled || MuffleStrength <= 0f || source == null)
            {
                return;
            }
            try
            {
                float factor = source.OcclusionVolumeFactor;
                float effect = 1f - Mathf.Clamp01(factor);
                if (effect <= 0.15f)
                {
                    return;
                }
                AudioEQFilterBase filter = source.source1 != null ? source.source1.GetComponent<AudioEQFilterBase>() : null;
                if (filter == null)
                {
                    return;
                }
                // the target, not the live filter value: when the system smooths (no immediate
                // update) the filter still holds the cutoff we already lowered last time
                float requested = TargetLowPassRef(filter);
                float strength = Mathf.Clamp01((effect - 0.15f) / 0.85f);
                float distance = CameraManager.Instance.Distance(source.Position);
                strength = Mathf.Pow(strength, 1.5f) * Mathf.Clamp01(MuffleStrength)
                    * Mathf.InverseLerp(CloseRangeStart, CloseRangeEnd, distance);
                float target = Mathf.Max(MinCutoffFrequency, Mathf.Lerp(requested, MinCutoffFrequency, strength));
                target = Mathf.Min(target, requested);
                if (LogCutoffChanges && Mathf.Abs(requested - target) > 300f && (++_logCounter % 200) == 0)
                {
                    SoundEngineBackport.Plugin.Log.LogWarning(
                        $"[occl] {(source.source1.clip != null ? source.source1.clip.name : source.name)} " +
                        $"cutoff {requested:F0}->{target:F0}Hz effect={effect:F2} strength={strength:F2} volFactor={factor:F2}");
                }
                source.SetLowPassFilterParameters(target, TargetLowPassQRef(filter), updateImmediately);
            }
            catch (Exception)
            {
                // never break the occlusion chain
            }
        }
    }
}
