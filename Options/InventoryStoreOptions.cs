namespace CatalogPilot.Options;

public sealed class InventoryStoreOptions
{
    public const string SectionName = "InventoryStore";

    public string DatabasePath { get; set; } = "Data/inventory-store.db";
}
