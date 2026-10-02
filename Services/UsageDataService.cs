using System.IO;
using System.Text.Json;
using AIUsageMonitor.Models;

namespace AIUsageMonitor.Services;

public sealed class UsageDataService
{
    private const string FileName = "usage.fake.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public string DataPath { get; }

    public UsageDataService()
    {
        var currentDirectoryPath = Path.Combine(Environment.CurrentDirectory, FileName);
        var outputDirectoryPath = Path.Combine(AppContext.BaseDirectory, FileName);

        DataPath = File.Exists(currentDirectoryPath) || !File.Exists(outputDirectoryPath)
            ? currentDirectoryPath
            : outputDirectoryPath;
    }

    public UsageSnapshot Load()
    {
        if (!File.Exists(DataPath))
        {
            throw new FileNotFoundException($"Could not find fake usage data at {DataPath}.", DataPath);
        }

        var json = File.ReadAllText(DataPath);
        var snapshot = JsonSerializer.Deserialize<UsageSnapshot>(json, SerializerOptions)
            ?? throw new InvalidDataException("Fake usage data was empty or invalid.");

        Validate(snapshot);
        return snapshot;
    }

    private static void Validate(UsageSnapshot snapshot)
    {
        if (snapshot.Providers.Count == 0)
        {
            throw new InvalidDataException("Fake usage data must contain at least one provider.");
        }

        foreach (var provider in snapshot.Providers)
        {
            if (string.IsNullOrWhiteSpace(provider.Name))
            {
                throw new InvalidDataException("Every provider needs a name.");
            }

            if (provider.Windows.Count == 0 && !provider.IsUnavailable)
            {
                throw new InvalidDataException($"{provider.Name} must include at least one usage window.");
            }

            foreach (var window in provider.Windows)
            {
                ValidateWindow(provider.Name, window);
            }
        }
    }

    private static void ValidateWindow(string providerName, UsageWindow window)
    {
        var windowName = string.IsNullOrWhiteSpace(window.Title) ? "usage" : window.Title;

        if (!window.IsBalance && window.Limit <= 0)
        {
            throw new InvalidDataException($"{providerName} {windowName} limit must be greater than zero.");
        }

        if (window.Used < 0)
        {
            throw new InvalidDataException($"{providerName} {windowName} used value cannot be negative.");
        }

        if (!window.IsBalance && window.Used > window.Limit)
        {
            throw new InvalidDataException($"{providerName} {windowName} used value cannot exceed the limit.");
        }

        if (window.EffectiveRemaining < 0 || (!window.IsBalance && window.EffectiveRemaining > window.Limit))
        {
            throw new InvalidDataException($"{providerName} {windowName} remaining value is outside the valid range.");
        }

        _ = window.ResetAt;
    }
}
