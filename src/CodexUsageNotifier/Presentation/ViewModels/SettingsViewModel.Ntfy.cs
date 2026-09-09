using CodexUsageNotifier.Application.Ntfy;
using CodexUsageNotifier.Domain.Models;

namespace CodexUsageNotifier.Presentation.ViewModels;

/// <summary>設定画面の匿名ntfyオンボーディングとテスト通知を管理します。</summary>
public sealed partial class SettingsViewModel
{
    private NtfyTopic? currentNtfyTopic;
    private bool isNtfyTopicVisible;
    private string lastNtfyTestResult = "未実行";
    private readonly string ntfyPrivacyDescription =
        "ntfyのアカウントは不要です。生成した秘密Topicを知る人は通知を購読できるため、パスワードと同様に扱ってください。プロンプトや会話本文は送信せず、利用枠の通知情報だけを送信します。";

    /// <summary>ntfyアカウントが不要であることとプライバシー境界を説明します。</summary>
    public string NtfyPrivacyDescription => ntfyPrivacyDescription;

    /// <summary>保存済みTopicが利用可能かどうかを取得します。</summary>
    public bool IsNtfyTopicConfigured => currentNtfyTopic is not null;

    /// <summary>Topic表示のマスクを解除しているかどうかを取得または設定します。</summary>
    public bool IsNtfyTopicVisible
    {
        get => isNtfyTopicVisible;
        set
        {
            if (SetProperty(ref isNtfyTopicVisible, value))
            {
                OnPropertyChanged(nameof(NtfyTopicDisplay));
            }
        }
    }

    /// <summary>画面へ表示するマスク済みまたは明示表示中のTopicを取得します。</summary>
    public string NtfyTopicDisplay => currentNtfyTopic is null
        ? "未生成"
        : IsNtfyTopicVisible ? currentNtfyTopic.Value : MaskTopic(currentNtfyTopic.Value);

    /// <summary>利用者が明示的にコピーする秘密Topicを取得します。</summary>
    public string? NtfyTopicValueForCopy => currentNtfyTopic?.Value;

    /// <summary>利用者がスマートフォンへ渡すAndroid ntfy購読用ディープリンクを取得します。</summary>
    public string? NtfyAndroidDeepLink => currentNtfyTopic is null
        ? null
        : NtfySubscriptionLinkFactory.CreateAndroidDeepLink(currentNtfyTopic);

    /// <summary>Topicがあり処理中でない場合にテスト通知を送れるかを取得します。</summary>
    public bool IsNtfyTestAvailable => !IsBusy && IsNtfyTopicConfigured;

    /// <summary>直近のntfyテスト通知結果を取得します。</summary>
    public string LastNtfyTestResult
    {
        get => lastNtfyTestResult;
        private set => SetProperty(ref lastNtfyTestResult, value);
    }

    /// <summary>暗号学的乱数からTopicを生成してDPAPI保護ストアへ保存します。</summary>
    public async Task GenerateNtfyTopicAsync(CancellationToken cancellationToken)
    {
        if (IsBusy || ntfyTopicGenerator is null || ntfyTopicStore is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            NtfyTopic topic = ntfyTopicGenerator.Generate();
            await Task.Run(() => ntfyTopicStore.SaveAsync(topic, cancellationToken), cancellationToken);
            if (baselineSettings.NtfyNotificationEnabled)
            {
                AppSettings disabled = baselineSettings with { NtfyNotificationEnabled = false };
                await Task.Run(async () =>
                {
                    await settingsRepository.SaveAsync(disabled, cancellationToken).ConfigureAwait(false);
                    await settingsChangeSink.ApplyAsync(disabled, cancellationToken).ConfigureAwait(false);
                }, cancellationToken);
                baselineSettings = disabled;
                NtfyNotificationEnabled = false;
                baselineSignature = CaptureSettingsSignature(disabled);
            }
            currentNtfyTopic = topic;
            IsNtfyTopicVisible = false;
            OperationMessage = "新しい秘密Topicを生成しました。ntfyアプリで購読後、テスト通知を確認してください。";
            RaiseNtfyProperties();
            ValidateAndTrackChanges();
        }
        finally
        {
            IsBusy = false;
            RaiseNtfyProperties();
        }
    }

    /// <summary>本番状態を変更せず保存済みTopicへテスト通知を送信します。</summary>
    public async Task SendNtfyTestNotificationAsync(CancellationToken cancellationToken)
    {
        if (!IsNtfyTestAvailable || ntfyTestNotificationService is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            NtfyTestNotificationResult result = await Task.Run(
                () => ntfyTestNotificationService.SendAsync(cancellationToken), cancellationToken);
            LastNtfyTestResult = $"{DateTimeOffset.Now:yyyy/MM/dd HH:mm} {result.Message}";
            OperationMessage = result.Message;
        }
        finally
        {
            IsBusy = false;
            RaiseNtfyProperties();
        }
    }

    /// <summary>DPAPI保護ストアからTopicを読み込み、破損時は安全な未設定表示にします。</summary>
    private async Task RefreshNtfyTopicAsync(CancellationToken cancellationToken)
    {
        try
        {
            currentNtfyTopic = ntfyTopicStore is null
                ? null
                : await Task.Run(() => ntfyTopicStore.LoadAsync(cancellationToken), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            currentNtfyTopic = null;
            OperationMessage = "保存されたスマホ通知Topicを読み込めません。再生成してください。";
        }

        IsNtfyTopicVisible = false;
        RaiseNtfyProperties();
    }

    /// <summary>Topic状態に依存する表示と操作可否の更新を通知します。</summary>
    private void RaiseNtfyProperties()
    {
        OnPropertyChanged(nameof(IsNtfyTopicConfigured));
        OnPropertyChanged(nameof(NtfyTopicDisplay));
        OnPropertyChanged(nameof(NtfyTopicValueForCopy));
        OnPropertyChanged(nameof(NtfyAndroidDeepLink));
        OnPropertyChanged(nameof(IsNtfyTestAvailable));
    }

    /// <summary>秘密Topicの先頭と末尾以外を画面上で隠します。</summary>
    private static string MaskTopic(string topic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        return topic.Length <= 12 ? "********" : $"{topic[..8]}…{topic[^4..]}";
    }
}
