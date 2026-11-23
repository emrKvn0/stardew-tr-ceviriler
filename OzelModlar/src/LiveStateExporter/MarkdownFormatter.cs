using System.Text;

namespace LiveStateExporter;

internal sealed record ItemEntry(
    string Name,
    string DisplayName,
    int Stack,
    int Quality,
    int UnitSellPrice = 0,
    int TotalSellPrice = 0,
    string QualifiedId = "");

internal sealed record CropEntry(
    string Location,
    string DisplayName,
    string EnglishName,
    int Count,
    int WateredCount,
    int ReadyToHarvestCount,
    int MinDaysRemaining,
    int MaxDaysRemaining,
    Dictionary<string, int> Fertilizers,
    string HarvestItemId = "");

internal sealed record FruitTreeEntry(
    string Location,
    string Tree,
    int DaysUntilMature,
    int FruitsReady,
    string Where = "",
    bool Reachable = true,
    string? BlockedBy = null,
    string FruitItemId = "",
    bool IsOnFarm = true);

internal sealed record FarmSpotEntry(
    string DisplayName,
    string EnglishName,
    string Where,
    bool Reachable,
    string? BlockedBy);

internal sealed record AnimalEntry(
    string Type,
    string Name,
    int Happiness,
    bool WasFedToday,
    bool WasPetToday,
    bool ProduceWaiting,
    bool IsOutside = false);

internal sealed record JunimoHutEntry(
    bool NoHarvest,
    int RaisinDays,
    List<ItemEntry> OutputItems);

internal sealed record SlimeHutchEntry(
    int SlimeCount,
    int WateredSpots,
    int TotalWaterSpots,
    int SlimeBallsReady);

internal sealed record BuildingEntry(
    string BuildingType,
    int DaysOfConstructionLeft,
    List<AnimalEntry> Animals,
    List<ItemEntry> AutoGrabberItems,
    FishPondEntry? FishPond = null,
    int MaxAnimalCapacity = 0,
    JunimoHutEntry? JunimoHut = null,
    SlimeHutchEntry? SlimeHutch = null,
    int HayInTrough = 0,
    bool AnimalDoorOpen = false);

internal sealed record FishPondEntry(
    string FishType,
    int Occupants,
    int MaxOccupants,
    string? NeededItem,
    int NeededCount,
    string? OutputReady);

internal sealed record MachineEntry(
    string Name,
    int Total,
    int Working,
    int Ready,
    int Idle,
    Dictionary<string, int>? WorkingItems = null,
    Dictionary<string, int>? ReadyItems = null);

internal sealed record ShopItemEntry(string DisplayName, string EnglishName, int Price, int Stock, string QualifiedId = "");

internal sealed record RegionForageEntry(string Region, int ForageCount, int ArtifactSpots);

internal sealed record ChestEntry(string Location, string Tile, List<ItemEntry> Items, string CustomName = "");

internal sealed record BundleMissingSlot(
    string ItemId,
    string DisplayName,
    int Quantity,
    int MinQuality);

internal sealed record BundleEntry(
    string Area,
    string BundleName,
    int FilledCount,
    int NeededCount,
    List<string> MissingOptions,
    List<string> MissingItemIds,
    List<BundleMissingSlot>? MissingSlots = null);

internal sealed record QuestEntry(
    string Title,
    string Objective,
    int DaysLeft,
    string? RequiredItemName = null,
    string? RequiredItemId = null);

internal sealed record SpecialOrderEntry(
    string Name,
    int DaysLeft,
    bool IsUntimed,
    List<string> Objectives);

internal sealed record FriendshipEntry(
    string Npc,
    int Hearts,
    int Points,
    string Status,
    bool TalkedToday,
    int GiftsToday,
    int GiftsThisWeek);

internal sealed class LiveStateSnapshot
{
    public string ExportedAt { get; init; } = string.Empty;
    public string Trigger { get; init; } = string.Empty;
    public string SaveFolder { get; init; } = string.Empty;

    // Time
    public int Year { get; init; }
    public string Season { get; init; } = string.Empty;
    public int DayOfMonth { get; init; }
    public int DaysLeftInSeason { get; init; }
    public string DayOfWeek { get; init; } = string.Empty;
    public int TimeOfDay { get; init; }
    public bool IsRainingOnFarm { get; init; }
    public string WeatherForTomorrow { get; init; } = string.Empty;
    public bool IsFestivalToday { get; init; }
    public bool IsPassiveFestivalToday { get; init; }
    public string? FestivalName { get; init; }
    public bool IsBooksellerToday { get; init; }
    public List<int> UpcomingBooksellerDays { get; init; } = new();
    public string? QueenOfSauceNote { get; init; }
    public string? BirthdayToday { get; init; }
    public List<string> UpcomingBirthdays { get; init; } = new();

