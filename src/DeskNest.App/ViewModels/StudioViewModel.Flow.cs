using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskNest.Core.Flow;

namespace DeskNest.App.ViewModels;

public sealed partial class StudioViewModel
{
    public Func<Task<IReadOnlyList<ManualFlowDefinition>>>? OnLoadFlowDefinitions { get; set; }
    public Func<Guid, string, Task<ManualFlowDefinition>>? OnSaveFlowDefinition { get; set; }
    public ObservableCollection<ManualFlowDefinition> FlowDefinitions { get; } = [];
    public bool IsFlowTab => SelectedTabIndex == 6;
    [ObservableProperty] private ManualFlowDefinition? _selectedFlowDefinition;
    [ObservableProperty] private string _flowJsonDraft = string.Empty;
    [ObservableProperty] private string _flowNotice = string.Empty;
    [ObservableProperty] private bool _isFlowBusy;
    private Guid _editingFlowId;
    private bool _flowsLoaded;

    partial void OnSelectedFlowDefinitionChanged(ManualFlowDefinition? value)
    {
        if (value is null) return;
        _editingFlowId = value.Id;
        FlowJsonDraft = value.Json;
        FlowNotice = string.Empty;
    }
    partial void OnFlowJsonDraftChanged(string value)
    {
        FlowNotice = string.Empty;
        if (!_writingFlowJson && !IsFlowCodeView) RefreshFlowSteps();
    }

    [RelayCommand]
    public void NewFlowDefinition()
    {
        if (IsFlowBusy) return;
        SelectedFlowDefinition = null;
        FlowJsonDraft = ManualFlowDefinitions.Create(Localizer["Flow.NewName"], Localizer["Flow.SampleTitle"], Localizer["Flow.SampleMessage"]);
        _editingFlowId = ManualFlowDefinitions.Read(FlowJsonDraft).Id;
    }

    [RelayCommand]
    public async Task LoadFlowDefinitionsAsync()
    {
        if (IsFlowBusy || OnLoadFlowDefinitions is null) return;
        IsFlowBusy = true;
        try
        {
            var loaded = await OnLoadFlowDefinitions();
            FlowDefinitions.Clear();
            foreach (var definition in loaded) FlowDefinitions.Add(definition);
            SelectedFlowDefinition = FlowDefinitions.FirstOrDefault();
            _flowsLoaded = true;
        }
        catch (Exception error) { FlowNotice = Localizer.GetString("Files.ActionFailedNotice", error.Message); }
        finally { IsFlowBusy = false; }
        if (_flowsLoaded && FlowDefinitions.Count == 0 && string.IsNullOrEmpty(FlowJsonDraft)) NewFlowDefinition();
    }

    [RelayCommand]
    public async Task ValidateFlowDefinitionAsync()
    {
        if (IsFlowBusy) return;
        string draft = FlowJsonDraft;
        IsFlowBusy = true;
        try
        {
            var result = await Task.Run(() => ManualFlowDefinitions.Validate(draft));
            if (draft == FlowJsonDraft) FlowNotice = Localizer[result switch
            {
                FlowDefinitionValidation.Valid => "Flow.Valid",
                FlowDefinitionValidation.NativeUnavailable => "Flow.NativeUnavailable",
                _ => "Flow.Invalid"
            }];
        }
        catch (Exception error) { FlowNotice = Localizer.GetString("Files.ActionFailedNotice", error.Message); }
        finally { IsFlowBusy = false; }
    }

    [RelayCommand]
    public async Task SaveFlowDefinitionAsync()
    {
        if (IsFlowBusy || OnSaveFlowDefinition is null || _editingFlowId == Guid.Empty) return;
        string draft = FlowJsonDraft;
        IsFlowBusy = true;
        try
        {
            var saved = await OnSaveFlowDefinition(_editingFlowId, draft);
            int index = FlowDefinitions.ToList().FindIndex(f => f.Id == saved.Id);
            if (index < 0) FlowDefinitions.Add(saved); else FlowDefinitions[index] = saved;
            if (draft == FlowJsonDraft) SelectedFlowDefinition = saved;
            FlowNotice = Localizer["Flow.Saved"];
        }
        catch (NotSupportedException) { FlowNotice = Localizer["Flow.NativeUnavailable"]; }
        catch (Exception error) { FlowNotice = Localizer.GetString("Files.ActionFailedNotice", error.Message); }
        finally { IsFlowBusy = false; }
    }
}
