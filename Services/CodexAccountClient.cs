using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using AIUsageMonitor.Collectors;

namespace AIUsageMonitor.Services;

public interface ICodexAccountClient
{
    Task<CodexAccountSnapshot> ReadAsync(string configDirectory, CancellationToken cancellationToken);
    Task LoginAsync(string configDirectory, Action<Uri> openBrowser, CancellationToken cancellationToken);
}

public sealed record CodexAccountSnapshot(string Email, string PlanType, JsonElement RateLimits);

// Only account RPCs are used: no threads, turns, prompts, or global login changes.
public sealed class CodexAccountClient : ICodexAccountClient
{
    private readonly Func<string, CodexRpcConnection> _start;
    public CodexAccountClient() : this(Start) { }
    internal CodexAccountClient(Func<string, CodexRpcConnection> start) { _start = start; }

    public async Task<CodexAccountSnapshot> ReadAsync(string configDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var rpc = _start(configDirectory);
        await rpc.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var account = await rpc.RequestAsync("account/read", new { refreshToken = false }, cancellationToken).ConfigureAwait(false);
        if (!account.TryGetProperty("account", out var info) || info.ValueKind != JsonValueKind.Object ||
            JsonString(info, "type") != "chatgpt")
            throw new InvalidOperationException("OpenAI login expired or is not a ChatGPT subscription. Use Accounts > Log in again.");
        JsonElement limits;
        try { limits = await rpc.RequestAsync("account/rateLimits/read", new { }, cancellationToken).ConfigureAwait(false); }
        catch (CodexAccountRequestException ex) when (ex.AuthenticationFailed)
        {
            // Let Codex renew its managed OAuth session, then retry the account request once.
            // No token exchange is implemented here and no global auth file is involved.
            await rpc.RequestAsync("account/read", new { refreshToken = true }, cancellationToken).ConfigureAwait(false);
            limits = await rpc.RequestAsync("account/rateLimits/read", new { }, cancellationToken).ConfigureAwait(false);
        }
        return new CodexAccountSnapshot(JsonString(info, "email"), JsonString(info, "planType"), limits);
    }