    // Farmer
    public string FarmerName { get; init; } = string.Empty;
    public string FarmName { get; init; } = string.Empty;
    public int Money { get; init; }
    public uint TotalMoneyEarned { get; init; }
    public int Stamina { get; init; }
    public int MaxStamina { get; init; }
    public int Health { get; init; }
    public int MaxHealth { get; init; }
    public double DailyLuck { get; init; }
    public int LuckLevel { get; init; }
    public int MaxItems { get; init; }
    public int HouseUpgradeLevel { get; init; }
    public int DaysUntilHouseUpgrade { get; init; }
    public int DeepestMineLevel { get; init; }
    public int StardropsFound { get; init; }
    public int MuseumDonatedCount { get; init; }
    public uint MasteryExp { get; init; }
    public int MasteryLevel { get; init; }
    public uint MasteryLevelsSpent { get; init; }
    public List<string> UnlockedMasteries { get; init; } = new();
    public int PowersUnlockedCount { get; init; }
    public int PowersTotalCount { get; init; }
    public List<string> UnlockedBooks { get; init; } = new();
    public int GoldenWalnuts { get; init; }
    public int GoldenWalnutsFound { get; init; }
    public int QiGems { get; init; }
    public string CollectionsSummary { get; init; } = string.Empty;
    public int MissingSeasonalFishTotal { get; init; }
    public List<string> MissingSeasonalFishSample { get; init; } = new();
    public int MonsterGoalsCompleted { get; init; }
    public int MonsterGoalsTotal { get; init; }
    public List<string> MonsterGoalsIncomplete { get; init; } = new();
    public (int Farming, int Mining, int Combat, int Foraging, int Fishing) Skills { get; init; }
    public List<int> Professions { get; init; } = new();
    public string? ToolBeingUpgraded { get; init; }
    public int DaysLeftForToolUpgrade { get; init; }
    public int AxeLevel { get; init; } = -1;
    public int PickaxeLevel { get; init; } = -1;
    public int WateringCanLevel { get; init; } = -1;
    public int HoeLevel { get; init; } = -1;
    public int PanLevel { get; init; } = -1;
    public int TrashCanLevel { get; init; }
    public string? BestRodSummary { get; init; }
    public List<string> ActiveBuffs { get; init; } = new();
    public List<string> Equipped { get; init; } = new();
    public List<string> WalletKeys { get; init; } = new();
    public List<string> UnlockedAreas { get; init; } = new();
    public List<string> MuseumReadyToDonate { get; init; } = new();
    public List<ShopItemEntry> TravelingCart { get; init; } = new();
    public List<string> UnvisitedKeyAreas { get; init; } = new();
    public List<RegionForageEntry> WorldForage { get; init; } = new();

    // Inventory & Shipping
    public List<ItemEntry> Inventory { get; init; } = new();
    public int ShippingEstimatedValue { get; init; }
    public List<ItemEntry> ShippingItems { get; init; } = new();

    // Farm
    public int HayInSilos { get; init; }
    public int HayCapacity { get; init; }
    public List<CropEntry> Crops { get; init; } = new();
    public List<FruitTreeEntry> FruitTrees { get; init; } = new();
    public List<FarmSpotEntry> FarmForageSpots { get; init; } = new();
    public List<BuildingEntry> Buildings { get; init; } = new();
    public List<MachineEntry> Machines { get; init; } = new();
    public Dictionary<string, int> ObjectsSummary { get; init; } = new();
    public List<ChestEntry> Chests { get; init; } = new();
    public List<ItemEntry> FarmCaveReadyItems { get; init; } = new();

    // Categorized Action Sections
    public List<string> MustDoToday { get; init; } = new();
    public List<string> DailyRoutine { get; init; } = new();
    public List<string> StrategicInsights { get; init; } = new();

    // Community Center
    public bool CcAvailable { get; init; }
    public int CcCompletedCount { get; init; }
    public List<BundleEntry> CcIncompleteBundles { get; init; } = new();

    // Quests & Social
    public List<QuestEntry> Quests { get; init; } = new();
    public List<SpecialOrderEntry> SpecialOrders { get; init; } = new();
    public List<FriendshipEntry> Friendships { get; init; } = new();
    public FishGuideSnapshot? FishGuide { get; init; }
}

internal static class MarkdownFormatter
{
    public static Dictionary<string, string> FormatAll(LiveStateSnapshot s)
    {
        return new Dictionary<string, string>
        {
            ["live_summary.md"] = FormatSummary(s),
            ["live_chests.md"] = FormatChests(s),
            ["live_cc.md"] = FormatCommunityCenter(s),
            ["live_social.md"] = FormatSocial(s),
            ["live_fish.md"] = FormatFishGuide(s)
        };
    }

