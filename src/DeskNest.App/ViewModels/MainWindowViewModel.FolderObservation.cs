using Avalonia.Threading;
using DeskNest.Core.Organization;
using DeskNest.Core.Workspace;
using DeskNest.Platform;

namespace DeskNest.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly SemaphoreSlim _observationGate = new(1, 1);
    private DesktopOrganizationMonitor? _folderMonitor;
    private CancellationTokenSource? _observationStop;
    private DispatcherTimer? _observationTimer;
    private int _observationEpoch;

    // Explicit, session-scoped observation only. No model calls or file operations.
    private async Task StartFolderObservationAsync(CancellationToken token)
    {
        await _observationGate.WaitAsync(token);
        try
        {
            if (_disposed || _store is null || StartupState != StartupState.Ready || _folderMonitor is not null) return;
            var store = _store;
            var snapshot = store.Snapshot;
            var excluded = ObservationExclusions(snapshot, store.DataDirectory);
            var roots = snapshot.Settings.MonitoredFolders.Select(FilePath)
                .Where(path => !excluded.Any(folder => ObservationContains(folder, path)))
                .Distinct(ObservationPathComparer).ToArray();
            if (roots.Length == 0) throw new InvalidOperationException(Localizer["Observation.NoFolders"]);
            foreach (var root in roots)
                if (!Directory.Exists(FilePath(root))) throw new DirectoryNotFoundException(root);
            int epoch = Interlocked.Increment(ref _observationEpoch);
            var stop = _observationStop = new CancellationTokenSource();
            var monitor = new DesktopOrganizationMonitor(new(roots, excluded),
                batch => ObserveCandidatesAsync(store, epoch, batch, stop.Token));
            _folderMonitor = monitor;
            try { await Task.Run(() => monitor.StartAsync(token), token); }
            catch
            {
                _folderMonitor = null;
                try { await monitor.DisposeAsync(); }
                finally { stop.Dispose(); _observationStop = null; }
                throw;
            }
            await SetUIStateAsync(() =>
            {
                if (Studio is null) return;
                Studio.IsFolderObservationActive = true;
                Studio.FolderObservationNotice = Localizer["Observation.Running"];
                _observationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _observationTimer.Tick += OnObservationTick;
                _observationTimer.Start();
            });
        }
        finally { _observationGate.Release(); }
    }

    private async Task ScanObservedFoldersAsync(CancellationToken token)
    {
        await _observationGate.WaitAsync(token);
        try
        {
            if (_disposed || _store is null || _manualCoordinator is null || StartupState != StartupState.Ready) return;
            var store = _store;
            var snapshot = store.Snapshot;
            var excluded = ObservationExclusions(snapshot, store.DataDirectory);
            var roots = snapshot.Settings.MonitoredFolders.Select(FilePath)
                .Where(path => !excluded.Any(folder => ObservationContains(folder, path))).Distinct(ObservationPathComparer).ToArray();
            if (roots.Length == 0) throw new InvalidOperationException(Localizer["Observation.NoFolders"]);
            var paths = await Task.Run(() => DesktopOrganizationMonitor.ScanPaths(roots, excluded, token), token);
            bool changed = await Task.Run(() => _manualCoordinator.RecordObservedPathsAsync(paths, token), token);
            if (changed) await SetUIStateAsync(() => { if (!_disposed) ApplySnapshot(store.Snapshot); });
        }
        finally { _observationGate.Release(); }
    }

    private async Task StopFolderObservationAsync()
    {
        Interlocked.Increment(ref _observationEpoch);
        await SetUIStateAsync(() =>
        {
            Studio?.StartFolderObservationCommand.Cancel();
            Studio?.ScanObservedFoldersCommand.Cancel();
        });
        await _observationGate.WaitAsync();
        try
        {
            var monitor = _folderMonitor;
            _folderMonitor = null;
            var stop = _observationStop;
            _observationStop = null;
            stop?.Cancel();
            Exception? failure = null;
            try { if (monitor is not null) await monitor.DisposeAsync(); }
            catch (Exception error) { failure = error; }
            finally
            {
                stop?.Dispose();
                await SetUIStateAsync(() =>
                {
                    if (_observationTimer is not null)
                    {
                        _observationTimer.Stop();
                        _observationTimer.Tick -= OnObservationTick;
                        _observationTimer = null;
                    }
                    if (Studio is not null)
                    {
                        Studio.IsFolderObservationActive = false;
                        Studio.FolderObservationNotice = failure is null ? Localizer["Observation.Stopped"] : Localizer.GetString("Files.ActionFailedNotice", failure.Message);
                    }
                });
            }
        }
        finally { _observationGate.Release(); }
    }

    private void OnObservationTick(object? sender, EventArgs args)
    {
        if (Studio is null || _folderMonitor is not { } monitor) return;
        Studio.FolderObservationNotice = monitor.Error is { } error
            ? Localizer.GetString("Files.ActionFailedNotice", error.Message)
            : Localizer[monitor.HasOverflowed ? "Observation.Overflow" : "Observation.Running"];
    }

    private async Task ObserveCandidatesAsync(WorkspaceStore store, int epoch,
        IReadOnlyList<DesktopOrganizationMonitorCandidate> candidates, CancellationToken token)
    {
        if (_disposed || epoch != Volatile.Read(ref _observationEpoch) || _manualCoordinator is null) return;
        bool changed;
        try { changed = await _manualCoordinator.RecordObservedPathsAsync(candidates.Select(item => item.Path).ToArray(), token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        if (changed) await SetUIStateAsync(() =>
        {
            if (!_disposed && epoch == Volatile.Read(ref _observationEpoch)) ApplySnapshot(store.Snapshot);
        });
    }

    private static readonly StringComparer ObservationPathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static string FilePath(string path) => Path.TrimEndingDirectorySeparator(PlatformFileActions.RequireExistingLocalPath(path));
    private static bool ObservationContains(string root, string path) => ObservationPathComparer.Equals(root, path) ||
        path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static string[] ObservationExclusions(WorkspaceState state, string dataDirectory) =>
        state.Settings.ExcludedFolders.Concat(state.Spaces.Select(space => space.Folder))
            .Concat([dataDirectory, state.Settings.ManagedRoot, state.Settings.ModelCacheDirectory ?? state.Settings.ManagedRoot])
            .Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))).Distinct(ObservationPathComparer).ToArray();
}
