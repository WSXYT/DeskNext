using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeskNest.App.ViewModels;
using DeskNest.Core.Workspace;

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
    private bool _refreshQueued, _closed, _savingLayout;

    public SpaceWindow()
    {
        InitializeComponent();
        Services.FileDragSource.Attach(this.FindControl<ListBox>("SpaceWindowFiles")!, () => _studio);
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
        RefreshFileView(selectedId);
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

    internal void RestorePlacement()
    {
        if (Space?.WindowPlacement is not { } placement) return;
        var screen = Screens.ScreenFromPoint(new PixelPoint(placement.X, placement.Y)) ??
            (_workbench is null ? null : Screens.ScreenFromWindow(_workbench)) ?? Screens.Primary;
        Width = Math.Clamp(placement.Width, MinWidth, 32768);
        Height = Math.Clamp(placement.Height, MinHeight, 32768);
        if (screen is null) return; // No screen geometry: retain the launcher's visible default position.
        var area = screen.WorkingArea;
        double scale = screen.Scaling;
        Width = Math.Min(Width, Math.Max(MinWidth, area.Width / scale));
        Height = Math.Min(Height, Math.Max(MinHeight, area.Height / scale));
        Position = new PixelPoint(
            (int)Math.Clamp((double)placement.X, area.X, Math.Max((double)area.X, (double)area.Right - Width * scale)),
            (int)Math.Clamp((double)placement.Y, area.Y, Math.Max((double)area.Y, (double)area.Bottom - Height * scale)));
    }

    private async void OnSaveLayoutClick(object? sender, RoutedEventArgs e) => await SavePlacementAsync();

    internal async Task SavePlacementAsync()
    {
        if (_closed || _savingLayout || _studio is null || Space is null || WindowState != WindowState.Normal) return;
        var studio = _studio;
        var placement = new SpaceWindowPlacement(Position.X, Position.Y, Bounds.Width, Bounds.Height);
        var button = this.FindControl<Button>("SaveSpaceLayoutButton")!;
        _savingLayout = true;
        button.IsEnabled = false;
        try
        {
            await studio.SaveSpaceWindowPlacementAsync(_spaceId, placement);
            if (!_closed) studio.FileActionNotice = studio.Localizer["Spaces.LayoutSaved"];
        }
        catch (Exception error)
        {
            if (!_closed) studio.FileActionNotice = studio.Localizer.GetString("Files.ActionFailedNotice", error.Message);
        }
        finally
        {
            _savingLayout = false;
            if (!_closed) button.IsEnabled = true;
        }
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

    private void OnFileMenuOpened(object? sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        menu.Items.Clear();
        if (_studio is not { } studio || Space is not { } space ||
            menu.DataContext is not WorkspaceFileItemViewModel selected ||
            space.Files.FirstOrDefault(f => f.Id == selected.Id && !f.IsInTrash) is not { } file) return;
        studio.SelectSpace(space);
        studio.SelectFile(file);
        this.FindControl<ListBox>("SpaceWindowFiles")!.SelectedItem = file;

        // Reuse the workbench executors. Dialogs stay on its existing confirmation/preview surface.
        MenuItem Action(string key, System.Windows.Input.ICommand command, object parameter, bool enabled, bool showWorkbench = false)
        {
            var item = new MenuItem { Header = studio.Localizer[key], Command = command, CommandParameter = parameter, IsEnabled = enabled };
            if (showWorkbench) item.Click += (_, args) => OnReturnClick(null, args);
            return item;
        }
        menu.Items.Add(Action("Files.ActionOpen", studio.ExecuteOpenFileCommand, file, studio.CanOpenFile));
        menu.Items.Add(Action("Files.ActionReveal", studio.ExecuteRevealFileCommand, file, studio.CanRevealFile));
        menu.Items.Add(Action("Files.ActionPreview", studio.ExecutePreviewFileCommand, file, studio.CanPreviewFile, showWorkbench: true));
        menu.Items.Add(new Separator());
        menu.Items.Add(Action("Files.ActionCopy", studio.ExecuteCopyFileCommand, file, studio.CanCopyFile));
        menu.Items.Add(Action("Files.ActionCut", studio.ExecuteCutFileCommand, file, studio.CanCutFile));
        menu.Items.Add(Action("Files.ActionPaste", studio.ExecutePasteFileCommand, space, studio.CanPasteFile));
        var move = new MenuItem { Header = studio.Localizer["Files.ActionMoveToSpace"], IsEnabled = studio.CanExecuteManualMove };
        foreach (var target in studio.AllSpaces.Where(s => s.Id != space.Id))
            move.Items.Add(new MenuItem { Header = target.Name, Command = studio.ExecuteManualMoveCommand, CommandParameter = (file.Id, target.Id) });
        move.IsEnabled &= move.Items.Count > 0;
        menu.Items.Add(move);
        if (studio.FindUndoForFile(file) is { } undo)
        {
            var undoItem = Action("Operations.UndoAction", studio.ExecuteUndoManualMoveCommand, undo.Id, true);
            undoItem.Name = "UndoFileMenu";
            ToolTip.SetTip(undoItem, new TextBlock { Text = undo.SourcePath, FlowDirection = Avalonia.Media.FlowDirection.LeftToRight });
            menu.Items.Add(undoItem);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Action("Files.ActionRename", studio.ExecuteRenameFileCommand, file, studio.CanRenameFile, showWorkbench: true));
        menu.Items.Add(Action(file.DeleteActionKey, studio.ExecuteDeleteFileCommand, file, studio.CanDeleteFile, showWorkbench: true));
        menu.Items.Add(new Separator());
        var changeIcon = new MenuItem { Header = studio.Localizer["Icons.Change"] };
        ToolTip.SetTip(changeIcon, studio.Localizer["Icons.LocalOnly"]);
        changeIcon.Click += async (_, _) => await PickDisplayIconAsync(file.Id, false);
        menu.Items.Add(changeIcon);
        var resetIcon = new MenuItem { Header = studio.Localizer["Icons.Reset"], IsEnabled = file.CustomIconPath is not null };
        resetIcon.Click += async (_, _) => await ApplyIconSelectionAsync(file.Id, false, null);
        menu.Items.Add(resetIcon);
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
        if (StudioView.HasFiles(e))
        {
            await studio.DropPathsOnSpaceAsync(StudioView.ExtractPaths(e));
            if (studio.IsImportConfirmationOpen)
            {
                OnReturnClick(sender, e);
                studio.SelectedTabIndex = 1; // The borrowed workbench owns the confirmation and pending review.
            }
        }
        else studio.SpaceDropNotice = studio.Localizer["Drop.UnsupportedPayload"];
    }
}
