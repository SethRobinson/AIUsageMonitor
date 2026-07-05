using System.Text.Json.Serialization;

namespace AIUsageMonitor.Models;

public sealed class UsageWindow
{
    public string Title { get; init; } = string.Empty;

    public string DisplayGroupName { get; init; } = string.Empty;

    public double Limit { get; init; } = 100;

    public double Used { get; init; }

    public double? Remaining { get; init; }

    public string RemainingText { get; init; } = string.Empty;

    public DateTimeOffset? ResetAt { get; init; }

    public string Detail { get; init; } = string.Empty;

    public bool HideReset { get; init; }

    /// <summary>
    /// True when this model family has no usable quota on the current account/plan — e.g. a free
    /// Google tier that still returns an empty, already-reset "Pro" bucket. Inactive windows are
    /// shown muted as "Not on this plan" and are excluded from the provider card's headline percent
    /// and overall status, so an unusable model can't drag the whole card to 0% / Exhausted.
    /// </summary>
    public bool IsInactive { get; init; }

    [JsonIgnore]
    public double EffectiveRemaining => Remaining ?? Math.Max(Limit - Used, 0);

    [JsonIgnore]
    public double UsedPercent => Limit <= 0 ? 0 : Math.Clamp(Used * 100d / Limit, 0, 100);

    [JsonIgnore]
    public double RemainingPercent => Limit <= 0 ? 0 : Math.Clamp(EffectiveRemaining * 100d / Limit, 0, 100);
}
