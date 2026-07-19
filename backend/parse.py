"""Extracts live player positions + names/levels from pulled save data."""

from pathlib import Path
from typing import Any

from palsav.core import decompress_sav_to_gvas
from palsav.gvas import GvasFile
from palsav.paltypes import PALWORLD_CUSTOM_PROPERTIES, PALWORLD_TYPE_HINTS

from coord import locate


def _read_gvas(path: Path) -> dict[str, Any]:
    raw = path.read_bytes()
    gvas, _ = decompress_sav_to_gvas(raw)
    g = GvasFile.read(gvas, PALWORLD_TYPE_HINTS, PALWORLD_CUSTOM_PROPERTIES)
    return g.dump()


def load_level_world_save_data(level_sav: Path) -> dict[str, Any]:
    """Single parse entrypoint for everything sourced from Level.sav's
    worldSaveData (player names/levels, guild bases) — this file is large
    (tens of MB decompressed), so callers needing more than one thing from
    it should parse once here and pass the result around rather than each
    calling _read_gvas separately."""
    d = _read_gvas(level_sav)
    return d["properties"]["worldSaveData"]["value"]


def load_game_time_ticks(world_save_data: dict[str, Any]) -> int:
    """worldSaveData.GameTimeSaveData.GameDateTimeTicks — the live in-game
    clock, in .NET-tick units (10,000,000 ticks/second). See
    backend/gametime.py for the decode."""
    return world_save_data["GameTimeSaveData"]["value"]["GameDateTimeTicks"]["value"]


def load_player_names_and_levels(world_save_data: dict[str, Any]) -> dict[str, dict[str, Any]]:
    """PlayerUId (lowercase str) -> {nickname, level}, from Level.sav's
    CharacterSaveParameterMap. Pals share the sentinel UID
    00000000-0000-0000-0000-000000000001 and are skipped by the caller,
    which only looks up UIDs it already knows are players."""
    entries = world_save_data["CharacterSaveParameterMap"]["value"]
    result = {}
    for entry in entries:
        player_uid = str(entry["key"]["PlayerUId"]["value"])
        save_param = entry["value"]["RawData"]["value"]["object"].get("SaveParameter", {}).get(
            "value", {}
        )
        nickname = save_param.get("NickName", {}).get("value")
        level = save_param.get("Level", {}).get("value")
        result[player_uid] = {
            "nickname": nickname,
            "level": level.get("value") if isinstance(level, dict) else None,
        }
    return result


