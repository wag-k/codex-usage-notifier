using CodexUsageNotifier.Domain.Models;
using CodexUsageNotifier.Presentation.ViewModels;

namespace CodexUsageNotifier.Tests.Presentation.ViewModels;

/// <summary>
/// ダッシュボード用表示モデルの境界値と安全な表示を検証します。
/// </summary>
[TestClass]
public sealed class DashboardViewModelsTests
{
    /// <summary>0%がそのまま円形表示へ渡されることを検証します。</summary>
    [TestMethod]
    public void Normalize_Zero_ReturnsZero()
    {
        Assert.AreEqual(0D, UsageRingValue.Normalize(0D));
    }

    /// <summary>50%がそのまま円形表示へ渡されることを検証します。</summary>
    [TestMethod]
    public void Normalize_Fifty_ReturnsFifty()
    {
        Assert.AreEqual(50D, UsageRingValue.Normalize(50D));
    }

    /// <summary>100%がそのまま円形表示へ渡されることを検証します。</summary>
    [TestMethod]
    public void Normalize_OneHundred_ReturnsOneHundred()
    {
        Assert.AreEqual(100D, UsageRingValue.Normalize(100D));
    }

    /// <summary>負数が0%へ制限されることを検証します。</summary>
    [TestMethod]
    public void Normalize_Negative_ReturnsZero()
    {
        Assert.AreEqual(0D, UsageRingValue.Normalize(-12D));
    }

    /// <summary>100%超過が100%へ制限されることを検証します。</summary>
    [TestMethod]
    public void Normalize_OverOneHundred_ReturnsOneHundred()
    {
        Assert.AreEqual(100D, UsageRingValue.Normalize(112D));
    }

    /// <summary>nullが未取得のまま保持されることを検証します。</summary>
    [TestMethod]
    public void Normalize_Null_ReturnsNull()
    {
        Assert.IsNull(UsageRingValue.Normalize(null));
    }

    /// <summary>NaNが未取得として扱われることを検証します。</summary>
    [TestMethod]
    public void Normalize_NaN_ReturnsNull()
    {
        Assert.IsNull(UsageRingValue.Normalize(double.NaN));
    }

    /// <summary>正負の無限大が未取得として扱われることを検証します。</summary>
    [TestMethod]
    public void Normalize_Infinity_ReturnsNull()
    {
        Assert.IsNull(UsageRingValue.Normalize(double.PositiveInfinity));
        Assert.IsNull(UsageRingValue.Normalize(double.NegativeInfinity));
    }

    /// <summary>未観測カードが0%とは異なる表示になることを検証します。</summary>
    [TestMethod]
    public void CreateUnobserved_DistinguishesFromZeroPercent()
    {
        RateLimitCardViewModel card = RateLimitCardViewModel.CreateUnobserved("5時間枠");

        Assert.IsFalse(card.IsObserved);
        Assert.IsNull(card.RemainingPercent);
        Assert.AreEqual("--", card.RemainingPercentText);
        Assert.AreEqual(DashboardVisualState.Unobserved, card.VisualState);
    }

    /// <summary>観測済み0%カードが警告表示になることを検証します。</summary>
    [TestMethod]
    public void Create_ObservedZero_ShowsZeroAndDanger()
    {
        RateLimitCardViewModel card = RateLimitCardViewModel.Create(
            "5時間枠",
            CreateWindow(0D, 100D),
            DateTimeOffset.UnixEpoch);

        Assert.IsTrue(card.IsObserved);
        Assert.AreEqual(0D, card.RemainingPercent);
        Assert.AreEqual("0%", card.RemainingPercentText);
        Assert.AreEqual(DashboardVisualState.Danger, card.VisualState);
    }

