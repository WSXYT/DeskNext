namespace DeskNest.Core.Organization;

public static class DesktopOrganizationCategoryIds
{
    public const string Shortcuts = "Shortcuts";
    public const string Documents = "Documents";
    public const string Images = "Images";
    public const string Media = "Media";
    public const string Packages = "Packages";
    public const string Other = "Other";

    public static readonly IReadOnlyList<string> DefaultOrder =
    [
        Shortcuts,
        Documents,
        Images,
        Media,
        Packages,
        Other
    ];
}

public static class DesktopOrganizationSubtypeIds
{
    public const string Pdf = "Pdf";
    public const string Word = "Word";
    public const string Excel = "Excel";
    public const string PowerPoint = "PowerPoint";
    public const string Text = "Text";
    public const string Audio = "Audio";
    public const string Video = "Video";
}

/// <summary>
/// A stable routing rule. The target is a widget identity, never a user-facing
/// widget name. Joining a group or renaming the widget does not affect routing.
/// </summary>
public sealed class DesktopOrganizationRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string TargetWidgetId { get; set; } = string.Empty;

    public bool IsEnabled { get; set; } = true;

    public List<string> CategoryIds { get; set; } = [];

    public List<string> SubtypeIds { get; set; } = [];

    public List<string> Extensions { get; set; } = [];

    public List<string> ExcludedExtensions { get; set; } = [];
}

public enum DesktopOrganizationSourceScope
{
    Personal,
    Public
}

public enum DesktopOrganizationExclusionReason
{
    None,
    Folder,
    HiddenOrSystem,
    ReparsePoint,
    OfflinePlaceholder,
    TemporaryOrDownloading,
    PublicDesktopItem,
    Unavailable,
    SlowItem,
    BatchLimit,
    UserChoice,
    SourceNotSelected
}

public sealed record DesktopOrganizationFileSnapshot(
    string SourcePath,
    string Name,
    string Extension,
    long Size,
    DateTime LastWriteTimeUtc,
    string CategoryId,
    string? SubtypeId,
    DesktopOrganizationExclusionReason ExclusionReason,
    bool IsDirectory = false,
    DesktopOrganizationSourceScope SourceScope = DesktopOrganizationSourceScope.Personal)
{
    public bool IsEligible => ExclusionReason == DesktopOrganizationExclusionReason.None;

    public bool CanOptIn => ExclusionReason is
        DesktopOrganizationExclusionReason.Folder or
        DesktopOrganizationExclusionReason.SlowItem or
        DesktopOrganizationExclusionReason.BatchLimit;
}

// Routing-only projection of DeskBox.Models.WidgetConfig; UI/layout fields are not imported.
public sealed class WidgetConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public WidgetKind WidgetKind { get; set; } = WidgetKind.File;
    public bool IsDisabled { get; set; }
    public string? MappedFolderPath { get; set; }
}

public enum WidgetKind
{
    /// <summary>File-oriented widget used for references or folder-backed storage.</summary>
    File,

    /// <summary>Built-in lightweight text/link capture widget.</summary>
    QuickCapture,

    /// <summary>Reserved for a future weather widget.</summary>
    Weather,

    /// <summary>Reserved for a future todo widget.</summary>
    Todo,

    /// <summary>Reserved for a future tag widget.</summary>
    Tags,

    /// <summary>Reserved for a future music control widget.</summary>
    Music,

    /// <summary>Reserved for a future system monitor widget.</summary>
    SystemMonitor,

    /// <summary>Global search widget that provides unified file and content search.</summary>
    Search,

    /// <summary>Legacy value kept only for migrating old settings files.</summary>
    Productivity,

    /// <summary>At-a-glance background, time and date widget.</summary>
    Glance
}

