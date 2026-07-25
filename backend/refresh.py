import json
import os
import sys
import traceback
from datetime import datetime, timedelta, timezone
from pathlib import Path

from coord import locate
from gametime import TICKS_PER_SECOND, decode_game_time
from parse import (
    build_player_snapshot,
    load_dungeon_marker_state,
    load_game_time_ticks,
    load_guild_bases,
    load_guild_storage_counts,
    load_item_container_index,
    load_level_world_save_data,
    load_player_common_inventory,
    load_player_names_and_levels,
)
from remote import get_server_uptime_seconds, refresh_and_pull
from rcon import get_online_uids, get_server_version

# Everything under data/live/ is mutable, refresh-loop-written state (save
# cache + the derived players/bases/dungeons-state JSON) — kept in its own
# subdirectory, separate from the extractor's baked-in data/*_static.json,
# so a Docker deploy can mount only this path as a volume and never shadow
# static data shipped in a newer image (see docker-compose.yml).
LIVE_DIR = Path(__file__).resolve().parent.parent / "data" / "live"

# If set, PalDex and the Palworld server share a host — the SaveGames dir
# (Level.sav + Players/) is bind-mounted straight into this container at
# this path (read-only) instead of being pulled over SSH/rsync every cycle.
# See deploy/README.md's "Same-host deploy" section / docker-compose.local.yml.
LOCAL_SAVE_ROOT = os.environ.get("PALDEX_LOCAL_SAVE_ROOT")

SAVE_DIR = Path(LOCAL_SAVE_ROOT) if LOCAL_SAVE_ROOT else LIVE_DIR / "saves"
OUTPUT = LIVE_DIR / "players.json"
BASES_OUTPUT = LIVE_DIR / "bases.json"
DUNGEONS_STATE_OUTPUT = LIVE_DIR / "dungeons_state.json"
INVENTORIES_OUTPUT = LIVE_DIR / "inventories.json"


