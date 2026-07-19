"""Static Dungeon entrance data, extracted once from the game's own
persistent level (Pal/Content/Pal/Maps/MainWorld_5/PL_MainWorld5) via
extractor/PalExtract — see data/dungeons_static.json. Not part of any save
file, so no per-poll refresh needed, same as relics/bosses/watchtowers.

Placed actor class BP_DungeonPortalMarker_<Biome>_C (157 instances across 11
biome variants) — the open-world "Dungeon" entrances (small loot/battle
instances scattered across the map), distinct from the 8 fixed Challenge
Towers (backend/bosses.py's load_towers()). No DataTable row/name reference
exists per instance, so there's no display name beyond the biome baked into
the class name, and no per-player "cleared"/"unlocked" state either — the
in-game activation timing is governed by a per-instance RespawnProbability
spawn-table override (not a player-visible flag), matching the "unclear when
they activate" behavior noted when this feature was requested. Because
there's nothing to check off, the frontend shows these as one shared cave
icon with a single show/hide-all toggle rather than the per-item checklist
used by every other section.
"""

import json
from pathlib import Path
from typing import Any

from coord import locate

DUNGEONS_PATH = Path(__file__).resolve().parent.parent / "data" / "dungeons_static.json"


def load_dungeons() -> list[dict[str, Any]]:
    entrances = []
    for r in json.loads(DUNGEONS_PATH.read_text(encoding="utf-8")):
        map_name, pixel_x, pixel_y = locate(r["x"], r["y"])
        entrances.append(
            {
                "id": r["instanceId"],
                "biome": r["biome"],
                "icon": r["icon"],
                "map": map_name,
                "pixel_x": pixel_x,
                "pixel_y": pixel_y,
            }
        )
    return entrances
