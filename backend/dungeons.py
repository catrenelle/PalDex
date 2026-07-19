"""Static Dungeon entrance *positions*, extracted once from the game's own
persistent level (Pal/Content/Pal/Maps/MainWorld_5/PL_MainWorld5) via
extractor/PalExtract — see data/dungeons_static.json.

Placed actor class BP_DungeonPortalMarker_<Biome>_C (157 instances across 11
biome variants) — the open-world "Dungeon" entrances (small loot/battle
instances scattered across the map), distinct from the 8 fixed Challenge
Towers (backend/bosses.py's load_towers()). No DataTable row/name reference
exists per instance, so there's no display name beyond the biome baked into
the class name, and there's still no per-player "cleared"/"unlocked" state.

There IS live world-shared active/inactive state, contrary to what was first
assumed when this feature shipped (2026-07-19) — worldSaveData.
DungeonPointMarkerSaveData/DungeonSaveData track exactly which markers
currently have a dungeon spawned, refreshed each poll like Guild Bases; see
parse.load_dungeon_marker_state and refresh.py's DUNGEONS_STATE_OUTPUT.
server.py's /api/dungeons merges that live state onto this static position
list by id (dashes-stripped/uppercase). Because there's still no per-item
*collectible* state to check off, the frontend keeps the "all or nothing"
single show/hide-all toggle rather than the per-item checklist used
elsewhere — but now filters the map to only the currently-active entrances
(instead of showing all 157 with an indicator), with an "active / total"
count in the section header.
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
