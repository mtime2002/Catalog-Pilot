using System.ComponentModel.DataAnnotations;

namespace CatalogPilot.Models;

public sealed class ListingInput
{
    [Required]
    [StringLength(120, MinimumLength = 3)]
    public string ItemName { get; set; } = string.Empty;

    [StringLength(4000)]
    public string Description { get; set; } = string.Empty;

    [StringLength(50)]
    public string Platform { get; set; } = string.Empty;

    [StringLength(30)]
    public string Condition { get; set; } = "Used";

    public bool IsSealed { get; set; }

    [Range(1, 20)]
    public int Quantity { get; set; } = 1;

    [Range(typeof(decimal), "0.01", "100000")]
    public decimal? UserPriceOverride { get; set; }

    public List<UploadedPhoto> Photos { get; set; } = [];
}
