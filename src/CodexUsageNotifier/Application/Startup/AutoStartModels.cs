namespace CodexUsageNotifier.Application.Startup;

/// <summary>
/// 設定値とWindowsの自動起動登録を比較した状態を表します。
/// </summary>
public enum AutoStartRegistrationState
{
    /// <summary>設定どおり登録されています。</summary>
    Registered,

    /// <summary>設定どおり登録されていません。</summary>
    NotRegistered,

    /// <summary>設定値とOS登録状態が一致していません。</summary>
    Mismatch,

    /// <summary>開発実行などの理由で登録できません。</summary>
    Unsupported,

    /// <summary>Registryの確認中にエラーが発生しました。</summary>
    Error,
}

/// <summary>
/// Windows自動起動の確認結果を表します。
/// </summary>
public sealed record AutoStartStatus
{
    /// <summary>設定値とOS状態を比較した結果を取得または設定します。</summary>
    public AutoStartRegistrationState State { get; init; }

    /// <summary>登録名に何らかの値が存在するかどうかを取得または設定します。</summary>
    public bool HasRegistration { get; init; }

    /// <summary>現在の実行ファイルと一致する登録かどうかを取得または設定します。</summary>
    public bool IsCurrentExecutableRegistered { get; init; }

    /// <summary>画面へ表示できる安全な説明を取得または設定します。</summary>
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// Windows自動起動の変更結果を表します。
/// </summary>
public sealed record AutoStartOperationResult
{
    /// <summary>OS状態の変更に成功したかどうかを取得または設定します。</summary>
    public bool Succeeded { get; init; }

    /// <summary>変更後または失敗時の状態を取得または設定します。</summary>
    public AutoStartStatus Status { get; init; } = new();
}
