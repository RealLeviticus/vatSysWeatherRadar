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

    /// <summary>
    /// A horizontal run of image pixels that all share one reflectivity level. Radar
    /// images are mostly long runs, so this holds a whole image in a few kilobytes.
    /// </summary>
    internal readonly struct RainRun
    {
        internal RainRun(int row, int start, int end, int level)
        {
            Row = (short)row; Start = (short)start; End = (short)end; Level = (sbyte)level;
        }

        internal short Row { get; }
        /// <summary>First pixel column of the run.</summary>
        internal short Start { get; }
        /// <summary>One past the last pixel column of the run.</summary>
        internal short End { get; }
        internal sbyte Level { get; }
    }

    /// <summary>
    /// Reads every rain pixel of a BOM radar image at full resolution. Nothing is sampled
    /// or merged here: combining radars and reducing to display cells happens in
    /// <see cref="RadarMosaic"/>, which needs each pixel's own position to do it.
    /// The result is bounded by the plot size, at most 512 rows of 256 runs.
    /// </summary>
    internal static List<RainRun> Decode(byte[] imageBytes)
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

        var runs = new List<RainRun>();
        // LockBits converts any source pixel format to 32bpp ARGB in one pass. GetPixel
        // would issue 262,144 individual GDI+ calls per radar, which alone was enough to
        // saturate a core for minutes across the radar network.
        var region = new Rectangle(offset, offset, PlotSize, PlotSize);
        var data = bitmap.LockBits(region, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new int[PlotSize];
            for (var y = 0; y < PlotSize; y++)
            {
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, PlotSize);
                // A fully transparent pixel is the correct seed: LevelOf(0) is also -1.
                var lastArgb = 0;
                var lastLevel = -1;
                var runStart = 0;
                var runLevel = -1;
                for (var x = 0; x < PlotSize; x++)
                {
                    var argb = row[x];
                    // Memoising the previous pixel avoids most of the palette lookups.
                    if (argb != lastArgb)
                    {
                        lastArgb = argb;
                        lastLevel = LevelOf(argb);
                    }
                    if (lastLevel == runLevel) continue;
                    if (runLevel >= 0) runs.Add(new RainRun(y, runStart, x, runLevel));
                    runStart = x;
                    runLevel = lastLevel;
                }
                if (runLevel >= 0) runs.Add(new RainRun(y, runStart, PlotSize, runLevel));
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return runs;
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
