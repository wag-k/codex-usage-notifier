using System.Windows;
using CodexUsageNotifier.Application.Abstractions;
using CodexUsageNotifier.Application.State;
using CodexUsageNotifier.Application.Monitoring;
using CodexUsageNotifier.Application.Notifications;
using CodexUsageNotifier.Domain.Models;
using CodexUsageNotifier.Infrastructure.Logging;
using CodexUsageNotifier.Infrastructure.Persistence;
using CodexUsageNotifier.Infrastructure.Codex;
using CodexUsageNotifier.Infrastructure.WindowsNotifications;
using CodexUsageNotifier.Infrastructure.Gmail;
using CodexUsageNotifier.Infrastructure.Startup;
using CodexUsageNotifier.Application.Gmail;
using CodexUsageNotifier.Application.Startup;
using CodexUsageNotifier.Presentation.Tray;
using CodexUsageNotifier.Presentation.ViewModels;
using CodexUsageNotifier.Presentation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CodexUsageNotifier;

/// <summary>
/// アプリケーションの起動、依存関係の構築、および終了処理を管理します。
/// </summary>
public partial class App : System.Windows.Application
{
    private static readonly Action<ILogger, Exception?> LogApplicationStopping =
        LoggerMessage.Define(LogLevel.Information, new EventId(1002, "ApplicationStopping"), "アプリケーションを終了します。");

    private static readonly Action<ILogger, Exception?> LogApplicationStarting =
        LoggerMessage.Define(LogLevel.Information, new EventId(1000, "ApplicationStarting"), "アプリケーションを起動します。");

    private static readonly Action<ILogger, Exception?> LogInitializationCompleted =
        LoggerMessage.Define(LogLevel.Information, new EventId(1001, "InitializationCompleted"), "設定と状態の読み込みが完了しました。");

    private static readonly Action<ILogger, Exception?> LogApplicationStopFailed =
        LoggerMessage.Define(LogLevel.Error, new EventId(1003, "ApplicationStopFailed"), "アプリケーションの終了処理中にエラーが発生しました。");

