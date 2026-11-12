using System;
using System.Collections.Generic;

namespace WeatherRadarPlugin;

/// <summary>One radar's decoded image.</summary>
internal sealed class RadarScan
{
    internal RadarScan(RadarDefinition radar, List<RadarImageDecoder.RainRun> runs)
    {
        Radar = radar; Runs = runs;
    }

    internal RadarDefinition Radar { get; }
    internal List<RadarImageDecoder.RainRun> Runs { get; }
}

/// <summary>
/// Merges every radar into one national picture.
///
/// Neighbouring 256 km images overlap heavily, and drawing each radar's rain separately
/// would paint the same shower two or three times over, which with translucent fills looks
/// denser than it is. Instead every rain pixel from every radar is dropped into one
/// latitude/longitude grid, each cell keeps the strongest return any radar saw there, and
/// rectangles are built from the grid once. A radar that sees rain weakly at long range
/// therefore never hides a closer radar's stronger return, and the reverse.
///
/// Instances reuse one grid buffer and are not thread safe; the owner serialises access.
/// </summary>
internal sealed class RadarMosaic
{
    // About 2 km north-south everywhere, and east-west 2.0 km at 35°S, 2.4 km at Darwin
    // and 1.7 km south of Tasmania. Image pixels are 1 km apart, so every cell under a
    // rain area receives at least one pixel and solid rain stays solid.
    internal const double LatStep = 0.018;
    internal const double LonStep = 0.022;

    // A merged rectangle is drawn with straight edges between its corners, while lines of
    // latitude curve on the display. Capping rectangles at about 128 km keeps that
    // disagreement under 300 m, well inside a cell.
    private const int MaxRectangleSpanKm = 128;
    private const int CellKm = 2;

    private static readonly int[] Scales = { 1, 2, 4 };

    internal static readonly double North;
    internal static readonly double West;
    internal static readonly int Rows;
    internal static readonly int Columns;

    // Each radar image's footprint, so a vis-limited picture can skip radars that cannot
    // reach the vis area without converting any of their pixels.
    private static readonly Dictionary<RadarDefinition, (double North, double South, double West, double East)> Footprints = new();

    static RadarMosaic()
    {
        double north = -90, south = 90, west = 180, east = -180;
        // The edges of an image are great circles, so in the southern hemisphere each one
        // bows towards the pole between its ends. Corners and edge midpoints together
        // bound the footprint.
        var fractions = new[] { 0d, 0.5d, 1d };
        foreach (var radar in RadarDefinition.All)
        {
            double n = -90, s = 90, w = 180, e = -180;
            foreach (var u in fractions)
            {
                foreach (var v in fractions)
                {
                    radar.PixelToLatLon(u * RadarImageDecoder.PlotSize, v * RadarImageDecoder.PlotSize,
                        out var latitude, out var longitude);
                    n = Math.Max(n, latitude); s = Math.Min(s, latitude);
                    w = Math.Min(w, longitude); e = Math.Max(e, longitude);
                }
            }
            // A little slack for the bow between the sampled points.
            Footprints[radar] = (n + 0.1, s - 0.1, w - 0.1, e + 0.1);
            north = Math.Max(north, n); south = Math.Min(south, s);
            west = Math.Min(west, w); east = Math.Max(east, e);
        }
        North = north + 2 * LatStep;
        West = west - 2 * LonStep;
        Rows = (int)Math.Ceiling((North - south) / LatStep) + 2;
        Columns = (int)Math.Ceiling((east - West) / LonStep) + 2;
    }

    /// <summary>A block of grid cells that all share one reflectivity level.</summary>
    internal readonly struct Rect
    {
        internal Rect(int level, int row, int column, int rowEnd, int columnEnd)
        {
            Level = level; Row = row; Column = column; RowEnd = rowEnd; ColumnEnd = columnEnd;
        }

        internal int Level { get; }
        internal int Row { get; }
        internal int Column { get; }
        internal int RowEnd { get; }
        internal int ColumnEnd { get; }
    }

    internal sealed class Picture
    {
        internal Picture(List<Rect> rectangles, int scale, bool truncated)
        {
            Rectangles = rectangles; Scale = scale; Truncated = truncated;
        }

        internal List<Rect> Rectangles { get; }
        /// <summary>Grid cells per rectangle unit: 1 is 2 km cells, 2 is 4 km, 4 is 8 km.</summary>
        internal int Scale { get; }
        /// <summary>True when even the coarsest grid was over the limit and weak rain was dropped.</summary>
        internal bool Truncated { get; }

        internal double NorthOf(Rect rect) => North - rect.Row * Scale * LatStep;
        internal double SouthOf(Rect rect) => North - rect.RowEnd * Scale * LatStep;
        internal double WestOf(Rect rect) => West + rect.Column * Scale * LonStep;
        internal double EastOf(Rect rect) => West + rect.ColumnEnd * Scale * LonStep;
    }

    // 0 is no rain, otherwise reflectivity level + 1. Sized for the finest grid.
    private readonly byte[] _cells = new byte[Rows * Columns];
    private int _rows, _columns;
    private int _minRow, _maxRow, _minColumn, _maxColumn;

