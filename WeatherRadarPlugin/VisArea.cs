using System;
using System.Collections.Generic;

namespace WeatherRadarPlugin;

/// <summary>
/// The part of the map the controller can see on the network: up to four vis centres,
/// each with the same radius. Matches the circles vatSys itself draws in its Vis Centres
/// window, whose radius is the smaller of the chosen visibility range and the facility's
/// radio range.
///
/// Immutable, and pure arithmetic, so it can be built on the GUI thread and used from a
/// background picture build.
/// </summary>
internal sealed class VisArea
{
    private const double EarthRadiusNm = 3440.065;

    // A change smaller than this is not worth rebuilding every picture for.
    private const double SameCentreNm = 1.0;

    private readonly double[] _latitudes;
    private readonly double[] _longitudes;
    private readonly double _cosRadius;

    internal VisArea(IReadOnlyList<(double Latitude, double Longitude)> centres, double radiusNm)
    {
        if (centres is null || centres.Count == 0) throw new ArgumentException("At least one centre is needed.", nameof(centres));
        if (!(radiusNm > 0)) throw new ArgumentOutOfRangeException(nameof(radiusNm));
        _latitudes = new double[centres.Count];
        _longitudes = new double[centres.Count];
        for (var index = 0; index < centres.Count; index++)
        {
            _latitudes[index] = centres[index].Latitude;
            _longitudes[index] = centres[index].Longitude;
        }
        RadiusNm = radiusNm;
        _cosRadius = Math.Cos(Math.Min(Math.PI, radiusNm / EarthRadiusNm));

        // A box around every circle, for skipping radars that cannot contribute.
        var radiusDegrees = radiusNm / EarthRadiusNm * 180 / Math.PI;
        North = double.MinValue; South = double.MaxValue; West = double.MaxValue; East = double.MinValue;
        for (var index = 0; index < _latitudes.Length; index++)
        {
            var north = Math.Min(90, _latitudes[index] + radiusDegrees);
            var south = Math.Max(-90, _latitudes[index] - radiusDegrees);
            var widest = Math.Max(Math.Abs(north), Math.Abs(south));
            var halfWidth = widest >= 89 ? 180 : Math.Min(180, radiusDegrees / Math.Cos(widest * Math.PI / 180));
            North = Math.Max(North, north);
            South = Math.Min(South, south);
            West = Math.Min(West, _longitudes[index] - halfWidth);
            East = Math.Max(East, _longitudes[index] + halfWidth);
        }
    }

    internal int CentreCount => _latitudes.Length;
    internal double RadiusNm { get; }
    internal double North { get; }
    internal double South { get; }
    internal double West { get; }
    internal double East { get; }

    internal bool Overlaps(double north, double south, double west, double east) =>
        north >= South && south <= North && east >= West && west <= East;

    /// <summary>
    /// For one line of latitude, the longitude span each circle covers there, written into
    /// <paramref name="west"/> and <paramref name="east"/> at <paramref name="offset"/> + centre
    /// index. A circle that does not reach that latitude gets an empty span (west > east).
    /// </summary>
    internal void SpansAt(double latitude, double[] west, double[] east, int offset)
    {
        var phi = latitude * Math.PI / 180;
        double sinPhi = Math.Sin(phi), cosPhi = Math.Cos(phi);
        for (var index = 0; index < _latitudes.Length; index++)
        {
            var phiC = _latitudes[index] * Math.PI / 180;
            var denominator = cosPhi * Math.Cos(phiC);
            double halfWidth;
            if (denominator <= 1e-12)
            {
                // At a pole every longitude is the same point.
                halfWidth = Math.Abs(latitude - _latitudes[index]) * Math.PI / 180 <= Math.Acos(_cosRadius) ? 180 : -1;
            }
            else
            {
                // Spherical law of cosines solved for the longitude difference.
                var cosDelta = (_cosRadius - sinPhi * Math.Sin(phiC)) / denominator;
                halfWidth = cosDelta > 1 ? -1 : cosDelta < -1 ? 180 : Math.Acos(cosDelta) * 180 / Math.PI;
            }
            if (halfWidth < 0)
            {
                west[offset + index] = 1;
                east[offset + index] = -1;
            }
            else
            {
                west[offset + index] = _longitudes[index] - halfWidth;
                east[offset + index] = _longitudes[index] + halfWidth;
            }
        }
    }

    /// <summary>Whether a point lies inside any circle. Exact, and slower than <see cref="SpansAt"/>.</summary>
    internal bool Contains(double latitude, double longitude)
    {
        var phi = latitude * Math.PI / 180;
        for (var index = 0; index < _latitudes.Length; index++)
        {
            var phiC = _latitudes[index] * Math.PI / 180;
            var deltaLambda = (longitude - _longitudes[index]) * Math.PI / 180;
            var cosDistance = Math.Sin(phi) * Math.Sin(phiC) + Math.Cos(phi) * Math.Cos(phiC) * Math.Cos(deltaLambda);
            if (cosDistance >= _cosRadius) return true;
        }
        return false;
    }

    /// <summary>True when the two areas are close enough that the picture need not change.</summary>
    internal static bool Same(VisArea? a, VisArea? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a._latitudes.Length != b._latitudes.Length) return false;
        if (Math.Abs(a.RadiusNm - b.RadiusNm) > 0.5) return false;
        for (var index = 0; index < a._latitudes.Length; index++)
        {
            if (DistanceNm(a._latitudes[index], a._longitudes[index], b._latitudes[index], b._longitudes[index]) > SameCentreNm)
                return false;
        }
        return true;
    }

    internal static double DistanceNm(double lat1, double lon1, double lat2, double lon2)
    {
        double p1 = lat1 * Math.PI / 180, p2 = lat2 * Math.PI / 180;
        double dp = p2 - p1, dl = (lon2 - lon1) * Math.PI / 180;
        var h = Math.Sin(dp / 2) * Math.Sin(dp / 2) + Math.Cos(p1) * Math.Cos(p2) * Math.Sin(dl / 2) * Math.Sin(dl / 2);
        return 2 * EarthRadiusNm * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    public override string ToString() => $"{CentreCount} vis centre(s), {RadiusNm:F0} nm";
}