    private static readonly Action<ILogger, string, Exception?> LogAutoStartSynchronizationFailed =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(1004, "AutoStartSynchronizationFailed"), "Windows自動起動の起動時同期に失敗しました。Reason={Reason}");

    private ServiceProvider? serviceProvider;
    private ApplicationInstanceGuard? instanceGuard;

    /// <summary>
    /// DIコンテナを構築し、永続化基盤とタスクトレイを初期化します。
    /// </summary>
    /// <param name="e">起動時の引数です。</param>
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (!ApplicationInstanceGuard.TryAcquireForCurrentUser(out ApplicationInstanceGuard? acquiredGuard))
        {
            System.Windows.MessageBox.Show(
                "Codex Usage Notifierはすでに起動しています。タスクトレイを確認してください。",
                "Codex Usage Notifier",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        instanceGuard = acquiredGuard;

        try
        {
            AppDataPaths paths = AppDataPaths.CreateDefault();
            paths.EnsureDirectories();
            serviceProvider = BuildServiceProvider(paths);
            serviceProvider.GetRequiredService<ApplicationLifetime>()
                .ConfigureExitAction(ShutdownFromTrayAsync);

            await InitializePersistenceAsync(serviceProvider, CancellationToken.None);

            MainWindow mainWindow = serviceProvider.GetRequiredService<MainWindow>();
            mainWindow.Show();
            serviceProvider.GetRequiredService<TrayIconService>().Initialize();
            serviceProvider.GetRequiredService<UsageMonitor>().Start();
        }
        catch (UnsupportedFutureStateVersionException exception)
        {
            System.Windows.MessageBox.Show(
                exception.Message,
                "Codex Usage Notifier",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
            Shutdown(-1);
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                $"アプリケーションを起動できませんでした。{Environment.NewLine}{exception.Message}",
                "Codex Usage Notifier",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    /// <summary>
    /// 起動時に使用するサービスを登録してDIコンテナを生成します。
    /// </summary>
    /// <param name="paths">アプリケーションデータの保存先です。</param>
    /// <returns>構築済みのサービスプロバイダーです。</returns>
    private static ServiceProvider BuildServiceProvider(AppDataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        ServiceCollection services = new();
        DailyFileLoggerProvider fileLoggerProvider = new(paths.LogDirectory);
        services.AddSingleton(paths);
        services.AddSingleton<IAppDataPaths>(paths);
        services.AddSingleton(fileLoggerProvider);
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(fileLoggerProvider);
        });

        services.AddSingleton<ISettingsRepository, JsonSettingsRepository>();
        services.AddSingleton<IAutoStartManager, WindowsAutoStartManager>();
        services.AddSingleton<IApplicationStateMigrator, ApplicationStateMigrator>();
        services.AddSingleton<IApplicationStateRepository, JsonApplicationStateRepository>();
        services.AddSingleton<IUsageHistoryRepository, JsonUsageHistoryRepository>();
        services.AddSingleton<IGoogleOAuthClientConfigurationService, GoogleOAuthClientConfigurationService>();
        services.AddSingleton<IUserDataProtector, WindowsUserDataProtector>();
        services.AddSingleton<IGmailCredentialStore, DpapiGmailCredentialStore>();
        services.AddSingleton<IGoogleOAuthFlow, GoogleOAuthFlow>();
        services.AddSingleton<GmailAuthenticationService>();
        services.AddSingleton<IGmailAuthenticationService>(provider => provider.GetRequiredService<GmailAuthenticationService>());
        services.AddSingleton<IGmailAuthenticationStatusProvider>(provider => provider.GetRequiredService<GmailAuthenticationService>());
        services.AddSingleton<IGmailMimeMessageFactory, GmailMimeMessageFactory>();
        services.AddSingleton<IGoogleGmailMessageGateway, GoogleGmailMessageGateway>();
        services.AddSingleton<IGmailApiClient, GmailApiClient>();
        services.AddSingleton<IGmailTestMailSender, GmailTestMailSender>();
        services.AddSingleton<IGmailNotificationSender, GmailNotificationSender>();
        services.AddSingleton<ApplicationStateStore>();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<IPowerEventSource, SystemPowerEventSource>();
        services.AddSingleton<TrayIconHost>();
        services.AddSingleton<IWindowsNotificationSender, WindowsBalloonNotificationSender>();
        services.AddSingleton<RateLimitNotificationProcessor>();
        services.AddSingleton<TestNotificationService>();
        services.AddSingleton(new CodexAppServerOptions());
        services.AddSingleton<ICodexAppServerProcessFactory, CodexAppServerProcessFactory>();
        services.AddSingleton<CodexAppServerClient>();
        services.AddSingleton<ICodexRateLimitClient>(provider => provider.GetRequiredService<CodexAppServerClient>());
        services.AddSingleton<ApplicationLifetime>();
        services.AddSingleton<StatusViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<IUsageStatusSink>(provider => provider.GetRequiredService<StatusViewModel>());
        services.AddSingleton<UsageMonitor>();
        services.AddSingleton<ISettingsChangeSink>(provider => provider.GetRequiredService<UsageMonitor>());
        services.AddSingleton<MainWindow>();
        services.AddSingleton<SettingsWindow>();
        services.AddSingleton<TrayIconService>();
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// 永続化済みの設定と状態を読み込み、状態画面へ反映します。
    /// </summary>
    /// <param name="provider">登録済みサービスの取得元です。</param>
    /// <param name="cancellationToken">処理のキャンセル通知です。</param>
    private static async Task InitializePersistenceAsync(
        IServiceProvider provider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);

        ILogger<App> logger = provider.GetRequiredService<ILogger<App>>();
        LogApplicationStarting(logger, null);
        AppSettings settings = await provider.GetRequiredService<ISettingsRepository>()
            .LoadAsync(cancellationToken);
        AutoStartOperationResult autoStartResult = await provider.GetRequiredService<IAutoStartManager>()
            .SynchronizeAsync(settings.AutoStartEnabled, cancellationToken);
        if (!autoStartResult.Succeeded)
        {
            LogAutoStartSynchronizationFailed(logger, autoStartResult.Status.Message, null);
        }

        provider.GetRequiredService<CodexAppServerOptions>().ExecutablePath = settings.CodexExecutablePath;
        ApplyLogLevel(settings, provider.GetRequiredService<DailyFileLoggerProvider>());
        ApplicationState state = await provider.GetRequiredService<ApplicationStateStore>()
            .LoadAsync(cancellationToken);
        if (state.GmailProductionDeliveryStartedAtUtc is null)
        {
            DateTimeOffset startedAtUtc = provider.GetRequiredService<TimeProvider>().GetUtcNow();
            state = await provider.GetRequiredService<ApplicationStateStore>().UpdateAsync(
                current => current with
                {
                    GmailProductionDeliveryStartedAtUtc =
                        current.GmailProductionDeliveryStartedAtUtc ?? startedAtUtc,
                },
                cancellationToken);
        }

        if (!state.InitialSetupCompleted)
        {
            state = await provider.GetRequiredService<ApplicationStateStore>().UpdateAsync(
                current => current with { InitialSetupCompleted = true },
                cancellationToken);
        }

        StatusViewModel statusViewModel = provider.GetRequiredService<StatusViewModel>();
        statusViewModel.Initialize(settings, state);
        await statusViewModel.RefreshGmailAuthenticationStatusAsync(cancellationToken);
        LogInitializationCompleted(logger, null);
    }

    /// <summary>
    /// 保存済み設定のログレベルをファイルロガーへ反映します。
    /// </summary>
    /// <param name="settings">読み込んだアプリケーション設定です。</param>
    /// <param name="provider">ログレベルを変更するファイルロガーです。</param>
    private static void ApplyLogLevel(AppSettings settings, DailyFileLoggerProvider provider)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(provider);

        if (!Enum.TryParse(settings.MinimumLogLevel, ignoreCase: true, out LogLevel minimumLevel))
        {
            throw new InvalidOperationException("設定されたログレベルを解釈できません。");
        }

        provider.MinimumLevel = minimumLevel;
    }

    /// <summary>
    /// トレイアイコンを先に隠し、監視と子プロセスを非同期解放してからWPFを終了します。
    /// </summary>
    /// <returns>終了準備の完了を表す処理です。</returns>
    private async Task ShutdownFromTrayAsync()
    {
        ServiceProvider? provider = serviceProvider;
        ILogger<App>? logger = provider?.GetService<ILogger<App>>();
        if (logger is not null)
        {
            LogApplicationStopping(logger, null);
        }

        try
        {
            provider?.GetService<TrayIconHost>()?.Dispose();
            if (provider is not null)
            {
                await provider.DisposeAsync();
            }
        }
        catch (Exception exception)
        {
            if (logger is not null)
            {
                LogApplicationStopFailed(logger, exception);
            }
        }
        finally
        {
            if (ReferenceEquals(serviceProvider, provider))
            {
                serviceProvider = null;
            }

            instanceGuard?.Dispose();
            instanceGuard = null;
            Shutdown();
        }
    }

    /// <summary>
    /// DIコンテナと、その配下の破棄可能なサービスを解放します。
    /// </summary>
    /// <param name="e">終了時の引数です。</param>
    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            ILogger<App>? logger = serviceProvider?.GetService<ILogger<App>>();
            if (logger is not null)
            {
                LogApplicationStopping(logger, null);
            }

            serviceProvider?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        finally
        {
            instanceGuard?.Dispose();
            instanceGuard = null;
            base.OnExit(e);
        }
    }
}
