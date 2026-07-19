import json
import sys
import traceback
from datetime import datetime, timedelta, timezone
from pathlib import Path

from coord import locate
from gametime import decode_game_time
from parse import (
    build_player_snapshot,
    load_game_time_ticks,
    load_guild_bases,
    load_level_world_save_data,
    load_player_names_and_levels,
)
from remote import get_server_uptime_seconds, refresh_and_pull
from rcon import get_online_uids, get_server_version

SAVE_DIR = Path(__file__).resolve().parent.parent / "data" / "saves"
OUTPUT = Path(__file__).resolve().parent.parent / "data" / "players.json"
BASES_OUTPUT = Path(__file__).resolve().parent.parent / "data" / "bases.json"


def run() -> list[dict]:
    refresh_and_pull(SAVE_DIR)

    # Level.sav is large — parse it once and share the result rather than
    # re-reading it separately for player names and guild bases.
    world_save_data = load_level_world_save_data(SAVE_DIR / "Level.sav")
    names = load_player_names_and_levels(world_save_data)
    players = build_player_snapshot(SAVE_DIR, names)

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

    try:
        uptime_seconds = get_server_uptime_seconds()
        server_start_time = (datetime.now(timezone.utc) - timedelta(seconds=uptime_seconds)).isoformat()
    except Exception:
        # SSH to the AMP host for this is a nice-to-have same as RCON above —
        # a transient failure here shouldn't take down player positions.
        traceback.print_exc()
        server_start_time = None

    try:
        game_time = decode_game_time(load_game_time_ticks(world_save_data))
    except Exception:
        traceback.print_exc()
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

    bases, guilds = load_guild_bases(world_save_data, names)
    for b in bases:
        b["map"], b["pixel_x"], b["pixel_y"] = locate(b["x"], b["y"])
    bases_payload = {
        "updated_at": datetime.now(timezone.utc).isoformat(),
        "bases": bases,
        "guilds": guilds,
    }
    BASES_OUTPUT.write_text(json.dumps(bases_payload, indent=2, default=str))

    return players


if __name__ == "__main__":
    players = run()
    for p in players:
        status = "online" if p["online"] else ("offline" if p["online"] is not None else "?")
        print(f"{p['nickname']:<20} lvl {p['level']!s:<4} {status:<8} {p['map']} ({p['pixel_x']}, {p['pixel_y']})")
    print(f"\n{len(players)} players written to {OUTPUT}", file=sys.stderr)
