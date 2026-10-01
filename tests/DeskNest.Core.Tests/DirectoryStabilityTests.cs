using DeskNest.Core.Organization;
using Xunit;

namespace DeskNest.Core.Tests;

public sealed class DirectoryStabilityTests : IDisposable
{
    private readonly string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),
        "DeskNest-stability-" + Guid.NewGuid().ToString("N"))).FullName;

    [Fact]
    public async Task StableNestedTreeFitsExactEntryBudget()
    {
        string nested = Directory.CreateDirectory(Path.Combine(root, "nested")).FullName;
        Directory.CreateDirectory(Path.Combine(nested, "empty"));
        await File.WriteAllTextAsync(Path.Combine(nested, "file.txt"), "data");
        await using var monitor = CreateMonitor(3);
        var result = await monitor.WaitForStabilityAsync(root, true, default, _ => Task.CompletedTask);
        Assert.True(result.Stable);
        Assert.True(result.WithinBudget);
        Assert.False(result.ReparsePoint);
    }

    [Fact]
    public async Task NestedWriteIsDetectedEvenWhenRootTimestampIsUnchanged()
    {
        string nested = Directory.CreateDirectory(Path.Combine(root, "nested")).FullName;
        string file = Path.Combine(nested, "file.txt");
        await File.WriteAllTextAsync(file, "before");
        var timestamp = Directory.GetLastWriteTimeUtc(root);
        await using var monitor = CreateMonitor(2);
        var result = await monitor.WaitForStabilityAsync(root, true, default, async _ =>
        {
            await File.WriteAllTextAsync(file, "after with different length");
            Directory.SetLastWriteTimeUtc(root, timestamp);
        });
        Assert.False(result.Stable);
        Assert.True(result.WithinBudget);
        Assert.False(result.ReparsePoint);
        Assert.Equal(timestamp, Directory.GetLastWriteTimeUtc(root));
    }

    [Fact]
    public async Task OverBudgetTreeCannotBeStable()
    {
        await File.WriteAllTextAsync(Path.Combine(root, "one"), "1");
        await File.WriteAllTextAsync(Path.Combine(root, "two"), "2");
        await using var monitor = CreateMonitor(1);
        var result = await monitor.WaitForStabilityAsync(root, true, default, _ => Task.CompletedTask);
        Assert.False(result.Stable);
        Assert.False(result.WithinBudget);
    }

    [Fact]
    public async Task CancellationStopsBeforeSampling()
    {
        await using var monitor = CreateMonitor(1);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        bool waited = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => monitor.WaitForStabilityAsync(
            root, true, cancelled.Token, _ => { waited = true; return Task.CompletedTask; }));
        Assert.False(waited);
    }

    private DesktopOrganizationMonitor CreateMonitor(int limit) => new(
        new DesktopOrganizationMonitorOptions([root], [], DirectoryEntryLimit: limit),
        _ => Task.CompletedTask);

    public void Dispose() => Directory.Delete(root, recursive: true);
}
