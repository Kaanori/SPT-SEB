// Where to cut the loop of a legacy salve (WTT-Armory...) before its tail.
//
// SPT cuts a loop on a grid of ClipLength / 16, assuming 16 shots per clip with the gaps
// on the grid lines. Many custom loops do not follow it: the KAC PDW loop holds 17.3 shots
// (83 ms apart, on a 90 ms grid), and its silenced loop starts each shot ~18 ms before its
// grid line. The grid cut then lands inside a shot, at up to full scale, while the tail
// starts around 0.2: a dry cut, as the queue does no crossfade. Each loop clip is read once
// and its quiet gaps between shots listed; the cut goes to the quietest gap near the grid,
// unless the grid cut is quieter. Over the 338 loop clips a V1 salve can play (WTT-Armory,
// the live RPD/M60 and silenced AA-12 loops, WTT-ContentBackport's), measured on the real
// samples, the peak left at the cut drops from 0.31 to 0.21 on single shots, 0.33 to 0.22 on
// 2-round bursts and 0.44 to 0.31 on salve ends (grid cuts already past counted where they
// really stop, at once). No loop gets worse, but for 5 bursts by 0.02 to 0.04 (KAC PDW outdoor
// close single shot: 0.97 -> 0.28, silenced 1.00 -> 0.25; its tails start near 0.2).
using System.Collections.Generic;
using UnityEngine;

namespace SoundEngineBackport
{
    internal static class LegacyLoopCuts
    {
        // a gap is the lowest point of the envelope over +-25 ms, under 70 % of its peak
        private const int GapRadiusMs = 25;
        private const float GapDepth = 0.7f;

        internal sealed class Analysis
        {
            public float Length;
            public float[] GapTimes;
            public float[] GapLevels;
            // peak of each quarter of a 1 ms block
            public float[] QuarterPeaks;
            public float QuarterSeconds;

            /// <summary>Level the cut leaves behind: peak of the millisecond played before
            /// <paramref name="clipTime"/>, from the quarters wholly before it. Peaks of whole
            /// blocks took in the next shot's attack right after a grid cut, which then looked
            /// loud and lost to a louder gap (25 of 338 loops worse on single shots).</summary>
            public float PeakAt(float clipTime)
            {
                int n = QuarterPeaks.Length;
                float t = Mathf.Repeat(clipTime, Length);
                int last = (int)(t / QuarterSeconds);
                int first = Mathf.FloorToInt((t - 0.001f) / QuarterSeconds);
                float peak = 0f;
                for (int q = first; q < Mathf.Max(last, first + 1); q++)
                {
                    peak = Mathf.Max(peak, QuarterPeaks[((q % n) + n) % n]);
                }
                return peak;
            }
        }

        private static readonly Dictionary<int, Analysis> Cache = new Dictionary<int, Analysis>();
        private static float[] _buffer;

        /// <summary>Gaps of a loop clip, or null when its samples can't be read (the grid cut
        /// applies then).</summary>
        internal static Analysis Get(AudioClip clip)
        {
            if (clip == null)
            {
                return null;
            }
            int id = clip.GetInstanceID();
            if (Cache.TryGetValue(id, out Analysis cached))
            {
                return cached;
            }
            if (clip.loadState != AudioDataLoadState.Loaded)
            {
                // not cached: read once loaded
                return null;
            }
            Analysis analysis = null;
            try
            {
                analysis = Analyze(clip);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"could not read the loop clip {clip.name}, grid cut kept: {e.Message}");
            }
            Cache[id] = analysis;
            return analysis;
        }

