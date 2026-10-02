namespace DeskNest.Core.Workspace;

// P2 metadata contracts. These records describe files and intentions; none authorize filesystem changes.
public enum SpaceStorageMode { Managed, Mapped }
public enum InferenceProvider { Laya, Jev }
public enum TriageReason { FilenameAmbiguous, CategoriesInsufficient, NearTie }
public enum ProposedOperationStatus { Proposed, PendingUser, Completed, Undone, RecoveryRequired }

// Position is in desktop pixels; size is in device-independent units.
public sealed record SpaceWindowPlacement(int X, int Y, double Width, double Height);
public sealed record WorkspaceSpace(Guid Id, string Name, string Description, SpaceStorageMode Mode, string Folder)
{
    public SpaceWindowPlacement? WindowPlacement { get; init; }
}
public sealed record WorkspacePublishedNodeIdentity(string RelativePath, string NativeId, bool IsDirectory,
    long Length, long LastWriteTimeUtcTicks, string Sha256);
public sealed record WorkspacePublicationEvidence(string NativeId, string VolumePath, long Length,
    long LastWriteTimeUtcTicks, string Sha256, string? ParentNativeId,
    List<WorkspacePublishedNodeIdentity>? DirectoryNodes)
{
    public List<string> AncestorNativeIds { get; init; } = [];
}
public sealed record WorkspaceFile(Guid Id, Guid SpaceId, string Name, string Path, bool IsDirectory)
{
    public bool IsInTrash { get; init; }
    // Creation-time evidence, not permission to undo or delete by path. Null on legacy metadata.
    public WorkspacePublicationEvidence? Publication { get; init; }
}
public sealed record PendingFile(Guid Id, string Name, string Path, TriageReason Reason,
    Guid? SuggestedSpaceId, DateTimeOffset SeenAt);
public sealed record WorkspaceDirectoryFileIdentity(
    string RelativePath, long Length, long LastWriteTimeUtcTicks, string Sha256)
{
    public string? NativeId { get; init; }
}
public sealed record ProposedOperation(Guid Id, Guid FileId, Guid? TargetSpaceId,
    ProposedOperationStatus Status, DateTimeOffset CreatedAt)
{
    // Persisted atomically with the metadata result, before transaction journal cleanup.
    public Guid? CommittedTransactionId { get; init; }
    // Explicit external-import history; undo returns this item to review, not to an invented space.
    public PendingFile? ImportSource { get; init; }
    public string? SourcePath { get; init; }
    public Guid SourceSpaceId { get; init; }
    public string? DestinationPath { get; init; }
    public long? OriginalLength { get; init; }
    public long? OriginalLastWriteUtcTicks { get; init; }
    public string? OriginalSha256 { get; init; }
    public string? OriginalNativeId { get; init; }
    public List<WorkspaceDirectoryFileIdentity>? OriginalDirectoryManifest { get; init; }
    // Absent on legacy operations; undo must not invent empty-directory evidence.
    public List<string>? OriginalDirectoryPaths { get; init; }
    // Root ("") and relative directory IDs; absent legacy history cannot authorize native directory undo.
    public Dictionary<string, string>? OriginalDirectoryNativeIds { get; init; }
}

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
