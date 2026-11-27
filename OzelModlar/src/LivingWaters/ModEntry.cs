using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Tools;

namespace LivingWaters;

public interface IGenericModConfigMenuApi
{
    void Register(IManifest mod, Action reset, Action save, bool titleScreenOnly = false);
    void AddSectionTitle(IManifest mod, Func<string> text, Func<string>? tooltip = null);
    void AddBoolOption(IManifest mod, Func<bool> getValue, Action<bool> setValue, Func<string> name, Func<string>? tooltip = null, string? fieldId = null);
    void AddNumberOption(IManifest mod, Func<int> getValue, Action<int> setValue, Func<string> name, Func<string>? tooltip = null, int? min = null, int? max = null, int? interval = null, Func<int, string>? formatValue = null, string? fieldId = null);
    void AddKeybind(IManifest mod, Func<SButton> getValue, Action<SButton> setValue, Func<string> name, Func<string>? tooltip = null, string? fieldId = null);
}

public sealed class ModEntry : Mod
{
    private const string SaveDataKey = "living-waters-ecosystem";
    private static EcosystemManager? ActiveEcosystem;
    private static VisibleFishBridge? ActiveVisibleFish;

    private ModConfig Config = new();
    private EcosystemLogger AuditLog = null!;
    private EcosystemManager Ecosystem = null!;
    private WaterHudRenderer HudRenderer = null!;
    private VisibleFishBridge VisibleFish = null!;
    private FishReleaseAnimator ReleaseAnimator = null!;

    private Harmony? HarmonyInstance;

    public override void Entry(IModHelper helper)
    {
        this.Config = helper.ReadConfig<ModConfig>();
        this.AuditLog = new EcosystemLogger(this.Monitor, this.ModManifest.Version.ToString());
        this.Ecosystem = new EcosystemManager(this.Monitor, () => this.Config, this.AuditLog);
        this.HudRenderer = new WaterHudRenderer(this.Ecosystem, () => this.Config, helper.Input);
        this.VisibleFish = new VisibleFishBridge(this.Monitor, this.Ecosystem, () => this.Config);
        this.ReleaseAnimator = new FishReleaseAnimator(this.Monitor, this.VisibleFish);
        ActiveEcosystem = this.Ecosystem;
        ActiveVisibleFish = this.VisibleFish;

        this.ApplyHarmonyPatches();

        helper.Events.GameLoop.GameLaunched += this.OnGameLaunched;
        helper.Events.GameLoop.SaveLoaded += this.OnSaveLoaded;
        helper.Events.GameLoop.DayStarted += this.OnDayStarted;
        helper.Events.GameLoop.Saving += this.OnSaving;
        helper.Events.GameLoop.ReturnedToTitle += this.OnReturnedToTitle;
        helper.Events.GameLoop.UpdateTicked += this.ReleaseAnimator.OnUpdateTicked;
        helper.Events.Input.ButtonPressed += this.OnButtonPressed;
        helper.Events.Display.RenderedWorld += this.ReleaseAnimator.OnRenderedWorld;
        helper.Events.Display.RenderedHud += this.HudRenderer.OnRenderedHud;
    }

    private void ApplyHarmonyPatches()
    {
        var harmony = new Harmony(this.ModManifest.UniqueID);
        this.HarmonyInstance = harmony;

        var getFishMethod = AccessTools.Method(typeof(GameLocation), nameof(GameLocation.getFish));
        harmony.Patch(
            original: getFishMethod,
            postfix: new HarmonyMethod(typeof(ModEntry), nameof(Postfix_GetFish)));

        var pullFishMethod = AccessTools.Method(typeof(FishingRod), nameof(FishingRod.pullFishFromWater));
        harmony.Patch(
            original: pullFishMethod,
            postfix: new HarmonyMethod(typeof(ModEntry), nameof(Postfix_PullFishFromWater)));
    }

    private static void Postfix_GetFish(GameLocation __instance, Vector2 bobberTile, Farmer who, ref Item? __result)
    {
        try
        {
            if (ActiveEcosystem is null || __instance is null)
                return;

            __result = ActiveEcosystem.ModifyGetFishResult(__instance, bobberTile, who, __result);
        }
        catch
        {
            // Never break vanilla fishing if a mod passes unexpected location/tile state
        }
    }

    private static void Postfix_PullFishFromWater(
        FishingRod __instance,
        string fishId,
        bool fromFishPond,
        bool isBossFish,
        int numCaught)
    {
        try
        {
            if (ActiveEcosystem is null || fromFishPond || isBossFish || numCaught <= 0)
                return;

            Farmer? player = __instance.getLastFarmerToUse() ?? Game1.player;
            GameLocation? loc = player?.currentLocation ?? Game1.currentLocation;
            if (player is null || loc is null)
                return;

            Vector2 bobberTile = __instance.bobber.Value != Vector2.Zero
                ? __instance.bobber.Value / 64f
                : player.Tile;
            ActiveEcosystem.OnFishPulledFromWater(loc, bobberTile, player, fishId, numCaught);
            ActiveVisibleFish?.OnFishCaughtLive(loc, bobberTile, EcosystemManager.NormalizeItemId(fishId));
        }
        catch
        {
            // Keep catch flow safe
        }
    }

    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        if (this.HarmonyInstance is not null)
            this.VisibleFish.TryAttach(this.HarmonyInstance, this.Helper.ModRegistry);

