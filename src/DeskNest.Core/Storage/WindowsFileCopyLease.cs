using Microsoft.Win32.SafeHandles;

namespace DeskNest.Core.Storage;

/// <summary>
/// Experimental NTFS file copy. Owns source, destination and ancestor handles through enrollment.
/// Disposing a published lease only releases handles; it never deletes the published object.
/// </summary>
internal sealed class WindowsFileCopyLease : IDisposable
{
    private WindowsDirectoryLease? sourceParent, destinationParent;
    private SafeFileHandle? source, output;
    private bool published;
    internal string DestinationPath { get; }
    internal IReadOnlyList<string> DestinationAncestorNativeIds => destinationParent!.NativeIds;
    internal string DestinationVolumePath => destinationParent!.VolumePath;
    internal FileIdentity Identity { get; private set; } = null!;

    private WindowsFileCopyLease(string destinationPath) => DestinationPath = destinationPath;

    internal static Task<WindowsFileCopyLease> CreateAsync(string sourcePath, string destinationPath,
        CancellationToken cancellationToken = default, Guid? publicationId = null, Action? afterFirstStagedWrite = null) =>
        Task.Run(() => Create(sourcePath, destinationPath, cancellationToken, publicationId ?? Guid.NewGuid(), afterFirstStagedWrite), cancellationToken);

    private static WindowsFileCopyLease Create(string sourcePath, string destinationPath, CancellationToken token,
        Guid publicationId, Action? afterFirstStagedWrite)
    {
        token.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(sourcePath) || !Path.IsPathFullyQualified(destinationPath))
            throw new ArgumentException("Copy requires absolute local paths.");
        string sourceFull = Path.GetFullPath(sourcePath), destinationFull = Path.GetFullPath(destinationPath);
        WindowsFileHandles.RequireLeaf(Path.GetFileName(sourceFull));
        WindowsFileHandles.RequireLeaf(Path.GetFileName(destinationFull));
        var lease = new WindowsFileCopyLease(destinationFull);
        try
        {
            lease.sourceParent = WindowsDirectoryLease.Open(Path.GetDirectoryName(sourceFull)!);
            lease.source = WindowsFileHandles.OpenFile(lease.sourceParent.Handle, Path.GetFileName(sourceFull));
            FileIdentity original = Capture(lease.source, token);
            token.ThrowIfCancellationRequested();
            lease.destinationParent = WindowsDirectoryLease.Open(Path.GetDirectoryName(destinationFull)!, create: true);
            lease.output = WindowsFileHandles.OpenFile(lease.destinationParent.Handle,
                ".desknext-copy-" + publicationId.ToString("N"), create: true);
            byte[] buffer = new byte[64 * 1024];
            long offset = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                int read = RandomAccess.Read(lease.source, buffer, offset);
                if (read == 0) break;
                RandomAccess.Write(lease.output, buffer.AsSpan(0, read), offset);
                offset += read;
                if (afterFirstStagedWrite is not null && offset == read)
                {
                    RandomAccess.FlushToDisk(lease.output);
                    afterFirstStagedWrite(); // Trusted test-only partial-write barrier; never read from metadata.
                }
            }
            RandomAccess.FlushToDisk(lease.output);
            var copied = Capture(lease.output, token);
            if (original != Capture(lease.source, token) || original.Length != copied.Length || original.Sha256 != copied.Sha256)
                throw new IOException("Copy verification failed; source or copied bytes changed.");
            token.ThrowIfCancellationRequested();
            WindowsFileHandles.RenameInDirectory(lease.output, Path.GetFileName(destinationFull));
            lease.published = true; // No cleanup deletion is authorized after publication, even on capture failure.
            lease.Identity = Capture(lease.output, token);
            return lease;
        }
        catch (Exception error)
        {
            bool retainPublished = lease.published;
            lease.Dispose();
            if (retainPublished)
                throw new IOException($"Copy published at '{destinationFull}' but verification is unconfirmed; reconcile manually.", error);
            throw;
        }
    }

    internal FileIdentity VerifyForEnrollment(CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(output is null, this);
        if (!published) throw new InvalidOperationException("The copy has not been published.");
        sourceParent!.VerifyOrdinaryAncestors();
        destinationParent!.VerifyPathBinding();
        var current = Capture(output, token);
        if (current != Identity) throw new IOException("Published copy identity changed before enrollment.");
        return current;
    }

    internal static FileIdentity Capture(SafeFileHandle handle, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        WindowsFileHandles.RequireCopyableContent(handle, directory: false);
        var identity = WindowsFileIdentity.CaptureContent(handle, token);
        WindowsFileHandles.RequireCopyableContent(handle, directory: false);
        return identity;
    }

    public void Dispose()
    {
        if (output is not null)
        {
            // A temporary file can only be removed through its original creation handle.
            // Failed native cleanup leaves evidence; it never falls back to a pathname delete.
            if (!published)
            {
                try { WindowsFileHandles.DeleteOwnedFile(output); }
                catch (IOException) { }
            }
            output.Dispose();
            output = null;
        }
        destinationParent?.Dispose(); destinationParent = null;
        source?.Dispose(); source = null;
        sourceParent?.Dispose(); sourceParent = null;
    }
}
