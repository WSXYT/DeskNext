using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using DeskNest.Core.Storage;
using Microsoft.Win32.SafeHandles;

namespace DeskNest.Core.Workspace;

public sealed record ModelAssetRemovalPlan(string DirectoryPath, string NativeId, long Revision, IReadOnlyList<string> Files);

/// <summary>Explicit removal of pinned model data, not a recursive directory cleaner. Windows NTFS only.</summary>
public static class ModelAssetRemoval
{
    public static ModelAssetRemovalPlan Prepare(WorkspaceState state, string trustedManifestSha256)
    {
        string directory = RequireTarget(state);
        using var root = WindowsDirectoryLease.Open(directory);
        RequireOutsideSpaces(root, state);
        var files = ReadManifest(root.Handle, trustedManifestSha256);
        return new(directory, WindowsFileIdentity.Capture(root.Handle).NativeId, state.Revision, files.Keys.Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>Caller holds workspace organization/metadata gates. Missing assets permit retry; modified assets refuse before deletion.</summary>
    public static int Remove(WorkspaceState state, ModelAssetRemovalPlan plan, string trustedManifestSha256, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        string directory = RequireTarget(state);
        if (state.Revision != plan.Revision || !PathComparer.Equals(directory, plan.DirectoryPath))
            throw new InvalidDataException("Model removal selection is stale; review it again.");
        using var root = WindowsDirectoryLease.Open(directory);
        if (WindowsFileIdentity.Capture(root.Handle).NativeId != plan.NativeId)
            throw new InvalidDataException("The selected model directory was replaced.");
        RequireOutsideSpaces(root, state);
        var files = ReadManifest(root.Handle, trustedManifestSha256);
        if (!files.Keys.Order(StringComparer.Ordinal).SequenceEqual(plan.Files))
            throw new InvalidDataException("The model removal manifest changed.");
        var opened = new List<SafeFileHandle>();
        try
        {
            // Bind and check every existing asset before the first irreversible action.
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                SafeFileHandle handle;
                try { handle = WindowsFileHandles.OpenFile(root.Handle, file.Key, allowDelete: true); }
                catch (IOException error) when (error.InnerException is Win32Exception { NativeErrorCode: 2 }) { continue; }
                opened.Add(handle);
                WindowsFileHandles.RequireCopyableContent(handle, directory: false);
                if (!WindowsFileIdentity.CaptureContent(handle, token).Sha256.Equals(file.Value, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("A model asset was modified; no model files were removed.");
            }
            token.ThrowIfCancellationRequested();
            foreach (var handle in opened)
            {
                root.VerifyPathBinding();
                WindowsFileHandles.RequireCopyableContent(handle, directory: false);
                WindowsFileHandles.DeleteOwnedFile(handle);
                handle.Dispose();
            }
            return opened.Count;
        }
        finally { foreach (var handle in opened) handle.Dispose(); }
    }

    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static bool Overlaps(string a, string b) => PathComparer.Equals(a, b) ||
        a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
        b.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string RequireTarget(WorkspaceState state)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Model data removal currently requires Windows NTFS.");
        if (state.Settings.ModelCacheDirectory is not { } path || state.Settings.GetModelInstallationRoot() is null)
            throw new InvalidDataException("Choose an application-installed model before removing its data.");
        string directory = Normalize(path);
        var protectedPaths = state.Spaces.Select(s => s.Folder).Concat(state.Files.Select(f => f.Path)).Append(state.Settings.ManagedRoot);
        if (protectedPaths.Any(p => Overlaps(directory, Normalize(p))))
            throw new InvalidDataException("The model overlaps file-space data and cannot be removed here.");
        return directory;
    }

    private static void RequireOutsideSpaces(WindowsDirectoryLease model, WorkspaceState state)
    {
        string modelId = WindowsFileIdentity.Capture(model.Handle).NativeId;
        foreach (string path in state.Spaces.Select(s => s.Folder).Append(state.Settings.ManagedRoot))
        {
            // Do not infer non-overlap through network, linked or otherwise unverifiable namespaces.
            if (Path.GetPathRoot(path) is not { Length: 3 } drive || drive[1] != ':')
                throw new NotSupportedException("Model removal requires verifiable local file-space boundaries.");
            FileSystemVolume.RequireNoReparsePoints(path);
            if (!Directory.Exists(path)) continue;
            using var space = WindowsDirectoryLease.Open(path);
            if (model.NativeIds.Contains(WindowsFileIdentity.Capture(space.Handle).NativeId) || space.NativeIds.Contains(modelId))
                throw new InvalidDataException("The model aliases file-space data.");
        }
    }

    private static Dictionary<string, string> ReadManifest(SafeFileHandle directory, string expectedHash)
    {
        using var handle = WindowsFileHandles.OpenFile(directory, "manifest.json");
        long length = RandomAccess.GetLength(handle);
        if (length is < 1 or > 65536) throw new InvalidDataException("Unsupported model manifest size.");
        byte[] bytes = new byte[(int)length];
        int read = 0;
        while (read < bytes.Length)
        {
            int count = RandomAccess.Read(handle, bytes.AsSpan(read), read);
            if (count == 0) throw new InvalidDataException("Incomplete model manifest.");
            read += count;
        }
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Model removal requires the application-pinned manifest.");
        using var document = JsonDocument.Parse(bytes);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in document.RootElement.GetProperty("files").EnumerateObject())
        {
            WindowsFileHandles.RequireLeaf(entry.Name);
            string? hash = entry.Value.GetString();
            if (entry.Name == "manifest.json" || hash is not { Length: 64 } || hash.Any(c => !Uri.IsHexDigit(c)) || result.Count >= 64)
                throw new InvalidDataException("Invalid model asset removal manifest.");
            result.Add(entry.Name, hash);
        }
        if (result.Count == 0) throw new InvalidDataException("Empty model asset removal manifest.");
        return result;
    }
}
