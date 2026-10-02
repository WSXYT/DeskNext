using Microsoft.Win32.SafeHandles;

namespace DeskNest.Core.Storage;

internal sealed record WindowsTreeIdentity(string RelativePath, bool IsDirectory, string NativeId,
    FileIdentity? File);

/// <summary>
/// Experimental bounded, handle-relative tree capture. The root is borrowed; every descendant
/// handle is retained until disposal. Empty directory nodes have native identities too.
/// This is not a filesystem snapshot: concurrent additions are detected on re-enumeration.
/// </summary>
internal sealed class WindowsTreeLease : IDisposable
{
    internal sealed record Node(string RelativePath, SafeFileHandle Handle, WindowsTreeIdentity Identity,
        IReadOnlyList<WindowsFileHandles.DirectoryEntry>? Children);

    private readonly List<Node> nodes = [];
    private readonly int maximumEntries;
    private readonly bool forCopy;
    private bool disposed;
    internal IReadOnlyList<Node> Nodes => nodes;
    internal IReadOnlyList<WindowsTreeIdentity> Identities => nodes.Select(n => n.Identity)
        .OrderBy(n => n.RelativePath, StringComparer.Ordinal).ToArray();

    private WindowsTreeLease(int maximumEntries, bool forCopy)
    {
        this.maximumEntries = maximumEntries;
        this.forCopy = forCopy;
    }

    private void CheckDirectory(SafeFileHandle handle)
    {
        if (forCopy) WindowsFileHandles.RequireCopyableContent(handle, directory: true);
        else WindowsFileHandles.RequireOrdinaryObject(handle, directory: true);
    }

    private FileIdentity CaptureFile(SafeFileHandle handle, CancellationToken token) => forCopy
        ? WindowsFileCopyLease.Capture(handle, token)
        : WindowsFileIdentity.CaptureContent(handle, token);

    internal static WindowsTreeLease Capture(SafeFileHandle root, CancellationToken token = default,
        int maximumEntries = DesktopOrganizationTransaction.MaximumDirectoryEntries,
        int maximumDepth = DesktopOrganizationTransaction.MaximumDirectoryDepth, bool forCopy = true)
    {
        if (maximumEntries < 0 || maximumEntries > DesktopOrganizationTransaction.MaximumDirectoryEntries ||
            maximumDepth < 0 || maximumDepth > DesktopOrganizationTransaction.MaximumDirectoryDepth)
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        token.ThrowIfCancellationRequested();
        var tree = new WindowsTreeLease(maximumEntries, forCopy);
        try
        {
            tree.CheckDirectory(root);
            string rootId = WindowsFileIdentity.Capture(root).NativeId;
            tree.nodes.Add(new("", root, new("", true, rootId, null),
                WindowsFileHandles.Enumerate(root, maximumEntries, token)));
            var pending = new Stack<(Node Node, int Depth)>();
            pending.Push((tree.nodes[0], 0));
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var (parent, depth) = pending.Pop();
                foreach (var child in parent.Children!)
                {
                    token.ThrowIfCancellationRequested();
                    if (tree.nodes.Count - 1 >= maximumEntries || depth + 1 > maximumDepth)
                        throw new IOException("Native directory capture exceeded its entry or depth budget.");
                    string relative = parent.RelativePath.Length == 0 ? child.Name : parent.RelativePath + "/" + child.Name;
                    var handle = child.IsDirectory
                        ? WindowsFileHandles.OpenDirectory(parent.Handle, child.Name)
                        : WindowsFileHandles.OpenFile(parent.Handle, child.Name);
                    bool owned = false;
                    try
                    {
                        if (child.IsDirectory) tree.CheckDirectory(handle);
                        string nativeId = WindowsFileIdentity.Capture(handle).NativeId;
                        if (!nativeId.AsSpan(0, 16).SequenceEqual(rootId.AsSpan(0, 16)))
                            throw new IOException("Directory member is not on the captured NTFS volume.");
                        var identity = new WindowsTreeIdentity(relative, child.IsDirectory, nativeId,
                            child.IsDirectory ? null : tree.CaptureFile(handle, token));
                        var node = new Node(relative, handle, identity, child.IsDirectory
                            ? WindowsFileHandles.Enumerate(handle, maximumEntries - tree.nodes.Count, token) : null);
                        tree.nodes.Add(node);
                        owned = true;
                        if (child.IsDirectory) pending.Push((node, depth + 1));
                    }
                    finally { if (!owned) handle.Dispose(); }
                }
            }
            tree.VerifyUnchanged(token);
            return tree;
        }
        catch { tree.Dispose(); throw; }
    }

    internal void VerifyUnchanged(CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        foreach (var node in nodes)
        {
            token.ThrowIfCancellationRequested();
            WindowsFileHandles.RequireOrdinaryObject(node.Handle, node.Identity.IsDirectory);
            if (WindowsFileIdentity.Capture(node.Handle).NativeId != node.Identity.NativeId)
                throw new IOException("Directory member native identity changed.");
            if (node.Identity.IsDirectory)
            {
                CheckDirectory(node.Handle);
                if (!node.Children!.SequenceEqual(WindowsFileHandles.Enumerate(node.Handle, maximumEntries, token)))
                    throw new IOException("Directory topology changed while holding the native copy lease.");
            }
            else if (node.Identity.File != CaptureFile(node.Handle, token))
                throw new IOException("Directory member content changed while holding the native copy lease.");
        }
    }

    internal OrganizationDirectoryMoveReceipt ToMoveReceipt(string source, string destination)
    {
        var identities = Identities;
        return new(source, destination, identities.Where(n => !n.IsDirectory)
            .Select(n => new DirectoryFileReceipt(n.RelativePath.Replace('/', Path.DirectorySeparatorChar), n.File!))
            .OrderBy(n => n.RelativePath, StringComparer.Ordinal).ToArray(), false)
        {
            Directories = identities.Where(n => n.IsDirectory && n.RelativePath.Length > 0)
                .Select(n => n.RelativePath.Replace('/', Path.DirectorySeparatorChar)).Order(StringComparer.Ordinal).ToArray(),
            DirectoryNativeIds = identities.Where(n => n.IsDirectory)
                .ToDictionary(n => n.RelativePath, n => n.NativeId, StringComparer.Ordinal)
        };
    }

    internal bool MatchesMoveReceipt(OrganizationDirectoryMoveReceipt expected) =>
        DesktopOrganizationTransaction.DirectoryReceiptsMatch(ToMoveReceipt(expected.SourcePath, expected.DestinationPath), expected);

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        // Root belongs to the caller; only handles opened by this capture are closed here.
        for (int i = nodes.Count - 1; i > 0; i--) nodes[i].Handle.Dispose();
        nodes.Clear();
    }
}
