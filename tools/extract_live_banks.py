#!/usr/bin/env python3
"""Rebuilds what the mod needs from a live install so that every weapon plays its live banks.

Live keeps many gunshot banks inside the weapon containers instead of audio/banks. Those
containers cannot be used as they are: they share their CAB with the SPT ones (Unity refuses
to load both) and they carry the model and animations of the weapon. This tool writes
sound-only bundles under audio/banks/live_embedded, the live blendoptions bundle, bundles.json
and the C# table that tells the client which bank each weapon must use.

Usage: extract_live_banks.py --live <Windows dir> --spt-mods <user/mods> --spt <Windows dir>
                             --mod <spt-mod dir> --table <LiveBankTable.cs>
Requires UnityPy.
"""
import argparse
import hashlib
import io
import json
import os
import re
import shutil
import sys

import UnityPy
from UnityPy.streams import EndianBinaryReader

UnityPy.config.FALLBACK_UNITY_VERSION = "2022.3.43f1"

ROLES = ("Body", "BodySilenced", "Tail", "TailSilenced", "Doublet", "DoubletSilenced")
BLEND_KEY = "assets/content/audio/blendoptions/assets.bundle"
EMBEDDED_DIR = "assets/content/audio/banks/live_embedded/"
OVERRIDING_MODS = ("WTT-ContentBackport",)


def serialized_file(env):
    name = next(n for n in env.file.files if not n.endswith((".resource", ".resS")))
    return name, env.file.files[name]


def clips_of(bank):
    return [c for env in bank["Environments"] for layer in env["Clips"] for c in layer["Clips"]]


def is_loop(names):
    return any("_loop" in n.lower() and "tail" not in n.lower() for n in names)


class Container:
    """Sound setup of a weapon container: which bank each role uses, inside or outside it."""

    def __init__(self, path):
        self.env = UnityPy.load(path)
        self.cab_name, self.cab = serialized_file(self.env)
        self.banks = {}
        self.player = None
        players = 0
        for obj in self.cab.objects.values():
            if obj.type.name != "MonoBehaviour":
                continue
            try:
                tree = obj.read_typetree()
            except Exception:
                continue
            if "Environments" in tree:
                self.banks[obj.path_id] = (obj, tree)
            elif "Body" in tree and "BodySilenced" in tree:
                players += 1
                if self.player is None or tree["Body"]["m_PathID"]:
                    self.player = tree
        self.several_players = players > 1

    def external_cab(self, file_id):
        return self.cab.externals[file_id - 1].path.split("/")[-1]

    def role(self, name):
        """(None, bank name) for a bank in this container, (cab, path id) for another bundle."""
        ref = (self.player or {}).get(name) or {}
        if not ref.get("m_PathID"):
            return None
        if ref["m_FileID"] == 0:
            entry = self.banks.get(ref["m_PathID"])
            return (None, entry[1]["m_Name"]) if entry else None
        return (self.external_cab(ref["m_FileID"]), ref["m_PathID"])


def index_banks(root):
    """(cab, path id) -> (bundle key, bank name) for every bundle of audio/banks."""
    index = {}
    base = os.path.join(root, "assets/content/audio/banks")
    for folder, _, files in os.walk(base):
        for file in files:
            if not file.endswith(".bundle"):
                continue
            path = os.path.join(folder, file)
            env = UnityPy.load(path)
            cab_name, cab = serialized_file(env)
            key = os.path.relpath(path, root)
            for obj in cab.objects.values():
                if obj.type.name != "MonoBehaviour":
                    continue
                try:
                    tree = obj.read_typetree()
                except Exception:
                    continue
                if "Environments" in tree:
                    index[(cab_name, obj.path_id)] = (key, tree["m_Name"])
    return index


def check_round_trip(obj):
    """The typetree rewrite must reproduce the bytes of an object before it gets edited."""
    raw = bytes(obj.get_raw_data())
    obj.save_typetree(obj.read_typetree())
    if bytes(obj.get_raw_data()) != raw:
        raise RuntimeError(f"typetree round trip differs for {obj.type.name} {obj.path_id}")


