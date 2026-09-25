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

public sealed record OrganizationRecoveryJournal(
    Guid OperationId,
    string Status,
    IReadOnlyList<OrganizationMoveReceipt> Moves,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public enum OrganizationTransactionStatus
{
    Completed,
    RecoveryRequired
}

public sealed record OrganizationTransactionResult(
    Guid OperationId,
    OrganizationTransactionStatus Status,
    IReadOnlyList<OrganizationMoveReceipt> Receipts);

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

    public DesktopOrganizationTransaction(string journalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journalPath);
        if (!Path.IsPathFullyQualified(journalPath))
        {
            throw new ArgumentException("The journal path must be absolute.", nameof(journalPath));
        }

        _journalPath = journalPath;
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

    public async Task<OrganizationTransactionResult> RecoverAsync(
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var journal = await LoadJournalAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("No recovery journal exists.", _journalPath);

            var restored = new List<OrganizationMoveReceipt>();
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
                restored);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private static List<OrganizationMoveReceipt> Prepare(IReadOnlyList<OrganizationMove> requestedMoves)
    {
        var result = new List<OrganizationMoveReceipt>(requestedMoves.Count);
        var destinations = new HashSet<string>(GetPathComparer());
        foreach (var move in requestedMoves)
        {
            string source = Normalize(move.SourcePath);
            string destination = Normalize(move.DestinationPath);
            if (source == destination || !destinations.Add(destination))
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

            string? parent = Path.GetDirectoryName(destination);
            if (parent is null)
            {
                throw new IOException($"Destination has no parent directory: {destination}");
            }

            var identity = FileIdentity.Capture(source);
            result.Add(new OrganizationMoveReceipt(source, destination, identity, false));
        }

        return result;
    }

    private static void MoveOne(OrganizationMoveReceipt move)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(move.DestinationPath)!);
        if (File.Exists(move.DestinationPath) || Directory.Exists(move.DestinationPath))
        {
            throw new IOException($"Destination appeared during transaction: {move.DestinationPath}");
        }

        var current = FileIdentity.Capture(move.SourcePath);
        if (current != move.Identity)
        {
            throw new IOException($"Source changed before move: {move.SourcePath}");
        }

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

        return await ResilientJsonStore.LoadAsync(
            _journalPath,
            json => JsonSerializer.Deserialize<OrganizationRecoveryJournal>(json, JsonOptions)
                ?? throw new InvalidDataException("Recovery journal is empty."),
            () => throw new InvalidDataException("Recovery journal is missing."),
            "organization-recovery").WaitAsync(cancellationToken).ConfigureAwait(false);
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

    private void DeleteJournalFiles()
    {
        File.Delete(_journalPath);
        string backupPath = ResilientJsonStore.GetBackupPath(_journalPath);
        if (File.Exists(backupPath))
            File.Delete(backupPath);
    }
}
