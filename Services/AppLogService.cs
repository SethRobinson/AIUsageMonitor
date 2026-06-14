using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using AIUsageMonitor.Models;

namespace AIUsageMonitor.Services;

public sealed class AppLogService
{
    private const string FileName = "monitor.log.jsonl";
    private const int MaxEntriesInMemory = 300;
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };
    private readonly object _entriesLock = new();
    private readonly object _fileLock = new();

    public AppLogService(string? baseDirectory = null)
    {
        LogPath = Path.Combine(baseDirectory ?? Environment.CurrentDirectory, FileName);
        LoadRecentEntries();
    }

    public string LogPath { get; }

    public ObservableCollection<AppLogEntry> Entries { get; } = [];

    public int RecentErrorCount => RunOnEntriesThread(() =>
    {
        lock (_entriesLock)
        {
            return Entries.Count(entry => entry.Level is "Error" or "Warning");
        }
    });

    public void Info(string source, string message) => Add("Info", source, message);

    public void Warning(string source, string message) => Add("Warning", source, message);

    public void Error(string source, string message) => Add("Error", source, message);

    public void Clear()
    {
        RunOnEntriesThread(() =>
        {
            lock (_entriesLock)
            {
                Entries.Clear();
            }
        });

        try
        {
            lock (_fileLock)
            {
                File.WriteAllText(LogPath, string.Empty);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void Add(string level, string source, string message)
    {
        var entry = new AppLogEntry
        {
            Timestamp = DateTimeOffset.Now,
            Level = level,
            Source = source,
            Message = message
        };

        RunOnEntriesThread(() => AddEntryToMemory(entry));

        try
        {
            lock (_fileLock)
            {
                File.AppendAllText(LogPath, JsonSerializer.Serialize(entry, SerializerOptions) + Environment.NewLine);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void AddEntryToMemory(AppLogEntry entry)
    {
        lock (_entriesLock)
        {
            Entries.Insert(0, entry);

            while (Entries.Count > MaxEntriesInMemory)
            {
                Entries.RemoveAt(Entries.Count - 1);
            }
        }
    }

    private void LoadRecentEntries()
    {
        if (!File.Exists(LogPath))
        {
            return;
        }

        try
        {
            var lines = File.ReadLines(LogPath)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .TakeLast(MaxEntriesInMemory)
                .ToList();

            for (var index = lines.Count - 1; index >= 0; index--)
            {
                var entry = JsonSerializer.Deserialize<AppLogEntry>(lines[index]);
                if (entry is not null)
                {
                    lock (_entriesLock)
                    {
                        Entries.Add(entry);
                    }
                }
            }
        }
        catch
        {
            lock (_entriesLock)
            {
                Entries.Clear();
            }
        }
    }

    private static void RunOnEntriesThread(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null &&
            !dispatcher.CheckAccess() &&
            !dispatcher.HasShutdownStarted &&
            !dispatcher.HasShutdownFinished)
        {
            dispatcher.Invoke(action);
            return;
        }

        action();
    }

    private static T RunOnEntriesThread<T>(Func<T> action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null &&
            !dispatcher.CheckAccess() &&
            !dispatcher.HasShutdownStarted &&
            !dispatcher.HasShutdownFinished)
        {
            return dispatcher.Invoke(action);
        }

        return action();
    }
}
