using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Tandem.Core.Devices;
using Tandem.Core.Files;

namespace Tandem.Core.Clipboard;

/// <summary>
/// Always-on clipboard sync without an app on the phone. Android only lets the foreground
/// app (or the keyboard) read the clipboard, but adb's shell user is exempt — so we run
/// scrcpy's server as shell in control-only mode (no video, no audio, screen left alone)
/// and use its clipboard messages.
/// </summary>
public sealed class ClipboardBridge(PhoneConnection phone, ToolPaths tools) : IAsyncDisposable
{
    private const string RemoteServerPath = "/data/local/tmp/tandem-scrcpy-server.jar";

    private readonly ClipboardLoopGuard _guard = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentQueue<string> _serverLog = new();
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private Task? _serverTask;
    private Task? _readTask;
    private int _localPort;
    private int _stopped;

    /// <summary>Raised on a background thread with text the phone copied.</summary>
    public event Action<string>? PhoneClipboardChanged;

    /// <summary>Raised once when the bridge stops for any reason (null = stopped on request).</summary>
    public event Action<Exception?>? Stopped;

    public PhoneConnection Phone => phone;
    public IEnumerable<string> ServerLog => _serverLog;

    /// <summary>scrcpy-server log level: verbose, debug, info, warn or error.</summary>
    public string LogLevel { get; init; } = "info";

    public async Task StartAsync(CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        var token = linked.Token;
        var scid = RandomNumberGenerator.GetInt32(1, int.MaxValue); // scrcpy wants a positive 31-bit id

        await using (var jar = File.OpenRead(tools.ScrcpyServer))
            await new PhoneFileSystem(phone).UploadAsync(jar, RemoteServerPath, DateTimeOffset.Now, null, token).ConfigureAwait(false);

        _localPort = await phone.Adb.Client.CreateForwardAsync(
            phone.Device, "tcp:0", $"localabstract:scrcpy_{scid:x8}", true, token).ConfigureAwait(false);

        var command = string.Join(' ',
            $"CLASSPATH={RemoteServerPath}", "app_process", "/", "com.genymobile.scrcpy.Server", ToolPaths.ScrcpyVersion,
            $"scid={scid:x8}", "log_level=" + LogLevel,
            "video=false", "audio=false", "control=true",
            "tunnel_forward=true", "send_device_meta=false",
            "power_on=false", "clipboard_autosync=true", "cleanup=true");
        _serverTask = Task.Run(() => RunServerAsync(command, _cts.Token), CancellationToken.None);

        await ConnectAsync(token).ConfigureAwait(false);
        _readTask = Task.Run(() => ReadLoopAsync(_cts.Token), CancellationToken.None);
    }

    /// <summary>
    /// Puts text on the phone's clipboard. Returns false (and sends nothing) when the text
    /// is an echo of what the phone just copied.
    /// </summary>
    public async Task<bool> SetPhoneClipboardAsync(string text, CancellationToken ct = default)
    {
        if (_stream is null || !_guard.ShouldSendToPhone(text)) return false;
        // Sequence 0 = no acknowledgement wanted.
        await WriteAsync(ScrcpyControlProtocol.EncodeSetClipboard(0, text, paste: false), ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Asks the phone for its current clipboard. The answer arrives through
    /// <see cref="PhoneClipboardChanged"/> (only if it differs from the last synced text).
    /// </summary>
    public Task RequestPhoneClipboardAsync(CancellationToken ct = default) =>
        WriteAsync(ScrcpyControlProtocol.EncodeGetClipboard(), ct);

    /// <summary>Has the phone index a file we just uploaded, so it appears in Gallery etc.</summary>
    public Task ScanFileAsync(string remotePath, CancellationToken ct = default) =>
        WriteAsync(ScrcpyControlProtocol.EncodeScanFile(remotePath), ct);

    private async Task WriteAsync(byte[] message, CancellationToken ct)
    {
        var stream = _stream ?? throw new InvalidOperationException("Clipboard bridge is not connected.");
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(message, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        // adb accepts the forwarded TCP connection even before the server listens on the phone;
        // in that case it closes right away. The server greets a real connection with one 0 byte.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var tcp = new TcpClient { NoDelay = true };
            try
            {
                await tcp.ConnectAsync(IPAddress.Loopback, _localPort, ct).ConfigureAwait(false);
                var stream = tcp.GetStream();
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                readTimeout.CancelAfter(TimeSpan.FromSeconds(3));
                var greeting = new byte[1];
                if (await stream.ReadAsync(greeting, readTimeout.Token).ConfigureAwait(false) == 1)
                {
                    _tcp = tcp;
                    _stream = stream;
                    return;
                }
            }
            catch (Exception e) when (e is SocketException or IOException ||
                                      (e is OperationCanceledException && !ct.IsCancellationRequested))
            {
            }
            tcp.Dispose();

            if (DateTime.UtcNow > deadline || _serverTask is { IsCompleted: true })
                throw new IOException("The clipboard helper didn't start on the phone. " + LastServerLines());
            await Task.Delay(150, ct).ConfigureAwait(false);
        }
    }

    private async Task RunServerAsync(string command, CancellationToken ct)
    {
        try
        {
            await foreach (var line in phone.Adb.Client.ExecuteRemoteEnumerableAsync(command, phone.Device, Encoding.UTF8, ct).ConfigureAwait(false))
            {
                _serverLog.Enqueue(line);
                while (_serverLog.Count > 100) _serverLog.TryDequeue(out _);
            }
        }
        catch (Exception) when (ct.IsCancellationRequested) { }
        catch (Exception e)
        {
            _serverLog.Enqueue("[shell ended] " + e.Message);
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        Exception? error = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var message = await DeviceMessage.ReadAsync(_stream!, ct).ConfigureAwait(false);
                if (message is null)
                {
                    error = new IOException("The phone closed the clipboard connection. " + LastServerLines());
                    break;
                }
                if (message is DeviceMessage.ClipboardText t && _guard.ShouldApplyToPc(t.Text))
                    PhoneClipboardChanged?.Invoke(t.Text);
            }
        }
        catch (Exception) when (ct.IsCancellationRequested) { }
        catch (Exception e)
        {
            error = e;
        }
        await StopCoreAsync(error).ConfigureAwait(false);
    }

    private string LastServerLines() =>
        string.Join(" | ", _serverLog.Reverse().Take(3).Reverse());

    public Task StopAsync() => StopCoreAsync(null);

    private async Task StopCoreAsync(Exception? error)
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 1) return;
        _cts.Cancel();
        // Closing the socket makes the server exit on its own; the shell then ends too.
        _stream?.Dispose();
        _tcp?.Dispose();
        if (_localPort != 0)
        {
            try { await phone.Adb.Client.RemoveForwardAsync(phone.Device, _localPort, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception) { /* device may already be gone */ }
        }
        Stopped?.Invoke(error);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        if (_serverTask is not null) await Task.WhenAny(_serverTask, Task.Delay(1000)).ConfigureAwait(false);
        _cts.Dispose();
        _writeLock.Dispose();
    }
}
