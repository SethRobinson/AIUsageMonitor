using System.IO;
using System.Text.Json;
using AIUsageMonitor.Models;

namespace AIUsageMonitor.Services;

public sealed class AppSettingsService
{
    private const string FileName = "monitor.settings.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true
    };

    public string SettingsPath { get; }

    public AppSettingsService(string? baseDirectory = null)
    {
        var currentDirectoryPath = Path.Combine(baseDirectory ?? Environment.CurrentDirectory, FileName);
        var outputDirectoryPath = baseDirectory is null
            ? Path.Combine(AppContext.BaseDirectory, FileName)
            : currentDirectoryPath;

        SettingsPath = File.Exists(outputDirectoryPath)
            ? outputDirectoryPath
            : currentDirectoryPath;
    }

    public AppSettings Load()
    {
        if (!File.Exists(SettingsPath))
        {
            var defaultSettings = new AppSettings();
            Save(defaultSettings);
            return defaultSettings;
        }

        var json = File.ReadAllText(SettingsPath);
        var settings = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions) ?? new AppSettings();
        settings.Normalize();
        return settings;
    }

    public void Save(AppSettings settings)
    {
        settings.Normalize();
        var json = JsonSerializer.Serialize(settings, SerializerOptions);
        File.WriteAllText(SettingsPath, json);
    }
}
