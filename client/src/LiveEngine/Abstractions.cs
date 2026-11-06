// Port of live 1.3 weapon-audio interfaces (Audio.Weapons), mapped onto SPT types.
using System;
using EFT;
using UnityEngine.Audio;

namespace SoundEngineBackport.LiveEngine
{
    public interface IAudioFireModeSelector
    {
        EEmitterFireMode CurrentFireMode { get; }
        event Action<EEmitterFireMode> FireModeChanged;
        void SetFireMode(EEmitterFireMode fireMode);
    }

    public interface IAudioScheduleService
    {
        double BufferLatency { get; }
        double GetScheduledTime(double timeToHear, double previousIdeal, double beatDuration);
    }

    public interface IAudioGunshotEmitter : IDisposable
    {
        double LastScheduledDspTime { get; }
        bool IsBusy { get; }
        Func<AudioMixerGroup> MixerGroupProvider { get; set; }
        void EnabledOcclusion(bool enabled);
        void EnabledReverb(bool enabled);
        void ScheduleShotSound(ShotPlaybackContext playbackContext);
        void RegisterInOcclusionProcess(BaseSoundPlayer.IObserverToPlayerBridge playerBridge);
        void Reset();
        void Tick(double dspNow);
    }
}
