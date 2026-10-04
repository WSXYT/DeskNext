using System.Text.Json;
using DeskNest.Core.Storage;
using Xunit;

namespace DeskNest.Core.Tests;

public sealed class NativeDirectoryMoveTests : IDisposable
{
    private readonly string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),
        "DeskNext-directory-move-" + Guid.NewGuid().ToString("N"))).FullName;

    [Theory]
    [InlineData("root")]
    [InlineData("empty")]
    [InlineData("legacy")]
    public async Task MatchingFilesCannotAuthorizeAReplacedOrUnidentifiedDirectory(string change)
    {
        string source = Path.Combine(root, "source"), destination = Path.Combine(root, "destination");
        string journal = Path.Combine(root, "recovery.json"), retired = Path.Combine(root, "retired");
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        File.WriteAllText(Path.Combine(source, "item.txt"), "same object and content");
        var transaction = new DesktopOrganizationTransaction(journal);
        var result = await transaction.ExecuteDirectoriesAsync([new(source, destination)], retainJournalUntilCommit: true);
        var receipt = Assert.Single(result.DirectoryReceipts!);
        Assert.Equal(new[] { "", "empty" }, receipt.DirectoryNativeIds!.Keys);
        var originalFile = FileIdentity.Capture(Path.Combine(destination, "item.txt"));

        if (change == "legacy")
        {
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var saved = JsonSerializer.Deserialize<OrganizationRecoveryJournal>(File.ReadAllText(journal), options)!;
            File.WriteAllText(journal, JsonSerializer.Serialize(saved with
            { DirectoryMoves = [receipt with { DirectoryNativeIds = null }] }, options));
        }
        else if (change == "root")
        {
            Directory.Move(destination, retired);
            Directory.CreateDirectory(destination);
            Directory.Move(Path.Combine(retired, "empty"), Path.Combine(destination, "empty"));
            File.Move(Path.Combine(retired, "item.txt"), Path.Combine(destination, "item.txt"));
        }
        else
        {
            Directory.Move(Path.Combine(destination, "empty"), retired);
            Directory.CreateDirectory(Path.Combine(destination, "empty"));
        }
        Assert.Equal(originalFile, FileIdentity.Capture(Path.Combine(destination, "item.txt")));
        await Assert.ThrowsAsync<IOException>(() => new DesktopOrganizationTransaction(journal).RecoverAsync());
        Assert.True(transaction.HasRecoveryJournal);
        Assert.False(Directory.Exists(source));
        Assert.Equal("same object and content", File.ReadAllText(Path.Combine(destination, "item.txt")));
        Assert.True(Directory.Exists(Path.Combine(destination, "empty")));
    }

    [Fact]
    public void NativeDirectorySchemaRequiresTheExactRootAndChildSet()
    {
        const string id = "0000000000000000:00000000000000000000000000000001";
        DesktopOrganizationTransaction.ValidateDirectoryManifest([], ["empty"],
            new Dictionary<string, string> { [""] = id, ["empty"] = id });
        Assert.Throws<InvalidDataException>(() => DesktopOrganizationTransaction.ValidateDirectoryManifest([], ["empty"],
            new Dictionary<string, string> { ["empty"] = id }));
        Assert.Throws<InvalidDataException>(() => DesktopOrganizationTransaction.ValidateDirectoryManifest([], [],
            new Dictionary<string, string> { [""] = "invalid" }));
        Assert.Throws<InvalidDataException>(() => DesktopOrganizationTransaction.ValidateDirectoryManifest([], [],
            new Dictionary<string, string> { [""] = id, ["extra"] = id }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecordedUndoEvidenceCannotBeReplacedDuringPreparation(bool directory)
    {
        string source = Path.Combine(root, "source"), retired = Path.Combine(root, "retired");
        string destination = Path.Combine(root, "not-created", "restored");
        string journal = Path.Combine(root, "recovery.json");
        var transaction = new DesktopOrganizationTransaction(journal);
        Func<Task> execute;
        if (directory)
        {
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "item.txt"), "original content");
            var expected = DesktopOrganizationTransaction.CaptureDirectoryReceipt(source, destination);
            Directory.Move(source, retired);
            Directory.CreateDirectory(source);
            File.Move(Path.Combine(retired, "item.txt"), Path.Combine(source, "item.txt"));
            execute = () => transaction.ExecuteDirectoriesAsync([new(source, destination) { ExpectedReceipt = expected }]);
        }
        else
        {
            File.WriteAllText(source, "original content");
            var expected = FileIdentity.Capture(source);
            File.Move(source, retired);
            File.WriteAllText(source, "original content");
            File.SetLastWriteTimeUtc(source, new DateTime(expected.LastWriteTimeUtcTicks, DateTimeKind.Utc));
            execute = () => transaction.ExecuteAsync([new(source, destination) { ExpectedIdentity = expected }]);
        }
        var error = await Assert.ThrowsAsync<IOException>(execute);
        Assert.Contains("changed after selecting the recorded operation", error.Message);
        Assert.False(transaction.HasRecoveryJournal);
        Assert.False(Directory.Exists(Path.GetDirectoryName(destination)));
        Assert.Equal("original content", File.ReadAllText(directory ? Path.Combine(source, "item.txt") : source));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryDoesNotRecreateAMissingOriginalParent(bool directory)
    {
        string parent = Directory.CreateDirectory(Path.Combine(root, "original-parent")).FullName;
        string source = Path.Combine(parent, "item"), destination = Path.Combine(root, "moved");
        if (directory) Directory.CreateDirectory(Path.Combine(source, "empty"));
        File.WriteAllText(directory ? Path.Combine(source, "item.txt") : source, "preserved");
        var transaction = new DesktopOrganizationTransaction(Path.Combine(root, "recovery.json"));
        if (directory) await transaction.ExecuteDirectoriesAsync([new(source, destination)], retainJournalUntilCommit: true);
        else await transaction.ExecuteAsync([new(source, destination)], retainJournalUntilCommit: true);
        Directory.Delete(parent);
        await Assert.ThrowsAsync<IOException>(() => transaction.RecoverAsync());
        Assert.False(Directory.Exists(parent));
        Assert.True(transaction.HasRecoveryJournal);
        Assert.Equal("preserved", File.ReadAllText(directory ? Path.Combine(destination, "item.txt") : destination));
        if (directory) Assert.True(Directory.Exists(Path.Combine(destination, "empty")));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
