using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.GameData.Locations;
using StardewValley.GameData.Objects;
using StardewValley.ItemTypeDefinitions;
using StardewValley.Locations;
using StardewValley.Quests;
using StardewValley.SpecialOrders;
using StardewValley.SpecialOrders.Objectives;
using StardewValley.TokenizableStrings;

namespace LiveStateExporter;

internal sealed record FishSpawnSpot(
    string LocationKey,
    string LocationDisplayName,
    string? AreaDisplayName,
    Rectangle? AreaBounds,
    List<string> EffectiveSeasons,
    string EffectiveWeather,
    string TimeDisplay,
    int MinFishingLevel,
    Rectangle? PlayerPosition,
    Rectangle? BobberPosition,
    int MinDistanceFromShore,
    int MaxDistanceFromShore,
    bool RequireMagicBait,
    bool IgnoreFishDataRequirements,
    float Chance,
    int CatchLimit,
    bool IsBossFish,
    string? ExtraCondition,
    bool SeasonMatchesNow,
    bool WeatherMatchesToday,
    bool TimeMatchesNow,
    bool LevelMet,
    bool ConditionPassedNow,
    string LiveStatus);

internal sealed record FishGuideEntry(
    string QualifiedItemId,
    string DisplayName,
    string InternalName,
    bool IsTrapFish,
    bool ExcludeFromCollection,
    bool IsCaught,
    int Difficulty,
    string Behavior,
    string TimeDisplay,
    string DataSeasons,
    string DataWeather,
    int DataMinLevel,
    List<string> Reasons,
    bool InStorageOrInventory,
    List<FishSpawnSpot> Spots);

internal sealed class FishGuideSnapshot
{
    public int FishingSkillEffective { get; init; }
    public int FishingSkillBase { get; init; }
    public string ActiveRod { get; init; } = "Yok";
    public List<FishGuideEntry> PriorityFish { get; init; } = new();
    public List<FishGuideEntry> MissingSeasonalCollectionFish { get; init; } = new();
    public List<FishGuideEntry> MissingOtherSeasonCollectionFish { get; init; } = new();
    public List<FishGuideEntry> ExcludedUncaughtFish { get; init; } = new();
    public List<string> DuplicateNameWarnings { get; init; } = new();
}

internal static class FishGuideBuilder
{
    private static readonly string[] AllSeasonKeys = { "spring", "summer", "fall", "winter" };

    private sealed record CachedFishBuild(
        string Key,
        FishGuideSnapshot Guide,
        int MissingSeasonalTotal,
        List<string> MissingSeasonalSample,
        int CaughtCount,
        int CollectionTotalCount);

    private static CachedFishBuild? _buildCache;

