using CodexUsageNotifier.Application.Startup;
using CodexUsageNotifier.Infrastructure.Startup;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodexUsageNotifier.Tests.Infrastructure.Startup;

/// <summary>
/// CurrentUser Runキーを使用する自動起動管理を検証します。
/// </summary>
[TestClass]
public sealed class WindowsAutoStartManagerTests
{
    /// <summary>空白を含むexeパスを引用符で囲んで登録することを検証します。</summary>
    [TestMethod]
    public async Task EnableAsync_PathContainsSpaces_WritesQuotedCommand()
    {
        MemoryAutoStartRegistry registry = new();
        WindowsAutoStartManager manager = CreateManager(registry, @"C:\Program Files\Codex Usage Notifier\CodexUsageNotifier.exe");

        AutoStartOperationResult result = await manager.EnableAsync(CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(
            "\"C:\\Program Files\\Codex Usage Notifier\\CodexUsageNotifier.exe\" --autostart",
            registry.Command);
    }

    /// <summary>無効化時に登録値を削除することを検証します。</summary>
    [TestMethod]
    public async Task DisableAsync_Registered_DeletesValue()
    {
        MemoryAutoStartRegistry registry = new() { Command = "registered" };
        WindowsAutoStartManager manager = CreateManager(registry);

        AutoStartOperationResult result = await manager.DisableAsync(CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.IsNull(registry.Command);
    }

    /// <summary>dotnet.exe経由の開発実行を登録しないことを検証します。</summary>
    [TestMethod]
    public async Task EnableAsync_DotnetHost_RejectsRegistration()
    {
        MemoryAutoStartRegistry registry = new();
        WindowsAutoStartManager manager = new(
            registry,
            new FixedExecutablePathProvider(new ExecutablePathInfo
            {
                Path = @"C:\Program Files\dotnet\dotnet.exe",
                CanRegister = false,
                UnsupportedReason = "開発実行中です。",
            }),
            NullLogger<WindowsAutoStartManager>.Instance);

        AutoStartOperationResult result = await manager.EnableAsync(CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(AutoStartRegistrationState.Unsupported, result.Status.State);
        Assert.IsNull(registry.Command);
    }

    /// <summary>設定ONかつRegistry未登録を不一致として検出することを検証します。</summary>
    [TestMethod]
    public async Task GetStatusAsync_SettingOnRegistryOff_ReturnsMismatch()
    {
        WindowsAutoStartManager manager = CreateManager(new MemoryAutoStartRegistry());

        AutoStartStatus status = await manager.GetStatusAsync(true, CancellationToken.None);

        Assert.AreEqual(AutoStartRegistrationState.Mismatch, status.State);
    }

    /// <summary>設定OFFかつRegistry登録済みを不一致として検出することを検証します。</summary>
    [TestMethod]
    public async Task GetStatusAsync_SettingOffRegistryOn_ReturnsMismatch()
    {
        MemoryAutoStartRegistry registry = new() { Command = "legacy" };
        WindowsAutoStartManager manager = CreateManager(registry);

        AutoStartStatus status = await manager.GetStatusAsync(false, CancellationToken.None);

        Assert.AreEqual(AutoStartRegistrationState.Mismatch, status.State);
    }

    /// <summary>同期処理が設定値どおりRegistryを変更することを検証します。</summary>
    [TestMethod]
    public async Task SynchronizeAsync_ChangesRegistryToRequestedState()
    {
        MemoryAutoStartRegistry registry = new();
        WindowsAutoStartManager manager = CreateManager(registry);

        Assert.IsTrue((await manager.SynchronizeAsync(true, CancellationToken.None)).Succeeded);
        Assert.IsNotNull(registry.Command);
        Assert.IsTrue((await manager.SynchronizeAsync(false, CancellationToken.None)).Succeeded);
        Assert.IsNull(registry.Command);
    }

    /// <summary>Registry例外を安全な失敗結果へ変換することを検証します。</summary>
    [TestMethod]
    public async Task SynchronizeAsync_RegistryThrows_ReturnsSafeFailure()
    {
        MemoryAutoStartRegistry registry = new() { ThrowOnAccess = true };
        WindowsAutoStartManager manager = CreateManager(registry);

        AutoStartOperationResult result = await manager.SynchronizeAsync(true, CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(AutoStartRegistrationState.Error, result.Status.State);
        Assert.IsFalse(result.Status.Message.Contains("Registry failure details", StringComparison.Ordinal));
    }

    /// <summary>実際のCurrentUser Runキーを一意なテスト値で安全に読み書きできることを検証します。</summary>
    [TestMethod]
    public void CurrentUserRunRegistry_UniqueTestValue_RoundTripsAndCleansUp()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows Registry統合テストはWindowsでのみ実行します。");
        }

        string valueName = $"Codex Usage Notifier Test {Guid.NewGuid():N}";
        CurrentUserRunRegistry registry = new(valueName);
        try
        {
            registry.WriteCommand("\"C:\\Test Path\\CodexUsageNotifier.exe\"");
            Assert.AreEqual("\"C:\\Test Path\\CodexUsageNotifier.exe\"", registry.ReadCommand());
        }
        finally
        {
            registry.DeleteCommand();
        }

        Assert.IsNull(registry.ReadCommand());
    }

    /// <summary>固定引数だけを自動起動として判定することを検証します。</summary>
    [TestMethod]
    public void IsAutoStartLaunch_FixedArgument_DistinguishesManualLaunch()
    {
        Assert.IsTrue(App.IsAutoStartLaunch(new[] { "--autostart" }));
        Assert.IsTrue(App.IsAutoStartLaunch(new[] { "--AUTOSTART" }));
        Assert.IsFalse(App.IsAutoStartLaunch(Array.Empty<string>()));
        Assert.IsFalse(App.IsAutoStartLaunch(new[] { "--other" }));
    }

    /// <summary>既定のテスト対象を生成します。</summary>
    private static WindowsAutoStartManager CreateManager(
        MemoryAutoStartRegistry registry,
        string executablePath = @"C:\Apps\CodexUsageNotifier.exe")
    {
        return new WindowsAutoStartManager(
            registry,
            new FixedExecutablePathProvider(new ExecutablePathInfo
            {
                Path = executablePath,
                CanRegister = true,
            }),
            NullLogger<WindowsAutoStartManager>.Instance);
    }

    /// <summary>Registry値をメモリ上で保持するテストダブルです。</summary>
    private sealed class MemoryAutoStartRegistry : IAutoStartRegistry
    {
        /// <summary>登録コマンドを取得または設定します。</summary>
        public string? Command { get; set; }

        /// <summary>操作時に例外を発生させるかどうかを取得または設定します。</summary>
        public bool ThrowOnAccess { get; set; }

        /// <inheritdoc />
        public string? ReadCommand()
        {
            ThrowIfRequested();
            return Command;
        }

        /// <inheritdoc />
        public void WriteCommand(string command)
        {
            ThrowIfRequested();
            Command = command;
        }

        /// <inheritdoc />
        public void DeleteCommand()
        {
            ThrowIfRequested();
            Command = null;
        }

        /// <summary>指定時にテスト用例外を発生させます。</summary>
        private void ThrowIfRequested()
        {
            if (ThrowOnAccess)
            {
                throw new UnauthorizedAccessException("Registry failure details");
            }
        }
    }

    /// <summary>固定した実行ファイル情報を返すテストダブルです。</summary>
    private sealed class FixedExecutablePathProvider : IExecutablePathProvider
    {
        private readonly ExecutablePathInfo info;

        /// <summary>返す情報を指定して初期化します。</summary>
        public FixedExecutablePathProvider(ExecutablePathInfo info) => this.info = info;

        /// <inheritdoc />
        public ExecutablePathInfo GetExecutablePath() => info;
    }
}
