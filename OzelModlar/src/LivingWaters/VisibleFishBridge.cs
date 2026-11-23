using System.Collections;
using System.Reflection;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Objects;

namespace LivingWaters;

internal sealed class VisibleFishBridge
{
    private readonly IMonitor Monitor;
    private readonly EcosystemManager Ecosystem;
    private readonly Func<ModConfig> GetConfig;

    private static VisibleFishBridge? Instance;

    private Type? ModEntryType;
    private FieldInfo? ModInstanceField;
    private FieldInfo? FishManagersField;
    private FieldInfo? WaterTilesField;
    private FieldInfo? WaterTileArrayField;
    private FieldInfo? FishTankField;
    private FieldInfo? TankFishField;
    private FieldInfo? FishTankWaterTilesField;
    private FieldInfo? FishTankRowsField;
    private FieldInfo? FishTankColsField;
    private FieldInfo? SpawnFishInCurrentLocationField;
    private FieldInfo? CustomFishItemIdField;
    private MethodInfo? AddFishMethod;
    private MethodInfo? SpawnFishInLocationMethod;
    private MethodInfo? OnUpdateTickedMethod;
    private bool IsInitialized;
    private int MaintainTickCounter;

    public VisibleFishBridge(IMonitor monitor, EcosystemManager ecosystem, Func<ModConfig> getConfig)
    {
        this.Monitor = monitor;
        this.Ecosystem = ecosystem;
        this.GetConfig = getConfig;
        Instance = this;
    }

    public void TryAttach(Harmony harmony, IModRegistry modRegistry)
    {
        if (!modRegistry.IsLoaded("shekurika.WaterFish"))
            return;

        try
        {
            this.ModEntryType = AccessTools.TypeByName("showFishInWater.ModEntry");
            Type? fishManagerType = AccessTools.TypeByName("showFishInWater.FishManager");
            Type? fishTankType = AccessTools.TypeByName("showFishInWater.FishTank");
            Type? customFishType = AccessTools.TypeByName("showFishInWater.CustomFish");

            if (this.ModEntryType is null || fishManagerType is null || fishTankType is null)
                return;

            this.ModInstanceField = AccessTools.Field(this.ModEntryType, "ModInstance");
            this.FishManagersField = AccessTools.Field(this.ModEntryType, "FishManagers");
            this.WaterTilesField = AccessTools.Field(fishManagerType, "waterTiles");
            this.WaterTileArrayField = AccessTools.Field(fishManagerType, "waterTileArray");
            this.FishTankField = AccessTools.Field(fishManagerType, "fishTank");
            this.SpawnFishInCurrentLocationField = AccessTools.Field(fishManagerType, "SpawnFishInCurrentLocation");
            this.TankFishField = AccessTools.Field(fishTankType, "tankFish");
            this.FishTankWaterTilesField = AccessTools.Field(fishTankType, "waterTiles");
            this.FishTankRowsField = AccessTools.Field(fishTankType, "rows");
            this.FishTankColsField = AccessTools.Field(fishTankType, "cols");
            this.CustomFishItemIdField = customFishType is not null ? AccessTools.Field(customFishType, "fishItemId") : null;
            this.AddFishMethod = AccessTools.Method(fishTankType, "addFish", new[] { typeof(string), typeof(int), typeof(int), typeof(bool) });
            this.SpawnFishInLocationMethod = AccessTools.Method(fishManagerType, "SpawnFishInLocation", new[] { typeof(GameLocation) });
            this.OnUpdateTickedMethod = AccessTools.Method(fishManagerType, "OnUpdateTicked");

            if (this.WaterTilesField is null
                || this.FishTankField is null
                || this.TankFishField is null
                || this.AddFishMethod is null
                || this.SpawnFishInLocationMethod is null)
            {
                return;
            }

            harmony.Patch(
                original: this.SpawnFishInLocationMethod,
                postfix: new HarmonyMethod(typeof(VisibleFishBridge), nameof(Postfix_SpawnFishInLocation)));

            if (this.OnUpdateTickedMethod is not null)
            {
                harmony.Patch(
                    original: this.OnUpdateTickedMethod,
                    postfix: new HarmonyMethod(typeof(VisibleFishBridge), nameof(Postfix_OnUpdateTicked)));
            }

            this.IsInitialized = true;
            this.Monitor.Log("VisibleFish entegrasyonu aktif: sudaki balık gölgeleri Canlı Sular nüfusuyla senkronlandı.", LogLevel.Info);
        }
        catch (Exception ex)
        {
            this.Monitor.Log($"VisibleFish entegrasyonu kurulamadı: {ex.Message}", LogLevel.Warn);
        }
    }

