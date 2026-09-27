using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Tandem.Core.Devices;

namespace Tandem.Core.Companion;

public enum CompanionProblem
{
    /// <summary>Nothing listening: not installed, notification access off, or Android stopped it.</summary>
    Unavailable,
    /// <summary>The companion has no secret yet (installed but setup didn't finish).</summary>
    NotPaired,
    /// <summary>Something answered but couldn't prove it's our companion.</summary>
    Untrusted,
}

public sealed class CompanionException(CompanionProblem problem, string message) : Exception(message)
{
    public CompanionProblem Problem { get; } = problem;
}

/// <summary>
/// A live link to the companion app, tunnelled through adb:
/// PC TCP → adb forward → localabstract:tandem_companion (inside NotificationBridgeService).
/// </summary>
public sealed class CompanionConnection : IAsyncDisposable
{
    public const string SocketName = "tandem_companion";

    private readonly PhoneConnection _phone;
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly int _localPort;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private Task? _readTask;
    private int _closed;

    private CompanionConnection(PhoneConnection phone, TcpClient tcp, int localPort, string appVersion)
    {
        _phone = phone;
        _tcp = tcp;
        _stream = tcp.GetStream();
        _localPort = localPort;
        AppVersion = appVersion;
    }

    public string AppVersion { get; }

    /// <summary>Notifications already on the phone when we connected (don't pop these up).</summary>
    public event Action<IReadOnlyList<PhoneNotification>>? Snapshot;
    public event Action<PhoneNotification>? Posted;
    public event Action<string>? Removed;
    public event Action<string, byte[]>? AppIcon;
    public event Action<string>? PhoneError;
    /// <summary>Raised once; null when closed on purpose.</summary>
    public event Action<Exception?>? Closed;

