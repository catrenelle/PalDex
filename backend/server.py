import json
import threading
import time
import traceback
from pathlib import Path

from flask import Flask, jsonify, request, send_from_directory

import refresh
from bosses import load_bosses, load_bounties, load_oilrigs, load_towers
from dungeons import load_dungeons
from fasttravel import load_watchtowers, load_waypoints
from notes import load_notes
from npcs import load_npcs
from parse import (
    load_active_quests,
    load_collected_effigy_ids,
    load_collected_schematic_ids,
    load_completed_quests,
    load_defeated_boss_spawner_ids,
    load_defeated_tower_flags,
    load_read_note_ids,
    load_unlocked_fasttravel_ids,
)
from quests import load_quests
from relics import RELIC_META, load_relics
from schematics import load_schematics

REFRESH_INTERVAL_SECONDS = 30
FRONTEND_DIR = Path(__file__).resolve().parent.parent / "frontend"
PLAYERS_DIR = refresh.SAVE_DIR / "Players"

app = Flask(__name__, static_folder=None)

# Relics and bosses are baked into the game's own data tables, not the save
# file — load once, no need to refresh per-poll like player positions.
_relics_cache = load_relics()
_bosses_cache = load_bosses()
_bounties_cache = load_bounties()
_oilrigs_cache = load_oilrigs()
_dungeons_cache = load_dungeons()
_towers_cache = load_towers()
_watchtowers_cache = load_watchtowers()
_waypoints_cache = load_waypoints()
_notes_cache = load_notes()
_schematics_cache = load_schematics()
_quests_cache = load_quests()
_npcs_cache = load_npcs()


def _player_sav_path(uid: str) -> Path:
    # PlayerUId "387579c4-0000-..." -> save filename "387579C4000...00.sav"
    return PLAYERS_DIR / (uid.replace("-", "").upper() + ".sav")


@app.route("/")
def index():
    return send_from_directory(FRONTEND_DIR, "index.html")


@app.route("/assets/<path:filename>")
def assets(filename):
    return send_from_directory(FRONTEND_DIR / "assets", filename)


@app.route("/api/players")
def api_players():
    if not refresh.OUTPUT.exists():
        return jsonify({"updated_at": None, "players": []})
    return app.response_class(refresh.OUTPUT.read_text(), mimetype="application/json")


@app.route("/api/relics")
def api_relics():
    view_as = request.args.get("view_as", "").strip()
    collected_ids = None
    if view_as:
        sav_path = _player_sav_path(view_as)
        if sav_path.exists():
            try:
                collected_ids = load_collected_effigy_ids(sav_path)
            except Exception:
                traceback.print_exc()

    relics = _relics_cache
    if collected_ids is not None:
        relics = [{**r, "collected": r["id"] in collected_ids} for r in _relics_cache]

    return jsonify(
        {
            "types": RELIC_META,
            "relics": relics,
            "collection_known": collected_ids is not None,
        }
    )


@app.route("/api/bosses")
def api_bosses():
    view_as = request.args.get("view_as", "").strip()
    defeated_ids = None
    if view_as:
        sav_path = _player_sav_path(view_as)
        if sav_path.exists():
            try:
                defeated_ids = load_defeated_boss_spawner_ids(sav_path)
            except Exception:
                traceback.print_exc()

    bosses = _bosses_cache
    if defeated_ids is not None:
        bosses = [{**b, "defeated": b["spawner_id"] in defeated_ids} for b in _bosses_cache]

    return jsonify(
        {
            "bosses": bosses,
            "defeat_known": defeated_ids is not None,
        }
    )


@app.route("/api/bounties")
def api_bounties():
    view_as = request.args.get("view_as", "").strip()
    defeated_ids = None
    if view_as:
        sav_path = _player_sav_path(view_as)
        if sav_path.exists():
            try:
                defeated_ids = load_defeated_boss_spawner_ids(sav_path)
            except Exception:
                traceback.print_exc()

    targets = _bounties_cache
    if defeated_ids is not None:
        targets = [{**t, "defeated": t["spawner_id"] in defeated_ids} for t in _bounties_cache]

    return jsonify(
        {
            "targets": targets,
            "defeat_known": defeated_ids is not None,
        }
    )


