using System.Security.Cryptography;
using System.Text.Json;

namespace DeskNest.Core.Storage;

public sealed record OrganizationMove(string SourcePath, string DestinationPath);

public sealed record FileIdentity(
    long Length,
    long LastWriteTimeUtcTicks,
    string Sha256)
{
    public static FileIdentity Capture(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("Source file does not exist.", path);
        }

        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Reparse-point files are not eligible for a safe move.");
        }

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        string hash = Convert.ToHexString(SHA256.HashData(stream));
        return new FileIdentity(info.Length, info.LastWriteTimeUtc.Ticks, hash);
    }
}

public sealed record OrganizationMoveReceipt(
    string SourcePath,
    string DestinationPath,
    FileIdentity Identity,
    bool Completed);

public sealed record OrganizationDirectoryMove(string SourcePath, string DestinationPath);

public sealed record DirectoryFileReceipt(string RelativePath, FileIdentity Identity);

public sealed record OrganizationDirectoryMoveReceipt(
    string SourcePath,
    string DestinationPath,
    IReadOnlyList<DirectoryFileReceipt> Files,
    bool Completed);

public sealed record OrganizationRecoveryJournal(
    Guid OperationId,
    string Status,
    IReadOnlyList<OrganizationMoveReceipt> Moves,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<OrganizationDirectoryMoveReceipt>? DirectoryMoves = null);

public enum OrganizationTransactionStatus
{
    Completed,
    RecoveryRequired
}

public enum OrganizationDirectoryMoveSupport
{
    SameVolumeAtomicWithManifest,
    CrossVolumeCopyNotImplemented
}

public sealed record OrganizationTransactionResult(
    Guid OperationId,
    OrganizationTransactionStatus Status,
    IReadOnlyList<OrganizationMoveReceipt> Receipts,
    IReadOnlyList<OrganizationDirectoryMoveReceipt>? DirectoryReceipts = null);

