using System;
using System.ComponentModel;
using Avalonia.Utilities;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskNest.App.Localization;
using DeskNest.App.Themes;
using DeskNest.Core.Workspace;

namespace DeskNest.App.ViewModels;

public enum SpaceFilterMode
{
    All,
    Managed,
    Mapped
}

public sealed partial class StudioViewModel : ViewModelBase
{
    private readonly Func<Func<WorkspaceState, WorkspaceState>, Task<WorkspaceState>> _updateStore;
    public Action? OnRequestReopenOnboarding { get; set; }

    public LocalizationManager Localizer => LocalizationManager.Instance;
    public ThemeManager ThemeMgr => ThemeManager.Instance;

    public IReadOnlyList<LanguageInfo> SupportedLanguages => LocalizationManager.SupportedLanguages;
    public IReadOnlyList<AppThemeMode> SupportedThemes { get; } = new[]
    {
        AppThemeMode.Dark,
        AppThemeMode.Light,
        AppThemeMode.System
    };

    [ObservableProperty]
    private int _selectedTabIndex = 0; // 0: Spaces, 1: Triage, 2: Capsule, 3: Settings, 4: Diagnostics

    public bool IsSpacesTab => SelectedTabIndex == 0;
    public bool IsTriageTab => SelectedTabIndex == 1;
    public bool IsCapsuleTab => SelectedTabIndex == 2;
    public bool IsSettingsTab => SelectedTabIndex == 3;
    public bool IsProbeTab => SelectedTabIndex == 4;

