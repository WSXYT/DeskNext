using Avalonia.Controls;
using DeskNest.App.ViewModels;
using DeskNest.App.Views;
using DeskNest.Core.Workspace;

namespace DeskNest.App.Services;

// Existing smoke runner only: real filesystem notifications, isolated folders, no file operations.
internal static class FolderObservationSmoke
{
    internal static async Task VerifyAsync(string root)
    {
        var first = Directory.CreateDirectory(Path.Combine(root, "first")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(root, "second")).FullName;
        var ignored = Directory.CreateDirectory(Path.Combine(first, "ignored")).FullName;
        string model = Directory.CreateDirectory(Path.Combine(first, "laya-multilingual-fp32-" + Guid.NewGuid().ToString("N"))).FullName;
        string modelCache = Directory.CreateDirectory(Path.Combine(first, WorkspaceSettings.ModelDownloadCacheFolderName)).FullName;
        string existing = Path.Combine(first, "existing.txt");
        File.WriteAllText(existing, "baseline");
        string oldFolder = Directory.CreateDirectory(Path.Combine(first, "old-folder")).FullName;
        File.WriteAllText(Path.Combine(oldFolder, "child.txt"), "scanned in place");
        var target = new WorkspaceSpace(Guid.NewGuid(), "Observed", "", SpaceStorageMode.Managed, Path.Combine(root, "managed", "Observed"));
        await using var store = await WorkspaceStore.OpenAsync(Path.Combine(root, "store"));
        await store.UpdateAsync(state => state with
        {
            OnboardingComplete = true, OnboardingStep = 5, Spaces = [target],
            Settings = state.Settings with
            {
                ManagedRoot = Path.Combine(root, "managed"),
                MonitoredFolders = [first, second], ExcludedFolders = [ignored], ModelCacheDirectory = model
            }
        });
        await using var vm = new MainWindowViewModel(store);
        var studio = vm.Studio!;
        studio.SelectedTabIndex = 3;
        var view = new StudioView { DataContext = studio };
        var window = new Window { Content = view, Width = 1280, Height = 720 };
        window.Show();
        try
        {
            var start = view.FindControl<Button>("StartFolderObservationButton");
            if (start?.Command != studio.StartFolderObservationCommand || !start.IsEffectivelyVisible)
                throw new InvalidOperationException("Folder observation must have a real settings entry.");
            var sourcePicker = view.FindControl<ComboBox>("ObservationSourcePicker")!;
            var targetPicker = view.FindControl<ComboBox>("ObservationTargetPicker")!;
            var bind = view.FindControl<Button>("BindObservationTargetButton");
            if (bind?.Command != studio.BindObservationTargetCommand || !bind.IsEffectivelyVisible)
                throw new InvalidOperationException("Source suggestions need a visible settings entry.");
            sourcePicker.SelectedItem = first;
            targetPicker.SelectedItem = studio.AllSpaces.Single();
            await studio.BindObservationTargetCommand.ExecuteAsync(null);
            using (var saved = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(store.DataDirectory, "workspace.json"))))
                if (saved.RootElement.GetProperty("settings").GetProperty("monitoredFolderTargets").GetProperty(first).GetGuid() != target.Id)
                    throw new InvalidOperationException("Source suggestions must be persisted.");
            long revision = store.Snapshot.Revision;
            await studio.StartFolderObservationCommand.ExecuteAsync(null);
            if (!studio.IsFolderObservationActive || store.Snapshot.Revision != revision)
                throw new InvalidOperationException("Starting observation must only establish a baseline: " + studio.FolderObservationNotice);
            File.WriteAllText(existing, "baseline changed");
            File.WriteAllText(Path.Combine(ignored, "skip.txt"), "excluded");
            string cacheFile = Path.Combine(modelCache, "cached.partial");
            File.WriteAllText(cacheFile, "model-cache-not-a-review-item");
            if ((store.Snapshot.Settings with { ModelCacheDirectory = model + Path.DirectorySeparatorChar }).GetModelDownloadCacheDirectory() != modelCache)
                throw new InvalidOperationException("An activated model must retain its derived download-cache location.");
            var coordinator = new ManualOrganizationCoordinator(store, new DeskNest.Core.Storage.DesktopOrganizationTransaction(
                Path.Combine(store.DataDirectory, "organization-recovery.json")));
            if (await coordinator.RecordObservedPathsAsync([cacheFile]) || store.Snapshot.Revision != revision)
                throw new InvalidOperationException("The Core observation boundary accepted a model download cache.");
            string[] fresh = [Path.Combine(first, "new.txt"), Path.Combine(second, "other.txt")];
            foreach (var path in fresh)
            {
                string staging = Path.Combine(root, Guid.NewGuid() + ".txt");
                File.WriteAllText(staging, "stays in place");
                File.Move(staging, path);
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            while (!fresh.All(path => store.Snapshot.Pending.Any(item => item.Path == path)))
                await Task.Delay(25, timeout.Token);
            await studio.StopFolderObservationCommand.ExecuteAsync(null);
            if (studio.IsFolderObservationActive || store.Snapshot.Pending.Count != 2 ||
                store.Snapshot.Files.Count != 0 || store.Snapshot.Operations.Count != 0 ||
                fresh.Any(path => File.ReadAllText(path) != "stays in place") ||
                store.Snapshot.Pending.Single(item => item.Path == fresh[0]).SuggestedSpaceId != target.Id ||
                store.Snapshot.Pending.Single(item => item.Path == fresh[1]).SuggestedSpaceId is not null || studio.IsImportConfirmationOpen)
                throw new InvalidOperationException("Observation must add only new, non-excluded review records without moving files.");
            revision = store.Snapshot.Revision;
            File.WriteAllText(Path.Combine(first, "after-stop.txt"), "untouched");
            await Task.Delay(50);
            if (store.Snapshot.Revision != revision) throw new InvalidOperationException("A stopped observer accepted a late event.");
            var scan = view.FindControl<Button>("ScanObservedFoldersButton");
            if (scan?.Command != studio.ScanObservedFoldersCommand || !scan.IsEffectivelyVisible)
                throw new InvalidOperationException("Manual scanning must have a real settings entry.");
            await studio.ScanObservedFoldersCommand.ExecuteAsync(null);
            var scanned = store.Snapshot;
            if (studio.FolderScanNotice != studio.Localizer["Observation.ScanComplete"] || studio.IsFolderObservationActive ||
                scanned.Revision != revision + 1 || scanned.Pending.Count != 5 || scanned.Operations.Count != 0 || scanned.Files.Count != 0 ||
                !scanned.Pending.Any(item => item.Path == existing) || !scanned.Pending.Any(item => item.Path == oldFolder) ||
                scanned.Pending.Any(item => item.Path.StartsWith(ignored + Path.DirectorySeparatorChar)) ||
                File.ReadAllText(existing) != "baseline changed" || File.ReadAllText(Path.Combine(oldFolder, "child.txt")) != "scanned in place")
                throw new InvalidOperationException("Scanning must find existing items once, respect exclusions and preserve files: " + studio.FolderScanNotice);
            await studio.ScanObservedFoldersCommand.ExecuteAsync(null);
            if (store.Snapshot.Revision != scanned.Revision) throw new InvalidOperationException("Repeated scans must not duplicate review entries.");
            await studio.ClearObservationTargetCommand.ExecuteAsync(null);
            if (store.Snapshot.Settings.MonitoredFolderTargets.Count != 0 ||
                store.Snapshot.Pending.Single(item => item.Path == fresh[0]).SuggestedSpaceId != target.Id)
                throw new InvalidOperationException("Clearing a source preset must not rewrite existing pending choices.");
            // A selected nested source must not leak events through its still-running parent when paused.
            string nested = Directory.CreateDirectory(Path.Combine(first, "nested-source")).FullName;
            await vm.UpdateStoreAsync(state => state with { Settings = state.Settings with { MonitoredFolders = [first, second, nested] } });
            await studio.StartFolderObservationCommand.ExecuteAsync(null);
            if (!studio.IsFolderObservationActive) throw new InvalidOperationException(studio.FolderObservationNotice);
            sourcePicker.SelectedItem = nested;
            var pause = view.FindControl<Button>("PauseObservationSourceButton")!;
            var resume = view.FindControl<Button>("ResumeObservationSourceButton")!;
            if (pause.Command != studio.PauseObservationSourceCommand || resume.Command != studio.ResumeObservationSourceCommand || !pause.IsEnabled)
                throw new InvalidOperationException("Per-source controls must use the selected source and live capability state.");
            revision = store.Snapshot.Revision;
            await studio.PauseObservationSourceCommand.ExecuteAsync(null);
            if (studio.CanPauseObservationSource || !studio.CanResumeObservationSource ||
                studio.ObservationSourceStatus != studio.Localizer["Observation.Paused"] || store.Snapshot.Revision != revision)
                throw new InvalidOperationException("Pausing a source must stop only its runtime observer without saving metadata.");
            string duringPause = Path.Combine(nested, "during-pause.txt");
            File.WriteAllText(duringPause, "keep for explicit scan");
            string liveOther = Path.Combine(second, "still-observed.txt");
            string stagedOther = Path.Combine(root, "staged-other.txt");
            File.WriteAllText(stagedOther, "another source stays live");
            File.Move(stagedOther, liveOther);
            using var pauseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            while (!store.Snapshot.Pending.Any(item => item.Path == liveOther)) await Task.Delay(25, pauseTimeout.Token);
            if (store.Snapshot.Pending.Any(item => item.Path == duringPause) || !studio.IsFolderObservationActive)
                throw new InvalidOperationException("A paused nested source leaked through its parent or stopped another source.");
            revision = store.Snapshot.Revision;
            await studio.ResumeObservationSourceCommand.ExecuteAsync(null);
            if (!studio.CanPauseObservationSource || studio.CanResumeObservationSource ||
                studio.ObservationSourceStatus != studio.Localizer["Observation.Resumed"] || store.Snapshot.Revision != revision)
                throw new InvalidOperationException("Resume must establish a new baseline and report the need for explicit scanning.");
            string afterResume = Path.Combine(nested, "after-resume.txt");
            string stagedResume = Path.Combine(root, "staged-resume.txt");
            File.WriteAllText(stagedResume, "resumed source stays in place");
            File.Move(stagedResume, afterResume);
            while (!store.Snapshot.Pending.Any(item => item.Path == afterResume)) await Task.Delay(25, pauseTimeout.Token);
            if (store.Snapshot.Pending.Any(item => item.Path == duringPause) || store.Snapshot.Operations.Count != 0 ||
                File.ReadAllText(duringPause) != "keep for explicit scan" || File.ReadAllText(afterResume) != "resumed source stays in place")
                throw new InvalidOperationException("Resuming must not claim missed-event repair or move any source.");
            Directory.Move(second, second + "-offline");
            sourcePicker.SelectedItem = second;
            if (studio.ObservationSourceStatus != studio.Localizer["Validation.FileNotFound"] || !studio.CanResumeObservationSource)
                throw new InvalidOperationException("An unavailable source must have visible status and a retry entry.");
            Directory.Move(second + "-offline", second);
            await studio.ResumeObservationSourceCommand.ExecuteAsync(null);
            if (studio.ObservationSourceStatus != studio.Localizer["Observation.Resumed"])
                throw new InvalidOperationException("A returned source must support an explicit restart.");
            await vm.DisposeAsync();
            if (studio.IsFolderObservationActive) throw new InvalidOperationException("Workbench disposal must stop its observer.");
        }
        finally { window.Close(); }
    }
}
