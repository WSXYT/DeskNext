using DeskNest.Core.Storage;

namespace DeskNest.Core.Workspace;

public sealed record ManualOrganizationResult(
    Guid FileId,
    Guid SourceSpaceId,
    Guid TargetSpaceId,
    string SourcePath,
    string DestinationPath,
    OrganizationTransactionStatus Status);

/// <summary>
/// Explicit manual operations only. Retain the physical transaction journal until
/// workspace metadata and its commit ID have been persisted together.
/// </summary>
public sealed class ManualOrganizationCoordinator
{
    private readonly WorkspaceStore _store;
    private readonly DesktopOrganizationTransaction _transaction;
    private SemaphoreSlim _gate => _store.OrganizationGate;

    public ManualOrganizationCoordinator(WorkspaceStore store, DesktopOrganizationTransaction transaction)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _transaction = transaction ?? throw new ArgumentNullException(nameof(transaction));
    }

    public async Task RecoverPendingAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_transaction.HasRecoveryJournal)
            {
                try
                {
                    var committed = _store.Snapshot.Operations
                        .Where(o => o.CommittedTransactionId.HasValue)
                        .Select(o => o.CommittedTransactionId!.Value).ToHashSet();
                    await _transaction.RecoverAsync(cancellationToken, committed).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException)
                {
                    throw new InvalidDataException(
                        "Organization recovery could not be completed; manual recovery is required.", ex);
                }
            }

            if (_store.Snapshot.Operations.Any(o => o.Status == ProposedOperationStatus.PendingUser))
                await _store.UpdateAsync(state => state with
                {
                    Operations = state.Operations.Select(o => o.Status == ProposedOperationStatus.PendingUser
                        ? o with { Status = ProposedOperationStatus.RecoveryRequired } : o).ToList()
                }, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<ManualOrganizationResult> MoveFileAsync(
        Guid fileId, Guid targetSpaceId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = _store.Snapshot;
            var file = GetFile(snapshot, fileId);
            var sourceSpace = GetSpace(snapshot, file.SpaceId);
            var targetSpace = GetSpace(snapshot, targetSpaceId);
            ValidateSource(file, sourceSpace);
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetSpace.Folder));
            string destination = Path.GetFullPath(Path.Combine(root, file.Name));
            if (!IsDescendantPath(destination, root) ||
                !PathComparer.Equals(Path.GetDirectoryName(destination), root))
                throw new InvalidDataException("The target file would escape the target space.");
            return await MoveAndCommitAsync(file, targetSpace.Id, destination, null, cancellationToken)
                .ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<ManualOrganizationResult> RenameFileAsync(
        Guid fileId, string newName, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = _store.Snapshot;
            var file = GetFile(snapshot, fileId);
            var space = GetSpace(snapshot, file.SpaceId);
            if (space.Mode != SpaceStorageMode.Managed)
                throw new NotSupportedException("Renaming mapped files is disabled; remove the catalog reference instead.");
            if (!IsValidLeafName(newName))
                throw new InvalidDataException("The new file name is not a safe leaf name.");
            ValidateSource(file, space);
            string destination = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(file.Path))!, newName);
            return await MoveAndCommitAsync(file, space.Id, destination, null, cancellationToken)
                .ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<ManualOrganizationResult> UndoOperationAsync(
        Guid operationId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = _store.Snapshot;
            var operation = snapshot.Operations.FirstOrDefault(o => o.Id == operationId)
                ?? throw new KeyNotFoundException($"Workspace operation was not found: {operationId}");
            if (operation.Status != ProposedOperationStatus.Completed || operation.SourcePath is null ||
                operation.DestinationPath is null || operation.SourceSpaceId == Guid.Empty)
                throw new InvalidDataException("Only completed operations with recovery paths can be undone.");
            var file = GetFile(snapshot, operation.FileId);
            GetSpace(snapshot, operation.SourceSpaceId);
            // Do not undo an older move after a subsequent move/rename of the same item.
            if (!PathComparer.Equals(Path.GetFullPath(file.Path), Path.GetFullPath(operation.DestinationPath)))
                throw new InvalidDataException("The operation is not the file's current location.");
            try
            {
                if (file.IsDirectory)
                {
                    var saved = operation.OriginalDirectoryManifest
                        ?? throw new InvalidDataException("The directory operation has no manifest for undo.");
                    var expected = saved.Select(item => new DirectoryFileReceipt(item.RelativePath,
                        new FileIdentity(item.Length, item.LastWriteTimeUtcTicks, item.Sha256)));
                    if (!DesktopOrganizationTransaction.CaptureDirectoryManifest(file.Path).SequenceEqual(expected))
                        throw new IOException("Undo refused because the moved directory contents changed.");
                }
                else
                {
                    if (operation.OriginalLength is null || operation.OriginalLastWriteUtcTicks is null ||
                        string.IsNullOrWhiteSpace(operation.OriginalSha256))
                        throw new InvalidDataException("The operation has no complete source identity for undo.");
                    var expected = new FileIdentity(operation.OriginalLength.Value,
                        operation.OriginalLastWriteUtcTicks.Value, operation.OriginalSha256);
                    if (FileIdentity.Capture(file.Path) != expected)
                        throw new IOException("Undo refused because the moved file was modified after organization.");
                }
            }
            catch
            {
                await MarkRecoveryRequiredAsync(operation.Id).ConfigureAwait(false);
                throw;
            }
            return await MoveAndCommitAsync(file, operation.SourceSpaceId, Path.GetFullPath(operation.SourcePath),
                operation, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<ManualOrganizationResult> MoveAndCommitAsync(
        WorkspaceFile file, Guid targetSpaceId, string destination, ProposedOperation? undo,
        CancellationToken cancellationToken)
    {
        if (_transaction.HasRecoveryJournal)
            throw new InvalidOperationException("Resolve the retained transaction journal before another operation.");
        string source = Path.GetFullPath(file.Path);
        var operationId = undo?.Id ?? Guid.NewGuid();
        if (undo is null)
        {
            var identity = file.IsDirectory ? null : FileIdentity.Capture(source);
            await _store.UpdateAsync(state => state with
            {
                Operations = [.. state.Operations, new ProposedOperation(operationId, file.Id, targetSpaceId,
                    ProposedOperationStatus.PendingUser, DateTimeOffset.UtcNow)
                {
                    SourceSpaceId = file.SpaceId, SourcePath = source, DestinationPath = destination,
                    OriginalLength = identity?.Length, OriginalLastWriteUtcTicks = identity?.LastWriteTimeUtcTicks,
                    OriginalSha256 = identity?.Sha256
                }]
            }, cancellationToken).ConfigureAwait(false);
        }

        bool metadataCommitted = false;
        try
        {
            var result = file.IsDirectory
                ? await _transaction.ExecuteDirectoriesAsync([new(source, destination)], cancellationToken,
                    retainJournalUntilCommit: true).ConfigureAwait(false)
                : await _transaction.ExecuteAsync([new(source, destination)], cancellationToken,
                    retainJournalUntilCommit: true).ConfigureAwait(false);
            var manifest = result.DirectoryReceipts?.SingleOrDefault()?.Files.Select(item =>
                new WorkspaceDirectoryFileIdentity(item.RelativePath, item.Identity.Length,
                    item.Identity.LastWriteTimeUtcTicks, item.Identity.Sha256)).ToList();
            if (file.IsDirectory && manifest is null)
                throw new InvalidDataException("The directory transaction returned no recovery manifest.");

            await _store.UpdateAsync(state => state with
            {
                Files = state.Files.Select(item => item.Id == file.Id
                    ? item with { SpaceId = targetSpaceId, Path = destination, Name = Path.GetFileName(destination) }
                    : item).ToList(),
                Pending = state.Pending.Where(item => !PathComparer.Equals(Path.GetFullPath(item.Path), source)).ToList(),
                Operations = state.Operations.Select(item => item.Id == operationId
                    ? item with
                    {
                        Status = undo is null ? ProposedOperationStatus.Completed : ProposedOperationStatus.Undone,
                        CommittedTransactionId = result.OperationId,
                        OriginalDirectoryManifest = undo is null ? manifest : item.OriginalDirectoryManifest
                    } : item).ToList()
            }, cancellationToken).ConfigureAwait(false);
            metadataCommitted = true;
            // If interrupted here, startup recognizes the persisted commit ID and only
            // finishes journal cleanup, rather than undoing an already committed move.
            await _transaction.CommitAsync(result.OperationId, CancellationToken.None).ConfigureAwait(false);
            return new(file.Id, file.SpaceId, targetSpaceId, source, destination, result.Status);
        }
        catch
        {
            // Never erase durable commit evidence because cleanup failed.
            if (!metadataCommitted) await MarkRecoveryRequiredAsync(operationId).ConfigureAwait(false);
            throw;
        }
    }

    private Task<WorkspaceState> MarkRecoveryRequiredAsync(Guid operationId) => _store.UpdateAsync(state => state with
    {
        Operations = state.Operations.Select(item => item.Id == operationId
            ? item with { Status = ProposedOperationStatus.RecoveryRequired } : item).ToList()
    }, CancellationToken.None);

    public async Task RemoveMappedReferenceAsync(Guid fileId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = _store.Snapshot;
            var file = GetFile(snapshot, fileId);
            if (GetSpace(snapshot, file.SpaceId).Mode != SpaceStorageMode.Mapped)
                throw new InvalidDataException("Only mapped references can be removed without a file transaction.");
            // Preserve history and recovery evidence instead of deleting referenced operations.
            if (snapshot.Operations.Any(o => o.FileId == fileId))
                throw new InvalidOperationException("This reference has operation history and cannot be removed yet.");
            await _store.UpdateAsync(state => state with
            {
                Files = state.Files.Where(item => item.Id != fileId).ToList()
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private static WorkspaceFile GetFile(WorkspaceState state, Guid id) =>
        state.Files.FirstOrDefault(f => f.Id == id) ?? throw new KeyNotFoundException($"Workspace file was not found: {id}");

    private static WorkspaceSpace GetSpace(WorkspaceState state, Guid id) =>
        state.Spaces.FirstOrDefault(s => s.Id == id) ?? throw new InvalidDataException($"Workspace space was not found: {id}");

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static void ValidateSource(WorkspaceFile file, WorkspaceSpace space)
    {
        string source = Path.GetFullPath(file.Path);
        if (!IsDescendantPath(source, Path.TrimEndingDirectorySeparator(Path.GetFullPath(space.Folder))) ||
            !PathComparer.Equals(Path.GetFileName(source), file.Name))
            throw new InvalidDataException("The source file is outside its recorded space or has a mismatched name.");
    }

    private static bool IsValidLeafName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 260 || name is "." or ".." ||
            name.StartsWith(' ') || name.EndsWith(' ') || name.EndsWith('.') ||
            name.IndexOfAny(['<', '>', ':', '"', '/', '\\', '|', '?', '*']) >= 0 ||
            name.Any(char.IsControl) || Path.IsPathRooted(name))
            return false;
        string stem = name.Split('.')[0];
        return !new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        }.Contains(stem);
    }

    private static bool IsDescendantPath(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return !string.Equals(path, root, comparison) && path.StartsWith(prefix, comparison);
    }
}
