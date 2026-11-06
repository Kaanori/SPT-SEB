// LiveWeaponSoundEngine: per-WeaponSoundPlayer port of the live 1.3 shot engine.
// Owns the composite emitter, builds ShotPlaybackContexts from SPT data and
// drives emitter.Tick from a manager MonoBehaviour.
using System;
using System.Collections.Generic;
using Audio.AuxiliaryAudioUtils;
using EFT;
using EFT.InventoryLogic;
using EFT.Weapons;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Audio;

namespace SoundEngineBackport.LiveEngine
{
    public sealed class LiveWeaponSoundEngine
    {
        private const float SoundSpeed = 340.29f;
        private const double DrainTimeout = 15.0;

        private static readonly AccessTools.FieldRef<BaseSoundPlayer, BaseSoundPlayer.IObserverToPlayerBridge> BridgeRef =
            AccessTools.FieldRefAccess<BaseSoundPlayer, BaseSoundPlayer.IObserverToPlayerBridge>("playersBridge");
        private static readonly AccessTools.FieldRef<BaseSoundPlayer, AudioSourcePriorityCalculator> PriorityRef =
            AccessTools.FieldRefAccess<BaseSoundPlayer, AudioSourcePriorityCalculator>("PriorityCalculator");
        private static readonly AccessTools.FieldRef<WeaponSoundPlayer, AudioMixerGroup> MixerGroupRef =
            AccessTools.FieldRefAccess<WeaponSoundPlayer, AudioMixerGroup>("_currentMixerGroup");

        public static readonly EngineManager Manager;
        private static readonly Dictionary<WeaponSoundPlayer, LiveWeaponSoundEngine> Engines = new Dictionary<WeaponSoundPlayer, LiveWeaponSoundEngine>();

        internal static bool ApplyReverbRouting = true;

        /// <summary>Live ClientAudioWeaponSettings defaults (decompiled constructor).</summary>
        internal static readonly WeaponPlaybackSettings SharedPlayback = new WeaponPlaybackSettings
        {
            FireLoopBreakMultiplier = 4f,
            BurstFireFadeMultiplier = 4f,
            MinRandomPitch = -0.015f,
            MaxRandomPitch = 0.015f,
            MinClampPitch = 0.98f,
            MaxClampPitch = 1.03f,
            OcclusionThreshold = 0.5f,
            AdaptiveFadeEnabled = true,
            AdaptiveFadeLeadBeatsThreshold = 1.5f,
            AdaptiveFadeMaxMultiplier = 2f,
            AdaptiveFadeMaxSeconds = 0.25f,
        };

        // live allows 15, but a shot holds its source while its sound travels: past ~380 m at
        // 800 rpm, stealing the oldest source would drop a shot not heard yet
        private static readonly WeaponPoolSettings AutoPool = new WeaponPoolSettings { DefaultPoolSize = 5, MinPoolSize = 2, MaxPoolSize = 32 };

        // clips last 2 to 3.5 s: enough for a fast semi-auto cadence to ring out uncut
        private static readonly WeaponPoolSettings SinglePool = new WeaponPoolSettings { DefaultPoolSize = 5, MinPoolSize = 2, MaxPoolSize = 16 };

        static LiveWeaponSoundEngine()
        {
            var go = new GameObject("SoundEngineBackport.LiveEngineManager");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            Manager = go.AddComponent<EngineManager>();
        }

        public sealed class EngineManager : MonoBehaviour
        {
            internal readonly List<LiveWeaponSoundEngine> Active = new List<LiveWeaponSoundEngine>();

            private void Update()
            {
                double dspNow = AudioSettings.dspTime;
                LiveFilterBlend.TickGrenades();
                for (int i = Active.Count - 1; i >= 0; i--)
                {
                    LiveWeaponSoundEngine engine = Active[i];
                    try
                    {
                        engine._emitter.Tick(dspNow);
                        if (engine.IsFinished(dspNow))
                        {
                            Forget(engine, i);
                        }
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogError($"engine tick failed, dropping this engine: {e}");
                        Forget(engine, i);
                    }
                }
            }

            private void Forget(LiveWeaponSoundEngine engine, int index)
            {
                Active.RemoveAt(index);
                Engines.Remove(engine._owner);
                try
                {
                    engine._emitter.Dispose();
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"engine dispose failed: {e.Message}");
                }
            }
        }

