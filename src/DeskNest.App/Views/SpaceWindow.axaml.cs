using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeskNest.App.ViewModels;

namespace DeskNest.App.Views;

/// <summary>A floating view of one existing space. Borrows the workbench and closes with it.</summary>
public partial class SpaceWindow : Window
{
    public static readonly StyledProperty<SpaceItemViewModel?> SpaceProperty =
        AvaloniaProperty.Register<SpaceWindow, SpaceItemViewModel?>(nameof(Space));
    public SpaceItemViewModel? Space { get => GetValue(SpaceProperty); private set => SetValue(SpaceProperty, value); }
    private StudioViewModel? _studio;
    private Window? _workbench;
    private Guid _spaceId;
    private bool _refreshQueued, _closed;

    public SpaceWindow()
    {
        InitializeComponent();
        Closed += OnClosed;
        var drop = this.FindControl<Border>("SpaceWindowDropSurface")!;
        drop.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        drop.AddHandler(DragDrop.DropEvent, OnDrop);
    }

    public SpaceWindow(StudioViewModel studio, Guid spaceId, Window workbench) : this()
    {
        _studio = studio;
        _spaceId = spaceId;
        _workbench = workbench;
        DataContext = studio;
        RefreshSpace();
        studio.AllSpaces.CollectionChanged += OnSpacesChanged;
        workbench.Closed += OnWorkbenchClosed;
    }

    private void OnSpacesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // A snapshot clears and repopulates AllSpaces; update once after that batch, not on the temporary empty list.
        if (_closed || _refreshQueued) return;
        _refreshQueued = true;
        Dispatcher.UIThread.Post(() => { _refreshQueued = false; if (!_closed) RefreshSpace(); });
    }

    private void RefreshSpace()
    {
        var list = this.FindControl<ListBox>("SpaceWindowFiles")!;
        var selectedId = (list.SelectedItem as WorkspaceFileItemViewModel)?.Id;
        Space = _studio?.AllSpaces.FirstOrDefault(s => s.Id == _spaceId);
        if (Space is null) { Close(); return; }
        Title = Space.Name + " · DeskNext";
        list.SelectedItem = Space.Files.FirstOrDefault(f => f.Id == selectedId);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        if (_studio is not null) _studio.AllSpaces.CollectionChanged -= OnSpacesChanged;
        if (_workbench is not null) _workbench.Closed -= OnWorkbenchClosed;
        _studio = null;
        _workbench = null;
        DataContext = null;
        Space = null;
    }

    private void OnWorkbenchClosed(object? sender, EventArgs e) => Close();
    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
    private void OnReturnClick(object? sender, RoutedEventArgs e)
    {
        if (_workbench is null || Space is null || _studio is null) return;
        _studio.SelectSpace(Space);
        _studio.SelectedTabIndex = 0;
        if (_workbench.WindowState == WindowState.Minimized) _workbench.WindowState = WindowState.Normal;
        _workbench.Show();
        _workbench.Activate();
    }

    private void OnTitlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual v && (v is Button || v.FindAncestorOfType<Button>() is not null)) return;
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    private void OnResizePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginResizeDrag(FlowDirection == Avalonia.Media.FlowDirection.RightToLeft ? WindowEdge.SouthWest : WindowEdge.SouthEast, e);
    }

    private async void OnFileDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_studio is null || sender is not Control { DataContext: WorkspaceFileItemViewModel file }) return;
        e.Handled = true;
        this.FindControl<ListBox>("SpaceWindowFiles")!.SelectedItem = file;
        await _studio.ExecuteOpenFileCommand.ExecuteAsync(file);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = StudioView.HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_studio is null || Space is null) return;
        var studio = _studio;
        studio.SelectSpace(Space);
        if (StudioView.HasFiles(e)) await studio.DropPathsOnSpaceAsync(StudioView.ExtractPaths(e));
        else studio.SpaceDropNotice = studio.Localizer["Drop.UnsupportedPayload"];
    }
}
