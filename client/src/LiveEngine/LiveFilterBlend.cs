// What live does with the filters of the gunshot and grenade sources and SPT does not: a
// spatializer request (front, behind, below the listener) blended with the occlusion request.
// SPT's occluder keeps calling BetterSource.SetLowPass/HighPassFilterParameters: for the
// sources of this engine, these calls become the occlusion side of the blend.
using System.Collections.Generic;
using Audio.Effects;
using HarmonyLib;
using UnityEngine;

namespace SoundEngineBackport.LiveEngine
{
    internal sealed class LiveFilterBlend
    {
        private const float ApplyFrequencyEpsilon = 1f;
        private const float ApplyQEpsilon = 0.001f;

        private static readonly Dictionary<BetterSource, LiveFilterBlend> Registry = new Dictionary<BetterSource, LiveFilterBlend>();
        private static readonly AccessTools.FieldRef<BetterSource, IAudioEQFilter[]> FiltersRef =
            AccessTools.FieldRefAccess<BetterSource, IAudioEQFilter[]>("_audioFilters");

        private static readonly AccessTools.FieldRef<BetterSource, bool> IncludedInOcclusionRef =
            AccessTools.FieldRefAccess<BetterSource, bool>("IncludedInOcclusionProcess");

        private static readonly List<LiveFilterBlend> Grenades = new List<LiveFilterBlend>();

        internal static bool Enabled = true;

        private readonly BetterSource _source;
        private readonly FilterBlend _blend = new FilterBlend();
        private bool _spatialEnabled;
        private FilterTargets _applied;

        private LiveFilterBlend(BetterSource source)
        {
            _source = source;
            _applied.LowPass = float.NegativeInfinity;
        }

        public static LiveFilterBlend Register(BetterSource source)
        {
            if (!Enabled || source == null)
            {
                return null;
            }
            var blend = new LiveFilterBlend(source);
            Registry[source] = blend;
            return blend;
        }

        public static void Unregister(BetterSource source)
        {
            // a destroyed source compares equal to null but its key must still go
            if (!ReferenceEquals(source, null) && Registry.Remove(source) && source != null)
            {
                source.ResetFilters();
            }
        }

        /// <summary>Patch of BetterAudio.GetSource: live also creates this group with the
        /// prefab of the simple spatializer. Nothing else holds these sources, so they are
        /// followed until they go back to their pool.</summary>
        public static void TrackGrenadeSource(BetterSource source)
        {
            if (Registry.ContainsKey(source))
            {
                return;
            }
            LiveFilterBlend blend = Register(source);
            if (blend != null)
            {
                blend.PrepareShot(true);
                blend.ApplyNow();
                Grenades.Add(blend);
            }
        }

        public static void TickGrenades()
        {
            for (int i = Grenades.Count - 1; i >= 0; i--)
            {
                LiveFilterBlend blend = Grenades[i];
                if (blend._source == null || !blend._source.gameObject.activeSelf)
                {
                    Grenades.RemoveAt(i);
                    Unregister(blend._source);
                }
                else
                {
                    blend.Update();
                }
            }
        }

        /// <summary>Patch of SetLowPassFilterParameters: true when the call was consumed.</summary>
        public static bool TryInterceptLowPass(BetterSource source, float frequency, float q, bool immediately)
        {
            if (!Registry.TryGetValue(source, out LiveFilterBlend blend))
            {
                return false;
            }
            if (IncludedInOcclusionRef(source))
            {
                blend._blend.SetOccluderLowPass(frequency, q);
                blend.Apply(immediately);
            }
            return true;
        }

        /// <summary>Patch of SetHighPassFilterParameters: true when the call was consumed.</summary>
        public static bool TryInterceptHighPass(BetterSource source, float frequency, float q, bool immediately)
        {
            if (!Registry.TryGetValue(source, out LiveFilterBlend blend))
            {
                return false;
            }
            if (IncludedInOcclusionRef(source))
            {
                blend._blend.SetOccluderHighPass(frequency, q);
                blend.Apply(immediately);
            }
            return true;
        }

        /// <summary>Live clears the occlusion request of a source that is not occluded.</summary>
        public static void ClearOcclusionIfRegistered(BetterSource source)
        {
            if (Registry.TryGetValue(source, out LiveFilterBlend blend))
            {
                blend._blend.ClearOccluder();
                blend.Apply(false);
            }
        }

        /// <summary>Before a shot: first-person shots are stereo and skip the spatializer.</summary>
        public void PrepareShot(bool spatialEnabled)
        {
            _spatialEnabled = spatialEnabled;
            _blend.ClearOccluder();
            if (!spatialEnabled)
            {
                _blend.ClearSpatial();
            }
            Update();
        }

        public void Update()
        {
            if (_spatialEnabled && _source != null)
            {
                BetterAudio betterAudio = MonoBehaviourSingleton<BetterAudio>.Instance;
                Transform listener = betterAudio != null ? betterAudio.ListenerTransform : null;
                if (listener != null)
                {
                    Vector3 direction = _source.Position - listener.position;
                    Vector3 forward = listener.forward;
                    direction = direction.sqrMagnitude > 0.000001f ? direction.normalized : forward;
                    _blend.SetSpatial(LiveAudioMath.EvaluateSpatializer(
                        direction.x, direction.y, direction.z, forward.x, forward.y, forward.z));
                }
            }
            Apply(false);
        }

        /// <summary>The shot starts with the right filter instead of gliding to it.</summary>
        public void ApplyNow() => Apply(true);

        private void Apply(bool immediately)
        {
            if (_source == null)
            {
                return;
            }
            IAudioEQFilter[] filters = FiltersRef(_source);
            if (filters == null)
            {
                return;
            }
            FilterTargets targets = _blend.Compute();
            bool lowPassChanged = Mathf.Abs(targets.LowPass - _applied.LowPass) > ApplyFrequencyEpsilon
                || Mathf.Abs(targets.LowPassQ - _applied.LowPassQ) > ApplyQEpsilon;
            bool highPassChanged = Mathf.Abs(targets.HighPass - _applied.HighPass) > ApplyFrequencyEpsilon
                || Mathf.Abs(targets.HighPassQ - _applied.HighPassQ) > ApplyQEpsilon;
            for (int i = 0; i < filters.Length; i++)
            {
                if (lowPassChanged || immediately)
                {
                    filters[i].SetTargetLowPassSettings(targets.LowPass, targets.LowPassQ, immediately);
                }
                if (highPassChanged || immediately)
                {
                    filters[i].SetTargetHighPassSettings(targets.HighPass, targets.HighPassQ, immediately);
                }
            }
            _applied = targets;
        }
    }
}
