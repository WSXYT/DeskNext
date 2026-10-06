using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DeskNest.App.ViewModels;

public partial class StudioViewModel
{
    // Download and repair both prepare a fresh verified directory. Non-null input selects an offline ZIP.
    public Func<string?, IProgress<int>, CancellationToken, Task<string>>? OnInstallLocalModelPackage { get; set; }
    [ObservableProperty] private string _modelInstallRoot = string.Empty;
    partial void OnModelInstallRootChanged(string value)
    {
        InstallLocalModelPackageCommand.Cancel();
        ModelInstallNotice = string.Empty;
    }

    public Func<Task<DeskNest.Core.Workspace.ModelAssetRemovalPlan>>? OnPrepareModelRemoval { get; set; }
    public Func<DeskNest.Core.Workspace.ModelAssetRemovalPlan, Task<int>>? OnRemoveModelData { get; set; }
    [ObservableProperty] private bool _isModelRemovalOpen;
    [ObservableProperty] private bool _isModelRemovalBusy;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModelRemovalDirectory), nameof(ModelRemovalFiles))]
    private DeskNest.Core.Workspace.ModelAssetRemovalPlan? _modelRemovalPlan;
    public string ModelRemovalDirectory => ModelRemovalPlan?.DirectoryPath ?? string.Empty;
    public string ModelRemovalFiles => string.Join(Environment.NewLine, ModelRemovalPlan?.Files ?? Array.Empty<string>());
    [ObservableProperty] private string? _modelRemovalError;

    [RelayCommand]
    private async Task OpenModelRemovalAsync()
    {
        if (IsModelRemovalBusy || IsModelRemovalOpen) return;
        if (OnPrepareModelRemoval is null) { ModelInstallNotice = Localizer["Classification.ModelRemovalUnavailable"]; return; }
        IsModelRemovalBusy = true;
        ModelInstallNotice = Localizer["Classification.CheckingRemoval"];
        try
        {
            ModelRemovalPlan = await OnPrepareModelRemoval();
            ModelRemovalError = null;
            IsModelRemovalOpen = true;
            ModelInstallNotice = string.Empty;
        }
        catch (Exception error) { ModelInstallNotice = Localizer.GetString("Files.ActionFailedNotice", error.Message); }
        finally { IsModelRemovalBusy = false; }
    }

    [RelayCommand]
    public void CloseModelRemoval()
    {
        if (IsModelRemovalBusy) return;
        IsModelRemovalOpen = false;
        ModelRemovalPlan = null;
        ModelRemovalError = null;
    }

    [RelayCommand]
    private async Task ConfirmModelRemovalAsync()
    {
        if (!IsModelRemovalOpen || IsModelRemovalBusy || ModelRemovalPlan is null || OnRemoveModelData is null) return;
        IsModelRemovalBusy = true;
        ModelRemovalError = null;
        try
        {
            int count = await OnRemoveModelData(ModelRemovalPlan);
            SettingsModelCache = string.Empty;
            IsModelRemovalBusy = false;
            CloseModelRemoval();
            ModelInstallNotice = Localizer.GetString("Classification.ModelRemoved", count);
        }
        catch (Exception error) { ModelRemovalError = Localizer.GetString("Files.ActionFailedNotice", error.Message); }
        finally { IsModelRemovalBusy = false; }
    }

    [ObservableProperty] private int _modelInstallProgress;
    [ObservableProperty] private string _modelInstallNotice = string.Empty;

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task InstallLocalModelPackageAsync(string? package, CancellationToken token)
    {
        if (IsModelRemovalOpen || IsModelRemovalBusy || !IsLayaPreview || package is not null && string.IsNullOrWhiteSpace(package) || OnInstallLocalModelPackage is null) return;
        string selected = SettingsModelCache;
        ModelInstallProgress = 0;
        ModelInstallNotice = Localizer[package is null ? "Classification.DownloadingPackage" : "Classification.InstallingPackage"];
        try
        {
            var progress = new Progress<int>(value =>
            {
                if (!token.IsCancellationRequested) ModelInstallProgress = Math.Min(value, 99); // 100% includes the worker loading check.
            });
            string installed = await OnInstallLocalModelPackage(package, progress, token);
            token.ThrowIfCancellationRequested();
            if (IsLayaPreview && selected == SettingsModelCache)
            {
                ModelInstallProgress = 100;
                SettingsModelCache = installed;
                ModelInstallNotice = Localizer["Classification.PackageInstalled"];
            }
        }
        catch (OperationCanceledException)
        {
            if (IsLayaPreview && selected == SettingsModelCache)
                ModelInstallNotice = Localizer["Classification.Cancelled"];
        }
        catch (Exception error)
        {
            if (IsLayaPreview && selected == SettingsModelCache)
                ModelInstallNotice = Localizer.GetString("Files.ActionFailedNotice", error.Message);
        }
    }
}