@app.route("/api/towers")
def api_towers():
    view_as = request.args.get("view_as", "").strip()
    defeated_keys = None
    if view_as:
        sav_path = _player_sav_path(view_as)
        if sav_path.exists():
            try:
                defeated_keys = load_defeated_tower_flags(sav_path)
            except Exception:
                traceback.print_exc()

    towers = _towers_cache
    if defeated_keys is not None:
        towers = [{**t, "defeated": t["defeat_flag_key"] in defeated_keys} for t in _towers_cache]

    return jsonify(
        {
            "towers": towers,
            "defeat_known": defeated_keys is not None,
        }
    )


def _fasttravel_response(cache):
    view_as = request.args.get("view_as", "").strip()
    unlocked_ids = None
    if view_as:
        sav_path = _player_sav_path(view_as)
        if sav_path.exists():
            try:
                unlocked_ids = load_unlocked_fasttravel_ids(sav_path)
            except Exception:
                traceback.print_exc()

    points = cache
    if unlocked_ids is not None:
        points = [{**p, "collected": p["id"] in unlocked_ids} for p in cache]

    return jsonify({"points": points, "collection_known": unlocked_ids is not None})


@app.route("/api/watchtowers")
def api_watchtowers():
    return _fasttravel_response(_watchtowers_cache)


@app.route("/api/waypoints")
def api_waypoints():
    return _fasttravel_response(_waypoints_cache)


@app.route("/api/notes")
def api_notes():
    view_as = request.args.get("view_as", "").strip()
    read_ids = None
    if view_as:
        sav_path = _player_sav_path(view_as)
        if sav_path.exists():
            try:
                read_ids = load_read_note_ids(sav_path)
            except Exception:
                traceback.print_exc()

    notes = _notes_cache
    if read_ids is not None:
        notes = [{**n, "collected": n["id"] in read_ids} for n in _notes_cache]

    return jsonify(
        {
            "notes": notes,
            "collection_known": read_ids is not None,
        }
    )


@app.route("/api/schematics")
def api_schematics():
    view_as = request.args.get("view_as", "").strip()
    collected_ids = None
    if view_as:
        sav_path = _player_sav_path(view_as)
        if sav_path.exists():
            try:
                collected_ids = load_collected_schematic_ids(sav_path)
            except Exception:
                traceback.print_exc()

    schematics = _schematics_cache
    if collected_ids is not None:
        schematics = [{**s, "collected": s["id"] in collected_ids} for s in _schematics_cache]

    return jsonify(
        {
            "schematics": schematics,
            "collection_known": collected_ids is not None,
        }
    )


@app.route("/api/quests")
def api_quests():
    # Unlike every other section, Quests has no meaningful "all players"
    # view at all — a quest's active/not-started status is inherently
    # per-player, so with no view_as selected this deliberately returns an
    # empty list (player_known: False) rather than some blended/unioned
    # state across everyone. The frontend hides the Quests sections entirely
    # in that case.
    view_as = request.args.get("view_as", "").strip()
    if not view_as:
        return jsonify({"quests": [], "player_known": False})

    sav_path = _player_sav_path(view_as)
    if not sav_path.exists():
        return jsonify({"quests": [], "player_known": False})

    try:
        active = load_active_quests(sav_path)
        completed = load_completed_quests(sav_path)
    except Exception:
        traceback.print_exc()
        return jsonify({"quests": [], "player_known": False})

    entries = []
    for q in _quests_cache:
        steps = q["steps"]
        if q["id"] in active:
            step_index = active[q["id"]]
            status = "active"
        elif q["id"] not in completed:
            # Not yet touched at all - "incomplete quest target based on
            # starting NPC": step 0 is wherever you'd go to begin it. If
            # step 0 itself carries no location (e.g. Sub_Zoe02/04, whose
            # first block is a location-less "talk" trigger and the real
            # marker only appears once active), this quest just has no
            # not-started marker - consistent with "no map marker, don't
            # care about it" applied per-status, not just per-quest.
            step_index = 0
            status = "not_started"
        else:
            continue  # completed - no marker

        if step_index < 0 or step_index >= len(steps):
            continue
        locations = steps[step_index]
        if not locations:
            continue

        for loc in locations:
            entries.append(
                {
                    "id": f"{q['id']}:{step_index}:{loc['row_name']}",
                    "quest_id": q["id"],
                    "type": q["type"],
                    "status": status,
                    "title": q["title"],
                    "step_index": step_index,
                    "total_steps": len(steps),
                    "map": loc["map"],
                    "pixel_x": loc["pixel_x"],
                    "pixel_y": loc["pixel_y"],
                }
            )

    return jsonify({"quests": entries, "player_known": True})


