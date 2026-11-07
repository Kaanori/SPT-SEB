// Audio data of the live game that SPT does not have: the master mixer, the sound group
// presets, the casing sound limits and the per-weapon AudioSettings of the item templates.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using EFT;
using EFT.CameraControl;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Audio;

namespace SoundEngineBackport.LiveEngine
{
    /// <summary>Loads the live MasterMixer in place of the one SPT finds in its Resources.
    /// Falls back to SPT's mixer if the bundle is missing or incomplete.</summary>
    internal static class LiveMixer
    {
        private const string BundleFile = "live_mastermixer.bundle";
        private const string LiveBypassGroup = "World/Bypass";
        private const string SptBypassGroup = "NonspatialBypass";
        private const string LiveBypassLeaf = "Bypass";
        private const string MasterVolume = "MasterVolume";
        private const string WorldVolumeParameter = "WorldVolume";
        internal const string AmbientVolumeParameter = "AmbientVolume";
        internal const string AmbientSendParameter = "AmbientCompressorSendLevel";
        private const string HideoutVolume = "HideoutVolume";
        private const string HideoutLowPass = "HideoutLowPass";
        private const string InGameVolume = "InGame";
        private const string MusicVolume = "MusicVolume";
        private const float MutedDb = -80f;
        private const float HideoutFadeSeconds = 1f;
        private const float HideoutLowPassOpenHz = 22000f;

        private static AudioMixer _mixer;

        internal static bool Enabled = true;

        /// <summary>Live reads this bus from the backend (MasterMixerSettings), which is not in the
        /// client. The +4 dB of the mixer asset is only its default, and clips peak at 0 dBFS. The
        /// default of the setting is SPT's 0 dB.</summary>
        internal static float WorldVolumeDb;

        /// <summary>Level of the ambience alone, on top of the headset template.</summary>
        internal static float AmbientTrimDb;

        public static bool Active { get; private set; }

        /// <summary>Live Gunshots preset OverallVolume. Only used with the live mixer, whose limiter
        /// and group levels it was set for.</summary>
        public static float GunshotGroupVolume =>
            (Active && LiveGroupPresets.Enabled ? LiveGroupPresetTable.Groups["Gunshots"].OverallVolume : 1f) * GunshotGain;

        /// <summary>Gunshots slider of the plugin, with or without the live mixer.</summary>
        internal static float GunshotGain = 1f;

        public static void Install(BetterAudio betterAudio)
        {
            if (!Enabled)
            {
                return;
            }
            try
            {
                AudioMixer mixer = Load();
                if (mixer == null)
                {
                    return;
                }
                string[] names = Enum.GetNames(typeof(EnvironmentType));
                var snapshots = new AudioMixerSnapshot[names.Length];
                for (int i = 0; i < names.Length; i++)
                {
                    snapshots[i] = mixer.FindSnapshot(names[i]);
                    if (snapshots[i] == null)
                    {
                        Plugin.Log.LogError($"live mixer has no {names[i]} snapshot, keeping SPT's mixer");
                        return;
                    }
                }
                betterAudio.Master = mixer;
                betterAudio.Snapshots = snapshots;
                Active = true;
                Plugin.Log.LogInfo("live master mixer installed");
                InstallRouting(mixer);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"could not install the live mixer, keeping SPT's: {e}");
            }
        }

