#!/usr/bin/env python3
"""Checks that every live weapon sound is shipped by the mod and selected by the client.

Read-only: it writes nothing in the mod. Four checks:

  1. manifest   bundles.json, files on disk and table bundles agree with each other.
  2. bundles    every live bundle under audio/ is in the mod and byte-identical
                (bundles in audio/banks/live_embedded are mod-made and skipped).
  3. weapons    for each live weapon/launcher container, the bank the game will play
                (the table entry if any, else SPT or WTT-ContentBackport's own bank) holds
                exactly the live clips (names and sizes), per role. Banks left in loop
                format are flagged.
  4. log        optional: BepInEx log lines "[bank] <container>: body=... silenced=..."
                (needs LogShotDetails) and "could not load the live banks" errors, compared
                with the table.

Usage: audit_live_coverage.py --live <Windows dir> --spt <Windows dir> --spt-mods <user/mods>
                              --mod spt-mod --table client/src/LiveEngine/LiveBankTable.cs
                              [--log BepInEx/LogOutput.log] [--skip-weapons]
Exit code 1 when a problem was found. Requires UnityPy (checks 1, 2 and 4 work without it
only if --skip-weapons is given).
"""
import argparse
import hashlib
import json
import os
import re
import sys

EMBEDDED_DIR = "assets/content/audio/banks/live_embedded/"
ROLES = ("Body", "BodySilenced", "Tail", "TailSilenced", "Doublet", "DoubletSilenced")
# a tail only matters when the live bank itself is still a loop
ALWAYS_COMPARED = ("Body", "BodySilenced", "Doublet", "DoubletSilenced")
OVERRIDING_MODS = ("WTT-ContentBackport",)
CONTAINER = re.compile(r"/(weapon|launcher)_[^/]*_container\.bundle$")

problems = []


def problem(section, message):
    problems.append((section, message))
    print(f"  PROBLEM {message}")