        private static Analysis Analyze(AudioClip clip)
        {
            int channels = Mathf.Max(1, clip.channels);
            int frames = clip.samples;
            int blockFrames = clip.frequency / 1000;
            int blocks = blockFrames > 0 ? frames / blockFrames : 0;
            if (blocks < 4 * GapRadiusMs)
            {
                return null;
            }
            int count = frames * channels;
            if (_buffer == null || _buffer.Length < count)
            {
                _buffer = new float[count];
            }
            // with fewer values than the buffer holds, GetData fills only what the clip has
            if (!clip.GetData(_buffer, 0))
            {
                return null;
            }

            // seconds per block: 44 frames at 44.1 kHz are 0.998 ms, not 1 ms (counted as 1 ms,
            // the gaps drifted 2.7 ms per repeat of a 1.37 s loop and salve ends got louder)
            float blockSeconds = (float)blockFrames / clip.frequency;
            var rms = new float[blocks];
            int perBlock = blockFrames * channels;
            for (int b = 0; b < blocks; b++)
            {
                double sum = 0.0;
                int start = b * perBlock;
                for (int i = start; i < start + perBlock; i++)
                {
                    float v = _buffer[i];
                    sum += v * v;
                }
                rms[b] = (float)System.Math.Sqrt(sum / perBlock);
            }

            int quarterFrames = Mathf.Max(1, blockFrames / 4);
            int quarters = frames / quarterFrames;
            var peaks = new float[quarters];
            int perQuarter = quarterFrames * channels;
            for (int q = 0; q < quarters; q++)
            {
                float peak = 0f;
                int start = q * perQuarter;
                for (int i = start; i < start + perQuarter; i++)
                {
                    float a = Mathf.Abs(_buffer[i]);
                    if (a > peak)
                    {
                        peak = a;
                    }
                }
                peaks[q] = peak;
            }

            // 3 ms envelope; the clip loops, so it wraps
            var env = new float[blocks];
            for (int b = 0; b < blocks; b++)
            {
                env[b] = (rms[(b - 1 + blocks) % blocks] + rms[b] + rms[(b + 1) % blocks]) / 3f;
            }

            var times = new List<float>();
            var levels = new List<float>();
            for (int b = 0; b < blocks; b++)
            {
                float e = env[b];
                float max = e;
                bool lowest = true;
                for (int k = -GapRadiusMs; k <= GapRadiusMs && lowest; k++)
                {
                    if (k == 0)
                    {
                        continue;
                    }
                    float o = env[(b + k + blocks) % blocks];
                    // first block of a flat bottom only
                    lowest = k < 0 ? e < o : e <= o;
                    if (o > max)
                    {
                        max = o;
                    }
                }
                if (lowest && e < GapDepth * max)
                {
                    times.Add(b * blockSeconds);
                    levels.Add(e);
                }
            }

            return new Analysis
            {
                // the loop repeats every clip length, not every whole block
                Length = (float)frames / clip.frequency,
                QuarterSeconds = (float)quarterFrames / clip.frequency,
                GapTimes = times.ToArray(),
                GapLevels = levels.ToArray(),
                QuarterPeaks = peaks,
            };
        }

        /// <summary>Clip time (counted over the loop's repeats) of the quietest gap in
        /// [<paramref name="from"/>, <paramref name="to"/>]; false, with <paramref name="grid"/>,
        /// when there is none or when it is not quieter than the grid cut.</summary>
        internal static bool TryChoose(AudioClip clip, float from, float to, float grid, out float cut)
        {
            cut = grid;
            Analysis a = Get(clip);
            if (a == null || a.GapTimes.Length == 0 || to < from)
            {
                return false;
            }
            float best = -1f;
            float bestLevel = float.MaxValue;
            for (int n = Mathf.Max(0, Mathf.FloorToInt(from / a.Length)); n * a.Length <= to; n++)
            {
                for (int i = 0; i < a.GapTimes.Length; i++)
                {
                    float t = n * a.Length + a.GapTimes[i];
                    if (t >= from && t <= to && a.GapLevels[i] < bestLevel)
                    {
                        best = t;
                        bestLevel = a.GapLevels[i];
                    }
                }
            }
            if (best < 0f || a.PeakAt(best) > a.PeakAt(grid))
            {
                return false;
            }
            cut = best;
            return true;
        }
    }
}
