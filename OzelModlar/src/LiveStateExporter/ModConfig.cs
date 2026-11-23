using StardewModdingAPI;

namespace LiveStateExporter;

public sealed class ModConfig
{
    /// <summary>Keybind to force an immediate state export with a HUD notification.</summary>
    public SButton ExportKey { get; set; } = SButton.F5;

    /// <summary>Automatically export on in-game time changes.</summary>
    public bool ExportOnTimeChanged { get; set; } = true;

    /// <summary>In-game minutes between automatic TimeChanged exports (minimum 10, default 30).</summary>
    public int AutoExportIntervalMinutes { get; set; } = 30;

    /// <summary>Show a HUD message when triggered manually via ExportKey.</summary>
    public bool ShowHudMessageOnHotkey { get; set; } = true;
}
