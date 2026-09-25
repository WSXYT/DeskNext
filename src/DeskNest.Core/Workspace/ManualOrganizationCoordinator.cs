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
            OrganizationTransactionResult result;
            if (file.IsDirectory)
            {
                if (operation.OriginalDirectoryManifest is not { } savedManifest)
                    throw new InvalidDataException("The directory operation has no manifest for undo.");

                var expectedManifest = savedManifest.Select(item => new DirectoryFileReceipt(
                    item.RelativePath,
                    new FileIdentity(item.Length, item.LastWriteTimeUtcTicks, item.Sha256))).ToArray();
                var actualManifest = DesktopOrganizationTransaction.CaptureDirectoryManifest(currentPath);
                if (!actualManifest.SequenceEqual(expectedManifest))
                    throw new IOException("Undo refused because the moved directory contents changed.");

                result = await _transaction.ExecuteDirectoriesAsync(
                    [new OrganizationDirectoryMove(currentPath, restorePath)], cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
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
                result = await _transaction.ExecuteAsync(
                    [new OrganizationMove(currentPath, restorePath)], cancellationToken)
                    .ConfigureAwait(false);
            }

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
                file.Id, file.SpaceId, sourceSpace.Id, currentPath, restorePath, result.Status);
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
        string sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceSpace.Folder));
        string targetRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetSpace.Folder));
        if (!IsDescendantPath(source, sourceRoot) ||
            !string.Equals(Path.GetFileName(source), file.Name,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidDataException("The source file is outside its recorded space or has a mismatched name.");
        }

        string destination = Path.GetFullPath(Path.Combine(targetRoot, file.Name));
        if (!IsDescendantPath(destination, targetRoot) ||
            !string.Equals(Path.GetDirectoryName(destination), targetRoot,
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
            OrganizationTransactionResult result;
            if (file.IsDirectory)
            {
                result = await _transaction.ExecuteDirectoriesAsync(
                    [new OrganizationDirectoryMove(source, destination)], cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                result = await _transaction.ExecuteAsync(
                    [new OrganizationMove(source, destination)], cancellationToken)
                    .ConfigureAwait(false);
            }

            var directoryManifest = file.IsDirectory
                ? result.DirectoryReceipts?.SingleOrDefault()?.Files.Select(item =>
                    new WorkspaceDirectoryFileIdentity(
                        item.RelativePath, item.Identity.Length,
                        item.Identity.LastWriteTimeUtcTicks, item.Identity.Sha256)).ToList()
                : null;
            if (file.IsDirectory && directoryManifest is null)
                throw new InvalidDataException("The directory transaction returned no recovery manifest.");

            await _store.UpdateAsync(state => state with
            {
                Files = state.Files.Select(item => item.Id == fileId
                    ? item with { SpaceId = targetSpaceId, Path = destination, Name = Path.GetFileName(destination) }
                    : item).ToList(),
                Pending = state.Pending.Where(item => !string.Equals(
                    Path.GetFullPath(item.Path), source,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)).ToList(),
                Operations = state.Operations.Select(item => item.Id == operationId
                    ? item with
                    {
                        Status = ProposedOperationStatus.Completed,
                        OriginalDirectoryManifest = directoryManifest
                    }
                    : item).ToList()
            }, cancellationToken).ConfigureAwait(false);

            return new ManualOrganizationResult(
                fileId, sourceSpace.Id, targetSpaceId, source, destination, result.Status);
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

    public async Task<ManualOrganizationResult> RenameFileAsync(
        Guid fileId,
        string newName,
        CancellationToken cancellationToken = default)
    {
        var snapshot = _store.Snapshot;
        var file = snapshot.Files.FirstOrDefault(item => item.Id == fileId)
            ?? throw new KeyNotFoundException($"Workspace file was not found: {fileId}");
        var space = snapshot.Spaces.FirstOrDefault(item => item.Id == file.SpaceId)
            ?? throw new InvalidDataException("Workspace file has no owning space.");
        if (space.Mode != SpaceStorageMode.Managed)
            throw new NotSupportedException("Renaming mapped files is disabled; remove the catalog reference instead.");
        if (!IsValidLeafName(newName))
            throw new InvalidDataException("The new file name is not a safe leaf name.");

        string source = Path.GetFullPath(file.Path);
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(space.Folder));
        if (!IsDescendantPath(source, root) ||
            !string.Equals(Path.GetFileName(source), file.Name,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("The source file is outside its recorded managed space.");

        string destination = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, newName));
        if (!IsDescendantPath(destination, root))
            throw new InvalidDataException("The renamed file would escape its managed space.");

        FileIdentity? originalIdentity = file.IsDirectory ? null : FileIdentity.Capture(source);
        var operationId = Guid.NewGuid();
        await _store.UpdateAsync(state => state with
        {
            Operations = [.. state.Operations, new ProposedOperation(
                operationId, fileId, space.Id, ProposedOperationStatus.PendingUser, DateTimeOffset.UtcNow)
            {
                SourceSpaceId = space.Id,
                SourcePath = source,
                DestinationPath = destination,
                OriginalLength = originalIdentity?.Length,
                OriginalLastWriteUtcTicks = originalIdentity?.LastWriteTimeUtcTicks,
                OriginalSha256 = originalIdentity?.Sha256
            }]
        }, cancellationToken).ConfigureAwait(false);

        try
        {
            OrganizationTransactionResult result;
            if (file.IsDirectory)
            {
                result = await _transaction.ExecuteDirectoriesAsync(
                    [new OrganizationDirectoryMove(source, destination)], cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                result = await _transaction.ExecuteAsync(
                    [new OrganizationMove(source, destination)], cancellationToken)
                    .ConfigureAwait(false);
            }

            var directoryManifest = file.IsDirectory
                ? result.DirectoryReceipts?.SingleOrDefault()?.Files.Select(item =>
                    new WorkspaceDirectoryFileIdentity(item.RelativePath, item.Identity.Length,
                        item.Identity.LastWriteTimeUtcTicks, item.Identity.Sha256)).ToList()
                : null;
            await _store.UpdateAsync(state => state with
            {
                Files = state.Files.Select(item => item.Id == fileId
                    ? item with { Name = newName, Path = destination }
                    : item).ToList(),
                Operations = state.Operations.Select(item => item.Id == operationId
                    ? item with
                    {
                        Status = ProposedOperationStatus.Completed,
                        OriginalDirectoryManifest = directoryManifest
                    }
                    : item).ToList()
            }, cancellationToken).ConfigureAwait(false);

            return new ManualOrganizationResult(fileId, space.Id, space.Id, source, destination, result.Status);
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

    public async Task RemoveMappedReferenceAsync(
        Guid fileId,
        CancellationToken cancellationToken = default)
    {
        var snapshot = _store.Snapshot;
        var file = snapshot.Files.FirstOrDefault(item => item.Id == fileId)
            ?? throw new KeyNotFoundException($"Workspace file was not found: {fileId}");
        var space = snapshot.Spaces.FirstOrDefault(item => item.Id == file.SpaceId)
            ?? throw new InvalidDataException("Workspace file has no owning space.");
        if (space.Mode != SpaceStorageMode.Mapped)
            throw new InvalidDataException("Only mapped references can be removed without a file transaction.");

        await _store.UpdateAsync(state => state with
        {
            Files = state.Files.Where(item => item.Id != fileId).ToList()
        }, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsValidLeafName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 260 || name is "." or ".." ||
            name.StartsWith(' ') || name.EndsWith(' ') || name.EndsWith('.') ||
            name.IndexOfAny(['<', '>', ':', '"', '/', '\\', '|', '?', '*']) >= 0 ||
            name.Any(char.IsControl) || Path.IsPathRooted(name))
            return false;
        string stem = Path.GetFileNameWithoutExtension(name);
        return !new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        }.Contains(stem);
    }

    private static bool IsDescendantPath(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(path, root, comparison))
            return false;

        string prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, comparison);
    }
}
