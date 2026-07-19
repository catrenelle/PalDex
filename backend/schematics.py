"""Static Schematic position data, extracted once from the game's own
placed level actors via extractor/PalExtract — see data/schematics_static.json.
Not part of any save file, so no per-poll refresh needed, same as
relics/bosses/towers/notes.

Internally these are "ItemPickupTower" actors (mesh SM_AncientShrine) — a
one-time interactable terminal, scattered across the map with no compass/map
icon in-game, that grants a fixed weapon/armor/accessory Schematic (or
occasionally a different one-off consumable) plus a Dog Coin bonus. 106
total: 2 placed in the persistent level, 104 streamed under World Partition
— exactly matching DT_ItemPickupDataTable's 107 rows minus one unused test
row. See Program.cs for the full extraction trail.

Per-player "ever picked up" state: each player's own save has a permanent
per-instance flag, RecordData.ItemPickupObtainForInstanceFlag, keyed by the
same LevelObjectInstanceId GUID (dashes stripped, uppercase) baked into the
level actor — identical scheme to Effigies/Watchtowers/Waypoints. See
parse.load_collected_schematic_ids().
"""

import json
from pathlib import Path
from typing import Any

from coord import locate

SCHEMATICS_PATH = Path(__file__).resolve().parent.parent / "data" / "schematics_static.json"


def _normalize_id(instance_id: str) -> str:
    return instance_id.replace("-", "").upper()


def load_schematics() -> list[dict[str, Any]]:
    schematics = []
    for r in json.loads(SCHEMATICS_PATH.read_text(encoding="utf-8")):
        map_name, pixel_x, pixel_y = locate(r["x"], r["y"])
        schematics.append(
            {
                "id": _normalize_id(r["instanceId"]),
                "type_key": r["id"],
                "name": r["name"],
                "icon": r["icon"],
                "preview": r["bonus"],
                "map": map_name,
                "pixel_x": pixel_x,
                "pixel_y": pixel_y,
            }
        )
    return schematics
