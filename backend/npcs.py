"""Static NPC position data (Black Market / Dog Coin / Wandering Merchant /
Pal Dealer / General) — extracted once, see data/npcs_static.json. Not part
of any save file, so no per-poll refresh — unlike every collectible/
defeatable section, there's no per-player state here at all (an NPC isn't
"collected" or "defeated").

Two different position sources feed this one file, both merged into the
same flat npcs_static.json list by the extractor:

- Black Market / Dog Coin / General come from BP_MonoNPCSpawner-family
  actors (persistent level + streamed World Partition), each carrying a
  real Properties.UniqueName.Key foreign key into DT_UniqueNPC (e.g.
  "DarkTrader", "MedalTrader") — not a naming-convention guess. See
  extractor/PalExtract/Program.cs's NPCs section for the full
  investigation, including what's deliberately excluded (quest-conditional
  spawners already covered by the Quests feature, human Bounty-boss
  spawners already covered by bosses.py) and what's genuinely missing
  (Bobby/Johnson/InnkeeperA/Doctor/MerchantwithPAL/DarkTrader02/04 — no
  spawner found anywhere in this scan, category coverage is real but not
  100% of DT_UniqueNPC's 216 rows).
- Wandering Merchant / Pal Dealer (categories "Wandering"/"PalDealer") are
  NOT from that spawner scan — neither BP_NPC_SalesPerson*/PalDealer*
  resolved to a spawner position via this project's own extraction (they
  wild-spawn once per server boot via procedural Blueprint logic, invisible
  to this pipeline — confirmed dead end, see NOTES.md). Positions for these
  two instead come from an external source, palpedia.ru's live map data,
  merged into this same file/module rather than a separate one (an earlier
  "Traders" section briefly existed as its own endpoint/data file before
  being folded back in here — don't recreate that split).

Shop data (2026-07-20), for the in-game-style "what do they have for sale"
modal — real for MedalTrader/BountyTrader/ArenaShop/Wandering Merchant
(items) and PalDealer/DarkTrader/DarkTrader03 (pal_pool), null for every
other NPC:

- "items": a real, complete, always-available inventory (confirmed
  single-entry 100%-weight lottery, not a random draw) for every item-
  selling category. Each entry has itemId/name/icon/price — price is
  OverridePrice if the shop sets one, else the item's own base Price
  (DT_ItemDataTable), in "currency" (a "currency"/"currency_icon" pair on
  the NPC itself, defaulting to Gold/"Money" when no
  DT_ItemShopSettingData override exists).
- "pal_pool": NOT a real-time inventory — DT_PalShopCreateData's own
  offeredCount field confirms only a random subset of
  pal_pool.poolSpecies is actually offered at once, rotating on restock.
  Each species has a name + real portrait icon, but deliberately no price:
  a Pal's real in-game price depends on its randomly-rolled level/stats,
  which this static extraction has no way to know (per an explicit user
  call - don't compute or display a fabricated one). Black Market
  (DarkTrader/DarkTrader03) works the same way as Pal Dealer, not items -
  confirmed via the same BP_PalShopVenderDataComponent mechanism, own
  palShopSimpleLotteryTableName "Dark_01"/"Dark_03".

MedalTrader/BountyTrader/ArenaShop items exist here because palpedia.ru's
own "medal"/"bounty"/"arena" merchant categories turned out to be exact
coordinate duplicates of these three NPCs already found by the spawner
scan — rather than show the same marker twice, real shop data was merged
onto these existing entries instead of duplicating them under a separate
section. See extractor/PalExtract/Program.cs's NPCs section for the full
mechanism and citations.
"""

import json
from pathlib import Path
from typing import Any

from coord import locate

NPCS_PATH = Path(__file__).resolve().parent.parent / "data" / "npcs_static.json"


def load_npcs() -> list[dict[str, Any]]:
    npcs = []
    for r in json.loads(NPCS_PATH.read_text(encoding="utf-8")):
        map_name, pixel_x, pixel_y = locate(r["x"], r["y"])
        npcs.append(
            {
                "id": r["id"],
                "type_key": r["category"],
                "unique_name": r["uniqueName"],
                "name": r["name"],
                "category": r["category"],
                "icon": r["icon"],
                "level": r["level"],
                "items": r.get("items"),
                "currency": r.get("currency"),
                "currency_icon": r.get("currencyIcon"),
                "pal_pool": r.get("palPool"),
                "map": map_name,
                "pixel_x": pixel_x,
                "pixel_y": pixel_y,
            }
        )
    return npcs
