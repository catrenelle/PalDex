using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Versions;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse_Conversion.Textures;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Quantization;

var paksDir = @"D:\Steam\steamapps\common\Palworld\Pal\Content\Paks";
var usmapPath = @"C:\Projects\PalworldMap\extractor\Mappings.usmap";

var provider = new DefaultFileProvider(paksDir, SearchOption.TopDirectoryOnly, new VersionContainer(EGame.GAME_UE5_1));
provider.MappingsContainer = new FileUsmapTypeMappingsProvider(usmapPath, StringComparer.OrdinalIgnoreCase);
provider.Initialize();
provider.Mount();
Console.WriteLine($"Mounted {provider.Files.Count} files");

JObject LoadRows(string path)
{
    var exports = provider.LoadPackageObjects(path).ToList();
    var tbl = exports.First(e => (e.Class?.Name.ToString() ?? "").Contains("DataTable"));
    var j = JObject.Parse(JsonConvert.SerializeObject(tbl));
    return (JObject)j["Rows"];
}

// Shared by every icon exporter below (bosses/effigies/schematics/items/
// NPCs/traders/dungeon pals alike) - source textures come straight off the
// game's own assets at whatever resolution they were authored at (item/
// currency icons are 256x256, ~30-36KB each; Pal/NPC portraits already
// 128x128, ~14-19KB), but nothing in this UI ever displays one above ~90px
// (map markers are 28-30px, shop modal cards ~84px). Downscaling here once,
// project-wide, is the same fix already proven necessary for Journal note
// icons (see ExportNoteIcon's own comment - that case was a much more
// extreme 3800x2100 "photo" texture, but the "don't ship 2-3x more pixels
// than anything ever displays" principle is identical). 128px leaves
// headroom above every actual display size without being wasteful.
//
// Web-optimization pass added 2026-07-22: re-encoding is now unconditional,
// not just when a resize actually happens - the old code's early-return
// (`if already <= maxDim, ship the raw source bytes untouched`) meant the
// ~90% of icons authored at exactly 128x128 (every Pal/NPC portrait) never
// got re-encoded at all, so the real win here was never about the resize.
// Measured on 40 real exported boss_icons files: plain re-encoding with
// PngCompressionLevel.BestCompression alone saved ~2% (the raw
// TextureEncoder-produced PNGs were already reasonably compressed) - the
// actual win is ColorType.Palette (8-bit indexed, WuQuantizer max 256
// colors), ~55-65% smaller. That's lossy (every sample file has 1000-3700+
// unique colors pre-quantization, real game-rendered portraits with soft
// shading, not flat pixel art) but verified visually safe at actual display
// size (3x-zoomed side-by-side crops of a busy human portrait and a
// gradient-heavy Pal portrait showed no perceptible banding) - these are
// ~30-84px UI elements, not full-screen art. Revisit if a future icon type
// needs higher fidelity than that (e.g. a zoomable full-size view).
byte[] DownscalePng(byte[] pngBytes, int maxDim = 128)
{
    using var image = SixLabors.ImageSharp.Image.Load<Rgba32>(pngBytes);
    if (image.Width > maxDim || image.Height > maxDim)
    {
        image.Mutate(x => x.Resize(new ResizeOptions
        {
            Mode = ResizeMode.Max,
            Size = new SixLabors.ImageSharp.Size(maxDim, maxDim),
        }));
    }
    using var ms = new MemoryStream();
    image.Save(ms, new PngEncoder
    {
        CompressionLevel = PngCompressionLevel.BestCompression,
        ColorType = PngColorType.Palette,
        BitDepth = PngBitDepth.Bit8,
        Quantizer = new WuQuantizer(new QuantizerOptions { MaxColors = 256 }),
    });
    return ms.ToArray();
}

string ElementString(JToken? t)
{
    var s = t?.ToString() ?? "";
    var idx = s.LastIndexOf("::", StringComparison.Ordinal);
    return idx >= 0 ? s[(idx + 2)..] : s;
}

// ============ Challenge Towers ============
// 9 fixed "Tower" boss encounters (internally "GYM_" bosses, a queue-then-enter
// battle instance, distinct from open-world DT_BossSpawnerLoactionData bosses).
// Region key -> GYM Pal codename, confirmed via each GYM row's own NamePrefixID
// field (e.g. GYM_Horus.NamePrefixID == "GYM_NAME_Desert") - not a guess.
//
// The 9th, WorldTree_Boss (GYM_WorldTreeDragon, "Zenara & Astralym"), was
// originally miscategorized and excluded: its NamePrefixID is
// "GYM_NAME_LastBoss", which reads like "the true final story boss" rather
// than a real queueable Tower — but the game's own data says otherwise
// (IsTowerBoss: true on its DT_PalMonsterParameter row, same as the other 8;
// user confirmed in-game it's a real Tower, in the World Tree DLC zone).
// Position: no exact placed-actor Blueprint exists for the original 8
// (extensively searched and ruled out - see NOTES.md for the full trail:
// DungeonPortalMarker, BossBattleInstanceRoot, a generic "Tower"-classname
// sweep, TowerLockBarrier (unrelated generic lock puzzle, ~66 instances
// elsewhere), and BP_PalRegionTriggerBox_C (a generic biome-boundary marker
// used all over the map, not tower-specific despite being coincidentally
// close to 4 of these)). What we use instead for those 8: in-game HUD map
// coordinates reported directly by a real player standing at each tower,
// cross-checked against palworld.fandom.com's Tower page (independently
// confirmed all 8 - see NOTES.md), converted via the same
// sav_x=map_y*459-123888, sav_y=map_x*459+158000 formula validated elsewhere
// in this pipeline (Penking: 765 units off; these: 1.5-3k units off where
// independently cross-checked against a placed actor, e.g. Grass vs.
// GrassBoss01, Skyisland vs. SkyBoss).
var towerHudCoordByRegion = new Dictionary<string, (double mapX, double mapY)>
{
    ["Grass_Boss"] = (113, -431),
    ["Forest_Boss"] = (37, -312),
    ["Desert_Boss"] = (556, 336),
    ["Volcano_Boss"] = (-587, -517),
    ["Frost_Boss"] = (-146, 448),
    ["Sakurajima_Boss"] = (-597, 205),
    ["Darkisland_Boss"] = (-1294, -1669), // wiki had a typo (missing minus sign) - user corrected
    ["Skyisland_Boss"] = (-423, -1425),
};
// WorldTree_Boss has no HUD-coordinate guess at all — unlike the 8 above, an
// exact placed-actor position exists for it: the "WorldTree_LastBoss"
// fast-travel Waypoint actor (display name "Within the Seal", also
// REGION_WorldTree08's own text) sits at this tower's entrance, sharing the
// same "LastBoss" root as this GYM row's own NamePrefixID
// (GYM_NAME_LastBoss) — not a coincidence, confirmed by proximity to the
// user-supplied rough World Tree map location. Raw coords taken directly
// from that actor (see waypoints_static.json's WorldTree_LastBoss entry) —
// more precise than the HUD-guess method used for the other 8.
var towerExactRawCoordByRegion = new Dictionary<string, (double x, double y)>
{
    ["WorldTree_Boss"] = (501010.0, -748555.0),
};
// Recommended level (Normal / Hard difficulty), from the same user-provided
// screenshots as the coordinates above - not data-mined (no Level field
// exists for these anywhere in DT_PalMonsterParameter or DT_DungeonLevelDataTable,
// unlike DT_BossSpawnerLoactionData's open-world bosses). WorldTree_Boss is
// deliberately absent here (no reported figure yet) - the loop below leaves
// levelNormal/levelHard null rather than guess, same "no fabricated numbers"
// rule as everywhere else in this pipeline.
var towerLevelByRegion = new Dictionary<string, (int normal, int hard)>
{
    ["Grass_Boss"] = (10, 72),
    ["Forest_Boss"] = (20, 74),
    ["Volcano_Boss"] = (30, 76),
    ["Desert_Boss"] = (40, 78),
    ["Frost_Boss"] = (50, 80),
    ["Sakurajima_Boss"] = (55, 80),
    ["Darkisland_Boss"] = (60, 80),
    ["Skyisland_Boss"] = (68, 80),
};
var regionNameRowsChk = LoadRows("Pal/Content/L10N/en/Pal/DataTable/Text/DT_WorldMap_Common_Text_Common");
// Text key for the region display name is normally "REGION_<region>" (e.g.
// "REGION_Grass_Boss"), but WorldTree_Boss's real text row is
// "REGION_WorldTree08" (part of the World Tree zone's own REGION_WorldTree01..12
// numbered list, not the "_Boss"-suffixed pattern the other 8 use) - override.
var regionTextKeyOverride = new Dictionary<string, string>
{
    ["WorldTree_Boss"] = "REGION_WorldTree08",
};
var towerGymByRegion = new (string region, string gym)[]
{
    ("Grass_Boss", "GYM_ElecPanda"),
    ("Forest_Boss", "GYM_LilyQueen"),
    ("Desert_Boss", "GYM_Horus"),
    ("Volcano_Boss", "GYM_ThunderDragonMan"),
    ("Frost_Boss", "GYM_BlackGriffon"),
    ("Sakurajima_Boss", "GYM_MoonQueen"),
    ("Darkisland_Boss", "GYM_SnowTigerBeastman"),
    ("Skyisland_Boss", "GYM_BlueSkyDragon"),
    ("WorldTree_Boss", "GYM_WorldTreeDragon"),
};
// SaveData.RecordData.TowerBossDefeatFlag key per region - confirmed against
// real players' saves (parse.load_defeated_boss_spawner_ids sibling). Note
// the Desert one is "DesertBoss" (single s) despite the monster row's own
// OverrideNameTextID typo "PAL_NAME_DessertBoss" (double s) - verified from
// the real flag key in a player's save, not derived from the (wrong) table
// text.
//
// Volcano_Boss corrected from a previous "VolcanoBoss" guess to the real
// observed key "ElectricBoss": scanning every player .sav's
// TowerBossDefeatFlag turned up "ElectricBoss" as an extra key with no home
// in the previous 8-entry map (present in 5 different players' saves) while
// "VolcanoBoss" appeared in zero — and GYM_ThunderDragonMan (Volcano_Boss's
// own GYM Pal) is Dragon/Electric-typed, the only unmapped tower that fits.
// The old value was apparently never actually cross-checked against a save
// despite the docstring's claim.
//
// WorldTree_Boss's flag is an educated guess, NOT save-confirmed like the
// rest of this map — no player has defeated it yet in the data available,
// so the key has never appeared in any save to confirm directly. "LastBoss"
// follows the same "strip GYM_NAME_ prefix" pattern that correctly predicts
// every other confirmed entry above (e.g. GYM_NAME_Snow -> "SnowBoss") and
// matches the WorldTree_LastBoss waypoint's own naming. Flag this one for
// re-verification once a real save shows it defeated.
var towerDefeatFlagByRegion = new Dictionary<string, string>
{
    ["Grass_Boss"] = "GrassBoss",
    ["Forest_Boss"] = "ForestBoss",
    ["Desert_Boss"] = "DesertBoss",
    ["Volcano_Boss"] = "ElectricBoss",
    ["Frost_Boss"] = "SnowBoss",
    ["Sakurajima_Boss"] = "SakurajimaBoss",
    ["Darkisland_Boss"] = "VikingBoss",
    ["Skyisland_Boss"] = "SorajimaBoss",
    ["WorldTree_Boss"] = "LastBoss", // unconfirmed - see comment above
};

var monsterRowsChk = LoadRows("Pal/Content/Pal/DataTable/Character/DT_PalMonsterParameter");
var palNameRowsChk = LoadRows("Pal/Content/L10N/en/Pal/DataTable/Text/DT_PalNameText_Common");

var towerIconOutDir = @"C:\Projects\PalworldMap\frontend\assets\boss_icons";
Directory.CreateDirectory(towerIconOutDir);
var towerExportedIcons = new HashSet<string>();
void ExportTowerIcon(string assetPathName, string outFileName)
{
    if (towerExportedIcons.Contains(outFileName)) return;
    towerExportedIcons.Add(outFileName);
    var withoutObjectName = assetPathName[..assetPathName.LastIndexOf('.')];
    var objPath = withoutObjectName.Replace("/Game/", "Pal/Content/");
    var exports = provider.LoadPackageObjects(objPath).ToList();
    var tex = exports.OfType<UTexture2D>().FirstOrDefault();
    if (tex == null) return;
    var decoded = tex.Decode();
    if (decoded == null) return;
    var bytes = TextureEncoder.Encode(decoded, ETextureFormat.Png, false, out _);
    File.WriteAllBytes(Path.Combine(towerIconOutDir, outFileName), bytes);
}

// All 8 towers share one icon: the game's own compass icon for boss towers
// (EPalLocationType::PointBossTower in DT_LocationUIData -> T_icon_compass_tower),
// not the per-species Pal portrait - the marker represents "a tower", not "a Pal".
const string towerIconFile = "T_icon_compass_Tower.png";
ExportTowerIcon("/Game/Pal/Texture/UI/InGame/T_icon_compass_tower.T_icon_compass_tower", towerIconFile);

var towerResult = new JArray();
foreach (var (region, gymName) in towerGymByRegion)
{
    var regionKey = "REGION_" + region;
    var regionTextKey = regionTextKeyOverride.GetValueOrDefault(region, regionKey);
    var regionNameRow = regionNameRowsChk?[regionTextKey] as JObject;
    var monster = monsterRowsChk[gymName] as JObject;
    var nameTextId = monster?["OverrideNameTextID"]?.ToString();
    var bossName = palNameRowsChk[nameTextId ?? ""]?["TextData"]?["LocalizedString"]?.ToString() ?? gymName;
    var element1 = ElementString(monster?["ElementType1"]);
    var element2 = ElementString(monster?["ElementType2"]);
    // GYM_WorldTreeDragon has ElementType1 == None too (untyped final boss),
    // unlike every other tower - the element1 side of this null-out was
    // missing until that surfaced it (element2 alone was handled below).
    if (element1 == "None") element1 = null;
    if (element2 == "None") element2 = null;

    double rawX, rawY;
    if (towerExactRawCoordByRegion.TryGetValue(region, out var exact))
    {
        (rawX, rawY) = exact;
    }
    else
    {
        var (mapX, mapY) = towerHudCoordByRegion[region];
        rawX = mapY * 459 - 123888;
        rawY = mapX * 459 + 158000;
    }

    int? levelNormal = null, levelHard = null;
    if (towerLevelByRegion.TryGetValue(region, out var levels))
    {
        levelNormal = levels.normal;
        levelHard = levels.hard;
    }

    towerResult.Add(new JObject
    {
        ["regionKey"] = regionKey,
        ["name"] = regionNameRow?["TextData"]?["LocalizedString"]?.ToString() ?? region,
        ["bossName"] = bossName,
        ["element1"] = element1,
        ["element2"] = element2,
        ["icon"] = towerIconFile,
        ["defeatFlagKey"] = towerDefeatFlagByRegion[region],
        ["positionExact"] = true,
        ["x"] = rawX,
        ["y"] = rawY,
        ["z"] = (double?)null,
        ["levelNormal"] = levelNormal,
        ["levelHard"] = levelHard,
    });
}
Console.WriteLine($"Total towers: {towerResult.Count}, icons exported: {towerExportedIcons.Count}");
File.WriteAllText(@"C:\Projects\PalworldMap\data\towers_static.json", towerResult.ToString(Formatting.Indented));
Console.WriteLine("Wrote data/towers_static.json");

// ============ Bosses / Bounty / Oil Rigs (existing pipeline) ============
var bossRows = LoadRows("Pal/Content/Pal/DataTable/UI/DT_BossSpawnerLoactionData");
var monsterRows = LoadRows("Pal/Content/Pal/DataTable/Character/DT_PalMonsterParameter");
var humanRows = LoadRows("Pal/Content/Pal/DataTable/Character/DT_PalHumanParameter");
var palNameRows = LoadRows("Pal/Content/L10N/en/Pal/DataTable/Text/DT_PalNameText_Common");
var humanNameRows = LoadRows("Pal/Content/L10N/en/Pal/DataTable/Text/DT_HumanNameText_Common");
var regionNameRows = LoadRows("Pal/Content/L10N/en/Pal/DataTable/Text/DT_WorldMap_Common_Text_Common");
var iconRows = LoadRows("Pal/Content/Pal/DataTable/Character/DT_PalBossNPCIcon");
var palIconRows = LoadRows("Pal/Content/Pal/DataTable/Character/DT_PalCharacterIconDataTable");
// Case-insensitive: Tribe values (e.g. "BadCatgirl") don't always match this
// table's own casing (e.g. "BadCatGirl") exactly.
var palIconLookup = palIconRows.Properties().ToDictionary(p => p.Name, p => p, StringComparer.OrdinalIgnoreCase);

