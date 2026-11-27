using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.GameData.Crops;
using StardewValley.Internal;
using StardewValley.Locations;
using StardewValley.Menus;
using StardewValley.Monsters;
using StardewValley.Objects;
using StardewValley.Quests;
using StardewValley.SpecialOrders;
using StardewValley.TerrainFeatures;
using StardewValley.TokenizableStrings;
using StardewValley.Tools;

namespace LiveStateExporter;

public interface IGenericModConfigMenuApi
{
    void Register(IManifest mod, Action reset, Action save, bool titleScreenOnly = false);
    void AddBoolOption(IManifest mod, Func<bool> getValue, Action<bool> setValue, Func<string> name, Func<string>? tooltip = null, string? fieldId = null);
    void AddNumberOption(IManifest mod, Func<int> getValue, Action<int> setValue, Func<string> name, Func<string>? tooltip = null, int? min = null, int? max = null, int? interval = null, Func<int, string>? formatValue = null, string? fieldId = null);
    void AddKeybind(IManifest mod, Func<SButton> getValue, Action<SButton> setValue, Func<string> name, Func<string>? tooltip = null, string? fieldId = null);
}

public sealed class ModEntry : Mod
{
    private static readonly HashSet<string> TrackedMachines = new(StringComparer.OrdinalIgnoreCase)
    {
        "Keg", "Preserves Jar", "Cheese Press", "Mayonnaise Machine", "Furnace", "Heavy Furnace",
        "Dehydrator", "Seed Maker", "Crystalarium", "Tapper", "Heavy Tapper", "Bee House",
        "Fish Smoker", "Bait Maker", "Oil Maker", "Loom", "Recycling Machine", "Charcoal Kiln",
        "Worm Bin", "Deluxe Worm Bin", "Bone Mill", "Geode Crusher", "Lightning Rod",
        "Solar Panel", "Wood Chipper", "Coffee Maker", "Statue Of Endless Fortune", "Statue Of Perfection",
        "Statue Of True Perfection", "Statue Of Blessings", "Cask", "Mushroom Box",
        "Crab Pot", "Incubator", "Ostrich Incubator", "Slime Incubator", "Slime Egg-Press",
        "Mini-Forge", "Deconstructor", "Anvil"
    };

    private readonly object WriteLock = new();
    private readonly Dictionary<string, string> LastWrittenBodies = new(StringComparer.Ordinal);
    private ModConfig Config = new();
    private string ModOutputDir = string.Empty;
    private int LastExportTotalDays = -1;
    private int LastExportDayMinutes = -1;
    private volatile bool BackgroundWriteInFlight;
    private (string Save, int TotalDays, int Axe, int Pickaxe, int Clumps, int Buildings)? ReachCacheKey;
    private FarmReachability? CachedReachability;
    private List<(string Name, int X, int Y)>? CachedLandmarks;

    public override void Entry(IModHelper helper)
    {
        this.Config = helper.ReadConfig<ModConfig>();
        this.ModOutputDir = helper.DirectoryPath;

        helper.Events.GameLoop.GameLaunched += this.OnGameLaunched;
        helper.Events.GameLoop.SaveLoaded += this.OnSaveLoaded;
        helper.Events.GameLoop.DayStarted += this.OnDayStarted;
        helper.Events.GameLoop.TimeChanged += this.OnTimeChanged;
        helper.Events.GameLoop.Saved += this.OnSaved;
        helper.Events.Input.ButtonPressed += this.OnButtonPressed;
    }

    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        var gmcm = this.Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
        if (gmcm is null)
            return;

        gmcm.Register(
            mod: this.ModManifest,
            reset: () => this.Config = new ModConfig(),
            save: () => this.Helper.WriteConfig(this.Config));

        gmcm.AddKeybind(
            mod: this.ModManifest,
            getValue: () => this.Config.ExportKey,
            setValue: val => this.Config.ExportKey = val,
            name: () => "Export Hotkey",
            tooltip: () => "Keybind to force an immediate live state export.");

        gmcm.AddBoolOption(
            mod: this.ModManifest,
            getValue: () => this.Config.ExportOnTimeChanged,
            setValue: val => this.Config.ExportOnTimeChanged = val,
            name: () => "Auto Export on Time",
            tooltip: () => "Automatically export live state on in-game time intervals.");

        gmcm.AddNumberOption(
            mod: this.ModManifest,
            getValue: () => Math.Clamp(this.Config.AutoExportIntervalMinutes, 10, 120),
            setValue: val => this.Config.AutoExportIntervalMinutes = Math.Clamp(val, 10, 120),
            name: () => "Auto Export Interval",
            tooltip: () => "In-game minutes between automatic exports (10 in-game min ~ 7 real sec).",
            min: 10,
            max: 120,
            interval: 10,
            formatValue: val => $"{val}m");

