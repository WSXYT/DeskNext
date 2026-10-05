using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DeskNest.App.ViewModels;
using DeskNest.Core.Workspace;

namespace DeskNest.App.Views;

public partial class SpaceWindow
{
    public static readonly StyledProperty<bool> IsDetailsViewProperty = AvaloniaProperty.Register<SpaceWindow, bool>(nameof(IsDetailsView));
    public bool IsDetailsView { get => GetValue(IsDetailsViewProperty); private set => SetValue(IsDetailsViewProperty, value); }
    private const int GridPageSize = 96; // Plain WrapPanel only realizes one bounded page; list/details remain virtualized.
    private int _gridPage;

    private void RefreshFileView(Guid? selected = null)
    {
        if (Space is null) return;
        bool grid = Space.FileView == SpaceFileView.Grid;
        IsDetailsView = Space.FileView == SpaceFileView.Details;
        int pages = Math.Max(1, (Space.Files.Count + GridPageSize - 1) / GridPageSize);
        if (grid && selected.HasValue && Space.Files.ToList().FindIndex(f => f.Id == selected) is var index && index >= 0)
            _gridPage = index / GridPageSize;
        _gridPage = Math.Clamp(_gridPage, 0, pages - 1);
        var list = this.FindControl<ListBox>("SpaceWindowFiles")!;
        list.ItemsPanel = (ITemplate<Panel?>)Resources[grid ? "GridPanel" : "ListPanel"]!;
        list.ItemTemplate = (IDataTemplate)Resources[grid ? "FileGridTemplate" : "FileListTemplate"]!;
        list.ItemsSource = grid ? Space.Files.Skip(_gridPage * GridPageSize).Take(GridPageSize).ToArray() : Space.Files;
        list.SelectedItem = Space.Files.FirstOrDefault(f => f.Id == selected);
        this.FindControl<StackPanel>("GridPageControls")!.IsVisible = grid && pages > 1;
        this.FindControl<TextBlock>("GridPageLabel")!.Text = _studio?.Localizer.GetString("SpaceView.Page", _gridPage + 1, pages);
        this.FindControl<Button>("PreviousGridPage")!.IsEnabled = _gridPage > 0;
        this.FindControl<Button>("NextGridPage")!.IsEnabled = _gridPage < pages - 1;
    }

    private void OnPreviousPage(object? sender, RoutedEventArgs e) { _gridPage--; RefreshFileView(); }
    private void OnNextPage(object? sender, RoutedEventArgs e) { _gridPage++; RefreshFileView(); }

    internal async Task SetFileViewAsync(SpaceFileView view)
    {
        if (_studio is null || Space is null) return;
        try { await _studio.SaveFileViewAsync(_spaceId, view); if (!_closed) RefreshSpace(); }
        catch (Exception error) { _studio.FileActionNotice = _studio.Localizer.GetString("Files.ActionFailedNotice", error.Message); }
    }

    private void OnViewOptionsClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || _studio is null || Space is null) return;
        var menu = new ContextMenu();
        foreach (SpaceFileView mode in Enum.GetValues<SpaceFileView>())
        {
            var item = new MenuItem { Header = _studio.Localizer["SpaceView." + mode], ToggleType = MenuItemToggleType.Radio, IsChecked = Space.FileView == mode };
            item.Click += async (_, _) => await SetFileViewAsync(mode);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var change = new MenuItem { Header = _studio.Localizer["Icons.Change"] };
        ToolTip.SetTip(change, _studio.Localizer["Icons.LocalOnly"]);
        change.Click += async (_, _) => await PickDisplayIconAsync(_spaceId, true);
        menu.Items.Add(change);
        var reset = new MenuItem { Header = _studio.Localizer["Icons.Reset"], IsEnabled = Space.CustomIconPath is not null };
        reset.Click += async (_, _) => await ApplyIconSelectionAsync(_spaceId, true, null);
        menu.Items.Add(reset);
        button.ContextMenu = menu;
        menu.Open(button);
    }

    private async Task PickDisplayIconAsync(Guid id, bool space)
    {
        if (_studio is not { } studio) return;
        try
        {
            if (!StorageProvider.CanOpen) { studio.FileActionNotice = studio.Localizer["Spaces.FolderPickerUnavailable"]; return; }
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = studio.Localizer["Icons.Change"], AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType(studio.Localizer["Icons.Images"]) { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.ico"] }]
            });
            try
            {
                if (files.Count != 0 && files[0].TryGetLocalPath() is { } path && !_closed)
                    await ApplyIconSelectionAsync(id, space, path);
            }
            finally { foreach (var file in files) file.Dispose(); }
        }
        catch (Exception error) { studio.FileActionNotice = studio.Localizer.GetString("Files.ActionFailedNotice", error.Message); }
    }

    internal async Task ApplyIconSelectionAsync(Guid id, bool space, string? path)
    {
        if (_studio is not { } studio || _closed) return;
        try
        {
            if (path is not null) await Task.Run(() => { using var decoded = FileIcon.LoadCustom(path); });
            if (!_closed) await studio.SaveDisplayIconAsync(id, space, path);
        }
        catch (Exception error) { studio.FileActionNotice = studio.Localizer.GetString("Files.ActionFailedNotice", error.Message); }
    }
}
