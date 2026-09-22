using DSHDesktop.Core;
using Xunit;

namespace DSHDesktop.Tests;

public class UpdateCoordinatorTests
{
    private static readonly UpdateCandidate Candidate =
        new("1.0.0", "1.1.0", "1.2.0-beta.1", StableUpdate: false);

    [Fact]
    public void NewCoordinator_IsIdle()
    {
        var coordinator = new UpdateCoordinator();

        Assert.Equal(UpdatePhase.Idle, coordinator.Snapshot.Phase);
        Assert.False(coordinator.Snapshot.HasChecked);
        Assert.False(coordinator.Snapshot.IsBusy);
        Assert.False(coordinator.Snapshot.HasUpdate);
    }

    [Fact]
    public void AutomaticCheck_RunsOnceWhileManualCheckCanRetry()
    {
        var coordinator = new UpdateCoordinator();

        Assert.True(coordinator.TryBeginCheck(manual: false));
        Assert.True(coordinator.CompleteCheck(candidate: null));
        Assert.Equal(UpdatePhase.UpToDate, coordinator.Snapshot.Phase);
        Assert.False(coordinator.TryBeginCheck(manual: false));
        Assert.True(coordinator.TryBeginCheck(manual: true));
    }

    [Fact]
    public void CompleteCheck_StoresAvailableCandidate()
    {
        var coordinator = new UpdateCoordinator();
        coordinator.TryBeginCheck(manual: false);

        Assert.True(coordinator.CompleteCheck(Candidate));

        Assert.Equal(UpdatePhase.Available, coordinator.Snapshot.Phase);
        Assert.Equal(Candidate, coordinator.Snapshot.Candidate);
        Assert.True(coordinator.Snapshot.HasChecked);
    }

    [Fact]
    public void FailedInitialCheck_AllowsLaterAutomaticRetry()
    {
        var coordinator = new UpdateCoordinator();
        coordinator.TryBeginCheck(manual: false);

        Assert.True(coordinator.FailCheck("  offline  "));

        Assert.Equal(UpdatePhase.Failed, coordinator.Snapshot.Phase);
        Assert.Equal("offline", coordinator.Snapshot.Error);
        Assert.False(coordinator.Snapshot.HasChecked);
        Assert.True(coordinator.TryBeginCheck(manual: false));
    }

    [Fact]
    public void FailedManualRecheck_PreservesKnownCandidate()
    {
        var coordinator = CoordinatorWithCandidate();
        coordinator.TryBeginCheck(manual: true);

        Assert.True(coordinator.FailCheck("registry unavailable"));

        Assert.Equal(UpdatePhase.Available, coordinator.Snapshot.Phase);
        Assert.Equal(Candidate, coordinator.Snapshot.Candidate);
        Assert.Equal("registry unavailable", coordinator.Snapshot.Error);
    }

    [Fact]
    public void ApplyRequiresCandidate()
    {
        var coordinator = new UpdateCoordinator();

        Assert.False(coordinator.TryBeginApply(out var candidate));
        Assert.Null(candidate);
        Assert.Equal(UpdatePhase.Idle, coordinator.Snapshot.Phase);
    }

    [Fact]
    public void DismissCandidate_ClearsAvailableUpdateWithoutStartingAnApply()
    {
        var coordinator = CoordinatorWithCandidate();

        Assert.True(coordinator.TryDismissCandidate(out var dismissed));

        Assert.Equal(Candidate, dismissed);
        Assert.Equal(UpdatePhase.UpToDate, coordinator.Snapshot.Phase);
        Assert.Null(coordinator.Snapshot.Candidate);
        Assert.False(coordinator.Snapshot.IsBusy);
    }

    [Fact]
    public void SuccessfulApply_ClearsCandidateAndRecordsVersion()
    {
        var coordinator = CoordinatorWithCandidate();
        Assert.True(coordinator.TryBeginApply(out var candidate));
        Assert.Equal(Candidate, candidate);

        Assert.True(coordinator.CompleteApply(" 1.2.0-beta.1 "));

        Assert.Equal(UpdatePhase.Applied, coordinator.Snapshot.Phase);
        Assert.Null(coordinator.Snapshot.Candidate);
        Assert.Equal("1.2.0-beta.1", coordinator.Snapshot.InstalledVersion);
    }

    [Fact]
    public void FailedApply_PreservesCandidateForRetry()
    {
        var coordinator = CoordinatorWithCandidate();
        coordinator.TryBeginApply(out _);

        Assert.True(coordinator.FailApply("npm failed"));

        Assert.Equal(UpdatePhase.Available, coordinator.Snapshot.Phase);
        Assert.Equal(Candidate, coordinator.Snapshot.Candidate);
        Assert.Equal("npm failed", coordinator.Snapshot.Error);
        Assert.True(coordinator.TryBeginApply(out _));
    }

    [Theory]
    [InlineData(UpdatePhase.Idle, false)]
    [InlineData(UpdatePhase.Checking, true)]
    [InlineData(UpdatePhase.UpToDate, false)]
    [InlineData(UpdatePhase.Available, false)]
    [InlineData(UpdatePhase.Applying, true)]
    [InlineData(UpdatePhase.Applied, false)]
    [InlineData(UpdatePhase.Failed, false)]
    public void Snapshot_IsBusyOnlyForActiveOperations(UpdatePhase phase, bool expected)
    {
        Assert.Equal(expected, new UpdateSnapshot(phase, HasChecked: false).IsBusy);
    }

    private static UpdateCoordinator CoordinatorWithCandidate()
    {
        var coordinator = new UpdateCoordinator();
        coordinator.TryBeginCheck(manual: false);
        coordinator.CompleteCheck(Candidate);
        return coordinator;
    }
}
