# Developer notes

Ground-truth findings from reverse-engineering Palworld's game data for this
project. Nothing here is guessed — every claim below was either extracted
directly from the game's own assets/save files via `extractor/PalExtract`, or
(where noted) cross-checked against multiple independent community sources
and sanity-checked against a real in-game fact. Dead ends are recorded too,
so they aren't re-attempted.

## Architecture

- `backend/remote.py` pulls saves from the AMP server via sudo rsync + SFTP
  (Bitvise `sexec.exe`/`sftpc.exe`).
- `backend/parse.py` extracts player position/name/level and per-player
  collection/defeat flags from the pulled `.sav` files.
- `backend/server.py` is a Flask app with a 30s background refresh loop,
  serving `/api/players`, `/api/relics`, `/api/bosses`.
- `frontend/index.html` is a Leaflet (`CRS.Simple`) map polling the above
  endpoints. World Tree is a small non-interactive inset (top-left), matching
  the game's own map presentation.
- `extractor/PalExtract` is a CUE4Parse (NuGet, C#) console app against the
  local Steam install (`D:\Steam\steamapps\common\Palworld`), using mapping
  files from `PalworldModding/UsefulFiles` (actively maintained — the
  `elliotks/Palworld-FModel` mappings are stale/pre-1.0). No FModel GUI is
  used or needed. **A full `_Generated_` World Partition scan takes ~4-6 min
  and must run in the foreground** — backgrounding a multi-minute `dotnet
  run` in this harness gets silently torn down partway through (reproduced
  twice), no error, it just stops.
- `palworld-save-tools` on PyPI (0.24.0) is broken for the 1.0 game release
  (no Oodle/`PlM` compression support, stale `RawData` sub-decoders). We use
  `deafdudecomputers/PalworldSaveTools`'s `palsav` library instead
  (`pip install "git+https://github.com/deafdudecomputers/PalworldSaveTools.git#subdirectory=src/palsav"`).
  Don't reuse the earlier hand-patched `MRHRTZ/palworld-save-tools` fork —
  superseded.

## Coordinate systems (three distinct ones — don't conflate them)

1. **Raw save/world units** — what `.sav` files and World Partition actors
   store (e.g. Penking at `(-285331.3, 210162.69)`).
2. **Pixel coordinates on our Leaflet canvas** — `backend/coord.py`'s
   `locate()`. Bounds pulled from the game's own
   `DT_WorldMapUIData.uasset` (`landScapeRealPositionMin/Max`).
   `T_WorldMap`/`T_TreeMap` textures are 8192×8192. Orientation (world Y ->
   image column, world X -> image row, no inversion) was verified against a
   real player's in-game HUD coordinate reading. Leaflet's `CRS.Simple` +
   `imageOverlay` renders a *higher* row value toward the top of the screen
   (confirmed empirically) — matters when placing the World Tree box
   relative to the main map.
3. **In-game HUD map coordinates** (the "-1000..1000-ish" numbers players
   read off the in-game map / report from wikis, e.g. Penking at "112,
   -352") — a *different* system from both of the above. Conversion formula
   (from `palworldlol/palworld-coord` on GitHub, confirmed byte-accurate
   against the boss table — computed (-285456, 209408) vs. actual
   (-285331, 210163), ~765 units apart, within rounding error of a
   human-reported reading):

   ```
   sav_x = map_y * 459 - 123888
   sav_y = map_x * 459 + 158000
   ```

   Note the **axis swap** between map (x,y) and save (x,y) — it's not a
   straight 1:1. This formula is *not currently in the codebase* (only used
   ad hoc during investigation to locate Penking) — add it to
   `backend/coord.py` if a "search by HUD coordinate" feature is ever built.

## Effigies (game's own internal name: "Relic")

Fully solved: positions, icons, real names, per-player collection state.

- **Positions**: static level actors, not in any save file. Found by
  scanning all ~9977 World Partition cell packages under
  `Pal/Content/Pal/Maps/MainWorld_5/PL_MainWorld5/_Generated_/*.umap` for
  actors whose class starts with `BP_LevelObject_Relic`, resolving each
  actor's `RootComponent` -> that component's own export index in the same
  package -> its `RelativeLocation` (= world position for a top-level
  actor). 407 total (155 base "Lifmunk" + 30×8 mid-tier + 4×3 rare). Output:
  `data/relics_static.json`.
- **Real names**: `BP_LevelObject_Relic_<Codename>`'s codename (e.g.
  `GuardianDog`) is a row key `PAL_NAME_<Codename>` in
  `Pal/Content/L10N/en/Pal/DataTable/Text/DT_PalNameText_Common.uasset` ->
  real name (e.g. `Yakumo`). **Use the `L10N/en` table, not the base
  (unsuffixed) one** — that one is Japanese source text. Don't trust
  codename vibes: `GuardianDog` ≠ "Relaxaurus" (that's actually
  `LazyDragon`) — always look it up.
- **Icons**: resolved 2026-07-19 via the same authoritative item join used
  for Schematics — each Pal has a real `"<Name> Effigy"` item row in
  `DT_ItemDataTable` (`Relic`, `Relic_01`..`Relic_12`, 13 rows total, e.g.
  `Relic_11` -> "Yakumo Effigy"), whose own `IconName` resolves through
  `DT_ItemIconDataTable` to the actual texture
  (`Pal/Content/Others/InventoryItemIcon/Texture/T_itemicon_Relic[_01..12].uasset`).
  Not a by-eye guess anymore — the earlier best-effort visual assignment in
  `backend/relics.py`'s `RELIC_META` had 5 of 12 icons wrong (caught via a
  real in-game effigy-effects list the user reported as mismatched):
  GuardianDog/Yakumo (`_10` -> `_11`), LazyDragon/Relaxaurus (`_09` ->
  `_10`), LeafMomonga/Herbil and Monkey/Tanzee (`_05`/`_06` were swapped),
  and Mutant/Lunaris (`_12` -> `_09` — `_12` is actually Mimog's icon, not
  Lunaris's). **13 item rows vs. only 12 placed Blueprints is not a gap in
  our extraction** — confirmed by a full asset-path sweep for "Relic": all
  12 `BP_LevelObject_Relic*` Blueprints exist and nothing else does.
  `Relic_12` "Mimog Effigy" (codename `MimicDog`) has real item/name/icon
  data but no placed level actor anywhere in the current game version —
  unobtainable in the open world right now, correctly absent from
  `RELIC_META` and this map.
- **Per-player collection state**: `SaveData.RecordData.RelicObtainForInstanceFlagByType`
  — keyed by the effigy's `LevelObjectInstanceId` GUID, **dashes stripped,
  uppercase** (confirmed byte-for-byte against a real player's save). This
  is a *permanent* per-instance flag. Sparse: absence = not yet found, not
  "not collected".
  - Dead end: `RelicPossessNumMap` looks tempting but tracks the *current
    unspent* effigy count before turning it in at a Statue of Power — resets
    on turn-in, not what we want.
  - Dead end: `LevelObjectRecoverPartySaveData` (checked exhaustively,
    including GUID byte-order permutations) tracks something unrelated.

## Bosses

Fully solved and wired into the live map (positions, names, elements,
weaknesses, per-player defeated state).

- **Do NOT scan World Partition actors for boss locations** — tried this
  first (mirroring the effigy method) and it's a dead end: the nearest
  placed level actors to any boss's coordinates are just the ordinary
  wildlife/resource spawners for that biome (e.g.
  `BP_PalSpawner_Sheets_blue_B_C`, `BP_PalMapObjectSpawner_log_C`). Bosses
  are not placed as individual level actors at all.
- **The real source**: a single DataTable,
  `Pal/Content/Pal/DataTable/UI/DT_BossSpawnerLoactionData.uasset` (note the
  typo "Loactio" in the actual asset name — must match exactly to load it).
  159 rows, each `{SpawnerID, CharacterID, Location{X,Y,Z}, Level}`.
  **`SpawnerID` is a field inside each row, not the row's own key/index** —
  easy to get backwards (the Rows object's own keys are just `"0".."158"`).
- **156 of the 159 rows split cleanly into two categories** — Pal field
  bosses ("category": "pal", 90 rows, e.g. Penking) and named human "boss"
  NPCs ("category": "human", 66 rows, e.g. "Scoot") — shown as a separate
  **"Bounty"** section in the UI (matches actual in-game terminology —
  confirmed via `T_icon_compass_Bounty`/`BP_NPC_BountyTrader` assets) even
  though both come from the same `DT_BossSpawnerLoactionData` table,
  because they're a genuinely distinct concept in-game: bandit/raider/
  syndicate faction leaders with their own dedicated portrait icon, not
  elemental Pal fights. The remaining 3 rows
  (`REGION_Oilrig_1/2/3`) have no `CharacterID`, no name-table entry, and no
  icon — not an actual named encounter, dropped entirely during extraction.
- Human bosses' `CharacterID` is genuinely the literal string `"None"`
  because they don't exist in the Pal-only `DT_PalMonsterParameter` table —
  so no elemental typing/weaknesses applies to them, unlike Pal bosses.
  Their real name and stats instead live in the human-equivalent table,
  `DT_PalHumanParameter` (joined by `SpawnerID` directly, e.g.
  `BOSS_Male_People03`), whose `OverrideNameTextID` (e.g.
  `NAME_BOSS_Male_People03`) resolves via a *different* text table,
  `DT_HumanNameText_Common` (not `DT_PalNameText_Common`) → real name (e.g.
  "Scoot" — confirmed against a real in-game reading, level 10 at HUD coords
  (-20, -462)).
- **Dedicated icon per human boss**: `DT_PalBossNPCIcon` (33 rows, one per
  distinct human boss, keyed by `SpawnerID`) → `Icon.AssetPathName` (e.g.
  `/Game/Pal/Texture/PalIcon/NPC/T_BOSS_NPC_Male03.T_BOSS_NPC_Male03`).
  **Gotchas extracting these as PNGs**: (1) strip the trailing
  `.ObjectName` repeat before calling `LoadPackageObjects` — the raw
  `AssetPathName` isn't a loadable package path as-is; (2) `UTexture2D.Decode()`
  returns a `CTexture`, not an `SKBitmap` — encode it with
  `TextureEncoder.Encode(texture, ETextureFormat.Png, false, out _)` (from
  `CUE4Parse_Conversion.Textures`), which returns a `byte[]` to write
  directly, no SkiaSharp needed; (3) the icon table has at least one casing
  mismatch against the boss table (`BOSS_Police_old` vs. `BOSS_Police_Old`)
  — look up case-insensitively. Exported once to
  `frontend/assets/boss_icons/<SpawnerID>.png`.
- **Name + element resolution** (for Pal bosses): join `CharacterID` (e.g.
  `BOSS_CaptainPenguin`) against
  `Pal/Content/Pal/DataTable/Character/DT_PalMonsterParameter.uasset` — this
  is a `CompositeDataTable`, so match the export by class name *containing*
  `"DataTable"`, not equal to it — for `OverrideNameTextID` /
  `ElementType1` / `ElementType2`. Then look up `OverrideNameTextID` in
  `DT_PalNameText_Common` for the real display name — the value lives at
  `row["TextData"]["LocalizedString"]`, not a top-level `"Text"` field
  (different shape from the plain `PAL_NAME_*` effigy lookup above, since
  this table is itself the `L10N/en` localized one already).
- **Game-internal element names differ from wiki/community naming** —
  confirmed by direct extraction, don't assume wiki terms carry over:
  internal `Earth` = wiki "Ground", `Leaf` = wiki "Grass", `Electricity` =
  wiki "Electric", `Normal` = wiki "Neutral". Fire/Water/Ice/Dark/Dragon
  match both.
- **Type effectiveness chart is NOT extractable game data** — it's baked
  into Blueprint UI logic (`WBP_MainMenu_Pal_ElementMatchup`), not a
  DataTable (confirmed by asset search: no `Element`/`Compat`/`Affinity`
  DataTable exists besides an unrelated item-awakening table). We use the
  publicly documented chart instead, cross-checked against two independent
  wikis (game8.co, dexerto.com) for agreement, then sanity-checked against
  Penking (Water/Ice) -> correctly predicts weak to Fire/Electric, matching
  known player experience. It's a 9-element near-single-cycle:
  `Fire -> {Leaf, Ice} -> Grass -> Ground -> Electricity -> Water -> Fire`,
  plus `Ice -> Dragon -> Dark -> Normal (none)`. Implemented as
  `ELEMENT_STRONG_AGAINST` in `backend/bosses.py`.
- **Pal boss portrait icons** (circular species icons, e.g. Penking's
  actual portrait, not a generic colored badge): `DT_PalCharacterIconDataTable`
  — **keyed by the bare species codename from `Tribe`** (e.g.
  `"CaptainPenguin"`), NOT by `CharacterID` (`"BOSS_CaptainPenguin"` is not a
  valid key here — a different join key than every other Pal lookup in this
  pipeline, easy to get wrong). Same casing-mismatch gotcha as the human
  icon table below (e.g. `Tribe` value `"BadCatgirl"` vs. this table's own
  `"BadCatGirl"` key) — look up case-insensitively.
- **Per-player "defeated" state**: `SaveData.RecordData.NormalBossDefeatFlag`
  — a `NameProperty` -> `BoolProperty` map keyed by the exact same
  `SpawnerID` string from the DataTable (confirmed exact match, e.g.
  Penking's `81_1_grass_FBOSS_9`). Reader:
  `parse.load_defeated_boss_spawner_ids()`.
  - Dead end / do not confuse: `TowerBossDefeatFlag` is for dungeon tower
    bosses only, keyed by `BOSS_BATTLE_NAME_*` instead — a different boss
    category, not present in `DT_BossSpawnerLoactionData`.
  - Dead end: `RaidBossDefeatCount` and `bFieldBossDefeatFlagResetDone` are
    both red herrings found while searching — neither is the per-spawner
    "have I beaten this" flag.
  - Dead end: `MapObjectSpawnerInStageSaveData` in `Level.sav` looked
    promising (spawner-related world state) but turned out to be loot
    lottery/respawn timers for map objects (chests/nodes), unrelated to
    boss kill tracking.
- **Oilrig raid zones are a fourth category** (`"category": "oilrig"`, 3
  rows: `REGION_Oilrig_1/2/3`) — these are the rows that were originally
  excluded as junk (no `CharacterID`, no icon in `DT_PalBossNPCIcon`) before
  a real user confirmed they're legitimate special zones visible in the
  water on the in-game map. They're not a boss at all — a raid
  zone/POI, shown on the in-game compass with its own dedicated icon,
  `Pal/Content/Pal/Texture/UI/InGame/T_icon_compass_Oilrig.uasset`. Real
  names come from `DT_WorldMap_Common_Text_Common` (the map's region-label
  table, a *different* text table from any of the above), keyed directly by
  the `SpawnerID` itself, e.g. `REGION_Oilrig_1` → "Rayne Syndicate Oil Rig".
  **No per-player defeated state exists for these** — confirmed
  `NormalBossDefeatFlag` never contains an Oilrig key for a real player. The
  actual "cleared" state lives in `Level.sav`'s
  `worldSaveData.OilrigSaveData.OilrigMap`, keyed by
  `EPalOilrigType::TypeA/TypeB/TypeC` with a `Clear` bool — but that's
  *world-shared* state (same for every player), and the `SpawnerID` ->
  `EPalOilrigType` mapping hasn't been confirmed (there are
  `BP_PalOilrigController_TypeA/B/C` Blueprints placed in the
  `oilrig_L*_X*_Y*` World Partition cells that would resolve it, same
  method as the effigy/relic actor scan, if this becomes worth doing).
  Left unimplemented for now — flagged as a possible follow-up. **This is
  intentional, not a gap** — the user confirmed oil rigs should "always
  persist on the map" with no cleared indicator, matching what world-shared
  (not per-player) state would imply anyway.
- **Confirmed exactly 3 physical oil rigs exist, matching all 3 table rows
  1:1** — cross-validated by scanning every single World Partition map cell
  (not just ones named `oilrig_*`) for any `BP_OilRig*`/`BP_Oilrig*`-classed
  actor and clustering by position. Each of the 3 clusters' actors share one
  common instance-ID hash suffix confirming they're the same physical
  structure at different LOD/streaming tiers, not separate rigs. **Gotcha**:
  the "Test Drilling Rig" (`REGION_Oilrig_2`)'s World Partition cells are
  named `CloseRange_L*_X*_Y*`, not `oilrig_L*_X*_Y*` like the other two —
  a naive filename-prefix search (as originally tried) misses it entirely;
  filter by actor class instead, across ALL map cells, if re-deriving this.
- **Frontend**: separate "Bosses", "Bounty", and "Oil Rigs" sections in the
  map legend, Bosses and Bounty sharing the existing effigy "View As" player
  dropdown (relabeled "All players (no filter)", now shared three ways) —
  Oil Rigs doesn't, since it has no per-player state. As of 2026-07-18, all
  three of Bosses/Bounty/Oil Rigs are **collapsible checklists** (same
  chevron/expand pattern as Effigies), grouped by `type_key` — `characterId`
  for Bosses (89 distinct species across 90 rows, essentially 1:1),
  `spawnerId` for Bounty (33 distinct named individuals across 66 rows, ~2
  spawn points each e.g. Scoot) and for Oil Rigs (3 rows, 1:1). The generic
  grouping/rendering logic is shared via a single
  `createTypeChecklistSection()` factory in `frontend/index.html` used by
  all three — it degrades gracefully for Oil Rigs' missing
  `elements`/`weaknesses`/`defeated` fields via JS optional chaining, no
  Oil-Rig-specific branching needed. All markers across all three sections
  are now the **actual circular species/portrait/compass icon** (see the
  `DT_PalCharacterIconDataTable` entry above) with a level-number badge in
  the corner, not a colored numbered-badge placeholder. Boss/Bounty markers
  get a green checkmark
  overlay when defeated; Oil Rig markers don't (no per-player state, see
  above). Boss tooltip shows name/level/elements/weaknesses; Bounty and Oil
  Rig tooltips show name/level only (no elements — not applicable).

## Challenge Towers

A completely separate content type from the bosses above — a queue-then-enter
battle instance, not in `DT_BossSpawnerLoactionData` at all. Internally
called "GYM" bosses (Pokémon-gym-esque naming). 9 real ones (see the World
Tree correction below — 8 was wrong, caught by the user), output baked to
`data/towers_static.json`, extraction logic in
`extractor/PalExtract/Program.cs` (runs as part of the same pipeline as
`bosses_static.json` — cheap now, no map scan needed, see Position below).

- **The 8 regions and their public names**, from `DT_WorldMap_Common_Text_Common`
  (`REGION_<X>_Boss` keys, same table effigies/bosses/oilrigs use): Grass =
  "Rayne Syndicate Tower", Forest = "Free Pal Alliance Tower", Desert = "PIDF
  Tower", Volcano = "Brothers of the Eternal Pyre Tower", Frost = "PAL
  Genetic Research Unit Tower", Sakurajima = "Moonflower Tower", Darkisland =
  "Feybreak Tower", Skyisland = "Azure Covenant Tower".
- **Boss species + elements**: join each region to a `GYM_<Pal>` row in
  `DT_PalMonsterParameter` via that row's own `NamePrefixID` field (e.g.
  `GYM_Horus.NamePrefixID == "GYM_NAME_Desert"`) — a hard, confirmed link,
  not a name-similarity guess (initial guesses based on lore/theming, e.g.
  assuming "Sky"-named things must be Skyisland, turned out partially wrong
  until this field was found — see the mislabeled-tower story below).
  Mapping: Grass→ElecPanda (Electric), Forest→LilyQueen (Grass/Leaf),
  Desert→Horus (Fire), Volcano→ThunderDragonMan (Dragon/Electric),
  Frost→BlackGriffon (Dark), Sakurajima→MoonQueen (Dark/Neutral),
  Darkisland→SnowTigerBeastman (Ice), Skyisland→BlueSkyDragon (Dragon/Water),
  WorldTree→WorldTreeDragon (untyped — `ElementType1`/`2` both `None`,
  `element1`/`element2` come out null after that null-out was extended to
  cover `element1` too, not just `element2` — see the elements-array
  regression note below).
- **9th tower: WorldTree_Boss / "Zenara & Astralym", region "Within the
  Seal"** — `GYM_WorldTreeDragon`'s `NamePrefixID` is `"GYM_NAME_LastBoss"`,
  which reads like "the true final story boss" and was *wrongly* excluded on
  that basis in an earlier pass. It's a real, separately-fightable Tower:
  `IsTowerBoss: true` on its own `DT_PalMonsterParameter` row, same as the
  other 8, and the user confirmed it in-game. Two things distinguish it from
  the original 8:
  - **Position is exact, not HUD-guessed**: no HUD coordinate was ever
    sourced for this one, but a Waypoint actor already extracted for the
    Waypoints section — `FastTravelPointID: "WorldTree_LastBoss"`, display
    name "Within the Seal" — sits at its entrance, sharing the same
    "LastBoss" root as this GYM row's `NamePrefixID`. Its raw position
    (`waypoints_static.json`) is used directly
    (`towerExactRawCoordByRegion` in Program.cs), more precise than the
    other 8's HUD-guess method.
  - **Region display name comes from a different text key pattern**:
    `REGION_WorldTree08` (part of the World Tree zone's own
    `REGION_WorldTree01`..`12` numbered list), not the `REGION_<region>_Boss`
    pattern the other 8 use — `regionTextKeyOverride` in Program.cs handles
    the exception.
  - **No recommended level** — left `null` (not guessed) pending a real
    figure; renders with no level badge/prefix, same "skip if null"
    robustness the frontend already had for towers with no position.
  - **Defeat flag `"LastBoss"` is an educated guess, not save-confirmed**
    like the rest of the map below — no player has beaten it yet in any
    available save, so the key has never appeared to verify directly.
    Follows the same "strip `GYM_NAME_` prefix" pattern that correctly
    predicts every *other* confirmed entry (e.g. `GYM_NAME_Snow` ->
    `"SnowBoss"`) and matches the WorldTree_LastBoss waypoint's own name.
    Re-verify once a real save shows it defeated.
- **`Volcano_Boss`'s defeat flag was wrong: `"VolcanoBoss"` -> `"ElectricBoss"`**
  (caught while investigating the 9th tower above, unrelated to it directly).
  Scanning every player `.sav`'s `TowerBossDefeatFlag` for the 9th tower
  turned up `"ElectricBoss"` as an extra key with no home in the previous
  8-entry map (present in 5 different players' saves) while `"VolcanoBoss"`
  appeared in *zero* — and `GYM_ThunderDragonMan` (Volcano's own GYM Pal) is
  Dragon/**Electric**-typed, the only unmapped tower that fits. The old
  value was apparently never actually cross-checked against a real save
  despite this file's own prior claim that it was — a reminder to
  spot-check "confirmed" claims when a nearby investigation turns up
  contradicting evidence, not just trust the comment.
- **Elements-array regression, fixed alongside the above**: `element1 ==
  "None"` was never null-checked (only `element2` was), so any tower/boss
  with an untyped `ElementType1` — just WorldTreeDragon so far — would have
  rendered a literal `"None"` in its tooltip's elements line. Fixed in both
  the Towers and Bosses extraction loops in Program.cs (the same
  copy-pasted `if (element2 == "None") element2 = null;` line existed in
  both places, both missing the `element1` half).
- **Boss display name** (e.g. "Zoe & Grizzbolt" — a trainer name + their
  Pal's real name, shown together): the GYM row's `OverrideNameTextID` (e.g.
  `PAL_NAME_GrassBoss`) resolved against `DT_PalNameText_Common`, same
  pattern as every other Pal name lookup in this pipeline. Icon is the
  regular Pal species portrait (`DT_PalCharacterIconDataTable`, keyed by the
  GYM row's own `Tribe` field, e.g. `"Horus"` not `"GYM_Horus"`).
- **Per-player "defeated" state**: `SaveData.RecordData.TowerBossDefeatFlag`
  — like `NormalBossDefeatFlag` but for towers, and with an easy-to-miss
  extra wrinkle: **raw keys have a `BOSS_BATTLE_NAME_` prefix**
  (`"BOSS_BATTLE_NAME_GrassBoss"`, not bare `"GrassBoss"`) — strip it in
  `parse.load_defeated_tower_flags()` before comparing against
  `towers_static.json`'s bare `defeatFlagKey` values. Also: the Desert row's
  `OverrideNameTextID` has a typo, `PAL_NAME_DessertBoss` (double s) — but
  the real flag key observed in a player's save is `DesertBoss` (single s).
  **Trust the observed real flag key over the (typo'd) table text** — this
  was only caught by actually inspecting a real save, not by deriving the
  flag name from the table field.
- **Position is NOT a placed-actor lookup, unlike every other category
  above** — none of the towers have an exact, distinctly-named entrance
  Blueprint placed in the world. Extensively searched and ruled out:
  `BP_DungeonPortalMarker_*` and `BP_PalTalkableLevelObject_*` (only found
  2/8 exactly — `GrassBoss01`, `SkyBoss` — the rest were unrelated or only
  had scattered `_Investigate`-style questline NPCs 100k+ units from the
  real tower), `BP_PalBossBattleInstanceRoot_*` (only the unused
  `MiddleBoss` variant is placed, at Z=-60020, clearly a hidden/test
  location), a generic "Tower"-classname sweep (surfaced the tower *arena*
  itself, which is a self-contained instanced sublevel at tiny local
  coordinates, not a real overworld position), `BP_LevelObject_TowerLockBarrier_C`
  (an unrelated generic lock-puzzle mechanic, ~66 instances elsewhere in the
  game), and `BP_PalRegionTriggerBox_C` (a generic biome-boundary marker
  used dozens of times all over the map — coincidentally close to 4 towers
  by thematic placement, but not tower-specific).
  **What we use instead**: real in-game HUD map coordinates, read directly
  off the map by a player standing at each tower, independently
  cross-checked against palworld.fandom.com's Tower page (which lists a
  named sub-region + coordinate per tower, e.g. "Windswept Island
  (113, -431)" for Rayne Syndicate) — full agreement across all 8. Converted
  via the same `sav_x=map_y*459-123888, sav_y=map_x*459+158000` formula
  validated elsewhere in this pipeline (Penking: 765 units off; Grass and
  Skyisland here: 1.5-3k units off vs. their confirmed placed-actor
  position — the same precision class, giving confidence in the other 6
  that have no placed actor to cross-check against at all). All 8 now have
  `positionExact: true`, hardcoded as `towerHudCoordByRegion` in
  `Program.cs` (cite-able, reproducible, no map scan needed — much simpler
  than the actor-hunting this replaced).
  **Gotcha (resolved)**: Feybreak Tower's wiki-sourced HUD coordinate was
  initially entered as `(1294, -1669)`, which converts to a Y value
  (751946) slightly past `MAIN_MAP_BOUNDS`'s nominal max (724400) — flagged
  at the time as "a real remote DLC-zone location near the edge, not a
  bug." It *was* a bug: the wiki had a typo (missing minus sign) and the
  real coordinate is `(-1294, -1669)`, confirmed by the user and correctly
  converting to `(-889959, -435946)` — comfortably inside the main map
  bounds. **Lesson**: a converted coordinate landing suspiciously close to
  (but past) a known boundary is itself a signal worth questioning, not
  just explaining away as "must be a remote location."
- **A naming mislabel was caught and corrected via two user-reported
  coordinates, not by name-matching alone** — the user's first-reported
  coordinate for "PIDF Tower" matched `SkyBoss` (a completely different,
  wrongly-adjacent region internally) almost perfectly by position; only a
  *second*, differently-located "PIDF Tower" coordinate (with the user also
  independently confirming "in the Desert") matched the real Desert-region
  location, revealing the first report was actually the Skyisland/Azure
  Covenant tower — later fully confirmed when the user's screenshots showed
  "Sunreach (-423, -1425)" (Azure Covenant) is nearly identical to that
  first-reported coordinate. **Position + independent semantic confirmation
  together resolved an ambiguity that either alone would have gotten
  wrong** — see [[feedback-verify-with-ground-truth]] for the general
  pattern.
- **No data-mined "recommended level" per tower exists anywhere in the
  game's own files** — towers aren't in `DT_BossSpawnerLoactionData` (which
  has a `Level` column for open-world bosses) and no equivalent field was
  found for the GYM rows or in `DT_DungeonLevelDataTable`. Each tower
  actually has 2 difficulty tiers (Normal/Hard — matching the `GYM_<Pal>_2`
  row variants found earlier, same `NamePrefixID`/elements as the base
  row, presumably the Hard-mode stat block). `levelNormal`/`levelHard` in
  `towers_static.json` are hardcoded from the same cross-checked
  screenshot source as the coordinates above (independently confirmed
  against palworld.fandom.com, full agreement): Grass 10/72, Forest 20/74,
  Volcano 30/76, Desert 40/78, Frost 50/80, Sakurajima 55/80, Darkisland
  60/80, Skyisland 68/80.
- **Frontend**: "Towers" section, placed above Oil Rigs per request, reusing
  the exact same `createTypeChecklistSection()` component as Bosses/Bounty/
  Oil Rigs (shares the "View As" dropdown with Bosses/Bounty, since towers
  do have per-player defeat state). The shared component needed 3 small
  generalizations to support towers cleanly rather than a bespoke branch:
  (1) tooltip shows `boss_name` alongside the region `name`, and a `Hard: Lv
  X` line, when present, (2) level badge/tooltip line omitted when `level`
  is `null` instead of rendering the literal string "null" (kept for
  robustness even though all 8 now have a level), (3) `render()` skips map
  placement (but not the checklist row) when `has_position` is `false`
  (kept for robustness even though all 8 now have a position).
- **Tower marker icon** (as of a later pass, 2026-07-18): all 8 towers share
  one icon — the game's own compass icon for boss towers
  (`EPalLocationType::PointBossTower` in `DT_LocationUIData` ->
  `T_icon_compass_tower`), not the per-species Pal portrait used earlier —
  the marker represents "a tower", not "a Pal". Rendered un-clipped
  (`.tower-marker .portrait { border-radius: 0 }`, a new marker class
  distinct from the circular Boss/Bounty/Oilrig portrait style) since the
  icon's own diamond shape would get cut off by a circular clip. The
  checklist's per-species icon and per-item visibility checkbox are
  unaffected by this — only the icon image changed.
- **Level display + sort, extended to Bosses and Bounty too**:
  `createTypeChecklistSection()` gained two options, `sortByLevel` (group
  list rows by ascending level instead of alphabetically, name as
  tiebreaker) and `squareIcon` (skip the circular list-icon clip, used only
  by Towers). Bosses, Bounty, and Towers all pass `sortByLevel: true`; each
  list row is prefixed `Lv <N> <name>`. Oil Rigs deliberately left at the
  default alphabetical/no-prefix behavior (not requested for it).

## Watchtowers / Waypoints

Fast-travel points the player unlocks by walking up and interacting with
them. On the game's own map these are greyed-out silhouettes until
unlocked, full color after — a different visual language from the
checkmark badge used by Bosses/Bounty/Towers, so the frontend renders
these with a CSS grayscale + dim filter instead (see below).

- **Not a DataTable** — like effigies, these are placed actors, not save
  data or a DataTable row. Unlike effigies (and unlike what was originally
  assumed from the effigy precedent), they live directly in the
  **persistent level** (`Pal/Content/Pal/Maps/MainWorld_5/PL_MainWorld5`,
  loaded via a single `LoadPackageObjects` call), **not** the streamed
  World Partition `_Generated_` grid — confirmed by two full 9977-cell
  `_Generated_` scans (one broad by class-name prefix, one specifically
  re-checking `BP_LevelObject_UnlockMapPoint`) that both found zero. These
  are always-loaded landmarks, so World Partition streaming doesn't apply.
- **Two distinct Blueprint classes, not one shared class with a type
  flag** (found by dumping a class histogram of the persistent level's
  31159 exports and grepping for Tower/Respawn/Warp/FastTravel):
  - `BP_LevelObject_UnlockMapPoint_C` (22 instances) = **Watchtowers**,
    the tall climbable tower structures (their `FastTravelPointID` values
    are `WatchTower_1`..`WatchTower_22` plus `WatchTower_WorldTree_1/2`
    for the Feybreak DLC area). The visible mesh is a child actor
    component (`BP_pal_map_small_tower_C`), but position/ID/instance-GUID
    live on the parent `UnlockMapPoint` actor, not the child.
  - `BP_LevelObject_TowerFastTravelPoint_C` (152 instances, confusingly
    named given the class above) = every other fast-travel point —
    **Waypoints**: generic named landmarks (e.g. "Fisherman's Point"),
    Sealed Realm dungeon entrances (e.g. "Sealed Realm of the Mystic"),
    and DLC-region-specific ones (`SkyIsland_*`, `WorldTree_*`,
    `New_SmallIsland*`, `Boss_KingWhale`, `Boss_Forest`, `FootOfWorldTree`).
- **Two named Watchtowers have no placed actor anywhere**: the text table
  (see below) has entries for `WatchTower_14` ("Lava Reservoir
  Watchtower") and `WatchTower_16` ("Ruined Fortress City Watchtower"),
  but neither appears in the persistent level or either `_Generated_`
  scan. Likely orphaned text rows from a since-renamed/consolidated
  region (same category of staleness as the Feybreak Tower coordinate
  typo documented under Challenge Towers below). Left out — 22 confirmed
  real ones is what's actually placed and collectible in-game.
- **Display names**: `Pal/Content/L10N/en/Pal/DataTable/Text/DT_MapRespawnPointInfoText`,
  keyed by the actor's own `FastTravelPointID` property (e.g.
  `WatchTower_6` -> "Verdant Stream Watchtower", `FTPoint35` -> "Sealed
  Realm of the Mystic"). This table also has 8 unrelated `SpawnPoint_N`
  rows (multiplayer initial-spawn location descriptions, joined to a
  separate `DT_RespawnPointInfo`/`BP_LevelObject_StaticRespawnPoint_C`
  pair — a dead end for this feature, not fast-travel points at all) and
  a handful of `FTPointNN = "en Text"` placeholder rows (unused/reserved
  IDs, no matching placed actor — expected, not a bug).
- **Position**: same actor-scan technique as effigies — resolve the
  actor's `RootComponent` property to the actual `SceneComponent` export
  and read its `RelativeLocation`. The property's `ObjectPath` suffix
  (e.g. `...PL_MainWorld5.5685`) is a **direct 0-based index** into the
  same `LoadPackageObjects()` export list (confirmed empirically — the
  naive assumption of a 1-based/off-by-one index resolved to the wrong
  sibling component, `IndicatorOrigin` instead of `Root`, one field
  earlier in the same export list; using the raw index fixed it).
  Validated against two real user-reported HUD coordinates (Verdant
  Stream Watchtower ~(203,-98), Sealed Realm of the Mystic ~(120,-62)):
  both converted extractions landed within ~500 units of the predicted
  raw position, the same precision class as the rest of this pipeline.
- **Per-player "unlocked" state**: `SaveData.RecordData.FastTravelPointUnlockFlag`
  — keyed by the same `LevelObjectInstanceId` GUID (dashes stripped,
  uppercase) baked into the level actor, identical scheme to effigies'
  `RelicObtainForInstanceFlagByType`. Confirmed by exact key match against
  a real player's save for both a Watchtower and a Waypoint instance.
- **Icons**: `EPalLocationType::PointFastTravel` (Watchtowers) and
  `::PointWarpAltar` (Waypoints) in `DT_LocationUIData` ->
  `T_icon_compass_FTtower` / `T_icon_compass_Teleport`. Both are the
  game's actual in-game compass icons and are already near-monochrome
  (white/pale blue) art — a plain CSS `grayscale(1)` filter for the
  "not yet unlocked" state barely changes them, so the frontend also
  dims brightness/opacity to make the two states visually distinct (a
  pure grayscale-filter-only first pass looked identical at a glance
  during testing — verify any future icon-state styling by screenshotting
  a real mixed collected/uncollected cluster, not just spot-checking one
  marker).

## Journals (game's own internal name: "Note")

Fully solved and wired into the live map, as of 2026-07-18: positions,
titles/lore text, icons, per-player "read" state. 64 total, shown as a
"Journals" section (checklist between Towers and Bases).

- **Two content types under one system**: 23 "Castaway's Journal" story
  entries (row keys `Day0`..`Day38`, plus `Day-xx`) scattered across the main
  island, and 41 NPC "Diary" entries (e.g. `GrassBoss1`..`GrassBoss5` = "Zoe
  Rayne's Diary") tied to one of the 9 Tower regions, found inside/near that
  region's Tower dungeon. Row names for the diary group loosely echo (but
  don't exactly match) the Tower `defeatFlagKey` naming — e.g. `SnowBoss1`
  belongs to the Frost region same as the tower's own `SnowBoss` defeat flag,
  but `VolcanoBoss1` belongs to Volcano despite the tower's own defeat flag
  there actually being `ElectricBoss` — these are two independently-authored
  ID schemes, don't assume they're the same key space.
- **Source tables**: `Pal/Content/Pal/DataTable/NoteData/DT_NoteMasterDataTable`
  (64 rows, just a row-exists marker), `DT_NoteTextureDataTable` (id ->
  per-note unique texture path), and
  `Pal/Content/L10N/en/Pal/DataTable/Text/DT_NoteDescText` (id -> full lore
  text). A third table, `DA_NoteDataAsset`'s own `NoteDataMap` property, is
  the authoritative id list (also 64 keys) and was used to cross-check
  extraction completeness — not otherwise needed since `DT_NoteTextureDataTable`
  already has a flat id -> texture mapping directly.
  `DT_NoteDescText`/`DT_NoteMasterDataTable` each have 1-3 extra orphaned rows
  (`Day70`, `Day??`, `VolcanoBoss2`) with no matching placed actor anywhere -
  same "stale/unused text row" pattern as the two orphaned Watchtower rows
  documented below - left out.
- **Title + lore text**: `DT_NoteDescText[id].TextData.LocalizedString` is
  always `"<title line>\r\n\r\n<body>"` (e.g. `"Zoe Rayne's Diary -
  1\r\n\r\nI don't have a family...\r\n\r\n..."`) - split on the first blank
  line to separate a short per-entry title from the multi-paragraph body.
  These are real, substantial lore writing (each Tower region's diary is
  narrated by that region's own boss NPC, in-character) - a short preview
  (first ~160 chars, `|emphasis|` pipe-markup stripped) is shown in the map
  tooltip rather than the full body, to keep the tooltip compact like every
  other section.
- **Position**: same actor-scan technique as Effigies/Watchtowers - resolve
  `BP_LevelObject_Note_C`'s `RootComponent` -> `RelativeLocation`. Unique
  among every category in this pipeline: the 64 instances are split across
  BOTH placed-actor locations used elsewhere - 15 (the Skyisland/`Sorajima*`
  and `WorldTree*`/`WorldTreeBoss*` regions) live directly in the
  **persistent level** (`PL_MainWorld5`, same as Watchtowers/Waypoints -
  always-loaded DLC landmarks), while the other 49 (all 23 Day notes + the
  remaining 7 Tower regions' diaries) are streamed **World Partition** actors
  under `_Generated_` (same as Effigies). Confirmed exhaustively: persistent
  level scan found exactly 15, a full 9977-cell `_Generated_` scan found
  exactly 49, total 64 with zero overlap or gap against `DA_NoteDataAsset`'s
  own 64-key `NoteDataMap`. The `_Generated_` half is the expensive part of
  this extraction (~4-6 min, must run in the foreground - see the top-of-file
  warning) — the Guild Base / Watchtower / Waypoint categories don't pay this
  cost since none of them need a `_Generated_` scan at all (persistent-level-
  only or live-save-only), only Effigies and now Journals do.
- **Icon**: `DT_NoteTextureDataTable[id].Texture.AssetPathName` gives each
  note its own unique in-world "photograph" texture (exported to
  `frontend/assets/note_icons/T_Note_<id>.png` by Program.cs, 64 distinct
  PNGs) - but **these are NOT used for the map marker**. They're
  full-screen "read this note" background art (up to ~3860x2180, 8-15MB
  each as a lossless PNG, 533MB total for 64 - downscaled to a 128px-max
  thumbnail via `SixLabors.ImageSharp`, already a transitive
  CUE4Parse-Conversion dependency, in `ExportNoteIcon`, but still 533MB ->
  ~1.4MB of essentially-unused files), landscape-oriented and inconsistent
  in aspect ratio, so they don't read as a recognizable icon at 30px map
  marker size no matter how small they're scaled. Checked exhaustively for
  a smaller, purpose-made "note/page/document" icon elsewhere in the game's
  own assets first (DT_LocationUIData, every Icon-ish/notebook/diary/
  document/journal-named texture path) - nothing exists; the per-note photo
  is genuinely the only game-provided art for this collectible, and it's
  the wrong shape for a marker.
  - **What's actually used instead**: one hand-drawn generic page/document
    SVG icon (`frontend/assets/note_icons/_page_icon.svg`, plain
    inline-color shape, no game asset), shared by all 64 notes -
    `backend/notes.py`'s `ICON_FILE` constant overrides the
    per-note `icon` field from `notes_static.json` at load time (the static
    JSON file / extracted PNGs are untouched, just unused for this
    purpose - same "one icon per section" precedent as Oil Rigs/Watchtower/
    Waypoint's shared compass icons). Rendered un-clipped like the Tower
    compass icon (`.journal-marker`, `squareIcon: true`), not
    circular-clipped like Bosses/Bounty/Effigies portraits - fits well since
    the SVG's own viewBox is square, so there's no letterboxing.
  - **A real, separate bug surfaced while diagnosing the oversized photos**
    (fixed regardless of the icon swap above, since it's not
    Journals-specific): on-screen markers rendered ~1.8x wider than every
    other marker type (~53x30 instead of the intended 30x30) even after the
    photos were downscaled. Root cause was a pre-existing latent CSS
    specificity bug - Leaflet's own `leaflet.css` ships
    `.leaflet-container .leaflet-marker-pane img { width: auto; ... }`,
    which outranks a plain `.foo-marker .portrait { width: 30px }` rule on
    specificity (its extra bare `img` beats our two classes), silently
    forcing every marker image's width back to aspect-ratio-derived `auto`.
    This has apparently been true since this project's first marker was
    added, but every icon used until Journals (relics, boss/bounty/oilrig/
    tower portraits, watchtower/waypoint/base compass icons) happens to be
    square, so `auto` computed to the same value as the intended fixed size
    and the bug was invisible - only a non-square source image (the
    Journal photos, before the generic-icon swap above) exposed it. Fixed
    by adding `!important` to every `.portrait`-style width/height rule in
    `frontend/index.html` (mirroring Leaflet's own use of `!important` in
    that exact same stylesheet rule) rather than only patching the Journals
    case - the other sections were silently relying on their icons
    coincidentally being square. Kept even after switching to the (square)
    generic page icon, since it's a correct fix for every section, not a
    Journals-specific workaround.
- **Checklist grouping**: not by note id prefix directly, but by resolving
  each Diary note's Tower region to that region's own display name via the
  same `regionNameRowsChk`/`regionTextKeyOverride` tables the Towers section
  above already loads (e.g. all `GrassBoss*` notes group under "Rayne
  Syndicate Tower", matching the Towers section's own display name for that
  region) - so a player can correlate "this diary was found near that
  Tower" without needing to know the internal region key. The `WorldTree*`
  (non-Boss) notes have no matching Tower region row at all (they're general
  World Tree DLC zone exploration notes, distinct from the `WorldTreeBoss*`
  ones tied to the Tower itself) - grouped separately under a literal "World
  Tree" label. The 23 Day notes group under a literal "Castaway's Journal"
  label (no table lookup - that's the note collection's own proper name
  in-game, the same name seen literally in every Day note's own title
  line).
- **Per-player "read" state**: `SaveData.RecordData.NoteObtainForInstanceFlag`
  - **notably different from every other placed-actor category in this
  pipeline**: this is a flat `NameProperty` -> `BoolProperty` map keyed
  **directly by the note's own row name** (e.g. `"Day1-1"`), NOT a
  `LevelObjectInstanceId` GUID like Effigies'
  `RelicObtainForInstanceFlagByType` or Watchtowers/Waypoints'
  `FastTravelPointUnlockFlag` - no dash-stripping/uppercasing normalization
  needed, just compare the bare id directly. Confirmed by checking all 13
  real players' saves at once: every single observed key exactly matched a
  known note id from `notes_static.json`, zero unknown/stray keys - strong
  confirmation this is the right (and only) flag for this purpose. Reader:
  `parse.load_read_note_ids()`.
- **Frontend**: "Journals" section between Towers and Bases, reusing the
  shared `createTypeChecklistSection()` component (`visualStyle: 'checkmark'`
  like Bosses/Towers, `collectedWord: 'Read'` instead of the default
  "Defeated") rather than Effigies' older bespoke hide-when-collected
  behavior - Journals are conceptually closer to Bosses/Towers (a discrete
  "have I found this specific one" checklist item with a stable per-instance
  identity) than to Effigies' original implementation, which predates this
  shared component. Tooltip's existing `boss_name` field (already used by
  Towers to show "region name — specific boss name") is reused unmodified
  for "group name — diary title"; the only frontend addition is a new
  optional `entry.preview` tooltip line (`.bt-preview`, italic/muted),
  generic enough that every other section's tooltip silently ignores it via
  the same "absent field -> no line" pattern already used throughout
  `createTypeChecklistSection()`.

## Guild Bases

Unlike every other section above, guild bases aren't static game data —
players build/dismantle them at runtime, so there's no extractor pipeline
for this one. Read live from `Level.sav` each 30s refresh cycle (like
player positions), written to `data/bases.json` alongside `data/players.json`.

- **Source**: `worldSaveData.BaseCampSaveData` (29 entries in a real save,
  keyed by base GUID) for position/ownership, cross-referenced against
  `worldSaveData.GroupSaveDataMap` (guild membership + name) — see
  `backend/parse.py`'s `load_guild_bases()`.
- **`GroupSaveDataMap` holds two unrelated group types** — filter to
  `RawData.group_type == "EPalGroupType::Guild"`. The other type,
  `"EPalGroupType::Organization"`, is some other per-entity grouping
  (observed with a single `individual_character_handle_ids` entry each,
  never a real player-facing guild) — skipped entirely.
- **Base position**: each `BaseCampSaveData` entry's own
  `RawData.transform.translation` (raw world units) — no actor-scan needed,
  unlike Watchtowers/Waypoints/Effigies, since this is direct save data.
  Validated against a real user-reported HUD coordinate (~(164,-463)):
  converted extraction landed within ~700 units, consistent with the rest
  of this pipeline.
- **`RawData.name` is not a usable per-base name** — confirmed every single
  base in a real save (all 29) carries the exact same untouched template
  string (e.g. `"新規生成拠点テンプレート名0(仮)"`, a Japanese
  placeholder meaning roughly "newly-generated base template name N
  (provisional)") — players never rename individual bases in practice, and
  the game's own UI shows the generic label "Base" for all of them
  regardless (confirmed via a real in-game screenshot of the interaction
  prompt). Not used; markers are just labeled by their owning guild instead.
- **Guild ownership**: each base's `RawData.group_id_belong_to` GUID
  matches a `GroupSaveDataMap` entry's own key. That guild's
  `RawData.players` list (each `{player_uid, player_info.player_name,
  role}`) gives per-player guild membership directly — no need to cross
  the individual player .sav files for this.
- **Guild display name fallback**: confirmed *every* guild in a real save
  is left at the default `"Unnamed Guild"` name (none of the 10 guilds
  tested had been custom-renamed except two: "Meowingus Bingus!" and
  "Eevee Garden" and "Little Bear LLC"). Falls back to
  `"<admin nickname>'s Guild"` using `RawData.admin_player_uid` — otherwise
  every unnamed guild's checklist row reads identically as "Unnamed Guild",
  indistinguishable from each other.
- **Icon**: `EPalLocationType::PointBaseCamp` in `DT_LocationUIData` ->
  `T_icon_compass_camp` (diamond frame + house/castle silhouette) —
  confirmed against a real in-game "Base" interaction-prompt screenshot
  *before* use this time, having been burned once already on the
  Watchtower/Waypoint icons (see above) trusting a DT_LocationUIData name
  match alone without a visual check.
- **Level.sav is parsed once per refresh cycle, not twice**: both player
  names (`CharacterSaveParameterMap`) and guild bases
  (`BaseCampSaveData`/`GroupSaveDataMap`) live in the same large
  `worldSaveData` block (~88MB decompressed) — `parse.py`'s
  `load_level_world_save_data()` parses it once per `refresh.run()` call
  and both `load_player_names_and_levels()` and `load_guild_bases()` take
  the already-parsed dict rather than each re-reading the file.
- **Per-request dimming is cheap, unlike Effigies/Bosses/Watchtowers**: guild
  membership is already embedded in the shared `bases.json` cache written
  every refresh cycle, so `/api/bases?view_as=<uid>` just looks up which
  guild's `player_uids` contains the requested uid and flags matching bases
  `own_guild: true` — no per-player `.sav` re-parse needed on each request
  (those other sections need one since their "collected" state lives in
  each player's own save file, not the shared world state).
- **Frontend**: reuses the same `visualStyle: 'grayscale'` dim-vs-full-color
  marker style as Watchtowers/Waypoints (`collectedField: 'own_guild'`,
  `knownFlagKey: 'guild_known'`) — bases belonging to another guild are
  dimmed once a player is selected, the viewed player's own guild's bases
  stay full color, and with no player selected every base shows full color
  (ownership unknown, same "don't imply false state" rule as the fast
  travel points). Checklist groups by guild (`type_key: guild_id`) rather
  than listing all 29 bases individually, since bases have no per-instance
  distinguishing name — the guild is what a user actually wants to
  toggle/see counts for.
- **Tooltip shows everything the save data has on the guild**: title
  ("<guild name> — Base"), leader (`admin_player_uid` resolved to a
  nickname, falling back to the guild's own cached `player_info.player_name`
  if that uid isn't in the live `CharacterSaveParameterMap` — e.g. a player
  who's left/been removed since), and member count. `guild_leader` /
  `guild_member_count` are base-entry fields the generic
  `createTypeChecklistSection` tooltip renders whenever present, harmless
  no-ops for every other section (Bosses/Towers/etc. never set them).

## Tooltip styling (all sections, not Journals-specific)

Every marker tooltip built via `tooltipHtml()`/`createTypeChecklistSection()`
(Bosses/Bounty/Towers/Oil Rigs/Journals/Watchtowers/Waypoints/Bases) had been
silently inheriting Leaflet's own default `.leaflet-tooltip` styling this
whole time - a white/cream background, dark (`#222`) text, and
`white-space: nowrap` - since no override was ever added. This was invisible
for short one-line tooltips (a boss's name/level fit on one un-wrapped line
regardless), but two real problems only surfaced once Journals added
longer, multi-clause content: the app's own `.bt-line`/`.bt-weak`/
`.bt-preview` text colors (`#cde`/`#f2b56b`/`#9ab`) were chosen for a *dark*
tooltip background and read as low-contrast on Leaflet's actual white one,
and `white-space: nowrap` meant long text (e.g. a Journal preview) never
wrapped at all - it just kept extending as one absurdly wide unbroken line.
Fixed with a `.leaflet-tooltip:not(.player-label)` override (dark
background/border matching the rest of the UI's `rgba(20,22,26,*)` theme,
`white-space: normal`, plus matching direction-arrow `:before` colors) -
scoped to exclude `.player-label` (the always-visible player name tooltips,
which are *also* real Leaflet tooltips under the hood and have their own
distinct pill styling that must stay untouched).

- **A second, non-obvious bug surfaced while fixing the first**: giving the
  tooltip a `max-width` (e.g. 260px) to make wrapping useful had no effect
  at all - the rendered width stayed stuck at roughly the length of the
  single longest word in the content (~70-90px), even after raising
  max-width to 1000px to rule out an unrelated cap. Root cause: Leaflet
  positions tooltips purely via CSS `transform: translate3d(...)`, never
  setting `left`/`right` - and Chromium's shrink-to-fit width algorithm for
  an absolutely-positioned element with no `left`/`right` set falls back to
  its *minimum* content width (the longest unbreakable token) rather than
  growing to fill available space up to `max-width`. An explicit `width`
  (220px, chosen to comfortably fit a Journal preview in ~4-5 lines)
  sidesteps the shrink-to-fit computation entirely and was the actual fix -
  `max-width` alone does not work for Leaflet tooltips in this codebase, a
  gotcha worth remembering for any future tooltip content.

## Operational gotcha

Port 5151 (the app's default) can end up with two Flask dev-server processes
bound simultaneously on Windows without erroring (silent dual-bind quirk) if
an old instance is left running when a new one starts — requests then
nondeterministically hit whichever process the OS happens to route to. If
`/api/bosses` (or any newly-added route) 404s unexpectedly, check
`netstat -ano | grep 5151` for more than one `LISTENING` PID before assuming
the code is wrong — kill the stale one (check `Get-Process -Id <pid> |
Select StartTime` to tell old from new) rather than the one you just started
by mistake.

## Quests (Main / Sub, map-marker-bearing only)

Frontend: two sections, "Main Quests" and "Sub Quests", each split into
"Active" / "Not Started" subgroups — genuinely per-player state, so (unlike
every other section) both stay hidden entirely with no player selected in
"View As", and an empty subgroup isn't shown-but-empty, it's just absent.
Investigated after the user asked whether quests map 1:1 to NPCs (for an
earlier, since-descoped idea of a checkbox-per-NPC "Quest NPC" category) —
they don't, in two different ways confirmed against real game data:

- **One NPC, multiple quests**: Zoe (`DT_UniqueNPC` row key is actually
  `"GrassBoss"`, not "Zoe" — her display name is a separate text lookup) is
  5 separate quest rows (`Sub_Zoe01`..`04` + `Sub_Zoe_Halloween`) at one
  spot — a real chain.
- **Numbered suffix that looks like a chain but isn't**: `Sub_Farmer01`..`04`
  resolve to 4 physically distinct map locations (confirmed via
  `DT_PalQuestLocationData`) — four different farmers, one quest each, not
  one farmer's 4-step chain. Same naming shape, opposite meaning. No
  explicit "chain group" field exists anywhere in the data to tell these
  apart generically.

The mechanism that sidesteps needing to tell those apart, and that this
whole feature is built on: every quest (`DT_PalQuestData`, 120 rows: 58
Main / 59 Sub / 3 Hidden) is its own Blueprint (`QuestData.AssetPathName`)
with an ordered `QuestBlockGroupList` — each group ("step") has a
`BlockList` (usually 1 block, sometimes several running in parallel).
**Each block optionally carries `LocationSettingData.FixedLocationPointArray`
— a real, explicit foreign key** (`{DataTable: DT_PalQuestLocationData,
RowName}`), not a naming-convention guess — confirmed directly against
Zoe's first block (`BP_SubQuestBlock_Zoe01_DisplayElecpanda`), which points
at row `"Sub_Zoe"` (plus `"Main_CaptureDeerGround"`, a second marker on the
same step). `extractor/PalExtract/Program.cs`'s Quests section walks every
Main/Sub quest's blocks, resolves this join, and drops the quest entirely if
none of its steps have a location anywhere — an explicit user scoping call
("if there's no map marker, we don't care about it"). 87 of 117 Main+Sub
quests survive that filter (28 Main, 59 Sub — every Sub quest happens to
have a marker somewhere; Main's mostly-tutorial rows without one, e.g.
`Main_CraftTools`/`Main_EatFood`, are correctly dropped). Hidden-type quests
(3 rows, background triggers like "you changed your weapon's ammo type")
are dropped outright, not evaluated.

**Live per-player join** (`backend/parse.py` + `backend/quests.py` +
`server.py`'s `/api/quests`, no extraction involved):
`SaveData.OrderedQuestArray_FullRelease` (NOT under `RecordData`, unlike
every other per-player flag in this project) is a flat array of
`{QuestName, BlockIndex}` structs for every currently in-progress quest.
**`BlockIndex` indexes directly into that quest's own
`QuestBlockGroupList`** — confirmed against a real player: their
`Main_RayneSyndicate` sat at `BlockIndex 1`, its own 2nd block group (the
"DefeatBoss" step), matching where a Rayne-Syndicate-in-progress player
would actually be. `SaveData.CompletedQuestArray_FullRelease` is a flat
`NameProperty` array (not a bool map) of finished quest IDs. A quest not in
either array is "not started" — its target is step 0 (wherever you'd go to
begin it). **This is applied per-status, not just per-quest**: a couple of
Zoe's quests (`Sub_Zoe02`/`04`) have a location-less first block (a pure
"talk" trigger — the real marker only appears once the quest is active), so
they correctly produce no "not started" pin even though the quest overall
does have a marker once active. Verified end-to-end against 11 real players'
saves via the Flask test client before wiring up the frontend — found real
active chains at every stage (e.g. one player's `Main_DefeatForestBoss` at
6/7, another's `Main_WorldTreeAbyss` at 2/6).

**Quest title text is not resolvable from any shipped game asset.**
`QuestTitleMsgId` (e.g. `"QUEST_MAIN_TITLE_BOSS_AURI"`) does not resolve
through any DataTable — an exhaustive DataTable/L10N path keyword search
(QuestText/QuestName/QuestTitle/QuestDesc) came back with zero hits, and a
follow-up check of every `ST_*`/StringTable uasset in the game (7 total)
and every shipped culture's `Game.locres` (byte-identical 37-byte empty
stubs across en/ja/ko/zh-Hans) confirmed the compiled UE localization
pipeline isn't used by this game at all — real text must come from native
code, not `Pal/Content`. **Resolved 2026-07-20** by sourcing real display
names from [palpedia.ru](https://palpedia.ru) instead, a data-mined
community Palworld quest database whose URLs are keyed directly by this
exact internal quest ID (e.g.
`https://palpedia.ru/en/missions/quest:Main_DefeatKingWhale`). Two entries
were user-confirmed in-game before trusting the source
(`Main_DefeatKingWhale` = "Panthalus", `Sub_Breeder01` = "Breeding Basics"),
both matching palpedia.ru exactly. 77 of 87 quests resolved this way; the 9
that 404 on palpedia.ru (`Sub_PalDisplay_A_01`..`I_01`) plus
`Test_UnlockAreaBarriers` (a leftover dev/test quest) still fall back to the
row-name-derived label described below. See
`extractor/PalExtract/Program.cs`'s Quests section for the implementation.

**Known clutter, left in deliberately, flagged for a follow-up call**: 14 of
the 59 Sub quests kept are reward/trigger stubs reusing the quest system for
non-NPC bookkeeping (`Sub_PalDisplay_A_01`..`I_01`, `Sub_PalCaptureCountReward`,
`Sub_BossDefeatReward`, `Sub_PaldexReward`, `Sub_FoodReward`,
`Sub_Kigurumi01_Replay`) — they do have real map markers so the "no marker,
don't care" rule doesn't drop them, but they aren't NPC-driven quests in any
meaningful sense. Not filtered out without an explicit call on it.

## NPCs (Trader / Black Market / Dog Coin / General)

Picked back up after the Quests feature shipped (Quests split off precisely
*because* "Quest NPC" as a checkbox-per-NPC category didn't work - see the
Quests section above). This is the general/shop/flavor NPC pass: is a named
`DT_UniqueNPC` row a Trader, Black Market dealer, the Dog Coin exchange NPC,
or just a General villager, and where is it actually standing?

**Mid-investigation mistake, for the record**: a `git checkout --
extractor/PalExtract/Program.cs` run to discard a scratch block also wiped
the *permanent*, already-working Quests extraction section, since it was
uncommitted in the same file. Recovered by re-adding the exact code from
conversation history and re-verifying the rebuilt `quests_static.json`
matched byte-for-byte (same 87-quest count, same title text). Lesson: check
`git diff` for what a checkout would actually discard before running it on
a file with real uncommitted work mixed into scratch edits, not just on
files believed to be scratch-only.

**Placement mechanism** (the blocker that shelved this the first time):
NPCs are NOT hand-placed as their own character Blueprint the way
Effigies/Watchtowers/Notes are. They're runtime-spawned by a
`BP_MonoNPCSpawner`-family actor (persistent level + streamed World
Partition - same two-place split as Notes/Schematics, folded into that same
scan pass rather than paying for a third ~4-6 min walk). Confirmed by
inspecting real placed instances: each spawner's `Properties.UniqueName.Key`
is a real, exact foreign key into `DT_UniqueNPC`'s row names (e.g.
`"DarkTrader"`, `"U_Male_SorajimaPeople01"`) - not a naming-convention
guess. This also explains the earlier `CharacterSaveParameterMap` sighting
of `"BOSS_Male_Trader01"`/`"BOSS_Male_Trader03"` from the very first Quests
investigation - those were spawned instances of a *different* spawner
family (`Spawner/HumanNPCBoss/*`, the human Bounty targets bosses.py
already covers), sharing the same underlying spawn concept.

Three spawner classes needed, found by trial: the plain base
`BP_MonoNPCSpawner_C`, `BP_MonoNPCSpawner_Unique_C` (a bare subclass, no own
properties, safe to treat identically), and
`BP_MonoNPCSpawner_MedalTrader_C` (in its own dedicated
`Spawner/UniqueNPC/` subfolder) - the first full scan came back with
`MedalTrader` (the sole Dog Coin NPC) completely missing, which is what
surfaced this third class. Unlike the first two, it bakes
`UniqueName`/`Level` as its own class defaults (`"MedalTrader"`/`50`) rather
than a per-instance override - `npcClassDefaultUniqueName`/
`npcClassDefaultLevel` in Program.cs are the fallback for that one case.

**Deliberately excluded** two other spawner families found in the same
search, both real but out of scope here: `_Quest`-suffixed /
`BP_QuestTargetNPCSpawner_*` variants (key off `HumanName` + a
`SpawnerRuleClass` gated on quest state, e.g.
`BP_MonoNPCSpawner_StrongOldMan02_C` derives from `BP_MonoNPCSpawner_Quest_C`
and links `QuestId.RowName: "Sub_StrongOldMan02"` - functionally the same
"go here" concept the Quests feature already covers, so including them
would just duplicate that), and `Spawner/HumanNPCBoss/*` (the human Bounty
target spawners, already covered by `bosses.py`/`DT_BossSpawnerLoactionData`
- confirmed by finding `BP_MonoNPCSpawnerBossBase_BOSS_DarkTrader` in that
folder, a *boss-tier* DarkTrader encounter distinct from the regular shop
NPC).

**Category classification**, from real data:
- **Black Market**: `uniqueName` starts with `"DarkTrader"` - 2 distinct
  individuals resolved (`DarkTrader` at 3 spawn points, `DarkTrader03` at 1).
- **Dog Coin**: `uniqueName == "MedalTrader"` - confirmed via
  `DT_ItemShopSettingData`'s `"Medal_Shop_1"` row, whose `CurrencyItemID` is
  literally `"DogCoin"` (not "Medal", despite the row/NPC name - see the
  Quests-era investigation notes elsewhere in this file). 1 individual, 4
  spawn points.
- **Trader (general store)**: **zero confirmed entries.** Real evidence the
  concept exists — `BP_NPC_SalesPerson*`/`PalDealer*` character Blueprints,
  38 shop configs in `DT_ItemShopCreateData`, Bobby's `DT_UniqueNPC` row
  having `OneTalkDTName: "ItemShop"` — but none of those specific named NPCs
  (`Bobby`, `Johnson`, `InnkeeperA`, `Doctor`, `MerchantwithPAL`,
  `DarkTrader02`/`04`) resolved to a spawner anywhere in this scan (checked
  both spawner-class broadening passes). Most likely placed inside an
  interior sub-level not covered by the open-world persistent+streamed scan
  - not chased further. Shown in the frontend as a disabled "(none found)"
  row rather than hidden, so the gap is visible, not silently absent.
- **General NPC**: everything else with a resolved position (~82 entries) -
  regional-people/Farmer/Scholar/Breeder/Ranger/Nomad variants,
  BountyNavigator_*, Yamishima guides, Head_of_Village, Police_dependable,
  etc. Excludes two prefixes that resolved with real positions but aren't
  real talkable characters: `U_Reward_*` (invisible reward-dispenser stubs
  reusing the NPC system) and `U_Emote_location_*` (background idle-emote
  trigger points) - same judgment call as the Quests reward-stub situation.

Only 90 of `DT_UniqueNPC`'s 216 rows resolved to a position in the end -
real, not exhaustive. No orphan `UniqueName` values (every spawner's key
matched a real `DT_UniqueNPC` row, 0 mismatches across both full scans) -
the join mechanism itself is fully trustworthy, coverage of the roster is
just partial.

**Icons**: per-individual real portraits wherever one exists, not just a
per-category fallback - `PalIcon/Normal` turns out to have near-complete
per-archetype coverage (e.g. `U_Female_Farmer01_v01` -> real file
`T_Female_Farmer01_v01_icon_normal`, found by stripping the `U_` prefix and
matching case-insensitively against the real game file list - casing isn't
consistent, e.g. `T_Male_Scholar01_v02_Icon_normal` capitalizes "Icon" and
nothing else does). Zoe (`GrassBoss`) has her own dedicated portrait,
`T_Human_GrassBoss_icon_normal` - visually confirmed to genuinely be her
(pink/white hair, black beanie, matches the known "Zoe & Grizzbolt" Tower
design), not assumed from the name. Police-flavor names
(`Police_dependable`/`Police_WarningOilrig`/`DesertPolice*`/`VolcanoPolice*`)
share `T_Police_icon_normal` (a PIDF officer, visually confirmed) - no
per-individual portrait exists for these specifically, but a real "an
officer" look beats the generic fallback. Final fallback (43 of 90 current
rows - `Head_of_Village`, `BountyTrader`, `BountyNavigator_*`, `ArenaShop`,
village/guide flavor names, etc.) is `T_MobuCitizen_Male_icon_normal`, the
game's own generic-villager archetype. `DarkTrader01_icon_normal` (Black
Market) is the one category-level override, applied unconditionally
regardless of the specific `DarkTrader`/`DarkTrader03` identity. Categories
are still visually told apart by marker border color too (same trick
`.bounty-marker`'s red border already uses) - color-codes the category,
portrait shows the individual. 37 distinct icon files exported in total.

**MedalTrader (Dog Coin) uses `T_Male_DarkTrader02_icon_normal`, not a
MobuCitizen fallback** - a real user report that Dog Coin "looks like Black
Market" turned out to be correct, not a bug: her own Blueprint
(`Character/NPC/Fat/BP_NPC_MedalTrader`) has its `CharacterMesh0` component
set to `SK_NPC_Male_DarkTrader02` directly - she's a literal reskin of the
DarkTrader02 model (confirmed against a real in-game screenshot of "Medal
Merchant" - same hooded, masked silhouette, an olive/yellow robe where
Black Market's is dark). No dedicated "MedalTrader"-named icon exists
anywhere in `PalIcon/`, so `T_Male_DarkTrader02_icon_normal` is her actual
real look, not a placeholder - the visual similarity to Black Market is a
genuine fact about the game's own asset reuse, not something to "fix"
further. Border color (gold vs. purple) is what actually distinguishes the
two categories at a glance.
Originally used `T_Male_Trader01_v04_icon_normal` (the "Wandering
Merchant" SalesPerson's own real look) as the generic fallback - switched
away once that turned out to be a specific, distinct NPC identity (see the
Trader investigation below), not a generic look.

### Trader ("general store" NPCs) — confirmed to exist, position mechanism not found

`BP_NPC_SalesPerson*`/`BP_NPC_PalDealer*`/`BP_NPC_Recruiter` are real
character Blueprints (`Character/NPC/Shop/`), with real portrait icons
confirmed by the user in-game: **red coat = item trader** ("Wandering
Merchant", `T_SalesPerson_icon_normal`), **green coat** = a second item-trader
variant (`T_SalesPerson_Green_icon_normal` - *not* the Pal trader, contrary
to the user's first guess), **blue coat = the Pal trader**
(`T_PalDealer_icon_normal`, the one actually named "PalDealer"). None of
this trio resolved to a placeable map marker despite an unusually thorough
search - documenting the full trail so it isn't re-walked:

- **Not spawned via the `BP_MonoNPCSpawner` family** (the mechanism that
  works for DarkTrader/MedalTrader) - the full non-quest asset listing (35
  files) has no SalesPerson/PalDealer/Recruiter member, and the full
  persistent+streamed scan for that family (110 resolved NPCs) has zero
  matches for these identities.
- **Not placed directly as their own actor** - a full persistent+streamed
  scan for `BP_NPC_SalesPerson*`/`BP_NPC_PalDealer*`/`BP_NPC_Recruiter*`
  placed as their own class anywhere in the world: 0 found.
- **Not in any spawn/lottery DataTable** - content-searched
  `DT_PalSpawnerPlacement` (8253 rows, the master spawner-placement table -
  this IS where the boss-tier `BOSS_DarkTrader`/`BOSS_Male_Trader01/02/03`
  bounty spawners live, confirming the table and method both work),
  `DT_PalWildSpawner` (1691 rows), and 6 other spawn/lottery tables for
  "SalesPerson"/"PalDealer"/"Recruiter"/"Wander": zero matches anywhere.
- **Not nearby either** - a real user-reported sighting (a "Wandering
  Merchant", red coat, Level 12) at in-game HUD coords (77, -475) converts
  to within ~750 raw units of Small Settlement's known exact position (same
  precision class as every other HUD cross-check in this pipeline - this
  location is confirmed correct). Every `DT_PalSpawnerPlacement` row within
  30,000 units of that point (105 rows) is a normal wildlife
  (`BP_PalSpawner_Sheets_*`) or dungeon spawner - nothing merchant-related,
  even searching by proximity instead of by name.

**Why, per the user (who plays on this server)**: these are wild spawns,
capturable exactly like a Pal (captures into the Pal Box, deployable to a
base to buy/sell from) - the user described the mechanic before this was
independently confirmed by save data. **Level.sav's `CharacterSaveParameterMap`
confirms the "captured" half exactly**: a real recruited `SalesPerson_Wander`
instance (Level 40, a different individual from the Level 12 wild one
above) has `FriendshipPoint`, `FriendshipBasecampSec`, `OwnerPlayerUId`,
`OwnedTime`, and a `SlotId` into a container - the identical schema a
captured Pal uses, and **no `Location`/transform field at all**. Once
captured, this is per-player base-worker state, not a world position - the
same category of thing as a Guild Base (`backend/parse.py`'s
`load_guild_bases`, read live each refresh, not baked into extracted
static data), not a fixed NPC like DarkTrader.

**Per the user: these spawn once when the dedicated server boots (or the
save loads, in single-player) and do NOT respawn if killed until the world
restarts.** That, combined with the total absence from every static
placement/spawn table checked above, means the wild (pre-capture) spawn
location is almost certainly chosen by procedural/scripted logic at world
boot (e.g. a hardcoded `SpawnActor` call in Blueprint graph/K2 bytecode)
rather than being a DataTable row or a placed actor - a fundamentally
different and harder problem than every other extraction in this pipeline,
all of which read serialized *properties*, not compiled *graph logic*.
**Not pursued further** - real Blueprint decompilation would be required,
which is out of scope for this pipeline as built. Trader remains an
empty/unpopulated category in `npcs_static.json`, shown in the frontend as
a disabled "(none found)" row rather than silently absent.

**Follow-up dig (position still not found, but two real wins on identity/
inventory) - picking this back up should start here, not from scratch:**

- **`DT_NPCTalkFlow` confirms `"SalesPerson_Wander"` is a real, intentional
  row key** (`SoftTalkFlowAsset` -> `FABP_CommonItemShop`, the generic shop
  dialogue graph) - and surfaces siblings never found via any placement/
  spawner search: `SalesPerson_Desert`, `SalesPerson_Desert2`,
  `SalesPerson_Volcano`, `SalesPerson_Volcano2`, plus `NPC_Dungeon_Shop`
  (a dungeon-interior variant) and `MedalTrader` itself (->
  `FABP_CommonItemShop_WithoutSell`, confirming Dog Coin's NPC is a
  buy-only shop, no sell option - matches a currency-exchange vendor, not
  a general trader). All the SalesPerson_* variants point to the *same*
  dialogue graph, so "which region flavor" isn't decided by the dialogue
  system either - it's just consumed by it. This means there are likely
  at least 2 more wild-spawn zones (Desert, Volcano biomes) beyond the
  Small Settlement one the user found, doubling as confirmation this is a
  real, designed, per-biome mechanic rather than a one-off.
- **Inventory mechanism, confirmed and extractable if positions are ever
  found**: `BP_NPC_SalesPerson`'s own `BP_PalShopVenderDataComponent` has
  `itemShopLotteryType: EPalShopLotteryType::SimpleLottery` and
  `itemShopSimpleLotteryTableName: "TestTable_2"` (a row key into
  `DT_ItemShopLotteryData`) - inventories are randomly rolled from a
  lottery table per restock (`ItemShopRestockMinute`/`PalShopRestockMinute`:
  48), not a fixed list. This is the "different inventories" the user
  described, and is a real, separate data pipe from the position problem -
  worth wiring up on its own even before/if position is ever solved.
- **Still not found**: no `UniqueName`/`TalkFlowId`-style property exists
  on `BP_NPC_SalesPerson`'s own CDO that would say "I am the Wander vs.
  Desert vs. Volcano variant" - and this identifier is confirmed NOT a
  `BP_MonoNPCSpawner`-family instance (already exhaustively scanned, 110
  resolved `UniqueName` values, none of the SalesPerson_* variants among
  them). Whatever assigns identity + position to a placed instance remains
  outside every static-data mechanism checked so far - still consistent
  with the boot-time-procedural-spawn conclusion above.

## Dungeons

Two independent mechanisms live under this one section: open-world Dungeon
Portal *entrance positions* + live active/inactive state (shipped
2026-07-19), and per-entrance *contents* — the actual enemy/loot roster
(shipped 2026-07-22). Only the second is new; the first predates this file
even having a dedicated Dungeons section (it was only documented in
`backend/dungeons.py`'s own docstring until now).

### Entrances + active/inactive state

- **Placed actor class** `BP_DungeonPortalMarker_<Biome>_C`, 157 instances
  across 11 biome variants (Desert, Forest, Grass1, Sakura, Skyland, Snow,
  Viking/Viking_B/Viking_C, Volcano, Yakushima — "Viking" is the
  Sakurajima/Darkisland snow-viking biome). All 157 live in the persistent
  level (`PL_MainWorld5`), no `_Generated_` World Partition scan needed —
  matched by a `BP_DungeonPortalMarker_` class-name prefix scan rather than
  hardcoding each biome variant, so a future biome wouldn't silently drop.
- **No per-instance display name or DataTable row reference** exists,
  unlike every other placed-actor category in this pipeline — only the
  biome baked into the class name (before this pass; see Contents below for
  what's now joined on top). No per-player "unlocked"/"cleared" state
  exists either — distinct from the per-instance `RespawnProbability`
  override seen on some instances' `EditSpawnParameter` (spawn-table
  tuning, not a player-visible flag).
- **Live world-shared active/inactive state**: contrary to what was first
  assumed when this shipped, `worldSaveData.DungeonPointMarkerSaveData`/
  `DungeonSaveData` track exactly which markers currently have a dungeon
  spawned, re-read each 30s refresh like Guild Bases (`parse.
  load_dungeon_marker_state`, `refresh.py`'s `DUNGEONS_STATE_OUTPUT`).
  `server.py`'s `/api/dungeons` merges that onto the static position list by
  id (dashes-stripped/uppercase). Because there's still no per-item
  *collectible* state, the frontend keeps a single show/hide-all toggle
  (not the per-item checklist every other section uses) over a list
  filtered to only the currently-active entrances, with an "active/total"
  header count — falls back to showing everything if the backend doesn't
  know the live state yet (`state_known: false`, e.g. right after a restart
  before the first refresh cycle).
- **Icon**: the game's own compass icon, `T_icon_compass_dungeon` — same
  "prefer the game's real icon" rule as Towers/Watchtowers/Waypoints/Bases.

### Contents (per-SpawnAreaId enemy/loot roster)

Fully solved via a throwaway scratch project (`extractor/ScratchDungeon`,
deleted after verification per this file's usual cleanup convention) before
being folded into the permanent `extractor/PalExtract/Program.cs` pipeline.

- **The join chain, end to end**: each placed marker instance resolves to a
  `SpawnAreaId` — either an instance-level `SpawnAreaIds` property override
  (`[{Key: "<id>"}]`, confirmed real on a minority of instances) or, absent
  that, the biome *class's own* CDO default (same property name/shape on
  the `Default__` object). `DT_DungeonEnemySpawnDataTable`
  (`Pal/Content/Pal/DataTable/Dungeon/DT_DungeonEnemySpawnDataTable`) has
  exactly one row per `(SpawnAreaId, RankType)` combo (confirmed: 59 rows,
  59 unique combos, zero duplicates — `WeightInSpawnAreaAndRank` is
  irrelevant to this extraction, not a second weighted-selection layer on
  top of a row's own spawner). Each row's `SpawnerBlueprintSoftClass` points
  at a Blueprint whose CDO has a `SpawnGroupList` array — each entry a
  weighted group of `{Weight, PalList: [{PalId.Key, NPCID.Key, Level,
  Level_Max, Num, Num_Max}]}`.
- **Dead end, don't repeat**: `DT_PalWildSpawner` (1691 rows) looked like
  the obvious roster join by analogy with regular wildlife spawners, but
  has zero overlap with any dungeon `SpawnerName`/`SpawnAreaId` — the real
  join is entirely through the enemy spawn table + spawner Blueprint CDO
  above, not this table.
- **Only 14 real `SpawnAreaId`s exist**, derived generically (not
  hardcoded) as whatever distinct `SpawnAreaId` value actually gets
  resolved onto at least one of the 157 placed markers: Grass001, Grass002,
  Forest001, Forest002, Dessert001 (double-s, a real typo baked into the
  game's own row key), Volcano001, Snow001, Sakura001, Viking001,
  Yakushima001, Skyland001, Island001, Island002, Island003.
  `DT_DungeonEnemySpawnDataTable` also has rows for `TestDebug01` and
  `Meadow01` — both dev-only/orphaned, never resolved onto by any placed
  marker — restricting to the real, marker-referenced set drops them
  automatically with no skip-list needed.
- **Island001/002/003 are NOT separate biomes** — they're a variety-
  injection override pool that some Grass1 instances get switched to via
  their own instance-level `SpawnAreaIds` override (confirmed: 6 Grass1
  instances override to Island001, 6 to Island002, 6 to Island003, on top
  of the separate 14 Grass1 instances overriding to Grass002 instead).
  Their Boss and Normal tiers (the only two tiers any Island area has) are
  content-identical to Grass001's, byte-for-byte after key-order
  normalization — same spawner Blueprints referenced, confirmed
  programmatically, not eyeballed. They do get their own real, distinct
  display name though (see below) — "Isolated Island Cavern", shared by all
  three, vs. Grass001's own "Hillside Cavern" — so they're worth surfacing
  as their own map/UI entries even though the roster is a duplicate.
- **Per-area display name**: `DT_DungeonSpawnAreaDataTable`
  (`Pal/Content/Pal/DataTable/Dungeon/DT_DungeonSpawnAreaDataTable`) maps
  each `SpawnAreaId` to a `DungeonNameTextId` (e.g. `Grass001` ->
  `NAME_RandomDungeon_grass01`), resolved through the L10N `en`
  `DT_DungeonNameText` table to a real localized name (e.g. "Hillside
  Cavern"). **Not** the same join as the unrelated "Fixed Dungeon"/Sealed
  Realm system, which also reads from `DT_DungeonNameText` but via
  different, non-`NAME_RandomDungeon_*` row keys (`NAME_Dungeon01`..`08`,
  `NAME_FixedDungeon_*`) and mostly-unlocalized "en Text" placeholder junk —
  don't conflate the two despite sharing a table.
- **Yakushima001 is a real Terraria crossover, confirmed via this exact
  lookup**: its `DungeonNameTextId` (`NAME_RandomDungeon_Yakushima01`)
  resolves to the literal localized string `"???"` — not a broken lookup,
  a real, deliberate easter-egg region name, kept as-is. Its trash tiers use
  non-Pal creature names (Green/Blue/Red Slime, "Demon Eye") alongside a
  handful of real Pals (Herbil, Dazzi, Dumud Gild) in the same tier — all of
  these resolve through the ordinary `PAL_NAME_<CharacterID>` /
  `DT_PalMonsterParameter` join with no special-casing needed (confirmed:
  every single Yakushima001 entry resolved a real name, including the
  Terraria-flavored ones).
- **Roster join per pal/human entry**: regular species resolve name via
  `PAL_NAME_<CharacterID>` direct-key lookup into `DT_PalNameText_Common`
  first, falling back to `DT_PalMonsterParameter`'s `OverrideNameTextID`
  indirection if the direct key misses (same order `ResolvePalShopPool`
  already uses for Pal Dealer/Black Market pools) — icon via the same
  `DT_PalCharacterIconDataTable`-keyed-by-`Tribe` join Bosses/shop pools use,
  exported to the existing shared `frontend/assets/boss_icons/` dir (no
  separate output folder for this feature). Human entries (`NPCID` set,
  `PalId: "None"`, `RankType: NPCHuman`) resolve name via
  `DT_PalHumanParameter` + `DT_HumanNameText_Common`, joined directly by the
  roster's own `NPCID` (not a `BOSS_`-prefixed `SpawnerID` like Bounty's
  human bosses use) — all 32 real human roster entries across every area
  resolved a name this way, zero misses. **No icon exists for these**
  though — `DT_PalBossNPCIcon` (Bounty's own icon table) is keyed by
  boss-tier `SpawnerID`s, not these generic dungeon-trash `NPCID`s, and no
  equivalent table was found — `icon: null` for every `isHuman: true` entry,
  by design, not a gap worth chasing further (this is a minor/rare category:
  32 entries total, 3 of 14 areas).
- **Correction (2026-07-22): `WindChimes`/`Icewitch` were never actually
  unresolvable — a real user recognized `WindChimes`' exported portrait
  in-game ("looks like Hangyu") and that tip cracked it.** Both are a
  **casing mismatch between the dungeon spawner Blueprints' own baked-in
  `PalId.Key` and the real `DT_PalMonsterParameter`/`DT_PalNameText_Common`
  row keys** — the same class of gotcha this file already flags repeatedly
  elsewhere (`BOSS_Police_old` vs `BOSS_Police_Old`, etc.), just missed here
  the first time because `DungeonPalName`/`DungeonPalIcon` were the only two
  lookups in this whole pipeline still doing a case-*sensitive* exact-match
  against `monsterRows`/`palNameRows` instead of a case-insensitive one
  (`palIconLookup` already used `StringComparer.OrdinalIgnoreCase`, these
  two didn't). Confirmed by direct query: `BOSS_WindChimes`'s own
  `OverrideNameTextID` is `"PAL_NAME_WindChimes"` (capital C) but the real
  text row key is `PAL_NAME_Windchimes` (lowercase c) → **"Hangyu"** (a
  `WindChimes_Ice` variant also exists → "Hangyu Cryst", unused in any
  current dungeon roster). `Icewitch` is worse-cased in the spawner data —
  `BOSS_Icewitch`/`Icewitch` (lowercase w) vs. the table's real
  `BOSS_IceWitch`/`IceWitch` (capital W) — same species Yakushima001's
  *correctly*-cased `IceWitch` Normal-tier entry already resolved fine, to
  **"Icelyn"**; its icon (`Pal_IceWitch.png`) already existed on disk from
  that entry, just needed reusing. Fixed in `Program.cs`'s
  `DungeonPalName`/`DungeonPalIcon` by building case-insensitive
  `monsterRowsCI`/`palNameRowsCI` dictionaries (mirroring `palIconLookup`'s
  existing pattern) instead of switching `monsterRows`/`palNameRows`
  globally (those two are used case-correctly everywhere else in this file
  — no need to risk changing behavior elsewhere for a fix that's only
  needed in this one section). The live `data/dungeon_contents_static.json`
  was hand-patched to the corrected values rather than paying for a full
  multi-section pipeline rebuild (effigies/journals/notes/schematics all
  re-run their own expensive World Partition scans) just for 2 species'
  names — the next real full extractor run will reproduce the same result
  from the fixed code, this was just a shortcut to ship the correction
  immediately. **Lesson: don't declare "real, confirmed absence" from an
  extractor coming back empty without checking whether the lookup itself
  might be case-sensitive first** — this file already carried that warning
  for other joins; it just hadn't been applied to this section yet.
- **"Guaranteed" vs. "random pool" is computed generically per tier, not
  hardcoded per area**: a tier is `guaranteed: true` only when its merged
  `groups` array has exactly one entry — i.e. its spawner Blueprint's
  `SpawnGroupList` had exactly one group (level-range width alone doesn't
  make a tier "random" for this purpose, only species-pool multiplicity
  does). Verified result: Yakushima001's Boss tier is the *only*
  guaranteed Boss/MidBoss tier among all 14 areas (Eye of Cthulhu, Lv45) —
  every other area's Boss (and MidBoss, where present) is a real weighted
  pool (15-31 candidates). A handful of trash tiers (FishPal on several
  areas, NPCHuman on a couple) are also single-group/guaranteed, which
  falls out of the same generic rule rather than being special-cased.
- **`Normal02`-`05`/`MidBoss02`-`05` merge into one `Normal`/`MidBoss` tier
  bucket** for the frontend — confirmed real (as of this game version) only
  for Yakushima001 (`Normal`/`02`/`03`/`04`, 4 distinct spawner Blueprints —
  cavern/mushroom/hallow variants of its trash tier, no `05`; no area has a
  `MidBoss02`-`05` at all in practice, but the merge rule is applied
  generically rather than hardcoding "only Yakushima has this"). Each
  contributing row's own `SpawnGroupList` groups are concatenated into one
  flat `groups` array — they're independent trash-tier rolls, not weighted
  alternatives of each other, so there's no single correct combined weight
  across rows; concatenating and leaving each group's own intra-row weight
  intact is the simplest faithful representation for a UI that's listing
  "what can appear here," not simulating exact roll probabilities.
- **Only 5 of 14 areas have any `MidBoss` row at all**: Grass001/002,
  Forest001/002, Dessert001. For exactly those 5, the `MidBoss` row points
  at the *same* spawner Blueprint as the area's own `Boss` row — two
  independent boss-tier rolls per dungeon instance for those 5 areas
  specifically, not a display duplicate.
- **Output split, not embedded**: `data/dungeon_contents_static.json` is
  keyed by `SpawnAreaId` (only 14 entries) and written separately from
  `data/dungeons_static.json` (157 entrance positions, each now carrying its
  own resolved `spawnAreaId` field) — embedding the same 14 rosters onto
  every one of the 157 entrances would be pure duplication. `backend/
  dungeons.py`'s `load_dungeon_contents()` / `server.py`'s
  `/api/dungeon_contents` serve it as a second, independent, cache-once
  endpoint; the frontend fetches both and joins client-side by
  `spawn_area_id`, same pattern as every other client-side join in this
  codebase.
- **Frontend**: the existing single show/hide-all Dungeons toggle and
  marker rendering are unchanged; markers are now clickable (mirroring the
  Shop modal's NPC-marker click pattern), opening a dungeon contents modal
  styled after (not a new visual language from) the existing Shop modal —
  stacked per-tier sections (Boss/Mid-Boss/Normal/Monster/Fish/Human Enemy,
  whichever a given area actually has) each with a "Guaranteed" or "Random
  pool (N possible)" badge and a card grid reusing the Shop modal's
  `.shop-card` styling. The tooltip gained one optional extra line — a
  one-sentence Boss-tier preview only (not every tier, to keep the tooltip
  short) — degrading gracefully to no extra line if dungeon contents
  haven't loaded yet or (in principle) an area has no Boss tier.
- **Boss/Mid-Boss merge, same day**: confirmed via the live API that every
  area with a `MidBoss` tier at all (Grass001/002, Forest001/002,
  Dessert001 — the same 5 already noted above as rolling "two independent
  boss-tier encounters") has `MidBoss.groups` byte-identical to
  `Boss.groups` — literally the same weighted pool, not two different
  rosters. The modal originally rendered both as separate stacked sections
  with the exact same 15-31-card grid twice, which just read as confusing
  duplication. Fixed by comparing `JSON.stringify(tiers.Boss.groups) ===
  JSON.stringify(tiers.MidBoss.groups)` in `openDungeonModal` and, when
  true, dropping the `MidBoss` section and relabeling `Boss` to "Boss & Mid-
  Boss (2 rolls)" — conveys that two independent picks from this same pool
  actually spawn, rather than silently implying only one. The equality
  check (not a hardcoded area-name list) means this stays correct
  automatically if a future game update ever gives some area's MidBoss its
  own distinct pool — it'd just fall back to two normal separate sections.

## Icon export web-optimization (2026-07-22)

`extractor/PalExtract/Program.cs`'s shared `DownscalePng` helper (used by
every icon exporter — bosses/effigies/schematics/NPCs/dungeon pals) had a
real inefficiency: it only resized+re-encoded when the source texture was
*larger* than the 128px cap, and just returned the raw source bytes
untouched otherwise (`if (image.Width <= maxDim && image.Height <= maxDim)
return pngBytes;`). Since most Pal/NPC portraits are already authored at
exactly 128×128, that early-return meant ~90% of exported icons were never
actually re-encoded at all — shipped as whatever raw PNG `TextureEncoder`
produced straight off the game's own compressed texture data, not tuned for
web delivery.

**Fix**: re-encoding is now unconditional, with `PngCompressionLevel.
BestCompression` + `ColorType.Palette` (8-bit indexed) + a `WuQuantizer`
capped at 256 colors. Measured on 40 real exported `boss_icons` files:
compression-level alone only saved ~2% (the source PNGs were already
reasonably compressed) — the real win is the palette conversion, ~55-65%
smaller. This is technically lossy (every sample file has 1000-3700+ unique
colors before quantization — these are real game-rendered portraits with
soft shading, not flat pixel art, so 256 colors doesn't capture them exactly)
but verified visually safe at actual display size: 3x-zoomed side-by-side
crops of a busy human portrait (`BOSS_Male_Trader01`) and a gradient-heavy
Pal portrait (`Pal_Bastet`) showed no perceptible banding. These render at
28-84px in the UI, never larger — revisit if a future icon type ever needs
a zoomable full-size view. `ExportNoteIcon` (Journal photo thumbnails,
currently unused by the frontend — see the Journals section above) had its
own separate, duplicate inline resize+encode; simplified to just call the
shared `DownscalePng` instead of maintaining two copies of the same logic.

Applied to the ~500 already-exported files on disk directly (a plain PNG
re-encode with the same settings, via a throwaway ImageSharp-only scratch
script — no CUE4Parse/game files needed) rather than paying for a full
multi-section pipeline rebuild, same "patch the artifact, fix the source for
next time" shortcut as the WindChimes/Icewitch correction above. Net result:
the icon subdirectories (`boss_icons`/`npc_icons`/`schematic_icons`/etc, not
counting the map backgrounds below) dropped from ~9.3MB to ~4.7MB, roughly
halved.

**Near-miss, caught before any commit — worth flagging for next time**: the
first pass of that batch re-encode script walked `frontend/assets/**/*.png`
unscoped, which also matched `frontend/assets/map.png` (27.5MB) and
`frontend/assets/tree.png` (39.6MB) — the actual 8192×8192 `T_WorldMap`/
`T_TreeMap` Leaflet background textures the whole map renders on top of,
*not* icons, sitting directly in `frontend/assets/` rather than a `*_icons/`
subdirectory. The script's blanket 128px resize cap shrank both down to
literal 128×128 thumbnails before the mistake was caught (`git status`
showed both as modified with drastically smaller sizes, which is what
raised the flag) — `git checkout -- frontend/assets/tree.png
frontend/assets/map.png frontend/assets/tree_thumb.png` restored the
originals byte-for-byte since they were already git-tracked and hadn't been
committed over. **These two backgrounds were correctly left un-reprocessed
in the end** — resizing them would break Leaflet's `CRS.Simple` coordinate
alignment at every zoom level (the whole map's coordinate system is
calibrated against their exact pixel dimensions, see `backend/coord.py`),
and they were never in scope for an "icon" web-optimization ask in the first
place. **Lesson: when a batch transform walks a directory tree by
extension/glob rather than an explicit known file list, double-check the
glob doesn't also match unrelated large assets that happen to share the
same file type** — `*.png` caught the map backgrounds because nothing about
the glob itself distinguished "128×128 UI icon" from "8192×8192 rendered
map layer," only their directory placement did, and the script didn't check
that.

**Follow-up, same day: `map.png`/`tree.png` themselves optimized properly
(not skipped forever just because the first attempt was scoped wrong).**
27.5MB/39.6MB PNGs (both fully opaque, no alpha channel) re-encoded as
lossy WebP at quality 90, same exact 8192×8192 pixel dimensions — **format
change only, not a resize**, so `backend/coord.py`'s `MAIN_TEXTURE_SIZE`
and `frontend/index.html`'s `MAIN_SIZE` constants (both hardcoded 8192,
driving every marker's pixel-space placement) didn't need to change.
Result: 4.8MB/7.1MB, ~82% smaller, `frontend/index.html`'s two
`L.imageOverlay()` calls updated to `/assets/map.webp`/`/assets/tree.webp`.
Verified safe (not just measured smaller) two ways: (1) a 700×700 crop from
each at native 1:1 pixel scale, viewed side-by-side against the PNG
original, showed no visible banding/blockiness even in fine gravel/rock
texture — the single most demanding comparison, since normal map usage is
zoomed out further than 1:1; (2) live in a real headless-Chromium session
zoomed to `maxZoom` (2), no failed asset requests, no console errors. Tried
plain `PngCompressionLevel.BestCompression` first (only ~5-6% smaller — these
PNGs were already reasonably compressed, same finding as the icon pass
above) and lossless WebP (~33% smaller, zero quality loss) before deciding
lossy q90 WebP's ~82% reduction was worth it — q80 (~90% reduction) was
tested too and still looked clean, but q90 was picked for extra margin
since the difference in absolute MB saved between q80 and q90 is small
relative to the win either already gets. **No pipeline code needed
updating** — unlike every icon type, `map.png`/`tree.png` were never
produced by `extractor/PalExtract/Program.cs` in the first place (confirmed
by grep — no `T_WorldMap`/`T_TreeMap` export code exists there), just a
one-off manual CUE4Parse pull per the Architecture section's own wording
("Pulled... textures... as the backgrounds") predating this file's
detailed-documentation habit. If these ever need re-pulling from a newer
game version, redo that manual export, then re-run this same WebP q90
conversion on the result — there's no automated regeneration path for
these two files specifically. `tree_thumb.png` (284KB, confirmed orphaned —
zero references anywhere in `frontend/`/`backend/`) was left alone, out of
scope for this pass; worth a separate cleanup if anyone asks.

## Pal Spawn Locations (wild field spawns, pals only) — 2026-07-22

"Where can I find X in the wild" as flat highlighted circular regions on
the map, not a color-gradient heatmap — an explicit user requirement,
along with "strictly pals only" (no Alpha Bosses, no dungeon spawns, no
human NPC patrols) and defaulting to nothing selected/shown (the *only*
section in this whole app that doesn't default to fully visible — every
other section shows everything until a user opts out; this one shows
nothing until a user opts in, since ~260 species selected at once would
just highlight the entire map and defeat the point).

- **Source table**: `DT_PalSpawnerPlacement` (`Pal/Content/Pal/DataTable/Spawner/`,
  a `CompositeDataTable`, 8253 rows) — the master spawn-point placement
  table for *every* spawner category in the game, not just wild Pals.
  Filtering to `SpawnerType::Common` + `PlacementType::Field` is exactly
  the regular wild-Pal field spawns wanted (~7474 rows). This single
  filter cleanly excludes every other category already covered elsewhere
  or out of scope: `SpawnerType::Common` + `PlacementType::Dungeon` (444
  rows, dungeon trash — already the Dungeon Contents feature),
  `SpawnerType::FieldBoss` (72 rows — the existing "Alpha Bosses" section,
  `backend/bosses.py`/`DT_BossSpawnerLoactionData` — confirms "alphas" in
  the user's own request means exactly this category, the sidebar
  literally labels it "Alpha Bosses"), `SpawnerType::RandomDungeonBoss`
  (245 rows, dungeon bosses, also Dungeon Contents), and
  `SpawnerType::ImprisonmentBoss` (18 rows, a separate boss category, out
  of scope). Each row carries its own `StaticRadius` (world units,
  virtually always 15000.0 but always read per-row, never hardcoded) —
  this maps a spawn point directly onto a circle, no clustering/heuristic
  needed to turn points into "regions."
- **Species/level join**: each placement row's `SpawnerName` field against
  `DT_PalWildSpawner` (1691 rows) **by that table's own `SpawnerName`
  property, not the dict row-key** — confirmed 7403/7474 match via the
  field vs. only 73/7474 via the key, the same "don't get this backwards"
  gotcha as `DT_BossSpawnerLoactionData.SpawnerID` elsewhere in this
  pipeline. **Multiple `DT_PalWildSpawner` rows can share one
  `SpawnerName`** — a weighted candidate pool, one row per candidate (NOT
  a single row containing a groups array like the dungeon tables' shape) —
  each candidate has up to 3 simultaneous slots (`Pal_1/NPC_1/LvMin_1/
  LvMax_1` through `_3`).
- **"Strictly pals only" filtering** (the user's explicit requirement): a
  slot counts only when `Pal_N` is a real species — excludes `Pal_N ==
  "None"` and `Pal_N == "RowName"` (a literal leftover template-default
  string found on at least one real stub row, e.g. `grass_FBOSS_1_1`,
  which also has `Weight: 0.0` — both guards kept, not relying on the
  weight check alone) — and excludes any slot where `NPC_N` is set instead
  of `Pal_N` (human, not a Pal — the 33 `RadiusType::NPC` rows hint some
  Field spawners are human patrols using these NPC slots; this is the
  "other enemies" half of the user's requirement).
- **Species name/icon resolution reuses the exact same case-insensitive
  logic already fixed for Dungeon Contents** (`DungeonPalName`/
  `DungeonPalIcon` generalized to `ResolvePalName`/`ResolvePalIcon` —
  same `monsterRowsCI`/`palNameRowsCI` case-insensitive dictionaries, not
  a second copy-pasted case-sensitive implementation) — keyed by the base
  non-`BOSS_` CharacterID (e.g. `"Alpaca"` not `"BOSS_Alpaca"`, since wild
  field spawns are already the common form). Icons land in the existing
  shared `frontend/assets/boss_icons/` directory, reusing whatever's
  already exported from Bosses/Dungeons where species overlap.
- **A genuine remaining resolution gap exists and is correctly dropped, not
  fabricated**: of 262 distinct species reachable from Field+Common
  placements, 261 resolve a real name cleanly. The sole miss,
  `Male_NinjaElite01`, is a human character mistakenly placed in a `Pal_2`
  slot (its sibling `Pal_1` slot on the *same row* correctly uses `NPC_1`
  for the human `Male_Ninja01`) rather than `NPC_2` — a real, if minor,
  upstream data inconsistency in the game's own table, not an extraction
  bug. It fails name resolution and is dropped rather than shown with its
  raw internal id as a fake species name, which would violate "strictly
  pals only."
- **Extraction totals** (logged, confirmed via `extractor/ScratchSpawns`
  before shipping, same "verify before trusting an extractor came back
  empty/wrong" discipline as Dungeon Contents' casing-bug correction):
  7474 Field+Common placement rows, 261 distinct resolved species, 57208
  total species-location entries (most spawn points offer more than one
  possible species — a location's own weighted candidate pool, same
  concept as a dungeon's Boss pool, just per-field-spawn instead of
  per-dungeon), only ~7310 distinct physical coordinates (confirming most
  of that 57208 fan-out is real multi-species overlap at shared spots, not
  a duplication bug). One species, `MimicDog` ("Mimog" — the treasure-
  chest-disguise Pal), has an outlier 5317 locations — genuinely correct,
  not a bug: Mimog is a real low-weight "any generic wild encounter might
  secretly be a mimic" mechanic bundled onto a huge share of the game's
  own spawner rows, confirmed by checking that its location entries really
  are 5317 *distinct* coordinates (not a repeated/duplicated handful) with
  wildly varying level ranges matching whatever normal spawn was rolled at
  each spot.
- **Coordinate/radius conversion**: `backend/coord.py` gained
  `radius_to_pixels(world_radius)` (`world_radius / _WORLD_UNITS_PER_PIXEL`
  — a pure linear scale, no offset needed unlike `locate()`, and shared
  between main-map and Tree-inset spawns since both use the same
  units-per-pixel factor) for use with Leaflet's `L.circle`. **Confirmed
  empirically, not just assumed, that `L.circle`'s `radius` option is in
  the same flat coordinate-unit space as marker lat/lng under this app's
  `L.CRS.Simple` setup** (no geographic projection to complicate it) — a
  circle's on-screen pixel radius was checked at two different map zoom
  levels (`zoom -4` → 5px, `zoom -2`, 2 levels in → 21px, ≈4×, matching
  the expected 2² zoom-scale factor) — proving `L.circle` (real
  world-space radius, scales with zoom) was the right choice over
  `L.circleMarker` (fixed screen-pixel radius, would NOT have scaled).
- **Backend**: `backend/pal_spawns.py`'s `load_pal_spawn_locations()`
  loads the static JSON once at startup (fully static, world-shared, no
  per-player/per-refresh state at all — spawn point *definitions* aren't
  live save data) and does the pixel/radius conversion at load time (not
  baked into the static JSON) so it stays in sync with `coord.py` if the
  map bounds/texture size ever change, same pattern as every other loader.
  Served via `/api/pal_spawn_locations`.
- **Frontend**: a new top-level sidebar category, "Pal Spawns", alongside
  the existing "Points of Interest"/"Collectibles"/"Combat"/"Quests"
  groups — deliberately **not** built on the shared
  `createTypeChecklistSection()` factory (that renders point markers
  grouped by species with per-player collected state; this renders
  potentially thousands of translucent area circles per species with zero
  per-player state — different enough on both axes to warrant bespoke
  code), but reuses `.type-row`'s row markup/CSS for visual consistency
  with every other checklist in the app. A plain substring search box
  (new UI pattern for this app — no other section has 260+ entries, so no
  prior section needed one) filters the visible rows without touching
  selection state. Each checked species gets a small colored swatch (a
  fixed 10-color palette cycling by check-order) so multiple simultaneous
  selections stay visually distinguishable; unchecking removes only that
  species' circles. **Uses `L.canvas()` as the circle layer's renderer,
  not Leaflet's default SVG** — a popular species (Mimog: 5317 circles at
  once) would be far more sluggish as one `<path>` element per circle than
  as canvas draw calls. Selection state persists across a page reload via
  the existing `uiState`/`localStorage` mechanism, but with **inverted
  polarity from every other section's persistence** — `sectionUnchecked`/
  `saveSectionUnchecked` store the *unchecked* exceptions (so new entries
  default visible, matching those sections' own "everything visible by
  default" behavior) — Pal Spawns instead stores the *checked* set
  directly (`uiState.palSpawnsChecked`), matching its own opposite
  "nothing visible by default" behavior. Deliberately excluded from
  `ALL_MASTER_TOGGLE_IDS` so the existing Show All/Hide All buttons and
  `?isolate=`/`?view_as=` screenshot-mode bootstraps never touch it
  either — Show All lighting up all 260 species would be exactly the
  "entire map highlighted" outcome the user explicitly didn't want, even
  as a side effect of an unrelated button.
- **Verified live in a real browser, not just via curl**: zero circles
  with nothing selected; selecting a 20-location species (Lullu/
  LeafPrincess) created exactly 20 `L.Circle` layers, positioned at
  real world coordinates that `map.fitBounds()` on them correctly framed
  as one tight, blended cluster in the Sakura region (screenshotted);
  selecting a second species (Pierdon/RockBeast) simultaneously produced
  two visually distinct colors (`rgb(224,108,117)` vs. `rgb(97,175,239)`,
  confirmed programmatically, not just eyeballed); zero console errors
  throughout.

### Mimog Effigy ("Capture Power" relic) progress badge — same day, follow-up

The user provided a real gameplay mechanic that isn't documented anywhere
else in this codebase: capturing 5 of a given Pal species awards one Mimog
Effigy (the game's own internal name for this relic type is "Capture
Power" — confirmed below). Investigated directly against a real player
save (`data/saves/Players/*.sav`, already locally cached by the refresh
loop, no fresh SSH pull needed) rather than guessing a field name:

- **`SaveData.RecordData.PalCaptureCount`** — a flat `NameProperty`→
  `IntProperty` map, keyed by the exact same base CharacterID codenames
  `pal_spawn_locations_static.json` already uses (`"PinkCat"` for Cattiva,
  `"MimicDog"` for Mimog — confirmed exact key match, no casing gotcha
  this time). **Lifetime, uncapped, monotonically non-decreasing** — real
  values seen well past 5 for well-farmed species (`"ChickenPal": 106`,
  `"CatMage": 50`) — captures are never "spent," so `count >= 5` is a
  stable, permanent "reward already earned" signal per species.
- **A real dead-end ruled out before trusting the right field**:
  `RelicPossessNumMap`'s `"EPalRelicType::CapturePower"` entry looked like
  an obvious candidate (its value, 54, is even the internal name match for
  "Capture Power") — but it's confirmed (via `RelicPossessNum`, a
  top-level duplicate of that same figure) to be the player's **current
  unspent currency balance**, which drops when spent at a Statue of Power
  — same "resets on turn-in" caveat this file already documented for
  `RelicPossessNumMap` under Effigies above, just now applied to the
  wrong assumption of what it could be reused for here. In this same real
  save, 182 distinct species already have `PalCaptureCount >= 5` while the
  spendable `CapturePower` balance sat at only 54 — proof the two numbers
  track genuinely different things, not just the same value observed at
  different times.
- **`bCaptureCompletionRelicFixupDone`** (a boolean "one-time migration
  applied" flag) independently confirms the internal system name really is
  "CaptureCompletionRelic," matching the user's description exactly.
- **Backend**: `backend/parse.py`'s `load_pal_capture_counts()` reads the
  map directly (species codename → count). `/api/pal_spawn_locations` now
  accepts the same `?view_as=<uid>` pattern as `/api/relics`/`/api/bosses`/
  etc., overlaying `capture_count`/`effigy_complete` (`count >= 5`) onto
  each species entry only when a player is selected — omitted entirely
  otherwise (`capture_known: false`), same "don't imply false state when
  the answer is actually unknown" rule as `defeat_known` elsewhere.
- **Frontend**: each Pal Spawns checklist row gained a small badge — a
  green ✓ once complete, an amber "`N/5`" while in progress, nothing when
  no player is selected or the species has zero captures. Refreshes on the
  same `reloadCollectionState()` cycle every other per-player section uses
  (view_as switch + the 10s poll), but **doesn't rebuild the checklist
  itself** on every refresh (the species roster/checkbox state never
  changes, only the badge content does) — a lighter `updatePalSpawnEffigyBadges()`
  path updates just the badge `<span>` per row in place, leaving
  checkboxes/circles/search-filter state untouched.
- **Verified against the real save used for the field investigation
  itself**: selecting that same player in "View As" showed Cattiva (5
  lifetime captures in that save) with the ✓ badge and a tooltip reading
  "Mimog Effigy earned (5 Cattiva captured)," and Anubis (1 capture) with
  "1/5" — both matching the raw save data exactly, not just plausible-
  looking output. Confirmed badges are empty with no player selected, and
  that toggling a species checkbox / drawing its circles still works
  unaffected by the new badge column, zero console errors.

**Follow-up, same day: real in-game icon instead of a checkmark, plus
column alignment.** Swapped the plain ✓ for the actual Mimog Effigy item
icon — `frontend/assets/relic_icons/T_itemicon_Relic_12.png`, the same
file the Effigies section already uses, confirmed correct (not Lunaris's,
an earlier icon-table mismatch this project already caught and fixed —
see `backend/relics.py`'s own comment) rather than assumed. Alignment
fix: `.pal-spawn-effigy-slot` changed from `min-width` to an exact fixed
`width`/`height` with flex centering, so the slot occupies identical
screen space whether it's empty, holding the effigy icon, or holding
`"N/5"` progress text — without this, rows would visibly jitter
left/right as content varied. **A real scare during verification, not an
actual bug**: an early screenshot taken 800ms after switching "View As"
showed every icon-based badge completely blank while the text-based
`"N/5"` ones rendered fine — looked exactly like a broken-image
regression. Direct DOM inspection (`img.src`, `naturalWidth`, the
species data actually in `palSpawnData`) showed everything was correct
underneath; a second screenshot at 2000ms showed every icon rendering
properly. Root cause was purely test-script timing — `reloadCollectionState()`
fires ~10 sections' fetches in parallel on a View As switch, and the
image itself needs an extra network round-trip beyond the JSON that
plain text badges don't, so it can lag behind by a few hundred ms on a
busy first load. Not a real user-facing issue (the browser's own image
cache means every occurrence after the first is instant, and a few
hundred ms lag on switching players isn't noticeable at normal
interaction speed) — flagged here so a future "screenshot taken right
after an action looks broken" doesn't get mistaken for a real bug again
without checking the DOM state directly first.

**Real alignment bug, caught by the user from an actual screenshot (not
this project's own verification, which had missed it) — fixed same day.**
The fixed-width effigy slot above only solved *horizontal* alignment
within a row; it didn't address that longer species names (`"Bushi Noct"`,
`"Capriity Noct"`, etc.) were wrapping to a second line, making just those
rows taller than their neighbors — every row's own internal content was
individually correct, but the *row-to-row vertical rhythm* looked jagged
scrolling past a mix of 1-line and 2-line rows, which is what actually
read as "not lining up." Fixed with `.pal-spawn-row .rt-label { white-space:
nowrap; overflow: hidden; text-overflow: ellipsis; min-width: 0; }` (plus
a `title` attribute so the full name is still available on hover) —
forces every row to the same single-line height regardless of name
length. Confirmed via real computed `getBoundingClientRect()` heights
across 30 consecutive rows (all exactly 32px, including the two that
previously wrapped) and a screenshot at the same narrow sidebar width the
user's own screenshot used, scrolled to the same species. **Lesson: this
project's own Playwright verification checked individual elements'
positions/sizes in isolation and missed a real visual defect that was
obvious from one glance at an actual screenshot of a scrolled list — when
a UI issue is about a list "feeling" janky/inconsistent while scrolling,
a full-list screenshot (not just spot-checking a couple of rows'
computed styles) is the right verification, not just after the fact
either — should have screenshotted a longer scroll of mixed-length names
before calling the badge feature done in the first place.**
