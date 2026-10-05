using System;
using CommunityToolkit.Mvvm.ComponentModel;
using DeskNest.Core.Workspace;

namespace DeskNest.App.ViewModels;

public sealed partial class PendingItemViewModel : ViewModelBase
{
    [ObservableProperty]
    private Guid _id;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _path = string.Empty;

    [ObservableProperty]
    private TriageReason _reason;

    [ObservableProperty]
    private Guid? _suggestedSpaceId;

    [ObservableProperty]
    private string? _suggestedSpaceName;

    [ObservableProperty]
    private DateTimeOffset _seenAt;

    [ObservableProperty]
    private string? _resolutionNotice;

    [ObservableProperty]
    private SpaceItemViewModel? _targetSpace;

    // A transient model suggestion; selecting it still requires the existing import confirmation.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasClassificationTarget))]
    private SpaceItemViewModel? _classificationTarget;

    [ObservableProperty] private string _classificationHint = string.Empty;
    partial void OnClassificationHintChanged(string value) => ClassificationTarget = null;

    public bool HasClassificationTarget => ClassificationTarget is not null;

    public string ReasonKey => Reason switch
    {
        TriageReason.FilenameAmbiguous => "Triage.ReasonAmbiguous",
        TriageReason.CategoriesInsufficient => "Triage.ReasonInsufficient",
        TriageReason.NearTie => "Triage.ReasonNearTie",
        _ => "Triage.ReasonAmbiguous"
    };

    public string ReasonLocalized => Localization.LocalizationManager.Instance[ReasonKey];

    public string FormattedSeenAt => SeenAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    public PendingItemViewModel(PendingFile file, string? suggestedSpaceName = null)
    {
        _id = file.Id;
        _name = file.Name;
        _path = file.Path;
        _reason = file.Reason;
        _suggestedSpaceId = file.SuggestedSpaceId;
        _seenAt = file.SeenAt;
        _suggestedSpaceName = suggestedSpaceName;
    }
}