    public async Task LoginAsync(string configDirectory, Action<Uri> openBrowser, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var rpc = _start(configDirectory);
        await rpc.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var login = await rpc.RequestAsync("account/login/start", new { type = "chatgpt" }, cancellationToken).ConfigureAwait(false);
        var loginId = JsonString(login, "loginId");
        if (string.IsNullOrWhiteSpace(loginId) || !Uri.TryCreate(JsonString(login, "authUrl"), UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" || !(uri.Host == "chatgpt.com" || uri.Host == "auth.openai.com"))
            throw new InvalidOperationException("Codex returned an unsupported login link. Update the Codex CLI and try again.");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            openBrowser(uri);
            var completed = await rpc.WaitForNotificationAsync("account/login/completed", loginId, cancellationToken).ConfigureAwait(false);
            if (!completed.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
                throw new InvalidOperationException("OpenAI sign-in failed. Please retry the browser login.");
        }
        catch (OperationCanceledException)
        {
            // Best effort cancellation; disposing also terminates the isolated callback server.
            using var cancelTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try { await rpc.RequestAsync("account/login/cancel", new { loginId }, cancelTimeout.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException) { }
            throw;
        }
    }

    private static CodexRpcConnection Start(string configDirectory)
    {
        var path = CommandPathResolver.FindCommandPath("codex") ??
            throw new InvalidOperationException("Codex CLI not installed or not on PATH. Install the Codex CLI to monitor saved OpenAI accounts.");
        var process = new Process { StartInfo = BuildStartInfo(path, configDirectory) };
        try
        {
            process.Start();
            return new CodexRpcConnection(process);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            process.Dispose();
            throw new InvalidOperationException("Could not start the Codex CLI. Check its installation and try again.");
        }
    }

    internal static ProcessStartInfo BuildStartInfo(string commandPath, string configDirectory)
    {
        var info = new ProcessStartInfo
        {
            FileName = commandPath,
            WorkingDirectory = configDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        // PowerShell invokes Windows npm shims without constructing a cmd command string.
        if (Path.GetExtension(commandPath).ToLowerInvariant() is ".cmd" or ".bat" or ".ps1")
        {
            info.FileName = CommandPathResolver.FindCommandPath("pwsh") ?? "powershell.exe";
            foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command",
                "& '" + commandPath.Replace("'", "''") + "' app-server --listen stdio:// -c 'cli_auth_credentials_store=\"file\"'" })
                info.ArgumentList.Add(arg);
        }
        else
        {
            foreach (var arg in new[] { "app-server", "--listen", "stdio://", "-c", "cli_auth_credentials_store=\"file\"" })
                info.ArgumentList.Add(arg);
        }
        info.Environment["CODEX_HOME"] = Path.GetFullPath(configDirectory);
        // A parent Codex task can export auth overrides. Saved accounts must use their own login.
        foreach (var key in new[] { "CODEX_API_KEY", "OPENAI_API_KEY", "CODEX_ACCESS_TOKEN", "OPENAI_BASE_URL" })
            info.Environment.Remove(key);
        info.Environment["NO_COLOR"] = "1";
        return info;
    }

    internal static string JsonString(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty : string.Empty;
}

// Sequential account requests, with notifications buffered because login completion may
// arrive before the response to account/login/start. Never log raw RPC payloads or stderr.
internal sealed class CodexRpcConnection : IDisposable
{
    private readonly TextReader _reader;
    private readonly TextWriter _writer;
    private readonly Process? _process;
    private readonly Task? _stderr;
    private readonly Queue<JsonElement> _notifications = new();
    private int _requestId;

    internal CodexRpcConnection(TextReader reader, TextWriter writer) { _reader = reader; _writer = writer; }
    internal CodexRpcConnection(Process process) : this(process.StandardOutput, process.StandardInput)
    {
        _process = process;
        _stderr = DrainAsync(process.StandardError);
    }

    public async Task InitializeAsync(CancellationToken token)
    {
        await RequestAsync("initialize", new { clientInfo = new { name = "seths_ai_usage_monitor", title = AppMetadata.DisplayName, version = AppMetadata.VersionText } }, token).ConfigureAwait(false);
        await WriteAsync(new { method = "initialized" }, token).ConfigureAwait(false);
    }

    public async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken token)
    {
        var id = ++_requestId;
        await WriteAsync(new { id, method, @params = parameters }, token).ConfigureAwait(false);
        while (true)
        {
            var message = await ReadAsync(token).ConfigureAwait(false);
            if (!message.TryGetProperty("id", out var responseId))
            {
                if (_notifications.Count < 100) _notifications.Enqueue(message);
                continue;
            }
            if (responseId.ValueKind != JsonValueKind.Number || !responseId.TryGetInt32(out var number) || number != id) continue;
            if (message.TryGetProperty("error", out var error))
            {
                var code = error.TryGetProperty("code", out var codeValue) && codeValue.TryGetInt32(out var parsed) ? parsed : 0;
                var errorMessage = CodexAccountClient.JsonString(error, "message");
                var authenticationFailed = code == 401 || errorMessage.Contains("401", StringComparison.Ordinal) ||
                    errorMessage.Contains("unauthorized", StringComparison.OrdinalIgnoreCase);
                throw new CodexAccountRequestException(code == -32601
                    ? "This Codex CLI does not support account usage reads. Update the Codex CLI."
                    : "Codex could not read this OpenAI account. Retry, or use Accounts > Log in again if the login expired.", authenticationFailed);
            }
            if (!message.TryGetProperty("result", out var result)) throw new InvalidOperationException("Codex returned an incomplete account response.");
            return result.Clone();
        }
    }

    public async Task<JsonElement> WaitForNotificationAsync(string method, string loginId, CancellationToken token)
    {
        while (true)
        {
            var message = _notifications.Count > 0 ? _notifications.Dequeue() : await ReadAsync(token).ConfigureAwait(false);
            if (CodexAccountClient.JsonString(message, "method") == method && message.TryGetProperty("params", out var parameters) &&
                CodexAccountClient.JsonString(parameters, "loginId") == loginId) return parameters.Clone();
        }
    }

    private async Task WriteAsync(object message, CancellationToken token)
    {
        await _writer.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), token).ConfigureAwait(false);
        await _writer.FlushAsync(token).ConfigureAwait(false);
    }

    private async Task<JsonElement> ReadAsync(CancellationToken token)
    {
        var line = await _reader.ReadLineAsync(token).ConfigureAwait(false);
        if (line is null) throw new InvalidOperationException("Codex closed the account connection. Check the CLI installation and retry.");
        try
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
            return document.RootElement.Clone();
        }
        catch (JsonException) { throw new InvalidOperationException("Codex returned an unreadable account response. Update the Codex CLI and retry."); }
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        var buffer = new char[2048];
        try { while (await reader.ReadAsync(buffer).ConfigureAwait(false) > 0) { } }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    public void Dispose()
    {
        if (_process is null) return;
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            _process.WaitForExit(2000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        _process.Dispose();
    }
}

internal sealed class CodexAccountRequestException(string message, bool authenticationFailed) : InvalidOperationException(message)
{
    public bool AuthenticationFailed { get; } = authenticationFailed;
}