def load_guild_bases(
    world_save_data: dict[str, Any], names: dict[str, dict[str, Any]]
) -> tuple[list[dict[str, Any]], dict[str, dict[str, Any]]]:
    """Guild-owned bases, read live from Level.sav each refresh (unlike
    Watchtowers/Waypoints/Bosses/etc., these are player-built and change at
    runtime, so there's no static extractor pipeline for them).

    - Guild membership + display name: GroupSaveDataMap, filtered to
      RawData.group_type == "EPalGroupType::Guild" (the map also holds
      unrelated "Organization" groups, e.g. per-Pal ownership groups — not
      guilds, skipped). Most guilds are left at the default "Unnamed Guild"
      name in practice (confirmed: every guild in a real save), so the
      display name falls back to "<admin nickname>'s Guild" in that case,
      using admin_player_uid + the CharacterSaveParameterMap nicknames
      already loaded for player markers — otherwise every unnamed guild's
      checklist row would read identically.
    - Base position: BaseCampSaveData, keyed by base GUID, each entry's own
      RawData.transform.translation (raw world units) and
      RawData.group_id_belong_to (-> the guild GUID above). RawData.name is
      *not* a real per-base name — every base in a real save carries the
      same untouched template string (e.g. "新規生成拠点テンプレート名0(仮)"),
      confirming players never rename individual bases; the game's own UI
      just shows the generic label "Base" for all of them (confirmed
      in-game), which is what's used here too — the guild name is what
      actually distinguishes markers.

    Returns (bases, guilds) where guilds is keyed by guild_id ->
    {name, player_uids, leader_name, member_count}, for the caller to
    resolve "which guild is this player in" without re-scanning bases, and
    to show as much guild context as we have on each base's tooltip.
    """
    guilds: dict[str, dict[str, Any]] = {}
    for entry in world_save_data["GroupSaveDataMap"]["value"]:
        raw = entry["value"]["RawData"]["value"]
        if raw.get("group_type") != "EPalGroupType::Guild":
            continue
        guild_id = str(entry["key"])
        guild_name = raw.get("guild_name") or ""
        player_uids = [str(p["player_uid"]).lower() for p in raw.get("players", [])]
        admin_uid = str(raw.get("admin_player_uid", "")).lower()
        # Prefer the live nickname from CharacterSaveParameterMap; fall back
        # to the (possibly stale, e.g. after a rename) name cached on the
        # guild's own player_info at the time they joined/were last synced.
        leader_name = names.get(admin_uid, {}).get("nickname")
        if not leader_name:
            for p in raw.get("players", []):
                if str(p["player_uid"]).lower() == admin_uid:
                    leader_name = p.get("player_info", {}).get("player_name")
                    break
        if not guild_name or guild_name == "Unnamed Guild":
            guild_name = f"{leader_name}'s Guild" if leader_name else "Unnamed Guild"
        guilds[guild_id] = {
            "name": guild_name,
            "player_uids": player_uids,
            "leader_name": leader_name,
            "member_count": len(player_uids),
        }

    bases = []
    for entry in world_save_data["BaseCampSaveData"]["value"]:
        raw = entry["value"]["RawData"]["value"]
        guild_id = str(raw.get("group_id_belong_to", ""))
        guild = guilds.get(guild_id, {})
        t = raw["transform"]["translation"]
        bases.append(
            {
                "id": str(entry["key"]),
                "guild_id": guild_id,
                "guild_name": guild.get("name", "Unknown Guild"),
                "guild_leader": guild.get("leader_name"),
                "guild_member_count": guild.get("member_count"),
                "x": t["x"],
                "y": t["y"],
                "z": t["z"],
            }
        )
    return bases, guilds


def load_player_position(player_sav: Path) -> dict[str, Any]:
    d = _read_gvas(player_sav)
    save_data = d["properties"]["SaveData"]["value"]
    player_uid = str(save_data["PlayerUId"]["value"])
    translation = save_data["LastTransform"]["value"]["Translation"]["value"]
    raw_x, raw_y, raw_z = translation["x"], translation["y"], translation["z"]
    map_name, pixel_x, pixel_y = locate(raw_x, raw_y)
    last_online = save_data.get("LastOnlineDateTime", {}).get("value")
    return {
        "uid": player_uid,
        "raw_x": raw_x,
        "raw_y": raw_y,
        "raw_z": raw_z,
        "map": map_name,
        "pixel_x": pixel_x,
        "pixel_y": pixel_y,
        "last_online": last_online,
    }


def load_collected_effigy_ids(player_sav: Path) -> set[str]:
    """Permanent per-instance "ever picked up" flags, keyed by the same
    LevelObjectInstanceId GUID (dashes stripped, uppercase) that
    extractor/PalExtract reads off each effigy actor — confirmed by exact
    match against a real player's save. Distinct from RelicPossessNumMap,
    which looks like the current unspent count before turning effigies in
    at a Statue of Power (resets on turn-in, unlike this flag)."""
    d = _read_gvas(player_sav)
    record_data = d["properties"]["SaveData"]["value"]["RecordData"]["value"]
    by_type = record_data.get("RelicObtainForInstanceFlagByType", {}).get("value", {}).get("values", [])
    collected = set()
    for entry in by_type:
        flags = entry.get("Flags", {}).get("value", [])
        for f in flags:
            if f.get("value"):
                collected.add(f["key"].upper())
    return collected


def load_defeated_boss_spawner_ids(player_sav: Path) -> set[str]:
    """Per-player "ever defeated" flags for field/raid bosses, keyed by the
    same SpawnerID string used in DT_BossSpawnerLoactionData (confirmed by
    exact match — e.g. Penking's spawner "81_1_grass_FBOSS_9" appears here
    too). Distinct from TowerBossDefeatFlag (dungeon tower bosses, keyed by
    "BOSS_BATTLE_NAME_*" instead) which this deliberately ignores."""
    d = _read_gvas(player_sav)
    record_data = d["properties"]["SaveData"]["value"]["RecordData"]["value"]
    entries = record_data.get("NormalBossDefeatFlag", {}).get("value", [])
    return {e["key"] for e in entries if e.get("value")}


