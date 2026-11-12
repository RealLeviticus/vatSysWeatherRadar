using System;
using System.Collections.Generic;

namespace WeatherRadarPlugin;

/// <summary>
/// One BOM radar and where its 256 km image sits on the Earth.
///
/// A BOM radar image is an axis-aligned square in a gnomonic plane centred on the radar:
/// straight lines from the radar are straight in the image, and one pixel is one kilometre.
/// The square is not quite centred on the radar, so its edges are carried here as
/// kilometres east and north of the radar. They are derived from the corner coordinates
/// BOM publishes for each product in
/// ftp://ftp.bom.gov.au/anon/gen/radar_transparencies/coordinates/IDRxx2.map.
///
/// The 256 km product is used, not the 128 km one, because neighbouring radars are often
/// more than 256 km apart: at 128 km a quarter of Tasmania, including Launceston, is
/// outside every image.
/// </summary>
internal sealed class RadarDefinition
{
    internal const double EarthRadiusKm = 6371.0;

    private readonly double _sinCentreLat;
    private readonly double _cosCentreLat;
    private readonly double _centreLonRadians;

    internal RadarDefinition(string product, string name, double centreLat, double centreLon,
        double westKm, double eastKm, double southKm, double northKm)
    {
        Product = product; Name = name;
        CentreLat = centreLat; CentreLon = centreLon;
        WestKm = westKm; EastKm = eastKm; SouthKm = southKm; NorthKm = northKm;

        var centreLatRadians = centreLat * Math.PI / 180d;
        _sinCentreLat = Math.Sin(centreLatRadians);
        _cosCentreLat = Math.Cos(centreLatRadians);
        _centreLonRadians = centreLon * Math.PI / 180d;
    }

    internal string Product { get; }
    internal string Name { get; }
    internal double CentreLat { get; }
    internal double CentreLon { get; }
    internal double WestKm { get; }
    internal double EastKm { get; }
    internal double SouthKm { get; }
    internal double NorthKm { get; }

    /// <summary>
    /// Latitude and longitude, in degrees, of a point in the radar's image. Pixel
    /// coordinates run from 0 to <see cref="RadarImageDecoder.PlotSize"/>, x eastwards and
    /// y southwards, and may be fractional: the centre of pixel (10, 20) is (10.5, 20.5).
    /// </summary>
    internal void PixelToLatLon(double pixelX, double pixelY, out double latitude, out double longitude)
    {
        var east = WestKm + (EastKm - WestKm) * pixelX / RadarImageDecoder.PlotSize;
        var north = NorthKm - (NorthKm - SouthKm) * pixelY / RadarImageDecoder.PlotSize;

        // Inverse gnomonic projection. With h the distance from the Earth's centre to the
        // point on the tangent plane, the usual trigonometric form reduces to this.
        var h = Math.Sqrt(east * east + north * north + EarthRadiusKm * EarthRadiusKm);
        latitude = Math.Asin((EarthRadiusKm * _sinCentreLat + north * _cosCentreLat) / h) * 180d / Math.PI;
        longitude = (_centreLonRadians +
            Math.Atan2(east, EarthRadiusKm * _cosCentreLat - north * _sinCentreLat)) * 180d / Math.PI;
    }

