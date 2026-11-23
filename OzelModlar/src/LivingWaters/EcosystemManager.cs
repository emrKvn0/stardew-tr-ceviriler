using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.GameData.Locations;
using StardewValley.Locations;
using StardewValley.Quests;

namespace LivingWaters;

internal sealed class WaterBodyInfo
{
    public string ZoneKey { get; init; } = string.Empty;
    public Point AnchorTile { get; init; }
    public Point CenterTile { get; init; }
    public List<(int X, int Y)> Tiles { get; init; } = new();
    public Rectangle Bounds { get; init; }
    public string? FishAreaId { get; init; }
    public bool TouchesMapEdge { get; init; }
    public string SubLabel { get; init; } = string.Empty;

    public bool Intersects(Rectangle rect)
    {
        if (!this.Bounds.Intersects(rect))
            return false;

        foreach (var (x, y) in this.Tiles)
        {
            if (rect.Contains(x, y))
                return true;
        }
        return false;
    }
}

internal readonly record struct WaterCapacityProfile(
    string SizeLabel,
    int MaxNaturalSpecies,
    int BasePerSpecies,
    int MinFloorPerSpecies,
    int MaxPerSpecies,
    int MaxTotalFish,
    int MaxVisibleFish,
    int QualityBonusThreshold,
    int MigrationThreshold);

internal sealed class EcosystemManager
{
    private sealed class CachedWaterMapEntry
    {
        public GameLocation LocationRef { get; init; } = null!;
        public xTile.Map? MapRef { get; init; }
        public bool HadWaterTiles { get; init; }
        public LocationWaterMap WaterMap { get; init; } = null!;
    }

    private sealed class LocationWaterMap
    {
        public int Width { get; }
        public int Height { get; }
        public int[,] ComponentGrid { get; }
        public List<WaterBodyInfo> Bodies { get; } = new();
        public Dictionary<string, WaterBodyInfo> ByZoneKey { get; } = new(StringComparer.OrdinalIgnoreCase);

