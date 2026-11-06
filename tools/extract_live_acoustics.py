#!/usr/bin/env python3
"""Transcribes the live rooms, portals and routes (.audiobakedata) into SPT's scenes.

A bake file stores, for each pair of audio rooms, the routes a sound can take between them as
lists of portal IDs. The IDs are the `_iD` fields of the `SpatialAudioRoom` and
`BaseSpatialAudioPortal` components of the location's `*_Sound` scene, and they are renumbered
at every bake: the live file read as is in SPT would point at other rooms and portals.

For each location both games share, this script:

1. reads the active rooms and portals of the `*_Sound` scene on both sides (the live game is
   IL2CPP: its components are decoded raw, following the field order of the live dump);
2. pairs each live room and portal with the SPT one at the same place (5 cm), or with the one
   of the same name within 1 m when the name is unique on both sides. Rooms are placed by their
   game object, then by the center of their volume. A portal left over that is
   the only one between the same two rooms on both sides, within 3 m, is paired too;
3. translates every live room pair whose rooms and portals all have an SPT equivalent, and
   checks each route against SPT's own portal connections (`_connectedRooms`);
4. writes a file where these translated pairs replace SPT's, and where every pair the live data
   can't cover keeps SPT's routes. A translated pair also keeps the SPT routes that go through
   an opening the live scene doesn't have, or that opens on other rooms there. No pair of the SPT file is lost. A pair whose routes would
   exceed the buffers SPT allocates (`maxRoutesCount`, `maxRoutePortalsCount` of the location)
   also keeps SPT's routes;
5. reads the written file back and checks it, and writes `<file>.scene` next to it: the size and
   SHA-256 of SPT's bake, and the ID and world position of every room and portal the file uses.
   The plugin compares them with the loaded scene before using the file.

The Meta XR acoustic maps (`.xramap`) and geometry (`.xrageo`) are not imported: they are baked
from the meshes, and 287 of the 565 geometry files of SPT's StreamingAssets hold another mesh
on the live game (`--metaxr-report` prints the comparison).

    python3 tools/extract_live_acoustics.py \\
      --live "<live game folder>" --spt "<SPT folder>" \\
      --out client/acoustics --report docs/live-acoustics-report.json

Requires UnityPy, TypeTreeGeneratorAPI and numpy.
"""
import argparse
import collections
import hashlib
import json
import math
import os
import struct
import sys

import numpy as np
import UnityPy
from UnityPy.helpers.TypeTreeGenerator import TypeTreeGenerator

SPT_UNITY = "2022.3.43f1"
DATA = "EscapeFromTarkov_Data"
EXACT_TOLERANCE = 0.05
NAME_TOLERANCE = 1.0
ROOM_PAIR_TOLERANCE = 3.0


# ---------------------------------------------------------------- bake file


class Reader:
    def __init__(self, data):
        self.data = data
        self.pos = 0

    def read(self, fmt):
        values = struct.unpack_from("<" + fmt, self.data, self.pos)
        self.pos += struct.calcsize("<" + fmt)
        return values if len(values) > 1 else values[0]

    def many(self, count, kind):
        values = struct.unpack_from(f"<{count}{kind}", self.data, self.pos)
        self.pos += struct.calcsize(f"<{count}{kind}")
        return list(values)


def read_bake(path):
    """Mirror of AudioBakeDataReader (same layout in SPT 4.1.6 and live 1.3)."""
    r = Reader(open(path, "rb").read())
    pairs = {}
    for _ in range(r.read("i")):
        key = r.read("I")
        pair_id, first, second, route_count = r.read("Ihhi")
        routes = []
        for _ in range(route_count):
            portals = tuple(r.many(r.read("i"), "h"))
            cost, distance = r.read("ff")
            routes.append((portals, cost, distance))
        total, shortest = r.read("iB")
        pairs[key] = dict(id=pair_id, a=first, b=second, routes=routes, total=total, shortest=shortest)
    aware = {}
    for _ in range(r.read("i")):
        room = r.read("h")
        aware[room] = r.many(r.read("i"), "I")
    route_data = {}
    for _ in range(r.read("i")):
        key = r.read("I")
        route_data[key] = [r.read("hfHH") for _ in range(r.read("i"))]
    settings = r.read("6i")
    caps = r.read("II")
    if r.pos != len(r.data):
        raise ValueError(f"{path}: {len(r.data) - r.pos} bytes left after the bake data")
    return dict(pairs=pairs, aware=aware, route_data=route_data, settings=settings, caps=caps)


