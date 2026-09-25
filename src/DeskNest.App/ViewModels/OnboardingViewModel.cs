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

public sealed partial class CategoryDraftItem : ObservableObject
{
    [ObservableProperty]
    private Guid _id;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private SpaceStorageMode _mode = SpaceStorageMode.Managed;

    [ObservableProperty]
    private string _folder = string.Empty;

    public bool IsManaged => Mode == SpaceStorageMode.Managed;
    public string ModeBadgeKey => IsManaged ? "Spaces.BadgeManaged" : "Spaces.BadgeMapped";
    public string ModeLocalized => Localization.LocalizationManager.Instance[ModeBadgeKey];

    partial void OnModeChanged(SpaceStorageMode value)
    {
        OnPropertyChanged(nameof(IsManaged));
        OnPropertyChanged(nameof(ModeBadgeKey));
        OnPropertyChanged(nameof(ModeLocalized));
    }

    public CategoryDraftItem(Guid id, string name, string description, SpaceStorageMode mode, string folder)
    {
        _id = id;
        _name = name;
        _description = description;
        _mode = mode;
        _folder = folder;
    }
}

public sealed partial class OnboardingViewModel : ViewModelBase
{
    private readonly Func<Func<WorkspaceState, WorkspaceState>, Task<WorkspaceState>> _updateStore;
    public Action? OnCompleted { get; set; }

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
    private int _currentStep = 1;

    public bool IsStep1 => CurrentStep == 1;
    public bool IsStep2 => CurrentStep == 2;
    public bool IsStep3 => CurrentStep == 3;
    public bool IsStep4 => CurrentStep == 4;
    public bool IsStep5 => CurrentStep == 5;
    public bool CanGoBack => CurrentStep > 1;
    public bool CanGoNext => CurrentStep < 5;
    public bool IsCompleteStep => CurrentStep == 5;

    public string Step1Bg => IsStep1 ? "#58A6FF" : (CurrentStep > 1 ? "#238636" : "#30363D");
    public string Step2Bg => IsStep2 ? "#58A6FF" : (CurrentStep > 2 ? "#238636" : "#30363D");
    public string Step3Bg => IsStep3 ? "#58A6FF" : (CurrentStep > 3 ? "#238636" : "#30363D");
    public string Step4Bg => IsStep4 ? "#58A6FF" : (CurrentStep > 4 ? "#238636" : "#30363D");
    public string Step5Bg => IsStep5 ? "#58A6FF" : "#30363D";

