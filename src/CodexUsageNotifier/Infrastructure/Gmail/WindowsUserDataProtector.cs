using System.Security.Cryptography;
using System.Text;
using CodexUsageNotifier.Application.Gmail;

namespace CodexUsageNotifier.Infrastructure.Gmail;

/// <summary>
/// Windows DPAPIのCurrentUserスコープで認証情報を保護します。
/// </summary>
public sealed class WindowsUserDataProtector : IUserDataProtector
{
    private static readonly byte[] OptionalEntropy = Encoding.UTF8.GetBytes("CodexUsageNotifier:GmailCredential:v1");

    /// <inheritdoc />
    public byte[] Protect(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        return ProtectedData.Protect(plaintext, OptionalEntropy, DataProtectionScope.CurrentUser);
    }

    /// <inheritdoc />
    public byte[] Unprotect(byte[] protectedData)
    {
        ArgumentNullException.ThrowIfNull(protectedData);
        return ProtectedData.Unprotect(protectedData, OptionalEntropy, DataProtectionScope.CurrentUser);
    }
}