    private static void Postfix_SpawnFishInLocation(object __instance, GameLocation location)
    {
        try
        {
            Instance?.SyncFishTank(__instance, location);
        }
        catch
        {
            // Never crash rendering if VisibleFish internal state is mid-transition
        }
    }

    private static void Postfix_OnUpdateTicked(object __instance)
    {
        try
        {
            Instance?.MaintainSyncedFish(__instance);
        }
        catch
        {
            // Never crash per-tick update
        }
    }

    private bool ShouldManageLocation(GameLocation? location)
    {
        if (location is null)
            return false;
        var cfg = this.GetConfig();
        if (!cfg.EnableMod || !cfg.SyncVisibleFish)
            return false;
        if (cfg.OnlyFarmWaters && location is not Farm)
            return false;
        return true;
    }

    private void MaintainSyncedFish(object fishManager)
    {
        if (!Context.IsWorldReady)
            return;

        GameLocation? location = Game1.currentLocation;
        if (!this.ShouldManageLocation(location))
            return;

        // Prevent VisibleFish's per-tick random ambient spawner from filling managed waters with random fish
        if (this.SpawnFishInCurrentLocationField is not null
            && this.SpawnFishInCurrentLocationField.GetValue(fishManager) is true)
        {
            this.SpawnFishInCurrentLocationField.SetValue(fishManager, false);
        }

        // Periodically (~every 45 ticks = 0.75s) replenish any synced shadows whose swim lifetime expired
        if (++this.MaintainTickCounter % 45 != 0)
            return;

        if (this.WaterTilesField?.GetValue(fishManager) is not IEnumerable waterTilesObj)
            return;

        var tilesByZone = GroupTilesByZone(location!, waterTilesObj);
        if (tilesByZone.Count == 0)
            return;

        object? fishTank = this.FishTankField?.GetValue(fishManager);
        if (fishTank is null || this.AddFishMethod is null || this.TankFishField?.GetValue(fishTank) is not IList tankFish)
            return;

        var desiredByZone = this.ComputeDesiredCounts(location!, tilesByZone);
        var currentCountsByZone = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);

        for (int i = tankFish.Count - 1; i >= 0; i--)
        {
            string? fishId = this.ExtractFishId(tankFish[i]);
            if (string.IsNullOrEmpty(fishId))
                continue;

            Vector2? fishTile = ExtractFishTile(tankFish[i]);
            if (!fishTile.HasValue)
            {
                tankFish.RemoveAt(i);
                continue;
            }

            string fishZoneKey = EcosystemManager.GetZoneKey(location!, fishTile.Value);
            string qid = EcosystemManager.NormalizeItemId(fishId);
            if (!desiredByZone.TryGetValue(fishZoneKey, out var zoneDesired)
                || !zoneDesired.TryGetValue(qid, out int maxDesiredForSpeciesInZone))
            {
                tankFish.RemoveAt(i);
                continue;
            }

            if (!currentCountsByZone.TryGetValue(fishZoneKey, out var zoneCurrent))
            {
                zoneCurrent = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                currentCountsByZone[fishZoneKey] = zoneCurrent;
            }

            int curInZone = zoneCurrent.GetValueOrDefault(qid, 0);
            if (curInZone >= maxDesiredForSpeciesInZone)
            {
                tankFish.RemoveAt(i);
                continue;
            }

            zoneCurrent[qid] = curInZone + 1;
        }

        if (desiredByZone.Count == 0)
            return;

