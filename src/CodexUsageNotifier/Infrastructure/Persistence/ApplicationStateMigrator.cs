using CodexUsageNotifier.Domain.Models;

namespace CodexUsageNotifier.Infrastructure.Persistence;

/// <summary>
/// 旧ApplicationStateを現在スキーマへ移行する契約です。
/// </summary>
public interface IApplicationStateMigrator
{
    /// <summary>
    /// 読み込んだ状態を指定元バージョンから現在バージョンへ移行します。
    /// </summary>
    /// <param name="state">旧スキーマとして読み込んだ状態です。</param>
    /// <param name="sourceVersion">ファイルから明示的に取得した元バージョンです。</param>
    /// <returns>現在スキーマへ段階移行した状態です。</returns>
    ApplicationState Migrate(ApplicationState state, int sourceVersion);
}

/// <summary>
/// サポート済みの旧ApplicationStateを段階的に現在スキーマへ移行します。
/// </summary>
public sealed class ApplicationStateMigrator : IApplicationStateMigrator
{
    /// <summary>
    /// 読み込んだ状態を指定元バージョンから現在バージョンへ移行します。
    /// </summary>
    /// <param name="state">旧スキーマとして読み込んだ状態です。</param>
    /// <param name="sourceVersion">ファイルから明示的に取得した元バージョンです。</param>
    /// <returns>現在スキーマへ段階移行した状態です。</returns>
    public ApplicationState Migrate(ApplicationState state, int sourceVersion)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (sourceVersion <= 0 || sourceVersion > ApplicationState.CurrentSchemaVersion)
        {
            throw new NotSupportedException($"状態スキーマバージョン{sourceVersion}からの移行はサポートされていません。");
        }

        ApplicationState migrated = state;
        int version = sourceVersion;
        while (version < ApplicationState.CurrentSchemaVersion)
        {
            migrated = version switch
            {
                1 => MigrateVersion1To2(migrated),
                2 => MigrateVersion2To3(migrated),
                3 => MigrateVersion3To4(migrated),
                _ => throw new NotSupportedException($"状態スキーマバージョン{version}からの移行はサポートされていません。"),
            };
            version = migrated.SchemaVersion;
        }

        return migrated;
    }

    /// <summary>Version 1の通知・回復一覧を正規化し、Version 2へ移行します。</summary>
    private static ApplicationState MigrateVersion1To2(ApplicationState state)
    {
        return state with
        {
            SchemaVersion = 2,
            RateLimitNotificationStates = state.RateLimitNotificationStates ?? [],
            RateLimitRecoveryStates = state.RateLimitRecoveryStates ?? [],
        };
    }

    /// <summary>Version 3で追加したGmail配送期間状態を安全な未観測値で初期化します。</summary>
    private static ApplicationState MigrateVersion2To3(ApplicationState state)
    {
        return state with
        {
            SchemaVersion = 3,
            GmailDeliveryEnabledSinceUtc = null,
            GmailDeliveryEnabledLastObserved = false,
            GmailAuthenticationWasUsable = false,
        };
    }

    /// <summary>Version 4で追加した最終保守時刻を未実行として初期化します。</summary>
    private static ApplicationState MigrateVersion3To4(ApplicationState state)
    {
        return state with
        {
            SchemaVersion = 4,
            LastMaintenanceAtUtc = null,
        };
    }
}
