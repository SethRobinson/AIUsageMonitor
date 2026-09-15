using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AIUsageMonitor.Services;

internal static class OpenAiIdentityFiles
{
    // These claims are identity metadata from a CLI-owned login, not authorization decisions.
    // Include user AND workspace: coworkers can share a workspace while having distinct quotas.
    public static string ReadIdentity(string directory)
    {
        try
        {
            using var auth = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "auth.json")));
            var tokens = auth.RootElement.GetProperty("tokens");
            var workspace = CodexAccountClient.JsonString(tokens, "account_id");
            var encoded = CodexAccountClient.JsonString(tokens, "id_token").Split('.');
            if (encoded.Length < 2 || string.IsNullOrWhiteSpace(CodexAccountClient.JsonString(tokens, "access_token"))) return string.Empty;
            var payload = encoded[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
            using var claims = JsonDocument.Parse(Convert.FromBase64String(payload));
            var user = CodexAccountClient.JsonString(claims.RootElement, "sub");
            if (claims.RootElement.TryGetProperty("https://api.openai.com/auth", out var details))
            {
                var userId = CodexAccountClient.JsonString(details, "chatgpt_user_id");
                if (!string.IsNullOrWhiteSpace(userId)) user = userId;
                if (string.IsNullOrWhiteSpace(workspace)) workspace = CodexAccountClient.JsonString(details, "chatgpt_account_id");
            }
            return string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(workspace) ? string.Empty
                : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user + "\0" + workspace)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or FormatException or InvalidOperationException)
        { return string.Empty; }
    }

    public static void InstallAuth(string sourceDirectory, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);
        var temporary = Path.Combine(targetDirectory, $"auth-{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(Path.Combine(sourceDirectory, "auth.json"), temporary);
            File.Move(temporary, Path.Combine(targetDirectory, "auth.json"), overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
