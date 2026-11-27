using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;

namespace NativeStardewCursor;

public interface IGenericModConfigMenuApi
{
    void Register(IManifest mod, Action reset, Action save, bool titleScreenOnly = false);
    void AddBoolOption(IManifest mod, Func<bool> getValue, Action<bool> setValue, Func<string> name, Func<string>? tooltip = null, string? fieldId = null);
    void AddNumberOption(IManifest mod, Func<int> getValue, Action<int> setValue, Func<string> name, Func<string>? tooltip = null, int? min = null, int? max = null, int? interval = null, Func<int, string>? formatValue = null, string? fieldId = null);
}

public sealed class ModEntry : Mod
{
    private const int TileSize = 16;
    private const int HiddenCursorKey = -999;

    private static ModEntry? _instance;

    private readonly Dictionary<(int CursorId, int AlphaTier), (MouseCursor Cursor, Texture2D Texture)> _cursorCache = new();
    private ModConfig Config = new();
    private Texture2D? _lastSourceTexture;
    private int _lastAppliedScale = -1;
    private (int CursorId, int AlphaTier) _lastAppliedKey = (int.MinValue, -1);
    private bool _wasActiveLastFrame;
    private bool _capturedThisFrame;

    public override void Entry(IModHelper helper)
    {
        _instance = this;
        this.Config = helper.ReadConfig<ModConfig>();

        helper.Events.GameLoop.GameLaunched += this.OnGameLaunched;
        helper.Events.GameLoop.SaveLoaded += this.OnSaveLoaded;
        helper.Events.Display.Rendering += this.OnRendering;
        helper.Events.Display.Rendered += this.OnRendered;
        helper.Events.Content.AssetReady += this.OnAssetReady;

        this.ApplyHarmonyPatches();
    }

