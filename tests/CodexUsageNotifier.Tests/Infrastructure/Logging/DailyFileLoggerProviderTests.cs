using CodexUsageNotifier.Infrastructure.Logging;
using Microsoft.Extensions.Logging;

namespace CodexUsageNotifier.Tests.Infrastructure.Logging;

/// <summary>
/// 日別ファイルロガーのログレベル制御を確認します。
/// </summary>
[TestClass]
public sealed class DailyFileLoggerProviderTests
{
    /// <summary>
    /// 設定した最小レベル未満のログがファイルへ出力されないことを確認します。
    /// </summary>
    [TestMethod]
    public void Log_MinimumLevelIsWarning_SkipsInformation()
    {
        using TemporaryDirectory temporaryDirectory = new();
        using DailyFileLoggerProvider provider = new(temporaryDirectory.Path)
        {
            MinimumLevel = LogLevel.Warning,
        };
        ILogger logger = provider.CreateLogger("TestCategory");

        logger.LogInformation("出力しない情報ログ");
        logger.LogWarning("出力する警告ログ");

        string logPath = Directory.EnumerateFiles(temporaryDirectory.Path, "*.log").Single();
        string contents = File.ReadAllText(logPath);
        Assert.IsFalse(contents.Contains("出力しない情報ログ", StringComparison.Ordinal));
        StringAssert.Contains(contents, "出力する警告ログ");
    }
}
