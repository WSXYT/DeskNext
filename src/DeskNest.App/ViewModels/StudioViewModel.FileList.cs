using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DeskNest.App.ViewModels;

public sealed partial class StudioViewModel
{
    [ObservableProperty] private string _fileSearchText = string.Empty;
    [ObservableProperty] private bool _sortFilesDescending;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoMatchingFiles))]
    private IReadOnlyList<WorkspaceFileItemViewModel> _visibleFiles = [];
    public bool NoMatchingFiles => SelectedSpace?.HasFiles == true && VisibleFiles.Count == 0;

    partial void OnFileSearchTextChanged(string value) => RefreshVisibleFiles();
    partial void OnSortFilesDescendingChanged(bool value) => RefreshVisibleFiles();
    private Guid? _fileSearchSpaceId;
    partial void OnSelectedSpaceChanged(SpaceItemViewModel? oldValue, SpaceItemViewModel? newValue)
    {
        // Snapshot refresh briefly clears ListBox selection; that isn't a different space.
        if (newValue is not null && _fileSearchSpaceId != newValue.Id)
        {
            _fileSearchSpaceId = newValue.Id;
            FileSearchText = string.Empty;
        }
        RefreshVisibleFiles();
    }

    // A view over catalog metadata, not a disk scan or a workspace mutation.
    private void RefreshVisibleFiles()
    {
        var selectedId = SelectedFile?.Id;
        string query = FileSearchText.Trim();
        var files = (SelectedSpace?.Files.AsEnumerable() ?? [])
            .Where(file => query.Length == 0 || file.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => !file.IsDirectory);
        var names = StringComparer.Create(CultureInfo.GetCultureInfo(Localizer.CurrentLanguage), ignoreCase: true);
        VisibleFiles = (SortFilesDescending ? files.ThenByDescending(file => file.Name, names) : files.ThenBy(file => file.Name, names))
            .ThenBy(file => file.Id).ToArray();
        SelectFile(VisibleFiles.FirstOrDefault(file => file.Id == selectedId) ?? VisibleFiles.FirstOrDefault());
        OnPropertyChanged(nameof(NoMatchingFiles));
    }
}