        // Top up any fish whose VisibleFish lifetime expired naturally so each water body's visuals stay exact
        foreach (var kv in desiredByZone)
        {
            string zoneKey = kv.Key;
            if (!tilesByZone.TryGetValue(zoneKey, out var validTiles) || validTiles.Count == 0)
                continue;

            currentCountsByZone.TryGetValue(zoneKey, out var zoneCurrent);
            foreach (var speciesKv in kv.Value)
            {
                int cur = zoneCurrent?.GetValueOrDefault(speciesKv.Key, 0) ?? 0;
                int missing = speciesKv.Value - cur;
                if (missing <= 0)
                    continue;

                for (int i = 0; i < missing; i++)
                {
                    var (x, y) = validTiles[Game1.random.Next(validTiles.Count)];
                    this.AddFishMethod.Invoke(fishTank, new object[] { StripObjectPrefix(speciesKv.Key), x, y, false });
                }
            }
        }
    }

    public void OnFishReleasedLive(GameLocation loc, Vector2 tile, string qid)
    {
        if (!this.IsInitialized || !this.ShouldManageLocation(loc))
            return;

        try
        {
            object? fishManager = this.GetCurrentFishManager();
            if (fishManager is null)
                return;

            this.SpawnFishInCurrentLocationField?.SetValue(fishManager, false);

            object? fishTank = this.FishTankField?.GetValue(fishManager);
            if (fishTank is null || this.AddFishMethod is null || this.TankFishField?.GetValue(fishTank) is not IList tankFish)
                return;

            var cfg = this.GetConfig();
            if (tankFish.Count < cfg.MaxVisibleFishPerZone)
            {
                string targetZoneKey = EcosystemManager.GetZoneKey(loc, tile);
                var zone = this.Ecosystem.GetOrInitZone(loc, tile);
                int desiredCount = zone?.SpeciesCounts.GetValueOrDefault(qid, 0) ?? 1;
                int existingCount = 0;
                for (int i = 0; i < tankFish.Count; i++)
                {
                    string? fid = this.ExtractFishId(tankFish[i]);
                    if (string.IsNullOrEmpty(fid) || !string.Equals(EcosystemManager.NormalizeItemId(fid), qid, StringComparison.OrdinalIgnoreCase))
                        continue;

                    Vector2? fishTile = ExtractFishTile(tankFish[i]);
                    if (fishTile.HasValue && string.Equals(EcosystemManager.GetZoneKey(loc, fishTile.Value), targetZoneKey, StringComparison.OrdinalIgnoreCase))
                        existingCount++;
                }

                if (existingCount < desiredCount)
                    this.AddFishMethod.Invoke(fishTank, new object[] { StripObjectPrefix(qid), (int)tile.X, (int)tile.Y, false });
            }
            else
            {
                this.SyncFishTank(fishManager, loc);
            }
        }
        catch (Exception ex)
        {
            this.Monitor.Log($"VisibleFish OnFishReleasedLive atlandı: {ex.Message}", LogLevel.Trace);
        }
    }

    public void OnFishCaughtLive(GameLocation loc, Vector2 bobberTile, string qid)
    {
        if (!this.IsInitialized || !this.ShouldManageLocation(loc))
            return;

        try
        {
            object? fishManager = this.GetCurrentFishManager();
            if (fishManager is null)
                return;

            object? fishTank = this.FishTankField?.GetValue(fishManager);
            if (fishTank is null || this.TankFishField?.GetValue(fishTank) is not IList tankFish)
                return;

            string targetZoneKey = EcosystemManager.GetZoneKey(loc, bobberTile);
            var zone = this.Ecosystem.GetOrInitZone(loc, bobberTile);
            int totalInZone = zone?.SpeciesCounts.Values.Sum() ?? 0;
            if (totalInZone <= this.GetConfig().MaxVisibleFishPerZone)
            {
                int bestIdx = -1;
                float bestDist = float.MaxValue;
                for (int i = tankFish.Count - 1; i >= 0; i--)
                {
                    string? entryFishId = this.ExtractFishId(tankFish[i]);
                    if (string.IsNullOrEmpty(entryFishId)
                        || !string.Equals(EcosystemManager.NormalizeItemId(entryFishId), qid, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    Vector2? fishTile = ExtractFishTile(tankFish[i]);
                    if (!fishTile.HasValue || !string.Equals(EcosystemManager.GetZoneKey(loc, fishTile.Value), targetZoneKey, StringComparison.OrdinalIgnoreCase))
                        continue;

                    float dist = Vector2.DistanceSquared(fishTile.Value, bobberTile);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestIdx = i;
                    }
                }

                if (bestIdx >= 0)
                {
                    tankFish.RemoveAt(bestIdx);
                    return;
                }
            }

            this.SyncFishTank(fishManager, loc);
        }
        catch (Exception ex)
        {
            this.Monitor.Log($"VisibleFish OnFishCaughtLive atlandı: {ex.Message}", LogLevel.Trace);
        }
    }

    public void SyncCurrentLocation(GameLocation? loc)
    {
        if (!this.IsInitialized || !this.ShouldManageLocation(loc))
            return;

        try
        {
            object? fishManager = this.GetCurrentFishManager();
            if (fishManager is not null)
                this.SyncFishTank(fishManager, loc!);
        }
        catch (Exception ex)
        {
            this.Monitor.Log($"VisibleFish SyncCurrentLocation atlandı: {ex.Message}", LogLevel.Trace);
        }
    }

    private static string StripObjectPrefix(string qid) =>
        qid.StartsWith("(O)", StringComparison.OrdinalIgnoreCase) ? qid[3..] : qid;

    private string? ExtractFishId(object? customFish)
    {
        if (customFish is null)
            return null;
        return this.CustomFishItemIdField?.GetValue(customFish) as string
            ?? (customFish as TankFish)?.fishItemId;
    }

    private static Vector2? ExtractFishTile(object? customFish)
    {
        if (customFish is TankFish tf)
        {
            int tx = (int)MathF.Floor(tf.position.X / 64f);
            int ty = (int)MathF.Floor(-tf.position.Y / 64f);
            return new Vector2(tx, ty);
        }
        return null;
    }

    private object? GetCurrentFishManager()
    {
        if (this.ModInstanceField is null || this.FishManagersField is null)
            return null;

        object? modInstance = this.ModInstanceField.GetValue(null);
        if (modInstance is null)
            return null;

        object? perScreen = this.FishManagersField.GetValue(modInstance);
        if (perScreen is null)
            return null;

        PropertyInfo? valueProp = perScreen.GetType().GetProperty("Value");
        return valueProp?.GetValue(perScreen);
    }

    private static Dictionary<string, List<(int X, int Y)>> GroupTilesByZone(GameLocation location, IEnumerable waterTilesObj)
    {
        var tilesByZone = new Dictionary<string, List<(int X, int Y)>>(StringComparer.OrdinalIgnoreCase);
        foreach (object? item in waterTilesObj)
        {
            if (item is Tuple<int, int> tuple)
            {
                string zKey = EcosystemManager.GetZoneKey(location, new Vector2(tuple.Item1, tuple.Item2));
                if (!tilesByZone.TryGetValue(zKey, out var list))
                {
                    list = new List<(int, int)>();
                    tilesByZone[zKey] = list;
                }
                list.Add((tuple.Item1, tuple.Item2));
            }
        }
        return tilesByZone;
    }

    private Dictionary<string, Dictionary<string, int>> ComputeDesiredCounts(
        GameLocation location,
        Dictionary<string, List<(int X, int Y)>> tilesByZone)
    {
        var cfg = this.GetConfig();
        var desiredByZone = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        int maxPerMap = Math.Max(10, cfg.MaxVisibleFishPerZone);
        int perZoneCap = Math.Max(8, maxPerMap / Math.Max(1, tilesByZone.Count));

        foreach (var kv in tilesByZone)
        {
            var tiles = kv.Value;
            if (tiles.Count == 0)
                continue;

            var zone = this.Ecosystem.GetOrInitZone(location, new Vector2(tiles[0].X, tiles[0].Y));
            if (zone is null)
                continue;

            var activeSpecies = zone.SpeciesCounts
                .Where(s => s.Value > 0
                    && this.Ecosystem.IsValidSwimmingFish(s.Key)
                    && ((location is Farm && cfg.FarmAllowsAllSeasons)
                        || this.Ecosystem.IsFishInSeasonForZone(location.Name, s.Key)))
                .OrderByDescending(s => s.Value)
                .ToList();

            if (activeSpecies.Count == 0)
                continue;

            var zoneDesired = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var profile = this.Ecosystem.GetWaterCapacityProfile(location, kv.Key);
            int totalFish = activeSpecies.Sum(s => s.Value);
            int cap = Math.Min(tiles.Count, Math.Min(perZoneCap, profile.MaxVisibleFish));

            if (totalFish <= cap)
            {
                foreach (var s in activeSpecies)
                    zoneDesired[s.Key] = s.Value;
            }
            else
            {
                double scale = (double)cap / totalFish;
                int assigned = 0;
                foreach (var s in activeSpecies)
                {
                    if (assigned >= cap)
                        break;
                    int visualCount = Math.Max(1, (int)Math.Round(s.Value * scale));
                    visualCount = Math.Min(visualCount, cap - assigned);
                    if (visualCount > 0)
                    {
                        zoneDesired[s.Key] = visualCount;
                        assigned += visualCount;
                    }
                }
            }

            if (zoneDesired.Count > 0)
                desiredByZone[kv.Key] = zoneDesired;
        }

        return desiredByZone;
    }

    private void SyncFishTank(object fishManager, GameLocation location)
    {
        if (!this.ShouldManageLocation(location))
            return;

        // Disable VisibleFish's per-tick ambient respawner so only LivingWaters populations appear
        this.SpawnFishInCurrentLocationField?.SetValue(fishManager, false);

        if (this.WaterTilesField?.GetValue(fishManager) is not IEnumerable waterTilesObj)
            return;

        object? fishTank = this.FishTankField?.GetValue(fishManager);
        if (fishTank is null || this.AddFishMethod is null || this.TankFishField?.GetValue(fishTank) is not IList tankFish)
            return;

        var tilesByZone = GroupTilesByZone(location, waterTilesObj);
        if (tilesByZone.Count == 0 && location.waterTiles is null)
        {
            this.PopulateFallbackWaterTiles(fishManager, fishTank, location);
            tilesByZone = GroupTilesByZone(location, waterTilesObj);
        }

        if (tilesByZone.Count == 0)
            return;

        tankFish.Clear();

        var desiredByZone = this.ComputeDesiredCounts(location, tilesByZone);
        if (desiredByZone.Count == 0)
            return;

        foreach (var kv in desiredByZone)
        {
            if (!tilesByZone.TryGetValue(kv.Key, out var validTiles) || validTiles.Count == 0)
                continue;

            foreach (var speciesKv in kv.Value)
            {
                for (int i = 0; i < speciesKv.Value; i++)
                {
                    var (x, y) = validTiles[Game1.random.Next(validTiles.Count)];
                    this.AddFishMethod.Invoke(fishTank, new object[] { StripObjectPrefix(speciesKv.Key), x, y, false });
                }
            }
        }
    }

    private void PopulateFallbackWaterTiles(object fishManager, object fishTank, GameLocation location)
    {
        var layer = location.map?.GetLayer("Back");
        if (layer is null || this.WaterTilesField?.GetValue(fishManager) is not IList<Tuple<int, int>> waterList)
            return;

        int w = layer.LayerWidth;
        int h = layer.LayerHeight;
        bool[,] grid = new bool[w + 1, h + 1];
        waterList.Clear();

        for (int x = 0; x < w; x++)
        {
            for (int y = 0; y < h; y++)
            {
                if (!location.isWaterTile(x, y))
                    continue;

                grid[x, y] = true;
                if (location.isTileFishable(x, y) || (x > 0 && y > 0 && x < w - 1 && y < h - 1))
                    waterList.Add(Tuple.Create(x, y));
            }
        }

        if (waterList.Count == 0)
            return;

        this.WaterTileArrayField?.SetValue(fishManager, grid);
        this.FishTankWaterTilesField?.SetValue(fishTank, grid);
        this.FishTankRowsField?.SetValue(fishTank, h + 1);
        this.FishTankColsField?.SetValue(fishTank, w + 1);
    }
}
