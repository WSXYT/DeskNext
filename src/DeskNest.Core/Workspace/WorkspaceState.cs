namespace DeskNest.Core.Workspace;

// P2 metadata contracts. These records describe files and intentions; none authorize filesystem changes.
public enum SpaceStorageMode { Managed, Mapped }
public enum InferenceProvider { Laya, Jev }
public enum TriageReason { FilenameAmbiguous, CategoriesInsufficient, NearTie }
public enum ProposedOperationStatus { Proposed, PendingUser }

public sealed record WorkspaceSpace(Guid Id, string Name, string Description, SpaceStorageMode Mode, string Folder);
public sealed record WorkspaceFile(Guid Id, Guid SpaceId, string Name, string Path, bool IsDirectory);
public sealed record PendingFile(Guid Id, string Name, string Path, TriageReason Reason,
    Guid? SuggestedSpaceId, DateTimeOffset SeenAt);
public sealed record ProposedOperation(Guid Id, Guid FileId, Guid? TargetSpaceId,
    ProposedOperationStatus Status, DateTimeOffset CreatedAt);

public sealed record WorkspaceSettings
{
    public string Language { get; init; } = "zh-CN";
    public string Theme { get; init; } = "System";
    public InferenceProvider Provider { get; init; } = InferenceProvider.Laya;
    public string? ModelCacheDirectory { get; init; }
    public string ManagedRoot { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "DeskNest", "Spaces");
    public List<string> MonitoredFolders { get; init; } = [];
    public List<string> ExcludedFolders { get; init; } = [];
    // Records user preference only. P2 never starts a watcher or moves files.
    public bool WantsMonitoring { get; init; }
}

public sealed record WorkspaceState
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public long Revision { get; init; }
    public int OnboardingStep { get; init; }
    public bool OnboardingComplete { get; init; }
    public string Preset { get; init; } = "custom";
    public WorkspaceSettings Settings { get; init; } = new();
    public List<WorkspaceSpace> Spaces { get; init; } = [];
    public List<WorkspaceFile> Files { get; init; } = [];
    public List<PendingFile> Pending { get; init; } = [];
    public List<ProposedOperation> Operations { get; init; } = [];
}