    public static async Task<CompanionConnection> ConnectAsync(PhoneConnection phone, string secretHex, CancellationToken ct = default)
    {
        try
        {
            await CompanionInstaller.WakeAsync(phone, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Older companions have no wake receiver; connecting may still work.
        }
        var port = await phone.Adb.Client.CreateForwardAsync(phone.Device, "tcp:0", "localabstract:" + SocketName, true, ct)
            .ConfigureAwait(false);
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            await tcp.ConnectAsync(IPAddress.Loopback, port, ct).ConfigureAwait(false);
            var stream = tcp.GetStream();
            var nonce = CompanionProtocol.NewNonce();
            await CompanionProtocol.WriteAsync(stream, new JsonObject
            {
                ["t"] = "hello",
                ["v"] = CompanionProtocol.Version,
                ["nonce"] = nonce,
            }, ct).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            JsonObject? reply;
            try
            {
                reply = await CompanionProtocol.ReadAsync(stream, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                reply = null;
            }
            // adb accepts the TCP connection even when nothing listens on the phone, then closes it.
            if (reply is null || (string?)reply["t"] != "hello")
                throw new CompanionException(CompanionProblem.Unavailable, "The Tandem app on your phone isn't running.");

            var proof = (string?)reply["proof"];
            if (proof is null)
                throw new CompanionException(CompanionProblem.NotPaired, "The Tandem app on your phone isn't set up for this PC yet.");
            if (!CompanionProtocol.ProofMatches(secretHex, nonce, proof))
                throw new CompanionException(CompanionProblem.Untrusted, "The app answering on your phone couldn't prove it's Tandem. Run setup again.");

            var connection = new CompanionConnection(phone, tcp, port, (string?)reply["app"] ?? "?");
            return connection;
        }
        catch
        {
            tcp.Dispose();
            await TryRemoveForwardAsync(phone, port).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Ping interval; the phone drops connections that are silent for 60 s.</summary>
    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(20);
    /// <summary>No message (not even a pong) for this long means the phone side is gone.</summary>
    public static readonly TimeSpan SilenceLimit = TimeSpan.FromSeconds(50);

    private long _lastReceivedTicks = Environment.TickCount64;

    /// <summary>Starts delivering events. Call after subscribing, so the snapshot isn't missed.</summary>
    public void Start()
    {
        _readTask ??= Task.Run(ReadLoopAsync);
        _ = Task.Run(HeartbeatAsync);
    }

    /// <summary>
    /// A connection can die without either side noticing (phone app killed, Wi-Fi drop, a
    /// write stuck on a dead peer). Ping regularly and give up on a silent phone, so the
    /// service reconnects instead of waiting forever.
    /// </summary>
    private async Task HeartbeatAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(PingInterval, _cts.Token).ConfigureAwait(false);
                var silentFor = TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref _lastReceivedTicks));
                if (silentFor > SilenceLimit)
                {
                    await CloseAsync(new TimeoutException("The phone stopped answering.")).ConfigureAwait(false);
                    return;
                }
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                await SendAsync(new JsonObject { ["t"] = "ping" }, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        catch (Exception e)
        {
            await CloseAsync(e).ConfigureAwait(false);
        }
    }

    public Task DismissAsync(string key, CancellationToken ct = default) =>
        SendAsync(new JsonObject { ["t"] = "dismiss", ["key"] = key }, ct);

    public Task InvokeActionAsync(string key, int index, CancellationToken ct = default) =>
        SendAsync(new JsonObject { ["t"] = "action", ["key"] = key, ["i"] = index }, ct);

    public Task ReplyAsync(string key, int index, string text, CancellationToken ct = default) =>
        SendAsync(new JsonObject { ["t"] = "reply", ["key"] = key, ["i"] = index, ["text"] = text }, ct);

    public Task SendTestNotificationAsync(CancellationToken ct = default) =>
        SendAsync(new JsonObject { ["t"] = "test" }, ct);

    private async Task SendAsync(JsonObject message, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await CompanionProtocol.WriteAsync(_stream, message, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        Exception? error = null;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var msg = await CompanionProtocol.ReadAsync(_stream, _cts.Token).ConfigureAwait(false);
                if (msg is null)
                {
                    error = new IOException("The phone closed the connection.");
                    break;
                }
                Interlocked.Exchange(ref _lastReceivedTicks, Environment.TickCount64);
                Dispatch(msg);
            }
        }
        catch (Exception) when (_cts.IsCancellationRequested) { }
        catch (Exception e)
        {
            error = e;
        }
        await CloseAsync(error).ConfigureAwait(false);
    }

    private void Dispatch(JsonObject msg)
    {
        switch ((string?)msg["t"])
        {
            case "snapshot":
                Snapshot?.Invoke((msg["items"] as JsonArray ?? []).OfType<JsonObject>().Select(PhoneNotification.FromJson).ToList());
                break;
            case "posted" when msg["n"] is JsonObject n:
                Posted?.Invoke(PhoneNotification.FromJson(n));
                break;
            case "removed":
                if ((string?)msg["key"] is { } key) Removed?.Invoke(key);
                break;
            case "icon":
                if ((string?)msg["pkg"] is { } pkg && (string?)msg["png"] is { } png)
                    AppIcon?.Invoke(pkg, Convert.FromBase64String(png));
                break;
            case "error":
                PhoneError?.Invoke((string?)msg["message"] ?? "Unknown error on the phone");
                break;
        }
    }

    private async Task CloseAsync(Exception? error)
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1) return;
        _cts.Cancel();
        _stream.Dispose();
        _tcp.Dispose();
        await TryRemoveForwardAsync(_phone, _localPort).ConfigureAwait(false);
        Closed?.Invoke(error);
    }

    private static async Task TryRemoveForwardAsync(PhoneConnection phone, int port)
    {
        try { await phone.Adb.Client.RemoveForwardAsync(phone.Device, port, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception) { /* phone may be gone */ }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync(null).ConfigureAwait(false);
        _cts.Dispose();
        _writeLock.Dispose();
    }
}