def load_defeated_tower_flags(player_sav: Path) -> set[str]:
    """Per-player "ever defeated" flags for the 8 Challenge Towers, keyed by
    RecordData.TowerBossDefeatFlag (a NameProperty->BoolProperty map). Raw
    keys are "BOSS_BATTLE_NAME_<name>" (e.g. "BOSS_BATTLE_NAME_GrassBoss") —
    stripped to the bare name here to match towers_static.json's
    defeatFlagKey per region. Distinct from NormalBossDefeatFlag
    (open-world bosses)."""
    d = _read_gvas(player_sav)
    record_data = d["properties"]["SaveData"]["value"]["RecordData"]["value"]
    entries = record_data.get("TowerBossDefeatFlag", {}).get("value", [])
    prefix = "BOSS_BATTLE_NAME_"
    return {e["key"].removeprefix(prefix) for e in entries if e.get("value")}


def load_unlocked_fasttravel_ids(player_sav: Path) -> set[str]:
    """Permanent per-instance "ever unlocked" flags for Watchtowers and
    Waypoints, keyed by the same LevelObjectInstanceId GUID (dashes
    stripped, uppercase) extractor/PalExtract reads off each placed actor —
    confirmed by exact match against a real player's save (both a Watchtower
    and a Waypoint instance). Same scheme as load_collected_effigy_ids, just
    a flat NameProperty->BoolProperty map instead of a by-type nested one."""
    d = _read_gvas(player_sav)
    record_data = d["properties"]["SaveData"]["value"]["RecordData"]["value"]
    entries = record_data.get("FastTravelPointUnlockFlag", {}).get("value", [])
    return {e["key"].upper() for e in entries if e.get("value")}


def load_collected_schematic_ids(player_sav: Path) -> set[str]:
    """Permanent per-instance "ever picked up" flags for Schematics, keyed by
    the same LevelObjectInstanceId GUID (dashes stripped, uppercase) baked
    into each ItemPickupTower level actor — confirmed byte-for-byte against
    real players' saves, same scheme as load_collected_effigy_ids /
    load_unlocked_fasttravel_ids."""
    d = _read_gvas(player_sav)
    record_data = d["properties"]["SaveData"]["value"]["RecordData"]["value"]
    entries = record_data.get("ItemPickupObtainForInstanceFlag", {}).get("value", [])
    return {e["key"].upper() for e in entries if e.get("value")}


def load_read_note_ids(player_sav: Path) -> set[str]:
    """Permanent per-note "ever read" flags for Journals, keyed directly by
    the note's own row name (e.g. "Day1-1", "GrassBoss1") — unlike
    load_collected_effigy_ids/load_unlocked_fasttravel_ids, this is NOT a
    LevelObjectInstanceId GUID, just RecordData.NoteObtainForInstanceFlag's
    bare NameProperty key already matching notes_static.json's "id" field.
    Confirmed by exact key match against 13 real players' saves - every
    observed key matched a known note id, no unknown/stray keys."""
    d = _read_gvas(player_sav)
    record_data = d["properties"]["SaveData"]["value"]["RecordData"]["value"]
    entries = record_data.get("NoteObtainForInstanceFlag", {}).get("value", [])
    return {e["key"] for e in entries if e.get("value")}


def build_player_snapshot(save_dir: Path, names: dict[str, dict[str, Any]]) -> list[dict[str, Any]]:
    players = []
    for player_sav in sorted((save_dir / "Players").glob("*.sav")):
        if player_sav.stem.endswith("_dps"):
            continue
        pos = load_player_position(player_sav)
        meta = names.get(pos["uid"], {})
        players.append(
            {
                **pos,
                "nickname": meta.get("nickname") or pos["uid"][:8],
                "level": meta.get("level"),
            }
        )
    return players
