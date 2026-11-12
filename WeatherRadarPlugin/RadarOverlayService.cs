using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using SharpDX;
using vatsys;

namespace WeatherRadarPlugin;

/// <summary>
/// Owns every interaction with vatSys's shared display state.
///
/// vatSys renders each ASD on its own background thread whose loop only catches
/// OperationCanceledException, so any exception this plugin can provoke on that thread
/// terminates the whole client. Two rules therefore hold everywhere below:
///
///   1. No exception may ever escape into a vatSys-owned thread. Every public entry
///      point, timer callback, and GUI-marshalled action is wrapped.
///   2. DisplayMaps.Maps, LogicalPositions.Positions[].Maps, Map.Infills and the custom
///      colour registry are read by the render threads without synchronisation. They are
///      only ever mutated on the GUI thread while every ASD render loop is suspended, and
///      the mutation is abandoned outright if a suspension cannot be obtained.
/// </summary>
internal sealed class RadarOverlayService : IDisposable
{
    // BOM asks users of its FTP service to match their downloads to how often a product is
    // updated, and radar images are updated every five minutes. So there is one fetch per
    // five-minute round, timed for just after BOM has published it, not a faster poll.
    private static readonly TimeSpan RoundInterval = TimeSpan.FromMinutes(5);
    // Every radar stamps its scans at minutes ending in 4 and 9 (UTC), and the file reaches
    // the FTP server one to two minutes after that stamp (measured 10 November 2026).
    private static readonly TimeSpan RoundPhase = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan PublishAllowance = TimeSpan.FromSeconds(125);
    // Without this every copy of the plugin would connect to BOM in the same second.
    private const int MaxStartJitterSeconds = 40;
    // A fetch that finds under half the radars updated means BOM was late or this PC's
    // clock is fast. It looks once more a minute later, then waits for the next round.
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(60);
    // Fetching starts when the plugin loads, which is in the middle of vatSys starting up.
    // A short wait keeps the first connections out of that.
    private static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(5);
    // A picture being built on a background thread reports back to the GUI thread. If that
    // report never arrives, another build is allowed after this long.
    private static readonly TimeSpan FrameBuildPatience = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AnimationInterval = TimeSpan.FromMilliseconds(900);
    // Setup happens once and can afford to wait for a slow frame. Animation runs on the GUI
    // thread every 900 ms, so it fails fast and skips the frame rather than stalling vatSys.
    private static readonly TimeSpan SetupSuspendTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DisplaySuspendTimeout = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan ErrorReportInterval = TimeSpan.FromMinutes(10);
    // A radar that stops producing scans keeps showing its last one, but only until that
    // scan is this much older than the newest scan anywhere. The same limit applies to the
    // whole picture when BOM cannot be reached or stops publishing. Past it the rain is too
    // old to pass off as current, and an empty patch is the honest display.
    private static readonly TimeSpan MaxScanAge = TimeSpan.FromMinutes(20);
    // If the newest scan on the server is already this old by this PC's clock, the feed has
    // stopped (or the clock is badly wrong) and nothing is drawn.
    private static readonly TimeSpan MaxFeedAge = TimeSpan.FromMinutes(30);
    private const int MaxConsecutiveApplyFailures = 10;
    // Anonymous FTP servers commonly limit connections per address; two is polite.
    private const int MaxParallelDownloads = 2;
    // While a picture is filling in from nothing, it is updated as radars arrive, but no
    // more often than this. A round's 65 files take about ten seconds, so that is a dozen
    // or so redraws rather than 65: every redraw rebuilds the merged picture, pauses
    // rendering for a moment and costs vatSys a full map repaint.
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(750);
    private const int DefaultTransparencyPercent = 80;
    private const int MaxHistoryFrames = 7;

    // vatSys is a 32-bit process, and every rectangle becomes an Infill holding four
    // Coordinate objects that the renderer also caches as screen vectors. This ceiling
    // bounds the plugin's worst case on a nationwide rain day to tens of megabytes. The
    // mosaic coarsens its grid to stay under it rather than leaving rain out.
    private const int MaxInfillsPerFrame = 30000;
    private const string ColourPrefix = "WeatherRadarPlugin_";
    private const string MapFolder = "Weather Radar";

    private static readonly List<DisplayMaps.Map.Infill> NoInfills = new();

    // The two calls that reach BOM. They are fields so tests can stand in for the server.
    private Func<CancellationToken, Task<string[]>> _listScans = BomFtp.ListScansAsync;
    private Func<string, CancellationToken, Task<byte[]>> _downloadScan = BomFtp.DownloadScanAsync;
    private readonly TimeSpan _jitter;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    // Written by a refresh and read by the history preload that follows it.
    private volatile string[]? _lastListing;
    // The newest scan time seen on the server, and when (by this PC's clock) it last moved on.
    private DateTime _newestStampSeen;
    private DateTime _feedAdvancedUtc;
    private bool _retriedThisRound;

    // The merged national grid is a few megabytes and is reused between frames. A refresh
    // and a history preload left over from a quick off-and-on can both want it at once.
    private readonly object _mosaicLock = new();
    private RadarMosaic? _mosaic;

    // What is known about each radar between checks, keyed by product. Refreshes read and
    // write it from background threads and a disable clears it, so it is concurrent.
    // Refreshes never overlap, but within one a partial picture reads every entry while
    // other radars are still downloading, so an entry is read and written under its own lock.
    private readonly ConcurrentDictionary<string, RadarState> _radars = new(StringComparer.Ordinal);
    // Stopwatch time the last partial picture was stored; 0 when there has been none.
    private long _lastProgressTimestamp;

    // The last few rounds, oldest first, at most MaxHistoryFrames. Fetching fills this in
    // the background whether or not the overlay is on show; the GUI thread reads it to draw.
    // The lock is only ever held for list and field access, never across a build, a wait or
    // a call into vatSys.
    private readonly object _roundsLock = new();
    private readonly List<Round> _rounds = new();
    private volatile bool _historyLoaded;
    // The last reason fetching failed, kept so it can be reported when the overlay is shown.
    // A hidden overlay does not put network errors in the controller's error list.
    private volatile Exception? _fetchProblem;

    // GUI-thread state. Nothing below is touched from a background thread.
    private DisplayMaps.Map[]? _maps;
    private System.Windows.Forms.Timer? _animationTimer;
    private int _animationIndex;
    private int _consecutiveApplyFailures;
    // Stopwatch time a picture build was started, 0 when none is in flight.
    private long _frameBuildStarted;
    private System.Windows.Forms.Timer? _memoryTimer;

    // How work reaches the GUI thread from a background one. A field so tests can stand in
    // for vatSys's main form, without which MMI.InvokeOnGUI does nothing.
    private Action<Action> _postToGui = action => MMI.InvokeOnGUI(() => action());

    // See HostMemory. A field so tests can lower it.
    private long _memoryLimitBytes = HostMemory.LimitBytes;
    private static readonly TimeSpan MemoryCheckInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Raised on the GUI thread after the overlay has switched itself off because vatSys
    /// was running out of memory, so the menu can be brought back in step.
    /// </summary>
    internal event Action? MemoryLow;

    private System.Threading.Timer? _refreshTimer;
    private CancellationTokenSource? _work;
    private int _started;
    // Whether the overlay is on show. Fetching does not depend on it.
    private volatile bool _visible;
    private volatile bool _animationEnabled;
    private volatile bool _paused;
    private volatile bool _disposed;
    private int _transparencyPercent = DefaultTransparencyPercent;

