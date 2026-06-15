namespace AIUsageMonitor.Models;

public sealed class AppSettings
{
    public const int DefaultUpdateIntervalMinutes = 20;
    public const int MinimumUpdateIntervalMinutes = 1;
    public const int MaximumUpdateIntervalMinutes = 1440;
    public const int DefaultUiScalePercent = 100;
    public const int MinimumUiScalePercent = 80;
    public const int MaximumUiScalePercent = 150;
    public const int UiScaleStepPercent = 5;
    public const double DefaultCursorIncludedBudgetDollars = 20;
    public const string CursorUsageModePersonal = "PersonalSubscription";
    public const string CursorUsageModeTeamsApiKey = "TeamsApiKey";

    public int UpdateIntervalMinutes { get; set; } = DefaultUpdateIntervalMinutes;

    public int UiScalePercent { get; set; } = DefaultUiScalePercent;

    public Dictionary<string, bool> EnabledProviders { get; set; } = CreateDefaultEnabledProviders();

    public string CursorUsageMode { get; set; } = string.Empty;

    public string CursorApiKey { get; set; } = string.Empty;

    public double CursorIncludedBudgetDollars { get; set; } = DefaultCursorIncludedBudgetDollars;

    public string CursorDashboardCookieHeaderProtected { get; set; } = string.Empty;

    public DateTimeOffset? CursorDashboardCookiesCapturedAt { get; set; }

    public bool ClaudeStatusExporterEnabled { get; set; } = true;

    public bool AutoRunAtLoginEnabled { get; set; }

    public bool DiagnosticLoggingEnabled { get; set; }

    public OverlayWindowPlacement OverlayWindowPlacement { get; set; } = new();

    public AppSettings Clone()
    {
        return new AppSettings
        {
            UpdateIntervalMinutes = UpdateIntervalMinutes,
            UiScalePercent = UiScalePercent,
            EnabledProviders = NormalizeEnabledProviders(EnabledProviders),
            CursorUsageMode = CursorUsageMode,
            CursorApiKey = CursorApiKey,
            CursorIncludedBudgetDollars = CursorIncludedBudgetDollars,
            CursorDashboardCookieHeaderProtected = CursorDashboardCookieHeaderProtected,
            CursorDashboardCookiesCapturedAt = CursorDashboardCookiesCapturedAt,
            ClaudeStatusExporterEnabled = ClaudeStatusExporterEnabled,
            AutoRunAtLoginEnabled = AutoRunAtLoginEnabled,
            DiagnosticLoggingEnabled = DiagnosticLoggingEnabled,
            OverlayWindowPlacement = OverlayWindowPlacement?.Clone() ?? new OverlayWindowPlacement()
        };
    }

    public bool IsProviderEnabled(string providerName)
    {
        return !EnabledProviders.TryGetValue(providerName, out var isEnabled) || isEnabled;
    }

    public void SetProviderEnabled(string providerName, bool isEnabled)
    {
        EnabledProviders[providerName] = isEnabled;
    }

    public void Normalize()
    {
        UpdateIntervalMinutes = Math.Clamp(
            UpdateIntervalMinutes,
            MinimumUpdateIntervalMinutes,
            MaximumUpdateIntervalMinutes);

        UiScalePercent = NormalizeUiScalePercent(UiScalePercent);

        EnabledProviders = NormalizeEnabledProviders(EnabledProviders);
        CursorUsageMode = NormalizeCursorUsageMode();

        CursorApiKey = CursorApiKey.Trim();

        if (CursorIncludedBudgetDollars <= 0 || double.IsNaN(CursorIncludedBudgetDollars) || double.IsInfinity(CursorIncludedBudgetDollars))
        {
            CursorIncludedBudgetDollars = DefaultCursorIncludedBudgetDollars;
        }

        OverlayWindowPlacement ??= new OverlayWindowPlacement();
        OverlayWindowPlacement.Normalize();
    }

    private static int NormalizeUiScalePercent(int uiScalePercent)
    {
        if (uiScalePercent <= 0)
        {
            return DefaultUiScalePercent;
        }

        var clampedScale = Math.Clamp(
            uiScalePercent,
            MinimumUiScalePercent,
            MaximumUiScalePercent);
        var stepCount = (int)Math.Round(
            (clampedScale - MinimumUiScalePercent) / (double)UiScaleStepPercent,
            MidpointRounding.AwayFromZero);
        var steppedScale = MinimumUiScalePercent + stepCount * UiScaleStepPercent;

        return Math.Clamp(steppedScale, MinimumUiScalePercent, MaximumUiScalePercent);
    }

    private string NormalizeCursorUsageMode()
    {
        if (string.Equals(CursorUsageMode, CursorUsageModePersonal, StringComparison.OrdinalIgnoreCase))
        {
            return CursorUsageModePersonal;
        }

        if (string.Equals(CursorUsageMode, CursorUsageModeTeamsApiKey, StringComparison.OrdinalIgnoreCase))
        {
            return CursorUsageModeTeamsApiKey;
        }

        if (!string.IsNullOrWhiteSpace(CursorDashboardCookieHeaderProtected))
        {
            return CursorUsageModePersonal;
        }

        return string.IsNullOrWhiteSpace(CursorApiKey)
            ? CursorUsageModePersonal
            : CursorUsageModeTeamsApiKey;
    }

    private static Dictionary<string, bool> CreateDefaultEnabledProviders()
    {
        return KnownProviders.All.ToDictionary(
            providerName => providerName,
            _ => true,
            StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, bool> NormalizeEnabledProviders(Dictionary<string, bool>? enabledProviders)
    {
        var normalizedProviders = CreateDefaultEnabledProviders();

        if (enabledProviders is not null)
        {
            foreach (var pair in enabledProviders)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key))
                {
                    normalizedProviders[pair.Key.Trim()] = pair.Value;
                }
            }
        }

        return normalizedProviders;
    }
}

public sealed class OverlayWindowPlacement
{
    public double? Left { get; set; }

    public double? Top { get; set; }

    public double? Width { get; set; }

    public double? Height { get; set; }

    public OverlayWindowPlacement Clone()
    {
        return new OverlayWindowPlacement
        {
            Left = Left,
            Top = Top,
            Width = Width,
            Height = Height
        };
    }

    public void Normalize()
    {
        Left = NormalizeFiniteValue(Left);
        Top = NormalizeFiniteValue(Top);
        Width = NormalizePositiveValue(Width);
        Height = NormalizePositiveValue(Height);
    }

    private static double? NormalizePositiveValue(double? value)
    {
        var normalizedValue = NormalizeFiniteValue(value);
        return normalizedValue is > 0 ? normalizedValue : null;
    }

    private static double? NormalizeFiniteValue(double? value)
    {
        return value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value)
            ? value
            : null;
    }
}
