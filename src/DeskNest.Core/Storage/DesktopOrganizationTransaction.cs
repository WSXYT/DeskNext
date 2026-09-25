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

public sealed record OrganizationTransactionResult(
    Guid OperationId,
    OrganizationTransactionStatus Status,
    IReadOnlyList<OrganizationMoveReceipt> Receipts,
    IReadOnlyList<OrganizationDirectoryMoveReceipt>? DirectoryReceipts = null);

/// <summary>
/// Fail-closed, journaled moves for the P3 manual-organization boundary.
/// This first implementation intentionally accepts regular files only; it never
/// follows reparse points, overwrites destinations, or treats a changed file as
/// an authorized undo target.
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

    public DesktopOrganizationTransaction(
        string journalPath,
        Func<OrganizationMoveReceipt, bool>? moveGuard = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journalPath);
        if (!Path.IsPathFullyQualified(journalPath))
            throw new ArgumentException("The journal path must be absolute.", nameof(journalPath));

        _journalPath = journalPath;
        _moveGuard = moveGuard;
    }

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
            if (File.Exists(_journalPath))
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
            if (File.Exists(_journalPath))
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
            var sourceRoot = Path.GetPathRoot(source);
            var destinationRoot = Path.GetPathRoot(destination);
            if (!string.Equals(sourceRoot, destinationRoot,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new IOException("Cross-volume directory moves require a copy-and-verify adapter.");

            result.Add(new OrganizationDirectoryMoveReceipt(
                source, destination, CaptureDirectoryManifest(source), false));
        }
        return result;
    }

    private static IReadOnlyList<DirectoryFileReceipt> CaptureDirectoryManifest(string root)
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
        return files;
    }

    private static void MoveDirectoryOne(OrganizationDirectoryMoveReceipt move)
    {
        if (Directory.Exists(move.DestinationPath) || File.Exists(move.DestinationPath))
            throw new IOException($"Destination appeared during directory move: {move.DestinationPath}");
        var current = CaptureDirectoryManifest(move.SourcePath);
        if (!current.SequenceEqual(move.Files))
            throw new IOException($"Directory changed before move: {move.SourcePath}");
        Directory.CreateDirectory(Path.GetDirectoryName(move.DestinationPath)!);
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

        File.Move(move.SourcePath, move.DestinationPath);
    }

    private async Task SaveJournalAsync(
        OrganizationRecoveryJournal journal,
        CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(journal, JsonOptions);
        await ResilientJsonStore.SaveAsync(_journalPath, json).WaitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<OrganizationRecoveryJournal?> LoadJournalAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_journalPath))
        {
            return null;
        }

        var loaded = await ResilientJsonStore.LoadWithResultAsync(
            _journalPath,
            json => JsonSerializer.Deserialize<OrganizationRecoveryJournal>(json, JsonOptions)
                ?? throw new InvalidDataException("Recovery journal is empty."),
            () => throw new InvalidDataException("Recovery journal is missing."),
            "organization-recovery").WaitAsync(cancellationToken).ConfigureAwait(false);

        if (loaded.Source == ResilientJsonLoadSource.DefaultAfterFailure)
        {
            throw new InvalidDataException(
                "Recovery journal and backup are both corrupt; manual recovery is required.");
        }

        return loaded.Value;
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
        File.Delete(_journalPath);
        string backupPath = ResilientJsonStore.GetBackupPath(_journalPath);
        if (File.Exists(backupPath))
            File.Delete(backupPath);
    }
}
