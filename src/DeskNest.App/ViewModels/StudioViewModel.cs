using System;
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
    private SpaceStorageMode _newSpaceMode = SpaceStorageMode.Managed;

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

    public void AttachManualUndoExecutor(Func<Guid, Task> executor)
    {
        OnUndoManualMove = executor ?? throw new ArgumentNullException(nameof(executor));
        OnPropertyChanged(nameof(CanUndoManualMove));
        OnPropertyChanged(nameof(ManualUndoStatusNotice));
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
    private InferenceProvider _settingsProvider;

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

        Localizer.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ManualMoveStatusNotice));
            OnPropertyChanged(nameof(ManualUndoStatusNotice));
        };

        // Load spaces and files
        RefreshFromState(state);
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
        var prevSpaceId = SelectedSpace?.Id;
        var prevPendingId = SelectedPendingItem?.Id;

        AllSpaces.Clear();
        var spacesDict = new Dictionary<Guid, SpaceItemViewModel>();

        foreach (var s in state.Spaces)
        {
            var filesInSpace = state.Files.Where(f => f.SpaceId == s.Id).ToList();
            var spaceVm = new SpaceItemViewModel(s, filesInSpace.Count);
            foreach (var f in filesInSpace)
            {
                spaceVm.Files.Add(new WorkspaceFileItemViewModel(f.Id, f.SpaceId, f.Name, f.Path, f.IsDirectory));
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

    [RelayCommand]
    public async Task EnrollUserFileMetadataAsync(string? filePath)
    {
        if (SelectedSpace == null || string.IsNullOrWhiteSpace(filePath)) return;
        var path = filePath.Trim();
        if (!Path.IsPathFullyQualified(path) || (!File.Exists(path) && !Directory.Exists(path)))
        {
            return;
        }

        var name = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(name)) name = path;
        bool isDir = Directory.Exists(path);

        var file = new WorkspaceFile(Guid.NewGuid(), SelectedSpace.Id, name, path, isDir);
        var updated = await _updateStore(state => state with
        {
            Files = [.. state.Files, file]
        });

        if (updated != null)
        {
            RefreshFromState(updated);
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

        int registered = 0;
        foreach (var p in paths)
        {
            if (Path.IsPathFullyQualified(p) && (File.Exists(p) || Directory.Exists(p)))
            {
                await EnrollUserFileMetadataAsync(p);
                registered++;
            }
        }

        if (registered > 0)
        {
            SpaceDropNotice = Localizer.GetString("Drop.SpaceSuccessFormat", registered, SelectedSpace.Name);
        }
        else
        {
            SpaceDropNotice = Localizer["Validation.FileNotFound"];
        }
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
    public void OpenCreateSpaceFromTriage()
    {
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

        if (parameter is ValueTuple<Guid, Guid> pair)
        {
            await OnExecuteManualMove(pair.Item1, pair.Item2);
        }
        else if (parameter is (Guid fileId, Guid targetSpaceId))
        {
            await OnExecuteManualMove(fileId, targetSpaceId);
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
            await OnUndoManualMove(operationId);
        }
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

        var cache = string.IsNullOrWhiteSpace(SettingsModelCache)
            ? null
            : (Path.IsPathFullyQualified(SettingsModelCache) ? SettingsModelCache : null);

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
