using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Audio;
using SoundEngineBackport.LiveEngine;

namespace SoundEngineBackport
{
    [BepInPlugin(Guid, "Sound Engine Backport", "1.0.2")]
    public class Plugin : BaseUnityPlugin
    {
        // same GUID as the server mod and the Forge listing (checked by the Forge scanner)
        internal const string Guid = "red.soundenginebackport";
        private const string OldGuid = "red.soundenginebackport.client";

        internal static ManualLogSource Log;
        internal static string Location;
        // runs the SPT mixer fades of trader visits: BetterAudio is destroyed when a visit ends
        internal static Plugin Instance;
        internal static new PluginConfig Config;

        private void Awake()
        {
            Log = Logger;
            Location = Info.Location;
            Instance = this;
            MigrateOldConfig();
            Config = new PluginConfig(((BaseUnityPlugin)this).Config);
            Config.BindAndApply();
            Harmony.CreateAndPatchAll(typeof(WeaponSoundPlayerPatches), Guid);
            Harmony.CreateAndPatchAll(typeof(LiveOcclusionPatch), Guid);
            try
            {
                Harmony.CreateAndPatchAll(typeof(LiveAudioPatches), Guid);
            }
            catch (System.Exception e)
            {
                // the shot engine above stays usable without these
                Logger.LogError($"live audio patches failed: {e}");
            }
            try
            {
                Harmony.CreateAndPatchAll(typeof(LiveAmbientPatches), Guid);
            }
            catch (System.Exception e)
            {
                Logger.LogError($"live ambient patches failed: {e}");
            }
            try
            {
                Harmony.CreateAndPatchAll(typeof(LiveAcousticPatches), Guid);
            }
            catch (System.Exception e)
            {
                Logger.LogError($"live acoustic data patch failed: {e}");
            }
            Logger.LogInfo($"Sound Engine Backport v1.0.2 loaded (live engine port, extraMuffleStrength={Config.MuffleStrength.Value})");
        }

        /// <summary>Up to 1.0.0 the client GUID, which names the config file, was
        /// <see cref="OldGuid"/>: its settings move to the new file once.</summary>
        private void MigrateOldConfig()
        {
            try
            {
                var config = ((BaseUnityPlugin)this).Config;
                string oldPath = System.IO.Path.Combine(BepInEx.Paths.ConfigPath, OldGuid + ".cfg");
                if (System.IO.File.Exists(config.ConfigFilePath) || !System.IO.File.Exists(oldPath))
                {
                    return;
                }
                System.IO.File.Move(oldPath, config.ConfigFilePath);
                config.Reload();
                Logger.LogInfo($"settings moved from {OldGuid}.cfg to {Guid}.cfg");
            }
            catch (System.Exception e)
            {
                Logger.LogError($"could not move the settings of {OldGuid}.cfg: {e}");
            }
        }

        private void Update()
        {
            try
            {
                if (Config.RecordKey.Value.IsDown())
                {
                    LiveOutputRecorder.Start();
                }
            }
            catch (System.Exception e)
            {
                Logger.LogError($"output recorder failed: {e}");
                enabled = false;
            }
        }
    }

    /// <summary>Read by BepInEx.ConfigurationManager (F12) through its type name.</summary>
    internal sealed class ConfigurationManagerAttributes
    {
        public bool? IsAdvanced;
        public int? Order;
        public string DispName;
    }

    internal sealed class PluginConfig
    {
        private const string VolumeSection = "Volume";
        private const string EngineSection = "Engine";
        // a source plays at 1.0 at most: the groups start at 0.75 in EFT 1.0, which leaves +2.5 dB
        private const float GroupMaxDb = 2.5f;

        private readonly BepInEx.Configuration.ConfigFile _file;

        internal BepInEx.Configuration.ConfigEntry<bool> LiveEngineEnabled;
        internal BepInEx.Configuration.ConfigEntry<float> MuffleStrength;
        internal BepInEx.Configuration.ConfigEntry<bool> LiveMixerEnabled;
        internal BepInEx.Configuration.ConfigEntry<float> RaidVolumeDb;
        internal BepInEx.Configuration.ConfigEntry<float> AmbientVolumeDb;
        internal BepInEx.Configuration.ConfigEntry<float> GunshotsVolumeDb;
        internal BepInEx.Configuration.ConfigEntry<float> HandlingVolumeDb;
        internal BepInEx.Configuration.ConfigEntry<float> ExplosionsVolumeDb;
        internal BepInEx.Configuration.ConfigEntry<float> CharacterVolumeDb;
        internal BepInEx.Configuration.ConfigEntry<float> ImpactsVolumeDb;
        internal BepInEx.Configuration.ConfigEntry<bool> LiveSpatializerEnabled;
        internal BepInEx.Configuration.ConfigEntry<bool> LiveGroupPresetsEnabled;
        internal BepInEx.Configuration.ConfigEntry<bool> LiveOcclusionGateEnabled;
        internal BepInEx.Configuration.ConfigEntry<float> MinMuffleCutoff;
        internal BepInEx.Configuration.ConfigEntry<float> SequenceResetBeats;
        internal BepInEx.Configuration.ConfigEntry<float> BurstFadeBeats;
        internal BepInEx.Configuration.ConfigEntry<bool> LiveReverbRouting;
        internal BepInEx.Configuration.ConfigEntry<bool> PreferLiveBanks;
        internal BepInEx.Configuration.ConfigEntry<bool> AdaptiveFadeEnabled;
        internal BepInEx.Configuration.ConfigEntry<bool> DebugLogs;
        internal BepInEx.Configuration.ConfigEntry<bool> LiveAmbientPortEnabled;
        internal BepInEx.Configuration.ConfigEntry<bool> LiveDynamicHrtfEnabled;
        internal BepInEx.Configuration.ConfigEntry<bool> LiveAcousticDataEnabled;
        internal BepInEx.Configuration.ConfigEntry<BepInEx.Configuration.KeyboardShortcut> RecordKey;

