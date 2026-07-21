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

**Quest title text is not real extracted display text.** `QuestTitleMsgId`
(e.g. `"QUEST_MAIN_TITLE_BOSS_AURI"`) does not resolve through any
DataTable — an exhaustive DataTable/L10N path keyword search
(QuestText/QuestName/QuestTitle/QuestDesc) came back with zero hits, so it's
presumably a StringTable (a different UE asset type CUE4Parse handles
differently) rather than the DataTable-based `L10N/en` convention every
other name/text lookup in this pipeline uses. Not chased further — `title`
in `quests_static.json` is a readable label mechanically derived from the
quest's own row name (`Main_RayneSyndicate` -> "Rayne Syndicate"), not real
game text. Revisit if real quest title text turns out to be worth it.

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
