using DeskNest.Core.Storage;

namespace DeskNest.Core.Workspace;

public sealed record UnconfirmedCopyInfo(Guid CopyId, string SourcePath, string TemporaryPath, string DestinationPath);

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
    private readonly Func<string, bool>? _copyMetadataGuard;
    private readonly CopyRecoveryJournal _copyJournal;
    private SemaphoreSlim _gate => _store.OrganizationGate;

    public ManualOrganizationCoordinator(WorkspaceStore store, DesktopOrganizationTransaction transaction,
        Func<string, bool>? copyMetadataGuard = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _transaction = transaction ?? throw new ArgumentNullException(nameof(transaction));
        _copyMetadataGuard = copyMetadataGuard;
        _copyJournal = new CopyRecoveryJournal(store.DataDirectory);
    }

    /// <summary>Record observed paths for review only, after any active file transaction finishes.</summary>
    public async Task<bool> RecordObservedPathsAsync(IReadOnlyList<string> paths, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_transaction.HasRecoveryJournal || _copyJournal.Exists)
                throw new InvalidOperationException("Resolve retained file recovery evidence before observing folders.");
            List<PendingFile> SelectNew(WorkspaceState state)
            {
                var excluded = state.Settings.ExcludedFolders.Concat(state.Spaces.Select(space => space.Folder))
                    .Concat([_store.DataDirectory, state.Settings.ManagedRoot, state.Settings.ModelCacheDirectory ?? state.Settings.ManagedRoot]);
                var additions = new List<PendingFile>();
                foreach (string input in paths)
                {
                    token.ThrowIfCancellationRequested();
                    if (!Path.IsPathFullyQualified(input)) continue;
                    string path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(input));
                    if (!state.Settings.MonitoredFolders.Any(root => IsDescendantPath(path, Path.GetFullPath(root))) ||
                        excluded.Any(root => PathComparer.Equals(path, Path.TrimEndingDirectorySeparator(root)) || IsDescendantPath(path, root)) ||
                        state.Files.Any(file => PathComparer.Equals(file.Path, path)) ||
                        state.Pending.Concat(additions).Any(item => PathComparer.Equals(item.Path, path) || IsDescendantPath(path, item.Path))) continue;
                    if (!File.Exists(path) && !Directory.Exists(path)) continue;
                    try { FileSystemVolume.RequireNoReparsePoints(path); }
                    catch (IOException) { continue; }
                    catch (UnauthorizedAccessException) { continue; }
                    if (state.Pending.Count + additions.Count >= 10_000)
                        throw new InvalidOperationException("Pending review is full; resolve items before scanning again.");
                    // The most specific selected source wins, even when it has no preset.
                    string sourceRoot = state.Settings.MonitoredFolders.Where(root => IsDescendantPath(path, Path.GetFullPath(root)))
                        .OrderByDescending(root => Path.GetFullPath(root).Length).First();
                    Guid? suggestion = state.Settings.MonitoredFolderTargets.TryGetValue(sourceRoot, out var target) ? target : null;
                    additions.Add(new PendingFile(Guid.NewGuid(), Path.GetFileName(path), path,
                        TriageReason.FilenameAmbiguous, suggestion, DateTimeOffset.UtcNow));
                }
                return additions;
            }
            if (SelectNew(_store.Snapshot).Count == 0) return false;
            try
            {
                await _store.UpdateAsync(state =>
                {
                    token.ThrowIfCancellationRequested();
                    var additions = SelectNew(state);
                    // A concurrent settings/catalog save can make every candidate obsolete.
                    if (additions.Count == 0) throw new OperationCanceledException();
                    return state with { Pending = [.. state.Pending, .. additions] };
                }, token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
        }
        finally { _gate.Release(); }
    }

    /// <summary>Read-only description for explicit user reconciliation; not file-operation authority.</summary>
    public async Task<UnconfirmedCopyInfo?> InspectUnconfirmedCopyAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return ReadUnconfirmedCopy() is { } intent ? DescribeCopy(intent) : null; }
        finally { _gate.Release(); }
    }

    /// <summary>User-confirmed abandonment of an unenrolled copy only. Keep every file and the exact journal.</summary>
    public async Task<string> ArchiveUnconfirmedCopyAsync(UnconfirmedCopyInfo expected, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var intent = ReadUnconfirmedCopy();
            if (intent is null || DescribeCopy(intent) != expected)
                throw new InvalidDataException("The unconfirmed copy changed; inspect recovery again.");
            await _store.CheckpointRecoveryBackupAsync().ConfigureAwait(false);
            return _copyJournal.Archive(intent);
        }
        finally { _gate.Release(); }
    }

    private CopyRecoveryIntent? ReadUnconfirmedCopy()
    {
        // Never bypass move recovery or verification of an already-enrolled publication.
        if (_transaction.HasRecoveryJournal || !_copyJournal.Exists) return null;
        var intent = _copyJournal.Read();
        return _store.Snapshot.Files.Any(f => f.Id == intent.FileId) ? null : intent;
    }

    private static UnconfirmedCopyInfo DescribeCopy(CopyRecoveryIntent intent) =>
        new(intent.FileId, intent.SourcePath, intent.TemporaryPath, intent.DestinationPath);

    public async Task RecoverPendingAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_copyJournal.Exists)
            {
                if (_transaction.HasRecoveryJournal)
                    throw new InvalidDataException("Copy and move recovery evidence overlap; reconcile manually.");
                try
                {
                    var intent = _copyJournal.Read();
                    var file = _store.Snapshot.Files.SingleOrDefault(f => f.Id == intent.FileId);
                    if (file is null || file.IsInTrash || file.Publication is null ||
                        file.SpaceId != intent.TargetSpaceId || file.IsDirectory != intent.IsDirectory ||
                        !PathComparer.Equals(file.Path, intent.DestinationPath))
                        throw new InvalidDataException($"Copy enrollment is unconfirmed at '{intent.DestinationPath}'; preserve source and staging '{intent.TemporaryPath}' for manual reconciliation.");
                    WindowsPublicationVerifier.Verify(file.Path, file.Publication, cancellationToken);
                    await _store.CheckpointRecoveryBackupAsync().ConfigureAwait(false);
                    _copyJournal.Complete(intent);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
                {
                    throw new InvalidDataException("Copy recovery requires manual reconciliation; no copied item was deleted.", ex);
                }
            }
            if (_transaction.HasRecoveryJournal)
            {
                try
                {
                    var committed = _store.Snapshot.Operations
                        .Where(o => o.CommittedTransactionId.HasValue)
                        .Select(o => o.CommittedTransactionId!.Value).ToHashSet();
                    if (committed.Count > 0)
                        await _store.CheckpointRecoveryBackupAsync().ConfigureAwait(false);
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

    // Explicit manual import only; callers must confirm source/target at the supplied revision.
    public async Task<ManualOrganizationResult> ImportPendingAsync(
        Guid pendingId, Guid targetSpaceId, long expectedWorkspaceRevision, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new NotSupportedException("External import currently requires the Windows NTFS move boundary.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = _store.Snapshot;
            if (snapshot.Revision != expectedWorkspaceRevision)
                throw new InvalidDataException("The workspace changed after selecting the import; review it again.");
            var pending = snapshot.Pending.SingleOrDefault(p => p.Id == pendingId)
                ?? throw new KeyNotFoundException("The pending import no longer exists.");
            string source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(pending.Path));
            if (snapshot.Files.Any(f => PathComparer.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(f.Path)), source)))
                throw new InvalidDataException("This item is already cataloged; use its existing move action.");
            if (!File.Exists(source) && !Directory.Exists(source))
                throw new FileNotFoundException("The pending import source is unavailable.", source);
            var file = new WorkspaceFile(Guid.NewGuid(), Guid.Empty, Path.GetFileName(source), source, Directory.Exists(source));
            var target = GetSpace(snapshot, targetSpaceId);
            string destination = GetSpaceLeafDestination(file, target);
            if (file.IsDirectory && IsDescendantPath(destination, source))
                throw new InvalidDataException("A directory cannot be imported into its own tree.");
            if (File.Exists(destination) || Directory.Exists(destination))
                throw new IOException("The import destination is occupied; nothing was changed.");
            FileSystemVolume.RequireNoReparsePoints(source);
            FileSystemVolume.RequireNoReparsePoints(destination);
            FileSystemVolume.RequireSameVolume(source, destination);
            return await MoveAndCommitAsync(file, target.Id, destination, null, cancellationToken,
                expectedWorkspaceRevision: expectedWorkspaceRevision,
                importSource: pending).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<ManualOrganizationResult> MoveFileAsync(
        Guid fileId, Guid targetSpaceId, CancellationToken cancellationToken = default,
        long? expectedWorkspaceRevision = null)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = _store.Snapshot;
            if (expectedWorkspaceRevision.HasValue && snapshot.Revision != expectedWorkspaceRevision.Value)
                throw new InvalidDataException("The workspace changed after selecting the move source; retry from the current state.");
            var file = GetFile(snapshot, fileId);
            var sourceSpace = GetSpace(snapshot, file.SpaceId);
            var targetSpace = GetSpace(snapshot, targetSpaceId);
            ValidateSource(file, sourceSpace);
            string destination = GetSpaceLeafDestination(file, targetSpace);
            return await MoveAndCommitAsync(file, targetSpace.Id, destination, null, cancellationToken,
                expectedWorkspaceRevision: expectedWorkspaceRevision).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Explicit catalog-to-space copy on Windows NTFS. Keeps the source; does not create move/undo history.</summary>
    public Task<ManualOrganizationResult> CopyFileAsync(Guid fileId, Guid targetSpaceId,
        long expectedWorkspaceRevision, CancellationToken cancellationToken = default) =>
        CopyFileAsync(fileId, targetSpaceId, cancellationToken, beforeCopyAcknowledgement: null,
            expectedWorkspaceRevision: expectedWorkspaceRevision);

    // Internal overload retains trusted fault barriers for existing recovery probes.
    internal async Task<ManualOrganizationResult> CopyFileAsync(
        Guid fileId, Guid targetSpaceId, CancellationToken cancellationToken = default,
        Action? beforeCopyAcknowledgement = null, Action? afterFirstStagedWrite = null,
        Action? beforeCopyPreparation = null, long? expectedWorkspaceRevision = null)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        WindowsFileCopyLease? fileCopy = null;
        WindowsDirectoryCopyLease? directoryCopy = null;
        try
        {
            if (_transaction.HasRecoveryJournal || _copyJournal.Exists)
                throw new InvalidOperationException("Resolve retained move/copy recovery evidence before another operation.");
            if (!OperatingSystem.IsWindows())
                throw new NotSupportedException("Verified handle-relative copy is currently available only on Windows NTFS.");
            var snapshot = _store.Snapshot;
            if (expectedWorkspaceRevision is { } revision && revision != snapshot.Revision)
                throw new InvalidDataException("The workspace changed after selecting the copy source; retry from the current state.");
            var file = GetFile(snapshot, fileId);
            var sourceSpace = GetSpace(snapshot, file.SpaceId);
            var targetSpace = GetSpace(snapshot, targetSpaceId);
            ValidateSource(file, sourceSpace);
            string source = Path.GetFullPath(file.Path);
            string destination = GetSpaceLeafDestination(file, targetSpace);
            // Refuse a known collision before writing an intent; publication still uses no-replace rename.
            if (File.Exists(destination) || Directory.Exists(destination))
                throw new IOException("The copy destination already exists.");
            Guid copiedId = Guid.NewGuid();
            var intent = new CopyRecoveryIntent(1, copiedId, file.Id, targetSpace.Id, source, destination, file.IsDirectory);
            await _copyJournal.BeginAsync(intent, cancellationToken).ConfigureAwait(false);
            beforeCopyPreparation?.Invoke(); // Trusted internal test barrier, never read from metadata.
            bool createDestinationParents = targetSpace.Mode == SpaceStorageMode.Managed;
            if (file.IsDirectory)
                directoryCopy = await WindowsDirectoryCopyLease.CreateAsync(source, destination, cancellationToken,
                    publicationId: copiedId, afterFirstStagedWrite: afterFirstStagedWrite,
                    createDestinationParents: createDestinationParents).ConfigureAwait(false);
            else
                fileCopy = await WindowsFileCopyLease.CreateAsync(source, destination, cancellationToken,
                    publicationId: copiedId, afterFirstStagedWrite: afterFirstStagedWrite,
                    createDestinationParents: createDestinationParents).ConfigureAwait(false);

            try
            {
                if (_copyMetadataGuard?.Invoke(destination) == false)
                    throw new IOException("Fault injection refused copy metadata enrollment.");
                await _store.UpdateAsync(state =>
                {
                    // Revalidate AFTER acquiring the metadata gate, not before awaiting it.
                    if (state.Revision != snapshot.Revision)
                        throw new InvalidOperationException("Workspace changed while the copy was being prepared.");
                    WorkspacePublicationEvidence publication;
                    if (file.IsDirectory)
                    {
                        var receipt = directoryCopy!.VerifyForEnrollment(cancellationToken);
                        publication = new(receipt.NativeId, receipt.VolumePath, 0, 0, "",
                            receipt.AncestorNativeIds[^1], receipt.Nodes.Select(node =>
                                new WorkspacePublishedNodeIdentity(node.RelativePath, node.NativeId, node.IsDirectory,
                                    node.File?.Length ?? 0, node.File?.LastWriteTimeUtcTicks ?? 0,
                                    node.File?.Sha256 ?? "")).ToList())
                        { AncestorNativeIds = receipt.AncestorNativeIds.ToList() };
                    }
                    else
                    {
                        var identity = fileCopy!.VerifyForEnrollment(cancellationToken);
                        var ancestors = fileCopy.DestinationAncestorNativeIds;
                        publication = new(identity.NativeId!, fileCopy.DestinationVolumePath, identity.Length,
                            identity.LastWriteTimeUtcTicks, identity.Sha256, ancestors[^1], null)
                        { AncestorNativeIds = ancestors.ToList() };
                    }
                    return state with
                    {
                        Files = [.. state.Files, new WorkspaceFile(copiedId, targetSpace.Id,
                            Path.GetFileName(destination), destination, file.IsDirectory) { Publication = publication }]
                    };
                }, cancellationToken).ConfigureAwait(false);
                // Keep the published handles until backup evidence matches the committed state.
                await _store.CheckpointRecoveryBackupAsync().ConfigureAwait(false);
                if (file.IsDirectory) directoryCopy!.VerifyForEnrollment();
                else fileCopy!.VerifyForEnrollment();
                // Trusted test-only fault barrier on this internal prototype; never supplied from metadata.
                beforeCopyAcknowledgement?.Invoke();
                _copyJournal.Complete(intent);
            }
            catch (Exception ex)
            {
                // A failed save may still have reached durable storage. The retained native lease
                // prevents replacement while this call unwinds; no pathname delete is attempted.
                throw new IOException($"Copy published at '{destination}' but workspace enrollment is unconfirmed; reconcile manually.", ex);
            }
            return new(copiedId, file.SpaceId, targetSpace.Id, source, destination,
                OrganizationTransactionStatus.Completed);
        }
        finally
        {
            directoryCopy?.Dispose();
            fileCopy?.Dispose();
            _gate.Release();
        }
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
                operation.DestinationPath is null || operation.SourceSpaceId == Guid.Empty && operation.ImportSource is null)
                throw new InvalidDataException("Only completed operations with recovery paths can be undone.");
            var file = GetFile(snapshot, operation.FileId);
            if (operation.ImportSource is { } import)
            {
                string originalPath = Path.GetFullPath(operation.SourcePath);
                if (!Directory.Exists(Path.GetDirectoryName(originalPath)))
                    throw new DirectoryNotFoundException("The original external parent is unavailable; it will not be recreated.");
                if (snapshot.Pending.Any(p => p.Id == import.Id || PathComparer.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(p.Path)), originalPath)) ||
                    snapshot.Files.Any(f => f.Id != file.Id && PathComparer.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(f.Path)), originalPath)) ||
                    snapshot.Operations.Any(o => o.FileId == file.Id && o.Id != operation.Id && o.Status != ProposedOperationStatus.Undone))
                    throw new InvalidDataException("Resolve this item's later operations or pending entries before undoing its import.");
            }
            else GetSpace(snapshot, operation.SourceSpaceId);
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
                        new FileIdentity(item.Length, item.LastWriteTimeUtcTicks, item.Sha256)
                        { NativeId = item.NativeId })).ToArray();
                    if (!DesktopOrganizationTransaction.DirectoryManifestMatches(file.Path, expected,
                        operation.OriginalDirectoryPaths, operation.OriginalDirectoryNativeIds))
                        throw new IOException("Undo refused because the moved directory contents changed.");
                }
                else
                {
                    if (operation.OriginalLength is null || operation.OriginalLastWriteUtcTicks is null ||
                        string.IsNullOrWhiteSpace(operation.OriginalSha256))
                        throw new InvalidDataException("The operation has no complete source identity for undo.");
                    var expected = new FileIdentity(operation.OriginalLength.Value,
                        operation.OriginalLastWriteUtcTicks.Value, operation.OriginalSha256)
                    { NativeId = operation.OriginalNativeId };
                    if (!DesktopOrganizationTransaction.IdentityMatches(file.Path, expected))
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
        CancellationToken cancellationToken, bool markInTrash = false, Guid? requestedOperationId = null,
        long? expectedWorkspaceRevision = null, PendingFile? importSource = null)
    {
        if (_transaction.HasRecoveryJournal || _copyJournal.Exists)
            throw new InvalidOperationException("Resolve retained move/copy recovery evidence before another operation.");
        string source = Path.GetFullPath(file.Path);
        var operationId = requestedOperationId ?? undo?.Id ?? Guid.NewGuid();
        if (undo is null)
        {
            var identity = file.IsDirectory ? null : FileIdentity.Capture(source, cancellationToken);
            await _store.UpdateAsync(state =>
            {
                if (expectedWorkspaceRevision.HasValue && state.Revision != expectedWorkspaceRevision.Value)
                    throw new InvalidDataException("The workspace changed before preparing the move; retry from the current state.");
                return state with
                {
                    Operations = [.. state.Operations, new ProposedOperation(operationId, file.Id, targetSpaceId,
                        ProposedOperationStatus.PendingUser, DateTimeOffset.UtcNow)
                    {
                        SourceSpaceId = file.SpaceId, SourcePath = source, DestinationPath = destination,
                        ImportSource = importSource,
                        OriginalLength = identity?.Length, OriginalLastWriteUtcTicks = identity?.LastWriteTimeUtcTicks,
                        OriginalSha256 = identity?.Sha256, OriginalNativeId = identity?.NativeId
                    }]
                };
            }, cancellationToken).ConfigureAwait(false);
        }

        bool metadataCommitted = false;
        try
        {
            // Keep undo bound to the original receipt across the verification/prepare gap.
            var expectedFile = undo is not null && !file.IsDirectory
                ? new FileIdentity(undo.OriginalLength!.Value, undo.OriginalLastWriteUtcTicks!.Value, undo.OriginalSha256!)
                    { NativeId = undo.OriginalNativeId } : null;
            var expectedDirectory = undo is not null && file.IsDirectory
                ? new OrganizationDirectoryMoveReceipt(source, destination,
                    undo.OriginalDirectoryManifest!.Select(item => new DirectoryFileReceipt(item.RelativePath,
                        new FileIdentity(item.Length, item.LastWriteTimeUtcTicks, item.Sha256) { NativeId = item.NativeId })).ToArray(), false)
                    { Directories = undo.OriginalDirectoryPaths, DirectoryNativeIds = undo.OriginalDirectoryNativeIds } : null;
            var result = file.IsDirectory
                ? await _transaction.ExecuteDirectoriesAsync([new(source, destination) { ExpectedReceipt = expectedDirectory }], cancellationToken,
                    retainJournalUntilCommit: true).ConfigureAwait(false)
                : await _transaction.ExecuteAsync([new(source, destination) { ExpectedIdentity = expectedFile }], cancellationToken,
                    retainJournalUntilCommit: true).ConfigureAwait(false);
            var directoryReceipt = result.DirectoryReceipts?.SingleOrDefault();
            var fileReceipt = result.Receipts.SingleOrDefault();
            var committedIdentity = fileReceipt?.Identity;
            var manifest = directoryReceipt?.Files.Select(item =>
                new WorkspaceDirectoryFileIdentity(item.RelativePath, item.Identity.Length,
                    item.Identity.LastWriteTimeUtcTicks, item.Identity.Sha256)
                { NativeId = item.Identity.NativeId }).ToList();
            var directories = directoryReceipt?.Directories?.ToList();
            var directoryNativeIds = directoryReceipt?.DirectoryNativeIds?.ToDictionary(pair => pair.Key, pair => pair.Value);
            if (file.IsDirectory && (manifest is null || directories is null))
                throw new InvalidDataException("The directory transaction returned no recovery manifest.");

            await _store.UpdateAsync(state =>
            {
                var catalog = importSource is not null ? state.Files.Append(file) : state.Files;
                var pending = state.Pending.Where(item => !PathComparer.Equals(Path.GetFullPath(item.Path), source)).ToList();
                if (undo?.ImportSource is { } restored)
                {
                    if (pending.Any(p => p.Id == restored.Id || PathComparer.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(p.Path)), destination)))
                        throw new InvalidDataException("The pending import changed during undo; recovery evidence is retained.");
                    catalog = catalog.Where(item => item.Id != file.Id);
                    pending.Add(restored);
                }
                return state with
                {
                Files = catalog.Select(item => item.Id == file.Id
                    ? item with
                    {
                        SpaceId = targetSpaceId,
                        Path = destination,
                        Name = Path.GetFileName(destination),
                        IsInTrash = undo is null && markInTrash
                    } : item).ToList(),
                Pending = pending,
                Operations = state.Operations.Select(item => item.Id == operationId
                    ? item with
                    {
                        Status = undo is null ? ProposedOperationStatus.Completed : ProposedOperationStatus.Undone,
                        CommittedTransactionId = result.OperationId,
                        OriginalLength = undo is null ? committedIdentity?.Length : item.OriginalLength,
                        OriginalLastWriteUtcTicks = undo is null ? committedIdentity?.LastWriteTimeUtcTicks : item.OriginalLastWriteUtcTicks,
                        OriginalSha256 = undo is null ? committedIdentity?.Sha256 : item.OriginalSha256,
                        OriginalNativeId = undo is null ? committedIdentity?.NativeId : item.OriginalNativeId,
                        OriginalDirectoryManifest = undo is null ? manifest : item.OriginalDirectoryManifest,
                        OriginalDirectoryPaths = undo is null ? directories : item.OriginalDirectoryPaths,
                        OriginalDirectoryNativeIds = undo is null ? directoryNativeIds : item.OriginalDirectoryNativeIds
                    } : item).ToList()
                };
            }, cancellationToken).ConfigureAwait(false);
            metadataCommitted = true;
            await _store.CheckpointRecoveryBackupAsync().ConfigureAwait(false);
            await _transaction.CommitAsync(result.OperationId, CancellationToken.None).ConfigureAwait(false);
            return new(file.Id, file.SpaceId, targetSpaceId, source, destination, result.Status);
        }
        catch
        {
            if (!metadataCommitted) await MarkRecoveryRequiredAsync(operationId).ConfigureAwait(false);
            throw;
        }
    }

    private Task<WorkspaceState> MarkRecoveryRequiredAsync(Guid operationId) => _store.UpdateAsync(state => state with
    {
        Operations = state.Operations.Select(item => item.Id == operationId
            ? item with { Status = ProposedOperationStatus.RecoveryRequired } : item).ToList()
    }, CancellationToken.None);

    public async Task<ManualOrganizationResult> DeleteManagedFileAsync(
        Guid fileId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = _store.Snapshot;
            var file = GetFile(snapshot, fileId);
            var space = GetSpace(snapshot, file.SpaceId);
            if (space.Mode != SpaceStorageMode.Managed)
                throw new InvalidDataException("Mapped references must use catalog removal, not managed deletion.");
            ValidateSource(file, space);
            var operationId = Guid.NewGuid();
            string destination = Path.Combine(Path.GetFullPath(space.Folder), ".desknest-trash",
                operationId.ToString("N"), file.Name);
            return await MoveAndCommitAsync(file, space.Id, destination, null, cancellationToken,
                markInTrash: true, requestedOperationId: operationId).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveMappedReferenceAsync(Guid fileId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = _store.Snapshot;
            var file = GetFile(snapshot, fileId);
            if (GetSpace(snapshot, file.SpaceId).Mode != SpaceStorageMode.Mapped)
                throw new InvalidDataException("Only mapped references can be removed without a file transaction.");
            if (snapshot.Operations.Any(o => o.FileId == fileId))
                throw new InvalidOperationException("This reference has operation history and cannot be removed yet.");
            await _store.UpdateAsync(state => state with
            {
                Files = state.Files.Where(item => item.Id != fileId).ToList()
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private static string GetSpaceLeafDestination(WorkspaceFile file, WorkspaceSpace targetSpace)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetSpace.Folder));
        string destination = Path.GetFullPath(Path.Combine(root, file.Name));
        if (!IsDescendantPath(destination, root) ||
            !PathComparer.Equals(Path.GetDirectoryName(destination), root))
            throw new InvalidDataException("The target item would escape the target space.");
        if (File.Exists(root) || (targetSpace.Mode == SpaceStorageMode.Mapped && !Directory.Exists(root)))
            throw new DirectoryNotFoundException("The target space folder is unavailable.");
        return destination;
    }

    private static WorkspaceFile GetFile(WorkspaceState state, Guid id) =>
        state.Files.FirstOrDefault(f => f.Id == id) ?? throw new KeyNotFoundException($"Workspace file was not found: {id}");

    private static WorkspaceSpace GetSpace(WorkspaceState state, Guid id) =>
        state.Spaces.FirstOrDefault(s => s.Id == id) ?? throw new InvalidDataException($"Workspace space was not found: {id}");

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static void ValidateSource(WorkspaceFile file, WorkspaceSpace space)
    {
        if (file.IsInTrash)
            throw new InvalidOperationException("Restore a trashed item through its recorded operation first.");
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
