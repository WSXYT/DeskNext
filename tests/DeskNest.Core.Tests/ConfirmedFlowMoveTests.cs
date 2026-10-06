using System.Text.Json.Nodes;
using DeskNest.Core.Flow;
using DeskNest.Core.Storage;
using DeskNest.Core.Workspace;
using Xunit;

namespace DeskNest.Core.Tests;

public sealed class ConfirmedFlowMoveTests
{
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
            var run = ManualFlowRunner.RunAsync(json, Show, cancel.Token, library, async (m, t) =>
            {
                string actual = await Move(m, t);
                committed.TrySetResult();
                await release.Task;
                return actual;
            }, host.Validate);
            try
            {
                await committed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                cancel.Cancel();
                await Assert.ThrowsAsync<TimeoutException>(() => run.WaitAsync(TimeSpan.FromMilliseconds(100)));
            }
            finally { release.TrySetResult(); }
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
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
