using System.Collections.Concurrent;
using System.Threading.Channels;

namespace DeskNest.Core.Organization;

public sealed record DesktopOrganizationMonitorOptions(
    IReadOnlyList<string> Roots,
    IReadOnlyList<string> ExcludedFolders,
    int QueueCapacity = 256,
    TimeSpan? StabilityWindow = null)
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
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _baseline = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<string> _events;
    private readonly CancellationTokenSource _stop = new();
    private Task? _worker;
    private int _overflowed;

    public DesktopOrganizationMonitor(
        DesktopOrganizationMonitorOptions options,
        Func<IReadOnlyList<DesktopOrganizationMonitorCandidate>, Task> onCandidates)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _onCandidates = onCandidates ?? throw new ArgumentNullException(nameof(onCandidates));
        if (_options.QueueCapacity is < 1 or > 16_384)
            throw new ArgumentOutOfRangeException(nameof(options), "Queue capacity must be between 1 and 16384.");

        _events = Channel.CreateBounded<string>(new BoundedChannelOptions(_options.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    }

    public bool IsRunning => _worker is not null;
    public bool HasOverflowed => Volatile.Read(ref _overflowed) != 0;
    public IReadOnlyCollection<string> BaselinePaths => _baseline.Keys.ToArray();

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_worker is not null)
            throw new InvalidOperationException("The monitor is already running.");

        var roots = NormalizeRoots(_options.Roots);
        var excluded = NormalizeRoots(_options.ExcludedFolders);
        var first = CaptureBaseline(roots, excluded);
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        var second = CaptureBaseline(roots, excluded);
        foreach (var path in second)
            _baseline[path] = DateTimeOffset.UtcNow;

        foreach (var root in roots)
        {
            var watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
                EnableRaisingEvents = true
            };
            watcher.Created += OnChanged;
            watcher.Changed += OnChanged;
            watcher.Renamed += OnRenamed;
            watcher.Deleted += OnChanged;
            watcher.Error += OnWatcherError;
            _watchers[root] = watcher;
        }

        foreach (var path in second.Except(first, StringComparer.OrdinalIgnoreCase))
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
        if (_worker is not null)
        {
            try { await _worker.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _stop.Dispose();
    }

    private async Task ProcessEventsAsync()
    {
        await foreach (var path in _events.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
        {
            if (!File.Exists(path) && !Directory.Exists(path))
                continue;
            if (IsExcluded(path))
                continue;

            bool isDirectory = Directory.Exists(path);
            bool stable = await WaitForStabilityAsync(path, _stop.Token).ConfigureAwait(false);
            var exclusion = isDirectory
                ? DesktopOrganizationExclusionReason.Folder
                : stable ? DesktopOrganizationExclusionReason.None : DesktopOrganizationExclusionReason.SlowItem;
            await _onCandidates([
                new DesktopOrganizationMonitorCandidate(
                    path, isDirectory, DesktopOrganizationSourceScope.Personal, stable, exclusion)
            ]).ConfigureAwait(false);
        }
    }

    private async Task<bool> WaitForStabilityAsync(string path, CancellationToken cancellationToken)
    {
        var first = GetFingerprint(path);
        await Task.Delay(_options.EffectiveStabilityWindow, cancellationToken).ConfigureAwait(false);
        return first == GetFingerprint(path);
    }

    private void OnChanged(object sender, FileSystemEventArgs args) => Enqueue(args.FullPath);
    private void OnRenamed(object sender, RenamedEventArgs args) => Enqueue(args.FullPath);
    private void OnWatcherError(object sender, ErrorEventArgs args) => Interlocked.Exchange(ref _overflowed, 1);

    private void Enqueue(string path)
    {
        if (!_events.Writer.TryWrite(Path.GetFullPath(path)))
            Interlocked.Exchange(ref _overflowed, 1);
    }

    private bool IsExcluded(string path) =>
        NormalizeRoots(_options.ExcludedFolders).Any(root =>
            string.Equals(path, root, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    private static HashSet<string> CaptureBaseline(IReadOnlyList<string> roots, IReadOnlyList<string> excluded)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
            {
                if (!excluded.Any(x => path.StartsWith(x + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                    paths.Add(Path.GetFullPath(path));
            }
        }
        return paths;
    }

    private static string? GetFingerprint(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                var directory = new DirectoryInfo(path);
                return directory.Exists ? $"dir:{directory.LastWriteTimeUtc.Ticks}" : null;
            }

            var file = new FileInfo(path);
            return file.Exists ? $"file:{file.Length}:{file.LastWriteTimeUtc.Ticks}" : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static IReadOnlyList<string> NormalizeRoots(IReadOnlyList<string> paths) =>
        paths.Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(Directory.Exists)
            .ToArray();
}
