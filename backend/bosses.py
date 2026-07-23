"""Static field/raid-boss spawn data, extracted once from the game's own
Pal/Content/Pal/DataTable/UI/DT_BossSpawnerLoactionData.uasset (see
data/bosses_static.json) — a baked DataTable of every boss encounter in the
game, not part of any save file, so no per-poll refresh needed. Name +
element typing (Pals) / name + portrait icon (humans) joined in at
extraction time — see extractor/PalExtract/Program.cs.

The table's own 159 raw rows contain exact-duplicate rows for every human
boss (confirmed 2026-07-23: all 66 raw human rows collapse to 33 distinct
encounters, byte-identical SpawnerID/Location/Level under two different row
names — a real authoring artifact in the game's own table, not an
extraction bug) — extractor/PalExtract/Program.cs dedupes by full field
equality (not just SpawnerID) before writing data/bosses_static.json, since
at least one Pal boss (remainsIsland_1_GrassGolem_FBOSS/Dualith) legitimately
reuses one SpawnerID for two different physical spawn points. 126 rows ship
today. They split into three groups:
- **Pal bosses** ("category": "pal") — real field bosses like Penking.
  Name + elements resolved via DT_PalMonsterParameter + DT_PalNameText_Common.
- **Human "boss" NPCs** ("category": "human") — named bandit/raider/syndicate
  leaders (e.g. "Scoot"), shown in-game as **Bounty** targets with their own
  dedicated portrait icon (DT_PalBossNPCIcon, 33 unique icons exported to
  frontend/assets/boss_icons/). CharacterID is genuinely "None" for these —
  they don't exist in the Pal-only DT_PalMonsterParameter table, so they
  have no elemental typing/weaknesses. Name resolved via
  DT_PalHumanParameter + DT_HumanNameText_Common.
- **Oilrig raid zones** ("category": "oilrig", 3 rows: "REGION_Oilrig_1/2/3")
  — not a boss at all, a raid zone (visible on the in-game compass with its
  own icon, T_icon_compass_Oilrig). Named via DT_WorldMap_Common_Text_Common
  (the map's region-label table), keyed directly by SpawnerID, e.g.
  "Rayne Syndicate Oil Rig". No per-player defeated state exists for these —
  confirmed NormalBossDefeatFlag never contains an Oilrig key for a real
  player. The actual "cleared" state lives in Level.sav's
  worldSaveData.OilrigSaveData.OilrigMap (keyed by EPalOilrigType::TypeA/B/C,
  a "Clear" bool) but that's *world-shared*, not per-player, and the
  SpawnerID -> EPalOilrigType mapping hasn't been confirmed — not
  implemented, flagged as a possible follow-up.

Per-player "defeated" state: RecordData.NormalBossDefeatFlag, keyed by the
same SpawnerID string baked into this table — see
parse.load_defeated_boss_spawner_ids(). Confirmed by exact key match (e.g.
Penking's "81_1_grass_FBOSS_9", and human bosses like "BOSS_Male_Soldier02"
use the same flag map).

Element type effectiveness itself isn't stored as game data (only per-Pal
typing is) — it's baked into Blueprint UI logic (WBP_MainMenu_Pal_ElementMatchup)
that CUE4Parse can't easily decompile. ELEMENT_STRONG_AGAINST below is the
game's own internal element names (Earth/Leaf/Electricity/Normal, not the
wiki-common Ground/Grass/Electric/Neutral) cross-checked against two
independent wikis (game8.co, dexerto.com) and sanity-checked against
Penking's (Water/Ice) real-world known weakness to Fire/Electric.
"""

import json
from pathlib import Path
from typing import Any

from coord import locate

BOSSES_PATH = Path(__file__).resolve().parent.parent / "data" / "bosses_static.json"
TOWERS_PATH = Path(__file__).resolve().parent.parent / "data" / "towers_static.json"

# Game-internal element name -> what strong-against-it deals 150%+ damage.
ELEMENT_STRONG_AGAINST: dict[str, list[str]] = {
    "Fire": ["Leaf", "Ice"],
    "Leaf": ["Earth"],
    "Earth": ["Electricity"],
    "Electricity": ["Water"],
    "Water": ["Fire"],
    "Ice": ["Dragon"],
    "Dragon": ["Dark"],
    "Dark": ["Normal"],
    "Normal": [],
}
ELEMENT_DISPLAY_NAME: dict[str, str] = {
    "Earth": "Ground",
    "Leaf": "Grass",
    "Electricity": "Electric",
    "Normal": "Neutral",
    "Fire": "Fire",
    "Water": "Water",
    "Ice": "Ice",
    "Dark": "Dark",
    "Dragon": "Dragon",
}

_WEAK_TO: dict[str, list[str]] = {el: [] for el in ELEMENT_STRONG_AGAINST}
for _attacker, _defenders in ELEMENT_STRONG_AGAINST.items():
    for _defender in _defenders:
        _WEAK_TO[_defender].append(_attacker)


