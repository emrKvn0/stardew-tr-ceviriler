using System.Reflection;
using HarmonyLib;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace HappyBirthdayLagFix;

/// <summary>
/// Caches Happy Birthday's Content Patcher tokens once per in-game day so they stop
/// re-rolling random items and triggering full-asset predicate cache invalidations every 10 minutes.
/// </summary>
public sealed class ModEntry : Mod
{
    private static readonly Dictionary<string, string> DailyNpcGiftCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<(bool UseCat, int Cat), string> DailyGenericGiftCache = new();
    private static int CachedTotalDays = -1;
    private static string CachedSaveFolder = string.Empty;

    private static FieldInfo? NpcNameField;
    private static FieldInfo? NpcPickedGiftField;
    private static FieldInfo? GenericPickedGiftField;
    private static FieldInfo? GenericUseCatField;
    private static FieldInfo? GenericCatField;

    private static IMonitor? LogMonitor;

    public override void Entry(IModHelper helper)
    {
        LogMonitor = this.Monitor;
        helper.Events.GameLoop.GameLaunched += this.OnGameLaunched;
        helper.Events.GameLoop.SaveLoaded += (_, _) => ClearDailyCaches();
        helper.Events.GameLoop.DayStarted += (_, _) => ClearDailyCaches();
    }

    private static void ClearDailyCaches()
    {
        DailyNpcGiftCache.Clear();
        DailyGenericGiftCache.Clear();
        CachedTotalDays = Game1.Date?.TotalDays ?? -1;
        CachedSaveFolder = Constants.SaveFolderName ?? string.Empty;
    }

    private static void EnsureCacheCurrent()
    {
        int totalDays = Game1.Date?.TotalDays ?? -1;
        string saveFolder = Constants.SaveFolderName ?? string.Empty;
        if (totalDays != CachedTotalDays || !string.Equals(saveFolder, CachedSaveFolder, StringComparison.Ordinal))
        {
            DailyNpcGiftCache.Clear();
            DailyGenericGiftCache.Clear();
            CachedTotalDays = totalDays;
            CachedSaveFolder = saveFolder;
        }
    }

    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        if (!this.Helper.ModRegistry.IsLoaded("Omegasis.HappyBirthday"))
            return;

        try
        {
            Type? npcTokenType = AccessTools.TypeByName("Omegasis.HappyBirthday.Framework.Compatibility.ContentPatcher.Tokens.NPCGiftToken");
            Type? genericTokenType = AccessTools.TypeByName("Omegasis.HappyBirthday.Framework.Compatibility.ContentPatcher.Tokens.GenericNPCGiftToken");

            if (npcTokenType is null || genericTokenType is null)
            {
                this.Monitor.Log("HappyBirthday token types were not found; skipping token lag patch.", LogLevel.Trace);
                return;
            }

            NpcNameField = AccessTools.Field(npcTokenType, "npcName");
            NpcPickedGiftField = AccessTools.Field(npcTokenType, "pickedGift");
            GenericPickedGiftField = AccessTools.Field(genericTokenType, "pickedGift");
            GenericUseCatField = AccessTools.Field(genericTokenType, "useCategoryMatching");
            GenericCatField = AccessTools.Field(genericTokenType, "matchingItemCategory");

            var harmony = new Harmony(this.ModManifest.UniqueID);

            MethodInfo? npcUpdate = AccessTools.Method(npcTokenType, "UpdateContext");
            if (npcUpdate is not null && NpcNameField is not null && NpcPickedGiftField is not null)
            {
                harmony.Patch(
                    npcUpdate,
                    prefix: new HarmonyMethod(typeof(ModEntry), nameof(Prefix_NpcGiftUpdateContext)),
                    postfix: new HarmonyMethod(typeof(ModEntry), nameof(Postfix_NpcGiftUpdateContext)));
            }

            MethodInfo? genUpdate = AccessTools.Method(genericTokenType, "UpdateContext");
            if (genUpdate is not null && GenericPickedGiftField is not null && GenericUseCatField is not null && GenericCatField is not null)
            {
                harmony.Patch(
                    genUpdate,
                    prefix: new HarmonyMethod(typeof(ModEntry), nameof(Prefix_GenericGiftUpdateContext)),
                    postfix: new HarmonyMethod(typeof(ModEntry), nameof(Postfix_GenericGiftUpdateContext)));
            }

            this.Monitor.Log("Applied per-day cache fix to HappyBirthday Content Patcher tokens (prevents 10-minute cache invalidation stutters).", LogLevel.Info);
        }
        catch (Exception ex)
        {
            LogMonitor?.Log($"Failed to apply HappyBirthdayLagFix: {ex}", LogLevel.Warn);
        }
    }

    private static bool Prefix_NpcGiftUpdateContext(object __instance, ref bool __result, out string? __state)
    {
        __state = null;
        if (!Context.IsWorldReady)
        {
            __result = false;
            return false;
        }

        EnsureCacheCurrent();
        string npcKey = (NpcNameField?.GetValue(__instance) as string) ?? string.Empty;
        if (DailyNpcGiftCache.TryGetValue(npcKey, out string? cachedGift) && !string.IsNullOrEmpty(cachedGift))
        {
            NpcPickedGiftField?.SetValue(__instance, cachedGift);
            __result = false;
            return false;
        }

        __state = npcKey;
        return true;
    }

    private static void Postfix_NpcGiftUpdateContext(object __instance, string? __state)
    {
        if (__state is null)
            return;

        if (NpcPickedGiftField?.GetValue(__instance) is string picked && !string.IsNullOrEmpty(picked))
            DailyNpcGiftCache[__state] = picked;
    }

    private static bool Prefix_GenericGiftUpdateContext(object __instance, ref bool __result, out (bool UseCat, int Cat)? __state)
    {
        __state = null;
        if (!Context.IsWorldReady)
        {
            __result = false;
            return false;
        }

        EnsureCacheCurrent();
        bool useCat = GenericUseCatField?.GetValue(__instance) is bool b && b;
        int cat = GenericCatField?.GetValue(__instance) is int c ? c : 0;
        var key = (useCat, cat);

        if (DailyGenericGiftCache.TryGetValue(key, out string? cachedGift) && !string.IsNullOrEmpty(cachedGift))
        {
            GenericPickedGiftField?.SetValue(__instance, cachedGift);
            __result = false;
            return false;
        }

        __state = key;
        return true;
    }

    private static void Postfix_GenericGiftUpdateContext(object __instance, (bool UseCat, int Cat)? __state)
    {
        if (__state is null)
            return;

        if (GenericPickedGiftField?.GetValue(__instance) is string picked && !string.IsNullOrEmpty(picked))
            DailyGenericGiftCache[__state.Value] = picked;
    }
}
