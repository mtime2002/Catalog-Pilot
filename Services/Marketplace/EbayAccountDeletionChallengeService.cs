using System.Security.Cryptography;
using System.Text;

namespace CatalogPilot.Services;

public static class EbayAccountDeletionChallengeService
{
    public static string BuildChallengeResponse(string challengeCode, string verificationToken, string endpoint)
    {
        var seed = $"{challengeCode}{verificationToken}{endpoint}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
