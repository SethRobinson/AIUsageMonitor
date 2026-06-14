using System.IO;
using System.IO.Pipes;
using System.Text;

namespace AIUsageMonitor.Services;

internal sealed class SingleInstanceCoordinator : IDisposable
{
    public const string ShowCenterCommand = "show-center";

    private static readonly TimeSpan DefaultSignalTimeout = TimeSpan.FromSeconds(3);

    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _listenerCts = new();
    private readonly object _listenerLock = new();
    private Task? _listenerTask;
    private bool _disposed;

    public SingleInstanceCoordinator(string instanceName)
    {
        var normalizedName = NormalizeInstanceName(instanceName);
        _pipeName = $"{normalizedName}.SingleInstance";
        _mutex = new Mutex(initiallyOwned: true, $@"Local\{normalizedName}.SingleInstance", out var isPrimaryInstance);
        IsPrimaryInstance = isPrimaryInstance;
    }

    public bool IsPrimaryInstance { get; }

    public void StartListening(Action<string> commandReceived)
    {
        if (!IsPrimaryInstance || _disposed)
        {
            return;
        }

        lock (_listenerLock)
        {
            _listenerTask ??= Task.Run(() => ListenAsync(commandReceived, _listenerCts.Token));
        }
    }

    public bool SignalExistingInstance()
    {
        return SignalExistingInstance(ShowCenterCommand, DefaultSignalTimeout);
    }

    internal bool SignalExistingInstance(string command, TimeSpan timeout)
    {
        if (IsPrimaryInstance || _disposed)
        {
            return false;
        }

        try
        {
            using var pipeClient = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out);
            pipeClient.Connect((int)Math.Clamp(timeout.TotalMilliseconds, 1, int.MaxValue));

            using var writer = new StreamWriter(pipeClient, Encoding.UTF8) { AutoFlush = true };
            writer.WriteLine(command);
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _listenerCts.Cancel();
        _listenerCts.Dispose();

        if (IsPrimaryInstance)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }
        }

        _mutex.Dispose();
    }

    private async Task ListenAsync(Action<string> commandReceived, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipeServer = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipeServer.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                using var reader = new StreamReader(pipeServer, Encoding.UTF8);
                var command = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(command))
                {
                    commandReceived(command);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (IOException)
            {
            }
        }
    }

    private static string NormalizeInstanceName(string instanceName)
    {
        var builder = new StringBuilder(instanceName.Length);
        foreach (var character in instanceName)
        {
            builder.Append(char.IsLetterOrDigit(character) || character is '.' or '-' or '_'
                ? character
                : '_');
        }

        return builder.Length > 0
            ? builder.ToString()
            : AppMetadata.StartupEntryName;
    }
}
