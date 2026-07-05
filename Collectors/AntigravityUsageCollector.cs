using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIUsageMonitor.Models;
using Microsoft.Data.Sqlite;

namespace AIUsageMonitor.Collectors;

public sealed class AntigravityUsageCollector : IUsageCollector
{
    private const string ServiceName = "exa.language_server_pb.LanguageServerService";
    private const string CsrfHeader = "x-codeium-csrf-token";
    private const int LogTailCharacters = 256 * 1024;
    private static readonly TimeSpan QuotaDiscoveryTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan TemporaryServerTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan TemporaryServerPollInterval = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan CachedStateMaxAge = TimeSpan.FromDays(8);
    private static readonly bool EnableTemporaryLanguageServerProbe = true;

    private static readonly HttpClient HttpClient = new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (request, _, _, errors) =>
            request.RequestUri?.IsLoopback == true || errors == System.Net.Security.SslPolicyErrors.None
    })
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    private static readonly Regex LocalUrlRegex = new(
        @"https://127\.0\.0\.1:(?<port>\d{2,5})/",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex CsrfTokenRegex = new(
        @"""csrfToken""\s*:\s*""(?<token>[^""]+)""|""csrf_token""\s*:\s*""(?<token>[^""]+)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex CliCsrfTokenRegex = new(
        @"--csrf[_-]token\s+(?<token>\S+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex OverrideIdeVersionRegex = new(
        @"--override_ide_version\s+(?<version>\S+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex ResourceExhaustedResetRegex = new(
        @"RESOURCE_EXHAUSTED.*?Resets?\s+in\s+(?<duration>(?:\d+\s*[dhms]\s*)+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex LogTimestampRegex = new(
        @"^[IWEF](?<month>\d{2})(?<day>\d{2})\s+(?<time>\d{2}:\d{2}:\d{2})(?:\.(?<fraction>\d+))?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DurationPartRegex = new(
        @"(?<value>\d+)\s*(?<unit>[dhms])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public string ProviderName => KnownProviders.Antigravity;

    public async Task<ProviderUsage> CollectAsync(CancellationToken cancellationToken)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var usage = await TryCollectQuotaAsync(home, cancellationToken);
        if (usage is not null)
        {
            return usage;
        }

        if (EnableTemporaryLanguageServerProbe)
        {
            usage = await TryCollectFromTemporaryLanguageServerAsync(cancellationToken);
            if (usage is not null)
            {
                return usage;
            }
        }

        usage = TryCollectCachedStateUsage();
        if (usage is not null)
        {
            return usage;
        }

        var latestLogExhaustion = TryGetLatestLogExhaustion(home);
        if (latestLogExhaustion is not null &&
            latestLogExhaustion.ResetAt > DateTimeOffset.Now.AddMinutes(-1))
        {
            return BuildLogExhaustionUsage(latestLogExhaustion);
        }

        return BuildUnavailableUsage(latestLogExhaustion);
    }

    private static async Task<ProviderUsage?> TryCollectQuotaAsync(string home, CancellationToken cancellationToken)
    {
        using var discoveryCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        discoveryCts.CancelAfter(QuotaDiscoveryTimeout);

        try
        {
            foreach (var endpoint in await FindEndpointsAsync(home, discoveryCts.Token))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var usage = await TryCollectEndpointAsync(endpoint, discoveryCts.Token);
                if (usage is not null)
                {
                    return usage;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        return null;
    }

    private static async Task<ProviderUsage?> TryCollectEndpointAsync(
        Endpoint endpoint,
        CancellationToken cancellationToken)
    {
        using var userStatus = await TryPostAsync(endpoint, "GetUserStatus", cancellationToken);
        if (userStatus is not null)
        {
            var usage = TryParseUserStatus(userStatus.RootElement, endpoint);
            if (usage is not null)
            {
                return usage;
            }
        }

        using var cascadeModels = await TryPostAsync(endpoint, "GetCascadeModelConfigs", cancellationToken);
        if (cascadeModels is not null)
        {
            var usage = TryParseModelConfigs(cascadeModels.RootElement, endpoint, "Antigravity");
            if (usage is not null)
            {
                return usage;
            }
        }

        using var commandModels = await TryPostAsync(endpoint, "GetCommandModelConfigs", cancellationToken);
        if (commandModels is not null)
        {
            var usage = TryParseModelConfigs(commandModels.RootElement, endpoint, "Antigravity");
            if (usage is not null)
            {
                return usage;
            }
        }

        using var availableModels = await TryPostAsync(endpoint, "GetAvailableModels", cancellationToken);
        if (availableModels is not null)
        {
            var usage = TryParseModelConfigs(availableModels.RootElement, endpoint, "Antigravity");
            if (usage is not null)
            {
                return usage;
            }
        }

        return null;
    }

    private static async Task<ProviderUsage?> TryCollectFromTemporaryLanguageServerAsync(CancellationToken cancellationToken)
    {
        var languageServerPath = FindLanguageServerPath();
        if (languageServerPath is null)
        {
            return null;
        }

        var port = GetAvailableLoopbackPort();
        if (port is null)
        {
            return null;
        }

        var csrfToken = Guid.NewGuid().ToString();
        var ideVersion = GetAntigravityIdeVersion(languageServerPath);
        using var process = new Process
        {
            StartInfo = BuildLanguageServerStartInfo(languageServerPath, port.Value, csrfToken, ideVersion)
        };

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }

        try
        {
            var endpoint = new Endpoint(
                new Uri($"https://127.0.0.1:{port.Value}/"),
                csrfToken,
                $"Temporary Antigravity language server (https://127.0.0.1:{port.Value}/)");
            var deadline = DateTimeOffset.UtcNow.Add(TemporaryServerTimeout);

            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (process.HasExited)
                {
                    return null;
                }

                var usage = await TryCollectEndpointAsync(endpoint, cancellationToken);
                if (usage is not null)
                {
                    return usage;
                }

                await Task.Delay(TemporaryServerPollInterval, cancellationToken);
            }

            return null;
        }
        finally
        {
            TryKill(process);
        }
    }

    private static string? FindLanguageServerPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new[]
        {
            Path.Combine(localAppData, "Programs", "Antigravity", "resources", "bin", "language_server.exe"),
            Path.Combine(localAppData, "Programs", "Antigravity IDE", "resources", "bin", "language_server.exe")
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static int? GetAvailableLoopbackPort()
    {
        var activePorts = GetLoopbackListenerPorts().ToHashSet();
        for (var port = 20184; port <= 20299; port++)
        {
            if (!activePorts.Contains(port))
            {
                return port;
            }
        }

        try
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private static ProcessStartInfo BuildLanguageServerStartInfo(
        string languageServerPath,
        int port,
        string csrfToken,
        string ideVersion)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = languageServerPath,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in new[]
        {
            "--standalone",
            "--override_ide_name",
            "antigravity",
            "--subclient_type",
            "hub",
            "--override_ide_version",
            ideVersion,
            "--override_user_agent_name",
            "antigravity",
            "--https_server_port",
            port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--csrf_token",
            csrfToken,
            "--app_data_dir",
            "antigravity",
            "--api_server_url",
            "https://generativelanguage.googleapis.com",
            "--cloud_code_endpoint",
            "https://daily-cloudcode-pa.googleapis.com",
            "--enable_sidecars"
        })
        {
            startInfo.ArgumentList.Add(arg);
        }

        return startInfo;
    }

    private static string GetAntigravityIdeVersion(string languageServerPath)
    {
        return TryGetLatestLogIdeVersion() ??
            TryGetExecutableFileVersion(languageServerPath) ??
            "2.1.4";
    }

    private static string? TryGetLatestLogIdeVersion()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var path in GetLogPaths(home))
        {
            var text = TryReadFileTail(path, LogTailCharacters);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var matches = OverrideIdeVersionRegex.Matches(text);
            if (matches.Count > 0)
            {
                var version = matches[^1].Groups["version"].Value.Trim();
                if (IsLikelyVersion(version))
                {
                    return version;
                }
            }
        }

        return null;
    }

    private static string? TryGetExecutableFileVersion(string languageServerPath)
    {
        try
        {
            var rootDirectory = Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(languageServerPath) ?? string.Empty,
                "..",
                ".."));
            var executablePath = Path.Combine(rootDirectory, "Antigravity.exe");
            if (!File.Exists(executablePath))
            {
                return null;
            }

            var version = FileVersionInfo.GetVersionInfo(executablePath).FileVersion;
            return IsLikelyVersion(version) ? version : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsLikelyVersion(string? version)
    {
        return !string.IsNullOrWhiteSpace(version) &&
            version.Length <= 32 &&
            version.All(character => char.IsDigit(character) || character == '.' || character == '-');
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private static ProviderUsage? TryCollectCachedStateUsage()
    {
        foreach (var databasePath in GetStateDatabasePaths())
        {
            if (!File.Exists(databasePath))
            {
                continue;
            }

            var capturedAt = new DateTimeOffset(File.GetLastWriteTime(databasePath));
            if (DateTimeOffset.Now - capturedAt > CachedStateMaxAge)
            {
                continue;
            }

            var source = $"Antigravity local state cache ({Path.GetFileName(databasePath)})";
            var usage = TryReadCachedStateUsage(databasePath, capturedAt, source);
            if (usage is not null)
            {
                return usage;
            }
        }

        return null;
    }

    private static IEnumerable<string> GetStateDatabasePaths()
    {
        var applicationData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(applicationData))
        {
            yield break;
        }

        yield return Path.Combine(applicationData, "Antigravity", "User", "globalStorage", "state.vscdb");
        yield return Path.Combine(applicationData, "Antigravity IDE", "User", "globalStorage", "state.vscdb");
    }

    private static ProviderUsage? TryReadCachedStateUsage(
        string databasePath,
        DateTimeOffset capturedAt,
        string source)
    {
        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Shared
            };

            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();

            var authStatus = TryReadStateValue(connection, "antigravityAuthStatus");
            if (!string.IsNullOrWhiteSpace(authStatus))
            {
                var usage = TryBuildCachedUsageFromAuthStatus(authStatus, capturedAt, source);
                if (usage is not null)
                {
                    return usage;
                }
            }

            var commandModelConfigs = TryReadStateValue(connection, "antigravity_allowed_command_model_configs");
            if (!string.IsNullOrWhiteSpace(commandModelConfigs))
            {
                var usage = TryBuildCachedUsageFromModelConfigArray(
                    commandModelConfigs,
                    capturedAt,
                    source,
                    "Antigravity");
                if (usage is not null)
                {
                    return usage;
                }
            }
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or JsonException or FormatException or InvalidOperationException)
        {
            return null;
        }

        return null;
    }

    private static string? TryReadStateValue(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM ItemTable WHERE key = $key LIMIT 1";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    private static ProviderUsage? TryBuildCachedUsageFromAuthStatus(
        string authStatusJson,
        DateTimeOffset capturedAt,
        string source)
    {
        using var document = JsonDocument.Parse(authStatusJson);
        if (!document.RootElement.TryGetProperty("userStatusProtoBinaryBase64", out var protoElement) ||
            protoElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(protoElement.GetString()))
        {
            return null;
        }

        var protoBytes = DecodeBase64(protoElement.GetString()!);
        return protoBytes is null
            ? null
            : TryBuildCachedUsageFromUserStatusProto(protoBytes, capturedAt, source);
    }

    private static ProviderUsage? TryBuildCachedUsageFromModelConfigArray(
        string modelConfigArrayJson,
        DateTimeOffset capturedAt,
        string source,
        string planName)
    {
        using var document = JsonDocument.Parse(modelConfigArrayJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var now = DateTimeOffset.Now;
        var quotas = new List<ModelQuota>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(element.GetString()))
            {
                continue;
            }

            var modelBytes = DecodeBase64(element.GetString()!);
            var quota = modelBytes is null ? null : TryParseCachedModelQuota(modelBytes, now);
            if (quota is not null)
            {
                quotas.Add(quota);
            }
        }

        return BuildUsageFromModelQuotas(quotas, source, planName, cached: true, capturedAt);
    }

    private static byte[]? DecodeBase64(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        var padding = trimmed.Length % 4;
        if (padding > 0)
        {
            trimmed = trimmed.PadRight(trimmed.Length + (4 - padding), '=');
        }

        return Convert.FromBase64String(trimmed);
    }

    private static ProviderUsage? TryBuildCachedUsageFromUserStatusProto(
        byte[] protoBytes,
        DateTimeOffset capturedAt,
        string source)
    {
        var now = DateTimeOffset.Now;
        var quotas = new List<ModelQuota>();
        var planName = TryParseCachedPlanName(protoBytes) ?? "Antigravity";

        foreach (var field in ReadProtoFields(protoBytes))
        {
            if (field.Number != 33 || field.WireType != 2)
            {
                continue;
            }

            foreach (var nestedField in ReadProtoFields(field.Data))
            {
                if (nestedField.Number != 1 || nestedField.WireType != 2)
                {
                    continue;
                }

                var quota = TryParseCachedModelQuota(nestedField.Data.ToArray(), now);
                if (quota is not null)
                {
                    quotas.Add(quota);
                }
            }
        }

        return BuildUsageFromModelQuotas(quotas, source, planName, cached: true, capturedAt);
    }

    internal static ProviderUsage? TryBuildCachedUsageFromModelConfigBytesForTests(
        IReadOnlyList<byte[]> modelConfigBytes,
        string planName,
        DateTimeOffset capturedAt,
        DateTimeOffset now)
    {
        var quotas = modelConfigBytes
            .Select(bytes => TryParseCachedModelQuota(bytes, now))
            .Where(quota => quota is not null)
            .Select(quota => quota!)
            .ToList();

        return BuildUsageFromModelQuotas(
            quotas,
            "Antigravity local state cache",
            planName,
            cached: true,
            capturedAt);
    }

    private static string? TryParseCachedPlanName(byte[] protoBytes)
    {
        foreach (var field in ReadProtoFields(protoBytes))
        {
            if (field.Number != 36 || field.WireType != 2)
            {
                continue;
            }

            foreach (var planField in ReadProtoFields(field.Data))
            {
                if (planField.WireType != 2 ||
                    planField.Number is not (2 or 3) ||
                    !TryReadUtf8(planField.Data.Span, out var text) ||
                    string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                return PlanNameFormatter.Format(text);
            }
        }

        return null;
    }

    private static ModelQuota? TryParseCachedModelQuota(byte[] modelBytes, DateTimeOffset now)
    {
        string? modelName = null;
        double? remainingFraction = null;
        DateTimeOffset? resetAt = null;

        foreach (var field in ReadProtoFields(modelBytes))
        {
            if (field.Number == 1 &&
                field.WireType == 2 &&
                TryReadUtf8(field.Data.Span, out var parsedName))
            {
                modelName = parsedName;
                continue;
            }

            if (field.Number != 15 || field.WireType != 2)
            {
                continue;
            }

            foreach (var quotaField in ReadProtoFields(field.Data))
            {
                if (quotaField.Number == 1)
                {
                    remainingFraction = quotaField.WireType switch
                    {
                        1 => quotaField.Float64,
                        5 => quotaField.Float32,
                        0 => quotaField.Varint,
                        _ => remainingFraction
                    };
                }
                else if (quotaField.Number == 2 && quotaField.WireType == 2)
                {
                    resetAt = TryParseProtoTimestamp(quotaField.Data);
                }
            }
        }

        if (string.IsNullOrWhiteSpace(modelName) ||
            !TryGetModelFamily(modelName, out var family) ||
            remainingFraction is null ||
            double.IsNaN(remainingFraction.Value) ||
            double.IsInfinity(remainingFraction.Value))
        {
            return null;
        }

        if (resetAt <= now.AddMinutes(-1))
        {
            resetAt = null;
        }

        return new ModelQuota(
            family,
            Math.Clamp(remainingFraction.Value, 0, 1),
            resetAt);
    }

    private static DateTimeOffset? TryParseProtoTimestamp(ReadOnlyMemory<byte> timestampBytes)
    {
        foreach (var field in ReadProtoFields(timestampBytes))
        {
            if (field.Number != 1 || field.WireType != 0)
            {
                continue;
            }

            try
            {
                return field.Varint > 10_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds((long)field.Varint)
                    : DateTimeOffset.FromUnixTimeSeconds((long)field.Varint);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        return null;
    }

    private static IReadOnlyList<ProtoField> ReadProtoFields(ReadOnlyMemory<byte> bytes)
    {
        var fields = new List<ProtoField>();
        var offset = 0;
        var span = bytes.Span;
        while (offset < span.Length)
        {
            if (!TryReadVarint(span, ref offset, out var key))
            {
                return fields;
            }

            var number = (int)(key >> 3);
            var wireType = (int)(key & 7);
            switch (wireType)
            {
                case 0:
                    if (!TryReadVarint(span, ref offset, out var varint))
                    {
                        return fields;
                    }

                    fields.Add(new ProtoField(number, wireType, ReadOnlyMemory<byte>.Empty, varint, 0, 0));
                    break;
                case 1:
                    if (offset + 8 > span.Length)
                    {
                        return fields;
                    }

                    var fixed64 = bytes.Slice(offset, 8);
                    offset += 8;
                    fields.Add(new ProtoField(
                        number,
                        wireType,
                        fixed64,
                        0,
                        0,
                        BitConverter.ToDouble(fixed64.Span)));
                    break;
                case 2:
                    if (!TryReadVarint(span, ref offset, out var length) ||
                        length > int.MaxValue ||
                        offset + (int)length > span.Length)
                    {
                        return fields;
                    }

                    var data = bytes.Slice(offset, (int)length);
                    offset += (int)length;
                    fields.Add(new ProtoField(number, wireType, data, 0, 0, 0));
                    break;
                case 5:
                    if (offset + 4 > span.Length)
                    {
                        return fields;
                    }

                    var fixed32 = bytes.Slice(offset, 4);
                    offset += 4;
                    fields.Add(new ProtoField(
                        number,
                        wireType,
                        fixed32,
                        0,
                        BitConverter.ToSingle(fixed32.Span),
                        0));
                    break;
                default:
                    return fields;
            }
        }

        return fields;
    }

    private static bool TryReadVarint(ReadOnlySpan<byte> span, ref int offset, out ulong value)
    {
        value = 0;
        var shift = 0;

        while (offset < span.Length && shift <= 63)
        {
            var current = span[offset++];
            value |= (ulong)(current & 0x7F) << shift;
            if ((current & 0x80) == 0)
            {
                return true;
            }

            shift += 7;
        }

        return false;
    }

    private static bool TryReadUtf8(ReadOnlySpan<byte> bytes, out string text)
    {
        text = string.Empty;
        if (bytes.Length == 0)
        {
            return false;
        }

        try
        {
            text = Encoding.UTF8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        return text.All(character => !char.IsControl(character) || character is '\r' or '\n' or '\t');
    }

    private static async Task<IReadOnlyList<Endpoint>> FindEndpointsAsync(
        string home,
        CancellationToken cancellationToken)
    {
        var endpoints = new List<Endpoint>();
        var seenEndpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var hint in GetLogEndpointHints(home))
        {
            if (string.IsNullOrWhiteSpace(hint.CsrfToken))
            {
                continue;
            }

            AddEndpoint(
                endpoints,
                seenEndpoints,
                new Uri($"https://127.0.0.1:{hint.Port}/"),
                hint.CsrfToken);
        }

        foreach (var port in GetCandidatePorts(home))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var baseUri = new Uri($"https://127.0.0.1:{port}/");
            var csrfToken = await TryLoadCsrfTokenAsync(baseUri, cancellationToken);
            if (!string.IsNullOrWhiteSpace(csrfToken))
            {
                AddEndpoint(endpoints, seenEndpoints, baseUri, csrfToken);
            }
        }

        return endpoints;
    }

    private static void AddEndpoint(
        ICollection<Endpoint> endpoints,
        ISet<string> seenEndpoints,
        Uri baseUri,
        string csrfToken)
    {
        var trimmedToken = csrfToken.Trim();
        if (trimmedToken.Length == 0)
        {
            return;
        }

        if (seenEndpoints.Add($"{baseUri}|{trimmedToken}"))
        {
            endpoints.Add(new Endpoint(baseUri, trimmedToken));
        }
    }

    private static IEnumerable<int> GetCandidatePorts(string home)
    {
        var seen = new HashSet<int>();

        foreach (var port in GetLogPorts(home))
        {
            if (seen.Add(port))
            {
                yield return port;
            }
        }

        foreach (var port in GetLoopbackListenerPorts())
        {
            if (seen.Add(port))
            {
                yield return port;
            }
        }

        foreach (var port in new[] { 20182, 20183 })
        {
            if (seen.Add(port))
            {
                yield return port;
            }
        }
    }

    private static IEnumerable<int> GetLogPorts(string home)
    {
        foreach (var hint in GetLogEndpointHints(home))
        {
            yield return hint.Port;
        }
    }

    private static IEnumerable<EndpointHint> GetLogEndpointHints(string home)
    {
        foreach (var path in GetLogPaths(home))
        {
            var text = TryReadFileTail(path, LogTailCharacters);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var hints = new List<EndpointHint>();
            string? csrfToken = null;
            foreach (var line in ReadLines(text))
            {
                var cliTokenMatch = CliCsrfTokenRegex.Match(line);
                if (cliTokenMatch.Success)
                {
                    csrfToken = cliTokenMatch.Groups["token"].Value;
                }
                else
                {
                    var jsonTokenMatch = CsrfTokenRegex.Match(line);
                    if (jsonTokenMatch.Success)
                    {
                        csrfToken = jsonTokenMatch.Groups["token"].Value;
                    }
                }

                foreach (Match match in LocalUrlRegex.Matches(line))
                {
                    if (int.TryParse(match.Groups["port"].Value, out var port))
                    {
                        hints.Add(new EndpointHint(port, csrfToken));
                    }
                }
            }

            for (var index = hints.Count - 1; index >= 0; index--)
            {
                yield return hints[index];
            }
        }
    }

    private static IEnumerable<string> GetLogPaths(string home)
    {
        var applicationData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var logDirectories = new[]
        {
            Path.Combine(applicationData, "Antigravity", "logs"),
            Path.Combine(applicationData, "Antigravity IDE", "logs"),
            Path.Combine(home, ".antigravity", "logs")
        };

        foreach (var directory in logDirectories)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            IEnumerable<string> paths;
            try
            {
                paths = Directory.EnumerateFiles(directory, "*.log", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .ToList();
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var path in paths)
            {
                yield return path;
            }
        }
    }

    private static string? TryReadFileTail(string path, int maxCharacters)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var offset = Math.Max(0, stream.Length - maxCharacters);
            stream.Seek(offset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IEnumerable<int> GetLoopbackListenerPorts()
    {
        IPEndPoint[] listeners;
        try
        {
            listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
        }
        catch (NetworkInformationException)
        {
            yield break;
        }

        foreach (var listener in listeners)
        {
            if (IPAddress.IsLoopback(listener.Address) && listener.Port is >= 20100 and <= 20299)
            {
                yield return listener.Port;
            }
        }
    }

    private static async Task<string?> TryLoadCsrfTokenAsync(Uri baseUri, CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(RequestTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, baseUri);
            using var response = await HttpClient.SendAsync(request, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var html = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            var match = CsrfTokenRegex.Match(html);
            return match.Success ? match.Groups["token"].Value : null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static async Task<JsonDocument?> TryPostAsync(
        Endpoint endpoint,
        string method,
        CancellationToken cancellationToken)
    {
        try
        {
            var requestUri = new Uri(endpoint.BaseUri, $"{ServiceName}/{method}");
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(RequestTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Post, requestUri);
            request.Headers.TryAddWithoutValidation(CsrfHeader, endpoint.CsrfToken);
            request.Content = new StringContent(GetRequestBody(method), Encoding.UTF8, "application/json");

            using var response = await HttpClient.SendAsync(request, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
            return await JsonDocument.ParseAsync(stream, cancellationToken: timeoutCts.Token);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string GetRequestBody(string method)
    {
        return method is "GetCascadeModelConfigs" or "GetCommandModelConfigs"
            ? """{"metadata":{}}"""
            : "{}";
    }

    private static ProviderUsage? TryParseUserStatus(JsonElement root, Endpoint endpoint)
    {
        var response = GetResponseOrRoot(root);

        if (TryGetObject(response, "userStatus", out var userStatus) &&
            TryGetClientModels(userStatus, out var userStatusModels))
        {
            return TryBuildUsage(
                userStatusModels.EnumerateArray(),
                endpoint,
                GetPlanName(userStatus)) ??
                TryBuildCreditUsage(userStatus, endpoint, GetPlanName(userStatus));
        }

        if (TryGetClientModels(response, out var responseModels))
        {
            return TryBuildUsage(
                responseModels.EnumerateArray(),
                endpoint,
                GetPlanName(response)) ??
                TryBuildCreditUsage(response, endpoint, GetPlanName(response));
        }

        return TryBuildCreditUsage(response, endpoint, GetPlanName(response));
    }

    private static ProviderUsage? TryParseModelConfigs(JsonElement root, Endpoint endpoint, string planName)
    {
        var response = GetResponseOrRoot(root);
        if (TryGetClientModels(response, out var clientModels))
        {
            return TryBuildUsage(
                clientModels.EnumerateArray(),
                endpoint,
                GetPlanName(response) is { Length: > 0 } responsePlanName ? responsePlanName : planName);
        }

        if (TryGetArray(response, "models", out var models))
        {
            return TryBuildUsage(models.EnumerateArray(), endpoint, planName);
        }

        if (TryGetObject(response, "models", out var modelMap))
        {
            return TryBuildUsage(
                modelMap.EnumerateObject().Select(property => property.Value),
                endpoint,
                planName);
        }

        return null;
    }

    private static ProviderUsage? TryBuildCreditUsage(JsonElement element, Endpoint endpoint, string planName)
    {
        if (!TryGetObject(element, "planStatus", out var planStatus) &&
            !TryGetObject(element, "plan_status", out planStatus))
        {
            return null;
        }

        if (!TryGetObject(planStatus, "planInfo", out var planInfo) &&
            !TryGetObject(planStatus, "plan_info", out planInfo))
        {
            return null;
        }

        var windows = new List<UsageWindow>();
        AddCreditWindow(
            windows,
            planStatus,
            planInfo,
            "Prompt credits",
            "availablePromptCredits",
            "available_prompt_credits",
            "monthlyPromptCredits",
            "monthly_prompt_credits");
        AddCreditWindow(
            windows,
            planStatus,
            planInfo,
            "Flow credits",
            "availableFlowCredits",
            "available_flow_credits",
            "monthlyFlowCredits",
            "monthly_flow_credits");

        if (windows.Count == 0)
        {
            return null;
        }

        return new ProviderUsage
        {
            Name = KnownProviders.Antigravity,
            PlanName = planName,
            Source = endpoint.Source ?? $"Antigravity language server ({endpoint.BaseUri})",
            StatusMessage = "Antigravity credit balances from local language server.",
            Windows = windows
        };
    }

    private static void AddCreditWindow(
        ICollection<UsageWindow> windows,
        JsonElement planStatus,
        JsonElement planInfo,
        string title,
        string availablePropertyName,
        string availableSnakeCasePropertyName,
        string monthlyPropertyName,
        string monthlySnakeCasePropertyName)
    {
        var hasAvailable = TryGetDouble(planStatus, availablePropertyName, out var available) ||
            TryGetDouble(planStatus, availableSnakeCasePropertyName, out available);
        var hasMonthly = TryGetDouble(planInfo, monthlyPropertyName, out var monthly) ||
            TryGetDouble(planInfo, monthlySnakeCasePropertyName, out monthly);

        if (!hasAvailable || !hasMonthly || monthly <= 0)
        {
            return;
        }

        var remainingPercent = Math.Clamp(available * 100d / monthly, 0, 100);
        windows.Add(ProviderUsageFactory.PercentWindow(
            title,
            100 - remainingPercent,
            null,
            $"{available:n0} of {monthly:n0} credits left"));
    }

    private static LogExhaustionRecord? TryGetLatestLogExhaustion(string home)
    {
        return GetLogPaths(home)
            .SelectMany(TryReadLogExhaustions)
            .OrderByDescending(record => record.Timestamp)
            .FirstOrDefault();
    }

    private static ProviderUsage BuildLogExhaustionUsage(LogExhaustionRecord latest)
    {
        return new ProviderUsage
        {
            Name = KnownProviders.Antigravity,
            PlanName = "Antigravity",
            Source = $"Antigravity language server log ({Path.GetFileName(latest.SourcePath)})",
            StatusMessage = "Antigravity quota exhaustion found in the local language server log.",
            Windows =
            [
                ProviderUsageFactory.PercentWindow(
                    "Gemini Models",
                    100,
                    latest.ResetAt,
                    "Baseline exhausted from latest Antigravity log")
            ]
        };
    }

    private static ProviderUsage BuildUnavailableUsage(LogExhaustionRecord? latestLogExhaustion)
    {
        var message = latestLogExhaustion is null
            ? "Antigravity is not running. Open Antigravity and refresh to read the local language-server quota."
            : $"Antigravity is not running. The last known exhausted quota reset passed at {latestLogExhaustion.ResetAt.ToLocalTime():MMM d, h:mm tt}; open Antigravity and refresh to read current quota.";

        return ProviderUsageFactory.Unavailable(
            KnownProviders.Antigravity,
            message,
            "Antigravity language server");
    }

    private static IEnumerable<LogExhaustionRecord> TryReadLogExhaustions(string path)
    {
        var text = TryReadFileTail(path, LogTailCharacters);
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        var fallbackTimestamp = new DateTimeOffset(File.GetLastWriteTime(path));
        foreach (var line in ReadLines(text))
        {
            var match = ResourceExhaustedResetRegex.Match(line);
            if (!match.Success || !TryParseDuration(match.Groups["duration"].Value, out var resetDelay))
            {
                continue;
            }

            var timestamp = TryParseLogTimestamp(line, fallbackTimestamp) ?? fallbackTimestamp;
            yield return new LogExhaustionRecord(timestamp, timestamp.Add(resetDelay), path);
        }
    }

    private static IEnumerable<string> ReadLines(string text)
    {
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    private static bool TryParseDuration(string text, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        var matched = false;

        foreach (Match match in DurationPartRegex.Matches(text))
        {
            if (!ProviderJson.TryParseDouble(match.Groups["value"].Value, out var value))
            {
                continue;
            }

            duration += match.Groups["unit"].Value.ToLowerInvariant() switch
            {
                "d" => TimeSpan.FromDays(value),
                "h" => TimeSpan.FromHours(value),
                "m" => TimeSpan.FromMinutes(value),
                "s" => TimeSpan.FromSeconds(value),
                _ => TimeSpan.Zero
            };
            matched = true;
        }

        return matched;
    }

    private static DateTimeOffset? TryParseLogTimestamp(string line, DateTimeOffset fallbackTimestamp)
    {
        var match = LogTimestampRegex.Match(line);
        if (!match.Success ||
            !int.TryParse(match.Groups["month"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var month) ||
            !int.TryParse(match.Groups["day"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var day) ||
            !TimeSpan.TryParseExact(match.Groups["time"].Value, "c", CultureInfo.InvariantCulture, out var time))
        {
            return null;
        }

        try
        {
            var localTime = new DateTime(
                fallbackTimestamp.Year,
                month,
                day,
                time.Hours,
                time.Minutes,
                time.Seconds,
                DateTimeKind.Unspecified);

            if (localTime > fallbackTimestamp.LocalDateTime.AddDays(1))
            {
                localTime = localTime.AddYears(-1);
            }

            var offset = TimeZoneInfo.Local.GetUtcOffset(localTime);
            return new DateTimeOffset(localTime, offset);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static ProviderUsage? TryBuildUsage(
        IEnumerable<JsonElement> models,
        Endpoint endpoint,
        string planName)
    {
        var quotas = models
            .Select(TryParseModelQuota)
            .Where(quota => quota is not null)
            .Select(quota => quota!);

        return BuildUsageFromModelQuotas(
            quotas,
            endpoint.Source ?? $"Antigravity language server ({endpoint.BaseUri})",
            planName,
            cached: false,
            capturedAt: null);
    }

    private static ProviderUsage? BuildUsageFromModelQuotas(
        IEnumerable<ModelQuota> modelQuotas,
        string source,
        string planName,
        bool cached,
        DateTimeOffset? capturedAt)
    {
        var quotaByFamily = modelQuotas
            .GroupBy(quota => quota.Family, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key)
            .ToList();

        if (quotaByFamily.Count == 0)
        {
            return null;
        }

        var windows = new List<UsageWindow>();
        foreach (var group in quotaByFamily)
        {
            var lowestQuota = group.OrderBy(quota => quota.RemainingFraction).First();
            var remainingPercent = Math.Clamp(lowestQuota.RemainingFraction * 100d, 0, 100);
            var detail = remainingPercent <= 0.05
                ? $"Baseline exhausted across {group.Count()} model(s)"
                : $"{remainingPercent:0.#}% left across {group.Count()} model(s)";

            windows.Add(ProviderUsageFactory.PercentWindow(
                group.Key,
                100 - remainingPercent,
                lowestQuota.ResetAt,
                detail));
        }

        return new ProviderUsage
        {
            Name = KnownProviders.Antigravity,
            PlanName = planName,
            Source = source,
            StatusMessage = cached
                ? $"Cached Antigravity model quota from local app state{FormatCapturedAt(capturedAt)}."
                : windows.Any(window => window.RemainingPercent <= 0.05)
                ? "Antigravity baseline quota is exhausted."
                : "Antigravity model quota from local language server.",
            Windows = windows
        };
    }

    private static ModelQuota? TryParseModelQuota(JsonElement element)
    {
        var modelId = TryGetString(element, "model") ??
            TryGetString(element, "name") ??
            TryGetString(element, "id") ??
            TryGetString(element, "modelId") ??
            TryGetString(element, "model_id") ??
            TryGetNestedString(element, "modelOrAlias", "model") ??
            TryGetNestedString(element, "model_or_alias", "model") ??
            string.Empty;
        var label = TryGetString(element, "label") ??
            TryGetString(element, "displayName") ??
            TryGetString(element, "display_name") ??
            modelId;
        var searchableName = $"{modelId} {label}";

        if (!TryGetModelFamily(searchableName, out var family) ||
            (!TryGetObject(element, "quotaInfo", out var quotaInfo) &&
             !TryGetObject(element, "quota_info", out quotaInfo)))
        {
            return null;
        }

        var resetAt = TryGetDateTimeOffset(quotaInfo, "resetTime") ??
            TryGetDateTimeOffset(quotaInfo, "reset_time");

        double remainingFraction;
        if (!TryGetDouble(quotaInfo, "remainingFraction", out remainingFraction) &&
            !TryGetDouble(quotaInfo, "remaining_fraction", out remainingFraction))
        {
            if (resetAt is null || resetAt.Value <= DateTimeOffset.UtcNow.AddMinutes(-1))
            {
                return null;
            }

            // Antigravity's protobuf reader defaults absent numeric fields to 0. Its UI uses
            // that behavior for baseline-exhausted Gemini models that only carry resetTime.
            remainingFraction = 0;
        }

        return new ModelQuota(
            family,
            Math.Clamp(remainingFraction, 0, 1),
            resetAt);
    }

    private static bool TryGetClientModels(JsonElement element, out JsonElement models)
    {
        if (TryGetObject(element, "cascadeModelConfigData", out var cascadeModelConfigData) &&
            TryGetArray(cascadeModelConfigData, "clientModelConfigs", out models))
        {
            return true;
        }

        if (TryGetObject(element, "cascade_model_config_data", out var snakeCaseConfigData) &&
            TryGetArray(snakeCaseConfigData, "client_model_configs", out models))
        {
            return true;
        }

        if (TryGetArray(element, "clientModelConfigs", out models) ||
            TryGetArray(element, "client_model_configs", out models))
        {
            return true;
        }

        models = default;
        return false;
    }

    private static JsonElement GetResponseOrRoot(JsonElement root)
    {
        return TryGetObject(root, "response", out var response)
            ? response
            : root;
    }

    private static string GetPlanName(JsonElement element)
    {
        if (TryGetObject(element, "userStatus", out var userStatus))
        {
            return GetPlanName(userStatus);
        }

        var rawPlanName = TryGetString(element, "userTier") ??
            TryGetNestedString(element, "userTier", "name") ??
            TryGetString(element, "user_tier") ??
            TryGetString(element, "planName") ??
            TryGetString(element, "plan_name") ??
            TryGetString(element, "plan") ??
            TryGetNestedString(element, "planStatus", "planInfo", "planName");

        return PlanNameFormatter.Format(rawPlanName);
    }

    private static string FormatCapturedAt(DateTimeOffset? capturedAt)
    {
        return capturedAt is null
            ? string.Empty
            : $" last updated {capturedAt.Value.ToLocalTime():MMM d, h:mm tt}";
    }

    private static bool TryGetModelFamily(string modelId, out string family)
    {
        if (modelId.Contains("gemini", StringComparison.OrdinalIgnoreCase) &&
            modelId.Contains("pro", StringComparison.OrdinalIgnoreCase))
        {
            family = "Gemini Pro";
            return true;
        }

        if (modelId.Contains("gemini", StringComparison.OrdinalIgnoreCase) &&
            modelId.Contains("flash", StringComparison.OrdinalIgnoreCase))
        {
            family = "Gemini Flash";
            return true;
        }

        if (modelId.Contains("gemini", StringComparison.OrdinalIgnoreCase))
        {
            family = "Gemini Models";
            return true;
        }

        if (modelId.Contains("claude", StringComparison.OrdinalIgnoreCase))
        {
            family = "Claude";
            return true;
        }

        if (modelId.Contains("gpt", StringComparison.OrdinalIgnoreCase))
        {
            family = "GPT";
            return true;
        }

        family = string.Empty;
        return false;
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return ProviderJson.TryGetString(element, propertyName);
    }

    private static string? TryGetNestedString(JsonElement element, string propertyName, string childPropertyName)
    {
        return TryGetObject(element, propertyName, out var child)
            ? TryGetString(child, childPropertyName)
            : null;
    }

    private static string? TryGetNestedString(
        JsonElement element,
        string propertyName,
        string childPropertyName,
        string grandchildPropertyName)
    {
        return TryGetObject(element, propertyName, out var child) &&
            TryGetObject(child, childPropertyName, out var grandchild)
                ? TryGetString(grandchild, grandchildPropertyName)
                : null;
    }

    private static bool TryGetObject(JsonElement element, string propertyName, out JsonElement property)
    {
        return element.TryGetProperty(propertyName, out property) &&
            property.ValueKind == JsonValueKind.Object;
    }

    private static bool TryGetArray(JsonElement element, string propertyName, out JsonElement property)
    {
        return element.TryGetProperty(propertyName, out property) &&
            property.ValueKind == JsonValueKind.Array;
    }

    private static bool TryGetDouble(JsonElement element, string propertyName, out double value)
    {
        return ProviderJson.TryGetDouble(element, propertyName, out value);
    }

    private static DateTimeOffset? TryGetDateTimeOffset(JsonElement element, string propertyName)
    {
        return ProviderJson.TryGetDateTimeOffset(element, propertyName);
    }

    private sealed record Endpoint(Uri BaseUri, string CsrfToken, string? Source = null);

    private sealed record EndpointHint(int Port, string? CsrfToken);

    private sealed record LogExhaustionRecord(DateTimeOffset Timestamp, DateTimeOffset ResetAt, string SourcePath);

    private sealed record ModelQuota(string Family, double RemainingFraction, DateTimeOffset? ResetAt);

    private readonly record struct ProtoField(
        int Number,
        int WireType,
        ReadOnlyMemory<byte> Data,
        ulong Varint,
        float Float32,
        double Float64);
}
