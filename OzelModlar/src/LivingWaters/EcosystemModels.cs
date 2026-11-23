namespace LivingWaters;

public sealed class SpeciesHistory
{
    public string Origin { get; set; } = "Natural"; // "Natural", "Stocked", "Migration"
    public string? SourceLocation { get; set; }
    public int FirstSeenTotalDays { get; set; } = -1;
    public int LastBirthTotalDays { get; set; } = -1;
    public int LastMigrationTotalDays { get; set; } = -1;
    public int TotalBorn { get; set; }
    public int TotalCaught { get; set; }
    public int TotalMigratedIn { get; set; }
    public int TotalMigratedOut { get; set; }
    public int TotalReleased { get; set; }
}

public sealed class ZonePopulation
{
    public string LastSeasonInitialized { get; set; } = string.Empty;
    public Dictionary<string, int> SpeciesCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> PlayerStockedSpecies { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> DailyCaughtToday { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, SpeciesHistory> SpeciesDetails { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class WaterEcosystemSaveData
{
    public int SchemaVersion { get; set; } = 3;
    public int LastProcessedTotalDays { get; set; } = -1;
    public string LastSeason { get; set; } = string.Empty;
    public Dictionary<string, ZonePopulation> Zones { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
