#!/usr/bin/env python3
"""Checks the transcribed bake files of client/acoustics against SPT's game (read-only).

Written apart from extract_live_acoustics.py so that its checks don't share the generator's
logic: the file is read the way SPT's AudioBakeDataReader reads it, and every invariant the game
relies on is checked again. Only the scene reading (rooms, portals, positions) is shared.

1. Format: whole file consumed; pair key = pair ID; first room < second room; one entry per
   pair of rooms; at least one route; TotalPortalsCount and ShortestRouteLength right; each pair
   listed once under its first room; route data (length, cost, StartIndex, NodeStartIndex) as
   the propagation job reads it, within ushort; maxima = the file's caps <= SPT's buffers.
2. Scene: every room and portal ID is an active object of SPT's scene with a unique ID (the
   game turns an unknown portal into 0); every route walks through doors that join the rooms
   it crosses, except routes copied unchanged from SPT's own file.
3. SPT's file: every SPT pair is still there; pairs not taken from the live game keep exactly
   SPT's routes.
4. Scene check sidecar: the SPT bake size and SHA-256 are those of the installed file; every
   room and portal the file uses is listed, at the position of SPT's scene.
5. Changes: per map, the shortest route of each pair compared with SPT's.

    python3 tools/check_live_acoustics.py --spt "<SPT>" --data client/acoustics

Exits with 1 on any failed check.
"""
import argparse
import collections
import hashlib
import math
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from extract_live_acoustics import DATA, SPT_UNITY, active_by_id, build_scenes, spt_scene  # noqa: E402
from UnityPy.helpers.TypeTreeGenerator import TypeTreeGenerator  # noqa: E402

USHORT = 65535


class GameReader:
    """Byte for byte what AudioBakeDataReader, RoomPair.Read and AudioRouteData.Read do."""

    def __init__(self, path):
        self.b = open(path, "rb").read()
        self.p = 0

    def take(self, fmt):
        v = struct.unpack_from("<" + fmt, self.b, self.p)
        self.p += struct.calcsize("<" + fmt)
        return v[0] if len(v) == 1 else v

    def read(self):
        pairs = {}
        for _ in range(self.take("i")):
            key = self.take("I")
            pid, first, second, count = self.take("I"), self.take("h"), self.take("h"), self.take("i")
            routes = []
            for _ in range(count):
                n = self.take("i")
                portals = tuple(self.take("h") for _ in range(n))
                routes.append((portals, self.take("f"), self.take("f")))
            pairs[key] = dict(id=pid, a=first, b=second, routes=routes, total=self.take("i"), short=self.take("B"))
        aware = []
        for _ in range(self.take("i")):
            room = self.take("h")
            aware.append((room, [self.take("I") for _ in range(self.take("i"))]))
        data = []
        for _ in range(self.take("i")):
            key = self.take("I")
            data.append((key, [self.take("hfHH") for _ in range(self.take("i"))]))
        settings = [self.take("i") for _ in range(6)]
        caps = (self.take("I"), self.take("I"))
        return dict(pairs=pairs, aware=aware, data=data, settings=settings, caps=caps, left=len(self.b) - self.p)


class Checks:
    def __init__(self, name):
        self.name = name
        self.failed = []

    def ok(self, condition, message):
        if not condition:
            self.failed.append(message)
        return condition


def joins_of(rooms, portals):
    by_path = {r["path_id"]: r["id"] for r in rooms}
    return {p["id"]: frozenset(by_path[c] for c in p["connected"] if c in by_path) for p in portals}


def walks(a, b, route, joins):
    room = a
    for portal in route:
        rooms = joins.get(portal, frozenset())
        if room not in rooms or len(rooms) != 2:
            return False
        room = next(r for r in rooms if r != room)
    return room == b


