using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskNest.App.Localization;
using DeskNest.Core.Flow;

namespace DeskNest.App.ViewModels;

public sealed class FlowStepType(string id, string labelKey) : ViewModelBase
{
    public string Id { get; } = id;
    public string Label => LocalizationManager.Instance[labelKey];
    internal void RefreshLanguage() => OnPropertyChanged(nameof(Label));
}

public sealed class FlowStepItem(string id, string type, string? displayName, FlowStepType? kind) : ViewModelBase
{
    public string Id { get; } = id;
    public string Type { get; } = type;
    public string Label => string.IsNullOrEmpty(displayName) ? kind?.Label ?? Type : displayName;
    internal void RefreshLanguage() => OnPropertyChanged(nameof(Label));
}

public sealed class FlowParameterEditor(string key, string value, Action<string> apply) : ViewModelBase
{
    private string _value = value;
    public string Key { get; } = key;
    public string Label => LocalizationManager.Instance["Flow.Param." + Key];
    public string Value { get => _value; set { if (SetProperty(ref _value, value)) apply(value); } }
    public bool IsMultiline => Key == "message";
    public Avalonia.Media.FlowDirection ValueFlowDirection => Key is "title" or "message"
        ? LocalizationManager.Instance.FlowDirectionValue : Avalonia.Media.FlowDirection.LeftToRight;
    internal void RefreshLanguage()
    {
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(ValueFlowDirection));
    }
}

