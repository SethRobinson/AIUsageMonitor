using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace AIUsageMonitor.Services;

internal static class AntigravityLauncherService
{
    public const string DownloadUrl = "https://antigravity.google/download";

    private const string CurrentExecutableName = "Antigravity.exe";
    private const string LegacyIdeExecutableName = "Antigravity IDE.exe";
    private const string UninstallSubKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string AppPathsSubKeyPath = @"Software\Microsoft\Windows\CurrentVersion\App Paths";

    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    public static AntigravityLaunchResult TryLaunch()
    {
        var executablePath = FindExecutablePath();
        if (executablePath is null)
        {
            return AntigravityLaunchResult.NotFound();
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(executablePath) ?? string.Empty
            };

            Process.Start(startInfo);
            return AntigravityLaunchResult.Started(executablePath);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or IOException or UnauthorizedAccessException)
        {
            return AntigravityLaunchResult.Failed(executablePath, ex);
        }
    }

    internal static string? FindExecutablePath()
    {
        return SelectExecutablePath(
            EnumerateKnownPathCandidates().Concat(EnumerateRegistryCandidates()),
            File.Exists);
    }

    internal static string? SelectExecutablePath(IEnumerable<string?> candidates, Func<string, bool> fileExists)
    {
        return candidates
            .Select((candidate, index) => new
            {
                Path = NormalizeCandidatePath(candidate),
                Index = index
            })
            .Where(candidate => candidate.Path is not null)
            .GroupBy(candidate => candidate.Path!, PathComparer)
            .Select(group => group.First())
            .Where(candidate => IsSupportedExecutablePath(candidate.Path!) && Exists(candidate.Path!, fileExists))
            .OrderBy(candidate => GetExecutablePriority(candidate.Path!))
            .ThenBy(candidate => candidate.Index)
            .Select(candidate => candidate.Path)
            .FirstOrDefault();
    }

    internal static string? ParseRegistryExecutablePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        foreach (var executableName in new[] { CurrentExecutableName, LegacyIdeExecutableName })
        {
            var executableIndex = trimmed.IndexOf(executableName, StringComparison.OrdinalIgnoreCase);
            if (executableIndex >= 0)
            {
                return NormalizeCandidatePath(trimmed[..(executableIndex + executableName.Length)]);
            }
        }

        if (trimmed.StartsWith('"'))
        {
            var closingQuoteIndex = trimmed.IndexOf('"', startIndex: 1);
            if (closingQuoteIndex > 1)
            {
                return NormalizeCandidatePath(trimmed[1..closingQuoteIndex]);
            }
        }

        var commaIndex = trimmed.IndexOf(',');
        return NormalizeCandidatePath(commaIndex >= 0 ? trimmed[..commaIndex] : trimmed);
    }

    private static IEnumerable<string?> EnumerateKnownPathCandidates()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            yield break;
        }

        yield return Path.Combine(localAppData, "Programs", "Antigravity", CurrentExecutableName);
        yield return Path.Combine(localAppData, "Programs", "Antigravity IDE", LegacyIdeExecutableName);
    }

    private static IEnumerable<string?> EnumerateRegistryCandidates()
    {
        foreach (var candidate in EnumerateUninstallRegistryCandidates(Registry.CurrentUser))
        {
            yield return candidate;
        }

        foreach (var candidate in EnumerateUninstallRegistryCandidates(Registry.LocalMachine))
        {
            yield return candidate;
        }

        foreach (var candidate in EnumerateUninstallRegistryCandidates(
            Registry.LocalMachine,
            @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"))
        {
            yield return candidate;
        }

        foreach (var candidate in EnumerateAppPathRegistryCandidates(Registry.CurrentUser))
        {
            yield return candidate;
        }

        foreach (var candidate in EnumerateAppPathRegistryCandidates(Registry.LocalMachine))
        {
            yield return candidate;
        }
    }

    private static IEnumerable<string?> EnumerateUninstallRegistryCandidates(
        RegistryKey root,
        string subKeyPath = UninstallSubKeyPath)
    {
        using var uninstallKey = root.OpenSubKey(subKeyPath, writable: false);
        if (uninstallKey is null)
        {
            yield break;
        }

        foreach (var subKeyName in uninstallKey.GetSubKeyNames())
        {
            using var appKey = uninstallKey.OpenSubKey(subKeyName, writable: false);
            if (appKey is null || !IsLikelyAntigravityEntry(appKey))
            {
                continue;
            }

            yield return ParseRegistryExecutablePath(appKey.GetValue("DisplayIcon") as string);

            if (appKey.GetValue("InstallLocation") is string installLocation &&
                !string.IsNullOrWhiteSpace(installLocation))
            {
                yield return Path.Combine(installLocation.Trim().Trim('"'), CurrentExecutableName);
                yield return Path.Combine(installLocation.Trim().Trim('"'), LegacyIdeExecutableName);
            }
        }
    }

    private static IEnumerable<string?> EnumerateAppPathRegistryCandidates(RegistryKey root)
    {
        foreach (var executableName in new[] { CurrentExecutableName, LegacyIdeExecutableName })
        {
            using var appPathKey = root.OpenSubKey(Path.Combine(AppPathsSubKeyPath, executableName), writable: false);
            if (appPathKey is null)
            {
                continue;
            }

            yield return ParseRegistryExecutablePath(appPathKey.GetValue(string.Empty) as string);
        }
    }

    private static bool IsLikelyAntigravityEntry(RegistryKey appKey)
    {
        return ContainsAntigravity(appKey.GetValue("DisplayName") as string) ||
            ContainsAntigravity(appKey.GetValue("DisplayIcon") as string) ||
            ContainsAntigravity(appKey.GetValue("InstallLocation") as string);
    }

    private static bool ContainsAntigravity(string? value)
    {
        return value?.IndexOf("Antigravity", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string? NormalizeCandidatePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var normalized = path.Trim().Trim('"');
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static bool IsSupportedExecutablePath(string path)
    {
        var fileName = Path.GetFileName(path);
        return string.Equals(fileName, CurrentExecutableName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, LegacyIdeExecutableName, StringComparison.OrdinalIgnoreCase);
    }

    private static int GetExecutablePriority(string path)
    {
        return string.Equals(Path.GetFileName(path), CurrentExecutableName, StringComparison.OrdinalIgnoreCase)
            ? 0
            : 1;
    }

    private static bool Exists(string path, Func<string, bool> fileExists)
    {
        try
        {
            return fileExists(path);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

internal enum AntigravityLaunchStatus
{
    Started,
    NotFound,
    Failed
}

internal sealed record AntigravityLaunchResult(
    AntigravityLaunchStatus Status,
    string? ExecutablePath,
    Exception? Exception)
{
    public static AntigravityLaunchResult Started(string executablePath)
    {
        return new AntigravityLaunchResult(AntigravityLaunchStatus.Started, executablePath, null);
    }

    public static AntigravityLaunchResult NotFound()
    {
        return new AntigravityLaunchResult(AntigravityLaunchStatus.NotFound, null, null);
    }

    public static AntigravityLaunchResult Failed(string executablePath, Exception exception)
    {
        return new AntigravityLaunchResult(AntigravityLaunchStatus.Failed, executablePath, exception);
    }
}
