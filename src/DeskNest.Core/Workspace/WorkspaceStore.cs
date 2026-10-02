using System.Text.Json;
using System.Text.Json.Serialization;
using DeskNest.Core.Storage;

namespace DeskNest.Core.Workspace;

/// <summary>Single in-process owner of P2 workspace metadata; never moves a user file.</summary>
public sealed class WorkspaceStore : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly SemaphoreSlim gate = new(1, 1);
    // All coordinators for this exclusive workspace owner share the full
    // physical-move -> metadata-commit -> cleanup critical section.
    internal SemaphoreSlim OrganizationGate { get; } = new(1, 1);
    private bool disposed;
    private readonly FileStream lockFile;
    private readonly string path;
    private WorkspaceState current;

    private WorkspaceStore(string path, FileStream lockFile, WorkspaceState state)
    {
        this.path = path;
        this.lockFile = lockFile;
        current = state;
    }

    public string DataDirectory => Path.GetDirectoryName(path)
        ?? throw new InvalidOperationException("Workspace store path has no parent directory.");

    public static string DefaultDataDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskNest");
        if (OperatingSystem.IsMacOS())
            return Path.Combine(home, "Library", "Application Support", "DeskNest");
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return Path.Combine(!string.IsNullOrWhiteSpace(xdg) && Path.IsPathFullyQualified(xdg)
            ? xdg : Path.Combine(home, ".local", "share"), "desknest");
    }

    public static async Task<WorkspaceStore> OpenAsync(string? dataDirectory = null)
    {
        var directory = Path.GetFullPath(dataDirectory ?? DefaultDataDirectory());
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "workspace.json");
        // Quarantine removes corrupt files; a durable marker must prevent a later boot
        // from mistaking the now-empty directory for a fresh installation.
        if (File.Exists(path + ".recovery-required"))
            throw new InvalidDataException("Workspace recovery is required; refusing to reset user metadata");
        // An exclusive OS handle prevents two UI processes writing the same snapshot at once.
        var owner = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var loaded = await ResilientJsonStore.LoadWithResultAsync(path,
                json => JsonSerializer.Deserialize<WorkspaceState>(json, Json)
                    ?? throw new InvalidDataException("Null workspace state"),
                () => new WorkspaceState(), "DeskNest workspace");
            if (loaded.Source == ResilientJsonLoadSource.DefaultAfterFailure)
            {
                await File.WriteAllTextAsync(path + ".recovery-required",
                    "Both workspace copies were unreadable. Preserve the quarantined files and restore manually.\n");
                throw new InvalidDataException("Workspace and backup could not be read; refusing to reset user metadata");
            }
            Validate(loaded.Value);
            return new WorkspaceStore(path, owner, loaded.Value);
        }
        catch
        {
            await owner.DisposeAsync();
            throw;
        }
    }

    public WorkspaceState Snapshot
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return Copy(current);
        }
    }

    public async Task<WorkspaceState> UpdateAsync(Func<WorkspaceState, WorkspaceState> update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        await gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var next = update(Copy(current)) ?? throw new InvalidDataException("Null workspace update");
            next = next with { Revision = checked(current.Revision + 1) };
            Validate(next);
            await ResilientJsonStore.SaveAsync(path, JsonSerializer.Serialize(next, Json));
            current = next;
            return Copy(current);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Called under OrganizationGate before deleting a physical-operation journal.
    /// Rotate the already committed primary into the backup, then verify that
    /// fallback cannot resurrect paths from before the physical operation.
    /// </summary>
    internal async Task CheckpointRecoveryBackupAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            string json = JsonSerializer.Serialize(current, Json);
            await ResilientJsonStore.SaveAsync(path, json).ConfigureAwait(false);
            string backup = await File.ReadAllTextAsync(ResilientJsonStore.GetBackupPath(path)).ConfigureAwait(false);
            if (!string.Equals(json, backup, StringComparison.Ordinal))
                throw new IOException("Workspace recovery backup does not match committed metadata; journal must be retained.");
        }
        finally { gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await OrganizationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (disposed) return;
                await lockFile.DisposeAsync().ConfigureAwait(false);
                disposed = true;
            }
            finally { gate.Release(); }
        }
        // Keep managed semaphores usable by queued callers so they can observe
        // the disposed flag rather than race Release against semaphore disposal.
        finally { OrganizationGate.Release(); }
    }

    private static WorkspaceState Copy(WorkspaceState state) =>
        JsonSerializer.Deserialize<WorkspaceState>(JsonSerializer.Serialize(state, Json), Json)!;

    private static bool IsInvalidDirectoryManifestEntry(WorkspaceDirectoryFileIdentity? item)
    {
        // Preserve the file-only legacy schema's metadata bounds. Full topology gets
        // stricter validation below; absent topology never authorizes physical undo.
        if (item is null || string.IsNullOrWhiteSpace(item.RelativePath) || item.RelativePath.Length > 4_096 ||
            Path.IsPathFullyQualified(item.RelativePath) || item.Length < 0 ||
            item.LastWriteTimeUtcTicks < 0 || string.IsNullOrWhiteSpace(item.Sha256) ||
            item.Sha256.Length != 64)
            return true;

        string root = Path.Combine(Path.GetTempPath(), "DeskNestManifestValidation");
        string full = Path.GetFullPath(Path.Combine(root, item.RelativePath));
        string prefix = root + Path.DirectorySeparatorChar;
        return !full.StartsWith(prefix,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static void Validate(WorkspaceState state)
    {
        if (state.SchemaVersion != WorkspaceState.CurrentSchemaVersion || state.Revision < 0 ||
            state.OnboardingStep is < 0 or > 5 || state.OnboardingComplete && state.OnboardingStep != 5 ||
            state.Settings is null ||
            state.Spaces is null || state.Files is null || state.Pending is null || state.Operations is null ||
            state.Spaces.Count > 200 || state.Files.Count > 100_000 || state.Pending.Count > 10_000 ||
            state.Operations.Count > 10_000 ||
            state.Preset is not ("office" or "development" or "creative" or "custom") ||
            string.IsNullOrWhiteSpace(state.Settings.Language) || state.Settings.Language.Length > 20 ||
            state.Settings.Theme is not ("System" or "Dark" or "Light") ||
            !Enum.IsDefined(state.Settings.Provider) ||
            !Path.IsPathFullyQualified(state.Settings.ManagedRoot) ||
            state.Settings.ModelCacheDirectory is { } cache && !Path.IsPathFullyQualified(cache) ||
            state.Settings.MonitoredFolders is null || state.Settings.ExcludedFolders is null ||
            state.Settings.MonitoredFolders.Count > 1_000 || state.Settings.ExcludedFolders.Count > 1_000 ||
            state.Settings.MonitoredFolders.Any(p => !Path.IsPathFullyQualified(p)) ||
            state.Settings.ExcludedFolders.Any(p => !Path.IsPathFullyQualified(p)))
            throw new InvalidDataException("Unsupported or malformed workspace settings");

        var spaces = new HashSet<Guid>();
        foreach (var space in state.Spaces)
        {
            if (space.Id == Guid.Empty || !spaces.Add(space.Id) ||
                string.IsNullOrWhiteSpace(space.Name) || space.Name.Length > 160 ||
                space.Description is null || space.Description.Length > 1_000 ||
                !Enum.IsDefined(space.Mode) || !Path.IsPathFullyQualified(space.Folder))
                throw new InvalidDataException("Invalid or duplicate workspace space");
            if (space.WindowPlacement is { } placement &&
                (!double.IsFinite(placement.Width) || !double.IsFinite(placement.Height) ||
                 placement.Width is < 1 or > 32768 || placement.Height is < 1 or > 32768))
                throw new InvalidDataException("Invalid space window size.");
        }
        if (state.Settings.MonitoredFolderTargets is null || state.Settings.MonitoredFolderTargets.Count > 1_000 ||
            state.Settings.MonitoredFolderTargets.Any(binding => !state.Settings.MonitoredFolders.Contains(binding.Key) || !spaces.Contains(binding.Value)))
            throw new InvalidDataException("Observation targets must reference saved sources and existing spaces.");
        var files = new HashSet<Guid>();
        foreach (var file in state.Files)
        {
            if (file.Id == Guid.Empty || !files.Add(file.Id) || !spaces.Contains(file.SpaceId) ||
                string.IsNullOrWhiteSpace(file.Name) || file.Name.Length > 260 ||
                !Path.IsPathFullyQualified(file.Path))
                throw new InvalidDataException("Invalid or orphaned workspace file metadata");
            if (file.Publication is { } publication)
                PublicationEvidenceValidation.Validate(publication, file.IsDirectory);
        }
        var pending = new HashSet<Guid>();
        foreach (var item in state.Pending)
        {
            if (item.Id == Guid.Empty || !pending.Add(item.Id) ||
                string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 260 ||
                !Path.IsPathFullyQualified(item.Path) || !Enum.IsDefined(item.Reason) ||
                item.SuggestedSpaceId is { } target && !spaces.Contains(target))
                throw new InvalidDataException("Invalid pending decision");
        }
        // An undone import may have returned its item outside the catalog. Its completed
        // undo history remains readable even after the restored pending item is dismissed.
        var returnedImports = state.Operations.Where(o => o.ImportSource is not null &&
            o.Status == ProposedOperationStatus.Undone).Select(o => o.FileId).ToHashSet();
        var operations = new HashSet<Guid>();
        foreach (var operation in state.Operations)
        {
            bool invalidPaths = operation.SourcePath is { } sourcePath && !Path.IsPathFullyQualified(sourcePath) ||
                operation.DestinationPath is { } destinationPath && !Path.IsPathFullyQualified(destinationPath);
            bool invalidManifest = operation.OriginalDirectoryManifest is { } manifest &&
                (manifest.Count > DesktopOrganizationTransaction.MaximumDirectoryEntries ||
                 manifest.Any(IsInvalidDirectoryManifestEntry));
            if (operation.OriginalDirectoryNativeIds is not null && operation.OriginalDirectoryPaths is null)
                throw new InvalidDataException("Native directory identities require topology evidence.");
            if (operation.OriginalDirectoryPaths is { } directories)
            {
                if (invalidManifest || operation.OriginalDirectoryManifest is null)
                    throw new InvalidDataException("Directory topology has no valid file manifest.");
                DesktopOrganizationTransaction.ValidateDirectoryManifest(
                    operation.OriginalDirectoryManifest.Select(item => new DirectoryFileReceipt(item.RelativePath,
                        new FileIdentity(item.Length, item.LastWriteTimeUtcTicks, item.Sha256)
                        { NativeId = item.NativeId })).ToArray(), directories, operation.OriginalDirectoryNativeIds);
            }
            bool knownItem = files.Contains(operation.FileId) ||
                operation.Status == ProposedOperationStatus.Undone && returnedImports.Contains(operation.FileId);
            if (operation.ImportSource is { } import)
            {
                if (import.Id == Guid.Empty || string.IsNullOrWhiteSpace(import.Name) || import.Name.Length > 260 ||
                    !Path.IsPathFullyQualified(import.Path) || !Enum.IsDefined(import.Reason) ||
                    import.SuggestedSpaceId is { } suggestion && !spaces.Contains(suggestion) ||
                    operation.SourceSpaceId != Guid.Empty ||
                    !string.Equals(operation.SourcePath, Path.TrimEndingDirectorySeparator(Path.GetFullPath(import.Path)),
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                    operation.DestinationPath is null || operation.TargetSpaceId is null ||
                    operation.Status == ProposedOperationStatus.Proposed)
                    throw new InvalidDataException("Invalid external import source history.");
                if (operation.Status == ProposedOperationStatus.PendingUser)
                    knownItem = state.Pending.Any(p => p.Id == import.Id && p.Path == import.Path);
                else if (operation.Status == ProposedOperationStatus.RecoveryRequired)
                    knownItem = true; // Immutable failure history survives dismissing its review item.
            }
            if (operation.Id == Guid.Empty || operation.FileId == Guid.Empty || !operations.Add(operation.Id) || !knownItem ||
                !Enum.IsDefined(operation.Status) ||
                operation.TargetSpaceId is { } operationTarget && !spaces.Contains(operationTarget) ||
                invalidPaths || invalidManifest)
                throw new InvalidDataException("Invalid proposed operation");
        }
    }
}
