using System.IO;
using System.Collections.Concurrent;
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
    private static readonly ConcurrentDictionary<string, object> SettingsLocks = new(StringComparer.OrdinalIgnoreCase);
    private object SyncRoot => SettingsLocks.GetOrAdd(Path.GetFullPath(SettingsPath), _ => new object());

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
        lock (SyncRoot) return LoadCore();
    }

    private AppSettings LoadCore()
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
        lock (SyncRoot) SaveCore(settings);
    }

    public void Update(Action<AppSettings> update)
    {
        lock (SyncRoot)
        {
            var settings = LoadCore();
            update(settings);
            SaveCore(settings);
        }
    }

    public void SavePreservingAccounts(AppSettings settings)
    {
        lock (SyncRoot)
        {
            settings.CopyAccountSettingsFrom(LoadCore());
            SaveCore(settings);
        }
    }

    private void SaveCore(AppSettings settings)
    {
        settings.Normalize();
        var json = JsonSerializer.Serialize(settings, SerializerOptions);
        var temporary = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, json);
            File.Move(temporary, SettingsPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