        gmcm.AddBoolOption(
            mod: this.ModManifest,
            getValue: () => this.Config.ShowHudMessageOnHotkey,
            setValue: val => this.Config.ShowHudMessageOnHotkey = val,
            name: () => "HUD Toast on Hotkey",
            tooltip: () => "Show a confirmation toast when triggered via the hotkey.");
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        lock (this.WriteLock)
            this.LastWrittenBodies.Clear();
        this.ReachCacheKey = null;
        this.LastExportTotalDays = -1;
        this.LastExportDayMinutes = -1;
        this.ExportSafe(showHud: false, trigger: "SaveLoaded");
    }

    private void OnDayStarted(object? sender, DayStartedEventArgs e) => this.ExportSafe(showHud: false, trigger: "DayStarted");

    private void OnSaved(object? sender, SavedEventArgs e) => this.ExportSafe(showHud: false, trigger: "Saved");

    private void OnTimeChanged(object? sender, TimeChangedEventArgs e)
    {
        if (!this.Config.ExportOnTimeChanged || this.BackgroundWriteInFlight)
            return;

        int interval = Math.Max(10, this.Config.AutoExportIntervalMinutes);
        int totalDays = Game1.Date.TotalDays;
        int dayMinutes = ToDayMinutes(e.NewTime);

        if (this.LastExportTotalDays == totalDays
            && this.LastExportDayMinutes >= 0
            && (dayMinutes - this.LastExportDayMinutes) < interval)
        {
            return;
        }

        this.ExportSafe(showHud: false, trigger: "TimeChanged");
    }

    private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (!Context.IsWorldReady || e.Button != this.Config.ExportKey)
            return;

        this.ExportSafe(showHud: this.Config.ShowHudMessageOnHotkey, trigger: "Hotkey");
    }

    private static int ToDayMinutes(int timeOfDay) => (timeOfDay / 100) * 60 + (timeOfDay % 100);

    private void ExportSafe(bool showHud, string trigger)
    {
        if (!Context.IsWorldReady)
            return;

        try
        {
            LiveStateSnapshot snapshot = this.BuildSnapshot(trigger);
            this.LastExportTotalDays = Game1.Date.TotalDays;
            this.LastExportDayMinutes = ToDayMinutes(Game1.timeOfDay);

            List<string> dirs = this.GetOutputDirectories();
            string? currentSavePath = Constants.CurrentSavePath;
            string? historyLine = trigger is "DayStarted" or "SaveLoaded" ? BuildHistoryLine(snapshot) : null;
            string dateKey = $"Y{snapshot.Year} {snapshot.Season} {snapshot.DayOfMonth}";
            bool forceWriteAll = trigger is "SaveLoaded" or "DayStarted" or "Saved" or "Hotkey";

            this.BackgroundWriteInFlight = true;

            // Offload markdown formatting and dirty-checked atomic disk writes off the game loop thread.
            _ = Task.Run(() =>
            {
                try
                {
                    Dictionary<string, string> files = MarkdownFormatter.FormatAll(snapshot);
                    lock (this.WriteLock)
                    {
                        var filesToWrite = new List<KeyValuePair<string, string>>();
                        foreach (var kv in files)
                        {
                            string body = ExtractBodyWithoutTimestamp(kv.Value);
                            if (forceWriteAll
                                || !this.LastWrittenBodies.TryGetValue(kv.Key, out string? prevBody)
                                || !string.Equals(prevBody, body, StringComparison.Ordinal))
                            {
                                filesToWrite.Add(kv);
                                this.LastWrittenBodies[kv.Key] = body;
                            }
                        }

                        foreach (string dir in dirs)
                        {
                            foreach (var kv in filesToWrite)
                                WriteAtomic(Path.Combine(dir, kv.Key), kv.Value);

                            if (historyLine is not null)
                            {
                                UpsertHistoryDay(Path.Combine(dir, "progress_history.jsonl"), historyLine, snapshot.SaveFolder, dateKey);

                                bool isSharedDir = string.IsNullOrEmpty(currentSavePath)
                                    || !string.Equals(Path.GetFullPath(dir).TrimEnd('\\'), Path.GetFullPath(currentSavePath).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
                                if (isSharedDir && !string.IsNullOrEmpty(snapshot.SaveFolder))
                                {
                                    UpsertHistoryDay(
                                        Path.Combine(dir, $"progress_history_{snapshot.SaveFolder}.jsonl"),
                                        historyLine,
                                        snapshot.SaveFolder,
                                        dateKey);
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    this.Monitor.Log($"LiveState background write failed ({trigger}): {ex}", LogLevel.Warn);
                }
                finally
                {
                    this.BackgroundWriteInFlight = false;
                }
            });

            if (showHud)
                ShowLiveUpdateToast();
        }
        catch (Exception ex)
        {
            this.BackgroundWriteInFlight = false;
            this.Monitor.Log($"LiveState export failed ({trigger}): {ex}", LogLevel.Warn);
        }
    }

    private static string ExtractBodyWithoutTimestamp(string markdown)
    {
        int firstNl = markdown.IndexOf('\n');
        if (firstNl < 0)
            return markdown;
        int secondNl = markdown.IndexOf('\n', firstNl + 1);
        return secondNl >= 0 ? markdown[(secondNl + 1)..] : string.Empty;
    }

    private static void ShowLiveUpdateToast()
    {
        Farmer p = Game1.player;
        string seasonTr = Game1.currentSeason switch
        {
            "spring" => "Bahar",
            "summer" => "Yaz",
            "fall" => "Güz",
            "winter" => "Kış",
            _ => Game1.currentSeason
        };

        string timeText = Game1.getTimeOfDayString(Game1.timeOfDay);
        string text = $"Canlı kayıt güncellendi! ({p.Money:N0}g • {seasonTr} {Game1.dayOfMonth}, {timeText})";

        Game1.hudMessages.RemoveAll(m => m.message?.StartsWith("Canlı kayıt güncellendi", StringComparison.Ordinal) == true);
        Game1.addHUDMessage(new HUDMessage(text, HUDMessage.achievement_type) { timeLeft = 2600f });
        Game1.playSound("jingle1");
    }

    private List<string> GetOutputDirectories()
    {
        var dirs = new List<string> { this.ModOutputDir };

        if (!string.IsNullOrEmpty(Constants.CurrentSavePath) && Directory.Exists(Constants.CurrentSavePath))
            dirs.Add(Constants.CurrentSavePath);

        if (!string.IsNullOrEmpty(Constants.DataPath) && Directory.Exists(Constants.DataPath))
            dirs.Add(Constants.DataPath);

        return dirs;
    }

    private static void WriteAtomic(string path, string content)
    {
        string tempPath = path + ".tmp";
        File.WriteAllText(tempPath, content, Encoding.UTF8);
        File.Move(tempPath, path, overwrite: true);
    }

    private static string BuildHistoryLine(LiveStateSnapshot s)
    {
        var entry = new
        {
            date = $"Y{s.Year} {s.Season} {s.DayOfMonth}",
            save = s.SaveFolder,
            money = s.Money,
            totalEarned = s.TotalMoneyEarned,
            mine = s.DeepestMineLevel,
            museum = s.MuseumDonatedCount,
            stardrops = s.StardropsFound,
            skills = new[] { s.Skills.Farming, s.Skills.Mining, s.Skills.Combat, s.Skills.Foraging, s.Skills.Fishing },
            ccCompleted = s.CcCompletedCount,
            crops = s.Crops.Sum(c => c.Count)
        };
        return JsonSerializer.Serialize(entry);
    }

    private static void UpsertHistoryDay(string path, string line, string saveFolder, string dateKey)
    {
        string dateToken = $"\"date\":\"{dateKey}\"";
        string saveToken = $"\"save\":\"{saveFolder}\"";

        if (!File.Exists(path))
        {
            File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            return;
        }

        var lines = File.ReadLines(path, Encoding.UTF8)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        int matchIndex = lines.FindLastIndex(l =>
            l.Contains(dateToken, StringComparison.Ordinal)
            && l.Contains(saveToken, StringComparison.Ordinal));

        if (matchIndex >= 0)
        {
            if (string.Equals(lines[matchIndex], line, StringComparison.Ordinal))
                return;

            lines[matchIndex] = line;
            WriteAtomic(path, string.Join(Environment.NewLine, lines) + Environment.NewLine);
            return;
        }

        File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
    }

    private LiveStateSnapshot BuildSnapshot(string trigger)
    {
        Farmer p = Game1.player;
        Farm farm = Game1.getFarm();
        FarmHouse? home = Utility.getHomeOfFarmer(p);
        bool greenhouseUnlocked = IsGreenhouseUnlocked();
        bool islandUnlocked = IsGingerIslandUnlocked();
        GameLocation? greenhouse = greenhouseUnlocked ? Game1.getLocationFromName("Greenhouse") : null;
        GameLocation? cellar = p.HouseUpgradeLevel >= 3 ? Game1.getLocationFromName("Cellar") : null;
        GameLocation? farmCave = Game1.getLocationFromName("FarmCave");
        GameLocation? islandWest = islandUnlocked ? Game1.getLocationFromName("IslandWest") : null;
        GameLocation? islandHouse = islandUnlocked ? Game1.getLocationFromName("IslandFarmHouse") : null;

        var cropLocations = BuildCropLocations(farm, greenhouse, islandWest);
        var storageLocations = BuildStorageLocations(farm, home, greenhouse, cellar, farmCave, islandWest, islandHouse, cropLocations);
        var chestLocations = BuildChestLocations(storageLocations);
        var tools = CollectToolDetails(p, chestLocations);
        var reachKey = (
            Constants.SaveFolderName ?? string.Empty,
            Game1.Date.TotalDays,
            tools.Axe,
            tools.Pickaxe,
            farm.resourceClumps.Count,
            farm.buildings.Count);

        bool forceReachRefresh = trigger is "SaveLoaded" or "DayStarted" or "Hotkey";
        if (forceReachRefresh
            || this.ReachCacheKey != reachKey
            || this.CachedReachability is null
            || this.CachedLandmarks is null)
        {
            this.CachedReachability = ComputeFarmReachability(farm, tools.Axe, tools.Pickaxe);
            this.CachedLandmarks = BuildFarmLandmarks(farm);
            this.ReachCacheKey = reachKey;
        }

        var farmReach = this.CachedReachability;
        var landmarks = this.CachedLandmarks;

        var booksellerDays = Utility.getDaysOfBooksellerThisSeason()
            .Where(d => d >= Game1.dayOfMonth)
            .OrderBy(d => d)
            .ToList();
        bool isBooksellerToday = booksellerDays.Contains(Game1.dayOfMonth);
        var upcomingBookseller = booksellerDays.Where(d => d > Game1.dayOfMonth).ToList();

        var (powersUnlocked, powersTotal, unlockedMasteries, unlockedBooks) = CollectPowersAndMastery(p);
        var (monsterCompleted, monsterTotal, monsterIncomplete) = CollectMonsterGoals(p);
        var (ccAvailable, ccCompleted, ccIncomplete) = CollectCommunityCenterBundles();
        bool forceWorldRescan = trigger is "SaveLoaded" or "DayStarted" or "Hotkey";
        var (fishGuide, missingFishTotal, missingFishSample, caughtFishCount, collectionFishTotal) =
            FishGuideBuilder.Build(p, ccIncomplete, storageLocations, tools.BestRodSummary, forceWorldRescan);
        string collectionsSummary = CollectCollectionsSummary(p, caughtFishCount, collectionFishTotal);

        var shippingItems = farm.getShippingBin(p)
            .Where(i => i is not null)
            .Select(i => new ItemEntry(
                i.Name,
                i.DisplayName,
                i.Stack,
                i.Quality,
                i.sellToStorePrice(),
                i.sellToStorePrice() * i.Stack))
            .ToList();

        var equipped = new List<string>();
        if (p.hat.Value is not null) equipped.Add($"Hat: {p.hat.Value.DisplayName}");
        if (p.boots.Value is not null) equipped.Add($"Boots: {p.boots.Value.DisplayName}");
        bool listedWmrRings = false;
        try
        {
            var wmr = this.Helper.ModRegistry.GetApi<IWearMoreRingsApi>("bcmpinc.WearMoreRings");
            if (wmr is not null)
            {
                int slotCount = wmr.RingSlotCount();
                var rings = new List<string>();
                for (int i = 0; i < slotCount; i++)
                {
                    Ring? r = wmr.GetRing(i);
                    if (r is not null)
                        rings.Add(r.DisplayName);
                }
                equipped.Add($"Rings ({rings.Count}/{slotCount}): {(rings.Count > 0 ? string.Join(", ", rings) : "None")}");
                listedWmrRings = true;
            }
        }
        catch { }
        if (!listedWmrRings)
        {
            if (p.leftRing.Value is not null) equipped.Add($"LeftRing: {p.leftRing.Value.DisplayName}");
            if (p.rightRing.Value is not null) equipped.Add($"RightRing: {p.rightRing.Value.DisplayName}");
        }
        foreach (var t in p.trinketItems.Where(t => t is not null))
            equipped.Add($"Trinket: {t!.DisplayName}");

        var walletKeys = new List<string>();
        if (p.hasRustyKey) walletKeys.Add("RustyKey");
        if (p.hasSkullKey) walletKeys.Add("SkullKey");
        if (p.hasClubCard) walletKeys.Add("ClubCard");
        if (p.hasSpecialCharm) walletKeys.Add("SpecialCharm");
        if (p.hasMagnifyingGlass) walletKeys.Add("MagnifyingGlass");
        if (p.canUnderstandDwarves) walletKeys.Add("DwarvishGuide");
        if (p.HasTownKey) walletKeys.Add("TownKey");

        var unlocked = new List<string>();
        if (p.mailReceived.Contains("ccVault") || p.mailReceived.Contains("jojaVault")) unlocked.Add("Desert");
        if (greenhouseUnlocked) unlocked.Add("Greenhouse");
        if (p.mailReceived.Contains("ccBoilerRoom") || p.mailReceived.Contains("jojaBoilerRoom")) unlocked.Add("Minecarts");
        if (p.mailReceived.Contains("ccFishTank") || p.mailReceived.Contains("jojaFishTank")) unlocked.Add("PanningBoulder");
        if (p.mailReceived.Contains("ccCraftsRoom") || p.mailReceived.Contains("jojaCraftsRoom")) unlocked.Add("QuarryBridge");
        if (islandUnlocked) unlocked.Add("GingerIsland");
        if (p.mailReceived.Contains("ccIsComplete")) unlocked.Add("CommunityCenterCompleted");

        var museumReadyToDonate = CollectMuseumReadyItems(p, chestLocations, home);
        var travelingCart = CollectTravelingCart();
        var unvisitedKeyAreas = CollectUnvisitedKeyAreas(p);
        var worldForage = CollectWorldForage();
        var inventory = p.Items.Where(i => i is not null).Select(ToItemEntry).ToList();
        var crops = SummarizeCrops(cropLocations, storageLocations);
        var fruitTrees = SummarizeFruitTrees(cropLocations, farm, farmReach, landmarks);
        var farmForageSpots = CollectFarmForageSpots(farm, farmReach, landmarks);
        var buildings = SummarizeBuildings(farm);
        var machines = SummarizeMachines(chestLocations, storageLocations);
        var objectsSummary = SummarizeObjects(farm);
        var chests = CollectChests(chestLocations, home, islandHouse);
        var farmCaveReadyItems = CollectFarmCaveReadyItems(farmCave);
        var quests = CollectQuests(p);
        var specialOrders = CollectSpecialOrders(p);
        var friendships = CollectFriendships(p, chestLocations);
        string? birthdayToday = Utility.getTodaysBirthdayNPC()?.displayName;
        string? queenOfSauceNote = GetQueenOfSauceNote(p);
        string? toolBeingUpgraded = p.toolBeingUpgraded.Value?.DisplayName ?? p.toolBeingUpgraded.Value?.Name;
        int daysLeftForToolUpgrade = p.daysLeftForToolUpgrade.Value;
        string? festivalName = CollectFestivalName();
        bool isFestivalToday = Utility.isFestivalDay();
        bool isRainingOnFarm = farm.IsRainingHere();

        bool desertUnlocked = unlocked.Contains("Desert");
        var ownedIndex = new OwnedInventoryIndex(inventory, chests, buildings, farmCaveReadyItems, fruitTrees, crops);
        var worldIntel = WorldIntelligenceBuilder.Build(
            this.ModOutputDir,
            this.Monitor,
            this.Helper.ModRegistry,
            p,
            farm,
            greenhouseUnlocked,
            islandUnlocked,
            desertUnlocked,
            ccAvailable,
            isRainingOnFarm,
            isBooksellerToday,
            inventory,
            chests,
            buildings,
            machines,
            crops,
            friendships,
            chestLocations,
            ownedIndex,
            tools.Hoe,
            forceWorldRescan);

        var (mustDoToday, dailyRoutine, strategicInsights, topProfitableCrops) = BuildActionSections(
            p,
            greenhouseUnlocked,
            islandUnlocked,
            desertUnlocked,
            isRainingOnFarm,
            Game1.weatherForTomorrow,
            isFestivalToday,
            festivalName,
            isBooksellerToday,
            queenOfSauceNote,
            birthdayToday,
            toolBeingUpgraded,
            daysLeftForToolUpgrade,
            museumReadyToDonate,
            travelingCart,
            worldForage,
            inventory,
            crops,
            fruitTrees,
            farmForageSpots,
            farmCaveReadyItems,
            buildings,
            machines,
            objectsSummary,
            chests,
            ccAvailable,
            ccIncomplete,
            quests,
            specialOrders,
            friendships,
            fishGuide,
            ownedIndex,
            worldIntel);

        return new LiveStateSnapshot
        {
            ExportedAt = DateTime.Now.ToString("o"),
            Trigger = trigger,
            SaveFolder = Constants.SaveFolderName ?? string.Empty,
            Year = Game1.year,
            Season = Game1.currentSeason,
            DayOfMonth = Game1.dayOfMonth,
            DaysLeftInSeason = Math.Max(0, 28 - Game1.dayOfMonth),
            DayOfWeek = Game1.shortDayNameFromDayOfSeason(Game1.dayOfMonth),
            TimeOfDay = Game1.timeOfDay,
            IsRainingOnFarm = isRainingOnFarm,
            WeatherForTomorrow = Game1.weatherForTomorrow,
            IsFestivalToday = isFestivalToday,
            IsPassiveFestivalToday = Utility.IsPassiveFestivalDay(),
            FestivalName = festivalName,
            IsBooksellerToday = isBooksellerToday,
            UpcomingBooksellerDays = upcomingBookseller,
            QueenOfSauceNote = queenOfSauceNote,
            BirthdayToday = birthdayToday,
            UpcomingBirthdays = CollectUpcomingBirthdays(chestLocations),
            FarmerName = p.Name,
            FarmName = p.farmName.Value,
            Money = p.Money,
            TotalMoneyEarned = p.totalMoneyEarned,
            Stamina = (int)Math.Round(p.Stamina),
            MaxStamina = p.MaxStamina,
            Health = p.health,
            MaxHealth = p.maxHealth,
            DailyLuck = Math.Round(p.DailyLuck, 4),
            LuckLevel = p.LuckLevel,
            MaxItems = p.MaxItems,
            HouseUpgradeLevel = p.HouseUpgradeLevel,
            DaysUntilHouseUpgrade = p.daysUntilHouseUpgrade.Value,
            DeepestMineLevel = p.deepestMineLevel,
            StardropsFound = Utility.numStardropsFound(p),
            MuseumDonatedCount = Game1.netWorldState.Value.MuseumPieces.Length,
            MasteryExp = p.stats.Get(StardewValley.Constants.StatKeys.MasteryExp),
            MasteryLevel = MasteryTrackerMenu.getCurrentMasteryLevel(),
            MasteryLevelsSpent = p.stats.Get(StardewValley.Constants.StatKeys.MasteryLevelsSpent),
            UnlockedMasteries = unlockedMasteries,
            PowersUnlockedCount = powersUnlocked,
            PowersTotalCount = powersTotal,
            UnlockedBooks = unlockedBooks,
            GoldenWalnuts = Game1.netWorldState.Value.GoldenWalnuts,
            GoldenWalnutsFound = Game1.netWorldState.Value.GoldenWalnutsFound,
            QiGems = p.QiGems,
            CollectionsSummary = collectionsSummary,
            MissingSeasonalFishTotal = missingFishTotal,
            MissingSeasonalFishSample = missingFishSample,
            MonsterGoalsCompleted = monsterCompleted,
            MonsterGoalsTotal = monsterTotal,
            MonsterGoalsIncomplete = monsterIncomplete,
            Skills = (p.FarmingLevel, p.MiningLevel, p.CombatLevel, p.ForagingLevel, p.FishingLevel),
            Professions = p.professions.OrderBy(x => x).ToList(),
            ToolBeingUpgraded = toolBeingUpgraded,
            DaysLeftForToolUpgrade = daysLeftForToolUpgrade,
            AxeLevel = tools.Axe,
            PickaxeLevel = tools.Pickaxe,
            WateringCanLevel = tools.WateringCan,
            HoeLevel = tools.Hoe,
            PanLevel = tools.Pan,
            TrashCanLevel = p.trashCanLevel,
            BestRodSummary = tools.BestRodSummary,
            ActiveBuffs = CollectActiveBuffs(p),
            Equipped = equipped,
            WalletKeys = walletKeys,
            UnlockedAreas = unlocked,
            MuseumReadyToDonate = museumReadyToDonate,
            TravelingCart = travelingCart,
            UnvisitedKeyAreas = unvisitedKeyAreas,
            WorldForage = worldForage,
            Inventory = inventory,
            ShippingEstimatedValue = shippingItems.Sum(x => x.TotalSellPrice),
            ShippingItems = shippingItems,
            HayInSilos = farm.piecesOfHay.Value,
            HayCapacity = farm.GetHayCapacity(),
            Crops = crops,
            TopProfitableCrops = topProfitableCrops,
            FruitTrees = fruitTrees,
            FarmForageSpots = farmForageSpots,
            Buildings = buildings,
            Machines = machines,
            ObjectsSummary = objectsSummary,
            Chests = chests,
            FarmCaveReadyItems = farmCaveReadyItems,
            MustDoToday = mustDoToday,
            DailyRoutine = dailyRoutine,
            StrategicInsights = strategicInsights,
            CcAvailable = ccAvailable,
            CcCompletedCount = ccCompleted,
            CcIncompleteBundles = ccIncomplete,
            Quests = quests,
            SpecialOrders = specialOrders,
            Friendships = friendships,
            FishGuide = fishGuide,
            WorldIntel = worldIntel
        };
    }

    private static List<GameLocation> BuildCropLocations(Farm farm, params GameLocation?[] extra)
    {
        var list = new List<GameLocation> { farm };
        foreach (var loc in extra)
        {
            if (loc is not null && !list.Contains(loc))
                list.Add(loc);
        }

        bool greenhouseUnlocked = IsGreenhouseUnlocked();
        foreach (Building b in farm.buildings)
        {
            GameLocation? indoors = b.GetIndoors();
            if (indoors is not null && !list.Contains(indoors) && indoors.IsGreenhouse && greenhouseUnlocked)
                list.Add(indoors);
        }

        foreach (GameLocation loc in Game1.locations)
        {
            if (loc is null || list.Contains(loc))
                continue;
            if ((greenhouseUnlocked && (loc.IsGreenhouse || loc.Name.Contains("Greenhouse", StringComparison.OrdinalIgnoreCase)))
                || loc.Name.Contains("SummitFarm", StringComparison.OrdinalIgnoreCase)
                || loc.Name.Contains("FerngillRepublicFrontier", StringComparison.OrdinalIgnoreCase))
            {
                list.Add(loc);
            }
        }

        return list;
    }

    private static List<GameLocation> BuildStorageLocations(Farm farm, params object?[] extra)
    {
        var list = new List<GameLocation> { farm };
        foreach (var item in extra)
        {
            if (item is GameLocation loc && !list.Contains(loc))
                list.Add(loc);
            else if (item is IEnumerable<GameLocation> locs)
            {
                foreach (var l in locs)
                {
                    if (l is not null && !list.Contains(l))
                        list.Add(l);
                }
            }
        }

        foreach (Building b in farm.buildings)
        {
            GameLocation? indoors = b.GetIndoors();
            if (indoors is not null && IsLocationUnlocked(indoors, Game1.player) && !list.Contains(indoors))
                list.Add(indoors);
        }

        foreach (GameLocation loc in Game1.locations)
        {
            if (loc is not null && !list.Contains(loc) && loc.Name.Contains("FarmCave", StringComparison.OrdinalIgnoreCase))
                list.Add(loc);
        }

        return list;
    }

    private static List<GameLocation> BuildChestLocations(IEnumerable<GameLocation> baseLocations)
    {
        var seen = new HashSet<GameLocation>();
        var list = new List<GameLocation>();
        Farmer player = Game1.player;

        void AddWithBuildings(GameLocation? loc)
        {
            if (loc is null || !IsLocationUnlocked(loc, player) || !seen.Add(loc))
                return;
            list.Add(loc);

            foreach (Building b in loc.buildings)
            {
                GameLocation? indoors = b.GetIndoors();
                if (indoors is not null && IsLocationUnlocked(indoors, player) && seen.Add(indoors))
                    list.Add(indoors);
            }
        }

        foreach (var loc in baseLocations)
            AddWithBuildings(loc);
        foreach (GameLocation loc in Game1.locations)
            AddWithBuildings(loc);

        return list;
    }

    private static string DisambiguateEggName(string? qualifiedOrId, string baseName)
    {
        return qualifiedOrId switch
        {
            "(O)174" or "174" or "(O)176" or "176" => $"{baseName} (Beyaz)",
            "(O)180" or "180" or "(O)182" or "182" => $"{baseName} (Kahverengi)",
            _ => baseName
        };
    }

    private static ItemEntry ToItemEntry(Item item) =>
        new(item.Name, DisambiguateEggName(item.QualifiedItemId, item.DisplayName), item.Stack, item.Quality, QualifiedId: item.QualifiedItemId);

    private sealed class CropAccumulator
    {
        public string Location { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string EnglishName { get; set; } = string.Empty;
        public string HarvestItemId { get; set; } = string.Empty;
        public int Count { get; set; }
        public int WateredCount { get; set; }
        public int ReadyToHarvestCount { get; set; }
        public int MinDaysRemaining { get; set; } = int.MaxValue;
        public int MaxDaysRemaining { get; set; }
        public Dictionary<string, int> Fertilizers { get; } = new();
    }

    private static List<CropEntry> SummarizeCrops(IEnumerable<GameLocation> cropLocations, IEnumerable<GameLocation> potLocations)
    {
        var map = new Dictionary<string, CropAccumulator>();
        var scanned = new HashSet<GameLocation>();

        foreach (var loc in cropLocations)
        {
            if (loc is null || !scanned.Add(loc))
                continue;

            foreach (var pair in loc.terrainFeatures.Pairs)
            {
                if (pair.Value is HoeDirt dirt)
                    RecordCrop(dirt, loc.Name, map);
            }

            foreach (var obj in loc.objects.Values)
            {
                if (obj is IndoorPot pot && pot.hoeDirt.Value is HoeDirt potDirt)
                    RecordCrop(potDirt, loc.Name, map);
            }
        }

        foreach (var loc in potLocations)
        {
            if (loc is null || !scanned.Add(loc))
                continue;

            foreach (var obj in loc.objects.Values)
            {
                if (obj is IndoorPot pot && pot.hoeDirt.Value is HoeDirt potDirt)
                    RecordCrop(potDirt, loc.Name, map);
            }
        }

        return map.Values
            .OrderByDescending(c => c.Count)
            .Select(c => new CropEntry(
                c.Location,
                c.DisplayName,
                c.EnglishName,
                c.Count,
                c.WateredCount,
                c.ReadyToHarvestCount,
                c.MinDaysRemaining == int.MaxValue ? 0 : c.MinDaysRemaining,
                c.MaxDaysRemaining,
                c.Fertilizers,
                c.HarvestItemId))
            .ToList();
    }

    private static void RecordCrop(HoeDirt dirt, string locationName, Dictionary<string, CropAccumulator> map)
    {
        if (dirt.crop is null)
            return;

        Crop crop = dirt.crop;
        if (crop.dead.Value)
            return;

        string harvestId = crop.indexOfHarvest.Value ?? string.Empty;
        if (string.IsNullOrEmpty(harvestId) || harvestId == "0")
        {
            if (crop.forageCrop.Value)
                harvestId = crop.whichForageCrop.Value == "2" ? "(O)829" : "(O)399";
            else if (crop.isWildSeedCrop())
                harvestId = "WildSeeds";
            else
                harvestId = "unknown";
        }

        string key = $"{locationName}::{harvestId}";

        if (!map.TryGetValue(key, out var acc))
        {
            var data = ItemRegistry.GetData(harvestId) ?? ItemRegistry.GetData("(O)" + harvestId);
            acc = new CropAccumulator
            {
                Location = locationName,
                DisplayName = data?.DisplayName ?? harvestId,
                EnglishName = data?.InternalName ?? harvestId,
                HarvestItemId = harvestId is "WildSeeds" or "unknown" ? string.Empty : FishGuideBuilder.NormalizeQid(harvestId)
            };
            map[key] = acc;
        }

        acc.Count++;
        if (dirt.state.Value == HoeDirt.watered)
            acc.WateredCount++;

        if (!string.IsNullOrEmpty(dirt.fertilizer.Value))
        {
            string fertName = ItemRegistry.GetData(dirt.fertilizer.Value)?.DisplayName ?? dirt.fertilizer.Value;
            acc.Fertilizers[fertName] = acc.Fertilizers.TryGetValue(fertName, out int fc) ? fc + 1 : 1;
        }

        int daysLeft = CalculateDaysRemaining(crop);
        if (daysLeft == 0)
            acc.ReadyToHarvestCount++;
        if (daysLeft < acc.MinDaysRemaining)
            acc.MinDaysRemaining = daysLeft;
        if (daysLeft > acc.MaxDaysRemaining)
            acc.MaxDaysRemaining = daysLeft;
    }

    private static int CalculateDaysRemaining(Crop crop)
    {
        if (crop.fullyGrown.Value)
            return crop.dayOfCurrentPhase.Value > 0 ? crop.dayOfCurrentPhase.Value : 0;

        int total = 0;
        for (int i = crop.currentPhase.Value; i < crop.phaseDays.Count - 1; i++)
            total += crop.phaseDays[i];

        return Math.Max(0, total - crop.dayOfCurrentPhase.Value);
    }

    private static List<FruitTreeEntry> SummarizeFruitTrees(
        IEnumerable<GameLocation?> locations,
        Farm farm,
        FarmReachability reach,
        List<(string Name, int X, int Y)> landmarks)
    {
        var trees = new List<FruitTreeEntry>();
        foreach (var loc in locations)
        {
            if (loc is null)
                continue;

            foreach (var pair in loc.terrainFeatures.Pairs)
            {
                if (pair.Value is not FruitTree tree || tree.stump.Value)
                    continue;

                int x = (int)pair.Key.X;
                int y = (int)pair.Key.Y;
                bool isFarm = ReferenceEquals(loc, farm);
                bool visitedLoc = isFarm || Game1.player.locationsVisited.Contains(loc.Name);
                string where = isFarm
                    ? DescribeRelativeToLandmarks(x, y, landmarks)
                    : (tree.fruit.Count > 0 ? $"@({x},{y})" : string.Empty);
                var (canReach, blockedBy) = isFarm
                    ? reach.CheckTile(x, y)
                    : (visitedLoc, visitedLoc ? null : "Bölge henüz ziyaret edilmedi");
                string fruitQid = tree.fruit.Count > 0 && tree.fruit[0] is not null
                    ? FishGuideBuilder.NormalizeQid(tree.fruit[0].QualifiedItemId)
                    : string.Empty;

                trees.Add(new FruitTreeEntry(
                    isFarm ? loc.Name : ShortRegionName(loc.Name),
                    tree.GetDisplayName(),
                    Math.Max(0, tree.daysUntilMature.Value),
                    tree.fruit.Count,
                    where,
                    canReach,
                    blockedBy,
                    fruitQid,
                    isFarm));
            }
        }
        return trees;
    }

    private static List<BuildingEntry> SummarizeBuildings(Farm farm)
    {
        var list = new List<BuildingEntry>();
        foreach (Building b in farm.buildings)
        {
            if (b is FishPond pond)
            {
                string fishName = string.IsNullOrEmpty(pond.fishType.Value)
                    ? "Empty"
                    : (ItemRegistry.GetData(pond.fishType.Value)?.DisplayName ?? pond.fishType.Value);

                list.Add(new BuildingEntry(
                    "Fish Pond",
                    b.daysOfConstructionLeft.Value,
                    new List<AnimalEntry>(),
                    new List<ItemEntry>(),
                    new FishPondEntry(
                        fishName,
                        pond.currentOccupants.Value,
                        pond.maxOccupants.Value,
                        pond.neededItem.Value?.DisplayName,
                        pond.neededItemCount.Value,
                        pond.output.Value?.DisplayName)));
                continue;
            }

            if (b is JunimoHut hut)
            {
                var hutItems = hut.GetOutputChest()?.Items
                    .Where(i => i is not null)
                    .Select(ToItemEntry)
                    .ToList() ?? new List<ItemEntry>();

                list.Add(new BuildingEntry(
                    "Junimo Hut",
                    b.daysOfConstructionLeft.Value,
                    new List<AnimalEntry>(),
                    new List<ItemEntry>(),
                    JunimoHut: new JunimoHutEntry(hut.noHarvest.Value, hut.raisinDays.Value, hutItems)));
                continue;
            }

            var animals = new List<AnimalEntry>();
            var autoGrabberItems = new List<ItemEntry>();
            int maxAnimalCapacity = 0;
            int hayInTrough = 0;
            bool animalDoorOpen = b.animalDoorOpen.Value;
            SlimeHutchEntry? slimeHutchEntry = null;
            GameLocation? indoors = b.GetIndoors();

            if (indoors is AnimalHouse house)
            {
                maxAnimalCapacity = house.animalLimit.Value;
                var seenAnimalIds = new HashSet<long>();

                foreach (var animal in house.animals.Values)
                    TryAddAnimal(animals, seenAnimalIds, animal, isOutside: false);

                foreach (long animalId in house.animalsThatLiveHere)
                {
                    if (seenAnimalIds.Contains(animalId))
                        continue;

                    if (!farm.animals.TryGetValue(animalId, out FarmAnimal? outdoorAnimal))
                        outdoorAnimal = Utility.getAnimal(animalId);

                    TryAddAnimal(animals, seenAnimalIds, outdoorAnimal, isOutside: true);
                }

                foreach (var animal in farm.animals.Values)
                {
                    if (ReferenceEquals(animal.home, b))
                        TryAddAnimal(animals, seenAnimalIds, animal, isOutside: true);
                }

                foreach (var obj in house.objects.Values)
                {
                    if (obj is null)
                        continue;
                    if (obj.QualifiedItemId == "(O)178" || string.Equals(obj.Name, "Hay", StringComparison.OrdinalIgnoreCase))
                    {
                        hayInTrough++;
                        continue;
                    }
                    if (obj.QualifiedItemId == "(BC)165" && obj.heldObject.Value is Chest grabber)
                    {
                        foreach (Item? gi in grabber.Items)
                        {
                            if (gi is not null)
                                autoGrabberItems.Add(ToItemEntry(gi));
                        }
                    }
                }
            }
            else if (indoors is SlimeHutch slimeHutch)
            {
                int slimeCount = slimeHutch.characters.OfType<GreenSlime>().Count();
                int totalWater = slimeHutch.waterSpots.Count;
                int watered = slimeHutch.waterSpots.Count(w => w);
                int balls = slimeHutch.objects.Values.Count(o =>
                    o is not null && (o.QualifiedItemId == "(BC)56" || string.Equals(o.Name, "Slime Ball", StringComparison.OrdinalIgnoreCase)));
                slimeHutchEntry = new SlimeHutchEntry(slimeCount, watered, totalWater, balls);
            }

            string buildingLabel = (b is GreenhouseBuilding
                || b.buildingType.Value.Contains("Greenhouse", StringComparison.OrdinalIgnoreCase))
                && !IsGreenhouseUnlocked()
                ? "Greenhouse [LOCKED]"
                : b.buildingType.Value;

            list.Add(new BuildingEntry(
                buildingLabel,
                b.daysOfConstructionLeft.Value,
                animals,
                autoGrabberItems,
                MaxAnimalCapacity: maxAnimalCapacity,
                SlimeHutch: slimeHutchEntry,
                HayInTrough: hayInTrough,
                AnimalDoorOpen: animalDoorOpen,
                DaysUntilUpgrade: b.daysUntilUpgrade.Value,
                UpgradeTarget: b.daysUntilUpgrade.Value > 0 ? b.upgradeName.Value : null));
        }

        return list;
    }

    private static bool IsGreenhouseUnlocked() =>
        Game1.MasterPlayer.mailReceived.Contains("ccPantry")
        || Game1.MasterPlayer.mailReceived.Contains("jojaPantry")
        || Game1.player.mailReceived.Contains("ccPantry")
        || Game1.player.mailReceived.Contains("jojaPantry");

    private static bool IsGingerIslandUnlocked() =>
        Game1.MasterPlayer.mailReceived.Contains("willyBackRoomInvitation")
        || Game1.player.mailReceived.Contains("willyBackRoomInvitation")
        || Game1.player.locationsVisited.Contains("IslandSouth");

    internal static bool IsLocationUnlockedForExport(GameLocation loc, Farmer player) => IsLocationUnlocked(loc, player);

    private static bool IsLocationUnlocked(GameLocation loc, Farmer player)
    {
        if ((loc is Cellar || loc.Name.StartsWith("Cellar", StringComparison.OrdinalIgnoreCase)) && player.HouseUpgradeLevel < 3)
            return false;
        if ((loc.IsGreenhouse || loc.Name.Contains("Greenhouse", StringComparison.OrdinalIgnoreCase)) && !IsGreenhouseUnlocked())
            return false;
        if (loc.Name.StartsWith("Island", StringComparison.OrdinalIgnoreCase) && !IsGingerIslandUnlocked())
            return false;
        return true;
    }

    private static List<ItemEntry> CollectFarmCaveReadyItems(GameLocation? farmCave)
    {
        var ready = new List<ItemEntry>();
        if (farmCave is null)
            return ready;

        foreach (var obj in farmCave.objects.Values)
        {
            if (obj is null || obj is Chest)
                continue;

            if (obj.QualifiedItemId == "(BC)128" || string.Equals(obj.Name, "Mushroom Box", StringComparison.OrdinalIgnoreCase))
            {
                if (obj.readyForHarvest.Value && obj.heldObject.Value is not null)
                    ready.Add(ToItemEntry(obj.heldObject.Value));
                continue;
            }

            if ((obj.IsSpawnedObject || obj.Category == StardewValley.Object.FruitsCategory || obj.isForage())
                && !IsTrackedMachine(obj))
            {
                ready.Add(ToItemEntry(obj));
            }
        }

        return ready;
    }

    private static void TryAddAnimal(List<AnimalEntry> animals, HashSet<long> seenIds, FarmAnimal? animal, bool isOutside)
    {
        if (animal is null || !seenIds.Add(animal.myID.Value))
            return;

        animals.Add(new AnimalEntry(
            animal.type.Value,
            animal.displayName,
            animal.happiness.Value,
            animal.fullness.Value > 0,
            animal.wasPet.Value,
            animal.currentProduce.Value is not null,
            isOutside));
    }

    private sealed class MachineAccumulator
    {
        public int Total { get; set; }
        public int Working { get; set; }
        public int Ready { get; set; }
        public int Idle { get; set; }
        public Dictionary<string, (int Count, int MinEta, int MaxEta, bool IsDays)> WorkingBuckets { get; } = new();
        public Dictionary<string, int> ReadyItems { get; } = new();

        public void AddWorking(string label, int eta, bool isDays)
        {
            if (this.WorkingBuckets.TryGetValue(label, out var cur))
                this.WorkingBuckets[label] = (cur.Count + 1, Math.Min(cur.MinEta, eta), Math.Max(cur.MaxEta, eta), isDays);
            else
                this.WorkingBuckets[label] = (1, eta, eta, isDays);
        }

        public Dictionary<string, int> BuildWorkingItems()
        {
            var dict = new Dictionary<string, int>();
            foreach (var kv in this.WorkingBuckets)
            {
                string key = kv.Value.MinEta < 0
                    ? kv.Key
                    : kv.Value.IsDays
                        ? (kv.Value.MinEta == kv.Value.MaxEta
                            ? $"{kv.Key}, {kv.Value.MinEta}d]"
                            : $"{kv.Key}, {kv.Value.MinEta}-{kv.Value.MaxEta}d]")
                        : (kv.Value.MinEta == kv.Value.MaxEta
                            ? $"{kv.Key} ({FormatMinutesEta(kv.Value.MinEta)})"
                            : $"{kv.Key} ({FormatMinutesEta(kv.Value.MinEta)}-{FormatMinutesEta(kv.Value.MaxEta)})");
                dict[key] = dict.TryGetValue(key, out int existing) ? existing + kv.Value.Count : kv.Value.Count;
            }
            return dict;
        }
    }

    private static string FormatMinutesEta(int minutes)
    {
        if (minutes < 60)
            return $"{minutes}m";
        if (minutes < 1440)
        {
            int h = minutes / 60;
            int m = minutes % 60;
            return m == 0 ? $"{h}h" : $"{h}h{m:D2}m";
        }
        double days = Math.Round(minutes / 1600.0, 1);
        return string.Create(CultureInfo.InvariantCulture, $"~{days:0.#}d");
    }

    private static bool IsTrackedMachine(StardewValley.Object obj)
    {
        if (obj is CrabPot)
            return true;
        if (obj is Chest)
            return false;
        if (string.IsNullOrEmpty(obj.Name))
            return false;
        if (TrackedMachines.Contains(obj.Name))
            return true;
        if (obj.Name.Equals("Workbench", StringComparison.OrdinalIgnoreCase)
            || obj.Name.Contains("Campfire", StringComparison.OrdinalIgnoreCase)
            || obj.Name.Equals("Cookout Kit", StringComparison.OrdinalIgnoreCase))
            return false;

        return obj.GetMachineData() is not null;
    }

    private static List<MachineEntry> SummarizeMachines(
        IEnumerable<GameLocation> locations,
        IEnumerable<GameLocation> playerStorageLocations)
    {
        var playerLocs = new HashSet<GameLocation>(playerStorageLocations);
        var map = new Dictionary<string, MachineAccumulator>();
        Farmer player = Game1.player;
        foreach (var loc in locations)
        {
            if (loc.Name.Equals("Custom_AuroraVineyard", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!IsLocationUnlocked(loc, player))
                continue;

            bool isPlayerOrPublicOutdoor = playerLocs.Contains(loc)
                || loc.IsOutdoors
                || loc.Name.Equals("Tunnel", StringComparison.OrdinalIgnoreCase);

            foreach (var obj in loc.objects.Values)
            {
                if (obj is null || !IsTrackedMachine(obj))
                    continue;

                if (obj is Cask)
                {
                    if (!playerLocs.Contains(loc) && obj.owner.Value == 0)
                        continue;
                }
                else if (!isPlayerOrPublicOutdoor && obj.owner.Value == 0)
                {
                    continue;
                }

                string machineKey = obj is CrabPot && string.IsNullOrEmpty(obj.Name) ? "Crab Pot" : obj.Name;
                if (!map.TryGetValue(machineKey, out var state))
                {
                    state = new MachineAccumulator();
                    map[machineKey] = state;
                }

                state.Total++;

                if (obj is CrabPot pot)
                {
                    if (pot.readyForHarvest.Value)
                    {
                        state.Ready++;
                        string catchName = pot.heldObject.Value?.DisplayName ?? "Catch";
                        state.ReadyItems[catchName] = state.ReadyItems.TryGetValue(catchName, out int c) ? c + 1 : 1;
                    }
                    else if (pot.bait.Value is not null)
                    {
                        state.Working++;
                        state.AddWorking("Baited", -1, isDays: false);
                    }
                    else
                    {
                        state.Idle++;
                    }
                    continue;
                }

                string? heldName = obj.heldObject.Value?.DisplayName;
                if (obj.readyForHarvest.Value)
                {
                    state.Ready++;
                    if (!string.IsNullOrEmpty(heldName))
                        state.ReadyItems[heldName] = state.ReadyItems.TryGetValue(heldName, out int c) ? c + 1 : 1;
                    continue;
                }

                if (obj is Cask cask)
                {
                    if (cask.heldObject.Value is not null && cask.daysToMature.Value > 0)
                    {
                        state.Working++;
                        int daysLeft = Math.Max(1, (int)Math.Ceiling(cask.daysToMature.Value / Math.Max(0.01f, cask.agingRate.Value)));
                        string itemLabel = $"{heldName ?? "Item"} [q{cask.heldObject.Value.Quality}";
                        state.AddWorking(itemLabel, daysLeft, isDays: true);
                    }
                    else
                    {
                        state.Idle++;
                    }
                    continue;
                }

                if (obj.MinutesUntilReady > 0)
                {
                    state.Working++;
                    state.AddWorking(string.IsNullOrEmpty(heldName) ? "Working" : heldName, obj.MinutesUntilReady, isDays: false);
                }
                else
                {
                    state.Idle++;
                }
            }
        }

        return map
            .OrderByDescending(kv => kv.Value.Ready)
            .ThenByDescending(kv => kv.Value.Working)
            .ThenByDescending(kv => kv.Value.Total)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new MachineEntry(
                kv.Key,
                kv.Value.Total,
                kv.Value.Working,
                kv.Value.Ready,
                kv.Value.Idle,
                kv.Value.BuildWorkingItems(),
                kv.Value.ReadyItems))
            .ToList();
    }

    private static Dictionary<string, int> SummarizeObjects(Farm farm)
    {
        var counts = new Dictionary<string, int>();
        foreach (var obj in farm.objects.Values)
        {
            if (obj is null)
                continue;
            if (obj.IsSpawnedObject || obj.QualifiedItemId is "(O)590" or "(O)SeedSpot")
                continue;
            string name = obj.Name ?? "Unknown";
            if (name is "Weeds" or "Twig" or "Stone")
                continue;
            counts[name] = counts.TryGetValue(name, out int cur) ? cur + 1 : 1;
        }

        return counts.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    private static List<ChestEntry> CollectChests(IEnumerable<GameLocation> locations, FarmHouse? home, GameLocation? islandHouse)
    {
        var result = new List<ChestEntry>();

        if (home?.fridge.Value is Chest fridge && fridge.Items.Any(i => i is not null))
        {
            fridge.modData.TryGetValue("Pathoschild.ChestsAnywhere/Name", out string? fridgeName);
            result.Add(new ChestEntry(
                "FarmHouse_Fridge",
                "Fridge",
                fridge.Items.Where(i => i is not null).Select(ToItemEntry).ToList(),
                fridgeName ?? string.Empty));
        }

        if (islandHouse is IslandFarmHouse ifh && ifh.fridge.Value is Chest islandFridge && islandFridge.Items.Any(i => i is not null))
        {
            islandFridge.modData.TryGetValue("Pathoschild.ChestsAnywhere/Name", out string? iFridgeName);
            result.Add(new ChestEntry(
                "IslandFarmHouse_Fridge",
                "Fridge",
                islandFridge.Items.Where(i => i is not null).Select(ToItemEntry).ToList(),
                iFridgeName ?? string.Empty));
        }

        foreach (var loc in locations)
        {
            foreach (var pair in loc.objects.Pairs)
            {
                if (pair.Value is not Chest chest || !chest.playerChest.Value)
                    continue;

                var items = chest.Items.Where(i => i is not null).Select(ToItemEntry).ToList();
                if (items.Count == 0)
                    continue;

                chest.modData.TryGetValue("Pathoschild.ChestsAnywhere/Name", out string? customName);
                result.Add(new ChestEntry(loc.Name, $"{pair.Key.X},{pair.Key.Y}", items, customName ?? string.Empty));
            }
        }

        return result;
    }

    private static List<string> CollectMuseumReadyItems(Farmer player, IEnumerable<GameLocation> locations, FarmHouse? home)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        void CheckItems(IEnumerable<Item?> items, string source)
        {
            foreach (Item? item in items)
            {
                if (item is null || string.IsNullOrEmpty(item.QualifiedItemId))
                    continue;

                if (!LibraryMuseum.IsItemSuitableForDonation(item.QualifiedItemId, checkDonatedItems: true))
                    continue;

                if (seen.Add(item.QualifiedItemId))
                {
                    string label = item.Name == item.DisplayName
                        ? item.DisplayName
                        : $"{item.DisplayName} ({item.Name})";
                    result.Add($"{label} [{source}]");
                }
            }
        }

        CheckItems(player.Items, "Inv");

        if (home?.fridge.Value is Chest fridge)
            CheckItems(fridge.Items, "Fridge");

        foreach (var loc in locations)
        {
            foreach (var pair in loc.objects.Pairs)
            {
                if (pair.Value is Chest chest && chest.playerChest.Value)
                {
                    chest.modData.TryGetValue("Pathoschild.ChestsAnywhere/Name", out string? customName);
                    string chestLabel = !string.IsNullOrWhiteSpace(customName)
                        ? $"Chest: {customName}"
                        : $"Chest: {ShortRegionName(loc.Name)} @ {(int)pair.Key.X},{(int)pair.Key.Y}";
                    CheckItems(chest.Items, chestLabel);
                }
            }
        }

        return result;
    }

    private static List<ShopItemEntry> CollectTravelingCart()
    {
        try
        {
            if (Game1.getLocationFromName("Forest") is not Forest forest || !forest.ShouldTravelingMerchantVisitToday())
                return new List<ShopItemEntry>();

            var stock = ShopBuilder.GetShopStock("Traveler");
            var result = new List<ShopItemEntry>();
            foreach (var pair in stock)
            {
                if (pair.Key is null)
                    continue;

                result.Add(new ShopItemEntry(
                    pair.Key.DisplayName,
                    pair.Key.Name,
                    pair.Value.Price,
                    pair.Value.Stock,
                    pair.Key.QualifiedItemId ?? string.Empty));
            }
            return result;
        }
        catch
        {
            return new List<ShopItemEntry>();
        }
    }

    private static string ShortRegionName(string name)
    {
        if (name.StartsWith("Custom_Ridgeside_", StringComparison.OrdinalIgnoreCase))
            return "RSV:" + name["Custom_Ridgeside_".Length..];
        if (name.StartsWith("Custom_EastScarp_", StringComparison.OrdinalIgnoreCase))
            return "ES:" + name["Custom_EastScarp_".Length..];
        if (name.StartsWith("Custom_", StringComparison.OrdinalIgnoreCase))
            return name["Custom_".Length..];
        return name;
    }

    private static List<string> CollectUnvisitedKeyAreas(Farmer player)
    {
        string[] keyLocations =
        {
            "Woods", "Desert", "Sewer", "BugLand", "WitchSwamp", "IslandSouth",
            "Custom_Ridgeside_RidgesideVillage", "Custom_Ridgeside_Ridge", "Custom_Ridgeside_RidgeForest",
            "Custom_EastScarp_Village", "Custom_EastScarp_DeepDark",
            "Custom_AuroraVineyard", "Custom_ShearwaterBridge", "Custom_CrimsonBadlands", "Custom_Highlands"
        };

        var unvisited = new List<string>();
        foreach (string locName in keyLocations)
        {
            if (Game1.getLocationFromName(locName) is not null && !player.locationsVisited.Contains(locName))
                unvisited.Add(ShortRegionName(locName));
        }
        return unvisited;
    }

    private static List<RegionForageEntry> CollectWorldForage()
    {
        var list = new List<RegionForageEntry>();
        Farmer player = Game1.player;
        foreach (var loc in Game1.locations)
        {
            if (loc is null || !loc.IsOutdoors || loc is Farm || loc.Name == "IslandWest" || !IsLocationUnlocked(loc, player))
                continue;

            int forage = 0;
            int spots = 0;
            foreach (var obj in loc.objects.Values)
            {
                if (obj is null)
                    continue;
                if (obj.QualifiedItemId is "(O)590" or "(O)SeedSpot")
                    spots++;
                else if (obj.IsSpawnedObject)
                    forage++;
            }

            if (forage > 0 || spots > 0)
                list.Add(new RegionForageEntry(ShortRegionName(loc.Name), forage, spots));
        }

        return list
            .OrderByDescending(r => r.ForageCount + r.ArtifactSpots)
            .ToList();
    }

    private static (bool Available, int CompletedCount, List<BundleEntry> Incomplete) CollectCommunityCenterBundles()
    {
        try
        {
            if (Game1.getLocationFromName("CommunityCenter") is not CommunityCenter cc)
                return (false, 0, new List<BundleEntry>());

            var bundleData = Game1.netWorldState.Value.BundleData;
            var incompleteBundles = new List<BundleEntry>();
            int completedCount = 0;

            foreach (var entry in bundleData)
            {
                string[] keyParts = entry.Key.Split('/');
                if (keyParts.Length < 2 || !int.TryParse(keyParts[1], out int bundleId))
                    continue;

                string area = keyParts[0];
                string[] valParts = entry.Value.Split('/');
                if (valParts.Length < 3)
                    continue;

                string bundleName = valParts[0];
                int neededCount = valParts.Length > 4 && int.TryParse(valParts[4], out int nc) ? nc : 0;

                if (cc.isBundleComplete(bundleId))
                {
                    completedCount++;
                    continue;
                }

                int areaNum = CommunityCenter.getAreaNumberFromName(area);
                if (areaNum >= 0 && !cc.shouldNoteAppearInArea(areaNum))
                    continue;

                string[] tokens = valParts[2].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var missingOptions = new List<string>();
                var missingItemIds = new List<string>();
                var missingSlots = new List<BundleMissingSlot>();
                int filledCount = 0;
                cc.bundles.TryGetValue(bundleId, out bool[]? slots);

                for (int i = 0; i + 2 < tokens.Length; i += 3)
                {
                    int slotIndex = i / 3;
                    bool isFilled = slots is not null && slotIndex < slots.Length && slots[slotIndex];
                    if (isFilled)
                    {
                        filledCount++;
                        continue;
                    }

                    string itemId = tokens[i];
                    string qty = tokens[i + 1];
                    string qual = tokens[i + 2];
                    if (itemId != "-1")
                        missingItemIds.Add(itemId);
                    string itemName = itemId == "-1"
                        ? "Gold"
                        : DisambiguateEggName(itemId, ItemRegistry.GetData(itemId)?.DisplayName ?? itemId);
                    missingOptions.Add($"{itemName} x{qty}" + (qual != "0" ? $" (q{qual})" : ""));
                    int.TryParse(qty, out int qtyNum);
                    int.TryParse(qual, out int qualNum);
                    missingSlots.Add(new BundleMissingSlot(
                        itemId == "-1" ? "-1" : FishGuideBuilder.NormalizeQid(itemId),
                        itemName,
                        Math.Max(1, qtyNum),
                        Math.Max(0, qualNum)));
                }

                incompleteBundles.Add(new BundleEntry(
                    area,
                    bundleName,
                    filledCount,
                    neededCount > 0 ? neededCount : missingOptions.Count + filledCount,
                    missingOptions,
                    missingItemIds,
                    missingSlots));
            }

            return (true, completedCount, incompleteBundles);
        }
        catch
        {
            return (false, 0, new List<BundleEntry>());
        }
    }

    private static (int Axe, int Pickaxe, int WateringCan, int Hoe, int Pan, string? BestRodSummary) CollectToolDetails(
        Farmer player,
        IEnumerable<GameLocation> locations)
    {
        int maxAxe = -1;
        int maxPick = -1;
        int maxCan = -1;
        int maxHoe = -1;
        int maxPan = -1;
        FishingRod? bestRod = null;

        void ScanItem(Item? item)
        {
            if (item is Axe axe && axe.UpgradeLevel > maxAxe)
                maxAxe = axe.UpgradeLevel;
            else if (item is Pickaxe pick && pick.UpgradeLevel > maxPick)
                maxPick = pick.UpgradeLevel;
            else if (item is WateringCan can && can.UpgradeLevel > maxCan)
                maxCan = can.UpgradeLevel;
            else if (item is Hoe hoe && hoe.UpgradeLevel > maxHoe)
                maxHoe = hoe.UpgradeLevel;
            else if (item is Pan pan && pan.UpgradeLevel > maxPan)
                maxPan = pan.UpgradeLevel;
            else if (item is FishingRod rod && (bestRod is null || rod.UpgradeLevel > bestRod.UpgradeLevel))
                bestRod = rod;
        }

        void ScanItems(IEnumerable<Item?> items)
        {
            foreach (Item? item in items)
                ScanItem(item);
        }

        ScanItems(player.Items);
        ScanItem(player.toolBeingUpgraded.Value);

        foreach (var loc in locations)
        {
            foreach (var obj in loc.objects.Values)
            {
                if (obj is Chest chest && chest.playerChest.Value)
                    ScanItems(chest.Items);
            }
        }

        string? rodSummary = null;
        if (bestRod is not null)
        {
            var att = new List<string>();
            if (bestRod.GetBait() is { } bait)
                att.Add($"Bait: {bait.DisplayName} x{bait.Stack}");
            var tackles = bestRod.GetTackle().Where(t => t is not null).Select(t => t.DisplayName).ToList();
            if (tackles.Count > 0)
                att.Add($"Tackle: {string.Join(", ", tackles)}");

            rodSummary = att.Count > 0
                ? $"{bestRod.DisplayName} ({string.Join("; ", att)})"
                : bestRod.DisplayName;
        }

        return (maxAxe, maxPick, maxCan, maxHoe, maxPan, rodSummary);
    }

    private static List<(string Name, int X, int Y)> BuildFarmLandmarks(Farm farm)
    {
        var list = new List<(string Name, int X, int Y)>();
        Point house = farm.GetMainFarmHouseEntry();
        list.Add(("Farmhouse", house.X, house.Y));

        foreach (Building b in farm.buildings)
        {
            string type = b.buildingType.Value;
            if (type is "Greenhouse" or "Shipping Bin" or "Coop" or "Big Coop" or "Deluxe Coop" or "Barn" or "Big Barn" or "Deluxe Barn")
                list.Add((type, b.tileX.Value + b.tilesWide.Value / 2, b.tileY.Value + b.tilesHigh.Value / 2));
        }

        return list;
    }

    private static string DescribeRelativeToLandmarks(int x, int y, List<(string Name, int X, int Y)> landmarks)
    {
        if (landmarks.Count == 0)
            return $"@({x},{y})";

        var best = landmarks[0];
        int bestDist = int.MaxValue;
        foreach (var lm in landmarks)
        {
            int d = Math.Abs(x - lm.X) + Math.Abs(y - lm.Y);
            if (d < bestDist)
            {
                bestDist = d;
                best = lm;
            }
        }

        int dx = x - best.X;
        int dy = y - best.Y;
        var dirs = new List<string>();
        if (dy != 0) dirs.Add($"{Math.Abs(dy)}{(dy < 0 ? "N" : "S")}");
        if (dx != 0) dirs.Add($"{Math.Abs(dx)}{(dx < 0 ? "W" : "E")}");
        string rel = dirs.Count > 0 ? string.Join("/", dirs) + $" of {best.Name}" : $"at {best.Name}";
        return $"@({x},{y}) [{rel}]";
    }

    private sealed class FarmReachability
    {
        private readonly bool[,] _visited;
        private readonly int _width;
        private readonly int _height;
        private readonly List<(int X, int Y, string Needed)> _frontierClumps;
        private readonly string? _primaryFrontierBlocker;

        public FarmReachability(
            bool[,] visited,
            int width,
            int height,
            List<(int X, int Y, string Needed)> frontierClumps,
            string? primaryFrontierBlocker)
        {
            _visited = visited;
            _width = width;
            _height = height;
            _frontierClumps = frontierClumps;
            _primaryFrontierBlocker = primaryFrontierBlocker;
        }

        public (bool Reachable, string? BlockedBy) CheckTile(int tx, int ty)
        {
            for (int oy = -1; oy <= 1; oy++)
            {
                for (int ox = -1; ox <= 1; ox++)
                {
                    int nx = tx + ox;
                    int ny = ty + oy;
                    if (nx >= 0 && ny >= 0 && nx < _width && ny < _height && _visited[nx, ny])
                        return (true, null);
                }
            }

            if (_frontierClumps.Count > 0)
            {
                string? nearestNeeded = null;
                int bestDist = int.MaxValue;
                foreach (var (cx, cy, needed) in _frontierClumps)
                {
                    int d = Math.Abs(tx - cx) + Math.Abs(ty - cy);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        nearestNeeded = needed;
                    }
                }
                if (!string.IsNullOrEmpty(nearestNeeded))
                    return (false, nearestNeeded);
            }

            return (false, _primaryFrontierBlocker);
        }
    }

    private static FarmReachability ComputeFarmReachability(Farm farm, int axeLevel, int pickaxeLevel)
    {
        int width = farm.Map?.Layers[0]?.LayerWidth ?? 0;
        int height = farm.Map?.Layers[0]?.LayerHeight ?? 0;
        if (width <= 0 || height <= 0)
            return new FarmReachability(new bool[0, 0], 0, 0, new List<(int, int, string)>(), null);

        var visited = new bool[width, height];
        var blockedClumps = new Dictionary<(int X, int Y), string>();

        foreach (var clump in farm.resourceClumps)
        {
            string? needed = clump.parentSheetIndex.Value switch
            {
                ResourceClump.stumpIndex when axeLevel < 1 => "Copper Axe",
                ResourceClump.hollowLogIndex when axeLevel < 2 => "Steel Axe",
                ResourceClump.boulderIndex or ResourceClump.quarryBoulderIndex when pickaxeLevel < 2 => "Steel Pickaxe",
                ResourceClump.meteoriteIndex when pickaxeLevel < 3 => "Gold Pickaxe",
                _ => null
            };
            if (needed is null)
                continue;

            int cx = (int)clump.Tile.X;
            int cy = (int)clump.Tile.Y;
            for (int dy = 0; dy < clump.height.Value; dy++)
            {
                for (int dx = 0; dx < clump.width.Value; dx++)
                    blockedClumps[(cx + dx, cy + dy)] = needed;
            }
        }

        bool IsWalkable(int x, int y)
        {
            if (x < 0 || y < 0 || x >= width || y >= height)
                return false;
            if (blockedClumps.ContainsKey((x, y)))
                return false;
            if (farm.isWaterTile(x, y))
                return false;
            Vector2 v = new(x, y);
            if (farm.getBuildingAt(v) is not null)
                return false;
            return farm.isTilePassable(v);
        }

        var queue = new Queue<(int X, int Y)>();
        Point houseEntry = farm.GetMainFarmHouseEntry();
        Point mailbox = farm.GetMainMailboxPosition();
        var seeds = new[]
        {
            (houseEntry.X, houseEntry.Y),
            (houseEntry.X, houseEntry.Y + 1),
            (mailbox.X, mailbox.Y + 1)
        };

        foreach (var (sx, sy) in seeds)
        {
            if (sx >= 0 && sy >= 0 && sx < width && sy < height && !visited[sx, sy])
            {
                visited[sx, sy] = true;
                queue.Enqueue((sx, sy));
            }
        }

        var frontierBlockers = new HashSet<string>();
        var frontierClumpCoords = new List<(int X, int Y, string Needed)>();
        int[] dxs = { 1, -1, 0, 0 };
        int[] dys = { 0, 0, 1, -1 };

        while (queue.Count > 0)
        {
            var (x, y) = queue.Dequeue();
            for (int i = 0; i < 4; i++)
            {
                int nx = x + dxs[i];
                int ny = y + dys[i];
                if (nx < 0 || ny < 0 || nx >= width || ny >= height || visited[nx, ny])
                    continue;

                if (blockedClumps.TryGetValue((nx, ny), out string? blocker))
                {
                    frontierBlockers.Add(blocker);
                    frontierClumpCoords.Add((nx, ny, blocker));
                    continue;
                }

                if (!IsWalkable(nx, ny))
                    continue;

                visited[nx, ny] = true;
                queue.Enqueue((nx, ny));
            }
        }

        string? primaryBlocker = frontierBlockers.Count > 0 ? string.Join("/", frontierBlockers.OrderBy(b => b)) : null;
        return new FarmReachability(visited, width, height, frontierClumpCoords, primaryBlocker);
    }

    private static List<FarmSpotEntry> CollectFarmForageSpots(
        Farm farm,
        FarmReachability reach,
        List<(string Name, int X, int Y)> landmarks)
    {
        var list = new List<FarmSpotEntry>();
        foreach (var pair in farm.objects.Pairs)
        {
            var obj = pair.Value;
            if (obj is null)
                continue;

            bool isSpot = obj.QualifiedItemId is "(O)590" or "(O)SeedSpot";
            if (!obj.IsSpawnedObject && !isSpot)
                continue;

            int x = (int)pair.Key.X;
            int y = (int)pair.Key.Y;
            var (canReach, blockedBy) = reach.CheckTile(x, y);
            string where = DescribeRelativeToLandmarks(x, y, landmarks);
            list.Add(new FarmSpotEntry(obj.DisplayName, obj.Name, where, canReach, blockedBy));
        }

        return list
            .OrderByDescending(f => f.Reachable)
            .ThenBy(f => f.DisplayName)
            .ToList();
    }

    private static List<SpecialOrderEntry> CollectSpecialOrders(Farmer player)
    {
        var list = new List<SpecialOrderEntry>();
        foreach (SpecialOrder? o in player.team.specialOrders)
        {
            if (o is null)
                continue;

            string rawName = o.GetName();
            string name = ResolveLocalizedText(rawName);
            if (string.IsNullOrWhiteSpace(name) || name == rawName)
            {
                string? byRaw = TryLoadSpecialOrderString(o.questName.Value);
                if (!string.IsNullOrWhiteSpace(byRaw))
                    name = byRaw;
            }

            int daysLeft = Math.Max(0, o.dueDate.Value - Game1.Date.TotalDays);
            bool isUntimed = daysLeft >= 9000
                || (o.questKey.Value?.Contains("Untimed", StringComparison.OrdinalIgnoreCase) == true);

            var objectives = new List<string>();
            foreach (var obj in o.objectives)
            {
                if (obj is null)
                    continue;

                string desc = ResolveLocalizedText(obj.GetDescription());
                if (string.IsNullOrWhiteSpace(desc))
                    continue;

                int max = obj.GetMaxCount();
                if (obj.ShouldShowProgress() && max > 0)
                    desc += $" ({obj.GetCount()}/{max})";
                objectives.Add(desc);
            }

            list.Add(new SpecialOrderEntry(name, daysLeft, isUntimed, objectives));
        }

        return list;
    }

    private static List<FriendshipEntry> CollectFriendships(Farmer player, IEnumerable<GameLocation> locations)
    {
        var displayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var loc in locations)
        {
            foreach (NPC? npc in loc.characters)
            {
                if (npc is not null && !string.IsNullOrEmpty(npc.Name) && !string.IsNullOrWhiteSpace(npc.displayName))
                    displayNames.TryAdd(npc.Name, npc.displayName);
            }
        }

        if (Game1.characterData is not null)
        {
            foreach (var kv in Game1.characterData)
            {
                if (!displayNames.ContainsKey(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value?.DisplayName))
                {
                    string parsed = ResolveLocalizedText(kv.Value.DisplayName);
                    if (!string.IsNullOrWhiteSpace(parsed))
                        displayNames[kv.Key] = parsed;
                }
            }
        }

        return player.friendshipData.Pairs
            .Where(kv => kv.Value.Points > 0 || kv.Value.TalkedToToday || kv.Value.GiftsToday > 0 || kv.Value.GiftsThisWeek > 0)
            .OrderByDescending(kv => kv.Value.Points)
            .ThenBy(kv => displayNames.GetValueOrDefault(kv.Key, kv.Key), StringComparer.OrdinalIgnoreCase)
            .Select(kv => new FriendshipEntry(
                displayNames.GetValueOrDefault(kv.Key, kv.Key),
                kv.Value.Points / 250,
                kv.Value.Points,
                kv.Value.Status.ToString(),
                kv.Value.TalkedToToday,
                kv.Value.GiftsToday,
                kv.Value.GiftsThisWeek))
            .ToList();
    }

    private static List<QuestEntry> CollectQuests(Farmer player)
    {
        var result = new List<QuestEntry>();
        foreach (Quest? q in player.questLog)
        {
            if (q is null || q.completed.Value)
                continue;

            string? reqId = q switch
            {
                ItemDeliveryQuest idq => idq.ItemId.Value,
                FishingQuest fq => fq.ItemId.Value,
                ResourceCollectionQuest rcq => rcq.ItemId.Value,
                LostItemQuest liq => liq.ItemId.Value,
                _ => null
            };

            string? reqName = null;
            string? cleanId = null;
            if (!string.IsNullOrWhiteSpace(reqId))
            {
                string qid = FishGuideBuilder.NormalizeQid(reqId);
                cleanId = FishGuideBuilder.StripObjectPrefix(qid);
                reqName = ItemRegistry.GetData(qid)?.DisplayName ?? cleanId;
            }

            result.Add(new QuestEntry(
                ResolveLocalizedText(q.questTitle),
                ResolveLocalizedText(q.currentObjective),
                q.daysLeft.Value,
                reqName,
                cleanId));
        }

        return result;
    }

    internal static string ResolveLocalizedText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        string text = TokenParser.ParseText(raw) ?? raw;

        if (text.Length >= 3 && text[0] == '[' && text[^1] == ']' && !text.Contains(' '))
        {
            string? fromKey = TryLoadSpecialOrderString(text);
            if (!string.IsNullOrWhiteSpace(fromKey))
                text = fromKey;
        }
        else
        {
            string? loaded = TryLoadAssetString(text);
            if (!string.IsNullOrWhiteSpace(loaded))
                text = TokenParser.ParseText(loaded) ?? loaded;
        }

        return text;
    }

    private static string? TryLoadSpecialOrderString(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        string trimmed = raw.Trim();
        if (trimmed.Length >= 3 && trimmed[0] == '[' && trimmed[^1] == ']')
            trimmed = trimmed[1..^1];

        string? loaded = TryLoadAssetString(trimmed.Contains(':') ? trimmed : $"Strings\\SpecialOrderStrings:{trimmed}");
        if (string.IsNullOrWhiteSpace(loaded))
            return null;

        return TokenParser.ParseText(loaded) ?? loaded;
    }

    private static string? TryLoadAssetString(string key)
    {
        int colon = key.IndexOf(':');
        if (colon <= 0)
            return null;

        string asset = key[..colon].Replace('/', '\\');
        if (!asset.StartsWith("Strings\\", StringComparison.OrdinalIgnoreCase)
            && !asset.StartsWith("Data\\", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string normalized = asset + key[colon..];
        try
        {
            return Game1.content.LoadStringReturnNullIfNotFound(normalized);
        }
        catch
        {
            return null;
        }
    }

    private static string? CollectFestivalName()
    {
        try
        {
            if (Utility.isFestivalDay())
            {
                var dates = DataLoader.Festivals_FestivalDates(Game1.content);
                string key = $"{Game1.currentSeason}{Game1.dayOfMonth}";
                if (dates.TryGetValue(key, out string? name) && !string.IsNullOrWhiteSpace(name))
                    return ResolveLocalizedText(name);
            }

            if (Utility.TryGetPassiveFestivalDataForDay(Game1.dayOfMonth, Game1.season, null, out _, out var pfData)
                && pfData is not null
                && !string.IsNullOrWhiteSpace(pfData.DisplayName))
            {
                return ResolveLocalizedText(pfData.DisplayName);
            }
        }
        catch
        {
            // Ignore malformed mod festival data
        }

        return null;
    }

    private static List<string> CollectUpcomingBirthdays(IEnumerable<GameLocation> locations)
    {
        var result = new List<(int Diff, string Label)>();
        if (Game1.characterData is null)
            return new List<string>();

        var liveNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var loc in locations)
        {
            foreach (NPC? npc in loc.characters)
            {
                if (npc is not null && !string.IsNullOrEmpty(npc.Name) && !string.IsNullOrWhiteSpace(npc.displayName))
                    liveNames.TryAdd(npc.Name, npc.displayName);
            }
        }

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

            string displayName = liveNames.GetValueOrDefault(kv.Key, string.Empty);
            if (string.IsNullOrWhiteSpace(displayName))
                displayName = !string.IsNullOrWhiteSpace(cd.DisplayName) ? ResolveLocalizedText(cd.DisplayName) : kv.Key;

            string label = diff == 0 ? $"{displayName} (today)" : $"{displayName} (+{diff}d)";
            result.Add((diff, label));
        }

        return result.OrderBy(x => x.Diff).ThenBy(x => x.Label, StringComparer.OrdinalIgnoreCase).Select(x => x.Label).ToList();
    }

    private static string? GetQueenOfSauceNote(Farmer player)
    {
        try
        {
            int mod7 = Game1.dayOfMonth % 7;
            if (mod7 == 0)
            {
                int episode = (int)(Game1.stats.DaysPlayed % 224 / 7);
                if (episode <= 0)
                    episode = 32;

                var channel = DataLoader.Tv_CookingChannel(Game1.content);
                if (channel.TryGetValue(episode.ToString(), out string? raw) && !string.IsNullOrWhiteSpace(raw))
                {
                    string recipeKey = raw.Split('/')[0];
                    string display = recipeKey;
                    var recipes = DataLoader.CookingRecipes(Game1.content);
                    if (recipes.TryGetValue(recipeKey, out string? recRaw))
                    {
                        string[] parts = recRaw.Split('/');
                        if (parts.Length > 4 && !string.IsNullOrWhiteSpace(parts[4]))
                            display = ResolveLocalizedText(parts[4]);
                    }
                    bool known = player.cookingRecipes.ContainsKey(recipeKey);
                    return $"{display} ({(known ? "known" : "NEW!")})";
                }
            }
            else if (mod7 == 3)
            {
                return "Rerun today";
            }
        }
        catch
        {
            // Ignore TV data issues
        }

        return null;
    }

    private static List<string> CollectActiveBuffs(Farmer player)
    {
        var list = new List<string>();
        foreach (var buff in player.buffs.AppliedBuffs.Values)
        {
            if (buff is null)
                continue;

            string name = !string.IsNullOrWhiteSpace(buff.displayName) ? buff.displayName : buff.id;
            if (string.IsNullOrWhiteSpace(name))
                continue;

            if (buff.millisecondsDuration > 0 && buff.millisecondsDuration < int.MaxValue / 2)
            {
                int secs = (int)Math.Ceiling(buff.millisecondsDuration / 1000.0);
                string dur = secs >= 60 ? $"{secs / 60}m{secs % 60:D2}s" : $"{secs}s";
                list.Add($"{name} ({dur})");
            }
            else
            {
                list.Add(name);
            }
        }
        return list;
    }

    private static (int Unlocked, int Total, List<string> Masteries, List<string> Books) CollectPowersAndMastery(Farmer player)
    {
        int unlocked = 0;
        int total = 0;
        var masteries = new List<string>();
        var books = new List<string>();

        try
        {
            var powers = DataLoader.Powers(Game1.content);
            total = powers.Count;
            foreach (var kv in powers)
            {
                var pData = kv.Value;
                if (pData is null)
                    continue;

                bool isUnlocked = !string.IsNullOrWhiteSpace(pData.UnlockedCondition)
                    && GameStateQuery.CheckConditions(pData.UnlockedCondition, Game1.currentLocation, player);
                if (!isUnlocked)
                    continue;

                unlocked++;
                string name = !string.IsNullOrWhiteSpace(pData.DisplayName) ? ResolveLocalizedText(pData.DisplayName) : kv.Key;
                if (kv.Key.Contains("Mastery", StringComparison.OrdinalIgnoreCase))
                    masteries.Add(name);
                else if (kv.Key.StartsWith("Book_", StringComparison.OrdinalIgnoreCase))
                    books.Add(name);
            }
        }
        catch
        {
            // Ignore malformed power data
        }

        return (unlocked, total, masteries, books);
    }

    private static (int Completed, int Total, List<string> Incomplete) CollectMonsterGoals(Farmer player)
    {
        int completed = 0;
        int total = 0;
        var incomplete = new List<string>();

        try
        {
            var quests = DataLoader.MonsterSlayerQuests(Game1.content);
            total = quests.Count;
            foreach (var kv in quests)
            {
                var q = kv.Value;
                if (q is null || q.Targets is null || q.Targets.Count == 0)
                    continue;

                int killed = 0;
                foreach (string target in q.Targets)
                    killed += player.stats.getMonstersKilled(target);

                if (killed >= q.Count)
                {
                    completed++;
                }
                else
                {
                    string name = !string.IsNullOrWhiteSpace(q.DisplayName) ? ResolveLocalizedText(q.DisplayName) : kv.Key;
                    incomplete.Add($"{name} {killed}/{q.Count}");
                }
            }
        }
        catch
        {
            // Ignore malformed monster slayer data
        }

        return (completed, total, incomplete);
    }

    private static string CollectCollectionsSummary(Farmer player, int caughtFish, int totalFish)
    {
        int totalCooking = 0;
        int knownCooking = 0;
        try
        {
            var cook = DataLoader.CookingRecipes(Game1.content);
            totalCooking = cook.Count;
            knownCooking = cook.Keys.Count(k => player.cookingRecipes.ContainsKey(k));
        }
        catch { }

        int totalCrafting = 0;
        int knownCrafting = 0;
        try
        {
            var craft = DataLoader.CraftingRecipes(Game1.content);
            totalCrafting = craft.Count;
            knownCrafting = craft.Keys.Count(k => player.craftingRecipes.ContainsKey(k));
        }
        catch { }

        int shippedCount = player.basicShipped.Count();
        return $"Fish {caughtFish}/{totalFish} | Shipped {shippedCount} | Cooking {knownCooking}/{totalCooking} | Crafting {knownCrafting}/{totalCrafting}";
    }

    internal sealed record OwnedSource(string SourceLabel, int Stack, int Quality, string DisplayName);

    internal sealed class OwnedInventoryIndex
    {
        private readonly Dictionary<string, List<OwnedSource>> _byQid = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<(string Qid, ItemEntry Item, string Source)> _storageItems = new();

        public OwnedInventoryIndex(
            List<ItemEntry> inventory,
            List<ChestEntry> chests,
            List<BuildingEntry> buildings,
            List<ItemEntry> farmCaveReadyItems,
            List<FruitTreeEntry> fruitTrees,
            List<CropEntry> crops)
        {
            foreach (ItemEntry item in inventory)
                AddStorageItem(item, "Envanter");

            foreach (ChestEntry chest in chests)
            {
                string label = FormatChestSourceLabel(chest);
                foreach (ItemEntry item in chest.Items)
                    AddStorageItem(item, label);
            }

            foreach (BuildingEntry b in buildings)
            {
                string bName = TranslateBuildingTypeTr(b.BuildingType);
                foreach (ItemEntry item in b.AutoGrabberItems)
                    AddItemEntry(item, $"Otomatik Toplayıcı ({bName})");

                if (b.JunimoHut is { OutputItems.Count: > 0 } hut)
                {
                    foreach (ItemEntry item in hut.OutputItems)
                        AddItemEntry(item, "Junimo Kulübesi");
                }
            }

            foreach (ItemEntry item in farmCaveReadyItems)
                AddItemEntry(item, "Çiftlik Mağarası (Toplanmaya hazır)");

            foreach (FruitTreeEntry tree in fruitTrees.Where(t => t.Reachable && t.FruitsReady > 0 && !string.IsNullOrEmpty(t.FruitItemId)))
            {
                string where = string.IsNullOrEmpty(tree.Where) ? string.Empty : " " + FormatTurkishWhere(tree.Where);
                string locLabel = $"{TranslateRegionNameTr(tree.Location)} Ağacı{where}".Trim();
                AddRaw(tree.FruitItemId, locLabel, tree.FruitsReady, 0, tree.Tree);
            }

            foreach (CropEntry crop in crops.Where(c => c.ReadyToHarvestCount > 0 && !string.IsNullOrEmpty(c.HarvestItemId)))
            {
                string locLabel = $"{TranslateRegionNameTr(crop.Location)} Tarlası (Hasada hazır)";
                AddRaw(crop.HarvestItemId, locLabel, crop.ReadyToHarvestCount, 0, crop.DisplayName);
            }
        }

        private void AddStorageItem(ItemEntry item, string sourceLabel)
        {
            if (string.IsNullOrEmpty(item.QualifiedId))
                return;
            string qid = FishGuideBuilder.NormalizeQid(item.QualifiedId);
            _storageItems.Add((qid, item, sourceLabel));
            AddRaw(qid, sourceLabel, item.Stack, item.Quality, item.DisplayName);
        }

        private void AddItemEntry(ItemEntry item, string sourceLabel)
        {
            if (string.IsNullOrEmpty(item.QualifiedId))
                return;
            AddRaw(FishGuideBuilder.NormalizeQid(item.QualifiedId), sourceLabel, item.Stack, item.Quality, item.DisplayName);
        }

        private void AddRaw(string qid, string sourceLabel, int stack, int quality, string displayName)
        {
            if (!_byQid.TryGetValue(qid, out var list))
            {
                list = new List<OwnedSource>();
                _byQid[qid] = list;
            }
            list.Add(new OwnedSource(sourceLabel, stack, quality, displayName));
        }

        public (int Total, List<string> Sources) FindMatches(string qid, int minQuality = 0)
        {
            string norm = FishGuideBuilder.NormalizeQid(qid);
            if (!_byQid.TryGetValue(norm, out var list))
                return (0, new List<string>());

            var matching = list.Where(x => x.Quality >= minQuality).ToList();
            if (matching.Count == 0)
                return (0, new List<string>());

            int total = matching.Sum(x => x.Stack);
            var sources = matching
                .GroupBy(x => x.SourceLabel)
                .Select(g => $"{g.Key} (x{g.Sum(x => x.Stack)})")
                .ToList();
            return (total, sources);
        }

        public int GetStorageCount(string qid, int minQuality = 0)
        {
            string norm = FishGuideBuilder.NormalizeQid(qid);
            int total = 0;
            foreach (var entry in _storageItems)
            {
                if (entry.Qid.Equals(norm, StringComparison.OrdinalIgnoreCase) && entry.Item.Quality >= minQuality)
                    total += entry.Item.Stack;
            }
            return total;
        }

        public (int Total, List<string> Sources) FindStorageMatches(string qid, int minQuality = 0)
        {
            string norm = FishGuideBuilder.NormalizeQid(qid);
            var matching = _storageItems
                .Where(x => x.Qid.Equals(norm, StringComparison.OrdinalIgnoreCase) && x.Item.Quality >= minQuality)
                .ToList();
            if (matching.Count == 0)
                return (0, new List<string>());

            int total = matching.Sum(x => x.Item.Stack);
            var sources = matching
                .GroupBy(x => x.Source)
                .Select(g => $"{g.Key} (x{g.Sum(x => x.Item.Stack)})")
                .ToList();
            return (total, sources);
        }

        public string? FindToolSourceOutsideInventory(string englishToolName)
        {
            foreach (var entry in _storageItems)
            {
                if (entry.Source == "Envanter")
                    continue;
                if (entry.Item.Name.Contains(englishToolName, StringComparison.OrdinalIgnoreCase))
                    return entry.Source;
            }
            return null;
        }

        public List<(string SeedName, string HarvestName, int GrowthDays, int TotalStack, string Sources)> FindSeasonalSeeds(
            Season currentSeason,
            Dictionary<string, CropData> cropData)
        {
            var grouped = new Dictionary<string, (string SeedName, string HarvestName, int GrowthDays, int Total, HashSet<string> Sources)>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in _storageItems)
            {
                string shortId = FishGuideBuilder.StripObjectPrefix(entry.Qid);
                if (!cropData.TryGetValue(shortId, out CropData? cd) || cd is null)
                    continue;
                if (cd.Seasons is null || !cd.Seasons.Contains(currentSeason))
                    continue;

                int growthDays = cd.DaysInPhase?.Sum() ?? 0;
                string harvestQid = FishGuideBuilder.NormalizeQid(cd.HarvestItemId ?? string.Empty);
                string harvestName = ItemRegistry.GetData(harvestQid)?.DisplayName ?? cd.HarvestItemId ?? entry.Item.DisplayName;

                if (!grouped.TryGetValue(entry.Qid, out var cur))
                    cur = (entry.Item.DisplayName, harvestName, growthDays, 0, new HashSet<string>(StringComparer.Ordinal));

                cur.Sources.Add($"{entry.Source} x{entry.Item.Stack}");
                grouped[entry.Qid] = (cur.SeedName, cur.HarvestName, cur.GrowthDays, cur.Total + entry.Item.Stack, cur.Sources);
            }

            return grouped.Values
                .OrderByDescending(v => v.GrowthDays)
                .ThenBy(v => v.SeedName, StringComparer.OrdinalIgnoreCase)
                .Select(v => (v.SeedName, v.HarvestName, v.GrowthDays, v.Total, string.Join(", ", v.Sources)))
                .ToList();
        }
    }

    private static (List<string> MustDoToday, List<string> DailyRoutine, List<string> StrategicInsights, List<CropProfitEntry> TopProfitableCrops) BuildActionSections(
        Farmer player,
        bool greenhouseUnlocked,
        bool islandUnlocked,
        bool desertUnlocked,
        bool isRainingOnFarm,
        string weatherForTomorrow,
        bool isFestivalToday,
        string? festivalName,
        bool isBooksellerToday,
        string? queenOfSauceNote,
        string? birthdayToday,
        string? toolBeingUpgraded,
        int daysLeftForToolUpgrade,
        List<string> museumReadyToDonate,
        List<ShopItemEntry> travelingCart,
        List<RegionForageEntry> worldForage,
        List<ItemEntry> inventory,
        List<CropEntry> crops,
        List<FruitTreeEntry> fruitTrees,
        List<FarmSpotEntry> farmForageSpots,
        List<ItemEntry> farmCaveReadyItems,
        List<BuildingEntry> buildings,
        List<MachineEntry> machines,
        Dictionary<string, int> objectsSummary,
        List<ChestEntry> chests,
        bool ccAvailable,
        List<BundleEntry> ccIncomplete,
        List<QuestEntry> quests,
        List<SpecialOrderEntry> specialOrders,
        List<FriendshipEntry> friendships,
        FishGuideSnapshot? fishGuide,
        OwnedInventoryIndex owned,
        WorldIntelligenceSnapshot worldIntel)
    {
        Dictionary<string, CropData> cropData;
        try
        {
            cropData = DataLoader.Crops(Game1.content);
        }
        catch
        {
            cropData = new Dictionary<string, CropData>();
        }

        // Build reverse map: HarvestItemQid -> (SeedQid, SeedName, CropData)
        var harvestToCrop = new Dictionary<string, (string SeedQid, string SeedName, CropData Data)>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in cropData)
        {
            if (kv.Value is null || string.IsNullOrWhiteSpace(kv.Value.HarvestItemId))
                continue;
            string harvestQid = FishGuideBuilder.NormalizeQid(kv.Value.HarvestItemId);
            if (!harvestToCrop.ContainsKey(harvestQid))
            {
                string seedQid = FishGuideBuilder.NormalizeQid(kv.Key);
                string seedName = ItemRegistry.GetData(seedQid)?.DisplayName ?? kv.Key;
                harvestToCrop[harvestQid] = (seedQid, seedName, kv.Value);
            }
        }

        int daysLeft = Math.Max(0, 28 - Game1.dayOfMonth);
        var mustDo = BuildMustDoToday(
            player,
            greenhouseUnlocked,
            daysLeft,
            weatherForTomorrow,
            isFestivalToday,
            festivalName,
            isBooksellerToday,
            queenOfSauceNote,
            birthdayToday,
            toolBeingUpgraded,
            daysLeftForToolUpgrade,
            buildings,
            museumReadyToDonate,
            travelingCart,
            crops,
            ccAvailable,
            ccIncomplete,
            quests,
            friendships,
            fishGuide,
            owned,
            cropData,
            harvestToCrop,
            worldIntel);

        var routine = BuildDailyRoutine(
            player,
            isRainingOnFarm,
            worldForage,
            crops,
            fruitTrees,
            farmForageSpots,
            farmCaveReadyItems,
            buildings,
            machines,
            owned);

        var topProfitableCrops = BuildTopProfitableCrops(
            player,
            daysLeft,
            desertUnlocked,
            travelingCart,
            owned,
            cropData);

        var strategic = BuildStrategicInsights(
            player,
            greenhouseUnlocked,
            islandUnlocked,
            daysLeft,
            crops,
            buildings,
            machines,
            objectsSummary,
            ccAvailable,
            ccIncomplete,
            specialOrders,
            owned,
            cropData,
            topProfitableCrops,
            worldIntel);

        return (mustDo, routine, strategic, topProfitableCrops);
    }

    private static List<string> BuildMustDoToday(
        Farmer player,
        bool greenhouseUnlocked,
        int daysLeftInSeason,
        string weatherForTomorrow,
        bool isFestivalToday,
        string? festivalName,
        bool isBooksellerToday,
        string? queenOfSauceNote,
        string? birthdayToday,
        string? toolBeingUpgraded,
        int daysLeftForToolUpgrade,
        List<BuildingEntry> buildings,
        List<string> museumReadyToDonate,
        List<ShopItemEntry> travelingCart,
        List<CropEntry> crops,
        bool ccAvailable,
        List<BundleEntry> ccIncomplete,
        List<QuestEntry> quests,
        List<FriendshipEntry> friendships,
        FishGuideSnapshot? fishGuide,
        OwnedInventoryIndex owned,
        Dictionary<string, CropData> cropData,
        Dictionary<string, (string SeedQid, string SeedName, CropData Data)> harvestToCrop,
        WorldIntelligenceSnapshot worldIntel)
    {
        var list = new List<string>();
        string seasonTr = FishGuideBuilder.TranslateSeason(Game1.currentSeason).Replace("Guz", "Güz").Replace("Kis", "Kış");

        // 1. Season timing & owned seeds
        if (Game1.dayOfMonth <= 3)
        {
            list.Add($"Mevsim Başı ({seasonTr} {Game1.dayOfMonth}): Uzun sürede yetişen mevsimlik ekinleri bugün ekmek sezon boyunca maksimum hasat kazandırır.");
            var ownedSeeds = owned.FindSeasonalSeeds(Game1.season, cropData);
            if (ownedSeeds.Count > 0)
            {
                string seedSummary = string.Join("; ", ownedSeeds.Select(s =>
                    $"{s.SeedName} x{s.TotalStack} ({s.GrowthDays}g -> {s.HarvestName}) [{s.Sources}]"));
                list.Add($"Sandıklarda/Envanterde Hazır {seasonTr} Tohumları (Yeniden satın alma): {seedSummary}.");
            }
        }
        else if (daysLeftInSeason <= 4)
        {
            list.Add($"Mevsim Sonu Uyarısı ({daysLeftInSeason} gün kaldı): Açık alana {daysLeftInSeason} günden uzun sürede büyüyen tohum ekmeyin.");
        }

        // 2. Calendar / TV / Traveling Cart / Birthday / Tool Upgrade / Tomorrow Weather
        if (isFestivalToday)
            list.Add($"Bugün Festival Günü ({festivalName ?? "Festival"}): Dükkanlar kapalı olabilir; etkinlik saatini ve yerini kaçırma.");
        if (isBooksellerToday)
        {
            var unreadBooks = worldIntel.BooksellerStock.Where(b => !b.AlreadyRead).Take(5).Select(b => $"{b.DisplayName} ({b.Price:N0}g)").ToList();
            string bookDetail = unreadBooks.Count > 0 ? $" | Okunmamış kitaplar: {string.Join(", ", unreadBooks)}" : "";
            list.Add($"Kitapçı Bugün Kasabada: Yeni güç/beceri kitapları için mutlaka uğra{bookDetail}.");
        }

        if (!string.IsNullOrWhiteSpace(queenOfSauceNote))
        {
            if (queenOfSauceNote.Contains("NEW", StringComparison.OrdinalIgnoreCase))
                list.Add($"TV — Sos Kraliçesi (YENİ TARİF): Bugün televizyondan yeni yemek tarifini öğren ({queenOfSauceNote}).");
            else if (queenOfSauceNote.Contains("Rerun", StringComparison.OrdinalIgnoreCase))
                list.Add("TV — Sos Kraliçesi (Tekrar Yayını): Eksik yemek tarifi kalmışsa televizyonu kontrol et.");
        }

        if (travelingCart.Count > 0)
        {
            var ccMissingIds = new HashSet<string>(
                ccIncomplete.SelectMany(b => b.MissingSlots ?? new List<BundleMissingSlot>())
                    .Where(sl => sl.ItemId != "-1")
                    .Select(sl => sl.ItemId),
                StringComparer.OrdinalIgnoreCase);

            var highlights = new List<string>();
            foreach (var item in travelingCart)
            {
                string normQid = string.IsNullOrEmpty(item.QualifiedId) ? string.Empty : FishGuideBuilder.NormalizeQid(item.QualifiedId);
                if (item.EnglishName.Contains("Rare Seed", StringComparison.OrdinalIgnoreCase))
                    highlights.Add($"{item.DisplayName} ({item.Price:N0}g — nadir tohum)");
                else if ((!string.IsNullOrEmpty(normQid) && ccMissingIds.Contains(normQid))
                    || ccIncomplete.Any(b => b.MissingSlots?.Any(sl =>
                        string.Equals(sl.DisplayName, item.DisplayName, StringComparison.OrdinalIgnoreCase)) == true))
                    highlights.Add($"{item.DisplayName} ({item.Price:N0}g — Halkevi eksiği!)");
            }

            string extra = highlights.Count > 0 ? $" | Öne çıkanlar: {string.Join(", ", highlights)}" : "";
            list.Add($"Gezgin Tüccar Bugün Açık (06:00–20:00, Kömürözü Ormanı): Halkevi veya nadir tohum fırsatları için kontrol et{extra}.");
        }

        if (!string.IsNullOrWhiteSpace(birthdayToday))
        {
            var todayBirthdays = worldIntel.BirthdayGifts.Where(b => b.DaysUntil == 0).ToList();
            if (todayBirthdays.Count > 0)
            {
                foreach (var bday in todayBirthdays)
                {
                    string status = bday.GiftedToday ? "Verildi" : "HENÜZ VERİLMEDİ";
                    string giftHint = bday.OwnedLovedGifts.Count > 0
                        ? $" | Sandıktaki Sevdiği Hediyeler: {string.Join(", ", bday.OwnedLovedGifts.Take(3))}"
                        : (bday.OwnedLikedGifts.Count > 0 ? $" | Beğendiği (Sandıkta): {string.Join(", ", bday.OwnedLikedGifts.Take(2))}" : "");
                    string locHint = !string.IsNullOrWhiteSpace(bday.CurrentLocation) ? $" | Konum: {bday.CurrentLocation}" : "";
                    list.Add($"Bugün Doğum Günü — {bday.DisplayName} ({bday.CurrentHearts} kalp, hediye: {status}){locHint}{giftHint}.");
                }
            }
            else
            {
                FriendshipEntry? fr = friendships.FirstOrDefault(f => f.Npc.Equals(birthdayToday, StringComparison.OrdinalIgnoreCase));
                string giftNote = fr is not null
                    ? $"{fr.Hearts} kalp, bugün hediye: {(fr.GiftsToday > 0 ? "Verildi" : "HENÜZ VERİLMEDİ")}"
                    : "henüz hediye verilmedi";
                list.Add($"Bugün Doğum Günü — {birthdayToday} ({giftNote}): Sevdiği bir hediye verirsen 8x arkadaşlık puanı alırsın.");
            }
        }

        // Ready-to-trigger heart / story events today (Vanilla + Mods)
        var triggerableToday = worldIntel.ReadyEvents
            .Where(e => e.LiveStatus is "ŞU AN TETİKLENEBİLİR" or "BUGÜN DAHA SONRA")
            .Take(4)
            .ToList();
        if (triggerableToday.Count > 0)
        {
            string evList = string.Join(" || ", triggerableToday.Select(e =>
                $"{e.NpcSummary} -> {e.LocationDisplayName} ({e.TimeWindow}, {e.LiveStatus})"));
            list.Add($"Tetiklenmeye Hazır Hikâye/Kalp Etkinlikleri ({worldIntel.ReadyEvents.Count(e => e.LiveStatus is "ŞU AN TETİKLENEBİLİR" or "BUGÜN DAHA SONRA")} bugün, tamamı live_world.md'de): {evList}");
        }

        // Daily Help Wanted & Untaken Special Order Boards
        if (worldIntel.DailyHelpWanted is { AlreadyAccepted: false } hw)
        {
            string ownedTag = hw.HasTargetItemInStorage
                ? $" [SANDIKTA HAZIR: {hw.StorageSources}]"
                : string.Empty;
            list.Add($"Pierre Önü Günlük İlan (Henüz Alınmadı): {hw.Title} — {hw.Objective}{ownedTag}");
        }

        var openOrders = worldIntel.AvailableSpecialOrders.Where(o => !o.BoardAlreadyAcceptedToday).ToList();
        if (openOrders.Count > 0)
        {
            var boardSummary = openOrders
                .GroupBy(o => o.BoardLabel)
                .Select(g => $"{g.Key}: {string.Join(" / ", g.Select(x => $"{x.Name} ({x.DurationText})"))}");
            list.Add($"Alınmamış Özel Sipariş Panoları ({openOrders.Count}): {string.Join(" || ", boardSummary)}");
        }

        // Data-driven Must-Do rules from advisor_rules.json (hot-reloaded without game restart)
        foreach (string ruleMsg in worldIntel.MustDoRules)
            list.Add(ruleMsg);

        if (!string.IsNullOrWhiteSpace(toolBeingUpgraded))
        {
            list.Add(daysLeftForToolUpgrade <= 0
                ? $"Demirci (Clint): {toolBeingUpgraded} yükseltmesi hazır, bugün teslim al!"
                : $"Demirci (Clint): {toolBeingUpgraded} yükseltmesi için {daysLeftForToolUpgrade} gün kaldı.");
        }

        if (player.daysUntilHouseUpgrade.Value > 0)
        {
            list.Add($"Marangoz (Robin): Çiftlik Evi Sv.{player.HouseUpgradeLevel + 1} yükseltmesi devam ediyor ({player.daysUntilHouseUpgrade.Value} gün kaldı).");
        }

        foreach (BuildingEntry b in buildings)
        {
            if (b.DaysOfConstructionLeft > 0)
                list.Add($"Marangoz (Robin): {TranslateBuildingTypeTr(b.BuildingType)} inşaatı devam ediyor ({b.DaysOfConstructionLeft} gün kaldı).");
            if (b.DaysUntilUpgrade > 0)
            {
                string targetTr = !string.IsNullOrWhiteSpace(b.UpgradeTarget)
                    ? $" -> {TranslateBuildingTypeTr(b.UpgradeTarget)}"
                    : "";
                list.Add($"Marangoz (Robin): {TranslateBuildingTypeTr(b.BuildingType)}{targetTr} yükseltmesi devam ediyor ({b.DaysUntilUpgrade} gün kaldı).");
            }
        }

        if (weatherForTomorrow is "Rain" or "Storm" or "GreenRain")
        {
            list.Add($"Yarınki Hava ({TranslateWeatherTr(weatherForTomorrow)}): Yarın yağmurlu olacağı için Sulama Kabı yükseltmesi veya yağmura özel balıklar için hazırlık yapılabilir.");
        }

        // 3. Community Center — Ready Right Now + Critical This Season
        if (ccAvailable && ccIncomplete.Count > 0)
        {
            var plantedCounts = crops
                .Where(c => !string.IsNullOrEmpty(c.HarvestItemId))
                .GroupBy(c => c.HarvestItemId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.Count), StringComparer.OrdinalIgnoreCase);

            var readyLines = new List<string>();
            var partialLines = new List<string>();
            var seasonalNeeds = new List<string>();

            foreach (var b in ccIncomplete)
            {
                string areaTr = TranslateBundleAreaTr(b.Area);
                var slots = b.MissingSlots ?? new List<BundleMissingSlot>();
                foreach (var slot in slots)
                {
                    if (slot.ItemId == "-1")
                    {
                        if (player.Money >= slot.Quantity)
                            readyLines.Add($"{areaTr} / {b.BundleName}: {slot.Quantity:N0}g (Bütçe yeterli)");
                        continue;
                    }

                    var (total, sources) = owned.FindMatches(slot.ItemId, slot.MinQuality);
                    string qualTag = slot.MinQuality >= 2 ? " [Altın+]" : "";
                    if (total >= slot.Quantity)
                    {
                        readyLines.Add($"{areaTr} / {b.BundleName} -> {slot.DisplayName} x{slot.Quantity}{qualTag} [Mevcut: {string.Join(", ", sources)}]");
                        continue;
                    }

                    if (total > 0)
                        partialLines.Add($"{areaTr} / {b.BundleName} -> {slot.DisplayName}{qualTag} ({total}/{slot.Quantity} mevcut: {string.Join(", ", sources)})");

                    if (harvestToCrop.TryGetValue(slot.ItemId, out var cropInfo)
                        && cropInfo.Data.Seasons is not null
                        && cropInfo.Data.Seasons.Contains(Game1.season))
                    {
                        int growthDays = cropInfo.Data.DaysInPhase?.Sum() ?? 0;
                        int planted = plantedCounts.GetValueOrDefault(slot.ItemId);
                        int seedOwned = owned.GetStorageCount(cropInfo.SeedQid);
                        bool canMature = greenhouseUnlocked || growthDays <= daysLeftInSeason;
                        string timing = canMature
                            ? $"{growthDays} günde yetişir"
                            : $"DİKKAT: {growthDays} gün sürüyor, mevsim bitimine {daysLeftInSeason} gün kaldı!";
                        string seedInfo = seedOwned > 0
                            ? $"Sandıkta {cropInfo.SeedName} x{seedOwned} var"
                            : "Pierre'den tohum alınmalı";
                        string plantInfo = planted > 0 ? $" | Tarlada x{planted} ekili" : "";
                        string fertNote = slot.MinQuality >= 2
                            ? " | Altın kalite için Kaliteli Gübre kullan"
                            : "";
                        seasonalNeeds.Add($"{slot.DisplayName} x{slot.Quantity}{qualTag} ({areaTr} / {b.BundleName}): {timing}; {seedInfo}{plantInfo}{fertNote}");
                    }
                }

                if (b.BundleName.Contains("Foraging", StringComparison.OrdinalIgnoreCase)
                    && b.BundleName.StartsWith(Game1.currentSeason, StringComparison.OrdinalIgnoreCase)
                    && slots.Count > 0)
                {
                    var unownedForage = slots
                        .Where(sl => owned.FindMatches(sl.ItemId, sl.MinQuality).Total < sl.Quantity)
                        .Select(sl => sl.DisplayName)
                        .ToList();
                    if (unownedForage.Count > 0)
                        seasonalNeeds.Add($"{seasonTr} Toplayıcılık ({areaTr} / {b.BundleName}): Doğadan topla -> {string.Join(", ", unownedForage)}");
                }
            }

            if (readyLines.Count > 0)
                list.Add($"Halkevi — Bugün Teslime Hazır ({readyLines.Count}): " + string.Join(" || ", readyLines));
            if (partialLines.Count > 0)
                list.Add($"Halkevi — Kısmen Hazır: " + string.Join(" || ", partialLines));
            if (seasonalNeeds.Count > 0)
                list.Add($"Halkevi — Bu Mevsim ({seasonTr}) İçin Kritik: " + string.Join(" || ", seasonalNeeds));
        }

        // 4. Museum donations
        if (museumReadyToDonate.Count > 0)
            list.Add($"Müze Bağışı Hazır ({museumReadyToDonate.Count}): {string.Join(", ", museumReadyToDonate)} (Günther'e 09:00–18:00 arası bağışla).");

        // 5. Quests feasibility check
        foreach (var q in quests)
        {
            if (string.IsNullOrWhiteSpace(q.RequiredItemId))
            {
                list.Add($"Aktif Görev — {q.Title}: {q.Objective}");
                continue;
            }

            string qid = FishGuideBuilder.NormalizeQid(q.RequiredItemId);
            var (total, sources) = owned.FindMatches(qid);
            if (total > 0)
            {
                list.Add($"Aktif Görev — {q.Title}: {q.RequiredItemName} teslime hazır! [{string.Join(", ", sources)}]");
                continue;
            }

            if (harvestToCrop.TryGetValue(qid, out var cropInfo)
                && cropInfo.Data.Seasons is not null
                && !cropInfo.Data.Seasons.Contains(Game1.season)
                && !greenhouseUnlocked)
            {
                string validSeasons = string.Join("/", cropInfo.Data.Seasons.Select(s =>
                    FishGuideBuilder.TranslateSeason(s.ToString()).Replace("Guz", "Güz").Replace("Kis", "Kış")));
                list.Add($"Aktif Görev — {q.Title}: {q.RequiredItemName} sandıklarda yok ve yalnızca {validSeasons} ürünü olduğu için şu an ekilemez (Gezgin Tüccar'ı veya kendi mevsimini/Sera'yı bekle).");
            }
            else
            {
                list.Add($"Aktif Görev — {q.Title}: {q.Objective}");
            }
        }

        // 6. Priority Fish catchable today vs waiting for rain
        if (fishGuide is { PriorityFish.Count: > 0 })
        {
            foreach (var f in fishGuide.PriorityFish.Where(x => !x.InStorageOrInventory))
            {
                var activeSpot = f.Spots.FirstOrDefault(sp => sp.SeasonMatchesNow && sp.WeatherMatchesToday);
                if (activeSpot is not null)
                    list.Add($"Öncelikli Balık Bugün Yakalanabilir: {f.DisplayName} -> {activeSpot.LocationDisplayName} ({activeSpot.TimeDisplay}, {activeSpot.LiveStatus}).");
                else
                {
                    var seasonalSpot = f.Spots.FirstOrDefault(sp => sp.SeasonMatchesNow);
                    if (seasonalSpot is not null)
                        list.Add($"Öncelikli Balık (Şart Bekliyor): {f.DisplayName} -> {seasonalSpot.LocationDisplayName} ({seasonalSpot.LiveStatus}).");
                }
            }
        }

        return list;
    }

    private static List<string> BuildDailyRoutine(
        Farmer player,
        bool isRainingOnFarm,
        List<RegionForageEntry> worldForage,
        List<CropEntry> crops,
        List<FruitTreeEntry> fruitTrees,
        List<FarmSpotEntry> farmForageSpots,
        List<ItemEntry> farmCaveReadyItems,
        List<BuildingEntry> buildings,
        List<MachineEntry> machines,
        OwnedInventoryIndex owned)
    {
        var list = new List<string>();

        // 1. Crops
        var readyCrops = crops.Where(c => c.ReadyToHarvestCount > 0).ToList();
        if (readyCrops.Count > 0)
        {
            list.Add("Hasat Edilecek Ekinler: " + string.Join(", ", readyCrops.Select(c =>
                $"{TranslateRegionNameTr(c.Location)}: {c.DisplayName} x{c.ReadyToHarvestCount}")));
        }

        var unwateredCrops = crops.Where(c => c.WateredCount < c.Count).ToList();
        if (unwateredCrops.Count > 0)
        {
            var items = new List<string>();
            foreach (var c in unwateredCrops)
            {
                int need = c.Count - c.WateredCount;
                string rainNote = isRainingOnFarm && !c.Location.Equals("Greenhouse", StringComparison.OrdinalIgnoreCase)
                    ? " (yağmur ıslatır)"
                    : "";
                items.Add($"{TranslateRegionNameTr(c.Location)}: {c.DisplayName} ({need}/{c.Count} sulanmamış{rainNote})");
            }
            list.Add("Sulama Kontrolü: " + string.Join("; ", items));
        }

        // 2. Fruit Trees (ONLY ready trees!)
        var readyTrees = fruitTrees.Where(t => t.Reachable && t.FruitsReady > 0).ToList();
        if (readyTrees.Count > 0)
        {
            var farmReady = readyTrees
                .Where(t => t.IsOnFarm)
                .Select(t => $"{t.Tree} {FormatTurkishWhere(t.Where)} ({t.FruitsReady} hazır)");
            var offFarmReady = readyTrees
                .Where(t => !t.IsOnFarm)
                .GroupBy(t => (t.Location, t.Tree))
                .Select(g => $"{TranslateRegionNameTr(g.Key.Location)} — {g.Key.Tree} x{g.Count()} ({g.Sum(x => x.FruitsReady)} hazır: {string.Join(", ", g.Select(x => x.Where))})");

            list.Add("Meyvesi Hazır Ağaçlar (Topla): " + string.Join(" | ", farmReady.Concat(offFarmReady)));
        }

        int emptyMatureCount = fruitTrees.Count(t => t.FruitsReady == 0 && t.DaysUntilMature == 0);
        int growingSaplingCount = fruitTrees.Count(t => t.FruitsReady == 0 && t.DaysUntilMature > 0);
        if (readyTrees.Count == 0 && (emptyMatureCount > 0 || growingSaplingCount > 0))
            list.Add($"Meyve Ağaçları: Bugün toplanacak hazır meyve yok ({emptyMatureCount} boş olgun ağaç, {growingSaplingCount} büyüyen fidan).");

        // 3. Farm Forage / Artifact Spots & Farm Cave
        var reachableSpots = farmForageSpots.Where(f => f.Reachable).ToList();
        if (reachableSpots.Count > 0)
        {
            list.Add("Çiftlikte Toplanacak / Kazılacak Noktalar: " + string.Join(" | ", reachableSpots.Select(f =>
                $"{f.DisplayName} {FormatTurkishWhere(f.Where)}")));
        }

        var blockedSpots = farmForageSpots.Where(f => !f.Reachable).ToList();
        if (blockedSpots.Count > 0)
        {
            list.Add($"Önü Kapalı Çiftlik Noktaları ({blockedSpots.Count}): " + string.Join(", ", blockedSpots.Select(f =>
                $"{f.DisplayName} ({TranslateBlockerTr(f.BlockedBy)})")));
        }

        if (farmCaveReadyItems.Count > 0)
        {
            list.Add("Çiftlik Mağarası (Toplanmaya Hazır): " + string.Join(", ", farmCaveReadyItems.Select(i =>
                i.Stack > 1 ? $"{i.DisplayName} x{i.Stack}" : i.DisplayName)));
        }
        else
        {
            var mBoxes = machines.FirstOrDefault(m => m.Name.Equals("Mushroom Box", StringComparison.OrdinalIgnoreCase));
            if (mBoxes is not null && mBoxes.Working > 0)
                list.Add($"Çiftlik Mağarası: Hazır mantar yok; {mBoxes.Working} Mantar Kutusu çalışıyor.");
        }

        // 4. Animal Care & Tool Check
        foreach (var b in buildings)
        {
            if (b.Animals.Count == 0 && b.AutoGrabberItems.Count == 0 && b.JunimoHut is null && b.SlimeHutch is null && b.FishPond is null)
                continue;

            string bTr = TranslateBuildingTypeTr(b.BuildingType);
            if (b.FishPond is { } fp)
            {
                var notes = new List<string>();
                if (!string.IsNullOrEmpty(fp.OutputReady)) notes.Add($"Hazır ürün: {fp.OutputReady}");
                if (!string.IsNullOrEmpty(fp.NeededItem)) notes.Add($"İstek: {fp.NeededItem} x{fp.NeededCount}");
                if (notes.Count > 0)
                    list.Add($"Balık Havuzu ({fp.FishType} {fp.Occupants}/{fp.MaxOccupants}): {string.Join(", ", notes)}");
                continue;
            }

            if (b.JunimoHut is { OutputItems.Count: > 0 } jh)
            {
                list.Add($"Junimo Kulübesi: {jh.OutputItems.Count} yığın ürün toplamaya hazır.");
                continue;
            }

            if (b.SlimeHutch is { } sh && (sh.WateredSpots < sh.TotalWaterSpots || sh.SlimeBallsReady > 0))
            {
                list.Add($"Balçıkça Kümesi: Su {sh.WateredSpots}/{sh.TotalWaterSpots}, hazır Balçıkça Topu: {sh.SlimeBallsReady}.");
                continue;
            }

            if (b.Animals.Count == 0)
                continue;

            int needPet = b.Animals.Count(a => !a.WasPetToday);
            int produceWaiting = b.Animals.Count(a => a.ProduceWaiting);
            int fedOrTrough = b.Animals.Count(a => a.WasFedToday) + b.HayInTrough;
            int hayNeeded = Math.Max(0, b.Animals.Count - fedOrTrough);

            var parts = new List<string>();
            if (needPet > 0) parts.Add($"{needPet}/{b.Animals.Count} hayvanı sev");
            if (produceWaiting > 0)
            {
                bool hasCowsOrGoats = b.Animals.Any(a => a.ProduceWaiting && (a.Type.Contains("Cow", StringComparison.OrdinalIgnoreCase) || a.Type.Contains("Goat", StringComparison.OrdinalIgnoreCase)));
                parts.Add(hasCowsOrGoats
                    ? $"{produceWaiting} hayvan sağılmayı bekliyor (Süt Kovası ile)"
                    : $"{produceWaiting} ürün toplanmayı bekliyor");

                if (hasCowsOrGoats && !player.Items.Any(i => i is not null && i.Name.Contains("Milk Pail", StringComparison.OrdinalIgnoreCase)))
                {
                    string? pailSource = owned.FindToolSourceOutsideInventory("Milk Pail");
                    if (!string.IsNullOrEmpty(pailSource))
                        parts.Add($"UYARI: Süt Kovası envanterde değil -> {pailSource}");
                }
            }
            if (b.AutoGrabberItems.Count > 0) parts.Add($"Otomatik Toplayıcı'da {b.AutoGrabberItems.Count} yığın hazır");
            if (hayNeeded > 0) parts.Add($"{hayNeeded} saman eksik");
            if (parts.Count > 0)
                list.Add($"{bTr} ({b.Animals.Count} hayvan): {string.Join(" | ", parts)}.");
        }

        // 5. Machines & Chest Inputs
        var readyMachines = machines.Where(m => m.Ready > 0).ToList();
        if (readyMachines.Count > 0)
        {
            list.Add("Hazır Makineler (Topla): " + string.Join("; ", readyMachines.Select(m =>
            {
                string detail = m.ReadyItems is { Count: > 0 }
                    ? " (" + string.Join(", ", m.ReadyItems.Select(kv => kv.Value > 1 ? $"{kv.Key} x{kv.Value}" : kv.Key)) + ")"
                    : string.Empty;
                return $"{TranslateMachineNameTr(m.Name)} x{m.Ready}{detail}";
            })));
        }

        var workingMachines = machines.Where(m => m.Working > 0).ToList();
        if (workingMachines.Count > 0)
        {
            list.Add("Çalışan Makineler: " + string.Join("; ", workingMachines.Select(m =>
            {
                string detail = m.WorkingItems is { Count: > 0 }
                    ? " (" + string.Join(", ", m.WorkingItems.Select(kv => kv.Value > 1 ? $"{kv.Key} x{kv.Value}" : kv.Key)) + ")"
                    : string.Empty;
                return $"{TranslateMachineNameTr(m.Name)} x{m.Working}{detail}";
            })));
        }

        var idleHints = BuildIdleMachineHints(machines, buildings, owned);
        if (idleHints.Count > 0)
            list.Add("Boştaki Makineler İçin Sandık Eşleştirmesi: " + string.Join(" | ", idleHints));

        // 6. Top World Forage Regions
        if (worldForage.Count > 0)
        {
            var topRegions = worldForage.Take(4).Select(r =>
            {
                var counts = new List<string>();
                if (r.ForageCount > 0) counts.Add($"{r.ForageCount} ürün");
                if (r.ArtifactSpots > 0) counts.Add($"{r.ArtifactSpots} kazı noktası");
                return $"{TranslateRegionNameTr(r.Region)} ({string.Join(", ", counts)})";
            });
            list.Add("Dünya Toplayıcılığı (En Verimli Bölgeler): " + string.Join(", ", topRegions));
        }

        return list;
    }

    private static List<string> BuildIdleMachineHints(
        List<MachineEntry> machines,
        List<BuildingEntry> buildings,
        OwnedInventoryIndex owned)
    {
        var hints = new List<string>();
        foreach (var m in machines.Where(x => x.Idle > 0))
        {
            if (m.Name.Equals("Cheese Press", StringComparison.OrdinalIgnoreCase))
            {
                bool hasMilking = buildings.Any(b => b.Animals.Any(a => a.ProduceWaiting && (a.Type.Contains("Cow", StringComparison.OrdinalIgnoreCase) || a.Type.Contains("Goat", StringComparison.OrdinalIgnoreCase))));
                int milk = owned.GetStorageCount("(O)184") + owned.GetStorageCount("(O)186") + owned.GetStorageCount("(O)436") + owned.GetStorageCount("(O)438");
                if (milk > 0 || hasMilking)
                    hints.Add($"Peynir Presi ({m.Idle} boş): {(milk > 0 ? $"Sandıkta {milk} süt hazır" : "İnek/keçi sağılınca peynir yap")}");
            }
            else if (m.Name.Equals("Mayonnaise Machine", StringComparison.OrdinalIgnoreCase))
            {
                int eggs = owned.GetStorageCount("(O)176") + owned.GetStorageCount("(O)174") + owned.GetStorageCount("(O)180") + owned.GetStorageCount("(O)182") + owned.GetStorageCount("(O)442");
                if (eggs > 0)
                    hints.Add($"Mayonez Makinesi ({m.Idle} boş): Sandıkta {eggs} yumurta var");
            }
            else if (m.Name.Equals("Charcoal Kiln", StringComparison.OrdinalIgnoreCase))
            {
                int coal = owned.GetStorageCount("(O)382");
                int wood = owned.GetStorageCount("(O)388");
                if (coal < 15 && wood >= 30)
                    hints.Add($"Kömür Fırını ({m.Idle} boş): Sadece {coal} Kömür kalmış, sandıkta {wood} Odun var (10 Odun -> 1 Kömür)");
            }
            else if (m.Name.Equals("Furnace", StringComparison.OrdinalIgnoreCase) || m.Name.Equals("Heavy Furnace", StringComparison.OrdinalIgnoreCase))
            {
                int ores = owned.GetStorageCount("(O)378") + owned.GetStorageCount("(O)380") + owned.GetStorageCount("(O)384") + owned.GetStorageCount("(O)386");
                int coal = owned.GetStorageCount("(O)382");
                if (ores >= 5 && coal > 0)
                    hints.Add($"{TranslateMachineNameTr(m.Name)} ({m.Idle} boş): {ores} cevher ve {coal} Kömür bekliyor");
            }
            else if (m.Name.Equals("Keg", StringComparison.OrdinalIgnoreCase))
            {
                int hops = owned.GetStorageCount("(O)304");
                int starfruit = owned.GetStorageCount("(O)268");
                if (hops > 0 || starfruit > 0)
                    hints.Add($"Fıçı ({m.Idle} boş): Şerbetçi Otu x{hops}, Yıldızmeyvesi x{starfruit}");
            }
            else if (m.Name.Equals("Preserves Jar", StringComparison.OrdinalIgnoreCase))
            {
                hints.Add($"Konserve Kavanozu ({m.Idle} boş): Meyve/sebze işlenebilir");
            }
            else if (m.Name.Equals("Crab Pot", StringComparison.OrdinalIgnoreCase))
            {
                hints.Add($"Yengeç Çömleği ({m.Idle} boş): Yem eklenmeli");
            }
        }
        return hints;
    }

    private static List<CropProfitEntry> BuildTopProfitableCrops(
        Farmer player,
        int daysLeftInSeason,
        bool desertUnlocked,
        List<ShopItemEntry> travelingCart,
        OwnedInventoryIndex owned,
        Dictionary<string, CropData> cropData)
    {
        var results = new List<CropProfitEntry>();
        if (daysLeftInSeason <= 0 || cropData.Count == 0)
            return results;

        var shopSeeds = new Dictionary<string, (int Price, string ShopName)>(StringComparer.OrdinalIgnoreCase);

        static void AddShopStock(string shopId, string shopLabel, Dictionary<string, (int Price, string ShopName)> map)
        {
            try
            {
                var stock = ShopBuilder.GetShopStock(shopId);
                foreach (var pair in stock)
                {
                    if (pair.Key is null || pair.Value.Price <= 0)
                        continue;

                    string qid = FishGuideBuilder.NormalizeQid(pair.Key.QualifiedItemId ?? string.Empty);
                    if (string.IsNullOrEmpty(qid))
                        continue;

                    if (!map.TryGetValue(qid, out var existing) || pair.Value.Price < existing.Price)
                        map[qid] = (pair.Value.Price, shopLabel);
                }
            }
            catch
            {
                // Ignore inaccessible or modded shop errors
            }
        }

        AddShopStock("SeedShop", "Pierre", shopSeeds);
        if (desertUnlocked)
            AddShopStock("Sandy", "Çöl", shopSeeds);

        foreach (var t in travelingCart)
        {
            if (t.Price <= 0 || string.IsNullOrWhiteSpace(t.QualifiedId))
                continue;
            string qid = FishGuideBuilder.NormalizeQid(t.QualifiedId);
            if (!shopSeeds.TryGetValue(qid, out var existing) || t.Price < existing.Price)
                shopSeeds[qid] = (t.Price, "Gezgin Tüccar");
        }

        foreach (var (seedId, cd) in cropData)
        {
            if (cd is null || cd.Seasons is null || !cd.Seasons.Contains(Game1.season))
                continue;

            int growthDays = cd.DaysInPhase?.Sum() ?? 0;
            if (growthDays <= 0 || growthDays > daysLeftInSeason)
                continue;

            string seedQid = FishGuideBuilder.NormalizeQid(seedId);
            string harvestQid = FishGuideBuilder.NormalizeQid(cd.HarvestItemId ?? string.Empty);
            if (string.IsNullOrEmpty(harvestQid))
                continue;

            var harvestObj = ItemRegistry.Create(harvestQid) as StardewValley.Object;
            if (harvestObj is null)
                continue;

            int sellPrice = harvestObj.sellToStorePrice();
            if (sellPrice <= 0)
                continue;

            bool inShop = shopSeeds.TryGetValue(seedQid, out var shopInfo);
            int ownedCount = owned.GetStorageCount(seedQid);
            if (!inShop && ownedCount <= 0)
                continue;

            int seedPrice = inShop ? shopInfo.Price : (ItemRegistry.Create(seedQid)?.salePrice() ?? 0);
            string shopSource = inShop ? shopInfo.ShopName : "Sandık";
            string seedName = ItemRegistry.GetData(seedQid)?.DisplayName ?? seedId;

            int minStack = Math.Max(1, cd.HarvestMinStack);
            int maxStack = Math.Max(minStack, cd.HarvestMaxStack);
            if (cd.HarvestMaxIncreasePerFarmingLevel > 0)
                maxStack += (int)(player.FarmingLevel * cd.HarvestMaxIncreasePerFarmingLevel);

            double extraChance = Math.Clamp(cd.ExtraHarvestChance, 0.0, 0.95);
            double expectedYield = ((minStack + maxStack) / 2.0) + (extraChance > 0 ? extraChance / (1.0 - extraChance) : 0.0);

            int harvestsInSeason;
            double netProfitInSeason;
            if (cd.RegrowDays > 0)
            {
                harvestsInSeason = 1 + (daysLeftInSeason - growthDays) / cd.RegrowDays;
                netProfitInSeason = (harvestsInSeason * expectedYield * sellPrice) - seedPrice;
            }
            else
            {
                int cycles = daysLeftInSeason / growthDays;
                harvestsInSeason = cycles;
                netProfitInSeason = cycles * ((expectedYield * sellPrice) - seedPrice);
            }

            if (netProfitInSeason <= 0)
                continue;

            double profitPerDay = netProfitInSeason / daysLeftInSeason;

            string sourceTag = (seedId.Contains("Cornucopia", StringComparison.OrdinalIgnoreCase)
                || harvestQid.Contains("Cornucopia", StringComparison.OrdinalIgnoreCase))
                ? "Cornucopia"
                : seedId.Contains("FlashShifter", StringComparison.OrdinalIgnoreCase)
                    ? "SVE"
                    : seedId.Contains("Rafseazz", StringComparison.OrdinalIgnoreCase)
                        ? "Ridgeside"
                        : seedId.Contains("EastScarp", StringComparison.OrdinalIgnoreCase)
                            ? "East Scarp"
                            : "Vanilla";

            results.Add(new CropProfitEntry(
                harvestObj.DisplayName,
                seedName,
                sourceTag,
                shopSource,
                seedPrice,
                sellPrice,
                growthDays,
                cd.RegrowDays,
                Math.Round(expectedYield, 2),
                harvestsInSeason,
                Math.Round(netProfitInSeason, 0),
                Math.Round(profitPerDay, 1),
                ownedCount));
        }

        return results
            .OrderByDescending(x => x.ProfitPerDay)
            .ThenByDescending(x => x.NetProfitInSeason)
            .Take(12)
            .ToList();
    }

    private static List<string> BuildStrategicInsights(
        Farmer player,
        bool greenhouseUnlocked,
        bool islandUnlocked,
        int daysLeftInSeason,
        List<CropEntry> crops,
        List<BuildingEntry> buildings,
        List<MachineEntry> machines,
        Dictionary<string, int> objectsSummary,
        bool ccAvailable,
        List<BundleEntry> ccIncomplete,
        List<SpecialOrderEntry> specialOrders,
        OwnedInventoryIndex owned,
        Dictionary<string, CropData> cropData,
        List<CropProfitEntry> topProfitableCrops,
        WorldIntelligenceSnapshot worldIntel)
    {
        var list = new List<string>();

        // 0. World, Story & Unlock Gates (Creator-Level Multi-Domain Briefing)
        foreach (var gate in worldIntel.ProgressionGates.Take(4))
        {
            list.Add($"[{gate.Category}] {gate.Title} ({gate.Progress}): {gate.NextStep}");
        }

        // 0b. High-Impact NPC Targets (Recipes, Events, Mod Storylines + Owned Gifts)
        if (worldIntel.NpcMilestones.Count > 0)
        {
            var topNpcs = worldIntel.NpcMilestones.Take(4).Select(n =>
            {
                string gift = n.OwnedLovedGifts.Count > 0
                    ? $" | Sandıkta sevdiği: {n.OwnedLovedGifts[0]}"
                    : string.Empty;
                return $"{n.DisplayName} ({n.CurrentHearts}♥ -> {n.TargetHearts}♥: {n.Unlocks[0]}{gift})";
            });
            list.Add("Öncelikli Karakter Yatırımları (Tarif / Hikâye Açanlar): " + string.Join(" || ", topNpcs));
        }

        // 0c. Installed Mod Ecosystem Opportunities (Automate, SVE, RSV, East Scarp, Solarium, BiggerBackpack, etc.)
        var topModTips = worldIntel.ModHighlights
            .Where(m => !m.ActionableTip.Contains("harika çalışıyor", StringComparison.OrdinalIgnoreCase))
            .Take(4)
            .Select(m => $"{m.ModName} ({m.StatusSummary}): {m.ActionableTip}")
            .ToList();
        if (topModTips.Count > 0)
            list.Add("Yüklü Mod Fırsatları (tamamı live_world.md'de): " + string.Join(" || ", topModTips));

        // 0d. Equipment & Unbuilt High-Value Buildings
        if (worldIntel.EquipmentAlerts.Count > 0)
            list.Add("Ekipman / Çanta Kontrolü: " + string.Join(" | ", worldIntel.EquipmentAlerts));

        if (worldIntel.UnbuiltKeyBuildings.Count > 0)
            list.Add("Henüz İnşa Edilmemiş Değerli Binalar: " + string.Join(" | ", worldIntel.UnbuiltKeyBuildings.Take(3)));

        // 1. Most Profitable Crops This Season (Cornucopia + Vanilla + Mods)
        if (topProfitableCrops.Count > 0 && daysLeftInSeason >= 5)
        {
            string seasonTr = FishGuideBuilder.TranslateSeason(Game1.currentSeason).Replace("Guz", "Güz").Replace("Kis", "Kış");
            var top4 = topProfitableCrops.Take(4).Select(p =>
            {
                string regrow = p.RegrowDays > 0 ? $"/{p.RegrowDays}g yenilenir" : "";
                string own = p.OwnedSeedCount > 0 ? $", sandıkta x{p.OwnedSeedCount}" : "";
                return $"{p.CropName} [{p.SourceTag}] {p.ProfitPerDay:0.1}g/gün (tohum {p.SeedPrice}g {p.ShopSource}, {p.GrowthDays}g{regrow}{own})";
            });
            list.Add($"En Kârlı {seasonTr} Ekinleri (kalan {daysLeftInSeason}g, ham satış kârı): " + string.Join(" | ", top4) + ".");
        }

        // 1. Rare Seed (347) & Ancient Seed (114/499) / Ancient Fruit (454)
        var (rareSeedCount, rareSeedSources) = owned.FindStorageMatches("(O)347");
        if (rareSeedCount > 0 && !(Game1.season == Season.Fall && daysLeftInSeason >= 24))
        {
            list.Add($"Ender Tohum Stokta (x{rareSeedCount}): [{string.Join(", ", rareSeedSources)}] — Güz 1–4 arasında açık tarlaya veya Sera açılınca Sera'ya ek.");
        }

        var (ancientSeedObjCount, ancientSeedObjSources) = owned.FindStorageMatches("(O)499");
        var (ancientArtifactCount, ancientArtifactSources) = owned.FindStorageMatches("(O)114");
        var plantedAncient = crops.FirstOrDefault(c => c.HarvestItemId == "(O)454");
        if (ancientSeedObjCount > 0 || ancientArtifactCount > 0 || plantedAncient is not null)
        {
            var notes = new List<string>();
            if (ancientArtifactCount > 0)
                notes.Add($"Müze'ye 1 adet Eski Tohum eseri bağışlayıp üretilebilir tohuma dönüştür [{string.Join(", ", ancientArtifactSources)}]");
            if (plantedAncient is not null)
                notes.Add($"Tarladaki Eski Meyve ({plantedAncient.MinDaysRemaining}g sonra ilk hasat): Çıkan meyveleri ASLA satma, Tohum Yapıcı (Seed Maker) ile çoğalt!");
            if (ancientSeedObjCount > 0)
                notes.Add(!greenhouseUnlocked
                    ? $"Sandıktaki Eski Tohumlar x{ancientSeedObjCount} [{string.Join(", ", ancientSeedObjSources)}] kışın ölmemesi için Sera açılana veya Bahar 1'e saklanmalı"
                    : $"Sandıktaki Eski Tohumlar x{ancientSeedObjCount} [{string.Join(", ", ancientSeedObjSources)}] doğrudan Sera'ya ekilmeli");
            list.Add("Eski Meyve İmparatorluğu (Uzun Vade): " + string.Join("; ", notes) + ".");
        }

        // 2. Greenhouse Unlock Bottleneck
        if (!greenhouseUnlocked && ccAvailable)
        {
            var pantryBundles = ccIncomplete.Where(b => b.Area.Equals("Pantry", StringComparison.OrdinalIgnoreCase)).ToList();
            if (pantryBundles.Count > 0)
            {
                string remaining = string.Join("; ", pantryBundles.Select(b => $"{b.BundleName} ({b.FilledCount}/{b.NeededCount})"));
                bool needsAnimal = pantryBundles.Any(b => b.BundleName.Contains("Animal", StringComparison.OrdinalIgnoreCase));
                string animalTip = string.Empty;
                if (needsAnimal)
                {
                    bool hasDuckOrBrown = buildings.Any(b => b.Animals.Any(a => a.Type.Contains("Duck", StringComparison.OrdinalIgnoreCase) || a.Type.Contains("Brown", StringComparison.OrdinalIgnoreCase)));
                    bool hasGoatOrSheep = buildings.Any(b => b.Animals.Any(a => a.Type.Contains("Goat", StringComparison.OrdinalIgnoreCase) || a.Type.Contains("Sheep", StringComparison.OrdinalIgnoreCase)));
                    if (!hasDuckOrBrown || !hasGoatOrSheep)
                        animalTip = " Hayvan Yığını için Büyük Kümes (Kahverengi Tavuk/Ördek) ve Büyük Ahır (Keçi) yükseltmesi + Gezgin Tüccar takibi Sera kilidini 1 yıl erkene çeker!";
                }
                list.Add($"Sera Kilidi Yolu (Kalan Kiler Yığınları): {remaining}.{animalTip}");
            }
        }

        // 3. Special Order / Long-Term Resource Tracking
        foreach (var so in specialOrders)
        {
            string combined = string.Join(" ", so.Objectives);
            var checks = new List<string>();
            if (combined.Contains("Kereste", StringComparison.OrdinalIgnoreCase) || combined.Contains("Hardwood", StringComparison.OrdinalIgnoreCase))
                checks.Add($"Kereste: {owned.GetStorageCount("(O)709")} adet");
            if (combined.Contains("Taş", StringComparison.OrdinalIgnoreCase) || combined.Contains("Stone", StringComparison.OrdinalIgnoreCase))
                checks.Add($"Taş: {owned.GetStorageCount("(O)390")} adet");
            if (combined.Contains("Demir külçe", StringComparison.OrdinalIgnoreCase) || combined.Contains("Iron Bar", StringComparison.OrdinalIgnoreCase))
                checks.Add($"Demir Külçesi: {owned.GetStorageCount("(O)335")} (+{owned.GetStorageCount("(O)380")} cevher)");
            if (combined.Contains("Pil", StringComparison.OrdinalIgnoreCase) || combined.Contains("Battery", StringComparison.OrdinalIgnoreCase))
            {
                int rods = objectsSummary.GetValueOrDefault("Lightning Rod");
                checks.Add($"Pil Paketi: {owned.GetStorageCount("(O)787")} (Çiftlikte {rods} Paratoner var — fırtınalı günler öncesi Paratoner artırmak büyük avantaj sağlar)");
            }

            if (checks.Count > 0)
                list.Add($"Özel Proje Hazırlığı ({so.Name}): {string.Join(" | ", checks)}.");
        }

        // 4. Data-driven strategic rules from advisor_rules.json (hot-reloaded without game restart)
        foreach (string ruleMsg in worldIntel.StrategicRules)
            list.Add(ruleMsg);

        return list;
    }

    private static string FormatChestSourceLabel(ChestEntry c)
    {
        if (c.Location.Equals("FarmHouse_Fridge", StringComparison.OrdinalIgnoreCase))
            return "Ev Buzdolabı";
        if (c.Location.Equals("IslandFarmHouse_Fridge", StringComparison.OrdinalIgnoreCase))
            return "Ada Buzdolabı";
        string region = TranslateRegionNameTr(c.Location);
        return string.IsNullOrWhiteSpace(c.CustomName)
            ? $"Sandık ({region} @{c.Tile})"
            : $"Sandık [{c.CustomName}] ({region} @{c.Tile})";
    }

    private static string FormatTurkishWhere(string where)
    {
        if (string.IsNullOrWhiteSpace(where))
            return string.Empty;

        int lb = where.IndexOf('[');
        int rb = where.IndexOf(']');
        if (lb < 0 || rb <= lb)
            return where;

        string coords = where[..lb].Trim();
        string inside = where[(lb + 1)..rb].Trim();
        if (inside.StartsWith("at ", StringComparison.OrdinalIgnoreCase))
            return $"{coords} ({TranslateLandmarkTr(inside[3..].Trim())} yanında)";

        int ofIdx = inside.IndexOf(" of ", StringComparison.OrdinalIgnoreCase);
        if (ofIdx <= 0)
            return where;

        string dirPart = inside[..ofIdx].Trim();
        string rawLandmark = inside[(ofIdx + 4)..].Trim();
        string landmarkGen = TranslateLandmarkGenitiveTr(rawLandmark);
        var trDirs = new List<string>();
        foreach (string token in dirPart.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            char dirChar = token[^1];
            string num = token[..^1];
            string dirWord = dirChar switch
            {
                'N' => "yukarısı",
                'S' => "aşağısı",
                'E' => "sağı",
                'W' => "solu",
                _ => dirChar.ToString()
            };
            trDirs.Add($"{num} kare {dirWord}");
        }

        return $"{coords} ({landmarkGen} {string.Join(", ", trDirs)})";
    }

    private static string TranslateLandmarkGenitiveTr(string lm) => lm switch
    {
        "Farmhouse" => "Çiftlik Evi'nin",
        "Greenhouse" => "Sera'nın",
        "Shipping Bin" => "Satış Kutusu'nun",
        "Coop" => "Kümes'in",
        "Big Coop" => "Büyük Kümes'in",
        "Deluxe Coop" => "Lüks Kümes'in",
        "Barn" => "Ahır'ın",
        "Big Barn" => "Büyük Ahır'ın",
        "Deluxe Barn" => "Lüks Ahır'ın",
        _ => $"{lm} yakınında"
    };

    private static string TranslateLandmarkTr(string lm) => lm switch
    {
        "Farmhouse" => "Çiftlik Evi",
        "Greenhouse" => "Sera",
        "Shipping Bin" => "Satış Kutusu",
        "Coop" => "Kümes",
        "Big Coop" => "Büyük Kümes",
        "Deluxe Coop" => "Lüks Kümes",
        "Barn" => "Ahır",
        "Big Barn" => "Büyük Ahır",
        "Deluxe Barn" => "Lüks Ahır",
        _ => lm
    };

    private static string TranslateBuildingTypeTr(string bType) => bType switch
    {
        "Coop" => "Kümes",
        "Big Coop" => "Büyük Kümes",
        "Deluxe Coop" => "Lüks Kümes",
        "Barn" => "Ahır",
        "Big Barn" => "Büyük Ahır",
        "Deluxe Barn" => "Lüks Ahır",
        "Fish Pond" => "Balık Havuzu",
        "Junimo Hut" => "Junimo Kulübesi",
        "Slime Hutch" => "Balçıkça Kümesi",
        _ => bType
    };

    private static string TranslateBundleAreaTr(string area) => area switch
    {
        "Pantry" => "Kiler",
        "Crafts Room" => "Zanaat Odası",
        "Fish Tank" => "Akvaryum",
        "Boiler Room" => "Kazan Dairesi",
        "Vault" => "Kasa",
        "Bulletin Board" => "İlan Panosu",
        "Abandoned Joja Mart" => "Terk Edilmiş JojaMart",
        _ => area
    };

    internal static string TranslateRegionNameTr(string name) => name switch
    {
        "Farm" => "Çiftlik",
        "FarmCave" => "Çiftlik Mağarası",
        "Greenhouse" => "Sera",
        "IslandWest" => "Zencefil Adası Batısı",
        "FerngillRepublicFrontier" => "Ferngill Cumhuriyeti Sınırı",
        "Desert" => "Patisko Çölü",
        "Town" => "Pelikan Kasabası",
        "Beach" => "Kumsal",
        "Forest" => "Kömürözü Ormanı",
        "Woods" => "Gizli Orman",
        "Mountain" => "Dağlar",
        "EastScarp_MeadowFarm" or "ES:MeadowFarm" => "Doğu Sarpı Çayır Çiftliği",
        "EastScarp_Orchard" or "ES:Orchard" => "Doğu Sarpı Meyve Bahçesi",
        "EastScarp_DeepMountainCrossroads" or "ES:DeepMountainCrossroads" => "Derin Dağlar Kavşağı",
        "RSV:SummitFarm" => "Tepeyanı Zirve Çiftliği",
        _ => name
    };

    internal static string TranslateMachineNameTr(string name) => name switch
    {
        "Keg" => "Fıçı",
        "Preserves Jar" => "Konserve Kavanozu",
        "Cheese Press" => "Peynir Presi",
        "Mayonnaise Machine" => "Mayonez Makinesi",
        "Furnace" => "Fırın",
        "Heavy Furnace" => "Ağır Fırın",
        "Dehydrator" => "Kurutucu",
        "Seed Maker" => "Tohum Yapıcı",
        "Crystalarium" => "Kristalaryum",
        "Tapper" => "Akıtıcı",
        "Heavy Tapper" => "Ağır Akıtıcı",
        "Bee House" => "Arı Kovanı",
        "Fish Smoker" => "Balık Tütsüleyici",
        "Bait Maker" => "Yem Yapıcı",
        "Oil Maker" => "Yağ Makinesi",
        "Loom" => "Dokuma Tezgâhı",
        "Recycling Machine" => "Geri Dönüşüm Makinesi",
        "Charcoal Kiln" => "Kömür Fırını",
        "Worm Bin" => "Solucan Kutusu",
        "Deluxe Worm Bin" => "Lüks Solucan Kutusu",
        "Lightning Rod" => "Paratoner",
        "Cask" => "Yıllandırma Varili",
        "Mushroom Box" => "Mantar Kutusu",
        "Crab Pot" => "Yengeç Çömleği",
        _ => name
    };

    private static string TranslateWeatherTr(string weather) => weather switch
    {
        "Rain" => "Yağmurlu",
        "Storm" => "Fırtınalı",
        "GreenRain" => "Yeşil Yağmur",
        "Snow" => "Karlı",
        "Wind" => "Rüzgârlı",
        "Sun" => "Güneşli",
        _ => weather
    };

    private static string TranslateBlockerTr(string? blocker) => blocker switch
    {
        "Copper Axe" => "Bakır Balta gerekir",
        "Steel Axe" => "Çelik Balta gerekir",
        "Steel Pickaxe" => "Çelik Kazma gerekir",
        "Gold Pickaxe" => "Altın Kazma gerekir",
        null or "" => "Önü kapalı",
        _ => $"{blocker} gerekir"
    };
}
