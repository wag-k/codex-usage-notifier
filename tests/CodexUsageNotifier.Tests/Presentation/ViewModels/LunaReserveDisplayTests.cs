using CodexUsageNotifier.Domain.Models;
using CodexUsageNotifier.Presentation.ViewModels;

namespace CodexUsageNotifier.Tests.Presentation.ViewModels;

/// <summary>通常週間枠とLuna予備枠の区別、表示条件、更新を検証します。</summary>
[TestClass]
public sealed class LunaReserveDisplayTests
{
    /// <summary>取得順と予備使用率にかかわらず通常週間枠を主表示に維持します。</summary>
    [TestMethod]
    [DataRow(true, 0D)]
    [DataRow(false, 0D)]
    [DataRow(true, 25D)]
    [DataRow(false, 100D)]
    public void Initialize_ReserveObserved_ShowsCodexWithReserveSuffix(bool reserveFirst, double reserveUsed)
    {
        RateLimitWindow codex = CreateWindow("codex", 35);
        RateLimitWindow reserve = CreateWindow("base_model_inference", reserveUsed);
        StatusViewModel viewModel = new();
        viewModel.Initialize(AppSettings.CreateDefault(), CreateState(reserveFirst ? [reserve, codex] : [codex, reserve]));

        Assert.AreEqual(65D, viewModel.WeeklyCard.RemainingPercent);
        Assert.AreEqual(35D, viewModel.WeeklyCard.UsedPercent);
        Assert.AreEqual($"使用率 35%（Luna予備 {reserveUsed:0.#}%）", viewModel.WeeklyCard.UsedPercentText);
        StringAssert.Contains(viewModel.WeeklyRateLimit, "残り 65%");
        Assert.AreEqual("使用率 --", viewModel.FiveHourCard.UsedPercentText);
        StringAssert.Contains(viewModel.AllRateLimits, "base_model_inference");
    }

    /// <summary>予備枠だけでは通常週間枠の数値やリセット時刻を補完しません。</summary>
    [TestMethod]
    public void CreateWeekly_OnlyReserve_KeepsMainQuotaUnobserved()
    {
        RateLimitCardViewModel card = RateLimitCardViewModel.CreateWeekly(new UsageSnapshot
        {
            RateLimits = [CreateWindow("base_model_inference", 0)],
        });
        Assert.IsFalse(card.IsObserved);
        Assert.IsNull(card.RemainingPercent);
        Assert.AreEqual("使用率 --（Luna予備 0%）", card.UsedPercentText);
        Assert.AreEqual("未観測", card.ResetAtText);
    }

    /// <summary>予備枠が次の取得から消えた場合に古い括弧表示を残しません。</summary>
    [TestMethod]
    public void Initialize_ReserveDisappears_RemovesSuffix()
    {
        RateLimitWindow codex = CreateWindow("codex", 35);
        StatusViewModel viewModel = new();
        viewModel.Initialize(AppSettings.CreateDefault(), CreateState(codex, CreateWindow("base_model_inference", 0)));
        viewModel.Initialize(AppSettings.CreateDefault(), CreateState(codex));
        Assert.AreEqual("使用率 35%", viewModel.WeeklyCard.UsedPercentText);
    }

    /// <summary>不正な使用率を有効な予備使用率として表示しません。</summary>
    [TestMethod]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    [DataRow(double.NegativeInfinity)]
    [DataRow(-1D)]
    [DataRow(101D)]
    public void CreateWeekly_InvalidReserveUsage_OmitsSuffix(double reserveUsed)
    {
        RateLimitCardViewModel card = RateLimitCardViewModel.CreateWeekly(new UsageSnapshot
        {
            RateLimits = [CreateWindow("codex", 35), CreateWindow("base_model_inference", reserveUsed)],
        });
        Assert.AreEqual("使用率 35%", card.UsedPercentText);
    }

    /// <summary>別IDや未対応の期間をLuna予備週間枠と推測しません。</summary>
    [TestMethod]
    [DataRow("other", RateLimitClassification.Weekly)]
    [DataRow("base_model_inference", RateLimitClassification.Unknown)]
    [DataRow("base_model_inference", RateLimitClassification.FiveHour)]
    public void CreateWeekly_UnrelatedWindow_OmitsSuffix(string limitId, RateLimitClassification classification)
    {
        RateLimitCardViewModel card = RateLimitCardViewModel.CreateWeekly(new UsageSnapshot
        {
            RateLimits = [CreateWindow("codex", 35), CreateWindow(limitId, 0, classification)],
        });
        Assert.AreEqual("使用率 35%", card.UsedPercentText);
    }

    /// <summary>公開ファクトリーのnull引数を明示的に拒否します。</summary>
    [TestMethod]
    public void CreateWeekly_NullSnapshot_ThrowsArgumentNullException()
    {
        Assert.ThrowsException<ArgumentNullException>(() => RateLimitCardViewModel.CreateWeekly(null!));
    }

    /// <summary>実アカウントに依存しない表示テスト用の利用枠を生成します。</summary>
    private static RateLimitWindow CreateWindow(
        string limitId,
        double usedPercent,
        RateLimitClassification classification = RateLimitClassification.Weekly)
    {
        return new RateLimitWindow
        {
            LimitId = limitId,
            Classification = classification,
            WindowDurationMinutes = classification == RateLimitClassification.Weekly ? 10080 : 300,
            UsedPercent = usedPercent,
            RemainingPercent = 100D - usedPercent,
        };
    }

    /// <summary>固定時刻のスナップショットを持つ初期表示状態を生成します。</summary>
    private static ApplicationState CreateState(params RateLimitWindow[] windows)
    {
        return new ApplicationState
        {
            LastUsageSnapshot = new UsageSnapshot { CapturedAtUtc = DateTimeOffset.UnixEpoch, RateLimits = windows },
        };
    }
}
