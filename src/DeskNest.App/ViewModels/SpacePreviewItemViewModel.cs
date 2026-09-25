using CommunityToolkit.Mvvm.ComponentModel;

namespace DeskNest.App.ViewModels;

public enum SpaceMode
{
    Managed,
    Mapped
}

public sealed partial class SpacePreviewItemViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _id;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _description;

    [ObservableProperty]
    private SpaceMode _mode;

    [ObservableProperty]
    private string _physicalPath;

    [ObservableProperty]
    private int _itemCount;

    [ObservableProperty]
    private string _status;

    [ObservableProperty]
    private string _boundsSummary;

    [ObservableProperty]
    private string _rulesSummary;

    [ObservableProperty]
    private bool _isSelected;

    public SpacePreviewItemViewModel(
        string id,
        string name,
        string description,
        SpaceMode mode,
        string physicalPath,
        int itemCount,
        string status,
        string boundsSummary,
        string rulesSummary)
    {
        _id = id;
        _name = name;
        _description = description;
        _mode = mode;
        _physicalPath = physicalPath;
        _itemCount = itemCount;
        _status = status;
        _boundsSummary = boundsSummary;
        _rulesSummary = rulesSummary;
    }

    public bool IsManaged => Mode == SpaceMode.Managed;

    public string ModeBadgeText => IsManaged ? "Spaces.BadgeManaged" : "Spaces.BadgeMapped";
}
