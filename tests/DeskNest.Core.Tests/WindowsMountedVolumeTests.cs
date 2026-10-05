using System.Text.Json;
using DeskNest.Core.Storage;
using Xunit;
using Xunit.Abstractions;

namespace DeskNest.Core.Tests;

// The hosted PowerShell fixture owns the VHDX and advances these three fresh-process phases.
public sealed class WindowsMountedVolumeTests(ITestOutputHelper output)
{
    private const string Marker = "DeskNext Windows volume fixture";

    [HostedWindowsVolumeFact("attached")]
    public async Task AttachedMountRefusesBatchesAndRecordsRecoverableMoves()
    {
        var (root, mount, data) = Fixture(online: true);
        string volume = FileSystemVolume.Identify(Path.Combine(data, "owner"));
        Assert.NotEqual(FileSystemVolume.Identify(root), volume);
        string mountedData = Path.Combine(mount, Path.GetFileName(root));
        Assert.Equal(volume, FileSystemVolume.Identify(Path.Combine(mountedData, "owner")));
        Assert.True((File.GetAttributes(mount) & FileAttributes.ReparsePoint) != 0);
        await File.WriteAllTextAsync(Path.Combine(root, "volume-id"), volume);
        foreach (bool directory in new[] { false, true })
        {
            await RefuseBatch(root, mountedData, sourceThroughMount: false, directory, "mounted-target");
            await RefuseBatch(root, mountedData, sourceThroughMount: true, directory, "mounted-source");
            string name = directory ? "receipt-tree" : "receipt-file";
            string source = Path.Combine(data, name), destination = source + "-moved";
            string journal = Path.Combine(root, name + ".json");
            CreateSource(source, directory);
            var transaction = new DesktopOrganizationTransaction(journal);
            if (directory) await transaction.ExecuteDirectoriesAsync([new(source, destination)], retainJournalUntilCommit: true);
            else await transaction.ExecuteAsync([new(source, destination)], retainJournalUntilCommit: true);
            Assert.True(transaction.HasRecoveryJournal);
            Assert.False(Exists(source));
            AssertContent(destination, directory);
            await File.WriteAllBytesAsync(journal + ".saved", await File.ReadAllBytesAsync(journal));
        }
        output.WriteLine(JsonSerializer.Serialize(new { phase = "attached", realMountRefusal = true, receiptsPersisted = true }));
    }

    [HostedWindowsVolumeFact("detached")]
    public async Task UnavailableVolumePreservesJournalsAndRefusesNewBatches()
    {
        var (root, _, data) = Fixture(online: false);
        Assert.False(Directory.Exists(Path.GetPathRoot(data)));
        foreach (bool directory in new[] { false, true })
        {
            string name = directory ? "receipt-tree" : "receipt-file";
            string journal = Path.Combine(root, name + ".json");
            var transaction = new DesktopOrganizationTransaction(journal);
            await Assert.ThrowsAnyAsync<IOException>(() => transaction.RecoverAsync());
            Assert.True(transaction.HasRecoveryJournal);
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var before = JsonSerializer.Deserialize<OrganizationRecoveryJournal>(await File.ReadAllTextAsync(journal + ".saved"), options)!;
            var after = JsonSerializer.Deserialize<OrganizationRecoveryJournal>(await File.ReadAllTextAsync(journal), options)!;
            Assert.Equal("Recovering", after.Status);
            Assert.True(File.Exists(journal + ".rollback-started"));
            // Recovery records its intent even when a volume is absent. Only these two header
            // fields may change; identity, topology, operation ID and every receipt flag must survive.
            Assert.Equal(JsonSerializer.Serialize(before),
                JsonSerializer.Serialize(after with { Status = before.Status, UpdatedAt = before.UpdatedAt }));
            Assert.False(Exists(Path.Combine(data, name)));
            Assert.False(Exists(Path.Combine(data, name + "-moved")));
            await RefuseBatch(root, data, sourceThroughMount: false, directory, "offline-target");
        }
        output.WriteLine(JsonSerializer.Serialize(new { phase = "detached", journalsPreserved = true, newBatchesRefused = true }));
    }

