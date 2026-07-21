"""Static Quest data, extracted once from the game's own quest Blueprints —
see data/quests_static.json. Not part of any save file itself (positions are
baked DataTable/Blueprint data), so this cache never needs a per-poll refresh
— only the live per-player active/completed join in server.py does.

Every quest (DT_PalQuestData) is an ordered sequence of "blocks" (steps).
Each step optionally carries one or more real map locations, resolved at
extraction time via each block's own LocationSettingData.FixedLocationPointArray
-> DT_PalQuestLocationData row (a genuine foreign key, not a naming-convention
guess — see extractor/PalExtract/Program.cs). Only quests where at least one
step has a location made it into quests_static.json in the first place — a
quest with no map marker anywhere isn't tracked at all, per an explicit
scoping call (Hidden-type quests, and most tutorial Main quests, are dropped
this way).

Quests are NOT 1:1 with NPCs: some (e.g. Zoe, "Sub_Zoe01".."Sub_Zoe04" +
"Sub_Zoe_Halloween") are a 5-quest chain at one spot; others that look like a
chain by naming (e.g. "Sub_Farmer01".."Sub_Farmer04") are actually four
different NPCs at four different locations, one quest each. The per-step
location join above sidesteps needing to tell these apart — each quest's own
steps carry their own locations regardless.
"""

import json
from pathlib import Path
from typing import Any

from coord import locate

QUESTS_PATH = Path(__file__).resolve().parent.parent / "data" / "quests_static.json"


def load_quests() -> list[dict[str, Any]]:
    """Static quest defs with map/pixel coords resolved per step-location.
    "steps" is a list (index = BlockIndex, matching
    SaveData.OrderedQuestArray_FullRelease's own BlockIndex) of location
    lists — usually 0 or 1 location, occasionally more (e.g. Zoe's first
    block has 2)."""
    quests = []
    for q in json.loads(QUESTS_PATH.read_text(encoding="utf-8")):
        steps = []
        for step in q["steps"]:
            locations = []
            for loc in step["locations"]:
                map_name, pixel_x, pixel_y = locate(loc["x"], loc["y"])
                locations.append(
                    {
                        "row_name": loc["rowName"],
                        "map": map_name,
                        "pixel_x": pixel_x,
                        "pixel_y": pixel_y,
                    }
                )
            steps.append(locations)
        quests.append(
            {
                "id": q["id"],
                "type": q["type"],  # "Main" | "Sub"
                "title": q["title"],
                "steps": steps,
            }
        )
    return quests
