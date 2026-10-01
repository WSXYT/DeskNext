using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DeskNest.App.ViewModels;

namespace DeskNest.App.Views;

public partial class DropCapsuleWindow : Window
{
    public DropCapsuleWindow() : this(new DropCapsuleViewModel(), ownsViewModel: true)
    {
    }

    public DropCapsuleWindow(StudioViewModel studioViewModel) : this(new DropCapsuleViewModel(studioViewModel), ownsViewModel: true)
    {
    }

    public DropCapsuleWindow(DropCapsuleViewModel viewModel, bool ownsViewModel = false)
    {
        InitializeComponent();
        DataContext = viewModel;
        Closed += (_, _) => { if (ownsViewModel) viewModel.Dispose(); };

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

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            if (e.Source is Visual visual && (visual is Button || visual.FindAncestorOfType<Button>() != null))
            {
                return;
            }
            BeginMoveDrag(e);
        }
    }

    private void OnCloseButtonClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnCapsuleDragEnter(object? sender, DragEventArgs e)
    {
        OnCapsuleDragOver(sender, e);
    }

    private void OnCapsuleDragOver(object? sender, DragEventArgs e)
    {
        if (DataContext is DropCapsuleViewModel vm)
        {
            if (StudioView.HasFiles(e))
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
        if (DataContext is DropCapsuleViewModel vm)
        {
            vm.IsDragOverCapsule = false;
        }
    }

    private async void OnCapsuleDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is DropCapsuleViewModel vm)
        {
            vm.IsDragOverCapsule = false;
            if (!StudioView.HasFiles(e))
            {
                vm.CapsuleNotice = vm.Localizer["Drop.UnsupportedPayload"];
            }
            else
            {
                var paths = StudioView.ExtractPaths(e);
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
