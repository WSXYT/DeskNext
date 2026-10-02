using System.Collections.Concurrent;
using System.Threading.Channels;
using DeskNest.Core.Storage;

namespace DeskNest.Core.Organization;

public sealed record DesktopOrganizationMonitorOptions(
    IReadOnlyList<string> Roots,
    IReadOnlyList<string> ExcludedFolders,
    int QueueCapacity = 256,
    TimeSpan? StabilityWindow = null,
    int DirectoryEntryLimit = 10_000)
{
    public TimeSpan EffectiveStabilityWindow => StabilityWindow ?? TimeSpan.FromMilliseconds(250);
}

public sealed record DesktopOrganizationMonitorCandidate(
    string Path,
    bool IsDirectory,
    DesktopOrganizationSourceScope SourceScope,
    bool IsStable,
    DesktopOrganizationExclusionReason ExclusionReason);

/// <summary>
/// Multi-root filesystem observation boundary. It creates candidates only;
/// physical moves remain exclusively owned by DesktopOrganizationTransaction.
/// </summary>
public sealed class DesktopOrganizationMonitor : IAsyncDisposable
{
    private readonly DesktopOrganizationMonitorOptions _options;
    private readonly Func<IReadOnlyList<DesktopOrganizationMonitorCandidate>, Task> _onCandidates;
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(PathComparer);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _baseline = new(PathComparer);
    private readonly ConcurrentDictionary<string, byte> _queued = new(PathComparer);
    private readonly Channel<string> _events;
    private readonly CancellationTokenSource _stop = new();
    private Task? _worker;
    private IReadOnlyList<string> _excluded = [];
    private int _overflowed;

    public DesktopOrganizationMonitor(
        DesktopOrganizationMonitorOptions options,
        Func<IReadOnlyList<DesktopOrganizationMonitorCandidate>, Task> onCandidates)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _onCandidates = onCandidates ?? throw new ArgumentNullException(nameof(onCandidates));
        if (_options.QueueCapacity is < 1 or > 16_384)
            throw new ArgumentOutOfRangeException(nameof(options), "Queue capacity must be between 1 and 16384.");
        if (_options.DirectoryEntryLimit is < 1 or > 100_000)
            throw new ArgumentOutOfRangeException(nameof(options), "Directory entry limit must be between 1 and 100000.");

        _events = Channel.CreateBounded<string>(new BoundedChannelOptions(_options.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    }

    public bool IsRunning => _worker is { IsCompleted: false };
    public Exception? Error => _worker?.Exception?.GetBaseException();
    public bool HasOverflowed => Volatile.Read(ref _overflowed) != 0;
    public IReadOnlyCollection<string> BaselinePaths => _baseline.Keys.ToArray();

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_worker is not null)
            throw new InvalidOperationException("The monitor is already running.");

