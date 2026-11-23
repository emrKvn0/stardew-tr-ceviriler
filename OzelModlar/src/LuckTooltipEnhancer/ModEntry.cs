using System.Globalization;
using System.Reflection;
using HarmonyLib;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Utilities;
using StardewValley;

namespace LuckTooltipEnhancer;

public sealed class ModEntry : Mod
{
    private static PropertyInfo? _showExactValueProp;
    private static FieldInfo? _hoverTextField;
    private static MethodInfo? _calculateLuckMethod;
    private static object? _lastShowLuckInstance;
    private static int _lastLuckLevel = int.MinValue;
    private static IMonitor? _monitor;

    public override void Entry(IModHelper helper)
    {
        _monitor = Monitor;
        helper.Events.GameLoop.GameLaunched += OnGameLaunched;
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (!e.IsMultipleOf(15) || !Context.IsWorldReady || Game1.player == null)
            return;

        int currentLuck = Game1.player.LuckLevel;
        if (currentLuck != _lastLuckLevel && _lastShowLuckInstance != null && _calculateLuckMethod?.GetParameters().Length == 0)
        {
            _lastLuckLevel = currentLuck;
            try
            {
                _calculateLuckMethod.Invoke(_lastShowLuckInstance, null);
            }
            catch (Exception ex)
            {
                _monitor?.Log($"Error refreshing luck tooltip: {ex}", LogLevel.Trace);
            }
        }
    }

    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        var showLuckType = AccessTools.TypeByName("UIInfoSuite2Alt.UIElements.ShowLuckOfDay");
        if (showLuckType == null)
        {
            Monitor.Log("UIInfoSuite2Alt.UIElements.ShowLuckOfDay type not found; patch skipped.", LogLevel.Warn);
            return;
        }

        _showExactValueProp = AccessTools.Property(showLuckType, "ShowExactValue");
        _hoverTextField = AccessTools.Field(showLuckType, "_hoverText");
        _calculateLuckMethod = AccessTools.Method(showLuckType, "CalculateLuck");

        if (_showExactValueProp == null || _hoverTextField == null || _calculateLuckMethod == null)
        {
            Monitor.Log("Expected ShowLuckOfDay members not found; patch skipped.", LogLevel.Warn);
            return;
        }

        var harmony = new Harmony(ModManifest.UniqueID);
        harmony.Patch(
            original: _calculateLuckMethod,
            prefix: new HarmonyMethod(typeof(ModEntry), nameof(CalculateLuck_Prefix)),
            postfix: new HarmonyMethod(typeof(ModEntry), nameof(CalculateLuck_Postfix))
        );

        Monitor.Log("Patched UIInfoSuite2Alt luck tooltip (0-100% + exact + ring/buff luck + status + min/max).", LogLevel.Info);
    }

    private static void CalculateLuck_Prefix(object __instance, out bool __state)
    {
        __state = false;
        if (_showExactValueProp == null)
            return;

        bool showExact = (bool)(_showExactValueProp.GetValue(__instance) ?? false);
        if (showExact)
        {
            __state = true;
            _showExactValueProp.SetValue(__instance, false);
        }
    }

    private static void CalculateLuck_Postfix(object __instance, bool __state)
    {
        if (!__state || _showExactValueProp == null || _hoverTextField == null)
            return;

        // Restore in-memory property immediately; never touches config.json or save files.
        _showExactValueProp.SetValue(__instance, true);

        try
        {
            _lastShowLuckInstance = __instance;
            if (_hoverTextField.GetValue(__instance) is not PerScreen<string> hoverText)
                return;

            string statusText = hoverText.Value ?? string.Empty;
            if (string.IsNullOrWhiteSpace(statusText) || statusText.Contains("\nMin: %0", StringComparison.Ordinal) || Game1.player == null)
                return;

            double dailyLuck = Game1.player.DailyLuck;
            int luckLevel = Game1.player.LuckLevel;
            _lastLuckLevel = luckLevel;

            double minLuck = Game1.player.hasSpecialCharm ? -0.075 : -0.100;
            double maxLuck = Game1.player.hasSpecialCharm ? 0.125 : 0.100;
            double effectiveLuck = dailyLuck + (luckLevel * 0.020);
            double pct = Math.Max(0.0, (effectiveLuck - minLuck) / (maxLuck - minLuck) * 100.0);

            string exactStr = dailyLuck > 0
                ? $"+{dailyLuck.ToString("0.###", CultureInfo.InvariantCulture)}"
                : dailyLuck.ToString("0.###", CultureInfo.InvariantCulture);
            string pctStr = Math.Round(pct, 1).ToString("0.#", CultureInfo.InvariantCulture);
            string minStr = minLuck.ToString("0.###", CultureInfo.InvariantCulture);
            string maxStr = $"+{maxLuck.ToString("0.###", CultureInfo.InvariantCulture)}";
            string luckBonusStr = luckLevel != 0
                ? $", Yüzük/Buff: {(luckLevel > 0 ? "+" : "")}{luckLevel}"
                : string.Empty;

            hoverText.Value = $"Şans: %{pctStr} ({exactStr}{luckBonusStr}) - {statusText}\nMin: %0 ({minStr}) | Max (Günlük): %100 ({maxStr})";
        }
        catch (Exception ex)
        {
            _monitor?.Log($"Error formatting luck tooltip: {ex}", LogLevel.Error);
        }
    }
}