    /// <summary>
    /// Builds a complete runtime fish & spawn guide from live patched Data/Locations, Data/Fish,
    /// active Quests/Special Orders, and incomplete Community Center bundles.
    /// </summary>
    public static (
        FishGuideSnapshot Guide,
        int MissingSeasonalTotal,
        List<string> MissingSeasonalSample,
        int CaughtCount,
        int CollectionTotalCount)
        Build(
            Farmer player,
            List<BundleEntry> incompleteBundles,
            IEnumerable<GameLocation> storageLocations,
            string? bestRodSummary,
            bool forceRebuild = false)
    {
        int bundleSig = incompleteBundles.Count * 397;
        foreach (BundleEntry b in incompleteBundles)
            bundleSig = (bundleSig * 31) ^ b.FilledCount ^ (b.MissingItemIds.Count * 17);

        int questSig = player.questLog.Count * 101;
        foreach (Quest? q in player.questLog)
        {
            if (q is not null)
                questSig ^= q.completed.Value ? 1 : (q.daysLeft.Value * 7);
        }

        int orderSig = player.team.specialOrders.Count * 131;
        foreach (SpecialOrder? o in player.team.specialOrders)
        {
            if (o is null)
                continue;
            foreach (var obj in o.objectives)
            {
                if (obj is not null)
                    orderSig ^= obj.GetCount();
            }
        }

        bool isRaining = Game1.getFarm()?.IsRainingHere() ?? Game1.isRaining;
        string cacheKey = $"{StardewModdingAPI.Constants.SaveFolderName}|{Game1.Date.TotalDays}|{Game1.timeOfDay / 100}|{isRaining}|{player.FishingLevel}|{player.fishCaught.Count()}|{bundleSig}|{questSig}|{orderSig}|{bestRodSummary}";
        if (!forceRebuild && _buildCache is not null && _buildCache.Key == cacheKey)
        {
            return (
                _buildCache.Guide,
                _buildCache.MissingSeasonalTotal,
                _buildCache.MissingSeasonalSample,
                _buildCache.CaughtCount,
                _buildCache.CollectionTotalCount);
        }

        var fishData = DataLoader.Fish(Game1.content);
        var locationsData = DataLoader.Locations(Game1.content);
        var objectsData = DataLoader.Objects(Game1.content);

        var neededReasons = CollectNeededReasons(player, incompleteBundles, fishData);
        var ownedFishIds = CollectOwnedFishIds(player, storageLocations);
        var spotsByFish = IndexAllRuntimeSpawns(player, locationsData, fishData);

        var priority = new List<FishGuideEntry>();
        var missingSeasonal = new List<FishGuideEntry>();
        var missingOtherSeason = new List<FishGuideEntry>();
        var excludedUncaught = new List<FishGuideEntry>();

        int caughtCount = 0;
        int collectionTotal = 0;
        var allEntriesForNameCheck = new List<FishGuideEntry>();

        foreach (var kv in fishData)
        {
            string qid = NormalizeQid(kv.Key);
            ParsedItemData? itemData = ItemRegistry.GetData(qid);
            if (itemData is not null && itemData.Category != StardewValley.Object.FishCategory && !neededReasons.ContainsKey(qid))
                continue;

            bool excluded = IsExcludedFromFishingCollection(kv.Key, qid, itemData, objectsData);
            if (!excluded)
                collectionTotal++;

            bool isCaught = player.fishCaught.ContainsKey(qid) || player.fishCaught.ContainsKey(kv.Key);
            if (isCaught && !excluded)
                caughtCount++;

            bool isNeeded = neededReasons.TryGetValue(qid, out var reasons) && reasons.Count > 0;
            if (isCaught && !isNeeded)
                continue;

            FishMetadata meta = ParseFishMetadata(kv.Key, kv.Value, itemData);
            List<FishSpawnSpot> spots = spotsByFish.GetValueOrDefault(qid) ?? new List<FishSpawnSpot>();

            var entry = new FishGuideEntry(
                QualifiedItemId: qid,
                DisplayName: meta.DisplayName,
                InternalName: meta.InternalName,
                IsTrapFish: meta.IsTrapFish,
                ExcludeFromCollection: excluded,
                IsCaught: isCaught,
                Difficulty: meta.Difficulty,
                Behavior: meta.Behavior,
                TimeDisplay: meta.TimeDisplay,
                DataSeasons: meta.SeasonsDisplay,
                DataWeather: meta.Weather,
                DataMinLevel: meta.MinLevel,
                Reasons: reasons ?? new List<string>(),
                InStorageOrInventory: ownedFishIds.Contains(qid),
                Spots: OrderSpotsForPlayer(spots));

            allEntriesForNameCheck.Add(entry);

            if (isNeeded)
                priority.Add(entry);

            if (!isCaught)
            {
                if (excluded)
                {
                    excludedUncaught.Add(entry);
                }
                else if (IsSeasonalAnywhereNow(entry, spots))
                {
                    missingSeasonal.Add(entry);
                }
                else
                {
                    missingOtherSeason.Add(entry);
                }
            }
        }

        // Also handle any quest/CC needed fish not present in Data/Fish (edge case for custom fish objects)
        foreach (var kv in neededReasons)
        {
            if (priority.Any(p => p.QualifiedItemId.Equals(kv.Key, StringComparison.OrdinalIgnoreCase)))
                continue;

            ParsedItemData? itemData = ItemRegistry.GetData(kv.Key);
            List<FishSpawnSpot> spots = spotsByFish.GetValueOrDefault(kv.Key) ?? new List<FishSpawnSpot>();
            if (spots.Count == 0 && (itemData is null || itemData.Category != StardewValley.Object.FishCategory))
                continue;

            bool isCaught = player.fishCaught.ContainsKey(kv.Key);
            var extra = new FishGuideEntry(
                QualifiedItemId: kv.Key,
                DisplayName: itemData?.DisplayName ?? kv.Key,
                InternalName: itemData?.InternalName ?? kv.Key,
                IsTrapFish: false,
                ExcludeFromCollection: IsExcludedFromFishingCollection(kv.Key, kv.Key, itemData, objectsData),
                IsCaught: isCaught,
                Difficulty: 0,
                Behavior: "-",
                TimeDisplay: "06:00-02:00",
                DataSeasons: "Tum mevsimler",
                DataWeather: "both",
                DataMinLevel: 0,
                Reasons: kv.Value,
                InStorageOrInventory: ownedFishIds.Contains(kv.Key),
                Spots: OrderSpotsForPlayer(spots));

            priority.Add(extra);
            allEntriesForNameCheck.Add(extra);
        }

        priority = priority
            .OrderByDescending(e => e.InStorageOrInventory)
            .ThenByDescending(e => BestSpotScore(e.Spots))
            .ThenBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        missingSeasonal = missingSeasonal
            .OrderByDescending(e => BestSpotScore(e.Spots))
            .ThenBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        missingOtherSeason = missingOtherSeason
            .OrderBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        excludedUncaught = excludedUncaught
            .OrderByDescending(e => BestSpotScore(e.Spots))
            .ThenBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var summarySamples = BuildSummarySampleList(missingSeasonal);
        var duplicateWarnings = DetectSimilarNames(allEntriesForNameCheck, fishData);

        var guide = new FishGuideSnapshot
        {
            FishingSkillEffective = player.FishingLevel,
            FishingSkillBase = player.fishingLevel.Value,
            ActiveRod = string.IsNullOrWhiteSpace(bestRodSummary) ? "Yok" : bestRodSummary,
            PriorityFish = priority,
            MissingSeasonalCollectionFish = missingSeasonal,
            MissingOtherSeasonCollectionFish = missingOtherSeason,
            ExcludedUncaughtFish = excludedUncaught,
            DuplicateNameWarnings = duplicateWarnings
        };

        _buildCache = new CachedFishBuild(cacheKey, guide, missingSeasonal.Count, summarySamples, caughtCount, collectionTotal);
        return (guide, missingSeasonal.Count, summarySamples, caughtCount, collectionTotal);
    }

