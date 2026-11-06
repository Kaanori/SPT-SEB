// Pure timing maths of the gunshot engine (no Unity dependency, so it can be
// exercised outside the game).
using System;

namespace SoundEngineBackport.LiveEngine
{
    internal static class ShotTiming
    {
        private const double MinValidBeatDuration = 0.001;
        private const double MinLeadSeconds = 0.03;

        /// <summary>Largest amount a shot may be held back to stay on the metronome.</summary>
        public static double MaxLead(double beatDuration)
        {
            return Math.Max(MinLeadSeconds, beatDuration * 0.5);
        }

        /// <summary>
        /// DSP start time of a shot: previous shot + one beat when that stays close to
        /// the moment the shot should be heard, otherwise the moment itself.
        ///
        /// Deviation from live, which rounds up to a whole beat: on SPT the shot events
        /// reach us quantized on frames and on the DSP clock (21.3 ms steps in the raid
        /// logs), so being 1 ms late on the grid cost a full beat (a 92 ms lag on the first
        /// first-person shot, 160 ms holes inside 80 ms salves), and a real cadence faster
        /// than the nominal rate accumulated an unbounded lag behind the gun.
        /// </summary>
        public static double ComputeScheduled(double now, double timeToHear, double previousIdeal, double beatDuration, double minOffset)
        {
            double earliest = Math.Max(timeToHear, now + minOffset);
            if (double.IsNaN(earliest) || double.IsInfinity(earliest))
            {
                return now + minOffset;
            }
            if (previousIdeal <= 0.0 || double.IsNaN(previousIdeal) || double.IsInfinity(previousIdeal)
                || beatDuration < MinValidBeatDuration || double.IsNaN(beatDuration) || double.IsInfinity(beatDuration))
            {
                return earliest;
            }
            double ideal = previousIdeal + beatDuration;
            double latest = earliest + MaxLead(beatDuration);
            return Math.Min(Math.Max(ideal, earliest), latest);
        }

        /// <summary>
        /// Fade of the previous shot when the next one starts (live ScheduleShotSound):
        /// beat * BurstFireFadeMultiplier, stretched when the shot leads the grid by more
        /// than the adaptive threshold, always capped to AdaptiveFadeMaxSeconds.
        /// </summary>
        public static float ComputeFade(
            float beatDuration,
            float leadSeconds,
            float fadeMultiplier,
            bool adaptive,
            float adaptiveLeadThresholdBeats,
            float adaptiveMaxMultiplier,
            float maxSeconds)
        {
            if (beatDuration <= 0f)
            {
                return 0f;
            }
            float fade = beatDuration * fadeMultiplier;
            if (adaptive)
            {
                float ratio = leadSeconds / beatDuration;
                if (ratio > adaptiveLeadThresholdBeats)
                {
                    float t = Math.Min(1f, ratio - adaptiveLeadThresholdBeats);
                    fade *= (adaptiveMaxMultiplier - 1f) * t + 1f;
                }
            }
            return Math.Max(0f, Math.Min(fade, maxSeconds));
        }

        /// <summary>
        /// True when the gap since the last shot ends the salve (live
        /// _sequenceResetMultiplier = FireLoopBreakMultiplier). Measured between two
        /// audition times, so that the distance delay does not hide the pause.
        /// </summary>
        public static bool IsNewSequence(double timeToHear, double previousIdeal, double beatDuration, double resetBeats)
        {
            return previousIdeal <= 0.0 || timeToHear - previousIdeal > resetBeats * beatDuration;
        }
    }
}
