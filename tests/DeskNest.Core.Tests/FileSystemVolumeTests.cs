using DeskNest.Core.Storage;
using Xunit;

namespace DeskNest.Core.Tests;

public sealed class FileSystemVolumeTests
{
    private static readonly string[] Mounts =
    [
        "1 0 8:1 / / rw - ext4 /dev/sda1 rw",
        "2 1 8:2 / /data rw - ext4 /dev/sdb1 rw",
        "3 1 8:1 /project /bind rw - ext4 /dev/sda1 rw",
        @"4 1 8:3 / /media/My\040Disk rw - xfs /dev/sdc1 rw",
        "5 1 0:42 / /network rw - nfs host:/share rw"
    ];

    [Theory]
    [InlineData("/file", "linux-mount:1:8:1")]
    [InlineData("/data", "linux-mount:2:8:2")]
    [InlineData("/data/new/item", "linux-mount:2:8:2")]
    [InlineData("/database/item", "linux-mount:1:8:1")]
    [InlineData("/bind/item", "linux-mount:3:8:1")]
    [InlineData("/media/My Disk/item", "linux-mount:4:8:3")]
    public void LinuxUsesLongestComponentBoundedMount(string path, string expected) =>
        Assert.Equal(expected, FileSystemVolume.IdentifyLinuxMount(path, Mounts));

    [Fact]
    public void LinuxRejectsUnknownNetworkMalformedOrAmbiguousMounts()
    {
        Assert.Throws<IOException>(() => FileSystemVolume.IdentifyLinuxMount("/network/item", Mounts));
        Assert.Throws<IOException>(() => FileSystemVolume.IdentifyLinuxMount("/file", []));
        Assert.Throws<IOException>(() => FileSystemVolume.IdentifyLinuxMount("/file", ["invalid"]));
        Assert.Throws<IOException>(() => FileSystemVolume.IdentifyLinuxMount("/data/item",
            [.. Mounts, "6 1 8:4 / /data rw - ext4 /dev/sdd1 rw"]));
    }

    [Fact]
    public void CoveredNestedMountIsRejectedInEveryRecordOrder()
    {
        string[] records =
        [
            "1 0 8:1 / / rw - ext4 /dev/sda1 rw",
            "2 1 8:2 / /data rw - ext4 /dev/sdb1 rw",
            "3 2 8:3 / /data/nested rw - ext4 /dev/sdc1 rw",
            "4 2 0:42 / /data rw - nfs host:/share rw"
        ];
        int checkedOrders = 0;
        foreach (int a in Enumerable.Range(0, 4))
        foreach (int b in Enumerable.Range(0, 4).Where(i => i != a))
        foreach (int c in Enumerable.Range(0, 4).Where(i => i != a && i != b))
        {
            int d = 6 - a - b - c;
            Assert.Throws<IOException>(() => FileSystemVolume.IdentifyLinuxMount("/data/nested/file",
                [records[a], records[b], records[c], records[d]]));
            checkedOrders++;
        }
        Assert.Equal(24, checkedOrders);
    }

    [Fact]
    public void UnixMountSelectionDoesNotConfuseRootOrSiblingPrefixes()
    {
        string[] mounts = ["/", "/Volumes/Data", "/Volumes/Data/Nested"];
        Assert.Equal("/", FileSystemVolume.FindMount("/Users/person", mounts));
        Assert.Equal("/", FileSystemVolume.FindMount("/Volumes/Database/file", mounts));
        Assert.Equal("/Volumes/Data/Nested", FileSystemVolume.FindMount("/Volumes/Data/Nested/file", mounts));
    }

    [Fact]
    public void NativeLookupUsesExistingAncestorForNewDestination()
    {
        string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "DeskNest-volume-" + Guid.NewGuid())).FullName;
        try
        {
            string source = Path.Combine(root, "source.txt");
            File.WriteAllText(source, "volume test");
            string destination = Path.Combine(root, "missing", "nested", "item.txt");
            Assert.NotEmpty(FileSystemVolume.Identify(source));
            Assert.Equal(FileSystemVolume.Identify(source), FileSystemVolume.Identify(destination));
            FileSystemVolume.RequireSameVolume(source, destination);
            Assert.False(Directory.Exists(Path.Combine(root, "missing")));
        }
        finally { Directory.Delete(root, true); }
    }
}
