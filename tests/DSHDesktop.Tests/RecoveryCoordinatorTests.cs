using DSHDesktop.Core;
using Xunit;

namespace DSHDesktop.Tests;

public class RecoveryCoordinatorTests
{
    private static StartupFailure.Detail Failure(int exitCode = 17)
        => StartupFailure.Create(
            StartupFailure.Kind.BackendExited,
            ServerHost.FormatExitCode(exitCode));

    [Fact]
    public void NewCoordinator_MonitorsWithEmptyBudget()
    {
        var coordinator = new RecoveryCoordinator(maximumAutomaticRestarts: 3);

        Assert.Equal(RecoveryPhase.Monitoring, coordinator.Snapshot.Phase);
        Assert.Equal(0, coordinator.Snapshot.AutomaticRestartCount);
        Assert.Equal(3, coordinator.Snapshot.MaximumAutomaticRestarts);
        Assert.Null(coordinator.Snapshot.LastFailure);
    }

    [Fact]
    public void UnexpectedExit_SchedulesAutomaticRestartWithinBudget()
    {
        var coordinator = new RecoveryCoordinator(maximumAutomaticRestarts: 3);
        var failure = Failure();

        var decision = coordinator.HandleUnexpectedExit(generation: 4, failure);

        Assert.Equal(RecoveryDirective.RestartAutomatically, decision.Directive);
        Assert.Equal(1, decision.AutomaticRestartCount);
        Assert.Equal(RecoveryPhase.AutomaticRestartPending, coordinator.Snapshot.Phase);
        Assert.Equal(failure, coordinator.Snapshot.LastFailure);
    }

    [Fact]
    public void RepeatedCallbackForSameGeneration_IsIgnored()
    {
        var coordinator = new RecoveryCoordinator(maximumAutomaticRestarts: 3);

        coordinator.HandleUnexpectedExit(generation: 4, Failure());
        var duplicate = coordinator.HandleUnexpectedExit(generation: 4, Failure());

        Assert.Equal(RecoveryDirective.Ignore, duplicate.Directive);
        Assert.Equal(1, coordinator.Snapshot.AutomaticRestartCount);
        Assert.Equal(1, coordinator.Snapshot.Revision);
    }

    [Fact]
    public void ExitAfterBudgetIsExhausted_RequiresAssistant()
    {
        var coordinator = new RecoveryCoordinator(maximumAutomaticRestarts: 2);

        Assert.Equal(
            RecoveryDirective.RestartAutomatically,
            coordinator.HandleUnexpectedExit(1, Failure(1)).Directive);
        Assert.Equal(
            RecoveryDirective.RestartAutomatically,
            coordinator.HandleUnexpectedExit(2, Failure(2)).Directive);
        var decision = coordinator.HandleUnexpectedExit(3, Failure(3));

        Assert.Equal(RecoveryDirective.ShowAssistant, decision.Directive);
        Assert.Equal(2, decision.AutomaticRestartCount);
        Assert.Equal(RecoveryPhase.AssistanceRequired, coordinator.Snapshot.Phase);
        Assert.Equal(Failure(3), coordinator.Snapshot.LastFailure);
    }

    [Fact]
    public void ZeroRestartBudget_RequiresAssistantImmediately()
    {
        var coordinator = new RecoveryCoordinator(maximumAutomaticRestarts: 0);

        var decision = coordinator.HandleUnexpectedExit(1, Failure());

        Assert.Equal(RecoveryDirective.ShowAssistant, decision.Directive);
        Assert.Equal(0, coordinator.Snapshot.AutomaticRestartCount);
    }

    [Fact]
    public void HealthyState_ResetsBudgetAndGenerationDeduplication()
    {
        var coordinator = new RecoveryCoordinator(maximumAutomaticRestarts: 1);
        coordinator.HandleUnexpectedExit(7, Failure());

        coordinator.RecordHealthy();
        var next = coordinator.HandleUnexpectedExit(7, Failure(8));

        Assert.Equal(RecoveryDirective.RestartAutomatically, next.Directive);
        Assert.Equal(1, next.AutomaticRestartCount);
        Assert.Equal(Failure(8), coordinator.Snapshot.LastFailure);
    }

    [Fact]
    public void ManualRetry_ResetsAutomaticRestartBudgetButRetainsDiagnosis()
    {
        var coordinator = new RecoveryCoordinator(maximumAutomaticRestarts: 1);
        var failure = Failure();
        coordinator.HandleUnexpectedExit(1, failure);
        coordinator.HandleUnexpectedExit(2, failure);

        coordinator.PrepareManualRetry();

        Assert.Equal(RecoveryPhase.ManualRetryPending, coordinator.Snapshot.Phase);
        Assert.Equal(0, coordinator.Snapshot.AutomaticRestartCount);
        Assert.Null(coordinator.Snapshot.LastHandledGeneration);
        Assert.Equal(failure, coordinator.Snapshot.LastFailure);
    }

    [Fact]
    public void ManualRecoveryEntry_PreservesCurrentFailureAndBudget()
    {
        var coordinator = new RecoveryCoordinator(maximumAutomaticRestarts: 3);
        var failure = Failure();
        coordinator.HandleUnexpectedExit(1, failure);

        coordinator.EnterAssistance();

        Assert.Equal(RecoveryPhase.AssistanceRequired, coordinator.Snapshot.Phase);
        Assert.Equal(1, coordinator.Snapshot.AutomaticRestartCount);
        Assert.Equal(failure, coordinator.Snapshot.LastFailure);
    }

    [Fact]
    public void NegativeRestartBudget_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new RecoveryCoordinator(maximumAutomaticRestarts: -1));
    }
}
