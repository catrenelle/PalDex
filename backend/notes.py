"""Static Journal (internally "Note") position data, extracted once from the
game's own placed level actors via extractor/PalExtract — see
data/notes_static.json. Not part of any save file, so no per-poll refresh
needed, same as relics/bosses/towers.

64 total: 23 "Castaway's Journal" story entries scattered on the main
island, plus 41 NPC "Diary" entries tied to one of the 9 Tower regions,
found inside/near that region's Tower dungeon. See Program.cs for the full
extraction trail (position comes from both the persistent level and a full
World Partition scan, split the same way Watchtowers/Effigies are).

Per-player "read" state: each player's own save has a flat per-note flag,
RecordData.NoteObtainForInstanceFlag, keyed directly by the note's own row
name (e.g. "Day1-1") — unlike Effigies/Watchtowers this is NOT a
LevelObjectInstanceId GUID, just the bare id already baked into
notes_static.json. Confirmed by exact key match across 13 real players'
saves (every observed key matched a known note id, no surprises). See
parse.load_read_note_ids().

Marker icon: notes_static.json's own per-note "icon" (the extracted in-world
"photo" texture, see Program.cs) is deliberately NOT used for the map
marker/checklist — those are full-screen note-reading background art (up to
~3860x2180 at the source), inconsistent aspect ratios, and don't read as a
recognizable icon at 30px. No small purpose-made "note/page" icon exists
anywhere in the game's own assets either (checked exhaustively). Every note
uses one shared hand-drawn generic page icon instead
(frontend/assets/note_icons/_page_icon.svg) - simple, uniform, and legible
at marker size, same "one icon per section" precedent as Oil Rigs/Watchtower/
Waypoint compass icons.
"""

import json
from pathlib import Path
from typing import Any

from coord import locate

NOTES_PATH = Path(__file__).resolve().parent.parent / "data" / "notes_static.json"
ICON_FILE = "_page_icon.svg"


def load_notes() -> list[dict[str, Any]]:
    notes = []
    for r in json.loads(NOTES_PATH.read_text(encoding="utf-8")):
        map_name, pixel_x, pixel_y = locate(r["x"], r["y"])
        notes.append(
            {
                "id": r["id"],
                "type_key": r["groupKey"],
                "name": r["groupName"],
                "boss_name": r["title"],
                "preview": r["preview"],
                "icon": ICON_FILE,
                "map": map_name,
                "pixel_x": pixel_x,
                "pixel_y": pixel_y,
            }
        )
    return notes
