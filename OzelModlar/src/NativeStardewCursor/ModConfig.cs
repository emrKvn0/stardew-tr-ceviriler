namespace NativeStardewCursor;

public sealed class ModConfig
{
    /// <summary>Enable native OS hardware cursor using Stardew's custom cursor sprites.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Pixel-art scale multiplier for the 16x16 cursor sprite (3 = 48px, 4 = 64px).</summary>
    public int CursorScale { get; set; } = 4;

    /// <summary>Automatically enable Stardew's hardwareCursor option so the 60 FPS software cursor is suppressed.</summary>
    public bool ForceHardwareCursorOption { get; set; } = true;

    /// <summary>Hide the cursor during cutscenes or when idle fade sets transparency to zero.</summary>
    public bool RespectHiddenCursor { get; set; } = true;
}
