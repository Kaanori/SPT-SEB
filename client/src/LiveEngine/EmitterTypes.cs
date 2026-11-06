// Port of live 1.3.0.48484 audio weapon types (from community IL2CPP dump).
// Types are faithful to the live metadata.
using System;
using EFT;

namespace SoundEngineBackport.LiveEngine
{
    public enum EEmitterFireMode
    {
        None = 0,
        Single = 1,
        Auto = 2,
    }

    /// <summary>
    /// Faithful port of ClientAudioWeaponSettings.WeaponPlaybackSettings (live 1.3).
    /// FireLoopBreakMultiplier is the pause (in beats) that ends a salve and
    /// BurstFireFadeMultiplier the fade between shots (in beats): the live emitter reads
    /// them at offsets 0x10 and 0x14. Pitch and occlusion fields are not used here: SPT
    /// computes the pitch before FireBullet and the occlusion in SpatialAudioOccluder.
    /// </summary>
    [Serializable]
    public class WeaponPlaybackSettings
    {
        public float FireLoopBreakMultiplier;
        public float BurstFireFadeMultiplier;
        public float MinRandomPitch;
        public float MaxRandomPitch;
        public float MinClampPitch;
        public float MaxClampPitch;
        public float OcclusionThreshold;
        public bool AdaptiveFadeEnabled;
        public float AdaptiveFadeLeadBeatsThreshold;
        public float AdaptiveFadeMaxMultiplier;
        public float AdaptiveFadeMaxSeconds;

        public WeaponPlaybackSettings Copy()
        {
            return (WeaponPlaybackSettings)MemberwiseClone();
        }
    }

    [Serializable]
    public class WeaponPoolSettings
    {
        public int DefaultPoolSize;
        public int MinPoolSize;
        public int MaxPoolSize;

        public WeaponPoolSettings Copy()
        {
            return (WeaponPoolSettings)MemberwiseClone();
        }
    }

    /// <summary>
    /// Faithful port of the live ShotPlaybackContext struct (live 1.3,
    /// Audio.Weapons namespace). Field order/semantics from dump.cs.
    /// </summary>
    public struct ShotPlaybackContext
    {
        public readonly SoundBank Bank;
        public readonly float DistanceToListener;
        public readonly float MaxDistance;
        public readonly float BaseVolume;
        public readonly EnvironmentType Environment;
        public readonly bool IsFirstPerson;
        public readonly float Delay;
        public readonly float PitchA;
        public readonly float PitchB;
        public readonly float BeatDuration;
        public readonly bool IsSilenced;

        public ShotPlaybackContext(
            SoundBank bank,
            float distanceToListener,
            float maxDistance,
            float baseVolume,
            EnvironmentType environment,
            bool isFirstPerson,
            float delay,
            float beatDuration,
            float pitchA,
            float pitchB,
            bool isSilenced)
        {
            Bank = bank;
            DistanceToListener = distanceToListener;
            MaxDistance = maxDistance;
            BaseVolume = baseVolume;
            Environment = environment;
            IsFirstPerson = isFirstPerson;
            Delay = delay;
            PitchA = pitchA;
            PitchB = pitchB;
            BeatDuration = beatDuration;
            IsSilenced = isSilenced;
        }
    }
}
