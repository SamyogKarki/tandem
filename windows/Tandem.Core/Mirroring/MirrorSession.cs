using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace Tandem.Core.Mirroring;

public enum MirrorProblem
{
    None,
    /// <summary>Xiaomi/HyperOS/MIUI (and a few others) block input injection until
    /// "USB debugging (Security settings)" is enabled.</summary>
    InputBlocked,
    NoAudio,
    DeviceGone,
    Other,
}

/// <summary>A running scrcpy window mirroring (and controlling) the phone.</summary>
public sealed class MirrorSession : IDisposable
{
    private readonly Process _process;
    private readonly ConcurrentQueue<string> _log = new();

    public event Action<MirrorSession>? Exited;
    public event Action<MirrorProblem>? ProblemDetected;

    public string Serial { get; }
    public bool IsRunning => !_process.HasExited;
    public IEnumerable<string> Log => _log;

    private MirrorSession(Process process, string serial)
    {
        _process = process;
        Serial = serial;
    }

    public static MirrorSession Start(ToolPaths tools, string serial, MirrorOptions options)
    {
        var psi = new ProcessStartInfo(tools.ScrcpyExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = tools.ScrcpyDirectory,
        };
        foreach (var a in options.ToArguments(serial)) psi.ArgumentList.Add(a);
        psi.Environment["ADB"] = tools.AdbExe;
        psi.Environment["SCRCPY_SERVER_PATH"] = tools.ScrcpyServer;

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var session = new MirrorSession(process, serial);
        process.OutputDataReceived += (_, e) => session.OnLine(e.Data);
        process.ErrorDataReceived += (_, e) => session.OnLine(e.Data);
        process.Exited += (_, _) => session.Exited?.Invoke(session);
        if (!process.Start()) throw new InvalidOperationException("Could not start scrcpy.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return session;
    }

    public static MirrorProblem Classify(string line)
    {
        if (line.Contains("INJECT_EVENTS", StringComparison.Ordinal) ||
            line.Contains("Could not inject", StringComparison.OrdinalIgnoreCase))
            return MirrorProblem.InputBlocked;
        if (line.Contains("Audio disabled", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("audio capture", StringComparison.OrdinalIgnoreCase) && line.Contains("ERROR", StringComparison.Ordinal))
            return MirrorProblem.NoAudio;
        if (line.Contains("Device disconnected", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Could not find any ADB device", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("not found", StringComparison.OrdinalIgnoreCase) && line.Contains("device", StringComparison.OrdinalIgnoreCase))
            return MirrorProblem.DeviceGone;
        return line.StartsWith("ERROR", StringComparison.Ordinal) ? MirrorProblem.Other : MirrorProblem.None;
    }

    private void OnLine(string? line)
    {
        if (string.IsNullOrEmpty(line)) return;
        _log.Enqueue(line);
        while (_log.Count > 200) _log.TryDequeue(out _);
        var problem = Classify(line);
        if (problem != MirrorProblem.None) ProblemDetected?.Invoke(problem);
    }

    /// <summary>Asks the scrcpy window to close, then kills it if it doesn't.</summary>
    public async Task StopAsync()
    {
        if (_process.HasExited) return;
        try
        {
            _process.CloseMainWindow();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
        catch (InvalidOperationException) { }
    }

    public void Dispose() => _process.Dispose();
}
