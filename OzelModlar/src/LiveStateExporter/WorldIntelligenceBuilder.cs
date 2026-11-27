using System.Globalization;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.GameData.Buildings;
using StardewValley.GameData.Shops;
using StardewValley.Internal;
using StardewValley.Locations;
using StardewValley.Objects;
using StardewValley.Quests;
using StardewValley.SpecialOrders;

namespace LiveStateExporter;

public interface ISpaceCoreApi
{
    string[] GetCustomSkills();
    int GetLevelForCustomSkill(Farmer farmer, string skill);
}

public interface IWearMoreRingsApi
{
    int RingSlotCount();
    Ring? GetRing(int slot);
}

internal sealed record ModEcosystemEntry(
    string ModName,
    string Category,
    string StatusSummary,
    string ActionableTip);

internal sealed record WorldEventEntry(
    string EventId,
    string LocationKey,
    string LocationDisplayName,
    string NpcSummary,
    int MaxRequiredHearts,
    int HeartGap,
    string TimeWindow,
    string WeatherNote,
    string ExtraNote,
    string LiveStatus,
    bool IsModEvent,
    int StartTime = 600,
    int EndTime = 2600,
    bool HasTimeWindow = false,
    string? WeatherReq = null);

internal sealed record DailyQuestBoardEntry(
    string Title,
    string Objective,
    bool AlreadyAccepted,
    string? TargetItemName,
    bool HasTargetItemInStorage,
    string StorageSources);

internal sealed record AvailableOrderEntry(
    string BoardLabel,
    string Name,
    string Requester,
    string DurationText,
    bool BoardAlreadyAcceptedToday,
    string Description);

internal sealed record NpcMilestoneEntry(
    string NpcKey,
    string DisplayName,
    int CurrentHearts,
    int TargetHearts,
    int PointsNeeded,
    List<string> Unlocks,
    List<string> OwnedLovedGifts,
    List<string> OwnedLikedGifts,
    string CurrentLocation);

internal sealed record BirthdayGiftMatch(
    string NpcKey,
    string DisplayName,
    int DaysUntil,
    int CurrentHearts,
    bool GiftedToday,
    List<string> OwnedLovedGifts,
    List<string> OwnedLikedGifts,
    string CurrentLocation);

internal sealed record BookStockEntry(
    string DisplayName,
    int Price,
    bool AlreadyRead);

internal sealed record ProgressionGateEntry(
    string Category,
    string Title,
    string Progress,
    string NextStep);

internal sealed record StardropStatusEntry(
    string Name,
    bool Obtained,
    string HowToGet);

internal sealed class WorldIntelligenceSnapshot
{
    public List<(string Name, int Level)> CustomSkills { get; init; } = new();
    public List<ModEcosystemEntry> ModHighlights { get; init; } = new();
    public List<WorldEventEntry> ReadyEvents { get; init; } = new();
    public List<WorldEventEntry> UpcomingEvents { get; init; } = new();
    public DailyQuestBoardEntry? DailyHelpWanted { get; init; }
    public List<AvailableOrderEntry> AvailableSpecialOrders { get; init; } = new();
    public List<NpcMilestoneEntry> NpcMilestones { get; init; } = new();
    public List<BirthdayGiftMatch> BirthdayGifts { get; init; } = new();
    public List<BookStockEntry> BooksellerStock { get; init; } = new();
    public List<ProgressionGateEntry> ProgressionGates { get; init; } = new();
    public List<StardropStatusEntry> Stardrops { get; init; } = new();
    public List<string> UnbuiltKeyBuildings { get; init; } = new();
    public List<string> UncraftedKnownMachines { get; init; } = new();
    public List<string> EquipmentAlerts { get; init; } = new();
    public int GeodeBacklogCount { get; init; }
    public string GeodeBacklogSummary { get; init; } = string.Empty;
    public int UnautomatedMachineCount { get; init; }
    public string UnautomatedMachineSummary { get; init; } = string.Empty;
    public bool HasSkullCavernElevatorMod { get; init; }
    public bool HasAutomateMod { get; init; }
    public bool HasTractorMod { get; init; }
    public bool HasBiggerBackpackMod { get; init; }
    public bool HasWearMoreRingsMod { get; init; }
    public bool HasSveMod { get; init; }
    public List<string> MustDoRules { get; init; } = new();
    public List<string> StrategicRules { get; init; } = new();
}

internal static class WorldIntelligenceBuilder
{
    private sealed record CachedEventScan(
        string Key,
        List<WorldEventEntry> Ready,
        List<WorldEventEntry> Upcoming);

    private sealed record CachedAutomateScan(
        string Key,
        int UnautomatedCount,
        string Summary);

    private static CachedEventScan? _eventCache;
    private static CachedAutomateScan? _automateCache;
    private static readonly Dictionary<string, StardewValley.Object?> _giftSampleCache = new(StringComparer.OrdinalIgnoreCase);
    private static string _giftTasteCacheDayKey = string.Empty;
    private static readonly Dictionary<(string NpcKey, string Qid), int> _giftTasteCache = new();

