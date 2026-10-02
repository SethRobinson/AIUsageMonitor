using System.IO;
using System.Text.Json;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace AIUsageMonitor.Services;

public sealed record ClaudeBillingWebResponse(int Status, string Content);

public interface IClaudeBillingWebSession : IAsyncDisposable
{
    Task<ClaudeBillingWebResponse> GetAsync(string path, CancellationToken cancellationToken);
}

public interface IClaudeBillingBrowser
{
    Task<IClaudeBillingWebSession> OpenAsync(string profileDirectory, CancellationToken cancellationToken);
}

// Claude's website requires the browser session/network stack, not cookies replayed by HttpClient.
// Hidden refresh controllers have their own STA dispatcher; none of this runs on the overlay thread.
public sealed class ClaudeBillingBrowser : IClaudeBillingBrowser
{
    private static readonly object ProfileGate = new();
    private static readonly HashSet<string> DeletedProfiles = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<BrowserLifetime> ActiveBrowsers = [];
    private readonly Action<CoreWebView2, CoreWebView2Environment>? _configureForTest;

    public ClaudeBillingBrowser() { }
    internal ClaudeBillingBrowser(Action<CoreWebView2, CoreWebView2Environment> configureForTest) => _configureForTest = configureForTest;

    public Task<IClaudeBillingWebSession> OpenAsync(string profileDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lifetime = new BrowserLifetime(Path.GetFullPath(profileDirectory), cancellationToken);
        lock (ProfileGate)
        {
            if (DeletedProfiles.Contains(lifetime.Directory))
            {
                lifetime.Stop.Dispose();
                throw new OperationCanceledException("Billing connection was removed.");
            }
            ActiveBrowsers.Add(lifetime);
        }
        var ready = new TaskCompletionSource<IClaudeBillingWebSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                HwndSource? host = null;
                CoreWebView2Controller? controller = null;
                Task browserExited = Task.CompletedTask;
                try
                {
                    lifetime.Stop.Token.ThrowIfCancellationRequested();
                    // WS_POPUP without WS_VISIBLE: native parent only, never shown or activated.
                    host = new HwndSource(new HwndSourceParameters("Claude billing refresh")
                    { WindowStyle = unchecked((int)0x80000000), Width = 1024, Height = 768 });
                    var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: lifetime.Directory);
                    var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    environment.BrowserProcessExited += (_, _) => exited.TrySetResult();
                    browserExited = exited.Task;
                    lifetime.Stop.Token.ThrowIfCancellationRequested();
                    controller = await environment.CreateCoreWebView2ControllerAsync(host.Handle);
                    controller.IsVisible = false;
                    controller.Bounds = new System.Drawing.Rectangle(0, 0, 1024, 768);
                    var core = controller.CoreWebView2;
                    core.Settings.AreDevToolsEnabled = false;
                    core.Settings.AreDefaultScriptDialogsEnabled = false;
                    core.NewWindowRequested += (_, e) => e.Handled = true;
                    core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
                    core.DownloadStarting += (_, e) => e.Cancel = true;
                    core.NavigationStarting += (_, e) => e.Cancel = !IsClaudePage(e.Uri);
                    _configureForTest?.Invoke(core, environment);
                    var navigation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    core.NavigationCompleted += (_, e) =>
                    {
                        if (e.IsSuccess) navigation.TrySetResult();
                        else navigation.TrySetException(new IOException("Claude billing page could not be loaded."));
                    };
                    lifetime.Stop.Token.ThrowIfCancellationRequested();
                    core.Navigate("https://claude.ai/settings/billing");
                    await navigation.Task.WaitAsync(lifetime.Stop.Token);
                    var closing = 0;
                    CancellationTokenRegistration stopped = default;
                    Task CloseAsync()
                    {
                        if (Interlocked.Exchange(ref closing, 1) == 0) dispatcher.BeginInvoke(async () =>
                        {
                            stopped.Dispose();
                            await CloseControllerAsync(controller, host, dispatcher, browserExited);
                        });
                        return lifetime.Finished.Task;
                    }
                    stopped = lifetime.Stop.Token.Register(() => { _ = CloseAsync(); });
                    ready.SetResult(new Session(core, dispatcher, lifetime.Stop.Token, CloseAsync));
                }
                catch (Exception ex)
                {
                    if (ex is OperationCanceledException) ready.TrySetCanceled();
                    else ready.TrySetException(ex);
                    await CloseControllerAsync(controller, host, dispatcher, browserExited);
                }
            });
            try { Dispatcher.Run(); }
            finally
            {
                lock (ProfileGate) ActiveBrowsers.Remove(lifetime);
                lifetime.Stop.Dispose();
                lifetime.Finished.TrySetResult();
            }
        }) { IsBackground = true, Name = "Claude usage credits" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task;
    }

    // Invalidate before deleting so an older refresh cannot recreate a disconnected profile.
    internal static async Task StopProfileAsync(string profileDirectory)
    {
        Task[] finished;
        lock (ProfileGate)
        {
            var directory = Path.GetFullPath(profileDirectory);
            DeletedProfiles.Add(directory);
            var active = ActiveBrowsers.Where(b => string.Equals(b.Directory, directory, StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach (var browser in active) browser.Stop.Cancel();
            finished = active.Select(b => b.Finished.Task).ToArray();
        }
        await Task.WhenAll(finished).ConfigureAwait(false);
    }

    public static IClaudeBillingWebSession FromVisibleBrowser(CoreWebView2 browser, Dispatcher dispatcher) =>
        new Session(browser, dispatcher, CancellationToken.None, null);

    private static async Task CloseControllerAsync(CoreWebView2Controller? controller, HwndSource? host,
        Dispatcher dispatcher, Task browserExited)
    {
        try
        {
            controller?.Close();
            // Keep the message pump alive until Chromium releases the user data folder. Closing
            // the controller alone can leave its lockfile held while pending browser work settles.
            if (controller is not null) await browserExited.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException) { /* Another controller may still share this connection's profile. */ }
        catch (System.Runtime.InteropServices.COMException) { /* Browser process already exited. */ }
        finally
        {
            host?.Dispose();
            dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
        }
    }

    private static bool IsClaudePage(string uri) => Uri.TryCreate(uri, UriKind.Absolute, out var parsed) &&
        parsed.Scheme == "https" && parsed.Host == "claude.ai" && parsed.IsDefaultPort;

    private sealed class BrowserLifetime(string directory, CancellationToken cancellationToken)
    {
        public string Directory { get; } = directory;
        public CancellationTokenSource Stop { get; } = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Session(CoreWebView2 core, Dispatcher dispatcher, CancellationToken lifetime,
        Func<Task>? close) : IClaudeBillingWebSession
    {
        private int _disposed;

        public async Task<ClaudeBillingWebResponse> GetAsync(string path, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            ValidateReadPath(path);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime);
            var requestToken = linked.Token;
            requestToken.ThrowIfCancellationRequested();
            return await dispatcher.InvokeAsync(() => FetchAsync(path, requestToken)).Task.Unwrap()
                .WaitAsync(requestToken).ConfigureAwait(false);
        }

        private async Task<ClaudeBillingWebResponse> FetchAsync(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsClaudePage(core.Source))
                throw new ClaudeBillingException(Models.ClaudeBillingConnectionState.ReconnectRequired,
                    "Finish signing in to Claude, then click Connect.");
            // ExecuteScriptAsync does not await a returned Promise. CDP awaitPromise waits for the
            // response body as well as fetch; no web-message bridge or page-provided identity needed.
            var expression = $$"""
                (async () => {
                    if (location.origin !== 'https://claude.ai') throw new Error('Wrong origin');
                    const response = await fetch({{JsonSerializer.Serialize(path)}}, {
                        method: 'GET', credentials: 'include', redirect: 'manual', cache: 'no-store',
                        headers: { Accept: 'application/json' }, signal: AbortSignal.timeout(15000)
                    });
                    const content = await response.text();
                    if (content.length > 1048576) throw new Error('Response too large');
                    return { status: response.status, content };
                })()
                """;
            var response = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", JsonSerializer.Serialize(new
            { expression, awaitPromise = true, returnByValue = true })).WaitAsync(cancellationToken);
            using var json = JsonDocument.Parse(response);
            if (json.RootElement.TryGetProperty("exceptionDetails", out _) ||
                !json.RootElement.TryGetProperty("result", out var result) || !result.TryGetProperty("value", out var value) ||
                !value.TryGetProperty("status", out var status) || !status.TryGetInt32(out var code) ||
                !value.TryGetProperty("content", out var contentElement) || contentElement.ValueKind != JsonValueKind.String)
                throw new IOException("Claude billing browser request did not complete.");
            return new(code, contentElement.GetString()!);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0 && close is not null) await close().ConfigureAwait(false);
        }
    }

    internal static void ValidateReadPath(string path)
    {
        if (path is "/api/account" or "/api/organizations") return;
        var parts = path.Split('/');
        if (parts.Length == 6 && parts[0] == "" && parts[1] == "api" && parts[2] == "organizations" &&
            Guid.TryParseExact(parts[3], "D", out _) && parts[4] == "prepaid" && parts[5] == "credits") return;
        throw new ArgumentException("Only Claude billing identity and balance reads are allowed.", nameof(path));
    }
}
