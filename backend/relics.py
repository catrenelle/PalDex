"""Static Effigy position data, extracted once from the game's own World
Partition level data via extractor/PalExtract (see data/relics_static.json)
— these are baked level actors, not part of any save file, so this list
doesn't need refreshing per-poll like players do. Internally these are
Palworld's "Relic" map objects (Blueprint class BP_LevelObject_Relic*); the
game calls them Effigies in the UI, so that's what we show the user.

Per-player "collected" state: each player's own save has a permanent
per-instance flag, RecordData.RelicObtainForInstanceFlagByType, keyed by the
same LevelObjectInstanceId GUID (dashes stripped, uppercase) baked into the
level actor — see parse.load_collected_effigy_ids(). Confirmed by exact key
match plus a real player's reported collection count. (RelicPossessNumMap,
found earlier, is a red herring for this purpose — it looks like the
current *unspent* count before turning effigies in at a Statue of Power,
which resets on turn-in unlike the permanent flag.)
"""

import json
from pathlib import Path
from typing import Any

from coord import locate

RELICS_PATH = Path(__file__).resolve().parent.parent / "data" / "relics_static.json"

# Blueprint suffix -> display metadata. `label` and `icon` both now come
# from the same authoritative join used for Schematics — each Pal's actual
# "<Name> Effigy" item row in DT_ItemDataTable (e.g. "Relic_11" -> "Yakumo
# Effigy") resolved through its own IconName to DT_ItemIconDataTable, not a
# by-eye guess. `effect` is EPalRelicType from each Blueprint's class
# defaults.
#
# The earlier best-effort icon guess had 5 of 12 wrong (confirmed 2026-07-19
# against a real in-game effigy-effects list the user reported as
# mismatched): GuardianDog/Yakumo (_10 -> _11), LazyDragon/Relaxaurus (_09 ->
# _10), LeafMomonga/Herbil and Monkey/Tanzee (_06/_05 swapped), and
# Mutant/Lunaris (_12 -> _09) — _12 is actually Mimog's icon, not Lunaris's.
#
# There are 13 Relic item rows in DT_ItemDataTable (Relic, Relic_01..12) but
# only 12 BP_LevelObject_Relic* Blueprints placed anywhere in the game's own
# level data (confirmed: full asset-path sweep for "Relic" turns up all 12
# by name, nothing for a 13th) — Relic_12 "Mimog Effigy" has real item/name/
# icon data but no placed spawn in the current game version, so it's
# unobtainable in the open world and deliberately absent from this map.
RELIC_META: dict[str, dict[str, Any]] = {
    "BP_LevelObject_Relic": {"label": "Lifmunk", "icon": "T_itemicon_Relic.png", "effect": "CapturePower"},
    "BP_LevelObject_Relic_FlameBambi": {"label": "Rooby", "icon": "T_itemicon_Relic_04.png", "effect": "JumpPower"},
    "BP_LevelObject_Relic_GuardianDog": {"label": "Yakumo", "icon": "T_itemicon_Relic_11.png", "effect": "RainbowPassiveRate"},
    "BP_LevelObject_Relic_IceCrocodile": {"label": "Munchill", "icon": "T_itemicon_Relic_03.png", "effect": "FoodDecayReduction"},
    "BP_LevelObject_Relic_LazyDragon": {"label": "Relaxaurus", "icon": "T_itemicon_Relic_10.png", "effect": "ExpBonus"},
    "BP_LevelObject_Relic_LeafMomonga": {"label": "Herbil", "icon": "T_itemicon_Relic_05.png", "effect": "GliderSpeed"},
    "BP_LevelObject_Relic_Monkey": {"label": "Tanzee", "icon": "T_itemicon_Relic_06.png", "effect": "ClimbSpeed"},
    "BP_LevelObject_Relic_Mutant": {"label": "Lunaris", "icon": "T_itemicon_Relic_09.png", "effect": "SphereHoming"},
    "BP_LevelObject_Relic_NegativeKoala": {"label": "Depresso", "icon": "T_itemicon_Relic_07.png", "effect": "StatusAilmentResist"},
    "BP_LevelObject_Relic_Penguin": {"label": "Pengullet", "icon": "T_itemicon_Relic_02.png", "effect": "SwimSpeed"},
    "BP_LevelObject_Relic_PinkCat": {"label": "Cattiva", "icon": "T_itemicon_Relic_08.png", "effect": "StaminaReduction"},
    "BP_LevelObject_Relic_SheepBall": {"label": "Lamball", "icon": "T_itemicon_Relic_01.png", "effect": "HungerReduction"},
}


def _type_key(blueprint_class: str) -> str:
    return blueprint_class[:-2] if blueprint_class.endswith("_C") else blueprint_class


def _normalize_id(instance_id: str) -> str:
    return instance_id.replace("-", "").upper()


def load_relics() -> list[dict[str, Any]]:
    raw = json.loads(RELICS_PATH.read_text())
    relics = []
    for r in raw:
        type_key = _type_key(r["relic"])
        meta = RELIC_META.get(type_key, {"label": type_key, "icon": "T_itemicon_Relic.png", "effect": "Unknown"})
        map_name, pixel_x, pixel_y = locate(r["x"], r["y"])
        raw_id = r.get("instanceId")
        relics.append(
            {
                "id": _normalize_id(raw_id) if raw_id else f"{r['cell']}:{r['x']}:{r['y']}",
                "type": type_key,
                "label": meta["label"],
                "icon": meta["icon"],
                "effect": meta["effect"],
                "map": map_name,
                "pixel_x": pixel_x,
                "pixel_y": pixel_y,
            }
        )
    return relics
