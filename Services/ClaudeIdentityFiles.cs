using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIUsageMonitor.Services;

// Shared read/write helpers for the cached `oauthAccount` identity block Claude Code keeps
// in .claude.json (the block `claude /status` displays). Every caller that touches that
// block goes through here so the "leave every other property of the file alone" and
// atomic-write rules only exist in one place.
//
// For the default setup the file is ~/.claude.json (profile root, next to the ~/.claude
// dir); for a CLAUDE_CONFIG_DIR it lives inside that dir.
internal static class ClaudeIdentityFiles
{
    public static string ClaudeJsonPathFor(string configDirectory) =>
        Path.Combine(configDirectory, ".claude.json");

    public static string? TryReadOAuthAccountUuid(string claudeJsonPath)
    {
        try
        {
            if (!File.Exists(claudeJsonPath))
            {
                return null;
            }

            return JsonNode.Parse(File.ReadAllText(claudeJsonPath)) is JsonObject root
                ? root["oauthAccount"]?["accountUuid"]?.GetValue<string>()
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    // Copies the `oauthAccount` identity block from one .claude.json into another, leaving
    // every other property of the destination file (projects, tips, onboarding state, ...)
    // untouched. Returns false when the source has no block to copy.
    public static bool TryCopyOAuthAccountBlock(string sourceClaudeJsonPath, string destinationClaudeJsonPath, out string? error)
    {
        error = null;

        try
        {
            if (!File.Exists(sourceClaudeJsonPath))
            {
                return false;
            }

            if (JsonNode.Parse(File.ReadAllText(sourceClaudeJsonPath)) is not JsonObject sourceRoot ||
                sourceRoot["oauthAccount"] is not JsonNode oauthAccount)
            {
                return false;
            }

            return TryWriteOAuthAccountBlock(destinationClaudeJsonPath, oauthAccount, out error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool TryWriteOAuthAccountBlock(string destinationClaudeJsonPath, JsonNode oauthAccount, out string? error)
    {
        error = null;

        try
        {
            JsonObject destinationRoot;
            if (File.Exists(destinationClaudeJsonPath) &&
                JsonNode.Parse(File.ReadAllText(destinationClaudeJsonPath)) is JsonObject existingRoot)
            {
                destinationRoot = existingRoot;
            }
            else
            {
                destinationRoot = [];
            }

            destinationRoot["oauthAccount"] = oauthAccount.DeepClone();

            var directory = Path.GetDirectoryName(destinationClaudeJsonPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = destinationClaudeJsonPath + ".tmp";
            File.WriteAllText(tempPath, destinationRoot.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tempPath, destinationClaudeJsonPath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            error = ex.Message;
            return false;
        }
    }

    // Atomic-enough credentials copy: write beside the destination first, then move over it,
    // so a crash mid-copy can never leave a half-written .credentials.json behind.
    public static bool TryCopyCredentials(string sourcePath, string destinationPath, out string? error)
    {
        error = null;

        try
        {
            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = destinationPath + ".tmp";
            File.Copy(sourcePath, tempPath, overwrite: true);
            File.Move(tempPath, destinationPath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }
}