// Case-insensitive icon lookup (DT_BossSpawnerLoactionData has "BOSS_Police_Rifle" style
// casing but DT_PalBossNPCIcon has at least one mismatch: "BOSS_Police_old" vs "BOSS_Police_Old").
var iconLookup = iconRows.Properties().ToDictionary(p => p.Name, p => p, StringComparer.OrdinalIgnoreCase);

// Same "BOSS_Police_old" vs "BOSS_Police_Old" casing mismatch also broke the
// *name* resolution for this one, not just its icon (confirmed 2026-07-22 -
// a real user reported the bounty rendering as the raw "BOSS_Police_Old" id
// instead of its real name "Pinch"; DT_PalHumanParameter's actual row key
// is lowercase "old"). humanRows/humanNameRows were still being indexed
// case-sensitively everywhere (this Bosses/Bounty section below, and
// DungeonHumanName/DungeonHumanIcon further down) even though every other
// join in this file learned this lesson already (palIconLookup/iconLookup
// above, monsterRowsCI/palNameRowsCI near the Dungeon Contents section) -
// case-insensitive dictionaries here close the gap for both remaining call
// sites at once, not just this one bounty.
var humanRowsCI = humanRows.Properties().ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);
var humanNameRowsCI = humanNameRows.Properties().ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);

var iconOutDir = @"C:\Projects\PalworldMap\frontend\assets\boss_icons";
Directory.CreateDirectory(iconOutDir);
var exportedIcons = new HashSet<string>();

void ExportIcon(string assetPathName, string outFileName)
{
    if (exportedIcons.Contains(outFileName)) return;
    exportedIcons.Add(outFileName);
    // "/Game/Pal/Texture/PalIcon/NPC/T_Foo.T_Foo" -> "Pal/Content/Pal/Texture/PalIcon/NPC/T_Foo"
    // (strip the trailing ".ObjectName" — LoadPackageObjects wants the package path only)
    var withoutObjectName = assetPathName[..assetPathName.LastIndexOf('.')];
    var objPath = withoutObjectName.Replace("/Game/", "Pal/Content/");
    var exports = provider.LoadPackageObjects(objPath).ToList();
    var tex = exports.OfType<UTexture2D>().FirstOrDefault();
    if (tex == null) { Console.WriteLine($"  no texture export at {objPath}"); return; }
    var decoded = tex.Decode();
    if (decoded == null) { Console.WriteLine($"  icon decode failed: {objPath}"); return; }
    var bytes = DownscalePng(TextureEncoder.Encode(decoded, ETextureFormat.Png, false, out _));
    File.WriteAllBytes(Path.Combine(iconOutDir, outFileName), bytes);
}

var result = new JArray();
foreach (var prop in bossRows.Properties())
{
    var row = (JObject)prop.Value;
    var spawnerId = row["SpawnerID"]?.ToString() ?? prop.Name;
    var characterId = row["CharacterID"]?.ToString() ?? "";

    if (spawnerId.StartsWith("REGION_Oilrig", StringComparison.OrdinalIgnoreCase))
    {
        // Not a boss at all - a raid zone. Named via the map's own region-label
        // table (DT_WorldMap_Common_Text_Common), keyed directly by this SpawnerID.
        var regionNameRow = regionNameRows[spawnerId] as JObject;
        var regionName = regionNameRow?["TextData"]?["LocalizedString"]?.ToString() ?? spawnerId;
        const string oilrigIconFile = "T_icon_compass_Oilrig.png";
        try
        {
            ExportIcon("/Game/Pal/Texture/UI/InGame/T_icon_compass_Oilrig.T_icon_compass_Oilrig", oilrigIconFile);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  icon export failed for {spawnerId}: {ex.Message}");
        }
        result.Add(new JObject
        {
            ["spawnerId"] = spawnerId,
            ["characterId"] = characterId,
            ["category"] = "oilrig",
            ["name"] = regionName,
            ["element1"] = null,
            ["element2"] = null,
            ["icon"] = oilrigIconFile,
            ["level"] = row["Level"],
            ["x"] = row["Location"]?["X"],
            ["y"] = row["Location"]?["Y"],
            ["z"] = row["Location"]?["Z"],
        });
        continue;
    }

    var isPal = characterId != "None";
    string name = spawnerId;
    string? element1 = null, element2 = null;
    string? icon = null;

    if (isPal)
    {
        var monster = monsterRows[characterId] as JObject;
        if (monster != null)
        {
            var nameTextId = monster["OverrideNameTextID"]?.ToString();
            if (!string.IsNullOrEmpty(nameTextId) && nameTextId != "None")
            {
                var nameRow = palNameRows[nameTextId] as JObject;
                name = nameRow?["TextData"]?["LocalizedString"]?.ToString() ?? nameTextId;
            }
            element1 = ElementString(monster["ElementType1"]);
            element2 = ElementString(monster["ElementType2"]);
            if (element1 == "None") element1 = null;
            if (element2 == "None") element2 = null;

            // Icon table is keyed by the bare species codename (Tribe), not the
            // "BOSS_"-prefixed CharacterID, e.g. "CaptainPenguin" not "BOSS_CaptainPenguin".
            var tribeKey = ElementString(monster["Tribe"]); // strips "EPalTribeID::" the same way
            string? iconAssetPath = null;
            if (palIconLookup.TryGetValue(tribeKey, out var palIconProp))
                iconAssetPath = ((JObject)palIconProp.Value)["Icon"]?["AssetPathName"]?.ToString();
            if (!string.IsNullOrEmpty(iconAssetPath))
            {
                var fileName = "Pal_" + tribeKey + ".png";
                try
                {
                    ExportIcon(iconAssetPath, fileName);
                    icon = fileName;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  pal icon export failed for {tribeKey}: {ex.Message}");
                }
            }
        }
    }
    else
    {
        humanRowsCI.TryGetValue(spawnerId, out var humanTok);
        var human = humanTok as JObject;
        if (human != null)
        {
            var nameTextId = human["OverrideNameTextID"]?.ToString();
            if (!string.IsNullOrEmpty(nameTextId) && nameTextId != "None")
            {
                humanNameRowsCI.TryGetValue(nameTextId, out var nameTok);
                var nameRow = nameTok as JObject;
                name = nameRow?["TextData"]?["LocalizedString"]?.ToString() ?? nameTextId;
            }
        }
        if (iconLookup.TryGetValue(spawnerId, out var iconProp))
        {
            var iconObj = (JObject)iconProp.Value;
            var assetPathName = iconObj["Icon"]?["AssetPathName"]?.ToString();
            if (!string.IsNullOrEmpty(assetPathName))
            {
                var fileName = iconProp.Name + ".png"; // use the icon table's own casing consistently
                try
                {
                    ExportIcon(assetPathName, fileName);
                    icon = fileName;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  icon export failed for {spawnerId}: {ex.Message}");
                }
            }
        }
    }

    result.Add(new JObject
    {
        ["spawnerId"] = spawnerId,
        ["characterId"] = characterId,
        ["category"] = isPal ? "pal" : "human",
        ["name"] = name,
        ["element1"] = element1,
        ["element2"] = element2,
        ["icon"] = icon,
        ["level"] = row["Level"],
        ["x"] = row["Location"]?["X"],
        ["y"] = row["Location"]?["Y"],
        ["z"] = row["Location"]?["Z"],
    });
}

// DT_BossSpawnerLoactionData itself contains exact-duplicate rows under
// different row-name keys for every single human "Bounty" boss (confirmed
// 2026-07-23: all 66 human rows collapse to exactly 33 distinct encounters,
// each duplicate pair byte-identical down to SpawnerID/Location/Level - a
// real authoring artifact in the game's own table, not a join/extraction
// bug here). Dedupe on full field equality, not just SpawnerID, because at
// least one Pal boss (remainsIsland_1_GrassGolem_FBOSS/Dualith) reuses the
// same SpawnerID for two genuinely different physical spawn points
// (different Location/Level) - deduping by SpawnerID alone would wrongly
// drop a real encounter there.
var dedupedResult = new JArray(
    result.Children<JObject>()
        .GroupBy(o => o.ToString(Formatting.None))
        .Select(g => g.First())
);
Console.WriteLine($"Total bosses: {result.Count} ({result.Count - dedupedResult.Count} exact-duplicate rows dropped), icons exported: {exportedIcons.Count}");
File.WriteAllText(@"C:\Projects\PalworldMap\data\bosses_static.json", dedupedResult.ToString(Formatting.Indented));
Console.WriteLine("Wrote data/bosses_static.json");

// ============ Watchtowers / Waypoints ============
// Both are "fast travel points" the player unlocks by walking up and
// interacting with them (grey silhouette on the map until then, full color
// after). Not in any DataTable with positions — they're placed actors in
// the *persistent* level itself (Pal/Content/Pal/Maps/MainWorld_5/PL_MainWorld5),
// NOT the streamed World Partition _Generated_ grid the effigies use (confirmed:
// a full 9977-cell _Generated_ scan for these classes found zero — they're
// always-loaded landmarks, so they live directly in the base persistent map).
//
// Two distinct Blueprint classes, not one shared class with a type flag:
//  - BP_LevelObject_UnlockMapPoint_C (22 instances) = Watchtowers, the tall
//    climbable tower structures. FastTravelPointID values are "WatchTower_N"
//    (N=1..22) plus "WatchTower_WorldTree_1/2" for the Feybreak DLC area.
//    Two named text rows (WatchTower_14 "Lava Reservoir", WatchTower_16
//    "Ruined Fortress City") have no matching placed actor anywhere (checked
//    both the persistent level and the full _Generated_ grid) - likely
//    renamed/consolidated in a later game update, leaving orphaned text rows.
//    Left out; 22 confirmed real ones is what's actually placed and
//    collectible in-game.
//  - BP_LevelObject_TowerFastTravelPoint_C (152 instances, confusingly named
//    given the class above) = every other fast-travel point: generic named
//    landmarks (e.g. "Fisherman's Point"), Sealed Realm dungeon entrances
//    (e.g. "Sealed Realm of the Mystic"), and DLC-region-specific ones
//    (SkyIsland_*, WorldTree_*, New_SmallIsland*, Boss_KingWhale/Forest,
//    FootOfWorldTree) - shown to the user as "Waypoints".
//
// Both classes share the same LevelObjectInstanceId scheme as effigies (GUID
// baked into the placed actor, dashes stripped + uppercase when matching the
// save's per-player unlock flag - see parse.load_unlocked_fasttravel_ids()).
// Display name comes from DT_MapRespawnPointInfoText, keyed by the actor's
// own FastTravelPointID property (e.g. "WatchTower_6" -> "Verdant Stream
// Watchtower"). Position resolved the same way as effigies: RootComponent's
// object-path number is a direct 0-based index into the same
// LoadPackageObjects() export list (validated against a real player's
// reported HUD coordinates for both a Watchtower and a Waypoint, within
// ~500 units - same precision class as the rest of this pipeline).
JObject? ResolveExportRef(List<CUE4Parse.UE4.Assets.Exports.UObject> allExports, JToken? refToken)
{
    var path = refToken?["ObjectPath"]?.ToString();
    if (path == null) return null;
    var idxStr = path[(path.LastIndexOf('.') + 1)..];
    if (!int.TryParse(idxStr, out var idx) || idx < 0 || idx >= allExports.Count) return null;
    return JObject.Parse(JsonConvert.SerializeObject(allExports[idx]));
}

var persistentLevelExports = provider.LoadPackageObjects("Pal/Content/Pal/Maps/MainWorld_5/PL_MainWorld5").ToList();
var respawnPointNameRows = LoadRows("Pal/Content/L10N/en/Pal/DataTable/Text/DT_MapRespawnPointInfoText");

var fastTravelIconOutDir = @"C:\Projects\PalworldMap\frontend\assets\fasttravel_icons";
Directory.CreateDirectory(fastTravelIconOutDir);
var fastTravelExportedIcons = new HashSet<string>();
void ExportFastTravelIcon(string assetPathName, string outFileName)
{
    if (fastTravelExportedIcons.Contains(outFileName)) return;
    fastTravelExportedIcons.Add(outFileName);
    var withoutObjectName = assetPathName[..assetPathName.LastIndexOf('.')];
    var objPath = withoutObjectName.Replace("/Game/", "Pal/Content/");
    var exports = provider.LoadPackageObjects(objPath).ToList();
    var tex = exports.OfType<UTexture2D>().FirstOrDefault();
    if (tex == null) return;
    var decoded = tex.Decode();
    if (decoded == null) return;
    var bytes = TextureEncoder.Encode(decoded, ETextureFormat.Png, false, out _);
    File.WriteAllBytes(Path.Combine(fastTravelIconOutDir, outFileName), bytes);
}

// Confirmed against a real in-game screenshot of the Watchtower "Transfer"
// interaction prompt: T_icon_compass_FTUnlockMap (bird, wings up, book/chevron
// base) is an exact visual match, and its name matches
// BP_LevelObject_UnlockMapPoint_C ("UnlockMap" point) - not
// T_icon_compass_FTtower as originally (wrongly) assumed from the DT_LocationUIData
// PointFastTravel row alone. FTtower's name in fact matches the *other* class,
// BP_LevelObject_TowerFastTravelPoint_C ("Tower FastTravel Point"), so it
// belongs to Waypoints instead. T_icon_compass_Teleport (the swirl, from
// DT_LocationUIData's PointWarpAltar row) turned out to belong to the two
// special BP_LevelObject_WarpAltar_WorldTreeEntrance/Exit actors, not the
// general Waypoint class - unused here.
const string watchtowerIconFile = "T_icon_compass_FTUnlockMap.png";
const string waypointIconFile = "T_icon_compass_FTtower.png";
ExportFastTravelIcon("/Game/Pal/Texture/UI/InGame/T_icon_compass_FTUnlockMap.T_icon_compass_FTUnlockMap", watchtowerIconFile);
ExportFastTravelIcon("/Game/Pal/Texture/UI/InGame/T_icon_compass_FTtower.T_icon_compass_FTtower", waypointIconFile);

JArray ExtractFastTravelPoints(string className)
{
    var arr = new JArray();
    foreach (var exp in persistentLevelExports)
    {
        if ((exp.Class?.Name.ToString() ?? "") != className) continue;
        var j = JObject.Parse(JsonConvert.SerializeObject(exp));
        var props = j["Properties"] as JObject;
        var pointId = props?["FastTravelPointID"]?.ToString();
        var instanceId = props?["LevelObjectInstanceId"]?.ToString();
        if (pointId == null || instanceId == null) continue;
        var rootObj = ResolveExportRef(persistentLevelExports, props?["RootComponent"]);
        var loc = (rootObj?["Properties"] as JObject)?["RelativeLocation"];
        if (loc == null) continue;
        var name = respawnPointNameRows[pointId]?["TextData"]?["LocalizedString"]?.ToString() ?? pointId;
        arr.Add(new JObject
        {
            ["pointId"] = pointId,
            ["instanceId"] = instanceId,
            ["name"] = name,
            ["x"] = loc["X"],
            ["y"] = loc["Y"],
            ["z"] = loc["Z"],
        });
    }
    return arr;
}

var watchtowerResult = ExtractFastTravelPoints("BP_LevelObject_UnlockMapPoint_C");
foreach (var t in watchtowerResult) ((JObject)t)["icon"] = watchtowerIconFile;
Console.WriteLine($"Total watchtowers: {watchtowerResult.Count}");
File.WriteAllText(@"C:\Projects\PalworldMap\data\watchtowers_static.json", watchtowerResult.ToString(Formatting.Indented));
Console.WriteLine("Wrote data/watchtowers_static.json");

var waypointResult = ExtractFastTravelPoints("BP_LevelObject_TowerFastTravelPoint_C");
foreach (var t in waypointResult) ((JObject)t)["icon"] = waypointIconFile;
Console.WriteLine($"Total waypoints: {waypointResult.Count}");
File.WriteAllText(@"C:\Projects\PalworldMap\data\waypoints_static.json", waypointResult.ToString(Formatting.Indented));
Console.WriteLine("Wrote data/waypoints_static.json");

