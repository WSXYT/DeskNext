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
/// Coordinates an explicitly approved manual move. The transaction owns all
/// physical I/O; the workspace snapshot changes only after that I/O succeeds.
/// Automatic watcher/model callers are intentionally not part of this API.
/// </summary>
public sealed class ManualOrganizationCoordinator
{
    private readonly WorkspaceStore _store;
    private readonly DesktopOrganizationTransaction _transaction;

    public ManualOrganizationCoordinator(
        WorkspaceStore store,
        DesktopOrganizationTransaction transaction)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _transaction = transaction ?? throw new ArgumentNullException(nameof(transaction));
    }

    public async Task RecoverPendingAsync(CancellationToken cancellationToken = default)
    {
        if (_transaction.HasRecoveryJournal)
        {
            try
            {
                await _transaction.RecoverAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                throw new InvalidDataException(
                    "Organization recovery could not be completed; manual recovery is required.", ex);
            }
        }

        var snapshot = _store.Snapshot;
        if (!snapshot.Operations.Any(operation => operation.Status == ProposedOperationStatus.PendingUser))
            return;

        await _store.UpdateAsync(state => state with
        {
            Operations = state.Operations.Select(operation =>
                operation.Status == ProposedOperationStatus.PendingUser
                    ? operation with { Status = ProposedOperationStatus.RecoveryRequired }
                    : operation).ToList()
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ManualOrganizationResult> UndoOperationAsync(
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        var snapshot = _store.Snapshot;
        var operation = snapshot.Operations.FirstOrDefault(item => item.Id == operationId)
            ?? throw new KeyNotFoundException($"Workspace operation was not found: {operationId}");
        if (operation.Status != ProposedOperationStatus.Completed ||
            operation.SourcePath is null || operation.DestinationPath is null || operation.SourceSpaceId == Guid.Empty)
            throw new InvalidDataException("Only completed operations with recovery paths can be undone.");

        var file = snapshot.Files.FirstOrDefault(item => item.Id == operation.FileId)
            ?? throw new InvalidDataException("The operation file metadata is missing.");
        var sourceSpace = snapshot.Spaces.FirstOrDefault(space => space.Id == operation.SourceSpaceId)
            ?? throw new InvalidDataException("The operation source space is missing.");
        string currentPath = Path.GetFullPath(operation.DestinationPath);
        string restorePath = Path.GetFullPath(operation.SourcePath);

        try
        {
            if (file.IsDirectory)
                throw new NotSupportedException("Directory undo requires a persisted manifest and is not yet enabled.");

            if (operation.OriginalLength is null || operation.OriginalLastWriteUtcTicks is null ||
                string.IsNullOrWhiteSpace(operation.OriginalSha256))
                throw new InvalidDataException("The operation has no complete source identity for undo.");

            var expectedIdentity = new FileIdentity(
                operation.OriginalLength.Value,
                operation.OriginalLastWriteUtcTicks.Value,
                operation.OriginalSha256);
            var currentIdentity = FileIdentity.Capture(currentPath);
            if (currentIdentity != expectedIdentity)
                throw new IOException("Undo refused because the moved file was modified after organization.");
            var result = await _transaction.ExecuteAsync(
                [new OrganizationMove(currentPath, restorePath)], cancellationToken)
                .ConfigureAwait(false);
            OrganizationTransactionStatus status = result.Status;

            await _store.UpdateAsync(state => state with
            {
                Files = state.Files.Select(item => item.Id == file.Id
                    ? item with { SpaceId = sourceSpace.Id, Path = restorePath, Name = Path.GetFileName(restorePath) }
                    : item).ToList(),
                Operations = state.Operations.Select(item => item.Id == operationId
                    ? item with { Status = ProposedOperationStatus.Undone }
                    : item).ToList()
            }, cancellationToken).ConfigureAwait(false);

            return new ManualOrganizationResult(
                file.Id, file.SpaceId, sourceSpace.Id, currentPath, restorePath, status);
        }
        catch
        {
            await _store.UpdateAsync(state => state with
            {
                Operations = state.Operations.Select(item => item.Id == operationId
                    ? item with { Status = ProposedOperationStatus.RecoveryRequired }
                    : item).ToList()
            }, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<ManualOrganizationResult> MoveFileAsync(
        Guid fileId,
        Guid targetSpaceId,
        CancellationToken cancellationToken = default)
    {
        var snapshot = _store.Snapshot;
        var file = snapshot.Files.FirstOrDefault(item => item.Id == fileId)
            ?? throw new KeyNotFoundException($"Workspace file was not found: {fileId}");
        var sourceSpace = snapshot.Spaces.FirstOrDefault(space => space.Id == file.SpaceId)
            ?? throw new InvalidDataException("Workspace file has no source space.");
        var targetSpace = snapshot.Spaces.FirstOrDefault(space => space.Id == targetSpaceId)
            ?? throw new KeyNotFoundException($"Target space was not found: {targetSpaceId}");

        string source = Path.GetFullPath(file.Path);
        string targetRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetSpace.Folder));
        string destination = Path.GetFullPath(Path.Combine(targetRoot, file.Name));
        if (!string.Equals(Path.GetDirectoryName(destination), targetRoot,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidDataException("The target file would escape the target space.");
        }

        FileIdentity? originalIdentity = file.IsDirectory ? null : FileIdentity.Capture(source);

        var operationId = Guid.NewGuid();
        await _store.UpdateAsync(state => state with
        {
            Operations = [.. state.Operations, new ProposedOperation(
                operationId, fileId, targetSpaceId, ProposedOperationStatus.PendingUser, DateTimeOffset.UtcNow)
            {
                SourceSpaceId = sourceSpace.Id,
                SourcePath = source,
                DestinationPath = destination,
                OriginalLength = originalIdentity?.Length,
                OriginalLastWriteUtcTicks = originalIdentity?.LastWriteTimeUtcTicks,
                OriginalSha256 = originalIdentity?.Sha256
            }]
        }, cancellationToken).ConfigureAwait(false);

        try
        {
            OrganizationTransactionStatus status;
            if (file.IsDirectory)
            {
                var result = await _transaction.ExecuteDirectoriesAsync(
                    [new OrganizationDirectoryMove(source, destination)], cancellationToken)
                    .ConfigureAwait(false);
                status = result.Status;
            }
            else
            {
                var result = await _transaction.ExecuteAsync(
                    [new OrganizationMove(source, destination)], cancellationToken)
                    .ConfigureAwait(false);
                status = result.Status;
            }

            await _store.UpdateAsync(state => state with
            {
                Files = state.Files.Select(item => item.Id == fileId
                    ? item with { SpaceId = targetSpaceId, Path = destination, Name = Path.GetFileName(destination) }
                    : item).ToList(),
                Pending = state.Pending.Where(item => !string.Equals(
                    Path.GetFullPath(item.Path), source,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)).ToList(),
                Operations = state.Operations.Select(item => item.Id == operationId
                    ? item with { Status = ProposedOperationStatus.Completed }
                    : item).ToList()
            }, cancellationToken).ConfigureAwait(false);

            return new ManualOrganizationResult(
                fileId, sourceSpace.Id, targetSpaceId, source, destination, status);
        }
        catch
        {
            await _store.UpdateAsync(state => state with
            {
                Operations = state.Operations.Select(item => item.Id == operationId
                    ? item with { Status = ProposedOperationStatus.RecoveryRequired }
                    : item).ToList()
            }, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
}
