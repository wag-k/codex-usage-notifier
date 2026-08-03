using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CodexUsageNotifier.Infrastructure.Persistence;

/// <summary>
/// JSONファイルの共通読み書き処理を提供します。
/// </summary>
internal static class JsonFileStore
{
    private static readonly Action<ILogger, string, Exception?> LogCorruptedJson =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(2000, "CorruptedJson"),
            "JSONファイルが破損しているため初期値を使用します。Path: {Path}");

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    /// <summary>
    /// JSONファイルを読み込み、存在しない場合や破損時は初期値を返します。
    /// </summary>
    /// <typeparam name="T">読み込むモデルの型です。</typeparam>
    /// <param name="path">読み込み先のパスです。</param>
    /// <param name="defaultFactory">初期値を生成する処理です。</param>
    /// <param name="logger">破損を記録するロガーです。</param>
    /// <param name="cancellationToken">処理のキャンセル通知です。</param>
    /// <returns>読み込んだモデル、または初期値です。</returns>
    internal static async Task<T> ReadOrDefaultAsync<T>(
        string path,
        Func<T> defaultFactory,
        ILogger logger,
        CancellationToken cancellationToken)
        where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(defaultFactory);
        ArgumentNullException.ThrowIfNull(logger);

        if (!File.Exists(path))
        {
            return defaultFactory();
        }

        try
        {
            await using FileStream stream = File.OpenRead(path);
            T? value = await JsonSerializer.DeserializeAsync<T>(stream, SerializerOptions, cancellationToken);
            return value ?? defaultFactory();
        }
        catch (JsonException exception)
        {
            LogCorruptedJson(logger, path, exception);
            return defaultFactory();
        }
    }

    /// <summary>
    /// JSONを一時ファイルへ書き込み、置換して保存途中の破損を防ぎます。
    /// </summary>
    /// <typeparam name="T">保存するモデルの型です。</typeparam>
    /// <param name="path">保存先のパスです。</param>
    /// <param name="value">保存するモデルです。</param>
    /// <param name="cancellationToken">処理のキャンセル通知です。</param>
    internal static async Task WriteAtomicAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
        where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(value);

        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("保存先ディレクトリを特定できません。");
        }

        Directory.CreateDirectory(directory);
        string temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";

        try
        {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, value, SerializerOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            if (File.Exists(path))
            {
                File.Replace(temporaryPath, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporaryPath, path);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