        public LocationWaterMap(GameLocation loc)
        {
            var layer = loc.map?.GetLayer("Back");
            if (layer is null || layer.LayerWidth <= 0 || layer.LayerHeight <= 0)
            {
                this.Width = 0;
                this.Height = 0;
                this.ComponentGrid = new int[0, 0];
                return;
            }

            this.Width = layer.LayerWidth;
            this.Height = layer.LayerHeight;
            this.ComponentGrid = new int[this.Width, this.Height];
            for (int x = 0; x < this.Width; x++)
            {
                for (int y = 0; y < this.Height; y++)
                    this.ComponentGrid[x, y] = -1;
            }

            var waterFlags = new bool[this.Width, this.Height];
            var areaIds = new string?[this.Width, this.Height];

            for (int y = 0; y < this.Height; y++)
            {
                for (int x = 0; x < this.Width; x++)
                {
                    if (!IsWaterTileSafe(loc, x, y))
                        continue;

                    waterFlags[x, y] = true;
                    if (loc.TryGetFishAreaForTile(new Vector2(x, y), out string? areaId, out _) && !string.IsNullOrWhiteSpace(areaId))
                        areaIds[x, y] = areaId;
                }
            }

            var queue = new Queue<(int X, int Y)>();
            for (int startY = 0; startY < this.Height; startY++)
            {
                for (int startX = 0; startX < this.Width; startX++)
                {
                    if (!waterFlags[startX, startY] || this.ComponentGrid[startX, startY] >= 0)
                        continue;

                    int compIdx = this.Bodies.Count;
                    string? compAreaId = areaIds[startX, startY];
                    var tiles = new List<(int X, int Y)>();

                    this.ComponentGrid[startX, startY] = compIdx;
                    queue.Enqueue((startX, startY));

                    int minX = startX, maxX = startX;
                    int minY = startY, maxY = startY;
                    long sumX = 0, sumY = 0;
                    Point anchor = new(startX, startY);
                    bool touchesEdge = false;

                    while (queue.Count > 0)
                    {
                        var (cx, cy) = queue.Dequeue();
                        tiles.Add((cx, cy));
                        sumX += cx;
                        sumY += cy;

                        if (cx < minX) minX = cx;
                        if (cx > maxX) maxX = cx;
                        if (cy < minY) minY = cy;
                        if (cy > maxY) maxY = cy;

                        if (cy < anchor.Y || (cy == anchor.Y && cx < anchor.X))
                            anchor = new Point(cx, cy);

                        if (cx <= 1 || cy <= 1 || cx >= this.Width - 2 || cy >= this.Height - 2)
                            touchesEdge = true;

                        for (int dy = -1; dy <= 1; dy++)
                        {
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                if (dx == 0 && dy == 0)
                                    continue;

                                int nx = cx + dx;
                                int ny = cy + dy;
                                if (nx < 0 || ny < 0 || nx >= this.Width || ny >= this.Height)
                                    continue;
                                if (!waterFlags[nx, ny] || this.ComponentGrid[nx, ny] >= 0)
                                    continue;
                                if (!string.Equals(areaIds[nx, ny], compAreaId, StringComparison.OrdinalIgnoreCase))
                                    continue;

                                this.ComponentGrid[nx, ny] = compIdx;
                                queue.Enqueue((nx, ny));
                            }
                        }
                    }

                    double avgX = (double)sumX / tiles.Count;
                    double avgY = (double)sumY / tiles.Count;
                    Point center = anchor;
                    double bestDistSq = double.MaxValue;
                    foreach (var (tx, ty) in tiles)
                    {
                        double ddx = tx - avgX;
                        double ddy = ty - avgY;
                        double distSq = ddx * ddx + ddy * ddy;
                        if (distSq < bestDistSq)
                        {
                            bestDistSq = distSq;
                            center = new Point(tx, ty);
                        }
                    }

                    bool isCoastal = loc.Name.Equals("Beach", StringComparison.OrdinalIgnoreCase)
                        || loc.Name.StartsWith("Island", StringComparison.OrdinalIgnoreCase);

                    string subLabel;
                    if (!touchesEdge && tiles.Count <= 40)
                        subLabel = $"Küçük Gölet ({center.X}, {center.Y})";
                    else if (!touchesEdge && tiles.Count <= 120)
                        subLabel = $"Gölet ({center.X}, {center.Y})";
                    else if (touchesEdge)
                        subLabel = isCoastal ? $"Kıyı Suyu ({center.X}, {center.Y})" : $"Akarsu ({center.X}, {center.Y})";
                    else
                        subLabel = $"Göl ({center.X}, {center.Y})";

                    string zoneKey = $"{loc.Name}::water_{anchor.X}_{anchor.Y}";
                    var body = new WaterBodyInfo
                    {
                        ZoneKey = zoneKey,
                        AnchorTile = anchor,
                        CenterTile = center,
                        Tiles = tiles,
                        Bounds = new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1),
                        FishAreaId = compAreaId,
                        TouchesMapEdge = touchesEdge,
                        SubLabel = subLabel
                    };

                    this.Bodies.Add(body);
                    this.ByZoneKey[zoneKey] = body;
                }
            }
        }
    }

    private static readonly Dictionary<string, CachedWaterMapEntry> WaterMapCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string[]> ConnectedWaterways = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Mountain"] = new[] { "Town", "Custom_Ridgeside_RidgesideVillage", "Custom_ESDeepMountains" },
        ["Town"] = new[] { "Mountain", "Forest", "Custom_ShearwaterBridge", "Custom_BlueMoonVineyard", "Custom_EastScarpe", "Custom_Ridgeside_RidgesideVillage" },
        ["Forest"] = new[] { "Town", "Farm", "Woods", "Custom_ShearwaterBridge", "Custom_ForestWest", "Custom_SpriteSpring" },
        ["Farm"] = new[] { "Forest", "Custom_FerngillRepublicFrontier" },
        ["Custom_FerngillRepublicFrontier"] = new[] { "Farm", "Forest", "Beach" },
        ["Woods"] = new[] { "Forest", "Custom_SpriteSpring" },
        ["Beach"] = new[] { "IslandSouth", "Custom_BlueMoonVineyard", "Custom_FableReef", "Custom_EastScarpe", "Custom_ESCastlePoint" },
        ["IslandSouth"] = new[] { "Beach", "IslandWest", "IslandEast", "IslandSouthEastCave" },
        ["IslandWest"] = new[] { "IslandSouth", "IslandNorth" },
        ["IslandNorth"] = new[] { "IslandWest", "IslandEast" },
        ["IslandEast"] = new[] { "IslandSouth", "IslandNorth" },
        ["IslandSouthEastCave"] = new[] { "IslandSouth" },
        ["Sewer"] = new[] { "BugLand" },
        ["BugLand"] = new[] { "Sewer" },
        ["Custom_ShearwaterBridge"] = new[] { "Town", "Forest", "Custom_EastScarpe" },
        ["Custom_BlueMoonVineyard"] = new[] { "Town", "Beach" },
        ["Custom_ForestWest"] = new[] { "Forest", "Custom_SpriteSpring" },
        ["Custom_SpriteSpring"] = new[] { "Forest", "Woods", "Custom_ForestWest" },
        ["Custom_FableReef"] = new[] { "Beach" },
        ["Custom_EastScarpe"] = new[] { "Town", "Beach", "Custom_ShearwaterBridge", "Custom_ESDeepMountains", "Custom_ESCastlePoint" },
        ["Custom_ESDeepMountains"] = new[] { "Custom_EastScarpe", "Mountain" },
        ["Custom_ESCastlePoint"] = new[] { "Custom_EastScarpe", "Beach" },
        ["Custom_Ridgeside_RidgesideVillage"] = new[] { "Mountain", "Town", "Custom_Ridgeside_Ridge", "Custom_Ridgeside_RidgeFalls" },
        ["Custom_Ridgeside_Ridge"] = new[] { "Custom_Ridgeside_RidgesideVillage", "Custom_Ridgeside_RidgeFalls", "Custom_Ridgeside_RidgeForest" },
        ["Custom_Ridgeside_RidgeFalls"] = new[] { "Custom_Ridgeside_RidgesideVillage", "Custom_Ridgeside_Ridge" },
        ["Custom_Ridgeside_RidgeForest"] = new[] { "Custom_Ridgeside_Ridge" }
    };

    private readonly IMonitor Monitor;
    private Func<ModConfig> GetConfig;
    private WaterEcosystemSaveData State = new();
    private readonly HashSet<string> ProtectedFishIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> BossFishIds = new(StringComparer.OrdinalIgnoreCase);
    private bool CachesDirty = true;

    public EcosystemManager(IMonitor monitor, Func<ModConfig> getConfig)
    {
        this.Monitor = monitor;
        this.GetConfig = getConfig;
    }

    public void LoadState(WaterEcosystemSaveData? saved)
    {
        WaterMapCache.Clear();
        this.SyncedZonesThisSession.Clear();

        this.State = saved ?? new WaterEcosystemSaveData();
        this.State.Zones = this.State.Zones is null
            ? new Dictionary<string, ZonePopulation>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, ZonePopulation>(this.State.Zones, StringComparer.OrdinalIgnoreCase);

        var cfg = this.GetConfig();
        foreach (var kv in this.State.Zones)
        {
            ZonePopulation zone = kv.Value;
            zone.LastSeasonInitialized ??= string.Empty;
            zone.SpeciesCounts = zone.SpeciesCounts is null
                ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, int>(zone.SpeciesCounts, StringComparer.OrdinalIgnoreCase);
            zone.PlayerStockedSpecies = zone.PlayerStockedSpecies is null
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(zone.PlayerStockedSpecies, StringComparer.OrdinalIgnoreCase);
            zone.DailyCaughtToday = zone.DailyCaughtToday is null
                ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, int>(zone.DailyCaughtToday, StringComparer.OrdinalIgnoreCase);
            zone.SpeciesDetails = zone.SpeciesDetails is null
                ? new Dictionary<string, SpeciesHistory>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, SpeciesHistory>(zone.SpeciesDetails, StringComparer.OrdinalIgnoreCase);

            foreach (string qid in zone.SpeciesCounts.Keys)
            {
                if (!zone.SpeciesDetails.ContainsKey(qid))
                {
                    zone.SpeciesDetails[qid] = new SpeciesHistory
                    {
                        Origin = zone.PlayerStockedSpecies.Contains(qid) ? "Stocked" : "Natural",
                        FirstSeenTotalDays = Context.IsWorldReady ? Game1.Date.TotalDays : -1
                    };
                }
            }
        }

        if (this.State.SchemaVersion < 2)
        {
            var legacyKeys = this.State.Zones.Keys
                .Where(k => !k.Contains("::water_", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (string oldKey in legacyKeys)
            {
                if (!this.State.Zones.TryGetValue(oldKey, out var oldZone))
                    continue;

                this.State.Zones.Remove(oldKey);

                if (oldZone.PlayerStockedSpecies.Count == 0)
                    continue;

                string locName = GetLocationNameFromZoneKey(oldKey);
                GameLocation? loc = Game1.getLocationFromName(locName);
                if (loc is null)
                    continue;

                var waterMap = GetOrBuildWaterMap(loc);
                if (waterMap.Bodies.Count == 0)
                    continue;

                int sep = oldKey.IndexOf("::", StringComparison.Ordinal);
                string? oldAreaSuffix = sep > 0 ? oldKey[(sep + 2)..] : null;

                WaterBodyInfo targetBody = waterMap.Bodies
                    .Where(b => oldAreaSuffix is null || string.Equals(b.FishAreaId, oldAreaSuffix, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(b => b.Tiles.Count)
                    .FirstOrDefault()
                    ?? waterMap.Bodies.OrderByDescending(b => b.Tiles.Count).First();

                if (!this.State.Zones.ContainsKey(targetBody.ZoneKey))
                    this.State.Zones[targetBody.ZoneKey] = oldZone;
            }

            this.State.SchemaVersion = 2;
        }

        foreach (var kv in this.State.Zones)
        {
            string zoneKey = kv.Key;
            ZonePopulation zone = kv.Value;

            // If EmptyFarmStart is enabled, clean up any unstocked/unmigrated auto-seeded Farm fish
            if (cfg.EmptyFarmStart && GetLocationNameFromZoneKey(zoneKey).Equals("Farm", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string qid in zone.SpeciesCounts.Keys.ToList())
                {
                    var h = GetOrCreateHistory(zone, qid);
                    if (!zone.PlayerStockedSpecies.Contains(qid)
                        && h.Origin == "Natural"
                        && h.TotalReleased == 0
                        && h.TotalMigratedIn == 0
                        && h.TotalBorn == 0)
                    {
                        zone.SpeciesCounts.Remove(qid);
                        zone.SpeciesDetails.Remove(qid);
                    }
                }
            }
        }

        if (this.State.SchemaVersion < 3 && Context.IsWorldReady)
        {
            this.RebalanceAllZonesForWaterSize();
            this.State.SchemaVersion = 3;
        }

        this.CachesDirty = true;
    }

    private void RebalanceAllZonesForWaterSize()
    {
        foreach (var kv in this.State.Zones.ToList())
        {
            string zoneKey = kv.Key;
            ZonePopulation zone = kv.Value;
            string locName = GetLocationNameFromZoneKey(zoneKey);
            GameLocation? loc = Game1.getLocationFromName(locName);
            if (loc is null)
                continue;

            var profile = this.GetWaterCapacityProfile(loc, zoneKey);
            var allowedNatural = new HashSet<string>(this.GetNaturalSeasonalFishForZone(loc, zoneKey), StringComparer.OrdinalIgnoreCase);

            // Remove out-of-season or excess natural species (never remove player-stocked species)
            foreach (string qid in zone.SpeciesCounts.Keys.ToList())
            {
                if (zone.PlayerStockedSpecies.Contains(qid))
                {
                    if (zone.SpeciesCounts[qid] > profile.MaxPerSpecies)
                        zone.SpeciesCounts[qid] = profile.MaxPerSpecies;
                    continue;
                }

                var hist = GetOrCreateHistory(zone, qid);
                if (hist.Origin == "Natural")
                {
                    if (!allowedNatural.Contains(qid))
                    {
                        zone.SpeciesCounts.Remove(qid);
                        zone.SpeciesDetails.Remove(qid);
                        continue;
                    }

                    int cur = zone.SpeciesCounts[qid];
                    if (hist.TotalBorn == 0 && hist.TotalReleased == 0 && hist.TotalMigratedIn == 0)
                        zone.SpeciesCounts[qid] = Math.Min(cur, profile.BasePerSpecies);
                    else if (cur > profile.MaxPerSpecies)
                        zone.SpeciesCounts[qid] = profile.MaxPerSpecies;
                }
                else if (zone.SpeciesCounts[qid] > profile.MaxPerSpecies)
                {
                    zone.SpeciesCounts[qid] = profile.MaxPerSpecies;
                }
            }

            this.EnforceTotalNaturalCapacity(zone, profile);
        }
    }

    private void EnforceTotalNaturalCapacity(ZonePopulation zone, WaterCapacityProfile profile)
    {
        int total = zone.SpeciesCounts.Values.Sum();
        if (total <= profile.MaxTotalFish)
            return;

        int excess = total - profile.MaxTotalFish;
        var naturalKeys = zone.SpeciesCounts
            .Where(kv => !zone.PlayerStockedSpecies.Contains(kv.Key) && kv.Value > profile.MinFloorPerSpecies)
            .OrderByDescending(kv => kv.Value)
            .Select(kv => kv.Key)
            .ToList();

        while (excess > 0 && naturalKeys.Count > 0)
        {
            bool reducedAny = false;
            for (int i = naturalKeys.Count - 1; i >= 0 && excess > 0; i--)
            {
                string key = naturalKeys[i];
                int val = zone.SpeciesCounts.GetValueOrDefault(key, 0);
                if (val > profile.MinFloorPerSpecies)
                {
                    zone.SpeciesCounts[key] = val - 1;
                    excess--;
                    reducedAny = true;
                }
                else
                {
                    naturalKeys.RemoveAt(i);
                }
            }

            if (!reducedAny)
                break;
        }
    }

    public WaterCapacityProfile GetWaterCapacityProfile(GameLocation? loc, string zoneKey)
    {
        WaterBodyInfo? body = loc is not null ? GetWaterBodyByZoneKey(loc, zoneKey) : null;
        return ComputeCapacityProfile(body, this.GetConfig());
    }

    public WaterCapacityProfile GetWaterCapacityProfile(string zoneKey)
    {
        string locName = GetLocationNameFromZoneKey(zoneKey);
        GameLocation? loc = Game1.getLocationFromName(locName);
        return this.GetWaterCapacityProfile(loc, zoneKey);
    }

    public static WaterCapacityProfile ComputeCapacityProfile(WaterBodyInfo? body, ModConfig cfg)
    {
        int cfgBase = Math.Max(2, cfg.BasePopulationPerSpecies);
        int cfgFloor = Math.Clamp(cfg.MinNaturalFloor, 1, cfgBase);
        int cfgMax = Math.Max(cfgBase + 4, cfg.MaxPopulationPerSpecies);
        int cfgVisible = Math.Max(10, cfg.MaxVisibleFishPerZone);

        if (body is null)
        {
            int fallbackQual = Math.Clamp((int)Math.Round(cfgMax * 0.60), 6, 18);
            int fallbackMig = Math.Clamp((int)Math.Round(cfgMax * 0.50), 10, 20);
            return new WaterCapacityProfile(
                "Gölet",
                MaxNaturalSpecies: 5,
                BasePerSpecies: Math.Clamp((int)Math.Round(cfgBase * 0.65), 4, cfgBase),
                MinFloorPerSpecies: Math.Min(cfgFloor, 2),
                MaxPerSpecies: Math.Clamp((int)Math.Round(cfgMax * 0.60), 15, cfgMax),
                MaxTotalFish: 85,
                MaxVisibleFish: Math.Min(cfgVisible, 20),
                QualityBonusThreshold: fallbackQual,
                MigrationThreshold: fallbackMig);
        }

        int tiles = Math.Max(1, body.Tiles.Count);
        bool open = body.TouchesMapEdge;

        string sizeLabel;
        int maxNaturalSpecies;
        int basePerSpecies;
        int minFloor;
        int maxPerSpecies;
        int maxTotalFish;
        int maxVisible;

        if (!open && tiles <= 15)
        {
            sizeLabel = "Çok Küçük Gölet";
            maxNaturalSpecies = tiles <= 6 ? 1 : 2;
            basePerSpecies = tiles <= 6
                ? Math.Clamp((int)Math.Round(cfgBase * 0.25), 2, 3)
                : Math.Clamp((int)Math.Round(cfgBase * 0.33), 2, 4);
            minFloor = 1;
            maxPerSpecies = Math.Clamp((int)Math.Round(cfgMax * 0.25), 6, Math.Min(12, cfgMax));
            maxTotalFish = tiles <= 6 ? 10 : 18;
            maxVisible = Math.Clamp(tiles / 3, 2, 5);
        }
        else if (!open && tiles <= 40)
        {
            sizeLabel = "Küçük Gölet";
            maxNaturalSpecies = 3;
            basePerSpecies = Math.Clamp((int)Math.Round(cfgBase * 0.45), 3, Math.Min(7, cfgBase));
            minFloor = Math.Min(cfgFloor, 2);
            maxPerSpecies = Math.Clamp((int)Math.Round(cfgMax * 0.38), 10, Math.Min(18, cfgMax));
            maxTotalFish = 35;
            maxVisible = Math.Clamp(tiles / 3, 4, 12);
        }
        else if (!open && tiles <= 120)
        {
            sizeLabel = "Gölet";
            maxNaturalSpecies = 5;
            basePerSpecies = Math.Clamp((int)Math.Round(cfgBase * 0.65), 4, Math.Min(10, cfgBase));
            minFloor = Math.Min(cfgFloor, 2);
            maxPerSpecies = Math.Clamp((int)Math.Round(cfgMax * 0.60), 15, Math.Min(28, cfgMax));
            maxTotalFish = 85;
            maxVisible = Math.Clamp(tiles / 4, 10, 24);
        }
        else if (!open)
        {
            sizeLabel = "Göl";
            maxNaturalSpecies = tiles > 250 ? 24 : 16;
            basePerSpecies = Math.Clamp((int)Math.Round(cfgBase * 0.90), 6, cfgBase);
            minFloor = cfgFloor;
            maxPerSpecies = Math.Clamp((int)Math.Round(cfgMax * 0.90), 20, cfgMax);
            maxTotalFish = Math.Clamp(tiles, 180, 360);
            maxVisible = Math.Min(cfgVisible, Math.Clamp(tiles / 5, 22, 48));
        }
        else if (tiles <= 50)
        {
            sizeLabel = "Küçük Akarsu";
            maxNaturalSpecies = 10;
            basePerSpecies = Math.Clamp((int)Math.Round(cfgBase * 0.65), 4, Math.Min(10, cfgBase));
            minFloor = Math.Min(cfgFloor, 2);
            maxPerSpecies = Math.Clamp((int)Math.Round(cfgMax * 0.65), 16, Math.Min(30, cfgMax));
            maxTotalFish = 110;
            maxVisible = Math.Clamp(tiles / 3, 8, 18);
        }
        else
        {
            sizeLabel = "Akarsu / Kıyı Suyu";
            maxNaturalSpecies = tiles > 200 ? 40 : 28;
            basePerSpecies = cfgBase;
            minFloor = cfgFloor;
            maxPerSpecies = cfgMax;
            maxTotalFish = Math.Clamp(tiles * 2, 260, 600);
            maxVisible = Math.Min(cfgVisible, Math.Clamp(tiles / 5, 24, 60));
        }

        minFloor = Math.Clamp(minFloor, 1, Math.Max(1, basePerSpecies));
        maxPerSpecies = Math.Max(basePerSpecies + 2, maxPerSpecies);
        int qualityThreshold = Math.Clamp((int)Math.Round(maxPerSpecies * 0.60), 6, 18);
        int migrationThreshold = Math.Clamp((int)Math.Round(maxPerSpecies * 0.50), 10, 20);

        return new WaterCapacityProfile(
            sizeLabel,
            maxNaturalSpecies,
            basePerSpecies,
            minFloor,
            maxPerSpecies,
            maxTotalFish,
            Math.Min(cfgVisible, maxVisible),
            qualityThreshold,
            migrationThreshold);
    }

    public static SpeciesHistory GetOrCreateHistory(ZonePopulation zone, string qid)
    {
        if (!zone.SpeciesDetails.TryGetValue(qid, out var hist))
        {
            hist = new SpeciesHistory
            {
                Origin = zone.PlayerStockedSpecies.Contains(qid) ? "Stocked" : "Natural",
                FirstSeenTotalDays = Context.IsWorldReady ? Game1.Date.TotalDays : -1
            };
            zone.SpeciesDetails[qid] = hist;
        }
        return hist;
    }

    public static string FormatGameDate(int totalDays)
    {
        if (totalDays < 0)
            return "Bilinmiyor";

        int year = (totalDays / 112) + 1;
        int rem = totalDays % 112;
        int seasonIdx = rem / 28;
        int day = (rem % 28) + 1;
        string seasonTr = seasonIdx switch
        {
            0 => "Bahar",
            1 => "Yaz",
            2 => "Güz",
            3 => "Kış",
            _ => "Bahar"
        };

        string dateLabel = $"{year}. Yıl {seasonTr} {day}";
        if (Context.IsWorldReady)
        {
            int diff = Game1.Date.TotalDays - totalDays;
            if (diff == 0)
                return $"Bugün ({dateLabel})";
            if (diff == 1)
                return $"Dün ({dateLabel})";
            if (diff > 1)
                return $"{dateLabel} ({diff} gün önce)";
        }

        return dateLabel;
    }

    public List<(string ZoneKey, ZonePopulation Zone)> GetJournalZones(GameLocation? currentLoc)
    {
        var result = new List<(string ZoneKey, ZonePopulation Zone)>();
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (currentLoc is not null && this.HasWaterOrFishData(currentLoc))
        {
            var localZones = this.GetZonesForLocation(currentLoc);
            foreach (var z in localZones)
            {
                if (added.Add(z.ZoneKey))
                    result.Add(z);
            }
        }

        foreach (var kv in this.State.Zones
            .Where(kv => kv.Value.PlayerStockedSpecies.Count > 0
                || kv.Value.SpeciesDetails.Values.Any(h => h.Origin == "Migration" || h.TotalBorn > 0))
            .OrderBy(kv => GetDisplayLocationName(kv.Key)))
        {
            if (added.Add(kv.Key))
                result.Add((kv.Key, kv.Value));
        }

        return result;
    }

    public bool HasAnyZone(string locName) =>
        this.State.Zones.Keys.Any(k => k.Equals(locName, StringComparison.OrdinalIgnoreCase)
            || k.StartsWith(locName + "::", StringComparison.OrdinalIgnoreCase));

    public static bool IsWaterTileSafe(GameLocation loc, int x, int y)
    {
        if (loc.isWaterTile(x, y))
            return true;

        var wt = loc.waterTiles?.waterTiles;
        return wt is not null
            && x >= 0 && y >= 0
            && x < wt.GetLength(0) && y < wt.GetLength(1)
            && wt[x, y].isWater;
    }

    private static LocationWaterMap GetOrBuildWaterMap(GameLocation loc)
    {
        string cacheKey = loc.NameOrUniqueName ?? loc.Name;
        bool hasWaterTiles = loc.waterTiles is not null;
        if (WaterMapCache.TryGetValue(cacheKey, out var cached)
            && ReferenceEquals(cached.LocationRef, loc)
            && ReferenceEquals(cached.MapRef, loc.map)
            && cached.HadWaterTiles == hasWaterTiles)
        {
            return cached.WaterMap;
        }

        var built = new LocationWaterMap(loc);
        WaterMapCache[cacheKey] = new CachedWaterMapEntry
        {
            LocationRef = loc,
            MapRef = loc.map,
            HadWaterTiles = hasWaterTiles,
            WaterMap = built
        };
        return built;
    }

    private static WaterBodyInfo? ResolveWaterBodyAtOrNear(GameLocation loc, Vector2 tile, int maxRadius = 3)
    {
        var waterMap = GetOrBuildWaterMap(loc);
        if (waterMap.Bodies.Count == 0)
            return null;

        int cx = (int)MathF.Floor(tile.X);
        int cy = (int)MathF.Floor(tile.Y);
        if (cx >= 0 && cy >= 0 && cx < waterMap.Width && cy < waterMap.Height)
        {
            int exactIdx = waterMap.ComponentGrid[cx, cy];
            if (exactIdx >= 0 && exactIdx < waterMap.Bodies.Count)
                return waterMap.Bodies[exactIdx];
        }

        int bestIdx = -1;
        int bestDistSq = maxRadius * maxRadius + 1;
        for (int dy = -maxRadius; dy <= maxRadius; dy++)
        {
            for (int dx = -maxRadius; dx <= maxRadius; dx++)
            {
                int distSq = dx * dx + dy * dy;
                if (distSq >= bestDistSq)
                    continue;

                int nx = cx + dx;
                int ny = cy + dy;
                if (nx < 0 || ny < 0 || nx >= waterMap.Width || ny >= waterMap.Height)
                    continue;

                int idx = waterMap.ComponentGrid[nx, ny];
                if (idx >= 0)
                {
                    bestDistSq = distSq;
                    bestIdx = idx;
                }
            }
        }

        return bestIdx >= 0 && bestIdx < waterMap.Bodies.Count
            ? waterMap.Bodies[bestIdx]
            : null;
    }

    public WaterBodyInfo? GetWaterBodyAtExactTile(GameLocation loc, Vector2 tile)
    {
        var waterMap = GetOrBuildWaterMap(loc);
        int tx = (int)MathF.Floor(tile.X);
        int ty = (int)MathF.Floor(tile.Y);
        if (tx < 0 || ty < 0 || tx >= waterMap.Width || ty >= waterMap.Height)
            return null;

        int compIdx = waterMap.ComponentGrid[tx, ty];
        return compIdx >= 0 && compIdx < waterMap.Bodies.Count
            ? waterMap.Bodies[compIdx]
            : null;
    }

    public static WaterBodyInfo? GetWaterBodyByZoneKey(GameLocation loc, string zoneKey)
    {
        var waterMap = GetOrBuildWaterMap(loc);
        return waterMap.ByZoneKey.TryGetValue(zoneKey, out var body) ? body : null;
    }

    public static float GetMinDistanceToWaterBody(WaterBodyInfo body, Vector2 playerTile)
    {
        float minDistSq = float.MaxValue;
        foreach (var (tx, ty) in body.Tiles)
        {
            float dx = playerTile.X - tx;
            float dy = playerTile.Y - ty;
            float distSq = dx * dx + dy * dy;
            if (distSq < minDistSq)
                minDistSq = distSq;
        }
        return minDistSq < float.MaxValue ? MathF.Sqrt(minDistSq) : float.MaxValue;
    }

    public static float GetMinDistanceToAnyWater(GameLocation loc, Vector2 playerTile)
    {
        var waterMap = GetOrBuildWaterMap(loc);
        float minDist = float.MaxValue;
        foreach (var body in waterMap.Bodies)
        {
            float dist = GetMinDistanceToWaterBody(body, playerTile);
            if (dist < minDist)
                minDist = dist;
        }
        return minDist;
    }

    public bool IsPlayerNearWaterBody(GameLocation loc, Vector2 waterTile, Vector2 playerTile, float maxDistance = 5.5f)
    {
        WaterBodyInfo? body = this.GetWaterBodyAtExactTile(loc, waterTile)
            ?? ResolveWaterBodyAtOrNear(loc, waterTile, maxRadius: 1);
        return body is not null && GetMinDistanceToWaterBody(body, playerTile) <= maxDistance;
    }

    public bool TryGetInspectableWaterZone(
        GameLocation? loc,
        Vector2 playerTile,
        Vector2 cursorTile,
        out string? zoneKey,
        out string warningMessage,
        float maxDistance = 5.5f)
    {
        zoneKey = null;
        warningMessage = string.Empty;

        var cfg = this.GetConfig();
        if (loc is null || GetOrBuildWaterMap(loc).Bodies.Count == 0)
        {
            warningMessage = "Canlı Sular: Bu bölgede incelenecek bir su kaynağı yok.";
            return false;
        }

        if (cfg.OnlyFarmWaters && loc is not Farm)
        {
            warningMessage = "Canlı Sular: Sadece Çiftlik Suları ayarı açık.";
            return false;
        }

        WaterBodyInfo? hoveredBody = this.GetWaterBodyAtExactTile(loc, cursorTile);
        if (hoveredBody is null)
        {
            float nearestWaterDist = GetMinDistanceToAnyWater(loc, playerTile);
            warningMessage = nearestWaterDist > maxDistance
                ? "Canlı Sular: İstediğiniz suya bakmak için suyun yanına yaklaşın ve imleci suyun üstüne getirin."
                : "Canlı Sular: İncelemek istediğiniz suyun üstüne gelip F6 tuşuna basın.";
            return false;
        }

        float distToHovered = GetMinDistanceToWaterBody(hoveredBody, playerTile);
        if (distToHovered > maxDistance)
        {
            warningMessage = "Canlı Sular: İstediğiniz suya bakmak için o suyun yanına yaklaşın.";
            return false;
        }

        this.EnsureZoneInitialized(loc, hoveredBody.ZoneKey);
        zoneKey = hoveredBody.ZoneKey;
        return true;
    }

    public bool HasWaterOrFishData(GameLocation loc)
    {
        if (GetOrBuildWaterMap(loc).Bodies.Count > 0 || this.HasAnyZone(loc.Name) || loc.waterTiles is not null)
            return true;

        return loc.GetData()?.Fish is { Count: > 0 };
    }

    public WaterEcosystemSaveData ExportState() => this.State;

    public void InvalidateProtectionCache() => this.CachesDirty = true;

    public static string NormalizeItemId(string? rawId)
    {
        if (string.IsNullOrWhiteSpace(rawId))
            return string.Empty;
        return rawId.StartsWith("(", StringComparison.Ordinal) ? rawId : "(O)" + rawId;
    }

    public static string GetZoneKey(GameLocation loc, Vector2 tile) =>
        ResolveWaterBodyAtOrNear(loc, tile, maxRadius: 3)?.ZoneKey ?? loc.Name;

    public static string GetLocationNameFromZoneKey(string zoneKey)
    {
        int sep = zoneKey.IndexOf("::", StringComparison.Ordinal);
        return sep > 0 ? zoneKey[..sep] : zoneKey;
    }

    public static string GetDisplayLocationName(string zoneKey)
    {
        string locName = GetLocationNameFromZoneKey(zoneKey);
        GameLocation? loc = Game1.getLocationFromName(locName);
        string baseName = locName switch
        {
            "Farm" => "Çiftlik",
            "Town" => "Pelikan Kasabası",
            "Forest" => "Kömürözü Ormanı",
            "Mountain" => "Dağ",
            "Beach" => "Kumsal",
            "Woods" => "Gizli Orman",
            "Desert" => "Çöl",
            "Sewer" => "Kanalizasyon",
            "BugLand" => "Böcek İni",
            "WitchSwamp" => "Cadı Bataklığı",
            "Submarine" => "Denizaltı",
            "Caldera" => "Kaldera",
            "UndergroundMine" => "Madenler",
            "IslandSouth" => "Zencefil Adası (Güney)",
            "IslandWest" => "Zencefil Adası (Batı)",
            "IslandNorth" => "Zencefil Adası (Kuzey)",
            "IslandEast" => "Zencefil Adası (Doğu)",
            "IslandSouthEastCave" => "Korsan Mağarası",
            _ => !string.IsNullOrWhiteSpace(loc?.DisplayName) ? loc.DisplayName : locName
        };

        int sep = zoneKey.IndexOf("::", StringComparison.Ordinal);
        if (sep > 0)
        {
            string suffix = zoneKey[(sep + 2)..];
            if (suffix.StartsWith("water_", StringComparison.OrdinalIgnoreCase))
            {
                if (loc is not null && GetWaterBodyByZoneKey(loc, zoneKey) is { } body)
                    return $"{baseName} - {body.SubLabel}";

                string[] parts = suffix.Split('_');
                if (parts.Length == 3 && int.TryParse(parts[1], out int wx) && int.TryParse(parts[2], out int wy))
                    return $"{baseName} - Su ({wx}, {wy})";
            }

            string? areaDisplay = loc?.GetFishingAreaDisplayName(suffix);
            return !string.IsNullOrWhiteSpace(areaDisplay) ? $"{baseName} ({areaDisplay})" : $"{baseName} ({suffix})";
        }
        return baseName;
    }

    public bool IsValidSwimmingFish(string? rawId)
    {
        string qid = NormalizeItemId(rawId);
        if (string.IsNullOrEmpty(qid))
            return false;

        var data = ItemRegistry.GetData(qid);
        if (data is null || data.Category != StardewValley.Object.FishCategory)
            return false;

        var fishDict = DataLoader.Fish(Game1.content);
        string rawKey = qid.StartsWith("(O)", StringComparison.Ordinal) ? qid[3..] : qid;
        if (!fishDict.TryGetValue(rawKey, out string? row) && !fishDict.TryGetValue(qid, out row))
            return false;

        string[] parts = row.Split('/');
        return parts.Length < 2 || !parts[1].Equals("trap", StringComparison.OrdinalIgnoreCase);
    }

    public bool IsBossOrLegendaryFish(string? rawId)
    {
        this.EnsureCaches();
        string qid = NormalizeItemId(rawId);
        return this.BossFishIds.Contains(qid);
    }

    public bool IsProtectedFish(Farmer player, string? rawId)
    {
        var cfg = this.GetConfig();
        if (!cfg.ProtectNeededFish)
            return false;

        this.EnsureCaches();
        string qid = NormalizeItemId(rawId);
        if (string.IsNullOrEmpty(qid))
            return false;

        if (!player.fishCaught.ContainsKey(qid) && !player.fishCaught.ContainsKey(qid.StartsWith("(O)") ? qid[3..] : qid))
            return true;

        return this.ProtectedFishIds.Contains(qid);
    }

    public ZonePopulation? GetOrInitZone(GameLocation loc, Vector2 tile)
    {
        var cfg = this.GetConfig();
        if (!cfg.EnableMod)
            return null;
        if (cfg.OnlyFarmWaters && loc is not Farm)
            return null;

        string zoneKey = GetZoneKey(loc, tile);
        return this.EnsureZoneInitialized(loc, zoneKey);
    }

    public ZonePopulation? GetZoneByKey(string zoneKey) =>
        this.State.Zones.TryGetValue(zoneKey, out var zone) ? zone : null;

    public List<(string ZoneKey, ZonePopulation Zone)> GetZonesForLocation(GameLocation loc)
    {
        var cfg = this.GetConfig();
        if (!cfg.EnableMod)
            return new List<(string, ZonePopulation)>();
        if (cfg.OnlyFarmWaters && loc is not Farm)
            return new List<(string, ZonePopulation)>();

        return this.State.Zones
            .Where(kv => kv.Key.StartsWith(loc.Name + "::water_", StringComparison.OrdinalIgnoreCase))
            .Select(kv => (kv.Key, kv.Value))
            .ToList();
    }

    public bool TryReleaseFish(GameLocation loc, Vector2 tile, Farmer player, out string message, out string? releasedQid)
    {
        message = string.Empty;
        releasedQid = null;
        var cfg = this.GetConfig();
        if (!cfg.EnableMod)
            return false;
        if (cfg.OnlyFarmWaters && loc is not Farm)
            return false;
        if (player.ActiveObject is not { } held || held.Category != StardewValley.Object.FishCategory)
            return false;
        int tx = (int)MathF.Floor(tile.X);
        int ty = (int)MathF.Floor(tile.Y);
        if (!IsWaterTileSafe(loc, tx, ty) || loc.objects.ContainsKey(tile))
            return false;
        if (Vector2.Distance(player.Tile, tile) > 5.5f)
            return false;

        string qid = NormalizeItemId(held.QualifiedItemId);
        if (!this.IsValidSwimmingFish(qid))
        {
            message = $"{held.DisplayName} bu suya salınabilecek bir yüzücü balık değil.";
            return true;
        }

        if (this.IsBossOrLegendaryFish(qid))
        {
            message = "Efsanevi balıklar suya geri salınamaz.";
            return true;
        }

        string zoneKey = GetZoneKey(loc, tile);
        var zone = this.EnsureZoneInitialized(loc, zoneKey);
        if (zone is null)
            return false;

        var profile = this.GetWaterCapacityProfile(loc, zoneKey);
        int cur = zone.SpeciesCounts.GetValueOrDefault(qid, 0);
        if (cur >= profile.MaxPerSpecies)
        {
            message = $"{held.DisplayName} bu suda zaten azami tür kapasitesine ({profile.MaxPerSpecies}) ulaşmış.";
            return true;
        }

        int totalInZone = zone.SpeciesCounts.Values.Sum();
        if (totalInZone >= profile.MaxTotalFish)
        {
            message = $"Bu su toplam kapasitesine ({totalInZone}/{profile.MaxTotalFish} balık) ulaşmış, daha fazla balık salınamaz.";
            return true;
        }

        string displayName = held.DisplayName;
        player.reduceActiveItemByOne();

        int next = cur + 1;
        zone.SpeciesCounts[qid] = next;
        zone.PlayerStockedSpecies.Add(qid);
        releasedQid = qid;

        var hist = GetOrCreateHistory(zone, qid);
        if (hist.TotalReleased == 0 && cur == 0)
        {
            hist.Origin = "Stocked";
            hist.FirstSeenTotalDays = Game1.Date.TotalDays;
        }
        else if (hist.Origin == "Natural" && loc is Farm)
        {
            hist.Origin = "Stocked";
        }
        hist.TotalReleased++;

        string locLabel = GetDisplayLocationName(zoneKey);
        string breedHint = next == 1
            ? " (Üremesi için aynı türden 1 tane daha sal!)"
            : " (Üreme çifti hazır)";
        message = $"{displayName} suya salındı! {locLabel}: {next} adet{breedHint}";
        return true;
    }

    public void ProcessNewDay(out string? morningToast)
    {
        morningToast = null;
        var cfg = this.GetConfig();
        if (!cfg.EnableMod || !Context.IsWorldReady)
            return;

        this.InvalidateProtectionCache();
        this.EnsureCaches();

        int today = Game1.Date.TotalDays;
        if (this.State.LastProcessedTotalDays == today)
            return;

        bool isSeasonChanged = !string.IsNullOrEmpty(this.State.LastSeason)
            && !string.Equals(this.State.LastSeason, Game1.currentSeason, StringComparison.OrdinalIgnoreCase);

        int stockedBirths = 0;
        int totalMigrations = 0;

        foreach (var kv in this.State.Zones.ToList())
        {
            string zoneKey = kv.Key;
            ZonePopulation zone = kv.Value;
            zone.DailyCaughtToday.Clear();

            string locName = GetLocationNameFromZoneKey(zoneKey);
            GameLocation? loc = Game1.getLocationFromName(locName);
            if (loc is null)
                continue;

            bool isFarm = loc is Farm;
            if (cfg.OnlyFarmWaters && !isFarm)
                continue;

            if (isSeasonChanged || !string.Equals(zone.LastSeasonInitialized, Game1.currentSeason, StringComparison.OrdinalIgnoreCase))
                this.ReplenishNaturalZone(loc, zoneKey, zone);

            var profile = this.GetWaterCapacityProfile(loc, zoneKey);
            int totalInZone = zone.SpeciesCounts.Values.Sum();

            // Breeding pass
            var speciesKeys = zone.SpeciesCounts.Keys.ToList();
            foreach (string qid in speciesKeys)
            {
                if (totalInZone >= profile.MaxTotalFish)
                    break;

                int count = zone.SpeciesCounts[qid];
                if (count < 2 || count >= profile.MaxPerSpecies)
                    continue;

                if (!(isFarm && cfg.FarmAllowsAllSeasons) && !this.IsFishInSeasonForZone(locName, qid))
                    continue;

                int pairs = count / 2;
                int births = 0;
                double chance = Math.Clamp(cfg.BreedingChancePercent, 0, 100) / 100.0;
                for (int i = 0; i < pairs && births < 3; i++)
                {
                    if (Game1.random.NextDouble() < chance)
                        births++;
                }

                if (births > 0)
                {
                    int roomInPond = Math.Max(0, profile.MaxTotalFish - totalInZone);
                    int updated = Math.Min(profile.MaxPerSpecies, count + Math.Min(births, roomInPond));
                    int actual = updated - count;
                    zone.SpeciesCounts[qid] = updated;
                    if (actual > 0)
                    {
                        totalInZone += actual;
                        var hist = GetOrCreateHistory(zone, qid);
                        hist.TotalBorn += actual;
                        hist.LastBirthTotalDays = today;
                        if (hist.FirstSeenTotalDays < 0)
                            hist.FirstSeenTotalDays = today;

                        if (isFarm || zone.PlayerStockedSpecies.Contains(qid))
                            stockedBirths += actual;
                    }
                }
            }
        }

        // Migration pass between connected waterways
        if (cfg.MigrationChancePercent > 0 && !cfg.OnlyFarmWaters)
        {
            double migChance = Math.Clamp(cfg.MigrationChancePercent, 0, 100) / 100.0;
            foreach (var kv in this.State.Zones.ToList())
            {
                string srcKey = kv.Key;
                ZonePopulation srcZone = kv.Value;
                string srcLocName = GetLocationNameFromZoneKey(srcKey);
                if (!ConnectedWaterways.TryGetValue(srcLocName, out string[]? neighbors))
                    continue;

                GameLocation? srcLoc = Game1.getLocationFromName(srcLocName);
                if (srcLoc is not null)
                {
                    WaterBodyInfo? srcBody = GetWaterBodyByZoneKey(srcLoc, srcKey);
                    if (srcBody is not null && !srcBody.TouchesMapEdge)
                        continue;
                }

                var srcProfile = this.GetWaterCapacityProfile(srcLoc, srcKey);

                foreach (string qid in srcZone.SpeciesCounts.Keys.ToList())
                {
                    int srcCount = srcZone.SpeciesCounts[qid];
                    if (srcCount < srcProfile.MigrationThreshold)
                        continue;

                    if (Game1.random.NextDouble() >= migChance)
                        continue;

                    string targetLocName = neighbors[Game1.random.Next(neighbors.Length)];
                    GameLocation? targetLoc = Game1.getLocationFromName(targetLocName);
                    if (targetLoc is null)
                        continue;

                    var targetResolved = this.ResolveMigrationTargetZone(targetLoc);
                    if (targetResolved is null)
                        continue;

                    var (targetZoneKey, targetZone) = targetResolved.Value;
                    var tgtProfile = this.GetWaterCapacityProfile(targetLoc, targetZoneKey);

                    int curTarget = targetZone.SpeciesCounts.GetValueOrDefault(qid, 0);
                    if (curTarget >= tgtProfile.MaxPerSpecies)
                        continue;
                    if (targetZone.SpeciesCounts.Values.Sum() >= tgtProfile.MaxTotalFish)
                        continue;

                    if (!(targetLoc is Farm && cfg.FarmAllowsAllSeasons) && !this.IsFishInSeasonForZone(targetLocName, qid))
                        continue;

                    srcZone.SpeciesCounts[qid] = srcCount - 1;
                    targetZone.SpeciesCounts[qid] = curTarget + 1;
                    if (srcZone.PlayerStockedSpecies.Contains(qid))
                        targetZone.PlayerStockedSpecies.Add(qid);

                    var srcHist = GetOrCreateHistory(srcZone, qid);
                    srcHist.TotalMigratedOut++;

                    var tgtHist = GetOrCreateHistory(targetZone, qid);
                    tgtHist.TotalMigratedIn++;
                    tgtHist.LastMigrationTotalDays = today;
                    if (curTarget == 0)
                    {
                        tgtHist.Origin = "Migration";
                        tgtHist.SourceLocation = srcLocName;
                        tgtHist.FirstSeenTotalDays = today;
                    }
                    else if (tgtHist.Origin == "Migration" && string.IsNullOrEmpty(tgtHist.SourceLocation))
                    {
                        tgtHist.SourceLocation = srcLocName;
                    }

                    totalMigrations++;
                }
            }
        }

        this.State.LastProcessedTotalDays = today;
        this.State.LastSeason = Game1.currentSeason;

        if (stockedBirths > 0)
        {
            morningToast = totalMigrations > 0
                ? $"Canlı Sular: Sularınızda gece {stockedBirths} yeni balık üredi, {totalMigrations} balık göç etti!"
                : $"Canlı Sular: Sularınızda gece {stockedBirths} yeni balık üredi!";
        }
    }

    public Item? ModifyGetFishResult(GameLocation loc, Vector2 bobberTile, Farmer? who, Item? originalResult)
    {
        var cfg = this.GetConfig();
        if (!cfg.EnableMod)
            return originalResult;
        if (cfg.OnlyFarmWaters && loc is not Farm)
            return originalResult;

        Farmer player = who ?? Game1.player;
        string zoneKey = GetZoneKey(loc, bobberTile);
        var zone = this.EnsureZoneInitialized(loc, zoneKey);
        if (zone is null)
            return originalResult;

        var profile = this.GetWaterCapacityProfile(loc, zoneKey);

        // 1. If player stocked fish here (or wild migrants arrived on Farm), let them bite & appear in VisibleFish
        var stockedCandidates = zone.SpeciesCounts
            .Where(kv => kv.Value > 0
                && this.IsValidSwimmingFish(kv.Key)
                && (zone.PlayerStockedSpecies.Contains(kv.Key)
                    || (loc is Farm && !this.IsNativeToLocation(loc.Name, kv.Key)))
                && ((loc is Farm && cfg.FarmAllowsAllSeasons) || this.IsFishInSeasonForZone(loc.Name, kv.Key)))
            .Select(kv => (Qid: kv.Key, Count: kv.Value))
            .ToList();

        if (stockedCandidates.Count > 0)
        {
            int stockedTotal = stockedCandidates.Sum(x => x.Count);
            bool vanillaIsTrashOrEmpty = originalResult is null || originalResult.Category == StardewValley.Object.junkCategory;
            double stockedRatio = Math.Min(0.75, stockedTotal / (stockedTotal + 8.0));

            if (vanillaIsTrashOrEmpty && Game1.random.NextDouble() < Math.Min(0.85, stockedRatio + 0.15))
            {
                string picked = PickWeighted(stockedCandidates);
                Item created = ItemRegistry.Create(picked);
                this.ApplyPopulationQualityBonus(zone, picked, created, profile);
                return created;
            }
            if (originalResult is not null && originalResult.Category == StardewValley.Object.FishCategory)
            {
                string preCheckQid = NormalizeItemId(originalResult.QualifiedItemId);
                if (!this.IsBossOrLegendaryFish(preCheckQid)
                    && !this.IsProtectedFish(player, preCheckQid)
                    && Game1.random.NextDouble() < stockedRatio * 0.65)
                {
                    string picked = PickWeighted(stockedCandidates);
                    Item created = ItemRegistry.Create(picked);
                    this.ApplyPopulationQualityBonus(zone, picked, created, profile);
                    return created;
                }
            }
        }

        // 2. Natural population weighting (never suppresses protected CC / uncaught / quest fish)
        if (originalResult is null || originalResult.Category != StardewValley.Object.FishCategory)
            return originalResult;

        string origQid = NormalizeItemId(originalResult.QualifiedItemId);
        if (!this.IsValidSwimmingFish(origQid))
            return originalResult;

        // If Farm starts empty, keep Farm hook strictly synced to stocked/migrated species in F6
        if (loc is Farm
            && cfg.EmptyFarmStart
            && zone.SpeciesCounts.GetValueOrDefault(origQid, 0) <= 0
            && !this.IsBossOrLegendaryFish(origQid)
            && !this.IsProtectedFish(player, origQid))
        {
            if (stockedCandidates.Count > 0)
            {
                string picked = PickWeighted(stockedCandidates);
                Item created = ItemRegistry.Create(picked);
                this.ApplyPopulationQualityBonus(zone, picked, created, profile);
                return created;
            }
            return ItemRegistry.Create("(O)168");
        }

        // Keep catches in small ponds aligned with the species that actually inhabit that water body
        WaterBodyInfo? catchBody = GetWaterBodyByZoneKey(loc, zoneKey);
        bool isSmallClosedPond = catchBody is not null && !catchBody.TouchesMapEdge && catchBody.Tiles.Count <= 120;
        if (isSmallClosedPond
            && zone.SpeciesCounts.GetValueOrDefault(origQid, 0) <= 0
            && !this.IsBossOrLegendaryFish(origQid)
            && !this.IsProtectedFish(player, origQid))
        {
            var residentSpecies = zone.SpeciesCounts
                .Where(kv => kv.Value > 0
                    && this.IsValidSwimmingFish(kv.Key)
                    && ((loc is Farm && cfg.FarmAllowsAllSeasons) || this.IsFishInSeasonForZone(loc.Name, kv.Key)))
                .Select(kv => (Qid: kv.Key, Count: kv.Value))
                .ToList();

            if (residentSpecies.Count > 0)
            {
                string picked = PickWeighted(residentSpecies);
                Item created = ItemRegistry.Create(picked);
                this.ApplyPopulationQualityBonus(zone, picked, created, profile);
                return created;
            }
        }

        if (!this.IsProtectedFish(player, origQid)
            && zone.SpeciesCounts.TryGetValue(origQid, out int origCount)
            && origCount <= profile.MinFloorPerSpecies)
        {
            var abundant = zone.SpeciesCounts
                .Where(kv => kv.Value >= profile.BasePerSpecies
                    && kv.Key != origQid
                    && this.IsValidSwimmingFish(kv.Key)
                    && this.IsFishInSeasonForZone(loc.Name, kv.Key))
                .Select(kv => (Qid: kv.Key, Count: kv.Value))
                .ToList();

            if (abundant.Count > 0 && Game1.random.NextDouble() < 0.35)
            {
                string replacement = PickWeighted(abundant);
                Item swapped = ItemRegistry.Create(replacement);
                this.ApplyPopulationQualityBonus(zone, replacement, swapped, profile);
                return swapped;
            }
        }

        this.ApplyPopulationQualityBonus(zone, origQid, originalResult, profile);
        return originalResult;
    }

    public void OnFishPulledFromWater(GameLocation loc, Vector2 bobberTile, Farmer player, string fishId, int numCaught)
    {
        var cfg = this.GetConfig();
        if (!cfg.EnableMod || numCaught <= 0)
            return;
        if (cfg.OnlyFarmWaters && loc is not Farm)
            return;

        string qid = NormalizeItemId(fishId);
        if (!this.IsValidSwimmingFish(qid))
            return;

        string zoneKey = GetZoneKey(loc, bobberTile);
        var zone = this.EnsureZoneInitialized(loc, zoneKey);
        if (zone is null)
            return;

        zone.DailyCaughtToday[qid] = zone.DailyCaughtToday.GetValueOrDefault(qid, 0) + numCaught;
        var catchHist = GetOrCreateHistory(zone, qid);
        catchHist.TotalCaught += numCaught;

        if (this.IsProtectedFish(player, qid))
            return;

        var profile = this.GetWaterCapacityProfile(loc, zoneKey);
        int cur = zone.SpeciesCounts.GetValueOrDefault(qid, profile.BasePerSpecies);
        bool isNative = this.IsNativeToLocation(loc.Name, qid);
        int floor = (!isNative && zone.PlayerStockedSpecies.Contains(qid)) || cfg.AllowLocalExtinction
            ? 0
            : profile.MinFloorPerSpecies;

        int next = Math.Max(floor, cur - numCaught);
        zone.SpeciesCounts[qid] = next;

        if (zone.PlayerStockedSpecies.Contains(qid) && next <= 2 && cur > 2)
        {
            string fishName = ItemRegistry.GetData(qid)?.DisplayName ?? qid;
            Game1.addHUDMessage(new HUDMessage($"Canlı Sular: Bu sudaki {fishName} sayısı azaldı ({next} kaldı).", HUDMessage.newQuest_type) { timeLeft = 2400f });
        }
    }

    private void ApplyPopulationQualityBonus(ZonePopulation zone, string qid, Item item, WaterCapacityProfile profile)
    {
        if (zone.SpeciesCounts.GetValueOrDefault(qid, 0) < profile.QualityBonusThreshold)
            return;
        if (Game1.random.NextDouble() >= 0.30)
            return;

        item.Quality = item.Quality switch
        {
            0 => 1,
            1 => 2,
            2 => 4,
            _ => item.Quality
        };
    }

    private ZonePopulation? EnsureZoneInitialized(GameLocation loc, string zoneKey)
    {
        if (!this.State.Zones.TryGetValue(zoneKey, out var zone))
        {
            zone = new ZonePopulation();
            this.State.Zones[zoneKey] = zone;
        }

        if (!string.Equals(zone.LastSeasonInitialized, Game1.currentSeason, StringComparison.OrdinalIgnoreCase))
        {
            this.ReplenishNaturalZone(loc, zoneKey, zone);
        }
        else if (this.SyncedZonesThisSession.Add(zoneKey))
        {
            // Sync newly installed fish mods (e.g. More New Fish) into already-initialized zones of the current season
            this.SyncMissingNaturalSpecies(loc, zoneKey, zone);
        }

        return zone;
    }

    private readonly HashSet<string> SyncedZonesThisSession = new(StringComparer.OrdinalIgnoreCase);

    private void SyncMissingNaturalSpecies(GameLocation loc, string zoneKey, ZonePopulation zone)
    {
        var profile = this.GetWaterCapacityProfile(loc, zoneKey);
        var naturalIds = this.GetNaturalSeasonalFishForZone(loc, zoneKey);
        int totalInZone = zone.SpeciesCounts.Values.Sum();
        int target = Math.Min(profile.BasePerSpecies, profile.MaxPerSpecies);

        foreach (string qid in naturalIds)
        {
            if (zone.SpeciesDetails.ContainsKey(qid))
                continue;

            int cur = zone.SpeciesCounts.GetValueOrDefault(qid, 0);
            if (cur == 0 && totalInZone < profile.MaxTotalFish)
            {
                int add = Math.Min(target, profile.MaxTotalFish - totalInZone);
                if (add > 0)
                {
                    zone.SpeciesCounts[qid] = add;
                    totalInZone += add;
                    zone.SpeciesDetails[qid] = new SpeciesHistory
                    {
                        Origin = "Natural",
                        FirstSeenTotalDays = Context.IsWorldReady ? Game1.Date.TotalDays : -1
                    };
                }
            }
        }
    }

    private void ReplenishNaturalZone(GameLocation loc, string zoneKey, ZonePopulation zone)
    {
        var profile = this.GetWaterCapacityProfile(loc, zoneKey);
        var naturalIds = this.GetNaturalSeasonalFishForZone(loc, zoneKey);
        var naturalSet = new HashSet<string>(naturalIds, StringComparer.OrdinalIgnoreCase);

        // Clear out-of-season non-stocked fish when seasons change so small ponds don't accumulate every season's species
        foreach (string existingQid in zone.SpeciesCounts.Keys.ToList())
        {
            if (zone.PlayerStockedSpecies.Contains(existingQid))
                continue;

            if (!naturalSet.Contains(existingQid) && !this.IsFishInSeasonForZone(loc.Name, existingQid))
            {
                zone.SpeciesCounts.Remove(existingQid);
                zone.SpeciesDetails.Remove(existingQid);
            }
        }

        int totalInZone = zone.SpeciesCounts.Values.Sum();
        foreach (string qid in naturalIds)
        {
            int cur = zone.SpeciesCounts.GetValueOrDefault(qid, 0);
            int target = Math.Min(profile.BasePerSpecies, profile.MaxPerSpecies);
            if (cur < target && totalInZone < profile.MaxTotalFish)
            {
                int add = Math.Min(target - cur, profile.MaxTotalFish - totalInZone);
                if (add > 0)
                {
                    zone.SpeciesCounts[qid] = cur + add;
                    totalInZone += add;
                }
            }

            if (zone.SpeciesCounts.GetValueOrDefault(qid, 0) > 0 && !zone.SpeciesDetails.ContainsKey(qid))
            {
                zone.SpeciesDetails[qid] = new SpeciesHistory
                {
                    Origin = "Natural",
                    FirstSeenTotalDays = Context.IsWorldReady ? Game1.Date.TotalDays : -1
                };
            }
        }

        this.EnforceTotalNaturalCapacity(zone, profile);
        zone.LastSeasonInitialized = Game1.currentSeason;
    }

    private List<string> GetNaturalSeasonalFishForZone(GameLocation loc, string zoneKey)
    {
        if (loc is Farm && this.GetConfig().EmptyFarmStart)
            return new List<string>();

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        WaterBodyInfo? body = GetWaterBodyByZoneKey(loc, zoneKey);
        int sep = zoneKey.IndexOf("::", StringComparison.Ordinal);
        string? areaId = body?.FishAreaId;
        if (areaId is null && sep > 0)
        {
            string suffix = zoneKey[(sep + 2)..];
            if (!suffix.StartsWith("water_", StringComparison.OrdinalIgnoreCase))
                areaId = suffix;
        }

        try
        {
            var locationsData = DataLoader.Locations(Game1.content);
            LocationData? locData = loc.GetData();
            if (locData?.Fish is null)
                locationsData.TryGetValue(loc.Name, out locData);

            bool usingLocationOverride = false;
            // Support farms that define FarmFishLocationOverride map property (e.g. Riverland/Forest farms)
            if (loc is Farm && (locData?.Fish is null || locData.Fish.Count == 0))
            {
                string[]? prop = loc.GetMapPropertySplitBySpaces("FarmFishLocationOverride");
                if (prop is { Length: > 0 } && !string.IsNullOrWhiteSpace(prop[0]) && locationsData.TryGetValue(prop[0], out var overrideData))
                {
                    locData = overrideData;
                    usingLocationOverride = true;
                }
            }

            if (locData?.Fish is null)
                return new List<string>();

            foreach (var spawn in locData.Fish)
            {
                if (spawn is null || spawn.RequireMagicBait || spawn.IsBossFish)
                    continue;
                if (areaId is not null && !string.IsNullOrEmpty(spawn.FishAreaId) && !string.Equals(spawn.FishAreaId, areaId, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!usingLocationOverride && body is not null)
                {
                    if (areaId is null
                        && !string.IsNullOrEmpty(spawn.FishAreaId)
                        && locData.FishAreas is not null
                        && locData.FishAreas.TryGetValue(spawn.FishAreaId, out var areaData)
                        && areaData?.Position is Rectangle areaRect
                        && !body.Intersects(areaRect))
                    {
                        continue;
                    }

                    if (spawn.BobberPosition is Rectangle bobberRect && !body.Intersects(bobberRect))
                        continue;
                }
                if (spawn.Season is { } s && s != Game1.season)
                    continue;
                if (!IsSpawnConditionSeasonMatch(spawn.Condition))
                    continue;

                void TryAdd(string? id)
                {
                    if (string.IsNullOrWhiteSpace(id))
                        return;
                    string qid = NormalizeItemId(id);
                    if (!this.IsValidSwimmingFish(qid))
                        return;
                    if (spawn.Season is null && !HasExplicitSeasonCondition(spawn.Condition) && !this.IsFishInDataFishSeason(qid))
                        return;
                    result.Add(qid);
                }

                TryAdd(spawn.ItemId);
                if (spawn.RandomItemId is not null)
                {
                    foreach (string? rid in spawn.RandomItemId)
                        TryAdd(rid);
                }
            }
        }
        catch (Exception ex)
        {
            this.Monitor.Log($"Failed to sample natural fish for {zoneKey}: {ex.Message}", LogLevel.Trace);
        }

        var candidates = result.ToList();
        var profile = this.GetWaterCapacityProfile(loc, zoneKey);
        if (candidates.Count <= profile.MaxNaturalSpecies)
            return candidates;

        return this.SelectNaturalSpeciesForBody(zoneKey, body, candidates, profile.MaxNaturalSpecies);
    }

    private List<string> SelectNaturalSpeciesForBody(string zoneKey, WaterBodyInfo? body, List<string> candidates, int limit)
    {
        var fishData = DataLoader.Fish(Game1.content);
        int tiles = body?.Tiles.Count ?? 60;
        bool open = body?.TouchesMapEdge ?? false;

        double diffWeight;
        double sizeWeight;
        double jitterRange;

        if (!open && tiles <= 40)
        {
            diffWeight = 0.85;
            sizeWeight = 0.50;
            jitterRange = 32.0;
        }
        else if ((!open && tiles <= 120) || (open && tiles <= 50))
        {
            diffWeight = 0.35;
            sizeWeight = 0.25;
            jitterRange = 55.0;
        }
        else
        {
            diffWeight = 0.10;
            sizeWeight = 0.05;
            jitterRange = 100.0;
        }

        return candidates
            .OrderBy(qid =>
            {
                int difficulty = 45;
                int maxSize = 24;
                string rawKey = qid.StartsWith("(O)", StringComparison.Ordinal) ? qid[3..] : qid;
                if (fishData.TryGetValue(rawKey, out string? row) || fishData.TryGetValue(qid, out row))
                {
                    string[] parts = row.Split('/');
                    if (parts.Length > 1 && int.TryParse(parts[1], out int parsedDiff))
                        difficulty = parsedDiff;
                    if (parts.Length > 4 && int.TryParse(parts[4], out int parsedMaxSize))
                        maxSize = parsedMaxSize;
                }

                double jitter = ComputeDeterministicUnit(zoneKey, qid) * jitterRange;
                return difficulty * diffWeight + maxSize * sizeWeight + jitter;
            })
            .Take(Math.Max(1, limit))
            .ToList();
    }

    private static double ComputeDeterministicUnit(string zoneKey, string qid)
    {
        unchecked
        {
            uint hash = 2166136261u;
            foreach (char ch in zoneKey)
            {
                hash ^= char.ToLowerInvariant(ch);
                hash *= 16777619u;
            }
            hash ^= '|';
            hash *= 16777619u;
            foreach (char ch in qid)
            {
                hash ^= char.ToLowerInvariant(ch);
                hash *= 16777619u;
            }
            return (hash % 10000u) / 10000.0;
        }
    }

    private bool IsNativeToLocation(string locName, string qid)
    {
        try
        {
            var locationsData = DataLoader.Locations(Game1.content);
            if (!locationsData.TryGetValue(locName, out var locData) || locData?.Fish is null)
                return false;

            foreach (var spawn in locData.Fish)
            {
                if (spawn is null)
                    continue;
                if (!string.IsNullOrEmpty(spawn.ItemId) && string.Equals(NormalizeItemId(spawn.ItemId), qid, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (spawn.RandomItemId is not null && spawn.RandomItemId.Any(r => string.Equals(NormalizeItemId(r), qid, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
        }
        catch { }

        return false;
    }

    public bool IsFishInSeasonForZone(string locName, string qid)
    {
        try
        {
            var locationsData = DataLoader.Locations(Game1.content);
            if (locationsData.TryGetValue(locName, out var locData) && locData?.Fish is not null)
            {
                bool foundInLoc = false;
                foreach (var spawn in locData.Fish)
                {
                    if (spawn is null || spawn.RequireMagicBait)
                        continue;

                    bool matchesId = (!string.IsNullOrEmpty(spawn.ItemId) && string.Equals(NormalizeItemId(spawn.ItemId), qid, StringComparison.OrdinalIgnoreCase))
                        || (spawn.RandomItemId is not null && spawn.RandomItemId.Any(r => string.Equals(NormalizeItemId(r), qid, StringComparison.OrdinalIgnoreCase)));
                    if (!matchesId)
                        continue;

                    foundInLoc = true;
                    if (spawn.Season is { } s)
                    {
                        if (s == Game1.season)
                            return true;
                    }
                    else if (HasExplicitSeasonCondition(spawn.Condition))
                    {
                        if (IsSpawnConditionSeasonMatch(spawn.Condition))
                            return true;
                    }
                    else if (this.IsFishInDataFishSeason(qid))
                    {
                        return true;
                    }
                }

                if (foundInLoc)
                    return false;
            }
        }
        catch { }

        return this.IsFishInDataFishSeason(qid);
    }

    private (string ZoneKey, ZonePopulation Zone)? ResolveMigrationTargetZone(GameLocation targetLoc)
    {
        var waterMap = GetOrBuildWaterMap(targetLoc);
        var openBodies = waterMap.Bodies.Where(b => b.TouchesMapEdge).ToList();
        if (openBodies.Count == 0)
            return null;

        var openKeys = new HashSet<string>(openBodies.Select(b => b.ZoneKey), StringComparer.OrdinalIgnoreCase);

        string? existingOpen = this.State.Zones
            .Where(kv => openKeys.Contains(kv.Key))
            .OrderByDescending(kv => kv.Value.PlayerStockedSpecies.Count > 0)
            .Select(kv => kv.Key)
            .FirstOrDefault();

        string key = existingOpen ?? openBodies.OrderByDescending(b => b.Tiles.Count).First().ZoneKey;
        var resolved = this.EnsureZoneInitialized(targetLoc, key);
        return resolved is not null ? (key, resolved) : null;
    }

    private static bool HasExplicitSeasonCondition(string? condition) =>
        !string.IsNullOrWhiteSpace(condition)
        && (condition.Contains("LOCATION_SEASON", StringComparison.OrdinalIgnoreCase)
            || condition.Contains("SEASON ", StringComparison.OrdinalIgnoreCase)
            || condition.Contains("SEASON_DAY", StringComparison.OrdinalIgnoreCase));

    private static bool IsSpawnConditionSeasonMatch(string? condition)
    {
        if (!HasExplicitSeasonCondition(condition))
            return true;

        string[] clauses = condition!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string clause in clauses)
        {
            if (!clause.Contains("SEASON", StringComparison.OrdinalIgnoreCase))
                continue;

            bool negated = clause.TrimStart().StartsWith("!", StringComparison.Ordinal);
            bool mentionsCurrent = clause.Contains(Game1.currentSeason, StringComparison.OrdinalIgnoreCase);

            if (negated && mentionsCurrent)
                return false;
            if (!negated && !mentionsCurrent)
                return false;
        }

        return true;
    }

    private bool IsFishInDataFishSeason(string qid)
    {
        try
        {
            var fishData = DataLoader.Fish(Game1.content);
            string rawKey = qid.StartsWith("(O)", StringComparison.Ordinal) ? qid[3..] : qid;
            if (!fishData.TryGetValue(rawKey, out string? row) && !fishData.TryGetValue(qid, out row))
                return true;

            string[] parts = row.Split('/');
            if (parts.Length < 7)
                return true;

            return parts[6].Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Any(s => s.Equals(Game1.currentSeason, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return true;
        }
    }

    private void EnsureCaches()
    {
        if (!this.CachesDirty)
            return;

        this.ProtectedFishIds.Clear();
        this.BossFishIds.Clear();

        foreach (string baseLegend in new[] { "(O)159", "(O)160", "(O)163", "(O)682", "(O)775", "(O)898", "(O)899", "(O)900", "(O)901", "(O)902" })
            this.BossFishIds.Add(baseLegend);

        try
        {
            var locationsData = DataLoader.Locations(Game1.content);
            foreach (var locData in locationsData.Values)
            {
                if (locData?.Fish is null)
                    continue;
                foreach (var spawn in locData.Fish)
                {
                    if (spawn is null || (!spawn.IsBossFish && spawn.CatchLimit <= 0))
                        continue;
                    if (!string.IsNullOrEmpty(spawn.ItemId))
                        this.BossFishIds.Add(NormalizeItemId(spawn.ItemId));
                    if (spawn.RandomItemId is not null)
                    {
                        foreach (string? rid in spawn.RandomItemId)
                        {
                            if (!string.IsNullOrEmpty(rid))
                                this.BossFishIds.Add(NormalizeItemId(rid));
                        }
                    }
                }
            }
        }
        catch { }

        try
        {
            if (Game1.getLocationFromName("CommunityCenter") is CommunityCenter cc)
            {
                foreach (var entry in Game1.netWorldState.Value.BundleData)
                {
                    string[] keyParts = entry.Key.Split('/');
                    if (keyParts.Length < 2 || !int.TryParse(keyParts[1], out int bundleId))
                        continue;
                    if (cc.isBundleComplete(bundleId))
                        continue;

                    string[] valParts = entry.Value.Split('/');
                    if (valParts.Length < 3)
                        continue;

                    string[] tokens = valParts[2].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    cc.bundles.TryGetValue(bundleId, out bool[]? slots);

                    for (int i = 0; i + 2 < tokens.Length; i += 3)
                    {
                        int slotIndex = i / 3;
                        if (slots is not null && slotIndex < slots.Length && slots[slotIndex])
                            continue;

                        string qid = NormalizeItemId(tokens[i]);
                        if (this.IsValidSwimmingFish(qid))
                            this.ProtectedFishIds.Add(qid);
                    }
                }
            }
        }
        catch { }

        try
        {
            if (Game1.player is { } p)
            {
                foreach (Quest? q in p.questLog)
                {
                    if (q is null || q.completed.Value)
                        continue;
                    if (q is FishingQuest fq && !string.IsNullOrEmpty(fq.ItemId.Value))
                        this.ProtectedFishIds.Add(NormalizeItemId(fq.ItemId.Value));
                    else if (q is ItemDeliveryQuest dq && !string.IsNullOrEmpty(dq.ItemId.Value))
                    {
                        string qid = NormalizeItemId(dq.ItemId.Value);
                        if (this.IsValidSwimmingFish(qid))
                            this.ProtectedFishIds.Add(qid);
                    }
                }
            }
        }
        catch { }

        this.CachesDirty = false;
    }

    private static string PickWeighted(List<(string Qid, int Count)> candidates)
    {
        int total = candidates.Sum(c => Math.Max(1, c.Count));
        int roll = Game1.random.Next(total);
        int acc = 0;
        foreach (var c in candidates)
        {
            acc += Math.Max(1, c.Count);
            if (roll < acc)
                return c.Qid;
        }
        return candidates[0].Qid;
    }

    public static void SpawnWaterRipple(GameLocation loc, Vector2 tile)
    {
        Vector2 pos = tile * 64f;
        loc.temporarySprites.Add(new TemporaryAnimatedSprite(
            "TileSheets\\animations",
            new Rectangle(0, 0, 64, 64),
            55f,
            8,
            0,
            pos,
            false,
            false,
            pos.Y / 10000f,
            0.01f,
            Color.White,
            0.9f,
            0f,
            0f,
            0f));
    }
}
