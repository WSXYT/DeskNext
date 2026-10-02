using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DeskNest.App.ViewModels;

public sealed record WorkspaceSearchResult(WorkspaceFileItemViewModel File, string SpaceName);

public sealed partial class StudioViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WorkspaceSearchHasNoResults))]
    private string _workspaceSearchText = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WorkspaceSearchHasNoResults))]
    private IReadOnlyList<WorkspaceSearchResult> _workspaceSearchResults = [];
    [ObservableProperty] private bool _workspaceSearchTruncated;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedWorkspaceResult))]
    private WorkspaceSearchResult? _selectedWorkspaceResult;

    public bool IsWorkspaceSearchTab => SelectedTabIndex == 5;
    public bool HasSelectedWorkspaceResult => SelectedWorkspaceResult is not null;
    public bool WorkspaceSearchHasNoResults => WorkspaceSearchText.Trim().Length > 0 && WorkspaceSearchResults.Count == 0;

    partial void OnWorkspaceSearchTextChanged(string value) => RefreshWorkspaceSearch();

    private void RefreshWorkspaceSearch()
    {
        var selectedId = SelectedWorkspaceResult?.File.Id;
        string query = WorkspaceSearchText.Trim();
        // ponytail: search the existing catalog in memory, capped at 100 rows. No disk/index service.
        var matches = query.Length == 0 ? [] : AllSpaces.SelectMany(space => space.Files
            .Where(file => !file.IsInTrash && file.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(file => new WorkspaceSearchResult(file, space.Name))).Take(101).ToArray();
        WorkspaceSearchTruncated = matches.Length > 100;
        WorkspaceSearchResults = matches.Take(100).ToArray();
        SelectedWorkspaceResult = WorkspaceSearchResults.FirstOrDefault(item => item.File.Id == selectedId)
            ?? WorkspaceSearchResults.FirstOrDefault();
    }

    [RelayCommand]
    public void RevealSearchResult(WorkspaceSearchResult? result)
    {
        if (result is null) return;
        var space = AllSpaces.FirstOrDefault(item => item.Id == result.File.SpaceId);
        var file = space?.Files.FirstOrDefault(item => item.Id == result.File.Id && !item.IsInTrash);
        if (file is null) { RefreshWorkspaceSearch(); return; }
        FilterMode = SpaceFilterMode.All;
        SelectSpace(space);
        SelectFile(file);
        SelectedTabIndex = 0;
    }
}