def check_references(tree, allowed_files):
    stack = [tree]
    while stack:
        value = stack.pop()
        if isinstance(value, dict):
            if "m_FileID" in value and value["m_FileID"] not in allowed_files:
                raise RuntimeError(f"unexpected external reference {value}")
            stack.extend(value.values())
        elif isinstance(value, list):
            stack.extend(value)


def extract(container, kept_ids, bundle_key):
    """Writes the sound-only bundle made of the given banks.

    Returns its bytes and the CABs of the other bundles it needs loaded."""
    bundle = container.env.file
    cab = container.cab
    old_name = container.cab_name
    new_name = "CAB-" + hashlib.md5(("wsb/" + bundle_key).encode()).hexdigest()

    banks = [container.banks[i] for i in kept_ids]
    # what stays external: the blend options, and the SoundBank script of some containers
    external_ids = sorted({t["BlendOptions"]["m_FileID"] for _, t in banks if t["BlendOptions"]["m_PathID"]}
                          | {t["m_Script"]["m_FileID"] for _, t in banks})
    external_ids = [i for i in external_ids if i != 0]
    new_file_id = {old: position + 1 for position, old in enumerate(external_ids)}
    new_file_id[0] = 0
    external_cabs = [container.external_cab(i) for i in external_ids]

    keep = set(kept_ids)
    clip_ids = []
    for _, tree in banks:
        check_references(tree, new_file_id)
        if tree["m_Script"]["m_FileID"] == 0:
            keep.add(tree["m_Script"]["m_PathID"])
        for clip in clips_of(tree):
            if clip["m_PathID"] not in clip_ids:
                clip_ids.append(clip["m_PathID"])
    keep.update(clip_ids)
    asset_bundle = next(o for o in cab.objects.values() if o.type.name == "AssetBundle")
    keep.add(asset_bundle.path_id)
    for path_id in keep:
        check_round_trip(cab.objects[path_id])

    # only the kept clips stay in the resource file
    sources = {}
    data = io.BytesIO()
    for clip_id in clip_ids:
        obj = cab.objects[clip_id]
        tree = obj.read_typetree()
        resource = tree["m_Resource"]
        source = resource["m_Source"].split("/")[-1]
        if source != old_name + ".resource":
            raise RuntimeError(f"clip stored in {source}")
        if source not in sources:
            node = bundle.files[source]
            sources[source] = bytes(node.bytes if hasattr(node, "bytes") else node.getvalue())
        chunk = sources[source][resource["m_Offset"]:resource["m_Offset"] + resource["m_Size"]]
        if len(chunk) != resource["m_Size"]:
            raise RuntimeError("clip outside its resource file")
        data.write(b"\0" * (-data.tell() % 16))
        resource["m_Offset"] = data.tell()
        resource["m_Source"] = f"archive:/{new_name}/{new_name}.resource"
        data.write(chunk)
        obj.save_typetree(tree)

    for path_id in list(cab.objects):
        if path_id not in keep:
            del cab.objects[path_id]

    cab.externals[:] = [cab.externals[i - 1] for i in external_ids]
    preload, entries = [], []
    for obj, tree in banks:
        start = len(preload)
        pointer = {"m_FileID": 0, "m_PathID": obj.path_id}
        preload.append(pointer)
        tree["m_Script"]["m_FileID"] = new_file_id[tree["m_Script"]["m_FileID"]]
        preload.append(tree["m_Script"])
        if tree["BlendOptions"]["m_PathID"]:
            tree["BlendOptions"]["m_FileID"] = new_file_id[tree["BlendOptions"]["m_FileID"]]
            preload.append(tree["BlendOptions"])
        seen = set()
        for clip in clips_of(tree):
            if clip["m_PathID"] not in seen:
                seen.add(clip["m_PathID"])
                preload.append({"m_FileID": 0, "m_PathID": clip["m_PathID"]})
        obj.save_typetree(tree)
        entries.append((f"{EMBEDDED_DIR}{tree['m_Name']}.asset",
                        {"preloadIndex": start, "preloadSize": len(preload) - start, "asset": pointer}))

    tree = asset_bundle.read_typetree()
    tree["m_Name"] = tree["m_AssetBundleName"] = bundle_key
    tree["m_Container"] = entries
    tree["m_PreloadTable"] = preload
    tree["m_MainAsset"] = {"preloadIndex": 0, "preloadSize": 0, "asset": {"m_FileID": 0, "m_PathID": 0}}
    tree["m_Dependencies"] = [c.lower() for c in external_cabs]
    asset_bundle.save_typetree(tree)

    for name in list(bundle.files):
        del bundle.files[name]
    cab.name = new_name
    bundle.files[new_name] = cab
    resource_node = EndianBinaryReader(data.getvalue())
    resource_node.flags = 0
    bundle.files[new_name + ".resource"] = resource_node
    return bundle.save(packer="lz4"), external_cabs


