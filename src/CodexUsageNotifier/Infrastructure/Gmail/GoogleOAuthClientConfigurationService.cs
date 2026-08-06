using System.Text.Json;
using CodexUsageNotifier.Application.Abstractions;
using CodexUsageNotifier.Application.Gmail;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Logging;

namespace CodexUsageNotifier.Infrastructure.Gmail;

/// <summary>
/// Googleデスクトップアプリ用OAuthクライアント設定を検証して管理します。
/// </summary>
public sealed class GoogleOAuthClientConfigurationService : IGoogleOAuthClientConfigurationService
{
    private static readonly Action<ILogger, string, Exception?> LogConfigurationLoaded =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(4000, "GoogleOAuthConfigurationLoaded"),
            "Google OAuthクライアント設定を読み込みました。Path={Path}");
    private static readonly Action<ILogger, string, Exception?> LogConfigurationImported =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(4001, "GoogleOAuthConfigurationImported"),
            "Google OAuthクライアント設定を標準配置先へ保存しました。Path={Path}");

    private readonly IAppDataPaths paths;
    private readonly ILogger<GoogleOAuthClientConfigurationService> logger;

    /// <summary>保存先とログ出力先を受け取ります。</summary>
    public GoogleOAuthClientConfigurationService(
        IAppDataPaths paths,
        ILogger<GoogleOAuthClientConfigurationService> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(logger);
        this.paths = paths;
        this.logger = logger;
    }

    /// <inheritdoc />
    public async Task<GoogleOAuthClientConfigurationStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        string path = paths.GoogleOAuthClientFilePath;
        if (!File.Exists(path))
        {
            return new GoogleOAuthClientConfigurationStatus
            {
                StandardPath = path,
                Message = $"OAuthクライアント設定がありません。Google Cloud Consoleでデスクトップアプリ用JSONを作成し、［設定ファイルを選択］から登録してください。配置先: {path}",
            };
        }

        try
        {
            await LoadAsync(cancellationToken);
            return new GoogleOAuthClientConfigurationStatus
            {
                Exists = true,
                IsValid = true,
                StandardPath = path,
                Message = "OAuthクライアント設定を読み込み済みです。",
            };
        }
        catch (GoogleOAuthClientConfigurationException exception)
        {
            return new GoogleOAuthClientConfigurationStatus
            {
                Exists = true,
                StandardPath = path,
                Message = exception.Message,
            };
        }
    }

    /// <inheritdoc />
    public async Task<GmailOperationResult> ImportAsync(string sourcePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        try
        {
            byte[] content = await File.ReadAllBytesAsync(sourcePath, cancellationToken);
            ParseAndValidate(content);
            string destination = paths.GoogleOAuthClientFilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            string temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await File.WriteAllBytesAsync(temporary, content, cancellationToken);
                File.Move(temporary, destination, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }

            LogConfigurationImported(logger, destination, null);
            return new GmailOperationResult
            {
                Succeeded = true,
                Message = "OAuthクライアント設定を登録しました。",
            };
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException
            or GoogleOAuthClientConfigurationException)
        {
            return new GmailOperationResult
            {
                Message = exception is GoogleOAuthClientConfigurationException configurationException
                    ? configurationException.Message
                    : "OAuthクライアント設定を保存できませんでした。ファイルと保存先を確認してください。",
            };
        }
    }

    /// <inheritdoc />
    public async Task<ClientSecrets> LoadAsync(CancellationToken cancellationToken)
    {
        string path = paths.GoogleOAuthClientFilePath;
        if (!File.Exists(path))
        {
            throw new GoogleOAuthClientConfigurationException("OAuthクライアント設定ファイルがありません。");
        }

        try
        {
            byte[] content = await File.ReadAllBytesAsync(path, cancellationToken);
            ClientSecrets secrets = ParseAndValidate(content);
            LogConfigurationLoaded(logger, path, null);
            return secrets;
        }
        catch (GoogleOAuthClientConfigurationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new GoogleOAuthClientConfigurationException(
                "OAuthクライアント設定を読み込めません。JSON形式とアクセス権を確認してください。",
                exception);
        }
    }

    /// <summary>Googleのdesktop client JSONから必要項目だけを検証して返します。</summary>
    private static ClientSecrets ParseAndValidate(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        using JsonDocument document = JsonDocument.Parse(content);
        if (!document.RootElement.TryGetProperty("installed", out JsonElement installed)
            || !TryGetRequiredString(installed, "client_id", out string clientId)
            || !TryGetRequiredString(installed, "client_secret", out string clientSecret)
            || !TryGetRequiredString(installed, "auth_uri", out _)
            || !TryGetRequiredString(installed, "token_uri", out _)
            || !installed.TryGetProperty("redirect_uris", out JsonElement redirects)
            || redirects.ValueKind != JsonValueKind.Array
            || !redirects.EnumerateArray().Any(IsLoopbackRedirect))
        {
            throw new GoogleOAuthClientConfigurationException(
                "デスクトップアプリ用OAuthクライアントJSONではありません。必要項目とループバックredirect_urisを確認してください。");
        }

        return new ClientSecrets { ClientId = clientId, ClientSecret = clientSecret };
    }

    /// <summary>必須文字列を安全に取得します。</summary>
    private static bool TryGetRequiredString(JsonElement parent, string propertyName, out string value)
    {
        value = string.Empty;
        if (!parent.TryGetProperty(propertyName, out JsonElement element)
            || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    /// <summary>リダイレクトURIがローカルループバックかを検証します。</summary>
    private static bool IsLoopbackRedirect(JsonElement element)
    {
        return element.ValueKind == JsonValueKind.String
            && Uri.TryCreate(element.GetString(), UriKind.Absolute, out Uri? uri)
            && (uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase));
    }
}
