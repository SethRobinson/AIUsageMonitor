using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIUsageMonitor.Services;

namespace AIUsageMonitor.Tests;

[TestClass]
public sealed class AnthropicTokenRefreshTests
{
    private string _tempDirectory = string.Empty;
    private string _credentialsPath = string.Empty;

    [TestInitialize]
    public void Initialize()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "AIUsageMonitor.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _credentialsPath = Path.Combine(_tempDirectory, ".credentials.json");
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [TestMethod]
    public async Task FreshTokenIsReturnedWithoutRefreshCall()
    {
        WriteCredentials(accessToken: "fresh-token", refreshToken: "refresh-1", expiresInFromNow: TimeSpan.FromHours(2));
        var handler = new ScriptedHttpHandler(_ => throw new InvalidOperationException("no HTTP call expected"));
        var refresher = CreateRefresher(handler);

        var token = await refresher.GetFreshAccessTokenAsync(_credentialsPath, forceRefresh: false, CancellationToken.None);

        Assert.AreEqual("fresh-token", token);
        Assert.AreEqual(0, handler.CallCount);
    }

    [TestMethod]
    public async Task ExpiredTokenTriggersRefreshWithExpectedPayload()
    {
        WriteCredentials(accessToken: "stale-token", refreshToken: "refresh-1", expiresInFromNow: TimeSpan.FromMinutes(-10));
        JsonObject? capturedPayload = null;
        var handler = new ScriptedHttpHandler(request =>
        {
            capturedPayload = JsonNode.Parse(request.Content!.ReadAsStringAsync().Result) as JsonObject;
            return TokenResponse("new-token", "refresh-2", expiresInSeconds: 3600);
        });
        var refresher = CreateRefresher(handler);

        var token = await refresher.GetFreshAccessTokenAsync(_credentialsPath, forceRefresh: false, CancellationToken.None);

        Assert.AreEqual("new-token", token);
        Assert.IsNotNull(capturedPayload);
        Assert.AreEqual("refresh_token", capturedPayload!["grant_type"]?.GetValue<string>());
        Assert.AreEqual("refresh-1", capturedPayload["refresh_token"]?.GetValue<string>());
        Assert.AreEqual(AnthropicOAuthTokenRefresher.ClaudeCodeClientId, capturedPayload["client_id"]?.GetValue<string>());
    }

    [TestMethod]
    public async Task RotatedTokensArePersistedAndUnknownPropertiesPreserved()
    {
        WriteCredentials(
            accessToken: "stale-token",
            refreshToken: "refresh-1",
            expiresInFromNow: TimeSpan.FromMinutes(-10),
            extraOAuthProperties: new Dictionary<string, JsonNode?>
            {
                ["scopes"] = new JsonArray("user:inference", "user:profile"),
                ["subscriptionType"] = "max"
            });
        var handler = new ScriptedHttpHandler(_ => TokenResponse("new-token", "refresh-2", expiresInSeconds: 3600));
        var refresher = CreateRefresher(handler);

        _ = await refresher.GetFreshAccessTokenAsync(_credentialsPath, forceRefresh: false, CancellationToken.None);

        var persisted = JsonNode.Parse(File.ReadAllText(_credentialsPath))!.AsObject();
        var oauth = persisted["claudeAiOauth"]!.AsObject();
        Assert.AreEqual("new-token", oauth["accessToken"]?.GetValue<string>());
        Assert.AreEqual("refresh-2", oauth["refreshToken"]?.GetValue<string>());
        Assert.AreEqual("max", oauth["subscriptionType"]?.GetValue<string>());
        Assert.AreEqual(2, oauth["scopes"]!.AsArray().Count);
        var expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(oauth["expiresAt"]!.GetValue<long>());
        Assert.IsTrue(expiresAt > DateTimeOffset.UtcNow.AddMinutes(30), "expiresAt should reflect the new expiry");
    }

    [TestMethod]
    public async Task RejectedRefreshReturnsNullAndDoesNotCorruptFile()
    {
        WriteCredentials(accessToken: "stale-token", refreshToken: "refresh-1", expiresInFromNow: TimeSpan.FromMinutes(-10));
        var originalContent = File.ReadAllText(_credentialsPath);
        var handler = new ScriptedHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":"invalid_grant"}""", Encoding.UTF8, "application/json")
        });
        var refresher = CreateRefresher(handler);

        var token = await refresher.GetFreshAccessTokenAsync(_credentialsPath, forceRefresh: false, CancellationToken.None);

        Assert.IsNull(token);
        Assert.AreEqual(originalContent, File.ReadAllText(_credentialsPath));
    }

    [TestMethod]
    public async Task TransientRefreshFailureFallsBackToStoredToken()
    {
        WriteCredentials(accessToken: "stale-token", refreshToken: "refresh-1", expiresInFromNow: TimeSpan.FromMinutes(-10));
        var handler = new ScriptedHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var refresher = CreateRefresher(handler);

        var token = await refresher.GetFreshAccessTokenAsync(_credentialsPath, forceRefresh: false, CancellationToken.None);

        Assert.AreEqual("stale-token", token);
    }

    [TestMethod]
    public async Task MissingCredentialsFileReturnsNull()
    {
        var handler = new ScriptedHttpHandler(_ => throw new InvalidOperationException("no HTTP call expected"));
        var refresher = CreateRefresher(handler);

        var token = await refresher.GetFreshAccessTokenAsync(
            Path.Combine(_tempDirectory, "does-not-exist.json"),
            forceRefresh: false,
            CancellationToken.None);

        Assert.IsNull(token);
    }

    private AnthropicOAuthTokenRefresher CreateRefresher(HttpMessageHandler handler)
    {
        return new AnthropicOAuthTokenRefresher(new HttpClient(handler));
    }

    private void WriteCredentials(
        string accessToken,
        string refreshToken,
        TimeSpan expiresInFromNow,
        Dictionary<string, JsonNode?>? extraOAuthProperties = null)
    {
        var oauth = new JsonObject
        {
            ["accessToken"] = accessToken,
            ["refreshToken"] = refreshToken,
            ["expiresAt"] = DateTimeOffset.UtcNow.Add(expiresInFromNow).ToUnixTimeMilliseconds()
        };

        foreach (var pair in extraOAuthProperties ?? [])
        {
            oauth[pair.Key] = pair.Value;
        }

        var credentials = new JsonObject
        {
            ["claudeAiOauth"] = oauth
        };

        File.WriteAllText(_credentialsPath, credentials.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static HttpResponseMessage TokenResponse(string accessToken, string refreshToken, long expiresInSeconds)
    {
        var body = new JsonObject
        {
            ["access_token"] = accessToken,
            ["refresh_token"] = refreshToken,
            ["expires_in"] = expiresInSeconds,
            ["token_type"] = "Bearer"
        };

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
    }

    private sealed class ScriptedHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        private int _callCount;

        public int CallCount => _callCount;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(responder(request));
        }
    }
}
