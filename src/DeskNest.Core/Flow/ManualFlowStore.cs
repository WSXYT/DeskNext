using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DeskNest.Core.Storage;
using DeskNest.Core.Workspace;

namespace DeskNest.Core.Flow;

/// <summary>Core-owned disabled definitions, separate from workspace schema. Never creates a Flow runtime.</summary>
public sealed class ManualFlowStore(WorkspaceStore workspace, string? nativeLibraryPath = null)
{
    private const int MaximumCatalogBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 40 };
    private sealed record Catalog(int SchemaVersion, List<string> Definitions);
    private string StorePath => Path.Combine(workspace.DataDirectory, "flow-definitions.json");

    public async Task<IReadOnlyList<ManualFlowDefinition>> LoadAsync(CancellationToken token = default)
    {
        await workspace.OrganizationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            _ = workspace.Snapshot; // Also rejects use after the workspace owner has closed.
            return (await LoadCatalogAsync().ConfigureAwait(false)).Definitions.Select(ManualFlowDefinitions.Read).ToArray();
        }
        finally { workspace.OrganizationGate.Release(); }
    }

    public async Task<ManualFlowDefinition> SaveAsync(Guid selectedId, string json, CancellationToken token = default)
    {
        var incoming = ManualFlowDefinitions.Read(json);
        if (incoming.Id != selectedId) throw new InvalidDataException("The edited definition changed its selected Flow ID.");
        await workspace.OrganizationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            _ = workspace.Snapshot;
            var validation = await Task.Run(() => ManualFlowDefinitions.Validate(json, nativeLibraryPath), token).ConfigureAwait(false);
            if (validation == FlowDefinitionValidation.NativeUnavailable)
                throw new NotSupportedException("The native Flow validator is unavailable; no definition was saved.");
            if (validation != FlowDefinitionValidation.Valid) throw new InvalidDataException("The native Flow validator rejected this definition.");
            var catalog = await LoadCatalogAsync().ConfigureAwait(false);
            var existing = catalog.Definitions.Select(ManualFlowDefinitions.Read).SingleOrDefault(d => d.Id == incoming.Id);
            if (existing is null ? incoming.Revision != 0 : existing.Revision != incoming.Revision)
                throw new InvalidDataException("The Flow revision changed; reload before saving.");
            if (existing is null && catalog.Definitions.Count >= 100) throw new InvalidDataException("The Flow definition limit was reached.");
            var root = JsonNode.Parse(json)!;
            root["revision"] = checked(incoming.Revision + 1);
            string saved = root.ToJsonString(Json);
            var definitions = catalog.Definitions.Where(d => ManualFlowDefinitions.Read(d).Id != incoming.Id).Append(saved).ToList();
            string next = JsonSerializer.Serialize(new Catalog(1, definitions), Json);
            if (Encoding.UTF8.GetByteCount(next) > MaximumCatalogBytes) throw new InvalidDataException("Flow definitions exceed the storage budget.");
            token.ThrowIfCancellationRequested();
            CheckPaths();
            await ResilientJsonStore.SaveAsync(StorePath, next).ConfigureAwait(false);
            return ManualFlowDefinitions.Read(saved);
        }
        finally { workspace.OrganizationGate.Release(); }
    }

    private void CheckPaths()
    {
        foreach (string path in new[] { StorePath, StorePath + ".bak", StorePath + ".recovery-required" })
        {
            FileSystemVolume.RequireNoReparsePoints(path);
            if (Directory.Exists(path) || File.Exists(path) && new FileInfo(path).Length > MaximumCatalogBytes)
                throw new InvalidDataException("Flow storage is not a bounded regular file; preserve it for recovery.");
        }
        if (File.Exists(StorePath + ".recovery-required"))
            throw new InvalidDataException("Flow storage requires recovery; definitions will not be replaced.");
    }

    private async Task<Catalog> LoadCatalogAsync()
    {
        CheckPaths();
        Catalog Parse(string text)
        {
            var catalog = JsonSerializer.Deserialize<Catalog>(text, Json);
            if (catalog is null || catalog.SchemaVersion != 1 || catalog.Definitions is null || catalog.Definitions.Count > 100)
                throw new InvalidDataException("Invalid Flow catalog.");
            var ids = catalog.Definitions.Select(d => ManualFlowDefinitions.Read(d).Id).ToArray();
            if (ids.Distinct().Count() != ids.Length) throw new InvalidDataException("Duplicate Flow IDs.");
            return catalog;
        }
        var loaded = await ResilientJsonStore.LoadWithResultAsync(StorePath, Parse, () => new Catalog(1, []), "DeskNext Flow definitions").ConfigureAwait(false);
        if (loaded.Source == ResilientJsonLoadSource.DefaultAfterFailure)
        {
            await File.WriteAllTextAsync(StorePath + ".recovery-required", "Preserve quarantined Flow definitions; automatic reset is refused.").ConfigureAwait(false);
            throw new InvalidDataException("Flow definitions and backup were unreadable; recovery is required.");
        }
        return loaded.Value;
    }
}
