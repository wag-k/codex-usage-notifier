using System.Security.Cryptography;
using System.Text.Json;
using CodexUsageNotifier.Application.Abstractions;
using CodexUsageNotifier.Application.Gmail;

namespace CodexUsageNotifier.Infrastructure.Gmail;

/// <summary>
/// GoogleのIDataStore内容と認証メタデータをDPAPI保護ファイルへ原子的に保存します。
/// </summary>
public sealed class DpapiGmailCredentialStore : IGmailCredentialStore, IDisposable
{
    private const int CurrentSchemaVersion = 1;
    private const string MetadataKey = "__gmail_metadata";
    private readonly string filePath;
    private readonly IUserDataProtector protector;
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>保存先と暗号化処理を受け取ります。</summary>
    public DpapiGmailCredentialStore(IAppDataPaths paths, IUserDataProtector protector)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(protector);
        filePath = paths.GoogleCredentialFilePath;
        this.protector = protector;
    }

    /// <inheritdoc />
    public bool Exists => File.Exists(filePath);

    /// <inheritdoc />
    public async Task StoreAsync<T>(string key, T value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            CredentialEnvelope envelope = await ReadEnvelopeCoreAsync(CancellationToken.None).ConfigureAwait(false);
            envelope.Entries[key] = JsonSerializer.Serialize(value);
            await WriteEnvelopeCoreAsync(envelope, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task DeleteAsync<T>(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            CredentialEnvelope envelope = await ReadEnvelopeCoreAsync(CancellationToken.None).ConfigureAwait(false);
            if (envelope.Entries.Remove(key))
            {
                await WriteOrDeleteCoreAsync(envelope, CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<T?> GetAsync<T>(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            CredentialEnvelope envelope = await ReadEnvelopeCoreAsync(CancellationToken.None).ConfigureAwait(false);
            return envelope.Entries.TryGetValue(key, out string? json)
                ? JsonSerializer.Deserialize<T>(json)
                : default;
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException or IOException)
        {
            throw new GmailCredentialStoreException("保存されたGmail認証情報を読み込めません。再認証してください。", exception);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task ClearAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new GmailCredentialStoreException("Gmail認証情報を削除できません。", exception);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public Task<GmailCredentialMetadata?> LoadMetadataAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return GetAsync<GmailCredentialMetadata>(MetadataKey);
    }

    /// <inheritdoc />
    public Task SaveMetadataAsync(GmailCredentialMetadata metadata, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        cancellationToken.ThrowIfCancellationRequested();
        return StoreAsync(MetadataKey, metadata);
    }

    /// <summary>保護ファイルを復号してスキーマを検証します。</summary>
    private async Task<CredentialEnvelope> ReadEnvelopeCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            return new CredentialEnvelope();
        }

        byte[] encrypted = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
        byte[] plaintext = protector.Unprotect(encrypted);
        try
        {
            CredentialEnvelope? envelope = JsonSerializer.Deserialize<CredentialEnvelope>(plaintext);
            if (envelope is null || envelope.SchemaVersion != CurrentSchemaVersion || envelope.Entries is null)
            {
                throw new JsonException("Unsupported Gmail credential schema.");
            }

            return envelope;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>資格情報が空なら削除し、それ以外は保存します。</summary>
    private Task WriteOrDeleteCoreAsync(CredentialEnvelope envelope, CancellationToken cancellationToken)
    {
        if (envelope.Entries.Count != 0)
        {
            return WriteEnvelopeCoreAsync(envelope, cancellationToken);
        }

        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }

        return Task.CompletedTask;
    }

    /// <summary>平文JSONをメモリ上で暗号化し、暗号文だけを原子的に保存します。</summary>
    private async Task WriteEnvelopeCoreAsync(CredentialEnvelope envelope, CancellationToken cancellationToken)
    {
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(envelope);
        byte[] encrypted;
        try
        {
            encrypted = protector.Protect(plaintext);
        }
        catch (Exception exception) when (exception is CryptographicException or PlatformNotSupportedException)
        {
            throw new GmailCredentialStoreException("Gmail認証情報を暗号化できません。", exception);
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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new GmailCredentialStoreException("Gmail認証情報を保存できません。", exception);
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

    /// <inheritdoc />
    public void Dispose()
    {
        gate.Dispose();
    }

    /// <summary>暗号化前の資格情報コンテナーを表します。</summary>
    private sealed class CredentialEnvelope
    {
        /// <summary>保存形式のスキーマバージョンを取得または設定します。</summary>
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        /// <summary>Googleデータストア項目と認証メタデータを取得または設定します。</summary>
        public Dictionary<string, string> Entries { get; set; } = new(StringComparer.Ordinal);
    }
}
