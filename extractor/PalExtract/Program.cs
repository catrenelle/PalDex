using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Versions;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse_Conversion.Textures;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Processing;

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
    var bytes = TextureEncoder.Encode(decoded, ETextureFormat.Png, false, out _);
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
        var human = humanRows[spawnerId] as JObject;
        if (human != null)
        {
            var nameTextId = human["OverrideNameTextID"]?.ToString();
            if (!string.IsNullOrEmpty(nameTextId) && nameTextId != "None")
            {
                var nameRow = humanNameRows[nameTextId] as JObject;
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

Console.WriteLine($"Total bosses: {result.Count}, icons exported: {exportedIcons.Count}");
File.WriteAllText(@"C:\Projects\PalworldMap\data\bosses_static.json", result.ToString(Formatting.Indented));
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
    // path - nothing). Downscale to a small thumbnail here instead, so 64 of
    // these don't cost 500+MB on disk and a multi-second page load for detail
    // nobody sees at 30px.
    using var image = SixLabors.ImageSharp.Image.Load(bytes);
    image.Mutate(x => x.Resize(new ResizeOptions
    {
        Mode = ResizeMode.Max,
        Size = new SixLabors.ImageSharp.Size(128, 128),
    }));
    using var ms = new MemoryStream();
    image.Save(ms, new PngEncoder());
    File.WriteAllBytes(Path.Combine(noteIconOutDir, outFileName), ms.ToArray());
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

Console.WriteLine("Scanning World Partition cells for the remaining Journal notes + Schematics...");
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
    var bytes = TextureEncoder.Encode(decoded, ETextureFormat.Png, false, out _);
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