    private static string FormatSummary(LiveStateSnapshot s)
    {
        var sb = new StringBuilder(2048);
        sb.AppendLine($"# Stardew Live Summary ({s.FarmName} - {s.FarmerName})");
        sb.AppendLine($"exportedAt: {s.ExportedAt} | trigger: {s.Trigger} | save: {s.SaveFolder}");
        sb.AppendLine();

        // Time & Weather
        sb.Append($"- **Time:** Y{s.Year} {s.Season} {s.DayOfMonth} ({s.DayOfWeek}, {s.DaysLeftInSeason}d left) {FormatTime(s.TimeOfDay)}");
        sb.Append($" | Rain: {(s.IsRainingOnFarm ? "Yes" : "No")} | Tomorrow: {s.WeatherForTomorrow}");
        if (!string.IsNullOrEmpty(s.FestivalName)) sb.Append($" | Festival: {s.FestivalName}");
        else if (s.IsFestivalToday) sb.Append(" | FestivalToday");
        else if (s.IsPassiveFestivalToday) sb.Append(" | PassiveFestival");
        if (s.IsBooksellerToday) sb.Append(" | BooksellerToday");
        else if (s.UpcomingBooksellerDays.Count > 0) sb.Append($" | Bookseller: day {string.Join("/", s.UpcomingBooksellerDays)}");
        if (!string.IsNullOrEmpty(s.QueenOfSauceNote)) sb.Append($" | QueenOfSauce: {s.QueenOfSauceNote}");
        if (s.UpcomingBirthdays.Count > 0) sb.Append($" | Birthdays: {string.Join(", ", s.UpcomingBirthdays)}");
        else if (!string.IsNullOrEmpty(s.BirthdayToday)) sb.Append($" | Birthday: {s.BirthdayToday}");
        sb.AppendLine();

        // Farmer Core
        sb.AppendLine($"- **Money:** {s.Money:N0}g (Total: {s.TotalMoneyEarned:N0}g) | **Stamina:** {s.Stamina}/{s.MaxStamina} | **HP:** {s.Health}/{s.MaxHealth}");
        string houseUpgrading = s.DaysUntilHouseUpgrade > 0 ? $" (+{s.DaysUntilHouseUpgrade}d)" : "";
        sb.AppendLine($"- **Luck:** {s.DailyLuck:0.###} (Lv.{s.LuckLevel}) | **Mine:** {s.DeepestMineLevel} | **Museum:** {s.MuseumDonatedCount} | **Stardrops:** {s.StardropsFound} | **House:** Lv.{s.HouseUpgradeLevel}{houseUpgrading}");
        string masteryDetail = s.UnlockedMasteries.Count > 0 ? $" [{string.Join(", ", s.UnlockedMasteries)}]" : "";
        sb.AppendLine($"- **Mastery:** Lv.{s.MasteryLevel} (XP: {s.MasteryExp:N0}, Spent: {s.MasteryLevelsSpent}){masteryDetail}");
        if (s.GoldenWalnutsFound > 0 || s.GoldenWalnuts > 0 || s.QiGems > 0)
            sb.AppendLine($"- **Ginger Island:** Walnuts {s.GoldenWalnutsFound} (held: {s.GoldenWalnuts}) | **QiGems:** {s.QiGems}");
        sb.AppendLine($"- **Skills:** Farm {s.Skills.Farming}, Mine {s.Skills.Mining}, Combat {s.Skills.Combat}, Foraging {s.Skills.Foraging}, Fish {s.Skills.Fishing}"
            + (s.Professions.Count > 0 ? $" | Professions: [{string.Join(", ", s.Professions.Select(FormatProfession))}]" : ""));

        var toolParts = new List<string>();
        if (s.AxeLevel >= 0) toolParts.Add($"Axe: {FormatToolTier(s.AxeLevel)}");
        if (s.PickaxeLevel >= 0) toolParts.Add($"Pickaxe: {FormatToolTier(s.PickaxeLevel)}");
        if (s.WateringCanLevel >= 0) toolParts.Add($"WateringCan: {FormatToolTier(s.WateringCanLevel)}");
        if (s.HoeLevel >= 0) toolParts.Add($"Hoe: {FormatToolTier(s.HoeLevel)}");
        if (s.PanLevel >= 0) toolParts.Add($"Pan: {FormatToolTier(s.PanLevel)}");
        toolParts.Add($"TrashCan: {FormatToolTier(s.TrashCanLevel)}");
        if (!string.IsNullOrEmpty(s.BestRodSummary)) toolParts.Add($"Rod: {s.BestRodSummary}");
        sb.AppendLine($"- **Tools Owned:** {string.Join(", ", toolParts)}");

        if (!string.IsNullOrEmpty(s.ToolBeingUpgraded))
            sb.AppendLine($"- **Tool Upgrade:** {s.ToolBeingUpgraded} ({s.DaysLeftForToolUpgrade}d left)");
        if (s.ActiveBuffs.Count > 0)
            sb.AppendLine($"- **Active Buffs:** {string.Join(", ", s.ActiveBuffs)}");
        if (s.Equipped.Count > 0)
            sb.AppendLine($"- **Equipped:** {string.Join(", ", s.Equipped)}");
        if (s.WalletKeys.Count > 0)
            sb.AppendLine($"- **Wallet:** {string.Join(", ", s.WalletKeys)}");
        sb.AppendLine($"- **Unlocked:** {(s.UnlockedAreas.Count > 0 ? string.Join(", ", s.UnlockedAreas) : "None")}");
        if (s.PowersTotalCount > 0)
        {
            string booksText = s.UnlockedBooks.Count > 0 ? $" | Books: {string.Join(", ", s.UnlockedBooks)}" : "";
            sb.AppendLine($"- **Powers:** {s.PowersUnlockedCount}/{s.PowersTotalCount}{booksText}");
        }
        if (!string.IsNullOrEmpty(s.CollectionsSummary))
            sb.AppendLine($"- **Collections:** {s.CollectionsSummary}");
        if (s.MissingSeasonalFishTotal > 0)
        {
            string more = s.MissingSeasonalFishTotal > s.MissingSeasonalFishSample.Count
                ? $" (+{s.MissingSeasonalFishTotal - s.MissingSeasonalFishSample.Count} more)"
                : "";
            sb.AppendLine($"- **Missing Seasonal Fish ({s.MissingSeasonalFishTotal}):** {string.Join(", ", s.MissingSeasonalFishSample)}{more}");
        }
        if (s.FishGuide is { PriorityFish.Count: > 0 } fg)
        {
            var neededBrief = fg.PriorityFish.Select(f =>
            {
                if (f.InStorageOrInventory)
                    return $"{f.DisplayName} [In Storage]";
                FishSpawnSpot? spot = f.Spots.FirstOrDefault();
                return spot is not null
                    ? $"{f.DisplayName} ({spot.LocationDisplayName}: {spot.LiveStatus})"
                    : f.DisplayName;
            });
            sb.AppendLine($"- **Needed Fish (CC/Quests -> see live_fish.md):** {string.Join("; ", neededBrief)}");
        }
        if (s.MonsterGoalsTotal > 0)
        {
            string goals = s.MonsterGoalsIncomplete.Count > 0 ? string.Join(", ", s.MonsterGoalsIncomplete) : "All complete!";
            sb.AppendLine($"- **Monster Goals ({s.MonsterGoalsCompleted}/{s.MonsterGoalsTotal}):** {goals}");
        }
        if (s.MuseumReadyToDonate.Count > 0)
            sb.AppendLine($"- **Museum Ready to Donate:** {string.Join(", ", s.MuseumReadyToDonate)}");

        // Categorized Action Sections (Top-Priority Briefing)
        sb.AppendLine();
        sb.AppendLine("## 1. Bugün Kesin Yapılması Gerekenler (Kritik & Zaman Duyarlı)");
        if (s.MustDoToday.Count == 0)
            sb.AppendLine("- Bugün için kritik zaman duyarlı acil görev tespit edilmedi.");
        else
            foreach (string line in s.MustDoToday)
                sb.AppendLine($"- {line}");

        sb.AppendLine();
        sb.AppendLine("## 2. Günlük Çiftlik Rutini (Toplanacaklar, Ağaçlar, Hayvanlar ve Makineler)");
        if (s.DailyRoutine.Count == 0)
            sb.AppendLine("- Çiftlikte bugün bekleyen acil rutin yok.");
        else
            foreach (string line in s.DailyRoutine)
                sb.AppendLine($"- {line}");

        sb.AppendLine();
        sb.AppendLine("## 3. Uzun Vadeli Stratejik Yatırımlar (Bugün Yaparsan İleride Çok İşine Yarar)");
        if (s.StrategicInsights.Count == 0)
            sb.AppendLine("- Mevcut aşama için ek uzun vadeli öneri yok.");
        else
            foreach (string line in s.StrategicInsights)
                sb.AppendLine($"- {line}");

        // Inventory
        sb.AppendLine();
        sb.AppendLine($"## Inventory ({s.Inventory.Count}/{s.MaxItems})");
        if (s.Inventory.Count == 0)
            sb.AppendLine("- Empty");
        else
            sb.AppendLine(string.Join(", ", s.Inventory.Select(FormatItem)));

        // Shipping Bin
        if (s.ShippingItems.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"## Shipping Bin (~{s.ShippingEstimatedValue:N0}g)");
            sb.AppendLine(string.Join(", ", s.ShippingItems.Select(i => $"{FormatItem(i)} ({i.TotalSellPrice}g)")));
        }

