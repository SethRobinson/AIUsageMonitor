using AIUsageMonitor.Models;
using MediaBrush = System.Windows.Media.Brush;

namespace AIUsageMonitor.ViewModels;

public sealed class UsageWindowDisplay
{
    public UsageWindowDisplay(UsageWindow usageWindow)
    {
        Title = string.IsNullOrWhiteSpace(usageWindow.Title) ? "Usage" : usageWindow.Title;
        Limit = usageWindow.Limit;
        Used = usageWindow.Used;
        Remaining = usageWindow.EffectiveRemaining;
        UsedPercent = usageWindow.UsedPercent;
        RemainingPercent = usageWindow.RemainingPercent;
        Detail = usageWindow.Detail;
        IsInactive = usageWindow.IsInactive;

        if (usageWindow.IsInactive)
        {
            // No usable quota on this plan: show a calm greyed row instead of an alarming
            // "0% left / reset passed". The card excludes inactive windows from its status.
            var muted = UsageStatus.Unavailable;
            RemainingText = "N/A";
            LimitText = string.IsNullOrWhiteSpace(usageWindow.Detail) ? "Not on this plan" : usageWindow.Detail;
            ResetText = string.Empty;
            ResetRelativeText = string.Empty;
            ResetRelativeBrush = UsageBrushes.FrozenBrush("#A8AFBA");
            StatusLabel = muted.Label;
            StatusBrush = muted.Foreground;
            StatusBackground = muted.Background;
            ProgressBrush = UsageBrushes.FrozenBrush("#4B5563");
            return;
        }

        var status = UsageStatus.FromRemainingPercent(usageWindow.RemainingPercent);

        RemainingText = string.IsNullOrWhiteSpace(usageWindow.RemainingText)
            ? $"{usageWindow.RemainingPercent:0}% left"
            : usageWindow.RemainingText;
        LimitText = string.IsNullOrWhiteSpace(Detail) ? RemainingText : Detail;
        ResetText = usageWindow.HideReset
            ? string.Empty
            : usageWindow.ResetAt is { } resetAt
            ? FormatResetText(resetAt)
            : FormatMissingResetText(usageWindow);
        ResetRelativeText = !usageWindow.HideReset && usageWindow.ResetAt is { } relativeResetAt
            ? FormatResetRelativeText(relativeResetAt)
            : string.Empty;
        ResetRelativeBrush = !usageWindow.HideReset && usageWindow.ResetAt is { } relativeBrushResetAt
            ? ResetRelativeBrushFor(relativeBrushResetAt)
            : UsageBrushes.FrozenBrush("#A8AFBA");
        StatusLabel = status.Label;
        StatusBrush = status.Foreground;
        StatusBackground = status.Background;
        ProgressBrush = status.Foreground;
    }

    public string Title { get; }

    public bool IsInactive { get; }

    public double Limit { get; }

    public double Used { get; }

    public double Remaining { get; }

    public double UsedPercent { get; }

    public double RemainingPercent { get; }

    public string RemainingText { get; }

    public string LimitText { get; }

    public string ResetText { get; }

    public string ResetRelativeText { get; }

    public MediaBrush ResetRelativeBrush { get; }

    public string Detail { get; }

    public string StatusLabel { get; }

    public MediaBrush StatusBrush { get; }

    public MediaBrush StatusBackground { get; }

    public MediaBrush ProgressBrush { get; }

    private static string FormatResetRelativeText(DateTimeOffset resetAt)
    {
        var remaining = resetAt.ToLocalTime() - DateTimeOffset.Now;

        if (remaining.TotalMinutes <= -1)
        {
            return "reset passed";
        }

        if (remaining.TotalMinutes < 1)
        {
            return "now";
        }

        if (remaining.TotalMinutes < 90)
        {
            var minutes = Math.Max(1, (int)Math.Round(remaining.TotalMinutes));
            return minutes == 1 ? "in 1 minute" : $"in {minutes} minutes";
        }

        if (remaining.TotalHours < 36)
        {
            var hours = Math.Max(1, (int)Math.Round(remaining.TotalHours));
            return hours == 1 ? "in 1 hour" : $"in {hours} hours";
        }

        var days = Math.Max(1, (int)Math.Round(remaining.TotalDays));
        return days == 1 ? "in 1 day" : $"in {days} days";
    }

    private static MediaBrush ResetRelativeBrushFor(DateTimeOffset resetAt)
    {
        var remaining = resetAt.ToLocalTime() - DateTimeOffset.Now;

        if (remaining.TotalMinutes <= -1)
        {
            return UsageBrushes.FrozenBrush("#A8AFBA");
        }

        if (remaining.TotalMinutes <= 0)
        {
            return UsageBrushes.FrozenBrush("#FB7185");
        }

        if (remaining.TotalHours <= 2)
        {
            return UsageBrushes.FrozenBrush("#FBBF24");
        }

        return UsageBrushes.FrozenBrush("#93C5FD");
    }

    private static string FormatResetText(DateTimeOffset resetAt)
    {
        var localResetAt = resetAt.ToLocalTime();
        var prefix = localResetAt <= DateTimeOffset.Now ? "Reset" : "Resets";
        return $"{prefix} {localResetAt:MMM d, h:mm tt}";
    }

    private static string FormatMissingResetText(UsageWindow usageWindow)
    {
        return usageWindow.RemainingPercent >= 99.9
            ? "No active reset"
            : "Reset time unavailable";
    }
}
