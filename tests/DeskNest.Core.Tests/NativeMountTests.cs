using System.Security.Cryptography;
using System.Text.Json;
using DeskNest.Core.Storage;
using Xunit;

namespace DeskNest.Core.Tests;

// The shell fixture owns every mount. Ordinary/local test runs explicitly skip these checks.
public sealed class NativeMountTests
{
    [HostedUnixMountsFact]
    public async Task RealVolumesAndMountAliasesRefuseBeforeAnyMove()
    {
        string root = Fixture();
        string volume = Path.Combine(root, "volume");
        Assert.NotEqual(FileSystemVolume.Identify(root), FileSystemVolume.Identify(volume));
        Assert.NotEqual(UnixFileIdentity.CaptureDirectory(root).NativeId[..22],
            UnixFileIdentity.CaptureDirectory(volume).NativeId[..22]);
        await RefuseBatch(root, volume, "volume");
        if (OperatingSystem.IsLinux())
        {
            string bind = Path.Combine(root, "bind");
            // Same underlying directory/device, different mount: not permission to rename across it.
            Assert.Equal(UnixFileIdentity.CaptureDirectory(Path.Combine(root, "source")).NativeId,
                UnixFileIdentity.CaptureDirectory(bind).NativeId);
            Assert.NotEqual(FileSystemVolume.Identify(Path.Combine(root, "source")), FileSystemVolume.Identify(bind));
            await RefuseBatch(root, bind, "bind");
            Assert.Throws<IOException>(() => FileSystemVolume.Identify(Path.Combine(root, "stack", "missing")));
        }
    }

    [HostedUnixMountsFact]
    public async Task AFullJournalVolumeRetainsUnreceiptedFileAndDirectoryRenames()
    {
        string root = Fixture(), volume = Path.Combine(root, "volume");
        // Never fill the host filesystem, including a bind alias of it.
        Assert.NotEqual(UnixFileIdentity.CaptureDirectory(root).NativeId[..22],
            UnixFileIdentity.CaptureDirectory(volume).NativeId[..22]);
        foreach (bool directory in new[] { false, true })
        {
            string name = directory ? "full-tree" : "full-file";
            string source = Path.Combine(root, name), destination = Path.Combine(root, name + "-moved");
            CreateSource(source, directory);
            string journal = Path.Combine(volume, name, "recovery.json");
            string filler = Path.Combine(volume, name + "-filler");
            byte[]? prepared = null;
            FileStream? filledVolume = null;
            bool diskFull = false;
            bool ExhaustJournalVolume()
            {
                prepared = File.ReadAllBytes(journal);
                filledVolume = FillUntilNoSpace(filler);
                diskFull = true;
                return true;
            }
            var transaction = new DesktopOrganizationTransaction(journal,
                moveGuard: _ => ExhaustJournalVolume(), directoryMoveGuard: _ => ExhaustJournalVolume());
            try
            {
                await Assert.ThrowsAsync<IOException>(() => directory
                    ? transaction.ExecuteDirectoriesAsync([new(source, destination)], retainJournalUntilCommit: true)
                    : transaction.ExecuteAsync([new(source, destination)], retainJournalUntilCommit: true));
                Assert.True(diskFull);
                Assert.NotNull(prepared);
                Assert.Equal(prepared, File.ReadAllBytes(journal));
                var retained = JsonSerializer.Deserialize<OrganizationRecoveryJournal>(prepared,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                Assert.Equal("Prepared", retained.Status);
                Assert.False(directory ? retained.DirectoryMoves!.Single().Completed : retained.Moves.Single().Completed);
                Assert.False(Exists(source));
                AssertContent(destination, directory);
            }
            finally
            {
                filledVolume?.Dispose();
                if (File.Exists(filler)) File.Delete(filler);
            }
            // Removing the real ENOSPC condition is not authorization to infer a missing receipt.
            await Assert.ThrowsAsync<IOException>(() => new DesktopOrganizationTransaction(journal).RecoverAsync());
            Assert.True(transaction.HasRecoveryJournal);
            Assert.Equal(prepared, File.ReadAllBytes(journal));
            AssertContent(destination, directory);
        }
    }

    private static async Task RefuseBatch(string root, string other, string label)
    {
        foreach (bool directory in new[] { false, true })
        {
            string prefix = label + (directory ? "-tree" : "-file");
            string first = Path.Combine(root, prefix + "-first"), second = Path.Combine(root, prefix + "-second");
            string localTarget = Path.Combine(root, prefix + "-moved");
            string newParent = Path.Combine(other, prefix + "-not-created");
            CreateSource(first, directory);
            CreateSource(second, directory);
            var transaction = new DesktopOrganizationTransaction(Path.Combine(root, prefix + "-journal.json"));
            await Assert.ThrowsAsync<NotSupportedException>(() => directory
                ? transaction.ExecuteDirectoriesAsync([new(first, localTarget), new(second, Path.Combine(newParent, "item"))])
                : transaction.ExecuteAsync([new(first, localTarget), new(second, Path.Combine(newParent, "item"))]));
            AssertContent(first, directory);
            AssertContent(second, directory);
            Assert.False(Exists(localTarget));
            Assert.False(Directory.Exists(newParent));
            Assert.False(transaction.HasRecoveryJournal);
        }
    }

    private static FileStream FillUntilNoSpace(string path)
    {
        byte[] block = RandomNumberGenerator.GetBytes(4096);
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            1, FileOptions.WriteThrough);
        try
        {
            // Exhaust filesystem blocks, not only large allocation requests. Keep the
            // descriptor open so closing it cannot release reserved extents before rename.
            for (int count = 0; count < 256 * 1024 * 1024 / block.Length; count++)
                stream.Write(block);
        }
        catch (IOException error) when ((error.HResult & 0xFFFF) is 28 or 112)
        {
            return stream; // Unix ENOSPC or mapped ERROR_DISK_FULL only.
        }
        catch { stream.Dispose(); throw; }
        stream.Dispose();
        throw new IOException("The bounded fixture did not produce ENOSPC; refusing to write more data.");
    }

    private static string Fixture()
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
            Environment.GetEnvironmentVariable("DESKNEXT_MOUNT_PROBE_ROOT")!));
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), Path.GetDirectoryName(root));
        Assert.StartsWith("DeskNext.MountProbe-", Path.GetFileName(root));
        Assert.Equal("DeskNext native storage fixture\n", File.ReadAllText(Path.Combine(root, "owner")));
        FileSystemVolume.RequireNoReparsePoints(root);
        return root;
    }

    private static void CreateSource(string path, bool directory)
    {
        if (directory) Directory.CreateDirectory(Path.Combine(path, "empty"));
        File.WriteAllText(directory ? Path.Combine(path, "item.txt") : path, "preserved mount fixture");
    }

    private static void AssertContent(string path, bool directory)
    {
        Assert.Equal("preserved mount fixture", File.ReadAllText(directory ? Path.Combine(path, "item.txt") : path));
        if (directory) Assert.True(Directory.Exists(Path.Combine(path, "empty")));
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
}

public sealed class HostedUnixMountsFactAttribute : FactAttribute
{
    public HostedUnixMountsFactAttribute()
    {
        if ((!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) ||
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
            Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted" ||
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DESKNEXT_MOUNT_PROBE_ROOT")))
            Skip = "Requires build/Test-UnixStorage.sh on a disposable hosted Linux/macOS runner.";
    }
}