def write_bake(path, pairs, settings):
    """pairs: {(a, b): routes} with a < b. Mirror of AudioBakeDataWriter."""
    keys = sorted(pairs)
    out = bytearray()
    out += struct.pack("<i", len(keys))
    for pair_id, (a, b) in enumerate(keys, 1):
        routes = pairs[(a, b)]
        out += struct.pack("<IIhhi", pair_id, pair_id, a, b, len(routes))
        for portals, cost, distance in routes:
            out += struct.pack(f"<i{len(portals)}h", len(portals), *portals)
            out += struct.pack("<ff", cost, distance)
        out += struct.pack("<iB", sum(len(p) for p, _, _ in routes), min(len(p) for p, _, _ in routes))
    by_room = collections.defaultdict(list)
    for pair_id, (a, _) in enumerate(keys, 1):
        by_room[a].append(pair_id)
    out += struct.pack("<i", len(by_room))
    for room in sorted(by_room):
        ids = by_room[room]
        out += struct.pack(f"<hi{len(ids)}I", room, len(ids), *ids)
    out += struct.pack("<i", len(keys))
    for pair_id, key in enumerate(keys, 1):
        routes = pairs[key]
        out += struct.pack("<Ii", pair_id, len(routes))
        start = node = 0
        for portals, cost, _ in routes:
            # the route's slice in the pair's portal list, and in its node list (portals + both ends)
            out += struct.pack("<hfHH", len(portals), cost, start, node)
            start += len(portals)
            node += len(portals) + 2
    max_routes = max(len(r) for r in pairs.values())
    max_portals = max(sum(len(p) for p, _, _ in r) for r in pairs.values())
    out += struct.pack("<6i", *settings)
    out += struct.pack("<II", max_routes, max_portals)
    with open(path, "wb") as f:
        f.write(out)


def bake_portal_ids(bake):
    return {x for pair in bake["pairs"].values() for p, _, _ in pair["routes"] for x in p}


def bake_pairs(bake):
    """{(a, b): routes} with a < b, as the bake stores them."""
    result = {}
    for pair in bake["pairs"].values():
        if pair["a"] >= pair["b"]:
            raise ValueError("bake pair not ordered")
        result[(pair["a"], pair["b"])] = pair["routes"]
    return result


# ---------------------------------------------------------------- scenes


def build_scenes(data_dir):
    env = UnityPy.load(os.path.join(data_dir, "globalgamemanagers"))
    for obj in env.objects:
        if obj.type.name == "BuildSettings":
            return obj.read_typetree()["scenes"]
    raise SystemExit(f"no BuildSettings in {data_dir}")