    /// <summary>
    /// Builds the merged picture in at most <paramref name="maxRectangles"/> rectangles.
    /// On a very wet day the 2 km grid can need more than that, so the grid is coarsened to
    /// 4 km and then 8 km cells, each still holding the strongest return within it. That
    /// makes rain look blockier but never removes any. Only if 8 km cells are still too many
    /// are the weakest levels dropped, and the caller is told.
    ///
    /// With an <paramref name="area"/>, only rain inside its vis circles is placed, so the
    /// picture ends at the edge of what the controller can see. Null means every radar.
    /// </summary>
    internal Picture Build(IReadOnlyList<RadarScan> scans, int maxRectangles, VisArea? area = null)
    {
        foreach (var scale in Scales)
        {
            Fill(scans, scale, area);
            var rectangles = Merge(scale, maxRectangles);
            if (rectangles is not null) return new Picture(rectangles, scale, truncated: false);
        }

        var coarsest = Scales[Scales.Length - 1];
        Fill(scans, coarsest, area);
        var all = Merge(coarsest, int.MaxValue)!;
        // Stable by construction: strongest first, and within a level the scan order.
        var kept = new List<Rect>(maxRectangles);
        for (var level = RadarImageDecoder.Palette.Length - 1; level >= 0 && kept.Count < maxRectangles; level--)
        {
            foreach (var rect in all)
            {
                if (rect.Level != level) continue;
                if (kept.Count >= maxRectangles) break;
                kept.Add(rect);
            }
        }
        return new Picture(kept, coarsest, truncated: true);
    }

    private void Fill(IReadOnlyList<RadarScan> scans, int scale, VisArea? area)
    {
        Array.Clear(_cells, 0, _cells.Length);
        _rows = (Rows + scale - 1) / scale;
        _columns = (Columns + scale - 1) / scale;
        _minRow = int.MaxValue; _maxRow = -1; _minColumn = int.MaxValue; _maxColumn = -1;

        var latStep = LatStep * scale;
        var lonStep = LonStep * scale;

        // For a vis-limited picture: per grid row, the longitude span each vis circle covers
        // at that row's middle latitude. A cell is kept when its pixel falls in any span.
        var circles = area?.CentreCount ?? 0;
        double[]? spanWest = null, spanEast = null;
        if (area is not null)
        {
            spanWest = new double[_rows * circles];
            spanEast = new double[_rows * circles];
            for (var row = 0; row < _rows; row++)
                area.SpansAt(North - (row + 0.5) * latStep, spanWest, spanEast, row * circles);
        }

        foreach (var scan in scans)
        {
            var radar = scan.Radar;
            if (area is not null && Footprints.TryGetValue(radar, out var footprint) &&
                !area.Overlaps(footprint.North, footprint.South, footprint.West, footprint.East))
                continue;

            foreach (var run in scan.Runs)
            {
                var value = (byte)(run.Level + 1);
                var pixelY = run.Row + 0.5;
                for (int x = run.Start; x < run.End; x++)
                {
                    radar.PixelToLatLon(x + 0.5, pixelY, out var latitude, out var longitude);
                    var row = (int)((North - latitude) / latStep);
                    var column = (int)((longitude - West) / lonStep);
                    if (row < 0 || row >= _rows || column < 0 || column >= _columns) continue;
                    if (spanWest is not null && !InSpans(spanWest, spanEast!, row * circles, circles, longitude)) continue;

                    var index = row * _columns + column;
                    if (value <= _cells[index]) continue;
                    _cells[index] = value;
                    if (row < _minRow) _minRow = row;
                    if (row > _maxRow) _maxRow = row;
                    if (column < _minColumn) _minColumn = column;
                    if (column > _maxColumn) _maxColumn = column;
                }
            }
        }
    }

    /// <summary>
    /// Covers the filled cells with same-level rectangles, greedily: the longest run to the
    /// right, then as far down as that whole run repeats. Consumes the grid as it goes.
    /// Returns null as soon as the limit is passed.
    /// </summary>
    private List<Rect>? Merge(int scale, int maxRectangles)
    {
        var rectangles = new List<Rect>();
        if (_maxRow < 0) return rectangles;

        var maxSpan = Math.Max(1, MaxRectangleSpanKm / (CellKm * scale));
        for (var row = _minRow; row <= _maxRow; row++)
        {
            var rowStart = row * _columns;
            for (var column = _minColumn; column <= _maxColumn; column++)
            {
                var value = _cells[rowStart + column];
                if (value == 0) continue;
                if (rectangles.Count >= maxRectangles) return null;

                var columnEnd = column + 1;
                while (columnEnd <= _maxColumn && columnEnd - column < maxSpan &&
                       _cells[rowStart + columnEnd] == value)
                    columnEnd++;

                var rowEnd = row + 1;
                while (rowEnd <= _maxRow && rowEnd - row < maxSpan && RowMatches(rowEnd, column, columnEnd, value))
                    rowEnd++;

                for (var r = row; r < rowEnd; r++)
                    Array.Clear(_cells, r * _columns + column, columnEnd - column);

                rectangles.Add(new Rect(value - 1, row, column, rowEnd, columnEnd));
            }
        }
        return rectangles;
    }

    private static bool InSpans(double[] west, double[] east, int offset, int count, double longitude)
    {
        for (var index = offset; index < offset + count; index++)
            if (longitude >= west[index] && longitude <= east[index]) return true;
        return false;
    }

    private bool RowMatches(int row, int column, int columnEnd, byte value)
    {
        var offset = row * _columns;
        for (var c = column; c < columnEnd; c++)
            if (_cells[offset + c] != value) return false;
        return true;
    }
}
