using System.Net;
using System.Net.Http;
using CodexUsageNotifier.Application.Gmail;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Util;
using Microsoft.Extensions.Logging;

namespace CodexUsageNotifier.Infrastructure.Gmail;

/// <summary>
/// Gmail OAuth認証、トークン更新、状態管理、および認証解除を担当します。
/// </summary>
public sealed class GmailAuthenticationService :
    IGmailAuthenticationService,
    IGmailAuthenticationStatusProvider,
    IDisposable
{
    private static readonly TimeSpan AuthenticationTimeout = TimeSpan.FromMinutes(5);
    private static readonly Action<ILogger, Exception?> LogAuthenticationStarted =
        LoggerMessage.Define(LogLevel.Information, new EventId(4010, "GmailAuthenticationStarted"), "Gmail OAuth認証を開始しました。");
    private static readonly Action<ILogger, string, Exception?> LogAuthenticationSucceeded =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(4011, "GmailAuthenticationSucceeded"), "Gmail OAuth認証に成功しました。Account={Account}");
    private static readonly Action<ILogger, Exception?> LogAuthenticationCanceled =
        LoggerMessage.Define(LogLevel.Information, new EventId(4012, "GmailAuthenticationCanceled"), "Gmail OAuth認証をキャンセルしました。");
    private static readonly Action<ILogger, string, Exception?> LogAuthenticationFailed =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(4013, "GmailAuthenticationFailed"), "Gmail OAuth認証に失敗しました。Reason={Reason}");
    private static readonly Action<ILogger, Exception?> LogTokenRefreshed =
        LoggerMessage.Define(LogLevel.Information, new EventId(4014, "GmailTokenRefreshed"), "Gmailアクセストークンを更新しました。");
    private static readonly Action<ILogger, string, Exception?> LogReauthenticationRequired =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(4015, "GmailReauthenticationRequired"), "Gmailの再認証が必要です。Reason={Reason}");
    private static readonly Action<ILogger, bool, bool, Exception?> LogDisconnected =
        LoggerMessage.Define<bool, bool>(LogLevel.Information, new EventId(4016, "GmailDisconnected"),
            "Gmail認証解除を実行しました。RemoteRevoked={RemoteRevoked}, LocalRemoved={LocalRemoved}");

    private readonly IGoogleOAuthClientConfigurationService configurationService;
    private readonly IGmailCredentialStore credentialStore;
    private readonly IGoogleOAuthFlow oauthFlow;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<GmailAuthenticationService> logger;
    private readonly SemaphoreSlim authenticationGate = new(1, 1);
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private GmailAuthenticationStatus currentStatus = new() { State = GmailAuthenticationState.Unauthenticated };
    private UserCredential? currentCredential;
    private int disposed;

    /// <summary>OAuth設定、資格情報、Googleフロー、時刻、およびログ出力先を受け取ります。</summary>
    public GmailAuthenticationService(
        IGoogleOAuthClientConfigurationService configurationService,
        IGmailCredentialStore credentialStore,
        IGoogleOAuthFlow oauthFlow,
        TimeProvider timeProvider,
        ILogger<GmailAuthenticationService> logger)
    {
        ArgumentNullException.ThrowIfNull(configurationService);
        ArgumentNullException.ThrowIfNull(credentialStore);
        ArgumentNullException.ThrowIfNull(oauthFlow);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        this.configurationService = configurationService;
        this.credentialStore = credentialStore;
        this.oauthFlow = oauthFlow;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    /// <inheritdoc />
    public async Task<GmailAuthenticationStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        GoogleOAuthClientConfigurationStatus configuration =
            await configurationService.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (!configuration.IsValid)
        {
            currentCredential = null;
            currentStatus = new GmailAuthenticationStatus
            {
                State = GmailAuthenticationState.NotConfigured,
                HasClientConfiguration = configuration.Exists,
                LastErrorSummary = configuration.Exists ? configuration.Message : null,
            };
            return currentStatus;
        }

        if (currentStatus.State is GmailAuthenticationState.Authenticating
            or GmailAuthenticationState.ReauthenticationRequired)
        {
            return currentStatus with { HasClientConfiguration = true };
        }

        try
        {
            if (!credentialStore.Exists)
            {
                return SetUnauthenticated();
            }

            GmailCredentialMetadata? metadata = await credentialStore.LoadMetadataAsync(cancellationToken).ConfigureAwait(false);
            if (metadata is null)
            {
                return SetUnauthenticated();
            }

            currentCredential ??= await oauthFlow.LoadCredentialAsync(
                await configurationService.LoadAsync(cancellationToken).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);
            if (currentCredential is null)
            {
                return SetUnauthenticated();
            }

            bool expired = currentCredential.Token.IsStale;
            currentStatus = new GmailAuthenticationStatus
            {
                State = expired ? GmailAuthenticationState.RefreshRequired : GmailAuthenticationState.Authenticated,
                HasClientConfiguration = true,
                AuthenticatedEmailAddress = metadata.EmailAddress,
                LastAuthenticatedAtUtc = metadata.LastAuthenticatedAtUtc,
                LastTokenRefreshedAtUtc = metadata.LastTokenRefreshedAtUtc,
            };
            return currentStatus;
        }
        catch (GmailCredentialStoreException)
        {
            MarkReauthenticationRequired("保存された認証情報を復号できません。再認証してください。");
            return currentStatus;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            currentStatus = new GmailAuthenticationStatus
            {
                State = GmailAuthenticationState.Error,
                HasClientConfiguration = true,
                LastErrorSummary = "認証情報の読み込みに失敗しました。",
            };
            return currentStatus;
        }
        catch (Exception exception) when (exception is GoogleOAuthClientConfigurationException
            or TokenResponseException or InvalidOperationException or HttpRequestException)
        {
            currentStatus = new GmailAuthenticationStatus
            {
                State = GmailAuthenticationState.Error,
                HasClientConfiguration = true,
                LastErrorSummary = "Gmail認証状態を確認できませんでした。設定とネットワークを確認してください。",
            };
            return currentStatus;
        }
    }

    /// <inheritdoc />
    public async Task<GmailOperationResult> AuthenticateAsync(
        bool forceReauthentication,
        CancellationToken cancellationToken)
    {
        if (!await authenticationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return new GmailOperationResult { Message = "Gmail認証はすでに実行中です。" };
        }

        try
        {
            GoogleOAuthClientConfigurationStatus configuration =
                await configurationService.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (!configuration.IsValid)
            {
                return new GmailOperationResult { Message = configuration.Message };
            }

            currentStatus = new GmailAuthenticationStatus
            {
                State = GmailAuthenticationState.Authenticating,
                HasClientConfiguration = true,
            };
            LogAuthenticationStarted(logger, null);
            using CancellationTokenSource timeout = new(AuthenticationTimeout);
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                lifetimeCancellation.Token,
                timeout.Token);
            ClientSecrets secrets = await configurationService.LoadAsync(linked.Token).ConfigureAwait(false);
            UserCredential credential;
            if (forceReauthentication && currentCredential is not null)
            {
                credential = await oauthFlow.ReauthorizeAsync(currentCredential, linked.Token).ConfigureAwait(false);
            }
            else
            {
                if (forceReauthentication)
                {
                    await credentialStore.ClearAsync().ConfigureAwait(false);
                    currentCredential = null;
                }

                credential = await oauthFlow.AuthorizeAsync(secrets, linked.Token).ConfigureAwait(false);
            }
            if (string.IsNullOrWhiteSpace(credential.Token.RefreshToken))
            {
                MarkReauthenticationRequired("リフレッシュトークンを取得できませんでした。Google側の接続を解除して再認証してください。");
                return new GmailOperationResult { Message = currentStatus.LastErrorSummary! };
            }

            string email = await oauthFlow.GetEmailAddressAsync(credential, linked.Token).ConfigureAwait(false);
            DateTimeOffset now = timeProvider.GetUtcNow();
            GmailCredentialMetadata metadata = new()
            {
                EmailAddress = email,
                LastAuthenticatedAtUtc = now,
            };
            await credentialStore.SaveMetadataAsync(metadata, linked.Token).ConfigureAwait(false);
            currentCredential = credential;
            currentStatus = new GmailAuthenticationStatus
            {
                State = GmailAuthenticationState.Authenticated,
                HasClientConfiguration = true,
                AuthenticatedEmailAddress = email,
                LastAuthenticatedAtUtc = now,
            };
            LogAuthenticationSucceeded(logger, MaskEmail(email), null);
            return new GmailOperationResult { Succeeded = true, Message = "Googleアカウントの認証に成功しました。" };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || lifetimeCancellation.IsCancellationRequested)
        {
            SetUnauthenticated();
            LogAuthenticationCanceled(logger, null);
            return new GmailOperationResult { WasCanceled = true, Message = "Googleアカウントの認証をキャンセルしました。" };
        }
        catch (OperationCanceledException)
        {
            return AuthenticationFailure("ローカルリダイレクトの待機がタイムアウトしました。ブラウザーを閉じた場合は再度認証してください。");
        }
        catch (HttpListenerException)
        {
            return AuthenticationFailure("ローカルの認証待受ポートを使用できません。ほかのアプリやファイアウォールを確認してください。");
        }
        catch (TokenResponseException exception)
        {
            string error = exception.Error?.Error ?? string.Empty;
            return AuthenticationFailure(error == "access_denied"
                ? "必要な権限が許可されなかったため認証できませんでした。"
                : "Google OAuthサーバーが認証を完了できませんでした。");
        }
        catch (HttpRequestException)
        {
            return AuthenticationFailure("ネットワークへ接続できないため認証できませんでした。");
        }
        catch (Exception exception) when (exception is GmailCredentialStoreException
            or GoogleOAuthClientConfigurationException or IOException or InvalidOperationException)
        {
            string safeSummary = exception switch
            {
                GmailCredentialStoreException => "認証情報を安全に保存または読み込みできませんでした。",
                GoogleOAuthClientConfigurationException configurationException => configurationException.Message,
                IOException => "認証情報ファイルを読み書きできませんでした。",
                _ => "Googleアカウントの認証を完了できませんでした。",
            };
            return AuthenticationFailure(safeSummary);
        }
        finally
        {
            authenticationGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<UserCredential> GetUsableCredentialAsync(CancellationToken cancellationToken)
    {
        GmailAuthenticationStatus status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (currentCredential is null || status.State is GmailAuthenticationState.NotConfigured
            or GmailAuthenticationState.Unauthenticated or GmailAuthenticationState.ReauthenticationRequired
            or GmailAuthenticationState.Error)
        {
            throw new InvalidOperationException("Gmailを使用するにはGoogleアカウントの認証が必要です。");
        }

        if (!currentCredential.Token.IsStale)
        {
            return currentCredential;
        }

        await refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!currentCredential.Token.IsStale)
            {
                return currentCredential;
            }

            bool refreshed = await oauthFlow.RefreshTokenAsync(currentCredential, cancellationToken).ConfigureAwait(false);
            if (!refreshed)
            {
                MarkReauthenticationRequired("アクセストークンを更新できませんでした。再認証してください。");
                throw new InvalidOperationException(currentStatus.LastErrorSummary);
            }

            DateTimeOffset now = timeProvider.GetUtcNow();
            GmailCredentialMetadata? metadata = await credentialStore.LoadMetadataAsync(cancellationToken).ConfigureAwait(false);
            if (metadata is not null)
            {
                await credentialStore.SaveMetadataAsync(
                    metadata with { LastTokenRefreshedAtUtc = now },
                    cancellationToken).ConfigureAwait(false);
            }

            currentStatus = currentStatus with
            {
                State = GmailAuthenticationState.Authenticated,
                LastTokenRefreshedAtUtc = now,
                LastErrorSummary = null,
            };
            LogTokenRefreshed(logger, null);
            return currentCredential;
        }
        catch (TokenResponseException exception) when (string.Equals(exception.Error?.Error, "invalid_grant", StringComparison.Ordinal))
        {
            MarkReauthenticationRequired("Google側で認証が失効しました。再認証してください。");
            throw new InvalidOperationException(currentStatus.LastErrorSummary, exception);
        }
        finally
        {
            refreshGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<GmailOperationResult> DisconnectAsync(CancellationToken cancellationToken)
    {
        await authenticationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool remoteRevoked = false;
        bool localRemoved = false;
        try
        {
            try
            {
                if (currentCredential is null)
                {
                    GoogleOAuthClientConfigurationStatus configuration =
                        await configurationService.GetStatusAsync(cancellationToken).ConfigureAwait(false);
                    if (configuration.IsValid && credentialStore.Exists)
                    {
                        currentCredential = await oauthFlow.LoadCredentialAsync(
                            await configurationService.LoadAsync(cancellationToken).ConfigureAwait(false),
                            cancellationToken).ConfigureAwait(false);
                    }
                }

                if (currentCredential is not null)
                {
                    remoteRevoked = await oauthFlow.RevokeTokenAsync(currentCredential, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is TokenResponseException or HttpRequestException
                or GmailCredentialStoreException or GoogleOAuthClientConfigurationException
                or IOException or InvalidOperationException)
            {
                remoteRevoked = false;
            }

            try
            {
                await credentialStore.ClearAsync().ConfigureAwait(false);
                localRemoved = true;
                currentCredential = null;
                SetUnauthenticated();
            }
            catch (GmailCredentialStoreException)
            {
                currentStatus = currentStatus with
                {
                    State = GmailAuthenticationState.Error,
                    LastErrorSummary = "ローカル認証情報を削除できませんでした。",
                };
            }

            LogDisconnected(logger, remoteRevoked, localRemoved, null);
            return new GmailOperationResult
            {
                Succeeded = localRemoved,
                LocalCredentialsRemoved = localRemoved,
                RemoteRevocationSucceeded = remoteRevoked,
                Message = localRemoved
                    ? remoteRevoked
                        ? "Google側の権限を失効し、ローカル認証情報を削除しました。"
                        : "Google側の失効は確認できませんでしたが、ローカル認証情報を削除しました。"
                    : "ローカル認証情報を削除できませんでした。ログを確認してください。",
            };
        }
        finally
        {
            authenticationGate.Release();
        }
    }

    /// <inheritdoc />
    public void MarkReauthenticationRequired(string safeSummary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(safeSummary);
        currentStatus = currentStatus with
        {
            State = GmailAuthenticationState.ReauthenticationRequired,
            HasClientConfiguration = true,
            LastErrorSummary = safeSummary,
        };
        LogReauthenticationRequired(logger, safeSummary, null);
    }

    /// <summary>未認証状態へ移行します。</summary>
    private GmailAuthenticationStatus SetUnauthenticated()
    {
        currentStatus = new GmailAuthenticationStatus
        {
            State = GmailAuthenticationState.Unauthenticated,
            HasClientConfiguration = true,
        };
        return currentStatus;
    }

    /// <summary>安全な概要だけを状態とログへ反映します。</summary>
    private GmailOperationResult AuthenticationFailure(string safeSummary)
    {
        currentStatus = new GmailAuthenticationStatus
        {
            State = GmailAuthenticationState.Error,
            HasClientConfiguration = true,
            LastErrorSummary = safeSummary,
        };
        LogAuthenticationFailed(logger, safeSummary, null);
        return new GmailOperationResult { Message = safeSummary };
    }

    /// <summary>ログ用にメールアドレスのローカル部を部分マスクします。</summary>
    internal static string MaskEmail(string email)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        int at = email.IndexOf('@');
        if (at <= 0)
        {
            return "***";
        }

        string local = email[..at];
        string visible = local[..Math.Min(2, local.Length)];
        return visible + "***" + email[at..];
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        lifetimeCancellation.Cancel();
        lifetimeCancellation.Dispose();
        authenticationGate.Dispose();
        refreshGate.Dispose();
    }
}