def rotation_matrix(q):
    x, y, z, w = q.x, q.y, q.z, q.w
    return np.array([
        [1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
        [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
        [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)],
    ])


class Scene:
    """Transforms and game objects of one level file: world position and activeInHierarchy."""

    def __init__(self, path):
        self.env = UnityPy.load(path)
        self.objects = {o.path_id: o for o in self.env.objects}
        self.transforms = {pid: o.read() for pid, o in self.objects.items() if o.type.name == "Transform"}
        self._cache = {}

    def game_object(self, path_id):
        if path_id not in self._cache:
            go = self.objects[path_id].read()
            transform = next((self.transforms[c.component.path_id] for c in go.m_Component
                              if c.component.path_id in self.transforms), None)
            matrix = np.eye(4)
            active = True
            cur = transform
            while cur is not None:
                local = np.eye(4)
                local[:3, :3] = rotation_matrix(cur.m_LocalRotation) @ np.diag(
                    [cur.m_LocalScale.x, cur.m_LocalScale.y, cur.m_LocalScale.z])
                local[:3, 3] = [cur.m_LocalPosition.x, cur.m_LocalPosition.y, cur.m_LocalPosition.z]
                matrix = local @ matrix
                active &= bool(self.objects[cur.m_GameObject.path_id].read().m_IsActive)
                father = cur.m_Father
                cur = self.transforms.get(father.path_id) if father and father.m_FileID == 0 else None
            self._cache[path_id] = (go.m_Name, [float(v) for v in matrix[:3, 3]], active)
        return self._cache[path_id]

    def behaviours(self):
        """(object, class name) of every MonoBehaviour whose script resolves."""
        for obj in self.env.objects:
            if obj.type.name != "MonoBehaviour":
                continue
            mb = obj.read(check_read=False)
            try:
                script = mb.m_Script.read()
            except Exception:
                continue
            yield obj, script


PORTAL_CLASSES = {"SpatialAudioPortal", "UniversalTriggerSpatialAudioPortal", "MultiWindowPortal"}


def is_portal(script):
    """The three BaseSpatialAudioPortal subclasses, the same in both games."""
    return script.m_ClassName in PORTAL_CLASSES


def spt_scene(data_dir, level, generator):
    """Rooms and portals of an SPT (Mono) scene, through the type trees of its assemblies."""
    scene = Scene(os.path.join(data_dir, level))
    rooms, portals, info = [], [], None
    for obj, script in scene.behaviours():
        name = script.m_ClassName
        if name not in ("SpatialAudioRoom", "SpatialAudioSystem") and not is_portal(script):
            continue
        full = (script.m_Namespace + "." if script.m_Namespace else "") + name
        tree = obj.read_typetree(nodes=generator.get_nodes_up(script.m_AssemblyName.replace(".dll", ""), full))
        if name == "SpatialAudioSystem":
            info = (obj.assets_file, tree["_locationInfo"])
            continue
        go_name, pos, active = scene.game_object(tree["m_GameObject"]["m_PathID"])
        record = dict(path_id=obj.path_id, id=tree["_iD"], name=go_name, pos=pos, active=active)
        if name == "SpatialAudioRoom":
            bounds = tree["_bounds"]
            record["center"] = [bounds["m_Center"][k] for k in "xyz"]
            rooms.append(record)
        else:
            record["connected"] = [p["m_PathID"] for p in tree["_connectedRooms"] if p["m_FileID"] == 0]
            portals.append(record)
    location = None
    if info:
        assets_file, pptr = info
        external = assets_file.externals[pptr["m_FileID"] - 1].path
        env = UnityPy.load(os.path.join(data_dir, external))
        obj = next(o for o in env.objects if o.path_id == pptr["m_PathID"])
        script = obj.read(check_read=False).m_Script.read()
        location = obj.read_typetree(nodes=generator.get_nodes_up(
            "Assembly-CSharp", (script.m_Namespace + "." if script.m_Namespace else "") + script.m_ClassName))
    return rooms, portals, location


class Raw(Reader):
    """Unity serialization without type tree: 4-byte alignment after bools, shorts and strings."""

    def align(self):
        self.pos = (self.pos + 3) & ~3

    def byte(self):
        value = self.data[self.pos]
        self.pos += 1
        self.align()
        return value

    def short(self):
        value = self.read("h")
        self.align()
        return value

    def pptr(self):
        return self.read("iq")

    def string(self):
        size = self.read("i")
        value = self.data[self.pos:self.pos + size].decode("utf8", "replace")
        self.pos += size
        self.align()
        return value

    def pptr_list(self):
        count = self.read("i")
        if not 0 <= count < 100000:
            raise ValueError(f"bad list size {count}")
        return [self.pptr() for _ in range(count)]

    def curve(self):
        count = self.read("i")
        if not 0 <= count < 10000:
            raise ValueError(f"bad curve size {count}")
        self.pos += count * 28 + 12  # keyframes, then pre/post infinity and rotation order

    def header(self):
        game_object = self.pptr()
        self.byte()      # m_Enabled
        self.pptr()      # m_Script
        self.string()    # m_Name
        return game_object


def live_room(raw):
    """SpatialAudioRoom of live 1.3: roomConnections is serialized, allPortals is not."""
    r = Raw(raw)
    game_object = r.header()
    r.read("i")          # priority
    r.read("f")          # WallOcclusion
    r.pptr_list()        # Areas
    r.byte()             # Outdoor
    r.byte()             # Isolated
    r.read("i")          # _type
    r.byte()             # FitToGeometry
    count = r.read("i")  # roomConnections
    if not 0 <= count < 10000:
        raise ValueError(f"bad connection count {count}")
    for _ in range(count):
        r.pptr()
        r.pptr_list()
    room_id = r.short()
    r.read("f")          # _roomSize
    center = list(r.read("3f"))
    return game_object, room_id, center


def live_portal(raw):
    """BaseSpatialAudioPortal of live 1.3, up to _connectedRooms."""
    r = Raw(raw)
    game_object = r.header()
    r.read("f")          # _transmission
    r.read("f")          # Depth
    r.string()           # portalName
    r.read("i")          # portalType
    r.byte()             # state
    r.pptr()             # portalCollider
    r.read("f")          # traversalMaxCost
    r.byte()             # ToOutdoor
    r.curve()            # openEnvelope
    r.curve()            # closeEnvelope
    r.read("f")          # openFadeTime
    r.read("f")          # closeFadeTime
    portal_id = r.short()
    connected = [pid for fid, pid in r.pptr_list() if fid == 0]
    return game_object, portal_id, connected


def live_scene(data_dir, level):
    scene = Scene(os.path.join(data_dir, level))
    rooms, portals = [], []
    for obj, script in scene.behaviours():
        if script.m_ClassName == "SpatialAudioRoom":
            game_object, room_id, center = live_room(obj.get_raw_data())
            record = dict(center=center)
        elif is_portal(script):
            game_object, room_id, connected = live_portal(obj.get_raw_data())
            record = dict(connected=connected)
        else:
            continue
        go_name, pos, active = scene.game_object(game_object[1])
        record.update(path_id=obj.path_id, id=room_id, name=go_name, pos=pos, active=active)
        (rooms if "center" in record else portals).append(record)
    return rooms, portals


# ---------------------------------------------------------------- matching


def active_by_id(items):
    """Active components with a unique positive ID: the ones FindObjectsOfType registers."""
    counts = collections.Counter(i["id"] for i in items if i["active"])
    return [i for i in items if i["active"] and i["id"] > 0 and counts[i["id"]] == 1]


def match(live_items, spt_items, keys):
    """live ID -> SPT ID, one to one. For each key in turn (a position): same place (5 cm), or
    same unique name within 1 m. Items already paired are left out of the next keys."""
    live_names = collections.Counter(i["name"] for i in live_items)
    spt_names = collections.Counter(i["name"] for i in spt_items)
    result = {}
    stats = collections.Counter()
    for key in keys:
        taken = set(result.values())
        free = [s for s in spt_items if s["id"] not in taken]
        grid = collections.defaultdict(list)
        by_name = {}
        for item in free:
            grid[tuple(math.floor(v) for v in item[key])].append(item)
            if spt_names[item["name"]] == 1:
                by_name[item["name"]] = item
        found = {}
        for item in live_items:
            if item["id"] in result:
                continue
            cx, cy, cz = (math.floor(v) for v in item[key])
            near = [s for dx in (-1, 0, 1) for dy in (-1, 0, 1) for dz in (-1, 0, 1)
                    for s in grid.get((cx + dx, cy + dy, cz + dz), [])
                    if math.dist(item[key], s[key]) <= EXACT_TOLERANCE]
            if len(near) > 1:
                near = [s for s in near if s["name"] == item["name"]]
            if len(near) == 1:
                found[item["id"]] = (near[0]["id"], f"same place ({key})")
                continue
            named = by_name.get(item["name"])
            if live_names[item["name"]] == 1 and named and math.dist(item[key], named[key]) <= NAME_TOLERANCE:
                found[item["id"]] = (named["id"], f"same name ({key})")
        targets = collections.Counter(v for v, _ in found.values())
        for live_id, (spt_id, how) in found.items():
            if targets[spt_id] == 1:
                result[live_id] = spt_id
                stats[how] += 1
    stats["no match"] = len(live_items) - len(result)
    return result, dict(stats)


def match_by_rooms(portal_map, room_map, live_portals, spt_portals, live_joins, spt_joins):
    """Unmatched portals that are the only one between the same two (matched) rooms on both
    sides, within ROOM_PAIR_TOLERANCE: the same opening, moved or renamed."""
    taken = set(portal_map.values())
    spt_pos = {p["id"]: p["pos"] for p in spt_portals}
    live_free, spt_free = collections.defaultdict(list), collections.defaultdict(list)
    for p in live_portals:
        rooms = live_joins.get(p["id"], set())
        if p["id"] not in portal_map and len(rooms) == 2 and rooms <= room_map.keys():
            live_free[frozenset(room_map[r] for r in rooms)].append(p)
    for p in spt_portals:
        rooms = spt_joins.get(p["id"], set())
        if p["id"] not in taken and len(rooms) == 2:
            spt_free[frozenset(rooms)].append(p["id"])
    added = 0
    for rooms, candidates in live_free.items():
        targets = spt_free.get(rooms, [])
        if len(candidates) == 1 and len(targets) == 1 \
                and math.dist(candidates[0]["pos"], spt_pos[targets[0]]) <= ROOM_PAIR_TOLERANCE:
            portal_map[candidates[0]["id"]] = targets[0]
            added += 1
    return added


def connections(rooms, portals):
    """portal ID -> set of room IDs it joins, from the serialized _connectedRooms."""
    room_by_path = {r["path_id"]: r["id"] for r in rooms}
    return {p["id"]: {room_by_path[c] for c in p["connected"] if c in room_by_path} for p in portals}


def route_is_valid(start, end, portals, joins):
    room = start
    for portal in portals:
        rooms = joins.get(portal, set())
        if room not in rooms or len(rooms) != 2:
            return False
        room = next(r for r in rooms if r != room)
    return room == end


def same_routes(a, b):
    def norm(routes):
        return sorted((p, round(c, 4), round(d, 3)) for p, c, d in routes)
    return norm(a) == norm(b)


# ---------------------------------------------------------------- location


def transcribe(name, live_rooms, live_portals, spt_rooms, spt_portals, live_bake, spt_bake, location):
    live_rooms, live_portals = active_by_id(live_rooms), active_by_id(live_portals)
    spt_rooms_ok, spt_portals_ok = active_by_id(spt_rooms), active_by_id(spt_portals)
    # a room's volume can grow on one side (75 m higher outdoors on Streets): its object first
    room_map, room_stats = match(live_rooms, spt_rooms_ok, ("pos", "center"))
    portal_map, portal_stats = match(live_portals, spt_portals_ok, ("pos",))
    live_joins = connections(live_rooms, live_portals)
    spt_joins = connections(spt_rooms_ok, spt_portals_ok)
    portal_stats["same rooms"] = match_by_rooms(portal_map, room_map, live_portals, spt_portals_ok,
                                                live_joins, spt_joins)
    portal_stats["no match"] -= portal_stats["same rooms"]
    max_routes, max_portals = location["maxRoutesCount"], location["maxRoutePortalsCount"]

    spt_pairs = bake_pairs(spt_bake)
    spt_valid = sum(all(route_is_valid(a, b, p, spt_joins) for p, _, _ in routes)
                    for (a, b), routes in spt_pairs.items())
    live_pairs = bake_pairs(live_bake)
    live_valid = sum(all(route_is_valid(a, b, p, live_joins) for p, _, _ in routes)
                     for (a, b), routes in live_pairs.items())

    reasons = collections.Counter()
    translated = {}
    completed = 0
    # openings the live routes can't account for: absent from the live scene, or opening on
    # other rooms there (Streets splits outdoor zones: a window that leads to the common outdoor
    # room in SPT leads to a smaller zone on the live game)
    spt_only = set(spt_joins) - set(portal_map.values())
    spt_only |= {s for l, s in portal_map.items()
                 if {room_map.get(r) for r in live_joins.get(l, ())} != spt_joins.get(s)}
    for (a, b), routes in live_pairs.items():
        if a not in room_map or b not in room_map:
            reasons["room without SPT equivalent"] += 1
            continue
        if any(x not in portal_map for p, _, _ in routes for x in p):
            reasons["portal without SPT equivalent"] += 1
            continue
        ta, tb = room_map[a], room_map[b]
        new = [(tuple(portal_map[x] for x in p), c, d) for p, c, d in routes]
        if ta > tb:
            ta, tb = tb, ta
            new = [(p[::-1], c, d) for p, c, d in new]
        if not all(route_is_valid(ta, tb, p, spt_joins) for p, _, _ in new):
            reasons["route through other SPT rooms"] += 1
            continue
        # the live bake can't know the openings SPT's scene has and the live one lost
        live_routes = {p for p, _, _ in new}
        kept = [r for r in spt_pairs.get((ta, tb), []) if spt_only & set(r[0]) and r[0] not in live_routes]
        if len(new) + len(kept) > max_routes or sum(len(p) for p, _, _ in new + kept) > max_portals:
            reasons["above SPT buffer size"] += 1
            continue
        if kept:
            completed += 1
        translated[(ta, tb)] = new + kept

    merged = dict(spt_pairs)
    identical = changed = added = 0
    for key, routes in translated.items():
        if key in spt_pairs:
            if same_routes(routes, spt_pairs[key]):
                identical += 1
            else:
                changed += 1
        else:
            added += 1
        merged[key] = routes
    report = dict(
        rooms=dict(live=len(live_rooms), spt=len(spt_rooms_ok), matched=len(room_map), **room_stats),
        portals=dict(live=len(live_portals), spt=len(spt_portals_ok), matched=len(portal_map), **portal_stats),
        bake_portals_outside_sound_scene=len(bake_portal_ids(live_bake) - {p["id"] for p in live_portals}),
        live_pairs=len(live_pairs), live_pairs_valid_in_live_scene=live_valid,
        spt_pairs=len(spt_pairs), spt_pairs_valid_in_spt_scene=spt_valid,
        translated=len(translated), not_translated=dict(reasons),
        spt_only_portals=len(spt_only), translated_with_spt_only_routes=completed,
        from_live_identical=identical, from_live_changed=changed, from_live_added=added,
        kept_from_spt=len(spt_pairs) - identical - changed, total=len(merged),
        live_settings=list(live_bake["settings"]), spt_settings=list(spt_bake["settings"]),
    )
    return merged, report, spt_joins, spt_rooms_ok, spt_portals_ok


def write_scene_check(path, merged, spt_rooms, spt_portals, spt_bake_path):
    """Sidecar the plugin reads before using the file: the SPT bake it was built from, and the ID
    and world position of every room and portal it uses. If SPT's scene changes, the plugin
    sees other objects under these IDs and falls back to SPT's file."""
    rooms = {r for key in merged for r in key}
    portals = {x for routes in merged.values() for p, _, _ in routes for x in p}
    data = open(spt_bake_path, "rb").read()
    lines = ["# Sound Engine Backport scene check, written by tools/extract_live_acoustics.py",
             "version 1",
             f"spt_bake_size {len(data)}",
             f"spt_bake_sha256 {hashlib.sha256(data).hexdigest()}"]
    for kind, items, used in (("room", spt_rooms, rooms), ("portal", spt_portals, portals)):
        for item in sorted(items, key=lambda i: i["id"]):
            if item["id"] in used:
                x, y, z = item["pos"]
                lines.append(f"{kind} {item['id']} {x:.3f} {y:.3f} {z:.3f}")
    with open(path, "w", newline="\n") as f:
        f.write("\n".join(lines) + "\n")
    return len(rooms), len(portals)


def check_written(path, merged, spt_joins, spt_room_ids, location):
    bake = read_bake(path)
    pairs = bake_pairs(bake)
    if {k: [p for p, _, _ in v] for k, v in pairs.items()} != {k: [p for p, _, _ in v] for k, v in merged.items()}:
        raise SystemExit(f"{path}: pairs differ after reading back")
    for (a, b), routes in pairs.items():
        if a not in spt_room_ids or b not in spt_room_ids:
            raise SystemExit(f"{path}: pair ({a}, {b}) uses a room absent from SPT's scene")
        for portals, _, _ in routes:
            if any(p not in spt_joins for p in portals):
                raise SystemExit(f"{path}: pair ({a}, {b}) uses a portal absent from SPT's scene")
    if bake["caps"][0] > location["maxRoutesCount"] or bake["caps"][1] > location["maxRoutePortalsCount"]:
        raise SystemExit(f"{path}: caps {bake['caps']} above SPT's buffers")
    for key, pair in bake["pairs"].items():
        data = bake["route_data"][key]
        if pair["id"] != key or len(data) != len(pair["routes"]):
            raise SystemExit(f"{path}: route data of pair {key} inconsistent")
    listed = [k for ids in bake["aware"].values() for k in ids]
    if sorted(listed) != sorted(bake["pairs"]):
        raise SystemExit(f"{path}: room lists don't cover every pair once")
    return bake["caps"]


# ---------------------------------------------------------------- Meta XR


def metaxr_report(live_data, spt_data):
    """Geometry files SPT's scenes can load, compared with the live ones of the same path."""
    live_root = os.path.join(live_data, "StreamingAssets", "Acoustics")
    spt_root = os.path.join(spt_data, "StreamingAssets", "Acoustics")
    counts = collections.Counter()
    for folder, _, files in os.walk(spt_root):
        for file in files:
            spt_path = os.path.join(folder, file)
            live_path = os.path.join(live_root, os.path.relpath(spt_path, spt_root))
            kind = os.path.splitext(file)[1]
            if not os.path.exists(live_path):
                counts[f"{kind} missing on live"] += 1
                continue
            a, b = open(live_path, "rb").read(), open(spt_path, "rb").read()
            if a == b:
                counts[f"{kind} identical"] += 1
            elif kind == ".xrageo" and a[16:28] != b[16:28]:
                counts[f"{kind} other mesh (vertex/triangle counts differ)"] += 1
            else:
                counts[f"{kind} different"] += 1
    return dict(counts)


# ---------------------------------------------------------------- main


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--live", required=True, help="live game folder (holds GameAssembly.dll)")
    parser.add_argument("--spt", required=True, help="SPT folder (holds EscapeFromTarkov_Data)")
    parser.add_argument("--out", required=True, help="output folder, AudioBakeData/ is written in it")
    parser.add_argument("--report", help="JSON report")
    parser.add_argument("--only", help="comma-separated bake names (e.g. labyrinth_sound)")
    args = parser.parse_args()

    live_data, spt_data = os.path.join(args.live, DATA), os.path.join(args.spt, DATA)
    generator = TypeTreeGenerator(SPT_UNITY)
    generator.load_local_game(args.spt)
    live_scenes, spt_scenes = build_scenes(live_data), build_scenes(spt_data)
    only = set(args.only.split(",")) if args.only else None
    out_dir = os.path.join(args.out, "AudioBakeData")
    os.makedirs(out_dir, exist_ok=True)
    report = {}

    for index, scene in enumerate(spt_scenes):
        if not scene.endswith("_Sound.unity") and not scene.endswith("_Sound_Rework.unity"):
            continue
        level = f"level{index}"
        if index >= len(live_scenes) or live_scenes[index] != scene:
            print(f"{scene}: not at the same index on the live game, skipped")
            continue
        spt_rooms, spt_portals, location = spt_scene(spt_data, level, generator)
        if not location:
            continue
        bake_name = location["relativeBakeDataPath"].replace("\\", "/").split("/")[-1]
        stem = bake_name.rsplit(".", 1)[0]
        if only and stem not in only:
            continue
        live_path = os.path.join(live_data, "StreamingAssets", "AudioBakeData", bake_name)
        spt_path = os.path.join(spt_data, "StreamingAssets", "AudioBakeData", bake_name)
        if not os.path.exists(live_path):
            print(f"{bake_name}: no live file, skipped")
            continue
        if open(live_path, "rb").read() == open(spt_path, "rb").read():
            print(f"{bake_name}: identical to SPT's, skipped")
            continue
        live_rooms, live_portals = live_scene(live_data, level)
        live_bake, spt_bake = read_bake(live_path), read_bake(spt_path)
        if list(spt_bake["caps"]) != [location["maxRoutesCount"], location["maxRoutePortalsCount"]]:
            print(f"{bake_name}: SPT caps {spt_bake['caps']} differ from its location info")
        merged, entry, spt_joins, spt_rooms_ok, spt_portals_ok = transcribe(
            stem, live_rooms, live_portals, spt_rooms, spt_portals, live_bake, spt_bake, location)
        spt_room_ids = {r["id"] for r in spt_rooms_ok}
        out_path = os.path.join(out_dir, bake_name)
        check_path = out_path + ".scene"
        if entry["translated"] == entry["from_live_identical"] and not entry["from_live_added"]:
            print(f"{bake_name}: the live routes change nothing, not written")
            for stale in (out_path, check_path):
                if os.path.exists(stale):
                    os.remove(stale)
        else:
            settings = [location["bakeSettings"][k] for k in (
                "maxDepthIndoorToIndoor", "maxDepthIndoorToOutdoor", "maxDepthOutdoorToOutdoor",
                "maxDistanceOutdoorToOutdoor", "maxDistanceIndoorToOutdoor", "maxDistanceIndoorToIndoor")]
            write_bake(out_path, merged, settings)
            entry["caps"] = list(check_written(out_path, merged, spt_joins, spt_room_ids, location))
            entry["spt_caps"] = [location["maxRoutesCount"], location["maxRoutePortalsCount"]]
            entry["written"] = os.path.relpath(out_path, args.out)
            entry["scene_check"] = dict(zip(("rooms", "portals"), write_scene_check(
                check_path, merged, spt_rooms_ok, spt_portals_ok, spt_path)))
        entry["scene"] = scene
        report[stem] = entry
        print(f"{stem}: rooms {entry['rooms']['matched']}/{entry['rooms']['live']}, "
              f"portals {entry['portals']['matched']}/{entry['portals']['live']}, "
              f"live pairs {entry['translated']}/{entry['live_pairs']} translated "
              f"({entry['from_live_changed']} changed, {entry['from_live_identical']} identical, "
              f"{entry['from_live_added']} added), {entry['kept_from_spt']}/{entry['spt_pairs']} kept from SPT")
        sys.stdout.flush()

    meta = metaxr_report(live_data, spt_data)
    print("Meta XR files (not imported):", meta)
    if args.report:
        with open(args.report, "w") as f:
            json.dump(dict(locations=report, metaxr=meta), f, indent=2, sort_keys=True)
            f.write("\n")


if __name__ == "__main__":
    main()
