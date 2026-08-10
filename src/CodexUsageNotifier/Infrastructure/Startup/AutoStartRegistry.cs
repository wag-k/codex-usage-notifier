using Microsoft.Win32;

namespace CodexUsageNotifier.Infrastructure.Startup;

/// <summary>
/// 現在ユーザーのRunキー操作を抽象化します。
/// </summary>
internal interface IAutoStartRegistry
{
    /// <summary>登録済みコマンドを読み取ります。</summary>
    /// <returns>登録値、または未登録の場合はnullです。</returns>
    string? ReadCommand();

    /// <summary>指定コマンドを登録します。</summary>
    /// <param name="command">登録する引用符付き実行コマンドです。</param>
    void WriteCommand(string command);

    /// <summary>登録値を削除します。</summary>
    void DeleteCommand();
}

/// <summary>
/// HKEY_CURRENT_USERのRunキーへ自動起動を保存します。
/// </summary>
internal sealed class CurrentUserRunRegistry : IAutoStartRegistry
{
    internal const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string DefaultValueName = "Codex Usage Notifier";
    private readonly string valueName;

    /// <summary>
    /// 本番用の登録名でRegistry操作を初期化します。
    /// </summary>
    public CurrentUserRunRegistry()
        : this(DefaultValueName)
    {
    }

    /// <summary>
    /// テストで衝突しない登録名を指定して初期化します。
    /// </summary>
    /// <param name="valueName">Runキー内の登録名です。</param>
    internal CurrentUserRunRegistry(string valueName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(valueName);
        this.valueName = valueName;
    }

    /// <inheritdoc />
    public string? ReadCommand()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    /// <inheritdoc />
    public void WriteCommand(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Windows自動起動のRegistryキーを開けませんでした。");
        key.SetValue(valueName, command, RegistryValueKind.String);
    }

    /// <inheritdoc />
    public void DeleteCommand()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }
}
