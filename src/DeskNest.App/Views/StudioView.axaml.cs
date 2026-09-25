using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using DeskNest.App.ViewModels;

namespace DeskNest.App.Views;

public partial class StudioView : UserControl
{
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
