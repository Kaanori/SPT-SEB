// Ambient blend values of the live scene (level397), which SPT's scene holds in older versions.
// AmbientSoundBlender: SPT's 0.3 s fade to outdoor is under its 0.5 s minimum, so it was applied
// at once, and its curves leave the outdoor ambience much louder inside buildings.
using Audio.AmbientSubsystem;
using Audio.AmbientSubsystem.Data;
using Audio.SpatialSystem;
using EFT;
using EFT.ActiveHeadphones;
using EFT.InventoryLogic;
using EFT.Settings.Sound;
using EFT.UI;
using HarmonyLib;
using UnityEngine;

namespace SoundEngineBackport.LiveEngine
{
    internal static class LiveAmbientPort
    {
        private const string MainBlenderName = "AmbientSoundBlender";
        private const float FadeOutDuration = 0.8f;
        private const float FromOutToInTransitionTime = 0.3f;
        private const float IndoorPrecipitationBlendSpeed = 2f;
        private const float MutedSendDb = -80f;

        internal static bool Enabled = true;

        // cut for IndoorGeneric rooms on live, 0 dB in SPT's CommonEffectsSendsMixerData
        private static readonly string[] IndoorMutedSends =
        {
            "AmbientOutDayEffectsSend",
            "AmbientOutNightEffectsSend",
            "CommonAmbOutEffectsSend",
        };

        public static void Apply(MonoBehaviour ambientSystem, MixerEffectsDataSO effects)
        {
            LiveMixerRouting.RerouteHierarchy(ambientSystem.gameObject);
            foreach (AmbientSoundBlender blender in ambientSystem.GetComponentsInChildren<AmbientSoundBlender>(true))
            {
                if (blender.name == MainBlenderName)
                {
                    ApplyBlender(blender);
                }
            }
            foreach (PrecipitationAmbientBlender blender in ambientSystem.GetComponentsInChildren<PrecipitationAmbientBlender>(true))
            {
                if (blender._environment == EnvironmentType.Indoor)
                {
                    blender._dynamicBlendSpeed = IndoorPrecipitationBlendSpeed;
                }
            }
            if (effects != null && effects.effectsSendsMixerData != null)
            {
                ApplySends(effects.effectsSendsMixerData);
            }
        }

        private static void ApplyBlender(AmbientSoundBlender blender)
        {
            blender._fadeOutDuration = FadeOutDuration;
            blender._fromOutToInTransitionTime = FromOutToInTransitionTime;
            blender._volumeCurve = new AnimationCurve(
                new Keyframe(1.36904e-07f, 0f, 0.0413363f, 0.0413363f),
                new Keyframe(0.885171f, 0.584296f, 2.06435f, 2.06435f),
                new Keyframe(1f, 1f, 4.74678f, 4.74678f));
            blender._occlusionCurve = new AnimationCurve(
                new Keyframe(0f, 0f, 0.0193454f, 0.0193454f),
                new Keyframe(0.484687f, 0.0614947f, 0.338727f, 0.338727f),
                new Keyframe(0.8427f, 0.426359f, 2.17692f, 2.17692f),
                new Keyframe(1f, 1f, 4.50415f, 4.50415f));
        }

        private static void ApplySends(GenericVolumeParamsMixerDataSO sends)
        {
            foreach (MixerEnvironmentParameter parameter in sends.mixerEnvironmentParameters)
            {
                if (System.Array.IndexOf(IndoorMutedSends, parameter.paramName) >= 0)
                {
                    parameter.parametersByRoom[EAudioRoomTypeMask.IndoorGeneric] = MutedSendDb;
                }
            }
        }
    }

    /// <summary>The indoor rain clips of SPT are much louder than those of live, with the same
    /// names: RMS gaps measured on amb_rain_indoor_weak, _medium and _strong. Bringing the live
    /// clips in would need a new bundle: the volume gets the same level.</summary>
    internal static class LiveIndoorRain
    {
        private const float WeakGain = 0.38f;     // -8.4 dB
        private const float MediumGain = 0.093f;  // -20.6 dB
        private const float StrongGain = 0.129f;  // -17.8 dB

        private static readonly AccessTools.FieldRef<PrecipitationAmbientBlender, float> GlobalsVolumeMult =
            AccessTools.FieldRefAccess<PrecipitationAmbientBlender, float>("_globalsVolumeMult");

        public static void Apply(PrecipitationAmbientBlender blender)
        {
            if (!LiveMixer.Active || !LiveAmbientPort.Enabled || blender._environment != EnvironmentType.Indoor)
            {
                return;
            }
            switch (blender.CurrentPrecipitationIntensity)
            {
                case RainController.ERainIntensity.Low:
                    GlobalsVolumeMult(blender) *= WeakGain;
                    break;
                case RainController.ERainIntensity.Med:
                    GlobalsVolumeMult(blender) *= MediumGain;
                    break;
                case RainController.ERainIntensity.High:
                    GlobalsVolumeMult(blender) *= StrongGain;
                    break;
            }
        }
    }

    /// <summary>Mixer values at each room change, with DebugLogs: what is really applied in a raid.</summary>
    internal static class LiveMixerStateLog
    {
        private static readonly string[] Parameters =
        {
            "MasterVolume", "InGame", "WorldVolume", "MainVolume", "AmbientVolume", "AmbientInVolume", "AmbientOutVolume",
            "AmbientOutLowpass", "PrecipitationInVolume", "PrecipitationOutVolume", "EnvironmentVolume",
            "HeadphonesMixerVolume", "AmbientCompressorSendLevel", "CompressorGain", "GunsVolume",
        };

        private static PrecipitationAmbientBlender[] _rain = System.Array.Empty<PrecipitationAmbientBlender>();