        internal static bool LogShotDetails;

        internal PluginConfig(BepInEx.Configuration.ConfigFile file) { _file = file; }

        /// <summary>Everything but the volumes: shown in F12 only with "Advanced settings" ticked.</summary>
        private BepInEx.Configuration.ConfigEntry<T> Advanced<T>(string section, string key, T value, string description,
            BepInEx.Configuration.AcceptableValueBase range = null)
        {
            return _file.Bind(section, key, value, new BepInEx.Configuration.ConfigDescription(description, range,
                new ConfigurationManagerAttributes { IsAdvanced = true }));
        }

        private BepInEx.Configuration.ConfigEntry<float> Volume(string key, string name, int order, float value, float min, float max,
            string description)
        {
            return _file.Bind(VolumeSection, key, value, new BepInEx.Configuration.ConfigDescription(description,
                new BepInEx.Configuration.AcceptableValueRange<float>(min, max),
                new ConfigurationManagerAttributes { DispName = name, Order = order }));
        }

        internal void BindAndApply()
        {
            RaidVolumeDb = Volume("RaidVolumeDb", "Raid volume (all sounds)", 70, 0f, -12f, 6f,
                "Volume of all raid sounds. The sliders below add to it. 0 = SPT 4.1.6 level, 4 = EFT 1.0 mixer default (may clip).");
            AmbientVolumeDb = Volume("AmbientVolumeDb", "Ambience (rain, wind, day/night)", 60, -4f, -20f, 6f,
                "Background ambience only: rain, wind, day and night.");
            GunshotsVolumeDb = Volume("GunshotsVolumeDb", "Gunshots", 50, 0f, -20f, 6f,
                "All gunshots, yours and others'. 0 = EFT 1.0 level.");
            HandlingVolumeDb = Volume("HandlingVolumeDb", "Weapon handling", 40, 0f, -20f, GroupMaxDb,
                "Reloads, magazines and fire mode switches. 0 = EFT 1.0 level.");
            ExplosionsVolumeDb = Volume("ExplosionsVolumeDb", "Explosions", 30, 0f, -20f, GroupMaxDb,
                "Grenades and explosions. 0 = EFT 1.0 level.");
            CharacterVolumeDb = Volume("CharacterVolumeDb", "Footsteps and gear", 20, 0f, -20f, GroupMaxDb,
                "Footsteps and gear of every character. 0 = EFT 1.0 level.");
            ImpactsVolumeDb = Volume("ImpactsVolumeDb", "Bullet impacts", 10, 0f, -20f, GroupMaxDb,
                "Bullet impacts. 0 = EFT 1.0 level.");

            LiveEngineEnabled = Advanced(EngineSection, "LiveEngineEnabled", true,
                "EFT 1.0 gunshot engine. Off = the simple one-shot playback of mod v1.");
            MuffleStrength = Advanced("Occlusion", "ExtraMuffleStrength", 0f,
                "Extra muffling of distant sounds behind walls, not in EFT 1.0. 0 = none, 1 = strongest.");
            LiveMixerEnabled = Advanced(EngineSection, "LiveMixerEnabled", true,
                "EFT 1.0 audio mixer, for all sounds. The raid and ambience volumes need it. Restart required.");
            LiveGroupPresetsEnabled = Advanced(EngineSection, "LiveGroupPresetsEnabled", true,
                "EFT 1.0 distance curves and volumes of all sound types. Off = SPT 4.1.6 values. Restart required.");
            LiveOcclusionGateEnabled = Advanced("Occlusion", "LiveOcclusionGateEnabled", true,
                "Keeps a sound silent until the game knows if a wall blocks it, like EFT 1.0. Off = SPT 4.1.6, where it can leak through walls.");
            LiveSpatializerEnabled = Advanced(EngineSection, "LiveSpatializerEnabled", true,
                "Filters other players' gunshots by direction, like EFT 1.0. Applies to weapons that have not fired yet.");
            MinMuffleCutoff = Advanced("Occlusion", "MinMuffleCutoffHz", 500f,
                "Strongest muffling behind walls, as a cutoff frequency in Hz. Lower = more muffled.");
            SequenceResetBeats = Advanced(EngineSection, "SequenceResetBeats", 4f,
                "Pause between two shots, in beats, that ends a burst.");
            BurstFadeBeats = Advanced(EngineSection, "BurstFadeBeats", 4f,
                "Fade of a shot when the next one starts, in beats (0.25 s at most).");
            AdaptiveFadeEnabled = Advanced(EngineSection, "AdaptiveFadeEnabled", true,
                "Longer fade when a shot comes early, like EFT 1.0.");
            LiveReverbRouting = Advanced(EngineSection, "LiveReverbRouting", true,
                "EFT 1.0 reverb rule: on for your shots and indoor shots, off for distant outdoor shots. Off = SPT 4.1.6 behaviour.");
            PreferLiveBanks = Advanced(EngineSection, "PreferLiveBanks", true,
                "Use this mod's EFT 1.0 gun sounds instead of those of WTT-ContentBackport. Applies to new weapons.");
            DebugLogs = Advanced("Debug", "DebugLogs", false, "Detailed log of every shot.");
            LiveAmbientPortEnabled = Advanced(EngineSection, "LiveAmbientPortEnabled", true,
                "EFT 1.0 ambience: fades, indoor rain, effects cut indoors. Off = SPT 4.1.6 values. Applies at the next raid.");
            LiveDynamicHrtfEnabled = Advanced(EngineSection, "LiveDynamicHrtfEnabled", true,
                "EFT 1.0 3D audio (HRTF) on handling, collision, window and door sounds. Off = SPT 4.1.6, switched on and off by distance.");
            RecordKey = Advanced("Debug", "RecordKey", new BepInEx.Configuration.KeyboardShortcut(KeyCode.F10),
                "Records 10 seconds of game audio to BepInEx/seb_record_<time>.wav, to report a sound bug.");
            LiveDynamicHrtf.Enabled = LiveDynamicHrtfEnabled.Value;
            LiveDynamicHrtfEnabled.SettingChanged += (_, __) => LiveDynamicHrtf.Enabled = LiveDynamicHrtfEnabled.Value;
            LiveAmbientPort.Enabled = LiveAmbientPortEnabled.Value;
            LiveAmbientPortEnabled.SettingChanged += (_, __) => LiveAmbientPort.Enabled = LiveAmbientPortEnabled.Value;
            LiveAcousticDataEnabled = Advanced(EngineSection, "LiveAcousticDataEnabled", true,
                "EFT 1.0 sound routes between the rooms of each map (through doors, windows, stairs), transcribed into SPT's maps. Off = SPT 4.1.6 routes. Applies at the next raid.");
            LiveAcousticData.Enabled = LiveAcousticDataEnabled.Value;
            LiveAcousticDataEnabled.SettingChanged += (_, __) => LiveAcousticData.Enabled = LiveAcousticDataEnabled.Value;

            LiveOcclusionModule.MuffleStrength = MuffleStrength.Value;
            LiveMixer.Enabled = LiveMixerEnabled.Value;
            LiveMixer.WorldVolumeDb = RaidVolumeDb.Value;
            LiveMixer.AmbientTrimDb = AmbientVolumeDb.Value;
            AmbientVolumeDb.SettingChanged += (_, __) =>
            {
                LiveMixer.AmbientTrimDb = AmbientVolumeDb.Value;
                LiveMixer.ApplyAmbientVolume();
            };
            RaidVolumeDb.SettingChanged += (_, __) =>
            {
                LiveMixer.WorldVolumeDb = RaidVolumeDb.Value;
                LiveMixer.ApplyWorldVolume();
            };
            ApplyVolumeTrims();
            foreach (var trim in new[] { GunshotsVolumeDb, HandlingVolumeDb, ExplosionsVolumeDb, CharacterVolumeDb, ImpactsVolumeDb })
            {
                trim.SettingChanged += (_, __) => ApplyVolumeTrims();
            }
            LiveFilterBlend.Enabled = LiveSpatializerEnabled.Value;
            LiveGroupPresets.Enabled = LiveGroupPresetsEnabled.Value;
            LiveOcclusionGate.Enabled = LiveOcclusionGateEnabled.Value;
            LiveOcclusionGateEnabled.SettingChanged += (_, __) => LiveOcclusionGate.Enabled = LiveOcclusionGateEnabled.Value;
            LiveOcclusionModule.MinCutoffFrequency = Mathf.Clamp(MinMuffleCutoff.Value, 100f, 8000f);
            ApplyEngineSettings();

            MuffleStrength.SettingChanged += (_, __) => LiveOcclusionModule.MuffleStrength = MuffleStrength.Value;
            MinMuffleCutoff.SettingChanged += (_, __) => LiveOcclusionModule.MinCutoffFrequency = Mathf.Clamp(MinMuffleCutoff.Value, 100f, 8000f);
            SequenceResetBeats.SettingChanged += (_, __) => ApplyEngineSettings();
            BurstFadeBeats.SettingChanged += (_, __) => ApplyEngineSettings();
            AdaptiveFadeEnabled.SettingChanged += (_, __) => ApplyEngineSettings();
            LiveReverbRouting.SettingChanged += (_, __) => ApplyEngineSettings();
            PreferLiveBanks.SettingChanged += (_, __) => ApplyEngineSettings();
            DebugLogs.SettingChanged += (_, __) => ApplyEngineSettings();
        }