    // What the pictures cover. By default only rain inside the controller's vis circles;
    // null means every radar (chosen from the menu, or when vatSys has no vis range yet).
    // Written on the GUI thread; a background build uses the snapshot it was started with.
    private volatile bool _showAllRadars;
    private volatile VisArea? _area;
    // Bumped whenever _area changes, so a picture built for the old area is thrown away.
    private int _areaGeneration;
    // GUI thread only: an area seen on the last check, applied once it holds still.
    private VisArea? _pendingArea;
    private bool _pendingAreaSeen;
    private System.Windows.Forms.Timer? _areaTimer;
    private static readonly TimeSpan AreaCheckInterval = TimeSpan.FromSeconds(1.5);

    private sealed class RadarState
    {
        /// <summary>The newest decoded image, or null when there is nothing current to draw.</summary>
        internal RadarScan? Scan;
        /// <summary>The server file <see cref="Scan"/> came from.</summary>
        internal string? FileName;
        /// <summary>When the radar made <see cref="Scan"/>, from the file name (UTC).</summary>
        internal DateTime StampUtc;
    }

    internal sealed class RefreshOutcome
    {
        /// <summary>BOM's file list was read.</summary>
        internal bool Listed;
        /// <summary>Radars with a current scan on the server.</summary>
        internal int Live;
        /// <summary>Radars whose scan was newer than the one held, and downloaded.</summary>
        internal int NewScans;
    }

    /// <summary>
    /// One five-minute round: the scans every radar made in it, and, only while the overlay
    /// is on show, the picture built from them. Scans are a few kilobytes per radar; a built
    /// picture is several megabytes, which is why hidden rounds do not carry one.
    /// Fields are read and written under <see cref="_roundsLock"/>.
    /// </summary>
    private sealed class Round
    {
        internal Round(DateTime stamp, List<RadarScan> scans) { Stamp = stamp; Scans = scans; }

        /// <summary>The round's scan time (UTC), on the five-minute grid.</summary>
        internal DateTime Stamp { get; }
        /// <summary>Never changed in place; a more complete set replaces it.</summary>
        internal List<RadarScan> Scans;
        internal DisplayFrame? Frame;
        /// <summary>The scans <see cref="Frame"/> was built from.</summary>
        internal List<RadarScan>? FrameScans;
        /// <summary>Scans a picture could not be built from, so the attempt is not repeated.</summary>
        internal List<RadarScan>? FailedScans;

        internal bool NeedsFrame =>
            !ReferenceEquals(FrameScans, Scans) && !ReferenceEquals(FailedScans, Scans);

        internal void DropFrame() { Frame = null; FrameScans = null; FailedScans = null; }
    }

    /// <summary>One complete observation, already converted to per-level vatSys polygons.</summary>
    private sealed class DisplayFrame
    {
        internal DisplayFrame(List<DisplayMaps.Map.Infill>[] levels) => Levels = levels;
        internal List<DisplayMaps.Map.Infill>[] Levels { get; }
    }

    internal RadarOverlayService()
    {
        _jitter = TimeSpan.FromSeconds(new Random().Next(0, MaxStartJitterSeconds + 1));
    }

    // ---------------------------------------------------------------- public surface

    /// <summary>
    /// Starts fetching in the background, and keeps it going until the plugin is disposed.
    /// Called when the plugin loads, so the current picture and the recent rounds are
    /// already here when the overlay is switched on. Never throws.
    /// </summary>
    internal void Start()
    {
        if (_disposed) return;
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        try
        {
            var work = new CancellationTokenSource();
            Interlocked.Exchange(ref _work, work);
            var token = work.Token;
            if (_disposed)
            {
                try { work.Cancel(); } catch (ObjectDisposedException) { }
                return;
            }
            _ = Task.Run(() => RunFetchingAsync(token));
        }
        catch (Exception ex)
        {
            Report("radar start", ex);
        }
    }

    /// <summary>The first fetch, the earlier rounds for animation, then one fetch per round.</summary>
    private async Task RunFetchingAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(StartDelay, token).ConfigureAwait(false);
            var outcome = await RefreshAsync(token).ConfigureAwait(false);
            if (_disposed || token.IsCancellationRequested) return;
            if (outcome.Listed) await PreloadHistoryAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { ReportFetch("radar start", ex); }

