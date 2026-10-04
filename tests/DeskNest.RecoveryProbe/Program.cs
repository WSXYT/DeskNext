using System.Text.Json;
using DeskNest.Core.Storage;
using DeskNest.Core.Workspace;

string[] scenarios = ["file", "directory", "committed-file", "committed-directory",
    "rename-file", "rename-directory", "delete-file", "delete-directory", "undo-file", "undo-directory",
    "copy-file", "copy-directory", "committed-copy-file", "committed-copy-directory",
    "unreceipted-file", "unreceipted-directory", "reverse-unreceipted-file", "reverse-unreceipted-directory",
    "staged-copy-file", "staged-copy-directory"];
if (args.Length != 2 || (args[0] != "recover" &&
    !scenarios.Any(kind => args[0] == "prepare-" + kind)))
    throw new ArgumentException("Usage: prepare-<scenario>|recover <isolated-temp-root>");

string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[1]));
string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
if (!string.Equals(Path.GetDirectoryName(root), temp, comparison) ||
    !Path.GetFileName(root).StartsWith("DeskNext.RecoveryProbe-", StringComparison.Ordinal) ||
    !Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
    throw new ArgumentException("The probe requires an existing, direct child of the system temp directory.");

string dataDir = Path.Combine(root, "workspace");
string journal = Path.Combine(dataDir, "organization-recovery.json");
string ready = Path.Combine(root, "ready");
string sourceDir = Path.Combine(root, "source");
string targetDir = Path.Combine(root, "target");
var transaction = new DesktopOrganizationTransaction(journal);
if (args[0] == "recover") RestoreJournalPermissions(root, journal);

if (args[0] != "recover")
{
    // The driver allocates a fresh mkdtemp root. Never overwrite an existing fixture.
    if (Directory.EnumerateFileSystemEntries(root).Any())
        throw new IOException("Preparation requires an empty fixture directory.");
    await VerifyJournalObserverAsync(root);
    bool directory = args[0].EndsWith("directory", StringComparison.Ordinal);
    bool stagedCopy = args[0].StartsWith("prepare-staged-copy-", StringComparison.Ordinal);
    bool committedCopy = args[0].StartsWith("prepare-committed-copy-", StringComparison.Ordinal);
    bool copy = stagedCopy || committedCopy || args[0].StartsWith("prepare-copy-", StringComparison.Ordinal);
    bool undo = args[0].StartsWith("prepare-undo-", StringComparison.Ordinal);
    bool coordinated = copy || undo || args[0].StartsWith("prepare-rename-", StringComparison.Ordinal) ||
        args[0].StartsWith("prepare-delete-", StringComparison.Ordinal);
    string name = directory ? "fixture-tree" : "fixture.txt";
    string source = Path.Combine(sourceDir, name);
    string destination = Path.Combine(targetDir, name);
    Directory.CreateDirectory(sourceDir);
    Directory.CreateDirectory(targetDir);
    if (directory)
    {
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        File.WriteAllText(Path.Combine(source, "nested", "receipt.txt"), "recovery-fixture");
    }
    else File.WriteAllText(source, "recovery-fixture");

    await using var store = await WorkspaceStore.OpenAsync(dataDir);
    var sourceSpace = new WorkspaceSpace(Guid.NewGuid(), "Source", "Isolated probe", SpaceStorageMode.Managed, sourceDir);
    var targetSpace = new WorkspaceSpace(Guid.NewGuid(), "Target", "Isolated probe", SpaceStorageMode.Managed, targetDir);
    var file = new WorkspaceFile(Guid.NewGuid(), sourceSpace.Id, name, source, directory);
    var operation = new ProposedOperation(Guid.NewGuid(), file.Id, targetSpace.Id,
        ProposedOperationStatus.PendingUser, DateTimeOffset.UtcNow)
    {
        SourceSpaceId = sourceSpace.Id, SourcePath = source, DestinationPath = destination
    };
    await store.UpdateAsync(state => state with
    {
        OnboardingComplete = true, OnboardingStep = 5, Spaces = [sourceSpace, targetSpace],
        Files = [file], Operations = coordinated ? [] : [operation]
    });

    if (args[0].StartsWith("prepare-unreceipted-", StringComparison.Ordinal))
    {
        Action? releaseJournal = null;
        byte[]? preparedBytes = null;
        bool DenyJournalReplacement()
        {
            preparedBytes = File.ReadAllBytes(journal);
            // Real I/O fault: deny replacement via Windows sharing or Unix directory permissions.
            releaseJournal = DenyJournalWrites(root, journal);
            return true;
        }
        var faulted = new DesktopOrganizationTransaction(journal,
            moveGuard: _ => DenyJournalReplacement(), directoryMoveGuard: _ => DenyJournalReplacement());
        try
        {
            bool refusedWrite = false;
            try
            {
                if (directory) await faulted.ExecuteDirectoriesAsync([new(source, destination)], retainJournalUntilCommit: true);
                else await faulted.ExecuteAsync([new(source, destination)], retainJournalUntilCommit: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { refusedWrite = true; }
            var retained = JsonSerializer.Deserialize<OrganizationRecoveryJournal>(await ReadObservedJournalAsync(journal),
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            if (!refusedWrite || releaseJournal is null || preparedBytes is null ||
                !preparedBytes.SequenceEqual(File.ReadAllBytes(journal)) || retained.Status != "Prepared" ||
                (directory ? retained.DirectoryMoves!.Single().Completed : retained.Moves.Single().Completed) ||
                File.Exists(source) || Directory.Exists(source) ||
                !(directory ? Directory.Exists(destination) : File.Exists(destination)))
                throw new IOException("The journal write fault did not leave an actual rename without a durable receipt.");
            WriteReady(ready);
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }
        finally { releaseJournal?.Invoke(); }
        return;
    }
    if (stagedCopy)
    {
        string originalContent = directory ? Path.Combine(source, "nested", "receipt.txt") : source;
        File.WriteAllText(originalContent, "recovery-fixture" + new string('x', 200_000));
        await new ManualOrganizationCoordinator(store, transaction).CopyFileAsync(file.Id, targetSpace.Id,
            afterFirstStagedWrite: () =>
            {
                var intent = new CopyRecoveryJournal(dataDir).Read();
                string stagedContent = directory ? Path.Combine(intent.TemporaryPath, "nested", "receipt.txt") : intent.TemporaryPath;
                if (intent.SourceFileId != file.Id || File.Exists(destination) || Directory.Exists(destination) ||
                    new FileInfo(stagedContent).Length != 65_536 || store.Snapshot.Files.Count != 1 ||
                    store.Snapshot.Operations.Count != 0 || transaction.HasRecoveryJournal)
                    throw new IOException("Copy did not reach its partial staged-write boundary.");
                WriteReady(ready);
                Thread.Sleep(Timeout.Infinite);
            });
        throw new IOException("Staged copy escaped the forced-termination barrier.");
    }
    if (committedCopy)
    {
        var copyCoordinator = new ManualOrganizationCoordinator(store, transaction);
        await copyCoordinator.CopyFileAsync(file.Id, targetSpace.Id, beforeCopyAcknowledgement: () =>
        {
            var intent = new CopyRecoveryJournal(dataDir).Read();
            var enrolled = store.Snapshot.Files.Single(item => item.Id == intent.FileId);
            byte[] primary = File.ReadAllBytes(Path.Combine(dataDir, "workspace.json"));
            using var persisted = JsonDocument.Parse(primary);
            if (intent.SourceFileId != file.Id || intent.DestinationPath != destination ||
                enrolled.Path != destination || enrolled.Publication is null || store.Snapshot.Files.Count != 2 ||
                store.Snapshot.Operations.Count != 0 || transaction.HasRecoveryJournal ||
                !primary.SequenceEqual(File.ReadAllBytes(Path.Combine(dataDir, "workspace.json.bak"))) ||
                !persisted.RootElement.GetProperty("files").EnumerateArray()
                    .Any(item => item.GetProperty("id").GetGuid() == intent.FileId))
                throw new IOException("Copy did not reach the checkpointed metadata / retained intent boundary.");
            WriteReady(ready);
            Thread.Sleep(Timeout.Infinite); // Test-only, fresh isolated root; driver forcibly terminates it.
        });
        throw new IOException("Committed copy escaped the forced-termination barrier.");
    }
    if (copy)
    {
        var copyCoordinator = new ManualOrganizationCoordinator(store, transaction, copyMetadataGuard: published =>
        {
            var intent = new CopyRecoveryJournal(dataDir).Read();
            if (intent.SourceFileId != file.Id || intent.DestinationPath != published ||
                store.Snapshot.Files.Count != 1 || store.Snapshot.Operations.Count != 0 ||
                !(directory ? Directory.Exists(source) && Directory.Exists(published) :
                    File.Exists(source) && File.Exists(published)))
                throw new IOException("Copy did not reach the published / unenrolled boundary.");
            WriteReady(ready);
            Thread.Sleep(Timeout.Infinite); // Test-only barrier; driver forcibly terminates this process.
            return false;
        });
        await copyCoordinator.CopyFileAsync(file.Id, targetSpace.Id);
        throw new IOException("Copy escaped the forced-termination barrier.");
    }
    if (coordinated)
    {
        Guid? undoOperationId = null;
        if (undo)
        {
            await new ManualOrganizationCoordinator(store, transaction).MoveFileAsync(file.Id, targetSpace.Id);
            file = store.Snapshot.Files.Single();
            var committedOperation = store.Snapshot.Operations.Single();
            if (committedOperation.Status != ProposedOperationStatus.Completed ||
                committedOperation.CommittedTransactionId is null || transaction.HasRecoveryJournal)
                throw new IOException("The undo fixture requires a completely committed forward move.");
            undoOperationId = committedOperation.Id;
        }
        await RunCoordinatorUntilKilledAsync(store, journal, ready, file,
            delete: args[0].StartsWith("prepare-delete-", StringComparison.Ordinal), undoOperationId);
        return;
    }

    OrganizationTransactionResult result = directory
        ? await transaction.ExecuteDirectoriesAsync([new(source, destination)], retainJournalUntilCommit: true)
        : await transaction.ExecuteAsync([new(source, destination)], retainJournalUntilCommit: true);
    if (args[0].StartsWith("prepare-reverse-unreceipted-", StringComparison.Ordinal))
    {
        Action? releaseJournal = null;
        var interruptedRestore = new DesktopOrganizationTransaction(journal, restoreGuard: _ =>
        {
            releaseJournal = DenyJournalWrites(root, journal);
            return true;
        });
        try
        {
            bool refusedWrite = false;
            try { await interruptedRestore.RecoverAsync(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { refusedWrite = true; }
            var retained = JsonSerializer.Deserialize<OrganizationRecoveryJournal>(await ReadObservedJournalAsync(journal),
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            bool uncheckpointed = directory
                ? retained.DirectoryMoves?.Single() is { Completed: true, Restored: false }
                : retained.Moves.Single() is { Completed: true, Restored: false };
            if (!refusedWrite || releaseJournal is null || retained.Status != "Recovering" || !uncheckpointed ||
                !File.Exists(journal + ".rollback-started") ||
                File.ReadAllText(journal + ".rollback-started") != retained.OperationId.ToString("N") ||
                !(directory ? Directory.Exists(source) : File.Exists(source)) ||
                File.Exists(destination) || Directory.Exists(destination))
                throw new IOException("Reverse rename did not reach the failed Restored-checkpoint boundary.");
            WriteReady(ready);
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }
        finally { releaseJournal?.Invoke(); }
        return;
    }
    if (args[0].StartsWith("prepare-committed-", StringComparison.Ordinal))
    {
        var identity = result.Receipts.SingleOrDefault()?.Identity;
        var treeReceipt = result.DirectoryReceipts?.SingleOrDefault();
        await store.UpdateAsync(state => state with
        {
            Files = state.Files.Select(item => item.Id == file.Id
                ? item with { SpaceId = targetSpace.Id, Path = destination } : item).ToList(),
            Operations = state.Operations.Select(item => item.Id == operation.Id
                ? item with
                {
                    Status = ProposedOperationStatus.Completed,
                    CommittedTransactionId = result.OperationId,
                    OriginalLength = identity?.Length,
                    OriginalLastWriteUtcTicks = identity?.LastWriteTimeUtcTicks,
                    OriginalSha256 = identity?.Sha256,
                    OriginalNativeId = identity?.NativeId,
                    OriginalDirectoryPaths = treeReceipt?.Directories?.ToList(),
                    OriginalDirectoryNativeIds = treeReceipt?.DirectoryNativeIds?.ToDictionary(pair => pair.Key, pair => pair.Value),
                    OriginalDirectoryManifest = treeReceipt?.Files.Select(entry =>
                        new WorkspaceDirectoryFileIdentity(entry.RelativePath, entry.Identity.Length,
                            entry.Identity.LastWriteTimeUtcTicks, entry.Identity.Sha256)
                        { NativeId = entry.Identity.NativeId }).ToList()
                } : item).ToList()
        });
        // Stop after metadata persistence, before the coordinator's backup checkpoint/acknowledgement.
    }
    if (!transaction.HasRecoveryJournal || File.Exists(source) || Directory.Exists(source) ||
        !(directory ? Directory.Exists(destination) : File.Exists(destination)))
        throw new IOException("The probe did not reach the retained-journal post-move boundary.");

    WriteReady(ready);
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return;
}

await using (var store = await WorkspaceStore.OpenAsync(dataDir))
{
    var coordinator = new ManualOrganizationCoordinator(store, transaction);
    var copyJournal = new CopyRecoveryJournal(dataDir);
    if (copyJournal.Exists)
    {
        byte[] before = File.ReadAllBytes(copyJournal.JournalPath);
        var intent = copyJournal.Read();
        if (File.Exists(intent.TemporaryPath) || Directory.Exists(intent.TemporaryPath))
        {
            string stagingSourceContent = intent.IsDirectory ? Path.Combine(intent.SourcePath, "nested", "receipt.txt") : intent.SourcePath;
            string stagedContent = intent.IsDirectory ? Path.Combine(intent.TemporaryPath, "nested", "receipt.txt") : intent.TemporaryPath;
            byte[] stagedBefore = File.ReadAllBytes(stagedContent);
            string metadataBefore = JsonSerializer.Serialize(store.Snapshot);
            bool refusedStage = false;
            try { await coordinator.RecoverPendingAsync(); }
            catch (InvalidDataException) { refusedStage = true; }
            bool blockedMove = false;
            try { await coordinator.MoveFileAsync(intent.SourceFileId, intent.TargetSpaceId); }
            catch (InvalidOperationException) { blockedMove = true; }
            byte[] sourceBytes = File.ReadAllBytes(stagingSourceContent);
            if (!refusedStage || !blockedMove || !before.SequenceEqual(File.ReadAllBytes(copyJournal.JournalPath)) ||
                metadataBefore != JsonSerializer.Serialize(store.Snapshot) || stagedBefore.Length != 65_536 ||
                System.Text.Encoding.UTF8.GetString(sourceBytes) != "recovery-fixture" + new string('x', 200_000) ||
                !stagedBefore.SequenceEqual(sourceBytes.Take(stagedBefore.Length)) ||
                !stagedBefore.SequenceEqual(File.ReadAllBytes(stagedContent)) ||
                File.Exists(intent.DestinationPath) || Directory.Exists(intent.DestinationPath) ||
                intent.IsDirectory && !Directory.Exists(Path.Combine(intent.TemporaryPath, "empty")))
                throw new IOException("Partial copy staging was not retained without publication or adoption.");
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                Success = true, StagingPreserved = true, SourcePreserved = true, IntentPreserved = true,
                PublicationAbsent = true, MetadataUnchanged = true, SubsequentMoveBlocked = true,
                ManualReconciliationRequired = true, JournalCleared = false
            }));
            return;
        }
        if (store.Snapshot.Files.SingleOrDefault(item => item.Id == intent.FileId) is { Publication: not null } enrolled)
        {
            string receiptBefore = JsonSerializer.Serialize(enrolled.Publication);
            await coordinator.RecoverPendingAsync();
            var after = store.Snapshot.Files.Single(item => item.Id == intent.FileId);
            WindowsPublicationVerifier.Verify(after.Path, after.Publication!);
            string originalContent = intent.IsDirectory ? Path.Combine(intent.SourcePath, "nested", "receipt.txt") : intent.SourcePath;
            string publishedContent = intent.IsDirectory ? Path.Combine(intent.DestinationPath, "nested", "receipt.txt") : intent.DestinationPath;
            if (copyJournal.Exists || transaction.HasRecoveryJournal || store.Snapshot.Files.Count != 2 ||
                store.Snapshot.Operations.Count != 0 || after.Path != intent.DestinationPath || after.IsInTrash ||
                receiptBefore != JsonSerializer.Serialize(after.Publication) ||
                !store.Snapshot.Files.Any(item => item.Id == intent.SourceFileId && item.Path == intent.SourcePath) ||
                File.ReadAllText(originalContent) != "recovery-fixture" || File.ReadAllText(publishedContent) != "recovery-fixture" ||
                intent.IsDirectory && (!Directory.Exists(Path.Combine(intent.SourcePath, "empty")) ||
                                      !Directory.Exists(Path.Combine(intent.DestinationPath, "empty"))))
                throw new IOException("Committed copy did not retain both objects and its original durable receipt.");
            await coordinator.RecoverPendingAsync(); // Acknowledgement is idempotent on the next startup.
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                Success = true, CopyRetained = true, SourcePreserved = true, CommittedPreserved = true,
                ReceiptPreserved = true, JournalCleared = true, ManualReconciliationRequired = false
            }));
            return;
        }
        bool refused = false;
        try { await coordinator.RecoverPendingAsync(); }
        catch (InvalidDataException) { refused = true; }
        string sourceContent = intent.IsDirectory ? Path.Combine(intent.SourcePath, "nested", "receipt.txt") : intent.SourcePath;
        string copyContent = intent.IsDirectory ? Path.Combine(intent.DestinationPath, "nested", "receipt.txt") : intent.DestinationPath;
        if (!refused || !copyJournal.Exists || !before.SequenceEqual(File.ReadAllBytes(copyJournal.JournalPath)) ||
            File.ReadAllText(sourceContent) != "recovery-fixture" || File.ReadAllText(copyContent) != "recovery-fixture" ||
            store.Snapshot.Files.Count != 1 || store.Snapshot.Files[0].Id != intent.SourceFileId ||
            store.Snapshot.Operations.Count != 0 ||
            intent.IsDirectory && (!Directory.Exists(Path.Combine(intent.SourcePath, "empty")) ||
                                  !Directory.Exists(Path.Combine(intent.DestinationPath, "empty"))))
            throw new IOException("Interrupted copy did not preserve its intent, original and unadopted publication.");
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Success = true, CopyRetained = true, SourcePreserved = true, IntentPreserved = true,
            PublicationNotAdopted = true, ManualReconciliationRequired = true, JournalCleared = false
        }));
        return;
    }
    var observed = JsonSerializer.Deserialize<OrganizationRecoveryJournal>(await ReadObservedJournalAsync(journal),
        new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    if (observed.Status == "Recovering" && File.Exists(journal + ".rollback-started"))
    {
        var item = store.Snapshot.Files.Single();
        string originalPath = store.Snapshot.Operations.Single().SourcePath!;
        string movedPath = store.Snapshot.Operations.Single().DestinationPath!;
        byte[] fence = File.ReadAllBytes(journal + ".rollback-started");
        string metadataBefore = JsonSerializer.Serialize(store.Snapshot);
        bool refused = false;
        try { await coordinator.RecoverPendingAsync(); }
        catch (InvalidDataException) { refused = true; }
        var after = JsonSerializer.Deserialize<OrganizationRecoveryJournal>(await ReadObservedJournalAsync(journal),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        string content = item.IsDirectory ? Path.Combine(originalPath, "nested", "receipt.txt") : originalPath;
        if (!refused || !transaction.HasRecoveryJournal ||
            !fence.SequenceEqual(File.ReadAllBytes(journal + ".rollback-started")) ||
            JsonSerializer.Serialize(after with { UpdatedAt = observed.UpdatedAt }) != JsonSerializer.Serialize(observed) ||
            metadataBefore != JsonSerializer.Serialize(store.Snapshot) ||
            File.Exists(movedPath) || Directory.Exists(movedPath) || File.ReadAllText(content) != "recovery-fixture" ||
            item.IsDirectory && !Directory.Exists(Path.Combine(originalPath, "empty")))
            throw new IOException("Uncheckpointed reverse rename lost its fence, evidence or restored source.");
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Success = true, ManualReconciliationRequired = true, JournalCleared = false,
            JournalEvidencePreserved = true, FencePreserved = true, SourcePreserved = true,
            DestinationAbsent = true, MetadataUnchanged = true
        }));
        return;
    }
    if (observed.Status == "Prepared")
    {
        var item = store.Snapshot.Files.Single();
        var unreceiptedOperation = store.Snapshot.Operations.Single();
        string unreceiptedDestination = unreceiptedOperation.DestinationPath!;
        byte[] before = File.ReadAllBytes(journal);
        string metadataBefore = JsonSerializer.Serialize(store.Snapshot);
        bool refused = false;
        try { await coordinator.RecoverPendingAsync(); }
        catch (InvalidDataException) { refused = true; }
        string content = item.IsDirectory ? Path.Combine(unreceiptedDestination, "nested", "receipt.txt") : unreceiptedDestination;
        if (!refused || !transaction.HasRecoveryJournal || File.Exists(item.Path) || Directory.Exists(item.Path) ||
            !before.SequenceEqual(File.ReadAllBytes(journal)) || metadataBefore != JsonSerializer.Serialize(store.Snapshot) ||
            File.ReadAllText(content) != "recovery-fixture" ||
            item.IsDirectory && !Directory.Exists(Path.Combine(unreceiptedDestination, "empty")))
            throw new IOException("Unreceipted rename was not preserved for manual reconciliation.");
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Success = true, ManualReconciliationRequired = true, JournalCleared = false,
            JournalPreserved = true, DestinationPreserved = true, SourceAbsent = true, MetadataUnchanged = true
        }));
        return;
    }
    await coordinator.RecoverPendingAsync();
    var state = store.Snapshot;
    var file = state.Files.Single();
    var operation = state.Operations.Single();
    string source = operation.SourcePath ?? throw new InvalidDataException("Missing source receipt.");
    string destination = operation.DestinationPath ?? throw new InvalidDataException("Missing destination receipt.");
    bool committed = operation.CommittedTransactionId.HasValue;
    bool sourceExists = file.IsDirectory ? Directory.Exists(source) : File.Exists(source);
    bool destinationExists = file.IsDirectory ? Directory.Exists(destination) : File.Exists(destination);
    if (transaction.HasRecoveryJournal ||
        (committed ? operation.Status != ProposedOperationStatus.Completed || sourceExists || !destinationExists ||
                     !string.Equals(file.Path, destination, comparison)
                   : operation.Status != ProposedOperationStatus.RecoveryRequired || !sourceExists || destinationExists ||
                     !string.Equals(file.Path, source, comparison) || file.IsInTrash))
        throw new IOException("Restart recovery did not respect the physical path and metadata commit boundary.");
    string recoveredPath = committed ? destination : source;
    string contentPath = file.IsDirectory ? Path.Combine(recoveredPath, "nested", "receipt.txt") : recoveredPath;
    if (File.ReadAllText(contentPath) != "recovery-fixture" ||
        file.IsDirectory && !Directory.Exists(Path.Combine(recoveredPath, "empty")))
        throw new IOException("Recovered file content or empty directory topology changed.");
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        Success = true, FileKind = file.IsDirectory ? "directory" : "file",
        SourceRestored = sourceExists, DestinationPresent = destinationExists,
        JournalCleared = true, CommittedPreserved = committed,
        PendingMarkedRecoveryRequired = !committed && operation.Status == ProposedOperationStatus.RecoveryRequired,
        EmptyDirectoryRestored = file.IsDirectory ? true : (bool?)null
    }));
}

