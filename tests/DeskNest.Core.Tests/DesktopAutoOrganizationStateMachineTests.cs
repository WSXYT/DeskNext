using DeskNest.Core.Organization;
using Xunit;

namespace DeskNest.Core.Tests;

public sealed class DesktopAutoOrganizationStateMachineTests
{
    [Fact]
    public void NewNotification_InvalidatesStaleGenerationAndPreservesRetryBudget()
    {
        var machine = new DesktopAutoOrganizationStateMachine();
        var first = machine.BeginPending("/desktop/file.txt");
        Assert.True(machine.MarkDeferred(first, DateTimeOffset.UtcNow));
        var changed = machine.BeginPending(first.Path, preserveRetryAttempts: true);
        Assert.Equal(1, machine.GetSnapshot(first.Path)?.RetryAttempts);
        Assert.NotEqual(first.Generation, changed.Generation);
        Assert.False(machine.TryTransition(first,
            DesktopAutoOrganizationItemState.Deferred, DesktopAutoOrganizationItemState.Processing));
        Assert.True(machine.TryTransition(changed,
            DesktopAutoOrganizationItemState.Pending, DesktopAutoOrganizationItemState.Settling));
    }

    [Fact]
    public void SuspendedSettlingItem_CannotCompleteWithOldGeneration()
    {
        var machine = new DesktopAutoOrganizationStateMachine();
        var item = machine.BeginPending("/desktop/file.txt");
        Assert.True(machine.TryTransition(item,
            DesktopAutoOrganizationItemState.Pending, DesktopAutoOrganizationItemState.Settling));
        Assert.Single(machine.SuspendRecoverableItems());
        Assert.False(machine.MarkTerminal(item, DesktopAutoOrganizationItemState.Completed));
        var now = DateTimeOffset.UtcNow;
        machine.ResumeDeferred(now);
        var resumed = Assert.Single(machine.TakeDueDeferred(now));
        Assert.NotEqual(item.Generation, resumed.Generation);
        Assert.Empty(machine.TakeDueDeferred(now));
    }

    [Fact]
    public void ProcessingIsNotInvalidatedBySuspend_BecauseFileMoveMayBeInFlight()
    {
        var machine = new DesktopAutoOrganizationStateMachine();
        var item = machine.BeginPending("/desktop/file.txt");
        Assert.True(machine.TryTransition(item,
            DesktopAutoOrganizationItemState.Pending, DesktopAutoOrganizationItemState.Processing));
        Assert.Empty(machine.SuspendRecoverableItems());
        Assert.True(machine.MarkTerminal(item, DesktopAutoOrganizationItemState.Completed));
    }

    [Fact]
    public void PathComparison_UsesPlatformCaseRules()
    {
        var machine = new DesktopAutoOrganizationStateMachine();
        var upper = machine.BeginPending("/desktop/FILE.txt");
        var lower = machine.BeginPending("/desktop/file.txt");
        Assert.Equal(OperatingSystem.IsWindows(),
            !machine.IsCurrent(upper, DesktopAutoOrganizationItemState.Pending));
        Assert.True(machine.IsCurrent(lower, DesktopAutoOrganizationItemState.Pending));
    }
}