        // Farm Overview
        sb.AppendLine();
        sb.AppendLine("## Farm");
        sb.AppendLine($"- **Hay:** {s.HayInSilos}/{s.HayCapacity} | **Planted Crops:** {s.Crops.Sum(c => c.Count)}");

        if (s.Crops.Count > 0)
        {
            foreach (var c in s.Crops)
            {
                string label = c.EnglishName == c.DisplayName ? c.DisplayName : $"{c.DisplayName} ({c.EnglishName})";
                string days = c.MinDaysRemaining == c.MaxDaysRemaining ? $"{c.MinDaysRemaining}d" : $"{c.MinDaysRemaining}-{c.MaxDaysRemaining}d";
                string fert = c.Fertilizers.Count > 0
                    ? " | fert: " + string.Join(", ", c.Fertilizers.Select(kv => $"{kv.Key} x{kv.Value}"))
                    : "";
                sb.AppendLine($"  - [{c.Location}] {label} x{c.Count} | watered {c.WateredCount}/{c.Count} | ready {c.ReadyToHarvestCount} | {days}{fert}");
            }
        }

        if (s.FruitTrees.Count > 0)
        {
            sb.AppendLine("- **Fruit Trees:** " + FormatFruitTrees(s.FruitTrees));
        }

        if (s.FarmForageSpots.Count > 0)
        {
            var reachable = s.FarmForageSpots.Where(f => f.Reachable).ToList();
            var blocked = s.FarmForageSpots.Where(f => !f.Reachable).ToList();
            if (reachable.Count > 0)
                sb.AppendLine("- **Farm Forage/Spots (Reachable):** " + string.Join("; ", reachable.Select(FormatFarmSpot)));
            if (blocked.Count > 0)
                sb.AppendLine("- **Farm Forage/Spots (Blocked):** " + string.Join("; ", blocked.Select(f =>
                    FormatFarmSpot(f) + (string.IsNullOrEmpty(f.BlockedBy) ? " [BLOCKED]" : $" [needs {f.BlockedBy}]"))));
        }

        if (s.FarmCaveReadyItems.Count > 0)
        {
            sb.AppendLine("- **Farm Cave Ready:** " + string.Join(", ", s.FarmCaveReadyItems.Select(FormatItem)));
        }

        if (s.Buildings.Count > 0)
        {
            sb.AppendLine("- **Buildings:**");
            foreach (var b in s.Buildings)
            {
                string buildNote = b.DaysOfConstructionLeft > 0 ? $" (building: {b.DaysOfConstructionLeft}d)" : "";
                if (b.FishPond is { } fp)
                {
                    string extra = !string.IsNullOrEmpty(fp.NeededItem) ? $", needs {fp.NeededItem} x{fp.NeededCount}" : "";
                    string ready = !string.IsNullOrEmpty(fp.OutputReady) ? $", ready: {fp.OutputReady}" : "";
                    sb.AppendLine($"  - Fish Pond{buildNote}: {fp.FishType} ({fp.Occupants}/{fp.MaxOccupants}{extra}{ready})");
                }
                else if (b.JunimoHut is { } jh)
                {
                    string paused = jh.NoHarvest ? " [PAUSED]" : "";
                    string raisins = jh.RaisinDays > 0 ? $", raisins: {jh.RaisinDays}d" : "";
                    sb.AppendLine($"  - {b.BuildingType}{buildNote}: {jh.OutputItems.Count} stacks ready{paused}{raisins}");
                }
                else if (b.SlimeHutch is { } sh)
                {
                    sb.AppendLine($"  - {b.BuildingType}{buildNote}: {sh.SlimeCount} slimes | water {sh.WateredSpots}/{sh.TotalWaterSpots} | slime balls ready: {sh.SlimeBallsReady}");
                }
                else if (b.MaxAnimalCapacity > 0 || b.Animals.Count > 0 || b.AutoGrabberItems.Count > 0)
                {
                    int fed = b.Animals.Count(a => a.WasFedToday);
                    int pet = b.Animals.Count(a => a.WasPetToday);
                    int prod = b.Animals.Count(a => a.ProduceWaiting);
                    int outside = b.Animals.Count(a => a.IsOutside);
                    string cap = b.MaxAnimalCapacity > 0 ? $"{b.Animals.Count}/{b.MaxAnimalCapacity}" : $"{b.Animals.Count}";
                    string doorTag = b.AnimalDoorOpen ? "door open" : "door closed";
                    string outsideTag = b.Animals.Count > 0 ? $", outside {outside}, {doorTag}" : $", {doorTag}";
                    string hayTag = b.Animals.Count > 0 ? $", troughHay {b.HayInTrough}/{b.Animals.Count}" : "";
                    string grabber = b.AutoGrabberItems.Count > 0 ? $" | autoGrabber: {b.AutoGrabberItems.Count} stacks" : "";
                    sb.AppendLine($"  - {b.BuildingType}{buildNote}: {cap} animals (fed {fed}, pet {pet}, produce {prod}{outsideTag}{hayTag}){grabber}");
                }
                else
                {
                    sb.AppendLine($"  - {b.BuildingType}{buildNote}");
                }
            }
        }

