using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DSHDesktop.Core;

public enum ServerHostPhase
{
    Stopped,
    Starting,
    Ready,
    TimedOut,
    Stopping,
    Exited,
    Failed,
}

public enum ServerStartOutcome
{
    Ready,
    AlreadyRunning,
    ExitedBeforeReady,
    TimedOut,
    FailedToStart,
    Cancelled,
}

public enum ServerOutputStream
{
    StandardOutput,
    StandardError,
}

public sealed record ServerHostSnapshot(
    int Generation,
    ServerHostPhase Phase,
    int? ProcessId = null,
    DateTimeOffset? StartedAt = null,
    string? Endpoint = null,
    int? ExitCode = null,
    string? Error = null)
{
    public bool IsRunning => Phase is ServerHostPhase.Starting
        or ServerHostPhase.Ready
        or ServerHostPhase.TimedOut
        or ServerHostPhase.Stopping;

    public bool ReadinessCompleted => Phase is not ServerHostPhase.Starting;
}

public sealed record ServerStartResult(
    ServerStartOutcome Outcome,
    ServerHostSnapshot Snapshot,
    string? Endpoint = null,
    string? Error = null);

public sealed class ServerOutputEventArgs(
    int generation,
    ServerOutputStream stream,
    string line) : EventArgs
{
    public int Generation { get; } = generation;
    public ServerOutputStream Stream { get; } = stream;
    public string Line { get; } = line;
}

public sealed class ServerExitedEventArgs(
    int generation,
    int? exitCode,
    bool stopRequested) : EventArgs
{
    public int Generation { get; } = generation;
    public int? ExitCode { get; } = exitCode;
    public bool StopRequested { get; } = stopRequested;
}

public interface IServerHost : IDisposable
{
    event EventHandler<ServerOutputEventArgs>? OutputReceived;
    event EventHandler<ServerExitedEventArgs>? Exited;

    ServerHostSnapshot Snapshot { get; }
    bool IsRunning { get; }
    bool ReadinessCompleted { get; }
    int Generation { get; }
    string? Endpoint { get; }

    Task<ServerStartResult> StartAsync(
        ProcessStartInfo startInfo,
        Regex endpointPattern,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    void Stop(TimeSpan waitTimeout);
}

/// <summary>
/// Owns one child process at a time and turns its lifecycle into stable, UI-independent state.
/// Callers remain responsible for constructing the command/environment and presenting output.
/// </summary>
public sealed class ServerHost : IServerHost
{
    private sealed class Session(Process process, int generation)
    {
        internal Process Process { get; } = process;
        internal int Generation { get; } = generation;
        internal TaskCompletionSource<string?> Readiness { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool StopRequested { get; set; }
    }

    private readonly object _sync = new();
    private Session? _session;
    private int _generation;
    private ServerHostSnapshot _snapshot = new(0, ServerHostPhase.Stopped);

    public event EventHandler<ServerOutputEventArgs>? OutputReceived;
    public event EventHandler<ServerExitedEventArgs>? Exited;

    public ServerHostSnapshot Snapshot
    {
        get { lock (_sync) return _snapshot; }
    }

    public bool IsRunning => Snapshot.IsRunning;
    public bool ReadinessCompleted => Snapshot.ReadinessCompleted;
    public int Generation => Snapshot.Generation;
    public string? Endpoint => Snapshot.Endpoint;

    public async Task<ServerStartResult> StartAsync(
        ProcessStartInfo startInfo,
        Regex endpointPattern,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(endpointPattern);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (startInfo.UseShellExecute
            || !startInfo.RedirectStandardOutput
            || !startInfo.RedirectStandardError)
        {
            throw new ArgumentException(
                "ServerHost requires UseShellExecute=false and both output streams redirected.",
                nameof(startInfo));
        }

        Session session;
        lock (_sync)
        {
            if (_snapshot.IsRunning)
                return new ServerStartResult(ServerStartOutcome.AlreadyRunning, _snapshot, _snapshot.Endpoint);

            DisposeSessionNoThrow(_session);
            _session = null;
            var generation = ++_generation;
            _snapshot = new ServerHostSnapshot(generation, ServerHostPhase.Starting);

            Process? process;
            try
            {
                process = Process.Start(startInfo);
                if (process == null)
                {
                    _snapshot = _snapshot with
                    {
                        Phase = ServerHostPhase.Failed,
                        Error = "Process.Start returned null.",
                    };
                    return new ServerStartResult(
                        ServerStartOutcome.FailedToStart,
                        _snapshot,
                        Error: _snapshot.Error);
                }
            }
            catch (Exception ex)
            {
                _snapshot = _snapshot with { Phase = ServerHostPhase.Failed, Error = ex.Message };
                return new ServerStartResult(
                    ServerStartOutcome.FailedToStart,
                    _snapshot,
                    Error: ex.Message);
            }

            session = new Session(process, generation);
            _session = session;
            var startedAt = TryGetStartedAt(process);
            _snapshot = _snapshot with { ProcessId = process.Id, StartedAt = startedAt };

            try
            {
                process.OutputDataReceived += (_, args) =>
                    OnOutput(session, ServerOutputStream.StandardOutput, args.Data, endpointPattern);
                process.ErrorDataReceived += (_, args) =>
                    OnOutput(session, ServerOutputStream.StandardError, args.Data, endpointPattern);
                process.Exited += (_, _) => OnExited(session);
                process.EnableRaisingEvents = true;
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                session.StopRequested = true;
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch { /* setup failure cleanup is best effort */ }
                _session = null;
                _snapshot = _snapshot with { Phase = ServerHostPhase.Failed, Error = ex.Message };
                DisposeSessionNoThrow(session);
                return new ServerStartResult(
                    ServerStartOutcome.FailedToStart,
                    _snapshot,
                    Error: ex.Message);
            }
        }

        var timeoutTask = Task.Delay(timeout, cancellationToken);
        var completed = await Task.WhenAny(session.Readiness.Task, timeoutTask).ConfigureAwait(false);
        if (completed == session.Readiness.Task)
        {
            var endpoint = await session.Readiness.Task.ConfigureAwait(false);
            lock (_sync)
            {
                if (!ReferenceEquals(_session, session) || session.StopRequested)
                    return new ServerStartResult(ServerStartOutcome.Cancelled, _snapshot);

                if (endpoint == null || _snapshot.Phase == ServerHostPhase.Exited)
                    return new ServerStartResult(ServerStartOutcome.ExitedBeforeReady, _snapshot);

                _snapshot = _snapshot with { Phase = ServerHostPhase.Ready, Endpoint = endpoint };
                return new ServerStartResult(ServerStartOutcome.Ready, _snapshot, endpoint);
            }
        }

        if (cancellationToken.IsCancellationRequested)
        {
            Stop(TimeSpan.FromSeconds(5));
            return new ServerStartResult(ServerStartOutcome.Cancelled, Snapshot);
        }

        lock (_sync)
        {
            if (ReferenceEquals(_session, session) && _snapshot.Phase == ServerHostPhase.Starting)
                _snapshot = _snapshot with { Phase = ServerHostPhase.TimedOut };
            return new ServerStartResult(ServerStartOutcome.TimedOut, _snapshot);
        }
    }

