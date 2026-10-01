using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskNest.App.Localization;

namespace DeskNest.App.ViewModels;

public sealed partial class DropCapsuleViewModel : ViewModelBase, IDisposable
{
    private readonly StudioViewModel? _studio;
    private bool _disposed;

    public LocalizationManager Localizer => LocalizationManager.Instance;

    public FlowDirection CurrentFlowDirection => Localizer.FlowDirectionValue;

    [ObservableProperty]
    private string _capsuleInputPath = string.Empty;

    [ObservableProperty]
    private string? _capsuleNotice;

    public bool HasCapsuleNotice => !string.IsNullOrWhiteSpace(CapsuleNotice);

    partial void OnCapsuleNoticeChanged(string? value)
    {
        OnPropertyChanged(nameof(HasCapsuleNotice));
    }

    [ObservableProperty]
    private bool _isDragOverCapsule;

    public Func<IReadOnlyList<string>, Task>? OnFilesDroppedOnCapsule { get; set; }
    public Func<string, Task>? OnPathSubmitted { get; set; }

    public DropCapsuleViewModel()
    {
        Localizer.PropertyChanged += OnLocalizerChanged;
    }

    public DropCapsuleViewModel(StudioViewModel studio) : this()
    {
        _studio = studio ?? throw new ArgumentNullException(nameof(studio));
        _capsuleInputPath = studio.CapsuleInputPath;
        _capsuleNotice = studio.CapsuleNotice;
        _isDragOverCapsule = studio.IsDragOverCapsule;

        studio.PropertyChanged += OnStudioChanged;
    }

    public DropCapsuleViewModel(Func<IReadOnlyList<string>, Task>? onFilesDropped, Func<string, Task>? onPathSubmitted = null) : this()
    {
        OnFilesDroppedOnCapsule = onFilesDropped;
        OnPathSubmitted = onPathSubmitted;
    }

    [RelayCommand]
    public async Task DropPathsOnCapsuleAsync(IReadOnlyList<string>? paths)
    {
        if (_disposed) return;
        if (paths == null || paths.Count == 0)
        {
            CapsuleNotice = Localizer["Drop.UnsupportedPayload"];
            return;
        }

        if (_studio != null)
        {
            await _studio.DropPathsOnCapsuleAsync(paths);
            CapsuleNotice = _studio.CapsuleNotice;
            return;
        }

        if (OnFilesDroppedOnCapsule != null)
        {
            await OnFilesDroppedOnCapsule(paths);
            return;
        }

        int valid = 0;
        foreach (var p in paths)
        {
            if (Path.IsPathFullyQualified(p) && (File.Exists(p) || Directory.Exists(p)))
            {
                valid++;
            }
        }

        if (valid == 0)
        {
            CapsuleNotice = Localizer["Validation.FileNotFound"];
        }
        else
        {
            CapsuleNotice = Localizer["Triage.P2Notice"];
        }
    }

    [RelayCommand]
    public async Task SubmitCapsuleAsync()
    {
        if (_disposed) return;
        if (string.IsNullOrWhiteSpace(CapsuleInputPath))
        {
            CapsuleNotice = Localizer["Validation.ValidAbsolutePathRequired"];
            return;
        }

        if (_studio != null)
        {
            _studio.CapsuleInputPath = CapsuleInputPath;
            await _studio.SubmitCapsuleAsync();
            CapsuleNotice = _studio.CapsuleNotice;
            CapsuleInputPath = _studio.CapsuleInputPath;
            return;
        }

        if (OnPathSubmitted != null)
        {
            await OnPathSubmitted(CapsuleInputPath.Trim());
            CapsuleInputPath = string.Empty;
            return;
        }

        var path = CapsuleInputPath.Trim();
        if (!Path.IsPathFullyQualified(path))
        {
            CapsuleNotice = Localizer["Validation.ValidAbsolutePathRequired"];
            return;
        }

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            CapsuleNotice = Localizer["Validation.FileNotFound"];
            return;
        }

        CapsuleNotice = Localizer["Triage.P2Notice"];
        CapsuleInputPath = string.Empty;
    }

    private void OnLocalizerChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(CurrentFlowDirection));
        OnPropertyChanged(nameof(Localizer));
    }

    private void OnStudioChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StudioViewModel.CapsuleNotice)) CapsuleNotice = _studio!.CapsuleNotice;
        else if (e.PropertyName == nameof(StudioViewModel.CapsuleInputPath)) CapsuleInputPath = _studio!.CapsuleInputPath;
        else if (e.PropertyName == nameof(StudioViewModel.IsDragOverCapsule)) IsDragOverCapsule = _studio!.IsDragOverCapsule;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Localizer.PropertyChanged -= OnLocalizerChanged;
        if (_studio is not null) _studio.PropertyChanged -= OnStudioChanged;
        OnFilesDroppedOnCapsule = null;
        OnPathSubmitted = null;
    }
}