/// <summary>
/// Fail-closed, journaled moves for the P3 manual-organization boundary.
/// Regular files and same-volume directories are supported; directory moves carry
/// a complete file manifest and never follow reparse points or overwrite destinations.
/// </summary>
public sealed class DesktopOrganizationTransaction
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };


    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly string _journalPath;
    private readonly Func<OrganizationMoveReceipt, bool>? _moveGuard;
    private readonly Func<OrganizationDirectoryMoveReceipt, bool>? _directoryMoveGuard;

    public DesktopOrganizationTransaction(
        string journalPath,
        Func<OrganizationMoveReceipt, bool>? moveGuard = null,
        Func<OrganizationDirectoryMoveReceipt, bool>? directoryMoveGuard = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journalPath);
        if (!Path.IsPathFullyQualified(journalPath))
            throw new ArgumentException("The journal path must be absolute.", nameof(journalPath));

        _journalPath = journalPath;
        _moveGuard = moveGuard;
        _directoryMoveGuard = directoryMoveGuard;
    }

    private string RecoveryMarkerPath => _journalPath + ".recovery-required";

    public bool HasRecoveryJournal => File.Exists(_journalPath) ||
        File.Exists(ResilientJsonStore.GetBackupPath(_journalPath)) || File.Exists(RecoveryMarkerPath);

    public OrganizationDirectoryMoveSupport DirectoryMoveSupport =>
        OrganizationDirectoryMoveSupport.SameVolumeAtomicWithManifest;

    public bool SupportsCrossVolumeDirectoryMoves => false;

    public async Task<OrganizationTransactionResult> ExecuteAsync(
        IReadOnlyList<OrganizationMove> requestedMoves,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestedMoves);
        if (requestedMoves.Count == 0)
        {
            throw new ArgumentException("At least one move is required.", nameof(requestedMoves));
        }

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var processLock = AcquireProcessLock();
            if (HasRecoveryJournal)
            {
                throw new InvalidOperationException(
                    "A recovery journal exists; recover or discard it explicitly before starting another operation.");
            }

            var prepared = Prepare(requestedMoves);
            var operationId = Guid.NewGuid();
            var journal = new OrganizationRecoveryJournal(
                operationId,
                "Prepared",
                prepared,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow);
            await SaveJournalAsync(journal, cancellationToken).ConfigureAwait(false);

            var completed = new List<OrganizationMoveReceipt>(prepared.Count);
            try
            {
                foreach (var move in prepared)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    MoveOne(move);
                    completed.Add(move with { Completed = true });
                    await SaveJournalAsync(journal with
                    {
                        Status = "Moving",
                        Moves = completed.Concat(prepared.Skip(completed.Count)).ToArray(),
                        UpdatedAt = DateTimeOffset.UtcNow
                    }, cancellationToken).ConfigureAwait(false);
                }

                DeleteJournalFiles();
                return new OrganizationTransactionResult(
                    operationId,
                    OrganizationTransactionStatus.Completed,
                    completed);
            }
            catch
            {
                var recoveryJournal = journal with
                {
                    Status = "RecoveryRequired",
                    Moves = completed.Concat(prepared.Skip(completed.Count)).ToArray(),
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                await SaveJournalAsync(recoveryJournal, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<OrganizationTransactionResult> ExecuteDirectoriesAsync(
        IReadOnlyList<OrganizationDirectoryMove> requestedMoves,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestedMoves);
        if (requestedMoves.Count == 0)
            throw new ArgumentException("At least one directory move is required.", nameof(requestedMoves));

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var processLock = AcquireProcessLock();
            if (HasRecoveryJournal)
                throw new InvalidOperationException("A recovery journal exists; recover it before starting another operation.");

            var prepared = PrepareDirectories(requestedMoves);
            var operationId = Guid.NewGuid();
            var journal = new OrganizationRecoveryJournal(
                operationId, "Prepared", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, prepared);
            await SaveJournalAsync(journal, cancellationToken).ConfigureAwait(false);
            var completed = new List<OrganizationDirectoryMoveReceipt>();
            try
            {
                foreach (var move in prepared)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    MoveDirectoryOne(move);
                    completed.Add(move with { Completed = true });
                    await SaveJournalAsync(journal with
                    {
                        Status = "Moving",
                        DirectoryMoves = completed.Concat(prepared.Skip(completed.Count)).ToArray(),
                        UpdatedAt = DateTimeOffset.UtcNow
                    }, cancellationToken).ConfigureAwait(false);
                }

                DeleteJournalFiles();
                return new OrganizationTransactionResult(operationId, OrganizationTransactionStatus.Completed, [], completed);
            }
            catch
            {
                await SaveJournalAsync(journal with
                {
                    Status = "RecoveryRequired",
                    DirectoryMoves = completed.Concat(prepared.Skip(completed.Count)).ToArray(),
                    UpdatedAt = DateTimeOffset.UtcNow
                }, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<OrganizationTransactionResult> RecoverAsync(
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var processLock = AcquireProcessLock();
            var journal = await LoadJournalAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("No recovery journal exists.", _journalPath);

            // A Prepared receipt may cover a crash after rename but before acknowledgement.
            // Never infer success from content alone or silently forget such an item.
            foreach (var pending in journal.Moves.Where(m => !m.Completed))
            {
                if (!File.Exists(pending.SourcePath) || File.Exists(pending.DestinationPath) ||
                    Directory.Exists(pending.DestinationPath) || FileIdentity.Capture(pending.SourcePath) != pending.Identity)
                    throw new IOException("Unacknowledged move requires manual reconciliation; journal retained.");
            }
            foreach (var pending in journal.DirectoryMoves?.Where(m => !m.Completed) ?? [])
            {
                if (!Directory.Exists(pending.SourcePath) || File.Exists(pending.DestinationPath) ||
                    Directory.Exists(pending.DestinationPath) ||
                    !CaptureDirectoryManifest(pending.SourcePath).SequenceEqual(pending.Files))
                    throw new IOException("Unacknowledged directory move requires manual reconciliation; journal retained.");
            }

            var restored = new List<OrganizationMoveReceipt>();
            var restoredDirectories = new List<OrganizationDirectoryMoveReceipt>();
            foreach (var move in journal.DirectoryMoves?.Where(m => m.Completed).Reverse()
                         ?? Enumerable.Empty<OrganizationDirectoryMoveReceipt>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Directory.Exists(move.DestinationPath) || Directory.Exists(move.SourcePath) || File.Exists(move.SourcePath))
                    throw new IOException($"Directory recovery paths are not safe: {move.DestinationPath}");
                var actual = CaptureDirectoryManifest(move.DestinationPath);
                if (!actual.SequenceEqual(move.Files))
                    throw new IOException($"Directory recovery refused because the destination changed: {move.DestinationPath}");
                Directory.CreateDirectory(Path.GetDirectoryName(move.SourcePath)!);
                FileSystemVolume.RequireSameVolume(move.DestinationPath, move.SourcePath);
                Directory.Move(move.DestinationPath, move.SourcePath);
                restoredDirectories.Add(move);
            }

            foreach (var move in journal.Moves.Where(m => m.Completed).Reverse())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(move.DestinationPath))
                {
                    throw new IOException($"Recovery destination is missing: {move.DestinationPath}");
                }

                var actual = FileIdentity.Capture(move.DestinationPath);
                if (actual != move.Identity)
                {
                    throw new IOException(
                        $"Recovery refused because the destination changed: {move.DestinationPath}");
                }

                if (File.Exists(move.SourcePath))
                {
                    throw new IOException(
                        $"Recovery refused because the source path is occupied: {move.SourcePath}");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(move.SourcePath)!);
                FileSystemVolume.RequireSameVolume(move.DestinationPath, move.SourcePath);
                File.Move(move.DestinationPath, move.SourcePath);
                restored.Add(move);
            }

            DeleteJournalFiles();
            return new OrganizationTransactionResult(
                journal.OperationId,
                OrganizationTransactionStatus.Completed,
                restored,
                restoredDirectories);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private static List<OrganizationDirectoryMoveReceipt> PrepareDirectories(
        IReadOnlyList<OrganizationDirectoryMove> requestedMoves)
    {
        var result = new List<OrganizationDirectoryMoveReceipt>(requestedMoves.Count);
        var sources = new HashSet<string>(GetPathComparer());
        var destinations = new HashSet<string>(GetPathComparer());
        foreach (var move in requestedMoves)
        {
            string source = Normalize(move.SourcePath);
            string destination = Normalize(move.DestinationPath);
            if (!Directory.Exists(source) || File.Exists(source))
                throw new DirectoryNotFoundException(source);
            if (source == destination || !sources.Add(source) || !destinations.Add(destination))
                throw new InvalidDataException("Directory move sources and destinations must be unique.");
            if (File.Exists(destination) || Directory.Exists(destination))
                throw new IOException($"Destination already exists: {destination}");
            if (IsPathInside(destination, source))
                throw new InvalidDataException("A directory cannot be moved inside itself.");
            FileSystemVolume.RequireSameVolume(source, destination);

            result.Add(new OrganizationDirectoryMoveReceipt(
                source, destination, CaptureDirectoryManifest(source), false));
        }
        return result;
    }

    public static IReadOnlyList<DirectoryFileReceipt> CaptureDirectoryManifest(string root)
    {
        var rootInfo = new DirectoryInfo(root);
        if ((rootInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Reparse-point directory is not eligible: {root}");

        var files = new List<DirectoryFileReceipt>();
        foreach (string directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            if ((new DirectoryInfo(directory).Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Reparse-point directory member is not eligible: {directory}");
        }

        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(path);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Reparse-point directory member is not eligible: {path}");
            files.Add(new DirectoryFileReceipt(Path.GetRelativePath(root, path), FileIdentity.Capture(path)));
        }
        return files.OrderBy(item => item.RelativePath, StringComparer.Ordinal).ToArray();
    }

    private void MoveDirectoryOne(OrganizationDirectoryMoveReceipt move)
    {
        if (_directoryMoveGuard is not null && !_directoryMoveGuard(move))
            throw new IOException($"Fault injection refused directory move: {move.SourcePath}");
        if (Directory.Exists(move.DestinationPath) || File.Exists(move.DestinationPath))
            throw new IOException($"Destination appeared during directory move: {move.DestinationPath}");
        var current = CaptureDirectoryManifest(move.SourcePath);
        if (!current.SequenceEqual(move.Files))
            throw new IOException($"Directory changed before move: {move.SourcePath}");
        Directory.CreateDirectory(Path.GetDirectoryName(move.DestinationPath)!);
        FileSystemVolume.RequireSameVolume(move.SourcePath, move.DestinationPath);
        Directory.Move(move.SourcePath, move.DestinationPath);
    }

    private static bool IsPathInside(string candidate, string root)
    {
        string prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static List<OrganizationMoveReceipt> Prepare(IReadOnlyList<OrganizationMove> requestedMoves)
    {
        var result = new List<OrganizationMoveReceipt>(requestedMoves.Count);
        var sources = new HashSet<string>(GetPathComparer());
        var destinations = new HashSet<string>(GetPathComparer());
        foreach (var move in requestedMoves)
        {
            string source = Normalize(move.SourcePath);
            string destination = Normalize(move.DestinationPath);
            if (source == destination || !sources.Add(source) || !destinations.Add(destination))
            {
                throw new InvalidDataException("Move sources and destinations must be unique.");
            }

            if (!File.Exists(source) || Directory.Exists(source))
            {
                throw new FileNotFoundException("Only existing regular files can be moved.", source);
            }

            if (File.Exists(destination) || Directory.Exists(destination))
            {
                throw new IOException($"Destination already exists: {destination}");
            }

            if (Path.GetDirectoryName(destination) is null)
            {
                throw new IOException($"Destination has no parent directory: {destination}");
            }

            FileSystemVolume.RequireSameVolume(source, destination);
            var identity = FileIdentity.Capture(source);
            result.Add(new OrganizationMoveReceipt(source, destination, identity, false));
        }

        return result;
    }

    private void MoveOne(OrganizationMoveReceipt move)
    {
        if (_moveGuard is not null && !_moveGuard(move))
            throw new IOException($"Fault injection refused move: {move.SourcePath}");

        Directory.CreateDirectory(Path.GetDirectoryName(move.DestinationPath)!);
        if (File.Exists(move.DestinationPath) || Directory.Exists(move.DestinationPath))
            throw new IOException($"Destination appeared during transaction: {move.DestinationPath}");

        var current = FileIdentity.Capture(move.SourcePath);
        if (current != move.Identity)
            throw new IOException($"Source changed before move: {move.SourcePath}");

        FileSystemVolume.RequireSameVolume(move.SourcePath, move.DestinationPath);
        File.Move(move.SourcePath, move.DestinationPath);
    }

    private async Task SaveJournalAsync(
        OrganizationRecoveryJournal journal,
        CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(journal, JsonOptions);
        cancellationToken.ThrowIfCancellationRequested();
        // Do not abandon an in-flight durable write: that would release the gate while
        // the write still races recovery or the next transaction.
        await ResilientJsonStore.SaveAsync(_journalPath, json).ConfigureAwait(false);
    }

    private async Task<OrganizationRecoveryJournal?> LoadJournalAsync(
        CancellationToken cancellationToken)
    {
        if (File.Exists(RecoveryMarkerPath))
            throw new InvalidDataException("Organization recovery is blocked pending explicit manual reconciliation.");

        bool found = false;
        // Unlike ordinary settings, journal copies are evidence. Never quarantine them
        // into apparent absence and never create a default empty recovery journal.
        foreach (string path in new[] { _journalPath, ResilientJsonStore.GetBackupPath(_journalPath) })
        {
            if (!File.Exists(path)) continue;
            found = true;
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                string json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                var journal = JsonSerializer.Deserialize<OrganizationRecoveryJournal>(json, JsonOptions);
                ValidateJournal(journal);
                return journal;
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                // Try the older copy. Prepared receipts are separately reconciled before rollback.
            }
        }
        if (!found) return null;

        using (var marker = new FileStream(RecoveryMarkerPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            marker.Write("Recovery journal copies are invalid. Preserve them for manual reconciliation."u8);
            marker.Flush(flushToDisk: true);
        }
        throw new InvalidDataException("Recovery journal and backup are invalid; manual recovery is required.");
    }

    private static void ValidateJournal(OrganizationRecoveryJournal? journal)
    {
        if (journal is null || journal.OperationId == Guid.Empty || journal.Moves is null ||
            journal.Status is not ("Prepared" or "Moving" or "RecoveryRequired") ||
            journal.Moves.Count + (journal.DirectoryMoves?.Count ?? 0) == 0)
            throw new InvalidDataException("Malformed recovery journal.");
        foreach (var move in journal.Moves)
        {
            if (move is null || string.IsNullOrWhiteSpace(move.SourcePath) ||
                string.IsNullOrWhiteSpace(move.DestinationPath) || !Path.IsPathFullyQualified(move.SourcePath) ||
                !Path.IsPathFullyQualified(move.DestinationPath) || move.Identity is null ||
                move.Identity.Length < 0 || string.IsNullOrWhiteSpace(move.Identity.Sha256))
                throw new InvalidDataException("Malformed recovery receipt.");
        }
        foreach (var move in journal.DirectoryMoves ?? [])
        {
            if (move is null || string.IsNullOrWhiteSpace(move.SourcePath) ||
                string.IsNullOrWhiteSpace(move.DestinationPath) || !Path.IsPathFullyQualified(move.SourcePath) ||
                !Path.IsPathFullyQualified(move.DestinationPath) || move.Files is null ||
                move.Files.Any(file => file is null || file.Identity is null || string.IsNullOrWhiteSpace(file.RelativePath)))
                throw new InvalidDataException("Malformed directory recovery receipt.");
        }
    }

    private static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("Move paths must be absolute.");
        }

        return Path.GetFullPath(path);
    }

    private static StringComparer GetPathComparer() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private FileStream AcquireProcessLock()
    {
        string lockPath = $"{_journalPath}.lock";
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private void DeleteJournalFiles()
    {
        // Delete the older copy first so a crash cannot resurrect a stale backup alone.
        File.Delete(ResilientJsonStore.GetBackupPath(_journalPath));
        File.Delete(_journalPath);
    }
}
