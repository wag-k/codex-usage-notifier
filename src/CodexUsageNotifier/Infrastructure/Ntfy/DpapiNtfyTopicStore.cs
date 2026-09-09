using System.Security.Cryptography;
using System.Text.Json;
using CodexUsageNotifier.Application.Abstractions;
using CodexUsageNotifier.Application.Gmail;
using CodexUsageNotifier.Application.Ntfy;

namespace CodexUsageNotifier.Infrastructure.Ntfy;

/// <summary>ntfy TopicをWindowsユーザー単位で暗号化し原子的に保存します。</summary>
public sealed class DpapiNtfyTopicStore : INtfyTopicStore, INtfyTopicConfigurationStatusProvider, IDisposable
{
    private const int CurrentSchemaVersion = 1;
    private readonly string filePath;
    private readonly IUserDataProtector protector;
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>保存先とDPAPI保護処理を受け取ります。</summary>
    public DpapiNtfyTopicStore(IAppDataPaths paths, IUserDataProtector protector)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(protector);
        filePath = paths.NtfyTopicFilePath;
        this.protector = protector;
    }

    /// <inheritdoc />
    public async Task<NtfyTopic?> LoadAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(filePath))
            {
                return null;
            }

            byte[] encrypted = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
            byte[] plaintext = protector.Unprotect(encrypted);
            try
            {
                TopicEnvelope? envelope = JsonSerializer.Deserialize<TopicEnvelope>(plaintext);
                if (envelope is null || envelope.SchemaVersion != CurrentSchemaVersion
                    || !NtfyTopicValidator.IsValid(envelope.Topic)
                    || string.IsNullOrWhiteSpace(envelope.GenerationId))
                {
                    throw new JsonException("Unsupported ntfy topic schema.");
                }

                return new NtfyTopic
                {
                    Value = envelope.Topic,
                    GenerationId = envelope.GenerationId,
                    CreatedAtUtc = envelope.CreatedAtUtc,
                };
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("保存されたスマホ通知Topicを読み込めません。再生成してください。", exception);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(NtfyTopic topic, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(topic);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(new TopicEnvelope
            {
                Topic = topic.Value,
                GenerationId = topic.GenerationId,
                CreatedAtUtc = topic.CreatedAtUtc,
            });
            byte[] encrypted;
            try
            {
                encrypted = protector.Protect(plaintext);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            string temporary = filePath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await File.WriteAllBytesAsync(temporary, encrypted, cancellationToken).ConfigureAwait(false);
                File.Move(temporary, filePath, overwrite: true);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encrypted);
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task DeleteAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> IsConfiguredAsync(CancellationToken cancellationToken) =>
        await LoadAsync(cancellationToken).ConfigureAwait(false) is not null;

    /// <inheritdoc />
    public void Dispose() => gate.Dispose();

    /// <summary>暗号化前のTopic保存形式を表します。</summary>
    private sealed class TopicEnvelope
    {
        /// <summary>保存形式のバージョンを取得または設定します。</summary>
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        /// <summary>秘密Topicを取得または設定します。</summary>
        public string Topic { get; set; } = string.Empty;

        /// <summary>Topic世代の非秘密IDを取得または設定します。</summary>
        public string GenerationId { get; set; } = string.Empty;

        /// <summary>生成UTC時刻を取得または設定します。</summary>
        public DateTimeOffset CreatedAtUtc { get; set; }
    }
}
