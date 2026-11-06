#!/usr/bin/env python3
"""Writes the live master mixer as a bundle the plugin can load next to SPT's own.

The live bundle has the same CAB as SPT's, which Unity refuses to load twice: the CAB and
the bundle name change here. The only change to the mixer itself is the Meta XR Audio
Reflection effect, bypassed: SPT's mixer already runs one on its InGame group, and the native
plugin has a single shared reverb that two instances read in turn (bursts of noise indoors).
The plugin chains the live mixer into SPT's InGame group, so that instance hears everything.
The 10 Hz resonant highpass of the World group is bypassed too: through it, the low hum of
the indoor room tones came out with short bursts of noise (SPT's World has no highpass).

    python3 tools/extract_live_mixer.py \\
      --live "<live>/EscapeFromTarkov_Data/StreamingAssets/Windows" \\
      --out client/live_mastermixer.bundle

    # bypass only, on a bundle written by an older version of this script
    python3 tools/extract_live_mixer.py --patch client/live_mastermixer.bundle
"""
import argparse
import hashlib
import os

import UnityPy

SOURCE_KEY = "assets/content/audio/mastermixer.bundle"
OUTPUT_KEY = "wsb/live_mastermixer.bundle"
FIRST_PLUGIN_EFFECT_TYPE = 1000
REFLECTION_PLUGIN = b"Meta XR Audio Reflection"


def mixer_summary(env):
    objects = list(env.objects)
    mixers = [o for o in objects if o.type.name == "AudioMixerController"]
    if len(mixers) != 1:
        raise SystemExit(f"expected one mixer, found {len(mixers)}")
    constant = mixers[0].read_typetree()["m_MixerConstant"]
    return {
        "groups": len(constant["groups"]),
        "effects": len(constant["effects"]),
        "values": len(constant["snapshots"][0]["values"]),
        "exposed": len(constant["exposedParameterNames"]),
    }


def bypass_reflection(env):
    """Bypasses the native reverb effect of the mixer. Returns how many effects changed."""
    mixer = next(o for o in env.objects if o.type.name == "AudioMixerController")
    tree = mixer.read_typetree()
    constant = tree["m_MixerConstant"]
    if REFLECTION_PLUGIN not in bytes(constant["pluginEffectNameBuffer"]):
        raise SystemExit("the mixer has no Meta XR Audio Reflection effect")
    plugins = [e for e in constant["effects"] if e["type"] >= FIRST_PLUGIN_EFFECT_TYPE]
    if len(plugins) != 1:
        raise SystemExit(f"expected one plugin effect, found {len(plugins)}")
    if plugins[0]["bypass"]:
        return 0
    plugins[0]["bypass"] = True
    mixer.save_typetree(tree)
    return 1


WORLD_GROUP = "World"
RESONANT_HIGHPASS_TYPE = 5


def bypass_world_highpass(env):
    """Bypasses the 10 Hz resonant highpass of the World group: with it, the low hum of the
    indoor room tones comes out with bursts of noise (heard in game only through this group).
    Returns how many effects changed."""
    names = {}
    mixer = None
    for o in env.objects:
        if o.type.name == "AudioMixerGroupController":
            t = o.read_typetree()
            names[tuple(t["m_GroupID"].values())] = t["m_Name"]
        elif o.type.name == "AudioMixerController":
            mixer = o
    tree = mixer.read_typetree()
    constant = tree["m_MixerConstant"]
    groups = [names.get(tuple(g.values())) for g in constant["groupGUIDs"]]
    world = [i for i, n in enumerate(groups) if n == WORLD_GROUP]
    if len(world) != 1:
        raise SystemExit(f"expected one World group, found {len(world)}")
    highpasses = [e for e in constant["effects"]
                  if e["groupConstantIndex"] == world[0] and e["type"] == RESONANT_HIGHPASS_TYPE]
    if len(highpasses) != 1:
        raise SystemExit(f"expected one highpass on World, found {len(highpasses)}")
    if highpasses[0]["bypass"]:
        return 0
    highpasses[0]["bypass"] = True
    mixer.save_typetree(tree)
    return 1


def reflection_bypassed(env):
    mixer = next(o for o in env.objects if o.type.name == "AudioMixerController")
    effects = mixer.read_typetree()["m_MixerConstant"]["effects"]
    return all(e["bypass"] for e in effects if e["type"] >= FIRST_PLUGIN_EFFECT_TYPE)


def write_checked(data, expected, cab_names, out):
    check = UnityPy.load(data)
    if mixer_summary(check) != expected or list(check.file.files) != cab_names or not reflection_bypassed(check):
        raise SystemExit("the written bundle does not match the live mixer")
    with open(out, "wb") as stream:
        stream.write(data)


def patch(path):
    env = UnityPy.load(path)
    expected = mixer_summary(env)
    changed = bypass_reflection(env) + bypass_world_highpass(env)
    if changed == 0:
        print(f"{path}: already patched")
        return
    data = env.file.save(packer="lz4")
    write_checked(data, expected, list(env.file.files), path)
    print(f"{path}: {changed} effects bypassed, {len(data)} bytes")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--live")
    parser.add_argument("--out")
    parser.add_argument("--patch")
    args = parser.parse_args()
    if args.patch:
        patch(args.patch)
        return
    if not args.live or not args.out:
        parser.error("--live and --out are required without --patch")

    env = UnityPy.load(os.path.join(args.live, SOURCE_KEY))
    expected = mixer_summary(env)
    bundle = env.file
    names = list(bundle.files)
    if len(names) != 1:
        raise SystemExit(f"expected a single file in the bundle, found {names}")
    cab = bundle.files[names[0]]
    new_name = "CAB-" + hashlib.md5(OUTPUT_KEY.encode()).hexdigest()

    asset_bundle = next(o for o in cab.objects.values() if o.type.name == "AssetBundle")
    tree = asset_bundle.read_typetree()
    tree["m_Name"] = tree["m_AssetBundleName"] = OUTPUT_KEY
    asset_bundle.save_typetree(tree)

    bypass_reflection(env)
    bypass_world_highpass(env)
    del bundle.files[names[0]]
    cab.name = new_name
    bundle.files[new_name] = cab
    data = bundle.save(packer="lz4")

    write_checked(data, expected, [new_name], args.out)
    print(f"{args.out}: {len(data)} bytes, {new_name}, {expected}")


if __name__ == "__main__":
    main()