// ============ Dungeons (open-world "Dungeon" entrances, e.g. Grassland/Desert) ============
// Random-feeling small loot/battle dungeons scattered across the map -
// distinct from the 8 fixed Challenge Towers above (which have no placed
// entrance actor at all, see the Towers section notes). Placed actor class
// is BP_DungeonPortalMarker_<Biome>_C (11 biome variants found: Desert,
// Forest, Grass1, Sakura, Skyland, Snow, Viking/Viking_B/Viking_C, Volcano,
// Yakushima - "Viking" = Sakurajima/Darkisland's snow-viking biome, matched
// by a prefix scan rather than hardcoding each variant name so a new biome
// variant wouldn't silently get dropped). All 157 live in the persistent
// level (no expensive World Partition scan needed, same as Watchtowers/
// Waypoints/Bosses). Each instance has a LevelObjectInstanceId but no
// DataTable row/name reference - unlike every other category here, there's
// no per-instance display name, only the biome baked into the class name.
// No per-player "unlocked"/"cleared" state exists for these (this is a
// distinct concept from the per-instance RespawnProbability override seen
// on some instances' EditSpawnParameter - that's spawn-table tuning, not a
// player-visible flag) - matches the user's own description ("we don't know
// exactly when they activate"), so unlike every other section this ships as
// a single show/hide-all toggle with one shared icon, no per-item checklist.
// Icon: the game's own compass icon for these, T_icon_compass_dungeon -
// confirmed to exist by name among all T_icon_compass_* textures (same
// "prefer the game's real icon" rule as Towers/Watchtowers/Waypoints/Bases).
var dungeonIconOutDir = @"C:\Projects\PalworldMap\frontend\assets\dungeon_icons";
Directory.CreateDirectory(dungeonIconOutDir);
const string dungeonIconFile = "T_icon_compass_dungeon.png";
{
    var exports = provider.LoadPackageObjects("Pal/Content/Pal/Texture/UI/InGame/T_icon_compass_dungeon").ToList();
    var tex = exports.OfType<UTexture2D>().First();
    var decoded = tex.Decode()!;
    var bytes = TextureEncoder.Encode(decoded, ETextureFormat.Png, false, out _);
    File.WriteAllBytes(Path.Combine(dungeonIconOutDir, dungeonIconFile), bytes);
}

// Each biome's *default* SpawnAreaId (the roster used unless a specific
// placed instance overrides it - see below) lives on the biome class's own
// CDO (Default__ object), as a SpawnAreaIds:[{Key: "<id>"}] property -
// discovered by dumping one instance's full property set per biome (a
// throwaway scratch project, extractor/ScratchDungeon, since deleted - its
// out_marker_cdo.json / out_all_marker_instances.json findings are what this
// block reproduces). Found generically via a package-path scan for
// "BP_DungeonPortalMarker_" rather than a hardcoded biome->path table, so a
// future biome variant (or Yakushima's own oddly-nested folder,
// .../Dungeon/Yakushima/BP_DungeonPortalMarker_Yakushima, vs. every other
// biome sitting directly under .../Dungeon/) is picked up automatically
// instead of silently missing a CDO lookup.
var dungeonMarkerPackages = provider.Files.Keys
    .Where(k => k.Contains("BP_DungeonPortalMarker_", StringComparison.Ordinal) && k.EndsWith(".uasset", StringComparison.Ordinal))
    .Select(k => k[..^".uasset".Length])
    .Distinct()
    .ToList();
var biomeDefaultSpawnAreaId = new Dictionary<string, string>();
foreach (var pkgPath in dungeonMarkerPackages)
{
    var fileName = pkgPath.Split('/').Last();
    if (!fileName.StartsWith("BP_DungeonPortalMarker_", StringComparison.Ordinal)) continue;
    var biomeKey = fileName["BP_DungeonPortalMarker_".Length..];
    var cdoExports = provider.LoadPackageObjects(pkgPath).ToList();
    var cdo = cdoExports.FirstOrDefault(e => (e.Name ?? "").StartsWith("Default__", StringComparison.Ordinal));
    var cdoJ = cdo != null ? JObject.Parse(JsonConvert.SerializeObject(cdo)) : null;
    var defaultAreaId = ((cdoJ?["Properties"] as JObject)?["SpawnAreaIds"] as JArray)?.FirstOrDefault()?["Key"]?.ToString();
    if (defaultAreaId != null) biomeDefaultSpawnAreaId[biomeKey] = defaultAreaId;
}
Console.WriteLine($"Biome default SpawnAreaIds resolved: {string.Join(", ", biomeDefaultSpawnAreaId.Select(kv => $"{kv.Key}={kv.Value}"))}");

var dungeonResult = new JArray();
foreach (var exp in persistentLevelExports)
{
    var cn = exp.Class?.Name.ToString() ?? "";
    if (!cn.StartsWith("BP_DungeonPortalMarker_", StringComparison.Ordinal)) continue;
    var biome = cn["BP_DungeonPortalMarker_".Length..];
    if (biome.EndsWith("_C", StringComparison.Ordinal)) biome = biome[..^2];
    var j = JObject.Parse(JsonConvert.SerializeObject(exp));
    var props = j["Properties"] as JObject;
    var instanceId = props?["LevelObjectInstanceId"]?.ToString();
    if (instanceId == null) continue;
    var rootObj = ResolveExportRef(persistentLevelExports, props?["RootComponent"]);
    var loc = (rootObj?["Properties"] as JObject)?["RelativeLocation"];
    if (loc == null) continue;
    // Per-instance SpawnAreaIds override (same property name/shape as the
    // class CDO's own default, above) - confirmed real on a minority of
    // instances (e.g. some Forest instances override to Forest002, some
    // Grass1 instances override to Grass002/Island001/002/003) - falls back
    // to the biome's class default when absent, which is the common case.
    var spawnAreaIdOverride = (props?["SpawnAreaIds"] as JArray)?.FirstOrDefault()?["Key"]?.ToString();
    var spawnAreaId = spawnAreaIdOverride ?? (biomeDefaultSpawnAreaId.TryGetValue(biome, out var defaultId) ? defaultId : null);
    dungeonResult.Add(new JObject
    {
        ["instanceId"] = instanceId,
        ["biome"] = biome,
        ["spawnAreaId"] = spawnAreaId,
        ["icon"] = dungeonIconFile,
        ["x"] = loc["X"],
        ["y"] = loc["Y"],
        ["z"] = loc["Z"],
    });
}
Console.WriteLine($"Total dungeon entrances: {dungeonResult.Count}");
File.WriteAllText(@"C:\Projects\PalworldMap\data\dungeons_static.json", dungeonResult.ToString(Formatting.Indented));
Console.WriteLine("Wrote data/dungeons_static.json");

// Case-insensitive lookup of every real Pal/Content/Pal/Texture/PalIcon/Normal/
// file, keyed by its own filename (no extension) - shared by DungeonHumanIcon
// below and the NPCs section further down this file (see that section's own
// comment for the full "why case-insensitive" story - casing isn't
// consistent in the real file list, e.g. "T_Male_Scholar01_v02_Icon_normal"
// capitalizes "Icon" and nothing else does). Declared here (not down in the
// NPCs section where it was originally written) purely because this section
// runs first and needs it too - not moved for any NPCs-section reason.
var normalIconLookup = provider.Files.Keys
    .Select(k => k.ToString())
    .Where(k => k.Contains("Pal/Content/Pal/Texture/PalIcon/Normal/", StringComparison.OrdinalIgnoreCase)
             && k.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
    .Select(k => Path.GetFileNameWithoutExtension(k))
    .GroupBy(f => f, StringComparer.OrdinalIgnoreCase)
    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

// ============ Dungeon Contents (per-SpawnAreaId enemy/loot roster) ============
// What's actually inside each of the 157 entrances above, keyed by the
// spawnAreaId resolved onto each dungeonResult entry - a separate, much
// smaller keyspace (only 14 distinct real SpawnAreaIds exist, see below)
// than the 157 placed markers, so this writes its own file
// (dungeon_contents_static.json) rather than duplicating the same 14
// rosters onto every one of the 157 marker entries.
//
// The join chain (fully solved via extractor/ScratchDungeon, a throwaway
// scratch project now deleted - see NOTES.md's Dungeons section for the
// full investigation writeup): DT_DungeonEnemySpawnDataTable has one row
// per (SpawnAreaId, RankType) combo (confirmed: 59 rows, 59 unique combos,
// zero duplicates - WeightInSpawnAreaAndRank is irrelevant to this
// extraction, not a second weighted-selection layer). Each row's own
// SpawnerBlueprintSoftClass points at a Blueprint whose CDO has a
// SpawnGroupList array - each entry a weighted group of
// {PalId/NPCID, Level/Level_Max, Num/Num_Max}. This is NOT joined via
// DT_PalWildSpawner (checked exhaustively during the scratch investigation,
// zero overlap - a dead end, don't repeat it).
//
// Only the 14 SpawnAreaIds actually referenced by a placed marker's
// resolved spawnAreaId (above) are real - TestDebug01 and Meadow01 both
// have rows in DT_DungeonEnemySpawnDataTable but no placed marker ever
// resolves to them (dev-only/orphaned), so restricting to
// realSpawnAreaIds below drops them automatically, no hardcoded skip list
// needed.
var realSpawnAreaIds = dungeonResult.Select(e => e["spawnAreaId"]?.ToString())
    .Where(id => id != null).Distinct().ToHashSet();
Console.WriteLine($"Real SpawnAreaIds in use: {realSpawnAreaIds.Count} ({string.Join(", ", realSpawnAreaIds)})");

// Real, authoritative per-area display name (e.g. Grass001 -> "Hillside
// Cavern", Grass002 -> "Ravine Grotto" - genuinely distinct names, not a
// generic "Grassland" label reused across the Island biome-pool override
// - see NOTES.md) - NOT the DT_DungeonNameText rows used for the unrelated
// "Fixed Dungeon"/Sealed Realm system (that table also has "en Text"
// placeholder junk and Sealed-Realm-specific rows; DT_DungeonSpawnAreaDataTable
// is the correct join for random-Dungeon-Portal SpawnAreaIds specifically).
// Yakushima001's own name resolves to the literal string "???" - a real,
// confirmed fact (its region is an unmarked Terraria crossover easter egg),
// not a broken lookup - kept as-is rather than special-cased.
var dungeonSpawnAreaRows = LoadRows("Pal/Content/Pal/DataTable/Dungeon/DT_DungeonSpawnAreaDataTable");
var dungeonNameTextRows = LoadRows("Pal/Content/L10N/en/Pal/DataTable/Text/DT_DungeonNameText");

string DungeonAreaLabel(string spawnAreaId)
{
    var textId = (dungeonSpawnAreaRows[spawnAreaId] as JObject)?["DungeonNameTextId"]?.ToString();
    if (string.IsNullOrEmpty(textId)) return spawnAreaId;
    var label = (dungeonNameTextRows[textId] as JObject)?["TextData"]?["LocalizedString"]?.ToString();
    return string.IsNullOrEmpty(label) ? spawnAreaId : label;
}

// Species name/icon resolution - deliberately NOT PalPortraitIcon/the boss
// name-resolution block above verbatim: those two both have a "never fails"
// fallback (MobuCitizen icon / raw characterId as name) appropriate for
// their own features (every shop-pool/boss species there really does
// resolve).
//
// Case-insensitive lookups, unlike every other join in this file: some
// spawner Blueprints/DataTable rows' own baked-in Pal ID casing genuinely
// disagrees with DT_PalMonsterParameter/DT_PalNameText_Common's real row
// keys for some species - confirmed real, not a typo in this extractor.
// WindChimes/BOSS_WindChimes (originally found via the Dungeon Contents
// Grass002/Dessert001 boss pools) has a real DT_PalMonsterParameter row
// (Tribe "WindChimes", exact case, so the old case-sensitive icon join
// happened to work) whose OverrideNameTextID points at "PAL_NAME_WindChimes"
// - but the actual text row key is "PAL_NAME_Windchimes" (lowercase c),
// LocalizedString "Hangyu" (a real Pal, user-confirmed by sight against this
// project's own exported icon - see NOTES.md). Icewitch (Snow001's boss
// pool) is worse-cased - "BOSS_Icewitch"/"Icewitch" (lowercase w) vs. the
// table's real "BOSS_IceWitch"/"IceWitch" (capital W), so even the old icon
// join failed (needs an exact monsterRows hit first) - same species as
// Yakushima001 Normal's correctly-cased "IceWitch" entry, which always
// resolved fine to "Icelyn". Building case-insensitive dictionaries here
// (matching palIconLookup's own existing OrdinalIgnoreCase pattern above)
// instead of switching monsterRows/palNameRows globally, since those two
// are plain case-sensitive JObjects used correctly-cased everywhere else
// in this file - no need to risk changing behavior elsewhere.
//
// Generalized 2026-07-22 from Dungeon-Contents-only DungeonPalName/
// DungeonPalIcon to ResolvePalName/ResolvePalIcon - the Pal Spawn Locations
// section below (wild field spawns) needs byte-identical species name/icon
// resolution logic (same case-insensitive gotcha applies there too, since
// it joins through the same monsterRowsCI/palNameRowsCI/palIconLookup
// tables), so this is now a shared helper rather than a second copy-pasted
// implementation. No behavior change for the existing Dungeon Contents
// call sites - purely a rename.
var monsterRowsCI = monsterRows.Properties().ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);
var palNameRowsCI = palNameRows.Properties().ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);

string? ResolvePalName(string characterId)
{
    // Same "PAL_NAME_<CharacterID> direct key first, OverrideNameTextID
    // fallback" order as ResolvePalShopPool above - regular wild-Pal-style
    // species (the common case in dungeon rosters) don't carry their own
    // OverrideNameTextID, only the "BOSS_"-tier variants do.
    palNameRowsCI.TryGetValue("PAL_NAME_" + characterId, out var directTok);
    var direct = (directTok as JObject)?["TextData"]?["LocalizedString"]?.ToString();
    if (!string.IsNullOrEmpty(direct)) return direct;
    monsterRowsCI.TryGetValue(characterId, out var monsterTok);
    var monster = monsterTok as JObject;
    var nameTextId = monster?["OverrideNameTextID"]?.ToString();
    if (!string.IsNullOrEmpty(nameTextId) && nameTextId != "None")
    {
        palNameRowsCI.TryGetValue(nameTextId, out var overrideTok);
        var viaOverride = (overrideTok as JObject)?["TextData"]?["LocalizedString"]?.ToString();
        if (!string.IsNullOrEmpty(viaOverride)) return viaOverride;
    }
    return null; // a genuine remaining gap, if any - not assumed, verify before shipping as "(unresolved)"
}

string? ResolvePalIcon(string characterId)
{
    monsterRowsCI.TryGetValue(characterId, out var monsterTok);
    var monster = monsterTok as JObject;
    if (monster == null) return null;
    var tribeKey = ElementString(monster["Tribe"]);
    if (!palIconLookup.TryGetValue(tribeKey, out var palIconProp)) return null;
    var iconAssetPath = ((JObject)palIconProp.Value)["Icon"]?["AssetPathName"]?.ToString();
    if (string.IsNullOrEmpty(iconAssetPath)) return null;
    var fileName = "Pal_" + tribeKey + ".png"; // shared boss_icons dir/naming - same species, no reason to export twice
    try { ExportIcon(iconAssetPath, fileName); return fileName; }
    catch (Exception ex) { Console.WriteLine($"  dungeon pal icon export failed for {tribeKey}: {ex.Message}"); return null; }
}

// Human trash-tier NPCs (RankType NPCHuman, e.g. "Hunter_Handgun" -> "Syndicate
// Thug") resolve via DT_PalHumanParameter + DT_HumanNameText_Common, same
// pattern as Bounty's human bosses - but joined by the roster's own NPCID
// directly (not a "BOSS_"-prefixed SpawnerID like Bounty uses).
string? DungeonHumanName(string npcId)
{
    humanRowsCI.TryGetValue(npcId, out var humanTok);
    var nameTextId = (humanTok as JObject)?["OverrideNameTextID"]?.ToString();
    if (string.IsNullOrEmpty(nameTextId) || nameTextId == "None") return null;
    humanNameRowsCI.TryGetValue(nameTextId, out var nameTok);
    return (nameTok as JObject)?["TextData"]?["LocalizedString"]?.ToString();
}