        var roots = NormalizeRoots(_options.Roots);
        var excluded = _excluded = _options.ExcludedFolders.Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))).Distinct(PathComparer).ToArray();
        if (roots.Count == 0) throw new InvalidOperationException("No existing observation folder was selected.");
        foreach (var root in roots) FileSystemVolume.RequireNoReparsePoints(root);
        var first = CaptureBaseline(roots, excluded, cancellationToken);

        foreach (var root in roots)
        {
            var watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite
            };
            watcher.Created += OnChanged;
            watcher.Changed += OnChanged;
            watcher.Renamed += OnRenamed;
            watcher.Deleted += OnChanged;
            watcher.Error += OnWatcherError;
            _watchers[root] = watcher;
            watcher.EnableRaisingEvents = true;
        }

        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        var second = CaptureBaseline(roots, excluded, cancellationToken);
        foreach (var path in second.Intersect(first, PathComparer))
            _baseline[path] = DateTimeOffset.UtcNow;

        foreach (var path in second.Except(first, PathComparer))
            Enqueue(path);
        _worker = Task.Run(ProcessEventsAsync);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _events.Writer.TryComplete();
        foreach (var watcher in _watchers.Values)
            watcher.Dispose();
        _watchers.Clear();
        try
        {
            if (_worker is not null)
            {
                try { await _worker.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
        }
        finally { _stop.Dispose(); }
    }

    private async Task ProcessEventsAsync()
    {
        await foreach (var path in _events.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
        {
            try
            {
                if (!File.Exists(path) && !Directory.Exists(path))
                {
                    _baseline.TryRemove(path, out _);
                    continue;
                }
                if (IsExcluded(path) || _baseline.ContainsKey(path))
                    continue;
                if (_baseline.Count >= DesktopOrganizationTransaction.MaximumDirectoryEntries)
                    throw new IOException("Observation baseline reached its entry limit; inspect the folder before restarting.");
                FileSystemVolume.RequireNoReparsePoints(path);

                bool isDirectory = Directory.Exists(path);
                var stability = await WaitForStabilityAsync(path, isDirectory, _stop.Token).ConfigureAwait(false);
                var exclusion = isDirectory
                    ? stability.ReparsePoint ? DesktopOrganizationExclusionReason.ReparsePoint
                    : stability.Stable ? DesktopOrganizationExclusionReason.Folder
                    : stability.WithinBudget ? DesktopOrganizationExclusionReason.SlowItem
                    : DesktopOrganizationExclusionReason.BatchLimit
                    : stability.Stable ? DesktopOrganizationExclusionReason.None : DesktopOrganizationExclusionReason.SlowItem;
                await _onCandidates([
                    new DesktopOrganizationMonitorCandidate(
                        path, isDirectory, DesktopOrganizationSourceScope.Personal, stability.Stable, exclusion)
                ]).ConfigureAwait(false);
                _baseline[path] = DateTimeOffset.UtcNow;
            }
            finally
            {
                _queued.TryRemove(path, out _);
            }
        }
    }

    internal async Task<(bool Stable, bool WithinBudget, bool ReparsePoint)> WaitForStabilityAsync(
        string path, bool isDirectory, CancellationToken cancellationToken,
        Func<CancellationToken, Task>? waitBetweenSamples = null)
    {
        var first = GetFingerprint(path, isDirectory, _options.DirectoryEntryLimit, cancellationToken);
        // An internal wait seam lets tests mutate between actual samples, not race a watcher timer.
        await (waitBetweenSamples?.Invoke(cancellationToken) ??
            Task.Delay(_options.EffectiveStabilityWindow, cancellationToken)).ConfigureAwait(false);
        var second = GetFingerprint(path, isDirectory, _options.DirectoryEntryLimit, cancellationToken);
        return (first.Value is not null && first == second, first.WithinBudget && second.WithinBudget,
            first.ReparsePoint || second.ReparsePoint);
    }

    private void OnChanged(object sender, FileSystemEventArgs args) => Enqueue(args.FullPath);
    private void OnRenamed(object sender, RenamedEventArgs args) => Enqueue(args.FullPath);
    private void OnWatcherError(object sender, ErrorEventArgs args) => Interlocked.Exchange(ref _overflowed, 1);

    private void Enqueue(string path)
    {
        string normalized = Path.GetFullPath(path);
        if (!_queued.TryAdd(normalized, 0))
            return;
        if (!_events.Writer.TryWrite(normalized))
        {
            _queued.TryRemove(normalized, out _);
            Interlocked.Exchange(ref _overflowed, 1);
        }
    }

    private bool IsExcluded(string path) => _excluded.Any(root => IsWithin(path, root));

    private static bool IsWithin(string path, string root) => PathComparer.Equals(path, root) ||
        path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, PathComparison);

    private static HashSet<string> CaptureBaseline(IReadOnlyList<string> roots, IReadOnlyList<string> excluded,
        CancellationToken token)
    {
        var paths = new HashSet<string>(PathComparer);
        var visited = new HashSet<string>(PathComparer);
        var pending = new Stack<(string Path, int Depth)>(roots.Select(root => (root, 0)));
        while (pending.TryPop(out var current))
        {
            token.ThrowIfCancellationRequested();
            if (!visited.Add(current.Path) || excluded.Any(root => IsWithin(current.Path, root))) continue;
            if ((File.GetAttributes(current.Path) & FileAttributes.ReparsePoint) != 0) continue;
            if (current.Depth > DesktopOrganizationTransaction.MaximumDirectoryDepth)
                throw new IOException("Observation baseline exceeded its directory depth limit.");
            foreach (var path in Directory.EnumerateFileSystemEntries(current.Path))
            {
                token.ThrowIfCancellationRequested();
                if (excluded.Any(root => IsWithin(path, root))) continue;
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (paths.Add(path) && paths.Count > DesktopOrganizationTransaction.MaximumDirectoryEntries)
                    throw new IOException("Observation baseline exceeded its entry limit.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push((path, current.Depth + 1));
            }
        }
        return paths;
    }

    private static Fingerprint GetFingerprint(string path, bool isDirectory, int entryLimit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                return new Fingerprint(null, false, true);
            if (!isDirectory)
            {
                var file = new FileInfo(path);
                return new Fingerprint(file.Exists ? $"file:{file.Length}:{file.LastWriteTimeUtc.Ticks}" : null, true, false);
            }

            var entries = new List<string>();
            var pending = new Stack<string>([path]);
            while (pending.TryPop(out var current))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    return new Fingerprint(null, true, true);
                foreach (var entry in Directory.EnumerateFileSystemEntries(current))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entries.Count >= entryLimit)
                        return new Fingerprint(null, false, false);
                    var entryAttributes = File.GetAttributes(entry);
                    if ((entryAttributes & FileAttributes.ReparsePoint) != 0)
                        return new Fingerprint(null, true, true);
                    bool directory = (entryAttributes & FileAttributes.Directory) != 0;
                    FileSystemInfo info = directory ? new DirectoryInfo(entry) : new FileInfo(entry);
                    long length = info is FileInfo file ? file.Length : 0;
                    string relative = Path.GetRelativePath(path, entry);
                    entries.Add($"{relative.Length}:{relative}:{length}:{info.LastWriteTimeUtc.Ticks}:{(directory ? 'd' : 'f')}");
                    if (directory)
                        pending.Push(entry);
                }
            }
            entries.Sort(StringComparer.Ordinal);
            return new Fingerprint($"dir:{string.Join('|', entries)}", true, false);
        }
        catch (IOException) { return new Fingerprint(null, false, false); }
        catch (UnauthorizedAccessException) { return new Fingerprint(null, false, false); }
    }

    private readonly record struct Fingerprint(string? Value, bool WithinBudget, bool ReparsePoint);

    private static IReadOnlyList<string> NormalizeRoots(IReadOnlyList<string> paths) =>
        paths.Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))
            .Distinct(PathComparer)
            .Where(Directory.Exists)
            .ToArray();
}