public sealed partial class StudioViewModel
{
    public IReadOnlyList<FlowStepType> FlowStepTypes { get; } =
    [
        new("pogget.interaction.tip", "Flow.StepTip"), new("pogget.action.file.move", "Flow.StepMove"),
        new("pogget.action.file.map", "Flow.StepMap"), new("pogget.action.file.regexRename", "Flow.StepRename"),
        new("pogget.action.file.readText", "Flow.StepRead")
    ];
    public ObservableCollection<FlowStepItem> FlowSteps { get; } = [];
    public ObservableCollection<FlowParameterEditor> FlowParameters { get; } = [];
    [ObservableProperty] private FlowStepType? _newFlowStepType;
    [ObservableProperty] private FlowStepItem? _selectedFlowStep;
    [ObservableProperty] private string _flowNameEditor = string.Empty;
    [ObservableProperty] private SpaceItemViewModel? _flowSourceSpace;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApplyFlowMoveSelection))]
    private WorkspaceFileItemViewModel? _flowSourceFile;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApplyFlowMoveSelection))]
    private SpaceItemViewModel? _flowTargetSpace;
    private bool _syncingFlowMovePickers;
    public bool IsFlowMoveStep => SelectedFlowStep?.Type == "pogget.action.file.move";
    public IEnumerable<WorkspaceFileItemViewModel> FlowSourceFiles => FlowSourceSpace?.Files.Where(f => !f.IsInTrash) ?? [];
    public bool CanApplyFlowMoveSelection => IsFlowMoveStep && FlowSourceFile is { } file && FlowTargetSpace is { } target && file.SpaceId != target.Id;

    partial void OnFlowSourceSpaceChanged(SpaceItemViewModel? value)
    {
        if (!_syncingFlowMovePickers) FlowSourceFile = null;
        OnPropertyChanged(nameof(FlowSourceFiles));
    }

    private void RefreshFlowMovePickers((Guid? SourceSpace, Guid? SourceFile, Guid? TargetSpace)? selection = null)
    {
        var sourceSpaceId = selection?.SourceSpace;
        var sourceFileId = selection?.SourceFile;
        var targetSpaceId = selection?.TargetSpace;
        if (selection is null && IsFlowMoveStep)
        {
            string? source = FlowParameters.FirstOrDefault(p => p.Key == "source")?.Value;
            string? target = FlowParameters.FirstOrDefault(p => p.Key == "destinationDirectory")?.Value;
            var match = string.IsNullOrWhiteSpace(source) ? null : AllSpaces.SelectMany(s => s.Files).FirstOrDefault(f => !f.IsInTrash && PickerPathMatches(f.Path, source));
            sourceSpaceId = match?.SpaceId;
            sourceFileId = match?.Id;
            targetSpaceId = AllSpaces.FirstOrDefault(s => PickerPathMatches(s.Folder, target))?.Id;
        }
        _syncingFlowMovePickers = true;
        try
        {
            FlowSourceSpace = IsFlowMoveStep ? AllSpaces.FirstOrDefault(s => s.Id == sourceSpaceId) : null;
            FlowSourceFile = FlowSourceSpace?.Files.FirstOrDefault(f => f.Id == sourceFileId && !f.IsInTrash);
            FlowTargetSpace = IsFlowMoveStep ? AllSpaces.FirstOrDefault(s => s.Id == targetSpaceId) : null;
        }
        finally { _syncingFlowMovePickers = false; }
        OnPropertyChanged(nameof(IsFlowMoveStep));
        OnPropertyChanged(nameof(CanApplyFlowMoveSelection));
    }

    private static bool PickerPathMatches(string path, string? draft)
    {
        if (string.IsNullOrWhiteSpace(draft) || !Path.IsPathFullyQualified(draft)) return false;
        try
        {
            return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(draft)),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException) { return false; }
    }

    [RelayCommand]
    public void ApplyFlowMoveSelection()
    {
        if (IsFlowBusy || !CanApplyFlowMoveSelection) return;
        var source = AllSpaces.FirstOrDefault(s => s.Id == FlowSourceSpace?.Id)?.Files.FirstOrDefault(f => f.Id == FlowSourceFile?.Id && !f.IsInTrash);
        var target = AllSpaces.FirstOrDefault(s => s.Id == FlowTargetSpace?.Id);
        if (source is null || target is null || source.SpaceId == target.Id) return;
        _syncingFlowMovePickers = true;
        try
        {
            FlowParameters.Single(p => p.Key == "source").Value = source.Path;
            FlowParameters.Single(p => p.Key == "destinationDirectory").Value = target.Folder;
            FlowParameters.Single(p => p.Key == "destinationContainerId").Value = string.Empty;
        }
        finally { _syncingFlowMovePickers = false; }
        RefreshFlowMovePickers();
    }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFlowStepsView))]
    private bool _isFlowCodeView;
    public bool IsFlowStepsView => !IsFlowCodeView;
    public bool CanEditFlowSteps => _flowRoot is not null;
    public bool HasFlowStepSelection => SelectedFlowStep is not null;
    public bool FlowStepNeedsCode => SelectedFlowStep is not null && FlowParameters.Count == 0;
    private JsonObject? _flowRoot;
    private bool _refreshingFlowSteps, _writingFlowJson;
    private static readonly JsonSerializerOptions FlowJsonOptions = new() { WriteIndented = true };

    partial void OnIsFlowCodeViewChanged(bool value) { if (!value) RefreshFlowSteps(); }
    partial void OnFlowNameEditorChanged(string value)
    {
        if (_refreshingFlowSteps || IsFlowBusy || _flowRoot is null) return;
        _flowRoot["name"] = value;
        WriteFlowJson();
    }
    partial void OnSelectedFlowStepChanged(FlowStepItem? value)
    {
        FlowParameters.Clear();
        if (value is not null && _flowRoot?["actions"] is JsonArray actions)
        {
            var node = actions.OfType<JsonObject>().Single(n => n["id"]!.GetValue<string>() == value.Id);
            var root = _flowRoot;
            foreach (string key in ParameterKeys(value.Type))
            {
                var parameter = (node["parameters"] as JsonObject)?[key];
                string text = parameter is JsonValue v && v.TryGetValue<string>(out var s) ? s : parameter?.ToJsonString() ?? "";
                FlowParameters.Add(new(key, text, edited =>
                {
                    if (IsFlowBusy || !ReferenceEquals(root, _flowRoot) || SelectedFlowStep?.Id != value.Id) return;
                    var parameters = node["parameters"] as JsonObject ?? new JsonObject();
                    if (node["parameters"] is not JsonObject) node["parameters"] = parameters;
                    // Only the edited literal changes. Inputs, aliases, extensions and branches remain intact.
                    parameters[key] = key == "maximumBytes" && long.TryParse(edited, NumberStyles.Integer, CultureInfo.InvariantCulture, out long bytes)
                        ? JsonValue.Create(bytes) : JsonValue.Create(edited);
                    WriteFlowJson();
                    if (!_syncingFlowMovePickers && (key is "source" or "destinationDirectory" or "destinationContainerId")) RefreshFlowMovePickers();
                }));
            }
        }
        OnPropertyChanged(nameof(HasFlowStepSelection));
        OnPropertyChanged(nameof(FlowStepNeedsCode));
        RefreshFlowMovePickers();
    }

    private static string[] ParameterKeys(string type) => type switch
    {
        "pogget.interaction.tip" => ["title", "message"],
        "pogget.action.file.move" => ["source", "destinationDirectory", "destinationContainerId"],
        "pogget.action.file.map" => ["source", "containerId", "insertPosition"],
        "pogget.action.file.regexRename" => ["source", "pattern", "replacement"],
        "pogget.action.file.readText" => ["source", "maximumBytes"],
        _ => []
    };

    private void RefreshFlowSteps()
    {
        string? selected = SelectedFlowStep?.Id;
        _refreshingFlowSteps = true;
        try
        {
            _flowRoot = null;
            SelectedFlowStep = null;
            FlowSteps.Clear();
            if (System.Text.Encoding.UTF8.GetByteCount(FlowJsonDraft) > ManualFlowDefinitions.MaximumBytes)
                throw new InvalidDataException("Flow draft is oversized.");
            // Projection only: incomplete field drafts are editable; native validation still gates saving.
            var root = JsonNode.Parse(FlowJsonDraft, documentOptions: new JsonDocumentOptions { MaxDepth = 32 })!.AsObject();
            if (root["actions"] is not JsonArray actions) throw new InvalidDataException("Missing Flow actions.");
            var rows = actions.Select(n => new FlowStepItem(n!["id"]!.GetValue<string>(), n["type"]!.GetValue<string>(),
                n["displayName"]?.GetValue<string>(), FlowStepTypes.FirstOrDefault(t => t.Id == n["type"]!.GetValue<string>()))).ToArray();
            if (rows.Select(r => r.Id).Distinct().Count() != rows.Length) throw new InvalidDataException("Duplicate step IDs.");
            _flowRoot = root;
            FlowNameEditor = root["name"]?.GetValue<string>() ?? "";
            foreach (var row in rows) FlowSteps.Add(row);
            NewFlowStepType ??= FlowStepTypes[0];
            SelectedFlowStep = FlowSteps.FirstOrDefault(r => r.Id == selected) ?? FlowSteps.FirstOrDefault();
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or InvalidOperationException or ArgumentException or NullReferenceException)
        {
            _flowRoot = null;
            FlowNotice = Localizer["Flow.Invalid"];
        }
        finally { _refreshingFlowSteps = false; OnPropertyChanged(nameof(CanEditFlowSteps)); }
    }

    private void WriteFlowJson()
    {
        if (_flowRoot is null) return;
        _writingFlowJson = true;
        try { FlowJsonDraft = _flowRoot.ToJsonString(FlowJsonOptions); }
        finally { _writingFlowJson = false; }
    }

    [RelayCommand]
    public void AddFlowStep()
    {
        if (IsFlowBusy || _flowRoot?["actions"] is not JsonArray actions || NewFlowStepType is null) return;
        string id = "step-" + Guid.NewGuid().ToString("N");
        var parameters = new JsonObject();
        if (NewFlowStepType.Id == "pogget.interaction.tip") parameters["message"] = Localizer["Flow.SampleMessage"];
        if (NewFlowStepType.Id == "pogget.action.file.readText") parameters["maximumBytes"] = 32768;
        if (NewFlowStepType.Id == "pogget.action.file.map") parameters["insertPosition"] = "end";
        actions.Add(new JsonObject { ["id"] = id, ["type"] = NewFlowStepType.Id, ["version"] = 1, ["enabled"] = true, ["parameters"] = parameters });
        WriteFlowJson();
        RefreshFlowSteps();
        SelectedFlowStep = FlowSteps.Single(r => r.Id == id);
    }

    [RelayCommand]
    public void MoveFlowStep(string? direction)
    {
        if (IsFlowBusy || SelectedFlowStep is null || _flowRoot?["actions"] is not JsonArray actions) return;
        int index = FlowSteps.IndexOf(SelectedFlowStep);
        int target = direction == "up" ? index - 1 : direction == "down" ? index + 1 : index;
        if (target < 0 || target >= actions.Count || target == index) return;
        var node = actions[index];
        actions.RemoveAt(index);
        actions.Insert(target, node);
        WriteFlowJson();
        RefreshFlowSteps();
    }

    [RelayCommand]
    public void RemoveFlowStep()
    {
        if (IsFlowBusy || SelectedFlowStep is null || _flowRoot?["actions"] is not JsonArray actions) return;
        actions.RemoveAt(FlowSteps.IndexOf(SelectedFlowStep));
        WriteFlowJson();
        RefreshFlowSteps();
    }

    private void RefreshFlowLanguage()
    {
        OnPropertyChanged(nameof(FlowPromptActionText));
        foreach (var run in FlowRuns) run.RefreshLanguage();
        foreach (var type in FlowStepTypes) type.RefreshLanguage();
        foreach (var step in FlowSteps) step.RefreshLanguage();
        foreach (var parameter in FlowParameters) parameter.RefreshLanguage();
    }
}