// Icons for these WERE originally left null - Bounty's own DT_PalBossNPCIcon
// is keyed by boss-tier SpawnerIDs only, no entry exists for these regular
// dungeon-trash humans - but a real one exists via a different, more
// reliable path: DT_PalHumanParameter's own BPClass field (e.g.
// "NPC_Hunter_Fat" for Hunter_Fat_GatlingGun, "NPC_Hunter" for the other
// Hunter_* variants that aren't the "Fat" one) - strip the "NPC_" prefix and
// it's a real, exact PalIcon/Normal filename (confirmed via
// extractor/ScratchHumanIcons, a throwaway investigation, now deleted - see
// NOTES.md's Dungeons section). More reliable than guessing off the row's
// own NPCID/name, since several distinct NPCIDs legitimately share one
// BPClass/portrait (all 4 non-"Fat" Hunter_* variants -> the same plain
// "Hunter" look) - matching on BPClass gets this grouping right
// automatically instead of needing a hardcoded synonym list.
string? DungeonHumanIcon(string npcId)
{
    humanRowsCI.TryGetValue(npcId, out var humanTok);
    var bpClass = (humanTok as JObject)?["BPClass"]?.ToString();
    if (string.IsNullOrEmpty(bpClass)) return null;
    var baseName = bpClass.StartsWith("NPC_", StringComparison.OrdinalIgnoreCase) ? bpClass["NPC_".Length..] : bpClass;
    var candidate = $"T_{baseName}_icon_normal";
    if (!normalIconLookup.TryGetValue(candidate, out var realFileName)) return null;
    // "NPC_" prefix on the exported filename keeps these visually/namespace
    // distinct from the "Pal_"-prefixed species portraits already sharing
    // this same boss_icons directory.
    var fileName = "NPC_" + realFileName + ".png";
    var assetPathName = $"/Game/Pal/Texture/PalIcon/Normal/{realFileName}.{realFileName}";
    try { ExportIcon(assetPathName, fileName); return fileName; }
    catch (Exception ex) { Console.WriteLine($"  dungeon human icon export failed for {npcId}: {ex.Message}"); return null; }
}

// Merge Normal02-05/MidBoss02-05 into one "Normal"/"MidBoss" tier bucket -
// confirmed real only for Yakushima001 (Normal/02/03/04, 4 distinct spawner
// Blueprints - cavern/mushroom/hallow variants of its trash tier; no area
// has a MidBoss02-05 in practice, but the same merge rule is applied
// generically rather than hardcoding "only Yakushima has this"). Each
// contributing row's own SpawnGroupList groups are concatenated into one
// flat groups array for the merged tier - they're independent trash-tier
// rolls, not weighted alternatives of each other, so there's no single
// correct combined "weight" to assign across rows; concatenating and
// leaving each group's own intra-row weight intact is the simplest
// faithful representation for a UI that's just listing "what can appear
// here", not simulating exact roll probabilities.
string MergedTierKey(string rankType)
{
    if (rankType == "Normal" || System.Text.RegularExpressions.Regex.IsMatch(rankType, @"^Normal0[2-9]$")) return "Normal";
    if (rankType == "MidBoss" || System.Text.RegularExpressions.Regex.IsMatch(rankType, @"^MidBoss0[2-9]$")) return "MidBoss";
    return rankType;
}

var dungeonEnemySpawnRows = LoadRows("Pal/Content/Pal/DataTable/Dungeon/DT_DungeonEnemySpawnDataTable");
var spawnerGroupListCache = new Dictionary<string, JArray?>();
JArray? SpawnerGroupList(string assetPathName)
{
    if (spawnerGroupListCache.TryGetValue(assetPathName, out var cached)) return cached;
    var withoutObjectName = assetPathName[..assetPathName.LastIndexOf('.')];
    var objPath = withoutObjectName.Replace("/Game/", "Pal/Content/");
    var exports = provider.LoadPackageObjects(objPath).ToList();
    var cdo = exports.FirstOrDefault(e => (e.Name ?? "").StartsWith("Default__", StringComparison.Ordinal));
    var cdoJ = cdo != null ? JObject.Parse(JsonConvert.SerializeObject(cdo)) : null;
    var groupList = (cdoJ?["Properties"] as JObject)?["SpawnGroupList"] as JArray;
    spawnerGroupListCache[assetPathName] = groupList;
    return groupList;
}

var dungeonContents = new JObject();
foreach (var areaId in realSpawnAreaIds)
{
    var tiers = new JObject();
    // mergedTier -> accumulated groups (JArray), built incrementally across
    // however many raw DT_DungeonEnemySpawnDataTable rows feed into it.
    var tierGroups = new Dictionary<string, JArray>();

    foreach (var prop in dungeonEnemySpawnRows.Properties())
    {
        var row = (JObject)prop.Value;
        if (row["SpawnAreaId"]?.ToString() != areaId) continue;
        var rankType = ElementString(row["RankType"]);
        var mergedKey = MergedTierKey(rankType);
        var assetPathName = row["SpawnerBlueprintSoftClass"]?["AssetPathName"]?.ToString();
        var groupList = !string.IsNullOrEmpty(assetPathName) ? SpawnerGroupList(assetPathName) : null;
        if (groupList == null) continue;

        if (!tierGroups.TryGetValue(mergedKey, out var groupsArr))
        {
            groupsArr = new JArray();
            tierGroups[mergedKey] = groupsArr;
        }

        foreach (var group in groupList)
        {
            var pals = new JArray();
            foreach (var palEntry in (group["PalList"] as JArray) ?? new JArray())
            {
                var palId = palEntry["PalId"]?["Key"]?.ToString() ?? "None";
                var npcId = palEntry["NPCID"]?["Key"]?.ToString() ?? "None";
                var isHuman = palId == "None" && npcId != "None";
                var characterId = isHuman ? npcId : palId;
                pals.Add(new JObject
                {
                    ["characterId"] = characterId,
                    ["name"] = isHuman ? DungeonHumanName(npcId) : ResolvePalName(palId),
                    ["icon"] = isHuman ? DungeonHumanIcon(npcId) : ResolvePalIcon(palId),
                    ["levelMin"] = palEntry["Level"],
                    ["levelMax"] = palEntry["Level_Max"],
                    ["numMin"] = palEntry["Num"],
                    ["numMax"] = palEntry["Num_Max"],
                    ["isHuman"] = isHuman,
                });
            }
            groupsArr.Add(new JObject
            {
                ["weight"] = group["Weight"],
                ["pals"] = pals,
            });
        }
    }

    foreach (var (tierKey, groupsArr) in tierGroups)
    {
        tiers[tierKey] = new JObject
        {
            ["guaranteed"] = groupsArr.Count == 1,
            ["groups"] = groupsArr,
        };
    }

    dungeonContents[areaId] = new JObject
    {
        ["biomeLabel"] = DungeonAreaLabel(areaId),
        ["tiers"] = tiers,
    };
}

// Sanity checks against known-good figures from the scratch investigation
// (see NOTES.md) - logged, not asserted/thrown, so a real future game
// content patch that shifts these numbers doesn't hard-fail the pipeline,
// just gets flagged for a human to look at.
var grass001Boss = (((dungeonContents["Grass001"] as JObject)?["tiers"] as JObject)?["Boss"] as JObject)?["groups"] as JArray;
var yakushimaBoss = (((dungeonContents["Yakushima001"] as JObject)?["tiers"] as JObject)?["Boss"] as JObject)?["groups"] as JArray;
Console.WriteLine($"Sanity: Grass001 Boss groups = {grass001Boss?.Count} (expect ~31), Yakushima001 Boss groups = {yakushimaBoss?.Count} (expect 1, Eye of Cthulhu Lv45)");
if (yakushimaBoss?.Count == 1)
{
    var soleName = yakushimaBoss[0]?["pals"]?[0]?["name"]?.ToString();
    var soleLevel = yakushimaBoss[0]?["pals"]?[0]?["levelMin"]?.ToString();
    Console.WriteLine($"  Yakushima001 sole Boss: {soleName} Lv{soleLevel}");
}

Console.WriteLine($"Total dungeon content areas: {dungeonContents.Count} (expect 14)");
File.WriteAllText(@"C:\Projects\PalworldMap\data\dungeon_contents_static.json", dungeonContents.ToString(Formatting.Indented));
Console.WriteLine("Wrote data/dungeon_contents_static.json");

// ============ Pal Spawn Locations (wild field spawns, pals only) ============
// "Spawn locations" as flat highlighted regions rather than a color-gradient
// heatmap, per explicit user request - strictly regular wild-Pal field
// spawns, NOT alpha/field bosses, NOT dungeon trash/boss spawns, NOT human
// NPC patrols. Defaults to nothing shown on the frontend (the one section in
// this app that doesn't default to fully visible - see frontend/index.html).
//
// DT_PalSpawnerPlacement (Pal/Content/Pal/DataTable/Spawner/DT_PalSpawnerPlacement,
// a CompositeDataTable, 8253 rows) is the master spawn-point placement table
// for every spawner category in the game. Filtering to
// SpawnerType::Common + PlacementType::Field is exactly the regular wild-Pal
// field spawns wanted (~7474 rows) - excludes SpawnerType::Common +
// PlacementType::Dungeon (dungeon trash, already covered by Dungeon Contents
// above), SpawnerType::FieldBoss (the existing Alpha Bosses section,
// backend/bosses.py/DT_BossSpawnerLoactionData), SpawnerType::RandomDungeonBoss
// (dungeon bosses, also covered by Dungeon Contents), and
// SpawnerType::ImprisonmentBoss (a separate boss category, out of scope
// here).
var spawnerPlacementRows = LoadRows("Pal/Content/Pal/DataTable/Spawner/DT_PalSpawnerPlacement");
var wildSpawnerRows = LoadRows("Pal/Content/Pal/DataTable/Spawner/DT_PalWildSpawner");

// Species/level resolution: join each placement row's SpawnerName field
// against DT_PalWildSpawner BY THAT TABLE'S OWN "SpawnerName" PROPERTY, NOT
// the dict row-key - confirmed 7403/7474 match via the field vs only 73/7474
// via the dict key, the same "don't get this backwards" gotcha as
// DT_BossSpawnerLoactionData.SpawnerID elsewhere in this pipeline. Multiple
// DT_PalWildSpawner rows can share one SpawnerName (a weighted candidate
// pool, one row per candidate - NOT a single row containing a groups array
// like the dungeon tables), so this builds a one-to-many lookup.
var wildSpawnersBySpawnerName = new Dictionary<string, List<JObject>>();
foreach (var prop in wildSpawnerRows.Properties())
{
    var row = (JObject)prop.Value;
    var spawnerName = row["SpawnerName"]?.ToString();
    if (string.IsNullOrEmpty(spawnerName)) continue;
    if (!wildSpawnersBySpawnerName.TryGetValue(spawnerName, out var list))
    {
        list = new List<JObject>();
        wildSpawnersBySpawnerName[spawnerName] = list;
    }
    list.Add(row);
}

// Pal_N/NPC_N are plain FName-style fields in this table (not a RowHandle
// struct like the dungeon roster's PalId/NPCID), but handled defensively for
// either shape (a bare string, or a {Key: ...}/{RowName: ...} object) rather
// than assuming.
string? RowKeyOf(JToken? t)
{
    if (t == null) return null;
    if (t is JObject o) return o["Key"]?.ToString() ?? o["RowName"]?.ToString();
    var s = t.ToString();
    return string.IsNullOrEmpty(s) ? null : s;
}

var palSpawnLocations = new JObject();
// characterId -> {x,y} pairs already recorded, defensive dedupe for the case
// (shouldn't happen - one location = one SpawnerName = one candidate pool -
// but be defensive) where the same species is reachable via multiple
// SpawnerName/candidate rows at the exact same location.
var palSpawnSeenLocations = new Dictionary<string, HashSet<(double x, double y)>>();

// characterId -> resolved name (or null), cached so ResolvePalName only
// actually runs once per distinct species rather than once per occurrence -
// a popular species can appear in hundreds of location slots.
var palNameResolutionCache = new Dictionary<string, string?>();

int placementFieldCommonRows = 0, placementNoWildSpawnerMatch = 0, placementResolvedSlots = 0, unresolvedNameSlots = 0;

foreach (var prop in spawnerPlacementRows.Properties())
{
    var row = (JObject)prop.Value;
    var spawnerType = ElementString(row["SpawnerType"]);
    var placementType = ElementString(row["PlacementType"]);
    if (spawnerType != "Common" || placementType != "Field") continue;
    placementFieldCommonRows++;

    var spawnerName = row["SpawnerName"]?.ToString();
    var loc = row["Location"];
    var xTok = loc?["X"];
    var yTok = loc?["Y"];
    if (string.IsNullOrEmpty(spawnerName) || xTok == null || yTok == null)
    {
        placementNoWildSpawnerMatch++;
        continue;
    }
    var x = (double)xTok;
    var y = (double)yTok;
    // Always read the row's own StaticRadius, never hardcode the common
    // 15000.0 value - a few rows might genuinely differ.
    var staticRadius = (double?)row["StaticRadius"] ?? 15000.0;

    if (!wildSpawnersBySpawnerName.TryGetValue(spawnerName, out var candidates))
    {
        placementNoWildSpawnerMatch++;
        continue;
    }

    bool anySlotResolved = false;
    foreach (var candidate in candidates)
    {
        // "Strictly pals only" per the user's explicit requirement: a slot
        // only counts when Pal_N is a real species - exclude Pal_N == "None"
        // AND Pal_N == "RowName" (a literal leftover template-default string
        // found on at least one real stub row, e.g. grass_FBOSS_1_1 - also
        // has Weight: 0.0, so the Weight<=0 guard below catches it too, kept
        // as a second explicit guard rather than relying on Weight alone).
        // Skip any candidate where NPC_N is set instead of Pal_N (human, not
        // a Pal - RadiusType::NPC rows, 33 of them, some Field spawners are
        // human patrols using NPC_N slots - this is the "other enemies" the
        // user wants excluded).
        var weight = (double?)candidate["Weight"] ?? 0.0;
        if (weight <= 0) continue;

        for (int slot = 1; slot <= 3; slot++)
        {
            var palKey = RowKeyOf(candidate[$"Pal_{slot}"]);
            var npcKey = RowKeyOf(candidate[$"NPC_{slot}"]);
            if (!string.IsNullOrEmpty(npcKey) && npcKey != "None") continue;
            if (string.IsNullOrEmpty(palKey) || palKey == "None" || palKey == "RowName") continue;

            // Key by the base non-BOSS_ CharacterID (e.g. "Alpaca" not
            // "BOSS_Alpaca") - wild field spawns are already the common form
            // in practice, but strip defensively rather than assume.
            var characterId = palKey.StartsWith("BOSS_", StringComparison.OrdinalIgnoreCase)
                ? palKey["BOSS_".Length..] : palKey;

            // A genuine remaining resolution gap exists and must be dropped,
            // not fabricated: confirmed via a throwaway scratch investigation
            // (extractor/ScratchSpawns, same "verify before shipping" rule as
            // Dungeon Contents) that 261 of 262 distinct species reachable
            // from Field+Common placements resolve a real name cleanly - the
            // sole miss, "Male_NinjaElite01", is a human character mistakenly
            // placed in a Pal_2 slot (its sibling Pal_1 slot on the same row
            // correctly uses NPC_1 for the human "Male_Ninja01") rather than
            // NPC_2 - it fails name resolution and must NOT be shown as a
            // fake "pal" species using its raw internal id as a display name,
            // which would violate this feature's explicit "strictly pals
            // only" requirement.
            if (!palNameResolutionCache.TryGetValue(characterId, out var resolvedName))
            {
                resolvedName = ResolvePalName(characterId);
                palNameResolutionCache[characterId] = resolvedName;
            }
            if (resolvedName == null) { unresolvedNameSlots++; continue; }

            anySlotResolved = true;

            if (!palSpawnSeenLocations.TryGetValue(characterId, out var seenLocs))
            {
                seenLocs = new HashSet<(double, double)>();
                palSpawnSeenLocations[characterId] = seenLocs;
            }
            if (!seenLocs.Add((x, y))) continue; // dedupe same species @ same location

            if (!palSpawnLocations.TryGetValue(characterId, out var speciesEntryTok))
            {
                speciesEntryTok = new JObject
                {
                    ["name"] = resolvedName,
                    ["icon"] = ResolvePalIcon(characterId),
                    ["locations"] = new JArray(),
                };
                palSpawnLocations[characterId] = speciesEntryTok;
            }
            ((JArray)speciesEntryTok!["locations"]!).Add(new JObject
            {
                ["x"] = x,
                ["y"] = y,
                ["radius"] = staticRadius,
                ["levelMin"] = candidate[$"LvMin_{slot}"],
                ["levelMax"] = candidate[$"LvMax_{slot}"],
            });
            placementResolvedSlots++;
        }
    }
    if (!anySlotResolved) placementNoWildSpawnerMatch++;
}

Console.WriteLine(
    $"Pal Spawn Locations: {placementFieldCommonRows} SpawnerType::Common+PlacementType::Field rows " +
    $"(expect ~7474), {placementFieldCommonRows - placementNoWildSpawnerMatch} resolved >=1 real Pal slot, " +
    $"{placementNoWildSpawnerMatch} empty/NPC-only/stub/unmatched/no-wildspawner-match, " +
    $"{unresolvedNameSlots} slots dropped for a genuine name-resolution gap (expect 1: Male_NinjaElite01), " +
    $"{placementResolvedSlots} total species-location entries, {palSpawnLocations.Count} distinct species.");
