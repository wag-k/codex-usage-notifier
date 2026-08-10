namespace CodexUsageNotifier.Infrastructure.Startup;

/// <summary>
/// 自動起動へ登録する実行ファイル情報を提供します。
/// </summary>
internal interface IExecutablePathProvider
{
    /// <summary>現在プロセスの実行ファイル情報を取得します。</summary>
    /// <returns>絶対パスと開発実行判定です。</returns>
    ExecutablePathInfo GetExecutablePath();
}

/// <summary>
/// 自動起動へ登録する実行ファイル情報を表します。
/// </summary>
internal sealed record ExecutablePathInfo
{
    /// <summary>実行ファイルの絶対パスを取得または設定します。</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>永続的な自動起動へ登録できるかどうかを取得または設定します。</summary>
    public bool CanRegister { get; init; }

    /// <summary>登録できない場合に表示する安全な理由を取得または設定します。</summary>
    public string? UnsupportedReason { get; init; }
}

/// <summary>
/// Environment.ProcessPathから安全な自動起動対象を判定します。
/// </summary>
internal sealed class EnvironmentExecutablePathProvider : IExecutablePathProvider
{
    /// <inheritdoc />
    public ExecutablePathInfo GetExecutablePath()
    {
        string? processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return Unsupported("実行ファイルのパスを取得できないため自動起動を登録できません。");
        }

        string fullPath = Path.GetFullPath(processPath);
        string fileName = Path.GetFileName(fullPath);
        bool isDotnetHost = string.Equals(fileName, "dotnet.exe", StringComparison.OrdinalIgnoreCase);
        bool isBuildOutput = IsUnpublishedBuildOutput(fullPath);
        if (isDotnetHost || isBuildOutput)
        {
            return Unsupported(
                "開発実行中のため自動起動を登録できません。配布用CodexUsageNotifier.exeから設定してください。",
                fullPath);
        }

        return new ExecutablePathInfo
        {
            Path = fullPath,
            CanRegister = true,
        };
    }

    /// <summary>通常のbin出力で、publish配下ではないパスか判定します。</summary>
    private static bool IsUnpublishedBuildOutput(string fullPath)
    {
        string normalized = fullPath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        string separator = Path.DirectorySeparatorChar.ToString();
        bool isBinOutput = normalized.Contains($"{separator}bin{separator}Debug{separator}", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains($"{separator}bin{separator}Release{separator}", StringComparison.OrdinalIgnoreCase);
        bool isPublished = normalized.Contains($"{separator}publish{separator}", StringComparison.OrdinalIgnoreCase);
        return isBinOutput && !isPublished;
    }

    /// <summary>登録不可の実行ファイル情報を生成します。</summary>
    private static ExecutablePathInfo Unsupported(string reason, string path = "")
    {
        return new ExecutablePathInfo
        {
            Path = path,
            CanRegister = false,
            UnsupportedReason = reason,
        };
    }
}