def check_map(name, out_path, spt_bake_path, rooms, portals, location):
    c = Checks(name)
    f = GameReader(out_path).read()
    s = GameReader(spt_bake_path).read()

    # 1. format
    c.ok(f["left"] == 0, f"{f['left']} bytes left after the data")
    seen = set()
    for key, p in f["pairs"].items():
        c.ok(key == p["id"], f"pair key {key} != id {p['id']}")
        c.ok(p["a"] < p["b"], f"pair {key}: rooms not ordered")
        c.ok((p["a"], p["b"]) not in seen, f"pair {key}: rooms ({p['a']}, {p['b']}) twice")
        seen.add((p["a"], p["b"]))
        c.ok(len(p["routes"]) > 0, f"pair {key}: no route")
        if p["routes"]:
            c.ok(p["total"] == sum(len(r[0]) for r in p["routes"]), f"pair {key}: TotalPortalsCount")
            c.ok(p["short"] == min(len(r[0]) for r in p["routes"]), f"pair {key}: ShortestRouteLength")
            c.ok(len({r[0] for r in p["routes"]}) == len(p["routes"]), f"pair {key}: same route twice")
    listed = collections.Counter(k for _, ids in f["aware"] for k in ids)
    c.ok(set(listed) == set(f["pairs"]) and max(listed.values()) == 1, "room lists don't hold every pair once")
    for room, ids in f["aware"]:
        c.ok(all(f["pairs"][k]["a"] == room for k in ids if k in f["pairs"]), f"room {room} lists a pair it doesn't start")
    c.ok(len(f["data"]) == len(f["pairs"]), "route data count")
    for key, entries in f["data"]:
        p = f["pairs"].get(key)
        if not c.ok(p is not None and len(entries) == len(p["routes"]), f"route data of {key}"):
            continue
        start = node = 0
        for (length, cost, s_idx, n_idx), (portals_, rcost, _) in zip(entries, p["routes"]):
            c.ok(length == len(portals_) and s_idx == start and n_idx == node and cost == rcost,
                 f"route data of pair {key}")
            start += len(portals_)
            node += len(portals_) + 2
        c.ok(node <= USHORT, f"pair {key}: {node} nodes, above ushort")
    max_routes = max(len(p["routes"]) for p in f["pairs"].values())
    max_portals = max(p["total"] for p in f["pairs"].values())
    c.ok(tuple(f["caps"]) == (max_routes, max_portals), f"caps {f['caps']} != ({max_routes}, {max_portals})")
    c.ok(max_routes <= location["maxRoutesCount"] and max_portals <= location["maxRoutePortalsCount"],
         f"({max_routes}, {max_portals}) above SPT's buffers")

    # 2. scene
    active_rooms, active_portals = active_by_id(rooms), active_by_id(portals)
    room_ids = {r["id"] for r in active_rooms}
    joins = joins_of(active_rooms, active_portals)
    spt_routes = {(p["a"], p["b"]): {r[0] for r in p["routes"]} for p in s["pairs"].values()}
    bad_rooms = bad_portals = bad_walks = inherited_bad = 0
    for p in f["pairs"].values():
        if p["a"] not in room_ids or p["b"] not in room_ids:
            bad_rooms += 1
        for route, _, _ in p["routes"]:
            if any(x not in joins for x in route):
                bad_portals += 1
            elif not walks(p["a"], p["b"], route, joins):
                if route in spt_routes.get((p["a"], p["b"]), ()):
                    inherited_bad += 1
                else:
                    bad_walks += 1
    c.ok(bad_rooms == 0, f"{bad_rooms} pairs use a room absent from the scene")
    c.ok(bad_portals == 0, f"{bad_portals} routes use a portal absent from the scene")
    c.ok(bad_walks == 0, f"{bad_walks} new routes go through doors that don't join their rooms")

    # 3. SPT's file
    out_pairs = {(p["a"], p["b"]): p["routes"] for p in f["pairs"].values()}
    missing = [k for k in spt_routes if k not in out_pairs]
    c.ok(not missing, f"{len(missing)} SPT pairs lost")

    # 4. sidecar
    check_path = out_path + ".scene"
    expected = {}
    header = {}
    if c.ok(os.path.exists(check_path), "no .scene file"):
        for line in open(check_path):
            parts = line.split()
            if not parts or parts[0].startswith("#"):
                continue
            if parts[0] in ("room", "portal"):
                expected[(parts[0], int(parts[1]))] = tuple(float(v) for v in parts[2:5])
            else:
                header[parts[0]] = parts[1]
        data = open(spt_bake_path, "rb").read()
        c.ok(header.get("version") == "1", "unknown .scene version")
        c.ok(int(header.get("spt_bake_size", -1)) == len(data), "SPT bake size differs from the .scene file")
        c.ok(header.get("spt_bake_sha256") == hashlib.sha256(data).hexdigest(), "SPT bake hash differs from the .scene file")
        used_rooms = {r for k in out_pairs for r in k}
        used_portals = {x for routes in out_pairs.values() for r, _, _ in routes for x in r}
        c.ok({i for k, i in expected if k == "room"} == used_rooms, ".scene rooms differ from the rooms used")
        c.ok({i for k, i in expected if k == "portal"} == used_portals, ".scene portals differ from the portals used")
        where = {("room", r["id"]): r["pos"] for r in active_rooms}
        where.update({("portal", p["id"]): p["pos"] for p in active_portals})
        far = [k for k, v in expected.items() if k not in where or math.dist(v, where[k]) > 0.01]
        c.ok(not far, f"{len(far)} .scene positions differ from the scene, e.g. {far[:3]}")

    # 5. changes against SPT
    stats = collections.Counter()
    for key, routes in out_pairs.items():
        old = s_pairs_routes(s, key)
        if old is None:
            stats["added pairs"] += 1
            continue
        if {r[0] for r in routes} == {r[0] for r in old}:
            stats["same routes"] += 1
            continue
        stats["other routes"] += 1
        d_old, d_new = min(r[2] for r in old), min(r[2] for r in routes)
        if d_new < d_old - 2:
            stats["shortest route > 2 m shorter"] += 1
        elif d_new > d_old + 2:
            stats["shortest route > 2 m longer"] += 1
    stats["routes copied from SPT that SPT's scene doesn't walk"] = inherited_bad
    return c, dict(stats), dict(pairs=len(out_pairs), spt_pairs=len(spt_routes), rooms=len(room_ids),
                                portals=len(joins))


