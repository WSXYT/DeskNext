using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DeskNest.App.ViewModels;

public partial class StudioViewModel
{
    // Null package is the explicit online-download button; a non-null value is an offline ZIP path.
    public Func<string?, IProgress<int>, CancellationToken, Task<string>>? OnInstallLocalModelPackage { get; set; }
    [ObservableProperty] private int _modelInstallProgress;
    [ObservableProperty] private string _modelInstallNotice = string.Empty;

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task InstallLocalModelPackageAsync(string? package, CancellationToken token)
    {
        if (!IsLayaPreview || package is not null && string.IsNullOrWhiteSpace(package) || OnInstallLocalModelPackage is null) return;
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