        private readonly WeaponSoundPlayer _owner;
        private readonly AudioFireModeSelector _selector;
        private readonly CompositeGunshotEmitter _emitter;
        private readonly AutoFireGunshotEmitter _autoEmitter;
        private readonly SingleGunshotEmitter _singleEmitter;
        private BaseSoundPlayer.IObserverToPlayerBridge _bridge;
        private double _drainDeadline = -1.0;

        private LiveWeaponSoundEngine(WeaponSoundPlayer owner)
        {
            _owner = owner;
            _selector = new AudioFireModeSelector();
            _autoEmitter = new AutoFireGunshotEmitter(SharedPlayback, AutoPool, new BeatQuantizedScheduler());
            _singleEmitter = new SingleGunshotEmitter(SinglePool);
            _autoEmitter.PriorityProvider = GetPriority;
            _singleEmitter.PriorityProvider = GetPriority;
            _emitter = new CompositeGunshotEmitter(_autoEmitter, _singleEmitter, _selector);
            _emitter.MixerGroupProvider = ResolveMixerGroup;
            Manager.Active.Add(this);
        }

        /// <summary>Starts the per-frame update before the first shot: grenades need it too.</summary>
        public static void EnsureManager() => _ = Manager;

        public static LiveWeaponSoundEngine GetOrCreate(WeaponSoundPlayer owner)
        {
            if (!Engines.TryGetValue(owner, out var engine))
            {
                engine = new LiveWeaponSoundEngine(owner);
                Engines.Add(owner, engine);
            }
            return engine;
        }

        /// <summary>The weapon is put away or destroyed: nothing new will start, the shots
        /// already fired still ring out (vanilla releases its queue with a delay too).</summary>
        public static void NotifyDisabled(WeaponSoundPlayer owner)
        {
            if (Engines.TryGetValue(owner, out var engine))
            {
                engine._emitter.Reset();
            }
        }

        public static void NotifyDestroyed(WeaponSoundPlayer owner)
        {
            if (Engines.TryGetValue(owner, out var engine))
            {
                engine._emitter.Reset();
                engine._drainDeadline = AudioSettings.dspTime + DrainTimeout;
            }
        }

        private bool IsFinished(double dspNow)
        {
            if (_owner != null && _drainDeadline < 0.0)
            {
                return false;
            }
            return !_emitter.IsBusy || (_drainDeadline > 0.0 && dspNow >= _drainDeadline);
        }

        /// <summary>Called from the FireBullet patch for every bullet of a live-format bank.</summary>
        public void FireBullet(Ammo ammo, Vector3 shotPosition, Vector3 shotDirection, float pitchMult, bool multiShot, bool burstOf2Start)
        {
            // read every shot: weapons are pooled and can change owner
            _bridge = BridgeRef(_owner);
            bool firstPerson = _bridge != null && _bridge.PointOfView.IsFirstPerson();
            EnvironmentType environment = _bridge != null ? _bridge.Environment : EnvironmentType.Outdoor;
            bool isSilenced = _owner.IsSilenced;

            SoundBank bank = multiShot
                ? (isSilenced ? _owner.DoubletSilenced : _owner.Doublet)
                : (isSilenced ? _owner.BodySilenced : _owner.Body);
            if (bank == null)
            {
                return;
            }

            Weapon weapon = _bridge?.Weapon as Weapon;
            bool continuousMode = weapon == null
                || weapon.SelectedFireMode == Weapon.EFireMode.fullauto
                || weapon.SelectedFireMode == Weapon.EFireMode.burst;
            bool auto = _owner.IsAutoWeapon && continuousMode;
            float beat = GetBeatDuration(weapon, continuousMode);

            float distance = _owner.Distance;
            float delay = firstPerson ? 0f : distance / SoundSpeed;
            LiveWeaponAudioSettings.Get(weapon?.TemplateId.ToString(), environment, isSilenced, out float maxDistance, out float volume);
            ComputePitch(weapon, continuousMode, pitchMult, out float pitchA, out float pitchB);
            Vector3 weaponPosition = _owner.WeaponPosition;

            _selector.SetFireMode(auto ? EEmitterFireMode.Auto : EEmitterFireMode.Single);
            _emitter.RegisterInOcclusionProcess(_bridge);
            _emitter.EnabledOcclusion(!firstPerson);
            _emitter.EnabledReverb(firstPerson || environment == EnvironmentType.Indoor);
            _autoEmitter.SoundPosition = weaponPosition;
            _singleEmitter.SoundPosition = weaponPosition;

            if (PluginConfig.LogShotDetails)
            {
                Plugin.Log.LogWarning($"[shot] bank={bank.name} fp={firstPerson} auto={auto} dist={distance:F1} " +
                    $"delay={delay * 1000f:F0}ms beat={beat * 1000f:F0}ms pitch={pitchA:F3}/{pitchB:F3} maxDist={maxDistance:F0} vol={volume:F2} env={environment}");
            }

            _emitter.ScheduleShotSound(new ShotPlaybackContext(
                bank, distance, maxDistance, volume, environment, firstPerson, delay, beat, pitchA, pitchB, isSilenced));
            if (burstOf2Start)
            {
                // the game fires this event once for both bullets of a 2-round burst
                // (the loop format covered them with the two beats before its tail)
                _emitter.ScheduleShotSound(new ShotPlaybackContext(
                    bank, distance, maxDistance, volume, environment, firstPerson, delay + beat, beat, pitchA, pitchB, isSilenced));
            }

            _owner.FireSonicSound(delay, bank.Rolloff, ammo, shotPosition, shotDirection);
        }

