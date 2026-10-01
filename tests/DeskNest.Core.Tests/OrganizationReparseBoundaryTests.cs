using System.Diagnostics;
using DeskNest.Core.Storage;
using Xunit;

namespace DeskNest.Core.Tests;

public sealed class OrganizationReparseBoundaryTests : IDisposable
{
    private readonly string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),
        "DeskNest-links-" + Guid.NewGuid().ToString("N"))).FullName;
    private string? link;

    [Theory]
    [InlineData(false, "prepare")]
    [InlineData(true, "prepare")]
    [InlineData(false, "forward")]
    [InlineData(true, "forward")]
    [InlineData(false, "restore-source")]
    [InlineData(true, "restore-source")]
    [InlineData(false, "restore-destination")]
    [InlineData(true, "restore-destination")]
    public async Task LinkedAncestorsCannotAuthorizeMovesOrRecovery(bool directory, string phase)
    {
        string sourceParent = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
        string targetParent = Path.Combine(root, "target");
        string outside = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
        string sentinel = Path.Combine(outside, "sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "untouched");
        string source = Path.Combine(sourceParent, "item");
        string destination = Path.Combine(targetParent, "item");
        if (directory) Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(directory ? Path.Combine(source, "content.txt") : source, "keep me");
        string journal = Path.Combine(root, "operation.json");
        if (phase == "prepare") CreateLink(targetParent, outside);
        bool BeforeMove()
        {
            if (phase == "forward") CreateLink(targetParent, outside);
            return true;
        }
        var transaction = new DesktopOrganizationTransaction(journal,
            moveGuard: _ => BeforeMove(), directoryMoveGuard: _ => BeforeMove());
        Task<OrganizationTransactionResult> Move() => directory
            ? transaction.ExecuteDirectoriesAsync([new(source, destination)], retainJournalUntilCommit: true)
            : transaction.ExecuteAsync([new(source, destination)], retainJournalUntilCommit: true);

        if (phase is "prepare" or "forward")
        {
            await Assert.ThrowsAsync<IOException>(Move);
            Assert.Equal("keep me", await File.ReadAllTextAsync(directory ? Path.Combine(source, "content.txt") : source));
            Assert.False(File.Exists(Path.Combine(outside, "item")) || Directory.Exists(Path.Combine(outside, "item")));
            Assert.Equal(phase == "forward", transaction.HasRecoveryJournal);
        }
        else
        {
            await Move();
            if (phase == "restore-source")
            {
                Directory.Delete(sourceParent);
                CreateLink(sourceParent, outside);
            }
            else
            {
                string holding = Path.Combine(root, "holding");
                Directory.Move(targetParent, holding);
                CreateLink(targetParent, holding);
            }
            // A fresh instance must refuse even though destination hashes/native IDs still match.
            var restarted = new DesktopOrganizationTransaction(journal);
            await Assert.ThrowsAsync<IOException>(() => restarted.RecoverAsync());
            Assert.True(restarted.HasRecoveryJournal);
            Assert.False(File.Exists(source) || Directory.Exists(source));
            Assert.Equal("keep me", await File.ReadAllTextAsync(directory ? Path.Combine(destination, "content.txt") : destination));
        }
        Assert.Equal("untouched", await File.ReadAllTextAsync(sentinel));
    }

    private void CreateLink(string path, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            using var process = Process.Start(new ProcessStartInfo(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
                $"/d /c mklink /J \"{path}\" \"{target}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
            if (!process.WaitForExit(10_000))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("Junction fixture timed out.");
            }
            Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
        }
        else Directory.CreateSymbolicLink(path, target);
        link = path;
    }

    public void Dispose()
    {
        if (link is not null) Directory.Delete(link);
        Directory.Delete(root, recursive: true);
    }
}