    private static Dictionary<string, List<string>> CollectNeededReasons(
        Farmer player,
        List<BundleEntry> incompleteBundles,
        Dictionary<string, string> fishData)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        void AddReason(string? rawId, string reason)
        {
            if (string.IsNullOrWhiteSpace(rawId))
                return;
            string qid = NormalizeQid(rawId);
            if (!IsFishObjectOrKnown(qid, fishData))
                return;

            if (!map.TryGetValue(qid, out var list))
            {
                list = new List<string>();
                map[qid] = list;
            }
            if (!list.Contains(reason, StringComparer.OrdinalIgnoreCase))
                list.Add(reason);
        }

        foreach (BundleEntry b in incompleteBundles)
        {
            foreach (string id in b.MissingItemIds)
                AddReason(id, $"Halkevi: {b.Area} / {b.BundleName}");
        }

        foreach (Quest? q in player.questLog)
        {
            if (q is null || q.completed.Value)
                continue;

            string title = ModEntry.ResolveLocalizedText(q.questTitle);
            if (q is ItemDeliveryQuest idq)
                AddReason(idq.ItemId.Value, $"Gorev: {title} ({idq.target.Value})");
            else if (q is FishingQuest fq)
                AddReason(fq.ItemId.Value, $"Gorev: {title} ({fq.target.Value} {fq.numberFished.Value}/{fq.numberToFish.Value})");
            else if (q is ResourceCollectionQuest rcq)
                AddReason(rcq.ItemId.Value, $"Gorev: {title} ({rcq.target.Value} {rcq.numberCollected.Value}/{rcq.number.Value})");
        }

        foreach (SpecialOrder? o in player.team.specialOrders)
        {
            if (o is null)
                continue;

            string name = ModEntry.ResolveLocalizedText(o.GetName());
            foreach (var obj in o.objectives)
            {
                if (obj is null)
                    continue;

                IEnumerable<string>? tagSets = obj switch
                {
                    FishObjective fo => fo.acceptableContextTagSets,
                    CollectObjective co => co.acceptableContextTagSets,
                    DonateObjective doObj => doObj.acceptableContextTagSets,
                    DeliverObjective del => del.acceptableContextTagSets,
                    ShipObjective so => so.acceptableContextTagSets,
                    _ => null
                };
                if (tagSets is null)
                    continue;

                foreach (string tag in tagSets)
                {
                    if (string.IsNullOrWhiteSpace(tag))
                        continue;

                    foreach (string sub in tag.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (!sub.StartsWith("fish_", StringComparison.OrdinalIgnoreCase)
                            && !sub.StartsWith("item_", StringComparison.OrdinalIgnoreCase))
                            continue;

                        foreach (string fk in fishData.Keys)
                        {
                            string qid = NormalizeQid(fk);
                            if (ItemRegistry.Create(qid, allowNull: true)?.HasContextTag(sub) == true)
                                AddReason(qid, $"Ozel Siparis: {name}");
                        }
                    }
                }
            }
        }

