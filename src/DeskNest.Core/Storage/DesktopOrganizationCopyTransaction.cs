
namespace DeskNest.Core.Storage;

/// <summary>
/// Experimental copy primitive. Not exposed outside Core until handle-relative
/// traversal and published-object ownership are verified.
/// The source remains untouched; a temporary sibling is checked before publication.
/// </summary>
internal static class DesktopOrganizationCopyTransaction
{
    public static async Task<FileIdentity> CopyFileAsync(
        string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequirePaths(sourcePath, destinationPath);
        FileSystemVolume.RequireNoReparsePoints(sourcePath);
        FileSystemVolume.RequireNoReparsePoints(destinationPath);
        if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
            throw new IOException("The paste destination already exists.");

        string parent = Path.GetDirectoryName(destinationPath)!
            ?? throw new InvalidDataException("The paste destination has no parent directory.");
        Directory.CreateDirectory(parent);
        FileSystemVolume.RequireNoReparsePoints(parent);
        string temporary = Path.Combine(parent, $".{Path.GetFileName(destinationPath)}.desknest-copy-{Guid.NewGuid():N}.tmp");
        try
        {
            FileIdentity sourceBefore = FileIdentity.Capture(sourcePath, cancellationToken);
            await using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
            {
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            FileIdentity sourceAfter = FileIdentity.Capture(sourcePath, cancellationToken);
            if (!SameIdentity(sourceBefore, sourceAfter))
                throw new IOException("The source changed while it was being copied.");
            FileIdentity copied = FileIdentity.Capture(temporary, cancellationToken);
            if (!SameContent(sourceBefore, copied))
                throw new IOException("The copied file failed identity verification.");

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destinationPath);
            return FileIdentity.Capture(destinationPath, cancellationToken);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    public static async Task CopyDirectoryAsync(
        string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequirePaths(sourcePath, destinationPath);
        string sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourcePath));
        string destination = Path.GetFullPath(destinationPath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string sourcePrefix = Path.EndsInDirectorySeparator(sourceRoot) ? sourceRoot : sourceRoot + Path.DirectorySeparatorChar;
        if (destination.StartsWith(sourcePrefix, comparison))
            throw new InvalidDataException("A directory cannot be copied into its own tree.");
        FileSystemVolume.RequireNoReparsePoints(sourcePath);
        FileSystemVolume.RequireNoReparsePoints(destinationPath);
        if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
            throw new IOException("The paste destination already exists.");

        string parent = Path.GetDirectoryName(destinationPath)!
            ?? throw new InvalidDataException("The paste destination has no parent directory.");
        Directory.CreateDirectory(parent);
        FileSystemVolume.RequireNoReparsePoints(parent);
        string temporary = Path.Combine(parent, $".{Path.GetFileName(destinationPath)}.desknest-copy-{Guid.NewGuid():N}.tmp");
        try
        {
            var before = DesktopOrganizationTransaction.CaptureDirectorySnapshot(sourcePath, cancellationToken);
            Directory.CreateDirectory(temporary);
            await CopyDirectoryContentsAsync(sourcePath, temporary, cancellationToken).ConfigureAwait(false);
            var sourceAfter = DesktopOrganizationTransaction.CaptureDirectorySnapshot(sourcePath, cancellationToken);
            if (!ManifestsEqual(before.Files, sourceAfter.Files) ||
                !before.Directories.SequenceEqual(sourceAfter.Directories))
                throw new IOException("The source directory changed while it was being copied.");
            var copied = DesktopOrganizationTransaction.CaptureDirectorySnapshot(temporary, cancellationToken);
            if (!ManifestsEqual(before.Files, copied.Files) ||
                !before.Directories.SequenceEqual(copied.Directories))
                throw new IOException("The copied directory failed identity verification.");
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(temporary, destinationPath);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    private static async Task CopyDirectoryContentsAsync(string source, string destination, CancellationToken cancellationToken)
    {
        foreach (string directory in Directory.EnumerateDirectories(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileSystemVolume.RequireNoReparsePoints(directory);
            string child = Path.Combine(destination, Path.GetFileName(directory));
            Directory.CreateDirectory(child);
            await CopyDirectoryContentsAsync(directory, child, cancellationToken).ConfigureAwait(false);
        }

        foreach (string file in Directory.EnumerateFiles(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileSystemVolume.RequireNoReparsePoints(file);
            string child = Path.Combine(destination, Path.GetFileName(file));
            await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = new FileStream(child, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static void RequirePaths(string source, string destination)
    {
        if (!Path.IsPathFullyQualified(source) || !Path.IsPathFullyQualified(destination))
            throw new ArgumentException("Copy paths must be fully qualified.");
        if (!File.Exists(source) && !Directory.Exists(source))
            throw new FileNotFoundException("The copy source does not exist.", source);
        if (Path.GetFileName(destination) is not { Length: > 0 } ||
            Path.GetFileName(destination) is "." or "..")
            throw new InvalidDataException("The copy destination is not a safe leaf path.");
    }

    private static bool SameIdentity(FileIdentity left, FileIdentity right) =>
        left.Length == right.Length && left.LastWriteTimeUtcTicks == right.LastWriteTimeUtcTicks &&
        string.Equals(left.Sha256, right.Sha256, StringComparison.OrdinalIgnoreCase) &&
        (!OperatingSystem.IsWindows() || string.Equals(left.NativeId, right.NativeId, StringComparison.Ordinal));

    private static bool SameContent(FileIdentity left, FileIdentity right) =>
        left.Length == right.Length && string.Equals(left.Sha256, right.Sha256, StringComparison.OrdinalIgnoreCase);

    private static bool ManifestsEqual(IReadOnlyList<DirectoryFileReceipt> left, IReadOnlyList<DirectoryFileReceipt> right) =>
        left.Count == right.Count && left.Zip(right).All(pair =>
            string.Equals(pair.First.RelativePath, pair.Second.RelativePath, StringComparison.Ordinal) &&
            SameContent(pair.First.Identity, pair.Second.Identity));

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }
}
