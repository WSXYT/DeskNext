using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using DeskNest.Core.Workspace;

namespace DeskNest.App.Services;

/// <summary>
/// Serializable payload carried across the Avalonia clipboard bridge.
/// Preserves fully-qualified paths, cut vs copy marker, and optional workspace identity.
/// </summary>
public sealed class WorkspaceClipboardPayload
{
    public List<string> Paths { get; set; } = new();
    public bool IsCut { get; set; }
    public Guid? SourceFileId { get; set; }
    public Guid? SourceSpaceId { get; set; }
}

/// <summary>
/// Production Avalonia 12 clipboard bridge using modern IClipboard and DataTransfer APIs.
/// Correctly disposes IAsyncDataTransfer returned by TryGetDataAsync to release native system handles.
/// </summary>
public static class AvaloniaClipboardBridge
{
    internal const int MaximumPayloadCharacters = 65_536;
    private static readonly JsonSerializerOptions PayloadOptions = new()
    {
        MaxDepth = 8,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    internal static WorkspaceClipboardPayload? ParsePayload(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaximumPayloadCharacters) return null;
        try
        {
            var payload = JsonSerializer.Deserialize<WorkspaceClipboardPayload>(json, PayloadOptions);
            return payload?.Paths is { Count: 1 } && IsSupportedPath(payload.Paths[0]) ? payload : null;
        }
        catch (JsonException) { return null; }
    }

    private static bool IsSupportedPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path.Length <= 4096 && !path.Any(char.IsControl) &&
        Path.IsPathFullyQualified(path);

    public static readonly DataFormat<string> WorkspaceFileFormat =
        DataFormat.CreateStringApplicationFormat("desknest.file-transfer");

    internal static WorkspaceFile? ResolveCutSource(WorkspaceClipboardPayload payload, WorkspaceState snapshot)
    {
        // The clipboard is untrusted: identifiers, path and source space must name the same item.
        if (!payload.IsCut || payload.SourceFileId is null || payload.SourceSpaceId is null ||
            payload.Paths is not { Count: 1 } || !IsSupportedPath(payload.Paths[0])) return null;
        string path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(payload.Paths[0]));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return snapshot.Files.SingleOrDefault(file => !file.IsInTrash && file.Id == payload.SourceFileId &&
            file.SpaceId == payload.SourceSpaceId && string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(file.Path)), path, comparison));
    }

    public static async Task SetFilePayloadAsync(IClipboard clipboard, WorkspaceClipboardPayload payload)
    {
        ArgumentNullException.ThrowIfNull(clipboard);
        ArgumentNullException.ThrowIfNull(payload);

        if (payload.Paths is not { Count: 1 } || !IsSupportedPath(payload.Paths[0]))
        {
            throw new ArgumentException("Clipboard payload must contain exactly one valid local path.", nameof(payload));
        }

        var dataTransfer = new DataTransfer();
        var json = JsonSerializer.Serialize(payload);
        var item = new DataTransferItem();
        item.Set(WorkspaceFileFormat, json);
        item.SetText(payload.Paths[0]);

        dataTransfer.Add(item);
        await clipboard.SetDataAsync(dataTransfer).ConfigureAwait(false);

        try
        {
            await clipboard.FlushAsync().ConfigureAwait(false);
        }
        catch
        {
            // Flush is platform-dependent (Windows only); safely ignored if unsupported.
        }
    }

    public static async Task<WorkspaceClipboardPayload?> TryGetFilePayloadAsync(IClipboard clipboard)
    {
        ArgumentNullException.ThrowIfNull(clipboard);

        IAsyncDataTransfer? dataTransfer = null;
        try
        {
            dataTransfer = await clipboard.TryGetDataAsync().ConfigureAwait(false);
            if (dataTransfer is null)
                return null;

            if (dataTransfer.Contains(WorkspaceFileFormat))
            {
                var json = await dataTransfer.TryGetValueAsync(WorkspaceFileFormat).ConfigureAwait(false);
                // A malformed application marker is not permission to reinterpret fallback data.
                return ParsePayload(json);
            }

            // Fallback: check platform file transfer items (e.g. from system file manager)
            var storageFiles = await dataTransfer.TryGetFilesAsync().ConfigureAwait(false);
            if (storageFiles is not null)
            {
                var paths = new List<string>();
                // Two paths suffice to reject multi-item input; never enumerate an unbounded list here.
                foreach (var file in storageFiles.Take(2))
                {
                    var localPath = file.TryGetLocalPath();
                    if (!string.IsNullOrWhiteSpace(localPath))
                    {
                        paths.Add(localPath);
                    }
                    else if (file.Path is { } uri && uri.IsFile)
                    {
                        paths.Add(uri.LocalPath);
                    }
                    else if (file.Path is { } remoteUri)
                    {
                        paths.Add(remoteUri.ToString());
                    }
                }

                if (paths.Count > 0)
                {
                    return new WorkspaceClipboardPayload
                    {
                        Paths = paths,
                        IsCut = false
                    };
                }
            }

            // Fallback: check plain text
            var text = await dataTransfer.TryGetTextAsync().ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(text) && text.Length <= MaximumPayloadCharacters)
            {
                var lines = text.Split(new[] { "\r\n", "\r", "\n" }, 3, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (lines.Length > 0)
                {
                    return new WorkspaceClipboardPayload
                    {
                        Paths = lines.ToList(),
                        IsCut = false
                    };
                }
            }

            return null;
        }
        finally
        {
            if (dataTransfer is IDisposable disposable)
            {
                disposable.Dispose();
            }
            else if (dataTransfer is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