    partial void OnCurrentStepChanged(int value)
    {
        OnPropertyChanged(nameof(IsStep1));
        OnPropertyChanged(nameof(IsStep2));
        OnPropertyChanged(nameof(IsStep3));
        OnPropertyChanged(nameof(IsStep4));
        OnPropertyChanged(nameof(IsStep5));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(IsCompleteStep));
        OnPropertyChanged(nameof(Step1Bg));
        OnPropertyChanged(nameof(Step2Bg));
        OnPropertyChanged(nameof(Step3Bg));
        OnPropertyChanged(nameof(Step4Bg));
        OnPropertyChanged(nameof(Step5Bg));
    }

    // Step 1: Language & Theme
    [ObservableProperty]
    private LanguageInfo _selectedLanguage;

    [ObservableProperty]
    private AppThemeMode _selectedTheme;

    // Step 2: Inference Provider & Cache
    [ObservableProperty]
    private InferenceProvider _selectedProvider = InferenceProvider.Laya;

    [ObservableProperty]
    private string _modelCacheDirectory = string.Empty;

    public bool IsLayaSelected => SelectedProvider == InferenceProvider.Laya;
    public bool IsJevSelected => SelectedProvider == InferenceProvider.Jev;

    // Step 3: Presets & Categories
    [ObservableProperty]
    private string _selectedPreset = "office";

    public ObservableCollection<CategoryDraftItem> Categories { get; } = new();

    [ObservableProperty]
    private string _newCategoryName = string.Empty;

    [ObservableProperty]
    private string _newCategoryDesc = string.Empty;

    // Step 4: Storage & Monitoring Preferences
    [ObservableProperty]
    private string _managedRoot = string.Empty;

    partial void OnManagedRootChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value) && Path.IsPathFullyQualified(value))
        {
            foreach (var c in Categories.Where(c => c.IsManaged))
            {
                c.Folder = Path.Combine(value, c.Name);
            }
        }
    }

    [ObservableProperty]
    private string _newMonitoredPath = string.Empty;

    [ObservableProperty]
    private string _newExcludedPath = string.Empty;

    public ObservableCollection<string> MonitoredFolders { get; } = new();
    public ObservableCollection<string> ExcludedFolders { get; } = new();

    [ObservableProperty]
    private bool _wantsMonitoring;

    // Step 5: Review Summary
    public string SummaryLanguage => SelectedLanguage?.DisplayText ?? "zh-CN";
    public string SummaryTheme => SelectedTheme.ToString();
    public string SummaryProvider => SelectedProvider == InferenceProvider.Laya
        ? Localizer["OOBE.Step2.Laya"] : Localizer["OOBE.Step2.Jev"];
    public int SummaryCategoryCount => Categories.Count;
    public string SummaryCategoryCountText => Localizer.GetString("Spaces.ItemCountFormat", SummaryCategoryCount);
    public string SummaryCategoryList => string.Join(", ", Categories.Select(c => c.Name));
    public string SummaryMonitoringNotice => WantsMonitoring
        ? Localizer["OOBE.Step4.MonitoringNotice"]
        : Localizer["Status.Disabled"];

    [ObservableProperty]
    private string? _validationError;

    public OnboardingViewModel(WorkspaceState state, Func<Func<WorkspaceState, WorkspaceState>, Task<WorkspaceState>> updateStore)
    {
        _updateStore = updateStore;

        // Restore language and theme
        _selectedLanguage = LocalizationManager.SupportedLanguages.FirstOrDefault(
            l => l.Code.Equals(state.Settings.Language, StringComparison.OrdinalIgnoreCase))
            ?? LocalizationManager.SupportedLanguages[0];

        _selectedTheme = Enum.TryParse<AppThemeMode>(state.Settings.Theme, out var themeMode)
            ? themeMode : AppThemeMode.Dark;

        // Restore provider and cache
        _selectedProvider = state.Settings.Provider;
        _modelCacheDirectory = state.Settings.ModelCacheDirectory
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskNest", "models");

        // Restore managed root
        _managedRoot = string.IsNullOrWhiteSpace(state.Settings.ManagedRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "DeskNest", "Spaces")
            : state.Settings.ManagedRoot;

        // Restore monitored/excluded
        foreach (var m in state.Settings.MonitoredFolders)
            MonitoredFolders.Add(m);
        foreach (var e in state.Settings.ExcludedFolders)
            ExcludedFolders.Add(e);
        _wantsMonitoring = state.Settings.WantsMonitoring;

        // Restore preset and categories without duplication
        _selectedPreset = string.IsNullOrWhiteSpace(state.Preset) ? "office" : state.Preset;
        if (state.Spaces != null && state.Spaces.Count > 0)
        {
            foreach (var s in state.Spaces)
            {
                Categories.Add(new CategoryDraftItem(s.Id, s.Name, s.Description, s.Mode, s.Folder));
            }
        }
        else
        {
            LoadPresetCategories(_selectedPreset);
        }

        // Restore current step (resumable: 1 to 5)
        _currentStep = state.OnboardingStep is >= 1 and <= 5 ? state.OnboardingStep : 1;
    }

    partial void OnSelectedLanguageChanged(LanguageInfo value)
    {
        if (value != null && !Localizer.CurrentLanguage.Equals(value.Code, StringComparison.OrdinalIgnoreCase))
        {
            Localizer.CurrentLanguage = value.Code;
            OnPropertyChanged(nameof(SummaryLanguage));
            OnPropertyChanged(nameof(SummaryProvider));
            OnPropertyChanged(nameof(SummaryMonitoringNotice));
            OnPropertyChanged(nameof(SummaryCategoryCountText));
        }
    }

    partial void OnSelectedThemeChanged(AppThemeMode value)
    {
        ThemeMgr.CurrentThemeMode = value;
        OnPropertyChanged(nameof(SummaryTheme));
    }

    partial void OnSelectedProviderChanged(InferenceProvider value)
    {
        OnPropertyChanged(nameof(IsLayaSelected));
        OnPropertyChanged(nameof(IsJevSelected));
        OnPropertyChanged(nameof(SummaryProvider));
    }

    [RelayCommand]
    public void SelectProvider(InferenceProvider provider)
    {
        SelectedProvider = provider;
    }

    [RelayCommand]
    public void SelectPreset(string preset)
    {
        SelectedPreset = preset;
        LoadPresetCategories(preset);
        OnPropertyChanged(nameof(SummaryCategoryCount));
        OnPropertyChanged(nameof(SummaryCategoryList));
    }

    private void LoadPresetCategories(string preset)
    {
        Categories.Clear();
        var root = string.IsNullOrWhiteSpace(ManagedRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "DeskNest", "Spaces")
            : ManagedRoot;

        switch (preset)
        {
            case "office":
                Categories.Add(new CategoryDraftItem(Guid.NewGuid(), Localizer["Preset.Office.Contracts.Name"], Localizer["Preset.Office.Contracts.Desc"], SpaceStorageMode.Managed, Path.Combine(root, Localizer["Preset.Office.Contracts.Name"])));
                Categories.Add(new CategoryDraftItem(Guid.NewGuid(), Localizer["Preset.Office.Financials.Name"], Localizer["Preset.Office.Financials.Desc"], SpaceStorageMode.Managed, Path.Combine(root, Localizer["Preset.Office.Financials.Name"])));
                Categories.Add(new CategoryDraftItem(Guid.NewGuid(), Localizer["Preset.Office.Presentations.Name"], Localizer["Preset.Office.Presentations.Desc"], SpaceStorageMode.Managed, Path.Combine(root, Localizer["Preset.Office.Presentations.Name"])));
                Categories.Add(new CategoryDraftItem(Guid.NewGuid(), Localizer["Preset.Office.Reference.Name"], Localizer["Preset.Office.Reference.Desc"], SpaceStorageMode.Managed, Path.Combine(root, Localizer["Preset.Office.Reference.Name"])));
                break;
            case "development":
                Categories.Add(new CategoryDraftItem(Guid.NewGuid(), Localizer["Preset.Dev.Source.Name"], Localizer["Preset.Dev.Source.Desc"], SpaceStorageMode.Managed, Path.Combine(root, Localizer["Preset.Dev.Source.Name"])));
                Categories.Add(new CategoryDraftItem(Guid.NewGuid(), Localizer["Preset.Dev.Docs.Name"], Localizer["Preset.Dev.Docs.Desc"], SpaceStorageMode.Managed, Path.Combine(root, Localizer["Preset.Dev.Docs.Name"])));
                Categories.Add(new CategoryDraftItem(Guid.NewGuid(), Localizer["Preset.Dev.Build.Name"], Localizer["Preset.Dev.Build.Desc"], SpaceStorageMode.Managed, Path.Combine(root, Localizer["Preset.Dev.Build.Name"])));
                Categories.Add(new CategoryDraftItem(Guid.NewGuid(), Localizer["Preset.Dev.Logs.Name"], Localizer["Preset.Dev.Logs.Desc"], SpaceStorageMode.Managed, Path.Combine(root, Localizer["Preset.Dev.Logs.Name"])));
                break;
            case "creative":
                Categories.Add(new CategoryDraftItem(Guid.NewGuid(), Localizer["Preset.Creative.Masters.Name"], Localizer["Preset.Creative.Masters.Desc"], SpaceStorageMode.Managed, Path.Combine(root, Localizer["Preset.Creative.Masters.Name"])));
                Categories.Add(new CategoryDraftItem(Guid.NewGuid(), Localizer["Preset.Creative.Assets.Name"], Localizer["Preset.Creative.Assets.Desc"], SpaceStorageMode.Mapped, Path.Combine(root, Localizer["Preset.Creative.Assets.Name"])));
                Categories.Add(new CategoryDraftItem(Guid.NewGuid(), Localizer["Preset.Creative.AudioVideo.Name"], Localizer["Preset.Creative.AudioVideo.Desc"], SpaceStorageMode.Mapped, Path.Combine(root, Localizer["Preset.Creative.AudioVideo.Name"])));
                Categories.Add(new CategoryDraftItem(Guid.NewGuid(), Localizer["Preset.Creative.Fonts.Name"], Localizer["Preset.Creative.Fonts.Desc"], SpaceStorageMode.Managed, Path.Combine(root, Localizer["Preset.Creative.Fonts.Name"])));
                break;
            case "custom":
            default:
                Categories.Add(new CategoryDraftItem(Guid.NewGuid(), Localizer["Preset.Custom.Scratchpad.Name"], Localizer["Preset.Custom.Scratchpad.Desc"], SpaceStorageMode.Managed, Path.Combine(root, Localizer["Preset.Custom.Scratchpad.Name"])));
                break;
        }
    }

    [RelayCommand]
    public void AddCategory()
    {
        var root = string.IsNullOrWhiteSpace(ManagedRoot) || !Path.IsPathFullyQualified(ManagedRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "DeskNest", "Spaces")
            : ManagedRoot;

        if (!StudioViewModel.TrySanitizeSpaceLeafName(NewCategoryName, root, out var safeName, out var safeFolder))
        {
            ValidationError = Localizer["Validation.InvalidSpaceName"];
            return;
        }

        var desc = string.IsNullOrWhiteSpace(NewCategoryDesc) ? safeName : NewCategoryDesc.Trim();

        Categories.Add(new CategoryDraftItem(Guid.NewGuid(), safeName, desc, SpaceStorageMode.Managed, safeFolder));
        NewCategoryName = string.Empty;
        NewCategoryDesc = string.Empty;
        ValidationError = null;
        OnPropertyChanged(nameof(SummaryCategoryCount));
        OnPropertyChanged(nameof(SummaryCategoryList));
        OnPropertyChanged(nameof(SummaryCategoryCountText));
    }

    [RelayCommand]
    public void RemoveCategory(CategoryDraftItem? item)
    {
        if (item != null)
        {
            Categories.Remove(item);
            OnPropertyChanged(nameof(SummaryCategoryCount));
            OnPropertyChanged(nameof(SummaryCategoryList));
        }
    }

    [RelayCommand]
    public void AddMonitoredFolder()
    {
        if (string.IsNullOrWhiteSpace(NewMonitoredPath)) return;
        var p = Path.GetFullPath(NewMonitoredPath.Trim());
        if (!MonitoredFolders.Contains(p))
            MonitoredFolders.Add(p);
        NewMonitoredPath = string.Empty;
    }

    [RelayCommand]
    public void RemoveMonitoredFolder(string? path)
    {
        if (path != null) MonitoredFolders.Remove(path);
    }

    [RelayCommand]
    public void AddExcludedFolder()
    {
        if (string.IsNullOrWhiteSpace(NewExcludedPath)) return;
        var p = Path.GetFullPath(NewExcludedPath.Trim());
        if (!ExcludedFolders.Contains(p))
            ExcludedFolders.Add(p);
        NewExcludedPath = string.Empty;
    }

    [RelayCommand]
    public void RemoveExcludedFolder(string? path)
    {
        if (path != null) ExcludedFolders.Remove(path);
    }

    [RelayCommand]
    public async Task GoBackAsync()
    {
        if (CurrentStep > 1)
        {
            ValidationError = null;
            CurrentStep--;
            await PersistCurrentStateAsync();
        }
    }

    [RelayCommand]
    public async Task GoNextAsync()
    {
        ValidationError = null;

        // Step validations
        if (CurrentStep == 3)
        {
            if (Categories.Count == 0)
            {
                ValidationError = Localizer["Validation.AtLeastOneSpaceRequired"];
                return;
            }

            var root = Path.IsPathFullyQualified(ManagedRoot)
                ? ManagedRoot
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "DeskNest", "Spaces");

            foreach (var c in Categories)
            {
                if (!StudioViewModel.TrySanitizeSpaceLeafName(c.Name, root, out _, out _))
                {
                    ValidationError = Localizer["Validation.InvalidSpaceName"];
                    return;
                }
            }
        }

        if (CurrentStep == 4)
        {
            if (string.IsNullOrWhiteSpace(ManagedRoot) || !Path.IsPathFullyQualified(ManagedRoot))
            {
                ValidationError = Localizer["Validation.ManagedRootRequired"];
                return;
            }
        }

        if (CurrentStep < 5)
        {
            CurrentStep++;
            OnPropertyChanged(nameof(SummaryLanguage));
            OnPropertyChanged(nameof(SummaryTheme));
            OnPropertyChanged(nameof(SummaryProvider));
            OnPropertyChanged(nameof(SummaryCategoryCount));
            OnPropertyChanged(nameof(SummaryCategoryList));
            OnPropertyChanged(nameof(SummaryMonitoringNotice));
            await PersistCurrentStateAsync();
        }
    }

    [RelayCommand]
    public async Task CompleteOnboardingAsync()
    {
        ValidationError = null;
        var root = Path.IsPathFullyQualified(ManagedRoot)
            ? ManagedRoot
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "DeskNest", "Spaces");

        var spaces = Categories.Select(c => new WorkspaceSpace(
            c.Id,
            c.Name,
            c.Description,
            c.Mode,
            c.IsManaged ? Path.Combine(root, c.Name) : (Path.IsPathFullyQualified(c.Folder) ? c.Folder : Path.Combine(root, c.Name))
        )).ToList();

        var cache = string.IsNullOrWhiteSpace(ModelCacheDirectory)
            ? null
            : (Path.IsPathFullyQualified(ModelCacheDirectory) ? ModelCacheDirectory : null);

        await _updateStore(state => state with
        {
            OnboardingStep = 5,
            OnboardingComplete = true,
            Preset = SelectedPreset,
            Settings = state.Settings with
            {
                Language = SelectedLanguage.Code,
                Theme = SelectedTheme.ToString(),
                Provider = SelectedProvider,
                ModelCacheDirectory = cache,
                ManagedRoot = root,
                MonitoredFolders = MonitoredFolders.Where(Path.IsPathFullyQualified).ToList(),
                ExcludedFolders = ExcludedFolders.Where(Path.IsPathFullyQualified).ToList(),
                WantsMonitoring = WantsMonitoring
            },
            Spaces = spaces
        });

        OnCompleted?.Invoke();
    }

    private async Task PersistCurrentStateAsync()
    {
        var root = Path.IsPathFullyQualified(ManagedRoot)
            ? ManagedRoot
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "DeskNest", "Spaces");

        var spaces = Categories.Select(c => new WorkspaceSpace(
            c.Id,
            c.Name,
            c.Description,
            c.Mode,
            c.IsManaged ? Path.Combine(root, c.Name) : (Path.IsPathFullyQualified(c.Folder) ? c.Folder : Path.Combine(root, c.Name))
        )).ToList();

        var cache = string.IsNullOrWhiteSpace(ModelCacheDirectory)
            ? null
            : (Path.IsPathFullyQualified(ModelCacheDirectory) ? ModelCacheDirectory : null);

        await _updateStore(state => state with
        {
            OnboardingStep = CurrentStep,
            OnboardingComplete = false,
            Preset = SelectedPreset,
            Settings = state.Settings with
            {
                Language = SelectedLanguage.Code,
                Theme = SelectedTheme.ToString(),
                Provider = SelectedProvider,
                ModelCacheDirectory = cache,
                ManagedRoot = root,
                MonitoredFolders = MonitoredFolders.Where(Path.IsPathFullyQualified).ToList(),
                ExcludedFolders = ExcludedFolders.Where(Path.IsPathFullyQualified).ToList(),
                WantsMonitoring = WantsMonitoring
            },
            Spaces = spaces
        });
    }
}
