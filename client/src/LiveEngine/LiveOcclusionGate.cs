// Live CombinedSpatialAudioOccluder.OnUpdateOcclusionEffects: a source stays silent and
// filtered until the occlusion of its position is known, and while it is out of hearing
// range. SPT lets it play unfiltered in the meantime, which is heard through walls.
using System.Runtime.CompilerServices;
using Audio.SpatialSystem;

namespace SoundEngineBackport.LiveEngine
{
    internal static class LiveOcclusionGate
    {
        private const float HearingRangeOffset = 5f;
        private const float ClosedLowPass = 20f;

        private sealed class State
        {
            public bool Calculated;
            public bool ScoreReady;
        }

        private static readonly ConditionalWeakTable<CombinedSpatialAudioOccluder, State> States =
            new ConditionalWeakTable<CombinedSpatialAudioOccluder, State>();

        internal static bool Enabled = true;

        public static void Reset(CombinedSpatialAudioOccluder occluder)
        {
            State state = States.GetOrCreateValue(occluder);
            state.Calculated = false;
            state.ScoreReady = false;
        }

        /// <summary>SPT has no ScoreUpdated flag: the calculators have run once the late
        /// update of the occluder has passed.</summary>
        public static void MarkCalculated(CombinedSpatialAudioOccluder occluder)
        {
            States.GetOrCreateValue(occluder).Calculated = true;
        }

        /// <summary>False when the source must stay closed and SPT's update must not run.</summary>
        public static bool BeforeEffects(CombinedSpatialAudioOccluder occluder, AudioSourceContainer container, ref bool updateImmediately)
        {
            if (!Enabled)
            {
                return true;
            }
            State state = States.GetOrCreateValue(occluder);
            if (container.IsOutOfHearingRangeWithOffset(HearingRangeOffset) || !state.Calculated)
            {
                HoldClosed(container);
                return false;
            }
            if (!state.ScoreReady)
            {
                // the first result applies at once instead of fading in from nothing
                occluder._smoothedPropagationEffect = occluder._propagationCalculator.GetScore();
                occluder._smoothedObstructionEffect = occluder._obstructionCalculator.GetScore();
                state.ScoreReady = true;
                updateImmediately = true;
            }
            return true;
        }

        private static void HoldClosed(AudioSourceContainer container)
        {
            for (int i = 0; i < container.Count; i++)
            {
                BetterSource source = container[i];
                source.SetOcclusionVolumeFactor(0f);
                source.SetOcclusionRolloffScale(1f);
                source.SetLowPassFilterParameters(ClosedLowPass, 1f, true);
            }
        }
    }
}
