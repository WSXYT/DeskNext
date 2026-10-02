using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskNest.Core.Workspace;

namespace DeskNest.App.ViewModels;

public sealed partial class StudioViewModel
{
    public ObservableCollection<string> ObservationSources { get; } = [];
    [ObservableProperty] private string? _observationSourcePath;
    [ObservableProperty] private SpaceItemViewModel? _observationTargetSpace;
    private IReadOnlyDictionary<string, Guid> _savedObservationTargets = new Dictionary<string, Guid>();

    partial void OnObservationSourcePathChanged(string? value) =>
        ObservationTargetSpace = value is not null && _savedObservationTargets.TryGetValue(value, out var id)
            ? AllSpaces.FirstOrDefault(space => space.Id == id) : null;

    private void RefreshObservationTargets(WorkspaceState state, Guid? target)
    {
        var source = ObservationSourcePath;
        _savedObservationTargets = state.Settings.MonitoredFolderTargets;
        ObservationSources.Clear();
        foreach (var root in state.Settings.MonitoredFolders) ObservationSources.Add(root);
        ObservationSourcePath = source is not null && ObservationSources.Contains(source) ? source : ObservationSources.FirstOrDefault();
        if (source is not null && source == ObservationSourcePath)
            ObservationTargetSpace = AllSpaces.FirstOrDefault(space => space.Id == target);
    }

    [RelayCommand]
    private async Task BindObservationTargetAsync()
    {
        if (ObservationTargetSpace is null) { FolderScanNotice = Localizer["Triage.SelectTargetPrompt"]; return; }
        await SaveObservationTargetAsync(ObservationTargetSpace.Id);
    }

    [RelayCommand]
    private async Task ClearObservationTargetAsync()
    {
        ObservationTargetSpace = null;
        await SaveObservationTargetAsync(null);
    }

    private async Task SaveObservationTargetAsync(Guid? target)
    {
        string? source = ObservationSourcePath;
        if (source is null) { FolderScanNotice = Localizer["Observation.NoFolders"]; return; }
        try
        {
            var updated = await _updateStore(state =>
            {
                if (!state.Settings.MonitoredFolders.Contains(source))
                    throw new InvalidOperationException(Localizer["Observation.NoFolders"]);
                var bindings = new Dictionary<string, Guid>(state.Settings.MonitoredFolderTargets);
                if (target is { } id) bindings[source] = id;
                else bindings.Remove(source);
                return state with { Settings = state.Settings with { MonitoredFolderTargets = bindings } };
            });
            RefreshFromState(updated);
            FolderScanNotice = Localizer["Settings.SavedToast"];
        }
        catch (Exception error) { FolderScanNotice = Localizer.GetString("Files.ActionFailedNotice", error.Message); }
    }
}
