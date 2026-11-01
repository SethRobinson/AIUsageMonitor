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

    private readonly AppLogService? _logService;

    public AppSettingsService(string? baseDirectory = null, AppLogService? logService = null)
    {
        _logService = logService;
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
        if (TryReadSettings(SettingsPath, out var settings, out var error))
        {
            return settings;
        }

        // A missing file is a first run (or a deliberate reset): start from defaults.
        if (error is not null)
        {
            settings = RecoverFromCorruptFile(error);
            SaveCore(settings);
            return settings;
        }

        var defaultSettings = new AppSettings();
        SaveCore(defaultSettings);
        return defaultSettings;
    }

    // A power loss can leave the file zero-filled (issue #2). Never let one bad file stop the
    // app from starting: set it aside, then recover from a leftover temp file from an
    // interrupted save, or fall back to defaults.
    private AppSettings RecoverFromCorruptFile(string error)
    {
        var quarantinePath = QuarantineCorruptFile();
        _logService?.Warning("Settings", $"Settings file was unreadable ({error}); moved it to {quarantinePath}.");

        foreach (var candidate in GetLeftoverTemporaryFiles())
        {
            if (TryReadSettings(candidate, out var recovered, out _))
            {
                _logService?.Warning("Settings", $"Recovered settings from {candidate}.");
                return recovered;
            }
        }

        _logService?.Warning("Settings", "No usable leftover settings file was found; starting with default settings.");
        return new AppSettings();
    }

    // Returns false with a null error when the file does not exist, and with an error message
    // when it exists but cannot be parsed. IO failures (for example a locked file) still throw,
    // so a transient read problem never causes a good file to be quarantined.
    private static bool TryReadSettings(string path, out AppSettings settings, out string? error)
    {
        settings = null!;
        error = null;
        if (!File.Exists(path))
        {
            return false;
        }

        var json = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(json.Replace('\0', ' ')))
        {
            error = json.Length == 0 ? "file is empty" : "file contains no data";
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
            if (parsed is null)
            {
                error = "file contains no settings object";
                return false;
            }

            parsed.Normalize();
            settings = parsed;
            return true;
        }
        // Only parsing and normalizing happen here, so any exception means the contents are bad.
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private string QuarantineCorruptFile()
    {
        var quarantinePath = $"{SettingsPath}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
        for (var suffix = 2; File.Exists(quarantinePath); suffix++)
        {
            quarantinePath = $"{SettingsPath}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}-{suffix}";
        }

        File.Move(SettingsPath, quarantinePath);
        return quarantinePath;
    }

    // Newest first, so an interrupted save wins over older leftovers.
    private IEnumerable<string> GetLeftoverTemporaryFiles()
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(SettingsPath));
        if (directory is null || !Directory.Exists(directory))
        {
            return [];
        }

        return new DirectoryInfo(directory)
            .EnumerateFiles(Path.GetFileName(SettingsPath) + ".*.tmp")
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Select(file => file.FullName)
            .ToList();
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
        WriteAtomically(SettingsPath, json);

        // V1.21 kept a permanent .bak copy; settings no longer leave any extra file behind.
        var legacyBackupPath = SettingsPath + ".bak";
        if (File.Exists(legacyBackupPath)) File.Delete(legacyBackupPath);
    }

    // Atomic write: flush the data to a temp file on disk, then rename it over the real file, so
    // a power loss cannot keep the rename while losing the contents, which is what left
    // zero-filled settings files behind (issue #2). No temp file remains once the write finishes.
    private static void WriteAtomically(string path, string contents)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(contents);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
