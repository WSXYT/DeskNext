using Microsoft.Win32.SafeHandles;

namespace DeskNest.Core.Storage;

internal sealed record WindowsDirectoryCopyReceipt(string DestinationPath, string NativeId,
    IReadOnlyList<WindowsTreeIdentity> Nodes, IReadOnlyList<string> AncestorNativeIds, string VolumePath);

/// <summary>
/// Internal experiment: handle-relative writes and exact identity checks across publication.
/// Retains published handles through enrollment. A failed publication/verification preserves
/// its tree for manual reconciliation; there is no path-based recursive cleanup.
/// </summary>
internal sealed class WindowsDirectoryCopyLease : IDisposable
{
    private WindowsDirectoryLease? destinationParent;
    private SafeFileHandle? publishedRoot;
    private WindowsTreeLease? publishedTree;
    internal WindowsDirectoryCopyReceipt Receipt { get; }

    private WindowsDirectoryCopyLease(WindowsDirectoryCopyReceipt receipt,
        WindowsDirectoryLease parent, SafeFileHandle root, WindowsTreeLease tree)
    {
        Receipt = receipt;
        destinationParent = parent;
        publishedRoot = root;
        publishedTree = tree;
    }

    internal static Task<WindowsDirectoryCopyLease> CreateAsync(string sourcePath, string destinationPath,
        CancellationToken token = default, Action<string>? afterPublishBeforeReopen = null, Guid? publicationId = null,
        Action? afterFirstStagedWrite = null, bool createDestinationParents = true) =>
        Task.Run(() => Create(sourcePath, destinationPath, token, afterPublishBeforeReopen,
            publicationId ?? Guid.NewGuid(), afterFirstStagedWrite, createDestinationParents), token);