        /// <summary>Shots are scaled by the engine at each shot, the other groups through their
        /// preset, which sources read at each play: all apply during a raid.</summary>
        private void ApplyVolumeTrims()
        {
            LiveMixer.GunshotGain = LiveAudioMath.DbToGain(GunshotsVolumeDb.Value);
            LiveGroupPresets.SetTrim("Weaponary", HandlingVolumeDb.Value);
            LiveGroupPresets.SetTrim("Grenades", ExplosionsVolumeDb.Value);
            LiveGroupPresets.SetTrim("Character", CharacterVolumeDb.Value);
            LiveGroupPresets.SetTrim("Impacts", ImpactsVolumeDb.Value);
        }

        private void ApplyEngineSettings()
        {
            LogShotDetails = DebugLogs.Value;
            LiveOcclusionModule.LogCutoffChanges = DebugLogs.Value;
            LiveWeaponSoundEngine.SharedPlayback.FireLoopBreakMultiplier = SequenceResetBeats.Value;
            LiveWeaponSoundEngine.SharedPlayback.BurstFireFadeMultiplier = BurstFadeBeats.Value;
            LiveWeaponSoundEngine.SharedPlayback.AdaptiveFadeEnabled = AdaptiveFadeEnabled.Value;
            LiveWeaponSoundEngine.ApplyReverbRouting = LiveReverbRouting.Value;
            LiveBankReplacement.Enabled = PreferLiveBanks.Value;
        }
    }