        var gmcm = this.Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
        if (gmcm is null)
            return;

        gmcm.Register(
            mod: this.ModManifest,
            reset: () => this.Config = new ModConfig(),
            save: () => this.Helper.WriteConfig(this.Config));

        gmcm.AddSectionTitle(this.ModManifest, () => "Canlı Sular Ayarları");

        gmcm.AddBoolOption(
            mod: this.ModManifest,
            getValue: () => this.Config.EnableMod,
            setValue: val => this.Config.EnableMod = val,
            name: () => "Modu Etkinleştir",
            tooltip: () => "Balık popülasyonu, üreme, göç ve suya salma sistemini açar/kapatır.");

        gmcm.AddBoolOption(
            mod: this.ModManifest,
            getValue: () => this.Config.OnlyFarmWaters,
            setValue: val => this.Config.OnlyFarmWaters = val,
            name: () => "Sadece Çiftlik Suları",
            tooltip: () => "Açıkken yalnızca Çiftlik suyundaki popülasyon yönetilir; vadi suları etkilenmez.");

        gmcm.AddBoolOption(
            mod: this.ModManifest,
            getValue: () => this.Config.ProtectNeededFish,
            setValue: val => this.Config.ProtectNeededFish = val,
            name: () => "Halkevi ve Görev Koruması",
            tooltip: () => "Halkevi'nde eksik, görevlerde istenen veya henüz yakalanmamış balıkların şansı asla düşmez.");

        gmcm.AddBoolOption(
            mod: this.ModManifest,
            getValue: () => this.Config.AllowLocalExtinction,
            setValue: val => this.Config.AllowLocalExtinction = val,
            name: () => "Doğal Türler Tükenebilsin",
            tooltip: () => "Kapalıyken (önerilen) doğal sularda balık sayısı taban sınırın altına düşmez.");

        gmcm.AddBoolOption(
            mod: this.ModManifest,
            getValue: () => this.Config.FarmAllowsAllSeasons,
            setValue: val => this.Config.FarmAllowsAllSeasons = val,
            name: () => "Çiftlikte Her Mevsim Üreme",
            tooltip: () => "Çiftlik suyuna salınan balıklar mevsim fark etmeksizin yaşayıp üreyebilir.");

        gmcm.AddBoolOption(
            mod: this.ModManifest,
            getValue: () => this.Config.EmptyFarmStart,
            setValue: val => this.Config.EmptyFarmStart = val,
            name: () => "Çiftlik Boş Başlasın",
            tooltip: () => "Açıkken Çiftlik suyu 0 yerli balıkla başlar; popülasyonu siz salarak veya göçle başlatırsınız.");

        gmcm.AddNumberOption(
            mod: this.ModManifest,
            getValue: () => this.Config.BasePopulationPerSpecies,
            setValue: val => this.Config.BasePopulationPerSpecies = val,
            name: () => "Başlangıç Tür Nüfusu",
            min: 4,
            max: 30,
            interval: 1);

        gmcm.AddNumberOption(
            mod: this.ModManifest,
            getValue: () => this.Config.MinNaturalFloor,
            setValue: val => this.Config.MinNaturalFloor = val,
            name: () => "Doğal Taban Nüfusu",
            min: 1,
            max: 10,
            interval: 1);

        gmcm.AddNumberOption(
            mod: this.ModManifest,
            getValue: () => this.Config.MaxPopulationPerSpecies,
            setValue: val => this.Config.MaxPopulationPerSpecies = val,
            name: () => "Azami Tür Kapasitesi",
            min: 15,
            max: 100,
            interval: 5);

        gmcm.AddNumberOption(
            mod: this.ModManifest,
            getValue: () => this.Config.BreedingChancePercent,
            setValue: val => this.Config.BreedingChancePercent = val,
            name: () => "Gece Üreme Şansı (%)",
            min: 0,
            max: 100,
            interval: 5,
            formatValue: val => $"%{val}");

        gmcm.AddNumberOption(
            mod: this.ModManifest,
            getValue: () => this.Config.MigrationChancePercent,
            setValue: val => this.Config.MigrationChancePercent = val,
            name: () => "Nehirler Arası Göç Şansı (%)",
            min: 0,
            max: 100,
            interval: 5,
            formatValue: val => $"%{val}");