    private static readonly Dictionary<string, (int Hearts, string Reward)> KeyNpcManualMilestones = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Caroline"] = (2, "Çay Fidanı tarifi (Güneş Odası etkinliği — çok kârlı erken/orta oyun geliri)"),
        ["Linus"] = (4, "Vahşi Yem tarifi (çift balık yakalama şansı)"),
        ["Wizard"] = (4, "Büyücü bodrumu (Görünüm Tapınağı) + SVE/Büyücü hikâye zinciri"),
        ["Willy"] = (6, "Willy özel tarifleri ve balıkçılık hikâyesi"),
        ["Robin"] = (3, "Balkabağı Çorbası (7♥: +2 Şans, +2 Savunma yemek tarifi)"),
        ["George"] = (7, "Baharatlı Yılanbalığı tarifi (+1 Hız, +1 Şans — maden/Kafatası için en iyi yemeklerden)"),
        ["Demetrius"] = (3, "Sonbahar Armağanı (7♥: +2 Toplayıcılık, +2 Savunma)"),
        ["Emily"] = (3, "Salata / Kumaş hediyeleri (7♥: Baharatlı Yılanbalığı/Yün/Kumaş postaları)"),
        ["Clint"] = (3, "Cevher/Külçe hediyeleri"),
        ["Marnie"] = (3, "Saman hediyeleri + soluk et suyu tarifi"),
        ["Gus"] = (3, "Somon Yemeği & Kızılcık Sosu tarifleri"),
        ["Marlon"] = (2, "SVE Lonca hikâyesi ve yüksek seviye savaş görevleri"),
        ["MarlonFay"] = (5, "SVE Paslı Anahtar (Kanalizasyon & Krobus kilidi — Yıl 1'de 5♥ ile erken açılır; Yıl 2'de Maden 120 ile otomatik)"),
        ["Sophia"] = (4, "SVE Mavi Ay Bağı hikâyesi & özel şarap etkileşimleri"),
        ["Andy"] = (4, "SVE Çiftçi komşu hikâyesi ve hediyeler"),
        ["Susan"] = (4, "SVE Zanaat/tarım tarifleri ve Demiryolu hikâyesi"),
        ["Lance"] = (2, "SVE İleri seviye macera/büyü bölgesi etkileşimleri"),
        ["Ian"] = (2, "Ridgeside Köyü Çiftlik Hizmetleri & Özel İşler"),
        ["Jio"] = (2, "Ridgeside Ninja Evi & Ruhlar Alemi zinciri"),
        ["Kiwi"] = (2, "Ridgeside Ninja Evi & Ruhlar Alemi zinciri"),
        ["Mateo"] = (2, "Kılıç ve Büyü (Sword & Sorcery) lonca/sınıf hikâyesi"),
        ["Hector"] = (2, "Kılıç ve Büyü (Sword & Sorcery) Druid/büyü hikâyesi"),
        ["Cirrus"] = (2, "Kılıç ve Büyü (Sword & Sorcery) hikâye ve yetenek zinciri"),
        ["Sen"] = (2, "Karanlıkta Gizlenen (East Scarp / Sen) hikâye zinciri")
    };

    private static readonly (string Flag, string Name, string HowToGet)[] VanillaStardrops =
    {
        ("CF_Fair", "Stardew Valley Fuarı (Güz 16)", "Güz 16 Fuarı'nda 2.000 Yıldız Jetonu ile ödül tezgâhından alınır"),
        ("CF_Mines", "Maden 100. Kat", "Normal Maden 100. kattaki hazine sandığından alınır"),
        ("CF_Statue", "Gizli Orman Heykeli (Yaşlı Usta Cannoli)", "Gizli Orman'daki heykele 1 adet Tatlıtaş Meyvesi verilir"),
        ("CF_Sewer", "Krobus (Kanalizasyon)", "60 Müze bağışı ile Paslı Anahtar açılır, Krobus'tan 20.000g'ye alınır"),
        ("CF_Fish", "Usta Balıkçı", "Koleksiyondaki tüm balıkları yakaladıktan sonra Willy posta ile gönderir"),
        ("CF_Museum", "Müze Tamamlama", "Müze koleksiyonunu tamamen doldurunca Günther ödül olarak verir"),
        ("CF_Spouse", "Eş / Oda Arkadaşı (12.5 Kalp)", "Evlenip veya Krobus'u eve alıp 12.5 kalbe ulaşınca hediye eder")
    };

    public static WorldIntelligenceSnapshot Build(
        string modDir,
        IMonitor? monitor,
        IModRegistry modRegistry,
        Farmer player,
        Farm farm,
        bool greenhouseUnlocked,
        bool islandUnlocked,
        bool desertUnlocked,
        bool ccAvailable,
        bool isRainingOnFarm,
        bool isBooksellerToday,
        List<ItemEntry> inventory,
        List<ChestEntry> chests,
        List<BuildingEntry> buildings,
        List<MachineEntry> machines,
        List<CropEntry> crops,
        List<FriendshipEntry> friendships,
        List<GameLocation> chestLocations,
        ModEntry.OwnedInventoryIndex owned,
        int hoeLevel,
        bool forceEventRescan)
    {
        var customSkills = CollectCustomSkills(modRegistry, player);
        bool hasSve = modRegistry.IsLoaded("FlashShifter.SVECode");
        bool hasAutomate = modRegistry.IsLoaded("Pathoschild.Automate");
        bool hasSkullElevator = modRegistry.IsLoaded("SkullCavernElevator");
        bool hasTractor = modRegistry.IsLoaded("Pathoschild.TractorMod");
        bool hasBiggerBackpack = modRegistry.IsLoaded("spacechase0.BiggerBackpack");
        bool hasWearMoreRings = modRegistry.IsLoaded("bcmpinc.WearMoreRings");

        var (geodeCount, geodeSummary) = CollectGeodeBacklog(owned);
        string autoKey = $"{StardewModdingAPI.Constants.SaveFolderName}|{Game1.Date.TotalDays}|{farm.objects.Length}|{buildings.Count}";
        int unautomatedCount;
        string unautomatedSummary;
        if (!hasAutomate)
        {
            (unautomatedCount, unautomatedSummary) = (0, string.Empty);
        }
        else if (!forceEventRescan && _automateCache is not null && _automateCache.Key == autoKey)
        {
            unautomatedCount = _automateCache.UnautomatedCount;
            unautomatedSummary = _automateCache.Summary;
        }
        else
        {
            (unautomatedCount, unautomatedSummary) = AnalyzeAutomateGaps(farm, buildings);
            _automateCache = new CachedAutomateScan(autoKey, unautomatedCount, unautomatedSummary);
        }

        var dailyHelpWanted = CollectDailyHelpWanted(player, owned);
        var availableOrders = CollectAvailableSpecialOrders(player, islandUnlocked);

        var (readyEvents, upcomingEvents) = ScanWorldEvents(
            player,
            isRainingOnFarm,
            owned,
            forceEventRescan);

        var npcIndex = BuildLiveNpcLookup();
        var storageSampleItems = BuildUniqueStorageItems(inventory, chests);

        var birthdayGifts = CollectBirthdayGiftMatches(player, npcIndex, storageSampleItems, owned);
        var npcMilestones = CollectNpcMilestones(player, npcIndex, storageSampleItems, owned, upcomingEvents);
        var booksellerStock = isBooksellerToday ? CollectBooksellerStock(player) : new List<BookStockEntry>();
        var stardrops = CollectStardrops(player, hasSve);
        var unbuiltBuildings = CollectUnbuiltKeyBuildings(farm, player, hasTractor, crops.Sum(c => c.Count));
        var uncraftedMachines = CollectUncraftedKnownMachines(player, machines);
        var equipmentAlerts = CollectEquipmentAlerts(modRegistry, player, hasWearMoreRings);

        var ruleCtx = BuildRuleContext(
            modRegistry,
            player,
            farm,
            greenhouseUnlocked,
            islandUnlocked,
            desertUnlocked,
            ccAvailable,
            isRainingOnFarm,
            isBooksellerToday,
            crops,
            machines,
            availableOrders,
            customSkills,
            owned,
            hoeLevel,
            geodeCount,
            geodeSummary,
            unautomatedCount,
            unautomatedSummary,
            readyEvents);

        var ruleResult = AdvisorRuleEngine.Evaluate(modDir, monitor, ruleCtx, forceEventRescan);

        // Combine rule-driven progression gates with dynamic event/order/stardrop gates
        var progressionGates = new List<ProgressionGateEntry>(ruleResult.GateRules);
        AppendDynamicGates(progressionGates, stardrops, availableOrders, readyEvents);

        return new WorldIntelligenceSnapshot
        {
            CustomSkills = customSkills,
            ModHighlights = ruleResult.ModRules,
            ReadyEvents = readyEvents,
            UpcomingEvents = upcomingEvents,
            DailyHelpWanted = dailyHelpWanted,
            AvailableSpecialOrders = availableOrders,
            NpcMilestones = npcMilestones,
            BirthdayGifts = birthdayGifts,
            BooksellerStock = booksellerStock,
            ProgressionGates = progressionGates,
            Stardrops = stardrops,
            UnbuiltKeyBuildings = unbuiltBuildings,
            UncraftedKnownMachines = uncraftedMachines,
            EquipmentAlerts = equipmentAlerts,
            GeodeBacklogCount = geodeCount,
            GeodeBacklogSummary = geodeSummary,
            UnautomatedMachineCount = unautomatedCount,
            UnautomatedMachineSummary = unautomatedSummary,
            HasSkullCavernElevatorMod = hasSkullElevator,
            HasAutomateMod = hasAutomate,
            HasTractorMod = hasTractor,
            HasBiggerBackpackMod = hasBiggerBackpack,
            HasWearMoreRingsMod = hasWearMoreRings,
            HasSveMod = hasSve,
            MustDoRules = ruleResult.MustDoRules,
            StrategicRules = ruleResult.StrategicRules
        };
    }

    private static List<(string Name, int Level)> CollectCustomSkills(IModRegistry modRegistry, Farmer player)
    {
        var result = new List<(string Name, int Level)>();
        try
        {
            var spaceCore = modRegistry.GetApi<ISpaceCoreApi>("spacechase0.SpaceCore");
            if (spaceCore is null)
                return result;

            string[] skills = spaceCore.GetCustomSkills() ?? Array.Empty<string>();
            foreach (string skillId in skills)
            {
                if (string.IsNullOrWhiteSpace(skillId))
                    continue;
                int lv = spaceCore.GetLevelForCustomSkill(player, skillId);
                string cleanName = FormatCustomSkillName(skillId);
                result.Add((cleanName, lv));
            }
        }
        catch
        {
            // SpaceCore API not available or incompatible
        }

        return result.OrderByDescending(x => x.Level).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string FormatCustomSkillName(string skillId)
    {
        int dot = skillId.LastIndexOf('.');
        string tail = dot >= 0 && dot + 1 < skillId.Length ? skillId[(dot + 1)..] : skillId;
        return tail.Replace("Skill", "", StringComparison.OrdinalIgnoreCase).Replace("_", " ").Trim();
    }

    private static (int Total, string Summary) CollectGeodeBacklog(ModEntry.OwnedInventoryIndex owned)
    {
        var types = new (string Qid, string Label)[]
        {
            ("(O)535", "Jeot"),
            ("(O)536", "Donuk Jeot"),
            ("(O)537", "Yanık Jeot"),
            ("(O)749", "Her Türlü Jeot"),
            ("(O)275", "Eser Sandığı"),
            ("(O)MysteryBox", "Gizem Kutusu"),
            ("(O)GoldenMysteryBox", "Altın Gizem Kutusu")
        };

        int total = 0;
        var parts = new List<string>();
        foreach (var (qid, label) in types)
        {
            int count = owned.GetStorageCount(qid);
            if (count > 0)
            {
                total += count;
                parts.Add($"{label} x{count}");
            }
        }

        return (total, string.Join(", ", parts));
    }

    private static (int UnautomatedCount, string Summary) AnalyzeAutomateGaps(Farm farm, List<BuildingEntry> buildings)
    {
        var unautomatedByType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        void ScanLocation(GameLocation? loc)
        {
            if (loc is null)
                return;

            var chestTiles = new HashSet<Vector2>();
            var connectorTiles = new HashSet<Vector2>();

            foreach (var pair in loc.objects.Pairs)
            {
                if (pair.Value is Chest c && c.playerChest.Value)
                    chestTiles.Add(pair.Key);
                else if (pair.Value is not null && (pair.Value.QualifiedItemId == "(BC)165" || pair.Value.Name.Contains("Shipping", StringComparison.OrdinalIgnoreCase)))
                    chestTiles.Add(pair.Key);
            }

            foreach (var pair in loc.terrainFeatures.Pairs)
            {
                if (pair.Value is StardewValley.TerrainFeatures.Flooring)
                    connectorTiles.Add(pair.Key);
            }

            // BFS from chests across adjacent objects/machines/connectors (8-way adjacency as Automate does)
            var visited = new HashSet<Vector2>(chestTiles);
            var queue = new Queue<Vector2>(chestTiles);
            while (queue.Count > 0)
            {
                Vector2 cur = queue.Dequeue();
                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        if (dx == 0 && dy == 0)
                            continue;
                        var next = new Vector2(cur.X + dx, cur.Y + dy);
                        if (visited.Contains(next))
                            continue;

                        bool isMachineOrConnector = connectorTiles.Contains(next)
                            || (loc.objects.TryGetValue(next, out var obj) && obj is not null && (obj is Chest || IsAutomatableMachine(obj)));
                        if (isMachineOrConnector)
                        {
                            visited.Add(next);
                            queue.Enqueue(next);
                        }
                    }
                }
            }

            foreach (var pair in loc.objects.Pairs)
            {
                var obj = pair.Value;
                if (obj is null || !IsAutomatableMachine(obj))
                    continue;
                if (visited.Contains(pair.Key))
                    continue;

                string name = ModEntry.TranslateMachineNameTr(obj.Name);
                unautomatedByType[name] = unautomatedByType.TryGetValue(name, out int cur) ? cur + 1 : 1;
            }
        }

        ScanLocation(farm);
        foreach (Building b in farm.buildings)
        {
            GameLocation? indoors = b.GetIndoors();
            if (indoors is not null && (b.buildingType.Value.Contains("Shed", StringComparison.OrdinalIgnoreCase)
                || b.buildingType.Value.Contains("Barn", StringComparison.OrdinalIgnoreCase)
                || b.buildingType.Value.Contains("Coop", StringComparison.OrdinalIgnoreCase)))
            {
                ScanLocation(indoors);
            }
        }
        ScanLocation(Game1.getLocationFromName("FarmCave"));

        int total = unautomatedByType.Values.Sum();
        string summary = string.Join(", ", unautomatedByType
            .OrderByDescending(kv => kv.Value)
            .Take(5)
            .Select(kv => $"{kv.Key} x{kv.Value}"));
        return (total, summary);
    }

    private static bool IsAutomatableMachine(StardewValley.Object obj)
    {
        if (obj is Chest || obj.Name is "Lightning Rod" or "Solar Panel" or "Scarecrow" or "Tapper" or "Heavy Tapper")
            return false;
        return obj is CrabPot
            || obj.Name is "Keg" or "Preserves Jar" or "Cheese Press" or "Mayonnaise Machine" or "Furnace" or "Heavy Furnace"
                or "Dehydrator" or "Crystalarium" or "Charcoal Kiln" or "Mushroom Box" or "Worm Bin" or "Deluxe Worm Bin"
                or "Oil Maker" or "Loom" or "Recycling Machine" or "Bone Mill" or "Geode Crusher" or "Fish Smoker" or "Bait Maker"
            || obj.GetMachineData() is not null;
    }

    private static DailyQuestBoardEntry? CollectDailyHelpWanted(Farmer player, ModEntry.OwnedInventoryIndex owned)
    {
        try
        {
            Quest? q = Game1.questOfTheDay;
            if (q is null)
                return null;

            string title = ModEntry.ResolveLocalizedText(q.questTitle);
            string obj = ModEntry.ResolveLocalizedText(q.currentObjective);
            if (string.IsNullOrWhiteSpace(obj))
                obj = ModEntry.ResolveLocalizedText(q.questDescription);

            string? reqId = q switch
            {
                ItemDeliveryQuest idq => idq.ItemId.Value,
                FishingQuest fq => fq.ItemId.Value,
                ResourceCollectionQuest rcq => rcq.ItemId.Value,
                _ => null
            };

            string? reqName = null;
            bool hasItem = false;
            string sourcesStr = string.Empty;

            if (!string.IsNullOrWhiteSpace(reqId))
            {
                string qid = FishGuideBuilder.NormalizeQid(reqId);
                reqName = ItemRegistry.GetData(qid)?.DisplayName ?? reqId;
                var (total, sources) = owned.FindMatches(qid);
                if (total > 0)
                {
                    hasItem = true;
                    sourcesStr = string.Join(", ", sources);
                }
            }

            return new DailyQuestBoardEntry(
                string.IsNullOrWhiteSpace(title) ? "Günlük İlan" : title,
                obj,
                player.acceptedDailyQuest.Value,
                reqName,
                hasItem,
                sourcesStr);
        }
        catch
        {
            return null;
        }
    }

    private static List<AvailableOrderEntry> CollectAvailableSpecialOrders(Farmer player, bool islandUnlocked)
    {
        var result = new List<AvailableOrderEntry>();
        try
        {
            var acceptedKeys = new HashSet<string>(
                player.team.specialOrders.Where(o => o is not null).Select(o => o.questKey.Value ?? string.Empty),
                StringComparer.OrdinalIgnoreCase);

            foreach (SpecialOrder? o in player.team.availableSpecialOrders)
            {
                if (o is null)
                    continue;
                if (acceptedKeys.Contains(o.questKey.Value ?? string.Empty))
                    continue;

                string orderType = o.orderType.Value ?? string.Empty;
                if (orderType.Equals("Qi", StringComparison.OrdinalIgnoreCase) && !islandUnlocked)
                    continue;
                bool alreadyAcceptedType = player.team.acceptedSpecialOrderTypes.Contains(orderType);

                string boardLabel = orderType switch
                {
                    "" => "Kasaba Özel Sipariş Panosu",
                    "Qi" => "Bay Qi Ceviz Odası Panosu",
                    _ when orderType.Contains("RSV", StringComparison.OrdinalIgnoreCase) && orderType.Contains("Ninja", StringComparison.OrdinalIgnoreCase) => "Ridgeside Ninja Panosu",
                    _ when orderType.Contains("RSV", StringComparison.OrdinalIgnoreCase) => "Ridgeside Özel İşler Panosu",
                    _ when orderType.Contains("SVE", StringComparison.OrdinalIgnoreCase) => "SVE Özel Pano",
                    _ => $"Özel Pano ({orderType})"
                };

                string name = ModEntry.ResolveLocalizedText(o.GetName());
                string desc = ModEntry.ResolveLocalizedText(o.GetDescription());
                if (desc.Length > 140)
                    desc = desc[..137] + "...";

                string requester = o.requester.Value ?? string.Empty;
                int days = Math.Max(0, o.GetData()?.Duration switch
                {
                    StardewValley.GameData.SpecialOrders.QuestDuration.Week => 7,
                    StardewValley.GameData.SpecialOrders.QuestDuration.TwoWeeks => 14,
                    StardewValley.GameData.SpecialOrders.QuestDuration.Month => 28,
                    StardewValley.GameData.SpecialOrders.QuestDuration.TwoDays => 2,
                    StardewValley.GameData.SpecialOrders.QuestDuration.ThreeDays => 3,
                    _ => 7
                });

                result.Add(new AvailableOrderEntry(
                    boardLabel,
                    name,
                    requester,
                    $"{days}g",
                    alreadyAcceptedType,
                    desc));
            }
        }
        catch
        {
            // Ignore malformed special order boards
        }

        return result;
    }

    private static (List<WorldEventEntry> Ready, List<WorldEventEntry> Upcoming) ScanWorldEvents(
        Farmer player,
        bool isRainingOnFarm,
        ModEntry.OwnedInventoryIndex owned,
        bool forceRescan)
    {
        int friendshipChecksum = player.friendshipData.Pairs.Sum(kv => kv.Value.Points / 250);
        string cacheKey = $"{StardewModdingAPI.Constants.SaveFolderName}|{Game1.Date.TotalDays}|{isRainingOnFarm}|{player.eventsSeen.Count}|{friendshipChecksum}|{player.mailReceived.Count}";

        if (!forceRescan && _eventCache is not null && _eventCache.Key == cacheKey)
        {
            var refreshedReady = RefreshReadyEventStatuses(_eventCache.Ready, isRainingOnFarm);
            return (refreshedReady, _eventCache.Upcoming);
        }

        var ready = new List<WorldEventEntry>();
        var upcoming = new List<WorldEventEntry>();
        var seenEventIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Build NPC display name lookup
        var npcNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Game1.characterData is not null)
        {
            foreach (var kv in Game1.characterData)
            {
                string dn = !string.IsNullOrWhiteSpace(kv.Value?.DisplayName)
                    ? ModEntry.ResolveLocalizedText(kv.Value.DisplayName)
                    : kv.Key;
                npcNames[kv.Key] = dn;
            }
        }

        foreach (GameLocation? loc in Game1.locations)
        {
            if (loc is null || string.IsNullOrWhiteSpace(loc.Name))
                continue;
            if (!ModEntry.IsLocationUnlockedForExport(loc, player))
                continue;

            Dictionary<string, string>? events = null;
            try
            {
                if (!loc.TryGetLocationEvents(out _, out events) || events is null || events.Count == 0)
                    continue;
            }
            catch
            {
                continue;
            }

            bool isModLoc = loc.Name.StartsWith("Custom_", StringComparison.OrdinalIgnoreCase);
            string locDisplay = TranslateEventLocationName(loc);

            foreach (var kv in events)
            {
                string key = kv.Key;
                string script = kv.Value;
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(script) || script.Equals("null", StringComparison.OrdinalIgnoreCase))
                    continue;

                string[] parts = key.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0)
                    continue;

                string eventId = parts[0].Trim();
                if (eventId.Equals("none", StringComparison.OrdinalIgnoreCase)
                    || eventId.Equals("null", StringComparison.OrdinalIgnoreCase)
                    || eventId.Equals("default", StringComparison.OrdinalIgnoreCase)
                    || eventId.Equals("message", StringComparison.OrdinalIgnoreCase)
                    || eventId.StartsWith("fork", StringComparison.OrdinalIgnoreCase)
                    || eventId.StartsWith("response", StringComparison.OrdinalIgnoreCase)
                    || eventId.StartsWith("choose", StringComparison.OrdinalIgnoreCase)
                    || eventId.StartsWith("question", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (player.eventsSeen.Contains(eventId) || !seenEventIds.Add(eventId))
                    continue;

                EvaluateEvent(
                    loc,
                    locDisplay,
                    isModLoc,
                    eventId,
                    parts,
                    player,
                    isRainingOnFarm,
                    owned,
                    npcNames,
                    ready,
                    upcoming);
            }
        }

        var sortedReady = RefreshReadyEventStatuses(ready, isRainingOnFarm)
            .Take(25)
            .ToList();

        var sortedUpcoming = upcoming
            .OrderBy(e => e.HeartGap)
            .ThenByDescending(e => e.MaxRequiredHearts)
            .ThenBy(e => e.NpcSummary, StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();

        _eventCache = new CachedEventScan(cacheKey, ready, sortedUpcoming);
        return (sortedReady, sortedUpcoming);
    }

    private static List<WorldEventEntry> RefreshReadyEventStatuses(List<WorldEventEntry> rawReady, bool isRainingOnFarm)
    {
        return rawReady
            .Select(e =>
            {
                bool weatherOk = e.WeatherReq is null
                    || (e.WeatherReq.Equals("sunny", StringComparison.OrdinalIgnoreCase) && !isRainingOnFarm)
                    || (e.WeatherReq.Equals("rainy", StringComparison.OrdinalIgnoreCase) && isRainingOnFarm);

                string status;
                if (!weatherOk)
                {
                    status = e.WeatherReq!.Equals("rainy", StringComparison.OrdinalIgnoreCase)
                        ? "YAĞMURLU GÜN BEKLİYOR"
                        : "GÜNEŞLİ GÜN BEKLİYOR";
                }
                else if (!e.HasTimeWindow || (Game1.timeOfDay >= e.StartTime && Game1.timeOfDay < e.EndTime))
                {
                    status = "ŞU AN TETİKLENEBİLİR";
                }
                else if (Game1.timeOfDay < e.StartTime)
                {
                    status = "BUGÜN DAHA SONRA";
                }
                else
                {
                    status = "YARIN TETİKLENEBİLİR";
                }

                return e.LiveStatus == status ? e : e with { LiveStatus = status };
            })
            .OrderBy(e => e.LiveStatus switch
            {
                "ŞU AN TETİKLENEBİLİR" => 0,
                "BUGÜN DAHA SONRA" => 1,
                _ => 2
            })
            .ThenByDescending(e => e.MaxRequiredHearts)
            .ThenBy(e => e.LocationDisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void EvaluateEvent(
        GameLocation loc,
        string locDisplay,
        bool isModLoc,
        string eventId,
        string[] keyParts,
        Farmer player,
        bool isRainingOnFarm,
        ModEntry.OwnedInventoryIndex owned,
        Dictionary<string, string> npcNames,
        List<WorldEventEntry> ready,
        List<WorldEventEntry> upcoming)
    {
        var npcs = new List<string>();
        int maxReqHearts = 0;
        int maxHeartGap = 0;
        int startTime = 600;
        int endTime = 2600;
        bool hasTimeWindow = false;
        string? weatherReq = null;
        var extraNotes = new List<string>();
        bool isModEvent = isModLoc || !int.TryParse(eventId, out _);

        for (int i = 1; i < keyParts.Length; i++)
        {
            string rawCond = keyParts[i].Trim();
            if (string.IsNullOrEmpty(rawCond))
                continue;

            string[] tokens = rawCond.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
                continue;

            string cmd = tokens[0];

            // Skip silent mail-trigger pseudo-events (mutating in vanilla)
            if (cmd == "x")
                return;

            // Friendship requirement: f <npc> <points> [npc2 points2 ...]
            if (cmd == "f" && tokens.Length >= 3)
            {
                for (int t = 1; t + 1 < tokens.Length; t += 2)
                {
                    string npcKey = tokens[t];
                    if (!int.TryParse(tokens[t + 1], out int reqPoints))
                        continue;

                    int reqHearts = Math.Max(1, reqPoints / 250);
                    int curPoints = player.friendshipData.TryGetValue(npcKey, out var fData) ? fData.Points : 0;
                    string dn = npcNames.GetValueOrDefault(npcKey, npcKey);
                    npcs.Add($"{dn} ({reqHearts}♥)");
                    if (reqHearts > maxReqHearts)
                        maxReqHearts = reqHearts;

                    if (curPoints < reqPoints)
                    {
                        int gapPts = reqPoints - curPoints;
                        if (gapPts > 500) // More than 2 hearts away -> skip
                            return;
                        int gapHearts = (int)Math.Ceiling(gapPts / 250.0);
                        if (gapHearts > maxHeartGap)
                            maxHeartGap = gapHearts;
                    }
                }
                continue;
            }

            // Time window: t <start> <end>
            if (cmd == "t" && tokens.Length >= 3 && int.TryParse(tokens[1], out int sT) && int.TryParse(tokens[2], out int eT))
            {
                startTime = sT;
                endTime = eT;
                hasTimeWindow = true;
                continue;
            }

            // Weather: w <sunny|rainy>
            if (cmd == "w" && tokens.Length >= 2)
            {
                weatherReq = tokens[1];
                continue;
            }

            // NPC present in room: p <npc>
            if (cmd == "p" && tokens.Length >= 2)
            {
                string dn = npcNames.GetValueOrDefault(tokens[1], tokens[1]);
                extraNotes.Add($"{dn} içerideyken");
                continue;
            }

            // Carrying item: i <itemId>
            if (cmd == "i" && tokens.Length >= 2)
            {
                string qid = FishGuideBuilder.NormalizeQid(tokens[1]);
                string itemName = ItemRegistry.GetData(qid)?.DisplayName ?? tokens[1];
                var (cnt, _) = owned.FindMatches(qid);
                if (cnt <= 0)
                    return; // Don't spam events requiring unowned items
                extraNotes.Add($"Çantanda {itemName} olsun");
                continue;
            }

            // Ignore tile step, random chance, and free-slot checks when previewing
            if (cmd is "a" or "r" or "c")
            {
                if (cmd == "r")
                    extraNotes.Add("Şansa bağlı");
                continue;
            }

            // Evaluate every other condition (season, day, mail, event chains, year, GSQ, modded tokens) safely via Stardew 1.6 API!
            try
            {
                if (!Event.CheckPrecondition(loc, eventId, rawCond))
                    return;
            }
            catch
            {
                return;
            }
        }

        // Only surface meaningful events (either involves an NPC heart threshold, or is a location story event in a visited/accessible hub)
        if (npcs.Count == 0 && !player.locationsVisited.Contains(loc.Name) && loc is not Farm)
            return;

        string timeText = hasTimeWindow
            ? $"{FormatClock(startTime)}–{FormatClock(endTime)}"
            : "Tüm gün";

        string weatherText = weatherReq?.ToLowerInvariant() switch
        {
            "sunny" => "Güneşli",
            "rainy" => "Yağmurlu",
            _ => "Her hava"
        };

        string npcSummary = npcs.Count > 0 ? string.Join(", ", npcs.Distinct()) : "Hikâye / Bölge Etkinliği";
        string extra = extraNotes.Count > 0 ? string.Join(", ", extraNotes.Distinct()) : string.Empty;

        if (maxHeartGap > 0)
        {
            upcoming.Add(new WorldEventEntry(
                eventId,
                loc.Name,
                locDisplay,
                npcSummary,
                maxReqHearts,
                maxHeartGap,
                timeText,
                weatherText,
                extra,
                $"{maxHeartGap} kalp kaldı",
                isModEvent));
            return;
        }

        bool weatherOk = weatherReq is null
            || (weatherReq.Equals("sunny", StringComparison.OrdinalIgnoreCase) && !isRainingOnFarm)
            || (weatherReq.Equals("rainy", StringComparison.OrdinalIgnoreCase) && isRainingOnFarm);

        string status;
        if (!weatherOk)
        {
            status = weatherReq!.Equals("rainy", StringComparison.OrdinalIgnoreCase)
                ? "YAĞMURLU GÜN BEKLİYOR"
                : "GÜNEŞLİ GÜN BEKLİYOR";
        }
        else if (!hasTimeWindow || (Game1.timeOfDay >= startTime && Game1.timeOfDay < endTime))
        {
            status = "ŞU AN TETİKLENEBİLİR";
        }
        else if (Game1.timeOfDay < startTime)
        {
            status = "BUGÜN DAHA SONRA";
        }
        else
        {
            status = "YARIN TETİKLENEBİLİR";
        }

        ready.Add(new WorldEventEntry(
            eventId,
            loc.Name,
            locDisplay,
            npcSummary,
            maxReqHearts,
            0,
            timeText,
            weatherText,
            extra,
            status,
            isModEvent,
            startTime,
            endTime,
            hasTimeWindow,
            weatherReq));
    }

    private static Dictionary<string, NPC> BuildLiveNpcLookup()
    {
        var map = new Dictionary<string, NPC>(StringComparer.OrdinalIgnoreCase);
        try
        {
            Utility.ForEachCharacter(npc =>
            {
                if (npc is not null && npc.IsVillager && !string.IsNullOrEmpty(npc.Name))
                    map.TryAdd(npc.Name, npc);
                return true;
            });
        }
        catch
        {
            // Fallback to scanning locations
            foreach (var loc in Game1.locations)
            {
                if (loc is null) continue;
                foreach (var npc in loc.characters)
                {
                    if (npc is not null && npc.IsVillager && !string.IsNullOrEmpty(npc.Name))
                        map.TryAdd(npc.Name, npc);
                }
            }
        }
        return map;
    }

    private static List<(string Qid, Item SampleItem, string DisplayName, string SourceHint)> BuildUniqueStorageItems(
        List<ItemEntry> inventory,
        List<ChestEntry> chests)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<(string Qid, Item SampleItem, string DisplayName, string SourceHint)>();

        void TryAdd(ItemEntry entry, string source)
        {
            if (string.IsNullOrWhiteSpace(entry.QualifiedId))
                return;
            string qid = FishGuideBuilder.NormalizeQid(entry.QualifiedId);
            if (!qid.StartsWith("(O)", StringComparison.OrdinalIgnoreCase))
                return;
            if (!seen.Add(qid))
                return;

            try
            {
                if (!_giftSampleCache.TryGetValue(qid, out StardewValley.Object? obj))
                {
                    Item? sample = ItemRegistry.Create(qid, allowNull: true);
                    obj = sample is StardewValley.Object created && created.canBeGivenAsGift() ? created : null;
                    _giftSampleCache[qid] = obj;
                }

                if (obj is not null)
                    list.Add((qid, obj, entry.DisplayName, source));
            }
            catch
            {
                // Ignore invalid mod items
            }
        }

        foreach (var item in inventory)
            TryAdd(item, "Envanter");

        foreach (var chest in chests)
        {
            string label = !string.IsNullOrWhiteSpace(chest.CustomName)
                ? chest.CustomName
                : ModEntry.TranslateRegionNameTr(chest.Location);
            foreach (var item in chest.Items)
                TryAdd(item, label);
        }

        return list;
    }

    private static (List<string> Loved, List<string> Liked) FindOwnedGiftsForNpc(
        NPC? npc,
        List<(string Qid, Item SampleItem, string DisplayName, string SourceHint)> storageItems,
        int maxLoved = 4,
        int maxLiked = 3)
    {
        var loved = new List<string>();
        var liked = new List<string>();
        if (npc is null)
            return (loved, liked);

        string dayKey = $"{StardewModdingAPI.Constants.SaveFolderName}|{Game1.Date.TotalDays}";
        if (!string.Equals(_giftTasteCacheDayKey, dayKey, StringComparison.Ordinal))
        {
            _giftTasteCache.Clear();
            _giftTasteCacheDayKey = dayKey;
        }

        foreach (var entry in storageItems)
        {
            try
            {
                var tasteKey = (npc.Name, entry.Qid);
                if (!_giftTasteCache.TryGetValue(tasteKey, out int taste))
                {
                    taste = npc.getGiftTasteForThisItem(entry.SampleItem);
                    _giftTasteCache[tasteKey] = taste;
                }

                if (taste == NPC.gift_taste_love && loved.Count < maxLoved)
                {
                    loved.Add($"{entry.DisplayName} [{entry.SourceHint}]");
                }
                else if (taste == NPC.gift_taste_like && liked.Count < maxLiked && loved.Count == 0)
                {
                    liked.Add($"{entry.DisplayName} [{entry.SourceHint}]");
                }

                if (loved.Count >= maxLoved)
                    break;
            }
            catch
            {
                // Ignore gift taste errors on custom items
            }
        }

        return (loved, liked);
    }

    private static List<BirthdayGiftMatch> CollectBirthdayGiftMatches(
        Farmer player,
        Dictionary<string, NPC> npcIndex,
        List<(string Qid, Item SampleItem, string DisplayName, string SourceHint)> storageItems,
        ModEntry.OwnedInventoryIndex owned)
    {
        var results = new List<BirthdayGiftMatch>();
        if (Game1.characterData is null)
            return results;

        int currentTotal = (int)Game1.season * 28 + Game1.dayOfMonth;
        foreach (var kv in Game1.characterData)
        {
            var cd = kv.Value;
            if (cd?.BirthSeason is null || cd.BirthDay <= 0)
                continue;

            int bTotal = (int)cd.BirthSeason.Value * 28 + cd.BirthDay;
            int diff = (bTotal - currentTotal + 112) % 112;
            if (diff > 7)
                continue;

            npcIndex.TryGetValue(kv.Key, out NPC? npc);
            string displayName = npc?.displayName
                ?? (!string.IsNullOrWhiteSpace(cd.DisplayName) ? ModEntry.ResolveLocalizedText(cd.DisplayName) : kv.Key);

            int hearts = 0;
            bool giftedToday = false;
            if (player.friendshipData.TryGetValue(kv.Key, out var f))
            {
                hearts = f.Points / 250;
                giftedToday = f.GiftsToday > 0;
            }

            var (loved, liked) = FindOwnedGiftsForNpc(npc, storageItems, maxLoved: 4, maxLiked: 2);
            string curLoc = npc?.currentLocation is not null ? TranslateEventLocationName(npc.currentLocation) : string.Empty;

            results.Add(new BirthdayGiftMatch(
                kv.Key,
                displayName,
                diff,
                hearts,
                giftedToday,
                loved,
                liked,
                curLoc));
        }

        return results
            .OrderBy(x => x.DaysUntil)
            .ThenByDescending(x => x.OwnedLovedGifts.Count)
            .ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<NpcMilestoneEntry> CollectNpcMilestones(
        Farmer player,
        Dictionary<string, NPC> npcIndex,
        List<(string Qid, Item SampleItem, string DisplayName, string SourceHint)> storageItems,
        ModEntry.OwnedInventoryIndex owned,
        List<WorldEventEntry> upcomingEvents)
    {
        // Aggregate all unlearned NPC-gated cooking & crafting recipes + manual high-impact milestones
        var npcRewards = new Dictionary<string, List<(int Hearts, string Reward)>>(StringComparer.OrdinalIgnoreCase);

        void AddReward(string npcKey, int reqHearts, string reward)
        {
            if (string.IsNullOrWhiteSpace(npcKey) || reqHearts <= 0)
                return;
            if (!npcRewards.TryGetValue(npcKey, out var list))
            {
                list = new List<(int, string)>();
                npcRewards[npcKey] = list;
            }
            list.Add((reqHearts, reward));
        }

        // 1. Cooking recipes unlocked by friendship ("f <NPC> <hearts>")
        try
        {
            foreach (var kv in DataLoader.CookingRecipes(Game1.content))
            {
                if (player.cookingRecipes.ContainsKey(kv.Key))
                    continue;
                string[] parts = kv.Value.Split('/');
                if (parts.Length < 4)
                    continue;
                string[] cond = parts[3].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (cond.Length >= 3 && cond[0] == "f" && int.TryParse(cond[2], out int h))
                {
                    string display = parts.Length > 4 && !string.IsNullOrWhiteSpace(parts[4])
                        ? ModEntry.ResolveLocalizedText(parts[4])
                        : kv.Key;
                    AddReward(cond[1], h, $"Yemek Tarifi: {display}");
                }
            }
        }
        catch { }

        // 2. Crafting recipes unlocked by friendship ("f <NPC> <hearts>")
        try
        {
            foreach (var kv in DataLoader.CraftingRecipes(Game1.content))
            {
                if (player.craftingRecipes.ContainsKey(kv.Key))
                    continue;
                string[] parts = kv.Value.Split('/');
                if (parts.Length < 5)
                    continue;
                string[] cond = parts[4].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (cond.Length >= 3 && cond[0] == "f" && int.TryParse(cond[2], out int h))
                {
                    string display = parts.Length > 5 && !string.IsNullOrWhiteSpace(parts[5])
                        ? ModEntry.ResolveLocalizedText(parts[5])
                        : kv.Key;
                    AddReward(cond[1], h, $"Üretim Tarifi: {display}");
                }
            }
        }
        catch { }

        // 3. Key progression/mechanic NPCs
        foreach (var kv in KeyNpcManualMilestones)
            AddReward(kv.Key, kv.Value.Hearts, kv.Value.Reward);

        var candidates = new List<NpcMilestoneEntry>();
        foreach (var kv in npcRewards)
        {
            string npcKey = kv.Key;
            if (!npcIndex.TryGetValue(npcKey, out NPC? npc))
                continue; // Only NPCs actually loaded in this game/modpack

            int curPoints = player.friendshipData.TryGetValue(npcKey, out var f) ? f.Points : 0;
            int curHearts = curPoints / 250;

            var nextRewards = kv.Value
                .Where(r => r.Hearts > curHearts && r.Hearts <= curHearts + 3)
                .OrderBy(r => r.Hearts)
                .ToList();
            if (nextRewards.Count == 0)
                continue;

            int nextTarget = nextRewards[0].Hearts;
            int pointsNeeded = Math.Max(0, nextTarget * 250 - curPoints);
            var unlockLabels = nextRewards
                .Where(r => r.Hearts == nextTarget)
                .Select(r => r.Reward)
                .Distinct()
                .Take(3)
                .ToList();

            var (loved, liked) = FindOwnedGiftsForNpc(npc, storageItems, maxLoved: 3, maxLiked: 2);
            string curLoc = npc.currentLocation is not null ? TranslateEventLocationName(npc.currentLocation) : string.Empty;

            candidates.Add(new NpcMilestoneEntry(
                npcKey,
                npc.displayName,
                curHearts,
                nextTarget,
                pointsNeeded,
                unlockLabels,
                loved,
                liked,
                curLoc));
        }

        return candidates
            .OrderByDescending(c => c.Unlocks.Any(u => u.Contains("Çay Fidanı") || u.Contains("Vahşi Yem") || u.Contains("Üretim Tarifi") || u.Contains("SVE") || u.Contains("Ridgeside") || u.Contains("Kılıç")))
            .ThenBy(c => c.PointsNeeded)
            .ThenByDescending(c => c.OwnedLovedGifts.Count)
            .Take(14)
            .ToList();
    }

    private static List<BookStockEntry> CollectBooksellerStock(Farmer player)
    {
        var list = new List<BookStockEntry>();
        try
        {
            var stock = ShopBuilder.GetShopStock("Bookseller");
            foreach (var pair in stock)
            {
                if (pair.Key is null || pair.Value.Price <= 0)
                    continue;

                string id = (pair.Key as Item)?.ItemId ?? FishGuideBuilder.StripObjectPrefix(pair.Key.QualifiedItemId ?? string.Empty);
                bool read = !string.IsNullOrEmpty(id)
                    && (player.stats.Get(id) > 0
                        || player.stats.Get($"Book_{id}") > 0
                        || player.mailReceived.Contains($"read_{id}")
                        || player.mailReceived.Contains(id));

                list.Add(new BookStockEntry(pair.Key.DisplayName, pair.Value.Price, read));
            }
        }
        catch { }

        return list.OrderBy(b => b.AlreadyRead).ThenBy(b => b.Price).ToList();
    }

    private static List<StardropStatusEntry> CollectStardrops(Farmer player, bool hasSve)
    {
        var list = new List<StardropStatusEntry>();
        foreach (var (flag, name, how) in VanillaStardrops)
        {
            bool obtained = player.mailReceived.Contains(flag);
            string resolvedHow = flag == "CF_Sewer" && hasSve
                ? "SVE'de Paslı Anahtar'ı Marlon verir (Yıl 1'de Marlon 5♥ veya Yıl 2 + Maden 120 → Maceracı Zirvesi), ardından Krobus'tan 20.000g'ye alınır"
                : how;
            list.Add(new StardropStatusEntry(name, obtained, resolvedHow));
        }
        return list;
    }

    private static List<string> CollectUnbuiltKeyBuildings(Farm farm, Farmer player, bool hasTractor, int totalPlantedCrops)
    {
        var ownedTypes = new HashSet<string>(
            farm.buildings.Select(b => b.buildingType.Value ?? string.Empty),
            StringComparer.OrdinalIgnoreCase);

        var list = new List<string>();

        if (!ownedTypes.Contains("Stable"))
            list.Add("At Ahırı (10.000g + 100 Kereste + 5 Demir Külçesi — At ile büyük modlu haritalarda hızın ciddi artar!)");

        if (!ownedTypes.Contains("Fish Pond"))
            list.Add("Balık Havuzu (5.000g + 200 Taş + 5 Su Yosunu + 5 Yeşil Su Yosunu — Mersinbalığı/Havyar, Lav Yılanbalığı veya özel mod balıkları için pasif gelir)");

        if (!ownedTypes.Contains("Shed") && !ownedTypes.Contains("Big Shed"))
            list.Add("Baraka (15.000g + 300 Odun — Fıçı, Kavanoz ve makineleri düzenli/otomatik toplamak için ideal)");

        if (hasTractor && !ownedTypes.Any(t => t.Contains("Tractor", StringComparison.OrdinalIgnoreCase)) && (totalPlantedCrops >= 80 || player.Money >= 100000))
            list.Add("Traktör Garajı [TractorMod] (Robin — büyük tarlada ekim/hasat/kazı hızını katlar)");

        if (!ownedTypes.Contains("Slime Hutch") && player.Money >= 40000)
            list.Add("Balçıkça Kümesi (10.000g + 500 Taş + 10 İşlenmiş Kuvars + 1 İridyum Külçesi — Balçıkça yumurtası düşürmeyi de açar!)");

        bool hasDarkTalisman = player.hasMagicInk || player.mailReceived.Contains("hasMagicInk");
        if (hasDarkTalisman && !ownedTypes.Contains("Junimo Hut"))
            list.Add("Junimo Kulübesi (Büyücü Kulesi — otomatik hasat, BetterJunimos ile ekstra yetenekler)");

        return list;
    }

    private static List<string> CollectUncraftedKnownMachines(Farmer player, List<MachineEntry> placedMachines)
    {
        var placedNames = new HashSet<string>(placedMachines.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
        var important = new (string RecipeKey, string TrName, string Why)[]
        {
            ("Bee House", "Arı Kovanı", "Çiçek yanında pasif bal geliri (Güz'de Peri Gülü Balı 680g+)"),
            ("Fish Smoker", "Balık Tütsüleyici", "Yakalanan değerli balıkları 2x fiyata satar"),
            ("Bait Maker", "Yem Yapıcı", "Hedef balığa özel yem üreterek nadir/yığın balığı tutmayı kolaylaştırır"),
            ("Oil Maker", "Yağ Makinesi", "Yermantarı Yağı (1.065g+) ve Ayçiçeği/Mısır yağı üretir"),
            ("Loom", "Dokuma Tezgâhı", "Yünü Kumaş'a çevirir (Değirmen / Halkevi / satış)"),
            ("Geode Crusher", "Jeot Kırıcı", "Clint'e gitmeden çiftlikte Jeot kırar"),
            ("Recycling Machine", "Geri Dönüşüm Makinesi", "Yengeç Çömleği çöplerini İşlenmiş Kuvars/Kömür/Kumaş'a çevirir"),
            ("Solar Panel", "Güneş Paneli", "Fırtına beklemeden 7 güneşli günde 1 Pil Paketi üretir"),
            ("Heavy Furnace", "Ağır Fırın", "Tek seferde 25 cevher + 3 kömürle toplu külçe eritir"),
            ("Deluxe Worm Bin", "Lüks Solucan Kutusu", "Her gün Lüks Yem üretir"),
            ("Mini-Obelisk", "Mini Dikilitaş", "Çiftliğin iki ucu arasında anında ışınlanma")
        };

        var result = new List<string>();
        foreach (var (key, tr, why) in important)
        {
            if (player.craftingRecipes.ContainsKey(key) && !placedNames.Contains(key))
                result.Add($"{tr}: {why}");
        }

        // Also detect modded craftable machines from Data/Machines that player knows how to craft
        try
        {
            var machineData = DataLoader.Machines(Game1.content);
            var crafting = DataLoader.CraftingRecipes(Game1.content);
            foreach (var kv in crafting)
            {
                if (!player.craftingRecipes.ContainsKey(kv.Key))
                    continue;
                if (important.Any(x => x.RecipeKey.Equals(kv.Key, StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (placedNames.Contains(kv.Key))
                    continue;

                string[] parts = kv.Value.Split('/');
                if (parts.Length < 3)
                    continue;
                string outId = parts[2].Split(' ')[0];
                string qid = outId.StartsWith("(") ? outId : "(BC)" + outId;
                if (machineData.ContainsKey(qid) && (qid.Contains("Cornucopia", StringComparison.OrdinalIgnoreCase) || qid.Contains("RSV", StringComparison.OrdinalIgnoreCase) || qid.Contains("SVE", StringComparison.OrdinalIgnoreCase) || qid.Contains("ES", StringComparison.OrdinalIgnoreCase)))
                {
                    string dn = parts.Length > 5 && !string.IsNullOrWhiteSpace(parts[5])
                        ? ModEntry.ResolveLocalizedText(parts[5])
                        : (ItemRegistry.GetData(qid)?.DisplayName ?? kv.Key);
                    result.Add($"{dn} (Mod Makinesi): Tarifi biliniyor ama çiftlikte henüz kurulmamış");
                }
            }
        }
        catch { }

        return result.Take(10).ToList();
    }

    private static List<string> CollectEquipmentAlerts(
        IModRegistry modRegistry,
        Farmer player,
        bool hasWearMoreRings)
    {
        var alerts = new List<string>();

        if (hasWearMoreRings)
        {
            try
            {
                var wmr = modRegistry.GetApi<IWearMoreRingsApi>("bcmpinc.WearMoreRings");
                if (wmr is not null)
                {
                    int totalSlots = wmr.RingSlotCount();
                    int filled = 0;
                    for (int i = 0; i < totalSlots; i++)
                    {
                        if (wmr.GetRing(i) is not null)
                            filled++;
                    }
                    if (filled < totalSlots)
                        alerts.Add($"Yüzük slotları: {filled}/{totalSlots} dolu ({totalSlots - filled} boş slotun var — sandıktaki yüzükleri değerlendirebilirsin).");
                }
            }
            catch
            {
                // Ignore WearMoreRings API error
            }
        }
        else if (player.leftRing.Value is null || player.rightRing.Value is null)
        {
            string emptySide = player.leftRing.Value is null && player.rightRing.Value is null
                ? "İki yüzük slotun da boş"
                : (player.rightRing.Value is null ? "Sağ yüzük slotun boş" : "Sol yüzük slotun boş");
            alerts.Add($"{emptySide}! Sandıklarda kullanılmayan yüzük varsa hemen takabilirsin.");
        }

        return alerts;
    }

    private static RuleRuntimeContext BuildRuleContext(
        IModRegistry modRegistry,
        Farmer player,
        Farm farm,
        bool greenhouseUnlocked,
        bool islandUnlocked,
        bool desertUnlocked,
        bool ccAvailable,
        bool isRainingOnFarm,
        bool isBooksellerToday,
        List<CropEntry> crops,
        List<MachineEntry> machines,
        List<AvailableOrderEntry> availableOrders,
        List<(string Name, int Level)> customSkills,
        ModEntry.OwnedInventoryIndex owned,
        int hoeLevel,
        int geodeCount,
        string geodeSummary,
        int unautomatedCount,
        string unautomatedSummary,
        List<WorldEventEntry> readyEvents)
    {
        var ctx = new RuleRuntimeContext
        {
            ModRegistry = modRegistry,
            Player = player,
            Farm = farm,
            Owned = owned
        };

        int daysLeft = Math.Max(0, 28 - Game1.dayOfMonth);
        int scFloor = Math.Max(0, player.deepestMineLevel - 120);
        int nextElevatorFloor = ((scFloor / 5) + 1) * 5;
        int marlonHearts = player.friendshipData.TryGetValue("MarlonFay", out var mf) ? mf.Points / 250 : 0;
        int copperOre = owned.GetStorageCount("(O)378");
        int ironOre = owned.GetStorageCount("(O)380");
        int goldOre = owned.GetStorageCount("(O)384");
        int iridiumOre = owned.GetStorageCount("(O)386");
        int coal = owned.GetStorageCount("(O)382");
        int kegCount = machines.FirstOrDefault(m => m.Name.Equals("Keg", StringComparison.OrdinalIgnoreCase))?.Total ?? 0;
        int tapperCount = machines.Where(m => m.Name.Contains("Tapper", StringComparison.OrdinalIgnoreCase)).Sum(m => m.Total);
        int crystCount = machines.FirstOrDefault(m => m.Name.Equals("Crystalarium", StringComparison.OrdinalIgnoreCase))?.Total ?? 0;
        int skillsNear10 = new[] { player.FarmingLevel, player.MiningLevel, player.ForagingLevel, player.FishingLevel }.Count(lv => lv >= 9);

        ctx.Numbers["year"] = Game1.year;
        ctx.Numbers["day"] = Game1.dayOfMonth;
        ctx.Numbers["daysLeftInSeason"] = daysLeft;
        ctx.Numbers["timeOfDay"] = Game1.timeOfDay;
        ctx.Numbers["money"] = player.Money;
        ctx.Numbers["totalEarned"] = player.totalMoneyEarned;
        ctx.Numbers["houseLevel"] = player.HouseUpgradeLevel;
        ctx.Numbers["deepestMine"] = player.deepestMineLevel;
        ctx.Numbers["skullCavernFloor"] = scFloor;
        ctx.Numbers["nextElevatorFloor"] = nextElevatorFloor;
        ctx.Numbers["museumCount"] = Game1.netWorldState.Value.MuseumPieces.Length;
        ctx.Numbers["stardropsFound"] = Utility.numStardropsFound(player);
        ctx.Numbers["maxBagSlots"] = player.MaxItems;
        ctx.Numbers["hoeLevel"] = hoeLevel;
        ctx.Numbers["marlonHearts"] = marlonHearts;
        ctx.Numbers["geodeBacklog"] = geodeCount;
        ctx.Numbers["unautomatedCount"] = unautomatedCount;
        ctx.Numbers["plantedCrops"] = crops.Sum(c => c.Count);
        ctx.Numbers["kegCount"] = kegCount;
        ctx.Numbers["tapperCount"] = tapperCount;
        ctx.Numbers["crystalariumCount"] = crystCount;
        ctx.Numbers["coalCount"] = coal;
        ctx.Numbers["totalOres"] = copperOre + ironOre + goldOre + iridiumOre;
        ctx.Numbers["oakResinCount"] = owned.GetStorageCount("(O)725");
        ctx.Numbers["jadeCount"] = owned.GetStorageCount("(O)70");
        ctx.Numbers["rareSeedCount"] = owned.GetStorageCount("(O)347");
        ctx.Numbers["skillsNear10"] = skillsNear10;
        ctx.Numbers["daysUntilFallFair"] = Game1.season == Season.Fall && Game1.dayOfMonth <= 16 ? 16 - Game1.dayOfMonth : -1;
        ctx.Numbers["readyEventsToday"] = readyEvents.Count(e => e.LiveStatus is "ŞU AN TETİKLENEBİLİR" or "BUGÜN DAHA SONRA");

        ctx.Strings["season"] = Game1.currentSeason;
        ctx.Strings["geodeSummary"] = geodeSummary;
        ctx.Strings["unautomatedSummary"] = unautomatedSummary;

        if (isRainingOnFarm) ctx.Flags.Add("isRaining");
        if (isBooksellerToday) ctx.Flags.Add("isBookseller");
        if (greenhouseUnlocked) ctx.Flags.Add("greenhouseUnlocked");
        if (islandUnlocked) ctx.Flags.Add("islandUnlocked");
        if (desertUnlocked) ctx.Flags.Add("desertUnlocked");
        if (ccAvailable) ctx.Flags.Add("ccAvailable");
        if (player.mailReceived.Contains("ccIsComplete")) ctx.Flags.Add("ccCompletedAll");
        if (player.hasRustyKey) ctx.Flags.Add("hasRustyKey");
        if (player.hasSkullKey) ctx.Flags.Add("hasSkullKey");
        if (player.hasClubCard) ctx.Flags.Add("hasClubCard");
        if (player.hasSpecialCharm) ctx.Flags.Add("hasSpecialCharm");
        if (player.hasMagnifyingGlass) ctx.Flags.Add("hasMagnifyingGlass");
        if (player.hasMagicInk) ctx.Flags.Add("hasMagicInk");

        foreach (Building b in farm.buildings)
        {
            if (!string.IsNullOrWhiteSpace(b.buildingType.Value))
                ctx.FarmBuildings.Add(b.buildingType.Value);
        }
        foreach (var m in machines.Where(m => m.Total > 0))
            ctx.PlacedMachines.Add(m.Name);
        foreach (var cs in customSkills)
            ctx.CustomSkills[cs.Name] = cs.Level;

        // Precompute dynamic mod ecosystem summaries for JSON templates
        string[] sveHubs = { "Custom_AuroraVineyard", "Custom_ShearwaterBridge", "Custom_SpriteSpring2", "Custom_JunimoWoods", "Custom_FableReef", "Custom_Highlands", "Custom_CrimsonBadlands", "Custom_FerngillRepublicFrontier" };
        int sveVisited = sveHubs.Count(h => player.locationsVisited.Contains(h));
        var sveUnvisited = sveHubs
            .Where(h => Game1.getLocationFromName(h) is not null && !player.locationsVisited.Contains(h))
            .Select(TranslateRawLocationKey)
            .Take(4)
            .ToList();
        ctx.Numbers["sveVisitedCount"] = sveVisited;
        ctx.Numbers["sveHubCount"] = sveHubs.Length;
        ctx.Strings["sveTip"] = sveUnvisited.Count > 0
            ? $"Henüz açılmamış/gidilmemiş bölgeler: {string.Join(", ", sveUnvisited)}. Büyücü, Marlon, Sophia ve Andy kalpleri yeni SVE bölgelerini ve Büyükbaba'nın Kulübesi / Aurora Bağı hikâyelerini açar."
            : "Ana SVE bölgeleri keşfedilmiş; ileri düzey Highlands/Crimson Badlands/Lance görevlerine odaklanabilirsin.";

        bool rsvTown = player.locationsVisited.Contains("Custom_Ridgeside_RidgesideVillage");
        bool rsvForest = player.locationsVisited.Contains("Custom_Ridgeside_RidgeForest") || player.locationsVisited.Contains("Custom_Ridgeside_Ridge");
        int rsvOrders = availableOrders.Count(o => o.BoardLabel.Contains("Ridgeside", StringComparison.OrdinalIgnoreCase));
        ctx.Strings["rsvProgress"] = $"Köy: {(rsvTown ? "Ziyaret edildi" : "Gidilmedi")} | Sırt/Orman: {(rsvForest ? "Açık" : "Henüz gidilmedi")}"
            + (rsvOrders > 0 ? $" | Panoda {rsvOrders} açık görev" : "");
        ctx.Strings["rsvTip"] = !rsvTown
            ? "Otobüs Durağı'nın sol üstündeki teleferik ile Ridgeside Köyü'ne çık: Heaps Market, Ian'ın Çiftlik Hizmetleri ve Özel İşler panosu seni bekliyor."
            : !rsvForest
                ? "Köyün yukarısındaki Sırt (Ridge) ve Sırt Ormanı'na çıkıp Ninja Evi + Ruhlar Alemi hikâyesini başlatabilir, köy meydanındaki Özel İşler panosundan yüksek ödüllü görevler alabilirsin."
                : "Ridgeside Özel İşler & Ninja panosundaki görevleri, Ian'ın sulama/çit hizmetlerini ve Ruhlar Alemi kalıntılarını takip et.";

        bool esTown = player.locationsVisited.Contains("Custom_EastScarp_Village");
        bool esDark = player.locationsVisited.Contains("Custom_EastScarp_DeepDark");
        string skillStr = customSkills.Count > 0
            ? " | Özel Beceriler: " + string.Join(", ", customSkills.Select(s => $"{s.Name} Sv.{s.Level}"))
            : string.Empty;
        ctx.Strings["esProgress"] = $"Doğu Uçurumu: {(esTown ? "Ziyaret edildi" : "Gidilmedi")} | Derin Karanlık: {(esDark ? "Açık" : "Gidilmedi")}{skillStr}";

        GameLocation? solarium = Game1.locations.FirstOrDefault(l => l is not null && l.Name.Contains("Solarium", StringComparison.OrdinalIgnoreCase));
        bool solVisited = solarium is not null && player.locationsVisited.Contains(solarium.Name);
        int solPlanted = solarium is not null ? solarium.terrainFeatures.Values.Count(tf => tf is StardewValley.TerrainFeatures.HoeDirt hd && hd.crop is not null) : 0;
        ctx.Strings["solariumProgress"] = solarium is null ? "Yüklü (kaplıca bölgesinde)" : $"{(solVisited ? "Ziyaret edildi" : "Henüz gidilmedi")} | Ekili ürün: {solPlanted}";
        ctx.Strings["solariumTip"] = solPlanted == 0
            ? "Kaplıca'daki Solaryum sera gibi kapalı ekim/ağaç alanı sunuyor — Kış gelmeden kontrol edip değerlendirebilirsin!"
            : $"Solaryum aktif kullanılıyor ({solPlanted} ekili ürün).";

        bool caveMailOpen = player.mailReceived.Contains("Tikamin557.CP.AdditionalFarmCave_CaveOpenMail");
        GameLocation? extraCave = Game1.locations.FirstOrDefault(l => l is not null && l.Name.Contains("AdditionalFarmCave", StringComparison.OrdinalIgnoreCase));
        bool caveVisited = extraCave is not null && player.locationsVisited.Contains(extraCave.Name);
        int robinPts = player.friendshipData.TryGetValue("Robin", out var rf) ? rf.Points : 0;
        int demPts = player.friendshipData.TryGetValue("Demetrius", out var df) ? df.Points : 0;
        ctx.Strings["addCaveProgress"] = caveVisited
            ? "Açık ve ziyaret edildi"
            : caveMailOpen
                ? "Açıldı — henüz girilmedi (Çiftlik Mağarası arka üst duvarı)"
                : $"Henüz kapalı (Robin {robinPts / 250}/3♥, Demetrius {demPts / 250}/2♥)";
        ctx.Strings["addCaveTip"] = caveVisited
            ? "Ek Çiftlik Mağarası keşfedildi; içindeki kaynakları ve 2. alan kilidini takip edebilirsin."
            : caveMailOpen
                ? "Robin tahtaları söktü! Çiftlik Mağarası'nın arka üst duvarındaki (Mantar Kutularının arkası) yeni kapıdan içeri girebilirsin."
                : (robinPts >= 750 && demPts >= 500
                    ? "Robin 3♥ ve Demetrius 2♥ şartı tamam — ertesi sabah posta kutuna Robin'den açılış mektubu gelince Çiftlik Mağarası'nın arka üst duvarında yeni geçit açılacak."
                    : "Açılması için Robin 3♥ + Demetrius 2♥ + Mağara seçimi gerekir; şart tamamlanınca Çiftlik Mağarası'nın arka duvarında yeni oda açılır.");

        var dhLocs = Game1.locations.Where(l => l is not null && (l.Name.Contains("Downhill", StringComparison.OrdinalIgnoreCase) || l.Name.Contains("Kiyuga", StringComparison.OrdinalIgnoreCase))).ToList();
        int dhVisited = dhLocs.Count(l => player.locationsVisited.Contains(l.Name));
        ctx.Strings["downhillProgress"] = dhLocs.Count > 0 ? $"Yokuş Aşağı bölgesi: {dhVisited}/{dhLocs.Count} alan keşfedildi" : "Özel Açılabilir Yığınlar aktif";

        ctx.Strings["automateProgress"] = unautomatedCount > 0
            ? $"Sandığa bağlı olmayan {unautomatedCount} makine tespit edildi ({unautomatedSummary})"
            : "Tüm ana makinelerin sandıklara bağlı görünüyor!";
        ctx.Strings["automateTip"] = unautomatedCount > 0
            ? "Otomasyon (Automate) modu yüklü: Fıçı, Fırın, Kavanoz, Peynir/Mayonez, Mantar Kutusu ve Yengeç Çömleklerinin yanına 1 sandık koyarsan otomatik doldurup boşaltır!"
            : "Otomasyon düzenin harika çalışıyor; yeni makine grubu ekledikçe tek sandığa bağlamaya devam et.";

        return ctx;
    }

    private static void AppendDynamicGates(
        List<ProgressionGateEntry> gates,
        List<StardropStatusEntry> stardrops,
        List<AvailableOrderEntry> availableOrders,
        List<WorldEventEntry> readyEvents)
    {
        int foundStardrops = stardrops.Count(s => s.Obtained);
        var nextStardrops = stardrops.Where(s => !s.Obtained).Take(3).Select(s => s.Name).ToList();
        if (nextStardrops.Count > 0)
        {
            gates.Add(new ProgressionGateEntry(
                "Kalıcı Enerji (+34)",
                "Yıldıztohumları (Stardrops)",
                $"{foundStardrops}/{stardrops.Count} alındı",
                $"Sıradaki en ulaşılabilirler: {string.Join(", ", nextStardrops)}."));
        }

        int readyNow = readyEvents.Count(e => e.LiveStatus is "ŞU AN TETİKLENEBİLİR" or "BUGÜN DAHA SONRA");
        if (readyNow > 0)
        {
            gates.Add(new ProgressionGateEntry(
                "Hikâye & Karakterler",
                "Tetiklenmeye Hazır Ara Sahneler",
                $"{readyNow} etkinlik bugün izlenebilir ({readyEvents.Count} toplam hazır)",
                "Ayrıntılı yer ve saatler için aşağıdaki 'Tetiklenmeye Hazır Etkinlikler' listesine bak."));
        }

        int untakenOpen = availableOrders.Count(o => !o.BoardAlreadyAcceptedToday);
        if (untakenOpen > 0)
        {
            gates.Add(new ProgressionGateEntry(
                "Görev Panoları",
                "Alınmamış Özel Siparişler",
                $"{untakenOpen} açık pano görevi bekliyor",
                "Kasaba / Ridgeside özel sipariş panolarındaki haftalık büyük ödüllü görevleri kontrol et (özel tarifler ve makineler kazandırır)."));
        }
    }

    internal static string TranslateEventLocationName(GameLocation loc)
    {
        if (loc is null)
            return string.Empty;
        string display = loc.DisplayName;
        if (!string.IsNullOrWhiteSpace(display) && !display.Equals(loc.Name, StringComparison.OrdinalIgnoreCase))
            return display;
        return TranslateRawLocationKey(loc.Name);
    }

    private static string TranslateRawLocationKey(string name)
    {
        return name switch
        {
            "Farm" => "Çiftlik",
            "FarmHouse" => "Çiftlik Evi",
            "Town" => "Pelikan Kasabası",
            "Forest" => "Kömürözü Ormanı",
            "Woods" => "Gizli Orman",
            "Mountain" => "Dağ",
            "Beach" => "Kumsal",
            "BusStop" => "Otobüs Durağı",
            "Backwoods" => "Arka Orman",
            "Railroad" => "Demiryolu",
            "SeedShop" => "Pierre'in Dükkânı",
            "Saloon" => "Yıldızkaydı Salonu",
            "ScienceHouse" => "Robin'in Evi / Marangoz",
            "Blacksmith" => "Demirci (Clint)",
            "AnimalShop" => "Marnie'nin Çiftliği",
            "WizardHouse" => "Büyücü Kulesi",
            "Hospital" => "Harvey'nin Kliniği",
            "ManorHouse" => "Lewis'in Evi",
            "JoshHouse" => "Evelyn & George'un Evi",
            "HaleyHouse" => "Haley & Emily'nin Evi",
            "SamHouse" => "Jodi & Sam'in Evi",
            "LeahHouse" => "Leah'nın Kulübesi",
            "ElliottHouse" => "Elliott'un Kulübesi",
            "FishShop" => "Willy'nin Balıkçı Dükkânı",
            "ArchaeologyHouse" => "Müze",
            "AdventureGuild" => "Maceracılar Loncası",
            "CommunityCenter" => "Halkevi",
            "Desert" => "Patisko Çölü",
            "Sewer" => "Kanalizasyon",
            "Custom_AuroraVineyard" => "Aurora Bağı (SVE)",
            "Custom_ShearwaterBridge" => "Shearwater Köprüsü (SVE)",
            "Custom_SpriteSpring2" => "Peri Pınarı (SVE)",
            "Custom_JunimoWoods" => "Junimo Korusu (SVE)",
            "Custom_FableReef" => "Masal Resifi (SVE)",
            "Custom_Highlands" => "Yaylalar (SVE)",
            "Custom_CrimsonBadlands" => "Kızıl Çorak Topraklar (SVE)",
            "Custom_FerngillRepublicFrontier" => "Ferngill Cumhuriyeti Sınırı",
            "Custom_Ridgeside_RidgesideVillage" => "Ridgeside Köyü",
            "Custom_Ridgeside_Ridge" => "Ridgeside Sırtı",
            "Custom_Ridgeside_RidgeForest" => "Ridgeside Sırt Ormanı",
            "Custom_EastScarp_Village" => "Doğu Uçurumu Köyü",
            "Custom_EastScarp_DeepDark" => "Doğu Uçurumu Derin Karanlık",
            _ when name.StartsWith("Custom_Ridgeside_", StringComparison.OrdinalIgnoreCase) => "Ridgeside: " + name["Custom_Ridgeside_".Length..],
            _ when name.StartsWith("Custom_EastScarp_", StringComparison.OrdinalIgnoreCase) => "Doğu Uçurumu: " + name["Custom_EastScarp_".Length..],
            _ when name.StartsWith("Custom_", StringComparison.OrdinalIgnoreCase) => name["Custom_".Length..],
            _ => name
        };
    }

    private static string FormatClock(int time)
    {
        int h = time / 100;
        int m = time % 100;
        return $"{h:D2}:{m:D2}";
    }
}
