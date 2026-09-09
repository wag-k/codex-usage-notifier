using System.Security.Cryptography;
using CodexUsageNotifier.Application.Ntfy;

namespace CodexUsageNotifier.Infrastructure.Ntfy;

/// <summary>暗号学的乱数から推測困難な匿名ntfy Topicを生成します。</summary>
public sealed class CryptographicNtfyTopicGenerator : INtfyTopicGenerator
{
    /// <inheritdoc />
    public NtfyTopic Generate()
    {
        Span<byte> random = stackalloc byte[20];
        RandomNumberGenerator.Fill(random);
        string token = Convert.ToHexString(random).ToLowerInvariant();
        return new NtfyTopic
        {
            Value = $"codex-usage-{token}",
            GenerationId = Guid.NewGuid().ToString("N"),
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
    }
}