        private static void ComputePitch(Weapon weapon, bool continuousMode, float sptPitch, out float pitchA, out float pitchB)
        {
            if (weapon == null)
            {
                pitchA = sptPitch;
                pitchB = sptPitch;
                return;
            }
            WeaponPlaybackSettings settings = SharedPlayback;
            LiveAudioMath.ComputeShotPitch(
                UnityEngine.Random.value,
                settings.MinRandomPitch,
                settings.MaxRandomPitch,
                settings.MinClampPitch,
                settings.MaxClampPitch,
                weapon.GetCurrentMagazineCount(),
                weapon.GetMaxMagazineCount(),
                continuousMode ? weapon.MalfState.OverheatFirerateMult : 0f,
                out pitchA,
                out pitchB);
        }

        private static float GetBeatDuration(Weapon weapon, bool continuousMode)
        {
            // live GetShotDuration; the SPT values are in rounds per minute
            float rpm = weapon != null ? (continuousMode ? weapon.FireRate : weapon.SingleFireRate) : 600f;
            return rpm > 0f ? Mathf.Max(60f / rpm, 0.03f) : 0.1f;
        }

        private int GetPriority(double distance, float rolloff, float pitch, BetterSource source)
        {
            AudioSourcePriorityCalculator calculator = PriorityRef(_owner);
            if (calculator == null)
            {
                return 0;
            }
            int priority = calculator.CalculatePriority((float)distance, rolloff);
            source.SetPriority(priority);
            return priority;
        }

        /// <summary>Vanilla UpdateMixerGroup does nothing without its _queue, which this
        /// engine never creates: shots would stay on the direct group for good, occluded or
        /// not. Live rule here, and _currentMixerGroup stays up to date for
        /// IsOccludedToListener (distant sonic cracks).</summary>
        private AudioMixerGroup ResolveMixerGroup()
        {
            BetterAudio betterAudio = MonoBehaviourSingleton<BetterAudio>.Instance;
            AudioMixerGroup group = betterAudio != null ? betterAudio.GunshotMixerGroup : null;
            var spatial = MonoBehaviourSingleton<Audio.SpatialSystem.SpatialAudioSystem>.Instance;
            if (_bridge != null && _bridge.PointOfView != EPointOfView.FirstPerson && spatial != null && _bridge.iPlayer != null
                && spatial.TryGetPlayerAudioContainer(_bridge.iPlayer.Id, out var container))
            {
                // live rule: its threshold is a weapon setting, 0.5 on every prefab (0.55 in SPT)
                if (betterAudio != null && container.OcclusionEffect > SharedPlayback.OcclusionThreshold)
                {
                    group = betterAudio.GunshotOccludedMixerGroup;
                }
            }
            MixerGroupRef(_owner) = group;
            return group;
        }

        /// <summary>Called from the StopFiringLoop patch.</summary>
        public void StopFiringLoop()
        {
            _emitter.Reset();
        }
    }
}
