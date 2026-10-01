using Avalonia.Controls;
using DeskNest.App.ViewModels;
using DeskNest.Core.Workspace;

namespace DeskNest.App.Services;

// Opt-in smoke only: real UI callbacks and Core transactions, isolated files and an in-memory clipboard.
// This does not establish native clipboard interop or physical keyboard/mouse acceptance.
internal static class ManualClipboardSmoke
{
    internal static async Task VerifyAsync(string root, bool directory)
    {
        string managedRoot = Path.Combine(root, "managed");
        string sourceFolder = Directory.CreateDirectory(Path.Combine(managedRoot, "Source")).FullName;
        string sourcePath = Path.Combine(sourceFolder, directory ? "Project" : "document.txt");
        if (directory) Directory.CreateDirectory(Path.Combine(sourcePath, "empty"));
        string contentPath = directory ? Path.Combine(sourcePath, "document.txt") : sourcePath;
        await File.WriteAllTextAsync(contentPath, "clipboard fixture");
        var source = new WorkspaceSpace(Guid.NewGuid(), "Source", "", SpaceStorageMode.Managed, sourceFolder);
        await using var store = await WorkspaceStore.OpenAsync(root);
        await store.UpdateAsync(state => state with
        {
            OnboardingComplete = true,
            OnboardingStep = 5,
            Spaces = [source],
            Settings = state.Settings with { ManagedRoot = managedRoot }
        });
        var window = new Window();
        window.Show();
        try
        {
            var clipboard = window.Clipboard ?? throw new InvalidOperationException("Headless clipboard is unavailable.");
            bool wrongThreadRefused = false;
            try { await Task.Run(() => AvaloniaClipboardBridge.TryGetFilePayloadAsync(clipboard)); }
            catch (InvalidOperationException) { wrongThreadRefused = true; }
            if (!wrongThreadRefused) throw new InvalidOperationException("Clipboard access was allowed off the UI dispatcher.");
            await using var main = new MainWindowViewModel(store, ownsStore: false, clipboardProvider: () => clipboard);
            var studio = main.Studio ?? throw new InvalidOperationException("Clipboard fixture has no Studio.");
            await studio.DropPathsOnSpaceAsync([sourcePath]);
            Guid fileId = store.Snapshot.Files.Single().Id;
            studio.OpenAddSpaceDialog();
            studio.NewSpaceName = "New destination";
            await studio.ConfirmAddSpaceAsync();
            var target = studio.AllSpaces.Single(s => s.Id != source.Id);
            if (Directory.Exists(target.Folder))
                throw new InvalidOperationException("The new managed space must still be metadata-only before paste.");
            studio.SelectSpace(studio.AllSpaces.Single(s => s.Id == source.Id));
            await studio.ExecuteCutFileAsync(studio.SelectedFile);
            studio.SelectSpace(target);
            await studio.ExecutePasteFileAsync(target);
            string destination = Path.Combine(target.Folder, Path.GetFileName(sourcePath));
            var moved = store.Snapshot;
            if (Exists(sourcePath) || !Exists(destination) || moved.Files.Single().Id != fileId ||
                moved.Files.Single().SpaceId != target.Id || moved.Operations.Count != 1 ||
                moved.Operations[0].Status != ProposedOperationStatus.Completed)
                throw new InvalidOperationException("Cut/paste into a new managed space failed: " + studio.FileActionNotice);
            if (await AvaloniaClipboardBridge.TryGetFilePayloadAsync(clipboard) is not null)
                throw new InvalidOperationException("Successful cut/paste did not clear the clipboard.");
            await studio.ExecuteUndoManualMoveAsync(moved.Operations[0].Id);
            if (File.ReadAllText(contentPath) != "clipboard fixture" || Exists(destination) ||
                (directory && !Directory.Exists(Path.Combine(sourcePath, "empty"))) ||
                store.Snapshot.Files.Single().SpaceId != source.Id ||
                store.Snapshot.Operations.Single().Status != ProposedOperationStatus.Undone)
                throw new InvalidOperationException("Undo did not restore the cut source.");

            // A missing mapped folder is not a request to recreate an external directory.
            Directory.Delete(target.Folder);
            await main.UpdateStoreAsync(state => state with
            {
                Spaces = state.Spaces.Select(s => s.Id == target.Id ? s with { Mode = SpaceStorageMode.Mapped } : s).ToList()
            });
            studio.SelectSpace(studio.AllSpaces.Single(s => s.Id == source.Id));
            await studio.ExecuteCutFileAsync(studio.SelectedFile);
            long revision = store.Snapshot.Revision;
            await studio.ExecutePasteFileAsync(studio.AllSpaces.Single(s => s.Id == target.Id));
            if (Directory.Exists(target.Folder) || !Exists(sourcePath) || store.Snapshot.Revision != revision ||
                studio.FileActionNotice != main.Localizer["Validation.FileNotFound"])
                throw new InvalidOperationException("Paste recreated a missing mapped folder or changed metadata.");
            await main.UpdateStoreAsync(state => state with
            {
                Spaces = state.Spaces.Select(s => s.Id == target.Id ? s with { Mode = SpaceStorageMode.Managed } : s).ToList()
            });
            await File.WriteAllTextAsync(target.Folder, "occupied target");
            await studio.ExecuteCutFileAsync(studio.SelectedFile);
            revision = store.Snapshot.Revision;
            await studio.ExecutePasteFileAsync(studio.AllSpaces.Single(s => s.Id == target.Id));
            if (File.ReadAllText(target.Folder) != "occupied target" || !Exists(sourcePath) || store.Snapshot.Revision != revision)
                throw new InvalidOperationException("Paste changed an occupied target or created a pending operation.");
        }
        finally
        {
            if (window.Clipboard is { } clipboard) await clipboard.ClearAsync();
            window.Close();
        }
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
}
