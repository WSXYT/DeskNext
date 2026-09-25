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
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)).ToList()
        }, cancellationToken).ConfigureAwait(false);

        return new ManualOrganizationResult(
            fileId, sourceSpace.Id, targetSpaceId, source, destination, status);
    }
}