    /// <summary>
    /// v2.0: routes WeaponSoundPlayer through the ported live engine for the
    /// backported live-format banks (one-shot bodies). Old-format banks (clips
    /// containing "_loop") keep the v1.1.1 one-shot playback behaviour, and the
    /// original SPT logic stays untouched behind both fallbacks.
    /// </summary>
    public static class WeaponSoundPlayerPatches
    {
        private const float SoundSpeed = 340.29f;

        private static readonly ConditionalWeakTable<WeaponSoundPlayer, V1State> V1States =
            new ConditionalWeakTable<WeaponSoundPlayer, V1State>();

        internal sealed class V1State
        {
            public bool LoopMode;
            public double LastBodyEnd;
            // loop clip of the current salve, read for its gaps (LegacyLoopCuts)
            public AudioClip LoopClip;
        }

        private static V1State S(WeaponSoundPlayer player) => V1States.GetOrCreateValue(player);

        private static BaseSoundPlayer.IObserverToPlayerBridge Bridge(WeaponSoundPlayer player)
        {
            return Traverse.Create(player).Field("playersBridge").GetValue<BaseSoundPlayer.IObserverToPlayerBridge>();
        }

        private static bool IsLoopClip(AudioClip clip)
        {
            if (clip == null)
            {
                return false;
            }
            string name = clip.name;
            // legacy SPT banks mark their loopable body clips with "loop": "_loop" for
            // most, but some custom banks drop the underscore (WTT-Armory AK5C family:
            // "AK5COutdoorLoopClose"), which sent their 1.5 s salve clip to the live
            // engine and played a whole burst for every bullet. No live clip contains
            // "loop" outside "_loop_tail". Those "*_loop_tail" clips in the live banks'
            // distant layer are NOT old-format loop bodies (they caused the
            // misclassification that made live banks fall back to the legacy
            // beat-aligned tail scheduling).
            return name.IndexOf("loop", StringComparison.OrdinalIgnoreCase) >= 0
                && name.IndexOf("tail", StringComparison.OrdinalIgnoreCase) < 0;
        }

        private static readonly Dictionary<int, bool> LegacyBanks = new Dictionary<int, bool>();

        /// <summary>True when the bank uses the old SPT format (loop clips). Asked for every
        /// bullet, and reading clip names is costly: scanned once per bank.</summary>
        private static bool IsLegacyBank(SoundBank bank)
        {
            if (bank == null || bank.Environments == null || bank.Environments.Length == 0)
            {
                return true;
            }
            int id = bank.GetInstanceID();
            if (!LegacyBanks.TryGetValue(id, out bool legacy))
            {
                legacy = ScanForLoopClips(bank);
                LegacyBanks[id] = legacy;
            }
            return legacy;
        }

