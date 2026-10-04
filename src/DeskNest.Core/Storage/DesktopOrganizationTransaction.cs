using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace DeskNest.Core.Storage;

public sealed record OrganizationMove(string SourcePath, string DestinationPath)
{
    // Undo supplies its recorded identity; preparation may not adopt a replacement.
    public FileIdentity? ExpectedIdentity { get; init; }
}

public sealed record FileIdentity(
    long Length,
    long LastWriteTimeUtcTicks,
    string Sha256)
{
    // Null is legacy evidence; it cannot authorize recovery or undo on supported platforms.
    public string? NativeId { get; init; }

    public static FileIdentity Capture(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
        var unix = OperatingSystem.IsWindows() ? null : UnixFileIdentity.Capture(stream.SafeFileHandle);
        using var hashAlgorithm = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        int bytesRead;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bytesRead = stream.Read(buffer);
            if (bytesRead == 0)
                break;
            hashAlgorithm.AppendData(buffer, 0, bytesRead);
        }
        string hash = Convert.ToHexString(hashAlgorithm.GetHashAndReset());
        if (OperatingSystem.IsWindows())
        {
            var (nativeId, lastWriteTicks) = WindowsFileIdentity.Capture(stream.SafeFileHandle);
            return new FileIdentity(stream.Length, lastWriteTicks, hash) { NativeId = nativeId };
        }
        var after = UnixFileIdentity.Capture(stream.SafeFileHandle);
        if (unix != after || stream.Length != after.Length)
            throw new IOException("The file changed while native identity and content were being captured.");
        return new FileIdentity(after.Length, after.LastWriteTicks, hash) { NativeId = after.NativeId };
    }
}

internal static class WindowsFileIdentity
{
    private enum FileInfoClass { FileBasicInfo = 0, FileIdInfo = 18 }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInfo
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo
    {
        public ulong VolumeSerialNumber;
        public ulong Low;
        public ulong High;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, FileInfoClass infoClass,
        out FileIdInfo info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, FileInfoClass infoClass,
        out FileBasicInfo info, uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationByHandleW(SafeFileHandle handle, StringBuilder? volumeName,
        uint volumeNameSize, out uint serial, out uint maxComponentLength, out uint flags,
        StringBuilder fileSystemName, uint fileSystemNameSize);

    internal static FileIdentity CaptureContent(SafeFileHandle handle, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        WindowsFileHandles.RequireOrdinaryObject(handle, directory: false);
        var native = Capture(handle);
        long length = RandomAccess.GetLength(handle), offset = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        while (true)
        {
            token.ThrowIfCancellationRequested();
            int read = RandomAccess.Read(handle, buffer, offset);
            if (read == 0) break;
            hash.AppendData(buffer, 0, read);
            offset += read;
        }
        if (offset != length || RandomAccess.GetLength(handle) != length || native != Capture(handle))
            throw new IOException("Object changed while capturing file evidence.");
        WindowsFileHandles.RequireOrdinaryObject(handle, directory: false);
        return new FileIdentity(length, native.LastWriteTicks, Convert.ToHexString(hash.GetHashAndReset())) { NativeId = native.NativeId };
    }

    internal static (string NativeId, long LastWriteTicks) Capture(SafeFileHandle handle)
    {
        var filesystem = new StringBuilder(32);
        if (!GetVolumeInformationByHandleW(handle, null, 0, out _, out _, out _, filesystem,
                (uint)filesystem.Capacity))
            throw new IOException("Cannot verify the source filesystem.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        if (!string.Equals(filesystem.ToString(), "NTFS", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Native file identity requires a local NTFS volume.");
        if (!GetFileInformationByHandleEx(handle, FileInfoClass.FileIdInfo, out FileIdInfo info,
                (uint)Marshal.SizeOf<FileIdInfo>()))
            throw new IOException("Cannot capture a native file ID.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        if (!GetFileInformationByHandleEx(handle, FileInfoClass.FileBasicInfo, out FileBasicInfo basic,
                (uint)Marshal.SizeOf<FileBasicInfo>()))
            throw new IOException("Cannot capture file timestamps from the open handle.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        return ($"{info.VolumeSerialNumber:X16}:{info.High:X16}{info.Low:X16}",
            DateTime.FromFileTimeUtc(basic.LastWriteTime).Ticks);
    }
}