    private void ApplyHarmonyPatches()
    {
        try
        {
            var harmony = new Harmony(this.ModManifest.UniqueID);

            MethodInfo? drawMouseCursor = typeof(Game1).GetMethod(
                nameof(Game1.drawMouseCursor),
                BindingFlags.Public | BindingFlags.Instance,
                null,
                Type.EmptyTypes,
                null);
            MethodInfo? prefix = typeof(ModEntry).GetMethod(nameof(PrefixDrawMouseCursor), BindingFlags.NonPublic | BindingFlags.Static);
            MethodInfo? transpiler = typeof(ModEntry).GetMethod(nameof(TranspileDrawMouseCursor), BindingFlags.NonPublic | BindingFlags.Static);
            if (drawMouseCursor is not null && transpiler is not null)
            {
                harmony.Patch(
                    drawMouseCursor,
                    prefix: prefix is not null ? new HarmonyMethod(prefix) { priority = Priority.Last } : null,
                    transpiler: new HarmonyMethod(transpiler));
            }
            else
            {
                this.Monitor.Log("Failed to locate Game1.drawMouseCursor for Harmony patching; world hover cursors will not update.", LogLevel.Error);
            }

            MethodInfo? menuDrawMouse = typeof(IClickableMenu).GetMethod(
                nameof(IClickableMenu.drawMouse),
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(SpriteBatch), typeof(bool), typeof(int) },
                null);
            MethodInfo? menuPostfix = typeof(ModEntry).GetMethod(nameof(PostfixMenuDrawMouse), BindingFlags.NonPublic | BindingFlags.Static);
            if (menuDrawMouse is not null && menuPostfix is not null)
            {
                harmony.Patch(menuDrawMouse, postfix: new HarmonyMethod(menuPostfix));
            }
            else
            {
                this.Monitor.Log("Failed to locate IClickableMenu.drawMouse for Harmony patching.", LogLevel.Error);
            }
        }
        catch (Exception ex)
        {
            this.Monitor.Log($"Failed to apply Harmony cursor patches: {ex}", LogLevel.Error);
        }
    }

    private static void PrefixDrawMouseCursor()
    {
        if (_instance is null)
            return;

        // When Game1.mouseCursor <= cursor_none, drawMouseCursor returns early and resets it to cursor_default
        if (Game1.activeClickableMenu is null && Game1.currentLocation is not null && Game1.mouseCursor <= Game1.cursor_none)
            _instance.CaptureCursorState(Game1.cursor_none, 0f);
    }

    private static IEnumerable<CodeInstruction> TranspileDrawMouseCursor(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo? hwGetter = typeof(Options).GetProperty(nameof(Options.hardwareCursor))?.GetGetMethod();
        MethodInfo? captureCall = typeof(ModEntry).GetMethod(nameof(OnWorldCursorEvaluated), BindingFlags.NonPublic | BindingFlags.Static);
        bool patched = false;

        foreach (CodeInstruction instr in instructions)
        {
            yield return instr;
            if (!patched && hwGetter is not null && captureCall is not null && instr.Calls(hwGetter))
            {
                yield return new CodeInstruction(OpCodes.Call, captureCall);
                patched = true;
            }
        }

        if (!patched)
            _instance?.Monitor.Log("TranspileDrawMouseCursor did not find Options.hardwareCursor getter; world hover cursors may not update.", LogLevel.Warn);
    }

    private static void OnWorldCursorEvaluated()
    {
        _instance?.CaptureCursorState(Game1.mouseCursor, Game1.mouseCursorTransparency);
    }

    private static void PostfixMenuDrawMouse(bool ignore_transparency, int cursor)
    {
        if (_instance is null)
            return;

        int effectiveCursor = cursor >= 0
            ? cursor
            : (Game1.options?.snappyMenus == true && Game1.options.gamepadControls ? Game1.cursor_gamepad_pointer : Game1.cursor_default);
        float alpha = ignore_transparency ? 1f : Game1.mouseCursorTransparency;
        _instance.CaptureCursorState(effectiveCursor, alpha);
    }

    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        this.ApplyHardwareCursorOption();

        var gmcm = this.Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
        if (gmcm is null)
            return;

        gmcm.Register(
            mod: this.ModManifest,
            reset: () =>
            {
                this.Config = new ModConfig();
                this.ResetCursorCache();
            },
            save: () =>
            {
                this.Helper.WriteConfig(this.Config);
                this.ResetCursorCache();
                this.ApplyHardwareCursorOption();
            });

        gmcm.AddBoolOption(
            mod: this.ModManifest,
            getValue: () => this.Config.Enabled,
            setValue: val =>
            {
                this.Config.Enabled = val;
                if (!val)
                    this.RestoreDefaultOsCursor();
                else
                    this.ApplyHardwareCursorOption();
            },
            name: () => "Enable Native Stardew Cursor",
            tooltip: () => "Draws Stardew's cursor icons at full monitor refresh rate (e.g. 300Hz) via OS hardware cursor.");

        gmcm.AddNumberOption(
            mod: this.ModManifest,
            getValue: () => Math.Clamp(this.Config.CursorScale, 2, 6),
            setValue: val =>
            {
                this.Config.CursorScale = Math.Clamp(val, 2, 6);
                this.ResetCursorCache();
            },
            name: () => "Cursor Size Scale",
            tooltip: () => "Scale multiplier for the 16x16 cursor sprite (3 = 48px, 4 = 64px).",
            min: 2,
            max: 6,
            interval: 1,
            formatValue: val => $"{val}x ({val * TileSize}px)");

        gmcm.AddBoolOption(
            mod: this.ModManifest,
            getValue: () => this.Config.ForceHardwareCursorOption,
            setValue: val =>
            {
                this.Config.ForceHardwareCursorOption = val;
                this.ApplyHardwareCursorOption();
            },
            name: () => "Auto-Enable Hardware Cursor",
            tooltip: () => "Keeps Stardew's Hardware Cursor option enabled so the 60 FPS software cursor is hidden.");

        gmcm.AddBoolOption(
            mod: this.ModManifest,
            getValue: () => this.Config.RespectHiddenCursor,
            setValue: val => this.Config.RespectHiddenCursor = val,
            name: () => "Hide Cursor When Game Hides It",
            tooltip: () => "Hides the OS cursor during cutscenes or when gamepad mode / idle fade hides it.");
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        this.ApplyHardwareCursorOption();
        this._lastAppliedKey = (int.MinValue, -1);
    }

    private void OnAssetReady(object? sender, AssetReadyEventArgs e)
    {
        if (e.NameWithoutLocale.IsEquivalentTo("LooseSprites/Cursors"))
            this.ResetCursorCache();
    }

    private void OnRendering(object? sender, RenderingEventArgs e)
    {
        this._capturedThisFrame = false;
    }

    private void OnRendered(object? sender, RenderedEventArgs e)
    {
        if (!this.Config.Enabled)
        {
            if (this._wasActiveLastFrame)
                this.RestoreDefaultOsCursor();
            return;
        }

        if (!this._capturedThisFrame)
        {
            // During cutscenes/frozen controls where neither drawMouseCursor nor drawMouse ran
            if (Game1.eventUp || Game1.freezeControls)
                this.CaptureCursorState(Game1.cursor_none, 0f);
            else if (Game1.currentMinigame is not null)
                this.CaptureCursorState(Game1.mouseCursor < 0 ? Game1.cursor_default : Game1.mouseCursor, 1f);
            else
                this.CaptureCursorState(Game1.mouseCursor, Game1.mouseCursorTransparency);
        }
    }

    private void CaptureCursorState(int cursorId, float transparency)
    {
        if (!this.Config.Enabled)
            return;

        if (Game1.options is null || Game1.game1 is null || Game1.mouseCursors is null || Game1.mouseCursors.IsDisposed)
            return;

        if (this.Config.ForceHardwareCursorOption && !Game1.options.hardwareCursor)
            Game1.options.hardwareCursor = true;

        if (!Game1.options.hardwareCursor)
        {
            if (this._wasActiveLastFrame)
                this.RestoreDefaultOsCursor();
            return;
        }

        if (!Game1.game1.IsMouseVisible)
            Game1.game1.IsMouseVisible = true;

        this._capturedThisFrame = true;

        int scale = Math.Clamp(this.Config.CursorScale, 2, 6);
        if (!ReferenceEquals(this._lastSourceTexture, Game1.mouseCursors) || this._lastAppliedScale != scale)
        {
            this.ResetCursorCache();
            this._lastSourceTexture = Game1.mouseCursors;
            this._lastAppliedScale = scale;
        }

        bool shouldHide = this.Config.RespectHiddenCursor
            && (cursorId == Game1.cursor_none
                || transparency <= 0.05f);

        (int CursorId, int AlphaTier) desiredKey;
        if (shouldHide)
        {
            desiredKey = (HiddenCursorKey, 0);
        }
        else
        {
            int cleanId = cursorId < 0 ? Game1.cursor_default : cursorId;
            int alphaTier = transparency < 0.75f ? 1 : 2; // 1 = dimmed (~50% out of reach), 2 = full opaque
            desiredKey = (cleanId, alphaTier);
        }

        if (desiredKey != this._lastAppliedKey || !this._wasActiveLastFrame)
        {
            MouseCursor? mc = this.GetOrCreateCursor(desiredKey.CursorId, desiredKey.AlphaTier, scale);
            if (mc is not null)
            {
                Mouse.SetCursor(mc);
                this._lastAppliedKey = desiredKey;
                this._wasActiveLastFrame = true;
            }
        }
    }

    private void ApplyHardwareCursorOption()
    {
        if (!this.Config.Enabled || !this.Config.ForceHardwareCursorOption)
            return;

        if (Game1.options is not null)
            Game1.options.hardwareCursor = true;
        if (Game1.game1 is not null)
            Game1.game1.IsMouseVisible = true;
    }

    private void RestoreDefaultOsCursor()
    {
        try
        {
            Mouse.SetCursor(MouseCursor.Arrow);
        }
        catch { }

        this._lastAppliedKey = (int.MinValue, -1);
        this._wasActiveLastFrame = false;
    }

    private MouseCursor? GetOrCreateCursor(int cursorId, int alphaTier, int scale)
    {
        var key = (cursorId, alphaTier);
        if (this._cursorCache.TryGetValue(key, out var cached))
            return cached.Cursor;

        try
        {
            if (cursorId == HiddenCursorKey)
            {
                var blankTex = new Texture2D(Game1.graphics.GraphicsDevice, 1, 1, false, SurfaceFormat.Color);
                blankTex.SetData(new[] { Color.Transparent });
                MouseCursor hidden = MouseCursor.FromTexture2D(blankTex, 0, 0);
                this._cursorCache[key] = (hidden, blankTex);
                return hidden;
            }

            Texture2D src = Game1.mouseCursors;
            Rectangle rect = Game1.getSourceRectForStandardTileSheet(src, cursorId, TileSize, TileSize);
            if (rect.X < 0 || rect.Y < 0 || rect.Right > src.Width || rect.Bottom > src.Height)
            {
                if (cursorId != Game1.cursor_default)
                    return this.GetOrCreateCursor(Game1.cursor_default, alphaTier, scale);
                return null;
            }

            var srcPixels = new Color[TileSize * TileSize];
            src.GetData(0, rect, srcPixels, 0, srcPixels.Length);

            float alphaMul = alphaTier == 1 ? 0.55f : 1f;
            int outSize = TileSize * scale;
            var outPixels = new Color[outSize * outSize];
            for (int sy = 0; sy < TileSize; sy++)
            {
                for (int sx = 0; sx < TileSize; sx++)
                {
                    Color c = srcPixels[sy * TileSize + sx];
                    if (c.A is > 0 and < 255)
                    {
                        c = new Color(
                            (byte)Math.Min(255, c.R * 255 / c.A),
                            (byte)Math.Min(255, c.G * 255 / c.A),
                            (byte)Math.Min(255, c.B * 255 / c.A),
                            c.A);
                    }
                    if (alphaMul < 0.99f && c.A > 0)
                    {
                        c = new Color(c.R, c.G, c.B, (byte)Math.Clamp((int)Math.Round(c.A * alphaMul), 1, 255));
                    }

                    int baseY = sy * scale;
                    int baseX = sx * scale;
                    for (int dy = 0; dy < scale; dy++)
                    {
                        int rowOffset = (baseY + dy) * outSize + baseX;
                        for (int dx = 0; dx < scale; dx++)
                            outPixels[rowOffset + dx] = c;
                    }
                }
            }

            var outTex = new Texture2D(Game1.graphics.GraphicsDevice, outSize, outSize, false, SurfaceFormat.Color);
            outTex.SetData(outPixels);
            MouseCursor mc = MouseCursor.FromTexture2D(outTex, 0, 0);
            this._cursorCache[key] = (mc, outTex);
            return mc;
        }
        catch (Exception ex)
        {
            this.Monitor.Log($"Failed to create native cursor for ID {cursorId}: {ex.Message}", LogLevel.Warn);
            return null;
        }
    }

    private void ResetCursorCache()
    {
        this.RestoreDefaultOsCursor();

        foreach (var entry in this._cursorCache.Values)
        {
            try { entry.Cursor.Dispose(); } catch { }
            try { entry.Texture.Dispose(); } catch { }
        }
        this._cursorCache.Clear();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            this.ResetCursorCache();
        base.Dispose(disposing);
    }
}