        gmcm.AddBoolOption(
            mod: this.ModManifest,
            getValue: () => this.Config.SyncVisibleFish,
            setValue: val => this.Config.SyncVisibleFish = val,
            name: () => "VisibleFish Senkronu",
            tooltip: () => "Sudaki balık gölgelerini gerçek Canlı Sular tür ve sayılarına göre çizer.");

        gmcm.AddNumberOption(
            mod: this.ModManifest,
            getValue: () => this.Config.MaxVisibleFishPerZone,
            setValue: val => this.Config.MaxVisibleFishPerZone = val,
            name: () => "Sudaki Azami Görünür Balık",
            min: 20,
            max: 250,
            interval: 10);

        gmcm.AddBoolOption(
            mod: this.ModManifest,
            getValue: () => this.Config.ShowTooltipOnWaterHover,
            setValue: val => this.Config.ShowTooltipOnWaterHover = val,
            name: () => "Su Üstünde Nüfus Bilgisi",
            tooltip: () => "Olta veya balık tutarken suyun üzerine gelince bilgi kutusu gösterir.");

        gmcm.AddBoolOption(
            mod: this.ModManifest,
            getValue: () => this.Config.RequireShiftForRodTooltip,
            setValue: val => this.Config.RequireShiftForRodTooltip = val,
            name: () => "Olta İpucu İçin Shift Gerekli",
            tooltip: () => "Açıkken olta ile suya bakınca büyük kutu otomatik çıkmaz; yalnızca Shift basılıyken çıkar.");

        gmcm.AddKeybind(
            mod: this.ModManifest,
            getValue: () => this.Config.TogglePanelKey,
            setValue: val => this.Config.TogglePanelKey = val,
            name: () => "Nüfus Paneli Tuşu",
            tooltip: () => "Yakınında olduğunuz ve imleci üstüne getirdiğiniz suyun popülasyon panelini açar/kapatır.");
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        this.ReleaseAnimator.Clear();
        this.AuditLog.ResetForSave(Constants.SaveFolderName);
        var saved = this.Helper.Data.ReadSaveData<WaterEcosystemSaveData>(SaveDataKey);
        this.Ecosystem.LoadState(saved);
    }

    private void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        this.Ecosystem.ProcessNewDay(out string? toast);
        this.VisibleFish.SyncCurrentLocation(Game1.currentLocation);
        if (!string.IsNullOrEmpty(toast))
            Game1.addHUDMessage(new HUDMessage(toast, HUDMessage.achievement_type) { timeLeft = 3200f });
    }

    private void OnSaving(object? sender, SavingEventArgs e)
    {
        if (Context.IsMainPlayer)
        {
            var state = this.Ecosystem.ExportState();
            this.Helper.Data.WriteSaveData(SaveDataKey, state);
            this.Ecosystem.RecordSaveSnapshot();
        }
    }

    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
    {
        this.ReleaseAnimator.Clear();
        this.AuditLog.Flush();
        this.AuditLog.ResetForSave(null);
        this.Ecosystem.LoadState(null);
    }

    private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (!Context.IsWorldReady || !this.Config.EnableMod)
            return;

        if (e.Button == this.Config.TogglePanelKey)
        {
            if (Game1.activeClickableMenu is WaterJournalMenu journal)
            {
                journal.exitThisMenu();
                Game1.playSound("bigDeSelect");
                this.Helper.Input.Suppress(e.Button);
                return;
            }

            if (Game1.activeClickableMenu is null)
            {
                this.Helper.Input.Suppress(e.Button);

                if (!this.Ecosystem.TryGetInspectableWaterZone(
                    Game1.currentLocation,
                    Game1.player.Tile,
                    e.Cursor.Tile,
                    out string? targetZoneKey,
                    out string warningMessage))
                {
                    Game1.playSound("cancel");
                    Game1.addHUDMessage(new HUDMessage(warningMessage, HUDMessage.error_type) { timeLeft = 2800f });
                    return;
                }

                Game1.playSound("bigSelect");
                Game1.activeClickableMenu = new WaterJournalMenu(
                    this.Ecosystem,
                    this.Config,
                    Game1.currentLocation,
                    targetZoneKey!);
                return;
            }
        }

        if (!Context.CanPlayerMove || Game1.activeClickableMenu is not null || !e.Button.IsActionButton())
            return;

        if (this.Ecosystem.TryReleaseFish(Game1.currentLocation, e.Cursor.Tile, Game1.player, out string message, out string? releasedQid))
        {
            this.Helper.Input.Suppress(e.Button);
            if (!string.IsNullOrEmpty(releasedQid))
                this.ReleaseAnimator.LaunchFish(Game1.currentLocation, Game1.player, e.Cursor.Tile, releasedQid);
            if (!string.IsNullOrEmpty(message))
                Game1.addHUDMessage(new HUDMessage(message, HUDMessage.newQuest_type) { timeLeft = 2600f });
        }
    }
}