    private static WindowsDirectoryCopyLease Create(string sourcePath, string destinationPath,
        CancellationToken token, Action<string>? afterPublishBeforeReopen, Guid publicationId, Action? afterFirstStagedWrite,
        bool createDestinationParents)
    {
        token.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(sourcePath) || !Path.IsPathFullyQualified(destinationPath))
            throw new ArgumentException("Directory copy requires absolute local paths.");
        string sourceFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourcePath));
        string destinationFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationPath));
        WindowsFileHandles.RequireLeaf(Path.GetFileName(sourceFull));
        WindowsFileHandles.RequireLeaf(Path.GetFileName(destinationFull));
        if (destinationFull.Equals(sourceFull, StringComparison.OrdinalIgnoreCase) ||
            destinationFull.StartsWith(sourceFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A directory cannot be copied into its own tree.");
        // This preliminary check only refuses work; authorization is the later no-replace rename.
        if (File.Exists(destinationFull) || Directory.Exists(destinationFull))
            throw new IOException("The copy destination already exists.");

        using var sourceParent = WindowsDirectoryLease.Open(Path.GetDirectoryName(sourceFull)!);
        using var sourceRoot = WindowsFileHandles.OpenDirectory(sourceParent.Handle, Path.GetFileName(sourceFull));
        using var sourceTree = WindowsTreeLease.Capture(sourceRoot, token);
        WindowsDirectoryLease? parent = null;
        SafeFileHandle? tempRoot = null, reopened = null;
        WindowsTreeLease? reopenedTree = null;
        var createdDirectories = new Dictionary<string, SafeFileHandle>(StringComparer.Ordinal);
        string? retainedPath = null;
        bool published = false;
        try
        {
            token.ThrowIfCancellationRequested();
            parent = WindowsDirectoryLease.Open(Path.GetDirectoryName(destinationFull)!, create: createDestinationParents,
                requiredVolumePath: sourceParent.VolumePath,
                forbiddenAncestorNativeId: WindowsFileIdentity.Capture(sourceRoot).NativeId);
            if (!string.Equals(sourceParent.VolumePath, parent.VolumePath, StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("Directory copy currently requires one native NTFS volume.");
            string sourceCanonical = WindowsFileHandles.GetVolumePath(sourceRoot).TrimEnd('\\');
            string parentCanonical = WindowsFileHandles.GetVolumePath(parent.Handle).TrimEnd('\\');
            if (parentCanonical.Equals(sourceCanonical, StringComparison.OrdinalIgnoreCase) ||
                parentCanonical.StartsWith(sourceCanonical + "\\", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The resolved target is inside the source tree.");

            string temporaryName = ".desknext-tree-" + publicationId.ToString("N");
            tempRoot = WindowsFileHandles.CreateDirectory(parent.Handle, temporaryName, allowDelete: true);
            retainedPath = Path.Combine(Path.GetDirectoryName(destinationFull)!, temporaryName);
            createdDirectories.Add("", tempRoot);
            var created = new List<WindowsTreeIdentity>
            {
                new("", true, WindowsFileIdentity.Capture(tempRoot).NativeId, null)
            };
            foreach (var node in sourceTree.Nodes.Where(n => n.RelativePath.Length > 0)
                         .OrderBy(n => n.RelativePath.Count(c => c == '/'))
                         .ThenBy(n => n.RelativePath, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                int slash = node.RelativePath.LastIndexOf('/');
                string parentPath = slash < 0 ? "" : node.RelativePath[..slash];
                string leaf = node.RelativePath[(slash + 1)..];
                var targetParent = createdDirectories[parentPath];
                if (node.Identity.IsDirectory)
                {
                    var handle = WindowsFileHandles.CreateDirectory(targetParent, leaf);
                    createdDirectories.Add(node.RelativePath, handle);
                    created.Add(new(node.RelativePath, true, WindowsFileIdentity.Capture(handle).NativeId, null));
                }
                else
                {
                    FileIdentity identity = CopyFile(node.Handle, targetParent, leaf, token, afterFirstStagedWrite);
                    created.Add(new(node.RelativePath, false, identity.NativeId!, identity));
                }
            }
            var expected = created.OrderBy(n => n.RelativePath, StringComparer.Ordinal).ToArray();
            sourceTree.VerifyUnchanged(token);
            using (var stagedTree = WindowsTreeLease.Capture(tempRoot, token))
                AssertExact(expected, stagedTree.Identities);

            // NTFS rejects a root rename while its descendants are open. Keep their original
            // native identities across the close/rename/reopen gap; matching bytes is insufficient.
            foreach (var entry in createdDirectories.Reverse())
                if (entry.Key.Length != 0) entry.Value.Dispose();
            parent.VerifyOrdinaryAncestors();
            token.ThrowIfCancellationRequested();
            WindowsFileHandles.RenameInDirectory(tempRoot, Path.GetFileName(destinationFull));
            published = true;
            retainedPath = destinationFull;
            tempRoot.Dispose();
            tempRoot = null;
            afterPublishBeforeReopen?.Invoke(destinationFull); // Test-only deterministic replacement seam.
            reopened = WindowsFileHandles.OpenDirectory(parent.Handle, Path.GetFileName(destinationFull));
            reopenedTree = WindowsTreeLease.Capture(reopened, token);
            AssertExact(expected, reopenedTree.Identities);
            parent.VerifyOrdinaryAncestors();
            var receipt = new WindowsDirectoryCopyReceipt(destinationFull, expected[0].NativeId,
                expected, parent.NativeIds, parent.VolumePath);
            var lease = new WindowsDirectoryCopyLease(receipt, parent, reopened, reopenedTree);
            lease.VerifyForEnrollment(token);
            parent = null;
            reopened = null;
            reopenedTree = null;
            return lease;
        }
        catch (Exception error) when (retainedPath is not null)
        {
            throw new IOException($"Directory copy {(published ? "published" : "staged")} at '{retainedPath}' but enrollment is unconfirmed; reconcile manually.", error);
        }
        finally
        {
            foreach (var entry in createdDirectories.Reverse()) entry.Value.Dispose();
            reopenedTree?.Dispose();
            reopened?.Dispose();
            tempRoot?.Dispose();
            parent?.Dispose();
        }
    }

    private static FileIdentity CopyFile(SafeFileHandle source, SafeFileHandle parent, string leaf,
        CancellationToken token, Action? afterFirstStagedWrite)
    {
        FileIdentity expected = WindowsFileCopyLease.Capture(source, token);
        using var output = WindowsFileHandles.OpenFile(parent, leaf, create: true);
        byte[] buffer = new byte[64 * 1024];
        long offset = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            int read = RandomAccess.Read(source, buffer, offset);
            if (read == 0) break;
            RandomAccess.Write(output, buffer.AsSpan(0, read), offset);
            offset += read;
            if (afterFirstStagedWrite is not null && offset == read)
            {
                RandomAccess.FlushToDisk(output);
                afterFirstStagedWrite(); // Trusted test-only partial-write barrier.
            }
        }
        RandomAccess.FlushToDisk(output);
        var actual = WindowsFileCopyLease.Capture(output, token);
        if (expected != WindowsFileCopyLease.Capture(source, token) ||
            expected.Length != actual.Length || expected.Sha256 != actual.Sha256)
            throw new IOException("Directory file copy verification failed.");
        return actual;
    }

    internal WindowsDirectoryCopyReceipt VerifyForEnrollment(CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(publishedTree is null, this);
        destinationParent!.VerifyPathBinding();
        if (destinationParent.VolumePath != Receipt.VolumePath ||
            !destinationParent.NativeIds.SequenceEqual(Receipt.AncestorNativeIds))
            throw new IOException("Published directory ancestor identities changed.");
        publishedTree.VerifyUnchanged(token);
        AssertExact(Receipt.Nodes, publishedTree.Identities);
        return Receipt;
    }

    private static void AssertExact(IReadOnlyList<WindowsTreeIdentity> expected, IReadOnlyList<WindowsTreeIdentity> actual)
    {
        if (!expected.SequenceEqual(actual))
            throw new IOException("Published directory node identities, topology, or content changed.");
    }

    public void Dispose()
    {
        publishedTree?.Dispose(); publishedTree = null;
        publishedRoot?.Dispose(); publishedRoot = null;
        destinationParent?.Dispose(); destinationParent = null;
    }
}