public sealed record OrganizationMoveReceipt(
    string SourcePath,
    string DestinationPath,
    FileIdentity Identity,
    bool Completed)
{
    public bool Restored { get; init; }
}

public sealed record OrganizationDirectoryMove(string SourcePath, string DestinationPath)
{
    public OrganizationDirectoryMoveReceipt? ExpectedReceipt { get; init; }
}

public sealed record DirectoryFileReceipt(string RelativePath, FileIdentity Identity);

public sealed record OrganizationDirectoryMoveReceipt(
    string SourcePath,
    string DestinationPath,
    IReadOnlyList<DirectoryFileReceipt> Files,
    bool Completed)
{
    // Null means legacy evidence, not an empty tree. Never infer missing topology.
    public IReadOnlyList<string>? Directories { get; init; }
    // Includes the root under ""; null is legacy evidence, not native authority on any platform.
    public IReadOnlyDictionary<string, string>? DirectoryNativeIds { get; init; }
    public bool Restored { get; init; }
}

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
    private readonly Func<string, bool>? _restoreGuard;

    public DesktopOrganizationTransaction(
        string journalPath,
        Func<OrganizationMoveReceipt, bool>? moveGuard = null,
        Func<OrganizationDirectoryMoveReceipt, bool>? directoryMoveGuard = null,
        Func<string, bool>? restoreGuard = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journalPath);
        if (!Path.IsPathFullyQualified(journalPath))
            throw new ArgumentException("The journal path must be absolute.", nameof(journalPath));

        _journalPath = journalPath;
        _moveGuard = moveGuard;
        _directoryMoveGuard = directoryMoveGuard;
        _restoreGuard = restoreGuard;
    }

    private string RecoveryMarkerPath => _journalPath + ".recovery-required";
    private string RollbackFencePath => _journalPath + ".rollback-started";

    public bool HasRecoveryJournal => File.Exists(_journalPath) ||
        File.Exists(ResilientJsonStore.GetBackupPath(_journalPath)) || File.Exists(RecoveryMarkerPath) ||
        File.Exists(RollbackFencePath) || Directory.Exists(RollbackFencePath);

    public OrganizationDirectoryMoveSupport DirectoryMoveSupport =>
        OrganizationDirectoryMoveSupport.SameVolumeAtomicWithManifest;

    public bool SupportsCrossVolumeDirectoryMoves => false;

    public async Task<OrganizationTransactionResult> ExecuteAsync(
        IReadOnlyList<OrganizationMove> requestedMoves,
        CancellationToken cancellationToken = default,
        bool retainJournalUntilCommit = false)
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

            var prepared = Prepare(requestedMoves, cancellationToken);
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
                    MoveOne(move, cancellationToken);
                    completed.Add(move with { Completed = true });
                    await SaveJournalAsync(journal with
                    {
                        Status = "Moving",
                        Moves = completed.Concat(prepared.Skip(completed.Count)).ToArray(),
                        UpdatedAt = DateTimeOffset.UtcNow
                    }, cancellationToken).ConfigureAwait(false);
                }

                if (!retainJournalUntilCommit) DeleteJournalFiles();
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
        CancellationToken cancellationToken = default,
        bool retainJournalUntilCommit = false)
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

            var prepared = PrepareDirectories(requestedMoves, cancellationToken);
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
                    MoveDirectoryOne(move, cancellationToken);
                    completed.Add(move with { Completed = true });
                    await SaveJournalAsync(journal with
                    {
                        Status = "Moving",
                        DirectoryMoves = completed.Concat(prepared.Skip(completed.Count)).ToArray(),
                        UpdatedAt = DateTimeOffset.UtcNow
                    }, cancellationToken).ConfigureAwait(false);
                }

                if (!retainJournalUntilCommit) DeleteJournalFiles();
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

    /// <summary>Called only after workspace metadata durably records this transaction ID.</summary>
    public async Task CommitAsync(Guid transactionId, CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var processLock = AcquireProcessLock();
            var journal = await LoadJournalAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("No journal to commit.", _journalPath);
            if (journal.OperationId != transactionId)
                throw new InvalidDataException("Commit ID does not match the retained journal.");
            RequireCompleteReceipts(journal);
            DeleteJournalFiles();
        }
        finally { _operationGate.Release(); }
    }

    private void RequireCompleteReceipts(OrganizationRecoveryJournal journal)
    {
        if (File.Exists(RollbackFencePath) || Directory.Exists(RollbackFencePath) ||
            journal.Status == "Recovering" || journal.Moves.Any(m => !m.Completed || m.Restored) ||
            journal.DirectoryMoves?.Any(m => !m.Completed || m.Restored) == true)
            throw new InvalidDataException("Incomplete or rolled-back receipts cannot be acknowledged as committed.");
    }

    public async Task<OrganizationTransactionResult> RecoverAsync(
        CancellationToken cancellationToken = default,
        IReadOnlySet<Guid>? committedTransactionIds = null)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var processLock = AcquireProcessLock();
            var journal = await LoadJournalAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("No recovery journal exists.", _journalPath);

            if (committedTransactionIds?.Contains(journal.OperationId) == true)
            {
                // Workspace commit succeeded before cleanup was interrupted. Never roll
                // back files behind an already committed workspace snapshot.
                RequireCompleteReceipts(journal);
                DeleteJournalFiles();
                return new OrganizationTransactionResult(journal.OperationId,
                    OrganizationTransactionStatus.Completed, journal.Moves, journal.DirectoryMoves);
            }

            // A Prepared receipt may cover a crash after rename but before acknowledgement.
            // Never infer success from content alone or silently forget such an item.
            // Previously checkpointed restores must still match before any new move.
            foreach (var pending in journal.Moves.Where(m => !m.Completed || m.Restored))
            {
                if (!File.Exists(pending.SourcePath) || File.Exists(pending.DestinationPath) ||
                    Directory.Exists(pending.DestinationPath) || !IdentityMatches(pending.SourcePath, pending.Identity))
                    throw new IOException("Unacknowledged move requires manual reconciliation; journal retained.");
            }
            foreach (var pending in journal.DirectoryMoves?.Where(m => !m.Completed || m.Restored) ?? [])
            {
                if (!Directory.Exists(pending.SourcePath) || File.Exists(pending.DestinationPath) ||
                    Directory.Exists(pending.DestinationPath) ||
                    !DirectoryManifestMatches(pending.SourcePath, pending.Files, pending.Directories, pending.DirectoryNativeIds))
                    throw new IOException("Unacknowledged directory move requires manual reconciliation; journal retained.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            EnsureRollbackFence(journal.OperationId);
            journal = journal with { Status = "Recovering", UpdatedAt = DateTimeOffset.UtcNow };
            await SaveJournalAsync(journal, cancellationToken).ConfigureAwait(false);
            var files = journal.Moves.ToArray();
            var directories = journal.DirectoryMoves?.ToArray() ?? [];
            var restored = new List<OrganizationMoveReceipt>();
            var restoredDirectories = new List<OrganizationDirectoryMoveReceipt>();
            for (int index = directories.Length - 1; index >= 0; index--)
            {
                var move = directories[index];
                if (!move.Completed || move.Restored) continue;
                cancellationToken.ThrowIfCancellationRequested();
                if (_restoreGuard is not null && !_restoreGuard(move.SourcePath))
                    throw new IOException("Fault injection refused directory restore.");
                if (!Directory.Exists(move.DestinationPath) || Directory.Exists(move.SourcePath) || File.Exists(move.SourcePath))
                    throw new IOException($"Directory recovery paths are not safe: {move.DestinationPath}");
                if (!DirectoryManifestMatches(move.DestinationPath, move.Files, move.Directories, move.DirectoryNativeIds))
                    throw new IOException($"Directory recovery refused because the destination changed: {move.DestinationPath}");
                FileSystemVolume.RequireNoReparsePoints(move.DestinationPath);
                FileSystemVolume.RequireNoReparsePoints(move.SourcePath);
                FileSystemVolume.RequireSameVolume(move.DestinationPath, move.SourcePath);
                if (OperatingSystem.IsWindows())
                    WindowsFileHandles.MoveDirectory(move.DestinationPath, move.SourcePath, move);
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(move.SourcePath)!);
                    Directory.Move(move.DestinationPath, move.SourcePath);
                }
                directories[index] = move with { Restored = true };
                journal = journal with { DirectoryMoves = directories, UpdatedAt = DateTimeOffset.UtcNow };
                // Once the rename happened, finish recording it even if cancellation
                // was requested. A crash before this save still requires manual review.
                await SaveJournalAsync(journal, CancellationToken.None).ConfigureAwait(false);
                restoredDirectories.Add(directories[index]);
            }

            for (int index = files.Length - 1; index >= 0; index--)
            {
                var move = files[index];
                if (!move.Completed || move.Restored) continue;
                cancellationToken.ThrowIfCancellationRequested();
                if (_restoreGuard is not null && !_restoreGuard(move.SourcePath))
                    throw new IOException("Fault injection refused file restore.");
                if (!File.Exists(move.DestinationPath))
                {
                    throw new IOException($"Recovery destination is missing: {move.DestinationPath}");
                }

                if (!IdentityMatches(move.DestinationPath, move.Identity))
                {
                    throw new IOException(
                        $"Recovery refused because the destination changed: {move.DestinationPath}");
                }

                if (File.Exists(move.SourcePath) || Directory.Exists(move.SourcePath))
                    throw new IOException($"Recovery refused because the source path is occupied: {move.SourcePath}");

                FileSystemVolume.RequireNoReparsePoints(move.DestinationPath);
                FileSystemVolume.RequireNoReparsePoints(move.SourcePath);
                FileSystemVolume.RequireSameVolume(move.DestinationPath, move.SourcePath);
                if (OperatingSystem.IsWindows())
                    WindowsFileHandles.MoveFile(move.DestinationPath, move.SourcePath, move.Identity);
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(move.SourcePath)!);
                    File.Move(move.DestinationPath, move.SourcePath);
                }
                files[index] = move with { Restored = true };
                journal = journal with { Moves = files, UpdatedAt = DateTimeOffset.UtcNow };
                await SaveJournalAsync(journal, CancellationToken.None).ConfigureAwait(false);
                restored.Add(files[index]);
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
        IReadOnlyList<OrganizationDirectoryMove> requestedMoves, CancellationToken cancellationToken)
    {
        var result = new List<OrganizationDirectoryMoveReceipt>(requestedMoves.Count);
        var sources = new HashSet<string>(GetPathComparer());
        var destinations = new HashSet<string>(GetPathComparer());
        foreach (var move in requestedMoves)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
            FileSystemVolume.RequireNoReparsePoints(source);
            FileSystemVolume.RequireNoReparsePoints(destination);
            FileSystemVolume.RequireSameVolume(source, destination);

            result.Add(CaptureDirectoryReceipt(source, destination, cancellationToken));
            if (move.ExpectedReceipt is { } expected && !DirectoryReceiptsMatch(result[^1], expected))
                throw new IOException("Directory changed after selecting the recorded operation; refusing to prepare it.");
        }
        return result;
    }

    internal static OrganizationDirectoryMoveReceipt CaptureDirectoryReceipt(string source, string destination,
        CancellationToken token = default)
    {
        if (OperatingSystem.IsWindows())
        {
            using var root = WindowsDirectoryLease.Open(source);
            using var tree = WindowsTreeLease.Capture(root.Handle, token, forCopy: false);
            var receipt = tree.ToMoveReceipt(source, destination);
            ValidateDirectoryManifest(receipt.Files, receipt.Directories, receipt.DirectoryNativeIds);
            return receipt;
        }
        var ids = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var snapshot = CaptureDirectorySnapshot(source, token, ids);
        ValidateDirectoryManifest(snapshot.Files, snapshot.Directories, ids);
        return new(source, destination, snapshot.Files, false)
        { Directories = snapshot.Directories, DirectoryNativeIds = ids };
    }

    // File-only compatibility helper; physical operations require the full snapshot.
    public static IReadOnlyList<DirectoryFileReceipt> CaptureDirectoryManifest(string root) =>
        CaptureDirectorySnapshot(root).Files;

    internal const int MaximumDirectoryEntries = 100_000;
    internal const int MaximumDirectoryDepth = 128;

    public static (IReadOnlyList<DirectoryFileReceipt> Files, IReadOnlyList<string> Directories)
        CaptureDirectorySnapshot(string root, CancellationToken cancellationToken = default) =>
        CaptureDirectorySnapshot(root, cancellationToken, null);

    private static (IReadOnlyList<DirectoryFileReceipt> Files, IReadOnlyList<string> Directories)
        CaptureDirectorySnapshot(string root, CancellationToken cancellationToken, IDictionary<string, string>? nativeIds)
    {
        cancellationToken.ThrowIfCancellationRequested();
        root = Normalize(root);
        var files = new List<DirectoryFileReceipt>();
        var directories = new List<string>();
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));
        while (pending.TryPop(out var current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(current.Path);
            if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                (attributes & FileAttributes.Directory) == 0)
                throw new IOException($"Directory is not eligible for a safe snapshot: {current.Path}");
            var native = nativeIds is null ? null : UnixFileIdentity.CaptureDirectory(current.Path);
            if (native is not null)
                nativeIds!.Add(current.Depth == 0 ? "" : Path.GetRelativePath(root, current.Path), native.NativeId);
            // Enumerate one level only. Inspect links before traversing, including empty directories.
            foreach (string path in Directory.EnumerateFileSystemEntries(current.Path))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (files.Count + directories.Count >= MaximumDirectoryEntries ||
                    current.Depth >= MaximumDirectoryDepth)
                    throw new IOException("Directory snapshot exceeds its entry or depth budget.");
                attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"Reparse-point directory member is not eligible: {path}");
                FileSystemVolume.RequireSameVolume(root, path);
                string relative = Path.GetRelativePath(root, path);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    directories.Add(relative);
                    pending.Push((path, current.Depth + 1));
                }
                else
                {
                    files.Add(new DirectoryFileReceipt(relative, FileIdentity.Capture(path, cancellationToken)));
                }
            }
        }
        // Native node receipts include the root and empty directories, not just file-bearing paths.
        // Re-open after the walk to detect replacements during traversal; this is not a snapshot lock.
        if (nativeIds is not null)
            foreach (var pair in nativeIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (UnixFileIdentity.CaptureDirectory(Path.Combine(root, pair.Key)).NativeId != pair.Value)
                    throw new IOException("Directory identity changed during capture.");
            }
        var sortedFiles = files.OrderBy(item => item.RelativePath, StringComparer.Ordinal).ToArray();
        var sortedDirectories = directories.Order(StringComparer.Ordinal).ToArray();
        ValidateDirectoryManifest(sortedFiles, sortedDirectories);
        return (sortedFiles, sortedDirectories);
    }

    internal static bool DirectoryManifestMatches(string root, IReadOnlyList<DirectoryFileReceipt> files,
        IReadOnlyList<string>? directories, IReadOnlyDictionary<string, string>? directoryNativeIds = null)
    {
        ValidateDirectoryManifest(files, directories, directoryNativeIds);
        if (OperatingSystem.IsWindows())
        {
            if (directoryNativeIds is null) return false;
            using var lease = WindowsDirectoryLease.Open(root);
            using var tree = WindowsTreeLease.Capture(lease.Handle, forCopy: false);
            return tree.MatchesMoveReceipt(new(root, root, files, false)
            { Directories = directories, DirectoryNativeIds = directoryNativeIds });
        }
        if (directoryNativeIds is null || directoryNativeIds.Values.Any(id => !UnixFileIdentity.IsNativeId(id)))
            return false;
        var actual = CaptureDirectoryReceipt(root, root);
        return actual.Files.All(file => HasNativeEvidence(file.Identity)) &&
            DirectoryReceiptsMatch(actual, new(root, root, files, false)
            { Directories = directories, DirectoryNativeIds = directoryNativeIds });
    }

    internal static bool IdentityMatches(string path, FileIdentity expected) =>
        HasNativeEvidence(expected) && FileIdentity.Capture(path) == expected;

    private static bool HasNativeEvidence(FileIdentity expected) =>
        OperatingSystem.IsWindows() ? IsNativeId(expected.NativeId) :
        (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) && UnixFileIdentity.IsNativeId(expected.NativeId);

    private static bool IsNativeId(string? nativeId) => nativeId is { Length: 49 } id &&
        id[16] == ':' && id.Where((_, index) => index != 16).All(Uri.IsHexDigit);

    internal static bool DirectoryReceiptsMatch(OrganizationDirectoryMoveReceipt actual, OrganizationDirectoryMoveReceipt expected)
    {
        if (actual.Directories is null || expected.Directories is null ||
            !actual.Files.SequenceEqual(expected.Files) || !actual.Directories.SequenceEqual(expected.Directories)) return false;
        if (expected.DirectoryNativeIds is null) return false;
        return actual.DirectoryNativeIds is not null && actual.DirectoryNativeIds.Count == expected.DirectoryNativeIds.Count &&
            actual.DirectoryNativeIds.All(pair => expected.DirectoryNativeIds.TryGetValue(pair.Key, out string? id) && id == pair.Value);
    }

    internal static bool IsValidManifestPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path.Length <= 4_096 &&
        !Path.IsPathRooted(path) && !path.Contains(':') && !path.Any(char.IsControl) &&
        path.Split(['/', '\\']).All(part => part.Length > 0 && part is not ("." or "..")) &&
        path.Split(['/', '\\']).Length <= MaximumDirectoryDepth;

    internal static void ValidateDirectoryManifest(IReadOnlyList<DirectoryFileReceipt> files,
        IReadOnlyList<string>? directories, IReadOnlyDictionary<string, string>? directoryNativeIds = null)
    {
        if (directories is null || (long)files.Count + directories.Count > MaximumDirectoryEntries)
            throw new InvalidDataException("Directory topology evidence is missing or exceeds its budget.");
        var comparer = GetPathComparer();
        var entries = new HashSet<string>(comparer);
        var folders = new HashSet<string>(comparer);
        foreach (string directory in directories)
        {
            if (!IsValidManifestPath(directory) || !entries.Add(directory.Replace('\\', '/')))
                throw new InvalidDataException("Invalid or duplicate directory topology entry.");
            folders.Add(directory.Replace('\\', '/'));
        }
        foreach (var file in files)
        {
            if (file is null || !IsValidManifestPath(file.RelativePath) || file.Identity is null ||
                file.Identity.Length < 0 || file.Identity.LastWriteTimeUtcTicks < 0 ||
                file.Identity.LastWriteTimeUtcTicks > DateTime.MaxValue.Ticks ||
                file.Identity.Sha256 is not { Length: 64 } || !file.Identity.Sha256.All(Uri.IsHexDigit) ||
                !entries.Add(file.RelativePath.Replace('\\', '/')))
                throw new InvalidDataException("Invalid or duplicate directory file receipt.");
        }
        foreach (string entry in entries)
        {
            int separator = entry.LastIndexOf('/');
            if (separator >= 0 && !folders.Contains(entry[..separator]))
                throw new InvalidDataException("Directory topology is missing an ancestor.");
        }
        if (directoryNativeIds is not null &&
            (!directoryNativeIds.Keys.SequenceEqual(folders.Append("").Order(StringComparer.Ordinal)) ||
             directoryNativeIds.Values.Any(id => !IsNativeId(id) && !UnixFileIdentity.IsNativeId(id, requireCurrentPlatform: false)) ||
             directoryNativeIds.Values.Any(id => !id.StartsWith(
                 directoryNativeIds[""][..(directoryNativeIds[""].Length == 49 ? 16 : 22)], StringComparison.Ordinal))))
            throw new InvalidDataException("Native directory identities must cover the exact sorted topology on one volume.");
    }

    private void MoveDirectoryOne(OrganizationDirectoryMoveReceipt move, CancellationToken token)
    {
        if (_directoryMoveGuard is not null && !_directoryMoveGuard(move))
            throw new IOException($"Fault injection refused directory move: {move.SourcePath}");
        if (Directory.Exists(move.DestinationPath) || File.Exists(move.DestinationPath))
            throw new IOException($"Destination appeared during directory move: {move.DestinationPath}");
        FileSystemVolume.RequireNoReparsePoints(move.SourcePath);
        FileSystemVolume.RequireNoReparsePoints(move.DestinationPath);
        FileSystemVolume.RequireSameVolume(move.SourcePath, move.DestinationPath);
        if (OperatingSystem.IsWindows())
        {
            WindowsFileHandles.MoveDirectory(move.SourcePath, move.DestinationPath, move, token);
            return;
        }
        if (!DirectoryManifestMatches(move.SourcePath, move.Files, move.Directories, move.DirectoryNativeIds))
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

    private static List<OrganizationMoveReceipt> Prepare(
        IReadOnlyList<OrganizationMove> requestedMoves, CancellationToken cancellationToken)
    {
        var result = new List<OrganizationMoveReceipt>(requestedMoves.Count);
        var sources = new HashSet<string>(GetPathComparer());
        var destinations = new HashSet<string>(GetPathComparer());
        foreach (var move in requestedMoves)
        {
            cancellationToken.ThrowIfCancellationRequested();
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

            FileSystemVolume.RequireNoReparsePoints(source);
            FileSystemVolume.RequireNoReparsePoints(destination);
            FileSystemVolume.RequireSameVolume(source, destination);
            var identity = FileIdentity.Capture(source, cancellationToken);
            if (move.ExpectedIdentity is { } expected && identity != expected)
                throw new IOException("File changed after selecting the recorded operation; refusing to prepare it.");
            result.Add(new OrganizationMoveReceipt(source, destination, identity, false));
        }

        return result;
    }

    private void MoveOne(OrganizationMoveReceipt move, CancellationToken token)
    {
        if (_moveGuard is not null && !_moveGuard(move))
            throw new IOException($"Fault injection refused move: {move.SourcePath}");

        FileSystemVolume.RequireNoReparsePoints(move.SourcePath);
        FileSystemVolume.RequireNoReparsePoints(move.DestinationPath);
        if (File.Exists(move.DestinationPath) || Directory.Exists(move.DestinationPath))
            throw new IOException($"Destination appeared during transaction: {move.DestinationPath}");
        FileSystemVolume.RequireSameVolume(move.SourcePath, move.DestinationPath);
        if (OperatingSystem.IsWindows())
        {
            WindowsFileHandles.MoveFile(move.SourcePath, move.DestinationPath, move.Identity, token);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(move.DestinationPath)!);
        if (!IdentityMatches(move.SourcePath, move.Identity))
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

        Guid? rollbackId = ReadRollbackFence();
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
                if (rollbackId.HasValue && rollbackId.Value != journal!.OperationId)
                    throw new InvalidDataException("Rollback fence belongs to a different transaction.");
                return journal;
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                // Try the older copy. Prepared receipts are separately reconciled before rollback.
            }
        }
        if (!found)
        {
            if (rollbackId.HasValue)
                throw new InvalidDataException("Rollback evidence exists without its journal; manual reconciliation required.");
            return null;
        }

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
            journal.Status is not ("Prepared" or "Moving" or "RecoveryRequired" or "Recovering") ||
            journal.Moves.Count + (journal.DirectoryMoves?.Count ?? 0) == 0)
            throw new InvalidDataException("Malformed recovery journal.");
        foreach (var move in journal.Moves)
        {
            if (move is null || string.IsNullOrWhiteSpace(move.SourcePath) ||
                string.IsNullOrWhiteSpace(move.DestinationPath) || !Path.IsPathFullyQualified(move.SourcePath) ||
                !Path.IsPathFullyQualified(move.DestinationPath) || move.Identity is null ||
                move.Identity.Length < 0 || string.IsNullOrWhiteSpace(move.Identity.Sha256) ||
                move.Restored && (!move.Completed || journal.Status != "Recovering"))
                throw new InvalidDataException("Malformed recovery receipt.");
        }
        foreach (var move in journal.DirectoryMoves ?? [])
        {
            if (move is null || string.IsNullOrWhiteSpace(move.SourcePath) ||
                string.IsNullOrWhiteSpace(move.DestinationPath) || !Path.IsPathFullyQualified(move.SourcePath) ||
                !Path.IsPathFullyQualified(move.DestinationPath) || move.Files is null ||
                move.Files.Any(file => file is null || file.Identity is null || string.IsNullOrWhiteSpace(file.RelativePath)) ||
                move.Restored && (!move.Completed || journal.Status != "Recovering"))
                throw new InvalidDataException("Malformed directory recovery receipt.");
            ValidateDirectoryManifest(move.Files, move.Directories, move.DirectoryNativeIds);
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

    private Guid? ReadRollbackFence()
    {
        if (Directory.Exists(RollbackFencePath))
            throw new InvalidDataException("Rollback fence is not a regular file.");
        if (!File.Exists(RollbackFencePath)) return null;
        using var stream = new FileStream(RollbackFencePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> bytes = stackalloc byte[32];
        if (stream.Length != bytes.Length)
            throw new InvalidDataException("Incomplete rollback fence; manual reconciliation required.");
        stream.ReadExactly(bytes);
        if (!Guid.TryParseExact(System.Text.Encoding.ASCII.GetString(bytes), "N", out Guid id) || id == Guid.Empty)
            throw new InvalidDataException("Invalid rollback fence; manual reconciliation required.");
        return id;
    }

    private void EnsureRollbackFence(Guid operationId)
    {
        var existing = ReadRollbackFence();
        if (existing.HasValue)
        {
            if (existing.Value != operationId)
                throw new InvalidDataException("Rollback fence belongs to a different transaction.");
            return;
        }
        // Independent of rotating journal copies: a stale Moving backup must never
        // authorize acknowledgement after any reverse move has started.
        using var stream = new FileStream(RollbackFencePath, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 4096, FileOptions.WriteThrough);
        stream.Write(System.Text.Encoding.ASCII.GetBytes(operationId.ToString("N")));
        stream.Flush(flushToDisk: true);
    }

    private void DeleteJournalFiles()
    {
        // Delete the older copy first so a crash cannot resurrect a stale backup alone.
        File.Delete(ResilientJsonStore.GetBackupPath(_journalPath));
        File.Delete(_journalPath);
        // Last: interrupted cleanup can leave a fence-only state, which blocks new
        // operations until manual reconciliation rather than guessing success.
        File.Delete(RollbackFencePath);
    }
}
