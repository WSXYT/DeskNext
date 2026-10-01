using DeskNest.Core.Storage;

namespace DeskNest.Core.Workspace;

/// <summary>Schema checks only; structurally valid evidence never authorizes an operation by itself.</summary>
internal static class PublicationEvidenceValidation
{
    internal static void Validate(WorkspacePublicationEvidence evidence, bool isDirectory)
    {
        if (evidence is null || !IsNativeId(evidence.NativeId) || !IsNativeId(evidence.ParentNativeId) ||
            !IsVolumeRoot(evidence.VolumePath) || evidence.AncestorNativeIds is not { Count: > 0 and <= 129 } ancestors ||
            ancestors.Any(id => !IsNativeId(id) || !SameVolume(id, evidence.NativeId)) ||
            ancestors.Distinct(StringComparer.OrdinalIgnoreCase).Count() != ancestors.Count ||
            ancestors[^1] != evidence.ParentNativeId || isDirectory != (evidence.DirectoryNodes is not null))
            throw new InvalidDataException("Invalid publication identity or ancestor evidence.");

        if (!isDirectory)
        {
            RequireFileContent(evidence.Length, evidence.LastWriteTimeUtcTicks, evidence.Sha256);
            return;
        }
        if (evidence.Length != 0 || evidence.LastWriteTimeUtcTicks != 0 || evidence.Sha256 != "" ||
            evidence.DirectoryNodes is not { Count: > 0 } nodes ||
            nodes.Count > DesktopOrganizationTransaction.MaximumDirectoryEntries + 1)
            throw new InvalidDataException("Invalid directory publication envelope.");

        var byPath = new Dictionary<string, WorkspacePublishedNodeIdentity>(StringComparer.OrdinalIgnoreCase);
        string? previous = null;
        foreach (var node in nodes)
        {
            if (node is null || node.RelativePath is null || node.RelativePath.Length > 4096 ||
                !IsNativeId(node.NativeId) || !SameVolume(node.NativeId, evidence.NativeId) ||
                !byPath.TryAdd(node.RelativePath, node) ||
                previous is not null && StringComparer.Ordinal.Compare(previous, node.RelativePath) >= 0)
                throw new InvalidDataException("Invalid, duplicate, or unsorted publication node.");
            previous = node.RelativePath;
            if (node.RelativePath.Length != 0)
            {
                string[] components = node.RelativePath.Split('/');
                if (components.Length > DesktopOrganizationTransaction.MaximumDirectoryDepth)
                    throw new InvalidDataException("Publication topology exceeds its depth budget.");
                foreach (string component in components) WindowsFileHandles.RequireLeaf(component);
                int slash = node.RelativePath.LastIndexOf('/');
                string parent = slash < 0 ? "" : node.RelativePath[..slash];
                if (!byPath.TryGetValue(parent, out var parentNode) || !parentNode.IsDirectory)
                    throw new InvalidDataException("Publication node has no directory parent.");
            }
            if (node.IsDirectory)
            {
                if (node.Length != 0 || node.LastWriteTimeUtcTicks != 0 || node.Sha256 != "")
                    throw new InvalidDataException("Directory nodes cannot contain file evidence.");
            }
            else RequireFileContent(node.Length, node.LastWriteTimeUtcTicks, node.Sha256);
        }
        var root = nodes[0];
        if (root.RelativePath != "" || !root.IsDirectory || root.NativeId != evidence.NativeId)
            throw new InvalidDataException("Publication root evidence is missing or inconsistent.");
    }

    private static void RequireFileContent(long length, long ticks, string? hash)
    {
        if (length < 0 || ticks < 0 || ticks > DateTime.MaxValue.Ticks ||
            hash is not { Length: 64 } || !hash.All(Uri.IsHexDigit))
            throw new InvalidDataException("Invalid publication file content evidence.");
    }

    private static bool IsNativeId(string? value) => value is { Length: 49 } && value[16] == ':' &&
        value.Where((_, index) => index != 16).All(Uri.IsHexDigit);

    private static bool SameVolume(string left, string right) =>
        left.AsSpan(0, 16).Equals(right.AsSpan(0, 16), StringComparison.OrdinalIgnoreCase);

    private static bool IsVolumeRoot(string? path) => path is { Length: 49 } &&
        path.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase) && path.EndsWith(@"}\", StringComparison.Ordinal) &&
        Guid.TryParseExact(path.AsSpan(11, 36), "D", out _);
}
