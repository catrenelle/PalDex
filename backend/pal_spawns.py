"""Static wild-Pal *spawn location* data - "spawn locations" as translucent
highlighted regions on the map rather than a color-gradient heatmap, per
explicit user request. Strictly regular wild-Pal field spawns: NOT alpha/
field bosses (backend/bosses.py's load_bosses()), NOT dungeon trash/boss
spawns (backend/dungeons.py's load_dungeon_contents()), NOT human NPC
patrols. See extractor/PalExtract/Program.cs's "Pal Spawn Locations" section
and NOTES.md for the full extraction/filtering writeup (DT_PalSpawnerPlacement
SpawnerType::Common + PlacementType::Field, joined against DT_PalWildSpawner
by that table's own SpawnerName property).

Fully static/world-shared data (spawn definitions aren't live save state, no
per-player state at all) - loaded once at startup like bosses/towers, no
per-refresh reload needed. Unlike every other section in this app, the
frontend defaults this to nothing shown - see frontend/index.html's Pal
Spawns section.
"""

import json
from pathlib import Path
from typing import Any

from coord import locate, radius_to_pixels

PAL_SPAWN_LOCATIONS_PATH = (
    Path(__file__).resolve().parent.parent / "data" / "pal_spawn_locations_static.json"
)


def load_pal_spawn_locations() -> dict[str, Any]:
    """CharacterID -> {name, icon, locationCount, locations: [{pixel_x,
    pixel_y, radius_px, level_min, level_max}, ...]} - pixel/radius
    conversion happens here (not baked into the static JSON) so it stays in
    sync with backend/coord.py if the map bounds/texture size ever change,
    same pattern as every other loader in this pipeline."""
    raw = json.loads(PAL_SPAWN_LOCATIONS_PATH.read_text(encoding="utf-8"))
    result: dict[str, Any] = {}
    for character_id, species in raw.items():
        locations = []
        for loc in species["locations"]:
            _map_name, pixel_x, pixel_y = locate(loc["x"], loc["y"])
            locations.append(
                {
                    "pixel_x": pixel_x,
                    "pixel_y": pixel_y,
                    "radius_px": radius_to_pixels(loc["radius"]),
                    "level_min": loc.get("levelMin"),
                    "level_max": loc.get("levelMax"),
                }
            )
        result[character_id] = {
            "name": species["name"],
            "icon": species.get("icon"),
            "location_count": len(locations),
            "locations": locations,
        }
    return result
