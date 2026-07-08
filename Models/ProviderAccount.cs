namespace AIUsageMonitor.Models;

public sealed class ProviderAccount
{
    public const string DefaultAnthropicAccountId = "anthropic-default";
    public const string DefaultAccountLabel = "Default";

    public string Id { get; set; } = string.Empty;

    public string ProviderName { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public bool IsDefault { get; set; }

    public string ConfigDir { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string AccountUuid { get; set; } = string.Empty;

    // Single source of truth for the per-account provider key used by the aggregator,
    // failure states, checking placeholders, and card upserts. The default account keeps
    // the bare provider name while unnamed (so existing installs see no change) but honors
    // a custom label like any other account.
    public string DisplayKey => IsDefault
        ? string.IsNullOrWhiteSpace(Label) || string.Equals(Label.Trim(), DefaultAccountLabel, StringComparison.OrdinalIgnoreCase)
            ? ProviderName
            : $"{ProviderName} - {Label.Trim()}"
        : $"{ProviderName} - {(string.IsNullOrWhiteSpace(Label) ? Id : Label.Trim())}";

    public ProviderAccount Clone()
    {
        return new ProviderAccount
        {
            Id = Id,
            ProviderName = ProviderName,
            Label = Label,
            Enabled = Enabled,
            IsDefault = IsDefault,
            ConfigDir = ConfigDir,
            Email = Email,
            AccountUuid = AccountUuid
        };
    }

    public static ProviderAccount CreateDefaultAnthropic()
    {
        return new ProviderAccount
        {
            Id = DefaultAnthropicAccountId,
            ProviderName = KnownProviders.Anthropic,
            Label = DefaultAccountLabel,
            Enabled = true,
            IsDefault = true,
            ConfigDir = string.Empty
        };
    }
}