File.WriteAllText(@"C:\Projects\PalworldMap\data\pal_spawn_locations_static.json", palSpawnLocations.ToString(Formatting.Indented));
Console.WriteLine("Wrote data/pal_spawn_locations_static.json");

// ============ Journals (internally "Notes") ============
// Collectible lore pickups, shown to the user as "Journals" - internally the
// game calls these "Notes" (Blueprint BP_LevelObject_Note_C, DataTables
// DT_NoteMasterDataTable/DT_NoteTextureDataTable/DT_NoteDescText). 64 total:
// 23 "Castaway's Journal" story entries (row keys "Day0".."Day38") found
// scattered on the main island, plus 41 NPC "Diary" entries tied to each of
// the 9 Tower regions (e.g. "GrassBoss1".."GrassBoss5" = Zoe Rayne's Diary),
// found inside/near that region's Tower dungeon.
//
// Position: same actor-scan technique as effigies/watchtowers - resolve
// RootComponent -> RelativeLocation. Unlike effigies (streamed World
// Partition only) or watchtowers (persistent level only), these are split
// across BOTH: the 15 Sorajima(Skyisland)/WorldTree region notes live in the
// persistent level (PL_MainWorld5, always-loaded DLC landmarks, same as
// Watchtowers/Waypoints), while the other 49 (Day notes + the remaining 7
// Tower regions) are streamed World Partition actors under _Generated_
// (same as Effigies) - confirmed by scanning both and getting 15+49=64,
// exactly matching DA_NoteDataAsset's NoteDataMap count with zero overlap
// or gap. The World Partition half of this scan is the expensive one - see
// the ~4-6 min full-cell-scan warning at the top of this file.
//
// Real note ID -> region key for the 41 Diary notes (used only to derive
// the checklist group's display name below, via the same
// regionNameRowsChk/regionTextKeyOverride tables the Towers section above
// already loaded) - order matters, "WorldTreeBoss" must be checked before
// "WorldTree" since it's a longer prefix match on the same string.
var noteGroupPrefixToRegion = new (string prefix, string region)[]
{
    ("WorldTreeBoss", "WorldTree_Boss"),
    ("GrassBoss", "Grass_Boss"),
    ("ForestBoss", "Forest_Boss"),
    ("DesertBoss", "Desert_Boss"),
    ("SnowBoss", "Frost_Boss"),
    ("VolcanoBoss", "Volcano_Boss"),
    ("SakurajimaBoss", "Sakurajima_Boss"),
    ("VikingBoss", "Darkisland_Boss"),
    ("SorajimaBoss", "Skyisland_Boss"),
};
(string groupKey, string groupName) NoteGroupFor(string noteId)
{
    foreach (var (prefix, region) in noteGroupPrefixToRegion)
    {
        if (!noteId.StartsWith(prefix, StringComparison.Ordinal)) continue;
        var textKey = regionTextKeyOverride.GetValueOrDefault(region, "REGION_" + region);
        var name = (regionNameRowsChk?[textKey] as JObject)?["TextData"]?["LocalizedString"]?.ToString() ?? region;
        return (region, name);
    }
    // "WorldTree1".."WorldTree3" - general World Tree DLC zone exploration
    // notes, distinct from the "WorldTreeBoss" ones tied to the Tower itself.
    // No dedicated region text row for this subset; the zone's own common
    // name is used directly (same "World Tree" name used elsewhere in this
    // pipeline/frontend for the DLC area as a whole).
    if (noteId.StartsWith("WorldTree", StringComparison.Ordinal)) return ("WorldTree", "World Tree");
    return ("CastawayJournal", "Castaway's Journal");
}

var noteDescRows = LoadRows("Pal/Content/L10N/en/Pal/DataTable/Text/DT_NoteDescText");
var noteTextureRows = LoadRows("Pal/Content/Pal/DataTable/NoteData/DT_NoteTextureDataTable");

var noteIconOutDir = @"C:\Projects\PalworldMap\frontend\assets\note_icons";
Directory.CreateDirectory(noteIconOutDir);
var noteExportedIcons = new HashSet<string>();
void ExportNoteIcon(string assetPathName, string outFileName)
{
    if (noteExportedIcons.Contains(outFileName)) return;
    noteExportedIcons.Add(outFileName);
    var withoutObjectName = assetPathName[..assetPathName.LastIndexOf('.')];
    var objPath = withoutObjectName.Replace("/Game/", "Pal/Content/");
    var exports = provider.LoadPackageObjects(objPath).ToList();
    var tex = exports.OfType<UTexture2D>().FirstOrDefault();
    if (tex == null) return;
    var decoded = tex.Decode();
    if (decoded == null) return;
    var bytes = TextureEncoder.Encode(decoded, ETextureFormat.Png, false, out _);
    // These are full-res in-world "photo" textures (up to ~3800x2100, 8-15MB
    // each as a lossless PNG straight off the source asset) meant for a
    // full-screen note-reading UI, not a 30px map marker - no small compact
    // icon exists anywhere in the game's own data for this (checked
    // DT_LocationUIData and every Icon-ish/notebook/diary/document texture
    // path - nothing). Downscale + web-optimize via the same shared
    // DownscalePng every other icon exporter uses (see its own comment) -
    // this used to be its own separate inlined resize+encode, duplicating
    // logic that now also does palette quantization, no reason to keep two
    // copies.
    File.WriteAllBytes(Path.Combine(noteIconOutDir, outFileName), DownscalePng(bytes));
}

// Collect raw {noteId, instanceId, x, y, z} from both sources first, then
// join text/texture below - keeps the (expensive) World Partition scan free
// of per-row JSON lookups.
var noteRawPositions = new List<(string id, string instanceId, double x, double y, double z)>();

// Schematics (internally "ItemPickupTower", see below) live in exactly the
// same two places as Journal notes - 2 in the persistent level, the rest
// streamed under World Partition _Generated_ - so their scan is folded into
// this same pass rather than paying for a second ~4-6 min full-map walk.
var schematicRawPositions = new List<(string id, string instanceId, double x, double y, double z)>();

// NPCs (Trader / Black Market / Dog Coin / General) are spawned at runtime
// by a BP_MonoNPCSpawner-family actor (persistent level + streamed
// World Partition, same two-place split as Notes/Schematics) rather than
// hand-placed as their own character Blueprint - confirmed by inspecting
// real placed instances: each spawner's Properties.UniqueName.Key is a
// real, exact foreign key into DT_UniqueNPC's row names (e.g. "DarkTrader",
// "U_Male_SorajimaPeople01") - not a naming-convention guess. Two extra
// classes beyond the plain base were needed after the first pass came back
// with real, important NPCs unexplained (MedalTrader - our sole Dog Coin
// NPC - was completely missing): BP_MonoNPCSpawner_Unique_C is a bare
// subclass with no own properties (safe to treat identically), while
// BP_MonoNPCSpawner_MedalTrader_C (in its own dedicated
// Spawner/UniqueNPC/ subfolder) bakes UniqueName/Level as its own class
// defaults rather than a per-instance override - npcClassDefaultUniqueName/
// npcClassDefaultLevel below are the fallback for that one case.
// Deliberately excluded: "_Quest"-suffixed / BP_QuestTargetNPCSpawner_*
// variants (key off HumanName + a quest-state-gated SpawnerRuleClass, not
// UniqueName - functionally the same "go here" concept the Quests feature
// already covers, so including them would just duplicate that) and
// Spawner/HumanNPCBoss/* (a separate mechanism for the human Bounty targets
// already covered by bosses.py/DT_BossSpawnerLoactionData).
var npcSpawnerClasses = new HashSet<string> { "BP_MonoNPCSpawner_C", "BP_MonoNPCSpawner_Unique_C", "BP_MonoNPCSpawner_MedalTrader_C" };
var npcClassDefaultUniqueName = new Dictionary<string, string> { ["BP_MonoNPCSpawner_MedalTrader_C"] = "MedalTrader" };
var npcClassDefaultLevel = new Dictionary<string, int> { ["BP_MonoNPCSpawner_MedalTrader_C"] = 50 };
var npcRawPositions = new List<(string uniqueName, int? level, double x, double y, double z)>();

void CollectNpcSpawner(List<CUE4Parse.UE4.Assets.Exports.UObject> exports, int idx)
{
    var exp = exports[idx];
    var cn = exp.Class?.Name.ToString() ?? "";
    if (!npcSpawnerClasses.Contains(cn)) return;
    var j = JObject.Parse(JsonConvert.SerializeObject(exp));
    var props = j["Properties"] as JObject;
    var uniqueName = props?["UniqueName"]?["Key"]?.ToString();
    if (string.IsNullOrEmpty(uniqueName) || uniqueName == "None")
        uniqueName = npcClassDefaultUniqueName.GetValueOrDefault(cn);
    if (string.IsNullOrEmpty(uniqueName)) return;
    var level = props?["Level"]?.ToObject<int?>() ?? npcClassDefaultLevel.GetValueOrDefault(cn);
    var rootPath = props?["RootComponent"]?["ObjectPath"]?.ToString();
    if (rootPath == null) return;
    var rootIdxStr = rootPath[(rootPath.LastIndexOf('.') + 1)..];
    if (!int.TryParse(rootIdxStr, out var rootIdx) || rootIdx < 0 || rootIdx >= exports.Count) return;
    var rootObj = JObject.Parse(JsonConvert.SerializeObject(exports[rootIdx]));
    var loc = (rootObj["Properties"] as JObject)?["RelativeLocation"];
    if (loc == null) return;
    npcRawPositions.Add((uniqueName, level, (double)loc["X"]!, (double)loc["Y"]!, (double)loc["Z"]!));
}

void CollectLevelObjectPosition<T>(List<T> results, List<CUE4Parse.UE4.Assets.Exports.UObject> exports, int idx,
    string wantedClass, string rowNamePropertyKey, Func<string, string, double, double, double, T> make)
{
    var exp = exports[idx];
    if ((exp.Class?.Name.ToString() ?? "") != wantedClass) return;
    var j = JObject.Parse(JsonConvert.SerializeObject(exp));
    var props = j["Properties"] as JObject;
    var rowName = props?[rowNamePropertyKey]?["Key"]?.ToString();
    var instanceId = props?["LevelObjectInstanceId"]?.ToString();
    if (rowName == null || instanceId == null) return;
    JObject? rootObj;
    var rootPath = props?["RootComponent"]?["ObjectPath"]?.ToString();
    if (rootPath == null) return;
    var rootIdxStr = rootPath[(rootPath.LastIndexOf('.') + 1)..];
    if (!int.TryParse(rootIdxStr, out var rootIdx) || rootIdx < 0 || rootIdx >= exports.Count) return;
    rootObj = JObject.Parse(JsonConvert.SerializeObject(exports[rootIdx]));
    var loc = (rootObj["Properties"] as JObject)?["RelativeLocation"];
    if (loc == null) return;
    results.Add(make(rowName, instanceId, (double)loc["X"]!, (double)loc["Y"]!, (double)loc["Z"]!));
}

foreach (var exp in persistentLevelExports)
{
    if ((exp.Class?.Name.ToString() ?? "") != "BP_LevelObject_Note_C") continue;
    var j = JObject.Parse(JsonConvert.SerializeObject(exp));
    var props = j["Properties"] as JObject;
    var rowName = props?["NoteRowName"]?["Key"]?.ToString();
    var instanceId = props?["LevelObjectInstanceId"]?.ToString();
    if (rowName == null || instanceId == null) continue;
    var rootObj = ResolveExportRef(persistentLevelExports, props?["RootComponent"]);
    var loc = (rootObj?["Properties"] as JObject)?["RelativeLocation"];
    if (loc == null) continue;
    noteRawPositions.Add((rowName, instanceId, (double)loc["X"]!, (double)loc["Y"]!, (double)loc["Z"]!));
}
foreach (var exp in persistentLevelExports)
{
    if ((exp.Class?.Name.ToString() ?? "") != "BP_LevelObject_ItemPickupTower_C") continue;
    var j = JObject.Parse(JsonConvert.SerializeObject(exp));
    var props = j["Properties"] as JObject;
    var rowName = props?["ItemPickupRowName"]?["Key"]?.ToString();
    var instanceId = props?["LevelObjectInstanceId"]?.ToString();
    if (rowName == null || instanceId == null) continue;
    var rootObj = ResolveExportRef(persistentLevelExports, props?["RootComponent"]);
    var loc = (rootObj?["Properties"] as JObject)?["RelativeLocation"];
    if (loc == null) continue;
    schematicRawPositions.Add((rowName, instanceId, (double)loc["X"]!, (double)loc["Y"]!, (double)loc["Z"]!));
}
for (int idx = 0; idx < persistentLevelExports.Count; idx++) CollectNpcSpawner(persistentLevelExports, idx);