    /// <summary>50%以上の残量が通常表示になることを検証します。</summary>
    [TestMethod]
    public void Create_FiftyPercent_UsesNormalVisualState()
    {
        RateLimitCardViewModel card = RateLimitCardViewModel.Create(
            "週間枠",
            CreateWindow(50D, 50D),
            DateTimeOffset.UnixEpoch);

        Assert.AreEqual(DashboardVisualState.Normal, card.VisualState);
    }

    /// <summary>20%以上50%未満の残量が注意表示になることを検証します。</summary>
    [TestMethod]
    public void Create_TwentyPercent_UsesWarningVisualState()
    {
        RateLimitCardViewModel card = RateLimitCardViewModel.Create(
            "週間枠",
            CreateWindow(20D, 80D),
            DateTimeOffset.UnixEpoch);

        Assert.AreEqual(DashboardVisualState.Warning, card.VisualState);
    }

    /// <summary>20%未満の残量が警告表示になることを検証します。</summary>
    [TestMethod]
    public void Create_BelowTwentyPercent_UsesDangerVisualState()
    {
        RateLimitCardViewModel card = RateLimitCardViewModel.Create(
            "週間枠",
            CreateWindow(19.9D, 80.1D),
            DateTimeOffset.UnixEpoch);

        Assert.AreEqual(DashboardVisualState.Danger, card.VisualState);
    }

    /// <summary>リセット時刻がない観測済み枠を例外なく表示できることを検証します。</summary>
    [TestMethod]
    public void Create_WithoutResetTime_ShowsUnavailableText()
    {
        RateLimitCardViewModel card = RateLimitCardViewModel.Create(
            "週間枠",
            CreateWindow(65D, 35D),
            DateTimeOffset.UnixEpoch);

        Assert.AreEqual("リセット時刻未取得", card.ResetAtText);
        Assert.AreEqual("残り時間を取得できません", card.RemainingTimeText);
    }

    /// <summary>非数の割合を表示へ伝播させないことを検証します。</summary>
    [TestMethod]
    public void Create_NonFinitePercent_ShowsUnavailableText()
    {
        RateLimitCardViewModel card = RateLimitCardViewModel.Create(
            "週間枠",
            CreateWindow(double.NaN, double.PositiveInfinity),
            DateTimeOffset.UnixEpoch);

        Assert.IsNull(card.RemainingPercent);
        Assert.AreEqual("--", card.RemainingPercentText);
        Assert.AreEqual("使用率 --", card.UsedPercentText);
    }

    /// <summary>メールアドレスのローカル部が概要画面向けに隠されることを検証します。</summary>
    [TestMethod]
    public void Mask_ValidEmail_HidesLocalPart()
    {
        Assert.AreEqual("u***@example.com", EmailAddressMaskFormatter.Mask("user@example.com"));
    }

    /// <summary>空のメールアドレスが未認証表示になることを検証します。</summary>
    [TestMethod]
    public void Mask_EmptyEmail_ShowsUnauthenticated()
    {
        Assert.AreEqual("未認証", EmailAddressMaskFormatter.Mask(null));
    }

    /// <summary>不正形式の文字列をそのまま画面へ露出しないことを検証します。</summary>
    [TestMethod]
    public void Mask_InvalidEmail_DoesNotExposeValue()
    {
        Assert.AreEqual("アカウント情報あり", EmailAddressMaskFormatter.Mask("private-value"));
    }

    /// <summary>テスト用の利用枠を生成します。</summary>
    /// <param name="remainingPercent">残量です。</param>
    /// <param name="usedPercent">使用率です。</param>
    /// <returns>週間枠として分類した利用枠です。</returns>
    private static RateLimitWindow CreateWindow(double remainingPercent, double usedPercent)
    {
        return new RateLimitWindow
        {
            LimitId = "codex",
            Position = RateLimitPosition.Primary,
            Classification = RateLimitClassification.Weekly,
            WindowDurationMinutes = 10080,
            RemainingPercent = remainingPercent,
            UsedPercent = usedPercent,
        };
    }
}
