namespace AIUsageMonitor.Services;

internal static class AppMetadata
{
    public const string DisplayName = "Seth's AI Usage Monitor";
    public const string VersionText = "V1.04";
    public const string StartupEntryName = "SethsAIUsageMonitor";

    public static string DisplayNameWithVersion => $"{DisplayName} {VersionText}";
}
