using System.Security.Cryptography;
using System.Text;

namespace Xianix.Rules;

/// <summary>
/// SHA-256 content hash for <c>rules.json</c> integrity verification. Uses the same
/// algorithm as the Xians platform's <c>HashGenerator</c> (UTF-8 bytes → lowercase hex).
/// </summary>
internal static class RulesContentHasher
{
    public static string ComputeSha256Hex(string content)
    {
        if (string.IsNullOrEmpty(content))
            return string.Empty;

        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
