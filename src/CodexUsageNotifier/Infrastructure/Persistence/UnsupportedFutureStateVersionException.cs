namespace CodexUsageNotifier.Infrastructure.Persistence;

/// <summary>
/// 現在のアプリケーションより新しい状態スキーマを検出したことを表します。
/// </summary>
public sealed class UnsupportedFutureStateVersionException : Exception
{
    /// <summary>
    /// 検出したバージョンと現在対応するバージョンを受け取ります。
    /// </summary>
    /// <param name="storedVersion">保存データのスキーマバージョンです。</param>
    /// <param name="supportedVersion">現在対応するスキーマバージョンです。</param>
    public UnsupportedFutureStateVersionException(int storedVersion, int supportedVersion)
        : base(CreateUserMessage(storedVersion, supportedVersion))
    {
        StoredVersion = storedVersion;
        SupportedVersion = supportedVersion;
    }

    /// <summary>保存データのスキーマバージョンを取得します。</summary>
    public int StoredVersion { get; }

    /// <summary>現在のアプリケーションが対応するスキーマバージョンを取得します。</summary>
    public int SupportedVersion { get; }

    /// <summary>
    /// 機密情報を含まないユーザー向けメッセージを生成します。
    /// </summary>
    /// <param name="storedVersion">保存データのスキーマバージョンです。</param>
    /// <param name="supportedVersion">現在対応するスキーマバージョンです。</param>
    /// <returns>起動中止の理由と対処を示すメッセージです。</returns>
    public static string CreateUserMessage(int storedVersion, int supportedVersion)
    {
        return string.Join(
            Environment.NewLine,
            "保存データは、このアプリより新しいバージョンで作成されています。",
            string.Empty,
            $"保存データのバージョン: {storedVersion}",
            $"このアプリが対応するバージョン: {supportedVersion}",
            string.Empty,
            "古いバージョンのアプリで保存データを変更すると破損する可能性があるため、起動を中止しました。",
            "新しいバージョンのCodex Usage Notifierを使用してください。");
    }
}
