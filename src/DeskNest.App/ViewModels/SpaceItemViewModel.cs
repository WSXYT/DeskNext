using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DeskNest.Core.Workspace;

namespace DeskNest.App.ViewModels;

public sealed partial class SpaceItemViewModel : ViewModelBase
{
    [ObservableProperty]
    private Guid _id;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private SpaceStorageMode _mode;

    [ObservableProperty]
    private string _folder = string.Empty;

    [ObservableProperty]
    private int _itemCount;

    public string ItemCountText => Localization.LocalizationManager.Instance.GetString("Spaces.ItemCountFormat", ItemCount);

    partial void OnItemCountChanged(int value)
    {
        OnPropertyChanged(nameof(ItemCountText));
    }

    [ObservableProperty]
    private bool _isSelected;

    public ObservableCollection<WorkspaceFileItemViewModel> Files { get; } = new();
    public SpaceWindowPlacement? WindowPlacement { get; }

    public bool IsManaged => Mode == SpaceStorageMode.Managed;
    public bool IsMapped => Mode == SpaceStorageMode.Mapped;

    public string ModeBadgeKey => IsManaged ? "Spaces.BadgeManaged" : "Spaces.BadgeMapped";
    public string ModeLocalized => Localization.LocalizationManager.Instance[ModeBadgeKey];
    public string StatusBadgeKey => IsManaged ? "Spaces.BadgeReady" : "Spaces.BadgeReadOnly";

    partial void OnModeChanged(SpaceStorageMode value)
    {
        OnPropertyChanged(nameof(IsManaged));
        OnPropertyChanged(nameof(IsMapped));
        OnPropertyChanged(nameof(ModeBadgeKey));
        OnPropertyChanged(nameof(ModeLocalized));
    }

    public bool HasFiles => Files.Count > 0;
    public bool IsEmpty => Files.Count == 0;

    public void NotifyFilesChanged()
    {
        OnPropertyChanged(nameof(HasFiles));
        OnPropertyChanged(nameof(IsEmpty));
    }

    public string BoundsSummary => IsManaged
        ? Localization.LocalizationManager.Instance["Spaces.DetailNotice"]
        : Localization.LocalizationManager.Instance["Welcome.MappedDesc"];

    public string RulesSummary => Localization.LocalizationManager.Instance["Status.NoticeP2"];

    public SpaceItemViewModel(WorkspaceSpace space, int itemCount = 0)
    {
        _id = space.Id;
        _name = space.Name;
        _description = space.Description;
        _mode = space.Mode;
        _folder = space.Folder;
        _itemCount = itemCount;
        WindowPlacement = space.WindowPlacement;
    }
}
