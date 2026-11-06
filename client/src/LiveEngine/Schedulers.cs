// Scheduler of the auto emitter. SPT has no server clock to align on, so the DSP
// clock is the only time base. The timing rules live in ShotTiming.
using UnityEngine;

namespace SoundEngineBackport.LiveEngine
{
    public sealed class BeatQuantizedScheduler : IAudioScheduleService
    {
        private const double BaseMinOffsetSec = 0.01;
        private readonly double _minOffset;

        public BeatQuantizedScheduler()
        {
            // never schedule inside the audio block being mixed, or the shot starts on the
            // next block instead of on its exact time (21.3 ms blocks in the raid logs)
            AudioSettings.GetDSPBufferSize(out int bufferLength, out _);
            int sampleRate = AudioSettings.outputSampleRate;
            BufferLatency = sampleRate > 0 ? (double)bufferLength / sampleRate : 0.0;
            _minOffset = System.Math.Max(BaseMinOffsetSec, BufferLatency);
        }

        public double BufferLatency { get; }

        public double GetScheduledTime(double timeToHear, double previousIdeal, double beatDuration)
        {
            return ShotTiming.ComputeScheduled(AudioSettings.dspTime, timeToHear, previousIdeal, beatDuration, _minOffset);
        }
    }
}
