using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
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
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AnimationInterval = TimeSpan.FromMilliseconds(900);
    // Setup happens once and can afford to wait for a slow frame. Animation runs on the GUI
    // thread every 900 ms, so it fails fast and skips the frame rather than stalling vatSys.
    private static readonly TimeSpan SetupSuspendTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DisplaySuspendTimeout = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan ErrorReportInterval = TimeSpan.FromMinutes(10);
    private const int MaxConsecutiveApplyFailures = 10;
    private const int MaxParallelDownloads = 6;
    private const int DefaultTransparencyPercent = 80;
    private const int MaxHistoryFrames = 7;

    // vatSys is a 32-bit process, and every rectangle becomes an Infill holding four
    // Coordinate objects that the renderer also caches as screen vectors. These ceilings
    // bound the plugin's worst case on a nationwide rain day to tens of megabytes.
    private const int MaxCellsPerRadar = 8000;
    private const int MaxInfillsPerFrame = 30000;
    private const int MaxImageBytes = 8 * 1024 * 1024;
    private const string ColourPrefix = "WeatherRadarPlugin_";
    private const string MapFolder = "Weather Radar";

    private static readonly List<DisplayMaps.Map.Infill> NoInfills = new();

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    // GUI-thread state. Nothing below is touched from a background thread.
    private readonly List<DisplayFrame> _history = new();
    private DisplayMaps.Map[]? _maps;
    private System.Windows.Forms.Timer? _animationTimer;
    private int _animationIndex;
    private int _consecutiveApplyFailures;
    private bool _historyLoaded;

    private System.Threading.Timer? _refreshTimer;
    private CancellationTokenSource? _work;
    private volatile bool _enabled;
    private volatile bool _animationEnabled;
    private volatile bool _paused;
    private volatile bool _disposed;
    private int _transparencyPercent = DefaultTransparencyPercent;

    private sealed class RadarFrame
    {
        internal RadarFrame(RadarDefinition radar, List<RadarImageDecoder.CellRect> cells)
        {
            Radar = radar; Cells = cells;
        }

        internal RadarDefinition Radar { get; }
        internal List<RadarImageDecoder.CellRect> Cells { get; }
    }

    /// <summary>One complete observation, already converted to per-level vatSys polygons.</summary>
    private sealed class DisplayFrame
    {
        internal DisplayFrame(List<DisplayMaps.Map.Infill>[] levels) => Levels = levels;
        internal List<DisplayMaps.Map.Infill>[] Levels { get; }
    }

    internal RadarOverlayService()
    {
        var handler = new HttpClientHandler();
        try { handler.MaxConnectionsPerServer = MaxParallelDownloads; }
        catch (PlatformNotSupportedException) { /* Falls back to the default limit. */ }
        try { handler.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate; }
        catch (PlatformNotSupportedException) { }

        _http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(25) };
        try { _http.DefaultRequestHeaders.UserAgent.ParseAdd("WeatherRadarPlugin/1.1 (vatSys)"); }
        catch (FormatException) { }
    }

    // ---------------------------------------------------------------- public surface

    /// <summary>Turns the overlay on or off. Never throws; failures are reported to vatSys.</summary>
    internal async Task SetEnabledAsync(bool enabled)
    {
        if (_disposed) return;
        _enabled = enabled;

        StopRefreshTimer();
        var previous = Interlocked.Exchange(ref _work, null);
        previous?.Cancel();
        previous?.Dispose();

        if (!enabled)
        {
            SetAnimationEnabled(false);
            RunOnGui("hide overlay", () =>
            {
                ShowFrame(null);
                _history.Clear();
                _historyLoaded = false;
                _animationIndex = 0;
            });
            return;
        }

        var work = new CancellationTokenSource();
        Interlocked.Exchange(ref _work, work);
        var token = work.Token;

        if (!RunOnGuiChecked("overlay setup", EnsureMapsRegistered))
            throw new InvalidOperationException("The vatSys display could not be prepared for the radar overlay.");

        await RefreshAsync(token).ConfigureAwait(true);
        if (_disposed || !_enabled || token.IsCancellationRequested) return;

        if (!_historyLoaded)
            await PreloadHistoryAsync(token).ConfigureAwait(true);
        if (_disposed || !_enabled || token.IsCancellationRequested) return;

        StartRefreshTimer(token);
    }

    internal void SetAnimationEnabled(bool enabled)
    {
        if (_disposed) return;
        _animationEnabled = enabled && _enabled;
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
            if (_history.Count > 0) ShowFrame(_history[0]);
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

    internal void SetTransparency(int transparencyPercent)
    {
        if (_disposed) return;
        _transparencyPercent = Math.Max(0, Math.Min(100, transparencyPercent));
        RunOnGui("transparency", () =>
        {
            if (!WriteColours()) return;
            MMI.RequestRedraw(true, false, false);
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _enabled = false;
        _animationEnabled = false;

        StopRefreshTimer();
        var work = Interlocked.Exchange(ref _work, null);
        try { work?.Cancel(); } catch (ObjectDisposedException) { }
        work?.Dispose();

        RunOnGui("cleanup", () =>
        {
            StopAnimationTimer();
            var maps = Interlocked.Exchange(ref _maps, null);
            _history.Clear();
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

        try { _http.Dispose(); } catch { /* Never let teardown surface an error. */ }
        // _refreshLock is intentionally left to the GC: an in-flight refresh still has to
        // release it, and disposing underneath that would throw on a background thread.
    }

    // ---------------------------------------------------------------- download pipeline

    private async Task RefreshAsync(CancellationToken token)
    {
        if (_disposed || !_enabled || token.IsCancellationRequested) return;
        try { if (!await _refreshLock.WaitAsync(0, CancellationToken.None).ConfigureAwait(false)) return; }
        catch (Exception ex) { Report("radar refresh lock", ex); return; }
        try
        {
            var frames = new ConcurrentBag<RadarFrame>();
            var failures = new ConcurrentBag<string>();
            // The throttle bounds how many responses are decoded at once. Issuing all
            // sixty-seven requests up front would make the later ones age out against
            // HttpClient's timeout while they queue for a connection.
            using (var throttle = new SemaphoreSlim(MaxParallelDownloads, MaxParallelDownloads))
            {
                var tasks = RadarDefinition.All.Select(async radar =>
                {
                    try { await throttle.WaitAsync(token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                    try
                    {
                        var bytes = await DownloadAsync(radar.ImageUrl, token).ConfigureAwait(false);
                        frames.Add(new RadarFrame(radar, RadarImageDecoder.Decode(bytes, MaxCellsPerRadar)));
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        // One unavailable radar must never hold back the other sixty-six.
                        failures.Add($"{radar.Product} {radar.Name}: {ex.Message}");
                    }
                    finally
                    {
                        try { throttle.Release(); } catch (ObjectDisposedException) { }
                    }
                }).ToList();
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }

            if (token.IsCancellationRequested || _disposed || !_enabled) return;
            if (frames.IsEmpty)
                throw new InvalidOperationException(
                    "No BOM local radar images could be downloaded. " + failures.FirstOrDefault());

            var display = BuildDisplayFrame(frames.ToList());
            RunOnGui("radar refresh", () =>
            {
                if (_disposed || !_enabled) return;
                AppendFrame(display);
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Report("radar refresh", ex);
        }
        finally
        {
            try { _refreshLock.Release(); } catch (ObjectDisposedException) { }
        }
    }

    private async Task PreloadHistoryAsync(CancellationToken token)
    {
        try
        {
            var available = await Task.Run(() => ListHistoricalImages(token), token).ConfigureAwait(false);
            var wanted = MaxHistoryFrames - 1;
            var filesByRadar = RadarDefinition.All.ToDictionary(
                radar => radar.Product,
                radar => available
                    .Where(file => file.StartsWith(radar.Product + ".T.", StringComparison.Ordinal))
                    .OrderByDescending(file => file, StringComparer.Ordinal)
                    .Take(wanted).Reverse().ToArray(),
                StringComparer.Ordinal);

            var frameBags = Enumerable.Range(0, wanted).Select(_ => new ConcurrentBag<RadarFrame>()).ToArray();
            using (var throttle = new SemaphoreSlim(MaxParallelDownloads, MaxParallelDownloads))
            {
                var tasks = new List<Task>();
                foreach (var radar in RadarDefinition.All)
                {
                    if (!filesByRadar.TryGetValue(radar.Product, out var files)) continue;
                    for (var index = 0; index < files.Length && index < frameBags.Length; index++)
                    {
                        var frameIndex = index;
                        var file = files[index];
                        tasks.Add(Task.Run(async () =>
                        {
                            try
                            {
                                await throttle.WaitAsync(token).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) { return; }
                            try
                            {
                                var bytes = await DownloadAsync("http://www.bom.gov.au/radar/" + file, token)
                                    .ConfigureAwait(false);
                                frameBags[frameIndex].Add(
                                    new RadarFrame(radar, RadarImageDecoder.Decode(bytes, MaxCellsPerRadar)));
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

            if (token.IsCancellationRequested || _disposed || !_enabled) return;

            var built = frameBags.Where(bag => !bag.IsEmpty)
                .Select(bag => BuildDisplayFrame(bag.ToList())).ToList();

            RunOnGui("history apply", () =>
            {
                if (_disposed || !_enabled || built.Count == 0) return;
                // The current scan was already appended by the initial refresh; keep it as
                // the newest frame and slot the older observations in underneath it.
                var latest = _history.Count > 0 ? _history[_history.Count - 1] : null;
                _history.Clear();
                _history.AddRange(built);
                if (latest is not null) _history.Add(latest);
                while (_history.Count > MaxHistoryFrames) _history.RemoveAt(0);
                _historyLoaded = _history.Count > 1;
                if (!_animationEnabled) ShowLatestFrame();
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Report("radar history preload", ex);
        }
    }

    private async Task<byte[]> DownloadAsync(string url, CancellationToken token)
    {
        using var response = await _http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var declared = response.Content.Headers.ContentLength;
        if (declared > MaxImageBytes)
            throw new InvalidDataException($"BOM radar response for {url} was {declared} bytes.");

        var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        if (bytes.Length > MaxImageBytes)
            throw new InvalidDataException($"BOM radar response for {url} was {bytes.Length} bytes.");
        return bytes;
    }

    private static string[] ListHistoricalImages(CancellationToken token)
    {
#pragma warning disable SYSLIB0014
        var request = (FtpWebRequest)WebRequest.Create("ftp://ftp.bom.gov.au/anon/gen/radar/");
#pragma warning restore SYSLIB0014
        request.Method = WebRequestMethods.Ftp.ListDirectory;
        request.Credentials = new NetworkCredential("anonymous", "WeatherRadarPlugin@localhost");
        request.Timeout = 30000;
        request.ReadWriteTimeout = 30000;

        using var registration = token.Register(() => { try { request.Abort(); } catch { } });
        using var response = (FtpWebResponse)request.GetResponse();
        using var stream = response.GetResponseStream()
            ?? throw new InvalidDataException("The BOM radar directory listing was empty.");
        using var reader = new StreamReader(stream);
        var listing = reader.ReadToEnd();
        token.ThrowIfCancellationRequested();
        return Regex.Matches(listing, @"IDR\d{2,4}3\.T\.\d{12}\.png",
                RegexOptions.None, TimeSpan.FromSeconds(10))
            .Cast<Match>().Select(match => match.Value).Distinct(StringComparer.Ordinal).ToArray();
    }

    // ---------------------------------------------------------------- frame construction

    /// <summary>
    /// Converts decoded cells into vatSys polygons. This allocates the bulk of the plugin's
    /// memory and runs entirely on a background thread — it touches no vatSys shared state.
    /// </summary>
    private static DisplayFrame BuildDisplayFrame(List<RadarFrame> frames)
    {
        var levels = new List<DisplayMaps.Map.Infill>[RadarImageDecoder.Palette.Length];
        for (var index = 0; index < levels.Length; index++) levels[index] = new List<DisplayMaps.Map.Infill>();

        var budget = MaxInfillsPerFrame;
        foreach (var frame in frames)
        {
            foreach (var cell in frame.Cells)
            {
                if (budget-- <= 0)
                {
                    // Truncating is a last-resort memory guard, and it hides real weather,
                    // so it is never allowed to happen quietly.
                    Report("radar detail", new InvalidOperationException(
                        $"More than {MaxInfillsPerFrame} radar cells were returned; " +
                        "the weakest returns in the remaining radars were not drawn."));
                    return new DisplayFrame(levels);
                }
                var level = Math.Min(Math.Max(cell.Level, 0), levels.Length - 1);
                levels[level].Add(CreateCellInfill(frame.Radar, cell));
            }
        }
        return new DisplayFrame(levels);
    }

    private static DisplayMaps.Map.Infill CreateCellInfill(RadarDefinition radar, RadarImageDecoder.CellRect cell)
    {
        var left = cell.Column * RadarImageDecoder.SamplePixels;
        var right = cell.ColumnEnd * RadarImageDecoder.SamplePixels;
        var top = cell.Row * RadarImageDecoder.SamplePixels;
        var bottom = cell.RowEnd * RadarImageDecoder.SamplePixels;
        var infill = new DisplayMaps.Map.Infill
        {
            Type = DisplayMaps.Map.InfillTypes.Normal,
            Pattern = DisplayMaps.Map.Patterns.Solid
        };
        infill.Points.Add(ToCoordinate(radar, left, top));
        infill.Points.Add(ToCoordinate(radar, right, top));
        infill.Points.Add(ToCoordinate(radar, right, bottom));
        infill.Points.Add(ToCoordinate(radar, left, bottom));
        return infill;
    }

    // BOM coordinate files define 0=bottom-left, 1=top-left, 2=top-right, 3=bottom-right.
    private static Coordinate ToCoordinate(RadarDefinition radar, double pixelX, double pixelY)
    {
        var u = pixelX / RadarImageDecoder.PlotSize;
        var v = pixelY / RadarImageDecoder.PlotSize;
        var topLat = Lerp(radar.Lat1, radar.Lat2, u);
        var topLon = Lerp(radar.Lon1, radar.Lon2, u);
        var bottomLat = Lerp(radar.Lat0, radar.Lat3, u);
        var bottomLon = Lerp(radar.Lon0, radar.Lon3, u);
        return new Coordinate(Lerp(topLat, bottomLat, v), Lerp(topLon, bottomLon, v));
    }

    private static double Lerp(double start, double end, double amount) => start + (end - start) * amount;

    // ---------------------------------------------------------------- GUI-thread display

    private void AppendFrame(DisplayFrame frame)
    {
        _history.Add(frame);
        while (_history.Count > MaxHistoryFrames) _history.RemoveAt(0);
        // Registration can fail if rendering could not be paused during setup, and an ASD
        // window opened since then may not have the maps in its visible set yet.
        if (!EnsureMapsRegistered()) return;
        if (_maps is not null) MakeMapsVisible(_maps);
        if (!_animationEnabled) ShowLatestFrame();
    }

    private void ShowLatestFrame()
    {
        if (_enabled && _history.Count > 0) ShowFrame(_history[_history.Count - 1]);
    }

    private void AnimationTick(object? sender, EventArgs e)
    {
        // Fires on the GUI thread, so a throw here would reach vatSys's message loop.
        try
        {
            if (_disposed || !_enabled || !_animationEnabled || _paused) return;
            if (_history.Count < 2) return;
            _animationIndex++;
            // One extra interval on the newest observation before looping.
            if (_animationIndex > _history.Count) _animationIndex = 0;
            ShowFrame(_history[Math.Min(_animationIndex, _history.Count - 1)]);
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
    private void ShowFrame(DisplayFrame? frame)
    {
        var maps = _maps;
        if (maps is null) return;

        using (var suspension = RenderSuspension.Acquire(DisplaySuspendTimeout))
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
                foreach (var map in maps)
                {
                    DisplayMaps.Maps.Add(map);
                    foreach (var position in LogicalPositions.Positions)
                        if (!position.Maps.Contains(map)) position.Maps.Add(map);
                }
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
        var timer = new System.Threading.Timer(_ =>
        {
            try { _ = RefreshAsync(token); }
            catch (Exception ex) { Report("radar refresh schedule", ex); }
        }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        var previous = Interlocked.Exchange(ref _refreshTimer, timer);
        previous?.Dispose();

        // Disposal or a disable can win the race with timer creation; drop the timer we just
        // installed rather than leaving it firing against a dead service.
        if (_disposed || !_enabled || token.IsCancellationRequested)
        {
            StopRefreshTimer();
            return;
        }
        try { timer.Change(RefreshInterval, RefreshInterval); }
        catch (ObjectDisposedException) { }
    }

    private void StopRefreshTimer()
    {
        var timer = Interlocked.Exchange(ref _refreshTimer, null);
        try { timer?.Dispose(); } catch { }
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
            MMI.InvokeOnGUI(Guarded);
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
