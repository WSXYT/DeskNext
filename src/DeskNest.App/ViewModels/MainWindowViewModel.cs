using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskNest.App.Localization;
using DeskNest.App.Services;
using DeskNest.App.Themes;
using DeskNest.Core.Workspace;

namespace DeskNest.App.ViewModels;

public enum StartupState
{
    Loading,
    Ready,
    LockConflict,
    RecoveryRequired,
    Error
}

public sealed partial class MainWindowViewModel : ViewModelBase, IAsyncDisposable
{
    private WorkspaceStore? _store;
    private string? _dataDirectory;

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
    private bool _isLoading = true;

    [ObservableProperty]
    private StartupState _startupState = StartupState.Loading;

    [ObservableProperty]
    private string _startupErrorMessage = string.Empty;

    public bool HasStartupError => StartupState is StartupState.LockConflict or StartupState.RecoveryRequired or StartupState.Error;
    public bool IsLockConflict => StartupState == StartupState.LockConflict;
    public bool IsRecoveryRequired => StartupState == StartupState.RecoveryRequired;

    [ObservableProperty]
    private bool _isOnboardingActive;

    [ObservableProperty]
    private bool _isStudioActive;

    [ObservableProperty]
    private OnboardingViewModel? _onboarding;

    [ObservableProperty]
    private StudioViewModel? _studio;

    [ObservableProperty]
    private LanguageInfo _selectedLanguage;

    [ObservableProperty]
    private AppThemeMode _selectedTheme;

    // Preserved for P1 probe compatibility
    public SystemProbeViewModel Probe { get; }
    public ObservableCollection<SpacePreviewItemViewModel> RepresentativeSpaces { get; } = new();

    public FlowDirection CurrentFlowDirection => Localizer.FlowDirectionValue;

    /// <summary>
    /// Default constructor for UI startup and XAML designer.
    /// Connects to the default WorkspaceStore.
    /// </summary>
    public MainWindowViewModel()
    {
        _selectedLanguage = Localizer.CurrentLanguageInfo;
        _selectedTheme = ThemeMgr.CurrentThemeMode;
        Probe = new SystemProbeViewModel();

        Localizer.LanguageChanged += OnLanguageChanged;
        Localizer.PropertyChanged += (s, e) =>
        {
            OnPropertyChanged(nameof(CurrentFlowDirection));
            OnPropertyChanged(nameof(Localizer));
        };

        ThemeMgr.ThemeChanged += (s, mode) =>
        {
            _selectedTheme = mode;
            OnPropertyChanged(nameof(SelectedTheme));
        };

        _ = InitializeWorkspaceAsync();
    }

    /// <summary>
    /// Test or direct injection constructor with an explicit WorkspaceStore.
    /// </summary>
    public MainWindowViewModel(WorkspaceStore store)
    {
        _store = store;
        _selectedLanguage = Localizer.CurrentLanguageInfo;
        _selectedTheme = ThemeMgr.CurrentThemeMode;
        Probe = new SystemProbeViewModel();

        Localizer.LanguageChanged += OnLanguageChanged;
        Localizer.PropertyChanged += (s, e) =>
        {
            OnPropertyChanged(nameof(CurrentFlowDirection));
            OnPropertyChanged(nameof(Localizer));
        };

        ThemeMgr.ThemeChanged += (s, mode) =>
        {
            _selectedTheme = mode;
            OnPropertyChanged(nameof(SelectedTheme));
        };

        ApplySnapshot(store.Snapshot);
        IsLoading = false;
        StartupState = StartupState.Ready;
    }

    /// <summary>
    /// Test or diagnostic constructor for demonstrating visible startup errors.
    /// </summary>
    public MainWindowViewModel(StartupState errorState, string errorMessage)
    {
        _startupState = errorState;
        _startupErrorMessage = errorMessage;
        _isLoading = false;
        _selectedLanguage = Localizer.CurrentLanguageInfo;
        _selectedTheme = ThemeMgr.CurrentThemeMode;
        Probe = new SystemProbeViewModel();
    }

