using System;
using CommunityToolkit.Mvvm.ComponentModel;
using DeskNest.App.Localization;
using DeskNest.Core.Workspace;

namespace DeskNest.App.ViewModels;

public sealed partial class WorkspaceFileItemViewModel : ViewModelBase
{
    [ObservableProperty]
    private Guid _id;

    [ObservableProperty]
    private Guid _spaceId;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _path = string.Empty;

    [ObservableProperty]
    private bool _isDirectory;

    [ObservableProperty]
    private SpaceStorageMode _storageMode = SpaceStorageMode.Managed;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isInTrash;

    public LocalizationManager Localizer => LocalizationManager.Instance;

    public bool IsManaged => StorageMode == SpaceStorageMode.Managed;
    public bool IsMapped => StorageMode == SpaceStorageMode.Mapped;

    public string CapabilityBadgeKey => IsManaged ? "Files.CapabilityManaged" : "Files.CapabilityMapped";
    public string CapabilityBadgeText => Localizer[CapabilityBadgeKey];

    public string CapabilityDescKey => IsManaged ? "Files.CapabilityManagedDesc" : "Files.CapabilityMappedDesc";
    public string CapabilityDescription => Localizer[CapabilityDescKey];

    public string DeleteActionKey => IsManaged ? "Files.ActionDelete" : "Files.ActionRemove";
    public string DeleteActionText => Localizer[DeleteActionKey];

    public string Icon => string.Empty;
    public string TypeLabel => IsDirectory ? "Directory" : "File";

    partial void OnStorageModeChanged(SpaceStorageMode value)
    {
        OnPropertyChanged(nameof(IsManaged));
        OnPropertyChanged(nameof(IsMapped));
        OnPropertyChanged(nameof(CapabilityBadgeKey));
        OnPropertyChanged(nameof(CapabilityBadgeText));
        OnPropertyChanged(nameof(CapabilityDescKey));
        OnPropertyChanged(nameof(CapabilityDescription));
        OnPropertyChanged(nameof(DeleteActionKey));
        OnPropertyChanged(nameof(DeleteActionText));
    }

    public WorkspaceFileItemViewModel(
        Guid id,
        Guid spaceId,
        string name,
        string path,
        bool isDirectory,
        SpaceStorageMode storageMode = SpaceStorageMode.Managed,
        bool isInTrash = false)
    {
        _id = id;
        _spaceId = spaceId;
        _name = name;
        _path = path;
        _isDirectory = isDirectory;
        _storageMode = storageMode;
        _isInTrash = isInTrash;

        Localizer.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CapabilityBadgeText));
            OnPropertyChanged(nameof(CapabilityDescription));
            OnPropertyChanged(nameof(DeleteActionText));
        };
    }
}