    partial void OnSelectedTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsSpacesTab));
        OnPropertyChanged(nameof(IsTriageTab));
        OnPropertyChanged(nameof(IsCapsuleTab));
        OnPropertyChanged(nameof(IsSettingsTab));
        OnPropertyChanged(nameof(IsProbeTab));
    }

    // ==========================================
    // SPACES CANVAS
    // ==========================================
    [ObservableProperty]
    private SpaceFilterMode _filterMode = SpaceFilterMode.All;

    public bool IsAllFilter => FilterMode == SpaceFilterMode.All;
    public bool IsManagedFilter => FilterMode == SpaceFilterMode.Managed;
    public bool IsMappedFilter => FilterMode == SpaceFilterMode.Mapped;

    [ObservableProperty]
    private SpaceItemViewModel? _selectedSpace;

    public ObservableCollection<SpaceItemViewModel> AllSpaces { get; } = new();
    public ObservableCollection<SpaceItemViewModel> FilteredSpaces { get; } = new();

    public int ManagedSpacesCount => AllSpaces.Count(s => s.IsManaged);
    public int MappedSpacesCount => AllSpaces.Count(s => s.IsMapped);

    public string ManagedSpacesCountText => Localizer.GetString("Spaces.ManagedCountFormat", ManagedSpacesCount);
    public string MappedSpacesCountText => Localizer.GetString("Spaces.MappedCountFormat", MappedSpacesCount);

    // New Space Dialog fields
    [ObservableProperty]
    private bool _isAddSpaceDialogOpen;

    [ObservableProperty]
    private string _newSpaceName = string.Empty;

    partial void OnNewSpaceNameChanged(string value)
    {
        if (NewSpaceMode == SpaceStorageMode.Managed && !string.IsNullOrWhiteSpace(SettingsManagedRoot))
        {
            var trimmed = value?.Trim() ?? string.Empty;
            NewSpaceFolder = string.IsNullOrWhiteSpace(trimmed)
                ? SettingsManagedRoot
                : Path.Combine(SettingsManagedRoot, trimmed);
        }
    }

    [ObservableProperty]
    private string _newSpaceDesc = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNewSpaceManaged))]
    [NotifyPropertyChangedFor(nameof(IsNewSpaceMapped))]
    private SpaceStorageMode _newSpaceMode = SpaceStorageMode.Managed;

    public bool IsNewSpaceManaged
    {
        get => NewSpaceMode == SpaceStorageMode.Managed;
        set { if (value) NewSpaceMode = SpaceStorageMode.Managed; }
    }

    public bool IsNewSpaceMapped
    {
        get => NewSpaceMode == SpaceStorageMode.Mapped;
        set { if (value) NewSpaceMode = SpaceStorageMode.Mapped; }
    }

    partial void OnNewSpaceModeChanged(SpaceStorageMode value)
    {
        if (value == SpaceStorageMode.Managed) OnNewSpaceNameChanged(NewSpaceName);
        SpaceDialogError = null;
    }

    [ObservableProperty]
    private string _newSpaceFolder = string.Empty;

    [ObservableProperty]
    private string? _spaceDialogError;

    // ==========================================
    // DRAG-AND-DROP SURFACE & CAPSULE STATE
    // ==========================================
    [ObservableProperty]
    private bool _isDragOverSpaceSurface;

    [ObservableProperty]
    private string? _spaceDropNotice;

    public bool HasSpaceDropNotice => !string.IsNullOrWhiteSpace(SpaceDropNotice);

    partial void OnSpaceDropNoticeChanged(string? value)
    {
        OnPropertyChanged(nameof(HasSpaceDropNotice));
    }

    [ObservableProperty]
    private bool _isDragOverCapsule;

    // Injectable callbacks for drag-and-drop routing without direct file moves
    public Func<IReadOnlyList<string>, SpaceItemViewModel?, Task>? OnFilesDroppedOnSpace { get; set; }
    public Func<IReadOnlyList<string>, Task>? OnFilesDroppedOnCapsule { get; set; }

    // ==========================================
    // OPERATION HISTORY & MANUAL MOVE BOUNDARY (P3)
    // ==========================================
    public ObservableCollection<OperationItemViewModel> OperationHistory { get; } = new();

    public int OperationHistoryCount => OperationHistory.Count;
    public bool HasOperationHistory => OperationHistoryCount > 0;

    // Injectable callback boundary for manual move execution (P3)
    public Func<Guid, Guid, Task>? OnExecuteManualMove { get; set; }
    public bool CanExecuteManualMove => OnExecuteManualMove != null;
    public string ManualMoveStatusNotice => CanExecuteManualMove
        ? Localizer["Operations.ManualMoveReadyNotice"]
        : Localizer["Operations.ManualMoveGatedNotice"];

    public void AttachManualMoveExecutor(Func<Guid, Guid, Task> executor)
    {
        OnExecuteManualMove = executor ?? throw new ArgumentNullException(nameof(executor));
        OnPropertyChanged(nameof(CanExecuteManualMove));
        OnPropertyChanged(nameof(ManualMoveStatusNotice));
    }

    // Injectable callback boundary for manual undo execution (P3)
    public Func<Guid, Task>? OnUndoManualMove { get; set; }
    public bool CanUndoManualMove => OnUndoManualMove != null;
    public string ManualUndoStatusNotice => CanUndoManualMove
        ? Localizer["Operations.UndoReadyNotice"]
        : Localizer["Operations.UndoGatedNotice"];

    // Presentation only; Core revalidates recorded identity before performing the undo.
    internal OperationItemViewModel? FindUndoForFile(WorkspaceFileItemViewModel? file)
    {
        if (!CanUndoManualMove || file is null || file.IsInTrash) return null;
        var operation = OperationHistory.FirstOrDefault(item => item.FileId == file.Id && !item.IsUndone);
        return operation is { CanUndo: true } && string.Equals(operation.DestinationPath, file.Path,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            ? operation : null;
    }

    public void AttachManualUndoExecutor(Func<Guid, Task> executor)
    {
        OnUndoManualMove = executor ?? throw new ArgumentNullException(nameof(executor));
        OnPropertyChanged(nameof(CanUndoManualMove));
        OnPropertyChanged(nameof(ManualUndoStatusNotice));
    }

    // ==========================================
    // WORKSPACE FILE ACTIONS & CAPABILITY GATES (P3 UI)
    // ==========================================
    [ObservableProperty]
    private WorkspaceFileItemViewModel? _selectedFile;

    public bool HasSelectedFile => SelectedFile != null;
    public string SelectedFileDeleteActionText => SelectedFile?.DeleteActionText ?? Localizer["Files.ActionDelete"];
    public string SelectedFileCapabilitySummary => SelectedFile?.CapabilityDescription ?? string.Empty;

    partial void OnSelectedFileChanged(WorkspaceFileItemViewModel? value)
    {
        if (SelectedSpace != null)
        {
            foreach (var f in SelectedSpace.Files)
            {
                f.IsSelected = (f == value);
            }
        }
        OnPropertyChanged(nameof(HasSelectedFile));
        OnPropertyChanged(nameof(SelectedFileDeleteActionText));
        OnPropertyChanged(nameof(SelectedFileCapabilitySummary));
        NotifyFileActionGates();
    }

    [RelayCommand]
    public void SelectFile(WorkspaceFileItemViewModel? file)
    {
        SelectedFile = file;
    }

    public Func<WorkspaceFileItemViewModel, Task>? OnOpenFile { get; set; }
    public bool CanOpenFile => OnOpenFile != null && SelectedFile != null;
    public string OpenFileStatusNotice => CanOpenFile
        ? Localizer["Files.OpenReadyNotice"]
        : Localizer["Files.OpenGatedNotice"];

    public Func<WorkspaceFileItemViewModel, Task>? OnRevealFile { get; set; }
    public bool CanRevealFile => OnRevealFile != null && SelectedFile != null;
    public string RevealFileStatusNotice => CanRevealFile
        ? Localizer["Files.RevealReadyNotice"]
        : Localizer["Files.RevealGatedNotice"];

    public Func<WorkspaceFileItemViewModel, Task>? OnPreviewFile { get; set; }
    public bool CanPreviewFile => OnPreviewFile != null && SelectedFile != null;
    public string PreviewFileStatusNotice => CanPreviewFile
        ? Localizer["Files.PreviewReadyNotice"]
        : Localizer["Files.PreviewGatedNotice"];

    public Func<WorkspaceFileItemViewModel, Task>? OnCopyFile { get; set; }
    public bool CanCopyFile => OnCopyFile != null && SelectedFile != null;
    public string CopyFileStatusNotice => CanCopyFile
        ? Localizer["Files.CopyReadyNotice"]
        : Localizer["Files.CopyGatedNotice"];

    public Func<WorkspaceFileItemViewModel, Task>? OnCutFile { get; set; }
    public bool CanCutFile => OnCutFile != null && SelectedFile != null;
    public string CutFileStatusNotice => CanCutFile
        ? Localizer["Files.CutReadyNotice"]
        : Localizer["Files.CutGatedNotice"];

    public Func<SpaceItemViewModel, Task>? OnPasteFile { get; set; }
    public bool CanPasteFile => OnPasteFile != null && SelectedSpace != null;
    public string PasteFileStatusNotice => CanPasteFile
        ? Localizer["Files.PasteReadyNotice"]
        : Localizer["Files.PasteGatedNotice"];

    public Func<WorkspaceFileItemViewModel, string, Task>? OnRenameFile { get; set; }
    public bool CanRenameFile => OnRenameFile != null && SelectedFile?.IsManaged == true && !SelectedFile.IsInTrash;
    public string RenameFileStatusNotice => CanRenameFile
        ? Localizer["Files.RenameReadyNotice"]
        : Localizer["Files.RenameGatedNotice"];

    public Func<WorkspaceFileItemViewModel, Task>? OnDeleteFile { get; set; }
    public bool CanDeleteFile => OnDeleteFile != null && SelectedFile != null && !SelectedFile.IsInTrash && !IsDeleteConfirmationBusy;
    public string DeleteFileStatusNotice => CanDeleteFile
        ? Localizer["Files.DeleteReadyNotice"]
        : Localizer["Files.DeleteGatedNotice"];

    [ObservableProperty]
    private string? _fileActionNotice;

    public bool HasFileActionNotice => !string.IsNullOrWhiteSpace(FileActionNotice);

    partial void OnFileActionNoticeChanged(string? value)
    {
        OnPropertyChanged(nameof(HasFileActionNotice));
    }

    public void AttachOpenFileExecutor(Func<WorkspaceFileItemViewModel, Task> executor)
    {
        OnOpenFile = executor ?? throw new ArgumentNullException(nameof(executor));
        NotifyFileActionGates();
    }

    public void AttachRevealFileExecutor(Func<WorkspaceFileItemViewModel, Task> executor)
    {
        OnRevealFile = executor ?? throw new ArgumentNullException(nameof(executor));
        NotifyFileActionGates();
    }

    public void AttachPreviewFileExecutor(Func<WorkspaceFileItemViewModel, Task> executor)
    {
        OnPreviewFile = executor ?? throw new ArgumentNullException(nameof(executor));
        NotifyFileActionGates();
    }

    public void AttachCopyFileExecutor(Func<WorkspaceFileItemViewModel, Task> executor)
    {
        OnCopyFile = executor ?? throw new ArgumentNullException(nameof(executor));
        NotifyFileActionGates();
    }

    public void AttachCutFileExecutor(Func<WorkspaceFileItemViewModel, Task> executor)
    {
        OnCutFile = executor ?? throw new ArgumentNullException(nameof(executor));
        NotifyFileActionGates();
    }

    public void AttachPasteFileExecutor(Func<SpaceItemViewModel, Task> executor)
    {
        OnPasteFile = executor ?? throw new ArgumentNullException(nameof(executor));
        NotifyFileActionGates();
    }

    public void AttachRenameFileExecutor(Func<WorkspaceFileItemViewModel, string, Task> executor)
    {
        OnRenameFile = executor ?? throw new ArgumentNullException(nameof(executor));
        NotifyFileActionGates();
    }

    public void AttachDeleteFileExecutor(Func<WorkspaceFileItemViewModel, Task> executor)
    {
        OnDeleteFile = executor ?? throw new ArgumentNullException(nameof(executor));
        NotifyFileActionGates();
    }

    public void NotifyFileActionGates()
    {
        OnPropertyChanged(nameof(CanOpenFile));
        OnPropertyChanged(nameof(OpenFileStatusNotice));
        OnPropertyChanged(nameof(CanRevealFile));
        OnPropertyChanged(nameof(RevealFileStatusNotice));
        OnPropertyChanged(nameof(CanPreviewFile));
        OnPropertyChanged(nameof(PreviewFileStatusNotice));
        OnPropertyChanged(nameof(CanCopyFile));
        OnPropertyChanged(nameof(CopyFileStatusNotice));
        OnPropertyChanged(nameof(CanCutFile));
        OnPropertyChanged(nameof(CutFileStatusNotice));
        OnPropertyChanged(nameof(CanPasteFile));
        OnPropertyChanged(nameof(PasteFileStatusNotice));
        OnPropertyChanged(nameof(CanRenameFile));
        OnPropertyChanged(nameof(RenameFileStatusNotice));
        OnPropertyChanged(nameof(CanDeleteFile));
        OnPropertyChanged(nameof(DeleteFileStatusNotice));
        OnPropertyChanged(nameof(SelectedFileDeleteActionText));
        OnPropertyChanged(nameof(SelectedFileCapabilitySummary));
        OnPropertyChanged(nameof(DeleteConfirmTitleText));
        OnPropertyChanged(nameof(DeleteConfirmPromptText));
        OnPropertyChanged(nameof(DeleteConfirmActionText));
    }

    public Func<object, System.Threading.CancellationToken, Task>? OnPreviewClassification { get; set; }

    // The editable key/permission never enter WorkspaceSettings. OS storage is explicit only.
    private readonly DeskNest.Platform.JevCredentialStore _jevCredentials = new();
    public bool CanStoreJevCredential => DeskNest.Platform.JevCredentialStore.IsSupported;
    [ObservableProperty] private string _jevCredentialNotice = string.Empty;

    [RelayCommand]
    public void ManageJevCredential(string action)
    {
        if (!CanStoreJevCredential || !IsJevPreview) return;
        JevSendConsent = false;
        PreviewClassificationCommand.Cancel();
        try
        {
            switch (action)
            {
                case "save":
                    _jevCredentials.Save(JevSessionKey);
                    JevCredentialNotice = Localizer["Classification.KeySaved"];
                    break;
                case "load":
                    ClearJevSession();
                    JevSessionKey = _jevCredentials.Load() ?? string.Empty;
                    JevCredentialNotice = Localizer[JevSessionKey.Length == 0 ? "Classification.KeyMissing" : "Classification.KeyLoaded"];
                    break;
                case "delete":
                    ClearJevSession();
                    _jevCredentials.Delete();
                    JevCredentialNotice = Localizer["Classification.KeyDeleted"];
                    break;
            }
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or ArgumentException or IOException or NotSupportedException)
        {
            // Do not echo native credential data or save it elsewhere after an OS-store failure.
            JevCredentialNotice = Localizer["Classification.KeyStoreFailed"];
        }
    }

    [ObservableProperty] private string _jevSessionKey = string.Empty;
    [ObservableProperty] private bool _jevSendConsent;
    partial void OnJevSessionKeyChanged(string value) { JevCredentialNotice = string.Empty; JevSendConsent = false; PreviewClassificationCommand.Cancel(); }
    partial void OnJevSendConsentChanged(bool value) { if (!value) PreviewClassificationCommand.Cancel(); }
    [RelayCommand]
    public void ClearJevSession()
    {
        PreviewClassificationCommand.Cancel();
        JevSessionKey = string.Empty;
        JevSendConsent = false;
        JevCredentialNotice = string.Empty;
    }

    [RelayCommand(IncludeCancelCommand = true)]
    public async Task PreviewClassificationAsync(object? file, System.Threading.CancellationToken token)
    {
        if (file is not (WorkspaceFileItemViewModel or PendingItemViewModel) || file is WorkspaceFileItemViewModel { IsInTrash: true }) return;
        var pending = file as PendingItemViewModel;
        if (pending is not null) pending.ClassificationTarget = null;
        if (OnPreviewClassification is null)
        {
            FileActionNotice = Localizer["Classification.Setup"];
            return;
        }
        FileActionNotice = Localizer["Classification.Running"];
        try { await OnPreviewClassification(file, token); }
        catch (OperationCanceledException) { FileActionNotice = Localizer["Classification.Cancelled"]; }
        catch (Exception error) { FileActionNotice = Localizer.GetString("Files.ActionFailedNotice", error.Message); }
        finally { if (pending is not null) pending.ResolutionNotice = FileActionNotice; }
    }

    [RelayCommand]
    public void UseClassificationTarget(PendingItemViewModel? item)
    {
        var current = PendingItems.FirstOrDefault(p => p.Id == item?.Id);
        if (current?.ClassificationTarget is not { } suggestion) return;
        current.TargetSpace = AllSpaces.FirstOrDefault(s => s.Id == suggestion.Id);
        current.ClassificationTarget = null;
    }

    public void ShowClassificationPreview(string fileName, string content, bool cloud = false)
    {
        PreviewFileName = fileName;
        PreviewKind = Localizer[cloud ? "Classification.JevCloud" : "Classification.LocalCpu"];
        PreviewContent = content;
        PreviewDetails = Localizer[cloud ? "Classification.JevResultNotice" : "Classification.ReadOnly"];
        IsPreviewTruncated = false;
        IsPreviewDialogOpen = true;
        FileActionNotice = PreviewDetails;
    }

    // ==========================================
    // PREVIEW BOUNDED RESULT SURFACE (P3 UI)
    // ==========================================
    [ObservableProperty]
    private bool _isPreviewDialogOpen;

    [ObservableProperty]
    private string _previewFileName = string.Empty;

    [ObservableProperty]
    private string _previewKind = string.Empty;

    [ObservableProperty]
    private string _previewContent = string.Empty;

    [ObservableProperty]
    private string _previewDetails = string.Empty;

    [ObservableProperty]
    private bool _isPreviewTruncated;

    public void ShowFilePreview(DeskNest.Platform.FilePreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        PreviewFileName = preview.Name;
        PreviewKind = preview.Kind;
        PreviewContent = preview.Content;
        IsPreviewTruncated = preview.Truncated;

        if (preview.Kind == "directory")
        {
            var truncatedNotice = preview.Truncated ? $" ({Localizer["Files.PreviewTruncatedNotice"]})" : string.Empty;
            PreviewDetails = Localizer.GetString("Files.PreviewDirectorySummary", truncatedNotice);
        }
        else if (preview.Kind == "text")
        {
            PreviewDetails = preview.Truncated
                ? Localizer["Files.PreviewTruncatedNotice"]
                : (preview.Length.HasValue ? Localizer.GetString("Files.PreviewMetadataSummary", preview.Length.Value) : string.Empty);
        }
        else
        {
            PreviewDetails = preview.Length.HasValue
                ? Localizer.GetString("Files.PreviewMetadataSummary", preview.Length.Value)
                : Localizer["Files.PreviewMetadataOnly"];
        }

        IsPreviewDialogOpen = true;
        FileActionNotice = Localizer.GetString("Files.PreviewSuccessNotice", preview.Name);
    }

    [RelayCommand]
    public void ClosePreviewDialog()
    {
        IsPreviewDialogOpen = false;
        PreviewFileName = string.Empty;
        PreviewKind = string.Empty;
        PreviewContent = string.Empty;
        PreviewDetails = string.Empty;
        IsPreviewTruncated = false;
    }

    // Rename Dialog Fields
    [ObservableProperty]
    private bool _isRenameDialogOpen;

    [ObservableProperty]
    private string _renameItemName = string.Empty;

    [ObservableProperty]
    private WorkspaceFileItemViewModel? _renamingFile;

    [ObservableProperty]
    private string? _renameDialogError;

    // Delete Confirmation Dialog Fields (P3 UI)
    [ObservableProperty]
    private bool _isDeleteConfirmationDialogOpen;

    [ObservableProperty]
    private WorkspaceFileItemViewModel? _deletingFile;

    [ObservableProperty]
    private string? _deleteConfirmationDialogError;

    [ObservableProperty]
    private bool _isDeleteConfirmationBusy;

    partial void OnIsDeleteConfirmationBusyChanged(bool value)
    {
        NotifyFileActionGates();
    }

    public string DeleteConfirmTitleText => Localizer["Files.DeleteConfirmTitle"];

    public string DeleteConfirmPromptText => DeletingFile != null
        ? Localizer.GetString("Files.DeleteConfirmPrompt", DeletingFile.Name)
        : string.Empty;

    public string DeleteConfirmActionText => Localizer["Files.DeleteConfirmAction"];

    partial void OnDeletingFileChanged(WorkspaceFileItemViewModel? value)
    {
        OnPropertyChanged(nameof(DeleteConfirmPromptText));
    }

    // ==========================================
    // DROP CAPSULE ENTRYPOINT
    // ==========================================
    [ObservableProperty]
    private string _capsuleInputPath = string.Empty;

    [ObservableProperty]
    private string? _capsuleNotice;

    // ==========================================
    // TRIAGE DECISION DRAWER
    // ==========================================
    public ObservableCollection<PendingItemViewModel> PendingItems { get; } = new();

    [ObservableProperty]
    private PendingItemViewModel? _selectedPendingItem;

    public int PendingCount => PendingItems.Count;
    public bool HasPendingItems => PendingCount > 0;

    public Func<Guid, Guid, long, Task>? OnImportPending { get; set; }
    private long _workspaceRevision;
    private (Guid PendingId, Guid TargetId, long Revision) _importRequest;
    [ObservableProperty] private bool _isImportConfirmationOpen;
    [ObservableProperty] private bool _isImportBusy;
    [ObservableProperty] private string _importSourcePath = string.Empty;
    [ObservableProperty] private string _importDestinationPath = string.Empty;
    [ObservableProperty] private string? _importError;

    [RelayCommand]
    public void OpenImportConfirmation(PendingItemViewModel? item)
    {
        if (item is null || IsImportBusy) return;
        if (OnImportPending is null) { item.ResolutionNotice = Localizer["Triage.ImportUnavailable"]; return; }
        var target = item.TargetSpace ?? AllSpaces.FirstOrDefault(s => s.Id == item.SuggestedSpaceId);
        if (target is null) { item.ResolutionNotice = Localizer["Triage.SelectTargetPrompt"]; return; }
        SelectedPendingItem = item;
        _importRequest = (item.Id, target.Id, _workspaceRevision);
        ImportSourcePath = item.Path;
        ImportDestinationPath = Path.Combine(target.Folder, Path.GetFileName(Path.TrimEndingDirectorySeparator(item.Path)));
        ImportError = null;
        IsImportConfirmationOpen = true;
    }

    [RelayCommand]
    public void CloseImportConfirmation()
    {
        if (IsImportBusy) return;
        IsImportConfirmationOpen = false;
        ImportSourcePath = ImportDestinationPath = string.Empty;
        ImportError = null;
        _importRequest = default;
    }

    [RelayCommand]
    public async Task ConfirmImportAsync()
    {
        if (!IsImportConfirmationOpen || IsImportBusy || OnImportPending is null) return;
        IsImportBusy = true;
        ImportError = null;
        try
        {
            await OnImportPending(_importRequest.PendingId, _importRequest.TargetId, _importRequest.Revision);
            IsImportBusy = false;
            CloseImportConfirmation();
            FileActionNotice = Localizer["Triage.ImportSuccess"];
        }
        catch (Exception error) { ImportError = Localizer.GetString("Files.ActionFailedNotice", error.Message); }
        finally { IsImportBusy = false; }
    }

    // Triage Action Dialog fields
    [ObservableProperty]
    private bool _isCreateSpaceFromTriageOpen;

    [ObservableProperty]
    private string _triageNewSpaceName = string.Empty;

    [ObservableProperty]
    private string _triageNewSpaceDesc = string.Empty;

    // ==========================================
    // SETTINGS SURFACE
    // ==========================================
    [ObservableProperty]
    private LanguageInfo _settingsLanguage;

    [ObservableProperty]
    private AppThemeMode _settingsTheme;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsJevPreview))]
    [NotifyPropertyChangedFor(nameof(IsLayaPreview))]
    private InferenceProvider _settingsProvider;

    public bool IsJevPreview { get => SettingsProvider == InferenceProvider.Jev; set { if (value) SettingsProvider = InferenceProvider.Jev; } }
    public bool IsLayaPreview { get => SettingsProvider == InferenceProvider.Laya; set { if (value) SettingsProvider = InferenceProvider.Laya; } }
    partial void OnSettingsProviderChanged(InferenceProvider value) { ClearJevSession(); }

    [ObservableProperty]
    private string _settingsModelCache = string.Empty;

    [ObservableProperty]
    private string _settingsManagedRoot = string.Empty;

    public ObservableCollection<string> SettingsMonitoredFolders { get; } = new();
    public ObservableCollection<string> SettingsExcludedFolders { get; } = new();

    [ObservableProperty]
    private string _newSettingsMonitoredFolder = string.Empty;

    [ObservableProperty]
    private string _newSettingsExcludedFolder = string.Empty;

    [ObservableProperty]
    private bool _settingsWantsMonitoring;

    [ObservableProperty]
    private string? _settingsSavedFeedback;

    // ==========================================
    // SYSTEM PROBE
    // ==========================================
    public SystemProbeViewModel Probe { get; }

    public StudioViewModel(WorkspaceState state, Func<Func<WorkspaceState, WorkspaceState>, Task<WorkspaceState>> updateStore)
    {
        _updateStore = updateStore;
        Probe = new SystemProbeViewModel();

        // Initialize settings fields
        _settingsLanguage = LocalizationManager.SupportedLanguages.FirstOrDefault(
            l => l.Code.Equals(state.Settings.Language, StringComparison.OrdinalIgnoreCase))
            ?? LocalizationManager.SupportedLanguages[0];

        _settingsTheme = Enum.TryParse<AppThemeMode>(state.Settings.Theme, out var tm)
            ? tm : AppThemeMode.Dark;

        _settingsProvider = state.Settings.Provider;
        _settingsModelCache = state.Settings.ModelCacheDirectory ?? string.Empty;
        _settingsManagedRoot = state.Settings.ManagedRoot;
        _settingsWantsMonitoring = state.Settings.WantsMonitoring;

        foreach (var m in state.Settings.MonitoredFolders)
            SettingsMonitoredFolders.Add(m);
        foreach (var e in state.Settings.ExcludedFolders)
            SettingsExcludedFolders.Add(e);

        WeakEventHandlerManager.Subscribe<LocalizationManager, PropertyChangedEventArgs, StudioViewModel>(
            Localizer, nameof(LocalizationManager.PropertyChanged), OnLocalizerChanged);

        // Load spaces and files
        RefreshFromState(state);
    }

    private void OnLocalizerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LocalizationManager.CurrentLanguage)) return;
        OnPropertyChanged(nameof(ManualMoveStatusNotice));
        OnPropertyChanged(nameof(ManualUndoStatusNotice));
        NotifyFileActionGates();
    }

    private static readonly char[] CrossPlatformForbiddenChars =
        ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static bool TrySanitizeSpaceLeafName(string? rawName, string managedRoot, out string safeName, out string safeFolder)
    {
        safeName = string.Empty;
        safeFolder = string.Empty;

        if (string.IsNullOrWhiteSpace(rawName))
            return false;

        // Reject leading or trailing whitespace, or trailing dots
        if (rawName.StartsWith(' ') || rawName.EndsWith(' ') || rawName.EndsWith('.'))
            return false;

        var trimmed = rawName.Trim();
        if (trimmed.Length == 0 || trimmed.Length > 160)
            return false;

        // Reject dot, dotdot
        if (trimmed == "." || trimmed == "..")
            return false;

        // Reject Windows-reserved characters across all platforms (< > : " / \ | ? *)
        if (trimmed.IndexOfAny(CrossPlatformForbiddenChars) >= 0)
            return false;

        // Reject OS-specific invalid filename characters and control characters
        if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || trimmed.Any(char.IsControl))
            return false;

        // Reject Windows reserved device names (CON, NUL, COM1, etc.)
        var stem = Path.GetFileNameWithoutExtension(trimmed);
        if (ReservedDeviceNames.Contains(trimmed) || ReservedDeviceNames.Contains(stem))
            return false;

        if (Path.IsPathRooted(trimmed))
            return false;

        if (string.IsNullOrWhiteSpace(managedRoot) || !Path.IsPathFullyQualified(managedRoot))
            return false;

        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(managedRoot));
        var combined = Path.GetFullPath(Path.Combine(fullRoot, trimmed));

        // Enforce exact parent directory equality to eliminate prefix-only spoofing
        var parent = Path.GetDirectoryName(combined);
        if (parent == null)
            return false;
        parent = Path.TrimEndingDirectorySeparator(parent);

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!string.Equals(parent, fullRoot, comparison))
            return false;

        safeName = trimmed;
        safeFolder = combined;
        return true;
    }

    public void RefreshFromState(WorkspaceState state)
    {
        _workspaceRevision = state.Revision;
        var prevSpaceId = SelectedSpace?.Id;
        var prevPendingId = SelectedPendingItem?.Id;
        var prevFileId = SelectedFile?.Id;

        AllSpaces.Clear();
        var spacesDict = new Dictionary<Guid, SpaceItemViewModel>();

        foreach (var s in state.Spaces)
        {
            var filesInSpace = state.Files.Where(f => f.SpaceId == s.Id && !f.IsInTrash).ToList();
            var spaceVm = new SpaceItemViewModel(s, filesInSpace.Count);
            foreach (var f in filesInSpace)
            {
                spaceVm.Files.Add(new WorkspaceFileItemViewModel(f.Id, f.SpaceId, f.Name, f.Path, f.IsDirectory, s.Mode, f.IsInTrash));
            }
            spaceVm.NotifyFilesChanged();
            AllSpaces.Add(spaceVm);
            spacesDict[s.Id] = spaceVm;
        }

        FilteredSpaces.Clear();
        foreach (var s in AllSpaces)
        {
            if (FilterMode == SpaceFilterMode.All ||
                (FilterMode == SpaceFilterMode.Managed && s.IsManaged) ||
                (FilterMode == SpaceFilterMode.Mapped && s.IsMapped))
            {
                FilteredSpaces.Add(s);
            }
        }

        var matchedSpace = (prevSpaceId.HasValue ? FilteredSpaces.FirstOrDefault(s => s.Id == prevSpaceId.Value) : null)
            ?? FilteredSpaces.FirstOrDefault();
        SelectSpace(matchedSpace);

        if (SelectedSpace != null)
        {
            var matchedFile = prevFileId.HasValue
                ? SelectedSpace.Files.FirstOrDefault(f => f.Id == prevFileId.Value)
                : SelectedSpace.Files.FirstOrDefault();
            SelectFile(matchedFile);
        }
        else
        {
            SelectFile(null);
        }

        // Preserve or gracefully reconcile in-flight dialog targets across state refresh
        if (DeletingFile != null)
        {
            var matchedDeleting = SelectedSpace?.Files.FirstOrDefault(f => f.Id == DeletingFile.Id)
                ?? AllSpaces.SelectMany(s => s.Files).FirstOrDefault(f => f.Id == DeletingFile.Id);
            if (matchedDeleting != null)
            {
                DeletingFile = matchedDeleting;
            }
            else
            {
                // Target has vanished from available spaces: dismiss programmatic confirmation,
                // do not retain stale enabled target
                DismissDeleteConfirmationDialog();
            }
        }

        if (RenamingFile != null)
        {
            var matchedRenaming = SelectedSpace?.Files.FirstOrDefault(f => f.Id == RenamingFile.Id)
                ?? AllSpaces.SelectMany(s => s.Files).FirstOrDefault(f => f.Id == RenamingFile.Id);
            if (matchedRenaming != null)
            {
                RenamingFile = matchedRenaming;
            }
            else
            {
                CloseRenameDialog();
            }
        }

        // Load Pending Triage
        PendingItems.Clear();
        foreach (var p in state.Pending)
        {
            string? suggestedName = p.SuggestedSpaceId != null && spacesDict.TryGetValue(p.SuggestedSpaceId.Value, out var target)
                ? target.Name : null;
            PendingItems.Add(new PendingItemViewModel(p, suggestedName));
        }

        SelectedPendingItem = prevPendingId.HasValue
            ? PendingItems.FirstOrDefault(p => p.Id == prevPendingId.Value)
            : null;

        // Load Operation History
        OperationHistory.Clear();
        foreach (var op in state.Operations.OrderByDescending(o => o.CreatedAt))
        {
            var file = state.Files.FirstOrDefault(f => f.Id == op.FileId);
            string fileName = file?.Name ?? (!string.IsNullOrEmpty(op.SourcePath) ? Path.GetFileName(op.SourcePath) : op.FileId.ToString());
            string? spaceName = op.TargetSpaceId.HasValue && spacesDict.TryGetValue(op.TargetSpaceId.Value, out var targetSpace)
                ? targetSpace.Name
                : (op.TargetSpaceId.HasValue ? state.Spaces.FirstOrDefault(s => s.Id == op.TargetSpaceId.Value)?.Name : null);
            OperationHistory.Add(new OperationItemViewModel(op, fileName, spaceName, () => ExecuteUndoManualMoveAsync(op.Id)));
        }

        OnPropertyChanged(nameof(OperationHistoryCount));
        OnPropertyChanged(nameof(HasOperationHistory));
        OnPropertyChanged(nameof(ManualMoveStatusNotice));
        OnPropertyChanged(nameof(CanExecuteManualMove));
        OnPropertyChanged(nameof(ManualUndoStatusNotice));
        OnPropertyChanged(nameof(CanUndoManualMove));

        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(HasPendingItems));
        OnPropertyChanged(nameof(ManagedSpacesCount));
        OnPropertyChanged(nameof(MappedSpacesCount));
        OnPropertyChanged(nameof(ManagedSpacesCountText));
        OnPropertyChanged(nameof(MappedSpacesCountText));
    }

    // ==========================================
    // SPACES LOGIC
    // ==========================================
    partial void OnFilterModeChanged(SpaceFilterMode value)
    {
        OnPropertyChanged(nameof(IsAllFilter));
        OnPropertyChanged(nameof(IsManagedFilter));
        OnPropertyChanged(nameof(IsMappedFilter));
        ApplySpaceFilter();
    }

    [RelayCommand]
    public void SetFilter(string mode)
    {
        if (Enum.TryParse<SpaceFilterMode>(mode, true, out var m))
        {
            FilterMode = m;
        }
    }

    private void ApplySpaceFilter()
    {
        var prevSpaceId = SelectedSpace?.Id;
        FilteredSpaces.Clear();
        foreach (var s in AllSpaces)
        {
            if (FilterMode == SpaceFilterMode.All ||
                (FilterMode == SpaceFilterMode.Managed && s.IsManaged) ||
                (FilterMode == SpaceFilterMode.Mapped && s.IsMapped))
            {
                FilteredSpaces.Add(s);
            }
        }

        var matched = (prevSpaceId.HasValue ? FilteredSpaces.FirstOrDefault(s => s.Id == prevSpaceId.Value) : null)
            ?? FilteredSpaces.FirstOrDefault();
        SelectSpace(matched);
    }

    [RelayCommand]
    public void SelectSpace(SpaceItemViewModel? space)
    {
        foreach (var s in AllSpaces)
        {
            s.IsSelected = (s == space);
        }
        SelectedSpace = space;

        var prevFileId = SelectedFile?.Id;
        var matchedFile = (prevFileId.HasValue ? SelectedSpace?.Files.FirstOrDefault(f => f.Id == prevFileId.Value) : null)
            ?? SelectedSpace?.Files.FirstOrDefault();
        SelectFile(matchedFile);
    }

    [RelayCommand]
    public void OpenAddSpaceDialog()
    {
        NewSpaceName = string.Empty;
        NewSpaceDesc = string.Empty;
        NewSpaceMode = SpaceStorageMode.Managed;
        NewSpaceFolder = SettingsManagedRoot;
        SpaceDialogError = null;
        IsAddSpaceDialogOpen = true;
    }

    [RelayCommand]
    public void CloseAddSpaceDialog()
    {
        IsAddSpaceDialogOpen = false;
        SpaceDialogError = null;
    }

    [RelayCommand]
    public async Task ConfirmAddSpaceAsync()
    {
        if (NewSpaceMode == SpaceStorageMode.Managed)
        {
            if (!TrySanitizeSpaceLeafName(NewSpaceName, SettingsManagedRoot, out var safeName, out var safeFolder))
            {
                SpaceDialogError = Localizer["Validation.InvalidSpaceName"];
                return;
            }

            var space = new WorkspaceSpace(
                Guid.NewGuid(),
                safeName,
                NewSpaceDesc?.Trim() ?? string.Empty,
                NewSpaceMode,
                safeFolder
            );

            var updated = await _updateStore(state => state with
            {
                Spaces = [.. state.Spaces, space]
            });

            if (updated != null)
            {
                RefreshFromState(updated);
                var created = AllSpaces.FirstOrDefault(s => s.Id == space.Id);
                if (created != null)
                    SelectSpace(created);
            }
        }
        else // Mapped
        {
            var trimmedName = NewSpaceName?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(trimmedName) || trimmedName.Length > 160 || trimmedName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || trimmedName.Contains('/') || trimmedName.Contains('\\'))
            {
                SpaceDialogError = Localizer["Validation.InvalidSpaceName"];
                return;
            }

            var folder = NewSpaceFolder?.Trim() ?? string.Empty;
            if (!Path.IsPathFullyQualified(folder))
            {
                SpaceDialogError = Localizer["Validation.ValidAbsolutePathRequired"];
                return;
            }

            var space = new WorkspaceSpace(
                Guid.NewGuid(),
                trimmedName,
                NewSpaceDesc?.Trim() ?? string.Empty,
                NewSpaceMode,
                folder
            );

            var updated = await _updateStore(state => state with
            {
                Spaces = [.. state.Spaces, space]
            });

            if (updated != null)
            {
                RefreshFromState(updated);
                var created = AllSpaces.FirstOrDefault(s => s.Id == space.Id);
                if (created != null)
                    SelectSpace(created);
            }
        }

        IsAddSpaceDialogOpen = false;
    }

    internal async Task SaveSpaceWindowPlacementAsync(Guid spaceId, SpaceWindowPlacement placement)
    {
        var updated = await _updateStore(state => state with
        {
            Spaces = state.Spaces.Select(space => space.Id == spaceId
                ? space with { WindowPlacement = placement } : space).ToList()
        });
        RefreshFromState(updated);
    }

    [RelayCommand]
    public async Task EnrollUserFileMetadataAsync(string? filePath)
    {
        var target = SelectedSpace;
        if (target == null)
        {
            SpaceDropNotice = Localizer["Drop.NoSpaceSelected"];
            return;
        }
        if (await TryEnrollFileMetadataAsync(filePath, target.Id))
            SpaceDropNotice = Localizer.GetString("Drop.SpaceSuccessFormat", 1, target.Name);
    }

    [RelayCommand]
    public async Task CatalogSpaceAsync()
    {
        if (SelectedSpace is not { IsMapped: true } target) return;
        try
        {
            string root = Path.TrimEndingDirectorySeparator(
                DeskNest.Platform.PlatformFileActions.RequireExistingLocalPath(target.Folder));
            // One directory level and one metadata save, not a recursive watcher or import.
            var entries = await Task.Run(() =>
            {
                var result = new List<WorkspaceFile>();
                int inspected = 0;
                foreach (string path in Directory.EnumerateFileSystemEntries(root))
                {
                    if (++inspected > 100_000)
                        throw new InvalidDataException(Localizer["Spaces.CatalogLimitNotice"]);
                    string name = Path.GetFileName(path);
                    if (name.Equals(".desknest-trash", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith(".desknext-copy-", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith(".desknext-tree-", StringComparison.OrdinalIgnoreCase)) continue;
                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                    result.Add(new WorkspaceFile(Guid.NewGuid(), target.Id, name, path,
                        (attributes & FileAttributes.Directory) != 0));
                }
                return result;
            });
            int added = 0;
            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var updated = await _updateStore(state =>
            {
                var current = state.Spaces.FirstOrDefault(s => s.Id == target.Id);
                if (current?.Mode != SpaceStorageMode.Mapped || !comparer.Equals(root,
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(current.Folder))))
                    throw new InvalidDataException(Localizer["Drop.OutsideSpaceNotice"]);
                DeskNest.Platform.PlatformFileActions.RequireExistingLocalPath(root);
                var known = state.Files.Where(f => f.SpaceId == target.Id)
                    .Select(f => Path.TrimEndingDirectorySeparator(Path.GetFullPath(f.Path))).ToHashSet(comparer);
                var additions = entries.Where(f => known.Add(f.Path)).ToList();
                if (state.Files.Count + additions.Count > 100_000)
                    throw new InvalidDataException(Localizer["Spaces.CatalogLimitNotice"]);
                added = additions.Count;
                return state with { Files = [.. state.Files, .. additions] };
            });
            RefreshFromState(updated);
            SpaceDropNotice = Localizer.GetString("Drop.SpaceSuccessFormat", added, target.Name);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            SpaceDropNotice = Localizer.GetString("Files.ActionFailedNotice", error.Message);
        }
    }

    private async Task<bool> TryEnrollFileMetadataAsync(string? filePath, Guid spaceId)
    {
        bool outsideSpace = false;
        try
        {
            var path = Path.TrimEndingDirectorySeparator(
                DeskNest.Platform.PlatformFileActions.RequireExistingLocalPath(filePath?.Trim() ?? string.Empty));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var updated = await _updateStore(state =>
            {
                var target = state.Spaces.FirstOrDefault(s => s.Id == spaceId)
                    ?? throw new InvalidDataException(Localizer["Drop.NoSpaceSelected"]);
                var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.Folder));
                var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
                if (string.Equals(path, root, comparison) || !path.StartsWith(prefix, comparison))
                {
                    outsideSpace = true;
                    throw new InvalidDataException(Localizer["Drop.OutsideSpaceNotice"]);
                }
                // This only catalogs an item already inside the space; it never imports or moves it.
                if (state.Files.Any(f => f.SpaceId == spaceId && string.Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(f.Path)), path, comparison)))
                    return state;
                var file = new WorkspaceFile(Guid.NewGuid(), spaceId, Path.GetFileName(path), path, Directory.Exists(path));
                return state with { Files = [.. state.Files, file] };
            });
            if (updated == null) return false;
            RefreshFromState(updated);
            return true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            SpaceDropNotice = outsideSpace ? Localizer["Drop.OutsideSpaceNotice"]
                : Localizer.GetString("Files.ActionFailedNotice", error.Message);
            return false;
        }
    }

    [RelayCommand]
    public async Task DropPathsOnSpaceAsync(IReadOnlyList<string>? paths)
    {
        if (paths == null || paths.Count == 0)
        {
            SpaceDropNotice = Localizer["Drop.UnsupportedPayload"];
            return;
        }

        if (SelectedSpace == null)
        {
            SpaceDropNotice = Localizer["Drop.NoSpaceSelected"];
            return;
        }

        if (OnFilesDroppedOnSpace != null)
        {
            await OnFilesDroppedOnSpace(paths, SelectedSpace);
            return;
        }

        var target = SelectedSpace; // Keep the user's target even if selection changes during persistence.
        if (IsImportConfirmationOpen || IsImportBusy) return;
        if (paths.Count == 1 && OnImportPending is not null)
        {
            try
            {
                string path = Path.TrimEndingDirectorySeparator(DeskNest.Platform.PlatformFileActions.RequireExistingLocalPath(paths[0]));
                string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.Folder));
                string prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
                var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (!string.Equals(path, root, comparison) && !path.StartsWith(prefix, comparison))
                {
                    // Cataloged items already have a move command. Never re-enroll them as external imports.
                    if (AllSpaces.SelectMany(s => s.Files).Any(f => string.Equals(
                        Path.TrimEndingDirectorySeparator(Path.GetFullPath(f.Path)), path, comparison)))
                    {
                        SpaceDropNotice = Localizer["Drop.UseMoveToSpace"];
                        return;
                    }
                    var pending = PendingItems.FirstOrDefault(p => string.Equals(
                        Path.TrimEndingDirectorySeparator(Path.GetFullPath(p.Path)), path, comparison));
                    if (pending is null)
                    {
                        await RegisterPathToTriageAsync(path); // Review metadata only; confirmation owns the move.
                        pending = PendingItems.FirstOrDefault(p => string.Equals(p.Path, path, comparison));
                    }
                    if (pending is null) return;
                    pending.TargetSpace = AllSpaces.FirstOrDefault(s => s.Id == target.Id);
                    SelectedTabIndex = 1;
                    OpenImportConfirmation(pending);
                    SpaceDropNotice = Localizer["Triage.ImportNotice"];
                    return;
                }
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
            {
                SpaceDropNotice = Localizer.GetString("Files.ActionFailedNotice", error.Message);
                return;
            }
        }
        int registered = 0;
        string? refusal = null;
        foreach (var p in paths)
        {
            if (await TryEnrollFileMetadataAsync(p, target.Id)) registered++;
            else refusal = SpaceDropNotice;
        }
        SpaceDropNotice = refusal ?? Localizer.GetString("Drop.SpaceSuccessFormat", registered, target.Name);
    }

    // ==========================================
    // DROP CAPSULE LOGIC
    // ==========================================
    [RelayCommand]
    public async Task DropPathsOnCapsuleAsync(IReadOnlyList<string>? paths)
    {
        if (paths == null || paths.Count == 0)
        {
            CapsuleNotice = Localizer["Drop.UnsupportedPayload"];
            return;
        }

        if (OnFilesDroppedOnCapsule != null)
        {
            await OnFilesDroppedOnCapsule(paths);
            return;
        }

        int registered = 0;
        foreach (var p in paths)
        {
            if (Path.IsPathFullyQualified(p) && (File.Exists(p) || Directory.Exists(p)))
            {
                await RegisterPathToTriageAsync(p);
                registered++;
            }
        }

        if (registered == 0)
        {
            CapsuleNotice = Localizer["Validation.FileNotFound"];
        }
    }

    [RelayCommand]
    public async Task SubmitCapsuleAsync()
    {
        if (string.IsNullOrWhiteSpace(CapsuleInputPath))
        {
            CapsuleNotice = Localizer["Validation.ValidAbsolutePathRequired"];
            return;
        }

        var path = CapsuleInputPath.Trim();
        if (!Path.IsPathFullyQualified(path))
        {
            CapsuleNotice = Localizer["Validation.ValidAbsolutePathRequired"];
            return;
        }

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            CapsuleNotice = Localizer["Validation.FileNotFound"];
            return;
        }

        await RegisterPathToTriageAsync(path);
        CapsuleInputPath = string.Empty;
    }

    public async Task RegisterPathToTriageAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return;

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            CapsuleNotice = Localizer["Validation.FileNotFound"];
            return;
        }

        var name = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(name))
            name = path;

        var pending = new PendingFile(
            Guid.NewGuid(),
            name,
            path,
            TriageReason.FilenameAmbiguous,
            null,
            DateTimeOffset.UtcNow
        );

        var updated = await _updateStore(state => state with
        {
            Pending = [.. state.Pending, pending]
        });

        if (updated != null)
        {
            RefreshFromState(updated);
            CapsuleNotice = Localizer["Triage.P2Notice"];
        }
    }

    // ==========================================
    // TRIAGE LOGIC
    // ==========================================
    [RelayCommand]
    public async Task AssignPendingToSpaceAsync(object? parameter)
    {
        if (SelectedPendingItem == null)
            return;

        SpaceItemViewModel? targetSpace = parameter as SpaceItemViewModel
            ?? SelectedPendingItem.TargetSpace
            ?? SelectedSpace
            ?? AllSpaces.FirstOrDefault();

        if (targetSpace == null)
        {
            SelectedPendingItem.ResolutionNotice = Localizer["Triage.NoSpacesAvailable"];
            return;
        }

        var pendingId = SelectedPendingItem.Id;
        var targetSpaceId = targetSpace.Id;

        // In P2: updates metadata by setting SuggestedSpaceId on pending item.
        // Filesystem relocation is deferred to P3.
        var updated = await _updateStore(state =>
        {
            var updatedPending = state.Pending.Select(p =>
                p.Id == pendingId ? p with { SuggestedSpaceId = targetSpaceId } : p
            ).ToList();

            return state with { Pending = updatedPending };
        });

        if (updated != null)
        {
            RefreshFromState(updated);
            SelectedPendingItem = PendingItems.FirstOrDefault(p => p.Id == pendingId);
            if (SelectedPendingItem != null)
            {
                SelectedPendingItem.ResolutionNotice = Localizer["Triage.P2Notice"];
            }
        }
    }

    [RelayCommand]
    public void OpenCreateSpaceFromTriage(PendingItemViewModel? item = null)
    {
        if (item is not null) SelectedPendingItem = PendingItems.FirstOrDefault(p => p.Id == item.Id);
        if (SelectedPendingItem == null) return;
        TriageNewSpaceName = string.Empty;
        TriageNewSpaceDesc = string.Empty;
        IsCreateSpaceFromTriageOpen = true;
    }

    [RelayCommand]
    public void CloseCreateSpaceFromTriage()
    {
        IsCreateSpaceFromTriageOpen = false;
    }

    [RelayCommand]
    public async Task ConfirmCreateSpaceFromTriageAsync()
    {
        if (SelectedPendingItem == null || string.IsNullOrWhiteSpace(TriageNewSpaceName))
            return;

        if (!TrySanitizeSpaceLeafName(TriageNewSpaceName, SettingsManagedRoot, out var safeName, out var safeFolder))
            return;

        var desc = string.IsNullOrWhiteSpace(TriageNewSpaceDesc) ? safeName : TriageNewSpaceDesc.Trim();
        var newSpace = new WorkspaceSpace(Guid.NewGuid(), safeName, desc, SpaceStorageMode.Managed, safeFolder);
        var pendingId = SelectedPendingItem.Id;

        var updated = await _updateStore(state =>
        {
            var updatedPending = state.Pending.Select(p =>
                p.Id == pendingId ? p with { SuggestedSpaceId = newSpace.Id } : p
            ).ToList();

            return state with
            {
                Spaces = [.. state.Spaces, newSpace],
                Pending = updatedPending
            };
        });

        if (updated != null)
        {
            RefreshFromState(updated);
            SelectedPendingItem = PendingItems.FirstOrDefault(p => p.Id == pendingId);
            if (SelectedPendingItem != null)
            {
                SelectedPendingItem.TargetSpace = AllSpaces.Single(s => s.Id == newSpace.Id);
                SelectedPendingItem.ResolutionNotice = Localizer["Triage.P2Notice"];
            }
        }

        IsCreateSpaceFromTriageOpen = false;
    }

    [RelayCommand]
    public async Task DismissPendingItemAsync(PendingItemViewModel? item)
    {
        var target = item ?? SelectedPendingItem;
        if (target == null) return;

        var updated = await _updateStore(state => state with
        {
            Pending = state.Pending.Where(p => p.Id != target.Id).ToList()
        });

        if (updated != null)
        {
            RefreshFromState(updated);
        }
    }

    // ==========================================
    // SETTINGS LOGIC
    // ==========================================
    partial void OnSettingsLanguageChanged(LanguageInfo value)
    {
        if (value != null && !Localizer.CurrentLanguage.Equals(value.Code, StringComparison.OrdinalIgnoreCase))
        {
            Localizer.CurrentLanguage = value.Code;
        }
    }

    partial void OnSettingsThemeChanged(AppThemeMode value)
    {
        ThemeMgr.CurrentThemeMode = value;
    }

    [RelayCommand]
    public async Task ExecuteManualMoveAsync(object? parameter)
    {
        if (OnExecuteManualMove == null)
        {
            OnPropertyChanged(nameof(CanExecuteManualMove));
            OnPropertyChanged(nameof(ManualMoveStatusNotice));
            return;
        }

        try
        {
            if (parameter is ValueTuple<Guid, Guid> pair)
            {
                await OnExecuteManualMove(pair.Item1, pair.Item2);
            }
            else if (parameter is (Guid fileId, Guid targetSpaceId))
            {
                await OnExecuteManualMove(fileId, targetSpaceId);
            }
        }
        catch (Exception ex)
        {
            FileActionNotice = Localizer.GetString("Files.ActionFailedNotice", ex.Message);
        }
    }

    [RelayCommand]
    public async Task ExecuteUndoManualMoveAsync(object? parameter)
    {
        if (OnUndoManualMove == null)
        {
            OnPropertyChanged(nameof(CanUndoManualMove));
            OnPropertyChanged(nameof(ManualUndoStatusNotice));
            return;
        }

        Guid operationId = Guid.Empty;
        if (parameter is Guid id)
        {
            operationId = id;
        }
        else if (parameter is OperationItemViewModel item)
        {
            operationId = item.Id;
        }
        else if (parameter is string idStr && Guid.TryParse(idStr, out var parsed))
        {
            operationId = parsed;
        }

        if (operationId != Guid.Empty)
        {
            try
            {
                await OnUndoManualMove(operationId);
            }
            catch (Exception ex)
            {
                FileActionNotice = Localizer.GetString("Files.ActionFailedNotice", ex.Message);
            }
        }
    }

    // ==========================================
    // WORKSPACE FILE ACTIONS EXECUTION (P3 UI)
    // ==========================================
    [RelayCommand]
    public async Task ExecuteOpenFileAsync(object? parameter)
    {
        var target = ResolveFileParameter(parameter) ?? SelectedFile;
        if (target == null)
        {
            FileActionNotice = Localizer["Files.NoFileSelectedNotice"];
            return;
        }

        if (OnOpenFile == null)
        {
            FileActionNotice = OpenFileStatusNotice;
            return;
        }

        try
        {
            await OnOpenFile(target);
            if (string.IsNullOrWhiteSpace(FileActionNotice))
                FileActionNotice = OpenFileStatusNotice;
        }
        catch (Exception ex)
        {
            FileActionNotice = Localizer.GetString("Files.ActionFailedNotice", ex.Message);
        }
    }

    [RelayCommand]
    public async Task ExecuteRevealFileAsync(object? parameter)
    {
        var target = ResolveFileParameter(parameter) ?? SelectedFile;
        if (target == null)
        {
            FileActionNotice = Localizer["Files.NoFileSelectedNotice"];
            return;
        }

        if (OnRevealFile == null)
        {
            FileActionNotice = RevealFileStatusNotice;
            return;
        }

        try
        {
            await OnRevealFile(target);
            if (string.IsNullOrWhiteSpace(FileActionNotice))
                FileActionNotice = RevealFileStatusNotice;
        }
        catch (Exception ex)
        {
            FileActionNotice = Localizer.GetString("Files.ActionFailedNotice", ex.Message);
        }
    }

    [RelayCommand]
    public async Task ExecutePreviewFileAsync(object? parameter)
    {
        var target = ResolveFileParameter(parameter) ?? SelectedFile;
        if (target == null)
        {
            FileActionNotice = Localizer["Files.NoFileSelectedNotice"];
            return;
        }

        if (OnPreviewFile == null)
        {
            FileActionNotice = PreviewFileStatusNotice;
            return;
        }

        try
        {
            await OnPreviewFile(target);
            if (string.IsNullOrWhiteSpace(FileActionNotice))
                FileActionNotice = PreviewFileStatusNotice;
        }
        catch (Exception ex)
        {
            FileActionNotice = Localizer.GetString("Files.ActionFailedNotice", ex.Message);
        }
    }

    [RelayCommand]
    public async Task ExecuteCopyFileAsync(object? parameter)
    {
        if (parameter is System.Collections.IEnumerable list && !(parameter is string))
        {
            int count = 0;
            WorkspaceFileItemViewModel? singleItem = null;
            foreach (var item in list)
            {
                if (item is WorkspaceFileItemViewModel f)
                {
                    count++;
                    singleItem = f;
                }
            }
            if (count > 1)
            {
                FileActionNotice = Localizer["Files.ClipboardMultiFileRejected"];
                return;
            }
            if (count == 1 && singleItem != null)
            {
                parameter = singleItem;
            }
        }

        var target = ResolveFileParameter(parameter) ?? SelectedFile;
        if (target == null)
        {
            FileActionNotice = Localizer["Files.NoFileSelectedNotice"];
            return;
        }

        if (OnCopyFile == null)
        {
            FileActionNotice = CopyFileStatusNotice;
            return;
        }

        try
        {
            await OnCopyFile(target);
            if (string.IsNullOrWhiteSpace(FileActionNotice))
                FileActionNotice = CopyFileStatusNotice;
        }
        catch (Exception ex)
        {
            FileActionNotice = Localizer.GetString("Files.ActionFailedNotice", ex.Message);
        }
    }

    [RelayCommand]
    public async Task ExecuteCutFileAsync(object? parameter)
    {
        if (parameter is System.Collections.IEnumerable list && !(parameter is string))
        {
            int count = 0;
            WorkspaceFileItemViewModel? singleItem = null;
            foreach (var item in list)
            {
                if (item is WorkspaceFileItemViewModel f)
                {
                    count++;
                    singleItem = f;
                }
            }
            if (count > 1)
            {
                FileActionNotice = Localizer["Files.ClipboardMultiFileRejected"];
                return;
            }
            if (count == 1 && singleItem != null)
            {
                parameter = singleItem;
            }
        }

        var target = ResolveFileParameter(parameter) ?? SelectedFile;
        if (target == null)
        {
            FileActionNotice = Localizer["Files.NoFileSelectedNotice"];
            return;
        }

        if (OnCutFile == null)
        {
            FileActionNotice = CutFileStatusNotice;
            return;
        }

        try
        {
            await OnCutFile(target);
            if (string.IsNullOrWhiteSpace(FileActionNotice))
                FileActionNotice = CutFileStatusNotice;
        }
        catch (Exception ex)
        {
            FileActionNotice = Localizer.GetString("Files.ActionFailedNotice", ex.Message);
        }
    }

    [RelayCommand]
    public async Task ExecutePasteFileAsync(object? parameter)
    {
        var targetSpace = parameter as SpaceItemViewModel ?? SelectedSpace;
        if (targetSpace == null)
        {
            FileActionNotice = Localizer["Files.NoSpaceSelectedNotice"];
            return;
        }

        if (OnPasteFile == null)
        {
            FileActionNotice = PasteFileStatusNotice;
            return;
        }

        try
        {
            await OnPasteFile(targetSpace);
            if (string.IsNullOrWhiteSpace(FileActionNotice))
                FileActionNotice = PasteFileStatusNotice;
        }
        catch (Exception ex)
        {
            FileActionNotice = Localizer.GetString("Files.ActionFailedNotice", ex.Message);
        }
    }

    [RelayCommand]
    public async Task ExecuteRenameFileAsync(object? parameter)
    {
        WorkspaceFileItemViewModel? target = null;
        string? newName = null;

        if (parameter is ValueTuple<WorkspaceFileItemViewModel, string> pair)
        {
            target = pair.Item1;
            newName = pair.Item2;
        }
        else if (parameter is (WorkspaceFileItemViewModel f, string n))
        {
            target = f;
            newName = n;
        }
        else if (parameter is WorkspaceFileItemViewModel file)
        {
            OpenRenameDialog(file);
            return;
        }
        else if (parameter == null && SelectedFile != null)
        {
            OpenRenameDialog(SelectedFile);
            return;
        }

        if (target == null)
        {
            FileActionNotice = Localizer["Files.NoFileSelectedNotice"];
            return;
        }

        if (string.IsNullOrWhiteSpace(newName))
            return;

        if (OnRenameFile == null)
        {
            FileActionNotice = RenameFileStatusNotice;
            return;
        }

        try
        {
            await OnRenameFile(target, newName);
            FileActionNotice = RenameFileStatusNotice;
            RenameDialogError = null;
        }
        catch (Exception ex)
        {
            FileActionNotice = Localizer.GetString("Files.ActionFailedNotice", ex.Message);
            RenameDialogError = ex.Message;
        }
    }

    [RelayCommand]
    public async Task ExecuteDeleteFileAsync(object? parameter)
    {
        if (IsDeleteConfirmationBusy)
            return;

        if (parameter is ValueTuple<WorkspaceFileItemViewModel, bool> (WorkspaceFileItemViewModel f, bool confirmed) && confirmed)
        {
            if (OnDeleteFile == null)
            {
                FileActionNotice = DeleteFileStatusNotice;
                return;
            }

            try
            {
                IsDeleteConfirmationBusy = true;
                await OnDeleteFile(f);
                FileActionNotice = Localizer.GetString(f.IsManaged
                    ? "Files.DeleteManagedSuccessNotice"
                    : "Files.DeleteMappedSuccessNotice", f.Name);
            }
            catch (Exception ex)
            {
                FileActionNotice = Localizer.GetString("Files.ActionFailedNotice", ex.Message);
            }
            finally
            {
                IsDeleteConfirmationBusy = false;
            }
            return;
        }

        var target = ResolveFileParameter(parameter) ?? SelectedFile;
        if (target == null)
        {
            FileActionNotice = Localizer["Files.NoFileSelectedNotice"];
            return;
        }

        if (OnDeleteFile == null)
        {
            FileActionNotice = DeleteFileStatusNotice;
            return;
        }

        if (target.IsManaged)
        {
            // Managed delete requires explicit confirmation to move to app recovery area
            OpenDeleteConfirmationDialog(target);
            return;
        }

        // Mapped file removal unmaps reference without touching source file
        try
        {
            IsDeleteConfirmationBusy = true;
            SelectFile(target);
            await OnDeleteFile(target);
            FileActionNotice = Localizer.GetString("Files.DeleteMappedSuccessNotice", target.Name);
        }
        catch (Exception ex)
        {
            FileActionNotice = Localizer.GetString("Files.ActionFailedNotice", ex.Message);
        }
        finally
        {
            IsDeleteConfirmationBusy = false;
        }
    }

    [RelayCommand]
    public void OpenRenameDialog(object? parameter)
    {
        var target = ResolveFileParameter(parameter) ?? SelectedFile;
        if (target == null)
        {
            FileActionNotice = Localizer["Files.NoFileSelectedNotice"];
            return;
        }

        RenamingFile = target;
        RenameItemName = target.Name;
        RenameDialogError = null;
        IsRenameDialogOpen = true;
    }

    [RelayCommand]
    public void CloseRenameDialog()
    {
        IsRenameDialogOpen = false;
        RenamingFile = null;
        RenameDialogError = null;
    }

    [RelayCommand]
    public async Task ConfirmRenameAsync()
    {
        if (RenamingFile == null)
        {
            IsRenameDialogOpen = false;
            return;
        }

        var trimmed = RenameItemName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed) || trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || trimmed.Contains('/') || trimmed.Contains('\\'))
        {
            RenameDialogError = Localizer["Validation.InvalidSpaceName"];
            return;
        }

        var file = RenamingFile;
        try
        {
            await ExecuteRenameFileAsync((file, trimmed));
            if (string.IsNullOrWhiteSpace(RenameDialogError))
            {
                IsRenameDialogOpen = false;
                RenamingFile = null;
                RenameDialogError = null;
            }
        }
        catch (Exception ex)
        {
            RenameDialogError = ex.Message;
        }
    }

    // ==========================================
    // DELETE CONFIRMATION DIALOG METHODS (P3 UI)
    // ==========================================
    [RelayCommand]
    public void OpenDeleteConfirmationDialog(object? parameter = null)
    {
        if (IsDeleteConfirmationBusy)
            return;

        var target = ResolveFileParameter(parameter) ?? SelectedFile;
        if (target == null)
        {
            FileActionNotice = Localizer["Files.NoFileSelectedNotice"];
            return;
        }

        SelectFile(target);
        DeletingFile = target;
        DeleteConfirmationDialogError = null;
        OnPropertyChanged(nameof(DeleteConfirmTitleText));
        OnPropertyChanged(nameof(DeleteConfirmPromptText));
        OnPropertyChanged(nameof(DeleteConfirmActionText));
        IsDeleteConfirmationDialogOpen = true;
    }

    [RelayCommand]
    public void CloseDeleteConfirmationDialog()
    {
        // User cancel command: blocked while deletion execution is in flight
        if (IsDeleteConfirmationBusy)
            return;

        DismissDeleteConfirmationDialog();
    }

    private void DismissDeleteConfirmationDialog()
    {
        IsDeleteConfirmationDialogOpen = false;
        DeletingFile = null;
        DeleteConfirmationDialogError = null;
    }

    [RelayCommand]
    public async Task ConfirmDeleteFileAsync()
    {
        if (IsDeleteConfirmationBusy)
            return;

        if (DeletingFile == null)
        {
            DismissDeleteConfirmationDialog();
            return;
        }

        var file = DeletingFile;
        if (OnDeleteFile == null)
        {
            FileActionNotice = DeleteFileStatusNotice;
            DismissDeleteConfirmationDialog();
            return;
        }

        try
        {
            IsDeleteConfirmationBusy = true;
            await OnDeleteFile(file);

            FileActionNotice = Localizer.GetString(file.IsManaged
                ? "Files.DeleteManagedSuccessNotice"
                : "Files.DeleteMappedSuccessNotice", file.Name);

            // Busy resets before programmatic close
            IsDeleteConfirmationBusy = false;
            DismissDeleteConfirmationDialog();
        }
        catch (Exception ex)
        {
            IsDeleteConfirmationBusy = false;

            // Re-verify whether the target file still exists in workspace after failed operation
            var targetStillExists = SelectedSpace?.Files.Any(f => f.Id == file.Id) == true ||
                                    AllSpaces.SelectMany(s => s.Files).Any(f => f.Id == file.Id);
            if (!targetStillExists)
            {
                // Target has vanished: dismiss modal, do not retain stale enabled target after failure
                DismissDeleteConfirmationDialog();
            }
            else
            {
                DeleteConfirmationDialogError = ex.Message;
            }

            FileActionNotice = Localizer.GetString("Files.ActionFailedNotice", ex.Message);
        }
        finally
        {
            IsDeleteConfirmationBusy = false;
        }
    }

    private WorkspaceFileItemViewModel? ResolveFileParameter(object? parameter)
    {
        if (parameter is WorkspaceFileItemViewModel f) return f;
        if (parameter is Guid id) return SelectedSpace?.Files.FirstOrDefault(x => x.Id == id);
        return null;
    }

    [RelayCommand]
    public void AddSettingsMonitoredFolder()
    {
        if (string.IsNullOrWhiteSpace(NewSettingsMonitoredFolder)) return;
        var p = Path.GetFullPath(NewSettingsMonitoredFolder.Trim());
        if (!SettingsMonitoredFolders.Contains(p))
            SettingsMonitoredFolders.Add(p);
        NewSettingsMonitoredFolder = string.Empty;
    }

    [RelayCommand]
    public void RemoveSettingsMonitoredFolder(string? path)
    {
        if (path != null) SettingsMonitoredFolders.Remove(path);
    }

    [RelayCommand]
    public void AddSettingsExcludedFolder()
    {
        if (string.IsNullOrWhiteSpace(NewSettingsExcludedFolder)) return;
        var p = Path.GetFullPath(NewSettingsExcludedFolder.Trim());
        if (!SettingsExcludedFolders.Contains(p))
            SettingsExcludedFolders.Add(p);
        NewSettingsExcludedFolder = string.Empty;
    }

    [RelayCommand]
    public void RemoveSettingsExcludedFolder(string? path)
    {
        if (path != null) SettingsExcludedFolders.Remove(path);
    }

    [RelayCommand]
    public async Task SaveSettingsAsync()
    {
        SettingsSavedFeedback = null;
        var root = Path.IsPathFullyQualified(SettingsManagedRoot)
            ? SettingsManagedRoot
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "DeskNest", "Spaces");

        var cache = string.IsNullOrWhiteSpace(SettingsModelCache) ? null : SettingsModelCache.Trim();
        if (cache is not null && !Path.IsPathFullyQualified(cache))
        {
            SettingsSavedFeedback = Localizer["Validation.ValidAbsolutePathRequired"];
            return; // Do not silently erase a previously configured model directory.
        }

        var updated = await _updateStore(state => state with
        {
            Settings = state.Settings with
            {
                Language = SettingsLanguage.Code,
                Theme = SettingsTheme.ToString(),
                Provider = SettingsProvider,
                ModelCacheDirectory = cache,
                ManagedRoot = root,
                MonitoredFolders = SettingsMonitoredFolders.Where(Path.IsPathFullyQualified).ToList(),
                ExcludedFolders = SettingsExcludedFolders.Where(Path.IsPathFullyQualified).ToList(),
                WantsMonitoring = SettingsWantsMonitoring
            }
        });

        if (updated != null)
        {
            SettingsSavedFeedback = Localizer["Settings.SavedToast"];
        }
    }

    [RelayCommand]
    public async Task ResetOnboardingAsync()
    {
        await _updateStore(state => state with
        {
            OnboardingComplete = false,
            OnboardingStep = 1
        });

        OnRequestReopenOnboarding?.Invoke();
    }

    [RelayCommand]
    public void SelectTab(object? parameter)
    {
        if (parameter is int idx)
            SelectedTabIndex = idx;
        else if (parameter is string str && int.TryParse(str, out var parsed))
            SelectedTabIndex = parsed;
    }
}
