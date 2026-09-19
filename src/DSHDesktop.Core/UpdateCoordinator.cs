namespace DSHDesktop.Core;

public enum UpdatePhase
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Applying,
    Applied,
    Failed,
}

public sealed record UpdateCandidate(
    string Current,
    string Latest,
    string Newest,
    bool StableUpdate);

public sealed record UpdateSnapshot(
    UpdatePhase Phase,
    bool HasChecked,
    UpdateCandidate? Candidate = null,
    string? Error = null,
    string? InstalledVersion = null,
    long Revision = 0)
{
    public bool IsBusy => Phase is UpdatePhase.Checking or UpdatePhase.Applying;
    public bool HasUpdate => Candidate != null;
}

public interface IUpdateCoordinator
{
    UpdateSnapshot Snapshot { get; }
    bool TryBeginCheck(bool manual);
    bool CompleteCheck(UpdateCandidate? candidate);
    bool FailCheck(string error);
    bool TryBeginApply(out UpdateCandidate? candidate);
    bool CompleteApply(string installedVersion);
    bool FailApply(string error);
}

/// <summary>
/// Serializes update checks and application attempts while preserving a known candidate across
/// transient failures. Network, backup, package installation, and UI remain injected side effects.
/// </summary>
public sealed class UpdateCoordinator : IUpdateCoordinator
{
    private readonly object _sync = new();
    private UpdateSnapshot _snapshot = new(UpdatePhase.Idle, HasChecked: false);

    public UpdateSnapshot Snapshot
    {
        get { lock (_sync) return _snapshot; }
    }

    public bool TryBeginCheck(bool manual)
    {
        lock (_sync)
        {
            if (_snapshot.IsBusy || (_snapshot.HasChecked && !manual)) return false;
            _snapshot = Next(UpdatePhase.Checking, _snapshot.HasChecked, _snapshot.Candidate);
            return true;
        }
    }

    public bool CompleteCheck(UpdateCandidate? candidate)
    {
        lock (_sync)
        {
            if (_snapshot.Phase != UpdatePhase.Checking) return false;
            _snapshot = candidate == null
                ? Next(UpdatePhase.UpToDate, hasChecked: true)
                : Next(UpdatePhase.Available, hasChecked: true, candidate);
            return true;
        }
    }

    public bool FailCheck(string error)
    {
        lock (_sync)
        {
            if (_snapshot.Phase != UpdatePhase.Checking) return false;
            _snapshot = _snapshot.Candidate == null
                ? Next(UpdatePhase.Failed, _snapshot.HasChecked, error: NormalizeError(error))
                : Next(
                    UpdatePhase.Available,
                    _snapshot.HasChecked,
                    _snapshot.Candidate,
                    NormalizeError(error));
            return true;
        }
    }

    public bool TryBeginApply(out UpdateCandidate? candidate)
    {
        lock (_sync)
        {
            candidate = _snapshot.Candidate;
            if (_snapshot.IsBusy || candidate == null) return false;
            _snapshot = Next(UpdatePhase.Applying, _snapshot.HasChecked, candidate);
            return true;
        }
    }

    public bool CompleteApply(string installedVersion)
    {
        lock (_sync)
        {
            if (_snapshot.Phase != UpdatePhase.Applying) return false;
            _snapshot = Next(
                UpdatePhase.Applied,
                hasChecked: true,
                installedVersion: string.IsNullOrWhiteSpace(installedVersion)
                    ? null
                    : installedVersion.Trim());
            return true;
        }
    }

    public bool FailApply(string error)
    {
        lock (_sync)
        {
            if (_snapshot.Phase != UpdatePhase.Applying) return false;
            _snapshot = Next(
                UpdatePhase.Available,
                _snapshot.HasChecked,
                _snapshot.Candidate,
                NormalizeError(error));
            return true;
        }
    }

    private UpdateSnapshot Next(
        UpdatePhase phase,
        bool hasChecked,
        UpdateCandidate? candidate = null,
        string? error = null,
        string? installedVersion = null)
        => new(phase, hasChecked, candidate, error, installedVersion, _snapshot.Revision + 1);

    private static string NormalizeError(string error)
        => string.IsNullOrWhiteSpace(error) ? "unknown" : error.Trim();
}