        /// <summary>The live mixer is in place at this point: a failure here must not say otherwise.</summary>
        private static void InstallRouting(AudioMixer mixer)
        {
            try
            {
                ChainIntoGameMixer(mixer);
                LiveMixerRouting.Install(mixer);
                CopySetting(HideoutVolume);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"live mixer routing failed, some sounds stay on SPT's mixer: {e}");
            }
        }

        /// <summary>The Meta XR reverb is native and shared: two Reflection effects read it in turn,
        /// which cuts it into bursts of noise indoors. The bundle bypasses the live one, and the
        /// live mixer plays through SPT's InGame group, where SPT's runs, as all sounds did in SPT.
        /// SPT's Master applies the volume setting and its own -1 dB compressor after the live one.</summary>
        private static void ChainIntoGameMixer(AudioMixer mixer)
        {
            AudioMixer game = EFT.Settings.Sound.SoundSettingsController.MasterMixer;
            AudioMixerGroup inGame = null;
            if (game != null && game != mixer)
            {
                foreach (AudioMixerGroup group in game.FindMatchingGroups(InGameVolume))
                {
                    if (group.name == InGameVolume)
                    {
                        inGame = group;
                        break;
                    }
                }
            }
            if (inGame == null)
            {
                // the live Reflection is bypassed: without this chain the live sounds lose their reverb
                Plugin.Log.LogError("SPT's InGame group not found: the live mixer plays without the Meta XR reverb");
                CopySetting(MasterVolume);
                return;
            }
            mixer.outputAudioMixerGroup = inGame;
            mixer.SetFloat(MasterVolume, 0f);
            Plugin.Log.LogInfo("live mixer chained into SPT's InGame group (single Meta XR reverb)");
        }

        private static AudioMixer Load()
        {
            if (_mixer != null)
            {
                return _mixer;
            }
            string path = Path.Combine(Path.GetDirectoryName(Plugin.Location) ?? "", BundleFile);
            if (!File.Exists(path))
            {
                Plugin.Log.LogError($"{path} is missing, keeping SPT's mixer");
                return null;
            }
            AssetBundle bundle = AssetBundle.LoadFromFile(path);
            if (bundle == null)
            {
                Plugin.Log.LogError($"{path} could not be loaded, keeping SPT's mixer");
                return null;
            }
            // kept loaded: the mixer lives as long as the game
            AudioMixer[] mixers = bundle.LoadAllAssets<AudioMixer>();
            _mixer = mixers.Length > 0 ? mixers[0] : null;
            return _mixer;
        }

        /// <summary>The only group SPT looks for that live renamed.</summary>
        public static string MapGroupName(string groupName)
        {
            return Active && groupName == SptBypassGroup ? LiveBypassGroup : groupName;
        }

        public static string MapLeafName(string groupName)
        {
            return groupName == SptBypassGroup ? LiveBypassLeaf : groupName;
        }

        /// <summary>Sets the World bus. SPT resets it to 0 dB, and the live mixer asset defaults to +4 dB.</summary>
        public static void RestoreWorldVolume(BetterAudio betterAudio)
        {
            if (Active)
            {
                betterAudio.Master.SetFloat(WorldVolumeParameter, WorldVolumeDb);
            }
        }

        public static void ApplyAmbientVolume()
        {
            if (Active && MonoBehaviourSingleton<BetterAudio>.Instantiated)
            {
                BetterAudio audio = MonoBehaviourSingleton<BetterAudio>.Instance;
                EFT.InventoryLogic.HeadphonesTemplate template = audio.CurrentHeadphonesTemplate;
                audio.Master.SetFloat(AmbientVolumeParameter, TrimmedAmbientVolume(template));
                audio.Master.SetFloat(AmbientSendParameter, TrimmedAmbientSend(template));
            }
        }

        public static float TrimmedAmbientVolume(EFT.InventoryLogic.HeadphonesTemplate template)
        {
            return Mathf.Max(MutedDb, template.AmbientVolume + AmbientTrimDb);
        }

        public static float TrimmedAmbientSend(EFT.InventoryLogic.HeadphonesTemplate template)
        {
            return Mathf.Max(MutedDb, template.AmbientCompressorSendLevel + AmbientTrimDb);
        }

        public static void ApplyWorldVolume()
        {
            LiveGroupPresets.UpdateHandlingVolume();
            if (MonoBehaviourSingleton<BetterAudio>.Instantiated)
            {
                RestoreWorldVolume(MonoBehaviourSingleton<BetterAudio>.Instance);
            }
        }

        public static void MirrorSetting(string channel)
        {
            // MasterVolume is applied once, by SPT's mixer that the live one plays into
            if (channel == HideoutVolume)
            {
                CopySetting(channel);
            }
        }

        public static void MirrorHideoutSound(bool active)
        {
            if (!Active || !MonoBehaviourSingleton<BetterAudio>.Instantiated)
            {
                return;
            }
            BetterAudio audio = MonoBehaviourSingleton<BetterAudio>.Instance;
            int hideoutVolume = Comfort.Common.Singleton<EFT.Settings.SettingsManager>.Instance.Sound.Settings.HideoutVolumeValue;
            audio.FadeMixerVolume(InGameVolume, active ? 0f : MutedDb, HideoutFadeSeconds);
            audio.FadeMixerVolume(HideoutVolume, active ? hideoutVolume : MutedDb, HideoutFadeSeconds);
        }

        /// <summary>BetterAudio.ToggleNarrate (trader visits) fades InGame and MusicVolume on
        /// BetterAudio.Master, which was SPT's mixer. The live mixer plays through SPT's InGame group
        /// and the menu music through SPT's Music group, so both fades must reach SPT's mixer too.
        /// Without this, SPT's InGame stays at the -80 dB the main menu set
        /// (MenuScreen.MainMenuBaseScreenController.ShowAction -> GUISounds.method_11(false)) and the
        /// whole visit, voices included, is silent. Raids are not affected only because
        /// LocalGame calls AudioSettings.Reset (AudioUtils.ResetAudioBuffer) before the raid.</summary>
        public static void MirrorNarrate(BetterAudio betterAudio, bool inScene, float seconds)
        {
            AudioMixer game = EFT.Settings.Sound.SoundSettingsController.MasterMixer;
            if (!Active || betterAudio == null || game == null || game == betterAudio.Master || Plugin.Instance == null)
            {
                return;
            }
            int music = Comfort.Common.Singleton<EFT.Settings.SettingsManager>.Instance.Sound.Settings.MusicVolumeValue;
            Plugin.Instance.StartCoroutine(FadeGameMixer(game, InGameVolume, inScene ? 0f : MutedDb, seconds));
            Plugin.Instance.StartCoroutine(FadeGameMixer(game, MusicVolume, inScene ? MutedDb : music, seconds));
        }

        private static IEnumerator FadeGameMixer(AudioMixer mixer, string parameter, float endDb, float seconds)
        {
            if (!mixer.GetFloat(parameter, out float startDb))
            {
                yield break;
            }
            for (float t = 0f; t < seconds; t += Time.unscaledDeltaTime)
            {
                mixer.SetFloat(parameter, Mathf.Lerp(startDb, endDb, t / seconds));
                yield return null;
            }
            mixer.SetFloat(parameter, endDb);
        }

        public static void MirrorHideoutLowPass(bool active, float seconds)
        {
            if (Active && MonoBehaviourSingleton<BetterAudio>.Instantiated)
            {
                MonoBehaviourSingleton<BetterAudio>.Instance.FadeMixerVolume(HideoutLowPass, active ? HideoutLowPassOpenHz : 0f, seconds);
            }
        }

        private static void CopySetting(string channel)
        {
            if (Active && _mixer != null && EFT.Settings.Sound.SoundSettingsController.MasterMixer.GetFloat(channel, out float value))
            {
                _mixer.SetFloat(channel, value);
            }
        }
    }

    /// <summary>Volume, curves and HRTF of the live AudioGroupPreset of each sound group. Sources
    /// take them from the preset when they are created. SPT's source limits stay: this engine
    /// holds up to 32 sources per weapon where live holds 15.</summary>
    internal static class LiveGroupPresets
    {
        private const string HandlingGroup = "Weaponary";
        private const float AssetWorldVolumeDb = 4f;

        /// <summary>A preset handed to the game, and its volume before this plugin touched it
        /// (LoadGroupPreset sets it from the backend each time).</summary>
        private sealed class TrackedPreset
        {
            public AudioGroupPreset Preset;
            public float SptVolume;
        }

        private static readonly Dictionary<string, TrackedPreset> Tracked = new Dictionary<string, TrackedPreset>();
        private static readonly Dictionary<string, float> TrimDb = new Dictionary<string, float>();

        internal static bool Enabled = true;

        /// <summary>The live 0.4 of the handling sounds goes with the +4 dB of the mixer asset: this
        /// keeps that absolute level when the World bus is lower, up to the source maximum of 1.</summary>
        public static void UpdateHandlingVolume()
        {
            UpdateVolume(HandlingGroup);
        }

        /// <summary>Volume slider of a group, in dB on top of its live (or SPT) volume. Sources read
        /// the preset at each play, so it applies during a raid.</summary>
        public static void SetTrim(string groupName, float db)
        {
            TrimDb[groupName] = db;
            UpdateVolume(groupName);
        }

        private static void UpdateVolume(string groupName)
        {
            if (!Tracked.TryGetValue(groupName, out TrackedPreset tracked) || tracked.Preset == null)
            {
                return;
            }
            float volume = tracked.SptVolume;
            // the live volumes go with the live mixer
            if (Enabled && LiveMixer.Active && LiveGroupPresetTable.Groups.TryGetValue(groupName, out var live))
            {
                volume = live.OverallVolume;
                if (groupName == HandlingGroup)
                {
                    volume *= Mathf.Pow(10f, (AssetWorldVolumeDb - LiveMixer.WorldVolumeDb) / 20f);
                }
            }
            if (TrimDb.TryGetValue(groupName, out float db))
            {
                volume *= LiveAudioMath.DbToGain(db);
            }
            tracked.Preset.OverallVolume = Mathf.Min(1f, volume);
        }

        public static void Apply(AudioGroupPreset preset, string groupName)
        {
            if (preset == null)
            {
                return;
            }
            if (Enabled && LiveGroupPresetTable.Groups.TryGetValue(groupName, out var live))
            {
                preset.SoundRolloff = live.Rolloff;
                preset.SpreadCurve = live.Spread;
                preset.OcclusionSourceRadius = live.OcclusionSourceRadius;
                LiveDynamicHrtf.Configure(preset, live);
            }
            bool liveVolume = Enabled && LiveMixer.Active && LiveGroupPresetTable.Groups.ContainsKey(groupName);
            if (liveVolume || TrimDb.ContainsKey(groupName))
            {
                Tracked[groupName] = new TrackedPreset { Preset = preset, SptVolume = preset.OverallVolume };
                UpdateVolume(groupName);
            }
        }
    }

    /// <summary>Live BetterSource.CheckBinauralAllowed: SPT switches the HRTF on and off with
    /// the distance, live keeps it on and doses it. Groups on the simple spatializer are left
    /// alone: their direct sound has no HRTF on live.</summary>
    internal static class LiveDynamicHrtf
    {
        private static readonly AccessTools.FieldRef<BetterSource, bool> ForceStereoRef =
            AccessTools.FieldRefAccess<BetterSource, bool>("_forceStereo");
        private static readonly AccessTools.FieldRef<BetterSource, AudioGroupPreset> PresetRef =
            AccessTools.FieldRefAccess<BetterSource, AudioGroupPreset>("Preset");
        private static readonly Dictionary<BetterAudio.AudioSourceGroupType, LiveGroupPresetTable.Entry> DynamicGroups =
            new Dictionary<BetterAudio.AudioSourceGroupType, LiveGroupPresetTable.Entry>();

        public static void Configure(AudioGroupPreset preset, LiveGroupPresetTable.Entry live)
        {
            if (live.UseSimpleSpatializer)
            {
                return;
            }
            preset.HrtfIntensity = live.HrtfIntensity;
            preset.DisableBinauralByDist = false;
            if (live.DynamicHrtf)
            {
                DynamicGroups[preset.Type] = live;
            }
        }

        /// <summary>Can change during a raid: SPT's check takes over from the next frame.</summary>
        internal static bool Enabled = true;

        /// <summary>False when SPT's own check must run.</summary>
        public static bool TryUpdate(BetterSource source)
        {
            if (!Enabled)
            {
                return false;
            }
            AudioGroupPreset preset = PresetRef(source);
            if (preset == null || !DynamicGroups.TryGetValue(preset.Type, out var live)
                || ForceStereoRef(source) || !preset.DirectBinaural || !preset.SteamSpatialize)
            {
                return false;
            }
            CameraManager camera = CameraManager.Instance;
            if (camera == null || camera.Camera == null)
            {
                return false;
            }
            Transform transform = camera.Camera.transform;
            Vector3 forward = transform.forward;
            Vector3 toSource = source.Position - transform.position;
            float angle = Vector2.SignedAngle(new Vector2(forward.x, forward.z), new Vector2(toSource.x, toSource.z));
            float target = LiveAudioMath.ComputeDynamicHrtf(
                camera.SqrDistance(source.Position),
                Mathf.Abs(angle),
                Mathf.Abs(toSource.y),
                live.HrtfMinDistance * live.HrtfMinDistance,
                live.HrtfMinAngle,
                live.HrtfMinHeight,
                live.HrtfIntensity);
            source.RefreshSpatialization(true);
            source.HrtfIntensity = Mathf.Lerp(source.HrtfIntensity, target, Time.deltaTime * live.HrtfSmoothSpeed);
            source.UpdateParameters();
            return true;
        }
    }

    /// <summary>Live SoundBank.LimitPercentOfClip of the shell casing banks, the only ones with
    /// AllowLimitedPlay among the 632 banks of the live audio bundles. SPT's SoundBank drops
    /// the field when it loads a bundle.</summary>
    internal static class LiveLimitedPlay
    {
        private const float PercentOfClip = 0.6f;
        private const string ShellBankPrefix = "Shell";

        public static void AdjustChokeTime(SoundBank bank, ref float chokeTime)
        {
            if (bank != null && bank.AllowLimitedPlay && bank.name.StartsWith(ShellBankPrefix, StringComparison.Ordinal))
            {
                // SPT's GagSoundLimiter multiplies this by the clip length, like live
                chokeTime = PercentOfClip;
            }
        }
    }

    /// <summary>Live WeaponTemplateAudioSettings. SPT's item templates have none: these are the
    /// values of all 190 live weapon prefabs and of the live templates carried by
    /// WTT-ContentBackport, which are the only source of per-weapon differences.</summary>
    internal static class LiveWeaponAudioSettings
    {
        private const float OutdoorDistance = 600f;
        private const float IndoorDistance = 200f;
        private const float OutdoorSilencedDistance = 300f;
        private const float IndoorSilencedDistance = 120f;

        private static readonly Dictionary<string, float> UnsilencedVolumes = new Dictionary<string, float>
        {
            ["69f9ebbcaae020b0db02f65d"] = 0.92f,
        };

        public static void Get(string templateId, EnvironmentType environment, bool isSilenced, out float maxDistance, out float volume)
        {
            bool indoor = environment == EnvironmentType.Indoor;
            maxDistance = isSilenced
                ? (indoor ? IndoorSilencedDistance : OutdoorSilencedDistance)
                : (indoor ? IndoorDistance : OutdoorDistance);
            volume = 1f;
            if (!isSilenced && templateId != null && UnsilencedVolumes.TryGetValue(templateId, out float weaponVolume))
            {
                volume = weaponVolume;
            }
        }
    }
}