    public async Task InitializeWorkspaceAsync(string? customDataDir = null)
    {
        _dataDirectory = customDataDir;
        IsLoading = true;
        try
        {
            _store = await WorkspaceStore.OpenAsync(customDataDir);
            StartupState = StartupState.Ready;
            StartupErrorMessage = string.Empty;
            ApplySnapshot(_store.Snapshot);
        }
        catch (IOException ex)
        {
            StartupState = StartupState.LockConflict;
            StartupErrorMessage = ex.Message;
        }
        catch (InvalidDataException ex)
        {
            StartupState = StartupState.RecoveryRequired;
            StartupErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            StartupState = StartupState.Error;
            StartupErrorMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(HasStartupError));
            OnPropertyChanged(nameof(IsLockConflict));
            OnPropertyChanged(nameof(IsRecoveryRequired));
            OnPropertyChanged(nameof(StatusDotColor));
            OnPropertyChanged(nameof(StatusTitleText));
            OnPropertyChanged(nameof(StatusNoticeText));
        }
    }

    private void ApplySnapshot(WorkspaceState state)
    {
        // Synchronize language and theme
        if (!string.IsNullOrWhiteSpace(state.Settings.Language))
        {
            var match = SupportedLanguages.FirstOrDefault(l => l.Code.Equals(state.Settings.Language, StringComparison.OrdinalIgnoreCase));
            if (match != null && !Localizer.CurrentLanguage.Equals(match.Code, StringComparison.OrdinalIgnoreCase))
            {
                Localizer.CurrentLanguage = match.Code;
                SelectedLanguage = match;
            }
        }

        if (Enum.TryParse<AppThemeMode>(state.Settings.Theme, out var themeMode))
        {
            ThemeMgr.CurrentThemeMode = themeMode;
            SelectedTheme = themeMode;
        }

        // Maintain RepresentativeSpaces for backward compatibility
        RepresentativeSpaces.Clear();
        foreach (var s in state.Spaces)
        {
            RepresentativeSpaces.Add(new SpacePreviewItemViewModel(
                id: s.Id.ToString(),
                name: s.Name,
                description: s.Description,
                mode: s.Mode == SpaceStorageMode.Managed ? SpaceMode.Managed : SpaceMode.Mapped,
                physicalPath: s.Folder,
                itemCount: state.Files.Count(f => f.SpaceId == s.Id),
                status: s.Mode == SpaceStorageMode.Managed ? "Spaces.BadgeReady" : "Spaces.BadgeReadOnly",
                boundsSummary: s.Mode == SpaceStorageMode.Managed ? "收纳受控于本目录" : "映射外部既有目录",
                rulesSummary: "【P2 边界说明】Core 管控元数据，无实际文件移动。"
            ));
        }

        if (!state.OnboardingComplete)
        {
            IsOnboardingActive = true;
            IsStudioActive = false;
            Studio = null;
            if (Onboarding == null)
            {
                Onboarding = new OnboardingViewModel(state, UpdateStoreAsync);
            }
        }
        else
        {
            IsOnboardingActive = false;
            IsStudioActive = true;
            Onboarding = null;
            if (Studio == null)
            {
                Studio = new StudioViewModel(state, UpdateStoreAsync);
            }
            else
            {
                Studio.RefreshFromState(state);
            }
        }
    }

    public async Task<WorkspaceState> UpdateStoreAsync(Func<WorkspaceState, WorkspaceState> update)
    {
        if (_store == null) throw new InvalidOperationException("WorkspaceStore is not open.");
        var next = await _store.UpdateAsync(update);
        ApplySnapshot(next);
        return next;
    }

    partial void OnSelectedLanguageChanged(LanguageInfo value)
    {
        if (value != null && !Localizer.CurrentLanguage.Equals(value.Code, StringComparison.OrdinalIgnoreCase))
        {
            Localizer.CurrentLanguage = value.Code;
        }
    }

    partial void OnSelectedThemeChanged(AppThemeMode value)
    {
        if (ThemeMgr.CurrentThemeMode != value)
        {
            ThemeMgr.CurrentThemeMode = value;
        }
    }

    public string StatusDotColor => HasStartupError ? "#EF4444" : "#10B981";
    public string StatusTitleText => HasStartupError ? Localizer["Status.Error"] : Localizer["Status.Ready"];
    public string StatusNoticeText => HasStartupError ? Localizer["Status.NoticeError"] : Localizer["Status.NoticeP2"];

    [RelayCommand]
    public async Task RetryStartupAsync()
    {
        await InitializeWorkspaceAsync(_dataDirectory);
    }

    [RelayCommand]
    public void ExitApplication()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    [RelayCommand]
    public void OpenDataFolder()
    {
        try
        {
            var dir = _dataDirectory ?? WorkspaceStore.DefaultDataDirectory();
            if (Directory.Exists(dir))
            {
                Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
            }
        }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_store != null)
        {
            await _store.DisposeAsync();
            _store = null;
        }
    }

    private void OnLanguageChanged(object? sender, string langCode)
    {
        foreach (var lang in SupportedLanguages)
        {
            if (lang.Code.Equals(langCode, StringComparison.OrdinalIgnoreCase))
            {
                SelectedLanguage = lang;
                break;
            }
        }
        OnPropertyChanged(nameof(CurrentFlowDirection));
    }
}