        if (s.Machines.Count > 0)
        {
            sb.AppendLine("- **Machines:** " + string.Join(", ", s.Machines.Select(m =>
            {
                var parts = new List<string>();
                if (m.Working > 0) parts.Add(FormatMachineBucket("w", m.Working, m.WorkingItems));
                if (m.Ready > 0) parts.Add(FormatMachineBucket("r", m.Ready, m.ReadyItems));
                if (m.Idle > 0) parts.Add($"{m.Idle}i");
                return $"{m.Name} x{m.Total} ({string.Join("/", parts)})";
            })));
        }

        if (s.TravelingCart.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Traveling Cart");
            sb.AppendLine(string.Join(", ", s.TravelingCart.Select(t =>
            {
                string label = t.EnglishName == t.DisplayName ? t.DisplayName : $"{t.DisplayName} ({t.EnglishName})";
                string stock = t.Stock > 1 && t.Stock < int.MaxValue ? $" x{t.Stock}" : "";
                return $"{label}{stock} ({t.Price:N0}g)";
            })));
        }

        if (s.UnvisitedKeyAreas.Count > 0 || s.WorldForage.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## World & Regions");
            if (s.UnvisitedKeyAreas.Count > 0)
                sb.AppendLine($"- **Unvisited Key Areas:** {string.Join(", ", s.UnvisitedKeyAreas)}");
            if (s.WorldForage.Count > 0)
            {
                sb.AppendLine("- **Forage / Dig Spots:** " + string.Join(", ", s.WorldForage.Take(10).Select(r =>
                {
                    var parts = new List<string>();
                    if (r.ForageCount > 0) parts.Add($"{r.ForageCount}f");
                    if (r.ArtifactSpots > 0) parts.Add($"{r.ArtifactSpots}s");
                    return $"{r.Region} ({string.Join("/", parts)})";
                })));
            }
        }

        if (s.ObjectsSummary.Count > 0)
        {
            sb.AppendLine("- **Objects:** " + string.Join(", ", s.ObjectsSummary.Select(kv => $"{kv.Key} x{kv.Value}")));
        }

        // Quests
        if (s.Quests.Count > 0 || s.SpecialOrders.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Quests");
            foreach (var q in s.Quests)
            {
                string req = !string.IsNullOrWhiteSpace(q.RequiredItemName)
                    ? $" [Target: {q.RequiredItemName} ({q.RequiredItemId})]"
                    : "";
                string days = q.DaysLeft > 0 ? $" ({q.DaysLeft}d)" : "";
                sb.AppendLine($"- {q.Title}: {q.Objective}{req}{days}");
            }
            foreach (var o in s.SpecialOrders)
            {
                string timeLabel = o.IsUntimed ? "untimed" : $"{o.DaysLeft}d";
                string objText = o.Objectives.Count > 0 ? $": {string.Join("; ", o.Objectives)}" : "";
                sb.AppendLine($"- [Special] {o.Name} ({timeLabel}){objText}");
            }
        }

        // CC Brief
        sb.AppendLine();
        if (!s.CcAvailable)
        {
            sb.AppendLine("## Community Center: Unavailable");
        }
        else
        {
            sb.AppendLine($"## Community Center (Completed: {s.CcCompletedCount}, Incomplete Visible: {s.CcIncompleteBundles.Count})");
            foreach (var b in s.CcIncompleteBundles)
                sb.AppendLine($"- {b.Area} / {b.BundleName}: {b.FilledCount}/{b.NeededCount}");
        }

