using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace WeatherRadarPlugin;

internal static class RadarImageDecoder
{
    internal const int PlotSize = 512;
    internal const int PlotOffset = 6;
    internal const int SamplePixels = 4;
    internal const int GridSize = PlotSize / SamplePixels;

    // A BOM local radar image is 524x564 at most. Anything far larger is not a
    // radar product and is rejected before it can be locked into memory.
    private const int MaxImageDimension = 2048;

    // Colours used by the BOM legacy radar GIF reflectivity scale, weakest to strongest.
    internal static readonly Color[] Palette =
    {
        Color.FromArgb(245, 245, 255), Color.FromArgb(180, 180, 255),
        Color.FromArgb(120, 120, 255), Color.FromArgb(20, 20, 255),
        Color.FromArgb(0, 216, 195), Color.FromArgb(0, 150, 144),
        Color.FromArgb(0, 102, 102), Color.FromArgb(255, 255, 0),
        Color.FromArgb(255, 200, 0), Color.FromArgb(255, 150, 0),
        Color.FromArgb(255, 100, 0), Color.FromArgb(255, 0, 0),
        Color.FromArgb(200, 0, 0), Color.FromArgb(120, 0, 0),
        Color.FromArgb(40, 0, 0)
    };

    private static readonly Dictionary<int, int> LevelByRgb = BuildLevelLookup();

    /// <summary>An axis-aligned block of grid cells that all share one reflectivity level.</summary>
    internal readonly struct CellRect
    {
        internal CellRect(int level, int row, int column, int rowEnd, int columnEnd)
        {
            Level = level; Row = row; Column = column; RowEnd = rowEnd; ColumnEnd = columnEnd;
        }

        internal int Level { get; }
        internal int Row { get; }
        internal int Column { get; }
        internal int RowEnd { get; }
        internal int ColumnEnd { get; }
    }

    /// <summary>
    /// Samples a BOM radar image into the 2 km display grid, keeping the strongest return in
    /// each cell, then merges same-level cells into the largest rectangles that fit. Merging
    /// never changes which cells are painted, only how many polygons vatSys has to hold.
    /// </summary>
    /// <param name="maxCells">Hard ceiling on emitted rectangles, bounding worst-case memory.</param>
    internal static List<CellRect> Decode(byte[] imageBytes, int maxCells)
    {
        if (imageBytes is null || imageBytes.Length == 0)
            throw new InvalidDataException("BOM radar response was empty.");

        using var stream = new MemoryStream(imageBytes, writable: false);
        using var bitmap = new Bitmap(stream);

        if (bitmap.Width > MaxImageDimension || bitmap.Height > MaxImageDimension)
            throw new InvalidDataException($"BOM radar image {bitmap.Width}x{bitmap.Height} is larger than expected.");

        // BOM's "latest" GIF layout insets the plot by six pixels; the timestamped PNG
        // history layout does not. Pick whichever inset the image can actually satisfy.
        var offset = bitmap.Width >= PlotOffset + PlotSize && bitmap.Height >= PlotOffset + PlotSize
            ? PlotOffset
            : 0;
        if (bitmap.Width < offset + PlotSize || bitmap.Height < offset + PlotSize)
            throw new InvalidDataException($"Unexpected BOM radar image size {bitmap.Width}x{bitmap.Height}.");

        return MergeCells(SampleGrid(bitmap, offset), maxCells);
    }

    private static int[] SampleGrid(Bitmap bitmap, int offset)
    {
        var grid = new int[GridSize * GridSize];
        for (var index = 0; index < grid.Length; index++) grid[index] = -1;

        // LockBits converts any source pixel format to 32bpp ARGB in one pass. GetPixel
        // would issue 262,144 individual GDI+ calls per radar, which alone was enough to
        // saturate a core for minutes across 67 radars.
        var region = new Rectangle(offset, offset, PlotSize, PlotSize);
        var data = bitmap.LockBits(region, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new int[PlotSize];
            // A fully transparent pixel is the correct seed: LevelOf(0) is also -1.
            var lastArgb = 0;
            var lastLevel = -1;
            for (var y = 0; y < PlotSize; y++)
            {
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, PlotSize);
                var gridRow = (y / SamplePixels) * GridSize;
                for (var x = 0; x < PlotSize; x++)
                {
                    var argb = row[x];
                    // Radar images are long runs of one colour, so memoising the previous
                    // pixel avoids most of the per-pixel palette lookups.
                    if (argb != lastArgb)
                    {
                        lastArgb = argb;
                        lastLevel = LevelOf(argb);
                    }
                    if (lastLevel < 0) continue;
                    var cell = gridRow + x / SamplePixels;
                    if (lastLevel > grid[cell]) grid[cell] = lastLevel;
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return grid;
    }

    private static List<CellRect> MergeCells(int[] grid, int maxCells)
    {
        var rects = new List<CellRect>();
        var consumed = new bool[grid.Length];
        for (var row = 0; row < GridSize; row++)
        {
            for (var column = 0; column < GridSize; column++)
            {
                var origin = row * GridSize + column;
                if (consumed[origin]) continue;
                var level = grid[origin];
                if (level < 0) continue;
                if (rects.Count >= maxCells) return rects;

                var columnEnd = column + 1;
                while (columnEnd < GridSize && !consumed[row * GridSize + columnEnd] &&
                       grid[row * GridSize + columnEnd] == level)
                    columnEnd++;

                var rowEnd = row + 1;
                while (rowEnd < GridSize && RowMatches(grid, consumed, rowEnd, column, columnEnd, level))
                    rowEnd++;

                for (var r = row; r < rowEnd; r++)
                    for (var c = column; c < columnEnd; c++)
                        consumed[r * GridSize + c] = true;

                rects.Add(new CellRect(level, row, column, rowEnd, columnEnd));
            }
        }
        return rects;
    }

    private static bool RowMatches(int[] grid, bool[] consumed, int row, int column, int columnEnd, int level)
    {
        var offset = row * GridSize;
        for (var c = column; c < columnEnd; c++)
            if (consumed[offset + c] || grid[offset + c] != level) return false;
        return true;
    }

    private static int LevelOf(int argb)
    {
        if ((argb & unchecked((int)0xFF000000)) == 0) return -1;
        return LevelByRgb.TryGetValue(argb & 0x00FFFFFF, out var level) ? level : -1;
    }

    private static Dictionary<int, int> BuildLevelLookup()
    {
        var lookup = new Dictionary<int, int>(Palette.Length);
        for (var index = 0; index < Palette.Length; index++)
        {
            var colour = Palette[index];
            lookup[(colour.R << 16) | (colour.G << 8) | colour.B] = index;
        }
        return lookup;
    }
}