    [HostedWindowsVolumeFact("reattached")]
    public async Task SameVolumeReturnsAndOriginalReceiptsAuthorizeRecovery()
    {
        var (root, _, data) = Fixture(online: true);
        Assert.Equal(await File.ReadAllTextAsync(Path.Combine(root, "volume-id")),
            FileSystemVolume.Identify(Path.Combine(data, "owner")));
        foreach (bool directory in new[] { false, true })
        {
            string name = directory ? "receipt-tree" : "receipt-file";
            string source = Path.Combine(data, name), destination = source + "-moved";
            string journal = Path.Combine(root, name + ".json");
            Assert.False(Exists(source));
            AssertContent(destination, directory);
            var transaction = new DesktopOrganizationTransaction(journal);
            await transaction.RecoverAsync();
            Assert.False(transaction.HasRecoveryJournal);
            AssertContent(source, directory);
            Assert.False(Exists(destination));
        }
        output.WriteLine(JsonSerializer.Serialize(new { phase = "reattached", sameVolume = true, receiptBackedRecovery = true }));
    }

    private static async Task RefuseBatch(string root, string other, bool sourceThroughMount, bool directory, string label)
    {
        string prefix = label + (directory ? "-tree" : "-file");
        string first = Path.Combine(root, prefix + "-first");
        string second = Path.Combine(sourceThroughMount ? other : root, prefix + "-second");
        string firstTarget = Path.Combine(root, prefix + "-moved");
        string newParent = Path.Combine(sourceThroughMount ? root : other, prefix + "-new-parent");
        CreateSource(first, directory);
        CreateSource(second, directory);
        int enteredForwardPhase = 0;
        bool BeforeMove() { enteredForwardPhase++; return true; }
        var transaction = new DesktopOrganizationTransaction(Path.Combine(root, prefix + ".json"),
            moveGuard: _ => BeforeMove(), directoryMoveGuard: _ => BeforeMove());
        await Assert.ThrowsAnyAsync<IOException>(() => directory
            ? transaction.ExecuteDirectoriesAsync([new(first, firstTarget), new(second, Path.Combine(newParent, "item"))])
            : transaction.ExecuteAsync([new(first, firstTarget), new(second, Path.Combine(newParent, "item"))]));
        Assert.Equal(0, enteredForwardPhase);
        AssertContent(first, directory);
        AssertContent(second, directory);
        Assert.False(Exists(firstTarget));
        Assert.False(Directory.Exists(newParent));
        Assert.False(transaction.HasRecoveryJournal);
    }

    private static (string Root, string Mount, string Data) Fixture(bool online)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
            Environment.GetEnvironmentVariable("DESKNEXT_WINDOWS_VOLUME_ROOT")!));
        string data = Environment.GetEnvironmentVariable("DESKNEXT_WINDOWS_VOLUME_DATA")!;
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), Path.GetDirectoryName(root), ignoreCase: true);
        Assert.StartsWith("DeskNext.WinMountProbe-", Path.GetFileName(root));
        FileSystemVolume.RequireNoReparsePoints(root);
        Assert.Equal(Marker, File.ReadAllText(Path.Combine(root, "owner")));
        Assert.True(Path.IsPathFullyQualified(data) && data.Length > 3 && data[1] == ':' && data[2] == '\\');
        Assert.Equal(Path.GetFileName(root), Path.GetFileName(data));
        if (online) Assert.Equal(Marker, File.ReadAllText(Path.Combine(data, "owner")));
        return (root, Path.Combine(root, "mounted"), data);
    }

    private static void CreateSource(string path, bool directory)
    {
        if (directory) Directory.CreateDirectory(Path.Combine(path, "empty"));
        File.WriteAllText(directory ? Path.Combine(path, "content.txt") : path, Marker);
    }

    private static void AssertContent(string path, bool directory)
    {
        Assert.Equal(Marker, File.ReadAllText(directory ? Path.Combine(path, "content.txt") : path));
        if (directory) Assert.True(Directory.Exists(Path.Combine(path, "empty")));
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
}

public sealed class HostedWindowsVolumeFactAttribute : FactAttribute
{
    public HostedWindowsVolumeFactAttribute(string phase)
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
            Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted" ||
            Environment.GetEnvironmentVariable("DESKNEXT_WINDOWS_VOLUME_PHASE") != phase)
            Skip = "Requires the matching phase of the isolated hosted VHDX fixture.";
    }
}