def run() -> list[dict]:
    if LOCAL_SAVE_ROOT:
        # Already mounted read-only at SAVE_DIR — nothing to pull.
        pass
    else:
        refresh_and_pull(SAVE_DIR)

    # Level.sav is large — parse it once and share the result rather than
    # re-reading it separately for player names and guild bases.
    world_save_data = load_level_world_save_data(SAVE_DIR / "Level.sav")
    names = load_player_names_and_levels(world_save_data)
    players = build_player_snapshot(SAVE_DIR, names)

    # Computed here (not just below for BASES_OUTPUT) so each player's own
    # guild name can be attached before the players.json payload is built.
    bases, guilds = load_guild_bases(world_save_data, names)
    guild_by_uid = {uid: g["name"] for g in guilds.values() for uid in g["player_uids"]}
    guild_id_by_uid = {uid: gid for gid, g in guilds.items() for uid in g["player_uids"]}
    for p in players:
        p["guild"] = guild_by_uid.get(p["uid"].lower())

    try:
        online_uids = get_online_uids()
    except Exception:
        # RCON is a nice-to-have on top of save-file data; don't let a
        # transient connection failure take down the whole refresh.
        traceback.print_exc()
        online_uids = None

    for p in players:
        p["online"] = p["uid"] in online_uids if online_uids is not None else None

    try:
        server_version = get_server_version()
    except Exception:
        traceback.print_exc()
        server_version = None

    server_start_time = None
    if not LOCAL_SAVE_ROOT:
        # Shells out over SSH, same as the save pull — nothing to query this
        # way in local-mount mode (no SSH access assumed at all).
        try:
            uptime_seconds = get_server_uptime_seconds()
            server_start_time = (datetime.now(timezone.utc) - timedelta(seconds=uptime_seconds)).isoformat()
        except Exception:
            # SSH to the AMP host for this is a nice-to-have same as RCON
            # above — a transient failure here shouldn't take down player
            # positions.
            traceback.print_exc()

    try:
        now_ticks = load_game_time_ticks(world_save_data)
        game_time = decode_game_time(now_ticks)
    except Exception:
        traceback.print_exc()
        now_ticks = None
        game_time = None

    payload = {
        "updated_at": datetime.now(timezone.utc).isoformat(),
        "online_known": online_uids is not None,
        "server_version": server_version,
        "server_start_time": server_start_time,
        "game_time": game_time,
        "players": players,
    }
    OUTPUT.write_text(json.dumps(payload, indent=2, default=str))

    for b in bases:
        b["map"], b["pixel_x"], b["pixel_y"] = locate(b["x"], b["y"])
    bases_payload = {
        "updated_at": datetime.now(timezone.utc).isoformat(),
        "bases": bases,
        "guilds": guilds,
    }
    BASES_OUTPUT.write_text(json.dumps(bases_payload, indent=2, default=str))

    try:
        # Backpack ("Common" container) + guild base storage item counts per
        # player, for "does this player already have N of this crafting
        # material" tooltips (Schematics) - "materials this player can
        # actually get to" means their own backpack plus whatever their
        # guild's shared base storage chests hold, not backpack alone.
        # container_index is a full scan of worldSaveData.ItemContainerSaveData
        # (23k+ entries across the whole world) - built once here and reused
        # for every player/guild rather than re-scanning per player, same
        # "parse Level.sav once, share the result" rule as world_save_data
        # itself above.
        container_index = load_item_container_index(world_save_data)
        guild_storage = load_guild_storage_counts(world_save_data, container_index)
        inventories = {}
        for player_sav in (SAVE_DIR / "Players").glob("*.sav"):
            if player_sav.stem.endswith("_dps"):
                continue
            # Inverse of server.py's _player_sav_path (uid.replace("-",
            # "").upper() + ".sav") - reinsert UUID dashes at the standard
            # 8-4-4-4-12 positions rather than re-deriving the real uid from
            # a fresh read of the save (load_player_common_inventory below
            # doesn't otherwise need to expose it).
            s = player_sav.stem
            uid = f"{s[0:8]}-{s[8:12]}-{s[12:16]}-{s[16:20]}-{s[20:32]}".lower()
            try:
                combined = dict(load_player_common_inventory(player_sav, container_index))
                for item_id, count in guild_storage.get(guild_id_by_uid.get(uid, ""), {}).items():
                    combined[item_id] = combined.get(item_id, 0) + count
                inventories[uid] = combined
            except Exception:
                traceback.print_exc()
    except Exception:
        traceback.print_exc()
        inventories = {}
    INVENTORIES_OUTPUT.write_text(json.dumps({"inventories": inventories}, indent=2, default=str))

    try:
        marker_state = load_dungeon_marker_state(world_save_data)
        # Convert raw GameDateTimeTicks (see gametime.py) to a plain seconds
        # ETA here, server-side, same as every other tick/coord conversion
        # in this project — the frontend never sees raw ticks.
        if now_ticks is not None:
            for entry in marker_state.values():
                if not entry["active"] and entry.get("next_respawn_ticks") is not None:
                    entry["respawn_in_seconds"] = max(
                        0, round((entry.pop("next_respawn_ticks") - now_ticks) / TICKS_PER_SECOND)
                    )
                elif entry["active"] and entry.get("disappear_ticks") is not None:
                    entry["disappear_in_seconds"] = max(
                        0, round((entry.pop("disappear_ticks") - now_ticks) / TICKS_PER_SECOND)
                    )
    except Exception:
        traceback.print_exc()
        marker_state = {}

    dungeons_state_payload = {
        "updated_at": datetime.now(timezone.utc).isoformat(),
        "markers": marker_state,
    }
    DUNGEONS_STATE_OUTPUT.write_text(json.dumps(dungeons_state_payload, indent=2, default=str))

    return players


if __name__ == "__main__":
    players = run()
    for p in players:
        status = "online" if p["online"] else ("offline" if p["online"] is not None else "?")
        print(f"{p['nickname']:<20} lvl {p['level']!s:<4} {status:<8} {p['map']} ({p['pixel_x']}, {p['pixel_y']})")
    print(f"\n{len(players)} players written to {OUTPUT}", file=sys.stderr)
