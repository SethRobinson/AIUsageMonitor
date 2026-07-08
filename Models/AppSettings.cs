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

    public List<ProviderAccount> ProviderAccounts { get; set; } = [];

    public string CursorUsageMode { get; set; } = string.Empty;

    public string CursorApiKey { get; set; } = string.Empty;

    public double CursorIncludedBudgetDollars { get; set; } = DefaultCursorIncludedBudgetDollars;

    public string CursorDashboardCookieHeaderProtected { get; set; } = string.Empty;

    public DateTimeOffset? CursorDashboardCookiesCapturedAt { get; set; }

    public string AnthropicApiCreditsCookieHeaderProtected { get; set; } = string.Empty;

    public DateTimeOffset? AnthropicApiCreditsCookiesCapturedAt { get; set; }

    public string AnthropicApiCreditsOrganizationUuid { get; set; } = string.Empty;

    public string AnthropicApiCreditsOrganizationName { get; set; } = string.Empty;

    public AnthropicApiCreditsBalanceCache? AnthropicApiCreditsLastBalance { get; set; }

    public bool ClaudeStatusExporterEnabled { get; set; } = true;

    public bool AutoRunAtLoginEnabled { get; set; }

    public bool DiagnosticLoggingEnabled { get; set; }

    public bool AlwaysOnTop { get; set; } = true;

    public OverlayWindowPlacement OverlayWindowPlacement { get; set; } = new();

    public AppSettings Clone()
    {
        return new AppSettings
        {
            UpdateIntervalMinutes = UpdateIntervalMinutes,
            UiScalePercent = UiScalePercent,
            EnabledProviders = NormalizeEnabledProviders(EnabledProviders),
            ProviderAccounts = NormalizeProviderAccounts(ProviderAccounts, clone: true),
            CursorUsageMode = CursorUsageMode,
            CursorApiKey = CursorApiKey,
            CursorIncludedBudgetDollars = CursorIncludedBudgetDollars,
            CursorDashboardCookieHeaderProtected = CursorDashboardCookieHeaderProtected,
            CursorDashboardCookiesCapturedAt = CursorDashboardCookiesCapturedAt,
            AnthropicApiCreditsCookieHeaderProtected = AnthropicApiCreditsCookieHeaderProtected,
            AnthropicApiCreditsCookiesCapturedAt = AnthropicApiCreditsCookiesCapturedAt,
            AnthropicApiCreditsOrganizationUuid = AnthropicApiCreditsOrganizationUuid,
            AnthropicApiCreditsOrganizationName = AnthropicApiCreditsOrganizationName,
            AnthropicApiCreditsLastBalance = AnthropicApiCreditsLastBalance?.Clone(),
            ClaudeStatusExporterEnabled = ClaudeStatusExporterEnabled,
            AutoRunAtLoginEnabled = AutoRunAtLoginEnabled,
            DiagnosticLoggingEnabled = DiagnosticLoggingEnabled,
            AlwaysOnTop = AlwaysOnTop,
            OverlayWindowPlacement = OverlayWindowPlacement?.Clone() ?? new OverlayWindowPlacement()
        };
    }

    public bool IsProviderEnabled(string providerName)
    {
        return !EnabledProviders.TryGetValue(providerName, out var isEnabled) || isEnabled;
    }

    public IReadOnlyList<ProviderAccount> GetAccounts(string providerName)
    {
        return ProviderAccounts
            .Where(account => string.Equals(account.ProviderName, providerName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(account => account.IsDefault)
            .ThenBy(account => account.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
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
        ProviderAccounts = NormalizeProviderAccounts(ProviderAccounts, clone: false);
        CursorUsageMode = NormalizeCursorUsageMode();

        CursorApiKey = CursorApiKey.Trim();
        AnthropicApiCreditsOrganizationUuid = AnthropicApiCreditsOrganizationUuid.Trim();
        AnthropicApiCreditsOrganizationName = AnthropicApiCreditsOrganizationName.Trim();

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
        var providers = KnownProviders.All.ToDictionary(
            providerName => providerName,
            _ => true,
            StringComparer.OrdinalIgnoreCase);
        providers[KnownProviders.AnthropicApiCredits] = false;
        return providers;
    }

    private static List<ProviderAccount> NormalizeProviderAccounts(List<ProviderAccount>? accounts, bool clone)
    {
        var normalizedAccounts = new List<ProviderAccount>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenDisplayKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var providersWithDefault = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var account in accounts ?? [])
        {
            if (account is null ||
                string.IsNullOrWhiteSpace(account.Id) ||
                string.IsNullOrWhiteSpace(account.ProviderName) ||
                !seenIds.Add(account.Id.Trim()))
            {
                continue;
            }

            var normalizedAccount = clone ? account.Clone() : account;
            normalizedAccount.Id = normalizedAccount.Id.Trim();
            normalizedAccount.ProviderName = normalizedAccount.ProviderName.Trim();
            normalizedAccount.Label = normalizedAccount.Label.Trim();
            normalizedAccount.ConfigDir = normalizedAccount.ConfigDir.Trim();
            normalizedAccount.Email = normalizedAccount.Email.Trim();
            normalizedAccount.AccountUuid = normalizedAccount.AccountUuid.Trim();

            // Only one default account per provider; a default account never has a config dir.
            if (normalizedAccount.IsDefault)
            {
                if (providersWithDefault.Add(normalizedAccount.ProviderName))
                {
                    normalizedAccount.ConfigDir = string.Empty;
                }
                else
                {
                    normalizedAccount.IsDefault = false;
                }
            }

            // Keep DisplayKeys unique or two accounts' cards would clobber each other.
            if (!seenDisplayKeys.Add(normalizedAccount.DisplayKey))
            {
                var suffix = 2;
                var baseLabel = string.IsNullOrWhiteSpace(normalizedAccount.Label)
                    ? normalizedAccount.Id
                    : normalizedAccount.Label;
                while (!seenDisplayKeys.Add(normalizedAccount.DisplayKey))
                {
                    normalizedAccount.Label = $"{baseLabel} {suffix}";
                    suffix++;
                }
            }

            normalizedAccounts.Add(normalizedAccount);
        }

        if (!providersWithDefault.Contains(KnownProviders.Anthropic))
        {
            normalizedAccounts.Insert(0, ProviderAccount.CreateDefaultAnthropic());
        }

        return normalizedAccounts;
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

public sealed class AnthropicApiCreditsBalanceCache
{
    public decimal AmountCents { get; set; }

    public decimal? PendingInvoiceAmountCents { get; set; }

    public decimal? ExpiringAmountCents { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    public DateTimeOffset VerifiedAt { get; set; }

    public string OrganizationUuid { get; set; } = string.Empty;

    public string OrganizationName { get; set; } = string.Empty;

    public AnthropicApiCreditsBalanceCache Clone()
    {
        return new AnthropicApiCreditsBalanceCache
        {
            AmountCents = AmountCents,
            PendingInvoiceAmountCents = PendingInvoiceAmountCents,
            ExpiringAmountCents = ExpiringAmountCents,
            ExpiresAt = ExpiresAt,
            VerifiedAt = VerifiedAt,
            OrganizationUuid = OrganizationUuid,
            OrganizationName = OrganizationName
        };
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
