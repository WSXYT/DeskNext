using Avalonia.Threading;
using DeskNest.Core.Organization;
using DeskNest.Core.Workspace;
using DeskNest.Platform;

namespace DeskNest.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly SemaphoreSlim _observationGate = new(1, 1);
    private Dictionary<string, ObservationSession> _observationSessions = new(ObservationPathComparer);
    private DispatcherTimer? _observationTimer;
    private int _observationEpoch;

    // Reuse the existing monitor per source: pausing disposes that source's watcher and drains its callback.
    // Session-only state. Resuming establishes a new baseline, not a repair of missed events.
    private sealed class ObservationSession(string root, string[] exclusions)
    {
        public string Root { get; } = root;
        public string[] Exclusions { get; } = exclusions;
        public DesktopOrganizationMonitor? Monitor { get; set; }
        public CancellationTokenSource? Stop { get; set; }
        public Exception? Error { get; set; }
        public bool NeedsScan { get; set; }
    }

    private async Task StartFolderObservationAsync(CancellationToken token)
    {
        await _observationGate.WaitAsync(token);
        try
        {
            if (_disposed || _store is null || StartupState != StartupState.Ready || _observationSessions.Count != 0) return;
            var snapshot = _store.Snapshot;
            var excluded = ObservationExclusions(snapshot, _store.DataDirectory);
            var roots = snapshot.Settings.MonitoredFolders.Select(NormalizeObservationPath)
                .Where(path => !excluded.Any(folder => ObservationContains(folder, path)))
                .Distinct(ObservationPathComparer).ToArray();
            if (roots.Length == 0) throw new InvalidOperationException(Localizer["Observation.NoFolders"]);
            int epoch = Interlocked.Increment(ref _observationEpoch);
            // A nested selected source owns its subtree, even while paused. Its parent must not bypass pause.
            _observationSessions = roots.ToDictionary(root => root, root => new ObservationSession(root,
                excluded.Concat(roots.Where(child => !ObservationPathComparer.Equals(root, child) && ObservationContains(root, child))).ToArray()),
                ObservationPathComparer);
            try
            {
                foreach (var session in _observationSessions.Values)
                    await StartObservationSourceAsync(session, epoch, token);
            }
            catch
            {
                var sessions = _observationSessions;
                _observationSessions = new(ObservationPathComparer);
                foreach (var session in sessions.Values) await StopObservationSourceAsync(session);
                throw;
            }
            await SetUIStateAsync(() =>
            {
                if (Studio is null) return;
                Studio.IsFolderObservationActive = true;
                _observationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _observationTimer.Tick += OnObservationTick;
                _observationTimer.Start();
                RefreshObservationStatus();
            });
        }
        finally { _observationGate.Release(); }
    }

    private async Task StartObservationSourceAsync(ObservationSession session, int epoch, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        session.Error = null;
        try
        {
            if (!Directory.Exists(FilePath(session.Root))) throw new DirectoryNotFoundException(session.Root);
            var stop = session.Stop = new CancellationTokenSource();
            var monitor = session.Monitor = new DesktopOrganizationMonitor(new([session.Root], session.Exclusions),
                batch => ObserveCandidatesAsync(_store!, epoch, batch, stop.Token));
            await Task.Run(() => monitor.StartAsync(token), token);
        }
        catch (Exception error)
        {
            await StopObservationSourceAsync(session);
            session.Error = error;
            if (error is OperationCanceledException) throw;
            // A failed source remains visible and can be resumed; other sources keep running.
        }
    }

    private static async Task StopObservationSourceAsync(ObservationSession session)
    {
        var monitor = session.Monitor;
        session.Monitor = null;
        var stop = session.Stop;
        session.Stop = null;
        stop?.Cancel();
        try { if (monitor is not null) await monitor.DisposeAsync(); }
        catch (Exception error) { session.Error = error; }
        finally { stop?.Dispose(); }
    }

    private async Task SetObservationSourcePausedAsync(string root, bool paused, CancellationToken token)
    {
        await _observationGate.WaitAsync(token);
        try
        {
            if (_disposed || !_observationSessions.TryGetValue(NormalizeObservationPath(root), out var session)) return;
            await StopObservationSourceAsync(session);
            session.NeedsScan = true;
            if (!paused) await StartObservationSourceAsync(session, Volatile.Read(ref _observationEpoch), token);
            await SetUIStateAsync(RefreshObservationStatus);
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
            Studio?.ResumeObservationSourceCommand.Cancel();
        });
        await _observationGate.WaitAsync();
        try
        {
            var sessions = _observationSessions;
            _observationSessions = new(ObservationPathComparer);
            foreach (var session in sessions.Values) await StopObservationSourceAsync(session);
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
                    Studio.FolderObservationNotice = sessions.Values.FirstOrDefault(item => item.Error is not null)?.Error is { } failure
                        ? Localizer.GetString("Files.ActionFailedNotice", failure.Message) : Localizer["Observation.Stopped"];
                    RefreshObservationStatus();
                }
            });
        }
        finally { _observationGate.Release(); }
    }

    private void OnObservationTick(object? sender, EventArgs args) => RefreshObservationStatus();

    private void RefreshObservationStatus()
    {
        if (Studio is null) return;
        var sessions = _observationSessions;
        if (sessions.Count != 0)
            Studio.FolderObservationNotice = Localizer.GetString("Observation.ActiveCount",
                sessions.Values.Count(item => item.Monitor?.IsRunning == true && Directory.Exists(item.Root)), sessions.Count);
        ObservationSession? session = null;
        if (Studio.ObservationSourcePath is { } root) sessions.TryGetValue(NormalizeObservationPath(root), out session);
        Studio.CanPauseObservationSource = session?.Monitor?.IsRunning == true;
        Studio.CanResumeObservationSource = session is not null &&
            (session.Monitor?.IsRunning != true || session.Monitor.HasOverflowed || !Directory.Exists(session.Root));
        Studio.ObservationSourceStatus = session is null ? Localizer["Observation.Stopped"]
            : (session.Monitor?.Error ?? session.Error) is { } error ? Localizer.GetString("Files.ActionFailedNotice", error.Message)
            : !Directory.Exists(session.Root) ? Localizer["Validation.FileNotFound"]
            : session.Monitor is null ? Localizer["Observation.Paused"]
            : Localizer[session.Monitor.HasOverflowed ? "Observation.Overflow" : session.NeedsScan ? "Observation.Resumed" : "Observation.Running"];
    }

    private async Task ObserveCandidatesAsync(WorkspaceStore store, int epoch,
        IReadOnlyList<DesktopOrganizationMonitorCandidate> candidates, CancellationToken token)
    {
        if (_disposed || token.IsCancellationRequested || epoch != Volatile.Read(ref _observationEpoch) || _manualCoordinator is null) return;
        bool changed;
        try { changed = await _manualCoordinator.RecordObservedPathsAsync(candidates.Select(item => item.Path).ToArray(), token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        if (changed) await SetUIStateAsync(() =>
        {
            if (!_disposed && epoch == Volatile.Read(ref _observationEpoch)) ApplySnapshot(store.Snapshot);
        });
    }

    private static readonly StringComparer ObservationPathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static string NormalizeObservationPath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static string FilePath(string path) => Path.TrimEndingDirectorySeparator(PlatformFileActions.RequireExistingLocalPath(path));
    private static bool ObservationContains(string root, string path) => ObservationPathComparer.Equals(root, path) ||
        path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static string[] ObservationExclusions(WorkspaceState state, string dataDirectory) =>
        state.Settings.ExcludedFolders.Concat(state.Spaces.Select(space => space.Folder))
            .Concat([dataDirectory, state.Settings.ManagedRoot, state.Settings.ModelCacheDirectory ?? state.Settings.ManagedRoot,
                state.Settings.GetModelDownloadCacheDirectory() ?? state.Settings.ManagedRoot])
            .Select(NormalizeObservationPath).Distinct(ObservationPathComparer).ToArray();
}