        private static bool ScanForLoopClips(SoundBank bank)
        {
            for (int e = 0; e < bank.Environments.Length; e++)
            {
                var variety = bank.Environments[e];
                for (int d = 0; d < variety.Length; d++)
                {
                    var clips = variety[d].Clips;
                    for (int i = 0; i < clips.Length; i++)
                    {
                        if (IsLoopClip(clips[i]))
                        {
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        [HarmonyPatch(typeof(WeaponSoundPlayer), "FireBullet")]
        [HarmonyPrefix]
        public static bool FireBulletPrefix(
            WeaponSoundPlayer __instance,
            Ammo ammo,
            Vector3 shotPosition,
            Vector3 shotDirection,
            float pitchMult,
            bool malfunctioned,
            bool multiShot,
            bool burstOf2Start)
        {
            try
            {
                LiveBankReplacement.Apply(__instance);
                SoundBank bank = multiShot
                    ? (__instance.IsSilenced ? __instance.DoubletSilenced : __instance.Doublet)
                    : (__instance.IsSilenced ? __instance.BodySilenced : __instance.Body);
                if (bank == null)
                {
                    return true; // original
                }

                bool legacy = IsLegacyBank(bank);
                if (Plugin.Config.LiveEngineEnabled.Value && !legacy)
                {
                    // ---- live engine path (v2.0) ----
                    LiveWeaponSoundEngine.GetOrCreate(__instance)
                        .FireBullet(ammo, shotPosition, shotDirection, pitchMult, multiShot, burstOf2Start);
                    return false;
                }

                // ---- v1.1.1 one-shot path (legacy banks) ----
                if (PluginConfig.LogShotDetails)
                {
                    Plugin.Log.LogWarning($"[fire] bank={bank.name} routed to the V1 engine dist={__instance.Distance:F1} " +
                        $"auto={__instance.IsAutoWeapon} silenced={__instance.IsSilenced} occluded={__instance.IsOccludedToListener}");
                }
                FireBulletV1(__instance, ammo, shotPosition, shotDirection, pitchMult, malfunctioned, multiShot, burstOf2Start, bank);
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"FireBullet patch failed, falling back to original: {e}");
                return true;
            }
        }

        [HarmonyPatch(typeof(WeaponSoundPlayer), "Init")]
        [HarmonyPostfix]
        public static void InitPostfix(WeaponSoundPlayer __instance)
        {
            try
            {
                LiveBankReplacement.Apply(__instance);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"live bank replacement failed: {e}");
            }
        }

        [HarmonyPatch(typeof(WeaponSoundPlayer), "StopFiringLoop")]
        [HarmonyPrefix]
        public static bool StopFiringLoopPrefix(WeaponSoundPlayer __instance)
        {
            try
            {
                SoundBank bank = __instance.IsSilenced
                    ? (__instance.BodySilenced != null ? __instance.BodySilenced : __instance.Body)
                    : (__instance.Body != null ? __instance.Body : __instance.BodySilenced);
                if (bank != null && Plugin.Config.LiveEngineEnabled.Value && !IsLegacyBank(bank))
                {
                    LiveWeaponSoundEngine.GetOrCreate(__instance).StopFiringLoop();
                    return false;
                }

                var state = S(__instance);
                if (state.LoopMode)
                {
                    StopLoopV1(__instance);
                    return false;
                }

                var t = Traverse.Create(__instance);
                t.Field("_isFiring").SetValue(false);
                if (state.LastBodyEnd > 0.0)
                {
                    t.Field("_releaseTime").SetValue(state.LastBodyEnd + 1.0);
                }
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"StopFiringLoop patch failed, falling back to original: {e}");
                return true;
            }
        }

        // ---- v1.1.1 logic (kept verbatim for legacy-format banks) ----------------

        private static void FireBulletV1(
            WeaponSoundPlayer __instance,
            Ammo ammo,
            Vector3 shotPosition,
            Vector3 shotDirection,
            float pitchMult,
            bool malfunctioned,
            bool multiShot,
            bool burstOf2Start,
            SoundBank soundBank)
        {
            var t = Traverse.Create(__instance);
            var bridge = Bridge(__instance);
            var state = S(__instance);
            bool isSilenced = __instance.IsSilenced;
            bool isAuto = __instance.IsAutoWeapon;
            bool firstPerson = bridge.PointOfView.IsFirstPerson();
            EnvironmentType environment = bridge.Environment;

            SuperBetterAudioQueue queue = t.Field("_queue").GetValue<SuperBetterAudioQueue>();
            bool isFiring = t.Field("_isFiring").GetValue<bool>();
            // the game ends a loop salve only when the trigger is released (BreakLoop), also in
            // semi: a 100 ms click played two beats of the loop for one bullet. Outside fullauto
            // and burst the salve is one bullet, ended at once like a released trigger (the live
            // engine sends these modes to its single-shot emitter the same way).
            bool triggerReleased = !bridge.isWeaponTriggerPressed || malfunctioned || burstOf2Start
                || !IsContinuousMode(bridge);

            if (isFiring && queue != null)
            {
                if (isAuto)
                {
                    __instance.Balance(soundBank);
                    if (!state.LoopMode)
                    {
                        EnqueueOneShot(__instance, queue, soundBank, pitchMult, environment, state);
                    }
                }
                __instance.UpdateMixerGroup();
                __instance.UpdatePitch(pitchMult);

                // a loop must end through StopFiringLoop, which schedules its tail: closing
                // the salve here cut the loop dry at release time
                if (isAuto && triggerReleased && !state.LoopMode)
                {
                    EndSalve(__instance, t, state);
                }
                return;
            }

            float distance = __instance.Distance;
            t.Field("_prevDistance").SetValue(distance);

            if (queue == null)
            {
                queue = Singleton<BetterAudio>.Instance.BorrowWeaponAudioQueue(BetterAudio.AudioSourceGroupType.Gunshots);
                if (queue == null)
                {
                    return;
                }
                queue.Pose(__instance.WeaponPosition);
                queue.SetRolloff(soundBank.Rolloff);
                queue.SetMixerGroup(MonoBehaviourSingleton<BetterAudio>.Instance.GunshotMixerGroup);
                t.Field("_queue").SetValue(queue);
                __instance.RegisterSourceForOcclusionProcess();
            }

            if (!(queue.AudioSources[0].OcclusionVolumeFactor > 0f))
            {
                return;
            }

            queue.SetMixerGroup(t.Field("_currentMixerGroup").GetValue<AudioMixerGroup>());
            queue.EnableStereo(firstPerson);

            AudioClip clip = null;
            AudioClip clip2 = null;
            float balance = 1f;
            soundBank.PickClipsByDistance(ref clip, ref clip2, ref balance, (int)environment, distance);
            t.Field("_balance").SetValue(balance);
            t.Field("_pitch").SetValue(pitchMult);
            t.Field("_prevPitchMult").SetValue(pitchMult);

            state.LoopMode = IsLoopClip(clip) || IsLoopClip(clip2);
            state.LoopClip = IsLoopClip(clip) ? clip : clip2;
            float length = Mathf.Max(clip != null ? clip.length : 0f, clip2 != null ? clip2.length : 0f);
            float delay = distance / SoundSpeed;
            double startFire = AudioSettings.dspTime;
            t.Field("_delay").SetValue(delay);
            t.Field("_startFire").SetValue(startFire);
            t.Field("_releaseTime").SetValue(startFire + length + 1.0);
            t.Field("_occlusionReleaseTime").SetValue(length / pitchMult / 6f);

            if (isAuto)
            {
                t.Field("_isFiring").SetValue(true);
            }

            __instance.Balance(soundBank);
            __instance.EnableSourceOcclusion(true);
            __instance.UpdateMixerGroup();

            if (state.LoopMode)
            {
                queue.Enqueue(clip, clip2, balance, startFire + delay, isAuto ? 0f : ClipTime(length, pitchMult), soundBank.BaseVolume * LiveMixer.GunshotGain, pitchMult);
                if (!isAuto)
                {
                    __instance.ReleaseOcclusion(length / pitchMult / 6f);
                }
                if (isAuto && triggerReleased)
                {
                    ScheduleLoopTail(__instance, t, state, isSilenced, environment, pitchMult, startFire, delay,
                        burstOf2Start ? 2 : 1);
                }
                __instance.FireSonicSound(delay, soundBank.Rolloff, ammo, shotPosition, shotDirection);
                return;
            }

            double bodyEnd = startFire + delay + length / pitchMult;
            queue.Enqueue(clip, clip2, balance, startFire + delay, ClipTime(length, pitchMult), soundBank.BaseVolume * LiveMixer.GunshotGain, pitchMult);
            state.LastBodyEnd = bodyEnd;

            if (!isAuto)
            {
                __instance.ReleaseOcclusion(length / pitchMult / 6f);
            }
            else if (triggerReleased)
            {
                EndSalve(__instance, t, state);
            }

            __instance.FireSonicSound(delay, soundBank.Rolloff, ammo, shotPosition, shotDirection);
        }

        /// <summary>True when one trigger pull fires several bullets (fullauto, burst), as in
        /// LiveWeaponSoundEngine. Unknown weapons keep SPT's trigger-driven behaviour.</summary>
        private static bool IsContinuousMode(BaseSoundPlayer.IObserverToPlayerBridge bridge)
        {
            Weapon weapon = bridge.Weapon as Weapon;
            return weapon == null
                || weapon.SelectedFireMode == Weapon.EFireMode.fullauto
                || weapon.SelectedFireMode == Weapon.EFireMode.burst;
        }

        private static void EnqueueOneShot(
            WeaponSoundPlayer __instance,
            SuperBetterAudioQueue queue,
            SoundBank soundBank,
            float pitchMult,
            EnvironmentType environment,
            V1State state)
        {
            AudioClip clip = null;
            AudioClip clip2 = null;
            float balance = 1f;
            float distance = __instance.Distance;
            soundBank.PickClipsByDistance(ref clip, ref clip2, ref balance, (int)environment, distance);

            float length = Mathf.Max(clip != null ? clip.length : 0f, clip2 != null ? clip2.length : 0f);
            if (length <= 0f)
            {
                return;
            }

            double start = AudioSettings.dspTime + 0.02;
            queue.Enqueue(clip, clip2, balance, start, ClipTime(length, pitchMult), soundBank.BaseVolume * LiveMixer.GunshotGain, pitchMult);
            state.LastBodyEnd = start + length / pitchMult;
        }

        /// <summary>Queue play time of a clip, stopped <see cref="GunshotEmitterBase.EndMargin"/>
        /// before its end: the queue stops the source exactly at the end otherwise, where the
        /// resampler reads the first sample again. Legacy banks (WTT-Armory...) mostly start at
        /// full scale (0.5 to 0.75 on the close loops): a dry click at the end of every shot and
        /// tail, like the one the live engine removes in ScheduleEndBeforeWrap.</summary>
        private static float ClipTime(float length, float pitch)
        {
            return Mathf.Max(0.001f, length / Mathf.Max(0.01f, pitch) - (float)GunshotEmitterBase.EndMargin);
        }

        /// <summary>Clip seconds a loop salve is cut before its beat (the tail starts there).
        /// SPT cuts at ClipLength / 16, but the shots of most custom loops do not sit on that
        /// grid: the AK5C's next shot starts 3 ms before each beat, so 3 ms of its attack played
        /// and were cut dead, a dry click after every single shot and salve. Measured over the
        /// 425 loop clips of the WTT legacy banks: 2 to 5 ms for most, 12 ms at most (loops
        /// drifting off the grid). Cutting earlier only shortens the decay the tail covers.</summary>
        internal const float LoopCutLead = 0.015f;

        /// <summary>Window of the gap that ends the shot of a beat, in beats around that beat's
        /// grid line. Before: the silenced KAC PDW's gaps sit 0.58 beat before the line (its
        /// shots start 18 ms early, then ring 30 ms); this also keeps a first shot 0.35 beat
        /// long at least. After: the unsilenced PDW's gaps fall up to 0.08 beat after the line,
        /// and later gaps belong to the next shot (a window of one beat from the release cut an
        /// AEK 3-round salve at 260 ms instead of 183: a 4th shot heard).</summary>
        private const float CutWindowBefore = 0.65f;
        private const float CutWindowAfter = 0.2f;

        /// <summary>Clip time of the cut ending the shot of the beat at <paramref name="beatTime"/>:
        /// the quietest gap of its window from <paramref name="earliest"/> on, else the grid.</summary>
        private static bool TryCut(V1State state, float beatTime, float beatLn, float earliest, out float cut)
        {
            float from = Mathf.Max(earliest, beatTime - CutWindowBefore * beatLn);
            return LegacyLoopCuts.TryChoose(state.LoopClip, from, beatTime + CutWindowAfter * beatLn, GridCut(beatTime), out cut);
        }

        /// <summary>Clip time of the grid cut on <paramref name="beatTime"/>.</summary>
        private static float GridCut(float beatTime)
        {
            return Mathf.Max(0f, beatTime - LoopCutLead);
        }

        /// <summary>Real time from the start of a loop to a cut at <paramref name="clipTime"/>.</summary>
        private static float ToRealTime(float clipTime, float pitch)
        {
            return clipTime / Mathf.Max(0.01f, pitch);
        }

        private static void LogCut(V1State state, string kind, float grid, float cut)
        {
            if (PluginConfig.LogShotDetails)
            {
                Plugin.Log.LogWarning($"[loop] {kind} clip={state.LoopClip?.name} grid={grid * 1000f:F0}ms cut={cut * 1000f:F0}ms");
            }
        }

        /// <summary>WeaponSoundPlayer.StopFiringLoop (tail of a loop salve), with the loop cut in
        /// the quietest gap of the next beat (<see cref="LegacyLoopCuts"/>, else on the grid before
        /// the next attack, see <see cref="LoopCutLead"/>) and the tail stopped before its end
        /// (see <see cref="ClipTime"/>).</summary>
        private static void StopLoopV1(WeaponSoundPlayer __instance)
        {
            var t = Traverse.Create(__instance);
            var state = S(__instance);
            double startFire = t.Field("_startFire").GetValue<double>();
            float delay = t.Field("_delay").GetValue<float>();
            float pitch = Mathf.Max(0.01f, t.Field("_pitch").GetValue<float>());
            float beatLn = __instance.BeatLn;
            int beats = (int)((AudioSettings.dspTime - startFire) / beatLn) + 1;
            // clip time the loop plays now. No margin for the frame the queue takes to apply
            // the cut: a cut a few ms late stops at the next audio buffer, still in the gap,
            // while a margin skipped gaps not reached yet (see below).
            float playing = (float)(AudioSettings.dspTime - startFire - delay) * pitch;
            float beatTime = beats * beatLn;
            if (!TryCut(state, beatTime, beatLn, playing, out float clipCut) && clipCut < playing
                && LegacyLoopCuts.Get(state.LoopClip) != null)
            {
                // past this beat's gap and grid cut, the next shot is already playing (the
                // silenced PDW's gap is 0.58 beat before the line): it is ended at its own gap
                // rather than cut at once inside it. Counted from the gap actually passed, this
                // plays a cut more than a beat after the release in 5 % of the releases over the
                // WTT loops; with a 30 ms margin it was 35 %, often a whole extra shot.
                beatTime += beatLn;
                TryCut(state, beatTime, beatLn, playing, out clipCut);
            }
            LogCut(state, "release", GridCut(beatTime), clipCut);
            float cut = ToRealTime(clipCut, pitch);
            double tailStart = startFire + delay + cut;
            t.Field("_startPlayingTime").SetValue(tailStart);

            float tailLength = 0f;
            SoundBank tail = __instance.IsSilenced ? __instance.TailSilenced : __instance.Tail;
            if (tail != null)
            {
                AudioClip clip = null;
                AudioClip clip2 = null;
                float balance = t.Field("_balance").GetValue<float>();
                tail.PickClipsByDistance(ref clip, ref clip2, ref balance, (int)Bridge(__instance).Environment, __instance.Distance);
                t.Field("_balance").SetValue(balance);
                tailLength = Mathf.Max(clip != null ? clip.length : 0f, clip2 != null ? clip2.length : 0f);
                t.Field("_queue").GetValue<SuperBetterAudioQueue>()?.Enqueue(clip, clip2, balance, tailStart, ClipTime(tailLength, pitch), tail.BaseVolume * LiveMixer.GunshotGain, pitch);
            }

            float occlusionRelease = delay + cut;
            t.Field("_isFiring").SetValue(false);
            t.Field("_occlusionReleaseTime").SetValue(occlusionRelease);
            t.Field("_releaseTime").SetValue(tailStart + tailLength / pitch);
            __instance.ReleaseOcclusion(occlusionRelease);
        }

        private static void EndSalve(WeaponSoundPlayer __instance, Traverse t, V1State state)
        {
            t.Field("_isFiring").SetValue(false);
            if (state.LastBodyEnd > 0.0)
            {
                t.Field("_releaseTime").SetValue(state.LastBodyEnd + 1.0);
                __instance.ReleaseOcclusion(Mathf.Max(0.05f, (float)(state.LastBodyEnd - AudioSettings.dspTime)));
            }
        }

        private static void ScheduleLoopTail(
            WeaponSoundPlayer __instance,
            Traverse t,
            V1State state,
            bool isSilenced,
            EnvironmentType environment,
            float pitchMult,
            double startFire,
            float delay,
            int beats)
        {
            var queue = t.Field("_queue").GetValue<SuperBetterAudioQueue>();
            if (queue == null)
            {
                t.Field("_isFiring").SetValue(false);
                return;
            }

            float firingLoopLength = t.Field("_firingLoopLength").GetValue<float>();
            float beatLn = firingLoopLength / 16f;

            SoundBank tail = isSilenced ? __instance.TailSilenced : __instance.Tail;
            if (tail == null)
            {
                t.Field("_isFiring").SetValue(false);
                return;
            }

            AudioClip clip3 = null;
            AudioClip clip4 = null;
            float balance = 1f;
            tail.PickClipsByDistance(ref clip3, ref clip4, ref balance, (int)environment, __instance.Distance);
            float tailLength = Mathf.Max(clip3 != null ? clip3.length : 0f, clip4 != null ? clip4.length : 0f);
            // the salve is already over: one bullet, or two for a 2-round burst, whose second
            // shot plays no sound of its own (SPT plays two beats for it; up to 1.0.1 this
            // played one, so a 2-round burst sounded like a single shot). The quietest gap
            // around that beat, the grid cut otherwise.
            float beatTime = beats * beatLn;
            TryCut(state, beatTime, beatLn, 0f, out float clipCut);
            LogCut(state, beats == 2 ? "burst2" : "single", GridCut(beatTime), clipCut);
            float beat = ToRealTime(clipCut, pitchMult);

            double tailStart = startFire + delay + beat;
            queue.Enqueue(clip3, clip4, balance, tailStart, ClipTime(tailLength, pitchMult), tail.BaseVolume * LiveMixer.GunshotGain, pitchMult);
            t.Field("_isFiring").SetValue(false);
            t.Field("_startPlayingTime").SetValue(tailStart);
            t.Field("_releaseTime").SetValue(tailStart + tailLength / pitchMult);
            t.Field("_occlusionReleaseTime").SetValue(delay + beat);
            __instance.ReleaseOcclusion(delay + beat);
        }
    }

    [HarmonyPatch]
    public static class LiveOcclusionPatch
    {
        [HarmonyPatch(typeof(Audio.SpatialSystem.SpatialAudioOccluder), "ApplyOcclusionToSource")]
        [HarmonyPostfix]
        public static void ApplyOcclusionToSourcePostfix(Audio.SpatialSystem.SpatialAudioOccluder __instance, BetterSource source, bool updateImmediately)
        {
            if (__instance.OcclusionEffect <= LiveAudioPatches.NoOcclusionEffect)
            {
                LiveFilterBlend.ClearOcclusionIfRegistered(source);
            }
            LiveOcclusionModule.Postfix(source, updateImmediately);
        }

        [HarmonyPatch(typeof(WeaponSoundPlayer), "OnDisable")]
        [HarmonyPrefix]
        public static void OnDisablePrefix(WeaponSoundPlayer __instance)
        {
            try
            {
                LiveWeaponSoundEngine.NotifyDisabled(__instance);
            }
            catch (Exception)
            {
            }
        }

        [HarmonyPatch(typeof(WeaponSoundPlayer), "OnDestroy")]
        [HarmonyPostfix]
        public static void OnDestroyPostfix(WeaponSoundPlayer __instance)
        {
            try
            {
                LiveWeaponSoundEngine.NotifyDestroyed(__instance);
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>What the live game does differently around the shot engine.</summary>
    [HarmonyPatch]
    public static class LiveAudioPatches
    {
        // live SpatialAudioOccluder.ApplyOcclusionToSource: at or below this effect, no filter
        internal const float NoOcclusionEffect = 0.001f;

        [HarmonyPatch(typeof(BetterAudio), "CreateSoundManagers")]
        [HarmonyPrefix]
        public static void CreateSoundManagersPrefix(BetterAudio __instance)
        {
            LiveMixer.Install(__instance);
            LiveWeaponSoundEngine.EnsureManager();
        }

        [HarmonyPatch(typeof(BetterAudio), "LoadGroupPreset")]
        [HarmonyPostfix]
        public static void LoadGroupPresetPostfix(string groupName, AudioGroupPreset __result)
        {
            LiveGroupPresets.Apply(__result, groupName);
        }

        [HarmonyPatch(typeof(BetterAudio), "GetSource", new[] { typeof(BetterAudio.AudioSourceGroupType), typeof(bool), typeof(bool) })]
        [HarmonyPostfix]
        public static void GetSourcePostfix(BetterAudio.AudioSourceGroupType sourceType, BetterSource __result)
        {
            if (sourceType == BetterAudio.AudioSourceGroupType.Grenades && __result != null)
            {
                LiveFilterBlend.TrackGrenadeSource(__result);
            }
        }

        [HarmonyPatch(typeof(BetterSource), "CheckBinauralAllowed")]
        [HarmonyPrefix]
        public static bool CheckBinauralAllowedPrefix(BetterSource __instance)
        {
            return !LiveDynamicHrtf.TryUpdate(__instance);
        }

        [HarmonyPatch(typeof(BetterAudio), "LimitedPlay")]
        [HarmonyPrefix]
        public static void LimitedPlayPrefix(SoundBank bank, ref float chokeTime)
        {
            LiveLimitedPlay.AdjustChokeTime(bank, ref chokeTime);
        }

        [HarmonyPatch(typeof(Audio.SpatialSystem.CombinedSpatialAudioOccluder), "SetAudioSourceContainer")]
        [HarmonyPostfix]
        public static void CombinedSetContainerPostfix(Audio.SpatialSystem.CombinedSpatialAudioOccluder __instance)
        {
            LiveOcclusionGate.Reset(__instance);
        }

        [HarmonyPatch(typeof(Audio.SpatialSystem.CombinedSpatialAudioOccluder), "OnLateUpdate")]
        [HarmonyPostfix]
        public static void CombinedLateUpdatePostfix(Audio.SpatialSystem.CombinedSpatialAudioOccluder __instance)
        {
            LiveOcclusionGate.MarkCalculated(__instance);
        }

        [HarmonyPatch(typeof(Audio.SpatialSystem.CombinedSpatialAudioOccluder), "OnUpdateOcclusionEffects")]
        [HarmonyPrefix]
        public static bool CombinedEffectsPrefix(Audio.SpatialSystem.CombinedSpatialAudioOccluder __instance, Audio.SpatialSystem.AudioSourceContainer sourceContainer, ref bool updateImmediately)
        {
            return LiveOcclusionGate.BeforeEffects(__instance, sourceContainer, ref updateImmediately);
        }

        [HarmonyPatch(typeof(BetterAudio), "FindMixerGroup")]
        [HarmonyPrefix]
        public static void FindMixerGroupPrefix(ref string groupName)
        {
            groupName = LiveMixer.MapGroupName(groupName);
        }

        // trader visits: the narrate fades must also reach SPT's mixer, the live one plays into it
        [HarmonyPatch(typeof(BetterAudio), "ToggleNarrate")]
        [HarmonyPostfix]
        public static void ToggleNarratePostfix(BetterAudio __instance, bool inScene, float seconds)
        {
            LiveMixer.MirrorNarrate(__instance, inScene, seconds);
        }

        [HarmonyPatch(typeof(BetterAudio), "ResetWorldMixerValues")]
        [HarmonyPostfix]
        public static void ResetWorldMixerValuesPostfix(BetterAudio __instance)
        {
            LiveMixer.RestoreWorldVolume(__instance);
        }

        [HarmonyPatch(typeof(BetterSource), "SetLowPassFilterParameters")]
        [HarmonyPrefix]
        public static bool SetLowPassPrefix(BetterSource __instance, float cutoffFreq, float targetQ, bool applyImmediately)
        {
            return !LiveFilterBlend.TryInterceptLowPass(__instance, cutoffFreq, targetQ, applyImmediately);
        }

        [HarmonyPatch(typeof(BetterSource), "SetHighPassFilterParameters")]
        [HarmonyPrefix]
        public static bool SetHighPassPrefix(BetterSource __instance, float cutoffFreq, float targetQ, bool applyImmediately)
        {
            return !LiveFilterBlend.TryInterceptHighPass(__instance, cutoffFreq, targetQ, applyImmediately);
        }
    }
}