Console.WriteLine("Scanning World Partition cells for the remaining Journal notes + Schematics + NPC spawners...");
var cellPaths = provider.Files.Keys
    .Where(k => k.ToString().Contains("Pal/Content/Pal/Maps/MainWorld_5/PL_MainWorld5/_Generated_/", StringComparison.OrdinalIgnoreCase)
             && k.ToString().EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
    .Select(k => k.ToString())
    .ToList();
int noteScanned = 0;
foreach (var cellPath in cellPaths)
{
    noteScanned++;
    if (noteScanned % 1000 == 0) Console.WriteLine($"...scanned {noteScanned}/{cellPaths.Count}");
    List<CUE4Parse.UE4.Assets.Exports.UObject> exports;
    try { exports = provider.LoadPackageObjects(cellPath).ToList(); }
    catch { continue; }
    for (int idx = 0; idx < exports.Count; idx++)
    {
        CollectLevelObjectPosition(noteRawPositions, exports, idx, "BP_LevelObject_Note_C", "NoteRowName",
            (id, instId, x, y, z) => (id, instId, x, y, z));
        CollectLevelObjectPosition(schematicRawPositions, exports, idx, "BP_LevelObject_ItemPickupTower_C", "ItemPickupRowName",
            (id, instId, x, y, z) => (id, instId, x, y, z));
        CollectNpcSpawner(exports, idx);
    }
}

var noteResult = new JArray();
foreach (var (id, instanceId, x, y, z) in noteRawPositions)
{
    var (groupKey, groupName) = NoteGroupFor(id);
    var fullText = noteDescRows[id]?["TextData"]?["LocalizedString"]?.ToString() ?? id;
    // Every note's text is "<title line>\r\n\r\n<body>" (e.g. "Zoe Rayne's
    // Diary - 1\r\n\r\nI don't have a family...") - split on the first blank
    // line to get a short per-entry title distinct from the group name above
    // (e.g. group "Rayne Syndicate Tower", title "Zoe Rayne's Diary - 1").
    var splitIdx = fullText.IndexOf("\r\n\r\n", StringComparison.Ordinal);
    var title = splitIdx >= 0 ? fullText[..splitIdx] : fullText;
    var body = splitIdx >= 0 ? fullText[(splitIdx + 4)..] : "";
    var preview = body.Replace("\r\n", " ").Replace("|", "").Trim();
    if (preview.Length > 160) preview = preview[..160].TrimEnd() + "...";

    var texturePath = noteTextureRows[id]?["Texture"]?["AssetPathName"]?.ToString();
    string? icon = null;
    if (!string.IsNullOrEmpty(texturePath))
    {
        var fileName = "T_Note_" + id + ".png";
        try
        {
            ExportNoteIcon(texturePath, fileName);
            icon = fileName;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  note icon export failed for {id}: {ex.Message}");
        }
    }

    noteResult.Add(new JObject
    {
        ["id"] = id,
        ["groupKey"] = groupKey,
        ["groupName"] = groupName,
        ["title"] = title,
        ["preview"] = preview,
        ["icon"] = icon,
        ["instanceId"] = instanceId,
        ["x"] = x,
        ["y"] = y,
        ["z"] = z,
    });
}
Console.WriteLine($"Total Journal notes: {noteResult.Count}, icons exported: {noteExportedIcons.Count}");
File.WriteAllText(@"C:\Projects\PalworldMap\data\notes_static.json", noteResult.ToString(Formatting.Indented));
Console.WriteLine("Wrote data/notes_static.json");

// ============ Schematics (internally "ItemPickupTower") ============
// The user-facing name "Schematics" comes straight from the game's own item
// names (e.g. "Old Revolver Schematic 2") - these are the ancient-shrine
// terminal pickups scattered across the map (mesh SM_AncientShrine, actor
// class BP_LevelObject_ItemPickupTower_C), granting a one-time item + Dog
// Coin reward. 106 total: 2 in the persistent level, 104 streamed under
// World Partition (scan folded into the Journals pass above, same "lives in
// both places" split as Journals) - exactly matching
// DT_ItemPickupDataTable's 107 rows minus one unused "Test_GrassLand01" test
// row, confirmed exhaustive.
//
// Reward resolution: each DT_ItemPickupDataTable row has Item_01_Id (the
// real reward - usually "Blueprint_<Weapon/Armor/Accessory>_N" a weapon/
// armor/accessory Schematic, but some rows grant a non-Schematic consumable
// instead, e.g. "WorkSuitability_AddTicket_Seeding" -> "Applied Planting
// Handbook I" or "PalPassiveSkillChange_Consumable_MoveSpeed_up_3" ->
// "Disposable Implant: Swift" - always resolved via the same generic path
// below, no special-casing needed) and Item_02_Id (always "DogCoin", amount
// varies 20-50). Display name: DT_ItemDataTable's OverrideName is always
// "None" for these, so the real name comes from DT_ItemNameText_Common
// keyed by "ITEM_NAME_<ItemId>_TextData" (a different key pattern than the
// direct-row-name lookups used elsewhere in this pipeline). Icon: resolve
// DT_ItemDataTable[itemId].IconName (NOT the item's own row name - most
// Schematics share one generic "Blueprint" IconName, e.g.
// T_itemicon_Material_Blueprint, since there's no unique per-weapon icon)
// against DT_ItemIconDataTable for the actual texture.
//
// Per-player "ever picked up" state: SaveData.RecordData.ItemPickupObtainForInstanceFlag,
// keyed by the same LevelObjectInstanceId GUID scheme (dashes stripped,
// uppercase) as Effigies/Watchtowers/Waypoints - confirmed byte-for-byte
// against real players' saves. See parse.load_collected_schematic_ids().
var itemPickupRows = LoadRows("Pal/Content/Pal/DataTable/Item/DT_ItemPickupDataTable");
var itemDataRows = LoadRows("Pal/Content/Pal/DataTable/Item/DT_ItemDataTable");
var itemIconRows = LoadRows("Pal/Content/Pal/DataTable/Item/DT_ItemIconDataTable");
var itemNameRows = LoadRows("Pal/Content/L10N/en/Pal/DataTable/Text/DT_ItemNameText_Common");

var schematicIconOutDir = @"C:\Projects\PalworldMap\frontend\assets\schematic_icons";
Directory.CreateDirectory(schematicIconOutDir);
var schematicExportedIcons = new HashSet<string>();
void ExportSchematicIcon(string assetPathName, string outFileName)
{
    if (schematicExportedIcons.Contains(outFileName)) return;
    schematicExportedIcons.Add(outFileName);
    var withoutObjectName = assetPathName[..assetPathName.LastIndexOf('.')];
    var objPath = withoutObjectName.Replace("/Game/", "Pal/Content/");
    var exports = provider.LoadPackageObjects(objPath).ToList();
    var tex = exports.OfType<UTexture2D>().FirstOrDefault();
    if (tex == null) return;
    var decoded = tex.Decode();
    if (decoded == null) return;
    var bytes = DownscalePng(TextureEncoder.Encode(decoded, ETextureFormat.Png, false, out _));
    File.WriteAllBytes(Path.Combine(schematicIconOutDir, outFileName), bytes);
}

string ItemDisplayName(string itemId) =>
    itemNameRows["ITEM_NAME_" + itemId]?["TextData"]?["LocalizedString"]?.ToString() ?? itemId;

string? ExportItemIcon(string itemId)
{
    var itemRow = itemDataRows[itemId] as JObject;
    var iconName = itemRow?["IconName"]?.ToString();
    if (string.IsNullOrEmpty(iconName) || iconName == "None") iconName = itemId;
    var iconAssetPath = (itemIconRows[iconName] as JObject)?["Icon"]?["AssetPathName"]?.ToString();
    if (string.IsNullOrEmpty(iconAssetPath)) return null;
    var fileName = "T_itemicon_" + iconName + ".png";
    try
    {
        ExportSchematicIcon(iconAssetPath, fileName);
        return fileName;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  schematic icon export failed for {itemId} (icon {iconName}): {ex.Message}");
        return null;
    }
}

var schematicResult = new JArray();
foreach (var (id, instanceId, x, y, z) in schematicRawPositions)
{
    var pickupRow = itemPickupRows[id] as JObject;
    var rewardItemId = pickupRow?["Item_01_Id"]?.ToString();
    var rewardItemNum = (int?)pickupRow?["Item_01_Num"] ?? 1;
    var bonusItemId = pickupRow?["Item_02_Id"]?.ToString();
    var bonusItemNum = (int?)pickupRow?["Item_02_Num"] ?? 0;
    if (string.IsNullOrEmpty(rewardItemId) || rewardItemId == "None") continue;

    var name = ItemDisplayName(rewardItemId);
    var icon = ExportItemIcon(rewardItemId);
    string? bonusText = (!string.IsNullOrEmpty(bonusItemId) && bonusItemId != "None" && bonusItemNum > 0)
        ? $"+{bonusItemNum} {ItemDisplayName(bonusItemId)}"
        : null;

    schematicResult.Add(new JObject
    {
        ["id"] = id,
        ["name"] = name,
        ["icon"] = icon,
        ["bonus"] = bonusText,
        ["instanceId"] = instanceId,
        ["x"] = x,
        ["y"] = y,
        ["z"] = z,
    });
}
Console.WriteLine($"Total Schematics: {schematicResult.Count}, icons exported: {schematicExportedIcons.Count}");
File.WriteAllText(@"C:\Projects\PalworldMap\data\schematics_static.json", schematicResult.ToString(Formatting.Indented));
Console.WriteLine("Wrote data/schematics_static.json");

// ============ Item shop tables (shared: NPCs' Medal/Bounty/Arena/Wandering
// Merchant entries below all need these) ============
// Each shop-NPC's BP_PalShopVenderDataComponent has an
// itemShopSimpleLotteryTableName (-> this DT_ItemShopLotteryData row's
// lotteryDataArray -> a ShopGroupName -> DT_ItemShopCreateData's real
// productDataArray of StaticItemIds, resolved via the same
// ItemDisplayName/ExportItemIcon helpers the Schematics section above
// already defines). Confirmed for every table used below: a single
// lotteryDataArray entry at Weight 100, i.e. a deterministic 100%-chance
// pointer to one ShopGroupName, not a real item-level random draw - so
// "items" below is the genuine complete, always-available inventory, not a
// lottery-selected subset (contrast the Pal Dealer/Black Market pal pool
// further down, which IS a real subset-per-restock pool).
//
// Price (2026-07-20, for the in-game-style shop modal): each product's own
// OverridePrice is 0 for most rows (Medal/Bounty/Arena/Wander alike) -
// that's NOT "free", it's UE's "no override" sentinel, confirmed by
// DT_ItemDataTable itself carrying a real per-item "Price" field (e.g.
// Pal_crystal_S = 70) that matches what the game actually charges. Real
// price = OverridePrice if > 0, else the item's own base Price.
var itemShopLotteryRows = LoadRows("Pal/Content/Pal/DataTable/ItemShop/DT_ItemShopLotteryData");
var itemShopCreateRows = LoadRows("Pal/Content/Pal/DataTable/ItemShop/DT_ItemShopCreateData");
var itemShopSettingRows = LoadRows("Pal/Content/Pal/DataTable/ItemShop/DT_ItemShopSettingData");
// Pal-selling side (Pal Dealer/Black Market's palShopSimpleLotteryTableName)
// - declared here, not down by ResolvePalShopPool's own definition further
// below, because the NPCs section's spawner loop (which calls
// NpcShopInventory -> ResolvePalShopPool for DarkTrader/DarkTrader03)
// needs it in scope first.
var palShopCreateRows = LoadRows("Pal/Content/Pal/DataTable/PalShop/DT_PalShopCreateData");

JArray ResolveItemShopProducts(string lotteryTableKey)
{
    var arr = new JArray();
    var groupName = ((itemShopLotteryRows[lotteryTableKey] as JObject)?["lotteryDataArray"] as JArray)
        ?.FirstOrDefault()?["ShopGroupName"]?.ToString() ?? lotteryTableKey;
    var products = (itemShopCreateRows[groupName] as JObject)?["productDataArray"] as JArray;
    if (products == null) return arr;
    foreach (var p in products)
    {
        var itemId = p["StaticItemId"]?.ToString();
        if (string.IsNullOrEmpty(itemId) || itemId == "None") continue;
        var overridePrice = (int?)p["OverridePrice"] ?? 0;
        var basePrice = (int?)(itemDataRows[itemId] as JObject)?["Price"] ?? 0;
        arr.Add(new JObject
        {
            ["itemId"] = itemId,
            ["name"] = ItemDisplayName(itemId),
            ["icon"] = ExportItemIcon(itemId),
            ["price"] = overridePrice > 0 ? overridePrice : basePrice,
        });
    }
    return arr;
}

// Currency + its own real icon (e.g. "Gold Coin"/T_itemicon_Money.png,
// "Dog Coin"/T_itemicon_DogCoin.png) - every shop has one, defaulting to
// plain Gold ("Money", DT_ItemDataTable's own row for it) when no
// DT_ItemShopSettingData override row exists (Wander/Village/Dark* tables
// have none - confirmed, only Medal/Bounty/Arena do).
(string name, string? icon) TraderCurrencyInfo(string? shopSettingKey)
{
    var currencyId = shopSettingKey != null ? (itemShopSettingRows[shopSettingKey] as JObject)?["CurrencyItemID"]?.ToString() : null;
    if (string.IsNullOrEmpty(currencyId) || currencyId == "None") currencyId = "Money";
    return (ItemDisplayName(currencyId), ExportItemIcon(currencyId));
}

// Real circular species portrait, same table/join the Bosses section above
// already uses for boss icons (DT_PalCharacterIconDataTable, keyed by the
// bare Tribe codename via DT_PalMonsterParameter - NOT always equal to the
// CharacterID, so this re-resolves Tribe per species rather than assuming
// CharacterID==Tribe). Reuses that section's own ExportIcon/boss_icons
// output - same real asset, no reason to export it twice into a separate
// folder just because this feature is a shop modal, not a boss marker.
string PalPortraitIcon(string characterId)
{
    var monster = monsterRows[characterId] as JObject;
    var tribeKey = monster != null ? ElementString(monster["Tribe"]) : characterId;
    if (palIconLookup.TryGetValue(tribeKey, out var palIconProp))
    {
        var iconAssetPath = ((JObject)palIconProp.Value)["Icon"]?["AssetPathName"]?.ToString();
        if (!string.IsNullOrEmpty(iconAssetPath))
        {
            var fileName = "Pal_" + tribeKey + ".png";
            try { ExportIcon(iconAssetPath, fileName); return fileName; }
            catch (Exception ex) { Console.WriteLine($"  pal shop portrait export failed for {tribeKey}: {ex.Message}"); }
        }
    }
    return "T_MobuCitizen_Male_icon_normal.png"; // unreached in practice - every pool species checked has a real portrait
}

// ============ NPCs (Trader / Black Market / Dog Coin / General) ============
// Positions resolved via npcRawPositions above (collected in the same
// persistent-level + World Partition passes as Notes/Schematics - see that
// section's comment for the full spawner-mechanism investigation).
//
// Category classification, from real data, not guesses:
// - Black Market: uniqueName starts with "DarkTrader" (BP_NPC_DarkTrader*
//   character Blueprints exist for exactly this family) - 2 distinct
//   individuals resolved (DarkTrader, DarkTrader03; DarkTrader has 3 spawn
//   points, DarkTrader03 has 1).
// - Dog Coin: uniqueName == "MedalTrader" - confirmed via
//   DT_ItemShopSettingData's "Medal_Shop_1" row, whose CurrencyItemID is
//   literally "DogCoin" (not "Medal", despite the row/NPC name - see
//   NOTES.md). Exactly 1 individual, 1 spawn point.
// - Wandering Merchant / Pal Dealer: NOT resolved via this spawner scan at
//   all - despite real evidence the "Trader"/"SalesPerson" concept exists
//   (BP_NPC_SalesPerson*/PalDealer* character Blueprints, 38 shop configs
//   in DT_ItemShopCreateData, Bobby's DT_UniqueNPC row having
//   OneTalkDTName "ItemShop"), none of those specific named NPCs (Bobby,
//   Johnson, InnkeeperA, Doctor, MerchantwithPAL, DarkTrader02/04) resolved
//   to a spawner anywhere in this scan - they wild-spawn once per server
//   boot via procedural Blueprint logic invisible to this pipeline
//   (confirmed dead end, see NOTES.md). Positions for these two categories
//   are merged in further below instead, from an external source
//   (palpedia.ru's live map data) - see that comment block for the full
//   story, including why "Trader" isn't the category name used.
// - General NPC: everything else with a resolved position - villagers
//   (Farmer/Scholar/Breeder/Ranger/Nomad/regional-people variants),
//   guides/navigators (BountyNavigator_*, Yamishima guides), Head_of_Village,
//   Police_dependable, etc. Excludes two prefixes that resolved with real
//   positions but aren't real talkable characters: "U_Reward_*" (invisible
//   reward-dispenser stubs reusing the NPC system) and "U_Emote_location_*"
//   (background idle-emote trigger points) - same judgment call already
//   applied to the equivalent Quests reward-stub situation.
//
// Shop inventory (items/currency, 2026-07-20): MedalTrader (Dog Coin),
// BountyTrader, and ArenaShop (all three General-category individuals)
// each get real "items"/"currency" fields via the shared
// ResolveItemShopProducts/TraderCurrency helpers above. This was
// discovered while building a separate Traders section (further down,
// covering Wandering Merchant/Pal Dealer positions sourced from
// palpedia.ru) - that site's "medal"/"bounty"/"arena" merchant categories
// turned out to be EXACT coordinate duplicates of these three already-
// resolved NPCs (confirmed by direct comparison, not assumed), so rather
// than show the same marker twice under two different sections, the real
// inventory data gets merged onto the entries here instead, and Traders
// only keeps the two categories (Wandering Merchant, Pal Dealer) that
// were genuinely never findable via this NPCs pipeline at all.
var npcNameRows = LoadRows("Pal/Content/L10N/en/Pal/DataTable/Text/DT_UniqueNPCText_Common");
var uniqueNpcRowsForNpcs = LoadRows("Pal/Content/Pal/DataTable/Character/DT_UniqueNPC");
var npcExcludedPrefixes = new[] { "U_Reward_", "U_Emote_location_" };

string ReadableNpcName(string uniqueName)
{
    var stripped = uniqueName.StartsWith("U_", StringComparison.Ordinal) ? uniqueName["U_".Length..] : uniqueName;
    var withSpaces = System.Text.RegularExpressions.Regex.Replace(stripped, "(?<!^)([A-Z])", " $1");
    var collapsed = System.Text.RegularExpressions.Regex.Replace(withSpaces.Replace("_", " "), @"\s+", " ");
    return collapsed.Trim();
}

string NpcCategory(string uniqueName)
{
    if (uniqueName.StartsWith("DarkTrader", StringComparison.Ordinal)) return "BlackMarket";
    if (uniqueName == "MedalTrader") return "DogCoin";
    return "General";
}

// Per-individual real portraits, not just a per-category fallback -
// PalIcon/Normal turns out to have near-complete per-archetype coverage
// (e.g. "U_Female_Farmer01_v01" -> "T_Female_Farmer01_v01_icon_normal",
// stripping "U_" - confirmed by visual check, a farmer girl with a straw
// hat, matching names like "Foodie Farmer"). Zoe (DT_UniqueNPC row
// "GrassBoss") has her own dedicated portrait, "T_Human_GrassBoss_icon_normal"
// - confirmed to genuinely be her (pink/white hair, black beanie, matches
// the known "Zoe & Grizzbolt" Tower design), not a guess from the name
// alone. Police-flavor names (Police_dependable/Police_WarningOilrig/
// DesertPolice*/VolcanoPolice*) share "T_Police_icon_normal" (a PIDF
// officer, visually confirmed) - no per-individual portrait exists for
// these, but a real "an officer" look beats the generic villager fallback.
// Matching is case-insensitive against the actual game file list (not
// blindly constructed) since casing isn't consistent -
// "T_Male_Scholar01_v02_Icon_normal" capitalizes "Icon", everything else
// doesn't. MobuCitizen_Male (the game's own generic-villager archetype,
// used for its own basic named NPCs like Johnson/Bobby in DT_UniqueNPC) is
// the final fallback for anything with no per-individual or per-flavor
// match. Originally used Trader01_v04 (the "Wandering Merchant"
// SalesPerson's own real look) as that fallback - switched away once that
// turned out to be a specific, distinct NPC identity (see the Trader
// investigation further down this file), not a generic look.
// (normalIconLookup itself now lives up near the Dungeon Contents section,
// shared with DungeonHumanIcon below - moved there since it needs to run
// before that section, not after; nothing here changed except its
// declaration site.)

var npcPoliceNames = new HashSet<string> { "Police_dependable", "Police_WarningOilrig", "DesertPolice001", "DesertPolice002", "DesertPolice003", "VolcanoPolice001", "VolcanoPolice002" };
var npcUsedIcons = new HashSet<string> { "T_Male_DarkTrader01_icon_normal", "T_MobuCitizen_Male_icon_normal" }; // always-present fallbacks

string NpcIcon(string uniqueName, string category)
{
    if (category == "BlackMarket") return "T_Male_DarkTrader01_icon_normal";
    // MedalTrader's own Blueprint (Character/NPC/Fat/BP_NPC_MedalTrader)
    // literally reuses the SK_NPC_Male_DarkTrader02 skeletal mesh - not a
    // guess, confirmed by reading its CharacterMesh0 component directly -
    // so DarkTrader02's icon IS her actual in-game look (a hooded, masked
    // figure like Black Market, just an olive/yellow robe instead of dark -
    // visually confirmed against a real user screenshot of "Medal
    // Merchant"). This is why Dog Coin and Black Market read as similar at
    // a glance - they genuinely share a character model in the game's own
    // assets, not an artifact of our icon choice.
    if (uniqueName == "MedalTrader") { npcUsedIcons.Add("T_Male_DarkTrader02_icon_normal"); return "T_Male_DarkTrader02_icon_normal"; }
    if (uniqueName == "GrassBoss") { npcUsedIcons.Add("T_Human_GrassBoss_icon_normal"); return "T_Human_GrassBoss_icon_normal"; }
    if (npcPoliceNames.Contains(uniqueName)) { npcUsedIcons.Add("T_Police_icon_normal"); return "T_Police_icon_normal"; }
    if (uniqueName.StartsWith("U_", StringComparison.Ordinal))
    {
        var candidate = "T_" + uniqueName["U_".Length..] + "_icon_normal";
        if (normalIconLookup.TryGetValue(candidate, out var real))
        {
            npcUsedIcons.Add(real);
            return real;
        }
    }
    return "T_MobuCitizen_Male_icon_normal";
}

// See the NPCs section's own comment above for why these five (and only
// these five) get real shop data merged in here. DarkTrader/DarkTrader03
// (Black Market) sell Pals, same mechanism as Pal Dealer further below
// (BP_NPC_DarkTrader/_03's own palShopSimpleLotteryTableName - "Dark_01"/
// "Dark_03" - confirmed real DT_PalShopCreateData rows, not a guess; the
// unresolved individuals _02/_04/_BOSS aren't placed anywhere this
// project's spawner scan reaches, same "coverage is real but not 100%"
// caveat as the rest of this NPCs section - _BOSS is also a wholly
// different bounty-boss encounter already covered by bosses.py, not a
// missed shop variant).
(JArray? items, string? currency, string? currencyIcon, JObject? palPool) NpcShopInventory(string uniqueName)
{
    string? shopSettingKey = uniqueName switch
    {
        "MedalTrader" => "Medal_Shop_1",
        "BountyTrader" => "Bounty_Shop_1",
        "ArenaShop" => "Arena_Shop_1",
        _ => null,
    };
    string? itemLotteryKey = uniqueName switch
    {
        "MedalTrader" => "MedalShop1",
        "BountyTrader" => "BountyShop1",
        "ArenaShop" => "ArenaShop1",
        _ => null,
    };
    if (itemLotteryKey != null)
    {
        var (currency, currencyIcon) = TraderCurrencyInfo(shopSettingKey);
        return (ResolveItemShopProducts(itemLotteryKey), currency, currencyIcon, null);
    }
    string? palShopKey = uniqueName switch
    {
        "DarkTrader" => "Dark_01",
        "DarkTrader03" => "Dark_03",
        _ => null,
    };
    if (palShopKey != null) return (null, null, null, ResolvePalShopPool(palShopKey));
    return (null, null, null, null);
}

var npcResult = new JArray();
foreach (var (uniqueName, level, x, y, z) in npcRawPositions)
{
    if (npcExcludedPrefixes.Any(p => uniqueName.StartsWith(p, StringComparison.Ordinal))) continue;
    var npcRow = uniqueNpcRowsForNpcs[uniqueName];
    var nameTextId = (string?)npcRow?["NameTextID"];
    var realName = !string.IsNullOrEmpty(nameTextId) ? npcNameRows[nameTextId]?["TextData"]?["LocalizedString"]?.ToString() : null;
    var displayName = !string.IsNullOrEmpty(realName) ? realName : ReadableNpcName(uniqueName);
    var category = NpcCategory(uniqueName);
    var (items, currency, currencyIcon, palPool) = NpcShopInventory(uniqueName);

    npcResult.Add(new JObject
    {
        ["id"] = $"{uniqueName}:{x:F0}:{y:F0}",
        ["uniqueName"] = uniqueName,
        ["name"] = displayName,
        ["category"] = category,
        ["icon"] = NpcIcon(uniqueName, category) + ".png",
        ["level"] = level,
        ["items"] = items,
        ["currency"] = currency,
        ["currencyIcon"] = currencyIcon,
        ["palPool"] = palPool,
        ["x"] = x,
        ["y"] = y,
        ["z"] = z,
    });
}
// Not written yet - Wandering Merchant/Pal Dealer entries (external
// positions, see the comment block further below) still need to be merged
// into npcResult first. The actual file write + icon export happens once,
// after that merge, so both sources land in one npcs_static.json.
var npcIconOutDir = @"C:\Projects\PalworldMap\frontend\assets\npc_icons";
Directory.CreateDirectory(npcIconOutDir);

// ============ Guild Base icon ============
// Guild bases themselves aren't static game data (players build/dismantle
// them at runtime) - positions/ownership are read live from Level.sav each
// refresh cycle (backend/parse.py's load_guild_bases + backend/refresh.py),
// not extracted here. Only the icon is a static asset worth baking once.
// EPalLocationType::PointBaseCamp in DT_LocationUIData -> T_icon_compass_camp
// (diamond frame + house silhouette) - confirmed against a real in-game
// "Base" interaction-prompt screenshot before use, learning from the earlier
// Watchtower/Waypoint icon mixup (don't trust the DT_LocationUIData name
// alone without a visual check).
var baseIconOutDir = @"C:\Projects\PalworldMap\frontend\assets\base_icons";
Directory.CreateDirectory(baseIconOutDir);
{
    var exports = provider.LoadPackageObjects("Pal/Content/Pal/Texture/UI/InGame/T_icon_compass_camp").ToList();
    var tex = exports.OfType<UTexture2D>().First();
    var decoded = tex.Decode()!;
    var bytes = TextureEncoder.Encode(decoded, ETextureFormat.Png, false, out _);
    File.WriteAllBytes(Path.Combine(baseIconOutDir, "T_icon_compass_camp.png"), bytes);
}
Console.WriteLine("Wrote frontend/assets/base_icons/T_icon_compass_camp.png");

// ============ NPCs continued: Wandering Merchant / Pal Dealer ============
// Merged directly into npcResult/npcs_static.json (categories "Wandering"/
// "PalDealer"), not a separate section - originally shipped as a standalone
// "Traders" feature, folded back in once it became clear these two plus
// the NPCs section's own Medal/Bounty/Arena were really one merchant
// system split across two data sources for no good reason. There's no
// standalone Traders UI section, endpoint, or data file anymore.
//
// Position is a genuine dead end for static extraction (see NOTES.md's
// "Trader" investigation - exhaustively confirmed not in any DataTable/
// placed-actor/spawner; these wild-spawn once per server boot via
// procedural Blueprint logic invisible to this pipeline). Positions below
// are instead sourced from palpedia.ru's own live map data
// (https://palpedia.ru/api/merchants, a data-mined community site - same
// "external source, cross-checked" approach as the Towers section's HUD
// coordinates), 2026-07-20. Coordinate system confirmed to already match
// this project's raw save units: converted a known HUD reference point
// (Small Settlement's Pal Merchant) through the project's own map->save
// formula and landed within ~1500 units of palpedia's own "pal" entry
// there - same tolerance class as every other cross-check in this
// pipeline, not a coincidence.
//
// Only 2 of palpedia's 5 merchant categories end up here. Its full 22-entry
// export also included "medal"/"bounty"/"arena" (8 entries) - but those
// turned out to be EXACT coordinate duplicates (confirmed by direct
// comparison, not assumed) of MedalTrader/BountyTrader/ArenaShop, three
// individuals the NPCs section above already finds via its own spawner
// scan. Rather than show the same merchant twice, those three keep their
// one entry above (enriched with real shop data via NpcShopInventory) and
// are deliberately left OUT of merchantPositions below. Only "wandering"
// (Wandering Merchant) and "pal" (Pal Dealer) remain - the two categories
// this project's own extraction has never been able to place at all.
//
// Inventory, by contrast, IS real extractable game data once you have the
// right Blueprint. Each shop-NPC class under Character/NPC/Shop/BP_NPC_*
// has its own BP_PalShopVenderDataComponent with an
// itemShopSimpleLotteryTableName (-> the shared ResolveItemShopProducts
// helper above, defined before the NPCs section since both need it) for
// item sellers, and a separate palShopSimpleLotteryTableName (->
// DT_PalShopCreateData's CharacterIDArray, resolved via the same
// palNameRows/monsterRows the Bosses section above already loads) for the
// Pal Dealer.
//
// "wandering" maps unambiguously to one Blueprint variant
// (BP_NPC_SalesPerson_4, "WanderShopTable" - no other SalesPerson variant
// uses that table) - its "items" list is real and complete (confirmed
// single-entry, Weight-100 lottery, not a random draw - see
// ResolveItemShopProducts' own comment above). "pal" is NOT similarly
// unambiguous: PalDealer has 3 known variants (base/Desert/Volcano, tables
// "Test_00"/"Desert_00"/"Volcano_00") and palpedia's data doesn't
// distinguish which of its 6 "pal" locations uses which - defaulted to the
// base "Test_00" pool below, clearly labeled as a best-guess, not a
// confirmed per-location fact. Per the user's own caution: even for a
// correctly-identified table, this is a PAL POOL (DT_PalShopCreateData's
// own CharacterNum field shows only a random subset of the pool is
// actually offered at once, rotating on restock - genuinely different from
// the item shops' deterministic full list). Frontend must not present the
// Pal pool as "in stock now" - see frontend copy below.
var merchantPositions = new (double x, double y, string type, int? level)[]
{
    (-248788, 356707, "pal", 13),
    (-248285, 356691, "wandering", 15),
    (-467186, -60985, "wandering", 30),
    (-469164, -60831, "pal", 27),
    (-467854, -61656, "wandering", 28),
    (-115720, -24042, "wandering", 14),
    (-116506, -23892, "pal", 11),
    (42175, 315392, "wandering", 43),
    (40444, 314941, "pal", 43),
    (42236, 314931, "wandering", 45),
    (-344289, 193934, "pal", 12),
    (-341414, 192849, "wandering", 12),
    (-399694, 71535, "wandering", 22),
    (-408341, 75076, "pal", 14),
};

JObject? ResolvePalShopPool(string palShopTableKey)
{
    var row = palShopCreateRows[palShopTableKey] as JObject;
    var charArray = row?["CharacterIDArray"] as JArray;
    if (charArray == null) return null;
    var species = new JArray();
    foreach (var c in charArray)
    {
        var characterId = c["Key"]?.ToString();
        if (string.IsNullOrEmpty(characterId)) continue;
        // Regular wild-Pal species (unlike bosses) don't carry their own
        // OverrideNameTextID on DT_PalMonsterParameter - it's "None" for
        // these - so the real lookup key is "PAL_NAME_<CharacterID>"
        // directly (confirmed: PAL_NAME_ChickenPal -> "Chikipi"), the same
        // convention NOTES.md documents for Effigies. Only fall back to the
        // boss-style OverrideNameTextID indirection if that direct key
        // comes up empty, in case a boss-tier species ever ends up in a
        // Pal Dealer's pool.
        //
        // Sanity check if this ever looks "wrong" again (spent real time
        // chasing this as a suspected bug 2026-07-20, it wasn't one):
        // DarkTrader's Dark_01/Dark_03 CharacterIDArray uses old internal
        // dev codenames (e.g. "GhostBlackCat", "RobinHood", "HerculesBeetle")
        // that read as completely unrelated to their real released names
        // ("Wispaw", "Robinquill", "Warsect") - PAL_NAME_ resolves them
        // correctly (confirmed thematically thanks to PalPortraitIcon's own
        // Tribe-keyed icon staying correctly paired regardless - Robinquill
        // gets the bird-archer "RobinHood" portrait, Warsect gets the
        // "HerculesBeetle" one, etc.) - a real codename/release-name split
        // baked into the game's own data, not a lookup bug here.
        var name = (palNameRows["PAL_NAME_" + characterId] as JObject)?["TextData"]?["LocalizedString"]?.ToString();
        if (string.IsNullOrEmpty(name))
        {
            var monster = monsterRows[characterId] as JObject;
            var nameTextId = monster?["OverrideNameTextID"]?.ToString();
            name = (!string.IsNullOrEmpty(nameTextId) && nameTextId != "None")
                ? (palNameRows[nameTextId] as JObject)?["TextData"]?["LocalizedString"]?.ToString() ?? characterId
                : characterId;
        }
        // No price here (unlike item shops) - per the user's own call, a
        // Pal's real in-game price depends on its randomly-rolled level and
        // stats, which this static extraction has no way to know. Name +
        // real portrait only.
        species.Add(new JObject { ["name"] = name, ["icon"] = PalPortraitIcon(characterId) });
    }
    return new JObject
    {
        ["poolSpecies"] = species,
        ["offeredCount"] = row?["CharacterNum"],
        ["levelMin"] = row?["MinCharacterLevel"],
        ["levelMax"] = row?["MaxCharacterLevel"],
    };
}

// Real portraits confirmed by the user in-game against these exact coat
// colors (see NOTES.md's Trader investigation) - not a guess. Added to the
// shared npcUsedIcons set (declared in the NPCs section above) so the
// NPCs section's own icon-export loop picks these up too - no separate
// export path needed.
string MerchantIconVariant(string type)
{
    var variant = type == "wandering" ? "T_SalesPerson_icon_normal" : "T_PalDealer_icon_normal";
    npcUsedIcons.Add(variant);
    return variant;
}

string MerchantCategory(string type) => type == "wandering" ? "Wandering" : "PalDealer";
string MerchantDisplayName(string type) => type == "wandering" ? "Wandering Merchant" : "Pal Dealer";
// Nominal identity, not a real DT_UniqueNPC row (these aren't spawner-
// sourced) - matches this project's own internal naming for these
// Blueprints (SalesPerson_Wander/PalDealer, see NOTES.md) rather than
// leaving uniqueName blank.
string MerchantUniqueName(string type) => type == "wandering" ? "SalesPerson_Wander" : "PalDealer";

foreach (var (x, y, type, level) in merchantPositions)
{
    var uniqueName = MerchantUniqueName(type);
    // wandering sells items for plain Gold - no DT_ItemShopSettingData
    // override row exists for it, unlike Medal/Bounty/Arena (see the NPCs
    // section's NpcShopInventory), so TraderCurrencyInfo(null) resolves to
    // the Gold/"Money" default. pal has no currency shown at all - see
    // ResolvePalShopPool's own comment on why no price is computed.
    JArray? items = null;
    string? currency = null, currencyIcon = null;
    JObject? palPool = null;
    if (type == "wandering")
    {
        items = ResolveItemShopProducts("WanderShopTable");
        (currency, currencyIcon) = TraderCurrencyInfo(null);
    }
    else
    {
        palPool = ResolvePalShopPool("Test_00");
    }

    npcResult.Add(new JObject
    {
        ["id"] = $"{uniqueName}:{x:F0}:{y:F0}",
        ["uniqueName"] = uniqueName,
        ["name"] = MerchantDisplayName(type),
        ["category"] = MerchantCategory(type),
        ["icon"] = MerchantIconVariant(type) + ".png",
        ["level"] = level,
        ["items"] = items,
        ["currency"] = currency,
        ["currencyIcon"] = currencyIcon,
        ["palPool"] = palPool,
        ["x"] = x,
        ["y"] = y,
        ["z"] = null,
    });
}

Console.WriteLine($"Total NPCs with a map marker: {npcResult.Count}, distinct icons used: {npcUsedIcons.Count}");
File.WriteAllText(@"C:\Projects\PalworldMap\data\npcs_static.json", npcResult.ToString(Formatting.Indented));
Console.WriteLine("Wrote data/npcs_static.json");
foreach (var variant in npcUsedIcons)
{
    var exports = provider.LoadPackageObjects($"Pal/Content/Pal/Texture/PalIcon/Normal/{variant}").ToList();
    var tex = exports.OfType<UTexture2D>().First();
    var decoded = tex.Decode()!;
    var bytes = DownscalePng(TextureEncoder.Encode(decoded, ETextureFormat.Png, false, out _));
    File.WriteAllBytes(Path.Combine(npcIconOutDir, $"{variant}.png"), bytes);
}
Console.WriteLine($"Wrote {npcUsedIcons.Count} frontend/assets/npc_icons/*.png");

// ============ Quests (Main + Sub, map-marker-bearing only) ============
// Every quest (DT_PalQuestData, 120 rows: 58 Main / 59 Sub / 3 Hidden) is an
// ordered sequence of "blocks" (its own Blueprint's QuestBlockGroupList ->
// each group's BlockList, one or more blocks running in parallel per step).
// Each block *optionally* carries LocationSettingData.FixedLocationPointArray
// - a real, explicit foreign key ({DataTable: DT_PalQuestLocationData,
// RowName}), not a naming-convention guess - confirmed directly against
// Zoe's first block (BP_SubQuestBlock_Zoe01_DisplayElecpanda), which points
// at row "Sub_Zoe" (plus "Main_CaptureDeerGround").
//
// Per user's own scoping call: only keep quests where at least one step
// resolves to a real location - "if there's no map marker, we don't care
// about it." Hidden-type quests (3 rows, background triggers like
// "change your weapon's ammo type") are dropped entirely, not evaluated.
//
// This directly resolves an earlier open question about whether quests map
// 1:1 to NPCs: they don't, in two different ways. Zoe alone is 5 separate
// quest rows (Sub_Zoe01..04 + Sub_Zoe_Halloween) at one spot - a real
// multi-quest chain. Sub_Farmer01..04 *look* like the same shape (numbered
// suffix) but resolve to 4 physically distinct locations - four different
// farmers, one quest each, not a chain. There's no explicit chain-grouping
// field in the data for either case - the per-block RowName join above
// sidesteps needing one: each quest's own steps carry their own locations
// regardless of how many other quests happen to share an NPC or a spot.
//
// Live per-player join (not done here - see backend/parse.py +
// backend/quests.py): SaveData.OrderedQuestArray_FullRelease gives
// {QuestName, BlockIndex} for every currently in-progress quest - BlockIndex
// indexes this array's own "steps" directly (confirmed: a real player's
// Main_RayneSyndicate was at BlockIndex 1, its own 2nd QuestBlockGroupList
// entry, the "DefeatBoss" step, matching where a Rayne-Syndicate-progress
// player would actually be). SaveData.CompletedQuestArray_FullRelease is a
// flat list of finished quest IDs. Not-started = known quest ID in neither
// array - its target is steps[0] (where you'd go to begin it).
//
// Quest title text is a genuine dead end for static extraction, confirmed
// exhaustively 2026-07-20 (an earlier pass just had a bug - see below - that
// made it look unexplored). QuestTitleMsgId (e.g. "Quest_Main_Title_
// DefeatKingWhale", "QUEST_SUB_QUESTNAME_BREEDER1") lives on the quest
// Blueprint's own CDO (questCdo["Properties"]["QuestTitleMsgId"]), NOT on
// the DT_PalQuestData row (qp.Value) - the original code read it from the
// row, where the field doesn't exist at all, so titleMsgId was silently
// always null. Fixed to read from questCdo. But the MsgId->real-text
// resolution itself has no static answer anywhere in the shipped game
// files: no DataTable row (any casing/substring of "QUEST") matches these
// keys; the only 7 ST_*/StringTable uassets in the whole game are unrelated
// (rope-physics/animation tables + one PSN EULA table); and
// Pal/Content/Localization/Game/<culture>/Game.locres is a byte-identical
// empty 37-byte stub for EVERY shipped culture (en/ja/ko/zh-Hans checked),
// i.e. the compiled UE StringTable/locres localization pipeline is entirely
// unused by this game - real text must be resolved by native code against
// something not present in Pal/Content at all.
//
// Real display names below (questRealTitleById) instead come from
// palpedia.ru (a data-mined Palworld quest database whose URLs are keyed
// directly by this exact internal quest ID, e.g.
// https://palpedia.ru/en/missions/quest:Main_DefeatKingWhale) - same
// external-source approach as the Towers section's boss names. Two entries
// were user-confirmed in-game before any lookup (Main_DefeatKingWhale =
// "Panthalus", Sub_Breeder01 = "Breeding Basics") and both matched
// palpedia.ru exactly, validating the source for the rest. 77 of 87 quests
// resolved this way 2026-07-20; the 9 unresolved (Sub_PalDisplay_A_01
// .. I_01, all 404 on palpedia.ru) and Test_UnlockAreaBarriers (a leftover
// dev/test quest, expected 404) fall back to ReadableQuestTitle's
// row-name-derived label below, same as before this pass.
string QuestPackagePath(string assetPathName)
{
    var withoutObjectName = assetPathName[..assetPathName.LastIndexOf('.')];
    return withoutObjectName.Replace("/Game/", "Pal/Content/");
}

JObject? LoadCdo(string packagePath)
{
    try
    {
        var exports = provider.LoadPackageObjects(packagePath).ToList();
        var cdo = exports.FirstOrDefault(e => e.Name.ToString().StartsWith("Default__", StringComparison.Ordinal));
        if (cdo == null) return null;
        return JObject.Parse(JsonConvert.SerializeObject(cdo));
    }
    catch
    {
        return null;
    }
}

string ReadableQuestTitle(string questId)
{
    var stripped = questId.StartsWith("Main_") ? questId["Main_".Length..]
        : questId.StartsWith("Sub_") ? questId["Sub_".Length..]
        : questId;
    var withSpaces = System.Text.RegularExpressions.Regex.Replace(stripped, "(?<!^)([A-Z])", " $1");
    var collapsed = System.Text.RegularExpressions.Regex.Replace(withSpaces.Replace("_", " "), @"\s+", " ");
    return collapsed.Trim();
}

var questRealTitleById = new Dictionary<string, string>
{
    ["Main_UnlockFastTravel"] = "Activate Great Eagle Statue",
    ["Main_DefeatWildBoss"] = "First Boss Battle",
    ["Main_DefeatDungeonBoss"] = "Sealed Pals",
    ["Main_DefeatGrassBoss"] = "The Girl and the Tower",
    ["Main_CaptureDeerGround"] = "Wildlife Sanctuaries",
    ["Main_DefeatForestBoss"] = "Thou Shalt Not Harm Pals",
    ["Main_DefeatVolcanoBoss"] = "Flawless Victory Fixation",
    ["Main_DefeatDesertBoss"] = "Ruler of the Sands",
    ["Main_DefeatSnowyMountainBoss"] = "Interspecies Experiments",
    ["Main_DefeatSakurajimaBoss"] = "A Fine Night for a Drink",
    ["Main_DefeatVikingBoss"] = "Bearer of All Burdens",
    ["Sub_Farmer01"] = "Pal's Blessing",
    ["Sub_Farmer02"] = "Hungry for Recruits",
    ["Sub_Farmer03"] = "A Special Dish",
    ["Sub_Farmer04"] = "Medical Bills",
    ["Sub_Scholar01"] = "Prove the Legend!",
    ["Sub_Scholar02"] = "Lock In",
    ["Sub_Scholar03"] = "Give a Fuack",
    ["Sub_Scholar04"] = "Metal on Your Mind",
    ["Sub_Breeder01"] = "Breeding Basics", // user-confirmed in-game, matches palpedia.ru exactly
    ["Sub_Breeder02"] = "Farewell Under the Sakura Tree",
    ["Sub_Breeder03"] = "No Mercy for Pal Thieves",
    ["Sub_Breeder04"] = "Operation: Splatterina Spree",
    ["Sub_Ranger01"] = "Hunting Essentials",
    ["Sub_Ranger02"] = "The Fugitive",
    ["Sub_Ranger03"] = "Formidable Faleris Aqua",
    ["Sub_Ranger04"] = "A Sweet Deal",
    ["Sub_Nomad01"] = "A True Ascent Ends in Descent",
    ["Sub_Nomad02"] = "Sparkly and Smooth",
    ["Sub_Nomad03"] = "Dungeon Hunt",
    ["Sub_Nomad04"] = "Easygoing Adventures",
    ["Sub_Zoe01"] = "Don't Get Cocky",
    ["Sub_Zoe02"] = "I Still Don't Trust You",
    ["Sub_Zoe03"] = "A Warm Memory",
    ["Sub_Angler01"] = "A Fine Day for Fishing",
    ["Sub_PalCaptureCountReward"] = "Request from a Wise Hunter",
    ["Sub_BossDefeatReward"] = "Request from a Veteran Hunter",
    ["Sub_PaldexReward"] = "Request from a Pal Ecological Researcher",
    ["Sub_FoodReward"] = "Request from an Arrogant Foodie",
    ["Sub_Kigurumi01"] = "That's That Me, Depresso!",
    ["Sub_Zoe04"] = "Zoe's Sphere",
    ["Sub_Zoe_Halloween"] = "Happy Halloween!",
    ["Sub_Kigurumi01_Replay"] = "That's That Me, Depresso! (Repeat)",
    ["Sub_StrongOldMan01"] = "Captured Adventurer",
    ["Sub_StrongOldMan02"] = "Taking Down a Bounty",
    ["Sub_StrongOldMan03"] = "The Proxy",
    ["Sub_StrongOldMan04"] = "The Past",
    ["Sub_StrongOldMan05"] = "The Knight's Last Stand",
    ["Main_TutorialStart"] = "The First Islander",
    ["Main_BeginAdventure"] = "The Adventure Begins",
    ["Main_SmallVillage"] = "Small Settlement",
    ["Main_CraftPalGear"] = "Crafting Pal Gear",
    ["Main_RayneSyndicate"] = "The Rayne Syndicate",
    ["Main_ReturnSmallVillage"] = "The Key Sphere",
    ["Main_TalkGrassBoss"] = "Zoe Rayne",
    ["Main_TalkSkyBoss"] = "The Sunreacher",
    ["Main_TalkSkyBossAgain"] = "The Calamity",
    ["Main_DefeatKingWhale"] = "Panthalus", // user-confirmed in-game, matches palpedia.ru exactly
    ["Main_ReachWorldTree"] = "To the World Tree",
    ["Main_TalkWorldTreeNPC"] = "Sin",
    ["Main_WorldTreeAbyss"] = "The Sealed Calamity",
    ["Main_DefeatWorldTreeDragon"] = "Awakening",
    ["Main_DefeatSkyIslandBoss"] = "Hope Springs Eternal",
    ["Main_DefeatWorldTreeMiddleBoss"] = "Path to the Abyss",
    ["Sub_RookieExpeditionTeam01"] = "The Rookie Expedition Team",
    ["Sub_RookieExpeditionTeam02"] = "Budding",
    ["Sub_RookieExpeditionTeam03"] = "Growth",
    ["Sub_RookieExpeditionTeam04"] = "A Sterile Flower",
    ["Sub_LoneWolf01"] = "The Sable Loner",
    ["Sub_LoneWolf02"] = "Reminiscent Wandering",
    ["Sub_LoneWolf03"] = "The Unsheathed Katana",
    ["Sub_DeliverySulfur"] = "Stockpiling Sulfur",
    ["Sub_DeliveryWood_Fine"] = "Stockpiling Hardwood",
    ["Sub_DeliveryQuartz"] = "Stockpiling Pure Quartz",
    ["Sub_DeliveryRainbowCrystal"] = "Stockpiling Hexolite Quartz",
    ["Sub_DeliverySkyIslandOre"] = "Stockpiling Soralite",
    ["Sub_HowSurviveWorldTree"] = "Surviving the World Tree",
    // Unresolved (404 on palpedia.ru, no confirmed real name yet):
    // Sub_PalDisplay_A_01 .. I_01 (9 quests), Test_UnlockAreaBarriers (dev/test
    // quest, expected). These fall through to ReadableQuestTitle below.
};

var questRows = LoadRows("Pal/Content/Pal/DataTable/Quest/DT_PalQuestData");
var questLocRows = LoadRows("Pal/Content/Pal/DataTable/Quest/DT_PalQuestLocationData");
var questResult = new JArray();
int questBlocksLoaded = 0, questBlocksMissing = 0;

foreach (var qp in questRows.Properties())
{
    var questId = qp.Name;
    var questType = ElementString(qp.Value["QuestType"]); // Main / Sub / Hidden
    if (questType == "Hidden") continue;

    var questAssetPath = (string?)qp.Value["QuestData"]?["AssetPathName"];
    if (string.IsNullOrEmpty(questAssetPath)) continue;
    var questCdo = LoadCdo(QuestPackagePath(questAssetPath));
    var groupList = questCdo?["Properties"]?["QuestBlockGroupList"] as JArray;
    if (groupList == null) continue;

    var steps = new JArray();
    bool anyLocation = false;

    foreach (var group in groupList)
    {
        var stepLocations = new JArray();
        var blockList = group["BlockList"] as JArray;
        if (blockList != null)
        {
            foreach (var block in blockList)
            {
                var blockAssetPath = (string?)block["AssetPathName"];
                if (string.IsNullOrEmpty(blockAssetPath)) continue;
                var blockCdo = LoadCdo(QuestPackagePath(blockAssetPath));
                if (blockCdo == null) { questBlocksMissing++; continue; }
                questBlocksLoaded++;

                var fixedPoints = blockCdo["Properties"]?["LocationSettingData"]?["FixedLocationPointArray"] as JArray;
                if (fixedPoints == null) continue;
                foreach (var fp in fixedPoints)
                {
                    var rowName = (string?)fp["RowName"];
                    if (string.IsNullOrEmpty(rowName) || rowName == "None") continue;
                    var locRow = questLocRows[rowName];
                    var pos = locRow?["Position"];
                    if (pos == null) continue;
                    stepLocations.Add(new JObject
                    {
                        ["rowName"] = rowName,
                        ["x"] = pos["X"],
                        ["y"] = pos["Y"],
                        ["z"] = pos["Z"],
                    });
                }
            }
        }
        if (stepLocations.Count > 0) anyLocation = true;
        steps.Add(new JObject { ["locations"] = stepLocations });
    }

    if (!anyLocation) continue; // no map marker anywhere in this quest - skip per scoping call

    questResult.Add(new JObject
    {
        ["id"] = questId,
        ["type"] = questType,
        ["title"] = questRealTitleById.TryGetValue(questId, out var realTitle) ? realTitle : ReadableQuestTitle(questId),
        ["titleMsgId"] = (string?)questCdo?["Properties"]?["QuestTitleMsgId"],
        ["steps"] = steps,
    });
}
Console.WriteLine($"Total quests with a map marker: {questResult.Count} (blocks loaded: {questBlocksLoaded}, missing: {questBlocksMissing})");
File.WriteAllText(@"C:\Projects\PalworldMap\data\quests_static.json", questResult.ToString(Formatting.Indented));
Console.WriteLine("Wrote data/quests_static.json");

// The game's own in-game compass quest marker (T_icon_Compass_Quest_0/_1) -
// two variants, found by an asset-path keyword sweep. Visually checked
// before wiring in (per this project's own rule - don't trust an asset name
// alone, see the Watchtower/Waypoint icon mixup elsewhere in NOTES.md):
// _0 is a gold diamond with "!" (standard "NPC has a quest for you" prompt
// icon - used here for Not Started), _1 is a plain blue diamond with no
// symbol (standard "current objective" tracking marker - used for Active).
var questIconOutDir = @"C:\Projects\PalworldMap\frontend\assets\quest_icons";
Directory.CreateDirectory(questIconOutDir);
foreach (var variant in new[] { "T_icon_Compass_Quest_0", "T_icon_Compass_Quest_1" })
{
    var exports = provider.LoadPackageObjects($"Pal/Content/Pal/Texture/UI/InGame/{variant}").ToList();
    var tex = exports.OfType<UTexture2D>().First();
    var decoded = tex.Decode()!;
    var bytes = TextureEncoder.Encode(decoded, ETextureFormat.Png, false, out _);
    File.WriteAllBytes(Path.Combine(questIconOutDir, $"{variant}.png"), bytes);
    Console.WriteLine($"Wrote frontend/assets/quest_icons/{variant}.png");
}