        return map;
    }

    private static HashSet<string> CollectOwnedFishIds(Farmer player, IEnumerable<GameLocation> storageLocations)
    {
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void CheckItems(IEnumerable<Item?>? items)
        {
            if (items is null)
                return;
            foreach (Item? item in items)
            {
                if (item is not null && item.Category == StardewValley.Object.FishCategory)
                    owned.Add(NormalizeQid(item.QualifiedItemId));
            }
        }

        CheckItems(player.Items);
        foreach (GameLocation loc in storageLocations)
        {
            foreach (var pair in loc.Objects.Pairs)
            {
                if (pair.Value is StardewValley.Objects.Chest chest)
                    CheckItems(chest.GetItemsForPlayer());
            }
            if (loc is FarmHouse fh)
                CheckItems(fh.fridge.Value?.GetItemsForPlayer());
            else if (loc is IslandFarmHouse ifh)
                CheckItems(ifh.fridge.Value?.GetItemsForPlayer());
        }

        return owned;
    }

    private static Dictionary<string, List<FishSpawnSpot>> IndexAllRuntimeSpawns(
        Farmer player,
        Dictionary<string, LocationData> locationsData,
        Dictionary<string, string> fishData)
    {
        var result = new Dictionary<string, List<FishSpawnSpot>>(StringComparer.OrdinalIgnoreCase);

        foreach (var locKv in locationsData)
        {
            string locKey = locKv.Key;
            LocationData? locData = locKv.Value;
            if (locKey == "Default" || locData?.Fish is null || locData.Fish.Count == 0)
                continue;

            GameLocation? loc = ResolveEffectiveLocation(locKey);
            if (loc is null)
                continue;

            string locDisplay = ResolveLocationDisplayName(locKey, locData, loc);
            bool isRainingAtLoc = loc.IsRainingHere();

            foreach (SpawnFishData? spawn in locData.Fish)
            {
                if (spawn is null)
                    continue;

                var candidateIds = new List<string>();
                if (!string.IsNullOrWhiteSpace(spawn.ItemId))
                    candidateIds.Add(spawn.ItemId);
                if (spawn.RandomItemId is not null)
                {
                    foreach (string? rid in spawn.RandomItemId)
                    {
                        if (!string.IsNullOrWhiteSpace(rid) && !candidateIds.Contains(rid, StringComparer.OrdinalIgnoreCase))
                            candidateIds.Add(rid);
                    }
                }

                if (candidateIds.Count == 0)
                    continue;

                string? areaDisplay = null;
                Rectangle? areaBounds = null;
                if (!string.IsNullOrWhiteSpace(spawn.FishAreaId))
                {
                    if (locData.FishAreas is not null && locData.FishAreas.TryGetValue(spawn.FishAreaId, out var areaData))
                    {
                        string? rawAreaName = ModEntry.ResolveLocalizedText(areaData?.DisplayName);
                        areaDisplay = string.IsNullOrWhiteSpace(rawAreaName) ? spawn.FishAreaId : rawAreaName;
                        areaBounds = areaData?.Position;
                    }
                    else
                    {
                        areaDisplay = spawn.FishAreaId;
                    }
                }

                foreach (string rawFishId in candidateIds)
                {
                    string qid = NormalizeQid(rawFishId);
                    string shortId = StripObjectPrefix(qid);
                    string? rawFishRow = fishData.GetValueOrDefault(shortId) ?? fishData.GetValueOrDefault(qid);
                    FishMetadata meta = ParseFishMetadata(shortId, rawFishRow, ItemRegistry.GetData(qid));

                    FishSpawnSpot spot = EvaluateSpawnSpot(
                        locKey,
                        locDisplay,
                        loc,
                        isRainingAtLoc,
                        areaDisplay,
                        areaBounds,
                        spawn,
                        meta,
                        player);

                    if (!result.TryGetValue(qid, out var list))
                    {
                        list = new List<FishSpawnSpot>();
                        result[qid] = list;
                    }
                    list.Add(spot);
                }
            }
        }

        return result;
    }

    private static FishSpawnSpot EvaluateSpawnSpot(
        string locKey,
        string locDisplay,
        GameLocation loc,
        bool isRainingAtLoc,
        string? areaDisplay,
        Rectangle? areaBounds,
        SpawnFishData spawn,
        FishMetadata meta,
        Farmer player)
    {
        // Determine effective seasons for THIS location (respecting SpawnFishData.Season, GSQ Condition, and Data/Fish seasons)
        List<string> allowedSeasons = new(AllSeasonKeys);
        if (spawn.Season.HasValue)
        {
            string sKey = spawn.Season.Value.ToString().ToLowerInvariant();
            allowedSeasons = allowedSeasons.Where(s => s == sKey).ToList();
        }

        List<string>? condSeasons = ExtractSeasonsFromCondition(spawn.Condition);
        if (condSeasons is not null)
            allowedSeasons = allowedSeasons.Where(s => condSeasons.Contains(s, StringComparer.OrdinalIgnoreCase)).ToList();

        if (!spawn.IgnoreFishDataRequirements && meta.Seasons.Count > 0)
        {
            // Apply Data/Fish seasons unless explicit SpawnFishData season/condition already overrode it
            if (!spawn.Season.HasValue && condSeasons is null)
                allowedSeasons = allowedSeasons.Where(s => meta.Seasons.Contains(s, StringComparer.OrdinalIgnoreCase)).ToList();
        }

        bool seasonMatchesNow = allowedSeasons.Contains(Game1.currentSeason, StringComparer.OrdinalIgnoreCase);

        string effectiveWeather = spawn.IgnoreFishDataRequirements
            ? "both"
            : meta.Weather;

        string? condWeather = ExtractWeatherFromCondition(spawn.Condition);
        if (!string.IsNullOrWhiteSpace(condWeather))
            effectiveWeather = condWeather;

        bool weatherMatchesToday = effectiveWeather.Equals("both", StringComparison.OrdinalIgnoreCase)
            || (isRainingAtLoc && effectiveWeather.Equals("rainy", StringComparison.OrdinalIgnoreCase))
            || (!isRainingAtLoc && effectiveWeather.Equals("sunny", StringComparison.OrdinalIgnoreCase));

        bool timeMatchesNow = spawn.IgnoreFishDataRequirements || IsTimeInRanges(Game1.timeOfDay, meta.TimeRanges);
        string timeDisplay = spawn.IgnoreFishDataRequirements ? "Tum gun" : meta.TimeDisplay;

        int minLevel = spawn.IgnoreFishDataRequirements
            ? spawn.MinFishingLevel
            : Math.Max(spawn.MinFishingLevel, meta.MinLevel);
        bool levelMet = player.FishingLevel >= minLevel;

        string? combinedCondition = CombineConditions(spawn.Condition, spawn.PerItemCondition);
        bool gsqPassed = string.IsNullOrWhiteSpace(combinedCondition)
            || GameStateQuery.CheckConditions(combinedCondition, loc, player);

        string? extraCondition = DescribeNonTrivialCondition(combinedCondition);

        string liveStatus;
        if (spawn.RequireMagicBait)
            liveStatus = seasonMatchesNow && gsqPassed ? "SIHIRLI YEM GEREKIR" : "SIHIRLI YEM + MEVSIM/SART";
        else if (!seasonMatchesNow)
            liveStatus = "MEVSIM DISI";
        else if (!gsqPassed)
            liveStatus = "SART SAGLANMADI";
        else if (!levelMet)
            liveStatus = $"SEVIYE YETERSIZ (Sv.{minLevel} gerek, sende Sv.{player.FishingLevel})";
        else if (!weatherMatchesToday)
            liveStatus = effectiveWeather.Equals("rainy", StringComparison.OrdinalIgnoreCase) ? "YAGMUR GEREKIR" : "GUNESLI HAVA GEREKIR";
        else if (!timeMatchesNow)
            liveStatus = $"BUGUN ACIK (saat: {timeDisplay})";
        else
            liveStatus = "SU AN ACIK";

        return new FishSpawnSpot(
            LocationKey: locKey,
            LocationDisplayName: locDisplay,
            AreaDisplayName: areaDisplay,
            AreaBounds: areaBounds,
            EffectiveSeasons: allowedSeasons,
            EffectiveWeather: effectiveWeather,
            TimeDisplay: timeDisplay,
            MinFishingLevel: minLevel,
            PlayerPosition: spawn.PlayerPosition,
            BobberPosition: spawn.BobberPosition,
            MinDistanceFromShore: spawn.MinDistanceFromShore,
            MaxDistanceFromShore: spawn.MaxDistanceFromShore,
            RequireMagicBait: spawn.RequireMagicBait,
            IgnoreFishDataRequirements: spawn.IgnoreFishDataRequirements,
            Chance: spawn.Chance,
            CatchLimit: spawn.CatchLimit,
            IsBossFish: spawn.IsBossFish,
            ExtraCondition: extraCondition,
            SeasonMatchesNow: seasonMatchesNow,
            WeatherMatchesToday: weatherMatchesToday,
            TimeMatchesNow: timeMatchesNow,
            LevelMet: levelMet,
            ConditionPassedNow: gsqPassed,
            LiveStatus: liveStatus);
    }

    private static GameLocation? ResolveEffectiveLocation(string locKey)
    {
        if (locKey.StartsWith("Farm_", StringComparison.OrdinalIgnoreCase))
            return null;

        GameLocation? direct = Game1.getLocationFromName(locKey);
        if (direct is not null)
            return direct;

        // UndergroundMine levels (20/60/100) and VolcanoCaldera levels are instantiated dynamically
        if (locKey.StartsWith("UndergroundMine", StringComparison.OrdinalIgnoreCase))
            return Game1.getLocationFromName("Mine") ?? Game1.getFarm();

        return null;
    }

    private static string ResolveLocationDisplayName(string locKey, LocationData locData, GameLocation loc)
    {
        if (locKey.StartsWith("UndergroundMine", StringComparison.OrdinalIgnoreCase))
        {
            string suffix = locKey["UndergroundMine".Length..];
            return $"Maden Kat {suffix}";
        }

        string parsed = ModEntry.ResolveLocalizedText(locData.DisplayName);
        if (!string.IsNullOrWhiteSpace(parsed) && parsed != locKey)
            return parsed;

        string locDisp = ModEntry.ResolveLocalizedText(loc.DisplayName);
        if (!string.IsNullOrWhiteSpace(locDisp) && locDisp != locKey)
            return locDisp;

        return locKey switch
        {
            "Beach" => "Kumsal",
            "Town" => "Pelikan Kasabasi",
            "Forest" => "Komurozu Ormani",
            "Mountain" => "Dag",
            "Woods" => "Gizli Orman",
            "Desert" => "Col",
            "Sewer" => "Lagim",
            "BugLand" => "Mutant Bocek Yuvasi",
            "WitchSwamp" => "Cadi Batakligi",
            "Submarine" => "Gece Pazari Denizaltisi",
            "IslandSouth" => "Zencefil Adasi (Guney)",
            "IslandWest" => "Zencefil Adasi (Bati)",
            "IslandNorth" => "Zencefil Adasi (Kuzey)",
            "IslandSouthEast" => "Zencefil Adasi (Guneydogu)",
            "IslandSouthEastCave" => "Korsan Koyu",
            "Caldera" => "Yanardag Krateri",
            _ => locKey
        };
    }

    private static List<string>? ExtractSeasonsFromCondition(string? condition)
    {
        if (string.IsNullOrWhiteSpace(condition))
            return null;

        var result = new HashSet<string>(AllSeasonKeys, StringComparer.OrdinalIgnoreCase);
        bool narrowed = false;

        foreach (string clause in condition.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            bool negated = clause.StartsWith("!", StringComparison.Ordinal);
            string clean = negated ? clause[1..].Trim() : clause;
            string[] tokens = clean.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length < 2)
                continue;

            int argStart = -1;
            if (tokens[0].Equals("LOCATION_SEASON", StringComparison.OrdinalIgnoreCase) && tokens.Length >= 3)
                argStart = 2;
            else if (tokens[0].Equals("SEASON", StringComparison.OrdinalIgnoreCase))
                argStart = 1;

            if (argStart < 0)
                continue;

            var mentioned = tokens.Skip(argStart)
                .Where(t => AllSeasonKeys.Contains(t, StringComparer.OrdinalIgnoreCase))
                .Select(t => t.ToLowerInvariant())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (mentioned.Count == 0)
                continue;

            narrowed = true;
            if (negated)
                result.RemoveWhere(s => mentioned.Contains(s));
            else
                result.RemoveWhere(s => !mentioned.Contains(s));
        }

        return narrowed ? AllSeasonKeys.Where(s => result.Contains(s)).ToList() : null;
    }

    private static string? ExtractWeatherFromCondition(string? condition)
    {
        if (string.IsNullOrWhiteSpace(condition))
            return null;

        foreach (string clause in condition.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            bool negated = clause.StartsWith("!", StringComparison.Ordinal);
            string clean = negated ? clause[1..].Trim() : clause;
            if (!clean.StartsWith("WEATHER ", StringComparison.OrdinalIgnoreCase))
                continue;

            if (clean.Contains("Rain", StringComparison.OrdinalIgnoreCase)
                || clean.Contains("Storm", StringComparison.OrdinalIgnoreCase))
                return negated ? "sunny" : "rainy";

            if (clean.Contains("Sun", StringComparison.OrdinalIgnoreCase))
                return negated ? "rainy" : "sunny";
        }

        return null;
    }

    private static string? DescribeNonTrivialCondition(string? condition)
    {
        if (string.IsNullOrWhiteSpace(condition))
            return null;

        var extras = new List<string>();
        foreach (string clause in condition.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string trim = clause.TrimStart('!').Trim();
            if (trim.StartsWith("LOCATION_SEASON ", StringComparison.OrdinalIgnoreCase)
                || trim.StartsWith("SEASON ", StringComparison.OrdinalIgnoreCase)
                || trim.StartsWith("WEATHER ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            extras.Add(clause.Trim());
        }

        return extras.Count > 0 ? string.Join(", ", extras) : null;
    }

    private static string? CombineConditions(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a)) return string.IsNullOrWhiteSpace(b) ? null : b;
        if (string.IsNullOrWhiteSpace(b)) return a;
        return $"{a}, {b}";
    }

    private static bool IsSeasonalAnywhereNow(FishGuideEntry entry, List<FishSpawnSpot> spots)
    {
        if (spots.Count > 0)
            return spots.Any(s => s.SeasonMatchesNow && !s.RequireMagicBait);

        return entry.DataSeasons.Contains(TranslateSeason(Game1.currentSeason), StringComparison.OrdinalIgnoreCase);
    }

    private static int BestSpotScore(List<FishSpawnSpot> spots)
    {
        int best = 0;
        foreach (FishSpawnSpot s in spots)
        {
            int score;
            if (!s.SeasonMatchesNow) score = 1;
            else if (s.RequireMagicBait) score = 2;
            else if (!s.ConditionPassedNow || !s.LevelMet) score = 3;
            else if (!s.WeatherMatchesToday) score = 4;
            else if (!s.TimeMatchesNow) score = 5;
            else score = 6;

            if (score > best)
                best = score;
        }
        return best;
    }

    private static List<FishSpawnSpot> OrderSpotsForPlayer(List<FishSpawnSpot> spots)
    {
        return spots
            .OrderByDescending(s => s.SeasonMatchesNow && !s.RequireMagicBait && s.ConditionPassedNow)
            .ThenByDescending(s => s.WeatherMatchesToday && s.TimeMatchesNow && s.LevelMet)
            .ThenByDescending(s => s.SeasonMatchesNow)
            .ThenBy(s => s.LocationDisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> BuildSummarySampleList(List<FishGuideEntry> missingSeasonal)
    {
        var list = new List<string>();
        foreach (FishGuideEntry e in missingSeasonal.OrderByDescending(x => BestSpotScore(x.Spots)).Take(16))
        {
            FishSpawnSpot? best = e.Spots.FirstOrDefault();
            string locTag = best is not null
                ? $"@{best.LocationDisplayName}"
                : "";
            string weatherTag = best is not null && !best.WeatherMatchesToday
                ? (best.EffectiveWeather == "rainy" ? " [yagmur]" : " [gunes]")
                : (e.IsTrapFish ? " [comlek]" : "");
            list.Add($"{e.DisplayName}{locTag}{weatherTag}");
        }
        return list;
    }

    private static List<string> DetectSimilarNames(List<FishGuideEntry> entries, Dictionary<string, string> fishData)
    {
        var allFish = new List<(string Qid, string DisplayName)>();
        foreach (string k in fishData.Keys)
        {
            string qid = NormalizeQid(k);
            string? disp = ItemRegistry.GetData(qid)?.DisplayName;
            if (!string.IsNullOrWhiteSpace(disp))
                allFish.Add((qid, disp));
        }

        var warnings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (FishGuideEntry target in entries)
        {
            foreach (var other in allFish)
            {
                if (other.Qid.Equals(target.QualifiedItemId, StringComparison.OrdinalIgnoreCase))
                    continue;

                bool exactMatch = string.Equals(other.DisplayName, target.DisplayName, StringComparison.OrdinalIgnoreCase);
                bool closeSuffix = !exactMatch
                    && Math.Abs(other.DisplayName.Length - target.DisplayName.Length) <= 2
                    && (other.DisplayName.StartsWith(target.DisplayName, StringComparison.OrdinalIgnoreCase)
                        || target.DisplayName.StartsWith(other.DisplayName, StringComparison.OrdinalIgnoreCase));

                if (!exactMatch && !closeSuffix)
                    continue;

                string a = $"{target.DisplayName} [{StripObjectPrefix(target.QualifiedItemId)}]";
                string b = $"{other.DisplayName} [{StripObjectPrefix(other.Qid)}]";
                string pair = string.CompareOrdinal(a, b) <= 0 ? $"{a} <-> {b}" : $"{b} <-> {a}";
                warnings.Add(pair);
            }
        }

        return warnings.OrderBy(w => w, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsFishObjectOrKnown(string qid, Dictionary<string, string> fishData)
    {
        if (fishData.ContainsKey(StripObjectPrefix(qid)) || fishData.ContainsKey(qid))
            return true;
        ParsedItemData? data = ItemRegistry.GetData(qid);
        return data is not null && data.Category == StardewValley.Object.FishCategory;
    }

    private static bool IsExcludedFromFishingCollection(
        string shortKey,
        string qid,
        ParsedItemData? itemData,
        Dictionary<string, ObjectData> objectsData)
    {
        if (itemData?.RawData is ObjectData od)
            return od.ExcludeFromFishingCollection;
        if (objectsData.TryGetValue(shortKey, out ObjectData? obj) || objectsData.TryGetValue(StripObjectPrefix(qid), out obj))
            return obj.ExcludeFromFishingCollection;
        return false;
    }

    private static FishMetadata ParseFishMetadata(string shortId, string? rawRow, ParsedItemData? itemData)
    {
        string displayName = itemData?.DisplayName ?? shortId;
        string internalName = itemData?.InternalName ?? shortId;
        if (string.IsNullOrWhiteSpace(rawRow))
        {
            return new FishMetadata(displayName, internalName, false, 0, "-", new List<(int, int)>(), "06:00-02:00",
                new List<string>(AllSeasonKeys), "Tum mevsimler", "both", 0);
        }

        string[] parts = rawRow.Split('/');
        bool isTrap = parts.Length > 1 && parts[1].Equals("trap", StringComparison.OrdinalIgnoreCase);
        if (isTrap)
        {
            return new FishMetadata(displayName, internalName, true, 0, "yengec comlegi", new List<(int, int)>(), "Tum gun (comlek)",
                new List<string>(AllSeasonKeys), "Tum mevsimler", "both", 0);
        }

        int.TryParse(parts.Length > 1 ? parts[1] : "0", out int diff);
        string behavior = parts.Length > 2 ? parts[2] : "-";
        List<(int Start, int End)> timeRanges = ParseTimeRanges(parts.Length > 5 ? parts[5] : string.Empty);
        string timeDisplay = FormatTimeRanges(timeRanges);

        var seasons = (parts.Length > 6 ? parts[6] : string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.ToLowerInvariant())
            .Where(s => AllSeasonKeys.Contains(s))
            .Distinct()
            .ToList();
        string seasonsDisplay = FormatSeasonList(seasons);

        string weather = parts.Length > 7 ? parts[7].Trim().ToLowerInvariant() : "both";
        int minLevel = 0;
        if (parts.Length > 12)
            int.TryParse(parts[12], out minLevel);

        return new FishMetadata(displayName, internalName, false, diff, behavior, timeRanges, timeDisplay, seasons, seasonsDisplay, weather, minLevel);
    }

    private static List<(int Start, int End)> ParseTimeRanges(string raw)
    {
        var list = new List<(int, int)>();
        string[] nums = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i + 1 < nums.Length; i += 2)
        {
            if (int.TryParse(nums[i], out int start) && int.TryParse(nums[i + 1], out int end))
                list.Add((start, end));
        }
        return list;
    }

    private static bool IsTimeInRanges(int timeOfDay, List<(int Start, int End)> ranges)
    {
        if (ranges.Count == 0)
            return true;
        foreach (var (s, e) in ranges)
        {
            if (timeOfDay >= s && timeOfDay < e)
                return true;
        }
        return false;
    }

    private static string FormatTimeRanges(List<(int Start, int End)> ranges)
    {
        if (ranges.Count == 0)
            return "Tum gun";
        return string.Join(", ", ranges.Select(r => $"{FormatClock(r.Start)}-{FormatClock(r.End)}"));
    }

    private static string FormatClock(int t)
    {
        int h = t / 100;
        int m = t % 100;
        return $"{h:D2}:{m:D2}";
    }

    public static string FormatSeasonList(List<string> seasons)
    {
        if (seasons.Count == 0)
            return "Hicbir mevsim";
        if (seasons.Count >= 4)
            return "Tum mevsimler";
        return string.Join("/", seasons.Select(TranslateSeason));
    }

    public static string TranslateSeason(string s) => s.ToLowerInvariant() switch
    {
        "spring" => "Bahar",
        "summer" => "Yaz",
        "fall" => "Guz",
        "winter" => "Kis",
        _ => s
    };

    public static string NormalizeQid(string id) => id.StartsWith("(", StringComparison.Ordinal) ? id : "(O)" + id;

    public static string StripObjectPrefix(string qid) => qid.StartsWith("(O)", StringComparison.OrdinalIgnoreCase) ? qid[3..] : qid;

    private sealed record FishMetadata(
        string DisplayName,
        string InternalName,
        bool IsTrapFish,
        int Difficulty,
        string Behavior,
        List<(int Start, int End)> TimeRanges,
        string TimeDisplay,
        List<string> Seasons,
        string SeasonsDisplay,
        string Weather,
        int MinLevel);
}
