using DeskNest.Core.Workspace;

namespace DeskNest.App.ViewModels;

public sealed partial class StudioViewModel
{
    public string? CapsuleIconPath { get; private set; }

    internal async Task SaveCapsuleIconAsync(string? path)
    {
        var state = await _updateStore(s => s with { Settings = s.Settings with { CapsuleIconPath = path } });
        RefreshFromState(state);
    }

    internal async Task SaveFileViewAsync(Guid spaceId, SpaceFileView view)
    {
        var state = await _updateStore(s => s with
        {
            Spaces = s.Spaces.Select(space => space.Id == spaceId ? space with { FileView = view } : space).ToList()
        });
        RefreshFromState(state);
    }

    internal async Task SaveDisplayIconAsync(Guid id, bool space, string? path)
    {
        // Presentation metadata only: no desktop.ini, file association or filesystem change.
        var state = await _updateStore(s => space ? s with
        {
            Spaces = s.Spaces.Select(item => item.Id == id ? item with { CustomIconPath = path } : item).ToList()
        } : s with
        {
            Files = s.Files.Select(item => item.Id == id && !item.IsInTrash ? item with { CustomIconPath = path } : item).ToList()
        });
        RefreshFromState(state);
    }
}
