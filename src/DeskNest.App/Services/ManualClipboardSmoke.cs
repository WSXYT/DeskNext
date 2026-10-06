using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
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
            VerifyFileList(view, store.Snapshot);
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
            // Undo is reachable from the clicked file, in either workspace surface.
            window.UpdateLayout();
            var undoRow = view.GetVisualDescendants().OfType<Border>().Single(b =>
                b.ContextMenu is not null && b.DataContext is WorkspaceFileItemViewModel f && f.Id == fileId);
            studio.SelectedFile = null;
            undoRow.ContextMenu!.Open(undoRow);
            var undoMenu = undoRow.ContextMenu.Items.OfType<MenuItem>().Single(item => item.Name == "UndoFileMenu");
            if (!undoMenu.IsVisible || undoMenu.Command != studio.ExecuteUndoManualMoveCommand ||
                !Equals(undoMenu.CommandParameter, moved.Operations[0].Id))
                throw new InvalidOperationException("Workbench undo must address the clicked file's recorded operation.");
            undoRow.ContextMenu.Close();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var undoWindow = view.OpenSelectedSpaceWindow() ?? throw new InvalidOperationException("Undo space window is unavailable.");
            undoWindow.UpdateLayout();
            var floatingUndoRow = undoWindow.GetVisualDescendants().OfType<Border>().Single(b =>
                b.ContextMenu is not null && b.DataContext is WorkspaceFileItemViewModel f && f.Id == fileId);
            floatingUndoRow.ContextMenu!.Open(floatingUndoRow);
            var floatingUndoMenu = floatingUndoRow.ContextMenu.Items.OfType<MenuItem>().Single(item => item.Name == "UndoFileMenu");
            if (floatingUndoMenu.Command != undoMenu.Command || !Equals(floatingUndoMenu.CommandParameter, undoMenu.CommandParameter))
                throw new InvalidOperationException("Space-window undo must reuse the same recorded operation.");
            floatingUndoRow.ContextMenu.Close();
            undoWindow.Close();
            window.Activate();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await studio.ExecuteUndoManualMoveCommand.ExecuteAsync(undoMenu.CommandParameter);
            if (File.ReadAllText(contentPath) != "clipboard fixture" || Exists(destination) ||
                (directory && !Directory.Exists(Path.Combine(sourcePath, "empty"))) ||
                store.Snapshot.Files.Single().SpaceId != source.Id ||
                store.Snapshot.Operations.Single().Status != ProposedOperationStatus.Undone)
                throw new InvalidOperationException("Undo did not restore the cut source.");
            if (studio.FindUndoForFile(studio.AllSpaces.SelectMany(s => s.Files).Single(f => f.Id == fileId)) is not null)
                throw new InvalidOperationException("An undone operation must not remain offered in the file menu.");

            // Outgoing drag supplies one platform file reference, never our private cut marker.
            // Payload preparation does not establish native file-manager drop interoperability.
            long dragRevision = store.Snapshot.Revision;
            using (var item = await FileDragSource.ResolveItemAsync(window, studio, fileId, source.Id, sourcePath))
            using (var transfer = FileDragSource.CreateTransfer(item)) // Not handed to the OS in this check.
            {
                if (FileDragSource.AllowedEffects != DragDropEffects.Copy || transfer.Formats.Count() != 1 ||
                    !transfer.Formats.Contains(DataFormat.File) || transfer.TryGetFiles()?.Single().TryGetLocalPath() != sourcePath ||
                    store.Snapshot.Revision != dragRevision || File.ReadAllText(contentPath) != "clipboard fixture")
                    throw new InvalidOperationException("Outgoing drag must preserve source and metadata and export only a file reference.");
            }

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
            Avalonia.Threading.Dispatcher.UIThread.RunJobs(); // Finish popup teardown before injecting pointer input.
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
            if (opens != 1) throw new InvalidOperationException($"Double-click must open the clicked item once; observed {opens} opens.");
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
            var verifyModel = view.FindControl<Button>("VerifyLocalModelButton")!;
            if (!verifyModel.IsEffectivelyVisible || verifyModel.Command != studio.VerifyLocalModelCommand)
                throw new InvalidOperationException("Model settings must expose their verification action.");
            await studio.VerifyLocalModelCommand.ExecuteAsync(null);
            if (string.IsNullOrWhiteSpace(studio.ModelVerificationNotice) ||
                studio.ModelVerificationNotice == main.Localizer["Classification.BundleVerified"] ||
                studio.ModelVerificationNotice == main.Localizer["Classification.VerifyingBundle"] ||
                store.Snapshot.Revision != modelRevision || File.ReadAllText(Path.Combine(bundle, "manifest.json")) != "{}")
                throw new InvalidOperationException("An invalid bundle must report failure without saving settings or modifying files.");
            await studio.SaveSettingsAsync();
            if (store.Snapshot.Settings.ModelCacheDirectory != bundle)
                throw new InvalidOperationException("Saving settings did not retain the chosen model folder.");
            modelRevision = store.Snapshot.Revision;
            studio.SettingsModelCache = "relative-model-folder";
            await studio.SaveSettingsAsync();
            if (store.Snapshot.Revision != modelRevision || store.Snapshot.Settings.ModelCacheDirectory != bundle)
                throw new InvalidOperationException("An invalid draft silently erased the saved model directory.");
            studio.SettingsModelCache = bundle;

            if (view.FindControl<Button>("ImportModelPackageButton")?.IsEffectivelyVisible != true ||
                view.FindControl<Button>("CancelModelInstallButton")?.Command != studio.InstallLocalModelPackageCancelCommand)
                throw new InvalidOperationException("Model package installation and cancellation must have real UI entries.");
            var repair = view.FindControl<Button>("RepairModelPackageButton");
            if (view.FindControl<Button>("DownloadModelPackageButton")?.Command != studio.InstallLocalModelPackageCommand ||
                repair?.Command != studio.InstallLocalModelPackageCommand || repair.CommandParameter is not null || !repair.IsEffectivelyVisible)
                throw new InvalidOperationException("Online installation and repair must share the cancellable model-install command.");
            var install = studio.OnInstallLocalModelPackage;
            try
            {
                studio.OnInstallLocalModelPackage = async (package, progress, token) =>
                {
                    if (package is not null) throw new InvalidOperationException("The download entry must not pretend to select an offline file.");
                    await Task.Delay(System.Threading.Timeout.Infinite, token);
                    return bundle;
                };
                var downloading = studio.InstallLocalModelPackageCommand.ExecuteAsync(null);
                studio.InstallLocalModelPackageCancelCommand.Execute(null);
                await downloading;
                if (studio.ModelInstallNotice != studio.Localizer["Classification.Cancelled"] || studio.SettingsModelCache != bundle)
                    throw new InvalidOperationException("Cancelled download changed model selection.");
            }
            finally { studio.OnInstallLocalModelPackage = install; }
            string deploymentRoot = Directory.CreateDirectory(Path.Combine(root, "Model installation target")).FullName;
            long beforeLocation = store.Snapshot.Revision;
            if (view.FindControl<Button>("BrowseModelInstallLocationButton")?.IsEffectivelyVisible != true ||
                view.FindControl<TextBox>("ModelInstallLocationInput")?.IsReadOnly != true)
                throw new InvalidOperationException("Model installation must expose a folder picker and read-only destination.");
            view.ApplyModelInstallLocation(deploymentRoot);
            view.ApplyModelInstallLocation(null);
            if (studio.ModelInstallRoot != deploymentRoot || studio.SettingsModelCache != bundle || store.Snapshot.Revision != beforeLocation)
                throw new InvalidOperationException("Choosing an installation destination must not activate or save a model.");
            string invalidPackage = Path.Combine(root, "invalid-model.zip");
            await File.WriteAllTextAsync(invalidPackage, "invalid model package");
            long beforeInstall = store.Snapshot.Revision;
            await studio.InstallLocalModelPackageCommand.ExecuteAsync(invalidPackage);
            if (string.IsNullOrWhiteSpace(studio.ModelInstallNotice) || studio.SettingsModelCache != bundle ||
                store.Snapshot.Revision != beforeInstall || File.ReadAllText(invalidPackage) != "invalid model package")
                throw new InvalidOperationException("Invalid model installation must retain the input and active model settings.");
            if (!directory && Environment.GetEnvironmentVariable("DESKNEXT_TEST_MODEL_ARCHIVE") is { Length: > 0 } package)
            {
                // Exercise the online/repair entry entirely offline by seeding its previous application cache.
                string previousCache = Directory.CreateDirectory(Path.Combine(store.DataDirectory, "models")).FullName;
                string archiveName = ".model-download-" + DeskNest.Inference.LocalModelInstaller.ArchiveSha256 + ".partial";
                string previousArchive = Path.Combine(previousCache, archiveName);
                File.Copy(package, previousArchive);
                await studio.InstallLocalModelPackageCommand.ExecuteAsync(null);
                string copiedArchive = Path.Combine(deploymentRoot, WorkspaceSettings.ModelDownloadCacheFolderName, archiveName);
                if (!File.Exists(copiedArchive) || new FileInfo(previousArchive).Length != DeskNest.Inference.LocalModelInstaller.ArchiveBytes)
                    throw new InvalidOperationException("Changing model location must copy the verified cache and retain the original.");
                string installed = studio.SettingsModelCache;
                if (installed == bundle || Path.GetDirectoryName(installed) != deploymentRoot || !File.Exists(Path.Combine(installed, "manifest.json")) ||
                    store.Snapshot.Revision != beforeInstall || store.Snapshot.Settings.ModelCacheDirectory != bundle ||
                    File.ReadAllText(Path.Combine(bundle, "manifest.json")) != "{}")
                    throw new InvalidOperationException("Installing the real model must prepare a verified draft without activating it: " + studio.ModelInstallNotice);
                HeadlessSmokeRunner.VerifyTemplateCandidateBudgets(root, installed);
                await studio.SaveSettingsAsync();
                if (store.Snapshot.Settings.ModelCacheDirectory != installed)
                    throw new InvalidOperationException("Explicit settings save did not activate the installed model.");
                await using (var reopenedViewModel = new MainWindowViewModel(store, ownsStore: false))
                {
                    var reopened = reopenedViewModel.Studio!;
                    if (reopened.ModelInstallRoot != deploymentRoot)
                        throw new InvalidOperationException("The activated model must retain its installation location on workspace reload.");
                    long beforePreview = store.Snapshot.Revision;
                    await reopened.PreviewClassificationCommand.ExecuteAsync(reopened.AllSpaces.SelectMany(s => s.Files).Single(f => f.Id == fileId));
                    if (!reopened.IsPreviewDialogOpen || reopened.PreviewKind != main.Localizer["Classification.LocalCpu"] ||
                        store.Snapshot.Revision != beforePreview || File.ReadAllText(contentPath) != "clipboard fixture")
                        throw new InvalidOperationException("The installed model failed the reopened CPU preview: " + reopened.FileActionNotice);
                    reopened.ClosePreviewDialog();
                    int? workerId = reopenedViewModel.LocalWorkerProcessId;
                    await reopened.PreviewClassificationCommand.ExecuteAsync(reopened.AllSpaces.SelectMany(s => s.Files).Single(f => f.Id == fileId));
                    if (workerId is null || reopenedViewModel.LocalWorkerProcessId != workerId || !reopened.IsPreviewDialogOpen ||
                        store.Snapshot.Revision != beforePreview)
                        throw new InvalidOperationException("The reopened model must reuse its worker without changing metadata.");
                    reopened.ClosePreviewDialog();
                    await reopenedViewModel.DisposeAsync();
                    if (reopenedViewModel.LocalWorkerProcessId is not null || reopenedViewModel.LocalWorkerShutdownError is not null)
                        throw new InvalidOperationException("The reused CPU worker did not exit cleanly: " + reopenedViewModel.LocalWorkerShutdownError);
                    Console.WriteLine("MODEL_PACKAGE_REOPENED_CPU_PREVIEW_VERIFIED: true");
                }
                studio.SettingsModelCache = bundle;
                await studio.SaveSettingsAsync();
                Console.WriteLine("MODEL_PACKAGE_UI_ACTIVATION_VERIFIED: true");
            }

            // Session-key/permission gates only. This smoke never sends a cloud request.
            var jevProvider = view.FindControl<RadioButton>("JevPreviewProvider")!;
            jevProvider.IsChecked = true;
            await studio.SaveSettingsAsync();
            var keyInput = view.FindControl<TextBox>("JevSessionKeyInput")!;
            var consent = view.FindControl<CheckBox>("JevSendConsent")!;
            // Never exercise the default OS credential target: it can hold a user's real key.
            foreach (var action in new[] { ("SaveJevKeyButton", "save"), ("LoadJevKeyButton", "load"), ("DeleteJevKeyButton", "delete") })
            {
                var button = view.FindControl<Button>(action.Item1)!;
                if (button.Command != studio.ManageJevCredentialCommand || !Equals(button.CommandParameter, action.Item2) ||
                    button.IsEffectivelyVisible != OperatingSystem.IsWindows())
                    throw new InvalidOperationException("OS credential buttons must expose explicit platform-gated commands.");
            }
            if (!keyInput.IsEffectivelyVisible || keyInput.PasswordChar == default || studio.JevSendConsent ||
                store.Snapshot.Settings.Provider != InferenceProvider.Jev)
                throw new InvalidOperationException("Jev must expose a masked session key and unchecked sending permission.");
            var previewFile = studio.AllSpaces.SelectMany(s => s.Files).Single(f => f.Id == fileId);
            consent.IsChecked = true; // A missing key must still refuse before HTTP.
            await studio.PreviewClassificationCommand.ExecuteAsync(previewFile);
            if (studio.FileActionNotice != studio.Localizer["Classification.JevSetup"])
                throw new InvalidOperationException("Jev preview accepted a missing session key.");
            keyInput.Text = "session-fixture-not-a-real-key"; // A changed key revokes permission.
            await studio.PreviewClassificationCommand.ExecuteAsync(previewFile);
            if (studio.JevSendConsent || studio.FileActionNotice != studio.Localizer["Classification.JevSetup"])
                throw new InvalidOperationException("Jev preview accepted an unapproved cloud send.");
            await studio.SaveSettingsAsync();
            if (File.ReadAllText(Path.Combine(store.DataDirectory, "workspace.json")).Contains(keyInput.Text!) ||
                File.ReadAllText(Path.Combine(store.DataDirectory, "workspace.json.bak")).Contains(keyInput.Text!))
                throw new InvalidOperationException("A session API key was persisted in workspace storage.");
            studio.ClearJevSessionCommand.Execute(null);
            if (studio.JevSessionKey.Length != 0 || studio.JevSendConsent)
                throw new InvalidOperationException("Clearing the Jev session must drop both key and permission.");
            studio.IsLayaPreview = true;
            var accentInput = view.FindControl<TextBox>("AccentColorInput")!;
            if (!accentInput.IsEffectivelyVisible)
                throw new InvalidOperationException("Accent setting must be visible.");
            var colorPicker = view.FindControl<ColorPicker>("CustomAccentPicker")!;
            long beforeColorDraft = store.Snapshot.Revision;
            colorPicker.Color = Avalonia.Media.Color.Parse("#F7D038");
            Dispatcher.UIThread.RunJobs();
            if (!colorPicker.IsEffectivelyVisible || studio.SettingsAccentColor != "#F7D038" ||
                store.Snapshot.Revision != beforeColorDraft || colorPicker.IsAlphaEnabled)
                throw new InvalidOperationException("Visual color selection must edit the opaque accent draft without saving.");
            await studio.SaveSettingsAsync();
            if (store.Snapshot.Settings.AccentColor != "#F7D038" ||
                !File.ReadAllText(Path.Combine(root, "workspace.json")).Contains("#F7D038"))
                throw new InvalidOperationException("Accent must persist through the existing settings save.");
            foreach (var variant in new[] { Avalonia.Styling.ThemeVariant.Light, Avalonia.Styling.ThemeVariant.Dark })
            {
                Avalonia.Application.Current!.TryGetResource("AccentPrimaryBrush", variant, out var background);
                Avalonia.Application.Current.TryGetResource("AccentPrimaryFgBrush", variant, out var foreground);
                if (background is not Avalonia.Media.SolidColorBrush bg || bg.Color != Avalonia.Media.Color.Parse("#F7D038") ||
                    foreground is not Avalonia.Media.SolidColorBrush fg || Themes.ThemeManager.Contrast(bg.Color, fg.Color) < 4.5)
                    throw new InvalidOperationException("Custom accent must reach both themes with readable foreground.");
            }
            long beforeInvalidAccent = store.Snapshot.Revision;
            studio.SettingsAccentColor = "invalid";
            await studio.SaveSettingsAsync();
            if (store.Snapshot.Revision != beforeInvalidAccent || Themes.ThemeManager.Instance.AccentColor != "#F7D038")
                throw new InvalidOperationException("Invalid color must not alter the saved palette.");
            studio.SelectAccentCommand.Execute(null);
            await studio.SaveSettingsAsync();
            if (store.Snapshot.Settings.AccentColor is not null || Themes.ThemeManager.Instance.AccentColor is not null)
                throw new InvalidOperationException("Reset must restore the default theme colors.");
            if (colorPicker.ColorSpectrumShape != ColorSpectrumShape.Ring || !colorPicker.IsColorComponentsVisible || !colorPicker.IsHexInputVisible)
                throw new InvalidOperationException("Custom accent must expose a complete color wheel and numeric controls.");
            studio.IsWindowsAccent = true;
            await studio.SaveSettingsAsync();
            if (store.Snapshot.Settings.AccentSource != AccentColorSource.Windows || colorPicker.IsEffectivelyEnabled)
                throw new InvalidOperationException("Automatic accent mode must persist without enabling manual controls.");
            studio.SelectAccentCommand.Execute(null);
            await studio.SaveSettingsAsync();
            string wallpaper = Path.Combine(root, "accent-wallpaper.png");
            using (var bitmap = new Avalonia.Media.Imaging.WriteableBitmap(new PixelSize(1, 1), new Vector(96, 96),
                Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Unpremul))
            {
                using (var pixels = bitmap.Lock()) System.Runtime.InteropServices.Marshal.WriteInt32(pixels.Address, unchecked((int)0xFF336699));
                bitmap.Save(wallpaper, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
            if (Themes.ThemeManager.ReadWallpaperColor(wallpaper) != Avalonia.Media.Color.Parse("#336699"))
                throw new InvalidOperationException("Wallpaper sampling must preserve the fixture's dominant color.");
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
            Console.WriteLine($"[MANUAL-WORKFLOW] {(directory ? "directory" : "file")}: space-window actions");
            var floatingRow = floatingFiles.GetVisualDescendants().OfType<Border>()
                .First(b => b.ContextMenu is not null && b.DataContext is WorkspaceFileItemViewModel f && f.Id == fileId);
            studio.SelectSpace(studio.AllSpaces.Single(s => s.Id == mapped.Id));
            studio.SelectedFile = null;
            var floatingMenu = floatingRow.ContextMenu!;
            floatingMenu.Open(floatingRow);
            var floatingCopy = floatingMenu.Items.OfType<MenuItem>().Single(i => i.Command == studio.ExecuteCopyFileCommand);
            var floatingPaste = floatingMenu.Items.OfType<MenuItem>().Single(i => i.Command == studio.ExecutePasteFileCommand);
            if (studio.SelectedSpace?.Id != source.Id || studio.SelectedFile?.Id != fileId ||
                floatingCopy.CommandParameter is not WorkspaceFileItemViewModel copyRow || copyRow.Id != fileId ||
                floatingCopy.IsEnabled != OperatingSystem.IsWindows() ||
                floatingPaste.CommandParameter is not SpaceItemViewModel pasteSpace || pasteSpace.Id != source.Id)
                throw new InvalidOperationException("Space-window actions must use the clicked row and its own space, with platform copy gating.");
            floatingMenu.Close();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.Activate();
            var spaceRow = view.FindControl<ListBox>("SpacesListBox")!.GetVisualDescendants().OfType<Border>()
                .First(border => border.ContextMenu is not null && border.DataContext is SpaceItemViewModel item && item.Id == source.Id);
            var spaceMenu = spaceRow.ContextMenu!;
            spaceMenu.Open(spaceRow);
            var editSpace = spaceMenu.Items.OfType<MenuItem>().Single();
            if (editSpace.Command != studio.OpenEditSpaceCommand || editSpace.CommandParameter is not SpaceItemViewModel editingSpace ||
                editingSpace.Id != source.Id)
                throw new InvalidOperationException("The space menu must edit the clicked space.");
            editSpace.Command!.Execute(editSpace.CommandParameter);
            spaceMenu.Close();
            Dispatcher.UIThread.RunJobs();
            var beforeSpaceEdit = store.Snapshot;
            if (!studio.IsEditingSpace || !studio.IsAddSpaceDialogOpen ||
                !view.FindControl<TextBox>("NewSpaceFolderInput")!.IsReadOnly ||
                view.FindControl<RadioButton>("NewSpaceMappedMode")!.IsEffectivelyEnabled ||
                studio.NewSpaceFolder != source.Folder)
                throw new InvalidOperationException("Editing space details must keep storage mode and path read-only.");
            studio.NewSpaceName = "Cancelled name";
            studio.CloseAddSpaceDialog();
            if (store.Snapshot.Revision != beforeSpaceEdit.Revision)
                throw new InvalidOperationException("Cancelling space details must not save the draft.");
            studio.OpenEditSpace(studio.AllSpaces.Single(item => item.Id == source.Id));
            studio.NewSpaceName = "Updated space";
            studio.NewSpaceDesc = "Updated description";
            await studio.ConfirmAddSpaceAsync();
            var editedSpace = store.Snapshot.Spaces.Single(item => item.Id == source.Id);
            if (studio.IsAddSpaceDialogOpen || editedSpace.Folder != source.Folder || editedSpace.Mode != source.Mode ||
                editedSpace.Description != "Updated description" || store.Snapshot.Revision != beforeSpaceEdit.Revision + 1 ||
                store.Snapshot.Operations.Count != beforeSpaceEdit.Operations.Count || File.ReadAllText(contentPath) != "clipboard fixture")
                throw new InvalidOperationException("Saving space details must change metadata only, preserving storage and history.");
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            if (floating.Space?.Name != "Updated space" || !floating.IsVisible ||
                (floatingFiles.SelectedItem as WorkspaceFileItemViewModel)?.Id != fileId)
                throw new InvalidOperationException("Space window must follow snapshot updates without losing selection.");
            // Presentation choices persist without touching source files or operation history.
            if (floating.Topmost) throw new InvalidOperationException("Space windows must not default to always-on-top.");
            int appearanceHistory = store.Snapshot.Operations.Count;
            await floating.SetFileViewAsync(SpaceFileView.Grid);
            Dispatcher.UIThread.RunJobs();
            if (store.Snapshot.Spaces.Single(s => s.Id == source.Id).FileView != SpaceFileView.Grid ||
                floatingFiles.GetVisualDescendants().OfType<WrapPanel>().Any() == false)
                throw new InvalidOperationException("Grid view must use the saved per-space arrangement.");
            await floating.SetFileViewAsync(SpaceFileView.Details);
            Dispatcher.UIThread.RunJobs();
            if (!floating.IsDetailsView) throw new InvalidOperationException("Details view was not applied.");
            await floating.ApplyIconSelectionAsync(fileId, false, wallpaper);
            await floating.ApplyIconSelectionAsync(source.Id, true, wallpaper);
            var capsuleView = new DropCapsuleViewModel(studio);
            using (capsuleView)
            {
                await capsuleView.SetCustomIconAsync(wallpaper);
                if (capsuleView.CustomIconPath != wallpaper || !capsuleView.HasCustomIcon)
                    throw new InvalidOperationException("Capsule icon must follow workspace presentation settings.");
                await capsuleView.SetCustomIconAsync(null);
            }
            if (store.Snapshot.Files.Single(f => f.Id == fileId).CustomIconPath != wallpaper ||
                store.Snapshot.Spaces.Single(s => s.Id == source.Id).CustomIconPath != wallpaper ||
                store.Snapshot.Operations.Count != appearanceHistory || File.ReadAllText(contentPath) != "clipboard fixture")
                throw new InvalidOperationException("Custom icons must change only presentation metadata.");
            using (var customIcon = FileIcon.LoadCustom(wallpaper))
                if (customIcon.PixelSize.Width != 64) throw new InvalidOperationException("Custom icon decoding must remain bounded.");
            if (OperatingSystem.IsWindows())
            {
                if (await SystemFileIcons.GetAsync("fixture.txt", false) is null)
                    throw new InvalidOperationException("Windows system/type icon failed. Native stage: " + SystemFileIcons.FailureFor(".txt"));
                if (await SystemFileIcons.GetAsync("README", false) is null)
                    throw new InvalidOperationException("Windows stock-document fallback failed: " + SystemFileIcons.FailureFor(""));
            }
            await floating.ApplyIconSelectionAsync(fileId, false, null);
            await floating.ApplyIconSelectionAsync(source.Id, true, null);
            await floating.SetFileViewAsync(SpaceFileView.List);
            Dispatcher.UIThread.RunJobs();
            floatingFiles.SelectedItem = floating.Space!.Files.Single(f => f.Id == fileId);
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
            // The explicit import uses the same production coordinator and existing undo path.
            string externalParent = Directory.CreateDirectory(Path.Combine(root, "external-import")).FullName;
            string external = Path.Combine(externalParent, directory ? "ImportedProject" : "Imported.txt");
            if (directory) Directory.CreateDirectory(Path.Combine(external, "empty"));
            string externalContent = directory ? Path.Combine(external, "item.txt") : external;
            File.WriteAllText(externalContent, "external fixture");
            studio.SelectSpace(studio.AllSpaces.Single(s => s.Id == source.Id));
            long beforeDrop = store.Snapshot.Revision;
            await studio.DropPathsOnSpaceAsync([external]);
            if (!studio.IsImportConfirmationOpen || studio.ImportSourcePath != external || !Exists(external) ||
                store.Snapshot.Revision != beforeDrop + 1)
                throw new InvalidOperationException("External space drop must create review metadata and request confirmation, never move immediately.");
            studio.CloseImportConfirmation();
            await studio.DropPathsOnSpaceAsync([external]); // Reuse the pending item rather than accumulating duplicates.
            if (store.Snapshot.Revision != beforeDrop + 1 || studio.PendingItems.Count(p => p.Path == external) != 1)
                throw new InvalidOperationException("Repeated external drop must reuse its pending review.");
            studio.CloseImportConfirmation();
            studio.SelectedTabIndex = 1;
            var pending = studio.PendingItems.Single(p => p.Path == external);
            pending.TargetSpace = studio.AllSpaces.Single(s => s.Id == source.Id);
            window.UpdateLayout();
            var importButton = view.GetVisualDescendants().OfType<Button>().Single(b =>
                b.Name == "ImportPendingButton" && b.DataContext is PendingItemViewModel p && p.Id == pending.Id);
            long importRevision = store.Snapshot.Revision;
            var pendingPreview = view.GetVisualDescendants().OfType<Button>().Single(b =>
                b.Name == "PendingFilePreviewButton" && b.DataContext is PendingItemViewModel p && p.Id == pending.Id);
            if (!pendingPreview.IsEffectivelyVisible || pendingPreview.Command != studio.PreviewPendingFileCommand ||
                pendingPreview.CommandParameter != pending)
                throw new InvalidOperationException("Pending preview must target the clicked review item.");
            await studio.PreviewPendingFileCommand.ExecuteAsync(pendingPreview.CommandParameter);
            if (!studio.IsPreviewDialogOpen || studio.PreviewFileName != pending.Name ||
                studio.PreviewKind != (directory ? "directory" : "text") ||
                !studio.PreviewContent.Contains(directory ? "item.txt" : "external fixture") ||
                store.Snapshot.Revision != importRevision || File.ReadAllText(externalContent) != "external fixture")
                throw new InvalidOperationException("Pending preview must be local, read-only and leave import unconfirmed.");
            studio.ClosePreviewDialog();
            importButton.Command!.Execute(importButton.CommandParameter);
            window.UpdateLayout();
            if (view.FindControl<Border>("ImportConfirmationOverlay")?.IsEffectivelyVisible != true || !Exists(external))
                throw new InvalidOperationException("Import must show confirmation before moving anything.");
            studio.CloseImportConfirmation();
            if (store.Snapshot.Revision != importRevision || !Exists(external))
                throw new InvalidOperationException("Cancelling import must leave files and metadata unchanged.");
            studio.OpenImportConfirmation(pending);
            await studio.ConfirmImportAsync();
            if (studio.IsImportConfirmationOpen || Exists(external))
                throw new InvalidOperationException("Confirmed import failed: " + studio.ImportError);
            var importedOperation = store.Snapshot.Operations.Single(o => o.ImportSource?.Id == pending.Id);
            await studio.ExecuteUndoManualMoveAsync(importedOperation.Id);
            if (File.ReadAllText(externalContent) != "external fixture" ||
                !store.Snapshot.Pending.Any(p => p.Id == pending.Id) ||
                store.Snapshot.Operations.Single(o => o.Id == importedOperation.Id).Status != ProposedOperationStatus.Undone ||
                directory && !Directory.Exists(Path.Combine(external, "empty")))
                throw new InvalidOperationException("Undo import must restore the external item and its pending record.");

            if (OperatingSystem.IsWindows())
            {
                // Same production clipboard path, for a new managed folder and an existing mapping.
                foreach (var mode in new[] { SpaceStorageMode.Managed, SpaceStorageMode.Mapped })
                {
                    studio.SelectedTabIndex = 0;
                    studio.OpenAddSpaceDialog();
                    studio.NewSpaceMode = mode;
                    studio.NewSpaceName = "Copy " + mode;
                    if (mode == SpaceStorageMode.Mapped)
                        studio.NewSpaceFolder = Directory.CreateDirectory(Path.Combine(root, "copy-mapped")).FullName;
                    await studio.ConfirmAddSpaceAsync();
                    var copyTarget = studio.AllSpaces.Single(s => s.Name == "Copy " + mode);
                    studio.SelectSpace(studio.AllSpaces.Single(s => s.Id == source.Id));
                    studio.SelectFile(studio.SelectedSpace!.Files.Single(f => f.Id == fileId));
                    window.Activate();
                    window.UpdateLayout();
                    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                    view.FindControl<ListBox>("FilesListBox")!.ContainerFromIndex(0)!.Focus();
                    window.KeyPress(Key.C, commandModifier, PhysicalKey.C, null);
                    window.KeyRelease(Key.C, commandModifier, PhysicalKey.C, null);
                    await (studio.ExecuteCopyFileCommand.ExecutionTask ?? throw new InvalidOperationException("Copy shortcut did not run."));
                    var copyPayload = await AvaloniaClipboardBridge.TryGetFilePayloadAsync(clipboard);
                    if (copyPayload is null || copyPayload.IsCut ||
                        AvaloniaClipboardBridge.ResolveFileSource(copyPayload, store.Snapshot)?.Id != fileId)
                        throw new InvalidOperationException("Copy must preserve its exact catalog source without a cut marker.");
                    var beforeCopy = store.Snapshot;
                    studio.SelectSpace(copyTarget);
                    await studio.ExecutePasteFileAsync(copyTarget);
                    var copied = store.Snapshot.Files.SingleOrDefault(f => f.SpaceId == copyTarget.Id);
                    string copiedPath = Path.Combine(copyTarget.Folder, Path.GetFileName(sourcePath));
                    if (copied is null || copied.Id == fileId || copied.Publication is null ||
                        store.Snapshot.Files.Count != beforeCopy.Files.Count + 1 ||
                        store.Snapshot.Operations.Count != beforeCopy.Operations.Count ||
                        File.ReadAllText(contentPath) != "clipboard fixture" ||
                        File.ReadAllText(directory ? Path.Combine(copiedPath, "document.txt") : copiedPath) != "clipboard fixture" ||
                        directory && !Directory.Exists(Path.Combine(copiedPath, "empty")) ||
                        (await AvaloniaClipboardBridge.TryGetFilePayloadAsync(clipboard))?.IsCut != false)
                        throw new InvalidOperationException("Manual copy must retain source/clipboard, publish a new receipt, and not invent undo history: " + studio.FileActionNotice);
                    long copiedRevision = store.Snapshot.Revision;
                    await studio.ExecutePasteFileAsync(copyTarget); // A known collision must not replace or start recovery.
                    if (store.Snapshot.Revision != copiedRevision || main.HasStartupError ||
                        File.Exists(Path.Combine(store.DataDirectory, "copy-recovery.json")))
                        throw new InvalidOperationException("Repeated paste must refuse an existing destination without changing metadata.");
                }
            }
            if (!directory)
            {
                var searchRevision = store.Snapshot.Revision;
                studio.FilterMode = SpaceFilterMode.Mapped;
                studio.SelectedTabIndex = 3;
                window.Activate();
                view.FindControl<Button>("WorkspaceSearchButton")!.Focus();
                window.KeyPress(Key.K, commandModifier, PhysicalKey.K, null);
                window.KeyRelease(Key.K, commandModifier, PhysicalKey.K, null);
                Dispatcher.UIThread.RunJobs();
                var globalInput = view.FindControl<TextBox>("WorkspaceSearchInput")!;
                globalInput.Text = Path.GetFileName(sourcePath).ToUpperInvariant();
                Dispatcher.UIThread.RunJobs();
                if (!studio.IsWorkspaceSearchTab || !globalInput.IsFocused ||
                    studio.WorkspaceSearchResults.Select(item => item.File.SpaceId).Distinct().Count() < 2)
                    throw new InvalidOperationException("Workspace search must find matching names across spaces regardless of sidebar filtering.");
                var searchHit = studio.WorkspaceSearchResults.Single(item => item.File.Id == fileId);
                studio.SelectedWorkspaceResult = searchHit;
                var revealResult = view.FindControl<Button>("RevealSearchResultButton")!;
                if (!revealResult.IsEffectivelyVisible || !revealResult.IsEnabled)
                    throw new InvalidOperationException("Workspace search must expose its location action.");
                revealResult.Command!.Execute(revealResult.CommandParameter);
                if (!studio.IsSpacesTab || studio.SelectedSpace?.Id != source.Id || studio.SelectedFile?.Id != fileId ||
                    store.Snapshot.Revision != searchRevision || File.ReadAllText(contentPath) != "clipboard fixture")
                    throw new InvalidOperationException("Search navigation must reveal the actual catalog item without changing metadata or files.");
                studio.WorkspaceSearchText = "absent-search-fixture";
                if (!studio.WorkspaceSearchHasNoResults || studio.SelectedWorkspaceResult is not null)
                    throw new InvalidOperationException("An empty search must not retain a stale action target.");

                int exitRequests = 0;
                var app = Application.Current!;
                int originalIcons = TrayIcon.GetIcons(app)?.Count ?? 0;
                using (var tray = new DesktopTray(app, window, main, () => exitRequests++))
                {
                    var entries = tray.Menu.Items.OfType<NativeMenuItem>().Where(item => item.Command is not null).ToArray();
                    window.WindowState = WindowState.Minimized;
                    entries[0].Command!.Execute(null);
                    Dispatcher.UIThread.RunJobs();
                    if (window.WindowState != WindowState.Normal || !window.IsVisible ||
                        entries[0].Header != main.Localizer["Spaces.ReturnToStudio"] ||
                        !entries[1].IsEnabled || entries[1].Header != main.Localizer["Capsule.Title"])
                        throw new InvalidOperationException("Tray actions must restore the existing workbench and use localized labels.");
                    entries[2].Command!.Execute(null);
                    if (exitRequests != 1) throw new InvalidOperationException("Tray exit must request application shutdown.");
                }
                if ((TrayIcon.GetIcons(app)?.Count ?? 0) != originalIcons)
                    throw new InvalidOperationException("Disposing the tray must release its application registration.");
            }
            // History remains usable without restarting the app or attempting another recovery.
            Guid inspectedId = store.Snapshot.Operations.First().Id;
            await main.UpdateStoreAsync(state => state with
            {
                Operations = state.Operations.Select(item => item.Id == inspectedId
                    ? item with { Status = ProposedOperationStatus.RecoveryRequired } : item).ToList()
            });
            string journalPath = Path.Combine(store.DataDirectory, "organization-recovery.json");
            string journalText = "{\"sourcePath\":" + System.Text.Json.JsonSerializer.Serialize(contentPath) +
                ",\"incomplete\":\"" + new string('x', 20_000);
            File.WriteAllText(journalPath, journalText);
            long inspectionRevision = store.Snapshot.Revision;
            string storedMetadata = File.ReadAllText(Path.Combine(store.DataDirectory, "workspace.json"));
            studio.SelectedTabIndex = 3;
            Dispatcher.UIThread.RunJobs();
            var history = view.FindControl<ListBox>("OperationsListBox")!;
            history.ScrollIntoView(studio.OperationHistory.Single(item => item.Id == inspectedId));
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var inspect = view.GetVisualDescendants().OfType<Button>().Single(button =>
                button.Name == "InspectOperationButton" &&
                button.DataContext is OperationItemViewModel item && item.Id == inspectedId);
            if (!inspect.IsEffectivelyVisible || !inspect.IsEnabled ||
                inspect.Command != studio.InspectOperationCommand)
                throw new InvalidOperationException("Failed history must expose its read-only evidence action.");
            await studio.InspectOperationCommand.ExecuteAsync(inspect.CommandParameter);
            if (!studio.IsPreviewDialogOpen || !studio.PreviewContent.Contains(inspectedId.ToString()) ||
                !studio.PreviewContent.Contains(journalPath) || studio.PreviewContent.Contains("clipboard fixture") ||
                !studio.PreviewContent.Contains(main.Localizer["Files.PreviewTruncatedNotice"]) ||
                studio.PreviewDetails != main.Localizer["Startup.RecoveryDetailsNotice"] ||
                studio.PreviewContent.Length > 25_000 || main.StartupState != StartupState.Ready ||
                store.Snapshot.Revision != inspectionRevision || File.ReadAllText(journalPath) != journalText ||
                File.ReadAllText(Path.Combine(store.DataDirectory, "workspace.json")) != storedMetadata ||
                File.ReadAllText(contentPath) != "clipboard fixture")
                throw new InvalidOperationException("History inspection must be bounded and never read linked content, recover or change files/metadata.");
            studio.ClosePreviewDialog();

            window.Close();
            if (floating.IsVisible || floating.DataContext is not null)
                throw new InvalidOperationException("Closing the workbench must close its space windows.");
            Console.WriteLine($"[MANUAL-WORKFLOW] {(directory ? "directory" : "file")}: complete");
        }
        finally
        {
            if (window.Clipboard is { } clipboard) await clipboard.ClearAsync();
            window.Close();
        }
    }

    private static void VerifyFileList(StudioView view, WorkspaceState state)
    {
        var space = state.Spaces.Single();
        var alpha = new WorkspaceFile(Guid.NewGuid(), space.Id, "Alpha.txt", Path.Combine(space.Folder, "Alpha.txt"), false);
        var zeta = new WorkspaceFile(Guid.NewGuid(), space.Id, "zeta.txt", Path.Combine(space.Folder, "zeta.txt"), false);
        var folder = new WorkspaceFile(Guid.NewGuid(), space.Id, "middle", Path.Combine(space.Folder, "middle"), true);
        var fixture = state with { Files = [zeta, alpha, folder] };
        var model = new StudioViewModel(fixture, _ => throw new InvalidOperationException("Filtering must not save workspace metadata."));
        var original = view.DataContext;
        try
        {
            view.DataContext = model;
            Dispatcher.UIThread.RunJobs();
            var search = view.FindControl<TextBox>("FileSearchInput")!;
            var descending = view.FindControl<Avalonia.Controls.Primitives.ToggleButton>("FilesDescending")!;
            if (!search.IsEffectivelyVisible || !model.VisibleFiles.Select(file => file.Id).SequenceEqual([folder.Id, alpha.Id, zeta.Id]))
                throw new InvalidOperationException("The visible file list must sort folders first, then names.");
            model.SelectFile(model.VisibleFiles.Single(file => file.Id == alpha.Id));
            descending.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            if (!model.VisibleFiles.Select(file => file.Id).SequenceEqual([folder.Id, zeta.Id, alpha.Id]) || model.SelectedFile?.Id != alpha.Id)
                throw new InvalidOperationException("Name order must preserve selection by ID and keep folders first.");
            search.Text = "ALP";
            Dispatcher.UIThread.RunJobs();
            model.RefreshFromState(fixture);
            if (model.VisibleFiles.Count != 1 || model.SelectedFile?.Id != alpha.Id || model.FileSearchText != "ALP")
                throw new InvalidOperationException("Name filtering must be case-insensitive and survive snapshot refresh.");
            search.Text = "not-found";
            Dispatcher.UIThread.RunJobs();
            if (!model.NoMatchingFiles || model.SelectedFile is not null || model.VisibleFiles.Count != 0)
                throw new InvalidOperationException("A hidden file must not remain the active action target.");
            model.SelectFile(model.SelectedSpace!.Files.Single(file => file.Id == alpha.Id));
            if (model.FileSearchText.Length != 0 || model.VisibleFiles.Count != 3 || model.SelectedFile?.Id != alpha.Id)
                throw new InvalidOperationException("An explicit floating-window selection must reveal its workbench target.");
        }
        finally { view.DataContext = original; Dispatcher.UIThread.RunJobs(); }
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
}