def s_pairs_routes(s, key):
    if "_index" not in s:
        s["_index"] = {(p["a"], p["b"]): p["routes"] for p in s["pairs"].values()}
    return s["_index"].get(key)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--spt", required=True, help="SPT folder")
    parser.add_argument("--data", required=True, help="folder holding AudioBakeData/")
    args = parser.parse_args()
    spt_data = os.path.join(args.spt, DATA)
    generator = TypeTreeGenerator(SPT_UNITY)
    generator.load_local_game(args.spt)
    bake_dir = os.path.join(args.data, "AudioBakeData")
    files = {f for f in os.listdir(bake_dir) if f.endswith(".audiobakedata")}
    sidecars = {f[:-len(".scene")] for f in os.listdir(bake_dir) if f.endswith(".scene")}
    failed = False
    if sidecars != files:
        print(f"FAIL: .scene files without bake or the reverse: {sorted(sidecars ^ files)}")
        failed = True
    done = set()
    for index, scene in enumerate(build_scenes(spt_data)):
        if not scene.endswith("_Sound.unity") and not scene.endswith("_Sound_Rework.unity"):
            continue
        rooms, portals, location = spt_scene(spt_data, f"level{index}", generator)
        if not location:
            continue
        file = location["relativeBakeDataPath"].replace("\\", "/").split("/")[-1]
        if file not in files:
            continue
        done.add(file)
        c, stats, sizes = check_map(file, os.path.join(bake_dir, file),
                                    os.path.join(spt_data, "StreamingAssets", "AudioBakeData", file),
                                    rooms, portals, location)
        status = "ok" if not c.failed else "FAIL"
        print(f"{file}: {status} | {sizes} | {stats}")
        for message in c.failed[:10]:
            print(f"   {message}")
        failed |= bool(c.failed)
        sys.stdout.flush()
    if files - done:
        print(f"FAIL: no SPT scene uses {sorted(files - done)}")
        failed = True
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
