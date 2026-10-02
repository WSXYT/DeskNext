using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskNest.Core.Storage;

internal sealed record CopyRecoveryIntent(int Version, Guid FileId, Guid SourceFileId,
    Guid TargetSpaceId, string SourcePath, string DestinationPath, bool IsDirectory)
{
    internal string TemporaryPath => Path.Combine(Path.GetDirectoryName(DestinationPath)!,
        (IsDirectory ? ".desknext-tree-" : ".desknext-copy-") + FileId.ToString("N"));
}

/// <summary>
/// Immutable intent written before the experimental copy has side effects. It never authorizes
/// deletion of a staged/published object. Unenrolled attempts require manual reconciliation.
/// </summary>
internal sealed class CopyRecoveryJournal(string dataDirectory)
{
    internal string JournalPath { get; } = Path.Combine(dataDirectory, "copy-recovery.json");
    internal bool Exists => File.Exists(JournalPath) || Directory.Exists(JournalPath);
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    internal async Task BeginAsync(CopyRecoveryIntent intent, CancellationToken token)
    {
        Validate(intent);
        token.ThrowIfCancellationRequested();
        FileSystemVolume.RequireNoReparsePoints(JournalPath);
        // Never replace an unresolved intent. Once created, finish the small write without
        // cancellation; truncated/power-loss evidence will be rejected and preserved on restart.
        await using var stream = new FileStream(JournalPath, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(stream, intent, Options, CancellationToken.None).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    internal CopyRecoveryIntent Read()
    {
        FileSystemVolume.RequireNoReparsePoints(JournalPath);
        using var stream = new FileStream(JournalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > 65536)
            throw new InvalidDataException("Invalid copy recovery intent size; preserve it for reconciliation.");
        CopyRecoveryIntent intent;
        try { intent = JsonSerializer.Deserialize<CopyRecoveryIntent>(stream, Options)
                ?? throw new InvalidDataException("Missing copy recovery intent."); }
        catch (JsonException ex) { throw new InvalidDataException("Corrupt copy recovery intent; preserve it for reconciliation.", ex); }
        Validate(intent);
        return intent;
    }

    internal void Complete(CopyRecoveryIntent expected)
    {
        if (Read() != expected) throw new InvalidDataException("Copy recovery intent changed; refusing acknowledgement.");
        File.Delete(JournalPath); // Only the app-owned journal, never the copied item.
    }

    internal string Archive(CopyRecoveryIntent expected)
    {
        if (Read() != expected) throw new InvalidDataException("Copy recovery intent changed; refusing to archive it.");
        string archive = Path.Combine(Path.GetDirectoryName(JournalPath)!,
            $"copy-recovery-{expected.FileId:N}-{Guid.NewGuid():N}.archived.json");
        File.Move(JournalPath, archive); // Preserve the exact evidence; never touch paths inside it.
        return archive;
    }

    private static void Validate(CopyRecoveryIntent intent)
    {
        if (intent.Version != 1 || intent.FileId == Guid.Empty || intent.SourceFileId == Guid.Empty ||
            intent.FileId == intent.SourceFileId || intent.TargetSpaceId == Guid.Empty ||
            intent.SourcePath is not { Length: > 0 and <= 4096 } ||
            intent.DestinationPath is not { Length: > 0 and <= 4096 } ||
            !Path.IsPathFullyQualified(intent.SourcePath) || !Path.IsPathFullyQualified(intent.DestinationPath))
            throw new InvalidDataException("Invalid copy recovery intent; manual reconciliation is required.");
    }
}