// Test-only permission faults are limited to this validated, isolated fixture's workspace.
// The restart removes the I/O fault before exercising recovery, not the retained journal.
static Action DenyJournalWrites(string root, string journal)
{
    if (OperatingSystem.IsWindows())
    {
        var denied = new FileStream(journal, FileMode.Open, FileAccess.Read, FileShare.Read);
        return denied.Dispose;
    }
    string directory = Path.GetDirectoryName(journal)!;
    var mode = File.GetUnixFileMode(directory);
    File.WriteAllText(Path.Combine(root, "journal-directory-mode.json"), JsonSerializer.Serialize(mode));
    File.SetUnixFileMode(directory, mode & ~(UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite));
    return () => RestoreJournalPermissions(root, journal);
}

static void RestoreJournalPermissions(string root, string journal)
{
    if (OperatingSystem.IsWindows()) return;
    string savedMode = Path.Combine(root, "journal-directory-mode.json");
    if (!File.Exists(savedMode)) return;
    File.SetUnixFileMode(Path.GetDirectoryName(journal)!, JsonSerializer.Deserialize<UnixFileMode>(File.ReadAllText(savedMode)));
    File.Delete(savedMode);
}

// The observer must never acquire a read handle that blocks a transaction's atomic replace.
static async Task<string> ReadObservedJournalAsync(string path, Action? whileOpen = null)
{
    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
    using var reader = new StreamReader(stream);
    whileOpen?.Invoke();
    return await reader.ReadToEndAsync();
}

