namespace AIUsageMonitor.Models;

public enum ClaudeBillingConnectionState { Connected, ReconnectRequired, TemporarilyUnavailable }

public sealed class ClaudeBillingConnection
{
    // Also names the isolated WebView profile. A new login gets a new ID so a late
    // request from an old session cannot update the replacement connection.
    public string Id { get; set; } = string.Empty;
    public string AccountUuid { get; set; } = string.Empty;
    public string OrganizationUuid { get; set; } = string.Empty;
    public string CookieHeaderProtected { get; set; } = string.Empty;
    public string UserAgent { get; set; } = string.Empty;
    public ClaudeBillingConnectionState State { get; set; }
    public string Message { get; set; } = string.Empty;
    public DateTimeOffset? LastCheckedAt { get; set; }
    public DateTimeOffset? RetryAfter { get; set; }

    public ClaudeBillingConnection Clone() => (ClaudeBillingConnection)MemberwiseClone();
}
