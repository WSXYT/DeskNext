using System.Diagnostics;
using System.Text;

namespace DeskNest.Platform;

public sealed record FilePreview(string Name, string Path, string Kind, string Content, bool Truncated, long? Length);

/// <summary>Explicit OS open/reveal actions and bounded, non-executing previews.</summary>
public static class PlatformFileActions
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".csv", ".json", ".xml", ".yaml", ".yml", ".log", ".cs", ".cpp", ".h", ".py", ".rs"
    };

    public static Task OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        path = RequireExistingLocalPath(path);
        // A successful shell handoff (e.g. an existing Explorer) can have no new process.
        // Start still throws when the OS rejects the request; null is not a failure receipt.
        using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        return Task.CompletedTask;
    }

    public static Task RevealAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        path = RequireExistingLocalPath(path);
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path))
            ?? throw new NotSupportedException("A filesystem root has no containing folder.");
        return OpenAsync(parent, cancellationToken);
    }

    public static async Task<FilePreview> ReadPreviewAsync(
        string path,
        CancellationToken cancellationToken = default,
        int maximumBytes = 65_536,
        int maximumEntries = 200)
    {
        if (maximumBytes is < 1 or > 1_048_576)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (maximumEntries is < 1 or > 1_000)
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));

        cancellationToken.ThrowIfCancellationRequested();
        path = RequireExistingLocalPath(path);
        var name = Path.GetFileName(path);

        if (Directory.Exists(path))
        {
            var names = new List<string>(maximumEntries);
            using var entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
            while (entries.MoveNext())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (names.Count == maximumEntries)
                    return new(name, path, "directory", string.Join(Environment.NewLine, names), true, null);
                names.Add(Path.GetFileName(entries.Current));
            }
            return new(name, path, "directory", string.Join(Environment.NewLine, names), false, null);
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = stream.Length;
        if (!TextExtensions.Contains(Path.GetExtension(path)))
            return new(name, path, "metadata", string.Empty, false, length);

        var bytes = new byte[maximumBytes];
        var used = 0;
        while (used < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(used), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            used += read;
        }

        var truncated = stream.Position < stream.Length;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = DecodePreview(bytes.AsSpan(0, used), truncated);
            if (text.Contains('\0'))
                return new(name, path, "metadata", string.Empty, truncated, length);
            return new(name, path, "text", text, truncated, length);
        }
        catch (DecoderFallbackException)
        {
            return new(name, path, "metadata", string.Empty, truncated, length);
        }
    }

    private static string DecodePreview(ReadOnlySpan<byte> bytes, bool truncated)
    {
        // Match the existing BOM-aware reader, with strict decoding. A bounded prefix is not EOF:
        // keep only complete characters and discard the decoder\'s incomplete trailing code point.
        Encoding encoding = new UTF8Encoding(false, true);
        int preamble = 0;
        if (bytes.StartsWith(new byte[] { 0xff, 0xfe, 0, 0 })) { encoding = new UTF32Encoding(false, false, true); preamble = 4; }
        else if (bytes.StartsWith(new byte[] { 0, 0, 0xfe, 0xff })) { encoding = new UTF32Encoding(true, false, true); preamble = 4; }
        else if (bytes.StartsWith(new byte[] { 0xff, 0xfe })) { encoding = new UnicodeEncoding(false, false, true); preamble = 2; }
        else if (bytes.StartsWith(new byte[] { 0xfe, 0xff })) { encoding = new UnicodeEncoding(true, false, true); preamble = 2; }
        else if (bytes.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) preamble = 3;
        var payload = bytes[preamble..];
        var characters = new char[encoding.GetMaxCharCount(payload.Length)];
        int count = encoding.GetDecoder().GetChars(payload, characters, flush: !truncated);
        return new string(characters, 0, count);
    }

    /// <summary>Read-only path validation, not a lease or permission for later filesystem mutation.</summary>
    public static string RequireExistingLocalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("An absolute local filesystem path is required.", nameof(path));

        path = Path.GetFullPath(path);
        if (!File.Exists(path) && !Directory.Exists(path))
            throw new FileNotFoundException("The selected item is unavailable.");

        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked items are not eligible for this action.");
        }
        return path;
    }
}
