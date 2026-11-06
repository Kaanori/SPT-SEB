// Gives each weapon the banks its live container uses when SPT or WTT-ContentBackport play
// something else: WTT containers embed their own banks, and live keeps many banks inside
// the containers (extracted to audio/banks/live_embedded by tools/extract_live_banks.py).
// LiveBankTable only lists weapons of the live manifest: custom weapons such as
// WTT-Armory's are not in it and keep their banks.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Comfort.Common;
using Diz.DependencyManager;
using Diz.Resources;
using EFT;
using EFT.AssetsManager;
using EFT.InventoryLogic;
using HarmonyLib;
using JsonType;

namespace SoundEngineBackport.LiveEngine
{
    public static class LiveBankReplacement
    {
        private sealed class LiveBundle
        {
            // kept for the whole game session: the banks are assigned to weapons living in
            // pools, and an unloaded bundle would leave them without any sound
            public DependencyGraph<IEasyBundle>.Token Token;
            public Dictionary<string, SoundBank> Banks;
            public bool Failed;
        }

        private sealed class Handled { }

        private static readonly Dictionary<string, LiveBundle> Bundles = new Dictionary<string, LiveBundle>();
        private static readonly ConditionalWeakTable<WeaponSoundPlayer, Handled> HandledPlayers = new ConditionalWeakTable<WeaponSoundPlayer, Handled>();
        private static readonly AccessTools.FieldRef<BaseSoundPlayer, BaseSoundPlayer.IObserverToPlayerBridge> BridgeRef =
            AccessTools.FieldRefAccess<BaseSoundPlayer, BaseSoundPlayer.IObserverToPlayerBridge>("playersBridge");
        private static readonly AccessTools.FieldRef<WeaponSoundPlayer, bool> IsFiringRef =
            AccessTools.FieldRefAccess<WeaponSoundPlayer, bool>("_isFiring");

        private static readonly AccessTools.FieldRef<AssetPoolObject, ResourceTypeInfo> PrefabResourceRef =
            AccessTools.FieldRefAccess<AssetPoolObject, ResourceTypeInfo>("ResourceType");

        internal static bool Enabled = true;

        /// <summary>Called when a weapon is set up (starts loading its live banks early) and
        /// before each shot (puts them in place as soon as they are ready; until then the
        /// weapon keeps its current banks rather than staying silent).</summary>
        public static void Apply(WeaponSoundPlayer player)
        {
            if (!Enabled || HandledPlayers.TryGetValue(player, out _))
            {
                return;
            }
            Weapon weapon = BridgeRef(player)?.Weapon as Weapon;
            if (weapon == null)
            {
                return;
            }
            // an underbarrel launcher has its own sound player, but its bridge gives the rifle
            // in hand: the rifle's banks must not end up on the launcher
            WeaponPrefab prefab = player.GetComponent<WeaponPrefab>();
            string path = prefab != null && prefab.IsUnderbarrelWeaponPrefab && PrefabResourceRef(prefab).ItemTemplate is LauncherTemplate launcher
                ? launcher.Prefab.path
                : weapon.Prefab.path;
            string container = Path.GetFileName(path ?? string.Empty);
            if (!LiveBankTable.ByContainer.TryGetValue(container, out LiveBankTable.Weapon banks))
            {
                HandledPlayers.Add(player, new Handled());
                return;
            }

            bool ready = true;
            foreach (LiveBankTable.Bank bank in new[] { banks.Body, banks.BodySilenced, banks.Tail, banks.TailSilenced, banks.Doublet, banks.DoubletSilenced })
            {
                if (bank != null && GetOrLoad(bank.Bundle).Banks == null && !GetOrLoad(bank.Bundle).Failed)
                {
                    ready = false;
                }
            }
            // not in the middle of a loop salve of the previous bank
            if (!ready || IsFiringRef(player))
            {
                return;
            }

            player.Body = Pick(banks.Body, player.Body);
            player.BodySilenced = Pick(banks.BodySilenced, player.BodySilenced);
            player.Tail = Pick(banks.Tail, player.Tail);
            player.TailSilenced = Pick(banks.TailSilenced, player.TailSilenced);
            player.Doublet = Pick(banks.Doublet, player.Doublet);
            player.DoubletSilenced = Pick(banks.DoubletSilenced, player.DoubletSilenced);
            HandledPlayers.Add(player, new Handled());
            if (PluginConfig.LogShotDetails)
            {
                Plugin.Log.LogWarning($"[bank] {container}: body={player.Body?.name} silenced={player.BodySilenced?.name}");
            }
        }

        private static SoundBank Pick(LiveBankTable.Bank wanted, SoundBank current)
        {
            if (wanted == null)
            {
                return current;
            }
            Dictionary<string, SoundBank> banks = GetOrLoad(wanted.Bundle).Banks;
            return banks != null && banks.TryGetValue(wanted.Name, out SoundBank live) ? live : current;
        }

        private static LiveBundle GetOrLoad(string key)
        {
            if (!Bundles.TryGetValue(key, out LiveBundle bundle))
            {
                bundle = new LiveBundle();
                Bundles.Add(key, bundle);
                Load(bundle, key);
            }
            return bundle;
        }

        private static async void Load(LiveBundle bundle, string key)
        {
            try
            {
                IEasyAssets assets = Singleton<IEasyAssets>.Instance;
                bundle.Token = assets.Retain(new[] { key });
                await EasyAssetsExtensions.LoadBundles(bundle.Token);
                var banks = new Dictionary<string, SoundBank>();
                foreach (SoundBank bank in assets.System.GetNode(key).Data.Assets.OfType<SoundBank>())
                {
                    banks[bank.name] = bank;
                }
                if (banks.Count == 0)
                {
                    throw new InvalidOperationException("the bundle holds no sound bank");
                }
                bundle.Banks = banks;
            }
            catch (Exception e)
            {
                bundle.Failed = true;
                Plugin.Log.LogError($"could not load the live banks of {key}, its weapons keep their current banks: {e}");
            }
        }
    }
}