def verify(data, bundle_key, expected_banks):
    env = UnityPy.load(data)
    cab_name, cab = serialized_file(env)
    clips = {o.path_id: o for o in cab.objects.values() if o.type.name == "AudioClip"}
    found = {}
    for obj in cab.objects.values():
        if obj.type.name == "MonoBehaviour":
            tree = obj.read_typetree()
            found[tree["m_Name"]] = tree
            for clip in clips_of(tree):
                if clip["m_PathID"] not in clips:
                    raise RuntimeError(f"{bundle_key}: dangling clip in {tree['m_Name']}")
    if sorted(found) != sorted(expected_banks):
        raise RuntimeError(f"{bundle_key}: banks {sorted(found)} instead of {sorted(expected_banks)}")
    for clip in clips.values():
        if not clip.read().samples:
            raise RuntimeError(f"{bundle_key}: clip {clip.path_id} does not decode")
    return len(clips)


def cs(value):
    return f'"{value}"' if value else "null"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--live", required=True)
    parser.add_argument("--spt", required=True)
    parser.add_argument("--spt-mods", required=True)
    parser.add_argument("--mod", required=True)
    parser.add_argument("--table", required=True)
    args = parser.parse_args()

    manifest = json.load(open(os.path.join(args.live, "Windows.json")))
    spt_manifest = json.load(open(os.path.join(args.spt, "Windows.json")))
    overrides = {}
    for mod in OVERRIDING_MODS:
        for entry in json.load(open(os.path.join(args.spt_mods, mod, "bundles.json")))["manifest"]:
            overrides[entry["key"]] = os.path.join(args.spt_mods, mod, "bundles", entry["key"])

    bank_index = index_banks(args.live)
    print(f"{len(bank_index)} live banks in audio/banks")

    table, written, report = {}, {}, []
    bundle_dependencies = {}
    known_cabs = {cab: key for (cab, _), (key, _) in bank_index.items()}
    known_cabs[serialized_file(UnityPy.load(os.path.join(args.live, BLEND_KEY)))[0]] = BLEND_KEY
    keys = sorted(k for k in set(spt_manifest) | set(overrides)
                  if re.search(r"/(weapon|launcher)_[^/]*_container\.bundle$", k) and k in manifest)
    for key in keys:
        file_name = key.split("/")[-1]
        live = Container(os.path.join(args.live, key))
        if live.player is None:
            continue
        spt_path = overrides.get(key, os.path.join(args.spt, key))
        spt = Container(spt_path) if os.path.exists(spt_path) else None
        if spt is None or spt.player is None:
            report.append((file_name, "no sound player on the SPT side"))
            continue
        if live.several_players:
            report.append((file_name, "several sound players, kept the first with a body"))

        names, kept = {}, []
        same = True
        for role in ROLES:
            live_role, spt_role = live.role(role), spt.role(role)
            if live_role is None:
                continue
            if live_role[0] is not None:
                location = bank_index.get(live_role)
                if location is None:
                    report.append((file_name, f"{role}: live bank outside audio/banks {live_role}"))
                    same = False
                    continue
                names[role] = (location[0], location[1])
                if spt_role != live_role:
                    same = False
            else:
                names[role] = (None, live_role[1])
                same = False
        if same or "Body" not in names:
            continue

        # embedded banks: body and doublets; a tail only serves a live bank still in loop format
        embedded = {}
        for path_id, (_, tree) in live.banks.items():
            embedded[tree["m_Name"]] = (path_id, tree)
        loop_silenced = loop_body = False
        for role in ("Body", "BodySilenced"):
            if role in names and names[role][0] is None:
                _, tree = embedded[names[role][1]]
                clip_names = [o.read().m_Name for o in
                              (live.cab.objects[c["m_PathID"]] for c in clips_of(tree))]
                if is_loop(clip_names):
                    if role == "Body":
                        loop_body = True
                    else:
                        loop_silenced = True
        wanted = []
        for role, (location, name) in list(names.items()):
            if location is not None:
                continue
            if role == "Tail" and not loop_body or role == "TailSilenced" and not loop_silenced:
                del names[role]
                continue
            if name not in wanted:
                wanted.append(name)

        bundle_key = None
        if wanted:
            signature = hashlib.md5(json.dumps(sorted(
                (n, sorted((live.cab.objects[c["m_PathID"]].read().m_Name,
                            live.cab.objects[c["m_PathID"]].read().m_Resource.m_Size)
                           for c in clips_of(embedded[n][1])))
                for n in wanted)).encode()).hexdigest()
            if signature in written:
                bundle_key = written[signature]
            else:
                stem = re.sub(r"[^a-z0-9_-]", "_", names["Body"][1].replace("_main", "").lower())
                bundle_key = f"{EMBEDDED_DIR}{stem}.bundle"
                if bundle_key in written.values():
                    bundle_key = f"{EMBEDDED_DIR}{stem}_{file_name[7:-17]}.bundle"
                try:
                    data, external_cabs = extract(live, [embedded[n][0] for n in wanted], bundle_key)
                    clip_count = verify(data, bundle_key, wanted)
                except Exception as error:
                    report.append((file_name, f"NOT EXTRACTED {wanted}: {error}"))
                    continue
                output = os.path.join(args.mod, "bundles", bundle_key)
                os.makedirs(os.path.dirname(output), exist_ok=True)
                with open(output, "wb") as stream:
                    stream.write(data)
                written[signature] = bundle_key
                bundle_dependencies[bundle_key] = [known_cabs[c] for c in external_cabs]
                print(f"{bundle_key}: {wanted}, {clip_count} clips, {len(data) / 1e6:.1f} MB")

        table[file_name] = {role: (bundle_key if loc is None else loc, name) for role, (loc, name) in names.items()}

    # blend options: same CAB and path ids, only the distances differ
    blend_out = os.path.join(args.mod, "bundles", BLEND_KEY)
    os.makedirs(os.path.dirname(blend_out), exist_ok=True)
    shutil.copyfile(os.path.join(args.live, BLEND_KEY), blend_out)

    # bundles.json
    json_path = os.path.join(args.mod, "bundles.json")
    mod_manifest = json.load(open(json_path))
    known = {e["key"] for e in mod_manifest["manifest"]}
    for key in [BLEND_KEY] + sorted(set(written.values())):
        if key not in known:
            mod_manifest["manifest"].append(
                {"key": key, "dependencyKeys": bundle_dependencies.get(key, [])})
    with open(json_path, "w") as stream:
        json.dump(mod_manifest, stream, indent=2)
        stream.write("\n")

    # client table
    lines = [
        "// Generated by tools/extract_live_banks.py from the live manifest: do not edit.",
        "using System.Collections.Generic;",
        "",
        "namespace SoundEngineBackport.LiveEngine",
        "{",
        "    internal static class LiveBankTable",
        "    {",
        "        internal sealed class Bank",
        "        {",
        "            public readonly string Bundle;",
        "            public readonly string Name;",
        "            public Bank(string bundle, string name) { Bundle = bundle; Name = name; }",
        "        }",
        "",
        "        internal sealed class Weapon",
        "        {",
        "            public Bank Body, BodySilenced, Tail, TailSilenced, Doublet, DoubletSilenced;",
        "        }",
        "",
        "        internal static readonly Dictionary<string, Weapon> ByContainer = new Dictionary<string, Weapon>",
        "        {",
    ]
    for file_name, roles in sorted(table.items()):
        fields = ", ".join(f"{r} = new Bank({cs(b)}, {cs(n)})" for r, (b, n) in roles.items())
        lines.append(f'            {{ "{file_name}", new Weapon {{ {fields} }} }},')
    lines += ["        };", "    }", "}", ""]
    with open(args.table, "w") as stream:
        stream.write("\n".join(lines))

    print(f"{len(keys)} containers checked, {len(table)} weapons in the table, {len(set(written.values()))} bundles")
    for item in report:
        print("NOTE", item)


if __name__ == "__main__":
    sys.exit(main())