def sha(path):
    digest = hashlib.sha256()
    with open(path, "rb") as stream:
        for chunk in iter(lambda: stream.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def parse_table(path):
    """container -> role -> (bundle key, bank name), read from the generated C# file."""
    table = {}
    entry = re.compile(r'\{ "([^"]+)", new Weapon \{ (.*) \} \},')
    bank = re.compile(r'(\w+) = new Bank\((?:"([^"]*)"|null), (?:"([^"]*)"|null)\)')
    for line in open(path, encoding="utf-8"):
        match = entry.search(line)
        if match:
            table[match.group(1)] = {r: (b, n) for r, b, n in bank.findall(match.group(2))}
    return table


# ------------------------------------------------------------------ 1. manifest

def check_manifest(mod, table):
    print("[1] manifest: bundles.json, files on disk, table")
    manifest = json.load(open(os.path.join(mod, "bundles.json")))["manifest"]
    keys = {e["key"] for e in manifest}
    root = os.path.join(mod, "bundles")
    on_disk = set()
    for folder, _, files in os.walk(root):
        for file in files:
            on_disk.add(os.path.relpath(os.path.join(folder, file), root).replace(os.sep, "/"))
    for key in sorted(keys - on_disk):
        problem("manifest", f"in bundles.json but not on disk: {key}")
    for key in sorted(on_disk - keys):
        problem("manifest", f"on disk but not in bundles.json (never loaded): {key}")
    for entry in manifest:
        for dep in entry["dependencyKeys"]:
            if dep not in keys:
                problem("manifest", f"{entry['key']} depends on {dep}, which is not in bundles.json")
    used = set()
    for container, roles in table.items():
        for role, (bundle, _) in roles.items():
            used.add(bundle)
            if bundle not in keys:
                problem("manifest", f"table {container} {role}: bundle {bundle} not in bundles.json")
    for key in sorted(k for k in keys if k.startswith(EMBEDDED_DIR) and k not in used):
        problem("manifest", f"embedded bundle used by no table entry: {key}")
    print(f"  {len(keys)} bundles in bundles.json, {len(table)} table entries")
    return keys


# ------------------------------------------------------------------ 2. bundles

def check_bundles(live, mod, mod_keys):
    print("[2] bundles: live audio bundles against the mod")
    manifest = json.load(open(os.path.join(live, "Windows.json")))
    live_audio = sorted(k for k in manifest if k.startswith("assets/content/audio/"))
    identical = missing = 0
    skipped = []
    for key in live_audio:
        path = os.path.join(mod, "bundles", key)
        if not os.path.exists(path):
            missing += 1
            skipped.append(key)
            continue
        if sha(path) != sha(os.path.join(live, key)):
            problem("bundles", f"differs from live: {key}")
        else:
            identical += 1
    for key in sorted(k for k in mod_keys if k.startswith("assets/content/audio/")
                      and not k.startswith(EMBEDDED_DIR) and k not in manifest):
        print(f"  note: not a live bundle (mod or WTT only): {key}")
    print(f"  {len(live_audio)} live audio bundles: {identical} identical, {missing} not shipped")
    # shipped on purpose: weapon sounds only. A missing bank or weapon bundle is worth a look.
    interesting = [k for k in skipped if "/banks/" in k or "/weapons/" in k or "audioshot" in k]
    for key in interesting:
        print(f"  not shipped (check it is not a weapon sound): {key}")
    return interesting


# ------------------------------------------------------------------ 3. weapons

def load_unitypy():
    import UnityPy
    UnityPy.config.FALLBACK_UNITY_VERSION = "2022.3.43f1"
    return UnityPy


def serialized_file(env):
    name = next(n for n in env.file.files if not n.endswith((".resource", ".resS")))
    return name, env.file.files[name]


def clips_of(tree):
    return [c for env in tree["Environments"] for layer in env["Clips"] for c in layer["Clips"]]


class Bundle:
    """A bundle loaded once: its CAB and its sound banks, with their clip signature."""

    def __init__(self, UnityPy, path):
        self.env = UnityPy.load(path)
        self.cab_name, self.cab = serialized_file(self.env)
        self.player = None
        self.banks = {}  # path id -> (name, tree)
        for obj in self.cab.objects.values():
            if obj.type.name != "MonoBehaviour":
                continue
            try:
                tree = obj.read_typetree()
            except Exception:
                continue
            if "Environments" in tree:
                self.banks[obj.path_id] = (tree["m_Name"], tree)
            elif "Body" in tree and "BodySilenced" in tree:
                if self.player is None or tree["Body"]["m_PathID"]:
                    self.player = tree

    def external_cab(self, file_id):
        return self.cab.externals[file_id - 1].path.split("/")[-1]

    def signature(self, path_id):
        """Clip names and sizes of a bank, order-insensitive, and whether it is a loop."""
        name, tree = self.banks[path_id]
        clips = []
        for clip in clips_of(tree):
            obj = self.cab.objects.get(clip["m_PathID"])
            if obj is None:
                clips.append(("<dangling>", 0))
                continue
            data = obj.read()
            clips.append((data.m_Name, data.m_Resource.m_Size))
        names = [n for n, _ in clips]
        loop = any("_loop" in n.lower() and "tail" not in n.lower() for n in names)
        return tuple(sorted(clips)), loop

    def role(self, role):
        """('local', path id) | (cab, path id) | None."""
        ref = (self.player or {}).get(role) or {}
        if not ref.get("m_PathID"):
            return None
        if ref["m_FileID"] == 0:
            return ("local", ref["m_PathID"])
        return (self.external_cab(ref["m_FileID"]), ref["m_PathID"])


class Banks:
    """Resolves (cab, path id) of audio/banks bundles of one install to a clip signature."""

    def __init__(self, UnityPy, *roots):
        """Later roots win: the mod's bundles replace SPT's at the same key and CAB."""
        self.by_ref = {}
        for root in roots:
            base = os.path.join(root, "assets/content/audio/banks")
            for folder, _, files in os.walk(base):
                if os.path.join(base, "live_embedded") in folder:
                    continue
                for file in files:
                    if file.endswith(".bundle"):
                        bundle = Bundle(UnityPy, os.path.join(folder, file))
                        for path_id in bundle.banks:
                            self.by_ref[(bundle.cab_name, path_id)] = (bundle, path_id)

    def resolve(self, ref):
        return self.by_ref.get(ref)


def effective_signature(container, role, banks):
    """(bank name, signature, loop) of what a container plays for a role, or None."""
    ref = container.role(role)
    if ref is None:
        return None
    if ref[0] == "local":
        entry = container.banks.get(ref[1])
        if entry is None:
            return ("<missing>", None, False)
        sig, loop = container.signature(ref[1])
        return (entry[0], sig, loop)
    found = banks.resolve(ref)
    if found is None:
        return ("<unresolved %s %s>" % ref, None, False)
    bundle, path_id = found
    sig, loop = bundle.signature(path_id)
    return (bundle.banks[path_id][0], sig, loop)


def check_weapons(UnityPy, live, spt, spt_mods, mod, table):
    print("[3] weapons: what each weapon plays against the live banks")
    live_manifest = json.load(open(os.path.join(live, "Windows.json")))
    spt_manifest = json.load(open(os.path.join(spt, "Windows.json")))
    overrides = {}
    for name in OVERRIDING_MODS:
        path = os.path.join(spt_mods, name, "bundles.json")
        if os.path.exists(path):
            for entry in json.load(open(path))["manifest"]:
                overrides[entry["key"]] = os.path.join(spt_mods, name, "bundles", entry["key"])
        else:
            print(f"  note: {name} not found in {spt_mods}, its weapons are not checked against it")

    print("  indexing live, SPT and mod banks...")
    live_banks = Banks(UnityPy, live)
    # what the game resolves: SPT's banks, with the mod's bundles loaded in their place
    spt_banks = Banks(UnityPy, spt, os.path.join(mod, "bundles"))
    mod_bundles = {}

    def mod_bank(key, name):
        if key not in mod_bundles:
            path = os.path.join(mod, "bundles", key)
            mod_bundles[key] = Bundle(UnityPy, path) if os.path.exists(path) else None
        bundle = mod_bundles[key]
        if bundle is None:
            return None
        for path_id, (bank_name, _) in bundle.banks.items():
            if bank_name == name:
                sig, loop = bundle.signature(path_id)
                return sig, loop
        return None

    keys = sorted(k for k in set(spt_manifest) | set(overrides) if CONTAINER.search(k) and k in live_manifest)
    counts = {"table_ok": 0, "native_ok": 0, "silent": 0}
    seen = set()
    for key in keys:
        file_name = key.split("/")[-1]
        seen.add(file_name)
        live_container = Bundle(UnityPy, os.path.join(live, key))
        if live_container.player is None:
            continue
        entries = table.get(file_name)
        played = None
        if entries is None:
            path = overrides.get(key, os.path.join(spt, key))
            played = Bundle(UnityPy, path) if os.path.exists(path) else None
        weapon_ok = True
        for role in ROLES:
            wanted = effective_signature(live_container, role, live_banks)
            if wanted is None:
                continue
            name, signature, loop = wanted
            if role not in ALWAYS_COMPARED and not loop:
                continue
            if signature is None:
                problem("weapons", f"{file_name} {role}: live bank not readable ({name})")
                weapon_ok = False
                continue
            if entries is not None:
                entry = entries.get(role)
                if entry is None:
                    problem("weapons", f"{file_name} {role}: live plays {name}, the table has nothing for this role")
                    weapon_ok = False
                    continue
                got = mod_bank(*entry)
                got_sig, got_loop = got if got else (None, False)
            else:
                have = effective_signature(played, role, spt_banks) if played else None
                got_sig, got_loop = (have[1], have[2]) if have else (None, False)
                entry = ("(game's own bank)", have[0] if have else "<none>")
            if got_sig is None:
                problem("weapons", f"{file_name} {role}: {entry[1]} from {entry[0]} not found")
                weapon_ok = False
            elif got_sig != signature:
                live_set, got_set = set(signature), set(got_sig)
                if live_set == got_set:
                    print(f"  note: {file_name} {role}: {name} has the live clips with other repeat counts")
                    continue
                problem("weapons", f"{file_name} {role}: plays {entry[1]} ({entry[0]}) but live is {name}: "
                                   f"{len(live_set - got_set)} live clips missing, {len(got_set - live_set)} extra"
                                   f"{'' if entries is not None else ' [NOT IN TABLE]'}")
                weapon_ok = False
            elif got_loop:
                print(f"  note: {file_name} {role}: {name} is in loop format on live too")
        if weapon_ok:
            counts["table_ok" if entries is not None else "native_ok"] += 1
    for stale in sorted(set(table) - seen):
        problem("weapons", f"table entry for a container that is not in both manifests: {stale}")
    print(f"  {len(keys)} containers: {counts['table_ok']} ok through the table, "
          f"{counts['native_ok']} ok with the game's own bank")


# ------------------------------------------------------------------ 4. log

def check_log(path, table):
    print("[4] log: banks selected at runtime")
    line = re.compile(r"\[bank\] (\S+): body=(\S*) silenced=(\S*)")
    seen = {}
    for text in open(path, encoding="utf-8", errors="replace"):
        match = line.search(text)
        if match:
            seen[match.group(1)] = (match.group(2), match.group(3))
        if "could not load the live banks" in text:
            problem("log", text.strip()[:200])
    if not seen:
        print("  no [bank] line: turn LogShotDetails on, shoot every weapon, then pass the log again")
    for container, (body, silenced) in sorted(seen.items()):
        entry = table.get(container)
        if entry is None:
            continue
        for role, got in (("Body", body), ("BodySilenced", silenced)):
            wanted = entry.get(role)
            if wanted and wanted[1] != got:
                problem("log", f"{container} {role}: played {got}, table says {wanted[1]}")
    missing = sorted(set(table) - set(seen))
    print(f"  {len(seen)} containers seen, {len(set(table) & set(seen))} from the table")
    if seen and missing:
        print(f"  table weapons never seen in the log ({len(missing)}): not tested yet")
        for name in missing:
            print(f"    {name}")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--live", required=True)
    parser.add_argument("--spt", required=True)
    parser.add_argument("--spt-mods", required=True)
    parser.add_argument("--mod", required=True)
    parser.add_argument("--table", required=True)
    parser.add_argument("--log")
    parser.add_argument("--skip-weapons", action="store_true", help="skip check 3 (the only one needing UnityPy)")
    args = parser.parse_args()

    table = parse_table(args.table)
    if not table:
        sys.exit("the table could not be read")
    mod_keys = check_manifest(args.mod, table)
    check_bundles(args.live, args.mod, mod_keys)
    if not args.skip_weapons:
        check_weapons(load_unitypy(), args.live, args.spt, args.spt_mods, args.mod, table)
    if args.log:
        check_log(args.log, table)

    print(f"\n{len(problems)} problem(s)")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
