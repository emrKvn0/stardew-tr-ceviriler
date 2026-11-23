using StardewModdingAPI;

namespace LivingWaters;

public sealed class ModConfig
{
    public bool EnableMod { get; set; } = true;
    public int BasePopulationPerSpecies { get; set; } = 12;
    public int MinNaturalFloor { get; set; } = 3;
    public int MaxPopulationPerSpecies { get; set; } = 40;
    public int BreedingChancePercent { get; set; } = 35;
    public int MigrationChancePercent { get; set; } = 20;
    public bool ProtectNeededFish { get; set; } = true;
    public bool AllowLocalExtinction { get; set; } = false;
    public bool OnlyFarmWaters { get; set; } = false;
    public bool EmptyFarmStart { get; set; } = true;
    public bool FarmAllowsAllSeasons { get; set; } = true;
    public bool SyncVisibleFish { get; set; } = true;
    public int MaxVisibleFishPerZone { get; set; } = 80;
    public bool ShowTooltipOnWaterHover { get; set; } = true;
    public bool RequireShiftForRodTooltip { get; set; } = true;
    public SButton TogglePanelKey { get; set; } = SButton.F6;
}
