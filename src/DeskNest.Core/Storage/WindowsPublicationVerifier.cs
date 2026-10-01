using DeskNest.Core.Workspace;

namespace DeskNest.Core.Storage;

/// <summary>
/// Read-only revalidation of creation-time evidence. This method releases its handles on return;
/// callers MUST NOT treat a successful check as permission for a later move or pathname deletion.
/// </summary>
internal static class WindowsPublicationVerifier
{
    internal static void Verify(string path, WorkspacePublicationEvidence evidence, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("An absolute publication path is required.");
        PublicationEvidenceValidation.Validate(evidence, evidence.DirectoryNodes is not null);
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        WindowsFileHandles.RequireLeaf(Path.GetFileName(full));
        using var parent = WindowsDirectoryLease.Open(Path.GetDirectoryName(full)!);
        if (!string.Equals(parent.VolumePath, evidence.VolumePath, StringComparison.OrdinalIgnoreCase) ||
            !parent.NativeIds.SequenceEqual(evidence.AncestorNativeIds, StringComparer.OrdinalIgnoreCase))
            throw new IOException("Published object volume or ancestor identities changed.");

        if (evidence.DirectoryNodes is null)
        {
            using var file = WindowsFileHandles.OpenFile(parent.Handle, Path.GetFileName(full));
            var actual = WindowsFileCopyLease.Capture(file, token);
            if (actual.NativeId != evidence.NativeId || actual.Length != evidence.Length ||
                actual.LastWriteTimeUtcTicks != evidence.LastWriteTimeUtcTicks || actual.Sha256 != evidence.Sha256)
                throw new IOException("Published file identity no longer matches its durable receipt.");
        }
        else
        {
            using var root = WindowsFileHandles.OpenDirectory(parent.Handle, Path.GetFileName(full));
            using var tree = WindowsTreeLease.Capture(root, token);
            var actual = tree.Identities;
            var expected = evidence.DirectoryNodes;
            if (actual.Count != expected.Count)
                throw new IOException("Published directory topology changed.");
            for (int i = 0; i < actual.Count; i++)
            {
                var node = actual[i];
                var saved = expected[i];
                if (node.RelativePath != saved.RelativePath || node.IsDirectory != saved.IsDirectory ||
                    node.NativeId != saved.NativeId ||
                    !node.IsDirectory && (node.File!.Length != saved.Length ||
                        node.File.LastWriteTimeUtcTicks != saved.LastWriteTimeUtcTicks || node.File.Sha256 != saved.Sha256))
                    throw new IOException("Published directory node identity or content changed.");
            }
            tree.VerifyUnchanged(token);
        }
        parent.VerifyOrdinaryAncestors();
    }
}
