using System.IO.Compression;
using System.Security.Cryptography;

namespace DeskNest.Inference;

/// <summary>Installs the pinned model package beside, never over, an existing model. Activation is separate.</summary>
public static class LocalModelInstaller
{
    public const long ArchiveBytes = 812781615;
    public const string ArchiveSha256 = "bd2464a8b63f195fd1aed579b355b37d3ef6f45f1e796116b041846a7de3f6fc";
    public const string ManifestSha256 = "3a65f3fb45166e0e7e2c044802740712ae48770cc26a11a715984d7b3aa81f52";
    private const long MaximumExpandedBytes = 2L * 1024 * 1024 * 1024;
    private static readonly string[] Names = ["encoder.onnx", "encoder.onnx.data", "head.onnx", "head.onnx.data",
        "rl_agent_config.json", "tokenizer.json", "manifest.json", "LICENSE-Laya.txt", "NOTICE.md", "export-strict.patch"];

    /// <summary>Returns a verified directory without changing settings. Interrupted staging is retained, never adopted.</summary>
    public static async Task<string> InstallArchiveAsync(string archivePath, string cacheRoot,
        IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(archivePath) || !Path.IsPathFullyQualified(cacheRoot))
            throw new ArgumentException("The model package and cache must use absolute filesystem paths.");
        archivePath = Path.GetFullPath(archivePath);
        cacheRoot = Path.GetFullPath(cacheRoot);
        RequirePlainAncestors(archivePath);
        RequirePlainAncestors(cacheRoot);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(45));
        var token = deadline.Token;
        await using var archive = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (archive.Length != ArchiveBytes) throw new InvalidDataException("Not the supported DeskNext model package (length mismatch).");
        progress?.Report(0);
        if (!Convert.ToHexString(await SHA256.HashDataAsync(archive, token).ConfigureAwait(false))
            .Equals(ArchiveSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The model package differs from the application-pinned release.");
        archive.Position = 0;
        using var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
        if (zip.Entries.Count != Names.Length ||
            !zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal).SequenceEqual(Names.Order(StringComparer.Ordinal)))
            throw new InvalidDataException("Unexpected model package entries.");
        long expandedBytes = 0;
        foreach (var entry in zip.Entries)
        {
            if (entry.Length < 0 || entry.Length > MaximumExpandedBytes - expandedBytes)
                throw new InvalidDataException("The model package exceeds the extraction budget.");
            expandedBytes += entry.Length;
        }
        string id = Guid.NewGuid().ToString("N");
        string staging = Path.Combine(cacheRoot, ".model-unpack-" + id);
        string destination = Path.Combine(cacheRoot, "laya-multilingual-fp32-" + id);
        RequirePlainAncestors(cacheRoot);
        Directory.CreateDirectory(cacheRoot);
        if (Directory.Exists(staging) || File.Exists(staging)) throw new IOException("Model staging already exists.");
        Directory.CreateDirectory(staging);
        byte[] buffer = new byte[131072];
        long written = 0;
        int reported = -1;
        foreach (string name in Names)
        {
            token.ThrowIfCancellationRequested();
            RequirePlainAncestors(staging);
            var entry = zip.GetEntry(name)!;
            await using var input = entry.Open();
            await using var output = new FileStream(Path.Combine(staging, name), FileMode.CreateNew,
                FileAccess.Write, FileShare.None, buffer.Length, FileOptions.Asynchronous);
            long entryWritten = 0;
            int count;
            while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                entryWritten += count;
                if (entryWritten > entry.Length) throw new InvalidDataException("A model entry exceeds its declared size.");
                await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                written += count;
                int percent = (int)(written * 99 / Math.Max(1, expandedBytes));
                if (percent != reported) { progress?.Report(percent); reported = percent; }
            }
            if (entryWritten != entry.Length) throw new InvalidDataException("Incomplete model entry.");
        }
        RequirePlainAncestors(staging);
        await using (var manifest = File.OpenRead(Path.Combine(staging, "manifest.json")))
            if (!Convert.ToHexString(await SHA256.HashDataAsync(manifest, token).ConfigureAwait(false))
                .Equals(ManifestSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The installed manifest differs from the pinned release.");
        await Task.Run(() => Probe.VerifyModel(staging, token), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        RequirePlainAncestors(staging);
        RequirePlainAncestors(cacheRoot);
        Directory.Move(staging, destination);
        progress?.Report(100);
        return destination;
    }

    internal static void RequirePlainAncestors(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked model-package/cache paths are unsupported.");
    }
}
