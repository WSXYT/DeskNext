using DeskNest.Core.Organization;
using Xunit;

namespace DeskNest.Core.Tests;

public sealed class DesktopOrganizationMonitorTests
{
    [Fact]
    public async Task MonitorBuildsBaselineAndEmitsStableFileCandidatesOutsideExclusions()
    {
        using var root = new TemporaryDirectory();
        string excluded = Directory.CreateDirectory(Path.Combine(root.Path, "excluded")).FullName;
        await File.WriteAllTextAsync(Path.Combine(root.Path, "existing.txt"), "existing");
        var candidates = new TaskCompletionSource<DesktopOrganizationMonitorCandidate>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var monitor = new DesktopOrganizationMonitor(
            new DesktopOrganizationMonitorOptions(
                [root.Path], [excluded], QueueCapacity: 8, StabilityWindow: TimeSpan.FromMilliseconds(25)),
            batch =>
            {
                var candidate = batch.FirstOrDefault(item => item.Path.EndsWith("new.txt", StringComparison.OrdinalIgnoreCase));
                if (candidate is not null)
                    candidates.TrySetResult(candidate);
                return Task.CompletedTask;
            });

        await monitor.StartAsync();
        Assert.Contains(Path.Combine(root.Path, "existing.txt"), monitor.BaselinePaths);

        string newPath = Path.Combine(root.Path, "new.txt");
        await File.WriteAllTextAsync(newPath, "new");
        var observed = await Task.WhenAny(candidates.Task, Task.Delay(TimeSpan.FromSeconds(3)));

        Assert.Same(candidates.Task, observed);
        var fileCandidate = await candidates.Task;
        Assert.True(fileCandidate.IsStable);
        Assert.Equal(DesktopOrganizationExclusionReason.None, fileCandidate.ExclusionReason);
        Assert.False(monitor.HasOverflowed);

        await File.WriteAllTextAsync(Path.Combine(excluded, "ignored.txt"), "ignored");
        await Task.Delay(100);
        Assert.False(candidates.Task.IsCanceled);
    }

    [Fact]
    public async Task MonitorMarksDirectoriesAsManualOptInCandidatesAndNeverMovesThem()
    {
        using var root = new TemporaryDirectory();
        var candidates = new TaskCompletionSource<DesktopOrganizationMonitorCandidate>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var monitor = new DesktopOrganizationMonitor(
            new DesktopOrganizationMonitorOptions(
                [root.Path], [], QueueCapacity: 8, StabilityWindow: TimeSpan.FromMilliseconds(20)),
            batch =>
            {
                var candidate = batch.FirstOrDefault(item => item.Path.EndsWith("new-folder", StringComparison.OrdinalIgnoreCase));
                if (candidate is not null)
                    candidates.TrySetResult(candidate);
                return Task.CompletedTask;
            });

        await monitor.StartAsync();
        string nested = Path.Combine(root.Path, "new-folder");
        Directory.CreateDirectory(nested);
        var observed = await Task.WhenAny(candidates.Task, Task.Delay(TimeSpan.FromSeconds(3)));

        Assert.Same(candidates.Task, observed);
        var directoryCandidate = await candidates.Task;
        Assert.True(directoryCandidate.IsDirectory);
        Assert.Equal(DesktopOrganizationExclusionReason.Folder, directoryCandidate.ExclusionReason);
        Assert.True(Directory.Exists(nested));
    }

    [Fact]
    public async Task DirectoryCandidateIsUnstableWhenNestedContentChanges()
    {
        using var root = new TemporaryDirectory();
        await using var monitor = new DesktopOrganizationMonitor(
            new DesktopOrganizationMonitorOptions([root.Path], []), _ => Task.CompletedTask);
        string project = Directory.CreateDirectory(Path.Combine(root.Path, "project")).FullName;
        var result = await monitor.WaitForStabilityAsync(project, true, default,
            _ => File.WriteAllTextAsync(Path.Combine(project, "nested.txt"), "still-writing"));
        Assert.False(result.Stable);
        Assert.True(result.WithinBudget);
        Assert.False(result.ReparsePoint);
    }

    [Fact]
    public async Task DirectoryCandidateBecomesManualFolderWhenBoundedTreeIsStable()
    {
        using var root = new TemporaryDirectory();
        var candidates = new TaskCompletionSource<DesktopOrganizationMonitorCandidate>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var monitor = new DesktopOrganizationMonitor(
            new DesktopOrganizationMonitorOptions([root.Path], [], QueueCapacity: 8,
                StabilityWindow: TimeSpan.FromMilliseconds(20), DirectoryEntryLimit: 4),
            batch =>
            {
                var candidate = batch.FirstOrDefault(item => item.Path.EndsWith("stable", StringComparison.OrdinalIgnoreCase));
                if (candidate is not null) candidates.TrySetResult(candidate);
                return Task.CompletedTask;
            });
        using var staging = new TemporaryDirectory();
        string prepared = Directory.CreateDirectory(Path.Combine(staging.Path, "prepared")).FullName;
        string nested = Directory.CreateDirectory(Path.Combine(prepared, "nested")).FullName;
        File.WriteAllText(Path.Combine(nested, "one.txt"), "one");
        await monitor.StartAsync();
        string stable = Path.Combine(root.Path, "stable");
        Directory.Move(prepared, stable);
        var observed = await Task.WhenAny(candidates.Task, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.Same(candidates.Task, observed);
        var candidate = await candidates.Task;
        Assert.True(candidate.IsStable);
        Assert.Equal(DesktopOrganizationExclusionReason.Folder, candidate.ExclusionReason);
    }


    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateDirectory(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "DeskNest.Monitor.Tests", Guid.NewGuid().ToString("N"))).FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
