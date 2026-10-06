using System.Text.Json.Nodes;
using DeskNest.Core.Flow;
using DeskNest.Core.Storage;
using DeskNest.Core.Workspace;
using Xunit;

namespace DeskNest.Core.Tests;

public sealed class ConfirmedFlowMoveTests
{
    [FlowNativeFact]
    public async Task DirectoryFlowPreservesTopologyRefusesSelfNestingAndChecksUndo()
    {
        string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "DeskNext-flow-directory-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            string fromRoot = Directory.CreateDirectory(Path.Combine(root, "from")).FullName;
            string toRoot = Directory.CreateDirectory(Path.Combine(root, "to")).FullName;
            string source = Directory.CreateDirectory(Path.Combine(fromRoot, "Project")).FullName;
            Directory.CreateDirectory(Path.Combine(source, "empty"));
            File.WriteAllText(Path.Combine(source, "item.txt"), "original");
            var from = new WorkspaceSpace(Guid.NewGuid(), "from", "", SpaceStorageMode.Managed, fromRoot);
            var to = new WorkspaceSpace(Guid.NewGuid(), "to", "", SpaceStorageMode.Managed, toRoot);
            var file = new WorkspaceFile(Guid.NewGuid(), from.Id, "Project", source, true);
            await using var store = await WorkspaceStore.OpenAsync(Path.Combine(root, "state"));
            await store.UpdateAsync(s => s with { Spaces = [from, to], Files = [file] });
            var coordinator = new ManualOrganizationCoordinator(store, new DesktopOrganizationTransaction(Path.Combine(store.DataDirectory, "organization-recovery.json")));
            var host = new ConfirmedFlowMoves(store, coordinator);
            var document = JsonNode.Parse(ManualFlowDefinitions.Create("directory", "", "unused"))!;
            document["actions"]![0]!["type"] = "pogget.action.file.move";
            document["actions"]![0]!["parameters"] = new JsonObject { ["source"] = source + Path.DirectorySeparatorChar, ["destinationDirectory"] = toRoot + Path.DirectorySeparatorChar };
            string json = document.ToJsonString(), target = Path.Combine(toRoot, "Project");
            string library = Environment.GetEnvironmentVariable("DESKNEXT_FLOW_NATIVE_LIBRARY")!;
            Task<string> Move(FlowMove m, CancellationToken t) => host.ExecuteAsync(m, (_, _) => Task.FromResult(true), t);
            Assert.Equal(1, await ManualFlowRunner.RunAsync(json, (_, _) => Task.FromResult(true), nativeLibraryPath: library, executeMove: Move, validateMove: host.Validate));
            Assert.False(Directory.Exists(source));
            Assert.True(Directory.Exists(Path.Combine(target, "empty")));
            var operation = Assert.Single(store.Snapshot.Operations);
            Assert.Equal(2, operation.OriginalDirectoryNativeIds!.Count);
            Assert.Single(operation.OriginalDirectoryManifest!);
            await coordinator.UndoOperationAsync(operation.Id);
            Assert.True(Directory.Exists(Path.Combine(source, "empty")));
            Assert.Equal("original", File.ReadAllText(Path.Combine(source, "item.txt")));
            Assert.False(Directory.Exists(target));

            string nested = Directory.CreateDirectory(Path.Combine(source, "nested")).FullName;
            await store.UpdateAsync(s => s with { Spaces = [from, to with { Folder = nested }] });
            long revision = store.Snapshot.Revision;
            Assert.Throws<InvalidDataException>(() => host.Validate(new FlowMove(source, Path.Combine(nested, "Project"))));
            Assert.Equal(revision, store.Snapshot.Revision);
            Assert.Empty(Directory.GetFileSystemEntries(nested));
            await store.UpdateAsync(s => s with { Spaces = [from, to] });
            Assert.Equal(1, await ManualFlowRunner.RunAsync(json, (_, _) => Task.FromResult(true), nativeLibraryPath: library, executeMove: Move, validateMove: host.Validate));
            File.WriteAllText(Path.Combine(target, "item.txt"), "changed");
            await Assert.ThrowsAsync<IOException>(() => coordinator.UndoOperationAsync(store.Snapshot.Operations.Last().Id));
            Assert.False(Directory.Exists(source));
            Assert.Equal("changed", File.ReadAllText(Path.Combine(target, "item.txt")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [FlowNativeFact]
    public async Task NativeMovesRequireCurrentConfirmationAndRetainNormalUndo()
    {
        string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "DeskNext-flow-move-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            string sourceFolder = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
            string targetFolder = Directory.CreateDirectory(Path.Combine(root, "target")).FullName;
            string source = Path.Combine(sourceFolder, "file.txt"), destination = Path.Combine(targetFolder, "file.txt");
            File.WriteAllText(source, "retain");
            var from = new WorkspaceSpace(Guid.NewGuid(), "source", "", SpaceStorageMode.Managed, sourceFolder);
            var to = new WorkspaceSpace(Guid.NewGuid(), "target", "", SpaceStorageMode.Managed, targetFolder);
            var file = new WorkspaceFile(Guid.NewGuid(), from.Id, "file.txt", source, false);
            await using var store = await WorkspaceStore.OpenAsync(Path.Combine(root, "state"));
            await store.UpdateAsync(s => s with { Spaces = [from, to], Files = [file] });
            var coordinator = new ManualOrganizationCoordinator(store, new DesktopOrganizationTransaction(Path.Combine(store.DataDirectory, "organization-recovery.json")));
            var host = new ConfirmedFlowMoves(store, coordinator);
            var document = JsonNode.Parse(ManualFlowDefinitions.Create("flow", "after", "after"))!;
            document["actions"]!.AsArray().Insert(0, new JsonObject { ["id"] = "move", ["type"] = "pogget.action.file.move", ["version"] = 1, ["enabled"] = true,
                ["parameters"] = new JsonObject { ["source"] = source, ["destinationDirectory"] = targetFolder } });
            string json = document.ToJsonString();
            string library = Environment.GetEnvironmentVariable("DESKNEXT_FLOW_NATIVE_LIBRARY")!;
            int prompts = 0;
            Task<bool> Show(FlowPrompt _, CancellationToken __) { prompts++; return Task.FromResult(true); }
            Task<string> Move(FlowMove move, CancellationToken token) => host.ExecuteAsync(move, (_, _) => Task.FromResult(true), token);
            Assert.Equal(2, await ManualFlowRunner.RunAsync(json, Show, nativeLibraryPath: library, executeMove: Move, validateMove: host.Validate));
            Assert.Equal(1, prompts);
            Assert.False(File.Exists(source));
            var operation = Assert.Single(store.Snapshot.Operations);
            Assert.Equal(ProposedOperationStatus.Completed, operation.Status);
            await coordinator.UndoOperationAsync(operation.Id);
            Assert.Equal("retain", File.ReadAllText(source));

            prompts = 0;
            long revision = store.Snapshot.Revision;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ManualFlowRunner.RunAsync(json, Show, nativeLibraryPath: library,
                executeMove: (m, t) => host.ExecuteAsync(m, (_, _) => Task.FromResult(false), t), validateMove: host.Validate));
            Assert.Equal(0, prompts);
            Assert.Equal(revision, store.Snapshot.Revision);
            await Assert.ThrowsAsync<InvalidDataException>(() => ManualFlowRunner.RunAsync(json, Show, nativeLibraryPath: library,
                executeMove: (m, t) => host.ExecuteAsync(m, async (_, _) => { await store.UpdateAsync(s => s); return true; }, t), validateMove: host.Validate));
            Assert.True(File.Exists(source));
            Assert.False(File.Exists(destination));
            Assert.Equal(0, prompts);

            using var cancel = new CancellationTokenSource();
            var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var observed = new List<FlowStepCompletion>();
            var run = ManualFlowRunner.RunAsync(json, Show, cancel.Token, library, async (m, t) =>
            {
                string actual = await Move(m, t);
                committed.TrySetResult();
                await release.Task;
                return actual;
            }, host.Validate, observed.Add);
            try
            {
                await committed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                cancel.Cancel();
                await Assert.ThrowsAsync<TimeoutException>(() => run.WaitAsync(TimeSpan.FromMilliseconds(100)));
            }
            finally { release.TrySetResult(); }
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(new FlowStepCompletion(1, "move", true), Assert.Single(observed));
            Assert.Equal(0, prompts);
            Assert.Equal("retain", File.ReadAllText(destination));
            var completed = store.Snapshot.Operations.Last();
            Assert.Equal(ProposedOperationStatus.Completed, completed.Status);
            await coordinator.UndoOperationAsync(completed.Id);
            Assert.Equal("retain", File.ReadAllText(source));

            // Source disappears after managed preflight: native failure must not fall through to the prompt.
            await Assert.ThrowsAsync<InvalidDataException>(() => ManualFlowRunner.RunAsync(json, Show, nativeLibraryPath: library,
                executeMove: Move, validateMove: m => { host.Validate(m); File.Delete(source); }));
            Assert.Equal(0, prompts);
            Assert.False(File.Exists(destination));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
