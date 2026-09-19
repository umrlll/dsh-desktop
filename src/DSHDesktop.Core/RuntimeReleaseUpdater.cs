namespace DSHDesktop.Core;

public enum RuntimeReleaseUpdateStatus
{
    Activated,
    InvalidRequest,
    AcquisitionFailed,
    CandidateRejected,
    Cancelled,
}

public sealed record RuntimeReleaseUpdateResult(
    RuntimeReleaseUpdateStatus Status,
    RuntimeReleaseAcquireResult? Acquisition = null,
    RuntimeUpdateStageResult? Adoption = null,
    string? Error = null)
{
    public bool Success => Status == RuntimeReleaseUpdateStatus.Activated;
}

/// <summary>
/// Connects a configured release source to the immutable runtime-slot update path. It does not
/// embed trust roots or feed endpoints: those remain explicit deployment configuration. A UI host
/// can run its health gate after a successful activation and use <see cref="RuntimeSlotManager"/>
/// to roll back if that gate fails.
/// </summary>
public sealed class RuntimeReleaseUpdater
{
    private readonly string _runtimeRoot;
    private readonly RuntimeReleaseAcquirer _acquirer;
    private readonly RuntimeUpdateStager _stager;

    public RuntimeReleaseUpdater(string runtimeRoot, RuntimeReleaseAcquirer acquirer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        _runtimeRoot = Path.GetFullPath(runtimeRoot);
        _acquirer = acquirer ?? throw new ArgumentNullException(nameof(acquirer));
        _stager = new RuntimeUpdateStager(_runtimeRoot);
    }

    public async Task<RuntimeReleaseUpdateResult> AcquireAndActivateAsync(
        RuntimeReleaseSource source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var acquisition = await _acquirer.AcquireAsync(
            source,
            Path.Combine(_runtimeRoot, RuntimeSlotManager.StagingDirectoryName),
            cancellationToken).ConfigureAwait(false);
        if (!acquisition.Success)
        {
            return new RuntimeReleaseUpdateResult(
                acquisition.Status == RuntimeReleaseAcquireStatus.Cancelled
                    ? RuntimeReleaseUpdateStatus.Cancelled
                    : acquisition.Status == RuntimeReleaseAcquireStatus.InvalidRequest
                        ? RuntimeReleaseUpdateStatus.InvalidRequest
                        : RuntimeReleaseUpdateStatus.AcquisitionFailed,
                Acquisition: acquisition,
                Error: acquisition.Error);
        }

        var adoption = _stager.AdoptAndActivateCandidate(acquisition.RuntimeDirectory!);
        if (!adoption.Success)
        {
            return new RuntimeReleaseUpdateResult(
                RuntimeReleaseUpdateStatus.CandidateRejected,
                acquisition,
                adoption,
                adoption.Error);
        }

        return new RuntimeReleaseUpdateResult(
            RuntimeReleaseUpdateStatus.Activated,
            acquisition,
            adoption);
    }
}