def _weaknesses(element1: str | None, element2: str | None) -> list[str]:
    weak = []
    for el in (element1, element2):
        if not el:
            continue
        for attacker in _WEAK_TO.get(el, []):
            if attacker not in weak:
                weak.append(attacker)
    return [ELEMENT_DISPLAY_NAME.get(el, el) for el in weak]


def _load_raw() -> list[dict[str, Any]]:
    return json.loads(BOSSES_PATH.read_text(encoding="utf-8"))


def load_bosses() -> list[dict[str, Any]]:
    """Pal field bosses only (e.g. Penking) — elemental typing + weaknesses."""
    bosses = []
    for r in _load_raw():
        if r["category"] != "pal":
            continue
        elements = [e for e in (r["element1"], r["element2"]) if e]
        map_name, pixel_x, pixel_y = locate(r["x"], r["y"])
        bosses.append(
            {
                "id": f"{r['spawnerId']}:{r['x']}:{r['y']}",
                "spawner_id": r["spawnerId"],
                "type_key": r["characterId"],
                "name": r["name"],
                "level": r["level"],
                "icon": r["icon"],
                "elements": [ELEMENT_DISPLAY_NAME.get(e, e) for e in elements],
                "weaknesses": _weaknesses(r["element1"], r["element2"]),
                "map": map_name,
                "pixel_x": pixel_x,
                "pixel_y": pixel_y,
            }
        )
    return bosses


def load_oilrigs() -> list[dict[str, Any]]:
    """Oilrig raid zones (not bosses) — no per-player defeated state exists."""
    zones = []
    for r in _load_raw():
        if r["category"] != "oilrig":
            continue
        map_name, pixel_x, pixel_y = locate(r["x"], r["y"])
        zones.append(
            {
                "id": f"{r['spawnerId']}:{r['x']}:{r['y']}",
                "spawner_id": r["spawnerId"],
                "type_key": r["spawnerId"],
                "name": r["name"],
                "level": r["level"],
                "icon": r["icon"],
                "map": map_name,
                "pixel_x": pixel_x,
                "pixel_y": pixel_y,
            }
        )
    return zones


def load_towers() -> list[dict[str, Any]]:
    """The 8 Challenge Towers (internally "GYM" bosses) — a queue-then-enter
    battle instance, distinct from open-world DT_BossSpawnerLoactionData
    bosses, so they live in their own extracted file (towers_static.json).

    Position: no placed entrance actor exists for these in the game's own
    data (extensively searched and ruled out - see NOTES.md). Instead uses
    real in-game HUD coordinates cross-checked against palworld.fandom.com's
    Tower page, converted via the same formula used elsewhere in this
    pipeline. `level` (Normal/Hard) is likewise from that same cross-checked
    source, not data-mined — no Level field exists for these anywhere in the
    game's own tables.

    Per-player "defeated" state: RecordData.TowerBossDefeatFlag, keyed by
    each row's own defeatFlagKey — see parse.load_defeated_tower_flags().
    """
    towers = []
    for r in json.loads(TOWERS_PATH.read_text(encoding="utf-8")):
        elements = [e for e in (r["element1"], r["element2"]) if e]
        has_position = r["x"] is not None
        map_name = pixel_x = pixel_y = None
        if has_position:
            map_name, pixel_x, pixel_y = locate(r["x"], r["y"])
        towers.append(
            {
                "id": r["regionKey"],
                "type_key": r["regionKey"],
                "region_key": r["regionKey"],
                "defeat_flag_key": r["defeatFlagKey"],
                "name": r["name"],
                "boss_name": r["bossName"],
                "level": r["levelNormal"],
                "level_hard": r["levelHard"],
                "elements": [ELEMENT_DISPLAY_NAME.get(e, e) for e in elements],
                "weaknesses": _weaknesses(r["element1"], r["element2"]),
                "icon": r["icon"],
                "position_exact": r["positionExact"],
                "has_position": has_position,
                "map": map_name,
                "pixel_x": pixel_x,
                "pixel_y": pixel_y,
            }
        )
    return towers


def load_bounties() -> list[dict[str, Any]]:
    """Named human 'boss' NPCs (bandit/raider/syndicate leaders, e.g.
    "Scoot"), shown in-game as Bounty targets — no elemental typing, but
    each has its own portrait icon. type_key groups the (usually 2, e.g.
    Scoot at 2 locations) spawn points sharing one name under one entry."""
    targets = []
    for r in _load_raw():
        if r["category"] != "human":
            continue
        map_name, pixel_x, pixel_y = locate(r["x"], r["y"])
        targets.append(
            {
                "id": f"{r['spawnerId']}:{r['x']}:{r['y']}",
                "spawner_id": r["spawnerId"],
                "type_key": r["spawnerId"],
                "name": r["name"],
                "level": r["level"],
                "icon": r["icon"],
                "map": map_name,
                "pixel_x": pixel_x,
                "pixel_y": pixel_y,
            }
        )
    return targets
