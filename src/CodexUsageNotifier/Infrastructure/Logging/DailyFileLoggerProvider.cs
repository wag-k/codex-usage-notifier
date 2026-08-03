using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace CodexUsageNotifier.Infrastructure.Logging;

/// <summary>
/// 日付ごとのUTF-8テキストファイルへログを書き込むプロバイダーです。
/// </summary>
public sealed class DailyFileLoggerProvider : ILoggerProvider
{
    private readonly string logDirectory;
    private readonly object writeLock = new();
    private int minimumLevel = (int)LogLevel.Information;
    private bool disposed;

    /// <summary>
    /// ログの保存先を指定してプロバイダーを初期化します。
    /// </summary>
    /// <param name="logDirectory">ログファイルを保存するディレクトリです。</param>
    public DailyFileLoggerProvider(string logDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        this.logDirectory = Path.GetFullPath(logDirectory);
    }

    /// <summary>
    /// ファイルへ出力する最小ログレベルを取得または設定します。
    /// </summary>
    public LogLevel MinimumLevel
    {
        get => (LogLevel)Volatile.Read(ref minimumLevel);
        set => Volatile.Write(ref minimumLevel, (int)value);
    }

    /// <summary>
    /// 指定カテゴリのファイルロガーを生成します。
    /// </summary>
    /// <param name="categoryName">ログカテゴリ名です。</param>
    /// <returns>ファイルへ出力するロガーです。</returns>
    public ILogger CreateLogger(string categoryName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(categoryName);
        ObjectDisposedException.ThrowIf(disposed, this);
        return new DailyFileLogger(categoryName, IsEnabled, WriteEntry);
    }

    /// <summary>
    /// プロバイダーを破棄済みとして、新しいロガーの生成を停止します。
    /// </summary>
    public void Dispose()
    {
        disposed = true;
    }

    /// <summary>
    /// 1件のログを当日のファイルへ追記します。
    /// </summary>
    /// <param name="entry">追記するログ文字列です。</param>
    private void WriteEntry(string entry)
    {
        try
        {
            lock (writeLock)
            {
                Directory.CreateDirectory(logDirectory);
                string fileName = $"codex-usage-notifier-{DateTimeOffset.Now:yyyyMMdd}.log";
                File.AppendAllText(Path.Combine(logDirectory, fileName), entry, Encoding.UTF8);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"ログファイルへ書き込めませんでした: {exception.Message}");
        }
    }

    /// <summary>
    /// 指定されたログレベルが現在の出力対象かどうかを判定します。
    /// </summary>
    /// <param name="logLevel">判定するログレベルです。</param>
    /// <returns>出力対象ならtrueです。</returns>
    private bool IsEnabled(LogLevel logLevel)
    {
        return logLevel != LogLevel.None && logLevel >= MinimumLevel;
    }

    /// <summary>
    /// 1カテゴリのログを整形してプロバイダーへ渡します。
    /// </summary>
    private sealed class DailyFileLogger : ILogger
    {
        private readonly string categoryName;
        private readonly Func<LogLevel, bool> isEnabled;
        private readonly Action<string> writer;

        /// <summary>
        /// カテゴリと書き込み処理を指定してロガーを初期化します。
        /// </summary>
        /// <param name="categoryName">ログカテゴリ名です。</param>
        /// <param name="isEnabled">ログレベルの出力可否を判定する処理です。</param>
        /// <param name="writer">整形済みログの書き込み処理です。</param>
        internal DailyFileLogger(
            string categoryName,
            Func<LogLevel, bool> isEnabled,
            Action<string> writer)
        {
            this.categoryName = categoryName;
            this.isEnabled = isEnabled;
            this.writer = writer;
        }

        /// <summary>
        /// このロガーでは外部スコープを保持しないため、空のスコープを返します。
        /// </summary>
        /// <typeparam name="TState">スコープ状態の型です。</typeparam>
        /// <param name="state">スコープへ渡された状態です。</param>
        /// <returns>何もしないスコープです。</returns>
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return NullScope.Instance;
        }

        /// <summary>
        /// 指定ログレベルが出力対象かどうかを返します。
        /// </summary>
        /// <param name="logLevel">確認するログレベルです。</param>
        /// <returns>現在の最小ログレベル以上ならtrueです。</returns>
        public bool IsEnabled(LogLevel logLevel) => isEnabled(logLevel);

        /// <summary>
        /// ログメッセージと例外を1行以上のテキストへ整形して出力します。
        /// </summary>
        /// <typeparam name="TState">ログ状態の型です。</typeparam>
        /// <param name="logLevel">ログレベルです。</param>
        /// <param name="eventId">イベントIDです。</param>
        /// <param name="state">ログ状態です。</param>
        /// <param name="exception">記録する例外です。</param>
        /// <param name="formatter">メッセージ整形処理です。</param>
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (!IsEnabled(logLevel))
            {
                return;
            }

            string message = formatter(state, exception);
            if (string.IsNullOrWhiteSpace(message) && exception is null)
            {
                return;
            }

            StringBuilder entry = new();
            entry.Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture));
            entry.Append(" [").Append(logLevel).Append("] ");
            entry.Append(categoryName).Append(": ").AppendLine(message);
            if (exception is not null)
            {
                entry.AppendLine(exception.ToString());
            }

            writer(entry.ToString());
        }
    }

    /// <summary>
    /// ロガーのスコープAPIを満たすための空実装です。
    /// </summary>
    private sealed class NullScope : IDisposable
    {
        /// <summary>
        /// 共有できる空スコープを取得します。
        /// </summary>
        internal static NullScope Instance { get; } = new();

        /// <summary>
        /// 保持資源がないため何も行いません。
        /// </summary>
        public void Dispose()
        {
        }
    }
}