    internal static readonly IReadOnlyList<RadarDefinition> All = new RadarDefinition[]
    {
        new("IDR012", "Broadmeadows", -37.69100, 144.94600, -254.69, 257.42, -254.88, 257.23),
        new("IDR022", "Melbourne", -37.85200, 144.75200, -255.38, 257.02, -255.36, 257.04),
        new("IDR032", "Wollongong", -34.26390, 150.87390, -255.36, 257.29, -255.47, 257.19),
        new("IDR042", "Newcastle", -32.73170, 152.02499, -255.10, 257.29, -254.76, 257.63),
        new("IDR062", "Geraldton", -28.80000, 114.70000, -255.38, 257.38, -255.31, 257.44),
        new("IDR072", "Wyndham", -15.45300, 128.11900, -255.28, 256.99, -255.38, 256.89),
        new("IDR082", "Gympie", -25.96700, 152.58299, -255.17, 257.26, -255.18, 257.25),
        new("IDR102", "Darwin Airport", -12.42500, 130.89101, -255.16, 257.13, -255.32, 256.98),
        new("IDR142", "Mount Gambier", -37.75000, 140.78000, -255.47, 257.29, -255.23, 257.53),
        new("IDR152", "Dampier", -20.65000, 116.68700, -255.51, 257.25, -255.22, 257.54),
        new("IDR162", "Port Hedland", -20.37190, 118.63170, -255.43, 256.71, -254.99, 257.15),
        new("IDR172", "Broome", -17.94500, 122.22500, -255.31, 257.24, -255.57, 256.99),
        new("IDR192", "Cairns", -16.81700, 145.68300, -255.31, 257.20, -255.51, 257.00),
        new("IDR222", "Mackay", -21.11700, 149.21700, -255.08, 257.03, -255.14, 256.97),
        new("IDR232", "Gladstone", -23.85000, 151.26700, -254.83, 257.31, -255.05, 257.09),
        new("IDR242", "Bowen", -19.88600, 148.07500, -255.36, 257.06, -254.99, 257.44),
        new("IDR252", "Alice Springs", -23.81700, 133.89999, -255.13, 257.13, -255.03, 257.23),
        new("IDR262", "Perth Airport", -31.93300, 115.96700, -254.99, 257.38, -255.60, 256.77),
        new("IDR272", "Woomera", -31.15700, 136.80299, -254.91, 257.09, -254.66, 257.34),
        new("IDR282", "Grafton", -29.62200, 152.95100, -254.59, 257.39, -255.26, 256.72),
        new("IDR292", "Learmonth", -22.10400, 113.99800, -255.54, 257.49, -255.51, 257.52),
        new("IDR312", "Albany", -34.95000, 117.80000, -255.21, 257.21, -254.87, 257.55),
        new("IDR322", "Esperance", -33.83000, 121.89200, -255.16, 256.66, -254.79, 257.03),
        new("IDR332", "Ceduna", -32.13100, 133.69501, -255.51, 257.48, -255.56, 257.42),
        new("IDR362", "Mornington Island", -16.66600, 139.16701, -255.17, 257.28, -255.13, 257.32),
        new("IDR372", "Hobart", -42.83300, 147.50999, -254.53, 257.32, -254.71, 257.13),
        new("IDR382", "Newdegate", -33.09700, 119.00870, -255.42, 257.04, -255.20, 257.26),
        new("IDR392", "Halls Creek", -18.23100, 127.66300, -255.17, 257.20, -255.14, 257.22),   // BOM corner file malformed; typical square used
        new("IDR402", "Canberra", -35.66280, 149.51080, -255.24, 257.23, -255.51, 256.97),
        new("IDR412", "Willis Island", -16.30000, 149.98300, -255.35, 256.82, -255.18, 257.00),
        new("IDR422", "Katherine", -14.51300, 132.44600, -255.30, 257.04, -255.17, 257.17),
        new("IDR442", "Giles", -25.03000, 128.30000, -255.38, 257.38, -255.33, 257.42),
        new("IDR462", "Sellicks Hill", -35.33000, 138.50000, -255.18, 257.52, -255.38, 257.32),
        new("IDR482", "Kalgoorlie", -30.78500, 121.45200, -255.48, 256.87, -254.91, 257.44),
        new("IDR492", "Yarrawonga", -36.03000, 146.02299, -254.96, 256.69, -254.80, 256.86),
        new("IDR502", "Marburg", -27.60800, 152.53900, -254.86, 257.08, -254.80, 257.13),
        new("IDR522", "North West Tasmania", -41.18100, 145.57899, -255.23, 257.17, -255.42, 256.98),
        new("IDR532", "Moree", -29.50000, 149.85001, -255.48, 257.48, -255.51, 257.46),
        new("IDR552", "Wagga Wagga", -35.16700, 147.46700, -255.17, 257.34, -255.08, 257.43),
        new("IDR562", "Longreach", -23.43000, 144.28999, -255.33, 257.41, -255.14, 257.59),
        new("IDR582", "South Doodlakine", -31.77700, 117.95290, -255.32, 257.07, -255.14, 257.26),
        new("IDR622", "Norfolk Island", -29.04000, 167.94000, -255.09, 257.13, -254.66, 257.56),
        new("IDR632", "Darwin", -12.45700, 130.92500, -254.90, 257.42, -254.78, 257.53),
        new("IDR642", "Adelaide", -34.61700, 138.46899, -255.08, 257.50, -255.49, 257.09),
        new("IDR662", "Brisbane", -27.71810, 153.24001, -255.18, 257.24, -255.43, 257.00),
        new("IDR672", "Warrego", -26.44000, 147.34920, -255.48, 257.49, -255.45, 257.52),
        new("IDR682", "Bairnsdale", -37.88760, 147.57550, -254.69, 257.06, -254.88, 256.87),
        new("IDR692", "Namoi", -31.02400, 150.19150, -254.83, 257.54, -254.82, 257.55),
        new("IDR702", "Perth", -32.39170, 115.86690, -255.49, 257.16, -255.37, 257.27),
        new("IDR712", "Sydney", -33.70080, 151.20950, -255.24, 257.44, -255.39, 257.28),
        new("IDR722", "Emerald", -23.54980, 148.23900, -255.35, 256.89, -255.37, 256.86),
        new("IDR742", "Greenvale", -18.99700, 144.99500, -255.43, 257.44, -255.26, 257.61),
        new("IDR752", "Mount Isa", -20.71120, 139.55499, -254.94, 256.76, -255.00, 256.70),
        new("IDR762", "Hobart Mt Koonya", -43.11200, 147.80600, -255.39, 256.98, -255.38, 256.99),
        new("IDR772", "Arafura", -11.64900, 133.38000, -254.65, 257.14, -254.98, 256.82),
        new("IDR782", "Weipa", -12.67100, 141.92200, -255.30, 257.42, -255.40, 257.32),
        new("IDR792", "Watheroo", -30.36000, 116.29220, -254.84, 256.72, -254.69, 256.88),
        new("IDR932", "Brewarrina", -29.96960, 146.81290, -254.80, 257.51, -254.76, 257.55),
        new("IDR942", "Hillston", -33.55190, 145.52859, -255.05, 257.24, -255.20, 257.09),
        new("IDR952", "Rainbow", -35.99750, 142.01331, -255.09, 256.76, -254.84, 257.01),
        new("IDR962", "Yeoval", -32.74442, 148.70810, -254.95, 257.38, -255.03, 257.29),
        new("IDR972", "Mildura", -34.28710, 141.59821, -254.86, 257.07, -254.69, 257.23),
        new("IDR982", "Taroom", -25.69600, 149.89799, -254.78, 257.17, -254.71, 257.24),
        new("IDR1062", "Townsville", -19.41960, 146.55099, -255.33, 257.39, -255.45, 257.26),
        new("IDR1072", "Richmond", -20.75178, 143.14145, -255.32, 257.31, -255.21, 257.42),
        new("IDR1082", "Toowoomba", -27.27430, 151.99240, -255.11, 257.11, -254.76, 257.46),
        new("IDR1112", "Karratha", -20.99240, 116.87580, -255.22, 257.22, -255.12, 257.31),
        new("IDR1122", "Gove Airport", -12.26898, 136.82043, -255.37, 257.36, -255.32, 257.41),
        new("IDR1142", "Carnarvon", -24.88300, 113.66700, -255.30, 257.09, -255.18, 257.22),
    };
}
