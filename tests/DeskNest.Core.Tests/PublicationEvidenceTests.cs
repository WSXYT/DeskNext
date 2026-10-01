using System.Text.Json;
using DeskNest.Core.Storage;
using DeskNest.Core.Workspace;
using Xunit;

namespace DeskNest.Core.Tests;

public sealed class PublicationEvidenceTests : IDisposable
{
    private readonly string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),
        "DeskNext-publication-" + Guid.NewGuid().ToString("N"))).FullName;

    [WindowsHandleTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnrollmentPersistsReceiptAndBackupReopensTheSameObjects(bool directory)
    {
        var (sourceSpace, targetSpace, file) = Fixture(directory);
        Guid newId;
        string expectedJson;
        await using (var store = await WorkspaceStore.OpenAsync(root))
        {
            await store.UpdateAsync(s => s with { Spaces = [sourceSpace, targetSpace], Files = [file] });
            bool guardRan = false;
            var coordinator = new ManualOrganizationCoordinator(store,
                new DesktopOrganizationTransaction(Path.Combine(root, "move.json")), destination =>
                {
                    guardRan = true;
                    if (directory)
                    {
                        Assert.Throws<IOException>(() => Directory.Move(destination, destination + "-replaced"));
                        Assert.Throws<IOException>(() => File.Delete(Path.Combine(destination, "item.txt")));
                    }
                    else Assert.Throws<IOException>(() => File.Delete(destination));
                    return true;
                });
            var result = await coordinator.CopyFileAsync(file.Id, targetSpace.Id);
            Assert.True(guardRan);
            newId = result.FileId;
            var copied = store.Snapshot.Files.Single(f => f.Id == newId);
            Assert.NotNull(copied.Publication);
            PublicationEvidenceValidation.Validate(copied.Publication!, directory);
            WindowsPublicationVerifier.Verify(copied.Path, copied.Publication!);
            expectedJson = JsonSerializer.Serialize(copied.Publication);
            Assert.Empty(store.Snapshot.Operations); // Copy never masquerades as an undoable move.
        }
        await File.WriteAllTextAsync(Path.Combine(root, "workspace.json"), "broken primary");
        await using var reopened = await WorkspaceStore.OpenAsync(root);
        var restored = reopened.Snapshot.Files.Single(f => f.Id == newId);
        Assert.Equal(expectedJson, JsonSerializer.Serialize(restored.Publication));
        WindowsPublicationVerifier.Verify(restored.Path, restored.Publication!);
        Assert.True(directory ? Directory.Exists(file.Path) : File.Exists(file.Path));
    }

    [WindowsHandleTheory]
    [InlineData("root")]
    [InlineData("empty")]
    [InlineData("file")]
    public async Task SameContentReplacementDuringDirectoryPublicationCannotBeAdopted(string kind)
    {
        var (_, target, file) = Fixture(true);
        string destination = Path.Combine(target.Folder, file.Name);
        string retiredArea = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),
            "DeskNext-retired-" + Guid.NewGuid().ToString("N"))).FullName;
        string retainedOriginal = Path.Combine(retiredArea, "original");
        bool replaced = false;
        var error = await Assert.ThrowsAsync<IOException>(() => WindowsDirectoryCopyLease.CreateAsync(file.Path,
            destination, afterPublishBeforeReopen: path =>
            {
                if (kind == "root")
                {
                    Directory.Move(path, retainedOriginal);
                    Directory.CreateDirectory(Path.Combine(path, "empty"));
                    File.Copy(Path.Combine(retainedOriginal, "item.txt"), Path.Combine(path, "item.txt"));
                }
                else if (kind == "empty")
                {
                    Directory.Move(Path.Combine(path, "empty"), retainedOriginal);
                    Directory.CreateDirectory(Path.Combine(path, "empty"));
                }
                else
                {
                    string original = Path.Combine(path, "item.txt");
                    var timestamp = File.GetLastWriteTimeUtc(original);
                    File.Move(original, retainedOriginal);
                    File.Copy(retainedOriginal, original);
                    File.SetLastWriteTimeUtc(original, timestamp);
                }
                replaced = true;
            }));
        Assert.True(replaced, error.ToString());
        Assert.Contains(destination, error.Message);
        Assert.Contains("identities", error.InnerException!.Message);
        Assert.Equal("unchanged", File.ReadAllText(Path.Combine(destination, "item.txt")));
        Assert.True(File.Exists(retainedOriginal) || Directory.Exists(retainedOriginal));
        Assert.Equal("unchanged", File.ReadAllText(Path.Combine(file.Path, "item.txt")));
        Directory.Delete(retiredArea, recursive: true);
    }

    [WindowsHandleTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartVerifierRejectsChangedBytesOnTheSameNativeObject(bool directory)
    {
        var (source, target, file) = Fixture(directory);
        await using var store = await WorkspaceStore.OpenAsync(root);
        await store.UpdateAsync(s => s with { Spaces = [source, target], Files = [file] });
        var result = await new ManualOrganizationCoordinator(store,
            new DesktopOrganizationTransaction(Path.Combine(root, "move.json"))).CopyFileAsync(file.Id, target.Id);
        var copy = store.Snapshot.Files.Single(f => f.Id == result.FileId);
        string content = directory ? Path.Combine(copy.Path, "item.txt") : copy.Path;
        var before = FileIdentity.Capture(content);
        File.WriteAllText(content, "different"); // Same length; no new object.
        File.SetLastWriteTimeUtc(content, new DateTime(before.LastWriteTimeUtcTicks, DateTimeKind.Utc));
        var after = FileIdentity.Capture(content);
        Assert.Equal(before.NativeId, after.NativeId);
        Assert.Equal(before.Length, after.Length);
        Assert.Equal(before.LastWriteTimeUtcTicks, after.LastWriteTimeUtcTicks);
        Assert.NotEqual(before.Sha256, after.Sha256);
        Assert.Throws<IOException>(() => WindowsPublicationVerifier.Verify(copy.Path, copy.Publication!));
        Assert.Equal("different", File.ReadAllText(content));
    }

    [WindowsHandleFact]
    public async Task NewDirectoryEntryAtMetadataBoundaryPreventsEnrollment()
    {
        var (source, target, file) = Fixture(true);
        await using var store = await WorkspaceStore.OpenAsync(root);
        await store.UpdateAsync(s => s with { Spaces = [source, target], Files = [file] });
        var coordinator = new ManualOrganizationCoordinator(store,
            new DesktopOrganizationTransaction(Path.Combine(root, "move.json")), path =>
            {
                File.WriteAllText(Path.Combine(path, "external.txt"), "external data");
                return true;
            });
        await Assert.ThrowsAsync<IOException>(() => coordinator.CopyFileAsync(file.Id, target.Id));
        Assert.Single(store.Snapshot.Files);
        string destination = Path.Combine(target.Folder, file.Name);
        Assert.Equal("external data", File.ReadAllText(Path.Combine(destination, "external.txt")));
        Assert.Equal("unchanged", File.ReadAllText(Path.Combine(destination, "item.txt")));
    }

    [WindowsHandleFact]
    public async Task DescendantDirectoryCopyRefusesBeforeCreatingParents()
    {
        var (_, _, file) = Fixture(true);
        string parent = Path.Combine(file.Path, "not-created");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            WindowsDirectoryCopyLease.CreateAsync(file.Path, Path.Combine(parent, "copy")));
        Assert.False(Directory.Exists(parent));
    }

    [WindowsHandleFact]
    public async Task ReplacingAncestorWhileKeepingParentAndFileIdsStillRefuses()
    {
        var (source, target, file) = Fixture(false);
        await using var store = await WorkspaceStore.OpenAsync(root);
        await store.UpdateAsync(s => s with { Spaces = [source, target], Files = [file] });
        var result = await new ManualOrganizationCoordinator(store,
            new DesktopOrganizationTransaction(Path.Combine(root, "move.json"))).CopyFileAsync(file.Id, target.Id);
        var copied = store.Snapshot.Files.Single(f => f.Id == result.FileId);
        string ancestor = Path.GetDirectoryName(target.Folder)!;
        string retired = ancestor + "-retired";
        Directory.Move(ancestor, retired);
        Directory.CreateDirectory(ancestor);
        Directory.Move(Path.Combine(retired, "target"), target.Folder);
        Assert.Equal(copied.Publication!.NativeId, FileIdentity.Capture(copied.Path).NativeId);
        Assert.Throws<IOException>(() => WindowsPublicationVerifier.Verify(copied.Path, copied.Publication));
    }

    [WindowsHandleTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartVerifierRejectsSameContentReplacement(bool directory)
    {
        var (source, target, file) = Fixture(directory);
        await using var store = await WorkspaceStore.OpenAsync(root);
        await store.UpdateAsync(s => s with { Spaces = [source, target], Files = [file] });
        var result = await new ManualOrganizationCoordinator(store,
            new DesktopOrganizationTransaction(Path.Combine(root, "move.json"))).CopyFileAsync(file.Id, target.Id);
        var copy = store.Snapshot.Files.Single(f => f.Id == result.FileId);
        string content = directory ? Path.Combine(copy.Path, "item.txt") : copy.Path;
        var before = FileIdentity.Capture(content);
        File.Move(content, content + ".original");
        File.Copy(content + ".original", content);
        File.SetLastWriteTimeUtc(content, new DateTime(before.LastWriteTimeUtcTicks, DateTimeKind.Utc));
        // Move the retained original outside the directory so topology/count still match.
        File.Move(content + ".original", Path.Combine(root, "retained-content"));
        var after = FileIdentity.Capture(content);
        Assert.NotEqual(before.NativeId, after.NativeId);
        Assert.Equal(before.Length, after.Length);
        Assert.Equal(before.LastWriteTimeUtcTicks, after.LastWriteTimeUtcTicks);
        Assert.Equal(before.Sha256, after.Sha256);
        Assert.Throws<IOException>(() => WindowsPublicationVerifier.Verify(copy.Path, copy.Publication!));
    }

    [WindowsHandleFact]
    public async Task ConcurrentMetadataUpdateCannotEnrollAStaleCopy()
    {
        var (source, target, file) = Fixture(false);
        await using var store = await WorkspaceStore.OpenAsync(root);
        await store.UpdateAsync(s => s with { Spaces = [source, target], Files = [file] });
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var guardReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? competing = null;
        var coordinator = new ManualOrganizationCoordinator(store,
            new DesktopOrganizationTransaction(Path.Combine(root, "move.json")), _ =>
            {
                competing = Task.Run(() => store.UpdateAsync(state =>
                {
                    entered.Set();
                    if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Metadata fixture timed out.");
                    return state;
                }));
                if (!entered.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Metadata gate was not acquired.");
                guardReturned.SetResult();
                return true;
            });
        var copying = coordinator.CopyFileAsync(file.Id, target.Id);
        try
        {
            await guardReturned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(copying.IsCompleted);
        }
        finally { release.Set(); }
        await competing!;
        var error = await Assert.ThrowsAsync<IOException>(() => copying);
        Assert.IsType<InvalidOperationException>(error.InnerException);
        Assert.Single(store.Snapshot.Files);
        Assert.Equal("unchanged", File.ReadAllText(Path.Combine(target.Folder, file.Name)));
    }

    [Fact]
    public void DirectorySchemaAllowsRootOnlyAndExactDepthButRejectsOverflow()
    {
        string id = "1122334455667788:" + new string('1', 32);
        var evidence = new WorkspacePublicationEvidence(id,
            @"\\?\Volume{11111111-2222-3333-4444-555555555555}\", 0, 0, "", id,
            [new("", id, true, 0, 0, "")]) { AncestorNativeIds = [id] };
        PublicationEvidenceValidation.Validate(evidence, true);
        string path = "";
        for (int depth = 1; depth <= DesktopOrganizationTransaction.MaximumDirectoryDepth; depth++)
        {
            path = path.Length == 0 ? "a" : path + "/a";
            evidence.DirectoryNodes!.Add(new(path, id, true, 0, 0, ""));
        }
        PublicationEvidenceValidation.Validate(evidence, true);
        evidence.DirectoryNodes!.Add(new(path + "/a", id, true, 0, 0, ""));
        Assert.Throws<InvalidDataException>(() => PublicationEvidenceValidation.Validate(evidence, true));
        var overCount = evidence with { DirectoryNodes = Enumerable.Repeat(evidence.DirectoryNodes[0],
            DesktopOrganizationTransaction.MaximumDirectoryEntries + 2).ToList() };
        Assert.Throws<InvalidDataException>(() => PublicationEvidenceValidation.Validate(overCount, true));
    }

    [Theory]
    [InlineData("native-null")]
    [InlineData("parent-null")]
    [InlineData("volume-null")]
    [InlineData("volume-relative")]
    [InlineData("hash-null")]
    [InlineData("hash-short")]
    [InlineData("hash-nonhex")]
    [InlineData("ancestors-null")]
    [InlineData("ancestors-empty")]
    [InlineData("ancestors-duplicate")]
    [InlineData("ancestors-wrong-volume")]
    [InlineData("nodes-null-entry")]
    [InlineData("nodes-no-root")]
    [InlineData("nodes-traversal")]
    [InlineData("nodes-duplicate")]
    [InlineData("nodes-no-parent")]
    public async Task MalformedPublicationCannotChangeWorkspaceRevision(string kind)
    {
        string id = "1122334455667788:" + new string('1', 32);
        string volume = @"\\?\Volume{11111111-2222-3333-4444-555555555555}\";
        var evidence = new WorkspacePublicationEvidence(id, volume, 9, 0, new string('A', 64), id, null)
        { AncestorNativeIds = [id] };
        bool directory = kind.StartsWith("nodes-", StringComparison.Ordinal);
        if (directory)
        {
            evidence = evidence with { Length = 0, Sha256 = "", DirectoryNodes = [new("", id, true, 0, 0, "")] };
        }
        evidence = kind switch
        {
            "native-null" => evidence with { NativeId = null! },
            "parent-null" => evidence with { ParentNativeId = null },
            "volume-null" => evidence with { VolumePath = null! },
            "volume-relative" => evidence with { VolumePath = "relative" },
            "hash-null" => evidence with { Sha256 = null! },
            "hash-short" => evidence with { Sha256 = "A" },
            "hash-nonhex" => evidence with { Sha256 = new('Z', 64) },
            "ancestors-null" => evidence with { AncestorNativeIds = null! },
            "ancestors-empty" => evidence with { AncestorNativeIds = [] },
            "ancestors-duplicate" => evidence with { AncestorNativeIds = [id, id] },
            "ancestors-wrong-volume" => evidence with { AncestorNativeIds = ["0000000000000000:" + new string('1', 32)] },
            "nodes-null-entry" => evidence with { DirectoryNodes = [null!] },
            "nodes-no-root" => evidence with { DirectoryNodes = [] },
            "nodes-traversal" => evidence with { DirectoryNodes = [.. evidence.DirectoryNodes!, new("../escape", id, true, 0, 0, "")] },
            "nodes-duplicate" => evidence with { DirectoryNodes = [.. evidence.DirectoryNodes!, evidence.DirectoryNodes![0]] },
            "nodes-no-parent" => evidence with { DirectoryNodes = [.. evidence.DirectoryNodes!, new("missing/child", id, true, 0, 0, "")] },
            _ => throw new ArgumentException(kind)
        };
        var (source, target, file) = Fixture(directory);
        await using var store = await WorkspaceStore.OpenAsync(root);
        await store.UpdateAsync(s => s with { Spaces = [source, target], Files = [file] });
        var revision = store.Snapshot.Revision;
        await Assert.ThrowsAsync<InvalidDataException>(() => store.UpdateAsync(s => s with
        {
            Files = [file with { Publication = evidence }]
        }));
        Assert.Equal(revision, store.Snapshot.Revision);
        Assert.Null(store.Snapshot.Files.Single().Publication);
    }

    private (WorkspaceSpace Source, WorkspaceSpace Target, WorkspaceFile File) Fixture(bool directory)
    {
        string left = Directory.CreateDirectory(Path.Combine(root, "source-space")).FullName;
        string right = Directory.CreateDirectory(Path.Combine(root, "destination-ancestor", "target")).FullName;
        var source = new WorkspaceSpace(Guid.NewGuid(), "Source", "", SpaceStorageMode.Managed, left);
        var target = new WorkspaceSpace(Guid.NewGuid(), "Target", "", SpaceStorageMode.Managed, right);
        string name = directory ? "tree" : "item.txt";
        string path = Path.Combine(left, name);
        if (directory)
        {
            Directory.CreateDirectory(Path.Combine(path, "empty"));
            File.WriteAllText(Path.Combine(path, "item.txt"), "unchanged");
        }
        else File.WriteAllText(path, "unchanged");
        return (source, target, new WorkspaceFile(Guid.NewGuid(), source.Id, name, path, directory));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
