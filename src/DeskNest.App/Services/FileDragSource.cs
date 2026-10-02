using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DeskNest.App.ViewModels;
using DeskNest.Platform;

namespace DeskNest.App.Services;

/// <summary>
/// Exports one existing file reference to the receiving application. Never moves/deletes the
/// source, invokes Core copy, or claims an undo receipt for work done by another application.
/// </summary>
internal static class FileDragSource
{
    internal const DragDropEffects AllowedEffects = DragDropEffects.Copy;

    internal static void Attach(ListBox list, Func<StudioViewModel?> getStudio)
    {
        (PointerPressedEventArgs Args, Point Origin, Guid Id, Guid SpaceId, string Path)? pending = null;
        IPointer? downPointer = null;
        bool busy = false;
        list.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            pending = null;
            if (busy || e.Pointer.Type != PointerType.Mouse || e.ClickCount != 1 ||
                !e.GetCurrentPoint(list).Properties.IsLeftButtonPressed ||
                e.Source is not Control { DataContext: WorkspaceFileItemViewModel { IsInTrash: false } file }) return;
            downPointer = e.Pointer;
            pending = (e, e.GetPosition(list), file.Id, file.SpaceId, file.Path);
        }, RoutingStrategies.Tunnel);
        list.AddHandler(InputElement.PointerReleasedEvent, (_, _) =>
        {
            pending = null;
            downPointer = null;
        }, RoutingStrategies.Tunnel);
        list.AddHandler(InputElement.PointerCaptureLostEvent, (_, _) =>
        {
            pending = null;
            downPointer = null;
        });
        list.AddHandler(InputElement.PointerMovedEvent, async (_, e) =>
        {
            if (busy || pending is not { } pressed || e.Pointer != downPointer) return;
            if (!e.GetCurrentPoint(list).Properties.IsLeftButtonPressed) { pending = null; return; }
            var delta = e.GetPosition(list) - pressed.Origin;
            if (Math.Abs(delta.X) < 6 && Math.Abs(delta.Y) < 6) return;
            pending = null;
            if (getStudio() is not { } studio || TopLevel.GetTopLevel(list) is not { } top) return;
            busy = true;
            e.Handled = true;
            try
            {
                using var item = await ResolveItemAsync(top, studio, pressed.Id, pressed.SpaceId, pressed.Path);
                if (downPointer != pressed.Args.Pointer || !list.IsEffectivelyVisible) return;
                // Ownership of the transfer passes to Avalonia; do not dispose it here.
                await DragDrop.DoDragDropAsync(pressed.Args, CreateTransfer(item), AllowedEffects);
                // Even an unexpected returned Move never authorizes source deletion.
            }
            catch (Exception error)
            {
                studio.FileActionNotice = studio.Localizer.GetString("Files.ActionFailedNotice", error.Message);
            }
            finally { busy = false; downPointer = null; }
        }, RoutingStrategies.Tunnel);
    }

    internal static DataTransfer CreateTransfer(IStorageItem item)
    {
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.CreateFile(item));
        return transfer;
    }

    internal static async Task<IStorageItem> ResolveItemAsync(TopLevel top, StudioViewModel studio,
        Guid fileId, Guid spaceId, string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        WorkspaceFileItemViewModel? Current() => studio.AllSpaces.SelectMany(s => s.Files).FirstOrDefault(f =>
            f.Id == fileId && f.SpaceId == spaceId && !f.IsInTrash && string.Equals(f.Path, path, comparison));
        var file = Current() ?? throw new IOException(studio.Localizer["Validation.FileNotFound"]);
        string fullPath = PlatformFileActions.RequireExistingLocalPath(path);
        IStorageItem? item = file.IsDirectory
            ? await top.StorageProvider.TryGetFolderFromPathAsync(new Uri(fullPath))
            : await top.StorageProvider.TryGetFileFromPathAsync(new Uri(fullPath));
        try
        {
            if (item is null || Current() is null)
                throw new IOException(studio.Localizer["Validation.FileNotFound"]);
            PlatformFileActions.RequireExistingLocalPath(fullPath);
            return item;
        }
        catch { item?.Dispose(); throw; }
    }
}