static async Task VerifyJournalObserverAsync(string root)
{
    string path = Path.Combine(root, "observer.json");
    string replacement = Path.Combine(root, "observer-next.json");
    await File.WriteAllTextAsync(path, "old snapshot");
    await File.WriteAllTextAsync(replacement, "new snapshot");
    // Deterministic: replace while the exact production-probe reader is still open.
    string observed = await ReadObservedJournalAsync(path, () => File.Replace(replacement, path, null));
    if (observed != "old snapshot" || await File.ReadAllTextAsync(path) != "new snapshot")
        throw new IOException("Journal observer did not retain a coherent snapshot across replacement.");
    File.Delete(path);
}

static void WriteReady(string ready)
{
    using var marker = new FileStream(ready, FileMode.CreateNew, FileAccess.Write, FileShare.None,
        4096, FileOptions.WriteThrough);
    marker.Write("ready"u8);
    marker.Flush(flushToDisk: true);
    Console.WriteLine("READY: durable recovery evidence and physical operation boundary verified; awaiting external termination.");
}

static async Task RunCoordinatorUntilKilledAsync(WorkspaceStore store, string journal,
    string ready, WorkspaceFile file, bool delete, Guid? undoOperationId)
{
    // Hold the public store-update gate before the coordinator persists its physical result.
    // The existing pre-move guard schedules this blocker; production code is unchanged.
    using var release = new ManualResetEventSlim();
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    Task? gateHolder = null;
    bool BlockMetadata()
    {
        gateHolder = Task.Run(() => store.UpdateAsync(state =>
        {
            entered.SetResult();
            release.Wait();
            return state;
        }));
        entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        return true;
    }

    var transaction = new DesktopOrganizationTransaction(journal,
        moveGuard: _ => BlockMetadata(), directoryMoveGuard: _ => BlockMetadata());
    var coordinator = new ManualOrganizationCoordinator(store, transaction);
    var action = Task.Run(() => undoOperationId.HasValue
        ? coordinator.UndoOperationAsync(undoOperationId.Value)
        : delete ? coordinator.DeleteManagedFileAsync(file.Id)
        : coordinator.RenameFileAsync(file.Id, file.IsDirectory ? "renamed-tree" : "renamed.txt"));
    try
    {
        var deadline = DateTime.UtcNow.AddSeconds(25);
        OrganizationRecoveryJournal? receipt = null;
        while (DateTime.UtcNow < deadline)
        {
            if (action.IsCompleted)
            {
                await action;
                throw new IOException("Coordinator escaped the blocked metadata boundary.");
            }
            try
            {
                if (File.Exists(journal))
                    receipt = JsonSerializer.Deserialize<OrganizationRecoveryJournal>(
                        await ReadObservedJournalAsync(journal), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }
            catch (IOException) { receipt = null; }
            catch (JsonException) { receipt = null; }
            bool completed = receipt?.Status == "Moving" && (file.IsDirectory
                ? receipt.DirectoryMoves is { Count: 1 } && receipt.DirectoryMoves[0].Completed
                : receipt.Moves is { Count: 1 } && receipt.Moves[0].Completed);
            if (completed) break;
            await Task.Delay(20);
        }
        var operation = store.Snapshot.Operations.Single();
        bool receipted = file.IsDirectory
            ? receipt?.DirectoryMoves is { Count: 1 } && receipt.DirectoryMoves[0].Completed
            : receipt?.Moves is { Count: 1 } && receipt.Moves[0].Completed;
        string? physicalSource = file.IsDirectory ? receipt?.DirectoryMoves?.SingleOrDefault()?.SourcePath
            : receipt?.Moves.SingleOrDefault()?.SourcePath;
        string? physicalDestination = file.IsDirectory ? receipt?.DirectoryMoves?.SingleOrDefault()?.DestinationPath
            : receipt?.Moves.SingleOrDefault()?.DestinationPath;
        bool metadataUncommitted = undoOperationId.HasValue
            ? operation.Status == ProposedOperationStatus.Completed && operation.CommittedTransactionId.HasValue &&
              operation.CommittedTransactionId != receipt?.OperationId && physicalDestination == operation.SourcePath
            : operation.Status == ProposedOperationStatus.PendingUser && operation.CommittedTransactionId is null &&
              physicalDestination == operation.DestinationPath;
        if (!entered.Task.IsCompletedSuccessfully || !receipted || receipt?.Status != "Moving" ||
            action.IsCompleted || !metadataUncommitted || physicalSource != file.Path ||
            File.Exists(file.Path) || Directory.Exists(file.Path) ||
            !(file.IsDirectory ? Directory.Exists(physicalDestination) : File.Exists(physicalDestination)))
            throw new IOException("Coordinator did not reach the durable receipt / blocked metadata boundary.");
        WriteReady(ready);
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }
    finally
    {
        release.Set();
        if (gateHolder is not null) await gateHolder;
        await action;
    }
}
