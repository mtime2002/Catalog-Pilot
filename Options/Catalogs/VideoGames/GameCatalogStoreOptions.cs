namespace CatalogPilot.Options;

public sealed class GameCatalogStoreOptions
{
    public const string SectionName = "GameCatalogStore";

    public string DatabasePath { get; set; } = "Data/game-catalog.db";

    public bool EnableSeedFromExternal { get; set; } = true;

    public int TargetSeedCount { get; set; } = 1200;
}
