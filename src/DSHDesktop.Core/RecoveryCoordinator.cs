namespace DSHDesktop.Core;

public enum RecoveryPhase
{
    Monitoring,
    AutomaticRestartPending,
    AssistanceRequired,
    ManualRetryPending,
    Healthy,
}

public enum RecoveryDirective
{
    Ignore,
    RestartAutomatically,
    ShowAssistant,
}

public sealed record RecoverySnapshot(
    RecoveryPhase Phase,
    int AutomaticRestartCount,
    int MaximumAutomaticRestarts,
    int? LastHandledGeneration = null,
    StartupFailure.Detail? LastFailure = null,
    long Revision = 0);

public sealed record RecoveryDecision(
    RecoveryDirective Directive,
    int AutomaticRestartCount,
    int MaximumAutomaticRestarts,
    StartupFailure.Detail Failure);

public interface IRecoveryCoordinator
{
    RecoverySnapshot Snapshot { get; }
    RecoveryDecision HandleUnexpectedExit(int generation, StartupFailure.Detail failure);
    void EnterAssistance();
    void PrepareManualRetry();
    void RecordHealthy();
}

/// <summary>
/// Owns the deterministic recovery policy for an unexpectedly exited server. File snapshots,
/// plugin changes, dialogs, delays, and process launches remain shell-side effects.
/// </summary>
public sealed class RecoveryCoordinator : IRecoveryCoordinator
{
    private readonly object _sync = new();
    private RecoverySnapshot _snapshot;

    public RecoveryCoordinator(int maximumAutomaticRestarts = 3)
    {
        if (maximumAutomaticRestarts < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumAutomaticRestarts));

        _snapshot = new RecoverySnapshot(
            RecoveryPhase.Monitoring,
            AutomaticRestartCount: 0,
            MaximumAutomaticRestarts: maximumAutomaticRestarts);
    }

    public RecoverySnapshot Snapshot
    {
        get { lock (_sync) return _snapshot; }
    }

    public RecoveryDecision HandleUnexpectedExit(int generation, StartupFailure.Detail failure)
    {
        lock (_sync)
        {
            if (_snapshot.LastHandledGeneration == generation)
                return Decision(RecoveryDirective.Ignore, failure);

            if (_snapshot.AutomaticRestartCount >= _snapshot.MaximumAutomaticRestarts)
            {
                _snapshot = Next(
                    RecoveryPhase.AssistanceRequired,
                    _snapshot.AutomaticRestartCount,
                    generation,
                    failure);
                return Decision(RecoveryDirective.ShowAssistant, failure);
            }

            var attempt = _snapshot.AutomaticRestartCount + 1;
            _snapshot = Next(
                RecoveryPhase.AutomaticRestartPending,
                attempt,
                generation,
                failure);
            return Decision(RecoveryDirective.RestartAutomatically, failure);
        }
    }

    public void EnterAssistance()
    {
        lock (_sync)
            _snapshot = Next(
                RecoveryPhase.AssistanceRequired,
                _snapshot.AutomaticRestartCount,
                _snapshot.LastHandledGeneration,
                _snapshot.LastFailure);
    }

    public void PrepareManualRetry()
    {
        lock (_sync)
            _snapshot = Next(
                RecoveryPhase.ManualRetryPending,
                automaticRestartCount: 0,
                lastHandledGeneration: null,
                _snapshot.LastFailure);
    }

    public void RecordHealthy()
    {
        lock (_sync)
            _snapshot = Next(
                RecoveryPhase.Healthy,
                automaticRestartCount: 0,
                lastHandledGeneration: null,
                lastFailure: null);
    }

    private RecoveryDecision Decision(
        RecoveryDirective directive,
        StartupFailure.Detail failure)
        => new(
            directive,
            _snapshot.AutomaticRestartCount,
            _snapshot.MaximumAutomaticRestarts,
            failure);

    private RecoverySnapshot Next(
        RecoveryPhase phase,
        int automaticRestartCount,
        int? lastHandledGeneration,
        StartupFailure.Detail? lastFailure)
        => new(
            phase,
            automaticRestartCount,
            _snapshot.MaximumAutomaticRestarts,
            lastHandledGeneration,
            lastFailure,
            _snapshot.Revision + 1);
}
