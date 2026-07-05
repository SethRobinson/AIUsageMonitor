using System.ComponentModel;
using System.Runtime.CompilerServices;
using AIUsageMonitor.Models;
using MediaBrush = System.Windows.Media.Brush;

namespace AIUsageMonitor.ViewModels;

public sealed class ProviderUsageCard : INotifyPropertyChanged
{
    private bool _isChecking;
    private string _checkedText = string.Empty;
    private readonly string _summaryText;
    private readonly string _compactSummaryText;
    private readonly string _miniSummaryText;

    public ProviderUsageCard(ProviderUsage usage)
    {
        ShortName = string.IsNullOrWhiteSpace(usage.Name) ? "Provider" : usage.Name.Trim();
        SourceProviderName = string.IsNullOrWhiteSpace(usage.SourceProviderName)
            ? ShortName
            : usage.SourceProviderName.Trim();
        ConstrainedShortName = GetConstrainedShortName(ShortName, SourceProviderName);
        Name = FormatDisplayName(ShortName, usage.PlanName);
        Source = usage.Source;
        StatusMessage = usage.StatusMessage;
        IsUnavailable = usage.IsUnavailable;
        LastCheckedAt = usage.LastCheckedAt;
        AccentBrush = UsageBrushes.ProviderAccent(usage.Name);
        Windows = usage.Windows.Select(window => new UsageWindowDisplay(window)).ToList();

        // Inactive windows (e.g. a "Pro" model the current plan can't use) are still shown, but they
        // must not set the card's headline percent or status — otherwise an unusable model reads as
        // "0% / Exhausted" even when the usable models are full.
        var activeWindows = Windows.Where(window => !window.IsInactive).ToList();
        PrimaryRemainingPercent = activeWindows.Count == 0
            ? 0
            : activeWindows.Min(window => window.RemainingPercent);
        ShowsSummaryProgress = activeWindows.Any(window => !window.IsBalance);

        var status = usage.IsUnavailable || activeWindows.Count == 0
            ? UsageStatus.Unavailable
            : UsageStatus.FromRemainingPercent(PrimaryRemainingPercent);
        OverallStatusLabel = status.Label;
        OverallStatusBrush = status.Foreground;
        OverallStatusBackground = status.Background;
        SummaryProgressBrush = status.Foreground;
        IsBalanceOnly = activeWindows.Count > 0 && activeWindows.All(window => window.IsBalance);
        BalanceSummaryText = IsBalanceOnly ? activeWindows[0].RemainingText : string.Empty;
        _summaryText = usage.IsUnavailable || activeWindows.Count == 0
            ? $"{ShortName} - {GetUnavailableSummary(usage.StatusMessage)}"
            : !string.IsNullOrWhiteSpace(BalanceSummaryText)
            ? $"{ShortName} - {BalanceSummaryText}"
            : $"{ShortName} - {PrimaryRemainingPercent:0}%";
        _compactSummaryText = usage.IsUnavailable || activeWindows.Count == 0 || string.IsNullOrWhiteSpace(BalanceSummaryText)
            ? _summaryText
            : $"{BalanceSummaryText} - {ShortName}";
        _miniSummaryText = usage.IsUnavailable || activeWindows.Count == 0
            ? $"{ConstrainedShortName} - {GetUnavailableSummary(usage.StatusMessage)}"
            : !string.IsNullOrWhiteSpace(BalanceSummaryText)
            ? BalanceSummaryText
            : $"{ConstrainedShortName} - {PrimaryRemainingPercent:0}%";
        RefreshCheckedText();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; }

    public string ShortName { get; }

    public string SourceProviderName { get; }

    public string ConstrainedShortName { get; }

    public string Source { get; }

    public string StatusMessage { get; }

    public bool IsUnavailable { get; }

    public DateTimeOffset? LastCheckedAt { get; }

    public bool IsChecking
    {
        get => _isChecking;
        private set
        {
            if (SetField(ref _isChecking, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SummaryText)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CompactSummaryText)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MiniSummaryText)));
            }
        }
    }

    public string CheckedText
    {
        get => _checkedText;
        private set => SetField(ref _checkedText, value);
    }

    public bool HasWindows => Windows.Count > 0;

    public double PrimaryRemainingPercent { get; }

    public bool ShowsSummaryProgress { get; }

    public bool IsBalanceOnly { get; }

    public string BalanceSummaryText { get; }

    public string SummaryText => IsChecking ? $"{ShortName} - checking" : _summaryText;

    public string CompactSummaryText => IsChecking ? $"{ShortName} - checking" : _compactSummaryText;

    public string MiniSummaryText => IsChecking ? $"{ConstrainedShortName} - checking" : _miniSummaryText;

    public MediaBrush SummaryProgressBrush { get; }

    public MediaBrush AccentBrush { get; }

    public IReadOnlyList<UsageWindowDisplay> Windows { get; }

    public string OverallStatusLabel { get; }

    public MediaBrush OverallStatusBrush { get; }

    public MediaBrush OverallStatusBackground { get; }

    public void SetChecking(bool isChecking)
    {
        IsChecking = isChecking;
        RefreshCheckedText();
    }

    public void RefreshCheckedText()
    {
        if (IsChecking)
        {
            CheckedText = "Checking...";
            return;
        }

        if (LastCheckedAt is null)
        {
            CheckedText = "Not checked yet";
            return;
        }

        var elapsed = DateTimeOffset.Now - LastCheckedAt.Value;
        CheckedText = elapsed.TotalSeconds switch
        {
            < 45 => "Checked now",
            < 90 => "Checked 1m ago",
            < 3600 => $"Checked {(int)Math.Round(elapsed.TotalMinutes)}m ago",
            < 5400 => "Checked 1h ago",
            < 86400 => $"Checked {(int)Math.Round(elapsed.TotalHours)}h ago",
            _ => $"Checked {(int)Math.Round(elapsed.TotalDays)}d ago"
        };
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    private static string FormatDisplayName(string providerName, string planName)
    {
        if (string.IsNullOrWhiteSpace(planName) ||
            providerName.Contains(planName, StringComparison.OrdinalIgnoreCase))
        {
            return providerName;
        }

        return $"{providerName} ({planName})";
    }

    private static string GetConstrainedShortName(string providerName, string sourceProviderName)
    {
        if (string.IsNullOrWhiteSpace(sourceProviderName) ||
            string.Equals(providerName, sourceProviderName, StringComparison.OrdinalIgnoreCase) ||
            !providerName.StartsWith(sourceProviderName, StringComparison.OrdinalIgnoreCase))
        {
            return providerName;
        }

        var suffix = providerName[sourceProviderName.Length..].Trim();
        return string.IsNullOrWhiteSpace(suffix) ? providerName : suffix;
    }

    private static string GetUnavailableSummary(string statusMessage)
    {
        if (statusMessage.Contains("not running", StringComparison.OrdinalIgnoreCase))
        {
            return "not running";
        }

        if (statusMessage.Contains("not installed", StringComparison.OrdinalIgnoreCase))
        {
            return "not installed";
        }

        if (statusMessage.Contains("paused", StringComparison.OrdinalIgnoreCase))
        {
            return "paused";
        }

        if (statusMessage.Contains("failed", StringComparison.OrdinalIgnoreCase))
        {
            return "failed";
        }

        return "unavailable";
    }
}
