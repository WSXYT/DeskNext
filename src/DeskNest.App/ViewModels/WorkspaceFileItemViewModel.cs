using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DeskNest.App.ViewModels;

public sealed partial class WorkspaceFileItemViewModel : ViewModelBase
{
    [ObservableProperty]
    private Guid _id;

    [ObservableProperty]
    private Guid _spaceId;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _path = string.Empty;

    [ObservableProperty]
    private bool _isDirectory;

    public string Icon => string.Empty;
    public string TypeLabel => IsDirectory ? "Directory" : "File";

    public WorkspaceFileItemViewModel(Guid id, Guid spaceId, string name, string path, bool isDirectory)
    {
        _id = id;
        _spaceId = spaceId;
        _name = name;
        _path = path;
        _isDirectory = isDirectory;
    }
}
