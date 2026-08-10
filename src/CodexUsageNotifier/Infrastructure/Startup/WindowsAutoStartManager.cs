using CodexUsageNotifier.Application.Startup;
using Microsoft.Extensions.Logging;

namespace CodexUsageNotifier.Infrastructure.Startup;

/// <summary>
/// 現在ユーザーのRunキーを使用してWindows自動起動を管理します。
/// </summary>
public sealed partial class WindowsAutoStartManager : IAutoStartManager
{
    private readonly IAutoStartRegistry registry;
    private readonly IExecutablePathProvider executablePathProvider;
    private readonly ILogger<WindowsAutoStartManager> logger;

    /// <summary>
    /// 本番のCurrentUser Registryと実行ファイル情報を使用して初期化します。
    /// </summary>
    /// <param name="logger">自動起動の変更結果を記録するロガーです。</param>
    public WindowsAutoStartManager(ILogger<WindowsAutoStartManager> logger)
        : this(new CurrentUserRunRegistry(), new EnvironmentExecutablePathProvider(), logger)
    {
    }

    /// <summary>
    /// テスト可能なRegistryと実行ファイル情報を指定して初期化します。
    /// </summary>
    /// <param name="registry">CurrentUser Runキーの操作先です。</param>
    /// <param name="executablePathProvider">登録対象パスの提供元です。</param>
    /// <param name="logger">処理結果の記録先です。</param>
    internal WindowsAutoStartManager(
        IAutoStartRegistry registry,
        IExecutablePathProvider executablePathProvider,
        ILogger<WindowsAutoStartManager> logger)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(executablePathProvider);
        ArgumentNullException.ThrowIfNull(logger);
        this.registry = registry;
        this.executablePathProvider = executablePathProvider;
        this.logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> IsEnabledAsync(CancellationToken cancellationToken)
    {
        AutoStartStatus status = await GetStatusAsync(expectedEnabled: true, cancellationToken);
        return status.State == AutoStartRegistrationState.Registered;
    }

    /// <inheritdoc />
    public async Task<AutoStartStatus> GetStatusAsync(
        bool expectedEnabled,
        CancellationToken cancellationToken)
    {
        try
        {
            ExecutablePathInfo executable = executablePathProvider.GetExecutablePath();
            string? command = await Task.Run(registry.ReadCommand, cancellationToken);
            bool hasRegistration = !string.IsNullOrWhiteSpace(command);
            bool isCurrent = executable.CanRegister
                && string.Equals(command, CreateCommand(executable.Path), StringComparison.OrdinalIgnoreCase);

            if (expectedEnabled && !executable.CanRegister)
            {
                return new AutoStartStatus
                {
                    State = AutoStartRegistrationState.Unsupported,
                    HasRegistration = hasRegistration,
                    IsCurrentExecutableRegistered = false,
                    Message = executable.UnsupportedReason ?? "現在の実行方法では自動起動を登録できません。",
                };
            }

            bool matches = expectedEnabled ? isCurrent : !hasRegistration;
            return new AutoStartStatus
            {
                State = matches
                    ? expectedEnabled ? AutoStartRegistrationState.Registered : AutoStartRegistrationState.NotRegistered
                    : AutoStartRegistrationState.Mismatch,
                HasRegistration = hasRegistration,
                IsCurrentExecutableRegistered = isCurrent,
                Message = matches
                    ? expectedEnabled ? "登録済み" : "未登録"
                    : "設定とWindowsの自動起動状態が一致していません。",
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogStatusFailed(logger, exception);
            return ErrorStatus();
        }
    }

    /// <inheritdoc />
    public async Task<AutoStartOperationResult> EnableAsync(CancellationToken cancellationToken)
    {
        ExecutablePathInfo executable = executablePathProvider.GetExecutablePath();
        if (!executable.CanRegister)
        {
            return new AutoStartOperationResult
            {
                Succeeded = false,
                Status = new AutoStartStatus
                {
                    State = AutoStartRegistrationState.Unsupported,
                    Message = executable.UnsupportedReason ?? "現在の実行方法では自動起動を登録できません。",
                },
            };
        }

        try
        {
            string command = CreateCommand(executable.Path);
            await Task.Run(() => registry.WriteCommand(command), cancellationToken);
            LogEnabled(logger, null);
            return new AutoStartOperationResult
            {
                Succeeded = true,
                Status = new AutoStartStatus
                {
                    State = AutoStartRegistrationState.Registered,
                    HasRegistration = true,
                    IsCurrentExecutableRegistered = true,
                    Message = "登録済み",
                },
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogEnableFailed(logger, exception);
            return new AutoStartOperationResult { Status = ErrorStatus() };
        }
    }

    /// <inheritdoc />
    public async Task<AutoStartOperationResult> DisableAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Run(registry.DeleteCommand, cancellationToken);
            LogDisabled(logger, null);
            return new AutoStartOperationResult
            {
                Succeeded = true,
                Status = new AutoStartStatus
                {
                    State = AutoStartRegistrationState.NotRegistered,
                    Message = "未登録",
                },
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogDisableFailed(logger, exception);
            return new AutoStartOperationResult { Status = ErrorStatus() };
        }
    }

    /// <inheritdoc />
    public Task<AutoStartOperationResult> SynchronizeAsync(
        bool enabled,
        CancellationToken cancellationToken)
    {
        return enabled
            ? EnableAsync(cancellationToken)
            : DisableAsync(cancellationToken);
    }

    /// <summary>実行ファイルを引数なしの引用符付きコマンドへ変換します。</summary>
    /// <param name="executablePath">登録する実行ファイルの絶対パスです。</param>
    /// <returns>Runキーへ登録する安全なコマンドです。</returns>
    internal static string CreateCommand(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        string fullPath = Path.GetFullPath(executablePath);
        return $"\"{fullPath}\"";
    }

    /// <summary>Registry確認エラーの安全な表示状態を生成します。</summary>
    private static AutoStartStatus ErrorStatus()
    {
        return new AutoStartStatus
        {
            State = AutoStartRegistrationState.Error,
            Message = "Windowsの自動起動状態を確認できませんでした。ログを確認してください。",
        };
    }

    [LoggerMessage(5100, LogLevel.Information, "Windows自動起動を現在ユーザーへ登録しました。")]
    private static partial void LogEnabled(ILogger logger, Exception? exception);

    [LoggerMessage(5101, LogLevel.Information, "Windows自動起動の現在ユーザー登録を削除しました。")]
    private static partial void LogDisabled(ILogger logger, Exception? exception);

    [LoggerMessage(5102, LogLevel.Error, "Windows自動起動を登録できませんでした。")]
    private static partial void LogEnableFailed(ILogger logger, Exception exception);

    [LoggerMessage(5103, LogLevel.Error, "Windows自動起動の登録を削除できませんでした。")]
    private static partial void LogDisableFailed(ILogger logger, Exception exception);

    [LoggerMessage(5104, LogLevel.Warning, "Windows自動起動の登録状態を確認できませんでした。")]
    private static partial void LogStatusFailed(ILogger logger, Exception exception);
}