        public static void Track(MonoBehaviour ambientSystem)
        {
            _rain = ambientSystem.GetComponentsInChildren<PrecipitationAmbientBlender>(true);
        }

        public static void Write(EAudioRoomTypeMask room)
        {
            if (!PluginConfig.LogShotDetails || !LiveMixer.Active || !MonoBehaviourSingleton<BetterAudio>.Instantiated)
            {
                return;
            }
            var text = new System.Text.StringBuilder($"[mixer-state] room={room}");
            UnityEngine.Audio.AudioMixer mixer = MonoBehaviourSingleton<BetterAudio>.Instance.Master;
            foreach (string parameter in Parameters)
            {
                if (mixer.GetFloat(parameter, out float value))
                {
                    text.Append($" {parameter}={value:F1}");
                }
            }
            foreach (PrecipitationAmbientBlender blender in _rain)
            {
                if (blender != null && blender._precipitationSource != null)
                {
                    AudioSource source = blender._precipitationSource;
                    string clip = source.clip != null ? source.clip.name : "none";
                    string group = source.outputAudioMixerGroup != null ? source.outputAudioMixerGroup.name : "none";
                    text.Append($" | {blender._environment}: {clip} vol={source.volume:F3} mix={blender._precipitationMixSource.volume:F3} group={group}");
                }
            }
            Plugin.Log.LogInfo(text.ToString());
        }
    }

    /// <summary>Live headset templates have fields SPT's items lack: the mixer has rain parameters
    /// nothing else sets. Values of the 13 live templates carried by WTT-ContentBackport. The
    /// ambient send stays SPT's: live is 5 dB higher, which would make the ambience louder still
    /// with the World bus unknown. The compressors stay SPT's too: with a headset every sound goes
    /// through them, and the live 3 dB of makeup gain (SPT: 10 dB) made the whole game too quiet,
    /// the live level that goes with it being on the backend.</summary>
    internal static class LiveHeadsetValues
    {
        private const float MutedDb = -80f;
        private const float HeadsetPrecipitationDb = -5f;

        public static void Apply(ActiveHeadphones headphones, HeadphonesTemplate template)
        {
            if (!LiveMixer.Active || headphones._components.Length == 0
                || !(headphones._components[0] is BaseActiveHeadphonesMixerDependentComponent mixer))
            {
                return;
            }
            bool isDefault = template.IsDefault();
            float precipitation = template.AmbientVolume <= MutedDb ? MutedDb : isDefault ? 0f : HeadsetPrecipitationDb;
            mixer.SetMixerFloat("PrecipitationInVolume", precipitation);
            mixer.SetMixerFloat("PrecipitationOutVolume", precipitation);
            mixer.SetMixerFloat(LiveMixer.AmbientVolumeParameter, LiveMixer.TrimmedAmbientVolume(template));
            mixer.SetMixerFloat(LiveMixer.AmbientSendParameter, LiveMixer.TrimmedAmbientSend(template));
            Plugin.Log.LogInfo($"[headset] {template.ShortName}: default={isDefault} rain={precipitation} dB");
        }
    }

    [HarmonyPatch]
    public static class LiveAmbientPatches
    {
        // before the blenders and sound player groups of the system start
        [HarmonyPatch(typeof(AmbientAudioSystem), "Init")]
        [HarmonyPrefix]
        public static void AmbientInitPrefix(AmbientAudioSystem __instance)
        {
            if (!LiveMixer.Active)
            {
                return;
            }
            try
            {
                // the World bus can still be at the +4 dB of the asset when a raid starts
                LiveMixer.ApplyWorldVolume();
                if (LiveAmbientPort.Enabled)
                {
                    LiveAmbientPort.Apply(__instance, __instance.EffectsData);
                }
                else
                {
                    LiveMixerRouting.RerouteHierarchy(__instance.gameObject);
                }
                LiveMixerStateLog.Track(__instance);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogError($"live ambient values failed: {e}");
            }
        }

        [HarmonyPatch(typeof(PrecipitationAmbientBlender), "UpdateGlobalsVolumeMult")]
        [HarmonyPostfix]
        public static void RainMultPostfix(PrecipitationAmbientBlender __instance)
        {
            LiveIndoorRain.Apply(__instance);
        }

        [HarmonyPatch(typeof(AudioMixerParamsController), "ApplyMixerParams")]
        [HarmonyPostfix]
        public static void RoomParamsPostfix(EAudioRoomTypeMask roomTypeMask)
        {
            LiveMixerStateLog.Write(roomTypeMask);
        }

        // after all of SPT's headset components, which set the ambient values too
        [HarmonyPatch(typeof(ActiveHeadphones), "Configure")]
        [HarmonyPostfix]
        public static void HeadsetConfigurePostfix(ActiveHeadphones __instance, HeadphonesTemplate template)
        {
            try
            {
                LiveHeadsetValues.Apply(__instance, template);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogError($"live headset values failed: {e}");
            }
        }

        // SPT's sound settings and hideout fades only reach the mixer of resources.assets
        [HarmonyPatch(typeof(SoundSettingsController), "AdjustSoundValue")]
        [HarmonyPostfix]
        public static void AdjustSoundValuePostfix(string mixerChannel)
        {
            LiveMixer.MirrorSetting(mixerChannel);
        }

        [HarmonyPatch(typeof(GUISounds), "method_11")]
        [HarmonyPostfix]
        public static void HideoutSoundPostfix(bool __0)
        {
            LiveMixer.MirrorHideoutSound(__0);
        }

        [HarmonyPatch(typeof(GUISounds), "SetHideoutLowPassFilter")]
        [HarmonyPostfix]
        public static void HideoutLowPassPostfix(bool __0, float __1)
        {
            LiveMixer.MirrorHideoutLowPass(__0, __1);
        }
    }
}
