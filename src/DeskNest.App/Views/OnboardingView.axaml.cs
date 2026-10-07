using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DeskNest.App.ViewModels;
using DeskNest.Platform;
using System;
using System.IO;

namespace DeskNest.App.Views;

public partial class OnboardingView : UserControl
{
    public OnboardingView()
    {
        InitializeComponent();
    }

    private async void OnModelPackageClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not OnboardingViewModel { IsStep2: true, IsLayaSelected: true, ModelDeployment: { } deployment } wizard ||
            sender is not Button button || deployment.InstallLocalModelPackageCommand.IsRunning) return;
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        bool folder = button.Tag as string == "folder";
        if (storage is null || (folder ? !storage.CanPickFolder : !storage.CanOpen))
        {
            deployment.ModelInstallNotice = wizard.Localizer["Spaces.FolderPickerUnavailable"];
            return;
        }
        try
        {
            button.IsEnabled = false;
            if (folder)
            {
                var selected = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
                { Title = wizard.Localizer["Classification.InstallLocation"], AllowMultiple = false });
                if (DataContext != wizard || !wizard.IsStep2 || !wizard.IsLayaSelected || selected.Count == 0) return;
                string path = PlatformFileActions.RequireExistingLocalPath(selected[0].TryGetLocalPath() ?? string.Empty);
                if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);
                deployment.ModelInstallRoot = path;
            }
            else
            {
                var selected = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = wizard.Localizer["Classification.ImportPackage"], AllowMultiple = false,
                    FileTypeFilter = [new FilePickerFileType("ZIP") { Patterns = ["*.zip"] }]
                });
                if (DataContext != wizard || !wizard.IsStep2 || !wizard.IsLayaSelected || selected.Count == 0) return;
                string path = PlatformFileActions.RequireExistingLocalPath(selected[0].TryGetLocalPath() ?? string.Empty);
                await deployment.InstallLocalModelPackageCommand.ExecuteAsync(path);
            }
        }
        catch (Exception error) { deployment.ModelInstallNotice = wizard.Localizer.GetString("Files.ActionFailedNotice", error.Message); }
        finally { button.IsEnabled = true; }
    }
}
