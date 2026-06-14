using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace AIUsageMonitor.Services;

internal static class OpenAiCodexLauncherService
{
    public const string SetupUrl = "https://developers.openai.com/codex/quickstart";

    private const string CodexCommandName = "codex";
    private const string CodexProtocol = "codex";
    private const string CodexProtocolUrl = "codex://";

    public static OpenAiCodexLaunchResult TryLaunch()
    {
        var target = SelectLaunchTarget(FindCodexCommandPath(), IsCodexAppRegistered());
        if (target is null)
        {
            return OpenAiCodexLaunchResult.NotFound();
        }

        try
        {
            if (target.Kind == OpenAiCodexLaunchTargetKind.Cli)
            {
                LaunchCodexCliLogin(target.Value);
            }
            else
            {
                LaunchCodexApp();
            }

            return OpenAiCodexLaunchResult.Started(target);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or IOException or UnauthorizedAccessException)
        {
            return OpenAiCodexLaunchResult.Failed(target, ex);
        }
    }

    internal static OpenAiCodexLaunchTarget? SelectLaunchTarget(
        string? codexCommandPath,
        bool isCodexAppRegistered)
    {
        if (!string.IsNullOrWhiteSpace(codexCommandPath))
        {
            return new OpenAiCodexLaunchTarget(
                OpenAiCodexLaunchTargetKind.Cli,
                codexCommandPath,
                "Codex CLI");
        }

        return isCodexAppRegistered
            ? new OpenAiCodexLaunchTarget(
                OpenAiCodexLaunchTargetKind.WindowsApp,
                CodexProtocolUrl,
                "Codex app")
            : null;
    }

    internal static string? FindCodexCommandPath()
    {
        return FindCommandPath(CodexCommandName);
    }

    internal static bool IsCodexAppRegistered()
    {
        return IsProtocolRegistered(Registry.CurrentUser) ||
            IsProtocolRegistered(Registry.LocalMachine) ||
            IsProtocolRegistered(Registry.ClassesRoot, subKeyPath: CodexProtocol);
    }

    private static void LaunchCodexCliLogin(string commandPath)
    {
        var shellPath = FindCommandPath("pwsh") ?? FindCommandPath("powershell") ?? "powershell.exe";
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var startInfo = new ProcessStartInfo
        {
            FileName = shellPath,
            WorkingDirectory = Directory.Exists(userProfile)
                ? userProfile
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            UseShellExecute = true
        };

        startInfo.ArgumentList.Add("-NoExit");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add($"& '{EscapePowerShellSingleQuotedString(commandPath)}' login");

        Process.Start(startInfo);
    }

    private static void LaunchCodexApp()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = CodexProtocolUrl,
            UseShellExecute = true
        });
    }

    private static string? FindCommandPath(string command)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var extensions = OperatingSystem.IsWindows()
            ? new[] { ".exe", ".cmd", ".bat", ".ps1", string.Empty }
            : new[] { string.Empty };

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, command + extension);
                if (Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static bool IsProtocolRegistered(RegistryKey root, string subKeyPath = $@"Software\Classes\{CodexProtocol}")
    {
        using var key = root.OpenSubKey(subKeyPath, writable: false);
        if (key is null)
        {
            return false;
        }

        return key.GetValue("URL Protocol") is not null ||
            string.Equals(key.GetValue(string.Empty) as string, $"URL:{CodexProtocol}", StringComparison.OrdinalIgnoreCase);
    }

    private static bool Exists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string EscapePowerShellSingleQuotedString(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }
}

internal enum OpenAiCodexLaunchTargetKind
{
    Cli,
    WindowsApp
}

internal sealed record OpenAiCodexLaunchTarget(
    OpenAiCodexLaunchTargetKind Kind,
    string Value,
    string DisplayName);

internal enum OpenAiCodexLaunchStatus
{
    Started,
    NotFound,
    Failed
}

internal sealed record OpenAiCodexLaunchResult(
    OpenAiCodexLaunchStatus Status,
    OpenAiCodexLaunchTarget? Target,
    Exception? Exception)
{
    public static OpenAiCodexLaunchResult Started(OpenAiCodexLaunchTarget target)
    {
        return new OpenAiCodexLaunchResult(OpenAiCodexLaunchStatus.Started, target, null);
    }

    public static OpenAiCodexLaunchResult NotFound()
    {
        return new OpenAiCodexLaunchResult(OpenAiCodexLaunchStatus.NotFound, null, null);
    }

    public static OpenAiCodexLaunchResult Failed(OpenAiCodexLaunchTarget target, Exception exception)
    {
        return new OpenAiCodexLaunchResult(OpenAiCodexLaunchStatus.Failed, target, exception);
    }
}
