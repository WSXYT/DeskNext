using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeskNest.Core.Workspace;
using Xunit;

namespace DeskNest.Core.Tests;

public sealed class ModelAssetRemovalTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "DeskNext-model-removal-" + Guid.NewGuid().ToString("N"))).FullName;

    [WindowsHandleFact]
    public void RemovalDeletesOnlyPinnedAssetsAndKeepsUnknownFilesAndManifest()
    {
        var (state, hash, directory) = Fixture();
        var plan = ModelAssetRemoval.Prepare(state, hash);
        Assert.Equal(2, plan.Files.Count);
        Assert.True(File.Exists(Path.Combine(directory, "a.bin")));
        Assert.Equal(2, ModelAssetRemoval.Remove(state, plan, hash));
        Assert.False(File.Exists(Path.Combine(directory, "a.bin")));
        Assert.False(File.Exists(Path.Combine(directory, "b.bin")));
        Assert.True(File.Exists(Path.Combine(directory, "manifest.json")));
        Assert.Equal("keep notes", File.ReadAllText(Path.Combine(directory, "notes.txt")));
        Assert.Equal("keep nested", File.ReadAllText(Path.Combine(directory, "unknown", "item.txt")));
        Assert.Equal("user content", File.ReadAllText(Path.Combine(state.Settings.ManagedRoot, "user.txt")));
        Assert.Equal(0, ModelAssetRemoval.Remove(state, plan, hash));
    }

    [WindowsHandleFact]
    public void RemovingTheModelLinkKeepsACatalogedHardLinkReadable()
    {
        var (state, hash, directory) = Fixture();
        string linked = Path.Combine(state.Settings.ManagedRoot, "retained-copy.bin");
        Assert.True(CreateHardLink(linked, Path.Combine(directory, "a.bin"), IntPtr.Zero));
        var space = new WorkspaceSpace(Guid.NewGuid(), "User files", "", SpaceStorageMode.Managed, state.Settings.ManagedRoot);
        state = state with { Spaces = [space], Files = [new WorkspaceFile(Guid.NewGuid(), space.Id, "retained-copy.bin", linked, false)] };
        Assert.Equal(2, ModelAssetRemoval.Remove(state, ModelAssetRemoval.Prepare(state, hash), hash));
        Assert.Equal("first model asset", File.ReadAllText(linked));
        Assert.False(File.Exists(Path.Combine(directory, "a.bin")));
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string newName, string existingName, IntPtr attributes);

    [WindowsHandleTheory]
    [InlineData("changed")]
    [InlineData("revision")]
    [InlineData("manifest")]
    [InlineData("locked")]
    public void RefusalsPrecedeAllDeletion(string kind)
    {
        var (state, hash, directory) = Fixture();
        var plan = ModelAssetRemoval.Prepare(state, hash);
        FileStream? held = null;
        try
        {
            if (kind == "changed") File.WriteAllText(Path.Combine(directory, "b.bin"), "changed model or user data");
            if (kind == "revision") state = state with { Revision = state.Revision + 1 };
            if (kind == "manifest") hash = new string('0', 64);
            if (kind == "locked") held = File.Open(Path.Combine(directory, "b.bin"), FileMode.Open, FileAccess.Read, FileShare.Read);
            if (kind == "locked") Assert.Throws<IOException>(() => ModelAssetRemoval.Remove(state, plan, hash));
            else Assert.Throws<InvalidDataException>(() => ModelAssetRemoval.Remove(state, plan, hash));
            Assert.Equal("first model asset", File.ReadAllText(Path.Combine(directory, "a.bin")));
            Assert.True(File.Exists(Path.Combine(directory, "b.bin")));
        }
        finally { held?.Dispose(); }
    }

    [WindowsHandleFact]
    public void ReplacedRootAndCatalogOverlapCannotAuthorizeRemoval()
    {
        var (state, hash, directory) = Fixture();
        var plan = ModelAssetRemoval.Prepare(state, hash);
        string retired = directory + "-retired";
        Directory.Move(directory, retired);
        Directory.CreateDirectory(directory);
        foreach (string file in new[] { "manifest.json", "a.bin", "b.bin" }) File.Copy(Path.Combine(retired, file), Path.Combine(directory, file));
        Assert.Throws<InvalidDataException>(() => ModelAssetRemoval.Remove(state, plan, hash));
        var overlap = state with { Spaces = [new WorkspaceSpace(Guid.NewGuid(), "Mapped model", "", SpaceStorageMode.Mapped, directory)] };
        Assert.Throws<InvalidDataException>(() => ModelAssetRemoval.Prepare(overlap, hash));
        Assert.True(File.Exists(Path.Combine(directory, "a.bin")));
        Assert.True(File.Exists(Path.Combine(retired, "a.bin")));
    }

    [WindowsHandleFact]
    public async Task CoordinatorBlocksRecoveryAndCheckpointsTheDisabledModel()
    {
        var (state, hash, directory) = Fixture();
        await using var store = await WorkspaceStore.OpenAsync(Path.Combine(_root, "workspace"));
        await store.UpdateAsync(s => s with { Settings = state.Settings });
        var plan = ModelAssetRemoval.Prepare(store.Snapshot, hash);
        var coordinator = new ManualOrganizationCoordinator(store, new DeskNest.Core.Storage.DesktopOrganizationTransaction(
            Path.Combine(store.DataDirectory, "organization-recovery.json")));
        string retained = Path.Combine(store.DataDirectory, "copy-recovery.json");
        await File.WriteAllTextAsync(retained, "{}");
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.RemoveModelAssetsAsync(plan, hash));
        Assert.True(File.Exists(Path.Combine(directory, "a.bin")));
        File.Delete(retained); // Owned fixture only; production requires the recovery UI.
        Assert.Equal(2, await coordinator.RemoveModelAssetsAsync(plan, hash));
        Assert.Null(store.Snapshot.Settings.ModelCacheDirectory);
        Assert.Equal(plan.Revision + 1, store.Snapshot.Revision);
        Assert.Equal(await File.ReadAllTextAsync(Path.Combine(store.DataDirectory, "workspace.json")),
            await File.ReadAllTextAsync(Path.Combine(store.DataDirectory, "workspace.json.bak")));
    }

    [Fact]
    public void NonWindowsAndPrecancelledRemovalHaveNoDeletionAuthority()
    {
        var (state, hash, directory) = Fixture();
        if (!OperatingSystem.IsWindows())
            Assert.Throws<PlatformNotSupportedException>(() => ModelAssetRemoval.Prepare(state, hash));
        else
            Assert.ThrowsAny<OperationCanceledException>(() => ModelAssetRemoval.Remove(state,
                ModelAssetRemoval.Prepare(state, hash), hash, new CancellationToken(true)));
        Assert.True(File.Exists(Path.Combine(directory, "a.bin")));
    }

    private (WorkspaceState State, string Hash, string Directory) Fixture()
    {
        string directory = Directory.CreateDirectory(Path.Combine(_root, "laya-multilingual-fp32-" + Guid.NewGuid().ToString("N"))).FullName;
        File.WriteAllText(Path.Combine(directory, "a.bin"), "first model asset");
        File.WriteAllText(Path.Combine(directory, "b.bin"), "second model asset");
        File.WriteAllText(Path.Combine(directory, "notes.txt"), "keep notes");
        Directory.CreateDirectory(Path.Combine(directory, "unknown"));
        File.WriteAllText(Path.Combine(directory, "unknown", "item.txt"), "keep nested");
        var files = new[] { "a.bin", "b.bin" }.ToDictionary(name => name,
            name => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, name)))));
        string json = JsonSerializer.Serialize(new { files });
        File.WriteAllText(Path.Combine(directory, "manifest.json"), json);
        string managed = Directory.CreateDirectory(Path.Combine(_root, "user-files")).FullName;
        File.WriteAllText(Path.Combine(managed, "user.txt"), "user content");
        return (new WorkspaceState { Revision = 5, Settings = new WorkspaceSettings { ModelCacheDirectory = directory, ManagedRoot = managed } },
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))), directory);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