        try { StartRefreshTimer(token); }
        catch (Exception ex) { ReportFetch("radar refresh schedule", ex); }
    }

    /// <summary>
    /// Shows or hides the overlay. Fetching carries on either way, so showing draws what is
    /// already held and hiding keeps it. Showing throws if the display cannot take the
    /// overlay; hiding never throws. Call on the GUI thread.
    /// </summary>
    internal void SetVisible(bool visible)
    {
        if (_disposed) return;

        if (!visible)
        {
            _visible = false;
            SetAnimationEnabled(false);
            lock (_mosaicLock) _mosaic = null;
            RunOnGui("hide overlay", () =>
            {
                StopMemoryTimer();
                StopAreaTimer();
                // Nothing comes back later to retry a hide, so it waits as long as setup does.
                ShowFrame(null, SetupSuspendTimeout);
                // The built pictures are the bulk of the memory; the scans stay.
                lock (_roundsLock)
                    foreach (var round in _rounds) round.DropFrame();
                _animationIndex = 0;
            });
            return;
        }

        var unreclaimable = HostMemory.ReleaseLeakedGraphicsObjects();
        if (unreclaimable is not null)
            Report("radar memory safeguard", new InvalidOperationException(
                "vatSys's leaked drawing objects cannot be reclaimed (" + unreclaimable +
                "). The overlay will switch itself off if memory runs low."));
        var inUse = HostMemory.PrivateBytes();
        if (inUse >= _memoryLimitBytes)
            throw new InvalidOperationException(
                $"vatSys is already using {inUse / (1024 * 1024)} MB of memory, too close to the limit " +
                "of a 32-bit program to add the radar overlay. Restart vatSys first.");

        _visible = true;
        if (!RunOnGuiChecked("overlay setup", EnsureMapsRegistered))
        {
            _visible = false;
            throw new InvalidOperationException("The vatSys display could not be prepared for the radar overlay.");
        }
        RunOnGui("show overlay", () =>
        {
            StartMemoryTimer();
            // Pictures were dropped on hide, so whatever area is current now is simply used.
            _area = _showAllRadars ? null : ReadVisArea();
            _areaGeneration++;
            _pendingAreaSeen = false;
            StartAreaTimer();
            SyncDisplay();
        });

        // Normally started when the plugin loaded; this covers a start that failed.
        Start();

        bool nothingHeld;
        lock (_roundsLock) nothingHeld = _rounds.Count == 0;
        var problem = _fetchProblem;
        if (nothingHeld && problem is not null) Report("radar refresh", problem);
    }

    internal void SetAnimationEnabled(bool enabled)
    {
        if (_disposed) return;
        _animationEnabled = enabled && _visible;
        _paused = false;
        RunOnGui("animation toggle", () =>
        {
            StopAnimationTimer();
            if (!_animationEnabled)
            {
                ShowLatestFrame();
                return;
            }
            _animationIndex = 0;
            var frames = BuiltFrames();
            if (frames.Length > 0) ShowFrame(frames[0]);
            _animationTimer = new System.Windows.Forms.Timer { Interval = (int)AnimationInterval.TotalMilliseconds };
            _animationTimer.Tick += AnimationTick;
            _animationTimer.Start();
        });
    }

    internal void SetPaused(bool paused)
    {
        if (_disposed) return;
        _paused = paused && _animationEnabled;
    }

    /// <summary>
    /// Chooses between rain inside the controller's vis circles (the default) and rain from
    /// every radar. Takes effect at once if the overlay is on show; nothing is downloaded,
    /// since every radar is fetched either way. Any thread.
    /// </summary>
    internal void SetShowAllRadars(bool showAll)
    {
        if (_disposed) return;
        _showAllRadars = showAll;
        RunOnGui("radar area", () =>
        {
            if (_disposed || !_visible) return;
            _pendingAreaSeen = false;
            ApplyArea(showAll ? null : ReadVisArea());
        });
    }

    internal void SetTransparency(int transparencyPercent)
    {
        if (_disposed) return;
        _transparencyPercent = Math.Max(0, Math.Min(100, transparencyPercent));
        RunOnGui("transparency", () =>
        {
            if (!MemoryIsSafe()) return;
            if (!WriteColours()) return;
            MMI.RequestRedraw(true, false, false);
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _visible = false;
        _animationEnabled = false;

        StopRefreshTimer();
        var work = Interlocked.Exchange(ref _work, null);
        try { work?.Cancel(); } catch (ObjectDisposedException) { }
        work?.Dispose();

        RunOnGui("cleanup", () =>
        {
            StopAnimationTimer();
            StopMemoryTimer();
            StopAreaTimer();
            var maps = Interlocked.Exchange(ref _maps, null);
            lock (_roundsLock) _rounds.Clear();
            if (maps is null) return;

            using (var suspension = RenderSuspension.Acquire(SetupSuspendTimeout))
            {
                if (!suspension.Acquired) return;
                foreach (var map in maps) map.Infills = NoInfills;
                foreach (var position in LogicalPositions.Positions.ToArray())
                    foreach (var map in maps) position.Maps.Remove(map);
                foreach (var map in maps) DisplayMaps.Maps.Remove(map);
            }
            MMI.RequestRedraw(true, false, false);
        });

        // _refreshLock is intentionally left to the GC: an in-flight refresh still has to
        // release it, and disposing underneath that would throw on a background thread.
    }

    // ---------------------------------------------------------------- download pipeline

    /// <summary>
    /// Reads BOM's list of radar scans, downloads the ones that are newer than those held,
    /// and updates the current round only if something changed.
    /// </summary>
    private async Task<RefreshOutcome> RefreshAsync(CancellationToken token)
    {
        var outcome = new RefreshOutcome();
        if (_disposed || token.IsCancellationRequested) return outcome;
        try { if (!await _refreshLock.WaitAsync(0, CancellationToken.None).ConfigureAwait(false)) return outcome; }
        catch (Exception ex) { Report("radar refresh lock", ex); return outcome; }
        try
        {
            Dictionary<string, string>? newest = null;
            try
            {
                var names = await _listScans(token).ConfigureAwait(false);
                newest = NewestByProduct(names);
                if (newest.Count == 0) throw new InvalidDataException("the list held no radar scans.");
                // Only the history preload reads it, and that happens once.
                _lastListing = _historyLoaded ? null : names;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return outcome; }
            catch (Exception ex)
            {
                newest = null;
                ReportFetch("radar refresh", new InvalidOperationException(
                    "BOM's list of radar images could not be read: " + ex.Message, ex));
            }

            var now = DateTime.UtcNow;
            var newestStamp = default(DateTime);
            var changed = 0;
            if (newest is not null)
            {
                outcome.Listed = true;
                foreach (var name in newest.Values)
                {
                    var stamp = StampOf(name);
                    if (stamp > newestStamp) newestStamp = stamp;
                }
                if (newestStamp > _newestStampSeen || _feedAdvancedUtc == default)
                {
                    _newestStampSeen = newestStamp;
                    _feedAdvancedUtc = now;
                }

                if (now - newestStamp > MaxFeedAge)
                {
                    ReportFetch("radar feed", new InvalidOperationException(
                        $"BOM's newest radar scan is from {newestStamp:HH:mm} UTC, " +
                        $"{(now - newestStamp).TotalMinutes:F0} minutes ago by this computer's clock. " +
                        "Rain that old is not drawn."));
                    newest = null;
                }
            }

            if (newest is not null)
            {
                var failures = new ConcurrentBag<string>();
                var attempted = 0;
                var live = 0;
                // The throttle bounds how many connections are open to BOM at once.
                using (var throttle = new SemaphoreSlim(MaxParallelDownloads, MaxParallelDownloads))
                {
                    var tasks = RadarDefinition.All.Select(async radar =>
                    {
                        if (!newest.TryGetValue(radar.Product, out var fileName)) return;
                        var stamp = StampOf(fileName);
                        // A radar that has been silent this long has nothing current to fetch.
                        if (newestStamp - stamp > MaxScanAge) return;
                        Interlocked.Increment(ref live);

                        var state = _radars.GetOrAdd(radar.Product, _ => new RadarState());
                        lock (state)
                        {
                            if (state.Scan is not null && stamp <= state.StampUtc) return;
                        }

                        var appeared = false;
                        try { await throttle.WaitAsync(token).ConfigureAwait(false); }
                        catch (OperationCanceledException) { return; }
                        try
                        {
                            Interlocked.Increment(ref attempted);
                            var bytes = await _downloadScan(fileName, token).ConfigureAwait(false);
                            var scan = new RadarScan(radar, RadarImageDecoder.Decode(bytes));
                            lock (state)
                            {
                                appeared = state.Scan is null;
                                state.Scan = scan;
                                state.FileName = fileName;
                                state.StampUtc = stamp;
                            }
                            Interlocked.Increment(ref changed);
                        }
                        catch (OperationCanceledException) { }
                        catch (Exception ex)
                        {
                            // One unavailable radar must never hold back the others. It
                            // keeps its previous scan until that ages out.
                            failures.Add($"{radar.Product} {radar.Name}: {ex.Message}");
                        }
                        finally
                        {
                            try { throttle.Release(); } catch (ObjectDisposedException) { }
                        }

                        // A radar with nothing held (the first fetch, or back after an
                        // outage) joins the picture as soon as it arrives rather than when
                        // the whole round has. A radar replacing its own older scan waits
                        // for the round, so a normal update is still one redraw. This runs
                        // inside the radar's task, after its connection is handed back, so
                        // every partial picture is stored before the complete one below.
                        if (appeared) StoreProgress(newestStamp, token);
                    }).ToList();
                    await Task.WhenAll(tasks).ConfigureAwait(false);
                }
                outcome.Live = live;
                outcome.NewScans = changed;

                if (token.IsCancellationRequested || _disposed) return outcome;
                if (attempted > 0 && changed == 0)
                    ReportFetch("radar refresh", new InvalidOperationException(
                        "No BOM radar images could be downloaded. " + failures.FirstOrDefault()));
                else
                    _fetchProblem = null;
            }

            if (token.IsCancellationRequested || _disposed) return outcome;

            // The whole picture ages out when the feed has not moved on, whether because BOM
            // cannot be reached or because it has stopped publishing. A negative interval
            // means the system clock moved backwards; treat that as aged out too.
            var sinceAdvance = now - _feedAdvancedUtc;
            var feedStopped = _feedAdvancedUtc != default &&
                (sinceAdvance < TimeSpan.Zero || sinceAdvance > MaxScanAge);

            var scans = new List<RadarScan>();
            var expired = false;
            foreach (var radar in RadarDefinition.All)
            {
                if (!_radars.TryGetValue(radar.Product, out var state) || state.Scan is null) continue;
                var stale = feedStopped || newest is null && outcome.Listed ||
                    newest is not null && newestStamp - state.StampUtc > MaxScanAge;
                if (!stale)
                {
                    scans.Add(state.Scan);
                    continue;
                }
                state.Scan = null;
                state.FileName = null;
                state.StampUtc = default;
                expired = true;
            }

            // Most fetches after the first find one new scan per radar; a retry may find none.
            if (changed == 0 && !expired) return outcome;

            // With no listing there is no new round; the newest one held loses whatever expired.
            StoreRound(newest is null ? default : RoundOf(newestStamp), scans);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ReportFetch("radar refresh", ex);
        }
        finally
        {
            try { _refreshLock.Release(); } catch (ObjectDisposedException) { }
        }
        return outcome;
    }

    /// <summary>
    /// Stores the radars downloaded so far as the current round, part way through a refresh,
    /// so a picture filling in from nothing can be shown as it grows. Called from download
    /// threads, at most two at a time; whichever arrives first in each
    /// <see cref="ProgressInterval"/> stores and the rest leave it to a later call or to the
    /// complete round that ends the refresh. Never throws.
    /// </summary>
    private void StoreProgress(DateTime newestStamp, CancellationToken token)
    {
        try
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            var last = Interlocked.Read(ref _lastProgressTimestamp);
            var elapsedSeconds = (now - last) / (double)System.Diagnostics.Stopwatch.Frequency;
            if (last != 0 && elapsedSeconds >= 0 && elapsedSeconds < ProgressInterval.TotalSeconds) return;
            if (Interlocked.CompareExchange(ref _lastProgressTimestamp, now, last) != last) return;
            if (token.IsCancellationRequested || _disposed) return;

            var scans = new List<RadarScan>();
            foreach (var radar in RadarDefinition.All)
            {
                if (!_radars.TryGetValue(radar.Product, out var state)) continue;
                lock (state)
                {
                    // The same age limit the complete round applies.
                    if (state.Scan is not null && newestStamp - state.StampUtc <= MaxScanAge)
                        scans.Add(state.Scan);
                }
            }
            if (scans.Count == 0) return;
            StoreRound(RoundOf(newestStamp), scans);
        }
        catch (Exception ex)
        {
            Report("radar progress", ex);
        }
    }

    /// <summary>
    /// Records what the radars show for a round and, if the overlay is on show, has it drawn.
    /// A stamp at or before the newest round held (including none at all) updates that
    /// round, so a retry, a partial picture or an expiry never adds a frame to the
    /// animation. No scans at all means everything has aged out, and the history with it.
    /// Called from background threads.
    /// </summary>
    private void StoreRound(DateTime roundStamp, List<RadarScan> scans)
    {
        lock (_roundsLock)
        {
            if (scans.Count == 0)
            {
                _rounds.Clear();
            }
            else
            {
                var last = _rounds.Count > 0 ? _rounds[_rounds.Count - 1] : null;
                if (last is not null && roundStamp <= last.Stamp)
                {
                    last.Scans = scans;
                }
                else if (roundStamp != default)
                {
                    _rounds.Add(new Round(roundStamp, scans));
                    PruneRounds();
                }
            }
        }
        RequestDisplaySync();
    }

    /// <summary>
    /// Keeps the newest <see cref="MaxHistoryFrames"/> rounds, and none older than that many
    /// rounds before the newest: after a long gap (the PC asleep, BOM unreachable) the
    /// animation must not run hours-old rain into current rain. Caller holds the lock.
    /// </summary>
    private void PruneRounds()
    {
        if (_rounds.Count == 0) return;
        var oldestKept = _rounds[_rounds.Count - 1].Stamp -
            TimeSpan.FromTicks(RoundInterval.Ticks * (MaxHistoryFrames - 1));
        _rounds.RemoveAll(round => round.Stamp < oldestKept);
        while (_rounds.Count > MaxHistoryFrames) _rounds.RemoveAt(0);
    }

    /// <summary>
    /// The round a scan belongs to: the latest time at or before it on the grid every radar
    /// stamps to (minutes ending in 4 and 9). Canberra's extra scans at 2 and 7 fall into
    /// the round before them.
    /// </summary>
    internal static DateTime RoundOf(DateTime stamp)
    {
        if (stamp == default) return default;
        var round = RoundInterval.Ticks;
        var sinceRound = (stamp.Ticks - RoundPhase.Ticks) % round;
        if (sinceRound < 0) sinceRound += round;
        return new DateTime(stamp.Ticks - sinceRound, DateTimeKind.Utc);
    }

    private void ReportFetch(string operation, Exception exception)
    {
        _fetchProblem = exception;
        if (_visible) Report(operation, exception);
    }

    /// <summary>Each radar's most recent file. Names sort by time because the stamp is yyyymmddhhmm.</summary>
    private static Dictionary<string, string> NewestByProduct(string[] names)
    {
        var newest = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var split = name.IndexOf(".T.", StringComparison.Ordinal);
            if (split <= 0 || StampOf(name) == default) continue;
            var product = name.Substring(0, split);
            if (!newest.TryGetValue(product, out var held) || string.CompareOrdinal(name, held) > 0)
                newest[product] = name;
        }
        return newest;
    }

    /// <summary>The scan time in a name such as IDR762.T.202611100459.png, or default if it has none.</summary>
    private static DateTime StampOf(string fileName)
    {
        var split = fileName.IndexOf(".T.", StringComparison.Ordinal);
        if (split < 0 || fileName.Length < split + 15) return default;
        return DateTime.TryParseExact(fileName.Substring(split + 3, 12), "yyyyMMddHHmm",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var stamp)
            ? stamp
            : default;
    }

    /// <summary>
    /// Loads the scans from the six rounds before the one on show, for the animation. Frames
    /// are lined up by scan time, not by position in the list, so a radar that scans more
    /// often than the others (Canberra) or has gaps still appears in the right frame.
    /// </summary>
    private async Task PreloadHistoryAsync(CancellationToken token)
    {
        try
        {
            var newestSeen = _newestStampSeen;
            // Nothing current to line the rounds up against, or a feed too old to draw.
            if (newestSeen == default || DateTime.UtcNow - newestSeen > MaxFeedAge) return;
            var roundStamp = RoundOf(newestSeen);
            var names = _lastListing ?? await _listScans(token).ConfigureAwait(false);

            var wanted = MaxHistoryFrames - 1;
            var frameBags = Enumerable.Range(0, wanted).Select(_ => new ConcurrentBag<RadarScan>()).ToArray();
            var byProduct = names
                .Select(name => new { Name = name, Stamp = StampOf(name), Split = name.IndexOf(".T.", StringComparison.Ordinal) })
                .Where(file => file.Split > 0 && file.Stamp != default)
                .GroupBy(file => file.Name.Substring(0, file.Split), StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.OrderByDescending(file => file.Stamp).ToArray(),
                    StringComparer.Ordinal);

            using (var throttle = new SemaphoreSlim(MaxParallelDownloads, MaxParallelDownloads))
            {
                var tasks = new List<Task>();
                foreach (var radar in RadarDefinition.All)
                {
                    if (!byProduct.TryGetValue(radar.Product, out var files)) continue;
                    for (var slot = 0; slot < wanted; slot++)
                    {
                        // Slot 0 is the oldest frame. Each slot takes the radar's last scan
                        // made in that round's five minutes.
                        var roundEnd = roundStamp - TimeSpan.FromTicks(RoundInterval.Ticks * (wanted - slot));
                        var roundStart = roundEnd - RoundInterval;
                        var file = files.FirstOrDefault(candidate => candidate.Stamp <= roundEnd && candidate.Stamp > roundStart);
                        if (file is null) continue;

                        var frameIndex = slot;
                        var fileName = file.Name;
                        tasks.Add(Task.Run(async () =>
                        {
                            try { await throttle.WaitAsync(token).ConfigureAwait(false); }
                            catch (OperationCanceledException) { return; }
                            try
                            {
                                var bytes = await _downloadScan(fileName, token).ConfigureAwait(false);
                                frameBags[frameIndex].Add(new RadarScan(radar, RadarImageDecoder.Decode(bytes)));
                            }
                            catch { /* A missing historical scan only affects that radar and frame. */ }
                            finally
                            {
                                try { throttle.Release(); } catch (ObjectDisposedException) { }
                            }
                        }, CancellationToken.None));
                    }
                }
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }

            if (token.IsCancellationRequested || _disposed) return;

            lock (_roundsLock)
            {
                for (var slot = 0; slot < wanted; slot++)
                {
                    if (frameBags[slot].IsEmpty) continue;
                    var stamp = roundStamp - TimeSpan.FromTicks(RoundInterval.Ticks * (wanted - slot));
                    if (_rounds.Any(round => round.Stamp == stamp)) continue;
                    _rounds.Add(new Round(stamp, frameBags[slot].ToList()));
                }
                // The current round is already there from the refresh; the earlier ones go
                // in underneath it.
                _rounds.Sort((a, b) => a.Stamp.CompareTo(b.Stamp));
                PruneRounds();
            }
            _historyLoaded = true;
            _lastListing = null;
            RequestDisplaySync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ReportFetch("radar history preload", ex);
        }
    }

    // ---------------------------------------------------------------- frame construction

    /// <summary>
    /// Merges the radars into one picture and converts it into vatSys polygons. This
    /// allocates the bulk of the plugin's memory and runs entirely on a background thread —
    /// it touches no vatSys shared state.
    /// </summary>
    private DisplayFrame BuildDisplayFrame(List<RadarScan> scans, VisArea? area)
    {
        var levels = new List<DisplayMaps.Map.Infill>[RadarImageDecoder.Palette.Length];
        for (var index = 0; index < levels.Length; index++) levels[index] = new List<DisplayMaps.Map.Infill>();

        RadarMosaic.Picture picture;
        lock (_mosaicLock)
        {
            _mosaic ??= new RadarMosaic();
            picture = _mosaic.Build(scans, MaxInfillsPerFrame, area);
        }

        if (picture.Truncated)
        {
            // Dropping rain is a last-resort memory guard, and it hides real weather, so it
            // is never allowed to happen quietly.
            Report("radar detail", new InvalidOperationException(
                $"There is too much rain to draw in {MaxInfillsPerFrame} cells even at 8 km detail; " +
                "the weakest returns were left out."));
        }

        foreach (var rect in picture.Rectangles)
        {
            var infill = new DisplayMaps.Map.Infill
            {
                Type = DisplayMaps.Map.InfillTypes.Normal,
                Pattern = DisplayMaps.Map.Patterns.Solid
            };
            double north = picture.NorthOf(rect), south = picture.SouthOf(rect);
            double west = picture.WestOf(rect), east = picture.EastOf(rect);
            infill.Points.Add(new Coordinate(north, west));
            infill.Points.Add(new Coordinate(north, east));
            infill.Points.Add(new Coordinate(south, east));
            infill.Points.Add(new Coordinate(south, west));
            levels[rect.Level].Add(infill);
        }
        return new DisplayFrame(levels);
    }

    // ---------------------------------------------------------------- GUI-thread display

    /// <summary>Asks the GUI thread to bring the display in step with the rounds held. Any thread.</summary>
    private void RequestDisplaySync()
    {
        if (_disposed || !_visible) return;
        RunOnGui("radar display", SyncDisplay);
    }

    /// <summary>
    /// Brings the display in step with the rounds held: clears it if there are none,
    /// otherwise builds the picture for the newest round that lacks an up-to-date one. Each
    /// finished build calls back here, so after switching on the newest round is drawn first
    /// and the earlier ones follow, one build at a time. GUI thread.
    /// </summary>
    private void SyncDisplay()
    {
        if (_disposed || !_visible) return;
        if (!MemoryIsSafe()) return;

        Round? target = null;
        List<RadarScan>? scans = null;
        bool empty;
        lock (_roundsLock)
        {
            empty = _rounds.Count == 0;
            for (var index = _rounds.Count - 1; index >= 0; index--)
            {
                if (!_rounds[index].NeedsFrame) continue;
                target = _rounds[index];
                scans = target.Scans;
                break;
            }
        }
        if (empty)
        {
            ShowFrame(null);
            return;
        }
        if (target is null || scans is null) return;

        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_frameBuildStarted != 0)
        {
            var waited = (now - _frameBuildStarted) / (double)System.Diagnostics.Stopwatch.Frequency;
            if (waited >= 0 && waited < FrameBuildPatience.TotalSeconds) return;
        }
        _frameBuildStarted = now;

        var buildFor = target;
        var buildFrom = scans;
        var area = _area;
        var generation = _areaGeneration;
        try
        {
            Task.Run(() =>
            {
                DisplayFrame? frame = null;
                try { frame = BuildDisplayFrame(buildFrom, area); }
                catch (Exception ex) { Report("radar picture", ex); }
                RunOnGui("radar picture", () => FrameBuilt(buildFor, buildFrom, frame, generation));
            });
        }
        catch (Exception ex)
        {
            _frameBuildStarted = 0;
            Report("radar picture", ex);
        }
    }

    /// <summary>A background build has finished (or failed, with a null frame). GUI thread.</summary>
    private void FrameBuilt(Round round, List<RadarScan> scans, DisplayFrame? frame, int generation)
    {
        _frameBuildStarted = 0;
        if (_disposed || !_visible) return;
        if (generation != _areaGeneration)
        {
            // Built for an area that has since changed. Start on the current one instead.
            SyncDisplay();
            return;
        }

        var isNewestBuilt = false;
        lock (_roundsLock)
        {
            // The round may have aged out of the history while its picture was being built.
            if (_rounds.Contains(round))
            {
                if (frame is null)
                {
                    round.FailedScans = scans;
                }
                else
                {
                    // Kept even if more radars have arrived since: it is newer than what is
                    // on show, and the round still counts as needing a picture.
                    round.Frame = frame;
                    round.FrameScans = scans;
                    for (var index = _rounds.Count - 1; index >= 0; index--)
                    {
                        if (_rounds[index].Frame is null) continue;
                        isNewestBuilt = ReferenceEquals(_rounds[index], round);
                        break;
                    }
                }
            }
        }

        if (isNewestBuilt)
        {
            // An ASD window opened since the last picture may not have the maps in its
            // visible set yet.
            if (_maps is not null) MakeMapsVisible(_maps);
            if (!_animationEnabled) ShowLatestFrame();
        }
        SyncDisplay();
    }

    /// <summary>The pictures built so far, oldest first. GUI thread.</summary>
    private DisplayFrame[] BuiltFrames()
    {
        lock (_roundsLock)
            return _rounds.Where(round => round.Frame is not null).Select(round => round.Frame!).ToArray();
    }

    private void ShowLatestFrame()
    {
        if (!_visible) return;
        var frames = BuiltFrames();
        if (frames.Length > 0) ShowFrame(frames[frames.Length - 1]);
    }

    private void AnimationTick(object? sender, EventArgs e)
    {
        // Fires on the GUI thread, so a throw here would reach vatSys's message loop.
        try
        {
            if (_disposed || !_visible || !_animationEnabled || _paused) return;
            if (!MemoryIsSafe()) return;
            var frames = BuiltFrames();
            if (frames.Length < 2) return;
            _animationIndex++;
            // One extra interval on the newest observation before looping.
            if (_animationIndex > frames.Length) _animationIndex = 0;
            ShowFrame(frames[Math.Min(_animationIndex, frames.Length - 1)]);
        }
        catch (Exception ex)
        {
            StopAnimationTimer();
            _animationEnabled = false;
            Report("radar animation", ex);
        }
    }

    /// <summary>
    /// Swaps the polygons behind the fifteen persistent maps. The maps themselves are never
    /// added or removed after setup, so vatSys's map list, menus and per-position map lists
    /// stay stable no matter how long the client runs.
    /// </summary>
    private void ShowFrame(DisplayFrame? frame) => ShowFrame(frame, DisplaySuspendTimeout);

    private void ShowFrame(DisplayFrame? frame, TimeSpan suspendTimeout)
    {
        var maps = _maps;
        if (maps is null) return;

        using (var suspension = RenderSuspension.Acquire(suspendTimeout))
        {
            if (!suspension.Acquired)
            {
                // Mutating Infills while a render pass is mid-flight makes vatSys look up a
                // polygon it never cached, which kills the render thread. Skipping the frame
                // is always the safe choice; the next refresh or tick tries again.
                if (++_consecutiveApplyFailures < MaxConsecutiveApplyFailures) return;
                _consecutiveApplyFailures = 0;
                if (_animationEnabled)
                {
                    // Repeatedly waiting on a render loop that never settles would stutter
                    // the GUI thread, so give up on animating rather than degrade vatSys.
                    _animationEnabled = false;
                    StopAnimationTimer();
                }
                Report("radar display", new TimeoutException(
                    "vatSys rendering could not be paused to update the radar overlay."));
                return;
            }
            _consecutiveApplyFailures = 0;
            for (var index = 0; index < maps.Length; index++)
                maps[index].Infills = frame is null ? NoInfills : frame.Levels[index];
        }
        MMI.RequestRedraw(true, false, false);
    }

    /// <summary>Creates and registers the fifteen reflectivity maps exactly once.</summary>
    private bool EnsureMapsRegistered()
    {
        if (_maps is not null) return true;
        if (!WriteColours()) return false;

        var maps = new DisplayMaps.Map[RadarImageDecoder.Palette.Length];
        for (var index = 0; index < maps.Length; index++)
        {
            maps[index] = new DisplayMaps.Map
            {
                Name = $"Reflectivity {index + 1:00}",
                Folder = MapFolder,
                Category = DisplayMaps.MapCategories.ASD,
                Type = DisplayMaps.MapTypes.Filled,
                Pattern = DisplayMaps.Map.Patterns.Solid,
                Priority = DisplayMaps.Map.PRIORITY_LOWEST,
                CustomColourName = ColourPrefix + index,
                Infills = NoInfills,
                Lines = new List<DisplayMaps.Map.Line>()
            };
        }

        using (var suspension = RenderSuspension.Acquire(SetupSuspendTimeout))
        {
            if (!suspension.Acquired) return false;
            try
            {
                DisplayMaps.Maps.InsertRange(RainLayerIndex(), maps);
                foreach (var map in maps)
                    foreach (var position in LogicalPositions.Positions)
                        if (!position.Maps.Contains(map)) position.Maps.Add(map);
            }
            catch (Exception ex)
            {
                // Registration is retried on the next refresh, so a half-applied set would
                // leave duplicate maps behind every time. Undo it while still suspended.
                foreach (var map in maps)
                {
                    try
                    {
                        foreach (var position in LogicalPositions.Positions) position.Maps.Remove(map);
                        DisplayMaps.Maps.Remove(map);
                    }
                    catch { /* Nothing further can be done from inside a failed rollback. */ }
                }
                Report("overlay registration", ex);
                return false;
            }
        }

        _maps = maps;
        MakeMapsVisible(maps);
        return true;
    }

    /// <summary>
    /// Where the rain maps go in vatSys's map list. vatSys paints that list front to back in
    /// list order and only sorts it by Priority once, when the profile loads, so a map added
    /// at the end is painted over every profile map whatever its Priority says. Rain belongs
    /// underneath the profile's lines, symbols and labels, but above any filled background
    /// map, which would otherwise paint straight over it.
    /// </summary>
    private static int RainLayerIndex()
    {
        var index = 0;
        for (var i = 0; i < DisplayMaps.Maps.Count; i++)
            if (DisplayMaps.Maps[i].Type == DisplayMaps.MapTypes.Filled) index = i + 1;
        return index;
    }

    /// <summary>
    /// Adds the plugin's map ids to each ASD's visible set. ASD windows opened later pick the
    /// maps up through their logical position, which was updated during registration.
    /// </summary>
    private static void MakeMapsVisible(DisplayMaps.Map[] maps)
    {
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (var control in AsdControls())
        {
            try
            {
                var type = control.GetType();
                var isVisible = type.GetMethod("IsMapVisible", flags, null, new[] { typeof(DisplayMaps.Map) }, null);
                var setVisible = type.GetMethod("SetMapVisible", flags, null,
                    new[] { typeof(DisplayMaps.Map), typeof(bool) }, null);
                if (setVisible is null) continue;
                foreach (var map in maps)
                {
                    if (isVisible?.Invoke(control, new object[] { map }) is true) continue;
                    setVisible.Invoke(control, new object[] { map, true });
                }
            }
            catch (Exception ex)
            {
                Report("map visibility", ex);
            }
        }
    }

    // ---------------------------------------------------------------- colours

    /// <summary>
    /// Publishes the palette into vatSys's custom colour registry. That registry is an
    /// unsynchronised Dictionary the render threads read on every painted map, so adding a
    /// key can rehash it underneath a reader. All writes therefore happen under suspension.
    /// </summary>
    private bool WriteColours()
    {
        try
        {
            var field = typeof(Colours).GetField("customColoursDX",
                BindingFlags.NonPublic | BindingFlags.Static);
            if (field?.GetValue(null) is not System.Collections.IDictionary registry)
                throw new MissingFieldException("The vatSys colour registry was not found.");

            var alpha = (byte)Math.Round(255d * (100 - _transparencyPercent) / 100d);
            using var suspension = RenderSuspension.Acquire(SetupSuspendTimeout);
            if (!suspension.Acquired) return false;
            for (var index = 0; index < RadarImageDecoder.Palette.Length; index++)
            {
                var source = RadarImageDecoder.Palette[index];
                registry[ColourPrefix + index] = new Color(source.R, source.G, source.B, alpha);
            }
            return true;
        }
        catch (Exception ex)
        {
            Report("radar colours", ex);
            return false;
        }
    }

    // ---------------------------------------------------------------- render suspension

    /// <summary>
    /// Parks every ASD render loop between frames. vatSys itself uses this handshake before
    /// touching render resources: clearing suspendRenderReset blocks the loop, and
    /// renderingReset signals that no pass is currently in flight.
    /// </summary>
    private sealed class RenderSuspension : IDisposable
    {
        private List<ManualResetEventSlim>? _held;

        private RenderSuspension(List<ManualResetEventSlim>? held, bool acquired)
        {
            _held = held; Acquired = acquired;
        }

        internal bool Acquired { get; }

        internal static RenderSuspension Acquire(TimeSpan timeout)
        {
            var held = new List<ManualResetEventSlim>();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            try
            {
                foreach (var control in AsdControls())
                {
                    ManualResetEventSlim? suspend;
                    ManualResetEventSlim? rendered;
                    try
                    {
                        var type = control.GetType();
                        suspend = type.GetField("suspendRenderReset", flags)?.GetValue(control) as ManualResetEventSlim;
                        rendered = type.GetField("renderingReset", flags)?.GetValue(control) as ManualResetEventSlim;
                    }
                    catch (Exception ex)
                    {
                        Release(held);
                        Report("render suspend", ex);
                        return new RenderSuspension(null, acquired: false);
                    }

                    // Null means this ASD has not started its render thread yet, so there is
                    // no reader to race with. Anything else must be parked before we proceed.
                    if (suspend is null || rendered is null) continue;

                    try
                    {
                        suspend.Reset();
                        // Registered before the wait so a throw can never strand the loop.
                        held.Add(suspend);
                        if (!rendered.Wait(timeout))
                        {
                            Release(held);
                            return new RenderSuspension(null, acquired: false);
                        }
                    }
                    catch (ObjectDisposedException)
                    {
                        // The control is being torn down; its render loop is already stopping.
                    }
                    catch (Exception ex)
                    {
                        Release(held);
                        Report("render suspend", ex);
                        return new RenderSuspension(null, acquired: false);
                    }
                }
            }
            catch (Exception ex)
            {
                Release(held);
                Report("render suspend", ex);
                return new RenderSuspension(null, acquired: false);
            }
            return new RenderSuspension(held, acquired: true);
        }

        public void Dispose() => Release(Interlocked.Exchange(ref _held, null));

        private static void Release(List<ManualResetEventSlim>? resets)
        {
            if (resets is null) return;
            foreach (var reset in resets)
            {
                try { reset.Set(); }
                catch (ObjectDisposedException) { }
                catch { }
            }
            resets.Clear();
        }
    }

    // ---------------------------------------------------------------- plumbing

    private void StartRefreshTimer(CancellationToken token)
    {
        var timer = new System.Threading.Timer(_ => { _ = RunScheduledRefreshAsync(token); },
            null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        var previous = Interlocked.Exchange(ref _refreshTimer, timer);
        previous?.Dispose();

        // Disposal or a disable can win the race with timer creation; drop the timer we just
        // installed rather than leaving it firing against a dead service.
        if (_disposed || token.IsCancellationRequested)
        {
            StopRefreshTimer();
            return;
        }
        _retriedThisRound = false;
        ScheduleRefresh(UntilNextRound(DateTime.UtcNow, _jitter));
    }

    /// <summary>One timer firing: fetch, then decide when to look next. Never throws.</summary>
    private async Task RunScheduledRefreshAsync(CancellationToken token)
    {
        var retry = false;
        try
        {
            var outcome = await RefreshAsync(token).ConfigureAwait(false);
            retry = ShouldRetry(outcome, _retriedThisRound);
            // BOM could not be reached when fetching started, so the earlier rounds for the
            // animation are still owed.
            if (!_historyLoaded && outcome.Listed && !token.IsCancellationRequested)
                await PreloadHistoryAsync(token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Report("radar refresh schedule", ex);
        }

        if (_disposed || token.IsCancellationRequested) return;
        _retriedThisRound = retry;
        try { ScheduleRefresh(retry ? RetryDelay : UntilNextRound(DateTime.UtcNow, _jitter)); }
        catch (Exception ex) { Report("radar refresh schedule", ex); }
    }

    private void ScheduleRefresh(TimeSpan delay)
    {
        var timer = _refreshTimer;
        try { timer?.Change(delay, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// How long until BOM should have finished publishing the next round of scans: scan
    /// stamps fall on <see cref="RoundPhase"/> of each five minutes, plus the time BOM
    /// takes to publish, plus this installation's jitter.
    /// </summary>
    internal static TimeSpan UntilNextRound(DateTime utcNow, TimeSpan jitter)
    {
        var round = RoundInterval.Ticks;
        var offset = (RoundPhase + PublishAllowance + jitter).Ticks % round;
        var sinceLast = (utcNow.Ticks - offset) % round;
        if (sinceLast < 0) sinceLast += round;
        return TimeSpan.FromTicks(round - sinceLast);
    }

    internal static bool ShouldRetry(RefreshOutcome outcome, bool alreadyRetried) =>
        !alreadyRetried && (!outcome.Listed || outcome.NewScans * 2 < outcome.Live);

    private void StopRefreshTimer()
    {
        var timer = Interlocked.Exchange(ref _refreshTimer, null);
        try { timer?.Dispose(); } catch { }
    }

    // ---------------------------------------------------------------- vis area

    /// <summary>
    /// Switches the pictures to a new area: every held round is rebuilt, newest first. The
    /// picture on show stays until its replacement is ready, so nothing flickers. GUI thread.
    /// </summary>
    private void ApplyArea(VisArea? area)
    {
        if (VisArea.Same(area, _area)) return;
        _area = area;
        _areaGeneration++;
        lock (_roundsLock)
            foreach (var round in _rounds) round.DropFrame();
        _animationIndex = 0;
        SyncDisplay();
    }

    private void StartAreaTimer()
    {
        StopAreaTimer();
        _areaTimer = new System.Windows.Forms.Timer { Interval = (int)AreaCheckInterval.TotalMilliseconds };
        _areaTimer.Tick += AreaTimerTick;
        _areaTimer.Start();
    }

    private void StopAreaTimer()
    {
        var timer = _areaTimer;
        _areaTimer = null;
        if (timer is null) return;
        try
        {
            timer.Stop();
            timer.Tick -= AreaTimerTick;
            timer.Dispose();
        }
        catch { /* A half-disposed WinForms timer is not worth reporting. */ }
    }

    /// <summary>
    /// Follows the vis centres and range. vatSys moves the centres with the display while
    /// they are automatic, so a change is only applied once two checks in a row agree:
    /// a pan in progress does not rebuild every picture on every tick.
    /// </summary>
    private void AreaTimerTick(object? sender, EventArgs e)
    {
        // Fires on the GUI thread, so a throw here would reach vatSys's message loop.
        try
        {
            if (_disposed || !_visible || _showAllRadars) return;
            var current = ReadVisArea();
            if (VisArea.Same(current, _area))
            {
                _pendingAreaSeen = false;
                return;
            }
            if (_pendingAreaSeen && VisArea.Same(current, _pendingArea))
            {
                _pendingAreaSeen = false;
                ApplyArea(current);
                return;
            }
            _pendingArea = current;
            _pendingAreaSeen = true;
        }
        catch (Exception ex)
        {
            Report("radar vis area", ex);
        }
    }

    private static readonly BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>
    /// The controller's vis circles as vatSys draws them: every vis centre, with a radius of
    /// the visibility range or the facility's radio range, whichever is smaller. Null when
    /// vatSys has no range or centre yet (before the connect window has set a range, for
    /// instance), in which case every radar is shown. GUI thread.
    ///
    /// vatSys keeps the centres on an internal object, so they are read by reflection, the
    /// same way vatSys's own Vis Centres window reads them (build 0.4.9305).
    /// </summary>
    private static VisArea? ReadVisArea()
    {
        try
        {
            var networkType = typeof(Network);
            var network = networkType.GetField("Instance", AnyStatic)?.GetValue(null);
            if (network is null) return null;

            var rangeProperty = networkType.GetProperty("Range", AnyInstance);
            var centresProperty = networkType.GetProperty("VisibilityCenters", AnyInstance);
            if (rangeProperty is null || centresProperty is null)
            {
                Report("radar vis area", new MissingMemberException(
                    "This vatSys version does not expose vis centres where expected; showing every radar."));
                return null;
            }

            var radius = rangeProperty.GetValue(network) is int range ? range : 0;
            if (networkType.GetMethod("GetRadioRange", AnyInstance, null, Type.EmptyTypes, null)
                    ?.Invoke(network, null) is int radio && radio > 0 && radio < radius)
                radius = radio;
            if (radius <= 0) return null;

            if (centresProperty.GetValue(network) is not IEnumerable<Coordinate> centres) return null;
            var points = new List<(double Latitude, double Longitude)>();
            foreach (var centre in centres)
            {
                // vatSys uses 0°, 0° for "not set".
                if (centre is null || (centre.Latitude == 0 && centre.Longitude == 0)) continue;
                points.Add((centre.Latitude, centre.Longitude));
            }
            return points.Count == 0 ? null : new VisArea(points, radius);
        }
        catch (Exception ex)
        {
            Report("radar vis area", ex);
            return null;
        }
    }

    // ---------------------------------------------------------------- memory safeguard

    private void StartMemoryTimer()
    {
        StopMemoryTimer();
        _memoryTimer = new System.Windows.Forms.Timer { Interval = (int)MemoryCheckInterval.TotalMilliseconds };
        _memoryTimer.Tick += MemoryTimerTick;
        _memoryTimer.Start();
    }

    private void StopMemoryTimer()
    {
        var timer = _memoryTimer;
        _memoryTimer = null;
        if (timer is null) return;
        try
        {
            timer.Stop();
            timer.Tick -= MemoryTimerTick;
            timer.Dispose();
        }
        catch { /* A half-disposed WinForms timer is not worth reporting. */ }
    }

    private void MemoryTimerTick(object? sender, EventArgs e)
    {
        // Fires on the GUI thread, so a throw here would reach vatSys's message loop.
        try { MemoryIsSafe(); }
        catch (Exception ex) { Report("radar memory safeguard", ex); }
    }

    /// <summary>
    /// False once vatSys's memory is too close to what a 32-bit program can have, in which
    /// case the overlay has just been switched off. Memory already lost is not recoverable
    /// without restarting vatSys; this only stops it getting worse. GUI thread only.
    /// </summary>
    private bool MemoryIsSafe()
    {
        if (_disposed || !_visible) return true;
        var inUse = HostMemory.PrivateBytes();
        if (inUse < _memoryLimitBytes) return true;

        // Not rate limited like other reports: it happens once and explains a visible change.
        try
        {
            Errors.Add(new Exception(
                $"The weather radar switched itself off because vatSys is using {inUse / (1024 * 1024)} MB of " +
                "memory, close to the most a 32-bit program can have. Restart vatSys to get the memory back."),
                "Weather Radar Plugin");
        }
        catch { /* Reporting must never become a second failure. */ }

        try { SetVisible(false); }
        catch (Exception ex) { Report("radar memory safeguard", ex); }
        try { MemoryLow?.Invoke(); }
        catch (Exception ex) { Report("radar memory safeguard", ex); }
        return false;
    }

    private void StopAnimationTimer()
    {
        var timer = _animationTimer;
        _animationTimer = null;
        if (timer is null) return;
        try
        {
            timer.Stop();
            timer.Tick -= AnimationTick;
            timer.Dispose();
        }
        catch { /* A half-disposed WinForms timer is not worth reporting. */ }
    }

    /// <summary>
    /// Snapshots every ASD control. Both the form list and each control tree are copied
    /// rather than enumerated lazily: vatSys can open or close a window at any moment, and a
    /// live enumeration would throw straight back into whichever thread we are on.
    /// </summary>
    private static List<Control> AsdControls()
    {
        var found = new List<Control>();
        Form[] forms;
        try { forms = Application.OpenForms.Cast<Form>().ToArray(); }
        catch (Exception) { return found; }

        foreach (var form in forms)
        {
            try { CollectAsdControls(form, found); }
            catch (Exception) { /* A window closing mid-walk simply contributes nothing. */ }
        }
        return found;
    }

    private static void CollectAsdControls(Control parent, List<Control> found)
    {
        var pending = new Queue<Control>();
        pending.Enqueue(parent);
        while (pending.Count > 0)
        {
            foreach (Control child in pending.Dequeue().Controls)
            {
                if (child.GetType().FullName == "vatsys.ASDControlDX") found.Add(child);
                pending.Enqueue(child);
            }
        }
    }

    /// <summary>
    /// Runs a function on the GUI thread and reports whether it ran AND returned true.
    ///
    /// Deliberately not an overload of <see cref="RunOnGui(string, Action)"/>. When both
    /// existed, `() =&gt; completed = action()` bound to this Func overload rather than the
    /// Action one — an expression lambda whose body has a type prefers the non-void
    /// delegate — so this method called itself forever and took vatSys down with an
    /// uncatchable StackOverflowException. Distinct names make that unrepresentable.
    /// </summary>
    private bool RunOnGuiChecked(string operation, Func<bool> action)
    {
        var completed = false;
        var executed = RunOnGui(operation, () => { completed = action(); });
        return executed && completed;
    }

    private bool RunOnGui(string operation, Action action)
    {
        void Guarded()
        {
            try { action(); }
            catch (Exception ex) { Report(operation, ex); }
        }

        try
        {
            var form = Application.OpenForms.Cast<Form>().FirstOrDefault();
            if (form is not null && !form.InvokeRequired)
            {
                Guarded();
                return true;
            }
            // InvokeOnGUI is a no-op before the main form exists or after it closes, so the
            // action may simply never run. Every caller is safe to skip in that case.
            _postToGui(Guarded);
            return false;
        }
        catch (Exception ex)
        {
            Report(operation + " dispatch", ex);
            return false;
        }
    }

    /// <summary>
    /// Records a fault against vatSys's error list, rate limited per operation. A radar
    /// outage repeating every five minutes must not bury the controller's real errors.
    /// </summary>
    private static void Report(string operation, Exception exception)
    {
        try
        {
            if (!ShouldReport(operation)) return;
            Errors.Add(
                new Exception($"Weather radar {operation} failed: {exception.Message}", exception),
                "Weather Radar Plugin");
        }
        catch { /* Error reporting must never become a second failure. */ }
    }

    private static readonly ConcurrentDictionary<string, long> Reported = new(StringComparer.Ordinal);

    private static bool ShouldReport(string operation)
    {
        var now = DateTime.UtcNow.Ticks;
        var previous = Reported.GetOrAdd(operation, 0L);
        if (previous != 0 && now - previous < ErrorReportInterval.Ticks) return false;
        return Reported.TryUpdate(operation, now, previous);
    }
}
