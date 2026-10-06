using DeskNest.Core.Storage;
using DeskNest.Core.Workspace;

namespace DeskNest.Core.Flow;

public sealed record FlowMoveReview(Guid FileId, Guid TargetSpaceId, long Revision, string SourcePath, string DestinationPath);

/// <summary>Catalog-bound, individually confirmed file/directory moves through the existing organization coordinator.</summary>
public sealed class ConfirmedFlowMoves(WorkspaceStore store, ManualOrganizationCoordinator coordinator)
{
    public void Validate(FlowMove move) => Resolve(store.Snapshot, move);

    public async Task<string> ExecuteAsync(FlowMove move, Func<FlowMoveReview, CancellationToken, Task<bool>> confirm, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var review = Resolve(store.Snapshot, move);
        if (!await confirm(review, token).ConfigureAwait(false)) throw new OperationCanceledException("Flow move was declined.", token);
        token.ThrowIfCancellationRequested();
        // Core checks this revision under both organization and metadata gates and owns the full move/commit/undo receipt.
        var result = await coordinator.MoveFileAsync(review.FileId, review.TargetSpaceId, token, review.Revision).ConfigureAwait(false);
        return result.DestinationPath;
    }

    private static FlowMoveReview Resolve(WorkspaceState state, FlowMove move)
    {
        if (!Path.IsPathFullyQualified(move.SourcePath) || !Path.IsPathFullyQualified(move.DestinationPath))
            throw new InvalidDataException("Flow move paths must be absolute.");
        var files = state.Files.Where(f => !f.IsInTrash && ManualFlowRunner.SamePath(f.Path, move.SourcePath)).Take(2).ToArray();
        if (files.Length != 1) throw new InvalidDataException("Flow moves require exactly one cataloged file or directory.");
        var file = files[0];
        var sourceSpace = state.Spaces.SingleOrDefault(s => s.Id == file.SpaceId)
            ?? throw new InvalidDataException("Flow source space is unavailable.");
        ManualOrganizationCoordinator.ValidateSource(file, sourceSpace);
        var parent = Path.GetDirectoryName(Path.GetFullPath(move.DestinationPath))!;
        var targets = state.Spaces.Where(s => s.Id != file.SpaceId && ManualFlowRunner.SamePath(s.Folder, parent)).Take(2).ToArray();
        if (targets.Length != 1 || !ManualFlowRunner.SamePath(Path.Combine(targets[0].Folder, file.Name), move.DestinationPath))
            throw new InvalidDataException("Flow move destination must be the unchanged filename in one other cataloged space.");
        if (!(file.IsDirectory ? Directory.Exists(file.Path) : File.Exists(file.Path)) || !Directory.Exists(targets[0].Folder))
            throw new IOException("Flow moves currently require an existing source and target folder.");
        if (file.IsDirectory)
        {
            string relative = Path.GetRelativePath(Path.GetFullPath(file.Path), Path.GetFullPath(move.DestinationPath));
            if (relative == "." || !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidDataException("A Flow directory cannot move into its own tree.");
        }
        if (File.Exists(move.DestinationPath) || Directory.Exists(move.DestinationPath))
            throw new IOException("Flow destination is occupied; nothing was moved.");
        FileSystemVolume.RequireNoReparsePoints(file.Path);
        FileSystemVolume.RequireNoReparsePoints(targets[0].Folder);
        FileSystemVolume.RequireSameVolume(file.Path, move.DestinationPath);
        return new(file.Id, targets[0].Id, state.Revision, Path.GetFullPath(file.Path), Path.GetFullPath(move.DestinationPath));
    }
}
