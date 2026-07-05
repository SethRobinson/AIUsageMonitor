using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIUsageMonitor.Services;

public sealed class ClaudeStatusExporterService
{
    private const string ScriptName = "ai-usage-monitor-statusline.ps1";
    private const string LegacyScriptName = "apimonitor-statusline.ps1";
    private const string OutputName = "ai-usage-monitor-usage.json";
    private readonly AppLogService _logService;

    public ClaudeStatusExporterService(AppLogService logService)
    {
        _logService = logService;
        var claudeDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        ClaudeDirectory = claudeDirectory;
        ScriptPath = Path.Combine(claudeDirectory, ScriptName);
        OutputPath = Path.Combine(claudeDirectory, OutputName);
        SettingsPath = Path.Combine(claudeDirectory, "settings.json");
    }

    public string ClaudeDirectory { get; }

    public string ScriptPath { get; }

    public string OutputPath { get; }

    public string SettingsPath { get; }

    public bool EnsureInstalled()
    {
        Directory.CreateDirectory(ClaudeDirectory);
        File.WriteAllText(ScriptPath, BuildScript(OutputPath), new UTF8Encoding(false));

        var root = LoadSettings();
        var statusLine = root["statusLine"] as JsonObject;
        var command = statusLine?["command"]?.GetValue<string>() ?? string.Empty;
        var alreadyConfigured = statusLine is not null &&
            (command.Contains(ScriptName, StringComparison.OrdinalIgnoreCase) ||
             command.Contains(LegacyScriptName, StringComparison.OrdinalIgnoreCase));

        if (statusLine is not null &&
            !alreadyConfigured)
        {
            _logService.Warning("Anthropic", "Claude Code already has a custom statusLine. Seth's AI Usage Monitor did not overwrite it.");
            return false;
        }

        root["statusLine"] = new JsonObject
        {
            ["type"] = "command",
            ["command"] = $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{ToClaudeCommandPath(ScriptPath)}\"",
            ["refreshInterval"] = 30
        };

        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json, new UTF8Encoding(false));
        if (!alreadyConfigured)
        {
            _logService.Info("Anthropic", $"Claude status exporter installed at {ScriptPath}.");
        }

        return true;
    }

    private JsonObject LoadSettings()
    {
        if (!File.Exists(SettingsPath))
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(SettingsPath)) as JsonObject ?? new JsonObject();
        }
        catch (JsonException ex)
        {
            var backupPath = SettingsPath + ".ai-usage-monitor-bad-json.bak";
            File.Copy(SettingsPath, backupPath, overwrite: true);
            _logService.Warning("Anthropic", $"Claude settings JSON was invalid and was backed up to {backupPath}: {ex.Message}");
            return new JsonObject();
        }
    }

    private static string ToClaudeCommandPath(string path)
    {
        return path.Replace('\\', '/');
    }

    private static string EscapePowerShellSingleQuotedString(string value)
    {
        return value.Replace("'", "''");
    }

    private static string BuildScript(string outputPath)
    {
        var escapedOutputPath = EscapePowerShellSingleQuotedString(outputPath);

        return $$"""
$inputJson = [Console]::In.ReadToEnd()
$outputPath = '{{escapedOutputPath}}'

function Get-Prop($obj, $name) {
    if ($null -eq $obj) {
        return $null
    }

    $prop = $obj.PSObject.Properties[$name]
    if ($null -eq $prop) {
        return $null
    }

    return $prop.Value
}

function Get-FirstProp($obj, [string[]]$names) {
    foreach ($name in $names) {
        $value = Get-Prop $obj $name
        if ($null -ne $value) {
            return $value
        }
    }

    return $null
}

function Format-Left($label, $window) {
    if ($null -eq $window) {
        return $null
    }

    $used = Get-Prop $window 'used_percentage'
    if ($null -eq $used) {
        $used = Get-Prop $window 'used_percent'
    }

    if ($null -eq $used) {
        return $null
    }

    $left = [Math]::Max(0, 100 - [double]$used)
    return ('{0} {1}% left' -f $label, [Math]::Round($left))
}

try {
    $data = $inputJson | ConvertFrom-Json
    $rateLimits = Get-Prop $data 'rate_limits'
    $extraUsage = Get-FirstProp $data @('extra_usage', 'extraUsage')
    $subscriptionType = Get-FirstProp $data @('subscriptionType', 'subscription_type')
    $rateLimitTier = Get-FirstProp $data @('rateLimitTier', 'rate_limit_tier')
    if ($null -eq $rateLimitTier) {
        $rateLimitTier = Get-FirstProp $rateLimits @('rateLimitTier', 'rate_limit_tier')
    }

    $model = Get-Prop (Get-Prop $data 'model') 'display_name'
    if ([string]::IsNullOrWhiteSpace($model)) {
        $model = 'Claude'
    }

    $status = 'ok'
    $message = 'Claude quota exported from status line.'
    if ($null -eq $rateLimits) {
        $status = 'missing_rate_limits'
        $message = 'Claude status line ran, but rate_limits was absent. It appears only for Claude.ai subscribers after the first API response.'
    }

    $export = [ordered]@{
        generatedAt = (Get-Date).ToUniversalTime().ToString('o')
        source = 'Claude Code statusLine'
        status = $status
        statusMessage = $message
        model = $model
        subscriptionType = $subscriptionType
        rateLimitTier = $rateLimitTier
        session_id = Get-Prop $data 'session_id'
        rate_limits = $rateLimits
        extra_usage = $extraUsage
    }

    $export | ConvertTo-Json -Depth 64 | Set-Content -LiteralPath $outputPath -Encoding UTF8

    $fiveHourText = Format-Left '5h' (Get-Prop $rateLimits 'five_hour')
    $sevenDayText = Format-Left '7d' (Get-Prop $rateLimits 'seven_day')
    $parts = @($fiveHourText, $sevenDayText) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    if ($parts.Count -gt 0) {
        Write-Output ('[{0}] Claude {1}' -f $model, ($parts -join ' | '))
    } else {
        Write-Output ('[{0}] Claude usage pending' -f $model)
    }
} catch {
    try {
        $export = [ordered]@{
            generatedAt = (Get-Date).ToUniversalTime().ToString('o')
            source = 'Claude Code statusLine'
            status = 'error'
            statusMessage = $_.Exception.Message
        }
        $export | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $outputPath -Encoding UTF8
    } catch {
    }

    Write-Output 'Claude usage export error'
}
""";
    }
}
