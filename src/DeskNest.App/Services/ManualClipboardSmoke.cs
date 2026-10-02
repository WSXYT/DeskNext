using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using DeskNest.App.Views;
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
        var window = new Window { Width = 1280, Height = 720 };
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
            var view = new StudioView { DataContext = studio };
            window.Content = view;
            await studio.DropPathsOnSpaceAsync([sourcePath]);
            Guid fileId = store.Snapshot.Files.Single().Id;
            studio.OpenAddSpaceDialog();
            studio.NewSpaceName = "New destination";
            await studio.ConfirmAddSpaceAsync();
            var target = studio.AllSpaces.Single(s => s.Id != source.Id);
            if (Directory.Exists(target.Folder))
                throw new InvalidOperationException("The new managed space must still be metadata-only before paste.");
            studio.SelectSpace(studio.AllSpaces.Single(s => s.Id == source.Id));
            window.UpdateLayout();
            var commandModifier = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
            view.FindControl<ListBox>("FilesListBox")!.ContainerFromIndex(0)!.Focus();
            window.KeyPress(Key.X, commandModifier, PhysicalKey.X, null);
            window.KeyRelease(Key.X, commandModifier, PhysicalKey.X, null);
            await (studio.ExecuteCutFileCommand.ExecutionTask ?? throw new InvalidOperationException("Cut shortcut did not run."));
            studio.SelectSpace(target);
            window.UpdateLayout();
            var spaces = view.GetVisualDescendants().OfType<ListBox>().Single(list => ReferenceEquals(list.ItemsSource, studio.FilteredSpaces));
            spaces.ContainerFromIndex(studio.FilteredSpaces.IndexOf(target))!.Focus();
            window.KeyPress(Key.V, commandModifier, PhysicalKey.V, null);
            window.KeyRelease(Key.V, commandModifier, PhysicalKey.V, null);
            await (studio.ExecutePasteFileCommand.ExecutionTask ?? throw new InvalidOperationException("Paste shortcut did not run for an empty space."));
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

            // The real context menu uses the clicked row, even with no current selection.
            studio.SelectSpace(studio.AllSpaces.Single(s => s.Id == source.Id));
            studio.SelectedFile = null;
            window.UpdateLayout();
            var row = view.GetVisualDescendants().OfType<Border>().Single(b =>
                b.ContextMenu is not null && b.DataContext is WorkspaceFileItemViewModel f && f.Id == fileId);
            var menu = row.ContextMenu!;
            row.RaiseEvent(new Avalonia.Input.ContextRequestedEventArgs());
            var moveMenu = menu.Items.OfType<MenuItem>().SingleOrDefault(item => item.Name == "MoveToSpaceMenu")
                ?? throw new InvalidOperationException("Move submenu is missing from the file menu.");
            var choice = moveMenu.ItemsSource?.Cast<MenuItem>().SingleOrDefault()
                ?? throw new InvalidOperationException($"Move targets missing: context={menu.DataContext?.GetType().Name}, enabled={moveMenu.IsEnabled}.");
            if (!moveMenu.IsEnabled || !Equals(moveMenu.Header, main.Localizer["Files.ActionMoveToSpace"]) ||
                choice.Command != studio.ExecuteManualMoveCommand ||
                !Equals(choice.CommandParameter, (fileId, target.Id)) || studio.SelectedFile?.Id != fileId)
                throw new InvalidOperationException("Move menu did not bind the clicked file and target space.");
            var classification = menu.Items.OfType<MenuItem>().Single(item => item.Name == "ClassificationPreviewMenu");
            if (classification.Command != studio.PreviewClassificationCommand || classification.CommandParameter != row.DataContext)
                throw new InvalidOperationException("Classification preview must target the clicked file row.");
            menu.Close();
            await studio.PreviewClassificationCommand.ExecuteAsync(studio.SelectedFile);
            if (studio.FileActionNotice != main.Localizer["Classification.Setup"] || studio.IsPreviewDialogOpen)
                throw new InvalidOperationException("An unconfigured model must show setup guidance, not a fake suggestion.");

            // Route real headless mouse/key input to the existing Open boundary; never launch a shell here.
            int opens = 0;
            var openExecutor = studio.OnOpenFile!;
            studio.AttachOpenFileExecutor(file =>
            {
                if (file.Id != fileId) throw new InvalidOperationException("Open targeted a different row.");
                opens++;
                return Task.CompletedTask;
            });
            window.UpdateLayout();
            var position = row.TranslatePoint(new Avalonia.Point(24, 20), window)!.Value;
            window.MouseDown(position, MouseButton.Left);
            window.MouseUp(position, MouseButton.Left);
            window.MouseDown(position, MouseButton.Left);
            window.MouseUp(position, MouseButton.Left);
            if (opens != 1) throw new InvalidOperationException("Double-click must open the clicked item once.");
            var files = view.FindControl<ListBox>("FilesListBox")!;
            files.ContainerFromIndex(0)!.Focus();
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            if (opens != 2) throw new InvalidOperationException("Enter must open the selected item once.");
            studio.AttachOpenFileExecutor(openExecutor);

            long shortcutRevision = store.Snapshot.Revision;
            window.KeyPress(Key.F2, RawInputModifiers.None, PhysicalKey.F2, null);
            window.KeyRelease(Key.F2, RawInputModifiers.None, PhysicalKey.F2, null);
            var renameInput = view.FindControl<TextBox>("RenameNameInput")!;
            if (!studio.IsRenameDialogOpen || !renameInput.IsFocused)
                throw new InvalidOperationException("F2 must open and focus the existing rename dialog.");
            window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
            window.KeyRelease(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
            if (studio.IsDeleteConfirmationDialogOpen || store.Snapshot.Revision != shortcutRevision)
                throw new InvalidOperationException("Editing a filename must not trigger a file action.");
            studio.CloseRenameDialog();
            files.ContainerFromIndex(0)!.Focus();
            window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
            window.KeyRelease(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
            if (!studio.IsDeleteConfirmationDialogOpen || !view.FindControl<Button>("DeleteConfirmationCancelButton")!.IsFocused ||
                store.Snapshot.Revision != shortcutRevision || !Exists(sourcePath))
                throw new InvalidOperationException("Delete must request confirmation without changing the source.");
            studio.CloseDeleteConfirmationDialog();

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
            await studio.ExecuteManualMoveAsync((fileId, target.Id));
            if (Directory.Exists(target.Folder) || !Exists(sourcePath) || store.Snapshot.Revision != revision)
                throw new InvalidOperationException("Manual move recreated a missing mapped folder.");
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
            await studio.ExecuteManualMoveAsync((fileId, target.Id));
            if (File.ReadAllText(target.Folder) != "occupied target" || !Exists(sourcePath) || store.Snapshot.Revision != revision)
                throw new InvalidOperationException("Manual move changed an occupied target or created a pending operation.");

            // Exercise the actual mode controls, not native picker interaction.
            studio.OpenAddSpaceDialog();
            var managedMode = view.FindControl<RadioButton>("NewSpaceManagedMode")!;
            var mappedMode = view.FindControl<RadioButton>("NewSpaceMappedMode")!;
            var folderInput = view.FindControl<TextBox>("NewSpaceFolderInput")!;
            var browse = view.FindControl<Button>("BrowseSpaceFolderButton")!;
            window.UpdateLayout();
            if (managedMode.IsChecked != true || !folderInput.IsReadOnly || browse.IsVisible)
                throw new InvalidOperationException("Managed storage must show its generated, read-only path.");
            mappedMode.IsChecked = true;
            window.UpdateLayout();
            if (!studio.IsNewSpaceMapped || folderInput.IsReadOnly || !browse.IsVisible)
                throw new InvalidOperationException("Mapped storage must expose an editable path and browse action.");
            studio.NewSpaceName = "Mapped source";
            folderInput.Text = sourceFolder;
            await studio.ConfirmAddSpaceAsync();
            var mapped = store.Snapshot.Spaces.Single(s => s.Name == "Mapped source");
            if (mapped.Mode != SpaceStorageMode.Mapped || mapped.Folder != sourceFolder ||
                File.ReadAllText(contentPath) != "clipboard fixture" || store.Snapshot.Files.Count != 1)
                throw new InvalidOperationException("Creating a mapped space must retain its path without importing files.");
            var catalog = view.FindControl<Button>("CatalogSpaceButton")!;
            window.UpdateLayout();
            if (!catalog.IsEffectivelyVisible || catalog.Command != studio.CatalogSpaceCommand)
                throw new InvalidOperationException("Mapped space must expose its catalog action.");
            Directory.CreateDirectory(Path.Combine(sourceFolder, ".desknest-trash"));
            string stagedCopy = Path.Combine(sourceFolder, ".desknext-copy-" + Guid.NewGuid().ToString("N"));
            await File.WriteAllTextAsync(stagedCopy, "retain staging");
            long catalogRevision = store.Snapshot.Revision;
            await studio.CatalogSpaceAsync();
            var cataloged = store.Snapshot;
            var entry = cataloged.Files.Single(f => f.SpaceId == mapped.Id);
            if (entry.Path != sourcePath || entry.IsDirectory != directory ||
                cataloged.Revision != catalogRevision + 1 || cataloged.Files.Count != 2 ||
                cataloged.Operations.Count != 1 || File.ReadAllText(contentPath) != "clipboard fixture" ||
                File.ReadAllText(stagedCopy) != "retain staging")
                throw new InvalidOperationException("Catalog must add direct children in one save, without moving them.");
            await studio.CatalogSpaceAsync();
            if (store.Snapshot.Files.Count != 2 || store.Snapshot.Files.Single(f => f.SpaceId == mapped.Id).Id != entry.Id)
                throw new InvalidOperationException("Repeated catalog must preserve existing file identities.");

            studio.SelectSpace(studio.AllSpaces.Single(s => s.Id == source.Id));
            studio.SelectedTabIndex = 3;
            window.UpdateLayout();
            if (view.FindControl<Button>("BrowseModelFolderButton")?.IsEffectivelyVisible != true)
                throw new InvalidOperationException("Model settings must expose their browse action.");
            string bundle = Directory.CreateDirectory(Path.Combine(root, "ModelBundle")).FullName;
            File.WriteAllText(Path.Combine(bundle, "manifest.json"), "{}"); // Selection fixture, not a verified model.
            long modelRevision = store.Snapshot.Revision;
            view.ApplyModelFolderSelection(bundle);
            view.ApplyModelFolderSelection(null);
            if (studio.SettingsModelCache != bundle || store.Snapshot.Revision != modelRevision || store.Snapshot.Settings.ModelCacheDirectory is not null)
                throw new InvalidOperationException("Picking a model folder must update the draft only.");
            await studio.SaveSettingsAsync();
            if (store.Snapshot.Settings.ModelCacheDirectory != bundle)
                throw new InvalidOperationException("Saving settings did not retain the chosen model folder.");
            modelRevision = store.Snapshot.Revision;
            studio.SettingsModelCache = "relative-model-folder";
            await studio.SaveSettingsAsync();
            if (store.Snapshot.Revision != modelRevision || store.Snapshot.Settings.ModelCacheDirectory != bundle)
                throw new InvalidOperationException("An invalid draft silently erased the saved model directory.");
            studio.SettingsModelCache = bundle;
            studio.SelectedTabIndex = 0;
            var floating = view.OpenSelectedSpaceWindow() ?? throw new InvalidOperationException("Space window did not open.");
            floating.UpdateLayout();
            var floatingFiles = floating.FindControl<ListBox>("SpaceWindowFiles")!;
            if (!floating.IsVisible || floating.Space?.Id != source.Id || floatingFiles.ItemCount != 1 ||
                !ReferenceEquals(floating, view.OpenSelectedSpaceWindow()))
                throw new InvalidOperationException("Space window must display its own catalog and reuse an existing window.");
            int floatingOpens = 0;
            studio.AttachOpenFileExecutor(f => { if (f.Id == fileId) floatingOpens++; return Task.CompletedTask; });
            floatingFiles.SelectedIndex = 0;
            floatingFiles.ContainerFromIndex(0)!.Focus();
            floating.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            floating.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            studio.AttachOpenFileExecutor(openExecutor);
            if (floatingOpens != 1) throw new InvalidOperationException("Space window Enter must use the existing open action.");
            await main.UpdateStoreAsync(s => s with { Spaces = s.Spaces.Select(x => x.Id == source.Id ? x with { Name = "Updated space" } : x).ToList() });
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            if (floating.Space?.Name != "Updated space" || !floating.IsVisible ||
                (floatingFiles.SelectedItem as WorkspaceFileItemViewModel)?.Id != fileId)
                throw new InvalidOperationException("Space window must follow snapshot updates without losing selection.");
            floating.Width = 420;
            floating.Height = 480;
            floating.Position = new PixelPoint(60, 80);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            floating.UpdateLayout();
            long layoutRevision = store.Snapshot.Revision;
            int historyCount = store.Snapshot.Operations.Count;
            if (floating.FindControl<Button>("SaveSpaceLayoutButton")?.IsEffectivelyVisible != true)
                throw new InvalidOperationException("The floating window must expose its layout save action.");
            await floating.SavePlacementAsync();
            var savedPlacement = store.Snapshot.Spaces.Single(s => s.Id == source.Id).WindowPlacement;
            if (savedPlacement != new SpaceWindowPlacement(60, 80, 420, 480) ||
                store.Snapshot.Revision != layoutRevision + 1 || store.Snapshot.Operations.Count != historyCount)
                throw new InvalidOperationException($"Saving layout must persist only space metadata: saved={savedPlacement}, bounds={floating.Bounds}, revision={store.Snapshot.Revision}/{layoutRevision + 1}, history={store.Snapshot.Operations.Count}/{historyCount}, notice={studio.FileActionNotice}");
            floating.Close();
            studio.RefreshFromState(store.Snapshot);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            if (floating.Space is not null || floating.DataContext is not null)
                throw new InvalidOperationException("Closed space windows must release their borrowed workspace.");
            floating = view.OpenSelectedSpaceWindow()!;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            floating.UpdateLayout();
            if (floating.Position != new PixelPoint(60, 80) || floating.Bounds.Size != new Size(420, 480))
                throw new InvalidOperationException("Reopened space window did not restore the saved position and size.");
            floating.Close();
            await studio.SaveSpaceWindowPlacementAsync(source.Id, savedPlacement! with { X = int.MaxValue, Y = int.MinValue });
            floating = view.OpenSelectedSpaceWindow()!;
            var screen = floating.Screens.ScreenFromWindow(floating);
            if (screen is not null && !screen.WorkingArea.Contains(floating.Position))
                throw new InvalidOperationException("A layout from a removed monitor must restore on a visible screen.");
            window.Close();
            if (floating.IsVisible || floating.DataContext is not null)
                throw new InvalidOperationException("Closing the workbench must close its space windows.");
        }
        finally
        {
            if (window.Clipboard is { } clipboard) await clipboard.ClearAsync();
            window.Close();
        }
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
}