@app.route("/api/bases")
def api_bases():
    if not refresh.BASES_OUTPUT.exists():
        return jsonify({"bases": [], "guild_known": False})

    payload = json.loads(refresh.BASES_OUTPUT.read_text())
    guilds = payload["guilds"]
    bases = [
        {
            "id": b["id"],
            "type_key": b["guild_id"],
            "name": b["guild_name"],
            "icon": "T_icon_compass_camp.png",
            "map": b["map"],
            "pixel_x": b["pixel_x"],
            "pixel_y": b["pixel_y"],
            "guild_id": b["guild_id"],
            "guild_leader": b.get("guild_leader"),
            "guild_member_count": b.get("guild_member_count"),
        }
        for b in payload["bases"]
    ]

    view_as = request.args.get("view_as", "").strip().lower()
    own_guild_id = None
    if view_as:
        for guild_id, guild in guilds.items():
            if view_as in guild["player_uids"]:
                own_guild_id = guild_id
                break

    if own_guild_id is not None:
        bases = [{**b, "own_guild": b["guild_id"] == own_guild_id} for b in bases]

    return jsonify(
        {
            "bases": bases,
            "guild_known": own_guild_id is not None,
        }
    )


@app.route("/api/oilrigs")
def api_oilrigs():
    # No per-player defeated state exists for these (world-shared "cleared"
    # state lives elsewhere — see bosses.load_oilrigs docstring).
    return jsonify({"zones": _oilrigs_cache, "defeat_known": False})


@app.route("/api/npcs")
def api_npcs():
    # No per-player state at all — an NPC isn't "collected" or "defeated",
    # so (unlike every other section) there's nothing to overlay per view_as.
    # Includes Wandering Merchant/Pal Dealer (categories "Wandering"/
    # "PalDealer") — folded in here rather than a separate Traders
    # endpoint, see npcs.py's own docstring.
    return jsonify({"npcs": _npcs_cache})


@app.route("/api/dungeons")
def api_dungeons():
    # Positions are static (_dungeons_cache), but active/inactive state is
    # live world-shared state re-read from Level.sav each refresh — see
    # dungeons.py docstring and refresh.load_dungeon_marker_state.
    marker_state = {}
    if refresh.DUNGEONS_STATE_OUTPUT.exists():
        try:
            marker_state = json.loads(refresh.DUNGEONS_STATE_OUTPUT.read_text())["markers"]
        except Exception:
            traceback.print_exc()

    entrances = [
        {**e, **marker_state.get(e["id"].replace("-", "").upper(), {"active": None})}
        for e in _dungeons_cache
    ]
    return jsonify({"entrances": entrances, "state_known": bool(marker_state)})


def _refresh_loop():
    while True:
        try:
            refresh.run()
        except Exception:
            traceback.print_exc()
        time.sleep(REFRESH_INTERVAL_SECONDS)


if __name__ == "__main__":
    threading.Thread(target=_refresh_loop, daemon=True).start()
    app.run(host="0.0.0.0", port=5151, debug=False)
