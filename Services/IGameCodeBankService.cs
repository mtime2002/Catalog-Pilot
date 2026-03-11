using CatalogPilot.Models;

namespace CatalogPilot.Services;

public interface IGameCodeBankService
{
    GameCodeMatchResult? FindBestMatch(IEnumerable<string> detectedCodes, string? platformHint = null);
}
