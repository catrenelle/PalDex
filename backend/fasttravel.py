"""Static Watchtower + Waypoint position data, extracted once from the
game's own persistent level (Pal/Content/Pal/Maps/MainWorld_5/PL_MainWorld5)
via extractor/PalExtract — see data/watchtowers_static.json and
data/waypoints_static.json. Not part of any save file, so no per-poll
refresh needed, same as relics/bosses.

Two distinct placed-actor Blueprint classes (see Program.cs for the full
trail — these live in the persistent level, not the streamed World
Partition grid the effigies use):
- BP_LevelObject_UnlockMapPoint_C (22) — Watchtowers, the tall climbable
  towers, shown to the user as "Watchtowers".
- BP_LevelObject_TowerFastTravelPoint_C (152) — every other fast-travel
  point (generic landmarks, Sealed Realm dungeon entrances, DLC-region
  ones), shown to the user as "Waypoints".

Per-player "unlocked" state: each player's own save has a permanent
per-instance flag, RecordData.FastTravelPointUnlockFlag, keyed by the same
LevelObjectInstanceId GUID (dashes stripped, uppercase) baked into the
level actor — see parse.load_unlocked_fasttravel_ids(). Confirmed by exact
key match against a real player's save for both a Watchtower and a Waypoint.

On the game's own map, these are greyed out until unlocked and full color
after — unlike bosses/towers (which get a checkmark badge once defeated),
so the frontend renders these with a grayscale filter instead.
"""

import json
from pathlib import Path
from typing import Any

from coord import locate

WATCHTOWERS_PATH = Path(__file__).resolve().parent.parent / "data" / "watchtowers_static.json"
WAYPOINTS_PATH = Path(__file__).resolve().parent.parent / "data" / "waypoints_static.json"


def _normalize_id(instance_id: str) -> str:
    return instance_id.replace("-", "").upper()


def _load(path: Path) -> list[dict[str, Any]]:
    points = []
    for r in json.loads(path.read_text(encoding="utf-8")):
        map_name, pixel_x, pixel_y = locate(r["x"], r["y"])
        points.append(
            {
                "id": _normalize_id(r["instanceId"]),
                "type_key": r["pointId"],
                "point_id": r["pointId"],
                "name": r["name"],
                "icon": r["icon"],
                "map": map_name,
                "pixel_x": pixel_x,
                "pixel_y": pixel_y,
            }
        )
    return points


def load_watchtowers() -> list[dict[str, Any]]:
    return _load(WATCHTOWERS_PATH)


def load_waypoints() -> list[dict[str, Any]]:
    return _load(WAYPOINTS_PATH)
