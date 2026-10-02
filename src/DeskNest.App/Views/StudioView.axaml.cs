using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using DeskNest.App.ViewModels;

namespace DeskNest.App.Views;

public partial class StudioView : UserControl
{
    private readonly Dictionary<Guid, SpaceWindow> _spaceWindows = new();

    private void OnOpenSpaceWindowClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => OpenSelectedSpaceWindow();

    internal SpaceWindow? OpenSelectedSpaceWindow()
    {
        if (DataContext is not StudioViewModel studio || studio.SelectedSpace is not { } space ||
            TopLevel.GetTopLevel(this) is not Window workbench) return null;
        Guid spaceId = space.Id;
        if (_spaceWindows.TryGetValue(spaceId, out var existing)) { existing.Activate(); return existing; }
        int offset = (_spaceWindows.Count % 6) * 24;
        var window = new SpaceWindow(studio, spaceId, workbench)
        {
            Position = workbench.Position + new Avalonia.PixelPoint(40 + offset, 60 + offset)
        };
        window.RestorePlacement();
        _spaceWindows.Add(spaceId, window);
        window.Closed += (_, _) => _spaceWindows.Remove(spaceId);
        window.Show();
        return window;
    }

    private async void OnWorkspaceKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not StudioViewModel studio || !studio.IsSpacesTab ||
            studio.IsAddSpaceDialogOpen || studio.IsRenameDialogOpen ||
            studio.IsDeleteConfirmationDialogOpen || studio.IsPreviewDialogOpen || studio.IsImportConfirmationOpen ||
            e.Source is TextBox || (e.Source as Avalonia.Visual)?.FindAncestorOfType<TextBox>() is not null)
            return;

        var commandModifier = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        IAsyncRelayCommand? command = (e.Key, e.KeyModifiers) switch
        {
            (Key.F2, KeyModifiers.None) when studio.CanRenameFile => studio.ExecuteRenameFileCommand,
            (Key.Delete, KeyModifiers.None) when studio.CanDeleteFile => studio.ExecuteDeleteFileCommand,
            (Key.X, var modifiers) when modifiers == commandModifier && studio.CanCutFile => studio.ExecuteCutFileCommand,
            (Key.V, var modifiers) when modifiers == commandModifier && studio.CanPasteFile => studio.ExecutePasteFileCommand,
            _ => null
        };
        object? parameter = e.Key == Key.V ? studio.SelectedSpace : studio.SelectedFile;
        if (command is null || !command.CanExecute(parameter)) return;
        e.Handled = true;
        await command.ExecuteAsync(parameter);
        if (e.Key == Key.F2 && studio.IsRenameDialogOpen)
        {
            var input = this.FindControl<TextBox>("RenameNameInput")!;
            input.Focus();
            input.SelectAll();
        }
        else if (e.Key == Key.Delete && studio.IsDeleteConfirmationDialogOpen)
            this.FindControl<Button>("DeleteConfirmationCancelButton")?.Focus();
    }

    public StudioView()
    {
        InitializeComponent();

        var spaceSurface = this.FindControl<Border>("SpaceDetailSurface");
        if (spaceSurface != null)
        {
            DragDrop.SetAllowDrop(spaceSurface, true);
            spaceSurface.AddHandler(DragDrop.DragEnterEvent, OnSpaceSurfaceDragEnter);
            spaceSurface.AddHandler(DragDrop.DragOverEvent, OnSpaceSurfaceDragOver);
            spaceSurface.AddHandler(DragDrop.DragLeaveEvent, OnSpaceSurfaceDragLeave);
            spaceSurface.AddHandler(DragDrop.DropEvent, OnSpaceSurfaceDrop);
        }

        var capsuleBorder = this.FindControl<Border>("DropCapsuleBorder");
        if (capsuleBorder != null)
        {
            DragDrop.SetAllowDrop(capsuleBorder, true);
            capsuleBorder.AddHandler(DragDrop.DragEnterEvent, OnCapsuleDragEnter);
            capsuleBorder.AddHandler(DragDrop.DragOverEvent, OnCapsuleDragOver);
            capsuleBorder.AddHandler(DragDrop.DragLeaveEvent, OnCapsuleDragLeave);
            capsuleBorder.AddHandler(DragDrop.DropEvent, OnCapsuleDrop);
        }
    }

    public static bool HasFiles(DragEventArgs e)
    {
        if (e.DataTransfer == null)
            return false;

        if (e.DataTransfer.Contains(DataFormat.File))
            return true;

        var files = e.DataTransfer.TryGetFiles();
        if (files != null && files.Any())
            return true;

        foreach (var format in e.DataTransfer.Formats)
        {
            if (string.Equals(format.Identifier, "Files", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(format.Identifier, "FileNames", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(format.Identifier, "File", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static List<string> ExtractPaths(DragEventArgs e)
    {
        var paths = new List<string>();
        if (e.DataTransfer == null)
            return paths;

        var storageItems = e.DataTransfer.TryGetFiles();
        if (storageItems != null)
        {
            foreach (var item in storageItems)
            {
                string? localPath = item.TryGetLocalPath();
                if (string.IsNullOrWhiteSpace(localPath) && item.Path != null)
                {
                    if (item.Path.IsFile || item.Path.IsAbsoluteUri)
                        localPath = item.Path.LocalPath;
                }

                if (!string.IsNullOrWhiteSpace(localPath))
                {
                    var full = Path.GetFullPath(localPath.Trim());
                    if (!paths.Contains(full))
                        paths.Add(full);
                }
            }
        }

        if (paths.Count == 0)
        {
            var text = e.DataTransfer.TryGetText();
            if (!string.IsNullOrWhiteSpace(text))
            {
                var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (Path.IsPathFullyQualified(trimmed) && (File.Exists(trimmed) || Directory.Exists(trimmed)))
                    {
                        var full = Path.GetFullPath(trimmed);
                        if (!paths.Contains(full))
                            paths.Add(full);
                    }
                }
            }
        }

        return paths;
    }

    internal void ApplyModelFolderSelection(string? path)
    {
        if (DataContext is not StudioViewModel vm || !vm.IsSettingsTab || path is null) return;
        path = DeskNest.Platform.PlatformFileActions.RequireExistingLocalPath(path);
        if (!Directory.Exists(path) || !File.Exists(Path.Combine(path, "manifest.json")))
            throw new InvalidDataException(vm.Localizer["Classification.Setup"]);
        // Only a settings draft. The worker still verifies the bundle before inference.
        vm.SettingsModelCache = path;
        vm.SettingsSavedFeedback = null;
    }

    private async void OnBrowseModelFolderClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not StudioViewModel vm || sender is not Button button) return;
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage?.CanPickFolder != true)
        {
            vm.SettingsSavedFeedback = vm.Localizer["Spaces.FolderPickerUnavailable"];
            return;
        }
        button.IsEnabled = false;
        try
        {
            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
                { Title = vm.Localizer["OOBE.Step2.ModelCache"], AllowMultiple = false });
            using var folder = folders.FirstOrDefault();
            if (folder is null || DataContext != vm || !vm.IsSettingsTab) return;
            ApplyModelFolderSelection(folder.TryGetLocalPath()
                ?? throw new InvalidDataException(vm.Localizer["Validation.ValidAbsolutePathRequired"]));
        }
        catch (Exception error) { vm.SettingsSavedFeedback = vm.Localizer.GetString("Files.ActionFailedNotice", error.Message); }
        finally { button.IsEnabled = true; }
    }

    private async void OnBrowseSpaceFolderClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not StudioViewModel vm || !vm.IsNewSpaceMapped || sender is not Button button) return;
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage?.CanPickFolder != true)
        {
            vm.SpaceDialogError = vm.Localizer["Spaces.FolderPickerUnavailable"];
            return;
        }

        button.IsEnabled = false;
        try
        {
            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = vm.Localizer["Spaces.DialogFolder"], AllowMultiple = false
            });
            using var folder = folders.FirstOrDefault();
            if (folder is null || !vm.IsAddSpaceDialogOpen || !vm.IsNewSpaceMapped || DataContext != vm) return;
            string? path = folder.TryGetLocalPath();
            if (path is null)
            {
                vm.SpaceDialogError = vm.Localizer["Validation.ValidAbsolutePathRequired"];
                return;
            }
            vm.NewSpaceFolder = DeskNest.Platform.PlatformFileActions.RequireExistingLocalPath(path);
            if (string.IsNullOrWhiteSpace(vm.NewSpaceName)) vm.NewSpaceName = folder.Name;
            vm.SpaceDialogError = null;
        }
        catch (Exception error)
        {
            vm.SpaceDialogError = vm.Localizer.GetString("Files.ActionFailedNotice", error.Message);
        }
        finally { button.IsEnabled = true; }
    }

    private async void OnFileDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not StudioViewModel studio ||
            sender is not Control { DataContext: WorkspaceFileItemViewModel file }) return;
        e.Handled = true;
        studio.SelectFile(file);
        if (studio.ExecuteOpenFileCommand.CanExecute(file))
            await studio.ExecuteOpenFileCommand.ExecuteAsync(file);
    }

    private void OnFileMenuOpened(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        var moveMenu = menu.Items.OfType<MenuItem>().Single(item => item.Name == "MoveToSpaceMenu");
        moveMenu.ItemsSource = null;
        moveMenu.IsEnabled = false;
        if (DataContext is not StudioViewModel studio || menu.DataContext is not WorkspaceFileItemViewModel file) return;
        studio.SelectFile(file); // File actions apply to the row whose menu was opened.
        if (file.IsInTrash || !studio.CanExecuteManualMove) return;

        // Populate only the opened menu, not every file row. Capture the clicked row's ID,
        // which can differ from the current selection when opening a context menu.
        var targets = studio.AllSpaces.Where(space => space.Id != file.SpaceId).Select(space =>
        {
            var item = new MenuItem
            {
                Header = space.Name,
                Command = studio.ExecuteManualMoveCommand,
                CommandParameter = (file.Id, space.Id)
            };
            ToolTip.SetTip(item, new TextBlock
            {
                Text = $"{space.ModeLocalized}\n{space.Folder}",
                FlowDirection = Avalonia.Media.FlowDirection.LeftToRight
            });
            return item;
        }).ToArray();
        moveMenu.ItemsSource = targets;
        moveMenu.IsEnabled = targets.Length > 0;
    }

    private void OnSpaceSurfaceDragEnter(object? sender, DragEventArgs e)
    {
        OnSpaceSurfaceDragOver(sender, e);
    }

    private void OnSpaceSurfaceDragOver(object? sender, DragEventArgs e)
    {
        if (DataContext is StudioViewModel vm)
        {
            if (HasFiles(e))
            {
                e.DragEffects = DragDropEffects.Copy;
                vm.IsDragOverSpaceSurface = true;
            }
            else
            {
                e.DragEffects = DragDropEffects.None;
                vm.IsDragOverSpaceSurface = false;
                vm.SpaceDropNotice = vm.Localizer["Drop.UnsupportedPayload"];
            }
        }
        e.Handled = true;
    }

    private void OnSpaceSurfaceDragLeave(object? sender, DragEventArgs e)
    {
        if (DataContext is StudioViewModel vm)
        {
            vm.IsDragOverSpaceSurface = false;
        }
    }

    private async void OnSpaceSurfaceDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is StudioViewModel vm)
        {
            vm.IsDragOverSpaceSurface = false;
            if (!HasFiles(e))
            {
                vm.SpaceDropNotice = vm.Localizer["Drop.UnsupportedPayload"];
            }
            else
            {
                var paths = ExtractPaths(e);
                if (paths.Count > 0)
                {
                    await vm.DropPathsOnSpaceAsync(paths);
                }
                else
                {
                    vm.SpaceDropNotice = vm.Localizer["Drop.UnsupportedPayload"];
                }
            }
        }
        e.Handled = true;
    }

    private void OnCapsuleDragEnter(object? sender, DragEventArgs e)
    {
        OnCapsuleDragOver(sender, e);
    }

    private void OnCapsuleDragOver(object? sender, DragEventArgs e)
    {
        if (DataContext is StudioViewModel vm)
        {
            if (HasFiles(e))
            {
                e.DragEffects = DragDropEffects.Copy;
                vm.IsDragOverCapsule = true;
            }
            else
            {
                e.DragEffects = DragDropEffects.None;
                vm.IsDragOverCapsule = false;
                vm.CapsuleNotice = vm.Localizer["Drop.UnsupportedPayload"];
            }
        }
        e.Handled = true;
    }

    private void OnCapsuleDragLeave(object? sender, DragEventArgs e)
    {
        if (DataContext is StudioViewModel vm)
        {
            vm.IsDragOverCapsule = false;
        }
    }

    private async void OnCapsuleDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is StudioViewModel vm)
        {
            vm.IsDragOverCapsule = false;
            if (!HasFiles(e))
            {
                vm.CapsuleNotice = vm.Localizer["Drop.UnsupportedPayload"];
            }
            else
            {
                var paths = ExtractPaths(e);
                if (paths.Count > 0)
                {
                    await vm.DropPathsOnCapsuleAsync(paths);
                }
                else
                {
                    vm.CapsuleNotice = vm.Localizer["Drop.UnsupportedPayload"];
                }
            }
        }
        e.Handled = true;
    }
}