        return sb.ToString();
    }

    private static string FormatChests(LiveStateSnapshot s)
    {
        var sb = new StringBuilder(2048);
        sb.AppendLine($"# Chests & Storage ({s.FarmName})");
        sb.AppendLine($"exportedAt: {s.ExportedAt}");
        sb.AppendLine();

        foreach (var c in s.Chests)
        {
            string label = string.IsNullOrEmpty(c.CustomName) ? "" : $" [{c.CustomName}]";
            sb.AppendLine($"## {c.Location} @ {c.Tile}{label} ({c.Items.Count} stacks)");
            sb.AppendLine(string.Join(", ", c.Items.Select(FormatItem)));
            sb.AppendLine();
        }

        var grabbers = s.Buildings.Where(b => b.AutoGrabberItems.Count > 0).ToList();
        if (grabbers.Count > 0)
        {
            sb.AppendLine("## Auto-Grabbers");
            foreach (var b in grabbers)
                sb.AppendLine($"- {b.BuildingType}: {string.Join(", ", b.AutoGrabberItems.Select(FormatItem))}");
        }

        var huts = s.Buildings.Where(b => b.JunimoHut is { OutputItems.Count: > 0 }).ToList();
        if (huts.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Junimo Huts");
            foreach (var b in huts)
                sb.AppendLine($"- {b.BuildingType}: {string.Join(", ", b.JunimoHut!.OutputItems.Select(FormatItem))}");
        }

        return sb.ToString();
    }

    private static string FormatCommunityCenter(LiveStateSnapshot s)
    {
        var sb = new StringBuilder(1024);
        sb.AppendLine($"# Community Center ({s.FarmName})");
        sb.AppendLine($"exportedAt: {s.ExportedAt} | Completed: {s.CcCompletedCount}");
        sb.AppendLine();

        if (!s.CcAvailable)
        {
            sb.AppendLine("Community Center unavailable.");
            return sb.ToString();
        }

        foreach (var b in s.CcIncompleteBundles)
        {
            sb.AppendLine($"## {b.Area} / {b.BundleName} ({b.FilledCount}/{b.NeededCount})");
            sb.AppendLine(string.Join(", ", b.MissingOptions));
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string FormatSocial(LiveStateSnapshot s)
    {
        var sb = new StringBuilder(1024);
        sb.AppendLine($"# Friendships & Animals ({s.FarmName})");
        sb.AppendLine($"exportedAt: {s.ExportedAt}");
        sb.AppendLine();

        sb.AppendLine($"## Friendships ({s.Friendships.Count})");
        if (s.Friendships.Count == 0)
        {
            sb.AppendLine("- None");
        }
        else
        {
            sb.AppendLine("| NPC | Hearts | Points | Status | Talked | Gifts (D/W) |");
            sb.AppendLine("| --- | --- | --- | --- | --- | --- |");
            foreach (var f in s.Friendships)
                sb.AppendLine($"| {f.Npc} | {f.Hearts} | {f.Points} | {f.Status} | {(f.TalkedToday ? "Y" : "N")} | {f.GiftsToday}/{f.GiftsThisWeek} |");
        }

        var animalBuildings = s.Buildings.Where(b => b.Animals.Count > 0).ToList();
        if (animalBuildings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Animals");
            foreach (var b in animalBuildings)
            {
                string doorNote = b.AnimalDoorOpen ? ", door open" : "";
                sb.AppendLine($"### {b.BuildingType} ({b.Animals.Count}{doorNote})");
                sb.AppendLine("| Name | Type | Location | Happiness | Fed | Pet | Produce |");
                sb.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");
                foreach (var a in b.Animals)
                    sb.AppendLine($"| {a.Name} | {a.Type} | {(a.IsOutside ? "Outside" : "Inside")} | {a.Happiness} | {(a.WasFedToday ? "Y" : "N")} | {(a.WasPetToday ? "Y" : "N")} | {(a.ProduceWaiting ? "Y" : "N")} |");
            }
        }

        return sb.ToString();
    }

    private static string FormatItem(ItemEntry item)
    {
        string label = string.IsNullOrEmpty(item.Name) || item.Name == item.DisplayName
            ? item.DisplayName
            : $"{item.DisplayName} ({item.Name})";

        if (item.Stack > 1)
            label += $" x{item.Stack}";
        if (item.Quality > 0)
            label += $" [q{item.Quality}]";

        return label;
    }

    private static string FormatTime(int timeOfDay)
    {
        int hour = timeOfDay / 100;
        int min = timeOfDay % 100;
        return $"{hour:D2}:{min:D2}";
    }

    private static string FormatFarmSpot(FarmSpotEntry f)
    {
        string label = f.EnglishName == f.DisplayName ? f.DisplayName : $"{f.DisplayName} ({f.EnglishName})";
        return $"{label} {f.Where}";
    }

    private static string FormatFruitTrees(List<FruitTreeEntry> trees)
    {
        var parts = new List<string>();

        // 1. Ready to Harvest (with exact coords)
        var readyFarm = trees.Where(t => t.FruitsReady > 0 && t.IsOnFarm).ToList();
        foreach (var t in readyFarm)
        {
            string blocked = t.Reachable ? "" : (string.IsNullOrEmpty(t.BlockedBy) ? " [BLOCKED]" : $" [BLOCKED: needs {t.BlockedBy}]");
            parts.Add($"[{t.Location}] {t.Tree} {t.Where} (ready: {t.FruitsReady}){blocked}");
        }

        var readyOffFarm = trees
            .Where(t => t.FruitsReady > 0 && !t.IsOnFarm)
            .GroupBy(t => (t.Location, t.Tree, t.Reachable, t.BlockedBy))
            .ToList();
        foreach (var g in readyOffFarm)
        {
            int treeCount = g.Count();
            int fruitSum = g.Sum(x => x.FruitsReady);
            string coords = string.Join(", ", g.Select(x => $"{x.Where}({x.FruitsReady})").Where(s => !string.IsNullOrWhiteSpace(s)));
            string coordNote = string.IsNullOrEmpty(coords) ? "" : $" @ {coords}";
            string blocked = g.Key.Reachable ? "" : (string.IsNullOrEmpty(g.Key.BlockedBy) ? " [BLOCKED]" : $" [BLOCKED: {g.Key.BlockedBy}]");
            string countLabel = treeCount > 1 ? $"{g.Key.Tree} x{treeCount}" : g.Key.Tree;
            parts.Add($"[{g.Key.Location}] {countLabel} ({fruitSum} ready{coordNote}){blocked}");
        }

        // 2. Growing Saplings
        var growing = trees.Where(t => t.FruitsReady == 0 && t.DaysUntilMature > 0).ToList();
        foreach (var t in growing)
        {
            string where = string.IsNullOrEmpty(t.Where) ? "" : $" {t.Where}";
            string blocked = t.Reachable ? "" : (string.IsNullOrEmpty(t.BlockedBy) ? " [BLOCKED]" : $" [BLOCKED: needs {t.BlockedBy}]");
            parts.Add($"[{t.Location}] {t.Tree}{where} (mature: {t.DaysUntilMature}d){blocked}");
        }

        // 3. Empty Mature Trees (grouped compactly without misleading 0d)
        var emptyGroups = trees
            .Where(t => t.FruitsReady == 0 && t.DaysUntilMature == 0)
            .GroupBy(t => t.Location)
            .ToList();
        foreach (var locGroup in emptyGroups)
        {
            var summary = locGroup
                .GroupBy(t => t.Tree)
                .Select(g => g.Count() > 1 ? $"{g.Key} x{g.Count()}" : g.Key);
            parts.Add($"Empty Mature [{locGroup.Key} ({locGroup.Count()})]: {string.Join(", ", summary)}");
        }

        return string.Join("; ", parts);
    }

    private static string FormatToolTier(int level) => level switch
    {
        0 => "Basic (Lv.0)",
        1 => "Copper (Lv.1)",
        2 => "Steel (Lv.2)",
        3 => "Gold (Lv.3)",
        4 => "Iridium (Lv.4)",
        < 0 => "Not in bag/chests",
        _ => $"Lv.{level}"
    };

    private static string FormatMachineBucket(string suffix, int count, Dictionary<string, int>? items)
    {
        if (items is null || items.Count == 0)
            return $"{count}{suffix}";

        string detail = string.Join(", ", items.Select(kv => kv.Value > 1 ? $"{kv.Key} x{kv.Value}" : kv.Key));
        return $"{count}{suffix}: {detail}";
    }

    private static string FormatProfession(int id) => id switch
    {
        0 => "Rancher",
        1 => "Tiller",
        2 => "Coopmaster",
        3 => "Shepherd",
        4 => "Artisan",
        5 => "Agriculturist",
        6 => "Fisher",
        7 => "Trapper",
        8 => "Angler",
        9 => "Pirate",
        10 => "Mariner",
        11 => "Luremaster",
        12 => "Forester",
        13 => "Gatherer",
        14 => "Lumberjack",
        15 => "Tapper",
        16 => "Botanist",
        17 => "Tracker",
        18 => "Miner",
        19 => "Geologist",
        20 => "Blacksmith",
        21 => "Prospector",
        22 => "Excavator",
        23 => "Gemologist",
        24 => "Fighter",
        25 => "Scout",
        26 => "Brute",
        27 => "Defender",
        28 => "Acrobat",
        29 => "Desperado",
        _ => id.ToString()
    };

    private static string FormatFishGuide(LiveStateSnapshot s)
    {
        var sb = new StringBuilder(4096);
        sb.AppendLine($"# Live Fishing Guide & Mod-Aware Spawn Matrix ({s.FarmName} - {s.FarmerName})");
        sb.AppendLine($"exportedAt: {s.ExportedAt} | Y{s.Year} {FishGuideBuilder.TranslateSeason(s.Season)} {s.DayOfMonth} ({FormatTime(s.TimeOfDay)}) | Valley Rain: {(s.IsRainingOnFarm ? "Yes" : "No")}");

        FishGuideSnapshot? fg = s.FishGuide;
        if (fg is null)
        {
            sb.AppendLine("No fishing snapshot available.");
            return sb.ToString();
        }

        sb.AppendLine($"- **Fishing Skill:** Lv.{fg.FishingSkillEffective} (base {fg.FishingSkillBase}) | **Best Rod:** {fg.ActiveRod}");
        if (!string.IsNullOrEmpty(s.CollectionsSummary))
            sb.AppendLine($"- **Progress:** {s.CollectionsSummary}");
        sb.AppendLine();

        if (fg.DuplicateNameWarnings.Count > 0)
        {
            sb.AppendLine("## Same/Similar Display Names (Disambiguation)");
            foreach (string w in fg.DuplicateNameWarnings)
                sb.AppendLine($"- {w}");
            sb.AppendLine();
        }

        sb.AppendLine($"## Needed Fish — Quests & Incomplete Community Center ({fg.PriorityFish.Count})");
        if (fg.PriorityFish.Count == 0)
        {
            sb.AppendLine("- None");
        }
        else
        {
            foreach (FishGuideEntry f in fg.PriorityFish)
                AppendFishEntry(sb, f, includeAllSpots: true);
        }
        sb.AppendLine();

        sb.AppendLine($"## Collection Missing Fish — Catchable This Season ({fg.MissingSeasonalCollectionFish.Count})");
        if (fg.MissingSeasonalCollectionFish.Count == 0)
        {
            sb.AppendLine("- None");
        }
        else
        {
            foreach (FishGuideEntry f in fg.MissingSeasonalCollectionFish)
                AppendFishEntry(sb, f, includeAllSpots: false);
        }
        sb.AppendLine();

        sb.AppendLine($"## Collection Missing Fish — Other Seasons ({fg.MissingOtherSeasonCollectionFish.Count})");
        if (fg.MissingOtherSeasonCollectionFish.Count == 0)
        {
            sb.AppendLine("- None");
        }
        else
        {
            foreach (FishGuideEntry f in fg.MissingOtherSeasonCollectionFish)
                AppendCompactFishEntry(sb, f);
        }

        if (fg.ExcludedUncaughtFish.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"## Extra Mod Fish (Excluded From Collection Tab) ({fg.ExcludedUncaughtFish.Count})");
            foreach (FishGuideEntry f in fg.ExcludedUncaughtFish.Take(40))
                AppendCompactFishEntry(sb, f);
            if (fg.ExcludedUncaughtFish.Count > 40)
                sb.AppendLine($"- ... (+{fg.ExcludedUncaughtFish.Count - 40} more)");
        }

        return sb.ToString();
    }

    private static void AppendFishEntry(StringBuilder sb, FishGuideEntry f, bool includeAllSpots)
    {
        string shortId = FishGuideBuilder.StripObjectPrefix(f.QualifiedItemId);
        var tags = new List<string>();
        if (f.InStorageOrInventory) tags.Add("DEPODA/ENVANTERDE VAR");
        if (f.IsCaught) tags.Add("Yakalandi");
        if (f.ExcludeFromCollection) tags.Add("Koleksiyon Disi Mod Baligi");
        if (f.IsTrapFish) tags.Add("Yengec Comlegi");
        if (f.Reasons.Count > 0) tags.AddRange(f.Reasons);

        string tagStr = tags.Count > 0 ? $" | [{string.Join(" | ", tags)}]" : "";
        string baseMeta = f.IsTrapFish
            ? "Yengec Comlegi"
            : $"Saat: {f.TimeDisplay} | Data/Fish Mevsim: {f.DataSeasons} | Hava: {TranslateWeather(f.DataWeather)} | Min Lv.{f.DataMinLevel} | Zorluk: {f.Difficulty} {f.Behavior}";

        sb.AppendLine($"- **{f.DisplayName}** (`{shortId}`){tagStr}");
        sb.AppendLine($"  - Base: {baseMeta}");

        if (f.Spots.Count == 0)
        {
            sb.AppendLine("  - Spawns: No matching loaded Data/Locations spawn entry found (may require Crab Pot area, shop, event, or unloaded area).");
            return;
        }

        IEnumerable<FishSpawnSpot> spotsToShow = includeAllSpots ? f.Spots : f.Spots.Take(5);
        foreach (FishSpawnSpot sp in spotsToShow)
            sb.AppendLine($"  - {FormatSpawnSpot(sp)}");

        if (!includeAllSpots && f.Spots.Count > 5)
            sb.AppendLine($"  - ... (+{f.Spots.Count - 5} more locations)");
    }

    private static void AppendCompactFishEntry(StringBuilder sb, FishGuideEntry f)
    {
        string shortId = FishGuideBuilder.StripObjectPrefix(f.QualifiedItemId);
        var topSpots = f.Spots.Take(3).Select(sp =>
        {
            string area = string.IsNullOrWhiteSpace(sp.AreaDisplayName) ? "" : $" ({sp.AreaDisplayName})";
            return $"{sp.LocationDisplayName}{area} [{FishGuideBuilder.FormatSeasonList(sp.EffectiveSeasons)}]";
        });
        string where = f.Spots.Count > 0 ? string.Join(", ", topSpots) : "No Data/Locations entry";
        string extra = f.Spots.Count > 3 ? $" (+{f.Spots.Count - 3})" : "";
        sb.AppendLine($"- **{f.DisplayName}** (`{shortId}`) | {f.TimeDisplay} | {TranslateWeather(f.DataWeather)} | Lv.{f.DataMinLevel} | {where}{extra}");
    }

    private static string FormatSpawnSpot(FishSpawnSpot sp)
    {
        string area = string.IsNullOrWhiteSpace(sp.AreaDisplayName) ? "" : $" -> Alan: {sp.AreaDisplayName}";
        if (sp.AreaBounds is { } ab)
            area += $" (X:{ab.X}..{ab.Right - 1}, Y:{ab.Y}..{ab.Bottom - 1})";

        var extras = new List<string>
        {
            $"Mevsim: {FishGuideBuilder.FormatSeasonList(sp.EffectiveSeasons)}",
            $"Hava: {TranslateWeather(sp.EffectiveWeather)}",
            $"Saat: {sp.TimeDisplay}"
        };

        if (sp.MinFishingLevel > 0)
            extras.Add($"Min Lv.{sp.MinFishingLevel}");
        if (sp.MinDistanceFromShore > 0)
            extras.Add($"Kiyidan en az {sp.MinDistanceFromShore} kare");
        if (sp.MaxDistanceFromShore >= 0)
            extras.Add($"Kiyiya en fazla {sp.MaxDistanceFromShore} kare");
        if (sp.PlayerPosition is { } pp)
            extras.Add($"Oyuncu Konumu: X={pp.X}..{pp.Right - 1}, Y={pp.Y}..{pp.Bottom - 1}");
        if (sp.BobberPosition is { } bp)
            extras.Add($"Mantar Konumu: X={bp.X}..{bp.Right - 1}, Y={bp.Y}..{bp.Bottom - 1}");
        if (sp.RequireMagicBait)
            extras.Add("Sihirli Yem gerekir");
        if (sp.IgnoreFishDataRequirements)
            extras.Add("Bolge Data/Fish kurallarini ezer");
        if (sp.IsBossFish)
            extras.Add("Boss Balik");
        if (sp.CatchLimit > 0)
            extras.Add($"Limit: {sp.CatchLimit}");
        if (!string.IsNullOrWhiteSpace(sp.ExtraCondition))
            extras.Add($"GSQ: {sp.ExtraCondition}");

        return $"[{sp.LiveStatus}] **{sp.LocationDisplayName}** (`{sp.LocationKey}`){area} | {string.Join(" | ", extras)}";
    }

    private static string TranslateWeather(string w) => w.ToLowerInvariant() switch
    {
        "rainy" => "Yagmurlu",
        "sunny" => "Gunesli",
        "both" => "Her hava",
        _ => w
    };
}