    public void Stop(TimeSpan waitTimeout)
    {
        if (waitTimeout < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(waitTimeout));

        Session? session;
        lock (_sync)
        {
            session = _session;
            _generation++;
            if (session == null)
            {
                _snapshot = new ServerHostSnapshot(_generation, ServerHostPhase.Stopped);
                return;
            }

            session.StopRequested = true;
            session.Readiness.TrySetResult(null);
            _snapshot = _snapshot with { Generation = _generation, Phase = ServerHostPhase.Stopping };
        }

        try
        {
            if (!session.Process.HasExited) session.Process.Kill(entireProcessTree: true);
        }
        catch
        {
            // The process may have exited between the state check and Kill.
        }

        if (waitTimeout > TimeSpan.Zero)
        {
            try { session.Process.WaitForExit((int)Math.Min(waitTimeout.TotalMilliseconds, int.MaxValue)); }
            catch { /* stopping is best effort */ }
        }

        lock (_sync)
        {
            if (!ReferenceEquals(_session, session)) return;
            var exitCode = TryGetExitCode(session.Process);
            _snapshot = new ServerHostSnapshot(_generation, ServerHostPhase.Stopped, ExitCode: exitCode);
            _session = null;
        }

        DisposeSessionNoThrow(session);
    }

    public static bool TryExtractEndpoint(Regex pattern, string line, out string endpoint)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var match = pattern.Match(line ?? string.Empty);
        if (match.Success && match.Groups.Count > 1 && !string.IsNullOrWhiteSpace(match.Groups[1].Value))
        {
            endpoint = match.Groups[1].Value;
            return true;
        }

        endpoint = string.Empty;
        return false;
    }

    public static string FormatExitCode(int? exitCode, bool isRunning = false)
    {
        if (isRunning) return "exit=未退出";
        if (exitCode == null) return "exit=不可用";
        return "exit=" + exitCode.Value + " (0x"
            + unchecked((uint)exitCode.Value).ToString("X8") + ")";
    }

    public void Dispose() => Stop(TimeSpan.FromSeconds(1));

    private void OnOutput(
        Session session,
        ServerOutputStream stream,
        string? line,
        Regex endpointPattern)
    {
        if (string.IsNullOrEmpty(line)) return;

        try { OutputReceived?.Invoke(this, new ServerOutputEventArgs(session.Generation, stream, line)); }
        catch { /* observers must not break process stream consumption */ }

        if (TryExtractEndpoint(endpointPattern, line, out var endpoint))
            session.Readiness.TrySetResult(endpoint);
    }

    private void OnExited(Session session)
    {
        var exitCode = TryGetExitCode(session.Process);

        lock (_sync)
        {
            if (ReferenceEquals(_session, session))
            {
                _snapshot = new ServerHostSnapshot(
                    session.Generation,
                    session.StopRequested ? ServerHostPhase.Stopped : ServerHostPhase.Exited,
                    session.Process.Id,
                    TryGetStartedAt(session.Process),
                    _snapshot.Endpoint,
                exitCode);
            }
        }

        // Publish the terminal snapshot before waking StartAsync; otherwise a fast exit can
        // return ExitedBeforeReady with the preceding Starting snapshot and no exit code.
        session.Readiness.TrySetResult(null);

        try
        {
            Exited?.Invoke(this, new ServerExitedEventArgs(
                session.Generation,
                exitCode,
                session.StopRequested));
        }
        catch
        {
            // Exit notification is diagnostic/control flow and must not escape a Process callback.
        }
    }

    private static DateTimeOffset? TryGetStartedAt(Process process)
    {
        try { return process.StartTime.ToUniversalTime(); }
        catch { return null; }
    }

    private static int? TryGetExitCode(Process process)
    {
        try { return process.HasExited ? process.ExitCode : null; }
        catch { return null; }
    }

    private static void DisposeSessionNoThrow(Session? session)
    {
        try { session?.Process.Dispose(); }
        catch { /* disposal is best effort */ }
    }
}
