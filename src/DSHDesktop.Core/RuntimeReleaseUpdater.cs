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
        CancellationToken cancellationToken = default,
        Func<RuntimeReleaseDescriptor, string?>? descriptorValidator = null)
    {
        var acquisition = await AcquireAsync(source, cancellationToken, descriptorValidator).ConfigureAwait(false);
        return ActivateAcquiredCandidate(acquisition);
    }

    /// <summary>
    /// Downloads, verifies, and safely unpacks a release into this store's staging directory
    /// without changing the active slot. Hosts may use this boundary to obtain a second user
    /// confirmation before installation.
    /// </summary>
    public Task<RuntimeReleaseAcquireResult> AcquireAsync(
        RuntimeReleaseSource source,
        CancellationToken cancellationToken = default,
        Func<RuntimeReleaseDescriptor, string?>? descriptorValidator = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        return _acquirer.AcquireAsync(
            source,
            Path.Combine(_runtimeRoot, RuntimeSlotManager.StagingDirectoryName),
            cancellationToken,
            descriptorValidator);
    }

    /// <summary>
    /// Re-verifies and atomically activates an acquired staging candidate. The candidate path is
    /// constrained by <see cref="RuntimeUpdateStager"/>, so callers cannot use this to adopt an
    /// arbitrary directory.
    /// </summary>
    public RuntimeReleaseUpdateResult ActivateAcquiredCandidate(RuntimeReleaseAcquireResult acquisition)
    {
        ArgumentNullException.ThrowIfNull(acquisition);
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

    /// <summary>
    /// Removes a verified-but-unaccepted candidate after a user declines installation. Only a
    /// direct child of this runtime store's staging directory is eligible; paths outside staging
    /// are rejected without filesystem changes.
    /// </summary>
    public bool DiscardAcquiredCandidate(RuntimeReleaseAcquireResult acquisition)
    {
        ArgumentNullException.ThrowIfNull(acquisition);
        if (!acquisition.Success || string.IsNullOrWhiteSpace(acquisition.RuntimeDirectory)) return false;

        try
        {
            var stagingRoot = Path.GetFullPath(Path.Combine(_runtimeRoot, RuntimeSlotManager.StagingDirectoryName));
            var candidate = Path.GetFullPath(acquisition.RuntimeDirectory);
            if (!string.Equals(Path.GetDirectoryName(candidate), stagingRoot, StringComparison.OrdinalIgnoreCase)
                || !Directory.Exists(candidate))
            {
                return false;
            }

            Directory.Delete(candidate, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
