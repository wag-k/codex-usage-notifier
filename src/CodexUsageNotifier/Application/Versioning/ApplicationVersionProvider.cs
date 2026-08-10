using System.Reflection;

namespace CodexUsageNotifier.Application.Versioning;

/// <summary>
/// AssemblyのInformationalVersionからアプリケーションのRelease Versionを提供します。
/// </summary>
public sealed class ApplicationVersionProvider
{
    /// <summary>実行中アプリケーションのAssemblyからバージョンを読み取ります。</summary>
    public ApplicationVersionProvider()
        : this(typeof(ApplicationVersionProvider).Assembly)
    {
    }

    /// <summary>指定Assemblyからバージョンを読み取ります。</summary>
    /// <param name="assembly">バージョン情報を持つAssemblyです。</param>
    internal ApplicationVersionProvider(Assembly assembly)
        : this(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3)
            ?? string.Empty)
    {
        ArgumentNullException.ThrowIfNull(assembly);
    }

    /// <summary>指定されたInformationalVersionからRelease Versionを生成します。</summary>
    /// <param name="informationalVersion">ビルドメタデータを含み得るバージョンです。</param>
    internal ApplicationVersionProvider(string informationalVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(informationalVersion);
        InformationalVersion = informationalVersion;
        Version = informationalVersion.Split('+', 2, StringSplitOptions.TrimEntries)[0];
        if (string.IsNullOrWhiteSpace(Version))
        {
            throw new InvalidOperationException("アプリケーションバージョンを解決できません。手動配布を中止してください。");
        }
    }

    /// <summary>ビルドメタデータを除いたRelease Versionを取得します。</summary>
    public string Version { get; }

    /// <summary>Assemblyへ埋め込まれた完全なInformationalVersionを取得します。</summary>
    public string InformationalVersion { get; }
}
