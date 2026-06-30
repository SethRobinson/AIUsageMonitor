using System.Globalization;
using AIUsageMonitor.Collectors;

namespace AIUsageMonitor.Tests;

[TestClass]
[DoNotParallelize]
public sealed class CodexLogUsageCollectorTests
{
    [TestMethod]
    [DataRow("ja-JP")]
    [DataRow("fr-FR")]
    [DataRow("de-DE")]
    [DataRow("es-ES")]
    [DataRow("it-IT")]
    [DataRow("pt-BR")]
    [DataRow("nl-NL")]
    [DataRow("pl-PL")]
    [DataRow("zh-CN")]
    [DataRow("zh-TW")]
    [DataRow("ko-KR")]
    [DataRow("ru-RU")]
    [DataRow("ar-SA")]
    [DataRow("tr-TR")]
    public void RateLimitLineParsingDoesNotDependOnCurrentCulture(string cultureName)
    {
        using var culture = CultureScope.Use(cultureName);
        var line = """
            {"timestamp":"2026-06-30T03:11:24.1234560Z","payload":{"rate_limits":{"limit_id":"codex","plan_type":"pro","primary":{"used_percent":"79.5","window_minutes":"300","resets_at":"1782801761"},"secondary":{"used_percent":66,"window_minutes":10080,"resets_at":1783388561}}}}
            """;

        var snapshot = CodexLogUsageCollector.TryParseRateLimitLine(line);

        Assert.IsNotNull(snapshot);
        Assert.AreEqual(
            DateTimeOffset.Parse("2026-06-30T03:11:24.1234560+00:00", CultureInfo.InvariantCulture),
            snapshot.Timestamp);
        Assert.AreEqual(79.5d, snapshot.Primary!.UsedPercent);
        Assert.AreEqual(300, snapshot.Primary.WindowMinutes);
        Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(1782801761), snapshot.Primary.ResetsAt);
        Assert.AreEqual(66d, snapshot.Secondary!.UsedPercent);
        Assert.AreEqual(10080, snapshot.Secondary.WindowMinutes);
        Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(1783388561), snapshot.Secondary.ResetsAt);
    }

    [TestMethod]
    [DataRow("Too many requests: rate limit reached.")]
    [DataRow("使用制限に達しました。しばらくしてからもう一度お試しください。")]
    [DataRow("Limite d'utilisation atteinte. Réessayez plus tard.")]
    [DataRow("Nutzungslimit erreicht. Bitte versuchen Sie es später erneut.")]
    [DataRow("Límite de uso alcanzado. Inténtalo de nuevo más tarde.")]
    [DataRow("Limite de uso atingido. Tente novamente mais tarde.")]
    [DataRow("Limite di utilizzo raggiunto. Riprova più tardi.")]
    [DataRow("Gebruikslimiet bereikt. Probeer het later opnieuw.")]
    [DataRow("Limit użycia osiągnięty. Spróbuj ponownie później.")]
    [DataRow("已达到使用限制。请稍后重试。")]
    [DataRow("사용 한도에 도달했습니다. 나중에 다시 시도하세요.")]
    [DataRow("Лимит использования достигнут. Повторите попытку позже.")]
    public void LocalizedQuotaLimitOutputIsDetectedAsExhausted(string output)
    {
        Assert.IsTrue(CliQuotaRefreshRunner.LooksQuotaExhausted(output));
    }

    [TestMethod]
    public void PassedResetWindowMakesCodexSnapshotUnavailable()
    {
        var now = new DateTimeOffset(2026, 6, 30, 12, 7, 0, TimeSpan.FromHours(9));
        var snapshot = new CodexLogUsageCollector.CodexRateLimitSnapshot(
            now.AddMinutes(-20),
            new CodexLogUsageCollector.CodexWindow(21, 300, now.AddMinutes(-2)),
            new CodexLogUsageCollector.CodexWindow(34, 10080, now.AddDays(2)),
            "pro")
        {
            SourceFile = @"C:\Users\Test\.codex\sessions\stale.jsonl"
        };

        var usage = new CodexLogUsageCollector().BuildProviderUsage(
            snapshot,
            "Codex refresh command ran, but no newer quota snapshot was written.",
            now);

        Assert.IsTrue(usage.IsUnavailable);
        Assert.AreEqual("Pro", usage.PlanName);
        Assert.IsEmpty(usage.Windows);
        StringAssert.Contains(usage.StatusMessage, "stale");
        StringAssert.Contains(usage.StatusMessage, "no newer quota snapshot");
    }

    [TestMethod]
    public void FutureResetWindowsStillProduceUsage()
    {
        var now = new DateTimeOffset(2026, 6, 30, 12, 7, 0, TimeSpan.FromHours(9));
        var snapshot = new CodexLogUsageCollector.CodexRateLimitSnapshot(
            now.AddMinutes(-1),
            new CodexLogUsageCollector.CodexWindow(21, 300, now.AddHours(2)),
            new CodexLogUsageCollector.CodexWindow(34, 10080, now.AddDays(2)),
            "pro");

        var usage = new CodexLogUsageCollector().BuildProviderUsage(snapshot, string.Empty, now);

        Assert.IsFalse(usage.IsUnavailable);
        Assert.HasCount(2, usage.Windows);
        Assert.IsTrue(usage.Windows.All(window => window.ResetAt > now));
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _previousCulture;
        private readonly CultureInfo _previousUiCulture;
        private readonly CultureInfo? _previousDefaultCulture;
        private readonly CultureInfo? _previousDefaultUiCulture;

        private CultureScope(CultureInfo culture)
        {
            _previousCulture = CultureInfo.CurrentCulture;
            _previousUiCulture = CultureInfo.CurrentUICulture;
            _previousDefaultCulture = CultureInfo.DefaultThreadCurrentCulture;
            _previousDefaultUiCulture = CultureInfo.DefaultThreadCurrentUICulture;

            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
        }

        public static CultureScope Use(string cultureName)
        {
            return new CultureScope(CultureInfo.GetCultureInfo(cultureName));
        }

        public void Dispose()
        {
            CultureInfo.CurrentCulture = _previousCulture;
            CultureInfo.CurrentUICulture = _previousUiCulture;
            CultureInfo.DefaultThreadCurrentCulture = _previousDefaultCulture;
            CultureInfo.DefaultThreadCurrentUICulture = _previousDefaultUiCulture;
        }
    }
}
